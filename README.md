# Backup Mirror

One-way, always-current folder mirroring for **Windows and Linux**, run by a background service
with a modern web dashboard.

- **Exact mirror**: new and changed files are copied, deleted files are removed, the source always wins.
- **Realtime, scheduled or manual** jobs. Realtime picks up changes within seconds and also repairs the
  mirror when someone deletes a file there. Schedules support several times a day on chosen weekdays,
  and a run missed while the machine was off is caught up at start-up.
- **Safe by design**: files are copied to a temp name and swapped in only when complete, transient
  errors are retried, unreadable folders are skipped and reported (their mirrored copies are never
  deleted), and large deletions wait for your review in the dashboard.
- **Compare before you sync** (as in OpenDiff): every planned change listed and colour-coded (green
  copy/create, orange update, red delete), filter by kind or path, uncheck anything, then apply exactly
  what you reviewed.
- **OpenDiff options per job**: *Sync to Source* (mirror matches source) or *Sync to Destination*
  (source matches mirror, e.g. a restore; manual jobs only), include subfolders, include hidden/system
  files (hidden on either side is left out), and copy missing / update changed / delete extra.
- **Keep removed files** for N days: anything a sync deletes or overwrites is moved to a dated
  `.backupmirror-trash` folder on the changed side instead (works on shares and Linux; covers overwrites).
- **Exclusions** with gitignore-style patterns (`node_modules`, `*.tmp`, `cache/**`).
- **Dashboard** at <http://127.0.0.1:8737>: live progress, compact list or card view with search and
  filters, activity history, service log, server-side folder picker (works on headless servers), light
  and dark themes.
- **Windows**: Windows service + tray app (status icon, notifications, native dashboard window that
  remembers its position, size and maximized state).
  Network-share sign-in per job, stored with DPAPI.
- **Linux**: systemd service (`.deb` or tarball), POSIX permissions and ownership preserved, mount
  network shares via `/etc/fstab`.

## Install

| Platform | Package | Command |
|---|---|---|
| Windows 10/11 x64 | `BackupMirror-Setup-<ver>.exe` | run as Administrator (upgrades v1 in place, keeps jobs) |
| Debian / Ubuntu | `backupmirror_<ver>_amd64.deb` | `sudo apt install ./backupmirror_<ver>_amd64.deb` |
| Other systemd distros | `backupmirror-<ver>-linux-x64.tar.gz` | extract, then `sudo ./install.sh` (`./uninstall.sh [--purge]`) |

Packages are self-contained: no .NET runtime needed.

Data lives in `C:\ProgramData\BackupMirror` (Windows) or `/var/lib/backupmirror` (Linux):
`jobs.json` (config), `state.json` (status + history), `logs/service.log`, `access.key`, optional `settings.json`.

## Using it

1. **New mirror**: pick a source and a destination with *Browse* (the picker shows folders as the
   service sees them, including network shares), choose when to sync, and what to sync.
2. **Compare** (on every mirror): see each change the next sync would make, uncheck what you don't
   want, and *Apply checked changes*. Nothing changes until you apply.
3. **Sync now**, or let realtime/scheduled mirrors run. Progress, problems and history show on the
   overview and in *Activity*; the tray icon turns amber or red when something needs you.
4. If a sync would delete more files than the mirror's safety limit, it waits: *Review* shows the list.
5. **Pausing**: *Pause all* (top of the overview) holds every mirror; ••• → *Pause this mirror* holds one.
   A paused mirror shows a green **Resume** button. If everything is paused, resuming one mirror asks
   whether to resume all or only that one.

### Network shares (Windows)

The service runs as the SYSTEM account, which cannot see drive letters you mapped (`X:`). Use the
share's network path instead (`\\nas\backups\Photos`):

- The folder picker lists your mapped drives and the shares you have opened in Explorer under
  **Network shares**, already as network paths.
- Type or paste `X:\…` and the check under the field offers a one-click **Use \\server\share\…** fix.
- If a share needs a password, the picker asks for it and stores it with the mirror (encrypted with
  Windows DPAPI). You can also set it under *Network share sign-in* in the mirror settings.

On Linux, mount shares in `/etc/fstab` (cifs/nfs) and pick the mount folder.

### Remote access to the dashboard

The dashboard listens on `127.0.0.1` only. To open it from another machine, create `settings.json` in
the data folder:

```json
{ "BackupMirror": { "Urls": "http://0.0.0.0:8737" } }
```

Restart the service, browse to `http://<server>:8737` and sign in with the key from `access.key`
(`sudo cat /var/lib/backupmirror/access.key`, or `backupmirror --access-key`). Put it behind a
reverse proxy with TLS if it crosses an untrusted network.

Other settings (same file): `MaxConcurrentJobs` (2), `RealtimeCatchUpMinutes` (5),
`ScheduleCatchUpHours` (12), `ExtraAllowedHosts` (extra host names for the Host-header check).

## Build

Requirements: .NET 8 SDK; Inno Setup 6 for the Windows installer; Linux/WSL with `dpkg-deb` and `rsync`
for Linux packages.

```powershell
dotnet test BackupMirror.sln -c Release            # unit tests (Windows)
.\scripts\build-installer.ps1                      # dist\BackupMirror-Setup-<ver>.exe
```

```bash
bash scripts/linux-test.sh                         # unit tests on Linux / WSL
bash scripts/package-linux.sh [x64|arm64]          # dist/*.deb and dist/*.tar.gz
bash scripts/linux-install-test.sh                 # install the .deb under systemd, test, purge (test machines only)
```

Run the service locally without installing:

```powershell
dotnet run --project src\BackupMirror.Service -- --data .smoke\devdata --BackupMirror:Urls=http://127.0.0.1:8738
```

## Layout

```
src/BackupMirror.Core      planner, engine, exclusions, schedules, stores, secrets (cross-platform)
src/BackupMirror.Service   ASP.NET Core host: REST API + SSE, job runner, watchers, wwwroot dashboard
src/BackupMirror.Tray      Windows tray companion (WebView2 dashboard window)
tests/                     xUnit tests (run on Windows and Linux)
installer/                 Inno Setup script + service helpers
packaging/linux/           systemd unit, sysctl, tarball install scripts
scripts/                   build, publish, packaging, icon
```

Version is set once in `Directory.Build.props`.

## Documentation

- `docs/spec/backup-mirror.md`: behaviour, architecture, API and safety rules (current).
- `docs/intent/backup-mirror.md`: what the product is for and what is out of scope.
- `memory.md`: developer handoff (status, build/test, pitfalls).
- `docs/history/`: the original v1 (WPF) plan, task list and verification log.
