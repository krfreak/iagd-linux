# Upstream features not ported

What the Windows tool does that this port does not, why, and what porting each would cost.

The point of writing it down is that "not ported" and "not noticed" look identical from the
outside. Everything here is a deliberate omission with a reason; if something upstream does is
*not* on this list and *not* in [PORTING.md](PORTING.md), that is a gap in this document rather
than a decision.

**This document has been wrong five times, in the same way.** It was written by surveying
upstream's *file tree*, and five entries turned out to describe code upstream never runs — a
wishlist that is really a filter panel, a stash-writing path that does not exist, a character
parser with no callers, an account-migration endpoint nobody calls, and half of entry 9. Existing
code is not a feature.

So: **before starting anything here, grep upstream for callers of the code it names.** No callers
means there is nothing to port. Every entry below has been checked that way, and what the check
found is recorded next to it — including for the things that turned out to be dead, because a
backlog that keeps re-proposing dead code is worse than one that says so once.

**Entry 9 is the instructive one, because the rule above was in place when it was written and did
not catch it.** The check was run against upstream's *handler* for a message and stopped there.
A handler is not a sender: nothing in either tree emits `TYPE_GameInfo_SetModName`, so upstream's
handler for it has never once run. When an entry names a message, an event or a callback, the
thing to grep for is what *raises* it.

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

## 3. Granted skills cannot be hidden

**Upstream:** `ItemStatService.cs:170` — `if (!HideSkills) { ApplySkills(items); }`, gated on
`PersistentSettings.HideSkills` and bound to `cbHideSkills` on the Settings tab.

Upstream lets a player leave granted skills out of what an item shows. The checkbox drives two
paths: `ItemStatService` skips `ApplySkills` so the skill is never attached to the item at all,
and `MainWindow:288` pushes the same flag to the browser as `SetHideItemSkills` so the card stops
drawing the block. This port always draws it — `item__skill` on the card, `skill` in the detail
panel — and has no key for it.

Of the settings on upstream's Settings tab that are live and missing here, this is the only one
that changes what an item *shows* rather than how the window behaves — which is why it leads the
settings entries rather than sitting with entries 7 and 8.

**Callers checked:** live on both paths.

**Cost.** Small. One `AppSettings` key, one checkbox on the Settings page, two conditionals in
`main.tsx`. The stat-line half of upstream's implementation does not transfer: this port composes
the card from a `skill` object rather than from a rendered stat list, so not drawing the block is
the whole of it.

## 4. Import and export cannot be reached from the window

**Upstream:** `SettingsWindow.cs:123` — `new Popups.ImportExport.ImportExportContainer(...)`,
behind the "Import/Export" button in the Actions group.

Upstream's Actions group opens a dialog that both reads and writes collections: `ImportMode`
offers GD Stash and IA Stash as radio buttons, `ExportMode` writes an `.ias` file.

**All of this is built here and tested — it just has no button.** `iagd export`,
`iagd import-file` and `iagd stash` cover the same ground from the CLI, which means the feature
is present for anyone who reads the README and absent for everyone who opens the window. That is
a worse state than not having it, and the same trap entry 1 describes.

**Callers checked:** live; the dialog is reachable from the Settings tab in a stock build.

**Cost.** Small, and none of it is new logic. An endpoint pair over `ItemExport`, and a Settings
group that reuses `PathSetting` — which already has the host-side file chooser, and already
degrades to a typed path in a browser where no chooser exists.

## 5. Nothing opens the backup folder

**Upstream:** `SettingsController.cs:109` / `:114` — `OpenDataFolder` opens
`GlobalPaths.BackupLocation`, `OpenLogFolder` opens `GlobalPaths.CoreFolder`. Two buttons in the
Actions group.

**Only the first half has a counterpart here.** This port writes database backups to
`LinuxPaths.BackupDir` before every risky operation — an import, a merge, a stash import, opening
someone's existing collection for the first time — and offers no way to find them short of
knowing the XDG layout. `iagd backup` lists them; the window does not mention they exist.

There is no log folder to open. This port logs to stdout and stderr, so the equivalent of "View
Logs" is `journalctl` or the terminal it was started from, and a button cannot help with either.
`LinuxPaths.StateDir` is defined and has **no callers** — it is a slot for a log file nobody
writes, not evidence of one.

**Callers checked:** both upstream buttons live; `StateDir` dead on this side.

**Cost.** Small, with one decision in it. `/api/open` is allowlisted to exactly three support
URLs (`ApiRouter.cs:285`), and widening it to arbitrary paths would turn a link opener into a
"launch anything on the host" endpoint reachable from any page in a browser. Expose a fixed set
of named directories rather than a path parameter — realistically just the backup folder.

## 6. Two stash tabs can be set to the same value

**Upstream:** `StashTabPicker.cs:23` — refuses to close while
`StashToLootFrom == StashToDepositTo && StashToLootFrom != 0`, with a message saying so.

Upstream's picker will not let the loot tab and the deposit tab be the same non-zero tab, because
items then land in the tab being emptied and the two halves fight each other. This port clamps
both to `>= 0` (`ApiRouter.cs:416`) and accepts the collision.

Note that upstream's own default — 0, meaning "the last tab" for both — is exempt, and this port
inherits that. The guard is only about a tab named explicitly on both sides.

**Callers checked:** live.

**Cost.** Two lines and a warning string. This is bug-for-bug parity rather than a feature, and
cheap enough that it should not wait for the entries above it.

## 7. Minimize to tray, and start minimized

**Upstream:** `MinimizeToTrayHandler.cs:27`, `:36` — `StartMinimized` decides whether the window
is shown at all on launch, `MinimizeToTray` whether minimizing hides it to the notifier instead
of the taskbar. Two checkboxes on the Settings tab.

**This entry contradicts one in section 10, and that is the point.** `MinimizeToTray` was written
off there as a WinForms concern, and then `TrayIcon.cs` landed: this port now registers a
StatusNotifierItem, draws an icon, and toggles the window from it. The reasoning that declined
the setting stopped being true when the thing it assumed was absent got built. The bullet in
section 10 has been narrowed to `DarkMode` and `WindowSizeManager`, which are still genuinely
WinForms-only.

**Callers checked:** both live. `DarkMode` is live upstream too, but stays declined for a
different reason — it is N/A here rather than dead, since the web UI has no light theme to switch
away from.

**Cost.** Moderate, and the cost is Photino's rather than ours. Both settings need window-state
control the tray icon does not currently exercise: `StartMinimized` means not showing the window
on launch while still standing up the host and the tray, and `MinimizeToTray` means intercepting
minimize. Worth confirming Photino can do the second before starting either.

## 8. Two toggles whose defaults this port hardcodes

**Upstream:** `CefBrowserHandler.cs:296` — `IsProgramActive.IsActive() ||
AutoDismissNotifications` decides whether a notification fades. `SplitSearchWindow.cs:229` —
`UpdateListViewDelayed(PreferDelayedSearch ? 200 : 0)`.

Both are live upstream, and this port already behaves as one setting of each:

- **Auto dismiss notifications.** The toast here always fades after four seconds
  (`main.tsx:2019`). Upstream fades whenever the window is focused *regardless* of the setting,
  so the checkbox only decides what happens to a notification that arrives while the user is in
  the game — which is most of them, and the reason the setting exists.
- **Delay when searching.** The port debounces every search by 200 ms (`main.tsx:1822`), which
  is exactly upstream's *enabled* state. The setting turns the delay off, for people who would
  rather have the list flicker than wait.

**Callers checked:** both live.

**Cost.** Trivial for each — a key and a conditional. Listed together, and last among the
settings, because neither changes what the port can do: they change a default that is currently
not a choice. Adding them is honest; declining them is defensible on the grounds that a settings
page full of toggles nobody moves is its own cost. What is *not* defensible is leaving them
undocumented, which is why they are here rather than in section 10.

## 9. The hardcore filter does not follow the running game

**Upstream:** `MainWindow.cs:423` and `:431` — on `TYPE_GameInfo_IsHardcore` and
`TYPE_GameInfo_SetModName`, `ModSelectionHandler.UpdateModSelection` switches the visible mod and
hardcore filter to whatever the game just reported.

**This entry was written as "the mod filter", and that half of it is dead.** `TYPE_GameInfo_SetModName`
(message 21) has **no sender anywhere** — not in this port's hook, not in upstream's `HookDll`.
The constant is declared in `MessageType.h` and nothing ever emits it; the mod name is read
*inside* the hook to choose the loot and deposit folders (`InventorySack_AddItem::GetModName`)
and is never put on the wire. Upstream's handler at `MainWindow.cs:431` is unreachable.

**That is the fifth time this document has proposed dead code, and the first time since the rule
at the top was written.** The rule was followed for the entry's *other* half and not for this one:
"upstream has a handler for it" was taken as evidence a message exists. A handler is not a sender.

**The hardcore half is live**, and is what remains of this entry. `SetHardcore::HookedMethod`
sends message 20 with a one-byte payload whenever the game calls `GameInfo::SetHardcore`, and
`InventorySack_AddItem.cpp:179` sends message 47 (`_via_init`) on the same value; the hook is
constructed and enabled in this port's build (`dllmain.cpp:315`), and every queued message is
written to `linuxhack/*.msg` under Wine mode (`dllmain.cpp:214`). So the state arrives here and
is thrown away: `HookMessageQueue.Drain` parses the 8-byte header and drops the payload.

Start Grim Dawn on a hardcore character and upstream's list follows you there without being
asked. This port picks a branch once, at startup, from whatever the collection happens to contain
(`main.tsx:1857`) and never revisits it.

**Not on upstream's settings page**, and included here anyway: its key (`AutoUpdateModSettings`)
defaults to `true` and has no checkbox anywhere in the tree, so it is behaviour rather than a
preference — the kind of thing that is invisible in a settings survey and obvious the first time
someone plays two characters.

**Callers checked:** message 20 and message 47 both have senders and a live hook. Message 21 has
no sender in either tree. Porting the hardcore axis alone therefore reaches *full* runtime parity
with upstream, because the mod axis never fires for upstream either.

**Cost.** Moderate, and the only entry here that reaches below the UI: `HookMessageQueue` must
keep the payload it currently discards, and the state needs pushing to the UI over the existing
event socket. One trap is already documented at the drain site — the first pass on an existing
install clears every message the hook has ever written (522 files on one machine, some weeks
old), so acting on a drained value must exclude that first sweep or the filter will follow a
session from a fortnight ago.

## 10. Deliberately not porting

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
- **`DarkMode`, `WindowSizeManager`** — WinForms concerns. The web UI has no light theme to
  switch away from, and the window is Photino's. This bullet used to name `MinimizeToTray` as
  well; it no longer does, because this port grew a tray icon after the bullet was written. See
  entry 7.
- **`BackupCustom` and `BackupCustomLocation`** — the "Zip backups" checkbox and its "Define"
  link on upstream's Settings tab. They are visible settings-page controls with nothing behind
  them but `FileBackup.Backup`, which is declined above: the pair only chooses a *second*
  destination for the save-file zips. Named separately because a survey of the settings page
  finds two controls, not one feature.
- **The rest of upstream's ~40 settings keys.** A settings store exists now, but keys are
  added when something needs one rather than ported wholesale: `CheckUpdatesDaily`,
  `IsRunningInWine` as a *user* setting, `EasterPrank` and the nag timers describe a Windows
  application, not this one. The seven online-sync keys *are* carried, and which of upstream's
  two settings files each comes from is recorded on `ICloudSettings`.

  This bullet used to say "most", and covered `HideSkills`, `AutoDismissNotifications` and
  `PreferDelayedSearch` by implication. It no longer does: all three are live upstream and are
  now entries 3 and 8. A blanket that hides live features is the same failure this document
  opens by describing.

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
section 10, there is nothing to port — the feature is absent by choice. The manifest deliberately
does not track those files, so they stay quiet.

**Entries 1 and 2 are tracked; entries 3 to 9 are not, and should not be.** The manifest catches
silent divergence in logic this port has *already* copied, and none of these have been built yet —
there is nothing to diverge. Tracking them would mean adding `MainWindow.cs` and
`SplitSearchWindow.cs`, which churn upstream constantly, and a manifest that cries wolf is worse
than one with gaps. The tripwire that matters is already set: `IAGrim/UI/Tabs/SettingsWindow.cs`
is tracked with the note "new controls here are new settings to consider", so a new checkbox on
upstream's settings page surfaces as a reported change whether or not anything below is built.

If one of these gets built, move it to PORTING.md, add its upstream files to
`upstream-sync.tsv`, and delete the section here.
