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
let defStem (key: string) : string =
    let last =
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

    let rec shape (desired: string) (srcKey: string) (parentStem: string) (path: string) (s: Schema) : string =
        let where = if path = "" then "the schema root" else path

        match s with
        | SRef key -> resolveRef key
        | SNullable inner ->
            // nullable → Option, the same wrap optionality takes
            opt (shape desired srcKey parentStem path inner)
        | SChoice [] -> shape desired srcKey parentStem path SAny
        | SChoice(first :: _) ->
            // the #infer heterogeneous-array posture: first variant +
            // a verify note, never a silent guess (tagged unions are
            // schema territory weir does not draft) [D:schema-types]
            reg.AddNote $"{where}: anyOf — drafted the FIRST variant only; verify the rest"
            shape desired srcKey parentStem path first
        | SEnum values ->
            // bounded v1: enum drafts string, the values ride a note
            // (a string union is a stated follow-up)
            reg.AddNote $"{where}: an enum of {enumText values} — drafted string; match the values yourself"
            "string"
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
                elif core.IsEmpty then
                    // type: ["null"] — nothing but absence to draft
                    reg.AddNote $"{where}: the schema allows only null — drafted Option<string>"
                    "Option<string>"
                else
                    // the IntOrString idiom (and any other multi-kind
                    // scalar): string carries every spelling; the note
                    // names the conversion decision
                    reg.AddNote $"{where}: the schema allows {kindsText core} — drafted string; convert deliberately"
                    "string"

            if kinds.Contains "null" then opt text else text
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

                    // the headline rule: absent from `required` → Option —
                    // the fact no sample can carry [D:schema-types]
                    let ty = if List.contains k required then core else opt core
                    k, ty)

            reg.Claim desired srcKey parentStem rendered

    and resolveRef (key: string) : string =
        match memo.TryGetValue key with
        | true, Some t -> t
        | true, None ->
            // a cycle (JSONSchemaProps and friends): no finite weir
            // record spells a self-referential type — the opaque note
            // posture, never a hang and never a silent guess
            reg.AddNote $"'{key}' is self-referential — kept opaque as Yaml (a bounded hand-written shape can replace it)"
            "Yaml"
        | _ ->
            memo[key] <- None

            // a def's collision prefix is its second-to-last segment
            // (io.k8s.api.core.v1.EnvVar → V1EnvVar when EnvVar is
            // taken) — the Infer parent-prefix rule with the def path
            // standing in for the parent record
            let parentSeg =
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
    | [] ->
        Error
            $"the schema's top level is '{top}', not an object with declared properties — there is no record to generate; read the value directly with that type"
    | decls -> Ok(Infer.renderDecls reserved decls, reg.Notes, top)

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
