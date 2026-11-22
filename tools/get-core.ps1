# Скачивает ядро sing-box в core\ и проверяет SHA256. Запуск: powershell -ExecutionPolicy Bypass -File tools\get-core.ps1
# Версия и хэш зафиксированы: обновление ядра — осознанное изменение этих двух строк.
param([switch]$Force)

$ErrorActionPreference = "Stop"
$Version = "1.14.4"
$Sha256 = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df"
$Asset = "sing-box-$Version-windows-amd64"
$Url = "https://github.com/SagerNet/sing-box/releases/download/v$Version/$Asset.zip"

$coreDir = Join-Path $PSScriptRoot "..\core"
$exe = Join-Path $coreDir "sing-box.exe"
if ((Test-Path $exe) -and -not $Force) {
    Write-Host "core\sing-box.exe already exists (use -Force to re-download)"
    exit 0
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("melowg-core-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp "$Asset.zip"
    Write-Host "Downloading sing-box $Version ..."
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "SHA256 mismatch: expected $Sha256, got $actual" }

    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force $coreDir | Out-Null
    Copy-Item (Join-Path $tmp "$Asset\sing-box.exe") $exe -Force
    Copy-Item (Join-Path $tmp "$Asset\LICENSE") (Join-Path $coreDir "LICENSE-sing-box.txt") -Force
    Write-Host "OK: $exe (sing-box $Version, SHA256 verified)" -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
