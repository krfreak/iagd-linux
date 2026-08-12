#!/usr/bin/env bash
# Asserts this port's schema is still upstream's, statement for statement.
#
# The point of matching it is that a userdata.db from a Windows IAGD install opens here with
# the collection intact -- no export, no conversion. That only holds while the DDL agrees, and
# a disagreement does not announce itself: a renamed column makes one filter return nothing,
# months later, on someone else's database.
#
# Two checks:
#   1. every CREATE TABLE and CREATE INDEX upstream issues appears in Schema.cs verbatim;
#   2. a database built from upstream's DDL alone is readable by this port -- which is the
#      claim itself, not a proxy for it.
#
# check-upstream.sh reports that the file changed; this says whether the change matters.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UPSTREAM="$(dirname "$LINUX_PORT")/iagd"

BASE="$UPSTREAM/IAGrim/Database/Migrations/AddBaseTables.cs"
OURS="$LINUX_PORT/src/IAGrim.Platform/Schema.cs"

for f in "$BASE" "$OURS"; do
    [ -f "$f" ] || { echo "error: file not found: $f" >&2; exit 1; }
done

status=0

# ---------------------------------------------------------------- 1. DDL parity

# Compared as normalised SQL (whitespace collapsed, case-folded) rather than as text: upstream
# writes its DDL as C# string literals with their own indentation, and this port re-indents them
# to sit inside a different class. Neither difference changes a single table.
normalise() {
    python3 -c "
import re, sys
for line in sys.stdin:
    sql = line.strip()
    if not sql: continue
    print(re.sub(r'\s+', ' ', sql).lower())
"
}

# Pulls the SQL out of C# string literals -- raw ("""...""") and ordinary alike -- keeping only
# the ones that are actually DDL. Applied identically to both files, so the comparison is
# between statements rather than between the C# that happens to wrap them.
extract_ddl() {
    python3 "$SCRIPT_DIR/extract-ddl.py" "$1" "${2:-}" "${3:-}"
}

extract_upstream() { extract_ddl "$BASE"; }

# Only the blocks copied from upstream; the port's own additions live in separate arrays after
# this window and are deliberately excluded.
extract_ours() {
    extract_ddl "$OURS" \
        'private static readonly (string Table, string Ddl)[] Tables' \
        "Columns upstream's .hbm.xml mappings declare"
}

up="$(extract_upstream | normalise | sort)"
mine="$(extract_ours | normalise | sort)"

if [ "$up" = "$mine" ]; then
    echo "OK    Schema matches upstream ($(echo "$up" | grep -c .) statements)"
else
    echo "DRIFT Schema no longer matches upstream"
    comm -23 <(echo "$up") <(echo "$mine") | cut -c1-110 | sed 's/^/        upstream only: /'
    comm -13 <(echo "$up") <(echo "$mine") | cut -c1-110 | sed 's/^/        ours only:     /'
    status=1
fi

# ------------------------------------------------- 2. read a database upstream would have written

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "SKIP  Cannot build the fixture database: sqlite3 not installed"
    exit $status
fi

fixture="$(mktemp -d)/upstream-userdata.db"

# Built from upstream's statements only -- none of this port's tables, and none of its code.
# If the port needs a table it creates itself, this is where that shows up.
extract_upstream | while IFS= read -r sql; do
    printf '%s;\n' "$sql"
done | sqlite3 "$fixture" 2>/dev/null

sqlite3 "$fixture" <<'SQL' 2>/dev/null
INSERT INTO PlayerItem (Id, baserecord, Seed, Name, namelowercase, Rarity, LevelRequirement,
                        IsHardcore, created_at, PrefixRarity)
VALUES (1, 'records/items/upgraded/gearweapons/guns1h/c030_gun1h.dbr', 1234567,
        'Mythical Plagueborne Revolver', 'mythical plagueborne revolver', 'Blue', 94, 0,
        1700000000, 0);
INSERT INTO ReplicaItem2 (Id, playeritemid) VALUES (1, 1);
INSERT INTO ReplicaItemRow (Id, replicaitemid, Type, Text, TextLowercase)
VALUES (1, 1, 6, 'Mythical Plagueborne Revolver', 'mythical plagueborne revolver'),
       (2, 1, 3, '27-40 Acid Damage', '27-40 acid damage');
INSERT INTO PlayerItemRecord (PlayerItemId, Record)
VALUES (1, 'records/items/upgraded/gearweapons/guns1h/c030_gun1h.dbr');
SQL

count="$(sqlite3 "$fixture" "SELECT COUNT(*) FROM PlayerItem;" 2>/dev/null)"
if [ "$count" != "1" ]; then
    echo "DRIFT Could not build a fixture from upstream's DDL -- the statements did not execute"
    rm -rf "$(dirname "$fixture")"
    exit 1
fi

# The port opens it and runs a search. Any missing table or renamed column fails here.
probe="$LINUX_PORT/scripts/.schema-probe"
mkdir -p "$probe"
cat > "$probe/probe.csproj" <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>schemaprobe</AssemblyName>
    <RootNamespace>SchemaProbe</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/IAGrim.Host/IAGrim.Host.csproj" />
  </ItemGroup>
</Project>
PROJ
cat > "$probe/Program.cs" <<'PROG'
// Opens a database built purely from upstream's DDL and exercises the read paths.
using IAGrim.Host;

var path = args[0];
var collection = new CollectionService(path);
var views = new CollectionViewService(path);

var all = collection.Search(new ItemQuery(), 0, 10);
Console.WriteLine($"items={all.Total}");

var byName = collection.Search(new ItemQuery { Wildcard = "plagueborne" }, 0, 10);
Console.WriteLine($"byName={byName.Total}");

// Reaches ReplicaItemRow, which is where a schema mismatch would hide.
var byStatLine = collection.Search(new ItemQuery { Wildcard = "acid" }, 0, 10);
Console.WriteLine($"byStatLine={byStatLine.Total}");

var byRarity = collection.Search(new ItemQuery { Rarity = "Blue" }, 0, 10);
Console.WriteLine($"byRarity={byRarity.Total}");

var byLevel = collection.Search(new ItemQuery { MinimumLevel = 90 }, 0, 10);
Console.WriteLine($"byLevel={byLevel.Total}");

// Every remaining filter, purely to prove none of them throws on upstream's tables.
collection.Search(new ItemQuery {
    Filters = [["offensiveBaseFireMin"]], IsRetaliation = true, PetBonuses = true,
    HasPetBonus = true, Classes = ["Occultist"], Slot = ["WeaponHunting_Ranged1h"],
    WithGrantSkillsOnly = true, WithSummonerSkillOnly = true, DuplicatesOnly = true,
    SocketedOnly = true, RecentOnly = true,
}, 0, 10);
Console.WriteLine("allFilters=ok");

var detail = collection.Get(1);
Console.WriteLine($"detailStats={detail?.Stats.Count ?? -1}");

views.Collection(new ItemQuery());
views.Aggregate();
views.Sets();
Console.WriteLine("views=ok");
PROG

output="$(cd "$probe" && dotnet run --project probe.csproj -- "$fixture" 2>&1)"
result=$?

expected="items=1
byName=1
byStatLine=1
byRarity=1
byLevel=1
allFilters=ok
detailStats=2
views=ok"

actual="$(echo "$output" | grep -E '^(items|byName|byStatLine|byRarity|byLevel|allFilters|detailStats|views)=')"

if [ $result -eq 0 ] && [ "$actual" = "$expected" ]; then
    echo "OK    A database built from upstream's DDL alone reads correctly"
else
    echo "DRIFT This port cannot read a database written by the Windows tool"
    echo "$output" | tail -20 | sed 's/^/        /'
    status=1
fi

rm -rf "$(dirname "$fixture")" "$probe/bin" "$probe/obj"

if [ $status -ne 0 ]; then
    echo
    echo "The schema is the compatibility contract: it is what lets an existing userdata.db be"
    echo "opened here, and a database written here be opened by the Windows tool. Port the"
    echo "change before shipping."
fi
exit $status
