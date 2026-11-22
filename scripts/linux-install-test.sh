#!/usr/bin/env bash
# End-to-end test of the .deb on a systemd Linux box or WSL with systemd enabled.
# Installs the newest dist/backupmirror_*_amd64.deb, exercises realtime copy, exclusions, mirror repair,
# permissions/ownership, case-sensitive names, CSRF and restart, then PURGES the package again.
# Do not run on a machine where Backup Mirror is installed for real: the purge removes its jobs.
#
# Usage: bash scripts/linux-install-test.sh
#   sudo: prompts as usual; for unattended runs set BM_SUDO_PASSWORD_FILE to a file holding the password.
set -uo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"

if [ -n "${BM_SUDO_PASSWORD_FILE:-}" ]; then
  sudo -S -v < "$BM_SUDO_PASSWORD_FILE" 2>/dev/null || { echo "sudo failed"; exit 1; }
else
  sudo -v || { echo "sudo failed"; exit 1; }
fi

DEB=$(ls "$SRC"/dist/backupmirror_*_amd64.deb | sort -V | tail -1)
echo "package: $(basename "$DEB")"
cp "$DEB" /tmp/bm.deb
sudo dpkg -i /tmp/bm.deb >/tmp/bm-install.log 2>&1 || { sudo apt-get -y -f install >/dev/null 2>&1; }
sleep 3
echo "service: $(systemctl is-active backupmirror)  enabled: $(systemctl is-enabled backupmirror)"
echo "inotify watches: $(sysctl -n fs.inotify.max_user_watches)"
echo "data perms: $(sudo stat -c '%a %U' /var/lib/backupmirror)  key perms: $(sudo stat -c '%a' /var/lib/backupmirror/access.key 2>/dev/null)"

API=http://127.0.0.1:8737/api
H=(-H "X-BackupMirror: 1" -H "Content-Type: application/json")
curl -s $API/status | python3 -c "import json,sys; s=json.load(sys.stdin); print('status:', s['os'], 'v'+s['version'], 'armed', s['armed'], 'data', s['dataRoot'])"

sudo rm -rf /tmp/bm-src /tmp/bm-dst; mkdir -p /tmp/bm-src/sub
echo upper > /tmp/bm-src/Report.txt
chmod 640 /tmp/bm-src/Report.txt
echo lower > /tmp/bm-src/report.txt          # distinct files on Linux
echo "ünïcode" > "/tmp/bm-src/sub/café ☕.txt"
ID=$(curl -s "${H[@]}" -X POST $API/jobs -d '{"name":"install test","sourcePath":"/tmp/bm-src","destinationPath":"/tmp/bm-dst","mode":"realtime","excludes":["*.tmp"]}' | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])")
echo "job: $ID"
sleep 4
echo "after first sync: $(ls /tmp/bm-dst | tr '\n' ' ') | sub: $(ls /tmp/bm-dst/sub)"
echo "owner/mode src: $(stat -c '%U:%G %a' /tmp/bm-src/Report.txt)  dst: $(sudo stat -c '%U:%G %a' /tmp/bm-dst/Report.txt)"
echo new > /tmp/bm-src/added.txt; echo junk > /tmp/bm-src/x.tmp
sleep 5
echo "realtime add: $(test -f /tmp/bm-dst/added.txt && echo copied || echo MISSING)  excluded tmp: $(test -f /tmp/bm-dst/x.tmp && echo COPIED-BAD || echo skipped)"
sudo rm /tmp/bm-dst/added.txt
sleep 6
echo "dest delete repaired: $(test -f /tmp/bm-dst/added.txt && echo yes || echo NO)"
rm /tmp/bm-src/Report.txt
sleep 5
echo "source delete mirrored: $(test -f /tmp/bm-dst/Report.txt && echo NO || echo yes)  lower kept: $(cat /tmp/bm-dst/report.txt)"
curl -s "$API/jobs/$ID" | python3 -c "import json,sys; j=json.load(sys.stdin); print('job status:', j['status'], '-', j['message'])"
curl -s -o /dev/null -w "csrf (no header) -> %{http_code}\n" -X POST $API/sync-all
sudo systemctl restart backupmirror; sleep 3
echo "after restart: $(systemctl is-active backupmirror), jobs persisted: $(curl -s $API/jobs | python3 -c 'import json,sys; print(len(json.load(sys.stdin)))')"
echo "--- purge ---"
sudo dpkg --purge backupmirror >/dev/null 2>&1
echo "after purge: service=$(systemctl is-active backupmirror 2>/dev/null) opt=$(test -d /opt/backupmirror && echo present || echo gone) data=$(sudo test -d /var/lib/backupmirror && echo present || echo gone)"
sudo rm -rf /tmp/bm-src /tmp/bm-dst /tmp/bm.deb
