# Installing weir

One AOT binary, no runtime. Every release carries one binary per
platform plus a `SHA256SUMS` file.

## The quick way

Linux, macOS:

```
curl -fsSL https://weir.sh/install.sh | sh
```

Windows (PowerShell):

```
irm https://weir.sh/install.ps1 | iex
```

Both detect your platform, download the **pinned** release, **verify
the checksum**, and install — `~/.local/bin/weir` on POSIX,
`%LOCALAPPDATA%\Programs\weir\weir.exe` on Windows (override with
`WEIR_INSTALL_DIR`).

These scripts are **generated per release and served from `weir.sh`** —
a different origin than the GitHub release binaries. Each one pins one
version and carries that release's checksums baked in, so verification
never fetches anything from the binary's own origin. Compromising the
release assets alone therefore can't feed the installer a matching
checksum: the check is **tamper-evident here**, not merely an integrity
check. (Where `gh` is present the installer also verifies GitHub's
signed build provenance, best-effort — a second, independent origin.)

A pinned script installs the version it was cut for. To move to a newer
release, re-fetch the script from `weir.sh` (it always serves the
latest) or grab the binary manually below.

> The files named `install.sh` / `install.ps1` in the repo are
> **templates** (`@WEIR_TAG@` / `@WEIR_SHA256SUMS@` placeholders);
> `ci/gen-install.weir` fills them in at release time. Users fetch the
> generated artifact from `weir.sh`, never the repo template.

## Manual download

Grab the binary for your platform from
[releases](https://github.com/weir-shell/weir/releases):

| platform | artifact |
|---|---|
| Linux x64 | `weir-<tag>-linux-x64` |
| Linux arm64 | `weir-<tag>-linux-arm64` |
| macOS (Apple silicon) | `weir-<tag>-osx-arm64` |
| macOS (Intel) | `weir-<tag>-osx-x64` |
| Windows x64 | `weir-<tag>-win-x64.exe` |
| Windows arm64 | `weir-<tag>-win-arm64.exe` |

Verify, then install:

```
sha256sum -c --ignore-missing SHA256SUMS     # macOS: shasum -a 256 -c
chmod +x weir-<tag>-<rid>
mv weir-<tag>-<rid> ~/.local/bin/weir
```

Windows: `Get-FileHash -Algorithm SHA256 weir-<tag>-win-<arch>.exe`
and compare against the `SHA256SUMS` line.

`weir --version` reports the release tag (`v0.1.0+<sha>`).

## Container image

The same released binary, on distroless. Three forms:

```
# the REPL — needs a tty
docker run --rm -it ghcr.io/weir-shell/weir:latest

# run a script from the current directory
docker run --rm -v "$PWD:/w" -w /w ghcr.io/weir-shell/weir:latest script.weir

# a one-liner
docker run --rm ghcr.io/weir-shell/weir:latest -e 'print "hello"'
```

Without `-it`, the bare form prints a prompt, reads end-of-file, and
exits immediately — correct behaviour that looks broken. The REPL
needs a terminal; give it one.

Worth knowing:

- `:latest` follows the latest published release, and never points at
  a prerelease — the release flow enforces this; it is not a
  convention.
- One manifest covers amd64 and arm64 — nothing to choose. The whole
  pull is ~17 MB compressed: the ~13 MB binary plus the distroless base.
- The image runs as a non-root user (uid 65532). A script that writes
  into a mounted volume inherits ordinary volume permissions.
- There is no shell inside — no `docker run … sh`, and `docker exec`
  has nothing to run. The image is the binary; that is the point.
- The image digest carries signed build provenance:
  `gh attestation verify oci://ghcr.io/weir-shell/weir:latest --repo weir-shell/weir`
- It is not a build environment: no SDK, no source. The development
  container is `ci/run.Dockerfile` in the repo — a different artifact.

## Unsigned binaries — the first-run dialogs

The binaries are **not code-signed** (a deliberate v1 posture; signing
is a stated later item). Two platforms will warn:

- **macOS** quarantines downloaded binaries. Either clear the
  attribute — `xattr -d com.apple.quarantine ~/.local/bin/weir` — or
  allow it under System Settings → Privacy & Security after the first
  refusal. (The `curl | sh` installer avoids the browser quarantine
  path entirely.)
- **Windows SmartScreen** shows "Windows protected your PC": choose
  *More info* → *Run anyway*. Verify the checksum first — that is
  what it is for.

A scary dialog with no explanation reads as malware; this section is
that explanation.

## Versioning

weir is `0.x`, and that means what semver says it means: **anything
can break between releases.** Release notes state what changed and
what broke. `1.0` happens when the language stops moving under its
users — not on a date.

## Building from source

The README's Developing section covers it: `./publish.sh` with the
.NET 10 SDK and clang (`./publish.ps1` + VS Build Tools on Windows).
