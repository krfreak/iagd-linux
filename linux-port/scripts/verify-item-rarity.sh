#!/usr/bin/env bash
# Asserts our rarity logic still matches upstream's, rule for rule.
#
# "Rarity" here is a display colour, and upstream's mapping is deliberately not the identity:
# the game's Legendary is IA's "Epic", the game's Epic is IA's "Blue". Get this wrong and every
# item in the collection is mislabelled -- and it fails quietly, because a wrong colour still
# looks like a colour. Same for the "level of green": it counts Rare affixes, and its record
# filters decide whether a legendary base wrongly counts as an affix.
#
# check-upstream.sh reports that the file changed; this says whether the change matters.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UPSTREAM="$(dirname "$LINUX_PORT")/iagd"

UTIL="$UPSTREAM/IAGrim/Database/DAO/Util/ItemOperationsUtility.cs"
DAO="$UPSTREAM/IAGrim/Database/DAO/PlayerItemDaoImpl.cs"
OURS="$LINUX_PORT/src/IAGrim.Core/ItemStats/ItemRarity.cs"

for f in "$UTIL" "$DAO" "$OURS"; do
    [ -f "$f" ] || { echo "error: file not found: $f" >&2; exit 1; }
done

# The classification -> colour chain, as ordered pairs. Order matters as much as membership:
# the chain is a sequence of Contains() tests, so an item that is both Legendary and Rare takes
# whichever is tested first.
mapping() {
    python3 - "$1" "$2" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
m = re.search(r'TranslateClassification\([^)]*\)\s*\{(.*?)\n\s{0,8}\}', src, re.S)
if not m:
    sys.exit(f"could not find TranslateClassification in {sys.argv[2]}")
body = m.group(1)
for classification, colour in re.findall(r'Contains\("(\w+)"\)\s*\)?\s*return "(\w+)";', body):
    print(f"{classification} -> {colour}")
fallback = re.search(r'else\s*\n?\s*return "(\w+)";', body)
if fallback:
    print(f"(no match) -> {fallback.group(1)}")
PY
}

# The record filters that decide which records count toward the "level of green".
green_filters() {
    python3 - "$1" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
m = re.search(r'(GetGreenQualityLevelForRecords|GreenQualityLevelForRecords)\(.*?\n\s*\}\n', src, re.S)
if not m:
    sys.exit("could not find the green-quality function")
body = m.group(0)
for pattern in re.findall(r'(?:StartsWith|Contains)\("([^"]+)"\)', body):
    print(f"record filter: {pattern}")
for classification in re.findall(r'm != "(\w+)"', body):
    print(f"disqualifying:  {classification}")
for counted in re.findall(r'Count\(m => m == "(\w+)"\)', body):
    print(f"counted:        {counted}")
PY
}

# The records the rules are applied *to*. Upstream's GetRecordsForItem is the whole input, and
# both of this port's writers -- the import path and the analysis pass -- have to use the same
# one. When they did not, an item's colour depended on which of them wrote it last: ModifierRecord
# holds a crafting bonus classified Magical, so counting it turned plain crafted items Yellow on
# import and White again at the next pass.
record_set() {
    python3 - "$1" "$2" <<'PY'
import re, sys
src = open(sys.argv[1]).read()
which = sys.argv[2]

if which == "upstream":
    m = re.search(r'GetRecordsForItem\(BaseItem item\)\s*\{(.*?)\n\s{8}\}', src, re.S)
    fields = re.findall(r'records\.Add\(item\.(\w+)\)', m.group(1)) if m else []
else:
    m = re.search(r'IEnumerable<string> Records\(\)\s*\{(.*?)\n\s{8}\}', src, re.S)
    fields = re.findall(r'yield return (\w+);', m.group(1)) if m else []

if not fields:
    sys.exit(f"could not read the record list from {sys.argv[1]}")
for field in fields:
    print(field)
PY
}

status=0

compare() {
    local label="$1" up="$2" mine="$3"
    if [ "$up" = "$mine" ]; then
        echo "OK    $label matches upstream ($(echo "$up" | grep -c .) rules)"
    else
        echo "DRIFT $label no longer matches upstream"
        diff <(echo "$up") <(echo "$mine") | sed 's/^/        /'
        status=1
    fi
}

compare "Classification to display colour" \
    "$(mapping "$UTIL" upstream)" \
    "$(mapping "$OURS" ours)" || status=1

compare "Level-of-green rules" \
    "$(green_filters "$DAO")" \
    "$(green_filters "$OURS")" || status=1

compare "Records an item is classified from (import path)" \
    "$(record_set "$DAO" upstream)" \
    "$(record_set "$LINUX_PORT/src/IAGrim.Core/ItemStats/NewItemDetails.cs" ours)" || status=1

compare "Records an item is classified from (analysis pass)" \
    "$(record_set "$DAO" upstream)" \
    "$(record_set "$LINUX_PORT/src/IAGrim.Core/ItemStats/StatPrecomputeService.cs" ours)" || status=1

if [ $status -ne 0 ]; then
    echo
    echo "A change here relabels items rather than erroring. Port it before trusting the"
    echo "rarity filter or the collection aggregates."
fi
exit $status
