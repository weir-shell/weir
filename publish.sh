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

mkdir -p ~/.local/bin
install -m 755 "src/Weir/bin/Release/net10.0/$rid/publish/Weir" ~/.local/bin/weir

echo "installed: ~/.local/bin/weir"
~/.local/bin/weir -e '(1 + 2) * 2'
