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
    | ERecord fields ->
        let body =
            fields |> List.map (fun (n, _, v) -> $"{n} = {show v}") |> String.concat "; "

        "{" + body + "}"
    | EMatch(scrut, arms) ->
        let showArm (p, b) = $"[{showPat p} -> {show b}]"
        let armsStr = arms |> List.map showArm |> String.concat " "
        $"(match {show scrut} {armsStr})"

and private showPat (p: Pattern) : string =
    match p.PKind with
    | PWildcard -> "_"
    | PVar x -> x
    | PCase(c, None) -> c
    | PCase(c, Some arg) -> $"({c} {showPat arg})"

let private expectParse input expected =
    Expect.equal (show (parse input)) expected $"parse of '{input}'"

let private parseDecl input =
    match FsLite.Parser.parseStmt input with
    | Ok(SType d) -> d
    | other -> failtest $"expected a type declaration, got: {other}"

let private declare input env =
    match FsLite.Check.checkDecl env (parseDecl input) with
    | Ok env' -> env'
    | Error terr -> failtest $"declaration failed: {formatError terr}"

let private declErr input env =
    match FsLite.Check.checkDecl env (parseDecl input) with
    | Ok _ -> failtest "expected the declaration to be rejected"
    | Error terr -> terr

let private env =
    FsLite.Builtins.typeEnv
    |> declare "type Proc = Running of int | Stopped"
    |> declare "type Point = { X: int; Y: int }"

let private ctorValues =
    [ "type Proc = Running of int | Stopped" ]
    |> List.collect (fun d ->
        match (parseDecl d).Body with
        | DUnion cases -> constructorValues cases
        | DRecord _ -> [])

let private fakeFiles =
    [ FsLite.Builtins.file "a.txt" 0 false
      FsLite.Builtins.file "b.bin" 5 true
      FsLite.Builtins.file "c.log" 1 false
      FsLite.Builtins.file "d.iso" 3 false ]

let private valueEnv =
    ("ls", VSeq fakeFiles) :: ctorValues
    |> List.fold (fun vs (n, v) -> Map.add n v vs) FsLite.Builtins.valueEnv

let private checkOk input =
    match typecheck env (parse input) with
    | Ok te -> te
    | Error terr -> failtest $"expected Ok, got: {formatError terr}"

let private checkErr input =
    match typecheck env (parse input) with
    | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
    | Error terr -> terr

let private run input = eval valueEnv (checkOk input)

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
        [ test "acceptance pipeline type-checks to seq<FileRow>" {
              Expect.equal (checkOk acceptance).Ty FsLite.Builtins.seqFileRow ""
          }
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
        [ test "acceptance pipeline evaluates over records" {
              expectValue
                  acceptance
                  (VSeq [ FsLite.Builtins.file "b.bin" 5 true; FsLite.Builtins.file "d.iso" 3 false ])
          }
          test "where by string field" {
              expectValue "ls | where (fun f -> f.Name == \"c.log\")" (VSeq [ FsLite.Builtins.file "c.log" 1 false ])
          }
          test "where by bool field" {
              expectValue "ls | where (fun f -> f.ReadOnly)" (VSeq [ FsLite.Builtins.file "b.bin" 5 true ])
          }
          test "first truncates" {
              expectValue
                  "ls | first 2"
                  (VSeq [ FsLite.Builtins.file "a.txt" 0 false; FsLite.Builtins.file "b.bin" 5 true ])
          }
          test "arithmetic and pipes: 1 + 2 |> double" { expectValue "1 + 2 |> double" (VInt 6) }
          test "precedence: 1 + 2 * 3" { expectValue "1 + 2 * 3" (VInt 7) }
          test "pipe chain" { expectValue "1 + 2 |> double |> double" (VInt 12) }
          test "pipe into lambda" { expectValue "5 |> fun x -> x * x" (VInt 25) }
          test "let-in" { expectValue "let x = 5 in x * 2" (VInt 10) }
          test "lambda application" { expectValue "(fun x -> x + 1) 41" (VInt 42) }
          test "closure captures environment" { expectValue "let y = 40 in (fun x -> x + y) 2" (VInt 42) }
          test "partially applied builtin is first-class" {
              expectValue
                  "let staged = where (fun f -> f.ReadOnly) in ls | staged"
                  (VSeq [ FsLite.Builtins.file "b.bin" 5 true ])
          }
          test "shadowing" { expectValue "let x = 1 in let x = 2 in x" (VInt 2) }
          test "string concat" { expectValue "\"foo\" + \"bar\"" (VStr "foobar") }
          test "comparison" { expectValue "2 > 1" (VBool true) }
          test "measure is erased at runtime" { expectValue "1<mb> + 2<mb>" (VInt 3) } ]

let rejectedAtCheckTests =
    testList
        "Rejected at check time, never at eval"
        [ test "unbound variable" { checkErr "nope" |> ignore }
          test "applying a non-function" { checkErr "1 2" |> ignore }
          test "string plus int" { checkErr "\"a\" + 1" |> ignore }
          test "field typo in a pipeline" { checkErr "ls | where (fun f -> f.Sze > 1<mb>)" |> ignore }
          test "wrong argument to a builtin" { checkErr "double \"x\"" |> ignore }
          test "piping a seq into an int function" { checkErr "ls | double" |> ignore }
          test "let-bound bare lambda cannot infer without annotations" {
              checkErr "let add = fun a -> fun b -> a + b in add 1 2" |> ignore
          } ]

let private warningsOf input =
    FsLite.Check.warnings env (checkOk input)

let declTests =
    testList
        "Type declarations"
        [ test "union declares constructors as typed values" {
              Expect.equal (checkOk "Running 5").Ty (TNamed "Proc") "payload ctor applies"
              Expect.equal (checkOk "Stopped").Ty (TNamed "Proc") "nullary ctor is a value"
          }
          test "constructor payload is checked" {
              Expect.stringContains (checkErr "Running \"x\"").Message "expected int, got string" ""
          }
          test "constructing evaluates to a union value" {
              expectValue "Running (2 + 3)" (VUnion("Running", Some(VInt 5)))
              expectValue "Stopped" (VUnion("Stopped", None))
          }
          test "record literal finds its nominal type" {
              Expect.equal (checkOk "{ X = 1; Y = 2 }").Ty (TNamed "Point") ""
          }
          test "record literal evaluates and fields project" {
              expectValue "{ X = 1; Y = 2 }" (VRecord("Point", Map [ "X", VInt 1; "Y", VInt 2 ]))
              expectValue "let p = { X = 3; Y = 4 } in p.X + p.Y" (VInt 7)
          }
          test "record literal with unknown field set is rejected" {
              Expect.stringContains (checkErr "{ X = 1; Z = 2 }").Message "no declared record" ""
          }
          test "record literal field values are checked" {
              Expect.stringContains (checkErr "{ X = 1; Y = \"two\" }").Message "expected int, got string" ""
          }
          test "duplicate field in a literal is rejected" {
              Expect.stringContains (checkErr "{ X = 1; X = 2 }").Message "duplicate field 'X'" ""
          }
          test "ambiguous record literal is rejected" {
              let ambEnv = env |> declare "type Vec = { X: int; Y: int }"

              match FsLite.Check.typecheck ambEnv (parse "{ X = 1; Y = 2 }") with
              | Ok _ -> failtest "expected ambiguity error"
              | Error terr -> Expect.stringContains terr.Message "ambiguous" ""
          }
          test "declaration referencing an unknown type is rejected" {
              let terr = declErr "type Bad = { F: Missing }" env
              Expect.stringContains terr.Message "unknown type 'Missing'" ""
          }
          test "self-recursive union declares" {
              let treeEnv = env |> declare "type Tree = Leaf | Node of Tree"
              let e = parse "Node (Node Leaf)"

              match FsLite.Check.typecheck treeEnv e with
              | Ok te -> Expect.equal te.Ty (TNamed "Tree") ""
              | Error terr -> failtest (formatError terr)
          }
          test "duplicate cases are rejected" {
              let terr = declErr "type Bad = A | A" env
              Expect.stringContains terr.Message "duplicate case 'A'" ""
          }
          test "redeclaration is allowed" { env |> declare "type Proc = Running of int | Stopped" |> ignore } ]

let matchTests =
    testList
        "Match"
        [ test "match on a union types and evaluates" {
              expectValue "match Running 5 with | Running n -> n | Stopped -> 0" (VInt 5)
              expectValue "match Stopped with | Running n -> n | Stopped -> 0" (VInt 0)
          }
          test "wildcard and variable arms" {
              expectValue "match Running 5 with | Stopped -> 0 | _ -> 9" (VInt 9)
              expectValue "match Stopped with | p -> p" (VUnion("Stopped", None))
          }
          test "nested constructor patterns" {
              let treeEnv = env |> declare "type Tree = Leaf | Node of Tree"
              let e = parse "match Node Leaf with | Node Leaf -> 1 | Node _ -> 2 | Leaf -> 0"

              match FsLite.Check.typecheck treeEnv e with
              | Ok te -> Expect.equal te.Ty (TInt None) ""
              | Error terr -> failtest (formatError terr)
          }
          test "arm bodies must agree on type" {
              Expect.stringContains
                  (checkErr "match Stopped with | Running n -> n | Stopped -> \"zero\"").Message
                  "expected int, got string"
                  ""
          }
          test "wrong constructor for the scrutinee type" {
              let terr = checkErr "match Running 1 with | Leaf -> 0 | _ -> 1"
              Expect.stringContains terr.Message "Proc has no case 'Leaf'" ""
          }
          test "constructor typo gets a hint" {
              Expect.stringContains
                  (checkErr "match Stopped with | Runing n -> n | Stopped -> 0").Message
                  "Did you mean 'Running'?"
                  ""
          }
          test "payload pattern is required" {
              Expect.stringContains
                  (checkErr "match Running 1 with | Running -> 1 | Stopped -> 0").Message
                  "carries int; add a pattern"
                  ""
          }
          test "no payload pattern on a nullary case" {
              Expect.stringContains
                  (checkErr "match Stopped with | Stopped n -> 1 | Running x -> x").Message
                  "'Stopped' has no payload"
                  ""
          }
          test "match result pipes onward" {
              expectValue "match Running 5 with | Running n -> n | Stopped -> 0 | double" (VInt 10)
          }
          test "match binds tighter than pipe in arm bodies" {
              expectParse
                  "match p with | Running n -> n | Stopped -> 0 | double"
                  "((match p [(Running n) -> n] [Stopped -> 0]) |> double)"
          }
          test "matching a record scrutinee against constructors is rejected" {
              Expect.stringContains
                  (checkErr "match { X = 1; Y = 2 } with | Running n -> n | _ -> 0").Message
                  "Point is a record"
                  ""
          } ]

let warningTests =
    testList
        "Exhaustiveness warnings"
        [ test "missing case warns" {
              let ws = warningsOf "match Running 5 with | Running n -> n"
              Expect.hasLength ws 1 ""
              Expect.stringContains ws[0].Message "missing: Stopped" ""
          }
          test "all cases covered does not warn" {
              Expect.isEmpty (warningsOf "match Running 5 with | Running n -> n | Stopped -> 0") ""
          }
          test "wildcard covers everything" { Expect.isEmpty (warningsOf "match Running 5 with | _ -> 0") "" }
          test "arm after a catch-all warns as unreachable" {
              let ws = warningsOf "match Running 5 with | _ -> 0 | Stopped -> 1"
              Expect.hasLength ws 1 ""
              Expect.stringContains ws[0].Message "unreachable" ""
          }
          test "match on a non-union needs a catch-all" {
              let ws = warningsOf "match 5 with | n -> n"
              Expect.isEmpty ws "variable arm is a catch-all"
              let ws2 = warningsOf "match Running 5 with | Running n -> n"
              Expect.hasLength ws2 1 ""
          }
          test "non-exhaustive match still evaluates when an arm hits" {
              expectValue "match Running 5 with | Running n -> n" (VInt 5)
          }
          test "non-exhaustive match fails at runtime when no arm hits" {
              Expect.throws (fun () -> run "match Stopped with | Running n -> n" |> ignore) ""
          } ]

[<Tests>]
let allTests =
    testList
        "FsLite"
        [ parserTests
          checkerTests
          evalTests
          rejectedAtCheckTests
          declTests
          matchTests
          warningTests ]
