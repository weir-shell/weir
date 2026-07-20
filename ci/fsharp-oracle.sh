#!/usr/bin/env bash
# The F# oracle: dotnet/fsharp (via FCS) referees weir's fidelity claims.
# Test-side only — FCS never approaches the shipping binary. PR-triggered;
# FCS restore is heavy, so CI should cache the NuGet package dir.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/Weir.Fidelity
