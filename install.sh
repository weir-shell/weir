#!/bin/sh
# weir installer [D:releases][D:install-checksum-scope] — the PINNED,
# two-origin form. This file is a TEMPLATE: the release workflow fills
# in the tag and checksum placeholders and serves the result
# from weir.sh, a DIFFERENT origin than the GitHub release binaries — so
# an attacker who compromises the release assets alone cannot also
# change the checksums the installer trusts. It installs ONE pinned
# version (the release it was generated for), not always-newest.
#
#   curl -fsSL https://weir.sh/install.sh | sh
#
# The whole body is a main() invoked on the LAST line
# [D:install-truncation]: a fetch cut mid-body is an unclosed function —
# a syntax error that defines and runs nothing (set -eu cannot catch a
# truncation; nothing failed). The repo copy is the template; do not run
# it directly (its placeholders are unsubstituted — it says so and stops).
set -eu

main() {
    REPO="weir-shell/weir"
    DEST="${WEIR_INSTALL_DIR:-$HOME/.local/bin}"
    TAG="@WEIR_TAG@"
    # the release's SHA256SUMS, baked in — the two-origin property
    # [D:install-checksum-scope]: verification does not fetch anything
    # from the binary's origin, so compromising the release assets alone
    # yields no code execution. ci/gen-install.weir replaces the anchor
    # line below with the release's checksum lines (heredoc, so template
    # and artifact both parse).
    SUMS="$(cat <<'WEIR_SUMS'
@WEIR_SHA256SUMS@
WEIR_SUMS
)"

    # unsubstituted template detector: a real tag is v0.1.0-shaped and
    # never contains '@' — only the placeholder does
    case "$TAG" in
        *@*) echo "this is the install TEMPLATE — fetch the generated script: curl -fsSL https://weir.sh/install.sh | sh" >&2; exit 1 ;;
    esac

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

    name="weir-$TAG-$rid"
    base="https://github.com/$REPO/releases/download/$TAG"
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT

    echo "downloading $name ($TAG)…"
    curl -fsSL -o "$tmp/$name" "$base/$name"

    # verify against the EMBEDDED checksum (macOS ships shasum, linux
    # sha256sum). A missing entry is NAMED, not left to a formatting
    # error [D:install-checksum-scope].
    if command -v sha256sum >/dev/null 2>&1; then
        SUM="sha256sum"
    else
        SUM="shasum -a 256"
    fi
    expected=$(printf '%s\n' "$SUMS" | grep " $name\$" | cut -d' ' -f1)
    [ -n "$expected" ] || { echo "no embedded checksum for $name (unsupported platform for $TAG?)" >&2; exit 1; }
    actual=$($SUM "$tmp/$name" | cut -d' ' -f1)
    [ "$expected" = "$actual" ] || { echo "CHECKSUM MISMATCH for $name — refusing to install (expected $expected, got $actual)" >&2; exit 1; }

    # signed build provenance, best-effort [D:install-checksum-scope]:
    # if the GitHub CLI is present, verify the binary was built by this
    # repo's Actions (OIDC identity) — a second, independent origin. Not
    # required (most curl|sh users lack gh); the embedded checksum stands
    # on its own. gh attestation REQUIRES auth even for public repos, so
    # the unauthenticated case names its repair instead of guessing.
    if command -v gh >/dev/null 2>&1; then
        if ! gh auth status >/dev/null 2>&1; then
            echo "note: gh present but not authenticated — 'gh auth login' enables provenance verification; the checksum stands"
        elif gh attestation verify "$tmp/$name" --repo "$REPO" >/dev/null 2>&1; then
            echo "provenance: verified (gh attestation)"
        else
            echo "note: gh present but provenance not verified (network, or attestation unavailable) — the checksum stands"
        fi
    fi

    mkdir -p "$DEST"
    install -m 755 "$tmp/$name" "$DEST/weir"
    # --version proves the binary RUNS, not merely that it landed
    echo "installed: $DEST/weir ($("$DEST/weir" --version))"
    case ":$PATH:" in
        *":$DEST:"*) ;;
        *) echo "note: $DEST is not on your PATH" ;;
    esac
}

main "$@"
