using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using AIMediaWorker.Settings;
using AIMediaWorker.Asr;
using AIMediaWorker.Localization;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Media;
using AIMediaWorker.Network;
using AIMediaWorker.Playback;
using AIMediaWorker.Views;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AIMediaWorker
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private readonly Task<AppSettings> _settingsLoadTask;
        private readonly bool _captureOnly;
        private readonly object _activationGate = new();
        private readonly Queue<string[]> _pendingExternalActivations = new();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App() : this(captureOnly: false)
        {
        }

        private App(bool captureOnly)
        {
            _captureOnly = captureOnly;
            StartupProfiler.Mark("app-constructor");
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var settingsService = SettingsService.CreateDefault();
            // Application.RequestedTheme must be selected during startup. Doing this before
            // any Window XAML is created prevents the default light resources from producing
            // a white first frame when the saved preference is Dark.
            var startupTheme = settingsService.LoadTheme();
            StartupProfiler.Mark("settings-load-start");
            // JSON metadata generation/JIT can complete synchronously for this small file.
            // Force it off the UI thread so App XAML and native DLL loading can overlap it.
            _settingsLoadTask = Task.Run(() => LoadSettingsAsync(settingsService));
            StartupProfiler.Mark("app-xaml-start");
            InitializeComponent();
            ApplyStartupTheme(startupTheme);
            StartupProfiler.Mark("app-xaml-end");
            UnhandledException += (_, eventArgs) => _ = AppLog.WriteAsync("critical", "application", "UNHANDLED_EXCEPTION", eventArgs.Message, eventArgs.Exception);
            if (!_captureOnly)
                Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += OnAppInstanceActivated;
        }

        private void ApplyStartupTheme(AppTheme theme)
        {
            // Leaving RequestedTheme unchanged preserves the Windows app-mode preference.
            if (theme == AppTheme.System) return;
            RequestedTheme = theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }

        private static async Task<AppSettings> LoadSettingsAsync(SettingsService settingsService)
        {
            try { return await settingsService.LoadAsync().ConfigureAwait(false); }
            finally { StartupProfiler.Mark("settings-load-end"); }
        }

        private const string SingleInstanceKey = "AIMediaWorker.SingleInstance";
        private const string CaptureInstanceKey = "AIMediaWorker.CaptureInstance";

        /// <summary>
        /// Custom entry point. Enforces a single running instance: a secondary launch
        /// redirects its activation (for example a file opened from Explorer) to the
        /// already running instance and then exits immediately.
        /// </summary>
        [STAThread]
        public static void Main(string[] args)
        {
            StartupProfiler.Start();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var captureOnly = IsCaptureOnlyLaunch(args);

            Microsoft.Windows.AppLifecycle.AppInstance primaryInstance;
            try
            {
                primaryInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(
                    captureOnly ? CaptureInstanceKey : SingleInstanceKey);
            }
            catch { primaryInstance = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent(); }

            if (!primaryInstance.IsCurrent)
            {
                try
                {
                    // RedirectActivationToAsync must run to completion before this process exits,
                    // and blocking the STA thread is discouraged, so redirect on a worker thread
                    // and wait for it to finish.
                    var activationArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                    using var redirectCompleted = new ManualResetEventSlim(false);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { primaryInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait(); }
                        catch { }
                        finally { redirectCompleted.Set(); }
                    });
                    redirectCompleted.Wait(TimeSpan.FromSeconds(5));
                }
                catch { /* The running instance may not accept redirection; exit anyway. */ }
                return;
            }

            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App(captureOnly);
            });
        }

        private static bool IsCaptureOnlyLaunch(IEnumerable<string> args) =>
            args.Any(value => string.Equals(value, "-capture", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                // File activation payloads can become unavailable after the first asynchronous
                // continuation, so capture the launch source before loading settings.
                var launchSource = GetLaunchSource();
                var settings = await _settingsLoadTask;
                AsrRuntimePaths.SetWorkerDirectory(settings.Asr.WorkerDirectory);
                UiFontService.Apply(settings.General.UiFontFamily);
                StartupProfiler.Mark("localization-apply-start");
                LocalizationService.Apply(settings.General.Language);
                StartupProfiler.Mark("localization-apply-end");
                StartupProfiler.Mark("main-window-create-start");
                if (_captureOnly)
                {
                    var captureWindow = new CaptureRecorderOverlayWindow(initialSettings: settings);
                    captureWindow.Closed += OnCaptureOnlyWindowClosed;
                    _window = captureWindow;
                    _window.Activate();
                    StartupProfiler.Mark("window-activated");
                    return;
                }

                var mainWindow = new MainWindow(launchSource, settings);
                mainWindow.ApplySavedWindowPlacement(settings.Window);
                string[][] pendingActivations;
                lock (_activationGate)
                {
                    _window = mainWindow;
                    pendingActivations = _pendingExternalActivations.ToArray();
                    _pendingExternalActivations.Clear();
                }
                _window.Activate();
                StartupProfiler.Mark("window-activated");
                foreach (var filePaths in pendingActivations)
                    mainWindow.ActivateFromExternalLaunch(filePaths);
                _ = MigrateWebDavCredentialsAsync(settings);
            }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("critical", "startup", "STARTUP_ERROR", exception.Message, exception);
                throw;
            }
        }

        private void OnCaptureOnlyWindowClosed(object sender, WindowEventArgs args)
        {
            if (sender is CaptureRecorderOverlayWindow captureWindow)
                captureWindow.Closed -= OnCaptureOnlyWindowClosed;
            _window = null;
            Exit();
        }

        private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            var filePaths = GetActivatedFilePaths(args);
            MainWindow? window;
            lock (_activationGate)
            {
                window = _window as MainWindow;
                if (window is null)
                {
                    _pendingExternalActivations.Enqueue(filePaths);
                    return;
                }
            }

            window.DispatcherQueue.TryEnqueue(() => window.ActivateFromExternalLaunch(filePaths));
        }

        private static string[] GetActivatedFilePaths(AppActivationArguments activation)
        {
            try
            {
                if (activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs fileActivation)
                    return fileActivation.Files.OfType<StorageFile>().Select(file => file.Path).ToArray();

                // Unpackaged file associations invoke `AIMediaWorker.exe "%1"`. Windows App SDK
                // reports that redirection as a Launch activation, not a File activation.
                if (activation.Kind == ExtendedActivationKind.Launch && activation.Data is ILaunchActivatedEventArgs launchActivation)
                    return ParseLaunchFilePaths(launchActivation.Arguments);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
            }

            return [];
        }

        private static string[] ParseLaunchFilePaths(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return [];

            // Prefix a synthetic argv[0] because CommandLineToArgvW applies special parsing
            // rules to the first token. The remaining tokens are the forwarded shell arguments.
            var argv = CommandLineToArgvW($"AIMediaWorker.exe {arguments}", out var argumentCount);
            if (argv == IntPtr.Zero) return [];
            try
            {
                return Enumerable.Range(1, Math.Max(0, argumentCount - 1))
                    .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)))
                    .Where(value => !string.IsNullOrWhiteSpace(value) && File.Exists(value))
                    .Select(value => System.IO.Path.GetFullPath(value!))
                    .Where(path => MediaFileClassifier.IsPlayable(path) || MediaFileClassifier.IsSubtitle(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            finally { LocalFree(argv); }
        }

        private static async Task MigrateWebDavCredentialsAsync(AppSettings settings)
        {
            try
            {
                var webDavCredentials = new WebDavCredentialStore(new WindowsCredentialService());
                var migratedWebDavCredentials = false;
                foreach (var server in settings.Network.WebDavServers) migratedWebDavCredentials |= webDavCredentials.MigrateLegacy(server);
                if (migratedWebDavCredentials) await SettingsService.CreateDefault().SaveAsync(settings);
            }
            catch (Exception exception) { await AppLog.WriteAsync("warning", "credentials", "WEBDAV_CREDENTIAL_MIGRATION_ERROR", exception.Message, exception); }
        }

        private static string? GetLaunchSource()
        {
            try
            {
                var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activation?.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs fileActivation)
                {
                    var activatedFile = fileActivation.Files.OfType<StorageFile>().FirstOrDefault();
                    if (activatedFile is not null) return activatedFile.Path;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { }

            return Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(value =>
                File.Exists(value) ||
                Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

    }
}
