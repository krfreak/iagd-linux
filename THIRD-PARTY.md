# Third-party code

This repository holds a Linux port of [Item Assistant for Grim Dawn][iagd]. It builds on other
people's work, and the rule here is that **their code stays theirs**: what is committed is the
porting work, not copies of the projects it depends on.

Both projects below are MIT licensed and could legally be redistributed. They are not, because
a submodule and a patch say something a copy does not — that this is a port tracking upstream,
not a fork of it, and that the code you are reading is the part that is actually mine.

## Pinned as submodules

| Project | Licence | Pin | What this repository takes from it |
|---|---|---|---|
| [marius00/iagd][iagd] | MIT (c) 2019 marius00 | `5ba6ec4` (`1.5.9715.11589`) | The hook DLL sources, and the behaviour the port reproduces |
| [jokelbaf/proton-injector][injector] | MIT (c) 2026 JokelBaf | `86ec625` (`v4.0.0`) | DLL injection into a Proton-hosted process |

Neither is vendored. `scripts/prepare.sh` unpacks each pinned commit into a generated,
gitignored directory and applies this repository's patches on top:

| Patch series | Applies to | Size |
|---|---|---|
| `patches/hook/*.patch` | `iagd/HookDll/Hook` → `linux-port/native/hook/generated/` | 11 files, ~500 lines — the MinGW port (see `linux-port/native/hook/PORT.md`) |
| `patches/proton-injector/*.patch` | `proton-injector` → `build/proton-injector/` | 1 file, ~200 lines — attach mode (`--attach-name`), needed because Grim Dawn must be launched by Steam and the hook cannot load before the game has initialised |

Of upstream's 28 hook source files, 17 are used unmodified; only the 11 that needed changing
appear as patches. Nothing of theirs is committed here beyond the context lines a diff carries.

## Unavoidably derived, and kept in the port's own source

Being able to read a collection written by the Windows tool means agreeing with it exactly, so
some of upstream's *data* is reproduced in `linux-port/src`, with the origin named at each site:

- the database schema, verbatim (`IAGrim.Platform/Schema.cs`)
- the stat blacklist and whitelist, the rarity and colour rules, and the 50 filter groups
  (`IAGrim.Core/ItemStats/`)
- the search SQL, ported clause by clause

`make verify` re-extracts each of these from the pinned submodule and fails if it has drifted,
so they stay a reflection of upstream rather than a copy that quietly ages.

## Vendored libraries

Small, stable dependencies that are built into the hook DLL, kept in-tree because the MinGW
cross-build has to be reproducible without a network:

| Library | Licence | Location |
|---|---|---|
| MinHook | BSD 2-clause, (c) 2009-2017 Tsuda Kageyu | `linux-port/native/hook/vendor/minhook/` |
| nlohmann/json | MIT, (c) 2013-2025 Niels Lohmann | `linux-port/native/hook/vendor/nlohmann/` |

`compat/boost/` contains no Boost code. Upstream includes a handful of Boost headers; those
files are ~470 lines of shims written for this port that map the few names actually used onto
the C++17 standard library, keeping the directory layout only so upstream's `#include` lines
compile unchanged.

## Fetched at build time

Resolved by NuGet and npm; not committed.

Photino.NET (Apache-2.0), Microsoft.Data.Sqlite.Core and SQLitePCLRaw (MIT), Tmds.DBus.Protocol
(MIT), log4net (Apache-2.0), Preact (MIT), Vite (MIT), TypeScript (Apache-2.0).

## Not distributed at all

Upstream's compiled `ItemAssistantHook_*.dll` binaries were used as a Phase 0 baseline and are
no longer kept here; `linux-port/vendor/hook-dll/PROVENANCE.md` records which release they came
from and how to obtain it. Grim Dawn's own data files are read from the player's installation
and never redistributed.

[iagd]: https://github.com/marius00/iagd
[injector]: https://github.com/jokelbaf/proton-injector
