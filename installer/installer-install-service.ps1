param(
    [Parameter(Mandatory = $true)]
    [string] $ServiceExe,
    [string] $ServiceName = "BackupMirror",
    [string] $DisplayName = "Backup Mirror",
    [string] $DataRoot = "",
    [string] $StartService = "1"
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    throw "Installer must run elevated to register the Windows service."
}

if (-not (Test-Path -LiteralPath $ServiceExe)) {
    throw "Service executable not found: $ServiceExe"
}

if (-not $DataRoot) {
    $DataRoot = Join-Path $env:ProgramData "BackupMirror"
}

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataRoot "logs") | Out-Null
$marker = Join-Path $DataRoot ".shared"
if (-not (Test-Path -LiteralPath $marker)) {
    Set-Content -LiteralPath $marker -Value "BackupMirror shared data root for service + GUI`n" -Encoding UTF8
}

# Migrate per-user jobs if present and ProgramData empty
$userData = Join-Path $env:LOCALAPPDATA "BackupMirror"
$destJobs = Join-Path $DataRoot "jobs.json"
$srcJobs = Join-Path $userData "jobs.json"
if (-not (Test-Path $destJobs) -and (Test-Path $srcJobs)) {
    Copy-Item -LiteralPath $srcJobs -Destination $destJobs -Force
}
$destState = Join-Path $DataRoot "appstate.json"
$srcState = Join-Path $userData "appstate.json"
if (-not (Test-Path $destState) -and (Test-Path $srcState)) {
    Copy-Item -LiteralPath $srcState -Destination $destState -Force
}

[Environment]::SetEnvironmentVariable("BACKUPMIRROR_DATA", $DataRoot, "Machine")
$env:BACKUPMIRROR_DATA = $DataRoot

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$bin = (Resolve-Path -LiteralPath $ServiceExe).Path
$binQuoted = '"' + $bin + '"'

& sc.exe create $ServiceName binPath= $binQuoted start= auto DisplayName= $DisplayName
if ($LASTEXITCODE -ne 0) { throw "sc create failed: $LASTEXITCODE" }

sc.exe description $ServiceName "Folder sync and backup service (Backup Mirror). Data: $DataRoot" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

if ($StartService -eq "1") {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
}

$svc = Get-Service -Name $ServiceName
Write-Host "Service $ServiceName status=$($svc.Status) startType=$($svc.StartType)"
Write-Host "Data root: $DataRoot"
