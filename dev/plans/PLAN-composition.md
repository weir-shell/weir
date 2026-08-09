# weir — mini-plan: `>>` composition + the redirect hints

Status: EXECUTED (landed 2026-07-22) — as blessed: LANDED 2026-07-22 (proposed 2026-07-21). Origin: the loc.weir
receipt — a bare FParsec expectation dump on `ls >> x` confirmed the
specced-but-never-landed redirect hint, and the user's borderline
question ("composition vs bash redirection?") resolves on inspection:
the two are complementary across the mode boundary, not competing for
the token. One session shipped both.

Completion addenda (2026-07-22):
- **The oracle REFUTED the plan's precedence clause.** The plan
  decided both "F# precedence exactly" and "composition binds tighter
  than pipe" — FCS proved they contradict: F# parses `xs |> f >> g`
  as `(xs |> f) >> g` (shared infix class). The controlling clause
  (F# exactly) won: `>>`/`<<` sit at the pipe's level, and the gotcha
  hint teaches the TRUE direction ("'>>' and '|>' share precedence…
  parenthesize the composition: xs |> (f >> g)"). Same folklore-vs-
  compiler pattern as prefix minus; the verdict-visible pin
  construction did its job before implementation shipped wrong.
- Scheme-vs-desugar report: the checker-arm route (typeBinOp), typed
  like a builtin scheme — composition types through lambda params
  (`let both f g = f >> g` checks), constraints flow through. Eval
  composes in the eval/apply knot (binOp cannot reach `apply`).
- A latent premise fixed en route: the "piping into an operator"
  rejection assumed operators never yield functions; `>>`/`<<` are
  excluded so `xs |> (f >> g)` takes the general pipe arm.

## The resolution, stated once

- EXPRESSION position: `>>` is F# forward composition (`<<` backward).
  `ls >> x` fails there because `ls : seq<FileRow>` is a value, not a
  function — the type system catches bash-append muscle memory by
  itself; a targeted rule names the real spelling.
- COMMAND position: `>` and `>>` remain literal argv words (the
  standing safety-pin family), now WITH the targeted hints —
  "`>` does not redirect in weir; pipe to File.write:
  `cmd | File.write \"out.txt\"`" and the File.append twin.
- The stance line in SEMANTICS: weir routes streams by application —
  `>` means comparison and `>>` means composition everywhere they
  mean anything; redirection is File.write/File.append at the end of
  a pipe.

## Pre-made decisions (as landed)

- Both `>>` and `<<` (F#-parity symmetry; oracle referees both).
- F# precedence exactly, oracle-pinned — the pipe's level, per the
  refutation above; the shared-precedence gotcha is the NAMED pin.
- The Diagnose rules, three: command bareword `>` → File.write hint;
  command bareword `>>` → File.append hint; expression `>>` with a
  non-function LHS → composition-aware error with the File.append
  suggestion (checked BEFORE the RHS infers, so `cmd >> file` lines
  with unbound RHS get the redirect hint, not "unbound variable");
  a PIPE on the left gets the parenthesize hint instead.
- Bash-prior rows: redirect-argv (`>`/`>>` argv + warning) and
  no-heredoc (`<<` is composition; stdin feeding is `xs | into`).
- Products: precedence pin, constraints-through-composition pin,
  `(f >> g)` as a command splice hits the scalar rule, POSITIONS
  sweep (then/arm/list), command argv behavior re-pinned unchanged,
  adjacent `>` vs `>>` lexing pin.

**Done when** (all held): the loc.weir line produces the composition-
aware type error with the File.append hint instead of an FParsec
dump; command `>`/`>>` hint their File spellings; the precedence
truth is pinned; all green.
