# PLAN — the third bug hunt (daily-driver W3)

Status: ROUND 1 FIXED (2026-10-16) — 37 confirmed (8 high, 29 medium), all fixed on plan/hunt3 (PLAN-hunt3-fixes, PLAN-kestrel), landed as #376. ROUND 2 RUNNING (2026-10-17) — see the last section.

Source plan: PLAN-daily-driver.md W3. Black-box, medium and above only;
lows go straight to BACKLOG. The loop stops at the first round with no
high or medium findings (exit criterion 1). This file is round 1.

## Method (every brief)

- **Target:** the AOT binary built from main @ the landing commit,
  published to `/output/tmp/weir-h3/` (`dotnet publish src/Weir -c
  Release -r linux-x64 -o …`). Never the JIT dev build: startup cost
  hides perf and timing behaviour.
- **Black-box first:** probe through the binary, SKILL.md and docs.
  Read source only to explain a finding already reproduced.
- **No fixing.** Agents report. Fixes come after rulings, in
  PLAN-hunt3-fixes.md, the hunt-2 pattern.
- **Every finding reproduced twice**, from a minimal repro file under
  `/output/tmp/h3-<area>/`, on a clean `weir` invocation.
- **Severity:**
  - high — crash, `internal error`, exit ≥ 128, hang, data loss, a
    trust boundary crossed (secret leak, command or path injection,
    file written outside its scope), or a silently wrong result in
    common use;
  - medium — a silently wrong result in uncommon use, a message that
    leads to the wrong fix, a 10× perf cliff on realistic input, a
    platform divergence a script would hit;
  - low — wording, cosmetics, rare-path inconvenience (BACKLOG only).
- **Report per finding:** id `H3-<area><n>`, severity, one-line claim,
  minimal repro (file + command), expected vs actual (exact output),
  platform, whether it's a regression since v0.0.74. End with what was
  probed and held, so the round's coverage is on record.
- **Swept areas** (hunt 1, hunt 2, the sweeps) are re-probed only for
  regressions from what landed since v0.0.74 (brief F).

## Operating rules (lessons of 2026-10-16)

- Worktrees and builds cut from main's landing commit, never an older
  branch.
- At most three heavy agents at once: the container caps pids at
  2048, and parallel dotnet builds/test hosts trip it into random
  EAGAIN failures. Brief D (perf) runs ALONE — load distorts timings.
- e2e only under `flock /output/wt/e2e.lock`; scratch under
  `/output/tmp/h3-<area>/`, never /tmp.
- A wall-clock failure under load is re-run alone before it's a
  finding.
- `/output` turning `ubuntu`-owned is the user taking the workspace:
  stop, report what was interrupted, resume after.
- No SSH: agents never push. Anything that needs CI (brief A) is
  handed to the user to push and run.

## Briefs

### A — Windows and macOS (via CI)

Every hunt so far ran on Linux; the macOS `timeout` polyfill bug and
today's Windows failures (resolved `sh.EXE` head, CRLF from fmt) hid
there. No Windows/macOS host here, so:

1. Write `tools/hunt3-xplat.weir`: one probe per claim, each printing
   `OK <id>` or `FAIL <id>: <detail>`, runnable on the release AOT
   asset. Cover:
   - paths: separators, drive letters, UNC, `~`, case-insensitive
     filesystems, `Path.*` on each;
   - line endings: scripts, heredocs, `fmt`, history, `from`/`to`
     adapters reading CRLF input;
   - process spawn: argv quoting on Windows (spaces, quotes,
     backslashes before quotes, empty args), `.exe`/`.cmd`/`.bat`
     resolution, PATHEXT, exit codes;
   - signals: Ctrl-C/Ctrl-Break on Windows, SIGTERM/SIGINT on macOS,
     `within proc` cleanup, `always`;
   - env: case-insensitive names on Windows, `within env` with both
     cases, `Env.vars`;
   - files: permissions/`chmod` semantics, symlinks, locked files,
     `within tmp` cleanup, `within lock`;
   - `serve` on http.sys (Windows): F8 truncation fallback, F11
     header refusals, loopback names, port-in-use wording;
   - REPL basics on Windows console: history location, `NO_COLOR`,
     Ctrl-D/Ctrl-Z.
2. Add a `workflow_dispatch` job (windows-latest, macos-latest,
   ubuntu-latest as the control) running it on the AOT build.
3. Hand the branch to the user to push and run; triage the logs they
   relay. A FAIL on Windows/macOS that passes on ubuntu is the finding.

### B — formats and generators

- **TOML:** round-trip and parse truth against a reference parser
  (Python's `tomllib` as the oracle — deterministic, not hand-checked):
  dates/times with offsets, inline tables, arrays of tables, dotted
  keys, Unicode escapes, integers at int64 edges, floats (`inf`,
  `nan`, exponents), duplicate keys, the toml-test corpus if
  reachable over read-only HTTPS.
- **`plan` / `Plan.*` in depth:** preview vs apply equivalence for
  every action kind, partial failure midway, nesting with `within`,
  `always` inside a plan, re-running an applied plan, a plan over a
  path that changes between preview and apply.
- **`weir add schema` / `gen types` beyond cycles:** JSON Schema
  `oneOf`/`anyOf`/`allOf`, `$ref` across files and to `#/$defs`,
  enums of mixed types, `nullable`/`type: [x, null]`, `format`,
  `additionalProperties` (true/false/schema), `patternProperties`,
  required vs optional, reserved-word property names, very large
  schemas (real-world: GitHub API, Kubernetes CRDs). For each: the
  generated types check, and a sample document reads back equal.

### C — concurrency under stress

`pmap`/`piter` + `within lock` + `within proc` + signals together:

- hundreds of iterations of mixed workloads, checking ordering
  guarantees, no lost or duplicated outputs, no orphaned children
  (`pgrep` by per-run marker), exit codes;
- SIGINT and SIGTERM delivered at random points (before spawn, mid
  `pmap`, inside `always`, during `within lock` acquire);
- cross-process `within lock` contention: N weir processes on one
  lock, a holder killed with SIGKILL (stale lock recovery);
- `serve` under concurrent clients at and above `maxConcurrent`,
  with streaming handlers whose producers raise;
- REPL: Ctrl-C during a `pmap` at the prompt, then continue.

Keep parallelism modest (pids cap); count, don't assume, child
processes.

### D — performance cliffs (run alone)

Scale inputs 10× and 100×; report the growth exponent (time and peak
RSS), not single timings. Anything superlinear on realistic input is a
finding; the two found so far were quadratic (positions, wide tables).
Targets:

- parser/assembler: long lines, deep nesting up to the limits, many
  statements, long heredocs, big `yaml` districts;
- checker: many bindings, wide records and unions, long `|>` chains,
  deep generic instantiation, many imports;
- runtime: `Seq` chains over 10⁶ elements, string building,
  `Map.add` loops, `show`/table rendering wide and tall;
- adapters: json/jsonl/yaml/toml/csv/xml documents at 1/10/100 MB;
- tooling: `fmt`, `check --json`, LSP hover/complete/format on a 20k
  and 200k-line file, REPL history at 100k entries.

### E — trust boundaries (security-minded)

- **argv:** splices with spaces, quotes, globs, a leading `-`, NUL,
  newlines; Windows quoting rides brief A.
- **Secrets:** a `Secret` must never appear in clear in errors,
  `show`, `--can`, LSP hover/diagnostics, `plan` previews, crash
  output, `check --json`, REPL echo or history.
- **HTTP client:** header injection, auth headers kept across
  cross-host redirects, the `insecure` flag's scope, proxies from env,
  response size and time bounds.
- **`serve`:** limits (body, headers, concurrency), malformed request
  lines, slowloris-shaped clients, a handler raising mid-stream.
- **Modules and packages:** import path traversal (`..`, absolute,
  symlinks), `weir add`/`restore` writing outside the project (the H5
  class), lockfile tampering, contracts.
- **tty output:** untrusted data containing ANSI/OSC escapes printed
  by `print`, `show`, error messages that echo input, the REPL — can
  data rewrite the terminal or the clipboard (OSC 52)?
- **files:** temp file permissions, `within tmp` cleanup on signal,
  writes through symlinks.

### F — regressions in what landed since v0.0.74

Re-probe only what changed: env sugar (`Env.need`/`getOr`, inline
`Env.load`/`Args.load` types), whole-document `Map` values,
assembly-error columns everywhere, recovery positions after unclosed
brackets, history with invalid UTF-8, fmt (parse gate, one final
newline, line endings, respace masks), `serve` F8/F11, Secret masking
in argv errors, the LSP analysis cache, `from xml` mixed content,
indirect re-enumeration warnings, `+` deferral, generic records at the
wire, `Yaml` fields through json. Edge cases and interactions, not
the happy paths the e2e cells already pin.

## Scheduling

Round 1: B, C, E, F in two waves of at most three (B+E+F, then C);
A in parallel with them (mostly authoring, then the user's CI run);
D alone at the end on a quiet machine. Then the coordinator:

1. de-duplicates and re-runs every high/medium;
2. writes `tools/hunt3-repro.weir` (one probe per finding, REPRODUCES
   on main) — the acceptance harness for the fixes;
3. drafts PLAN-hunt3-fixes.md with rulings owed, recommendation first;
4. moves lows to BACKLOG.

If round 1 finds no high/medium, W3 is done; otherwise round 2 after
the fixes land, over the same areas plus regressions from the fixes.

## Round 2 (2026-10-17)

Same method, severity and operating rules as round 1. Target: the AOT
binary from main @ ae61572 at `/output/tmp/weir-h3r2/Weir`; scratch
under `/output/tmp/h3r2-<area>/`. Round 1's findings and repros
(`/output/tmp/h3-findings.md`, `/output/tmp/h3-*/`) are the baseline: a
round-1 repro that still reproduces is a finding (fix incomplete).

What round 1 landed, so what can regress (the areas each brief re-probes
beside its own):

- the fixes (PLAN-hunt3-fixes Stages 1–3): tty sanitizing of printerr/
  Log/Self.prompt/`-e`, Secret masking in Args.load and header
  refusals, signal teardown (`always` spawns, pmap/piter stop, SIGHUP),
  pfirst cancel-and-join (R2) incl. the pipe-read cut, REPL `^C` cuts,
  env-key `=` refusal, owner-only temp files, plan guards (Http method
  classes, stale reads, writeAtomic, `within tmp`), gen types (int-or-
  string, integer enums, validation-keyword notes), JSON depth, TOML
  local date-time, Yaml duplicate keys, the three perf fixes;
- `serve` rebuilt on Kestrel (PLAN-kestrel): limits, pipelining, the
  listener's 404/505 answers, streamErrors, scope-close drain;
- the binary-size trims (PLAN-binary-size, `ILLink.Substitutions.xml`):
  stack-trace data, FSharp.Core resources, F# signature resources, the
  regex NonBacktracking engine and Kestrel HTTP/2-3 are gone — anything
  that printed, reflected or matched through them;
- the cross-platform fixes (Path.normalize at a drive root, Path.glob
  with a native prefix, the plan guard's path comparison).

Waves: B+E+F, then C, then D alone. A rides the user's dispatch of
`hunt3-xplat.yml` on main (ubuntu the control).
