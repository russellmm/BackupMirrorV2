# Backup Mirror — developer handoff

**Last updated:** 2026-11-21 · **Version:** 2.1.4
**Repo:** https://github.com/russellmm/BackupMirrorV2 (public, branch `main`). Started fresh on 2026-11-21
with a single commit so no personal details survive in history; the old repo's full history is kept as a
local git bundle (see `memory.local.md`).
**Private notes:** `memory.local.md` (gitignored; owner's machine, network and credential locations). Read it too.
**Behaviour/API reference:** `docs/spec/backup-mirror.md` · **Purpose/scope:** `docs/intent/backup-mirror.md`
**v1 history (WPF era):** `docs/history/`

---

## Current state

- **Installed on the dev PC:** 2.1.4 in `C:\Program Files\BackupMirror` (service `BackupMirror`, LocalSystem,
  automatic; tray `app\BackupMirror.exe`, started at sign-in). Dashboard http://127.0.0.1:8737.
  Data `C:\ProgramData\BackupMirror`. v1 was uninstalled and its job migrated (details in `memory.local.md`).
- **Packages:** `dist\BackupMirror-Setup-<ver>.exe`, `dist\backupmirror_<ver>_amd64.deb`,
  `dist\backupmirror-<ver>-linux-x64.tar.gz` (dist is gitignored).
- **GitHub release:** v2.1.4 (latest), https://github.com/russellmm/BackupMirrorV2/releases/tag/v2.1.4,
  with the installer, .deb, tarball and `SHA256SUMS-2.1.4.txt`.
  No `gh` CLI here: releases go through the REST API with curl, token fed via `curl -K -` on stdin.
- **Tests:** 77 passing on Windows and on Linux (WSL).

## Layout

```
src/BackupMirror.Core       Mirror/ (planner, engine, plan+options, ExcludeMatcher, TrashBin, DeleteSafetyGate)
                            Models/ (Job, JobRuntime, RunRecord, SyncDirection…), Storage/ (JsonJobStore, RuntimeStore, DataPaths)
                            Scheduling/ScheduleCalculator, Security/SecretProtector, Platform/ (PathRules, UnixMetadata), Network/UncShareConnector
src/BackupMirror.Service    Program.cs; Runtime/ (JobManager, watchers, hosted service, EventHub); Web/ (ApiEndpoints, Dtos,
                            WebSecurity, FileBrowser, NetworkLocations); Logging/; wwwroot/ (index.html, app.js, app.css, login.*, theme.js)
src/BackupMirror.Tray       Program, TrayContext (icon/menu/notifications), DashboardForm (WebView2), ServiceClient
tests/BackupMirror.Core.Tests
installer/ (BackupMirror.iss + service ps1 helpers)   packaging/linux/ (systemd unit, sysctl, install/uninstall.sh)
scripts/ (publish.ps1, build-installer.ps1, package-linux.sh, linux-test.sh, make-icon.py)
```

## Build / test / run

```powershell
dotnet test BackupMirror.sln -c Release           # Windows tests (2 POSIX-only tests no-op here)
.\scripts\build-installer.ps1                     # dist\BackupMirror-Setup-<ver>.exe; never touches the installed service
```
```bash
# From Git Bash on Windows; REPO_WSL = the repo's path inside WSL, e.g. /mnt/<drive>/…/BackupMirror
# (MSYS_NO_PATHCONV stops /mnt paths being mangled; exact path for this PC: memory.local.md):
MSYS_NO_PATHCONV=1 wsl.exe -e bash $REPO_WSL/scripts/linux-test.sh
MSYS_NO_PATHCONV=1 wsl.exe -e bash $REPO_WSL/scripts/package-linux.sh
```

- **Version:** bump `<Version>` in `Directory.Build.props` (shown in dashboard, installer and package names).
- **Dev server:** `backupmirror-dev` in the workspace's `.claude/launch.json` (location: `memory.local.md`) runs the Debug build on port 8738
  with data in `.smoke\devdata` (`dotnet build src/BackupMirror.Service` first). `.smoke/` is gitignored
  scratch space, created empty on first run (the old demo set was deleted 2026-11-21; add jobs pointing at
  test folders under `.smoke/` to rebuild one). wwwroot is copied to `bin`; after editing web files
  without rebuilding, copy them to `bin\Debug\net8.0\wwwroot`.
- **Upgrade the installed copy:** run `dist\BackupMirror-Setup-<ver>.exe /SILENT /SUPPRESSMSGBOXES /NORESTART`
  (UAC prompt for the owner). Data and jobs are kept.
- **Linux install test:** `scripts/linux-install-test.sh` installs the newest `dist/*.deb` under systemd
  (WSL), checks realtime, repair, permissions/ownership, case-sensitive names, CSRF and restart, then
  purges. Sudo prompts, or set `BM_SUDO_PASSWORD_FILE` (this PC's file: see `memory.local.md`):
  `MSYS_NO_PATHCONV=1 wsl.exe -e env BM_SUDO_PASSWORD_FILE=… bash $REPO_WSL/scripts/linux-install-test.sh`
- **Push:** use the `github-push` skill (credentials: see `memory.local.md`). Only `main` exists.
  Commits as `russellmm <russellmm@users.noreply.github.com>`; never use a real name or personal email.
- **The repo is public** (since 2026-11-21): never commit tokens, passwords, job data (`jobs.json`,
  `state.json`), `.smoke/`, `backupmirror_notes.txt`, or the owner's hosts, shares and paths (those go in
  `memory.local.md`). Builds use a PathMap (Directory.Build.props) so binaries don't embed local folder paths.

## Rules that must not regress

1. One-way: the folder being read is never written. Sync to Destination is manual-only.
2. Copies are atomic (`.bmpart` → rename). Never delete under an unreadable source folder.
3. Job config (`jobs.json`) and runtime (`state.json`) stay separate; only the service writes either.
4. Requests during a run queue a re-run; they are never dropped.
5. A new job's destination watcher attaches after the first sync (reconcile after every run). Don't
   lengthen the 1.5 s own-write window: it swallows real deletes in the mirror.
6. Picker roots must not touch the network. List and connect calls have timeouts.
7. Menus are a floating layer on `<body>`; the compact list clips anything inside it.
8. Animations are transform-only: opacity animations stall in background panes and leave content invisible.
9. Installer: `UsePreviousTasks=no` and no `checkedonce` on service/tray tasks (otherwise an upgrade
   leaves the service stopped).
10. Web security: keep the Host allow-list, the `X-BackupMirror` header check and no inline scripts (CSP).

## Pitfalls on this PC

- **Never** `taskkill /IM BackupMirror.Service.exe`: it also kills the installed service. Stop test copies by PID.
- The service is SYSTEM: mapped letters (U: V: W: X: Z:) are invisible to it. Use UNC plus the job's sign-in.
- `.gitignore` once hid `SecretProtector.cs` (`*secret*` rule); keep the narrowed rules.
  `backupmirror_notes.txt` contains a token and is ignored. Never commit it.
- Edit text files with Python/Edit, not PowerShell `Get-Content | Set-Content` (Windows PowerShell 5
  reads UTF-8 as ANSI and garbles ·, →, —).
- Shell files must stay LF (`.gitattributes`); Python edits with backslashes are safer via script files.
- In the browser pane, screenshots of emulated viewports are cropped (DPR 1.5). Use JS geometry checks or `zoom`.

## Release history

- **2.0.0:** web dashboard + REST/SSE service (replaces WPF + pipes), Windows tray, Linux .deb/tarball,
  atomic copies, retries, exclusions, multi-time schedules, delete review, remote access key.
- **2.1.0:** OpenDiff options (Sync to Source/Destination, subfolders, hidden/system, copy/update/delete),
  colour-coded Compare with apply-checked, keep removed files N days; installer upgrade fix.
- **2.1.1:** job ••• menu no longer clipped (floating layer).
- **2.1.2:** network shares in the folder picker (mapped drives + Explorer history as UNC), share
  sign-in from the picker, mapped-letter → UNC suggestion, real error for shares SYSTEM can't open.
- **2.1.4:** tray dashboard window remembers position, size and maximized state
  (`%LOCALAPPDATA%\BackupMirror\window.json`; off-screen positions ignored).
- **2.1.3:** per-mirror Pause/Resume buttons; "Resume all" vs "Only this mirror" when everything is
  paused; "All paused" vs "Paused" labels; docs rewritten for v2.

## Ideas not done

- Checksum compare mode; bandwidth limit; per-job concurrency/priority.
- Email/webhook notifications on failure.
- HTTPS for the remote dashboard (today: reverse proxy).
- Snapshots/version history (only the keep-removed safety net exists).
