# Sign L2PointAI.exe (or any build output) with Authenticode.
#
#   .\sign.ps1 -Pfx .\codesign.pfx -Password secret          # 用购买的代码签名证书
#   .\sign.ps1 -Thumbprint ABC123...                          # 用证书存储里的证书
#   .\sign.ps1 -SelfSigned                                    # 自签名（内部分发/测试用）
#   .\sign.ps1 -Pfx ... -Path .\dist\L2PointAI.exe -TimestampUrl http://timestamp.digicert.com
param(
    [string]$Path = "",
    [string]$Pfx = "",
    [string]$Password = "",
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SelfSigned,
    [switch]$NoTimestamp
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($Path)) { $Path = Join-Path $root 'dist\L2PointAI.exe' }
if (-not (Test-Path $Path)) { throw "找不到要签名的文件：$Path" }

function Find-SignTool {
    $sdk = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $sdk) {
        $hit = Get-ChildItem (Join-Path $sdk '*\x64\signtool.exe') -ErrorAction SilentlyContinue |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $hit2 = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\*\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if ($hit2) { return $hit2.FullName }
    return $null
}

$signtool = Find-SignTool
if (-not $signtool) {
    throw '未找到 signtool.exe。请安装 Windows SDK（“Windows 10/11 SDK”），或改用 Visual Studio 安装器里的“Windows SDK”组件。'
}
Write-Host ("signtool: " + $signtool)

if ($SelfSigned) {
    $outDir = Join-Path $root 'certs'
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $pwdPlain = 'L2PointAI-local'
    $signtoolTmp = [System.Security.Cryptography.HashAlgorithmName]::SHA256
    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=L2PointAI Self Signed", $rsa,
        $signtoolTmp, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $eku = [System.Security.Cryptography.OidCollection]::new()
    [void]$eku.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.3"))   # Code Signing
    $req.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
    $req.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $false))
    $req.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(3))
    $Pfx = Join-Path $outDir 'L2PointAI-selfsigned.pfx'
    [IO.File]::WriteAllBytes($Pfx, $cert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pwdPlain))
    [IO.File]::WriteAllBytes((Join-Path $outDir 'L2PointAI-selfsigned.cer'),
        $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $Password = $pwdPlain
    Write-Host ("已创建自签名证书：" + $Pfx + "（口令 " + $pwdPlain + "）") -ForegroundColor Yellow
    Write-Host "  内部部署：把 .cer 导入到“受信任的根证书颁发机构”，否则其他机器仍显示不受信任。" -ForegroundColor Yellow
}

$args = @('sign', '/fd', 'SHA256', '/td', 'SHA256')
if (-not $NoTimestamp) { $args += @('/tr', $TimestampUrl) }
if ($Thumbprint) {
    $args += @('/sha1', $Thumbprint)
} elseif ($Pfx) {
    $args += @('/f', $Pfx)
    if ($Password) { $args += @('/p', $Password) }
} else {
    throw '请提供 -Pfx / -Thumbprint，或使用 -SelfSigned'
}
$args += $Path

Write-Host ("执行：" + $signtool + " " + ($args -join ' '))
$log = Join-Path $env:TEMP ('signtool_' + [guid]::NewGuid().ToString('N') + '.log')
$err = Join-Path $env:TEMP ('signtool_' + [guid]::NewGuid().ToString('N') + '.err')
$p = Start-Process -FilePath $signtool -ArgumentList $args -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $err
Get-Content $log -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Get-Content $err -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
Remove-Item $log,$err -Force -ErrorAction SilentlyContinue

$sig = Get-AuthenticodeSignature $Path
Write-Host ''
Write-Host ("状态：" + $sig.Status)
if ($sig.SignerCertificate) {
    Write-Host ("签名者：" + $sig.SignerCertificate.Subject)
    Write-Host ("指纹  ：" + $sig.SignerCertificate.Thumbprint)
}
Write-Host ("SHA256：" + (Get-FileHash $Path -Algorithm SHA256).Hash)

if ($sig.Status -eq 'Valid') { exit 0 }
if ($sig.Status -eq 'UnknownError' -or $sig.Status -eq 'UntrustedRoot') {
    Write-Host '签名已写入，但证书不受信任（自签名属正常）。' -ForegroundColor Yellow
    exit 0
}
exit 1
