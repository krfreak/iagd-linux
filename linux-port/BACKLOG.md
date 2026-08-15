# Upstream features not ported

What the Windows tool does that this port does not, why, and what porting each would cost.

The point of writing it down is that "not ported" and "not noticed" look identical from the
outside. Everything here is a deliberate omission with a reason; if something upstream does is
*not* on this list and *not* in [PORTING.md](PORTING.md), that is a gap in this document rather
than a decision.

**This document has been wrong four times, in the same way.** It was written by surveying
upstream's *file tree*, and four entries turned out to describe code upstream never runs — a
wishlist that is really a filter panel, a stash-writing path that does not exist, a character
parser with no callers, and an account-migration endpoint nobody calls. Existing code is not a
feature.

So: **before starting anything here, grep upstream for callers of the code it names.** No callers
means there is nothing to port. Every entry below has been checked that way, and what the check
found is recorded next to it — including for the things that turned out to be dead, because a
backlog that keeps re-proposing dead code is worse than one that says so once.

This is a port, not a superset: features upstream lacks do not get built here, however useful
they might be, because nothing upstream-facing can then keep them honest.


---

## 1. Buddy items are stored but never shown

**Upstream:** `SearchController.cs:249` — `var buddyItems = new List<BuddyItem>(_buddyItemDao.FindBy(query));`

Online sync is built (see PORTING.md), and that includes following a friend: their items are
fetched, stored in `buddyitems_v6`, indexed into `BuddyItemRecord_v2`, and removed again when
they transfer them away. All of that is tested against a real server.

**What is missing is the last step.** Upstream runs every search against the buddy tables as well
as the player's own and merges the two lists, marking each buddy item with its owner's nickname
so the grid can show whose it is. This port's `CollectionService` queries `PlayerItem` only, so a
followed buddy's collection is downloaded, kept up to date, and invisible.

That makes the feature half-built rather than absent, which is the most misleading state
available, so it is the first entry here rather than a footnote in PORTING.md.

**Cost.** Moderate and well-understood. `BuddyItemDaoImpl.FindBy` is one SQL statement of the
same shape as the player-item search this port already reproduces — the same filter fragments,
against `buddyitems_v6` joined to `BuddySubscription`, with `NOT S.IsHidden` and the buddy's
nickname selected as the item's `Stash`. The work is in `CollectionService` and in the item card,
which needs a "whose" column; the DAO half is a straight port. `scripts/verify-search-filters.sh`
already diffs upstream's filter SQL against this port's and would extend to cover it.

**Note the hidden flag already works** end to end — `PUT /api/cloud/buddies/{id}` sets it and the
panel shows it — because it is stored on the subscription. It just has no search to affect yet.

## 2. Buddy items have no rendered tooltips

**Upstream:** `ItemReplicaRequesterService.cs:104` — `var items = buddyItemDao.ListMissingReplica();`

Upstream asks the running game to render tooltips for a buddy's items exactly as it does for the
player's own, gated on `!OptOutOfBackups`, writing a request CSV per item and reading the reply
back into `ReplicaItem2` / `ReplicaItemRow` keyed by `buddyitemid`. This port's `ReplicaService`
handles player items only.

Without it a buddy's item shows its name and records but none of its rolled values. Worth doing
**after** entry 1 and not before: tooltips for items that cannot be searched for are tooltips
nobody can reach.

**Cost.** Small once entry 1 exists. The request/reply plumbing is already here; what is missing
is the second source of work and the `buddyitemid` side of the replica tables, which the schema
already has (`idx_replicaitem_buddyitemid` is created and `BuddyStore` already sweeps orphans
from both tables).

## 3. Deliberately not porting

- **Auto-update** (`UpdateModal`, `DownloadingUpdateModal`). Linux packaging handles this;
  a self-updater inside a Flatpak is actively wrong.
- **Upstream's *local* save-file backup** — `FileBackup.Backup`, run by the `BackgroundTask` in
  `MainWindow`. It zips Grim Dawn's own saves plus an `.ias` export to AppData every three days,
  which is the game's data rather than this tool's, and Steam Cloud already covers it; `iagd
  backup` covers the collection, which is the part nothing else protects. This is *not* the same
  thing as character backup to the cloud (`CharacterBackupService`), which shares the same file
  and **is** ported — see PORTING.md.
- **Nag screens** (`DonateNagScreen`, `BackupLoginNagScreen`, `LastNagTimestamp`).
  `BackupLoginNagScreen` is the one upstream shows unprompted to anyone with more than 100 items
  who is not signed in. The Online tab has a sign-in button instead, which is the same offer
  without the interruption.
- **`RecipeItemsOnly`** — dead code upstream; see PORTING.md. Filing it here would imply it is
  a feature waiting to be ported, and it is not.
- **Account migration** (`Uris.MigrateUrl`, `GET /migrate`). The endpoint exists on the server and
  moves a token from the old Azure system; **upstream declares the URL and never calls it**
  (`grep MigrateUrl` finds the declaration and nothing else). The constant is carried in
  `CloudUris` only because `scripts/verify-cloud-protocol.sh` diffs the endpoint list against
  upstream's and would flag its absence. Nothing requests it.
- **Buddy rarity and level-requirement recomputation** (`BuddyItemDaoImpl.UpdateRarity`,
  `UpdateLevelRequirements`, `ListItemsWithMissingRarity`). All three have **zero callers**: the
  buddy loop calls `ListItemsWithMissingName` / `UpdateNames` and nothing else. Both values
  already arrive on the wire and are stored, so there is nothing for them to fix.
- **Character file parsing** (`Parser/Character/*`). The parser exists in upstream's tree and
  has **zero callers**. Character *backup* is a different thing entirely — it zips the raw save
  files and uploads them, never parsing one — and **is** ported. Building a character reader here
  would be inventing a feature.
- **Writing `transfer.gst`** — this entry used to describe writing the shared stash as "a
  second deposit path alongside the hook". There is no such path: upstream's
  `TransferStashService.Deposit` writes CSV files for the hook to collect, exactly as this port
  does, and nothing in upstream ever writes a stash file. Reading it is now implemented; writing
  it would be inventing a feature, and one whose failure mode is losing a player's shared stash.
- **"Desired skills"** — this entry used to list a skill wishlist. There is no such feature:
  upstream's `DesiredSkills.cs` is the *filter panel* (damage, DoT, resistances, misc, classes),
  and the name is misleading. Every filter it contains is now implemented. The mistake was mine
  when this document was written, and it is recorded rather than quietly deleted because a
  backlog that invents features is worse than one that omits them.
- **`MinimizeToTray`, `DarkMode`, `WindowSizeManager`** — WinForms concerns. The web UI is
  dark by default and the window is Photino's.
- **Most of upstream's ~40 settings keys.** A settings store exists now, but keys are
  added when something needs one rather than ported wholesale: `CheckUpdatesDaily`,
  `IsRunningInWine` as a *user* setting, `EasterPrank` and the nag timers describe a Windows
  application, not this one. The seven online-sync keys *are* carried, and which of upstream's
  two settings files each comes from is recorded on `ICloudSettings`.

---

## Closed: cloud backup and buddy sharing

Built, and moved to the "Online backup and buddy sharing" section of [PORTING.md](PORTING.md).
The upstream files are tracked in `upstream-sync.tsv` and the wire format is pinned by
`scripts/verify-cloud-protocol.sh`. Entries 1 and 2 above are what remains of it.

One line is worth keeping, because it was the reason the work was deferred rather than declined:

> "I don't want to hammer their servers with junk."

Three of the four things that were promised against it hold, and are described in PORTING.md: the
tests run against a locally built copy of the (open source) server and refuse any host that is
not loopback; uploads are driven by `cloud_hassync` and `cloudid`; the pacing is the server's own,
with downloads stopping entirely after 31 idle minutes.

**The fourth was not implemented, deliberately.** "Nothing partially-parsed — items whose stats
could not be computed should not be pushed at all" would have meant filtering the upload queue on
something upstream does not filter on. Upstream uploads every unsynchronised item regardless of
whether its rarity and rolled values have been computed yet, and the server stores the metadata
as it arrives; filtering here would mean this port's account holds a different set of items from
the same collection on Windows. The constraint was written before the protocol was understood,
and matching upstream turned out to matter more.

---

## How to use this list

When `scripts/check-upstream.sh` reports a change in a file that belongs to something in
section 3, there is nothing to port — the feature is absent by choice. The manifest deliberately
does not track those files, so they stay quiet. Entries 1 and 2 are different: their upstream
files *are* tracked, because they are wanted.

If one of these gets built, move it to PORTING.md, add its upstream files to
`upstream-sync.tsv`, and delete the section here.
