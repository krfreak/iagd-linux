#include "detours.h"
#include "MinHook.h"
#include "Logger.h"   // LogToFile, so hook-install failures are visible in the hook log

#include <mutex>
#include <unordered_map>
#include <vector>
#include <string>
#include <cstdint>

namespace {

std::mutex g_mutex;
bool       g_initialized = false;
bool       g_inTransaction = false;

// Detours hands DetourDetach the trampoline; MinHook needs the target.
std::unordered_map<void *, void *> g_trampolineToTarget;

// Applied on commit, so a transaction stays all-or-nothing from the caller's point of
// view even though MinHook installs eagerly.
struct PendingRemoval {
    void *target;
    void **callerPointer;
};
std::vector<PendingRemoval> g_pendingRemovals;

bool ensure_initialized() {
    if (g_initialized)
        return true;

    MH_STATUS st = MH_Initialize();
    if (st == MH_OK || st == MH_ERROR_ALREADY_INITIALIZED) {
        g_initialized = true;
        return true;
    }
    return false;
}

} // namespace

extern "C" {

LONG DetourTransactionBegin(void) {
    std::lock_guard<std::mutex> lock(g_mutex);

    if (!ensure_initialized())
        return ERROR_INVALID_OPERATION;

    g_inTransaction = true;
    g_pendingRemovals.clear();
    return NO_ERROR;
}

LONG DetourTransactionAbort(void) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_inTransaction = false;
    g_pendingRemovals.clear();
    return NO_ERROR;
}

// MinHook suspends and fixes up threads itself inside MH_ApplyQueued, so there is nothing
// for the caller to register. Accepting and ignoring the handle keeps the call sites intact.
LONG DetourUpdateThread(HANDLE hThread) {
    (void)hThread;
    return NO_ERROR;
}

LONG DetourAttach(PVOID *ppPointer, PVOID pDetour) {
    std::lock_guard<std::mutex> lock(g_mutex);

    if (!ppPointer || !*ppPointer || !pDetour)
        return ERROR_INVALID_PARAMETER;
    if (!ensure_initialized())
        return ERROR_INVALID_OPERATION;

    void *target = *ppPointer;
    void *trampoline = nullptr;

    MH_STATUS st = MH_CreateHook(target, pDetour, &trampoline);
    if (st != MH_OK) {
        // Callers (BaseMethodHook::HookDll) do not check the return value, so a silent
        // failure here would leave the caller holding the raw target address and no hook
        // installed. Make it loud.
        LogToFile(LogLevel::FATAL,
            std::string("MinHook: MH_CreateHook failed with status ") + std::to_string((int)st)
            + " for target " + std::to_string((uintptr_t)target));
        return ERROR_INVALID_BLOCK;
    }

    st = MH_QueueEnableHook(target);
    if (st != MH_OK) {
        LogToFile(LogLevel::FATAL,
            std::string("MinHook: MH_QueueEnableHook failed with status ") + std::to_string((int)st));
        MH_RemoveHook(target);
        return ERROR_INVALID_BLOCK;
    }

    LogToFile(LogLevel::WARNING,
        std::string("MinHook: hooked target ") + std::to_string((uintptr_t)target)
        + " trampoline " + std::to_string((uintptr_t)trampoline));

    g_trampolineToTarget[trampoline] = target;

    // Detours' contract: the caller's pointer becomes the trampoline.
    *ppPointer = trampoline;
    return NO_ERROR;
}

LONG DetourDetach(PVOID *ppPointer, PVOID pDetour) {
    std::lock_guard<std::mutex> lock(g_mutex);
    (void)pDetour;

    if (!ppPointer || !*ppPointer)
        return ERROR_INVALID_PARAMETER;
    if (!g_initialized)
        return ERROR_INVALID_OPERATION;

    auto it = g_trampolineToTarget.find(*ppPointer);
    if (it == g_trampolineToTarget.end())
        return ERROR_INVALID_BLOCK;

    void *target = it->second;

    if (MH_QueueDisableHook(target) != MH_OK)
        return ERROR_INVALID_BLOCK;

    // Deferred to commit: the trampoline must stay valid until the hook is actually off,
    // and the caller's pointer still refers to it until then.
    g_pendingRemovals.push_back({target, ppPointer});
    g_trampolineToTarget.erase(it);
    return NO_ERROR;
}

LONG DetourTransactionCommit(void) {
    std::lock_guard<std::mutex> lock(g_mutex);

    if (!g_initialized)
        return ERROR_INVALID_OPERATION;

    MH_STATUS st = MH_ApplyQueued();
    if (st != MH_OK) {
        LogToFile(LogLevel::FATAL,
            std::string("MinHook: MH_ApplyQueued failed with status ") + std::to_string((int)st));
    }

    for (const auto &removal : g_pendingRemovals) {
        MH_RemoveHook(removal.target);
        // Detours restores the caller's pointer to the original target on detach.
        if (removal.callerPointer)
            *removal.callerPointer = removal.target;
    }
    g_pendingRemovals.clear();
    g_inTransaction = false;

    return st == MH_OK ? NO_ERROR : ERROR_INVALID_BLOCK;
}

LONG DetourCompatShutdown(void) {
    std::lock_guard<std::mutex> lock(g_mutex);

    if (!g_initialized)
        return NO_ERROR;

    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();

    g_trampolineToTarget.clear();
    g_pendingRemovals.clear();
    g_initialized = false;
    return NO_ERROR;
}

} // extern "C"
