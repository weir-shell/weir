module Weir.Repl

open System
open System.IO
open Weir.Ast
open Weir.Types

let private prompt = "weir> "

// the prompt's status tint [D:red-prompt]: TRUE after an entry ends in
// a printed error (parse, check, or eval), FALSE after one executes
// clean — a REIFIED nonzero exit (`cmd | exitCode`, `| complete`) is
// DATA and never reddens (the reifier family's point); directives
// clear like any succeeding entry; blank/comment no-ops leave it
// untouched (bash's own $? behavior for empty input). Column math
// everywhere counts prompt.Length — the tint is zero-width.
let mutable private lastErrored = false

// the cooked-terminal trap [D:repl-cooked-trap]: when a child that shares
// the terminal runs long enough, .NET restores cooked mode for it and can
// fail to re-apply its raw config afterwards (the managed surface offers
// no way to force it — ReadKey latches whatever mode it entered with).
// So weir snapshots the raw termios .NET itself established and
// re-asserts it at each editor entry. POSIX-only; Windows has no termios
// and no such restore path.
module private Term =
    open System.Runtime.InteropServices

    [<DllImport("libc", SetLastError = true)>]
    extern int private tcgetattr(int fd, byte[] termios)

    [<DllImport("libc", SetLastError = true)>]
    extern int private tcsetattr(int fd, int optionalActions, byte[] termios)

    // 128 bytes covers struct termios on linux (~60) and macOS (~72)
    let mutable private raw: byte[] option = None

    /// capture once, right after a ReadKey — the one moment raw mode is
    /// known to be .NET's own configuration
    let snapshot () =
        if not (System.OperatingSystem.IsWindows()) && raw.IsNone then
            let buf = Array.zeroCreate<byte> 128

            if tcgetattr (0, buf) = 0 then
                raw <- Some buf

    /// re-apply the captured raw mode (TCSANOW = 0); a no-op before the
    /// first snapshot and on Windows
    let reassert () =
        if not (System.OperatingSystem.IsWindows()) then
            match raw with
            | Some buf -> tcsetattr (0, 0, buf) |> ignore
            | None -> ()

    // the SHELL's cooked termios, captured at run() before the editor's
    // first raw entry — ISIG included. Eval runs under THIS disposition
    // [D:repl-isig], so a ^C is a group SIGINT and a foreground child
    // dies the way it does in a script; the editor's raw config (ISIG
    // alone cleared — TreatControlCAsInput's footprint) re-asserts at
    // the eval boundary and at each editor entry.
    let mutable private cooked: byte[] option = None

    let snapshotCooked () =
        if not (System.OperatingSystem.IsWindows()) && cooked.IsNone then
            let buf = Array.zeroCreate<byte> 128

            if tcgetattr (0, buf) = 0 then
                cooked <- Some buf

    let restoreCooked () =
        if not (System.OperatingSystem.IsWindows()) then
            match cooked with
            | Some buf -> tcsetattr (0, 0, buf) |> ignore
            | None -> ()

    /// the editor-active gate for the watchdog below — children run
    /// during EVAL, when this is false, so the watchdog never fights an
    /// interactive child (fzf, an editor) for the terminal
    let editorActive = ref false

    let mutable private watchdogStarted = false

    /// .NET's restore lands ASYNC after the child's reap — later than any
    /// single re-assert at editor entry can outwait. A watchdog re-applies
    /// the raw config whenever the live termios drifts while the editor
    /// owns the prompt.
    let startWatchdog () =
        if not (System.OperatingSystem.IsWindows()) && not watchdogStarted then
            watchdogStarted <- true

            let t =
                System.Threading.Thread(
                    (fun () ->
                        while true do
                            System.Threading.Thread.Sleep 50

                            if editorActive.Value then
                                match raw with
                                | Some good ->
                                    let cur = Array.zeroCreate<byte> 128

                                    if tcgetattr (0, cur) = 0 && cur <> good then
                                        tcsetattr (0, 0, good) |> ignore
                                | None -> ()),
                    IsBackground = true
                )

            t.Start()

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
      FinderFlags: string list
      // REVIVED [D:echo-cap]: cut as unwired once (repl-quality) — the
      // wiring exists now (the session cap), so the key is real again
      EchoElems: int }

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
      FinderFlags = [ "--height"; "40%"; "--reverse" ]
      EchoElems = 100 }

let private configKeys =
    set [ "historySize"; "historyDedup"; "historyPath"; "finderFlags"; "echoElems" ]

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
              FinderFlags = getStrList "finderFlags" defaultConfig.FinderFlags
              EchoElems = getInt "echoElems" defaultConfig.EchoElems }
        with ex ->
            Console.Error.WriteLine $"weir: config: {ex.Message} (using defaults)"
            defaultConfig

let private config = loadConfig ()

// the SESSION echo cap [D:echo-cap]: config seeds it, #echo moves it;
// None = uncapped. A non-positive config value cannot mean anything
// (Seq.truncate refuses it) — say so once and keep the default.
let mutable private echoCap: int option =
    if config.EchoElems > 0 then
        Some config.EchoElems
    else
        Console.Error.WriteLine $"weir: config: echoElems must be positive; got {config.EchoElems} (using 100)"
        Some 100

let private historyFile = config.HistoryPath

// the streaming echo's stop reasons [D:stream-echo]
type internal EchoStopReason =
    | CleanBinary
    | MidBinary
    | Clipped

exception internal EchoStop of EchoStopReason

// the table's tint is POSITIONAL [D:table-polish]: echoTable's lines
// stay plain (one law, tests untouched) — the printer knows line 0 is
// the header, line 1 the rule, a trailing "…" the clip row. Cells are
// DATA and stay untinted; NO_COLOR and piped ride Color's own gate.
let private printTable (lines: string list) =
    let on = Types.Color.onStdout.Value

    lines
    |> List.iteri (fun i l ->
        Console.WriteLine(
            if i = 0 then Types.Color.bold on l
            elif i = 1 || l = "…" then Types.Color.dim on l
            else l
        ))

// the echo's metadata line (name : ty = / : ty (hint)) recedes — dim
// at a tty, plain elsewhere [D:table-polish]
let private echoMeta (s: string) =
    Console.WriteLine(Types.Color.dim Types.Color.onStdout.Value s)

// the live terminal width for the table clamp — piped echoes never
// tabulate, so None only guards the resize/console-less edge
let private termWidth () =
    try
        Some(max 20 Console.WindowWidth)
    with _ ->
        None

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

// completion's word rule is Complete's, never a copy — the copy is how the
// two drifted from filesystemComplete when argv paths landed
let private wordStartAt (text: string) (pos: int) = Complete.wordStartAt text pos

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

        // fzf (≥0.52) pushes the kitty keyboard protocol on the tty; a
        // quirky exit can leave it PUSHED, after which Ctrl+C arrives as
        // CSI-u DATA (\x1b[99;5u) instead of SIGINT — the unkillable-child
        // incident [D:binary-echo]. Pop unconditionally: popping an empty
        // stack is a no-op by the protocol's own spec.
        if not Console.IsOutputRedirected then
            Console.Out.Write "\x1b[<u"
            Console.Out.Flush()
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

    // (display-row offset from region top, display column) of the cursor.
    // At an EXACT wrap boundary ((6+col) % w = 0) the logical column has
    // two screen positions [D:windows-findings]: MID-line the true one is
    // START of the next row (that row exists — more text is painted on
    // it); at END of line it is the wrap-PENDING cell (the terminal never
    // wrapped, so the next row does not exist) — the last column, named
    // explicitly rather than emitted as an off-screen w that the terminal
    // clamps (the clamp was the column-N ambiguity: two logical columns
    // painted at one cell, then the crossing jumped two)
    let cursorDisplay (w: int) =
        let mutable above = 0

        for i in 0 .. row - 1 do
            above <- above + dispRows w lines[i].Length

        let dc = (6 + col) % w
        let dr = (6 + col) / w

        if dc = 0 && col > 0 then
            if col < lines[row].Length then
                above + dr, 0
            else
                above + dr - 1, w - 1
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

            let p0 =
                // the status tint [D:red-prompt] — zero-width dressing;
                // every width computation keeps counting prompt.Length
                if lastErrored then
                    Types.Color.red Types.Color.onStdout.Value prompt
                else
                    prompt

            out.Append(if i = 0 then p0 else contPrompt).Append painted |> ignore
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

    // a slow child's exit can leave the terminal COOKED (ICRNL on) behind
    // .NET's cached config — every later Enter then arrives as '\n', the
    // force-newline key, and the buffer can never submit again. Re-assert
    // the known-good raw mode at editor entry; the caller flags the
    // editor active so the watchdog holds it [D:repl-cooked-trap].
    Term.reassert ()
    Term.startWatchdog ()

    while result.IsNone do
        let k = Console.ReadKey(intercept = true)
        Term.snapshot ()
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
                     // park at the region end so the list prints BELOW the
                     // buffer — but the tracked (row, col) must survive:
                     // toEnd() mutates it, and the repaint then put the real
                     // cursor at end-of-buffer, past any text after the
                     // cursor (one bug, no state — a second Tab completed
                     // from where the cursor genuinely was)
                     // [D:windows-findings]
                     let keepRow, keepCol = row, col
                     toEnd ()
                     Console.WriteLine()
                     Console.WriteLine(String.concat "  " (many |> List.truncate 24))
                     row <- keepRow
                     col <- keepCol
                     lastCursorDisplay <- 0
                     redraw ())
        | _ when k.KeyChar >= ' ' ->
            cur().Insert(col, k.KeyChar) |> ignore
            col <- col + 1
            redraw ()
        | _ -> ()

    activeRedraw.Value <- None
    result |> Option.defaultValue None

// ROOTED: a collected PosixSignalRegistration disposes and stops
// cancelling — the sweep-hook roots set the precedent [D:exit-hook]
let mutable private sigintSurvival: obj option = None

// TRUE only when the tty editor exists [D:repl-isig]: the eval-boundary
// toggle is that editor's un-doing, and the TreatControlCAsInput SETTER
// throws on Windows with redirected input ("the handle is invalid" — a
// piped REPL has no console). POSIX-scoped like the rest of the split.
let mutable private ttyEval = false

let private setupLineEditor () =
    // the shell's cooked termios FIRST — the editor's raw config has
    // not been established yet, so this is the one moment the
    // surrounding disposition (ISIG included) is knowable [D:repl-isig]
    Term.snapshotCooked ()

    Console.TreatControlCAsInput <- true // Ctrl+C is a KEY (cancel line), not SIGINT

    // the REPL survives SIGINT (bash parity) [D:repl-isig]: with ISIG
    // restored around eval, a ^C is a GROUP signal — the child dies,
    // the session must not. Cancel suppresses default termination; the
    // exit-hook sweep skips the survived case (Session.replSurvivesSigint).
    if not (OperatingSystem.IsWindows()) then
        ttyEval <- true
        Session.replSurvivesSigint.Value <- true

        sigintSurvival <-
            Some(
                Runtime.InteropServices.PosixSignalRegistration.Create(
                    Runtime.InteropServices.PosixSignal.SIGINT,
                    fun ctx -> ctx.Cancel <- true
                )
                :> obj
            )
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
        Term.editorActive.Value <- true

        let entry =
            try
                readLineTty ()
            finally
                Term.editorActive.Value <- false

        match entry with
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
let private evalCheckedBody (state: State) (chk: Script.CheckedStatement) : State =
    lastErrored <- false

    match chk.Kind with
    | Script.KType decl ->
        let ctors =
            match decl.Body with
            | DUnion cases -> Eval.constructorValues cases
            | DRecord _ -> []

        // the REPL REPLACES on redeclaration (ruled [D:dup-type-decl] —
        // scripts error instead); the note exists because the probe
        // showed the confusing half: an old value still ECHOES with its
        // fields while field ACCESS resolves against the new shape
        if Map.containsKey decl.Name state.TypeEnv.Types then
            Console.WriteLine $"type {decl.Name} redeclared; earlier values keep the old shape"
        else
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
             lastErrored <- true
             Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
             state)
    | Script.KLet(name, _, te) ->
        printWarnings state te

        (try
            let v = Eval.eval state.Values te

            if v <> Eval.VUnit then
                // ONE enumeration for the whole echo [D:echo-once] — the
                // table probe and the line rendering share a cached
                // prefix; the STORED value keeps the original seq (the
                // lazy re-run law is the binding's, not the echo's)
                let ev = Eval.echoPrep v

                // the presentation echoes, tty-only (piped REPL output
                // is pinned surface): records tabulate [D:repl-table],
                // seq<string> shows its LINES [D:echo-lines] — keyed on
                // the TYPE, never the content — the literal otherwise
                // the cap in effect [D:echo-cap]: the session's at a
                // tty; the piped surface keeps its pinned constant
                let cap =
                    if Console.IsOutputRedirected then
                        Eval.echoPipedCap
                    else
                        echoCap

                match
                    (if Console.IsOutputRedirected then
                         None
                     // binary refuses the terminal [D:binary-echo] —
                     // zero body lines, the reason rides the footer
                     elif Eval.echoBinary cap ev then
                         Some(
                             [],
                             Some
                                 "binary output — the echo refuses a terminal; redirect to a file, or print deliberately"
                         )
                     elif te.Ty = TSeq TStr then
                         Eval.echoLines cap ev
                     else
                         Eval.echoTable cap (termWidth ()) ev)
                with
                | Some(lines, hint) ->
                    let tail = Eval.echoTail hint
                    echoMeta $"{name} : {formatTy te.Ty} ={tail}"

                    if te.Ty = TSeq TStr then
                        lines |> List.iter Console.WriteLine
                    else
                        printTable lines
                | None ->
                    let rendered, hint = Eval.echoValue cap ev
                    let tail = Eval.echoTail hint
                    Console.WriteLine $"{name} : {formatTy te.Ty} = {rendered}{tail}"

            { TypeEnv = chk.Env
              Values = Map.add name v state.Values }
         with
         | Eval.ExitRequest _ -> reraise ()
         | ex ->
             lastErrored <- true
             Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
             state)
    | Script.KExpr te
    | Script.KCmd te ->
        printWarnings state te

        // the bare command statement STREAMS its echo [D:stream-echo]:
        // chunks flush as they arrive — a partial line (an interactive
        // prompt) shows before its newline. The binary guard buffers
        // the first 4 KiB before emitting anything, so small outputs
        // keep the never-leak guarantee whole; past the threshold each
        // chunk is still checked and the stream STOPS on a NUL — a
        // BOUNDED leak, the stated residue (SECURITY's register). The
        // cap clips as before; a mid-stream raise suppresses the
        // footer and the error carries the echoed-line count.
        // Reifiers/captures are unaffected by law (| complete is
        // in-memory capture; $() never streams).
        match te.Kind with
        | Check.TECmd _ when not Console.IsOutputRedirected && te.Ty = TSeq TStr ->
            let cap = echoCap |> Option.defaultValue System.Int32.MaxValue
            let threshold = 4096
            let events = ResizeArray<Choice<string, unit>>()
            // one lock for buffer, writes, and the flush TIMER: the
            // guard's buffer is bounded in bytes AND time (an
            // interactive prompt is 20 bytes and then silence — a
            // size-only threshold would hold it forever)
            let sync = obj ()
            let mutable flushed = false
            let mutable bufferedBytes = 0
            let mutable emitted = 0
            let mutable totalBreaks = 0
            let mutable atLineStart = true

            let writeText (t: string) =
                Console.Out.Write t
                Console.Out.Flush()
                atLineStart <- false

            let writeBreak () =
                Console.Out.Write '\n'
                Console.Out.Flush()
                atLineStart <- true
                emitted <- emitted + 1

            let flush () =
                if not flushed then
                    flushed <- true

                    for e in events do
                        match e with
                        | Choice1Of2 t -> writeText t
                        | Choice2Of2() -> writeBreak ()

                    events.Clear()

            let onText (t: string) =
                lock sync (fun () ->
                    if t.Contains '\000' then
                        raise (EchoStop(if flushed then MidBinary else CleanBinary))
                    elif flushed then
                        writeText t
                    else
                        events.Add(Choice1Of2 t)
                        bufferedBytes <- bufferedBytes + t.Length

                        if bufferedBytes >= threshold then
                            flush ())

            let onBreak () =
                lock sync (fun () ->
                    totalBreaks <- totalBreaks + 1

                    if flushed then writeBreak () else events.Add(Choice2Of2())

                    if totalBreaks >= cap then
                        flush ()
                        raise (EchoStop Clipped))

            use _timer =
                new System.Threading.Timer((fun _ -> lock sync flush), null, 100, System.Threading.Timeout.Infinite)

            (try
                (try
                    Eval.streamCommandStatement state.Values te onText onBreak
                    lock sync flush

                    if not atLineStart then
                        Console.Out.Write '\n'

                    echoMeta $": {formatTy te.Ty}"
                 with
                 | EchoStop CleanBinary ->
                     echoMeta
                         ": seq<string> = binary output — the echo refuses a terminal; redirect to a file, or print deliberately"
                 | EchoStop MidBinary ->
                     if not atLineStart then
                         Console.Out.Write '\n'

                     echoMeta
                         $": seq<string> = binary mid-stream — the echo stopped after {emitted} line(s); redirect to a file"
                 | EchoStop Clipped -> echoMeta $": seq<string> ={Eval.echoTail (Some(Eval.unforcedHint cap))}")

                state
             with
             | Eval.ExitRequest _ -> reraise ()
             | ex ->
                 lastErrored <- true

                 // the mid-stream raise ruling [D:stream-echo]: footer
                 // suppressed, the error names how much the human saw
                 let seen =
                     if emitted > 0 then
                         $" (stream raised after {emitted} echoed line(s))"
                     else
                         ""

                 Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}{seen}")

                 state)
        | _ ->

            (try
                let v = Eval.eval state.Values te

                if v <> Eval.VUnit then
                    // ONE enumeration for the whole echo [D:echo-once]
                    let v = Eval.echoPrep v

                    let cap =
                        if Console.IsOutputRedirected then
                            Eval.echoPipedCap
                        else
                            echoCap

                    match
                        (if Console.IsOutputRedirected then
                             None
                         elif Eval.echoBinary cap v then
                             Some(
                                 [],
                                 Some
                                     "binary output — the echo refuses a terminal; redirect to a file, or print deliberately"
                             )
                         elif te.Ty = TSeq TStr then
                             Eval.echoLines cap v
                         else
                             Eval.echoTable cap (termWidth ()) v)
                    with
                    | Some(lines, hint) ->
                        (if te.Ty = TSeq TStr then
                             lines |> List.iter Console.WriteLine
                         else
                             printTable lines)

                        echoMeta $": {formatTy te.Ty}{Eval.echoTail hint}"
                    | None ->
                        let rendered, hint = Eval.echoValue cap v
                        let tail = Eval.echoTail hint
                        Console.WriteLine $"{rendered} : {formatTy te.Ty}{tail}"

                state
             with
             | Eval.ExitRequest _ -> reraise ()
             | ex ->
                 lastErrored <- true
                 Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
                 state)
    | Script.KModule _ ->
        Console.WriteLine "the REPL has no file to be a module of; 'module' belongs at the top of a script file"

        state
    | Script.KImport _ ->
        // unreachable: scriptOnlyImport rejects imports at check
        state


// ---- session directives [D:repl-directives] -------------------------
// '#' is the prefix for everything addressed to the TOOLING: file
// directives (#sig, #schema) read at check time, session directives
// (#help, #quit) executed now — one glyph, two lifetimes.

// simple flow-wrap for name lists
// eval runs under the SHELL's tty disposition [D:repl-isig]: ISIG on,
// so ^C reaches the foreground child as a group SIGINT (the script
// path's exact behaviour) — weir itself survives via the cancel
// registration in setupLineEditor. The finally closes the eval->prompt
// window; both no-op when there is no tty (piped REPL, Windows).
let private evalChecked (state: State) (chk: Script.CheckedStatement) : State =
    // the PROPERTY is the load-bearing half [D:repl-isig]: .NET re-applies
    // ITS OWN terminal notion when a child spawns (probed: a raw tcsetattr
    // sticks for ~10ms and the child still sees -isig), so the notion must
    // change — TreatControlCAsInput=false makes .NET's spawn-time config
    // agree with the cooked termios the restore sets now
    if ttyEval then
        Console.TreatControlCAsInput <- false
        Term.restoreCooked ()

    try
        evalCheckedBody state chk
    finally
        if ttyEval then
            Console.TreatControlCAsInput <- true
            Term.reassert ()

let private flowNames (indent: string) (width: int) (words: string list) : string =
    let sb = Text.StringBuilder()
    let mutable col = indent.Length

    for w in words do
        if col > indent.Length && col + 1 + w.Length > width then
            sb.Append('\n').Append(indent) |> ignore
            col <- indent.Length
        elif col > indent.Length then
            sb.Append ' ' |> ignore
            col <- col + 1

        sb.Append w |> ignore
        col <- col + w.Length

    sb.ToString()

/// ONE SOURCE [D:repl-directives]: the hover's own composition — the
/// annotated signature (formatSignature over the builtinDocs params)
/// plus renderBuiltinDoc. A hover improvement lifts #help for free.
let private memberHelp (te: TypeEnv) (name: string) : string option =
    let schemeOf (n: string) =
        match n.Split '.' with
        | [| m; mem |] -> te.Modules |> Map.tryFind m |> Option.bind (Map.tryFind mem)
        | _ -> Map.tryFind n te.Values

    match Map.tryFind name Builtins.builtinDocs, schemeOf name with
    | Some d, sch ->
        let sigLine =
            sch
            |> Option.map (fun s -> formatSignature name d.Params s.Ty + "\n\n")
            |> Option.defaultValue ""

        Some(sigLine + Builtins.renderBuiltinDoc d)
    | None, Some sch -> Some(formatSignature name [] sch.Ty)
    | None, None -> None

let private helpDirective (te: TypeEnv) (arg: string) : string =
    match arg.Trim() with
    | "" ->
        let mods = te.Modules |> Map.keys |> Seq.sort |> List.ofSeq

        "Directives:\n"
        + "  #help                 // this list\n"
        + "  #help <name>          // documentation for a module or member\n"
        + "  #echo [<n> | all]     // the unforced-echo cap (default 100); bare reports;\n"
        + "                        //   all = no cap — an INFINITE seq will hang (Ctrl+C)\n"
        + "  #quit                 // leave the REPL (Ctrl+D works too)\n\n"
        + "Modules: "
        + flowNames "         " 72 mods
    | name when Map.containsKey name te.Modules ->
        // members from COMPLETION'S source — the module map plus the
        // bespoke checker arms — never a copy
        let members =
            Seq.append
                (te.Modules[name] |> Map.keys)
                (Check.specialModuleMembers |> Map.tryFind name |> Option.defaultValue [])
            |> Seq.distinct
            |> Seq.sort
            |> List.ofSeq

        $"{name} ({List.length members} members):\n  "
        + flowNames "  " 72 members
        + $"\n\n#help {name}.<member> shows one member's doc"
    | name ->
        match memberHelp te name with
        | Some h -> h
        | None ->
            match Map.tryFind name te.Types with
            | Some(Record d) ->
                let fields =
                    d.Fields |> List.map (fun (f, t) -> $"{f}: {formatTy t}") |> String.concat "; "

                $"type {name} = {{ {fields} }}"
            | Some(Union u) ->
                let cases =
                    u.Cases
                    |> List.map (fun (c, p) ->
                        match p with
                        | Some t -> $"{c} of {formatTy t}"
                        | None -> c)
                    |> String.concat " | "

                $"type {name} = {cases}"
            | None ->
                // a dotted typo did-you-means within its MODULE's members
                match name.Split '.' with
                | [| m; mem |] when Map.containsKey m te.Modules ->
                    $"#help: {m} has no member '{mem}'{didYouMean mem (te.Modules[m] |> Map.keys)}"
                | _ ->
                    let pool =
                        Seq.concat
                            [ te.Modules |> Map.keys |> Seq.cast<string>
                              te.Values |> Map.keys |> Seq.filter Types.isUserName
                              te.Types |> Map.keys |> Seq.cast<string> ]

                    $"#help: unknown name '{name}'{didYouMean name pool}"

let rec private loop (state: State) =
    currentEnv.Value <- state.TypeEnv

    match readInput () with
    | null -> ()
    | line when
        line.Split '\n'
        |> Array.forall (fun l -> Script.classifyLine l <> Script.LineKind.Code)
        ->
        // blank AND comment-only entries are a NO-OP at the prompt
        // [D:repl-directives]: nothing follows for a comment to be
        // transparent to — no message is the right answer
        loop state
    | line when line.TrimStart().StartsWith "#" ->
        let t = line.Trim()

        lastErrored <- false

        if t = "#quit" then
            ()
        elif t = "#help" || t.StartsWith "#help " then
            Console.WriteLine(helpDirective state.TypeEnv (t.Substring 5))
            loop state
        elif t = "#echo" || t.StartsWith "#echo " then
            // the echo cap [D:echo-cap]: bare reports (FSI's #time
            // convention), a count sets, `all` uncaps — the footgun is
            // the user's own (the forced side made the same call)
            (match t.Substring(5).Trim() with
             | "" ->
                 Console.WriteLine(
                     match echoCap with
                     | Some n -> $"echo cap: {n}"
                     | None -> "echo cap: all"
                 )
             | "all" ->
                 echoCap <- None
                 Console.WriteLine "echo cap: all"
             | arg ->
                 match Int32.TryParse arg with
                 | true, n when n > 0 ->
                     echoCap <- Some n
                     Console.WriteLine $"echo cap: {n}"
                 | _ -> Console.WriteLine $"#echo takes a positive count or 'all' — e.g. #echo 100")

            loop state
        else
            let word = t.Split(' ').[0]

            Console.WriteLine
                $"unknown directive '{word}' — #help lists them (#sig and #schema are file directives, read at check time)"

            loop state
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
                            lastErrored <- true
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

                            // LABEL BOTH KINDS [D:not-weir-shape]: a bare parse message was the one
                            // place in weir that shows an error without saying it is one (check says
                            // `error [parse]`, the runner says `parse error:`). It hid because
                            // FParsec's backtrack note carried the word "error", so the pin meaning
                            // "the error shows" passed on parser noise; removing that noise surfaced it.
                            Console.WriteLine(
                                if d.Parse then
                                    $"parse error: {d.Message}"
                                else
                                    $"type error: {d.Message}"
                            )

                            st
                        | Ok chk -> evalChecked st chk)
                    state

        loop next
    | line ->
        Extern.refresh ()

        let next =
            // [D:one-pipeline]: a single-line LogicalLine feeds
            // checkStatement; the REPL only renders. Comment-STRIPPED
            // first, like scripts (the assembler) and -e do — the
            // multiline arm rides the assembler, this arm never did
            // [D:repl-directives]; a district cannot occur here (marker
            // lines open the multiline buffer)
            let ll = Script.singleLine (Script.stripComment line)

            match Script.checkStatement false (fun _ -> resolver state) Script.scriptOnlyImport state.TypeEnv ll with
            | Error d when d.Parse ->
                lastErrored <- true
                // the input sits on the prompt line above — caret under it
                Console.WriteLine(
                    Types.Color.red Types.Color.onStdout.Value (String(' ', prompt.Length + d.PhysCol - 1) + "^")
                )

                // labelled, like every other parse diagnostic weir prints
                Console.WriteLine $"parse error: {d.Message}"
                printHint state line
                state
            | Error d ->
                lastErrored <- true

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
