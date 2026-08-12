# MinGW cross-build of the Item Assistant hook DLL.
#
# The prebuilt MSVC binary cannot initialise under Proton (see ../../PHASE0.md §9). This
# build exists to replace it, and to make the DLL debuggable and fixable from Linux.
#
# Static linking is deliberate: -static -static-libgcc -static-libstdc++ leaves the DLL
# depending only on core Win32 (kernel32/user32/shell32/ole32), with no MSVCP140,
# VCRUNTIME140 or UCRT api-set imports at all. The mingw canary that loads cleanly inside
# the live game is built the same way.

CXX     := x86_64-w64-mingw32-g++
CC      := x86_64-w64-mingw32-gcc
STRIP   := x86_64-w64-mingw32-strip

# Upstream's HookDll sources, unpacked from the pinned submodule and patched by
# ../../../scripts/prepare.sh. Generated, never committed — this repository carries the
# patches, not somebody else's source.
SRC_DIR    := generated
COMPAT_DIR := compat
VENDOR_DIR := vendor
BUILD_DIR  := build
BIN_DIR    := bin

TARGET := $(BIN_DIR)/ItemAssistantHook_x64.dll

INCLUDES := -I$(SRC_DIR) -I$(COMPAT_DIR) -I$(VENDOR_DIR) -I$(VENDOR_DIR)/minhook/include

WARNINGS := -Wall -Wno-unknown-pragmas -Wno-unused-variable -Wno-unused-function

CXXFLAGS := -std=c++17 -O2 $(WARNINGS) $(INCLUDES) \
            -DUNICODE -D_UNICODE -DWIN32_LEAN_AND_MEAN \
            -fno-strict-aliasing -fpermissive

CFLAGS   := -O2 -Wall $(INCLUDES)

# Hooks patch live game code, so the trampolines must not be reordered under us.
CXXFLAGS += -fno-omit-frame-pointer

# Header dependency tracking: the compat shims and GrimTypes.h change often.
CXXFLAGS += -MMD -MP
CFLAGS   += -MMD -MP

# TRACE=1 emits per-call tracing around the stash hooks, at WARNING level so each line is
# flushed to disk immediately. INFO lines sit in the ofstream buffer and are lost when the
# game dies, which makes the log's apparent last line misleading during a crash hunt.
TRACE ?= 0
ifeq ($(TRACE),1)
  CXXFLAGS += -DIAGD_TRACE_HOOKS
endif

LDFLAGS := -shared -static -static-libgcc -static-libstdc++ \
           -Wl,--exclude-all-symbols
LDLIBS  := -lshell32 -lole32 -luuid -luser32 -lkernel32 -ladvapi32

HOOK_SRCS   := $(wildcard $(SRC_DIR)/*.cpp)
COMPAT_SRCS := $(wildcard $(COMPAT_DIR)/*.cpp)
MINHOOK_SRCS := $(wildcard $(VENDOR_DIR)/minhook/src/*.c) \
                $(wildcard $(VENDOR_DIR)/minhook/src/hde/*.c)

OBJS := $(HOOK_SRCS:%.cpp=$(BUILD_DIR)/%.o) \
        $(COMPAT_SRCS:%.cpp=$(BUILD_DIR)/%.o) \
        $(MINHOOK_SRCS:%.c=$(BUILD_DIR)/%.o)

DEPS := $(OBJS:.o=.d)

# Before including them: a .d file's first rule would otherwise become the default goal on
# every build after the first, so a bare `make` would rebuild one object and stop.
.DEFAULT_GOAL := all

-include $(DEPS)

.PHONY: all clean deps

all: $(SRC_DIR) $(TARGET)

# A clone has no sources until prepare has run, and a missing-file error from the compiler
# would send the reader looking in the wrong place.
$(SRC_DIR):
	@echo "error: $(SRC_DIR)/ is missing. Run from the repository root:" >&2
	@echo "         scripts/prepare.sh hook" >&2
	@exit 1

$(TARGET): $(OBJS) | $(BIN_DIR)
	$(CXX) $(OBJS) -o $@ $(LDFLAGS) $(LDLIBS)
	$(STRIP) --strip-unneeded $@
	@echo "Build complete: $@ ($$(du -h $@ | cut -f1))"
	@echo -n "Imports: "; x86_64-w64-mingw32-objdump -p $@ | grep -c "DLL Name:" || true

$(BUILD_DIR)/%.o: %.cpp
	@mkdir -p $(dir $@)
	$(CXX) $(CXXFLAGS) -c $< -o $@

$(BUILD_DIR)/%.o: %.c
	@mkdir -p $(dir $@)
	$(CC) $(CFLAGS) -c $< -o $@

$(BIN_DIR):
	mkdir -p $(BIN_DIR)

# Verify the point of the static link: no MSVC C++ runtime.
#
# The UCRT api-set imports (api-ms-win-crt-*) are fine and are NOT checked: this
# toolchain targets UCRT, Wine resolves those api-sets internally, and the canary DLL that
# loads cleanly inside the live game imports them too. What broke the original binary was
# never the api-sets -- MSVCP140/VCRUNTIME140 are the C++ runtime that must not come back.
deps: $(TARGET)
	@echo "=== imported DLLs ==="
	@x86_64-w64-mingw32-objdump -p $(TARGET) | grep -i "DLL Name:" | sort -u
	@echo
	@if x86_64-w64-mingw32-objdump -p $(TARGET) | grep -qiE "msvcp|vcruntime"; then \
	    echo "FAIL: still importing the MSVC C++ runtime"; exit 1; \
	else \
	    echo "OK: no MSVCP140 / VCRUNTIME140 imports"; \
	fi

clean:
	rm -rf $(BUILD_DIR) $(BIN_DIR)
