param(
    [string] $Configuration = "Release",
    [string] $OutputDir = "",
    [switch] $SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) {
    $OutputDir = Join-Path $root "publish"
}

$serviceOut = Join-Path $OutputDir "service"
$appOut = Join-Path $OutputDir "app"

$scArgs = @()
if ($SelfContained) {
    $scArgs = @("--self-contained", "true", "-p:PublishSingleFile=false", "-p:IncludeNativeLibrariesForSelfExtract=true")
} else {
    $scArgs = @("--self-contained", "false")
}

Write-Host "Publishing Service -> $serviceOut  (SelfContained=$SelfContained)"
& dotnet publish (Join-Path $root "src\BackupMirror.Service\BackupMirror.Service.csproj") `
    -c $Configuration -r win-x64 -o $serviceOut @scArgs
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

Write-Host "Publishing tray app -> $appOut  (SelfContained=$SelfContained)"
& dotnet publish (Join-Path $root "src\BackupMirror.Tray\BackupMirror.Tray.csproj") `
    -c $Configuration -r win-x64 -o $appOut @scArgs
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed" }

Write-Host ""
Write-Host "Publish complete."
Write-Host "  Service: $(Join-Path $serviceOut 'BackupMirror.Service.exe')"
Write-Host "  Tray:    $(Join-Path $appOut 'BackupMirror.exe')"
Write-Host "  Dashboard (once the service runs): http://127.0.0.1:8737"
