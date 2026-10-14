# LayerChannelLive 一键卸载
#
#   从 Paint.NET 的 Effects 目录里移除本插件部署的文件。
#   只删除安装清单里记录的文件，不动其它插件。
#
# 参数：
#   -PdnRoot <路径>   指定 Paint.NET 安装目录（不指定则自动探测）
#   -KeepRunning      不自动关闭已运行的 Paint.NET
#   -Purge            连同清单文件、日志一起删除

[CmdletBinding()]
param(
    [string]$PdnRoot,
    [switch]$KeepRunning,
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$LogPath = Join-Path $env:TEMP 'LayerChannelLive-install.log'

function Write-Step([string]$m) { Write-Host $m -ForegroundColor Cyan }
function Write-Ok([string]$m) { Write-Host $m -ForegroundColor Green }
function Write-Warn2([string]$m) { Write-Host $m -ForegroundColor Yellow }
function Write-Err([string]$m) { Write-Host $m -ForegroundColor Red }
function Write-Log([string]$m) {
    try { Add-Content -Path $LogPath -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) -Encoding UTF8 } catch { }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-PdnRoot([string]$explicit) {
    if ($explicit -and (Test-Path (Join-Path $explicit 'paintdotnet.exe'))) {
        return (Resolve-Path $explicit).Path
    }

    $proc = Get-Process paintdotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.Path) {
        $dir = Split-Path $proc.Path -Parent
        if (Test-Path (Join-Path $dir 'Effects')) { return $dir }
    }

    $candidates = @()
    foreach ($key in @('HKLM:\SOFTWARE\Paint.NET', 'HKLM:\SOFTWARE\WOW6432Node\Paint.NET', 'HKCU:\SOFTWARE\Paint.NET')) {
        try {
            $v = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in @('InstallDirectory', 'InstallDir', 'Path')) {
                if ($v.PSObject.Properties.Name -contains $name -and $v.$name) { $candidates += $v.$name }
            }
        } catch { }
    }
    $candidates += @('C:\Program Files\paint.net', 'C:\Program Files (x86)\paint.net', "$env:LOCALAPPDATA\Paint.NET")
    foreach ($drive in @('D', 'E', 'F')) {
        $base = ($drive + ':\ruanjian')
        if (Test-Path $base) {
            $candidates += (Get-ChildItem $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'paint.net*' } | ForEach-Object { $_.FullName })
        }
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'paintdotnet.exe'))) { return (Resolve-Path $c).Path }
    }

    return $null
}

Write-Step "=== LayerChannelLive 卸载 ==="
Write-Log "uninstall started"

$root = Resolve-PdnRoot $PdnRoot
if (-not $root) {
    Write-Err "找不到 Paint.NET 安装目录，请用 -PdnRoot 指定。"
    Write-Log "FAILED: pdn root not found"
    exit 1
}

$effects = Join-Path $root 'Effects'
$manifestPath = Join-Path $effects 'LayerChannelLive.install.json'

$targets = @()
if (Test-Path $manifestPath) {
    try {
        $m = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($f in $m.files) { $targets += (Join-Path $effects $f) }
        Write-Ok "读到安装清单：$manifestPath"
    } catch {
        Write-Warn2 "清单读取失败，回退到默认文件名"
    }
}

if ($targets.Count -eq 0) {
    $targets = @(Join-Path $effects 'LayerChannelLive.dll')
}

# 权限探测
$needAdmin = $false
try {
    $probe = Join-Path $effects ('.lcv-write-probe-{0}' -f (Get-Random))
    New-Item -ItemType File -Path $probe -ErrorAction Stop | Out-Null
    Remove-Item $probe -Force
} catch { $needAdmin = $true }

if ($needAdmin -and -not (Test-Admin)) {
    Write-Warn2 "需要管理员权限，正在提权重启本脚本…"
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-PdnRoot', ('"' + $root + '"'))
    if ($KeepRunning) { $argList += '-KeepRunning' }
    if ($Purge) { $argList += '-Purge' }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
    } catch {
        Write-Err "提权被取消或失败：$($_.Exception.Message)"
        exit 1
    }
    exit 0
}

# 关闭运行中的实例以解除 DLL 占用
$running = Get-Process paintdotnet -ErrorAction SilentlyContinue
if ($running -and -not $KeepRunning) {
    Write-Warn2 "正在关闭 Paint.NET 以释放文件占用…"
    foreach ($p in $running) { try { $p.CloseMainWindow() | Out-Null } catch { } }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if (-not (Get-Process paintdotnet -ErrorAction SilentlyContinue)) { break }
    }
    Get-Process paintdotnet -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Warn2 "强制结束 pid $($_.Id)"
        try { $_.Kill(); $_.WaitForExit(5000) } catch { }
    }
    Start-Sleep -Milliseconds 600
}

$removed = 0
foreach ($t in $targets) {
    if (-not (Test-Path $t)) {
        Write-Host ("跳过（不存在）: {0}" -f (Split-Path $t -Leaf)) -ForegroundColor DarkGray
        continue
    }

    try {
        Remove-Item -Path $t -Force
        Write-Ok ("已删除 {0}" -f (Split-Path $t -Leaf))
        Write-Log ("removed " + $t)
        $removed++
    } catch {
        Write-Err ("删除失败 {0} ：{1}" -f (Split-Path $t -Leaf), $_.Exception.Message)
        Write-Log ("FAILED remove " + $t + " : " + $_.Exception.Message)
    }
}

if (Test-Path $manifestPath) {
    Remove-Item $manifestPath -Force -ErrorAction SilentlyContinue
    Write-Host "已删除清单文件" -ForegroundColor DarkGray
}

if ($Purge) {
    Remove-Item $LogPath -Force -ErrorAction SilentlyContinue
    Write-Host "已删除日志" -ForegroundColor DarkGray
}

Write-Host ""
if ($removed -gt 0) {
    Write-Ok "=== 卸载完成，共删除 $removed 个文件 ==="
} else {
    Write-Warn2 "=== 没有文件被删除（可能本来就没装） ==="
}
Write-Log ("uninstall OK, removed=" + $removed)
exit 0
