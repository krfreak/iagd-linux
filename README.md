# Item Assistant for Grim Dawn — Linux port

A native Linux WIP build of [Item Assistant][iagd], the item stash manager for Grim Dawn. Same
collection, same database file, no Wine for the app itself.

The game runs under Proton; only the small hook DLL that watches your stash is a Windows
binary, and it is cross-compiled here with MinGW. Everything else — the loot importer, the
search, the item stat engine, the UI — is .NET and Preact running natively.

**It is a port, not a fork.** A `userdata.db` written by the Windows tool opens here with the
collection intact, and one written here stays loadable there. Where that constrains the design
it wins, because a collection can represent years of play and there is no export step to fall
back on.

**DISCLAIMER: This is not an official release of Item Assistant. It is a completely vibe-coded port with Claude Opus 5 that**
**aims to reproduce the functionality of the original Windows version on Linux.**
**Use at your own risk. This might destroy your items. Back them up.**
**The original Item Assistant is maintained by [marius00](https://github.com/marius00). Support their effort over at [the official site](https://grimdawn.evilsoft.net/).**
**Do not bug them about the work in this repository nor ask them for support.**

This version has been tested on archlinux with GD running in Proton-GE 11-1 and against the upstream Windows version @1.5.9693.21779.

## What works

Loot capture from a running game, the full item search with upstream's filters, the collection
and set views, transfers to and from the stash, backups, GD Stash import and export, merging
another collection in, mod support, and a desktop window with a tray icon.

`linux-port/BACKLOG.md` lists what upstream has that this does not: cloud backup and buddy
sharing, deferred rather than dropped.

## What is left out

I deliberately left out the cloud backup and buddy sharing features, because I don't want to mangle any data on
the server [marius00](https://github.com/marius00) provides. If you want to use that data, download it to your
machine and then merge it into your collection here to get access. No warranty of inconsistency between the two
versions is provided, but it should be fine as long as you don't use the cloud features on this port.

Translations are copied from upstream, but the UI is English-only. The Preact UI is a single-page app and does
not have a translation framework.

## Building

Needs the .NET 10 SDK, Node 20+, `mingw-w64` and `sqlite3`.

```bash
git clone --recurse-submodules https://github.com/<you>/iagd-linux
cd iagd-linux
make            # prepare + hook + injector + app
make run        # the desktop window
```

`make` on its own is enough from a fresh clone. If you cloned without `--recurse-submodules`:

```bash
git submodule update --init --recursive
```

Other targets: `make cli` (the `iagd` command-line tool), `make host` (headless, serves the UI
over HTTP), `make verify` (the parity checks below), `make package` (an AppImage).

## Layout

```
iagd/                    submodule — upstream Item Assistant, pinned
proton-injector/         submodule — DLL injection under Proton, pinned
patches/                 our diffs against both; nothing of theirs is committed
scripts/prepare.sh       unpacks the pinned commits and applies those patches
linux-port/              the port itself
  src/                   .NET: Core, Platform, Host, App, and the Preact UI
  native/hook/           MinGW cross-build of the hook DLL
  tools/iagd/            the CLI
  scripts/               attach the hook, and the upstream drift checks
build/                   generated; safe to delete
```

See [THIRD-PARTY.md](THIRD-PARTY.md) for what comes from where and why none of it is vendored.

## Following upstream

The port reproduces behaviour from about thirty upstream files, and the risk is never a change
that breaks the build — it is one that silently changes what the data means. So the parity is
pinned by checks rather than by memory:

```bash
make verify
```

It re-extracts the schema, the stat blacklist and whitelist, the rarity and colour rules and
all 50 filter groups from the `iagd` submodule and fails on any drift, then builds a database
from upstream's DDL alone and reads it. `linux-port/scripts/check-upstream.sh` reports which of
the tracked files have changed since the pinned commit.

To move to a newer upstream:

```bash
git -C iagd fetch && git -C iagd checkout <commit>
scripts/prepare.sh hook          # tells you which patches no longer apply
make verify                      # tells you what behaviour moved under you
git add iagd && git commit
```

A patch that fails to apply is the point of the arrangement: it names the file that changed
underneath the port instead of letting a stale copy compile cleanly.

## Licence

MIT — see [LICENSE](LICENSE). Item Assistant is MIT © marius00, proton-injector MIT ©
JokelBaf; neither is redistributed here.

[iagd]: https://github.com/marius00/iagd
