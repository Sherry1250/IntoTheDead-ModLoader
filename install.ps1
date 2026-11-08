<#
    Into the Dead: Our Darkest Days — BepInEx 安装脚本
    适用于 Unity 6000.5.11f1 / IL2CPP 元数据 v107
    用法：
        powershell -ExecutionPolicy Bypass -File .\install.ps1
        powershell -ExecutionPolicy Bypass -File .\install.ps1 -GamePath "D:\steam\steamapps\common\Into the Dead Our Darkest Days"
#>

param(
    [string]$GamePath = ""
)

$ErrorActionPreference = "Stop"
$AppId = "2239710"
$BepInExUrl = "https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip"
$Tmp = Join-Path $env:TEMP "itd_modloader"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    !   $msg" -ForegroundColor Yellow }

# ---------- 1. 定位游戏 ----------
Write-Step "定位游戏目录"

if (-not $GamePath) {
    $candidates = @()
    $libFile = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $libFile) {
        $raw = Get-Content -LiteralPath $libFile -Raw
        foreach ($m in [regex]::Matches($raw, '"path"\s+"([^"]+)"')) {
            $p = $m.Groups[1].Value -replace "\\\\", "\"
            $candidates += (Join-Path $p "steamapps\common\Into the Dead Our Darkest Days")
        }
    }
    $candidates += "D:\steam\steamapps\common\Into the Dead Our Darkest Days"
    $candidates += "C:\Program Files (x86)\Steam\steamapps\common\Into the Dead Our Darkest Days"
    $GamePath = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ "IntoTheDeadOurDarkestDays.exe") } | Select-Object -First 1
}

if (-not $GamePath -or -not (Test-Path -LiteralPath (Join-Path $GamePath "IntoTheDeadOurDarkestDays.exe"))) {
    Write-Host "找不到游戏目录，请手动指定：" -ForegroundColor Red
    Write-Host '    .\install.ps1 -GamePath "你的游戏根目录"' -ForegroundColor Gray
    exit 1
}
Write-Ok $GamePath

# ---------- 2. 检查是否已装 ----------
$winhttp = Join-Path $GamePath "winhttp.dll"
if ((Test-Path -LiteralPath $winhttp) -and (Test-Path -LiteralPath (Join-Path $GamePath "BepInEx\core"))) {
    Write-Warn2 "已存在 BepInEx，将覆盖 core 目录（plugins 与 config 不动）"
}

# ---------- 3. 下载 BepInEx ----------
Write-Step "下载 BepInEx 6.0.0-be.791 (IL2CPP)"

if (-not (Test-Path -LiteralPath $Tmp)) { New-Item -ItemType Directory -Path $Tmp -Force | Out-Null }
$zip = Join-Path $Tmp "bepinex.zip"

if (-not (Test-Path -LiteralPath $zip)) {
    try {
        Write-Host "    下载中，约 65MB ..." -ForegroundColor Gray
        Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
        Write-Ok "下载完成"
    } catch {
        Write-Host "下载失败：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host "请手动下载后解压到游戏根目录：" -ForegroundColor Gray
        Write-Host "    $BepInExUrl" -ForegroundColor Gray
        exit 1
    }
} else {
    Write-Ok "使用缓存 $zip"
}

Write-Step "解压到游戏目录"
Expand-Archive -LiteralPath $zip -DestinationPath $GamePath -Force
Write-Ok "BepInEx / dotnet / winhttp.dll 已就位"

# ---------- 4. 关键配置 ----------
Write-Step "写入启动必需的配置"

$cfgDir = Join-Path $GamePath "BepInEx\config"
if (-not (Test-Path -LiteralPath $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null }
$cfg = Join-Path $cfgDir "BepInEx.cfg"

if (-not (Test-Path -LiteralPath $cfg)) {
@"
[Logging]
UnityLogListening = false
"@ | Set-Content -LiteralPath $cfg -Encoding UTF8
} else {
    $raw = Get-Content -LiteralPath $cfg -Raw
    if ($raw -match "UnityLogListening\s*=") {
        $raw = $raw -replace "UnityLogListening\s*=\s*.*", "UnityLogListening = false"
    } else {
        $raw = $raw + "`r`n[Logging]`r`nUnityLogListening = false`r`n"
    }
    [IO.File]::WriteAllText($cfg, $raw)
}
Write-Ok "UnityLogListening = false"

# ---------- 5. MoonSweepers ----------
Write-Step "MoonSweepers ModEngine（需手动下载）"

$patchDir = Join-Path $GamePath "BepInEx\patchers\MoonSweepers.ModEngine"
if (Test-Path -LiteralPath (Join-Path $patchDir "MoonSweepers.ModEngine.dll")) {
    Write-Ok "已安装"
} else {
    Write-Host ""
    Write-Host "    这一步无法自动完成（Nexus 需要登录）。请：" -ForegroundColor Yellow
    Write-Host "    1) 打开 https://www.nexusmods.com/moonsweepers/mods/9" -ForegroundColor Gray
    Write-Host "    2) 下载 Moon Sweepers Patcher 1.0.2" -ForegroundColor Gray
    Write-Host "    3) 解压，把 MoonSweepers.ModEngine.dll 放到：" -ForegroundColor Gray
    Write-Host "       $patchDir" -ForegroundColor Gray
    Write-Host ""
    Write-Host "    没有它，游戏会报 Unsupported metadata version 107，框架起不来。" -ForegroundColor Yellow
    Read-Host "    放好后按回车继续（直接回车跳过也行）" | Out-Null
}

# ---------- 6. 启动 ----------
Write-Step "通过 Steam 启动游戏（首次运行会生成 interop 程序集）"

$steam = @("C:\Program Files (x86)\Steam\steam.exe", "D:\steam\steam.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($steam) {
    Start-Process -FilePath $steam -ArgumentList "-applaunch $AppId"
    Write-Ok "已启动，首次进入游戏约需 1 分钟"
} else {
    Write-Warn2 "找不到 steam.exe，请自己从 Steam 启动（不要双击 exe）"
}

Write-Host ""
Write-Host "安装完成。" -ForegroundColor Green
Write-Host "生成物：" -ForegroundColor Gray
Write-Host "    $GamePath\BepInEx\interop    （编译插件时引用）" -ForegroundColor Gray
Write-Host "    $GamePath\BepInEx\dummy      （反查类结构时用）" -ForegroundColor Gray
Write-Host "    $GamePath\BepInEx\plugins    （插件 DLL 放这里）" -ForegroundColor Gray
Write-Host "日志：BepInEx\LogOutput.log" -ForegroundColor Gray
