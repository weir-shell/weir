#!/usr/bin/env bash
# Run the full CI pipeline locally in the CI image (clean-room mirror of
# .gitlab-ci.yml's test job). Post-stage ritual: run this before pushing.
#
# The repo is COPIED into the image as build context, not bind-mounted:
# bind mounts silently break with remote docker daemons (paths resolve on
# the daemon host -> empty /work), FUSE checkouts, and unlabeled SELinux.
# Context upload works everywhere. WEIR_CI_ENGINE=podman for rootless.
set -euo pipefail

cd "$(dirname "$0")/.."

ENGINE="${WEIR_CI_ENGINE:-docker}"

"$ENGINE" build -t weir-ci ci/
"$ENGINE" build -t weir-ci-run -f ci/run.Dockerfile .

"$ENGINE" run --rm \
    -v weir-nuget:/root/.nuget \
    weir-ci-run \
    bash -ec 'dotnet test tests/Weir.Tests/Weir.Tests.fsproj && dotnet test tests/Weir.Tests/Weir.Tests.fsproj && ./publish.sh && ci/e2e.sh && dotnet test tests/Weir.Fuzz/Weir.Fuzz.fsproj && ci/skill-doc.sh && ci/fsharp-oracle.sh && ci/timing.sh'
