#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""为 CSLModernMap 的《城市：天际线 II》Mod 开发配置本机构建环境。

设计目标：**用户级、不需要管理员、可反复运行、可逆**。
不安装任何东西，只做"接线"——把已经存在的 .NET SDK 和游戏自带的官方
Modding Toolchain 用官方规定的环境变量串起来。

做四件事：
  1. 把 .NET SDK 根目录加入用户 PATH，并写 DOTNET_ROOT；
  2. 写入官方 Toolchain 要求的 CSII_* 用户环境变量；
  3. 建出 %CSII_TOOLPATH%，并把游戏自带的 Mod.props / Mod.targets 复制过去；
  4. 建出 %CSII_UNITYMODPROJECTPATH% 的目录骨架（含 PackageCache 占位）。

路径全部自动发现：
  - 游戏安装目录：读 Steam 的 libraryfolders.vdf，或环境变量 CSII_INSTALLATIONPATH；
  - .NET SDK 根：依次找 %LOCALAPPDATA%\\Microsoft\\dotnet、%ProgramFiles%\\dotnet、~/.dotnet、PATH；
  - com.unity.entities 版本：直接读游戏自带的 UnityModsProject.zip 里的 Packages/manifest.json，
    所以游戏更新后重跑本脚本即可自动跟上版本。

用法：
    python tools/cs2_setup_env.py               # 配置
    python tools/cs2_setup_env.py --dry-run     # 只报告，不写注册表
    python tools/cs2_setup_env.py --game "D:\\Other\\Path"
"""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import shutil
import sys
import zipfile
from pathlib import Path

IS_WINDOWS = os.name == "nt"

# --------------------------------------------------------------------------- #
# 发现
# --------------------------------------------------------------------------- #


def _steam_library_roots() -> list[Path]:
    """从 Steam 的 libraryfolders.vdf 里列出所有库根目录。"""
    roots: list[Path] = []
    candidates = [
        Path(r"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"),
        Path(r"C:\Program Files\Steam\steamapps\libraryfolders.vdf"),
        Path(r"D:\Steam\steamapps\libraryfolders.vdf"),
        Path(r"D:\SteamLibrary\steamapps\libraryfolders.vdf"),
        Path(r"E:\SteamLibrary\steamapps\libraryfolders.vdf"),
    ]
    for vdf in candidates:
        if not vdf.is_file():
            continue
        try:
            text = vdf.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        for m in re.finditer(r'"path"\s+"([^"]+)"', text):
            p = Path(m.group(1).replace("\\\\", "\\"))
            if p not in roots:
                roots.append(p)
    # 兜底：常见位置直接猜
    for guess in (Path(r"C:\Program Files (x86)\Steam"), Path(r"D:\SteamLibrary")):
        if guess not in roots:
            roots.append(guess)
    return roots


def _steam_manifest_names() -> dict[str, str]:
    """扫描各库，返回 {appid: 游戏显示名}，用于在库里认出 CS2。"""
    found: dict[str, str] = {}
    for root in _steam_library_roots():
        sa = root / "steamapps"
        if not sa.is_dir():
            continue
        for acf in sa.glob("appmanifest_*.acf"):
            try:
                text = acf.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            m_name = re.search(r'"name"\s+"([^"]+)"', text)
            appid = acf.stem.split("_", 1)[-1]
            if m_name:
                found[appid] = m_name.group(1)
    return found


def find_game(explicit: str | None) -> Path | None:
    """定位《城市：天际线 II》安装目录（其中必须存在 Cities2_Data/Managed/Game.dll）。"""
    tried: list[Path] = []

    def ok(p: Path) -> bool:
        return (p / "Cities2_Data" / "Managed" / "Game.dll").is_file()

    if explicit:
        p = Path(explicit)
        if ok(p):
            return p
        tried.append(p)

    env = os.environ.get("CSII_INSTALLATIONPATH")
    if env:
        p = Path(env)
        if ok(p):
            return p
        tried.append(p)

    # 已知 AppID：949230（Cities: Skylines II）
    for root in _steam_library_roots():
        common = root / "steamapps" / "common"
        if not common.is_dir():
            continue
        for child in common.iterdir():
            if child.is_dir() and ("Cities" in child.name or "Skylines" in child.name):
                if ok(child):
                    return child
                tried.append(child)

    # 再按 Steam 清单里的显示名精确找一遍
    for _appid, name in _steam_manifest_names().items():
        if "Cities" in name and name.rstrip().endswith("II"):
            for root in _steam_library_roots():
                p = root / "steamapps" / "common" / name
                if ok(p):
                    return p
                tried.append(p)

    # Xbox / Game Pass 版
    for p in (Path(r"C:\XboxGames\Cities- Skylines II - PC Edition")
              / "Content", Path(r"C:\XboxGames\Cities Skylines II - PC Edition")):
        if ok(p):
            return p
        tried.append(p)

    print("[x] 没找到 CS2 安装目录。试过：", file=sys.stderr)
    for p in tried:
        print("      ", p, file=sys.stderr)
    print("    用 --game 显式指定。", file=sys.stderr)
    return None


def find_dotnet_root() -> Path | None:
    """定位 .NET SDK 根目录（其中必须存在 dotnet.exe 和非空 sdk/）。"""
    cands: list[Path] = []
    la = os.environ.get("LOCALAPPDATA")
    if la:
        cands.append(Path(la) / "Microsoft" / "dotnet")
    pf = os.environ.get("ProgramFiles")
    if pf:
        cands.append(Path(pf) / "dotnet")
    up = os.environ.get("USERPROFILE")
    if up:
        cands.append(Path(up) / ".dotnet")
    # DOTNET_ROOT / PATH
    for key in ("DOTNET_ROOT", "DOTNET_ROOT_X64"):
        v = os.environ.get(key)
        if v:
            cands.append(Path(v))
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        if entry and "dotnet" in entry.lower():
            cands.append(Path(entry))

    seen: set[Path] = set()
    for c in cands:
        if c in seen:
            continue
        seen.add(c)
        if (c / "dotnet.exe").is_file() and (c / "sdk").is_dir():
            return c
    return None


def read_entities_version(game: Path) -> str | None:
    """从游戏自带的 UnityModsProject.zip 里读出 com.unity.entities 的版本。"""
    zpath = game / "Cities2_Data" / "Content" / "Game" / ".ModdingToolchain" / "UnityModsProject.zip"
    if not zpath.is_file():
        return None
    try:
        with zipfile.ZipFile(zpath) as z:
            # 该 zip 用反斜杠做分隔符，直接按 basename 找
            for name in z.namelist():
                if name.replace("\\", "/").endswith("Packages/manifest.json"):
                    data = json.loads(z.read(name).decode("utf-8-sig"))
                    return data.get("dependencies", {}).get("com.unity.entities")
    except (OSError, zipfile.BadZipFile, json.JSONDecodeError, UnicodeDecodeError):
        return None
    return None


def extract_unity_project(game: Path, unityproj: Path, dry: bool) -> list[str]:
    """把游戏自带的 UnityModsProject.zip 铺进 %CSII_UNITYMODPROJECTPATH%。

    这一步不是可选的：官方 ModPostProcessor 在 PathSet 构造时会把该目录当成
    一个**真实 Unity 工程**来校验，只建空目录会直接抛
    "Modding toolchain is incomplete, please reinstall it" 并以 -1 退出。

    坑：zip 内的路径分隔符是**反斜杠**，用 zipfile 自带的 extract 会被
    Windows 名字净化逻辑打平成 Assets_Empty.unity，所以必须手工把
    '\\' 归一成 '/' 再落盘。已存在的文件一律不覆盖（保住 PackageCache 与手工改动）。
    """
    zpath = (game / "Cities2_Data" / "Content" / "Game" / ".ModdingToolchain"
             / "UnityModsProject.zip")
    if not zpath.is_file():
        return []
    log: list[str] = []
    with zipfile.ZipFile(zpath) as z:
        for info in z.infolist():
            if info.is_dir():
                continue
            rel = info.filename.replace("\\", "/").lstrip("/")
            if not rel or ".." in rel.split("/"):
                continue
            dst = unityproj / rel
            if dst.exists():
                continue
            if dry:
                log.append(f"[dry] 铺 Unity 工程 {rel}")
                continue
            dst.parent.mkdir(parents=True, exist_ok=True)
            with z.open(info) as src, open(dst, "wb") as out:
                shutil.copyfileobj(src, out)
            log.append(f"[uproj] {rel}")
    return log


# --------------------------------------------------------------------------- #
# 注册表写入
# --------------------------------------------------------------------------- #


def _write_env(values: dict[str, str], path_entry: str | None, dry: bool) -> list[str]:
    """写用户级环境变量，并（可选）把 path_entry 加到用户 PATH 首位。

    返回人读的操作日志。
    """
    log: list[str] = []
    if dry:
        for k, v in values.items():
            log.append(f"[dry] 设 {k} = {v or '(空)'}")
        if path_entry:
            log.append(f"[dry] 把 {path_entry} 加入用户 PATH 首位")
        return log

    import winreg  # 仅 Windows

    key = winreg.OpenKey(
        winreg.HKEY_CURRENT_USER, "Environment", 0, winreg.KEY_READ | winreg.KEY_WRITE
    )
    try:
        for k, v in values.items():
            winreg.SetValueEx(key, k, 0, winreg.REG_SZ, v)
            log.append(f"[env] {k} = {v or '(空)'}")

        if path_entry:
            try:
                cur, typ = winreg.QueryValueEx(key, "Path")
            except FileNotFoundError:
                cur, typ = "", winreg.REG_EXPAND_SZ
            parts = [p for p in cur.split(";") if p]
            if path_entry in parts:
                log.append(f"[env] PATH 已含 {path_entry}（跳过）")
            else:
                parts.insert(0, path_entry)
                if typ not in (winreg.REG_SZ, winreg.REG_EXPAND_SZ):
                    typ = winreg.REG_EXPAND_SZ
                winreg.SetValueEx(key, "Path", 0, typ, ";".join(parts))
                log.append(f"[env] PATH 首位加入 {path_entry}")
    finally:
        winreg.CloseKey(key)
    return log


# --------------------------------------------------------------------------- #
# 主流程
# --------------------------------------------------------------------------- #


def main() -> int:
    ap = argparse.ArgumentParser(description="配置 CS2 Mod 构建环境（用户级）")
    ap.add_argument("--game", help="CS2 安装目录；不传则自动发现")
    ap.add_argument("--dotnet-root", help=".NET SDK 根目录；不传则自动发现")
    ap.add_argument("--dry-run", action="store_true", help="只报告，不改任何东西")
    args = ap.parse_args()

    if not IS_WINDOWS:
        print("[x] 本脚本只针对 Windows。", file=sys.stderr)
        return 2

    game = find_game(args.game)
    if game is None:
        return 1
    dotnet = Path(args.dotnet_root) if args.dotnet_root else find_dotnet_root()
    if dotnet is None or not (dotnet / "dotnet.exe").is_file():
        print("[x] 没找到 .NET SDK。装一个：winget install Microsoft.DotNet.SDK.8", file=sys.stderr)
        return 1

    sdk_dirs = sorted(p.name for p in (dotnet / "sdk").iterdir() if p.is_dir())
    tool = game / "Cities2_Data" / "Content" / "Game" / ".ModdingToolchain"
    managed = game / "Cities2_Data" / "Managed"
    mscorlib = managed / "mscorlib.dll"

    userdata = Path(os.environ["USERPROFILE"]) / "AppData" / "LocalLow" / "Colossal Order" / "Cities Skylines II"
    toolpath = userdata / ".cache" / "Modding"
    # 当前 CS2 版本的官方本地 Mod 根目录是 %CSII_USERDATAPATH%\Mods。
    # .cache\Mods\local 是旧版工具链遗留目录，部署脚本仍会清理它，
    # 但不应再作为新的构建输出目录，否则游戏可能看不到构建结果。
    localmods = userdata / "Mods"
    unityproj = toolpath / "UnityModsProject"
    entities = read_entities_version(game) or "1.3.10"

    print("=" * 68)
    print(" CS2 Mod 构建环境配置")
    print("=" * 68)
    print(f"  游戏安装目录   {game}")
    print(f"  Managed 程序集 {managed}")
    print(f"  mscorlib       {mscorlib}  {'OK' if mscorlib.is_file() else '<缺失>'}")
    print(f"  .NET SDK       {dotnet}  版本 {', '.join(sdk_dirs) or '(无)'}")
    print(f"  官方 Toolchain {tool}  {'OK' if tool.is_dir() else '<缺失>'}")
    print(f"  用户数据目录   {userdata}")
    print(f"  entities 版本  {entities}")
    print()

    if not tool.is_dir():
        print("[x] 官方 Toolchain 目录不存在，游戏版本可能不兼容。", file=sys.stderr)
        return 1
    if not mscorlib.is_file():
        print("[x] 找不到 mscorlib.dll，无法编译。", file=sys.stderr)
        return 1

    # ---- 1) 环境变量 ----
    env_values = {
        "DOTNET_ROOT": str(dotnet),
        # 官方 ModPostProcessor 是 net6.0 应用，而本机只有 .NET 8 运行时。
        # 允许"前滚到最新大版本"，否则构建后处理那步会报 "You must install .NET to run"。
        "DOTNET_ROLL_FORWARD": "LatestMajor",
        "CSII_INSTALLATIONPATH": str(game),
        "CSII_USERDATAPATH": str(userdata),
        "CSII_TOOLPATH": str(toolpath),
        "CSII_LOCALMODSPATH": str(localmods),
        "CSII_UNITYMODPROJECTPATH": str(unityproj),
        "CSII_ENTITIESVERSION": entities,
        # 注意：官方 Mod.targets 里这两项是直接当命令执行的（"$(...FullPath)" PostProcess ...），
        # 所以必须指到 .exe，指目录会得到 "不是内部或外部命令" 9009。
        "CSII_MODPOSTPROCESSORPATH": str(tool / "ModPostProcessor" / "ModPostProcessor.exe"),
        "CSII_MODPUBLISHERPATH": str(tool / "ModPublisher" / "ModPublisher.exe"),
        "CSII_MANAGEDPATH": str(managed),
        "CSII_MSCORLIBPATH": str(mscorlib),
        "CSII_ASSEMBLYSEARCHPATH": "",
        "CSII_PATHSET": "Build",
    }
    for line in _write_env(env_values, str(dotnet), args.dry_run):
        print(line)

    # ---- 2) 目录 + Mod.props / Mod.targets + Unity 工程实体 ----
    created: list[str] = []
    if not args.dry_run:
        for d in (toolpath, localmods, unityproj / "Assets", unityproj / "ProjectSettings",
                  unityproj / "Packages"):
            if not d.is_dir():
                d.mkdir(parents=True, exist_ok=True)
                created.append(str(d))
        for name in ("Mod.props", "Mod.targets"):
            src, dst = tool / name, toolpath / name
            if src.is_file():
                shutil.copy2(src, dst)
                created.append(f"{dst}  <- {src}")
    created.extend(extract_unity_project(game, unityproj, args.dry_run))
    for c in created:
        print(f"[dir] {c}" if not c.startswith("[") else c)
    if args.dry_run:
        print(f"[dry] 会建目录 {toolpath} / {localmods} / {unityproj}，复制 Mod.props + Mod.targets，"
              f"并铺开 UnityModsProject.zip")

    # ---- 3) 自检：官方后处理器的硬前提 ----
    echo_probe = [
        ("Mod.props", toolpath / "Mod.props"),
        ("Mod.targets", toolpath / "Mod.targets"),
        ("Unity 工程 manifest", unityproj / "Packages" / "manifest.json"),
        ("Unity 工程 ProjectVersion", unityproj / "ProjectSettings" / "ProjectVersion.txt"),
        ("entities 源码生成器",
         unityproj / "Library" / "PackageCache" / f"com.unity.entities@{entities}"
         / "Unity.Entities" / "SourceGenerators"),
    ]
    print()
    for label, p in echo_probe:
        print(f"[chk] {label:<24} {'OK' if p.exists() else '<缺失 —— 后处理器会报 toolchain incomplete>'}")
    print()

    print()
    print("完成。新开的终端里 `dotnet` 可直接用；已开的终端需要重启才认新 PATH。")
    print(f"Mod 会部署到： {localmods}\\<项目名>")
    return 0


if __name__ == "__main__":
    sys.exit(main())
