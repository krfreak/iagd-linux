// Validates the single-instance mutex added to the hook DLL (dllmain.cpp:ClaimSingleInstance).
//
// The hook cannot be used to test this directly: without game.dll loaded its ProcessAttach
// aborts and returns FALSE, which triggers DLL_PROCESS_DETACH and releases the mutex, so
// the guard never engages. This DLL reproduces the guard's exact logic and succeeds, so a
// second copy under a different filename is genuinely rejected.
#include <windows.h>
#include <stdio.h>

static HANDLE g_mutex = NULL;

static void marker(const char *text) {
    HANDLE h = CreateFileW(L"C:\\iagd\\dupguard.log", FILE_APPEND_DATA, FILE_SHARE_READ,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    DWORD w; WriteFile(h, text, (DWORD)strlen(text), &w, NULL);
    CloseHandle(h);
}

static BOOL claim_single_instance(void) {
    wchar_t name[128];
    swprintf(name, 128, L"Local\\DupGuardTest_%lu", (unsigned long)GetCurrentProcessId());

    g_mutex = CreateMutexW(NULL, TRUE, name);
    if (g_mutex == NULL) return TRUE;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(g_mutex); g_mutex = NULL;
        return FALSE;
    }
    return TRUE;
}

BOOL APIENTRY DllMain(HINSTANCE i, DWORD reason, LPVOID r) {
    (void)i; (void)r;
    if (reason == DLL_PROCESS_ATTACH) {
        if (!claim_single_instance()) {
            marker("SECOND copy REJECTED by the mutex guard (correct)\n");
            return FALSE;   // as the hook does
        }
        marker("FIRST copy attached and claimed the mutex\n");
        return TRUE;
    }
    if (reason == DLL_PROCESS_DETACH && g_mutex) {
        ReleaseMutex(g_mutex); CloseHandle(g_mutex); g_mutex = NULL;
    }
    return TRUE;
}
