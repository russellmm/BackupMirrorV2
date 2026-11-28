# Spec: Backup Mirror v2

**Status:** Implemented (2.1.5, 2026-11-21)
**Intent:** [../intent/backup-mirror.md](../intent/backup-mirror.md) · v1 spec: [../history/v1-spec.md](../history/v1-spec.md)

## 1. Architecture

```
 Browser / tray window ──HTTP + SSE──▶ BackupMirror.Service (ASP.NET Core, net8.0)
                                        ├─ JobManager      runs, queue, status, compare sessions
                                        ├─ RealtimeWatchManager / ScheduleWatcher
                                        ├─ Web: ApiEndpoints, WebSecurity, FileBrowser, NetworkLocations
                                        └─ BackupMirror.Core (planner, engine, stores, secrets)
```

| Project | Target | Role |
|---|---|---|
| `BackupMirror.Core` | net8.0 | Planning, copying, exclusions, schedules, stores, secrets. No OS-specific types in its API. |
| `BackupMirror.Service` | net8.0, Microsoft.NET.Sdk.Web | Windows service or systemd unit (`Type=notify`); serves `wwwroot` (dashboard) and `/api`. |
| `BackupMirror.Tray` | net8.0-windows | Tray icon, notifications, WebView2 dashboard window (`BackupMirror.exe`, `--tray` = start hidden). Window position/size/maximized saved per user in `%LOCALAPPDATA%\BackupMirror\window.json`; ignored if off every screen. |

The dashboard is plain HTML/CSS/JS (no build step, no CDN), so it works offline and on headless servers.

## 2. Data

Data root: `C:\ProgramData\BackupMirror` (Windows service), `/var/lib/backupmirror` (Linux service),
per-user folder otherwise; override with `BACKUPMIRROR_DATA` or `--data <path>`.

| File | Content |
|---|---|
| `jobs.json` | Job configuration only. Atomic writes (temp + rename) with `.bak`; a corrupt file falls back to the backup, never to an empty list. |
| `appstate.json` | `jobsArmed` (global Pause/Resume all), survives reboot. |
| `state.json` | Per-job runtime (status, message, last run, pending deletes) and the last 500 runs. Owned by the service, so editing a job can never race a run. |
| `access.key` | Key for remote dashboard sign-in (0600 on Linux). |
| `secret.key` | Linux only: AES-256-GCM key for share passwords (0600). Windows uses DPAPI LocalMachine. |
| `settings.json` | Optional overrides (`BackupMirror:Urls`, `MaxConcurrentJobs`, …). |
| `logs/service.log` | Rotated at 5 MB, 5 files. |

v1 `jobs.json` (status fields, single `scheduledTimeLocal`) migrates automatically on first start.

### Job

`name, enabled, sourcePath, destinationPath, destinationUserName, destinationPasswordProtected, mode
(realtime | scheduled | manualOnly), schedule {times[], days[]}, excludes[], deleteConfirmThreshold
(-1 = never ask), direction, includeSubfolders, includeHidden, copyMissing, updateChanged, deleteExtra,
keepRemovedDays`.

Validation: name/paths required; source and destination must not contain each other; scheduled jobs
need a time; at least one change kind; `direction = destinationToSource` only with `mode = manualOnly`.

## 3. Sync behaviour

**Direction.** `sourceToDestination` ("Sync to Source", default): the destination is changed to match
the source. `destinationToSource` ("Sync to Destination"): the source is changed to match the
destination (restore). The folder being read is never written.

**Planning** (`MirrorPlanner`): walk both trees; match by relative path (case-insensitive on
Windows/macOS, case-sensitive on Linux). A file *differs* when size differs or modified times are more
than 2 s apart. Produces create-folder, copy (missing), update (changed), delete (extra) and
remove-folder operations, limited by the job options:

- **Include subfolders** off: only files directly in the root.
- **Include hidden/system** off: an entry is left out when it, or a folder above it, is hidden or system
  on *either* side (on Linux/macOS, dot-files are hidden).
- **Exclusions**: gitignore-style; a pattern without `/` matches any path segment, with `/` it is
  anchored at the root; `**` crosses folders. Applied to both sides (excluded files are never deleted).
- Junctions and symlinked folders are never followed.
- A folder that cannot be read is reported as a warning, and nothing under it is deleted.
- Always excluded: `*.bmpart` (in-flight temp files, cleaned up) and `.backupmirror-trash`.

**Executing** (`MirrorEngine`): create folders → copy/update → delete files → remove empty folders.
Each copy goes to `name.bmpart` and is renamed over the target when complete, with the source's
modified time. On Linux, POSIX mode is preserved, and owner/group too when running as root. Transient
I/O errors are retried 3× with back-off. A source file that vanished mid-run is skipped silently. The
source is opened with sharing for read/write/delete, so open files can be copied.

**Delete safety.** If a run would delete more files than `deleteConfirmThreshold`, copies and updates
still happen but deletes wait (`waitingConfirm`); the dashboard lists them for review (Delete / Keep
for now).

**Keep removed files.** With `keepRemovedDays > 0`, files a run deletes or overwrites are moved to
`<changed root>/.backupmirror-trash/<yyyy-MM-dd_HHmmss>/<relative path>`; folders older than N days are
purged at the start of each run.

**Triggers.**
- *Realtime*: file-system watcher on the source (debounce 2 s, at most 30 s); a destination watcher
  repairs files deleted in the mirror; full catch-up every 5 min; a watcher overflow or error restarts
  the watcher and does a full sync.
- *Scheduled*: times of day on chosen weekdays; a missed run (machine off) is caught up once within 12 h.
- *Manual*: Sync now or Compare → Apply.
- A request arriving during a run queues one re-run, so no change is lost. At most 2 jobs run at once.

**Pausing.** *Pause all* (`jobsArmed = false`) stops every run and trigger. A single mirror is paused
with `enabled = false`. Resuming one mirror while everything is paused offers "Resume all" or "Only
this mirror" (the others are then paused individually).

## 4. Compare

`POST /api/jobs/{id}/compare` plans with the job's options or per-request overrides, without changing
anything. The response lists items `{k, p, s, t}` with k = 0 create folder, 1 copy, 2 update, 3 delete,
4 remove folder, plus counts and bytes. The session is kept for 30 minutes.
`POST /api/compare/{id}/apply {skip:[indexes]}` runs exactly that plan, minus unchecked items, as a
normal run (trigger `compare`, deletes confirmed). Changing options in the window clears the list.

## 5. API

All under `/api`; JSON camelCase; enums as camelCase strings. Mutating calls need header
`X-BackupMirror: 1`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/status` | version, machine, OS, armed, all jobs (with live progress) |
| GET | `/events` | Server-Sent Events: `job`, `jobRemoved`, `state`, `progress`, `run`, `compareProgress` |
| POST | `/arm` | `{armed}`: Pause/Resume all |
| GET/POST | `/jobs` | list / create |
| GET/PUT/DELETE | `/jobs/{id}` | read / update (password: null keeps, "" clears) / delete (files untouched) |
| POST | `/jobs/{id}/enabled` | pause/resume one mirror |
| POST | `/jobs/{id}/sync`, `/cancel` | run now / stop |
| GET/POST | `/jobs/{id}/pending-deletes`, `/confirm-deletes`, `/skip-deletes` | delete review |
| POST | `/jobs/{id}/compare`, `/compare/{cid}/apply` | Compare |
| POST | `/sync-all` | run every enabled mirror |
| GET | `/history?jobId=&limit=` | run history |
| GET | `/fs/roots` | drives/mounts, plus network shares known to signed-in users (Windows) |
| POST | `/fs/list` | `{path, userName?, password?, jobId?}`: folders; connects to a share with the given or saved sign-in (a job without one borrows the sign-in saved on another job for the same share, else the same server); 15 s timeout |
| POST | `/fs/check` | `{path, kind, …}`: exists/writable/free space; `suggestion` = UNC path for a mapped drive letter |
| POST | `/fs/mkdir` | create a folder (picker) |
| GET | `/logs?lines=` | tail of the service log |
| POST | `/login`, `/logout` | remote sign-in with the access key |

## 6. Network shares on Windows

The service runs as LocalSystem, which cannot see drive letters a user mapped. `NetworkLocations` reads,
for each loaded user profile, `HKU\<sid>\Network` (persistent mapped drives) and
`…\Explorer\MountPoints2` (`##server#share`, shares opened in Explorer). The picker lists these by UNC
path without touching the network; a `X:\…` path gets a one-click UNC suggestion. Before a run (and
when browsing) the service connects to `\\server\share` with the job's user/password
(`WNetAddConnection2`).

## 7. Security

- Listens on `127.0.0.1:8737` by default; loopback clients are trusted.
- Host-header allow-list (localhost, IP literals, machine name, `ExtraAllowedHosts`) against DNS rebinding.
- Mutating API calls require `X-BackupMirror` (blocks cross-site form/CSRF).
- Non-loopback clients must sign in with `access.key` (HttpOnly, SameSite=Strict cookie).
- CSP: scripts from self only (no inline script); frames denied.
- Share passwords: DPAPI (Windows) / AES-GCM with a 0600 key (Linux); never returned by the API.

## 8. Packaging

- Windows: Inno Setup, self-contained win-x64; service + tray; upgrades in place (stops the service
  first, always restarts it; `UsePreviousTasks=no`); keeps ProgramData.
- Linux: self-contained linux-x64/arm64 `.deb` (systemd unit, sysctl raising inotify watches,
  `/usr/bin/backupmirror`) and tarball with `install.sh` / `uninstall.sh [--purge]`.

## 9. Testing

`tests/BackupMirror.Core.Tests` (xUnit + FluentAssertions, 77 tests) runs on Windows and on Linux/WSL
(`scripts/linux-test.sh`, non-root so permission tests run). It covers the planner, engine, exclusions,
options, keep-removed, schedules, stores and migration, and secrets. The service, dashboard and
packages are verified end to end: the dev server in the browser, the installed service as LocalSystem,
and the `.deb` under systemd in WSL.
