<div align="center">

<span style="font-size: 48px">
🐿️🌰🧪
</span>

<h1 align="center"><span style="font-weight: bold">Out-of-process WinUI surfaces</span> <br /><span style="font-weight: 200">(experiment)</span></h1>

</div>

Present WinUI 3 content from an out-of-process Command Palette extension without
moving its XAML objects or event handlers into the host process. This proof of
concept activates a packaged extension through COM and displays the same
extension-owned window either inline or inside a host-designed popup.

The extension keeps ownership of its XAML tree, dispatcher, state, text input,
context menus, and event handlers. Only a narrow activation/provider contract and
an HWND cross the process boundary.

## Features

- **Command Palette integration** — Activates the packaged extension through COM
  using the real `Microsoft.CommandPalette.Extensions.IExtension` projection.
- **Two presentation modes** — Moves one extension-owned WinUI surface between an
  inline slot in the main window and a separate host-owned popup.
- **Host-owned design** — Keeps the main-window chrome, status bar, popup acrylic
  shell, header, border, placement, and Close button under `HostApp` control.
- **Anchored and resizable popup** — Keeps the popup aligned to its host button as
  the main window moves, resizes, or changes DPI, while retaining the size selected
  by the user.
- **Remote interaction** — Supports text input, left-click, right-click, and a
  WinUI context menu while the controls and event handlers remain in `WidgetApp`.
- **Process separation** — Runs the extension UI and its dispatcher in a separate
  process while the host monitors the remote window and process lifetime.

## How it works

1. `WidgetApp` is registered as a development package. Its manifest registers
   CLSID `95DDD426-45EF-48D2-85B1-45DB00CD6FB7` as a `windows.comServer` local
   server and advertises the `com.microsoft.commandpalette` app extension.
2. `HostApp` calls `CoCreateInstance(..., CLSCTX_LOCAL_SERVER, IID_IExtension)` on
   a worker thread. It does not start or reference `WidgetApp.exe` directly.
3. The host asks the projected `IExtension` for its commands provider. For this
   first POC, `ICommandParameterRun.GetSelectValueCommand(hostHwnd)` is used as a
   temporary adapter to pass the host HWND. Its returned `NoOpCommand.Id` carries
   the extension PID so the host can monitor the child window.
4. The COM server remains MTA, matching the Command Palette extension template. It
   starts a dedicated STA thread for `Application.Start`, the WinUI dispatcher,
   `Window`, and `WidgetPage`.
5. The extension changes its WinUI window to `WS_CHILD` and initially calls
   `SetParent` with the host-selected surface. Windows continues rendering and
   dispatching input in the extension process.
6. `HostApp` moves the same HWND between the inline slot and a second WinUI
   `Window`. The popup owns its Fluent shell while the extension owns only the
   content rectangle.
7. The popup shell finishes creating its WinUI composition surface before the
   extension HWND is inserted. This ordering keeps the remote pixels visible above
   the popup's own XAML composition child.

The temporary `ICommandParameterRun` adapter is not the proposed product ABI. A
Command Palette implementation should use a small, versioned UI-provider interface
that returns a site/session object with explicit attach, resize, focus, close, and
failure semantics.

## Installation

### Requirements

- `Microsoft.CommandPalette.Extensions` 0.11.260520004
- `Microsoft.Windows.CsWinRT` 2.2.0
- `Microsoft.WindowsAppSDK` 2.2.0
- `Microsoft.WindowsAppSDK.Foundation` 2.1.0
- `Microsoft.WindowsAppSDK.AI` 2.2.3
- `Microsoft.WindowsAppSDK.Runtime` 2.2.0
- `Shmuelie.WinRTServer` 2.1.1
- .NET 10 with the Windows SDK 26100 projection

The minimum supported Windows version remains 10.0.19041.0.

### Build, register, and run

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

> [!NOTE]
> Close a running host before rebuilding because its self-contained output is
> loaded. Closing the host also asks the remote window and COM server to shut down.

## Verified behavior

The x64 Debug POC has been built with zero warnings and zero errors, registered as
a development package, and launched through COM. Runtime inspection confirmed:

- the host and widget have different process IDs;
- the same widget HWND survives moves from inline to popup, back to inline, and
  into the popup again;
- moving or resizing the main window keeps the popup aligned to its host button;
- native edge resizing changes the popup and remote content together, and the
  selected popup size survives dismissal and reopening;
- the popup window, header, and Close button are owned by `HostApp`, while the
  embedded pane, text box, context menu, and buttons are owned by `WidgetApp` in
  the UI Automation tree;
- invoking the remote button updates its click counter;
- setting the remote text box updates its `TextChanged` state;
- right-clicking opens the WinUI context menu and its command updates widget-owned
  state;
- the host-owned Close button hides the popup without disconnecting the extension,
  which can then be shown inline or in the popup again; and
- closing the host also terminates the extension process.

## Limitations

- This POC validates Command Palette-shaped COM activation plus
  extension-owned HWND hosting. It does not transfer a WinUI `Page` through COM.
- The extension window is a direct child of either host top-level HWND. An
  intermediate host-owned child site produced the correct HWND hierarchy and UI
  Automation tree but visually blank WinUI content, so this result should not be
  generalized to arbitrary nested HWND topologies without compositor validation.
- The host can design chrome around the remote rectangle but cannot restyle the
  extension's XAML tree. Host overlays cannot occupy that rectangle because the
  child HWND must remain above the host's XAML composition child.
- DPI, focus, tab navigation, IME, popups, drag and drop, accessibility, theme, and
  suspend/reconnect behavior still need explicit contracts and testing.
- Extension calls can hang even though extension crashes are process-isolated. The
  POC keeps activation/provider RPC off the host UI thread; product code still
  needs deadlines, cancellation/restart policy, and terminal failure handling.
- Package registration identifies the COM server but is not authorization. A
  production host must validate extension identity and consent, minimize shared
  handles and capabilities, and define who may attach.
- Host and widget should run at compatible integrity levels and DPI-awareness
  contexts. Elevated-host behavior is intentionally not claimed by this POC.
- Reparenting a WinUI top-level window is a focused Win32 prototype, not a
  documented WinUI app model. A supported remote `ContentIsland` path would be
  preferable when it supports the required production scenario.



## References

- [C#/WinRT out-of-process server authoring](https://learn.microsoft.com/windows/apps/develop/platform/csharp-winrt/create-windows-runtime-component-cswinrt)
- [SetParent](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent)
- [Content islands overview](https://learn.microsoft.com/windows/apps/develop/composition/content-island)


## Licence

Apache 2.0

## Author

[Jiří Polášek](https://jiripolasek.com)