/// Source text to statements: comments and strings, doc comments, and the assembler that joins physical lines into logical ones. One of the files Script.fs was [D:script-by-concern].
module Weir.Assemble

open System
open Weir.Ast
open Weir.Types

// the quote-aware scanner [D:one-scanner] lives in Scan.fs
let private foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a =
    Scan.foldOutsideStrings f init s

let endsInsideString (s: string) : bool = Scan.endsInsideString s

type StringState = Scan.StringState

let stringStart: StringState = Scan.stringStart

let stringStep (st: StringState) (chunk: string) : StringState = Scan.stringStep st chunk

let isInsideString (st: StringState) : bool = Scan.isInsideString st

let stripComment (line: string) : string =
    let cut = Scan.commentAt line

    // TrimEnd only when a comment was cut [D:trailing-comments]: the
    // code before a comment never needs its gap (district heads match
    // EndsWith), and an untouched line stays byte-equal
    if cut >= 0 then line.Substring(0, cut).TrimEnd() else line

// ---- `///` doc comments [D:doc-comments] -------------------------
// Docs are out-of-band metadata about a source location, never part of
// the program's meaning: Value/Eval/Check never see one, so runtime is
// byte-identical by construction (nothing to erase, nothing to pin).
// The key is the physical (line, col, len) of the documented name,
// never the name itself (shadowing / inner lets / duplicate field
// names). Hover and completion look attachments up by that position.

/// The doc lines attached to one declaration, with the physical
/// (1-based line, 1-based col, length) of the name they document.
type DocAttach = {
    Line: int
    Col: int
    Len: int
    Doc: string list
}

let internal isDocLine (raw: string) : bool = raw.TrimStart().StartsWith "///"

/// the text of a `///` line: after the three slashes, one optional
/// leading space consumed (so `/// x` and `///x` both yield "x")
let private docText (raw: string) : string =
    let t = raw.TrimStart().Substring 3
    if t.StartsWith " " then t.Substring 1 else t

/// the documented name's (1-based col, len) on a declaration line: the
/// identifier after `let`/`type`, after `|` (union case), or leading
/// (a record field). None when the line has no such name.
let internal declName (code: string) : (int * int) option =
    let trimmed = code.TrimStart()
    let indent = code.Length - trimmed.Length

    let identAt (from: int) : (int * int) option =
        let mutable i = from

        while i < code.Length && (code[i] = ' ' || code[i] = '\t') do
            i <- i + 1

        if i < code.Length && (System.Char.IsLetter code[i] || code[i] = '_') then
            let s = i

            while i < code.Length && (System.Char.IsLetterOrDigit code[i] || code[i] = '_') do
                i <- i + 1

            Some(s + 1, i - s)
        else
            None

    // a same-line attribute (`[<Short "n">] n: int`) is transparent: the
    // documented name is the one after the attribute list [D:doc-help]
    let rec afterAttrs (from: int) : int =
        let mutable i = from

        while i < code.Length && (code[i] = ' ' || code[i] = '\t') do
            i <- i + 1

        if i + 1 < code.Length && code[i] = '[' && code[i + 1] = '<' then
            match code.IndexOf(">]", i + 2) with
            | -1 -> from
            | j -> afterAttrs (j + 2)
        else
            from

    if trimmed.StartsWith "let " then
        identAt (indent + 3)
    elif trimmed.StartsWith "type " then
        identAt (indent + 4)
    elif trimmed.StartsWith "|" then
        identAt (afterAttrs (indent + 1))
    elif trimmed = "" then
        None
    else
        identAt (afterAttrs indent)

// In-string mask over a line — true where a char sits inside any
// string kind (plain/single/verbatim/triple). The scanner family's
// third consumer [D:fmt-respace]: respacing must never touch
// string interiors.
let inStringMask (s: string) : bool[] =
    let mask = Array.create s.Length false
    let mutable outside = System.Collections.Generic.HashSet<int>()

    foldOutsideStrings
        (fun () i _ ->
            outside.Add i |> ignore
            ())
        ()
        s

    for i in 0 .. s.Length - 1 do
        mask[i] <- not (outside.Contains i)

    mask

// Canonical intra-line spacing [D:fmt-respace], bounded: collapse
// space runs, pad record braces, tidy `;`, and pad the unambiguous
// binary operators (`=`, `==`, `<>`, `<=`, `>=`, `&&`, `||`, `|>`,
// `+` — never `-`, whose adjacency is meaning [D:operator-values],
// and never bare `<`/`>`/`*`/`/`, which live in types, globs and
// paths). String interiors and leading indent untouched. Fmt applies
// this under a parse-shape safety check — any statement whose sexpr
// changes reverts — so a rule misfiring on a command line (argv
// `{x}`, literal `;`, an `a||b` word, `attempts=3`) can never change
// meaning, only be skipped.
let respaceLine (line: string) : string =
    let mask = inStringMask line
    let indent = line |> Seq.takeWhile ((=) ' ') |> Seq.length
    let sb = System.Text.StringBuilder()

    let lastEmitted () =
        if sb.Length = 0 then ' ' else sb[sb.Length - 1]

    let mutable i = 0

    // the operator at i, length included — two-char first, then the
    // single `=`/`+` with their neighbor guards (`=` next to another
    // operator char is that operator's own business, not a binding)
    let opAt (i: int) : int option =
        let two =
            i + 1 < line.Length
            && not mask[i + 1]
            && (match line[i], line[i + 1] with
                | '=', '='
                | '<', '>'
                | '<', '='
                | '>', '='
                | '&', '&'
                | '|', '|'
                | '|', '>' -> true
                | _ -> false)

        if two then
            Some 2
        else
            let prev = if i = 0 then ' ' else line[i - 1]
            let next = if i + 1 < line.Length then line[i + 1] else ' '
            let opChar (ch: char) = "=<>&|+-*/^!." |> Seq.contains ch

            match line[i] with
            | '=' when not (opChar prev) && not (opChar next) -> Some 1
            | '+' when not (opChar prev) && not (opChar next) -> Some 1
            | _ -> None

    while i < line.Length do
        let c = line[i]

        if i < indent || mask[i] then
            sb.Append c |> ignore
            i <- i + 1
        else
            match opAt i with
            | Some len ->
                if lastEmitted () <> ' ' then
                    sb.Append ' ' |> ignore

                sb.Append(line.Substring(i, len)) |> ignore

                if i + len < line.Length && line[i + len] <> ' ' then
                    sb.Append ' ' |> ignore

                i <- i + len
            | None ->
                (match c with
                 | ' ' when lastEmitted () = ' ' -> () // collapse runs
                 | '{' when
                     i + 1 < line.Length
                     && line[i + 1] <> ' '
                     && line[i + 1] <> '{'
                     && lastEmitted () <> '{'
                     ->
                     sb.Append "{ " |> ignore
                 | '}' when lastEmitted () <> ' ' -> sb.Append " }" |> ignore
                 | ';' ->
                     // no space before, one after
                     while sb.Length > indent && lastEmitted () = ' ' do
                         sb.Remove(sb.Length - 1, 1) |> ignore

                     sb.Append ';' |> ignore

                     if i + 1 < line.Length && line[i + 1] <> ' ' then
                         sb.Append ' ' |> ignore
                 | c -> sb.Append c |> ignore)

                i <- i + 1

    sb.ToString()

// the 1-based column of the first tab in a line's leading whitespace
// [D:spaces-only-indent]
let private leadingTabCol (raw: string) : int option =
    let mutable i = 0

    while i < raw.Length && raw[i] = ' ' do
        i <- i + 1

    if i < raw.Length && raw[i] = '\t' then
        Some(i + 1)
    else
        None

let private leadingSpaces (s: string) : int =
    let mutable i = 0

    while i < s.Length && s[i] = ' ' do
        i <- i + 1

    i

let private leadingSpacesTabs (s: string) : int =
    let mutable i = 0

    while i < s.Length && (s[i] = ' ' || s[i] = '\t') do
        i <- i + 1

    i

let private tabIndentError (lineNo: int) (raw: string) : string =
    let col = leadingTabCol raw |> Option.defaultValue 1
    $"line {lineNo}: tabs are not allowed in indentation (a tab at column {col}) — indent with spaces"

// net { vs } outside strings — interpolation holes sit inside the
// quotes, so their braces never count; clamping to >= 0 happens at the
// consumer (stray command-arg } must not poison the statement)
let private braceDelta (s: string) : int =
    foldOutsideStrings
        (fun d _ c ->
            (if c = '{' then d + 1
             elif c = '}' then d - 1
             else d))
        0
        s

// --- the line classifier: one derivation, three consumers -----------
// (assembler fold, fmt's block logic, the oracle's weirVerdict mirror
// — sharing the derivation is what makes them agree)

/// Whole-line classification, pre-assembly: the statement filter.
[<RequireQualifiedAccess>]
type LineKind =
    | Blank
    | CommentOnly
    | Code

let classifyLine (raw: string) : LineKind =
    if raw.Trim() = "" then
        LineKind.Blank
    elif (stripComment raw).Trim() = "" then
        LineKind.CommentOnly
    else
        LineKind.Code

// the init file declares itself [D:init-marker]: `#init` as the first
// code line makes a file init-kind WHEREVER it lives — authorable in a
// dotfiles repo, applied by symlinking into <configHome>/weir/init.weir
// (the REPL loads only that path: which init runs must never depend on
// the cwd). The marker is REQUIRED — the canonical file without it
// refuses at load — so a file's kind is always in the file.
let isInitMarked (lines: string list) : bool =
    lines
    |> List.tryFind (fun l -> classifyLine l = LineKind.Code)
    |> Option.map (fun l -> (stripComment l).Trim() = "#init")
    |> Option.defaultValue false

// init.weir carries init-only directives (#session, #alias) the script
// checker does not know [D:repl-init][D:session-prompt] — rewrite them
// into an equivalent script for diagnostics AND completion: blank the
// `#init`/`#session {`/`}`/`#alias` lines, and turn each
// `<4-indent>key = value` field into `let <binder> = value`
// COLUMN-PRESERVING — 4-space indent out, `let ` in, a same-length
// binder (so the value stays at its exact column for hover/completion,
// and a distinct `_`-led binder neither self-references the value nor
// warns unused).
let sameLenBinder (key: string) : string =
    // same length as the key, distinct from it (no self-reference), no
    // unused/discard warning: `_` + the key's tail, e.g. prompt -> _rompt
    if key.Length = 0 then "_" else "_" + key.Substring 1

let stripInitDirectives (lines: string list) : string list =
    let fieldStart =
        System.Text.RegularExpressions.Regex @"^(\s{0,4})([A-Za-z_][A-Za-z0-9_]*)(\s*=\s*)(.*)$"

    let mutable inBlock = false

    [
        for l in lines do
            let t = l.Trim()

            if inBlock then
                if t = "}" then
                    inBlock <- false
                    yield ""
                else
                    let m = fieldStart.Match l

                    if m.Success then
                        // `<indent>key = value` -> `let <binder> = value`; when the
                        // indent is 4, `let ` + a same-length binder keeps the value
                        // column exact (columns matter for hover/completion)
                        let indent = m.Groups.[1].Value
                        let binder = sameLenBinder m.Groups.[2].Value

                        if indent.Length = 4 then
                            yield "let " + binder + m.Groups.[3].Value + m.Groups.[4].Value
                        else
                            yield "let " + binder + " = " + m.Groups.[4].Value
                    else
                        yield l // a continuation line (list entry) is part of the value
            elif t = "#init" then
                yield ""
            elif t.StartsWith "#session" then
                inBlock <- t.EndsWith "{"
                yield ""
            elif t = "#alias" || t.StartsWith "#alias " then
                yield ""
            elif l.StartsWith "#" then
                // any other directive line is splitInit's to report
                // [D:directive-diagnostic]
                yield ""
            else
                yield l
    ]


/// a col-0 `else`/`elif` line continues its open `if` [D:toplevel-if-else]
/// — the top-level block form (`if c then <block>` then a dedented
/// `else`/`elif <block>`). `else`/`elif` are keywords, so a line whose
/// leading word is one can only continue an open if, never head a fresh
/// statement; checked on the trimmed text so an identifier such as
/// `elsewhere` is untouched. Hoisted out of the assembler's col-0 gate so
/// that giant function carries no inline `let … in`.
// the trailing-whitespace-trimmed line as a span: the col-0 keyword
// checks compare without a TrimEnd allocation per line
let private trimmedEnd (raw: string) =
    System.MemoryExtensions.TrimEnd(System.MemoryExtensions.AsSpan raw)

let private spanIs (t: System.ReadOnlySpan<char>) (word: string) =
    System.MemoryExtensions.SequenceEqual(t, System.MemoryExtensions.AsSpan word)

let private spanStarts (t: System.ReadOnlySpan<char>) (prefix: string) =
    System.MemoryExtensions.StartsWith(t, System.MemoryExtensions.AsSpan prefix, System.StringComparison.Ordinal)

let continuesOpenIf (raw: string) : bool =
    let lw = trimmedEnd raw
    spanIs lw "else" || spanStarts lw "else " || spanStarts lw "elif "

/// Piece classification, inside assembly: the join/structure decisions.
/// Kind is exclusive; Marker and OpensCompound are orthogonal fields —
/// `let d = yaml` is a let head and also arms the yaml district.
[<RequireQualifiedAccess>]
type PieceKind =
    | PipeHead
    | ElseHead
    | LetHead
    | Plain

/// Line-end district markers: `yaml` (with an optional schema= suffix)
/// is the only surviving marker — the `!`/`!name` command districts
/// were retired [D:district-retirement].
[<RequireQualifiedAccess>]
type MarkerKind =
    | NoMarker
    // line-end `yaml` opens a yaml district [D:yaml-district] — except
    // `to yaml` / `from yaml`, which are the boundary adapters.
    // The `!` and `!ev` districts are retired [D:district-retirement]:
    // the arming rule made their mode gate unnecessary and `within env`
    // covers the overlay; the $e() capture sigil covers fragments.
    | Yaml
    // line-end `<<<` / `$<<<` opens a heredoc district [D:text-block] —
    // the yaml district's sibling; identical join semantics (verbatim
    // lines, relative indentation behind the sentinel)
    | Heredoc

type PieceClass = {
    Kind: PieceKind
    Marker: MarkerKind
    OpensCompound: bool
    ClosesBrace: bool
    ClosesParen: bool
    StartsField: bool
    StartsTypeField: bool
    BraceDelta: int
}

let private isIdentToken (t: string) =
    t.Length > 0
    && (System.Char.IsLetter t[0] || t[0] = '_')
    && t |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

// isIdentToken over `piece[0 .. stop)` with its trailing whitespace
// trimmed, without the substring
let private isIdentPrefix (piece: string) (stop: int) =
    let mutable e = stop

    while e > 0 && System.Char.IsWhiteSpace piece[e - 1] do
        e <- e - 1

    e > 0
    && (System.Char.IsLetter piece[0] || piece[0] = '_')
    && (let mutable ok = true
        let mutable i = 1

        while ok && i < e do
            let c = piece[i]
            ok <- System.Char.IsLetterOrDigit c || c = '_'
            i <- i + 1

        ok)

/// the marker predicate lives in Parser (shared with completion);
/// this alias keeps Script's callers and pins stable
let isYamlMarkerPiece = Parser.isYamlMarkerPiece
let isHeredocMarkerPiece = Parser.isHeredocMarkerPiece

/// the arm arrow `->` at bracket-depth 0, outside strings
/// [D:match-pipe-offside] — the match arm's body offside. Patterns carry
/// no arrow and a guard lambda's arrow nests in parens, so the first such
/// `->` is the arm's; a string literal in the pattern is skipped.
let armArrowIndex (s: string) : int option =
    let mutable depth = 0
    let mutable inStr = false
    let mutable i = 0
    let mutable found = -1

    while found < 0 && i < s.Length - 1 do
        let c = s[i]

        if inStr then
            if c = '\\' then
                i <- i + 1
            elif c = '"' then
                inStr <- false
        else
            match c with
            | '"' -> inStr <- true
            | '('
            | '['
            | '{' -> depth <- depth + 1
            | ')'
            | ']'
            | '}' -> depth <- depth - 1
            | '-' when depth = 0 && s[i + 1] = '>' -> found <- i
            | _ -> ()

        i <- i + 1

    if found >= 0 then Some found else None

/// an inline `let x = e in body` [D:inline-let-in-blocks]: a `let`
/// piece whose every `let` (outside strings and brackets) is closed by an
/// `in` on the same line, with a body after the last one, is a complete
/// expression — not a block-let head awaiting its body on the next
/// same-indent line. A `for`/`pfor` takes its own `in`.
let private hasInlineIn (piece: string) : bool =
    let isWord c =
        System.Char.IsLetterOrDigit c || c = '_'

    let mutable depth = 0
    let mutable inStr = false
    let mutable i = 0
    let mutable openLets = 0
    let mutable openFors = 0
    let mutable lastWasIn = false

    while i < piece.Length do
        let c = piece[i]

        if inStr then
            if c = '\\' then
                i <- i + 1
            elif c = '"' then
                inStr <- false

            i <- i + 1
        elif c = '"' then
            inStr <- true
            lastWasIn <- false
            i <- i + 1
        elif c = '(' || c = '[' || c = '{' then
            depth <- depth + 1
            lastWasIn <- false
            i <- i + 1
        elif c = ')' || c = ']' || c = '}' then
            depth <- depth - 1
            lastWasIn <- false
            i <- i + 1
        elif isWord c then
            let s = i

            while i < piece.Length && isWord piece[i] do
                i <- i + 1

            let w = piece.Substring(s, i - s)

            // a keyword stands alone: glued to a member dot, a type-var
            // tick or any other token character it is part of a word
            // (`-in`/`--in` is a flag, `a.in` a member, `'in` a type var)
            let bounded =
                s = 0
                || (let p = piece[s - 1]

                    System.Char.IsWhiteSpace p
                    || p = '('
                    || p = ')'
                    || p = '['
                    || p = ']'
                    || p = '{'
                    || p = '}'
                    || p = ';'
                    || p = ',')

            lastWasIn <- false

            if depth = 0 && bounded then
                match w with
                | "let" -> openLets <- openLets + 1
                | "for"
                | "pfor" -> openFors <- openFors + 1
                | "in" when openFors > 0 -> openFors <- openFors - 1
                | "in" ->
                    openLets <- openLets - 1
                    lastWasIn <- true
                | _ -> ()
        else
            if not (System.Char.IsWhiteSpace c) then
                lastWasIn <- false

            i <- i + 1

    openLets <= 0 && not lastWasIn

let classifyPiece (piece: string) : PieceClass = {
    Kind =
        if piece.StartsWith "|" then
            PieceKind.PipeHead
        elif
            piece = "else"
            || piece.StartsWith "else "
            // elif extends a compound exactly as else does [D:elif]
            || piece = "elif"
            || piece.StartsWith "elif "
            // `until` extends retry/poll the same way [D:retry-poll]
            || piece = "until"
            || piece.StartsWith "until "
            // `always` extends a bare within the same way [D:within-always]
            || piece = "always"
        then
            PieceKind.ElseHead
        elif piece.StartsWith "let " && not (hasInlineIn piece) then
            PieceKind.LetHead
        else
            PieceKind.Plain
    Marker =
        if isYamlMarkerPiece piece then MarkerKind.Yaml
        elif isHeredocMarkerPiece piece then MarkerKind.Heredoc
        else MarkerKind.NoMarker
    OpensCompound =
        // within/for block heads close-and-wrap exactly like the
        // conditionals [D:dedent-join] — same machine, two more
        // members, not a fifth alignment stack (let-prefixed forms
        // stay Lets-owned, the if/match convention)
        piece.StartsWith "if "
        || piece.StartsWith "match "
        || piece.StartsWith "within "
        // the bare within [D:within-always] — its `always` closes the
        // body like an else, and a dedented sibling statement closes both
        || piece = "within"
        || piece.StartsWith "for "
        || piece.StartsWith "pfor "
        // the bounded-loop pair [D:retry-poll] — the machine's 5th and
        // 6th members, still no stack
        || piece.StartsWith "retry "
        || piece.StartsWith "poll "
        // and repeat [D:repeat]
        || piece.StartsWith "repeat "
    ClosesBrace = piece.StartsWith "}"
    ClosesParen = piece.StartsWith ")"
    StartsField =
        (match piece.IndexOf '=' with
         | i when i > 0 -> isIdentPrefix piece i
         | _ -> false)
    // type-declaration fields carry `:` not `=`; attribute lines
    // start their field [D:multiline-brackets]
    StartsTypeField =
        piece.StartsWith "[<"
        || (match piece.IndexOf ':' with
            | i when i > 0 -> isIdentPrefix piece i
            | _ -> false)
    BraceDelta = braceDelta piece
}

/// The multiline-lambda opener [D:multiline-lambda]: a line ends with
/// `->` while its innermost unclosed paren opens a `fun` — `(fun r ->`
/// dangling at EOL opens a body block closed by its own `)`. A same-line
/// `(fun r -> body)` never arms (its parens balance at EOL).
let lambdaOpens (piece: string) : bool =
    let trimmed = piece.TrimEnd()

    if not (trimmed.EndsWith "->") then
        false
    else
        let opens =
            foldOutsideStrings
                (fun stack i c ->
                    match c with
                    | '(' -> i :: stack
                    | ')' ->
                        (match stack with
                         | _ :: t -> t
                         | [] -> [])
                    | _ -> stack)
                []
                trimmed

        match opens with
        | top :: _ -> trimmed.Substring(top + 1).TrimStart().StartsWith "fun "
        | [] -> false

/// A bracket entry whose value dangles a lambda arrow at EOL opens a
/// statement body below it [D:bracket-lambda-body]: the trailing `->`
/// is the last `fun`'s own — an arm's `->` after that `fun` keeps the
/// line a plain continuation, its arms below it.
let lambdaDangles (piece: string) : bool =
    let t = piece.TrimEnd()

    if not (t.EndsWith "->") then
        false
    else
        let body = t.Substring(0, t.Length - 2)

        let isIdent (c: char) =
            System.Char.IsLetterOrDigit c || c = '_'

        // the last marker before the trailing arrow: a `fun` word (1) or
        // an earlier arrow (2)
        let last =
            foldOutsideStrings
                (fun (st: int) i c ->
                    if c = '-' && i + 1 < body.Length && body[i + 1] = '>' then
                        2
                    elif
                        c = 'f'
                        && i + 2 < body.Length
                        && body[i + 1] = 'u'
                        && body[i + 2] = 'n'
                        && (i = 0 || not (isIdent body[i - 1]))
                        && (i + 3 = body.Length || body[i + 3] = ' ' || body[i + 3] = '(')
                    then
                        1
                    else
                        st)
                0
                body

        last = 1

/// A piece that dangles a block open at EOL: the next deeper line
/// starts a statement of that block (the lambda restore level rides
/// this) [D:multiline-lambda].
let dangleEnders = [| "="; "then"; "else"; "with"; "->" |]

// a within head [D:within-scopes]: `within <kind> <args…>` (optionally
// behind `let <name> =`) opens its block — the head ends with arbitrary
// argument words, so the classifier keys on the keyword, as the yaml
// marker does (a lexical rule shared by assembler and REPL, never a parse)
let isWithinHead (piece: string) : bool =
    let t = piece.Trim()

    let afterLet =
        if t.StartsWith "let " then
            match t.IndexOf '=' with
            | -1 -> t
            | i -> t.Substring(i + 1).TrimStart()
        else
            t

    afterLet = "within"
    || afterLet.StartsWith "within " && not (afterLet.Contains ";")

// a standalone district head [D:pure-stage1]: a bare district keyword
// (or one behind `let <name> =`) opens its block exactly as a within
// head does — the same lexical rule, one word shorter (no kind, no args)
let private isDistrictHead (keyword: string) (piece: string) : bool =
    if not (piece.Contains keyword) then
        false
    else

        let t = piece.Trim()

        let afterLet =
            if t.StartsWith "let " then
                match t.IndexOf '=' with
                | -1 -> t
                | i -> t.Substring(i + 1).TrimStart()
            else
                t

        afterLet = keyword
        || afterLet.StartsWith(keyword + " ") && not (afterLet.Contains ";")

let isPureHead (piece: string) : bool = isDistrictHead "pure" piece

// the standalone readonly head [D:desugar-namespace]: readonly opens a
// statement block exactly as pure does — a body may sequence unit
// statements before its result value, so it needs the sibling sentinel
// between them (treating the body as value-shaped space-joined a
// multi-statement body and mis-parsed it)
let isReadonlyHead (piece: string) : bool = isDistrictHead "readonly" piece

// the standalone plan head [D:plan-apply]: `plan` (bare, or behind
// `let <name> =`) opens its block exactly as pure/readonly do — a plan
// body is a statement sequence (bare consecutive mutations, captured as
// Ops), so it needs the sibling sentinel between its statements
let isPlanHead (piece: string) : bool = isDistrictHead "plan" piece

// the binder-head scanner [D:scoped-procs] [D:http-serve]: a `within
// proc <b> =` / `within serve <b> …` head whose tail is a resource spec
// (a command for proc, options or `{config} handler` atoms for serve), so the
// first block statement must join at the machine boundary too — a space
// join would feed it to the tail (proc: the command's argv; serve: the
// handler application). This scan is `;`-blind (it finds the marker
// outside strings and checks only the binder-`=` shape), so an inline
// record `;` in serve's config tail does not hide the head — the reason
// serve routes through here and not isWithinHead's `;`-guarded path.
// The test takes an already-extracted last segment
// [D:assemble-quadratic]: the whole-text `LastIndexOf sibSep` slice moved
// to the caller so the assembler can hand its incrementally-tracked last
// segment (short) instead of re-slicing the whole growing text per join.
// A fast reject — Contains is allocation-free and vectorized — skips the
// per-char foldOutsideStrings scan on the vast majority of segments,
// which carry no marker at all.
let private endsInBinderHeadSeg (needsEq: bool) (marker: string) (lastSeg: string) : bool =
    if not (lastSeg.Contains marker) then
        false
    else

        // the head may sit mid-segment (behind a let, a lambda arrow, a
        // pipe) — find its last occurrence outside strings (the one
        // scanner), then require the binder-`=` shape; the tail then runs to
        // the segment's end, which is the thing a space join would feed
        let lastStart =
            foldOutsideStrings
                (fun acc i _ ->
                    if
                        i + marker.Length <= lastSeg.Length
                        && lastSeg.Substring(i, marker.Length) = marker
                        // word-bounded: an identifier merely ending in the
                        // letters must not trip it (the `do` rule's argument)
                        && (i = 0
                            || not (System.Char.IsLetterOrDigit lastSeg[i - 1] || lastSeg[i - 1] = '_'))
                    then
                        i
                    else
                        acc)
                -1
                lastSeg

        lastStart >= 0
        && (not needsEq
            || (let rest = lastSeg.Substring(lastStart + marker.Length)

                match rest.IndexOf '=' with
                | -1 -> false
                | eq ->
                    rest.Substring(0, eq).Trim()
                    |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')))

// the last segment of a full logical-line text (after the last sibling
// sentinel) — the whole-text callers (dangleOpensBlock on a short piece)
let private lastSegOf (text: string) : string =
    match text.LastIndexOf Parser.sibSep with
    | -1 -> text
    | i -> text.Substring(i + 1)

let private endsInBinderHead (needsEq: bool) (marker: string) (text: string) : bool =
    endsInBinderHeadSeg needsEq marker (lastSegOf text)

let endsInProcHead (text: string) : bool =
    endsInBinderHead true "within proc " text

// the serve head [D:http-serve]: same shape as proc's — a `within serve
// <b> …` binder head whose block joins sentineled — routed through the
// same `;`-blind scanner so an inline config record `{ a; b }` on the
// head line does not defeat the body attach. Every serve head qualifies,
// `=` or not [D:serve-defaults]: its tail is key=value pairs or atoms,
// and a first block line `NAME=value cmd` must not read as one more pair
let endsInServeHead (text: string) : bool =
    endsInBinderHead false "within serve " text

let private isLoopHead (t: string) : bool =
    let afterLet =
        if t.StartsWith "let " then
            match t.IndexOf '=' with
            | -1 -> t
            | i -> t.Substring(i + 1).TrimStart()
        else
            t

    // repeat joins retry/poll [D:repeat]
    [ "retry"; "poll"; "repeat" ]
    |> List.exists (fun k -> afterLet = k || afterLet.StartsWith(k + " "))

let dangleOpensBlock (piece: string) : bool =
    let t = piece.TrimEnd()

    dangleEnders |> Array.exists t.EndsWith
    // `do` needs a word boundary — `sudo` at EOL must not dangle a
    // block open [D:for-do] (the existing enders keep their exact
    // suffix behavior)
    || t = "do"
    || t.EndsWith " do"
    // a line-end `function` dangles its arm block open, the match-head
    // shape one word shorter [D:function-keyword]; word-bounded like
    // `do` (an identifier merely ending in the letters must not dangle)
    || t = "function"
    || t.EndsWith " function"
    || t.EndsWith "(function"
    || isWithinHead t
    // a serve head with an inline config `;` is `;`-blind here
    // [D:http-serve] — isWithinHead's `;`-guard would hide it
    || endsInServeHead t
    || isPureHead t
    // readonly opens a statement block too [D:desugar-namespace] — a
    // multi-statement body sentinels between its statements, like pure
    || isReadonlyHead t
    // the plan head opens a statement block [D:plan-apply]
    || isPlanHead t
    // retry/poll heads and the until binder line open their blocks
    // [D:retry-poll] — behind `let <name> =` too, the within head's rule
    // [D:let-loop-body-sequences]: the body's same-indent lines sequence
    // exactly as under a bare loop head
    || isLoopHead t
    || t.StartsWith "until "
    // the bare within's teardown segment dangles too [D:within-always]
    || t = "always"

/// Smart indent's question [D:repl-autoindent]: does the text before the
/// editor's cursor open an indented body? The assembler's own set
/// (dangleOpensBlock) on the comment-stripped text, minus what only
/// looks like an opener at EOL: a line ending inside a string, a
/// comparison (`==` `<=` `>=` `!=`), an `=` inside an unclosed `{`
/// (a record field), and a word ender glued to an identifier
let opensIndentedBody (before: string) : bool =
    if Scan.endsInsideString before then
        false
    else
        let t = (stripComment before).TrimEnd()

        let gluedWord =
            [ "then"; "else"; "with"; "do" ]
            |> List.exists (fun w ->
                t.EndsWith w
                && t.Length > w.Length
                && (System.Char.IsLetterOrDigit t[t.Length - w.Length - 1]
                    || t[t.Length - w.Length - 1] = '_'))

        let comparison = [ "=="; "<="; ">="; "!=" ] |> List.exists t.EndsWith

        let fieldEq = t.EndsWith "=" && braceDelta t > 0

        // a within's `until stopped` takes no block: it dangles for the
        // assembler only so a deeper line reaches the parser's teaching
        // [D:held-scope]
        let held = t.TrimStart() = "until stopped"

        t <> ""
        && dangleOpensBlock (t.TrimStart())
        && not gluedWord
        && not comparison
        && not fieldEq
        && not held

/// A line-end district marker of any kind — the mask below and the
/// REPL share classifyPiece's marker rules through these predicates.
let isMarkerPiece (piece: string) =
    isYamlMarkerPiece piece || isHeredocMarkerPiece piece

/// True for physical lines that are district content (deeper than an
/// arming marker line) [D:content-bytes]: the byte-preserving passes —
/// doc attachment, the doc-align lint, fmt's doc canonicalization —
/// must neither read nor move them; content is bytes.
let private districtContentMaskOf (raws: string[]) (codes: string[]) : bool[] =
    let mask = Array.create raws.Length false
    let mutable armed = -1 // the marker line's indent

    for i in 0 .. raws.Length - 1 do
        let raw = raws[i]
        let code = codes[i]

        if System.String.IsNullOrWhiteSpace raw then
            () // blank: transparent, stays armed, unmasked
        elif System.String.IsNullOrWhiteSpace code then
            if armed >= 0 && leadingSpaces raw > armed then
                mask[i] <- true
        elif armed >= 0 && leadingSpaces raw > armed then
            mask[i] <- true
        else
            armed <- -1

            if
                (code.Contains "yaml" || code.Contains "<<<")
                && isMarkerPiece (code.TrimStart())
            then
                armed <- leadingSpaces raw

    mask

let districtContentMask (lines: string list) : bool[] =
    let raws = List.toArray lines
    districtContentMaskOf raws (Array.map stripComment raws)

/// Pure pass: a contiguous run of `///` lines attaches to the
/// declaration on the next code line; a blank or a plain `//` line
/// breaks the run (the contiguity rule). An attribute-only line
/// (`[<...>]`) is transparent: the doc rides through to the
/// declaration below, so F#'s canonical doc-then-attribute order and
/// the attribute-then-doc order both attach.
let isAttributeOnlyLine (raw: string) =
    let t = (stripComment raw).Trim()
    t.StartsWith "[<" && t.EndsWith ">]"

let docAttachments (lines: string list) : DocAttach list =
    let mutable pending: string list = []
    let acc = System.Collections.Generic.List<DocAttach>()

    let raws = List.toArray lines
    let codes = Array.map stripComment raws
    let masked = districtContentMaskOf raws codes

    raws
    |> Array.iteri (fun idx raw ->
        let code = codes[idx]
        let ln = idx + 1

        if masked[idx] then
            () // district content is bytes — never doc syntax [D:content-bytes]
        elif isDocLine raw then
            // Cons, not `@ [x]`: a contiguous /// run of N lines appended
            // one at a time is O(N^2) (each append copies the whole list),
            // pinning a core on a heavily documented module. Cons is O(1);
            // the run is reversed once at the single consumer (Doc below).
            pending <- docText raw :: pending
        elif
            (let t = System.MemoryExtensions.Trim(System.MemoryExtensions.AsSpan code)

             System.MemoryExtensions.StartsWith(t, "[<", System.StringComparison.Ordinal)
             && System.MemoryExtensions.EndsWith(t, ">]", System.StringComparison.Ordinal))
        then
            ()
        elif System.String.IsNullOrWhiteSpace raw then
            pending <- []
        elif System.String.IsNullOrWhiteSpace code then
            pending <- [] // a plain // comment-only line breaks contiguity
        else
            if not (List.isEmpty pending) then
                match declName code with
                | Some(col, len) ->
                    acc.Add {
                        Line = ln
                        Col = col
                        Len = len
                        Doc = List.rev pending
                    } // reverse the consed run
                | None -> ()

            pending <- [])

    List.ofSeq acc

/// the module's own doc [D:module-doc]: the `///` run directly above
/// the `module` line, by the contiguity rule every doc follows
let moduleDocOf (lines: string list) : string list =
    let raws = List.toArray lines

    docAttachments lines
    |> List.tryPick (fun d ->
        let code = (stripComment raws[d.Line - 1]).Trim()

        if code = "module" || code.StartsWith "module " then
            Some d.Doc
        else
            None)
    |> Option.defaultValue []

/// The marker's district wrap: opener text and how many trailing
/// characters of the armed line the first district line strips.
/// the retired district spellings [D:district-retirement] — detected
/// so their removal error explains itself instead of dumping an
/// expecting-list (a documented feature's removal is the one case a
/// reader has a right to be confused about)
let retiredDistrictMarker (piece: string) : bool =
    if not (piece.Contains '!') then
        false
    else

        let lastToken =
            match piece.LastIndexOf ' ' with
            | -1 -> piece
            | i -> piece.Substring(i + 1)

        // the district marker ended a COMMAND line; a `!name` after an
        // expression-position token (or alone) is C negation, which the
        // parser teaches as `not` [D:left-to-right-ops]
        let before =
            let rest = piece.Substring(0, piece.Length - lastToken.Length).TrimEnd()

            match rest.LastIndexOf ' ' with
            | -1 -> rest
            | i -> rest.Substring(i + 1)

        // (a standalone `!e`, or one after then/else/->, was a district header
        // and keeps its retirement teaching)
        let negationSlot =
            // an operator's operand too: `1 + !x` is the negation slip
            List.contains before [
                "="
                "("
                "&&"
                "||"
                "<-"
                ","
                "["
                "not"
                "=="
                "<>"
                "+"
                "-"
                "*"
                "/"
                "%"
                "<"
                ">"
                "<="
                ">="
            ]
            || before.EndsWith "("

        piece = "!"
        || piece.EndsWith " !"
        || (lastToken.StartsWith "!"
            && not (lastToken.Contains "(")
            && isIdentToken (lastToken.Substring 1)
            && not negationSlot)

let private markerOpener (m: MarkerKind) : (string * int * bool) option =
    match m with
    | MarkerKind.NoMarker -> None

    // the yaml district keeps its marker word; lines join verbatim with
    // relative indentation behind the sentinel [D:yaml-district]
    | MarkerKind.Yaml -> Some("", 0, true)
    // the text district joins exactly as yaml does [D:text-block]
    | MarkerKind.Heredoc -> Some("", 0, true)

// translate's per-line data rides the line itself [D:line-pos-cache]:
// a ConditionalWeakTable entry per line is a GC dependent handle, and one
// per statement made every collection walk them all. Equality is the
// line's text and segments only, as before the fields moved here.
[<CustomEquality; NoComparison>]
type LogicalLine = {
    Text: string
    Head: int
    Segments: (int * int * int) list
    // where each segment's own text ends, aligned with Segments (the
    // join text after it is no physical column); empty for a line not
    // from the assembler
    Ends: int[]
    // translate's index, built on first use; it names the Text,
    // Segments and Ends it was built from, so a copy never reads a
    // stale one
    mutable PosIndex: obj
} with

    override this.Equals(other) =
        match other with
        | :? LogicalLine as o -> this.Text = o.Text && this.Head = o.Head && this.Segments = o.Segments
        | _ -> false

    override this.GetHashCode() =
        hash (this.Text, this.Head, this.Segments)

// a one-physical-line LogicalLine — the REPL's and -e's spelling
let singleLine (text: string) : LogicalLine = {
    Text = text
    Head = 1
    Segments = [ (0, 1, 0) ]
    Ends = [||]
    PosIndex = null
}

// Block lets — F# light syntax at the assembly layer, the same way F#'s own
// lexer implements it (token insertion at offside boundaries): a continuation
// line beginning with `let` opens a binding; the next line at the same
// indentation closes it by joining with " in " instead of " ", so the
// single-line grammar sees the explicit form. `|`-headed lines are inert to
// the stack only while it is empty (statement-level pipeline continuations
// and column-0 match arms — the two customers); with a binding pending they
// follow the plain indent rules, so a dedented arm inside a block is the
// same "needs a body" error F# gives the shape. Every pending let must be
// closed before the statement ends.
// Unbalanced ( and { closers for a text fragment — the completion
// repair path appends these so a mid-edit dangling line parses.
// Quote-aware via its own stack machine, not stringScan: interp `$"`
// holes reopen code land mid-string, which a flag-based scanner cannot
// model — the four string-kind close rules are re-implemented here by
// necessity (and stringScan reads `$"…"` as a plain string).
let closers (text: string) : string =
    // one stack of expected closers models the full nesting: brackets
    // in code, strings ('"' plain, '$' interp — closes with '"' but
    // '{' opens a hole back into code land), single quotes, holes.
    // Mid-edit dangling text closes correctly at any depth.
    let mutable stack: char list = []
    let mutable i = 0

    while i < text.Length do
        let c = text[i]

        match stack with
        | ('"' | '$') :: rest ->
            if c = '\\' && i + 1 < text.Length then
                i <- Scan.escapeEnd text i
            elif c = '"' then
                stack <- rest
            elif c = '{' && List.head stack = '$' then
                if i + 1 < text.Length && text[i + 1] = '{' then
                    i <- i + 1
                else
                    stack <- '}' :: stack
        // raw kinds [D:raw-strings]: 'V' verbatim ("" stays inside),
        // 'T' triple (closes only at """)
        | 'V' :: rest ->
            if c = '"' then
                if i + 1 < text.Length && text[i + 1] = '"' then
                    i <- i + 1
                else
                    stack <- rest
        | 'T' :: rest ->
            if c = '"' && i + 2 < text.Length && text[i + 1] = '"' && text[i + 2] = '"' then
                i <- i + 2
                stack <- rest
        | '\'' :: rest ->
            if c = '\'' then
                stack <- rest
        | _ ->
            if c = '"' && i + 2 < text.Length && text[i + 1] = '"' && text[i + 2] = '"' then
                stack <- 'T' :: stack
                i <- i + 2
            elif c = '"' then
                stack <-
                    (if i > 0 && text[i - 1] = '@' then 'V'
                     elif i > 0 && text[i - 1] = '$' then '$'
                     else '"')
                    :: stack
            elif c = '\'' && not (Scan.isTyvarTick text i) then
                stack <- '\'' :: stack
            elif c = '(' then
                stack <- ')' :: stack
            elif c = '{' then
                stack <- '}' :: stack
            elif (c = ')' || c = '}') then
                match stack with
                | top :: rest when top = c -> stack <- rest
                | _ -> ()

        i <- i + 1

    stack
    |> List.map (fun c ->
        match c with
        | '$'
        | 'V' -> "\""
        | 'T' -> "\"\"\""
        | c -> string c)
    |> String.concat ""

// Still-open brackets (kind, column) after folding a line into the
// running stack — fmt's only caller; parens ride it too, since inside
// any open bracket fmt shifts rather than re-levels [D:fmt-bracket-shift]
// (quote-aware via the scanner family; lives here per the rule).
let braceStack (prev: (char * int) list) (line: string) : (char * int) list =
    // rides the one scanner [D:one-scanner]
    foldOutsideStrings
        (fun stack i c ->
            match c with
            | '{'
            | '['
            | '(' -> (c, i) :: stack
            // fmt drops a paren the offside rule has already closed, so a
            // `)` pops only its own kind
            | ')' ->
                (match stack with
                 | ('(', _) :: rest -> rest
                 | _ -> stack)
            | '}'
            | ']' ->
                (match stack with
                 | _ :: rest -> rest
                 | [] -> [])
            | _ -> stack)
        prev
        line

// Net ( / ) delta of a piece (scanner-aware) — compounds opened
// inside parens are pruned when the user's own closer buries them
// [D:compound-paren-prune]
let parenDelta (s: string) : int =
    foldOutsideStrings
        (fun d _ c ->
            (if c = '(' then d + 1
             elif c = ')' then d - 1
             else d))
        0
        s

// Fold a piece's brackets into the pending statement's open-bracket
// stack (kind, line, firstEntryCol) [D:multiline-brackets]. A bracket
// with content after its opener on the same line records that content's
// physical column as the sibling-entry anchor [D:field-alignment]; a
// dangling opener records None (the first continuation entry sets it).
// A mismatched closer is an error naming both sides; over-closing stays
// permissive (the parser owns that message). Parens are not tracked.
let bracketFold
    (lineNo: int)
    (indent: int)
    (stack: (char * int * int option) list)
    (piece: string)
    : Result<(char * int * int option) list, string> =
    foldOutsideStrings
        (fun acc i c ->
            match acc with
            | Error _ -> acc
            | Ok stack ->
                match c with
                | '{'
                | '[' ->
                    let entryCol =
                        // `{|` is one opener token [D:anon-literals] — the
                        // `|` is not entry content, and `|}` closes from
                        // the entry-content side too
                        let mutable j = i + 1

                        if c = '{' && j < piece.Length && piece[j] = '|' then
                            j <- j + 1

                        while j < piece.Length && piece[j] = ' ' do
                            j <- j + 1

                        // an update header's opener-line content is the
                        // source, not a field — the first field after `with`
                        // anchors, on this line or (dangling `with`) the next
                        // [D:field-alignment] [D:record-update]. Only a record
                        // brace can carry a `with`. The scan is bounded: it
                        // stops at the first top-level `=`, `;`, opener or
                        // closer, so a bracket-heavy single line stays linear
                        // [D:assemble-quadratic].
                        let withField =
                            if c <> '{' then
                                None
                            else
                                let mutable k = j
                                let mutable depth = 0
                                let mutable found = None
                                let mutable stop = false

                                while not stop && k < piece.Length do
                                    let ch = piece[k]

                                    if ch = '"' then
                                        k <- k + 1

                                        while k < piece.Length && piece[k] <> '"' do
                                            if piece[k] = '\\' then
                                                k <- k + 1

                                            k <- k + 1
                                    elif ch = '(' then
                                        depth <- depth + 1
                                    elif ch = ')' then
                                        depth <- depth - 1

                                        if depth < 0 then
                                            stop <- true
                                    elif
                                        depth = 0
                                        && (ch = '=' || ch = ';' || ch = '{' || ch = '}' || ch = '[' || ch = ']')
                                    then
                                        stop <- true
                                    elif
                                        depth = 0
                                        && ch = 'w'
                                        && k > j
                                        && piece[k - 1] = ' '
                                        && k + 3 < piece.Length
                                        && piece[k + 1] = 'i'
                                        && piece[k + 2] = 't'
                                        && piece[k + 3] = 'h'
                                        && (k + 4 = piece.Length || piece[k + 4] = ' ')
                                    then
                                        let mutable f = k + 4

                                        while f < piece.Length && piece[f] = ' ' do
                                            f <- f + 1

                                        found <- Some f
                                        stop <- true

                                    k <- k + 1

                                found

                        match withField with
                        | Some f when f >= piece.Length -> None
                        | Some f -> Some(indent + f)
                        | None when j < piece.Length && piece[j] <> '}' && piece[j] <> ']' -> Some(indent + j)
                        | None -> None

                    Ok((c, lineNo, entryCol) :: stack)
                | '}'
                | ']' ->
                    let expected = if c = '}' then '{' else '['

                    (match stack with
                     | (o, _, _) :: rest when o = expected -> Ok rest
                     | (o, oline, _) :: _ -> Error $"line {lineNo}: '{c}' closes the '{o}' opened at line {oline}"
                     | [] -> Ok [])
                | _ -> Ok stack)
        (Ok stack)
        piece

// Pending-statement state. Compounds is the offside stack: each open
// if/match-headed piece as (headIndent, textStart). A sibling arriving
// at or left of a head closes that compound by paren-wrapping it — a
// balanced, line-structural unit — so same-level siblings sequence
// after the conditional while deeper lines still join into its body,
// where greedy `;` grouping is exactly right. `else` and `|` pieces
// extend a compound instead of closing it. BraceDepth > 0 puts the
// assembler in record-continuation mode: line breaks separate fields,
// every other joining rule is inert (records are expressions).

// The pending statement's growing text [D:assemble-quadratic]: a single
// StringBuilder mutated in place, so a continuation-line flood joins in
// amortized-linear time (the old `ll.Text + sep + piece` rebuilt the whole
// string per join → O(N²) on hundreds-of-KB inputs). The immutable
// LogicalLine.Text is materialized once at statement close (`bufToLL`);
// every join site threads a PendBuf instead. Span arithmetic is byte-
// identical: `joinedStart` derives from `Sb.Length` at the same point the
// old code read `ll.Text.Length`, and the separators are the same literals.
//
// The incremental indexes answer the assembler's hot queries in O(1)/
// O(suffix) instead of a full-text rescan per line:
//   LastNonWs   — index of the last non-whitespace char (or -1): the
//                 bracket-continuation `prev.TrimEnd()` predicates read it
//                 (last char, EndsWith, Length) without slicing.
//   LastSegStart — index just past the last sibling sentinel (or 0): the
//                 proc/serve binder-head scan runs over the last segment
//                 only, never the whole growing text (LastIndexOf was O(N)).
type private PendBuf = {
    Sb: System.Text.StringBuilder
    // the statement's first physical line (LogicalLine.Head)
    Head: int
    Segments: (int * int * int) list
    // where each segment's own text ends (aligned with Segments): the
    // synthetic join text after it is no physical column
    Ends: int list
    // index of the last non-whitespace char in Sb, or -1
    LastNonWs: int
    // index just past the last sibling sentinel (Parser.sibSep) in Sb, or 0
    LastSegStart: int
}

type private District = {
    MarkerIndent: int
    MarkerLine: int
    Opener: string
    Strip: int
    Yaml: bool
    // the marker kind that armed this district — errors name its word
    Marker: MarkerKind
    Active: int option
    // a heredoc's outdented `//` line, transparent only if the block
    // ends there — further content makes it an outdent error
    OutdentedComment: int option
}

// content after an outdented heredoc comment: the comment sat inside the
// block, where every line is content — refuse like any outdent
let private heredocCommentGap (dst: District) : string option =
    match dst.Marker, dst.OutdentedComment with
    | MarkerKind.Heredoc, Some n ->
        Some
            $"line {n}: this comment line outdents below the heredoc's first line — inside a heredoc it is content: indent it to keep it, or move the comment out of the block"
    | _ -> None

// a marker-ending line arms its district, whatever kind of line it is —
// a plain head, a `| pat ->` arm, an `else` [D:arm-blocks]
let private armDistrict (marker: MarkerKind) (indent: int) (lineNo: int) : District option =
    markerOpener marker
    |> Option.map (fun (opener, strip, isYaml) -> {
        MarkerIndent = indent
        MarkerLine = lineNo
        Opener = opener
        Strip = strip
        Yaml = isYaml
        Marker = marker
        Active = None
        OutdentedComment = None
    })

type private Pend = {
    Buf: PendBuf
    Lets: (int * int) list
    LastIndent: int
    // sibling pipe columns, innermost first [D:pipe-alignment]: a
    // consecutive `|` line must sit exactly on a group column; the
    // first pipe after a non-pipe line opens a group. Per group:
    // (groupCol, bodyCol, isForward, parenDepth) [D:match-pipe-offside]. isForward =
    // a `|>` group vs a bare `|` (match/union arm) group; bodyCol = the
    // arm body's column (after the arm `->`; for a dangling arm resolved
    // to the body line's indent when it arrives; MaxValue until then).
    // A forward `|>` landing on a bare arm group: at the `|` it closes
    // the match, at or under the body it extends the arm, left of the
    // body it is rejected.
    // The 4th element is the paren depth at open: a line whose closers
    // drop below it ends the group — `(match … | _ -> x)` leaves no arm
    // group behind [D:compound-paren-prune]
    PipeGroups: (int * int * bool * int) list
    LastWasPipe: bool
    District: District option
    // (headIndent, textStart, parenDepthAtOpen) [D:compound-paren-prune]
    Compounds: (int * int * int) list
    ParenDepth: int
    // the indent where the current statement started (statement = a
    // sibling/`in` join, or the first line after a dangling head) —
    // the lambda pop restores to this level, so a block sibling after
    // the `)` sequences while a fold's init still applies
    // [D:multiline-lambda]
    StmtLevel: int
    PrevDangles: bool
    // open multiline lambdas (opening line, opener indent, paren depth
    // before the open, statement level to restore on pop), innermost
    // first [D:multiline-lambda] — popped by paren balance; the user's
    // `)` is the closer
    Lambdas: (int * int * int * int) list
    // still-open brackets (kind, opening line, sibling-entry column)
    // [D:multiline-brackets] [D:field-alignment]
    Brackets: (char * int * int option) list
    // open lambda bodies of bracket entries (bracket depth, entry column,
    // statement level to restore when the entry ends), innermost first
    // [D:bracket-lambda-body] — the body's lines read as statements; the
    // next entry, the closer, or a `}` attached to the body closes it
    Bodies: (int * int * int) list
}

// The join algebra: every way a continuation line attaches to the
// pending statement, its inserted text in one place. joinedStart
// derives from the same strings, so span arithmetic cannot drift from
// the insertion.
type private Join =
    | JIn // let-close: text + " in " + piece
    | JSibling // bracket field/element separators [D:field-sep-sentinel]
    | JStmtSibling // statement-sibling sequencing [D:sibling-sentinel]:
    // " <sentinel> " — same width as " ; " so span arithmetic is
    // identical, but command mode stops at it (a user ';' does not)
    | JSpace // plain continuation: " "
    | JDistrictOpen of strip: int * opener: string // strip the armed marker, wrap
    | JDistrictSibling of opener: string // text + " ; " + opener + piece + ")"
    | JDistrictPipe // reopen the wrap: stem + " " + piece + ")"
    | JYamlLine of rel: int // sentinel + rel spaces + VERBATIM line [D:yaml-district]

// recompute LastNonWs / LastSegStart for the chars appended from `at`
// onward (the buffer below `at` is unchanged, so the old indexes carry
// unless the tail overwrote them — callers pass the smaller of the two)
let private scanTail (sb: System.Text.StringBuilder) (from: int) (seedNonWs: int) (seedSegStart: int) =
    let mutable nonWs = seedNonWs
    let mutable segStart = seedSegStart

    for i in from .. sb.Length - 1 do
        let c = sb[i]

        if c = Parser.sibSep then
            segStart <- i + 1

        if not (System.Char.IsWhiteSpace c) then
            nonWs <- i

    nonWs, segStart

// append `s` to the buffer, maintaining the incremental indexes over the
// newly-added region (the existing buffer is untouched, so its indexes stand)
let private bufAppend (b: PendBuf) (s: string) : PendBuf =
    let from = b.Sb.Length
    b.Sb.Append s |> ignore
    let nonWs, segStart = scanTail b.Sb from b.LastNonWs b.LastSegStart

    {
        b with
            LastNonWs = nonWs
            LastSegStart = segStart
    }

// the joins append their parts straight into the buffer — no joined
// string per line — then index the added region exactly as bufAppend does
let private bufAppended (b: PendBuf) (from: int) : PendBuf =
    let nonWs, segStart = scanTail b.Sb from b.LastNonWs b.LastSegStart

    {
        b with
            LastNonWs = nonWs
            LastSegStart = segStart
    }

let private siblingJoinStr = " " + Parser.fieldSepStr + " "
let private stmtSiblingJoinStr = " " + Parser.sibSepStr + " "

let private bufLen (b: PendBuf) : int = b.Sb.Length

// the TrimEnd'd length of the buffer (LastNonWs + 1), and whether the
// TrimEnd'd buffer ends with `suffix` — both answered from the tracked
// last-non-white index, so the bracket-continuation predicates cost
// O(suffix) instead of a full-text TrimEnd allocation per line
// [D:assemble-quadratic]
let private bufTrimEndLen (b: PendBuf) : int = b.LastNonWs + 1

let private bufTrimEndEndsWith (b: PendBuf) (suffix: string) : bool =
    let sb = b.Sb
    let last = b.LastNonWs
    let n = suffix.Length

    last + 1 >= n
    && (let mutable ok = true
        let mutable i = 0

        while ok && i < n do
            if sb[last - n + 1 + i] <> suffix[i] then
                ok <- false

            i <- i + 1

        ok)

// the last non-white char of the buffer (the caller guards emptiness via
// bufTrimEndLen > 0)
let private bufLastNonWsChar (b: PendBuf) : char = b.Sb[b.LastNonWs]

// does the buffer, from index `at`, start with `prefix`? (the within-tail
// wrap check, O(prefix) with no Substring allocation)
let private bufStartsWithAt (b: PendBuf) (at: int) (prefix: string) : bool =
    let sb = b.Sb

    at >= 0
    && sb.Length - at >= prefix.Length
    && (let mutable ok = true
        let mutable i = 0

        while ok && i < prefix.Length do
            if sb[at + i] <> prefix[i] then
                ok <- false

            i <- i + 1

        ok)

// does the buffer start with `prefix`? (O(prefix), no allocation) — the
// `type ` / `[<` head checks
let private bufStartsWith (b: PendBuf) (prefix: string) : bool =
    let sb = b.Sb

    sb.Length >= prefix.Length
    && (let mutable ok = true
        let mutable i = 0

        while ok && i < prefix.Length do
            if sb[i] <> prefix[i] then
                ok <- false

            i <- i + 1

        ok)

// a declaration's buffer may carry its own-line attribute list ahead of
// `type` (the attr-only join, [D:attr-positions]) — the record-body
// district reads through it [D:derived-ord]
let private bufIsTypeDecl (b: PendBuf) : bool =
    bufStartsWith b "type "
    || (bufStartsWith b "[<"
        && (let mutable i = 2
            let mutable found = false

            while not found && i < b.Sb.Length do
                if bufStartsWithAt b i ">] type " then
                    found <- true

                i <- i + 1

            found))

// the buffer's last segment (after the last sibling sentinel), O(segment)
// via the tracked LastSegStart — the proc/serve binder-head scan reads
// this instead of LastIndexOf over the whole growing text [D:assemble-quadratic]
let private bufLastSeg (b: PendBuf) : string =
    b.Sb.ToString(b.LastSegStart, b.Sb.Length - b.LastSegStart)

let private bufLastSegContains (b: PendBuf) (marker: string) : bool =
    let len = b.Sb.Length - b.LastSegStart

    len >= marker.Length
    && (let pool = System.Buffers.ArrayPool<char>.Shared
        let chars = pool.Rent len

        try
            b.Sb.CopyTo(b.LastSegStart, System.Span<char>(chars, 0, len), len)

            System.MemoryExtensions.IndexOf(
                System.ReadOnlySpan<char>(chars, 0, len),
                System.MemoryExtensions.AsSpan marker,
                System.StringComparison.Ordinal
            )
            >= 0
        finally
            pool.Return chars)

// a bare `repeat` head (no options) [D:repeat-missing-interval]: its body
// joins sentineled, so the parser sees the options are missing instead of
// reading the first body line as the options atom
let private bufEndsInBareRepeat (b: PendBuf) : bool =
    bufTrimEndLen b = 6 && bufTrimEndEndsWith b "repeat"
    || bufTrimEndEndsWith b " repeat"
    || bufTrimEndEndsWith b "(repeat"

let private bufEndsInProcHead (b: PendBuf) : bool =
    bufLastSegContains b "within proc "
    && endsInBinderHeadSeg true "within proc " (bufLastSeg b)

let private bufEndsInServeHead (b: PendBuf) : bool =
    bufLastSegContains b "within serve "
    && endsInBinderHeadSeg false "within serve " (bufLastSeg b)

// `within env A=1 …` [D:within-env-pairs]: its block joins sentineled too,
// so a first body line that is itself `B=2 cmd` stays that command's own
// prefix instead of widening the scope (the binder-head shape: an
// identifier glued to `=`; a bound `within env e` keeps the space join)
let private bufEndsInEnvHead (b: PendBuf) : bool =
    bufLastSegContains b "within env "
    && endsInBinderHeadSeg true "within env " (bufLastSeg b)

// materialize a LogicalLine from a finished pending buffer (segments are
// reversed at the boundary exactly as before)
let private bufToLL (b: PendBuf) : LogicalLine = {
    Text = b.Sb.ToString()
    Head = b.Head
    Segments = List.rev b.Segments
    Ends = List.rev b.Ends |> List.toArray
    PosIndex = null
}

// a line's segments re-pointed at the physical indents in `indents`
// (line -> leading spaces): assembly recovery re-indents a misaligned
// line, and every position map must read the source, not the repair
// [D:recovery-physical-indent]
let withPhysicalIndents (indents: Map<int, int>) (ll: LogicalLine) : LogicalLine =
    if ll.Segments |> List.exists (fun (_, l, _) -> indents.ContainsKey l) then
        {
            ll with
                Segments =
                    ll.Segments
                    |> List.map (fun (js, l, i) ->
                        match indents.TryFind l with
                        | Some phys -> js, l, phys
                        | None -> js, l, i)
                PosIndex = null
        }
    else
        ll

// a fresh single-line buffer (the assembler's per-statement seed)
let private bufNew (text: string) (lineNo: int) (indent: int) : PendBuf =
    let sb = System.Text.StringBuilder(text)
    let nonWs, segStart = scanTail sb 0 -1 0

    {
        Sb = sb
        Head = lineNo
        Segments = [ (0, lineNo, indent) ]
        Ends = [ text.Length ]
        LastNonWs = nonWs
        LastSegStart = segStart
    }

let private applyJoin (j: Join) (b: PendBuf) (piece: string) (lineNo: int) (indent: int) : PendBuf =
    // joinedStart derives from bufLen at the same point the old code read
    // ll.Text.Length — byte-identical span arithmetic [D:assemble-quadratic]
    let joinedStart =
        match j with
        | JIn -> bufLen b + " in ".Length
        | JSibling -> bufLen b + (" " + Parser.fieldSepStr + " ").Length
        | JStmtSibling -> bufLen b + (" " + Parser.sibSepStr + " ").Length
        | JSpace -> bufLen b + " ".Length
        | JDistrictOpen(strip, opener) -> (bufLen b - strip) + opener.Length
        | JDistrictSibling opener -> bufLen b + (" ; " + opener).Length
        | JDistrictPipe -> (bufLen b - 1) + " ".Length
        | JYamlLine rel -> bufLen b + (Parser.sibSepStr + String(' ', rel)).Length

    // apply the text mutation (identical bytes to the old string algebra)
    let b =
        match j with
        | JIn ->
            let from = b.Sb.Length
            b.Sb.Append(" in ").Append(piece) |> ignore
            bufAppended b from
        | JSibling ->
            let from = b.Sb.Length
            b.Sb.Append(siblingJoinStr).Append(piece) |> ignore
            bufAppended b from
        | JStmtSibling ->
            let from = b.Sb.Length
            b.Sb.Append(stmtSiblingJoinStr).Append(piece) |> ignore
            bufAppended b from
        | JSpace ->
            let from = b.Sb.Length
            b.Sb.Append(' ').Append(piece) |> ignore
            bufAppended b from
        | JDistrictOpen(strip, opener) ->
            // strip the armed marker's trailing chars, then wrap. This arm
            // fires once per district (the first content line), so the full
            // rescan-after-remove is not on any quadratic path.
            if strip > 0 then
                b.Sb.Remove(b.Sb.Length - strip, strip) |> ignore

            let nonWs, segStart = scanTail b.Sb 0 -1 0

            let b = {
                b with
                    LastNonWs = nonWs
                    LastSegStart = segStart
                    Ends = b.Ends |> List.map (min b.Sb.Length)
            }

            let from = b.Sb.Length
            b.Sb.Append(opener).Append(piece).Append(')') |> ignore
            bufAppended b from
        | JDistrictSibling opener ->
            let from = b.Sb.Length
            b.Sb.Append(" ; ").Append(opener).Append(piece).Append(')') |> ignore
            bufAppended b from
        | JDistrictPipe ->
            // only on a `|` continuation line inside a district — not a
            // quadratic path; rescan the (now one-shorter) buffer whole
            b.Sb.Remove(b.Sb.Length - 1, 1) |> ignore
            let nonWs, segStart = scanTail b.Sb 0 -1 0

            let b = {
                b with
                    LastNonWs = nonWs
                    LastSegStart = segStart
                    Ends = b.Ends |> List.map (min b.Sb.Length)
            }

            let from = b.Sb.Length
            b.Sb.Append(' ').Append(piece).Append(')') |> ignore
            bufAppended b from
        | JYamlLine rel ->
            let from = b.Sb.Length
            b.Sb.Append(Parser.sibSep).Append(' ', rel).Append(piece) |> ignore
            bufAppended b from

    // the piece ends before a district's closing ")"
    let pieceEnd =
        match j with
        | JDistrictOpen _
        | JDistrictSibling _
        | JDistrictPipe -> bufLen b - 1
        | _ -> bufLen b

    {
        b with
            Segments = (joinedStart, lineNo, indent) :: b.Segments
            Ends = pieceEnd :: b.Ends
    }

// onMisaligned (line, siblings' column) fires before a sibling-alignment
// error — the recovering assembler re-indents that line instead of
// dropping it, so its closers survive (no cascading "still open")
let assembleWith (onMisaligned: int -> int -> unit) (numbered: (int * string) list) : Result<LogicalLine list, string> =
    // trailing comments strip here, per physical line [D:trailing-comments]:
    // stripComment is the whitespace-preceded rule (glued // — http://a,
    // --format=a//b — stays data). Skipped: yaml district content (bytes)
    // and comment-only lines (their class carries transparency semantics
    // a blanked line would lose — blankSinceHead is the difference)
    // each line as (number, text, code): the text is the line as the
    // joins see it, the code its comment-cut form (the cut is idempotent,
    // so the code of a cut text is the text)
    let numbered =
        // the mask must see stripped heads (a commented district head
        // still opens its district), so the cut runs twice: once to
        // find the content regions, once — content excluded — for real.
        // TrimEnd only on actual cuts: untouched lines stay byte-equal
        let arr = List.toArray numbered
        let codes = arr |> Array.map (fun (_, raw) -> stripComment raw)
        let mask = districtContentMaskOf codes codes

        List.init arr.Length (fun i ->
            let n, raw = arr[i]
            let code = codes[i]

            if (i < mask.Length && mask[i]) || System.String.IsNullOrWhiteSpace code then
                n, raw, code
            else
                n, code, code)

    // the retired ! districts get an explanatory error
    // [D:district-retirement] — checked up front over every non-content
    // line (yaml district bodies are bytes, never read:
    // districtContentMask)
    let retiredHit =
        let arr = List.toArray numbered
        let texts = arr |> Array.map (fun (_, text, _) -> text)
        let codes = arr |> Array.map (fun (_, _, code) -> code)
        let mask = districtContentMaskOf texts codes

        let mutable hit = None
        let mutable i = 0

        while hit.IsNone && i < arr.Length do
            if not (i < mask.Length && mask[i]) then
                let t = codes[i].TrimEnd()

                if t <> "" && retiredDistrictMarker t then
                    let n, _, _ = arr[i]
                    hit <- Some n

            i <- i + 1

        hit

    match retiredHit with
    | Some n ->
        Error
            $"line {n}: a line-end '!' is not a district marker — commands are ordinary statements (drop the !); for an env overlay over a block, use `within env vars`"
    | None ->
        let noBody letLine =
            Error
                $"line {letLine}: this let needs a body — an expression at the same indentation must follow before the statement ends"

        let braceOpen (p: Pend) =
            match p.Brackets with
            | ('{', line, _) :: _ when bufIsTypeDecl p.Buf ->
                Error $"line {line}: this record type's {{ is still open when the statement ends — close the brace"
            | ('{', line, _) :: _ ->
                Error $"line {line}: this record literal's {{ is still open when the statement ends — close the brace"
            | (kind, line, _) :: _ ->
                Error $"line {line}: this list's {kind} is still open when the statement ends — close the bracket"
            | [] -> Error "unreachable: bracketOpen on an empty stack"

        let close (current: Pend option) acc =
            match current with
            | Some p when not p.Brackets.IsEmpty -> braceOpen p
            | Some { Lambdas = (oline, _, _, _) :: _ } ->
                Error $"line {oline}: this lambda's '(' is still open when the statement ends — close the paren"
            | Some {
                       District = Some {
                                           Active = None
                                           MarkerLine = mLine
                                           Marker = m
                                       }
                   } ->
                let what =
                    match m with
                    | MarkerKind.Heredoc -> "'<<<'"
                    | _ -> "'yaml'"

                Error $"line {mLine}: line-end {what} needs an indented block below it"
            | Some { Lets = (_, letLine) :: _ } -> noBody letLine
            | Some p -> Ok(bufToLL p.Buf :: acc)
            | None -> Ok acc

        let districtLineCheck lineNo (cls: PieceClass) =
            if cls.Kind = PieceKind.LetHead then
                Error $"line {lineNo}: district lines are commands; bind values outside the block"
            else
                Ok()

        // paren-wrap the compound starting at textStart; later segment
        // starts shift by the inserted "(" (remaining compounds all start
        // earlier — pops run deepest-first — so they never shift). The buffer
        // Insert/Append is byte-identical to the old Substring algebra; the
        // trailing ")" becomes the last non-white char [D:assemble-quadratic]
        let wrapFrom (b: PendBuf) (ts: int) : PendBuf =
            b.Sb.Insert(ts, "(") |> ignore
            b.Sb.Append ")" |> ignore

            {
                b with
                    LastNonWs = b.Sb.Length - 1
                    LastSegStart =
                        (if b.LastSegStart >= ts then
                             b.LastSegStart + 1
                         else
                             b.LastSegStart)
                    Segments =
                        b.Segments
                        |> List.map (fun (js, l, i) -> (if js >= ts then js + 1 else js), l, i)
                    Ends = List.map2 (fun (js, _, _) e -> if js >= ts || e > ts then e + 1 else e) b.Segments b.Ends
            }

        let folded =
            numbered
            |> List.fold
                (fun state (lineNo, raw, code) ->
                    match state with
                    | Error e -> Error e
                    | Ok _ when
                        raw.Contains Parser.sibSep
                        || raw.Contains Parser.districtClose
                        || raw.Contains Parser.fieldSep
                        ->
                        // unproduceability [D:sibling-sentinel] [D:district-terminates]:
                        // the machine sibling token and the district-close
                        // terminator can never come from source — reject both
                        // at the one place text becomes logical lines
                        Error $"line {lineNo}: illegal control character in source"
                    | Ok(current, acc, blankSinceHead) ->
                        let inOpenBrace =
                            match current with
                            | Some p -> not p.Brackets.IsEmpty
                            | None -> false

                        // the col-0 rule suspends while a lambda's paren is
                        // open [D:multiline-lambda]: the closer (or the leak
                        // guard) owns those lines
                        let inOpenLambda =
                            match current with
                            | Some p -> not p.Lambdas.IsEmpty
                            | None -> false

                        if System.String.IsNullOrWhiteSpace raw then
                            match current with
                            // inside an active yaml district a blank line is
                            // bytes [D:block-scalars] — a block scalar's
                            // content keeps it, so it rides as an empty
                            // verbatim line; the template parser skips blanks
                            // everywhere outside a block scalar's content
                            | Some({
                                       District = Some { Active = Some _; Yaml = true }
                                   } as p) ->
                                Ok(
                                    Some {
                                        p with
                                            Buf = applyJoin (JYamlLine 0) p.Buf "" lineNo 0
                                    },
                                    acc,
                                    blankSinceHead
                                )
                            // transparency is total while a statement pends
                            // [D:body-blanks] — the comment-line class, second
                            // member; the col-0 rule (plus EOF) is the sole
                            // statement boundary, so every error the blank
                            // boundary produced still fires at close
                            | Some p -> Ok(Some p, acc, blankSinceHead)
                            | None -> Ok(None, acc, true)
                        elif System.String.IsNullOrWhiteSpace code then
                            // comment-only: transparent [D:comment-transparency] —
                            // except inside an active yaml district, where the
                            // line is bytes (`// x` in a block scalar is data)
                            match current with
                            // a comment-shaped block line is content, so it
                            // obeys the content indentation rule
                            // [D:spaces-only-indent]
                            | Some {
                                       District = Some { Yaml = true; MarkerIndent = m }
                                   } when (leadingTabCol raw).IsSome && leadingSpacesTabs raw > m ->
                                Error(tabIndentError lineNo raw)
                            | Some({
                                       District = Some({
                                                           Active = Some bse
                                                           Yaml = true
                                                           MarkerIndent = m
                                                       } as dst)
                                   } as p) when (let ind = leadingSpaces raw in ind > m && ind >= bse) ->
                                let ind = leadingSpaces raw

                                if (heredocCommentGap dst).IsSome then
                                    Error (heredocCommentGap dst).Value
                                else
                                    Ok(
                                        Some {
                                            p with
                                                Buf =
                                                    applyJoin
                                                        (JYamlLine(ind - bse))
                                                        p.Buf
                                                        (raw.Substring ind)
                                                        lineNo
                                                        ind
                                        },
                                        acc,
                                        blankSinceHead
                                    )
                            // a comment-only FIRST content line is bytes in a
                            // HEREDOC [D:block-scalars]: it arms the district
                            // and fixes the block base exactly as any first
                            // line does — transparency fired here once, and a
                            // heredoc's leading `// header` line silently
                            // vanished from the value. Heredocs only: a yaml
                            // template strips `//` lines as its own comments,
                            // so a leading one stays transparent there (an
                            // inserted comment must not fix the block base —
                            // the fuzzer's comment-neutrality invariant)
                            | Some({
                                       District = Some({
                                                           Active = None
                                                           Yaml = true
                                                           Marker = MarkerKind.Heredoc
                                                           MarkerIndent = m
                                                       } as dst)
                                   } as p) when leadingSpaces raw > m ->
                                let ind = leadingSpaces raw

                                Ok(
                                    Some {
                                        p with
                                            Buf = applyJoin (JYamlLine 0) p.Buf (raw.Substring ind) lineNo ind
                                            LastIndent = ind
                                            District = Some { dst with Active = Some ind }
                                    },
                                    acc,
                                    blankSinceHead
                                )
                            // an outdented `//` inside an ACTIVE heredoc: transparent
                            // only if the block ends here; content after it makes it
                            // an outdent error (pasted code lost its comment)
                            | Some({
                                       District = Some({
                                                           Active = Some _
                                                           Yaml = true
                                                           Marker = MarkerKind.Heredoc
                                                           OutdentedComment = None
                                                       } as dst)
                                   } as p) ->
                                Ok(
                                    Some {
                                        p with
                                            District =
                                                Some {
                                                    dst with
                                                        OutdentedComment = Some lineNo
                                                }
                                    },
                                    acc,
                                    blankSinceHead
                                )
                            | Some p -> Ok(Some p, acc, blankSinceHead)
                            | None -> Ok(None, acc, blankSinceHead)
                        elif
                            raw[0] = ' '
                            || raw[0] = '\t'
                            || raw[0] = '|'
                            // a col-0 `until` continues its retry/poll —
                            // matching the col-0 `|` arm [D:retry-poll]
                            || raw.StartsWith "until "
                            || spanIs (trimmedEnd raw) "until"
                            // a col-0 `always` continues its bare within
                            // [D:within-always]
                            || spanIs (trimmedEnd raw) "always"
                            // a col-0 `else`/`elif` continues its `if` — the
                            // top-level block form; the ElseHead join below
                            // already handles it, only this gate excluded a
                            // dedented else/elif [D:toplevel-if-else]
                            || continuesOpenIf raw
                            || inOpenBrace
                            || inOpenLambda
                        then
                            let indent = leadingSpaces raw

                            // indentation is spaces only, block content
                            // included: a tab is content only after the
                            // line's first non-whitespace character
                            // [D:spaces-only-indent]
                            if (leadingTabCol raw).IsSome then
                                Error(tabIndentError lineNo raw)
                            else
                                match current with
                                | None ->
                                    if blankSinceHead then
                                        Error
                                            $"line {lineNo}: continuation after a blank line has no statement to continue"
                                    else
                                        Error $"line {lineNo}: continuation without a statement"
                                | Some p ->
                                    // structure decisions read the stripped text;
                                    // yaml-district joins carry the raw bytes
                                    let rawPiece = raw.Substring indent

                                    let piece =
                                        if obj.ReferenceEquals(raw, code) then
                                            rawPiece
                                        else
                                            code.Substring indent

                                    let cls = classifyPiece piece

                                    // close the lambda bodies of bracket entries that have
                                    // ended [D:bracket-lambda-body]: every body opened at a
                                    // bracket depth above `depth`. A block let still open
                                    // inside one has no body; an unclosed `(fun` inside one
                                    // names its paren; the body's compounds and arm groups
                                    // are gone; the statement level the outermost body saved
                                    // comes back
                                    let closeBodiesAbove (depth: int) (p: Pend) : Result<Pend, string> =
                                        let gone, kept = p.Bodies |> List.partition (fun (d, _, _) -> d > depth)

                                        match List.tryLast gone with
                                        | None -> Ok p
                                        | Some(_, entryCol, saved) ->
                                            match p.Lets |> List.tryFind (fun (k, _) -> k > entryCol) with
                                            | Some(_, letLine) -> noBody letLine
                                            | None ->
                                                match
                                                    p.Lambdas |> List.tryFind (fun (_, oi, _, _) -> oi >= entryCol)
                                                with
                                                | Some(oline, _, _, _) ->
                                                    Error
                                                        $"line {oline}: this lambda's '(' is still open when its field/element ends — close the paren"
                                                | None ->
                                                    Ok {
                                                        p with
                                                            Bodies = kept
                                                            StmtLevel = saved
                                                            Compounds =
                                                                p.Compounds
                                                                |> List.filter (fun (h, _, _) -> h <= entryCol)
                                                            PipeGroups =
                                                                p.PipeGroups
                                                                |> List.filter (fun (g, _, _, _) -> g <= entryCol)
                                                            LastWasPipe = false
                                                    }

                                    let rec go (p: Pend) =
                                        // a body is open for the innermost bracket when its
                                        // depth matches [D:bracket-lambda-body]
                                        let bodyOpen =
                                            match p.Bodies with
                                            | (d, _, _) :: _ -> d = p.Brackets.Length
                                            | [] -> false

                                        // a closer line ends its bracket whatever its indent
                                        let closesBracket =
                                            piece.StartsWith "]" || piece.StartsWith "}" || piece.StartsWith "|}"

                                        match p.Brackets with
                                        // the statement-head guard [D:blank-in-brackets]:
                                        // keywords cannot be entries, so a col-0
                                        // let/type bounds a runaway unclosed bracket
                                        | (kind, bline, _) :: _ when
                                            indent = 0 && (piece.StartsWith "let " || piece.StartsWith "type ")
                                            ->
                                            Error
                                                $"line {lineNo}: statement at column 0 while the '{kind}' opened at line {bline} is still open — close the bracket"
                                        // a line of an entry's lambda body is a statement line
                                        // [D:bracket-lambda-body]: block lets, siblings,
                                        // compounds, arms and nested lambdas read as they do
                                        // in any block; the entry column is the bracket's own
                                        | (_, _, Some entryCol) :: _ when
                                            bodyOpen
                                            && not closesBracket
                                            && (indent > entryCol || (cls.ClosesParen && not p.Lambdas.IsEmpty))
                                            ->
                                            stmt p
                                        | (_, _, _) :: _ when bodyOpen ->
                                            // the next entry or the closer ends the body
                                            closeBodiesAbove (p.Brackets.Length - 1) p |> Result.bind go
                                        | (kind, _, entryCol) :: _ ->
                                            // bracket continuation: a line break after a
                                            // field/element is a separator. The predicates
                                            // below read the pending buffer's tracked
                                            // last-non-white index — no full-text TrimEnd per
                                            // line [D:assemble-quadratic]
                                            let isTypeDecl = bufIsTypeDecl p.Buf

                                            // the separator goes before an entry-start line,
                                            // never before a value continuation — a field's
                                            // value may open on the next line.
                                            // Lists have no entry marker: every line starts an
                                            // element unless the previous line dangles an
                                            // opener/separator/operator [D:multiline-brackets]
                                            let startsEntry =
                                                // a closer line ends its bracket, never
                                                // starts an entry (Stroustrup closers)
                                                // [D:multiline-brackets]
                                                if piece.StartsWith "]" || piece.StartsWith "}" then false
                                                elif kind = '[' then true
                                                elif isTypeDecl then cls.StartsTypeField
                                                else cls.StartsField

                                            let prevEndsWith (s: string) = bufTrimEndEndsWith p.Buf s

                                            let danglesOpen =
                                                prevEndsWith "{"
                                                // the anon opener is one token [D:anon-literals] —
                                                // named here because the operator clause below
                                                // excludes `type` lines, where a nested anon
                                                // type may dangle it too
                                                || prevEndsWith "{|"
                                                || prevEndsWith "["
                                                || prevEndsWith ";"
                                                // an update header ends at `with`; the
                                                // first field after it is not a sibling
                                                // [D:record-update]
                                                || prevEndsWith " with"
                                                // a preceding-line attribute binds to its
                                                // field: no separator between them
                                                || prevEndsWith ">]"
                                                // a dangling operator/comma continues the
                                                // same element (wrapped elements) — but not in
                                                // type declarations, where a generic closer
                                                // (`Option<string>`) legitimately ends a field
                                                || (not (kind = '{' && isTypeDecl)
                                                    && bufTrimEndLen p.Buf > 0
                                                    && "+-*/,(<>=|&" |> Seq.contains (bufLastNonWsChar p.Buf))

                                            let join = if startsEntry && not danglesOpen then JSibling else JSpace

                                            // sibling entries align exactly [D:field-alignment]:
                                            // the first entry (opener-line content, or the first
                                            // continuation entry of a dangling opener) sets the
                                            // column; every later entry must hit it
                                            // an attribute and its field are one entry on two
                                            // lines — the `>]` dangle suppresses the separator,
                                            // never the alignment [D:field-alignment]
                                            let attrField = startsEntry && prevEndsWith ">]"

                                            let alignment =
                                                if
                                                    join = JSibling || attrField || (startsEntry && entryCol.IsNone)
                                                then
                                                    match entryCol with
                                                    | Some c when indent <> c ->
                                                        onMisaligned lineNo c

                                                        Error
                                                            $"line {lineNo}: this field/element is indented off its siblings (they sit at column {c}) — align the group exactly"
                                                    | Some _ -> Ok p.Brackets
                                                    | None ->
                                                        // dangling opener: this entry anchors it
                                                        (match p.Brackets with
                                                         | (k, l, None) :: rest -> Ok((k, l, Some indent) :: rest)
                                                         | other -> Ok other)
                                                else
                                                    Ok p.Brackets

                                            match alignment with
                                            | Error e -> Error e
                                            | Ok anchored ->
                                                bracketFold lineNo indent anchored piece
                                                |> Result.bind (fun brackets ->
                                                    let depth = p.ParenDepth + parenDelta piece

                                                    // a `(fun x ->` entry arms its lambda like any
                                                    // statement line, and a dangling lambda arrow
                                                    // opens the entry's body [D:bracket-lambda-body]
                                                    let lambdas =
                                                        if lambdaOpens piece then
                                                            (lineNo, indent, depth - 1, p.StmtLevel) :: p.Lambdas
                                                        else
                                                            p.Lambdas

                                                    let bodies =
                                                        match brackets with
                                                        | (_, _, Some c) :: _ when lambdaDangles piece ->
                                                            (brackets.Length, c, p.StmtLevel) :: p.Bodies
                                                        | _ -> p.Bodies

                                                    let joinedP = {
                                                        p with
                                                            Buf = applyJoin join p.Buf piece lineNo indent
                                                            LastIndent = indent
                                                            Brackets = brackets
                                                            ParenDepth = depth
                                                            PrevDangles = dangleOpensBlock piece
                                                            Lambdas = lambdas
                                                            Bodies = bodies
                                                    }

                                                    if not brackets.IsEmpty then
                                                        closeBodiesAbove brackets.Length joinedP
                                                        |> Result.map (fun q -> Some q, acc, blankSinceHead)
                                                    else
                                                        // the line closing the last bracket rejoins paren
                                                        // tracking: its parens move the depth, so `])` /
                                                        // `})` closes a multiline lambda exactly as a
                                                        // `)` line does, and a dangling `(fun x ->` after
                                                        // the closer opens one [D:multiline-lambda]
                                                        let depth = p.ParenDepth + parenDelta piece

                                                        let popped, kept =
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                        let innerLet =
                                                            match List.tryLast popped with
                                                            | Some(_, oindent, _, _) ->
                                                                p.Lets |> List.tryFind (fun (k, _) -> k > oindent)
                                                            | None -> None

                                                        match innerLet with
                                                        | Some(_, letLine) -> noBody letLine
                                                        | None ->
                                                            let backTo =
                                                                match popped with
                                                                | [] -> joinedP.LastIndent
                                                                | ps ->
                                                                    let (_, _, _, restore) = List.last ps
                                                                    restore

                                                            let stmtLevel =
                                                                if popped.IsEmpty then p.StmtLevel else backTo

                                                            let kept =
                                                                if lambdaOpens piece then
                                                                    (lineNo, indent, depth - 1, stmtLevel) :: kept
                                                                else
                                                                    kept

                                                            Ok(
                                                                Some {
                                                                    joinedP with
                                                                        LastIndent = backTo
                                                                        StmtLevel = stmtLevel
                                                                        ParenDepth = depth
                                                                        Lambdas = kept
                                                                        Compounds =
                                                                            p.Compounds
                                                                            |> List.filter (fun (_, _, d) ->
                                                                                d <= depth)
                                                                        PipeGroups =
                                                                            if popped.IsEmpty then
                                                                                p.PipeGroups
                                                                            else
                                                                                p.PipeGroups
                                                                                |> List.skipWhile (fun (g, _, _, _) ->
                                                                                    g > backTo)
                                                                                |> List.filter (fun (_, _, _, d) ->
                                                                                    d <= depth)
                                                                        LastWasPipe =
                                                                            if popped.IsEmpty then
                                                                                p.LastWasPipe
                                                                            else
                                                                                false
                                                                },
                                                                acc,
                                                                blankSinceHead
                                                            ))
                                        | [] -> stmt p

                                    and stmt (p: Pend) =
                                        match p.District with
                                        | Some({ Active = None; Yaml = true } as dst) when indent > dst.MarkerIndent ->
                                            // the first yaml line fixes the block base; it rides
                                            // at relative indent 0 [D:yaml-district]
                                            Ok(
                                                Some {
                                                    p with
                                                        Buf = applyJoin (JYamlLine 0) p.Buf rawPiece lineNo indent
                                                        LastIndent = indent
                                                        District = Some { dst with Active = Some indent }
                                                },
                                                acc,
                                                blankSinceHead
                                            )
                                        | Some({ Active = Some bse; Yaml = true } as dst) when
                                            indent > dst.MarkerIndent
                                            ->
                                            if (heredocCommentGap dst).IsSome then
                                                Error (heredocCommentGap dst).Value
                                            elif indent < bse then
                                                let noun =
                                                    match dst.Marker with
                                                    | MarkerKind.Heredoc -> "heredoc"
                                                    | _ -> "yaml"

                                                Error
                                                    $"line {lineNo}: this {noun} line outdents below the block's first line"
                                            else
                                                Ok(
                                                    Some {
                                                        p with
                                                            Buf =
                                                                applyJoin
                                                                    (JYamlLine(indent - bse))
                                                                    p.Buf
                                                                    rawPiece
                                                                    lineNo
                                                                    indent
                                                            LastIndent = indent
                                                    },
                                                    acc,
                                                    blankSinceHead
                                                )
                                        | Some({ Active = None } as dst) when indent > dst.MarkerIndent ->
                                            districtLineCheck lineNo cls
                                            |> Result.map (fun () ->
                                                Some {
                                                    p with
                                                        Buf =
                                                            applyJoin
                                                                (JDistrictOpen(dst.Strip, dst.Opener))
                                                                p.Buf
                                                                piece
                                                                lineNo
                                                                indent
                                                        LastIndent = indent
                                                        District = Some { dst with Active = Some indent }
                                                },
                                                acc,
                                                blankSinceHead)
                                        | Some {
                                                   Active = None
                                                   MarkerLine = mLine
                                                   Marker = m
                                               } ->
                                            let what =
                                                match m with
                                                | MarkerKind.Heredoc -> "'<<<'"
                                                | _ -> "'yaml'"

                                            Error $"line {mLine}: line-end {what} needs an indented block below it"
                                        | Some({ Active = Some d } as dst) when indent > dst.MarkerIndent ->
                                            if cls.Kind = PieceKind.PipeHead then
                                                Ok(
                                                    Some {
                                                        p with
                                                            Buf = applyJoin JDistrictPipe p.Buf piece lineNo indent
                                                            LastIndent = indent
                                                    },
                                                    acc,
                                                    blankSinceHead
                                                )
                                            elif indent = d then
                                                districtLineCheck lineNo cls
                                                |> Result.map (fun () ->
                                                    Some {
                                                        p with
                                                            Buf =
                                                                applyJoin
                                                                    (JDistrictSibling dst.Opener)
                                                                    p.Buf
                                                                    piece
                                                                    lineNo
                                                                    indent
                                                            LastIndent = indent
                                                    },
                                                    acc,
                                                    blankSinceHead)
                                            else
                                                Error
                                                    $"line {lineNo}: district lines are commands, one per line (use a leading | to continue a pipeline)"
                                        | Some dst ->
                                            // at or left of the marker while the statement still
                                            // pends [D:district-terminates]: the district closes and
                                            // the line reprocesses under the normal rules — a
                                            // following `|>`, `in`, or sibling composes with the
                                            // block's value. The flattened logical line carries no
                                            // content terminator, so without a marker the
                                            // continuation would glue into the last content line
                                            // (silent corruption); appending districtClose marks
                                            // the extent, and districtTail stops there so the
                                            // expression grammar resumes on the reprocessed line.
                                            go {
                                                p with
                                                    District = None
                                                    Buf = bufAppend p.Buf Parser.districtCloseStr
                                                    LastIndent = dst.MarkerIndent
                                            }
                                        // the multiline lambda's closer and leak guard
                                        // [D:multiline-lambda]: a `)`-headed line continues
                                        // the statement at any indent; any other line at or
                                        // left of the opener is a leak (the error names the
                                        // opener)
                                        | None when
                                            (match p.Lambdas with
                                             | (_, oindent, _, _) :: _ -> cls.ClosesParen || indent < oindent
                                             | [] -> false)
                                            ->
                                            let (oline, oindent, _, _) = List.head p.Lambdas

                                            if not cls.ClosesParen then
                                                // F#-parity: FS0058 is an error left of the
                                                // opener; at the opener's indent the line is a
                                                // body continuation (handled below)
                                                Error
                                                    $"line {lineNo}: this line sits left of the lambda '(' opened at line {oline} — close the paren first"
                                            else
                                                // a body let still needs its body before the paren
                                                match p.Lets |> List.tryFind (fun (k, _) -> k > oindent) with
                                                | Some(_, letLine) -> noBody letLine
                                                | None ->
                                                    let depth = p.ParenDepth + parenDelta piece

                                                    let popped, kept =
                                                        p.Lambdas |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                    let kept =
                                                        if lambdaOpens piece then
                                                            (lineNo, indent, depth - 1, p.StmtLevel) :: kept
                                                        else
                                                            kept

                                                    // restore the level the popped lambda's own
                                                    // statement started at
                                                    let backTo =
                                                        match popped with
                                                        | [] -> indent
                                                        | ps ->
                                                            let (_, _, _, restore) = List.last ps
                                                            restore

                                                    bracketFold lineNo indent p.Brackets piece
                                                    |> Result.bind (fun brackets ->
                                                        closeBodiesAbove brackets.Length {
                                                            p with
                                                                Buf =
                                                                    applyJoin
                                                                        (if
                                                                             bufEndsInProcHead p.Buf
                                                                             || bufEndsInServeHead p.Buf
                                                                             || bufEndsInEnvHead p.Buf
                                                                         then
                                                                             JStmtSibling
                                                                         else
                                                                             JSpace)
                                                                        p.Buf
                                                                        piece
                                                                        lineNo
                                                                        indent
                                                                LastIndent = backTo
                                                                StmtLevel = backTo
                                                                PrevDangles = dangleOpensBlock piece
                                                                ParenDepth = depth
                                                                Lambdas = kept
                                                                Compounds =
                                                                    p.Compounds
                                                                    |> List.filter (fun (_, _, d) -> d <= depth)
                                                                PipeGroups =
                                                                    p.PipeGroups
                                                                    |> List.skipWhile (fun (g, _, _, _) -> g > backTo)
                                                                    |> List.filter (fun (_, _, _, d) -> d <= depth)
                                                                LastWasPipe = false
                                                                Brackets = brackets
                                                        }
                                                        |> Result.map (fun q -> Some q, acc, blankSinceHead))
                                        | None ->
                                            if cls.Kind = PieceKind.PipeHead || cls.Kind = PieceKind.ElseHead then
                                                // arms, pipeline stages, and else extend the
                                                // current piece: no sibling `;` — but siblings
                                                // must align, and a shallower arm offside-closes
                                                // deeper compounds [D:pipe-alignment]
                                                let isUntil = piece = "until" || piece.StartsWith "until "
                                                let isAlways = piece = "always"
                                                let isFwd = piece.StartsWith "|>"

                                                match p.Lets with
                                                | (k, letLine) :: _ when indent <= k && not isUntil -> noBody letLine
                                                | _ ->
                                                    // deeper groups close at this line's column
                                                    let groups =
                                                        p.PipeGroups |> List.skipWhile (fun (g, _, _, _) -> g > indent)

                                                    // the arm body's column [D:match-pipe-offside]: the
                                                    // token after the arm `->` for an inline body;
                                                    // MaxValue for a dangling arm (resolved to the body
                                                    // line's indent when it arrives) or a `|`-decl with
                                                    // no arrow
                                                    let bodyCol =
                                                        match armArrowIndex piece with
                                                        | Some i ->
                                                            let after = piece.Substring(i + 2)
                                                            let t = after.TrimStart()

                                                            if t = "" then
                                                                System.Int32.MaxValue
                                                            else
                                                                indent + (i + 2) + (after.Length - t.Length)
                                                        | None -> System.Int32.MaxValue

                                                    // `closes` rides through to the wrap; `extends`
                                                    // joins the arm body and opens a forward group
                                                    let aligned =
                                                        if cls.Kind = PieceKind.ElseHead then
                                                            // else/elif keep their standing rules
                                                            Ok(false, groups)
                                                        elif
                                                            // a forward `|>` landing on a bare `|`
                                                            // (arm/union) group follows F#'s offside
                                                            // [D:match-pipe-offside], regardless of
                                                            // whether the arm body was inline or dangled
                                                            isFwd
                                                            && (match groups with
                                                                | (_, _, false, _) :: _ -> true
                                                                | _ -> false)
                                                        then
                                                            match groups with
                                                            | (g, _, _, _) :: _ when indent = g ->
                                                                // at the `|` column: close the match,
                                                                // pipe the whole
                                                                Ok(true, groups)
                                                            | (g, bc, _, _) :: _ when indent < bc ->
                                                                // left of the arm body: neither closes
                                                                // nor continues
                                                                let bodyDesc =
                                                                    if bc = System.Int32.MaxValue then
                                                                        "the arm body"
                                                                    else
                                                                        $"the arm body (column {bc})"

                                                                Error
                                                                    $"line {lineNo}: this pipe sits left of {bodyDesc} — put it at the '|' (column {g}) to pipe the whole match, or under the body to continue the arm"
                                                            | _ ->
                                                                // at or under the body: extend the arm
                                                                // body, opening a forward group
                                                                Ok(
                                                                    false,
                                                                    (indent, indent, true, p.ParenDepth) :: groups
                                                                )
                                                        elif not p.LastWasPipe || p.PrevDangles then
                                                            // first pipe after a non-pipe line
                                                            // opens a group — anchored at or
                                                            // right of the innermost open
                                                            // compound head (F#'s offside).
                                                            // A dangling pipe line (ending
                                                            // with/->/function) opens one too:
                                                            // its arms sit deeper by design
                                                            // [D:function-keyword]
                                                            match groups with
                                                            | (g, _, false, d) :: rest when g = indent && not isFwd ->
                                                                // a returning arm after a non-pipe
                                                                // body [D:match-pipe-offside]: the
                                                                // group at this exact column is the
                                                                // arm's own match — a deeper open
                                                                // compound (the previous arm's
                                                                // if/let tail) offside-closes below,
                                                                // it is not "its match"
                                                                Ok(false, (g, bodyCol, false, d) :: rest)
                                                            | _ ->
                                                                match p.Compounds with
                                                                | (h, _, _) :: _ when indent < h ->
                                                                    Error
                                                                        $"line {lineNo}: this arm sits left of its match (head at column {h}) — align arms at or right of it"
                                                                | _ ->
                                                                    Ok(
                                                                        false,
                                                                        (indent,
                                                                         (if isFwd then indent else bodyCol),
                                                                         isFwd,
                                                                         p.ParenDepth)
                                                                        :: groups
                                                                    )
                                                        else
                                                            match groups with
                                                            | (g, _, false, d) :: rest when g = indent && not isFwd ->
                                                                // a new arm aligns with its siblings:
                                                                // reset the group's body column to this
                                                                // arm's — each arm has its own body
                                                                // offside [D:match-pipe-offside]
                                                                Ok(false, (g, bodyCol, false, d) :: rest)
                                                            | (g, _, _, _) :: _ when g = indent ->
                                                                // aligned sibling (pipeline stage)
                                                                Ok(false, groups)
                                                            | (g, _, _, _) :: _ ->
                                                                onMisaligned lineNo g

                                                                Error
                                                                    $"line {lineNo}: this line is indented off its siblings (they sit at column {g}) — align the group exactly"
                                                            | [] ->
                                                                Error
                                                                    $"line {lineNo}: this line (column {indent}) is indented off its siblings — align the group exactly"

                                                    match aligned with
                                                    | Error e -> Error e
                                                    | Ok(closes, groups) ->
                                                        // a shallower arm closes compounds whose
                                                        // heads sit deeper (the nested-match
                                                        // return F# reads from the columns); a
                                                        // closing `|>` also wraps the head it sits
                                                        // on — `(match …) |> f` [D:match-pipe-offside]
                                                        let closeAt = if closes then indent - 1 else indent

                                                        let rec closeDeeper (b: PendBuf) compounds =
                                                            match compounds with
                                                            | (h, ts, _) :: rest when
                                                                h > closeAt
                                                                // a within head has no arm at its
                                                                // column [D:within-tail-pipe]: a pipe
                                                                // at the head closes the scope and
                                                                // pipes the whole — `(within …) |> f`,
                                                                // the match-close wrap one keyword over
                                                                || (h = indent
                                                                    && cls.Kind = PieceKind.PipeHead
                                                                    && bufStartsWithAt b ts "within ")
                                                                ->
                                                                closeDeeper (wrapFrom b ts) rest
                                                            | _ -> b, compounds

                                                        let buf, compounds = closeDeeper p.Buf p.Compounds
                                                        let depth = p.ParenDepth + parenDelta piece

                                                        let poppedL, keptL =
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                        let lambdas =
                                                            if lambdaOpens piece then
                                                                (lineNo, indent, depth - 1, p.StmtLevel) :: keptL
                                                            else
                                                                keptL

                                                        let unpruned =
                                                            if closes then
                                                                (indent, indent, true, p.ParenDepth)
                                                                :: (groups
                                                                    |> List.filter (fun (g, _, _, _) -> g < indent))
                                                            else
                                                                groups

                                                        let pipeGroups =
                                                            unpruned |> List.filter (fun (_, _, _, d) -> d <= depth)

                                                        let lastIndent, stmtLevel =
                                                            match poppedL with
                                                            | [] -> indent, p.StmtLevel
                                                            | ps ->
                                                                let (_, _, _, restore) = List.last ps
                                                                restore, restore

                                                        Ok(
                                                            Some {
                                                                p with
                                                                    Buf =
                                                                        applyJoin
                                                                            // until/always join at the sentinel:
                                                                            // a space would feed the keyword to a
                                                                            // command body's argv (cmdWord stops
                                                                            // at the sentinel) [D:until-argv-join]
                                                                            (if
                                                                                 bufEndsInProcHead buf
                                                                                 || bufEndsInServeHead buf
                                                                                 || bufEndsInEnvHead buf
                                                                                 || isUntil
                                                                                 || isAlways
                                                                             then
                                                                                 JStmtSibling
                                                                             else
                                                                                 JSpace)
                                                                            buf
                                                                            piece
                                                                            lineNo
                                                                            indent
                                                                    LastIndent = lastIndent
                                                                    StmtLevel = stmtLevel
                                                                    PrevDangles = dangleOpensBlock piece
                                                                    ParenDepth = depth
                                                                    Lambdas = lambdas
                                                                    District = armDistrict cls.Marker indent lineNo
                                                                    // a closing `|>` pops the arm
                                                                    // group and opens a fresh forward
                                                                    // group so a following `|>` aligns
                                                                    // as a pipeline [D:match-pipe-offside]
                                                                    PipeGroups = pipeGroups
                                                                    // a group this line's `)` closed ends the
                                                                    // pipe run: the next pipe opens afresh
                                                                    LastWasPipe =
                                                                        not (isUntil || isAlways)
                                                                        && pipeGroups.Length = unpruned.Length
                                                                    Compounds =
                                                                        compounds
                                                                        |> List.filter (fun (_, _, d) -> d <= depth)
                                                            },
                                                            acc,
                                                            blankSinceHead
                                                        )
                                            else
                                                match p.Lets with
                                                | (k, letLine) :: _ when indent < k -> noBody letLine
                                                | _ ->
                                                    // the offside close: siblings at or left of an
                                                    // open if/match head wrap it shut
                                                    let rec closeCompounds b compounds closedHead =
                                                        match compounds with
                                                        | (h, ts, _) :: rest when indent <= h ->
                                                            closeCompounds (wrapFrom b ts) rest (Some h)
                                                        | _ -> b, compounds, closedHead

                                                    let buf, compounds, closedHead =
                                                        closeCompounds p.Buf p.Compounds None

                                                    // the sibling floor is the statement's start column,
                                                    // not the last line's [D:continuation-siblings]: a
                                                    // deeper continuation line must not hoist the floor,
                                                    // or the second argument line of a multi-line
                                                    // application sequences as a statement
                                                    let siblingLevel =
                                                        match closedHead with
                                                        | Some h -> h
                                                        | None -> p.StmtLevel

                                                    // while a lambda's paren is open, lines at (or
                                                    // right of) its opener that would close a let or
                                                    // sequence a sibling outside it are body
                                                    // continuations instead — the `in`/`;` joins wait
                                                    // for the `)` [D:multiline-lambda]
                                                    let lambdaFloor =
                                                        match p.Lambdas with
                                                        | (_, oi, _, _) :: _ -> oi
                                                        | [] -> -1

                                                    let lets, join =
                                                        match p.Lets with
                                                        // a `)`-headed line while a plain paren is
                                                        // open closes a multi-line application — a
                                                        // continuation at any body indent, never a
                                                        // sibling; the `in`/`;` joins wait for the
                                                        // `)` exactly as the lambda floor rules
                                                        // [D:paren-close-continuation]
                                                        | _ when cls.ClosesParen && p.ParenDepth > 0 -> p.Lets, JSpace
                                                        | (k, _) :: rest when indent = k && k > lambdaFloor ->
                                                            rest, JIn
                                                        // a proc head's block joins sentineled even in
                                                        // the dangle position [D:scoped-procs]
                                                        | _ when
                                                            bufEndsInProcHead buf
                                                            || bufEndsInServeHead buf
                                                            || bufEndsInEnvHead buf
                                                            ->
                                                            p.Lets, JStmtSibling
                                                        | _ when
                                                            p.PrevDangles
                                                            && indent > p.LastIndent
                                                            && bufEndsInBareRepeat buf
                                                            ->
                                                            p.Lets, JStmtSibling
                                                        // the first line after a dangling head opens its
                                                        // body — a stale statement level from an earlier
                                                        // block must not sibling-capture it
                                                        // [D:continuation-siblings]
                                                        | _ when p.PrevDangles && indent > p.LastIndent ->
                                                            p.Lets, JSpace
                                                        // same-indent sibling = block sequencing
                                                        // [D:sibling-sentinel]: the machine boundary,
                                                        // not a user ';' — command mode stops here
                                                        | _ when indent = siblingLevel && indent > lambdaFloor ->
                                                            p.Lets, JStmtSibling
                                                        | _ -> p.Lets, JSpace

                                                    let lets =
                                                        if cls.Kind = PieceKind.LetHead then
                                                            (indent, lineNo) :: lets
                                                        else
                                                            lets

                                                    let district = armDistrict cls.Marker indent lineNo

                                                    let joined = applyJoin join buf piece lineNo indent

                                                    let depth = p.ParenDepth + parenDelta piece

                                                    // a net-negative piece closed parens the
                                                    // compounds were opened inside: those are
                                                    // balanced units already — prune, never wrap
                                                    // [D:compound-paren-prune]
                                                    let compounds =
                                                        compounds |> List.filter (fun (_, _, d) -> d <= depth)

                                                    let compounds =
                                                        if cls.OpensCompound then
                                                            // the piece starts where the join put it:
                                                            // its segment is the newest entry
                                                            let (js, _, _) = List.head joined.Segments
                                                            (indent, js, p.ParenDepth) :: compounds
                                                        else
                                                            compounds

                                                    // a statement starts at a sibling/`in` join or on
                                                    // the first line after a dangling head; the level
                                                    // rides into the lambda entry as its pop restore
                                                    let stmtLevel =
                                                        if join = JStmtSibling || join = JIn || p.PrevDangles then
                                                            indent
                                                        else
                                                            p.StmtLevel

                                                    // an attached closer pops its lambda and restores
                                                    // the statement level — the next sibling must
                                                    // join with `;`, never as an application
                                                    let poppedL, keptL =
                                                        p.Lambdas |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                    let lambdas =
                                                        if lambdaOpens piece then
                                                            (lineNo, indent, depth - 1, stmtLevel) :: keptL
                                                        else
                                                            keptL

                                                    let lastIndent, stmtLevel =
                                                        match poppedL with
                                                        | [] -> indent, stmtLevel
                                                        | ps ->
                                                            let (_, _, _, restore) = List.last ps
                                                            restore, restore

                                                    // the dedent floor [D:district-retirement]: a line
                                                    // that dedents below the open block but aligns with
                                                    // no enclosing level would space-join — silently
                                                    // absorbed as argv when the previous line is a
                                                    // command (a legal parse, wrong meaning). Error instead.
                                                    if
                                                        join = JSpace
                                                        && indent < p.LastIndent
                                                        // open lambdas/parens legitimately take
                                                        // dedented body continuations (an open
                                                        // bracket reaches here only through an
                                                        // entry's lambda body, where the floor holds
                                                        // [D:bracket-lambda-body])
                                                        && List.isEmpty p.Lambdas
                                                        && p.ParenDepth = 0
                                                    then
                                                        Error
                                                            $"line {lineNo}: this line dedents below the open block but aligns with no enclosing statement — align it with the statement it continues, or with the block level it should follow"
                                                    else

                                                        bracketFold lineNo indent p.Brackets piece
                                                        |> Result.bind (fun brackets ->
                                                            closeBodiesAbove brackets.Length {
                                                                p with
                                                                    Buf = joined
                                                                    Lets = lets
                                                                    LastIndent = lastIndent
                                                                    StmtLevel = stmtLevel
                                                                    PrevDangles = dangleOpensBlock piece
                                                                    District = district
                                                                    Compounds = compounds
                                                                    Lambdas = lambdas
                                                                    Brackets = brackets
                                                                    ParenDepth = depth
                                                                    PipeGroups =
                                                                        // the first body line of a
                                                                        // dangling arm resolves its body
                                                                        // column [D:match-pipe-offside]
                                                                        (match p.PipeGroups with
                                                                         | (g, bc, false, d) :: rest when
                                                                             bc = System.Int32.MaxValue
                                                                             && p.PrevDangles
                                                                             && indent > g
                                                                             ->
                                                                             (g, indent, false, d) :: rest
                                                                         | pg -> pg)
                                                                        |> List.skipWhile (fun (g, _, _, _) ->
                                                                            g > lastIndent)
                                                                        |> List.filter (fun (_, _, _, d) ->
                                                                            d <= depth)
                                                                    LastWasPipe = false
                                                            }
                                                            |> Result.map (fun q -> Some q, acc, blankSinceHead))

                                    go p
                        else
                            let rawT = raw.TrimEnd()
                            let cls = classifyPiece rawT

                            // a statement-level attribute line binds to the
                            // declaration below it [D:attr-positions] — a pend
                            // that is only a complete attr list joins the next
                            // col-0 line instead of closing (the field-attr `>]`
                            // dangle, one level up); the joined text re-parses
                            // as one statement, so a non-decl follower gets the
                            // parser's located error about attribute position
                            let attrOnlyPend =
                                match current with
                                | Some p when
                                    p.Brackets.IsEmpty
                                    && p.Lambdas.IsEmpty
                                    && p.District.IsNone
                                    && bufStartsWith p.Buf "[<"
                                    && bufTrimEndEndsWith p.Buf ">]"
                                    ->
                                    Some p
                                | _ -> None

                            let joinedBuf, accR =
                                match attrOnlyPend with
                                | Some p -> Some p.Buf, Ok acc
                                | None -> None, close current acc

                            accR
                            |> Result.bind (fun acc ->
                                bracketFold lineNo 0 [] rawT
                                |> Result.map (fun brackets ->
                                    Some {
                                        Buf =
                                            match joinedBuf with
                                            | Some b -> applyJoin JSpace b rawT lineNo 0
                                            | None -> bufNew raw lineNo 0
                                        Lets = []
                                        LastIndent = 0
                                        District = armDistrict cls.Marker 0 lineNo
                                        // a fresh logical-line head that opens a
                                        // compound is tracked too [D:match-pipe-offside]
                                        // — so a later dedented `|>` can wrap it
                                        // `(match …) |> f`; a sibling head already is
                                        Compounds =
                                            if cls.OpensCompound && joinedBuf.IsNone then
                                                [ (0, 0, 0) ]
                                            else
                                                []
                                        ParenDepth = parenDelta rawT
                                        StmtLevel = 0
                                        PrevDangles = dangleOpensBlock rawT
                                        Lambdas =
                                            (if lambdaOpens rawT then
                                                 [ (lineNo, 0, parenDelta rawT - 1, 0) ]
                                             else
                                                 [])
                                        PipeGroups = []
                                        LastWasPipe = false
                                        Brackets = brackets
                                        // a head line's dangling lambda arrow inside its
                                        // bracket opens that entry's body [D:bracket-lambda-body]
                                        Bodies =
                                            (match brackets with
                                             | (_, _, Some c) :: _ when lambdaDangles rawT -> [
                                                 (brackets.Length, c, 0)
                                               ]
                                             | _ -> [])
                                    },
                                    acc,
                                    false)))
                (Ok(None, [], false))

        match folded with
        | Error e -> Error e
        | Ok(current, acc, _) -> close current acc |> Result.map List.rev

// how many logical statements a raw physical buffer assembles to
// [D:repl-multiline] — comment-only lines filtered, comments stripped
// exactly as the REPL preprocesses. An assembly error (a still-open or
// pending statement) is "not yet countable" -> None. The piped REPL's
// continuation oracle: an added line that keeps the count merged into the
// pending statement; one that raises it started a new statement.
let assemble (numbered: (int * string) list) : Result<LogicalLine list, string> = assembleWith (fun _ _ -> ()) numbered

/// an assembly error's location [D:assembly-col]: the line its message
/// names and that line's first non-blank column, with the message's
/// `line N: ` lead removed (the location carries it). None when the
/// message names no line.
let assemblyErrorAt (numbered: (int * string) list) (msg: string) : (int * int * string) option =
    let m = Text.RegularExpressions.Regex.Match(msg, @"^line ([0-9]{1,9}): ")

    if not m.Success then
        None
    else
        let line = int m.Groups[1].Value

        let col =
            numbered
            |> List.tryFind (fun (n, _) -> n = line)
            |> Option.map (fun (_, raw: string) -> (raw |> Seq.takeWhile ((=) ' ') |> Seq.length) + 1)
            |> Option.defaultValue 1

        Some(line, col, msg.Substring m.Length)

/// the located one-line spelling of an assembly error [D:assembly-col]:
/// `<where>:<line>:<col>: msg`, the message unchanged when it names no line
let assemblyErrorLocated (where: string) (numbered: (int * string) list) (msg: string) : string =
    match assemblyErrorAt numbered msg with
    | Some(l, c, rest) -> $"{where}:{l}:{c}: {rest}"
    | None -> $"{where}: {msg}"

let statementCount (bufLines: string list) : int option =
    let numbered =
        bufLines
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> classifyLine raw <> LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, stripComment raw)

    match assemble numbered with
    | Ok lls -> Some(List.length lls)
    | Error _ -> None

/// does `next` continue the already-complete statement in `buf`?
/// [D:repl-multiline] The assembler's own answer, no second parser: a
/// blank/comment line breaks a completed statement (the blank-boundary
/// rule) and does not attach; otherwise `next` attaches iff appending it
/// does not raise the assembled statement count (a `|>` tail, an offside
/// `else`, a district body keep it; still-pending buffers always want
/// more). Drives the piped REPL's read-ahead so a multi-line statement
/// assembles the way a script does.
let pipedAttaches (buf: string list) (next: string) : bool =
    // a column-0 `#` line is a directive, one line by definition — it
    // never continues the statement above it [D:repl-multiline]
    if classifyLine next <> LineKind.Code || next.StartsWith "#" then
        false
    else
        match statementCount buf, statementCount (buf @ [ next ]) with
        // a complete buffer plus a column-0 line that is pending on its
        // own (a heredoc/yaml opener, `let f x =`): the new statement
        // is what waits for more, not the old one [D:repl-multiline]
        | Some _, None when
            next.Length > 0
            && next[0] <> ' '
            && next[0] <> '\t'
            && statementCount [ next ] = None
            ->
            false
        | _, None -> true
        | None, Some _ -> true
        | Some a, Some b -> b <= a

// a logical line's segment index, built once per line: translate is called
// per typed node, and a linear scan per call made a long statement
// quadratic (a 64k-line heredoc)
type private SegIndex = {
    Starts: int[]
    Lines: int[]
    Indents: int[]
    // where each segment's own text ends — the synthetic join text
    // between it and the next segment is no physical column
    Ends: int[]
    // what it was built from [D:line-pos-cache]
    ForText: string
    ForSegments: (int * int * int) list
    ForEnds: int[]
}

let private buildSegIndex (ll: LogicalLine) : SegIndex =
    let segs = List.toArray ll.Segments
    let starts = segs |> Array.map (fun (js, _, _) -> js)

    // a line not from the assembler has no join text to step over
    let ends =
        if ll.Ends.Length = starts.Length then
            ll.Ends
        else
            Array.init starts.Length (fun i ->
                if i + 1 < starts.Length then
                    starts[i + 1]
                else
                    ll.Text.Length)

    {
        Starts = starts
        Lines = segs |> Array.map (fun (_, l, _) -> l)
        Indents = segs |> Array.map (fun (_, _, i) -> i)
        Ends = ends
        ForText = ll.Text
        ForSegments = ll.Segments
        ForEnds = ll.Ends
    }

let private segIndexOf (ll: LogicalLine) : SegIndex =
    match ll.PosIndex with
    | :? SegIndex as ix when
        obj.ReferenceEquals(ix.ForText, ll.Text)
        && obj.ReferenceEquals(ix.ForSegments, ll.Segments)
        && obj.ReferenceEquals(ix.ForEnds, ll.Ends)
        ->
        ix
    | _ ->
        let ix = buildSegIndex ll
        ll.PosIndex <- ix
        ix

let translate (ll: LogicalLine) (col: int) : int * int =
    let ix = segIndexOf ll
    let joinedIdx = col - 1

    // the last segment starting at or before the index
    let i =
        match Array.BinarySearch(ix.Starts, joinedIdx) with
        | i when i >= 0 ->
            // equal starts (an empty segment): the last of them, as before
            let mutable j = i

            while j + 1 < ix.Starts.Length && ix.Starts[j + 1] = joinedIdx do
                j <- j + 1

            j
        | i -> max 0 (~~~i - 1)

    // a position in the join text sits just past its line's last
    // character, never beyond the physical line — except on the
    // assembler's wrapping "(" directly before the next segment, which
    // opens that segment's compound: it maps to the segment's start
    let next = i + 1

    if
        joinedIdx > ix.Ends[i]
        && next < ix.Starts.Length
        && (let mutable k = joinedIdx

            while k < ix.Starts[next] && ll.Text[k] = '(' do
                k <- k + 1

            k = ix.Starts[next])
    then
        ix.Lines[next], ix.Indents[next] + 1
    else
        let at = min joinedIdx ix.Ends[i]
        ix.Lines[i], at - ix.Starts[i] + ix.Indents[i] + 1
