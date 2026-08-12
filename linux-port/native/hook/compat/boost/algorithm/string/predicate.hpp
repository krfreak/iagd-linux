#pragma once
#include <algorithm>
#include <cctype>
#include <string>
namespace boost {
namespace algorithm {

// Call sites mix char types: ends_with(std::wstring, ".csv"). Boost's range-based
// version tolerates that, so widen the needle to match rather than force edits upstream.
template <class Ch> std::basic_string<Ch> as_string(const char *s) {
    return std::basic_string<Ch>(s, s + std::char_traits<char>::length(s));
}
template <class Ch> std::basic_string<Ch> as_string(const std::basic_string<Ch> &s) { return s; }
template <class Ch> std::basic_string<Ch> as_string(const Ch *s) { return std::basic_string<Ch>(s); }

template <class S1, class S2> bool ends_with(const S1 &input, const S2 &test) {
    using Ch = typename S1::value_type;
    const std::basic_string<Ch> in(input);
    const std::basic_string<Ch> t = as_string<Ch>(test);
    return in.size() >= t.size() && in.compare(in.size() - t.size(), t.size(), t) == 0;
}
template <class S1, class S2> bool starts_with(const S1 &input, const S2 &test) {
    const std::basic_string<typename S1::value_type> in(input), t(test);
    return in.size() >= t.size() && in.compare(0, t.size(), t) == 0;
}

template <class S> void trim(S &s) {
    auto notSpace = [](typename S::value_type c) {
        return !(c == (typename S::value_type)' '  || c == (typename S::value_type)'\t' ||
                 c == (typename S::value_type)'\r' || c == (typename S::value_type)'\n');
    };
    s.erase(s.begin(), std::find_if(s.begin(), s.end(), notSpace));
    s.erase(std::find_if(s.rbegin(), s.rend(), notSpace).base(), s.end());
}
}
using algorithm::ends_with;
using algorithm::starts_with;
using algorithm::trim;
}
