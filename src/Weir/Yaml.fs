module Weir.Yaml

// The OWNED strict-YAML subset [D:yaml-v1] — scalars, block maps, block
// sequences, `#` comments; multi-doc `---`; literal block scalars `|`
// and `|-` [D:block-scalars]. NOT parsed, each a teaching error:
// anchors/aliases, tags, flow style, directives, complex keys, folded
// scalars (`>`), `|+`, explicit indentation indicators. The
// config-format spike's receipt: the subset is small enough to OWN —
// weir's own error positions, zero dependency bytes.

// the check-time-resolved TARGET SHAPE for `from yaml T` — eval has no
// env.Types (the [D:env-enums] precedent: pack what eval needs into the
// typed node at check)
type Shape =
    | SInt
    | SFloat
    | SStr
    | SBool
    | SOpt of Shape
    | SRec of name: string * fields: (string * Shape) list
    | SSeq of Shape
    // seq<string * X> — an open mapping (labels/annotations)
    | SPairs of Shape

// the INTERNAL node tree — quotedness and positions ride here; the
// public `Yaml` union (prelude) carries neither, because construction
// never needs them and typed conversion does
type Node =
    | NScalar of raw: string * quoted: bool * line: int
    // blockness is quotedness's sibling [D:block-scalars]: a block scalar
    // is unambiguously a STRING (an int/bool field errors on one), and
    // the case IS the internal record of the form
    | NBlock of text: string * line: int
    | NNull of line: int
    | NSeq of items: Node list * line: int
    | NMap of entries: (string * Node) list * line: int

let nodeLine (n: Node) =
    match n with
    | NScalar(_, _, l)
    | NBlock(_, l)
    | NNull l
    | NSeq(_, l)
    | NMap(_, l) -> l

// ---- lexical helpers ------------------------------------------------------

let indentOf (s: string) =
    let mutable i = 0

    while i < s.Length && s[i] = ' ' do
        i <- i + 1

    i

// strip a trailing ` #comment` OUTSIDE quotes — YAML's own lexical rule
// (a yaml-text scanner, not a second weir-text quote machine)
// ONE machine, two faces [D:district-hash]: the plain face is YAML's
// lexical rule alone; the district face ALSO skips $(...) splice
// holes, whose interior is weir expression text (weir string rules:
// double with backslash escapes, single raw) — a `#` inside a hole or
// inside quotes is data, a whitespace-preceded `#` outside both is a
// comment.
let private commentCutAt (holes: bool) (s: string) : int =
    let mutable inD = false // yaml double quote (backslash escapes)
    let mutable inS = false // yaml single quote
    let mutable holeDepth = 0 // $(...) nesting, district face only
    let mutable wD = false // weir double-quoted string inside a hole
    let mutable wS = false // weir single-quoted raw inside a hole
    let mutable cut = -1
    let mutable i = 0

    while cut < 0 && i < s.Length do
        let c = s[i]

        if holeDepth > 0 then
            if wD then
                if c = '\\' then
                    i <- i + 1
                elif c = '"' then
                    wD <- false
            elif wS then
                if c = '\'' then
                    wS <- false
            elif c = '"' then
                wD <- true
            elif c = '\'' then
                wS <- true
            elif c = '(' then
                holeDepth <- holeDepth + 1
            elif c = ')' then
                holeDepth <- holeDepth - 1
        elif inD then
            if c = '\\' then
                i <- i + 1
            elif c = '"' then
                inD <- false
        elif inS then
            if c = '\'' then
                inS <- false
        elif holes && c = '$' && i + 1 < s.Length && s[i + 1] = '(' then
            holeDepth <- 1
            i <- i + 1
        elif c = '"' then
            inD <- true
        elif c = '\'' then
            inS <- true
        elif c = '#' && i > 0 && s[i - 1] = ' ' then
            cut <- i

        i <- i + 1

    cut

let private stripTrailingComment (s: string) =
    let cut = commentCutAt false s
    (if cut >= 0 then s.Substring(0, cut) else s).TrimEnd()

/// the district face [D:district-hash]: a whitespace-preceded `#` on a
/// district STRUCTURE line is a comment (YAML's own rule — the read
/// side already said so); quoted regions and $(...) holes are data.
/// Block-scalar content never reaches this (consumed as bytes first).
let stripDistrictComment (s: string) =
    let cut = commentCutAt true s
    (if cut >= 0 then s.Substring(0, cut) else s).TrimEnd()

// a scalar token: quoted (double: \" \\ \n \t \r \N \L \P \xNN \uNNNN
// unescaped — the emitter's own escape set, so weir reads what weir
// writes; single: '' = ') or plain (raw, trimmed). Rejections carry
// the subset's teaching. The CORE is position-free so the yaml
// DISTRICT's template parser reuses it (one machine); parseScalar
// wraps it with the line prefix.
// Ok None = null (empty); Ok (Some (text, quoted)) = a scalar.
let scalarCore (raw: string) : Result<(string * bool) option, string> =
    let t = raw.Trim()

    if t = "" then
        Ok None
    elif t.StartsWith "\"" then
        if t.Length < 2 || not (t.EndsWith "\"") then
            Error "unclosed double-quoted scalar"
        else
            let body = t.Substring(1, t.Length - 2)
            let sb = System.Text.StringBuilder()
            let mutable i = 0
            let mutable bad = None

            let hex (start: int) (len: int) =
                if start + len <= body.Length then
                    let mutable v = 0
                    let mutable ok = true

                    for k in start .. start + len - 1 do
                        let c = body[k]

                        if System.Uri.IsHexDigit c then
                            v <- v * 16 + System.Uri.FromHex c
                        else
                            ok <- false

                    if ok then Some v else None
                else
                    None

            while i < body.Length do
                if body[i] = '\\' && i + 1 < body.Length then
                    (match body[i + 1] with
                     | 'n' ->
                         sb.Append '\n' |> ignore
                         i <- i + 2
                     | 't' ->
                         sb.Append '\t' |> ignore
                         i <- i + 2
                     | 'r' ->
                         sb.Append '\r' |> ignore
                         i <- i + 2
                     | 'N' ->
                         sb.Append '\u0085' |> ignore
                         i <- i + 2
                     | 'L' ->
                         sb.Append '\u2028' |> ignore
                         i <- i + 2
                     | 'P' ->
                         sb.Append '\u2029' |> ignore
                         i <- i + 2
                     | '\\' ->
                         sb.Append '\\' |> ignore
                         i <- i + 2
                     | '"' ->
                         sb.Append '"' |> ignore
                         i <- i + 2
                     | 'x' ->
                         (match hex (i + 2) 2 with
                          | Some v ->
                              sb.Append(char v) |> ignore
                              i <- i + 4
                          | None ->
                              bad <- Some 'x'
                              i <- i + 2)
                     | 'u' ->
                         (match hex (i + 2) 4 with
                          | Some v ->
                              sb.Append(char v) |> ignore
                              i <- i + 6
                          | None ->
                              bad <- Some 'u'
                              i <- i + 2)
                     | c ->
                         bad <- Some c
                         i <- i + 2)
                else
                    sb.Append body[i] |> ignore
                    i <- i + 1

            match bad with
            | Some c ->
                Error $"unsupported escape '\\{c}' (the subset takes \\\" \\\\ \\n \\t \\r \\N \\L \\P \\xNN \\uNNNN)"
            | None -> Ok(Some(sb.ToString(), true))
    elif t.StartsWith "'" then
        if t.Length < 2 || not (t.EndsWith "'") then
            Error "unclosed single-quoted scalar"
        else
            Ok(Some(t.Substring(1, t.Length - 2).Replace("''", "'"), true))
    elif t.StartsWith "&" || t.StartsWith "*" then
        Error "anchors/aliases are outside the yaml subset (repeat the value, or build with weir)"
    elif t.StartsWith "{" || t.StartsWith "[" then
        Error "flow style is outside the yaml subset (use block maps and sequences)"
    elif t.StartsWith "!" then
        Error "tags are outside the yaml subset"
    elif t.StartsWith "|" || t.StartsWith ">" then
        // block scalars live in VALUE positions (mapping value, sequence
        // item, whole document) and are intercepted there; a header
        // reaching the scalar path is misplaced [D:block-scalars]
        Error
            "a block scalar cannot appear in this position (| and |- work as a mapping value, a sequence item, or a whole document)"
    else
        Ok(Some(t, false))

let private parseScalar (lineNo: int) (raw: string) : Result<Node, string> =
    match scalarCore raw with
    | Error msg -> Error $"line {lineNo}: {msg}"
    | Ok None -> Ok(NNull lineNo)
    | Ok(Some(text, quoted)) -> Ok(NScalar(text, quoted, lineNo))

// split `key: value` / `key:` at the first unquoted `: ` (or `:` at EOL);
// keys may be plain (k8s dotted/slashed labels) or quoted
let splitKey (lineNo: int) (s: string) : (string * string) option =
    let mutable inD = false
    let mutable inS = false
    let mutable i = 0
    let mutable found = -1

    while found < 0 && i < s.Length do
        let c = s[i]

        if inD then
            if c = '\\' then
                i <- i + 1
            elif c = '"' then
                inD <- false
        elif inS then
            if c = '\'' then
                inS <- false
        elif c = '"' && i = 0 then
            inD <- true
        elif c = '\'' && i = 0 then
            inS <- true
        elif c = ':' && (i + 1 = s.Length || s[i + 1] = ' ') then
            found <- i

        i <- i + 1

    if found < 0 then
        None
    else
        let rawKey = s.Substring(0, found).Trim()

        let key =
            if rawKey.StartsWith "\"" && rawKey.EndsWith "\"" && rawKey.Length >= 2 then
                rawKey.Substring(1, rawKey.Length - 2)
            elif rawKey.StartsWith "'" && rawKey.EndsWith "'" && rawKey.Length >= 2 then
                rawKey.Substring(1, rawKey.Length - 2).Replace("''", "'")
            else
                rawKey

        if key = "" then None else Some(key, s.Substring(found + 1))

// ---- block scalars [D:block-scalars] --------------------------------------

/// classify a value slot as a block scalar header: Ok keep? for `|`/`|-`,
/// a teaching error for the rejected forms, None for a non-header
let blockHeader (rest: string) : Result<bool, string> option =
    let t = rest.Trim()

    if t = "|" then
        Some(Ok true) // clip: exactly one trailing newline
    elif t = "|-" then
        Some(Ok false) // strip: none
    elif t = "|+" then
        Some(
            Error
                "'|+' (keep all trailing newlines) is outside the yaml subset — use | (one trailing newline) or |- (none)"
        )
    elif t.StartsWith ">" then
        Some(Error "folded block scalars (>) are outside the yaml subset — use | (literal)")
    elif t.Length > 1 && t[0] = '|' && System.Char.IsDigit t[1] then
        Some(
            Error
                "explicit indentation indicators are outside the yaml subset — content indentation is detected from the first line"
        )
    elif t.StartsWith "|" then
        Some(Error "a block scalar header takes no inline content — the content is the indented lines below")
    else
        None

/// content comes from the RAW lines (blank lines and `#`-shaped lines
/// are BYTES inside a block scalar — the filtered view already dropped
/// them), bounded by the first non-blank line at or left of the parent
/// indent. Chomping is SEMANTIC: `|` yields one trailing newline, `|-`
/// none; interior blanks become newlines; more-indented lines keep
/// their extra indentation.
let private blockScalar
    (raw: (int * string)[])
    (headerNo: int)
    (parentIndent: int)
    (keep: bool)
    : Result<Node, string> =
    let content = ResizeArray<int * string>()
    let mutable i = 0

    while i < raw.Length && fst raw[i] <= headerNo do
        i <- i + 1

    let mutable stop = false

    while not stop && i < raw.Length do
        let no, line = raw[i]

        if line.Trim() = "" then
            // a whitespace-only line is kept RAW: bytes beyond the
            // block's indentation are content (PyYAML agrees), bytes
            // at-or-below it are an empty line — extraction decides
            content.Add(no, line.TrimEnd '\r')
            i <- i + 1
        elif indentOf line > parentIndent then
            content.Add(no, line.TrimEnd '\r')
            i <- i + 1
        else
            stop <- true

    let isWsOnly (l: string) = l.Trim() = ""

    match
        content
        |> Seq.filter (fun (_, l) -> not (isWsOnly l))
        |> Seq.tryHead
        |> Option.map (snd >> indentOf)
    with
    | None -> Error $"line {headerNo}: a block scalar header needs an indented block below it"
    | Some cIndent ->
        match content |> Seq.tryFind (fun (_, l) -> not (isWsOnly l) && indentOf l < cIndent) with
        | Some(no, _) -> Error $"line {no}: this line sits left of the block scalar's content indentation"
        | None ->
            let extracted =
                content
                |> Seq.map (fun (_, l) -> if l.Length > cIndent then l.Substring cIndent else "")
                |> Seq.toArray

            // trailing EMPTY lines drop for both forms (keeping them is
            // |+'s job, rejected); a trailing whitespace line beyond the
            // indent is content and STAYS — dropping it loses bytes
            let mutable last = extracted.Length

            while last > 0 && extracted[last - 1] = "" do
                last <- last - 1

            let body = extracted[.. last - 1] |> String.concat "\n"
            Ok(NBlock((if keep then body + "\n" else body), headerNo))

/// the last content line's number, for the extent-consistency guard
let private blockLastNo (raw: (int * string)[]) (headerNo: int) (parentIndent: int) : int =
    let mutable last = headerNo
    let mutable i = 0

    while i < raw.Length && fst raw[i] <= headerNo do
        i <- i + 1

    let mutable stop = false

    while not stop && i < raw.Length do
        let no, line = raw[i]

        if line.Trim() = "" then
            i <- i + 1
        elif indentOf line > parentIndent then
            last <- no
            i <- i + 1
        else
            stop <- true

    last

// ---- the block parser -----------------------------------------------------

// numbered CONTENT lines (blank and full-line-comment lines already
// dropped, trailing comments stripped) → one document node. `raw` is
// the UNFILTERED source — block scalar content reads from it
// [D:block-scalars], because inside a block those dropped lines are bytes.
let rec private parseBlock
    (rawSrc: (int * string)[])
    (lines: (int * string)[])
    (start: int)
    (fin: int)
    (indent: int)
    : Result<Node, string> =

    // a block value, with the extent guard: a dedented `#` line inside
    // the extent would strand the deeper lines after it outside the
    // content — refuse rather than silently drop them
    let blockValue (no: int) (parentIndent: int) (keep: bool) (i: int) (j: int) : Result<Node, string> =
        let lastNo = blockLastNo rawSrc no parentIndent

        match lines[i + 1 .. j - 1] |> Array.tryFind (fun (n2, _) -> n2 > lastNo) with
        | Some(n2, _) ->
            Error
                $"line {n2}: this line is inside the block scalar's extent but outside its content (a dedented line above it ended the block)"
        | None -> blockScalar rawSrc no parentIndent keep

    if start >= fin then
        Ok(
            NNull(
                if start > 0 && start <= lines.Length then
                    fst lines[start - 1]
                else
                    0
            )
        )
    else
        let firstNo, firstRaw = lines[start]
        let firstBody = firstRaw.Substring indent

        if firstBody.StartsWith "- " || firstBody.TrimEnd() = "-" then
            // a block SEQUENCE: items at exactly this indent
            let rec items i acc =
                if i >= fin then
                    Ok(List.rev acc)
                else
                    let no, raw = lines[i]
                    let ind = indentOf raw

                    if ind < indent then
                        Ok(List.rev acc)
                    elif ind > indent then
                        Error $"line {no}: unexpected indentation (a sequence item line must start with '- ')"
                    elif not (raw.Substring(indent).StartsWith "- " || raw.Substring(indent).TrimEnd() = "-") then
                        Ok(List.rev acc)
                    else
                        // the item's content: rest of this line at VIRTUAL
                        // indent+2, plus deeper lines until the next sibling
                        let mutable j = i + 1

                        while j < fin && indentOf (snd lines[j]) > indent do
                            j <- j + 1

                        let inline' =
                            raw.Substring(indent + (if raw.Substring(indent).TrimEnd() = "-" then 1 else 2))

                        let itemR =
                            match blockHeader inline' with
                            | Some(Error msg) -> Error $"line {no}: {msg}"
                            | Some(Ok keep) -> blockValue no indent keep i j
                            | None ->

                                if inline'.Trim() = "" then
                                    // the item is the nested block below (or null)
                                    parseBlock rawSrc lines (i + 1) j (indent + 2)
                                else
                                    match splitKey no inline' with
                                    | Some _ ->
                                        // compact map item: `- key: v` — the first
                                        // entry lives on this line at indent+2
                                        let shifted =
                                            Array.append
                                                [| no, String.replicate (indent + 2) " " + inline' |]
                                                lines[i + 1 .. j - 1]

                                        parseBlock rawSrc shifted 0 shifted.Length (indent + 2)
                                    | None ->
                                        if j > i + 1 then
                                            Error $"line {no}: a scalar sequence item cannot have a nested block"
                                        else
                                            parseScalar no inline'

                        match itemR with
                        | Error e -> Error e
                        | Ok item -> items j (item :: acc)

            items start [] |> Result.map (fun its -> NSeq(its, firstNo))
        else
            match splitKey firstNo firstBody with
            | Some _ ->
                // a block MAP: entries at exactly this indent
                let rec entries i acc (seen: Set<string>) =
                    if i >= fin then
                        Ok(List.rev acc)
                    else
                        let no, raw = lines[i]
                        let ind = indentOf raw

                        if ind < indent then
                            Ok(List.rev acc)
                        elif ind > indent then
                            Error $"line {no}: unexpected indentation"
                        else
                            match splitKey no (raw.Substring indent) with
                            | None -> Error $"line {no}: expected 'key: value' in this mapping"
                            | Some(key, rest) ->
                                if seen.Contains key then
                                    Error $"line {no}: duplicate key '{key}'"
                                else
                                    let mutable j = i + 1

                                    while j < fin && indentOf (snd lines[j]) > indent do
                                        j <- j + 1

                                    let valueR =
                                        match blockHeader rest with
                                        | Some(Error msg) -> Error $"line {no}: {msg}"
                                        | Some(Ok keep) -> blockValue no indent keep i j
                                        | None ->

                                            if rest.Trim() = "" then
                                                if j > i + 1 then
                                                    parseBlock rawSrc lines (i + 1) j (indentOf (snd lines[i + 1]))
                                                else
                                                    Ok(NNull no)
                                            elif j > i + 1 then
                                                Error $"line {no}: '{key}' has both an inline value and a nested block"
                                            else
                                                parseScalar no rest

                                    match valueR with
                                    | Error e -> Error e
                                    | Ok v -> entries j ((key, v) :: acc) (seen.Add key)

                entries start [] Set.empty |> Result.map (fun es -> NMap(es, firstNo))
            | None ->
                match blockHeader firstBody with
                | Some(Error msg) -> Error $"line {firstNo}: {msg}"
                | Some(Ok keep) ->
                    // a whole-document block scalar: `|` at the doc root
                    blockValue firstNo indent keep start fin
                | None ->
                    if fin > start + 1 then
                        Error $"line {firstNo}: expected 'key:' or '- ' at this indentation"
                    else
                        parseScalar firstNo firstBody

/// parse numbered raw lines into DOCUMENTS (`---` separated, indent-0
/// separators only; a leading `---` is allowed)
let parseDocs (numbered: (int * string) list) : Result<Node list, string> =
    let content =
        numbered
        |> List.filter (fun (no, raw) ->
            let t = raw.TrimEnd()

            if t.TrimStart().StartsWith "%" then
                true // kept so the rejection below fires with its line
            else
                t <> "" && not (t.TrimStart().StartsWith "#"))
        |> List.map (fun (no, raw) -> no, stripTrailingComment raw)
        |> List.filter (fun (_, raw) -> raw.Trim() <> "")

    match content |> List.tryFind (fun (_, raw) -> raw.TrimStart().StartsWith "%") with
    | Some(no, _) -> Error $"line {no}: directives are outside the yaml subset"
    | None ->

        match
            content
            |> List.tryFind (fun (_, raw) -> raw.StartsWith "? " || raw.TrimStart().StartsWith "? ")
        with
        | Some(no, _) -> Error $"line {no}: complex keys are outside the yaml subset"
        | None ->

            // split into docs on indent-0 `---`
            let docs = ResizeArray<ResizeArray<int * string>>()
            docs.Add(ResizeArray())

            for (no, raw) in content do
                if raw.TrimEnd() = "---" then
                    if docs[docs.Count - 1].Count > 0 then
                        docs.Add(ResizeArray())
                else
                    docs[docs.Count - 1].Add((no, raw))

            let rawArr = Array.ofList numbered

            let rec build (acc: Node list) (ds: (int * string)[] list) =
                match ds with
                | [] -> Ok(List.rev acc)
                | d :: rest ->
                    if d.Length = 0 then
                        build acc rest
                    else
                        let baseIndent = indentOf (snd d[0])

                        match parseBlock rawArr d 0 d.Length baseIndent with
                        | Error e -> Error e
                        | Ok node -> build (node :: acc) rest

            build [] (docs |> Seq.map (fun d -> d.ToArray()) |> List.ofSeq)

// ---- the render-side scalar law -------------------------------------------

let private looksNumeric (s: string) =
    s.Length > 0
    && (System.Char.IsDigit s[0]
        || ((s[0] = '-' || s[0] = '+' || s[0] = '.')
            && s.Length > 1
            && System.Char.IsDigit s[1]))

let private ambiguousPlain =
    // the reverse-Norway set: a plain rendering a YAML reader would
    // mis-TYPE — booleans in three casings (the 1.1 legacy set), null
    // forms, and the float specials (.inf/.nan families) — a reader
    // types those as floats
    set
        [ "true"
          "false"
          "yes"
          "no"
          "on"
          "off"
          "null"
          "~"
          "True"
          "False"
          "Yes"
          "No"
          "On"
          "Off"
          "Null"
          "TRUE"
          "FALSE"
          "YES"
          "NO"
          "ON"
          "OFF"
          "NULL"
          ".inf"
          ".Inf"
          ".INF"
          "+.inf"
          "+.Inf"
          "+.INF"
          "-.inf"
          "-.Inf"
          "-.INF"
          ".nan"
          ".NaN"
          ".NAN" ]

let private needsQuote (s: string) =
    s = ""
    || s <> s.Trim()
    || ambiguousPlain.Contains s
    || looksNumeric s // "007", "1e5", "1.5" must not re-read as numbers
    || s.Contains '\n'
    || s.Contains ": "
    || s.EndsWith ":"
    || s.Contains " #" // a mid-line comment marker would truncate the value
    || "-?[]{},#&*!|>'\"%@`".Contains s[0]
    || (s.StartsWith "- ")
    // past Char.IsControl: U+2028/U+2029 are line breaks to a YAML
    // reader but not IsControl, so they must force quoting too
    || s
       |> Seq.exists (fun c -> System.Char.IsControl c || c = '\u2028' || c = '\u2029')

/// render a STRING scalar per the quoting law [D:yaml-v1]: plain when a
/// reader cannot mis-type it, double-quoted otherwise — the
/// reverse-Norway rule: `"no"`, `"007"`, `"1e5"` get quotes so no YAML
/// reader turns them into bool/number. Every control character and
/// unicode line break is ESCAPED inside the quotes (\r \N \L \P, \xNN
/// for the rest): a raw CR/NEL/LS in a quoted scalar is a line break
/// to a YAML reader and the value changes
let renderScalar (s: string) : string =
    if not (needsQuote s) then
        s
    else
        let sb = System.Text.StringBuilder()
        sb.Append '"' |> ignore

        for c in s do
            (match c with
             | '"' -> sb.Append "\\\"" |> ignore
             | '\\' -> sb.Append "\\\\" |> ignore
             | '\n' -> sb.Append "\\n" |> ignore
             | '\t' -> sb.Append "\\t" |> ignore
             | '\r' -> sb.Append "\\r" |> ignore
             | '\u0085' -> sb.Append "\\N" |> ignore
             | '\u2028' -> sb.Append "\\L" |> ignore
             | '\u2029' -> sb.Append "\\P" |> ignore
             // ToString, not sprintf: printf's generic specialization
             // is reflection under AOT and this arm must run there
             | c when System.Char.IsControl c ->
                 (if int c < 256 then
                      sb.Append("\\x").Append((int c).ToString "X2")
                  else
                      sb.Append("\\u").Append((int c).ToString "X4"))
                 |> ignore
             | c -> sb.Append c |> ignore)

        sb.Append '"' |> ignore
        sb.ToString()
