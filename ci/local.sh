#!/usr/bin/env bash
# Run the full CI pipeline locally in the CI image (clean-room mirror of
# .github/workflows/ci.yml). Post-stage ritual: run this before pushing.
set -euo pipefail

cd "$(dirname "$0")/.."

# Fedora note: if the repo lives on a FUSE/network mount, the ROOT docker
# daemon cannot read it and the container sees an empty /work regardless
# of flags — use rootless podman: WEIR_CI_ENGINE=podman ./ci/local.sh
ENGINE="${WEIR_CI_ENGINE:-docker}"

"$ENGINE" build -t weir-ci ci/

# :z relabels the bind mount for SELinux hosts (Fedora et al.) — without
# it the container sees an EMPTY /work and every dotnet invocation fails
# with a misleading MSB1003/MSB1009; harmless no-op elsewhere
"$ENGINE" run --rm \
    -v "$PWD":/work:z \
    -v weir-nuget:/root/.nuget \
    -w /work \
    weir-ci \
    bash -ec 'dotnet test tests/Weir.Tests/Weir.Tests.fsproj && dotnet test tests/Weir.Tests/Weir.Tests.fsproj && ./publish.sh && ci/e2e.sh && ci/skill-doc.sh && ci/fsharp-oracle.sh && ci/timing.sh'
