# CSLModernMap Code Mod

[English](README.md) | [简体中文](README.zh-CN.md)

The Cities: Skylines II code mod for CSLModernMap. It exports the current city as a compressed `.cmm.gz` map file.

This repository includes the Code Mod source and the prebuilt renderer payload used in official releases. The renderer source is not included; the renderer only reads exported `.cmm.gz` files and does not directly access game save files.

## Features

- Exports roads, tracks, buildings, public transport, terrain, and water.
- Writes gzip-compressed UTF-8 JSON using the `cmm-v1` schema.
- Opens completed exports with the companion renderer included in official releases.

## Build

Requires Windows, Cities: Skylines II with its official modding toolchain, .NET SDK 8+, and Node.js 18+.

```powershell
dotnet build dev\CSLModernMapModCs2\CSLModernMapCs2.csproj -c Release
```

The mod does not download required files from the network.

## Contributors

- Liuiuera
- OpenAI Codex

Some code and documentation were generated with AI assistance and reviewed by the project maintainer.
