#!/usr/bin/env bash
# Shared discovery for the Phase 0 scripts. Sourced, not executed.
#
# Exports: STEAM_ROOT GAME_DIR COMPAT_DATA TARGET PROTON BRIDGE
# Provides: die()
#
# IAGD_GAME_DIR and IAGD_COMPAT_DATA override the search below. The host sets both from what it
# resolved, which is how a game folder or a Proton prefix set by hand in Settings reaches the
# injector: without them this rediscovers from scratch and dies on exactly the layouts those
# settings exist for, leaving a client that captures loot and can never attach to collect it.

die() { echo "error: $*" >&2; exit 1; }

APPID="${APPID:-219990}"

find_steam_root() {
    for c in "$HOME/.local/share/Steam" "$HOME/.steam/steam" \
             "$HOME/.steam/debian-installation" \
             "$HOME/.var/app/com.valvesoftware.Steam/data/Steam"; do
        [ -d "$c/steamapps" ] && { echo "$c"; return; }
    done
    die "no Steam installation found"
}

STEAM_ROOT="$(find_steam_root)"

LIBRARIES=("$STEAM_ROOT")
LIB_VDF="$STEAM_ROOT/steamapps/libraryfolders.vdf"
if [ -f "$LIB_VDF" ]; then
    while IFS= read -r line; do
        [[ $line =~ \"path\"[[:space:]]+\"([^\"]+)\" ]] && LIBRARIES+=("${BASH_REMATCH[1]}")
    done < "$LIB_VDF"
fi

GAME_DIR="${IAGD_GAME_DIR:-}"
COMPAT_DATA="${IAGD_COMPAT_DATA:-}"

# A prefix may be named either way round — the compatdata folder or the pfx inside it — since
# both are called "the prefix". Everything below wants the compatdata folder.
if [ -n "$COMPAT_DATA" ] && [ ! -d "$COMPAT_DATA/pfx" ] && [ -d "$COMPAT_DATA/drive_c" ]; then
    COMPAT_DATA="$(dirname "$COMPAT_DATA")"
fi

for lib in "${LIBRARIES[@]}"; do
    [ -z "$GAME_DIR" ] && [ -d "$lib/steamapps/common/Grim Dawn" ] && \
        GAME_DIR="$lib/steamapps/common/Grim Dawn"
    [ -z "$COMPAT_DATA" ] && [ -d "$lib/steamapps/compatdata/$APPID" ] && \
        COMPAT_DATA="$lib/steamapps/compatdata/$APPID"
done

[ -n "$GAME_DIR" ]    || die "Grim Dawn install not found in any Steam library"
[ -n "$COMPAT_DATA" ] || die "no Proton prefix for appid $APPID — launch the game through Steam once first"

# The root "Grim Dawn.exe" is 32-bit; IA supports only x64.
TARGET="$GAME_DIR/x64/Grim Dawn.exe"
[ -f "$TARGET" ] || die "64-bit executable not found: $TARGET"

# Match the Proton the prefix was built with — running a prefix under a different build
# can force an upgrade or subtly misbehave.
resolve_proton() {
    if [ -n "${PROTON_PATH:-}" ]; then echo "$PROTON_PATH"; return; fi

    local info="$COMPAT_DATA/config_info" name="" dir=""

    if [ -f "$info" ]; then
        name="$(head -1 "$info")"
        dir="$(sed -n '2p' "$info" | sed 's#/files/share/fonts/*$##')"
        if [ -n "$dir" ] && [ -x "$dir/proton" ]; then echo "$dir/proton"; return; fi
    fi

    if [ -n "$name" ]; then
        for base in "$HOME/.steam/root/compatibilitytools.d" \
                    "$STEAM_ROOT/compatibilitytools.d" \
                    "${LIBRARIES[@]/%//steamapps/common}"; do
            [ -x "$base/$name/proton" ] && { echo "$base/$name/proton"; return; }
        done
    fi

    die "could not resolve Proton for this prefix (config_info says '${name:-unknown}'). Set PROTON_PATH."
}

PROTON="$(resolve_proton)"
[ -x "$PROTON" ] || die "Proton not executable: $PROTON"

BRIDGE="$COMPAT_DATA/pfx/drive_c/users/steamuser/AppData/Local/EvilSoft/IAGD"

warn_if_wine_mode_disabled() {
    if ! grep -qs '"isRunningInWine"[[:space:]]*:[[:space:]]*true' "$BRIDGE/settings.json"; then
        echo "warning: Wine mode is not enabled in the bridge settings.json." >&2
        echo "         The DLL will use WM_COPYDATA and the probe will see nothing." >&2
        echo "         Run:  dotnet run --project tools/probe -- --setup" >&2
        echo >&2
    fi
}

to_windows_path() { echo "Z:$(realpath "$1" | sed 's#/#\\#g')"; }

# Pids of the running Grim Dawn, and of nothing else.
#
# `pgrep -f "Grim Dawn.exe"` on its own is not that. We launch the injector as
#   proton run injector64.exe ... --attach-window "Grim Dawn" --attach-name "Grim Dawn.exe"
# so while an attach is in flight three processes carry the game's name in their command line:
# the Proton wrapper, wine's steam.exe, and the injector. Measured with no game running:
# zero matches before an attach, three during it.
#
# Same rule as GameProcess.IsGameCommandLine() in IAGrim.Platform; the two must agree.
game_pids() {
    local p cmd
    for p in $(pgrep -f "Grim Dawn.exe" 2>/dev/null); do
        cmd="$(tr '\0' ' ' < "/proc/$p/cmdline" 2>/dev/null)" || continue
        case "$cmd" in
            *injector64.exe*|*--attach-name*|*--attach-window*) continue ;;
        esac
        echo "$p"
    done
}

# Earliest start time (epoch seconds) of any running Grim Dawn process, or empty if none.
# Used to decide whether a hook marker belongs to the CURRENT game session: markers record
# a Wine pid, which is meaningless to Linux, so age relative to the game is the only
# reliable signal.
game_start_epoch() {
    local now oldest="" secs
    now=$(date +%s)
    for p in $(game_pids); do
        secs=$(ps -o etimes= -p "$p" 2>/dev/null | tr -d ' ')
        [ -z "$secs" ] && continue
        local started=$(( now - secs ))
        if [ -z "$oldest" ] || [ "$started" -lt "$oldest" ]; then oldest=$started; fi
    done
    echo "$oldest"
}
