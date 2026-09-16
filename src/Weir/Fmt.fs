module Weir.Fmt

open Weir.Ast

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
// weir fmt <script> [D:fmt-v1] + intra-line respace [D:fmt-respace]
// (v2, on the update-example receipt): collapse space runs, pad
// record braces, tidy `;` — under a PARSE-SHAPE safety check: each
// respaced statement must sexpr-match its original (same permissive
// resolver both sides) or that statement REVERTS to its pre-respace
// text. Comments keep their text; re-flowing stays parked.
// Pipe-headed lines keep the column-0 shell style if they use it.

// `///` doc canonicalization [D:doc-comments]: a doc rides the indent of
// the declaration it attaches to (the line right after the run, if that
// is real code). Docs are transparent to assembly, so re-indenting one
// never changes the logical lines — the safety re-check still holds, and
// the output is always clean under the doc-alignment lint. Idempotent:
// an already-anchored doc is left where it is.
let private canonicalizeDocs (out: string list) : string list =
    let isDoc (s: string) = s.TrimStart().StartsWith "///"
    let isCode (s: string) = (Script.stripComment s).Trim() <> ""
    let arr = List.toArray out
    // district content is bytes [D:content-bytes] — a ///-shaped line
    // inside one is data, never re-anchored
    let masked = Script.districtContentMask out

    for i in 0 .. arr.Length - 1 do
        if not masked[i] && isDoc arr[i] then
            let mutable j = i + 1

            while j < arr.Length && isDoc arr[j] do
                j <- j + 1

            if j < arr.Length && isCode arr[j] then
                let anchor = arr[j].Length - arr[j].TrimStart().Length
                arr[i] <- String.replicate anchor " " + arr[i].TrimStart()

    List.ofArray arr

// district markers ride the binding line [D:district-canonical]: a
// marker ALONE on a continuation line (`let x =` / `    <<<`) merges
// up onto the line it continues — the assembler joins the two with a
// single space, so the merged line assembles to the SAME logical text
// and the rewrite is lossless by construction (the caller verifies and
// reverts wholesale on any mismatch). Content lines are bytes and stay
// put — their indentation is RELATIVE to the first content line, so
// the main pass re-anchors them under the merged marker's depth. A
// close line between the binding indent and the old marker indent
// (`    |> f` under a next-line marker) outdents with the marker, or
// it would read as content; deeper statement-tail lines ride the same
// uniform shift.
let private canonicalizeDistrictMarkers (body: string list) : string list =
    let arr = List.toArray body
    let indentOf (s: string) = s |> Seq.takeWhile ((=) ' ') |> Seq.length
    let isBlank (s: string) = s.Trim() = ""

    // the WHOLE piece is the marker — `<<<`, `$<<<`, `yaml` plus its
    // marker-line modifiers (`patch`, `by=`, `schema=`), nothing else
    let markerOnly (piece: string) =
        match (Script.classifyPiece piece).Marker with
        | Script.MarkerKind.NoMarker -> false
        | Script.MarkerKind.Heredoc -> piece = "<<<" || piece = "$<<<"
        | _ -> Parser.yamlMarkerLen piece = piece.Length

    let out = ResizeArray<string>()
    // the arming marker line's ORIGINAL indent while its content pends
    let mutable armed: int option = None
    // after a merge: the merged line's indent, waiting for the close line
    let mutable pendingShift: int option = None
    // (delta, floor): statement-tail lines deeper than floor outdent by delta
    let mutable tailShift: (int * int) option = None

    let shifted (raw: string) (ind: int) =
        match tailShift with
        | Some(delta, floor) when ind > floor -> String.replicate (max floor (ind - delta)) " " + raw.TrimStart()
        | _ -> raw

    for i in 0 .. arr.Length - 1 do
        let raw = arr[i]
        let code = Script.stripComment raw

        if isBlank raw then
            out.Add raw
        elif code.Trim() = "" then
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
                let piece = code.TrimStart()

                let mergeTarget =
                    if markerOnly piece && ind > 0 && i > 0 && out.Count > 0 then
                        // the physically previous line, and it must be code
                        // (adjacency: blanks and comments never sit between),
                        // shallower, and comment-free (a trailing comment has
                        // no seat once the marker takes the line end)
                        let prevRaw = arr[i - 1]
                        let prev = out[out.Count - 1]

                        if
                            not (isBlank prevRaw)
                            && (Script.stripComment prevRaw).Trim() <> ""
                            && indentOf (Script.stripComment prev) < ind
                            && (Script.stripComment prev).TrimEnd() = prev.TrimEnd()
                        then
                            Some prev
                        else
                            None
                    else
                        None

                match mergeTarget with
                | Some prev ->
                    out[out.Count - 1] <- prev.TrimEnd() + " " + raw.TrimStart().TrimEnd()
                    armed <- Some ind // the OLD marker indent still bounds the content
                    pendingShift <- Some(indentOf (Script.stripComment prev))
                | None ->
                    out.Add raw

                    if Script.isMarkerPiece piece then
                        armed <- Some ind

    List.ofSeq out

let private formatLinesCore (body: string list) : Result<string list, string> =
    // trailing whitespace is never significant (strings are single-line and
    // close with a quote), so both equivalence passes compare TrimEnd'd code
    let commentOnly (raw: string) =
        Script.classifyLine raw = Script.LineKind.CommentOnly

    let logicalOf (ls: string list) =
        ls
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> not (commentOnly raw))
        |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd())
        |> Script.assemble

    let texts (lls: Script.LogicalLine list) = lls |> List.map (fun ll -> ll.Text)

    let body =
        // adopt the canonical marker layout ONLY under proof: the merged
        // lines must assemble to the byte-identical logical text
        // [D:district-canonical] — anything else keeps the source layout
        let candidate = canonicalizeDistrictMarkers body

        if candidate = body then
            body
        else
            match logicalOf body, logicalOf candidate with
            | Ok a, Ok b when texts a = texts b -> candidate
            | _ -> body

    match logicalOf body with
    | Error e -> Error $"cannot format: {e} (fix errors first)"
    | Ok originalLogical ->

        // open indent levels, deepest first [D:fmt-depth-model]: any
        // deeper line opens a level, a line AT a level returns to it,
        // col-0 resets — depth preserves every relational comparison
        // the assembler makes (=, <, >), so re-assembly is
        // join-for-join identical.
        let mutable levels: int list = []
        // open brackets, annotated: (kind, column, stroustrup, opener
        // line's formatted indent). A DANGLING opener (line ends at the
        // bracket, or a `{ ... with` header) takes Stroustrup rules —
        // entries at opener-indent+4, closers at opener-indent; an
        // inline opener keeps column alignment [D:fmt-stroustrup]
        let mutable braces: (char * int * bool * int * int option) list = []
        // district: Some(markerOrigIndent, markerDepth) while inside a
        // district block — yaml is the ONE surviving marker
        // [D:district-retirement], so yamlDistrict is always true here; a
        // yaml district's RELATIVE indentation is semantic
        // [D:yaml-district] — its lines keep their offset from the
        // block's first line (the base), re-anchored at marker+1
        let mutable district: (int * int) option = None
        let mutable yamlDistrict = false
        let mutable yamlBase: int option = None
        // open match heads, innermost first: (originalIndent,
        // formattedCol, first arm's original indent once seen) — arms
        // align under the m [D:fmt-match-arms]
        let mutable matches: (int * int * int option) list = []

        let formatted =
            body
            |> List.map (fun raw ->
                let code = Script.stripComment raw
                let content = raw.TrimStart().TrimEnd()

                if raw.Trim() = "" then
                    // blanks never end statements [D:body-blanks]: all
                    // state survives the gap; the col-0 branches already
                    // reset levels/matches at real statement boundaries,
                    // which IS the deferred decision the plan asks for
                    ""
                elif code.Trim() = "" then
                    // comment-only: transparent to assembly [D:comment-transparency];
                    // keep it verbatim and leave formatter state alone —
                    // EXCEPT inside a yaml district, where the line is
                    // CONTENT [D:content-bytes] and rides the re-anchor
                    let cIndent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                    match district with
                    | Some(m, mDepth) when yamlDistrict && cIndent > m ->
                        let b =
                            match yamlBase with
                            | Some b -> b
                            | None ->
                                yamlBase <- Some cIndent
                                cIndent

                        String.replicate ((mDepth + 1) * 4 + (cIndent - b)) " "
                        + raw.Substring(min cIndent raw.Length)
                    | _ -> raw.TrimEnd()
                else
                    let indent = code |> Seq.takeWhile ((=) ' ') |> Seq.length
                    let piece = code.TrimStart()

                    match district with
                    | Some(m, mDepth) when indent > m ->
                        if yamlDistrict then
                            let b =
                                match yamlBase with
                                | Some b -> b
                                | None ->
                                    yamlBase <- Some indent
                                    indent

                            // bytes: trailing whitespace AND a content-
                            // leading tab survive [D:content-bytes] — strip
                            // only the space indentation
                            String.replicate ((mDepth + 1) * 4 + (indent - b)) " "
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

                        let formatted =
                            match braces with
                            | (_, _, true, oIndent, _) :: _ ->
                                // Stroustrup: closers return to the opener
                                // line's indent, entries sit one level in
                                let col =
                                    if piece.StartsWith "]" || piece.StartsWith "}" then
                                        oIndent
                                    else
                                        oIndent + 4

                                String.replicate col " " + content
                            | (kind, top, false, _, anchor) :: _ ->
                                // bracket continuation: align under the first
                                // entry's MEASURED column [D:field-alignment]
                                let col =
                                    match anchor with
                                    | Some a -> a
                                    | None -> top + (if kind = '{' then 2 else 1)

                                String.replicate col " " + content
                            | [] ->
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

                                let armCol =
                                    if isPipe then
                                        match matches with
                                        | (mi, col, None) :: rest ->
                                            // the first pipe after a match head
                                            // IS an arm; its indent anchors the
                                            // arm set [D:fmt-match-arms]
                                            matches <- (mi, col, Some indent) :: rest
                                            Some col
                                        | (_, col, Some armIndent) :: _ when indent = armIndent -> Some col
                                        | _ -> None
                                    else
                                        None

                                match armCol with
                                | Some col -> String.replicate col " " + content
                                | None ->
                                    let depth =
                                        if indent = 0 then
                                            levels <- []
                                            0
                                        else
                                            let kept = levels |> List.filter (fun k -> k < indent)
                                            levels <- indent :: kept
                                            List.length kept + 1

                                    match (Script.classifyPiece piece).Marker with
                                    | Script.MarkerKind.NoMarker -> ()
                                    | marker ->
                                        district <- Some(indent, depth)
                                        // heredoc districts are content-bytes exactly as
                                        // yaml's are [D:text-block] — the flag means
                                        // "district content is verbatim", not "is yaml"
                                        yamlDistrict <-
                                            (marker = Script.MarkerKind.Yaml || marker = Script.MarkerKind.Heredoc)

                                        yamlBase <- None

                                    let line = String.replicate (depth * 4) " " + content

                                    if piece.StartsWith "match " then
                                        matches <- (indent, depth * 4, None) :: matches

                                    line

                        // re-annotate: entries pushed on THIS line learn
                        // their style from where the line leaves its opener
                        let raw = braces |> List.map (fun (k, c, _, _, _) -> k, c)
                        let newRaw = Script.braceStack raw formatted

                        let survived =
                            let rec common j =
                                let tailOf (l: (char * int) list) = List.skip (List.length l - j) l

                                if j < List.length raw && j < List.length newRaw && tailOf raw = tailOf newRaw then
                                    common (j + 1)
                                else
                                    j

                            common 0

                        let trimmed = formatted.TrimEnd()
                        let lineIndent = formatted.Length - formatted.TrimStart().Length

                        let pushed =
                            newRaw
                            |> List.take (List.length newRaw - survived)
                            |> List.map (fun (k, c) ->
                                let stroustrup = c = trimmed.Length - 1 || (k = '{' && trimmed.EndsWith " with")

                                // the sibling anchor is the first entry's
                                // MEASURED column [D:field-alignment] — never
                                // offset arithmetic (a `[ x` list anchors at
                                // +2 like a brace)
                                let anchor =
                                    let mutable j = c + 1

                                    while j < formatted.Length && formatted[j] = ' ' do
                                        j <- j + 1

                                    if j < formatted.Length && stroustrup |> not then
                                        Some j
                                    else
                                        None

                                k, c, stroustrup, lineIndent, anchor)

                        braces <- pushed @ (braces |> List.skip (List.length braces - survived))
                        formatted)

        match logicalOf formatted with
        | Error e -> Error $"fmt safety check failed (file left unchanged): {e}"
        | Ok formattedLogical ->
            if texts originalLogical <> texts formattedLogical then
                Error "fmt safety check failed: reformatting would change the parse; file left unchanged"
            else
                // ---- v2: intra-line respace under the shape guard ----
                // [D:fmt-respace] — a fixed permissive resolver on BOTH
                // sides, so sexpr differences can only come from the
                // respacing itself
                // Script.assumeResolver: command-SHAPED heads only —
                // an always-true IsExternal would claim `{Lomo` as a
                // head and make every let-RHS a command
                let shapeResolver = Script.assumeResolver Builtins.typeEnv

                let shape (text: string) =
                    match Parser.parseLine shapeResolver text with
                    | Ok stmt -> Some(sexprStmt stmt)
                    | Error _ -> None

                let respaced =
                    formatted
                    |> List.map (fun raw ->
                        // respace the code only; the gap before a
                        // trailing comment is ALIGNMENT and survives
                        let code = Script.stripComment raw
                        let codeTrim = code.TrimEnd()

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
                            match shape o.Text, shape n.Text with
                            | Some a, Some b when a = b -> []
                            | sa, sb ->
                                if System.Environment.GetEnvironmentVariable "WEIR_FMT_DEBUG" <> null then
                                    // %A is reflection printing — FSharp.Core's AOT-flagged
                                    // corner [D:aot-warnings]; interpolation stays AOT-safe
                                    eprintfn $"REVERT {sa} vs {sb} for {o.Text} ||| {n.Text}"

                                n.Segments |> List.map (fun (_, pl, _) -> pl))
                        |> Set.ofList

                    let final =
                        List.zip formatted respaced
                        |> List.mapi (fun i (pre, post) -> if Set.contains (i + 1) revertLines then pre else post)

                    Ok final

let formatLines (body: string list) : Result<string list, string> =
    formatLinesCore body |> Result.map canonicalizeDocs

// #save's bare-alias QUALIFIER [D:repl-save]: a saved script is STRICT,
// where bare aliases (`map`, `where`, `startsWith`) do not exist — so
// each single-home bare name is rewritten to its qualified spelling
// (`Seq.map`, `Str.startsWith`) using the SAME `bareAliasHomes` map the
// checker's did-you-mean reads. Span-based (parse, collect EVar uses,
// replace right-to-left) so it never touches a string literal or a field
// name that happens to spell a bare alias. A SINGLE physical line
// (the REPL transcript's logical-line text); parse failure -> unchanged.
let qualifyBareAliases (r: Parser.Resolver) (line: string) : string =
    match Parser.parseLineFull r line with
    | Error _ -> line
    | Ok stmt ->
        let uses =
            Parser.stmtExprs stmt
            |> List.collect collectBareUses
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

let formatFile (checkOnly: bool) (path: string) : int =
    if not (System.IO.File.Exists path) then
        System.Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let lines = System.IO.File.ReadAllLines path |> Array.toList

        let header, body =
            match lines with
            | first :: rest when first.StartsWith "#!" -> [ first ], rest
            | _ -> [], lines

        match formatLines body with
        | Error msg ->
            System.Console.Error.WriteLine $"weir fmt: {msg}"
            3
        | Ok formattedBody ->
            let result = header @ formattedBody

            if result = lines then
                if not checkOnly then
                    System.Console.Error.WriteLine "weir fmt: already formatted"

                0
            elif checkOnly then
                System.Console.Error.WriteLine $"weir fmt: {path} would be reformatted"
                1
            else
                System.IO.File.WriteAllLines(path, result)
                System.Console.Error.WriteLine $"weir fmt: formatted {path}"
                0
