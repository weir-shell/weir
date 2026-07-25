#!/usr/bin/env bash
# The ONE freshness gate [D:masking-mechanized] — the binary's build
# stamp must equal git HEAD AND no source .fs may be newer than it.
# Every binary consumer runs this (shell scripts exec it; harness.py
# subprocesses it) so the guard has a SINGLE implementation that cannot
# drift from its own documentation. The fuzzer's Runner.fs is the one
# F#-native twin (shelling out from the test runtime is a worse
# dependency) — kept in step deliberately, cross-referenced here.
#
# Dirty builds: a `-dirty` stamp is USABLE for local iteration (warn,
# continue) but must never pass in CI (hard fail) — a shipped artifact
# is not allowed to carry uncommitted source.
#
# The ONE window this gate does NOT close: a republish DURING a live run
# (the binary is replaced mid-run, so a per-case check still sees a
# matching stamp while the bytes changed underfoot — a deep fuzz run
# comparing P and T(P) across two builds is the known case). This gate
# is a start-of-run check, not a per-case one; until it is a lockfile
# around publish, the standing rule holds: never republish while a
# deep run is live. Stated here so there is one boundary, not two
# half-known ones.
set -euo pipefail

BIN="${1:-${WEIR_BIN:-$HOME/.local/bin/weir}}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ ! -f "$BIN" ]; then
    echo "STALE BINARY: no weir binary at $BIN — build with ./publish.sh" >&2
    exit 1
fi

# stamp gate (skipped outside a git checkout — e.g. a release tarball,
# where the mtime gate below still applies)
if command -v git >/dev/null 2>&1 && git -C "$repo_root" rev-parse --short HEAD >/dev/null 2>&1; then
    head_hash=$(git -C "$repo_root" rev-parse --short HEAD)
    stamp=$("$BIN" --version 2>/dev/null || echo none)
    case "$stamp" in
        "$head_hash"-dirty*)
            if [ -n "${CI:-}" ]; then
                echo "STALE BINARY: $BIN stamps '$stamp' (dirty tree) — CI requires a clean build" >&2
                exit 1
            fi
            echo "note: $BIN is a dirty-tree build ('$stamp') — local iteration only" >&2
            ;;
        "$head_hash"*) : ;;
        *)
            echo "STALE BINARY: $BIN stamps '$stamp', HEAD is '$head_hash' — rebuild with ./publish.sh" >&2
            exit 1
            ;;
    esac
fi

# mtime gate: any source newer than the binary means it is out of date
if [ -d "$repo_root/src/Weir" ]; then
    newer=$(find "$repo_root/src/Weir" -path '*/obj' -prune -o -path '*/bin' -prune -o -name '*.fs' -newer "$BIN" -print -quit)
    if [ -n "$newer" ]; then
        echo "STALE BINARY: $BIN is older than $newer — rebuild with ./publish.sh" >&2
        exit 1
    fi
fi
