# Spec: Backup Mirror (Windows 11)

**Status:** Approved  
**Date:** 2026-07-26  
**Approved:** 2026-07-26  
**Intent:** [docs/intent/backup-mirror.md](../intent/backup-mirror.md)

## Objective

Build a personal Windows 11 desktop application that keeps chosen local folders as **exact one-way mirrors** on a network share.

| | |
|---|---|
| **Who** | Single user (you) on Windows 11 |
| **What** | Multi-job true mirror: source is always source of truth; destination matches (adds, updates, renames, deletes) |
| **Why** | Quiet reliability — a durable second copy on a share without manual copy rituals |
| **Success** | After reboot and normal network blips, jobs resume and catch up; tray/GUI status is trustworthy; when you inspect the share, it matches the source |

### User stories

1. As a user, I can **create multiple backup jobs**, each pairing one source folder with one destination folder on a share.
2. As a user, I can **see all jobs at a glance** (name, mode, status, last run/error) and **edit** any job (e.g. change schedule 2:00am → 3:00am) without recreating it.
3. As a user, I can set a job to **realtime**, **scheduled (time of day)**, or rely on **Sync now** (manual).
4. As a user, when I am not using the main window, the app **sits in the system tray**; I can restore the window from the tray.
5. As a user, I can **exit the application from both the GUI and the tray menu**.
6. As a user, I can **Stop all jobs** and **Start all jobs** globally (separate from Exit), without toggling each job one by one.
7. As a user, jobs **keep running / restart after reboot** without me reopening the app and reconfiguring.
8. As a user, if a sync would **delete more than 10 files** on the destination, the app **pauses and asks me to confirm** before deleting.
9. As a user, if the share is offline or files are locked, the app **degrades gracefully** (retry/skip) rather than corrupting the mirror or dying silently.

### Out of scope (v1)

- Bidirectional sync  
- Cloud backup  
- Full-disk / system imaging  
- Version history / snapshots  
- Multi-user / team features  
- “Never delete on destination” archive mode  
- Bandwidth throttling  
- Separate stored share credentials (use Windows logged-on user)  
- Content-hash full-scan every pass  
- **Exclude patterns / filters** (entire source tree is mirrored; confirmed not needed in v1)  
- Portable / CLI-only mode  

---

## Tech Stack

| Layer | Choice | Notes |
|---|---|---|
| Language / runtime | **C# / .NET 8** | Windows 11, long support, solid service + desktop story |
| UI | **WPF** | Native desktop GUI + NotifyIcon tray |
| Always-on host | **Windows Service** (Worker Service template) | Survives GUI close and reboot (when installed/started) |
| GUI ↔ service IPC | **Named pipe** + simple JSON messages (or gRPC-over-pipe) | Local only; same user machine |
| Mirror I/O | **System.IO** + optional **Robocopy** for bulk reconcile passes | Watcher for realtime; full pass for schedule/manual/catch-up |
| Config / state | **JSON** under `%LOCALAPPDATA%\BackupMirror\` | Jobs, settings, pending confirmations |
| Logging | **Serilog** → rolling files under `%LOCALAPPDATA%\BackupMirror\logs\` | Also surfaced in GUI activity view |
| Install / autostart | **Service install** + optional **Startup** shortcut/task for tray GUI | Documented install script or single setup project later |
| Test | **xUnit** + **FluentAssertions**; file-system tests on temp dirs | No UI automation required for v1 core engine |

**Assumption (approved unless corrected):** Stack is native .NET 8 WPF + Windows Service, not Electron/Python.

---

## Commands

Project root: `<v1-workspace>\BackupMirror\` (solution name `BackupMirror`).

```text
# Restore / build
dotnet restore BackupMirror.sln
dotnet build BackupMirror.sln -c Release

# Test
dotnet test BackupMirror.sln -c Release

# Run GUI (dev)
dotnet run --project src/BackupMirror.App/BackupMirror.App.csproj

# Run service host (dev, console mode)
dotnet run --project src/BackupMirror.Service/BackupMirror.Service.csproj -- --console

# Publish self-contained (optional later)
dotnet publish src/BackupMirror.App/BackupMirror.App.csproj -c Release -r win-x64 --self-contained false
dotnet publish src/BackupMirror.Service/BackupMirror.Service.csproj -c Release -r win-x64 --self-contained false
```

Service install/uninstall (post-v1 packaging may wrap these):

```text
# Elevated — illustrative; exact names fixed at implement time
sc create BackupMirror binPath= "C:\Path\To\BackupMirror.Service.exe"
sc start BackupMirror
sc stop BackupMirror
sc delete BackupMirror
```

---

## Project Structure

```text
<v1-workspace>\
  docs\
    intent\backup-mirror.md          # Confirmed intent
    spec\backup-mirror.md            # This spec (living)
  BackupMirror\
    BackupMirror.sln
    README.md
    src\
      BackupMirror.Core\             # Domain: jobs, mirror engine, safety gate, models
      BackupMirror.Service\          # Windows Service / worker host; runs engine
      BackupMirror.App\              # WPF GUI + tray; IPC client
      BackupMirror.Ipc\              # Shared contracts/messages for pipe protocol
    tests\
      BackupMirror.Core.Tests\       # Unit + temp-dir integration tests for mirror logic
    scripts\                         # install-service.ps1, dev helpers (as needed)
    tasks\                           # plan.md / todo.md when Plan/Tasks phases run
```

| Path | Responsibility |
|---|---|
| `BackupMirror.Core` | Job store, reconcile algorithm, delete safety gate, path/UNC helpers, status model |
| `BackupMirror.Service` | Host lifetime, schedule timer, FileSystemWatcher wiring, executes sync, exposes IPC server |
| `BackupMirror.App` | Job list, add/edit, Sync now, confirm large delete, logs view, tray, exit from GUI + tray |
| `BackupMirror.Ipc` | DTOs + operation names (ListJobs, UpsertJob, DeleteJob, SyncNow, GetStatus, ConfirmDeletes, …) |
| `BackupMirror.Core.Tests` | Mirror correctness, >10 delete gate, rename/delete propagation, offline/skip behavior where mockable |

---

## Code Style

- **Nullable reference types** enabled; prefer explicit null handling at boundaries.
- **Async** for I/O (`async`/`await`); don’t block UI thread.
- Types: `PascalCase`; locals/params: `camelCase`; private fields: `_camelCase`.
- One primary type per file when practical.
- Domain logic in **Core** with no WPF or Service dependencies (testable).
- Prefer small pure functions for “what would this reconcile do?” (plan) vs “apply plan” (execute).

**Example (illustrative Core style):**

```csharp
public sealed record MirrorPlan(
    IReadOnlyList<FileOp> Copies,
    IReadOnlyList<FileOp> Deletes,
    IReadOnlyList<string> Skipped);

public interface IMirrorEngine
{
    Task<MirrorPlan> PlanAsync(Job job, CancellationToken ct);
    Task<MirrorResult> ExecuteAsync(Job job, MirrorPlan plan, MirrorExecuteOptions options, CancellationToken ct);
}

public sealed class DeleteSafetyGate
{
    public const int DefaultFileThreshold = 10;

    public bool RequiresConfirmation(MirrorPlan plan, int threshold = DefaultFileThreshold)
        => plan.Deletes.Count > threshold;
}
```

**Formatting:** `dotnet format` friendly; 4-space indent; file-scoped namespaces.

---

## Testing Strategy

| Level | Where | What |
|---|---|---|
| Unit | `tests/BackupMirror.Core.Tests` | Safety gate threshold; plan diff given synthetic file lists; job serialization |
| Integration (temp FS) | same | Create temp source/dest trees; copy/update/delete/rename; assert dest matches source |
| Manual | Dev machine | GUI create/edit job, tray minimize, exit from GUI, schedule change, share disconnect simulation |
| Service | Manual / script | Start service without GUI; reboot checklist; job still present and runs |

**Framework:** xUnit + FluentAssertions.

**Expectations (v1):**

- Core mirror + safety gate covered by automated tests before calling engine “done.”
- No mandatory UI automation framework in v1.
- `dotnet test` must pass before merge/commit of engine changes.

**Critical cases:**

1. New file on source → appears on dest  
2. Changed file on source → updated on dest  
3. Deleted file on source → deleted on dest (when ≤10 deletes or after confirm)  
4. Rename on source → dest reflects rename (no orphan old name)  
5. Planned deletes **11+** → execute blocked until confirm; cancel leaves dest unchanged for those deletes  
6. Dest unreachable → no partial destructive apply; status degraded; retries  
7. Job JSON round-trip (create, edit schedule, reload)

---

## Boundaries

### Always

- Keep **source → destination one-way** semantics; never write to source from mirror logic.
- Run **`dotnet test`** before committing Core/engine changes.
- Persist jobs so they survive process restart and reboot.
- Log skipped locked files and share-offline events.
- Enforce **>10 destination file deletes → confirm** before apply.
- Provide **Exit** in both main window (e.g. File → Exit or explicit control) and tray menu.
- Closing the main window **minimizes to tray** (does not exit); Exit shuts down GUI only (service keeps running by default).
- Provide global **Stop all jobs** and **Start all jobs**, distinct from Exit and from per-job enable/disable.
- Do **not** implement exclude patterns in v1.

### Ask first

- Adding NuGet dependencies beyond the stack table  
- Changing IPC protocol in a breaking way after first working version  
- Raising/lowering default delete threshold or making it per-job  
- Switching compare strategy to content hash  
- Packaging (MSIX/installer) approach and service install UX  
- Adding exclude patterns or multi-root jobs (exclude patterns explicitly out of v1)  
- Whether **Exit GUI** should also **stop the Windows Service** (resolved default: **No**)

### Never

- Commit secrets, share passwords, or machine-specific absolute paths into the repo  
- Bidirectional sync “just for this one case”  
- Silent destination mass-delete without the safety gate  
- Delete or skip failing tests to go green  
- Store only in-memory job config with no disk persistence  

---

## Architecture (normative for implementers)

```text
[WPF App + Tray]  --named pipe-->  [Windows Service]
                                         |
                                         v
                                   [BackupMirror.Core]
                                    plan → safety → execute
                                         |
                                         v
                                   Source disk / UNC dest
```

### Job model (v1)

```text
Job {
  Id: Guid
  Name: string
  Enabled: bool                 // per-job enable; independent of global pause
  SourcePath: string            // local directory
  DestinationPath: string       // UNC or drive letter path
  Mode: Realtime | Scheduled | ManualOnly
  ScheduledTimeLocal: TimeOnly? // required if Scheduled
  LastRunUtc: DateTimeOffset?
  LastStatus: Idle | Running | WaitingConfirm | Degraded | Failed | Success | Stopped
  LastError: string?
}

AppState {
  JobsArmed: bool               // global: false = Stop all jobs; true = Start all jobs
}
```

**Per-job `Enabled` vs global `JobsArmed`:**

- A job is **eligible to run automatically** only when `Enabled == true` **and** `JobsArmed == true`.
- **Stop all jobs:** set `JobsArmed = false`. Cancel in-flight work gracefully where possible; no realtime watches fire; no scheduled fires; service stays up. Does **not** exit GUI or stop the Windows Service.
- **Start all jobs:** set `JobsArmed = true`. Does **not** force an immediate full sync of every job. Each job resumes according to **its own mode**:
  - **Realtime** — watchers/catch-up become active again
  - **Scheduled** — next run is at that job’s configured `ScheduledTimeLocal` (if today’s time already passed, next calendar occurrence per normal scheduler rules)
  - **ManualOnly** — remains idle until **Sync now** (or until user changes mode)
- **Sync now** on a single job: allowed when that job is `Enabled` even if we need a rule for global stop — **v1 rule:** Sync now is **blocked while `JobsArmed == false`** (global stop means nothing runs until Start all). User must Start all jobs first, or we could allow Sync now as override — **default: blocked during global stop** for true “everything quiet.”
- Individual Enable/Disable still available on the job list for finer control once globally armed.

### Sync modes

| Mode | Behavior |
|---|---|
| **Realtime** | Watch source tree; debounced reconcile on changes; plus periodic catch-up pass (only when job enabled and JobsArmed) |
| **Scheduled** | Full reconcile once per day at `ScheduledTimeLocal` (local clock) when enabled and JobsArmed |
| **ManualOnly** | Only when user clicks **Sync now** (and JobsArmed; job Enabled) |

All modes support **Sync now** from the GUI when globally armed and the job is enabled.

### Global job control (normative)

| Command | Effect |
|---|---|
| **Stop all jobs** | `JobsArmed = false` (persisted). Stops automatic activity for every job. Not Exit. |
| **Start all jobs** | `JobsArmed = true` (persisted). Re-enables automatic activity per each job’s mode/schedule/realtime settings. Does not blast “sync everything now.” |
| **Exit** (GUI/tray) | Quits GUI/tray process only. Service keeps running; `JobsArmed` unchanged. |

Default after fresh install: `JobsArmed = true`.

### Delete safety gate

1. Engine builds a **plan** (copies/updates + deletes).  
2. If `deletes.Count > 10`, set job status **WaitingConfirm**, notify tray + GUI, **do not delete**.  
3. User **Confirms** → execute deletes (and any remaining plan ops).  
4. User **Cancels** → discard delete portion (and do not remove those dest files); record cancellation in log; job returns to prior mode.  
5. Non-delete operations may either wait with the plan or apply non-destructive ops first — **v1 decision:** apply **copies/updates first**, hold **deletes only** behind the gate (minimizes “dest missing new files” while waiting).  

### Offline / locked files

- If source or destination root is unreachable: status **Degraded**, exponential backoff retry, **no delete execution**.  
- If individual files locked: skip, log, retry on next pass.  

### GUI requirements

- **Job list** main view: name, paths (abbrev), mode, status, last run, actions (Edit, Sync now, Enable/Disable).  
- **Add / Edit job** dialog: all job fields; schedule time control when mode is Scheduled; validate paths.  
- **Global controls:** **Stop all jobs** and **Start all jobs** clearly visible (toolbar/menu); status indicator when globally stopped (e.g. banner “All jobs stopped”).  
- **Activity / log** panel or window: recent events for selected job.  
- **Large-delete confirmation** dialog listing count (and sample paths if feasible).  
- **Tray:** show window; status hint (OK / attention / all stopped); **Exit**. Optional: Start all / Stop all in tray menu.  
- **GUI Exit:** available from main window menu/UI **and** tray.  
- Close (X) → **hide to tray**, do not exit.  
- Exit ≠ Stop all jobs.

### Autostart

- **Machine-wide Windows Service** starts automatically with Windows (mirroring continues headless when `JobsArmed` and jobs allow).  
- **GUI/tray** starts at user logon (status/confirm UI). If user exits GUI, service keeps running; `JobsArmed` unchanged.  
- If user left **Stop all jobs** active, after reboot the service starts but **does not** run job activity until **Start all jobs**.

---

## Success Criteria

Concrete, testable conditions for “v1 done”:

1. **Multi-job CRUD:** Create ≥2 jobs, edit one job’s schedule (2:00 → 3:00), restart GUI — changes persist.  
2. **Exact mirror:** For a test tree, after sync, destination file set matches source (names + contents for changed files).  
3. **Delete propagation:** Delete a file on source, sync → gone on dest (when ≤10 pending deletes).  
4. **Safety gate:** Stage 11 destination deletes in plan → no deletes until Confirm; Cancel leaves those dest files.  
5. **Sync now:** Manual trigger runs a full reconcile for that job.  
6. **Scheduled:** Job with mode Scheduled runs within a reasonable window of the configured local time (manual clock test or injectable clock in tests).  
7. **Realtime:** Create file in watched source → appears on dest without pressing Sync now (debounce allowed, e.g. ≤30s under normal load).  
8. **Tray + GUI lifecycle:** X hides to tray; restore from tray; **Exit from GUI** and **Exit from tray** both quit the GUI process.  
9. **Stop all / Start all:** Stop all jobs prevents scheduled and realtime activity for every job; Start all jobs restores automatic behavior **per each job’s mode/schedule** without forcing an immediate sync-all; state persists across GUI restart.  
10. **Reboot / service:** With service installed, `JobsArmed == true`, and a job enabled, after reboot **without opening GUI first**, a scheduled or realtime job still performs sync as configured.  
11. **Share blip:** Disconnect dest → job Degraded, no mass-delete; restore share → catch-up without user re-creating job.  
12. **`dotnet test`** passes for Core tests covering mirror plan + safety gate + global arming gates + at least one temp-directory integration test.

---

## Open Questions

| # | Topic | Status |
|---|---|---|
| 1 | Exit GUI stops service? | **Resolved: No** — service keeps running |
| 2 | Exclude patterns in v1? | **Resolved: No** — entire source tree mirrored |
| 3 | Compare method | **Resolved:** Size + last-write time |
| 4 | Apply copies before confirmed deletes? | **Resolved: Yes** |
| 5 | Mapped drive vs UNC | **Resolved:** Both allowed; UNC preferred in docs |
| 6 | Per-user vs machine-wide service | **Resolved: Machine-wide Windows Service** |
| 7 | Stop all jobs persists across reboot? | **Resolved: Yes** — `JobsArmed = false` survives reboot until Start all jobs |
| 8 | Sync now while globally stopped? | **Resolved: No** — Sync now only after jobs have been started globally again (`JobsArmed == true`) |

All previously open defaults are now closed for v1.

---

## Approval

- [x] Human approves this spec (or lists deltas)  
- [x] Open questions accepted or overridden  
- [x] Then Phase 2: Plan → `BackupMirror/tasks/plan.md` (written; await human plan approval)  
- [x] Phase 3: Tasks → `BackupMirror/tasks/todo.md` (written; await human plan approval)  
- [x] Phase 4: Implement (incremental + TDD) — Core + service + GUI landed; elevate install optional

**Spec approved 2026-07-26.** Implementation in progress / core path working.
