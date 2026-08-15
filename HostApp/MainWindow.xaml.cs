using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace HostApp;

public sealed partial class MainWindow : Window
{
    private const int WidgetDiscoveryLimit = 100;

    private nint _hostWindowHandle;
    private nint _widgetWindowHandle;
    private Process? _widgetProcess;
    private DispatcherQueueTimer? _discoveryTimer;
    private int _discoveryTicks;
    private bool _started;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();

        AppWindow.Resize(new SizeInt32(760, 620));
        WidgetSlot.Loaded += WidgetSlot_Loaded;
        WidgetSlot.LayoutUpdated += WidgetSlot_LayoutUpdated;
        Closed += MainWindow_Closed;
    }

    private void WidgetSlot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        try
        {
            _hostWindowHandle = WindowNative.GetWindowHandle(this);
            string widgetPath = Path.Combine(AppContext.BaseDirectory, "WidgetApp", "WidgetApp.exe");
            if (!File.Exists(widgetPath))
            {
                throw new FileNotFoundException("The WidgetApp build output was not copied beside HostApp.", widgetPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = widgetPath,
                Arguments = _hostWindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture),
                UseShellExecute = false,
            };

            _widgetProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("WidgetApp.exe did not start.");
            _widgetProcess.EnableRaisingEvents = true;
            _widgetProcess.Exited += WidgetProcess_Exited;

            ConnectionStatus.Text = $"Waiting for WidgetApp.exe (PID {_widgetProcess.Id})…";
            _discoveryTimer = DispatcherQueue.CreateTimer();
            _discoveryTimer.Interval = TimeSpan.FromMilliseconds(50);
            _discoveryTimer.IsRepeating = true;
            _discoveryTimer.Tick += DiscoveryTimer_Tick;
            _discoveryTimer.Start();
        }
        catch (Exception ex)
        {
            ShowFailure($"Widget host failed: {ex.Message}");
        }
    }

    private void DiscoveryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_widgetProcess is null || _widgetProcess.HasExited)
        {
            StopDiscoveryTimer();
            ShowFailure("WidgetApp.exe exited before publishing its window.");
            return;
        }

        if (TryFindWidgetWindow(_widgetProcess.Id, out nint widgetWindowHandle))
        {
            _widgetWindowHandle = widgetWindowHandle;
            StopDiscoveryTimer();
            UpdateWidgetBounds();
            ConnectionProgress.IsActive = false;
            ConnectionStatus.Text = $"Embedded WidgetApp.exe (PID {_widgetProcess.Id}, HWND 0x{_widgetWindowHandle:X}).";
            return;
        }

        _discoveryTicks++;
        if (_discoveryTicks >= WidgetDiscoveryLimit)
        {
            StopDiscoveryTimer();
            ShowFailure("Timed out waiting for WidgetApp.exe to attach its child window.");
        }
    }

    private bool TryFindWidgetWindow(int processId, out nint widgetWindowHandle)
    {
        nint found = 0;

        NativeMethods.EnumChildWindows(
            _hostWindowHandle,
            (candidate, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(candidate, out uint candidateProcessId);
                if (candidateProcessId == (uint)processId && NativeMethods.GetParent(candidate) == _hostWindowHandle)
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

    private void WidgetSlot_LayoutUpdated(object? sender, object e)
    {
        UpdateWidgetBounds();
    }

    private void UpdateWidgetBounds()
    {
        if (_widgetWindowHandle == 0 || WidgetSlot.XamlRoot is null)
        {
            return;
        }

        GeneralTransform transform = WidgetSlot.TransformToVisual(RootGrid);
        Point topLeft = transform.TransformPoint(new Point(0, 0));
        double scale = WidgetSlot.XamlRoot.RasterizationScale;
        int inset = Math.Max(1, (int)Math.Round(scale));

        int x = (int)Math.Round(topLeft.X * scale) + inset;
        int y = (int)Math.Round(topLeft.Y * scale) + inset;
        int width = Math.Max(1, (int)Math.Round(WidgetSlot.ActualWidth * scale) - (inset * 2));
        int height = Math.Max(1, (int)Math.Round(WidgetSlot.ActualHeight * scale) - (inset * 2));

        NativeMethods.SetWindowPos(
            _widgetWindowHandle,
            0,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
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
            StopDiscoveryTimer();
            ShowFailure("WidgetApp.exe disconnected.");
        });
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

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        WidgetSlot.LayoutUpdated -= WidgetSlot_LayoutUpdated;
        StopDiscoveryTimer();

        if (_widgetWindowHandle != 0)
        {
            NativeMethods.PostMessage(_widgetWindowHandle, NativeMethods.WM_CLOSE, 0, 0);
            _widgetWindowHandle = 0;
        }
        else if (_widgetProcess is { HasExited: false })
        {
            _widgetProcess.Kill(entireProcessTree: true);
        }

        if (_widgetProcess is not null)
        {
            _widgetProcess.Exited -= WidgetProcess_Exited;
            _widgetProcess.Dispose();
            _widgetProcess = null;
        }
    }

    private static class NativeMethods
    {
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint WM_CLOSE = 0x0010;

        internal delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            nint parentWindow,
            EnumWindowsProc callback,
            nint parameter);

        [DllImport("user32.dll")]
        internal static extern nint GetParent(nint windowHandle);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);
    }
}
