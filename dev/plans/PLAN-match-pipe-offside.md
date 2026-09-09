# Plan — a trailing `|>` closes the match, the F# way (offside)

Status: PLANNED (2026-09-09). Implements the parked "real fix" named in
`dev/NOTES.md` (the `|`-vs-pipe resolution) and closes the unrecorded
fidelity gap the reference-review surfaced.

## The bug, in one shape

```
match b with
| Z d -> d()
| _  -> 20
|> print
```

Today weir keeps `|> print` INSIDE the last arm (`_ -> (20 |> print)`),
so the arms disagree (`d()` is `int`, `20 |> print` is `unit`) and the
match is a type error. F# CLOSES the match on that dedented `|>` and
pipes the WHOLE match. Weir diverges; the only escapes today are parens
or a `let` binding.

## The F# rule, measured (not recalled)

Verified with `fsy` against `match … | … -> d | … -> 20 <PIPE> printfn`,
arms at column 0, arm body at column 7, `|>` swept across columns:

| `|>` column vs the arm `\|` column | F# | weir today |
| --- | --- | --- |
| == arm column (dedented to the arms) | **closes the match**, pipes the whole | absorbed into the last arm (type error) |
| deeper than the arm body | stays in the arm body | offside REJECT ("indented off its siblings") |
| strictly between (the pattern column) | parse error | reject |

So F#'s discriminator is purely the physical column of the `|>` line
relative to the match's offside line (the arm `|` column):
**at-or-left → close; deeper → arm body.**

## Why weir gets it wrong — the architecture

Two layers (mapped in Parser.fs / Script.fs):

1. **The assembler** (`Script.fs:878` `assemble`) groups physical lines
   into logical lines BEFORE parsing. A pipe-headed line (`raw[0] = '|'`,
   Script.fs:1045) is an unconditional CONTINUATION at any indentation,
   so `| arm`, `| arm`, and `|> print` all join into ONE logical line.
   Each joined segment keeps its physical `(joinedStart, lineNo, indent)`
   in `LogicalLine.Segments` (Script.fs:841-876) — **the column is
   preserved**, recoverable via `translate` (Script.fs:1699).
2. **Pipe-alignment** [D:pipe-alignment] (`Script.fs:1367-1410`) tracks
   `PipeGroups: int list` — a `|`/`|>` line must sit EXACTLY on a group
   column; the first pipe after a non-pipe line opens a group. This is
   ONE mechanism shared by arm `|`, union-case `|`, command `|`, and
   pipeline `|>` — it cannot today tell a `|>` from a `|`.
3. **The parser** (`matchArm`, Parser.fs:1809) parses each arm body with
   the pipe-including `seqExpr`, so `20 |> print` is one arm body. The
   arm list stops only at `armBoundaryAhead` (Parser.fs:3499) — a
   lookahead for `| <pattern> (when|->)`. A `|>` fails that lookahead, so
   the body swallows it. The parser sees only LOGICAL positions; it
   cannot see the `|>`'s physical column mid-parse.

Root cause: the assembler treats a `|>` that lands on a `|`-group as a
sibling of the arms, and the parser has no signal to stop the arm body
before it.

## The rule we implement (F#-faithful, scoped to the reported case)

Give each pipe group a KIND — the piece that OPENED it:
`PipeGroups: (int * PipeKind) list`, `PipeKind = BarePipe | FwdPipe`
(arm/union/command `|` open `BarePipe`; a leading `|>` opens `FwdPipe`).

When the assembler meets a `|>` (forward-pipe) continuation at column C:

- **C matches (or is left of) a `BarePipe` group** — the `|>` is NOT a
  sibling of those `|`s. POP the bare-pipe group(s) down to C and emit a
  **close sentinel** at the join, then the `|>` text. The `|>` now binds
  to whatever construct those `|`s belonged to (the match), at the outer
  level. → F#'s "close the match, pipe the whole."
- **C matches a `FwdPipe` group** — ordinary pipeline sibling, unchanged
  (multi-line `x |> f |> g` keeps working; verified it does today).
- **C deeper than the enclosing group** — arm-body continuation. See the
  decision point below; the primary fix leaves this a reject.

`BarePipe`-vs-`BarePipe` alignment (real match arms, union cases) is
untouched — the strictness [D:pipe-alignment] deliberately keeps stays.

## Design — reuse the sibling-sentinel bridge [D:sibling-sentinel]

The assembler already injects sentinels the parser reads for structural
decisions. Add one for the pipe-close boundary:

1. **Script.fs** — carry `PipeKind` on each group; classify a piece as
   `FwdPipe` when it `StartsWith "|>"`, else `BarePipe` for a leading
   `|`. In the alignment step, when a `FwdPipe` piece lands on/left of a
   `BarePipe` group, choose a new join `JPipeClose` (a sentinel like the
   existing `JStmtSibling`/`JSibling`) instead of the plain pipe join,
   and pop the bare group(s). One sentinel constant, unproduceable from
   source (same discipline as the sibling sentinel).
2. **Parser.fs** — two touches:
   - The arm-list terminator: `matchArm`'s `many (str_ws "|" >>. …)` and
     the arm-body pipe loop must STOP at the close sentinel (treat it
     exactly like an arm boundary — the arm body ends, the arm list
     ends, the `EMatch` completes at the last real arm body).
   - The outer expression: after the sentinel, a normal `|>` operator
     token remains, so the top-level OPP applies it to the whole
     `EMatch` — free, that is already how `(match …) |> f` parses.
   The sentinel is consumed/skipped by the pipe operator's `ws`, so it
   never reaches an AST node; spans still translate (Segments intact).

No checker/eval/AST change — the result is the existing
`EApp(pipe, EMatch, …)` shape, which already checks and evaluates. This
is a PARSE-SHAPE fix only.

## Decision point — the deeper-`|>`-is-arm-body case (F# accepts, weir rejects)

F# lets a `|>` indented UNDER the arm body continue that arm across
lines. Weir rejects it today as an alignment error. Matching F# here
means RELAXING [D:pipe-alignment] for a `FwdPipe` that is strictly
deeper than its enclosing group — which partly undoes the deliberate
anti-typo strictness that decision bought (`reject off-by-one` vs F#'s
`warn-accept`).

DECIDED (2026-09-09): ship the PRIMARY fix only (the at-column close —
the actual reported bug and the surprising one) and KEEP the deeper case
a reject, recorded as a narrow, deliberate divergence (weir stricter;
the one-line form `| W -> 20 |> print` always works). The
[D:pipe-alignment] anti-typo strictness stays. Revisit only if a real
script wants the multi-line arm-body pipe.

## Edge cases / risks (each gets a test)

- **Nested match**: `PipeGroups` is a stack; a `|>` closes down to the
  matching column — inner arm column closes only the inner match, outer
  column closes both. Mirror F#.
- **Multi-line `|>` pipeline** (`FwdPipe` group): unchanged — verified
  `xs |> Seq.map … |> Seq.sum |> print` and misaligned-stage rejection
  both still hold.
- **Union-case decl** `type X = A | B` followed by a stray `|>`: not
  valid F# either; assert it still errors cleanly (no crash), context is
  type-position so no match to close.
- **Command-mode `|` pipe** (`git log | grep`) followed by `|>`: rare;
  the `|` group is `BarePipe`, so a `|>` at its column would close it —
  document and test the outcome.
- **`function | …`** (the implicit-match lambda, Parser.fs:2037): shares
  the arm machinery — the terminator change must cover it; add a twin
  test.
- **`when` guards, nested `let`/`fun` in arm bodies**: the sentinel is
  emitted by the assembler on layout, orthogonal to arm-body content.

## Tests

- Unit (Parser/Tests.fs): the reported shape now parses as
  `(match …) |> print` and typechecks; nested-match close at each level;
  `function` twin; multi-line pipeline still parses; union/command edges
  error-not-crash. Assert the AST is `EApp(pipe, EMatch …)`, not a pipe
  buried in the last arm.
- e2e: the exact reported snippet prints the whole-match result; the
  parenthesised form still works; a deeper `|>` still rejects (decision).
- Fidelity (`Pins.fs` + `divergences.md`): FLIP the shape to `Same`
  (weir and F# agree the trailing `|>` closes the match); remove the
  divergence row if one is added first. Add a pin for the KEPT deeper
  divergence (Diverges, documented).

## Ledger + docs

- New `[D:match-pipe-offside]` row in `docs/DECISIONS.md`: the at-column
  `|>` closes the match (F#-faithful), realized by a kinded pipe group +
  a close sentinel; the deeper case kept strict (cite
  [D:pipe-alignment]) as a narrow divergence.
- `docs/reference/*` (the pipe/match layout note) and the guide: state
  the rule — a `|>` under the arm body extends the arm; a `|>` back at
  the match column pipes the whole match.
- `dev/NOTES.md`: retire the "real fix is the offside rule — parked"
  note with the outcome.

## Verification gates (this is a parser change)

`weir check` the scripts; `dotnet build -c Release`; units; publish;
skill-doc; **fuzz 3×10k (mandatory — parser changed)**; the F# oracle
(`ci/fsharp-oracle.sh`) green with the flipped pin; e2e; then
`ci/commit-area.weir --commit HEAD` for the prefix (`parser` at least,
plus `checker`/`tests`/`docs` riding).

## Cost

Assembler kind+sentinel: ~15-25 lines (Script.fs). Parser terminator:
~10 lines (two stop-at-sentinel sites). Tests/ledger/docs: the bulk.
No checker/eval/AST surface. **~0.5-1 session.** The risk is entirely in
the assembler's group bookkeeping (nested + kind), which the fuzz and
the alignment tests must pin — the pipe-alignment machinery is
load-bearing for arms, union cases, command pipes, and pipelines all at
once, so every one of those needs a survivor test.
