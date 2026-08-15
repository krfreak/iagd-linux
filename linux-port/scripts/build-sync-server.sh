#!/usr/bin/env bash
# Builds the backup server this port talks to, so the cloud tests have something to talk to
# that is not production.
#
# The service is open source (github.com/marius00/IAGD-Onlinesync) and the monolith runs
# standalone: MySQL is only the read-only source for an in-progress migration, and it skips that
# entirely when DATABASE_* is unset. So a full, real server -- the same validation, the same
# SQLite storage, the same login flow -- comes up on localhost with one env var.
#
# That matters more than a mock would. Every previous parity mistake in this port came from
# reading someone's source and believing it; this makes the tests ask the actual implementation.
#
# Writes to build/iagd-onlinesync and prints the binary path. Idempotent: an existing build
# newer than the checkout is reused.

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"

REPO="${IAGD_SYNC_SERVER_REPO:-https://github.com/marius00/IAGD-Onlinesync.git}"
OUT="$ROOT/build/iagd-onlinesync"
BINARY="$OUT/Server/bin/monolith"

if ! command -v go >/dev/null 2>&1; then
    echo "error: go is required to build the test server (pacman -S go)" >&2
    exit 1
fi

if [ ! -d "$OUT/.git" ]; then
    rm -rf "$OUT"
    mkdir -p "$(dirname "$OUT")"
    git clone --depth 1 "$REPO" "$OUT" >&2
fi

if [ ! -x "$BINARY" ] || [ "$OUT/Server/go.mod" -nt "$BINARY" ]; then
    ( cd "$OUT/Server" && mkdir -p bin && go build -o bin/monolith ./endpoints/monolith.go ) >&2
fi

echo "$BINARY"
