"""给 Unity 的 ILPP 后处理工具目录补写权限（一次性，需要一次 UAC 确认）。

背景
----
CS2 官方工具链在构建后会用 ModPostProcessor.exe 做 Unity IL 后处理（ILPP）。
ModPostProcessor 会把 Unity.ILPP.Runner.exe 作为子进程拉起，并**把它的工作目录
设成 runner 自己所在的目录**。runner 启动时要在工作目录下建 `Library/`：

    C:\\Program Files\\Unity 2022.3.62f2\\Editor\\Data\\Tools\\ilpp\\Unity.ILPP.Runner\\Library

如果 Unity 装在 `C:\\Program Files\\` 下，该目录默认 ACL 是 `BUILTIN\\Users:(RX)`
—— 只读不写。于是 runner 秒退，命名管道没人服务，ModPostProcessor 只能报：

    Failed to createILPP runner client
    HttpRequestException: Requesting HTTP version 2.0 ... unable to establish HTTP/2 connection

这个报错和 HTTP/2 毫无关系，纯粹是 runner 没起来。本脚本给该目录补一条可继承的
Modify 权限，之后构建即可正常走到部署。

用法
----
    python tools\\fix_unity_ilpp_permission.py

会弹一次 UAC。已修好时脚本直接返回，不弹窗（幂等）。
Unity 升级/重装到新版本目录后需要重跑一次。
"""

from __future__ import annotations

import ctypes
import os
import re
import subprocess
import sys
import winreg
from ctypes import wintypes
from pathlib import Path

SEE_MASK_NOCLOSEPROCESS = 0x00000040
SEE_MASK_NOASYNC = 0x00000100
SW_SHOWNORMAL = 1
INFINITE = 0xFFFFFFFF
ERROR_CANCELLED = 1223


class SHELLEXECUTEINFOW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("fMask", ctypes.c_ulong),
        ("hwnd", wintypes.HWND),
        ("lpVerb", wintypes.LPCWSTR),
        ("lpFile", wintypes.LPCWSTR),
        ("lpParameters", wintypes.LPCWSTR),
        ("lpDirectory", wintypes.LPCWSTR),
        ("nShow", ctypes.c_int),
        ("hInstApp", wintypes.HINSTANCE),
        ("lpIDList", ctypes.c_void_p),
        ("lpClass", wintypes.LPCWSTR),
        ("hkeyClass", wintypes.HKEY),
        ("dwHotKey", wintypes.DWORD),
        ("hIcon", wintypes.HANDLE),
        ("hProcess", wintypes.HANDLE),
    ]


def unity_version() -> str | None:
    """CSII_UNITYVERSION（用户环境变量，由官方工具链写入）。"""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
            value, _ = winreg.QueryValueEx(key, "CSII_UNITYVERSION")
            return str(value)
    except OSError:
        return None


def unity_root(version: str) -> Path | None:
    """从 HKLM 的 Unity 安装记录里取编辑器根目录（ModPostProcessor 用的就是这条路）。"""
    for hive, base in (
        (winreg.HKEY_LOCAL_MACHINE, rf"Software\Unity Technologies\Installer\Unity {version}"),
        (winreg.HKEY_LOCAL_MACHINE, rf"SOFTWARE\Unity Technologies\Installer\Unity {version}"),
    ):
        try:
            with winreg.OpenKey(hive, base) as key:
                for value_name in ("Location x64", "Location"):
                    try:
                        path, _ = winreg.QueryValueEx(key, value_name)
                    except OSError:
                        continue
                    if path:
                        return Path(path)
        except OSError:
            continue
    return None


def ilpp_runner_dir(version: str) -> Path | None:
    root = unity_root(version)
    if root is None:
        return None
    return root / "Editor" / "Data" / "Tools" / "ilpp" / "Unity.ILPP.Runner"


def can_write(directory: Path) -> bool:
    """真建一个目录再删掉，比读 ACL 可靠。"""
    probe = directory / "cslmm_write_probe"
    try:
        probe.mkdir(exist_ok=True)
    except OSError:
        return False
    try:
        probe.rmdir()
    except OSError:
        pass
    return True


def current_sid() -> str | None:
    try:
        out = subprocess.run(
            [str(Path(os.environ["SystemRoot"]) / "System32" / "whoami.exe"), "/user"],
            capture_output=True,
            check=False,
        ).stdout.decode("utf-8", errors="replace")
    except (OSError, KeyError):
        return None
    match = re.search(r"S-1-5-21(?:-\d+){3,}", out)
    return match.group(0) if match else None


def grant(directory: Path) -> tuple[int, str]:
    """在提权实例里执行：补一条可继承的 Modify 权限。返回 (退出码, 输出文本)。"""
    sid = current_sid()
    if sid is None:
        return 2, "[fix] 取不到当前用户 SID，无法授权。"
    # icacls 的 *SID 写法可以绕开本地化账户名的问题
    command = ["icacls", str(directory), "/grant", f"*{sid}:(OI)(CI)M", "/C"]
    result = subprocess.run(command, capture_output=True, check=False)
    text = [f"[fix] 执行：{' '.join(command)}"]
    text.append(result.stdout.decode("utf-8", errors="replace").strip())
    if result.stderr.strip():
        text.append(result.stderr.decode("gbk", errors="replace").strip())
    return result.returncode, "\n".join(t for t in text if t)


def log_path() -> Path:
    import tempfile

    return Path(tempfile.gettempdir()) / "cslmm_ilpp_permission.log"


def elevate(argv: list[str]) -> int:
    """用 UAC 重新拉起自己，并等待子进程结束。"""
    params = subprocess.list2cmdline(argv)
    info = SHELLEXECUTEINFOW()
    info.cbSize = ctypes.sizeof(info)
    info.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC
    info.lpVerb = "runas"
    info.lpFile = sys.executable
    info.lpParameters = params
    info.lpDirectory = str(Path.cwd())
    info.nShow = SW_SHOWNORMAL

    if not ctypes.windll.shell32.ShellExecuteExW(ctypes.byref(info)):
        error = ctypes.get_last_error()
        if error == ERROR_CANCELLED:
            print("[fix] 已取消 UAC 授权。")
            return 1
        print(f"[fix] 提权启动失败，GetLastError={error}")
        return 1

    ctypes.windll.kernel32.WaitForSingleObject(info.hProcess, INFINITE)
    code = wintypes.DWORD()
    ctypes.windll.kernel32.GetExitCodeProcess(info.hProcess, ctypes.byref(code))
    ctypes.windll.kernel32.CloseHandle(info.hProcess)
    return int(code.value)


def main() -> int:
    elevated = "--elevated" in sys.argv

    version = unity_version()
    if not version:
        print("[fix] 没有找到 CSII_UNITYVERSION。请先运行 tools\\cs2_setup_env.py。")
        return 2

    directory = ilpp_runner_dir(version)
    if directory is None:
        print(f"[fix] 注册表里没有 Unity {version} 的安装路径，无法定位 ILPP 工具目录。")
        print("      确认 Unity 已安装；若装在别处，请重跑一次 Unity Hub 安装。")
        return 2

    if not directory.is_dir():
        print(f"[fix] 目录不存在：{directory}")
        return 2

    if elevated:
        code, text = grant(directory)
        try:
            log_path().write_text(text, encoding="utf-8")
        except OSError:
            pass
        print(text)
        return 0 if code == 0 else code

    if can_write(directory):
        print(f"[fix] 已是可写状态，无需处理：{directory}")
        return 0

    print(f"[fix] ILPP 工具目录不可写：{directory}")
    print("[fix] 这与构建报的 “unable to establish HTTP/2 connection” 是同一件事——")
    print("[fix] runner 建不了自己的 Library 目录就直接退出，gRPC 自然握不上手。")
    print("[fix] 现在请求一次管理员授权来修复（会弹 UAC）…")

    marker = log_path()
    try:
        marker.unlink()
    except OSError:
        pass

    code = elevate([sys.executable, str(Path(__file__).resolve()), "--elevated"])
    try:
        print(marker.read_text(encoding="utf-8").strip())
    except OSError:
        pass

    if code != 0:
        print(f"[fix] 提权实例退出码 {code}。若上面是取消 UAC，请重跑本脚本再点“是”。")
        return code

    if can_write(directory):
        print(f"[fix] 修复成功：{directory} 现在可写。")
        print("[fix] 可以重新构建了（tools\\deploy_cs2_mod.cmd）。")
        return 0

    print("[fix] 授权已执行，但目录仍不可写。请手动检查 ACL：")
    print(f'      icacls "{directory}"')
    return 3


if __name__ == "__main__":
    raise SystemExit(main())
