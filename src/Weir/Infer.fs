module Weir.Infer

// The composable core of #infer [D:repl-infer]: a sample value becomes
// a set of named `type` declarations (the `weir add schema` category).
// Nothing here evaluates weir or touches the session; a concrete
// JSON/YAML document is turned into declaration text (the REPL
// directive is a thin wrapper that also injects). This file is never
// on a check path, so check stays evaluation-free.

open System

// The schema-less intermediate: the one shape both adapters lower
// into, so the walker and the auto-naming pass are written once.
// Scalars carry no value (inference wants the type, not the datum);
// objects keep field order (declaration order is wire order). IOpt and
// IMap are merge verdicts [D:repl-infer], produced only by the
// array-element merge and the open-map detection below, never by the
// adapters: a key absent in some elements is IOpt (drafts Option), an
// object whose keys are data is IMap (drafts the mapping
// seq<string * V>).
type INode =
    | IStr
    | IInt
    | IFloat
    | IBool
    | INull
    | IObj of (string * INode) list
    | IArr of INode list
    | IOpt of INode
    | IMap of INode

// A printed note [D:repl-infer]: every place the sample cannot decide
// (empty array, null field, heterogeneous array) surfaces here rather
// than guessing silently.
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
        // number widening [D:floats-boundaries]: a decimal token is
        // float, an integer token is int — the raw token tells them
        // apart, exactly as the from-json reader does
        let raw = el.GetRawText()

        if raw.Contains '.' || raw.Contains 'e' || raw.Contains 'E' then
            IFloat
        else
            IInt
    | _ -> IStr

/// join the sample lines and parse one JSON document into an INode
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

// classify an unquoted yaml scalar (quoted is always a string) — the
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

/// parse one yaml document into an INode
let private yamlNode (lines: string seq) : Result<INode, string> =
    let numbered = lines |> Seq.mapi (fun i l -> i + 1, l) |> List.ofSeq

    match Yaml.parseDocs numbered with
    | Error msg -> Error $"#infer: the sample is not valid YAML: {msg}"
    | Ok [] -> Error "#infer: the yaml sample is empty"
    | Ok(doc :: _) -> Ok(ofYamlNode doc)

// ---- auto-naming helpers ----------------------------------------------

let capitalize (s: string) : string =
    if s = "" then s
    else string (Char.ToUpperInvariant s[0]) + s.Substring 1

// sanitize a wire key into an identifier stem: keep letters/digits/_,
// split on the rest, camel-join. `metadata.name` / `kube-system`
// become legible type stems; a head that starts with a digit is
// repaired.
let identStem (key: string) : string =
    let parts =
        System.Text.RegularExpressions.Regex.Split(key, "[^A-Za-z0-9]+")
        |> Array.filter (fun p -> p <> "")

    let joined = parts |> Array.map capitalize |> String.concat ""
    let cleaned = String(joined |> Seq.filter (fun c -> Char.IsLetterOrDigit c || c = '_') |> Seq.toArray)

    match cleaned with
    | "" -> "Field"
    | c when Char.IsDigit c[0] -> "N" + c
    | c -> c

/// Best-effort singularisation [D:repl-infer] for naming seq elements.
/// Conservative: a word that does not obviously pluralise (data,
/// status, metadata, series) is left as-is, so the fallback is the
/// field name.
let singularize (s: string) : string =
    let lower = s.ToLowerInvariant()

    // words that are already singular or do not pluralise the -s way
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

// ---- the taken-name guard ---------------------------------------------
// A drafted type must not land on a type name the session already
// resolves [D:repl-infer]: `type Secret = { … }` injects, but a
// `secret: Secret` annotation still resolves to the primitive (the
// parser eats Secret/Duration/Instant/Size/Bytes before any declared
// type), and a registered builtin name (Yaml, Option, Retry, …)
// refuses at checkDecl — either way the draft is shadowed or dead.
// Callers thread the live name set (env Types keys,
// Check.builtinTypeNames — both compile after this file, as
// Parser.keywords does); the primitive spellings are completed here
// from the renderer (Types.formatTy), so no name is hand-spelled.

/// the taken type-name set for a draft: the caller's live names
/// (session Types keys and/or the registered builtin nominals)
/// completed with the parser's primitive spellings
let takenTypeNames (liveNames: string seq) : Set<string> =
    [ Types.TDur; Types.TInstant; Types.TSize; Types.TBytes; Types.TSecret ]
    |> List.map Types.formatTy
    |> Set.ofList
    |> Set.union (Set.ofSeq liveNames)

// ---- the walker + naming pass -----------------------------------------
// The registry disambiguates: a desired name maps to the shapes
// claimed under it. The same name with the same shape dedups to one
// type; the same name with a different shape — or a name the
// environment already holds (`taken`) — gets a parent prefix
// (PodSpec / ContainerSpec, VolumeSecret). Children resolve before
// their parent references them (bottom-up), so a field's rendered
// type already carries the child's final name.

type Registry(taken: Set<string>) =
    // desiredName -> list of (structural signature, finalName, fields)
    let claimed = System.Collections.Generic.Dictionary<string, ResizeArray<string * string * (string * string) list>>()
    // emission order preserved: finalName -> fields (rendered), in insert order
    let order = ResizeArray<string * (string * string) list>()
    let notes = ResizeArray<Note>()

    member _.Notes = List.ofSeq notes
    member _.AddNote(n: Note) = if not (notes.Contains n) then notes.Add n
    member _.Decls = List.ofSeq order

    /// claim a record type for an object with `fields` (rendered field
    /// types), desiring `desired` (derived from the wire key `srcKey`),
    /// whose parent stem is `parentStem`. Returns the final type name
    /// to reference.
    member this.Claim (desired: string) (srcKey: string) (parentStem: string) (fields: (string * string) list) : string =
        let sign =
            fields
            |> List.sortBy fst
            |> List.map (fun (n, t) -> $"{n}:{t}")
            |> String.concat ";"

        let bucket =
            match claimed.TryGetValue desired with
            | true, b -> b
            | _ ->
                let b = ResizeArray<_>()
                claimed[desired] <- b
                b

        match bucket |> Seq.tryFind (fun (s, _, _) -> s = sign) with
        | Some(_, fin, _) -> fin // DEDUP: same name, same shape
        | None ->
            // a name is unusable when a sibling claim or the
            // environment holds it — a taken landing would be shadowed
            let inUse n =
                Set.contains n taken || bucket |> Seq.exists (fun (_, f, _) -> f = n)

            let candidate =
                if bucket.Count = 0 && not (Set.contains desired taken) then
                    desired
                else
                    // collision (same name different shape, or taken
                    // by the environment): parent-prefix, then a
                    // numeric fallback
                    let pfx = capitalize parentStem + desired

                    if pfx <> desired && not (inUse pfx) then
                        pfx
                    else
                        let mutable n = max 2 (bucket.Count + 1)

                        while inUse (desired + string n) do
                            n <- n + 1

                        desired + string n

            if Set.contains desired taken then
                // the never-guess-silently convention [D:repl-infer]
                this.AddNote $"'{srcKey}' would shadow the existing type '{desired}' — drafted as '{candidate}'"

            bucket.Add(sign, candidate, fields)
            order.Add(candidate, fields)
            candidate

let private isIdentStart c = System.Char.IsLetter c || c = '_'
let private isIdentCont c = System.Char.IsLetterOrDigit c || c = '_'

// a key with identifier shape — grammar only, so a reserved word still
// counts (`type:` is a schema key on real wires; keywords never vote
// an object into a mapping) [D:repl-infer]
let private isIdentShaped (k: string) : bool =
    k.Length > 0 && isIdentStart k[0] && k |> Seq.forall isIdentCont

/// Option is a merge verdict, never nested and never over null: an
/// absent-or-null key wraps once; a further merge folds into the wrap
let private iopt (n: INode) : INode =
    match n with
    | INull -> INull
    | IOpt _ -> n
    | _ -> IOpt n

// Open-map detection [D:repl-infer], value half: the object's entries
// carry one value shape — every value structurally identical, none
// null, at least one entry. Without a single value type there is no V
// to write in seq<string * V>, so the object stays a record.
let private uniformValue (vals: INode list) : INode option =
    match vals |> List.distinct with
    | [ v ] when v <> INull -> Some v
    | _ -> None

// Open-map detection [D:repl-infer], key half (a): the keys look like
// data — a majority (strictly more than half) are not
// identifier-shaped, i.e. would need [<Wire>] sanitization (k8s
// labels/annotations: dots, slashes, dashes). Both halves must hold.
// The other trigger, (b), lives in the merge: sibling elements of one
// array carrying different key sets for the same object. An object
// with identifier-shaped keys identical across elements is schema,
// not a mapping.
let private detectOpenMap (fields: (string * INode) list) : INode option =
    match uniformValue (fields |> List.map snd) with
    | Some v when 2 * (fields |> List.filter (fst >> isIdentShaped >> not) |> List.length) > List.length fields -> Some v
    | _ -> None

// ---- the array-element merge [D:repl-infer] ---------------------------
// The element type is the union of every element's shape: object key
// sets union (a key absent in some elements drafts Option); a key
// whose sibling key sets differ under one uniform value shape is an
// open map (detection half b); a genuine type conflict keeps the first
// element's shape with a printed verify note rather than a silent
// guess. `path` is the wire-key path from the array's own key down —
// the address the notes use.

let rec private mergeTwo (note: Note -> unit) (path: string) (a: INode) (b: INode) : INode =
    match a, b with
    | a, b when a = b -> a
    | INull, b -> iopt b
    | a, INull -> iopt a
    | IOpt x, IOpt y -> iopt (mergeTwo note path x y)
    | IOpt x, y -> iopt (mergeTwo note path x y)
    | x, IOpt y -> iopt (mergeTwo note path x y)
    | IObj xs, IObj ys -> mergeObjs note path xs ys
    // arrays pool their elements; the enclosing seq walk merges the pool
    | IArr xs, IArr ys -> IArr(xs @ ys)
    | a, _ ->
        note $"a heterogeneous array at '{path}' — kept the first element's type; verify the rest"
        a

and private mergeObjs (note: Note -> unit) (path: string) (xs: (string * INode) list) (ys: (string * INode) list) : INode =
    // Always the record union here; the open-map verdict (detection
    // half b) is deferred to openMaps over the fully-merged shape
    // [D:repl-infer]. Value uniformity must hold across all siblings,
    // not one pair — a pairwise verdict can commit to a map from a
    // coincidentally-uniform early pair, then absorb a later
    // conflicting value first-wins (a k8s securityContext with a bool
    // field then an int field drafted Map<string, bool> and rejected
    // the int). The record merge: union of keys in first-seen order; a
    // shared key merges recursively, a one-sided key drafts Option
    // (which is how differing key sets read downstream).
    let kx = xs |> List.map fst |> Set.ofList
    let ym = Map.ofList ys

    let fromX =
        xs
        |> List.map (fun (k, xv) ->
            match Map.tryFind k ym with
            | Some yv -> k, mergeTwo note $"{path}.{k}" xv yv
            | None -> k, iopt xv)

    let fromY =
        ys
        |> List.filter (fun (k, _) -> not (Set.contains k kx))
        |> List.map (fun (k, yv) -> k, iopt yv)

    IObj(fromX @ fromY)

// The open-map verdict, deferred to the fully-merged shape (detection
// half b) [D:repl-infer]: an object whose keys differ across the
// array's elements — every field optional after the union, none
// shared — and whose values share one shape has data keys, drafted
// seq<string * V>. Deciding here rather than in the pairwise merge is
// what lets value uniformity be judged over all siblings (a
// securityContext's bool+int values are not uniform, so it stays a
// record; a ConfigMap data's all-string values are). Walks bottom-up
// so a nested map is settled before its parent is judged.
let rec private openMaps (note: Note -> unit) (path: string) (node: INode) : INode =
    match node with
    | IObj fields ->
        let fields' = fields |> List.map (fun (k, v) -> k, openMaps note $"{path}.{k}" v)
        let allOptional = fields' |> List.forall (fun (_, v) -> (match v with IOpt _ -> true | _ -> false))

        let bare =
            fields' |> List.map (fun (_, v) -> (match v with IOpt x -> x | x -> x))

        match (if allOptional && List.length fields' > 1 then uniformValue bare else None) with
        | Some v ->
            note $"'{path}' carries different keys across the array's elements — its keys are data, drafted as an open mapping seq<string * _>"
            IMap v
        | None -> IObj fields'
    | IArr items -> IArr(items |> List.map (openMaps note path))
    | IOpt x -> iopt (openMaps note path x)
    | IMap v -> IMap(openMaps note path v)
    | _ -> node

/// merge every non-null element of an array into one element shape
/// (null elements never decide a shape, consistent with the adapters),
/// then settle open maps over the full result
let private mergeElems (note: Note -> unit) (path: string) (items: INode list) : INode option =
    match items |> List.filter ((<>) INull) with
    | [] -> None
    | first :: rest -> Some(rest |> List.fold (mergeTwo note path) first |> openMaps note path)

// `srcKey` is the wire key (or top name) that produced `desired` — the
// shadow note names the user's own spelling, not the derived stem
let rec private shapeOf (reg: Registry) (desired: string) (srcKey: string) (parentStem: string) (node: INode) : string =
    match node with
    | IStr -> "string"
    | IInt -> "int"
    | IFloat -> "float"
    | IBool -> "bool"
    | INull ->
        // a null field cannot be inferred — Option<string> + a note
        reg.AddNote $"a null value under '{desired}' — inferred Option<string> (edit if the real type is known)"
        "Option<string>"
    | IOpt inner ->
        // a merge verdict: the key is absent (or null) in some of the
        // array's elements [D:repl-infer]
        $"Option<{shapeOf reg desired srcKey parentStem inner}>"
    | IMap v ->
        // a merge verdict (detection half b) — the note fired at the
        // merge; here the value shape renders [D:repl-infer]
        $"seq<string * {shapeOf reg (capitalize (singularize (identStem desired))) srcKey parentStem v}>"
    | IObj [] ->
        // an empty mapping [D:yaml-empty-flow] (`{}` — kubectl's
        // resources/securityContext/…): an open map with zero entries.
        // No evidence for V, so string, with a note (as for empty
        // arrays); the mapping shape reads on both wire boundaries,
        // which an opaque Yaml field does not
        reg.AddNote $"an empty mapping under '{desired}' — no entries to infer, drafted as seq<string * string> (edit if the real shape is known)"
        "seq<string * string>"
    | IObj fields ->
        match detectOpenMap fields with
        | Some v ->
            // open-map detection half (a) [D:repl-infer]: one value
            // shape plus a majority of non-identifier keys — the keys
            // are data (k8s labels/annotations), so the draft is the
            // mapping, not a record full of [<Wire>]; mapping keys
            // need no sanitizing, an empty-string key included
            reg.AddNote $"'{srcKey}' has mostly non-identifier keys — its keys are data, drafted as an open mapping seq<string * _>"
            $"seq<string * {shapeOf reg (capitalize (singularize (identStem desired))) srcKey parentStem v}>"
        | None ->
            // an empty-string key is unspellable either way
            // [D:infer-wire-sanitize]: a field name cannot be empty
            // and [<Wire>] refuses an empty wire key — so the key is
            // dropped from the draft, with a note (the readers
            // tolerate an undeclared key, so the drafted type still
            // reads the sample)
            let spellable = fields |> List.filter (fun (k, _) -> k <> "")

            if List.length spellable < List.length fields then
                reg.AddNote
                    $"an empty-string key under '{desired}' — no field name can spell it and [<Wire>] refuses an empty wire key, so the key is DROPPED from the draft (reading tolerates the extra key)"

            match spellable with
            | [] ->
                // nothing spellable remains — fall back to opaque
                // Yaml; the drop note already fired
                "Yaml"
            | spellable ->
                // the collision prefix is the enclosing record's stem
                // (a `spec` under `pod` disambiguates to `PodSpec`),
                // so each field's child carries this record's stem as
                // its parentStem, not the field's
                let thisStem = identStem desired

                let rendered =
                    spellable
                    |> List.map (fun (k, v) ->
                        let childDesired = capitalize (identStem k)
                        k, shapeOf reg childDesired k thisStem v)

                reg.Claim desired srcKey parentStem rendered
    | IArr items ->
        match items with
        | [] ->
            reg.AddNote $"an empty array under '{desired}' — element type unknown, inferred seq<string> (edit if known)"
            "seq<string>"
        | _ ->
            // array elements merge [D:repl-infer]: the element type is
            // the union of every element's shape — absent keys draft
            // Option, conflicts keep the first with a verify note
            match mergeElems reg.AddNote srcKey items with
            | None ->
                reg.AddNote $"an all-null array under '{desired}' — inferred seq<Option<string>> (edit if known)"
                "seq<Option<string>>"
            | Some elem ->
                // the element of a seq-of-record takes the singularised
                // field name; a seq of scalars is seq<scalar>. The
                // element's collision prefix is the record enclosing
                // the seq field (parentStem), so two same-named seqs
                // under different parents disambiguate.
                let elemDesired = capitalize (singularize (identStem desired))
                let inner = shapeOf reg elemDesired srcKey parentStem elem
                $"seq<{inner}>"

// ---- the public surface -----------------------------------------------

// a wire key that a weir field name cannot spell rides the [<Wire>]
// attribute on a legal identifier [D:wire-keys]/[D:infer-wire-sanitize]:
// keyword keys (`in`, `let`, `match`, … — the parser rejects every
// keyword in field position) and keys that are not valid weir
// identifiers (k8s labels: `k8s-app`, `node.kubernetes.io/os`,
// `helm.sh/chart`). A key that is a legal, non-reserved identifier
// stays verbatim — zero churn. The reserved set is the parser's own
// (Weir.Parser.keywords), threaded in by every caller because Parser
// compiles after this file — one source, no hand copy to drift.

/// a key weir can spell as a field name as-is: a legal identifier that
/// is not a reserved word
let private isCleanFieldName (reserved: Set<string>) (f: string) : bool =
    isIdentShaped f && not (Set.contains f reserved)

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

    // any other reserved word (`in`, `let`, `match`, …) — or a camel
    // result that lands on one (`in-` camelizes to `in`) — takes the
    // parser's repair spelling: `in` → `inField`
    if Set.contains camel reserved then camel + "Field" else camel

/// a record's fields as (fieldName, wireKeyOrNone, ty): clean keys keep
/// their name and carry no wire attribute; others sanitize to a deduped
/// valid identifier and carry [<Wire "key">]. Dedup is deterministic:
/// clean keys (the user's own spellings) reserve their names first,
/// then sanitized names take the next free `base`/`base2`/`base3`… —
/// so a sanitized key never steals a clean field's name.
let resolveFieldNames (reserved: Set<string>) (fields: (string * string) list) : (string * string option * string) list =
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

/// render claimed records as `type` decls, house style — the single
/// emitter tail, shared by #infer and the schema→types generator
/// [D:schema-types] (field names ride the Wire sanitizer either way)
let renderDecls (reserved: Set<string>) (decls: (string * (string * string) list) list) : string list =
    decls
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

/// the inferred declarations plus printed notes, for a sample already
/// lowered to an INode with a chosen top name. A top object is the
/// named record; a top array (or jsonl) names the element (the user
/// writes seq<Name>). `reserved` is the parser's own keyword set
/// (Weir.Parser.keywords); `taken` is the in-scope type-name set
/// (takenTypeNames over the live env) that derived names must dodge —
/// both threaded in because their sources compile after this file.
let inferDecls (reserved: Set<string>) (taken: Set<string>) (topName: string) (node: INode) : string list * Note list =
    // the top name is the caller's choice, exempt from the guard: the
    // REPL refuses a builtin 'as'-name outright, and re-inferring under
    // a session name re-declares it (the REPL's redeclare semantics)
    let reg = Registry(Set.remove topName taken)

    (match node with
     | IArr items ->
         match mergeElems reg.AddNote topName items with
         | None ->
             reg.AddNote
                 $"the sample's top level is an array — '{topName}' names the element; read it as 'seq<{topName}>'"

             reg.AddNote "an empty top-level array — element type unknown"
         | Some elem ->
             let ty = shapeOf reg topName topName (identStem topName) elem

             // a top-level array of open mappings claims no record — the
             // read spelling is the note's job [D:repl-infer]
             if ty.StartsWith "seq<string * " then
                 reg.AddNote $"the sample's top level is an array of open mappings — nothing to name; read it as 'seq<{ty}>'"
             else
                 reg.AddNote
                     $"the sample's top level is an array — '{topName}' names the element; read it as 'seq<{topName}>'"
     | IObj _ ->
         let ty = shapeOf reg topName topName (identStem topName) node

         // a top-level object detected as an open map claims no record —
         // its keys are data, so there is nothing to name [D:repl-infer]
         if ty.StartsWith "seq<string * " then
             reg.AddNote $"the sample's top level is an open mapping — nothing to name; read it as '{ty}'"
     | _ ->
         // a top-level scalar has no record to declare
         reg.AddNote "the sample's top level is a scalar — nothing to name; #infer drafts record shapes")

    renderDecls reserved reg.Decls, reg.Notes

// ---- the aligned-table drafting arm [D:from-table] --------------------
// A table never lowers to INode: Option-ness is a per-column merge
// over every row (any empty/`<none>` cell), which the first-element
// walker cannot see — so the column scan lives here and Table.fs owns
// the text model (columns, cells, match keys).

/// derive a field name from a table header: split on non-alnum runs,
/// lowercase the all-caps segments (headers are usually uppercase),
/// camel-join — `NAME`→name, `POD-TEMPLATE-HASH`→podTemplateHash,
/// `CONTAINER ID`→containerId. Reserved-word landings keep toIdent's
/// historical spellings (`TYPE`→kind); the rest take the `Field`
/// suffix repair.
let private tableFieldName (reserved: Set<string>) (header: string) : string =
    let segs =
        System.Text.RegularExpressions.Regex.Split(header, "[^A-Za-z0-9]+")
        |> Array.filter (fun s -> s <> "")

    let camel =
        segs
        |> Array.map (fun s ->
            if s |> Seq.forall (fun c -> not (Char.IsLower c)) then
                s.ToLowerInvariant()
            else
                s)
        |> Array.mapi (fun i s ->
            if i = 0 then
                string (Char.ToLowerInvariant s[0]) + s.Substring 1
            else
                string (Char.ToUpperInvariant s[0]) + s.Substring 1)
        |> String.concat ""

    let camel =
        match camel with
        | "type" -> "kind"
        | "to" -> "target"
        | "from" -> "source"
        | c -> c

    let camel =
        if camel = "" || not (isIdentStart camel[0]) then "f" + camel else camel

    if Set.contains camel reserved then camel + "Field" else camel

let private tableIntRx = System.Text.RegularExpressions.Regex "^-?[0-9]+$"

let private tableFloatRx =
    System.Text.RegularExpressions.Regex "^-?[0-9]+\.[0-9]+([eE][-+]?[0-9]+)?$|^-?[0-9]+[eE][-+]?[0-9]+$"

/// draft the row record from a table sample [D:from-table]: per-column
/// token scan over the data rows (all-int → int, else float/bool by
/// token, else string); any empty/`<none>` cell → Option<T> plus a
/// note; a note says the value reads as seq<Name> (as for a bare
/// top-level array). The `as` name is the caller's own — the
/// taken-name guard is the caller's refusal, exactly as for
/// inferDecls' top name.
let private tableDecls (reserved: Set<string>) (topName: string) (lines: string seq) : Result<string list * Note list, string> =
    let numbered = lines |> Seq.mapi (fun i l -> i + 1, l) |> List.ofSeq

    match Table.parse numbered with
    | Error e -> Error $"#infer: {e}"
    | Ok(cols, rows) ->
        let notes = ResizeArray<Note>()
        notes.Add $"a table reads rows — '{topName}' names the row; read the value as 'seq<{topName}>'"

        if rows.IsEmpty then
            notes.Add "no data rows under the header — every column drafted string; verify against a fuller sample"

        let used = System.Collections.Generic.HashSet<string>()

        let fields =
            cols
            |> List.mapi (fun i c ->
                let cells =
                    rows |> List.map (fun (_, cs) -> if i < List.length cs then cs[i] else "")

                let vals = cells |> List.filter (fun s -> not (Table.isAbsent s))

                let tyText =
                    if rows.IsEmpty then
                        "string"
                    elif vals.IsEmpty then
                        notes.Add $"column '{c.Header}' has no values — drafted Option<string> (edit if the real type is known)"
                        "Option<string>"
                    else
                        let scanned =
                            if vals |> List.forall tableIntRx.IsMatch then
                                "int"
                            elif vals |> List.forall (fun v -> tableIntRx.IsMatch v || tableFloatRx.IsMatch v) then
                                "float"
                            elif vals |> List.forall (fun v -> v = "true" || v = "false") then
                                "bool"
                            else
                                "string"

                        if List.length vals < List.length cells then
                            notes.Add $"column '{c.Header}' has empty/<none> cells — drafted Option<{scanned}>"
                            $"Option<{scanned}>"
                        else
                            scanned

                let baseName = tableFieldName reserved c.Header

                let name =
                    if used.Add baseName then
                        baseName
                    else
                        let mutable n = 2

                        while not (used.Add(baseName + string n)) do
                            n <- n + 1

                        baseName + string n

                // the Wire decision is the read-side recovery test: a
                // field whose normalized name still matches the header
                // needs no attribute; anything lossier carries the raw
                // header verbatim [D:from-table]
                let wire = if Table.matchKey name = Table.matchKey c.Header then None else Some c.Header

                name, wire, tyText)

        let body =
            fields
            |> List.map (fun (fname, wire, t) ->
                match wire with
                | Some k -> $"    [<Wire \"{k}\">]\n    {fname}: {t}"
                | None -> $"    {fname}: {t}")
            |> String.concat "\n"

        Ok([ $"type {topName} = {{\n{body}\n}}" ], List.ofSeq notes)

// ---- format dispatch --------------------------------------------------

type Format =
    | Json
    | Jsonl
    | Yaml
    | Table

let parseFormat (s: string) : Result<Format, string> =
    match s.Trim() with
    | "json" -> Ok Json
    | "jsonl" -> Ok Jsonl
    | "yaml" -> Ok Yaml
    | "table" -> Ok Table
    | other -> Error $"#infer: unknown format '{other}' — use json, jsonl, yaml, or table"

let nodeOf (fmt: Format) (lines: string seq) : Result<INode, string> =
    match fmt with
    | Json -> jsonNode lines
    | Jsonl -> jsonlNode lines
    | Yaml -> yamlNode lines
    // a table never lowers to a document node [D:from-table] — `infer`
    // dispatches it to the per-column scan before reaching here
    | Table -> Error "#infer: a table drafts per column, not from a document node"

/// the whole pipeline for a chosen format + name: lines -> decls + notes
let infer
    (reserved: Set<string>)
    (taken: Set<string>)
    (fmt: Format)
    (topName: string)
    (lines: string seq)
    : Result<string list * Note list, string> =
    match fmt with
    | Table ->
        // the top name is the caller's choice (the REPL refuses builtin
        // landings), and a flat row drafts no nested types — `taken`
        // has nothing left to guard here
        ignore taken
        tableDecls reserved topName lines
    | fmt -> nodeOf fmt lines |> Result.map (fun node -> inferDecls reserved taken topName node)

/// the composable builtin body: sample lines -> declaration text (the
/// notes ride as trailing `//` comment lines so the one-string return
/// stays honest outside the REPL). Raises on a parse failure, per the
/// builtin-raise convention.
let inferShapeText (reserved: Set<string>) (taken: Set<string>) (fmt: Format) (topName: string) (lines: string seq) : string =
    match infer reserved taken fmt topName lines with
    | Error msg -> failwith msg
    | Ok(decls, notes) ->
        let noteLines = notes |> List.map (fun n -> $"// note: {n}")
        String.Join("\n", decls @ noteLines)
