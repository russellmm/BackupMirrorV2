# Backup Mirror

Folder sync and backup for **Windows and Linux**: a background service keeps copies of your folders on
another disk, a NAS or a network share, and a web dashboard lets you set it up, compare and watch it.

Each mirror is a pair of folders, a **source** and a **destination**, and you decide what a sync does:

- **Which way.** *Sync to Source* changes the destination to match the source (the normal backup).
  *Sync to Destination* changes the source to match the destination, e.g. to restore from a backup
  (run by hand only, never automatically).
- **What changes.** Any mix of *copy missing files*, *update changed files* and *delete extra files*.
  With all three on, the result is an exact mirror; turn *delete extra* off for a backup that only
  ever adds and updates.
- **What's included.** Subfolders on or off, hidden/system files on or off, and gitignore-style
  exclusions (`node_modules`, `*.tmp`, `cache/**`).
- **When.** Realtime (changes picked up within seconds; files deleted in the copy are put back),
  scheduled (several times a day on chosen weekdays; a run missed while the machine was off is caught
  up at start-up) or manual.

A sync always reads one folder and writes the other. It is not two-way sync: changes made on both
sides are not merged.

## Features

- **Compare before you sync**: every planned change listed and colour-coded (green copy/create,
  orange update, red delete), filter by kind or path, uncheck anything, then apply exactly what you
  reviewed. Nothing changes until you apply.
- **Safe by design**: files are copied to a temp name and swapped in only when complete, transient
  errors are retried, folders that can't be read are skipped and reported (nothing under them is
  deleted), and a run that would delete more files than the mirror's safety limit waits for your review.
- **Keep removed files** for N days: anything a sync deletes or overwrites is moved to a dated
  `.backupmirror-trash` folder on the changed side instead (works on shares and Linux).
- **Dashboard** at <http://127.0.0.1:8737>: live progress, compact list or card view with search and
  filters, pause/resume all or one mirror, activity history, service log, a folder picker that browses
  as the service sees it (works on headless servers), light and dark themes.
- **Windows**: Windows service + tray app (status icon, notifications, dashboard window that remembers
  its position, size and maximized state). Per-mirror network-share sign-in, stored with DPAPI; mapped
  drives and recently used shares are offered in the picker.
- **Linux**: systemd service (`.deb` or tarball), POSIX permissions and ownership preserved, network
  shares via `/etc/fstab` mounts.

## Install

Download from [Releases](https://github.com/russellmm/BackupMirrorV2/releases).

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
   service sees them, including network shares), choose when to sync, and under *What to sync* the
   direction, change kinds and filters. The editor tells you whether the result is an exact mirror.
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
