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

let rec private loop (state: State) =
    currentEnv.Value <- state.TypeEnv

    match readInput () with
    | null
    | ":q" -> ()
    | line when String.IsNullOrWhiteSpace line -> loop state
    | line ->
        Extern.refresh ()

        let next =
            // the ONE pipeline (2026-07-21): the REPL is a consumer, not a
            // replica — a single-line LogicalLine feeds checkStatement
            let ll: Script.LogicalLine =
                { Text = line
                  Head = 1
                  Segments = [ (0, 1, 0) ] }

            match Script.checkStatement false (fun _ -> resolver state) state.TypeEnv ll with
            | Error d when d.Parse ->
                Console.WriteLine d.Message
                printHint state line
                state
            | Error d ->
                d.Span |> Option.iter (underline >> Console.WriteLine)

                (match d.Span with
                 | Some sp -> Console.WriteLine(Check.formatError { Span = sp; Message = d.Message })
                 | None -> Console.WriteLine d.Message)

                // hint only where the pre-pipeline REPL hinted (expression
                // and let forms; type/binder-pattern errors stayed bare)
                (match d.Tag with
                 | Some(Script.StmtTag.Let | Script.StmtTag.Expr | Script.StmtTag.Cmd) when d.Span.IsSome ->
                     printHint state line
                 | _ -> ())

                state
            | Ok chk ->
                match chk.Kind with
                | Script.KType decl ->
                    let ctors =
                        match decl.Body with
                        | DUnion cases -> Eval.constructorValues cases
                        | DRecord _ -> []

                    Console.WriteLine $"type {decl.Name} declared"

                    { TypeEnv = chk.Env
                      Values = ctors |> List.fold (fun vs (n, v) -> Map.add n v vs) state.Values }
                | Script.KLetPat(pat, schemes, te) ->
                    printWarnings state te

                    (try
                        let v = Eval.eval state.Values te
                        let bindings = Eval.bindPattern pat v

                        // one destructuring line reports each binding on its
                        // own line — matching what two lets would have shown
                        for n, sch in schemes do
                            Console.WriteLine $"{n} : {formatTy sch.Ty}"

                        { TypeEnv = chk.Env
                          Values = bindings |> List.fold (fun vs (n, v) -> Map.add n v vs) state.Values }
                     with
                     | Eval.ExitRequest _ -> reraise ()
                     | ex ->
                         Console.WriteLine $"error: {ex.Message}"
                         state)
                | Script.KLet(name, _, te) ->
                    printWarnings state te

                    (try
                        let v = Eval.eval state.Values te

                        if v <> Eval.VUnit then
                            Console.WriteLine $"{name} : {formatTy te.Ty} = {Eval.formatValue v}"

                        { TypeEnv = chk.Env
                          Values = Map.add name v state.Values }
                     with
                     | Eval.ExitRequest _ -> reraise ()
                     | ex ->
                         Console.WriteLine $"error: {ex.Message}"
                         state)
                | Script.KExpr te
                | Script.KCmd te ->
                    printWarnings state te

                    (try
                        let v = Eval.eval state.Values te

                        if v <> Eval.VUnit then
                            Console.WriteLine $"{Eval.formatValue v} : {formatTy te.Ty}"

                        state
                     with
                     | Eval.ExitRequest _ -> reraise ()
                     | ex ->
                         Console.WriteLine $"error: {ex.Message}"
                         state)

        loop next

let run () =
    if not Console.IsInputRedirected then
        setupLineEditor ()

    try
        loop initial
        0
    with Eval.ExitRequest code ->
        code
