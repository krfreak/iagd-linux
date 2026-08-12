// Shim: the hook uses only mutex/lock_guard from boost::thread.
#pragma once
#include <mutex>
#include <thread>
namespace boost {
using mutex  = std::mutex;
using thread = std::thread;
template <class T> using lock_guard = std::lock_guard<T>;
}
