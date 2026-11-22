param(
    [string] $ServiceName = "BackupMirror",
    [switch] $RemoveMachineEnv
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    throw "Run this script elevated (Administrator)."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Deleted service $ServiceName"
} else {
    Write-Host "Service $ServiceName not found."
}

if ($RemoveMachineEnv) {
    [Environment]::SetEnvironmentVariable("BACKUPMIRROR_DATA", $null, "Machine")
    Write-Host "Removed machine env BACKUPMIRROR_DATA"
}

Write-Host "Done. ProgramData files left in place (jobs retained)."
