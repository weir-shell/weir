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

dotnet publish src/Weir -c Release -r "$rid"

mkdir -p ~/.local/bin
install -m 755 "src/Weir/bin/Release/net10.0/$rid/publish/Weir" ~/.local/bin/weir

echo "installed: ~/.local/bin/weir"
~/.local/bin/weir -e '1 + 2 |> double'
