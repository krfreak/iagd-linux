#!/usr/bin/env bash
#
# Rebuilds the two patched third-party trees this port compiles against.
#
# Neither is committed here. Upstream Item Assistant and proton-injector are pinned as
# submodules, and this repository carries only the diffs against them — so what is published
# is the porting work, not somebody else's source. Run this once after cloning, and again
# whenever a submodule is moved to a newer commit.
#
#   scripts/prepare.sh            both trees
#   scripts/prepare.sh hook       just the hook sources
#   scripts/prepare.sh injector   just the injector
#
# Output (all generated, all gitignored):
#   linux-port/native/hook/generated/   upstream HookDll/Hook + patches/hook/*
#   build/proton-injector/              the injector + patches/proton-injector/*
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

HOOK_UPSTREAM="iagd/HookDll/Hook"
HOOK_OUT="linux-port/native/hook/generated"
INJECTOR_UPSTREAM="proton-injector"
INJECTOR_OUT="build/proton-injector"

die() { echo "error: $*" >&2; exit 1; }

# A submodule that was never initialised is an empty directory, which would otherwise fail
# much later as a confusing compile error.
require_submodule() {
    local path="$1" marker="$2"
    [ -e "$path/$marker" ] || die "$path is empty. Run: git submodule update --init --recursive"
}

# Patches are applied to a copy, never to the submodule, so `git status` stays clean and a
# second run is idempotent rather than an already-applied failure.
apply_patches() {
    local dir="$1" patches="$2" applied=0
    shopt -s nullglob
    for patch in "$patches"/*.patch; do
        git apply --directory="$dir" -p1 --whitespace=nowarn "$patch" \
            || die "$(basename "$patch") did not apply. Upstream has changed under it — see
       README.md 'Following upstream' for how to refresh the series."
        applied=$((applied + 1))
    done
    shopt -u nullglob
    echo "  $applied patch(es) applied"
}

prepare_hook() {
    require_submodule "$HOOK_UPSTREAM" dllmain.cpp
    echo "hook: $HOOK_UPSTREAM -> $HOOK_OUT"

    rm -rf "$HOOK_OUT"
    mkdir -p "$HOOK_OUT"
    # From the pinned commit rather than the working tree, so a submodule someone has been
    # poking at cannot quietly become the thing we compile — and so the patches always apply
    # to what they were made against.
    #
    # Sources only. The MSVC project files, the .rc and the solution are upstream's build
    # system, which this port replaces with native/hook/Makefile.
    git -C iagd archive HEAD "$(basename "$(dirname "$HOOK_UPSTREAM")")/$(basename "$HOOK_UPSTREAM")" \
        | tar -x -C "$HOOK_OUT" --strip-components=2 \
              --wildcards '*.cpp' '*.h'

    apply_patches "$HOOK_OUT" patches/hook
    echo "  $(ls "$HOOK_OUT" | wc -l) file(s) ready"
}

prepare_injector() {
    require_submodule "$INJECTOR_UPSTREAM" Makefile
    echo "injector: $INJECTOR_UPSTREAM -> $INJECTOR_OUT"

    rm -rf "$INJECTOR_OUT"
    mkdir -p "$INJECTOR_OUT"
    # The pinned commit, not the working tree: the tree may already carry the patch from an
    # earlier session, and applying it twice is the failure this whole scheme exists to avoid.
    git -C "$INJECTOR_UPSTREAM" archive HEAD | tar -x -C "$INJECTOR_OUT"

    apply_patches "$INJECTOR_OUT" patches/proton-injector
}

case "${1:-all}" in
    hook)     prepare_hook ;;
    injector) prepare_injector ;;
    all)      prepare_hook; prepare_injector ;;
    *)        die "usage: $0 [all|hook|injector]" ;;
esac
