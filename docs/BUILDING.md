# Building

## Plug-in

Install BepInEx 6 for IL2CPP into Sea of Stars and run the game once so BepInEx generates its interop assemblies. Set `SEA_OF_STARS_DIR` to the game directory, then build:

```powershell
$env:SEA_OF_STARS_DIR = "D:\SteamLibrary\steamapps\common\Sea of Stars"
dotnet build src\SeaOfStarsLocalMap\LocalMap.csproj -c Release
```

You can also pass the path directly:

```powershell
dotnet build src\SeaOfStarsLocalMap\LocalMap.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Sea of Stars"
```

The build never copies files into the game directory.

## Tests

The managed projection, fog, persistence, and viewport tests do not require the game:

```powershell
dotnet run --project tests\StaticMap.Tests\StaticMap.Tests.csproj -c Release
```

## Map payload

Place the prepared runtime payload under `payload/LocalMap` with this layout:

```text
payload/LocalMap/
  SeaOfStarsLocalMap.dll
  maps-manifest.json
  README.txt
  UI/
  Maps/<level-guid>/
    map.json
    base.png
    base-hd.png
    image-hd.json
    tiles-5x.json
    tiles-5x-512/*.png
```

The installer builder reads only files required by the active manifests. Old tile directories and rollback manifests are excluded even if they exist in the local working payload.

## Installer

Run:

```powershell
python installer\build_installer.py
```

Output is written to `dist/LocalMap-Installer-0.9.3`. Keep the installer executable and `.lmpkg` file together. SHA-256 files are generated for both release artifacts.

The installer supports these command-line operations:

```text
LocalMapInstaller.exe --verify-package
LocalMapInstaller.exe --list-games
LocalMapInstaller.exe --install "<game-folder>"
LocalMapInstaller.exe --verify-installed "<game-folder>"
LocalMapInstaller.exe --uninstall "<game-folder>"
```
