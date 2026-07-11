module FsLite.Main

open System
open FsLite.Ast
open FsLite.Types

let private evalOnce (input: string) : int =
    match Parser.parseStmt input with
    | Error msg ->
        Console.Error.WriteLine msg
        1
    | Ok(SType _) ->
        Console.Error.WriteLine "-e takes an expression, not a declaration"
        1
    | Ok(SLet _) ->
        Console.Error.WriteLine "-e takes an expression, not a let statement"
        1
    | Ok(SExpr e) ->
        match Check.typecheck Builtins.typeEnv e with
        | Error terr ->
            Console.Error.WriteLine(Check.formatError terr)
            1
        | Ok te ->
            try
                let v = Eval.eval Builtins.valueEnv te
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
        FsLite.Repl.run ()
        0
    | _ ->
        Console.Error.WriteLine "usage: fslite [-e <expression>]"
        2
