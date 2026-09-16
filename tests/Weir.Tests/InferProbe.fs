module Weir.Tests.InferProbe

open Expecto
open Weir
open Weir.Types

// PROBES [D:repl-infer]: the four unknowns the plan demands answered
// before building — kept as living tests.

let private preludeTypeEnv, _preludeValueEnv =
    Weir.Prelude.extend Weir.Builtins.typeEnv Weir.Builtins.valueEnv

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
              let _, n1 = Infer.inferDecls "T" (Infer.IObj [ "xs", Infer.IArr [] ])
              Expect.isTrue (n1 |> List.exists (fun n -> n.Contains "empty array")) "empty-array note"
              // null field
              let _, n2 = Infer.inferDecls "T" (Infer.IObj [ "v", Infer.INull ])
              Expect.isTrue (n2 |> List.exists (fun n -> n.Contains "null value")) "null note"
              // heterogeneous array
              let het =
                  Infer.IObj
                      [ "xs", Infer.IArr [ Infer.IObj [ "a", Infer.IInt ]; Infer.IObj [ "b", Infer.IStr ] ] ]

              let _, n3 = Infer.inferDecls "T" het
              Expect.isTrue (n3 |> List.exists (fun n -> n.Contains "heterogeneous")) "heterogeneous note"
          } ]

// render the inferred declarations to ONE string for substring pins
let private renderJson (topName: string) (json: string) : string =
    match Infer.infer Infer.Json topName [ json ] with
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

          test "round-trip: inferred decls check, and 'from json' lights up" {
              match Infer.infer Infer.Json "Cfg" [ "{\"host\": \"h\", \"port\": 8080}" ] with
              | Error e -> failtestf "infer failed: %s" e
              | Ok(decls, _) ->
                  match checksAfterInject decls "[\"{}\"] |> from json Cfg |> _.port |> print" with
                  | Ok() -> ()
                  | Error m -> failtestf "round-trip failed: %s" m
          }

          test "top-level array names the element" {
              match Infer.infer Infer.Json "User" [ "[{\"id\": 1}]" ] with
              | Ok(decls, notes) ->
                  Expect.stringContains (String.concat "\n" decls) "type User = {" "element named User"
                  Expect.isTrue (notes |> List.exists (fun n -> n.Contains "array")) "array note"
              | Error e -> failtestf "infer failed: %s" e
          } ]
