# Jinra Shader Sync

Jinra Shader Sync adds shared Vulkan pipeline entries to the transferable shader cache used by [Jinra](https://github.com/JinWooPT/Jinra). It supports cache format v54 on Windows x64.

[Download Jinra Shader Sync](https://github.com/JinWooPT/jinra-shader-sync/releases/latest) · [Browse the shader catalog](https://github.com/JinWooPT/jinra-shader-sync/releases/tag/shader-catalog-v54)

## Using the app

1. Open `Jinra Shader Sync.exe` and select your Jinra data folder and game.
2. Click **Check online** to see how many entries are missing and how much data the app needs to download.
3. Close Jinra and click **Sync cache**. The app verifies each download and backs up the previous cache beside it as `vulkan.fork-v54.bin.jinra-sync.bak`.

The Ocarina of Time pack is a 246 MB `.7z` archive containing 76,923 unique entries. Syncing it uses Windows `tar.exe` and can require about 8 GB of temporary free space while the cache is being rebuilt. Jinra can still compile additional pipelines during play.

## Building

Run `./build.ps1` in PowerShell on Windows x64 with .NET Framework 4.x. The executable is written to `bin/` by default.

## Catalog maintenance

The app reads `<TITLEID>.json` from the `shader-catalog-v54` release. Each manifest lists the title ID, cache version, asset names, release tags, sizes, SHA-256 digests and pipeline IDs. Asset filenames must match the names reported by GitHub.

For a `.7z` pack, extract its `vulkan.fork-v54.bin` to a temporary folder. Compile the manifest generator from `tools/PackManifest.cs`, `ShaderCache.cs` and `ShaderSync.cs` as a console app with references to `System.Net.Http.dll` and `System.Runtime.Serialization.dll`. Run it with the uppercase title ID, extracted cache path, archive path, GitHub asset filename and destination JSON path. Upload the archive and the resulting manifest as release assets.

The source code is available under GPL-3.0-or-later. The catalog links to separately packaged community cache data.

