# Top-level build.
#
# Two things have to exist before the port itself builds: the hook DLL that runs inside Grim
# Dawn, and the injector that gets it there. Both come from pinned submodules with this
# repository's patches applied — see THIRD-PARTY.md — so `prepare` is a real step rather than
# a formality, and every target that needs those trees depends on it.

PORT      := linux-port
HOOK_DIR  := $(PORT)/native/hook
INJ_DIR   := build/proton-injector

HOOK      := $(HOOK_DIR)/bin/ItemAssistantHook_x64.dll
INJECTOR  := $(INJ_DIR)/bin/injector64.exe

.PHONY: all prepare hook injector native app ui cli run host verify package clean distclean

## Everything a working install needs: the native pieces and the .NET app.
all: native
	$(MAKE) -C $(PORT) all

## Unpack both pinned submodules and apply our patches.
prepare:
	scripts/prepare.sh

native: hook injector

hook: $(HOOK)
$(HOOK): $(wildcard patches/hook/*.patch) $(wildcard $(HOOK_DIR)/src/*) $(HOOK_DIR)/Makefile
	scripts/prepare.sh hook
	$(MAKE) -C $(HOOK_DIR)

injector: $(INJECTOR)
$(INJECTOR): $(wildcard patches/proton-injector/*.patch)
	scripts/prepare.sh injector
	$(MAKE) -C $(INJ_DIR)

# The port's own targets, forwarded so the whole thing is drivable from the root.
app ui cli run host verify:
	$(MAKE) -C $(PORT) $@

## The AppImage, which stages the hook and the injector alongside the app.
package: native
	$(MAKE) -C $(PORT) package

clean:
	$(MAKE) -C $(PORT) clean
	-$(MAKE) -C $(HOOK_DIR) clean
	rm -rf build

## Also throws away the generated third-party trees; `prepare` rebuilds them.
distclean: clean
	rm -rf $(HOOK_DIR)/generated $(HOOK_DIR)/bin
