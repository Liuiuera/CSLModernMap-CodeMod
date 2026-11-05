# Third-party notices

## Carto

The CSLModernMap code mod's network geometry and related export implementation were informed by the open-source Carto mod for Cities: Skylines II.

- Source: https://github.com/taipei-native/Carto
- Referenced revision: `8ab791a6fa1c4c45f887c66fcb576056228017b8`
- Copyright (c) 2025 Chang-Yu Ho
- License: MIT; see `CS2/LICENSES/Carto-LICENSE.txt`.

No Carto source files or binaries are bundled in this source repository.

## AlgernonCommons (CS1)

- Source: https://github.com/algernon-A/AlgernonCommons
- Vendored revision: `3197277312ac7a865ae8453691605a75297ed204`.
- Copyright (c) 2022-24 algernon (K. Algernon A. Sheppard).
- License: MIT; see `CS1/AlgernonCommons/LICENSE.txt`.

The vendored shared project is compiled into the CS1 mod.

## Clipper 6.4.2 (CS1)

- Source: https://sourceforge.net/projects/polyclipping/
- C# mirror: https://github.com/Geri-Borbas/Clipper
- Pinned revision: `d59f4d02e0d28fd7ee88d8b7dc93ff5f0806b7b2`.
- Copyright (c) 2010-2017 Angus Johnson.
- License: Boost Software License 1.0; see `CS1/Geometry/Clipper/LICENSE.txt`.

Clipper source is compiled into the CS1 mod.

## Json.NET 13.0.4 (CS1)

- Source: https://github.com/JamesNK/Newtonsoft.Json
- Pinned revision: `4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a`.
- Copyright James Newton-King.
- License: MIT; see `CS1/third_party/jsonnet/LICENSE.md` and `CS1/LICENSES/Newtonsoft.Json-MIT.txt`.

The pinned source archive is included. The net35 build uses upstream feature
switches and is privately merged into the CS1 mod. See the bundled project README.
ILRepack 2.0.48 (Apache-2.0) is restored from NuGet as a build tool only.

## External game and mod references

Game, Colossal Order, Unity, TM:PE and Procedural Objects assemblies are local
compile-time references and are not included in this repository. Their licenses
are not replaced by this repository's MIT License.

## Companion renderer

The companion renderer source and binary payloads are not included in this
repository. The root MIT License applies to original Code Mod source only.
Official mod releases supply the viewer separately with its own license notices.
