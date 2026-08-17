#!/usr/bin/env bash
# Prints the version the next release should carry: today's date, plus a counter.
#
#   2026.08.17.1    first release today
#   2026.08.17.2    second
#   2026.08.18.1    tomorrow, counting from one again
#
# The counter exists because the old scheme was the date alone, which meant a second release on
# the same day had nowhere to go — the tags v2026.08.17a through v2026.08.17d are what that looks
# like when a person is inventing suffixes by hand at the point of release.
#
# The counter is derived from the tags that already exist rather than from a file, so nothing has
# to be committed to cut a release and two releases can never claim the same number. Tags in the
# old shapes are ignored: they do not parse as a counter, and inventing one for them would be
# guessing at history.
#
#   ./scripts/next-version.sh            the next version
#   ./scripts/next-version.sh --tag      the same thing as a tag name, with the v
#
# Requires the tags to be present: a shallow clone without them will happily print .1 and
# collide. CI checks out with fetch-depth 0 for exactly this reason.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

TODAY="$(date +%Y.%m.%d)"

# Highest counter already used today, or 0. `git tag` is asked for an exact shape so that a tag
# like v2026.08.17c cannot contribute a bogus number; sort -n rather than sort's version mode
# because the field is a plain integer and this is one comparison, not a version ordering.
highest=0
while read -r counter; do
    [ -n "$counter" ] || continue
    case "$counter" in
        ''|*[!0-9]*) continue ;;                # not a counter; some other tag shape
    esac
    [ "$counter" -gt "$highest" ] && highest="$counter"
done < <(git tag --list "v$TODAY.*" 2>/dev/null | sed "s/^v$TODAY\.//")

next="$((highest + 1))"

if [ "${1:-}" = "--tag" ]; then
    echo "v$TODAY.$next"
else
    echo "$TODAY.$next"
fi
