# weir — multi-line scripts: design gate (PLAN-modules-and-scripts Session 3)

Status: EXECUTED (landed 2026-07-17; a DESIGN gate record, not a
plan — the kill criteria were beaten in the gate session and the
implementation shipped with them). PARTLY SUPERSEDED in detail: the
assembler has since grown block lets (PLAN-block-let-cmd), bracket
stacks (PLAN-multiline-brackets), body-blank transparency
(PLAN-body-blanks — any blank-ends-statement reading here is
RETIRED), and multiline lambdas (PLAN-multiline-lambda). The core
decision — logical-line reconstruction, not expression-level
offside — stands.

## Kill criteria (written before prototyping)

1. Expression suite stays green with fewer than 150 parser-lines changed.
2. Command-mode interaction must not demand mode decisions inside
   continuation lines.
3. The startup timing guard must not trip.

## Decision: logical-line reconstruction, not expression-level offside

Indentation-based multi-line is implemented as a script-runner pre-pass
that assembles physical lines into logical statements:

- A statement head starts at column 0.
- A line with leading whitespace is a continuation of the current
  statement; it is joined with a single space.
- A line whose first character is `|` is also a continuation, regardless
  of indentation — no statement can begin with `|`, so this is
  unambiguous, and it admits both F#-canonical match arms
  (`| Some n -> ...` at column 0) and shell-style unindented pipeline
  continuations. (Gate finding: the indent-only rule missed canonical
  match layout on the first live script.)
- A blank line ends the current statement; an indented line with no open
  statement is an error ("continuation without a statement").
- Tabs in leading indentation are an error (deterministic indentation).
- The existing single-line parser then consumes each logical line
  unchanged. Zero grammar changes.

What this buys (the F# look, statement-level):

    let names =
        ls
        |> Seq.sortBy _.Size
        |> Seq.map _.Name

    type Verdict =
        | Pass of int
        | Fail

    match names |> Seq.tryHead with
    | Some n -> n
    | None -> "empty"

    git status --porcelain
        | from porcelain
        | Seq.length

What it deliberately does not buy (documented limitations, not bugs):

- Nested `let ... in` inside expressions still requires `in` (in-less
  nested lets are expression-level offside — out of scope; top-level lets
  are already in-less).
- Indentation does not delimit *scope* (no block bodies); it only
  continues statements.
- `let x = git status ...` remains expression mode (the existing
  "let-lines are expression mode" rule; command-mode-in-let-value is an
  orthogonal future decision, unchanged by this design).
- The REPL stays single-line (continuation prompts are a separate
  ergonomics feature).

## Interaction table (all no-ops by construction)

| Concern | Outcome |
|---|---|
| Mode decision | Per logical line head, exactly as today |
| Commit-to-command-mode | Per logical line, unchanged |
| Match-arm `\|` | Arm lines are continuations; the joined line is today's grammar |
| Lambda greediness | Within one logical line — status quo |
| Seq literals across lines | Continuations join; existing grammar |
| `#` directives / shebang | Column-0 physical lines, handled before assembly |
| Comments | Stripped per physical line before joining |

## Error mapping (the real cost)

Joined-line columns are not source columns. Each logical line carries a
segment table (joined-start, physical line, physical column); type-error
spans translate to physical `file:line:col`. FParsec *parse* errors report
against the logical line and are attributed to the head line (documented
limitation; parse errors on continuations show the joined text).

## Verdict

CONTINUE — criteria results: parser-lines changed: 0; expression suite:
green; mode decisions in continuations: structurally impossible; timing:
unchanged. The prototype was production-shaped at ~90 lines in Script.fs
(assembly + span translation), so Session 4 collapses into this session:
the implementation ships now, and the remaining Session-4 scope is only
future polish (REPL continuation prompts, parse-error column mapping) —
recorded on the parked list, driven by dogfooding.
