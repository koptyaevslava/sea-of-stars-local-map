from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import zipfile


VERSION = "0.9.3"
ROOT = Path(__file__).resolve().parent
WORKSPACE = ROOT.parent
SOURCE = WORKSPACE / "payload/LocalMap"
OUTPUT = WORKSPACE / f"dist/LocalMap-Installer-{VERSION}"
PACKAGE = OUTPUT / f"LocalMap-{VERSION}.lmpkg"
EXE = OUTPUT / "LocalMapInstaller.exe"
CSC = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def active_payload_files() -> list[Path]:
    selected: set[Path] = set()
    for name in ("SeaOfStarsLocalMap.dll", "maps-manifest.json", "README.txt"):
        path = SOURCE / name
        if not path.is_file():
            raise RuntimeError(f"Required payload file is missing: {path}")
        selected.add(path)

    ui = SOURCE / "UI"
    selected.update(path for path in ui.rglob("*") if path.is_file())

    map_folders = sorted(path for path in (SOURCE / "Maps").iterdir() if path.is_dir())
    if len(map_folders) != 97:
        raise RuntimeError("Local Map payload must contain exactly 97 map folders")
    for folder in map_folders:
        for name in ("map.json", "base.png", "base-hd.png", "image-hd.json", "tiles-5x.json"):
            path = folder / name
            if not path.is_file():
                raise RuntimeError(f"Required map file is missing: {path}")
            selected.add(path)
        manifest = json.loads((folder / "tiles-5x.json").read_text(encoding="utf-8"))
        if manifest.get("tileSize") != 512:
            raise RuntimeError(f"Map does not use 512px tiles: {folder.name}")
        for tile in manifest.get("tiles", []):
            path = (folder / tile["file"]).resolve()
            if folder.resolve() not in path.parents or not path.is_file():
                raise RuntimeError(f"Unsafe or missing map tile: {folder.name}/{tile['file']}")
            selected.add(path)

    result = sorted(selected, key=lambda path: path.relative_to(SOURCE).as_posix().casefold())
    forbidden = [path for path in result if "generousblock" in path.name.casefold() or "timing" in path.name.casefold()]
    if forbidden:
        raise RuntimeError(f"Other mod files found in Local Map payload: {forbidden}")
    return result


def build_package() -> tuple[int, int]:
    selected = active_payload_files()
    records: list[tuple[str, int, str]] = []
    total = 0
    for index, path in enumerate(selected, 1):
        relative = str(PurePosixPath(path.relative_to(SOURCE).as_posix()))
        size = path.stat().st_size
        records.append((relative, size, sha256(path)))
        total += size
        if index % 1000 == 0:
            print(f"Manifest {index}/{len(selected)}", flush=True)

    manifest = "# path\tbytes\tsha256\n" + "".join(
        f"{path}\t{size}\t{digest}\n" for path, size, digest in records
    )
    info = (
        "Format=1\n"
        "ModId=local.seaofstars.localmap\n"
        "Name=Local Map\n"
        f"Version={VERSION}\n"
        f"Files={len(records)}\n"
        f"Bytes={total}\n"
    )
    OUTPUT.mkdir(parents=True, exist_ok=True)
    temporary = PACKAGE.with_suffix(".lmpkg.tmp")
    temporary.unlink(missing_ok=True)
    with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_STORED, allowZip64=True) as archive:
        archive.writestr("installer-info.ini", info.encode("utf-8"))
        archive.writestr("installer-manifest.tsv", manifest.encode("utf-8"))
        for index, (relative, _, _) in enumerate(records, 1):
            archive.write(SOURCE / Path(relative), "payload/" + relative)
            if index % 1000 == 0:
                print(f"Package {index}/{len(records)}", flush=True)
    temporary.replace(PACKAGE)
    return len(records), total


def build_exe() -> None:
    if not CSC.is_file():
        raise RuntimeError(f".NET Framework compiler not found: {CSC}")
    command = [
        str(CSC), "/nologo", "/target:winexe", "/optimize+", "/platform:anycpu",
        f"/out:{EXE}", f"/win32manifest:{ROOT / 'app.manifest'}",
        "/reference:System.dll", "/reference:System.Core.dll", "/reference:System.Drawing.dll",
        "/reference:System.Windows.Forms.dll", "/reference:System.IO.Compression.dll",
        "/reference:System.IO.Compression.FileSystem.dll",
    ]
    icon = ROOT / "local-map.ico"
    if icon.is_file():
        command.append(f"/win32icon:{icon}")
    command.append(str(ROOT / "Installer.cs"))
    subprocess.run(command, check=True, cwd=WORKSPACE)


def write_readme(count: int, total: int) -> None:
    text = f"""Sea of Stars Local Map {VERSION}

1. Keep LocalMapInstaller.exe and LocalMap-{VERSION}.lmpkg in the same folder.
2. Run LocalMapInstaller.exe.
3. The installer detects a Steam installation or lets you select the folder that contains SeaOfStars.exe.
4. Select INSTALL.

The package contains only Local Map files: the plug-in DLL, UI assets, and 97 local maps.
It does not include BepInEx or any other mod. Install BepInEx 6 for IL2CPP before Local Map.

Installation uses a staging directory, verifies every SHA-256 checksum, and rolls back if the final swap fails.
REMOVE MOD deletes only BepInEx/plugins/LocalMap. Exploration fog and settings stored outside the plug-in folder are preserved.

Package files: {count}
Unpacked size: {total} bytes
"""
    (OUTPUT / "README.txt").write_text(text, encoding="utf-8")


def clean_output() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for path in OUTPUT.iterdir():
        if path.is_file():
            path.unlink()
        elif path.is_dir():
            shutil.rmtree(path)


def main() -> None:
    parser = argparse.ArgumentParser(description="Build the Local Map Windows installer and package.")
    parser.parse_args()
    clean_output()
    count, total = build_package()
    build_exe()
    write_readme(count, total)
    package_hash = sha256(PACKAGE)
    exe_hash = sha256(EXE)
    (OUTPUT / f"LocalMap-{VERSION}.lmpkg.sha256").write_text(
        f"{package_hash}  {PACKAGE.name}\n", encoding="ascii"
    )
    (OUTPUT / "LocalMapInstaller.exe.sha256").write_text(
        f"{exe_hash}  {EXE.name}\n", encoding="ascii"
    )
    print({
        "files": count,
        "bytes": total,
        "packageBytes": PACKAGE.stat().st_size,
        "packageSha256": package_hash,
        "exeBytes": EXE.stat().st_size,
        "exeSha256": exe_hash,
    }, flush=True)


if __name__ == "__main__":
    main()
