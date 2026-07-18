module DataRange

// Part 3 of PLAN-read-booleans-overflow: the permanent data-range layer.
// Boundaries of int64, big strings, laziness under huge sources.

open Expecto
open Weir.Eval

let private prelude, private preludeVals =
    Weir.Prelude.extend Weir.Builtins.typeEnv Weir.Builtins.valueEnv

let private run input =
    match Weir.Parser.parseExpr input with
    | Error msg -> failtest $"parse failed: {msg}"
    | Ok e ->
        match Weir.Check.typecheck prelude e with
        | Error terr -> failtest (Weir.Check.formatError terr)
        | Ok te -> eval preludeVals te

let private runErr input =
    match Weir.Parser.parseExpr input with
    | Error msg -> msg
    | Ok e ->
        match Weir.Check.typecheck prelude e with
        | Error terr -> Weir.Check.formatError terr
        | Ok te ->
            try
                eval preludeVals te |> ignore
                failtest "expected a failure"
            with ex ->
                ex.Message

[<Tests>]
let dataRangeTests =
    testList
        "Data range"
        [ test "int64 boundaries are literal-reachable" {
              Expect.equal (run "9223372036854775807") (VInt System.Int64.MaxValue) "max"
              Expect.equal (run "0 - 9223372036854775807") (VInt(-System.Int64.MaxValue)) "near min"
          }
          test "arithmetic overflow raises, never wraps" {
              Expect.stringContains (runErr "9223372036854775807 + 1") "integer overflow" "+"
              Expect.stringContains (runErr "(0 - 9223372036854775807) - 2") "integer overflow" "-"
              Expect.stringContains (runErr "4611686018427387904 * 2") "integer overflow" "*"
          }
          test "sum overflow raises with its own name" {
              Expect.stringContains (runErr "[9223372036854775807; 1] |> Seq.sum") "overflow in sum" ""
          }
          test "literals beyond 64 bits are parse errors" {
              Expect.stringContains (runErr "99999999999999999999") "out of range (64-bit)" ""
          }
          test "gigabyte-scale arithmetic has headroom" {
              Expect.equal (run "3000000000 * 3") (VInt 9000000000L) "3GB x 3"
          }
          test "a range touching Int64.Max terminates" {
              Expect.equal (run "[9223372036854775805..9223372036854775807] |> Seq.length") (VInt 3L) ""
              Expect.equal (run "[9223372036854775807..9223372036854775807] |> Seq.length") (VInt 1L) ""
          }
          test "division edge: Int64.Min / -1 raises rather than wraps" {
              Expect.stringContains
                  (runErr "((0 - 9223372036854775807) - 1) / (0 - 1)")
                  "overflow"
                  "the one division overflow"
          }
          test "megabyte strings survive the Str pipeline" {
              let v = run "\"ab\" |> Str.replace \"b\" \"a\" |> Str.length"

              Expect.equal v (VInt 2L) "sanity"

              match run "[1..500000] |> Seq.map (fun i -> \"xy\") |> Str.join \"\" |> Str.length" with
              | VInt n -> Expect.equal n 1000000L "1MB string built and measured"
              | v -> failtest $"unexpected {formatValue v}"
          }
          test "laziness under huge sources" {
              Expect.equal (run "[1..1000000000] |> Seq.first 2 |> Seq.length") (VInt 2L) "billion-range"
          } ]
