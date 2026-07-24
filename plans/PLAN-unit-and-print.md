# weir — unit, print, and the statement rule

Status: EXECUTED (landed 2026-07-18) — as blessed: BLESSED (proposal 2026-07-18; advisor pass + amendment same day).
Amendment at proposal stage, recorded per decision-archaeology convention:
**the `sh`/`cmd` bare-application exemption (draft form 2) was removed
before implementation.** Rationale: (a) deciding whether `sh "x"` was
exempt required resolving `sh` to the real builtin — name resolution
inside a rule the plan itself demands be syntactic-never-type-directed;
the shadowing cliff (`let sh = fun s -> s in sh "hi"`) was the proof.
(b) No shell has such a form for a structural reason: bash has no
expression layer to escape from; weir's command mode IS the bash-
equivalent surface, and `sh` is a library function returning a value —
the governing sentence already says what happens to values. (c) The
exemption forced the runner to force-and-stream a lazy seq through a
special path outside the type system; removal makes bare `sh "x"` the
same check error as any unforced effectful stream — the `seq<unit>` trap
rule applied consistently. Migration is one suffix: `|> print`.

Trigger (unchanged from draft): bare expression statements currently
print shell-shaped — PowerShell semantics. PS is the cautionary tale:
implicit output is its worst bug class — any uncaptured value silently
joins the output stream, and a forgotten debug line corrupts a pipeline
three calls away. F# went the other way: output is explicit, discarded
values are flagged. Weir currently has the PS rule with types. This plan
removes it before user functions/modules make it a permanent bug class,
and lands `unit` so Part 2 (booleans) can adopt F#'s else-optional-when-
unit rule instead of retrofitting it.

Sequencing: before Part 2 of PLAN-read-booleans-overflow. Does not touch
the READ.md gate — checker additions are post-anchor addenda (see
Hygiene).

## Pre-made decisions

- DECIDED — **`unit` is a real type, F# semantics.** `TUnit` leaf type,
  `VUnit` runtime value, `()` literal (parsed before parens so `()` is
  not an empty-parens error), `unit` in type syntax. Equatable,
  trivially (`() == ()` is `true`). Excluded from the splice family
  (command args, interpolation holes) by construction — the scalar rule
  is str/int/bool and stays that way. No `()` pattern in `match` for now
  (parked; unit is irrefutable anyway). It is a leaf in `bind`/`occurs`/
  row machinery — no interaction with generalization, rows, or generics
  beyond another ground case.
- DECIDED — **The statement rule (the point of the plan): in scripts, a
  pure expression statement must have type `unit`.** Anything else is a
  check error before line one runs (check-everything-first does the
  enforcement for free): "this statement computes a `<ty>` and discards
  it — bind it, or pipe it to print". Stricter than F#'s warning FS0020,
  deliberately: weir's pitch is that scripts fail before effects.
  Special-cased text when the type is `seq<unit>`: suggest `Seq.iter` —
  the lazy-effects trap (`xs |> Seq.map print` never forces) becomes a
  targeted check error instead of a silent no-op. `let`/`type`
  statements stay silent as today. `#loose` does NOT loosen this —
  resolution mode and output semantics are different axes.
- DECIDED — **Exactly one exempt statement form: command-mode
  statements, including their `|`-chains.** (`git add -A`,
  `git branch | map trim | where ...`, `^ls`, `grep x f | complete`
  keep today's shell-shaped streaming output.) The exemption is decided
  by the parser alone — the mode decision already happened; no name
  resolution, no type direction, no checker involvement. Whether a line
  outputs is visible in its syntax and survives any refactor.
  Consequences, named so they are not rediscovered as bugs:
  - Bare `sh "x"` is a check error (discards `seq<string>`); the
    designated spelling is `sh "x" |> print` — forces, streams stdout,
    stderr passes through as always, nonzero raises at force. The line
    now says it produces output, which it does.
  - Effect-only sh lines (`sh "rm -rf build" |> print`) read slightly
    odd — printing nothing, forcing everything. Semantically exact,
    ergonomically noted: this is the first candidate *real demand* for
    the parked discard hatch if dogfooding hates it. Not pre-solved.
  - Install-then-use is barely affected: the install line is command
    mode (form-1 exempt); only expression-land `sh` uses need the
    suffix, and those lines want their output anyway.
  - Bare builtin `ls` (`seq<FileRow>`) in a script is an error —
    builtin `ls` is data, and data gets bound or printed; a script that
    wants a *listing* says `^ls` and gets the real program. This
    asymmetry (a command line may discard-and-stream, a pure expression
    may not) is inherent to being a shell, not a gap.
  The governing sentence, final form: **command-mode lines stream;
  every expression computes a value; values are bound or printed.**
- DECIDED — **`print` is a builtin with a bespoke checker rule** (same
  species as `to json`): its argument is either a scalar from the splice
  family — str, int any measure, bool, rendered by the one shared
  renderer (`scalarString`) — or `seq<string>`, streamed line-per-element
  with strict enumeration. Returns `unit`. Pipeable: `xs |> print`.
  Scalars via interpolation: `print $"status: {n}"` — no overloading
  machinery, the splice rule is the polymorphism. NOT command-callable:
  `echo` owns bareword ergonomics in command mode; `print` is the typed
  in-process form. The retired "shell-shaped statement output" rule
  becomes `print`'s rendering rule verbatim — `weir script | grep x`
  composes exactly as before, output is just explicit now.
- DECIDED — **`Seq.iter : ('a -> unit) -> seq<'a> -> unit`**, strict,
  qualified-only in both modes (same rule-shape as Option: bare names
  are the data plane; effects are not data). The `seq<unit>` error text
  points here.
- DECIDED — **`File.write`/`File.append` return `unit`.** Their
  string-path return was a stopgap because no unit existed (recorded at
  the time as "returns the resolved path" for lack of anything honest to
  return). With unit, a bare `File.write path lines` statement is
  well-typed and silent, as it should be. `File.read`/`File.exists`
  unchanged.
- DECIDED — **REPL and `-e` are untouched: auto-print stays.** That is
  FSI's `it` behavior, not the PS disease — REPL lines are ephemeral.
  Refinement: unit is invisible everywhere interactive — `print "x"`
  shows no `() : unit` trailer, `let x = ()` shows nothing, and bare
  `x` (bound to unit) also shows nothing. The statement rule is a
  *script* rule.
- DECIDED — **Interface to Part 2, stated now**: this plan only
  guarantees `unit` exists and is ordinary. The else-optional-when-
  then-is-unit rule is Part 2's to adopt (`if cond then print "dirty"`
  types without an else, F#-style); nothing here pre-implements
  conditionals.
- OPEN (default: park) — **discard escape hatch** (`ignore : 'a -> unit`
  or `let _ = ...`). With command statements exempt, `File.write`
  returning unit, and `print` available, no dogfooded statement needs a
  discard today. Adding `ignore` invites exactly the sloppiness this
  plan removes. Park until a real script produces the need — the
  effect-only-sh ergonomic above is the named first candidate; record
  the demand here when it arrives.

## Session 1 — unit, print, statement rule (one session, one branch)

1. `TUnit`/`VUnit`/`()`/`unit` tySyn; equatability; REPL display rule
   (unit invisible, all three boundary cases above pinned). Tripwires
   re-run — this touches the checker's ground-type set.
2. `print` checker arm + eval (streaming for `seq<string>`, shared
   scalar renderer); `Seq.iter` member.
3. Statement rule in the script runner's check layer: classify each
   statement (let/type | command-mode | pure expression), enforce unit
   on the last class. The classification consumes the parser's mode
   decision only — assert in the classifier that no name lookup occurs
   (the removed form-2 exemption must not creep back in as an
   "optimization"). Error texts including the `seq<unit>` hint and the
   bare-`sh` case (message names the `|> print` spelling).
   The classification function is one place, tested directly — it is
   the semantics of this plan.
4. Migration: `examples/repo-report.weir` (`"staged:"` → `print ...`;
   bare `match` → `|> print`; bare seq pipelines → `|> print`; any bare
   `sh` lines → `|> print`); e2e script battery entries that relied on
   bare-statement output; SEMANTICS.md scripts section rewritten around
   the statement rule (governing sentence above; the form-2 removal
   recorded in the decision-archaeology style); TRANSCRIPTION.md
   post-anchor addenda; NOTES.md.
5. e2e battery additions against the AOT binary:
   - a discarded-string script exits nonzero with zero effects
     (check-first pin for the new error class);
   - `print` streaming composes through a host pipe
     (`weir script | grep`);
   - **renderer adversarial case**: a seq containing empty strings and
     an element containing an embedded newline round-trips
     byte-identically vs the retired statement printer (this is where
     line-per-element implementations silently diverge — skip-empties,
     double-newline — and the `| grep` test alone cannot catch an extra
     blank line grep ignores);
   - the `seq<unit>`-hint error; bare `sh "x"` rejection naming
     `|> print`; bare builtin `ls` rejection naming `^ls`;
   - `sh "x" |> print` end-to-end: stdout streamed, nonzero exit raises
     at force (the migration spelling proven, not just named).
6. Timing pins re-verified (no reason to move; verify anyway).

**Done when:** repo-report produces byte-identical output through
`weir script | grep staged`; the renderer adversarial case is
byte-identical; the discarded-value and bare-`sh` scripts are rejected
at check time with zero effects (e2e-pinned); full suite + battery
green; timing pins hold.

## Parked (recorded, not forgotten)

- `()` pattern in `match`; unit-payload constructors get no special
  treatment (a nullary case is already the honest spelling).
- `ignore`/`let _` — see OPEN above; effect-only-sh ergonomics is the
  named first candidate demand.
- **`show`/record-print** — `print` takes scalars and `seq<string>`
  only; a `FileRow`, a `Completed`, an `Option<int>` have no printed
  form in scripts (the REPL renders them; `print` rejects them). Part 2
  will make `print`-as-effect the dominant script idiom
  (`if cond then print ...`), so debugging sessions will hit
  `print row` → check error → hand-built interpolation. Known collision,
  named now: the resolution is either `print` gaining the REPL renderer
  for records/unions, or a `show : 'a -> string` builtin — which raises
  the what-is-showable question (functions are not) and therefore waits
  for evidence. First dogfood complaint lands here, not in a surprise.
- `printerr` (stderr twin) — first script that needs diagnostics
  separated from output revives it; rendering rule would be `print`'s.

## Hygiene

- One session, one branch, merges alone — it changes what existing
  scripts mean (statement outputs), so nothing else rides along.
- Checker read: additions land as TRANSCRIPTION.md post-anchor addenda;
  READ.md scope stays exactly d12aefd. If READ.md lands first, its
  verdict is unaffected; none of the read-path arms (a–g) are touched —
  unit is a leaf and `print` is an isolated arm.
- The claim-vs-behavior discipline applies with force here: every
  DECIDED bullet above that names a behavior boundary (the single
  exemption form, `#loose` non-interaction, REPL invisibility, the
  bare-`sh` rejection, check-first rejection) gets a test at the
  boundary, e2e where the AOT binary is the claim.
