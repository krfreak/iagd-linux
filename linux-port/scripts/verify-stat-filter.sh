#!/usr/bin/env bash
# Asserts our stat filter still matches upstream's, entry for entry.
#
# The seed engine draws from a shared random stream in a fixed field order. Feeding it one
# field the game did not roll inserts an extra draw and silently changes every value after
# it. So these two lists -- upstream's SpecialIgnores and SpecialStats -- are part of the
#contract, not configuration.
#
# check-upstream.sh reports that the file changed; this says whether the change matters.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
SOURCE="$(dirname "$LINUX_PORT")/iagd/IAGrim/Database/DAO/DatabaseItemStatDaoImpl.cs"

[ -f "$SOURCE" ] || { echo "error: upstream file not found: $SOURCE" >&2; exit 1; }

extract() {
    python3 - "$SOURCE" "$1" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
m = re.search(r'private static readonly string\[\] ' + sys.argv[2] + r'\s*=\s*\{(.*?)\};', src, re.S)
if not m:
    sys.exit(f"could not find {sys.argv[2]} in upstream source")
for value in re.findall(r'"([^"]+)"', m.group(1)):
    print(value.lower())
PY
}

ours() {
    python3 - "$LINUX_PORT/src/IAGrim.Core/ItemStats/StatFilter.cs" "$1" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
m = re.search(r'HashSet<string> ' + sys.argv[2] + r' = new\(StringComparer\.OrdinalIgnoreCase\) \{(.*?)\};', src, re.S)
if not m:
    sys.exit(f"could not find {sys.argv[2]} in StatFilter.cs")
for value in re.findall(r'"([^"]+)"', m.group(1)):
    print(value.lower())
PY
}

status=0
for pair in "SpecialIgnores:Blacklist" "SpecialStats:Whitelist"; do
    upstream_name="${pair%%:*}"; our_name="${pair##*:}"

    up="$(extract "$upstream_name" | sort)" || exit 1
    mine="$(ours "$our_name" | sort)" || exit 1

    if [ "$up" = "$mine" ]; then
        echo "OK    $our_name matches upstream $upstream_name ($(echo "$up" | wc -l) entries)"
    else
        echo "DRIFT $our_name no longer matches upstream $upstream_name"
        comm -23 <(echo "$up") <(echo "$mine") | sed 's/^/        upstream only: /'
        comm -13 <(echo "$up") <(echo "$mine") | sed 's/^/        ours only:     /'
        status=1
    fi
done

if [ $status -ne 0 ]; then
    echo
    echo "These lists gate what reaches the seed engine. A mismatch changes computed stat"
    echo "values silently -- port the change before trusting any numeric filter."
fi
exit $status
