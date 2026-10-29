module Weir.Fmt

open Weir.Ast

// fmt trims only what the parser skips as whitespace: any other
// character (U+3000, U+2028, a BOM) is a token to the parser, so
// trimming it would change whether the file checks
let private parserWs = [| ' '; '\t'; '\r'; '\n' |]

let collectBareUses (e: Expr) : (Span * string) list =
    // traversal is Ast.exprChildren's job; only EVar collects
    let acc = ResizeArray<Span * string>()

    let rec walk (e: Expr) =
        (match e.Kind with
         | EVar name when Map.containsKey name Builtins.bareAliasHomes -> acc.Add(e.Span, name)
         | _ -> ())

        exprChildren e |> List.iter walk

    walk e
    List.ofSeq acc

// ---------------------------------------------------------------------------
// weir fmt <script> [D:fmt-v1] plus intra-line respace [D:fmt-respace]
// (v2): collapse space runs, pad record braces, tidy `;` — under a
// parse-shape safety check: each respaced statement must sexpr-match
// its original (same permissive resolver on both sides) or that
// statement reverts to its pre-respace text. Comments keep their
// text; re-flowing stays parked. Pipe-headed lines keep the column-0
// shell style if they use it.

// `///` doc canonicalization [D:doc-comments]: a doc rides the indent of
// the declaration it attaches to (the line right after the run, if that
// is real code). Docs are transparent to assembly, so re-indenting one
// never changes the logical lines — the safety re-check still holds, and
// the output is always clean under the doc-alignment lint. Idempotent:
// an already-anchored doc is left where it is.
let private canonicalizeDocs (out: string list) : string list =
    let isDoc (s: string) = s.TrimStart(parserWs).StartsWith "///"

    let isCode (s: string) =
        (Script.stripComment s).Trim(parserWs) <> ""

    let arr = List.toArray out
    // district content is bytes [D:content-bytes] — a ///-shaped line
    // inside one is data, never re-anchored
    let masked = Script.districtContentMask out

    for i in 0 .. arr.Length - 1 do
        if not masked[i] && isDoc arr[i] then
            let mutable j = i + 1

            while j < arr.Length && isDoc arr[j] do
                j <- j + 1

            // a run followed by content sits inside the block (an
            // outdented comment never ends a district) — no anchor
            if j < arr.Length && isCode arr[j] && not masked[j] then
                let anchor = arr[j].Length - arr[j].TrimStart(parserWs).Length
                arr[i] <- String.replicate anchor " " + arr[i].TrimStart(parserWs)

    List.ofArray arr

// district markers ride the binding line [D:district-canonical]: a
// marker alone on a continuation line (`let x =` / `    <<<`) merges
// up onto the line it continues — the assembler joins the two with a
// single space, so the merged line assembles to the same logical text
// and the rewrite is lossless by construction (the caller verifies and
// reverts wholesale on any mismatch). Content lines are bytes and stay
// put — their indentation is relative to the first content line, so
// the main pass re-anchors them under the merged marker's depth. A
// close line between the binding indent and the old marker indent
// (`    |> f` under a next-line marker) outdents with the marker, or
// it would read as content; deeper statement-tail lines ride the same
// uniform shift.
let private canonicalizeDistrictMarkers (body: string list) : string list =
    let arr = List.toArray body

    let indentOf (s: string) =
        s |> Seq.takeWhile ((=) ' ') |> Seq.length

    let isBlank (s: string) = s.Trim(parserWs) = ""

    // the whole piece must be the marker — `<<<`, `$<<<`, `yaml` plus
    // its marker-line modifiers (`patch`, `by=`, `schema=`), nothing
    // else
    let markerOnly (piece: string) =
        match (Script.classifyPiece piece).Marker with
        | Script.MarkerKind.NoMarker -> false
        | Script.MarkerKind.Heredoc -> piece = "<<<" || piece = "$<<<"
        | _ -> Parser.yamlMarkerLen piece = piece.Length

    let out = ResizeArray<string>()
    // the arming marker line's original indent while its content pends
    let mutable armed: int option = None
    // after a merge: the merged line's indent, waiting for the close line
    let mutable pendingShift: int option = None
    // (delta, floor): statement-tail lines deeper than floor outdent by delta
    let mutable tailShift: (int * int) option = None

    let shifted (raw: string) (ind: int) =
        match tailShift with
        | Some(delta, floor) when ind > floor ->
            String.replicate (max floor (ind - delta)) " " + raw.TrimStart(parserWs)
        | _ -> raw

    for i in 0 .. arr.Length - 1 do
        let raw = arr[i]
        let code = Script.stripComment raw

        if isBlank raw then
            out.Add raw
        elif code.Trim(parserWs) = "" then
            // comment-only: rides a live tail shift, otherwise verbatim
            // (content bytes and transparent comments alike)
            out.Add(shifted raw (indentOf raw))
        else
            let ind = indentOf code

            match armed with
            | Some m when ind > m -> out.Add(shifted raw ind) // content is bytes
            | _ ->
                // leaving content: a close line above the merged indent must
                // outdent with the marker, and the statement tail rides along
                (match armed, pendingShift with
                 | Some _, Some newBase when ind > newBase -> tailShift <- Some(ind - newBase, newBase)
                 | _ -> ())

                if armed.IsSome then
                    armed <- None
                    pendingShift <- None

                (match tailShift with
                 | Some(_, floor) when ind <= floor -> tailShift <- None
                 | _ -> ())

                let raw = shifted raw ind
                let code = Script.stripComment raw
                let ind = indentOf code
                let piece = code.TrimStart(parserWs)

                let mergeTarget =
                    if markerOnly piece && ind > 0 && i > 0 && out.Count > 0 then
                        // the physically previous line, and it must be code
                        // (adjacency: blanks and comments never sit between),
                        // shallower, and comment-free (a trailing comment has
                        // nowhere to go once the marker takes the line end)
                        let prevRaw = arr[i - 1]
                        let prev = out[out.Count - 1]

                        if
                            not (isBlank prevRaw)
                            && (Script.stripComment prevRaw).Trim(parserWs) <> ""
                            && indentOf (Script.stripComment prev) < ind
                            && (Script.stripComment prev).TrimEnd(parserWs) = prev.TrimEnd(parserWs)
                        then
                            Some prev
                        else
                            None
                    else
                        None

                match mergeTarget with
                | Some prev ->
                    out[out.Count - 1] <- prev.TrimEnd(parserWs) + " " + raw.TrimStart(parserWs).TrimEnd(parserWs)
                    armed <- Some ind // the OLD marker indent still bounds the content
                    pendingShift <- Some(indentOf (Script.stripComment prev))
                | None ->
                    out.Add raw

                    if Script.isMarkerPiece piece then
                        armed <- Some ind

    List.ofSeq out

// an open bracket as fmt tracks it: inside one, a line that is not a
// new entry or a closer moves by the delta (formatted − original indent)
// of the line it continues [D:fmt-bracket-shift]
type private OpenBracket = {
    Kind: char
    // formatted column
    Col: int
    Stroustrup: bool
    // the opener line's formatted indent
    LineIndent: int
    // the opener line's original indent
    OrigLineIndent: int
    // first entry's formatted column (inline `{`/`[`) [D:field-alignment]
    Anchor: int option
    // entries' original indent; a deeper line continues an entry
    EntryIndent: int option
    Delta: int
}

// the column of a `match` keyword heading the arms below a line that
// ends `with`, past the line's start: outside string literals, a whole
// word (not `matched`, `x.match`)
let private midLineMatchHead (code: string) : int option =
    let code = code.TrimEnd(parserWs)

    if not (code.EndsWith " with") then
        None
    else
        let isIdent c =
            System.Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '\''

        let mutable inStr = false
        let mutable found = None
        let mutable i = 0

        while i < code.Length do
            let c = code[i]

            if inStr then
                if c = '\\' then
                    i <- i + 1
                elif c = '"' then
                    inStr <- false
            elif c = '"' then
                inStr <- true
            elif
                i > 0
                && System.String.CompareOrdinal(code, i, "match ", 0, 6) = 0
                && not (isIdent code[i - 1])
            then
                found <- Some i

            i <- i + 1

        found

// `lineOffset`: the physical lines above `body` (a shebang), so an error
// names the file's own line number
let private formatLinesCore (lineOffset: int) (body: string list) : Result<string list, string> =
    // trailing whitespace is never significant (strings are single-line and
    // close with a quote), so both equivalence passes compare TrimEnd'd code
    let commentOnly (raw: string) =
        Script.classifyLine raw = Script.LineKind.CommentOnly

    let logicalAt (off: int) (ls: string list) =
        ls
        |> List.mapi (fun i l -> i + 1 + off, l)
        |> List.filter (fun (_, raw) -> not (commentOnly raw))
        |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd(parserWs))
        |> Script.assemble

    let logicalOf = logicalAt 0

    let texts (lls: Script.LogicalLine list) = lls |> List.map (fun ll -> ll.Text)

    let body =
        // adopt the canonical marker layout only under proof: the
        // merged lines must assemble to the byte-identical logical
        // text [D:district-canonical] — anything else keeps the source
        // layout
        let candidate = canonicalizeDistrictMarkers body

        if candidate = body then
            body
        else
            match logicalOf body, logicalOf candidate with
            | Ok a, Ok b when texts a = texts b -> candidate
            | _ -> body

    // the gate reads comment lines as the script reader does: inside a
    // heredoc they are content and can break its layout [D:one-pipeline]
    let gate =
        match logicalAt lineOffset body with
        | Ok lls ->
            match body |> List.mapi (fun i l -> i + 1 + lineOffset, l) |> Script.assemble with
            | Error e -> Error e
            | Ok _ -> Ok lls
        | Error e -> Error e

    match gate with
    | Error e ->
        // located as a parse refusal is, line:col [D:assembly-col]
        match Script.assemblyErrorAt (body |> List.mapi (fun i l -> i + 1 + lineOffset, l)) e with
        | Some(l, c, rest) -> Error $"cannot format: line {l}:{c}: {rest} (fix errors first)"
        | None -> Error $"cannot format: {e} (fix errors first)"
    | Ok originalLogical ->

        // open indent levels, deepest first [D:fmt-depth-model]: any
        // deeper line opens a level, a line at a level returns to it,
        // col-0 resets — depth preserves every relational comparison
        // the assembler makes (=, <, >), so re-assembly is
        // join-for-join identical. Each level keeps (original indent,
        // formatted column): a line inside a bracket opens a level at the
        // column it was shifted to [D:fmt-bracket-shift], and a deeper
        // line goes to the next 4-column stop past its parent's column.
        let mutable levels: (int * int) list = []
        // open brackets, innermost first. A dangling opener (line ends
        // at the bracket, or a `{ ... with` header) takes Stroustrup
        // rules — entries at opener-indent+4, closers at opener-indent;
        // an inline opener keeps column alignment [D:fmt-stroustrup]
        let mutable braces: OpenBracket list = []
        // district: Some(markerOrigIndent, markerDepth) while inside a
        // district block — yaml is the one surviving marker
        // [D:district-retirement], so yamlDistrict is always true here;
        // a yaml district's relative indentation is semantic
        // [D:yaml-district] — its lines keep their offset from the
        // block's first line (the base), re-anchored at marker+1
        let mutable district: (int * int) option = None
        let mutable yamlDistrict = false
        let mutable yamlBase: int option = None
        // a heredoc's comment lines are its content, so one sets the base
        let mutable heredocDistrict = false
        // open match heads, innermost first: (originalIndent,
        // formattedCol, first arm's original indent once seen) — arms
        // align under the m [D:fmt-match-arms]
        let mutable matches: (int * int * int option) list = []
        // the last arm line's (original indent, original body column,
        // formatted body column): a `|>` line at or under an arm's body
        // continues that arm [D:match-pipe-offside], so it moves with the
        // body, never to block depth (left of it) [D:fmt-arm-pipe]
        let mutable armBody: (int * int * int) option = None

        // the indent of the next code line at or below each line: a
        // comment heading a yaml block takes its base from the block's
        // first code line, as the reader does (comments are invisible
        // to its layout) [D:yaml-district]
        let bodyArr = List.toArray body

        let nextCodeIndent =
            let arr = bodyArr
            let out = Array.create (arr.Length + 1) -1

            for i in arr.Length - 1 .. -1 .. 0 do
                out[i] <-
                    if Script.classifyLine arr[i] = Script.LineKind.Code then
                        arr[i] |> Seq.takeWhile ((=) ' ') |> Seq.length
                    else
                        out[i + 1]

            out

        // the distinct indents strictly between `parent` and `indent` of
        // the `else`/`elif` lines after `lineIdx`, until a line returns to
        // `parent` (arms and pipes have their own alignment)
        let between (lineIdx: int) (parent: int) (indent: int) =
            let arr = bodyArr
            let mutable j = lineIdx + 1
            let mutable stop = false
            let found = System.Collections.Generic.HashSet<int>()

            while not stop && j < arr.Length do
                if Script.classifyLine arr[j] = Script.LineKind.Code then
                    let i = arr[j] |> Seq.takeWhile ((=) ' ') |> Seq.length

                    if i <= parent then
                        stop <- true
                    elif i < indent && Script.continuesOpenIf (arr[j].TrimStart ' ') then
                        found.Add i |> ignore

                j <- j + 1

            found.Count

        let formatted =
            body
            |> List.mapi (fun lineIdx raw ->
                let code = Script.stripComment raw
                let content = raw.TrimStart(parserWs).TrimEnd(parserWs)

                if raw.Trim(parserWs) = "" then
                    // blanks never end statements [D:body-blanks]: all
                    // state survives the gap; the col-0 branches reset
                    // levels/matches at real statement boundaries
                    ""
                elif code.Trim(parserWs) = "" then
                    // comment-only: transparent to assembly [D:comment-transparency];
                    // keep it verbatim and leave formatter state alone
                    // — except inside a yaml district, where the line
                    // is content [D:content-bytes] and rides the
                    // re-anchor
                    let cIndent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                    match district with
                    | Some(m, mDepth) when yamlDistrict && cIndent > m ->
                        let b =
                            match yamlBase with
                            | Some b -> b
                            | None when not heredocDistrict && nextCodeIndent[lineIdx] > m -> nextCodeIndent[lineIdx]
                            | None ->
                                if heredocDistrict then
                                    yamlBase <- Some cIndent

                                cIndent

                        // a comment outdented past the base keeps its
                        // offset, clamped to stay inside the district
                        String.replicate (max (mDepth * 4 + 1) ((mDepth + 1) * 4 + (cIndent - b))) " "
                        + raw.Substring(min cIndent raw.Length)
                    | _ -> raw.TrimEnd(parserWs)
                else
                    let indent = code |> Seq.takeWhile ((=) ' ') |> Seq.length
                    let piece = code.TrimStart(parserWs)

                    match district with
                    | Some(m, mDepth) when indent > m ->
                        if yamlDistrict then
                            let b =
                                match yamlBase with
                                | Some b -> b
                                | None ->
                                    yamlBase <- Some indent
                                    indent

                            // bytes: trailing whitespace survives
                            // [D:content-bytes]; indentation is spaces only
                            // [D:spaces-only-indent] — strip just that
                            String.replicate (max (mDepth * 4 + 1) ((mDepth + 1) * 4 + (indent - b))) " "
                            + raw.Substring(min indent raw.Length)
                        else
                            // unreachable while yaml is the one marker
                            // [D:district-retirement] — kept as the non-yaml
                            // fallback shape, verbatim at marker+1 depth
                            String.replicate ((mDepth + 1) * 4) " " + content
                    | _ ->

                        district <- None
                        yamlDistrict <- false
                        yamlBase <- None

                        // match arms align under the m [D:fmt-match-arms];
                        // runs outside brackets and inside a paren (`(match`)
                        let armColOf () =
                            let isPipe =
                                piece.StartsWith "|"
                                && not (piece.StartsWith "|>")
                                && not (piece.StartsWith "||")

                            // a match closes at the offside boundary:
                            // strictly-shallower lines pop for arms too;
                            // same-indent non-arm lines pop as siblings
                            matches <-
                                matches
                                |> List.skipWhile (fun (mi, _, _) -> mi > indent || (mi >= indent && not isPipe))

                            if isPipe then
                                match matches with
                                | (mi, col, None) :: rest ->
                                    // the first pipe after a match head
                                    // is an arm; its indent anchors the
                                    // arm set [D:fmt-match-arms]
                                    matches <- (mi, col, Some indent) :: rest
                                    Some col
                                | (_, col, Some armIndent) :: _ when indent = armIndent -> Some col
                                | _ -> None
                            else
                                None

                        let armOrPipeCol () =
                            match armColOf () with
                            | Some col ->
                                armBody <-
                                    Script.armArrowIndex piece
                                    |> Option.bind (fun a ->
                                        let mutable j = a + 2

                                        while j < piece.Length && piece[j] = ' ' do
                                            j <- j + 1

                                        if j < piece.Length then
                                            Some(indent, indent + j, col + j)
                                        else
                                            None)

                                Some col
                            | None ->
                                match armBody with
                                | Some(_, ob, nb) when piece.StartsWith "|>" && indent >= ob -> Some(nb + indent - ob)
                                | Some(ai, _, _) when indent <= ai ->
                                    armBody <- None
                                    None
                                | _ -> None

                        let isCloser = piece.StartsWith "]" || piece.StartsWith "}" || piece.StartsWith ")"

                        // a continuation keeps its offset: it moves by the
                        // delta of the line it continues [D:fmt-bracket-shift]
                        let shifted delta =
                            if indent = 0 then 0 else max 1 (indent + delta)

                        let joinLevel col =
                            levels <- (indent, col) :: (levels |> List.filter (fun (k, _) -> k < indent))
                            col

                        // the assembler does not track parens: a line back at
                        // or left of a paren's opener line is outside it
                        braces <-
                            braces
                            |> List.skipWhile (fun b -> b.Kind = '(' && not isCloser && indent <= b.OrigLineIndent)

                        let col =
                            match braces with
                            | b :: rest when b.Stroustrup ->
                                // Stroustrup: closers return to the opener
                                // line's indent, entries sit one level in
                                if isCloser then
                                    joinLevel b.LineIndent
                                else
                                    match (if b.Kind = '(' then armOrPipeCol () else None), b.EntryIndent with
                                    | Some col, _ ->
                                        braces <- { b with Delta = col - indent } :: rest
                                        col
                                    | _, Some e when indent > e -> joinLevel (shifted b.Delta)
                                    | _ ->
                                        let col = b.LineIndent + 4

                                        braces <-
                                            {
                                                b with
                                                    EntryIndent = Some(defaultArg b.EntryIndent indent)
                                                    Delta = col - indent
                                            }
                                            :: rest

                                        joinLevel col
                            | b :: rest when b.Kind = '(' ->
                                // an arm's body moves with its arm
                                match armOrPipeCol () with
                                | Some col ->
                                    braces <- { b with Delta = col - indent } :: rest
                                    col
                                | None -> joinLevel (shifted b.Delta)
                            | b :: rest ->
                                // a new entry (or a closer) aligns under the
                                // first entry's measured column
                                // [D:field-alignment]; deeper lines continue
                                // an entry and shift with it
                                let anchor = defaultArg b.Anchor (b.Col + (if b.Kind = '{' then 2 else 1))
                                let entry = defaultArg b.EntryIndent (anchor - b.Delta)

                                if isCloser then
                                    joinLevel anchor
                                elif indent <= entry then
                                    braces <- { b with Delta = anchor - indent } :: rest
                                    joinLevel anchor
                                else
                                    joinLevel (shifted b.Delta)
                            | [] ->
                                match armOrPipeCol () with
                                | Some col -> col
                                | None ->
                                    if indent = 0 then
                                        levels <- []
                                        0
                                    else
                                        let col =
                                            match levels |> List.tryFind (fun (k, _) -> k <= indent) with
                                            | Some(k, c) when k = indent -> c
                                            | Some(k, c) ->
                                                // a later line that lands between this
                                                // level and its parent (an `else` between
                                                // its `if` and a deeper then-body) takes
                                                // the next stop, so this level steps past
                                                // it — the relation stays strict
                                                // [D:fmt-depth-model]
                                                (c / 4 + 1 + between lineIdx k indent) * 4
                                            | None -> 4

                                        joinLevel col

                        let formatted = String.replicate col " " + content

                        match (Script.classifyPiece piece).Marker with
                        | Script.MarkerKind.NoMarker -> ()
                        | marker ->
                            district <- Some(indent, col / 4)
                            // heredoc districts are content-bytes exactly as
                            // yaml's are [D:text-block] — the flag means
                            // "district content is verbatim", not "is yaml"
                            yamlDistrict <- (marker = Script.MarkerKind.Yaml || marker = Script.MarkerKind.Heredoc)
                            heredocDistrict <- (marker = Script.MarkerKind.Heredoc)

                            yamlBase <- None

                        // a match head, possibly opened by `(` on the same
                        // line (`(match …`), aligns its arms under the `m`
                        // [D:fmt-match-arms] — the stored column is the
                        // keyword's own, not the line indent
                        let mOff = piece |> Seq.takeWhile (fun c -> c = '(' || c = ' ') |> Seq.length

                        let mRest = piece.Substring mOff

                        if mRest.StartsWith "match " || mRest = "match" then
                            matches <- (indent, col + mOff, None) :: matches
                        else
                            // a match head later on its line (`let r = match
                            // x with`, `if c then match x with`) opens its arm
                            // set too [D:fmt-arm-pipe]
                            match midLineMatchHead (Script.stripComment piece) with
                            | Some at -> matches <- (indent, col + at, None) :: matches
                            | None -> ()

                        // re-annotate: brackets pushed on this line learn
                        // their style from where the line leaves its opener
                        // (comments carry no brackets)
                        let code = Script.stripComment formatted
                        let raw = braces |> List.map (fun b -> b.Kind, b.Col)
                        let newRaw = Script.braceStack raw code

                        let survived =
                            let rec common j =
                                let tailOf (l: (char * int) list) = List.skip (List.length l - j) l

                                if j < List.length raw && j < List.length newRaw && tailOf raw = tailOf newRaw then
                                    common (j + 1)
                                else
                                    j

                            common 0

                        let trimmed = code.TrimEnd(parserWs)

                        let pushed =
                            newRaw
                            |> List.take (List.length newRaw - survived)
                            |> List.map (fun (k, c) ->
                                let stroustrup = c = trimmed.Length - 1 || (k = '{' && trimmed.EndsWith " with")

                                // the sibling anchor is the first entry's
                                // measured column [D:field-alignment], not
                                // offset arithmetic (a `[ x` list anchors
                                // at +2 like a brace)
                                let anchor =
                                    let mutable j = c + 1

                                    while j < trimmed.Length && trimmed[j] = ' ' do
                                        j <- j + 1

                                    if j < trimmed.Length && stroustrup |> not then
                                        Some j
                                    else
                                        None

                                let delta = col - indent

                                {
                                    Kind = k
                                    Col = c
                                    Stroustrup = stroustrup
                                    LineIndent = col
                                    OrigLineIndent = indent
                                    Anchor = anchor
                                    EntryIndent = anchor |> Option.map (fun a -> a - delta)
                                    Delta = delta
                                })

                        braces <- pushed @ (braces |> List.skip (List.length braces - survived))
                        formatted)

        match logicalAt lineOffset formatted with
        | Error e -> Error $"fmt safety check failed (file left unchanged): {e}"
        | Ok formattedLogical ->
            if texts originalLogical <> texts formattedLogical then
                Error "fmt safety check failed: reformatting would change the parse; file left unchanged"
            else
                // ---- v2: intra-line respace under the shape guard ----
                // [D:fmt-respace] — a fixed permissive resolver on
                // both sides, so sexpr differences can only come from
                // the respacing itself.
                // Script.assumeResolver: command-shaped heads only —
                // an always-true IsExternal would claim `{Lomo` as a
                // head and make every let-RHS a command
                let shapeResolver = Script.assumeResolver Builtins.typeEnv

                // the expression-only twin [D:fmt-respace]: under it no
                // head is a command, so an ident-headed RHS (`dirty ||
                // staged`) keeps its expression shape once spaced — the
                // assume-resolver reads the spaced form as argv and would
                // veto every operator respace on such a line. A real
                // command line refuses to parse here (argv is not an
                // expression), so the assume side still guards argv.
                let exprResolver = {
                    shapeResolver with
                        IsExternal = (fun _ -> false)
                        IsCommandCallable = (fun _ -> false)
                        AliasHead = (fun _ -> None)
                }

                let shapeWith (r: Parser.Resolver) (text: string) =
                    match Parser.parseLine r text with
                    | Ok stmt -> Some(sexprStmt stmt)
                    | Error _ -> None

                let shape = shapeWith shapeResolver
                let shapeExpr = shapeWith exprResolver

                // a statement that reads as a command under the assume
                // resolver may BE one — its argv words are data
                // [D:fmt-argv-verbatim], so only the command-side shape
                // may vouch for its respace; one the assume side cannot
                // parse (`echo a||b`) is not proven command-free either
                let mayRunCommand (text: string) =
                    let rec hasCmd (e: Expr) =
                        match e.Kind with
                        | ECmd _ -> true
                        | _ -> exprChildren e |> List.exists hasCmd

                    match Parser.parseLine shapeResolver text with
                    | Ok stmt -> Parser.stmtExprs stmt |> List.exists hasCmd
                    | Error _ -> true

                // lines respace never touches [D:fmt-respace]: district
                // content is bytes [D:content-bytes], and a line that begins
                // inside a string (an interpolation hole spanning lines) has
                // no per-line string mask — the shape guard cannot catch a
                // respaced hole (`{a}` → `{ a }` keeps the sexpr)
                let frozen =
                    let arr = List.toArray formatted
                    let mask = Script.districtContentMask formatted

                    for ll in formattedLogical do
                        let mutable prefix = Script.stringStart

                        for (_, pl, _) in ll.Segments do
                            let i = pl - 1 - lineOffset

                            if i >= 0 && i < arr.Length then
                                if Script.isInsideString prefix then
                                    mask[i] <- true

                                prefix <- Script.stringStep prefix ("\n" + Script.stripComment arr[i])

                    mask

                let respaced =
                    formatted
                    |> List.mapi (fun i raw ->
                        if frozen[i] then
                            raw
                        else
                            // respace the code only; the gap before a
                            // trailing comment is alignment and survives
                            let code = Script.stripComment raw
                            let codeTrim = code.TrimEnd(parserWs)

                            Script.respaceLine codeTrim
                            + code.Substring codeTrim.Length
                            + raw.Substring code.Length)

                match logicalOf respaced with
                | Error _ -> Ok formatted // respace broke assembly: revert wholesale
                | Ok respacedLogical when List.length respacedLogical <> List.length formattedLogical -> Ok formatted
                | Ok respacedLogical ->
                    // statements whose shape changed (or was never
                    // parseable) revert to their pre-respace lines
                    let revertLines =
                        List.zip formattedLogical respacedLogical
                        |> List.collect (fun (o, n) ->
                            // the assume side proves command lines
                            // unchanged; the expression side vouches only
                            // when neither spelling can be a command
                            // (`c&&d` is an expression, `c && d` may be
                            // argv) [D:fmt-respace] [D:fmt-argv-verbatim]
                            let agreesUnder shapeOf =
                                match shapeOf o.Text, shapeOf n.Text with
                                | Some a, Some b -> a = b
                                | _ -> false

                            if
                                agreesUnder shape
                                || (not (mayRunCommand o.Text)
                                    && not (mayRunCommand n.Text)
                                    && agreesUnder shapeExpr)
                            then
                                []
                            else
                                if System.Environment.GetEnvironmentVariable "WEIR_FMT_DEBUG" <> null then
                                    // %A is reflection printing — FSharp.Core's AOT-flagged
                                    // corner [D:aot-warnings]; interpolation stays AOT-safe
                                    eprintfn $"REVERT {shape o.Text} vs {shape n.Text} for {o.Text} ||| {n.Text}"

                                n.Segments |> List.map (fun (_, pl, _) -> pl))
                        |> Set.ofList

                    let final =
                        List.zip formatted respaced
                        |> List.mapi (fun i (pre, post) -> if Set.contains (i + 1) revertLines then pre else post)

                    Ok final

// fmt is idempotent on everything it accepts: a layout that moves again
// on a second pass (mismatched brackets the re-indenter cannot place) is
// refused, never written to flip-flop between two forms
let private formatSettled (lineOffset: int) (body: string list) : Result<string list, string> =
    let once = formatLinesCore lineOffset body |> Result.map canonicalizeDocs

    match once with
    | Ok first ->
        match formatLinesCore lineOffset first |> Result.map canonicalizeDocs with
        | Ok second when second = first -> once
        | Ok second ->
            let moved =
                Seq.zip first second
                |> Seq.tryFindIndex (fun (a, b) -> a <> b)
                |> Option.defaultValue (min first.Length second.Length)

            Error
                $"cannot format: line {moved + 1 + lineOffset}: the layout does not settle — a bracket is likely mismatched (fix errors first)"
        | Error e -> Error e
    | Error e -> Error e

let formatLines (body: string list) : Result<string list, string> = formatSettled 0 body

let private alignSessionFields (body: string list) : string list =
    let fieldStart = System.Text.RegularExpressions.Regex @"^[A-Za-z_][A-Za-z0-9_]*\s*="

    let indentOf (l: string) =
        l |> Seq.takeWhile ((=) ' ') |> Seq.length

    let mutable inBlock = false
    let mutable delta = 0

    [
        for l in body do
            let t = l.Trim(parserWs)

            if not inBlock then
                if t = "#session {" then
                    inBlock <- true
                    delta <- 0

                l
            elif t = "}" then
                inBlock <- false
                l
            elif t = "" || Script.classifyLine l <> Script.LineKind.Code then
                l
            elif fieldStart.IsMatch t then
                delta <- 4 - indentOf l
                "    " + l.TrimStart ' '
            else
                // a continuation keeps its offset from its field, never
                // leaving the block's body
                let i = indentOf l

                if i + delta > 4 then
                    String.replicate (i + delta) " " + l.TrimStart ' '
                else
                    l
    ]

/// The one formatter entry point for `weir fmt` and the LSP's
/// formatting request [D:fmt-v1]: a source text in, its canonical lines
/// out, plus whether they differ from the source. The shebang line
/// rides verbatim (never formatted), an error names the file's own line
/// number, and the canonical text ends in exactly one newline — a
/// source without it is not canonical. CRLF reads as LF. A file `weir
/// check` cannot parse is refused [D:fmt-parse-gate]; `path` anchors
/// its imports, as check's does.
let formatSource (path: string) (text: string) : Result<string list * bool, string> =
    // lines split as the script reader splits them (a bare \r too)
    // [D:one-pipeline]
    let lines =
        if text = "" then
            []
        else
            let all = Script.splitSourceText text

            if Script.endsWithLineBreak text then
                List.take (all.Length - 1) all
            else
                all

    let finalNewline = text = "" || Script.endsWithLineBreak text

    let header, body =
        match lines with
        | first :: rest when first.StartsWith "#!" -> [ first ], rest
        | _ -> [], lines

    // the formatter only proves the file assembles; the checker's own
    // parse decides whether it is a script fmt may rewrite — a parse
    // error in this file (an import's is its own) refuses, located
    let parseError () =
        let diags, _, _, _, _ = Script.analyzeLines path lines

        diags
        |> List.tryFind (fun d ->
            d.File = path
            && (d.Code = "parse" || d.Code = "directive")
            && d.Severity = "error")

    // an init file's #session fields are separate settings, each read on
    // its own (Script.splitInit) — so any field indent loads; fmt seats
    // every field start at 4, its continuation lines riding along
    // [D:init-session-fields]
    let body =
        if Script.isInitMarked lines then
            alignSessionFields body
        else
            body

    formatSettled header.Length body
    |> Result.bind (fun formattedBody ->
        match parseError () with
        | Some d -> Error $"cannot format: line {d.Line}:{d.Col}: {d.Message} (fix errors first)"
        | None ->
            // exactly one final newline: trailing blank lines go — blanks
            // never end a statement [D:body-blanks], so the assembly is
            // unchanged (checked, not assumed)
            let trimmed =
                let kept =
                    formattedBody
                    |> List.rev
                    |> List.skipWhile (fun l -> l.Trim(parserWs) = "")
                    |> List.rev

                let assembled (ls: string list) =
                    ls
                    |> List.mapi (fun i l -> i + 1, l)
                    |> Script.assemble
                    |> Result.map (List.map _.Text)

                match assembled formattedBody, assembled kept with
                | Ok a, Ok b when a = b -> kept
                | _ -> formattedBody

            let result = header @ trimmed
            Ok(result, (result <> lines || not finalNewline)))

// #save's bare-alias qualifier [D:repl-save]: a saved script is
// strict, where bare aliases (`map`, `where`, `startsWith`) do not
// exist — so each single-home bare name is rewritten to its qualified
// spelling (`Seq.map`, `Str.startsWith`) using the same
// `bareAliasHomes` map the checker's did-you-mean reads. Span-based
// (parse, collect EVar uses, replace right-to-left) so it never
// touches a string literal or a field name that happens to spell a
// bare alias. Operates on a single physical line (the REPL
// transcript's logical-line text); parse failure leaves it unchanged.
/// `qualifyBareAliases`, leaving the names in `bound` alone: a session
/// binding named like a bare alias (`let find = 3`) is the user's value
/// [D:alias-binding-shadow]
let qualifyBareAliasesExcept (bound: Set<string>) (r: Parser.Resolver) (line: string) : string =
    match Parser.parseLineFull r line with
    | Error _ -> line
    | Ok stmt ->
        let uses =
            Parser.stmtExprs stmt
            |> List.collect collectBareUses
            |> List.filter (fun (_, name) -> not (bound.Contains name))
            // right-to-left so earlier column offsets stay valid
            |> List.sortByDescending (fun (sp, _) -> sp.Start.Col)

        uses
        |> List.fold
            (fun (acc: string) (sp, name) ->
                let col = sp.Start.Col - 1 // 1-based -> 0-based

                if
                    col >= 0
                    && col + name.Length <= acc.Length
                    && acc.Substring(col, name.Length) = name
                then
                    match Map.tryFind name Builtins.bareAliasHomes with
                    | Some home -> acc.Substring(0, col) + $"{home}.{name}" + acc.Substring(col + name.Length)
                    | None -> acc
                else
                    acc)
            line

let qualifyBareAliases (r: Parser.Resolver) (line: string) : string =
    qualifyBareAliasesExcept Set.empty r line

// collect command-head spans and their head names [D:command-head-alias]:
// an ECmd's head is the literal program name, and its token sits at the
// ECmd span's start (length = the name). Only heads, never a name in an
// argument or a string — so `k` as an argv word or inside `"k"` is
// untouched.
let collectCmdHeads (e: Expr) : (Span * string) list =
    let acc = ResizeArray<Span * string>()

    let rec walk (e: Expr) =
        (match e.Kind with
         // literal heads only — a dynamic head [D:dynamic-head] is
         // never an alias, so #save leaves it untouched
         | ECmd(HeadLit prog, _, _) ->
             let headSpan = {
                 Start = e.Span.Start
                 End = {
                     e.Span.Start with
                         Col = e.Span.Start.Col + prog.Length
                 }
             }

             acc.Add(headSpan, prog)
         | _ -> ())

        exprChildren e |> List.iter walk

    walk e
    List.ofSeq acc

// Desugar command-head aliases for `#save` [D:command-head-alias]:
// rewrite each command head that is an alias back to its real
// invocation (exe + fixed prefix args), span-based, so the saved
// script is alias-free and `weir check` clean. Mirrors
// `qualifyBareAliases`' discipline exactly.
//
// The resolver `r` must resolve alias names as external heads (so
// `k get po` parses as an ECmd) without rewriting them (AliasHead =
// None), so the head keeps its source name and span; `aliasOf`
// supplies the desugaring. Operates on a single physical line; parse
// failure leaves it unchanged.
let desugarAliasHeads (r: Parser.Resolver) (aliasOf: string -> (string * string list) option) (line: string) : string =
    match Parser.parseLineFull r line with
    | Error _ -> line
    | Ok stmt ->
        let heads =
            Parser.stmtExprs stmt
            |> List.collect collectCmdHeads
            // right-to-left so earlier column offsets stay valid
            |> List.sortByDescending (fun (sp, _) -> sp.Start.Col)

        heads
        |> List.fold
            (fun (acc: string) (sp, name) ->
                let col = sp.Start.Col - 1 // 1-based -> 0-based

                if
                    col >= 0
                    && col + name.Length <= acc.Length
                    && acc.Substring(col, name.Length) = name
                then
                    match aliasOf name with
                    | Some(exe, prefix) ->
                        // a target that names a weir value (`ls`) would read
                        // as that value in a script: force the PATH binary,
                        // as the alias did (`cd` stays — it is command-callable)
                        let exe =
                            if r.IsKnown exe && not (r.IsCommandCallable exe) then
                                "^" + exe
                            else
                                exe

                        let replacement =
                            match prefix with
                            | [] -> exe
                            | _ -> exe + " " + String.concat " " prefix

                        acc.Substring(0, col) + replacement + acc.Substring(col + name.Length)
                    | None -> acc
                else
                    acc)
            line

let formatFile (checkOnly: bool) (path: string) : int =
    match Script.readEntryScript path with
    | Error msg ->
        System.Console.Error.WriteLine $"weir: {msg}"
        2
    | Ok _ ->
        // the entry read validated the file [D:source-utf8]; the formatter
        // takes the raw text so a BOM and the shebang survive verbatim
        let text = System.IO.File.ReadAllText path

        match formatSource path text with
        | Error msg ->
            System.Console.Error.WriteLine $"weir fmt: {msg}"
            3
        | Ok(result, changed) ->
            if not changed then
                if not checkOnly then
                    System.Console.Error.WriteLine "weir fmt: already formatted"

                0
            elif checkOnly then
                System.Console.Error.WriteLine $"weir fmt: {Weir.Types.quoteData path} would be reformatted"
                1
            else
                try
                    // a leading BOM is kept: the reader strips exactly one, so
                    // dropping it would expose a second one to the parser
                    let bom =
                        use f = System.IO.File.OpenRead path
                        let head = Array.zeroCreate<byte> 3
                        f.Read(head, 0, 3) = 3 && head = [| 0xEFuy; 0xBBuy; 0xBFuy |]

                    // the file's own line ending is kept: CRLF reads as LF, so
                    // writing the platform newline would turn an LF file CRLF on
                    // Windows (churn fmt --check never asked for)
                    let nl = if text.Contains "\r\n" then "\r\n" else "\n"

                    let out =
                        if List.isEmpty result then
                            ""
                        else
                            String.concat nl result + nl

                    System.IO.File.WriteAllText(path, out, System.Text.UTF8Encoding(bom))
                    System.Console.Error.WriteLine $"weir fmt: formatted {Weir.Types.quoteData path}"
                    0
                with
                | :? System.UnauthorizedAccessException ->
                    System.Console.Error.WriteLine
                        $"weir fmt: cannot write {Weir.Types.quoteData path}: permission denied"

                    2
                | :? System.IO.IOException as ex ->
                    System.Console.Error.WriteLine
                        $"weir fmt: cannot write {Weir.Types.quoteData path}: {Weir.Types.quoteData ex.Message}"

                    2
