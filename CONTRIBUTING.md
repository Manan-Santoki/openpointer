# Contributing to OpenPointer

Thanks for helping! Bug reports, device reports, cursor packs and code are all welcome.

## Reporting bugs

[Open an issue](https://github.com/Manan-Santoki/openpointer/issues) and include:

- your mouse model and how it connects (Bolt, Unifying or Lightspeed receiver, or Bluetooth)
- your OpenPointer version (Settings → Updates) and Windows version
- what you did, what you expected, and what happened
- your log: **Settings → Log → Open log**, or `%APPDATA%\OpenPointer\openpointer.log`

**Device reports are especially useful.** Tell us which mouse you tried and whether buttons, DPI, SmartShift, thumb wheel and battery work. The output of the diagnostics tool helps even more (see below).

## Development setup

You need:
- Windows 10 or 11, x64
- the [.NET 10 SDK](https://dotnet.microsoft.com/download)
- a Logitech HID++ 2.0 mouse, to test against real hardware

```
git clone https://github.com/Manan-Santoki/openpointer.git
cd openpointer
dotnet test tests/OpenPointer.Tests
dotnet run --project src/OpenPointer
```

Only one instance runs at a time. Quit an installed copy first (tray → Quit, or `OpenPointer.exe --quit`), or it will just bring that copy's window forward. Stop Logi Options+ too, since it competes for the same buttons.

### Diagnostics tool

```
dotnet run --project src/OpenPointer.Cli -- dump      # identity, features, controls, DPI, battery
dotnet run --project src/OpenPointer.Cli -- listen    # remap controls and print their raw events
```

Set `OPENPOINTER_TRACE=1` to log every HID++ report.

## Project layout

| Path | What lives there |
|---|---|
| `src/OpenPointer.Core/Hidpp` | HID++ transport, feature access, mouse features (buttons, DPI, SmartShift, wheels, battery, Easy-Switch) |
| `src/OpenPointer.Core/Engine` | Device lifecycle and reconnects, per-app button remapping, action execution |
| `src/OpenPointer.Core/Config` | Settings model, profiles, actions, key chords, action catalog |
| `src/OpenPointer.Core/Platform` | Windows integration: input, focus tracking, cursors, autostart, Options+ detection |
| `src/OpenPointer.Core/Updates` | GitHub Releases update check and verified download |
| `src/OpenPointer` | WPF tray app and settings window |
| `src/OpenPointer.Cli` | Protocol diagnostics |
| `tests/OpenPointer.Tests` | Unit tests |
| `installer` | Inno Setup script |

## Making changes

- **Keep pull requests focused:** one feature or fix per PR.
- **Add or update tests** for logic in `OpenPointer.Core` (config, profile resolution, protocol parsing, actions).
- **Match the surrounding code.** Follow the existing naming, comment density and idioms. Comments explain *why*, not *what*.
- **UI changes:** keep the monochrome, shadcn-style look. Use the theme tokens in `Themes/` (no hard-coded colors) and check both light and dark mode. Include before/after screenshots in the PR. `OpenPointer.exe --capture-docs <folder>` renders every page to PNG.
- **Don't break fail-safe behavior.** Buttons must go back to normal when OpenPointer exits, crashes or the mouse reconnects.
- **Add a changelog entry** under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything users will notice.

Before opening a PR, run:

```
dotnet test tests/OpenPointer.Tests
dotnet build src/OpenPointer -c Release
```

CI runs the same checks on every push and pull request.

## Adding a cursor pack

Cursor packs are folders under `src/OpenPointer/Assets/Cursors/`, each with its cursor files, its license and a `pack.json`. See [the cursor pack guide](src/OpenPointer/Assets/Cursors/README.md).

- **Only submit packs whose license allows redistribution.**
- Keep the author, source link and license accurate, because the app shows them as credits.
- The tests check that every file referenced in `pack.json` exists.

## Supporting a new device or feature

Most HID++ 2.0 mice work without changes, because features are discovered from the device. If yours needs something new:

1. Run `openpointer-cli dump` and include the output in the issue or PR.
2. Add the feature in `Hidpp/MouseDevice.cs`, and show it in the UI only when the device reports it.
3. Write the protocol code from public documentation. Don't copy GPL-licensed code into this MIT project.

## License

By contributing, you agree that your contributions are licensed under the [MIT license](LICENSE).
