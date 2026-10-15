# OpenPointer

[![Latest release](https://img.shields.io/github/v/release/Manan-Santoki/openpointer)](https://github.com/Manan-Santoki/openpointer/releases/latest)
[![License: MIT](https://img.shields.io/github/license/Manan-Santoki/openpointer)](LICENSE)

OpenPointer configures Logitech mice on Windows without Logi Options+. It talks to the mouse directly over HID++ 2.0, the protocol Options+ itself uses, so there's no Logitech account, cloud sync or telemetry involved.

I started it because I wanted the side buttons on my mouse to do different things on the desktop than in File Explorer, and to keep changing the volume while I hold a button. Options+ can't do either.

<p align="center">
  <img src="docs/screenshots/buttons.png" width="49%" />
  <img src="docs/screenshots/cursors.png" width="49%" />
</p>

It can:

- remap buttons, globally or per app (the desktop and taskbar count as separate apps)
- send shortcuts, open apps, files or URLs, type text, and run macros
- repeat keys like volume and arrows while the button is held
- do gestures, and show an Actions Ring (hold a button, aim at an action, release)
- change DPI, scroll speed, SmartShift and the thumb wheel, if your mouse has them
- switch Windows cursors between the default set and a macOS set
- show battery level in the tray and warn when it's low

## Install

Download `OpenPointer-Setup-x.y.z.exe` from the [latest release](https://github.com/Manan-Santoki/openpointer/releases/latest) and run it. It installs for the current user and doesn't need admin rights. Each release also has a portable zip.

The builds aren't code-signed yet, so SmartScreen will warn you the first time. Click "More info", then "Run anyway".

Quit Logi Options+ before using OpenPointer, since only one of them can control the buttons. OpenPointer will offer to stop it if it's running. You don't have to uninstall it.

OpenPointer updates itself from GitHub releases. You can also check from Settings.

## Usage

Open OpenPointer from the Start menu or the tray icon.

- **Buttons:** pick a button and choose what it does, then click Save. The menu at the top switches between "All applications" and your per-app profiles.
- **Applications:** add an app to give it its own buttons.
- **Tray menu:** right-click the tray icon while any app is in front to change that app's buttons, DPI or wheel mode without opening the window.

Settings live in `%APPDATA%\OpenPointer\config.json`. You can edit the file by hand and it reloads when you save it.

Buttons are only remapped while OpenPointer runs. If you quit it, or it crashes, the mouse goes back to its normal behavior.

## Compatibility

- Windows 10 1809 or later, x64.
- Logitech mice that speak HID++ 2.0, connected through a Bolt, Unifying or Lightspeed receiver or over Bluetooth. That's most Logitech mice from the last decade.

So far it has only been tested on a Signature M650 L. If you try another mouse, please [open an issue](https://github.com/Manan-Santoki/openpointer/issues) saying whether it worked.

Known limitations:

- Remapped buttons don't work in windows running as administrator, because Windows blocks input from normal apps there.
- It doesn't pair devices or update firmware. Use Logitech's Logi Bolt app or Options+ for that.
- Mice only for now, no keyboards.

## Contributing

Bug reports, device reports and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). Release notes are in [CHANGELOG.md](CHANGELOG.md).

## License

MIT. The macOS cursors are by [antiden](https://github.com/antiden/macOS-cursors-for-Windows) (MIT), and the Geist font is under the SIL Open Font License.

OpenPointer isn't affiliated with Logitech.
