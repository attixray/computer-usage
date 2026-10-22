# CompUsage app icon

The icon is a flat, hand-authored vector chosen on 2026-10-22 from five concepts ("D · Napló"): a light work-log sheet with a folded corner and two text lines on a teal rounded-square tile, with a dark clock badge in the lower right. It replaces the earlier generated gradient padlock-clock artwork.

- Vector source: `design/app-icon.svg` (64×64 view box)
- Rendered artwork: `src/CompUsage.App/Assets/app-icon-source.png` (1024×1024, transparent outside the tile)
- Windows icon: `src/CompUsage.App/Assets/CompUsage.ico`
- Interface image: `src/CompUsage.App/Ui/assets/app-icon.png`

Palette: tile #146b66, sheet #e9f1f3, fold #b9d6d9, clock #1d2b31, clock hands #8dd8cf.

## Rebuilding

Render the SVG to a 1024×1024 PNG with a transparent background, for example with headless Microsoft Edge:

```powershell
msedge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=00000000 --window-size=1024,1024 --screenshot=app-icon-source.png icon.html
```

where `icon.html` embeds the SVG at 1024×1024 px with a transparent page background. Then package it with the IconBuilder command in the README.
