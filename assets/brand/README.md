# Brand renders

Rasters of the weir mark for profiles and listings. The editable
sources live with the VS Code extension (its marketplace icon):
`editors/vscode/icon.svg` (the tiled mark) and
`editors/vscode/icon-mark.svg` (tileless, surface-adaptive).

- `avatar-1024/512/256.png` — **full-bleed** ground, no rounded
  corners: GitHub and most platforms apply their own mask, and a
  pre-rounded tile double-rounds. Use these for the GitHub org
  avatar and social profiles.
- `tile-1024.png` — the rounded tile, for contexts that show the
  image unmasked.

Re-render (from `site/`, where sharp lives): see the svg sources;
`sharp(svg).resize(N, N).png()` — the bars are the dark-ground set
(`#7fb2f0 / #4fc1e8 / #2fc6c6`) on `#0e1113`.
