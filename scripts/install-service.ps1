param(
    [string] $BinPath = "",
    [string] $ServiceName = "BackupMirror",
    [string] $DisplayName = "Backup Mirror",
    [string] $DataRoot = "",
    [switch] $RunAsCurrentUser,
    [string] $ServiceUser = "",
    [string] $ServicePassword = ""
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    throw "Run this script elevated (Administrator). Right-click PowerShell -> Run as administrator."
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $BinPath) {
    $default = Join-Path $root "publish\service\BackupMirror.Service.exe"
    if (Test-Path -LiteralPath $default) {
        $BinPath = $default
    } else {
        $fallback = Join-Path $root "src\BackupMirror.Service\bin\Release\net8.0\BackupMirror.Service.exe"
        if (Test-Path -LiteralPath $fallback) { $BinPath = $fallback }
    }
}

if (-not $BinPath -or -not (Test-Path -LiteralPath $BinPath)) {
    throw "Service binary not found. Run .\scripts\publish.ps1 first, or pass -BinPath."
}

if (-not $DataRoot) {
    $DataRoot = Join-Path $env:ProgramData "BackupMirror"
}

# Shared data root for service + GUI
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataRoot "logs") | Out-Null
$marker = Join-Path $DataRoot ".shared"
if (-not (Test-Path -LiteralPath $marker)) {
    Set-Content -LiteralPath $marker -Value "BackupMirror shared data root for service + GUI`n" -Encoding UTF8
}

# Migrate per-user jobs if ProgramData empty
$userData = Join-Path $env:LOCALAPPDATA "BackupMirror"
$destJobs = Join-Path $DataRoot "jobs.json"
$srcJobs = Join-Path $userData "jobs.json"
if (-not (Test-Path $destJobs) -and (Test-Path $srcJobs)) {
    Copy-Item -LiteralPath $srcJobs -Destination $destJobs
    Write-Host "Migrated jobs.json from LocalAppData -> ProgramData"
}
$destState = Join-Path $DataRoot "appstate.json"
$srcState = Join-Path $userData "appstate.json"
if (-not (Test-Path $destState) -and (Test-Path $srcState)) {
    Copy-Item -LiteralPath $srcState -Destination $destState
    Write-Host "Migrated appstate.json from LocalAppData -> ProgramData"
}

# Machine env so GUI also prefers this root when opened before service marker checks
[Environment]::SetEnvironmentVariable("BACKUPMIRROR_DATA", $DataRoot, "Machine")
$env:BACKUPMIRROR_DATA = $DataRoot

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$bin = (Resolve-Path -LiteralPath $BinPath).Path
# Quote path for sc.exe
$binQuoted = '"' + $bin + '"'

$createArgs = @("create", $ServiceName, "binPath=", $binQuoted, "start=", "auto", "DisplayName=", $DisplayName)

if ($RunAsCurrentUser) {
    $who = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    Write-Host "Configuring service to run as $who (you will be prompted for password if not supplied)."
    if (-not $ServicePassword) {
        $sec = Read-Host -AsSecureString "Password for $who"
        $ServicePassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    }
    $createArgs += @("obj=", $who, "password=", $ServicePassword)
} elseif ($ServiceUser) {
    if (-not $ServicePassword) { throw "-ServicePassword required with -ServiceUser" }
    $createArgs += @("obj=", $ServiceUser, "password=", $ServicePassword)
}

& sc.exe @createArgs
if ($LASTEXITCODE -ne 0) { throw "sc create failed: $LASTEXITCODE" }

sc.exe description $ServiceName "One-way folder mirror service (Backup Mirror). Data: $DataRoot" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# Grant service exe directory read for LOCAL SERVICE / users as needed — published under user folder is OK if RunAsCurrentUser
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
Get-Service -Name $ServiceName | Format-List Name, Status, StartType, DisplayName

Write-Host ""
Write-Host "Installed and started $ServiceName"
Write-Host "  Binary: $bin"
Write-Host "  Data:   $DataRoot"
Write-Host "  Log:    $(Join-Path $DataRoot 'logs\service.log')"
Write-Host ""
Write-Host "Tips:"
Write-Host "  - Prefer UNC destinations (\\server\share\...). Mapped drives often fail for services."
Write-Host "  - For share access as your Windows user, re-run with -RunAsCurrentUser"
Write-Host "  - Open the GUI after install; it talks to the service over the named pipe."
Write-Host "  - New console windows: restart them so BACKUPMIRROR_DATA machine env is visible (or reboot)."
