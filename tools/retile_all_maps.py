#!/usr/bin/env python3
"""Sequentially prepare 512px Local Map tiles for every legacy map."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("maps_root", type=Path)
    parser.add_argument("staging_root", type=Path)
    args = parser.parse_args()

    maps_root = args.maps_root.resolve()
    staging_root = args.staging_root.resolve()
    staging_root.mkdir(parents=True, exist_ok=True)
    worker = Path(__file__).with_name("retile_local_map.py")

    work: list[Path] = []
    for folder in sorted(path for path in maps_root.iterdir() if path.is_dir()):
        manifest_path = folder / "tiles-5x.json"
        if not manifest_path.exists():
            continue
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest["tileSize"] != 512:
            work.append(folder)

    started = time.perf_counter()
    totals = {"sourceTiles": 0, "outputTiles": 0, "outputPngMiB": 0.0, "seconds": 0.0}
    for index, folder in enumerate(work, 1):
        destination = staging_root / folder.name
        if (destination / "tiles-5x.json").exists():
            manifest = json.loads((destination / "tiles-5x.json").read_text(encoding="utf-8"))
            print(f"PROGRESS {index}/{len(work)} {folder.name} already prepared ({len(manifest['tiles'])} tiles)", flush=True)
            continue
        result = subprocess.run(
            [sys.executable, str(worker), str(folder), str(destination), "--tile-size", "512"],
            check=True,
            capture_output=True,
            text=True,
        )
        stats = json.loads(result.stdout)
        for key in totals:
            totals[key] += stats[key]
        print(
            f"PROGRESS {index}/{len(work)} {folder.name}: {stats['sourceTiles']} -> "
            f"{stats['outputTiles']} tiles, {stats['outputPngMiB']:.2f} MiB, {stats['seconds']:.1f}s",
            flush=True,
        )

    totals["wallSeconds"] = time.perf_counter() - started
    totals["maps"] = len(work)
    print("COMPLETE " + json.dumps(totals, sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
