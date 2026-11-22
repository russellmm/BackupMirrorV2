#!/usr/bin/env bash
# Builds Linux packages: dist/backupmirror_<ver>_amd64.deb and dist/backupmirror-<ver>-linux-x64.tar.gz
# Run on Linux or WSL with the .NET 8 SDK and dpkg-deb.   Usage: bash scripts/package-linux.sh [arch: x64|arm64]
set -euo pipefail
ARCH="${1:-x64}"
case "$ARCH" in x64) DEBARCH=amd64 ;; arm64) DEBARCH=arm64 ;; *) echo "arch must be x64 or arm64"; exit 1 ;; esac

SRC="$(cd "$(dirname "$0")/.." && pwd)"
VER="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$SRC/Directory.Build.props")"
WORK="${TMPDIR:-/tmp}/backupmirror-pkg"
rm -rf "$WORK" && mkdir -p "$WORK/src"
# Build from a native copy: drvfs (/mnt/x) breaks file modes and is slow.
rsync -a --exclude bin --exclude obj --exclude .smoke --exclude publish --exclude publish-installer --exclude dist --exclude .git "$SRC/" "$WORK/src/"

echo "=== Publishing service $VER for linux-$ARCH ==="
dotnet publish "$WORK/src/src/BackupMirror.Service/BackupMirror.Service.csproj" -c Release -r "linux-$ARCH" \
  --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o "$WORK/payload" --nologo -v q
chmod 755 "$WORK/payload/BackupMirror.Service"

LNX="$WORK/src/packaging/linux"
OUT="$SRC/dist"
mkdir -p "$OUT"

echo "=== Building .deb ==="
DEB="$WORK/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/backupmirror" "$DEB/lib/systemd/system" "$DEB/usr/lib/sysctl.d" "$DEB/usr/bin"
cp -a "$WORK/payload/." "$DEB/opt/backupmirror/"
install -m 644 "$LNX/backupmirror.service" "$DEB/lib/systemd/system/backupmirror.service"
install -m 644 "$LNX/60-backupmirror.conf" "$DEB/usr/lib/sysctl.d/60-backupmirror.conf"
ln -s /opt/backupmirror/BackupMirror.Service "$DEB/usr/bin/backupmirror"
SIZE_KB="$(du -sk "$DEB" | cut -f1)"
cat > "$DEB/DEBIAN/control" <<EOF
Package: backupmirror
Version: $VER
Section: admin
Priority: optional
Architecture: $DEBARCH
Maintainer: russellmm <https://github.com/russellmm/BackupMirrorV2>
Installed-Size: $SIZE_KB
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl3t64, libicu-dev | libicu74 | libicu76 | libicu72 | libicu70
Homepage: https://github.com/russellmm/BackupMirrorV2
Description: One-way folder mirroring service with a web dashboard
 Keeps exact copies of folders on other disks, NAS mounts or network shares.
 Realtime, scheduled or manual jobs; atomic copies with retries; delete safety
 limits; dashboard at http://127.0.0.1:8737.
EOF
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
mkdir -p /var/lib/backupmirror && chmod 700 /var/lib/backupmirror
sysctl --system >/dev/null 2>&1 || true
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  systemctl enable backupmirror >/dev/null 2>&1 || true
  systemctl restart backupmirror || true
fi
echo "Backup Mirror dashboard: http://127.0.0.1:8737  (remote access: see /var/lib/backupmirror/settings.json)"
EOF
cat > "$DEB/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && [ "$1" = remove ]; then
  systemctl disable --now backupmirror >/dev/null 2>&1 || true
fi
EOF
cat > "$DEB/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
[ -d /run/systemd/system ] && systemctl daemon-reload || true
# Jobs and history are only removed on purge.
if [ "$1" = purge ]; then rm -rf /var/lib/backupmirror; fi
EOF
chmod 755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/prerm" "$DEB/DEBIAN/postrm"
DEBFILE="$OUT/backupmirror_${VER}_${DEBARCH}.deb"
dpkg-deb --root-owner-group --build "$DEB" "$DEBFILE" >/dev/null

echo "=== Building tarball ==="
TARDIR="$WORK/backupmirror-$VER"
mkdir -p "$TARDIR/opt/backupmirror"
cp -a "$WORK/payload/." "$TARDIR/opt/backupmirror/"
cp "$LNX/backupmirror.service" "$LNX/60-backupmirror.conf" "$LNX/install.sh" "$LNX/uninstall.sh" "$TARDIR/"
chmod +x "$TARDIR/install.sh" "$TARDIR/uninstall.sh"
TARFILE="$OUT/backupmirror-${VER}-linux-${ARCH}.tar.gz"
# Record entries as root:root (numeric) so the build machine's user name never ends up in the archive.
tar --owner=0 --group=0 --numeric-owner -C "$WORK" -czf "$TARFILE" "backupmirror-$VER"

ls -la "$DEBFILE" "$TARFILE"
