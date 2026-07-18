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
            Extern.exists
            input
        |> Option.iter (fun h -> Console.Error.WriteLine $"hint: {h}")

    match Parser.parseLine resolver input with
    | Error msg ->
        Console.Error.WriteLine msg
        printHint ()
        1
    | Ok(SType _) ->
        Console.Error.WriteLine "-e takes an expression, not a declaration"
        1
    | Ok(SLet _) ->
        Console.Error.WriteLine "-e takes an expression, not a let statement"
        1
    | Ok(SExpr e)
    | Ok(SCmd e) ->
        match Check.typecheck typeEnv e with
        | Error terr ->
            Console.Error.WriteLine(Check.formatError terr)
            printHint ()
            1
        | Ok te ->
            try
                let v = Eval.eval valueEnv te

                if v <> Eval.VUnit then
                    Console.WriteLine $"{Eval.formatValue v} : {formatTy te.Ty}"

                0
            with ex ->
                Console.Error.WriteLine $"error: {ex.Message}"
                1

[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | [ "-e"; input ] -> evalOnce input
    | [] ->
        Weir.Repl.run ()
        0
    | [ "fmt"; "--qualify"; path ] -> Fmt.qualifyFile path
    | "run" :: path :: rest -> Script.run path rest
    | path :: rest when not (path.StartsWith "-") -> Script.run path rest
    | _ ->
        Console.Error.WriteLine "usage: weir [-e <expression>] [run] <script> [args...] | weir fmt --qualify <script>"
        2
