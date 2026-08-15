# CmdPal-style out-of-process WinUI page POC

This solution proves the first CmdPal integration slice: a packaged extension is
activated out of process through COM, using the real Command Palette `IExtension`
projection, and the extension-owned WinUI 3 page is presented inside an unpackaged
host.

No `Page`, XAML object, or event delegate crosses the process boundary. The extension
keeps its XAML tree, dispatcher, state, text input, context menu, and event handlers.
The object projected over COM is a narrow activation/provider contract; presentation
uses an extension-owned child HWND.

## Topology

1. `WidgetApp` is registered as a development package. Its manifest registers CLSID
   `95DDD426-45EF-48D2-85B1-45DB00CD6FB7` as a `windows.comServer` local server and
   also advertises the `com.microsoft.commandpalette` app extension.
2. `HostApp` calls `CoCreateInstance(..., CLSCTX_LOCAL_SERVER, IID_IExtension)` on a
   worker thread. It does not start or reference `WidgetApp.exe` directly.
3. The host asks the projected `IExtension` for its commands provider. For this first
   POC, `ICommandParameterRun.GetSelectValueCommand(hostHwnd)` is deliberately used as
   a temporary adapter to pass the host HWND. Its returned `NoOpCommand.Id` carries the
   extension PID so the host can monitor the child window.
4. The COM server remains MTA, matching the CmdPal extension template. It starts a
   dedicated STA thread for `Application.Start`, the WinUI dispatcher, `Window`, and
   `WidgetPage`.
5. The extension changes its WinUI window to `WS_CHILD`, calls `SetParent`, and the
   host positions that HWND over its XAML placeholder. Windows keeps rendering and
   input dispatch in the extension process.

The temporary `ICommandParameterRun` adapter is not the proposed product ABI. The
next CmdPal step should add a small, versioned UI-provider interface that returns a
site/session object and has explicit attach, resize, focus, close, and failure
semantics.

## Compatibility baseline

- `Microsoft.CommandPalette.Extensions` 0.11.260520004
- `Microsoft.Windows.CsWinRT` 2.2.0
- `Microsoft.WindowsAppSDK` 2.2.0
- `Shmuelie.WinRTServer` 2.1.1
- .NET 10 with the Windows SDK 26100 projection, matching the current CmdPal extension
  template; the minimum supported Windows version remains 10.0.19041.0

The resolved Windows App SDK graph retains `Microsoft.WindowsAppSDK.Foundation`
2.1.0, `Microsoft.WindowsAppSDK.AI` 2.2.3, and
`Microsoft.WindowsAppSDK.Runtime` 2.2.0.

## Build, register, and run

From this directory in PowerShell:

```powershell
dotnet restore .\RemoveWindowRendererDemo.slnx --configfile .\NuGet.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build .\RemoveWindowRendererDemo.slnx --no-restore -p:Platform=x64 -p:Configuration=Debug

$widgetLayout = (Resolve-Path '.\WidgetApp\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64').Path
$existingWidgetPackage = Get-AppxPackage -Name '77b99c6f-b3ed-484c-8ffd-0e0c4e566e8a'
if ($existingWidgetPackage) {
    Remove-AppxPackage -Package $existingWidgetPackage.PackageFullName
}
Add-AppxPackage -Register "$widgetLayout\AppxManifest.xml"

& '.\HostApp\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\HostApp.exe'
```

Close a running host before rebuilding because its self-contained output is loaded.
Closing the host requests the remote window and COM server to shut down as well.

## Verified behavior

The x64 Debug POC has been built with zero warnings and zero errors, registered as a
development package, and launched through COM. Runtime inspection confirmed that:

- the host and widget have different process IDs;
- the embedded pane and its controls are owned by `WidgetApp` in the UI Automation
  tree;
- invoking the remote button updates its click counter;
- setting the remote text box updates its `TextChanged` state;
- right-clicking opens the WinUI context menu and its command updates widget-owned
  state; and
- closing the host also terminates the extension process.

## Important POC boundaries

- This validates CmdPal-shaped COM activation plus extension-owned HWND hosting. It
  does not transfer a WinUI control through COM.
- A dedicated child site HWND should replace the host's top-level HWND before product
  integration. DPI, focus, tab navigation, IME, popups, drag/drop, accessibility,
  theme, and suspend/reconnect behavior still need explicit contracts and testing.
- Extension calls can hang even though extension crashes are process-isolated. The
  POC keeps activation/provider RPC off the host UI thread; product code still needs
  deadlines, cancellation/restart policy, and terminal failure handling.
- Package registration identifies the COM server but is not authorization. Any
  process allowed to activate the class can attempt the protocol. A production host
  must validate extension identity and consent, minimize handles/capabilities, and
  define who may attach.
- Keep host and widget at compatible integrity levels and DPI-awareness contexts.
  Elevated-host behavior is intentionally not claimed by this POC.
- Reparenting a WinUI top-level window is a focused Win32 prototype, not a documented
  WinUI app model. A supported remote `ContentIsland` path would be preferable once it
  supports the required production scenario.

Useful platform references:

- [C#/WinRT out-of-process server authoring](https://learn.microsoft.com/windows/apps/develop/platform/csharp-winrt/create-windows-runtime-component-cswinrt)
- [SetParent](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent)
- [Content islands overview](https://learn.microsoft.com/windows/apps/develop/composition/content-island)
