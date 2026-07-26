module Weir.Main

open System
open Weir.Ast
open Weir.Types

let private evalOnce (input: string) : int =
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnv Builtins.valueEnv

    let resolver = Script.resolver typeEnv

    let printHint () =
        Diagnose.hint
            (fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules)
            Builtins.commandCallable.Contains
            Extern.exists
            input
        |> Option.iter (fun h -> Console.Error.WriteLine $"hint: {h}")

    let ll = Script.singleLine input

    // [D:one-pipeline]: -e is a consumer; non-expression kinds are
    // rejected AFTER checking, so an ill-typed let reports its real
    // error rather than the form message
    match Script.checkStatement false (fun _ -> resolver) typeEnv ll with
    | Error d ->
        (if d.Parse then
             Console.Error.WriteLine input

             Console.Error.WriteLine(
                 Script.Color.red Script.Color.onStderr.Value (String(' ', max 0 (d.PhysCol - 1)) + "^")
             )

             Console.Error.WriteLine d.Message
         else
             match d.Span with
             | Some _ ->
                 Console.Error.WriteLine input

                 let width =
                     match d.PhysEnd with
                     | Some(el, ec) when el = d.PhysLine -> max 1 (ec - d.PhysCol)
                     | _ -> 1

                 Console.Error.WriteLine(
                     Script.Color.red
                         Script.Color.onStderr.Value
                         (String(' ', max 0 (d.PhysCol - 1)) + String('^', width))
                 )

                 Console.Error.WriteLine $"type error: {d.Message}"
             | None -> Console.Error.WriteLine d.Message)

        printHint ()
        1
    | Ok chk ->
        match chk.Kind with
        | Script.KType _ ->
            Console.Error.WriteLine "-e takes an expression, not a declaration"
            1
        | Script.KLet _ ->
            Console.Error.WriteLine "-e takes an expression, not a let statement"
            1
        | Script.KLetPat _ ->
            Console.Error.WriteLine "-e evaluates one expression; use 'let (x, y) = ... in ...'"
            1
        | Script.KExpr te
        | Script.KCmd te ->
            for w in Check.warnings te do
                Console.Error.WriteLine(Check.formatWarning w)

            try
                let v = Eval.eval valueEnv te

                if v <> Eval.VUnit then
                    let rendered, hint = Eval.echoValue v

                    let tail = Eval.echoTail (te.Ty = TSeq TStr) hint

                    Console.WriteLine $"{rendered} : {formatTy te.Ty}{tail}"

                0
            with
            | Eval.ExitRequest code -> code
            | ex ->
                Console.Error.WriteLine(Script.Color.red Script.Color.onStderr.Value "error" + $": {ex.Message}")
                1

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | [ "-e"; input ] -> evalOnce input
    | [] -> Weir.Repl.run ()
    | [ "--version" ] ->
        // the build stamp [D:masking-mechanized] — harness gates
        // compare this against git HEAD
        let v =
            match
                System.Reflection.Assembly
                    .GetEntryAssembly()
                    .GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
            with
            | [| :? System.Reflection.AssemblyInformationalVersionAttribute as a |] -> a.InformationalVersion
            | _ -> "dev"

        Console.WriteLine v
        0
    | [ "lsp" ] -> Lsp.run ()
    | "lsp" :: _ ->
        Console.Error.WriteLine
            "usage: weir lsp — the language server, JSON-RPC over stdio; takes no arguments.\nWire your editor to run this command: see docs/editors.md"

        2
    | [ "check"; path ] -> Script.checkOnly false path
    | [ "check"; "--json"; path ] -> Script.checkOnly true path
    | [ "fmt"; "--qualify"; path ] -> Fmt.qualifyFile path
    | [ "fmt"; "--check"; path ] -> Fmt.formatFile true path
    | [ "fmt"; path ] -> Fmt.formatFile false path
    | "fmt" :: _ ->
        Console.Error.WriteLine "usage: weir fmt [--check|--qualify] <script>"
        2
    | "run" :: path :: rest -> Script.run path rest
    | path :: rest when not (path.StartsWith "-") -> Script.run path rest
    | _ ->
        Console.Error.WriteLine(
            "usage: weir                                    the REPL\n"
            + "       weir <script> [args...]                 run a script\n"
            + "       weir -e <expression>                    evaluate one expression\n"
            + "       weir check [--json] <script>            diagnostics only (no evaluation)\n"
            + "       weir fmt [--check|--qualify] <script>   canonical formatter\n"
            + "       weir lsp                                language server (stdio)"
        )

        2
