module Tests

open System.Diagnostics
open Expecto
open FsLite.Ast
open FsLite.Types
open FsLite.Check
open FsLite.Eval

let private parse input =
    match FsLite.Parser.parseExpr input with
    | Ok e -> e
    | Error msg -> failtest $"parse failed: {msg}"

let rec private show (e: Expr) : string =
    match e.Kind with
    | EInt(n, None) -> string n
    | EInt(n, Some m) -> $"{n}<{m}>"
    | EStr s -> $"\"{s}\""
    | EBool b -> if b then "true" else "false"
    | EVar x -> x
    | ELet(n, v, b) -> $"(let {n} {show v} {show b})"
    | ELambda(p, b) -> $"(fun {p} {show b})"
    | EApp(f, a) -> $"({show f} {show a})"
    | EPipe(a, f) -> $"({show a} |> {show f})"
    | EField(t, f, _) -> $"{show t}.{f}"
    | EBinOp(op, l, r) -> $"({op} {show l} {show r})"

let private expectParse input expected =
    Expect.equal (show (parse input)) expected $"parse of '{input}'"

let private fileRow =
    { Name = "FileRow"
      Fields = [ "Name", TStr; "Size", TInt(Some "mb"); "ReadOnly", TBool ] }

let private seqFile = TSeq(TRecord "FileRow")

let private env =
    { Values =
        Map
            [ "ls", seqFile
              "where", TFun(TFun(TRecord "FileRow", TBool), TFun(seqFile, seqFile))
              "first", TFun(TInt None, TFun(seqFile, seqFile))
              "double", TFun(TInt None, TInt None) ]
      Types = Map [ "FileRow", fileRow ] }

let private checkOk input =
    match typecheck env (parse input) with
    | Ok te -> te
    | Error terr -> failtest $"expected Ok, got: {formatError terr}"

let private checkErr input =
    match typecheck env (parse input) with
    | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
    | Error terr -> terr

let private run input = eval builtins (parse input)

let private expectValue input expected =
    Expect.equal (run input) expected $"eval of '{input}'"

let acceptance = "ls | where (fun f -> f.Size > 1<mb>) | first 5"

let parserTests =
    testList
        "Parser"
        [ test "binop and pipe" { expectParse "1 + 2 |> double" "((+ 1 2) |> double)" }
          test "application is left-assoc" { expectParse "f x y" "((f x) y)" }
          test "precedence" { expectParse "1 + 2 * 3" "(+ 1 (* 2 3))" }
          test "comparison binds looser than plus" { expectParse "1 + 2 > 2" "(> (+ 1 2) 2)" }
          test "lambda body extends right" { expectParse "fun x -> x + 1" "(fun x (+ x 1))" }
          test "let-in" { expectParse "let x = 1 in x" "(let x 1 x)" }
          test "field access chains" { expectParse "f.Size > 1<mb>" "(> f.Size 1<mb>)" }
          test "shell pipe is pipe" { expectParse "ls | where p | first 5" "((ls |> (where p)) |> (first 5))" }
          test "measure literal" { expectParse "1<mb> + 2<mb>" "(+ 1<mb> 2<mb>)" }
          test "less-than without space is not a measure" { expectParse "1<2" "(< 1 2)" }
          test "leaf span is exact" {
              let e = parse "  double  "
              Expect.equal e.Span.Start.Col 3 "start"
              Expect.equal e.Span.End.Col 9 "end"
          }
          test "top-level let statement" {
              match FsLite.Parser.parseStmt "let x = 1" with
              | Ok(SLet("x", _)) -> ()
              | other -> failtest $"unexpected: {other}"
          } ]

let checkerTests =
    testList
        "Check"
        [ test "acceptance pipeline type-checks to seq<FileRow>" { Expect.equal (checkOk acceptance).Ty seqFile "" }
          test "typo in field is rejected with exact span and a hint" {
              let input = "ls | where (fun f -> f.Sze > 1<mb>) | first 5"
              let terr = checkErr input
              let expectedStart = input.IndexOf "Sze" + 1
              Expect.equal terr.Span.Start.Col expectedStart "start col"
              Expect.equal terr.Span.End.Col (expectedStart + 3) "end col"
              Expect.stringContains terr.Message "FileRow has no field 'Sze'" ""
              Expect.stringContains terr.Message "Did you mean 'Size'?" ""
          }
          test "lambda body of wrong type reports expected vs actual" {
              let terr = checkErr "ls | where (fun f -> f.Size) | first 5"
              Expect.stringContains terr.Message "expected bool, got int<mb>" ""
          }
          test "measure mismatch in comparison" {
              let input = "ls | where (fun f -> f.Size > 1) | first 5"
              let terr = checkErr input
              Expect.stringContains terr.Message "expected int<mb>, got int" ""
              Expect.equal terr.Span.Start.Col (input.IndexOf "1)" + 1) "span points at the bare 1"
          }
          test "same measures add" { Expect.equal (checkOk "1<mb> + 2<mb>").Ty (TInt(Some "mb")) "" }
          test "different measures do not add" {
              Expect.stringContains (checkErr "1<mb> + 2<gb>").Message "expected int<mb>, got int<gb>" ""
          }
          test "measureless int does not add to measured" {
              Expect.stringContains (checkErr "1<mb> + 2").Message "expected int<mb>, got int" ""
          }
          test "unbound variable gets a hint" {
              Expect.stringContains (checkErr "doble 5").Message "Did you mean 'double'?" ""
          }
          test "bare lambda cannot be inferred" {
              Expect.stringContains (checkErr "fun x -> x").Message "cannot infer" ""
          }
          test "lambda applied to a known argument infers" {
              Expect.equal (checkOk "(fun x -> x + 1) 41").Ty (TInt None) ""
          }
          test "pipe into lambda infers" { Expect.equal (checkOk "5 |> fun x -> x * x").Ty (TInt None) "" }
          test "let body type is the expression type" {
              Expect.equal (checkOk "let x = 5 in x |> double").Ty (TInt None) ""
          }
          test "applying a non-function is rejected" {
              Expect.stringContains (checkErr "5 |> 3").Message "must be a function" ""
          }
          test "field on a non-record is rejected" {
              Expect.stringContains (checkErr "let x = 1 in x.foo").Message "only records have fields" ""
          }
          test "string concat types" { Expect.equal (checkOk "\"a\" + \"b\"").Ty TStr "" }
          test "equality on bools" { Expect.equal (checkOk "true == false").Ty TBool "" }
          test "checks a line in well under 10ms" {
              let e = parse acceptance
              typecheck env e |> ignore
              let sw = Stopwatch.StartNew()

              for _ in 1..200 do
                  typecheck env e |> ignore

              sw.Stop()
              let avgMs = float sw.ElapsedMilliseconds / 200.0
              Expect.isLessThan avgMs 10.0 $"average check time {avgMs}ms"
          } ]

let evalTests =
    testList
        "Eval"
        [ test "acceptance: 1 + 2 |> double" { expectValue "1 + 2 |> double" (VInt 6) }
          test "precedence: 1 + 2 * 3" { expectValue "1 + 2 * 3" (VInt 7) }
          test "parens override precedence" { expectValue "(1 + 2) * 3" (VInt 9) }
          test "pipe chain" { expectValue "1 + 2 |> double |> double" (VInt 12) }
          test "pipe into lambda" { expectValue "5 |> fun x -> x * x" (VInt 25) }
          test "let-in" { expectValue "let x = 5 in x * 2" (VInt 10) }
          test "lambda application" { expectValue "(fun x -> x + 1) 41" (VInt 42) }
          test "closure captures environment" {
              expectValue "let add = fun a -> fun b -> a + b in let add5 = add 5 in add5 37" (VInt 42)
          }
          test "shadowing" { expectValue "let x = 1 in let x = 2 in x" (VInt 2) }
          test "string concat" { expectValue "\"foo\" + \"bar\"" (VStr "foobar") }
          test "comparison" { expectValue "2 > 1" (VBool true) }
          test "measure is erased at runtime" { expectValue "1<mb> + 2<mb>" (VInt 3) }
          test "unbound variable fails" { Expect.throws (fun () -> run "nope" |> ignore) "" }
          test "applying a non-function fails" { Expect.throws (fun () -> run "1 2" |> ignore) "" } ]

[<Tests>]
let allTests = testList "FsLite" [ parserTests; checkerTests; evalTests ]
