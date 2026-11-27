# CSLModernMap 代码模组

[English](README.md) | [简体中文](README.zh-CN.md)

CSLModernMap 的《城市：天际线》和《城市：天际线 II》代码模组，可将当前城市导出为带版本号的 `.cmm` 地图归档。

本仓库在 `CS1/`、`CS2/` 中分别存放两代游戏内模组源码，不包含查看器源码及预编译载荷。
正式模组发行包附带配套查看器；查看器只读取导出的地图文件，不直接访问游戏存档。

## 功能

- 导出道路、轨道、建筑、功能区、公共交通、地形、水域及原生交通统计快照。
- 将城市数据和可选的卫星图写入 CMM 归档。
- 使用正式版本附带的配套查看器打开导出结果。
- 保留游戏原生名称、自定义名称、区域归属与设施父子关系。
- 显示导出和安装进度，设置页支持 12 种语言。

## 构建

CS1 需要 Windows、.NET SDK 10 和本地游戏／模组引用，配置见 [CS1 构建说明](CS1/README.md)。
CS2 需要 Windows、.NET SDK 8+ 和《城市：天际线 II》官方模组工具链。

```powershell
# 城市：天际线
dotnet build CS1\CSLModernMap.csproj -c Release
# 城市：天际线 II
dotnet build CS2\CSLModernMapCs2.csproj -c Release
```

CS1 构建产物为已验证的 `CS1/bin/private/CSLModernMap.dll`。
CS2 构建还会部署到本地游戏 Mods 目录，运行前须退出 CS2。
游戏／模组程序集和查看器载荷需另行提供。Windows 发行包需要将签名的查看器 MSIX 放入模组目录。CS2 在构建前暂存，CS1 在组装发行目录时暂存。

    python tools/pack_cs2_renderer.py --msix path/to/viewer.msix
    python tools/pack_cs2_renderer.py --msix path/to/viewer.msix --target path/to/CS1-release

暂存命令同时写入应用身份。两个导出按钮都会先检查查看器。未检测到时，可以先安装，也可以继续导出。从安装窗口返回游戏并回答提示后，模组会再检查一次。如果仍未检测到查看器，地图会照常保存，导出结果会显示警告。「导出并打开」会在保存后启动已安装的查看器。「安装 / 更新查看器」可重复打开安装器，重装当前版本。模组不会从网络下载必需文件。

## 许可

CodeMod 源码使用 MIT 许可。请参阅 [LICENSE](LICENSE) 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，以及随模组附带的 Carto 许可。

## 贡献者

- Liuiuera
- OpenAI Codex

部分代码和文档由 AI 辅助生成，并经项目维护者审查。
