module Weir.Main

open System
open Weir.Ast
open Weir.Types

let private evalOnce (input: string) : int =
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnv Builtins.valueEnv

    let resolver: Parser.Resolver =
        { IsKnown = fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules
          IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
          IsExternal = Extern.exists
          ExternalNames = fun () -> Extern.names () :> seq<string> }

    let printHint () =
        Diagnose.hint
            (fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules)
            Builtins.commandCallable.Contains
            Extern.exists
            input
        |> Option.iter (fun h -> Console.Error.WriteLine $"hint: {h}")

    let ll: Script.LogicalLine =
        { Text = input
          Head = 1
          Segments = [ (0, 1, 0) ] }

    // the ONE pipeline (2026-07-21): -e is a consumer; non-expression
    // kinds are rejected AFTER checking, so an ill-typed let reports
    // its real error rather than the form message (reported delta)
    match Script.checkStatement false (fun _ -> resolver) typeEnv ll with
    | Error d ->
        (if d.Parse then
             Console.Error.WriteLine d.Message
         else
             match d.Span with
             | Some sp -> Console.Error.WriteLine(Check.formatError { Span = sp; Message = d.Message })
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
            for w in Check.warnings typeEnv te do
                Console.Error.WriteLine(Check.formatWarning w)

            try
                let v = Eval.eval valueEnv te

                if v <> Eval.VUnit then
                    Console.WriteLine $"{Eval.formatValue v} : {formatTy te.Ty}"

                0
            with
            | Eval.ExitRequest code -> code
            | ex ->
                Console.Error.WriteLine $"error: {ex.Message}"
                1

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | [ "-e"; input ] -> evalOnce input
    | [] -> Weir.Repl.run ()
    | [ "lsp" ] -> Lsp.run ()
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
