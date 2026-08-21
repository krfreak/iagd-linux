# Item Assistant for Grim Dawn — Linux port.
#
#   make          build everything (web UI, then .NET)
#   make run      build and open the app
#   make verify   every upstream-parity check
#   make doctor   build the collection diagnostic (iagd-dbdoctor)
#
# The web UI must build before the .NET projects: vite writes into
# src/IAGrim.Host/wwwroot, which the host project embeds as content.

WEBUI  := src/WebUI
APP    := src/IAGrim.App/IAGrim.App.csproj
HOST   := src/IAGrim.Host/IAGrim.Host.csproj
CLI    := tools/iagd/iagd.csproj
DOCTOR := tools/dbdoctor/DbDoctor.csproj

.PHONY: all ui help favicon icon app cli doctor run host test verify package clean

all: ui app cli

ui: $(WEBUI)/node_modules help favicon
	cd $(WEBUI) && npm run build

## The browser tab's icon, from the same source as the application's. Running the host and
## opening a browser is a supported way to use this, so that tab should not be blank.
favicon: $(WEBUI)/public/favicon.png
$(WEBUI)/public/favicon.png: packaging/make-icon.sh
	@./packaging/make-icon.sh $@ 64

## The help page, read out of upstream's pinned submodule rather than copied into this
## repository — the same rule the hook and the injector follow. See scripts/extract-help.py.
help:
	@python3 scripts/extract-help.py \
		../iagd/WebUI/src/containers/help/Help.tsx \
		$(WEBUI)/help-notes.json \
		$(WEBUI)/src/generated/help.json \
		$(WEBUI)/public/help

$(WEBUI)/node_modules: $(WEBUI)/package.json
	cd $(WEBUI) && npm install
	@touch $@

# Generated rather than committed: it is a few shapes, and a script can be read and changed in
# a way a binary cannot. Regenerated only when the generator changes.
src/IAGrim.App/assets/iagd.png: packaging/make-icon.sh
	@./packaging/make-icon.sh $@ 256

icon: src/IAGrim.App/assets/iagd.png

app: ui icon
	dotnet build $(APP) -v q --nologo

cli:
	dotnet build $(CLI) -v q --nologo

# Reads a collection and reports what is wrong with it — unreadable items, lost deletions,
# duplicates. Not part of `all`: it is for looking at a database somebody sent in, not for
# running the app. Opens the file read-only.
doctor:
	dotnet build $(DOCTOR) -v q --nologo

# The desktop app: local host plus a window onto it.
run: ui icon
	dotnet run --project $(APP)

# Headless: same API, no window. Point a browser at http://127.0.0.1:5680/.
host: ui
	dotnet run --project $(HOST)

# The cloud tests need a real backup server on loopback; it is built on first run and never
# points at api.iagd.evilsoft.net (see tests/IAGrim.Cloud.Tests). The platform, core and host
# tests need nothing and run in milliseconds, so they go first — a broken attach pace or a
# mis-composed item name should not wait behind a Go build.
test:
	dotnet test $(PLATFORM_TESTS) -v q --nologo
	dotnet test $(CORE_TESTS) -v q --nologo
	dotnet test $(HOST_TESTS) -v q --nologo
	@./scripts/build-sync-server.sh >/dev/null
	dotnet test $(TESTS) -v q --nologo

PLATFORM_TESTS := tests/IAGrim.Platform.Tests/IAGrim.Platform.Tests.csproj
CORE_TESTS := tests/IAGrim.Core.Tests/IAGrim.Core.Tests.csproj
HOST_TESTS := tests/IAGrim.Host.Tests/IAGrim.Host.Tests.csproj
TESTS := tests/IAGrim.Cloud.Tests/IAGrim.Cloud.Tests.csproj

# Everything that asserts we still match upstream. Run after pulling upstream.
verify:
	@./scripts/check-upstream.sh      || true
	@./scripts/verify-stat-filter.sh
	@./scripts/verify-item-rarity.sh
	@bash ./scripts/verify-filter-groups.sh
	@bash ./scripts/verify-slot-filters.sh
	@bash ./scripts/verify-item-admission.sh
	@bash ./scripts/verify-schema.sh
	@bash ./scripts/verify-search-filters.sh
	@bash ./scripts/verify-stat-rendering.sh
	@bash ./scripts/verify-help.sh
	@bash ./scripts/verify-cloud-protocol.sh

# A relocatable, self-contained package: AppDir + tarball, and an AppImage when appimagetool
# is installed. See packaging/build-appimage.sh for why this is not a Flatpak.
package:
	@./packaging/build-appimage.sh

clean:
	rm -rf src/*/bin src/*/obj tools/*/bin tools/*/obj
	rm -rf src/IAGrim.Host/wwwroot $(WEBUI)/node_modules packaging/out
