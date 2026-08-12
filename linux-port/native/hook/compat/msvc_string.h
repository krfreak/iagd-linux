// MSVC-layout std::basic_string, for data that crosses between this DLL and Grim Dawn.
//
// Grim Dawn is built with MSVC; this DLL is built with mingw/libstdc++. Their
// std::basic_string implementations are both 32 bytes but lay their fields out completely
// differently:
//
//   offset   MSVC                              libstdc++
//   ------   -------------------------------   ---------------------------------
//   0        union { E buf[N]; E* ptr; }       E*      _M_p
//   8          (still inside that union)       size_t  _M_string_length
//   16       size_t _Mysize                    union { E local_buf[N]; size_t cap; }
//   24       size_t _Myres                       (continues)
//
// So when the game writes a short (SSO) string, the characters land at offsets 0-15.
// libstdc++ then reads those characters *as a pointer* and *as a length*, producing a
// garbage range. Iterating it — as GetModName does via std::remove — hangs the game.
// Upstream never hits this because MSVC talks to MSVC.
//
// This type reproduces MSVC's layout so the two sides agree.

#pragma once

#include <cstddef>
#include <cstring>
#include <string>
#include <string_view>

namespace MsvcInterop {

template <class E>
struct BasicString {
    // MSVC: _BUF_SIZE = 16 / sizeof(_Elem), floored at 1.
    static constexpr std::size_t kBufSize = (16 / sizeof(E)) < 1 ? 1 : (16 / sizeof(E));

    union {
        E  buf[kBufSize];
        E* ptr;
    } bx;
    std::size_t size;
    std::size_t res;    // capacity; < kBufSize means the data is inline in bx.buf

    /// MUST initialise. std::string self-initialises, so replacing it with a plain struct
    /// silently turned `ItemReplicaInfo replica;` into 10 fields of stack garbage. The game
    /// then assigns into them, and MSVC's operator= calls _Tidy_deallocate(), which frees
    /// _Bx._Ptr whenever _Myres >= _BUF_SIZE -- so a garbage capacity makes it deallocate a
    /// garbage pointer. That corrupts the heap and hangs the game.
    BasicString() { init_empty(); }

    bool is_inline() const { return res < kBufSize; }
    const E* data() const { return is_inline() ? bx.buf : bx.ptr; }

    /// Valid empty MSVC string, ready to be assigned to by the game.
    void init_empty() {
        bx.buf[0] = E(0);
        size = 0;
        res  = kBufSize - 1;
    }

    std::basic_string<E> to_std() const {
        const E* p = data();
        return (p == nullptr || size == 0) ? std::basic_string<E>() : std::basic_string<E>(p, size);
    }

    /// Present an existing buffer to the game for READ-ONLY use (a const& parameter).
    ///
    /// For longer strings this points at the caller's storage rather than copying, so that
    /// storage must outlive the call. The game will not free it: it only ever sees a const
    /// reference, and MSVC only releases a buffer it believes it allocated, which requires
    /// the object to be destroyed or reassigned.
    void borrow(const E* source, std::size_t length) {
        if (length < kBufSize) {
            std::memcpy(bx.buf, source, length * sizeof(E));
            bx.buf[length] = E(0);
            size = length;
            res  = kBufSize - 1;
        } else {
            bx.ptr = const_cast<E*>(source);
            size   = length;
            res    = length;      // capacity == length: exact fit, nothing to grow into
        }
    }

    void borrow(const std::basic_string<E>& s) { borrow(s.c_str(), s.size()); }

    // ---- reading a string the GAME wrote -------------------------------------------
    // Never free these: the buffer belongs to the game's allocator.

    const E* c_str() const {
        const E* p = data();
        static const E kEmpty[1] = { E(0) };
        return p ? p : kEmpty;
    }
    std::size_t length() const { return size; }
    bool empty() const { return size == 0; }

    /// Views the game's buffer without copying. Prefer this over to_std() for inspection.
    std::basic_string_view<E> view() const { return { c_str(), size }; }

    static constexpr std::size_t npos = std::basic_string<E>::npos;

    std::size_t find(const E* needle, std::size_t pos = 0) const { return view().find(needle, pos); }
    std::size_t find(const std::basic_string<E>& needle, std::size_t pos = 0) const {
        return view().find(needle, pos);
    }

    // ---- writing a string for the GAME to read ---------------------------------------
    // Short values live in the inline buffer; longer ones need storage that outlives the
    // call, so they are copied to a buffer this object owns. The game only ever receives
    // ItemReplicaInfo by const reference, so it will not free them -- free_owned() must be
    // called by whoever built the object.

    void assign_owned(const E* source, std::size_t length) {
        if (length < kBufSize) {
            std::memcpy(bx.buf, source, length * sizeof(E));
            bx.buf[length] = E(0);
            size = length;
            res  = kBufSize - 1;
            return;
        }

        E* owned = new E[length + 1];
        std::memcpy(owned, source, length * sizeof(E));
        owned[length] = E(0);
        bx.ptr = owned;
        size   = length;
        res    = length;
    }

    BasicString& operator=(const std::basic_string<E>& s) {
        assign_owned(s.c_str(), s.size());
        return *this;
    }

    /// Only valid for strings written by assign_owned. Calling it on a string the game
    /// allocated would hand its pointer to the wrong allocator.
    void free_owned() {
        if (!is_inline() && bx.ptr != nullptr) {
            delete[] bx.ptr;
        }
        init_empty();
    }
};

// Both specialisations must match MSVC's 32-byte string exactly.
static_assert(sizeof(BasicString<char>) == 32, "MSVC narrow string must be 32 bytes");
static_assert(sizeof(BasicString<wchar_t>) == 32, "MSVC wide string must be 32 bytes");
static_assert(BasicString<char>::kBufSize == 16, "narrow SSO buffer is 16 chars");
static_assert(BasicString<wchar_t>::kBufSize == 8, "wide SSO buffer is 8 chars (2-byte wchar_t)");

using String  = BasicString<char>;
using WString = BasicString<wchar_t>;

/// Receives a string written by the game, then converts it to a native one.
/// Scoped so the game's assignment target is a properly-initialised MSVC string.
template <class E>
class Receiver {
public:
    Receiver() { m_value.init_empty(); }

    void* address() { return &m_value; }
    std::basic_string<E> get() const { return m_value.to_std(); }

private:
    BasicString<E> m_value;
};

} // namespace MsvcInterop
