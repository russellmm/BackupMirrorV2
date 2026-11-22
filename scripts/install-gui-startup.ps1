param(
    [string] $AppPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $AppPath) {
    $published = Join-Path $root "publish\app\BackupMirror.exe"
    $built = Join-Path $root "src\BackupMirror.Tray\bin\Release\net8.0-windows\BackupMirror.exe"
    if (Test-Path $published) { $AppPath = $published }
    elseif (Test-Path $built) { $AppPath = $built }
}

if (-not $AppPath -or -not (Test-Path -LiteralPath $AppPath)) {
    throw "Tray exe not found. Run .\scripts\publish.ps1 first, or pass -AppPath."
}

$AppPath = (Resolve-Path -LiteralPath $AppPath).Path
$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "Backup Mirror.lnk"

$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($shortcutPath)
$sc.TargetPath = $AppPath
$sc.Arguments = "--tray"
$sc.WorkingDirectory = Split-Path -Parent $AppPath
$sc.WindowStyle = 7  # minimized
$sc.Description = "Backup Mirror GUI (starts in tray)"
$sc.Save()

Write-Host "Created Startup shortcut:"
Write-Host "  $shortcutPath"
Write-Host "  -> $AppPath"
Write-Host ""
Write-Host "The GUI will start at user logon. Close (X) hides to tray; use Exit to quit."
