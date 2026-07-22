# Corpus re-mine report — dotnet/fsharp @ 5928e91 (2026-07-22)

PLAN-corpus-remine. The debt owed since the tuples session, paid with
four feature waves banked: tuples, literal patterns, composition, raw
strings. The extraction/filter pipeline is now a COMMITTED artifact
(tools/corpus-mine.py) — the first mine's filter died with its
session; this one is calibrated to its published denominator and its
wave-rejects are explicit, so the filter diff reads as language
growth.

## The denominators (the measure-removal lesson)

| | first mine | re-mine base (reconstruction) | re-mine wide |
|---|---|---|---|
| extracted | 4256 | 4253 | 4253 |
| weir-plausible | 78 | 76 | 102 |

The comparable set grew 34% (76 -> 102): 26 snippets whose shapes the
original filter excluded are now comparable — every one a free
fidelity verdict on machinery that shipped since.

## The two prize numbers

1. **ZERO GOLD holds over the widened set.** Weir accepts nothing F#
   rejects — the safe-direction claim survives first contact with
   tuple, literal-pattern, composition, and raw-string shapes it was
   never exposed to.
2. **Disagreements FELL 24 -> 18 while the set grew by 26**, and
   agree-accepts rose 4 -> 9: the four waves converted disagreement
   into agreement at scale. Every one of the 18 is bucketed below;
   the human-triage residue after naming is ZERO.

## Triage (all 18 fsharp-accepts-weir-rejects)

| snippets | bucket | status |
|---|---|---|
| f659750b, a03e7bf | no-anonymous-records + no-floats | existing rows, corpus-tagged |
| 91dce7d, bb2fe76 | no-elif | existing row — DEMAND upgrade (2 corpus hits + agent friction); top reopen candidate |
| 1df9606 | exhaustiveness-hard-error | existing row, corpus-confirmed |
| 56d739b, bbffe98 | no-record-update | NEW row — the headline absence (`{ r with F = v }`, incl. nested I.X) |
| e9b3c31, 236ee3c, 08b5dc6, 09e667e, 087c9b1 | column-zero-statements | NEW row — the assembly law vs F#'s uniform-indent tolerance (not the `///` comments, which weir handles) |
| 353c8e8 | ctor-pattern-scrutinee | NEW row — params are not typed FROM patterns |
| bd677c9, 9418435 | no-auto-members | NEW row — union testers .IsA/.IsCaseB |
| 7b2eba6 | record-field-comma-trap | NEW row — weir REJECTS the F# tuple-in-field trap (safe direction) |
| 5175178 | no-arrays | NEW row |
| 9cd6798 | no-access-modifiers | NEW row (`;;` in the same snippet is an fsi artifact) |

Prediction scorecard (the plan's on-record residue guesses): raw-edge
and tuple-comparison shapes did NOT survive the filter (predicted,
partially — none passed); the predicted >=1 unnamed absence from the
new rule families arrived as ctor-pattern-scrutinee; the biggest find
(no-record-update) was NOT predicted.

## Raw verdict report

# Corpus comparison report (102 snippets)

- agree-accept: 9
- agree-reject: 75
- weir-accepts-fsharp-rejects (GOLD): 0
- fsharp-accepts-weir-rejects: 18

## GOLD: weir accepts, F# rejects

## F# accepts, weir rejects
--- f659750b4cf7.snippet
let B = {| v = 9.3 |}
--- a03e7fbf44fd.snippet
let A = {| v = 7.2 |}
--- 9cd6798b7272.snippet
let internal original_submission = "From the first submission";;
--- e9b3c313d7cd.snippet
    /// <summary> Return <paramref/> </summary>
    /// <param> the parameter </param>
    let f a = a
--- 236ee3c6d005.snippet
    /// <summary> Return <paramref name="a" /> </summary>
    /// <param name="a"> the parameter </param>
    /// <param name="a"> the parameter </param>
    let f a = a
--- 08b5dc60f114.snippet
    /// <summary> Return <paramref name="b" /> </summary>
    /// <param name="b"> the parameter </param>
    let f a = a
--- 09e667e608ce.snippet
    /// <summary> F </summary>
    /// <param name="x"> the parameter </param>
    let f a = a
--- 087c9b1b7d28.snippet
    /// <summary> Return <paramref name="b" /> </summary>
    /// <param name="a"> the parameter </param>
    let f a = a
--- 353c8e8e8e44.snippet
type MyDU = A | B
let getnumberOutOfDU x = 
    match x with
    | A -> 42
    | _ -> 43
--- bd677c9934c0.snippet
type X = A | B

let c = A
let result = c.IsA && c.IsB
--- 941843599461.snippet
type MyUnion = CaseA | CaseB of int

let x = CaseA
let useA = x.IsCaseA
let useB = x.IsCaseB
--- 56d739b2d78b.snippet
type Inner = { X: int }
type Outer = { I: Inner }
let o = { I = { X = 1 } }
let o2 = { o with I.X = 2 }
--- bbffe988de00.snippet
type Model = { V: string; I: int }
let m = { V = ""; I = 0 }
let m1 = { m with V = "m" }

type R = { M: Model }
--- 91dce7d433a1.snippet
let x = 10
let y =
   if x > 10 then "a"
   elif x > 8 then "b"
   elif x > 5 then "c"
   elif x > 2 then "d"
   else "e"
--- bb2fe7669415.snippet
let x = 10
let y =
   if x > 10 then "a"
   elif x > 5 then "b"
   else "c"
--- 7b2eba658b8d.snippet
type Person = { Name : string * bool * bool }
let Age = 22
let City = "London"
let x = { Name = "Isaac", Age = 21, City = "London" }
--- 5175178c1d31.snippet
let x = [| 1, 2, 3 |]
--- 1df960697256.snippet
let f x =
    match x with
    | 0 -> 0
