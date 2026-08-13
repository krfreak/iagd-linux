#!/usr/bin/env bash
# Asserts every search filter matches the same items upstream's would.
#
# The other verify scripts compare *tables* — which stat names a checkbox covers, which classes a
# slot means. This one compares *behaviour*: for each filter, upstream's SQL and this port's SQL
# are run against the same collection and the two sets of item ids are diffed. A filter that
# quietly matches the wrong items — as the mastery filter did, and as the Components filter did
# for a different reason — is caught here and nowhere else.
#
# Both sides are taken from the source rather than written out here:
#
#   * upstream's fragments are pinned. Every case names a line that must still exist in
#     PlayerItemDaoImpl.cs; if upstream rewords a clause the pin fails and the case is stale,
#     rather than silently testing a fragment upstream no longer uses.
#   * ours comes from the running code. scripts/search-probe reflects over ItemQueryBuilder and
#     CollectionService, so this tests what the client executes, not a copy of it.
#
# The collection is the user's own, snapshotted read-only (VACUUM INTO). Filters are only
# interesting against real data: an empty database agrees on everything.
#
#   IAGD_VERIFY_DB=/path/to/userdata.db  to test some other collection.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UPSTREAM="$(dirname "$LINUX_PORT")/iagd"
DAO="$UPSTREAM/IAGrim/Database/DAO/PlayerItemDaoImpl.cs"
SKILL_DAO="$UPSTREAM/IAGrim/Database/DAO/ItemSkillDaoImpl.cs"

[ -f "$DAO" ] || { echo "error: upstream DAO not found: $DAO" >&2; exit 1; }
command -v sqlite3 >/dev/null || { echo "error: sqlite3 is required" >&2; exit 1; }

# Upstream's source with runs of whitespace collapsed, so a pin can be written on one line
# regardless of how the fragment is wrapped in C#.
FLAT="$(tr '\n' ' ' < "$DAO" | tr -s ' ')"
FLAT_SKILLS="$(tr '\n' ' ' < "$SKILL_DAO" | tr -s ' ')"

# ---------------------------------------------------------------------------- the collection

DB="${IAGD_VERIFY_DB:-}"
SNAPSHOT=""
if [ -z "$DB" ]; then
    LIVE="${XDG_DATA_HOME:-$HOME/.local/share}/iagd-linux/userdata.db"
    if [ ! -f "$LIVE" ]; then
        echo "SKIP  Search filters: no collection to test against ($LIVE)"
        exit 0
    fi
    SNAPSHOT="$(mktemp -t iagd-verify-XXXXXX.db)"
    rm -f "$SNAPSHOT"
    # VACUUM INTO rather than cp: it is consistent without holding a write lock, and it never
    # touches the collection being played with.
    if ! sqlite3 -readonly "$LIVE" "VACUUM INTO '$SNAPSHOT';" 2>/dev/null; then
        echo "SKIP  Search filters: could not snapshot $LIVE"
        exit 0
    fi
    DB="$SNAPSHOT"
fi
trap '[ -n "$SNAPSHOT" ] && rm -f "$SNAPSHOT"' EXIT

# Two filters ask about things a collection may simply not contain: items looted in the last
# twelve hours, and a hardcore branch. Both were wrong here at some point — "recent" compared a
# millisecond column against a seconds cutoff — and neither is testable without an item that
# qualifies. One item of each is arranged for, and only ever in a snapshot this script made
# itself, never in a collection handed to it.
if [ -n "$SNAPSHOT" ]; then
    sqlite3 "$SNAPSHOT" "
        UPDATE PlayerItem SET created_at = (CAST(strftime('%s','now') AS INTEGER)) * 1000
         WHERE Id = (SELECT MIN(Id) FROM PlayerItem);
        UPDATE PlayerItem SET IsHardcore = 1
         WHERE Id = (SELECT MAX(Id) FROM PlayerItem);" 2>/dev/null
fi

items="$(sqlite3 "$DB" "SELECT COUNT(*) FROM PlayerItem;" 2>/dev/null || echo 0)"
if [ "${items:-0}" -lt 1 ]; then
    echo "SKIP  Search filters: the collection is empty"
    exit 0
fi

# ------------------------------------------------------------------------------- upstream SQL

# Upstream's PetRecordCondition and RecordStatSubquery, which most filters are expressed through.
PET_RECORD_CONDITION="pir.record NOT IN (
    IFNULL(pi2.BaseRecord, ''), IFNULL(pi2.PrefixRecord, ''), IFNULL(pi2.SuffixRecord, ''),
    IFNULL(pi2.MateriaRecord, ''), IFNULL(pi2.AscendantAffixNameRecord, ''), IFNULL(pi2.AscendantAffix2hNameRecord, '')
)"

# $1 condition on the game stat row, $2 "pet" to restrict to the item's pet records.
record_stat_subquery() {
    local condition="$1" pet="${2:-}"
    local join="" filter=""
    if [ "$pet" = "pet" ]; then
        join="JOIN PlayerItem pi2 ON pi2.Id = pir.Playeritemid"
        filter="AND $PET_RECORD_CONDITION"
    fi
    echo "SELECT pir.Playeritemid FROM PlayerItemRecord pir
          $join
          JOIN databaseitem_v2 db ON db.baserecord = pir.record
          JOIN databaseitemstat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem AND ($condition)
          $filter"
}

# Upstream's ItemSkillDaoImpl.ListItemsQuery, reduced to the column the filter selects from it.
GRANTS_SKILL_QUERY="SELECT p.baserecord as PlayerItemRecord
    from itemskill_v2 s, itemskill_mapping map, DatabaseItem_v2 db, PlayerItem p
    where s.id_skill = map.id_skill
    and map.id_databaseitem = db.id_databaseitem
    and db.baserecord = p.baserecord"

# Upstream always scopes a search to one mod and one hardcore branch: the selected transfer file
# (SplitSearchWindow.UpdateListView). Every case carries that scope so it isolates one filter.
BASE_WHERE="(PI.Mod IS NULL OR PI.Mod = '') AND NOT PI.IsHardcore"

RECENT_CUTOFF="$(( ($(date +%s) - 12 * 3600) * 1000 ))"   # ToTimestamp() is milliseconds

# ---------------------------------------------------------------------------------- the cases

names=(); pins=(); wheres=(); queries=(); modes=()

# name, pinned upstream line, upstream's added WHERE, our ItemQuery, comparison mode
add_case() {
    names+=("$1"); pins+=("$2"); wheres+=("$3"); queries+=("$4"); modes+=("${5:-equal}")
}

add_case "rarity" \
    'queryFragments.Add("PI.Rarity = :rarity");' \
    "PI.Rarity = 'Epic'" \
    '{"Rarity":"Epic"}'

add_case "prefix rarity" \
    'queryFragments.Add("PI.PrefixRarity >= :prefixRarity");' \
    "PI.PrefixRarity >= 2" \
    '{"PrefixRarity":2}'

add_case "socketed" \
    'queryFragments.Add("PI.MateriaRecord is not null and PI.MateriaRecord != \x27\x27");' \
    "PI.MateriaRecord is not null and PI.MateriaRecord != ''" \
    '{"SocketedOnly":true}'

add_case "minimum level" \
    'queryFragments.Add("PI.LevelRequirement >= :minlevel");' \
    "PI.LevelRequirement >= 50" \
    '{"MinimumLevel":50}'

add_case "maximum level" \
    'queryFragments.Add("PI.LevelRequirement <= :maxlevel");' \
    "PI.LevelRequirement <= 70" \
    '{"MaximumLevel":70}'

add_case "recent only" \
    'queryFragments.Add("created_at > :filter_recentOnly");' \
    "PI.created_at > $RECENT_CUTOFF" \
    '{"RecentOnly":true}'

add_case "grants skill" \
    'queryFragments.Add($"PI.baserecord IN (SELECT PlayerItemRecord from ({ItemSkillDaoImpl.ListItemsQuery}) y)");' \
    "PI.baserecord IN (SELECT PlayerItemRecord FROM ($GRANTS_SKILL_QUERY) y)" \
    '{"WithGrantSkillsOnly":true}'

add_case "grants summon skill" \
    "and stat.stat = 'spawnObjects')" \
    "PI.baserecord IN (SELECT p.baserecord as PlayerItemRecord
        from itemskill_v2 s, itemskill_mapping map, DatabaseItem_v2 db, playeritem p, DatabaseItemStat_v2 stat
        where s.id_skill = map.id_skill
        and map.id_databaseitem = db.id_databaseitem
        and db.baserecord = p.baserecord
        and stat.id_databaseitem = s.id_databaseitem
        and stat.stat = 'spawnObjects')" \
    '{"WithSummonerSkillOnly":true}'

add_case "slot" \
    'var subQuerySql = RecordStatSubquery("dbs.stat = \x27Class\x27 AND dbs.TextValue in ( :class )");' \
    "PI.Id IN ($(record_stat_subquery "dbs.stat = 'Class' AND dbs.TextValue in ( 'WeaponMelee_Sword2h','WeaponMelee_Axe2h','WeaponMelee_Mace2h' )"))" \
    '{"Slot":["WeaponMelee_Sword2h","WeaponMelee_Axe2h","WeaponMelee_Mace2h"]}'

add_case "slot inverted" \
    'sql.Add($" AND PI.Id {(query.SlotInverse ? "NOT" : "")} IN ({subQuerySql})");' \
    "PI.Id NOT IN ($(record_stat_subquery "dbs.stat = 'Class' AND dbs.TextValue in ( 'ArmorProtective_Head' )"))" \
    '{"Slot":["ArmorProtective_Head"],"SlotInverse":true}'

# Components are the one slot with a second condition: without it the filter returns every item
# that *has* a component rather than the components themselves.
add_case "slot components" \
    'if (query.Slot.Length == 1 && query.Slot[0] == "ItemRelic") {' \
    "PI.Id IN ($(record_stat_subquery "dbs.stat = 'Class' AND dbs.TextValue in ( 'ItemRelic' )")) AND PI.MateriaRecord = ''" \
    '{"Slot":["ItemRelic"]}'

add_case "retaliation" \
    'queryFragments.Add($"(dbs.stat >= \x27{retaliationPrefix}\x27 AND dbs.stat < \x27{retaliationUpper}\x27)");' \
    "PI.Id IN ($(record_stat_subquery "(dbs.stat >= 'retaliation' AND dbs.stat < 'retaliatioo')"))" \
    '{"IsRetaliation":true}'

add_case "has pet bonus" \
    "sql.Add(RecordStatSubquery(\"dbs.stat = 'petBonusName'\"));" \
    "PI.Id IN ($(record_stat_subquery "dbs.stat = 'petBonusName'"))" \
    '{"HasPetBonus":true}'

add_case "pet scope alone" \
    'if (query.PetBonuses && queryFragments.Count == 0) {' \
    "PI.Id IN (SELECT pir.Playeritemid FROM PlayerItemRecord pir
               JOIN PlayerItem pi2 ON pi2.Id = pir.Playeritemid
               WHERE $PET_RECORD_CONDITION)" \
    '{"PetBonuses":true}'

add_case "stat group" \
    'queryFragments.Add($"dbs.stat in ( :filter_{filter.GetHashCode()} )");' \
    "PI.Id IN ($(record_stat_subquery "dbs.stat in ( 'offensiveFire','offensiveFireMin','offensiveFireMax' )"))" \
    '{"Filters":[["offensiveFire","offensiveFireMin","offensiveFireMax"]]}'

add_case "two stat groups" \
    'foreach (var filter in query.Filters) {' \
    "PI.Id IN ($(record_stat_subquery "dbs.stat in ( 'offensiveFire','offensiveFireMin','offensiveFireMax' )"))
     AND PI.Id IN ($(record_stat_subquery "dbs.stat in ( 'offensiveCold','offensiveColdMin','offensiveColdMax' )"))" \
    '{"Filters":[["offensiveFire","offensiveFireMin","offensiveFireMax"],["offensiveCold","offensiveColdMin","offensiveColdMax"]]}'

# The pet prefix is a rename, not a different table: pet-bonus records store every stat under a
# "pet"-prefixed name.
add_case "stat group on the pet" \
    'var effectiveFilter = query.PetBonuses ? filter.Select(s => petPrefix + s).ToArray() : filter;' \
    "PI.Id IN ($(record_stat_subquery "dbs.stat in ( 'petoffensiveFire','petoffensiveFireMin','petoffensiveFireMax' )" pet))" \
    '{"PetBonuses":true,"Filters":[["offensiveFire","offensiveFireMin","offensiveFireMax"]]}'

add_case "mastery" \
    "dbs.stat IN ({string.Join(\",\", classStats)}) " \
    "PI.Id IN ($(record_stat_subquery "dbs.stat IN ('augmentSkill1Extras','augmentSkill2Extras','augmentSkill3Extras','augmentSkill4Extras','augmentMastery1','augmentMastery2','augmentMastery3','augmentMastery4') AND dbs.TextValue = 'class03'"))" \
    '{"Classes":["class03"]}'

add_case "duplicates only" \
    'group by Records HAVING N > 1' \
    "PI.BaseRecord IN (SELECT BaseRecord FROM (
        select baserecord || prefixrecord || suffixrecord as Records, count(*) as N, BaseRecord from PlayerItem
        WHERE (Mod IS NULL OR Mod = '')
        AND NOT IsHardcore
        group by Records
        HAVING N > 1
        order by N desc
    ))" \
    '{"DuplicatesOnly":true}'

add_case "hardcore" \
    'queryFragments.Add(query.IsHardcore ? "PI.IsHardcore" : "NOT PI.IsHardcore");' \
    "PI.IsHardcore" \
    '{"IsHardcore":true}' \
    hardcore

# The name match is upstream's; the tooltip match reaches the item's captured stat lines.
# This port adds a third arm (the parsed template name) for items whose tooltip was never
# captured, so it can only ever match more — never fewer — than upstream.
add_case "wildcard" \
    'queryFragments.Add("(PI.namelowercase LIKE :name OR R.id IN (SELECT replicaitemid FROM replicaitemrow WHERE IFNULL(textlowercase, text) LIKE :wildcard))");' \
    "(PI.namelowercase LIKE '%mythical%' OR R.id IN (SELECT replicaitemid FROM replicaitemrow WHERE IFNULL(textlowercase, text) LIKE '%mythical%'))" \
    '{"Wildcard":"mythical"}' \
    superset

add_case "numeric stat filter" \
    'HAVING SUM(value) {ToSqlOperator(svf.Operator)} :{thresholdParam})' \
    "PI.Id IN (SELECT playeritemid FROM ComputedItemStat
               WHERE stat IN ('offensivePhysicalMin','offensivePhysicalMax')
               GROUP BY playeritemid HAVING SUM(value) >= 50)" \
    '{"StatFilters":[{"Fields":["offensivePhysicalMin","offensivePhysicalMax"],"Operator":"GreaterOrEqual","Threshold":50}]}'

add_case "numeric stat filter, less than" \
    'case StatValueFilter.Op.LessThan: return "<";' \
    "PI.Id IN (SELECT playeritemid FROM ComputedItemStat
               WHERE stat IN ('offensivePhysicalMin','offensivePhysicalMax')
               GROUP BY playeritemid HAVING SUM(value) < 50)" \
    '{"StatFilters":[{"Fields":["offensivePhysicalMin","offensivePhysicalMax"],"Operator":"LessThan","Threshold":50}]}'

# Upstream drops the numeric filters entirely under a pet scope: the pre-computed values are the
# player's, so comparing them against a pet threshold would answer a different question.
add_case "numeric filter ignored on the pet" \
    '&& !query.PetBonuses) {' \
    "PI.Id IN ($(record_stat_subquery "dbs.stat in ( 'petoffensiveTotalDamageModifier' )" pet))" \
    '{"PetBonuses":true,"Filters":[["offensiveTotalDamageModifier"]],"StatFilters":[{"Fields":["offensivePhysicalMin"],"Operator":"GreaterOrEqual","Threshold":50}]}'

# --------------------------------------------------------------------------------- our SQL

PROBE="$SCRIPT_DIR/search-probe"
if ! out="$(cd "$PROBE" && dotnet build -v q --nologo 2>&1)"; then
    echo "error: could not build the search probe" >&2
    echo "$out" >&2
    exit 1
fi
PROBE_DLL="$(find "$PROBE/bin" -name searchprobe.dll -print -quit)"
[ -n "$PROBE_DLL" ] || { echo "error: search probe was not built" >&2; exit 1; }

# The client normalises any database it opens; comparing against one it has not touched would
# test a shape the client never sees. Only ever run against the snapshot, never the collection.
if [ -n "$SNAPSHOT" ] && ! out="$(dotnet "$PROBE_DLL" --prepare "$SNAPSHOT" 2>&1)"; then
    echo "error: could not prepare the snapshot" >&2
    echo "$out" >&2
    exit 1
fi

probe_input=""
for i in "${!names[@]}"; do
    # Every case carries upstream's mod and hardcore scope; the hardcore case sets its own.
    scope='"Mod":"","IsHardcore":false'
    [ "${modes[$i]}" = "hardcore" ] && scope='"Mod":""'
    query="{$scope,${queries[$i]#\{}"
    probe_input+="$i	$query"$'\n'
done

if ! probe_output="$(printf '%s' "$probe_input" | dotnet "$PROBE_DLL" 2>&1)"; then
    echo "error: the search probe failed" >&2
    echo "$probe_output" >&2
    exit 1
fi

# ------------------------------------------------------------------------------- comparison

failures=0
stale=0
checked=0
empty=0

for i in "${!names[@]}"; do
    name="${names[$i]}"
    pin="$(printf '%b' "${pins[$i]}")"
    mode="${modes[$i]}"

    # Is the fragment this case was written against still upstream's?
    haystack="$FLAT"
    [ "$name" = "grants skill" ] && haystack="$FLAT $FLAT_SKILLS"
    flat_pin="$(printf '%s' "$pin" | tr '\n' ' ' | tr -s ' ')"
    if ! printf '%s' "$haystack" | grep -qF -- "$flat_pin"; then
        echo "STALE $name: upstream no longer contains \"$flat_pin\""
        stale=$((stale + 1))
        continue
    fi

    ours="$(printf '%s\n' "$probe_output" | awk -F'\t' -v n="$i" '$1 == n { print $2 }')"
    if [ -z "$ours" ]; then
        echo "FAIL  $name: the probe produced no SQL"
        failures=$((failures + 1))
        continue
    fi

    base="$BASE_WHERE"
    [ "$mode" = "hardcore" ] && base="(PI.Mod IS NULL OR PI.Mod = '')"

    upstream_sql="SELECT PI.Id FROM PlayerItem PI
        LEFT OUTER JOIN ReplicaItem2 R ON PI.ID = R.playeritemid
        WHERE $base AND (${wheres[$i]}) ORDER BY PI.Id"

    if ! upstream_ids="$(sqlite3 "$DB" "$upstream_sql" 2>&1)"; then
        echo "FAIL  $name: upstream's SQL did not run: $upstream_ids"
        failures=$((failures + 1))
        continue
    fi
    if ! our_ids="$(sqlite3 "$DB" "$ours" 2>&1)"; then
        echo "FAIL  $name: this port's SQL did not run: $our_ids"
        failures=$((failures + 1))
        continue
    fi

    checked=$((checked + 1))
    up_n="$(printf '%s' "$upstream_ids" | grep -c . )"
    our_n="$(printf '%s' "$our_ids" | grep -c . )"

    # comm compares lexicographically; the ids arrive in numeric order.
    missing="$(comm -23 <(printf '%s\n' "$upstream_ids" | sort) <(printf '%s\n' "$our_ids" | sort) | grep -c .)"
    extra="$(comm -13 <(printf '%s\n' "$upstream_ids" | sort) <(printf '%s\n' "$our_ids" | sort) | grep -c .)"

    if [ "$mode" = "superset" ]; then
        if [ "$missing" -eq 0 ]; then
            printf 'ok    %-32s %s items, %s more than upstream (documented)\n' "$name" "$up_n" "$extra"
        else
            echo "FAIL  $name: misses $missing of upstream's $up_n items"
            failures=$((failures + 1))
        fi
        continue
    fi

    if [ "$missing" -eq 0 ] && [ "$extra" -eq 0 ]; then
        # Agreeing on nothing is agreement, but it is not evidence: say so rather than let a
        # filter nobody's collection exercises look verified.
        if [ "$up_n" -eq 0 ] && [ "$our_n" -eq 0 ]; then
            printf 'ok    %-32s no items either way — not exercised\n' "$name"
            empty=$((empty + 1))
        else
            printf 'ok    %-32s %s items\n' "$name" "$up_n"
        fi
    else
        echo "FAIL  $name: upstream matches $up_n, this port $our_n (misses $missing, adds $extra)"
        failures=$((failures + 1))
    fi
done

echo
if [ "$failures" -eq 0 ] && [ "$stale" -eq 0 ]; then
    note=""
    [ "$empty" -gt 0 ] && note=", $empty not exercised by this collection"
    echo "OK    Search filters match upstream ($checked filters over $items items$note)"
    exit 0
fi
[ "$stale" -gt 0 ] && echo "      $stale case(s) no longer match upstream's source and need rewriting"
[ "$failures" -gt 0 ] && echo "DRIFT $failures filter(s) do not match upstream's behaviour"
exit 1
