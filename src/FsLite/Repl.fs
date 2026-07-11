module FsLite.Repl

open System
open System.IO
open FsLite.Ast
open FsLite.Types

let private prompt = "fslite> "

type private State = { TypeEnv: TypeEnv; Values: Eval.Env }

let private initial =
    { TypeEnv = Builtins.typeEnv
      Values = Builtins.valueEnv }

let private currentEnv = ref initial.TypeEnv

type private Completer() =
    let mutable separators = [| ' '; '('; ')' |]

    interface IAutoCompleteHandler with
        member _.Separators
            with get () = separators
            and set v = separators <- v

        member _.GetSuggestions(text, index) =
            Complete.suggest currentEnv.Value text index |> List.toArray

let private historyFile =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".fslite_history")

let private setupLineEditor () =
    ReadLine.HistoryEnabled <- true
    ReadLine.AutoCompletionHandler <- Completer()

    if File.Exists historyFile then
        ReadLine.AddHistory(File.ReadAllLines historyFile)

let private readInput () =
    if Console.IsInputRedirected then
        Console.Write prompt
        Console.ReadLine()
    else
        let line = ReadLine.Read prompt

        if line <> null && line.Trim() <> "" then
            try
                File.AppendAllText(historyFile, line + Environment.NewLine)
            with _ ->
                ()

        line

let private underline (span: Span) : string =
    String(' ', prompt.Length + span.Start.Col - 1)
    + String('^', max 1 (span.End.Col - span.Start.Col))

let private printWarnings (state: State) (te: Check.TypedExpr) =
    Check.warnings state.TypeEnv te
    |> List.iter (fun w ->
        Console.WriteLine(underline w.Span)
        Console.WriteLine(Check.formatWarning w))

let private tryRun (state: State) (e: Expr) : Result<Check.TypedExpr * Eval.Value * string, string * Span option> =
    match Check.typecheck state.TypeEnv e with
    | Error terr -> Error(Check.formatError terr, Some terr.Span)
    | Ok te ->
        try
            let v = Eval.eval state.Values te
            Ok(te, v, Eval.formatValue v)
        with ex ->
            Error($"error: {ex.Message}", None)

let rec private loop (state: State) =
    currentEnv.Value <- state.TypeEnv

    match readInput () with
    | null
    | ":q" -> ()
    | line when String.IsNullOrWhiteSpace line -> loop state
    | line ->
        let next =
            match Parser.parseStmt line with
            | Error msg ->
                Console.WriteLine msg
                state
            | Ok(SType decl) ->
                match Check.checkDecl state.TypeEnv decl with
                | Error terr ->
                    Console.WriteLine(underline terr.Span)
                    Console.WriteLine(Check.formatError terr)
                    state
                | Ok typeEnv ->
                    let ctors =
                        match decl.Body with
                        | DUnion cases -> Eval.constructorValues cases
                        | DRecord _ -> []

                    Console.WriteLine $"type {decl.Name} declared"

                    { TypeEnv = typeEnv
                      Values = ctors |> List.fold (fun vs (n, v) -> Map.add n v vs) state.Values }
            | Ok(SLet(name, e)) ->
                match tryRun state e with
                | Error(msg, span) ->
                    span |> Option.iter (underline >> Console.WriteLine)
                    Console.WriteLine msg
                    state
                | Ok(te, v, formatted) ->
                    printWarnings state te
                    Console.WriteLine $"{name} : {formatTy te.Ty} = {formatted}"

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
                | Ok(te, _, formatted) ->
                    printWarnings state te
                    Console.WriteLine $"{formatted} : {formatTy te.Ty}"
                    state

        loop next

let run () =
    if not Console.IsInputRedirected then
        setupLineEditor ()

    loop initial
