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
Copy-Item "src\Weir\bin\Release\net10.0\$Rid\publish\Weir.exe" (Join-Path $dest "weir.exe") -Force

Write-Host "installed: $dest\weir.exe"

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
