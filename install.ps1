# weir installer (Windows) [D:releases] — detects the architecture,
# downloads the latest release binary, VERIFIES its checksum against
# the release's SHA256SUMS, and installs to
# %LOCALAPPDATA%\Programs\weir\weir.exe (override: $env:WEIR_INSTALL_DIR).
#
#   irm https://raw.githubusercontent.com/weir-shell/weir/main/install.ps1 | iex
#
# Unsigned binary: SmartScreen may warn on first run — More info ->
# Run anyway. The checksum below verifies the download is intact (not
# truncated/corrupted); it is NOT a signature — SHA256SUMS shares the
# release origin [D:install-checksum-scope] (docs/INSTALL.md).
$ErrorActionPreference = "Stop"

$repo = "weir-shell/weir"
$dest = if ($env:WEIR_INSTALL_DIR) { $env:WEIR_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Programs\weir" }

$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { "x64" }
    "ARM64" { "arm64" }
    default { throw "unsupported architecture: $env:PROCESSOR_ARCHITECTURE — download from https://github.com/$repo/releases" }
}
$rid = "win-$arch"

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$tag = $release.tag_name
if (-not $tag) { throw "could not resolve the latest release tag" }

$name = "weir-$tag-$rid.exe"
$base = "https://github.com/$repo/releases/download/$tag"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "weir-install-$PID"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    Write-Host "downloading $name ($tag)..."
    Invoke-WebRequest -Uri "$base/$name" -OutFile (Join-Path $tmp $name)
    Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $tmp "SHA256SUMS")

    # verify BEFORE installing [D:install-checksum-scope]: catches
    # truncation and CDN corruption. SHA256SUMS shares the release
    # origin with the binary, so this is integrity, NOT tamper
    # protection. (irm buffers the whole response before iex sees it and
    # throws on an incomplete read, so there is no partial-script hazard
    # — the install.sh main() guard has no ps1 equivalent to need.)
    $expected = (Get-Content (Join-Path $tmp "SHA256SUMS") |
        Where-Object { $_ -match [regex]::Escape($name) + "$" }) -split "\s+" |
        Select-Object -First 1
    if (-not $expected) { throw "no checksum for $name in SHA256SUMS" }
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $name)).Hash.ToLower()
    if ($actual -ne $expected.ToLower()) {
        throw "CHECKSUM MISMATCH for $name — refusing to install (expected $expected, got $actual)"
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
