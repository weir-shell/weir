module Pins

// Fidelity pins: weir snippets tagged with the oracle expectation.
//   Same          — F#'s verdict must MATCH weir's (translated snippet)
//   Diverges id   — verdicts must DIFFER, and id must name a divergence
// Untranslatable pins are EXEMPT with a reason (forced translation is how
// oracle bugs masquerade as fidelity bugs) — none yet.
// Each snippet is a complete script; value results are let-bound so the
// statement rule never muddies a pin that is not ABOUT the statement rule.

open Expecto
open Oracle

type Tag =
    | Same
    | Diverges of string

type Pin =
    { Name: string
      Weir: string
      Fs: string option // None = identical text
      Tag: Tag }

let private pin name weir tag =
    { Name = name
      Weir = weir
      Fs = None
      Tag = tag }

let private pinT name weir fs tag =
    { Name = name
      Weir = weir
      Fs = Some fs
      Tag = tag }

let pins =
    [ // --- block lets (the incident cluster: drift risk concentrated here) ---
      pin "block let, implicit in" "let x =\n    let a = 1\n    a + 1\n" Same
      pin "nested block let with RHS spill" "let x =\n    let a =\n        1 + 2\n    a * 2\n" Same
      pin
          "valid match arms sit deeper than the binding (with a guard)"
          "let category =\n    match 3 with\n    | s when s > 2 -> \"big\"\n    | _ -> \"small\"\n"
          Same
      pin
          "F#-rejects-this: dedented arm inside a block"
          "let r =\n    let v =\n        match 3 with\n| _ -> 0\n    v\n"
          Same
      pin
          "F#-rejects-this: arm at exactly the binding indent"
          "let r =\n    let v =\n        match 3 with\n    | _ -> 0\n"
          Same
      pin "F#-rejects-this: bodyless block let" "let x =\n    let a = 1\n" Same
      pin "comment lines are transparent inside blocks" "let x =\n    // note\n    let a = 1\n    a + 1\n" Same
      pin
          "blank line inside a block ends the statement (weir only)"
          "let x =\n    let a = 1\n\n    a + 1\n"
          (Diverges "blank-line-ends-statement")

      // --- let ... in ---
      pin "explicit let-in one-liner" "let y = let x = 1 in x + 1\n" Same

      // --- conditionals and matches ---
      pin "if-then-else expression" "let v = if 1 > 2 then \"a\" else \"b\"\n" Same
      pin "else-if chain" "let v = if 1 > 2 then 1 else if 2 > 3 then 2 else 3\n" Same
      pin "bool patterns" "let v = match 1 > 2 with\n        | true -> 1\n        | false -> 0\n" Same

      // --- interpolation ---
      pin "interpolated string with a hole" "let s = $\"a{1 + 1}b\"\n" Same
      pin "brace escapes in interpolation" "let s = $\"x{{y}}z\"\n" Same

      // --- ranges ---
      pin "basic range" "let r = [1..5]\n" Same
      pin "stepped descending range, spaced" "let r = [10 .. -1 .. 1]\n" Same
      pin "F#-rejects-this: open range" "let r = [1..]\n" Same
      pin "F#-rejects-this: triple-dotted range" "let r = [1..2..3..4]\n" Same

      // --- named divergences, refereed from both sides ---
      pinT "equality spelling: == vs =" "let b = 1 == 1\n" "let b = 1 == 1\n" (Diverges "double-equals")
      pinT "binding-only =: F# equality rejected by weir" "let b = 1 = 1\n" "let b = 1 = 1\n" (Diverges "double-equals")
      pin "tuple literal" "let p = (1, 2)\n" (Diverges "no-tuples")
      pin "starred union payload" "type T = A of int * string\n" (Diverges "single-payload-unions")
      pin "discarded value statement" "\"orphan\"\n" (Diverges "statement-rule")
      pin "block comment" "(* block *)\nlet x = 1\n" (Diverges "line-comments-only")
      pin "printfn" "printfn \"hi\"\n" (Diverges "no-printf-family")
      pin "mutable binding" "let mutable x = 1\n" (Diverges "no-mutation")
      pin "let rec" "let rec f = 1\n" (Diverges "no-let-rec")
      pin "negative literal outside a range" "let n = -1\n" (Diverges "no-unary-minus")

      // --- corpus-born pins (dotnet/fsharp @ 5928e91, ComponentTests mining) ---
      pin "let parameter sugar (corpus-born feature, 2026-07-20)" "let f x = x + 1\n" Same
      pin "HOF param application" "let apply f x = f x\n" (Diverges "no-hof-inference")
      pin "operator on two unresolved params" "let add x y = x + y\n" (Diverges "no-operator-defaulting")
      pin
          "corpus: literal int pattern"
          "let v =\n    match 1 with\n    | 0 -> 0\n    | _ -> 1\n"
          (Diverges "no-literal-patterns")
      pin
          "corpus: function-valued interpolation hole"
          "let f = fun x -> x + 1\nlet s = $\"{f}\"\n"
          (Diverges "interp-scalar-only")
      pin "corpus: format specifier in a hole" "let s = $\"{1:N2}\"\n" (Diverges "no-format-specifiers") ]

[<Tests>]
let fidelityTests =
    testList
        "F# oracle"
        [ for p in pins ->
              test p.Name {
                  let weirV = weirVerdict p.Weir
                  let fsV = fsharpVerdict (defaultArg p.Fs p.Weir)

                  match p.Tag with
                  | Same -> Expect.equal fsV weirV $"fidelity claim: F# must agree (weir={weirV}, fsharp={fsV})"
                  | Diverges id ->
                      Expect.isTrue (divergenceIds.Contains id) $"divergence id '{id}' missing from divergences.md"

                      Expect.notEqual
                          fsV
                          weirV
                          $"claimed divergence did not diverge (both={weirV}); fix weir or retire the entry"
              } ]
