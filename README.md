# CSLModernMap Code Mod

[English](README.md) | [简体中文](README.zh-CN.md)

The Cities: Skylines and Cities: Skylines II code mods for CSLModernMap. Both export the current city as a compressed `.cmm.gz` map file.

This repository contains the in-game Code Mod source in `CS1/` and `CS2/`.
Renderer source and prebuilt payloads are not included. Official mod releases
include the companion viewer, which reads exported maps without accessing game saves.

## Features

- Exports roads, tracks, buildings, functional areas, public transport, terrain, water, and native traffic snapshots.
- Writes gzip-compressed UTF-8 JSON using the `cmm-v1` schema.
- Opens completed exports with the companion renderer included in official releases.
- Preserves native and custom names, area ownership, and facility relationships.
- Shows export and installation progress, with settings available in 12 languages.

## Build

CS1 requires Windows, .NET SDK 10 and local game/mod references;
see [CS1 build setup](CS1/README.md). CS2 requires Windows, .NET SDK 8+
and the official Cities: Skylines II modding toolchain.

```powershell
# Cities: Skylines
dotnet build CS1\CSLModernMap.csproj -c Release
# Cities: Skylines II
dotnet build CS2\CSLModernMapCs2.csproj -c Release
```

The CS1 build produces a verified `CS1/bin/private/CSLModernMap.dll`.
The CS2 build also deploys into the local game Mods directory; exit CS2 first.
Game/mod assemblies and viewer payloads must be supplied separately.
The mods do not download required files from the network.

## License

The Code Mod source is MIT-licensed. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), including the Carto notice shipped with the mod.

## Contributors

- Liuiuera
- OpenAI Codex

Some code and documentation were generated with AI assistance and reviewed by the project maintainer.
