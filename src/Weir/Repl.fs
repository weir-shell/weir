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

// Windows has no XDG: config -> %APPDATA%, state -> %LOCALAPPDATA%
// [D:windows-v1]. POSIX unchanged (XDG var, else ~/.config | ~/.local/state).
let private configHome () =
    if OperatingSystem.IsWindows() then
        Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
    else
        xdgHome "XDG_CONFIG_HOME" ".config"

let private stateHome () =
    if OperatingSystem.IsWindows() then
        Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData
    else
        xdgHome "XDG_STATE_HOME" ".local/state"

let private defaultConfig =
    { HistorySize = 5000
      HistoryDedup = true
      // STATE, not config — history is data the REPL produced, not settings
      HistoryPath = Path.Combine(stateHome (), "weir", "history")
      FinderFlags = [ "--height"; "40%"; "--reverse" ] }

let private configKeys =
    set [ "historySize"; "historyDedup"; "historyPath"; "finderFlags" ]

// read $XDG_CONFIG_HOME/weir/config.json (fallback ~/.config/weir/config.json);
// unknown keys are REJECTED with did-you-mean (a typo silently doing nothing is
// the config-file's vacuous pin). Absent file / parse error -> defaults.
let private loadConfig () : ReplConfig =
    let path = Path.Combine(configHome (), "weir", "config.json")

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

// history holds LOGICAL entries [D:repl-multiline] — a multi-line match is
// ONE entry, recalled whole. In-memory entries carry real newlines.
let private history = ResizeArray<string>()

// on disk: one entry per FILE LINE (the cap counts lines = entries, the
// file stays greppable), newlines backslash-ESCAPED per entry — `\` -> `\\`,
// newline -> `\n`. Decode reverses. A legacy plain-line file reads fine;
// a legacy entry containing a literal `\n` (a weir string like
// Str.split "\n") decodes with a real newline — accepted NOW, pre-adoption,
// while the fix is an amendment and not a migration [D:repl-multiline].
let private encodeEntry (entry: string) =
    entry.Replace("\\", "\\\\").Replace("\n", "\\n")

let private decodeEntry (line: string) =
    let sb = Text.StringBuilder()
    let mutable i = 0

    while i < line.Length do
        if line[i] = '\\' && i + 1 < line.Length then
            sb.Append(if line[i + 1] = 'n' then '\n' else line[i + 1]) |> ignore
            i <- i + 2
        else
            sb.Append line[i] |> ignore
            i <- i + 1

    sb.ToString()

// the one-line DISPLAY form for search UIs [D:repl-multiline] — fzf matches
// per line, so a multi-line entry feeds as its lines joined with ⏎ (every
// line stays searchable, unlike first-line-plus-ellipsis)
let private displayEntry (entry: string) = entry.Replace("\n", " ⏎ ")

// the history file is created 0600 [D:repl-quality] — a REPL line can carry a
// secret (`runEnv [Env.pair "TOKEN" "…"]`), so it is a place secrets land
let private ensureHistoryFile () =
    let dir = Path.GetDirectoryName historyFile

    if dir <> "" then
        Directory.CreateDirectory dir |> ignore

    if not (File.Exists historyFile) then
        (File.Create historyFile).Dispose()

    // 0600 on POSIX. On Windows there is no chmod: the file inherits
    // %LOCALAPPDATA%'s ACLs, which already deny other non-admin users —
    // equivalent protection by inheritance, stated in SECURITY.md, not
    // silently skipped [D:windows-v1]
    if not (OperatingSystem.IsWindows()) then
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

        history.AddRange(capped |> Array.map decodeEntry)

// per-ENTRY append with consecutive-dup dedup (readline's ignoredups);
// the dedup compares WHOLE entries [D:repl-multiline]
let private appendHistory (entry: string) =
    let dup =
        config.HistoryDedup && history.Count > 0 && history[history.Count - 1] = entry

    if not dup then
        history.Add entry

        try
            ensureHistoryFile ()
            File.AppendAllText(historyFile, encodeEntry entry + Environment.NewLine)
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
        // feed history most-recent-first as one-line DISPLAY forms (fzf
        // matches per line); the selection maps back to the full entry —
        // identical displays imply identical text, so the map is lossless
        let byDisplay = Collections.Generic.Dictionary<string, string>()

        for i in history.Count - 1 .. -1 .. 0 do
            let d = displayEntry history[i]

            if not (byDisplay.ContainsKey d) then
                byDisplay[d] <- history[i]

            p.StandardInput.WriteLine d

        p.StandardInput.Close()
        let sel = p.StandardOutput.ReadToEnd().TrimEnd('\n', '\r')
        p.WaitForExit()
        // exit 130 (Esc) -> cancel; only a clean selection replaces the line
        if p.ExitCode = 0 && sel <> "" then
            match byDisplay.TryGetValue sel with
            | true, entry -> Some entry
            | _ -> Some sel
        else
            None
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
        let m = firstMatch () |> Option.map displayEntry |> Option.defaultValue ""
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

// is the buffer a COMPLETE statement? [D:repl-multiline] — the assembler
// answers STRUCTURE (open brackets, pending bindings, dangling openers);
// a parse failure AT THE VERY END of the assembled text means "more input
// wanted". A mid-text failure is a real error and SUBMITS (the message
// shows) — the user is never trapped adding newlines.
// at the REPL a leading-space FIRST line has no statement above to
// continue, so its indentation carries no meaning [D:windows-s2]: the
// whole buffer dedents by the first line's indent (relative structure
// inside the entry is preserved — a pasted indented block keeps its
// shape). Scripts are untouched; this is REPL input handling only.
let private dedentEntry (bufLines: string list) : string list =
    match bufLines |> List.tryFind (fun l -> l.Trim() <> "") with
    | Some first when first.Length > first.TrimStart(' ').Length ->
        let n = first.Length - (first.TrimStart ' ').Length

        bufLines
        |> List.map (fun (l: string) ->
            let indent = l.Length - (l.TrimStart ' ').Length
            l.Substring(min n indent))
    | _ -> bufLines

let private bufferComplete (bufLines: string list) : bool =
    let numbered =
        dedentEntry bufLines
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> Script.classifyLine raw <> Script.LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, Script.stripComment raw)

    if List.isEmpty numbered then
        true
    // weir strings are SINGLE-LINE: a line ending inside one can never be
    // completed by more input — submit (the parse error shows) rather
    // than trap the user growing an unfixable buffer
    elif bufLines |> List.exists Script.endsInsideString then
        true
    else
        match Script.assemble numbered with
        | Error _ -> false
        | Ok lls ->
            let r = Script.resolver currentEnv.Value

            lls
            |> List.forall (fun ll ->
                match Parser.parseLineFull r ll.Text with
                | Ok _ -> true
                | Error f ->
                    match f.Col with
                    | Some c -> c <= ll.Text.TrimEnd().Length
                    | None -> true)

// the continuation prompt — SAME WIDTH as "weir> " so column math is
// uniform across rows [D:repl-multiline]
let private contPrompt = "  ... "

// the live editor's repaint hook for SIGWINCH (full repaint on resize;
// best-effort — the climb to the region top uses pre-resize wrap math)
let private activeRedraw: (unit -> unit) option ref = ref None

/// returns None on EOF (Ctrl+D at an empty buffer); Some entry (lines
/// joined with \n) otherwise. The buffer is TWO-DIMENSIONAL
/// [D:repl-multiline]: a list of lines plus a (row, col) cursor; the
/// horizontal machinery applies per line unchanged.
let private readLineTty () : string option =
    let lines = ResizeArray<Text.StringBuilder>()
    lines.Add(Text.StringBuilder())
    let mutable row = 0
    let mutable col = 0
    let mutable histIdx = history.Count // one past the end = the new entry
    let mutable draft = ""
    // display rows between the region TOP and the cursor at the last
    // paint — the way back up through wraps
    let mutable lastCursorDisplay = 0

    let termWidth () =
        try
            max 20 Console.WindowWidth
        with _ ->
            80

    // display rows a buffer line occupies at width w (prompt included);
    // a line filling its final row exactly leaves the terminal
    // wrap-PENDING, which the ceil and the \r\n emission agree about
    let dispRows (w: int) (len: int) = max 1 ((6 + len + w - 1) / w)

    // (display-row offset from region top, display column) of the cursor
    let cursorDisplay (w: int) =
        let mutable above = 0

        for i in 0 .. row - 1 do
            above <- above + dispRows w lines[i].Length

        let dc = (6 + col) % w
        let dr = (6 + col) / w

        if dc = 0 && col > 0 then
            above + dr - 1, w
        else
            above + dr, dc

    let cur () = lines[row]

    let redraw () =
        // region repaint [D:repl-multiline]: climb to the region top (the
        // tracked cursor offset), clear to screen end, repaint every line
        // with its prompt, reposition by display-row math. The colorizer
        // applies per line [D:repl-color] — buffers never hold escapes,
        // so the math stays plain-text.
        let w = termWidth ()
        let out = Text.StringBuilder()
        out.Append '\r' |> ignore

        if lastCursorDisplay > 0 then
            out.Append $"\x1b[{lastCursorDisplay}A" |> ignore

        out.Append "\x1b[J" |> ignore

        let env = currentEnv.Value

        let isKnown n =
            Map.containsKey n env.Values
            || Map.containsKey n env.Modules
            || Builtins.commandCallable.Contains n

        let mutable totalRows = 0

        // the dedent's THIRD consumer [D:windows-s3]: head verdicts run
        // on the DEDENTED text (what will parse), painted back behind the
        // typed prefix — bufferComplete, submission, and the colorizer
        // must share ONE dedent or the verdict and the paint split
        let bufTexts = [ for i in 0 .. lines.Count - 1 -> lines[i].ToString() ]
        let dedented = dedentEntry bufTexts

        for i in 0 .. lines.Count - 1 do
            let text = bufTexts[i]
            let ded = dedented[i]

            let painted =
                if Types.Color.onStdout.Value then
                    text.Substring(0, text.Length - ded.Length) + Script.colorizeRepl isKnown ded
                else
                    text

            out.Append(if i = 0 then prompt else contPrompt).Append painted |> ignore
            totalRows <- totalRows + dispRows w text.Length

            if i < lines.Count - 1 then
                out.Append "\r\n" |> ignore

        // the paint leaves the cursor at the END of the last line; walk
        // back up to the (row, col) target
        let curDisplay, curCol = cursorDisplay w
        let up = totalRows - 1 - curDisplay

        if up > 0 then
            out.Append $"\x1b[{up}A" |> ignore

        out.Append '\r' |> ignore

        if curCol > 0 then
            out.Append $"\x1b[{curCol}C" |> ignore

        Console.Write(out.ToString())
        lastCursorDisplay <- curDisplay

    activeRedraw.Value <- Some redraw
    redraw ()

    let bufText () =
        String.Join("\n", lines |> Seq.map (fun sb -> sb.ToString()))

    let setBuffer (entry: string) =
        lines.Clear()

        for l in entry.Split '\n' do
            lines.Add(Text.StringBuilder(l: string))

        row <- lines.Count - 1
        col <- lines[row].Length
        redraw ()

    // split the current line at the cursor — Enter-on-incomplete and the
    // Alt+Enter / Ctrl+J force share it
    let insertNewline () =
        let tail = cur().ToString().Substring col
        cur().Remove(col, cur().Length - col) |> ignore
        lines.Insert(row + 1, Text.StringBuilder(tail: string))
        row <- row + 1
        col <- 0
        redraw ()

    // park the cursor at the region end (echoes print BELOW the buffer)
    let toEnd () =
        row <- lines.Count - 1
        col <- lines[row].Length
        redraw ()

    let mutable result: string option option = None

    while result.IsNone do
        let k = Console.ReadKey(intercept = true)
        let ctrl = k.Modifiers.HasFlag ConsoleModifiers.Control
        let alt = k.Modifiers.HasFlag ConsoleModifiers.Alt

        match k.Key with
        // Alt+Enter (and Ctrl+J below): FORCE a newline even when the
        // statement is complete — formatting, not a second statement
        // (an entry stays ONE statement) [D:repl-multiline]
        | ConsoleKey.Enter when alt -> insertNewline ()
        | _ when k.KeyChar = '\n' -> insertNewline ()
        | ConsoleKey.Enter ->
            // submit when the statement is COMPLETE; grow the buffer when
            // it is not — the parser's own answer, not an approximation
            let text = bufText ()
            let bufList = lines |> Seq.map (fun sb -> sb.ToString()) |> List.ofSeq

            // blank-line ESCAPE [D:windows-s2]: Enter on an empty FINAL
            // line closes a pending buffer even when incomplete — the
            // parse error shows and the input is kept, instead of Ctrl+C
            // being the only (input-losing) way out of an uncompletable
            // state. Scripts unchanged: blanks are transparent there.
            // Alt+Enter/Ctrl+J never trigger it (deliberate newlines).
            let blankEscape =
                row = lines.Count - 1
                && cur().ToString().Trim() = ""
                && bufList |> List.exists (fun l -> l.Trim() <> "")

            if bufferComplete bufList || blankEscape then
                toEnd ()
                Console.WriteLine()
                result <- Some(Some text)
            else
                insertNewline ()
        // some terminals deliver control chords as bare KeyChars —
        // match the codes as well as the (Key, Modifier) pairs
        | _ when k.KeyChar = '' ->
            if lines.Count = 1 && lines[0].Length = 0 then
                Console.WriteLine()
                result <- Some None
            elif col < cur().Length then
                cur().Remove(col, 1) |> ignore
                redraw ()
            elif row < lines.Count - 1 then
                cur().Append(lines[row + 1].ToString()) |> ignore
                lines.RemoveAt(row + 1)
                redraw ()
        | _ when k.KeyChar = '' ->
            Console.WriteLine "^C"
            result <- Some(Some "")
        | ConsoleKey.D when ctrl ->
            if lines.Count = 1 && lines[0].Length = 0 then
                Console.WriteLine()
                result <- Some None // EOF
            elif col < cur().Length then
                cur().Remove(col, 1) |> ignore // readline delete-char
                redraw ()
            elif row < lines.Count - 1 then
                // delete at line end joins the next line
                cur().Append(lines[row + 1].ToString()) |> ignore
                lines.RemoveAt(row + 1)
                redraw ()
        | ConsoleKey.C when ctrl ->
            // abandon the WHOLE buffer, keep the session
            toEnd ()
            Console.WriteLine "^C"
            result <- Some(Some "")
        | ConsoleKey.Escape ->
            // Esc mid-buffer: abandon the whole buffer [D:repl-multiline]
            toEnd ()
            Console.WriteLine()
            result <- Some(Some "")
        | ConsoleKey.R when ctrl ->
            // history search [D:repl-quality]: the selection REPLACES the
            // whole buffer (an entry is a statement, not an insertion); a
            // cancel leaves the buffer untouched
            (match historySearch (displayEntry (bufText ())) with
             | Some entry -> setBuffer entry
             | None -> redraw ())
        | ConsoleKey.Backspace ->
            if col > 0 then
                cur().Remove(col - 1, 1) |> ignore
                col <- col - 1
                redraw ()
            elif row > 0 then
                // backspace at line start joins the previous line
                let prevLen = lines[row - 1].Length
                lines[row - 1].Append(cur().ToString()) |> ignore
                lines.RemoveAt row
                row <- row - 1
                col <- prevLen
                redraw ()
        | ConsoleKey.LeftArrow when ctrl ->
            // readline word-wise: skip separators, then the word
            let t = cur().ToString()
            let mutable p = col

            while p > 0 && not (isWordChar t[p - 1]) do
                p <- p - 1

            while p > 0 && isWordChar t[p - 1] do
                p <- p - 1

            col <- p
            redraw ()
        | ConsoleKey.RightArrow when ctrl ->
            let t = cur().ToString()
            let mutable p = col

            while p < t.Length && not (isWordChar t[p]) do
                p <- p + 1

            while p < t.Length && isWordChar t[p] do
                p <- p + 1

            col <- p
            redraw ()
        | ConsoleKey.LeftArrow ->
            if col > 0 then
                col <- col - 1
                redraw ()
            elif row > 0 then
                row <- row - 1
                col <- lines[row].Length
                redraw ()
        | ConsoleKey.RightArrow ->
            if col < cur().Length then
                col <- col + 1
                redraw ()
            elif row < lines.Count - 1 then
                row <- row + 1
                col <- 0
                redraw ()
        | ConsoleKey.Home ->
            col <- 0
            redraw ()
        | ConsoleKey.End ->
            col <- cur().Length
            redraw ()
        | ConsoleKey.A when ctrl ->
            col <- 0
            redraw ()
        | ConsoleKey.E when ctrl ->
            col <- cur().Length
            redraw ()
        | ConsoleKey.U when ctrl ->
            cur().Remove(0, col) |> ignore
            col <- 0
            redraw ()
        | ConsoleKey.K when ctrl ->
            cur().Remove(col, cur().Length - col) |> ignore
            redraw ()
        | ConsoleKey.UpArrow ->
            // Up WITHIN the buffer; history only from the FIRST line
            // (the fish/ipython convention; Ctrl+R is the explicit path)
            if row > 0 then
                row <- row - 1
                col <- min col lines[row].Length
                redraw ()
            elif histIdx > 0 then
                if histIdx = history.Count then
                    draft <- bufText ()

                histIdx <- histIdx - 1
                setBuffer history[histIdx]
        | ConsoleKey.DownArrow ->
            // Down within the buffer; at the last line, forward through
            // history ONLY while already browsing it — a fresh buffer's
            // last line is a no-op (Up's asymmetry) [D:repl-multiline]
            if row < lines.Count - 1 then
                row <- row + 1
                col <- min col lines[row].Length
                redraw ()
            elif histIdx < history.Count then
                histIdx <- histIdx + 1

                setBuffer (if histIdx = history.Count then draft else history[histIdx])
        | ConsoleKey.Tab ->
            // completion operates on the CURRENT line (per-line machinery)
            let text = cur().ToString()
            let ws = wordStartAt text col
            // suggest's contract: text ends at the CURSOR — the tail past
            // it must not leak into the word (the mid-line receipt: the
            // typed closer ` })` became part of the prefix and killed
            // every match); insertion below re-attaches the tail
            let suggestions = Complete.suggest currentEnv.Value (text.Substring(0, col)) ws

            (match suggestions with
             | [] -> ()
             | [ one ] ->
                 let replaced = text.Substring(0, ws) + one + text.Substring col
                 cur().Clear().Append(replaced) |> ignore
                 col <- ws + one.Length
                 redraw ()
             | many ->
                 // extend to the common prefix; list on a second Tab-worth
                 let prefix =
                     many
                     |> List.reduce (fun a b ->
                         let n = Seq.zip a b |> Seq.takeWhile (fun (x, y) -> x = y) |> Seq.length
                         a.Substring(0, n))

                 if prefix.Length > col - ws then
                     let replaced = text.Substring(0, ws) + prefix + text.Substring col
                     cur().Clear().Append(replaced) |> ignore
                     col <- ws + prefix.Length
                     redraw ()
                 else
                     toEnd ()
                     Console.WriteLine()
                     Console.WriteLine(String.concat "  " (many |> List.truncate 24))
                     lastCursorDisplay <- 0
                     redraw ())
        | _ when k.KeyChar >= ' ' ->
            cur().Insert(col, k.KeyChar) |> ignore
            col <- col + 1
            redraw ()
        | _ -> ()

    activeRedraw.Value <- None
    result |> Option.defaultValue None

let private setupLineEditor () =
    Console.TreatControlCAsInput <- true // Ctrl+C is a KEY (cancel line), not SIGINT
    // full repaint on SIGWINCH [D:repl-multiline] — best-effort (the climb
    // to the region top uses pre-resize wrap math). No SIGWINCH on
    // Windows; Create throws there, so the guard is load-bearing
    // [D:windows-v1] — resize repaint is simply absent.
    if not (OperatingSystem.IsWindows()) then
        Runtime.InteropServices.PosixSignalRegistration.Create(
            Runtime.InteropServices.PosixSignal.SIGWINCH,
            fun _ -> activeRedraw.Value |> Option.iter (fun f -> f ())
        )
        |> ignore

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
        Console.WriteLine(Types.Color.yellow Types.Color.onStdout.Value (underline w.Span))
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

// the Ok-side rendering, shared by the single-line and multiline
// submission paths [D:repl-multiline]
let private evalChecked (state: State) (chk: Script.CheckedStatement) : State =
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
             Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
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
             Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
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
             Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
             state)
    | Script.KModule _ ->
        Console.WriteLine "the REPL has no file to be a module of; 'module' belongs at the top of a script file"

        state
    | Script.KImport _ ->
        // unreachable: scriptOnlyImport rejects imports at check
        state

let rec private loop (state: State) =
    currentEnv.Value <- state.TypeEnv

    match readInput () with
    | null
    | ":q" -> ()
    | line when String.IsNullOrWhiteSpace line -> loop state
    | entry when entry.Contains '\n' ->
        // a MULTILINE entry [D:repl-multiline]: the same assembler the
        // script runner uses turns the buffer into logical lines — the
        // submitted text means exactly what the same lines mean in a file
        Extern.refresh ()
        // the same dedent bufferComplete judged by — the two must agree
        // or Enter's verdict and the submission's meaning split
        let srcLines = entry.Split '\n' |> Array.toList |> dedentEntry |> Array.ofList

        let numbered =
            srcLines
            |> Array.toList
            |> List.mapi (fun i l -> i + 1, l)
            |> List.filter (fun (_, raw) -> Script.classifyLine raw <> Script.LineKind.CommentOnly)
            |> List.map (fun (n, raw) -> n, Script.stripComment raw)

        let next =
            match Script.assemble numbered with
            | Error msg ->
                Console.WriteLine msg
                state
            | Ok lls ->
                lls
                |> List.fold
                    (fun st ll ->
                        match
                            Script.checkStatement false (fun _ -> resolver st) Script.scriptOnlyImport st.TypeEnv ll
                        with
                        | Error d ->
                            // script-style rendering: the offending source
                            // line + caret + message (the buffer's echo is
                            // rows above; reprinting is deterministic)
                            let src =
                                if d.PhysLine >= 1 && d.PhysLine <= srcLines.Length then
                                    srcLines[d.PhysLine - 1]
                                else
                                    ""

                            Console.WriteLine src

                            Console.WriteLine(
                                Types.Color.red Types.Color.onStdout.Value (String(' ', max 0 (d.PhysCol - 1)) + "^")
                            )

                            Console.WriteLine(if d.Parse then d.Message else $"type error: {d.Message}")
                            st
                        | Ok chk -> evalChecked st chk)
                    state

        loop next
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
                    Types.Color.red Types.Color.onStdout.Value (String(' ', prompt.Length + d.PhysCol - 1) + "^")
                )

                Console.WriteLine d.Message
                printHint state line
                state
            | Error d ->
                d.Span
                |> Option.iter (underline >> Types.Color.red Types.Color.onStdout.Value >> Console.WriteLine)

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
            | Ok chk -> evalChecked state chk

        loop next

let run () =
    if not Console.IsInputRedirected then
        setupLineEditor ()

    try
        loop initial
        0
    with Eval.ExitRequest code ->
        code
