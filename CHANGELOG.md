# Changelog

All notable changes to OpenPointer. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

Each release's notes on GitHub are taken from its section here, so add an entry before tagging.

## [Unreleased]

## [0.9.1] - 2026-10-18

### Fixed
- Drawing a circle on the touchpad no longer triggers from small wiggles or zigzags. A circle must be at least about 15 mm across and roughly round, and sensor jitter is ignored.

### Added
- `openpointer-cli touchpad-replay` replays a touchpad recording through the gesture recognizer.

## [0.9.0] - 2026-10-17

### Added
- **Touchpad gestures** for precision touchpads. Select your touchpad under Devices (Settings → Developer mode → Show touchpads) to see a live drawing of it and give gestures an action, per app like buttons:
  - Touch and hold (or tap, when Windows tap to click is off) a corner.
  - Slide along an edge; the action repeats as you slide, which makes edges good volume or brightness sliders.
  - Swipe in from an edge.
  - Draw a circle, clockwise or counterclockwise; the action repeats every full turn.
  - Three- and four-finger swipes and taps, and a five-finger tap.
- OpenPointer never changes your Windows touchpad settings. Gestures Windows already uses are shown as Windows' and locked; set one to Nothing in Windows touchpad settings to use it in OpenPointer.
- openpointer-cli touchpad prints live finger positions.

## [0.8.2] - 2026-10-17

### Fixed
- Back and Forward no longer appear for mice that don't have them. Many mice tell Windows they have five buttons when they have three, so standard mice now show side buttons after you press them once, and OpenPointer remembers them for that model.
- Button remapping for standard mice recovers by itself if Windows stops delivering mouse input to OpenPointer.
- Built-in pointing devices are no longer labelled "PCI Device".
- The log lists the extra buttons each mouse reports and records the first press of each button, which makes mouse problems easier to diagnose.

## [0.8.1] - 2026-10-17

### Added
- Standard Windows mouse support for Bluetooth, USB and wireless mice from any brand, including three-button mice such as the Lenovo 600 Bluetooth Silent Mouse. Remap middle and available side buttons with per-app profiles, shortcuts, macros, gestures, the Actions Ring, and double/triple presses.
- Device details explain which settings are available. Standard Windows button mappings apply across mice; hardware DPI, battery, special wheel settings and host switching still require device-specific support.
- `openpointer-cli devices` lists Windows mice, connection types and reported buttons without opening HID++ connections.
- Touchpads are hidden by default. Enable **Settings → Developer mode → Show touchpads** and save to include them in the Devices list.

### Fixed
- Logitech Bolt receiver fallback reads the paired mouse name instead of showing "USB Receiver". Physical device identities keep different receivers apart, and built-in pointing devices have a clear label.
- Receivers retry mice that did not answer the first HID++ probe, so a missed startup response does not leave the mouse in generic mode.
- Simultaneous receiver notifications and discovery retries initialize each mouse once.
- Device discovery keeps checking for newly connected mice even when a Logitech receiver is already open.
- Pausing, disconnecting or quitting cancels pending presses and releases held actions without firing a gesture or ring action.

## [0.7.0] - 2026-10-15

### Added
- **Double and triple press.** Give the back, forward or middle button a separate action for pressing it twice or three times: open the button's menu and choose **Double & triple press…**. App profiles inherit these from All applications and can turn them off with **Off**. Buttons without them still respond instantly.
- **Double-press speed** in Settings (Fast, Normal, Slow) sets how long OpenPointer waits for another press.
- The Actions Ring editor shows a map of the ring, and an arrow on each item points to where it sits. Hover an item to highlight it on the map.

### Fixed
- The Actions Ring no longer picks an item by itself. The small movement as the button goes down is ignored, the dead zone is larger and scales with DPI, and a quick click closes the ring without running anything.

## [0.6.0] - 2026-10-15

### Added
- **Open app…** lists every app in the Start menu, including Microsoft Store apps like WhatsApp and Spotify, with icons and search. Store apps open by their app ID, so the action keeps working after the app updates and its install folder changes.
- **Browse…** in the same dialog opens any other program, script or portable app.
- Search in the **Add application** picker. Type to filter; Enter picks the first match.

### Changed
- "Open app or file…" is now two actions: **Open app…** and **Open file or folder…**.
- Open-app actions show the app's name, for example "Open WhatsApp".

## [0.5.1] - 2026-10-15

### Fixed
- Sliders and switches no longer flicker. Moving a slider or flipping a switch rebuilt the whole page, and the live pointer-speed preview rebuilt it again, so the control was replaced under the pointer. Now only the save bar updates, and the DPI preset highlight follows the slider.

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

[Unreleased]: https://github.com/Manan-Santoki/openpointer/compare/v0.8.1...HEAD
[0.8.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.7.0...v0.8.1
[0.7.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.6.0...v0.7.0
[Unreleased]: https://github.com/Manan-Santoki/openpointer/compare/v0.9.1...HEAD
[0.9.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.9.0...v0.9.1
[0.9.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.8.2...v0.9.0
[0.8.2]: https://github.com/Manan-Santoki/openpointer/compare/v0.8.1...v0.8.2
[0.8.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.7.0...v0.8.1
[0.7.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/Manan-Santoki/openpointer/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/Manan-Santoki/openpointer/compare/v0.5.0...v0.5.1
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
