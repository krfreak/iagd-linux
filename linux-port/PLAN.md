# IAGD Linux — Port Plan

A Linux-native rewrite of the Item Assistant host application for Grim Dawn, using
`proton-injector` to get the hook DLL into a Proton-hosted game.

**Decisions taken:**

| Decision | Choice |
|---|---|
| UI | Local HTTP/WS API + Preact web UI, wrapped in a Photino.NET window (WebKitGTK) |
| Injection | ~~launch-option wrapper first~~ → **`--attach-name` required**, see below |
| v1 scope | Core loop only: loot → database → search → transfer back into the game |
| Hook DLL | Probe with a current release build, then port to MinGW and own it |

> **Revised 2026-08-10 after the first probe run.** The launch-wrapper-first decision is
> withdrawn — not as a preference, but because it cannot work. The hook DLL crashes if
> injected before Grim Dawn has loaded `game.dll`, which rules out launch-time injection
> entirely. Full diagnosis in [PHASE0.md §7](PHASE0.md). `--attach-name` moves from
> Phase 5 to Phase 1.

**Ground rule: start clean — but clean *binaries*, not a fresh prefix.** The existing Wine
IAGD install in the Grim Dawn prefix is not a reference and must not be built on (§2).
That does **not** extend to wiping the prefix: `<bridge>/data/userdata.db` holds a 210 MB
IAGD item collection, and Grim Dawn's saves are not in the prefix at all (§9). Leave the
prefix in place; the stale IAGD install is inert because `IAGrim.exe` is never launched.

---

## 1. The core insight

Upstream IAGD contains a Wine mode in which **every game↔host channel is a file in one
directory**. No shared memory, no named pipe, no window message on the critical path:

```
<prefix>/drive_c/users/steamuser/AppData/Local/EvilSoft/IAGD/
```

| Channel | Direction | Mechanism |
|---|---|---|
| `linuxhack/*.msg` | hook → host | binary `[int32 type][int32 len][data]`, replaces `WM_COPYDATA` |
| `linuxhack/*.PID` | hook → host | injection-alive marker |
| `linuxhack/*.ABORTED` | hook → host | "game not ready to hook yet", distinct from a failed inject |
| `itemqueue/ingoing/*.csv` | hook → host | items looted from the stash |
| `replica/from_ia/*.csv` | host → hook | items to materialise back into the game |
| `replica/to_ia/*.json` | hook → host | resolved item stats |
| `settings.json` | host → hook | 4 keys only (see §7) |

Source of truth for these formats: `iagd/HookDll/Hook/dllmain.cpp:135`,
`iagd/IAGrim/UI/MainWindow.cs:801`, `iagd/IAGrim/UI/Misc/MessageType.cs`.

This is what makes a native Linux host possible at all: it reads and writes a directory,
and occasionally spawns a Windows executable through Proton. It never loads a Windows API.

---

## 2. Why the existing prefix install is unusable

| | Date |
|---|---|
| Installed IAGD build in `drive_c/Program Files/IAGD` | **2025-11-25** |
| Wine file-based IPC landed upstream (`72c0579`) | **2026-04-17** |
| Repo HEAD | 2026-08-07 |

The installed build predates the file-based IPC by five months. Confirmed by inspection:
the bridge directory has **no `linuxhack/` folder**, and its `settings.json` has **no
`isRunningInWine` key**. That binary has never executed this protocol.

Consequences:

1. Its DLLs are not a usable source for the hook DLL.
2. Its on-disk artifacts cannot be used to validate the message formats.
3. **The protocol is new and empirically unproven.** The most recent commit touching it
   is titled "*Possible* improvement for injection verification under wine/proton" —
   upstream appears to be iterating without a reliable Linux test rig. Treat the source as
   a trustworthy *specification* and its runtime behaviour as unverified. This is the
   single largest risk in the project and is why Phase 0 exists.

---

## 3. Architecture

```
LINUX NATIVE  (.NET 10, zero Windows dependencies)
  IAGrim.Core         Parser, Database, Services, Settings   — ported
  IAGrim.Platform     XDG paths, Steam/Proton discovery, PrefixBridge (inotify)
  IAGrim.Injection    supervises injector runs inside the prefix
  IAGrim.Host         Photino window + Kestrel HTTP/WS API + static WebUI
                              │
                              ▼   shared directory, plain filesystem
INSIDE THE PROTON PREFIX
  Grim Dawn.exe
  ItemAssistantHook_x64.dll     ← ours, MinGW-built (Phase 1)
  injector64.exe                ← proton-injector, MinGW-built
```

Both Windows-side binaries come from the same mingw-w64 cross-toolchain. Nothing in the
build requires Windows.

### 3.1 Repository layout

```
linux-port/
  IAGrim.Linux.sln
  src/
    IAGrim.Core/
    IAGrim.Platform/
    IAGrim.Injection/
    IAGrim.Host/
    WebUI/                 fork of iagd/WebUI
  native/
    hook/                  MinGW port of iagd/HookDll/Hook
    vendor/minhook/        vendored, MIT
  vendor/
    iagd/                  submodule → upstream, for ProjectReference only
    proton-injector/       fork or submodule
  packaging/
```

### 3.2 What to fork vs. what to reference

Reference upstream `.csproj` directly (via submodule) — these are already clean, and this
way upstream Grim Dawn format changes arrive for free:

- `Parser/Parser.csproj` — ARZ/ARC/stash/character parsing, the highest-churn code
- `StatTranslator/StatTranslator.csproj`
- `DataAccess/DataAccess.csproj`

Copy and edit (WinForms or Windows coupling):

- `IAGrim/Database/**`, `IAGrim/Services/**`, `IAGrim/Parsers/**`, `IAGrim/Settings/**`
- `IAGrim/Utilities/**` — needs Linux implementations throughout
- `EvilsoftCommons` minus `SingleInstance`

Discard: `IAGrim/UI/**`, `IAGrim/Theme/**`, `DllInjector/**`.

---

## 4. Phases

### Phase 0 — Probe: does the Wine IPC actually work?  ← ANSWERED: YES

The prebuilt MSVC DLL could not initialise under Proton, so the question was unanswerable
until Phase 1b produced a DLL that could ([PHASE0.md §9](PHASE0.md)). With the ported DLL
injected into a live Proton Grim Dawn:

- the hook reaches `Hooking complete` / `Initialization complete` and installs its hooks
- it writes its `.PID` marker and `.msg` files into the bridge directory
- `tools/probe`, a **native Linux .NET process**, decodes them: `SUCCESS_HOOKING_GENERIC`,
  `REPORT_WORKER_THREAD_LAUNCHED`, `INJECTION_CANCELLED`
- the graceful `.ABORTED` path works too, exercised when the game is absent

**The architecture in §1 is validated.** A Windows DLL cross-compiled on Linux, injected
into a Proton game, communicating with a native Linux host over a shared directory.

Still unexercised: the *behaviour* of the hooked paths (instaloot, stash detection, replica
stats) needs real in-game activity. See "Not yet verified" in
[native/hook/PORT.md](native/hook/PORT.md).

Historical detail — the tooling, the two launch-mode dead ends, and the full elimination
of every environmental hypothesis — is in [PHASE0.md](PHASE0.md).

### Phase 1a — `--attach-name` in proton-injector  *(promoted from Phase 5)*

Launch-time injection crashes the DLL ([PHASE0.md §7](PHASE0.md)), so attaching to an
already-running game is the only viable mechanism, not a convenience.

The logic **must** live inside the injector: Wine PIDs are not Linux PIDs, so a PID
observed from `/proc` is meaningless to `OpenProcess` inside the prefix. The pieces
already exist in `src/main.c` — `find_descendant_by_name`, toolhelp enumeration, and
`inject_into_followed_process` (which already does `OpenProcess(PROCESS_ALL_ACCESS, pid)`
+ inject). Roughly 50 lines.

Add a retry loop as well: the DLL legitimately refuses to attach during loading screens
and character select, writing `.ABORTED`. Upstream's host retries on a timer
(`DllInjector/InjectionHelper.cs`); the injector or the host supervisor needs the same.

### Phase 1b — Own the hook DLL (MinGW port)  ← DONE, instaloot verified

`make -C native/hook` produces a 1.6 MB DLL with no MSVC C++ runtime imports that
initialises inside a live Proton Grim Dawn, installs its hooks, and **correctly loots
items**: base record, seed, name and all stat lines land in a CSV the Linux host reads.

The decisive bug was not the build system but an ABI one: Grim Dawn is MSVC-built and this
DLL is libstdc++, and their `std::basic_string` layouts are incompatible. Every crossing now
goes through `MsvcInterop` (see PORT.md). Full write-up of the
root causes and every dependency replacement in [native/hook/PORT.md](native/hook/PORT.md).

No longer a matter of preference. The shipped binary cannot be made to load under Proton
([PHASE0.md §9](PHASE0.md)), and no host-side work changes that. Everything downstream
waits on this.

Two assets from Phase 0 make the loop fast:

- `tools/loadtest` reproduces the failure in seconds, **without the game running**.
- `tools/canary` is a known-good mingw control that already loads inside the live game,
  proving the target is achievable.


Port `iagd/HookDll/Hook` to build with the mingw-w64 toolchain already in use for the
injector. Measured surface — the readme's "MSVC + Boost 1.78 + Detours" overstates it:

| Coupling | Reality | Action |
|---|---|---|
| Compiled sources | 11 `.cpp`, includes **zero** `Shared/` headers — self-contained | — |
| Detours | 5 functions only: `DetourAttach`, `DetourDetach`, `DetourTransactionBegin`, `DetourTransactionCommit`, `DetourUpdateThread` | → MinHook, vendored |
| `boost::shared_array`, `shared_ptr` | `DataQueue.h` | → `std::shared_ptr<T[]>` |
| `boost::thread`, `boost::mutex` | `DataQueue.h`, `InventorySack_AddItem.h` | → `std::thread`, `std::mutex` |
| `boost::property_tree` + `json_parser` | `SettingsReader.cpp`, `OnDemandSeedInfo` | → nlohmann/json (installed) |
| `boost::filesystem` | `OnDemandSeedInfo.cpp` | → `std::filesystem` |
| `boost::algorithm` (`ends_with`), `range`, `optional`, `lexical_cast` | scattered | → trivial std equivalents |
| `<atlbase.h>` | vestigial, no ATL type used | → delete |
| Assembly, `__declspec(naked)`, SEH | **none present** | — |
| `__thiscall` / `__fastcall` | no-ops on x64; both collapse to the same ABI, MinGW's default for the target | verify, likely nothing |
| Exported symbol strings (`Exports.h`) | MSVC-mangled names resolved against the *game's* `Game.dll` | compiler-independent, unchanged |

`Shared/SQLite.cpp`, `IDB.h`, `ITable.h` carry Boost but are **not compiled into the DLL**
— dead code, ignore.

Validate the port by re-running the Phase 0 probe against the self-built DLL and getting
identical behaviour. Then fix what Phase 0 uncovered:

- **Null-check `fnGetGameEngine`** (`GrimTypes.cpp:145`) — it currently dereferences the
  result of `GetProcAddress` unconditionally.
- **Make the four `GrimTypes.cpp:292-295` globals lazy.** As namespace-scope initialisers
  they run before `DllMain` and log through `g_log`, a global in another translation unit,
  with unspecified initialisation order between them.

Both turn a hard crash into the clean "game not ready yet" abort the code already
implements everywhere else — which is what makes retrying safe.

Ongoing cost to accept: rebasing against upstream `HookDll` changes. Historically ~20
commits touch it per year, mostly Grim Dawn version adaptations in `Exports.h`.

### Phase 2 — Core port

1. Stand up the solution; wire in the three referenced upstream projects.
2. ~~**Port `DDSImageReader` off `System.Drawing`.**~~ **DONE.** Confirmed empirically:
   `System.Drawing` throws `TypeInitializationException` (GDI+ unavailable) on Linux, and
   the analyser flags it at compile time too. Resolved without ImageSharp or SkiaSharp —
   only 18 of the file's 942 lines touched `System.Drawing`; the rest is DDS/DXT decoding.

   - `src/IAGrim.Core/Imaging/PngEncoder.cs` — dependency-free PNG writer (8-bit RGBA via
     `ZLibStream` + CRC32). Avoids a native dependency and ImageSharp's split-licence
     question. Verified by round-tripping pixels, alpha included.
   - `src/IAGrim.Core/Imaging/DdsIconExtractor.cs` — fork of the one tainted file, plus the
     two DDS format structs it needs. DDS does not change, so rebase risk is near zero,
     unlike the ARC/ARZ parsing which stays referenced.
   - Entries already in PNG form are now copied verbatim instead of decoded and
     re-encoded — faster and lossless.
   - `IOHelper` resolved by aliasing the public `EvilsoftCommons` one, so **no upstream
     change was needed**.

   **Verified against the real game:** 2342 icons extracted from
   `resources/Items.arc` in 1.6 s, all sampled files structurally valid, and spot-checked
   visually.
3. Strip WinForms from non-UI code. Only ~10 files, coupling is almost entirely progress
   reporting:
   - `Parsers/GameDataParsing/Model/WinformsProgressBar.cs` → `IProgress<T>`
   - `Parsers/GameDataParsing/UI/ParsingDatabaseProgressView.*` → delete, report over WS
   - `Parsers/GameDataParsing/Service/{ParsingService,ArzParsingWrapper}.cs`
   - `Parsers/Arz/{ArzParser,LocalizationLoader}.cs`
   - `Services/HelpService.cs`, `Utilities/AutomaticUpdateChecker.cs`,
     `Utilities/Logging/TextBoxAppender.cs`
4. Write `IAGrim.Platform`:
   - `GlobalPaths` → XDG (`~/.local/share/iagd-linux`). Database and host settings stay
     native-side; only the IPC directories live in the prefix.
   - `GrimDawnDetector` → registry lookups become `libraryfolders.vdf` parsing plus a
     GOG/manual fallback. Delete `DependencyChecker` (WebView2 registry probe).
   - `RegistryHelper` → JSON config file.
   - **Save files: prefer Steam Cloud userdata, not the prefix.** Verified on this machine
     (§9): `transfer.gst` lives at `<steam>/userdata/<id>/219990/remote/save/` and the
     prefix's `Documents/My Games/Grim Dawn/Save` is empty. Upstream's
     `GlobalPaths.SavePath` hardcodes the Documents path and finds nothing. Working
     implementation already exists in `tools/probe/SteamPaths.cs:FindSavePath`.
5. Confirm database migrations run and an ARZ parse completes end to end.

**Exit criterion:** headless. Loot in-game → lands in SQLite → queryable → transfers back.

### Phase 3 — Host API  ← DONE

`src/IAGrim.Host` (`iagd-host`) exposes the core over loopback HTTP plus a WebSocket:

```
GET  /api/status                    game/hook state, counts, resolved paths
GET  /api/items?q=&skip=&take=      paged search across looted and template names
GET  /api/items/{id}                item with its stat lines
POST /api/items/{id}/transfer       send it back to the game
GET  /api/icons/{file}              icons extracted from the game archives
WS   /ws                            push: itemLooted, itemRemoved, status, message
```

Verified against the real database: search, detail with 27 stat lines, a 64x64 PNG icon
served, 404 for a missing item, and a path-traversal attempt on the icon route rejected.
The WebSocket delivers a status snapshot on connect.

**Built on `System.Net.HttpListener`, not Kestrel.** The surface is five endpoints and one
socket, and the base runtime handles both (WebSocket upgrade verified on Linux). That avoids
requiring the ASP.NET Core runtime as a separate install -- it was not present on this
machine -- and keeps the eventual Flatpak to the base runtime. If this ever needs routing,
auth or middleware, Kestrel is the right answer instead.

Loot import runs inside the host, so items appear as they are deposited rather than on
request. It uses the same `LootWatcher` as the CLI, so there is one implementation of the
"do not lose an item" rules.

Two deliberate boundaries:

- **Wire contracts are separate from storage records.** Database columns follow upstream's
  names so a future NHibernate migration stays a schema match; the UI should not inherit
  that constraint.
- **Bound to 127.0.0.1 only.** This serves the player's collection and can push items into
  their running game.

### Phase 4 — UI  ← DONE

`src/WebUI` is a Preact app served by the host at `http://127.0.0.1:5680`. Verified running
against the real collection: search, item grid with icons from the game's own archives, a
detail panel with all stat lines, and live status.

**Purpose-built rather than forked from upstream's WebUI.** That was a scope decision worth
recording: upstream's Preact app does **not** own its search box or filters — those live in
the WinForms sidebar, and the app only receives `SetItems` pushes. Adopting it wholesale
would have meant rebuilding ~19 WinForms dialogs and the whole filter panel first, which is
well outside the v1 core loop.

What was kept from upstream's design:

- **Grim Dawn colour codes are rendered, not stripped.** Tooltip text arrives as
  `^PMythical ^BPlagueborne Revolver`; rarity and damage type are conveyed by colour alone,
  so stripping them loses real information. `GrimText.tsx` maps them to a palette.
- **The display name prefers the raw first tooltip line** over the stripped name, for the
  same reason. `PlayerItem.Name` is stripped for search; the coloured original lives in
  `PlayerItemStat` (TextClass 6).

Transport: `fetch` for commands, WebSocket for pushes, both behind `src/api.ts` — the same
single-seam shape upstream used for its WebView2 bridge, so the UI never knows which host it
is talking to.

**Transfers are queue-and-observe, not blocking.** The endpoint originally held the HTTP
request open until the hook collected the file. That matched the CLI, but the hook only
deposits while the player has the transfer stash open, so a request could hang for minutes
and a page reload lost all knowledge of it.

```
POST   /api/items/{id}/transfer   -> 202 { transferId } (or 409 if the game/hook is absent)
GET    /api/transfers             -> what is still queued
DELETE /api/transfers/{id}        -> cancel, if the game has not taken it yet
WS                                -> transferQueued, transferCompleted
```

The safety rule is unchanged, and is now tested in a sandbox rather than argued for:

| Situation | Required behaviour | Verified |
|---|---|---|
| Queued, not yet collected | item stays in the collection | yes |
| Collected by the hook | item deleted, but only then | yes |
| Cancelled before collection | file removed, item kept | yes |
| Timed out | file **kept** so the game can still take it, item kept | yes |

The timeout case is the subtle one: giving up watching must not delete the queued file, or
the player ends up with neither the item nor a pending transfer.

Making that testable required injecting the database path into `TransferTracker` rather than
reading it from `LinuxPaths` -- otherwise any test of the deletion rule would have mutated
the real collection, which is precisely the rule most worth testing.

**The desktop window is `src/IAGrim.App`** — Photino (a WebKitGTK view) with the host running
in-process. One process, one lifecycle: closing the window stops the host, and there is no
port negotiation between a parent and a child. `HostServer` was extracted from the headless
entry point so both drive the same code; `iagd-host` still runs standalone for anyone who
wants a browser instead, and gets an identical UI rather than a degraded one.

Two things had to be worked around, both documented at the call site:

- **Photino's GTK3 layer dies on a Wayland session** with `Gdk-Message: Error 71 (Protocol
  error)`, taking the host down with it. Under XWayland the same build is fine, so the app
  selects `GDK_BACKEND=x11` when it sees a Wayland session with an X display available.
  Escapable: set `GDK_BACKEND` yourself and it defers. The real fix is Photino on GTK4.
- **`Environment.SetEnvironmentVariable` does not work for this.** On Unix it updates a managed
  dictionary and never calls `setenv`, so GTK — which reads `getenv` — sees nothing. It looks
  like it works. The app P/Invokes `setenv` instead.

Launching twice does not fail: the second instance finds the port taken and opens a window onto
the running host, which is what clicking a desktop icon twice should do.

### Phase 5 — Packaging  ← DONE

`make package` produces a relocatable AppDir, a tarball, and an AppImage when `appimagetool` is
installed. Self-contained, so no .NET install is required; the hook DLL, the injector and the
attach script travel with it, located through `IAGD_HOOK_DLL` / `IAGD_INJECTOR` so the same
scripts work from a checkout or from a package.

Four entry points: `iagd` (desktop app), `iagd-host` (headless), `iagd-cli`, `iagd-attach`.

**Not a Flatpak, for reasons that are properties of the sandbox rather than of packaging.** The
app finds the running game by scanning `/proc`, and Flatpak gives an app its own PID namespace
with a fresh procfs — measured here, a sandbox sees 4 processes where the host sees 626. It also
attaches by executing Proton from the host, which enters the game's own pressure-vessel
container. Both would need `--filesystem=host` plus `flatpak-spawn --host` for every subprocess,
at which point the sandbox is decorative.

Three self-contained publishes share one runtime, so identical files are hard-linked: 269 MB of
output becomes 92 MB extracted and a 39 MB tarball, and `tar` stores a hard link once.

WebKitGTK is taken from the host — it is bound to the system graphics stack and bundling it
reliably is not realistic. `AppRun` checks for it and names the package to install, rather than
failing inside the webview.

**The icon.** Three mechanisms, and the window is the least important of them:

- `SetIconFile` gives the window an icon hint. Photino exposes it; nothing called it.
- `iagd install-desktop` writes a `.desktop` entry and the icon into `~/.local/share`, at five
  sizes, and rebuilds the caches — including `kbuildsycoca6`, which is the one that matters on
  KDE: Plasma reads entries from its own cache, so without it a new entry stays invisible.
- **`g_set_prgname("iagd")`, which is what actually fixed it.** This session runs on *Wayland*,
  not XWayland — the session exports `GDK_BACKEND=wayland`, and the code only overrode that when
  it was unset. Wayland has no per-window icon at all: a compositor identifies a window by its
  `app_id` and looks up the matching desktop entry. GTK takes that app_id from GLib's program
  name, which for a .NET application is whatever the host process is called.

Verified through KWin's own scripting interface rather than by inference — before, the window
resolved to `desktopFileName=` (empty); after:

```
resourceClass=iagd  resourceName=iagd  desktopFileName=iagd
```

which is the whole chain: app_id → `iagd.desktop` → `Icon=iagd` → `hicolor/*/apps/iagd.png`.

A related correction: the earlier claim that Photino's GTK3 cannot survive a Wayland session was
wrong. The `Error 71` crash was WebKitGTK's DMA-BUF renderer, disabled separately — with that
off the window comes up natively on Wayland, which is what it has been doing since.

Both the app and the CLI carry the icon, and the packaged CLI is told which executable the entry
should launch: both publish an executable named `iagd`, so a "sibling named iagd" lookup finds
the CLI itself and produces an entry that opens a terminal tool when clicked. It now refuses
rather than guessing, and `--exec` overrides.

**The tray icon** is done, in its minimal form: an icon, a title, and left-click to toggle the
window. `src/IAGrim.App/TrayIcon.cs` speaks StatusNotifierItem over D-Bus directly, because
Photino has no tray API and there is no libappindicator here to P/Invoke.

No context menu. Menus are a second protocol (`com.canonical.dbusmenu`) with its own layout tree
and event plumbing, and Show/Quit entries do not justify it — **closing the window still quits
the application**, which is the behaviour worth keeping. A tray icon is not a good enough reason
to introduce a background process nobody knows how to stop.

Optional by construction: no session bus, no watcher, a refused name or a rejected reply all
return null and the app runs exactly as it would without a tray. `IAGD_DEBUG_TRAY=1` prints why,
since a missing icon otherwise has several indistinguishable causes.

Single-instance is handled by port ownership.

---

## 4b. Decided: no NHibernate; port its SQL instead  *(re-confirmed 2026-08-11)*

Upstream's DAO layer uses NHibernate, and it **does** work on Linux -- `BuildSessionFactory`
and `OpenSession` both succeed once `Microsoft.Data.Sqlite` is referenced alongside the
driver. So feasibility was not the deciding factor; what it buys was:

| | Count |
|---|---|
| Hand-written SQL (`CreateSQLQuery`) across the ten DAOs | 81 |
| ORM queries (`Query`/`Criteria`/`QueryOver`) | 23 |

NHibernate is largely a connection manager and result mapper for SQL upstream wrote by hand.
The valuable asset is that SQL, and it ports directly because this schema deliberately kept
upstream's column names. Adopting the ORM would mean 15 `hbm.xml` mappings pulled out of the
WinForms project, ~5,900 lines of DAO code, upstream's `ThreadExecuter` session model, and an
extra dependency in the Flatpak.

Revisit if cloud backup or buddy sharing is wanted -- those DAOs carry sync-state machinery
more entangled with the session model. Better decided before building sync than after.

**Upstream drift is now tracked rather than remembered.** `scripts/check-upstream.sh` hashes
every upstream file this port copies from and reports what changed since last reviewed.
Re-porting needs judgement; noticing should not. Verified by simulating an upstream edit to
the loot CSV parser and confirming it was reported with the right context. See
[PORTING.md](PORTING.md) for the manifest, which upstream changes actually matter, and what
the search port covers.

**Search parity reached.** Every filter upstream's `ItemSearchRequest` declares is implemented,
and the database is upstream's own schema — a `userdata.db` from a Windows install opens here
with the collection intact. `scripts/verify-schema.sh` proves it by building a fixture from
upstream's DDL alone and reading it.

## 5. Explicitly out of scope for v1

Cloud backup, buddy item sharing, character file parsing, mod database configuration,
localisation, transfer-stash file rewriting, and the automatic updater.

Each is filed in [BACKLOG.md](BACKLOG.md) with why it is deferred, what it would buy, and what
it would cost — because "not ported" and "not noticed" look identical from outside, and only
one of them is a decision.

---

## 6. Known risks

| Risk | Mitigation |
|---|---|
| **Wine file-based IPC is new (2026-04) and unproven** | Phase 0 probes it before anything is built on it; Phase 1 makes it fixable rather than a blocker |
| Current official release may itself predate the IPC | Verify release date before the Phase 0 probe; if too old, Phase 1 moves ahead of Phase 0 |
| MinGW ABI differences in hooked `__thiscall` calls | x64 collapses the conventions; validate with the Phase 0 probe as the A/B baseline |
| Upstream `HookDll` divergence after forking | Accept rebase cost; churn is low and concentrated in `Exports.h` |
| `System.Drawing` port may drift from upstream `Parser` | Prefer an upstream PR over a local fork |
| Photino.NET is small; WebKitGTK varies by distro | Core stays headless through Phase 3, so the shell is swappable; system browser as fallback |
| Bridge-directory races | Upstream writes `.tmp` then renames for atomicity; mirror when writing |

---

## 7. Reference: what the hook DLL reads from `settings.json`

The host must write a minimal `settings.json` into the bridge directory. Only these keys
are consumed by the DLL (`HookDll/Hook/SettingsReader.cpp`):

| Key | Purpose |
|---|---|
| `persistent.isRunningInWine` | **Must be `true`** — this is what switches the DLL into file-based IPC |
| `local.stashToLootFrom` | stash tab index, 0 = last |
| `local.stashToDepositTo` | stash tab index, 0 = last |

Note that upstream stores `local.grimDawnLocation` as Windows-style `Z:\...` paths. Decide
whether to keep that convention or translate at the boundary.

---

## 8. Environment notes (this machine, checked 2026-08-10)

| Tool | State |
|---|---|
| .NET SDK 10.0.108 | present |
| Node v24.13.0 | present |
| nlohmann-json 3.12.0 | present |
| 7z | present |
| mingw-w64-gcc 16.1.0 (x86_64 + i686) | present |
| GNU Make 4.4.1 | present |
| innoextract 1.9 | present |
| MinHook | not packaged for Arch; vendor from source (MIT, ~5 files) — Phase 1 |
| shellcheck | absent; optional, for `scripts/` |

---

## 9. Verified layout on this machine (2026-08-10)

Resolved by `tools/probe`, not assumed:

| | |
|---|---|
| Steam root | `~/.local/share/Steam` |
| Game dir | `<steam>/steamapps/common/Grim Dawn` |
| Target exe | `<game>/x64/Grim Dawn.exe` — PE32+. The root `Grim Dawn.exe` is **PE32 (32-bit)** and unsupported by IA |
| Prefix | `<steam>/steamapps/compatdata/219990/pfx` |
| Proton | GE-Proton11-1, read from `compatdata/219990/config_info` |
| **Save path** | `<steam>/userdata/27957186/219990/remote/save/` — Steam Cloud. The prefix Documents Save dir is **empty** |
| Bridge dir | `<pfx>/drive_c/users/steamuser/AppData/Local/EvilSoft/IAGD` |
| Existing item DB | `<bridge>/data/userdata.db`, **210 MB — preserve** |
| `linuxhack/` | **does not exist** — independently confirms the stale DLL never ran Wine mode |

One further finding worth carrying into the port: **in Wine mode the DLL does not look for
our window.** The worker thread skips `FindWindow("GDIAWindowClass")` and pins
`InventorySack_AddItem` active (`dllmain.cpp:187`). A native host therefore never needs to
fake a Win32 window.
