# Installing weir

weir is a single static binary with nothing else to install. Each
release has one binary per platform, plus a `SHA256SUMS` file with
their checksums.

## The quick way

Linux, macOS:

```
curl -fsSL https://weir.sh/install.sh | sh
```

Windows (PowerShell):

```
irm https://weir.sh/install.ps1 | iex
```

Both scripts detect your platform, download the release the script
was made for, **check its checksum**, and install it:

- `~/.local/bin/weir` on Linux and macOS
- `%LOCALAPPDATA%\Programs\weir\weir.exe` on Windows

Set `WEIR_INSTALL_DIR` to install somewhere else.

A new copy of each script is made for every release and served from
`weir.sh`, while the binaries are downloaded from GitHub. The script
already contains that release's version and checksums, so it doesn't
fetch checksums from GitHub along with the binary. Someone who
replaced a binary on GitHub couldn't also make the checksum match:
the installer would notice the change and stop. If `gh` is installed and logged in,
the installer also checks GitHub's signed build provenance, as a
second check from a separate source. If that check can't run, the
checksum still applies.

Each script installs the one version it was made for. `weir.sh`
always serves the script for the latest release, so to upgrade, fetch
the script from `weir.sh` again (or download the binary by hand, as
below). To install a specific or older version, fetch the installer
from that release instead of from `weir.sh`. Every release has its
own `install.sh` and `install.ps1` with that version's checksums:

```
curl -fsSL https://github.com/weir-shell/weir/releases/download/<tag>/install.sh | sh
```

There are no packages for `brew`, `winget` or `apt` yet. Use the
script above or download the binary by hand.

> Don't run the `install.sh` / `install.ps1` files in the repo. They
> are templates with `@WEIR_TAG@` / `@WEIR_SHA256SUMS@` placeholders
> that are filled in for each release. Fetch the installer from
> `weir.sh` or from a release.

## Manual download

Download the binary for your platform, and that release's
`SHA256SUMS`, from
[releases](https://github.com/weir-shell/weir/releases):

| platform | artifact |
|---|---|
| Linux x64 | `weir-<tag>-linux-x64` |
| Linux arm64 | `weir-<tag>-linux-arm64` |
| macOS (Apple silicon) | `weir-<tag>-osx-arm64` |
| macOS (Intel) | `weir-<tag>-osx-x64` |
| Windows x64 | `weir-<tag>-win-x64.exe` |
| Windows arm64 | `weir-<tag>-win-arm64.exe` |

Check the checksum, then install. This is the same check the
installer does: it picks the binary's line out of `SHA256SUMS` and
uses no GNU-only flags, so it works on macOS too:

```
grep " weir-<tag>-<rid>$" SHA256SUMS | sha256sum -c -   # macOS: shasum -a 256 -c -
chmod +x weir-<tag>-<rid>
mv weir-<tag>-<rid> ~/.local/bin/weir
```

`~/.local/bin` must be on your `PATH`. The `curl | sh` installer
warns you when it isn't; a manual install doesn't. On Windows the
install directory is `%LOCALAPPDATA%\Programs\weir`, which isn't on
`PATH` by default either.

On Windows, run `Get-FileHash -Algorithm SHA256 weir-<tag>-win-<arch>.exe`
and compare the result with the binary's line in `SHA256SUMS`.

A binary downloaded through a browser triggers the first-run dialogs
described [below](#unsigned-binaries--the-first-run-dialogs). The
`curl | sh` installer doesn't.

`weir --version` reports the release tag (`v<tag>+<sha>`).

## Container image

The image holds the same released binary on a distroless base. You
can use it three ways:

```
# the REPL — needs a tty
docker run --rm -it ghcr.io/weir-shell/weir:latest

# run a script from the current directory
docker run --rm -v "$PWD:/w" -w /w ghcr.io/weir-shell/weir:latest script.weir

# a one-liner
docker run --rm ghcr.io/weir-shell/weir:latest -e 'print "hello"'
```

The REPL needs a terminal, so pass `-it`. Without it, the REPL prints
a prompt, reads end-of-file and exits at once. That is the correct
behaviour, but it looks broken.

Worth knowing:

- `:latest` is the latest published release. It never points at a
  prerelease; the release flow enforces this.
- The same image works on amd64 and arm64, so there's nothing to
  choose. The whole download is about 17 MB compressed: the ~13 MB
  binary plus the distroless base.
- The image runs as a non-root user (uid 65532). Writing to a
  mounted volume follows the volume's normal permissions for that
  user.
- There is no shell inside, so `docker run … sh` doesn't work and
  `docker exec` has nothing to run. The image is just weir on the
  distroless base.
- The image comes with signed build provenance, which you can check:
  `gh attestation verify oci://ghcr.io/weir-shell/weir:latest --repo weir-shell/weir`
- It is not a build environment: there is no SDK and no source. The
  development container is a separate image, built from
  `ci/run.Dockerfile` in the repo.

## Unsigned binaries — the first-run dialogs

The binaries are **not code-signed** yet; signing is planned for a
later release. Until then, two platforms warn on first run:

- **macOS** quarantines binaries downloaded through a browser. Either
  remove the quarantine flag with
  `xattr -d com.apple.quarantine ~/.local/bin/weir`, or allow weir
  under System Settings → Privacy & Security after macOS first blocks
  it. (Binaries installed with `curl | sh` aren't quarantined.)
- **Windows SmartScreen** shows "Windows protected your PC". Choose
  *More info* → *Run anyway*. Check the checksum first, so you know
  the binary is the one that was released.

These warnings are expected and don't mean the download is malware.

## Versioning

weir is `0.x`, which in semver means **anything can break between
releases.** The release notes say what changed and what broke. `1.0`
will come when the language stops changing under its users, not on a
set date.

## Building from source

See the Developing section of the README. In short: run
`./publish.sh` with the .NET 10 SDK and clang installed
(`./publish.ps1` with the VS Build Tools on Windows).
