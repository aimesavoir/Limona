# Edit Icons

Drop your PNG icons here (24x24 or 32x32, transparent background recommended).

Current file names used by the Edit window:

- rotate+90.png   — Rotate 90° clockwise
- rotate-90.png   — Rotate 90° counter-clockwise
- back.png        — Rotate 180° / Reset
- flipH.png       — Flip horizontal
- flipV.png       — Flip vertical
- crop-tool.png   — Crop mode
- confirm.png     — Apply crop (checkmark)

Fallback directories checked at runtime:
1. `Assets\EditIcons\` next to the exe (bundled)
2. `%LOCALAPPDATA%\Limona\EditIcons\` (override without rebuilding)

If a file is missing, the button shows a fallback text glyph.
