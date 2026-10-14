# LayerChannelLive 一键安装
#
#   把 LayerChannelLive.dll 部署到 Paint.NET 的 Effects 目录。
#   需要管理员权限（Program Files 之类的目录要提权）。
#
# 参数：
#   -PdnRoot <路径>   指定 Paint.NET 安装目录（不指定则自动探测）
#   -NoClose          不自动关闭已运行的 Paint.NET
#   -Quiet            少输出

[CmdletBinding()]
param(
    [string]$PdnRoot,
    [switch]$NoClose,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$PluginNames = @('LayerChannelLive.dll')
$LogPath = Join-Path $env:TEMP 'LayerChannelLive-install.log'

function Write-Step([string]$msg) { if (-not $Quiet) { Write-Host $msg -ForegroundColor Cyan } }
function Write-Ok([string]$msg) { Write-Host $msg -ForegroundColor Green }
function Write-Warn2([string]$msg) { Write-Host $msg -ForegroundColor Yellow }
function Write-Err([string]$msg) { Write-Host $msg -ForegroundColor Red }

function Write-Log([string]$msg) {
    try { Add-Content -Path $LogPath -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8 } catch { }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---- 定位 Paint.NET 安装目录 -------------------------------------------------
function Resolve-PdnRoot([string]$explicit) {
    if ($explicit) {
        if (Test-Path (Join-Path $explicit 'paintdotnet.exe')) { return (Resolve-Path $explicit).Path }
        throw "指定的目录里没有 paintdotnet.exe：$explicit"
    }

    # 1) 正在运行的进程
    $proc = Get-Process paintdotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.Path) {
        $dir = Split-Path $proc.Path -Parent
        if (Test-Path (Join-Path $dir 'Effects')) { return $dir }
    }

    # 2) 注册表（标准安装 / 商店版）
    $candidates = @()
    foreach ($key in @(
            'HKLM:\SOFTWARE\Paint.NET',
            'HKLM:\SOFTWARE\WOW6432Node\Paint.NET',
            'HKCU:\SOFTWARE\Paint.NET')) {
        try {
            $v = (Get-ItemProperty -Path $key -ErrorAction Stop)
            foreach ($name in @('InstallDirectory', 'InstallDir', 'Path')) {
                if ($v.PSObject.Properties.Name -contains $name -and $v.$name) { $candidates += $v.$name }
            }
        } catch { }
    }

    # 3) 常见安装位置
    $candidates += @(
        'C:\Program Files\paint.net',
        'C:\Program Files (x86)\paint.net',
        "$env:LOCALAPPDATA\Paint.NET",
        "$env:ProgramFiles\Paint.NET"
    )

    # 4) 用户本机的自定目录（含 D 盘）
    foreach ($drive in @('D', 'E', 'F')) {
        $base = ($drive + ':\ruanjian')
        if (Test-Path $base) {
            $candidates += (Get-ChildItem $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'paint.net*' } | ForEach-Object { $_.FullName })
        }
    }

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        if (Test-Path (Join-Path $c 'paintdotnet.exe')) { return (Resolve-Path $c).Path }
    }

    return $null
}

# ---- 已运行实例的强制加载 -----------------------------------------------------
#
# 为什么需要这一步（实测结论）：
#   Paint.NET 扫描 Effects 目录时**只读程序集元数据**来构建菜单，不会实例化里面的类型。
#   所以放在 Effects 里的 DLL 在你手动调用它之前，托管代码一行都不会执行。
#   要让面板立刻生效，就得让 CLR 把我们的程序集加载进 Paint.NET 进程。
#
#   做法：在目标进程里调用 kernel32!LoadLibraryW(程序集路径)。
#   Paint.NET 是自包含 .NET 9 宿主，CLR 已在运行，会走 shim 把托管程序集装进去，
#   从而执行模块初始化。若失败不报错，只提示用户需要手动握手一次。

function Add-InjectionHelper {
    if ('LCVInject' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LCVInject
{
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern IntPtr GetModuleHandleW(string n);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Ansi, ExactSpelling=true)]
    private static extern IntPtr GetProcAddress(IntPtr m, string n);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr s, uint t, uint prot);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr w);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr a, IntPtr s, IntPtr start, IntPtr param, uint f, out IntPtr tid);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetExitCodeThread(IntPtr h, out IntPtr code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr h);

    // kernel32!LoadLibraryW 是转发到 kernelbase 的，但 GetProcAddress 会跟随转发，
    // 这里从 kernel32 解析即可（实测返回有效地址）。
    public static IntPtr LoadLibraryAddress()
    {
        return GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
    }

    public static bool Load(int pid, string path, out string detail)
    {
        detail = "";
        IntPtr addr = LoadLibraryAddress();
        if (addr == IntPtr.Zero) { detail = "无法解析 LoadLibraryW"; return false; }

        IntPtr hProc = OpenProcess(0x1F0FFF, false, pid);
        if (hProc == IntPtr.Zero) { detail = "OpenProcess 失败 err=" + Marshal.GetLastWin32Error(); return false; }
        try
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)bytes.Length, 0x3000, 0x04);
            if (remote == IntPtr.Zero) { detail = "VirtualAllocEx 失败"; return false; }

            IntPtr written;
            if (!WriteProcessMemory(hProc, remote, bytes, (IntPtr)bytes.Length, out written))
            { detail = "WriteProcessMemory 失败"; return false; }

            IntPtr tid;
            IntPtr hT = CreateRemoteThread(hProc, IntPtr.Zero, IntPtr.Zero, addr, remote, 0, out tid);
            if (hT == IntPtr.Zero) { detail = "CreateRemoteThread 失败 err=" + Marshal.GetLastWin32Error(); return false; }
            try
            {
                WaitForSingleObject(hT, 20000);
                IntPtr code;
                GetExitCodeThread(hT, out code);
                detail = "module handle = 0x" + code.ToString("X");
                return code != IntPtr.Zero;
            }
            finally { CloseHandle(hT); }
        }
        finally { CloseHandle(hProc); }
    }
}
'@
}

function Invoke-ForceLoad([string[]]$dllPaths) {
    $proc = Get-Process paintdotnet -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { return $false }

    foreach ($dll in $dllPaths) {
        if (-not (Test-Path $dll)) { continue }
        try {
            Add-InjectionHelper
            $detail = ''
            $ok = [LCVInject]::Load($proc.Id, $dll, [ref]$detail)
            if ($ok) {
                Write-Ok ("已强制加载进 pid {0}：{1}  ({2})" -f $proc.Id, (Split-Path $dll -Leaf), $detail)
                Write-Log ("forced load OK pid=$($proc.Id) dll=$dll $detail")
                return $true
            } else {
                Write-Warn2 ("强制加载未成功：{0}  ({1})" -f (Split-Path $dll -Leaf), $detail)
                Write-Log ("forced load FAILED dll=$dll $detail")
            }
        } catch {
            Write-Warn2 "强制加载异常：$($_.Exception.Message)"
            Write-Log ("forced load EX " + $_.Exception.Message)
        }
    }
    return $false
}

# ---- 主流程 -----------------------------------------------------------------
Write-Step "=== LayerChannelLive 安装 ==="
Write-Log "install started"

$root = Resolve-PdnRoot $PdnRoot
if (-not $root) {
    Write-Err "找不到 Paint.NET 安装目录。请用 -PdnRoot 显式指定，例如："
    Write-Err '  .\install.ps1 -PdnRoot "D:\ruanjian\paint.net.5.0.13.install.x64"'
    Write-Log "FAILED: pdn root not found"
    exit 1
}

$effects = Join-Path $root 'Effects'
Write-Ok  "Paint.NET : $root"
Write-Ok  "Effects   : $effects"
Write-Log "pdn root = $root"

if (-not (Test-Path $effects)) {
    Write-Warn2 "Effects 目录不存在，尝试创建"
    New-Item -ItemType Directory -Force -Path $effects | Out-Null
}

# 源文件
$srcDir = $PSScriptRoot
$missing = @()
foreach ($n in $PluginNames) {
    if (-not (Test-Path (Join-Path $srcDir $n))) { $missing += $n }
}

if ($missing.Count -gt 0) {
    Write-Err "安装包缺少文件：$($missing -join ', ')"
    Write-Err "请先编译：dotnet build -c Release"
    Write-Err "然后把生成的 LayerChannelLive.dll 复制到本脚本所在目录。"
    Write-Log ("FAILED: missing " + ($missing -join ','))
    exit 1
}

# 权限检查：只在目标目录不可写时才要求提权
$needAdmin = $false
try {
    $probe = Join-Path $effects ('.lcv-write-probe-{0}' -f (Get-Random))
    New-Item -ItemType File -Path $probe -ErrorAction Stop | Out-Null
    Remove-Item $probe -Force
} catch {
    $needAdmin = $true
}

if ($needAdmin -and -not (Test-Admin)) {
    Write-Warn2 "Effects 目录需要管理员权限，正在提权重启本脚本…"
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-PdnRoot', ('"' + $root + '"'))
    if ($NoClose) { $argList += '-NoClose' }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
    } catch {
        Write-Err "提权被取消或失败：$($_.Exception.Message)"
        Write-Log "FAILED: elevation cancelled"
        exit 1
    }
    exit 0
}

# 正在运行的 Paint.NET 会锁住已加载的 DLL
$running = Get-Process paintdotnet -ErrorAction SilentlyContinue
if ($running) {
    if ($NoClose) {
        Write-Warn2 "Paint.NET 正在运行，且指定了 -NoClose。"
        # 预检：目标 DLL 若已被加载就会被锁住。直接点明，别等复制中途才失败。
        $locked = @()
        foreach ($n in $PluginNames) {
            $dstPath = Join-Path $effects $n
            if (-not (Test-Path $dstPath)) { continue }
            try {
                $fs = [System.IO.File]::Open($dstPath, 'Open', 'ReadWrite', 'None')
                $fs.Close()
            } catch {
                $locked += $n
            }
        }

        if ($locked.Count -gt 0) {
            Write-Err ("以下文件正被 Paint.NET 占用，无法替换：{0}" -f ($locked -join ', '))
            Write-Err "请先退出 Paint.NET 再重试，或者去掉 -NoClose 让脚本自动关闭它。"
            Write-Log ("FAILED: locked files " + ($locked -join ','))
            exit 1
        }
    } else {
        Write-Warn2 "检测到 Paint.NET 正在运行（pid $(($running | ForEach-Object { $_.Id }) -join ',')），正在关闭以便替换文件…"
        foreach ($p in $running) {
            try { $p.CloseMainWindow() | Out-Null } catch { }
        }
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
}

# 复制
$installed = @()
foreach ($n in $PluginNames) {
    $src = Join-Path $srcDir $n
    $dst = Join-Path $effects $n
    try {
        Copy-Item -Path $src -Destination $dst -Force
        $item = Get-Item $dst
        $installed += $item
        Write-Ok ("已部署 {0}  ({1} 字节, {2})" -f $n, $item.Length, $item.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
        Write-Log ("copied {0} -> {1}" -f $src, $dst)
    } catch {
        Write-Err "复制失败 $n ：$($_.Exception.Message)"
        Write-Log ("FAILED copy {0}: {1}" -f $n, $_.Exception.Message)
        exit 1
    }
}

# 记录清单，供卸载使用
$manifest = [pscustomobject]@{
    plugin     = 'LayerChannelLive'
    pdnRoot    = $root
    effectsDir = $effects
    files      = @($installed | ForEach-Object { $_.Name })
    installedAt = (Get-Date).ToString('o')
}
$manifestPath = Join-Path $effects 'LayerChannelLive.install.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Log ("manifest -> " + $manifestPath)

Write-Host ""
Write-Ok "=== 文件部署完成 ==="
Write-Host ""

# ---- 让它立刻生效 -----------------------------------------------------------
$wasRunning = [bool](Get-Process paintdotnet -ErrorAction SilentlyContinue)
$installedPaths = $installed | ForEach-Object { $_.FullName }

if ($wasRunning) {
    Write-Step "Paint.NET 正在运行，尝试直接加载新插件…"
    $loaded = Invoke-ForceLoad $installedPaths
    if ($loaded) {
        Write-Host ""
        Write-Ok "=== 安装完成，图层面板已生效 ==="
        Write-Host "  打开任意图片 → 菜单「窗口 → 图层」，图层列表就会变成 图层 + R/G/B/A 通道。" -ForegroundColor White
        Write-Host "  若窗口里还是旧列表，把图层窗口关掉再打开一次。" -ForegroundColor DarkGray
    } else {
        Write-Host ""
        Write-Warn2 "=== 文件已装好，但需要一次手动握手 ==="
        Write-Host "  Paint.NET 不会在启动时加载第三方插件 DLL，所以自动加载没成功。请手动做一次：" -ForegroundColor White
        Write-Host "    1. 菜单 效果 → 艺术风格 → 选一个分类，找到「Live Layer Channels」跑一次" -ForegroundColor White
        Write-Host "    2. 之后打开「窗口 → 图层」即可看到通道面板" -ForegroundColor White
    }
} else {
    Write-Step "启动 Paint.NET 以便立刻生效…"
    try {
        $exePath = Join-Path $root 'paintdotnet.exe'
        $p = Start-Process -FilePath $exePath -PassThru
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 800
            if ($p.HasExited) { break }
            $p.Refresh()
            if ($p.MainWindowHandle -ne 0) { break }
        }
        Start-Sleep -Seconds 3
        if (-not $p.HasExited) {
            $loaded = Invoke-ForceLoad $installedPaths
            if ($loaded) {
                Write-Host ""
                Write-Ok "=== 安装完成，图层面板已生效 ==="
                Write-Host "  打开或新建一张图片 → 菜单「窗口 → 图层」" -ForegroundColor White
            } else {
                Write-Host ""
                Write-Warn2 "=== 文件已装好，但需要一次手动握手 ==="
                Write-Host "  请手动：效果菜单 → 找到「Live Layer Channels」跑一次，然后打开「窗口 → 图层」" -ForegroundColor White
            }
        } else {
            Write-Warn2 "Paint.NET 启动后立刻退出了，跳过自动加载。"
        }
    } catch {
        Write-Warn2 "自动启动失败：$($_.Exception.Message)"
        Write-Host "  请手动启动 Paint.NET，然后打开「窗口 → 图层」。" -ForegroundColor White
    }
}

Write-Host ""
Write-Host "卸载：运行同目录下的 uninstall.bat" -ForegroundColor DarkGray
Write-Host "安装日志：$LogPath" -ForegroundColor DarkGray
Write-Log "install OK"
exit 0
