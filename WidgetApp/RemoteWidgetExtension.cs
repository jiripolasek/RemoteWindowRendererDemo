using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace WidgetApp;

[Guid(ClassId)]
public sealed partial class RemoteWidgetExtension : IExtension
{
    public const string ClassId = "95DDD426-45EF-48D2-85B1-45DB00CD6FB7";

    private readonly RemoteWidgetBridge _bridge;
    private readonly Action _requestServerExit;
    private int _disposed;

    public RemoteWidgetExtension(RemoteWidgetController controller, Action requestServerExit)
    {
        _bridge = new RemoteWidgetBridge(controller);
        _requestServerExit = requestServerExit;
    }

    public object? GetProvider(ProviderType providerType) =>
        providerType == ProviderType.Commands ? _bridge : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _bridge.Dispose();
        _requestServerExit();
    }
}

public sealed partial class RemoteWidgetBridge : CommandParameterRun, IDisposable
{
    private readonly RemoteWidgetController _controller;

    public RemoteWidgetBridge(RemoteWidgetController controller)
    {
        _controller = controller;
        Required = false;
        PlaceholderText = "Host HWND";
    }

    public override ICommand? GetSelectValueCommand(ulong hostHwnd)
    {
        _controller.Attach(new nint(unchecked((long)hostHwnd)));

        return new NoOpCommand
        {
            Id = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            Name = "Remote widget attached",
        };
    }

    public void Dispose() => _controller.Close();
}
