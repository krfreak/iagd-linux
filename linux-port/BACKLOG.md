# Upstream features not ported

What the Windows tool does that this port does not, why, and what porting each would cost.

The point of writing it down is that "not ported" and "not noticed" look identical from the
outside. Everything here is a deliberate omission with a reason; if something upstream does is
*not* on this list and *not* in [PORTING.md](PORTING.md), that is a gap in this document rather
than a decision.

**This document was originally wrong three times, in the same way.** It was written by surveying
upstream's *file tree*, and three entries turned out to describe code upstream never runs — a
wishlist that is really a filter panel, a stash-writing path that does not exist, and a character
parser with no callers. Existing code is not a feature.

So: **before starting anything here, grep upstream for callers of the code it names.** No
callers means there is nothing to port. The remaining entry has been checked that way — cloud
backup and buddy sharing are constructed in `MainWindow` and wired to its UI, so they are real.

This is a port, not a superset: features upstream lacks do not get built here, however useful
they might be, because nothing upstream-facing can then keep them honest.


---

## 1. Cloud backup and buddy sharing

**Upstream:** `Cloud/`, `IAGrim/Backup/Cloud/`, `IAGrim/BuddyShare/`, `BuddyItemsService`,
`BuddySubscription`, `CloudAuthToken`

Account-bound sync to EvilSoft's servers (`https://api.iagd.evilsoft.net`, browser login at
`iagd.evilsoft.net/login`), and sharing a collection with named friends.

**Both are wanted, and both are deferred** (asked and answered, 2026-08-11): cloud sync is part
of how this collection is used, and buddy sharing is used with actual friends — so neither drops
out of scope — but no work is scheduled. The approach below is decided so that whenever it does
start, the design questions are already answered rather than reopened.

**This port** creates the tables (`buddyitems_v6`, `BuddyItemRecord_v2`, `BuddySubscription`) and
the `PlayerItem.cloudid` / `cloud_hassync` columns, so the file stays readable by the Windows
tool, but writes nothing to them and has no auth.

**Approach: port the SQL, no NHibernate.** Decided with the counts in PORTING.md — the buddy DAO
is 26 hand-written SQL statements and 1 ORM query, i.e. the same shape as everything already
ported. The earlier "revisit before building sync" caveat was measured and did not hold up.

### The constraint that shapes the design

> "I don't want to hammer their servers with junk."

This is somebody else's hosted service, run for free, and the account is real. So the work is
not "make sync happen" but "make sync happen without ever sending garbage":

- **Develop against something other than production.** `Uris.Initialize` already has a
  `localdev` branch pointing at `http://localhost:8080` (currently commented out to fall through
  to the cloud host) — that is the hook for a local mock while the protocol is worked out.
- **Upload only what is provably new.** Upstream tracks this with `cloud_hassync` and `cloudid`,
  which this schema already carries; those must drive uploads rather than a "sync everything"
  pass.
- **No retry storms.** A failed upload must back off, not loop.
- **Nothing partially-parsed.** Items whose stats could not be computed, or whose records did
  not resolve, should not be pushed at all.

### The interim that costs nothing

Because this port now writes upstream's exact schema, the Windows IAGD can be run inside the
Proton prefix against the same `userdata.db` purely to perform a cloud sync — accepted as a
fallback *if it is verified to work*, which it has not been yet. Worth testing before writing
any cloud code, since it may remove the urgency entirely.

**Cost.** Large, and partly outside our control: the auth flow is a browser login
(`Backup/Cloud/CefSharp/`), which needs rethinking without CEF — the Photino window can host it,
but that is its own piece of work.

## 2. Deliberately not porting

- **Auto-update** (`UpdateModal`, `DownloadingUpdateModal`). Linux packaging handles this;
  a self-updater inside a Flatpak is actively wrong.
- **Upstream's save-file backup** (`Utilities/Cloud/FileBackup.cs`). It zips Grim Dawn's own
  `.gdc`/`.gst`/`.fow` saves, which is the game's data rather than this tool's, and Steam Cloud
  already does it. `iagd backup` covers the collection, which is the part nothing else protects.
- **Nag screens** (`DonateNagScreen`, `BackupLoginNagScreen`, `LastNagTimestamp`).
- **`RecipeItemsOnly`** — dead code upstream; see PORTING.md. Filing it here would imply it is
  a feature waiting to be ported, and it is not.
- **Character file parsing** (`Parser/Character/*`). The parser exists in upstream's tree and
  has **zero callers**. Character *backup* is a different thing entirely: it zips the raw save
  files and uploads them, never parsing one, and belongs to cloud sync above. Building a
  character reader here would be inventing a feature.
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
  application, not this one.

---

## How to use this list

When `scripts/check-upstream.sh` reports a change in a file that belongs to something here,
there is nothing to port — the feature is absent by choice. The manifest deliberately does not
track those files, so they stay quiet.

If one of these gets built, move it to PORTING.md, add its upstream files to
`upstream-sync.tsv`, and delete the section here.
