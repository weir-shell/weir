# CI image

The workflow runs inside `codeberg.org/queil/weir-ci:latest` — dotnet 10 SDK
plus the AOT toolchain (clang, zlib1g-dev, binutils), git (checkout + e2e
battery), and procps (lifecycle tests use pgrep/ps).

One-time build and push (rebuild only when the toolchain changes):

```sh
docker build -t codeberg.org/queil/weir-ci:latest ci/
docker login codeberg.org        # Codeberg access token with package scope
docker push codeberg.org/queil/weir-ci:latest
```

Then mark the package public in Codeberg → Packages → weir-ci → settings, so
the runner can pull without credentials.

Notes:
- SELinux hosts (Fedora): the repo bind mount needs the `:z` relabel —
  `ci/local.sh` bakes it in. An empty `/work` with MSB1003/MSB1009 is
  the symptom of a missing label, not an SDK problem.
- Forgejo's act-based runner injects node into job containers, so JS actions
  (`actions/checkout`) need nothing extra in the image.
- The image is toolchain-only, deliberately not coupled to the repo (no NuGet
  restore baked in). If restore time ever dominates, warm the package cache in
  a build stage — but that couples image rebuilds to dependency bumps.
- `ci/timing.sh` thresholds are env-overridable (`WEIR_MAX_EXPR_MS`,
  `WEIR_MAX_CMD_MS`) if the tiny runner needs more headroom.
