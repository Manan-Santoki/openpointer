# Cursor packs

Each folder here is one cursor pack, listed automatically in **Pointer & scrolling → Cursor**.

## Adding a pack

1. Create a folder named after the pack id, e.g. `breeze`.
2. Add the cursor files (`.cur` / `.ani`) and the pack's license file.
3. Add `pack.json`:

```json
{
  "id": "breeze",
  "name": "Breeze",
  "description": "KDE Breeze pointers",
  "author": "Author name",
  "source": "https://example.com/original-repo",
  "license": "MIT",
  "licenseFile": "LICENSE",
  "shadow": { "on": "shadow", "off": "no-shadow" },
  "sizes": { "normal": "32", "large": "48" },
  "path": "{shadow}/{size}",
  "roles": { "Arrow": "default.cur", "IBeam": "text.cur", "Wait": "wait.ani" }
}
```

- `shadow` and `sizes` are optional. Omit them (and the placeholders in `path`) for a single-variant pack; `path` can then be `""`.
- `sizes` keys must be `normal`, `large` or `xlarge`; values are folder names.
- `roles` maps Windows cursor roles to files: Arrow, Help, AppStarting, Wait, Crosshair, IBeam, NWPen, No, SizeNS, SizeWE, SizeNWSE, SizeNESW, SizeAll, UpArrow, Hand, Pin, Person. Roles you leave out use the Windows default.
- Only include packs whose license allows redistribution, and keep the author, source and license accurate: the app shows them as attribution.
