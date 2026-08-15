# Cross-process WinUI 3 Page demo

This demo hosts a WinUI 3 `Page` owned by `WidgetApp.exe` inside the window owned by `HostApp.exe`. The Page is not serialized, marshaled, or reconstructed by the host. Its XAML tree, state, and event handlers stay on the widget process's UI thread.

The current compatibility baseline is `Microsoft.WindowsAppSDK` 2.2.0. Its resolved package graph includes `Microsoft.WindowsAppSDK.Foundation` 2.1.0, `Microsoft.WindowsAppSDK.AI` 2.2.3, and `Microsoft.WindowsAppSDK.Runtime` 2.2.0. Rendering, editable text input, `Click`/`RightTapped`, and a WinUI `ContextFlyout` have been verified on that package set.

The runnable implementation uses a child HWND:

1. `HostApp.exe` starts `WidgetApp.exe` and passes its host HWND on the command line.
2. `WidgetApp.exe` creates a normal WinUI 3 `Window` containing `WidgetPage`.
3. The widget changes that window to `WS_CHILD` and calls the Win32 `SetParent` API.
4. The host discovers the widget's child HWND and moves it over the XAML placeholder whenever layout changes.
5. Windows renders the child window and routes mouse input to its owning process. The Page's normal `Click` and `RightTapped` handlers update the visible counters.

## Relation to Xbox Game Bar widgets

Xbox Game Bar widgets also remain separate UI/process entities. Public Game Bar APIs give a widget its own `CoreWindow` and XAML `Frame`, while Game Bar owns placement, activation, visibility, and other host policy. The public documentation does not describe Game Bar as transferring a `Page` object across IPC; the useful model is out-of-process UI ownership plus platform-managed presentation and input.

- [Xbox Game Bar overview](https://learn.microsoft.com/en-us/xbox/game-bar/overview)
- [XboxGameBarWidget API](https://learn.microsoft.com/en-us/xbox/game-bar/api/xgb-widget)

Windows App SDK has a closer composition-level shape in `ContentIsland`, `DesktopChildSiteBridge`, and the experimental remote-endpoint APIs. In the current experimental packages, those remote APIs are not a production contract. A remote `XamlIsland` also currently fails during XAML input/drag-drop initialization in this scenario; `InputUnderlyingWindowController` is documented for `ContentIsland.CreateForSystemVisual`, not for repairing a remote XAML island. This demo therefore uses the HWND route on the stable Windows App SDK instead of presenting that experimental path as working.

- [Content islands overview](https://learn.microsoft.com/en-us/windows/apps/develop/composition/content-island)
- [XamlIsland](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.xamlisland)
- [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)

## Build and run

From this directory:

```powershell
dotnet restore .\RemoveWindowRendererDemo.slnx --configfile .\NuGet.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build .\RemoveWindowRendererDemo.slnx --no-restore -p:Platform=x64 -p:Configuration=Release
& .\HostApp\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\HostApp.exe
```

The HostApp build target builds WidgetApp first and copies its output into a `WidgetApp` directory beside `HostApp.exe`.

## Important limitations

Cross-process `SetParent` is a useful prototype, not an equivalent replacement for Game Bar's system host:

- Both processes should use the same DPI-awareness mode and integrity level.
- Popups, keyboard focus, IME, drag/drop, accessibility, and shutdown need deliberate production handling.
- A widget crash is isolated by process, but the host still needs restart and health policy.
- The host passes a raw HWND on the command line; a real plugin protocol needs authentication, capability negotiation, versioning, and lifecycle IPC.
- Reparented WinUI top-level windows are not a documented WinUI app model, even though the underlying Win32 child-window mechanism works for this focused demo.

For a production extension system, prefer a platform-owned widget contract when one exists. Otherwise choose between a constrained HWND host, a custom pixel/input remoting protocol, or the Windows App SDK remote-island APIs only after they become supported for the required UI stack.
