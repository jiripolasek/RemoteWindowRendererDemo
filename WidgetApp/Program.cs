using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace WidgetApp;

public static class Program
{
    private const string RegisterProcessAsComServer = "-RegisterProcessAsComServer";

    [MTAThread]
    public static void Main(string[] args)
    {
        if (!args.Contains(RegisterProcessAsComServer, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var serverExit = new ManualResetEvent(initialState: false);
        using var widgetController = new RemoteWidgetController(() => serverExit.Set());
        var extension = new RemoteWidgetExtension(widgetController, () => serverExit.Set());
        var server = new ComServer();

        server.RegisterClass<RemoteWidgetExtension, IExtension>(() => extension);
        server.Start();

        serverExit.WaitOne();
        server.Stop();
        server.UnsafeDispose();
    }
}
