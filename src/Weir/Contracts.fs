module Weir.Contracts

// External contracts [D:contracts-spine]: vendored, pinned artifacts
// that constrain what the CHECKER accepts and contribute NOTHING at
// run time. Four properties, each load-bearing: vendored (checked in
// under .weir/, never fetched during check), pinned (exact identity,
// no ranges — pairwise comparisons, not a dependency graph), check-time
// only (deleting every contract leaves every script running
// identically), declared not discovered (a .weir/ directory's mere
// existence never changes how a file checks).
//
// The first customer is YAML schemas [D:yaml-schemas]; command
// signatures and remote modules inherit this spine.

open System
open System.IO
open Weir.Ast
open Weir.Types

// ---- discovery -------------------------------------------------------------

/// walk UP from `fromDir` to the first `.weir/`; stop there. Bounded by
/// a `.git` (dir or file — worktrees) and the filesystem root. The
/// error names both what was looked for and where the walk stopped.
let findWeirDir (fromDir: string) : Result<string, string> =
    let rec walk (dir: string) =
        let candidate = Path.Combine(dir, ".weir")

        if Directory.Exists candidate then
            Ok candidate
        elif
            Directory.Exists(Path.Combine(dir, ".git"))
            || File.Exists(Path.Combine(dir, ".git"))
        then
            Error $"no .weir directory between {fromDir} and the repo root {dir}"
        else
            match Directory.GetParent dir with
            | null -> Error $"no .weir directory between {fromDir} and the filesystem root"
            | parent -> walk parent.FullName

    walk (Path.GetFullPath fromDir)

// ---- the lockfile ----------------------------------------------------------

// per artifact: kind, name, source url, sha256 of the file bytes, and
// the path relative to .weir/. THE LOCKFILE IS THE MANIFEST (a
// deliberate choice — no ranges means nothing for a separate manifest
// to hold): `weir add` writes it, `weir restore` re-materializes from
// it, `weir verify` checks it. Absent until the first add.
type LockEntry =
    { Kind: string
      Name: string
      Url: string
      Sha256: string
      Path: string }

let sha256Hex (bytes: byte[]) : string =
    use sha = Security.Cryptography.SHA256.Create()

    sha.ComputeHash bytes
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

let private lockPath (weirDir: string) = Path.Combine(weirDir, "lock.json")

let readLock (weirDir: string) : Result<LockEntry list, string> =
    let p = lockPath weirDir

    if not (File.Exists p) then
        Ok [] // absent until the first fetch — nothing vendored yet
    else
        try
            use doc = Text.Json.JsonDocument.Parse(File.ReadAllText p)

            doc.RootElement.GetProperty("artifacts").EnumerateArray()
            |> Seq.map (fun e ->
                { Kind = e.GetProperty("kind").GetString()
                  Name = e.GetProperty("name").GetString()
                  Url = e.GetProperty("url").GetString()
                  Sha256 = e.GetProperty("sha256").GetString()
                  Path = e.GetProperty("path").GetString() })
            |> List.ofSeq
            |> Ok
        with ex ->
            Error $"{p}: cannot read the lockfile: {ex.Message}"

let writeLock (weirDir: string) (entries: LockEntry list) : unit =
    use ms = new MemoryStream()

    (use w =
        new Text.Json.Utf8JsonWriter(ms, Text.Json.JsonWriterOptions(Indented = true))

     w.WriteStartObject()
     w.WriteStartArray "artifacts"

     for e in entries do
         w.WriteStartObject()
         w.WriteString("kind", e.Kind)
         w.WriteString("name", e.Name)
         w.WriteString("url", e.Url)
         w.WriteString("sha256", e.Sha256)
         w.WriteString("path", e.Path)
         w.WriteEndObject()

     w.WriteEndArray()
     w.WriteEndObject())

    File.WriteAllBytes(lockPath weirDir, ms.ToArray())

// ---- fetch (ruling 4: each failure mode its own message) -------------------

let fetchBytes (url: string) : Result<byte[], string> =
    try
        use client = new Net.Http.HttpClient()
        client.Timeout <- TimeSpan.FromSeconds 60.0
        use resp = client.GetAsync(url).Result

        if not resp.IsSuccessStatusCode then
            Error $"{url} answered {int resp.StatusCode} ({resp.StatusCode})"
        else
            Ok(resp.Content.ReadAsByteArrayAsync().Result)
    with ex ->
        let root =
            let rec inner (e: exn) =
                match e.InnerException with
                | null -> e
                | i -> inner i

            inner ex

        let host =
            try
                Uri(url).Host
            with _ ->
                url

        Error $"cannot reach {host} — {root.Message}"

// ---- add / restore / verify ------------------------------------------------
// `add <kind>` is KIND-AWARE (acquiring differs per kind: a schema is
// a url fetch; a signature will GENERATE from the installed tool; a
// module will clone at a ref); `restore` and `verify` are
// kind-agnostic BY CONSTRUCTION — every lock entry is source + hash +
// path, so they need to know nothing about the artifact.

/// `weir add schema <url> --as <name>`: fetch, write under the kind
/// directory, upsert the lock entry.
let addFetched (weirDir: string) (kind: string) (name: string) (url: string) : Result<string, string> =
    match fetchBytes url with
    | Error e -> Error e
    | Ok bytes ->
        let rel = Path.Combine(kind + "s", name + ".json")
        let dest = Path.Combine(weirDir, rel)
        Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
        File.WriteAllBytes(dest, bytes)
        let hash = sha256Hex bytes

        // a schema with no `additionalProperties: false` ANYWHERE cannot
        // fire unknown-field checks — the feature's whole point — so a
        // silently-inert contract warns at ADD time (the vacuous-pin
        // class). Plain `-standalone` k8s variants have exactly this
        // shape; `-standalone-strict` is the load-bearing variant.
        if kind = "schema" then
            let hasClosed =
                try
                    use doc = Text.Json.JsonDocument.Parse bytes

                    let rec anyClosed (el: Text.Json.JsonElement) =
                        match el.ValueKind with
                        | Text.Json.JsonValueKind.Object ->
                            el.EnumerateObject()
                            |> Seq.exists (fun p ->
                                (p.Name = "additionalProperties"
                                 && p.Value.ValueKind = Text.Json.JsonValueKind.False)
                                || anyClosed p.Value)
                        | Text.Json.JsonValueKind.Array -> el.EnumerateArray() |> Seq.exists anyClosed
                        | _ -> false

                    anyClosed doc.RootElement
                with _ ->
                    true // unparseable here — the loader teaches later

            if not hasClosed then
                Console.Error.WriteLine
                    $"weir add: warning: {name} has no `additionalProperties: false` anywhere — unknown-field checking will NOT fire for it (for k8s, use the -standalone-strict variant)"

        match readLock weirDir with
        | Error e -> Error e
        | Ok entries ->
            let entry =
                { Kind = kind
                  Name = name
                  Url = url
                  Sha256 = hash
                  Path = rel }

            let others = entries |> List.filter (fun e -> not (e.Kind = kind && e.Name = name))

            writeLock weirDir (others @ [ entry ])
            Ok $"added {kind} {name} ({bytes.Length} bytes, sha256 {hash.Substring(0, 12)}…) from {url}"

/// `weir restore`: re-materialize anything in the lock missing on
/// disk, verifying each fetch against the recorded hash.
let restore (weirDir: string) : Result<string list, string> =
    match readLock weirDir with
    | Error e -> Error e
    | Ok [] -> Ok [ "the lock records nothing yet — add with: weir add schema <url> --as <name>" ]
    | Ok entries ->
        let results =
            entries
            |> List.map (fun e ->
                let dest = Path.Combine(weirDir, e.Path)

                if File.Exists dest then
                    Ok $"{e.Kind} {e.Name}: present"
                else
                    match fetchBytes e.Url with
                    | Error err -> Error $"{e.Kind} {e.Name}: {err}"
                    | Ok bytes ->
                        let hash = sha256Hex bytes

                        if hash <> e.Sha256 then
                            Error
                                $"{e.Kind} {e.Name}: fetched bytes hash {hash.Substring(0, 12)}… but the lock records {e.Sha256.Substring(0, 12)}… — the source changed; if intended, `weir add schema` again"
                        else
                            Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                            File.WriteAllBytes(dest, bytes)
                            Ok $"{e.Kind} {e.Name}: restored from {e.Url}")

        match
            results
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some firstErr -> Error firstErr
        | None ->
            Ok(
                results
                |> List.map (function
                    | Ok s -> s
                    | Error _ -> "")
            )

/// `weir verify`, two-arm shaped (ruling: today the hash arm; a future
/// signature arm — tool `--version` against the recorded identity —
/// slots beside it, not into a rewrite).
type VerifyFinding =
    | Absent of LockEntry
    | Modified of LockEntry * actual: string

let verify (weirDir: string) : Result<string list * VerifyFinding list, string> =
    match readLock weirDir with
    | Error e -> Error e
    | Ok entries ->
        let lines = ResizeArray<string>()
        let findings = ResizeArray<VerifyFinding>()

        for e in entries do
            let dest = Path.Combine(weirDir, e.Path)

            match e.Kind with
            // the hash arm: vendored artifacts verify by content
            | _ when not (File.Exists dest) ->
                findings.Add(Absent e)
                lines.Add $"{e.Kind} {e.Name}: ABSENT — run `weir restore`"
            | _ ->
                let actual = sha256Hex (File.ReadAllBytes dest)

                if actual <> e.Sha256 then
                    findings.Add(Modified(e, actual))

                    lines.Add
                        $"{e.Kind} {e.Name}: MODIFIED — sha256 {actual.Substring(0, 12)}…, lock records {e.Sha256.Substring(0, 12)}…"
                else
                    lines.Add $"{e.Kind} {e.Name}: ok"

        Ok(List.ofSeq lines, List.ofSeq findings)

// ---- the JSON Schema subset [D:yaml-schemas] -------------------------------

// corpus-measured (six real k8s standalone-strict schemas): IN — type
// (string or array-of-strings, the nullable spelling), properties,
// required, items, additionalProperties (bool or schema), enum, and
// oneOf RESTRICTED to scalar-type alternatives (every corpus occurrence
// is the IntOrString idiom). Annotations accepted and ignored:
// description, format, title, $schema, x-*. EVERYTHING else rejects
// with a teaching error naming the keyword and its JSON path — $ref's
// teaching names the standalone variants (refs inlined at publish).
type Schema =
    | SAny
    | SScalar of kinds: Set<string> // "string" | "integer" | "number" | "boolean" | "null"
    | SEnum of values: string list
    | SObject of props: (string * Schema) list * required: string list * additional: AdditionalProps
    | SArray of items: Schema

and AdditionalProps =
    | Closed
    | OpenProps
    | Vals of Schema

let private annotationKeys =
    set
        [ "description"
          "format"
          "title"
          "$schema"
          "example"
          "examples"
          "default" ]

let private rejectedTeaching (path: string) (kw: string) : string =
    let where = if path = "" then "the schema root" else path

    match kw with
    | "$ref"
    | "$defs"
    | "definitions" ->
        $"at {where}: '{kw}' is outside the schema subset — add the STANDALONE variant of this schema instead (refs inlined at publish; kubernetes-json-schema ships both)"
    | "allOf"
    | "anyOf"
    | "not"
    | "if"
    | "then"
    | "else" -> $"at {where}: '{kw}' (schema composition) is outside the subset — zero uses in the measured corpus"
    | _ -> $"at {where}: '{kw}' is outside the schema subset (it joins when a corpus needs it)"

let rec private parseNode (path: string) (el: Text.Json.JsonElement) : Result<Schema, string> =
    let where = if path = "" then "the schema root" else path

    if el.ValueKind <> Text.Json.JsonValueKind.Object then
        Error $"at {where}: a schema node must be an object"
    else
        // reject unknown keywords FIRST, so the teaching names them
        let mutable rejection = None

        for p in el.EnumerateObject() do
            let k = p.Name

            let known =
                k = "type"
                || k = "properties"
                || k = "required"
                || k = "items"
                || k = "additionalProperties"
                || k = "enum"
                || k = "oneOf"
                || annotationKeys.Contains k
                || k.StartsWith "x-"

            if not known && rejection.IsNone then
                rejection <- Some(rejectedTeaching path k)

        match rejection with
        | Some e -> Error e
        | None ->

            let getProp (name: string) =
                match el.TryGetProperty name with
                | true, v -> Some v
                | _ -> None

            let typeKinds =
                match getProp "type" with
                | None -> None
                | Some t when t.ValueKind = Text.Json.JsonValueKind.String -> Some(Set.singleton (t.GetString()))
                | Some t when t.ValueKind = Text.Json.JsonValueKind.Array ->
                    Some(t.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq)
                | Some _ -> Some Set.empty

            match getProp "enum" with
            | Some e ->
                let values =
                    e.EnumerateArray()
                    |> Seq.map (fun v ->
                        match v.ValueKind with
                        | Text.Json.JsonValueKind.String -> v.GetString()
                        | _ -> v.GetRawText())
                    |> List.ofSeq

                Ok(SEnum values)
            | None ->

                match getProp "oneOf" with
                | Some alts ->
                    // the IntOrString idiom: every alternative must be scalar-typed
                    let kinds =
                        alts.EnumerateArray()
                        |> Seq.collect (fun alt ->
                            match alt.TryGetProperty "type" with
                            | true, t when t.ValueKind = Text.Json.JsonValueKind.String -> [ t.GetString() ]
                            | true, t when t.ValueKind = Text.Json.JsonValueKind.Array ->
                                t.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                            | _ -> [ "?" ])
                        |> Set.ofSeq

                    if kinds.Contains "?" || kinds.Contains "object" || kinds.Contains "array" then
                        Error
                            $"at {where}: 'oneOf' is supported only over scalar type alternatives (the IntOrString idiom) — general composition is outside the subset"
                    else
                        Ok(SScalar kinds)
                | None ->

                    match typeKinds with
                    | Some kinds when kinds.Contains "object" || (kinds.IsEmpty && getProp("properties").IsSome) ->
                        objectNode path el getProp
                    | None when (getProp "properties").IsSome -> objectNode path el getProp
                    | Some kinds when kinds.Contains "array" ->
                        match getProp "items" with
                        | Some items -> parseNode (path + "items.") items |> Result.map SArray
                        | None -> Ok(SArray SAny)
                    | Some kinds when not kinds.IsEmpty -> Ok(SScalar kinds)
                    | _ -> Ok SAny

and private objectNode (path: string) (el: Text.Json.JsonElement) (getProp: string -> Text.Json.JsonElement option) =
    let required =
        match getProp "required" with
        | Some r when r.ValueKind = Text.Json.JsonValueKind.Array ->
            r.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
        | _ -> []

    let additional =
        match getProp "additionalProperties" with
        | Some a when a.ValueKind = Text.Json.JsonValueKind.False -> Ok Closed
        | Some a when a.ValueKind = Text.Json.JsonValueKind.True -> Ok OpenProps
        | Some a when a.ValueKind = Text.Json.JsonValueKind.Object ->
            parseNode (path + "additionalProperties.") a |> Result.map Vals
        | Some _ -> Ok OpenProps
        | None -> Ok OpenProps

    match additional with
    | Error e -> Error e
    | Ok additional ->
        let props =
            match getProp "properties" with
            | Some ps when ps.ValueKind = Text.Json.JsonValueKind.Object ->
                ps.EnumerateObject()
                |> Seq.fold
                    (fun acc p ->
                        match acc with
                        | Error e -> Error e
                        | Ok list ->
                            parseNode (path + "properties." + p.Name + ".") p.Value
                            |> Result.map (fun s -> (p.Name, s) :: list))
                    (Ok [])
                |> Result.map List.rev
            | _ -> Ok []

        props |> Result.map (fun props -> SObject(props, required, additional))

/// parse a vendored schema file's text into the subset tree; errors
/// carry the schema NAME and the JSON path of the offending keyword.
let parseSchema (name: string) (text: string) : Result<Schema, string> =
    try
        use doc = Text.Json.JsonDocument.Parse text

        parseNode "" doc.RootElement |> Result.mapError (fun e -> $"schema {name}: {e}")
    with ex ->
        Error $"schema {name}: not valid JSON — {ex.Message}"

// ---- validation against a district template [D:yaml-schemas] ---------------
//
// STRUCTURAL validation always (unknown fields, missing required
// fields, misplaced nesting); VALUE validation where types permit
// (a splice checks by its weir TYPE; enum/pattern constraints on
// splices do not check). `for`-generated entries and key splices
// RELAX the unknown/required checks for the map they touch — dynamic
// keys may supply what the checker cannot see. All stated in docs.

let private levenshtein (a: string) (b: string) =
    let d = Array2D.create (a.Length + 1) (b.Length + 1) 0

    for i in 0 .. a.Length do
        d[i, 0] <- i

    for j in 0 .. b.Length do
        d[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)

    d[a.Length, b.Length]

let private didYouMean (k: string) (props: (string * Schema) list) =
    props
    |> List.map fst
    |> List.filter (fun p -> k.Length > 3 && levenshtein k p <= 2)
    |> function
        | best :: _ -> $" — did you mean '{best}'?"
        | [] -> ""

/// the scalar kind a weir TYPE guarantees, or None when the type
/// cannot speak (Yaml nodes, unresolved template parameters)
let rec private tyKind (t: Ty) : string option =
    match t with
    | TStr -> Some "string"
    | TInt -> Some "integer"
    | TBool -> Some "boolean"
    | TNamed("Option", [ inner ]) -> tyKind inner
    | _ -> None

let private kindOk (kinds: Set<string>) (got: string) =
    kinds.Contains got
    || (got = "integer" && kinds.Contains "number")
    || (got = "null" && kinds.Contains "null")

let private kindsText (kinds: Set<string>) = String.Join("/", Set.toList kinds)

let private literalKind (raw: string) (quoted: bool) =
    if quoted then
        "string"
    elif raw = "" then
        "null"
    elif raw = "true" || raw = "false" then
        "boolean"
    else
        match Int64.TryParse raw with
        | true, _ -> "integer"
        | _ -> "string"

// paths are ALWAYS in the message (ruling: a few characters buys a
// self-contained CI log — the span still carries editor identity).
// The root renders without a suffix so shallow messages stay terse.
let private atPath (p: string) = if p = "" then "" else $" at {p}"

let private fieldName (p: string) =
    if p = "" then "this value" else $"field {p}"

// enum rendering: a SINGLE allowed value states it plainly (k8s `kind`
// is a one-element enum — the common case); longer lists cap at 6 with
// an honest remainder count, never a decorative ellipsis
let private enumText (values: string list) =
    match values with
    | [ one ] -> $"'{one}'"
    | vs when List.length vs <= 6 -> "one of " + String.Join(", ", vs)
    | vs ->
        let shown = vs |> List.truncate 6 |> String.concat ", "
        $"one of {shown} (+{List.length vs - 6} more)"

let rec validateTpl (name: string) (path: string) (schema: Schema) (tpl: Check.TypedYamlTpl) : (Span * string) list =
    let child k = if path = "" then k else path + "." + k

    match schema, tpl with
    | SAny, _ -> []
    | _, Check.TYtSplice te -> spliceCheck name path schema te
    | SObject(props, required, additional), Check.TYtMap(entries, mspan) ->
        let hasDynamic =
            entries
            |> List.exists (function
                | Check.TYtPair(Check.TYtKeySplice _, _) -> true
                | Check.TYtForEntries _ -> true
                | _ -> false)

        let literalKeys =
            entries
            |> List.choose (function
                | Check.TYtPair(Check.TYtKeyLit(k, _), _) -> Some k
                | _ -> None)

        let rec entryErrors (es: Check.TypedYamlTplEntry list) =
            es
            |> List.collect (function
                | Check.TYtPair(Check.TYtKeyLit(k, kspan), v) ->
                    match props |> List.tryFind (fun (p, _) -> p = k) with
                    | Some(_, sub) -> validateTpl name (child k) sub v
                    | None ->
                        match additional with
                        | Vals s -> validateTpl name (child k) s v
                        | OpenProps -> []
                        | Closed -> [ kspan, $"schema {name}: unknown field '{k}'{atPath path}{didYouMean k props}" ]
                | Check.TYtPair(Check.TYtKeySplice _, v) ->
                    // a dynamic key: unknowable at check; its VALUE still
                    // checks when the schema constrains all values
                    match additional with
                    | Vals s -> validateTpl name path s v
                    | _ -> []
                | Check.TYtForEntries(_, _, body) -> entryErrors body)

        let missing =
            if hasDynamic then
                [] // dynamic keys may supply the required fields
            else
                required
                |> List.filter (fun r -> not (List.contains r literalKeys))
                |> List.map (fun r -> mspan, $"schema {name}: missing required field '{r}'{atPath path}")

        entryErrors entries @ missing
    | SObject _, Check.TYtScalar(_, _, span)
    | SObject _, Check.TYtBlock(_, span) -> [ span, $"schema {name}: {fieldName path} expects a mapping, got a scalar" ]
    | SObject _, Check.TYtSeq(_, span) -> [ span, $"schema {name}: {fieldName path} expects a mapping, got a sequence" ]
    | SArray items, Check.TYtSeq(elems, _) ->
        let rec itemErrors (es: Check.TypedYamlTplItem list) =
            es
            |> List.collect (function
                | Check.TYtItem t -> validateTpl name path items t
                | Check.TYtForItems(_, _, body) -> itemErrors body)

        itemErrors elems
    | SArray _, Check.TYtScalar(_, _, span)
    | SArray _, Check.TYtBlock(_, span) -> [ span, $"schema {name}: {fieldName path} expects a sequence, got a scalar" ]
    | SArray _, Check.TYtMap(_, span) -> [ span, $"schema {name}: {fieldName path} expects a sequence, got a mapping" ]
    | SScalar kinds, Check.TYtScalar(raw, quoted, span) ->
        let got = literalKind raw quoted

        if kindOk kinds got then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {kindsText kinds}, got {got} ('{raw}')" ]
    | SScalar kinds, Check.TYtBlock(_, span) ->
        if kindOk kinds "string" then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {kindsText kinds}, got a block scalar (string)" ]
    | SScalar _, Check.TYtMap(_, span) -> [ span, $"schema {name}: {fieldName path} expects a scalar, got a mapping" ]
    | SScalar _, Check.TYtSeq(_, span) -> [ span, $"schema {name}: {fieldName path} expects a scalar, got a sequence" ]
    | SEnum values, Check.TYtScalar(raw, _, span) ->
        if List.contains raw values then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {enumText values}, got '{raw}'" ]
    | SEnum values, Check.TYtBlock(text, span) ->
        if List.contains text values then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {enumText values}, got a block scalar" ]
    | SEnum _, Check.TYtMap(_, span)
    | SEnum _, Check.TYtSeq(_, span) ->
        [ span, $"schema {name}: {fieldName path} expects a scalar (enum), got a collection" ]

and private spliceCheck (name: string) (path: string) (schema: Schema) (te: Check.TypedExpr) : (Span * string) list =
    // value validation WHERE TYPES PERMIT: the splice's weir type is
    // all the checker can see. Yaml-typed and unresolved splices skip;
    // enum constraints on splices skip (stated).
    match schema with
    | SAny
    | SEnum _ -> []
    | SScalar kinds ->
        match te.Ty with
        | TSeq _ ->
            [ te.Span, $"schema {name}: {fieldName path}: a seq splices as sequence items; it expects {kindsText kinds}" ]
        | t ->
            match tyKind t with
            | None -> []
            | Some got ->
                if kindOk kinds got then
                    []
                else
                    [ te.Span, $"schema {name}: {fieldName path} expects {kindsText kinds}, but the splice is {got}" ]
    | SObject _ ->
        match tyKind te.Ty with
        | Some got -> [ te.Span, $"schema {name}: {fieldName path} expects a mapping, but the splice is {got}" ]
        | None -> []
    | SArray items ->
        match te.Ty with
        | TSeq elem ->
            (match items, tyKind elem with
             | SScalar kinds, Some got when not (kindOk kinds got) ->
                 [ te.Span,
                   $"schema {name}: {fieldName path}: sequence items expect {kindsText kinds}, but the spliced seq's elements are {got}" ]
             | _ -> [])
        | t ->
            match tyKind t with
            | Some got -> [ te.Span, $"schema {name}: {fieldName path} expects a sequence, but the splice is {got}" ]
            | None -> []
