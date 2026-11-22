# Backup Mirror — session memory / handoff

**Last updated:** 2026-07-26  
**Purpose:** Continue work tomorrow without losing context.  
**Working tree:** `<v1-workspace>\`

---

## What this project is

Personal **Windows 11** app: **one-way true mirror** of important local folders to a **network share** (source always wins).

Confirmed intent: `docs/intent/backup-mirror.md`  
Approved spec: `docs/spec/backup-mirror.md`  
Plan / tasks: `BackupMirror/tasks/plan.md`, `BackupMirror/tasks/todo.md`

### Product rules (locked)

| Rule | Detail |
|------|--------|
| Direction | Source → destination only; exact mirror (adds/updates/renames/deletes) |
| Jobs | Multiple jobs; list + easy edit (e.g. schedule 2:00 → 3:00) |
| Modes | Realtime, Scheduled (local time of day), Manual + **Sync now** |
| Always-on | Machine-wide Windows Service; GUI is control plane |
| GUI | Full window; **X hides to tray**; **Exit** from GUI menu and tray |
| Global control | **Stop all jobs** / **Start all jobs** (not the same as Exit) |
| Stop all persists | Survives reboot until Start all again (`JobsArmed`) |
| Sync now while stopped | **Blocked** until Start all |
| Start all | Does **not** force-sync every scheduled job; realtime resumes + catch-up; scheduled waits for its time |
| Large deletes | If plan would delete **> 10 files** on dest → **WaitingConfirm**; user must confirm |
| Exclude patterns | **Not in v1** |
| Exit GUI | Does **not** stop the Windows Service |

---

## Repo layout

```
<v1-workspace>\
  docs\
    intent\backup-mirror.md
    spec\backup-mirror.md
  BackupMirror\
    BackupMirror.sln
    README.md
    memory.md                 ← this file (optional copy; canonical handoff)
    src\
      BackupMirror.Core\      # models, JSON store, planner, engine, eligibility
      BackupMirror.Ipc\       # named-pipe protocol
      BackupMirror.Service\   # Windows Service / console host
      BackupMirror.App\       # WPF GUI + tray
    tests\
      BackupMirror.Core.Tests\
    scripts\
      install-service.ps1
      uninstall-service.ps1
    tools\
      SmokeClient\            # IPC smoke test
      JobEditSmoke\           # STA smoke for Add/Edit dialog
    tasks\
      plan.md
      todo.md
```

**Config / state at runtime:**  
`%LOCALAPPDATA%\BackupMirror\` (`jobs.json`, `appstate.json`, logs)  
Override: env var `BACKUPMIRROR_DATA`

---

## Tech stack

- .NET **8** SDK (installed on this machine: 8.0.423)
- C# / WPF GUI + WinForms `NotifyIcon` + `FolderBrowserDialog`
- Worker / Windows Service host
- Named pipe IPC: pipe name `BackupMirror.Ipc`
- xUnit + FluentAssertions for Core tests
- Compare: size + last-write time
- Deletes >10: confirm gate; copies/updates may apply first

---

## How to run (dev)

**Requires .NET 8 SDK on PATH** (`C:\Program Files\dotnet`).

```bat
cd /d <v1-workspace>\BackupMirror

REM Terminal 1 — service (keep open)
dotnet run --project src\BackupMirror.Service -c Release

REM Terminal 2 — GUI
dotnet run --project src\BackupMirror.App -c Release
```

**Build / test:**

```bat
cd /d <v1-workspace>\BackupMirror
dotnet build BackupMirror.sln -c Release
dotnet test BackupMirror.sln -c Release
```

**Prefer UNC** destinations (`\\server\share\folder`) over mapped drives for service use.

---

## What works (user-tested 2026-07-26)

- Multi-job CRUD, persist across restart  
- Realtime: starts without mandatory Sync now; catch-up after Start all  
- Stop all / Start all behavior as above  
- WaitingConfirm → **Confirm deletes** / **Cancel deletes** buttons (not only via Sync now)  
- Browse source/dest folders  
- Schedule hour/minute dropdowns + AM/PM preview  
- Option: **Put source folder name under destination** (default on for new jobs)  
  - e.g. source `...\myfiles` + dest parent `...\backup` → `...\backup\myfiles`  
- Tray, close-to-tray, Exit from GUI and tray  
- Core automated tests: **29 passed** (last run)

### Bugs fixed along the way (don’t reintroduce)

1. Realtime required Sync now first → initial run + catch-up on watcher start / Start all / upsert realtime  
2. WaitingConfirm only via Sync now → Confirm/Cancel deletes UI + banner  
3. Start all didn’t resume realtime → `RequestImmediateCatchUp` + reconcile  
4. Add/Edit job **crash** → checkbox `Checked` handler during XAML init before `DestPreviewText` existed; handlers now wired **after** `InitializeComponent` + `_uiReady` guard  

---

## Not done yet / next steps tomorrow

Priority suggestions:

1. **Machine-wide service install (elevated)**  
   - `scripts\install-service.ps1 -BinPath "<path-to-BackupMirror.Service.exe>"`  
   - Publish or point at `src\BackupMirror.Service\bin\Release\net8.0\BackupMirror.Service.exe`  
   - Verify: reboot with `JobsArmed=true`, job runs without opening GUI first  
   - Service account vs share access: may need “Log on as” your user; UNC preferred  
   - If `%LOCALAPPDATA%` inaccessible to LocalSystem, set `BACKUPMIRROR_DATA` to e.g. `C:\ProgramData\BackupMirror` (would be a small config/docs change)

2. **GUI at user logon**  
   - Startup shortcut or Task Scheduler to `BackupMirror.App.exe`

3. **Task 16 verification log**  
   - Walk `docs/spec/backup-mirror.md` success criteria 1–12  
   - Write `BackupMirror/tasks/verification-log.md`

4. **Polish (optional)**  
   - Richer activity/log panel in GUI  
   - Tray status icons (OK / stopped / needs confirm)  
   - Path validation warnings if source missing  
   - Double-check edit job preserves status fields (already attempted)

5. **Git**  
   - Repo under `<v1-workspace>` may not be initialized; consider `git init` + commit if desired

---

## Key code map

| Concern | Where |
|---------|--------|
| Job / AppState models | `BackupMirror.Core/Models/` |
| Persist jobs + JobsArmed | `BackupMirror.Core/Storage/JsonJobStore.cs` |
| Plan diff | `BackupMirror.Core/Mirror/MirrorPlanner.cs` |
| Execute + delete gate | `BackupMirror.Core/Mirror/MirrorEngine.cs`, `DeleteSafetyGate.cs` |
| Eligibility | `BackupMirror.Core/Jobs/JobEligibility.cs` |
| Runner / confirm | `BackupMirror.Service/Runner/JobRunner.cs` |
| IPC server | `BackupMirror.Service/Ipc/NamedPipeIpcServer.cs` |
| Realtime watchers | `BackupMirror.Service/Watching/RealtimeWatchManager.cs` |
| Schedule | `BackupMirror.Service/Scheduling/ScheduleWatcher.cs` |
| Host loop | `BackupMirror.Service/MirrorHostedService.cs` |
| GUI main | `BackupMirror.App/MainWindow.xaml(.cs)` |
| Add/Edit job dialog | `BackupMirror.App/JobEditWindow.xaml(.cs)` |
| Tray | `BackupMirror.App/Tray/TrayIconService.cs` |
| IPC client | `BackupMirror.App/Ipc/IpcClient.cs` |

---

## Skills / process used

- `interview-me` → confirmed intent  
- `spec-driven-development` → approved spec  
- `planning-and-task-breakdown` → plan + todo  
- Implementation: incremental + Core TDD  

When continuing: load spec + this memory; don’t silently change locked product rules without updating the spec.

---

## Quick “am I healthy?” checklist

- [ ] `dotnet test BackupMirror.sln -c Release` green  
- [ ] Service console shows `IPC listening on pipe BackupMirror.Ipc`  
- [ ] GUI status: `Connected — jobs armed`  
- [ ] Add job opens (no crash)  
- [ ] Realtime job syncs a new file without Sync now  
- [ ] Stop all → Sync now blocked; Start all → realtime catches up  

---

## Contact / environment notes (this machine)

- Windows 11 host; working dir `<v1-workspace>`  
- .NET SDK installed via winget: `Microsoft.DotNet.SDK.8`  

*End of handoff.*
