// std::wstring_convert / <codecvt> were deprecated in C++17 and removed in GCC 16, so the
// two exception-logging sites that used them need a replacement. Win32 does the conversion
// directly and without the deprecated machinery.
#pragma once
#include <string>
#include <windows.h>

namespace Utf8Compat {

inline std::wstring FromBytes(const char *s) {
    if (!s || !*s) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring out((size_t)(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, out.data(), n);
    return out;
}

inline std::string ToBytes(const std::wstring &w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string out((size_t)(n > 0 ? n : 0), '\0');
    if (n > 0)
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), out.data(), n, nullptr, nullptr);
    return out;
}


// Drop-in stand-in for std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>>.
// Same to_bytes/from_bytes surface, so the ~20 call sites are unchanged; only the
// declaration line differs.
class Converter {
public:
    std::string  to_bytes(const std::wstring &w) const { return ToBytes(w); }
    std::string  to_bytes(const wchar_t *w) const      { return ToBytes(w ? std::wstring(w) : std::wstring()); }
    std::wstring from_bytes(const std::string &s) const { return FromBytes(s.c_str()); }
    std::wstring from_bytes(const char *s) const        { return FromBytes(s); }
};

} // namespace Utf8Compat
