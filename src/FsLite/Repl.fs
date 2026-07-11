module FsLite.Repl

open System
open FsLite.Ast
open FsLite.Types

let private prompt = "fslite> "

type private State = { TypeEnv: TypeEnv; Values: Eval.Env }

let private initial =
    { TypeEnv = Builtins.typeEnv
      Values = Builtins.valueEnv }

let private underline (span: Span) : string =
    String(' ', prompt.Length + span.Start.Col - 1)
    + String('^', max 1 (span.End.Col - span.Start.Col))

let private tryRun (state: State) (e: Expr) : Result<Check.TypedExpr * Eval.Value, string * Span option> =
    match Check.typecheck state.TypeEnv e with
    | Error terr -> Error(Check.formatError terr, Some terr.Span)
    | Ok te ->
        try
            Ok(te, Eval.eval state.Values te)
        with ex ->
            Error($"error: {ex.Message}", None)

let rec private loop (state: State) =
    Console.Write prompt

    match Console.ReadLine() with
    | null
    | ":q" -> ()
    | line when String.IsNullOrWhiteSpace line -> loop state
    | line ->
        let next =
            match Parser.parseStmt line with
            | Error msg ->
                Console.WriteLine msg
                state
            | Ok(SLet(name, e)) ->
                match tryRun state e with
                | Error(msg, span) ->
                    span |> Option.iter (underline >> Console.WriteLine)
                    Console.WriteLine msg
                    state
                | Ok(te, v) ->
                    Console.WriteLine $"{name} : {formatTy te.Ty} = {Eval.formatValue v}"

                    { TypeEnv =
                        { state.TypeEnv with
                            Values = Map.add name te.Ty state.TypeEnv.Values }
                      Values = Map.add name v state.Values }
            | Ok(SExpr e) ->
                match tryRun state e with
                | Error(msg, span) ->
                    span |> Option.iter (underline >> Console.WriteLine)
                    Console.WriteLine msg
                    state
                | Ok(te, v) ->
                    Console.WriteLine $"{Eval.formatValue v} : {formatTy te.Ty}"
                    state

        loop next

let run () = loop initial
