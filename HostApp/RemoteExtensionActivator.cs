using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using WinRT;

namespace HostApp;

internal static partial class RemoteExtensionActivator
{
    internal const string ExtensionClassId = "95DDD426-45EF-48D2-85B1-45DB00CD6FB7";

    internal static RemoteExtensionConnection ActivateAndAttach(ulong hostHwnd)
    {
        Guid classId = Guid.Parse(ExtensionClassId);
        Guid interfaceId = typeof(IExtension).GUID;
        nint extensionPointer = 0;
        IExtension? extension = null;

        try
        {
            int result = NativeMethods.CoCreateInstance(
                in classId,
                0,
                NativeMethods.CLSCTX_LOCAL_SERVER,
                in interfaceId,
                out extensionPointer);
            Marshal.ThrowExceptionForHR(result);

            extension = MarshalInterface<IExtension>.FromAbi(extensionPointer);
            object? provider = extension.GetProvider(ProviderType.Commands);
            if (provider is not ICommandParameterRun bridge)
            {
                throw new InvalidOperationException(
                    "The extension did not expose the temporary ICommandParameterRun widget bridge.");
            }

            ICommand? resultCommand = bridge.GetSelectValueCommand(hostHwnd);
            if (resultCommand is null ||
                !int.TryParse(resultCommand.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
            {
                throw new InvalidOperationException("The extension did not return its process ID.");
            }

            return new RemoteExtensionConnection(extension, processId);
        }
        catch
        {
            try
            {
                extension?.Dispose();
            }
            catch
            {
                // Preserve the activation failure; shutdown is best effort here.
            }

            throw;
        }
        finally
        {
            if (extensionPointer != 0)
            {
                Marshal.Release(extensionPointer);
            }
        }
    }

    private static partial class NativeMethods
    {
        internal const uint CLSCTX_LOCAL_SERVER = 0x4;

        [LibraryImport("ole32.dll")]
        internal static partial int CoCreateInstance(
            in Guid classId,
            nint outer,
            uint context,
            in Guid interfaceId,
            out nint instance);
    }
}

internal sealed record RemoteExtensionConnection(IExtension Extension, int ProcessId);
