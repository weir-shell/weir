// `weir add sig`: generating a signature by probing the tool. One of the files Script.fs was [D:script-by-concern].
namespace Weir

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor
open Weir.Statements
open Weir.ModuleLoader
open Weir.Diagnostics
open Weir.Sigs

// ---- `weir add sig <tool>`: generation [D:command-signatures] --------------
// Sources in fidelity order [D:sig-sources], the chosen one recorded in the
// provenance comment and the lock's Url slot ("generated:<source>"): a
// cobra tool's `__complete` endpoint, git's own command list and
// completion helper, a completion endpoint or shipped fish file, a
// full-help switch the help names (`--help all`, `--dump-options`,
// `--help-all`, `--v8-options`), then `--help` rows (and `-h`/`-help`
// when `--help` refuses). The help pages always ride along: they carry
// the arities and the subcommand tables. Subcommands are walked from the
// tables only — a word the tool did not list as a command is never run.
// The generated file is validated (loads as a signature) before anything
// persists.
module SigGen =
    // the probes whose output passed the byte bound, for the generated
    // file's note [D:sig-probe-bounds]
    let private cutProbes = System.Collections.Concurrent.ConcurrentQueue<string>()

    // the probes stopped at the time bound: what they printed is read, and
    // the file and the added line say so [D:sig-probe-bounds]
    let private slowProbes = System.Collections.Concurrent.ConcurrentQueue<string>()

    // one run per argv and cwd in a generation: git's builtins ask the
    // same helper and `-h` from two places, the refused-help harvest asks
    // `--help` again
    let private probed =
        System.Collections.Concurrent.ConcurrentDictionary<
            string option * string * string list,
            Lazy<Contracts.ProbeOutput option>
         >()

    let private runIn (cwd: string option) (anyExit: bool) (tool: string) (args: string list) : string option =
        // the bounded probe [D:sig-probe-bounds]: empty stdin (a
        // stdin-reader must not hang generation), a fresh temp cwd, a
        // time and a byte bound, the probe's process group killed
        let run () =
            match
                (try
                    Contracts.runProbe (Proc.resolveProg tool) args cwd
                 with _ ->
                     None)
            with
            | Some r as res ->
                if r.Cut then
                    cutProbes.Enqueue(String.concat " " args)
                elif r.Exit.IsNone then
                    slowProbes.Enqueue(String.concat " " args)

                res
            | None -> None

        let once = probed.GetOrAdd((cwd, tool, args), (fun _ -> lazy (run ())))

        match once.Force() with
        | Some r when r.Exit = Some 0 || r.Cut || r.Exit.IsNone || (anyExit && r.Exit.IsSome) ->
            match Contracts.stripAnsi (if r.Out.Trim() <> "" then r.Out else r.Err) with
            | blank when blank.Trim() = "" -> None
            | text -> Some(text.Replace("\r", ""))
        | _ -> None

    let private runTool = runIn None false

    // BSD grep's --help exits 2 with the usage on stderr — a nonzero
    // exit's dump is harvest-only material [D:sig-version-probe]:
    // never structured rows, never the walk, never a bare-word gate
    let private runToolAnyExit = runIn None true

    // one discovered flag: its spellings (a `--long`, one-letter shorts,
    // whole dash words), the value it takes when known, a doc
    type private Flag = {
        Long: string
        Shorts: string list
        Literals: string list
        Arity: SigArity option
        Doc: string option
        // the value is a boolean (`--legend=BOOL`): `--no-x` is the
        // tool's too [D:sig-help-shapes]
        BoolValue: bool
    }

    let private noFlag = {
        Long = ""
        Shorts = []
        Literals = []
        Arity = None
        Doc = None
        BoolValue = false
    }

    let private legalLong (l: string) =
        l.Length > 0
        && System.Char.IsLetterOrDigit l[0]
        && l |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '-' || c = '_')

    let private legalShort (s: string) =
        s.Length = 1
        && (System.Char.IsLetterOrDigit s[0] || s = "?" || s = "#" || s = "@")

    let private keyOf (f: Flag) =
        if f.Long <> "" then
            "--" + f.Long
        else
            match f.Shorts, f.Literals with
            | s :: _, _ -> "-" + s
            | [], l :: _ -> l
            | [], [] -> ""

    // one flag per spelling key, first source wins on arity and doc;
    // spellings union
    let private mergeFlags (lists: Flag list list) : Flag list =
        let byKey = System.Collections.Generic.Dictionary<string, Flag>()
        let order = ResizeArray<string>()
        // a short or literal already owned by a long never starts its own flag
        let owner = System.Collections.Generic.Dictionary<string, string>()

        for f in List.concat lists do
            let k0 = keyOf f

            let k =
                if f.Long = "" then
                    match owner.TryGetValue k0 with
                    | true, o -> o
                    | _ -> k0
                else
                    k0

            if k <> "" then
                match byKey.TryGetValue k with
                | true, g ->
                    byKey[k] <- {
                        g with
                            Shorts = g.Shorts @ (f.Shorts |> List.filter (fun s -> not (List.contains s g.Shorts)))
                            Literals =
                                g.Literals
                                @ (f.Literals |> List.filter (fun s -> not (List.contains s g.Literals)))
                            Arity = g.Arity |> Option.orElse f.Arity
                            Doc = g.Doc |> Option.orElse f.Doc
                            BoolValue = g.BoolValue || f.BoolValue
                    }
                | _ ->
                    byKey[k] <- f
                    order.Add k

                let g = byKey[k]

                for s in g.Shorts do
                    owner["-" + s] <- k

                for l in g.Literals do
                    owner[l] <- k

        // a short-only entry recorded before its long arrived folds in
        [
            for k in order do
                let f = byKey[k]

                if f.Long <> "" || not (owner.ContainsKey k && owner[k] <> k) then
                    yield f
        ]

    // ---- the parsers ----------------------------------------------------

    let private allCaps (t: string) =
        t.Length > 0
        && System.Char.IsUpper t[0]
        && t
           |> Seq.forall (fun c -> System.Char.IsUpper c || System.Char.IsDigit c || "_-|.:[]".Contains c)

    // the head of a help row: its spellings and the value shape, then the
    // doc. `-o, --outfile <x>  doc`, `--file=ARCHIVE`, `-i[SUFFIX],
    // --in-place[=SUFFIX]`, `-E, -r, --regexp-extended`, cobra's
    // `-a, --attach list   doc`, python's `-c cmd : doc`, Go's `-H value`.
    // None when the line does not start with a spelling.
    let private parseRow (line: string) : Flag option =
        // git's `--[no-]quiet` is the long `--quiet` (its helper lists
        // the `--no-` form apart)
        let s = line.TrimStart().Replace("--[no-]", "--")

        if not (s.StartsWith "-") || s.StartsWith "--- " || s = "--" || s.StartsWith "-- " then
            None
        else
            let mutable pos = 0
            let mutable longs = []
            let mutable shorts = []
            let mutable literals = []
            let mutable arity: SigArity option = None
            let mutable boolValue = false
            let mutable go = true
            let n = s.Length
            let at i = if i < n then s[i] else '\000'

            let readWhile (i: int) (pred: char -> bool) =
                let mutable j = i

                while j < n && pred s[j] do
                    j <- j + 1

                j

            while go do
                // one spelling, `+x` toggles skipped (fzf)
                if at pos = '+' then
                    pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                elif at pos = '-' then
                    let dashes = if at (pos + 1) = '-' then 2 else 1
                    let st = pos + dashes

                    let e =
                        readWhile st (fun c ->
                            System.Char.IsLetterOrDigit c
                            || c = '-'
                            || c = '_'
                            || c = '?'
                            || c = '#'
                            || c = '@')

                    let name = s.Substring(st, e - st)

                    let letters =
                        System.Text.RegularExpressions.Regex.Match(s.Substring st, "^\\[([A-Za-z]{2,})\\]")

                    // the spelling's end, or -1 when this is no spelling
                    let spelled =
                        if dashes = 1 && at st = '<' && s.IndexOf('>', st) > 0 then
                            // kill's `-<signal>`: any dash word, no spelling of
                            // its own (the help states it [D:sig-help-shapes])
                            s.IndexOf('>', st) + 1
                        elif dashes = 1 && letters.Success then
                            // perl's `-[mM]`: each letter a short
                            for c in letters.Groups[1].Value do
                                shorts <- shorts @ [ string c ]

                            st + letters.Length
                        elif name = "" || (name.Length > 1 && not (System.Char.IsLetterOrDigit name[0])) then
                            -1
                        elif
                            dashes = 1
                            && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][a-z]{2,}$")
                        then
                            // perl's `-Idirectory`: the letter, its value glued
                            shorts <- shorts @ [ name.Substring(0, 1) ]
                            arity <- Some AOne
                            e
                        else
                            if dashes = 2 then
                                if legalLong name then
                                    longs <- longs @ [ name ]
                            elif name.Length = 1 then
                                if legalShort name then
                                    shorts <- shorts @ [ name ]
                            elif legalLong name then
                                literals <- literals @ [ "-" + name ]

                            e

                    if spelled < 0 then
                        go <- false
                    else
                        let oneLetter = dashes = 1 && name.Length = 1 && spelled = e
                        pos <- spelled
                        // the value shape glued to the spelling
                        let glued = s.Substring(pos, readWhile pos (fun c -> c <> ' ' && c <> ',') - pos)

                        if
                            System.Text.RegularExpressions.Regex.IsMatch(
                                glued,
                                "^\\[?=(BOOL|BOOLEAN|<bool>|<boolean>)\\]?$",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                            )
                        then
                            boolValue <- true

                        if at pos = '[' then
                            arity <- Some AOpt
                            pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                        elif at pos = '=' then
                            arity <- Some AOne
                            pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                        elif oneLetter && "/:%@<".Contains(at pos) then
                            // perl's `-F/pattern/`: a value glued to the letter
                            arity <- Some AOne
                            pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                        elif
                            at pos = ' '
                            && at (pos + 1) <> ' '
                            && at (pos + 1) <> '-'
                            && at (pos + 1) <> '\000'
                        then
                            // ` <x>`, ` VALUE`, ` [X]`, ` cmd :`, cobra's
                            // ` string   doc` — one word, then a gap,
                            // a comma, a colon or the end of the line
                            let ts = pos + 1
                            let te = readWhile ts (fun c -> c <> ' ' && c <> ',')
                            let tok = s.Substring(ts, te - ts)
                            let tail = s.Substring te

                            let gapAfter =
                                tail = ""
                                || tail.StartsWith "  "
                                || tail.StartsWith ", "
                                || tail.StartsWith ","
                                || tail.StartsWith " :"
                                || tail.StartsWith ":"

                            let placeholder =
                                (tok.StartsWith "<" && tok.TrimEnd(':').EndsWith ">")
                                || (tok.StartsWith "[" && tok.EndsWith "]")
                                || allCaps (tok.TrimEnd(':', ','))
                                || tok = "..."
                                || (gapAfter
                                    && tok.Length > 0
                                    && System.Char.IsLower tok[0]
                                    && tok.TrimEnd(':', ',')
                                       |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || "|-_.[]<>=".Contains c))

                            // az's `[Required]` marks the flag, not a value
                            if tok = "[Required]" then
                                pos <- te
                            elif placeholder then
                                arity <- Some(if tok.StartsWith "[" then AOpt else AOne)
                                pos <- te
                            elif System.Text.RegularExpressions.Regex.IsMatch(tok, "^<[^<>]+>\\.\\.\\.$") then
                                // fd's `--exec <cmd>...`: the values run to the
                                // end — the tool's options end here [D:sig-arity]
                                arity <- Some ARest
                                pos <- te

                        // the next spelling: `, -x`, ` | -x`, `|-x`, `, `
                        let rest = s.Substring pos

                        if rest.StartsWith ", -" || rest.StartsWith ",-" then
                            pos <- pos + (if rest.StartsWith ", " then 2 else 1)
                        elif System.Text.RegularExpressions.Regex.IsMatch(rest, "^, +-") then
                            // shfmt's aligned `-w,     --write`
                            pos <- pos + (rest.Length - rest.Substring(1).TrimStart().Length)
                        elif rest.StartsWith " | -" then
                            pos <- pos + 3
                        elif rest.StartsWith "|-" then
                            pos <- pos + 1
                        elif rest.StartsWith " -" && not (rest.StartsWith "  ") then
                            // journalctl's `-h --help`, az's `--uri --url -u`
                            // (no comma)
                            pos <- pos + 1
                        elif
                            longs.IsEmpty
                            && not shorts.IsEmpty
                            && rest.StartsWith "  "
                            && rest.TrimStart().StartsWith "--"
                        then
                            // a box table's `-d    --dates` (the column gap)
                            pos <- pos + (rest.Length - rest.TrimStart().Length)
                        else
                            go <- false
                else
                    go <- false

            if longs.IsEmpty && shorts.IsEmpty && literals.IsEmpty then
                None
            else
                let doc = s.Substring(min pos n).Trim().TrimStart(':').Trim()

                // the tool's own options end at this one (python's `-c cmd`,
                // `-m mod`) [D:sig-arity]
                let arity =
                    if doc.Contains "terminates option list" then
                        Some ARest
                    else
                        arity

                Some {
                    Long = List.tryHead longs |> Option.defaultValue ""
                    Shorts = shorts
                    Literals = literals
                    Arity = arity
                    Doc = (if doc = "" then None else Some doc)
                    BoolValue = boolValue
                }

    // every long a row names beyond its first is an alias of it (`-n,
    // --quiet, --silent`; az's `--name --alias -n`): they become their own
    // flags with the row's arity
    let private rowAliases (line: string) : Flag list =
        let s = line.TrimStart().Replace("--[no-]", "--")

        if not (s.StartsWith "-") then
            []
        else
            // spellings up to the doc gap only
            let head =
                match s.IndexOf "  " with
                | -1 -> s
                | i -> s.Substring(0, i)

            System.Text.RegularExpressions.Regex.Matches(head, "(?:^|[\\s,|])--([A-Za-z0-9][A-Za-z0-9_-]*)")
            |> Seq.map (fun m -> m.Groups[1].Value)
            |> Seq.distinct
            |> List.ofSeq
            |> function
                | _ :: aliases -> aliases |> List.map (fun l -> { noFlag with Long = l })
                | [] -> []

    let private parseHelp (text: string) : Flag list =
        let flags = ResizeArray<Flag>()
        // a docless row adopts the next deeper prose line (Go's flag
        // package, git's -h)
        let mutable docless = -1
        let mutable rowIndent = 0

        // a two-column table (unzip's `-p  doc     -l  doc`): the right
        // column is a row of its own
        let columns (raw: string) =
            match
                System.Text.RegularExpressions.Regex.Match(raw, "^(\\s*-.*?\\S)\\s{3,}(-[A-Za-z][A-Za-z]?\\s+\\S.*)$")
            with
            | m when m.Success && (parseRow m.Groups[1].Value).IsSome ->
                let ind = raw.Length - raw.TrimStart().Length
                [ m.Groups[1].Value; String.replicate ind " " + m.Groups[2].Value ]
            | _ -> [ raw ]

        for raw in text.Split '\n' |> Array.collect (columns >> Array.ofList) do
            // broot draws its options as a box table — the border and
            // column glyphs (U+2500..U+257F) become spaces and the rows
            // collapse into the standard `-d  --dates  desc` shape
            let line = System.Text.RegularExpressions.Regex.Replace(raw, "[─-╿]", " ")
            let indent = line.Length - line.TrimStart().Length

            match parseRow line with
            | None when docless >= 0 && line.Trim() <> "" && indent > rowIndent ->
                flags[docless] <- {
                    flags[docless] with
                        Doc = Some(line.Trim())
                }

                docless <- -1
            | None ->
                if line.Trim() <> "" then
                    docless <- -1
            | Some f ->
                docless <- (if f.Doc.IsNone then flags.Count else -1)
                rowIndent <- indent

                let aliases =
                    rowAliases line |> List.map (fun a -> { a with Arity = f.Arity; Doc = f.Doc })

                flags.Add f
                flags.AddRange aliases

                // python's `-h : … (also -? or --help)`: the doc names more
                // spellings of the same flag
                match f.Doc with
                | Some d ->
                    for m in System.Text.RegularExpressions.Regex.Matches(d, "\\(also ([^)]*)\\)") do
                        for t in m.Groups[1].Value.Split([| ' '; ',' |], StringSplitOptions.RemoveEmptyEntries) do
                            if t.StartsWith "--" && legalLong (t.Substring 2) then
                                flags.Add {
                                    noFlag with
                                        Long = t.Substring 2
                                        Arity = f.Arity
                                }
                            elif t.Length = 2 && t[0] = '-' && legalShort (t.Substring 1) then
                                flags.Add {
                                    noFlag with
                                        Shorts = [ t.Substring 1 ]
                                        Arity = f.Arity
                                }
                | None -> ()

        mergeFlags [ List.ofSeq flags ]

    // usage brackets: `[-w|--workspace <name>]`, `[--json]`, git's
    // `[-C <path>] [--exec-path[=<path>]] [-p | --paginate | -P]`
    let private parseUsage (text: string) : Flag list =
        let flags = ResizeArray<Flag>()

        for m in System.Text.RegularExpressions.Regex.Matches(text, "\\[((?:[^\\[\\]]|\\[[^\\[\\]]*\\])*)\\]") do
            let inner = m.Groups[1].Value.Trim()

            if inner.StartsWith "-" then
                for alt in inner.Split '|' do
                    let a = alt.Trim()

                    if a.StartsWith "-" then
                        // a nested `[-w|--workspace <x> ...]` repeat is the same flag
                        match parseRow ("  " + a.Replace("--[no-]", "--")) with
                        | Some f -> flags.Add { f with Doc = None }
                        | None -> ()

        mergeFlags [ List.ofSeq flags ]

    // the last-resort pass [D:sig-version-probe]: a usage-table help
    // (weir's own: `weir check [--json] <script>`) has no flag rows, so
    // the structured scrape finds nothing — harvest every --flag token
    // instead, docless. Only ever consulted at zero structured hits;
    // partial by default covers the imprecision.
    let private harvestHelp (text: string) : Flag list =
        System.Text.RegularExpressions.Regex.Matches(text, "--([a-zA-Z][a-zA-Z0-9-]*)")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Seq.filter legalLong
        |> Seq.distinct
        |> Seq.map (fun long -> { noFlag with Long = long })
        |> List.ofSeq

    // fish `complete` lines, both spellings: `-c tool -s x -l long -o old
    // -d desc -r` and `--command tool --short-option x --long-option long
    // --old-option old --description desc --require-parameter`; `-r`/`-x`
    // (and their long forms) mark a value. A line whose condition asks for
    // a subcommand (`-n "__fish_tool_using_subcommand rec"`, `-n
    // "__fish_seen_subcommand_from rec"`) is that subcommand's flag, not
    // the tool's [D:sig-fish-scoped]
    let private fishUnderSub =
        System.Text.RegularExpressions.Regex @"(?<!\bnot\s+)\b__fish_\w*(?:using_subcommand|seen_subcommand_from)\s+\S"

    let private parseFish (text: string) (tool: string) : Flag list =
        let flags = ResizeArray<Flag>()

        for line in text.Split '\n' do
            let t = line.Trim()

            if t.StartsWith "complete " then
                let tokens =
                    System.Text.RegularExpressions.Regex.Matches(t, "'(?:[^'\\\\]|\\\\.)*'|\"[^\"]*\"|\\S+")
                    |> Seq.map (fun m ->
                        // fish's single quotes escape only `'` and `\`
                        if m.Value.StartsWith "'" then
                            m.Value.Substring(1, m.Value.Length - 2).Replace("\\'", "'").Replace("\\\\", "\\")
                        else
                            m.Value.Trim('"'))
                    |> List.ofSeq

                let underSub =
                    tokens
                    |> List.pairwise
                    |> List.exists (fun (k, v) -> (k = "-n" || k = "--condition") && fishUnderSub.IsMatch v)

                let rec walk (cmd: string option) (f: Flag) toks =
                    match toks with
                    | ("-c" | "--command") :: v :: rest -> walk (Some v) f rest
                    | ("-l" | "--long-option") :: v :: rest ->
                        walk
                            cmd
                            {
                                f with
                                    Long = (if f.Long = "" then v else f.Long)
                            }
                            rest
                    | ("-s" | "--short-option") :: v :: rest -> walk cmd { f with Shorts = f.Shorts @ [ v ] } rest
                    | ("-o" | "--old-option") :: v :: rest ->
                        walk
                            cmd
                            {
                                f with
                                    Literals = f.Literals @ [ "-" + v ]
                            }
                            rest
                    | ("-d" | "--description") :: v :: rest -> walk cmd { f with Doc = Some v } rest
                    | ("-r" | "--require-parameter" | "-x" | "--exclusive") :: rest ->
                        walk cmd { f with Arity = Some AOne } rest
                    | ("-n" | "--condition" | "-a" | "--arguments" | "-w" | "--wraps" | "-k" | "--keep-order") :: _ :: rest ->
                        walk cmd f rest
                    | _ :: rest -> walk cmd f rest
                    | [] -> cmd, f

                match walk None noFlag tokens with
                | Some c, f when c = tool && not underSub ->
                    let f = {
                        f with
                            Shorts = f.Shorts |> List.filter legalShort
                            Literals = f.Literals |> List.filter (fun l -> legalLong (l.Substring 1))
                    }

                    if
                        (f.Long <> "" && legalLong f.Long)
                        || not f.Shorts.IsEmpty
                        || not f.Literals.IsEmpty
                    then
                        flags.Add(if legalLong f.Long then f else { f with Long = "" })
                | _ -> ()

        mergeFlags [ List.ofSeq flags ]

    // cobra's `__complete <path> -`: `--flag<TAB>doc` lines, a flag's
    // short right after it with the same doc, then the `:N` directive
    let private parseCobraComplete (text: string) : Flag list =
        let rows =
            text.Split '\n'
            |> Array.choose (fun l ->
                let name, doc =
                    match l.IndexOf '\t' with
                    | -1 -> l.Trim(), ""
                    | i -> l.Substring(0, i).Trim(), l.Substring(i + 1).Trim()

                if name.StartsWith "-" && not (name.StartsWith ":") then
                    Some(name, doc)
                else
                    None)
            |> List.ofArray

        let doc (d: string) = if d = "" then None else Some d

        let rec go (acc: Flag list) rows =
            match rows with
            | [] -> List.rev acc
            | (n: string, d) :: rest when n.StartsWith "--" ->
                let shorts, rest =
                    match rest with
                    | (s: string, d2) :: rest' when s.Length = 2 && not (s.StartsWith "--") && d2 = d ->
                        [ s.Substring 1 ], rest'
                    | _ -> [], rest

                go
                    ({
                        noFlag with
                            Long = n.Substring 2
                            Shorts = shorts
                            Doc = doc d
                     }
                     :: acc)
                    rest
            | (s, d) :: rest when s.Length = 2 ->
                go
                    ({
                        noFlag with
                            Shorts = [ s.Substring 1 ]
                            Doc = doc d
                     }
                     :: acc)
                    rest
            | _ :: rest -> go acc rest

        go [] rows
        |> List.map (fun f -> {
            f with
                Shorts = f.Shorts |> List.filter legalShort
        })
        |> List.filter (fun f -> (f.Long = "" || legalLong f.Long) && (f.Long <> "" || not f.Shorts.IsEmpty))
        |> fun fs -> mergeFlags [ fs ]

    // git's `--git-completion-helper`: one line of `--x`, `--y=` (a
    // value), then `--` and the `--no-x` forms
    let private parseGitHelper (text: string) : Flag list =
        text.Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun t ->
            if t.StartsWith "--" && t.Length > 2 then
                let v = t.EndsWith "="
                let name = t.Substring(2).TrimEnd '='

                if legalLong name then
                    Some {
                        noFlag with
                            Long = name
                            Arity = Some(if v then AOne else ANone)
                    }
                else
                    None
            else
                None)
        |> List.ofArray
        |> fun fs -> mergeFlags [ fs ]

    // V8's `--v8-options`: `  --name (doc)` then `type: bool  default: …`
    let private parseV8 (text: string) : Flag list =
        let lines = text.Split '\n'

        [
            for i in 0 .. lines.Length - 1 do
                let m =
                    System.Text.RegularExpressions.Regex.Match(lines[i], "^  --([a-z0-9][a-z0-9_-]*) \\((.*)\\)\\s*$")

                if m.Success then
                    let ty =
                        if i + 1 < lines.Length then
                            let t = System.Text.RegularExpressions.Regex.Match(lines[i + 1], "type: (\\S+)")
                            if t.Success then t.Groups[1].Value else ""
                        else
                            ""

                    yield {
                        noFlag with
                            Long = m.Groups[1].Value.Replace('_', '-')
                            Arity = Some(if ty = "bool" || ty = "maybe_bool" then ANone else AOne)
                            Doc = Some m.Groups[2].Value
                    }
        ]
        |> fun fs -> mergeFlags [ fs ]

    // gpg's `--dump-options`: one `--name` per line
    let private parseDump (text: string) : Flag list =
        text.Split '\n'
        |> Array.choose (fun l ->
            let t = l.Trim()

            if t.StartsWith "--" && legalLong (t.Substring 2) then
                Some { noFlag with Long = t.Substring 2 }
            else
                None)
        |> List.ofArray
        |> fun fs -> mergeFlags [ fs ]

    // find's predicates: single-dash words, several per line, a value
    // when an all-caps word or N follows (`-name PATTERN`, `-mtime N`)
    let private parsePredicates (text: string) : Flag list =
        [
            for line in text.Split '\n' do
                let toks = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

                for i in 0 .. toks.Length - 1 do
                    let t = toks[i]

                    if
                        t.Length > 2
                        && t[0] = '-'
                        && t[1] <> '-'
                        && legalLong (t.Substring 1)
                        && System.Char.IsLower t[1]
                    then
                        let next = if i + 1 < toks.Length then toks[i + 1] else ""

                        let arity =
                            if next = "COMMAND" then
                                ARest
                            elif
                                next <> ""
                                && (allCaps next || next.StartsWith "[" || next = "N" || next.StartsWith "N[")
                            then
                                AOne
                            else
                                ANone

                        yield {
                            noFlag with
                                Literals = [ t ]
                                Arity = Some arity
                        }
        ]
        |> fun fs -> mergeFlags [ fs ]

    // a heading names a command table: non-indented, mentions commands,
    // never options/flags/usage, ends with a colon or is an all-caps banner
    let private isCommandHeading (line: string) =
        let low = line.ToLowerInvariant()

        line.Length > 0
        && not (System.Char.IsWhiteSpace line[0])
        && not (line.StartsWith "-")
        && low.Contains "command"
        && not (low.Contains "option" || low.Contains "flag" || low.Contains "usage")
        && (line.TrimEnd().EndsWith ":" || line = line.ToUpperInvariant())

    // the phantom filter [D:sig-sources]: the words a help page lists as
    // its commands — rows at the table's own indent whose first word
    // starts with a letter and ends at a gap, a colon (gh's `auth:`), an
    // argument shape (systemctl's `list-units [PATTERN...]`) or the line;
    // a prose line, a description column, a deeper continuation, a flag
    // row never count. npm's comma-separated table counts word by word.
    let private commandTokens (helpText: string) : string list =
        let subs = ResizeArray<string>()
        let mutable inSection = false
        let mutable indent = -1

        let word (t: string) =
            t.Length > 1
            && System.Char.IsLower t[0]
            && t
               |> Seq.forall (fun c -> System.Char.IsLower c || System.Char.IsDigit c || c = '-')

        for raw in helpText.Split '\n' do
            let line = raw.TrimEnd()

            if isCommandHeading line then
                inSection <- true
                indent <- -1
            elif line.Length > 0 && not (System.Char.IsWhiteSpace line[0]) then
                inSection <- false
            elif inSection && line.Trim() <> "" then
                let ind = line.Length - line.TrimStart().Length

                if indent < 0 then
                    indent <- ind

                if ind = indent then
                    let body = line.TrimStart()

                    if
                        body.Contains ", "
                        && body.Split(',') |> Array.forall (fun p -> word (p.Trim()) || p.Trim() = "")
                    then
                        for p in body.Split ',' do
                            let w = p.Trim()

                            if word w && not (subs.Contains w) then
                                subs.Add w
                    else
                        let m =
                            System.Text.RegularExpressions.Regex.Match(
                                body,
                                "^([a-z][a-z0-9-]*)\\*?:?(?:$|  |: | \\[| <| \\{| [A-Z][A-Z0-9_.]*(?:\\.\\.\\.)?(?: |$))"
                            )

                        if m.Success then
                            let w = m.Groups[1].Value

                            if word w && not (subs.Contains w) then
                                subs.Add w

        List.ofSeq subs

    // the usage ends the tool's own options at the first operand
    // [D:sig-operand-stop]: `docker run [OPTIONS] IMAGE [COMMAND] [ARG...]`,
    // `python3 [option] ... [-c cmd | -m mod | file | -] [arg] ...`,
    // `node [options] [ script.js ] [arguments]`, `ssh … destination
    // [command [argument ...]]` — an operand followed by the arguments it
    // hands on; a wrapper's `COMMAND...`, `<program> [<argument>...]`, a
    // usage ending at its command after its options (`stdbuf [OPTION]...
    // COMMAND` — bat's bare `bat <COMMAND>` is a subcommand slot), bash's
    // `script-file ...`; sudo's `[command [arg ...]]`, systemd-run's
    // `COMMAND [ARGUMENTS...]`, chrt's `<priority> <command> [<arg>...]`,
    // strace's `PROG [ARGS]`, a usage ending at an optional `[COMMAND]`
    // after its options (uvx; a tool with a commands table never stops at
    // the top), bunx's `[flags and arguments for the package]`. Every usage
    // line counts (flock, bash, sudo list several); a help with none reads
    // its synopsis lines, `<tool> [`/`<tool> <`/`<tool> COMMAND`
    // (systemd-run, chrt) [D:sig-help-shapes]
    let private stopsAtOperand (toolName: string) (helpText: string) =
        let lines = helpText.Split '\n'

        let usageAt (i: int) =
            let l = lines[i]

            let after =
                l.Substring(l.ToLowerInvariant().IndexOf "usage" + 5).TrimStart(':', ' ', '\t')

            let more =
                lines
                |> Array.skip (i + 1)
                |> Array.takeWhile (fun l ->
                    l.Trim() <> ""
                    && not (l.TrimStart().ToLowerInvariant().StartsWith "usage")
                    && (System.Char.IsWhiteSpace l[0] || l.TrimStart().StartsWith "or:"))
                |> List.ofArray

            (if after.Trim() = "" then [] else [ after ]) @ more

        let usage =
            match
                lines
                |> Array.indexed
                |> Array.filter (fun (_, l) -> l.TrimStart().ToLowerInvariant().StartsWith "usage")
                |> Array.map fst
                |> List.ofArray
            with
            | [] ->
                let synopsis =
                    "^" + System.Text.RegularExpressions.Regex.Escape toolName + " +(\\[|<|[A-Z])"

                lines
                |> Array.filter (fun l -> System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), synopsis))
                |> Array.map (fun l -> l.Trim())
                |> List.ofArray
            | starts -> starts |> List.collect usageAt

        usage
        |> List.exists (fun u ->
            System.Text.RegularExpressions.Regex.IsMatch(
                u.TrimEnd(),
                "(?i)(\\b(command|program|cmd|prog)>?\\]?\\s+\\[?<?[\\w-]*arg(s|uments?)?\\b|\\[arg\\]\\s*\\.\\.\\.|\\[arguments\\]|\\[args\\.\\.\\.\\]|\\bcommand\\.\\.\\.|\\]\\S*\\s+\\[?<?\\b(command|program)>?\\]*$|\\bscript(-file)?\\s*(\\.\\.\\.|\\[)|\\[(flags|options) and arg(s|uments)\\b)"
            ))

    // a listed switch `--no-x` has its `--x` (node's `--no-deprecation`:
    // `--deprecation` is valid), a listed `--x=BOOL` its `--no-x`
    // (systemctl's `--legend=BOOL`: `--no-legend`); a tool that lacks it
    // costs a missed typo, never a false warning [D:sig-help-shapes]
    let private withPositives (fs: Flag list) : Flag list =
        let have = fs |> List.map (fun f -> f.Long) |> Set.ofList

        let positives =
            fs
            |> List.choose (fun f ->
                if
                    f.Long.StartsWith "no-"
                    && f.Long.Length > 3
                    && (f.Arity = None || f.Arity = Some ANone)
                    && not (Set.contains (f.Long.Substring 3) have)
                then
                    Some {
                        noFlag with
                            Long = f.Long.Substring 3
                            Arity = Some ANone
                    }
                elif
                    f.BoolValue
                    && f.Long <> ""
                    && not (f.Long.StartsWith "no-")
                    && not (Set.contains ("no-" + f.Long) have)
                then
                    Some {
                        noFlag with
                            Long = "no-" + f.Long
                            Arity = Some ANone
                    }
                else
                    None)

        mergeFlags [ fs; positives ]

    // ---- the walk -------------------------------------------------------

    // one walked case: its path words, own flags, and whether its first
    // operand ends the tool's options
    type private Node = {
        Path: string list
        Flags: Flag list
        Stop: bool
        // other spellings of the same command (cobra's Aliases:
        // `eval-all, ea`; `docker container run, docker run`) — recorded
        // as cases of the same record, never probed
        Aliases: string list list
    }

    let private aliasesOf (toolName: string) (path: string list) (page: string) : string list list =
        let lines = page.Split '\n'

        match
            lines
            |> Array.tryFindIndex (fun l -> let t = l.Trim() in t = "Aliases:" || t = "ALIASES")
        with
        | None -> []
        | Some i ->
            lines
            |> Array.skip (i + 1)
            |> Array.takeWhile (fun l -> l.StartsWith " " && l.Trim() <> "")
            |> Array.collect (fun l -> l.Split ',')
            |> Array.map (fun e -> e.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
            |> Array.choose (fun ws ->
                match ws with
                | [ w ] -> Some((List.truncate (path.Length - 1) path) @ [ w ])
                | t :: rest when t = toolName && not rest.IsEmpty -> Some rest
                | _ -> None)
            |> Array.filter (fun a ->
                a <> path
                && a |> List.forall (fun w -> w.Length > 0 && System.Char.IsLetter w[0]))
            |> Array.distinct
            |> List.ofArray

    // depth-first per top-level command, each with its own budget, so a
    // tool with forty commands still reaches `gh pr create` and `docker
    // compose up` [D:sig-sources]. A page identical to its parent's is the
    // tool ignoring the word (systemctl, whose verbs share one help): no
    // case, no descent. `flagsAt` reads one node's flags from its page.
    let private walkTree
        (toolName: string)
        (topHelp: string)
        (probeHelp: string list -> string option)
        (flagsAt: string list -> string -> Flag list)
        : Node list * int =
        // help/completion are listed everywhere and walk nowhere
        let noise k = k = "help" || k = "completion"

        // one top-level command's subtree, depth-first, on its own budget
        let walkTop (top: string) : Node list * int =
            let nodes = ResizeArray<Node>()
            let budget = ref 40
            let probed = ref 0

            let rec visit (path: string list) (parentPage: string) =
                if budget.Value > 0 && path.Length <= 4 then
                    budget.Value <- budget.Value - 1
                    probed.Value <- probed.Value + 1

                    match probeHelp path with
                    | Some page when page.Trim() <> parentPage.Trim() && page.Trim() <> topHelp.Trim() ->
                        let kids =
                            commandTokens page
                            |> List.filter (fun k -> not (noise k) && not (List.contains k path))

                        nodes.Add {
                            Path = path
                            Flags = flagsAt path page
                            Stop = kids.IsEmpty && stopsAtOperand toolName page
                            Aliases = aliasesOf toolName path page
                        }

                        for k in kids do
                            visit (path @ [ k ]) page
                    | _ -> ()

            visit [ top ] topHelp
            List.ofSeq nodes, probed.Value

        // the subtrees are independent: eight at a time (gh and docker
        // spend seconds per hundred probes), joined in the tables' order,
        // so the signature is the same however the probes interleave
        let tops =
            commandTokens topHelp
            |> List.filter (noise >> not)
            |> List.truncate 100
            |> Array.ofList

        let results = Array.create tops.Length ([], 0)

        System.Threading.Tasks.Parallel.For(
            0,
            tops.Length,
            System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 8),
            fun i -> results[i] <- walkTop tops[i]
        )
        |> ignore

        (results |> Array.toList |> List.collect fst), (results |> Array.sumBy snd)

    let private fieldName (long: string) =
        // inverse kebab: dry-run -> dryRun
        long.Split([| '-'; '_' |])
        |> Array.mapi (fun i part ->
            if i = 0 || part = "" then
                part
            else
                string (System.Char.ToUpper part[0]) + part.Substring 1)
        |> String.concat ""

    // a tool's --version and help text are data [D:sig-text-sanitize]:
    // control bytes and escapes render visibly in the generated file
    let private escape (s: string) =
        (Eval.sanitizeTtyEcho (s.Replace("\r", ""))).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")

    // where a tool's own fish completion sits: beside its install prefix
    // (`<prefix>/bin/tool` -> `<prefix>/share/fish/…`), the PATH entry's
    // and the real file's [D:sig-sources] — never another install's
    let private shippedFish (tool: string) : string option =
        let name = IO.Path.GetFileName tool

        let resolved =
            if tool.Contains '/' then
                Some(IO.Path.GetFullPath tool)
            else
                Extern.resolveFile tool

        let real (p: string) =
            try
                match IO.File.ResolveLinkTarget(p, true) with
                | null -> p
                | t -> t.FullName
            with _ ->
                p

        resolved
        |> Option.bind (fun bin ->
            [ bin; real bin ]
            |> List.distinct
            |> List.collect (fun b ->
                match
                    IO.Path.GetDirectoryName b
                    |> Option.ofObj
                    |> Option.bind (IO.Path.GetDirectoryName >> Option.ofObj)
                with
                | Some prefix -> [
                    IO.Path.Combine(prefix, "share", "fish", "vendor_completions.d", name + ".fish")
                    IO.Path.Combine(prefix, "share", "fish", "completions", name + ".fish")
                  ]
                | None -> [])
            |> List.tryFind IO.File.Exists)

    let generateTo (globalDir: string option) (weirDir: string) (tool: string) : Result<string, string> =
        cutProbes.Clear()
        slowProbes.Clear()
        probed.Clear()
        // Flag probes are safe on any tool; a bare word is not — `code
        // completion fish` opens VS Code on two files
        // [D:sig-version-probe]. Bare-word probes (completion fish, the
        // version word, a subcommand's help) run only for words the tool's
        // own --help lists as commands.
        match Contracts.probeVersionFlag Proc.resolveProg tool with
        | Contracts.ToolAbsent ->
            // a file that exists but would not run is a different wrong
            // turn than a missing tool — `add sig rooz/v3/xr.yaml` is a
            // data file, not a binary
            if IO.File.Exists tool then
                Error $"'{tool}' exists but did not run — a signature describes a runnable tool; is this a data file?"
            else
                Error $"'{tool}' is not on PATH — generation probes the installed binary"
        | rung1 ->
            let topHelp = runTool tool [ "--help" ]

            // exit-0 help only gates and walks; a refused --help still
            // yields its usage dump to the harvest below, and a tool that
            // answers `-h` or `-help` instead gets those rows
            let harvestOnly =
                match topHelp with
                | Some _ -> None
                | None -> runToolAnyExit tool [ "--help" ]

            let topHelp =
                topHelp
                |> Option.orElseWith (fun () ->
                    [ [ "-h" ]; [ "-help" ] ]
                    |> List.tryPick (fun a -> runTool tool a |> Option.filter (fun t -> (parseHelp t).Length > 0)))

            let advertised = topHelp |> Option.map commandTokens |> Option.defaultValue []

            // a tool that refuses --version has no recorded identity
            // [D:sig-version-probe] — the refusal's usage dump is not a
            // version, and recording it leaks paths into the sig
            let version =
                match rung1 with
                | Contracts.ToolVersion raw -> Some(raw.Trim())
                | _ when List.contains "version" advertised ->
                    match Contracts.probeVersionWord Proc.resolveProg tool with
                    | Contracts.ToolVersion raw -> Some(raw.Trim())
                    | _ -> None
                | _ -> None

            let fishText =
                if List.contains "completion" advertised then
                    runTool tool [ "completion"; "fish" ]
                else
                    None

            let help = topHelp |> Option.defaultValue ""
            let helpRows = parseHelp help

            // the cobra endpoint: its fish script — printed, or the one it
            // ships — calls `<tool> __complete`
            let cobra =
                match fishText with
                | Some t -> t.Contains "__complete"
                | None ->
                    shippedFish tool
                    |> Option.exists (fun f ->
                        try
                            (IO.File.ReadAllText f).Contains "__complete"
                        with _ ->
                            false)

            let isGit = version |> Option.exists (fun v -> v.StartsWith "git version ")

            // the full-help switches a help page names [D:sig-sources]
            // (GnuPG's tools answer --dump-options without naming it)
            let gnupg = version |> Option.exists (fun v -> v.Contains "(GnuPG)")

            let fullHelp =
                [
                    "--help all", [ "--help"; "all" ], parseHelp
                    "--help-all", [ "--help-all" ], parseHelp
                    "--dump-options", [ "--dump-options" ], parseDump
                    "--v8-options", [ "--v8-options" ], parseV8
                ]
                |> List.filter (fun (mention, _, _) -> help.Contains mention || (gnupg && mention = "--dump-options"))
                |> List.choose (fun (mention, argv, parse) ->
                    runTool tool argv |> Option.map (fun t -> mention, parse t))

            // the convention [D:sig-conventions]: find's predicates (lines
            // listing several single-dash words), Go's flag package (rows
            // that are single-dash words, one per line, no `--long` rows)
            let isGoRow (l: string) =
                System.Text.RegularExpressions.Regex.IsMatch(l, "^\\s{0,4}-[A-Za-z][\\w.-]*( [A-Za-z][\\w.]*)?\\s*$")

            let goRows = help.Split '\n' |> Array.filter isGoRow |> Array.length

            let predicateLines =
                help.Split '\n'
                |> Array.filter (fun l ->
                    l.StartsWith " "
                    && not (isGoRow l)
                    && (l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.filter (fun t -> t.Length > 2 && t[0] = '-' && System.Char.IsLower t[1])
                        |> Array.length)
                       >= 2)
                |> Array.length

            let longRows = helpRows |> List.filter (fun f -> f.Long <> "") |> List.length

            let convention =
                if isGit || cobra then "gnu"
                elif goRows >= 2 && longRows <= 2 then "go"
                elif predicateLines >= 3 && longRows <= 3 then "find"
                else "gnu"

            // a Go row is a long under another dash count: one spelling per
            // flag, single-letter ones too
            let goFlags (fs: Flag list) =
                fs
                |> List.map (fun f ->
                    let words =
                        (f.Literals |> List.map (fun l -> l.TrimStart '-'))
                        @ f.Shorts
                        @ (if f.Long <> "" then [ f.Long ] else [])

                    {
                        f with
                            Long = ""
                            Shorts = []
                            Literals = words |> List.distinct |> List.map (fun w -> "-" + w)
                    })

            let gitCwd () =
                // git's helper needs a repository: an empty one in a temp
                // dir, discarded after the probes
                let d =
                    IO.Path.Combine(IO.Path.GetTempPath(), $"weir-siggit-{System.Guid.NewGuid():N}")

                Contracts.ProbeGuard.trackPath d
                IO.Directory.CreateDirectory(IO.Path.Combine(d, ".git", "objects")) |> ignore
                IO.Directory.CreateDirectory(IO.Path.Combine(d, ".git", "refs")) |> ignore
                IO.File.WriteAllText(IO.Path.Combine(d, ".git", "HEAD"), "ref: refs/heads/main\n")
                d

            let walkNoteOf (nodes: Node list) (probed: int) =
                if nodes.IsEmpty && probed > 0 then
                    $" (walked {probed} subcommand help(s), none yielded a case)"
                else
                    ""

            let source, flags, nodes, unchecked, walkNote =
                if isGit then
                    // git [D:sig-sources]: its own builtin list; per builtin
                    // the completion helper (longs, `=` a value) and `-h`
                    // (shorts and arities). A builtin that passes an
                    // unknown option on (log, show: revision options
                    // outside parse-options) cannot be listed — unchecked
                    let d = gitCwd ()

                    try
                        let run args = runIn (Some d) false tool args
                        let runAny args = runIn (Some d) true tool args

                        let builtins =
                            run [ "--list-cmds=builtins" ]
                            |> Option.map (fun t ->
                                t.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                |> Array.map (fun s -> s.Trim())
                                |> List.ofArray)
                            |> Option.defaultValue []
                            |> List.filter (fun c ->
                                c.Length > 1
                                && System.Char.IsLower c[0]
                                && c
                                   |> Seq.forall (fun ch ->
                                       System.Char.IsLower ch || System.Char.IsDigit ch || ch = '-'))
                            |> List.truncate 200

                        let rows (t: string option) =
                            t
                            |> Option.map (fun t -> mergeFlags [ parseHelp t; parseUsage t ])
                            |> Option.defaultValue []

                        let mutable opened = []

                        // one parse-options command: unchecked when it passes
                        // an unknown option on, else its helper's longs and
                        // its -h's shorts
                        let probeCmd (parentHelper: string option) (path: string list) : Node option =
                            let case (flags: Flag list) : Node option =
                                Some {
                                    Path = path
                                    Flags = flags
                                    Stop = false
                                    Aliases = []
                                }

                            match run (path @ [ "--weir-probe-unknown"; "--git-completion-helper-all" ]) with
                            | Some _ ->
                                opened <- String.concat " " path :: opened
                                case []
                            | None ->
                                // `-all` lists the hidden options too (--allow-empty)
                                // a sub whose helper is its parent's is the
                                // parent ignoring the word (`rebase options`)
                                match run (path @ [ "--git-completion-helper-all" ]) with
                                | Some h when (parseGitHelper h).Length > 0 && Some h <> parentHelper ->
                                    let dashText = runAny (path @ [ "-h" ])

                                    // its usage hands diff or log options on
                                    // (`git stash show [<diff-options>]`): no
                                    // list holds them [D:sig-unwalked]
                                    let passesOn =
                                        let at =
                                            path
                                            |> List.map System.Text.RegularExpressions.Regex.Escape
                                            |> String.concat "\\s+"

                                        (dashText |> Option.defaultValue "").Split '\n'
                                        |> Array.exists (fun l ->
                                            let m =
                                                System.Text.RegularExpressions.Regex.Match(
                                                    l,
                                                    "^\\s*(?:usage|or):\\s+git\\s+" + at + "(\\s.*)?$"
                                                )

                                            m.Success
                                            && not (
                                                System.Text.RegularExpressions.Regex.IsMatch(
                                                    m.Groups[1].Value,
                                                    "^\\s+[a-z][a-z0-9-]*(\\s|$)"
                                                )
                                            )
                                            && System.Text.RegularExpressions.Regex.IsMatch(
                                                m.Groups[1].Value,
                                                "<[a-z-]*(diff|log)-options>"
                                            ))

                                    let dashH = rows dashText

                                    let byLong =
                                        dashH
                                        |> List.filter (fun f -> f.Long <> "")
                                        |> List.map (fun f -> f.Long, f)
                                        |> Map.ofList

                                    let helper =
                                        parseGitHelper h
                                        |> List.map (fun f ->
                                            match Map.tryFind f.Long byLong with
                                            | Some r -> {
                                                f with
                                                    Shorts = r.Shorts
                                                    Doc = r.Doc
                                                    Arity =
                                                        (if f.Arity = Some AOne then
                                                             Some AOne
                                                         else
                                                             r.Arity |> Option.orElse f.Arity)
                                              }
                                            | None -> f)

                                    if passesOn then
                                        opened <- String.concat " " path :: opened
                                        case []
                                    else
                                        case (mergeFlags [ helper; dashH ])
                                | _ -> None

                        // a builtin's own subcommands (remote add, notes add,
                        // stash push): the words its usage lines name right
                        // after `git <c>` and its bracketed options; only
                        // those whose helper answers become cases
                        // [D:sig-help-shapes]
                        let subsOf (c: string) : string list =
                            match runAny [ c; "-h" ] with
                            | None -> []
                            | Some t ->
                                [
                                    for l in t.Split '\n' do
                                        let m =
                                            System.Text.RegularExpressions.Regex.Match(
                                                l,
                                                "^\\s*(?:usage|or):\\s+git "
                                                + System.Text.RegularExpressions.Regex.Escape c
                                                + "((?:\\s+\\[(?:[^\\[\\]]|\\[[^\\[\\]]*\\])*\\])*)\\s+([a-z][a-z0-9-]*)\\b(?![=>])"
                                            )

                                        if m.Success then
                                            yield m.Groups[2].Value

                                        // an optional one: `git stash [push [-p] …`
                                        let o =
                                            System.Text.RegularExpressions.Regex.Match(
                                                l,
                                                "^\\s*(?:usage|or):\\s+git "
                                                + System.Text.RegularExpressions.Regex.Escape c
                                                + "\\s+\\[([a-z][a-z0-9-]*)\\b(?![=>])"
                                            )

                                        if o.Success then
                                            yield o.Groups[1].Value
                                ]
                                |> List.distinct
                                |> List.truncate 30

                        let nodes =
                            builtins
                            |> List.collect (fun c ->
                                let own = probeCmd None [ c ]

                                let subs =
                                    match own with
                                    | Some _ when List.contains c opened -> []
                                    | _ ->
                                        let parent = run [ c; "--git-completion-helper-all" ]

                                        subsOf c
                                        |> List.choose (fun sub ->
                                            probeCmd (Some(parent |> Option.defaultValue "")) [ c; sub ])

                                // a builtin whose helper lists only its
                                // subcommands (config, stash, worktree) still
                                // gets a case [D:sig-help-shapes]: its own flags
                                // are its subcommands' and, for an older form
                                // (`git config --global k v`), the table its
                                // `-h` prints after one of them
                                let parent =
                                    match own with
                                    | Some _ -> None
                                    | None ->
                                        let union = subs |> List.map (fun n -> n.Flags) |> mergeFlags

                                        union
                                        |> List.tryFind (fun f ->
                                            f.Long <> "" && (f.Arity = None || f.Arity = Some ANone))
                                        |> Option.map (fun first ->
                                            let older = rows (runAny [ c; "--" + first.Long; "-h" ])

                                            {
                                                Path = [ c ]
                                                Flags = mergeFlags [ union; older; rows (runAny [ c; "-h" ]) ]
                                                Stop = false
                                                Aliases = []
                                            })

                                Option.toList own @ Option.toList parent @ subs)

                        let globals = mergeFlags [ parseUsage help; helpRows ]
                        "git-helper", globals, nodes, List.rev opened, ""
                    finally
                        try
                            IO.Directory.Delete(d, true)
                        with _ ->
                            ()

                        Contracts.ProbeGuard.untrackPath d
                elif cobra then
                    // cobra [D:sig-sources]: per command, `__complete <path> -`
                    // lists its flags with shorts (plugins answer too), the
                    // help page its arities and its subcommands
                    let complete path =
                        runTool tool ("__complete" :: path @ [ "-" ])
                        |> Option.map parseCobraComplete
                        |> Option.defaultValue []

                    let arityFrom (rows: Flag list) (fs: Flag list) =
                        let byKey = rows |> List.map (fun f -> keyOf f, f) |> Map.ofList

                        fs
                        |> List.map (fun f ->
                            match Map.tryFind (keyOf f) byKey with
                            | Some r -> {
                                f with
                                    Arity = f.Arity |> Option.orElse r.Arity
                                    Doc = f.Doc |> Option.orElse r.Doc
                              }
                            | None -> f)

                    let flagsAt path page =
                        let rows = parseHelp page
                        mergeFlags [ arityFrom rows (complete path); rows ]

                    let nodes, probed =
                        walkTree (IO.Path.GetFileName tool) help (fun p -> runTool tool (p @ [ "--help" ])) flagsAt

                    let root = mergeFlags [ arityFrom helpRows (complete []); helpRows ]

                    "cobra-complete", root, nodes, [], walkNoteOf nodes probed
                else
                    let fish =
                        match fishText with
                        | Some t when t.Contains "complete " ->
                            match parseFish t (IO.Path.GetFileName tool) with
                            | [] -> None
                            | fs -> Some("completion-fish", fs)
                        | _ ->
                            shippedFish tool
                            |> Option.bind (fun f ->
                                match parseFish (IO.File.ReadAllText f) (IO.Path.GetFileName tool) with
                                | [] -> None
                                | fs -> Some("fish-file", fs))

                    let full = fullHelp |> List.collect snd

                    let source, top =
                        match fish, fullHelp with
                        | _, (mention, _) :: _ when not full.IsEmpty ->
                            // `--help all` and `--help-all` are one source
                            let label = mention.TrimStart('-').Replace(" ", "-")
                            (if label = "help-all" then label else "help+" + label), mergeFlags [ full; helpRows ]
                        | Some(label, fs), _ -> label, mergeFlags [ fs; helpRows ]
                        | None, _ ->
                            match topHelp, harvestOnly with
                            | Some _, _ when not helpRows.IsEmpty -> "help", helpRows
                            | Some t, _ ->
                                "help-scan",
                                (match parseUsage t with
                                 | [] -> harvestHelp t
                                 | fs -> fs)
                            | None, Some dump ->
                                match harvestHelp dump with
                                | [] -> "help", []
                                | fs -> "help-scan", fs
                            | None, None -> "help", []

                    let top =
                        match convention with
                        | "go" ->
                            // a Go tool's help may show a flag only in its
                            // examples (mkcert's `$ mkcert -install`)
                            let prompt = "$ " + IO.Path.GetFileName tool + " "

                            let examples =
                                help.Split '\n'
                                |> Array.map (fun l -> l.Trim())
                                |> Array.filter (fun l -> l.StartsWith prompt)
                                |> Array.collect (fun l ->
                                    let piped = (l.Substring prompt.Length).Split '|'
                                    piped[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                |> Array.filter (fun w ->
                                    System.Text.RegularExpressions.Regex.IsMatch(w, "^-[A-Za-z][\\w-]*$"))
                                |> Array.distinct
                                |> Array.map (fun w -> {
                                    noFlag with
                                        Literals = [ w ]
                                        Arity = Some ANone
                                })
                                |> List.ofArray

                            goFlags (mergeFlags [ top; examples ])
                        | "find" ->
                            // the usage's options: `-L` whole, `-Olevel` a
                            // letter with its value glued
                            let usage =
                                parseUsage help
                                |> List.map (fun f ->
                                    let glued, whole =
                                        f.Literals
                                        |> List.partition (fun l ->
                                            System.Text.RegularExpressions.Regex.IsMatch(l, "^-[A-Z][a-z]+$"))

                                    {
                                        f with
                                            Literals =
                                                whole
                                                @ (f.Shorts |> List.map (fun s -> "-" + s))
                                                @ (glued |> List.map (fun l -> l.Substring(0, 2)))
                                            Shorts = []
                                            Arity = (if glued.IsEmpty then f.Arity else Some AOpt)
                                    })

                            mergeFlags [ parsePredicates help; usage; top ]
                        | _ -> withPositives top

                    // a refused --help still lists its commands (npm exits 1
                    // on a full page): the walk reads a recognized command
                    // table from either [D:sig-sources]
                    match topHelp |> Option.orElse harvestOnly with
                    // an open surface [D:sig-open-tool]: the help says any
                    // config key is a flag (npm's `npm <command>
                    // --key=value`) — no list can be complete, so the tool
                    // is recorded unchecked and its commands are not walked
                    | Some t when
                        t.Contains "--key=value"
                        || System.Text.RegularExpressions.Regex.IsMatch(t, "(?m)^\\s*npm [a-z]")
                        ->
                        // npx's help is `npm exec …`: npm's config keys too
                        source, top, [], [ "" ], ""
                    | Some t when convention = "gnu" ->
                        // a sub page's rows: its own help, or its usage brackets (npm)
                        let flagsAt _ page =
                            match parseHelp page with
                            | [] -> parseUsage page
                            | fs -> withPositives (mergeFlags [ fs; parseUsage page ])

                        let nodes, probed =
                            walkTree (IO.Path.GetFileName tool) t (fun p -> runTool tool (p @ [ "--help" ])) flagsAt

                        source, top, nodes, [], walkNoteOf nodes probed
                    | _ -> source, top, [], [], ""

            // GNU getopt's unique-prefix longs [D:sig-conventions]: the
            // tool answers `--versio` exactly as `--version`. Asked only of
            // a help that spells values getopt_long's way (`--file=ARCHIVE`)
            // — a tool that launches on an unknown flag (an editor) is
            // never handed one
            let abbreviations =
                convention = "gnu"
                && not cobra
                && not isGit
                && System.Text.RegularExpressions.Regex.IsMatch(help, "--[a-z][a-z0-9-]*=[A-Z<\\[]")
                && (version
                    |> Option.exists (fun v ->
                        runTool tool [ "--versio" ]
                        |> Option.exists (fun out ->
                            let first =
                                out.Split('\n')
                                |> Array.tryFind (fun l -> l.Trim() <> "")
                                |> Option.defaultValue ""

                            System.Text.RegularExpressions.Regex.Replace(first.Trim(), "\\s+", " ") = v)))

            // V8 documents `--no-flag` for every bool flag, node for its own
            let negatable = fullHelp |> List.exists (fun (m, _) -> m = "--v8-options")

            // a row spelled `-<signal>` (kill): any dash word is a value
            let dashWords =
                convention = "gnu"
                && System.Text.RegularExpressions.Regex.IsMatch(help, "(?m)^\\s*-<[A-Za-z][\\w-]*>")

            // a refused help still states its usage (bunx exits 1 on --help)
            let topStop =
                advertised.IsEmpty
                && stopsAtOperand
                    (IO.Path.GetFileName tool)
                    (topHelp |> Option.orElse harvestOnly |> Option.defaultValue "")

            match flags, nodes with
            | [], [] ->
                // the file #sig reads, as it names it [D:sig-probe-bounds]
                let slow =
                    match slowProbes |> Seq.distinct |> List.ofSeq with
                    | [] -> ""
                    | ps ->
                        let named = ps |> List.map (fun a -> $"'{tool} {a}'") |> String.concat ", "
                        $"; stopped at {Contracts.probeTimeoutMs / 1000} s: {named}"

                Error
                    $"'{tool}': found no flags to record (probed: completion fish, shipped fish files, --help{slow}) — write .weir/sigs/{sigFileName tool} by hand"
            | flags, nodes ->
                // a path-y tool name must still mint a legal module
                // name: letters and digits only, letter-first (an
                // absolute path would otherwise mint `module /Users…`)
                let moduleName =
                    let core = tool |> String.filter System.Char.IsLetterOrDigit

                    if core = "" then "Sig"
                    elif System.Char.IsDigit core[0] then "Sig" + core
                    else string (System.Char.ToUpper core[0]) + core.Substring 1

                let sb = System.Text.StringBuilder()
                let line (l: string) = sb.AppendLine l |> ignore

                // one record body [D:sig-arity]: a field per spelling group.
                // The long names the field (Wire when its camel cannot
                // round-trip, Flag suffix on a keyword), the first short
                // rides [<Short>]; every further spelling — a second
                // short, `-h`, a short-only row, a Go or find word — is its
                // own field with a dash Wire. Shorts and words dedup within
                // the record (per case — docker's -a on all and all-tags
                // live apart); the arity is the field's type.
                let renderRecord (name: string) (flags: Flag list) =
                    line $"type {name} = {{"
                    let names = System.Collections.Generic.HashSet<string>()
                    let taken = System.Collections.Generic.HashSet<string>()

                    let uniq (n: string) =
                        let n =
                            if Set.contains n Weir.Parser.keywords then
                                n + "Flag"
                            else
                                n

                        let mutable c = n
                        let mutable i = 2

                        while not (names.Add c) do
                            c <- $"{n}{i}"
                            i <- i + 1

                        c

                    let ty =
                        function
                        | Some AOne -> "string"
                        | Some AOpt -> "Option<string>"
                        | Some ARest -> "seq<string>"
                        | _ -> "bool"

                    let field (doc: string option) (attrs: string list) (fname: string) (arity: SigArity option) =
                        match doc with
                        | Some d -> line $"    /// {escape d}"
                        | None -> ()

                        if not attrs.IsEmpty then
                            let joined = String.concat "; " attrs
                            line $"    [<{joined}>]"

                        line $"    {fname}: {ty arity}"

                    let spellingName (sp: string) =
                        let bare = sp.TrimStart '-'

                        if bare.Length = 1 then
                            if System.Char.IsLetterOrDigit bare[0] then
                                "short" + bare
                            else
                                $"shortX{int bare[0]:X}"
                        else
                            let camel = fieldName bare

                            if camel <> "" && System.Char.IsLetter camel[0] then
                                camel
                            else
                                "dashWord"

                    // same-camel spellings share a field (claude's
                    // --allowedTools and --allowed-tools): the kebab one is
                    // the field's own, the other rides its one Wire
                    let twins =
                        flags
                        |> List.filter (fun f -> f.Long <> "")
                        |> List.groupBy (fun f -> fieldName f.Long)
                        |> List.choose (fun (camel, g) ->
                            match g with
                            | [ _; _ ] when not (Set.contains camel Weir.Parser.keywords) ->
                                match g |> List.partition (fun f -> f.Long = Weir.Argv.kebabFlag camel) with
                                | [ own ], [ other ] -> Some(own.Long, other)
                                | _ -> None
                            | _ -> None)
                        |> Map.ofList

                    let twinned = twins |> Map.values |> Seq.map (fun f -> f.Long) |> Set.ofSeq

                    let ordered =
                        flags
                        |> List.filter (fun f -> not (Set.contains f.Long twinned))
                        |> List.sortBy (fun f ->
                            (if f.Long <> "" then f.Long else keyOf f).TrimStart('-').ToLowerInvariant(), keyOf f)

                    for f in ordered do
                        let twin = Map.tryFind f.Long twins

                        let f =
                            match twin with
                            | Some t -> {
                                f with
                                    Shorts = f.Shorts @ t.Shorts
                                    Literals = f.Literals @ t.Literals
                                    Doc = f.Doc |> Option.orElse t.Doc
                              }
                            | None -> f

                        let shorts = f.Shorts |> List.filter (fun s -> taken.Add("-" + s))
                        let words = f.Literals |> List.filter (fun w -> taken.Add w)

                        let extra =
                            if f.Long <> "" && taken.Add("--" + f.Long) then
                                let camel = fieldName f.Long

                                let fname =
                                    uniq (
                                        if camel <> "" && System.Char.IsLetter camel[0] then
                                            camel
                                        else
                                            "flag" + camel
                                    )

                                let covered = Weir.Argv.kebabFlag fname

                                let attrs =
                                    match twin with
                                    | Some t when taken.Add("--" + t.Long) -> [ $"Wire \"{escape t.Long}\"" ]
                                    | _ ->
                                        if covered <> f.Long then
                                            [ $"Wire \"{escape f.Long}\"" ]
                                        else
                                            []

                                match shorts with
                                | s :: rest when s <> "h" && s.Length = 1 && System.Char.IsLetterOrDigit s[0] ->
                                    field f.Doc (attrs @ [ $"Short \"{s}\"" ]) fname f.Arity
                                    rest
                                | _ ->
                                    field f.Doc attrs fname f.Arity
                                    shorts
                            else
                                shorts

                        let docOnce = if f.Long = "" then f.Doc else None

                        for s in extra do
                            field docOnce [ $"Wire \"-{escape s}\"" ] (uniq (spellingName s)) f.Arity

                        for w in words do
                            field docOnce [ $"Wire \"{escape w}\"" ] (uniq (spellingName w)) f.Arity

                    line "}"

                // a case name from a sub path ("issue list" ->
                // IssueList): PascalCase of the camel of the kebab-joined
                // tokens — kebabFlag round-trips it to the key the
                // checker builds from the line's own words
                let caseName (path: string) =
                    let camel = fieldName (path.Replace(" ", "-"))
                    let pascal = string (System.Char.ToUpper camel[0]) + camel.Substring 1

                    if pascal = "Cmd" || pascal = "Globals" then
                        pascal + "Cmd"
                    else
                        pascal

                line $"module {moduleName}"

                line (
                    "/// generated from '"
                    + (Eval.sanitizeTtyEcho tool).Replace("\n", "\\x0a")
                    + "' on "
                    + System.DateTime.UtcNow.ToString "yyyy-MM-dd"
                )

                let fullSource = source + (if nodes.IsEmpty then "" else "+subs") + walkNote
                line $"/// source: {fullSource} — a scraped surface may be incomplete (partial"
                line "/// by default; add `let exhaustive = true` once verified by hand)"

                // a probe whose output passed the byte bound was read in
                // part [D:sig-probe-bounds]
                let cutNote =
                    match cutProbes |> Seq.distinct |> List.ofSeq with
                    | [] -> ""
                    | cuts ->
                        let mib = Contracts.probeMaxBytes / (1024 * 1024)
                        let named = cuts |> List.map (fun a -> $"'{tool} {a}'") |> String.concat ", "
                        $"output cut at {mib} MiB per stream: {named}"

                if cutNote <> "" then
                    line $"/// {escape cutNote} — the rest was not read"

                // a probe stopped at the time bound was read up to the stop
                // [D:sig-probe-bounds]
                let slowNote =
                    match slowProbes |> Seq.distinct |> List.ofSeq with
                    | [] -> ""
                    | slow ->
                        let secs = Contracts.probeTimeoutMs / 1000
                        let named = slow |> List.map (fun a -> $"'{tool} {a}'") |> String.concat ", "
                        $"stopped at {secs} s: {named}"

                if slowNote <> "" then
                    line $"/// {escape slowNote} — what it printed by then was read"

                if nodes.IsEmpty then
                    line "/// flat surface: flags checked across the whole line — split into"
                    line "/// subcommand records by hand if the tool warrants it"
                else
                    line "/// scoped surface [D:scoped-sigs]: the LONGEST matching subcommand"
                    line "/// path picks the case (issue list beats issue); a case checks its"
                    line "/// own flags and the Globals; an unlisted subcommand is not checked"

                match version with
                | Some v -> line $"let version = \"{escape v}\""
                | None ->
                    line
                        "// no `let version`: the tool answers neither --version nor the version subcommand, so there is no identity to record"

                // the REPL's Tab may ask the tool for values
                // [D:sig-dynamic-complete]; the line is the user's to drop
                match source with
                | "cobra-complete" ->
                    line "/// the REPL's Tab asks the tool for values (`__complete`); honored from"
                    line "/// the user sig dir only (weir add sig --global) — delete to turn off"
                    line "let dynamic = \"cobra\""
                | "git-helper" ->
                    line "/// the REPL's Tab lists refs and remotes (weir's own read-only git"
                    line "/// commands) — delete to turn off"
                    line "let dynamic = \"git\""
                | _ -> ()

                // the convention lets [D:sig-conventions]
                if convention <> "gnu" then
                    line $"let convention = \"{convention}\""

                if abbreviations then
                    line "/// the tool takes a unique prefix of a long flag (`--verb` for `--verbose`)"
                    line "let abbreviations = true"

                if negatable then
                    line "/// `--no-x` turns off any switch `--x`"
                    line "let negatable = true"

                if dashWords then
                    line "/// a dash word no short spells is a value (the help's `-<signal>`)"
                    line "let dashWords = true"

                // the marker that this is no older all-bool signature
                // [D:sig-help-shapes]: its bool fields were read as switches
                if convention = "gnu" then
                    line "/// a multi-letter single-dash word is a bundle of shorts (`-xzf`)"
                    line "let bundles = true"

                let caseKey (path: string list) = String.concat " " path

                let quoted (xs: string list) =
                    xs |> List.map (fun s -> $"\"{escape s}\"") |> String.concat "; "

                let stops =
                    [
                        if topStop && nodes.IsEmpty then
                            yield ""
                        for n in nodes do
                            if n.Stop then
                                yield caseKey n.Path

                                for a in n.Aliases do
                                    yield caseKey a
                    ]
                    // an alias can repeat a case or another alias
                    |> List.distinct

                if not stops.IsEmpty then
                    line "/// the first operand of these ends the tool's own options (what follows is"
                    line "/// handed on — a container's command, a script's arguments)"
                    line $"let stopAtOperand = [ {quoted stops} ]"

                if unchecked = [ "" ] then
                    line "/// the tool takes any config key as a flag (its help: `--key=value`):"
                    line "/// no flag on its lines is checked [D:sig-open-tool]"
                    line "let unchecked = [ \"\" ]"
                elif not unchecked.IsEmpty then
                    line "/// these pass options the source cannot list (git's revision options):"
                    line "/// reaching one ends the check"
                    line $"let unchecked = [ {quoted unchecked} ]"

                if nodes.IsEmpty then
                    renderRecord "Cmd" flags
                else
                    // globals once [D:sig-sources]: the top level's flags
                    // are checked at every level, so no case repeats them;
                    // a help-walked case also carries its ancestors' own
                    // flags (`jira issue list` takes issue's), a cobra case
                    // lists its inherited ones itself
                    let globalKeys = flags |> List.map keyOf |> Set.ofList
                    let byPath = nodes |> List.map (fun n -> n.Path, n.Flags) |> Map.ofList

                    let walked = nodes |> List.map (fun n -> caseKey n.Path) |> Set.ofList

                    let cases =
                        nodes
                        |> List.distinctBy (fun n -> caseName (caseKey n.Path))
                        |> List.map (fun n ->
                            let ancestors =
                                if cobra || isGit then
                                    []
                                else
                                    [
                                        for k in n.Path.Length - 1 .. -1 .. 1 do
                                            match Map.tryFind (List.truncate k n.Path) byPath with
                                            | Some fs -> yield! fs
                                            | None -> ()
                                    ]

                            let own =
                                mergeFlags [ n.Flags; ancestors ]
                                |> List.filter (fun f -> not (Set.contains (keyOf f) globalKeys))

                            n, caseName (caseKey n.Path), own)

                    if not flags.IsEmpty then
                        renderRecord "Globals" flags

                    for _, cn, own in cases do
                        if not own.IsEmpty then
                            renderRecord $"{cn}Flags" own

                    line "type Cmd ="

                    let aliasCases =
                        [
                            for n, cn, own in cases do
                                for a in n.Aliases do
                                    if not (Set.contains (caseKey a) walked) then
                                        yield caseName (caseKey a), cn, own
                        ]
                        |> List.distinctBy (fun (an, _, _) -> an)
                        |> List.filter (fun (an, _, _) -> cases |> List.forall (fun (_, cn, _) -> cn <> an))

                    let caseLine (name: string) (cn: string) (own: Flag list) =
                        if own.IsEmpty then
                            line $"    | {name}"
                        else
                            line $"    | {name} of {cn}Flags"

                    for an, cn, own in aliasCases do
                        caseLine an cn own

                    for _, cn, own in cases do
                        // the matching key is the kebab of the case — it
                        // must round-trip to the sub token the user types;
                        // a case with no flags of its own takes the Globals
                        caseLine cn cn own

                let text = sb.ToString()

                // validate before write [D:add-validates]: the generated
                // signature must itself load as one; nothing persists if
                // it does not
                let tmp =
                    IO.Path.Combine(IO.Path.GetTempPath(), $"weir-siggen-{System.Guid.NewGuid():N}.weir")

                Contracts.ProbeGuard.trackPath tmp

                try
                    IO.File.WriteAllText(tmp, text)

                    let probe =
                        // a synthetic importer path — the sig file must
                        // not read as importing itself
                        loadSigs (tmp + ".from") [
                            {
                                Tool = tool
                                Override = Some tmp
                                Line = 1
                            }
                        ]

                    match probe with
                    | d :: _, _ ->
                        // name the offending line — without it a generator
                        // bug reports blind [D:sig-version-probe]
                        let atLine =
                            match text.Split '\n' |> Array.tryItem (d.Line - 1) with
                            | Some l when l.Trim() <> "" -> $" at line {d.Line}: {l.Trim()}"
                            | _ -> ""

                        Error $"generated signature does not validate: {d.Message}{atLine} — this is a generator bug"
                    | [], _ ->
                        let versionNote =
                            match version with
                            | Some v -> v
                            | None -> $"none — '{tool}' answers neither --version nor version"

                        let surfaceNote =
                            if nodes.IsEmpty then
                                $"{flags.Length} flag(s)"
                            else
                                let total =
                                    (flags @ (nodes |> List.collect (fun n -> n.Flags)))
                                    |> List.map keyOf
                                    |> List.distinct
                                    |> List.length

                                $"{total} flag(s) across {nodes.Length} subcommand case(s)"

                        let cutTail =
                            [ cutNote; slowNote ]
                            |> List.filter ((<>) "")
                            |> List.map (fun n -> "; " + n)
                            |> String.concat ""

                        match globalDir with
                        | Some dir ->
                            // the user dir [D:sig-arg-complete]: no lock — a
                            // user-level sig serves the REPL's Tab only, so
                            // verify/restore never see it and no #sig reads it
                            let dest = IO.Path.Combine(dir, sigFileName tool)
                            IO.Directory.CreateDirectory dir |> ignore
                            Contracts.AtomicFile.replaceFile dest (Text.UTF8Encoding(false).GetBytes text)

                            Ok
                                $"added sig {tool} to {dest} ({surfaceNote}, source: {fullSource}, version: {versionNote}{cutTail}) — user-level: the REPL's Tab reads it in every directory; no lock entry"
                        | None ->
                            let rel = "sigs/" + sigFileName tool

                            Contracts.AtomicFile.withWeirDirLock weirDir (fun () ->
                                Contracts.readLock weirDir
                                |> Result.bind (fun entries0 ->
                                    // a file another lock entry records under this name
                                    // (a legacy name: `a b` wrote sigs/a_b.weir) is that
                                    // tool's signature — never overwritten: it moves to
                                    // its owner's own name first, its lock entry with it
                                    // [D:sig-probe-bounds]
                                    let moveLegacy () =
                                        match
                                            entries0
                                            |> List.tryFind (fun e ->
                                                e.Kind = "sig" && e.Name <> tool && e.Path.Replace('\\', '/') = rel)
                                        with
                                        | None -> Ok(entries0, "")
                                        | Some owner ->
                                            let ownName = sigFileName owner.Name
                                            let src = IO.Path.Combine(weirDir, "sigs", sigFileName tool)
                                            let dst = IO.Path.Combine(weirDir, "sigs", ownName)
                                            let shown = Eval.sanitizeTtyEcho owner.Name

                                            if IO.File.Exists dst then
                                                Error
                                                    $"{rel} is the signature of '{shown}' (the lock records it under the name it had before file names were injective), and sigs/{ownName} exists too — move or remove one by hand"
                                            else
                                                try
                                                    if IO.File.Exists src then
                                                        IO.File.Move(src, dst)

                                                    Ok(
                                                        entries0
                                                        |> List.map (fun e ->
                                                            if e = owner then
                                                                {
                                                                    e with
                                                                        Path = IO.Path.Combine("sigs", ownName)
                                                                }
                                                            else
                                                                e),
                                                        $"; moved the signature of '{shown}' from {rel} to sigs/{ownName}"
                                                    )
                                                with ex ->
                                                    Error
                                                        $"{rel} is the signature of '{shown}', and moving it to sigs/{ownName} failed: {ex.Message}"

                                    // a write that would be refused moves nothing first
                                    Contracts.confinedLeaf
                                        weirDir
                                        (IO.Path.Combine(weirDir, "sigs", sigFileName tool))
                                    |> Result.bind moveLegacy
                                    |> Result.bind (fun (entries, movedNote) ->
                                        let dest = IO.Path.Combine(weirDir, "sigs", sigFileName tool)
                                        let bytes = Text.UTF8Encoding(false).GetBytes text

                                        // confined like every vendored write
                                        // [D:lockfile-symlink-confinement]: a linked
                                        // sigs/ or leaf is refused, never written
                                        // through — and replaced whole
                                        Contracts.replaceConfined weirDir dest bytes
                                        |> Result.bind (fun () ->

                                            let entry: Contracts.LockEntry = {
                                                Kind = "sig"
                                                Name = tool
                                                Url =
                                                    "generated:" + source + (if nodes.IsEmpty then "" else "+subs")
                                                Sha256 = Contracts.sha256Hex bytes
                                                Path = IO.Path.Combine("sigs", sigFileName tool)
                                                Version = version
                                                Closure = None
                                            }

                                            let others =
                                                entries
                                                |> List.filter (fun e -> not (e.Kind = "sig" && e.Name = tool))

                                            Contracts.writeLock weirDir (others @ [ entry ])

                                            // this tool's file under the legacy name is
                                            // superseded — unless another entry owns that
                                            // name [D:sig-probe-bounds]
                                            let legacy = "sigs/" + legacySigFileName tool

                                            if
                                                legacy <> "sigs/" + sigFileName tool
                                                && entries
                                                   |> List.exists (fun e ->
                                                       e.Kind = "sig"
                                                       && e.Name = tool
                                                       && e.Path.Replace('\\', '/') = legacy)
                                                && not (
                                                    others
                                                    |> List.exists (fun e -> e.Path.Replace('\\', '/') = legacy)
                                                )
                                            then
                                                try
                                                    IO.File.Delete(IO.Path.Combine(weirDir, legacy))
                                                with _ ->
                                                    ()

                                            Ok
                                                $"added sig {tool} ({surfaceNote}, source: {fullSource}, version: {versionNote}{cutTail}{movedNote}) — partial by default; verify and mark exhaustive by hand"))))
                finally
                    try
                        IO.File.Delete tmp
                    with _ ->
                        ()

                    Contracts.ProbeGuard.untrackPath tmp

    /// generate into the project's .weir/sigs/ and lock it
    let generate (weirDir: string) (tool: string) : Result<string, string> = generateTo None weirDir tool
