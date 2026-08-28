using System.Runtime.InteropServices;
using AIMediaWorker.Localization;
using AIMediaWorker.Network;
using AIMediaWorker.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace AIMediaWorker.Views;

public sealed partial class WebDavServerWindow : Window
{
    private const int PreferredWidth = 450;
    private const int PreferredHeight = 550;
    private readonly Window _owner;
    private readonly nint _ownerHandle;
    private readonly nint _selfHandle;
    private readonly AppWindow? _appWindow;
    private TaskCompletionSource<WebDavServerInput?>? _completion;

    private WebDavServerWindow(Window owner, AppTheme theme)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        InitializeComponent();
        AddEscapeShortcut();
        Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyLocalizedText();
        WindowOwner.Attach(this, owner);
        _ownerHandle = WindowNative.GetWindowHandle(owner);
        _selfHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_selfHandle));
        ConfigureWindow();
        Root.ActualThemeChanged += OnRootActualThemeChanged;
        Closed += OnClosed;
    }

    private void AddEscapeShortcut()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape, ScopeOwner = Root };
        escape.Invoked += (_, args) =>
        {
            args.Handled = true;
            Close();
        };
        Root.KeyboardAccelerators.Add(escape);
    }

    internal static Task<WebDavServerInput?> ShowAsync(Window owner, AppTheme theme) =>
        new WebDavServerWindow(owner, theme).ShowCoreAsync();

    private async Task<WebDavServerInput?> ShowCoreAsync()
    {
        if (_completion is not null) throw new InvalidOperationException("The WebDAV server window is already open.");
        _completion = new TaskCompletionSource<WebDavServerInput?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnableWindow(_ownerHandle, false);
        try
        {
            Activate();
            return await _completion.Task;
        }
        finally
        {
            EnableWindow(_ownerHandle, true);
            _owner.Activate();
        }
    }

    private void ConfigureWindow()
    {
        if (_appWindow is null) return;
        var ownerWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_ownerHandle);
        var ownerAppWindow = AppWindow.GetFromWindowId(ownerWindowId);
        var workArea = DisplayArea.GetFromWindowId(ownerWindowId, DisplayAreaFallback.Nearest).WorkArea;
        var scale = Math.Max(96u, GetDpiForWindow(_selfHandle)) / 96d;
        var preferredWidth = (int)Math.Ceiling(PreferredWidth * scale);
        var preferredHeight = (int)Math.Ceiling(PreferredHeight * scale);
        var width = Math.Min(preferredWidth, Math.Max(1, workArea.Width - 32));
        var height = Math.Min(preferredHeight, Math.Max(1, workArea.Height - 32));
        var ownerCenterX = ownerAppWindow.Position.X + ownerAppWindow.Size.Width / 2;
        var ownerCenterY = ownerAppWindow.Position.Y + ownerAppWindow.Size.Height / 2;
        var x = Math.Clamp(ownerCenterX - width / 2, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(ownerCenterY - height / 2, workArea.Y, workArea.Y + workArea.Height - height);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Match SettingsWindow: retain the window border while omitting the
            // system title bar. The form supplies its own heading and close action.
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        ApplyTitleBarTheme(Root.ActualTheme);
    }

    private void ApplyLocalizedText()
    {
        Title = L("AddWebDavServerTitle");
        HeadingText.Text = Title;
        NameBox.Header = L("NameHeader");
        AddressBox.Header = L("AddressHeader");
        PortBox.Header = L("PortHeader");
        UsernameBox.Header = L("UsernameHeader");
        PasswordBox.Header = L("PasswordHeader");
        SaveButton.Content = L("SaveButtonText");
        CancelButton.Content = L("CancelButtonText");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!WebDavConnectionCredential.TryParseHttpsAddress(AddressBox.Text, out var address) ||
            double.IsNaN(PortBox.Value) || PortBox.Value % 1 != 0 || PortBox.Value is < 1 or > 65535)
        {
            ValidationBar.Message = L("InvalidWebDavHttpsAddressMessage");
            ValidationBar.IsOpen = true;
            return;
        }

        _completion?.TrySetResult(new WebDavServerInput(
            NameBox.Text.Trim(),
            address,
            (int)PortBox.Value,
            UsernameBox.Text.Trim(),
            PasswordBox.Password));
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Root.ActualThemeChanged -= OnRootActualThemeChanged;
        Closed -= OnClosed;
        _completion?.TrySetResult(null);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTitleBarTheme(sender.ActualTheme);

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_appWindow?.TitleBar is not { } titleBar) return;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 24, 24, 24);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 58, 58, 58) : Windows.UI.Color.FromArgb(255, 224, 224, 224);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 72, 72, 72) : Windows.UI.Color.FromArgb(255, 208, 208, 208);
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private static string L(string key) => LocalizationService.Get(key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}

internal sealed record WebDavServerInput(string Name, Uri Address, int Port, string Username, string Password);
