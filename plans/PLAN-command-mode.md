# weir — process lifecycle fix + sh/cmd split + command mode

Status: EXECUTED (landed 2026-07-12).

Three work items, strictly ordered: the bug fix first (it's live and silent),
the primitive split second (command mode's runtime target), the parser mode
last (biggest, depends on both).

## Pre-made decisions (don't relitigate mid-session)

- Binding/builtin shadows PATH; `^prog` forces external.
- `$name` and `(expr)` splice expression values into command-mode args; spliced
  values go as single argv entries (never re-split, no injection class).
- `|` is accepted as `|>` in command mode.
- Bareword terminators: whitespace, `|`, `|>`, `)`, end of line. Quoting:
  `"..."` and `'...'` produce single args.
- `sh` = deliberate POSIX escape hatch (string → `/bin/sh -c`). `cmd` = direct
  exec, zero expansion, weir owns (prog, args).
- Command-not-found is a check-time error (PATH lookup during checking), with
  did-you-mean capped at edit distance ≤ 2.
- `cd`/`pwd` are builtins mutating `Session.Cwd`; all process spawns use
  `Session.Cwd` as working directory.

## Session 1 — kill the orphan bug (do first, it's shipped)

- Reproduce: test spawning a compound command (`yes | cat`) via the current
  sh-backed path, take 3, assert via pgrep-equivalent that no `yes` or `cat`
  survives. Expect red.
- Fix: process-tree kill — .NET `Process.Kill(entireProcessTree: true)` in the
  existing try/finally teardown; verify it works for both the exec-optimized
  simple case and the compound case.
- Tripwire: keep BOTH tests (simple + compound) with a comment: the simple case
  passes even without tree-kill due to sh's exec optimization; the compound
  test is the real guard. Removing sh-backing changes this analysis — see
  Session 2.
- Same session, adjacent check: zombie reaping — a completed-but-unwaited child
  shouldn't accumulate as `<defunct>` across many pipeline runs. Add a
  loop-50-commands test asserting no defunct children.

Done when: compound-command teardown is clean under partial consumption, both
tripwires green, run in CI.

## Session 2 — split sh and cmd

- Rename current builtin → `sh : string -> Seq<Str>`. Unchanged semantics,
  documented as the escape hatch. History/tests referencing `cmd "..."`
  migrate.
- New `cmd : string -> List<string> -> Seq<Str>` — direct Process.Start, no
  shell: program resolved against PATH + Session.Cwd-relative, args passed as
  argv vector, `UseShellExecute=false`, working dir = `Session.Cwd`.
- Spawn audit: grep every `ProcessStartInfo` construction; assert working
  directory comes from Session (test: `cd` to a temp dir, `cmd "pwd" []`
  returns it — this also forces item 4).
- Builtins `cd` (arg optional → `$HOME`; handles `~`, `..`, relative paths;
  errors on nonexistent) and `pwd`. `cd` mutates `Session.Cwd` only — never
  `Environment.CurrentDirectory` (AOT/global-state hygiene, and it keeps the
  shell honest under future concurrency).
- Lifecycle tests from Session 1 duplicated against `cmd` (no sh in front —
  the exec-optimization analysis doesn't apply; tree-kill must hold on its
  own).
- Rules-doc entries: `sh` vs `cmd` semantics, the (prog, args) ownership line,
  cross-reference to the lifecycle tripwires.

Done when: `cmd "echo" ["*"]` prints a literal `*`; `sh "echo *"` globs;
`cd /tmp` then `cmd "pwd" []` → `/tmp`; injection test
(`cmd "echo" ["; rm -rf x"]`) emits the string, executes nothing.

## Session 3 — command-mode lexer/parser

- Mode decision at line head: first token is an identifier → resolve against
  (bindings ∪ builtins ∪ keywords); hit → expression mode (today's path,
  unchanged); miss → PATH lookup; hit → command mode; miss → unbound-variable
  error with the ≤2 edit-distance cap fixed (session includes that one-liner).
- Command-mode grammar: `head bareword* ( (| or |>) rest )*` where `rest`
  re-enters mode decision (so `git log | where ...` flows
  external→expression). Barewords: anything until a terminator; quotes make
  single args; `$name` splices a binding (must be Str or stringable scalar —
  checker enforces); `(expr)` splices an expression result under the same
  rule; `^prog` at head forces PATH even if shadowed.
- Desugar: command segment → `ExternCmd(prog, args)` AST node → checked as
  `Seq<Str>` → evaluated via Session 2's cmd machinery. No new runtime —
  parser and checker work only.
- Check-time PATH resolution caching: stat PATH entries per line, not per
  keystroke; note staleness rule (re-resolve each submission — a
  `brew install` mid-session must be visible).
- Parser test battery: `git status`, `ls -la`, `grep "a b" file`,
  `git checkout $branch`, `echo (1 + 2)`, `^ls`, `git log | first 5`, mixed
  `ls | where (fun ...)`, a bareword containing `/` and `.` and `-`, quoting
  edge with embedded quote.
- Deliberate exclusions this session (documented, not forgotten): no globs, no
  redirects (`>`), no env-var assignment prefix (`FOO=1 cmd`), no `&&`/`;`
  chaining in command mode. Each is a rules-doc "not yet" line — they're the
  next dogfooding cliff, and they should be chosen, not improvised in a parser
  session.

Done when: the two lines that started this — `cd` and `git status` — work bare
at the prompt, plus the battery above; expression mode has zero regressions
(full existing suite green).

## Session 4 — integration + dogfood re-entry

- End-to-end on the AOT binary: `cd repo`,
  `git status --porcelain | from porcelain | where (fun c -> c.Staged)`,
  `^ls`, splice test with a bound variable.
- Re-measure `-c` startup with the PATH-resolution addition (should be noise;
  pin the number).
- Update SEMANTICS.md: command mode rules, mode-decision algorithm, splice
  typing rule, the exclusions list, tripwire cross-refs.
- Return to the dogfooding task list — the original tasks now legible as
  actually typed at a shell.

## Claude Code hygiene

- One session per branch; Session 1 merges alone and immediately (it's a
  shipped-bug fix, don't couple it to the feature).
- Session 3's parser is the one to review by behavior battery rather than
  line-read — grammar code is tedious but its failure modes are all visible in
  tests, unlike the checker.
- The mode-decision function and the splice typing rule are the two spots worth
  a human read: mode decision because it's now the security boundary between
  "weir semantics" and "PATH execution," splice because it's a new checker rule
  (small, but it's the first checker change since the audit — re-run the
  tripwire suite explicitly).
- Estimate: sessions 1–2 ≈ a day; session 3 ≈ 1–2 days; session 4 ≈ half.
  [guess] the parser battery grows 2× during session 3 as edge cases surface —
  that's normal, let it.
