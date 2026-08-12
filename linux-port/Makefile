# Item Assistant for Grim Dawn — Linux port.
#
#   make          build everything (web UI, then .NET)
#   make run      build and open the app
#   make verify   every upstream-parity check
#
# The web UI must build before the .NET projects: vite writes into
# src/IAGrim.Host/wwwroot, which the host project embeds as content.

WEBUI  := src/WebUI
APP    := src/IAGrim.App/IAGrim.App.csproj
HOST   := src/IAGrim.Host/IAGrim.Host.csproj
CLI    := tools/iagd/iagd.csproj

.PHONY: all ui icon app cli run host verify package clean

all: ui app cli

ui: $(WEBUI)/node_modules
	cd $(WEBUI) && npm run build

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

# The desktop app: local host plus a window onto it.
run: ui icon
	dotnet run --project $(APP)

# Headless: same API, no window. Point a browser at http://127.0.0.1:5680/.
host: ui
	dotnet run --project $(HOST)

# Everything that asserts we still match upstream. Run after pulling upstream.
verify:
	@./scripts/check-upstream.sh      || true
	@./scripts/verify-stat-filter.sh
	@./scripts/verify-item-rarity.sh
	@bash ./scripts/verify-filter-groups.sh
	@bash ./scripts/verify-slot-filters.sh
	@bash ./scripts/verify-schema.sh

# A relocatable, self-contained package: AppDir + tarball, and an AppImage when appimagetool
# is installed. See packaging/build-appimage.sh for why this is not a Flatpak.
package:
	@./packaging/build-appimage.sh

clean:
	rm -rf src/*/bin src/*/obj tools/*/bin tools/*/obj
	rm -rf src/IAGrim.Host/wwwroot $(WEBUI)/node_modules packaging/out
