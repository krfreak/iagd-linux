#!/usr/bin/env bash
# Builds a self-contained, relocatable package of the Linux port.
#
# Produces an AppDir, then an AppImage if appimagetool is available and a plain tarball either
# way. The tarball is not a consolation prize: it is what makes this testable without network
# access, and extracting it is a perfectly good way to run the thing.
#
#   ./packaging/build-appimage.sh              build into packaging/out
#   ./packaging/build-appimage.sh --skip-ui    reuse the existing web UI build
#
# ── Why AppImage and not Flatpak ─────────────────────────────────────────────────────────────
#
# This application cannot work inside a Flatpak sandbox, for two reasons that are properties of
# the sandbox rather than packaging details:
#
#   1. It finds the running game by scanning /proc for "Grim Dawn.exe". Flatpak gives an app its
#      own PID namespace with a fresh procfs, so host processes are invisible — measured here, a
#      sandbox sees 4 processes where the host sees 626. There is no permission that turns this
#      off; it is the point of the namespace.
#   2. It attaches the hook by executing Proton from the host, which then enters the game's own
#      pressure-vessel container.
#
# Both could be worked around with --filesystem=host plus flatpak-spawn --host for every
# subprocess, at which point the sandbox is decorative and the packaging is worse for it.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
WORKSPACE="$(dirname "$LINUX_PORT")"

OUT="$LINUX_PORT/packaging/out"
APPDIR="$OUT/iagd.AppDir"
# The date is the version for a local build, where nothing else names it. A release is built
# from a tag, and the tag is the better name — so the caller may override it.
VERSION="${VERSION:-$(date +%Y.%m.%d)}"
RID="linux-x64"

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
die() { echo "error: $*" >&2; exit 1; }

# ── prerequisites ────────────────────────────────────────────────────────────────────────────

HOOK_DLL="$LINUX_PORT/native/hook/bin/ItemAssistantHook_x64.dll"
INJECTOR="$WORKSPACE/build/proton-injector/bin/injector64.exe"

[ -f "$HOOK_DLL" ] || die "hook DLL not built. Run: make -C $LINUX_PORT/native/hook"
[ -f "$INJECTOR" ] || die "injector not built. Run: scripts/prepare.sh injector && make -C $WORKSPACE/build/proton-injector"

# ── build ────────────────────────────────────────────────────────────────────────────────────

if [ "${1:-}" != "--skip-ui" ]; then
    say "Building the web UI"
    make -C "$LINUX_PORT" ui
fi

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/iagd"

PROJECTS="src/IAGrim.App/IAGrim.App.csproj tools/iagd/iagd.csproj src/IAGrim.Host/IAGrim.Host.csproj"

# Restored on its own, before anything is published, because a restore that fails *inside*
# `dotnet publish` is unactionable: NuGet's task returns false without logging why, and all the
# build reports is "MSB4181: The RestoreTask task returned false but did not log an error" with
# no package, no source and no path. Standing it up as its own step at normal verbosity means
# the log names what it was fetching when it died.
#
# --disable-parallel for the same reason. Restoring one package at a time is slower and is what
# a starved machine can actually complete; the silent-failure shape above is what a CI runner
# produces when the parallel restore hits a resource limit rather than a NuGet error.
#
# IAGD_RESTORE_BINLOG_DIR turns on a binary log per project. It exists because the failure this
# is chasing logs nothing the console can show: NuGet reports every project as "Failed to
# restore (in 34 ms)" and then MSB4181, with no package, no path and no NU code, on a runner
# where the same restore from an empty package cache succeeds on this machine. A binlog records
# what the loggers dropped. Replay one with:
#
#     dotnet msbuild restore-IAGrim.App.binlog -v:diag
say "Restoring packages"
for project in $PROJECTS; do
    binlog=()
    if [ -n "${IAGD_RESTORE_BINLOG_DIR:-}" ]; then
        mkdir -p "$IAGD_RESTORE_BINLOG_DIR"
        binlog=("-bl:$IAGD_RESTORE_BINLOG_DIR/restore-$(basename "$project" .csproj).binlog")
    fi

    dotnet restore "$LINUX_PORT/$project" -r "$RID" -p:SelfContained=true \
        --disable-parallel --nologo -v "${IAGD_RESTORE_VERBOSITY:-normal}" "${binlog[@]}"
done

say "Publishing (self-contained, $RID)"
# Self-contained so the package does not require a .NET install. Trimming is deliberately off:
# the ARZ/ARC parsers and the SQLite driver reach for types reflectively, and a trimmed build
# fails at run time rather than at build time.
#
# Separate output directories because the desktop app and the CLI both publish an executable
# named "iagd" — sharing one directory silently leaves whichever was published last.
publish() {
    dotnet publish "$LINUX_PORT/$1" \
        -c Release -r "$RID" --self-contained true --no-restore \
        -p:PublishTrimmed=false -p:PublishSingleFile=false \
        -o "$APPDIR/usr/lib/iagd/$2" --nologo -v q
}

publish src/IAGrim.App/IAGrim.App.csproj app
publish tools/iagd/iagd.csproj cli
publish src/IAGrim.Host/IAGrim.Host.csproj host

# Three self-contained publishes means three copies of the .NET runtime — around 270 MB for
# what is really one runtime and three small assemblies. Identical files are replaced with hard
# links, which tar stores once and which cost nothing to extract.
say "Deduplicating the runtime"
python3 - "$APPDIR/usr/lib/iagd" <<'DEDUPE'
import hashlib, os, sys

root = sys.argv[1]
seen = {}
saved = links = 0

for directory, _, files in os.walk(root):
    for name in sorted(files):
        path = os.path.join(directory, name)
        if os.path.islink(path):
            continue
        info = os.stat(path)
        if info.st_size < 4096:          # not worth a link
            continue

        with open(path, "rb") as handle:
            digest = hashlib.sha256(handle.read()).hexdigest()
        key = (digest, info.st_size)

        original = seen.get(key)
        if original is None:
            seen[key] = path
            continue
        if os.stat(original).st_ino == info.st_ino:
            continue                      # already linked

        os.remove(path)
        os.link(original, path)
        links += 1
        saved += info.st_size

print(f"  {links} file(s) linked, {saved / 1024 / 1024:.0f} MiB saved")
DEDUPE

# ── stage the pieces that are not .NET ───────────────────────────────────────────────────────

say "Staging the hook, the injector and the scripts"
install -Dm644 "$HOOK_DLL"  "$APPDIR/usr/share/iagd/hook/$(basename "$HOOK_DLL")"
install -Dm755 "$INJECTOR"  "$APPDIR/usr/share/iagd/injector/$(basename "$INJECTOR")"
install -Dm755 "$LINUX_PORT/scripts/attach-gd.sh" "$APPDIR/usr/share/iagd/scripts/attach-gd.sh"
install -Dm644 "$LINUX_PORT/scripts/_discover.sh" "$APPDIR/usr/share/iagd/scripts/_discover.sh"

# ── entry points ─────────────────────────────────────────────────────────────────────────────

cat > "$APPDIR/usr/bin/iagd" <<'LAUNCHER'
#!/usr/bin/env bash
# Resolves the package root from this script's own location, so the package can be extracted
# anywhere.
HERE="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
export IAGD_HOOK_DLL="$(echo "$HERE"/usr/share/iagd/hook/*.dll)"
export IAGD_INJECTOR="$HERE/usr/share/iagd/injector/injector64.exe"
export IAGD_SCRIPTS="$HERE/usr/share/iagd/scripts"
exec "$HERE/usr/lib/iagd/app/iagd" "$@"
LAUNCHER

cat > "$APPDIR/usr/bin/iagd-cli" <<'LAUNCHER'
#!/usr/bin/env bash
HERE="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
export IAGD_HOOK_DLL="$(echo "$HERE"/usr/share/iagd/hook/*.dll)"
export IAGD_INJECTOR="$HERE/usr/share/iagd/injector/injector64.exe"
export IAGD_SCRIPTS="$HERE/usr/share/iagd/scripts"
# So `install-desktop` writes an entry that launches the app rather than this CLI: both
# executables are named "iagd" and it cannot tell them apart on its own.
export IAGD_APP_EXEC="$HERE/usr/bin/iagd"
exec "$HERE/usr/lib/iagd/cli/iagd" "$@"
LAUNCHER

cat > "$APPDIR/usr/bin/iagd-attach" <<'LAUNCHER'
#!/usr/bin/env bash
# Attaches the hook to a running Grim Dawn, using the copies inside this package.
HERE="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
export IAGD_HOOK_DLL="$(echo "$HERE"/usr/share/iagd/hook/*.dll)"
export IAGD_INJECTOR="$HERE/usr/share/iagd/injector/injector64.exe"
exec "$HERE/usr/share/iagd/scripts/attach-gd.sh" "$@"
LAUNCHER

cat > "$APPDIR/usr/bin/iagd-host" <<'LAUNCHER'
#!/usr/bin/env bash
# The API and loot importer without a window, for running headless.
HERE="$(cd "$(dirname "$(readlink -f "$0")")/../.." && pwd)"
export IAGD_HOOK_DLL="$(echo "$HERE"/usr/share/iagd/hook/*.dll)"
export IAGD_INJECTOR="$HERE/usr/share/iagd/injector/injector64.exe"
export IAGD_SCRIPTS="$HERE/usr/share/iagd/scripts"
exec "$HERE/usr/lib/iagd/host/iagd-host" "$@"
LAUNCHER

chmod +x "$APPDIR/usr/bin/"iagd "$APPDIR/usr/bin/"iagd-cli \
         "$APPDIR/usr/bin/"iagd-attach "$APPDIR/usr/bin/"iagd-host

# ── AppImage metadata ────────────────────────────────────────────────────────────────────────

# StartupWMClass has to match what the window actually identifies itself as — the Wayland app_id
# and the X11 WM_CLASS, both of which the app sets to "iagd" — and not the window's title. Get it
# wrong and the desktop cannot tie the window to this entry, which costs it the taskbar icon.
cat > "$APPDIR/iagd.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Item Assistant for Grim Dawn
Comment=Manage your Grim Dawn stash
Exec=iagd
Icon=iagd
Categories=Utility;
Terminal=false
StartupWMClass=iagd
DESKTOP

install -Dm644 "$APPDIR/iagd.desktop" "$APPDIR/usr/share/applications/iagd.desktop"
"$SCRIPT_DIR/make-icon.sh" "$APPDIR/iagd.png"
install -Dm644 "$APPDIR/iagd.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/iagd.png"

cat > "$APPDIR/AppRun" <<'APPRUN'
#!/usr/bin/env bash
# AppImage entry point. Everything the app needs from the package is located relative to
# $APPDIR, so the mount point can be anywhere.
export APPDIR="${APPDIR:-$(cd "$(dirname "$(readlink -f "$0")")" && pwd)}"
export PATH="$APPDIR/usr/bin:$PATH"

# WebKitGTK comes from the host: it is deeply tied to the system's graphics stack and
# bundling it reliably is not realistic. Say so clearly rather than crashing in the webview.
if ! ldconfig -p 2>/dev/null | grep -q 'libwebkit2gtk-4\.1'; then
    echo "error: libwebkit2gtk-4.1 is not installed." >&2
    echo "       Debian/Ubuntu: libwebkit2gtk-4.1-0   Arch: webkit2gtk-4.1   Fedora: webkit2gtk4.1" >&2
    echo "       The headless host still works: run '$APPDIR/usr/bin/iagd-cli' or 'iagd-host'." >&2
    exit 1
fi

exec "$APPDIR/usr/bin/iagd" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# ── outputs ──────────────────────────────────────────────────────────────────────────────────

say "Creating the tarball"
TARBALL="$OUT/iagd-$VERSION-$RID.tar.gz"
tar -C "$OUT" -czf "$TARBALL" "$(basename "$APPDIR")"
echo "  $TARBALL  ($(du -h "$TARBALL" | cut -f1))"

if command -v appimagetool >/dev/null 2>&1; then
    say "Creating the AppImage"
    ARCH=x86_64 appimagetool "$APPDIR" "$OUT/iagd-$VERSION-x86_64.AppImage"
    echo "  $OUT/iagd-$VERSION-x86_64.AppImage"
else
    say "appimagetool not installed — AppDir and tarball built, AppImage skipped"
    echo "  Install it and re-run, or:  ARCH=x86_64 appimagetool $APPDIR"
fi

say "Done"
echo "  Run from the AppDir:  $APPDIR/AppRun"
echo "  Add to the app menu:  $APPDIR/usr/bin/iagd-cli install-desktop"
echo "  Or the CLI:           $APPDIR/usr/bin/iagd-cli status"
