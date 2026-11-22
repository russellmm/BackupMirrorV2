$ErrorActionPreference = "Continue"
$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "Backup Mirror.lnk"
if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
    Write-Host "Removed $shortcutPath"
} else {
    Write-Host "No startup shortcut"
}
