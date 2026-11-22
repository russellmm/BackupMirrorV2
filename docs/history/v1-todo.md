# Tasks: Backup Mirror

**Status:** Approved — implementation in progress  
**Plan:** [plan.md](plan.md)  
**Spec:** [../../docs/spec/backup-mirror.md](../../docs/spec/backup-mirror.md) 

Commands (from `BackupMirror/`):

```text
dotnet test BackupMirror.sln -c Release
dotnet build BackupMirror.sln -c Release
```

---

## Phase 1: Foundation

### Task 1: Solution + project skeleton

**Description:** Create `BackupMirror.sln` with Core, Service, App, Ipc, and Core.Tests projects; net8.0 / net8.0-windows as appropriate; project references wired.

**Status:** DONE

**Acceptance criteria:**
- [x] Solution builds with `dotnet build`
- [x] Projects exist per spec structure (`src/*`, `tests/*`)
- [x] App is WPF (`net8.0-windows`); Service is worker-capable; Core/Ipc are library

**Verification:**
- [ ] `dotnet build BackupMirror.sln -c Release`
- [ ] Manual: solution opens / projects listed

**Dependencies:** None  

**Files likely touched:**
- `BackupMirror.sln`
- `src/BackupMirror.Core/BackupMirror.Core.csproj`
- `src/BackupMirror.Ipc/BackupMirror.Ipc.csproj`
- `src/BackupMirror.Service/BackupMirror.Service.csproj`
- `src/BackupMirror.App/BackupMirror.App.csproj`
- `tests/BackupMirror.Core.Tests/BackupMirror.Core.Tests.csproj`

**Estimated scope:** M (scaffold only; if tooling generates extra files, OK)

---

### Task 2: Domain models + JSON store

**Description:** Implement `Job`, `AppState` (`JobsArmed`), status enums, and a file-backed store under a configurable root (default `%LOCALAPPDATA%\BackupMirror\`).

**Status:** DONE

**Acceptance criteria:**
- [x] CRUD jobs persist and reload
- [x] `JobsArmed` persists across store reload
- [x] Invalid paths/empty names rejected at model or store boundary with clear errors

**Verification:**
- [ ] Unit tests for round-trip serialize/deserialize
- [ ] `dotnet test --filter FullyQualifiedName~Store`

**Dependencies:** Task 1  

**Files likely touched:**
- `src/BackupMirror.Core/Models/*.cs`
- `src/BackupMirror.Core/Storage/JsonJobStore.cs`
- `tests/BackupMirror.Core.Tests/JsonJobStoreTests.cs`

**Estimated scope:** M

---

### Task 3: Mirror planner (inventory + diff)

**Description:** Given source and dest roots, build a `MirrorPlan` of copies/updates and deletes using size + last-write time. No disk writes.

**Status:** DONE

**Acceptance criteria:**
- [x] New/changed files → copy ops
- [x] Dest orphans → delete ops
- [x] Identical files (size + mtime) → no op
- [x] Relative paths stable (normalize separators)

**Verification:**
- [ ] Temp-directory tests for add/change/delete/rename cases (rename = delete old + copy new acceptable in v1 if documented)
- [ ] `dotnet test --filter FullyQualifiedName~MirrorPlan`

**Dependencies:** Task 2  

**Files likely touched:**
- `src/BackupMirror.Core/Mirror/MirrorPlan.cs`
- `src/BackupMirror.Core/Mirror/MirrorPlanner.cs`
- `tests/BackupMirror.Core.Tests/MirrorPlannerTests.cs`

**Estimated scope:** M

---

### Task 4: Delete safety gate + execute

**Description:** `DeleteSafetyGate` (threshold 10 files). `ExecuteAsync` applies copies/updates always from plan; applies deletes only if count ≤10 or `confirmDeletes` true.

**Status:** DONE

**Acceptance criteria:**
- [x] ≤10 deletes auto-applied with copies
- [x] >10 deletes: copies may apply; deletes skipped until confirm
- [x] Cancel path leaves dest files for skipped deletes
- [x] Locked file skip logged (simulate with open write handle if feasible)

**Verification:**
- [ ] Tests for threshold boundary (10 vs 11)
- [ ] Temp FS execute tests
- [ ] `dotnet test --filter FullyQualifiedName~MirrorExecute|DeleteSafety`

**Dependencies:** Task 3  

**Files likely touched:**
- `src/BackupMirror.Core/Mirror/DeleteSafetyGate.cs`
- `src/BackupMirror.Core/Mirror/MirrorEngine.cs`
- `tests/BackupMirror.Core.Tests/MirrorEngineTests.cs`

**Estimated scope:** M

---

### Task 5: Eligibility + global arming rules

**Description:** Pure helper: job may auto-run / Sync now only when `job.Enabled && appState.JobsArmed`. Encapsulate Start all / Stop all state transitions.

**Status:** DONE

**Acceptance criteria:**
- [x] Stop all → `JobsArmed = false` persisted via store API
- [x] Start all → `JobsArmed = true`; does not enqueue sync-all
- [x] Sync now refused when not armed
- [x] Auto-run refused when job disabled or not armed

**Verification:**
- [ ] Unit tests for eligibility matrix
- [ ] `dotnet test --filter FullyQualifiedName~Eligibility|JobsArmed`

**Dependencies:** Task 2  

**Files likely touched:**
- `src/BackupMirror.Core/Jobs/JobEligibility.cs`
- `src/BackupMirror.Core/Jobs/AppStateService.cs`
- `tests/BackupMirror.Core.Tests/JobEligibilityTests.cs`

**Estimated scope:** S

---

## Checkpoint: Foundation

- [x] `dotnet test BackupMirror.sln -c Release` passes (29 tests)
- [x] `dotnet build` clean  
- [x] Core success paths for plan/execute/gate/arming covered  
- [ ] Human review before service/GUI  

---

## Phase 2: Headless runtime

### Task 6: IPC contracts

**Description:** Define JSON-serializable request/response DTOs and operation names: ListJobs, UpsertJob, DeleteJob, GetAppState, StartAllJobs, StopAllJobs, SyncNow, GetJobStatus, ConfirmDeletes, CancelDeletes, Subscribe/poll status as needed for v1.

**Acceptance criteria:**
- [ ] Shared contracts in `BackupMirror.Ipc`
- [ ] Version field or constant for future compatibility
- [ ] Core tests can construct messages without WPF

**Verification:**
- [ ] `dotnet build`  
- [ ] Optional round-trip JSON tests in Core.Tests or Ipc tests

**Dependencies:** Task 2, 5  

**Files likely touched:**
- `src/BackupMirror.Ipc/Messages/*.cs`
- `src/BackupMirror.Ipc/IpcProtocol.cs`

**Estimated scope:** S–M

---

### Task 7: Service host (console mode) + IPC server

**Description:** Worker host runs in `--console` mode; loads store; listens on named pipe; handles IPC ops by calling Core.

**Acceptance criteria:**
- [ ] `dotnet run --project src/BackupMirror.Service -- --console` stays up
- [ ] Client can ListJobs / UpsertJob over pipe
- [ ] Config root documented; spike note if LocalAppData inaccessible under service account

**Verification:**
- [ ] Manual: run console host + small test client or `dotnet` harness script
- [ ] Build succeeds

**Dependencies:** Task 6, 2  

**Files likely touched:**
- `src/BackupMirror.Service/Program.cs`
- `src/BackupMirror.Service/Ipc/NamedPipeIpcServer.cs`
- `src/BackupMirror.Service/Hosting/*.cs`

**Estimated scope:** M

---

### Task 8: Job runner orchestration

**Description:** Single runner coordinates plan → gate → execute per job; sets statuses (`Running`, `WaitingConfirm`, `Success`, `Failed`, `Degraded`, `Stopped`); holds pending delete confirmation per job.

**Acceptance criteria:**
- [ ] Sync now path end-to-end in process
- [ ] WaitingConfirm blocks deletes until Confirm/Cancel IPC
- [ ] Concurrent two jobs don’t crash (queue or per-job lock)

**Verification:**
- [ ] Integration-style tests with temp dirs against runner if hostable; else manual with console + client
- [ ] `dotnet test` still green for Core

**Dependencies:** Task 4, 5, 7  

**Files likely touched:**
- `src/BackupMirror.Service/Runner/JobRunner.cs`
- `src/BackupMirror.Core/Mirror/*` (only if small hooks needed)
- `tests/...` optional runner tests

**Estimated scope:** M

---

### Task 9: Scheduled trigger

**Description:** Timer checks enabled+armed Scheduled jobs; fires once per local day at `ScheduledTimeLocal`.

**Acceptance criteria:**
- [ ] Job scheduled for near-future time runs without Sync now
- [ ] Does not fire when `JobsArmed == false`
- [ ] After Start all, waits for next schedule (no immediate forced run)

**Verification:**
- [ ] Test with injectable clock/time provider
- [ ] Manual console check optional

**Dependencies:** Task 8  

**Files likely touched:**
- `src/BackupMirror.Service/Scheduling/ScheduleWatcher.cs`
- `src/BackupMirror.Core/Time/IClock.cs`
- `tests/BackupMirror.Core.Tests/ScheduleTests.cs`

**Estimated scope:** M

---

### Task 10: Realtime watcher + debounce

**Description:** For Realtime jobs when eligible, watch source tree; debounce events; trigger reconcile; periodic catch-up.

**Acceptance criteria:**
- [ ] New file appears on dest without Sync now (debounce ≤30s target)
- [ ] Watchers stop when Stop all or job disabled
- [ ] Watchers start again on Start all for realtime jobs

**Verification:**
- [ ] Manual temp-folder test with console host
- [ ] Unit test debounce helper if extracted

**Dependencies:** Task 8  

**Files likely touched:**
- `src/BackupMirror.Service/Watching/RealtimeWatcher.cs`
- `src/BackupMirror.Service/Watching/Debounce.cs`

**Estimated scope:** M

---

## Checkpoint: Headless

- [ ] Console host runs jobs: manual, scheduled, realtime  
- [ ] Stop all / Start all behavior matches spec  
- [ ] Delete confirm via IPC works  
- [ ] Human review before GUI polish  

---

## Phase 3: GUI

### Task 11: WPF shell + tray + Exit / close-to-tray

**Description:** Main window chrome; NotifyIcon; X hides to tray; Exit from menu and tray quits GUI only.

**Acceptance criteria:**
- [ ] Close (X) → tray, process alive
- [ ] Exit from GUI and tray → process ends
- [ ] Double-click/tray open restores window

**Verification:**
- [ ] Manual run of App (may show disconnected state if service down)

**Dependencies:** Task 1  

**Files likely touched:**
- `src/BackupMirror.App/App.xaml`
- `src/BackupMirror.App/MainWindow.xaml`
- `src/BackupMirror.App/Tray/TrayIconService.cs`

**Estimated scope:** M

---

### Task 12: Job list + add/edit via IPC

**Description:** List jobs with status; add/edit dialog (name, paths, mode, schedule time, enabled); save through IPC.

**Acceptance criteria:**
- [ ] Create ≥2 jobs, edit schedule 2:00→3:00, restart app — persisted
- [ ] Validation errors shown for bad paths/empty name

**Verification:**
- [ ] Manual against console/service host
- [ ] Spec success criterion 1

**Dependencies:** Task 7, 11  

**Files likely touched:**
- `src/BackupMirror.App/Views/*`
- `src/BackupMirror.App/ViewModels/*`
- `src/BackupMirror.App/Ipc/IpcClient.cs`

**Estimated scope:** M

---

### Task 13: Sync now + Start all / Stop all UI

**Description:** Toolbar/menu actions; banner when stopped; Sync now disabled when not armed or job disabled.

**Acceptance criteria:**
- [ ] Stop all stops automatic activity (observe with realtime/scheduled)
- [ ] Start all does not immediate-sync all jobs
- [ ] Sync now blocked while globally stopped

**Verification:**
- [ ] Manual checklist vs spec criteria 5, 9
- [ ] Tray reflects “all stopped” if implemented

**Dependencies:** Task 12, 8  

**Files likely touched:**
- `src/BackupMirror.App/ViewModels/MainViewModel.cs`
- `src/BackupMirror.App/MainWindow.xaml`

**Estimated scope:** S–M

---

### Task 14: WaitingConfirm UX

**Description:** When job status is WaitingConfirm, show dialog with delete count (+ sample paths if available); Confirm/Cancel IPC.

**Acceptance criteria:**
- [ ] 11+ pending deletes never apply without Confirm
- [ ] Cancel leaves those dest files
- [ ] Tray attention state when confirm needed

**Verification:**
- [ ] Manual test staging 11 deletes
- [ ] Spec success criterion 4

**Dependencies:** Task 12, 8  

**Files likely touched:**
- `src/BackupMirror.App/Dialogs/ConfirmDeletesWindow.xaml`
- `src/BackupMirror.App/ViewModels/*`

**Estimated scope:** M

---

## Checkpoint: GUI

- [ ] Control plane complete against running host  
- [ ] Tray + Exit behavior correct  
- [ ] Human smoke test  

---

## Phase 4: Install & verification

### Task 15: Machine-wide service install + README

**Description:** PowerShell scripts to install/start/stop/uninstall Windows Service; document UNC preference, GUI autostart, Log On account if needed for share access; README with dev and prod commands.

**Acceptance criteria:**
- [ ] Elevated install registers machine-wide service
- [ ] Service auto-start type automatic
- [ ] README covers reboot + Stop all persistence

**Verification:**
- [ ] Manual install on dev machine (user-approved elevation)
- [ ] `sc query` shows service

**Dependencies:** Task 7–10  

**Files likely touched:**
- `scripts/install-service.ps1`
- `scripts/uninstall-service.ps1`
- `README.md`

**Estimated scope:** M

---

### Task 16: Spec success-criteria walkthrough

**Description:** Run through spec success criteria 1–12; file results in `tasks/verification-log.md` (pass/fail/notes).

**Acceptance criteria:**
- [ ] Each criterion marked pass or explicit defer with reason
- [ ] `dotnet test` green

**Verification:**
- [ ] `tasks/verification-log.md` completed
- [ ] Human sign-off

**Dependencies:** Tasks 1–15  

**Files likely touched:**
- `tasks/verification-log.md`

**Estimated scope:** S (process)

---

## Checkpoint: Complete

- [ ] All tasks checked  
- [ ] Spec success criteria met  
- [ ] Spec approval section Phase 4 checked  
- [ ] Ready for daily use / further polish  

---

## Implementation rules (when Phase 4 coding starts)

- Follow **TDD** for Core (Tasks 2–5 especially): red → green → refactor  
- **One task at a time**; leave solution building after each  
- Update spec if behavior changes — don’t silently diverge  
- Do not start Task N+1 if checkpoint before it is red  
