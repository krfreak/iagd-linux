// Shim: a minimal boost::property_tree over nlohmann/json.
//
// Only the operations the hook actually performs are implemented:
//
//   read_json(stream, tree)          settings.json, and CSV-adjacent config reads
//   tree.get_child_optional(path)    presence check for a dotted path
//   tree.get<T>(path)                int / bool extraction
//   tree.put(key, value)             scalar write, dotted path auto-creates
//   tree.add_child(key, subtree)     nested object
//   tree.push_back({"", subtree})    array element (ptree models arrays as empty keys)
//   write_json(stream, tree)         serialise
//
// Deliberately faithful quirk: property_tree stores every scalar as a string and writes
// them all quoted, so `{"seed": "123"}` rather than `{"seed": 123}`. That is what the
// existing IAGD release emits and what its C# side already consumes, so reproducing it
// keeps the ported DLL byte-compatible with the current wire format.

#pragma once

#include <algorithm>
#include <optional>
#include <sstream>
#include <string>
#include <utility>
#include <vector>

#include <windows.h>

namespace boost {
namespace property_tree {

namespace detail {

inline std::string to_utf8(const std::wstring &w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string out((size_t)(n > 0 ? n : 0), '\0');
    if (n > 0)
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), out.data(), n, nullptr, nullptr);
    return out;
}

inline std::wstring to_wide(const std::string &s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring out((size_t)(n > 0 ? n : 0), L'\0');
    if (n > 0)
        MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), out.data(), n);
    return out;
}

// Narrow/wide bridges so a single template body serves both ptree and wptree.
inline std::string  narrow(const std::string &s)  { return s; }
inline std::string  narrow(const std::wstring &s) { return to_utf8(s); }
inline void assign(std::string &dst, const std::string &src)  { dst = src; }
inline void assign(std::wstring &dst, const std::string &src) { dst = to_wide(src); }

template <class Ch, class T> struct stringify {
    static std::basic_string<Ch> apply(const T &v) {
        std::basic_ostringstream<Ch> oss;
        oss << v;
        return oss.str();
    }
};
// Narrow text into a wide tree has to go through UTF-8 rather than operator<<.
template <> struct stringify<wchar_t, std::string> {
    static std::wstring apply(const std::string &v) { return to_wide(v); }
};
template <> struct stringify<wchar_t, const char *> {
    static std::wstring apply(const char *v) { return to_wide(v ? v : ""); }
};
template <> struct stringify<wchar_t, char *> {
    static std::wstring apply(char *v) { return to_wide(v ? v : ""); }
};

template <class Ch> struct chars;
template <> struct chars<char> {
    static char dot()   { return '.'; }
    static const char *true_() { return "true"; }
};
template <> struct chars<wchar_t> {
    static wchar_t dot() { return L'.'; }
    static const wchar_t *true_() { return L"true"; }
};

} // namespace detail

template <class Ch>
class basic_ptree {
public:
    using self_type   = basic_ptree<Ch>;
    using string_type = std::basic_string<Ch>;
    using value_type  = std::pair<string_type, self_type>;

    basic_ptree() = default;
    explicit basic_ptree(string_type data) : m_data(std::move(data)) {}

    // ---------------------------------------------------------------- accessors

    const string_type &data() const { return m_data; }
    void set_data(string_type d) { m_data = std::move(d); }

    bool empty() const { return m_children.empty(); }
    typename std::vector<value_type>::iterator begin() { return m_children.begin(); }
    typename std::vector<value_type>::iterator end() { return m_children.end(); }
    typename std::vector<value_type>::const_iterator begin() const { return m_children.begin(); }
    typename std::vector<value_type>::const_iterator end() const { return m_children.end(); }

    const std::vector<value_type> &children() const { return m_children; }

    // ---------------------------------------------------------------- lookup

    self_type *find_path(const string_type &path) {
        self_type *node = this;
        for (const auto &part : split(path)) {
            self_type *next = nullptr;
            for (auto &child : node->m_children) {
                if (child.first == part) { next = &child.second; break; }
            }
            if (!next) return nullptr;
            node = next;
        }
        return node;
    }

    std::optional<std::reference_wrapper<self_type>> get_child_optional(const string_type &path) {
        if (self_type *node = find_path(path))
            return std::ref(*node);
        return std::nullopt;
    }

    self_type &get_child(const string_type &path) {
        self_type *node = find_path(path);
        if (!node) throw std::runtime_error("ptree: no such path");
        return *node;
    }

    template <class T> T get(const string_type &path) const {
        self_type *node = const_cast<self_type *>(this)->find_path(path);
        if (!node) throw std::runtime_error("ptree: no such path");
        return node->template value<T>();
    }

    template <class T> T get(const string_type &path, const T &fallback) const {
        self_type *node = const_cast<self_type *>(this)->find_path(path);
        if (!node) return fallback;
        try { return node->template value<T>(); } catch (...) { return fallback; }
    }

    // ---------------------------------------------------------------- mutation

    template <class T> void put(const string_type &path, const T &v) {
        ensure_path(path)->m_data = detail::stringify<Ch, T>::apply(v);
    }
    // Explicit-template form used at one call site: put<std::string>("text", ...).
    template <class T> void put(const string_type &path, const T &v, int) {
        put<T>(path, v);
    }

    void add_child(const string_type &path, const self_type &subtree) {
        *ensure_path(path) = subtree;
    }

    void push_back(const value_type &pair) { m_children.push_back(pair); }

private:
    template <class T> T value() const;

    static std::vector<string_type> split(const string_type &path) {
        std::vector<string_type> parts;
        string_type current;
        for (Ch c : path) {
            if (c == detail::chars<Ch>::dot()) {
                parts.push_back(current);
                current.clear();
            } else {
                current.push_back(c);
            }
        }
        parts.push_back(current);
        return parts;
    }

    self_type *ensure_path(const string_type &path) {
        self_type *node = this;
        for (const auto &part : split(path)) {
            self_type *next = nullptr;
            for (auto &child : node->m_children) {
                if (child.first == part) { next = &child.second; break; }
            }
            if (!next) {
                node->m_children.emplace_back(part, self_type{});
                next = &node->m_children.back().second;
            }
            node = next;
        }
        return node;
    }

    string_type            m_data;
    std::vector<value_type> m_children;
};

// value<T> specialisations: everything is stored as text, so reads parse.
template <> template <>
inline std::string basic_ptree<char>::value<std::string>() const { return m_data; }

template <> template <>
inline std::wstring basic_ptree<wchar_t>::value<std::wstring>() const { return m_data; }

template <class Ch> template <class T>
inline T basic_ptree<Ch>::value() const {
    std::basic_istringstream<Ch> iss(m_data);
    T out{};
    iss >> out;
    return out;
}

// bool needs the textual forms; operator>> would only accept 0/1.
template <> template <>
inline bool basic_ptree<char>::value<bool>() const {
    std::string s = m_data;
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return (char)::tolower(c); });
    return s == "true" || s == "1";
}
template <> template <>
inline bool basic_ptree<wchar_t>::value<bool>() const {
    std::wstring s = m_data;
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) { return (wchar_t)::towlower(c); });
    return s == L"true" || s == L"1";
}

using ptree  = basic_ptree<char>;
using wptree = basic_ptree<wchar_t>;

} // namespace property_tree
} // namespace boost
