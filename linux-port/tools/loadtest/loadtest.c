// Phase 0 diagnostic: why does LoadLibraryA on the hook DLL return NULL?
//
// The injector can only report "returned NULL" — it cannot read GetLastError() out of the
// remote process. This runs in-prefix and prints the actual code, which distinguishes the
// candidate causes:
//
//    126  ERROR_MOD_NOT_FOUND    a dependency DLL is missing
//    127  ERROR_PROC_NOT_FOUND   a dependency lacks an imported symbol
//    193  ERROR_BAD_EXE_FORMAT   architecture mismatch
//   1114  ERROR_DLL_INIT_FAILED  the DLL loaded but DllMain returned FALSE
//                                (i.e. IA's deliberate "game not ready" abort)
//
// Build:  x86_64-w64-mingw32-gcc -O2 -municode -o loadtest.exe loadtest.c
// Run:    "$PROTON" run Z:\path\to\loadtest.exe Z:\path\to\hook.dll

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

// Under `proton run` there is no console attached, so wprintf never reaches the caller's
// pipe. Everything is mirrored to a log file the Linux side can read directly.
static FILE *g_log = NULL;

static void outf(const wchar_t *fmt, ...) {
    va_list a;
    va_start(a, fmt);
    vwprintf(fmt, a);
    va_end(a);

    if (g_log) {
        va_start(a, fmt);
        vfwprintf(g_log, fmt, a);
        va_end(a);
        fflush(g_log);
    }
}

static const char *explain(DWORD e) {
    switch (e) {
        case 0:    return "success";
        case 126:  return "ERROR_MOD_NOT_FOUND - a dependency DLL is missing";
        case 127:  return "ERROR_PROC_NOT_FOUND - a dependency lacks an imported symbol";
        case 193:  return "ERROR_BAD_EXE_FORMAT - architecture mismatch";
        case 1114: return "ERROR_DLL_INIT_FAILED - DllMain ran and returned FALSE";
        case 998:  return "ERROR_NOACCESS";
        case 2:    return "ERROR_FILE_NOT_FOUND - the DLL path itself is wrong";
        case 3:    return "ERROR_PATH_NOT_FOUND";
        default:   return "(see winerror.h)";
    }
}

int wmain(int argc, wchar_t **argv) {
    // argv[2] optionally overrides the log path (Windows-style).
    g_log = _wfopen(argc >= 3 ? argv[2] : L"Z:\\tmp\\iagd-loadtest.log", L"w");

    if (argc < 2) {
        outf(L"usage: loadtest.exe <dll path> [log path]\n");
        return 2;
    }

    outf(L"Target: %ls\n", argv[1]);

    // A bare module name ("MSVCP140.dll") has no path to check — the loader resolves it
    // via the search order, and for api-set names there is deliberately no file at all.
    // Only treat a missing file as fatal when an explicit path was given.
    BOOL has_path = (wcschr(argv[1], L'\\') != NULL) || (wcschr(argv[1], L'/') != NULL);
    DWORD attrs = GetFileAttributesW(argv[1]);

    if (attrs == INVALID_FILE_ATTRIBUTES) {
        if (has_path) {
            outf(L"  file not visible from the prefix (GetFileAttributes err %lu)\n", GetLastError());
            return 1;
        }
        outf(L"  no file on disk; resolving by name through the loader\n\n");
    } else {
        outf(L"  file is visible from the prefix\n\n");
    }

    // Map without running DllMain. Isolates "can the loader resolve imports?" from
    // "does DllMain succeed?".
    SetLastError(0);
    HMODULE asData = LoadLibraryExW(argv[1], NULL, LOAD_LIBRARY_AS_DATAFILE);
    DWORD dataErr = GetLastError();
    outf(L"LOAD_LIBRARY_AS_DATAFILE : %ls (err %lu - %hs)\n",
            asData ? L"OK" : L"FAILED", asData ? 0 : dataErr,
            explain(asData ? 0 : dataErr));
    if (asData) FreeLibrary(asData);

    // Resolve imports but still skip DllMain.
    SetLastError(0);
    HMODULE noInit = LoadLibraryExW(argv[1], NULL, DONT_RESOLVE_DLL_REFERENCES);
    DWORD noInitErr = GetLastError();
    outf(L"DONT_RESOLVE_DLL_REFERENCES: %ls (err %lu - %hs)\n",
            noInit ? L"OK" : L"FAILED", noInit ? 0 : noInitErr,
            explain(noInit ? 0 : noInitErr));
    if (noInit) FreeLibrary(noInit);

    // The real thing: resolve imports and run DllMain.
    SetLastError(0);
    HMODULE full = LoadLibraryW(argv[1]);
    DWORD fullErr = GetLastError();
    outf(L"LoadLibraryW (full)        : %ls (err %lu - %hs)\n",
            full ? L"OK" : L"FAILED", full ? 0 : fullErr,
            explain(full ? 0 : fullErr));

    if (full) {
        outf(L"\nLoaded at %p. DllMain accepted the attach.\n", (void *)full);
        FreeLibrary(full);
        return 0;
    }

    outf(L"\nVerdict: ");
    if (fullErr == 1114) {
        outf(L"the DLL and all its dependencies loaded fine.\n"
                L"DllMain rejected the attach - this is IA's deliberate abort, meaning the\n"
                L"game was not ready to be hooked. Expected when injecting at process start.\n");
    } else if (noInit && !full) {
        outf(L"imports resolve, so this is a DllMain failure, not a dependency problem.\n");
    } else {
        outf(L"dependency resolution failed - err %lu.\n", fullErr);
    }
    return 1;
}
