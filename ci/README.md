# CI (GitLab)

`.gitlab-ci.yml` runs the full battery inside a toolchain image built
FROM this directory's Dockerfile: dotnet 10 SDK, the AOT toolchain
(clang, zlib1g-dev, binutils), git (checkout + e2e battery), procps
(lifecycle tests use pgrep/ps), and python3 (the LSP/REPL/harness/
grammar-inventory probes — these were silently SKIPPED under the old
python-less image; the GitLab migration paid that gap).

The image lives in the PROJECT registry
(`$CI_REGISTRY_IMAGE/weir-ci:latest`) and is rebuilt BY THE PIPELINE
(kaniko, no docker daemon) whenever ci/Dockerfile changes. Bootstrap
on a fresh project: run the `build-image` job manually once (it is
`when: manual` outside Dockerfile changes), then `test` pipelines pull
it with the job token — no docker login ritual, no external registry.

Notes (carried from the previous home where still true):
- `ci/local.sh` copies the repo into the image (build context) instead
  of bind-mounting: bind mounts silently yield an EMPTY /work with
  remote docker daemons (paths resolve daemon-side), FUSE/network
  checkouts, and unlabeled SELinux — the symptom is a misleading
  MSB1003/MSB1009. Context upload works with every daemon topology.
  `WEIR_CI_ENGINE=podman` for rootless.
- The image is toolchain-only, deliberately not coupled to the repo
  (no NuGet restore baked in); the pipeline caches `.nuget/packages`
  instead (the F# oracle's FCS restore is the heavy hitter).
- The e2e battery HARD-gates on the build stamp (`weir --version` ==
  HEAD) and source mtimes — publish.sh runs inside the pipeline
  before e2e, so the gate holds by construction there too.
- `ci/timing.sh` thresholds are env-overridable (`WEIR_MAX_EXPR_MS`,
  `WEIR_MAX_CMD_MS`) if a small shared runner needs headroom.
