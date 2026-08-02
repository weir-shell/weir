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
& (Join-Path $dest "weir.exe") -e '(1 + 2) * 2'
