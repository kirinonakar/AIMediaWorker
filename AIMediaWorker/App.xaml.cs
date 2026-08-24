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
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using AIMediaWorker.Settings;
using AIMediaWorker.Localization;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Network;
using AIMediaWorker.Playback;

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

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            StartupProfiler.Mark("app-constructor");
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _ = MpvPlaybackEngine.PreloadAsync();
            var settingsService = SettingsService.CreateDefault();
            StartupProfiler.Mark("settings-load-start");
            // JSON metadata generation/JIT can complete synchronously for this small file.
            // Force it off the UI thread so App XAML and native DLL loading can overlap it.
            _settingsLoadTask = Task.Run(() => LoadSettingsAsync(settingsService));
            StartupProfiler.Mark("app-xaml-start");
            InitializeComponent();
            StartupProfiler.Mark("app-xaml-end");
            UnhandledException += (_, eventArgs) => _ = AppLog.WriteAsync("critical", "application", "UNHANDLED_EXCEPTION", eventArgs.Message, eventArgs.Exception);
        }

        private static async Task<AppSettings> LoadSettingsAsync(SettingsService settingsService)
        {
            try { return await settingsService.LoadAsync().ConfigureAwait(false); }
            finally { StartupProfiler.Mark("settings-load-end"); }
        }

        private const string SingleInstanceKey = "AIMediaWorker.SingleInstance";

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

            Microsoft.Windows.AppLifecycle.AppInstance primaryInstance;
            try { primaryInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(SingleInstanceKey); }
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
                new App();
            });
        }

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
                UiFontService.Apply(settings.General.UiFontFamily);
                StartupProfiler.Mark("localization-apply-start");
                LocalizationService.Apply(settings.General.Language);
                StartupProfiler.Mark("localization-apply-end");
                StartupProfiler.Mark("main-window-create-start");
                var mainWindow = new MainWindow(launchSource, settings);
                mainWindow.ApplySavedWindowPlacement(settings.Window);
                _window = mainWindow;
                _window.Activate();
                StartupProfiler.Mark("window-activated");
                Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += (_, args) =>
                {
                    if (_window is not MainWindow window) return;
                    var filePaths = args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs fileActivation
                        ? fileActivation.Files.OfType<StorageFile>().Select(file => file.Path).ToArray()
                        : Array.Empty<string>();
                    window.DispatcherQueue.TryEnqueue(() => window.ActivateFromExternalLaunch(filePaths));
                };
                _ = MigrateWebDavCredentialsAsync(settings);
            }
            catch (Exception exception)
            {
                await AppLog.WriteAsync("critical", "startup", "STARTUP_ERROR", exception.Message, exception);
                throw;
            }
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

    }
}
