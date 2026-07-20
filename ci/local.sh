#!/usr/bin/env bash
# Run the full CI pipeline locally in the CI image (clean-room mirror of
# .github/workflows/ci.yml). Post-stage ritual: run this before pushing.
set -euo pipefail

cd "$(dirname "$0")/.."

docker build -t weir-ci ci/

docker run --rm \
    -v "$PWD":/work \
    -v weir-nuget:/root/.nuget \
    -w /work \
    weir-ci \
    bash -ec 'dotnet test && dotnet test && ./publish.sh && ci/e2e.sh && ci/skill-doc.sh && ci/fsharp-oracle.sh && ci/timing.sh'
