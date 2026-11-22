# Build L2PointAI.exe with the .NET Framework 4.8 compiler that ships with Windows.
# An optional Windows SDK rc.exe is used to embed the icon + Win32 version info.
param(
    [switch]$NoVersionRes
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = Join-Path $root 'assets'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (need .NET Framework 4.x)' }

# ---- optional: version.rc -> version.res (icon + file version shown in Explorer)
$res = Join-Path $assets 'version.res'
$rc = $null
if (-not $NoVersionRes) {
    $sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $sdkRoot) {
        $rc = Get-ChildItem (Join-Path $sdkRoot '*\x64\rc.exe') -ErrorAction SilentlyContinue |
              Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if ($rc) {
        if (Test-Path $res) { Remove-Item $res -Force }
        $rcOut = Join-Path $root 'rc.log'
        $rcErr = Join-Path $root 'rc.err.log'
        Push-Location $assets
        Start-Process -FilePath $rc -ArgumentList '/nologo','/fo','version.res','version.rc' `
            -RedirectStandardOutput $rcOut -RedirectStandardError $rcErr -NoNewWindow -Wait | Out-Null
        Pop-Location
        if (-not (Test-Path $res)) {
            Write-Host 'rc.exe failed, falling back to /win32icon' -ForegroundColor Yellow
            if (Test-Path $rcErr) { Get-Content $rcErr }
            $rc = $null
        }
    }
}

$src = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName })
$outDir = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $outDir | Out-Null
$exe = Join-Path $outDir 'L2PointAI.exe'

$outLog = Join-Path $root 'build.log'
$errLog = Join-Path $root 'build.err.log'
if (Test-Path $outLog) { Remove-Item $outLog -Force }
if (Test-Path $errLog) { Remove-Item $errLog -Force }

# NOTE: csc wants resource options as /opt:path (no embedded quotes); the project path
# must therefore not contain spaces.
$argList = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/debug:pdbonly',
    "/out:$exe",
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Security.dll',
    '/nowarn:1591'
)
$mode = 'none'
if ($rc -and (Test-Path $res)) {
    $argList += "/win32res:$res"
    $mode = 'res (icon + version)'
} elseif (Test-Path (Join-Path $assets 'app.ico')) {
    $argList += "/win32icon:$(Join-Path $assets 'app.ico')"
    $mode = 'icon only'
}
if (-not ($rc -and (Test-Path $res))) {
    $manifest = Join-Path $root 'src\app.manifest'
    if (Test-Path $manifest) { $argList += "/win32manifest:$manifest" }
}
$argList += $src

if (Test-Path $exe) { Remove-Item $exe -Force }
Start-Process -FilePath $csc -ArgumentList $argList -RedirectStandardOutput $outLog -RedirectStandardError $errLog -NoNewWindow -Wait | Out-Null

foreach ($logFile in @($outLog, $errLog)) {
    if (Test-Path $logFile) {
        $msg = Get-Content $logFile -Raw
        if ($msg -and $msg.Trim().Length -gt 0) { Write-Host '--- compiler output ---' -ForegroundColor Yellow; Write-Host $msg }
    }
}

if (Test-Path $exe) {
    $fi = Get-Item $exe
    $vi = $fi.VersionInfo
    Write-Host ("BUILD OK -> {0}  ({1:N0} bytes)" -f $fi.FullName, $fi.Length) -ForegroundColor Green
    Write-Host ("            resource: {0}" -f $mode)
    Write-Host ("            version : {0} / {1}" -f $vi.FileVersion, $vi.ProductName)
} else {
    Write-Host 'BUILD FAILED (no exe produced)' -ForegroundColor Red
    exit 1
}
