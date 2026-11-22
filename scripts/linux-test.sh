#!/usr/bin/env bash
# Runs the test suite on Linux from a native copy of the repo (avoids drvfs permission quirks).
# Usage (WSL or Linux): bash scripts/linux-test.sh
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/backupmirror-test"
mkdir -p "$WORK"
rsync -a --delete --exclude bin --exclude obj --exclude .smoke --exclude publish --exclude dist --exclude .git "$SRC/" "$WORK/"
cd "$WORK"
dotnet test tests/BackupMirror.Core.Tests -c Release --nologo -v q 2>&1 | grep -E "error|Failed |Passed!|Failed!|Expected" || true
