module Weir.Fmt

open Weir.Ast

let private collectBareUses (e: Expr) : (Span * string) list =
    // traversal is Ast.exprChildren's job; only EVar collects
    let acc = ResizeArray<Span * string>()

    let rec walk (e: Expr) =
        (match e.Kind with
         | EVar name when Map.containsKey name Builtins.bareAliasHomes -> acc.Add(e.Span, name)
         | _ -> ())

        exprChildren e |> List.iter walk

    walk e
    List.ofSeq acc

let qualifyLine (r: Parser.Resolver) (line: string) : string * int =
    match Parser.parseLine r line with
    | Error _ -> line, 0
    | Ok stmt ->
        let uses =
            match stmt with
            | SExpr e
            | SCmd e -> collectBareUses e
            | SLet(_, e) -> collectBareUses e
            | SLetPat(_, e) -> collectBareUses e
            | SType _
            | SModule _
            | SImport _ -> []

        let applicable =
            uses
            |> List.filter (fun (span, _) ->
                let idx = span.Start.Col - 1
                idx >= line.Length || line[idx] <> '$')
            |> List.sortByDescending (fun (span, _) -> span.Start.Col)

        let rewritten =
            applicable
            |> List.fold
                (fun (l: string) (span, name) ->
                    let home = Builtins.bareAliasHomes[name]
                    let before = l.Substring(0, span.Start.Col - 1)
                    let after = l.Substring(span.Start.Col - 1 + name.Length)
                    before + $"{home}.{name}" + after)
                line

        rewritten, List.length applicable

let qualifyFile (path: string) : int =
    if not (System.IO.File.Exists path) then
        System.Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let typeEnv, _ = Prelude.extend Builtins.typeEnv Builtins.valueEnv

        let r = Script.resolver typeEnv

        Extern.refresh ()
        let lines = System.IO.File.ReadAllLines path
        let mutable total = 0
        let mutable droppedLoose = false

        let output =
            lines
            |> Array.map (fun line ->
                if line.StartsWith "#!" then
                    line
                elif line.Trim() = "#loose" then
                    droppedLoose <- true
                    null
                else
                    let code = Script.stripComment line

                    if code.Trim() = "" then
                        line
                    else
                        let rewrittenCode, n = qualifyLine r code
                        total <- total + n

                        if n = 0 then
                            line
                        else
                            rewrittenCode + line.Substring(code.Length))
            |> Array.filter (fun l -> not (isNull l))

        System.IO.File.WriteAllLines(path, output)

        let looseNote = if droppedLoose then "; #loose directive removed" else ""
        System.Console.Error.WriteLine $"weir fmt: {total} name(s) qualified{looseNote}"
        0

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

let private formatLinesCore (body: string list) : Result<string list, string> =
    // trailing whitespace is never significant (strings are single-line and
    // close with a quote), so both equivalence passes compare TrimEnd'd code
    let commentOnly (raw: string) =
        Script.classifyLine raw = Script.LineKind.CommentOnly

    let numbered =
        body
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> not (commentOnly raw))
        |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd())

    match Script.assemble numbered with
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
                                        yamlDistrict <- (marker = Script.MarkerKind.Yaml)
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

        let renumbered =
            formatted
            |> List.mapi (fun i l -> i + 1, l)
            |> List.filter (fun (_, raw) -> not (commentOnly raw))
            |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd())

        match Script.assemble renumbered with
        | Error e -> Error $"fmt safety check failed (file left unchanged): {e}"
        | Ok formattedLogical ->
            let texts (lls: Script.LogicalLine list) = lls |> List.map (fun ll -> ll.Text)

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

                let renumbered2 =
                    respaced
                    |> List.mapi (fun i l -> i + 1, l)
                    |> List.filter (fun (_, raw) -> not (commentOnly raw))
                    |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd())

                match Script.assemble renumbered2 with
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

let formatFile (checkOnly: bool) (path: string) : int =
    if not (System.IO.File.Exists path) then
        System.Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let lines = System.IO.File.ReadAllLines path |> Array.toList

        let header, body =
            match lines with
            | first :: rest when first.StartsWith "#!" ->
                match rest with
                | second :: tail when second.Trim() = "#loose" -> [ first; second ], tail
                | _ -> [ first ], rest
            | first :: rest when first.Trim() = "#loose" -> [ first ], rest
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
