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
    | IObj [] ->
        // an EMPTY mapping [D:yaml-empty-flow] (`{}` — kubectl's
        // resources/securityContext/…): no fields, no shape to name.
        // Mirror the empty-array posture: the opaque `Yaml` + a note,
        // never a silently-empty record.
        reg.AddNote $"an empty mapping under '{desired}' — no fields to infer, kept as opaque Yaml (edit if the real shape is known)"
        "Yaml"
    | IObj fields ->
        // an EMPTY-STRING key is unspellable BOTH ways
        // [D:infer-wire-sanitize]: a field name cannot be empty and
        // [<Wire>] refuses an empty wire key — so the key is DROPPED
        // from the draft, loudly (the readers tolerate an undeclared
        // key, so the drafted type still reads the sample)
        let spellable = fields |> List.filter (fun (k, _) -> k <> "")

        if List.length spellable < List.length fields then
            reg.AddNote
                $"an empty-string key under '{desired}' — no field name can spell it and [<Wire>] refuses an empty wire key, so the key is DROPPED from the draft (reading tolerates the extra key)"

        match spellable with
        | [] ->
            // nothing spellable remains — the opaque-Yaml posture (the
            // empty-mapping arm above); the drop note already fired
            "Yaml"
        | spellable ->
            // the collision prefix is the ENCLOSING record's stem: a `spec`
            // under `pod` disambiguates to `PodSpec`. So each field's child
            // carries THIS record's stem as its parentStem, not the field's.
            let thisStem = identStem desired

            let rendered =
                spellable
                |> List.map (fun (k, v) ->
                    let childDesired = capitalize (identStem k)
                    k, shapeOf reg childDesired thisStem v)

            reg.Claim desired parentStem rendered
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
                // field name; a seq of scalars is seq<scalar>. The
                // element's collision prefix is the record enclosing the
                // seq FIELD (parentStem), so two same-named seqs under
                // different parents disambiguate.
                let elemDesired = capitalize (singularize (identStem desired))
                let inner = shapeOf reg elemDesired parentStem elem
                $"seq<{inner}>"

// ---- the public surface -----------------------------------------------

// a wire key that a weir field name cannot spell rides the [<Wire>]
// attribute on a legal identifier [D:wire-keys]/[D:infer-wire-sanitize]:
// KEYWORD keys (`in`, `let`, `match`, … — the parser rejects EVERY
// keyword in field position, probe-pinned) AND keys that are not valid
// weir identifiers (k8s labels: `k8s-app`, `node.kubernetes.io/os`,
// `helm.sh/chart`). A key that IS a legal, non-reserved identifier
// stays verbatim — zero churn. The reserved set is the PARSER'S OWN
// (Weir.Parser.keywords), threaded in by every caller because Parser
// compiles after this file — one source, never a hand copy, no drift.

let private isIdentStart c = System.Char.IsLetter c || c = '_'
let private isIdentCont c = System.Char.IsLetterOrDigit c || c = '_'

/// a key weir can spell as a field name AS-IS: a legal identifier that
/// is not a reserved word
let private isCleanFieldName (reserved: Set<string>) (f: string) : bool =
    f.Length > 0
    && isIdentStart f[0]
    && f |> Seq.forall isIdentCont
    && not (Set.contains f reserved)

/// derive a valid weir identifier from an arbitrary wire key: camelCase
/// the alnum segments (split on every non-alnum run), lower the first
/// segment, capitalize the rest — `k8s-app`→`k8sApp`,
/// `node.kubernetes.io/os`→`nodeKubernetesIoOs`. A leading digit (or an
/// all-symbol key) gets an `f` prefix so it starts with a letter.
let private toIdent (reserved: Set<string>) (key: string) : string =
    let segs =
        key.Split([| for c in key do
                         if not (System.Char.IsLetterOrDigit c) then c |])
        |> Array.filter (fun s -> s <> "")

    let camel =
        segs
        |> Array.mapi (fun i s ->
            if i = 0 then
                string (System.Char.ToLowerInvariant s[0]) + s.Substring 1
            else
                string (System.Char.ToUpperInvariant s[0]) + s.Substring 1)
        |> String.concat ""

    let camel =
        // the reserved-word landings keep their historical spellings so
        // existing pins do not churn
        match key with
        | "type" -> "kind"
        | "to" -> "target"
        | "from" -> "source"
        | _ -> camel

    let camel =
        if camel = "" || not (isIdentStart camel[0]) then "f" + camel else camel

    // any OTHER reserved word (`in`, `let`, `match`, …) — or a camel
    // result that LANDS on one (`in-` camelizes to `in`) — takes the
    // parser's own repair spelling: `in` → `inField`
    if Set.contains camel reserved then camel + "Field" else camel

/// a record's fields as (fieldName, wireKeyOrNone, ty): clean keys keep
/// their name and carry no wire attribute; others sanitize to a deduped
/// valid identifier and carry [<Wire "key">]. Dedup is deterministic:
/// CLEAN keys (the user's own spellings) reserve their names FIRST, then
/// sanitized names take the next free `base`/`base2`/`base3`… — so a
/// sanitized key never steals a clean field's name.
let private resolveFieldNames (reserved: Set<string>) (fields: (string * string) list) : (string * string option * string) list =
    let used = System.Collections.Generic.HashSet<string>()

    // pass 1: clean keys reserve their exact names
    for (key, _) in fields do
        if isCleanFieldName reserved key then
            used.Add key |> ignore

    // pass 2: emit in order; sanitized keys disambiguate around the
    // reserved clean names and each other
    fields
    |> List.map (fun (key, ty) ->
        if isCleanFieldName reserved key then
            key, None, ty
        else
            let baseName = toIdent reserved key

            let name =
                if used.Add baseName then
                    baseName
                else
                    let mutable n = 2
                    while not (used.Add(baseName + string n)) do
                        n <- n + 1
                    baseName + string n

            name, Some key, ty)

/// the inferred DECLARATIONS + printed notes, for a sample already lowered
/// to an INode with a chosen top name. A top OBJECT is the named record;
/// a top ARRAY (or jsonl) names the ELEMENT (the user writes seq<Name>).
/// `reserved` is the parser's own keyword set (Weir.Parser.keywords) —
/// threaded in because Parser compiles after this file.
let inferDecls (reserved: Set<string>) (topName: string) (node: INode) : string list * Note list =
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
                resolveFieldNames reserved fields
                |> List.map (fun (fname, wire, t) ->
                    // a key weir cannot spell as a field name rides the
                    // wire attribute over a sanitized identifier
                    // [D:infer-wire-sanitize]
                    match wire with
                    | Some k -> $"    [<Wire \"{k}\">]\n    {fname}: {t}"
                    | None -> $"    {fname}: {t}")
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
let infer (reserved: Set<string>) (fmt: Format) (topName: string) (lines: string seq) : Result<string list * Note list, string> =
    nodeOf fmt lines |> Result.map (fun node -> inferDecls reserved topName node)

/// the composable BUILTIN body: sample lines -> declaration TEXT (the
/// notes ride as trailing `//` comment lines so the one-string return
/// stays honest outside the REPL). Raises on a parse failure, the
/// builtin-raise convention.
let inferShapeText (reserved: Set<string>) (fmt: Format) (topName: string) (lines: string seq) : string =
    match infer reserved fmt topName lines with
    | Error msg -> failwith msg
    | Ok(decls, notes) ->
        let noteLines = notes |> List.map (fun n -> $"// note: {n}")
        String.Join("\n", decls @ noteLines)
