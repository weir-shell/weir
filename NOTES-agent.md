# Agent dogfooding telemetry (append-only)

Protocol: skills/weir/SKILL.md + CLAUDE.md scripting policy. Graded
weekly: self-correction rate (stranded = failures), fallback ranking
(replaces the hatch-grep as roadmap input), prior-bleed catalog (feeds
skill lines and targeted hints).

## fallbacks

## stranded

## friction

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
