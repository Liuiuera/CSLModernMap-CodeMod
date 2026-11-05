# CSLModernMap 代码模组

[English](README.md) | [简体中文](README.zh-CN.md)

CSLModernMap 的《城市：天际线》和《城市：天际线 II》代码模组，可将当前城市导出为压缩的 `.cmm.gz` 地图文件。

本仓库在 `CS1/`、`CS2/` 中分别存放两代游戏内模组源码，不包含查看器源码及预编译载荷。
正式模组发行包附带配套查看器；查看器只读取导出的地图文件，不直接访问游戏存档。

## 功能

- 导出道路、轨道、建筑、功能区、公共交通、地形、水域及原生交通统计快照。
- 使用 `cmm-v1` 格式写出 gzip 压缩的 UTF-8 JSON。
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
游戏／模组程序集和查看器载荷需另行提供。模组不会从网络下载必需文件。

## 许可

CodeMod 源码使用 MIT 许可。请参阅 [LICENSE](LICENSE) 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，以及随模组附带的 Carto 许可。

## 贡献者

- Liuiuera
- OpenAI Codex

部分代码和文档由 AI 辅助生成，并经项目维护者审查。
