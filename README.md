# OpenPointer

An open-source, customizable alternative to Logi Options+ for Windows. It talks to Logitech mice directly over HID++ 2.0, so there is no account, no cloud, and no telemetry.

## Features

- **Per-app button profiles.** Chrome, File Explorer, the desktop, the taskbar, or any `.exe`. Switches instantly when focus changes.
- **Actions:** keyboard shortcuts (held while the button is held), mouse buttons, scrolling, launch an app/file/URL, type text, macros, media keys, DPI set/cycle/precision-hold, wheel mode, Easy-Switch.
- **Gestures:** hold a button and move — tap/up/down/left/right each run their own action.
- **Actions Ring:** hold a button for a radial menu of up to 8 actions at the cursor; aim and release.
- **Macros:** multi-step actions (shortcuts, typed text, launches, waits) with an editor.
- **Pointer speed** with the device's real DPI range, presets, and per-app DPI.
- **Scrolling:** scroll speed, smooth (high-resolution) scrolling, natural scrolling, SmartShift on/off and sensitivity, ratchet/free spin, thumb wheel remap and invert.
- **Backup:** export and import all settings as one file.
- **Battery** in the tray, the window, and a low-battery notification.
- **Tray menu** to change the focused app's buttons, DPI and wheel mode on the fly.
- **One JSON file** (`%APPDATA%\OpenPointer\config.json`), reloaded live when edited.
- Fails safe: buttons are only remapped while OpenPointer runs; on exit, crash or sleep the mouse falls back to its built-in behavior.

## Supported hardware

Any HID++ 2.0 mouse over a Bolt, Unifying or Lightspeed receiver, or Bluetooth. Features are discovered from the device, so options only appear when your mouse supports them. Verified on a Signature M650 L (Bolt).

## Install

Download `OpenPointer-Setup-x.y.z.exe` from [Releases](https://github.com/Manan-Santoki/openpointer/releases/latest) and run it. It installs per user (no admin rights) and can start with Windows. A portable zip is also attached to every release.

OpenPointer checks GitHub Releases for updates and installs them in one click from the tray or Settings. Downloads are verified against the release's `SHA256SUMS.txt`.

The builds are not code-signed yet, so Windows SmartScreen may warn on first run ("More info" → "Run anyway").

## Release

Push a version tag; GitHub Actions tests, builds the installer and portable zip, and publishes the release:

```
git tag v0.3.0
git push origin v0.3.0
```

Local installer build (Inno Setup 6.6+): `ISCC.exe /DAppVersion=0.3.0 installer\OpenPointer.iss` after publishing to `artifacts/publish`.

## Run from source

Requires the .NET 10 SDK.

```
dotnet run --project src/OpenPointer
```

Logi Options+ competes for the same buttons. OpenPointer detects it and offers to stop it (it stays installed).

Command-line flags: `--background` (start in the tray), `--quit` (close a running instance).

## Diagnostics

```
dotnet run --project src/OpenPointer.Cli -- dump              # features, controls, DPI, battery
dotnet run --project src/OpenPointer.Cli -- listen            # divert controls and print raw events
OPENPOINTER_TRACE=1 dotnet run --project src/OpenPointer.Cli  # log every HID++ report
```

## Layout

| Path | Contents |
|---|---|
| `src/OpenPointer.Core/Hidpp` | HID++ transport, feature access, mouse features |
| `src/OpenPointer.Core/Engine` | Device lifecycle, per-app diversion, action execution |
| `src/OpenPointer.Core/Config` | Config model, profiles, actions, key chords |
| `src/OpenPointer` | WPF tray app and settings window |
| `src/OpenPointer.Cli` | Protocol diagnostics |
| `tests/OpenPointer.Tests` | Unit tests |

## License

MIT. Geist fonts under the SIL Open Font License. Not affiliated with Logitech; Logitech, Logi, MX Master and Options+ are trademarks of Logitech.
