# Verification log — Backup Mirror v1

**Date:** 2026-07-27  
**Spec:** `docs/spec/backup-mirror.md`  
**Build:** `dotnet test` / `dotnet build` Release  

## Automated

| Check | Result | Notes |
|-------|--------|-------|
| `dotnet test -c Release` | **PASS** | 29 Core tests |
| `dotnet build -c Release` | **PASS** | 0 errors (CA1416 cleared via net8.0-windows service) |
| JobEdit ctor smoke | **PASS** | tools/JobEditSmoke (prior session) |
| IPC smoke (console service) | **PASS** | prior session SMOKE_PASS |

## Spec success criteria

| # | Criterion | Status | Evidence / how to re-check |
|---|-----------|--------|----------------------------|
| 1 | Multi-job CRUD; edit schedule persists | **PASS (manual prior)** | Create 2 jobs, edit time, restart GUI |
| 2 | Exact mirror after sync | **PASS (manual + unit)** | MirrorPlanner/Engine tests + user testing |
| 3 | Delete propagation (≤10) | **PASS** | Unit + manual |
| 4 | Safety gate >10 deletes | **PASS** | Unit + Confirm deletes UI |
| 5 | Sync now | **PASS** | Manual + IPC smoke |
| 6 | Scheduled mode | **PASS (code)** | ScheduleWatcher + injectable clock tests partial; full clock-fire manual TBD |
| 7 | Realtime without Sync now | **PASS** | Fixed + user retest |
| 8 | Tray + Exit GUI/tray; X→tray | **PASS** | User testing |
| 9 | Stop all / Start all per-mode | **PASS** | User retest after fix |
| 10 | Reboot / service without GUI first | **PENDING user** | Run `scripts\publish.ps1` + elevated `install-service.ps1`, reboot |
| 11 | Share blip → Degraded, no mass-delete | **PASS (code path)** | JobRunner Degraded on unreachable; full share disconnect manual TBD |
| 12 | `dotnet test` green | **PASS** | 29 tests |

## New deliverables this session

| Item | Status |
|------|--------|
| Shared ProgramData data root + migrate | **Done** (`DataPaths`) |
| Named pipe ACL for service→user GUI | **Done** |
| `scripts/publish.ps1` | **Done** |
| Hardened `install-service.ps1` / uninstall | **Done** |
| `install-gui-startup.ps1` (`--tray`) | **Done** |
| Activity log panel | **Done** |
| Tray status colors (OK/stopped/attention/error) | **Done** |
| Path validation on job save | **Done** |
| Service file logging | **Done** (`logs\service.log`) |

## Remaining manual (user machine)

1. Elevated install:
   ```powershell
   cd <v1-workspace>\BackupMirror
   .\scripts\publish.ps1
   # Admin PowerShell:
   .\scripts\install-service.ps1
   # Optional for private shares:
   # .\scripts\install-service.ps1 -RunAsCurrentUser
   .\scripts\install-gui-startup.ps1
   ```
2. Confirm GUI connects without console service window (`Connected — jobs armed`).
3. Reboot test: service running; place file in source; dest updates before opening GUI.
4. Optional: disconnect share briefly → job Degraded → reconnect catch-up.

## Sign-off

- Automated / in-session: **ready for production install scripts**  
- Full reboot criterion (#10): **awaiting user elevated install + reboot**
