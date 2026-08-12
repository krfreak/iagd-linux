// Loads two copies of the same DLL under DIFFERENT filenames -- exactly the mistake that
// crashed the game -- and reports whether the second was rejected.
#include <windows.h>
#include <stdio.h>

int wmain(void) {
    FILE *log = _wfopen(L"Z:\\tmp\\duptest.log", L"w");

    HMODULE a = LoadLibraryW(L"C:\\iagd\\dupA.dll");
    DWORD ea = GetLastError();
    HMODULE b = LoadLibraryW(L"C:\\iagd\\dupB.dll");
    DWORD eb = GetLastError();

    if (log) {
        fwprintf(log, L"first  copy (dupA.dll): %ls (err %lu)\n", a ? L"LOADED" : L"REJECTED", a ? 0 : ea);
        fwprintf(log, L"second copy (dupB.dll): %ls (err %lu)\n", b ? L"LOADED" : L"REJECTED", b ? 0 : eb);
        fwprintf(log, L"\n%ls\n", (a && !b) ? L"PASS: guard rejected the duplicate"
                                            : L"FAIL: both copies loaded -- guard ineffective");
        fclose(log);
    }
    return (a && !b) ? 0 : 1;
}
