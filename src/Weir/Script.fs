module Weir.Script

open System
open Weir.Ast
open Weir.Types

// The quote-aware scanner — the ONE string-state primitive
// [D:one-scanner]. Folds f over the characters that sit OUTSIDE string
// literals: double quotes honor backslash escapes, single quotes close
// at the next single quote.
let private foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a =
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

    st

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

    if cut >= 0 then line.Substring(0, cut) else line

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
// — previously each re-derived these by StartsWith/Trim and agreed by
// discipline; now they agree by construction)

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
    | Bare
    | Env of name: string

type PieceClass =
    { Kind: PieceKind
      Marker: MarkerKind
      OpensCompound: bool
      IsBangSigil: bool
      ClosesBrace: bool
      StartsField: bool
      StartsTypeField: bool
      BraceDelta: int }

let private isIdentToken (t: string) =
    t.Length > 0
    && (System.Char.IsLetter t[0] || t[0] = '_')
    && t |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

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
        then
            PieceKind.ElseHead
        elif piece.StartsWith "let " then
            PieceKind.LetHead
        else
            PieceKind.Plain
      Marker =
        if piece = "!" || piece.EndsWith " !" then
            MarkerKind.Bare
        elif lastToken.StartsWith "!" && isIdentToken (lastToken.Substring 1) then
            MarkerKind.Env(lastToken.Substring 1)
        else
            MarkerKind.NoMarker
      OpensCompound = piece.StartsWith "if " || piece.StartsWith "match "
      IsBangSigil =
        piece.StartsWith "!("
        || (piece.StartsWith "!"
            && (match piece.IndexOf '(' with
                | i when i > 1 -> isIdentToken (piece.Substring(1, i - 1))
                | _ -> false))
      ClosesBrace = piece.StartsWith "}"
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

/// The marker's district wrap: opener text and how many trailing
/// characters of the armed line the first district line strips.
let private markerOpener (m: MarkerKind) : (string * int) option =
    match m with
    | MarkerKind.NoMarker -> None
    | MarkerKind.Bare -> Some("!(", 1)
    | MarkerKind.Env name -> Some("!" + name + "(", 1 + name.Length)

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
      // still-open brackets (kind, opening line, sibling-entry column)
      // [D:multiline-brackets] [D:field-alignment]
      Brackets: (char * int * int option) list }

// The join algebra: every way a continuation line attaches to the
// pending statement, its inserted text in ONE place. joinedStart
// derives from the same strings, so span arithmetic cannot drift from
// the insertion.
type private Join =
    | JIn // let-close: text + " in " + piece
    | JSibling // sequencing (and record field separators): " ; "
    | JSpace // plain continuation: " "
    | JDistrictOpen of strip: int * opener: string // strip the armed marker, wrap
    | JDistrictSibling of opener: string // text + " ; " + opener + piece + ")"
    | JDistrictPipe // reopen the wrap: stem + " " + piece + ")"

let private applyJoin (j: Join) (ll: LogicalLine) (piece: string) (lineNo: int) (indent: int) : LogicalLine =
    let text, joinedStart =
        match j with
        | JIn ->
            let sep = " in "
            ll.Text + sep + piece, ll.Text.Length + sep.Length
        | JSibling ->
            let sep = " ; "
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

    { ll with
        Text = text
        Segments = (joinedStart, lineNo, indent) :: ll.Segments }

let assemble (numbered: (int * string) list) : Result<LogicalLine list, string> =
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
                | Ok(current, acc, blankSinceHead) ->
                    let inOpenBrace =
                        match current with
                        | Some p -> not p.Brackets.IsEmpty
                        | None -> false

                    if raw.Trim() = "" then
                        match current with
                        // transparency is total while a statement pends
                        // [D:body-blanks] — the comment-line class, second
                        // member; the col-0 law (plus EOF) is the sole
                        // statement boundary, so every error the blank
                        // boundary produced still fires at close
                        | Some p -> Ok(Some p, acc, blankSinceHead)
                        | None -> Ok(None, acc, true)
                    elif raw[0] = ' ' || raw[0] = '\t' || raw[0] = '|' || inOpenBrace then
                        let indent = raw |> Seq.takeWhile (fun c -> c = ' ' || c = '\t') |> Seq.length

                        if raw.Substring(0, indent).Contains '\t' then
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
                                let piece = raw.Substring indent
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
                                            if join = JSibling || attrField || (startsEntry && entryCol.IsNone) then
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
                                        | Some { Active = None; MarkerLine = mLine } ->
                                            Error
                                                $"line {mLine}: line-end '!' needs an indented block of command lines below it"
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
                                            // follows (like a compound closing — found via the
                                            // standalone-marker shape, latent for bare ! too);
                                            // then this line reprocesses under the normal rules
                                            go
                                                { p with
                                                    District = None
                                                    LastIndent = dst.MarkerIndent }
                                        | None ->
                                            if cls.Kind = PieceKind.PipeHead || cls.Kind = PieceKind.ElseHead then
                                                // arms, pipeline stages, and else extend the
                                                // current piece: no sibling `;` — but siblings
                                                // must ALIGN, and a shallower arm offside-closes
                                                // deeper compounds [D:pipe-alignment]
                                                match p.Lets with
                                                | (k, letLine) :: _ when indent <= k -> noBody letLine
                                                | _ ->
                                                    // deeper groups die at this line's column
                                                    let groups = p.PipeGroups |> List.skipWhile (fun g -> g > indent)

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

                                                        Ok(
                                                            Some
                                                                { p with
                                                                    LL = applyJoin JSpace ll piece lineNo indent
                                                                    LastIndent = indent
                                                                    ParenDepth = depth
                                                                    PipeGroups = groups
                                                                    LastWasPipe = true
                                                                    Compounds =
                                                                        compounds
                                                                        |> List.filter (fun (_, _, d) -> d <= depth) },
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

                                                    let lets, join =
                                                        match p.Lets with
                                                        | (k, _) :: rest when indent = k -> rest, JIn
                                                        // same-indent sibling = block sequencing
                                                        | _ when indent = siblingLevel -> p.Lets, JSibling
                                                        | _ -> p.Lets, JSpace

                                                    let lets =
                                                        if cls.Kind = PieceKind.LetHead then
                                                            (indent, lineNo) :: lets
                                                        else
                                                            lets

                                                    let district =
                                                        markerOpener cls.Marker
                                                        |> Option.map (fun (opener, strip) ->
                                                            { MarkerIndent = indent
                                                              MarkerLine = lineNo
                                                              Opener = opener
                                                              Strip = strip
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

                                                    bracketFold lineNo indent [] piece
                                                    |> Result.map (fun brackets ->
                                                        Some
                                                            { p with
                                                                LL = joined
                                                                Lets = lets
                                                                LastIndent = indent
                                                                District = district
                                                                Compounds = compounds
                                                                Brackets = brackets
                                                                ParenDepth = depth
                                                                PipeGroups =
                                                                    p.PipeGroups
                                                                    |> List.skipWhile (fun g -> g > indent)
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
                                        |> Option.map (fun (opener, strip) ->
                                            { MarkerIndent = 0
                                              MarkerLine = lineNo
                                              Opener = opener
                                              Strip = strip
                                              Active = None })
                                      Compounds = []
                                      ParenDepth = parenDelta (raw.TrimEnd())
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
module Color =
    let private enabled (redirected: bool) =
        not redirected
        && isNull (Environment.GetEnvironmentVariable "NO_COLOR")
        && Environment.GetEnvironmentVariable "TERM" <> "dumb"

    let onStdout = lazy enabled Console.IsOutputRedirected
    let onStderr = lazy enabled Console.IsErrorRedirected

    let private wrap (on: bool) (code: string) (s: string) =
        if on then $"\x1b[{code}m{s}\x1b[0m" else s

    let red on s = wrap on "31" s
    let yellow on s = wrap on "33" s
    let bold on s = wrap on "1" s

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

        let commentCut = (stripComment line).Length

        for i in commentCut .. line.Length - 1 do
            codes[i] <- Some "90" // comments override to EOL

        let isIdentStart c = Char.IsLetter c || c = '_'
        let isIdentCont c = Char.IsLetterOrDigit c || c = '_'
        let free i = i < commentCut && not mask[i]

        // token pass over the code region
        let mutable i = 0
        let mutable headSeen = false

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
                        if isKnown word then Some "1" // known: bold
                        elif Extern.exists word then Some "1;34" // PATH: bold blue
                        else Some "31" // unresolved: red
                    elif Char.IsUpper word[0] then
                        Some "33" // the casing law: types/ctors/modules
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
                codes[i] <- Some "1" // operators: bold
                i <- i + 1
            else
                i <- i + 1

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

type private CheckedStmt =
    | CLet of name: string * te: Check.TypedExpr
    | CLetPat of binder: Weir.Ast.Pattern * te: Check.TypedExpr
    | CExpr of te: Check.TypedExpr
    | CCmd of te: Check.TypedExpr
    | CType of decl: Decl
    | CNoop

let private baseEnvs (mode: Mode) (scriptArgs: string list) =
    let typeEnv =
        match mode with
        | Strict -> Builtins.typeEnvStrict
        | Loose -> Builtins.typeEnv

    let typeEnv, valueEnv = Prelude.extend typeEnv Builtins.valueEnv

    let typeEnv =
        { typeEnv with
            Values =
                typeEnv.Values
                |> Map.add "args" (generalize (TSeq TStr))
                |> Map.add "stdin" (generalize (TSeq TStr)) }

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
        |> Map.add "args" (Eval.VSeq(scriptArgs |> List.map Eval.VStr :> seq<Eval.Value>))
        |> Map.add "stdin" stdinStream

    typeEnv, valueEnv

let private resolver (typeEnv: TypeEnv) : Parser.Resolver =
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

let checkStatement
    (gateExprs: bool)
    (mkR: TypeEnv -> Parser.Resolver)
    (tenv: TypeEnv)
    (ll: LogicalLine)
    : Result<CheckedStatement, StmtDiag> =
    let r = mkR tenv

    let typed (tag: StmtTag) (terr: Check.TypeError) =
        let physLine, physCol = translate ll terr.Span.Start.Col

        { PhysLine = physLine
          PhysCol = physCol
          PhysEnd = Some(translate ll terr.Span.End.Col)
          Tag = Some tag
          HasCol = true
          Span = Some terr.Span
          Parse = false
          Message = terr.Message
          Warnings = [] }

    let warningsOf te =
        [ for w in Check.warnings te do
              let physLine, physCol = translate ll w.Span.Start.Col
              physLine, physCol, w.Message ]

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
                    | SType _ -> None

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
        | Ok(te, cs) ->
            let scheme = generalizeWith cs te.Ty

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
            // [D:exit-reifiers]; record-valued (| complete) statements
            // keep their standing echo behavior
            match te.Ty with
            | TBool ->
                Error(
                    typed
                        StmtTag.Cmd
                        { Span = te.Span
                          Message =
                            "this statement computes a bool and discards it — bind it "
                            + "(let ok = ... | succeeds) or use it in a condition" }
                )
            | _ ->
                Ok
                    { Kind = KCmd te
                      Env = tenv
                      Warnings = warningsOf te }
    | Ok(SExpr e) ->
        match Check.typecheck tenv e with
        | Error terr -> Error(typed StmtTag.Expr terr)
        | Ok te ->
            match (if gateExprs then discardError te.Ty else None) with
            | Some msg ->
                Error
                    { typed StmtTag.Expr { Span = e.Span; Message = msg } with
                        Warnings = warningsOf te }
            | None ->
                Ok
                    { Kind = KExpr te
                      Env = tenv
                      Warnings = warningsOf te }

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

// full analysis for tooling (the LSP re-frames this): diagnostics AND
// the successfully-checked statements with their logical lines — plus
// the initial env, so consumers can pick the in-scope env per position
let analyzeLines
    (path: string)
    (rawLines: string list)
    : Diagnostic list * (LogicalLine * CheckedStatement) list * TypeEnv * LogicalLine list =
    let _, body, bodyOffset = scriptBody rawLines
    let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)

    let typeEnv0, _ = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    let typeEnv0 =
        { typeEnv0 with
            Values =
                typeEnv0.Values
                |> Map.add "args" (generalize (TSeq TStr))
                |> Map.add "stdin" (generalize (TSeq TStr)) }

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

    let logicalLines =
        numbered
        |> List.filter (fun (_, raw) -> classifyLine raw <> LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, stripComment raw)
        |> assembleRecovering 10

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

        let rec cmdHeads (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TECmd(prog, _, _) when not (Extern.exists prog) -> [ prog, te.Span ]
             | _ -> [])
            @ (Check.childExprs te |> List.collect cmdHeads)

        for ll in logicalLines do
            match checkStatement true assumeResolver tenv ll with
            | Ok chk ->
                chk.Warnings |> List.iter warn

                (match chk.Kind with
                 | KType _ -> ()
                 | KLet(_, _, te)
                 | KLetPat(_, _, te)
                 | KCmd te
                 | KExpr te ->
                     for prog, span in cmdHeads te do
                         let wl, wc = translate ll span.Start.Col

                         // a near-miss BINDING bridges the check/run
                         // verdict split: the runner reads this head in
                         // expression mode and errors "unbound 'xx' —
                         // did you mean 'xr'?"; check's command reading
                         // must surface the same candidate
                         let hint = didYouMean prog (Map.keys tenv.Values)

                         diags.Add
                             { File = path
                               Line = wl
                               Col = wc
                               EndLine = None
                               EndCol = None
                               Severity = "warning"
                               Code = "cmd-not-found"
                               Message =
                                 $"command not found on PATH: {prog}{hint} — weir resolves commands at check time; the script runs once it is installed" })

                stmts.Add(ll, chk)
                tenv <- chk.Env
            | Error d ->
                d.Warnings |> List.iter warn

                diags.Add
                    { File = path
                      Line = d.PhysLine
                      Col = d.PhysCol
                      EndLine = d.PhysEnd |> Option.map fst
                      EndCol = d.PhysEnd |> Option.map snd
                      Severity = "error"
                      Code = codeOf d.Parse d.Message
                      Message = d.Message }

        List.ofSeq assemblyDiags @ List.ofSeq diags, List.ofSeq stmts, typeEnv0, logicalLines

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
                    else
                        Color.red c $"error [{d.Code}]"

                Console.WriteLine(Color.bold c $"{d.File}:{d.Line}:{d.Col}" + $": {sev}: {d.Message}")

        if diags |> List.exists (fun d -> d.Severity = "error") then
            1
        else
            0

let run (path: string) (scriptArgs: string list) : int =
    if not (IO.File.Exists path) then
        Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let rawLines = IO.File.ReadAllLines path |> Array.toList

        let mode, body, bodyOffset = scriptBody rawLines

        let directiveError =
            body
            |> List.mapi (fun i l -> i, l.Trim())
            |> List.tryFind (fun (_, l) -> l.StartsWith "#")

        match directiveError with
        | Some(i, l) ->
            Console.Error.WriteLine(
                located path (bodyOffset + i + 1) $"unknown or misplaced directive: {l} (#loose belongs at file head)"
            )

            1
        | None ->
            let typeEnv0, valueEnv0 = baseEnvs mode scriptArgs
            Extern.refresh ()

            let rawByLine = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> Map.ofList

            // comment-only lines are TRANSPARENT [D:comment-transparency]
            let assembled =
                body
                |> List.mapi (fun i l -> bodyOffset + i + 1, l)
                |> List.filter (fun (_, raw) -> classifyLine raw <> LineKind.CommentOnly)
                |> List.map (fun (n, raw) -> n, stripComment raw)
                |> assemble

            match assembled with
            | Error msg ->
                Console.Error.WriteLine $"{path}: {msg}"
                1
            | Ok logicalLines ->

                let checkedProgram =
                    logicalLines
                    |> List.fold
                        (fun state ll ->
                            match state with
                            | Error e -> Error e
                            | Ok(tenv, acc) ->
                                match checkStatement true resolver tenv ll with
                                | Error d ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in d.Warnings do
                                        Console.Error.WriteLine(
                                            $"{path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    let locatedMsg =
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

                                    Error locatedMsg
                                | Ok chk ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in chk.Warnings do
                                        Console.Error.WriteLine(
                                            $"{path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    let stmt =
                                        match chk.Kind with
                                        | KType decl -> CType decl
                                        | KLet(name, _, te) -> CLet(name, te)
                                        | KLetPat(pat, _, te) -> CLetPat(pat, te)
                                        | KCmd te -> CCmd te
                                        | KExpr te -> CExpr te

                                    Ok(chk.Env, (ll.Head, stmt) :: acc))
                        (Ok(typeEnv0, []))

                match checkedProgram with
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
