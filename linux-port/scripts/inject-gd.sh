#!/usr/bin/env bash
# DEPRECATED — use attach-gd.sh instead.
#
# Launch-time injection does not work for Grim Dawn, for two independent reasons
# (PHASE0.md §7 and §8):
#   1. The hook DLL crashes if it initialises before the game has loaded game.dll.
#   2. Launching outside Steam breaks Steam API auth ("Steamworks Error! No SteamUtils011").
#
# Kept only as a fallback for experimenting with SLEEP-delayed injection.
#
# Phase 0 probe launcher: start Grim Dawn under Proton with the IA hook DLL injected.
#
# A thin, Grim-Dawn-specific front end for proton-injector/scripts/inject.sh that fills in
# the things that are easy to get wrong:
#
#   * targets x64/Grim Dawn.exe — the root "Grim Dawn.exe" is 32-bit and IA does not
#     support it (see DllInjector/InjectionHelper.cs, INJECTION_ERROR_32BIT)
#   * reuses the Proton build the prefix was actually created with, read from
#     compatdata/219990/config_info, rather than guessing
#   * uses our MinGW hook build from native/hook/bin/
#
# Usage:
#   ./inject-gd.sh                       # inject and launch
#   ./inject-gd.sh --dry-run             # resolve and print everything, launch nothing
#   ./inject-gd.sh --method apc          # any extra args pass through to the injector
#   DLL=/path/to/other.dll ./inject-gd.sh
#
# Run tools/probe with --setup first, or the DLL will not enter Wine mode.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
WORKSPACE="$(dirname "$LINUX_PORT")"

APPID="${APPID:-219990}"
INJECT_SH="$WORKSPACE/build/proton-injector/scripts/inject.sh"
DLL="${DLL:-$LINUX_PORT/native/hook/bin/ItemAssistantHook_x64.dll}"

DRY_RUN=0
ARGS=()
for arg in "$@"; do
    if [ "$arg" = "--dry-run" ]; then DRY_RUN=1; else ARGS+=("$arg"); fi
done
set -- ${ARGS[@]+"${ARGS[@]}"}

die() { echo "error: $*" >&2; exit 1; }

[ -f "$INJECT_SH" ] || die "proton-injector not prepared. Run: scripts/prepare.sh injector"
[ -f "$WORKSPACE/build/proton-injector/bin/injector64.exe" ] || \
    die "injector not built. Run: make -C $WORKSPACE/build/proton-injector"
[ -f "$DLL" ] || die "hook DLL not found: $DLL"

# ---------------------------------------------------------------- steam library

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

GAME_DIR=""
COMPAT_DATA=""
for lib in "${LIBRARIES[@]}"; do
    [ -z "$GAME_DIR" ] && [ -d "$lib/steamapps/common/Grim Dawn" ] && \
        GAME_DIR="$lib/steamapps/common/Grim Dawn"
    [ -z "$COMPAT_DATA" ] && [ -d "$lib/steamapps/compatdata/$APPID" ] && \
        COMPAT_DATA="$lib/steamapps/compatdata/$APPID"
done

[ -n "$GAME_DIR" ]    || die "Grim Dawn install not found in any Steam library"
[ -n "$COMPAT_DATA" ] || die "no Proton prefix for appid $APPID — launch the game through Steam once first"

TARGET="$GAME_DIR/x64/Grim Dawn.exe"
[ -f "$TARGET" ] || die "64-bit executable not found: $TARGET"

# ---------------------------------------------------------------------- proton
# Match the Proton the prefix was built with. Running a prefix under a different Proton
# than it was created with can force an upgrade or subtly misbehave.

resolve_proton() {
    if [ -n "${PROTON_PATH:-}" ]; then echo "$PROTON_PATH"; return; fi

    local info="$COMPAT_DATA/config_info"
    local name="" dir=""

    if [ -f "$info" ]; then
        name="$(head -1 "$info")"
        # line 2 looks like <protondir>/files/share/fonts/
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

# ----------------------------------------------------------------- bridge check

BRIDGE="$COMPAT_DATA/pfx/drive_c/users/steamuser/AppData/Local/EvilSoft/IAGD"
if ! grep -qs '"isRunningInWine"[[:space:]]*:[[:space:]]*true' "$BRIDGE/settings.json"; then
    echo "warning: Wine mode is not enabled in the bridge settings.json." >&2
    echo "         The DLL will use WM_COPYDATA and the probe will see nothing." >&2
    echo "         Run:  dotnet run --project $LINUX_PORT/tools/probe -- --setup" >&2
    echo >&2
fi

# ---------------------------------------------------------------------- launch

cat <<EOF
Grim Dawn + IA hook injection
─────────────────────────────────────────────────────────────────────────────
  App ID    $APPID
  Game      $TARGET
  Prefix    $COMPAT_DATA
  Proton    $PROTON
  DLL       $DLL
  Bridge    $BRIDGE
─────────────────────────────────────────────────────────────────────────────

EOF

if [ "$DRY_RUN" -eq 1 ]; then
    echo "dry run — would exec:"
    echo "  APPID=$APPID PROTON_PATH=$PROTON \\"
    echo "  $INJECT_SH \\"
    echo "      '$TARGET' \\"
    echo "      '$DLL'" "$@"
    exit 0
fi

export APPID
export PROTON_PATH="$PROTON"
export STEAM_COMPAT_DATA_PATH="$COMPAT_DATA"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT"

exec "$INJECT_SH" "$TARGET" "$DLL" "$@"
