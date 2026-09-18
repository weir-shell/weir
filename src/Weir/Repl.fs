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

// the streamed-statement latch [D:repl-it]: Some <source text> while the
// LAST evaluated statement took the inherit path [D:colour-inherit] — the
// child wrote the terminal itself, weir never held the bytes, so `it` did
// NOT bind. Any other evaluated statement clears it; a check error or a
// directive leaves it (the very next line may be the `it` that needs the
// teach). Read by the unbound-`it` arm and the streamed meta line.
let mutable private lastStreamed: string option = None

// the session resolver's verdict [D:repl-color]: ONE membership feeding
// the live prompt's head tint AND the #help example tint [D:help-tint]
// — values, modules, and the command-callable externs, never two lists
let private knownIn (env: TypeEnv) (n: string) : bool =
    Map.containsKey n env.Values
    || Map.containsKey n env.Modules
    || Builtins.commandCallable.Contains n

// the session TRANSCRIPT [D:repl-save]: the ACCEPTED statements, in
// order, that `#save` DISTILLS into a runnable .weir file (option B — a
// session is scratch; #save crystallizes its definitions and guarantees
// the output `weir check`s clean). A statement lands here only after it
// CHECKS and EVALUATES clean (errored lines drop).
//
// `PhysText` is the statement's REAL physical source — a multi-line
// heredoc/`type`/binding carries its newlines + indentation, NOT the
// assembler's control-char-joined logical line (`ll.Text`), so a kept
// definition round-trips through `weir check`. `Kind` classifies the
// statement so #save can KEEP definitions and DROP scratch:
//   - `TDef name` — a `type` decl or a NAMED `let` binding (a real
//     binder, not `_`-prefixed / a `_r<n>` discard). #save keeps these,
//     deduping by name (last wins), and checks each survivor.
//   - `TDiscard` — a non-unit expression/command echo (bare scratch).
//     Dropped; it never carried reuse value.
//   - `TOther` — a unit statement (an effect echo). Dropped from the
//     distilled file (a session's effects are not definitions).
// Directives never record; #infer records the DRAFTED type decls (each a
// `TDef`) instead of the directive line.
type private TranscriptKind =
    | TDef of name: string
    | TDiscard
    | TOther

type private TranscriptLine =
    { Text: string
      PhysText: string
      Kind: TranscriptKind }

let private transcript = System.Collections.Generic.List<TranscriptLine>()

// record a KEPT definition (type or named let) with its physical source
let private recordDef (name: string) (physText: string) (text: string) =
    transcript.Add
        { Text = text
          PhysText = physText
          Kind = TDef name }

// record a bare non-unit echo — scratch, dropped by #save
let private recordDiscard (physText: string) (text: string) =
    transcript.Add
        { Text = text
          PhysText = physText
          Kind = TDiscard }

// record a unit statement — an effect echo, dropped by #save
let private recordOther (physText: string) (text: string) =
    transcript.Add
        { Text = text
          PhysText = physText
          Kind = TOther }

// the PHYSICAL source of a logical line [D:repl-save]: a multi-line
// statement (a heredoc, a `type` decl, a block let) assembled `ll.Text`
// with the group-separator sentinel — unparseable in a file. Every
// segment carries its physical line number, so the real source is those
// lines, in order, from the dedented `srcLines` the submission built.
// One physical line (the REPL's common case) is just its own text.
let private physicalSource (srcLines: string[]) (ll: Script.LogicalLine) : string =
    let lineNos =
        [ for (_, lineNo, _) in ll.Segments -> lineNo ]
        |> List.append [ ll.Head ]
        |> List.distinct
        |> List.sort
        |> List.filter (fun n -> n >= 1 && n <= srcLines.Length)

    match lineNos with
    | [] -> ll.Text
    | _ -> lineNos |> List.map (fun n -> srcLines[n - 1]) |> String.concat "\n"

// how an accepted statement RECORDS [D:repl-save]: a `type` decl or a
// NAMED `let` binds a definition (`Some (TDef name)`); a `_`-prefixed or
// `_r<n>` discard binder is scratch (`Some TDiscard` — it carried no
// reuse value); a non-unit expression/command echo is scratch too
// (`Some TDiscard`); a unit statement is an effect echo (`Some TOther`);
// a module/import/sig never records (`None`).
//
// a named binder is a real name — not `_` and not `_`-prefixed (the
// deliberate-discard escape and the REPL's own `_r<n>` scratch both lead
// with `_`, so both drop from the distilled file)
let private isNamedBinder (name: string) =
    name <> "" && not (name.StartsWith "_")

let private recordKind (chk: Script.CheckedStatement) : TranscriptKind option =
    match chk.Kind with
    | Script.KLet(name, _, _) -> Some(if isNamedBinder name then TDef name else TDiscard)
    | Script.KLetPat(_, schemes, _) ->
        // a destructuring binds several names (the schemes list); keep it
        // as a definition when ANY binder is real (the line reads its RHS
        // once). dedup keys on the FIRST real name — a destructuring is
        // rarely reshadowed whole, and the check-guarantee catches any
        // survivor that still fails
        let names = schemes |> List.map fst

        match names |> List.filter isNamedBinder with
        | first :: _ -> Some(TDef first)
        | [] -> Some TDiscard
    | Script.KType decl -> Some(TDef decl.Name)
    | Script.KCmd te
    | Script.KExpr te -> Some(if te.Ty = TUnit then TOther else TDiscard)
    | Script.KModule _
    | Script.KImport _
    | Script.KSig _ -> None

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

// a command-head alias [D:command-head-alias]: (real exe, fixed prefix
// args). Single-hop by construction — the exe is a real program name,
// never another alias (define-time resolution rejects alias-of-alias).
type private Alias = { Exe: string; Prefix: string list }

/// parse a `#alias name = cmd [args...]` directive line [D:command-head-alias].
/// The head token maps to (exe, prefix args); argv is split on whitespace
/// (a fixed prefix is literal words — no quoting/typed-argv machinery: an
/// alias is a resolution-table entry, not a script line). Returns the name
/// and its target, or a message. Single-hop is enforced later against the
/// whole table.
let private parseAliasLine (body: string) : Result<string * Alias, string> =
    // body is the text AFTER `#alias`
    match
        body.Trim()
        |> fun s -> System.Text.RegularExpressions.Regex.Match(s, @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$")
    with
    | m when m.Success ->
        let name = m.Groups.[1].Value

        let rhsWords =
            m.Groups.[2].Value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        match rhsWords with
        | [] -> Error "#alias needs a target: '#alias name = command [args...]'"
        | exe :: prefix ->
            // `#alias ls = ls` (or `= ls --color`) is a legal shadow (bash
            // allows it), bypassable with `^ls` — register as-is
            Ok(name, { Exe = exe; Prefix = prefix })
    | _ -> Error "malformed #alias — usage: '#alias name = command [args...]' (e.g. '#alias k = kubectl')"

/// a test seam over parseAliasLine [D:command-head-alias]: the private
/// Alias record is flattened to a public (exe, prefix) pair so unit tests
/// can assert on the parse without the module-private type
let parseAliasLineForTest (body: string) : Result<string * (string * string list), string> =
    parseAliasLine body |> Result.map (fun (n, a) -> (n, (a.Exe, a.Prefix)))

type private State =
    { TypeEnv: TypeEnv
      Values: Eval.Env
      // REPL-only, populated from init.weir's #alias lines (and a live
      // `#alias` at the prompt). Consulted ONLY in command-head position,
      // before PATH; never threaded to scripts/-e.
      Aliases: Map<string, Alias> }

let private initial =
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnv Builtins.valueEnv

    { TypeEnv = typeEnv
      Values = valueEnv
      Aliases = Map.empty }

let private currentEnv = ref initial.TypeEnv

/// the reference dump [D:reference]: the SAME one source #help and hover
/// read — builtinDocs plus the typed signatures — emitted as JSON for
/// the site's generated reference pages (weir docs-json). Modules carry
/// their members with signatures; the form heads (retry/poll/within…)
/// are builtinDocs keys with no module and no scheme.
let docsJson () : string =
    let te = initial.TypeEnv
    use ms = new MemoryStream()

    use w =
        // LF on every platform [D:lf-output]: the dump is a generated,
        // committed, diffed artifact — Indented's default newline is
        // Environment.NewLine, which made the Windows dump byte-different
        new System.Text.Json.Utf8JsonWriter(ms, System.Text.Json.JsonWriterOptions(Indented = true, NewLine = "\n"))

    let writeDocFields (d: Builtins.BuiltinDoc option) =
        match d with
        | Some d ->
            w.WriteString("summary", d.Summary)

            match d.Example with
            | Some e -> w.WriteString("example", e)
            | None -> w.WriteNull "example"

            match d.Pointer with
            | Some p -> w.WriteString("pointer", p)
            | None -> w.WriteNull "pointer"
        | None ->
            w.WriteNull "summary"
            w.WriteNull "example"
            w.WriteNull "pointer"

    w.WriteStartObject()
    w.WriteStartArray "modules"

    // Self is script-only [D:self-module] — absent from the REPL env this
    // dump reads, but the reference documents the language, so it joins
    // the module walk from its one source (Script.selfMembers)
    let modules = te.Modules |> Map.add "Self" Script.selfMembers

    for KeyValue(m, members) in modules do
        w.WriteStartObject()
        w.WriteString("name", m)
        w.WriteStartArray "members"

        for KeyValue(mem, sch) in members do
            let q = $"{m}.{mem}"
            let d = Map.tryFind q Builtins.builtinDocs
            w.WriteStartObject()
            w.WriteString("name", mem)

            let ps = d |> Option.map (fun d -> d.Params) |> Option.defaultValue []

            w.WriteString("signature", formatSignature q ps sch.Ty)
            writeDocFields d
            w.WriteEndObject()

        w.WriteEndArray()
        w.WriteEndObject()

    w.WriteEndArray()
    w.WriteStartArray "forms"

    for KeyValue(k, d) in Builtins.builtinDocs do
        if not (k.Contains '.') then
            w.WriteStartObject()
            w.WriteString("name", k)

            match Map.tryFind k te.Values with
            | Some sch -> w.WriteString("signature", formatSignature k d.Params sch.Ty)
            | None -> w.WriteNull "signature"

            writeDocFields (Some d)

            // the within entry carries its kind table structurally — the
            // site renders rows where the summary's prose enumeration
            // cannot (the one source: Ast.withinKinds)
            if k = "within" then
                w.WriteStartArray "kinds"

                for kind in Ast.withinKinds |> List.filter (fun x -> not x.Standalone) do
                    w.WriteStartObject()
                    w.WriteString("name", kind.Name)
                    w.WriteString("doc", kind.Doc)
                    w.WriteEndObject()

                w.WriteEndArray()

            w.WriteEndObject()

    w.WriteEndArray()
    w.WriteEndObject()
    w.Flush()
    Text.Encoding.UTF8.GetString(ms.ToArray())

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

// the `let`-echo meta line [D:echo-teaching-consistency]: name, type, and
// the SAME truncation-teaching tail the bare-expression echo carries — one
// spelling so a clipped `let` bind can never look like it silently dropped
// data (the tail is printed AFTER the lines, so it stays visible)
let letEchoMeta (name: string) (ty: Ty) (hint: string option) : string =
    $"{name} : {formatTy ty}{Eval.echoTail hint}"

// the inherited-statement meta line [D:repl-it]: the statement STREAMED —
// the child wrote the terminal, weir never held the bytes — so unlike
// every other value echo, `it` did not bind; the meta says so instead of
// looking like a value landed
let streamedEchoMeta (ty: Ty) : string =
    $": {formatTy ty} (streamed — not bound to 'it'; let x = … captures)"

// the unbound-`it` teach after a streamed statement [D:repl-it]: the
// generic unbound error reads as a regression there; the repair is
// re-typing the command as a `let` (the value path captures, and a let
// binds `it` too). The command text rides verbatim when it is one clean
// physical line; sentinel-joined (assembled) source falls back to the
// generic spelling.
let streamedItTeach (source: string option) : string list =
    let cmd =
        match source with
        | Some s when s.Trim() <> "" && not (s |> Seq.exists Char.IsControl) -> s.Trim()
        | _ -> "<the command>"

    [ "unbound variable 'it' — the last command streamed to the terminal; weir never held its output"
      $"to capture (and bind 'it'): let x = {cmd}" ]

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
        let isKnown = knownIn env

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

// one-line pushback for the redirected accumulator [D:repl-multiline]: a
// peeked line that did NOT attach to the current statement belongs to the
// NEXT one — it must survive across readInput calls (a fresh statement is
// a fresh readRedirected call), so it lives at module scope, not inside
// the function
let mutable private pendingLine: string option = None

// the redirected-stdin ACCUMULATOR [D:repl-multiline]: a piped REPL
// (printf '…' | weir) has no tty line editor, so it reads physical lines
// with Console.ReadLine. A statement that spans lines (heredoc, a
// multi-line `type`, an open `if`/`for`/`match` block, a leading-`|>`
// pipeline, an unbalanced bracket) must ASSEMBLE the way a SCRIPT does —
// Script.assemble is the ONE authority (never a second parser). The tty
// editor cannot look ahead (the user types), so it submits on the first
// complete buffer; a pipe HAS the rest of the input, so it reads
// script-like: keep the current statement open while the NEXT physical
// line still attaches to it (a `|>` tail, an offside `else`, a district
// body). Return one logical statement (lines \n-joined) so the loop's
// existing single/multiline arms handle it unchanged.
//
// A directive line (#help/#quit/#infer/…) is one line by definition —
// returned immediately, its own multi-line handling (#infer) untouched. A
// blank/comment-only first line has no statement to open, so it returns
// alone (the no-op arm). AT EOF with an unclosed buffer: return what
// accumulated and let it error normally — the tty editor's
// blank-line/Ctrl+D escape does the same at its equivalent boundary.
let private readRedirected () : string =
    let readLine () =
        match pendingLine with
        | Some l ->
            pendingLine <- None
            l
        | None -> Console.ReadLine()

    // the completed statement's prompts, emitted ONCE at return so the
    // eval output that follows keeps its order [D:repl-multiline] — a peek
    // reads the next statement's line BEFORE this one evaluates, so the
    // prompt cannot be written at read time. weir> for the first line,
    // "  ... " (same width) for each continuation, mirroring the tty.
    let emitPrompts (buf: string list) =
        buf
        |> List.iteri (fun i _ -> Console.Write(if i = 0 then prompt else contPrompt))

    let ret (buf: string list) =
        emitPrompts buf
        String.Join("\n", buf)

    let rec go (acc: string list) =
        match readLine () with
        | null ->
            match acc with
            | [] ->
                // EOF, empty buffer: still show the prompt the loop would
                // have shown, then end the session (the loop's null arm)
                Console.Write prompt
                null
            | _ -> ret (List.rev acc)
        | line ->
            let acc' = line :: acc
            let buf = List.rev acc'

            // a FRESH first line that is a directive, blank, or
            // comment-only is complete on its own — it opens no statement
            // for later lines to continue
            let firstLineComplete =
                List.isEmpty acc
                && (line.TrimStart().StartsWith "#"
                    || Script.classifyLine line <> Script.LineKind.Code)

            if firstLineComplete then
                ret buf
            elif not (bufferComplete buf) then
                go acc' // structurally incomplete — read the body
            else
                // complete AS-IS; peek whether the next line continues it
                match readLine () with
                | null -> ret buf
                | next when Script.pipedAttaches buf next -> go (next :: acc')
                | next ->
                    pendingLine <- Some next
                    ret buf

    go []

let private readInput () =
    if Console.IsInputRedirected then
        readRedirected ()
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

let private resolver (state: State) : Parser.Resolver =
    { Script.resolver state.TypeEnv with
        // the session alias table beats PATH in command-head position
        // [D:command-head-alias]; `^` skips it (parser-side). Single-hop:
        // the stored Exe is a real program, never another alias name.
        AliasHead =
            fun n ->
                Map.tryFind n state.Aliases
                |> Option.map (fun a -> (a.Exe, a.Prefix)) }

let private printHint (state: State) (line: string) =
    Diagnose.hint
        (fun n ->
            Map.containsKey n state.TypeEnv.Values
            || Map.containsKey n state.TypeEnv.Modules)
        Builtins.commandCallable.Contains
        Extern.exists
        line
    |> Option.iter (fun h -> Console.WriteLine $"hint: {h}")

// the LAST-RESULT binding [D:repl-it]: every REPL line that produces a
// value (an expression, a command, or a `let` RHS — the echo path) also
// binds it to the session name `it`, ghci's convention. `_` is taken
// (the `_.field` lambda shorthand and the `let _ =` discard), so `it` is
// the spelling; it collides with nothing and is REPL-only (scripts/-e
// never see it, like the bare aliases). A unit statement or a directive
// leaves `it` untouched. Both the TypeEnv scheme AND the value bind, so
// the next line both checks and evaluates against `it`.
let private bindIt (ty: Ty) (v: Eval.Value) (env: State) : State =
    if ty = TUnit then
        env
    else
        { env with
            TypeEnv =
                { env.TypeEnv with
                    Values = Map.add "it" (Types.generalize ty) env.TypeEnv.Values }
            Values = Map.add "it" v env.Values }

// the Ok-side rendering, shared by the single-line and multiline
// submission paths [D:repl-multiline]
let private evalCheckedBody (source: string) (state: State) (chk: Script.CheckedStatement) : State =
    lastErrored <- false
    lastStreamed <- None

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

        { state with
            TypeEnv = chk.Env
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

            { state with
                TypeEnv = chk.Env
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
                    // the lines/table FIRST, the meta+teaching LAST
                    // [D:echo-teaching-consistency]: a truncation footer
                    // printed before 100 lines scrolls off the top, so a
                    // clipped `let` bind looked like it silently dropped
                    // data — the bare-expression echo already prints the
                    // footer last (visible), and the two arms must agree
                    (if te.Ty = TSeq TStr then
                         lines |> List.iter Console.WriteLine
                     else
                         printTable lines)

                    echoMeta (letEchoMeta name te.Ty hint)
                | None ->
                    let rendered, hint = Eval.echoValue cap ev
                    let tail = Eval.echoTail hint
                    Console.WriteLine $"{name} : {formatTy te.Ty} = {rendered}{tail}"

            { state with
                TypeEnv = chk.Env
                Values = Map.add name v state.Values }
            |> bindIt te.Ty v
         with
         | Eval.ExitRequest _ -> reraise ()
         | ex ->
             lastErrored <- true
             Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
             state)
    | Script.KExpr te
    | Script.KCmd te ->
        printWarnings state te

        // the bare command statement at a tty INHERITS stdout
        // [D:colour-inherit]: the child writes the terminal itself —
        // isatty true, colour on — and weir never holds the bytes, so
        // the relay's guard/threshold/cap do not apply (bash's posture:
        // the child chose its bytes for a terminal it can see; gzip
        // refuses a tty by ITSELF, which is the incident's cause
        // removed). The echo cap governs VALUE echoes, which keep the
        // guard. Reifiers/captures are unaffected by law (| complete is
        // in-memory capture; $() never streams); a redirected REPL
        // keeps the batched value path.
        match te.Kind with
        | _ when Eval.inheritsStdout te ->
            (try
                Console.Out.Flush()
                Eval.inheritCommandStatement state.Values te
                echoMeta (streamedEchoMeta te.Ty)
                lastStreamed <- Some source
                state
             with
             | Eval.ExitRequest _ -> reraise ()
             | ex ->
                 lastErrored <- true
                 Console.WriteLine(Types.Color.red Types.Color.onStdout.Value "error" + $": {ex.Message}")
                 state)
        | _ ->

            (try
                let v = Eval.eval state.Values te

                // ONE enumeration for the whole echo [D:echo-once]; the
                // prepped (cached) value is also what binds to `it`, so a
                // command-backed seq reused as `it` does not re-run
                let ev = if v <> Eval.VUnit then Eval.echoPrep v else v

                if v <> Eval.VUnit then
                    let cap =
                        if Console.IsOutputRedirected then
                            Eval.echoPipedCap
                        else
                            echoCap

                    match
                        (if Console.IsOutputRedirected then
                             None
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
                        (if te.Ty = TSeq TStr then
                             lines |> List.iter Console.WriteLine
                         else
                             printTable lines)

                        echoMeta $": {formatTy te.Ty}{Eval.echoTail hint}"
                    | None ->
                        let rendered, hint = Eval.echoValue cap ev
                        let tail = Eval.echoTail hint
                        Console.WriteLine $"{rendered} : {formatTy te.Ty}{tail}"

                bindIt te.Ty ev state
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
    | Script.KSig _ ->
        // unreachable: the REPL passes no sig context, so the form
        // already refused with the scripts-infer teaching
        // [D:module-signatures]
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
let private evalChecked (source: string) (state: State) (chk: Script.CheckedStatement) : State =
    // the PROPERTY is the load-bearing half [D:repl-isig]: .NET re-applies
    // ITS OWN terminal notion when a child spawns (probed: a raw tcsetattr
    // sticks for ~10ms and the child still sees -isig), so the notion must
    // change — TreatControlCAsInput=false makes .NET's spawn-time config
    // agree with the cooked termios the restore sets now
    if ttyEval then
        Console.TreatControlCAsInput <- false
        Term.restoreCooked ()

    try
        evalCheckedBody source state chk
    finally
        if ttyEval then
            Console.TreatControlCAsInput <- true
            Term.reassert ()

// ---- the #help glance [D:help-glance]: one name per line, each with
// the FIRST LINE of its one-source doc (builtinDocs / moduleBlurbs),
// clipped to the terminal — glanceable by construction. Piped output
// uses a FIXED width so the byte surface stays deterministic.
let private glanceWidth () =
    if Console.IsOutputRedirected then
        100
    else
        try
            max 40 Console.WindowWidth
        with _ ->
            100

let private clipTo (width: int) (s: string) : string =
    if s.Length <= width then s else s.Substring(0, width - 1) + "…"

/// a member's glance: the first line of its builtinDocs Summary — the
/// same text hover and `#help Module.member` lead with, never a copy
let private memberGlance (qualified: string) : string =
    match Map.tryFind qualified Builtins.builtinDocs with
    | Some d -> (d.Summary.Split '\n')[0]
    | None -> ""

/// a module's member names — completion's source (the module map plus
/// the bespoke checker arms), the same derivation `#help Module` shows
let private moduleMembersOf (te: TypeEnv) (name: string) : string list =
    Seq.append
        (te.Modules
         |> Map.tryFind name
         |> Option.map (fun m -> Map.keys m :> seq<string>)
         |> Option.defaultValue Seq.empty)
        (Check.specialModuleMembers |> Map.tryFind name |> Option.defaultValue [])
    |> Seq.distinct
    |> Seq.sort
    |> List.ofSeq

/// pad names into a two-column glance table, one entry per line
let private glanceTable (width: int) (rows: (string * string) list) : string =
    let pad = 2 + (rows |> List.fold (fun w (n, _) -> max w n.Length) 0)

    rows
    |> List.map (fun (n, g) ->
        if g = "" then
            "  " + n
        else
            clipTo width ("  " + n.PadRight pad + g))
    |> String.concat "\n"

/// ONE SOURCE [D:repl-directives]: the hover's own composition — the
/// annotated signature (formatSignature over the builtinDocs params)
/// plus renderBuiltinDoc. A hover improvement lifts #help for free.
/// `color` [D:help-tint]: at a tty the signature tints STRUCTURALLY at
/// composition (sigTintStyle — the input colorizer's palette) and the
/// Example renders through Script.colorizeRepl itself — an example is
/// weir code, so the live prompt and #help share one brain; false is
/// the pinned piped/--repl-doc byte surface, untouched.
let private memberHelp (color: bool) (te: TypeEnv) (name: string) : string option =
    let style = if color then sigTintStyle else plainSigStyle
    let fmtSig = formatSignatureWith style

    let tintExample: string -> string =
        if color then
            fun ex ->
                ex.Split '\n'
                |> Array.map (Script.colorizeRepl (knownIn te))
                |> String.concat "\n"
        else
            id

    let schemeOf (n: string) =
        match n.Split '.' with
        | [| m; mem |] -> te.Modules |> Map.tryFind m |> Option.bind (Map.tryFind mem)
        | _ -> Map.tryFind n te.Values

    match Map.tryFind name Builtins.builtinDocs, schemeOf name with
    | Some d, sch ->
        let sigLine =
            sch
            |> Option.map (fun s -> fmtSig name d.Params s.Ty + "\n\n")
            |> Option.defaultValue ""

        Some(sigLine + Builtins.renderBuiltinDocWith tintExample d)
    | None, Some sch -> Some(fmtSig name [] sch.Ty)
    | None, None -> None

// the init file's /// docs [D:repl-init]: position-keyed attachments
// mapped to binding names at load, so #help on a session name answers
// with its doc — and a failed init leaves this EMPTY, never stale
let mutable private initDocs: Map<string, string list> = Map.empty

let private helpDirective (color: bool) (te: TypeEnv) (arg: string) : string =
    match arg.Trim() with
    | "" ->
        // modules one per line with their one-source blurb [D:help-glance];
        // a module missing from moduleBlurbs renders bare (the completeness
        // unit pin fails loud, the render never does)
        let mods =
            te.Modules
            |> Map.keys
            |> Seq.sort
            |> Seq.map (fun m -> m, (Builtins.moduleBlurbs |> Map.tryFind m |> Option.defaultValue ""))
            |> List.ofSeq

        "Directives:\n"
        + "  #help                 // this list\n"
        + "  #help <name>          // documentation for a module or member\n"
        + "  #find [query]         // fuzzy-search modules and members (fzf + live preview)\n"
        + "  #echo [<n> | all]     // the unforced-echo cap (default 100); bare reports;\n"
        + "                        //   all = no cap — an infinite seq will hang (Ctrl+C)\n"
        + "  #infer [<src>] from <json|jsonl|yaml> as <Name>\n"
        + "                        //   draft named types from a sample (src defaults to 'it')\n"
        + "  #save <path>          // dump the session's accepted lines to a runnable .weir\n"
        + "  #alias [name = cmd …] // bare lists; a command-head alias (init.weir is canonical)\n"
        + "  #quit                 // leave the REPL (Ctrl+D works too)\n\n"
        + "Modules:\n"
        + glanceTable (glanceWidth ()) mods
    | name when Map.containsKey name te.Modules ->
        // members from COMPLETION'S source — the module map plus the
        // bespoke checker arms — never a copy; each with its doc's first
        // line from builtinDocs, the one member-text source [D:help-glance]
        let members = moduleMembersOf te name

        let rows =
            members |> List.map (fun m -> m, memberGlance $"{name}.{m}")

        $"{name} ({List.length members} members):\n"
        + glanceTable (glanceWidth ()) rows
        + $"\n\n#help {name}.<member> shows one member's doc"
    | name when (Check.ctorOwners te name |> List.length) > 1 ->
        // the checker refuses this name [D:ambiguous-ctor]; answering with one
        // candidate would be a confident wrong suggestion
        let owners = Check.ctorOwners te name |> String.concat ", "
        $"'{name}' is an ambiguous constructor; it is declared by: {owners} — rename one of the cases"
    | name ->
        match memberHelp color te name with
        | Some h ->
            (match Map.tryFind name initDocs with
             | Some doc -> h + "\n" + (doc |> String.concat "\n")
             | None -> h)
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
                    // the documentable universe, ONE source shared with the
                    // #help-arg completion slot [D:help-arg-complete] — so
                    // what #help documents and what #help <TAB> offers, and
                    // the did-you-mean over misses, cannot drift
                    let pool = Complete.helpNames te

                    $"#help: unknown name '{name}'{didYouMean name pool}"

/// the headless doc render [D:help-find]: `weir --repl-doc <name>`
/// prints the EXACT `#help <name>` text for weir's own builtin surface
/// to stdout — read-only, no user code, no eval. #find's fzf --preview
/// is the caller; the e2e pin holds the byte equality.
let replDocText (name: string) : string =
    helpDirective false initial.TypeEnv name

// ---- the tty help render [D:help-tint] -------------------------------
// A doc's `code` spans tint cyan at a colored tty, the backticks
// themselves dropped — the span reads as code, not markdown source.
// ONE function, called by every REPL help print (the #help dispatch,
// #find's selection and fallback); replDocText and piped output keep
// the literal backticks (the pinned byte surface — and the stripped
// tty, NO_COLOR / TERM=dumb, falls back to that spelling so the span
// boundary is never lost). The signature line and the Example block
// tint upstream, at composition (memberHelp's colour flag) — this
// regex never has to re-parse what helpDirective already knew.
let private codeSpanRe = Text.RegularExpressions.Regex @"`([^`\n]+)`"

/// the transform with the colour gate explicit — the unit pins' seam
let renderHelpText (color: bool) (s: string) : string =
    if color then
        codeSpanRe.Replace(s, (fun m -> Types.Color.cyan true m.Groups[1].Value))
    else
        s

let private renderHelp (s: string) : string =
    renderHelpText Types.Color.onStdout.Value s

/// the one tty #help pipeline — composition-time tint (signature,
/// example) and the prose span pass share a single colour gate
let private renderHelpDirective (te: TypeEnv) (arg: string) : string =
    renderHelp (helpDirective Types.Color.onStdout.Value te arg)

// ---- #find [D:help-find]: fuzzy help search over ONE candidate set —
// every module (`Seq — blurb`) and every member (`Seq.map — glance`),
// the same sources #help renders, never a second copy. fzf at a tty
// (with a live --preview via the headless doc render); a minimal
// substring fallback otherwise [D:repl-quality] — never an "install
// fzf" message. Piped sessions always take the fallback (deterministic).

let private findCandidates (te: TypeEnv) : string list =
    [ for m in te.Modules |> Map.keys |> Seq.sort do
          let blurb = Builtins.moduleBlurbs |> Map.tryFind m |> Option.defaultValue ""
          yield (if blurb = "" then m else $"{m} — {blurb}")

          for mem in moduleMembersOf te m do
              let g = memberGlance $"{m}.{mem}"
              yield (if g = "" then $"{m}.{mem}" else $"{m}.{mem} — {g}") ]

/// the fallback (and the piped behavior): case-insensitive substring
/// filter over the candidate lines — name and glance text both match
let private findFallback (te: TypeEnv) (query: string) : string =
    if query = "" then
        "#find <query> — fuzzy-search modules and members (interactive with fzf at a tty; a substring filter otherwise)"
    else
        let w = glanceWidth ()

        let hits =
            findCandidates te
            |> List.filter (fun l -> l.Contains(query, StringComparison.OrdinalIgnoreCase))

        if List.isEmpty hits then
            $"#find: no matches for '{query}'"
        else
            hits |> List.map (clipTo w) |> String.concat "\n"

/// the fzf path: feed the candidates, `{1}` (the name field) previews
/// via the running binary's own --repl-doc, selection prints the exact
/// `#help <name>` answer. Cancel (130/Esc) prints nothing.
let private findFzf (te: TypeEnv) (query: string) : string option =
    try
        let psi = Diagnostics.ProcessStartInfo "fzf"

        // candidate names carry weir glyphs' MODULE dots only, but the
        // same ruling as Ctrl+R holds: literal fuzzy matching is the
        // correct default, and last-flag-wins lets finderFlags restore
        // `--extended` [D:repl-quality]
        psi.ArgumentList.Add "--no-extended"

        // the live preview: the running binary renders its own docs
        // headlessly — never assume `weir` is on PATH
        let exe =
            match Environment.ProcessPath with
            | null -> "weir"
            | p -> p

        psi.ArgumentList.Add "--preview"
        psi.ArgumentList.Add("\"" + exe + "\" --repl-doc {1}")

        for f in config.FinderFlags do
            psi.ArgumentList.Add f

        if query <> "" then
            psi.ArgumentList.Add "--query"
            psi.ArgumentList.Add query

        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.UseShellExecute <- false // fzf draws its UI on /dev/tty directly

        use p = Diagnostics.Process.Start psi

        for line in findCandidates te do
            p.StandardInput.WriteLine line

        p.StandardInput.Close()
        let sel = p.StandardOutput.ReadToEnd().TrimEnd('\n', '\r')
        p.WaitForExit()

        // pop the kitty keyboard stack unconditionally — the same
        // unkillable-child guard Ctrl+R's fzf spawn carries [D:binary-echo]
        if not Console.IsOutputRedirected then
            Console.Out.Write "\x1b[<u"
            Console.Out.Flush()

        if p.ExitCode = 0 && sel <> "" then
            // the name is the first whitespace-delimited field ({1} for
            // the preview, the same split here)
            Some((sel.Split ' ')[0])
        else
            None
    with _ ->
        None

let private findDirective (te: TypeEnv) (query: string) =
    let interactive =
        not Console.IsInputRedirected
        && not Console.IsOutputRedirected
        && Extern.exists "fzf"

    if interactive then
        match findFzf te query with
        | Some name -> Console.WriteLine(renderHelpDirective te name)
        | None -> () // cancel: no output, the session continues
    else
        Console.WriteLine(renderHelp (findFallback te query))

/// test seams [D:help-find] (the parseAliasLineForTest precedent): the
/// deterministic pieces, against the builtin session env
let helpTextForTest (arg: string) : string = helpDirective false initial.TypeEnv arg

/// the tty pipeline with the gate forced on [D:help-tint] — the unit
/// pins' seam for the composed tint (signature, example, prose spans)
let helpTintedForTest (arg: string) : string =
    renderHelpText true (helpDirective true initial.TypeEnv arg)

/// the example tint's resolver, exposed so the pins can call the input
/// colorizer with the SAME verdict the help render uses [D:help-tint]
let knownForTest: string -> bool = knownIn initial.TypeEnv
let findFallbackForTest (query: string) : string = findFallback initial.TypeEnv query
let findCandidatesForTest () : string list = findCandidates initial.TypeEnv

// ---- #infer [D:repl-infer]: draft named types from a sample ----------
// The directive OWNS its parse (string-match, like #help): split the
// line on the literal ` from ` and ` as ` markers, evaluate the source
// as an ordinary weir expression ONCE, parse the resulting seq<string>
// by the named adapter, walk it to a set of named `type` decls, INJECT
// them into the session TypeEnv (the same path a typed `type` line
// takes), and print `defined: …` plus any inference notes. `check`
// stays evaluation-free — this is the RUNTIME's exploration surface.

/// split "src from fmt as Name" on the literal markers; source may be
/// empty (defaults to `it`). Returns (source, format, name) or an error.
let private parseInfer (rest: string) : Result<string * string * string, string> =
    // find ` as ` LAST (a name has no spaces), ` from ` before it
    let asIdx = rest.LastIndexOf " as "

    if asIdx < 0 then
        Error "#infer <source> from <json|jsonl|yaml> as <Name> — missing 'as <Name>'"
    else
        let head = rest.Substring(0, asIdx)
        let name = rest.Substring(asIdx + 4).Trim()
        let fromIdx = head.LastIndexOf " from "

        if fromIdx < 0 then
            // allow "#infer from json as N": the head IS " from …" with an
            // empty source when it starts with the marker
            if head.TrimStart().StartsWith "from " then
                let fmt = head.TrimStart().Substring(5).Trim()
                Ok("", fmt, name)
            else
                Error "#infer <source> from <json|jsonl|yaml> as <Name> — missing 'from <format>'"
        else
            let source = head.Substring(0, fromIdx).Trim()
            let fmt = head.Substring(fromIdx + 6).Trim()
            Ok(source, fmt, name)

/// evaluate a source EXPRESSION to a seq<string> for the adapter (checked
/// against the session env, evaluated once); teaches on a non-seq<string>
let private evalSource (state: State) (source: string) : Result<string list, string> =
    let ll = Script.singleLine (Script.stripComment source)

    match Script.checkStatement false None (fun _ -> resolver state) Script.scriptOnlyImport state.TypeEnv ll with
    | Error d -> Error $"#infer: the source does not check: {d.Message}"
    | Ok chk ->
        let teOpt =
            match chk.Kind with
            | Script.KExpr te
            | Script.KCmd te
            | Script.KLet(_, _, te) -> Some te
            | _ -> None

        match teOpt with
        | None -> Error "#infer: the source must be an expression that produces seq<string>"
        | Some te ->
            if te.Ty <> TSeq TStr then
                Error $"#infer: the source has type {formatTy te.Ty}; the adapter needs seq<string> (a captured JSON/YAML sample)"
            else
                try
                    match Eval.eval state.Values te with
                    | Eval.VSeq items ->
                        Ok(
                            items
                            |> Seq.map (fun v ->
                                match v with
                                | Eval.VStr s -> s
                                | _ -> "")
                            |> List.ofSeq
                        )
                    | _ -> Error "#infer: the source did not produce a sequence"
                with ex ->
                    Error $"#infer: evaluating the source raised: {ex.Message}"

/// inject the decl TEXT (multi-line `type` blocks) into the session,
/// the drafted-type diagnostic [D:infer-diagnostic]: the drafted text is
/// weir's OWN synthesis, so a check failure is a generated-field bug the
/// user must SEE — number the drafted lines and render the offending one
/// with a caret, the same shape weir's normal parse/type errors use
/// (line:col + snippet), so the user can tell WHICH generated field is bad.
let formatDraftedDiag (physical: string list) (d: Script.StmtDiag) : string =
    let line = physical |> List.tryItem (d.PhysLine - 1) |> Option.defaultValue ""

    let caretWidth =
        match d.PhysEnd with
        | Some(el, ec) when el = d.PhysLine -> max 1 (ec - d.PhysCol)
        | _ -> 1

    let caret = String(' ', max 0 (d.PhysCol - 1)) + String('^', caretWidth)
    let on = Types.Color.onStdout.Value

    String.concat
        "\n"
        [ $"#infer: a drafted type did not check at line {d.PhysLine}, col {d.PhysCol}: {d.Message}"
          "  " + line
          "  " + Types.Color.red on caret ]

/// returning the new state and the ordered list of defined type names.
/// Reuses the assembler + checkStatement — the multiline submission path.
let private injectDecls (state: State) (declText: string list) : Result<State * string list, string> =
    // each decl is a MULTI-LINE `type` block — split to physical lines so
    // the assembler groups them (the multiline-submission path)
    let physical = declText |> List.collect (fun d -> d.Split '\n' |> List.ofArray)

    let numbered =
        physical
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> Script.classifyLine raw <> Script.LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, Script.stripComment raw)

    match Script.assemble numbered with
    | Error msg -> Error $"#infer: the drafted types did not assemble: {msg}"
    | Ok lls ->
        let rec go (st: State) (defined: string list) rest =
            match rest with
            | [] -> Ok(st, List.rev defined)
            | (ll: Script.LogicalLine) :: tail ->
                match
                    Script.checkStatement false None (fun _ -> resolver st) Script.scriptOnlyImport st.TypeEnv ll
                with
                | Error d -> Error(formatDraftedDiag physical d)
                | Ok chk ->
                    match chk.Kind with
                    | Script.KType decl ->
                        let ctors =
                            match decl.Body with
                            | DUnion cases -> Eval.constructorValues cases
                            | DRecord _ -> []

                        let st' =
                            { st with
                                TypeEnv = chk.Env
                                Values = ctors |> List.fold (fun vs (n, v) -> Map.add n v vs) st.Values }

                        go st' (decl.Name :: defined) tail
                    | _ -> Error "#infer: a drafted statement was not a type declaration"

        go state [] lls

let private inferDirective (state: State) (rest: string) : State =
    match parseInfer rest with
    | Error msg ->
        Console.WriteLine msg
        state
    | Ok(source, fmtStr, name) ->
        match Infer.parseFormat fmtStr with
        | Error msg ->
            Console.WriteLine msg
            state
        | Ok fmt ->
            // an empty source defaults to `it` — the last result
            let sourceExpr, sourceMissing =
                if source = "" then
                    (if Map.containsKey "it" state.TypeEnv.Values then "it", false else "", true)
                else
                    source, false

            if sourceMissing then
                Console.WriteLine
                    "#infer: no prior result — write '#infer <source> from … as <Name>', or run an expression first (it binds 'it')"

                state
            elif name = "" || not (Char.IsUpper name[0]) then
                Console.WriteLine $"#infer: the type name '{name}' must start with an uppercase letter"
                state
            elif Set.contains name (Infer.takenTypeNames Check.builtinTypeNames.Keys) then
                // the 'as'-name is the USER'S choice [D:repl-infer]: a
                // builtin name could never be referenced (the builtin
                // wins every use), and renaming their choice silently
                // would be worse — refuse with the teaching
                Console.WriteLine
                    $"#infer: '{name}' is a built-in type — a drafted '{name}' could never be referenced (the built-in wins every use); pick another 'as' name"

                state
            else
                match evalSource state sourceExpr with
                | Error msg ->
                    Console.WriteLine msg
                    state
                | Ok lines ->
                    // derived names dodge EVERY name the session already
                    // resolves [D:repl-infer] — a taken landing would be
                    // injected-and-shadowed, never usable
                    let taken =
                        Infer.takenTypeNames (Seq.append Check.builtinTypeNames.Keys (Map.keys state.TypeEnv.Types))

                    match Infer.infer Parser.keywords taken fmt name lines with
                    | Error msg ->
                        Console.WriteLine msg
                        state
                    | Ok(decls, notes) ->
                        match injectDecls state decls with
                        | Error msg ->
                            Console.WriteLine msg
                            state
                        | Ok(state', defined) ->
                            // the drafted types are session declarations —
                            // record each as a `TDef` carrying its CLEAN
                            // multi-line text (not the assembler's
                            // control-char-joined logical line) so #save
                            // distills runnable `type` decls, deduped by name
                            for d in decls do
                                // "type <Name> = …" — the name keys dedup
                                let declName =
                                    match d.Trim().Split([| ' '; '\n' |]) with
                                    | [| _typeKw; nm |] -> nm
                                    | arr when arr.Length >= 2 -> arr[1]
                                    | _ -> ""

                                recordDef declName d d

                            let count = List.length defined

                            Console.WriteLine
                                $"""defined: {String.concat ", " defined} ({count} type{if count = 1 then "" else "s"})"""

                            for n in notes do
                                Console.WriteLine $"  note: {n}"

                            state'

// ---- #save [D:repl-save]: DISTILL the session to a runnable script ----
// Option B: a REPL session is scratch; `#save <path>` crystallizes its
// reusable DEFINITIONS and GUARANTEES the written file `weir check`s
// clean. It KEEPS `type` decls and NAMED `let` bindings (the transcript's
// `TDef`s — carrying their real multi-line source), DROPS the bare-echo
// scratch and unit effect echoes, DEDUPS a re-declared name to its LAST
// definition (survivor order preserved), then AUTO-QUALIFIES bare aliases
// (map -> Seq.map) and formats. The CHECK GUARANTEE is the last gate: a
// survivor that still does not check — a named binding whose RHS
// references session-only state (`let gobeldy = it`), or one left unused
// once its consumers dropped — is removed, and #save prints a
// dropped-count note. Injected #infer types are ordinary `type` decls.

// one distilled STATEMENT: its dedup key (a type/binder name) and its
// physical source lines (already qualified)
type private DistillStmt = { Key: string; Lines: string list }

// DEDUP by key, keeping the LAST definition, preserving survivor order.
// A re-declared `type Color`/rebound `let x` keeps only its final form.
let private dedupLast (stmts: DistillStmt list) : DistillStmt list =
    let lastIdx =
        stmts
        |> List.mapi (fun i s -> s.Key, i)
        |> List.fold (fun m (k, i) -> Map.add k i m) Map.empty

    stmts |> List.mapi (fun i s -> i, s) |> List.filter (fun (i, s) -> Map.find s.Key lastIdx = i) |> List.map snd

// a `cmd-not-found` diagnostic that names the REPL-only `it` [D:repl-it]:
// `it` can NEVER be a real script command (a warning at check time, but
// in a distilled file it is always a session-only reference), so #save
// treats it as a DROP trigger — the `let x = it` the guarantee must
// remove. The message is `command not found on PATH: it{. hint | — …}`,
// so a `PATH: it` followed by a word boundary is the exact signal.
let private namesSessionIt (d: Script.Diagnostic) : bool =
    d.Code = "cmd-not-found"
    && (let marker = "command not found on PATH: it"

        d.Message.StartsWith marker
        && (d.Message.Length = marker.Length
            || (let c = d.Message[marker.Length] in c = '.' || c = ' ')))

// the distill-fatal diagnostics for a candidate file, [] when clean.
// Line-split each block, assemble the file, analyze it (the synthetic
// path is used only for import resolution — none here — and messages).
// Distill-fatal = any error-severity diagnostic OR a session-only `it`
// reference (a warning the guarantee still must act on).
let private candidateErrors (stmts: DistillStmt list) : Script.Diagnostic list =
    let lines = stmts |> List.collect (fun s -> s.Lines)

    if List.isEmpty lines then
        []
    else
        let diags, _, _, _ = Script.analyzeLines "#save" lines
        diags |> List.filter (fun d -> d.Severity = "error" || namesSessionIt d)

// PROTECT a self-contained-but-unused named `let` [D:repl-save]: the
// unused-binding law refuses a top-level `let x = <<<…>>>` that nothing
// reads, but a distilled DEFINITION is the product, not scratch — so it
// is KEPT, its binder `_`-prefixed (the deliberate-discard escape, which
// the law exempts) rather than dropped. This is NOT a session-only-state
// drop and is NOT counted. A `type` decl never needs this (types do not
// trip unused-binding). Only the FIRST line carries the binder.
let private protectUnusedLet (s: DistillStmt) : DistillStmt =
    match s.Lines with
    | first :: rest ->
        let t = first.TrimStart()
        let indent = first.Substring(0, first.Length - t.Length)

        if t.StartsWith "let " then
            let afterLet = t.Substring 4
            let binder = afterLet.Split([| ' '; '=' |]).[0]

            if binder <> "" && not (binder.StartsWith "_") then
                { s with
                    Lines = (indent + "let _" + afterLet) :: rest }
            else
                s
        else
            s
    | [] -> s

// THE CHECK GUARANTEE [D:repl-save]: return the survivors that check
// clean, plus the count DROPPED for referencing session-only state. Two
// moves, in order:
//   1. PROTECT — if the only errors are unused-binding, `_`-prefix those
//      named lets (a self-contained definition is kept, not dropped) and
//      recheck. Not counted as a drop.
//   2. DROP — otherwise remove the LAST survivor whose removal lets the
//      rest check (falling back to the last survivor for progress), the
//      one that referenced session-only state (`it`, an unbound name).
// A binding used only by a LATER survivor is kept (the whole-file check
// sees the forward reference). The loop always shrinks the candidate set
// or resolves the unused set, so it terminates.
// which survivor OWNS a candidate-file line? blocks are concatenated in
// order, each contributing `Lines.Length` physical lines — so a running
// offset maps a 1-based file line to the survivor index containing it.
let private ownerOf (kept: DistillStmt list) (fileLine: int) : int option =
    let rec go idx offset rest =
        match rest with
        | [] -> None
        | (s: DistillStmt) :: tail ->
            let n = s.Lines.Length

            if fileLine >= offset + 1 && fileLine <= offset + n then
                Some idx
            else
                go (idx + 1) (offset + n) tail

    go 0 0 kept

let rec private guaranteeChecks (kept: DistillStmt list) (dropped: int) : DistillStmt list * int =
    let errs = candidateErrors kept

    if List.isEmpty errs then
        kept, dropped
    else
        match kept with
        | [] -> [], dropped
        | _ ->
            // the survivors OWNING a session-only-state error (an `it`
            // reference, an unbound name) — these are DROPPED with a note.
            // A bare unused-binding is NOT here: it means a self-contained
            // definition nothing reads, which is PROTECTED, not dropped.
            let sessionErrs = errs |> List.filter (fun d -> not (d.Code = "unused-binding"))

            let ownersToDrop =
                sessionErrs |> List.choose (fun d -> ownerOf kept d.Line) |> List.distinct

            match ownersToDrop with
            | _ :: _ ->
                let kept' =
                    kept
                    |> List.mapi (fun j s -> j, s)
                    |> List.filter (fun (j, _) -> not (List.contains j ownersToDrop))
                    |> List.map snd

                guaranteeChecks kept' (dropped + List.length ownersToDrop)
            | [] ->
                // only unused-binding errors remain — PROTECT exactly the
                // lets the findings point at (a definition is the product,
                // kept not dropped) and recheck. SURGICAL by necessity: a
                // binder a LATER survivor reads is not unused, and renaming
                // it would orphan its readers into phantom commands
                // (`let _base = …` + `let _total = base |> …` — the rename
                // broke the chain the session built). If nothing could be
                // protected (a block-local finding, an already-`_` binder),
                // drop the finding's owner for progress.
                let unusedOwners =
                    errs |> List.choose (fun d -> ownerOf kept d.Line) |> List.distinct

                let protectedSet =
                    kept
                    |> List.mapi (fun j s -> if List.contains j unusedOwners then protectUnusedLet s else s)

                if protectedSet <> kept then
                    guaranteeChecks protectedSet dropped
                else
                    let victim =
                        unusedOwners |> List.tryHead |> Option.defaultValue (List.length kept - 1)

                    let kept' =
                        kept |> List.mapi (fun j s -> j, s) |> List.filter (fun (j, _) -> j <> victim) |> List.map snd

                    guaranteeChecks kept' (dropped + 1)

// the distill core, exposed as a seam for tests: transcript survivors
// (the `TDef` name + physical source) through qualify -> dedup -> the
// check guarantee. Returns the final qualified lines (pre-format) and the
// count of statements dropped by the check guarantee.
let distillDefs
    (r: Parser.Resolver)
    (aliasOf: string -> (string * string list) option)
    (defs: (string * string) list)
    : string list * int =
    // a resolver that parses alias NAMES as external heads (so an aliased
    // command line parses to an ECmd) but never rewrites them, so the head
    // keeps its source name and span for the desugar [D:command-head-alias]
    let desugarR =
        { r with
            IsExternal = (fun n -> r.IsExternal n || (aliasOf n).IsSome)
            AliasHead = fun _ -> None }

    // qualify bare module aliases, THEN desugar command-head aliases (both
    // per physical line, span-based; a multi-line heredoc/type keeps its
    // real newlines)
    let rewrite (line: string) =
        line |> Fmt.qualifyBareAliases r |> Fmt.desugarAliasHeads desugarR aliasOf

    let qualified =
        [ for (name, physText) in defs ->
              let lines = physText.Split '\n' |> Array.toList |> List.map rewrite
              { Key = name; Lines = lines } ]

    let deduped = dedupLast qualified
    let survivors, dropped = guaranteeChecks deduped 0
    (survivors |> List.collect (fun s -> s.Lines)), dropped

let private saveDirective (state: State) (path: string) : unit =
    if path = "" then
        Console.WriteLine "#save <path> — a destination path is required (e.g. #save explore.weir)"
    else
        // KEEP only definitions (types + named lets); DROP scratch echoes
        // and unit effect echoes
        let defs =
            [ for entry in transcript do
                  match entry.Kind with
                  | TDef name -> yield (name, entry.PhysText)
                  | TDiscard
                  | TOther -> () ]

        if List.isEmpty defs then
            Console.WriteLine
                "#save: nothing to save yet — the session has no definitions (a type or a named let binding)"
        else
            let r = resolver state

            let aliasOf n =
                Map.tryFind n state.Aliases |> Option.map (fun a -> (a.Exe, a.Prefix))

            let distilled, dropped = distillDefs r aliasOf defs

            let note () =
                if dropped > 0 then
                    Console.WriteLine
                        $"#save: dropped {dropped} line(s) that referenced session-only state (e.g. it) — bind a self-contained value to keep it"

            if List.isEmpty distilled then
                note ()
                Console.WriteLine "#save: nothing left to save after distilling — every definition referenced session-only state"
            else
                match Fmt.formatLines distilled with
                | Error msg ->
                    // the distilled lines still failed to format — write them
                    // raw rather than lose the session, and say what happened
                    Console.WriteLine $"#save: formatting failed ({msg}); writing unformatted"

                    try
                        File.WriteAllLines(path, distilled)
                        note ()
                        Console.WriteLine $"#save: wrote {distilled.Length} line(s) to {path} (unformatted)"
                    with ex ->
                        Console.WriteLine $"#save: could not write {path}: {ex.Message}"
                | Ok formatted ->
                    try
                        File.WriteAllLines(path, formatted)
                        note ()
                        Console.WriteLine $"#save: wrote {List.length formatted} line(s) to {path}"
                    with ex ->
                        Console.WriteLine $"#save: could not write {path}: {ex.Message}"

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
            Console.WriteLine(renderHelpDirective state.TypeEnv (t.Substring 5))
            loop state
        elif t = "#find" || t.StartsWith "#find " then
            findDirective state.TypeEnv ((t.Substring 5).Trim())
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
        elif t = "#infer" || t.StartsWith "#infer " then
            loop (inferDirective state (t.Substring(6).Trim()))
        elif t = "#save" || t.StartsWith "#save " then
            saveDirective state (t.Substring(5).Trim())
            loop state
        elif t = "#alias" || t.StartsWith "#alias " then
            // a live command-head alias [D:command-head-alias]. init.weir is
            // the canonical place; a bare `#alias` LISTS the table, a
            // `#alias name = cmd …` adds one (same single-hop rule)
            let body = t.Substring(6)

            if body.Trim() = "" then
                if Map.isEmpty state.Aliases then
                    Console.WriteLine "no aliases — declare them in the init file (weir/init.weir) or here: #alias k = kubectl"
                else
                    for KeyValue(name, a) in state.Aliases do
                        let tgt = String.concat " " (a.Exe :: a.Prefix)
                        Console.WriteLine $"#alias {name} = {tgt}"

                loop state
            else
                match parseAliasLine body with
                | Error msg ->
                    Console.WriteLine msg
                    loop state
                | Ok(name, alias) when alias.Exe <> name && Map.containsKey alias.Exe state.Aliases ->
                    Console.WriteLine
                        $"#alias {name} = {alias.Exe} …: an alias resolves to a program, not to another alias ('{alias.Exe}' is itself an alias) — aliases are single-hop"

                    loop state
                | Ok(name, alias) ->
                    let tgt = String.concat " " (alias.Exe :: alias.Prefix)
                    Console.WriteLine $"alias {name} = {tgt}"
                    loop { state with Aliases = Map.add name alias state.Aliases }
        else
            let word = t.Split(' ').[0]

            Console.WriteLine(
                if word = "#session" then
                    "#session is read from the init file at startup (config dir, weir/init.weir) — edit it and restart"
                elif word = "#sig" || word = "#schema" then
                    $"{word} is a file directive, read at check time — it has no effect in the REPL"
                else
                    // the did-you-mean pool is the dispatch's ONE source
                    // (Complete.sessionDirectives) [D:repl-directives]
                    let pool = Complete.sessionDirectives |> List.map (fun d -> "#" + d)
                    $"unknown directive '{word}' — #help lists them{didYouMean word pool}"
            )

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
                            Script.checkStatement
                                false
                                None
                                (fun _ -> resolver st)
                                Script.scriptOnlyImport
                                st.TypeEnv
                                ll
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
                        | Ok chk ->
                            let st' = evalChecked ll.Text st chk

                            (if not lastErrored then
                                 // carry the REAL physical source (newlines +
                                 // indentation), not the sentinel-joined
                                 // `ll.Text` — a kept heredoc/type must
                                 // round-trip through `weir check` [D:repl-save]
                                 let phys = physicalSource srcLines ll

                                 match recordKind chk with
                                 | Some(TDef name) -> recordDef name phys ll.Text
                                 | Some TDiscard -> recordDiscard phys ll.Text
                                 | Some TOther -> recordOther phys ll.Text
                                 | None -> ())

                            st')
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

            match
                Script.checkStatement false None (fun _ -> resolver state) Script.scriptOnlyImport state.TypeEnv ll
            with
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
            // the streamed-statement trap [D:repl-it]: the previous
            // statement inherited the terminal [D:colour-inherit], so no
            // value landed and `it` is unbound — the generic error (with
            // its did-you-mean) reads as a regression; teach the `let`
            // capture instead. The closing quote in the needle keeps
            // 'iterations' and friends on the generic path.
            | Error d when lastStreamed.IsSome && d.Message.StartsWith "unbound variable 'it'" ->
                lastErrored <- true

                d.Span
                |> Option.iter (underline >> Types.Color.red Types.Color.onStdout.Value >> Console.WriteLine)

                streamedItTeach lastStreamed |> List.iter Console.WriteLine
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
            | Ok chk ->
                let next = evalChecked ll.Text state chk

                (if not lastErrored then
                     // a single-line entry: physical source IS its own text
                     match recordKind chk with
                     | Some(TDef name) -> recordDef name ll.Text ll.Text
                     | Some TDiscard -> recordDiscard ll.Text ll.Text
                     | Some TOther -> recordOther ll.Text ll.Text
                     | None -> ())

                next

        loop next


// ---- the init file [D:repl-init] ----------------------------------------
// config dir/weir/init.weir: an implicitly-loaded, DECLARATION-ONLY file
// (the module rule, applied to the prompt) plus one #session directive
// for the four settings a declaration cannot express. Loading is
// ALL-OR-NOTHING: a broken init reports its located weir error and the
// session starts with none of it — safe to continue past ONLY because
// nothing in the file can run. This is not an exception to
// check-before-run: the init is not part of any program; a failed init
// means fewer names, never a program that half-ran.

let private initFilePath () =
    Path.Combine(configHome (), "weir", "init.weir")

let private sessionKeys = [ "cwd"; "env"; "logLevel"; "echoCap" ]

/// #session field values must be command-free (the module-let rule);
/// beyond that any closed expression checks — literals in practice
let private initDiag (path: string) (line: int) (col: int) (srcLine: string) (msg: string) =
    Console.Error.WriteLine $"{path}:{line}:{col}: {msg}"

    if srcLine <> "" then
        Console.Error.WriteLine srcLine
        Console.Error.WriteLine(String(' ', max 0 (col - 1)) + "^")

/// returns the session-block field lets (synthesized, line numbers kept)
/// and the remaining lines (block replaced by blanks — transparent, so
/// declaration diagnostics keep their real positions)
let private splitSessionBlock
    (path: string)
    (lines: string[])
    : Result<(int * string) list * (int * string) list * (int * string) list, unit> =
    let mutable fields: (int * string) list = []
    let mutable rest: (int * string) list = []
    // #alias directive lines, collected with their (1-based) line number;
    // the text is what follows `#alias` [D:command-head-alias]
    let mutable aliases: (int * string) list = []
    let mutable inBlock = false
    let mutable seen = false
    let mutable failed = false

    for i in 0 .. lines.Length - 1 do
        let l = lines.[i]
        let t = l.Trim()

        if not inBlock && (t = "#alias" || t.StartsWith "#alias ") then
            // a top-level directive line; kept out of the declaration
            // stream and replaced by a blank so declaration diagnostics keep
            // their real positions (the #session discipline)
            aliases <- (i + 1, t.Substring("#alias".Length)) :: aliases
            rest <- (i + 1, "") :: rest
        elif not inBlock && t.StartsWith "#session" then
            if seen then
                initDiag path (i + 1) 1 l "a second #session block — the init takes one"
                failed <- true

            seen <- true

            if t = "#session {" then
                inBlock <- true
            else
                initDiag path (i + 1) 1 l "malformed #session — usage: '#session {' then 'key = value' lines then '}'"
                failed <- true

            rest <- (i + 1, "") :: rest
        elif inBlock && t = "}" then
            inBlock <- false
            rest <- (i + 1, "") :: rest
        elif inBlock then
            if t <> "" then
                // a FIELD-START (`key = …`) dedents 4 and gains a 'let '
                // prefix — 4 chars out, 4 in, columns survive. Every
                // other line (list entries, closers) passes through
                // UNTOUCHED: still indented, still a continuation of the
                // synthesized let, columns exact
                let isFieldStart =
                    System.Text.RegularExpressions.Regex.IsMatch(t, "^[A-Za-z_][A-Za-z0-9_]*\s*=")

                let synthesized =
                    if isFieldStart then
                        "let " + (if l.StartsWith "    " then l.Substring 4 else t)
                    else
                        l

                fields <- (i + 1, synthesized) :: fields

            rest <- (i + 1, "") :: rest
        else
            rest <- (i + 1, l) :: rest

    if inBlock then
        initDiag path lines.Length 1 "" "unclosed #session block — a lone '}' line ends it"
        failed <- true

    if failed then
        Error()
    else
        Ok(List.rev fields, List.rev rest, List.rev aliases)

let private applySessionField
    (path: string)
    (lines: string[])
    (ll: Script.LogicalLine)
    (name: string)
    (te: Check.TypedExpr)
    (venv: Eval.Env)
    : bool =
    let srcLine (n: int) =
        if n >= 1 && n <= lines.Length then lines.[n - 1] else ""

    let err msg =
        initDiag path ll.Head 5 (srcLine ll.Head) msg
        false

    match name, te.Ty with
    | "cwd", Types.TStr ->
        (match Eval.eval venv te with
         | Eval.VStr p ->
             let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

             let expanded =
                 if p = "~" then
                     home
                 elif p.StartsWith "~/" then
                     Path.Combine(home, p.Substring 2)
                 else
                     p

             let resolved = Session.resolve expanded

             if Directory.Exists resolved then
                 Session.setCwd resolved
                 true
             else
                 err $"cwd: no such directory: {resolved}"
         | _ -> err "cwd expects a string")
    | "cwd", ty -> err $"cwd expects a string, got {Types.formatTy ty}"
    | "logLevel", Types.TStr ->
        (match Eval.eval venv te with
         | Eval.VStr lvl ->
             (match Builtins.parseLogLevel lvl with
              | Ok i ->
                  Builtins.setLogThreshold i
                  true
              | Error e -> err (e.Replace("WEIR_LOG=", "logLevel=")))
         | _ -> err "logLevel expects a string")
    | "logLevel", ty -> err $"logLevel expects a string, got {Types.formatTy ty}"
    | "echoCap", Types.TInt ->
        (match Eval.eval venv te with
         | Eval.VInt n when n > 0 ->
             echoCap <- Some(int n)
             true
         | Eval.VInt n -> err $"echoCap must be positive; got {n}"
         | _ -> err "echoCap expects an int")
    | "echoCap", ty -> err $"echoCap expects an int, got {Types.formatTy ty}"
    | "env", Types.TSeq(Types.TTuple [ Types.TStr; Types.TStr ]) ->
        (match Eval.eval venv te with
         | Eval.VSeq pairs ->
             // the session's base reality, not an overlay: set the
             // PROCESS env once, before the first prompt — exactly what
             // exec'ing weir with this environment would mean. Env.get,
             // Env.vars, and every spawn see it with no new machinery;
             // within env and the sigils layer over it as they layer
             // over any inherited env. (No unset: an entry adds or
             // overrides, never hides — stated, not discovered.)
             for pv in pairs do
                 match pv with
                 | Eval.VTuple [ Eval.VStr k; Eval.VStr v ] -> Environment.SetEnvironmentVariable(k, v)
                 | _ -> ()

             true
         | _ -> err "env expects seq<string * string>")
    | "env", ty -> err $"env expects seq<string * string> — pairs, order kept; got {Types.formatTy ty}"
    | k, _ ->
        let hint = didYouMean k (Set.ofList sessionKeys)
        err $"unknown #session key '{k}'{hint} (the keys: cwd, env, logLevel, echoCap)"

/// load the init file into the session; ALL-OR-NOTHING. Returns the
/// state to start with and prints the one report line (stderr — a piped
/// session's stdout stays data).
let private loadInit (baseState: State) : State =
    let path = initFilePath ()

    if not (File.Exists path) then
        baseState // no init is the normal case — silent
    else
        let lines = File.ReadAllLines path

        let srcLine (n: int) =
            if n >= 1 && n <= lines.Length then lines.[n - 1] else ""

        let notLoaded () =
            Console.Error.WriteLine $"init: not loaded ({path}) — the session starts without it"
            baseState

        match splitSessionBlock path lines with
        | Error() -> notLoaded ()
        | Ok(fieldLines, declLines, aliasLines) ->
            let checkAll (lls: Script.LogicalLine list) (tenv: TypeEnv) =
                let rec go env acc rest =
                    match rest with
                    | [] -> Ok(List.rev acc)
                    | (ll: Script.LogicalLine) :: tail ->
                        match Script.checkStatement false None Script.resolver Script.scriptOnlyImport env ll with
                        | Error d -> Error(ll, d)
                        | Ok chk -> go chk.Env ((ll, chk) :: acc) tail

                go tenv [] lls

            let strictTenv, strictVenv = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

            let reportDiag (ll: Script.LogicalLine) (d: Script.StmtDiag) =
                initDiag path d.PhysLine d.PhysCol (srcLine d.PhysLine) d.Message

            // the #alias table [D:command-head-alias] — a malformed line is
            // a LOUD init error (all-or-nothing), and a define-time
            // alias-of-alias is REJECTED (single-hop by construction: the
            // stored Exe is always a real program, never a table name)
            let aliasTable: Result<Map<string, Alias>, unit> =
                let mutable tbl: Map<string, Alias> = Map.empty
                let mutable failed = false

                for (lineNo, body) in aliasLines do
                    if not failed then
                        match parseAliasLine body with
                        | Error msg ->
                            initDiag path lineNo 1 (srcLine lineNo) msg
                            failed <- true
                        | Ok(name, alias) -> tbl <- Map.add name alias tbl

                if failed then
                    Error()
                else
                    // single-hop: reject any alias whose target is itself an
                    // alias name — an alias resolves to a binary, never to
                    // another alias
                    let mutable bad = None

                    for (lineNo, body) in aliasLines do
                        if bad.IsNone then
                            match parseAliasLine body with
                            // a self-shadow (`#alias ls = ls --color`) targets
                            // the REAL binary, bypassable with `^ls` — legal.
                            // Only a target naming a DIFFERENT alias is rejected.
                            | Ok(name, alias) when alias.Exe <> name && Map.containsKey alias.Exe tbl ->
                                bad <- Some(lineNo, name, alias.Exe)
                            | _ -> ()

                    match bad with
                    | Some(lineNo, name, exe) ->
                        initDiag
                            path
                            lineNo
                            1
                            (srcLine lineNo)
                            $"#alias {name} = {exe} …: an alias resolves to a program, not to another alias ('{exe}' is itself an alias) — aliases are single-hop"

                        Error()
                    | None -> Ok tbl

            // the #session fields first — settings before names
            let fieldsOutcome =
                match Script.assemble fieldLines with
                | Error msg ->
                    Console.Error.WriteLine $"{path}: {msg}"
                    false
                | Ok lls ->
                    match checkAll lls strictTenv with
                    | Error(ll, d) ->
                        reportDiag ll d
                        false
                    | Ok checked' ->
                        let mutable ok = true
                        let mutable seenKeys: Set<string> = Set.empty

                        for ll, chk in checked' do
                            if ok then
                                match chk.Kind with
                                | Script.KLet(name, _, te) ->
                                    if Set.contains name seenKeys then
                                        initDiag path ll.Head 5 (srcLine ll.Head) $"duplicate #session key '{name}'"
                                        ok <- false
                                    elif Script.runsCommandT te then
                                        initDiag
                                            path
                                            ll.Head
                                            5
                                            (srcLine ll.Head)
                                            "a #session value cannot run a command — settings are read, not computed by running things"

                                        ok <- false
                                    else
                                        seenKeys <- Set.add name seenKeys

                                        ok <-
                                            try
                                                applySessionField path lines ll name te strictVenv
                                            with ex ->
                                                // a RAISING value (File.read on a missing
                                                // path…) fails the load like any other
                                                // located error, never a raw trace
                                                // [D:init-eval-guard]
                                                initDiag path ll.Head 5 (srcLine ll.Head) ex.Message
                                                false
                                | _ ->
                                    initDiag
                                        path
                                        ll.Head
                                        1
                                        (srcLine ll.Head)
                                        "a #session block takes 'key = value' lines only"

                                    ok <- false

                        ok

            if not fieldsOutcome then
                notLoaded ()
            else

            match aliasTable with
            | Error() -> notLoaded ()
            | Ok aliasMap ->
                // the declarations — the module rule, applied to the prompt
                match Script.assemble declLines with
                | Error msg ->
                    Console.Error.WriteLine $"{path}: {msg}"
                    notLoaded ()
                | Ok lls ->
                    match checkAll lls baseState.TypeEnv with
                    | Error(ll, d) ->
                        reportDiag ll d
                        notLoaded ()
                    | Ok checked' ->
                        let mutable bad = None

                        for ll, chk in checked' do
                            if bad.IsNone then
                                match chk.Kind with
                                | Script.KType _ -> ()
                                | Script.KLet(_, _, te)
                                | Script.KLetPat(_, _, te) ->
                                    if Script.runsCommandT te then
                                        bad <-
                                            Some(
                                                ll,
                                                "an init 'let' cannot run a command at load — wrap it in a function (let f () = …), the command runs when the prompt calls it"
                                            )
                                | Script.KCmd _
                                | Script.KExpr _ ->
                                    bad <-
                                        Some(
                                            ll,
                                            "the init file is declaration-only — 'type' and 'let'; it configures the prompt, it cannot run"
                                        )
                                | Script.KImport _ ->
                                    bad <-
                                        Some(
                                            ll,
                                            "the init file does not import — declare here, or put shared code in a module scripts import"
                                        )
                                | Script.KModule _ ->
                                    bad <-
                                        Some(
                                            ll,
                                            "the init file is not a module — no 'module' marker; its names are the prompt's"
                                        )
                                | Script.KSig _ ->
                                    // unreachable: no sig context here, the
                                    // form already refused [D:module-signatures]
                                    ()

                        match bad with
                        | Some(ll, msg) ->
                            initDiag path ll.Head 1 (srcLine ll.Head) msg
                            notLoaded ()
                        | None ->
                            let attachments = Script.docAttachments (List.ofArray lines)
                            let mutable venv = baseState.Values
                            let mutable tenv = baseState.TypeEnv
                            let mutable names = 0
                            let mutable docs: Map<string, string list> = Map.empty
                            let mutable evalFailed = false

                            for ll, chk in checked' do
                                if not evalFailed then
                                    tenv <- chk.Env

                                    try
                                        match chk.Kind with
                                        | Script.KType decl ->
                                            names <- names + 1

                                            (match decl.Body with
                                             | Ast.DUnion cases ->
                                                 venv <-
                                                     Eval.constructorValues cases
                                                     |> List.fold (fun m (n, v) -> Map.add n v m) venv
                                             | Ast.DRecord _ -> ())
                                        | Script.KLet(name, _, te) ->
                                            names <- names + 1
                                            venv <- Map.add name (Eval.eval venv te) venv

                                            (match
                                                attachments
                                                |> List.tryFind (fun (a: Script.DocAttach) -> a.Line = ll.Head)
                                             with
                                             | Some a -> docs <- Map.add name a.Doc docs
                                             | None -> ())
                                        | Script.KLetPat(pat, _, te) ->
                                            let bindings = Eval.bindPattern pat (Eval.eval venv te)
                                            names <- names + bindings.Length
                                            venv <- bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv
                                        | _ -> ()
                                    with ex ->
                                        // a raising let fails the load LOCATED
                                        // [D:init-eval-guard]; names stay
                                        // all-or-nothing (nothing below binds). An effect an
                                        // earlier let already ran is the file's own doing —
                                        // the load reports, it cannot unwrite
                                        initDiag path ll.Head 1 (srcLine ll.Head) ex.Message
                                        evalFailed <- true

                            if evalFailed then
                                notLoaded ()
                            else

                                initDocs <- docs

                                if names > 0 || not (List.isEmpty fieldLines) || not (Map.isEmpty aliasMap) then
                                    let aliasNote =
                                        if Map.isEmpty aliasMap then
                                            ""
                                        else
                                            $", {Map.count aliasMap} alias(es)"

                                    Console.Error.WriteLine $"init: {names} name(s){aliasNote} from {path}"

                                { TypeEnv = tenv
                                  Values = venv
                                  Aliases = aliasMap }

let run () =
    if not Console.IsInputRedirected then
        setupLineEditor ()

    let state = loadInit initial

    try
        loop state
        0
    with Eval.ExitRequest code ->
        code
