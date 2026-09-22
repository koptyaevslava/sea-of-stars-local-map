#!/usr/bin/env python3
"""Split one existing Local Map PNG atlas into small alpha-cropped tiles.

This never renders or rescales the map. Pixel values and atlas coordinates stay
unchanged; fully transparent cells are omitted. The result is written to a new
directory so the source map remains available for rollback.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import time
from pathlib import Path

from PIL import Image


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("map_folder", type=Path)
    parser.add_argument("output_folder", type=Path)
    parser.add_argument("--tile-size", type=int, default=512)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.tile_size < 256 or args.tile_size > 2048:
        raise SystemExit("tile size must be in the Local Map manifest range 256..2048")
    map_folder = args.map_folder.resolve()
    output_folder = args.output_folder.resolve()
    if output_folder.exists():
        raise SystemExit(f"output already exists: {output_folder}")

    source_manifest_path = map_folder / "tiles-5x.json"
    source_manifest = json.loads(source_manifest_path.read_text(encoding="utf-8"))
    source_tiles = source_manifest["tiles"]
    output_tiles_dir = output_folder / "tiles-5x-512"
    output_tiles_dir.mkdir(parents=True)

    started = time.perf_counter()
    output_tiles: list[dict[str, object]] = []
    decoded_pixels = 0
    visible_pixels = 0

    for source in source_tiles:
        source_path = (map_folder / source["file"]).resolve()
        if map_folder not in source_path.parents:
            raise ValueError(f"source tile escapes map folder: {source['file']}")
        with Image.open(source_path) as opened:
            image = opened.convert("RGBA")
        if image.size != (source["width"], source["height"]):
            raise ValueError(f"source dimensions do not match manifest: {source['file']}")
        decoded_pixels += image.width * image.height

        for local_y in range(0, image.height, args.tile_size):
            for local_x in range(0, image.width, args.tile_size):
                right = min(local_x + args.tile_size, image.width)
                bottom = min(local_y + args.tile_size, image.height)
                cell = image.crop((local_x, local_y, right, bottom))
                alpha_bounds = cell.getchannel("A").getbbox()
                if alpha_bounds is None:
                    continue
                cropped = cell.crop(alpha_bounds)
                atlas_x = source["x"] + local_x + alpha_bounds[0]
                atlas_y = source["y"] + local_y + alpha_bounds[1]
                name = f"tile-{atlas_x:05d}-{atlas_y:05d}.png"
                relative = f"tiles-5x-512/{name}"
                destination = output_tiles_dir / name
                cropped.save(destination, format="PNG", compress_level=6, optimize=False)
                encoded = destination.read_bytes()
                output_tiles.append(
                    {
                        "file": relative,
                        "sha256": hashlib.sha256(encoded).hexdigest(),
                        "x": atlas_x,
                        "y": atlas_y,
                        "width": cropped.width,
                        "height": cropped.height,
                    }
                )
                visible_pixels += cropped.width * cropped.height

    output_tiles.sort(key=lambda tile: (tile["y"], tile["x"]))
    if len(output_tiles) > 4096:
        shutil.rmtree(output_folder)
        raise ValueError(f"result has {len(output_tiles)} tiles; manifest limit is 4096")

    output_manifest = dict(source_manifest)
    output_manifest["tileSize"] = args.tile_size
    output_manifest["tiles"] = output_tiles
    (output_folder / "tiles-5x.json").write_text(
        json.dumps(output_manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    size = sum(path.stat().st_size for path in output_tiles_dir.iterdir())
    elapsed = time.perf_counter() - started
    print(
        json.dumps(
            {
                "sourceTiles": len(source_tiles),
                "outputTiles": len(output_tiles),
                "sourceDecodedMiB": decoded_pixels * 4 / 1048576,
                "outputDecodedMiB": visible_pixels * 4 / 1048576,
                "outputPngMiB": size / 1048576,
                "seconds": elapsed,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
