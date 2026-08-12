# Hook DLL provenance

**The binaries this describes are no longer kept here.** They were throwaway Phase 0 probes,
deleted once the MinGW port landed and this became a public repository — upstream's compiled
DLLs are theirs to distribute, not ours. The record is kept because PHASE0.md's findings only
make sense against a known build. To reproduce it, extract the installer named below.

These were **throwaway probe binaries**, not a dependency. They exist to answer one
question in Phase 0 — does the Wine file-based IPC work under Proton at all? — before
committing to the MinGW port in Phase 1. Delete them once the port lands.

## Source

| | |
|---|---|
| Release | `1.5.9715.11589` |
| Published | 2026-08-07T09:27:18Z |
| Installer | https://github.com/marius00/iagd/releases/download/1.5.9715.11589/GDItemAssistant.exe |
| Installer SHA-256 | `040682f72f514dd20a9eb5e332e3d6d2a92cf759339cd9ec24d16819830f4efd` |
| Extracted with | `innoextract 1.9` (extract only — the installer was never executed) |
| Build timestamp on files | 2026-08-07 08:26 |

```
feffb0495e298ed4907b09cfaa4f73bdcf10e77e0b80315dd0491a4bc21ec989  ItemAssistantHook_x64.dll
9c860cb73a680e3519ba768799744cbc120bd8639ad51e8c66a10c5796651541  ItemAssistantHook_playtest_x64.dll
```

## Why this release specifically

The release tag `1.5.9715.11589` points at commit `c3ce9fe`, which is one commit behind
the vendored upstream HEAD (`5ba6ec4`). Critically:

```
git diff --stat 1.5.9715.11589 HEAD -- HookDll/     # empty
```

**The DLL source is byte-identical between this release and HEAD.** The binary here was
therefore built from exactly the source in `vendor/iagd/HookDll/Hook/`, which makes it a
valid A/B baseline for validating the Phase 1 MinGW port.

## Wine IPC verified present

The 2025-11-25 build previously installed in the Grim Dawn prefix predates the file-based
IPC (landed `72c0579`, 2026-04-17) and could not be used. This build was checked directly:

```
strings -el ItemAssistantHook_x64.dll   # all found as UTF-16
  linuxhack            → bridge folder      (dllmain.cpp:437)
  ABORTED              → abort marker       (dllmain.cpp:384)
  isRunningInWine      → settings key       (SettingsReader.cpp:98)
  Wine mode enabled    → log line           (dllmain.cpp:439)
  stashToLootFrom      → settings key       (SettingsReader.cpp:16)
```

Confirmed by tag ancestry: `git merge-base --is-ancestor 72c0579 1.5.9715.11589` → true.

## Which variant to use

`InjectionHelper` selects `ItemAssistantHook_x64.dll` by default and swaps to the
`_playtest_` variant only when a Grim Dawn playtest build is detected. Use the base
variant unless testing against a playtest install. The `-GD12` variant in the installer is
for Grim Dawn 1.2 and is not staged here.
