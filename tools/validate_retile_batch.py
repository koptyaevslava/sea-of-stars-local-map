#!/usr/bin/env python3
"""Validate a staged Local Map retile batch before it is installed."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("staging_root", type=Path)
    args = parser.parse_args()
    root = args.staging_root.resolve()

    map_count = 0
    tile_count = 0
    png_bytes = 0
    decoded_bytes = 0
    largest_manifest = 0

    for map_folder in sorted(path for path in root.iterdir() if path.is_dir()):
        manifest_path = map_folder / "tiles-5x.json"
        if not manifest_path.is_file():
            raise ValueError(f"missing manifest: {map_folder.name}")
        raw = manifest_path.read_bytes()
        largest_manifest = max(largest_manifest, len(raw))
        if len(raw) > 1024 * 1024:
            raise ValueError(f"manifest exceeds 1 MiB: {map_folder.name}")
        manifest = json.loads(raw)
        if manifest.get("tileSize") != 512:
            raise ValueError(f"wrong tile size: {map_folder.name}")
        tiles = manifest.get("tiles", [])
        if not tiles or len(tiles) > 4096:
            raise ValueError(f"invalid tile count {len(tiles)}: {map_folder.name}")

        positions: set[tuple[int, int]] = set()
        for tile in tiles:
            x = int(tile["x"])
            y = int(tile["y"])
            width = int(tile["width"])
            height = int(tile["height"])
            if (x, y) in positions:
                raise ValueError(f"duplicate tile position {(x, y)}: {map_folder.name}")
            positions.add((x, y))
            if width < 1 or height < 1 or width > 512 or height > 512:
                raise ValueError(f"invalid tile dimensions {width}x{height}: {map_folder.name}")
            if x < 0 or y < 0 or x + width > manifest["width"] or y + height > manifest["height"]:
                raise ValueError(f"tile outside atlas: {map_folder.name}/{tile['file']}")

            tile_path = (map_folder / tile["file"]).resolve()
            if map_folder.resolve() not in tile_path.parents or not tile_path.is_file():
                raise ValueError(f"missing or unsafe tile path: {map_folder.name}/{tile['file']}")
            encoded = tile_path.read_bytes()
            if hashlib.sha256(encoded).hexdigest() != tile["sha256"]:
                raise ValueError(f"SHA mismatch: {map_folder.name}/{tile['file']}")
            with Image.open(tile_path) as image:
                if image.mode != "RGBA" or image.size != (width, height):
                    raise ValueError(f"PNG metadata mismatch: {map_folder.name}/{tile['file']}")
                if image.getchannel("A").getbbox() is None:
                    raise ValueError(f"fully transparent tile: {map_folder.name}/{tile['file']}")

            tile_count += 1
            png_bytes += len(encoded)
            decoded_bytes += width * height * 4
        map_count += 1

    print(json.dumps({
        "maps": map_count,
        "tiles": tile_count,
        "pngMiB": round(png_bytes / 1048576, 2),
        "decodedMiB": round(decoded_bytes / 1048576, 2),
        "largestManifestKiB": round(largest_manifest / 1024, 2),
    }, indent=2))


if __name__ == "__main__":
    main()
