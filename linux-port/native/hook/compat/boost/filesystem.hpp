// Shim: boost::filesystem -> std::filesystem.
// Used for is_directory / create_directories / directory_iterator only. The call sites
// pass std::wstring, which std::filesystem::path accepts implicitly on Windows.
#pragma once
#include <filesystem>
namespace boost {
namespace filesystem {
using path               = std::filesystem::path;
using directory_iterator = std::filesystem::directory_iterator;
using directory_entry    = std::filesystem::directory_entry;

inline bool is_directory(const path &p) {
    std::error_code ec;
    return std::filesystem::is_directory(p, ec);
}
inline bool create_directories(const path &p) {
    std::error_code ec;
    return std::filesystem::create_directories(p, ec);
}
inline bool exists(const path &p) {
    std::error_code ec;
    return std::filesystem::exists(p, ec);
}
inline bool remove(const path &p) {
    std::error_code ec;
    return std::filesystem::remove(p, ec);
}
}
}
