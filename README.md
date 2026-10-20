# CSLModernMap Code Mod

[English](README.md) | [简体中文](README.zh-CN.md)

The Cities: Skylines II code mod for CSLModernMap. It exports the current city as a compressed `.cmm.gz` map file.

This repository includes the Code Mod source and the prebuilt renderer payload used in official releases. The renderer source is not included; the renderer only reads exported `.cmm.gz` files and does not directly access game save files.

## Features

- Exports roads, tracks, buildings, functional areas, public transport, terrain, water, and native traffic snapshots.
- Writes gzip-compressed UTF-8 JSON using the `cmm-v1` schema.
- Opens completed exports with the companion renderer included in official releases.
- Preserves native and custom names, area ownership, and facility relationships.
- Shows export and installation progress, with settings available in 12 languages.

## Build

Requires Windows, Cities: Skylines II with its official modding toolchain, and .NET SDK 8+.

```powershell
dotnet build dev\CSLModernMapModCs2\CSLModernMapCs2.csproj -c Release
```

This command builds and deploys into the local game Mods directory. Exit the game before building. The checked-in renderer payload is version 6.7.1; it is separate from the Code Mod source version. Renderer updates are supplied through official mod releases. The mod does not download required files from the network.

## License

The Code Mod source is MIT-licensed. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), including the Carto notice shipped with the mod.

## Contributors

- Liuiuera
- OpenAI Codex

Some code and documentation were generated with AI assistance and reviewed by the project maintainer.
