module Fuzz.Main

// The assembler fuzzer [D:fuzz-harness]: metamorphic properties over
// generated line-shape programs.
//   Invariant 1 (metamorphic equivalence): semantics-neutral transforms
//     — blank/comment insertion, whole-block re-indent, district ↔
//     `!(...)`, bare command RHS ↔ `$(...)`, block siblings ↔ `;`,
//     Stroustrup ↔ inline brackets, and ALL COMPOSED — leave the AOT
//     binary's (rc, stdout, stderr) byte-identical.
//   Invariant 2 (total assembly): assembler/parser/checker return a
//     Result/diagnostic on every generated program AND every mutated
//     neighbor (deletion, indent perturbation, duplication, swap) — no
//     exception, no hang.
//   Invariant 3 (span soundness): an injected bad token is reported on
//     its own physical line, col within the line's extent.
//   Invariant 4 (fmt roundtrip): fmt succeeds on every generated
//     program, is idempotent, preserves per-statement sexpr shape, and
//     the formatted program is output-identical on the binary.
// Seeds/counts: WEIR_FUZZ_SEED / WEIR_FUZZ_COUNT (smoke default is the
// pinned seed; tools/fuzz.weir passes fresh seeds for deep runs).

open System
open Expecto
open FsCheck
open Fuzz.Grammar

let private envInt name dflt =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> dflt
    | v ->
        match Int32.TryParse v with
        | true, n -> n
        | _ -> dflt

let private seed = envInt "WEIR_FUZZ_SEED" 1789001
let private count = envInt "WEIR_FUZZ_COUNT" 200

// invariant 3's positional assertion is a standing guarantee
// [D:arm-commit] — strict positions on by default; the env is an
// off-switch only
let private strictSpans = envInt "WEIR_FUZZ_STRICT_SPANS" 1 = 1

// the observed-config report [D:observed-report]: an instrument
// reports what it measured, never what it was asked for — the driver
// (tools/fuzz.weir) reads this back and fails LOUD on any mismatch
// with its requested values (the deep runs that silently ran the
// defaults are the receipt)
match Environment.GetEnvironmentVariable "WEIR_FUZZ_REPORT" with
| null
| "" -> ()
| path -> System.IO.File.WriteAllText(path, $"seed={seed} count={count}")

type Arbs =
    static member Program() : Arbitrary<Program> =
        Arb.fromGenShrink (genProgram, shrinkProgram)

// invariant 3's DETECTOR CORE [D:walk-findings]: both span properties
// and the positive controls call THIS, so a control cannot pass against
// a copy while the real detector rots (the compareRuns/totalityCore
// mechanism, third instance)
type SpanVerdict =
    | SpanOk
    | SpanNoDiag of string
    | SpanMiss of string

let spanSoundCore (injected: string list) (physLine: int) (extent: int) : SpanVerdict =
    let diags, _, _, _ = Weir.Script.analyzeLines "fuzz.weir" injected

    match diags |> List.filter (fun d -> d.Severity = "error") with
    | [] -> SpanNoDiag $"no diagnostic for a bad token at line {physLine}"
    | errs ->
        let hit =
            errs
            |> List.exists (fun d -> d.Line = physLine && d.Col >= 1 && d.Col <= extent)

        if hit then
            SpanOk
        else
            SpanMiss
                $"""the site at line {physLine} (extent {extent}) lost its report: {errs |> List.map (fun d -> d.Line, d.Col, d.Message)}"""

let private cfg =
    { FsCheckConfig.defaultConfig with
        maxTest = count
        endSize = 100
        replay = Some(seed, 1)
        arbitrary = [ typeof<Arbs> ] }

let private showProgram (lines: string list) =
    lines |> List.map (fun l -> "  |" + l) |> String.concat "\n"

// invariant 1's detector CORE, extracted so the positive control can
// call it and assert it fires BY NAME [D:walk-findings] — Ok means
// byte-identical (rc, stdout, stderr); Error carries the invariant's
// own failure text
let private compareRuns (baseLines: string list) (transformed: string list) : Result<unit, string> =
    let r0 = Runner.runProgram baseLines

    if r0.TimedOut then
        Error(sprintf "base program HANGS:\n%s" (showProgram baseLines))
    elif r0.Rc <> 0 then
        Error(
            sprintf
                "base program rejected (rc=%d) — generator claims validity:\n%s\nstderr:\n%s"
                r0.Rc
                (showProgram baseLines)
                r0.Err
        )
    else
        let r1 = Runner.runProgram transformed

        if r1.TimedOut then
            Error(sprintf "transformed program HANGS:\n%s" (showProgram transformed))
        elif (r1.Rc, r1.Out, r1.Err) <> (r0.Rc, r0.Out, r0.Err) then
            Error(
                sprintf
                    "transform changed behavior\n--- base (rc=%d):\n%s\nout: %A\nerr: %A\n--- transformed (rc=%d):\n%s\nout: %A\nerr: %A"
                    r0.Rc
                    (showProgram baseLines)
                    r0.Out
                    r0.Err
                    r1.Rc
                    (showProgram transformed)
                    r1.Out
                    r1.Err
            )
        else
            Ok()

// run P once, require it clean, then require T(P) identical
let private metamorphic (name: string) (transform: Random -> Program -> string list option) =
    testPropertyWithConfig cfg name (fun (p: Program) (NonNegativeInt s) ->
        let baseLines = renderPlain p

        match transform (Random s) p with
        | None -> () // no applicable site (e.g. no reindentable block)
        | Some transformed ->
            match compareRuns baseLines transformed with
            | Ok() -> ()
            | Error m -> failtest m)

// invariant 2's detector CORE [D:walk-findings]: run `work` under a
// hang bound; Error names throw-or-hang
let private totalityCore (timeoutMs: int) (label: string) (work: unit -> unit) : Result<unit, string> =
    let task = System.Threading.Tasks.Task.Run work

    try
        if task.Wait timeoutMs then
            Ok()
        else
            Error(sprintf "%s exceeded %dms (possible hang)" label timeoutMs)
    with :? AggregateException as ae ->
        Error(sprintf "%s THREW %s: %s" label (ae.InnerException.GetType().Name) ae.InnerException.Message)

// assembler/parser/checker totality on one input, with a hang bound
let private totality (lines: string list) =
    match totalityCore 5000 "check pipeline" (fun () -> Weir.Script.analyzeLines "fuzz.weir" lines |> ignore) with
    | Ok() -> ()
    | Error m -> failtestf "%s\non:\n%s" m (showProgram lines)

// the depth guard's acceptance [D:depth-guard]: a pathological-depth
// input must DIAGNOSE (an error, not silent acceptance) within the
// hang bound and without crashing the process — a segfault here takes
// the whole test runner down, so survival IS the no-crash pin.
// 15s, not 5: the biggest seed is ~0.6s of real work, but these run
// in PARALLEL with the process-spawning metamorphic properties and a
// loaded runner multiplies wall-clock several-fold; a real hang is
// infinite, so the wider bound loses no detection.
let private depthDiagnoses (label: string) (line: string) =
    let work =
        System.Threading.Tasks.Task.Run(fun () ->
            let diags, _, _, _ = Weir.Script.analyzeLines "fuzz.weir" [ line ]
            diags)

    let finished =
        try
            work.Wait 15000
        with :? AggregateException as ae ->
            failtestf "%s: pipeline THREW %s: %s" label (ae.InnerException.GetType().Name) ae.InnerException.Message

    if not finished then
        failtestf "%s: exceeded 15s (hang)" label

    match work.Result |> List.filter (fun d -> d.Severity = "error") with
    | [] -> failtestf "%s: expected an error diagnostic, got none" label
    | _ -> ()

// the TIME axis of invariant 2 [D:depth-guard]: ANY non-termination is
// the failure — a hang and a crash are different failures, and exit
// codes cannot see the first. Same detector as depthDiagnoses, lines
// instead of a line.
let private diagnosesWithinBound (label: string) (boundMs: int) (lines: string list) =
    let work =
        System.Threading.Tasks.Task.Run(fun () ->
            let diags, _, _, _ = Weir.Script.analyzeLines "fuzz.weir" lines
            diags)

    let finished =
        try
            work.Wait boundMs
        with :? AggregateException as ae ->
            failtestf "%s: pipeline THREW %s: %s" label (ae.InnerException.GetType().Name) ae.InnerException.Message

    if not finished then
        failtestf "%s: exceeded %dms (hang)" label boundMs

    match work.Result |> List.filter (fun d -> d.Severity = "error") with
    | [] -> failtestf "%s: expected an error diagnostic, got none" label
    | _ -> ()

let private deepNest opener closer n =
    "let x = " + String.replicate n opener + "1" + String.replicate n closer

let private opSpine n =
    "let x = " + (List.replicate n "1" |> String.concat " + ")

// per-statement parse shapes under the permissive resolver — the
// respace guard's own predicate, reused (one shape language)
let private shapesOf (lines: string list) : string list option =
    match Weir.Script.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
    | Error _ -> None
    | Ok lls ->
        let r = Weir.Script.assumeResolver Weir.Builtins.typeEnv

        Some(
            lls
            |> List.map (fun ll ->
                match Weir.Parser.parseLine r ll.Text with
                | Ok stmt -> Weir.Ast.sexprStmt stmt
                | Error _ -> "<unparsed>")
        )

[<Tests>]
let tests =
    testList
        "Assembler fuzz"
        [ metamorphic "blank insertion is output-neutral" (fun rnd p ->
              Some(Transform.insertBlanks rnd (renderPlain p)))

          metamorphic "comment insertion is output-neutral" (fun rnd p ->
              Some(Transform.insertComments rnd (renderPlain p)))

          metamorphic "trailing comments are output-neutral" (fun rnd p ->
              Some(Transform.appendTrailing rnd (renderPlain p)))

          metamorphic "whole-block re-indent is output-neutral" (fun rnd p -> Transform.reindent rnd p)

          metamorphic "district marker form and explicit !(...) lines agree" (fun rnd p ->
              Transform.districtSigil rnd p)

          metamorphic "bare command RHS and $(...) agree" (fun rnd p -> Transform.cmdSigil rnd p)

          metamorphic "Stroustrup and inline bracket styles agree" (fun rnd p -> Transform.bracketStyle rnd p)

          metamorphic "block siblings and single-line ';' agree" (fun rnd p -> Transform.joinSiblings rnd p)

          metamorphic "multiline lambdas and their single-line form agree" (fun rnd p -> Transform.lambdaSingle rnd p)

          metamorphic "splat-of-literal and inline words agree [D:argv-splat]" (fun rnd p ->
              Transform.splatInline rnd p)

          // the laws must hold under COMPOSITION — where they have
          // historically failed
          metamorphic "all transforms composed stay output-neutral" (fun rnd p -> Some(Transform.composedAll rnd p))

          // the value-headed pipe ≡ feed equivalence law RETIRED
          // [D:drop-command-builtins]: feed is dropped, so there is no second
          // spelling to compare. The value-headed pipe is pinned by e2e + unit.

          // splat-in-reifier equivalence [D:splat-reifier-chains]:
          // `echo m $@([ws]) | reifier` ≡ the inline-words spelling,
          // byte-identical — the splat's elements ride the builtin's argv
          // with word integrity intact. A DEDICATED generator (reifier
          // chains are outside the main grammar's shape list, like the
          // depth axis); adversarial words are pinned by unit + e2e.
          testPropertyWithConfig cfg "splatted reifier chain ≡ inline words, byte-identical"
          <| fun (NonNegativeInt s) ->
              let rnd = Random s

              let words = List.init (1 + rnd.Next 4) (fun _ -> $"w{rnd.Next 1000}")

              let listLit =
                  "[ " + (words |> List.map (fun w -> $"\"{w}\"") |> String.concat "; ") + " ]"

              let inlineWords = String.concat " " words

              let reifier, reader =
                  [ "complete", "r.stdout |> Seq.iter print"
                    "complete", "print $\"rc={r.exitCode}\""
                    "succeeds", "print $\"ok={r}\""
                    "exitCode", "print $\"rc={r}\"" ].[rnd.Next 4]

              let splatted = [ $"let r = echo m0 $@({listLit}) | {reifier}"; reader ]
              let plain = [ $"let r = echo m0 {inlineWords} | {reifier}"; reader ]

              let r0 = Runner.runProgram splatted
              let r1 = Runner.runProgram plain

              if r0.TimedOut || r1.TimedOut then
                  failtestf "hang:\n%s" (showProgram splatted)

              if r0.Rc <> 0 then
                  failtestf "splatted reifier rejected (rc=%d):\n%s\n%s" r0.Rc (showProgram splatted) r0.Err

              if (r1.Rc, r1.Out, r1.Err) <> (r0.Rc, r0.Out, r0.Err) then
                  failtestf
                      "splat ≠ inline\n--- splatted: %A\n%s\n--- inline: %A\n%s"
                      (r0.Rc, r0.Out)
                      (showProgram splatted)
                      (r1.Rc, r1.Out)
                      (showProgram plain)

          testPropertyWithConfig cfg "assembly/check is total on generated programs and mutated neighbors"
          <| fun (p: Program) (NonNegativeInt s) ->
              let rnd = Random s
              let lines = renderPlain p
              totality lines
              totality (Mutate.deleteLine rnd lines)
              totality (Mutate.perturbIndent rnd lines)
              totality (Mutate.duplicateLine rnd lines)
              totality (Mutate.swapLines rnd lines)
              totality (Mutate.deleteLine rnd (Mutate.perturbIndent rnd lines))
              totality (Mutate.swapLines rnd (Mutate.duplicateLine rnd lines))

          // Invariant 2's depth axis [D:depth-guard]: the generator
          // favors breadth, so extreme DEPTH is pinned here explicitly —
          // the three safe-by-design-review fixtures (two were SEGV, one
          // was O(2^n)) become standing seeds, plus a generated sweep.
          test "deep parens diagnose-or-bound (was SEGV ~6000)" { depthDiagnoses "parens" (deepNest "(" ")" 20000) }
          test "long operator spine diagnoses-or-bound (was SEGV in check)" { depthDiagnoses "opspine" (opSpine 50000) }
          test "nested brackets diagnose-or-bound (was O(2^n))" { depthDiagnoses "brackets" (deepNest "[" "]" 2000) }
          test "nested records diagnose-or-bound" { depthDiagnoses "records" (deepNest "{a=" "}" 2000) }

          // the TYPE and PATTERN axes, plus the axes the coverage gate
          // surfaced (index/let-in/fun/elif/field/sigil) — every
          // constructor that nests, per the fuzzer-grammar-membership
          // rule [D:depth-guard]
          test "deep seq<> type diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "ty-seq"
                  ("type T = { x: "
                   + String.replicate 20000 "seq<"
                   + "int"
                   + String.replicate 20000 ">"
                   + " }")
          }
          test "deep Option<> type diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "ty-opt"
                  ("type T = { x: "
                   + String.replicate 20000 "Option<"
                   + "int"
                   + String.replicate 20000 ">"
                   + " }")
          }
          test "deep Map<string,> type diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "ty-map"
                  ("type T = { x: "
                   + String.replicate 20000 "Map<string, "
                   + "int"
                   + String.replicate 20000 ">"
                   + " }")
          }
          test "deep anon-shape type diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "ty-anon"
                  ("type T = { x: "
                   + String.replicate 20000 "{| a: "
                   + "int"
                   + String.replicate 20000 " |}"
                   + " }")
          }
          test "deep paren pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "pat-paren"
                  ("let v = match 1 with | "
                   + String.replicate 20000 "("
                   + "0"
                   + String.replicate 20000 ")"
                   + " -> 0 | _ -> 1")
          }
          test "deep tuple pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "pat-tuple"
                  ("let v = match (1, 1) with | "
                   + String.replicate 20000 "(1, "
                   + "(a, b)"
                   + String.replicate 20000 ")"
                   + " -> a | _ -> 1")
          }
          test "deep constructor pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "pat-ctor"
                  ("let v = match 1 with | "
                   + String.replicate 20000 "Some ("
                   + "x"
                   + String.replicate 20000 ")"
                   + " -> 1 | _ -> 0")
          }
          test "deep list pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "pat-list"
                  ("let v = match [1] with | "
                   + String.replicate 20000 "["
                   + "0"
                   + String.replicate 20000 "]"
                   + " -> 0 | _ -> 1")
          }
          test "long cons-chain pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "pat-cons"
                  ("let v = match [1] with | "
                   + String.replicate 20000 "a :: "
                   + "_"
                   + " -> 1 | _ -> 0")
          }
          test "deep let-binder pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses "pat-let" ("let " + String.replicate 20000 "(" + "x" + String.replicate 20000 ")" + " = 1")
          }
          test "deep lambda-param pattern diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses
                  "pat-lambda"
                  ("let f = fun "
                   + String.replicate 20000 "("
                   + "x"
                   + String.replicate 20000 ")"
                   + " -> x")
          }
          test "deep index chain diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses "index" ("let x = " + String.replicate 20000 "a[" + "0" + String.replicate 20000 "]")
          }
          test "long let-in chain diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses "letin" ("let x = " + String.replicate 20000 "let a = 1 in " + "1")
          }
          test "long fun chain diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses "fun" ("let x = " + String.replicate 20000 "fun a -> " + "1")
          }
          test "long elif chain diagnoses-or-bounds (was SEGV)" {
              // 5000 clauses: 10x the ceiling; each clause pays the full
              // stmtElem alternative cost, and 20000 ran into the hang
              // bound under parallel test load without adding coverage
              depthDiagnoses "elif" ("let x = if true then 1 " + String.replicate 5000 "elif true then 1 " + "else 2")
          }
          test "long field chain diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses "field" ("let x = a" + String.replicate 50000 ".b")
          }
          test "deep sigil nest diagnoses-or-bounds (was SEGV)" {
              depthDiagnoses "sigil" ("let x = " + String.replicate 5000 "$(echo " + "hi" + String.replicate 5000 ")")
          }

          // the post-parse seam [D:depth-guard]: parse depth ~1, but each
          // line DOUBLES the inferred type — the inference budget must
          // convert "no answer, forever" into a located diagnostic
          test "the inference bomb diagnoses inside the budget (was: hang, forever)" {
              diagnosesWithinBound
                  "infer-bomb"
                  30000
                  [ "let f0 x = (x, x)"
                    "let f1 x = f0 (f0 x)"
                    "let f2 x = f1 (f1 x)"
                    "let f3 x = f2 (f2 x)"
                    "let f4 x = f3 (f3 x)"
                    "let f5 x = f4 (f4 x)"
                    "let v = f5 1"
                    "print \"done\"" ]
          }

          testPropertyWithConfig cfg "arbitrary over-ceiling depth diagnoses-or-bounds"
          <| fun (NonNegativeInt s) ->
              let rnd = Random s
              let d = 600 + rnd.Next 4000 // above the 500 ceiling

              let line =
                  match rnd.Next 3 with
                  | 0 -> deepNest "(" ")" d
                  | 1 -> deepNest "[" "]" d
                  | _ -> opSpine d

              depthDiagnoses "generated" line

          testPropertyWithConfig cfg "span soundness: an injected bad token is reported on its own line"
          <| fun (p: Program) (NonNegativeInt s) ->
              let rnd = Random s
              let tagged = renderTagged defaultCfg p

              let eligible =
                  tagged
                  |> List.mapi (fun i (l, ok) -> i, l, ok)
                  |> List.filter (fun (_, _, ok) -> ok)

              match eligible with
              | [] -> ()
              | _ ->
                  let (idx, line, _) = eligible[rnd.Next eligible.Length]

                  let injected =
                      tagged |> List.mapi (fun i (l, _) -> if i = idx then l + " ?!?" else l)

                  let physLine = idx + 1
                  let extent = line.Length + 5

                  // [D:diag-arbitration]: the PRIMARY error lands at the
                  // true physical site — no backtrack-note escape hatch.
                  // The hatch existed for the district-wrap class (the
                  // consumed-separator law [D:seq-commit][D:arm-commit]
                  // closed it), so the assertion holds the primary itself.
                  match spanSoundCore injected physLine extent with
                  | SpanOk -> ()
                  | SpanNoDiag m -> failtestf "%s:\n%s" m (showProgram injected)
                  | SpanMiss m ->
                      if strictSpans then
                          failtestf "%s\n%s" m (showProgram injected)

          testPropertyWithConfig cfg "arbitration: a deeper second junk does not steal the first-reached error's site"
          <| fun (p: Program) (NonNegativeInt s) ->
              let rnd = Random s
              let tagged = renderTagged defaultCfg p

              let eligible =
                  tagged
                  |> List.mapi (fun i (l, ok) -> i, l, ok)
                  |> List.filter (fun (_, _, ok) -> ok)

              match eligible with
              | []
              | [ _ ] -> () // need two distinct sites to arbitrate between
              | _ ->
                  // [D:diag-arbitration]: "furthest the parser REACHED", not
                  // "latest in the file" — with two obstructions the FIRST is
                  // where the parse stops, so the shallower (first-reached)
                  // junk owns the report; a deeper junk downstream must not
                  // steal it or corrupt its position.
                  let a = rnd.Next eligible.Length
                  let mutable b = rnd.Next eligible.Length

                  while b = a do
                      b <- rnd.Next eligible.Length

                  let (ia, _, _) = eligible[a]
                  let (ib, _, _) = eligible[b]
                  let lo, hi = min ia ib, max ia ib

                  let injected =
                      tagged |> List.mapi (fun i (l, _) -> if i = lo || i = hi then l + " ?!?" else l)

                  let firstLine = lo + 1
                  let extent = (tagged[lo] |> fst).Length + 5

                  match spanSoundCore injected firstLine extent with
                  | SpanOk -> ()
                  | SpanNoDiag m -> failtestf "%s (second junk at line %d):\n%s" m (hi + 1) (showProgram injected)
                  | SpanMiss m ->
                      if strictSpans then
                          failtestf "first-reached %s\n%s" m (showProgram injected)

          // check agrees with run [PLAN-refactor-followups 1]: the tree's
          // most-repeated failure shape is the assume-resolver (check)
          // and the hard resolver (run) disagreeing about what a name
          // is — five incidents, one predicate (Script.assumeResolver),
          // and until now nothing asserting agreement. For every
          // generated program: parse each logical line under BOTH
          // resolvers (same sexpr), then check under both (same
          // verdict). Generated heads are echo/real, so the hard
          // resolver resolves them exactly as the runner would.
          testPropertyWithConfig
              cfg
              "check agrees with run: same parse, same verdict (the resolver seam)"
              (fun (p: Program) ->
                  let lines = renderPlain p
                  let numbered = lines |> List.mapi (fun i l -> i + 1, l)

                  match Weir.Script.assemble numbered with
                  | Error e -> failtestf "assemble: %s\n%s" e (showProgram lines)
                  | Ok lls ->
                      Weir.Extern.refresh ()

                      let noImports: Weir.Script.ImportLoader =
                          fun _ _ _ -> failwith "generated programs never import"

                      let mutable ta = Weir.Builtins.typeEnv
                      let mutable tb = Weir.Builtins.typeEnv
                      let mutable stop = false

                      for ll in lls do
                          if not stop then
                              let pa = Weir.Parser.parseLine (Weir.Script.assumeResolver ta) ll.Text
                              let pb = Weir.Parser.parseLine (Weir.Script.resolver tb) ll.Text

                              (match pa, pb with
                               | Ok sa, Ok sb ->
                                   let xa = Weir.Ast.sexprStmt sa
                                   let xb = Weir.Ast.sexprStmt sb

                                   if xa <> xb then
                                       failtestf
                                           "PARSE DIVERGENCE on:\n  %s\nassume: %s\nhard:   %s\n%s"
                                           ll.Text
                                           xa
                                           xb
                                           (showProgram lines)
                               | Error _, Error _ -> stop <- true // agreed rejection
                               | Ok _, Error eb ->
                                   failtestf
                                       "VERDICT DIVERGENCE (assume accepts, hard rejects) on:\n  %s\nhard: %s\n%s"
                                       ll.Text
                                       eb
                                       (showProgram lines)
                               | Error ea, Ok _ ->
                                   failtestf
                                       "VERDICT DIVERGENCE (hard accepts, assume rejects) on:\n  %s\nassume: %s\n%s"
                                       ll.Text
                                       ea
                                       (showProgram lines))

                              if not stop then
                                  let ca = Weir.Script.checkStatement true Weir.Script.assumeResolver noImports ta ll
                                  let cb = Weir.Script.checkStatement true Weir.Script.resolver noImports tb ll

                                  match ca, cb with
                                  | Ok a, Ok b ->
                                      ta <- a.Env
                                      tb <- b.Env
                                  | Error _, Error _ -> stop <- true // agreed rejection
                                  | Ok _, Error d ->
                                      failtestf
                                          "CHECK DIVERGENCE (assume accepts, hard rejects) on:\n  %s\n%s\n%s"
                                          ll.Text
                                          d.Message
                                          (showProgram lines)
                                  | Error d, Ok _ ->
                                      failtestf
                                          "CHECK DIVERGENCE (hard accepts, assume rejects) on:\n  %s\n%s\n%s"
                                          ll.Text
                                          d.Message
                                          (showProgram lines))
          testPropertyWithConfig cfg "fmt roundtrip: succeeds, idempotent, shape-preserving, output-neutral"
          <| fun (p: Program) ->
              let lines = renderPlain p

              match Weir.Fmt.formatLines lines with
              | Error e -> failtestf "fmt refused a valid program: %s\n%s" e (showProgram lines)
              | Ok fmted ->
                  (match Weir.Fmt.formatLines fmted with
                   | Error e -> failtestf "fmt output does not re-format: %s\n%s" e (showProgram fmted)
                   | Ok fmted2 ->
                       if fmted2 <> fmted then
                           failtestf
                               "fmt is not idempotent\n--- first:\n%s\n--- second:\n%s"
                               (showProgram fmted)
                               (showProgram fmted2))

                  if shapesOf fmted <> shapesOf lines then
                      failtestf
                          "fmt changed the parse shape\n--- original:\n%s\n--- formatted:\n%s"
                          (showProgram lines)
                          (showProgram fmted)

                  let r0 = Runner.runProgram lines

                  if r0.Rc <> 0 then
                      failtestf "base program rejected (rc=%d):\n%s\nstderr:\n%s" r0.Rc (showProgram lines) r0.Err

                  let r1 = Runner.runProgram fmted

                  if (r1.Rc, r1.Out, r1.Err) <> (r0.Rc, r0.Out, r0.Err) then
                      failtestf
                          "fmt changed behavior\n--- base (rc=%d) out %A err %A:\n%s\n--- formatted (rc=%d) out %A err %A:\n%s"
                          r0.Rc
                          r0.Out
                          r0.Err
                          (showProgram lines)
                          r1.Rc
                          r1.Out
                          r1.Err
                          (showProgram fmted) ]


// ---- positive controls [D:walk-findings] ----------------------------
// A control that ran once is not a control: the DECISIONS walk found
// the equality detector's bring-up evidence gone from the tree, so
// nothing distinguished "the detector works" from "the detector never
// fires" — the vacuous-probe class at the harness's own root. Each
// control below feeds its invariant's detector a case that MUST fail,
// and asserts the failure carries the invariant's own words (a control
// that passes because something threw is the disease it treats). These
// are cheap (fixed inputs, two spawns) and run in the SMOKE, so they
// cannot drift back to bring-up status.
[<Tests>]
let positiveControls =
    testList
        "Positive controls: the detectors themselves fire [D:walk-findings]"
        [ test "invariant 1: an output-changing transform is DETECTED, named" {
              match compareRuns [ "print \"control\"" ] [ "print \"control\""; "print \"EXTRA\"" ] with
              | Error m -> Expect.stringContains m "transform changed behavior" "fails by NAME, not by throwing"
              | Ok() -> failtest "the equality detector did not fire — invariant 1 is vacuous"
          }
          test "invariant 1: an rc-changing transform is detected too (all three channels)" {
              match compareRuns [ "print \"ok\"" ] [ "fail \"boom\"" ] with
              | Error m ->
                  Expect.stringContains m "transform changed behavior" "rc + stderr changes fire the same detector"
              | Ok() -> failtest "an rc change escaped the detector"
          }
          test "invariant 2: a throwing pipeline is DETECTED, named" {
              match totalityCore 5000 "control pipeline" (fun () -> failwith "deliberate") with
              | Error m -> Expect.stringContains m "control pipeline THREW" "the throw detector names itself"
              | Ok() -> failtest "a throwing pipeline escaped the totality detector"
          }
          test "invariant 2: a hang is DETECTED within the bound, named" {
              match totalityCore 50 "control pipeline" (fun () -> System.Threading.Thread.Sleep 500) with
              | Error m -> Expect.stringContains m "exceeded 50ms" "the hang detector names the bound"
              | Ok() -> failtest "a hang escaped the totality detector"
          }
          test "invariant 3: a claimed site the report is NOT at is DETECTED, named" {
              // junk on line 1, site claimed at line 2 — the detector must
              // refuse the claim, not hum along
              match spanSoundCore [ "let a = 1 ?!?"; "print \"x\"" ] 2 20 with
              | SpanMiss m -> Expect.stringContains m "lost its report" "the miss detector names itself"
              | SpanOk -> failtest "a wrong-site claim escaped the span detector"
              | SpanNoDiag m -> failtest $"expected a site miss, got no-diagnostic: {m}"
          }
          test "invariant 3: a clean program with a claimed bad site is DETECTED, named" {
              match spanSoundCore [ "let a = 1"; "print \"x\"" ] 1 12 with
              | SpanNoDiag m -> Expect.stringContains m "no diagnostic" "the no-diagnostic detector names itself"
              | SpanOk -> failtest "a clean program cannot satisfy a bad-site claim"
              | SpanMiss m -> failtest $"expected no-diagnostic, got a miss: {m}"
          }
          test "invariant 4's shape language DISTINGUISHES (equality is not vacuous)" {
              // the fmt roundtrip compares per-statement sexpr shapes; if
              // shapesOf collapsed everything to one value the comparison
              // would pass vacuously
              let a = shapesOf [ "print \"a\"" ]
              let b = shapesOf [ "let x = 1" ]
              Expect.isTrue (a.IsSome && b.IsSome && a <> b) "two different programs must have different shapes"
          } ]
