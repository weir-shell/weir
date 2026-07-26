#!/usr/bin/env bash
# The deep-run lock [D:masking-mechanized] — closes the one window
# ci/check-fresh.sh cannot: a republish DURING a live deep fuzz run
# swaps the binary underfoot, so a metamorphic property compares P
# against T(P) across two builds and fails against a half-swapped
# binary (a manufactured failure, the harness-truth class). The deep
# driver (tools/fuzz.weir) holds this lock for the run; publish.sh
# refuses to install while a LIVE holder exists. One file, one
# liveness definition, shared by both — the check-fresh.sh pattern.
#
#   deep-lock.sh acquire <pid>   # claim it (refuse if a live run holds it)
#   deep-lock.sh release         # drop it
#   deep-lock.sh check           # exit 0 + print pid if a live run holds it,
#                                # else exit 1 (clearing a stale lock)
set -euo pipefail

LOCK="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.weir-deep-run.lock"

# a lock is LIVE only if its recorded pid is still running; a pid that
# has died (crashed run, no release) leaves a STALE lock that any actor
# may clear — staleness is decidable, so the lock never wedges.
live_holder() {
    [ -f "$LOCK" ] || return 1
    local h
    # an UNREADABLE or garbage lock is not a STALE lock — refusing to
    # guess beats silently clearing a live one [D:vacuous-probe-audit]
    if ! h=$(cat "$LOCK" 2>/dev/null); then
        echo "deep-lock: $LOCK exists but is unreadable — inspect it; refusing to treat as stale" >&2
        exit 3
    fi
    case "$h" in
        '' | *[!0-9]*)
            echo "deep-lock: $LOCK holds garbage ('$h') — inspect and remove it; refusing to guess" >&2
            exit 3
            ;;
    esac
    if kill -0 "$h" 2>/dev/null; then
        printf '%s\n' "$h"
        return 0
    fi
    rm -f "$LOCK"
    return 1
}

case "${1:-}" in
    acquire)
        if h=$(live_holder); then
            echo "a deep fuzz run is already live (pid $h)" >&2
            exit 1
        fi
        printf '%s\n' "${2:?acquire needs a pid}" >"$LOCK"
        ;;
    release)
        rm -f "$LOCK"
        ;;
    check)
        live_holder
        ;;
    *)
        echo "usage: deep-lock.sh {acquire <pid>|release|check}" >&2
        exit 2
        ;;
esac
