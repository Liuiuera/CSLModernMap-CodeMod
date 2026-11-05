# Json.NET for CS1

Unmodified upstream source, built as net35 using Json.NET's existing feature switches.

- Version: **13.0.4**
- Commit: `4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a`
- Archive: https://codeload.github.com/JamesNK/Newtonsoft.Json/zip/refs/tags/13.0.4
- SHA-256: `7cddbec9c4f2e9dc6d87b31ee3a68b9602a0c299e74bc4beae764263762dded5`
- License: MIT, retained in `LICENSE.md` and the archive.

`JsonNet.CS1.csproj` verifies the archive, extracts it into `obj`, and compiles
the original library sources. There is no build-time source download. The mod
references this project and merges its DLL with ILRepack 2.0.48.

The constants match upstream net35 except for these disabled features:

| Constant | Removed support | Reason |
| --- | --- | --- |
| HAVE_DATA_CONTRACTS | DataContract / DataMember / EnumMember attributes | JsonWriter enum initialization otherwise requires System.Runtime.Serialization |
| HAVE_ADO_NET | DataSet / DataTable converters | The default serializer otherwise requires System.Data even for ordinary DTOs |
| HAVE_ENTITY_FRAMEWORK | Entity Framework handling | Not used by map or traffic export |
| HAVE_XLINQ | LINQ-to-XML conversion | Avoid System.Xml.Linq, which is not required by the export contract |

DOM, JSON writer/reader, ordinary DTO serialization and Json.NET attributes
remain upstream implementations. No game or framework DLL is merged or shipped.
The final assembly checker rejects these optional framework dependencies.

To update: review a fixed upstream release/commit, replace the archive and hash,
compare its net35 constants, then build the mod and run its assembly verification before testing in game. Keep the license and package
verification versions in sync. Do not edit extracted `obj` sources.
