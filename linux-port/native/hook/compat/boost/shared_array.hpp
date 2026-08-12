// Shim: boost::shared_array -> std::shared_ptr<T[]>.
// Identical ownership semantics and array-delete behaviour since C++17.
#pragma once
#include <memory>
namespace boost {
template <class T> using shared_array = std::shared_ptr<T[]>;
template <class T> using shared_ptr   = std::shared_ptr<T>;
}
