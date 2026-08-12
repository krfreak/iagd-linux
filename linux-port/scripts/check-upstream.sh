#!/usr/bin/env bash
# Reports upstream files that have changed since we last reviewed them.
#
# This port copies or reimplements logic from a dozen upstream files. Re-porting a change
# will always need judgement, but *noticing* one should not: an unnoticed change to the loot
# CSV columns or an item-record field surfaces as corrupted data months later, not as a
# build error.
#
# Run after pulling upstream. Review each diff, port what matters, then re-record with
# --accept.
#
#   ./scripts/check-upstream.sh            what changed since last accepted
#   ./scripts/check-upstream.sh --diff     show the actual diffs
#   ./scripts/check-upstream.sh --accept   record current state as reviewed

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UPSTREAM="$(dirname "$LINUX_PORT")/iagd"

MANIFEST="$LINUX_PORT/upstream-sync.tsv"
BASELINE="$LINUX_PORT/.upstream-baseline"

MODE="report"
case "${1:-}" in
    --diff)   MODE="diff" ;;
    --accept) MODE="accept" ;;
    --help|-h)
        sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
        exit 0 ;;
esac

[ -d "$UPSTREAM" ] || { echo "error: upstream checkout not found at $UPSTREAM" >&2; exit 1; }
[ -f "$MANIFEST" ] || { echo "error: manifest not found at $MANIFEST" >&2; exit 1; }

upstream_rev="$(git -C "$UPSTREAM" rev-parse --short HEAD 2>/dev/null || echo unknown)"

changed=0
missing=0
new_baseline="$(mktemp)"

while IFS=$'\t' read -r upstream_path our_path note; do
    # Skip comments and blanks.
    [[ "$upstream_path" =~ ^# ]] && continue
    [ -z "${upstream_path// }" ] && continue

    full="$UPSTREAM/$upstream_path"
    if [ ! -f "$full" ]; then
        echo "MISSING  $upstream_path"
        echo "         (upstream removed or moved it — our copy is now orphaned)"
        missing=$((missing + 1))
        continue
    fi

    hash="$(sha256sum "$full" | cut -d' ' -f1)"
    printf '%s\t%s\n' "$upstream_path" "$hash" >> "$new_baseline"

    previous=""
    if [ -f "$BASELINE" ]; then
        # One upstream file can feed several of ours (PlayerItemDaoImpl supplies both the search
        # and the rarity rules), so it appears more than once in the manifest. Stop at the first
        # match: without the exit this returns two lines, which can never equal one hash, and
        # every run would report the file as changed forever.
        previous="$(awk -F'\t' -v p="$upstream_path" '$1 == p { print $2; exit }' "$BASELINE")"
    fi

    if [ -z "$previous" ]; then
        [ "$MODE" = "accept" ] || echo "NEW      $upstream_path"
        [ "$MODE" = "accept" ] || echo "         not previously recorded — $note"
        changed=$((changed + 1))
    elif [ "$previous" != "$hash" ]; then
        echo "CHANGED  $upstream_path"
        echo "         we take: $note"
        echo "         ours:    $our_path"
        changed=$((changed + 1))

        if [ "$MODE" = "diff" ]; then
            echo
            git -C "$UPSTREAM" log --oneline -3 -- "$upstream_path" 2>/dev/null | sed 's/^/         /'
            echo
        fi
    fi
done < "$MANIFEST"

if [ "$MODE" = "accept" ]; then
    sort -u "$new_baseline" > "$BASELINE.tmp" && mv "$BASELINE.tmp" "$BASELINE"
    rm -f "$new_baseline"
    printf '# reviewed against upstream %s on %s\n' "$upstream_rev" "$(date -I)" >> "$BASELINE"
    echo "Recorded $(grep -cv '^#' "$BASELINE") file(s) as reviewed at upstream $upstream_rev."
    exit 0
fi

rm -f "$new_baseline"

echo
if [ "$changed" -eq 0 ] && [ "$missing" -eq 0 ]; then
    echo "Up to date with upstream $upstream_rev — nothing we ported has changed."
    exit 0
fi

echo "$changed changed, $missing missing (upstream $upstream_rev)."
echo "Review each, port what matters, then: ./scripts/check-upstream.sh --accept"
exit 1
