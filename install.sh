#!/bin/sh
# weir installer [D:releases] — POSIX only (Windows: install.ps1, or
# download the .exe from the releases page; see docs/INSTALL.md). Detects the platform,
# downloads the latest release binary, VERIFIES its checksum against
# the release's SHA256SUMS, and installs to ~/.local/bin/weir.
#
#   curl -fsSL https://raw.githubusercontent.com/weir-shell/weir/main/install.sh | sh
set -eu

REPO="weir-shell/weir"
DEST="${WEIR_INSTALL_DIR:-$HOME/.local/bin}"

case "$(uname -s)" in
    Linux)  os=linux ;;
    Darwin) os=osx ;;
    *) echo "unsupported OS: $(uname -s) — download from https://github.com/$REPO/releases" >&2; exit 1 ;;
esac
case "$(uname -m)" in
    x86_64|amd64)  arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "unsupported arch: $(uname -m) — download from https://github.com/$REPO/releases" >&2; exit 1 ;;
esac
rid="$os-$arch"

if [ "$rid" = "osx-x64" ]; then
    echo "no osx-x64 build is published (Apple silicon only for now) — see docs/INSTALL.md" >&2
    exit 1
fi

tag=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | grep -m1 '"tag_name"' | cut -d'"' -f4)
[ -n "$tag" ] || { echo "could not resolve the latest release tag" >&2; exit 1; }

name="weir-$tag-$rid"
base="https://github.com/$REPO/releases/download/$tag"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "downloading $name ($tag)…"
curl -fsSL -o "$tmp/$name" "$base/$name"
curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS"

# verify BEFORE installing — an installer that skips this is worse
# than no installer (macOS ships shasum, linux sha256sum; use whichever)
if command -v sha256sum >/dev/null 2>&1; then
    SUM="sha256sum"
else
    SUM="shasum -a 256"
fi
(cd "$tmp" && grep " $name\$" SHA256SUMS | $SUM -c -) \
    || { echo "CHECKSUM MISMATCH — refusing to install" >&2; exit 1; }

mkdir -p "$DEST"
install -m 755 "$tmp/$name" "$DEST/weir"
echo "installed: $DEST/weir ($("$DEST/weir" --version))"
case ":$PATH:" in
    *":$DEST:"*) ;;
    *) echo "note: $DEST is not on your PATH" ;;
esac
