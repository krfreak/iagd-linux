#!/usr/bin/env bash
# Asserts every entry on upstream's help page has been looked at.
#
# The Help tab is built from upstream's page at build time, and inclusion is the default: an
# entry upstream adds shows up here without anyone deciding it should. That is the right default
# for a port — it keeps the page current — but it means a new entry about a feature this port
# does not have, or advice that only works on Windows, would be presented to a user as fact.
#
# So each of upstream's tags has to appear in help-notes.json under exactly one of:
#
#   keep      correct here exactly as upstream wrote it
#   notes     correct, with a line added for the Linux path or button
#   exclude   would be wrong here, and why
#
# Anything else is an entry nobody has read yet.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UPSTREAM="$(dirname "$LINUX_PORT")/iagd"
HELP_TSX="$UPSTREAM/WebUI/src/containers/help/Help.tsx"
NOTES="$LINUX_PORT/src/WebUI/help-notes.json"

if [ ! -f "$HELP_TSX" ]; then
    echo "SKIP  Help page: upstream's is not checked out ($HELP_TSX)"
    exit 0
fi
[ -f "$NOTES" ] || { echo "error: help-notes.json not found: $NOTES" >&2; exit 1; }

upstream_tags="$(grep -oE "tag: '[^']+'" "$HELP_TSX" | sed "s/tag: '//; s/'$//" | LC_ALL=C sort -u)"
classified="$(python3 -c "
import json, sys
notes = json.load(open('$NOTES'))
tags = set(notes.get('keep', {})) | set(notes.get('notes', {})) | set(notes.get('exclude', {}))
print('\n'.join(sorted(tags)))
")" || { echo "error: could not read $NOTES" >&2; exit 1; }

unreviewed="$(comm -23 <(echo "$upstream_tags") <(echo "$classified" | LC_ALL=C sort))"
stale="$(comm -13 <(echo "$upstream_tags") <(echo "$classified" | LC_ALL=C sort))"

kept="$(echo "$classified" | wc -l)"
excluded="$(python3 -c "
import json; print(len(json.load(open('$NOTES')).get('exclude', {})))
")"

failures=0

if [ -n "$unreviewed" ]; then
    echo "DRIFT upstream has help entries nobody has classified:"
    echo "$unreviewed" | sed 's/^/        /'
    echo "      add each to keep, notes or exclude in src/WebUI/help-notes.json"
    failures=$((failures + 1))
fi

if [ -n "$stale" ]; then
    echo "DRIFT help-notes.json classifies entries upstream no longer has:"
    echo "$stale" | sed 's/^/        /'
    failures=$((failures + 1))
fi

if [ "$failures" -eq 0 ]; then
    echo "OK    Help page reviewed against upstream ($(echo "$upstream_tags" | wc -l) entries, $excluded not applicable)"
    exit 0
fi
exit 1
