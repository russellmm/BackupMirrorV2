param(
    [string] $Configuration = "Release",
    [string] $Version = ""
)

# Builds dist\BackupMirror-Setup-<version>.exe (service + tray, self-contained win-x64).
# Never touches an installed/running copy: the payload goes to publish-installer\.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup compiler (ISCC.exe) not found. Install JRSoftware.InnoSetup via winget." }

Write-Host "=== 1/2 Publish self-contained win-x64 ($Version) ==="
$payload = Join-Path $root "publish-installer"
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
& "$root\scripts\publish.ps1" -Configuration $Configuration -SelfContained -OutputDir $payload
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
foreach ($f in @("service\BackupMirror.Service.exe", "service\wwwroot\index.html", "app\BackupMirror.exe")) {
    if (-not (Test-Path (Join-Path $payload $f))) { throw "Missing $f in payload" }
}

Write-Host "=== 2/2 Compile installer ==="
$issPath = Join-Path $root "installer\BackupMirror.iss"
$issWork = Join-Path $root "installer\BackupMirror.build.iss"
$iss = Get-Content -LiteralPath $issPath -Raw
$iss = [regex]::Replace($iss, '#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$Version`"")
$iss = $iss.Replace('..\publish\app\', '..\publish-installer\app\').Replace('..\publish\service\', '..\publish-installer\service\')
Set-Content -LiteralPath $issWork -Value $iss -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $root "dist") | Out-Null
try {
    & $iscc /Q $issWork
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed: $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $issWork -Force -ErrorAction SilentlyContinue
}

$setup = Get-Item (Join-Path $root "dist\BackupMirror-Setup-$Version.exe")
Write-Host ""
Write-Host "Installer ready: $($setup.FullName)  ($([math]::Round($setup.Length / 1MB, 1)) MB)"
Write-Host "Run it as Administrator. It upgrades v1 in place and keeps your jobs."
