#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *)
        echo "unsupported arch: $(uname -m)" >&2
        exit 1
        ;;
esac

# fail fast [D:masking-mechanized]: refuse BEFORE the build if a deep run
# is live, so the common concurrent case doesn't waste 30s. The
# authoritative barrier is re-checked just before the install swap.
if holder=$(ci/deep-lock.sh check); then
    echo "REFUSING TO PUBLISH: a deep fuzz run is live (pid $holder) — it would compare P against T(P) across two builds. Wait for it, or kill it, then retry." >&2
    exit 1
fi

# the build STAMP [D:masking-mechanized]: harnesses assert this
# equals HEAD before running anything — stale results become
# impossible rather than catchable. The `-dirty` suffix marks a build
# from an uncommitted tree (the gate warns locally, hard-fails in CI)
# so a dirty binary can't masquerade as its clean HEAD. Built from
# rev-parse + a porcelain check, NOT `git describe` — describe becomes
# tag-relative the moment a tag exists and would break the gate's
# short-hash comparison.
stamp="$(git rev-parse --short HEAD 2>/dev/null || echo nogit)"
if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
    stamp="${stamp}-dirty"
fi
dotnet publish src/Weir -c Release -r "$rid" -p:InformationalVersion="$stamp" -p:IncludeSourceRevisionInInformationalVersion=false

# never swap the binary underfoot of a live deep run [D:masking-mechanized]
# — that is the one window the start-of-run freshness gate can't close.
if holder=$("$(dirname "$0")/ci/deep-lock.sh" check); then
    echo "REFUSING TO PUBLISH: a deep fuzz run is live (pid $holder) — installing now would swap the binary mid-run, so its metamorphic properties would compare P against T(P) across two builds. Wait for it, or kill it, then retry." >&2
    exit 1
fi

mkdir -p ~/.local/bin
install -m 755 "src/Weir/bin/Release/net10.0/$rid/publish/Weir" ~/.local/bin/weir

echo "installed: ~/.local/bin/weir"
~/.local/bin/weir -e '(1 + 2) * 2'
