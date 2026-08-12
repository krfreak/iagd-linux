// Phase 0 canary: a minimal DLL that proves (a) our injection pipeline works end to end,
// and (b) whether SHGetKnownFolderPath is survivable from an injected DLL's static
// initialisers under Proton.
//
// The IA hook DLL calls SHGetKnownFolderPath(FOLDERID_RoamingAppData) from
// HookLog's constructor (HookDll/Hook/HookLog.cpp:26 -> GetIagdFolder), which runs during
// static initialisation — under the loader lock. If that call fails, GetIagdFolder frees
// an uninitialised pointer and then logs through g_log while g_log is still being
// constructed. Either is fatal, and both happen before any log file exists, which is
// exactly what we observe.
//
// Markers are written with kernel32 only, so marker 1 cannot itself be the thing that
// fails. Each stage writes its own file; the highest-numbered file present is how far
// initialisation got.
//
//   canary-1-staticinit.txt   static initialisers ran at all
//   canary-2-shgetknown.txt   SHGetKnownFolderPath returned (with its HRESULT)
//   canary-3-dllmain.txt      DllMain(DLL_PROCESS_ATTACH) ran
//
// Build:  x86_64-w64-mingw32-gcc -shared -O2 -o canary.dll canary.c -lshell32 -lole32

#include <windows.h>
#include <objbase.h>
#include <string.h>
#include <stdio.h>

#define MARKER_DIR L"C:\\iagd\\"

static void write_marker(const wchar_t *name, const char *text) {
    wchar_t path[MAX_PATH];
    lstrcpynW(path, MARKER_DIR, MAX_PATH);
    lstrcatW(path, name);

    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE)
        return;

    DWORD written = 0;
    WriteFile(h, text, (DWORD)strlen(text), &written, NULL);
    CloseHandle(h);
}

/* {3EB685DB-65F9-4CF6-A03A-E3EF65729F3D} — defined locally so this does not depend on
   which knownfolders GUIDs the toolchain happens to export. */
static const GUID kRoamingAppData = {
    0x3EB685DB, 0x65F9, 0x4CF6, {0xA0, 0x3A, 0xE3, 0xEF, 0x65, 0x72, 0x9F, 0x3D}
};

typedef HRESULT (WINAPI *PFN_SHGetKnownFolderPath)(const GUID *, DWORD, HANDLE, PWSTR *);

__attribute__((constructor))
static void on_static_init(void) {
    write_marker(L"canary-1-staticinit.txt",
                 "static initialisers ran (kernel32-only path OK)\n");

    /* Resolved dynamically: a static import of shell32 would change the loader's work at
       load time, and the point is to test the call itself, not the import. */
    HMODULE shell32 = LoadLibraryW(L"shell32.dll");
    if (!shell32) {
        write_marker(L"canary-2-shgetknown.txt",
                     "FAILED: could not LoadLibrary shell32.dll from static init\n");
        return;
    }

    PFN_SHGetKnownFolderPath fn =
        (PFN_SHGetKnownFolderPath)(void *)GetProcAddress(shell32, "SHGetKnownFolderPath");
    if (!fn) {
        write_marker(L"canary-2-shgetknown.txt",
                     "FAILED: shell32.dll has no SHGetKnownFolderPath\n");
        return;
    }

    PWSTR out = NULL;
    HRESULT hr = fn(&kRoamingAppData, 0, NULL, &out);

    char buf[512];
    if (hr == S_OK && out) {
        char narrow[MAX_PATH] = {0};
        WideCharToMultiByte(CP_UTF8, 0, out, -1, narrow, sizeof(narrow) - 1, NULL, NULL);
        snprintf(buf, sizeof(buf),
                 "SHGetKnownFolderPath OK from static init\nhr=0x%08lX\npath=%s\n",
                 (unsigned long)hr, narrow);
        CoTaskMemFree(out);
    } else {
        /* This is the branch that kills the real hook DLL: it would CoTaskMemFree an
           uninitialised pointer here and then log through a half-built g_log. */
        snprintf(buf, sizeof(buf),
                 "SHGetKnownFolderPath FAILED from static init\nhr=0x%08lX\n"
                 "This is the branch that crashes the IA hook DLL.\n",
                 (unsigned long)hr);
    }
    write_marker(L"canary-2-shgetknown.txt", buf);
}

BOOL APIENTRY DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved) {
    (void)inst; (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        char buf[128];
        snprintf(buf, sizeof(buf), "DllMain DLL_PROCESS_ATTACH ran in pid %lu\n",
                 (unsigned long)GetCurrentProcessId());
        write_marker(L"canary-3-dllmain.txt", buf);
    }
    return TRUE;
}
