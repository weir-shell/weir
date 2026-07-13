# weir — prompt ergonomics: command-callable builtins, diagnostics, CI, complete/collect

Follow-up to PLAN-command-mode.md (complete, 222 tests). Driven by two dogfood
findings: `ls -la` parses as subtraction, `cd /work` parses as division — both
correct consequences of "builtin head → expression mode," both wrong at a
prompt. Plus accumulated hygiene debt (CI) and the two sibling builtins
(`complete`, `collect`).

Session order is dependency order: Session 1 is pure hygiene and merges alone;
Session 2 changes the mode decision (the security boundary — human read
required); Session 3 is independent of 2 but shares the rules-doc.

## Pre-made decisions (do not relitigate mid-session)

- **Command-callable builtins**: a flagged subset of builtins may head a
  command-mode line. Initial set: exactly `cd`. Members are added deliberately,
  one per demonstrated need — never wholesale.
- Mode decision becomes: head is command-callable builtin → command mode,
  desugaring to the builtin call; head is any other binding/builtin/keyword →
  expression mode; head hits PATH → external command mode; `^prog` forces PATH.
  Everything else falls through to expression parsing (conservative-by-
  construction is preserved).
- `cd` semantics: bare `cd` → `$HOME`. `~` and `~/...` expanded **by the cd
  builtin itself** — this is cd-local behavior, NOT general tilde expansion,
  which stays on the exclusions list. Rules-doc must say this explicitly (the
  next confusion is "cd expands ~ so surely echo does").
- Bareword args to a command-callable builtin arrive as string literals; `cd`
  keeps type `string -> string`; arity is a check-time error ("cd takes at most
  one argument").
- `grep`-style nonzero exits: default stays raise-on-force. The opt-out is a
  `complete` builtin reifying the outcome as a record — NO per-command
  allowlist (unbounded, and wrong: grep's 1 is no-match but 2 is a real error).
- `collect`: force-once materialization. `pwd`/`ls` stay live queries; binding
  them binds the query, not the answer; `collect` is the snapshot operator.
- Diagnostics: one shared mechanism for the muscle-memory cliffs, not per-case
  hacks (see Session 2, item 4).

## Session 1 — CI + error-hint hygiene (merges alone, before anything else)

1. **GitHub Actions workflow**: `dotnet test` (full suite), AOT publish, then
   the e2e battery from command-mode Session 4 against the AOT binary. The
   suite is 222 tests and already produced a parallelism flake caught only by
   a manual 3x run — that is the workload CI exists for. Keep the
   `testSequenced` groups; add a workflow step that runs the suite twice
   (cheap flake detection).
2. **Startup-time regression guard**: pin the two medians from Session 4
   (expression line ~6ms, `echo hi | first 1` ~14ms) as a CI check with a
   generous threshold (e.g. fail above 2x). Timing already regressed silently
   once (the +10ms PATH-enumeration tax); make the next one loud.
3. **Did-you-mean cap** verified <=2 everywhere it fires (was verified for
   PATH hints; confirm the unbound-variable path shares the capped helper).

**Done when:** push triggers test + AOT + e2e green; a deliberate 3x slowdown
in a scratch branch fails the timing step.

## Session 2 — command-callable builtins + shared cliff diagnostic

The mode decision changes; it is the security boundary between weir semantics
and PATH execution. Human line-read of the mode-decision function after this
session, and the tripwire suite re-run explicitly (checker gains the
builtin-desugar arm — second checker change since the audit).

1. **Mode decision gains one arm** (in `commandSegment`'s head function, over
   the injected Resolver): command-callable builtin at head → command mode.
   Resolver interface gains `IsCommandCallable`; the fake-PATH unit tests gain
   the builtin cases.
2. **Desugar**: command segment with builtin head → the builtin's checked call
   node with bareword args as string literals (NOT `ECmd`). Splice rule
   (`$name`, `(expr)`) reused unchanged. Segments still split on `|`/`|>` and
   re-enter the mode decision, so `cd /work | ...` composes if ever sensible.
3. **`cd` completion**: bare `cd` → `$HOME`; `~`-expansion inside the builtin;
   relative paths against `Session.Cwd`; runtime error on nonexistent dir must
   print the **resolved absolute path** it tried (`cd /wrok` now parses fine
   and fails at runtime — shell-normal, but the error must show what was
   resolved).
4. **Shared cliff diagnostic** — one function, covering both dogfood findings
   and future members: when expression parsing fails AND the head is a known
   name (builtin or binding) AND the tail looks command-invoked (bareword,
   flag, or path token follows), the error becomes a targeted hint:
   - `ls -la` → "`ls` is a weir builtin and takes no flags; use `^ls -la` for
     the external, or `ls |> ...` to pipe the builtin."
   - future non-command-callable builtin with a path arg → "use quotes:
     `foo \"/path\"`, or `^foo` for the external."
   Test battery: both original findings verbatim, plus a binding shadowing a
   PATH name.
5. **Tests**: `cd /work`, `cd ..`, `cd ~/src`, bare `cd`, `cd $dir` splice,
   `cd a b` arity error at check time, `cd /nonexistent` runtime error with
   absolute path, expression-mode `let d = cd "/tmp"` unchanged, `^cd` — decide
   and pin: PATH `cd` doesn't exist as an external on most systems, so `^cd`
   should be a parse-time command-not-found (verify, don't assume).
6. **SEMANTICS.md**: "Command-callable builtins" subsection — the set (cd),
   the head-position-privilege rule (command-callability never leaks into
   expression checking beyond the desugar), cd-local `~` with the explicit
   non-generalization warning, and the governing note: *the command-callable
   set, cd-local expansion, and `|` aliasing are case law; if the set grows
   past a handful, stop and write the general line-head grammar philosophy as
   a rules section instead of accreting cases.*

**Done when:** `cd /work`, `cd ..`, `cd ~` work bare at the prompt; `ls -la`
produces the hint, not a subtraction error; expression suite zero regressions;
tripwires green; mode-decision function read by a human.

## Session 3 — `complete` and `collect` (siblings: reify stream/outcome as value)

1. **`collect : seq<'a> -> seq<'a>`** — forces the source exactly once,
   materializes, re-enumeration replays the materialized values with no
   re-run of effects (no re-spawn). Pin with: the Session-2-era liveness test
   inverted (`let p = pwd | collect in let d = cd "/tmp" in p` → original
   dir), and a `cmd`-spawn-count test (side-effecting source enumerated twice
   after collect → one spawn).
2. **`complete`** — applied to an external-command stream, returns a record
   `{ ExitCode: int; Stdout: seq<string>; Stderr: seq<string> }` (declare the
   record type as a builtin-owned nominal type). Semantics: forces the
   process to completion, never raises on nonzero exit — the exit code is
   data. `grep nomatch file | complete` → `ExitCode = 1`, empty Stdout, no
   raise. Stderr capture is new plumbing: until now stderr passed through to
   the terminal; `complete` redirects and captures it. Decide and pin:
   non-`complete` streams keep passthrough stderr (document as the default).
3. **Interaction rule**: `complete` binds to the nearest upstream external
   segment (it consumes the process handle, not just the lines). Typed rule:
   argument must be a stream backed by an external process — applying it to
   `[1;2;3]` is a check-time error ("complete requires an external command
   stream"). This needs the checker to distinguish process-backed seqs; if
   that type distinction is too invasive, fallback design (decide in-session,
   document either way): `complete` as a variant of `cmd`/command-mode suffix
   rather than a generic builtin.
4. **Rules-doc entries**:
   - `sh`/`cmd` exit behavior default + `complete` opt-out, cross-referenced
     to the grep dogfood finding.
   - "`pwd` (like `ls`) is a live query; binding it binds the query, not the
     answer; snapshot via `collect`."
   - The splice defaulting rule ("unresolved argv-position types default to
     string") gains its soundness condition in writing: harmless *because*
     command segments exist only at top level and cannot occur under a
     generalizing `let` — cite the "expression mode never flows back into
     command mode" exclusion as the guard.
   - Known seam line: `Session.Cwd` is ambient mutable state; `testSequenced`
     is the symptom-level fix; any future daemon/concurrent story reopens it.

**Done when:** grep-no-match is expressible without a raise; double-spawn
surprise has its documented escape hatch; both new builtins in SEMANTICS.md
with cross-references.

## Deliberately NOT in this plan (exclusions list unchanged)

Globs, redirects (`>`), env-var assignment prefix, `&&`/`;` chaining, general
tilde expansion, flag-parsing for weir builtins (`ls -a` semantics), stderr
in pipelines beyond `complete`'s capture. Each is a chosen future decision;
dogfooding frequency data (the `sh`-escape-hatch grep) decides order.

## Claude Code hygiene

- One session per branch; Session 1 merges alone and first.
- Human-read targets: Session 2's mode-decision arm (security boundary) and
  Session 3's process-backed-stream type distinction if taken (first new
  type-level distinction since the audit).
- Tripwire suite re-run explicitly in Sessions 2 and 3 (both touch the
  checker).
- After merge: resume the dogfooding task list from the original battery;
  collect the week-one pain list (`grep sh ~/.weir_history` + times-left-weir
  notes) before planning anything further.
