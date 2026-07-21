module Weir.Script

open System
open Weir.Ast
open Weir.Types

// The quote-aware scanner — the ONE string-state primitive (2026-07-20
// formalization). Folds f over the characters that sit OUTSIDE string
// literals: double quotes honor backslash escapes, single quotes close
// at the next single quote. Every line-shape rule that must ignore
// string interiors (comment cut, brace depth) consumes this scanner;
// a second inline quote state machine is a review flag.
let private foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a =
    let mutable st = init
    let mutable i = 0
    let mutable inDouble = false
    let mutable inSingle = false

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
/// (2026-07-20 — the marker distributes `!name(...)` over the block).
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
        elif piece = "else" || piece.StartsWith "else " then
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
        | '\'' :: rest ->
            if c = '\'' then
                stack <- rest
        | _ ->
            if c = '"' then
                stack <- (if i > 0 && text[i - 1] = '$' then '$' else '"') :: stack
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
    |> List.map (fun c -> if c = '$' then '"' else c)
    |> List.toArray
    |> String

// Columns (0-based) of still-open record braces after folding a line
// into the running stack — fmt aligns record fields at TOP+2
// (quote-aware via the scanner family; lives here per the rule).
let braceStack (prev: int list) (line: string) : int list =
    let mutable stack = prev
    let mutable inDouble = false
    let mutable inSingle = false
    let mutable i = 0

    while i < line.Length do
        let c = line[i]

        if inDouble then
            if c = '\\' && i + 1 < line.Length then
                i <- i + 1
            elif c = '"' then
                inDouble <- false
        elif inSingle then
            if c = '\'' then
                inSingle <- false
        elif c = '"' then
            inDouble <- true
        elif c = '\'' then
            inSingle <- true
        elif c = '{' then
            stack <- i :: stack
        elif c = '}' then
            match stack with
            | _ :: rest -> stack <- rest
            | [] -> ()

        i <- i + 1

    stack

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
      District: District option
      Compounds: (int * int) list
      BraceDepth: int
      BraceLine: int }

// The join algebra: every way a continuation line attaches to the
// pending statement, its inserted text in ONE place. joinedStart
// derives from the same strings, so span arithmetic cannot drift from
// the insertion (the hand-audited `+ 5` / `- 1 + 2` offsets retired).
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

    let noBodyBlank letLine =
        Error
            $"line {letLine}: this let needs a body — a blank line ends the statement; keep the block's lines contiguous"

    let braceOpen braceLine =
        Error $"line {braceLine}: this record literal's {{ is still open when the statement ends — close the brace"

    let close (current: Pend option) acc =
        match current with
        | Some p when p.BraceDepth > 0 -> braceOpen p.BraceLine
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
                        | Some p -> p.BraceDepth > 0
                        | None -> false

                    if raw.Trim() = "" then
                        match current with
                        | Some p when p.BraceDepth > 0 -> braceOpen p.BraceLine
                        | Some { District = Some { Active = None; MarkerLine = mLine } } ->
                            Error $"line {mLine}: line-end '!' needs an indented block of command lines below it"
                        | Some { Lets = (_, letLine) :: _ } -> noBodyBlank letLine
                        | _ -> close current acc |> Result.map (fun acc -> None, acc, true)
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
                                    if p.BraceDepth > 0 then
                                        // record continuation: a line break after a
                                        // field is a separator (with or without `;`)
                                        let prev = p.LL.Text.TrimEnd()

                                        // the separator goes BEFORE a field-start line
                                        // (`Ident =`), never before a value continuation —
                                        // a field's value may open on the next line (the
                                        // fixture-diversity sweep's first catch, 2026-07-20)
                                        let join =
                                            if
                                                cls.StartsField && not (prev.EndsWith "{") && not (prev.EndsWith ";")
                                            then
                                                JSibling
                                            else
                                                JSpace

                                        Ok(
                                            Some
                                                { p with
                                                    LL = applyJoin join p.LL piece lineNo indent
                                                    LastIndent = indent
                                                    BraceDepth = max 0 (p.BraceDepth + cls.BraceDelta) },
                                            acc,
                                            blankSinceHead
                                        )
                                    else
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
                                                // current piece: no sibling `;`, no offside close
                                                match p.Lets with
                                                | (k, letLine) :: _ when indent <= k -> noBody letLine
                                                | _ ->
                                                    Ok(
                                                        Some
                                                            { p with
                                                                LL = applyJoin JSpace p.LL piece lineNo indent
                                                                LastIndent = indent },
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
                                                        | (h, ts) :: rest when indent <= h ->
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

                                                    let compounds =
                                                        if cls.OpensCompound then
                                                            // the piece starts where the join put it:
                                                            // its segment is the newest entry
                                                            let (js, _, _) = List.head joined.Segments
                                                            (indent, js) :: compounds
                                                        else
                                                            compounds

                                                    Ok(
                                                        Some
                                                            { p with
                                                                LL = joined
                                                                Lets = lets
                                                                LastIndent = indent
                                                                District = district
                                                                Compounds = compounds
                                                                BraceDepth = max 0 cls.BraceDelta
                                                                BraceLine = lineNo },
                                                        acc,
                                                        blankSinceHead
                                                    )

                                go p
                    else
                        let cls = classifyPiece (raw.TrimEnd())

                        close current acc
                        |> Result.map (fun acc ->
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
                                  BraceDepth = max 0 cls.BraceDelta
                                  BraceLine = lineNo },
                            acc,
                            false))
            (Ok(None, [], false))

    match folded with
    | Error e -> Error e
    | Ok(current, acc, _) -> close current acc |> Result.map List.rev

let translate (ll: LogicalLine) (col: int) : int * int =
    let joinedIdx = col - 1

    let segStart, physLine, physIndent =
        ll.Segments |> List.filter (fun (js, _, _) -> js <= joinedIdx) |> List.last

    physLine, joinedIdx - segStart + physIndent + 1

type Mode =
    | Strict
    | Loose

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
// The checked-statement pipeline — ONE owner (2026-07-21, the oracle-
// mirror incident's fix): parse -> statement dispatch -> check ->
// statement-rule gate, physical spans computed INSIDE. The script
// runner, the REPL, -e, and the oracle's weirVerdict mirror all call
// this; a fifth consumer (weir check / the LSP) starts here. Consumers
// agreeing "by discipline, not construction" is the drift class this
// retires.

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
// application; ^cat forces the binary). The old once-built resolver
// left script names unknown and correct only by accident (found via
// weir check's assume-command rule, 2026-07-21).
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
        [ for w in Check.warnings tenv te do
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

            // multi-line statements are shown as their ASSEMBLED logical
            // line — say so, or the ` ; `/` in ` insertions read as
            // phantom source text
            let note =
                if List.length ll.Segments > 1 then
                    "\n(note: shown as the assembled logical line — ' ; ' and ' in ' are inserted by multi-line assembly)"
                else
                    ""

            Error
                { PhysLine = physLine
                  PhysCol = physCol
                  PhysEnd = None
                  Tag = None
                  HasCol = hasCol
                  Span = None
                  Parse = true
                  Message = f.Message + note
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
        match
            Check.checkBinderName e.Span name
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
// weir check [--json] — the agent-facing diagnostics core and LSP v1's
// payload generator (2026-07-21, LSP chain 2/3). Check-everything, no
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
// never string interpolation's (corrected 2026-07-21 on user review;
// the reflection SERIALIZER stays banned, the writer never was).
// UnsafeRelaxedJsonEscaping: "unsafe" means HTML-embedding only —
// these payloads are LSP/CLI, never HTML; the default encoder's
// \u0022-style quote escaping is valid but trips naive clients
// (micro's plugin rendered it mangled — user report, 2026-07-21)
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
    let afterShebang, shebangOffset =
        match rawLines with
        | first :: rest when first.StartsWith "#!" -> rest, 1
        | _ -> rawLines, 0

    let body, bodyOffset =
        match afterShebang with
        | first :: rest when first.Trim() = "#loose" -> rest, shebangOffset + 1
        | _ -> afterShebang, shebangOffset

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
    // input (the gateExprs pattern), the verdict difference pinned.
    // Receipt: editing bicep-deploy.weir without az/bicep installed
    // cascaded into parse errors (user report, 2026-07-21).

    // ASSEMBLY RECOVERY (2026-07-21): a single mid-edit line that breaks
    // assembly must not erase the whole document's knowledge (types,
    // bindings, completion env). Drop the offending line — the error
    // names it — and retry, keeping each drop as a diagnostic. The
    // RUNNER keeps hard assembly failure; this is tooling-only, the
    // same recovery philosophy as the statement level, one layer down.
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

                         diags.Add
                             { File = path
                               Line = wl
                               Col = wc
                               EndLine = None
                               EndCol = None
                               Severity = "warning"
                               Code = "cmd-not-found"
                               Message =
                                 $"command not found on PATH: {prog} — weir resolves commands at check time; the script runs once it is installed" })

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
            for d in diags do
                let sev = if d.Severity = "warning" then "warning" else "error"
                Console.WriteLine $"{d.File}:{d.Line}:{d.Col}: {sev} [{d.Code}]: {d.Message}"

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

        let afterShebang, shebangOffset =
            match rawLines with
            | first :: rest when first.StartsWith "#!" -> rest, 1
            | _ -> rawLines, 0

        let mode, body, bodyOffset =
            match afterShebang with
            | first :: rest when first.Trim() = "#loose" -> Loose, rest, shebangOffset + 1
            | _ -> Strict, afterShebang, shebangOffset

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

            // comment-only lines are TRANSPARENT (F#-faithful, fixed
            // 2026-07-20 — they used to strip to blank and end statements)
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
                                    for wl, wc, wm in d.Warnings do
                                        Console.Error.WriteLine $"{path}:{wl}:{wc}: warning: {wm}"

                                    let locatedMsg =
                                        if d.Parse then
                                            if d.HasCol then
                                                $"{path}:{d.PhysLine}:{d.PhysCol}: parse error:\n{d.Message}"
                                            else
                                                located path d.PhysLine d.Message
                                        else
                                            $"{path}:{d.PhysLine}:{d.PhysCol}: type error: {d.Message}"

                                    Error locatedMsg
                                | Ok chk ->
                                    for wl, wc, wm in chk.Warnings do
                                        Console.Error.WriteLine $"{path}:{wl}:{wc}: warning: {wm}"

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
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1
                            | CLet(name, te) ->
                                try
                                    exec (Map.add name (Eval.eval venv te) venv) tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1
                            | CCmd te ->
                                try
                                    printResult (Eval.eval venv te)
                                    exec venv tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1
                            | CExpr te ->
                                try
                                    Eval.eval venv te |> ignore
                                    exec venv tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1

                    exec valueEnv0 stmts
