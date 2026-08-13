#!/usr/bin/env bash
# Inject the IA hook DLL into an ALREADY-RUNNING Grim Dawn.
#
# This is the mechanism the port actually needs. Launch-time injection does not work, for
# two independent reasons (see PHASE0.md §7 and §8):
#
#   1. The hook DLL crashes if it initialises before Grim Dawn has loaded game.dll.
#   2. Grim Dawn's Steam API cannot authenticate when launched outside Steam
#      ("Steamworks Error! No SteamUtils011").
#
# Both disappear if Steam launches the game normally and we attach afterwards.
#
# Usage:
#   1. Launch Grim Dawn from Steam as usual, and load a character.
#   2. ./attach-gd.sh
#
#   ./attach-gd.sh --dry-run          resolve and print, do nothing
#   ./attach-gd.sh --method apc       pass through to the injector
#   PROCESS_NAME="Grim Dawn.exe" ./attach-gd.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
WORKSPACE="$(dirname "$LINUX_PORT")"

# shellcheck source=_discover.sh
source "$SCRIPT_DIR/_discover.sh"

# Where the injector and hook live.
#
# In a source checkout the injector is built from the pinned submodule plus our patch, into
# build/ at the repository root (scripts/prepare.sh); in a package it is staged next to the
# scripts. The environment variables let a package point at its own copies without this script
# having to know which layout it is in.
INJECTOR="${IAGD_INJECTOR:-$WORKSPACE/build/proton-injector/bin/injector64.exe}"

# The hook is our MinGW build from native/hook, which is the only one that initialises under
# Proton — the prebuilt MSVC binaries from the upstream installer crash during static
# initialisation (PHASE0.md §9). They were kept here for A/B comparison while the port was
# being written; that comparison is finished, and this repository does not redistribute them.
#
# IAGD_HOOK_DLL lets a package supply its own copy; the checkout path is the fallback.
DLL="${DLL:-${IAGD_HOOK_DLL:-$LINUX_PORT/native/hook/bin/ItemAssistantHook_x64.dll}}"
[ -f "$DLL" ] || die "hook DLL not built. Run: make -C $LINUX_PORT/native/hook"
PROCESS_NAME="${PROCESS_NAME:-Grim Dawn.exe}"

# Inject into the owner of this window, not into whatever has the right image name.
#
# This is how the Windows tool picks its target: FindWindowEx with "Grim Dawn" as the window
# class, then GetWindowThreadProcessId (DllInjector/InjectionHelper.cs). The distinction is the
# whole difference between attaching cleanly and hammering a game that is still starting: the
# process exists within a second of launch, the window only once the loader has finished and the
# engine is up. Targeting the process meant every attach attempt fired a LoadLibraryA remote
# thread into a half-initialised game, over and over, which is as bad for the game as it sounds.
WINDOW_NAME="${WINDOW_NAME:-Grim Dawn}"

RETRY_MS="${RETRY_MS:-5000}"
ATTACH_TIMEOUT_MS="${ATTACH_TIMEOUT_MS:-0}"     # 0 = wait forever
LOG_FILE="$WORKSPACE/build/proton-injector/injector.log"

DRY_RUN=0
ARGS=()
for arg in "$@"; do
    if [ "$arg" = "--dry-run" ]; then DRY_RUN=1; else ARGS+=("$arg"); fi
done
set -- ${ARGS[@]+"${ARGS[@]}"}

[ -f "$INJECTOR" ] || die "injector not built. Run: scripts/prepare.sh injector && make -C $WORKSPACE/build/proton-injector"
[ -f "$DLL" ]      || die "hook DLL not found: $DLL"

warn_if_wine_mode_disabled

if ! pgrep -f "Grim Dawn.exe" >/dev/null 2>&1; then
    echo "warning: no Grim Dawn process visible from Linux right now." >&2
    echo "         The injector will wait for one to appear." >&2
    echo >&2
fi

# Stage the DLL inside the prefix and inject it by C: path.
#
# The game runs inside a pressure-vessel container with its own mount namespace, and
# LoadLibrary executes in the game's process, with the game's filesystem view. A C: path
# resolves through the prefix itself and is independent of how the host tree happens to be
# mounted into that container. It is also what upstream does (C:\Program Files\IAGD\).
STAGE_DIR="$COMPAT_DATA/pfx/drive_c/iagd"
mkdir -p "$STAGE_DIR"
cp -f "$DLL" "$STAGE_DIR/" || die "could not stage the DLL into the prefix"
STAGED_DLL="$STAGE_DIR/$(basename "$DLL")"

WIN_INJECTOR="$(to_windows_path "$INJECTOR")"
WIN_DLL="C:\\iagd\\$(basename "$DLL")"
WIN_LOG="$(to_windows_path "$LOG_FILE")"

cat <<EOF
Attach IA hook to a running Grim Dawn
─────────────────────────────────────────────────────────────────────────────
  Window    $WINDOW_NAME  (class), owned by $PROCESS_NAME
  Prefix    $COMPAT_DATA
  Proton    $PROTON
  DLL       $DLL
  staged →  $STAGED_DLL
  injected  $WIN_DLL
  Bridge    $BRIDGE
  Retry     every ${RETRY_MS} ms until the game accepts the attach
  Log       $LOG_FILE
─────────────────────────────────────────────────────────────────────────────

EOF

# Both: the window says *when* the game is ready, the image name says *which* process that
# window belongs to. Inside the prefix Steam runs too, and its windows carry the game's name.
CMD=("$PROTON" run "$WIN_INJECTOR" "$WIN_DLL"
     --attach-window "$WINDOW_NAME"
     --attach-name "$PROCESS_NAME"
     --attach-retry "$RETRY_MS"
     --attach-timeout "$ATTACH_TIMEOUT_MS"
     --log-file "$WIN_LOG" "$@")

if [ "$DRY_RUN" -eq 1 ]; then
    echo "dry run — would exec:"
    printf '  STEAM_COMPAT_DATA_PATH=%s \\\n' "$COMPAT_DATA"
    printf '  %q ' "${CMD[@]}"
    echo
    exit 0
fi

export STEAM_COMPAT_DATA_PATH="$COMPAT_DATA"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT"

# PRE-FLIGHT: refuse to inject if the hook is already live in this game session.
#
# This is not politeness, it prevents a crash. LoadLibrary only dedupes by module path, so
# injecting the same code under a second filename loads a SECOND full copy of the hook into
# the game: two sets of MinHook patches over the same functions (the later one patching the
# earlier one's trampolines), two worker threads, two seed-info threads, all writing the
# same files. That reliably takes the game down, and it is exactly what happens if the DLL
# gets renamed between runs.
PID_BEFORE="$(ls "$BRIDGE/linuxhack/"*.PID 2>/dev/null | head -1 || true)"
# An abort marker counts as a marker too. Sweeping only when a .PID existed left an old
# .ABORTED lying around for ever, and the verdict at the end of this script would then report
# "the DLL refused to attach" for a run that never injected anything.
ABORTED_BEFORE="$(ls "$BRIDGE/linuxhack/"*.ABORTED 2>/dev/null | head -1 || true)"

# A marker left behind by a previous session is stale, and a stale marker would otherwise
# block every future attach. The pid inside it is a *Wine* pid and means nothing to Linux,
# so staleness is decided two ways:
#
#   * no Grim Dawn process at all -> nothing can be hooked, every marker is stale
#   * the marker predates the running game -> it belongs to an earlier session
#
# The second case is the one that matters after a crash and restart.
if [ -n "$PID_BEFORE" ] || [ -n "$ABORTED_BEFORE" ]; then
    GAME_STARTED="$(game_start_epoch)"
    MARKER_MTIME="$(stat -c %Y "${PID_BEFORE:-$ABORTED_BEFORE}" 2>/dev/null || echo 0)"

    if [ -z "$GAME_STARTED" ]; then
        echo "Clearing stale hook markers (no Grim Dawn process is running)."
        rm -f "$BRIDGE/linuxhack/"*.PID "$BRIDGE/linuxhack/"*.ABORTED
        PID_BEFORE=""
        echo
    elif [ "$MARKER_MTIME" -lt "$GAME_STARTED" ]; then
        echo "Clearing stale hook markers: the marker is from $(date -d "@$MARKER_MTIME" +%H:%M:%S)"
        echo "but the running game started at $(date -d "@$GAME_STARTED" +%H:%M:%S)."
        rm -f "$BRIDGE/linuxhack/"*.PID "$BRIDGE/linuxhack/"*.ABORTED
        PID_BEFORE=""
        echo
    fi
fi

if [ -n "$PID_BEFORE" ] && [ "${FORCE:-0}" != "1" ]; then
    echo "The hook is already live in this game session (wine pid $(basename "$PID_BEFORE" .PID))."
    echo
    echo "  Nothing to do: the DLL stays loaded for the lifetime of the game. Injecting"
    echo "  again risks loading a second copy, which crashes Grim Dawn."
    echo
    echo "  To watch the bridge:   dotnet run --project tools/probe"
    echo "  If the game was restarted and this marker is stale, delete it:"
    echo "      rm '$PID_BEFORE'"
    echo "  Override anyway (not recommended):  FORCE=1 $0"
    exit 0
fi

echo "Waiting for the '$WINDOW_NAME' window. Nothing is injected until it exists, and the DLL"
echo "still refuses to attach at character select, so this may retry. Ctrl+C to stop."
echo

# Only markers written from here on describe this run.
RUN_STARTED="$(date +%s)"

set +e
"${CMD[@]}"
INJECTOR_RC=$?
set -e

echo
# The injector is linked as a GUI binary so it cannot pop a console window over the game, which
# also means it prints nothing to a terminal. Its log is the only account of what happened.
if [ -f "$LOG_FILE" ]; then
    echo "Injector log (tail):"
    tail -6 "$LOG_FILE" | sed 's/^/  /'
    echo
fi

echo "─────────────────────────────────────────────────────────────────────────────"

# The injector only reports that LoadLibrary succeeded. The authoritative signal that the
# hook actually initialised is the .PID marker the DLL writes itself.
PID_FILE="$(ls "$BRIDGE/linuxhack/"*.PID 2>/dev/null | head -1 || true)"

# An abort marker is only this run's if it was written during it. Otherwise a leftover from an
# earlier attempt reports a refusal that never happened.
ABORTED="$(ls "$BRIDGE/linuxhack/"*.ABORTED 2>/dev/null | head -1 || true)"
if [ -n "$ABORTED" ] && [ "$(stat -c %Y "$ABORTED" 2>/dev/null || echo 0)" -lt "$RUN_STARTED" ]; then
    ABORTED=""
fi

if [ -n "$PID_FILE" ]; then
    if [ -n "$PID_BEFORE" ] && [ "$PID_BEFORE" = "$PID_FILE" ]; then
        echo "ALREADY HOOKED  (wine pid $(basename "$PID_FILE" .PID))"
        echo
        echo "  The DLL was already loaded before this run, so nothing changed. That is fine:"
        echo "  it stays loaded for the lifetime of the game."
    else
        echo "SUCCESS  hook is live (wine pid $(basename "$PID_FILE" .PID))"
    fi
    echo
    echo "  The injector exits here by design -- the DLL is loaded into Grim Dawn and stays"
    echo "  there. This window closing is not an error."
    echo
    echo "  Next: watch the bridge with"
    echo "      dotnet run --project tools/probe"
    echo "  then open your stash in game."
elif grep -q "NOT READY" "$LOG_FILE" 2>/dev/null && [ "$(stat -c %Y "$LOG_FILE" 2>/dev/null || echo 0)" -ge "$RUN_STARTED" ]; then
    echo "NOT READY  the game window has not appeared yet"
    echo
    echo "  Nothing was injected. The process exists long before it can accept a DLL, so the"
    echo "  window is what this waits for. Try again once the game has drawn one."
elif [ -n "$ABORTED" ]; then
    echo "ABORTED  the DLL loaded but refused to attach"
    echo
    echo "  Expected while the game is loading or in character select. Load a character"
    echo "  and run this again."
else
    echo "FAILED  no .PID marker was written (injector exit $INJECTOR_RC)"
    echo
    echo "  The DLL did not initialise. Check:"
    echo "      $LOG_FILE"
    echo "      $BRIDGE/iagd_hook.log"
    echo "  and try a different method, e.g. --method apc"
fi
echo "─────────────────────────────────────────────────────────────────────────────"

exit $INJECTOR_RC
