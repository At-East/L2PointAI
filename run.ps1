# 一键构建并启动 L2PointAI（不依赖 VS 界面、不依赖快捷键）
#   .\run.ps1              编译 Debug 并启动
#   .\run.ps1 -Release     编译 Release 并启动
#   .\run.ps1 -NoBuild     跳过编译直接启动
#   .\run.ps1 -NoLaunch    只编译
#   .\run.ps1 -NoSharedCompilation  受限环境下使用
param(
    [switch]$Release,
    [switch]$NoBuild,
    [switch]$NoLaunch,
    [switch]$NoSharedCompilation
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sln = Join-Path $root 'L2PointAI.sln'
$cfg = 'Debug'
if ($Release) { $cfg = 'Release' }
$exe = Join-Path $root ('bin\' + $cfg + '\L2PointAI.exe')

function Find-MSBuild {
    $fallbacks = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2026\*\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2026\*\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe'),
        'E:\VisualStudio2026\MSBuild\Current\Bin\MSBuild.exe',
        'D:\VisualStudio2026\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
    )
    foreach ($pattern in $fallbacks) {
        $hit = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('msbuild_' + [guid]::NewGuid().ToString('N') + '.txt')
        try {
            Start-Process -FilePath $vswhere `
                -ArgumentList '-latest','-products','*','-requires','Microsoft.Component.MSBuild',
                              '-find','MSBuild\**\Bin\MSBuild.exe' `
                -RedirectStandardOutput $tmp -NoNewWindow -Wait | Out-Null
            if (Test-Path $tmp) {
                $line = Get-Content $tmp -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } | Select-Object -First 1
                if ($line -and (Test-Path $line.Trim())) { return $line.Trim() }
            }
        }
        catch { }
        finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }
    return $null
}

function Build-WithCsc {
    Write-Host '改用 build.ps1（系统自带 csc.exe，不依赖 MSBuild）' -ForegroundColor Yellow
    & (Join-Path $root 'build.ps1')
    return (Join-Path $root 'bin\L2PointAI.exe')
}

function Show-StartupLog {
    param([string]$dir)
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'L2PointAI\logs\startup.log'),
        (Join-Path $dir 'startup.log')
    )
    foreach ($lg in $candidates) {
        if (Test-Path $lg) {
            Write-Host ('--- 日志: ' + $lg + ' ---') -ForegroundColor Yellow
            Get-Content $lg -Tail 20
            return
        }
    }
    Write-Host '（没有找到 startup.log）' -ForegroundColor Yellow
}

if (-not $NoBuild) {
    $built = $false
    $msbuild = Find-MSBuild
    if ($msbuild) {
        Write-Host ('使用 MSBuild: ' + $msbuild)
        $params = @("`"$sln`"", '/t:Restore;Build', ('/p:Configuration=' + $cfg), '/v:m', '/nologo')
        if ($NoSharedCompilation) { $params += '/p:UseSharedCompilation=false' }
        $log = Join-Path ([IO.Path]::GetTempPath()) ('l2ai_build_' + [guid]::NewGuid().ToString('N') + '.log')
        try {
            $p = Start-Process -FilePath $msbuild -ArgumentList $params -RedirectStandardOutput $log -NoNewWindow -Wait -PassThru
            $p.WaitForExit()
            $out = [string](Get-Content $log -Raw -ErrorAction SilentlyContinue)
            $code = $p.ExitCode
            $hasError = ($out -match '(?m):\s*error\b') -or ($out -match '生成失败|构建失败')
            if ((-not $hasError) -and (($code -eq 0) -or ($null -eq $code))) {
                ($out -split "`r?`n" | Where-Object { $_ -match '->|已还原' } | Select-Object -Last 3) | ForEach-Object { Write-Host $_ }
                $built = $true
            } else {
                Write-Host $out
                Write-Host ('MSBuild 构建失败 (exit=' + $code + ')') -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host ('MSBuild 启动失败：' + $_.Exception.Message) -ForegroundColor Yellow
        }
        finally { Remove-Item $log -Force -ErrorAction SilentlyContinue }
    } else {
        Write-Host '未找到 MSBuild' -ForegroundColor Yellow
    }
    if (-not $built) { $exe = Build-WithCsc }
}

if (-not (Test-Path $exe)) {
    Write-Host ('找不到 ' + $exe) -ForegroundColor Red
    exit 1
}

if ($NoLaunch) {
    Write-Host ('已构建：' + $exe) -ForegroundColor Green
    exit 0
}

Write-Host ('启动 ' + $exe) -ForegroundColor Green
$dir = Split-Path $exe -Parent
$proc = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru

# 最多等 10 秒，看进程是否存活且是否出现主窗口
$seenWindow = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.HasExited) { break }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $seenWindow = $true; break }
}

$proc.Refresh()
if ($proc.HasExited) {
    Write-Host ('程序启动后退出，退出码 ' + $proc.ExitCode) -ForegroundColor Red
    Show-StartupLog $dir
    exit 1
}

Write-Host ('进程存活，PID ' + $proc.Id) -ForegroundColor Green
if (-not $seenWindow) {
    Write-Host '但还没看到主窗口（可能仍在初始化，或窗口跑到屏幕外）。' -ForegroundColor Yellow
    Show-StartupLog $dir
}
