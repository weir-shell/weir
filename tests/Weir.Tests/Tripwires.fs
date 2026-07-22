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
          }
          test "class constraints on env-free vars stay ambient (Session A, checklist 3.x analog)" {
              // g's parameter unifies with the enclosing lambda's y — an
              // env-free var. The Eq constraint from == must NOT be scooped
              // into g's scheme (the var is not generalized), so g stays
              // monomorphic and the second use at string errors. If this
              // stops erroring, constraint scooping over-generalized — the
              // class analog of the transitive-reachability tripwire.
              Expect.stringContains
                  (checkErr "fun y -> let g = fun x -> x == y in (g 1) && (g \"s\")").Message
                  "expected int, got string"
                  ""
          }
          test "Ord never decomposes (Session B): orderable fields do not make a record orderable" {
              // Ord is int|string|bool EXACTLY — if this stops erroring,
              // someone added structural Ord decomposition without the
              // receipts (records/unions ordering is parked, message-named)
              Expect.stringContains (checkErr "ls |> Seq.sortBy (fun f -> f)").Message "cannot sort" ""
          }
          test "() params pin unit — the generalization trap (why the checker arm exists)" {
              // desugaring () to an unconstrained fresh param would
              // generalize to forall a. a -> ... and `cleanup 5` would
              // typecheck; the ELambda \"()\" arm pins TUnit instead. If
              // this stops erroring, the arm was replaced with plain sugar.
              Expect.stringContains (checkErr "let cleanup = fun () -> 1 in cleanup 5").Message "expected unit" ""
          }
          test "binder generalization respects env-free vars (per-name scoop)" {
              // the tuple component ties to the enclosing lambda's y — its
              // var is env-free and must NOT generalize through the binder;
              // the second use at string errors. The destructuring analog
              // of the transitive-reachability tripwire.
              Expect.stringContains
                  (checkErr "fun y -> let (g, _) = ((fun x -> x == y), 1) in (g 1) && (g \"s\")").Message
                  "expected int, got string"
                  ""
          }
          test "constraint instantiation is per-use (deep-copy discipline)" {
              // first use at int succeeds; second at functions fails — if
              // constraint state were shared between instantiations, the
              // first's solution would leak into (or corrupt) the second
              Expect.stringContains
                  (checkErr "let same = fun x -> fun y -> x == y in (same 1 1) && (same print print)").Message
                  "requires equatable values"
                  ""
          }
          test "check and eval share ONE compiled regex per literal (regex-pattern arity honesty)" {
              // the arity the checker read and the instance eval matches
              // against are the same object BY CONSTRUCTION — replacing
              // the cache with per-site compilation reopens the
              // arity/match agreement question [D:regex-pattern]
              match Weir.Check.compileRegex "(x)(y)", Weir.Check.compileRegex "(x)(y)" with
              | Ok a, Ok b -> Expect.isTrue (System.Object.ReferenceEquals(a, b)) "one instance per literal"
              | _ -> failtest "compilation failed"
          } ]
