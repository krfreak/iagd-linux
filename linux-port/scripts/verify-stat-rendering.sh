#!/usr/bin/env bash
# Asserts every item in a collection renders with the colours upstream gives it.
#
# Upstream has two renderers and picks between them by where a line came from:
#
#   * a line Grim Dawn drew goes through ReplicaStat.tsx, coloured by the row type the game
#     assigned it — 34 is the "Granted Skills" heading, 19 the level requirement, 27 a component.
#   * a line computed from the game database goes through ItemStat.tsx, which has no type to work
#     with and splits the line instead: the leading value in one colour (--item-stat-modifier),
#     what it applies to in another (--item-stat-label), and a modified skill's name in a third.
#
# This port had only the first, so every computed line came out one flat colour — and since only
# items looted with the hook attached carry a captured tooltip, that is nearly every line.
#
# There is nothing stored to check: a computed line is built per request from the game's stat
# rows, so the only way to know a collection renders correctly is to render it. That is what this
# does — every item, through the client's own code, counting how the lines came out.
#
#   IAGD_VERIFY_DB=/path/to/userdata.db  to check some other collection.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"

DB="${IAGD_VERIFY_DB:-${XDG_DATA_HOME:-$HOME/.local/share}/iagd-linux/userdata.db}"
if [ ! -f "$DB" ]; then
    echo "SKIP  Stat rendering: no collection to check ($DB)"
    exit 0
fi

PROBE="$SCRIPT_DIR/search-probe"
if ! out="$(cd "$PROBE" && dotnet build -v q --nologo 2>&1)"; then
    echo "error: could not build the stat probe" >&2
    echo "$out" >&2
    exit 1
fi
PROBE_DLL="$(find "$PROBE/bin" -name searchprobe.dll -print -quit)"
[ -n "$PROBE_DLL" ] || { echo "error: stat probe was not built" >&2; exit 1; }

# Read-only, and on the collection itself: rendering writes nothing.
if ! report="$(dotnet "$PROBE_DLL" --describe "$DB" 2>&1)"; then
    echo "error: rendering the collection failed" >&2
    echo "$report" >&2
    exit 1
fi

field() { printf '%s\n' "$report" | awk -F'\t' -v k="$1" '$1 == k { print $2 }'; }

if [ -n "$(field unavailable)" ]; then
    echo "SKIP  Stat rendering: $(field unavailable)"
    exit 0
fi

captured="$(field captured)"; described="$(field described)"; undescribed="$(field undescribed)"
lines="$(field lines)"; split="$(field split)"; unsplit="$(field unsplit)"
headings="$(field headings)"; skills="$(field skills)"

printf 'ok    %-32s %s items, %s lines\n' "computed tooltips" "${described:-0}" "${lines:-0}"
printf 'ok    %-32s %s captured by the hook\n' "coloured by row type" "${captured:-0}"
printf 'ok    %-32s %s two-tone, %s headings\n' "coloured by split" "${split:-0}" "${headings:-0}"
printf 'ok    %-32s %s lines\n' "skill names in their own colour" "${skills:-0}"

failures=0

# A stat with neither half has nothing to colour and falls back to the container's default,
# which is exactly what the whole collection looked like before this was ported.
if [ "${unsplit:-0}" -gt 0 ]; then
    echo "DRIFT ${unsplit} stat line(s) have no modifier and no label, so they render uncoloured"
    failures=$((failures + 1))
fi

# An item with no lines at all shows a name and a level and nothing else. It means the game's
# stat rows are missing for its records, which the analysis pass fills in.
if [ "${undescribed:-0}" -gt 0 ]; then
    echo "DRIFT ${undescribed} item(s) render no stat lines at all — the analysis pass has not reached them"
    failures=$((failures + 1))
fi

echo
if [ "$failures" -eq 0 ]; then
    echo "OK    Stat rendering matches upstream ($(( ${described:-0} + ${captured:-0} )) items)"
    exit 0
fi
exit 1
