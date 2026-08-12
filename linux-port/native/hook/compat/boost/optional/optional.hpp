// Shim: boost::optional -> std::optional. Only tested for engagement (`if (!child)`)
// and dereferenced, both of which std::optional supports identically.
#pragma once
#include <optional>
namespace boost {
template <class T> using optional = std::optional<T>;
}
