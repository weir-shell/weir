# CI (GitHub Actions)

`.github/workflows/ci.yml` runs the battery `ci/local.sh` mirrors, on
a three-platform matrix [D:github-ci]:

- **linux** — the full battery at GitLab parity: unit twice (flake
  detection), AOT publish + the freshness gate, e2e (the
  conflict-marker gate at its top), fuzz smoke, doc-tests, the F#
  oracle, timing.
- **macos** — unit, publish+gate, e2e, doc-tests; timing advisory.
  The e2e arm is the GNU-ism sweep's real signal: first-run reds are
  findings to triage, not flakes.
- **windows** — unit (STATED SKIPS expected; the Skipped count is the
  visible list — a new skip is a finding), publish via publish.ps1 +
  the same bash freshness gate under Git Bash (one implementation,
  no .ps1 twin), e2e (HTTP is the one surface never run there),
  doc-tests; timing advisory.

`.github/workflows/fuzz-deep.yml` is the scheduled deep run (linux,
three 10k seeds — one pinned, two run-number-derived); the driver
holds the deep-lock exactly as locally.

The old `.gitlab-ci.yml` (kaniko image + single test job) retired
with the GitHub migration; the toolchain image's job is done by
setup-dotnet + apt on the runner.

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
