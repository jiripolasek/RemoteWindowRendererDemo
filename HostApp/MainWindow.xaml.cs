using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace HostApp;

public sealed partial class MainWindow : Window
{
    private const int WidgetDiscoveryLimit = 100;
    private const int PopupWidthInDips = 560;
    private const int PopupHeightInDips = 500;

    private nint _hostWindowHandle;
    private nint _activationParentWindowHandle;
    private nint _widgetWindowHandle;
    private IExtension? _extension;
    private Process? _widgetProcess;
    private RemoteWidgetPopupWindow? _popupWindow;
    private DispatcherQueueTimer? _discoveryTimer;
    private WidgetPresentation _requestedPresentation;
    private WidgetPresentation? _currentPresentation;
    private int _discoveryTicks;
    private bool _activationInProgress;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.Resize(new SizeInt32(780, 680));
        AppWindow.Changed += MainAppWindow_Changed;
        RootGrid.Loaded += RootGrid_Loaded;
        InlineWidgetSlot.LayoutUpdated += InlineWidgetSlot_LayoutUpdated;
        ShowPopupButton.LayoutUpdated += ShowPopupButton_LayoutUpdated;
        Closed += MainWindow_Closed;
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        _hostWindowHandle = WindowNative.GetWindowHandle(this);
    }

    private async void ShowInlineButton_Click(object sender, RoutedEventArgs e)
    {
        await RequestPresentationAsync(WidgetPresentation.Inline);
    }

    private async void ShowPopupButton_Click(object sender, RoutedEventArgs e)
    {
        await RequestPresentationAsync(WidgetPresentation.Popup);
    }

    private async Task RequestPresentationAsync(WidgetPresentation presentation)
    {
        if (_closing || _activationInProgress)
        {
            return;
        }

        _requestedPresentation = presentation;
        if (_widgetWindowHandle != 0)
        {
            try
            {
                await PresentWidgetAsync(presentation);
            }
            catch (Exception ex)
            {
                ShowFailure($"Could not move the widget to the requested host surface: {ex.Message}");
            }

            return;
        }

        _activationInProgress = true;
        SetPresentationButtonsEnabled(false);
        ConnectionProgress.IsActive = true;
        ConnectionStatus.Text = "Preparing the requested host surface…";

        try
        {
            if (_hostWindowHandle == 0)
            {
                _hostWindowHandle = WindowNative.GetWindowHandle(this);
            }

            if (presentation == WidgetPresentation.Popup)
            {
                await ShowPopupShellAsync();
            }

            _activationParentWindowHandle = presentation == WidgetPresentation.Inline
                ? _hostWindowHandle
                : EnsurePopupWindow().ParentWindowHandle;

            ConnectionStatus.Text = "Activating IExtension through packaged COM…";
            RemoteExtensionConnection connection = await Task.Run(
                () => RemoteExtensionActivator.ActivateAndAttach(
                    unchecked((ulong)_activationParentWindowHandle.ToInt64())));

            if (_closing)
            {
                await Task.Run(connection.Extension.Dispose);
                return;
            }

            _extension = connection.Extension;
            _widgetProcess = Process.GetProcessById(connection.ProcessId);
            _widgetProcess.EnableRaisingEvents = true;
            _widgetProcess.Exited += WidgetProcess_Exited;

            ConnectionStatus.Text =
                $"COM extension activated (PID {_widgetProcess.Id}); waiting for its child HWND…";
            _discoveryTicks = 0;
            _discoveryTimer = DispatcherQueue.CreateTimer();
            _discoveryTimer.Interval = TimeSpan.FromMilliseconds(50);
            _discoveryTimer.IsRepeating = true;
            _discoveryTimer.Tick += DiscoveryTimer_Tick;
            _discoveryTimer.Start();
        }
        catch (Exception ex)
        {
            _activationInProgress = false;
            SetPresentationButtonsEnabled(true);
            DisposePopupWindow();
            ShowFailure($"Widget COM activation failed: {ex.Message}");
        }
    }

    private async void DiscoveryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_widgetProcess is null || _widgetProcess.HasExited)
        {
            StopDiscoveryTimer();
            _activationInProgress = false;
            SetPresentationButtonsEnabled(true);
            ShowFailure("The COM extension exited before publishing its window.");
            return;
        }

        if (TryFindWidgetWindow(
            _activationParentWindowHandle,
            _widgetProcess.Id,
            out nint widgetWindowHandle))
        {
            _widgetWindowHandle = widgetWindowHandle;
            StopDiscoveryTimer();
            _activationInProgress = false;
            SetPresentationButtonsEnabled(true);
            ConnectionProgress.IsActive = false;
            try
            {
                await PresentWidgetAsync(_requestedPresentation);
            }
            catch (Exception ex)
            {
                ShowFailure($"Could not present the extension window: {ex.Message}");
            }

            return;
        }

        _discoveryTicks++;
        if (_discoveryTicks >= WidgetDiscoveryLimit)
        {
            StopDiscoveryTimer();
            _activationInProgress = false;
            SetPresentationButtonsEnabled(true);
            ShowFailure("Timed out waiting for the extension to attach to the requested host surface.");
        }
    }

    private async Task PresentWidgetAsync(WidgetPresentation presentation)
    {
        if (_widgetWindowHandle == 0)
        {
            return;
        }

        if (presentation == WidgetPresentation.Inline)
        {
            _popupWindow?.DetachWidgetWindow();
            _popupWindow?.Hide();
            ReparentWidget(_hostWindowHandle);
            _currentPresentation = WidgetPresentation.Inline;
            InlinePlaceholder.Visibility = Visibility.Collapsed;
            PositionInlineWidget();
            ConnectionStatus.Text =
                $"WidgetApp PID {_widgetProcess?.Id}, HWND 0x{_widgetWindowHandle:X}, is hosted inline.";
            return;
        }

        RemoteWidgetPopupWindow popupWindow = EnsurePopupWindow();
        await ShowPopupShellAsync();
        ReparentWidget(popupWindow.ParentWindowHandle);
        popupWindow.AttachWidgetWindow(_widgetWindowHandle);
        _currentPresentation = WidgetPresentation.Popup;
        InlinePlaceholder.Visibility = Visibility.Visible;
        ConnectionStatus.Text =
            $"HostApp popup 0x{popupWindow.ParentWindowHandle:X} owns the chrome; " +
            $"WidgetApp PID {_widgetProcess?.Id}, HWND 0x{_widgetWindowHandle:X}, owns the content.";
    }

    private void ReparentWidget(nint newParentWindowHandle)
    {
        if (NativeMethods.GetParent(_widgetWindowHandle) == newParentWindowHandle)
        {
            return;
        }

        NativeMethods.SetLastError(0);
        nint previousParent = NativeMethods.SetParent(_widgetWindowHandle, newParentWindowHandle);
        int error = Marshal.GetLastWin32Error();
        if (previousParent == 0 && error != 0)
        {
            throw new Win32Exception(error, "Could not move the extension window to the requested host surface.");
        }
    }

    private static bool TryFindWidgetWindow(
        nint parentWindowHandle,
        int processId,
        out nint widgetWindowHandle)
    {
        nint found = 0;

        NativeMethods.EnumChildWindows(
            parentWindowHandle,
            (candidate, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(candidate, out uint candidateProcessId);
                if (candidateProcessId == (uint)processId &&
                    NativeMethods.GetParent(candidate) == parentWindowHandle)
                {
                    found = candidate;
                    return false;
                }

                return true;
            },
            0);

        widgetWindowHandle = found;
        return found != 0;
    }

    private void InlineWidgetSlot_LayoutUpdated(object? sender, object e)
    {
        PositionInlineWidget();
    }

    private void ShowPopupButton_LayoutUpdated(object? sender, object e)
    {
        RepositionPopupIfVisible();
    }

    private void MainAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            RepositionPopupIfVisible();
        }
    }

    private void PositionInlineWidget()
    {
        if (_currentPresentation != WidgetPresentation.Inline ||
            _widgetWindowHandle == 0 ||
            InlineWidgetSlot.XamlRoot is null)
        {
            return;
        }

        GeneralTransform transform = InlineWidgetSlot.TransformToVisual(RootGrid);
        Point topLeft = transform.TransformPoint(new Point(0, 0));
        double scale = InlineWidgetSlot.XamlRoot.RasterizationScale;
        int inset = Math.Max(1, (int)Math.Round(scale));

        int x = (int)Math.Round(topLeft.X * scale) + inset;
        int y = (int)Math.Round(topLeft.Y * scale) + inset;
        int width = Math.Max(1, (int)Math.Round(InlineWidgetSlot.ActualWidth * scale) - (inset * 2));
        int height = Math.Max(1, (int)Math.Round(InlineWidgetSlot.ActualHeight * scale) - (inset * 2));

        NativeMethods.SetWindowPos(
            _widgetWindowHandle,
            NativeMethods.HWND_TOP,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_ASYNCWINDOWPOS |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW);
    }

    private async Task ShowPopupShellAsync()
    {
        RemoteWidgetPopupWindow popupWindow = EnsurePopupWindow();
        (RectInt32 anchorBounds, SizeInt32 desiredSize) = GetPopupPlacement();

        await popupWindow.ShowAtAsync(anchorBounds, desiredSize);
    }

    private void RepositionPopupIfVisible()
    {
        if (_closing || _popupWindow?.IsVisible != true || ShowPopupButton.XamlRoot is null)
        {
            return;
        }

        RectInt32 anchorBounds = GetElementScreenBounds(ShowPopupButton);
        _popupWindow.Reposition(anchorBounds);
    }

    private (RectInt32 AnchorBounds, SizeInt32 DesiredSize) GetPopupPlacement()
    {
        RectInt32 anchorBounds = GetElementScreenBounds(ShowPopupButton);
        double scale = ShowPopupButton.XamlRoot?.RasterizationScale ?? 1;
        var desiredSize = new SizeInt32(
            (int)Math.Round(PopupWidthInDips * scale),
            (int)Math.Round(PopupHeightInDips * scale));

        return (anchorBounds, desiredSize);
    }

    private RectInt32 GetElementScreenBounds(FrameworkElement element)
    {
        GeneralTransform transform = element.TransformToVisual(null);
        Point topLeft = transform.TransformPoint(new Point(0, 0));
        double scale = element.XamlRoot?.RasterizationScale ?? 1;

        var screenPoint = new NativeMethods.POINT
        {
            X = (int)Math.Round(topLeft.X * scale),
            Y = (int)Math.Round(topLeft.Y * scale),
        };

        if (!NativeMethods.ClientToScreen(_hostWindowHandle, ref screenPoint))
        {
            throw new InvalidOperationException("Could not locate the popup anchor on screen.");
        }

        return new RectInt32(
            screenPoint.X,
            screenPoint.Y,
            Math.Max(1, (int)Math.Round(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(element.ActualHeight * scale)));
    }

    private RemoteWidgetPopupWindow EnsurePopupWindow()
    {
        if (_popupWindow is not null)
        {
            return _popupWindow;
        }

        _popupWindow = new RemoteWidgetPopupWindow(_hostWindowHandle);
        _popupWindow.Dismissed += PopupWindow_Dismissed;
        return _popupWindow;
    }

    private void PopupWindow_Dismissed(object? sender, EventArgs e)
    {
        if (!_closing)
        {
            ConnectionStatus.Text =
                $"The host popup is hidden; WidgetApp PID {_widgetProcess?.Id} remains connected.";
        }
    }

    private void WidgetProcess_Exited(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _widgetWindowHandle = 0;
            _currentPresentation = null;
            _activationInProgress = false;
            StopDiscoveryTimer();
            _popupWindow?.Hide();
            InlinePlaceholder.Visibility = Visibility.Visible;
            SetPresentationButtonsEnabled(false);
            ShowFailure("The COM extension disconnected from its host surface.");
        });
    }

    private void SetPresentationButtonsEnabled(bool isEnabled)
    {
        ShowInlineButton.IsEnabled = isEnabled;
        ShowPopupButton.IsEnabled = isEnabled;
    }

    private void ShowFailure(string message)
    {
        ConnectionProgress.IsActive = false;
        ConnectionStatus.Text = message;
    }

    private void StopDiscoveryTimer()
    {
        if (_discoveryTimer is null)
        {
            return;
        }

        _discoveryTimer.Stop();
        _discoveryTimer.Tick -= DiscoveryTimer_Tick;
        _discoveryTimer = null;
    }

    private void DisposePopupWindow()
    {
        if (_popupWindow is null)
        {
            return;
        }

        _popupWindow.Dismissed -= PopupWindow_Dismissed;
        _popupWindow.Dispose();
        _popupWindow = null;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        AppWindow.Changed -= MainAppWindow_Changed;
        InlineWidgetSlot.LayoutUpdated -= InlineWidgetSlot_LayoutUpdated;
        ShowPopupButton.LayoutUpdated -= ShowPopupButton_LayoutUpdated;
        StopDiscoveryTimer();

        if (_widgetWindowHandle != 0)
        {
            NativeMethods.PostMessage(_widgetWindowHandle, NativeMethods.WM_CLOSE, 0, 0);
            _widgetWindowHandle = 0;
        }

        DisposePopupWindow();

        IExtension? extension = _extension;
        _extension = null;
        if (extension is not null)
        {
            _ = Task.Run(extension.Dispose);
        }

        if (_widgetProcess is not null)
        {
            _widgetProcess.Exited -= WidgetProcess_Exited;
            _widgetProcess.Dispose();
            _widgetProcess = null;
        }
    }

    private enum WidgetPresentation
    {
        Inline,
        Popup,
    }

    private static partial class NativeMethods
    {
        internal static readonly nint HWND_TOP = 0;

        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint SWP_ASYNCWINDOWPOS = 0x4000;
        internal const uint WM_CLOSE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        internal delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

        [LibraryImport("kernel32.dll")]
        internal static partial void SetLastError(uint errorCode);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EnumChildWindows(
            nint parentWindow,
            EnumWindowsProc callback,
            nint parameter);

        [LibraryImport("user32.dll")]
        internal static partial nint GetParent(nint windowHandle);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint SetParent(nint childWindow, nint newParentWindow);

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClientToScreen(nint windowHandle, ref POINT point);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);
    }
}
