# MinGW port of the IA hook DLL

Builds `ItemAssistantHook_x64.dll` from Linux with mingw-w64, replacing the prebuilt MSVC
binary that cannot initialise under Proton (see [../../PHASE0.md §9](../../PHASE0.md)).

```bash
make          # -> bin/ItemAssistantHook_x64.dll  (1.6 MB)
make deps     # assert no MSVCP140 / VCRUNTIME140 imports
```

**Status: instaloot verified end to end.** Injected into a live Proton Grim Dawn it hooks
successfully, and moving an item into the stash produces a correct loot CSV:

```
;0;records/items/upgraded/gearweapons/guns1h/c030_gun1h.dbr;;;1041370630;0;...
6;^PMythical ^BPlagueborne Revolver
66;Epic One-Handed Ranged
```

Correct base record, seed, item name (Grim Dawn colour codes intact) and all 27 stat lines
— produced by a Windows DLL cross-compiled on Linux and read by a native .NET process.

Build with `TRACE=1` only for diagnosis: it writes several flushed log lines per frame while
the stash is open, which is far too slow for play.

---

## Why it failed before, and what fixed it

The MSVC binary died with `ERROR_NOACCESS` during initialisation. Two independent defects,
both in code that runs during **static initialisation — before `DllMain`**, and therefore
before any log file exists, which is why the crash was completely silent:

### 1. Export binding at namespace scope

`GrimTypes.cpp` bound four function pointers, and `GrimTypes.h` a further eleven, at
namespace scope:

```cpp
static auto fnCreateItem = pCreateItem(GetProcAddressOrLogToFile(L"game.dll", "..."));
```

`GetProcAddressOrLogToFile` logs — through `g_log`, a global in *another* translation unit.
Static initialisation order across translation units is unspecified, so this can write to a
`HookLog` that has not been constructed. It also runs before `game.dll` is necessarily
loaded, which is exactly when the logging path is taken.

Fixed two ways, both preserving call-site syntax:

- The four in `GrimTypes.cpp` moved into `ResolveGameExports()`, called from
  `ProcessAttach` once the game modules are known present.
- The eleven in `GrimTypes.h` became `LazyExport<Fn>`, which resolves on first use. Its
  implicit conversion to the function-pointer type means `fnX(args)` and `if (fnX)` behave
  exactly as before.

### 2. Unchecked dereference of `GetProcAddress`

```cpp
auto gameEngine = (GAME::GameEngine*)*(DWORD_PTR*)GetProcAddressOrLogToFile(L"game.dll", "?gGameEngine...");
```

Dereferenced before the null check. With `game.dll` absent this dereferences null, taking
the process down instead of reporting "not ready yet" as every other path does. Same bug in
`fnGetEngine`. Both now check first.

### 3. Data race introduced by the lazy binding *(regression, found and fixed)*

Making the exports lazy fixed the static-init crash but introduced a new one. The first
version published the flag before the value:

```cpp
if (!m_resolved) { m_resolved = true; m_fn = GetProcAddress(...); }
return m_fn;
```

Upstream resolved everything during static initialisation, before any thread existed. Lazy
resolution moves it to first use, and the first use of several of these is the moment the
stash opens — reached concurrently by the game's render thread, the DLL's worker thread and
the seed-info thread. A second thread could see `m_resolved == true` while `m_fn` was still
null and call through a null pointer, crashing the game.

Upstream's own `IsFirstLookupOfExport` carries the comment *"Called from the game threads as
well as IA's polling threads, so the set needs a lock"* — confirming these paths are known
to be multithreaded.

`LazyExport` now holds a single `std::atomic<void*>` initialised to a `-1` sentinel
(distinct from `nullptr`, which legitimately means "export missing"). A thread either sees
the sentinel and performs the lookup, or sees a fully-written pointer; there is no ordering
window. Two threads racing may both resolve, which is harmless because `GetProcAddress` is
idempotent.

**Symptom to recognise:** the hook log ends immediately after a
`Successfully found DLL export: ...` line, with no error — the thread that logged the
success survived, the one that raced it did not.

### 4. `GetIagdFolder` failure path (latent)

Two defects on the error branch: `CoTaskMemFree` on an **uninitialised** pointer, and a
`LogToFile` call issued from inside `HookLog`'s own constructor — using a `HookLog` that is
still being built. Neither is reachable while `SHGetKnownFolderPath` succeeds, which it does
under Proton (verified with `tools/canary`), but both were live landmines.

The observable result of the fixes: `998 ERROR_NOACCESS` (crash) became
`1114 ERROR_DLL_INIT_FAILED` (clean, deliberate abort) when the game is absent, and full
initialisation when it is present.

---

## Dependency replacements

| Upstream | Replacement | Approach |
|---|---|---|
| Detours (5 functions) | MinHook 1.3.3, vendored | `compat/detours.h` + `detours_minhook.cpp` reimplement the Detours API, so **no call site changed** |
| `boost::property_tree` | nlohmann/json, vendored | `compat/boost/property_tree/` — a minimal ptree with the ~8 operations actually used |
| `boost::filesystem` | `std::filesystem` | header shim |
| `boost::thread`/`mutex` | `std::thread`/`std::mutex` | header shim |
| `boost::shared_array` | `std::shared_ptr<T[]>` | header shim |
| `boost::algorithm`, `lexical_cast`, `optional`, `range` | std equivalents | header shims |
| `<atlbase.h>` | removed | no ATL type was ever used |
| `std::wstring_convert` / `<codecvt>` | `Utf8Compat::Converter` | removed from GCC 16; the shim keeps `to_bytes`/`from_bytes` so call sites are unchanged |

The shim-first strategy is deliberate: it keeps the diff against upstream `HookDll` small,
which is what makes future rebases tractable — the plan already flags that as the standing
cost of forking.

### Deliberate compatibility choice

property_tree stores every scalar as a string and writes them all quoted, so it emits
`{"seed": "123"}` rather than `{"seed": 123}`. The shim reproduces that quirk exactly,
because it is what the current IAGD release emits and what its C# side already parses.

---

## Other portability fixes

| Issue | Fix |
|---|---|
| `#include "StdAfx.h"` vs `stdafx.h`, `Windows.h` vs `windows.h` | `compat/StdAfx.h`, `compat/Windows.h` case aliases — Linux is case-sensitive where MSVC was not |
| `ofstream::open(std::wstring)` | libstdc++ needs `std::filesystem::path` |
| `min`/`max` macros from `windows.h` | Two sites rewritten to `std::min`. Both were `max(0, min(...))` where the operands convert to `size_t`, making the `max(0, ...)` unreachable — the replacement is the same clamp, stated explicitly |
| `std::atomic` used without `<atomic>` | added include |

## Build notes

Static linking (`-static -static-libgcc -static-libstdc++`) is the point, not an
optimisation: it removes the `MSVCP140`/`VCRUNTIME140` imports entirely. The remaining
`api-ms-win-crt-*` imports are this toolchain's UCRT and are **fine** — Wine resolves those
api-sets internally, Grim Dawn itself imports ten of them, and the known-good canary DLL
imports them too. `make deps` therefore checks only for the C++ runtime.

## Not yet verified

The hooks install and the DLL runs, but the **behaviour** of the hooked paths has not been
exercised: instaloot, stash open/close detection, and item replica stats all need real
in-game activity. The `__thiscall`/`__fastcall` calling conventions collapse to the same
ABI on x64, so they should be correct, but an ABI mistake would show up as subtly wrong
payloads rather than a crash. Compare against `probe-baseline.jsonl`.

---

## The big one: MSVC vs libstdc++ `std::string` across the boundary

Grim Dawn is MSVC-built; this DLL is mingw/libstdc++. Their `std::basic_string` are both
32 bytes but lay out fields completely differently:

| offset | MSVC | libstdc++ |
|---|---|---|
| 0 | `union { E buf[N]; E* ptr; }` | `E* _M_p` |
| 8 | *(still inside that union)* | `size_t _M_string_length` |
| 16 | `size_t _Mysize` | local buffer / capacity |
| 24 | `size_t _Myres` | |

So when the game writes a short (SSO) string, the **characters land at offsets 0-15** —
exactly where libstdc++ keeps its pointer and length. Reading it back yields a garbage
pointer and a garbage length. Upstream never sees this: MSVC talks to MSVC.

**Symptom:** the game *freezes* rather than crashing. `GetModName` does
`std::remove(modName.begin(), modName.end(), ...)` over that garbage range, which walks
multiple gigabytes.

`compat/msvc_string.h` reproduces MSVC's layout (`static_assert`ed at 32 bytes, with 16-char
narrow and 8-char wide SSO buffers) and is used at every crossing:

| Crossing | Direction | Notes |
|---|---|---|
| `GetModName` | game writes | via `MsvcInterop::Receiver`, then converted |
| `ShowCinematicText` | game reads | `borrow()`. Previously "worked" only because long text is heap-backed, so offset 0 held a real pointer |
| `ItemReplicaInfo` (10 fields) | both | members are `MsvcInterop::String`; still 32 bytes, so every documented offset is unchanged (`var1` remains `0x160`) |
| `GameTextLine::text` | game writes | filled by `Item::GetUIDisplayText`. 27 garbage strings per item, each walked by `to_bytes()` — another hang |

Every game-facing struct has since been swept; no native `std::string`/`std::wstring`
remains on the boundary. `mem::vector` needs no equivalent treatment: it shares the
three-pointer `{begin, end, capacity}` shape, confirmed live by `sacks->size() = 10`.

### Two ways this bit beyond the layout itself

1. **`MsvcInterop::String` needs a default constructor.** `std::string` self-initialises;
   a plain struct does not, so `GAME::ItemReplicaInfo replica;` briefly meant ten fields of
   stack garbage. The game's `operator=` then calls MSVC's `_Tidy_deallocate()`, which frees
   `_Bx._Ptr` whenever `_Myres >= _BUF_SIZE` — a garbage capacity means deallocating a
   garbage pointer.
2. **Layout is asserted, not assumed**, since a drift here corrupts the game's heap in
   silence:

```cpp
static_assert(offsetof(ItemReplicaInfo, var1) == 0x160, "ItemReplicaInfo layout drifted");
static_assert(sizeof(MsvcInterop::WString) == sizeof(std::wstring),
              "GameTextLine stride would change; the game's vector would be misread");
```

The function-pointer typedefs now take `void*` rather than `std::wstring*`, so a native
string cannot be passed by accident.

### Ownership rule

Which allocator owns a string depends on the direction, and getting it wrong corrupts a
heap:

- **Game writes** (`GetItemReplicaInfo`, `GetModName`) — the buffer belongs to the game's
  allocator. Never free it.
- **We write** (`Deserialize` → `CreateItem`) — `assign_owned()` copies into the inline
  buffer, or allocates one we own. `free_owned()` releases it. The game only ever receives
  `ItemReplicaInfo` by const reference, so it never frees ours.

`TrimOwned()` in `OnDemandSeedInfo.cpp` exists for this reason: `boost::algorithm::trim`
mutated the string in place, which is only valid on strings we built.

---

## Hazard: never inject two copies

`LoadLibrary` dedupes by **module path**, not by content. Injecting the same hook under a
second filename therefore loads a second, fully independent copy into the game:

- two sets of MinHook patches over the same game functions, the later one patching over the
  earlier one's trampolines
- two worker threads and two seed-info threads
- both writing the same log, `.PID` marker and `.msg` files

This crashes Grim Dawn. It happened here during development, by staging the same build
first as `ItemAssistantHook_ported.dll` and then as `ItemAssistantHook_x64.dll` and
injecting both into the same process.

Two independent guards now exist:

1. **In the DLL** — `ClaimSingleInstance()` in `dllmain.cpp` takes a named mutex
   (`Local\ItemAssistantHook_<pid>`) at the top of `ProcessAttach` and returns `FALSE` if it
   already exists, so a second copy refuses to initialise regardless of filename. Released
   in `ProcessDetach`.
2. **In the tooling** — `scripts/attach-gd.sh` refuses to inject when a `.PID` marker is
   already present, and always stages under the single stable filename
   `ItemAssistantHook_x64.dll`.

The mutex guard is verified by `tools/dupguard`, which loads the same DLL twice under two
names and asserts the second is rejected:

```
first  copy (dupA.dll): LOADED (err 0)
second copy (dupB.dll): REJECTED (err 1114)
PASS: guard rejected the duplicate
```

The hook itself cannot be used for that test: without `game.dll` loaded its `ProcessAttach`
aborts and returns `FALSE`, which triggers `DLL_PROCESS_DETACH` and releases the mutex, so
the guard never engages. `tools/dupguard` reproduces the guard's exact logic in a DLL whose
attach succeeds.
