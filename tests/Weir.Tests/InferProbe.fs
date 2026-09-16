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
