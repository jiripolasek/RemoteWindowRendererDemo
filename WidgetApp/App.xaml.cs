using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WidgetApp;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLineArgs = Environment.GetCommandLineArgs();
        if (commandLineArgs.Length < 2 ||
            !long.TryParse(commandLineArgs[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parentValue) ||
            parentValue == 0)
        {
            throw new InvalidOperationException("WidgetApp.exe must be launched by HostApp.exe with the host HWND.");
        }

        nint parentWindowHandle = new(parentValue);
        _window = new Window
        {
            Title = "RemoveWindowRendererDemo.Widget",
            Content = new WidgetPage(),
        };
        _window.Closed += WidgetWindow_Closed;

        nint widgetWindowHandle = WindowNative.GetWindowHandle(_window);
        AttachToHost(widgetWindowHandle, parentWindowHandle);
        _window.Activate();
    }

    private static void AttachToHost(nint widgetWindowHandle, nint parentWindowHandle)
    {
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
            _window = null;
        }

        Exit();
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
