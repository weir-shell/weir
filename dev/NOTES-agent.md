# Agent dogfooding telemetry (append-only)

Protocol: skills/weir/SKILL.md + CLAUDE.md scripting policy. Graded
weekly: self-correction rate (stranded = failures), fallback ranking
(replaces the hatch-grep as roadmap input), prior-bleed catalog (feeds
skill lines and targeted hints).

## fallbacks
- 2026-07-29 | dedupe detector (maintenance sweep M2) -> awk | the
  token-window hash needs a 6-line sliding window with cross-file
  grouping; weir has no `Seq.windowed` (index-into-seq is the gap), so
  the normalized-line-window hash ran as awk over the 6 large src files.

## stranded

## friction
- 2026-07-24 | fuzz.sh -> fuzz.weir | stream-AND-reify has no
  spelling: `| orFail msg` swallows the chain's output (probed —
  the predicate-silence family), `| complete` captures it, so a
  long-running child whose LIVE output matters can only run bare
  (raise carries a generic located error; the reproduce-hint moved
  BEFORE the run). Also: no `$0` — the repo root rides
  `git rev-parse --show-toplevel` instead of dirname-$0.
  CLOSED same day [D:exit-reifiers]: orFail streams now, and
  `| exitCode` streams-and-binds; fuzz.weir carries the reproduce
  message in the orFail. The `$0` half CLOSED by [D:script-path]:
  `scriptPath` answers it; fuzz.weir keeps git-toplevel
  DELIBERATELY (it wants the repo root, not the script dir).

- 2026-07-23 | git-subrepo example | two reflex-errors while writing:
  `then`/`else` on their own lines at the `if`'s indent are SIBLINGS
  (offside law — correct, surprising mid-write); command lines inside
  function bodies need `run`/`cmd` — RETIRED same week for the
  param-ful-let half (PLAN-paramful-rhs); block lets inside bodies
  remain the receipt collector for the parked half. New: mid-word
  splices (`--file=$f`) pass literally — whole-argv law; caught by
  the flagship smoke during the rewrite.

- 2026-07-22 | git-subrepo translation exercise | no `Seq.fold` (now
  LANDED on this receipt), no multi-param lambdas (now LANDED), and
  ctor-pattern-scrutinee's first live receipt (a standalone result
  dispatcher; match moved to call sites — row tagged).

- 2026-07-21 | loc report | no `elif`: wrote it reflexively in a
  cascade; `else if`/match-with-guards cover it, but the parse error
  points at `then` rather than naming `elif`. Also no `Seq.last` —
  spelled `Seq.skip (n - 1) |> Seq.head` for the extension split.

- 2026-07-18 | skill-file authoring | let-RHS command mode: wrote
  `let files = git ls-files` reflexively; the doc-test rejected it on
  first run. The parked let-RHS extension now has agent evidence.
- 2026-07-18 | test-count report | no Seq.pairwise (or indexing): could
  not compute deltas between adjacent commits in-pipeline; printed
  absolute counts instead.
- 2026-07-18 | test-count report | blank line inside a lambda block
  ended the statement; the "this let needs a body" error is accurate
  but does not name the blank line as the cause. Self-corrected in one
  iteration; a "(a blank line ends the statement)" suffix would have
  made it zero.
- 2026-07-18 | test-count deltas rewrite | record literals need a
  declared nominal type; wrote `{ Label = ...; Count = ... }` expecting
  F#-anonymous-record inference. Error was exact ("no declared record
  has exactly the fields"); self-corrected in one iteration.
- 2026-07-18 | FIXES SHIPPED for the day-zero entries: let-RHS command
  mode (with an `in`-bareword stop closing the silent-argv cliff),
  Seq.pairwise (Pair<'a> record, Group precedent; tuples deferred to an
  evidenced plan — see NOTES.md), blank-line error now names its cause.
- 2026-07-18 | FIXES SHIPPED round 2: `fail "reason"` (located error,
  exit 1), `printerr` (stderr twin), and the |>-vs-operator precedence
  trap is now a targeted check error naming the parenthesize fix.
- 2026-07-20 | nu-script translation (jira-branch) | three frictions:
  (1) no Seq.contains/exists — flag check spelled where+isEmpty+not;
  (2) no Seq.nth/skip — second tab field extracted via
  Seq.pairwise |> Seq.head |> .Snd, cute but obscure;
  (3) conditional multi-effect block — no ESeq, so effects sequence as
  named let-bound `completed` calls with a summed ExitCode check.
  Workable idiom, but this script is direct evidence for backlog #0.
  Untested here: fzf interactivity (draws on /dev/tty with piped
  stdio — expected to work in command mode; no fzf in container).
- 2026-07-20 | jira-branch acceptance rewrite | multi-line record
  literals lose field separators in assembly (F# separates by newline;
  weir joins with a space) — trailing `;` on each field line is the
  spelling. Candidate: record-field `;`-insertion, same technique as
  Session 2's sibling rule but a distinct context. Logged, not
  improvised.
- 2026-07-20 | jira-branch, real terminal | fzf interactivity VERIFIED
  (parked item discharged): ran, drew UI, returned cancel. Weir raised
  at the cancel (exit 130) before any git mutation; the nu original
  marched past it into `git switch -c ''` — error-at-a-distance, the
  exact failure class weir's raise-at-force kills. Verdict: weir more
  correct. NEW friction: graceful-cancel has no spelling — `| complete`
  is single-segment, so the exit of a chain's LAST stage cannot be
  reified (want: tolerate cancel, exit 0 silently). Candidate shapes:
  chain-level complete, or completed-with-stdin. Logged, not
  improvised.
  PARTIALLY CLOSED [D:exit-reifiers]: the code-as-data half is
  `| exitCode` + match (`| 130 -> ...`); the selection-AND-code half
  stays `| complete` (both are data there — the captured cell).
- 2026-07-20 | nuget http-get translation | TWO receipts in one line:
  (1) env-var access — the launch-day predicted gap arrived as a user
  task; Env.get shipped same session (Option<string>, no $NAME
  expansion — interpolation is the spelling). (2) the comment stripper
  ATE a bareword URL at `//` — fixed to whitespace-boundary comments
  (F# divergence row comment-boundary, oracle-pinned). Translation
  verdict: weir's header spelling (interpolated argv strings) beats
  nu's bracket-list header syntax; curl replaces the http builtin.
- 2026-07-20 | F# bicep-script translation (deploy/snapshot/quality)
  | The dbt plan{} discovery half was NOT translated (user waiver:
  "do not rewrite dbt"); everything else runs, verified with stubbed
  az/bicep/curl including the OIDC login path (curl | from json Oidc
  typed the token fetch cleanly). Receipts, strongest first:
  (1) shEnv — per-target env files around child processes had to be
  spelled `sh -c "set -a; . file.env; cmd"`. The typed boundary drops
  to bash EXACTLY where the F# original had `shEnv (p.LoadEnv())`.
  This is the arrived receipt for parked Env.set — but the shape the
  script wants is child-env injection (`Env.with`?), not session
  mutation. Plan-worthy.
  (2) use!-disposal (azure logout) — no try/finally means
  cleanup-on-error has no spelling; logout runs on the success path
  only. Second entry in the error-handling ledger after chain-tail
  exit reification.
  (3) `exit code` — fail is exit-1 only; the original propagated
  specific codes from Errors.
  (4) `App of stack * env` (two-value CLI option) — reshaped to
  --stack/--env flags; Args has no multi-value options (and no
  subcommand notion; args[0] matching was fine).
  (5) multi-line record separators struck AGAIN (double-`;` when the
  sibling rule joins field lines that already end in `;`) — candidate
  count now 2. Workaround: one logical line via continuation indent.
  (6) greedy-`;` pulled a record sibling into `if ... then fail`
  inside a block let — the named divergence's first real bite;
  restructured to top-level statements. FIX SHIPPED this session for
  the blocker found first: function bodies of effect lines
  (`let quality t =` + sibling commands) failed to parse — seqExpr
  was missing from the let-RHS and let-in value positions (3 pins).
  (7) weir fmt refuses the translated script: minimal repro is a
  multi-line if/else INSIDE a function body (`let f t =` over an
  indented if-block) — the reformat changes the parse and the safety
  check correctly leaves the file unchanged (exit 3). Formatter bug,
  new since function bodies can sequence; needs a fix session.
- 2026-07-20 | FIXES SHIPPED for the bicep receipts (grammar
  consolidation session): the offside close (kills receipt 6 AND the
  silent conditional-swallow found during review), multi-line record
  continuations (receipt 5, both spellings), Exit.code (receipt 3),
  fmt if/else roundtrip (receipt 7, general indent-level model), and
  the cleanup idiom documented in GUIDE (receipt 2's answer — a
  finally-shaped feature stays parked pending repeat receipts against
  the idiom). Still open: shEnv/child-env (receipt 1, own plan),
  two-value CLI options (receipt 4, unranked).
- 2026-07-20 | FIX SHIPPED for the strongest bicep receipt: child-env
  injection (cmdEnv/runEnv + Env.fromFile, overlay semantics). The
  sh -c "set -a; . file" spelling survives only as the ESCAPE for
  lines that genuinely need shell evaluation — the boundary error
  names it. Two-value options formally parked (idiom documented in
  GUIDE). All bicep receipts now dispositioned: 1 shipped, 2 GUIDE
  idiom, 3 shipped (Exit.code), 4 parked-with-idiom, 5-7 shipped in
  the consolidation session.
- 2026-08-15 | adversarial-repro.weir | writing the review's repro
  harness hit F6 (PLAN-adversarial-review): a block `let` with a
  command RHS keeps command mode OFF the topLet spine, but the
  reifier marker does not survive there — `let r = sh -c "…" |
  complete` inside an if-body, a within-body, or a lambda body
  re-reads `complete` as a PATH program, runs whatever has that name,
  and pipes the command's stdout into it. The harness is shaped
  around it (every command-running helper hoisted to a top-level
  function). The diagnostic when nothing is on PATH — "install the
  tool" — is what sent the first attempt looking for a missing
  binary rather than at the position.
