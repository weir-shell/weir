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
      // FLIPPED 2026-07-23 [D:body-blanks]: the founding divergence
      // retired — blanks are transparent; the col-0 law is the boundary
      pin "blank line inside a block is transparent (row RETIRED)" "let x =\n    let a = 1\n\n    a + 1\n" Same

      // --- offside close & record continuations (2026-07-20) ---
      pin "multi-line if/else as a let body" "let x =\n    if true then 1\n    else 2\n" Same
      pinT
          "sibling at the if's indent runs unconditionally"
          "let f c =\n    if c then printerr \"a\"\n    printerr \"b\"\n"
          "let f c =\n    if c then eprintfn \"a\"\n    eprintfn \"b\"\n"
          Same
      pin
          "multi-line record, bare fields (F# light syntax)"
          "type T = { Name: string; Count: int }\nlet t =\n    { Name = \"a\"\n      Count = 2 }\n"
          Same
      pin "F#-rejects-this: EOF inside an open brace" "type T = { Name: string }\nlet t =\n    { Name = \"a\"\n" Same
      pin
          "record field at column 0 (weir braces ignore indent)"
          "type T = { Name: string; Count: int }\nlet t =\n    { Name = \"a\"\nCount = 2 }\n"
          (Diverges "record-fields-ignore-indent")

      // --- type classes: Eq (Session A, 2026-07-20) — the fidelity GAIN ---
      pinT
          "generic equality generalizes (F# equality constraint, inferred)"
          "let same x y = x == y\nlet r = same 1 1\n"
          "let same x y = x = y\nlet r = same 1 1\n"
          Same
      pinT
          "generic equality rejected at functions (both sides) — the mirror-drift incident pin, now a REGRESSION GUARD (one-pipeline, 2026-07-21: the mirror calls checkStatement, drift is unconstructible)"
          "let same x y = x == y\nlet r = same (fun a -> a) (fun a -> a)\n"
          "let same x y = x = y\nlet r = same (fun (a: int) -> a) (fun (a: int) -> a)\n"
          Same

      // --- type classes: Show/Ord (Session B, 2026-07-21) ---
      pinT
          "generic sort helper (F# comparison constraint, inferred)"
          "let bykey k xs = xs |> Seq.sortBy k\nlet r = [3; 1] |> bykey (fun n -> n)\n"
          "let bykey k xs = xs |> Seq.sortBy k\nlet r = [3; 1] |> bykey (fun n -> n)\n"
          Same
      pinT
          "sort by function key rejected (both compilers)"
          "let r = [1] |> Seq.sortBy (fun n -> fun x -> x + n)\n"
          "let r = [1] |> Seq.sortBy (fun n -> fun x -> x + n)\n"
          Same

      // --- exit (renamed from Exit.code 2026-07-21 — F#-parity) ---
      pin "exit is F#'s exit (statement position)" "let go () = exit 3\n" Same

      // --- literal patterns + () thunks (2026-07-21) ---
      pin "int literal patterns with catch-all" "let v =\n    match 1 with\n    | 0 -> 10\n    | _ -> 20\n" Same
      pin "uppercase value binding rejected (the casing law)" "let Foo = 1\n" (Diverges "lowercase-binds")
      pin "underscore-leading binding accepted both sides" "let _keep = 1\n" Same
      pin
          "unknown uppercase pattern: weir errors, F# binds a var (FS0049 warn)"
          "type T = A of int | B\nlet v =\n    match B with\n    | Foo -> 1\n    | _ -> 2\n"
          (Diverges "uppercase-pattern-is-ctor")
      pin
          "literal arms never exhaust: weir hard-errors, F# warns+accepts"
          "let v =\n    match 1 with\n    | 0 -> 10\n    | 1 -> 20\n"
          (Diverges "exhaustiveness-hard-error")
      pin
          "dead arm after a catch-all: weir hard-errors, F# warns FS0026+accepts"
          "type T = A of int | B\nlet v =\n    match B with\n    | x -> 1\n    | B -> 2\n"
          (Diverges "unreachable-arm-hard-error")

      // --- prefix minus (2026-07-21) — the no-unary-minus row retires.
      // The oracle overturned the folklore mid-landing: F# parses
      // `f -1` as APPLICATION of -1 (adjacency), not subtraction ---
      pin "negative literal at operand position" "let x = -5\n" Same
      pin "prefix minus binds above * (both compilers)" "let x = 2 * -3\n" Same
      pin "f -1 applies the negative literal (both compilers)" "let f n = n + 1\nlet r = f -1\n" Same
      pin "F#-rejects-this: 1 -2 is int applied to int" "let r = 1 -2\n" Same

      // --- composition >>/<< (mini-plan 2026-07-21) ---
      pin "forward composition of let-functions" "let f n = n + 1\nlet g = f >> f\nlet r = g 40\n" Same
      pin "backward composition" "let f n = n + 1\nlet g = f << f\nlet r = g 40\n" Same
      // verdict-visible precedence — the oracle REFUTED tighter-than-
      // pipe: F# parses `xs |> f >> g` as `(xs |> f) >> g` (shared
      // infix class), both compilers reject it unparenthesized
      pin
          "F#-rejects-this: |> mixed with >> unparenthesized (shared precedence)"
          "let r = [1; 2] |> Seq.map (fun x -> x) >> Seq.sum\n"
          Same
      pin
          "the parenthesized composition pipes fine (both compilers)"
          "let r = [1; 2] |> (Seq.map (fun x -> x) >> Seq.sum)\n"
          Same
      pin "F#-rejects-this: >> on a non-function LHS" "let r = 1 >> 2\n" Same
      pin "adjacent lexing: > comparison vs >> composition" "let a = 1 > 2\nlet f n = n + 1\nlet g = f >> f\n" Same

      // --- raw strings (PLAN-raw-strings) — probes BEFORE code, per the
      // folklore-vs-compiler rule; edge verdicts are ASKED, not recalled ---
      pin "verbatim string with backslashes" "let s = @\"a\\nb\"\n" Same
      pin "verbatim quote doubling" "let s = @\"x\"\"y\"\n" Same
      pin "triple-quoted with a bare quote" "let s = \"\"\"a\"b\"\"\"\n" Same
      pin "edge: quad-quote opener (\"\"\"\"a\"\"\")" "let s = \"\"\"\"a\"\"\"\n" Same
      pin "edge: quad-quote closer (\"\"\"a\"\"\"\")" "let s = \"\"\"a\"\"\"\"\n" Same
      pin "multi-line verbatim: weir is single-line" "let s = @\"a\nb\"\n" (Diverges "raw-single-line")
      pin "multi-line triple: weir is single-line" "let s = \"\"\"a\nb\"\"\"\n" (Diverges "raw-single-line")
      pin "interpolated verbatim $@ is parked" "let s = $@\"x{1}\"\n" (Diverges "no-interpolated-raw")
      pin "interpolated triple is parked" "let s = $\"\"\"x{1}\"\"\"\n" (Diverges "no-interpolated-raw")

      // --- Seq.fold + fun-sugar probes (PLAN-fold) — BEFORE code; the
      // argument-order claim class bit once (composition precedence) ---
      pin
          "fold: state-first folder (verdict-visible via string state)"
          "let n = Seq.fold (fun s x -> s + $\"{x}\") \"\" [ 1; 2 ]\n"
          Same
      // shape amended in-session: `a + b` hits weir's KNOWN
      // +-on-unknowns limit (wrong reject reason); `b` isolates currying
      pin "two-param lambda is CURRIED: partial application works" "let f = (fun a b -> b) 1\nlet s = f \"x\"\n" Same
      pin "F#-rejects-this: a tupled lambda is not curried" "let g = fun (a, b) -> a + b\nlet n = g 1 2\n" Same
      // shape amended in-session: the arithmetic-empty form hits the
      // +-on-unknowns limit in weir's one-pass order (nothing anchors
      // s or x — the documented anchor-one-side rule); the identity
      // folder isolates the empty-seq acceptance claim
      pin "fold over empty returns the initial state (acceptance)" "let n = Seq.fold (fun s x -> s) 7 []\n" Same
      pin "duplicate lambda params: ask F#, do not recall" "let f = fun a a -> a\nlet n = f 1 2\n" Same

      // --- elif (small-items sweep; the no-elif row retires) ---
      pin
          "elif chains, F# semantics"
          "let x = 10\nlet y =\n    if x > 100 then \"a\"\n    elif x > 5 then \"b\"\n    else \"c\"\n"
          Same
      pin "F#-rejects-this: elif without a preceding if" "let y = elif 1 > 0 then \"x\"\n" Same
      pin
          "F#-rejects-this: elif after else"
          "let y =\n    if 1 > 2 then \"a\"\n    else \"b\"\n    elif 1 > 0 then \"c\"\n"
          Same

      // --- splice defaulting is a FINALIZATION step (small-items sweep) ---
      pin "hole under a pipe-bound lambda types from the pipe" "let s = 1 |> (fun k -> $\"{k}\")\n" Same

      // --- record update probes (PLAN-record-update) — BEFORE code, the
      // folklore rule: every asserted F# grammar fact gets its verdict
      // pin first; guesses flip to FCS's truth before implementation ---
      pin
          "record update: flat single field (corpus bbffe988 shape)"
          "type R = { V: string; I: int }\nlet m = { V = \"\"; I = 0 }\nlet m1 = { m with V = \"m\" }\n"
          Same
      pin
          "record update: multiple fields"
          "type R = { A: int; B: int }\nlet r = { A = 1; B = 2 }\nlet r2 = { r with A = 3; B = 4 }\n"
          Same
      pin
          "record update: nested I.X sugar (corpus 56d739b shape)"
          "type Inner = { X: int }\ntype Outer = { I: Inner }\nlet o = { I = { X = 1 } }\nlet o2 = { o with I.X = 2 }\n"
          Same
      pin
          "record update: parenthesized general-expression source"
          "type R = { A: int }\nlet id2 r = r\nlet x = { A = 1 }\nlet y = { (id2 x) with A = 2 }\n"
          Same
      pin
          "record update: unparenthesized application source"
          "type R = { A: int }\nlet id2 r = r\nlet x = { A = 1 }\nlet y = { id2 x with A = 2 }\n"
          Same
      pin
          "F#-rejects-this: update cannot add fields"
          "type R = { A: int }\nlet r = { A = 1 }\nlet r2 = { r with New = 2 }\n"
          Same
      // weir field paths never consult TYPE names; F# name resolution
      // captures a type named like the field and rejects — designed
      // divergence, weir-accepts direction, rowed as update-path-plain
      pin
          "update paths ignore type names (weir accepts; F# captures the type)"
          "type I = { X: int }\ntype O = { I: I }\nlet o = { I = { X = 1 } }\nlet o2 = { o with I.X = 2 }\n"
          (Diverges "update-path-plain")

      pin
          "record update: bare match source (parens expected required)"
          "type R = { A: int }\nlet x = { A = 1 }\nlet y = { match 1 with | _ -> x with A = 2 }\n"
          Same

      // --- the Regex pattern (2026-07-22) — the first weir-only match form ---
      pin
          "the Regex match pattern: weir-only (F# has no built-in regex pattern)"
          "let v =\n    match \"a1\" with\n    | Regex @\"([a-z])(1)\" (a, b) -> a\n    | _ -> \"\"\n"
          (Diverges "regex-pattern")
      pin "unit param pins the thunk type" "let cleanup () = 1\nlet r = cleanup ()\n" Same
      pin "F#-rejects-this: thunk applied to a value" "let cleanup () = 1\nlet r = cleanup 5\n" Same

      // --- tuples (2026-07-21, the reversal) — the no-tuples rows retire ---
      pin "tuple literal, type, pattern" "let p = (1, \"a\")\nlet v =\n    match p with\n    | (n, s) -> n\n" Same
      pin "multi-payload constructor" "type Msg = | Move of int * int | Stop\nlet m = Move (1, 2)\n" Same
      pinT
          "tuple equality (componentwise, both compilers)"
          "let b = (1, \"a\") == (1, \"a\")\n"
          "let b = (1, \"a\") = (1, \"a\")\n"
          Same
      pin
          "tuple ordering: weir rejects, F# compares lexicographically"
          "let r = [(2, 1); (1, 2)] |> Seq.sortBy (fun x -> x)\n"
          (Diverges "no-tuple-ord")
      pin
          "bool-component tuple arms: weir demands catch-all, F# products"
          "let v =\n    match (true, 1) with\n    | (true, _) -> 1\n    | (false, _) -> 2\n"
          (Diverges "tuple-exhaustiveness-bounded")
      // 2026-07-21: the binder shapes SHIPPED — both pins flip Same
      pin "pattern params (row content moved to refutable binders)" "let f = fun (a, b) -> a\n" Same
      pin "destructuring let (shipped; the row's arc completes)" "let p = (1, 2)\nlet x, y = p\n" Same
      pin "bare-comma tuple at full precedence" "let t = 1, 2\n" Same
      pin
          "refutable binder: F# warns-accepts, weir rejects (the row's remaining content)"
          "let x = Some 1\nlet (Some y) = x\n"
          (Diverges "no-pattern-binders")

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

      // --- multiline bracket probes (PLAN-multiline-brackets) ---
      pin
          "multiline type declaration (F# light's own rule)"
          "type Ctx =\n    { Subdir: string\n      Subref: string }\nlet c = { Subdir = \"a\"; Subref = \"b\" }\n"
          Same
      pin "multiline list literal" "let pairs =\n    [(\"a\", 1)\n     (\"b\", 2)]\n" Same
      pin "multiline list: wrapped element via dangling operator" "let x =\n    [1 +\n     2\n     3]\n" Same
      pin "F#-rejects-this: cross-bracket closer" "let x =\n    [1; 2\n     3}\n" Same
      pin
          "type field at column 0 rides the records-indent divergence"
          "type T =\n    { A: int\nB: int }\nlet t = { A = 1; B = 2 }\n"
          (Diverges "record-fields-ignore-indent")
      pin
          "preceding-line attribute on a type field (THE F# style; names diverge)"
          "type T =\n    { [<System.Obsolete>]\n      A: int }\nlet t = { A = 1 }\n"
          (Diverges "attributes-registered")

      // --- body-blank probes (PLAN-body-blanks: the core reversal) ---
      pin "blank inside a function body" "let f x =\n    let a = 1\n\n    a + x\nlet y = f 1\n" Same
      pin "blank between match arms" "let v =\n    match 1 with\n    | 1 -> \"a\"\n\n    | _ -> \"b\"\n" Same
      pin "blank between a let head and its first body line" "let x =\n\n    1\n" Same
      pin "blank inside an if body" "let v =\n    if true then\n        let a = 1\n\n        a + 1\n    else 2\n" Same
      pin "blank between match head and the first arm" "let v =\n    match 1 with\n\n    | _ -> \"b\"\n" Same
      pin "stray after a blank (the deliberate consequence)" "let x = 1\n\n    2\n" Same

      // --- blank-inside-bracket probes (PLAN-blank-lines) ---
      pin
          "blank inside a Stroustrup type declaration"
          "type Ctx = {\n    A: int\n\n    B: int\n}\nlet c = { A = 1; B = 2 }\n"
          Same
      pin "blank inside a record literal" "type R = { A: int; B: int }\nlet r = {\n    A = 1\n\n    B = 2\n}\n" Same
      pin "blank inside a list" "let xs = [\n    1\n\n    2\n]\n" Same
      pin
          "blank inside an update rides the update-offside row"
          "type R = { A: int; B: int }\nlet r = { A = 1; B = 2 }\nlet r2 = { r with\n    A = 3\n\n    B = 4\n}\n"
          (Diverges "record-fields-ignore-indent")
      pin "F#-rejects-this: col-0 let while a bracket is open (the guard)" "let xs = [\n    1\nlet y = 2\n" Same

      // --- Stroustrup bracket probes (fantomas-poll house style) ---
      pin
          "Stroustrup type declaration"
          "type Ctx = {\n    Subdir: string\n    Repo: string\n}\nlet c = { Subdir = \"a\"; Repo = \"b\" }\n"
          Same
      pin "Stroustrup record literal" "type R = { A: int }\nlet r = {\n    A = 1\n}\n" Same
      pin "Stroustrup list literal" "let xs = [\n    1\n    2\n]\n" Same
      // REFUTED as Same by the probe: F# offside-rejects the col-0
      // closer in UPDATE position (accepts it for type decls,
      // literals, lists — the fantomas-poll controversy, refereed);
      // weir is indentation-blind inside brackets — the standing row
      pin
          "Stroustrup copy-and-update (weir indentation-blind; F# offside-rejects)"
          "type R = { A: int; B: int }\nlet r = { A = 1; B = 2 }\nlet r2 = { r with\n    A = 3\n}\n"
          (Diverges "record-fields-ignore-indent")

      // --- pipe-alignment probes (the indentation session) ---
      // F# only WARNS (FS0058) on off-by-one pipes — weir hard-errors:
      // the warning-vs-error strictness family [D:pipe-alignment]
      pin
          "union case off by one (deeper): weir errors, F# warns-accepts"
          "type C =\n    | A of int\n     | B of string\nlet x = A 1\n"
          (Diverges "pipe-alignment")
      pin
          "union case off by one (shallower)"
          "type C =\n    | A of int\n   | B of string\nlet x = A 1\n"
          (Diverges "pipe-alignment")
      pin
          "match arm off by one from its siblings"
          "let v =\n    match 1 with\n    | 1 -> \"a\"\n     | _ -> \"b\"\n"
          (Diverges "pipe-alignment")
      pin
          "F#-rejects-this: col-0 arms under an indented match (statement let)"
          "let category =\n    match 3 with\n| s when s > 2 -> \"big\"\n| _ -> \"small\"\n"
          Same
      pin
          "arms consistently deeper than the match head are fine"
          "let v =\n    match 1 with\n        | 1 -> \"a\"\n        | _ -> \"b\"\n"
          Same
      pin
          "nested arms return to the outer column"
          "let v =\n    match 1 with\n    | 1 ->\n        match 2 with\n        | 2 -> \"a\"\n        | _ -> \"b\"\n    | _ -> \"c\"\n"
          Same

      // --- function reservation probe (block-let-cmd rider) ---
      pin "F#-rejects-this: function as a binder name" "let function = 1\n" Same

      // --- seq-pattern probes (PLAN-seq-force-patterns Part 2) ---
      pin
          "seq patterns: F#'s spelling on a LIST literal scrutinee agrees"
          "let v =\n    match [1; 2] with\n    | [] -> 0\n    | x :: rest -> x\n"
          Same
      pin
          "seq patterns on a SEQ scrutinee: F# rejects, weir extends (the row)"
          "let v =\n    match ([1; 2] |> Seq.skip 0) with\n    | [] -> 0\n    | x :: rest -> x\n"
          (Diverges "seq-patterns")
      pin
          "fixed-arity pattern on a seq scrutinee"
          "let v =\n    match ([1; 2] |> Seq.skip 0) with\n    | [a; b] -> a + b\n    | _ -> 0\n"
          (Diverges "seq-patterns")
      pin
          "chained cons on a list literal (F#'s right assoc)"
          "let v =\n    match [1; 2; 3] with\n    | a :: b :: rest -> a + b\n    | _ -> 0\n"
          Same

      // --- Seq.append probe (the full-port receipt: variable argv) ---
      pin "Seq.append: piped tail after the head seq" "let xs = [3; 4] |> Seq.append [1; 2] |> Seq.length\n" Same

      // --- Seq.choose probes (PLAN-choose) ---
      pin
          "Seq.choose: partial map, applied"
          "let n = [1; 2; 3] |> Seq.choose (fun x -> if x > 1 then Some x else None) |> Seq.length\n"
          Same
      pin "Seq.choose: all-None yields empty" "let n = [1; 2] |> Seq.choose (fun x -> None) |> Seq.length\n" Same
      pin "Seq.choose: chooser must return Option" "let bad = [1] |> Seq.choose (fun x -> x)\n" Same

      // --- attribute probes (PLAN-attributes) — attachment shape is
      // F#-real (the System.Obsolete direction proves FCS parses it);
      // weir's registry is closed, so names diverge both ways ---
      pin
          "attributes: registered name attaches (F# has no Short type)"
          "type T = { [<Short \"c\">] A: int }\nlet t = { A = 1 }\n"
          (Diverges "attributes-registered")
      pin
          "attributes: F# accepts a real attribute weir does not register"
          "type T = { [<System.Obsolete>] A: int }\nlet t = { A = 1 }\n"
          (Diverges "attributes-registered")
      pin "F#-rejects-this: literal in attribute-name position" "type T = { [<5>] A: int }\n" Same
      pin "F#-rejects-this: attribute in expression position" "let x = [<Short \"c\">] 1\n" Same
      pin
          "attributes: multiple in one list, semicolon-separated"
          "type T = { [<Short \"c\"; Doc \"count\">] A: int }\nlet t = { A = 1 }\n"
          (Diverges "attributes-registered")

      // --- named divergences, refereed from both sides ---
      pinT "equality spelling: == vs =" "let b = 1 == 1\n" "let b = 1 == 1\n" (Diverges "double-equals")
      pinT "binding-only =: F# equality rejected by weir" "let b = 1 = 1\n" "let b = 1 = 1\n" (Diverges "double-equals")
      pin "tuple literal (row RETIRED 2026-07-21 — the reversal)" "let p = (1, 2)\n" Same
      pin "starred union payload (single-payload rule retired with tuples)" "type T = A of int * string\n" Same
      pin "discarded value statement" "\"orphan\"\n" (Diverges "statement-rule")
      pin "block comment" "(* block *)\nlet x = 1\n" (Diverges "block-comments")
      pin "printfn" "printfn \"hi\"\n" (Diverges "no-printf-family")
      pin "mutable binding" "let mutable x = 1\n" (Diverges "no-mutation")
      pin "let rec" "let rec f = 1\n" (Diverges "no-let-rec")
      pin "negative literal outside a range" "let n = -1\n" Same

      // --- corpus-born pins (dotnet/fsharp @ 5928e91, ComponentTests mining) ---
      pin "let parameter sugar (corpus-born feature, 2026-07-20)" "let f x = x + 1\n" Same
      pin "HOF param application" "let apply f x = f x\n" (Diverges "no-hof-inference")
      pin "operator on two unresolved params" "let add x y = x + y\n" (Diverges "no-operator-defaulting")
      pin
          "corpus: literal int pattern (row RETIRED 2026-07-21 — the fidelity gain)"
          "let v =\n    match 1 with\n    | 0 -> 0\n    | _ -> 1\n"
          Same
      pin
          "corpus: function-valued interpolation hole"
          "let f = fun x -> x + 1\nlet s = $\"{f}\"\n"
          (Diverges "interp-scalar-only")
      pin "corpus: format specifier in a hole" "let s = $\"{1:N2}\"\n" (Diverges "no-format-specifiers")
      pin "computation expressions" "let s = seq { 1 }\n" (Diverges "no-computation-expressions")

      pin "mid-token // is not a comment (weir: URL survival)" "let x = 1// c\n" (Diverges "comment-boundary")

      // --- block sequencing (Session 2): the fidelity GAIN pins ---
      pinT
          "sequenced effect block under if"
          "let go = 1 > 0\nlet w =\n    if go then\n        print \"a\"\n        print \"b\"\n"
          "let go = 1 > 0\nlet w =\n    if go then\n        printf \"a\"\n        printf \"b\"\n"
          Same
      pinT "explicit semicolon sequencing" "let u = (print \"x\" ; 1)\n" "let u = (printf \"x\" ; 1)\n" Same ]

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
