module Weir.SchemaTypes

// the schema→types generator [D:schema-types]: a locked JSON Schema →
// a decl-only weir module the user owns. #infer drafts from a sample
// and can only see what the sample had — a container without `env` in
// the next run breaks the drafted type; a schema carries the facts no
// sample can: required vs optional, additionalProperties, names. This
// file is the frontend only — the naming registry, the Wire sanitizer
// and the decl renderer are Infer's own (one emitter, two frontends;
// a schema is shape directly, no sample walking). Nothing here touches
// the network or the session: the input is the vendored, hash-locked
// schema text, so generation is offline and byte-deterministic.

open System
open Weir.Contracts

/// the last dot-segment of a definition key names the type —
/// io.k8s.api.core.v1.PodSpec → PodSpec (the $ref-name law); the
/// segment still rides the identifier sanitizer
// a pointer key's name segments: its last segments that are not schema
// keywords or array indexes (#/definitions/opts/properties/inner →
// inner, opts) [D:schema-deep-refs]
let private pointerNames (key: string) : string list =
    let keywords =
        set
            [ "properties"
              "items"
              "additionalProperties"
              "anyOf"
              "oneOf"
              "allOf"
              "definitions"
              "$defs"
              "components"
              "schemas" ]

    key.TrimStart('#').Split '/'
    |> Array.filter (fun seg -> seg <> "" && not (keywords.Contains seg) && not (Seq.forall Char.IsDigit seg))
    |> Array.map (fun seg -> Uri.UnescapeDataString(seg).Replace("~1", "/").Replace("~0", "~"))
    |> Array.rev
    |> List.ofArray

let defStem (key: string) : string =
    let last =
        if key.StartsWith "#" then
            match pointerNames key with
            | n :: _ -> n
            | [] -> "Root"
        else
            match key.Split '.' |> Array.filter (fun s -> s <> "") with
            | [||] -> key
            | segs -> Array.last segs

    Infer.capitalize (Infer.identStem last)

// Option never nests from the two absence spellings — an optional
// nullable field is Option<T> once
let private opt (t: string) : string =
    if t.StartsWith "Option<" then t else $"Option<{t}>"

let private kindsText (kinds: Set<string>) =
    String.Join("/", Set.toList kinds)

// how each JSON kind reads into the `Yaml` node, for the drafted-Yaml
// notes [D:schema-bool-or-string] [D:schema-mixed-kinds]
let private yamlCasesText (kinds: Set<string>) =
    [ if kinds.Contains "boolean" then "a bool", "YBool b"
      if kinds.Contains "number" then "a number", "YInt n or YFloat f"
      elif kinds.Contains "integer" then "an int", "YInt n"
      if kinds.Contains "string" then "a string", "YStr s"
      if kinds.Contains "array" then "a sequence", "YSeq items"
      if kinds.Contains "object" then "a mapping", "YMap entries" ]
    |> List.mapi (fun i (what, case) -> if i = 0 then $"{what} reads as {case}" else $"{what} as {case}")
    |> String.concat ", "

let private enumText (values: string list) =
    values |> List.map (fun v -> $"'{v}'") |> String.concat ", "

/// the generated declarations + notes + the top type's final name.
/// `reserved` is the parser's keyword set, `taken` the builtin nominal
/// names (both threaded in — their sources compile after Infer, the
/// standing pattern); `topName` is the user's own (--as or derived) and
/// exempt from the taken-name guard exactly as #infer's `as` name is.
let generate
    (reserved: Set<string>)
    (taken: Set<string>)
    (schemaName: string)
    (topName: string)
    (doc: SchemaDoc)
    : Result<string list * Infer.Note list * string, string> =
    let reg = Infer.Registry(Set.remove topName (Infer.takenTypeNames taken))

    // defName -> final rendered type text; None marks in-progress, so a
    // re-entrant resolve is a cycle — the opaque note posture
    let memo = Collections.Generic.Dictionary<string, string option>()

    // a cyclic reference renders as this marker; the record walk drafts
    // a field whose type carries it as the opaque `Yaml` node — weir
    // records are finite trees, and both boundaries read a subtree whole
    // into `Yaml` [D:json-yaml-field]
    let cyc = "\u0000cycle"

    let rec shape (desired: string) (srcKey: string) (parentStem: string) (path: string) (s: Schema) : string =
        let where = if path = "" then "the schema root" else path

        match s with
        | SRef key -> resolveRef key
        | SNullable(SEnum(values, kind, listed)) ->
            // the enum's null is absence — its note lists it as null
            // only when the enum holds it and the type allows it
            // [D:schema-enum-null]
            opt (enumShape where values kind true listed)
        | SNullable inner ->
            // nullable → Option, the same wrap optionality takes
            opt (shape desired srcKey parentStem path inner)
        | SAllOf members ->
            // a $ref with shape siblings [D:schema-ref-siblings]: all
            // objects merge into one record (later members win a shared
            // key, required unions); anything else drafts the first
            // member and says so
            let objs =
                members
                |> List.map (deref doc.Defs Set.empty)
                |> List.map (function
                    | SObject(props, req, add) -> Some(props, req, add)
                    | _ -> None)

            if objs |> List.forall Option.isSome then
                let objs = objs |> List.choose id

                let props =
                    objs
                    |> List.collect (fun (ps, _, _) -> ps)
                    |> List.fold
                        (fun acc (k, v) ->
                            if acc |> List.exists (fun (k2, _) -> k2 = k) then
                                acc |> List.map (fun (k2, v2) -> if k2 = k then (k, v) else (k2, v2))
                            else
                                acc @ [ (k, v) ])
                        []

                let required = objs |> List.collect (fun (_, r, _) -> r) |> List.distinct

                let additional =
                    objs
                    |> List.map (fun (_, _, a) -> a)
                    |> List.tryFind (function
                        | OpenProps -> false
                        | _ -> true)
                    |> Option.defaultValue OpenProps

                reg.AddNote $"{where}: a $ref with sibling properties — drafted the merged record"
                shape desired srcKey parentStem path (SObject(props, required, additional))
            else
                reg.AddNote
                    $"{where}: a $ref with sibling keywords that are not both objects — drafted the reference only; the siblings also apply, verify"

                shape desired srcKey parentStem path (List.head members)
        | SChoice [] -> shape desired srcKey parentStem path SAny
        | SChoice(first :: _) ->
            // the #infer heterogeneous-array posture: first variant +
            // a verify note, never a silent guess (tagged unions are
            // schema territory weir does not draft) [D:schema-types]
            reg.AddNote $"{where}: anyOf — drafted the FIRST variant only; verify the rest"
            shape desired srcKey parentStem path first
        | SEnum(values, kind, _) -> enumShape where values kind false false
        | SIntOrStringFormat inner ->
            // [D:schema-num-or-string]: the int-or-string fix's
            // class, spelled as a string format
            let nullable =
                match inner with
                | SScalar kinds -> kinds.Contains "null"
                | _ -> false

            reg.AddNote
                $"{where}: format int-or-string — drafted Yaml; an int reads as YInt n, a string as YStr s"

            if nullable then opt "Yaml" else "Yaml"
        | SUint64Format inner ->
            // [D:schema-uint64]: int would refuse a value past int64 and
            // float would round it; the node keeps its digits both ways
            let nullable =
                match inner with
                | SScalar kinds -> kinds.Contains "null"
                | _ -> false

            reg.AddNote
                $"{where}: format uint64 — drafted Yaml; a value reads as YInt n, one past int64 as YFloat f that writes back its digits"

            if nullable then opt "Yaml" else "Yaml"
        | SAny ->
            reg.AddNote $"{where}: unconstrained in the schema — drafted string (edit if the real shape is known)"
            "string"
        | SScalar kinds ->
            let core = Set.remove "null" kinds

            let text =
                if core = Set.singleton "string" then "string"
                elif core = Set.singleton "integer" then "int"
                elif core = Set.singleton "boolean" then "bool"
                elif core = Set.singleton "number" || core = Set.ofList [ "integer"; "number" ] then
                    "float"
                elif core = Set.ofList [ "integer"; "string" ] then
                    // int-or-string (a k8s port, a rollout percentage)
                    // drafts the opaque node [D:schema-int-or-string]: it
                    // crosses json and yaml both ways and keeps 8080 an int
                    // and "25%" a string — string would refuse 8080 on read
                    // and quote it on write
                    reg.AddNote
                        $"{where}: the schema allows integer/string — drafted Yaml; an int reads as YInt n, a string as YStr s"

                    "Yaml"
                elif
                    core.Contains "string"
                    && core.Contains "number"
                    && Set.isSubset core (set [ "integer"; "number"; "string" ])
                then
                    // number-or-string (a k8s quantity, an HPA target)
                    // is the same class [D:schema-num-or-string]
                    reg.AddNote
                        $"{where}: the schema allows {kindsText core} — drafted Yaml; a number reads as YInt n or YFloat f, a string as YStr s"

                    "Yaml"
                elif core.IsEmpty then
                    // type: ["null"] — nothing but absence to draft
                    reg.AddNote $"{where}: the schema allows only null — drafted Option<string>"
                    "Option<string>"
                else
                    // boolean beside a string or number (GH Actions'
                    // cancel-in-progress) is the same class
                    // [D:schema-bool-or-string]: string would refuse
                    // every bool on read
                    reg.AddNote $"{where}: the schema allows {kindsText core} — drafted Yaml; {yamlCasesText core}"
                    "Yaml"

            if kinds.Contains "null" then opt text else text
        | SMixed(kinds, _) ->
            // a scalar beside a container drafts the node whole
            // [D:schema-mixed-kinds] — never a silent pick of one side
            let core = Set.remove "null" kinds
            reg.AddNote $"{where}: the schema allows {kindsText core} — drafted Yaml; {yamlCasesText core}"
            if kinds.Contains "null" then opt "Yaml" else "Yaml"
        | SArray items ->
            let elemDesired = Infer.capitalize (Infer.singularize (Infer.identStem desired))
            let itemPath = path + "[]"
            $"seq<{shape elemDesired srcKey parentStem itemPath items}>"
        | SObject([], _, Vals v) ->
            // the open mapping — additionalProperties carries the value
            // schema, the keys are data (k8s labels/annotations)
            let elemDesired = Infer.capitalize (Infer.singularize (Infer.identStem desired))
            let valPath = path + ".*"
            $"seq<string * {shape elemDesired srcKey parentStem valPath v}>"
        | SObject([], _, _) ->
            // an object with no declared properties and no value schema:
            // the empty-mapping posture, said aloud [D:repl-infer]
            reg.AddNote
                $"{where}: an object with no declared properties — drafted seq<string * string> (edit if the real shape is known)"

            "seq<string * string>"
        | SObject(props, required, additional) ->
            (match additional with
             | Vals _ ->
                 reg.AddNote
                     $"{where}: declared properties AND additionalProperties — the record drafts the declared ones; undeclared keys are ignored on read"
             | _ -> ())

            let thisStem = Infer.identStem desired

            let rendered =
                props
                // determinism: fields alphabetical by wire key (ordinal),
                // the sig generator's own ordering law
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                |> List.map (fun (k, sub) ->
                    let childPath = if path = "" then k else path + "." + k
                    let core = shape (Infer.capitalize (Infer.identStem k)) k thisStem childPath sub

                    let core =
                        if core.Contains cyc then
                            reg.AddNote
                                $"{childPath}: refers back to its own definition — drafted Yaml (a weir record is a finite tree; the node holds the subtree whole)"

                            core.Replace(cyc, "Yaml")
                        else
                            core

                    // the headline rule: absent from `required` → Option —
                    // the fact no sample can carry [D:schema-types]
                    let isRequired = List.contains k required

                    // required + nullable [D:schema-required-nullable]:
                    // Option's None omits the key, so a null the
                    // document held does not write back
                    if isRequired && core.StartsWith "Option<" then
                        reg.AddNote
                            $"{childPath}: required and nullable — drafted {core}; None writes the key omitted, not null (a PATCH reads that as unchanged); declare it Yaml to keep a null (YNull writes null)"

                    let ty = if isRequired then core else opt core
                    k, ty)

            reg.Claim desired srcKey parentStem rendered

    and enumShape (where: string) (values: string list) (kind: string) (nullable: bool) (nullListed: bool) : string =
        let withNull (listed: string) =
            if nullListed then listed + ", null" else listed

        let drafted (ty: string) = if nullable then opt ty else ty
        let asString = drafted "string"
        let asYaml = drafted "Yaml"

        match kind with
        | "string" ->
            // bounded v1: enum drafts string, the values ride a note
            // (a string union is a stated follow-up)
            reg.AddNote
                $"{where}: an enum of {withNull (enumText values)} — drafted {asString}; match the values yourself"

            "string"
        | "mixed" ->
            // values of differing kinds draft the opaque node
            // [D:schema-enum-kind]: any one type would refuse the rest
            let listed = String.concat ", " values

            reg.AddNote
                $"{where}: an enum mixing kinds ({withNull listed}) — drafted {asYaml}; match the node's case and value yourself"

            "Yaml"
        | _ ->
            // a non-string enum drafts its type [D:schema-int-enum]
            // [D:schema-enum-kind] — string would refuse the document's
            // own numbers and booleans
            let ty =
                match kind with
                | "integer" -> "int"
                | "number" -> "float"
                | _ -> "bool"

            let listed = String.concat ", " values
            reg.AddNote $"{where}: an enum of {withNull listed} — drafted {drafted ty}; match the values yourself"
            ty

    and resolveRef (key: string) : string =
        match memo.TryGetValue key with
        | true, Some t -> t
        | true, None ->
            // a cycle (JSONSchemaProps and friends): no finite weir
            // record spells a self-referential type — the marker makes
            // the referring field an opaque Yaml node with a note, never
            // a hang and never a silent guess
            cyc
        | _ ->
            memo[key] <- None

            // a def's collision prefix is its second-to-last segment
            // (io.k8s.api.core.v1.EnvVar → V1EnvVar when EnvVar is
            // taken) — the Infer parent-prefix rule with the def path
            // standing in for the parent record
            let parentSeg =
                if key.StartsWith "#" then
                    match pointerNames key with
                    | _ :: parent :: _ -> Infer.identStem parent
                    | _ -> ""
                else
                    match key.Split '.' |> Array.filter (fun s -> s <> "") with
                    | segs when segs.Length >= 2 -> Infer.identStem segs[segs.Length - 2]
                    | _ -> ""

            let t =
                match Map.tryFind key doc.Defs with
                | Some s -> shape (defStem key) key parentSeg key s
                | None -> "string" // unreachable: parseSchema refuses dangling refs

            memo[key] <- Some t
            t

    // the top: a root $ref generates its target under the top name (the
    // user's --as wins over the definition's own); a root object claims
    // the top record directly
    // `#` is the root itself: a ref to it from inside the tree is a
    // cycle, drafted as the opaque node [D:schema-deep-refs]
    memo["#"] <- None

    let top =
        match doc.Root with
        | SRef key ->
            memo[key] <- None

            let t =
                match Map.tryFind key doc.Defs with
                | Some s -> shape topName key "" "" s
                | None -> "string" // unreachable: parseSchema refuses dangling refs

            memo[key] <- Some t
            t
        | root -> shape topName schemaName "" "" root

    match reg.Decls with
    | _ when top.Contains cyc ->
        Error
            "the schema's top level is defined only through itself — a weir record is a finite tree, so there is no record to generate"
    | [] ->
        Error
            $"the schema's top level is '{top}', not an object with declared properties — there is no record to generate; read the value directly with that type"
    | decls ->
        // the validation keywords of the trees this module drafts — the
        // root and every definition it reached — one note per keyword
        // [D:schema-validation-keys]
        let shown (key: string option) (p: string) =
            let local =
                p.Replace("additionalProperties.", "*.").Replace("properties.", "").Replace("items.", "[].").Replace("allOf.", "").Replace(".[]", "[]").TrimEnd('.')

            match key, local with
            | None, "" -> "the schema root"
            | None, l -> l
            | Some k, "" -> defStem k
            | Some k, l -> defStem k + "." + l

        let unenforced =
            doc.Unenforced
            |> List.filter (fun (key, _, _) ->
                match key with
                | None -> true
                | Some k -> memo.ContainsKey k)
            |> List.groupBy (fun (_, kw, _) -> kw)
            |> List.sortBy fst
            |> List.map (fun (kw, hits) ->
                let places = hits |> List.map (fun (key, _, p) -> shown key p) |> List.distinct
                let listed = places |> List.truncate 5 |> String.concat ", "

                let more =
                    if places.Length > 5 then $" and {places.Length - 5} more" else ""

                $"not enforced: {kw} on {listed}{more}")

        Ok(Infer.renderDecls reserved decls, reg.Notes @ unenforced, top)

/// what one generation produced — the CLI's whole answer
type Generated =
    { Text: string
      ModuleName: string
      TopType: string
      TypeCount: int
      NoteCount: int }

/// the whole generated module text: provenance header (schema name +
/// lock hash + source — no timestamp, so regeneration is byte-identical
/// [D:schema-types]), the notes as comment lines, then the decls. The
/// module name derives from the schema name (k8s-pod → K8sPod).
let moduleText
    (reserved: Set<string>)
    (taken: Set<string>)
    (schemaName: string)
    (asName: string option)
    (lockSha256: string)
    (url: string)
    (doc: SchemaDoc)
    : Result<Generated, string> =
    let moduleName = Infer.capitalize (Infer.identStem schemaName)

    let topName =
        match asName with
        | Some n -> n
        | None ->
            match doc.Root with
            | SRef key -> defStem key
            | _ -> moduleName

    match generate reserved taken schemaName topName doc with
    | Error e -> Error e
    | Ok(decls, notes, top) ->
        let header =
            [ $"module {moduleName}"
              $"// generated by `weir gen types` from schema {schemaName} ({lockSha256.Substring(0, 12)}…)"
              $"// source: {url}"
              "// user-owned after generation: edit freely — `weir gen types` again overwrites" ]
            @ (notes |> List.map (fun n -> $"// note: {n}"))

        let text =
            String.Join("\n", header) + "\n\n" + String.Join("\n\n", decls) + "\n"

        Ok
            { Text = text
              ModuleName = moduleName
              TopType = top
              TypeCount = List.length decls
              NoteCount = List.length notes }
