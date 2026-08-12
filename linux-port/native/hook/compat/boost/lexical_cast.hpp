// Shim: boost::lexical_cast, only ever used for number -> string here.
#pragma once
#include <sstream>
#include <string>
namespace boost {
template <class Target, class Source>
Target lexical_cast(const Source &src) {
    std::basic_ostringstream<typename Target::value_type> oss;
    oss << src;
    return oss.str();
}
}
