/// The REPL input-line colorizer. One of the files Script.fs was [D:script-by-concern].
module Weir.ReplColor

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble

// ANSI color for interactive diagnostics: gated per stream on TTY,
// NO_COLOR, and TERM=dumb — pipes and CI capture always get plain
// text, so pinned messages never see escape codes.
// Color moved to Types.fs (shared with the Log builtins); the alias
// keeps Script.Color consumers (Repl) and bare Color.* sites stable
module Color = Weir.Types.Color

// ---- the REPL input-line colorizer [D:repl-color] -----------------
// Rides the one scanner (inStringMask), stripComment, and the parser's
// keyword set — no re-derived string states, so this is the one
// highlighter that is correct by construction. Lexical grade only;
// the head word additionally colors by the session resolver's verdict
// (the fish trick). Fixed palette, no theming.

let stripAnsi (s: string) : string =
    System.Text.RegularExpressions.Regex.Replace(s, "\x1b\[[0-9;]*m", "")

// the string and hole masks of a line with `$"…{hole}…"` strings: a hole
// is code inside a string, so the colorizer paints it as code
// [D:interp-hole-complete]. The kinds mirror `closers`: plain and interp
// strings (backslash escapes), verbatim @"…" (`""` stays inside), triple
// """…""", single-quoted; `{{` in an interp string is a literal brace; a
// string inside a hole is a string again; an unclosed one runs to the end
let private interpMasks (line: string) : bool[] * bool[] =
    let str = Array.create line.Length false
    let hole = Array.create line.Length false
    let mutable stack: char list = []
    let mutable i = 0

    let mark (from: int) (until: int) =
        for j in from .. min until (line.Length - 1) do
            str[j] <- true

    while i < line.Length do
        let c = line[i]

        match stack with
        | ('"' | '$') :: rest ->
            str[i] <- true

            if c = '\\' && i + 1 < line.Length then
                let e = Scan.escapeEnd line i
                mark i e
                i <- e
            elif c = '"' then
                stack <- rest
            elif c = '{' && List.head stack = '$' then
                if i + 1 < line.Length && line[i + 1] = '{' then
                    mark i (i + 1)
                    i <- i + 1
                else
                    stack <- '}' :: stack
        | 'V' :: rest ->
            str[i] <- true

            if c = '"' then
                if i + 1 < line.Length && line[i + 1] = '"' then
                    mark i (i + 1)
                    i <- i + 1
                else
                    stack <- rest
        | 'T' :: rest ->
            str[i] <- true

            if c = '"' && i + 2 < line.Length && line[i + 1] = '"' && line[i + 2] = '"' then
                mark i (i + 2)
                i <- i + 2
                stack <- rest
        | '\'' :: rest ->
            str[i] <- true

            if c = '\'' then
                stack <- rest
        | top ->
            let inHole =
                match top with
                | '}' :: _ -> true
                | _ -> false

            if inHole && c = '}' then
                // the hole's closing brace belongs to the string
                str[i] <- true
                stack <- List.tail stack
            else
                if inHole then
                    hole[i] <- true

                if c = '"' && i + 2 < line.Length && line[i + 1] = '"' && line[i + 2] = '"' then
                    mark i (i + 2)
                    stack <- 'T' :: stack
                    i <- i + 2
                elif c = '"' then
                    str[i] <- true

                    stack <-
                        (if i > 0 && line[i - 1] = '@' then 'V'
                         elif i > 0 && line[i - 1] = '$' then '$'
                         else '"')
                        :: stack
                elif c = '\'' && not (Scan.isTyvarTick line i) then
                    str[i] <- true
                    stack <- '\'' :: stack
                elif c = '{' && inHole then
                    stack <- '}' :: stack

        i <- i + 1

    (str, hole)

let colorizeReplWith (isAlias: string -> bool) (isKnown: string -> bool) (line: string) : string =
    if line = "" then
        ""
    else
        // per-char color codes; None = plain
        let codes: string option array = Array.create line.Length None
        // a line with an interp string takes the hole-aware masks; any
        // other line keeps the shared scanner's
        let mask, holes =
            if line.Contains "$\"" then
                interpMasks line
            else
                inStringMask line, Array.create line.Length false

        for i in 0 .. line.Length - 1 do
            if mask[i] then
                codes[i] <- Some "32" // strings, all three kinds

        let commentCut =
            // paint from the '//' itself, not from the cut (the cut
            // TrimEnds [D:trailing-comments] — the gap stays uncolored)
            let code = stripComment line

            if code.Length = line.Length then
                line.Length
            else
                line.IndexOf("//", code.Length)

        for i in commentCut .. line.Length - 1 do
            codes[i] <- Some "90" // comments override to EOL

        let isIdentStart c = Char.IsLetter c || c = '_'
        let isIdentCont c = Char.IsLetterOrDigit c || c = '_'
        let free i = i < commentCut && not mask[i]

        // token pass over the code region
        let mutable i = 0
        // the within kind paints as part of the form [D:within-kinds]
        let mutable prevWord = ""
        // the mode tint [D:semantic-tokens]: an external head arms
        // command mode; argv words render dim until a '|' hands the
        // chain to an expression stage — the same three-way the LSP
        // tokens carry (head / argv / splice), from the same resolver
        let mutable cmdMode = false
        // a `(…)` or `$@(…)` argument is expression land between its
        // delimiters [D:tint-parity]: how many parens deep inside one
        let mutable argParens = 0

        while i < line.Length do
            if not (free i) then
                i <- i + 1
            elif cmdMode && not holes[i] && line[i] <> '$' && Parser.cmdWordChar line[i] then
                // an argv word is one inert token, as the parser reads
                // it: `v0.123.0` and `--some-flag` hold no numbers and
                // no operators [D:repl-color-words]
                let start = i

                while i < line.Length
                      && free i
                      && not holes[i]
                      && line[i] <> '$'
                      && Parser.cmdWordChar line[i] do
                    i <- i + 1

                for j in start .. i - 1 do
                    codes[j] <- Some "2"
            elif isIdentStart line[i] then
                let start = i

                while i < line.Length && free i && isIdentCont line[i] do
                    i <- i + 1

                let word = line.Substring(start, i - start)

                // the head word as the parser reads it: `argo-workflows`
                // is one command name, not `argo` minus `workflows`
                // [D:repl-color-words]
                let fullEnd =
                    let mutable k = i

                    while k < line.Length && free k && Parser.cmdWordChar line[k] do
                        k <- k + 1

                    k

                let full = line.Substring(start, fullEnd - start)

                let code =
                    // the head slot is Complete's one predicate
                    // [D:let-rhs-head]: the statement head and the
                    // let-RHS take the same verdict — tint and Tab
                    // cannot disagree about where a head stands
                    // an uppercase word after `|` is a module or constructor,
                    // never a program — a case-insensitive filesystem would
                    // otherwise find `Seq` as seq on PATH [D:constructors-not-heads]
                    match
                        (if holes[start] then
                             Complete.HeadSlot.No // a hole is an expression
                         else
                             match Complete.headSlotAt (line.Substring(0, start)) with
                             | Complete.HeadSlot.Stage when Char.IsUpper word[0] -> Complete.HeadSlot.No
                             | s -> s)
                    with
                    | Complete.HeadSlot.Forced ->
                        // ^head: PATH only — `^x` names a program,
                        // never a keyword, a form, or an alias
                        i <- fullEnd

                        if Extern.exists full then
                            // a forced head is a command like any other:
                            // its words are argv [D:tint-parity]
                            cmdMode <- true
                            Some "34"
                        else
                            Some "31"
                    | slot ->
                        if
                            slot <> Complete.HeadSlot.No
                            && i < line.Length
                            && line[i] = '='
                            && not (i + 1 < line.Length && line[i + 1] = '=')
                        then
                            // an env-prefix name is not a head [D:env-prefix],
                            // and its value is one inert word: `dkr@22aed5`
                            // holds no number and no head [D:repl-color-words]
                            let mutable k = i + 1

                            while k < line.Length
                                  && free k
                                  && not holes[k]
                                  && line[k] <> '$'
                                  && Parser.cmdWordChar line[k] do
                                k <- k + 1

                            i <- k
                            None
                        elif
                            slot = Complete.HeadSlot.Stage
                            && (word = "and" || word = "or")
                            && (match Complete.lastStage (line.Substring(0, start)) with
                                | Some(_, true) -> true
                                | _ -> false)
                        then
                            Some "34" // the chain words read as keywords [D:cmd-chaining]
                        elif Weir.Parser.keywords.Contains word then
                            // keywords: blue — the red family (31/35 render
                            // near-identically in some themes) is reserved for
                            // exactly one signal: a head that would fail
                            Some "34"
                        elif prevWord = "within" && Ast.withinKinds |> List.exists (fun k -> k.Name = word) then
                            Some "34" // the kind is form, not a name [D:within-kinds]
                        elif (prevWord = "from" || prevWord = "to") && Builtins.allAdapterNames.Contains word then
                            Some "34" // the adapter is form, not a name [D:form-word-hover]
                        elif slot <> Complete.HeadSlot.No && fullEnd > i && Extern.exists full then
                            i <- fullEnd
                            cmdMode <- true
                            Some "1;34" // PATH, by its whole name
                        elif
                            slot <> Complete.HeadSlot.No
                            && Char.IsUpper word[0]
                            && not (i < line.Length && line[i] = '.')
                            && isKnown word
                            && not (isAlias word)
                        then
                            Some "33"
                        elif slot <> Complete.HeadSlot.No && isKnown word then
                            // an alias head is a command: its words are
                            // argv [D:alias-argv-tint]
                            if isAlias word then
                                cmdMode <- true

                            // the fish trick: the head resolves live
                            Some "1" // known: bold
                        elif
                            Char.IsUpper word[0]
                            && i < line.Length
                            && line[i] = '.'
                            && (not cmdMode || holes[start])
                            && isKnown word
                        then
                            // a module qualifier reads the same wherever
                            // it stands — the head's known tint
                            // [D:repl-color-words]
                            Some "1"
                        elif slot <> Complete.HeadSlot.No && Extern.exists word then
                            cmdMode <- true
                            Some "1;34" // PATH: bold blue
                        elif slot = Complete.HeadSlot.Stmt || slot = Complete.HeadSlot.Stage then
                            if not (Char.IsUpper word[0]) then
                                i <- fullEnd

                            Some "31" // unresolved statement or stage head: red
                        elif Char.IsUpper word[0] then
                            // the casing rule: types/ctors/modules — at the
                            // let-RHS an unknown uppercase head is a legal
                            // constructor application, never red
                            // [D:constructors-not-heads]
                            Some "33"
                        elif slot = Complete.HeadSlot.LetRhs then
                            i <- fullEnd
                            Some "31" // unresolved lowercase RHS head: red
                        elif cmdMode && not holes[start] then
                            Some "2" // argv words: dim (inert data)
                        else
                            None

                prevWord <- word

                match code with
                | Some c ->
                    for j in start .. i - 1 do
                        codes[j] <- Some c
                | None -> ()
            elif Char.IsDigit line[i] then
                let start = i

                while i < line.Length && free i && Char.IsDigit line[i] do
                    i <- i + 1

                for j in start .. i - 1 do
                    codes[j] <- Some "36" // numbers: cyan
            elif line[i] = '$' || line[i] = '^' then
                // sigils, splices, force-prefix
                codes[i] <- Some "36"
                i <- i + 1

                // `$@name` is one splice, as `$name` is [D:argv-splat]
                if line[i - 1] = '$' && i < line.Length && free i && line[i] = '@' then
                    codes[i] <- Some "36"
                    i <- i + 1

                    if cmdMode && i < line.Length && free i && line[i] = '(' then
                        codes[i] <- Some "36"
                        i <- i + 1
                        argParens <- 1
                        cmdMode <- false

                if
                    (line[i - 1] = '$' || line[i - 1] = '@')
                    && i < line.Length
                    && free i
                    && isIdentStart line[i]
                then
                    while i < line.Length && free i && isIdentCont line[i] do
                        codes[i] <- Some "36"
                        i <- i + 1
            elif cmdMode && line[i] = '(' && not holes[i] then
                // a parenthesised argument: the delimiters mark the
                // splice, the inside is expression code [D:tint-parity]
                codes[i] <- Some "36"
                i <- i + 1
                argParens <- 1
                cmdMode <- false
            elif argParens > 0 && line[i] = '(' then
                argParens <- argParens + 1
                i <- i + 1
            elif argParens > 0 && line[i] = ')' then
                argParens <- argParens - 1

                if argParens = 0 then
                    codes[i] <- Some "36"
                    cmdMode <- true

                i <- i + 1
            elif "|><=+-*/".Contains(line[i]) then
                if line[i] = '|' && not holes[i] then
                    // a stage boundary: what follows is expression land
                    cmdMode <- false

                codes[i] <- Some "1" // operators: bold
                i <- i + 1
            else
                i <- i + 1

        // the yaml district marker [D:yaml-district]: the line-end word
        // tints like the `!` markers do; district body lines stay
        // per-line lexical — the block treatment is the static grammars'
        // and semantic tokens' job, not a line colorizer's
        let codeTrimmed = (line.Substring(0, commentCut)).TrimEnd()

        if codeTrimmed.Length >= 4 && isYamlMarkerPiece codeTrimmed then
            // marker + modifiers (patch/by=/schema=) — one strip loop
            // shared with the predicate, so tint and the marker rule
            // cannot disagree [D:yaml-nodes]
            let markerLen = Parser.yamlMarkerLen codeTrimmed

            for j in codeTrimmed.Length - markerLen .. codeTrimmed.Length - 1 do
                codes[j] <- Some "36"

        // emit: group adjacent same-code chars into spans
        let sb = System.Text.StringBuilder()
        let mutable j = 0

        while j < line.Length do
            let code = codes[j]
            let start = j

            while j < line.Length && codes[j] = code do
                j <- j + 1

            let span = line.Substring(start, j - start)

            match code with
            | Some c ->
                sb.Append("\x1b[").Append(c).Append('m').Append(span).Append("\x1b[0m")
                |> ignore
            | None -> sb.Append span |> ignore

        sb.ToString()

let colorizeRepl: (string -> bool) -> string -> string =
    colorizeReplWith (fun _ -> false)
