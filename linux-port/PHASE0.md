# Phase 0 runbook — does the Wine IPC actually work?

One question: **does the hook DLL's file-based IPC work under Proton?** Everything in
[PLAN.md](PLAN.md) rests on it, and it has never been verified on this machine — the IAGD
build previously installed in the prefix predates the mechanism by five months (§2 of the
plan).

Everything below is built and verified. Only the four steps in §3 need you.

Two launch-mode failures have already been diagnosed and designed out — see §7 and §8.
The mechanism is now **attach to a Steam-launched game**, not launch-and-inject.

---

## 1. What is already done

| | Status |
|---|---|
| mingw-w64, make, innoextract | verified present |
| `proton-injector` built | `bin/injector64.exe`, PE32+ x86-64, clean under `-Wall` |
| Current hook DLL obtained | release `1.5.9715.11589`, Wine IPC markers verified present — see [vendor/hook-dll/PROVENANCE.md](vendor/hook-dll/PROVENANCE.md) (the binaries themselves are not kept in this repository) |
| Bridge watcher | `tools/probe`, builds clean, discovery verified against the real setup |
| Load diagnostic | `tools/loadtest`, in-prefix `LoadLibrary` error reporting |
| **Attach mode** | `--attach-name` implemented in proton-injector, rebuilt clean |
| **Attach wrapper** | `scripts/attach-gd.sh`, dry-run verified |
| ~~Launch wrapper~~ | `scripts/inject-gd.sh` — **deprecated**, cannot work (§7, §8) |

The staged DLL was built from source **byte-identical** to the vendored upstream HEAD, so
it is a valid A/B baseline for the Phase 1 MinGW port.

## 2. Your prefix is NOT being recreated

The plan originally said "fresh prefix". That was wrong and is retracted:

- `<bridge>/data/userdata.db` is **210 MB** — your entire IAGD item collection.
- `<bridge>/storage` is 39 MB of extracted item icons.
- Grim Dawn's saves and `transfer.gst` are **not in the prefix at all**; they live in
  Steam Cloud userdata (§5). Recreating the prefix would not have protected them and
  would have destroyed the item database.

Nothing is deleted. `--setup` backs up `settings.json` before touching it, the probe
never deletes loot CSVs, and consumed `.msg` files are moved to `probe-archive/` rather
than discarded.

The stale IAGD install in `drive_c/Program Files/IAGD` is left alone — it is inert
because `IAGrim.exe` is never launched.

---

## 3. Running the probe

### Step 1 — enable the DLL's Wine mode

```bash
cd linux-port
dotnet run --project tools/probe -- --setup
```

Sets `persistent.isRunningInWine = true` in the bridge `settings.json`, preserving your
existing keys and writing a `.probe-backup` first. **Without this the DLL uses
`WM_COPYDATA` and the probe sees nothing** — it is the single most likely cause of a
silent failure.

### Step 2 — launch Grim Dawn from Steam, normally

Just press Play in Steam. Do **not** use `inject-gd.sh`; launching outside Steam breaks
the game's Steam API authentication (§8), and injecting at launch crashes the DLL (§7).

Load a character. The DLL refuses to attach in the main menu or character select.

### Step 3 — attach the hook

```bash
cd linux-port
make -C native/hook          # build the ported DLL (once)
./scripts/attach-gd.sh
```

Waits for a running `Grim Dawn.exe`, injects, and **retries every 5 s** if the DLL rejects
the attach — which it legitimately does during loading screens. `--dry-run` to preview;
`--method apc` and friends pass through. Logs to `proton-injector/injector.log`.

### Step 4 — watch the bridge

In a third terminal:

```bash
cd linux-port
dotnet run --project tools/probe
```

Then **open your stash** in game.

---

## 4. What success looks like

```
[14:02:11.204] PID      wine pid 1234  — hook DLL attached and running
[14:02:11.455] MSG REPORT_WORKER_THREAD_LAUNCHED    len=0      (no payload)
[14:02:12.100] MSG SUCCESS_HOOKING_GENERIC          len=4      int32=25
[14:02:31.882] MSG GameInfo_IsHardcore              len=1      byte=0  (bool=False)
[14:02:44.019] LOOT     a1b2….csv  1 row(s)
```

**Exit criterion:** stash events and looted items appear in a native Linux process.

`Ctrl+C` prints a summary and writes `probe-baseline.jsonl`. **Keep that file** — it is
the A/B reference for validating the Phase 1 MinGW build. An ABI mistake in the
`__thiscall` hooks would surface as subtly wrong payloads rather than a clean crash, and
the recorded baseline is the only way to catch that.

## 5. If nothing arrives

The probe's summary prints this checklist, in diagnostic order:

1. **No `linuxhack/` directory** → the DLL never entered Wine mode. `--setup` was not run,
   or the DLL read a different `settings.json` than expected.
2. **No `.PID` file** → the DLL never attached. Injection failed; check
   `proton-injector/injector.log` and try `--method apc`.
3. **An `.ABORTED` file** → injection worked, but the game was not ready. Expected in
   menus. Load a character and retry.
4. **Otherwise** → read the DLL's own log at `<bridge>/iagd_hook.log`.

A failure here is a finding, not a dead end: it is precisely the argument for owning the
DLL in Phase 1. It may, however, mean Phase 1 grows from "port the build" to "port the
build and fix the protocol".

---

## 6. Findings so far (already worth folding into the port)

**Saves are not in the prefix.** With Steam Cloud enabled, `transfer.gst` lives at
`<steam>/userdata/<id>/219990/remote/save/`. The prefix's
`Documents/My Games/Grim Dawn/Save` is **empty**. Upstream's `GlobalPaths.SavePath`
hardcodes the Documents path and would find nothing here. `IAGrim.Platform` must prefer
the userdata path — see `SteamPaths.FindSavePath`. Good news: it is natively readable, so
stash-file manipulation involves no Wine at all.

**Two Grim Dawn executables.** The root `Grim Dawn.exe` is PE32 (32-bit); the real target
is `x64/Grim Dawn.exe` (PE32+). IA does not support 32-bit
(`INJECTION_ERROR_32BIT` in `DllInjector/InjectionHelper.cs`), so injecting into the
wrong one fails in a confusing way.

**The DLL does not need our window in Wine mode.** In Wine mode the worker thread skips
`FindWindow("GDIAWindowClass")` entirely and pins `InventorySack_AddItem` active
(`dllmain.cpp:187`). A native host therefore does not need to fake a Win32 window — one
less thing to emulate.

---

## 7. Diagnosed: launch-time injection cannot work

First run produced nothing. Diagnosed to root cause, and it changes the plan.

### What happened

`proton-injector/injector.log` showed injection succeeding mechanically and then failing
at the last step:

```
[DEBUG] kernel32.dll loaded after 10ms
[DEBUG] Resolved LoadLibraryA via PE exports: 0x00006FFFFFE7F794
[ERROR] LoadLibraryA returned NULL
```

No `linuxhack/`, no `.PID`, no hook log — the DLL never initialised.

### Ruling out the obvious

The DLL imports `MSVCP140.dll`, `VCRUNTIME140.dll` and seven `api-ms-win-crt-*` api-set
DLLs, none of which exist as files in the prefix or the Proton dist. Tempting, but wrong:
**Grim Dawn itself imports ten of the same api-sets and runs fine**, so Wine resolves them
internally. Not a dependency problem.

### The actual cause

`tools/loadtest` loads the DLL in-prefix and reports the real error code:

| Load mode | Result |
|---|---|
| `LOAD_LIBRARY_AS_DATAFILE` | OK |
| `DONT_RESOLVE_DLL_REFERENCES` | OK |
| `LoadLibraryW` (full — runs `DllMain`) | **FAILED, 998 `ERROR_NOACCESS`** |

The PE maps cleanly; it dies with an access violation during initialisation.

`HookDll/Hook/GrimTypes.cpp:292-295` declares four namespace-scope globals:

```cpp
IsGameLoadingPtr IsGameLoading = IsGameLoadingPtr(
    GetProcAddressOrLogToFile(L"game.dll", "?IsGameLoading@GameEngine@GAME@@QEBA_NXZ"));
```

Their initialisers run during static init, **before `DllMain`**. With `game.dll` not yet
loaded, `GetModuleHandle` returns NULL and `GetProcAddressOrLogToFile` calls `LogToFile`,
which writes to `g_log` — a global in *another* translation unit (`dllmain.cpp:17`).
Static initialisation order across translation units is unspecified, so `g_log` may still
be unconstructed: undefined behaviour, observed as the AV. Past that,
`ProcessAttach` calls `fnGetGameEngine()`, which dereferences the result of
`GetProcAddress` without a null check (`GrimTypes.cpp:145`).

**The DLL has an unstated precondition: `game.dll` must already be loaded.** The injector
attaches ~10 ms in; `game.dll` appears tens of seconds later.

### Consequences for the plan

1. **`SLEEP` is mandatory for the probe** (§3 step 2). `--sleep` is applied after process
   creation (`proton-injector/src/main.c:630`).
2. **`--attach-name` is promoted out of Phase 5.** It was filed as a UX nicety —
   "start IA any time". It is not: launch-time injection is *fundamentally incompatible*
   with this DLL, so attach-to-running-process is the only robust mechanism. The
   launch-option wrapper, chosen as the Phase 1 shortcut, does not actually work.
3. **This is a concrete argument for owning the DLL.** A null check in `fnGetGameEngine`
   and lazy initialisation of those four globals would turn a hard crash into the clean
   "not ready yet" abort the code already implements everywhere else.

### Mistimed injection is still informative

If injection lands while the game is at a loading screen or character select, `game.dll`
*is* loaded, so static init succeeds, `ProcessAttach` runs, sees `IsGameLoading`, and
takes the graceful abort path — creating `linuxhack/` and writing an `.ABORTED` marker.

That outcome would still prove the file-based bridge works, which is Phase 0's real
question. Both a clean success and a clean abort are passes.

---

## 8. Diagnosed: launching outside Steam breaks Steam auth

Second run, with `SLEEP` set, produced a game-side dialog:

```
Steamworks Error!
No SteamUtils011
```

Grim Dawn's `steam_api64.dll` could not obtain an interface from `steamclient64.dll`.
Checked and ruled out: the Steam client **is** running, `ubuntu12_32/steam-launch-wrapper`
and `reaper` are both present and executable, `steam_api64.dll` and `steamclient64.dll`
are in place, and `SteamAppId`/`SteamGameId` are exported by `inject.sh`. The plumbing
proton-injector relies on is intact; the game still cannot authenticate when it is not
launched through Steam's own path.

This is the **second independent reason** launch-time injection is the wrong mechanism,
and it is not worth fixing: both problems disappear when Steam launches the game and we
attach afterwards. `scripts/inject-gd.sh` is deprecated.

### What was built instead

`--attach-name` now exists in proton-injector (`src/main.c`), promoted from Phase 5:

| Flag | Meaning |
|---|---|
| `--attach-name <exe>` | Wait for a running process with this image name, then inject. No `target.exe` argument |
| `--attach-timeout <ms>` | How long to wait for it to appear (default 0 = forever) |
| `--attach-retry <ms>` | Retry interval when injection fails (default 5000, 0 = disable) |

Notes on the implementation:

- **Newest match wins.** If the game was restarted, a stale entry may still be winding
  down, and injecting into a dying process silently does nothing. Candidates are ranked by
  process start time.
- **Retry is the point, not a nicety.** A rejected attach is indistinguishable from a hard
  failure from outside the process, and the DLL legitimately rejects attaches while the
  game is loading. The loop also re-resolves the PID if the process exits.
- The logic **must** live in the injector: Wine PIDs are not Linux PIDs, so a PID observed
  from `/proc` is meaningless to `OpenProcess` inside the prefix.

### Caveat: the container namespace

Steam runs Proton games inside a pressure-vessel container (`SteamLinuxRuntime_sniper`
and friends are installed here). `attach-gd.sh` runs `proton run` from outside that
container, so it may get its own wineserver and fail to enumerate the game's processes.

Symptom: the injector logs `Waiting for a running process named 'Grim Dawn.exe'...`
forever while the game is plainly running.

If that happens, the fallback is to start the injector *inside* the game's container, via
Steam launch options on Grim Dawn:

```
sh -c '/path/to/linux-port/scripts/attach-gd.sh & exec "$@"' -- %command%
```

Steam still launches the game itself, so Steam auth is unaffected, and the helper attaches
from within the same namespace once the game is up. This is the shape the Phase 5
packaging story will take anyway.

---

## 9. Verdict: the prebuilt hook DLL cannot initialise under Proton

Attach mode works. The DLL does not. Everything around it has been eliminated.

### Proven working

The canary (`tools/canary`, a ~90-line mingw DLL) was injected into the **live game**:

```
[INFO ] Found 'Grim Dawn.exe' (PID: 320)
[DEBUG] DLL loaded via CreateRemoteThread + LoadLibraryA
[INFO ] DLL injected successfully: C:\iagd\canary.dll
[INFO ] Injection complete after 1 attempt(s)
```

All three stage markers were written from inside the game process. So the attach logic,
the remote `LoadLibraryA`, the `C:` staging path, and the container boundary are all fine.

### Eliminated, with evidence

| Hypothesis | Verdict |
|---|---|
| Missing MSVC runtime / api-set DLLs | **No.** Grim Dawn imports ten of the same api-sets and runs; and each of the 11 dependencies loads individually by name inside the prefix |
| Container mount-namespace isolation | **No.** The game's namespace can see the DLL directory (`/proc/<pid>/root`) |
| Wrong hook variant for this game build | **No.** 26 of 27 symbols the source resolves are exported by the installed `Game.dll`/`Engine.dll`; the only gap is one optional hook |
| `SHGetKnownFolderPath` unsafe in static init | **No.** The canary calls it from a static initialiser under the loader lock: `hr=0x0`, returns `C:\users\steamuser\AppData\Roaming` |
| DLL path not reachable from the game | **No.** Fails identically from a `C:\` path inside the prefix |
| Our injector | **No.** Same injector, same path, same target — canary succeeds, IA hook fails |

### The reproducible failure

`tools/loadtest`, which needs no running game:

```
canary.dll                  LoadLibraryW (full): OK
ItemAssistantHook_x64.dll   LoadLibraryW (full): FAILED (err 998 ERROR_NOACCESS)
```

The MSVC-built hook DLL takes an access violation during its own initialisation. Remaining
candidates are all internal to that binary and unreachable without source control of it:
MSVC C++ runtime startup (wide iostreams, locale/codecvt), Boost static init, Detours, and
the two landmines already identified in `GetIagdFolder` (freeing an uninitialised pointer
on the failure path, and logging through `g_log` while `g_log` is still being constructed).

### Consequence: Phase 1b is now blocking, not optional

Owning the DLL was chosen for the ability to fix bugs in an unproven IPC path. That
ability is now the *only* way forward: the shipped binary cannot be made to load here, and
no amount of host-side work changes that.

Two things make this a good position rather than a bad one:

- **The target is demonstrated achievable.** A mingw-built DLL loads and runs inside the
  live game. That is exactly what the port produces.
- **The iteration loop is fast and needs no game.** `tools/loadtest` reproduces the
  failure in seconds, and `tools/canary` is a known-good control.

### Phase 0's original question is still open

"Does the file-based IPC work under Proton?" remains **unanswered** — the DLL never reaches
the code that would exercise it. It cannot be answered until the port produces a DLL that
initialises. The question has not been shown to be a problem; it has been deferred.

### Side note on the original premise

There is no `iagd_hook.log` anywhere in this prefix, and none was produced by the previous
2025-11-25 install either. The hook DLL appears never to have loaded on this machine at
all — which is a concrete explanation for "IAGD has major issues under Linux": instaloot,
stash-state detection, and real item stats all depend on it.
