# CI (GitHub Actions)

`.github/workflows/ci.yml` runs the battery `ci/local.sh` mirrors, on
a three-platform matrix [D:github-ci]:

- **linux** — the full battery at GitLab parity: unit twice in Debug
  (flake detection), AOT publish + the freshness gate, e2e (the
  conflict-marker gate at its top), fuzz smoke, doc-tests, the
  dead-private check, the sweep and hunt2 repro harnesses, the F#
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

`.github/workflows/storm.yml` is the nightly signal storm
[D:storm-nightly] (linux, also workflow_dispatch with `runs`, `count`,
`tty`, `trees`, `repl_runs`). `ci/storm/storm.py` sends INT/TERM/HUP/QUIT
at random points into the fixtures in `ci/storm/fx/` and judges each run
against docs/reference/concurrency.md (exit by the first signal, cleanups
whole and once, nothing after a cut, no orphan, no tmp left, no core, a
cooked pty after a terminal run); six storms — one signal, `--count 2-3`,
`--tty`, `--kinds QUIT`, `--group` (TERM/HUP to the process group) and
`--tty --kinds INT,QUIT,HANGUP` (typed ^C/^\, the master closed). Fixture
directives (`// storm-feed:`, `storm-cut-err:`, `storm-cpu-max:`,
`storm-allow-orphan:`) are listed in its header. `ci/storm/repl.py`
storms the tty REPL with the entries in `ci/storm/rx/`: `--mode int` (a
^C into a many-statement entry: `↳ exit 130`, nothing after, the session
lives on) and `--mode kill` (TERM/HUP/hangup at the prompt or mid-entry:
death by the signal, terminal restored). Then `ci/storm/fuzz.py`, random
fan-out/race trees against a discrete-event oracle. Unlike
harnesses.yml they gate: any failing run exits 1; a run whose only
issues are an open finding's (storm.py `known_of`, repl.py
`KNOWN_SCOPED_130`) prints as `known`, not bad. Each writes
`runs.jsonl`/`trees.jsonl` and `summary.json` into its work dir and
keeps the failing run dirs; the job uploads them as `storm-results`.
Locally (Linux, never /tmp):

    STORM_PORT0=20000 python3 -I ci/storm/storm.py --bin BIN --out DIR --runs 6 --count 1
    STORM_PORT0=20000 python3 -I ci/storm/storm.py --bin BIN --out DIR --runs 6 --count 1 --kinds QUIT
    STORM_PORT0=20000 python3 -I ci/storm/storm.py --bin BIN --out DIR --runs 6 --count 1 --group --kinds TERM,HUP
    STORM_PORT0=20000 python3 -I ci/storm/storm.py --bin BIN --out DIR --runs 6 --count 1-2 --tty --kinds INT,QUIT,HANGUP
    python3 -I ci/storm/repl.py --bin BIN --out DIR --runs 12 --mode int
    python3 -I ci/storm/repl.py --bin BIN --out DIR --runs 12 --mode kill
    python3 -I ci/storm/fuzz.py --bin BIN --out DIR --trees 60

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
