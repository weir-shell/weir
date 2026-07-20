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
            let r = resolver typeEnv0

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

                let typedErr (ll: LogicalLine) (terr: Check.TypeError) =
                    let physLine, physCol = translate ll terr.Span.Start.Col
                    $"{path}:{physLine}:{physCol}: type error: {terr.Message}"

                // Warnings were silently dropped by the runner until the
                // bool-branching session (found via a warning-less
                // non-exhaustive match in -e); they go to stderr, located.
                let printWarnings (ll: LogicalLine) (te: Check.TypedExpr) =
                    for w in Check.warnings typeEnv0 te do
                        let physLine, physCol = translate ll w.Span.Start.Col
                        Console.Error.WriteLine $"{path}:{physLine}:{physCol}: warning: {w.Message}"

                let checkedProgram =
                    logicalLines
                    |> List.fold
                        (fun state ll ->
                            match state with
                            | Error e -> Error e
                            | Ok(tenv, acc) ->
                                match Parser.parseLineFull r ll.Text with
                                | Error f ->
                                    let locatedMsg =
                                        match f.Col with
                                        | Some col ->
                                            let physLine, physCol = translate ll col
                                            $"{path}:{physLine}:{physCol}: parse error:\n{f.Message}"
                                        | None -> located path ll.Head f.Message

                                    Error locatedMsg
                                | Ok(SType decl) ->
                                    match Check.checkDecl tenv decl with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok tenv' -> Ok(tenv', (ll.Head, CType decl) :: acc)
                                | Ok(SLet(name, e)) ->
                                    match Check.typecheck tenv e with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok te ->
                                        printWarnings ll te

                                        let tenv' =
                                            { tenv with
                                                Values = Map.add name (generalize te.Ty) tenv.Values }

                                        Ok(tenv', (ll.Head, CLet(name, te)) :: acc)
                                | Ok(SCmd e) ->
                                    match Check.typecheck tenv e with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok te ->
                                        printWarnings ll te
                                        Ok(tenv, (ll.Head, CCmd te) :: acc)
                                | Ok(SExpr e) ->
                                    match Check.typecheck tenv e with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok te ->
                                        printWarnings ll te

                                        match discardError te.Ty with
                                        | Some msg -> Error(typedErr ll { Span = e.Span; Message = msg })
                                        | None -> Ok(tenv, (ll.Head, CExpr te) :: acc))
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
