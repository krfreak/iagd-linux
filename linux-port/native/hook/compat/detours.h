// Detours-compatible shim over MinHook.
//
// The hook source uses exactly five Detours entry points. Rather than rewrite the call
// sites — which would inflate the diff against upstream and make every future rebase
// painful — this reimplements those five on top of MinHook, preserving Detours' semantics
// exactly. HookDll/Hook/*.cpp keeps its `#include <detours.h>` unchanged.
//
// The semantics being preserved:
//
//   DetourAttach(&p, detour)   p is the target function on entry, and is REPLACED with the
//                              trampoline on success. Callers invoke the original through
//                              the trampoline afterwards.
//   DetourDetach(&p, detour)   p is the trampoline on entry, and is restored to the target.
//
// MinHook keys everything by target address, whereas Detours' detach is handed a
// trampoline, so the shim keeps a trampoline -> target map to bridge the two.

#pragma once

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

LONG DetourTransactionBegin(void);
LONG DetourTransactionCommit(void);
LONG DetourTransactionAbort(void);
LONG DetourUpdateThread(HANDLE hThread);
LONG DetourAttach(PVOID *ppPointer, PVOID pDetour);
LONG DetourDetach(PVOID *ppPointer, PVOID pDetour);

// Not part of Detours. Lets DllMain tear MinHook down on unload; Detours has no equivalent
// because it has no global state to release.
LONG DetourCompatShutdown(void);

#ifdef __cplusplus
}
#endif
