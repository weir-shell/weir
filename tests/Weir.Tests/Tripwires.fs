module Tripwires

// Tests named for INCIDENTAL protections: each pins a mechanism that currently
// shields a soundness-checklist item. If a test here fails because you changed
// the named mechanism on purpose, the referenced checklist item REOPENS —
// write direct tests for it before proceeding.

open Expecto
open Weir.Types
open Weir.Check

let private env = Weir.Builtins.typeEnv

let private checkErr input =
    match Weir.Parser.parseExpr input with
    | Error msg -> failtest $"parse failed: {msg}"
    | Ok e ->
        match typecheck env e with
        | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
        | Error terr -> terr

[<Tests>]
let tripwires =
    testList
        "Tripwires"
        [ test "occurs check is shielded by funParams (checklist 1.1)" {
              // Language rule, not accident: weir never unifies a type
              // variable with a function type at application, so un-annotated
              // higher-order lambdas do not infer (HOFs flow from typed
              // builtins). This same rule blocks the standard occurs-check
              // cycle constructions before `occurs` is consulted. Adding
              // arrow-var unification (higher-order inference) reopens 1.1:
              // add direct cyclic-row/occurs tests first.
              Expect.stringContains (checkErr "fun f -> f 1").Message "not a function" ""
              Expect.stringContains (checkErr "fun f -> f.x f").Message "not a function" ""
          }
          test "no unit algebra means no normalization question (checklist 4.2)" {
              // Measures are nominal tags (string option) with no arithmetic:
              // scalar*measure and measure*measure are rejected, so no unit
              // representation exists to (mis)normalize. Adding measure algebra
              // (top of the backlog: scalar-times-measure, same-measure sum)
              // reopens 4.2: unit equality must become normalization-based,
              // never structural comparison of the representation.
              Expect.stringContains (checkErr "ls |> map (fun f -> f.Size * 2)").Message "expected int<mb>, got int" ""
              Expect.stringContains (checkErr "1<mb> * 1<mb>").Message "'*' is not defined for int<mb>" ""
          }
          test "no annotation syntax means no trust boundary (checklist 2.3)" {
              // There is no type-ascription syntax, so an annotation cannot
              // launder a row constraint. The day ascription lands, 2.3
              // reopens: an annotation must re-verify (check mode), never
              // relabel.
              match Weir.Parser.parseExpr "(5 : int)" with
              | Error _ -> ()
              | Ok _ -> failtest "ascription unexpectedly parses; checklist 2.3 is live"
          }
          test "envFreeVars reaches vars transitively through row constraints (checklist 3.x)" {
              // 'g' below has type ('a -> bool) -> seq<'a> where 'a occurs in
              // the enclosing parameter y's type ONLY inside y's row
              // constraints (y itself is a bare var). Generalizing 'a at the
              // let would let the two uses instantiate independently — unsound.
              // envFreeVars avoids this because it expands rows via finalTy
              // before collecting vars. If this stops erroring, transitive
              // reachability was lost.
              Expect.stringContains
                  (checkErr
                      "fun y -> let g = fun p -> y.Kids |> where p in let u1 = g (fun n -> n > 1) in let u2 = g (fun s -> s == \"a\") in 0")
                      .Message
                  "expected int, got string"
                  ""
          }
          test "instantiate deep-copies row constraints per use site (checklist 3.1)" {
              // A generalized scheme's row snapshot is immutable; instantiate
              // renames every quantified var (rows included) and installs a
              // FRESH Rows entry per use. If instantiations ever alias a shared
              // Rows entry, discharging one use would poison its siblings and
              // this reuse at two different field types would fail.
              let declare input e =
                  match Weir.Parser.parseStmt input with
                  | Ok(Weir.Ast.SType d) ->
                      match checkDecl e d with
                      | Ok e' -> e'
                      | Error terr -> failtest (formatError terr)
                  | _ -> failtest "expected a declaration"

              let e2 =
                  env
                  |> declare "type IntV = { V: int; Tag: bool }"
                  |> declare "type StrV = { V: string; Alt: bool }"

              let expr =
                  "let getV = map _.V in "
                  + "let a = nats |> take 1 |> map (fun n -> { V = n; Tag = true }) |> getV in "
                  + "let b = nats |> take 1 |> map (fun n -> { V = \"s\"; Alt = true }) |> getV in "
                  + "0"

              match Weir.Parser.parseExpr expr with
              | Error msg -> failtest msg
              | Ok e ->
                  match typecheck e2 e with
                  | Ok _ -> ()
                  | Error terr -> failtest $"sibling instantiations interfered: {formatError terr}"
          } ]
