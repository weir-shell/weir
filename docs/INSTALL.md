# Installing weir

One AOT binary, no runtime. Every release carries one binary per
platform plus a `SHA256SUMS` file.

## The quick way

Linux, macOS:

```
curl -fsSL https://raw.githubusercontent.com/weir-shell/weir/main/install.sh | sh
```

Windows (PowerShell):

```
irm https://raw.githubusercontent.com/weir-shell/weir/main/install.ps1 | iex
```

Both detect your platform, download the latest release, **verify the
checksum**, and install — `~/.local/bin/weir` on POSIX,
`%LOCALAPPDATA%\Programs\weir\weir.exe` on Windows (override with
`WEIR_INSTALL_DIR`).

The checksum defends against **truncated or corrupted downloads**, not
tampering: `SHA256SUMS` ships from the same release as the binary, so
it is an integrity check, not a signature. It is genuinely worth
having — and it is what the SmartScreen note below leans on — but do
not read it as tamper protection.

## Manual download

Grab the binary for your platform from
[releases](https://github.com/weir-shell/weir/releases):

| platform | artifact |
|---|---|
| Linux x64 | `weir-<tag>-linux-x64` |
| Linux arm64 | `weir-<tag>-linux-arm64` |
| macOS (Apple silicon) | `weir-<tag>-osx-arm64` |
| Windows x64 | `weir-<tag>-win-x64.exe` |
| Windows arm64 | `weir-<tag>-win-arm64.exe` |

Not published: `osx-x64` (Intel Macs) — a stated gap, not an
accident; it joins when someone needs it.

Verify, then install:

```
sha256sum -c --ignore-missing SHA256SUMS     # macOS: shasum -a 256 -c
chmod +x weir-<tag>-<rid>
mv weir-<tag>-<rid> ~/.local/bin/weir
```

Windows: `Get-FileHash -Algorithm SHA256 weir-<tag>-win-<arch>.exe`
and compare against the `SHA256SUMS` line.

`weir --version` reports the release tag (`v0.1.0+<sha>`).

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
