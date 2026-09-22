# Changelog

## 0.9.3

- Retiled all 97 high-resolution maps into alpha-cropped 512 px tiles.
- Replaced the fixed tile-count cache with a 192 MiB decoded-texture budget.
- Pooled map tile UI objects to avoid hierarchy rebuilds during pan and zoom.
- Added aggregated full-map performance diagnostics to the BepInEx log.
- Kept the full map centered on the player whenever it opens.
- Added discovered campfire and save-point markers using game-style assets.
- Added localized map controls and native per-language font selection.
- Updated the standalone installer with strict payload selection and English UI.
