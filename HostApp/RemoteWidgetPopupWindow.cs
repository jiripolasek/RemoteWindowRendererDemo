using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace HostApp;

internal sealed partial class RemoteWidgetPopupWindow : Window, IDisposable
{
    private const int PopupInsetInDips = 8;
    private const int PopupChromeHeightInDips = 52;

    private readonly nint _windowHandle;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly Grid _rootGrid;
    private readonly Button _closeButton;
    private readonly TaskCompletionSource _contentLoaded = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private RectInt32 _anchorBounds;
    private nint _widgetWindowHandle;
    private bool _hasAnchor;
    private bool _hasShown;
    private bool _isApplyingPlacement;
    private bool _isVisible;
    private bool _isDisposing;

    internal RemoteWidgetPopupWindow(nint ownerWindowHandle)
    {
        Title = "Remote extension popup";
        _rootGrid = new Grid();
        _rootGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(PopupChromeHeightInDips),
        });
        _rootGrid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });

        var header = new Grid
        {
            Padding = new Thickness(16, 8, 8, 4),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });

        var title = new TextBlock
        {
            Text = "Extension content",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(title);

        _closeButton = new Button
        {
            Content = "Close",
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_closeButton, "Close extension popup");
        _closeButton.Click += CloseButton_Click;
        Grid.SetColumn(_closeButton, 1);
        header.Children.Add(_closeButton);

        Grid.SetRow(header, 0);
        _rootGrid.Children.Add(header);
        _rootGrid.Loaded += RootGrid_Loaded;
        Content = new FlyoutPresenter
        {
            Content = _rootGrid,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            MinWidth = 0,
            MinHeight = 0,
            MaxWidth = double.PositiveInfinity,
            MaxHeight = double.PositiveInfinity,
        };
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _windowHandle = WindowNative.GetWindowHandle(this);
        SetOwner(ownerWindowHandle);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        ApplyFlyoutWindowChrome();

        Activated += PopupWindow_Activated;
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
    }

    internal event EventHandler? Dismissed;

    internal nint ParentWindowHandle => _windowHandle;

    internal bool IsVisible => _isVisible;

    internal void AttachWidgetWindow(nint widgetWindowHandle)
    {
        if (widgetWindowHandle == 0)
        {
            throw new ArgumentException("A non-zero widget HWND is required.", nameof(widgetWindowHandle));
        }

        if (NativeMethods.GetParent(widgetWindowHandle) != _windowHandle)
        {
            throw new InvalidOperationException("The extension window did not attach to the host-owned popup.");
        }

        _widgetWindowHandle = widgetWindowHandle;
        ResizeAndRaiseWidget();
    }

    internal void DetachWidgetWindow()
    {
        _widgetWindowHandle = 0;
    }

    internal async Task ShowAtAsync(RectInt32 anchorBounds, SizeInt32 desiredSize)
    {
        SizeInt32 popupSize = _hasShown ? AppWindow.Size : desiredSize;
        MoveToAnchor(anchorBounds, popupSize);
        _hasShown = true;
        Activate();
        _isVisible = true;
        NativeMethods.SetForegroundWindow(_windowHandle);

        // A WinUI Window creates its composition child asynchronously on first
        // activation. Wait until that host-owned surface has loaded and yielded
        // a frame before inserting the out-of-process HWND above it.
        await _contentLoaded.Task;
        await Task.Delay(50);
        ResizeAndRaiseWidget();
    }

    internal void Reposition(RectInt32 anchorBounds)
    {
        if (_isVisible)
        {
            MoveToAnchor(anchorBounds, AppWindow.Size);
        }
    }

    private void MoveToAnchor(RectInt32 anchorBounds, SizeInt32 desiredSize)
    {
        _anchorBounds = anchorBounds;
        _hasAnchor = true;

        var anchorPoint = new PointInt32(anchorBounds.X, anchorBounds.Y + anchorBounds.Height);
        DisplayArea displayArea = DisplayArea.GetFromPoint(anchorPoint, DisplayAreaFallback.Nearest);
        RectInt32 workArea = displayArea.WorkArea;

        int width = Math.Min(desiredSize.Width, workArea.Width);
        int height = Math.Min(desiredSize.Height, workArea.Height);
        int gap = Math.Max(4, (int)Math.Round(4 * GetScale()));

        int maximumX = workArea.X + workArea.Width - width;
        int x = Math.Clamp(anchorBounds.X, workArea.X, maximumX);

        int below = anchorBounds.Y + anchorBounds.Height + gap;
        int above = anchorBounds.Y - height - gap;
        int y = below + height <= workArea.Y + workArea.Height ? below : above;
        y = Math.Clamp(y, workArea.Y, workArea.Y + workArea.Height - height);

        PointInt32 currentPosition = AppWindow.Position;
        SizeInt32 currentSize = AppWindow.Size;
        if (currentPosition.X != x ||
            currentPosition.Y != y ||
            currentSize.Width != width ||
            currentSize.Height != height)
        {
            _isApplyingPlacement = true;
            try
            {
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            finally
            {
                _isApplyingPlacement = false;
            }
        }
    }

    internal void Hide()
    {
        if (!_isVisible)
        {
            return;
        }

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_HIDE);
        _isVisible = false;
    }

    public void Dispose()
    {
        if (_isDisposing)
        {
            return;
        }

        _isDisposing = true;
        _isVisible = false;
        _nonClientPointerSource.ClearAllRegionRects();
        Activated -= PopupWindow_Activated;
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        _rootGrid.Loaded -= RootGrid_Loaded;
        _closeButton.Click -= CloseButton_Click;
        _widgetWindowHandle = 0;
        Close();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            UpdateResizeRegions();
            RemoveClassicFrameStyle();
            ResizeAndRaiseWidget();

            if (_isVisible && _hasAnchor && !_isApplyingPlacement)
            {
                MoveToAnchor(_anchorBounds, AppWindow.Size);
            }
        }
    }

    private void PopupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        ApplyFlyoutWindowChrome();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _contentLoaded.TrySetResult();
        ResizeAndRaiseWidget();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isDisposing)
        {
            return;
        }

        args.Cancel = true;
        Dismiss();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Dismiss()
    {
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeAndRaiseWidget()
    {
        if (_widgetWindowHandle == 0 ||
            !NativeMethods.GetClientRect(_windowHandle, out NativeMethods.RECT popupBounds))
        {
            return;
        }

        double scale = GetScale();
        int inset = Math.Max(1, (int)Math.Round(PopupInsetInDips * scale));
        int contentTop = Math.Max(inset, (int)Math.Round(PopupChromeHeightInDips * scale));
        int width = Math.Max(1, popupBounds.Width - (inset * 2));
        int height = Math.Max(1, popupBounds.Height - contentTop - inset);

        // The remote HWND must remain above the popup Window's own XAML composition child.
        NativeMethods.SetWindowPos(
            _widgetWindowHandle,
            NativeMethods.HWND_TOP,
            inset,
            contentTop,
            width,
            height,
            NativeMethods.SWP_ASYNCWINDOWPOS |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_SHOWWINDOW);
    }

    private double GetScale() => NativeMethods.GetDpiForWindow(_windowHandle) / 96.0;

    private void ApplyFlyoutWindowChrome()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            UpdateResizeRegions();
            RemoveClassicFrameStyle();
            return;
        }

        uint noSystemBorder = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _windowHandle,
            NativeMethods.DWMWA_BORDER_COLOR,
            ref noSystemBorder,
            sizeof(uint));

        uint roundedCorners = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _windowHandle,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref roundedCorners,
            sizeof(uint));

        UpdateResizeRegions();
        RemoveClassicFrameStyle();
    }

    private void UpdateResizeRegions()
    {
        int border = Math.Max(4, (int)Math.Round(6 * GetScale()));
        int width = Math.Max(1, AppWindow.Size.Width);
        int height = Math.Max(1, AppWindow.Size.Height);

        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.TopBorder,
            [new RectInt32(0, 0, width, Math.Min(border, height))]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.BottomBorder,
            [new RectInt32(0, Math.Max(0, height - border), width, Math.Min(border, height))]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.LeftBorder,
            [new RectInt32(0, 0, Math.Min(border, width), height)]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.RightBorder,
            [new RectInt32(Math.Max(0, width - border), 0, Math.Min(border, width), height)]);
    }

    private void RemoveClassicFrameStyle()
    {
        nint currentStyle = IntPtr.Size == 8
            ? NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE)
            : new nint(NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GWL_STYLE));
        nint flyoutStyle = new(
            currentStyle.ToInt64() &
            ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME));

        if (flyoutStyle == currentStyle)
        {
            return;
        }

        if (IntPtr.Size == 8)
        {
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE, flyoutStyle);
        }
        else
        {
            NativeMethods.SetWindowLong(
                _windowHandle,
                NativeMethods.GWL_STYLE,
                flyoutStyle.ToInt32());
        }

        NativeMethods.SetWindowPos(
            _windowHandle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE);
    }

    private void SetOwner(nint ownerWindowHandle)
    {
        NativeMethods.SetLastError(0);
        nint previousOwner = IntPtr.Size == 8
            ? NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWLP_HWNDPARENT, ownerWindowHandle)
            : new nint(NativeMethods.SetWindowLong(
                _windowHandle,
                NativeMethods.GWLP_HWNDPARENT,
                ownerWindowHandle.ToInt32()));

        int error = Marshal.GetLastWin32Error();
        if (previousOwner == 0 && error != 0)
        {
            throw new Win32Exception(error, "Could not assign the host window as the popup owner.");
        }
    }

    private static partial class NativeMethods
    {
        internal static readonly nint HWND_TOP = 0;

        internal const int GWLP_HWNDPARENT = -8;
        internal const int GWL_STYLE = -16;
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        internal const int DWMWA_BORDER_COLOR = 34;
        internal const long WS_CAPTION = 0x00C00000;
        internal const long WS_THICKFRAME = 0x00040000;
        internal const uint DWMWCP_ROUND = 2;
        internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
        internal const int SW_HIDE = 0;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint SWP_ASYNCWINDOWPOS = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

            internal readonly int Width => Right - Left;

            internal readonly int Height => Bottom - Top;
        }

        [LibraryImport("kernel32.dll")]
        internal static partial void SetLastError(uint errorCode);

        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmSetWindowAttribute(
            nint windowHandle,
            int attribute,
            ref uint attributeValue,
            int attributeSize);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static partial nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static partial nint GetWindowLongPtr(nint windowHandle, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        internal static partial int SetWindowLong(nint windowHandle, int index, int newValue);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        internal static partial int GetWindowLong(nint windowHandle, int index);

        [LibraryImport("user32.dll")]
        internal static partial nint GetParent(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetClientRect(nint windowHandle, out RECT bounds);

        [LibraryImport("user32.dll")]
        internal static partial uint GetDpiForWindow(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(nint windowHandle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(nint windowHandle, int command);

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
    }
}
