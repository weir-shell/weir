module Weir.Main

open System
open Weir.Ast
open Weir.Types

let private evalOnce (input: string) : int =
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnv Builtins.valueEnv

    let resolver: Parser.Resolver =
        { IsKnown = fun n -> Map.containsKey n typeEnv.Values
          IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
          IsExternal = Extern.exists
          ExternalNames = fun () -> Extern.names () :> seq<string> }

    let printHint () =
        Diagnose.hint (fun n -> Map.containsKey n typeEnv.Values) Extern.exists input
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
    | Ok(SExpr e) ->
        match Check.typecheck typeEnv e with
        | Error terr ->
            Console.Error.WriteLine(Check.formatError terr)
            printHint ()
            1
        | Ok te ->
            try
                let v = Eval.eval valueEnv te
                Console.WriteLine $"{Eval.formatValue v} : {formatTy te.Ty}"
                0
            with ex ->
                Console.Error.WriteLine $"error: {ex.Message}"
                1

[<EntryPoint>]
let main argv =
    match argv with
    | [| "-e"; input |] -> evalOnce input
    | [||] ->
        Weir.Repl.run ()
        0
    | _ ->
        Console.Error.WriteLine "usage: weir [-e <expression>]"
        2
