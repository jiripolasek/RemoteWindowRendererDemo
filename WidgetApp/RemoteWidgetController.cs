using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WidgetApp;

public sealed class RemoteWidgetController : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _uiThreadExited;

    private Thread? _uiThread;
    private DispatcherQueue? _dispatcherQueue;
    private App? _app;
    private Window? _window;
    private nint _parentWindowHandle;
    private bool _disposed;

    public RemoteWidgetController(Action uiThreadExited)
    {
        _uiThreadExited = uiThreadExited;
    }

    public void Attach(nint parentWindowHandle)
    {
        if (parentWindowHandle == 0)
        {
            throw new ArgumentException("A non-zero host HWND is required.", nameof(parentWindowHandle));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _parentWindowHandle = parentWindowHandle;

            if (_uiThread is null)
            {
                _uiThread = new Thread(RunUiThread)
                {
                    IsBackground = true,
                    Name = "Remote widget WinUI STA",
                };
                _uiThread.SetApartmentState(ApartmentState.STA);
                _uiThread.Start();
                return;
            }

            _dispatcherQueue?.TryEnqueue(AttachWindowToCurrentParent);
        }
    }

    public void Close()
    {
        DispatcherQueue? dispatcherQueue;
        lock (_gate)
        {
            dispatcherQueue = _dispatcherQueue;
        }

        dispatcherQueue?.TryEnqueue(() => _window?.Close());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Close();
    }

    private void RunUiThread()
    {
        try
        {
            Application.Start(_ =>
            {
                DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(dispatcherQueue));

                var app = new App();
                var window = new Window
                {
                    Title = "RemoveWindowRendererDemo.Widget",
                    Content = new WidgetPage(),
                };
                window.Closed += WidgetWindow_Closed;

                lock (_gate)
                {
                    _dispatcherQueue = dispatcherQueue;
                    _app = app;
                    _window = window;
                }

                AttachWindowToCurrentParent();
                window.Activate();
            });
        }
        finally
        {
            lock (_gate)
            {
                _window = null;
                _app = null;
                _dispatcherQueue = null;
                _uiThread = null;
            }

            _uiThreadExited();
        }
    }

    private void AttachWindowToCurrentParent()
    {
        Window? window;
        nint parentWindowHandle;
        lock (_gate)
        {
            window = _window;
            parentWindowHandle = _parentWindowHandle;
        }

        if (window is null || parentWindowHandle == 0)
        {
            return;
        }

        nint widgetWindowHandle = WindowNative.GetWindowHandle(window);
        int style = NativeMethods.GetWindowLong(widgetWindowHandle, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_OVERLAPPEDWINDOW | NativeMethods.WS_POPUP);
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS;

        NativeMethods.SetLastError(0);
        int previousStyle = NativeMethods.SetWindowLong(widgetWindowHandle, NativeMethods.GWL_STYLE, style);
        int styleError = Marshal.GetLastWin32Error();
        if (previousStyle == 0 && styleError != 0)
        {
            throw new Win32Exception(styleError, "Could not convert the widget window to a child window.");
        }

        NativeMethods.SetLastError(0);
        nint previousParent = NativeMethods.SetParent(widgetWindowHandle, parentWindowHandle);
        int parentError = Marshal.GetLastWin32Error();
        if (previousParent == 0 && parentError != 0)
        {
            throw new Win32Exception(parentError, "Could not attach the widget window to HostApp.exe.");
        }
    }

    private void WidgetWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= WidgetWindow_Closed;
        }

        _app?.Exit();
    }

    private static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const int WS_CHILD = 0x40000000;
        internal const int WS_CLIPCHILDREN = 0x02000000;
        internal const int WS_CLIPSIBLINGS = 0x04000000;
        internal const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        internal const int WS_POPUP = unchecked((int)0x80000000);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        internal static extern int GetWindowLong(nint windowHandle, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        internal static extern int SetWindowLong(nint windowHandle, int index, int newValue);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint childWindow, nint newParentWindow);

        [DllImport("kernel32.dll")]
        internal static extern void SetLastError(uint errorCode);
    }
}
