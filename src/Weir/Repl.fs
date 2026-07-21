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

let private historyFile =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".weir_history")

// ---------------------------------------------------------------------------
// The owned line editor [D:owned-line-editor]: replaced the ReadLine package —
// it swallowed Ctrl+D with no hook, and the REPL wants bash key
// semantics: Ctrl+C cancels the LINE, Ctrl+D on an empty line exits
// (EOF). Owning ~150 lines also drops the last non-FParsec dependency.

let private history = ResizeArray<string>()

let private loadHistory () =
    if File.Exists historyFile then
        history.AddRange(File.ReadAllLines historyFile)

// Ctrl+Left/Right navigation; '.' stays a separator here (unlike
// completion's wordStartAt) so field chains hop segment by segment
let private isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_'

let private wordStartAt (text: string) (pos: int) =
    let mutable i = pos

    while i > 0
          && (Char.IsLetterOrDigit text[i - 1] || text[i - 1] = '_' || text[i - 1] = '.') do
        i <- i - 1

    i

/// returns None on EOF (Ctrl+D at an empty line)
let private readLineTty () : string option =
    Console.Write prompt
    let buf = Text.StringBuilder()
    let mutable pos = 0
    let mutable histIdx = history.Count // one past the end = the new line
    let mutable draft = ""

    let redraw () =
        Console.Write("\r" + prompt + buf.ToString() + "\x1b[K")
        let back = buf.Length - pos

        if back > 0 then
            Console.Write $"\x1b[{back}D"

    let setLine (s: string) =
        buf.Clear().Append(s) |> ignore
        pos <- buf.Length
        redraw ()

    let mutable result: string option option = None

    while result.IsNone do
        let k = Console.ReadKey(intercept = true)
        let ctrl = k.Modifiers.HasFlag ConsoleModifiers.Control

        match k.Key with
        | ConsoleKey.Enter ->
            Console.WriteLine()
            result <- Some(Some(buf.ToString()))
        // some terminals deliver control chords as bare KeyChars —
        // match the codes as well as the (Key, Modifier) pairs
        | _ when k.KeyChar = '\u0004' ->
            if buf.Length = 0 then
                Console.WriteLine()
                result <- Some None
            elif pos < buf.Length then
                buf.Remove(pos, 1) |> ignore
                redraw ()
        | _ when k.KeyChar = '\u0003' ->
            Console.WriteLine "^C"
            result <- Some(Some "")
        | ConsoleKey.D when ctrl ->
            if buf.Length = 0 then
                Console.WriteLine()
                result <- Some None // EOF
            elif pos < buf.Length then
                buf.Remove(pos, 1) |> ignore // readline delete-char
                redraw ()
        | ConsoleKey.C when ctrl ->
            // cancel the line, keep the session
            Console.WriteLine "^C"
            result <- Some(Some "")
        | ConsoleKey.Backspace ->
            if pos > 0 then
                buf.Remove(pos - 1, 1) |> ignore
                pos <- pos - 1
                redraw ()
        | ConsoleKey.LeftArrow when ctrl ->
            // readline word-wise: skip separators, then the word
            let t = buf.ToString()
            let mutable p = pos

            while p > 0 && not (isWordChar t[p - 1]) do
                p <- p - 1

            while p > 0 && isWordChar t[p - 1] do
                p <- p - 1

            pos <- p
            redraw ()
        | ConsoleKey.RightArrow when ctrl ->
            let t = buf.ToString()
            let mutable p = pos

            while p < t.Length && not (isWordChar t[p]) do
                p <- p + 1

            while p < t.Length && isWordChar t[p] do
                p <- p + 1

            pos <- p
            redraw ()
        | ConsoleKey.LeftArrow when pos > 0 ->
            pos <- pos - 1
            Console.Write "\x1b[1D"
        | ConsoleKey.RightArrow when pos < buf.Length ->
            pos <- pos + 1
            Console.Write "\x1b[1C"
        | ConsoleKey.Home ->
            pos <- 0
            redraw ()
        | ConsoleKey.End ->
            pos <- buf.Length
            redraw ()
        | ConsoleKey.A when ctrl ->
            pos <- 0
            redraw ()
        | ConsoleKey.E when ctrl ->
            pos <- buf.Length
            redraw ()
        | ConsoleKey.U when ctrl ->
            buf.Remove(0, pos) |> ignore
            pos <- 0
            redraw ()
        | ConsoleKey.K when ctrl ->
            buf.Remove(pos, buf.Length - pos) |> ignore
            redraw ()
        | ConsoleKey.UpArrow ->
            if histIdx > 0 then
                if histIdx = history.Count then
                    draft <- buf.ToString()

                histIdx <- histIdx - 1
                setLine history[histIdx]
        | ConsoleKey.DownArrow ->
            if histIdx < history.Count then
                histIdx <- histIdx + 1
                setLine (if histIdx = history.Count then draft else history[histIdx])
        | ConsoleKey.Tab ->
            let text = buf.ToString()
            let ws = wordStartAt text pos
            let suggestions = Complete.suggest currentEnv.Value text ws

            (match suggestions with
             | [] -> ()
             | [ one ] ->
                 let replaced = text.Substring(0, ws) + one + text.Substring pos
                 buf.Clear().Append(replaced) |> ignore
                 pos <- ws + one.Length
                 redraw ()
             | many ->
                 // extend to the common prefix; list on a second Tab-worth
                 let prefix =
                     many
                     |> List.reduce (fun a b ->
                         let n = Seq.zip a b |> Seq.takeWhile (fun (x, y) -> x = y) |> Seq.length
                         a.Substring(0, n))

                 if prefix.Length > pos - ws then
                     let replaced = text.Substring(0, ws) + prefix + text.Substring pos
                     buf.Clear().Append(replaced) |> ignore
                     pos <- ws + prefix.Length
                     redraw ()
                 else
                     Console.WriteLine()
                     Console.WriteLine(String.concat "  " (many |> List.truncate 24))
                     redraw ())
        | _ when k.KeyChar >= ' ' ->
            buf.Insert(pos, k.KeyChar) |> ignore
            pos <- pos + 1
            redraw ()
        | _ -> ()

    result |> Option.defaultValue None

let private setupLineEditor () =
    Console.TreatControlCAsInput <- true // Ctrl+C is a KEY (cancel line), not SIGINT
    loadHistory ()

let private readInput () =
    if Console.IsInputRedirected then
        Console.Write prompt
        Console.ReadLine()
    else
        match readLineTty () with
        | None -> null // EOF: the loop's exit condition
        | Some line ->
            if line.Trim() <> "" then
                history.Add line

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
        Console.WriteLine(Script.Color.yellow Script.Color.onStdout.Value (underline w.Span))
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
            // the ONE pipeline [D:one-pipeline]: the REPL is a consumer, not a
            // replica — a single-line LogicalLine feeds checkStatement
            let ll: Script.LogicalLine =
                { Text = line
                  Head = 1
                  Segments = [ (0, 1, 0) ] }

            match Script.checkStatement false (fun _ -> resolver state) state.TypeEnv ll with
            | Error d when d.Parse ->
                // the input sits on the prompt line above — caret under it
                Console.WriteLine(
                    Script.Color.red Script.Color.onStdout.Value (String(' ', prompt.Length + d.PhysCol - 1) + "^")
                )

                Console.WriteLine d.Message
                printHint state line
                state
            | Error d ->
                d.Span
                |> Option.iter (underline >> Script.Color.red Script.Color.onStdout.Value >> Console.WriteLine)

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
                         Console.WriteLine(Script.Color.red Script.Color.onStdout.Value "error" + $": {ex.Message}")
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
                         Console.WriteLine(Script.Color.red Script.Color.onStdout.Value "error" + $": {ex.Message}")
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
                         Console.WriteLine(Script.Color.red Script.Color.onStdout.Value "error" + $": {ex.Message}")
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
