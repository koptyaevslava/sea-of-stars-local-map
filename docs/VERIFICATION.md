# Verification

## Automated checks

- Release build: 0 warnings and 0 errors.
- Managed test suite: 18 tests passed.
- Map inventory: 97 maps.
- Detail inventory: 27,130 PNG tiles, all no larger than 512 by 512 pixels.
- Every tile path, dimension, atlas position, PNG mode, and SHA-256 checksum was validated.
- Every map manifest is below 1 MiB and contains fewer than 4,096 tiles.
- Installer payload selection rejects files associated with other mods.

## Performance diagnosis

The previous 2048 by 2048 tiles decoded to as much as 16 MiB each and were uploaded synchronously by Unity during pan and zoom. The 512 by 512 layout caps one decoded upload at 1 MiB. Visible tiles are retained under a 192 MiB memory budget and UI tile objects are pooled.

## Manual checks

The player should verify several large locations in the game:

1. Open the full map with Select/View or M.
2. Confirm the map initially centers on the player.
3. Pan and zoom repeatedly and watch for frame-time spikes.
4. Confirm that the minimap uses a fixed scale.
5. Confirm that discovered campfires and save points appear in both views.
6. Change the game language and confirm that map prompts use the selected language and the native font baseline.

The game is not launched by the build or verification scripts.
