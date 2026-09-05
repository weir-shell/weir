# weir installer (Windows) [D:releases][D:install-checksum-scope] — the
# PINNED, two-origin form. This file is a TEMPLATE: the release workflow
# fills in the tag and checksum placeholders and serves the result
# from weir.sh, a DIFFERENT origin than the GitHub release binaries — so
# an attacker who compromises the release assets alone cannot also
# change the checksums the installer trusts. It installs ONE pinned
# version, not always-newest.
#
#   irm https://weir.sh/install.ps1 | iex
#
# Unsigned binary: SmartScreen may warn on first run — More info ->
# Run anyway. The embedded checksum below verifies the download against
# a checksum served from a different origin than the binary — so it is
# tamper-evident here, not merely integrity (docs/INSTALL.md). The repo
# copy is the template; do not run it directly (its placeholders are
# unsubstituted — it says so and stops). irm buffers the whole response
# before iex sees it and throws on an incomplete read, so there is no
# partial-script hazard — the install.sh main() truncation guard has no
# ps1 equivalent to need.
$ErrorActionPreference = "Stop"

$repo = "weir-shell/weir"
$dest = if ($env:WEIR_INSTALL_DIR) { $env:WEIR_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Programs\weir" }
$tag = "@WEIR_TAG@"
# the release's SHA256SUMS, baked in — the two-origin property
$sums = @"
@WEIR_SHA256SUMS@
"@

# unsubstituted template detector: a real tag is v0.1.0-shaped and
# never contains '@' — only the placeholder does
if ($tag -like "*@*") {
    throw "this is the install TEMPLATE — fetch the generated script: irm https://weir.sh/install.ps1 | iex"
}

$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { "x64" }
    "ARM64" { "arm64" }
    default { throw "unsupported architecture: $env:PROCESSOR_ARCHITECTURE — download from https://github.com/$repo/releases" }
}
$rid = "win-$arch"

$name = "weir-$tag-$rid.exe"
$base = "https://github.com/$repo/releases/download/$tag"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "weir-install-$PID"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    Write-Host "downloading $name ($tag)..."
    Invoke-WebRequest -Uri "$base/$name" -OutFile (Join-Path $tmp $name)

    # verify BEFORE installing against the EMBEDDED checksum
    # [D:install-checksum-scope]: nothing is fetched from the binary's
    # origin to verify it, so compromising the release assets alone
    # yields no code execution. A missing entry is NAMED.
    $expected = ($sums -split "`n" |
        Where-Object { $_ -match [regex]::Escape($name) + "$" }) -split "\s+" |
        Select-Object -First 1
    if (-not $expected) { throw "no embedded checksum for $name (unsupported platform for $tag?)" }
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $name)).Hash.ToLower()
    if ($actual -ne $expected.ToLower()) {
        throw "CHECKSUM MISMATCH for $name — refusing to install (expected $expected, got $actual)"
    }

    # signed build provenance, best-effort: if the GitHub CLI is present,
    # confirm this repo's Actions built the binary. Not required.
    # gh attestation REQUIRES auth even for public repos — the
    # unauthenticated case names its repair instead of guessing.
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        gh auth status *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "note: gh present but not authenticated — 'gh auth login' enables provenance verification; the checksum stands"
        } else {
            gh attestation verify (Join-Path $tmp $name) --repo $repo *> $null
            if ($LASTEXITCODE -eq 0) { Write-Host "provenance: verified (gh attestation)" }
            else { Write-Host "note: gh present but provenance not verified (network, or attestation unavailable) — the checksum stands" }
        }
    }

    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item (Join-Path $tmp $name) (Join-Path $dest "weir.exe") -Force
    $installed = Join-Path $dest "weir.exe"
    Write-Host "installed: $installed ($(& $installed --version))"

    # segment match, not substring [D:install-checksum-scope]: a Path
    # entry that merely has $dest as a PREFIX would falsely suppress the
    # note (the segment-vs-prefix class Path.under rule 4 guards)
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $onPath = ($userPath -split ';') -contains $dest
    if (-not $onPath) {
        Write-Host "note: $dest is not on your PATH — add it once:"
        Write-Host "  [Environment]::SetEnvironmentVariable('Path', `"$userPath;$dest`", 'User')"
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
