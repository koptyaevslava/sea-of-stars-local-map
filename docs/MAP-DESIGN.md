# Local Map Design

Local Map uses authored, pre-rendered images for each supported gameplay area. It does not build a map from screenshots during play. The player sees the complete map geometry immediately, while persistent exploration fog controls which parts are revealed.

## Views

The circular minimap uses a fixed world scale and follows the player. The full map opens centered on the player, supports manual zoom and pan, and can reset to either the player or the complete-map view.

Both views use the same projection metadata. Map pixels are projected from game-world coordinates with two authored basis vectors and an offset. Elevation is retained where the original map uses a sheared basis.

## Rendering and streaming

Each high-resolution map is split into alpha-cropped tiles no larger than 512 by 512 pixels. Only visible tiles are loaded. A 192 MiB decoded-texture budget controls eviction, and pooled UI objects avoid rebuilding the entire tile hierarchy during pan and zoom.

Overview images are used when the visible-detail selection would exceed the tile or memory limit. Point filtering preserves the intended pixel-art presentation.

## Exploration

Exploration state is stored separately from game saves. Reveal operations update a persistent fog texture and interpolate fast player movement so that explored paths do not contain gaps. Floor metadata keeps vertically separated rooms independent when required.

Campfires and save points become visible after their position has been revealed. The player marker remains upright because direction is not useful in the isometric view.

## Input and native UI

The full map integrates with the existing Select/View HUD state. Controller input uses triggers, the right stick, and right-stick click. Keyboard input uses M, the mouse wheel or Up/Down, WASD, Space, and Home.

Frames, button prompts, markers, portraits, and fonts follow the game's existing UI assets and language-specific font selection.
