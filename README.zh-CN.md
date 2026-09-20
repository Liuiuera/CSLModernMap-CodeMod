# CSLModernMap 代码模组

[English](README.md) | [简体中文](README.zh-CN.md)

CSLModernMap 的《城市：天际线 II》代码模组，可将当前城市导出为压缩的 `.cmm.gz` 地图文件。

## 功能

- 导出道路、轨道、建筑、公共交通、地形与水域。
- 使用 `cmm-v1` 格式写出 gzip 压缩的 UTF-8 JSON。
- 使用正式版本附带的配套查看器打开导出结果。

## 构建

需要 Windows、《城市：天际线 II》及其官方模组工具链、.NET SDK 8+ 和 Node.js 18+。

```powershell
dotnet build dev\CSLModernMapModCs2\CSLModernMapCs2.csproj -c Release
```

模组不会从网络下载必需文件。

## 贡献者

- Liuiuera
- OpenAI Codex

部分代码和文档由 AI 辅助生成，并经项目维护者审查。
