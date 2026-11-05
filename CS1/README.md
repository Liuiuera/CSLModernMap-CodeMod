# CS1 build setup / 构建配置

Requires Windows and .NET SDK 10. The mod targets .NET Framework 3.5.
The assembly verification tool runs on .NET 10.

From the repository root, set the local reference paths and build:

```powershell
$env:CS1ManagedPath = 'D:\SteamLibrary\steamapps\common\Cities_Skylines\Cities_Data\Managed'
$env:TmpePath = 'D:\SteamLibrary\steamapps\workshop\content\255710\1637663252'
$env:ProceduralObjectsPath = 'D:\SteamLibrary\steamapps\workshop\content\255710\1094334744'
dotnet build CS1\CSLModernMap.csproj -c Release
```

将路径替换为本机游戏、TM:PE v11 STABLE 和 Procedural Objects 的安装目录。
这些程序集仅作编译引用，不在仓库中分发。

Json.NET source is pinned under `third_party/jsonnet/`. The build verifies its
archive, compiles the net35 profile, privately merges it and validates the result.
Use `bin/private/CSLModernMap.dll` together with its `.verified.json` report.

Viewer payloads are not included in the source repository. Official release
packages supply `CSLModernMapRenderer.cslmr`, its SHA-256 file and
`renderer-version.txt` separately.
