# OpenPointer: notes for agents and maintainers

Open-source mouse customization for Windows. A WPF tray app supports standard Bluetooth, USB and wireless mice through Windows input, with additional HID++ 2.0 hardware controls for Logitech mice (HidSharp). User docs are in README.md; contributor docs are in CONTRIBUTING.md. This file covers how to build, verify and ship.

## Build, run, test

```
dotnet test tests/OpenPointer.Tests          # unit tests (must pass before any commit)
dotnet build src/OpenPointer                 # app (Debug)
dotnet run --project src/OpenPointer         # run from source
```

- Single instance (mutex `OpenPointer.Singleton`). A second launch only signals the running one to show its window.
- Quit an installed instance with `"%LOCALAPPDATA%\Programs\OpenPointer\OpenPointer.exe" --quit` before running a dev build.
- Flags:
  - `--background`: start in the tray.
  - `--quit`: ask the running instance to exit; exits immediately if none is running.
  - `--restore-cursors`: used by the uninstaller.
  - `--capture-docs <dir>`: render pages to PNG.

## Verifying changes

- **Protocol:** `dotnet run --project src/OpenPointer.Cli -- dump` (read-only) and `-- listen` (remaps controls, prints events). `OPENPOINTER_TRACE=1` logs raw reports.
- **UI:** `OpenPointer.exe --capture-docs <dir>` renders every page plus a sample dialog to 2× PNGs.
  - It runs a *passive* DeviceManager that never remaps controls, so it's safe next to a running instance.
  - Use `OPENPOINTER_CAPTURE_HEIGHT=1500` for tall pages.
  - Set `OPENPOINTER_CONFIG=<path to a config.json copy>` to render a modified config without touching the real one.
  - Prefer this to screenshotting the live window: the user may be using it, and UI Automation leaves focus rings.
- **Runtime log:** `%APPDATA%\OpenPointer\openpointer.log` records every button press (focused app → profile → action) and every remapping change.

## README screenshots

Regenerate them with:

```
src/OpenPointer/bin/Debug/net10.0-windows/OpenPointer.exe --capture-docs docs/screenshots
```

Then delete `settings.png` (it shows the local username in the config path) and `dialog-pointers.png`. The README uses `buttons.png`, `cursors.png` and `applications.png`.

## Releasing

1. Bump `<Version>` in `src/OpenPointer/OpenPointer.csproj`.
2. Add a `## [x.y.z] - YYYY-MM-DD` section to `CHANGELOG.md` (Keep a Changelog: Added / Changed / Fixed, written for users) and update the compare links at the bottom.
3. Commit and merge to `main`. A push to `main` whose `<Version>` has no release yet publishes it; pushing a tag `vX.Y.Z` still works too.
4. `.github/workflows/release.yml` then:
   - takes the notes from the changelog via `.github/scripts/release-notes.ps1`, and **fails if the entry is missing**
   - runs the tests and publishes a single-file self-contained build
   - builds the Inno Setup installer and a portable zip, and writes `SHA256SUMS.txt`
   - creates the GitHub release

The in-app updater only accepts releases that include `OpenPointer-Setup-*.exe` and `SHA256SUMS.txt`, and skips drafts and prereleases.

### Local installer build and install test

```
dotnet publish src/OpenPointer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:Version=X.Y.Z -o artifacts/publish
ISCC.exe /Q /DAppVersion=X.Y.Z installer\OpenPointer.iss
artifacts\OpenPointer-Setup-X.Y.Z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Inno Setup 6.6+ is required (`WizardStyle=modern dynamic`); locally it's from scoop `extras/inno-setup`. A silent install quits the running app gracefully (`PrepareToInstall` → `--quit`) and relaunches it with `--background`. Don't use PowerShell `Start-Process -Wait` on the installer: it also waits for the relaunched app and hangs.

## Architecture notes

- **Two mouse backends.** Standard mice use `WindowsMouseDiscovery` for identity and HID button capabilities, and a dedicated `WindowsMouseHook` thread for the shared Windows input stream. `WindowsMouseRouter` ignores injected input and preserves intercepted press/release pairs. The hook cannot select a physical source mouse, so standard mappings apply across mice. Logitech HID++ devices retain direct hardware control and replace the generic entry for the same physical device.
- **Touchpads are opt-in.** A sibling HID Touch Pad collection identifies their legacy mouse interfaces. They are hidden unless both `General.DeveloperMode` and `General.ShowTouchpads` are enabled. These settings use Draft and Save like other settings.

- **Remapping is temporary.** Controls are remapped with HID++ "temporary diversion", which the device forgets on power cycle or reconnect, so OpenPointer fails safe. `DeviceManager` re-applies on reconnect (receiver 0x41 notifications) and hands controls back on exit.
- **Remapping follows focus.** Only controls whose action in the focused app's profile isn't `native` are remapped, so native means truly untouched. Gestures and the Actions Ring also request raw XY movement.
- **Desktop is its own context.** `@desktop` means window class `Progman`/`WorkerW` and `@taskbar` means `Shell_TrayWnd`. Both are separate from `explorer.exe` (File Explorer windows).
- **One Save for everything in the window:**
  - All edits (profiles, device settings, general settings, cursors) go through `Draft`, a full clone of the config, and the floating Save bar. `Draft.Save` writes the config, then applies the parts that live outside the mouse: cursors, theme, Windows scroll lines and autostart.
  - DPI previews live on the device and is reverted by `Draft.Discard`.
  - Pause remapping and the tray menu are instant. One-off actions (Restore cursors, Reset, Import, Easy-Switch) run immediately after a confirm.
- **Config:** `%APPDATA%\OpenPointer\config.json`, reloaded live by `ConfigStore`. Actions are polymorphic records with a `type` discriminator. Computed properties need `[JsonIgnore]`.
- **Cursors:**
  - Packs live in `src/OpenPointer/Assets/Cursors/<id>/pack.json`, shipped as loose files next to the exe (`ExcludeFromSingleFile`).
  - Applying copies the files to `%LOCALAPPDATA%\OpenPointer\Cursors\<variant>`, writes `HKCU\Control Panel\Cursors` and calls `SPI_SETCURSORS`.
  - The original scheme is backed up once to `cursor-backup.json`.
- **Dialogs:**
  - `DialogHost` is an in-window overlay card, not a separate window. `ShowFor` pumps a nested `DispatcherFrame`, so callers stay synchronous.
  - It closes via ×, Esc, or a click on the backdrop.

## UI conventions

- Monochrome zinc palette, in the shadcn style. Tokens live in `Themes/Dark.xaml` and `Themes/Light.xaml`, styles in `Themes/Controls.xaml` and `Themes/Picker.xaml`.
- Never hard-code colors. Never give a style the same key as a brush (a `Card` style vs `CardBackground` brush collision broke rendering once).
- Geist is bundled for text. Icons come from Segoe MDL2 Assets, because the user's Windows 10 has no Segoe Fluent Icons.
- Pages are code-built `IPage` classes that re-render on config, device and theme changes. Use the `Ui.*` helpers (Row, Card, Section, Switch, MenuItem) so code-built UI matches the XAML styles.

## Git

- Commit and push only when asked.
- No attribution trailers in commit messages.
- Author: Manan Santoki.
