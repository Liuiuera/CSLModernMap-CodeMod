[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "dev\CSLModernMapModCs2\CSLModernMapCs2.csproj"

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "找不到 CS2 Mod 工程：$projectPath"
}

if (Get-Process -Name "Cities2" -ErrorAction SilentlyContinue) {
    throw "《城市：天际线 II》仍在运行。请完全退出游戏后再部署，避免游戏继续持有旧 DLL。"
}

# 当前 PowerShell 可能早于 cs2_setup_env.py 启动，因此主动刷新用户级构建变量。
$environmentNames = @(
    "DOTNET_ROOT",
    "CSII_INSTALLATIONPATH",
    "CSII_USERDATAPATH",
    "CSII_TOOLPATH",
    "CSII_LOCALMODSPATH",
    "CSII_UNITYMODPROJECTPATH",
    "CSII_ENTITIESVERSION",
    "CSII_MODPOSTPROCESSORPATH",
    "CSII_MODPUBLISHERPATH",
    "CSII_MANAGEDPATH",
    "CSII_MSCORLIBPATH",
    "CSII_ASSEMBLYSEARCHPATH",
    "CSII_PATHSET",
    "CSII_UNITYVERSION"
)
foreach ($name in $environmentNames) {
    $value = [Environment]::GetEnvironmentVariable($name, "User")
    if ($null -ne $value) {
        Set-Item -LiteralPath "Env:$name" -Value $value
    }
}
$env:DOTNET_ROLL_FORWARD = "LatestMajor"

if (-not $env:CSII_USERDATAPATH) {
    $env:CSII_USERDATAPATH = Join-Path $env:USERPROFILE "AppData\LocalLow\Colossal Order\Cities Skylines II"
}
if (-not $env:DOTNET_ROOT) {
    throw "没有配置 DOTNET_ROOT。请先运行 tools\cs2_setup_env.py。"
}

$dotnetPath = Join-Path $env:DOTNET_ROOT "dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "找不到 .NET SDK：$dotnetPath"
}

# 预检：官方 ILPP 后处理的工具目录必须可写。
# ModPostProcessor 会把 Unity.ILPP.Runner.exe 的工作目录设成 runner 自己所在的目录，
# runner 启动时要在那里建 Library/。Unity 若装在 C:\Program Files 下，该目录默认只读，
# runner 会秒退，构建只报一句与 HTTP/2 有关的误导性错误，排查成本很高。
function Test-IlppRunnerWritable {
    param([string]$Version)

    if (-not $Version) {
        return $null
    }

    $installerKeys = @(
        "HKLM:\SOFTWARE\Unity Technologies\Installer\Unity $Version",
        "HKLM:\Software\Unity Technologies\Installer\Unity $Version"
    )

    foreach ($key in $installerKeys) {
        if (-not (Test-Path -LiteralPath $key)) {
            continue
        }
        $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($null -eq $item) {
            continue
        }
        foreach ($name in @("Location x64", "Location")) {
            if (@($item.PSObject.Properties.Name) -notcontains $name) {
                continue
            }
            $root = $item.$name
            if (-not $root) {
                continue
            }
            $runnerDir = Join-Path $root "Editor\Data\Tools\ilpp\Unity.ILPP.Runner"
            if (-not (Test-Path -LiteralPath $runnerDir -PathType Container)) {
                continue
            }
            $probe = Join-Path $runnerDir "cslmm_write_probe"
            try {
                New-Item -ItemType Directory -Path $probe -ErrorAction Stop | Out-Null
                Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
                return $true
            } catch {
                return $false
            }
        }
    }
    return $null
}

$ilppWritable = Test-IlppRunnerWritable -Version $env:CSII_UNITYVERSION
if ($ilppWritable -eq $false) {
    Write-Warning @"
Unity 的 ILPP 工具目录不可直接写入；当前工具链仍会尝试正常运行后处理。
如果本次构建随后出现 “unable to establish HTTP/2 connection”，再执行：

    python tools\fix_unity_ilpp_permission.py
"@
}
if ($ilppWritable -eq $null) {
    Write-Host "提示：未能定位 Unity 的 ILPP 工具目录，跳过写权限预检。" -ForegroundColor Yellow
}

$officialModsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:CSII_USERDATAPATH "Mods"))
$legacyModsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:CSII_USERDATAPATH ".cache\Mods\local"))
# 当前版本的官方工具链应直接把本地 Mod 输出到正式 Mods 目录。
# 即使当前 PowerShell 继承了旧版缓存路径，也在本次构建中强制纠正，
# 避免“构建成功但游戏里看不到 Mod”。
$env:CSII_LOCALMODSPATH = $officialModsRoot
$deployPath = [System.IO.Path]::GetFullPath((Join-Path $officialModsRoot "CSLModernMapCs2"))
$modRoots = @($officialModsRoot, $legacyModsRoot)

function Assert-DirectChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $candidatePath = [System.IO.Path]::GetFullPath($Candidate)
    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd("\")
    $parentPath = [System.IO.Path]::GetDirectoryName($candidatePath).TrimEnd("\")
    if (-not $parentPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理不在模组根目录中的路径：$candidatePath"
    }
}

function Get-CSLModernMapCopies {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $Root -Directory -Force | Where-Object {
            $_.Name -match '^CSLModernMapCs2(?:$|[._-])' -or
            (Test-Path -LiteralPath (Join-Path $_.FullName "CSLModernMapCs2.dll") -PathType Leaf)
        }
    )
}

Write-Host "[1/4] 清理旧的 CSLModernMap CS2 部署副本"
$removed = New-Object System.Collections.Generic.List[string]
foreach ($root in $modRoots) {
    foreach ($copy in @(Get-CSLModernMapCopies -Root $root)) {
        Assert-DirectChildPath -Candidate $copy.FullName -Root $root
        Remove-Item -LiteralPath $copy.FullName -Recurse -Force
        $removed.Add($copy.FullName)
        Write-Host "  已移除 $($copy.FullName)"
    }

    if (Test-Path -LiteralPath $root -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Force | Where-Object {
            $_.Name -match '^CSLModernMapCs2(?:[._-].*)?\.(dll|pdb|mjs|css|json)$'
        })) {
            Assert-DirectChildPath -Candidate $file.FullName -Root $root
            Remove-Item -LiteralPath $file.FullName -Force
            $removed.Add($file.FullName)
            Write-Host "  已移除 $($file.FullName)"
        }
    }
}
if ($removed.Count -eq 0) {
    Write-Host "  没有发现旧副本"
}

Write-Host "[2/4] 使用官方工具链构建并部署（$Configuration）"
& $dotnetPath build $projectPath --configuration $Configuration --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "CS2 Mod 构建失败，退出代码：$LASTEXITCODE"
}

Write-Host "[3/4] 验证唯一部署"
$activeCopies = New-Object System.Collections.Generic.List[string]
foreach ($root in $modRoots) {
    foreach ($copy in @(Get-CSLModernMapCopies -Root $root)) {
        $activeCopies.Add([System.IO.Path]::GetFullPath($copy.FullName))
    }
}

$deployedDll = Join-Path $deployPath "CSLModernMapCs2.dll"
if (-not (Test-Path -LiteralPath $deployedDll -PathType Leaf)) {
    throw "构建完成，但正式部署 DLL 不存在：$deployedDll"
}
if ($activeCopies.Count -ne 1 -or
    -not $activeCopies[0].Equals($deployPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "部署后仍存在多个副本：$($activeCopies -join '; ')"
}

# 主 DLL 在、原生库不在 = ILPP 后处理被跳过了。构建照样"成功"，游戏里却可能缺 Burst 产物，
# 所以这里单独提醒一句，而不是当成部署失败。
$burstDll = Join-Path $deployPath "CSLModernMapCs2_win_x86_64.dll"
if (-not (Test-Path -LiteralPath $burstDll -PathType Leaf)) {
    Write-Host "警告：没有看到 Burst 原生库（CSLModernMapCs2_win_x86_64.dll）。" -ForegroundColor Yellow
    Write-Host "      通常意味着 ILPP 后处理没有真正跑完；若随后游戏内导出异常，先查这一条。" -ForegroundColor Yellow
}

# 随包查看器载荷：Mod 侧按「程序集同目录」找它（RendererLauncher 的 ModDirectory）。
# 载荷没打出来时不影响导出本身，但游戏内「安装 / 更新查看器」会提示载荷缺失。
Write-Host "[4/4] 部署随包查看器载荷"
$payloadSource = Join-Path $repositoryRoot "dev\CSLModernMapModCs2"
$payloadNames = @(
    "CSLModernMapRenderer.cslmr",
    "CSLModernMapRenderer.cslmr.sha256",
    "renderer-version.txt"
)
$missingPayload = New-Object System.Collections.Generic.List[string]
foreach ($name in $payloadNames) {
    $source = Join-Path $payloadSource $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        $missingPayload.Add($name)
        continue
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $deployPath $name) -Force
    $sizeMb = (Get-Item -LiteralPath $source).Length / 1MB
    Write-Host ("  已复制 {0}（{1:N1} MB）" -f $name, $sizeMb)
}

if ($missingPayload.Count -gt 0) {
    Write-Host "警告：随包查看器载荷不完整，「安装 / 更新查看器」会提示载荷缺失。" -ForegroundColor Yellow
    Write-Host "      缺少：$($missingPayload -join ', ')" -ForegroundColor Yellow
    Write-Host "      先跑：python tools\pack_cs2_renderer.py" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "部署成功：$deployPath" -ForegroundColor Green
Write-Host "已确认正式 Mods 与旧缓存目录中只有这一份 CSLModernMapCs2。"
