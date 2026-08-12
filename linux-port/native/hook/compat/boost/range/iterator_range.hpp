// Shim: make_iterator_range(directory_iterator(p), {}) is used purely to make a
// directory_iterator usable in a range-for. std::filesystem::directory_iterator already
// satisfies that directly, so the range object just forwards the begin iterator.
#pragma once
#include <utility>
namespace boost {
template <class It> class iterator_range {
public:
    iterator_range(It begin, It end) : m_begin(std::move(begin)), m_end(std::move(end)) {}
    It begin() const { return m_begin; }
    It end() const { return m_end; }
private:
    It m_begin, m_end;
};
template <class It> iterator_range<It> make_iterator_range(It begin, It end) {
    return iterator_range<It>(std::move(begin), std::move(end));
}
}
