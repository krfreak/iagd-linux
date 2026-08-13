# Staying in sync with upstream

This port copies or reimplements logic from about thirty upstream files. Re-porting a
change will always need judgement — but **noticing** one should not, and that is the real
risk. An unnoticed change to the loot CSV columns or an item-record field does not break the
build; it corrupts data, months later, silently.

## The database is upstream's, not ours

This port reads and writes **upstream's schema**, table for table and column for column. A
`userdata.db` copied from a Windows IAGD install opens here with the collection intact — no
export step, no conversion — and a database written here stays loadable by the Windows tool.

That is a deliberate constraint and it costs something: the code says `Id` rather than
`Id_PlayerItem`, `created_at` rather than `CreationDate`, and stores tooltip lines across
`ReplicaItem2` + `ReplicaItemRow` rather than in one flat table. Every one of those is less
pleasant in isolation. Together they are why upstream's SQL — including the subqueries behind
the pet, damage-type and mastery filters — could be ported *verbatim* rather than
reinterpreted, which is worth far more than tidier names.

`src/IAGrim.Platform/Schema.cs` holds the DDL, copied from upstream's `AddBaseTables` and
annotated. It also carries the one-way migration from this port's own earlier layout, because
a collection is not reproducible: the loot files are consumed on import.

Tables upstream owns but this port does not use yet — buddy sharing, cloud sync, deleted-item
tombstones — are still created, so the file stays a shared format rather than a one-way export.

### Opening an existing collection

Because the schema is upstream's, an existing IAGD database opens directly — a Windows install's
`userdata.db`, one sitting in a Wine prefix, or simply a second collection kept elsewhere. Three
ways to say so, most specific first:

```bash
iagd list --database /path/to/userdata.db      # one command
IAGD_DATABASE=/path/to/userdata.db iagd list   # one shell
iagd settings databaseFile /path/to/userdata.db  # from now on
```

Every entry point — app, headless host, CLI — resolves it the same way through `Startup`, so a
CLI import and a running host can never end up working on different collections.

Two safeguards, because the target may be someone's real collection:

- **A file that is not an Item Assistant database is refused**, rather than having IAGD's tables
  created inside it. A path typo should not modify whatever it happened to hit. Verified: a
  SQLite file with unrelated tables is rejected and left untouched.
- **An existing collection is copied before it is changed.** This port adds `ItemTemplate` and
  `GameDataMeta`, which upstream ignores, but that is still a modification of a file the user did
  not create with this program. The copy goes into this port's own backup directory, never beside
  the original — which may be inside a Wine prefix. It happens once, detected by whether those
  additive tables are already present, so no state of our own is needed.

Verified end to end against a database built from upstream's DDL: adopted with a copy taken,
adopted only once, all 15 upstream tables intact afterwards, items preserved, and the default
collection untouched throughout.

### Merging two collections

`iagd merge <other-userdata.db>` adds another collection's items to this one, skipping exact
duplicates. Reads the source strictly read-only, backs up the destination first, and carries the
tooltips across so merged items keep the game's own rendering.

**What counts as a duplicate is the whole design.** Every field describing what an item *is* —
records, seeds, reroll counts, stack size, mod, hardcore — but not the row id and not when it
was looted, since two collections will never agree on those.

Base record plus seed, which the loot importer uses, is **wrong here**. It works for live
capture, where the game will not issue the same seed twice for different items. In a merge it
would collapse every non-rolling item — components, potions, crafting materials, quest items,
all of which carry seed 0 — into a single row. Five items in the real transfer stash have seed 0.

The remaining error runs the safe way: two genuinely identical items looted separately merge
into one. An item wrongly dropped can be looted again; an item wrongly duplicated inflates a
collection with no way to tell afterwards which copy was real.

Verified with a source containing an exact copy, a same-record-different-seed item, two seed-0
stacks differing only in size, and a new item: 1 skipped, 4 imported, the two stacks kept
separate, re-running imports nothing, the source unchanged, and a merged item arriving with all
27 of its tooltip lines.

**From the settings page** the same merge runs behind a preview. `POST /api/merge` takes
`{path, dryRun}`; the page always asks for the dry run first, because the number worth seeing
before committing — how many are duplicates — cannot be known without reading both collections.
Only then does a confirm button appear, naming the count it will add. The host takes the same
`before-merge` backup the CLI does and names it back to the page.

Progress is pushed as `mergeProgress` events over the existing socket rather than returned with
the result, which cannot say anything until the merge is over. The per-row callback is throttled
in the host to one report every 150 ms and never overlaps a send in flight — `EventHub` writes
to each socket from the calling thread, and concurrent sends on one socket are not allowed.
Dropped ticks cost nothing; the final report is always sent, and the completion message waits for
it so the bar finishes rather than being overtaken. The bar sweeps rather than measuring until
the first report arrives, since the total is not known before the source is opened. The CLI
draws the same progress as a single rewritten line, and only when the output is a terminal.

**A source may be older than our schema.** Upstream has added columns over the years, and the
collection in this machine's Wine prefix — 7,480 items, written by the Windows tool — predates
`AffixRerollsUsed`. Selecting a column SQLite does not have is an error rather than a null, so
the source query is built from the columns `PRAGMA table_info` reports and the rest are
substituted with the same value `IFNULL` gives on the destination. A column one side never had
therefore still compares equal instead of failing the merge.

**The page finishes the job.** A merged row carries its own records but nothing derived from the
game's data — no rolled values, no rarity, no pet-bonus records — because none of that lives in
the source collection. Left to the user, the result is items that are in the collection and
invisible to most of the filters, so the host runs the same pass `iagd stats` does as soon as a
merge has added anything. It recomputes the whole collection rather than the new rows, which is
what upstream's own recompute does and what makes it also repair anything looted since the last
one. Measured at 11 s for 7,480 items, against 1 s for the merge itself.

That pass reports what it is doing rather than how far along it is, so the bar sweeps for that
stage and shows its messages. When Grim Dawn cannot be found the merge still succeeds and the
page says the values were not computed; a pass that throws is reported the same way, since the
items are already safely in. The CLI keeps the two as separate commands and prints the reminder.

Verified against scratch collections of 6,000 and 60,000 items: counts agreed with the dry run
(4,500 imported, 1,500 duplicates), the frames arrived in order and ended exactly on the total,
re-merging imported nothing, merging a collection into itself was refused, and a missing file
came back as a message rather than a stack trace. Verified again against the prefix collection:
7,480 items previewed and merged in under a second, the source byte-identical afterwards
(md5 unchanged), and both the CLI and the page reporting 7,480 duplicates on a second pass. With
the stats pass included the page took 11.6 s end to end and rolled 7,002 of them, every item
ending with a rarity. Its records match the source exactly — 12,781 rows against 12,784, the
three missing ones being orphans in the source itself, left behind by an item deleted in the
Windows tool.

### The window's layout

Upstream is WinForms chrome wrapped around an embedded browser: tab strip and status bar from
the window, filter panels and the search toolbar from WinForms, and only the item list itself
from the web page. This port draws all of it in one page, but as the same arrangement — tabs
across the top (Items, Online, Settings, Grim Dawn), five collapsible filter panels down the
left, the search toolbar above the list, and "N matching items found" along the bottom.

The two dropdowns in that toolbar are data, not decoration: `SlotFilters` is upstream's
`UIHelper.SlotFilter` and `UIHelper.QualityFilter`, each slot label mapped to the item classes it
selects. A wrong class there fails silently — the dropdown works, the search returns items, it
just excludes a slot the Windows tool would include — so `verify-slot-filters.sh` re-extracts
both tables from the pinned checkout and compares them against what the port serves at runtime.

**Identical items are one card.** The host groups rows by base record plus prefix plus suffix,
which is upstream's `ItemOperationsUtility.MergeStackSize`, and the card offers "Transfer all
(N)". Paging counts groups rather than rows, or scrolling would run off the end of the list.
Ordering is upstream's too: name then id, and level first when "Order By Level" is ticked —
ascending in both cases, from `PlayerItemDaoImpl.SearchForItems`.

**The card is upstream's card.** Icon, name in its rarity colour, the tooltip the game itself
drew, then the level requirement and the transfer links along the bottom edge. Two details are
worth stating because they are not what a fresh implementation would do:

- The name has its colour codes **stripped** and is coloured by rarity as a whole, rather than
  rendered `^P`-segment by segment. That is upstream's rendering, and a Mythical reads as one
  purple name in both tools.
- Tooltip rows are styled by their **row type**, ported from upstream's `ReplicaStat.css`: type
  34 is the "Granted Skills" heading, 22 is "(2) Set", 19 the level requirement. Types 3, 4, 5,
  6, 64 and 77 are hidden because the header already shows them, and 35 is hidden because
  "[Release Ctrl to Hide Details]" is a prompt to someone standing in front of the game.

The palette is upstream's dark theme value for value (`WebUI/src/style/index.css`, `.App-dark`):
`#a638ff` legendary, `#338cce` epic, `#08b908` magical, `#dbb284` stat text. The only deliberate
substitution is the rarity glow behind an icon, which upstream draws with a PNG per colour and
this port draws as a radial gradient — its images are its own.

**Stat text has two sources, as it does upstream.** The hook captures what Grim Dawn actually
rendered, and that is used whenever it exists. Everything else — a merged collection, a GD Stash
import — is described from the game's own database instead, by `ItemStatText`.

That describing is upstream's own code rather than a reimplementation: this project already
referenced its `StatTranslator` for `ItemNameCombinator`, so `StatManager` and `EnglishLanguage`
were already compiled in and merely unused. A thousand lines of stat-to-text rules — damage
ranges, conversions, racial bonuses, pet scoping — is exactly the material that comes out subtly
wrong when retyped, and the tags come from `ItemTag`, filled by `iagd parse`, so nothing needs
the game's archives at request time.

What the port supplies is the input: every stat of every record the item is made of, with its
rolled values from `ComputedItemStat` laid over them. Two details are upstream's and easy to get
wrong:

- `+2 to Black Death` is two rows in the game's data, a skill record and a level. Upstream merges
  them while parsing (`ArzParser.GetSpecialSkillAugments`) into one `augmentSkill{i}` carrying
  the skill's *resolved* name, because nothing downstream can turn a record into a name. The
  precompute pass does the same, in the scan that is already streaming those records past.
  Masteries keep their tag instead, since `TryGetClassName` resolves that itself.
- Identical lines are dropped, which upstream does with a `ToHashSet()` over its rendered stats.
  An item is several records, and where two carry the same stat the same sentence comes out
  twice — three copies of "+198% Aether Damage" on a real item before this.

**Verified against the game's own rendering.** Any item with a captured tooltip is a known-good
answer, so the two were compared line by line. Every stat the game showed is present, and the
differences are upstream's rendering rather than defects: damage lines are ordered
alphabetically, attack speed reads "Speed: Very Slow (-0.18)" rather than "1.46 Attacks per
Second", "Frost Damage" rather than "Frostburn Damage", and `skillCooldownReduction` prints with
a minus because upstream's own tag is `-{0}% Skill Cooldown Reduction`. The game's `[204-306]`
range annotations are absent, being its ctrl-detail display rather than a property of the item.

**Clicking an item opens a docked panel**, not a floating one. Upstream has no equivalent — its
card carries everything — so this panel exists only for what the card cannot do: choosing which
stash an item goes to, following a queued transfer, and copying the record and seed. It shows
the granted skill and those controls, and deliberately not the stat lines the card behind it is
already showing. When it is open the list reflows to whatever whole cards still fit.

### Choosing paths

The settings page has a **Choose…** button beside the collection and Grim Dawn paths, backed by
Photino's native dialog through `POST /api/browse`. Dialogs are GTK and must run on the thread
that owns the window, while the request arrives on a listener thread — so the picker marshals
through `window.Invoke`.

The button appears only when the host advertises a chooser (`GET /api/browse`). That is true in
the desktop window and false headless, because the settings page is served over HTTP and can be
opened in a browser, where a page cannot choose a path on the machine running the host. Typing
a path always works, so the browser case is not a dead end.

**What is not shared:** `ItemTemplate` and `GameDataMeta` are this port's own, additive, and
ignored by upstream. They denormalise what upstream pivots out of `DatabaseItemStat_v2` at
query time, plus the icon filename, which has no upstream equivalent. They are derived data —
`iagd parse` rebuilds them in about 13 s — so an upstream database dropped in here needs one
parse before names and icons appear, and nothing is lost if it never happens.

## The tool

```bash
./scripts/check-upstream.sh            # what changed since last reviewed
./scripts/check-upstream.sh --diff     # with recent upstream commits for each
./scripts/check-upstream.sh --accept   # record current state as reviewed

./scripts/verify-stat-filter.sh        # does the change matter? (seed engine inputs)
./scripts/verify-item-rarity.sh        # does the change matter? (rarity rules)
./scripts/verify-schema.sh             # does the change matter? (database compatibility)
```

`check-upstream.sh` tells you a file changed. The `verify-*` scripts parse the specific rules
out of upstream's source and compare them to ours, so they tell you whether the change matters.
Both are tested by simulating an upstream edit and confirming they fail.

`upstream-sync.tsv` lists every upstream file we depend on, where our version lives, and
what we take from it. `.upstream-baseline` records the hash last reviewed.

Run it after pulling upstream. Output names the file, what we take from it, and where ours
is:

```
CHANGED  IAGrim/Services/CsvParsingService.cs
         we take: Loot CSV column order and accepted column counts
         ours:    src/IAGrim.Platform/LootCsv.cs
```

`MISSING` means upstream deleted or moved the file and our copy is orphaned — worth acting
on immediately, since it usually signals a rewrite rather than a tweak.

## What is referenced rather than copied

These build against upstream's projects directly, so their changes arrive automatically:

- `Parser` — ARZ/ARC/stash/character parsing, the highest-churn code
- `StatTranslator`, `DataAccess`, `EvilsoftCommons`

Prefer this whenever possible. The exceptions are listed in `upstream-sync.tsv` and each has
a reason: the file is Windows-only (`DDSImageReader` needs System.Drawing), lives in the
WinForms project, or is `internal` to an assembly we cannot see into.

## Which upstream changes actually matter

Not every change needs porting. In rough order of how much they hurt if missed:

| Upstream change | Effect if missed |
|---|---|
| `HookDll/Hook/Exports.h` | Hooks fail to install after a Grim Dawn patch. Loud — the hook log says which export is missing |
| `GrimTypes.h` struct layout | **Silent memory corruption.** The `static_assert`s on `ItemReplicaInfo` offsets catch most of it at build time |
| Loot CSV column order | Items import with fields shifted — wrong affixes, wrong seed. Silent |
| Message type enum | Unknown types; the probe reports these as `UNKNOWN(n)` |
| Search semantics | Divergent results, no error |
| `AddBaseTables` schema | A database from a Windows install stops opening, or one written here stops being readable by it. `verify-schema.sh` builds a fixture from upstream's DDL and reads it |
| `ItemSearchRequest` fields | A filter upstream has and this port does not. Silent — the option simply is not offered |

The first two are the reason `Exports.h` and `GrimTypes.h` are in the manifest even though
we forked the whole hook: they are the parts that change with the *game*, not with upstream's
own code.

## Item search

`src/IAGrim.Host/ItemQuery.cs` ports the query builder from
`PlayerItemDaoImpl.SearchForItems`. SQL fragments are kept close to upstream's wording and
in the same order so the two remain diffable. Deviations are marked `PORT:` — each is a
place this schema differs, not a change of intent.

**Every filter upstream's `ItemSearchRequest` declares is implemented.** Wildcard (name *and*
stat lines), mod, hardcore, socketed, duplicates, level range, recency, item class (multi-select
and inverse), rarity, prefix rarity, grants-skill, summoner, retaliation, mastery, damage-type
groups, pet-bonus, pet scope, and **numeric stat filters over seed-applied values**.

The last of those is what makes this an item manager rather than a loot log:

```
GET /api/items?stat=offensiveBaseFireMin>=50
GET /api/items?stat=offensiveBaseFireMin+offensiveBaseFireMax>=140   # summed, as upstream's checkboxes do
GET /api/items?q=mythical&stat=offensiveBasePoisonMin>=25            # combined
GET /api/items?rarity=Epic&prefixRarity=2                            # double-rare legendaries
GET /api/items?summoner=1                                            # items that summon a pet
GET /api/items?has=offensiveFireMin,offensiveFireModifier            # any fire field
GET /api/items?has=offensiveFireMin&has=offensiveColdMin             # fire AND cold
GET /api/items?petScope=1&has=characterAttackSpeed                   # attack speed on the pet
GET /api/items?mastery=Occultist&retaliation=1
GET /api/items?slot=ItemRelic  &  ?slot=WeaponHunting_Ranged1h&slotInverse=1
```

`has` groups are OR within a group and AND between groups, which is exactly how upstream's
damage checkboxes behave — the UI ships the field lists for the nine damage types.

**Not implemented:** `RecipeItemsOnly` — **because upstream does not implement it either**;
see below. Nothing else from `ItemSearchRequest` is missing.

### Two upstream filters that do not do what their names say

Worth recording, because matching upstream's *behaviour* here means diverging from upstream's
*documentation*:

- **`RecipeItemsOnly`** is declared on `ItemSearchRequest` and never read. `HasRecipe` is
  declared on `PlayerItem` and never assigned. `RecipeItem_v2` appears only in a migration's
  list of tables to drop. Recipe filtering is dead code upstream — so implementing it here
  would be inventing a feature, not porting one.
- **`WithGrantSkillsOnly`** is documented as "items which grant a skill that can be placed on
  the hotbar and triggered", but `ItemSkillDaoImpl.ListItemsQuery` never filters on the
  trigger column, so upstream also returns proc skills ("33% chance on any hit"). This port
  matches the query, not the comment. The `Trigger` column is stored, so narrowing to real
  hotbar skills later is a `WHERE` clause away.

### Rarity is a display colour, and the mapping is not the identity

`src/IAGrim.Core/ItemStats/ItemRarity.cs` ports upstream's `TranslateClassification`. The
remapping is the whole point and is easy to "fix" into being wrong:

| Grim Dawn says | IA calls it |
|---|---|
| Legendary | Epic |
| Epic | Blue |
| Rare | Green |
| Magical | Yellow |
| Common | White |

So `?rarity=Epic` means *legendaries*. `ItemTemplate.Classification` stores the game's raw
value — the collection view selects on that, item search selects on the remapped
`PlayerItem.Rarity`. Confusing the two silently mislabels everything, because a wrong colour
still looks like a colour.

`PrefixRarity` is not a rarity either: it counts an item's *Rare affixes*, so a double-rare
green sorts above a single-rare one, and upstream's filter is `>=` over that count. Only
`/lootaffixes/` records count, which is why a legendary base does not inflate it.

`scripts/verify-item-rarity.sh` compares both rule sets against upstream's source, as ordered
pairs — order matters as much as membership, since the chain is a sequence of `Contains()`
tests:

```
OK    Classification to display colour matches upstream (6 rules)
OK    Level-of-green rules matches upstream (5 rules)
```

These three columns (`Rarity`, `PrefixRarity`, `LevelRequirement`) are filled in by
`iagd stats`, matching upstream, where the same columns stay empty until its stat parse runs.
They are written for **every** item, including ones the seed engine bails out on: they are
read off the records and do not depend on the roll, so a skipped roll must not also cost an
item its rarity.

`LevelRequirement` is the **maximum** across base, prefix, suffix, materia and both ascendant
affix records — an item is gated by its most demanding part. Filtering on the template's level
instead would understate any item whose affix out-levels its base.

### Granted skills

`SkillParser` ports `ComplexItemParser`. An item names a skill via `itemSkillName`; that skill
record may be a shell that only points at a `buffSkillName` sub-skill, which is where the real
name and description live. Missing that indirection leaves a slice of items looking as though
they grant nothing.

Measured against this installation: **907 skills across 2,590 items, 41 of which summon pets.**

Two deliberate deviations, both marked `PORT:` in the source:

- Upstream keys `itemskill_v2` / `itemskill_mapping` by a surrogate `id_databaseitem`. Here
  they are keyed by record path, which this schema already joins on everywhere else. The joins
  collapse; the result set does not change.
- Upstream's summoner filter joins the skill back to `DatabaseItemStat_v2` and tests
  `stat = 'spawnObjects'` at query time. This port does not keep the game's stat rows, so the
  same predicate is evaluated during the parse and stored as `ItemSkill.SpawnsPets`.

### The filter checkboxes are their stat-field lists

A checkbox labelled "Fire" is nothing but the set
`{offensiveFire, offensiveFireModifier, offensiveElemental, offensiveElementalModifier}`. Get
the set wrong and the filter still runs, still returns items, and still looks right — it just
answers a different question than the Windows tool.

An earlier version of this port invented these lists from the shape of the stat names:
`offensiveFireMin`, `offensiveBaseFireMax`, and so on. Every one of those is a real Grim Dawn
field, which is exactly what made the guess convincing. None of them is what upstream searches.

`FilterGroups.cs` now ports all four of upstream's filter controls — `Damage`, `DamageOverTime`,
`Resistances`, `Misc` — 50 groups, and `scripts/verify-filter-groups.sh` expands upstream's
templates and compares them against what this port produces *at runtime*:

```
OK    Filter groups match upstream (50 groups, 1 documented deviation)
```

The host serves them at `/api/filters` so the UI has no second copy to drift.

**The one deviation: upstream's mastery filter cannot match anything.** It searches
`augmentSkill1Extras…4Extras` and `augmentMastery1…4`, comparing their text to a class id like
`class03`. Scanning all 4.8 million stat rows of this installation, **those eight field names
occur zero times.** The fields that exist are `augmentMasteryName1..3` and `augmentSkillName1..5`,
and they hold a record path (`records/skills/playerclass03/_classtraining_class03.dbr`), not a
class id — so both the field names and the compared value are wrong.

Reproducing that faithfully would mean shipping a checkbox that silently returns nothing. This
port searches the real fields and matches the class through the record path. Verified:
`mastery=class03` returns the Plagueborne Revolver, which grants Bloody Pox, an Occultist skill.
The deviation is encoded in the verify script so genuine drift still fails while this does not.

Masteries themselves are discovered from the tag table, not hardcoded — upstream's
`GetValidClassItemTags`, including its normalisation of expansion tags
(`tagGDX1Class07SkillName00A` → `class07`) and its exclusion of four-digit dual-class
combinations. All ten come back, Berserker included.

### Attaching the hook without being asked

The hook cannot be attached when the game launches — that was settled early, and is why
`--attach-name` exists. So something has to notice Grim Dawn and act. There is no notification
to subscribe to for "a Wine process has become hookable", so the host polls, on the two-second
timer it already runs for loot.

`AutoAttachService` is the pacing, and the pacing is the whole design. "Attach whenever the hook
is not live" launches a Proton process every two seconds for as long as someone sits at
character select. Instead:

- one attempt at a time, never overlapping;
- **not ready** — the game is loading, and the hook declined — waits 10 s and says nothing,
  because that is the normal state and announcing it every ten seconds is noise;
- **failure** backs off 30 s, doubling to a 5 minute ceiling, and says so once per attempt;
- state resets when the game restarts, so a new session starts eager.

**Retrying is only safe because of two things in the hook**, both worth knowing before changing
this: every abort path in `DllMain` returns FALSE, so Windows unloads the module and the next
attempt loads it fresh; and `ClaimSingleInstance()` holds a per-process named mutex, so a second
copy is refused even if something else goes wrong. Two copies would put two sets of MinHook
patches over the same functions and reliably crash the game.

`HookAttacher` shells out to `scripts/attach-gd.sh` rather than reimplementing it. That script
stages the DLL inside the prefix, converts paths to their Windows form, runs Proton so the
injector executes inside the game's container, clears stale markers, and refuses to inject over
a live hook. Two copies of that procedure is one too many.

The pacing is tested without a game, by substituting the attach step: game not running, already
attached, and disabled all produce zero attempts; a minute of polling while "not ready" produces
exactly one; and a failure produces one followed by silence.

### Asking the game to render a tooltip

Items the hook captures arrive with their tooltip — the game drew it as the item was looted.
Items that arrive from a *file* do not: the transfer stash and the GD Stash format both record
what an item **is**, not how the game renders it. Those items get a base name from ItemTemplate
and numbers from the seed engine, but none of Grim Dawn's own colour-coded text.

The hook can produce the real thing, and `OnDemandSeedInfo` was ported and running the whole
time — **nothing ever asked it to**. Both halves of the pathway existed (the hook, and
`replica/from_ia` + `replica/to_ia` in the bridge) with no client. `ReplicaService` is the
missing half.

The protocol is taken from the hook's own source rather than from upstream's client, because the
hook is what actually reads the files:

- **Request**: one semicolon-separated line in `replica/from_ia[/mod]`, 15 fields (12 pre-
  Asterkarn). A field out of place yields a *different item*, not a parse error, so the order is
  transcribed rather than remembered. The hook deletes the file as it reads it.
- **Response**: JSON in `replica/to_ia` with `playerItemId` and a `stats` array of
  `{text, type}` — which is exactly a `ReplicaItemRow`.

Requests only go out while the game is running with the hook attached; otherwise they would pile
up in the prefix for a reader that is not there.

**The request file is named after the item id**, and that is load-bearing. "Has no tooltip yet"
stays true until the game answers, so without knowing which items are already outstanding the
poll loop re-requests the same item every two seconds. Naming the file after the id makes the
filesystem the record of what has been asked, which survives a host restart and is shared with
the CLI. Verified: 30 items, first pass writes 20 (the cap), second and third write nothing, and
when five are answered the next pass requests five *different* items.

### The transfer stash: read, never written

`iagd stash` reads Grim Dawn's shared stash; `--import` copies its contents into the collection.
That is the migration path — everything already in the stash, without depositing it again item
by item.

**Nothing writes this file, here or upstream.** Upstream's `TransferStashService.Deposit` writes
CSV files for the hook to collect, which is exactly what this port already does. A note in
BACKLOG.md used to describe stash writing as a second deposit path; that was an assumption, and
a wrong one. It is a relief: a malformed transfer file loses a player's shared stash.

**This reads a version upstream cannot.** Upstream's `Stash.Read` accepts versions 4, 5, 8 and
9. The game on this machine writes **version 11** — its most recent commit (2026-08-07) predates
the game build (2026-08-10), so upstream rejects the file outright and its stash features are
currently dead against a current install.

The v11 delta was established empirically and is narrow: **one extra uint per item**, after
`Rerolls` and before the grid offsets. The evidence, against the real 29 KB file:

| Assumed layout | Result |
|---|---|
| upstream's (no extra field) | first tab's block terminator already misaligned |
| **+1 uint per item** | **10 tabs, 222 items, 222/222 valid `records/….dbr`, every block terminator exact** |
| +2 uints per item | misaligned |

A one-field desync cannot produce 222 well-formed record strings and correct block framing by
luck. The crypto and block framing are still upstream's `GDCryptoDataBuffer` and `Block` — the
genuinely hard part — with only the field walk done here, so the version gate is the only thing
diverging.

The extra field is zero on every item in the sample, so its meaning is unknown; it is consumed
and discarded rather than guessed at.

### Stacks are items too

A stash holds 27 aether clusters as *one* entry with `StackCount = 27`. Both file-based import
paths read that field and threw it away, so importing a stash produced one of each.

Measured on the real stash: 224 rows representing **371 actual units**, with an ×80 stack of
experience potions among them. 147 units would have gone missing, silently, and the collection
would simply have looked slightly wrong.

`StackCount` now travels through `LootedItem`, both importers and the GD Stash export, and is
shown on the item card. The hook's loot CSV has no such column — it reports items one at a time
as they are looted — so live capture is unaffected.

### Mods layer, they do not replace

A mod ships only the records it adds or changes and layers over the base game — measured here,
Crucible's `SurvivalMode.arz` is 7 MB against the base game's 58 MB. So a modded item usually
uses a *vanilla* record and only sometimes a mod-defined one.

`ItemTemplate` is therefore keyed by `(Mod, Record)` rather than by record alone, and every
query resolves mod-first with a vanilla fallback — two indexed joins and a `COALESCE`, rather
than a correlated subquery per row. The collision this guards against is real, not theoretical:
with one mod installed, `potion_constitutionc01.dbr` is defined by both vanilla and the mod, and
the old single-key table silently kept whichever was written last.

Verified with a synthetic modded install: an item tagged with the mod and using a mod-defined
record resolves to the mod's template; an item tagged with the mod but using a vanilla record
falls back; a vanilla item is unaffected by either.

Two things upstream gets right that are easy to miss, and are ported:

- **Crucible is not a mod.** `mods/survivalmode` is in the *expansion* list, because the hook
  explicitly skips the mod name in Crucible (`InventorySack_AddItem.cpp`), so its items are
  vanilla items and must resolve against vanilla templates.
- **The expansion list is a range, not a list.** Upstream scans `gdx1..9` and `survivalmode1..9`
  so a future expansion needs no code change. This port had `gdx1`, `gdx2`, `gdx3` hardcoded and
  was missing `survivalmode*` entirely — fixing it added 170 records.

Templates for a mod that has since been uninstalled are dropped on the next parse. Otherwise
they keep winning the mod-first lookup for items still tagged with that mod, naming them from a
mod the player no longer has.

### Pet bonuses

A pet bonus is a `petBonusName` on one of the item's records pointing at *another* record, and
upstream stores every stat of such a target under a `pet`-prefixed name — so "attack speed on
the pet" and "attack speed on me" are different fields rather than the same field in a
different table. That single convention is what makes `petScope=1` a rename of the stat names
being searched for, and nothing else.

Two consequences worth knowing, both upstream's:

- The classification is **global**. A record used as a pet bonus anywhere is a pet record
  everywhere, so the prefixing pass scans the whole database, not just owned records. Narrowing
  it would prefix the same record inconsistently depending on what you happened to own.
- `PlayerItemRecord` is the entire signal. A row in it that is *not* one of the item's own
  core records is by definition a pet-bonus target — that is what `PetRecordCondition` tests,
  IFNULLs and all, and upstream's comment explains why those IFNULLs are load-bearing.

Measured against this installation: **805 pet-bonus records.**

### The stat filter must not be applied to everything

`StatFilter` gates what reaches the seed engine — and *only* that. Upstream stores every stat
row in `DatabaseItemStat_v2` and applies the filter when reading rows for the engine; fields it
excludes are still there for other queries.

This port loads stats on demand rather than storing them, which made it tempting to filter once
at load time. That would have been wrong: the summoner filter searches for `spawnObjects`,
which the filter drops (zero value, not whitelisted), so it would have matched nothing at all —
silently, since "no summoner items" is a plausible-looking answer. `ItemDatabase.LoadAllStats`
therefore takes `applyStatFilter`, on for the engine and off for everything else.

### Upstream's settings page, item by item

Comparing against the controls on `SettingsWindow`, rather than against the settings *keys*
(which include a lot that no UI exposes):

| Upstream control | Here |
|---|---|
| Transfer to any mod | **Implemented** — gates the choice of target stash |
| Hide Skills | The *toggle* is absent because the thing it hides now exists (below); worth adding only if the display proves noisy |
| Delay when searching | Fixed at 200 ms, which is upstream's "on" value. A toggle for 0 ms would be a UI preference, not a capability |
| Zip backups + Define location | Not ported: it zips Grim Dawn's **save files** to a chosen folder, and on this machine those live in Steam Cloud's `userdata` tree, already replicated. `iagd backup` covers the collection, which is the part nothing else protects |
| Using IA on multiple PCs | Deferred with cloud sync, which is the only thing it affects |
| Dark mode, Minimize to tray, Start minimized, Auto dismiss notifications, Automatic Updates | Not applicable — WinForms window behaviour and a Windows self-updater |

Not settings, but reachable from the same area: `OnlineSettings` is cloud (deferred) and
`ModsDatabaseConfig` configures which install and mod to parse — this port discovers both, with
`gameDir` as the manual override.

### Granted skills are shown, not just filtered on

The audit above turned up something better than a setting. `cbHideSkills` hides a *display* —
upstream puts the granted skill on the item, with name, level, description and trigger
(`JsonItem.Skill`). This port had the data (`itemskill_v2`, populated at parse time) and the
filters that use it, and never showed it.

The item panel now does. Whether a skill is a proc or an action-bar skill is read from the
presence of a trigger, which is also the reason upstream's "grants a skill" filter returns proc
items despite its wording.

Verified against the collection: the Plagueborne Revolver shows *Bloody Pox*, triggered; Nar's
Arcane Destroyer shows *Elemental Seal*.

### Detecting that the game data went stale

A Grim Dawn patch rewrites the item databases. Nothing breaks when it does — the item list still
renders, every name, level and icon simply becomes last patch's. Changing the language and
forgetting to re-parse fails the same silent way.

`iagd parse` therefore records what it was made from: the newest modification time across every
`.arz` and `Text_*.arc` it reads (upstream's `ParsingService.GetHighestTimestamp`), and the
language it used. `GameDataStatus.Check` compares both, and the CLI status and the UI status bar
say which one is out of date. Upstream keeps the same two facts, as
`GrimDawnLocationLastModified` and `ParsedLanguageCode`.

Verified by ageing the recorded timestamp and by switching language — each produces its own
message, and clearing them silences it.

### Settings, and the one that is not optional

Two settings files, deliberately:

- `~/.config/iagd-linux/settings.json` — this port's own.
- `<prefix>/…/EvilSoft/IAGD/settings.json` — **upstream's, and the hook DLL's contract.**

The second is not configuration, it is a prerequisite. `HookDll/Hook/SettingsReader.cpp` reads
three keys out of it, and one of them decides whether the hook works at all:

| Key | Purpose |
|---|---|
| `persistent.isRunningInWine` | Must be true, or the DLL uses the shared-memory path that does not work under Proton |
| `local.stashToLootFrom` | Stash tab to take from, 0 = last |
| `local.stashToDepositTo` | Stash tab to place into, 0 = last |

Until this was written, nothing in this port produced that file — only `probe --setup` did. It
worked here purely because a stale Windows install had left one behind. On a clean machine the
hook would have fallen back to shared memory and captured nothing, silently.

`BridgeSettings.Apply` now runs on every host start, so a prefix rebuilt by Steam repairs
itself. It **merges** rather than rewrites: on a machine that has run the Windows tool, that
file holds cloud credentials and window geometry, and replacing it would log someone out of a
service this port does not implement. Verified — after a write, the only changed keys were the
two stash indices, with `cloudAuthToken`, `cloudUser` and `windowPositionSettings` untouched. A
malformed file is refused rather than replaced, for the same reason.

The settings UI shows what the hook will *actually* read alongside what is saved, because those
can disagree and the symptom of a disagreement is loot quietly not arriving.

**`transferAnyMod` used to be a setting that did nothing.** It was stored, listed by
`iagd settings`, returned by the API and rendered as a checkbox — and no code read it. That is
worse than a missing feature: the UI asserted something false. It now gates what upstream gates,
namely the choice of target stash. Upstream asks with a stash picker shown only when the setting
is on; here the transfer request carries an optional target and the host ignores it unless the
setting allows it, so a stale client cannot move items into a stash the user never opted into.

Off by default, in both. Hardcore and softcore are separate stashes and the game cannot move an
item back.

### Localisation

`iagd parse` reads the language from settings and offers only what the installation ships
(`ItemDatabase.FindLanguages`) — the set varies by release and by what was downloaded, so a
hardcoded list would let someone pick a language that silently does nothing.

Two things make this less trivial than swapping a filename:

- **Translations are partial, so English is always loaded underneath.** The chosen language is
  layered over it. Measured here: `Text_DE.arc` translates 17,727 of English's 19,138 tags, and
  the expansions ship English only. Loading German alone would leave items *unnamed* rather than
  English-named. Upstream reaches the same place by passing its English language object in as
  the per-lookup fallback.
- **A localised tag holds every grammatical form at once** —
  `[ms]Mächtiger[fs]Mächtige[ns]Mächtiges…` — so a German item name arrives as
  `[fs]Blutschwurpistole`. English tags carry no markers, which is why nothing needed this
  before. Resolution uses upstream's own `ItemNameCombinator` from the referenced
  `StatTranslator` project rather than a reimplementation: the parsing is fiddly (variants
  contain spaces; adjacent tags have no separator) and upstream's comments record a bug it has
  already fixed there.

Composed names — prefix + quality + core + suffix — come from the game's own tooltip through the
hook, so they are already in the player's language and already agree grammatically. Only the
template names needed this.

### The window has to keep itself up to date

The host knew the game was running and the hook was attached; the window said "Grim Dawn is not
running" for the rest of the session. Status was pushed only after a merge or a settings change,
and the page asked for it once when it loaded — so anything that changed while nobody was making
requests never reached the screen.

The import loop now sends the status whenever it differs from what was last sent. It ticks every
two seconds anyway, `HostStatus` is a record so the comparison is a value comparison, and nothing
is sent while nothing moves. Upstream's window updates itself continuously for the same reason;
this is the equivalent for a UI at the end of a socket.

**Retrying after an abort is what a player feels.** The DLL refuses to attach at the main menu
and at character select, so the interval between attempts is the delay between loading a
character and the hook going live. Upstream retries about once a second, which it can afford —
an in-process FindWindow and a subprocess with a one-second timeout. Each attempt here launches
Proton, so the interval is eight seconds: often enough not to be noticed, rarely enough not to
tax a machine that is running a game.

### Injecting only once the game can take it

Upstream never looks for a *process*. `InjectionHelper` calls `FindWindowEx(NULL, prev, "Grim
Dawn", NULL)` and injects into whoever owns that window — window class, not image name. That is
the readiness gate, and it is the whole difference between attaching cleanly and mauling a game
that is still starting: the process exists within a second of launch, while the window only
appears once the loader has finished and the engine is up.

This port had been passing `--attach-name "Grim Dawn.exe"` instead, so every attach attempt
found the process immediately and fired a `LoadLibraryA` remote thread into a half-initialised
game. `LoadLibraryA returned NULL` each time, and the injector's own retry loop kept going —
twenty-six attempts in one launch, observed. The game does not enjoy that.

Three changes, all of them upstream's behaviour:

- `--attach-window` in the injector patch: `FindWindowExW` **by class**, then
  `GetWindowThreadProcessId` — the same call pair upstream uses, and the third argument of
  `FindWindowEx` is `lpszClass`, so class is what upstream matches. No window means no injection
  attempt at all, reported as `NOT READY` rather than as a failure.

  An earlier version of this also fell back to matching the window *title*, reasoning that a
  game whose class changed would still be found. That was a bad trade and it cost a crash:
  Steam runs inside the same prefix and its windows are titled after the game being launched, so
  during startup the fallback could resolve to Steam's process and the injector would push a hook
  meant for Grim Dawn into it, mid-launch. The class match is now the only one, and the owner is
  confirmed against the expected image name (`--attach-name` alongside `--attach-window`) so a
  matching window belonging to anything else is ignored. Verified in the prefix: a title-only
  match is refused, and a class match whose owner is the wrong process is logged and skipped.
- **One injection per invocation.** `HookAttacher` now sets `RETRY_MS=0`, so the cadence is
  `AutoAttachService`'s and not a loop inside the injector. Upstream injects at most once per
  pass for the same reason.
- A missing window is a *not ready* outcome, so the service keeps its short 10 s interval
  instead of backing off to five minutes as it would after a real failure.

Verified in the prefix: with nothing running the injector waits, logs `NOT READY: no window of
'Grim Dawn' after 3000 ms` and injects nothing; pointed at a live Wine window it reports
`Found window 'Wine configuration' (PID: 220)` and makes exactly one attempt.

**The injector must not own a console.** Wine gives a console-subsystem program a console, and
when the program is launched by a service rather than from a terminal, that console is a window
— which appeared over the game every time the attacher polled. On a "minimise on focus loss"
setup that took the game down with it, and Grim Dawn stops its game loop while minimised, so it
was a genuinely bad experience rather than a cosmetic one. Upstream avoids the same window with
`CreateNoWindow` on its injector process (`InjectionHelper.InjectXBit`); there is no equivalent
to set from the Linux side, so the binary itself is linked `-mwindows` with a `wWinMain` that
forwards to `wmain` (`patches/proton-injector/Makefile.patch`). Output goes to `--log-file`,
which the attach script now prints a tail of, since nothing reaches a terminal any more.

While testing this, the attach script was also found to report `ABORTED` for a run that never
injected: it swept stale markers only when a `.PID` file existed, so an old `.ABORTED` from a
previous session survived for ever and was read as this run's verdict. Markers are now swept on
either kind, and an abort only counts if it was written after the run started.

### Captured tooltips are normalised, not stored raw

The hook asks the game for its **detailed** tooltip — the one a player sees holding Ctrl —
because that is the view carrying every number. It comes back annotated with the range each stat
could have rolled in ("+21% Physical Damage [16-24]") and full of colour codes. Upstream strips
both before storing, in `ItemReplicaParser`:

```csharp
Regex.Replace(Regex.Replace(text.Trim(), @"(\^.?)", ""), @" (\[|\().+(\]|\))$", "")
```

This port had been storing the raw text, so its cards showed ranges the Windows tool does not,
and the game does not either. The same two regexes now run on capture, together with upstream's
rule that lines beginning "Tag not found" are dropped. Rows stored before this are rewritten
once, guarded by a marker in upstream's own settings table, because otherwise a collection keeps
showing the old text until every item happens to be captured again.

The second regex takes *any* trailing bracketed group, not only a numeric range, so
"Bloody Pox (33% Chance when Hit)" is stored as "Bloody Pox". That is upstream's behaviour and
is reproduced rather than corrected; the card shows the trigger separately anyway, from
`itemskill_v2`.

### Reading the game's data is the client's job

Upstream has no command line. Its Grim Dawn tab lists the installations it has found, **Load
Database** reads the selected one, and a parse also happens by itself at startup when the game
has been patched or was never read. Everything after that — icons, names, skills, then the
analysis pass — follows without anyone being told to do anything.

This port had all of that in `iagd parse`, so the UI's contribution was a line of text telling
the user to open a terminal. The parse now lives in `IAGrim.Core.GameData.GameDataParse`, which
the CLI and the client both call, and the client runs it:

- **at startup** when `GameDataStatus` reports the data stale — never parsed, game patched, or
  the language changed;
- **when the installation or the language changes**, since a setting that changes what the data
  means has to be followed by reading it again;
- **on demand**, from the Grim Dawn tab's Load Database button (`POST /api/parse`).

One at a time, guarded by a semaphore, because a parse replaces every template and reassigns
every record id. The analysis pass runs immediately after, since a parse clears the rolled values
it depends on. Progress reaches the status line rather than a terminal.

Verified on a collection with its templates deleted: the client noticed at startup, reported
`scanning database.arz` while it worked, and finished with 29,587 templates, 908 skills, 4,546
icons and nothing left needing analysis — no commands run. The button and a game-folder change
both start the same work, and a second request while one is running is refused rather than
queued.

### The analysis pass runs itself

Upstream checks `RequiresStatUpdate` at startup and runs a parse when more than fifty items lack
a rarity, or the skills table is empty. That is not a nicety: an item with no rarity is drawn in
the "unknown" colour instead of as the epic it is, and every record-driven filter matches
nothing.

This port had left it as `iagd stats`, to be run by hand — so a freshly merged collection sat
there grey and unfilterable until someone read the documentation. `StatRefresh` now makes the
same check when the host starts, on upstream's threshold, plus the case above where a re-parse
has emptied `DatabaseItemStat_v2`. It runs in the background and reports through the same
channel a merge uses; the collection stays usable throughout, just incompletely described. The
command still exists for when you want it immediately.

### Icons come from two archives, not one

A few items are world objects the player can pick up, so their textures live with the level art
rather than with the item icons. Lokarr's four pieces are the visible case: they had names and
stats in the list and no picture at all.

Upstream reads both — `LoadIconsOrWarn(items.arc)` and `LoadIconsOrWarn("Level Art.arc")`, for
the base game and every expansion — and this port read only the first. It reads both now.

The cost is small despite those archives being gigabytes, because the extractor skips anything
over 45 KB and almost no level art is that small: measured here, the four of them add about six
seconds to a parse and seven icons, four of which are Lokarr's. Upstream's own storage folder on
this machine holds 4,030 icons including those signs, which is what pointed at the answer.

### What may enter a collection

Item Assistant has never collected everything the game drops. The hook decides as an item is
looted (`InventorySack_AddItem::IsRelevant`): no components, no crafting materials, no quest
items, nothing from the miscellaneous drawer, no salt bag, nothing that stacks, and nothing under
`storyelements` apart from Lokarr's four pieces and the two Gazer Man torsos, which are real gear
the game happens to file there.

Anything the hook loots is therefore already filtered. This port's **file imports are not** — a
transfer stash, a GD Stash file, another collection — and they had no equivalent rule, so a stash
full of components would have gone straight in. `ItemAdmission` carries the hook's list and all
three paths ask it first, reporting what they refused rather than silently dropping it.
`verify-item-admission.sh` compares the two lists on every `make verify`, because a copied rule
is a rule that drifts.

Verified against real records: a revolver and Lokarr's Gaze are kept, Gazer Man is kept while
another quest torso is refused, and components, potions, scrap, quest items, the salt bag and a
stack of twelve are all turned away.

### Links this port does not carry

Upstream's nav has Discord and Patreon links. They are deliberately absent here and should stay
absent: this is an unaffiliated port, and those are somebody else's community and somebody else's
funding. Reproducing them would imply a connection that does not exist, and would put support
requests for this code in front of people who did not write it.

### Which description a card shows

Upstream's precedence, reproduced: the captured tooltip when the item has one, the computed
description otherwise — its `Item.tsx` renders `replicaStats` *instead of* the computed body
stats.

That order is not about fidelity of colour, it is about completeness. `ItemStatEngine` — whose
files in this port are **byte-identical to upstream's** — returns only the fields that actually
*roll*. So a computed description carries an affix's "4-7 Physical Damage" but not the weapon's
own fixed "17-41 Physical Damage", and no level or attribute requirements. The captured line has
all of it, because the game wrote it.

**What made a captured tooltip look like it belonged to one character was this port storing it
raw.** The hook asks for the detail view, which carries colour codes meaning "better or worse
than what you have equipped" and the roll range behind each value. Upstream strips both before
storing; so does this port now, and with that the two renderings agree. Measured against the
game's own tooltip for a freshly looted revolver, every line matches — flavour text, class line,
weapon damage, attack speed, the affix damage, all four modifiers, the granted skill and both
requirements. The only lines the game shows and the card does not are its DPS comparisons
against the equipped weapon, which are not in the captured text at all.

**A new item is described the moment it lands.** Upstream stores looted items and then updates
their rarity, level and records immediately, as long as fewer than five hundred arrived at once
(`PlayerItemDaoImpl`, `itemsToStore.Count < 500`); only a bulk import defers to the batch. This
port had left all of it to the batch, so a freshly looted epic was drawn in the "unknown" colour
and showed its records' base numbers instead of what it rolled — the item that prompted this was
the only one of 7,482 in the collection without a rarity. `NewItemDetails` now does the same work
on import, from the stat rows already stored, so nothing re-reads the archives.

**The computed description is built upstream's way too.** `ItemStatService.BuildTags` is
reproduced in `ItemStatText`: one row per stat per record at its highest value, the seed engine's
output replacing the numerics of base, prefix and suffix wholesale while their text rows are
carried over, then modifier and pet rows added and numerics summed across records. This port used
to read the roll back from `ComputedItemStat`, which is keyed by item and stat name — so where
two records carried the same stat, one value overwrote the other and a line vanished. That table
stays as it is for the filters, which need the values in SQL; it is no longer what the card reads.

### Never ask for the same tooltip twice

The hook **deletes a request file the moment it queues it**, long before an answer exists. So
"is this item still waiting?" cannot be answered by looking at the directory — and this port was
doing exactly that. Every pass, two seconds apart, it found the same items still lacking a
tooltip and asked again; the game rebuilt the same twenty items on its render thread, forever.
The hook's own log shows it plainly: ids 7463-7482 queued at 18:40:36, and the identical twenty
queued again at 18:40:38.

Upstream does not have this problem because it keeps a `ReplicaCache` of what it has asked,
with the comment *"Don't ask for the same item twice. Esp if the user somehow gets two identical
items in, this would infinitely loop."* The port now keeps the same set, and the service outlives
a single pass rather than being rebuilt every two seconds, which was throwing the memory away.
An item the game never answers for stays unanswered until the next run — upstream's trade too —
and `Reset()` exists for the point after a parse where a previously undescribable item might
become describable.

Measured over five passes against a 7,480-item collection with the hook's delete-on-queue
behaviour simulated: twenty requests per pass, none of them repeated.

Upstream's one rejection in its own serialiser is reproduced with it: a record longer than 255
characters cannot be reproduced by the game, so the item is skipped rather than asked about.

### Asking the game for tooltips costs frames

The hook answers replica requests **on the render thread**: `OnDemandSeedInfo`'s hook into
`Engine::Render` drains up to a hundred queued requests per frame, and answering one means
constructing a real game item and reading its tooltip. So the number of requests outstanding is
a frame-budget decision, not a throughput one. Raising the in-flight cap to 250 to fill a merged
collection quickly made the game stutter; it is back to 20, which at the importer's two-second
pass is around 600 an hour of play — slow, and invisible.

Upstream has no cap because its situation is different: its items arrive by looting, which
captures the tooltip at the same moment, so a backlog of thousands never forms. This port can
have one after a merge, and the right answer there is patience — the cards are readable
meanwhile, because `ItemStatText` describes them from the game database.

### A re-parse hides itself from the status line

`iagd parse` reassigns every `id_databaseitem`, so it clears `DatabaseItemStat_v2` — the table
every record-driven filter joins against. It does **not** clear each item's `Rarity`, and the
"needs analysing" count was a count of null rarities. The result was a collection that reported
nothing to do while the slot, damage-type, mastery and pet-bonus filters all silently matched
zero items. Found on this machine's own collection, which had 29,587 parsed records and not one
stat row. The status check now asks the table that actually gets cleared.

### Backup, import and export

`iagd backup` copies the collection with **`VACUUM INTO`, not a file copy.** The database runs
in WAL mode, so part of the committed state lives in `-wal` at any moment; copying the main file
alone can silently produce a backup missing the most recent items. `VACUUM INTO` takes a read
transaction, so it snapshots consistently, compacts on the way out, and is safe while the host
is running — which is when backups happen. Ten copies are kept, count-based rather than
age-based so someone returning after six months still has theirs. A copy is taken automatically
before `parse` and before `import-file`, the two operations that touch data they cannot rebuild.

`iagd export` / `iagd import-file` speak the **GD Stash / Mambastash** interchange format, which
is what the Grim Dawn tool ecosystem actually shares items in. The layout is upstream's
`GDFileExporter`, and the reading and writing use `EvilsoftCommons.IOHelper` — upstream's own
primitives, from a project this port already references. That is deliberate: it is a positional
binary stream with a version header and length-prefixed strings, and re-deriving the field order
is exactly the transcription that fails silently, yielding items with their affixes shifted by
one. Versions 1-3 read, 3 written, matching upstream. Import de-duplicates on base record + seed,
so re-importing a file is a no-op rather than a way to clone a collection.

### Deleting an item has to clear four tables

Upstream's schema declares no cascades. This port's earlier schema did, and adopting upstream's
removed them without anything failing at the time.

`PlayerItem.Id` is a rowid alias, so SQLite hands a deleted item's id to the **next** looted
item. With rows left behind, that new item collides with them — and `ReplicaItem2.playeritemid`
is `UNIQUE`, so the collision is a hard failure:

```
transfer an item out  →  loot anything  →  UNIQUE constraint failed: ReplicaItem2.playeritemid
```

`ComputedItemStat` is the quieter half: no unique constraint there, so the new item would simply
have shown the departed item's rolled values.

`LootStore.Delete` now clears `ReplicaItemRow`, `ReplicaItem2`, `PlayerItemRecord` and
`ComputedItemStat` in one transaction, and `Schema.Apply` sweeps orphans on every open so a
database already in that state repairs itself rather than staying broken until someone works out
why looting stopped.

### Collection and set views

`CollectionViewService` ports `ItemCollectionDaoImpl` (the legendary/epic checklist and the
rarity-by-slot aggregate) and `DatabaseItemDaoImpl.GetItemSetAssociations` (sets).

Upstream's collection query pivots `DatabaseItemStat_v2` rows at query time; this schema stores
the same handful of fields as columns on `ItemTemplate`, so the shape differs while the
selection does not — including the `NOT LIKE '%/crafting/%'` exclusion, without which every
legendary appears twice (blueprint records carry the same classification as what they produce).

Sets need two hops: an item's `itemSetName` gives the set's *record*, and that record's
`setName` tag gives its display name. Both are resolved during the parse. Verified against the
case upstream calls out in `ArzParser.IsInteresting` — Lokarr's Spoils lives under
`/storyelements/signs/` rather than `/items/`, and comes back with all four pieces.

Measured: **3,504 collection entries, 200 sets.**

## The seed engine

`src/IAGrim.Core/ItemStats/` is upstream's seed-stat engine, copied essentially verbatim. It
is self-contained, and it is the most delicate logic in the project: a Park-Miller MINSTD
stream replayed in the exact draw order the game uses.

Verified two ways rather than trusted:

1. **Against the published sequence.** From seed 1 it yields
   `282475249, 1622650073, 984943658, 1144108930, 470211272` -- the Park-Miller terms, so the
   priming convention is right.
2. **Against the game itself.** The hook captures the tooltip Grim Dawn rendered, which is
   ground truth for the roll:

   | Game reported | Engine computed |
   |---|---|
   | `27-40 Acid Damage` | `offensiveBasePoisonMin 27`, `Max 40` |
   | `+236% Acid Damage` | `offensivePoisonModifier 236` |
   | `420 Poison Damage over 5 Seconds` | `offensiveSlowPoisonMin 84` x `DurationMin 5` = 420 |
   | `+56 Spirit` | `characterIntelligence 56` |
   | `62-81 Fire/Cold/Lightning` | `offensiveBaseFire/Cold/LightningMin 62`, `Max 81` |

   Internal names differ from displayed ones: Acid is Poison, Spirit is Intelligence. The
   derived case -- 84 per second over 5 seconds rendering as 420 -- is the one that cannot be
   coincidence.

### Matching upstream exactly: the stat filter

The engine is only half the contract. Before any row reaches it, upstream filters:

```sql
AND (val1 > 0 or stat in ( :whitelist ))    -- positive values, or whitelisted text stats
AND NOT stat IN ( :blacklist )              -- presentation/physics fields
```

This is **not cosmetic**. The engine draws from a shared random stream in a fixed field
order, so feeding it one rollable field the game did not roll inserts an extra draw and
silently changes every value after it.

An earlier version of this port fed the engine everything and happened to match on the two
test items -- luck, not correctness. It was including `characterBaseAttackSpeed` at -0.08,
which upstream's `val1 > 0` excludes. `StatFilter` now reproduces both lists verbatim
(15 blacklist, 62 whitelist entries) and is applied at ingestion.

`scripts/verify-stat-filter.sh` parses the lists straight out of upstream's source and
compares them entry for entry:

```
OK    Blacklist matches upstream SpecialIgnores (15 entries)
OK    Whitelist matches upstream SpecialStats (62 entries)
```

`check-upstream.sh` says the file changed; this says whether the change matters. Verified by
adding an entry to upstream's blacklist and confirming it was reported as `upstream only`.

`SeedStatCalculator` returns null when an item carries rollable fields the engine does not
model, because an unmodeled field desyncs every later draw. Those items are skipped rather
than stored approximately: a filter that silently lies is worse than one that omits.

### The game's stat rows: stored, but only the ones that matter

`DatabaseItemStat_v2` is upstream's table of every stat of every record. Measured against this
installation, populating it fully is **4.8 million rows (~274 MB)** across 2,678 fields —
consistent with upstream's own ~210 MB database.

This port fills the same table, restricted to the records the collection actually references,
plus the skill records granted skills point at. On this installation that is **29,852 rows for
949 records** — four orders of magnitude smaller, with identical results for every filter,
because those filters only ever reach the table through `PlayerItemRecord` (owned records) or
`itemskill_mapping` (granted skills). A record nobody owns cannot change an answer about
items somebody owns.

The exception is the collection and set views, which deliberately browse items you do *not*
own. Those read `ItemTemplate` instead — a denormalised row per record holding the six fields
those views need, which is why it is cheap where the full stat table would not be.

The cost is re-parsing the ARZ on a full recompute (about 10 s, two passes: one to classify
pet-bonus records, one to load stats). `iagd stats` is where it happens.

An earlier version of this port stored no game stat rows at all, on the reasoning that they
were an intermediate the seed engine consumed and discarded. That was true right up until the
pet, damage-type, mastery and retaliation filters — all four of which are questions about what
an item's *records* contain rather than what it rolled, and all four of which upstream answers
with a join against this table.

## Why not NHibernate — settled

Upstream's DAO layer uses NHibernate, and it does work on Linux (verified: `BuildSessionFactory`
and `OpenSession` both succeed once `Microsoft.Data.Sqlite` is referenced alongside the driver).
It is not adopted here, and as of 2026-08-11 that is a closed decision rather than a provisional
one — including for cloud sync and buddy sharing, which were supposed to be the trigger to
reconsider.

How upstream actually uses it, measured:

| | Count |
|---|---|
| Hand-written SQL (`CreateSQLQuery`) | 131 |
| ORM queries (`Query`/`Criteria`/`QueryOver`) | 26 |

NHibernate is doing connection management and result mapping for SQL upstream wrote by hand.
That SQL is the valuable asset, and it now runs **verbatim** here, because this port adopted
upstream's schema rather than inventing one.

### The caveat that turned out to be wrong

An earlier version of this section said to revisit the decision before building cloud or buddy
sync, because `BuddyItemDaoImpl` and `BuddySubscriptionDaoImpl` "carry sync-state machinery more
entangled with the session model than the search is". That was asserted without counting. The
counts:

| `BuddyItemDaoImpl` | Count |
|---|---|
| Hand-written SQL | 26 |
| ORM queries | 1 |

It is not ORM-entangled. It is the same shape as everything else already ported.

Two of the other original arguments also moved, in the same direction:

- **`ThreadExecuter`**, cited as a session model that would have to come along, has **0
  references** in upstream today. That argument is gone.
- **The 15 `hbm.xml` mappings** were a cost because this port's schema differed. It no longer
  does, so they would line up — which removes a cost of adopting, but also removes the reason
  to: their SQL already works against these tables.

So the case for adopting NHibernate is weaker now than when it was first declined. Reviewed with
the answers in BACKLOG.md: cloud and buddy sync are both wanted, and both will be built by
porting the SQL, as everything else here was.

