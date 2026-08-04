module Weir.Script

open System
open Weir.Ast
open Weir.Types

// The quote-aware scanner — the ONE string-state primitive
// [D:one-scanner]. Folds f over the characters that sit OUTSIDE string
// literals: double quotes honor backslash escapes, single quotes close
// at the next single quote.
// the ONE string-state machine [D:one-scanner] — the outside-string fold
// and the end-state question share it (a second inline quote machine is a
// review flag). stringScan returns the fold result AND whether the line
// ENDS inside a string.
let private stringScan (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a * bool =
    let mutable st = init
    let mutable i = 0
    let mutable inDouble = false
    let mutable inSingle = false
    // raw kinds [D:raw-strings]: verbatim @"..." ("" = one quote, no
    // escapes) and triple-quoted """...""" (no escapes at all,
    // closes at the FIRST triple [D:raw-strings])
    let mutable inVerbatim = false
    let mutable inTriple = false

    while i < s.Length do
        let c = s[i]

        if inDouble then
            if c = '\\' && i + 1 < s.Length then
                i <- i + 1
            elif c = '"' then
                inDouble <- false
        elif inSingle then
            if c = '\'' then
                inSingle <- false
        elif inVerbatim then
            if c = '"' then
                if i + 1 < s.Length && s[i + 1] = '"' then
                    i <- i + 1
                else
                    inVerbatim <- false
        elif inTriple then
            if c = '"' && i + 2 < s.Length && s[i + 1] = '"' && s[i + 2] = '"' then
                i <- i + 2
                inTriple <- false
        elif c = '@' && i + 1 < s.Length && s[i + 1] = '"' then
            inVerbatim <- true
            i <- i + 1
        elif c = '"' && i + 2 < s.Length && s[i + 1] = '"' && s[i + 2] = '"' then
            inTriple <- true
            i <- i + 2
        elif c = '"' then
            inDouble <- true
        elif c = '\'' then
            inSingle <- true
        else
            st <- f st i c

        i <- i + 1

    st, (inDouble || inSingle || inVerbatim || inTriple)

let private foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a = fst (stringScan f init s)

// weir strings are SINGLE-LINE (all four kinds), so a line ending inside
// one can never be completed by more input — the multiline REPL submits
// such a buffer instead of growing it [D:repl-multiline]
let endsInsideString (s: string) : bool =
    snd (stringScan (fun () _ _ -> ()) () s)

let stripComment (line: string) : string =
    // comment only at line start or after whitespace: bareword URLs
    // (https://...) live in command lines (nuget-script receipt)
    let cut =
        foldOutsideStrings
            (fun cut i c ->
                if
                    cut < 0
                    && c = '/'
                    && i + 1 < line.Length
                    && line[i + 1] = '/'
                    && (i = 0 || System.Char.IsWhiteSpace line[i - 1])
                then
                    i
                else
                    cut)
            -1
            line

    // TrimEnd ON CUT only [D:trailing-comments]: the code before a
    // comment never needs its gap (district heads match EndsWith), and
    // an untouched line stays byte-equal
    if cut >= 0 then line.Substring(0, cut).TrimEnd() else line

// ---- `///` doc comments [D:doc-comments] -------------------------
// Docs are OUT-OF-BAND metadata about a source LOCATION, never part of
// the program's meaning: Value/Eval/Check never see one, so runtime is
// byte-identical BY ARCHITECTURE (nothing to erase, nothing to pin).
// The key is the PHYSICAL (line, col, len) of the documented name,
// never the name itself (shadowing / inner lets / duplicate field
// names). Hover and completion look attachments up by that position.

/// The doc lines attached to one declaration, with the physical
/// (1-based line, 1-based col, length) of the name they document.
type DocAttach =
    { Line: int
      Col: int
      Len: int
      Doc: string list }

let private isDocLine (raw: string) : bool = raw.TrimStart().StartsWith "///"

/// the text of a `///` line: after the three slashes, one optional
/// leading space consumed (so `/// x` and `///x` both yield "x")
let private docText (raw: string) : string =
    let t = raw.TrimStart().Substring 3
    if t.StartsWith " " then t.Substring 1 else t

/// the documented NAME's (1-based col, len) on a declaration line: the
/// identifier after `let`/`type`, after `|` (union case), or leading
/// (a record field). None when the line has no such name.
let private declName (raw: string) : (int * int) option =
    let code = stripComment raw
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

    if trimmed.StartsWith "let " then identAt (indent + 3)
    elif trimmed.StartsWith "type " then identAt (indent + 4)
    elif trimmed.StartsWith "|" then identAt (indent + 1)
    elif trimmed = "" then None
    else identAt indent

// In-string mask over a line — TRUE where a char sits inside any
// string kind (plain/single/verbatim/triple). The scanner family's
// third consumer face [D:fmt-respace]: respacing must never touch
// string interiors.
let private inStringMask (s: string) : bool[] =
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
// space runs, pad record braces, tidy `;`. String interiors and
// leading indent untouched. Fmt applies this under a PARSE-SHAPE
// safety check — any statement whose sexpr changes reverts — so a
// rule misfiring on a command line (argv `{x}`, literal `;`) can
// never change meaning, only be skipped.
let respaceLine (line: string) : string =
    let mask = inStringMask line
    let indent = line |> Seq.takeWhile ((=) ' ') |> Seq.length
    let sb = System.Text.StringBuilder()

    let lastEmitted () =
        if sb.Length = 0 then ' ' else sb[sb.Length - 1]

    let mutable i = 0

    while i < line.Length do
        let c = line[i]

        if i < indent || mask[i] then
            sb.Append c |> ignore
        else
            match c with
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
            | c -> sb.Append c |> ignore

        i <- i + 1

    sb.ToString()

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
// — one derivation, so the three agree by construction)

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

/// Piece classification, inside assembly: the join/structure decisions.
/// Kind is exclusive; IsMarker and OpensCompound are orthogonal flags —
/// `if c then !` is a compound head AND arms a district.
[<RequireQualifiedAccess>]
type PieceKind =
    | PipeHead
    | ElseHead
    | LetHead
    | Plain

/// Line-end district markers: bare `!` or the Layer-2 env header `!name`
/// ([D:env-sugar-layers] — the marker distributes `!name(...)` over the block).
[<RequireQualifiedAccess>]
type MarkerKind =
    | NoMarker
    // line-end `yaml` opens a yaml district [D:yaml-district] — except
    // `to yaml` / `from yaml`, which are the boundary adapters.
    // The `!` and `!ev` districts RETIRED [D:district-retirement]: the
    // arming rule made their mode gate unnecessary and `within env`
    // covers the overlay; the $e()/!e() SIGIL forms stay (fragment and
    // single-command uses have no block spelling).
    | Yaml

type PieceClass =
    { Kind: PieceKind
      Marker: MarkerKind
      OpensCompound: bool
      IsBangSigil: bool
      ClosesBrace: bool
      ClosesParen: bool
      StartsField: bool
      StartsTypeField: bool
      BraceDelta: int }

let private isIdentToken (t: string) =
    t.Length > 0
    && (System.Char.IsLetter t[0] || t[0] = '_')
    && t |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

/// the marker predicate lives in Parser (shared with completion);
/// this alias keeps Script's callers and pins stable
let isYamlMarkerPiece = Parser.isYamlMarkerPiece

let classifyPiece (piece: string) : PieceClass =
    let lastToken =
        match piece.LastIndexOf ' ' with
        | -1 -> piece
        | i -> piece.Substring(i + 1)

    { Kind =
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
        then
            PieceKind.ElseHead
        elif piece.StartsWith "let " then
            PieceKind.LetHead
        else
            PieceKind.Plain
      Marker =
        if isYamlMarkerPiece piece then
            MarkerKind.Yaml
        else
            MarkerKind.NoMarker
      OpensCompound =
        // within/for block heads close-and-wrap exactly like the
        // conditionals [D:dedent-join] — same machine, two more
        // members, NOT a fifth alignment stack (let-prefixed forms
        // stay Lets-owned, the if/match convention)
        piece.StartsWith "if "
        || piece.StartsWith "match "
        || piece.StartsWith "within "
        || piece.StartsWith "for "
        // the bounded-loop pair [D:retry-poll] — the machine's 5th and
        // 6th members, still no stack
        || piece.StartsWith "retry "
        || piece.StartsWith "poll "
      IsBangSigil =
        piece.StartsWith "!("
        || (piece.StartsWith "!"
            && (match piece.IndexOf '(' with
                | i when i > 1 -> isIdentToken (piece.Substring(1, i - 1))
                | _ -> false))
      ClosesBrace = piece.StartsWith "}"
      ClosesParen = piece.StartsWith ")"
      StartsField =
        (match piece.IndexOf '=' with
         | i when i > 0 -> isIdentToken (piece.Substring(0, i).TrimEnd())
         | _ -> false)
      // type-declaration fields carry `:` not `=`; attribute lines
      // start their field [D:multiline-brackets]
      StartsTypeField =
        piece.StartsWith "[<"
        || (match piece.IndexOf ':' with
            | i when i > 0 -> isIdentToken (piece.Substring(0, i).TrimEnd())
            | _ -> false)
      BraceDelta = braceDelta piece }

/// The multiline-lambda opener [D:multiline-lambda]: a line ends with
/// `->` while its INNERMOST unclosed paren opens a `fun` — `(fun r ->`
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

/// A piece that dangles a block open at EOL: the NEXT deeper line
/// starts a statement of that block (the lambda restore level rides
/// this) [D:multiline-lambda].
let dangleEnders = [| "="; "then"; "else"; "with"; "->" |]

// a within HEAD [D:within-scopes]: `within <kind> <args…>` (optionally
// behind `let <name> =`) opens its block — the head ends with arbitrary
// argument words, so the classifier keys on the KEYWORD, the yaml-marker
// precedent (a lexical rule shared by assembler and REPL, never a parse)
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

let dangleOpensBlock (piece: string) : bool =
    let t = piece.TrimEnd()

    dangleEnders |> Array.exists t.EndsWith
    // `do` needs a WORD boundary — `sudo` at EOL must not dangle a
    // block open [D:for-do] (the existing enders keep their exact
    // suffix behavior, zero movement)
    || t = "do"
    || t.EndsWith " do"
    || isWithinHead t
    // retry/poll heads and the until binder line open their blocks
    // [D:retry-poll]
    || t = "retry"
    || t.StartsWith "retry "
    || t = "poll"
    || t.StartsWith "poll "
    || t.StartsWith "until "

/// A line-end district marker of ANY kind — the mask below and the
/// REPL share classifyPiece's marker rules through these predicates.
let isMarkerPiece (piece: string) =
    (classifyPiece piece).Marker <> MarkerKind.NoMarker

/// TRUE for physical lines that are DISTRICT content (deeper than an
/// arming marker line) [D:content-bytes]: the byte-preserving passes —
/// doc attachment, the doc-align lint, fmt's doc canonicalization —
/// must neither read nor move them; content is bytes.
let districtContentMask (lines: string list) : bool[] =
    let arr = List.toArray lines
    let mask = Array.create arr.Length false
    let mutable armed: int option = None // the marker line's indent

    for i in 0 .. arr.Length - 1 do
        let raw = arr[i]
        let code = stripComment raw
        let indent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

        if raw.Trim() = "" then
            () // blank: transparent, stays armed, unmasked
        elif code.Trim() = "" then
            match armed with
            | Some m when indent > m -> mask[i] <- true
            | _ -> ()
        else
            match armed with
            | Some m when indent > m -> mask[i] <- true
            | _ ->
                armed <- None

                if isMarkerPiece (code.TrimStart()) then
                    armed <- Some indent

    mask

/// Pure pass: a contiguous run of `///` lines attaches to the
/// declaration on the next CODE line; a blank OR a plain `//` line
/// breaks the run (the contiguity law). An attribute-only line
/// (`[<...>]`) is TRANSPARENT: the doc rides through to the
/// declaration below, so F#'s canonical doc-then-attribute order and
/// the attribute-then-doc order both attach.
let isAttributeOnlyLine (raw: string) =
    let t = (stripComment raw).Trim()
    t.StartsWith "[<" && t.EndsWith ">]"

let docAttachments (lines: string list) : DocAttach list =
    let mutable pending: string list = []
    let acc = System.Collections.Generic.List<DocAttach>()

    let masked = districtContentMask lines

    lines
    |> List.iteri (fun idx raw ->
        let ln = idx + 1

        if masked[idx] then
            () // district content is bytes — never doc syntax [D:content-bytes]
        elif isDocLine raw then
            pending <- pending @ [ docText raw ]
        elif isAttributeOnlyLine raw then
            ()
        elif raw.Trim() = "" then
            pending <- []
        elif (stripComment raw).Trim() = "" then
            pending <- [] // a plain // comment-only line breaks contiguity
        else
            if not (List.isEmpty pending) then
                match declName raw with
                | Some(col, len) ->
                    acc.Add
                        { Line = ln
                          Col = col
                          Len = len
                          Doc = pending }
                | None -> ()

            pending <- [])

    List.ofSeq acc

/// The marker's district wrap: opener text and how many trailing
/// characters of the armed line the first district line strips.
/// the RETIRED district spellings [D:district-retirement] — detected
/// so their removal error TEACHES instead of dumping an expecting-list
/// (a documented feature's removal is the one case a reader has a
/// right to be confused about)
let retiredDistrictMarker (piece: string) : bool =
    let lastToken =
        match piece.LastIndexOf ' ' with
        | -1 -> piece
        | i -> piece.Substring(i + 1)

    piece = "!"
    || piece.EndsWith " !"
    || (lastToken.StartsWith "!"
        && not (lastToken.Contains "(")
        && isIdentToken (lastToken.Substring 1))

let private markerOpener (m: MarkerKind) : (string * int * bool) option =
    match m with
    | MarkerKind.NoMarker -> None

    // the yaml district keeps its marker word; lines join VERBATIM with
    // relative indentation behind the sentinel [D:yaml-district]
    | MarkerKind.Yaml -> Some("", 0, true)

type LogicalLine =
    { Text: string
      Head: int
      Segments: (int * int * int) list }

// a one-physical-line LogicalLine — the REPL's and -e's spelling
let singleLine (text: string) : LogicalLine =
    { Text = text
      Head = 1
      Segments = [ (0, 1, 0) ] }

// Block lets — F# light syntax at the assembly layer, the same way F#'s own
// lexer implements it (token insertion at offside boundaries): a continuation
// line beginning with `let` opens a binding; the next line at the SAME
// indentation closes it by joining with " in " instead of " ", so the
// single-line grammar sees the explicit form. `|`-headed lines are inert to
// the stack ONLY while it is empty (statement-level pipeline continuations
// and column-0 match arms — the two customers); with a binding pending they
// follow the plain indent rules, so a dedented arm inside a block is the
// same "needs a body" error F# gives the shape. Every pending let must be
// closed before the statement ends.
// Unbalanced ( and { closers for a text fragment — the completion
// repair path appends these so a mid-edit dangling line parses
// (quote-aware via the one scanner, per the formalization rule).
let closers (text: string) : string =
    // one stack of expected closers models the full nesting: brackets
    // in code, strings ('"' plain, '$' interp — closes with '"' but
    // '{' opens a HOLE back into code land), single quotes, holes.
    // Mid-edit dangling text closes correctly at any depth.
    let mutable stack: char list = []
    let mutable i = 0

    while i < text.Length do
        let c = text[i]

        match stack with
        | ('"' | '$') :: rest ->
            if c = '\\' && i + 1 < text.Length then
                i <- i + 1
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
            elif c = '\'' then
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
// running stack — fmt aligns record fields at TOP+2 and list elements
// at TOP+1, under the first field/element either way
// (quote-aware via the scanner family; lives here per the rule).
let braceStack (prev: (char * int) list) (line: string) : (char * int) list =
    // rides the ONE scanner [D:one-scanner]
    foldOutsideStrings
        (fun stack i c ->
            match c with
            | '{'
            | '[' -> (c, i) :: stack
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
// PHYSICAL column as the sibling-entry anchor [D:field-alignment]; a
// dangling opener records None (the first continuation entry sets it).
// A mismatched closer is an error naming BOTH sides; over-closing stays
// permissive (the parser owns that message). Parens are NOT tracked.
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
                        let mutable j = i + 1

                        while j < piece.Length && piece[j] = ' ' do
                            j <- j + 1

                        // an update header's opener-line content is the
                        // SOURCE, not a field — the first continuation
                        // entry anchors instead [D:record-update]
                        let isWithHeader =
                            let rest = piece.Substring(j).TrimEnd()
                            rest = "with" || rest.EndsWith " with"

                        if j < piece.Length && piece[j] <> '}' && piece[j] <> ']' && not isWithHeader then
                            Some(indent + j)
                        else
                            None

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
// AFTER the conditional while deeper lines still join into its body,
// where greedy `;` grouping is exactly right. `else` and `|` pieces
// extend a compound instead of closing it. BraceDepth > 0 puts the
// assembler in record-continuation mode: line breaks separate fields,
// every other joining rule is inert (records are expressions).
type private District =
    { MarkerIndent: int
      MarkerLine: int
      Opener: string
      Strip: int
      Yaml: bool
      Active: int option }

type private Pend =
    { LL: LogicalLine
      Lets: (int * int) list
      LastIndent: int
      // sibling pipe columns, innermost first [D:pipe-alignment]: a
      // consecutive `|` line must sit exactly on a group column; the
      // first pipe after a non-pipe line opens a group
      PipeGroups: int list
      LastWasPipe: bool
      District: District option
      // (headIndent, textStart, parenDepthAtOpen) [D:compound-paren-prune]
      Compounds: (int * int * int) list
      ParenDepth: int
      // the indent where the CURRENT statement started (statement = a
      // sibling/`in` join, or the first line after a dangling head) —
      // the lambda pop restores to this level, so a block sibling after
      // the `)` sequences while a fold's init still applies
      // [D:multiline-lambda]
      StmtLevel: int
      PrevDangles: bool
      // open multiline lambdas (opening line, opener indent, paren depth
      // BEFORE the open, statement level to RESTORE on pop), innermost
      // first [D:multiline-lambda] — popped by paren balance; the user's
      // `)` is the closer
      Lambdas: (int * int * int * int) list
      // still-open brackets (kind, opening line, sibling-entry column)
      // [D:multiline-brackets] [D:field-alignment]
      Brackets: (char * int * int option) list }

// The join algebra: every way a continuation line attaches to the
// pending statement, its inserted text in ONE place. joinedStart
// derives from the same strings, so span arithmetic cannot drift from
// the insertion.
type private Join =
    | JIn // let-close: text + " in " + piece
    | JSibling // bracket field/element separators: " ; "
    | JStmtSibling // statement-sibling sequencing [D:sibling-sentinel]:
    // " <sentinel> " — same width as " ; " so span arithmetic is
    // identical, but command mode stops at it (a user ';' does not)
    | JSpace // plain continuation: " "
    | JDistrictOpen of strip: int * opener: string // strip the armed marker, wrap
    | JDistrictSibling of opener: string // text + " ; " + opener + piece + ")"
    | JDistrictPipe // reopen the wrap: stem + " " + piece + ")"
    | JYamlLine of rel: int // sentinel + rel spaces + VERBATIM line [D:yaml-district]

let private applyJoin (j: Join) (ll: LogicalLine) (piece: string) (lineNo: int) (indent: int) : LogicalLine =
    let text, joinedStart =
        match j with
        | JIn ->
            let sep = " in "
            ll.Text + sep + piece, ll.Text.Length + sep.Length
        | JSibling ->
            let sep = " ; "
            ll.Text + sep + piece, ll.Text.Length + sep.Length
        | JStmtSibling ->
            // same 3-char width as " ; " — translate arithmetic unchanged
            let sep = " " + Parser.sibSepStr + " "
            ll.Text + sep + piece, ll.Text.Length + sep.Length
        | JSpace ->
            let sep = " "
            ll.Text + sep + piece, ll.Text.Length + sep.Length
        | JDistrictOpen(strip, opener) ->
            let stem = ll.Text.Substring(0, ll.Text.Length - strip)
            stem + opener + piece + ")", stem.Length + opener.Length
        | JDistrictSibling opener ->
            let sep = " ; " + opener
            ll.Text + sep + piece + ")", ll.Text.Length + sep.Length
        | JDistrictPipe ->
            let stem = ll.Text.Substring(0, ll.Text.Length - 1)
            let sep = " "
            stem + sep + piece + ")", stem.Length + sep.Length
        | JYamlLine rel ->
            // the block line rides VERBATIM: sentinel, then its indentation
            // RELATIVE to the block's first line, then the text — the
            // parser reconstructs the 2D structure from exactly this
            let sep = Parser.sibSepStr + String(' ', rel)
            ll.Text + sep + piece, ll.Text.Length + sep.Length

    { ll with
        Text = text
        Segments = (joinedStart, lineNo, indent) :: ll.Segments }

let assemble (numbered: (int * string) list) : Result<LogicalLine list, string> =
    // trailing comments strip HERE, per physical line [D:trailing-comments]:
    // stripComment is the whitespace-preceded rule (glued // — http://a,
    // --format=a//b — stays data). Skipped: yaml district content (BYTES)
    // and comment-ONLY lines (their class carries transparency semantics
    // a blanked line would lose — blankSinceHead is the difference)
    let numbered =
        // the mask must see STRIPPED heads (a commented district head
        // still opens its district), so the cut runs twice: once to
        // find the content regions, once — content excluded — for real.
        // TrimEnd only on actual cuts: untouched lines stay byte-equal
        let mask = districtContentMask (numbered |> List.map (snd >> stripComment))

        numbered
        |> List.mapi (fun i (n, raw) ->
            if (i < mask.Length && mask[i]) || (stripComment raw).Trim() = "" then
                n, raw
            else
                n, stripComment raw)

    // the retired ! districts TEACH [D:district-retirement] — checked
    // up front over every non-content line (yaml district bodies are
    // bytes, never read: districtContentMask)
    let retiredHit =
        let mask = districtContentMask (numbered |> List.map snd)

        numbered
        |> List.mapi (fun i (n, raw) -> i, n, raw)
        |> List.tryPick (fun (i, n, raw) ->
            if i < mask.Length && mask[i] then
                None
            else
                let t = (stripComment raw).TrimEnd()
                if t <> "" && retiredDistrictMarker t then Some n else None)

    match retiredHit with
    | Some n ->
        Error
            $"line {n}: the line-end ! district retired [D:district-retirement] — commands are ordinary statements now (drop the !); for an env overlay over a block, use `within env vars`"
    | None ->
        let noBody letLine =
            Error
                $"line {letLine}: this let needs a body — an expression at the same indentation must follow before the statement ends"

        let braceOpen (p: Pend) =
            match p.Brackets with
            | ('{', line, _) :: _ when p.LL.Text.StartsWith "type " ->
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
            | Some { District = Some { Active = None; MarkerLine = mLine } } ->
                Error $"line {mLine}: line-end '!' needs an indented block of command lines below it"
            | Some { Lets = (_, letLine) :: _ } -> noBody letLine
            | Some p ->
                Ok(
                    { p.LL with
                        Segments = List.rev p.LL.Segments }
                    :: acc
                )
            | None -> Ok acc

        let districtLineCheck lineNo (cls: PieceClass) =
            if cls.IsBangSigil then
                Error $"line {lineNo}: already inside a command block; drop the !(...)"
            elif cls.Kind = PieceKind.LetHead then
                Error $"line {lineNo}: district lines are commands; bind values outside the block"
            else
                Ok()

        // paren-wrap the compound starting at textStart; later segment
        // starts shift by the inserted "(" (remaining compounds all start
        // earlier — pops run deepest-first — so they never shift)
        let wrapFrom (ll: LogicalLine) (ts: int) =
            { ll with
                Text = ll.Text.Substring(0, ts) + "(" + ll.Text.Substring ts + ")"
                Segments =
                    ll.Segments
                    |> List.map (fun (js, l, i) -> (if js >= ts then js + 1 else js), l, i) }

        let folded =
            numbered
            |> List.fold
                (fun state (lineNo, raw) ->
                    match state with
                    | Error e -> Error e
                    | Ok _ when raw.Contains Parser.sibSep ->
                        // unproduceability [D:sibling-sentinel]: the machine
                        // sibling token can never come from source — reject it
                        // at the one place text becomes logical lines
                        Error $"line {lineNo}: illegal control character in source"
                    | Ok(current, acc, blankSinceHead) ->
                        let inOpenBrace =
                            match current with
                            | Some p -> not p.Brackets.IsEmpty
                            | None -> false

                        // the col-0 law suspends while a lambda's paren is
                        // open [D:multiline-lambda]: the closer (or the leak
                        // guard) owns those lines
                        let inOpenLambda =
                            match current with
                            | Some p -> not p.Lambdas.IsEmpty
                            | None -> false

                        if raw.Trim() = "" then
                            match current with
                            // inside an ACTIVE yaml district a blank line is
                            // BYTES [D:block-scalars] — a block scalar's
                            // content keeps it, so it rides as an empty
                            // verbatim line; the template parser skips blanks
                            // everywhere outside a block scalar's content
                            | Some({ District = Some { Active = Some _; Yaml = true } } as p) ->
                                Ok(
                                    Some
                                        { p with
                                            LL = applyJoin (JYamlLine 0) p.LL "" lineNo 0 },
                                    acc,
                                    blankSinceHead
                                )
                            // transparency is total while a statement pends
                            // [D:body-blanks] — the comment-line class, second
                            // member; the col-0 law (plus EOF) is the sole
                            // statement boundary, so every error the blank
                            // boundary produced still fires at close
                            | Some p -> Ok(Some p, acc, blankSinceHead)
                            | None -> Ok(None, acc, true)
                        elif (stripComment raw).Trim() = "" then
                            // comment-only: transparent [D:comment-transparency] —
                            // EXCEPT inside an active yaml district, where the
                            // line is BYTES (`// x` in a block scalar is data)
                            match current with
                            | Some({ District = Some { Active = Some bse
                                                       Yaml = true
                                                       MarkerIndent = m } } as p) when
                                (let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length in ind > m && ind >= bse)
                                ->
                                let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                                Ok(
                                    Some
                                        { p with
                                            LL = applyJoin (JYamlLine(ind - bse)) p.LL (raw.Substring ind) lineNo ind },
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
                            // the col-0 `|` arm precedent [D:retry-poll]
                            || raw.StartsWith "until "
                            || raw.TrimEnd() = "until"
                            || inOpenBrace
                            || inOpenLambda
                        then
                            let wsRun = raw |> Seq.takeWhile (fun c -> c = ' ' || c = '\t') |> Seq.length
                            let indent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                            // content is bytes: inside an active yaml district a
                            // tab AFTER the (space) indentation is CONTENT — the
                            // structure-level rejection must not reach it
                            let inYamlContent =
                                match current with
                                | Some { District = Some { Active = Some _
                                                           Yaml = true
                                                           MarkerIndent = m } } -> indent > m
                                | _ -> false

                            if indent < wsRun && not inYamlContent then
                                Error $"line {lineNo}: tabs are not allowed in indentation"
                            else
                                match current with
                                | None ->
                                    if blankSinceHead then
                                        Error
                                            $"line {lineNo}: continuation after a blank line has no statement to continue"
                                    else
                                        Error $"line {lineNo}: continuation without a statement"
                                | Some p ->
                                    // structure decisions read the STRIPPED text;
                                    // yaml-district joins carry the raw BYTES
                                    let rawPiece = raw.Substring indent
                                    let piece = (stripComment raw).Substring indent
                                    let cls = classifyPiece piece

                                    let rec go (p: Pend) =
                                        match p.Brackets with
                                        // the statement-head guard [D:blank-in-brackets]:
                                        // keywords cannot be entries, so a col-0
                                        // let/type bounds a runaway unclosed bracket
                                        | (kind, bline, _) :: _ when
                                            indent = 0 && (piece.StartsWith "let " || piece.StartsWith "type ")
                                            ->
                                            Error
                                                $"line {lineNo}: statement at column 0 while the '{kind}' opened at line {bline} is still open — close the bracket"
                                        | (kind, _, entryCol) :: _ ->
                                            // bracket continuation: a line break after a
                                            // field/element is a separator
                                            let prev = p.LL.Text.TrimEnd()

                                            // the separator goes BEFORE an entry-start line,
                                            // never before a value continuation — a field's
                                            // value may open on the next line (the
                                            // fixture-diversity sweep's first catch — PROCESS.md).
                                            // Lists have no entry marker: every line starts an
                                            // element unless the previous line dangles an
                                            // opener/separator/operator [D:multiline-brackets]
                                            let startsEntry =
                                                // a closer line ends its bracket, never
                                                // starts an entry (Stroustrup closers)
                                                // [D:multiline-brackets]
                                                if piece.StartsWith "]" || piece.StartsWith "}" then false
                                                elif kind = '[' then true
                                                elif p.LL.Text.StartsWith "type " then cls.StartsTypeField
                                                else cls.StartsField

                                            let danglesOpen =
                                                prev.EndsWith "{"
                                                || prev.EndsWith "["
                                                || prev.EndsWith ";"
                                                // an update header ends at `with`; the
                                                // first field after it is not a sibling
                                                // [D:record-update]
                                                || prev.EndsWith " with"
                                                // a preceding-line attribute binds to ITS
                                                // field: no separator between them
                                                || prev.EndsWith ">]"
                                                // a dangling operator/comma continues the
                                                // same element (wrapped elements) — but NOT in
                                                // type declarations, where a generic closer
                                                // (`Option<string>`) legitimately ends a field
                                                || (not (kind = '{' && p.LL.Text.StartsWith "type ")
                                                    && prev.Length > 0
                                                    && "+-*/,(<>=|&" |> Seq.contains prev[prev.Length - 1])

                                            let join = if startsEntry && not danglesOpen then JSibling else JSpace

                                            // sibling entries align exactly [D:field-alignment]:
                                            // the first entry (opener-line content, or the first
                                            // continuation entry of a dangling opener) sets the
                                            // column; every later entry must hit it
                                            // an attribute and its field are ONE entry on two
                                            // lines — the `>]` dangle suppresses the separator,
                                            // never the alignment [D:field-alignment]
                                            let attrField = startsEntry && prev.EndsWith ">]"

                                            let alignment =
                                                if
                                                    join = JSibling || attrField || (startsEntry && entryCol.IsNone)
                                                then
                                                    match entryCol with
                                                    | Some c when indent <> c ->
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
                                                |> Result.map (fun brackets ->
                                                    Some
                                                        { p with
                                                            LL = applyJoin join p.LL piece lineNo indent
                                                            LastIndent = indent
                                                            Brackets = brackets },
                                                    acc,
                                                    blankSinceHead)
                                        | [] ->
                                            match p.District with
                                            | Some({ Active = None; Yaml = true } as dst) when
                                                indent > dst.MarkerIndent
                                                ->
                                                // the first yaml line fixes the block BASE; it rides
                                                // at relative indent 0 [D:yaml-district]
                                                Ok(
                                                    Some
                                                        { p with
                                                            LL = applyJoin (JYamlLine 0) p.LL rawPiece lineNo indent
                                                            LastIndent = indent
                                                            District = Some { dst with Active = Some indent } },
                                                    acc,
                                                    blankSinceHead
                                                )
                                            | Some({ Active = Some bse; Yaml = true } as dst) when
                                                indent > dst.MarkerIndent
                                                ->
                                                if indent < bse then
                                                    Error
                                                        $"line {lineNo}: this yaml line outdents below the block's first line"
                                                else
                                                    Ok(
                                                        Some
                                                            { p with
                                                                LL =
                                                                    applyJoin
                                                                        (JYamlLine(indent - bse))
                                                                        p.LL
                                                                        rawPiece
                                                                        lineNo
                                                                        indent
                                                                LastIndent = indent },
                                                        acc,
                                                        blankSinceHead
                                                    )
                                            | Some({ Active = None } as dst) when indent > dst.MarkerIndent ->
                                                districtLineCheck lineNo cls
                                                |> Result.map (fun () ->
                                                    Some
                                                        { p with
                                                            LL =
                                                                applyJoin
                                                                    (JDistrictOpen(dst.Strip, dst.Opener))
                                                                    p.LL
                                                                    piece
                                                                    lineNo
                                                                    indent
                                                            LastIndent = indent
                                                            District = Some { dst with Active = Some indent } },
                                                    acc,
                                                    blankSinceHead)
                                            | Some { Active = None
                                                     MarkerLine = mLine
                                                     Yaml = isY } ->
                                                let what = if isY then "'yaml'" else "'!'"

                                                Error $"line {mLine}: line-end {what} needs an indented block below it"
                                            | Some({ Active = Some d } as dst) when indent > dst.MarkerIndent ->
                                                if cls.Kind = PieceKind.PipeHead then
                                                    Ok(
                                                        Some
                                                            { p with
                                                                LL = applyJoin JDistrictPipe p.LL piece lineNo indent
                                                                LastIndent = indent },
                                                        acc,
                                                        blankSinceHead
                                                    )
                                                elif indent = d then
                                                    districtLineCheck lineNo cls
                                                    |> Result.map (fun () ->
                                                        Some
                                                            { p with
                                                                LL =
                                                                    applyJoin
                                                                        (JDistrictSibling dst.Opener)
                                                                        p.LL
                                                                        piece
                                                                        lineNo
                                                                        indent
                                                                LastIndent = indent },
                                                        acc,
                                                        blankSinceHead)
                                                else
                                                    Error
                                                        $"line {lineNo}: district lines are commands, one per line (use a leading | to continue a pipeline)"
                                            | Some dst ->
                                                // at or left of the marker: the district closes and
                                                // its marker line is the sibling level for what
                                                // follows (like a compound closing); then this line
                                                // reprocesses under the normal rules
                                                go
                                                    { p with
                                                        District = None
                                                        LastIndent = dst.MarkerIndent }
                                            // the multiline lambda's closer and leak guard
                                            // [D:multiline-lambda]: a `)`-headed line continues
                                            // the statement at ANY indent; any other line at or
                                            // left of the opener is a leak, named
                                            | None when
                                                (match p.Lambdas with
                                                 | (_, oindent, _, _) :: _ -> cls.ClosesParen || indent < oindent
                                                 | [] -> false)
                                                ->
                                                let (oline, oindent, _, _) = List.head p.Lambdas

                                                if not cls.ClosesParen then
                                                    // F#-parity: FS0058 is an ERROR left of the
                                                    // opener; AT the opener's indent the line is a
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
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

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

                                                        bracketFold lineNo indent [] piece
                                                        |> Result.map (fun brackets ->
                                                            Some
                                                                { p with
                                                                    LL = applyJoin JSpace p.LL piece lineNo indent
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
                                                                        |> List.skipWhile (fun g -> g > backTo)
                                                                    LastWasPipe = false
                                                                    Brackets = brackets },
                                                            acc,
                                                            blankSinceHead)
                                            | None ->
                                                if cls.Kind = PieceKind.PipeHead || cls.Kind = PieceKind.ElseHead then
                                                    // arms, pipeline stages, and else extend the
                                                    // current piece: no sibling `;` — but siblings
                                                    // must ALIGN, and a shallower arm offside-closes
                                                    // deeper compounds [D:pipe-alignment]
                                                    let isUntil = piece = "until" || piece.StartsWith "until "

                                                    match p.Lets with
                                                    | (k, letLine) :: _ when indent <= k && not isUntil ->
                                                        noBody letLine
                                                    | _ ->
                                                        // deeper groups die at this line's column
                                                        let groups =
                                                            p.PipeGroups |> List.skipWhile (fun g -> g > indent)

                                                        let aligned =
                                                            if cls.Kind = PieceKind.ElseHead then
                                                                // else/elif keep their standing rules
                                                                Ok groups
                                                            elif not p.LastWasPipe then
                                                                // first pipe after a non-pipe line
                                                                // opens a group — anchored at or
                                                                // right of the innermost open
                                                                // compound head (F#'s offside)
                                                                match p.Compounds with
                                                                | (h, _, _) :: _ when indent < h ->
                                                                    Error
                                                                        $"line {lineNo}: this arm sits left of its match (head at column {h}) — align arms at or right of it"
                                                                | _ -> Ok(indent :: groups)
                                                            else
                                                                match groups with
                                                                | g :: _ when g = indent -> Ok groups
                                                                | g :: _ ->
                                                                    Error
                                                                        $"line {lineNo}: this line is indented off its siblings (they sit at column {g}) — align the group exactly"
                                                                | [] ->
                                                                    Error
                                                                        $"line {lineNo}: this line is indented off its siblings — align the group exactly"

                                                        match aligned with
                                                        | Error e -> Error e
                                                        | Ok groups ->
                                                            // a shallower arm closes compounds whose
                                                            // heads sit deeper (the nested-match
                                                            // return F# reads from the columns)
                                                            let rec closeDeeper ll compounds =
                                                                match compounds with
                                                                | (h, ts, _) :: rest when h > indent ->
                                                                    closeDeeper (wrapFrom ll ts) rest
                                                                | _ -> ll, compounds

                                                            let ll, compounds = closeDeeper p.LL p.Compounds
                                                            let depth = p.ParenDepth + parenDelta piece

                                                            let poppedL, keptL =
                                                                p.Lambdas
                                                                |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                            let lambdas =
                                                                if lambdaOpens piece then
                                                                    (lineNo, indent, depth - 1, p.StmtLevel) :: keptL
                                                                else
                                                                    keptL

                                                            let lastIndent, stmtLevel =
                                                                match poppedL with
                                                                | [] -> indent, p.StmtLevel
                                                                | ps ->
                                                                    let (_, _, _, restore) = List.last ps
                                                                    restore, restore

                                                            Ok(
                                                                Some
                                                                    { p with
                                                                        LL = applyJoin JSpace ll piece lineNo indent
                                                                        LastIndent = lastIndent
                                                                        StmtLevel = stmtLevel
                                                                        PrevDangles = dangleOpensBlock piece
                                                                        ParenDepth = depth
                                                                        Lambdas = lambdas
                                                                        PipeGroups = groups
                                                                        LastWasPipe = not isUntil
                                                                        Compounds =
                                                                            compounds
                                                                            |> List.filter (fun (_, _, d) ->
                                                                                d <= depth) },
                                                                acc,
                                                                blankSinceHead
                                                            )
                                                else
                                                    match p.Lets with
                                                    | (k, letLine) :: _ when indent < k -> noBody letLine
                                                    | _ ->
                                                        // the offside close: siblings at or left of an
                                                        // open if/match head wrap it shut
                                                        let rec closeCompounds ll compounds closedHead =
                                                            match compounds with
                                                            | (h, ts, _) :: rest when indent <= h ->
                                                                closeCompounds (wrapFrom ll ts) rest (Some h)
                                                            | _ -> ll, compounds, closedHead

                                                        let ll, compounds, closedHead =
                                                            closeCompounds p.LL p.Compounds None

                                                        let siblingLevel =
                                                            match closedHead with
                                                            | Some h -> h
                                                            | None -> p.LastIndent

                                                        // while a lambda's paren is open, lines at (or
                                                        // right of) its opener that would close a let or
                                                        // sequence a sibling OUTSIDE it are body
                                                        // continuations instead — the `in`/`;` joins wait
                                                        // for the `)` [D:multiline-lambda]
                                                        let lambdaFloor =
                                                            match p.Lambdas with
                                                            | (_, oi, _, _) :: _ -> oi
                                                            | [] -> -1

                                                        let lets, join =
                                                            match p.Lets with
                                                            | (k, _) :: rest when indent = k && k > lambdaFloor ->
                                                                rest, JIn
                                                            // same-indent sibling = block sequencing
                                                            // [D:sibling-sentinel]: the machine boundary,
                                                            // NOT a user ';' — command mode stops here
                                                            | _ when indent = siblingLevel && indent > lambdaFloor ->
                                                                p.Lets, JStmtSibling
                                                            | _ -> p.Lets, JSpace

                                                        let lets =
                                                            if cls.Kind = PieceKind.LetHead then
                                                                (indent, lineNo) :: lets
                                                            else
                                                                lets

                                                        let district =
                                                            markerOpener cls.Marker
                                                            |> Option.map (fun (opener, strip, isYaml) ->
                                                                { MarkerIndent = indent
                                                                  MarkerLine = lineNo
                                                                  Opener = opener
                                                                  Strip = strip
                                                                  Yaml = isYaml
                                                                  Active = None })

                                                        let joined = applyJoin join ll piece lineNo indent

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

                                                        // an attached closer pops its lambda AND restores
                                                        // the statement level — the next sibling must
                                                        // join with `;`, never as an application
                                                        let poppedL, keptL =
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

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

                                                        // THE DEDENT FLOOR [D:district-retirement]: a line
                                                        // that dedents below the open block but aligns with
                                                        // no enclosing level would SPACE-JOIN — silently
                                                        // absorbed as argv when the previous line is a
                                                        // command (legal-parse-wrong-meaning). Error instead.
                                                        if
                                                            join = JSpace
                                                            && indent < p.LastIndent
                                                            // open lambdas/brackets/parens legitimately take
                                                            // dedented body/element continuations
                                                            && List.isEmpty p.Lambdas
                                                            && List.isEmpty p.Brackets
                                                            && p.ParenDepth = 0
                                                        then
                                                            Error
                                                                $"line {lineNo}: this line dedents below the open block but aligns with no enclosing statement — align it with the statement it continues, or with the block level it should follow"
                                                        else

                                                            bracketFold lineNo indent [] piece
                                                            |> Result.map (fun brackets ->
                                                                Some
                                                                    { p with
                                                                        LL = joined
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
                                                                            p.PipeGroups
                                                                            |> List.skipWhile (fun g ->
                                                                                g > lastIndent)
                                                                        LastWasPipe = false },
                                                                acc,
                                                                blankSinceHead)

                                    go p
                        else
                            let cls = classifyPiece (raw.TrimEnd())

                            close current acc
                            |> Result.bind (fun acc ->
                                bracketFold lineNo 0 [] (raw.TrimEnd())
                                |> Result.map (fun brackets ->
                                    Some
                                        { LL =
                                            { Text = raw
                                              Head = lineNo
                                              Segments = [ (0, lineNo, 0) ] }
                                          Lets = []
                                          LastIndent = 0
                                          District =
                                            markerOpener cls.Marker
                                            |> Option.map (fun (opener, strip, isYaml) ->
                                                { MarkerIndent = 0
                                                  MarkerLine = lineNo
                                                  Opener = opener
                                                  Strip = strip
                                                  Yaml = isYaml
                                                  Active = None })
                                          Compounds = []
                                          ParenDepth = parenDelta (raw.TrimEnd())
                                          StmtLevel = 0
                                          PrevDangles = dangleOpensBlock (raw.TrimEnd())
                                          Lambdas =
                                            (if lambdaOpens (raw.TrimEnd()) then
                                                 [ (lineNo, 0, parenDelta (raw.TrimEnd()) - 1, 0) ]
                                             else
                                                 [])
                                          PipeGroups = []
                                          LastWasPipe = false
                                          Brackets = brackets },
                                    acc,
                                    false)))
                (Ok(None, [], false))

        match folded with
        | Error e -> Error e
        | Ok(current, acc, _) -> close current acc |> Result.map List.rev

let translate (ll: LogicalLine) (col: int) : int * int =
    let joinedIdx = col - 1

    let segStart, physLine, physIndent =
        ll.Segments |> List.filter (fun (js, _, _) -> js <= joinedIdx) |> List.last

    physLine, joinedIdx - segStart + physIndent + 1

// ANSI color for interactive diagnostics: gated per stream on TTY,
// NO_COLOR, and TERM=dumb — pipes and CI capture always get plain
// text, so pinned messages never see escape codes.
// Color moved to Types.fs (shared with the Log builtins); the alias
// keeps Script.Color consumers (Repl) and bare Color.* sites stable
module Color = Weir.Types.Color

// ---- the REPL input-line colorizer [D:repl-color] -----------------
// Rides the ONE scanner (inStringMask), stripComment, and the parser's
// keyword set — no re-derived string states, by law: this is the one
// highlighter that is correct by construction. Lexical grade only;
// the head word additionally colors by the session resolver's verdict
// (the fish trick). Fixed palette, no theming.

let stripAnsi (s: string) : string =
    System.Text.RegularExpressions.Regex.Replace(s, "\x1b\[[0-9;]*m", "")

let colorizeRepl (isKnown: string -> bool) (line: string) : string =
    if line = "" then
        ""
    else
        // per-char color codes; None = plain
        let codes: string option array = Array.create line.Length None
        let mask = inStringMask line

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
        let mutable headSeen = false
        // the mode tint [D:semantic-tokens]: an external head arms
        // command mode; argv words render DIM until a '|' hands the
        // chain to an expression stage — the same three-way the LSP
        // tokens carry (head / argv / splice), from the same resolver
        let mutable cmdMode = false

        while i < line.Length do
            if not (free i) then
                i <- i + 1
            elif isIdentStart line[i] then
                let start = i

                while i < line.Length && free i && isIdentCont line[i] do
                    i <- i + 1

                let word = line.Substring(start, i - start)

                let code =
                    if Weir.Parser.keywords.Contains word then
                        // keywords: BLUE — the red family (31/35 render
                        // near-identically in some themes) is reserved for
                        // exactly one signal: a head that would fail
                        Some "34"
                    elif not headSeen && start = 0 then
                        // the fish trick: the head resolves live
                        if isKnown word then
                            Some "1" // known: bold
                        elif Extern.exists word then
                            cmdMode <- true
                            Some "1;34" // PATH: bold blue
                        else
                            Some "31" // unresolved: red
                    elif Char.IsUpper word[0] then
                        Some "33" // the casing law: types/ctors/modules
                    elif cmdMode then
                        Some "2" // argv words: dim (inert data)
                    else
                        None

                headSeen <- true

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
            elif line[i] = '$' || line[i] = '^' || line[i] = '!' then
                // sigils, splices, markers, force-prefix
                codes[i] <- Some "36"
                i <- i + 1

                if line[i - 1] = '$' && i < line.Length && free i && isIdentStart line[i] then
                    while i < line.Length && free i && isIdentCont line[i] do
                        codes[i] <- Some "36"
                        i <- i + 1
            elif "|><=+-*/".Contains(line[i]) then
                if line[i] = '|' then
                    // a stage boundary: what follows is expression land
                    cmdMode <- false

                codes[i] <- Some "1" // operators: bold
                i <- i + 1
            else
                i <- i + 1

        // the yaml district marker [D:yaml-district]: the line-end word
        // tints like the `!` markers do; district BODY lines stay
        // per-line lexical — the block treatment is the static grammars'
        // and semantic tokens' job, not a line colorizer's
        let codeTrimmed = (line.Substring(0, commentCut)).TrimEnd()

        if codeTrimmed.Length >= 4 && isYamlMarkerPiece codeTrimmed then
            let markerLen =
                let lastTok =
                    match codeTrimmed.LastIndexOf ' ' with
                    | -1 -> codeTrimmed
                    | i -> codeTrimmed.Substring(i + 1)

                if lastTok.StartsWith "schema=" then
                    min codeTrimmed.Length (lastTok.Length + 5)
                else
                    4

            for j in codeTrimmed.Length - markerLen .. codeTrimmed.Length - 1 do
                codes[j] <- Some "36"

        // ^ls: the forced head resolves against PATH only
        if line.Length > 1 && line[0] = '^' && isIdentStart line[1] then
            let mutable e = 1

            while e < line.Length && isIdentCont line[e] do
                e <- e + 1

            let word = line.Substring(1, e - 1)
            let c = if Extern.exists word then "34" else "31"

            for j in 1 .. e - 1 do
                codes[j] <- Some c

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

type Mode =
    | Strict
    | Loose

// shebang/#loose peeling — ONE derivation for the runner and the
// check-side analyzeLines
let private scriptBody (rawLines: string list) : Mode * string list * int =
    let afterShebang, shebangOffset =
        match rawLines with
        | first :: rest when first.StartsWith "#!" -> rest, 1
        | _ -> rawLines, 0

    match afterShebang with
    | first :: rest when first.Trim() = "#loose" -> Loose, rest, shebangOffset + 1
    | _ -> Strict, afterShebang, shebangOffset

type CheckedStmt =
    | CLet of name: string * te: Check.TypedExpr
    | CLetPat of binder: Weir.Ast.Pattern * te: Check.TypedExpr
    | CExpr of te: Check.TypedExpr
    | CCmd of te: Check.TypedExpr
    | CType of decl: Decl
    | CNoop
    // an imported module's replayable body [D:modules-v1] — evaluated once
    // at exec into the module's own venv, exposed as `alias.member`
    | CImport of LoadedModule

// a checked, imported module [D:modules-v1]. Its check-time contributions
// (Members -> Modules[Alias], TypeDefs -> Types, TypeNames -> ModuleTypes)
// merge into the importer's tenv; Body replays at exec to build the
// module's values. AbsPath is the normalized identity (symlinks unresolved).
and LoadedModule =
    { Alias: string
      // the module's OWN name (declared or filename-derived), independent
      // of a site's `as` — a cached module re-aliases from this [D:modules-v1]
      NaturalName: string
      AbsPath: string
      TypeDefs: (string * TypeDef) list
      Members: (string * Scheme) list
      TypeNames: string list
      Body: CheckedStmt list }

// an import failure [D:modules-v1]. File=Some for a MODULE-CONTENT error —
// reported at the module's OWN site (Line/Col into that file) plus an
// "imported here" note at the import line; File=None for an import-STATEMENT
// error (self-import, missing file, not-a-module) reported at the import line.
and ImportError =
    { File: string option
      Line: int
      Col: int
      Message: string }

// resolves an `import` to a checked module, or an ImportError [D:modules-v1].
// The caller binds it to the importing file's directory; the script-only
// (-e/REPL) and nested-import variants just return Error. Args: the literal
// path, its span (for error placement), the optional `as` alias.
and ImportLoader = string -> Span -> string option -> Result<LoadedModule, ImportError>

// the Self module [D:self-module]: script/process introspection grouped
// under one name, freeing the bare `args`/`stdin`/`scriptPath` for users.
// Members are per-run, so they inject here (not in static Builtins); the
// type side is a Modules entry, the value side mangled "Self.member" keys
// — module access checks `EField {EVar "Self"} field` -> TEVar "Self.field"
let selfMembers: Map<string, Scheme> =
    Map
        [ "pid", generalize TInt
          "args", generalize (TSeq TStr)
          "stdin", generalize (TSeq TStr)
          // scriptPath = the FILE'S OWN path (a module sees its own);
          // entryPath = the INVOKED script's, a process fact like args/stdin
          // — the same for every file in the run [D:modules-v1] (decision 12)
          "scriptPath", generalize TStr
          "entryPath", generalize TStr ]

let private baseEnvs (mode: Mode) (scriptArgs: string list) (scriptPath: string) =
    let typeEnv =
        match mode with
        | Strict -> Builtins.typeEnvStrict
        | Loose -> Builtins.typeEnv

    let typeEnv, valueEnv = Prelude.extend typeEnv Builtins.valueEnv

    let typeEnv =
        { typeEnv with
            Modules = typeEnv.Modules |> Map.add "Self" selfMembers }

    let stdinStream =
        Eval.VSeq(
            Seq.delay (fun () ->
                seq {
                    let mutable line = Console.In.ReadLine()

                    while line <> null do
                        yield Eval.VStr line
                        line <- Console.In.ReadLine()
                })
        )

    Session.ScriptArgs <- scriptArgs

    let valueEnv =
        valueEnv
        |> Map.add "Self.pid" (Eval.VInt(int64 System.Environment.ProcessId))
        |> Map.add "Self.args" (Eval.VSeq(scriptArgs |> List.map Eval.VStr :> seq<Eval.Value>))
        |> Map.add "Self.stdin" stdinStream
        // the entry IS the invoked script, so its own path and the entry
        // path coincide here; a module later overrides scriptPath with its
        // own while entryPath rides along as a process fact
        |> Map.add "Self.scriptPath" (Eval.VStr scriptPath)
        |> Map.add "Self.entryPath" (Eval.VStr scriptPath)

    typeEnv, valueEnv

// THE base resolver over a type env — one constructor behind the
// script/fmt/REPL/CLI call sites (was ×4 verbatim; the census's
// conviction)
let resolver (typeEnv: TypeEnv) : Parser.Resolver =
    { IsKnown = fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules
      IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
      IsExternal = Extern.exists
      ExternalNames = fun () -> Extern.names () :> seq<string> }

let private located (path: string) (lineNo: int) (msg: string) : string =
    let msg =
        if msg.StartsWith "[1:" then
            $"[{lineNo}:" + msg.Substring 3
        else
            msg

    $"{path}:{lineNo}: {msg}"

// Streaming output for command-mode statements — the single exempt form.
// The seq case goes through Eval.writeLines, the same renderer print uses.
let private printResult (v: Eval.Value) =
    match v with
    | Eval.VStr s -> Console.WriteLine s
    | Eval.VSeq items -> Eval.writeLines items
    // unit-valued command statements (| orFail) print NOTHING —
    // the assert idiom is silent on success [D:exit-reifiers]
    | Eval.VUnit -> ()
    | other -> Console.WriteLine(Eval.formatValue other)

// The statement rule: a pure expression statement must have type unit.
// Classification is the parser's mode decision alone (SCmd vs SExpr) — no
// name lookup, no type direction; the removed form-2 exemption (bare
// sh/cmd applications) must not creep back in here.
let discardError (ty: Ty) : string option =
    match ty with
    | TUnit -> None
    // a hole-typed statement descends from an already-reported failed
    // let — silent, not a second complaint [PLAN-diagnostics-arc B6]
    | TVar v when v.StartsWith "__hole" -> None
    | TSeq TUnit ->
        Some "this statement computes a seq<unit> and discards it — a lazy effect sequence never runs; use Seq.iter"
    | TSeq(TNamed("FileRow", [])) as ty ->
        Some(
            $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"
            + " (for a plain listing, ^ls runs the real program)"
        )
    | ty -> Some $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"

// ---------------------------------------------------------------------------
// The checked-statement pipeline — ONE owner [D:one-pipeline]:
// parse -> statement dispatch -> check -> statement-rule gate,
// physical spans computed INSIDE. Every consumer (runner, REPL, -e,
// check/LSP, the oracle mirror) calls this and only renders.

type CheckedKind =
    | KType of Decl
    | KLet of name: string * scheme: Scheme * te: Check.TypedExpr
    | KLetPat of binder: Pattern * schemes: (string * Scheme) list * te: Check.TypedExpr
    | KCmd of te: Check.TypedExpr
    | KExpr of te: Check.TypedExpr
    // the module marker [D:modules-v1] — makes the file a module (decl-only,
    // not runnable); the runner turns it into the running-a-module error
    | KModule of name: string option * kwSpan: Span
    // a resolved import [D:modules-v1] — its Env merge already applied
    | KImport of LoadedModule

[<RequireQualifiedAccess>]
type StmtTag =
    | Type
    | Let
    | LetPat
    | Cmd
    | Expr

type StmtDiag =
    { PhysLine: int
      PhysCol: int
      PhysEnd: (int * int) option // physical end of the span, when known
      Tag: StmtTag option // None for parse failures (kind unknown)
      HasCol: bool // false only for col-less parse failures
      Span: Span option // None for parse failures
      Parse: bool // parse error (FParsec text) vs type error (message)
      Message: string
      // multi-file [D:modules-v1]: File=Some when PhysLine/PhysCol point
      // into ANOTHER file (a module's own error site); Note carries an
      // extra (line, col, message) in the CURRENT file (the import-line
      // "imported here"). Both default to None (single-file).
      File: string option
      Note: (int * int * string) option
      // the runner prints warnings even when the discard gate then
      // errors (standing behavior) — they travel with the diag
      Warnings: (int * int * string) list }

type CheckedStatement =
    { Kind: CheckedKind
      Env: TypeEnv // the env AFTER this statement (bindings added)
      Warnings: (int * int * string) list } // physical line, col, message

// gateExprs: scripts apply the statement rule (values must be bound or
// printed); the REPL and -e ECHO values instead — the same pipeline,
// one explicit switch, never a re-derivation
// assume only COMMAND-SHAPED words (letter-initial, ident chars +
// dashes; never keywords, never dotted): the expression grammar must
// keep claiming Env.load, from-adapters, and punctuation heads
let assumeResolver (tenv: TypeEnv) : Parser.Resolver =
    { resolver tenv with
        IsExternal =
            fun n ->
                Extern.exists n
                || (n.Length > 0
                    && System.Char.IsLetter n[0]
                    && not (Parser.isKeyword n)
                    // a declared type or module is never a command — else the
                    // named literal `Ctx { .. }` / `Paths.Ctx { .. }` mis-parses
                    // under check's assume-command rule [D:modules-v1]
                    && not (Map.containsKey n tenv.Types)
                    && not (Map.containsKey n tenv.Modules)
                    && n |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '-')) }

// mkR builds the resolver FROM THE CURRENT ENV per statement, so
// script-defined names are known at parse time — bindings shadow PATH
// commands by construction (`let cat = ...` then `cat x` is an
// application; ^cat forces the binary) [D:assume-resolver].
// FParsec dumps embed the ASSEMBLED logical line — never what the user
// wrote. Strip every snippet+caret block, keep the diagnostic text,
// and translate embedded positions to physical line/col
// [D:clean-parse-dump].
let private cleanParseDump (ll: LogicalLine) (msg: string) : string =
    // no-leak [D:sibling-sentinel]: FParsec may echo the assembled line OR
    // list the sentinel as an expected token; the machine sentinel must
    // never surface — render both the raw char and FParsec's  escape
    // (its expected-set rendering) as ';'
    let msg = msg.Replace(Parser.sibSepStr, ";").Replace("\\u001f", ";")
    let lines = msg.Replace("\r\n", "\n").Split('\n') |> Array.toList

    let isCaret (l: string) =
        l.Trim() <> "" && l.Trim() |> Seq.forall ((=) '^')

    let translateErrorLine (l: string) : string option =
        let m = System.Text.RegularExpressions.Regex.Match(l, @"Error in Ln: 1 Col: (\d+)")

        if m.Success then
            let pl, pc = translate ll (int m.Groups[1].Value)
            Some($"at line {pl}, col {pc}:")
        else
            None

    let rec go (first: bool) (acc: string list) (rest: string list) =
        match rest with
        | [] -> List.rev acc
        | l :: tail ->
            match translateErrorLine l with
            | Some pos ->
                // drop the snippet rows up to and including the caret
                let indent = l |> Seq.takeWhile ((=) ' ') |> Seq.length

                let rec dropSnippet r =
                    match r with
                    | [] -> []
                    | x :: xs when isCaret x -> xs
                    | _ :: xs -> dropSnippet xs

                // the FIRST position is the diag's own — consumers render
                // it with the source line; only BACKTRACK positions stay
                let acc' = if first then acc else (String(' ', indent) + pos) :: acc
                go false acc' (dropSnippet tail)
            | None -> go first (l :: acc) tail

    go true [] lines |> List.filter (fun l -> l.Trim() <> "") |> String.concat "\n"

// merge a loaded module into the importer's env [D:modules-v1]: values
// (and union ctors) under Modules[Alias], types flat under their plain
// name, and the provenance for the qualified literal under ModuleTypes.
let private mergeModule (tenv: TypeEnv) (lm: LoadedModule) : TypeEnv =
    { tenv with
        Modules = Map.add lm.Alias (Map.ofList lm.Members) tenv.Modules
        Types = lm.TypeDefs |> List.fold (fun ts (n, d) -> Map.add n d ts) tenv.Types
        ModuleTypes = Map.add lm.Alias (Set.ofList lm.TypeNames) tenv.ModuleTypes }

let checkStatement
    (gateExprs: bool)
    (mkR: TypeEnv -> Parser.Resolver)
    (loadImport: ImportLoader)
    (tenv: TypeEnv)
    (ll: LogicalLine)
    : Result<CheckedStatement, StmtDiag> =
    let r = mkR tenv

    let typed (tag: StmtTag) (terr: Check.TypeError) =
        // an Origin is already physical, recorded by the statement that
        // owned the access [D:row-provenance] — it bypasses translate
        let physLine, physCol, physEnd =
            match terr.Origin with
            | Some(ol, oc, len) -> ol, oc, Some(ol, oc + len)
            | None ->
                let pl, pc = translate ll terr.Span.Start.Col
                pl, pc, Some(translate ll terr.Span.End.Col)

        { PhysLine = physLine
          PhysCol = physCol
          PhysEnd = physEnd
          Tag = Some tag
          HasCol = true
          Span = Some terr.Span
          Parse = false
          Message = terr.Message
          File = None
          Note = None
          Warnings = [] }

    let warningsOf te =
        [ for w in Check.warnings te do
              let physLine, physCol = translate ll w.Span.Start.Col
              physLine, physCol, w.Message ]

    // physical translator for the checker's boundary-crossing
    // positions [D:row-provenance]; reset so non-statement consumers
    // (Complete, tests) never see a stale statement
    Check.toPhys.Value <- Some(translate ll)

    try
        match Parser.parseLineFull r ll.Text with
        | Error f ->
            // FParsec's primary error is often IRRELEVANT when the real cause
            // is an unresolvable command head (the backtrack note buries it).
            // Retry under the assume-resolver: if that parses, the failure IS
            // missing commands — name them precisely instead of the dump.
            let missingHeads =
                match Parser.parseLineFull (assumeResolver tenv) ll.Text with
                | Error _ -> []
                | Ok stmt ->
                    let e =
                        match stmt with
                        | SLet(_, e)
                        | SLetPat(_, e)
                        | SExpr e
                        | SCmd e -> Some e
                        | SType _
                        | SModule _
                        | SImport _ -> None

                    let rec heads (e: Expr) =
                        (match e.Kind with
                         | ECmd(prog, _, _) when not (Extern.exists prog) -> [ prog, e.Span ]
                         | _ -> [])
                        @ (exprChildren e |> List.collect heads)

                    e |> Option.map heads |> Option.defaultValue []

            match missingHeads with
            | (prog, span) :: rest ->
                let physLine, physCol = translate ll span.Start.Col

                let others =
                    match rest |> List.map fst |> List.distinct |> List.filter ((<>) prog) with
                    | [] -> ""
                    | more ->
                        let joined = String.concat ", " more
                        $" (also missing: {joined})"

                Error
                    { PhysLine = physLine
                      PhysCol = physCol
                      PhysEnd = Some(translate ll span.End.Col)
                      Tag = None
                      HasCol = true
                      Span = Some span
                      Parse = true
                      Message =
                        $"unknown command '{prog}' — not found on PATH{others}. weir resolves command names before running: install the tool, or run it through sh -c"
                      File = None
                      Note = None
                      Warnings = [] }
            | [] ->
                let physLine, physCol, hasCol =
                    match f.Col with
                    | Some col ->
                        let l, c = translate ll col
                        l, c, true
                    | None -> ll.Head, 1, false

                Error
                    { PhysLine = physLine
                      PhysCol = physCol
                      PhysEnd = None
                      Tag = None
                      HasCol = hasCol
                      Span = None
                      Parse = true
                      Message = cleanParseDump ll f.Message
                      File = None
                      Note = None
                      Warnings = [] }
        | Ok(SType decl) ->
            match Check.checkDecl tenv decl with
            | Error terr -> Error(typed StmtTag.Type terr)
            | Ok tenv' ->
                Ok
                    { Kind = KType decl
                      Env = tenv'
                      Warnings = [] }
        | Ok(SLetPat(pat, e)) ->
            match Check.typecheckBinder tenv pat e with
            | Error terr -> Error(typed StmtTag.LetPat terr)
            | Ok(te, schemes) ->
                Ok
                    { Kind = KLetPat(pat, schemes, te)
                      Env =
                        { tenv with
                            Values = schemes |> List.fold (fun vs (n, sch) -> Map.add n sch vs) tenv.Values }
                      Warnings = warningsOf te }
        | Ok(SLet(name, e)) ->
            // SLet carries the name as a bare string, so re-derive its own
            // columns from the statement text (grammar: ws `let` ws name)
            // [D:squiggle-on-binder]
            let nameSpan =
                let m = Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+")

                if m.Success then
                    { Start = { Line = 1; Col = m.Length + 1 }
                      End =
                        { Line = 1
                          Col = m.Length + 1 + name.Length } }
                else
                    e.Span

            match
                Check.checkBinderName nameSpan name
                |> Result.bind (fun () -> Check.typecheckWith tenv e)
            with
            | Error terr -> Error(typed StmtTag.Let terr)
            | Ok(te, cs, origins) ->
                let scheme = generalizeWithOrigins cs origins te.Ty

                Ok
                    { Kind = KLet(name, scheme, te)
                      Env =
                        { tenv with
                            Values = Map.add name scheme tenv.Values }
                      Warnings = warningsOf te }
        | Ok(SCmd e) ->
            match Check.typecheck tenv e with
            | Error terr -> Error(typed StmtTag.Cmd terr)
            | Ok te ->
                // a bool-valued chain (| succeeds) as a bare statement is a
                // DISCARDED value, not a stream — the discard family
                // [D:exit-reifiers]. `| complete` (a Completed record) joins
                // it: same mistake, keyed on the distinctive value type.
                let rec exitCodeSpine (t: Check.TypedExpr) =
                    match t.Kind with
                    | Check.TEVar("|exitCoded" | "|exitCodedEnv") -> true
                    | Check.TEApp(f, _) -> exitCodeSpine f
                    | _ -> false

                match te.Ty with
                | TBool ->
                    Error(
                        typed
                            StmtTag.Cmd
                            { Span = te.Span
                              Message =
                                "this statement computes a bool and discards it — bind it "
                                + "(let ok = ... | succeeds) or use it in a condition"
                              Origin = None }
                    )
                // `set +e; cmd; rc=$?` muscle memory lands here [D:exit-reifiers]
                | TInt when exitCodeSpine te ->
                    Error(
                        typed
                            StmtTag.Cmd
                            { Span = te.Span
                              Message =
                                "this statement computes the exit code and discards it — bind it "
                                + "(let rc = <command> | exitCode), match on it, or drop '| exitCode' if you don't need the code"
                              Origin = None }
                    )
                // `| complete` captures a Completed record — reading it is
                // the point, so discarding it is the family mistake
                | TNamed("Completed", _) ->
                    Error(
                        typed
                            StmtTag.Cmd
                            { Span = te.Span
                              Message =
                                "this statement computes a Completed record and discards it — bind it "
                                + "(let r = ... | complete) or read a field (.exitCode, .stdout)"
                              Origin = None }
                    )
                | _ ->
                    Ok
                        { Kind = KCmd te
                          Env = tenv
                          Warnings = warningsOf te }
        | Ok(SExpr e) ->
            // statement position demands unit, so a commandish TAIL arms
            // [D:within-scopes] — reaching through scopes and let-ins;
            // the REPL (gateExprs=false) keeps its echo instead
            let e = if gateExprs then Check.armTail e else e

            match Check.typecheck tenv e with
            | Error terr -> Error(typed StmtTag.Expr terr)
            | Ok te ->
                // the likeliest intent behind a discarded $(cmd) is "run
                // it" — the wrapper is what is in the way, so the error
                // names the DROP [D:district-retirement] (the wrap-it
                // hint's principle, inverted)
                let dropClause =
                    match e.Kind with
                    | ECapture { Kind = ECmd _ }
                    | ECapture { Kind = EPipe(_, { Kind = ECmd _ }) } -> ", or drop the $( ) to run it as a command"
                    | _ -> ""

                match (if gateExprs then discardError te.Ty else None) with
                | Some msg ->
                    let msg = msg + dropClause

                    Error
                        { typed
                              StmtTag.Expr
                              { Span = e.Span
                                Message = msg
                                Origin = None } with
                            Warnings = warningsOf te }
                | None ->
                    Ok
                        { Kind = KExpr te
                          Env = tenv
                          Warnings = warningsOf te }
        | Ok(SModule(nameOpt, kwSpan)) ->
            // the marker adds no bindings; decl-only enforcement and the
            // running-a-module error are the caller's (loadModule / run)
            Ok
                { Kind = KModule(nameOpt, kwSpan)
                  Env = tenv
                  Warnings = [] }
        | Ok(SImport(path, pathSpan, aliasOpt)) ->
            let importLine, importCol = translate ll pathSpan.Start.Col

            // a module-CONTENT error (File=Some) reports at the module's OWN
            // site [D:modules-v1], with an "imported here" note at the import
            // line; an import-STATEMENT error reports at the import line
            let importDiag (e: ImportError) =
                match e.File with
                | Some mf ->
                    { PhysLine = e.Line
                      PhysCol = e.Col
                      PhysEnd = None
                      Tag = None
                      HasCol = true
                      Span = None
                      Parse = false
                      Message = e.Message
                      File = Some mf
                      // point at THIS level's import; the outermost (entry)
                      // level runs last, so its note (in the entry file) wins
                      Note = Some(importLine, importCol, "imported here")
                      Warnings = [] }
                | None ->
                    { PhysLine = importLine
                      PhysCol = importCol
                      PhysEnd = Some(translate ll pathSpan.End.Col)
                      Tag = None
                      HasCol = true
                      Span = Some pathSpan
                      Parse = false
                      Message = e.Message
                      File = None
                      Note = None
                      Warnings = [] }

            let stmtErr msg =
                importDiag
                    { File = None
                      Line = 0
                      Col = 0
                      Message = msg }

            match loadImport path pathSpan (aliasOpt |> Option.map fst) with
            | Error e -> Error(importDiag e)
            | Ok lm when Map.containsKey lm.Alias tenv.Modules ->
                Error(
                    stmtErr
                        $"the name '{lm.Alias}' is already a module in scope; import it under a different name with 'as'"
                )
            | Ok lm ->
                match lm.TypeNames |> List.tryFind (fun n -> Map.containsKey n tenv.Types) with
                | Some clash ->
                    Error(
                        stmtErr
                            $"import '{lm.Alias}' declares a type '{clash}' that is already declared here; rename one (cross-module same-name types are not yet distinguishable)"
                    )
                | None ->
                    Ok
                        { Kind = KImport lm
                          Env = mergeModule tenv lm
                          Warnings = [] }
    finally
        Check.toPhys.Value <- None

// ---- the module loader [D:modules-v1] ------------------------------------
// A module's OWN base env: builtins (strict) + prelude + Self, with
// Self.scriptPath = the module's own path. Pure — no stdin/args/Session
// wiring, so loading a module never disturbs the entry's process facts.
let private moduleBaseEnvs (absPath: string) : TypeEnv * Eval.Env =
    let te, ve = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    { te with
        Modules = te.Modules |> Map.add "Self" selfMembers },
    ve |> Map.add "Self.scriptPath" (Eval.VStr absPath)

// the ONE import path resolver [D:modules-v1]: absolute + normalized (for
// identity and, later, caching); symlinks stay UNRESOLVED — the Path.glob
// precedent, two links to one file are two files.
let private resolveImportPath (importingAbsPath: string) (path: string) : string =
    let dir = IO.Path.GetDirectoryName importingAbsPath
    IO.Path.GetFullPath(IO.Path.Combine(dir, path))

// F#'s filename fallback for a bare `module`: capitalize the base name. A
// non-identifier filename has no derivation (name it, or import `as`).
let private deriveModuleName (absPath: string) : string option =
    let bare = IO.Path.GetFileNameWithoutExtension absPath

    if
        bare.Length > 0
        && System.Char.IsLetter bare[0]
        && bare |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')
    then
        Some(string (System.Char.ToUpper bare[0]) + bare.Substring 1)
    else
        None

// does evaluating this RHS at import RUN a command? — the weak purity rule
// [D:modules-v1]. Eager positions only, STOPS at lambdas: a command in a
// lambda body is deferred, so a param-ful `let f r = git …` is a function,
// not an import-time effect; only a paramless `let x = git …` is rejected.
let rec private runsCommandT (te: Check.TypedExpr) : bool =
    match te.Kind with
    | Check.TECmd _ -> true
    | Check.TELambda _
    | Check.TELambdaPat _ -> false
    | _ -> Check.childExprs te |> List.exists runsCommandT

// buffer-over-disk [D:modules-v1]: when the LSP sets this, an imported file's
// content is read through it (open editor buffers first, then disk) so an
// unsaved dependency checks against what the user sees; None -> CLI, plain
// disk (decision 14).
let importSourceOverride: (string -> string list option) option ref = ref None

let private readImportSource (absPath: string) : string list option =
    match importSourceOverride.Value with
    | Some f -> f absPath
    | None ->
        if IO.File.Exists absPath then
            Some(IO.File.ReadAllLines absPath |> Array.toList)
        else
            None

// resolve + check an imported module to a LoadedModule, or an ImportError.
// THE GRAPH [D:modules-v1]: `cache` (normalized abs path -> module) checks a
// shared module ONCE (diamonds); `chain` is the current DFS path of importing
// files, so a repeat is a cycle. Transitive: a reached module's own imports
// resolve too, sharing the cache with this module pushed on the chain.
let rec loadModuleCached
    (cache: System.Collections.Generic.Dictionary<string, LoadedModule>)
    (chain: string list)
    (importingAbsPath: string)
    (path: string)
    (importAs: string option)
    : Result<LoadedModule, ImportError> =
    let absPath = resolveImportPath importingAbsPath path

    // an import-STATEMENT error reports at the import line (File=None)
    let stmt msg =
        Error
            { File = None
              Line = 0
              Col = 0
              Message = msg }

    let notAModule =
        stmt $"{absPath} is not a module; add `module` at the top, or invoke it as a command"

    if absPath = importingAbsPath then
        stmt "a file cannot import itself"
    elif List.contains absPath chain then
        // a cycle — same detector as self-import, different rendering
        // (decision 9): render the loop by file name
        let recent = chain |> List.takeWhile ((<>) absPath)

        let loop =
            (absPath :: (List.rev recent @ [ absPath ]))
            |> List.map IO.Path.GetFileName
            |> String.concat " → "

        stmt $"import cycle: {loop}"
    elif cache.ContainsKey absPath then
        // a diamond's shared module is checked ONCE; re-alias per import site
        let cached = cache[absPath]

        Ok
            { cached with
                Alias = importAs |> Option.defaultValue cached.NaturalName }
    elif (readImportSource absPath) |> Option.isNone then
        stmt $"cannot resolve import: no file at {absPath}"
    else
        let rawLines = readImportSource absPath |> Option.get
        let _, body, bodyOffset = scriptBody rawLines

        let assembled = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> assemble // raw lines: assemble classifies/strips internally [D:content-bytes]

        match assembled with
        | Error msg -> stmt $"{absPath}: {msg}"
        | Ok [] -> notAModule
        | Ok(first :: rest) ->
            let baseTenv, _ = moduleBaseEnvs absPath

            match Parser.parseLineFull (resolver baseTenv) first.Text with
            | Ok(SModule(declName, _)) ->
                let natural = declName |> Option.orElseWith (fun () -> deriveModuleName absPath)

                match importAs |> Option.orElse natural with
                | None ->
                    stmt
                        $"cannot derive a module name from '{IO.Path.GetFileName absPath}'; name it (module Name) or import it as a name (import \"…\" as Name)"
                | Some alias ->
                    // transitive: a reached module's OWN imports resolve, sharing
                    // the cache, with this module pushed on the chain
                    let childLoader: ImportLoader =
                        fun p _ a -> loadModuleCached cache (absPath :: chain) absPath p a

                    // a module-CONTENT error reports at the module's OWN site
                    let at line col msg : Result<_, ImportError> =
                        Error
                            { File = Some absPath
                              Line = line
                              Col = col
                              Message = msg }

                    let rec go (tenv: TypeEnv) (accBody: CheckedStmt list) (stmts: LogicalLine list) =
                        match stmts with
                        | [] -> Ok(tenv, List.rev accBody)
                        | (ll: LogicalLine) :: tail ->
                            match checkStatement true resolver childLoader tenv ll with
                            | Error d ->
                                // a DEEPER module's error (File already set)
                                // propagates unchanged; this module's OWN error
                                // takes this module's site
                                match d.File with
                                | Some _ ->
                                    Error
                                        { File = d.File
                                          Line = d.PhysLine
                                          Col = d.PhysCol
                                          Message = d.Message }
                                | None -> at d.PhysLine d.PhysCol d.Message
                            | Ok chk ->
                                match chk.Kind with
                                | KType decl -> go chk.Env (CType decl :: accBody) tail
                                | KImport lm -> go chk.Env (CImport lm :: accBody) tail
                                | KLet(_, _, te) when runsCommandT te ->
                                    at
                                        ll.Head
                                        1
                                        "a module 'let' cannot run a command at import — wrap it in a function (let f () = …), the command runs when a script calls it"
                                | KLetPat(_, _, te) when runsCommandT te ->
                                    at ll.Head 1 "a module 'let' cannot run a command at import"
                                | KLet(name, _, te) -> go chk.Env (CLet(name, te) :: accBody) tail
                                | KLetPat(pat, _, te) -> go chk.Env (CLetPat(pat, te) :: accBody) tail
                                | KModule _ -> at ll.Head 1 "a file has at most one 'module' marker, and it comes first"
                                | KCmd _
                                | KExpr _ ->
                                    at
                                        ll.Head
                                        1
                                        "a module declares only — 'type' and 'let', no commands or bare expressions"

                    match go baseTenv [] rest with
                    | Error e -> Error e
                    | Ok(finalTenv, moduleBody) ->
                        // a module exports only its OWN types (from its Body's
                        // decls), NOT what it transitively imported (no re-export,
                        // decision 3); Members already exclude imported ones
                        // (those live under Modules[·], not Values)
                        let typeDefs =
                            moduleBody
                            |> List.choose (function
                                | CType decl ->
                                    Map.tryFind decl.Name finalTenv.Types |> Option.map (fun d -> decl.Name, d)
                                | _ -> None)

                        let members =
                            finalTenv.Values
                            |> Map.toList
                            |> List.filter (fun (n, _) -> not (Map.containsKey n baseTenv.Values))

                        let loaded =
                            { Alias = alias
                              NaturalName = natural |> Option.defaultValue alias
                              AbsPath = absPath
                              TypeDefs = typeDefs
                              Members = members
                              TypeNames = typeDefs |> List.map fst
                              Body = moduleBody }

                        cache[absPath] <- loaded
                        Ok loaded
            | Ok _ -> notAModule
            | Error _ -> notAModule

// the module-body replayer [D:modules-v1]: evaluate a checked module's Body
// in its OWN clean env (Self.scriptPath is the module's; process facts ride
// from the entry), exposing a NESTED import's members as `alias.member` for
// this module's own lets. Returns the module's venv (bindings under bare
// names); the caller exposes THIS module's Members qualified.
let rec replayModule (procFacts: (string * Eval.Value) list) (lm: LoadedModule) : Eval.Env =
    let _, mBase0 = moduleBaseEnvs lm.AbsPath
    let mBase = procFacts |> List.fold (fun m (k, v) -> Map.add k v m) mBase0

    let expose (alias: string) (members: (string * Scheme) list) (from: Eval.Env) (into: Eval.Env) =
        members
        |> List.fold
            (fun acc (n, _) ->
                match Map.tryFind n from with
                | Some v -> Map.add $"{alias}.{n}" v acc
                | None -> acc)
            into

    let rec replay (mv: Eval.Env) body =
        match body with
        | [] -> mv
        | CType decl :: t ->
            let mv' =
                match decl.Body with
                | DUnion cases -> Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) mv
                | DRecord _ -> mv

            replay mv' t
        | CLet(name, te) :: t -> replay (Map.add name (Eval.eval mv te) mv) t
        | CLetPat(pat, te) :: t ->
            let bs = Eval.bindPattern pat (Eval.eval mv te)
            replay (bs |> List.fold (fun m (n, v) -> Map.add n v m) mv) t
        | CImport nested :: t ->
            let nestedVenv = replayModule procFacts nested
            replay (expose nested.Alias nested.Members nestedVenv mv) t
        | _ :: t -> replay mv t

    replay mBase lm.Body

// the -e / REPL import loader [D:modules-v1]: there is no file to resolve
// relative paths against, so import is script-only (decision 12)
let scriptOnlyImport: ImportLoader =
    fun _ _ _ ->
        Error
            { File = None
              Line = 0
              Col = 0
              Message =
                "import is script-only — it needs a file to resolve its path against (not available with -e or in the REPL)" }

// ---------------------------------------------------------------------------
// weir check [--json] [D:check-lsp-chain]. Check-everything, no
// evaluation BY CONSTRUCTION (this function cannot reach Eval).
// Statement-level error RECOVERY: a failed statement records its diag
// and checking continues with the env unchanged, so a multi-error file
// reports every independent error. Codes are SEEDED from the message
// families (structured codes at error origin are the parked upgrade).

type Diagnostic =
    { File: string
      Line: int
      Col: int
      EndLine: int option
      EndCol: int option
      Severity: string // "error" | "warning"
      Code: string
      Message: string }

let private codeOf (parse: bool) (msg: string) : string =
    if parse then
        "parse"
    elif msg.StartsWith "binding names start lowercase" then
        "casing-law"
    elif msg.Contains "discards it" then
        "discard"
    elif msg.StartsWith "a sequenced expression must be unit" then
        "seq-unit"
    elif msg.StartsWith "this pattern can fail" then
        "refutable-binder"
    elif msg.StartsWith "match is not exhaustive" || msg.Contains "needs a catch-all" then
        "non-exhaustive"
    elif msg.Contains "unreachable" then
        "unreachable-arm"
    elif msg.StartsWith "invalid regex" then
        "regex"
    elif msg.StartsWith "this regex has" then
        "regex-arity"
    elif msg.StartsWith "cannot sort by this key" then
        "ord-key"
    elif msg.Contains "equatable" || msg.Contains "cannot be compared" then
        "eq"
    elif msg.Contains "cannot render functions" then
        "show-fn"
    elif msg.StartsWith "unbound variable" then
        "unbound"
    elif msg.Contains "nothing determines" then
        "ambiguous-constraint"
    else
        "check"

// AOT-safe JSON writing: Utf8JsonWriter (reflection-free, the write
// twin of the JsonDocument reader) — escaping is the library's job,
// never string interpolation's. UnsafeRelaxedJsonEscaping: "unsafe"
// means HTML-embedding only — these payloads are LSP/CLI, never HTML;
// the default encoder's \u0022-style quote escaping is valid but
// trips naive clients [D:json-relaxed-escaping]
let private jsonWriterOptions =
    System.Text.Json.JsonWriterOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let jsonBuild (build: System.Text.Json.Utf8JsonWriter -> unit) : string =
    use ms = new IO.MemoryStream()
    use w = new System.Text.Json.Utf8JsonWriter(ms, jsonWriterOptions)
    build w
    w.Flush()
    Text.Encoding.UTF8.GetString(ms.ToArray())

let writeDiag (w: System.Text.Json.Utf8JsonWriter) (d: Diagnostic) =
    w.WriteStartObject()
    w.WriteString("file", d.File)
    w.WriteNumber("line", d.Line)
    w.WriteNumber("col", d.Col)

    match d.EndLine, d.EndCol with
    | Some el, Some ec ->
        w.WriteNumber("endLine", el)
        w.WriteNumber("endCol", ec)
    | _ -> ()

    w.WriteString("severity", d.Severity)
    w.WriteString("code", d.Code)
    w.WriteString("message", d.Message)
    w.WriteEndObject()

// doc-comment ALIGNMENT lint [D:doc-comments]: a `///` attaches to the
// declaration below it, so it must sit at that declaration's indent —
// the entry anchor, exactly as an attribute line does. Docs are INERT
// (dropped before assembly), so a misaligned one cannot mis-parse: this
// is a LINT, not the parse-time attribute machinery. Pinned both ways
// (doc above a field, doc above a union case).
let private docMisalignments (path: string) (lines: string list) : Diagnostic list =
    let indent (s: string) = s.Length - s.TrimStart().Length
    let arr = List.toArray lines
    let diags = ResizeArray<Diagnostic>()
    let mutable runStart = -1 // index of the first `///` in the pending run

    let masked = districtContentMask lines

    lines
    |> List.iteri (fun idx raw ->
        if masked[idx] then
            () // district content is bytes — the lint has no claim [D:content-bytes]
        elif isDocLine raw then
            (if runStart < 0 then
                 runStart <- idx)
        elif isAttributeOnlyLine raw then
            () // transparent: the doc rides through to the declaration below
        elif raw.Trim() = "" || (stripComment raw).Trim() = "" then
            runStart <- -1 // a blank / plain-// breaks the run — no attachment, no claim
        else
            (if runStart >= 0 then
                 match declName raw with
                 | Some _ ->
                     let anchor = indent raw

                     for k in runStart .. idx - 1 do
                         let di = indent arr[k]

                         if di <> anchor then
                             diags.Add
                                 { File = path
                                   Line = k + 1
                                   Col = di + 1
                                   EndLine = None
                                   EndCol = None
                                   Severity = "error"
                                   Code = "doc-align"
                                   Message =
                                     $"this /// doc sits at column {di + 1}, but the declaration it documents is at column {anchor + 1} — a doc aligns with what it describes" }
                 | None -> ())

            runStart <- -1)

    List.ofSeq diags

// full analysis for tooling (the LSP re-frames this): diagnostics AND
// the successfully-checked statements with their logical lines — plus
// the initial env, so consumers can pick the in-scope env per position
// external contracts: schema validation [D:yaml-schemas]. Walks every
// typed district carrying a `schema=` declaration; STRUCTURAL checks
// always, VALUE checks where the splice's type permits. Check-time
// only, and it reads VENDORED files exclusively — never the network
// (the never-fetch-during-check pin).
let schemaDiagnostics (path: string) (pairs: (LogicalLine * CheckedStatement) list) : Diagnostic list =
    let cache =
        System.Collections.Generic.Dictionary<string, Result<Contracts.Schema, string>>()

    let loadSchema (name: string) : Result<Contracts.Schema, string> =
        match cache.TryGetValue name with
        | true, r -> r
        | _ ->
            let r =
                let fromDir =
                    try
                        let full = IO.Path.GetFullPath path
                        let d = IO.Path.GetDirectoryName full
                        if String.IsNullOrEmpty d then "." else d
                    with _ ->
                        "."

                match Contracts.findWeirDir fromDir with
                | Error e -> Error $"schema '{name}': {e}"
                | Ok weirDir ->
                    let file = IO.Path.Combine(weirDir, "schemas", name + ".json")

                    if not (IO.File.Exists file) then
                        // the checker can tell restore from add: a lock
                        // entry means the artifact was declared (a fresh
                        // clone restores); no entry means it never was
                        let locked =
                            match Contracts.readLock weirDir with
                            | Ok entries -> entries |> List.exists (fun e -> e.Kind = "schema" && e.Name = name)
                            | Error _ -> false

                        if locked then
                            Error
                                $"schema '{name}': no {file} (searched from {weirDir}) — the lock records it; run `weir restore`"
                        else
                            Error
                                $"schema '{name}': no {file} (searched from {weirDir}) — add it: weir add schema <url> --as {name}"
                    else
                        Contracts.parseSchema name (IO.File.ReadAllText file)

            cache[name] <- r
            r

    pairs
    |> List.collect (fun (ll, chk) ->
        let roots =
            match chk.Kind with
            | KLet(_, _, te)
            | KLetPat(_, _, te)
            | KCmd te
            | KExpr te -> [ te ]
            | KType _
            | KModule _
            | KImport _ -> []

        let rec districts (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TEYaml(tpl, Some name) -> [ te.Span, name, tpl ]
             | _ -> [])
            @ (Check.childExprs te |> List.collect districts)

        roots
        |> List.collect districts
        |> List.collect (fun (dspan, name, tpl) ->
            let mk (sp: Span) (msg: string) =
                let l1, c1 = translate ll sp.Start.Col
                let l2, c2 = translate ll (max sp.Start.Col (sp.End.Col - 1))

                { File = path
                  Line = l1
                  Col = c1
                  EndLine = Some l2
                  EndCol = Some(c2 + 1)
                  Severity = "error"
                  Code = "schema"
                  Message = msg }

            match loadSchema name with
            | Error e -> [ mk dspan e ]
            | Ok schema -> Contracts.validateTpl name "" schema tpl |> List.map (fun (sp, m) -> mk sp m)))

let analyzeLines
    (path: string)
    (rawLines: string list)
    : Diagnostic list * (LogicalLine * CheckedStatement) list * TypeEnv * LogicalLine list =
    let _, body, bodyOffset = scriptBody rawLines
    let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)

    let typeEnv0, _ = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    let typeEnv0 =
        { typeEnv0 with
            Modules = typeEnv0.Modules |> Map.add "Self" selfMembers }

    // imports resolve relative to the file being checked [D:modules-v1];
    // one cache per check dedups a diamond, one chain catches a cycle
    let analyzeImport: ImportLoader =
        let absPath = IO.Path.GetFullPath path
        let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
        fun p _ alias -> loadModuleCached cache [ absPath ] absPath p alias

    Extern.refresh ()

    // CHECK-ONLY consumers assume unknown heads are commands, so a
    // script for uninstalled tools still parses; each head missing
    // from PATH becomes a WARNING (cmd-not-found). The RUNNER keeps
    // hard resolution — same pipeline, explicitly different resolver
    // input (the gateExprs pattern), the verdict difference pinned
    // [D:assume-resolver].

    // ASSEMBLY RECOVERY [D:assembly-recovery]: drop the line the
    // error names and retry, keeping each drop as a diagnostic. The
    // RUNNER keeps hard assembly failure; tooling-only.
    let assemblyDiags = ResizeArray<Diagnostic>()

    let rec assembleRecovering (attempts: int) (input: (int * string) list) =
        match assemble input with
        | Ok lls -> lls
        | Error msg when attempts > 0 ->
            let line =
                match msg.Split(' ') |> Array.tryItem 1 with
                | Some tok ->
                    tok.TrimEnd(':')
                    |> fun t ->
                        (match System.Int32.TryParse t with
                         | true, n -> n
                         | false, _ -> -1)
                | None -> -1

            assemblyDiags.Add
                { File = path
                  Line = (if line > 0 then line else 1)
                  Col = 1
                  EndLine = None
                  EndCol = None
                  Severity = "error"
                  Code = "assembly"
                  Message = msg }

            if line > 0 && input |> List.exists (fun (n, _) -> n = line) then
                assembleRecovering (attempts - 1) (input |> List.filter (fun (n, _) -> n <> line))
            else
                []
        | Error _ -> []

    let logicalLines = numbered |> assembleRecovering 10 // raw lines: assemble classifies/strips internally

    (let diags0 = List.ofSeq assemblyDiags
     diags0 |> ignore)

    match Some logicalLines with
    | None -> [], [], typeEnv0, []
    | Some logicalLines ->
        let diags = ResizeArray<Diagnostic>()
        let stmts = ResizeArray<LogicalLine * CheckedStatement>()

        let warn (wl, wc, wm) =
            diags.Add
                { File = path
                  Line = wl
                  Col = wc
                  EndLine = None
                  EndCol = None
                  Severity = "warning"
                  Code = "warning"
                  Message = wm }

        let mutable tenv = typeEnv0

        // a module file [D:modules-v1] is checkable in isolation: direct
        // `weir check` enforces the same decl-only + weak-purity rules an
        // import would (decision 10)
        let isModule =
            match logicalLines with
            | first :: _ ->
                let t = first.Text.TrimStart()
                t = "module" || t.StartsWith "module "
            | [] -> false

        let rec cmdHeads (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TECmd(prog, _, _) when not (Extern.exists prog) -> [ prog, te.Span ]
             | _ -> [])
            @ (Check.childExprs te |> List.collect cmdHeads)

        for ll in logicalLines do
            // ONE spelling for the head warning, shared by the Ok walk
            // (typed) and the Error walk (parse-level) below
            let warnMissingHead (prog: string) (startCol: int) =
                let wl, wc = translate ll startCol

                // a near-miss BINDING bridges the check/run
                // verdict split: the runner reads this head in
                // expression mode and errors "unbound 'xx' —
                // did you mean 'xr'?"; check's command reading
                // must surface the same candidate
                let hint = didYouMean prog (Map.keys tenv.Values |> Seq.filter Types.isUserName)

                diags.Add
                    { File = path
                      Line = wl
                      Col = wc
                      // the full head word squiggles, not one
                      // char [PLAN-diagnostics-arc A4]
                      EndLine = Some wl
                      EndCol = Some(wc + prog.Length)
                      Severity = "warning"
                      Code = "cmd-not-found"
                      Message =
                        $"command not found on PATH: {prog}{hint} — weir resolves commands at check time; the script runs once it is installed" }

            match checkStatement true assumeResolver analyzeImport tenv ll with
            | Ok chk ->
                chk.Warnings |> List.iter warn

                (match chk.Kind with
                 | KType _
                 | KModule _
                 | KImport _ -> ()
                 | KLet(_, _, te)
                 | KLetPat(_, _, te)
                 | KCmd te
                 | KExpr te ->
                     for prog, span in cmdHeads te do
                         warnMissingHead prog span.Start.Col)

                (if isModule then
                     let violation =
                         match chk.Kind with
                         | KCmd _
                         | KExpr _ -> Some "a module declares only — 'type' and 'let', no commands or bare expressions"
                         | KLet(_, _, te)
                         | KLetPat(_, _, te) when runsCommandT te ->
                             Some "a module 'let' cannot run a command at import — wrap it in a function (let f () = …)"
                         | _ -> None

                     match violation with
                     | Some msg ->
                         let wl, wc = translate ll 1

                         diags.Add
                             { File = path
                               Line = wl
                               Col = wc
                               EndLine = None
                               EndCol = None
                               Severity = "error"
                               Code = "module-rule"
                               Message = msg }
                     | None -> ())

                stmts.Add(ll, chk)
                tenv <- chk.Env
            | Error d ->
                d.Warnings |> List.iter warn

                // [PLAN-diagnostics-arc B5+B6]: an ERRORED statement
                // still (a) surfaces its command-head warnings — no
                // typed tree exists, so the walk is parse-level — and
                // (b) binds its let NAMES to hole schemes so downstream
                // uses don't cascade as "unbound". SUPPRESSION WITH
                // DEFERRAL, deliberately: a hole unifies with anything,
                // so a later genuine mismatch against the real type may
                // surface only after this error is fixed — one real
                // error beats N echoes. (The poison-type alternative —
                // suppressing downstream errors that MENTION the name —
                // needs a new type node through unify; declined as
                // disproportionate.)
                (match Parser.parseLine (assumeResolver tenv) ll.Text with
                 | Ok stmt ->
                     let rec eheads (e: Expr) =
                         (match e.Kind with
                          | ECmd(prog, _, _) when not (Extern.exists prog) -> [ prog, e.Span ]
                          | _ -> [])
                         @ (exprChildren e |> List.collect eheads)

                     let exprs =
                         match stmt with
                         | SLet(_, v)
                         | SLetPat(_, v)
                         | SExpr v
                         | SCmd v -> [ v ]
                         | SType _
                         | SModule _
                         | SImport _ -> []

                     for prog, span in exprs |> List.collect eheads do
                         warnMissingHead prog span.Start.Col

                     let rec patVars (p: Pattern) =
                         match p.PKind with
                         | PVar n -> [ n ]
                         | PTuple ps -> ps |> List.collect patVars
                         | PCase(_, Some inner) -> patVars inner
                         | _ -> []

                     let holeScheme =
                         { Forall = Set.singleton "__hole"
                           Cs = Map.empty
                           Ty = TVar "__hole"
                           RowOrigins = Map.empty }

                     let bound =
                         match stmt with
                         | SLet(name, _) -> [ name ]
                         | SLetPat(pat, _) -> patVars pat
                         | _ -> []

                     for n in bound do
                         tenv <-
                             { tenv with
                                 Values = Map.add n holeScheme tenv.Values }
                 | Error _ -> ())

                // multi-file [D:modules-v1]: a module error carries File +
                // PhysLine/Col into that OTHER file; its Note is the "imported
                // here" pointer at the import line in THIS file
                diags.Add
                    { File = d.File |> Option.defaultValue path
                      Line = d.PhysLine
                      Col = d.PhysCol
                      EndLine = d.PhysEnd |> Option.map fst
                      EndCol = d.PhysEnd |> Option.map snd
                      Severity = "error"
                      Code = codeOf d.Parse d.Message
                      Message = d.Message }

                match d.Note with
                | Some(nl, nc, nmsg) ->
                    diags.Add
                        { File = path
                          Line = nl
                          Col = nc
                          EndLine = None
                          EndCol = None
                          Severity = "note"
                          Code = "imported-here"
                          Message = nmsg }
                | None -> ()

        List.ofSeq assemblyDiags
        @ List.ofSeq diags
        @ docMisalignments path rawLines
        @ schemaDiagnostics path (List.ofSeq stmts),
        List.ofSeq stmts,
        typeEnv0,
        logicalLines

let checkOnly (json: bool) (path: string) : int =
    if not (IO.File.Exists path) then
        Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let rawLines = IO.File.ReadAllLines path |> Array.toList
        let diags, _, _, _ = analyzeLines path rawLines

        if json then
            Console.WriteLine(
                jsonBuild (fun w ->
                    w.WriteStartArray()
                    diags |> List.iter (writeDiag w)
                    w.WriteEndArray())
            )
        else
            let c = Color.onStdout.Value

            for d in diags do
                let sev =
                    if d.Severity = "warning" then
                        Color.yellow c $"warning [{d.Code}]"
                    elif d.Severity = "note" then
                        Color.bold c $"note [{d.Code}]"
                    else
                        Color.red c $"error [{d.Code}]"

                Console.WriteLine(Color.bold c $"{d.File}:{d.Line}:{d.Col}" + $": {sev}: {d.Message}")

        if diags |> List.exists (fun d -> d.Severity = "error") then
            1
        else
            0

// [D:doc-help] the `///` first-line help for the fields of the type decl
// on `ll`, keyed by field name. A field's DocAttach sits at the field's own
// physical line, so scope by the decl's physical lines (its Segments); a
// field name is unique within a decl. An EMPTY first line -> no help entry
// (silence beats a mystery), same as no doc.
let private fieldDocsFor (rawLines: string list) (ll: LogicalLine) : Map<string, string> =
    let physLines = ll.Segments |> List.map (fun (_, p, _) -> p) |> Set.ofList
    let arr = List.toArray rawLines

    docAttachments rawLines
    |> List.choose (fun d ->
        match d.Doc with
        | first :: _ when
            first <> ""
            && Set.contains d.Line physLines
            && d.Line - 1 < arr.Length
            && d.Col - 1 + d.Len <= arr[d.Line - 1].Length
            ->
            Some(arr[d.Line - 1].Substring(d.Col - 1, d.Len), first)
        | _ -> None)
    |> Map.ofList

let run (path: string) (scriptArgs: string list) : int =
    if not (IO.File.Exists path) then
        Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let rawLines = IO.File.ReadAllLines path |> Array.toList

        let mode, body, bodyOffset = scriptBody rawLines

        // COLUMN-0 only: a directive is a statement-position thing.
        // Indented `#` lines are continuations — inside a yaml district
        // they are content (`#!/bin/sh` in a block scalar, `#` comments)
        // [D:block-scalars]; anywhere else they fail the parse as the
        // junk they are
        let directiveError =
            body
            |> List.mapi (fun i l -> i, l.TrimEnd())
            |> List.tryFind (fun (_, l) -> l.StartsWith "#")

        match directiveError with
        | Some(i, l) ->
            Console.Error.WriteLine(
                located path (bodyOffset + i + 1) $"unknown or misplaced directive: {l} (#loose belongs at file head)"
            )

            1
        | None ->
            // the script's own absolute path [D:script-path]: resolved
            // against the STARTUP cwd, before any cd; symlinks stay
            // unresolved (the bash-$0 behavior)
            let absScriptPath = IO.Path.GetFullPath path

            let typeEnv0, valueEnv0 = baseEnvs mode scriptArgs absScriptPath
            Extern.refresh ()

            let rawByLine = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> Map.ofList

            // comment-only lines are TRANSPARENT [D:comment-transparency]
            let assembled = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> assemble // raw lines: assemble classifies/strips internally

            match assembled with
            | Error msg ->
                Console.Error.WriteLine $"{path}: {msg}"
                1
            | Ok logicalLines ->

                // a module file is not runnable [D:modules-v1] — the marker
                // is what makes this message possible (an empty SCRIPT is a
                // different, "nothing to run", situation)
                let moduleMarker =
                    logicalLines
                    |> List.tryFind (fun ll ->
                        let t = ll.Text.TrimStart()
                        t = "module" || t.StartsWith "module ")

                // the entry's import loader, bound to its directory; one cache
                // per run dedups a diamond, one chain catches a cycle
                let entryImport: ImportLoader =
                    let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
                    fun p _ alias -> loadModuleCached cache [ absScriptPath ] absScriptPath p alias

                let checkedProgram =
                    logicalLines
                    |> List.fold
                        (fun state ll ->
                            match state with
                            | Error e -> Error e
                            | Ok(tenv, acc) ->
                                match checkStatement true resolver entryImport tenv ll with
                                | Error d ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in d.Warnings do
                                        Console.Error.WriteLine(
                                            $"{path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    let sameFileMsg =
                                        if d.Parse then
                                            if d.HasCol then
                                                // the ORIGINAL source line + caret — never
                                                // the assembled text
                                                let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                                let caret = Color.red c (String(' ', max 0 (d.PhysCol - 1)) + "^")

                                                Color.bold c $"{path}:{d.PhysLine}:{d.PhysCol}"
                                                + ": "
                                                + Color.red c "parse error"
                                                + $":\n{src}\n{caret}\n{d.Message}"
                                            else
                                                located path d.PhysLine d.Message
                                        else
                                            // same source-line treatment as parse
                                            // errors, with the span underlined
                                            let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                            let width =
                                                match d.PhysEnd with
                                                | Some(el, ec) when el = d.PhysLine -> max 1 (ec - d.PhysCol)
                                                | _ -> 1

                                            let underline =
                                                Color.red c (String(' ', max 0 (d.PhysCol - 1)) + String('^', width))

                                            Color.bold c $"{path}:{d.PhysLine}:{d.PhysCol}"
                                            + ": "
                                            + Color.red c "type error"
                                            + $":\n{src}\n{underline}\n{d.Message}"

                                    // multi-file [D:modules-v1]: a module error
                                    // renders at its OWN file + an "imported here"
                                    // note at the import line
                                    let locatedMsg =
                                        match d.File with
                                        | Some mf ->
                                            let note =
                                                match d.Note with
                                                | Some(nl, nc, nmsg) ->
                                                    "\n" + Color.bold c $"{path}:{nl}:{nc}" + ": note: " + nmsg
                                                | None -> ""

                                            Color.bold c $"{mf}:{d.PhysLine}:{d.PhysCol}"
                                            + ": "
                                            + Color.red c "error"
                                            + $": {d.Message}"
                                            + note
                                        | None -> sameFileMsg

                                    Error locatedMsg
                                | Ok chk ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in chk.Warnings do
                                        Console.Error.WriteLine(
                                            $"{path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    // schema contracts gate the RUN too — check
                                    // before effects [D:yaml-schemas]
                                    match schemaDiagnostics path [ (ll, chk) ] with
                                    | d :: _ ->
                                        let src = rawByLine |> Map.tryFind d.Line |> Option.defaultValue ""

                                        let width =
                                            match d.EndLine, d.EndCol with
                                            | Some el, Some ec when el = d.Line -> max 1 (ec - d.Col)
                                            | _ -> 1

                                        let underline =
                                            Color.red c (String(' ', max 0 (d.Col - 1)) + String('^', width))

                                        Error(
                                            Color.bold c $"{path}:{d.Line}:{d.Col}"
                                            + ": "
                                            + Color.red c "schema error"
                                            + $":\n{src}\n{underline}\n{d.Message}"
                                        )
                                    | [] ->

                                        let stmt =
                                            match chk.Kind with
                                            | KType decl -> CType decl
                                            | KLet(name, _, te) -> CLet(name, te)
                                            | KLetPat(pat, _, te) -> CLetPat(pat, te)
                                            | KCmd te -> CCmd te
                                            | KExpr te -> CExpr te
                                            | KImport lm -> CImport lm
                                            // unreachable: the marker is caught before the fold
                                            | KModule _ -> CNoop

                                        // enrich a record's Docs from the `///`
                                        // field docs, so --help reads them
                                        // [D:doc-help]; the Args.load arm (checked
                                        // later, the type comes first) captures it
                                        let env' =
                                            match chk.Kind with
                                            | KType decl ->
                                                let docs = fieldDocsFor rawLines ll

                                                if Map.isEmpty docs then
                                                    chk.Env
                                                else
                                                    { chk.Env with
                                                        Types =
                                                            chk.Env.Types
                                                            |> Map.change
                                                                decl.Name
                                                                (Option.map (function
                                                                    | Record rd -> Record { rd with Docs = docs }
                                                                    | u -> u)) }
                                            | _ -> chk.Env

                                        Ok(env', (ll.Head, stmt) :: acc))
                        (Ok(typeEnv0, []))

                match
                    (match moduleMarker with
                     | Some ll ->
                         Error(
                             located
                                 path
                                 ll.Head
                                 "a module declares; it does not run. To run a script from a script, invoke it as a command"
                         )
                     | None -> checkedProgram)
                with
                | Error msg ->
                    Console.Error.WriteLine msg
                    1
                | Ok(_, revStmts) ->
                    let stmts = List.rev revStmts

                    let rec exec (venv: Eval.Env) (rest: (int * CheckedStmt) list) : int =
                        match rest with
                        | [] -> 0
                        | (lineNo, stmt) :: tail ->
                            match stmt with
                            | CNoop -> exec venv tail
                            | CImport lm ->
                                try
                                    // replay the module (its Body, including any
                                    // nested imports) and expose ITS members as
                                    // `alias.member` [D:modules-v1]
                                    // process facts (incl. entryPath) ride from
                                    // the entry; the module keeps its OWN scriptPath
                                    let procFacts =
                                        [ "Self.pid"; "Self.args"; "Self.stdin"; "Self.entryPath" ]
                                        |> List.choose (fun k -> Map.tryFind k venv |> Option.map (fun v -> k, v))

                                    let moduleVenv = replayModule procFacts lm

                                    let venv' =
                                        lm.Members
                                        |> List.fold
                                            (fun acc (n, _) ->
                                                match Map.tryFind n moduleVenv with
                                                | Some v -> Map.add $"{lm.Alias}.{n}" v acc
                                                | None -> acc)
                                            venv

                                    exec venv' tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        located path lineNo (Color.red Color.onStderr.Value "error" + $": {ex.Message}")
                                    )

                                    1
                            | CType decl ->
                                let venv' =
                                    match decl.Body with
                                    | DUnion cases ->
                                        Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) venv
                                    | DRecord _ -> venv

                                exec venv' tail
                            | CLetPat(pat, te) ->
                                try
                                    let bindings = Eval.bindPattern pat (Eval.eval venv te)
                                    exec (bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv) tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        located path lineNo (Color.red Color.onStderr.Value "error" + $": {ex.Message}")
                                    )

                                    1
                            | CLet(name, te) ->
                                try
                                    exec (Map.add name (Eval.eval venv te) venv) tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        located path lineNo (Color.red Color.onStderr.Value "error" + $": {ex.Message}")
                                    )

                                    1
                            | CCmd te ->
                                try
                                    printResult (Eval.eval venv te)
                                    exec venv tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        located path lineNo (Color.red Color.onStderr.Value "error" + $": {ex.Message}")
                                    )

                                    1
                            | CExpr te ->
                                try
                                    Eval.eval venv te |> ignore
                                    exec venv tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        located path lineNo (Color.red Color.onStderr.Value "error" + $": {ex.Message}")
                                    )

                                    1

                    exec valueEnv0 stmts
