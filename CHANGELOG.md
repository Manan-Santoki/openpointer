# Changelog

All notable changes to OpenPointer. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Each release's notes on GitHub are taken from its section here, so add an entry before tagging.

## [Unreleased]

## [0.5.0] - 2026-10-15

### Changed
- One Save for everything. Changes anywhere in the window (buttons and apps, pointer speed, scrolling, wheel mode, SmartShift, cursors, appearance, startup and update settings) are kept as pending changes until you click Save, and Discard reverts all of them. The save bar lists what's pending, for example "Buttons & apps · Pointer & scrolling · Cursor".
- Pointer speed still changes on the mouse as you move the slider so you can feel it, but it goes back if you discard.
- Pause remapping stays instant, and so does the tray menu.

## [0.4.4] - 2026-10-14

### Added
- Close button (×) in every dialog. Clicking the dimmed area outside a dialog or pressing Esc anywhere in the window also closes it.

### Fixed
- Dialogs now always fit inside the window: the title and buttons stay visible and long content scrolls. Before, tall dialogs could push their buttons out of view, leaving no way to dismiss them.
- The pointer preview grid fills four even columns instead of leaving empty space on the right.

## [0.4.2] - 2026-10-14

### Changed
- The Cursors page now shows only style cards. Each card has **View pointers**, which shows all 17 pointers in a dialog. Styles with settings also have **Options**, where you set shadow and size with a live preview and apply.
- Clicking a style that has options opens its options dialog. Clicking Windows applies it immediately.
- The **In use** badge uses the primary colors: white on dark, black on light.
- Cursor changes apply from their own dialog instead of the Save bar.

## [0.4.1] - 2026-10-14

### Added
- **Cursors** page in the sidebar, with style cards that preview each set's pointers.
- Live previews of all 17 Windows cursor types, rendered from the real cursor files. Animated cursors play.

### Changed
- The cursor style, shadow and size moved from Pointer & scrolling to the Cursors page.

## [0.4.0] - 2026-10-14

### Added
- **Cursor styles:** switch all of Windows between the default cursors and a bundled macOS set, applied instantly with no sign-out.
- The macOS set (Sierra and newer, by [antiden](https://github.com/antiden/macOS-cursors-for-Windows), MIT) comes with or without a shadow, in Normal, Large and Extra large.
- Your original cursors are backed up the first time OpenPointer changes them, can be restored at any time, and are restored when OpenPointer is uninstalled.
- **Cursor pack library:** each pack is a folder with a `pack.json` that holds its attribution, license, variants and file mapping. New packs are picked up automatically and validated by tests.

### Fixed
- Gesture and Actions Ring editors no longer touch the divider line of the button above them.

## [0.3.1] - 2026-10-14

### Fixed
- More accurate Actions Ring aiming:
  - The ring shows an aim dot and line, since the cursor is frozen while the button is held.
  - Aim is capped at the ring's edge, so reversing after an overshoot responds immediately.
  - A larger dead zone keeps small wobbles from selecting an item.
  - The selection holds until you move 8° past an item's border, so it no longer flickers between neighbours.

### Added
- README screenshots, and `--capture-docs` to regenerate them without touching the mouse.

## [0.3.0] - 2026-10-14

### Added
- **Actions Ring:** hold a button to get up to 8 actions in a circle around the cursor, then move toward one and release. Items can be edited, renamed, added and removed.
- **Macro editor:** multi-step actions made of shortcuts, typed text, launching apps or files, and waits.
- **Scroll speed:** lines per wheel notch, a Windows-wide setting.
- **Smooth scrolling:** high-resolution wheel reporting, on mice whose wheel supports it.
- **Settings backup:** export and import all profiles and device settings as a file.

### Fixed
- Quitting with unsaved changes no longer asks to save, which could block silent updates.

## [0.2.2] - 2026-10-14

### Changed
- The unsaved-changes bar is now a rounded card that floats over the page and slides in.

## [0.2.1] - 2026-10-14

### Added
- Save / Discard for button and profile edits in the settings window. Closing with unsaved changes asks first. Tray menu changes still apply instantly.
- **Apps** action group: Chrome new tab, Edge new tab, File Explorer, Task Manager, Calculator and Settings. These work on the desktop, where shortcuts like Ctrl+T have nothing to act on.
- Every button press is logged with the focused app, the profile used and the action, to help troubleshoot.

### Fixed
- The config file no longer stores an internal `shouldRepeat` field.

## [0.2.0] - 2026-10-14

### Added
- **Installer:** installs per user without admin rights, with Start menu entry, optional start with Windows, and an uninstaller. Before replacing files it asks a running instance to quit, so the buttons go back to normal first.
- **Release builds:** GitHub Actions tests, builds and publishes the installer, a portable zip and `SHA256SUMS.txt` for every version tag. CI runs on every push.
- **In-app updater:** checks GitHub Releases every 6 hours (or on demand) and offers updates in the tray and Settings. Downloads are verified against `SHA256SUMS.txt` and install silently, then the app relaunches.
- **Hold to repeat:** volume, arrows, Delete, zoom and undo/redo repeat at your keyboard repeat rate while the button is held, and scroll actions keep scrolling. This can be turned on or off per button.
- Dialogs open inside the window over a blurred background and stay with it.
- Smoother, slower scrolling in the settings window.

### Changed
- The Forward button is listed above Back, matching the mouse.
- The app picker hides background processes and always offers File Explorer.
- Shortcuts like `ctrl+shift+t` are stored readably in the config file.

### Fixed
- Menu separators use the app's styling.

## [0.1.0] - 2026-10-14

First build (not published as a release).

### Added
- Talks to Logitech mice directly over HID++ 2.0 through Bolt, Unifying and Lightspeed receivers or Bluetooth, with no Logi Options+ needed.
- **Per-app profiles:** remap buttons per app, with the desktop and taskbar as their own targets. The profile switches as soon as focus changes.
- **Actions:** keyboard shortcuts, mouse buttons, scrolling, launching apps/files/URLs, typing text, macros, media keys, DPI set/cycle/precision, wheel mode and Easy-Switch.
- **Gestures:** hold a button and move up, down, left or right, or just click.
- **Device settings:** pointer speed (DPI) with presets and per-app DPI, SmartShift, natural scrolling, thumb wheel, battery with low-battery alerts.
- **Tray menu:** change the focused app's buttons, DPI and wheel mode on the fly; pause remapping; start with Windows.
- **Settings window:** monochrome UI in light and dark, using the Geist font.
- All settings in one JSON file that reloads as soon as it's saved.
- Fails safe: buttons are remapped only while OpenPointer runs.
- Detects Logi Options+ and offers to stop it.

[Unreleased]: https://github.com/Manan-Santoki/openpointer/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.4.4...v0.5.0
[0.4.4]: https://github.com/Manan-Santoki/openpointer/compare/v0.4.2...v0.4.4
[0.4.2]: https://github.com/Manan-Santoki/openpointer/compare/v0.4.1...v0.4.2
[0.4.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/Manan-Santoki/openpointer/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/Manan-Santoki/openpointer/releases/tag/v0.2.0
