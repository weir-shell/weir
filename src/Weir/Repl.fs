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

// ---- the REPL config [D:repl-quality]: INERT data (values that tune an
// affordance, never anything that runs), read ONLY by the REPL. It lives in
// THIS module by design — scripts never touch Repl.fs, so `weir script.weir`
// provably ignores it; that is the property the whole language exists to keep.
type private ReplConfig =
    { HistorySize: int
      HistoryDedup: bool
      HistoryPath: string
      FinderFlags: string list }

let private xdgHome (var: string) (fallback: string) =
    match Environment.GetEnvironmentVariable var with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, fallback)
    | v -> v

let private defaultConfig =
    { HistorySize = 5000
      HistoryDedup = true
      // STATE, not config — history is data the REPL produced, not settings
      HistoryPath = Path.Combine(xdgHome "XDG_STATE_HOME" ".local/state", "weir", "history")
      FinderFlags = [ "--height"; "40%"; "--reverse" ] }

let private configKeys =
    set [ "historySize"; "historyDedup"; "historyPath"; "finderFlags" ]

// read $XDG_CONFIG_HOME/weir/config.json (fallback ~/.config/weir/config.json);
// unknown keys are REJECTED with did-you-mean (a typo silently doing nothing is
// the config-file's vacuous pin). Absent file / parse error -> defaults.
let private loadConfig () : ReplConfig =
    let path = Path.Combine(xdgHome "XDG_CONFIG_HOME" ".config", "weir", "config.json")

    if not (File.Exists path) then
        defaultConfig
    else
        try
            use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText path)
            let root = doc.RootElement

            for prop in root.EnumerateObject() do
                if not (configKeys.Contains prop.Name) then
                    Console.Error.WriteLine $"weir: config: unknown key '{prop.Name}'{didYouMean prop.Name configKeys}"

            let getInt (k: string) d =
                match root.TryGetProperty k with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.Number -> v.GetInt32()
                | _ -> d

            let getBool (k: string) d =
                match root.TryGetProperty k with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.True -> true
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.False -> false
                | _ -> d

            let getStr (k: string) d =
                match root.TryGetProperty k with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.String -> v.GetString()
                | _ -> d

            let getStrList (k: string) d =
                match root.TryGetProperty k with
                | true, v when v.ValueKind = System.Text.Json.JsonValueKind.Array ->
                    [ for e in v.EnumerateArray() do
                          if e.ValueKind = System.Text.Json.JsonValueKind.String then
                              e.GetString() ]
                | _ -> d

            { HistorySize = getInt "historySize" defaultConfig.HistorySize
              HistoryDedup = getBool "historyDedup" defaultConfig.HistoryDedup
              HistoryPath = getStr "historyPath" defaultConfig.HistoryPath
              FinderFlags = getStrList "finderFlags" defaultConfig.FinderFlags }
        with ex ->
            Console.Error.WriteLine $"weir: config: {ex.Message} (using defaults)"
            defaultConfig

let private config = loadConfig ()

let private historyFile = config.HistoryPath

// ---------------------------------------------------------------------------
// The owned line editor [D:owned-line-editor]: bash key semantics —
// Ctrl+C cancels the LINE, Ctrl+D on an empty line is EOF.

let private history = ResizeArray<string>()

// the history file is created 0600 [D:repl-quality] — a REPL line can carry a
// secret (`runEnv [Env.pair "TOKEN" "…"]`), so it is a place secrets land
let private ensureHistoryFile () =
    let dir = Path.GetDirectoryName historyFile

    if dir <> "" then
        Directory.CreateDirectory dir |> ignore

    if not (File.Exists historyFile) then
        (File.Create historyFile).Dispose()

    try
        File.SetUnixFileMode(historyFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    with _ ->
        ()

let private loadHistory () =
    if File.Exists historyFile then
        let lines = File.ReadAllLines historyFile
        // front-truncate to the cap ONCE at load (per-line append never
        // rewrites during a session — durability)
        let capped =
            if lines.Length > config.HistorySize then
                lines[lines.Length - config.HistorySize ..]
            else
                lines

        if capped.Length <> lines.Length then
            ensureHistoryFile ()
            File.WriteAllLines(historyFile, capped)

        history.AddRange capped

// per-line append with consecutive-dup dedup (readline's ignoredups)
let private appendHistory (line: string) =
    let dup =
        config.HistoryDedup && history.Count > 0 && history[history.Count - 1] = line

    if not dup then
        history.Add line

        try
            ensureHistoryFile ()
            File.AppendAllText(historyFile, line + Environment.NewLine)
        with _ ->
            ()

// Ctrl+Left/Right navigation; '.' stays a separator here (unlike
// completion's wordStartAt) so field chains hop segment by segment
let private isWordChar (c: char) = Char.IsLetterOrDigit c || c = '_'

let private wordStartAt (text: string) (pos: int) =
    let mutable i = pos

    while i > 0
          && (Char.IsLetterOrDigit text[i - 1] || text[i - 1] = '_' || text[i - 1] = '.') do
        i <- i - 1

    i

// ---- history search [D:repl-quality]: fzf when present (the good path,
// its spawn-and-restore proven), a minimal built-in otherwise. NEVER a
// "install fzf" message — behavior is defined either way. Returns the chosen
// line (whole line replaces the buffer), or None on cancel (buffer unchanged).

let private fzfSearch (query: string) : string option =
    try
        let psi = Diagnostics.ProcessStartInfo "fzf"

        // history lines are weir CODE, and weir's glyphs are fzf QUERY
        // OPERATORS in its extended-search mode (`^` prefix-anchor vs the
        // force-PATH sigil, `|` OR vs the pipe, `$` suffix, `!` negation) —
        // typing `^ls` would EXCLUDE every `^ls …` entry. Literal fuzzy
        // matching is the correct default for searching code, so extended
        // mode is off HERE (correctness, not style); fzf is last-flag-wins,
        // so finderFlags can restore it with `--extended`.
        psi.ArgumentList.Add "--no-extended"

        for f in config.FinderFlags do
            psi.ArgumentList.Add f

        if query <> "" then
            psi.ArgumentList.Add "--query"
            psi.ArgumentList.Add query

        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.UseShellExecute <- false // fzf draws its UI on /dev/tty directly
        use p = Diagnostics.Process.Start psi
        // feed history most-recent-first (its stdin is the pipe; the tty is fzf's)
        for i in history.Count - 1 .. -1 .. 0 do
            p.StandardInput.WriteLine history[i]

        p.StandardInput.Close()
        let sel = p.StandardOutput.ReadToEnd().TrimEnd('\n', '\r')
        p.WaitForExit()
        // exit 130 (Esc) -> cancel; only a clean selection replaces the line
        if p.ExitCode = 0 && sel <> "" then Some sel else None
    with _ ->
        None

// the fallback: incremental reverse substring search, most-recent-first,
// Esc cancels — sufficient because fzf is the good path
let private minimalSearch (query0: string) : string option =
    let mutable q = query0
    let mutable result = None
    let mutable searching = true

    let firstMatch () =
        seq { history.Count - 1 .. -1 .. 0 }
        |> Seq.map (fun i -> history[i])
        |> Seq.tryFind (fun h -> h.Contains q)

    let render () =
        let m = firstMatch () |> Option.defaultValue ""
        Console.Write $"\r(reverse-i-search)`{q}': {m}\x1b[K"

    render ()

    while searching do
        let k = Console.ReadKey true

        match k.Key with
        | ConsoleKey.Enter ->
            result <- firstMatch ()
            searching <- false
        | ConsoleKey.Escape ->
            result <- None
            searching <- false
        | ConsoleKey.Backspace ->
            if q.Length > 0 then
                q <- q.Substring(0, q.Length - 1)
                render ()
        | _ when k.KeyChar >= ' ' ->
            q <- q + string k.KeyChar
            render ()
        | _ -> ()

    Console.Write "\r\x1b[K"
    result

let private historySearch (query: string) : string option =
    if Extern.exists "fzf" then
        fzfSearch query
    else
        minimalSearch query

/// returns None on EOF (Ctrl+D at an empty line)
let private readLineTty () : string option =
    Console.Write prompt
    let buf = Text.StringBuilder()
    let mutable pos = 0
    let mutable histIdx = history.Count // one past the end = the new line
    let mutable draft = ""

    let redraw () =
        // paint-only coloring [D:repl-color]: buf/pos never hold
        // escapes, so cursor math below stays plain-text; ANSI spans
        // are zero display columns
        let painted =
            if Script.Color.onStdout.Value then
                let env = currentEnv.Value

                let isKnown n =
                    Map.containsKey n env.Values
                    || Map.containsKey n env.Modules
                    || Builtins.commandCallable.Contains n

                Script.colorizeRepl isKnown (buf.ToString())
            else
                buf.ToString()

        Console.Write("\r" + prompt + painted + "\x1b[K")
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
        | ConsoleKey.R when ctrl ->
            // history search [D:repl-quality]: the selection REPLACES the
            // whole line (a history entry is a line, not an insertion); a
            // cancel leaves buf/pos untouched
            (match historySearch (buf.ToString()) with
             | Some line ->
                 buf.Clear().Append line |> ignore
                 pos <- buf.Length
             | None -> ())

            redraw ()
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
            // suggest's contract: text ends at the CURSOR — the tail past
            // it must not leak into the word (the mid-line receipt: the
            // typed closer ` })` became part of the prefix and killed
            // every match); insertion below re-attaches the tail
            let suggestions = Complete.suggest currentEnv.Value (text.Substring(0, pos)) ws

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
                appendHistory line

            line

let private underline (span: Span) : string =
    String(' ', prompt.Length + span.Start.Col - 1)
    + String('^', max 1 (span.End.Col - span.Start.Col))

let private printWarnings (state: State) (te: Check.TypedExpr) =
    Check.warnings te
    |> List.iter (fun w ->
        Console.WriteLine(Script.Color.yellow Script.Color.onStdout.Value (underline w.Span))
        Console.WriteLine(Check.formatWarning w))

let private resolver (state: State) : Parser.Resolver = Script.resolver state.TypeEnv

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
            // [D:one-pipeline]: a single-line LogicalLine feeds
            // checkStatement; the REPL only renders
            let ll = Script.singleLine line

            match Script.checkStatement false (fun _ -> resolver state) Script.scriptOnlyImport state.TypeEnv ll with
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
                 | Some sp ->
                     Console.WriteLine(
                         Check.formatError
                             { Span = sp
                               Message = d.Message
                               Origin = None }
                     )
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
                            let rendered, hint = Eval.echoValue v

                            let tail = Eval.echoTail (te.Ty = TSeq TStr) hint

                            Console.WriteLine $"{name} : {formatTy te.Ty} = {rendered}{tail}"

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
                            let rendered, hint = Eval.echoValue v

                            let tail = Eval.echoTail (te.Ty = TSeq TStr) hint

                            Console.WriteLine $"{rendered} : {formatTy te.Ty}{tail}"

                        state
                     with
                     | Eval.ExitRequest _ -> reraise ()
                     | ex ->
                         Console.WriteLine(Script.Color.red Script.Color.onStdout.Value "error" + $": {ex.Message}")
                         state)
                | Script.KModule _ ->
                    Console.WriteLine
                        "the REPL has no file to be a module of; 'module' belongs at the top of a script file"

                    state
                | Script.KImport _ ->
                    // unreachable: scriptOnlyImport rejects imports at check
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
