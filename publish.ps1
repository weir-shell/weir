# Windows twin of publish.sh [D:windows-v1]: publish-and-copy only —
# the stamp lives in Weir.fsproj's WeirStamp target, so every publish
# path stamps identically. The deep-run lock guard is deliberately
# absent: deep fuzz runs are a Linux workflow.
param([string] $Rid = "win-x64")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish src/Weir -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dest = Join-Path $env:LOCALAPPDATA "Programs\weir"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$exe = Join-Path $dest "weir.exe"

# a RUNNING weir.exe (the LSP inside an open editor) LOCKS the file
# against overwrite, but Windows allows RENAMING a running image —
# move it aside, copy fresh. POSIX needs none of this (install(1)
# replaces via unlink; the running process keeps its inode).
if (Test-Path $exe) {
    $old = Join-Path $dest "weir.exe.old"
    if (Test-Path $old) {
        try { Remove-Item $old -ErrorAction Stop }
        catch { $old = Join-Path $dest ("weir.exe.old-" + (Get-Random)) }
    }
    Move-Item $exe $old -Force
}
Copy-Item "src\Weir\bin\Release\net10.0\$Rid\publish\Weir.exe" $exe -Force

# sweep leftovers best-effort — one may still be a live process's
# image until its editor reloads; it goes on the next run
Get-ChildItem $dest -Filter "weir.exe.old*" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }

Write-Host "installed: $exe"
Write-Host "note: a running LSP keeps the OLD image until you reload the editor window"

# PATH is the user's to mutate, not this script's — detect and TEACH
# (the CLI teaching-arms posture): a fresh shell only sees weir once
# the user-scope Path carries $dest.
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -notcontains $dest) {
    Write-Host ""
    Write-Host "note: $dest is not on your user PATH — new shells will not find weir."
    Write-Host "Add it once with:"
    Write-Host "  [Environment]::SetEnvironmentVariable(`"Path`", [Environment]::GetEnvironmentVariable(`"Path`", `"User`") + `";$dest`", `"User`")"
    Write-Host "(then open a new shell, or for this one: `$env:Path += `";$dest`")"
    Write-Host ""
}

& (Join-Path $dest "weir.exe") -e '(1 + 2) * 2'
