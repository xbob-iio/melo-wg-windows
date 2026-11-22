# Сборка готовой к запуску папки dist\MeloWG (сначала прогоняются самопроверки).
# Запуск: powershell -ExecutionPolicy Bypass -File build.ps1 [-Zip]
param(
    [string]$Out = (Join-Path $PSScriptRoot "dist\MeloWG"),
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw ".NET SDK not found. Install it: winget install Microsoft.DotNet.SDK.10" }

if (-not (Test-Path (Join-Path $PSScriptRoot "core\sing-box.exe"))) {
    Write-Host "== Core" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "tools\get-core.ps1")
}

Write-Host "== Self-test" -ForegroundColor Cyan
& $dotnet run --project (Join-Path $PSScriptRoot "tests\MeloWG.SelfTest") -c Release
if ($LASTEXITCODE -ne 0) { throw "Self-test failed" }

Write-Host "== Publish to $Out" -ForegroundColor Cyan
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
& $dotnet publish (Join-Path $PSScriptRoot "src\MeloWG\MeloWG.csproj") -c Release -o $Out --self-contained false -p:DebugType=none -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Copy-Item (Join-Path $PSScriptRoot "LICENSE") (Join-Path $Out "LICENSE.txt") -Force

if ($Zip) {
    [xml]$proj = Get-Content (Join-Path $PSScriptRoot "src\MeloWG\MeloWG.csproj")
    $version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    $zipPath = Join-Path (Split-Path $Out -Parent) "MeloWG-$version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $Out -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Zip: $zipPath" -ForegroundColor Green
}

Write-Host "Done: $Out\MeloWG.exe" -ForegroundColor Green
