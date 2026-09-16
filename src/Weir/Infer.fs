module Weir.Infer

// #infer's COMPOSABLE CORE [D:repl-infer]: sample VALUE -> a set of named
// `type` declarations, the `weir add schema` category. NOTHING here
// evaluates weir or touches the session; it turns a concrete JSON/YAML
// document into DECLARATION TEXT (the REPL directive is a thin wrapper
// that also injects). check stays evaluation-free and untouched — this
// file is never on a check path.

open System

// the schema-less intermediate — the ONE shape both adapters lower into,
// so the walker and the auto-naming pass are written once. Scalars carry
// no value (inference wants the TYPE, not the datum); objects keep field
// ORDER (declaration order is wire order — the record-order law).
type INode =
    | IStr
    | IInt
    | IFloat
    | IBool
    | INull
    | IObj of (string * INode) list
    | IArr of INode list

// a printed NOTE [D:repl-infer]: the inference's honesty channel — every
// place the sample cannot decide (empty array, null field, heterogeneous
// array) surfaces here rather than guessing silently.
type Note = string

// ---- adapters: text -> INode ------------------------------------------

let rec private ofJsonElement (el: System.Text.Json.JsonElement) : INode =
    match el.ValueKind with
    | System.Text.Json.JsonValueKind.Object ->
        IObj [ for p in el.EnumerateObject() -> p.Name, ofJsonElement p.Value ]
    | System.Text.Json.JsonValueKind.Array -> IArr [ for e in el.EnumerateArray() -> ofJsonElement e ]
    | System.Text.Json.JsonValueKind.String -> IStr
    | System.Text.Json.JsonValueKind.True
    | System.Text.Json.JsonValueKind.False -> IBool
    | System.Text.Json.JsonValueKind.Null -> INull
    | System.Text.Json.JsonValueKind.Number ->
        // the adapter's own widening [D:floats-boundaries]: a decimal
        // token in this position is float, an integer token is int (the
        // raw token tells them apart, exactly as the from-json reader does)
        let raw = el.GetRawText()

        if raw.Contains '.' || raw.Contains 'e' || raw.Contains 'E' then
            IFloat
        else
            IInt
    | _ -> IStr

/// join the sample lines and parse ONE JSON document into an INode
let private jsonNode (lines: string seq) : Result<INode, string> =
    let text = String.Join("\n", lines)

    try
        use doc = System.Text.Json.JsonDocument.Parse text
        Ok(ofJsonElement doc.RootElement)
    with ex ->
        Error $"#infer: the sample is not valid JSON: {ex.Message}"

/// NDJSON — one JSON document per non-blank line, gathered into an array
let private jsonlNode (lines: string seq) : Result<INode, string> =
    let docs = ResizeArray<INode>()
    let mutable err = None

    for line in lines do
        if err.IsNone && line.Trim() <> "" then
            try
                use doc = System.Text.Json.JsonDocument.Parse line
                docs.Add(ofJsonElement doc.RootElement)
            with ex ->
                err <- Some $"#infer: a jsonl line is not valid JSON: {ex.Message}"

    match err with
    | Some e -> Error e
    | None -> Ok(IArr(List.ofSeq docs))

// classify an UNQUOTED yaml scalar (quoted is always a string) — the
// yaml number/bool boundary, integer-or-float by the token's shape
let private classifyYamlScalar (text: string) : INode =
    let t = text.Trim()

    if t = "true" || t = "false" then
        IBool
    elif System.Text.RegularExpressions.Regex.IsMatch(t, "^-?[0-9]+$") then
        IInt
    elif System.Text.RegularExpressions.Regex.IsMatch(t, "^-?[0-9]+\.[0-9]+([eE][-+]?[0-9]+)?$") then
        IFloat
    elif System.Text.RegularExpressions.Regex.IsMatch(t, "^-?[0-9]+[eE][-+]?[0-9]+$") then
        IFloat
    else
        IStr

let rec private ofYamlNode (n: Yaml.Node) : INode =
    match n with
    | Yaml.NNull _ -> INull
    | Yaml.NBlock _ -> IStr
    | Yaml.NScalar(text, quoted, _) -> if quoted then IStr else classifyYamlScalar text
    | Yaml.NSeq(items, _) -> IArr(items |> List.map ofYamlNode)
    | Yaml.NMap(entries, _) -> IObj(entries |> List.map (fun (k, v) -> k, ofYamlNode v))

/// parse ONE yaml document into an INode
let private yamlNode (lines: string seq) : Result<INode, string> =
    let numbered = lines |> Seq.mapi (fun i l -> i + 1, l) |> List.ofSeq

    match Yaml.parseDocs numbered with
    | Error msg -> Error $"#infer: the sample is not valid YAML: {msg}"
    | Ok [] -> Error "#infer: the yaml sample is empty"
    | Ok(doc :: _) -> Ok(ofYamlNode doc)

// ---- auto-naming helpers ----------------------------------------------

let private capitalize (s: string) : string =
    if s = "" then s
    else string (Char.ToUpperInvariant s[0]) + s.Substring 1

// sanitize a wire key into an identifier stem: keep letters/digits/_,
// split on the rest, camel-join. `metadata.name` / `kube-system` become
// legible type stems; a leading digit is dropped from the head.
let private identStem (key: string) : string =
    let parts =
        System.Text.RegularExpressions.Regex.Split(key, "[^A-Za-z0-9]+")
        |> Array.filter (fun p -> p <> "")

    let joined = parts |> Array.map capitalize |> String.concat ""
    let cleaned = String(joined |> Seq.filter (fun c -> Char.IsLetterOrDigit c || c = '_') |> Seq.toArray)

    match cleaned with
    | "" -> "Field"
    | c when Char.IsDigit c[0] -> "N" + c
    | c -> c

/// BEST-EFFORT singularisation [D:repl-infer]: the seq-element namer.
/// Conservative — when a word does not obviously pluralise (data, status,
/// metadata, series) it is LEFT AS-IS, so the fallback is the field name.
let private singularize (s: string) : string =
    let lower = s.ToLowerInvariant()

    // words that are already singular OR do not pluralise the -s way
    let invariant = set [ "data"; "status"; "metadata"; "series"; "info"; "spec"; "news" ]

    if Set.contains lower invariant then
        s
    elif s.EndsWith "ies" && s.Length > 3 then
        s.Substring(0, s.Length - 3) + "y"
    elif s.EndsWith "ses" && s.Length > 3 then
        s.Substring(0, s.Length - 2)
    elif s.EndsWith "s" && not (s.EndsWith "ss") && s.Length > 1 then
        s.Substring(0, s.Length - 1)
    else
        s

// ---- the walker + naming pass -----------------------------------------
// The registry disambiguates: a desired name maps to the shapes claimed
// under it. Same field-name SAME shape dedups to one type; same name
// DIFFERENT shape parent-prefixes (PodSpec / ContainerSpec). Children
// resolve BEFORE their parent references them (bottom-up), so a field's
// rendered type already carries the child's final name.

type private Registry() =
    // desiredName -> list of (structural signature, finalName, fields)
    let claimed = System.Collections.Generic.Dictionary<string, ResizeArray<string * string * (string * string) list>>()
    // emission order preserved: finalName -> fields (rendered), in insert order
    let order = ResizeArray<string * (string * string) list>()
    let notes = ResizeArray<Note>()

    member _.Notes = List.ofSeq notes
    member _.AddNote(n: Note) = if not (notes.Contains n) then notes.Add n
    member _.Decls = List.ofSeq order

    /// claim a record type for an object with `fields` (rendered field
    /// types), desiring `desired`, whose parent stem is `parentStem`.
    /// Returns the FINAL type name to reference.
    member _.Claim (desired: string) (parentStem: string) (fields: (string * string) list) : string =
        let sign =
            fields
            |> List.sortBy fst
            |> List.map (fun (n, t) -> $"{n}:{t}")
            |> String.concat ";"

        match claimed.TryGetValue desired with
        | true, bucket ->
            match bucket |> Seq.tryFind (fun (s, _, _) -> s = sign) with
            | Some(_, fin, _) -> fin // DEDUP: same name, same shape
            | None ->
                // COLLISION: same name, different shape — parent-prefix
                let candidate =
                    let pfx = capitalize parentStem + desired
                    if pfx <> desired && not (bucket |> Seq.exists (fun (_, f, _) -> f = pfx)) then pfx
                    else desired + string (bucket.Count + 1)

                bucket.Add(sign, candidate, fields)
                order.Add(candidate, fields)
                candidate
        | _ ->
            let bucket = ResizeArray<_>()
            bucket.Add(sign, desired, fields)
            claimed[desired] <- bucket
            order.Add(desired, fields)
            desired

// the first element that is not null decides an array's element shape;
// a following DIFFERENT object shape flags heterogeneity (verify note)
let private firstNonNull (xs: INode list) : INode option =
    xs |> List.tryFind (fun x -> x <> INull)

let rec private shapeOf (reg: Registry) (desired: string) (parentStem: string) (node: INode) : string =
    match node with
    | IStr -> "string"
    | IInt -> "int"
    | IFloat -> "float"
    | IBool -> "bool"
    | INull ->
        // a null field cannot be inferred — Option<string> + a note
        reg.AddNote $"a null value under '{desired}' — inferred Option<string> (edit if the real type is known)"
        "Option<string>"
    | IObj fields ->
        let rendered =
            fields
            |> List.map (fun (k, v) ->
                let childDesired = capitalize (identStem k)
                let childStem = identStem k
                k, shapeOf reg childDesired childStem v)

        let finalName = reg.Claim desired parentStem rendered
        finalName
    | IArr items ->
        match items with
        | [] ->
            reg.AddNote $"an empty array under '{desired}' — element type unknown, inferred seq<string> (edit if known)"
            "seq<string>"
        | _ ->
            // heterogeneity: object elements whose field SETS differ
            let objSigs =
                items
                |> List.choose (fun x ->
                    match x with
                    | IObj fs -> Some(fs |> List.map fst |> List.sort)
                    | _ -> None)
                |> List.distinct

            if objSigs.Length > 1 then
                reg.AddNote
                    $"a heterogeneous array under '{desired}' — inferred from the first element only; verify the rest"

            match firstNonNull items with
            | None ->
                reg.AddNote $"an all-null array under '{desired}' — inferred seq<Option<string>> (edit if known)"
                "seq<Option<string>>"
            | Some elem ->
                // the element of a seq-of-record takes the SINGULARISED
                // field name; a seq of scalars is seq<scalar>
                let elemDesired = capitalize (singularize (identStem desired))
                let elemStem = singularize (identStem desired)
                let inner = shapeOf reg elemDesired elemStem elem
                $"seq<{inner}>"

// ---- the public surface -----------------------------------------------

// a wire key that is a reserved word rides the [<Wire>] attribute on a
// legal field name [D:wire-keys]; every other json/yaml key is already a
// legal field name and stays verbatim
let private reservedWireKeys = set [ "type"; "to"; "from" ]

let private sanitizeFieldName (f: string) : string =
    match f with
    | "type" -> "kind"
    | "to" -> "target"
    | "from" -> "source"
    | other -> other

/// the inferred DECLARATIONS + printed notes, for a sample already lowered
/// to an INode with a chosen top name. A top OBJECT is the named record;
/// a top ARRAY (or jsonl) names the ELEMENT (the user writes seq<Name>).
let inferDecls (topName: string) (node: INode) : string list * Note list =
    let reg = Registry()

    (match node with
     | IArr items ->
         reg.AddNote $"the sample's top level is an array — '{topName}' names the element; read it as 'seq<{topName}>'"

         match firstNonNull items with
         | None -> reg.AddNote "an empty top-level array — element type unknown"
         | Some elem -> shapeOf reg topName (identStem topName) elem |> ignore
     | IObj _ -> shapeOf reg topName (identStem topName) node |> ignore
     | _ ->
         // a top-level scalar has no record to declare
         reg.AddNote "the sample's top level is a scalar — nothing to name; #infer drafts record shapes")

    // render each claimed record as a `type` decl, house style
    let decls =
        reg.Decls
        |> List.map (fun (name, fields) ->
            let body =
                fields
                |> List.map (fun (f, t) ->
                    // a reserved word key rides the wire attribute
                    if Set.contains f reservedWireKeys then
                        $"    [<Wire \"{f}\">]\n    {sanitizeFieldName f}: {t}"
                    else
                        $"    {f}: {t}")
                |> String.concat "\n"

            $"type {name} = {{\n{body}\n}}")

    decls, reg.Notes

// ---- format dispatch --------------------------------------------------

type Format =
    | Json
    | Jsonl
    | Yaml

let parseFormat (s: string) : Result<Format, string> =
    match s.Trim() with
    | "json" -> Ok Json
    | "jsonl" -> Ok Jsonl
    | "yaml" -> Ok Yaml
    | other -> Error $"#infer: unknown format '{other}' — use json, jsonl, or yaml"

let nodeOf (fmt: Format) (lines: string seq) : Result<INode, string> =
    match fmt with
    | Json -> jsonNode lines
    | Jsonl -> jsonlNode lines
    | Yaml -> yamlNode lines

/// the whole pipeline for a chosen format + name: lines -> decls + notes
let infer (fmt: Format) (topName: string) (lines: string seq) : Result<string list * Note list, string> =
    nodeOf fmt lines |> Result.map (fun node -> inferDecls topName node)

/// the composable BUILTIN body: sample lines -> declaration TEXT (the
/// notes ride as trailing `//` comment lines so the one-string return
/// stays honest outside the REPL). Raises on a parse failure, the
/// builtin-raise convention.
let inferShapeText (fmt: Format) (topName: string) (lines: string seq) : string =
    match infer fmt topName lines with
    | Error msg -> failwith msg
    | Ok(decls, notes) ->
        let noteLines = notes |> List.map (fun n -> $"// note: {n}")
        String.Join("\n", decls @ noteLines)
