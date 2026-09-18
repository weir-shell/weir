module Weir.Tests.InferProbe

open Expecto
open Weir
open Weir.Types

// PROBES [D:repl-infer]: the four unknowns the plan demands answered
// before building — kept as living tests.

let private preludeTypeEnv, _preludeValueEnv =
    Weir.Prelude.extend Weir.Builtins.typeEnv Weir.Builtins.valueEnv

// the REAL taken set a fresh session starts from [D:repl-infer] — the
// existing drafting pins below run against it, so they double as the
// zero-movement guarantee: no builtin hit, no renaming
let private takenBase: Set<string> =
    Infer.takenTypeNames (Seq.append Check.builtinTypeNames.Keys (Map.keys preludeTypeEnv.Types))

// probe 1: session TypeEnv injection — a `type` line checked through the
// same path the REPL uses produces a new env whose `from json <Name>`
// checks. We drive checkStatement exactly as Repl.fs does.
let private inject (tenv: TypeEnv) (line: string) : Result<TypeEnv, string> =
    let ll = Script.singleLine line

    match Script.checkStatement false None (fun _ -> Script.resolver tenv) Script.scriptOnlyImport tenv ll with
    | Ok chk -> Ok chk.Env
    | Error d -> Error d.Message

// inject a possibly MULTI-LINE statement (a `type` decl block) via the
// assembler — the path the REPL's multiline arm uses.
let private injectBlock (tenv: TypeEnv) (text: string) : Result<TypeEnv, string> =
    let numbered =
        text.Split '\n'
        |> Array.toList
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> Script.classifyLine raw <> Script.LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, Script.stripComment raw)

    match Script.assemble numbered with
    | Error msg -> Error msg
    | Ok lls ->
        let rec go env rest =
            match rest with
            | [] -> Ok env
            | (ll: Script.LogicalLine) :: tail ->
                match Script.checkStatement false None (fun _ -> Script.resolver env) Script.scriptOnlyImport env ll with
                | Ok chk -> go chk.Env tail
                | Error d -> Error d.Message

        go tenv lls

[<Tests>]
let probes =
    testList
        "infer probes"
        [ test "P1: injecting a type decl makes 'from json Name' check" {
              // inject the type, then a line that USES it must check
              match inject preludeTypeEnv "type Item = { id: int }" with
              | Error e -> failtestf "type injection failed: %s" e
              | Ok env1 ->
                  Expect.isTrue (Map.containsKey "Item" env1.Types) "Item is now a declared type"

                  match inject env1 "[\"{\\\"id\\\": 1}\"] |> from json Item |> _.id |> print" with
                  | Ok _ -> ()
                  | Error e -> failtestf "from json Item should check: %s" e
          }

          test "P2: bareAliasHomes gives qualified spellings" {
              let home n = Map.tryFind n Weir.Builtins.bareAliasHomes
              Expect.equal (home "map") (Some "Seq") "map -> Seq"
              Expect.equal (home "where") (Some "Seq") "where -> Seq"
              Expect.equal (home "iter") (Some "Seq") "iter -> Seq"
              Expect.equal (home "startsWith") (Some "Str") "startsWith -> Str"
          }

          test "P3: adapters parse seq<string> into an INode" {
              match Infer.nodeOf Infer.Json [ "{\"a\": 1, \"b\": \"x\"}" ] with
              | Ok(Infer.IObj [ ("a", Infer.IInt); ("b", Infer.IStr) ]) -> ()
              | Ok other -> failtestf "unexpected json node: %A" other
              | Error e -> failtestf "json parse failed: %s" e

              match Infer.nodeOf Infer.Yaml [ "a: 1"; "b: x" ] with
              | Ok(Infer.IObj fields) -> Expect.equal (fields |> List.map fst) [ "a"; "b" ] "yaml keys"
              | Ok other -> failtestf "unexpected yaml node: %A" other
              | Error e -> failtestf "yaml parse failed: %s" e

              match Infer.nodeOf Infer.Jsonl [ "{\"n\": 1}"; "{\"n\": 2}" ] with
              | Ok(Infer.IArr [ Infer.IObj _; Infer.IObj _ ]) -> ()
              | Ok other -> failtestf "unexpected jsonl node: %A" other
              | Error e -> failtestf "jsonl parse failed: %s" e
          }

          test "P4: note paths fire for empty/null/heterogeneous" {
              // empty array
              let _, n1 = Infer.inferDecls Parser.keywords takenBase "T" (Infer.IObj [ "xs", Infer.IArr [] ])
              Expect.isTrue (n1 |> List.exists (fun n -> n.Contains "empty array")) "empty-array note"
              // null field
              let _, n2 = Infer.inferDecls Parser.keywords takenBase "T" (Infer.IObj [ "v", Infer.INull ])
              Expect.isTrue (n2 |> List.exists (fun n -> n.Contains "null value")) "null note"
              // heterogeneous = a TYPE CONFLICT for one key across the
              // array's elements [D:repl-infer] — differing key SETS are
              // the merge's ordinary work, never a note
              let het =
                  Infer.IObj
                      [ "xs", Infer.IArr [ Infer.IObj [ "a", Infer.IInt ]; Infer.IObj [ "a", Infer.IStr ] ] ]

              let _, n3 = Infer.inferDecls Parser.keywords takenBase "T" het
              Expect.isTrue (n3 |> List.exists (fun n -> n.Contains "heterogeneous")) "heterogeneous note"
          } ]

// render the inferred declarations to ONE string for substring pins
let private renderJson (topName: string) (json: string) : string =
    match Infer.infer Parser.keywords takenBase Infer.Json topName [ json ] with
    | Ok(decls, _) -> String.concat "\n" decls
    | Error e -> failtestf "infer failed: %s" e

// a session that CHECKS the injected decls followed by a `from json`
// use — the round-trip that proves injection lights up the field.
let private checksAfterInject (decls: string list) (useLine: string) : Result<unit, string> =
    let mutable env = preludeTypeEnv
    let mutable err = None

    for line in decls @ [ useLine ] do
        if err.IsNone then
            match injectBlock env line with
            | Ok e -> env <- e
            | Error m -> err <- Some m

    match err with
    | Some m -> Error m
    | None -> Ok()

// run a full multi-line program from a temp file; returns its exit code
// (0 = clean) — the in-process read/write ROUNDTRIP driver
let private runFile (lines: string list) : int =
    let path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-infer-{System.Guid.NewGuid():N}.weir")

    System.IO.File.WriteAllLines(path, lines)

    try
        Script.run path []
    finally
        System.IO.File.Delete path

[<Tests>]
let inferRules =
    testList
        "infer rules"
        [ test "scalar widening: int vs float by token" {
              let out = renderJson "T" "{\"a\": 1, \"b\": 1.5, \"c\": \"x\", \"d\": true}"
              Expect.stringContains out "a: int" "integer token -> int"
              Expect.stringContains out "b: float" "decimal token -> float"
              Expect.stringContains out "c: string" "string"
              Expect.stringContains out "d: bool" "bool"
          }

          test "nested record capitalises the field name" {
              let out = renderJson "Root" "{\"metadata\": {\"name\": \"x\"}}"
              Expect.stringContains out "type Metadata = {" "metadata -> Metadata"
              Expect.stringContains out "metadata: Metadata" "field references it"
          }

          test "seq-of-record element is singularised" {
              let out = renderJson "Root" "{\"items\": [{\"id\": 1}]}"
              Expect.stringContains out "type Item = {" "items -> Item"
              Expect.stringContains out "items: seq<Item>" "field is seq<Item>"
          }

          test "un-pluralisable field name falls back as-is" {
              let out = renderJson "Root" "{\"data\": [{\"v\": 1}]}"
              Expect.stringContains out "type Data = {" "data stays Data (not Datum)"
          }

          test "array of scalars -> seq<scalar>" {
              let out = renderJson "T" "{\"tags\": [\"a\", \"b\"]}"
              Expect.stringContains out "tags: seq<string>" "seq of scalars"
          }

          test "dedup: same field name + same shape -> one type" {
              // two 'spec' objects, IDENTICAL shape -> a single Spec type
              let out = renderJson "Root" "{\"a\": {\"spec\": {\"n\": 1}}, \"b\": {\"spec\": {\"n\": 2}}}"
              let count =
                  System.Text.RegularExpressions.Regex.Matches(out, "type Spec = \\{").Count

              Expect.equal count 1 "one Spec type, deduped"
          }

          test "collision: same field name + different shape -> parent prefix" {
              // two 'spec' objects with DIFFERENT shapes under different parents
              let out =
                  renderJson "Root" "{\"pod\": {\"spec\": {\"cpu\": 1}}, \"container\": {\"spec\": {\"image\": \"x\"}}}"
              // the first Spec keeps the name; the second parent-prefixes
              Expect.stringContains out "type Spec = {" "first spec"
              Expect.isTrue
                  (out.Contains "type ContainerSpec = {" || out.Contains "type PodSpec = {")
                  "second spec parent-prefixed"
          }

          test "reserved wire key rides [<Wire>]" {
              let out = renderJson "T" "{\"type\": \"user\"}"
              Expect.stringContains out "[<Wire \"type\">]" "wire attr"
              Expect.stringContains out "kind: string" "legal field name"
          }

          test "PROBE PIN: the parser rejects EVERY keyword in field position [D:infer-wire-sanitize]" {
              // the sanitizer's reserved set IS Parser.keywords — this pin
              // holds field-name legality to that set: if the parser ever
              // accepts a keyword as a field name (or the set moves), it
              // fails HERE, not by silent drift in #infer's drafts
              for kw in Parser.keywords do
                  match injectBlock preludeTypeEnv $"type T = {{ {kw}: int }}" with
                  | Ok _ ->
                      failtestf
                          "'%s' checked as a field name — field-name legality widened; revisit the sanitizer's reserved set"
                          kw
                  | Error m -> Expect.stringContains m "keyword" $"'{kw}' refuses AS a keyword"

              // the control: a non-keyword word checks in field position
              match injectBlock preludeTypeEnv "type T = { json: int }" with
              | Ok _ -> ()
              | Error m -> failtestf "the control field name must check: %s" m
          }

          test "keyword key rides [<Wire>] on the parser's repair spelling [D:infer-wire-sanitize]" {
              let out = renderJson "T" "{\"in\": 2, \"match\": \"x\"}"
              Expect.stringContains out "[<Wire \"in\">]" "the keyword key carries its wire attribute"
              Expect.stringContains out "inField: int" "'in' lands on the parser's own repair name"
              Expect.stringContains out "[<Wire \"match\">]" "every keyword sanitizes, not just type/to/from"
              Expect.stringContains out "matchField: string" "'match' likewise"
          }

          test "EVERY keyword key drafts a type that CHECKS [D:infer-wire-sanitize]" {
              let sample =
                  Parser.keywords
                  |> Seq.mapi (fun i k -> $"\"{k}\": {i}")
                  |> String.concat ", "

              match Infer.infer Parser.keywords takenBase Infer.Json "Kw" [ "{" + sample + "}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  match checksAfterInject decls "[\"{\\\"in\\\": 0}\"] |> from json Kw |> _.inField |> print" with
                  | Ok() -> ()
                  | Error m -> failtestf "the all-keywords draft must check: %s" m
          }

          test "keyword key ROUNDTRIP: the draft checks, READS and WRITES the wire key [D:infer-wire-sanitize]" {
              match Infer.infer Parser.keywords takenBase Infer.Json "InTy" [ "{\"in\": 2}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  let script =
                      decls
                      @ [ "let v = [\"{\\\"in\\\": 2}\"] |> from json InTy"
                          "if v.inField <> 2 then fail \"the wire'd field did not read\""
                          "let out = v |> to json |> Seq.exactlyOne"
                          "if not (Str.contains \"\\\"in\\\":\" out) then fail \"the wire key did not write back\"" ]

                  Expect.equal (runFile script) 0 "the drafted type reads and writes {\"in\": 2}"
          }

          test "the yaml twin: a keyword key sanitizes through the shared walker [D:infer-wire-sanitize]" {
              match Infer.infer Parser.keywords takenBase Infer.Yaml "Y" [ "in: 2"; "clean: x" ] with
              | Error e -> failtestf "yaml infer failed: %s" e
              | Ok(decls, _) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "[<Wire \"in\">]" "the yaml keyword key carries its wire attribute"
                  Expect.stringContains out "inField: int" "and lands on the repair name"
                  Expect.stringContains out "clean: string" "the clean yaml key stays bare"
          }

          test "collision: a clean 'inField' beside the keyword 'in' dedupes deterministically [D:infer-wire-sanitize]" {
              let out = renderJson "T" "{\"inField\": 1, \"in\": 2}"
              Expect.stringContains out "\n    inField: int" "the clean key keeps its bare name"
              Expect.stringContains out "inField2: int" "the keyword-derived name dedupes around it"
              Expect.stringContains out "[<Wire \"in\">]" "and still carries the wire key"
          }

          test "empty-string key: DROPPED with a note; the draft checks and reads around it [D:infer-wire-sanitize]" {
              // [<Wire "">] refuses at check ('expects the wire key as a
              // string') and a field name cannot be empty — the machinery
              // cannot address an empty key, so the sanitizer drops it
              // LOUDLY and the readers' extra-key tolerance carries the rest
              match Infer.infer Parser.keywords takenBase Infer.Json "Ek" [ "{\"\": 1, \"a\": 2}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, notes) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "a: int" "the spellable key stays"
                  Expect.isFalse (out.Contains "Wire \"\"") "no empty wire key is ever drafted"
                  Expect.isFalse (out.Contains "f: int") "no orphan field is drafted for the dropped key"

                  Expect.isTrue
                      (notes |> List.exists (fun n -> n.Contains "empty-string key"))
                      "the drop is loud, never silent"

                  let script =
                      decls
                      @ [ "let v = [\"{\\\"\\\": 1, \\\"a\\\": 2}\"] |> from json Ek"
                          "if v.a <> 2 then fail \"the draft did not read around the dropped key\"" ]

                  Expect.equal (runFile script) 0 "the draft reads the sample, dropped key tolerated"
          }

          test "an object of ONLY an empty key is an open map — mapping keys are data, '' included" {
              // MOVED PIN [D:repl-infer]: the empty key is unspellable as
              // a FIELD, but a mapping's keys are data — the map detection
              // (one value shape, every key non-identifier) outranks the
              // drop rule, so nothing is dropped and nothing goes opaque
              let decls, notes =
                  Infer.inferDecls Parser.keywords takenBase "T" (Infer.IObj [ "m", Infer.IObj [ "", Infer.IInt ] ])

              Expect.stringContains (String.concat "\n" decls) "m: seq<string * int>" "the open map, value shape kept"
              Expect.isTrue (notes |> List.exists (fun n -> n.Contains "non-identifier keys")) "the map note fires"
              Expect.isFalse (notes |> List.exists (fun n -> n.Contains "empty-string key")) "no drop — the key is data"
          }

          test "the record path still goes opaque when ONLY empty keys remain and values conflict" {
              // the map detection needs ONE value shape — without it the
              // record path drops the unspellable keys and lands opaque
              let decls, notes =
                  Infer.inferDecls
                      Parser.keywords
                      takenBase
                      "T"
                      (Infer.IObj [ "m", Infer.IObj [ "", Infer.IInt; "", Infer.IStr ] ])

              Expect.stringContains (String.concat "\n" decls) "m: Yaml" "nothing spellable remains — opaque Yaml"
              Expect.isTrue (notes |> List.exists (fun n -> n.Contains "empty-string key")) "the drop note fires"
          }

          test "round-trip: inferred decls check, and 'from json' lights up" {
              match Infer.infer Parser.keywords takenBase Infer.Json "Cfg" [ "{\"host\": \"h\", \"port\": 8080}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  match checksAfterInject decls "[\"{}\"] |> from json Cfg |> _.port |> print" with
                  | Ok() -> ()
                  | Error m -> failtestf "round-trip failed: %s" m
          }

          test "top-level array names the element" {
              match Infer.infer Parser.keywords takenBase Infer.Json "User" [ "[{\"id\": 1}]" ] with
              | Ok(decls, notes) ->
                  Expect.stringContains (String.concat "\n" decls) "type User = {" "element named User"
                  Expect.isTrue (notes |> List.exists (fun n -> n.Contains "array")) "array note"
              | Error e -> failtestf "infer failed: %s" e
          }

          test "PROBE PIN: the parser eats the primitive spellings in FIELD position — a decl under one is injected-and-shadowed [D:repl-infer]" {
              // the taken set's primitive completion holds exactly because
              // `secret: Secret` in a drafted decl means the PRIMITIVE,
              // whatever the session injects under that name — if the
              // parser ever lets a session decl win here, this fails and
              // takenTypeNames must be revisited
              for prim in [ TDur; TInstant; TSize; TBytes; TSecret ] do
                  let name = formatTy prim

                  Expect.isTrue
                      (Set.contains name (Infer.takenTypeNames []))
                      $"'{name}' completes the taken set even with no live names"

                  match Parser.parseStmt $"type Probe = {{ p: {name} }}" with
                  | Result.Ok(Ast.SType decl) ->
                      match decl.Body with
                      | Ast.DRecord [ (_, ty, _) ] ->
                          Expect.equal ty prim $"'{name}' in field position is the primitive"
                      | other -> failtestf "unexpected decl body: %A" other
                  | other -> failtestf "unexpected parse: %A" other

              // the shadow is REAL end-to-end: the decl injects, then a
              // record carrying the field refuses the Secret wire crossing
              match injectBlock preludeTypeEnv "type Secret = {\n    secretName: string\n}" with
              | Error m -> failtestf "the shadowed decl still injects (probe assumption): %s" m
              | Ok env1 ->
                  match injectBlock env1 "type V = {\n    secret: Secret\n}" with
                  | Error m -> failtestf "the carrying decl checks: %s" m
                  | Ok env2 ->
                      match injectBlock env2 "let v = [\"{}\"] |> from json V" with
                      | Ok _ -> failtest "reading a shadowed 'secret: Secret' field must refuse — the builtin won the reference"
                      | Error m -> Expect.stringContains m "Secret must not cross" "the primitive wins the field use"
          }

          test "a 'secret' sub-object dodges the builtin: VolumeSecret drafted, noted, and the draft READS the sample [D:repl-infer]" {
              let sample = "{\"volumes\": [{\"name\": \"x\", \"secret\": {\"secretName\": \"s\"}}]}"

              match Infer.infer Parser.keywords takenBase Infer.Json "PodList" [ sample ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, notes) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "type VolumeSecret = {" "the colliding name parent-prefixes"
                  Expect.stringContains out "secret: VolumeSecret" "the field types as the renamed decl"
                  Expect.isFalse (out.Contains "type Secret = {") "no drafted decl lands on the builtin name"

                  Expect.isTrue
                      (notes
                       |> List.exists (fun n ->
                           n.Contains "'secret' would shadow the existing type 'Secret'"
                           && n.Contains "'VolumeSecret'"))
                      "the rename is loud, never silent"

                  let script =
                      decls
                      @ [ "let s = [\"{\\\"volumes\\\": [{\\\"name\\\": \\\"x\\\", \\\"secret\\\": {\\\"secretName\\\": \\\"s\\\"}}]}\"]"
                          "let v = s |> from json PodList"
                          "if (v.volumes |> Seq.head).secret.secretName <> \"s\" then fail \"the renamed draft did not read\"" ]

                  Expect.equal (runFile script) 0 "the drafted types read the user's sample end-to-end"
          }

          test "the yaml twin: a secret volume drafts VolumeSecret and READS through from yaml [D:repl-infer]" {
              let sample = [ "volumes:"; "- name: x"; "  secret:"; "    secretName: s" ]

              match Infer.infer Parser.keywords takenBase Infer.Yaml "PodList" sample with
              | Error e -> failtestf "yaml infer failed: %s" e
              | Ok(decls, notes) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "type VolumeSecret = {" "the shared walker renames for yaml too"
                  Expect.stringContains out "secret: VolumeSecret" "the field types as the renamed decl"
                  Expect.isTrue (notes |> List.exists (fun n -> n.Contains "would shadow")) "the note fires"

                  let script =
                      decls
                      @ [ "let s = [\"volumes:\"; \"- name: x\"; \"  secret:\"; \"    secretName: s\"]"
                          "let v = s |> from yaml PodList"
                          "if (v.volumes |> Seq.head).secret.secretName <> \"s\" then fail \"the yaml draft did not read\"" ]

                  Expect.equal (runFile script) 0 "the yaml draft reads the sample end-to-end"
          }

          test "a 'yaml' field dodges the prelude type: RootYaml drafted, and the draft checks + reads [D:repl-infer]" {
              // before the guard this died at injection — checkDecl
              // refuses a registered builtin name ('Yaml' is a built-in
              // type), so #infer errored instead of drafting
              match Infer.infer Parser.keywords takenBase Infer.Json "Root" [ "{\"yaml\": {\"a\": 1}}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, notes) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "type RootYaml = {" "parent-prefixed off the top name"
                  Expect.stringContains out "yaml: RootYaml" "the field types as the renamed decl"

                  Expect.isTrue
                      (notes
                       |> List.exists (fun n -> n.Contains "'yaml' would shadow the existing type 'Yaml'"))
                      "the note fires"

                  match checksAfterInject decls "[\"{\\\"yaml\\\": {\\\"a\\\": 1}}\"] |> from json Root |> _.yaml |> _.a |> print" with
                  | Ok() -> ()
                  | Error m -> failtestf "the renamed draft must check and read: %s" m
          }

          test "same-shape dedup survives the rename: two secret volumes share ONE VolumeSecret [D:repl-infer]" {
              let out =
                  renderJson
                      "PodList"
                      "{\"volumes\": [{\"name\": \"a\", \"secret\": {\"secretName\": \"x\"}}, {\"name\": \"b\", \"secret\": {\"secretName\": \"y\"}}]}"

              let count =
                  System.Text.RegularExpressions.Regex.Matches(out, "type VolumeSecret = \\{").Count

              Expect.equal count 1 "one renamed type, deduped"
          }

          test "array elements MERGE: a key absent in some elements drafts Option, silently [D:repl-infer]" {
              let out, notes =
                  match Infer.infer Parser.keywords takenBase Infer.Json "Root" [ "{\"items\": [{\"a\": 1}, {\"a\": 2, \"b\": \"x\"}]}" ] with
                  | Ok(decls, notes) -> String.concat "\n" decls, notes
                  | Error e -> failtestf "infer failed: %s" e

              Expect.stringContains out "a: int" "the shared key keeps its shape"
              Expect.stringContains out "b: Option<string>" "the one-sided key drafts Option"

              Expect.isFalse
                  (notes |> List.exists (fun n -> n.Contains "heterogeneous"))
                  "differing key SETS are the merge's ordinary work — no note"
          }

          test "a TYPE CONFLICT across elements keeps the first + the verify note [D:repl-infer]" {
              match Infer.infer Parser.keywords takenBase Infer.Json "Root" [ "{\"xs\": [{\"p\": 1}, {\"p\": \"s\"}]}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, notes) ->
                  Expect.stringContains (String.concat "\n" decls) "p: int" "the first element's type wins"

                  Expect.isTrue
                      (notes
                       |> List.exists (fun n ->
                           n.Contains "a heterogeneous array at 'xs.p'"
                           && n.Contains "kept the first element's type; verify the rest"))
                      "the verify note names the conflicting key's path"
          }

          test "open map (a): a majority of non-identifier keys over ONE value shape [D:repl-infer]" {
              let out, notes =
                  match Infer.infer Parser.keywords takenBase Infer.Json "Root" [ "{\"labels\": {\"k8s-app\": \"w\", \"helm.sh/chart\": \"c\", \"app\": \"x\"}}" ] with
                  | Ok(decls, notes) -> String.concat "\n" decls, notes
                  | Error e -> failtestf "infer failed: %s" e

              Expect.stringContains out "labels: seq<string * string>" "the mapping draft"
              Expect.isFalse (out.Contains "Wire") "mapping keys are data — nothing rides [<Wire>]"

              Expect.isTrue
                  (notes
                   |> List.exists (fun n ->
                       n.Contains "'labels' has mostly non-identifier keys"
                       && n.Contains "drafted as an open mapping seq<string * _>"))
                  "the map note, pinned wording"

              // NOT a map without value uniformity: the same keys over
              // mixed value shapes stay a [<Wire>]'d record
              match Infer.infer Parser.keywords takenBase Infer.Json "Root" [ "{\"labels\": {\"k8s-app\": \"w\", \"helm.sh/chart\": 2, \"app\": \"x\"}}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  Expect.stringContains (String.concat "\n" decls) "[<Wire \"k8s-app\">]" "mixed values keep the record path"
          }

          test "open map (b): sibling elements carrying DIFFERENT key sets [D:repl-infer]" {
              let sample =
                  "{\"items\": [{\"data\": {\"Corefile\": \"x\"}}, {\"data\": {\"networkYml\": \"y\", \"mode\": \"z\"}}]}"

              match Infer.infer Parser.keywords takenBase Infer.Json "CmList" [ sample ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, notes) ->
                  Expect.stringContains (String.concat "\n" decls) "data: seq<string * string>" "the mapping draft"

                  Expect.isTrue
                      (notes
                       |> List.exists (fun n ->
                           n.Contains "'items.data' carries different keys across the array's elements"
                           && n.Contains "drafted as an open mapping seq<string * _>"))
                      "the map note, pinned wording"
          }

          test "metadata zero-movement: identifier-uniform objects stay records [D:repl-infer]" {
              // identifier keys, IDENTICAL across elements — schema, never
              // a mapping, even though every value shares one shape
              let sample =
                  "{\"items\": [{\"metadata\": {\"name\": \"a\", \"namespace\": \"x\"}}, {\"metadata\": {\"name\": \"b\", \"namespace\": \"y\"}}]}"

              match Infer.infer Parser.keywords takenBase Infer.Json "PodList" [ sample ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "type Metadata = {" "metadata stays a named record"
                  Expect.stringContains out "\n    name: string" "plain required fields"
                  Expect.stringContains out "\n    namespace: string" "no Option, no mapping — zero movement"
                  Expect.isFalse (out.Contains "metadata: seq<") "never a mapping"
          }

          test "an empty {} drafts seq<string * string> and READS on both boundaries [D:yaml-empty-flow]" {
              // json draft + note
              match Infer.infer Parser.keywords takenBase Infer.Json "Spec" [ "{\"resources\": {}}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, notes) ->
                  Expect.stringContains (String.concat "\n" decls) "resources: seq<string * string>" "the empty map draft"

                  Expect.isTrue
                      (notes
                       |> List.exists (fun n ->
                           n.Contains "an empty mapping under 'Resources'"
                           && n.Contains "drafted as seq<string * string>"))
                      "the empty-map note, pinned wording"

                  // the draft READS its own sample through BOTH adapters —
                  // the opaque-Yaml draft could not cross the json boundary
                  let script =
                      decls
                      @ [ "let j = [\"{\\\"resources\\\": {}}\"] |> from json Spec"
                          "if not (j.resources |> Seq.isEmpty) then fail \"json empty {} did not read empty\""
                          "let y = [\"resources: {}\"] |> from yaml Spec"
                          "if not (y.resources |> Seq.isEmpty) then fail \"yaml empty {} did not read empty\"" ]

                  Expect.equal (runFile script) 0 "the empty-mapping draft reads via from json AND from yaml"
          }

          test "the json mapping ROUNDTRIP: an object reads as pairs, writes back as an object [D:repl-infer]" {
              let script =
                  [ "type Meta = {"
                    "    name: string"
                    "    labels: seq<string * string>"
                    "}"
                    "let m = [\"{\\\"name\\\": \\\"web\\\", \\\"labels\\\": {\\\"k8s-app\\\": \\\"w\\\", \\\"tier\\\": \\\"fe\\\"}}\"] |> from json Meta"
                    "let app = m.labels |> Seq.tryFind (fun (k, _) -> k == \"k8s-app\")"
                    "if app <> Some ((\"k8s-app\", \"w\")) then fail \"the pair did not read\""
                    "let out = m |> to json |> Seq.exactlyOne"
                    "if not (Str.contains \"\\\"labels\\\":{\\\"k8s-app\\\":\\\"w\\\"\" out) then fail $\"the mapping did not write as an object: {out}\""
                    "let rt = [out] |> from json Meta"
                    "if (rt.labels |> Seq.length) <> 2 then fail \"the roundtrip lost pairs\""
                    // the EMPTY mapping writes [] and reads back empty —
                    // the writer's own empty spelling, tolerated on read
                    "let e = [\"{\\\"name\\\": \\\"x\\\", \\\"labels\\\": {}}\"] |> from json Meta"
                    "let eout = e |> to json |> Seq.exactlyOne"
                    "if not (Str.contains \"\\\"labels\\\":[]\" eout) then fail $\"empty mapping spelling moved: {eout}\""
                    "let ert = [eout] |> from json Meta"
                    "if not (ert.labels |> Seq.isEmpty) then fail \"the empty roundtrip broke\"" ]

              Expect.equal (runFile script) 0 "the json mapping roundtrip holds, empty included"
          }

          test "the merged ConfigMapList draft reads its own sample end-to-end [D:repl-infer]" {
              let sample =
                  "{\"apiVersion\": \"v1\", \"kind\": \"ConfigMapList\", \"metadata\": {\"resourceVersion\": \"123\"}, "
                  + "\"items\": [{\"metadata\": {\"name\": \"coredns\"}, \"data\": {\"Corefile\": \".:53\"}}, "
                  + "{\"metadata\": {\"name\": \"net\", \"annotations\": {\"a.b/c\": \"1\", \"d.e/f\": \"2\"}}, \"data\": {\"network.yml\": \"nodes: 3\"}}]}"

              match Infer.infer Parser.keywords takenBase Infer.Json "CmList" [ sample ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  let out = String.concat "\n" decls
                  Expect.stringContains out "data: seq<string * string>" "the per-item data mapping"
                  Expect.stringContains out "annotations: Option<seq<string * string>>" "merged-Option annotations, itself a mapping"

                  let escaped = sample.Replace("\"", "\\\"")

                  let script =
                      decls
                      @ [ $"let v = [\"{escaped}\"] |> from json CmList"
                          "if v.metadata.resourceVersion <> \"123\" then fail \"the list metadata did not read\""
                          "let hit = v.items |> Seq.head |> _.data |> Seq.tryFind (fun (k, _) -> k == \"Corefile\")"
                          "if hit == None then fail \"Corefile did not read as a pair\"" ]

                  Expect.equal (runFile script) 0 "the merged draft reads the sample"
          }

          test "the yaml twin: the merged data mapping reads through from yaml [D:repl-infer]" {
              let sample =
                  [ "items:"
                    "- metadata:"
                    "    name: a"
                    "  data:"
                    "    Corefile: x"
                    "- metadata:"
                    "    name: b"
                    "  data:"
                    "    network.yml: y"
                    "    mode: flat" ]

              match Infer.infer Parser.keywords takenBase Infer.Yaml "CmList" sample with
              | Error e -> failtestf "yaml infer failed: %s" e
              | Ok(decls, _) ->
                  Expect.stringContains (String.concat "\n" decls) "data: seq<string * string>" "the yaml data mapping"

                  let lit = sample |> List.map (fun l -> "\"" + l + "\"") |> String.concat "; "

                  let script =
                      decls
                      @ [ $"let v = [{lit}] |> from yaml CmList"
                          "let hit = v.items |> Seq.skip 1 |> Seq.head |> _.data |> Seq.tryFind (fun (k, _) -> k == \"network.yml\")"
                          "if hit <> Some ((\"network.yml\", \"y\")) then fail \"the yaml pair did not read\"" ]

                  Expect.equal (runFile script) 0 "the yaml twin reads the merged draft"
          } ]

// #save's bare-alias qualifier [D:repl-save]: span-based, string-safe
[<Tests>]
let saveQualify =
    let r = Script.resolver preludeTypeEnv

    testList
        "save qualify"
        [ test "single-home bare aliases qualify" {
              Expect.equal
                  (Fmt.qualifyBareAliases r "[1; 2] |> map (fun n -> n + 1) |> where (fun n -> n > 1)")
                  "[1; 2] |> Seq.map (fun n -> n + 1) |> Seq.where (fun n -> n > 1)"
                  "map -> Seq.map, where -> Seq.where"
          }

          test "startsWith qualifies to Str" {
              Expect.equal
                  (Fmt.qualifyBareAliases r "[\"ab\"] |> where (startsWith \"a\")")
                  "[\"ab\"] |> Seq.where (Str.startsWith \"a\")"
                  "startsWith -> Str.startsWith"
          }

          test "a bare alias INSIDE a string literal is untouched" {
              // 'map' as data in a string must not be rewritten
              Expect.equal
                  (Fmt.qualifyBareAliases r "let s = \"map where iter\"")
                  "let s = \"map where iter\""
                  "string data untouched"
          }

          test "an already-qualified name is untouched" {
              Expect.equal
                  (Fmt.qualifyBareAliases r "[1] |> Seq.map (fun n -> n)")
                  "[1] |> Seq.map (fun n -> n)"
                  "Seq.map stays"
          }

          test "the saved round-trip CHECKS [D:repl-save]" {
              // simulate a #save output: a binding, injected type decls,
              // and a discard-wrapped bare-alias echo — then weir check it
              let saved =
                  [ "let sample = [\"{\\\"items\\\": [{\\\"name\\\": \\\"a\\\"}], \\\"count\\\": 1}\"]"
                    "type Item = {"
                    "    name: string"
                    "}"
                    "type Root = {"
                    "    items: seq<Item>"
                    "    count: int"
                    "}"
                    "let _r1 = sample |> from json Root |> _.items |> Seq.map _.name |> Seq.length" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "saved.weir" saved
              Expect.isEmpty diags (sprintf "saved script checks clean; diags: %A" diags)
          } ]

// ---- the table drafting arm [D:from-table] ----------------------------
// `#infer … from table as Name` drafts the ROW record: header offsets
// name the columns, a per-column token scan types them, empty/`<none>`
// cells draft Option with a note, and a note says the value reads as
// seq<Name>. The fixtures pad by width so header and cell offsets agree
// by construction (the tabwriter reality).

let private trow (ws: int list) (cs: string list) =
    (List.zip ws cs |> List.map (fun (w, c: string) -> c.PadRight w) |> String.concat "")
        .TrimEnd()

let private inferTable (topName: string) (lines: string list) =
    match Infer.infer Parser.keywords takenBase Infer.Table topName lines with
    | Ok(decls, notes) -> String.concat "\n" decls, notes
    | Error e -> failtestf "table infer failed: %s" e

[<Tests>]
let tableInferRules =
    testList
        "table infer rules [D:from-table]"
        [ test "headers sanitize to field names; the per-column scan types them" {
              let ws = [ 8; 22; 15; 11; 8; 8 ]

              let out, notes =
                  inferTable
                      "Pod"
                      [ trow ws [ "NAME"; "POD-TEMPLATE-HASH"; "CONTAINER ID"; "RESTARTS"; "CPU"; "READY" ]
                        trow ws [ "web-1"; "abc"; "3f4e"; "0"; "1.5"; "true" ]
                        trow ws [ "db-0"; "def"; "9a1b"; "3"; "2"; "false" ] ]

              Expect.stringContains out "type Pod = {" "the as-name is the row type"
              Expect.stringContains out "name: string" "NAME lowers to name"
              Expect.stringContains out "podTemplateHash: string" "POD-TEMPLATE-HASH camels, no Wire needed (the match recovers it)"
              Expect.stringContains out "containerId: string" "a two-word single-space header is one column"
              Expect.stringContains out "restarts: int" "all-int column -> int"
              Expect.stringContains out "cpu: float" "int-or-float tokens -> float"
              Expect.stringContains out "ready: bool" "true/false column -> bool"
              Expect.isFalse (out.Contains "[<Wire") "every header here recovers from its field name"

              Expect.exists
                  notes
                  (fun n -> n.Contains "read the value as 'seq<Pod>'")
                  "the rows-are-plural note (the bare-top-array precedent)"
          }
          test "a reserved-word header keeps the historical landing + Wire verbatim" {
              let ws = [ 8; 8 ]

              let out, _ =
                  inferTable "Svc" [ trow ws [ "NAME"; "TYPE" ]; trow ws [ "web"; "ClusterIP" ] ]

              Expect.stringContains out "[<Wire \"TYPE\">]\n    kind: string" "TYPE lands kind (toIdent's spelling) and carries the raw header"
          }
          test "empty/<none> cells draft Option with a note; an all-absent column drafts Option<string>" {
              let ws = [ 8; 8; 8 ]

              let out, notes =
                  inferTable
                      "Node"
                      [ trow ws [ "NAME"; "AGE"; "ROLES" ]
                        trow ws [ "n1"; "3"; "<none>" ]
                        trow ws [ "n2"; ""; "<none>" ] ]

              Expect.stringContains out "age: Option<int>" "one empty cell makes the int column Option"
              Expect.stringContains out "roles: Option<string>" "an all-absent column has no evidence beyond string"
              Expect.exists (notes) (fun n -> n.Contains "column 'AGE' has empty/<none> cells — drafted Option<int>") "the honesty note"
              Expect.exists (notes) (fun n -> n.Contains "column 'ROLES' has no values") "the no-evidence note"
          }
          test "a header-only sample drafts all-string with one note" {
              let out, notes = inferTable "Row" [ "NAME   AGE" ]
              Expect.stringContains out "name: string" ""
              Expect.stringContains out "age: string" ""
              Expect.exists (notes) (fun n -> n.Contains "no data rows under the header") "verify-against-a-fuller-sample"
          }
          test "the drafted type checks and its from-table read checks (the injection round-trip)" {
              let decl, _ =
                  inferTable "Pod" [ "NAME   RESTARTS"; "web-1  0" ]

              let saved =
                  [ "let sample = [\"NAME   RESTARTS\"; \"web-1  0\"]" ]
                  @ (decl.Split '\n' |> List.ofArray)
                  @ [ "let _r = sample |> from table Pod |> Seq.map (fun p -> p.name) |> Seq.length" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "drafted.weir" saved
              Expect.isEmpty diags (sprintf "the draft must check and read; diags: %A" diags)
          } ]
