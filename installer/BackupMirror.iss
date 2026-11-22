; Inno Setup script — Backup Mirror 2
; Build with: scripts\build-installer.ps1 -Version 2.0.0

#define MyAppName "Backup Mirror"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "russellmm"
#define MyAppURL "https://github.com/russellmm/BackupMirrorV2"
#define MyAppExeName "BackupMirror.exe"
#define MyServiceExeName "BackupMirror.Service.exe"
#define MyServiceName "BackupMirror"

[Setup]
AppId={{A7C3E8F1-4B2D-4E9A-9C1F-8D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\BackupMirror
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=BackupMirror-Setup-{#MyAppVersion}
SetupIconFile=..\src\BackupMirror.Tray\app.ico
UninstallDisplayIcon={app}\app\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
CloseApplications=force
; Always start the service / tray on upgrades (do not inherit an earlier unchecked choice)
UsePreviousTasks=no
RestartIfNeededByRun=no
; .NET is bundled via self-contained publish; the dashboard window uses WebView2 (built into Windows 11)
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "autostartgui"; Description: "Show Backup Mirror in the system tray when anyone signs in"; GroupDescription: "Startup:"
Name: "startservice"; Description: "Start the Backup Mirror service after install"; GroupDescription: "Service:"

[InstallDelete]
; v1 shipped a WPF app in the same folder; remove it so nothing stale is left behind
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\service"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installer-install-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "installer-uninstall-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "installer-uninstall-startup.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; Tasks: desktopicon
Name: "{commonstartup}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; Parameters: "--tray"; Tasks: autostartgui

[Run]
; Remove the v1 per-user startup shortcut (it pointed at the old WPF app)
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\installer-uninstall-startup.ps1"""; \
  Flags: runhidden waituntilterminated
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\installer-install-service.ps1"" -ServiceExe ""{app}\service\{#MyServiceExeName}"" -StartService {code:StartServiceFlag}"; \
  StatusMsg: "Installing the Backup Mirror service..."; \
  Flags: runhidden waituntilterminated
Filename: "{app}\app\{#MyAppExeName}"; \
  Description: "Open {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\installer-uninstall-service.ps1"""; \
  RunOnceId: "RemoveService"; \
  Flags: runhidden waituntilterminated
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\installer-uninstall-startup.ps1"""; \
  RunOnceId: "RemoveStartup"; \
  Flags: runhidden waituntilterminated

[Code]
function StartServiceFlag(Param: string): string;
begin
  if WizardIsTaskSelected('startservice') then
    Result := '1'
  else
    Result := '0';
end;

{ Upgrades: stop the running service and tray so their files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec('sc.exe', 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('taskkill.exe', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('taskkill.exe', '/F /IM BackupMirror.App.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(2500);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: string;
begin
  if CurStep = ssPostInstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\BackupMirror');
    ForceDirectories(DataDir);
    ForceDirectories(DataDir + '\logs');
    SaveStringToFile(DataDir + '\.shared', 'BackupMirror shared data root' + #13#10, False);
  end;
end;
