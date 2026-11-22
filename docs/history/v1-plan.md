# Implementation Plan: Backup Mirror (Windows 11)

**Spec:** [docs/spec/backup-mirror.md](../../docs/spec/backup-mirror.md)  
**Intent:** [docs/intent/backup-mirror.md](../../docs/intent/backup-mirror.md)  
**Status:** Approved — implementation in progress  
**Date:** 2026-07-26  
**Approved:** 2026-07-26

## Overview

Build a personal Windows 11 one-way folder mirror tool: machine-wide Windows Service runs the engine; WPF GUI + tray is the control plane over named-pipe IPC. Multi-job true mirror (source wins), realtime / scheduled / Sync now, global Start all / Stop all (`JobsArmed`, persisted), and confirm before destination deletes of more than 10 files.

## Architecture Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Stack | .NET 8, WPF, Worker Service | Native Win11, testable Core, solid service + tray story |
| Process split | Service = engine; App = UI/tray | Closing GUI must not stop mirrors; machine-wide autostart |
| IPC | Named pipe + JSON DTOs | Simple, local-only, enough for v1 |
| Compare | Size + last-write time | Fast; matches spec |
| Plan then execute | Pure `PlanAsync` then `ExecuteAsync` | Enables safety gate + unit tests without touching disk until apply |
| Deletes gate | Hold deletes only; copies/updates first | Dest stays useful while waiting for confirm |
| Global pause | `AppState.JobsArmed` persisted | Stop all ≠ Exit; survives reboot |
| Config location | `%LOCALAPPDATA%\BackupMirror\` | Jobs + app state + logs; service must run in a context that can read this (document: configure service logon / path — see Risks) |
| Bulk I/O | System.IO first; Robocopy optional later | Keep Core pure and testable in v1 |

### Dependency graph

```text
Core models + JSON store
    │
    ├── Mirror planner (diff)
    │       │
    │       ├── Safety gate + execute
    │       │       │
    │       │       └── JobsArmed / Enabled eligibility
    │       │
    │       └── xUnit temp-FS tests
    │
    ├── IPC contracts
    │       │
    │       ├── Service host (console → Windows Service)
    │       │       ├── Scheduler
    │       │       ├── FileSystemWatcher (realtime)
    │       │       └── IPC server
    │       │
    │       └── WPF App (IPC client)
    │               ├── Job list + edit
    │               ├── Start/Stop all, Sync now
    │               ├── Delete confirm UI
    │               └── Tray + Exit / close-to-tray
    │
    └── Install scripts + README (machine-wide service)
```

### Vertical slice order (not horizontal layers only)

1. **Foundation:** solution, Core models, persistence, planner + gate + execute + tests  
2. **Headless runtime:** eligibility, service loop, IPC, schedule + realtime + Sync now over IPC (console host)  
3. **Control plane UI:** job CRUD, global arming, confirm deletes, tray lifecycle  
4. **Ship path:** Windows Service packaging, autostart, reboot/manual verification  

## Task List (summary)

See `tasks/todo.md` for full acceptance criteria. Summary:

### Phase 1: Foundation
- Task 1: Solution + project skeleton  
- Task 2: Domain models + JSON job/app-state store  
- Task 3: Mirror planner (file inventory + diff)  
- Task 4: Delete safety gate + execute apply  
- Task 5: Eligibility (`Enabled` ∧ `JobsArmed`) + Sync-now rules  

### Checkpoint: Foundation
- `dotnet test` green; planner/execute/gate covered on temp dirs  

### Phase 2: Headless runtime
- Task 6: IPC contracts + in-process fakes for tests  
- Task 7: Service host console mode + IPC server wiring  
- Task 8: Job runner orchestration (queue, status, confirm tokens)  
- Task 9: Scheduled trigger  
- Task 10: Realtime watcher + debounce  

### Checkpoint: Headless
- Console service: create jobs via test client or minimal harness; scheduled/realtime/manual paths work; Stop all blocks  

### Phase 3: GUI
- Task 11: WPF shell + tray + Exit / close-to-tray  
- Task 12: Job list + add/edit dialog + persistence via IPC  
- Task 13: Sync now, Start all, Stop all + armed banner  
- Task 14: WaitingConfirm UX + confirm/cancel deletes  

### Checkpoint: GUI
- Full control plane against local console/service host  

### Phase 4: Install & verification
- Task 15: Windows Service install scripts + README  
- Task 16: Spec success-criteria manual checklist pass  

### Checkpoint: Complete
- Spec success criteria 1–12 satisfied or waived with reason  

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Service account cannot see user `%LOCALAPPDATA%` or mapped drives | High | Prefer UNC paths; document service Log On as user or store config under ProgramData with explicit path; spike early in Task 7 |
| FileSystemWatcher misses events on network/unstable trees | Med | Periodic catch-up pass for realtime jobs |
| Large trees slow full reconcile | Med | Stream inventories; don’t load entire file bodies; optimize later if needed |
| Named pipe permissions for service ↔ user GUI | Med | Create pipe with ACL for interactive user / same account; test under real service install |
| Partial copy then crash | Med | Copy to temp name then replace where practical; log failures; next pass retries |
| Robocopy vs managed I/O divergence | Low | v1 managed I/O only; revisit only if perf forces it |

## Parallelization

| Parallel-safe after contract exists | Sequential |
|---|---|
| GUI shell (Task 11) vs scheduler/watcher (9–10) once IPC + runner exist | Tasks 1→5 foundation |
| Docs/scripts (15) while GUI polish (14) | Service install validation needs working host |

## Open Questions (plan-level)

None blocking — spec open questions are resolved.  
**Spike note:** Confirm service identity vs config path during Task 7; if blocked, switch config root to `C:\ProgramData\BackupMirror\` (ask before changing spec).

## Verification (plan gate)

Before Phase 4 implementation:

- [ ] Human approves this plan  
- [ ] Every task in `todo.md` has acceptance + verify  
- [ ] Dependencies ordered  
- [ ] No task intentionally > ~5 files without being split  
