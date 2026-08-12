// Shim: read_json / write_json for the minimal ptree above.
//
// Reading goes through nlohmann/json (vendored) for correctness on real-world settings
// files. Writing is done directly, because it must reproduce property_tree's output shape:
// all scalars quoted, and arrays represented as children with empty keys.

#pragma once

#include "ptree.hpp"

#include <istream>
#include <ostream>
#include <string>

#include <nlohmann/json.hpp>

namespace boost {
namespace property_tree {
namespace json_parser {

namespace detail {

using boost::property_tree::detail::assign;
using boost::property_tree::detail::narrow;

template <class Ch>
void from_json(const nlohmann::json &j, basic_ptree<Ch> &out) {
    if (j.is_object()) {
        for (auto it = j.begin(); it != j.end(); ++it) {
            basic_ptree<Ch> child;
            from_json(*it, child);
            std::basic_string<Ch> key;
            assign(key, it.key());
            out.push_back({key, child});
        }
        return;
    }

    if (j.is_array()) {
        for (const auto &element : j) {
            basic_ptree<Ch> child;
            from_json(element, child);
            out.push_back({std::basic_string<Ch>(), child});   // ptree models arrays as empty keys
        }
        return;
    }

    // Scalars are stored as text, matching property_tree.
    std::string raw;
    if (j.is_string())        raw = j.get<std::string>();
    else if (j.is_boolean())  raw = j.get<bool>() ? "true" : "false";
    else if (j.is_null())     raw = "";
    else                      raw = j.dump();

    std::basic_string<Ch> data;
    assign(data, raw);
    out.set_data(data);
}

inline void escape_into(std::string &dst, const std::string &src) {
    for (char c : src) {
        switch (c) {
            case '"':  dst += "\\\""; break;
            case '\\': dst += "\\\\"; break;
            case '\n': dst += "\\n";  break;
            case '\r': dst += "\\r";  break;
            case '\t': dst += "\\t";  break;
            default:
                if ((unsigned char)c < 0x20) {
                    char buf[8];
                    snprintf(buf, sizeof(buf), "\\u%04x", (unsigned char)c);
                    dst += buf;
                } else {
                    dst += c;
                }
        }
    }
}

template <class Ch>
void to_json_text(const basic_ptree<Ch> &tree, std::string &out, int indent) {
    const std::string pad(indent * 4, ' ');
    const std::string padInner((indent + 1) * 4, ' ');

    if (tree.children().empty()) {
        out += '"';
        escape_into(out, narrow(tree.data()));
        out += '"';
        return;
    }

    const bool isArray = std::all_of(tree.children().begin(), tree.children().end(),
                                     [](const auto &kv) { return kv.first.empty(); });

    out += isArray ? "[\n" : "{\n";
    bool first = true;
    for (const auto &kv : tree.children()) {
        if (!first) out += ",\n";
        first = false;
        out += padInner;
        if (!isArray) {
            out += '"';
            escape_into(out, narrow(kv.first));
            out += "\": ";
        }
        to_json_text(kv.second, out, indent + 1);
    }
    out += '\n';
    out += pad;
    out += isArray ? ']' : '}';
}

} // namespace detail

template <class Ch>
void read_json(std::basic_istream<Ch> &stream, basic_ptree<Ch> &tree) {
    std::basic_string<Ch> content((std::istreambuf_iterator<Ch>(stream)),
                                   std::istreambuf_iterator<Ch>());

    const std::string utf8 = boost::property_tree::detail::narrow(content);
    if (utf8.empty())
        throw std::runtime_error("read_json: empty input");

    nlohmann::json parsed = nlohmann::json::parse(utf8);   // throws on malformed input, as boost does

    tree = basic_ptree<Ch>();
    detail::from_json(parsed, tree);
}

template <class Ch>
void write_json(std::basic_ostream<Ch> &stream, const basic_ptree<Ch> &tree) {
    std::string out;
    detail::to_json_text(tree, out, 0);
    out += '\n';

    if constexpr (sizeof(Ch) == 1) {
        stream << out.c_str();
    } else {
        stream << boost::property_tree::detail::to_wide(out).c_str();
    }
}

} // namespace json_parser

using json_parser::read_json;
using json_parser::write_json;

} // namespace property_tree
} // namespace boost
