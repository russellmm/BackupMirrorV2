# Backup Mirror — developer notes

**Version:** 2.1.6 · **Repo:** https://github.com/russellmm/BackupMirrorV2 (branch `main`)
**Behaviour/API reference:** `docs/spec/backup-mirror.md` · **Purpose/scope:** `docs/intent/backup-mirror.md`
**v1 history (WPF era):** `docs/history/`
Machine-specific notes (installed copy, credentials, local paths) belong in an untracked `*.local.md`
file next to this one, never here: this repository is public.

---

## Layout

```
src/BackupMirror.Core       Mirror/ (planner, engine, plan+options, ExcludeMatcher, TrashBin, DeleteSafetyGate)
                            Models/ (Job, JobRuntime, RunRecord, SyncDirection…), Storage/ (JsonJobStore, RuntimeStore, DataPaths)
                            Scheduling/ScheduleCalculator, Security/SecretProtector, Platform/ (PathRules, UnixMetadata), Network/UncShareConnector
src/BackupMirror.Service    Program.cs; Runtime/ (JobManager, watchers, hosted service, EventHub); Web/ (ApiEndpoints, Dtos,
                            WebSecurity, FileBrowser, NetworkLocations); Logging/; wwwroot/ (index.html, app.js, app.css, login.*, theme.js)
src/BackupMirror.Tray       Program, TrayContext (icon/menu/notifications), DashboardForm (WebView2), WindowPlacement, ServiceClient
tests/BackupMirror.Core.Tests
installer/ (BackupMirror.iss + service ps1 helpers)   packaging/linux/ (systemd unit, sysctl, install/uninstall.sh)
scripts/ (publish.ps1, build-installer.ps1, package-linux.sh, linux-test.sh, linux-install-test.sh, make-icon.py)
```

## Build / test / run

```powershell
dotnet test BackupMirror.sln -c Release           # Windows tests (2 POSIX-only tests no-op here)
.\scripts\build-installer.ps1                     # dist\BackupMirror-Setup-<ver>.exe; never touches an installed service
```
```bash
bash scripts/linux-test.sh                        # tests on Linux/WSL from a native copy (non-root)
bash scripts/package-linux.sh [x64|arm64]         # dist/*.deb + dist/*.tar.gz
bash scripts/linux-install-test.sh                # installs the .deb under systemd, tests, PURGES (test machines only)
```
From Git Bash on Windows, run the Linux scripts with `MSYS_NO_PATHCONV=1 wsl.exe -e bash <repo path in WSL>/scripts/…`
(MSYS would otherwise rewrite the `/mnt/...` path). `linux-install-test.sh` prompts for sudo, or reads the
password from `BM_SUDO_PASSWORD_FILE` for unattended runs.

- **Version:** bump `<Version>` in `Directory.Build.props` (shown in dashboard, installer and package names).
- **Dev server:** `dotnet run --project src/BackupMirror.Service -- --data .smoke/devdata --BackupMirror:Urls=http://127.0.0.1:8738`.
  `.smoke/` is gitignored scratch space. wwwroot is copied to `bin`; after editing web files without
  rebuilding, copy them to `bin/Debug/net8.0/wwwroot`.
- **Upgrade an installed copy:** `dist\BackupMirror-Setup-<ver>.exe /SILENT /SUPPRESSMSGBOXES /NORESTART`
  (needs elevation). Data and jobs are kept.
- **Release:** bump the version; run tests on Windows and Linux; build the installer and Linux packages;
  write `dist/SHA256SUMS-<ver>.txt` (`sha256sum` of the three packages); commit and push; create the
  GitHub release (tag `v<ver>` on that commit) with the four files. No `gh` CLI is assumed: the REST API
  with curl works, with the token passed on stdin (`curl -K -`), never on the command line.
  Build after committing, so the version string's commit ID matches the tag.
- **`dist/`** is untracked build output; keep only the current release there (older ones are on GitHub).
- **Commits:** use the GitHub noreply identity, never a real name or personal email.
- **Public repo:** never commit tokens, passwords, job data (`jobs.json`, `state.json`), `.smoke/`, or
  anyone's hosts, shares or local paths. Builds use a PathMap (Directory.Build.props) so binaries don't embed
  local folder paths, and the Linux tarball records files as root (uid 0), not the build user.

## Rules that must not regress

1. A sync never writes the folder it reads (no two-way sync). Sync to Destination is manual-only.
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

## Pitfalls

- On a machine where Backup Mirror is installed, never `taskkill /IM BackupMirror.Service.exe`: it also
  kills the installed service. Stop test copies by PID.
- A silent install (`/SILENT`) doesn't restart the tray app; start `BackupMirror.exe --tray` from the
  install folder afterwards.
- The Windows service runs as SYSTEM: drive letters mapped by a user (e.g. `X:`) are invisible to it.
  Use UNC paths plus the job's share sign-in.
- `.gitignore` once hid `SecretProtector.cs` through a broad `*secret*` rule; keep ignore rules narrow.
- Edit text files with an editor or Python, not PowerShell `Get-Content | Set-Content` (Windows
  PowerShell 5 reads UTF-8 as ANSI and garbles ·, →, —).
- Shell files must stay LF (`.gitattributes`).
- In an embedded browser preview, screenshots of emulated viewports can be cropped at high DPI; use JS
  geometry checks to verify layout.

## Release history

- **2.0.0:** web dashboard + REST/SSE service (replaces WPF + pipes), Windows tray, Linux .deb/tarball,
  atomic copies, retries, exclusions, multi-time schedules, delete review, remote access key.
- **2.1.0:** OpenDiff options (Sync to Source/Destination, subfolders, hidden/system, copy/update/delete),
  colour-coded Compare with apply-checked, keep removed files N days; installer upgrade fix.
- **2.1.1:** job ••• menu no longer clipped (floating layer).
- **2.1.2:** network shares in the folder picker (mapped drives + Explorer history as UNC), share
  sign-in from the picker, mapped-letter → UNC suggestion, real error for shares SYSTEM can't open.
- **2.1.3:** per-mirror Pause/Resume buttons; "Resume all" vs "Only this mirror" when everything is
  paused; "All paused" vs "Paused" labels; docs rewritten for v2.
- **2.1.4:** tray dashboard window remembers position, size and maximized state
  (`%LOCALAPPDATA%\BackupMirror\window.json`; off-screen positions ignored).

- **2.1.5:** wording only. README, intent doc, dashboard help card and empty state, and the
  service/package descriptions describe folder sync/backup with a chosen direction and change kinds
  (not only an exact one-way mirror).

## Ideas not done

- Checksum compare mode; bandwidth limit; per-job concurrency/priority.
- Email/webhook notifications on failure.
- HTTPS for the remote dashboard (today: reverse proxy).
- Snapshots/version history (only the keep-removed safety net exists).
