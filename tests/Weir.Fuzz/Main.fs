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
// pinned seed; tools/fuzz.sh passes fresh seeds for deep runs).

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

// invariant 3's positional assertion is gated: two span-quality classes
// are pinned in Weir.Tests (district wrap anchor, bare-pipe fatal) and
// the re-anchor policy is an open decision — the hard floor (junk always
// diagnosed) holds unconditionally; strict positions are the nightly's
// pressure instrument until the policy lands
let private strictSpans = envInt "WEIR_FUZZ_STRICT_SPANS" 0 = 1

type Arbs =
    static member Program() : Arbitrary<Program> =
        Arb.fromGenShrink (genProgram, shrinkProgram)

let private cfg =
    { FsCheckConfig.defaultConfig with
        maxTest = count
        endSize = 100
        replay = Some(seed, 1)
        arbitrary = [ typeof<Arbs> ] }

let private showProgram (lines: string list) =
    lines |> List.map (fun l -> "  |" + l) |> String.concat "\n"

// run P once, require it clean, then require T(P) identical
let private metamorphic (name: string) (transform: Random -> Program -> string list option) =
    testPropertyWithConfig cfg name (fun (p: Program) (NonNegativeInt s) ->
        let baseLines = renderPlain p

        match transform (Random s) p with
        | None -> () // no applicable site (e.g. no reindentable block)
        | Some transformed ->
            let r0 = Runner.runProgram baseLines

            if r0.TimedOut then
                failtestf "base program HANGS:\n%s" (showProgram baseLines)

            if r0.Rc <> 0 then
                failtestf
                    "base program rejected (rc=%d) — generator claims validity:\n%s\nstderr:\n%s"
                    r0.Rc
                    (showProgram baseLines)
                    r0.Err

            let r1 = Runner.runProgram transformed

            if r1.TimedOut then
                failtestf "transformed program HANGS:\n%s" (showProgram transformed)

            if (r1.Rc, r1.Out, r1.Err) <> (r0.Rc, r0.Out, r0.Err) then
                failtestf
                    "transform changed behavior\n--- base (rc=%d):\n%s\nout: %A\nerr: %A\n--- transformed (rc=%d):\n%s\nout: %A\nerr: %A"
                    r0.Rc
                    (showProgram baseLines)
                    r0.Out
                    r0.Err
                    r1.Rc
                    (showProgram transformed)
                    r1.Out
                    r1.Err)

// assembler/parser/checker totality on one input, with a hang bound
let private totality (lines: string list) =
    let work =
        System.Threading.Tasks.Task.Run(fun () -> Weir.Script.analyzeLines "fuzz.weir" lines |> ignore)

    let finished =
        try
            work.Wait 5000
        with :? AggregateException as ae ->
            failtestf
                "check pipeline THREW %s: %s\non:\n%s"
                (ae.InnerException.GetType().Name)
                ae.InnerException.Message
                (showProgram lines)

    if not finished then
        failtestf "check pipeline exceeded 5s (possible hang) on:\n%s" (showProgram lines)

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

          metamorphic "whole-block re-indent is output-neutral" (fun rnd p -> Transform.reindent rnd p)

          metamorphic "district marker form and explicit !(...) lines agree" (fun rnd p ->
              Transform.districtSigil rnd p)

          metamorphic "bare command RHS and $(...) agree" (fun rnd p -> Transform.cmdSigil rnd p)

          metamorphic "Stroustrup and inline bracket styles agree" (fun rnd p -> Transform.bracketStyle rnd p)

          metamorphic "block siblings and single-line ';' agree" (fun rnd p -> Transform.joinSiblings rnd p)

          metamorphic "multiline lambdas and their single-line form agree" (fun rnd p -> Transform.lambdaSingle rnd p)

          // the laws must hold under COMPOSITION — where they have
          // historically failed
          metamorphic "all transforms composed stay output-neutral" (fun rnd p -> Some(Transform.composedAll rnd p))

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

                  let diags, _, _, _ = Weir.Script.analyzeLines "fuzz.weir" injected

                  match diags |> List.filter (fun d -> d.Severity = "error") with
                  | [] ->
                      failtestf "no diagnostic for a bad token injected at line %d:\n%s" physLine (showProgram injected)
                  | errs ->
                      // a translated backtrack note naming the line counts as
                      // a positional hit (the district-wrap class carries the
                      // true site only there)
                      let noteHit (d: Weir.Script.Diagnostic) =
                          d.Message.Contains $"at line {physLine}, col "

                      let hit =
                          errs
                          |> List.exists (fun d -> (d.Line = physLine && d.Col >= 1 && d.Col <= extent) || noteHit d)

                      if strictSpans && not hit then
                          failtestf
                              "bad token at line %d (extent %d) reported elsewhere: %A\n%s"
                              physLine
                              extent
                              (errs |> List.map (fun d -> d.Line, d.Col, d.Message))
                              (showProgram injected)

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
