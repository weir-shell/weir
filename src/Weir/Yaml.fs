module Weir.Yaml

// The OWNED strict-YAML subset [D:yaml-v1] — scalars, block maps, block
// sequences, `#` comments; multi-doc `---`. NOT parsed, each a teaching
// error: anchors/aliases, tags, flow style, directives, complex keys,
// block scalars (session-1 bound). The config-format spike's receipt:
// the subset is small enough to OWN — weir's own error positions, zero
// dependency bytes.

// the check-time-resolved TARGET SHAPE for `from yaml T` — eval has no
// env.Types (the [D:env-enums] precedent: pack what eval needs into the
// typed node at check)
type Shape =
    | SInt
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
    | NNull of line: int
    | NSeq of items: Node list * line: int
    | NMap of entries: (string * Node) list * line: int

let nodeLine (n: Node) =
    match n with
    | NScalar(_, _, l)
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
let private stripTrailingComment (s: string) =
    let mutable inD = false
    let mutable inS = false
    let mutable cut = -1
    let mutable i = 0

    while i < s.Length do
        let c = s[i]

        if inD then
            if c = '\\' then
                i <- i + 1
            elif c = '"' then
                inD <- false
        elif inS then
            if c = '\'' then
                inS <- false
        elif c = '"' then
            inD <- true
        elif c = '\'' then
            inS <- true
        elif c = '#' && i > 0 && s[i - 1] = ' ' && cut < 0 then
            cut <- i

        i <- i + 1

    (if cut >= 0 then s.Substring(0, cut) else s).TrimEnd()

// a scalar token: quoted (double: \" \\ \n \t unescaped; single: '' = ')
// or plain (raw, trimmed). Rejections carry the subset's teaching. The
// CORE is position-free so the yaml DISTRICT's template parser reuses it
// (one machine); parseScalar wraps it with the line prefix.
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

            while i < body.Length do
                if body[i] = '\\' && i + 1 < body.Length then
                    (match body[i + 1] with
                     | 'n' -> sb.Append '\n' |> ignore
                     | 't' -> sb.Append '\t' |> ignore
                     | '\\' -> sb.Append '\\' |> ignore
                     | '"' -> sb.Append '"' |> ignore
                     | c -> bad <- Some c)

                    i <- i + 2
                else
                    sb.Append body[i] |> ignore
                    i <- i + 1

            match bad with
            | Some c -> Error $"unsupported escape '\\{c}' (the subset takes \\\" \\\\ \\n \\t)"
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
    elif
        t = "|"
        || t = ">"
        || t.StartsWith "| "
        || t.StartsWith "> "
        || t = "|-"
        || t = ">-"
    then
        Error "block scalars are outside the yaml subset (use a quoted scalar with \\n)"
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

// ---- the block parser -----------------------------------------------------

// numbered CONTENT lines (blank and full-line-comment lines already
// dropped, trailing comments stripped) → one document node
let rec private parseBlock (lines: (int * string)[]) (start: int) (fin: int) (indent: int) : Result<Node, string> =
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
                            if inline'.Trim() = "" then
                                // the item is the nested block below (or null)
                                parseBlock lines (i + 1) j (indent + 2)
                            else
                                match splitKey no inline' with
                                | Some _ ->
                                    // compact map item: `- key: v` — the first
                                    // entry lives on this line at indent+2
                                    let shifted =
                                        Array.append
                                            [| no, String.replicate (indent + 2) " " + inline' |]
                                            lines[i + 1 .. j - 1]

                                    parseBlock shifted 0 shifted.Length (indent + 2)
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
                                        if rest.Trim() = "" then
                                            if j > i + 1 then
                                                parseBlock lines (i + 1) j (indentOf (snd lines[i + 1]))
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

            let rec build (acc: Node list) (ds: (int * string)[] list) =
                match ds with
                | [] -> Ok(List.rev acc)
                | d :: rest ->
                    if d.Length = 0 then
                        build acc rest
                    else
                        let baseIndent = indentOf (snd d[0])

                        match parseBlock d 0 d.Length baseIndent with
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
    // mis-TYPE — booleans in three casings (the 1.1 legacy set), null forms
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
          "NULL" ]

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
    || s |> Seq.exists System.Char.IsControl

/// render a STRING scalar per the quoting law [D:yaml-v1]: plain when a
/// reader cannot mis-type it, double-quoted (with \" \\ \n \t escapes)
/// otherwise — the reverse-Norway rule: `"no"`, `"007"`, `"1e5"` get
/// quotes so no YAML reader turns them into bool/number
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
             | c -> sb.Append c |> ignore)

        sb.Append '"' |> ignore
        sb.ToString()
