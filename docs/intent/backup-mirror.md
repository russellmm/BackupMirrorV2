# Intent: Backup Mirror

**Status:** Confirmed (v1 2026-07-26; revised for v2 2026-11-21; wording updated for 2.1 options)
**Spec:** [../spec/backup-mirror.md](../spec/backup-mirror.md)

## Outcome

Keep copies of chosen folders on another disk, a NAS or a network share, on the owner's Windows 11
workstation **and on bare-metal Linux servers**. Each sync reads one side and writes the other. By
default the source is the truth and adds, updates, renames and deletes propagate (an exact mirror);
per mirror, the owner can instead sync the other way (restore) or limit which kinds of change are made
(e.g. a backup that never deletes).

- **User:** the owner only (personal and home-lab use, not a multi-user product).
- **Why:** a durable second copy that can be trusted without manual copy rituals or babysitting.
- **Success:** quiet reliability. After reboots and network blips jobs resume and catch up; status is
  trustworthy at a glance; when inspected, the mirror matches the source; nothing surprising is deleted.

## Constraints

- Always-on background service that starts at boot (Windows service / systemd).
- A good-looking, modern UI that scales to many jobs: overview at a glance, compact list, search and
  filters, easy editing (e.g. change 02:00 → 03:00 without recreating a job).
- Works headless: the UI is a web dashboard served by the service; on Windows a tray app shows it in
  its own window.
- Per-job modes: realtime, scheduled (times of day on chosen weekdays), manual (Sync now).
- Global Pause/Resume all, plus pausing a single mirror.
- Safety: hold for review when a run would delete more than a per-job number of files (default 10).
- OpenDiff parity (owner's own compare/sync tool): Sync using Source / Sync using Destination, include
  subfolders, include hidden/system, copy missing / update changed / delete extra, and a colour-coded
  **Compare** preview before applying.
- Network shares: per-job share sign-in on Windows (the service cannot use the user's mapped drive
  letters); OS mounts on Linux.

## Out of scope

- Bidirectional (two-way) sync and conflict resolution.
- Cloud storage targets.
- Full-disk / system imaging.
- Full version history / snapshots. (A short "keep removed files for N days" safety net is in scope.)
- Multi-user / team features and role-based access.

## Changes from v1

v1 ruled out exclusions, stored share credentials and Linux. All three are now in. The WPF GUI and
named-pipe IPC were replaced by the web dashboard and REST API. The v1 documents are in
[`docs/history/`](../history/).
