# CSLModernMap 代码模组

[English](README.md) | [简体中文](README.zh-CN.md)

CSLModernMap 的《城市：天际线 II》代码模组，可将当前城市导出为压缩的 `.cmm.gz` 地图文件。

本仓库包含游戏内代码模组源码和正式版本使用的预编译渲染器载荷。配套查看器与渲染器的源码在 [CSLModernMap](https://github.com/Liuiuera/CSLModernMap) 仓库维护，用于读取导出的城市数据、显示地图，并导出图片和 HTML 网页地图。渲染器只读取导出的 `.cmm.gz` 文件，不直接访问游戏存档。

## 功能

- 导出道路、轨道、建筑、功能区、公共交通、地形、水域及原生交通统计快照。
- 使用 `cmm-v1` 格式写出 gzip 压缩的 UTF-8 JSON。
- 使用正式版本附带的配套查看器打开导出结果。
- 保留游戏原生名称、自定义名称、区域归属与设施父子关系。
- 显示导出和安装进度，设置页支持 12 种语言。

## 构建

需要 Windows、《城市：天际线 II》及其官方模组工具链和 .NET SDK 8+。

```powershell
dotnet build dev\CSLModernMapModCs2\CSLModernMapCs2.csproj -c Release
```

此命令编译并部署到游戏的本地 Mods 目录，构建前请先退出游戏。仓库现有查看器载荷为 6.7.1，与 CodeMod 源码版本独立；查看器更新通过正式模组版本提供。模组不会从网络下载必需文件。

## 许可

CodeMod 源码使用 MIT 许可。请参阅 [LICENSE](LICENSE) 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，以及随模组附带的 Carto 许可。

## 贡献者

- Liuiuera
- OpenAI Codex

部分代码和文档由 AI 辅助生成，并经项目维护者审查。
