module Fuzz.Main

// The assembler fuzzer [D:fuzz-harness]: metamorphic properties over
// generated line-shape programs.
//   Invariant 1 (metamorphic equivalence): semantics-neutral transforms
//     — blank insertion, comment insertion, whole-block re-indent —
//     leave the AOT binary's (rc, stdout, stderr) byte-identical.
//   Invariant 2 (total assembly): assembler/parser/checker return a
//     Result/diagnostic on every generated program AND every mutated
//     neighbor (line deletion, indent perturbation) — no exception, no
//     hang.
// Seeds/counts: WEIR_FUZZ_SEED / WEIR_FUZZ_COUNT (smoke default is the
// pinned seed; the deep run passes fresh seeds explicitly).

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

[<Tests>]
let tests =
    testList
        "Assembler fuzz"
        [ metamorphic "blank insertion is output-neutral" (fun rnd p ->
              Some(Transform.insertBlanks rnd (renderPlain p)))

          metamorphic "comment insertion is output-neutral" (fun rnd p ->
              Some(Transform.insertComments rnd (renderPlain p)))

          metamorphic "whole-block re-indent is output-neutral" (fun rnd p -> Transform.reindent rnd p)

          // the laws must hold under COMPOSITION — where they have
          // historically failed
          metamorphic "re-indent + comments + blanks composed stay output-neutral" (fun rnd p ->
              let reindented = Transform.reindent rnd p |> Option.defaultValue (renderPlain p)

              Some(Transform.insertBlanks rnd (Transform.insertComments rnd reindented)))

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
              totality (Mutate.swapLines rnd (Mutate.duplicateLine rnd lines)) ]
