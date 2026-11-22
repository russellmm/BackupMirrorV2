param(
    [string] $ServiceName = "BackupMirror",
    [switch] $RemoveMachineEnv
)

$ErrorActionPreference = "Continue"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    Write-Host "Not admin — attempting service removal may fail."
}

# Stop GUI if running (so files unlock)
Get-Process -Name "BackupMirror", "BackupMirror.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 1
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Removed service $ServiceName"
} else {
    Write-Host "Service $ServiceName not found"
}

if ($RemoveMachineEnv) {
    [Environment]::SetEnvironmentVariable("BACKUPMIRROR_DATA", $null, "Machine")
}

# Jobs in ProgramData are retained on purpose
Write-Host "Uninstall service step complete (ProgramData jobs retained)."
