#!/usr/bin/env bash
# Removes Backup Mirror. Jobs and history in /var/lib/backupmirror are kept unless --purge is given.
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo ./uninstall.sh [--purge]"; exit 1; }
systemctl disable --now backupmirror 2>/dev/null || true
rm -f /etc/systemd/system/backupmirror.service /etc/sysctl.d/60-backupmirror.conf /usr/local/bin/backupmirror
rm -rf /opt/backupmirror
systemctl daemon-reload
if [ "${1:-}" = "--purge" ]; then rm -rf /var/lib/backupmirror; echo "Removed jobs and history."; else echo "Kept /var/lib/backupmirror (jobs, history)."; fi
