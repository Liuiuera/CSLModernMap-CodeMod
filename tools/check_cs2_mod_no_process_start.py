#!/usr/bin/env python3
"""CS2 模组合规护栏：不出网取必需文件。

判据（2026-09-17 重裁）
--------------------
要守的不变量**不是**「Mod 里没有 `Process.Start`」，而是：

    必需文件不得从网络获取 —— 所有需要的文件都必须躺在 Paradox Mod 包里。

这句话是 Paradox 给 CS2MapView 的原话（*"Any required files to be downloaded
should be included in the mod uploaded to Paradox Mods."*），也是它被 ban 的原因。
换成这个判据以后：

允许
    在 `Systems/RendererLauncher.cs`（**唯一**进程出口）里启动
      · 系统文件管理器：`Process.Start("explorer", dir)` / `open` / `xdg-open`（字面量）
      · 随包下发、已经解压到本机磁盘的查看器（路径来自自己算出来的安装目录）
    —— 判据是「文件从哪来」，不是「谁按的按钮」。

禁止
    · 其它任何文件里出现 `Process.Start` / `ProcessStartInfo` / `UseShellExecute`
    · 任何出网取二进制的动作：`WebClient` / `HttpClient` / `DownloadFile` /
      `DownloadString` / `UnityWebRequest` / `WebRequest` / `Assembly.LoadFrom`
    · 任何 `http://` / `https://` 字面量（启动 URL、下载 URL 一并封死）
    · 引用渲染器可执行文件名以外的 `.exe`（可执行文件只有一个来处：随包载荷）
    · 启动目标的字面量不是文件管理器，或（针对 URL）不符合上面的白名单

用法
----
    python tools/check_cs2_mod_no_process_start.py            # 检查，退出码 0=通过
    python tools/check_cs2_mod_no_process_start.py -v         # 顺带打印扫了哪些文件
    python tools/check_cs2_mod_no_process_start.py --self-test   # 用两条反向探针自检护栏本身
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MOD_ROOT = REPO_ROOT / "dev" / "CSLModernMapModCs2"

# 唯一允许出现进程启动调用的文件（相对 MOD_ROOT）
LAUNCH_EXIT = Path("Systems") / "RendererLauncher.cs"

# 允许被启动的**字面量**程序：只能是系统文件管理器本身
ALLOWED_SHELL_LAUNCH = ("explorer", "open", "xdg-open")

# 随包查看器的入口文件名：全工程只允许在 LAUNCH_EXIT 里出现这一个 .exe
RENDERER_EXE = "CSLModernMapRenderer.exe"

# 全局禁止出现的标识：命中即「在出网取二进制 / 动态加载来路不明的程序集」
FORBIDDEN_TOKENS = (
    "WebClient",
    "HttpClient",
    "DownloadFile",
    "DownloadString",
    "UnityWebRequest",
    "WebRequest",
    "Assembly.LoadFrom",
    "http://",
    "https://",
)

# 只允许出现在 LAUNCH_EXIT 的调用与属性
PROCESS_ONLY_TOKENS = ("Process.Start", "ProcessStartInfo", "UseShellExecute")

PROCESS_START_RE = re.compile(r"Process\s*\.\s*Start\s*\(")
LAUNCH_LITERAL_RE = re.compile(
    r"Process\s*\.\s*Start\s*\(\s*\"(?P<exe>[^\"]*)\""
)
EXE_SUFFIX_RE = re.compile(r"\.[Ee][Xx][Ee]\b")

# 反向探针（必须被抓）与正向样例（不该被抓）：护栏本身的自检。
SELF_TEST_CASES = (
    (True, Path("Systems/BadProbe.cs"),
     'Process.Start("https://example.invalid/payload.exe");',
     "探针 A：启动一个 URL"),
    (True, Path("Systems/BadProbe2.cs"),
     'using (var client = new WebClient()) { client.DownloadFile(url, target); }',
     "探针 B：出网下载文件"),
    (True, Path("Systems/ShellFolder.cs"),
     'Process.Start("explorer", directory);',
     "反向用例：进程出口搬家后，旧出口也必须被抓"),
    (False, Path("Systems/ShellFolder.cs"),
     'GUIUtility.systemCopyBuffer = text;',
     "正向样例：剪贴板不涉及进程启动"),
    (False, LAUNCH_EXIT,
     'Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = false });',
     "正向样例：启动本机已解压的查看器"),
    (False, LAUNCH_EXIT,
     'Process.Start("explorer", directory);',
     "正向样例：用系统文件管理器打开导出目录"),
)


def iter_sources() -> list[Path]:
    """CS2 模组里所有需要检查的 .cs（跳过构建产物）。"""
    if not MOD_ROOT.is_dir():
        raise SystemExit(f"[FAIL] 找不到模组目录: {MOD_ROOT}")

    skip_dirs = {"obj", "bin", "Library", ".vs"}
    files = []
    for path in sorted(MOD_ROOT.rglob("*.cs")):
        if any(part in skip_dirs for part in path.parts):
            continue
        files.append(path)
    return files


def check_text(rel: Path, text: str) -> list[str]:
    """对一段源码文本跑全部规则。rel 决定豁免（只有 LAUNCH_EXIT 享受豁免）。"""
    problems: list[str] = []
    is_launch_exit = rel == LAUNCH_EXIT

    for lineno, line in enumerate(text.splitlines(), start=1):
        if line.lstrip().startswith("//"):
            continue  # 注释里提到这些名字（比如本文件顶部的说明）不算违规

        for token in FORBIDDEN_TOKENS:
            if token in line:
                problems.append(
                    f"{rel}:{lineno}: 出现禁用标识 {token!r}"
                    f"（必需文件必须随包下发，不得出网获取）—— {line.strip()[:90]}"
                )

        if PROCESS_START_RE.search(line):
            if not is_launch_exit:
                problems.append(
                    f"{rel}:{lineno}: 只有 {LAUNCH_EXIT} 允许启动进程 —— {line.strip()[:90]}"
                )
                continue

            match = LAUNCH_LITERAL_RE.search(line)
            if match and match.group("exe").strip() not in ALLOWED_SHELL_LAUNCH:
                problems.append(
                    f"{rel}:{lineno}: 不允许启动字面量程序 "
                    f"{match.group('exe').strip()!r}；字面量只许是 "
                    f"{' / '.join(ALLOWED_SHELL_LAUNCH)}（其它启动目标必须来自变量，"
                    f"且是本地路径）"
                )
        elif not is_launch_exit:
            for token in PROCESS_ONLY_TOKENS:
                if token in line:
                    problems.append(
                        f"{rel}:{lineno}: {token} 只允许出现在 {LAUNCH_EXIT} —— "
                        f"{line.strip()[:90]}"
                    )

        if EXE_SUFFIX_RE.search(line) and not is_launch_exit:
            problems.append(
                f"{rel}:{lineno}: 出现可执行文件引用 .exe —— 唯一来处是随包载荷，"
                f"只允许 {LAUNCH_EXIT} 提到它 —— {line.strip()[:90]}"
            )

    return problems


def check_file(path: Path) -> list[str]:
    rel = path.relative_to(MOD_ROOT)
    try:
        text = path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as exc:
        return [f"{rel}: 不是 UTF-8 文本，无法检查（{exc}）"]
    return check_text(rel, text)


def self_test() -> int:
    """跑内置探针：两条反向用例必须被抓，正向样例不该被抓。"""
    failures: list[str] = []
    for should_fail, rel, source, label in SELF_TEST_CASES:
        caught = bool(check_text(rel, source))
        if caught != should_fail:
            failures.append(
                f"{label}：期望{'被抓' if should_fail else '放行'}，实际"
                f"{'被抓' if caught else '放行'}"
            )

    if failures:
        print("[FAIL] 护栏自检不通过 —— 规则写坏了，别拿它去判别人的代码：")
        for item in failures:
            print("  -", item)
        return 1

    probes = sum(1 for case in SELF_TEST_CASES if case[0])
    print(f"[OK] 护栏自检通过：{probes} 条反向探针全部被抓，"
          f"{len(SELF_TEST_CASES) - probes} 条正向样例全部放行")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="CS2 模组合规检查：必需文件随包下发，不出网获取")
    parser.add_argument("-v", "--verbose", action="store_true", help="打印检查过的文件")
    parser.add_argument("--self-test", action="store_true",
                        help="用内置反向探针与正向样例自检护栏本身")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    files = iter_sources()

    # 护栏自身的完整性：唯一出口必须存在，否则「只有一个出口」这条规则失效
    launch_exit = MOD_ROOT / LAUNCH_EXIT
    if not launch_exit.is_file():
        print(f"[FAIL] 约定的唯一进程出口不存在: {LAUNCH_EXIT}")
        return 1

    problems: list[str] = []
    for path in files:
        problems.extend(check_file(path))

    # 正查 1：出口文件里应该**确实**在启动系统文件管理器（否则说明「打开导出目录」被改坏了）
    launch_text = launch_exit.read_text(encoding="utf-8-sig")
    shell_launches = [
        m.group("exe").strip() for m in LAUNCH_LITERAL_RE.finditer(launch_text)
    ]
    if not shell_launches:
        problems.append(
            f"{LAUNCH_EXIT}: 找不到任何 Process.Start(\"explorer\", …) —— "
            "“打开导出目录”按钮的实现不见了？"
        )
    for exe in shell_launches:
        if exe not in ALLOWED_SHELL_LAUNCH:
            problems.append(f"{LAUNCH_EXIT}: 不允许启动字面量程序 {exe!r}")

    # 正查 2：启动查看器必须走「不开 shell + 本地路径」这条路
    if "UseShellExecute" not in launch_text or "= false" not in launch_text:
        problems.append(
            f"{LAUNCH_EXIT}: 找不到 UseShellExecute = false —— "
            "启动查看器必须直接执行本地程序，不经过系统关联"
        )

    # 正查 3：启动的可执行文件名只能是随包载荷里的那一个
    if RENDERER_EXE not in launch_text:
        problems.append(
            f"{LAUNCH_EXIT}: 找不到 {RENDERER_EXE} —— "
            "启动目标必须是随包下发的查看器，不是其它来路的程序"
        )

    if args.verbose:
        print(f"检查了 {len(files)} 个源文件（跳过 obj/bin/Library）")
        for path in files:
            print("  ·", path.relative_to(MOD_ROOT))

    if problems:
        print(f"[FAIL] 发现 {len(problems)} 处越界：\n")
        for item in problems:
            print("  -", item)
        print(
            "\n规则：必需文件必须随 Mod 包下发。允许启动本机已存在的文件管理器与"
            "随包解压出来的查看器；禁止出网下载、禁止启动 URL、"
            "禁止引用随包载荷以外的可执行文件。"
        )
        return 1

    print(f"[OK] {len(files)} 个源文件：无出网下载、无 URL、无越界的进程启动")
    print(f"     唯一进程出口 {LAUNCH_EXIT}；"
          f"文件管理器出口 {', '.join(sorted(set(shell_launches)))}；"
          f"查看器 {RENDERER_EXE}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
