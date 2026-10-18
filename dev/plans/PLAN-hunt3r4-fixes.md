# PLAN — fixing hunt 3 round 4 (H3R4-*)

Source: PLAN-hunt3r4.md "Black-box round 4". Findings, repros, rulings:
`/output/tmp/h3r4-findings.md` (repros under `/output/tmp/h3r4-<area>/`).
Target: main @ e932f24 (`/output/tmp/weir-h3r4f-base/Weir`).

7 high, 26 medium; C21 and C22 already fixed in #380. A (xplat) clean.
Severity rulings: C22 high, F3 medium, B8 and B11 low. Lows go to BACKLOG.

## Gates (every stage)

As PLAN-hunt3r3-fixes, plus: `tools/hunt3r4f-repro.weir --bin <aot>` —
the stage's probes OK, nothing newly failing; `tools/hunt3r4-repro.weir`
9/9, `hunt3r3-repro` 22/22, `hunt3r2-repro` 25/25 stay green.
Stage A also: the storm driver (single, double/triple-signal and `--tty`
storms) and the race-tree fuzzer at zero failures.

## Rulings (user 2026-10-18, all as recommended)

- **R1 (E5)** runtime errors escape the data they quote, as the echo
  does; weir's own diagnostic styling keeps its SGR.
- **R2 (C25)** `exit n` in a serve handler stops the scope (no new
  request, handlers cut and joined, cleanups whole), then exits n.
- **R3 (F5)** a cut `Self.prompt` never splices: no reader sees a line
  that wasn't written.
- **R4 (B2)** a plan's captured op checks its preconditions against the
  plan's simulated state and raises at capture.
- **R5 (B9)** `from xml` decodes per the declared encoding; an unknown
  one refuses.
- **R6** one cancellation-scope tree: every flow, wait and arm takes its
  cut from its parent scope; one owner runs cleanups LIFO; a second
  signal hard-exits with no concurrent sweep.
- **R7 (C1-4)** after the unwind weir dies by the signal (INT/TERM/HUP),
  scoped or not.
- **R8 (C1-2)** commands spawned inside an `always` run in their own
  process group: a terminal ^C cannot reach them.

## Stage A — the cancellation-scope tree (`fix/h3r4f-core`)

Design note first, reviewed by the user before implementation.

- **R6** the tree, replacing the per-feature tokens, marks and the
  abandon protocol (`flowCancel`/`fanCancel`/`flowBase`, `RaceGroup`
  parents, `armMarks`/`armOwner`, `liveAlways`/`armAlways` sweeps).
- **C1-1 (high)** a second signal hard-exits at once; no concurrent
  sweep, LIFO kept, one exit code.
- **C1-5** an abandoned arm's late cleanup is owned by its scope, never
  run on its own thread past the unwind.
- **R7 (C1-4)**, **R8 (C1-2, high)**.
- **D1** a scope's children leave it when they exit (no unbounded bag).
- **R2 (C25)**; **C23** a REPL ^C ends the whole entry; **C24** a cut
  `Self.stdin` before any read is not an enumeration; **R3 (F5)**.
- **C1-6** an inherited SIG_IGN SIGINT without a tty is reset at start.

## Stage B — safety (`fix/h3r4f-safety`)

- **E1 (high)** a proxy's refusal names the proxy without userinfo, no
  raw .NET text (`weir add` too); ResponseEnded from a proxy likewise.
- **E2** `Plan.preview`'s http send line renders escapes visibly.
- **E3** `weir add sig` prints and stores a tool's `--version`/help text
  sanitized.
- **E4** `NO_PROXY` domain entries cover subdomains (curl's rules, the
  trailing dot too).
- **R1 (E5)**.
- **C1-3 (high)** console EIO (a hung-up tty) never aborts: the spinner
  and every console write tolerate it, cleanups run whole, exit 129.
- **C1-7** docs: the SIG_IGN carve-out holds where job control is off.

## Stage C — formats, REPL (`fix/h3r4f-formats`)

- **B1** `to yaml` quotes `+.5`-style numbers, `y/Y/n/N`, `<<`, `=`.
- **R4 (B2)**, **R5 (B9)**.
- **B3** `anyOf [X, {type: null}]` drafts `Option<X>` in either order.
- **B4** a `$ref`'s sibling properties/required merge (2020-12) or note.
- **B5** deep in-document pointers and `#` resolve.
- **B6** integers beyond int64 in a `Yaml` node keep their digits.
- **B7** TOML/`Instant.parse` accept lowercase `t`/`z` and TOML 1.1
  seconds-less times.
- **B10** a YAML lone-surrogate escape refuses, as JSON does.
- **F1** the piped REPL puts every looked-ahead line back in order.
- **F2** `to yaml` writes a `None` pair value as null.
- **F3** bool/int-or-string `oneOf` with a `$ref` side drafts as inline.
- **F4** `File.*` resolves `..` after symlinks, as a shell does.
- **F6** an enum containing null drafts `Option<T>`; its note says null.

## Stage D — the nightly signal-stress job

`ci/storm/` from `/output/tmp/h3r4-C1/storm/` (driver, fixtures, client)
and a scheduled Linux workflow on main (+ dispatch): single-signal storms
gate now; double/triple-signal and `--tty` storms gate once stage A lands.

## Order

Harness first (`tools/hunt3r4f-repro.weir`, every probe REPRODUCES on
e932f24). Stages A, B and C in parallel (A: Session/Builtins/Proc/Eval
scopes/Repl/Serve; B: Http/Contracts/Script/Types console/docs; C: Yaml/
Toml/Xml/SchemaTypes/Plan/Repl lookahead/File paths); A's design note
reviewed before it codes. D after A. Merge B → C → A → D.
