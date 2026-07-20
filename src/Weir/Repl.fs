module Weir.Repl

open System
open System.IO
open Weir.Ast
open Weir.Types

let private prompt = "weir> "

type private State = { TypeEnv: TypeEnv; Values: Eval.Env }

let private initial =
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnv Builtins.valueEnv
    { TypeEnv = typeEnv; Values = valueEnv }

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
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".weir_history")

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

let private resolver (state: State) : Parser.Resolver =
    { IsKnown =
        fun n ->
            Map.containsKey n state.TypeEnv.Values
            || Map.containsKey n state.TypeEnv.Modules
      IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
      IsExternal = Extern.exists
      ExternalNames = fun () -> Extern.names () :> seq<string> }

let private printHint (state: State) (line: string) =
    Diagnose.hint
        (fun n ->
            Map.containsKey n state.TypeEnv.Values
            || Map.containsKey n state.TypeEnv.Modules)
        Builtins.commandCallable.Contains
        Extern.exists
        line
    |> Option.iter (fun h -> Console.WriteLine $"hint: {h}")

let private tryRun (state: State) (e: Expr) : Result<Check.TypedExpr * Eval.Value * string, string * Span option> =
    match Check.typecheck state.TypeEnv e with
    | Error terr -> Error(Check.formatError terr, Some terr.Span)
    | Ok te ->
        try
            let v = Eval.eval state.Values te
            Ok(te, v, Eval.formatValue v)
        with
        | Eval.ExitRequest _ ->
            // intentional exit, not an eval error — the run loop turns it
            // into the process exit code (the fifth-site pin's fix)
            reraise ()
        | ex -> Error($"error: {ex.Message}", None)

let rec private loop (state: State) =
    currentEnv.Value <- state.TypeEnv

    match readInput () with
    | null
    | ":q" -> ()
    | line when String.IsNullOrWhiteSpace line -> loop state
    | line ->
        Extern.refresh ()

        let next =
            match Parser.parseLine (resolver state) line with
            | Error msg ->
                Console.WriteLine msg
                printHint state line
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

                    if span.IsSome then
                        printHint state line

                    state
                | Ok(te, v, formatted) ->
                    printWarnings state te

                    if v <> Eval.VUnit then
                        Console.WriteLine $"{name} : {formatTy te.Ty} = {formatted}"

                    { TypeEnv =
                        { state.TypeEnv with
                            Values = Map.add name (generalize te.Ty) state.TypeEnv.Values }
                      Values = Map.add name v state.Values }
            | Ok(SExpr e)
            | Ok(SCmd e) ->
                match tryRun state e with
                | Error(msg, span) ->
                    span |> Option.iter (underline >> Console.WriteLine)
                    Console.WriteLine msg

                    if span.IsSome then
                        printHint state line

                    state
                | Ok(te, v, formatted) ->
                    printWarnings state te

                    if v <> Eval.VUnit then
                        Console.WriteLine $"{formatted} : {formatTy te.Ty}"

                    state

        loop next

let run () =
    if not Console.IsInputRedirected then
        setupLineEditor ()

    try
        loop initial
        0
    with Eval.ExitRequest code ->
        code
