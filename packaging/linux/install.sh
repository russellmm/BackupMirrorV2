#!/usr/bin/env bash
# Installs Backup Mirror from the extracted tarball (any systemd-based distro).
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo ./install.sh"; exit 1; }
HERE="$(cd "$(dirname "$0")" && pwd)"
systemctl stop backupmirror 2>/dev/null || true
rm -rf /opt/backupmirror
mkdir -p /opt/backupmirror /var/lib/backupmirror
cp -a "$HERE/opt/backupmirror/." /opt/backupmirror/
chmod 755 /opt/backupmirror/BackupMirror.Service
chmod 700 /var/lib/backupmirror
install -m 644 "$HERE/backupmirror.service" /etc/systemd/system/backupmirror.service
install -m 644 "$HERE/60-backupmirror.conf" /etc/sysctl.d/60-backupmirror.conf
ln -sf /opt/backupmirror/BackupMirror.Service /usr/local/bin/backupmirror
sysctl --system >/dev/null 2>&1 || true
systemctl daemon-reload
systemctl enable --now backupmirror
echo
echo "Backup Mirror is running.  Dashboard: http://127.0.0.1:8737"
echo "Remote access: set \"BackupMirror\": { \"Urls\": \"http://0.0.0.0:8737\" } in /var/lib/backupmirror/settings.json,"
echo "               restart (systemctl restart backupmirror) and sign in with: sudo cat /var/lib/backupmirror/access.key"
