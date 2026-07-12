module Tests

open System.Diagnostics
open System.IO
open Expecto
open Weir.Ast
open Weir.Types
open Weir.Check
open Weir.Eval

let private parse input =
    match Weir.Parser.parseExpr input with
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
    | EFrom(fmt, None) -> $"(from {fmt})"
    | EFrom(fmt, Some ty) -> $"(from {fmt} {ty})"
    | ETo fmt -> $"(to {fmt})"
    | EList items ->
        let body = items |> List.map show |> String.concat "; "
        $"[{body}]"

and private showPat (p: Pattern) : string =
    match p.PKind with
    | PWildcard -> "_"
    | PVar x -> x
    | PCase(c, None) -> c
    | PCase(c, Some arg) -> $"({c} {showPat arg})"

let private expectParse input expected =
    Expect.equal (show (parse input)) expected $"parse of '{input}'"

let private parseDecl input =
    match Weir.Parser.parseStmt input with
    | Ok(SType d) -> d
    | other -> failtest $"expected a type declaration, got: {other}"

let private declare input env =
    match Weir.Check.checkDecl env (parseDecl input) with
    | Ok env' -> env'
    | Error terr -> failtest $"declaration failed: {formatError terr}"

let private declErr input env =
    match Weir.Check.checkDecl env (parseDecl input) with
    | Ok _ -> failtest "expected the declaration to be rejected"
    | Error terr -> terr

let private env =
    let e =
        Weir.Builtins.typeEnv
        |> declare "type Proc = Running of int | Stopped"
        |> declare "type Point = { X: int; Y: int }"

    { e with
        Values = Map.add "src" (generalize (TSeq TStr)) e.Values }

let private ctorValues =
    [ "type Proc = Running of int | Stopped" ]
    |> List.collect (fun d ->
        match (parseDecl d).Body with
        | DUnion cases -> constructorValues cases
        | DRecord _ -> [])

let private fakeFiles =
    [ Weir.Builtins.file "a.txt" 0 false
      Weir.Builtins.file "b.bin" 5 true
      Weir.Builtins.file "c.log" 1 false
      Weir.Builtins.file "d.iso" 3 false ]

let private valueEnv =
    ("ls", VSeq fakeFiles) :: ctorValues
    |> List.fold (fun vs (n, v) -> Map.add n v vs) Weir.Builtins.valueEnv

let private checkOk input =
    match typecheck env (parse input) with
    | Ok te -> te
    | Error terr -> failtest $"expected Ok, got: {formatError terr}"

let private checkErr input =
    match typecheck env (parse input) with
    | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
    | Error terr -> terr

let private runWith (overrides: (string * Value) list) input =
    let env = overrides |> List.fold (fun vs (n, v) -> Map.add n v vs) valueEnv
    eval env (checkOk input)

let private run input = eval valueEnv (checkOk input)

let private forceSeq v =
    match v with
    | VSeq items -> List.ofSeq items
    | v -> failtest $"expected a seq, got {formatValue v}"

let private expectValue input expected =
    Expect.equal (run input) expected $"eval of '{input}'"

let acceptance = "ls |> where (fun f -> f.Size > 1<mb>) |> first 5"

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
          test "shell pipe is pipe" { expectParse "ls |> where p |> first 5" "((ls |> (where p)) |> (first 5))" }
          test "measure literal" { expectParse "1<mb> + 2<mb>" "(+ 1<mb> 2<mb>)" }
          test "less-than without space is not a measure" { expectParse "1<2" "(< 1 2)" }
          test "leaf span is exact" {
              let e = parse "  double  "
              Expect.equal e.Span.Start.Col 3 "start"
              Expect.equal e.Span.End.Col 9 "end"
          }
          test "top-level let statement" {
              match Weir.Parser.parseStmt "let x = 1" with
              | Ok(SLet("x", _)) -> ()
              | other -> failtest $"unexpected: {other}"
          } ]

let checkerTests =
    testList
        "Check"
        [ test "acceptance pipeline type-checks to seq<FileRow>" {
              Expect.equal (checkOk acceptance).Ty Weir.Builtins.seqFileRow ""
          }
          test "typo in field is rejected with exact span and a hint" {
              let input = "ls |> where (fun f -> f.Sze > 1<mb>) |> first 5"
              let terr = checkErr input
              let expectedStart = input.IndexOf "Sze" + 1
              Expect.equal terr.Span.Start.Col expectedStart "start col"
              Expect.equal terr.Span.End.Col (expectedStart + 3) "end col"
              Expect.stringContains terr.Message "FileRow has no field 'Sze'" ""
              Expect.stringContains terr.Message "Did you mean 'Size'?" ""
          }
          test "lambda body of wrong type reports expected vs actual" {
              let terr = checkErr "ls |> where (fun f -> f.Size) |> first 5"
              Expect.stringContains terr.Message "expected bool, got int<mb>" ""
          }
          test "measure mismatch in comparison" {
              let input = "ls |> where (fun f -> f.Size > 1) |> first 5"
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
          test "bare lambda infers a polymorphic type" {
              match (checkOk "fun x -> x").Ty with
              | TFun(TVar a, TVar b) when a = b -> ()
              | t -> failtest $"expected 'a -> 'a, got {formatTy t}"
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
              expectValue acceptance (VSeq [ Weir.Builtins.file "b.bin" 5 true; Weir.Builtins.file "d.iso" 3 false ])
          }
          test "where by string field" {
              expectValue "ls |> where (fun f -> f.Name == \"c.log\")" (VSeq [ Weir.Builtins.file "c.log" 1 false ])
          }
          test "where by bool field" {
              expectValue "ls |> where (fun f -> f.ReadOnly)" (VSeq [ Weir.Builtins.file "b.bin" 5 true ])
          }
          test "first truncates" {
              expectValue
                  "ls |> first 2"
                  (VSeq [ Weir.Builtins.file "a.txt" 0 false; Weir.Builtins.file "b.bin" 5 true ])
          }
          test "arithmetic and pipes: 1 + 2 |> double" { expectValue "1 + 2 |> double" (VInt 6) }
          test "precedence: 1 + 2 * 3" { expectValue "1 + 2 * 3" (VInt 7) }
          test "pipe chain" { expectValue "1 + 2 |> double |> double" (VInt 12) }
          test "pipe into lambda" { expectValue "5 |> fun x -> x * x" (VInt 25) }
          test "let-in" { expectValue "let x = 5 in x * 2" (VInt 10) }
          test "lambda application" { expectValue "(fun x -> x + 1) 41" (VInt 42) }
          test "closure captures environment" { expectValue "let y = 40 in (fun x -> x + y) 2" (VInt 42) }
          test "partially applied polymorphic builtin stays polymorphic" {
              expectValue
                  "let firstTwo = first 2 in ls |> firstTwo |> where (fun f -> f.ReadOnly)"
                  (VSeq [ Weir.Builtins.file "b.bin" 5 true ])
          }
          test "lambda in polymorphic position without data now checks via rows" {
              expectValue
                  "let staged = where (fun f -> f.ReadOnly) in ls |> staged"
                  (VSeq [ Weir.Builtins.file "b.bin" 5 true ])
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
          test "field typo in a pipeline" { checkErr "ls |> where (fun f -> f.Sze > 1<mb>)" |> ignore }
          test "wrong argument to a builtin" { checkErr "double \"x\"" |> ignore }
          test "piping a seq into an int function" { checkErr "ls |> double" |> ignore }
          test "let-bound bare lambda cannot infer without annotations" {
              checkErr "let add = fun a -> fun b -> a + b in add 1 2" |> ignore
          } ]

let private warningsOf input = Weir.Check.warnings env (checkOk input)

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

              match Weir.Check.typecheck ambEnv (parse "{ X = 1; Y = 2 }") with
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

              match Weir.Check.typecheck treeEnv e with
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

              match Weir.Check.typecheck treeEnv e with
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
              expectValue "(match Running 5 with | Running n -> n | Stopped -> 0) |> double" (VInt 10)
          }
          test "arm bodies can contain full pipelines" {
              expectValue "match Running 42 with | Running n -> n |> double | Stopped -> 0" (VInt 84)
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

let streamingTests =
    testList
        "Streaming"
        [ test "acceptance: infinite source |> first 5 terminates" {
              let infinite = Seq.initInfinite (fun i -> Weir.Builtins.file $"f{i}" i false)

              let result =
                  runWith [ "ls", VSeq infinite ] "ls |> where (fun f -> f.Size > 1<mb>) |> first 5"
                  |> forceSeq

              Expect.equal (List.length result) 5 "exactly five rows"
              Expect.equal result[0] (Weir.Builtins.file "f2" 2 false) "first surviving row"
          }
          test "acceptance: first 5 pulls exactly 5 elements from the source" {
              let pulled = ref 0

              let counting =
                  Seq.initInfinite (fun i ->
                      System.Threading.Interlocked.Increment pulled |> ignore
                      Weir.Builtins.file $"f{i}" i false)

              runWith [ "ls", VSeq counting ] "ls |> first 5" |> forceSeq |> ignore
              Expect.equal pulled.Value 5 "no over-pulling"
          }
          test "where pulls only what the filter and take demand" {
              let pulled = ref 0

              let counting =
                  Seq.initInfinite (fun i ->
                      System.Threading.Interlocked.Increment pulled |> ignore
                      Weir.Builtins.file $"f{i}" i false)

              runWith [ "ls", VSeq counting ] "ls |> where (fun f -> f.Size > 1<mb>) |> first 2"
              |> forceSeq
              |> ignore

              Expect.equal pulled.Value 4 "sizes 0..3 examined, 2 and 3 survive"
          }
          test "unforced pipeline pulls nothing" {
              let pulled = ref 0

              let counting =
                  Seq.initInfinite (fun i ->
                      System.Threading.Interlocked.Increment pulled |> ignore
                      Weir.Builtins.file $"f{i}" i false)

              runWith [ "ls", VSeq counting ] "ls |> where (fun f -> f.Size > 1<mb>) |> first 5"
              |> ignore

              Expect.equal pulled.Value 0 "evaluation alone must not enumerate"
          }
          test "nats through map and take" {
              Expect.equal
                  (run "nats |> map (fun x -> x * x) |> take 5" |> forceSeq)
                  [ VInt 0; VInt 1; VInt 4; VInt 9; VInt 16 ]
                  ""
          }
          test "sum consumes a finite stream" { expectValue "nats |> take 5 |> sum" (VInt 10) }
          test "lambda pipe stage stays lazy" { expectValue "nats |> map (fun x -> x + 1) |> take 3 |> sum" (VInt 6) }
          test "equality on seqs is rejected" {
              Expect.stringContains (checkErr "nats == nats").Message "'==' is not defined for seq<int>" ""
          }
          test "equality through a seq-carrying record is rejected" {
              let holderEnv = env |> declare "type Holder = { S: seq<int> }"
              let e = parse "let h = { S = nats } in h == h"

              match Weir.Check.typecheck holderEnv e with
              | Ok _ -> failtest "expected rejection"
              | Error terr -> Expect.stringContains terr.Message "'==' is not defined for Holder" ""
          }
          test "equality on union values still works" {
              expectValue "Running 1 == Running 1" (VBool true)
              expectValue "Running 1 == Stopped" (VBool false)
          } ]

let polymorphismTests =
    testList
        "Pipe-directed instantiation"
        [ test "where instantiates from the piped seq" {
              Expect.equal (checkOk "ls |> where (fun f -> f.ReadOnly)").Ty Weir.Builtins.seqFileRow ""
          }
          test "map changes the element type" {
              Expect.equal (checkOk "ls |> map (fun f -> f.Size)").Ty (TSeq(TInt(Some "mb"))) ""
          }
          test "map over ints still works" {
              Expect.equal (run "nats |> map (fun x -> x * x) |> take 3" |> forceSeq) [ VInt 0; VInt 1; VInt 4 ] ""
          }
          test "map with an inferable function argument works standalone" {
              Expect.equal (run "nats |> map double |> take 3" |> forceSeq) [ VInt 0; VInt 2; VInt 4 ] ""
          }
          test "full application instantiates from the trailing data argument" {
              expectValue "where (fun f -> f.ReadOnly) ls |> first 1" (VSeq [ Weir.Builtins.file "b.bin" 5 true ])
          }
          test "instantiation mismatch is reported" {
              Expect.stringContains
                  (checkErr "nats |> where (fun f -> f.ReadOnly)").Message
                  "only records have fields"
                  ""
          } ]

let boundaryTests =
    testList
        "External command boundary"
        [ test "cmd yields stdout lines" {
              Expect.equal (run "sh \"printf 'a\\nb\\n'\"" |> forceSeq) [ VStr "a"; VStr "b" ] ""
          }
          test "cmd is lazy across the process boundary" {
              Expect.equal (run "sh \"yes\" |> first 3" |> forceSeq) [ VStr "y"; VStr "y"; VStr "y" ] ""
          }
          test "failing command raises when forced" {
              Expect.throws (fun () -> run "sh \"exit 3\"" |> forceSeq |> ignore) ""
          }
          test "unforced command runs nothing" { run "sh \"exit 3\"" |> ignore }
          test "porcelain adapter parses status lines" {
              let src =
                  VSeq
                      [ VStr " M a.txt"
                        VStr "A  b.txt"
                        VStr "?? c.txt"
                        VStr "R  old.txt -> new.txt" ]

              let result = runWith [ "src", src ] "src |> from porcelain" |> forceSeq

              let change status staged unstaged path =
                  VRecord(
                      "Change",
                      Map
                          [ "Status", VStr status
                            "Staged", VBool staged
                            "Unstaged", VBool unstaged
                            "Path", VStr path ]
                  )

              Expect.equal
                  result
                  [ change " M" false true "a.txt"
                    change "A " true false "b.txt"
                    change "??" false true "c.txt"
                    change "R " true false "new.txt" ]
                  ""
          }
          test "acceptance: git status |> from porcelain |> where staged on a real repo" {
              let dir = Path.Combine(Path.GetTempPath(), $"weir-{System.Guid.NewGuid():N}")

              let setup =
                  $"mkdir -p {dir} && cd {dir} && git init -q && echo a > staged.txt && echo b > untracked.txt && git add staged.txt"

              let psi = System.Diagnostics.ProcessStartInfo("/bin/sh")
              psi.ArgumentList.Add "-c"
              psi.ArgumentList.Add setup
              use p = System.Diagnostics.Process.Start psi
              p.WaitForExit()
              Expect.equal p.ExitCode 0 "repo setup"

              try
                  let result =
                      run $"sh \"cd {dir} && git status --porcelain\" |> from porcelain |> where (fun c -> c.Staged)"
                      |> forceSeq

                  match result with
                  | [ VRecord("Change", fields) ] ->
                      Expect.equal fields["Path"] (VStr "staged.txt") "path"
                      Expect.equal fields["Staged"] (VBool true) "staged"
                  | other -> failtest $"unexpected result: {other}"
              finally
                  Directory.Delete(dir, true)
          }
          test "to json serializes records as ndjson" {
              Expect.equal
                  (run "ls |> first 1 |> to json" |> forceSeq)
                  [ VStr """{"Name":"a.txt","ReadOnly":false,"Size":0}""" ]
                  ""
          }
          test "json roundtrip preserves rows" {
              Expect.equal (run "ls |> to json |> from json FileRow" |> forceSeq) fakeFiles ""
          }
          test "from json validates field types" {
              let src = VSeq [ VStr """{"Name":"x","Size":"big","ReadOnly":false}""" ]

              Expect.throws (fun () -> runWith [ "src", src ] "src |> from json FileRow" |> forceSeq |> ignore) ""
          }
          test "from json rejects missing fields" {
              let src = VSeq [ VStr """{"Name":"x"}""" ]

              Expect.throws (fun () -> runWith [ "src", src ] "src |> from json FileRow" |> forceSeq |> ignore) ""
          }
          test "from json ignores extra fields" {
              let src = VSeq [ VStr """{"Name":"x","Size":1,"ReadOnly":true,"Extra":42}""" ]

              Expect.equal
                  (runWith [ "src", src ] "src |> from json FileRow" |> forceSeq)
                  [ Weir.Builtins.file "x" 1 true ]
                  ""
          }
          test "into feeds stdin and yields stdout" {
              Expect.equal (run "nats |> take 3 |> to json |> into \"wc -l\"" |> forceSeq) [ VStr "3" ] ""
          }
          test "from can be let-bound" {
              expectValue
                  "let p = from porcelain in sh \"printf 'A  x.txt\\n'\" |> p |> first 1 |> map (fun c -> c.Path)"
                  (VSeq [ VStr "x.txt" ])
          } ]

let boundaryCheckTests =
    testList
        "Boundary check errors"
        [ test "from json needs a record name" {
              Expect.stringContains (checkErr "sh \"x\" |> from json").Message "needs a record name" ""
          }
          test "from json rejects unknown records" {
              Expect.stringContains (checkErr "sh \"x\" |> from json Missing").Message "unknown type 'Missing'" ""
          }
          test "from json rejects unions" {
              Expect.stringContains (checkErr "sh \"x\" |> from json Proc").Message "needs a record" ""
          }
          test "unknown format is rejected" {
              Expect.stringContains (checkErr "sh \"x\" |> from yaml").Message "unknown format 'yaml'" ""
          }
          test "from porcelain takes no type name" {
              Expect.stringContains (checkErr "sh \"x\" |> from porcelain Proc").Message "fixed row type" ""
          }
          test "piping a non-string seq into from is rejected" {
              Expect.stringContains (checkErr "nats |> from porcelain").Message "expected string, got int" ""
          }
          test "to json on a union seq is rejected" {
              let e = "let xs = nats |> map (fun n -> Running n) in xs |> to json"
              Expect.stringContains (checkErr e).Message "primitive or record elements" ""
          }
          test "to json standalone is rejected" { Expect.stringContains (checkErr "to json").Message "pipe stage" "" } ]

let shorthandTests =
    testList
        "Underscore shorthand and escapes"
        [ test "underscore field access desugars to a lambda" {
              expectParse "where _.ReadOnly" "(where (fun _ _.ReadOnly))"
          }
          test "where with shorthand filters" {
              expectValue "ls |> where _.ReadOnly" (VSeq [ Weir.Builtins.file "b.bin" 5 true ])
          }
          test "map with shorthand projects" {
              Expect.equal (checkOk "ls |> map _.Size").Ty (TSeq(TInt(Some "mb"))) ""

              Expect.equal (run "ls |> map _.Name |> first 2" |> forceSeq) [ VStr "a.txt"; VStr "b.bin" ] ""
          }
          test "shorthand chains through nested records" { expectParse "map _.A.B" "(map (fun _ _.A.B))" }
          test "bare underscore is not an expression" {
              Expect.stringContains (checkErr "_ + 1").Message "unbound variable '_'" ""
          }
          test "string escapes parse" {
              expectValue "\"a\\nb\"" (VStr "a\nb")
              expectValue "\"say \\\"hi\\\"\"" (VStr "say \"hi\"")
              expectValue "\"tab\\there\"" (VStr "tab\there")
              expectValue "\"back\\\\slash\"" (VStr "back\\slash")
          }
          test "escaped strings survive a json roundtrip" {
              let src = VSeq [ VStr """{"Name":"a\"b","Size":1,"ReadOnly":false}""" ]

              Expect.equal
                  (runWith [ "src", src ] "src |> from json FileRow |> map _.Name" |> forceSeq)
                  [ VStr "a\"b" ]
                  ""
          } ]

let private suggest text (wordStart: int) =
    Weir.Complete.suggest env text wordStart

let completionTests =
    testList
        "Completion"
        [ test "name completion from values in scope" { Expect.equal (suggest "ls |> whe" 6) [ "where" ] "" }
          test "keyword completion" { Expect.contains (suggest "ma" 0) "match" "" }
          test "lambda parameter completes from the pipeline element type" {
              let text = "ls |> where (fun f -> f."
              Expect.equal (suggest text (text.Length - 2)) [ "f.Name"; "f.ReadOnly"; "f.Size" ] ""
          }
          test "field prefix narrows the suggestions" {
              let text = "ls |> where (fun f -> f.S"
              Expect.equal (suggest text (text.Length - 3)) [ "f.Size" ] ""
          }
          test "bound record variable completes its fields" {
              let envWithQ =
                  { env with
                      Values = Map.add "q" (generalize (TNamed "Point")) env.Values }

              Expect.equal (Weir.Complete.suggest envWithQ "q." 0) [ "q.X"; "q.Y" ] ""
          }
          test "later pipeline stages track the element type" {
              let text = "sh \"git status --porcelain\" |> from porcelain |> where (fun c -> c."
              Expect.equal (suggest text (text.Length - 2)) [ "c.Path"; "c.Staged"; "c.Status"; "c.Unstaged" ] ""
          }
          test "no fields on a non-record element" {
              let text = "nats |> map (fun x -> x."
              Expect.equal (suggest text (text.Length - 2)) [] ""
          }
          test "from json completes record names" {
              let text = "sh \"x\" |> from json "
              Expect.contains (suggest text text.Length) "FileRow" ""
              Expect.contains (suggest text text.Length) "Change" ""
          } ]

let rowTests =
    testList
        "Row polymorphism"
        [ test "field projection lambda infers a row type" {
              match (checkOk "fun f -> f.ReadOnly").Ty with
              | TFun(TRowVar(_, [ "ReadOnly", TVar _ ]), TVar _) -> ()
              | t -> failtest $"expected a row-typed projection, got {formatTy t}"
          }
          test "field usage constrains the row" {
              match (checkOk "fun f -> f.Size > 1<mb>").Ty with
              | TFun(TRowVar(_, [ "Size", TInt(Some "mb") ]), TBool) -> ()
              | t -> failtest $"expected {{ Size: int<mb>; .. }} -> bool, got {formatTy t}"
          }
          test "row-typed filter discharges against FileRow" {
              expectValue "let staged = where _.ReadOnly in ls |> staged" (VSeq [ Weir.Builtins.file "b.bin" 5 true ])
          }
          test "one row-polymorphic projection reused across two record types" {
              let vecEnv = env |> declare "type Vec = { X: int; Z: int }"

              let e =
                  parse (
                      "let xs = map _.X in "
                      + "let a = nats |> take 2 |> map (fun n -> { X = n; Y = n }) |> xs |> sum in "
                      + "let b = nats |> take 3 |> map (fun n -> { X = n; Z = n }) |> xs |> sum in "
                      + "a + b"
                  )

              match Weir.Check.typecheck vecEnv e with
              | Error terr -> failtest (formatError terr)
              | Ok te ->
                  Expect.equal te.Ty (TInt None) "type"
                  Expect.equal (eval valueEnv te) (VInt 4) "0+1 and 0+1+2"
          }
          test "row discharge through a let reports the typo at the use site" {
              let input = "let f = where (fun c -> c.Sze > 1<mb>) in ls |> f"
              let terr = checkErr input
              Expect.stringContains terr.Message "FileRow has no field 'Sze'" ""
              Expect.stringContains terr.Message "Did you mean 'Size'?" ""
              Expect.equal terr.Span.Start.Col (input.LastIndexOf "f" + 1) "span points at the use"
          }
          test "direct pipeline typo keeps the exact field span" {
              let input = "ls |> where (fun c -> c.Sze > 1<mb>)"
              let terr = checkErr input
              Expect.equal terr.Span.Start.Col (input.IndexOf "Sze" + 1) "span points at the typo"
          }
          test "row discharge checks the field type" {
              let terr = checkErr "let f = where (fun c -> c.Name > 1<mb>) in ls |> f"
              Expect.stringContains terr.Message "expected string, got int<mb>" ""
          }
          test "record missing a constrained field is rejected" {
              Expect.stringContains
                  (checkErr "let g = where (fun p -> p.X > 1) in ls |> g").Message
                  "FileRow has no field 'X'"
                  ""
          }
          test "unitless product binds unknowns to int" {
              Expect.equal (checkOk "fun x -> x * x").Ty (TFun(TInt None, TInt None)) ""
          }
          test "ambiguous operands stay an error" {
              Expect.stringContains (checkErr "fun x -> x + x").Message "cannot infer the operand types" ""
          }
          test "applying a variable as a function is rejected" {
              Expect.stringContains (checkErr "fun f -> f 1").Message "not a function" ""
          }
          test "generalization does not capture enclosing lambda parameters" {
              checkErr "fun y -> let g = fun x -> y in (g 1 + 1) + (g 2 + \"s\")" |> ignore
          }
          test "row types survive the REPL display" {
              Expect.equal (formatTy (checkOk "where _.ReadOnly").Ty |> fun s -> s.Contains "ReadOnly") true ""
          } ]

let private survivors (marker: string) : int =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add $"pgrep -f '[{marker[0]}]{marker.Substring 1}' | wc -l"
    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd().Trim()
    p.WaitForExit()
    int out

let private eventuallyNoSurvivors (marker: string) : bool =
    let mutable tries = 20
    let mutable count = survivors marker

    while count > 0 && tries > 0 do
        System.Threading.Thread.Sleep 100
        tries <- tries - 1
        count <- survivors marker

    count = 0

let private defunctChildren () : int =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add $"ps -o stat= --ppid {System.Environment.ProcessId} | grep -c Z"
    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd().Trim()
    p.WaitForExit()
    if out = "" then 0 else int out

let lifecycleTests =
    testList
        "Process lifecycle"
        [ // TRIPWIRE PAIR: the simple case passes even without tree-kill because
          // sh execs a single command (one process). The compound case is the
          // real guard — sh forks pipeline children and only
          // Kill(entireProcessTree: true) reaches them. If the sh backing is
          // removed (PLAN-command-mode Session 2), this analysis changes:
          // re-derive which of these guards what.
          test "simple command: no survivors after partial consumption" {
              run "sh \"yes weir-s1-simple\" |> first 3" |> forceSeq |> ignore
              Expect.isTrue (eventuallyNoSurvivors "weir-s1-simple") "yes leaked"
          }
          test "compound command: no survivors after partial consumption" {
              run "sh \"yes weir-s1-compound | grep --line-buffered weir-s1-compound\" |> first 3"
              |> forceSeq
              |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s1-compound") "pipeline children leaked"
          }
          test "50 completed commands leave no zombies" {
              for _ in 1..50 do
                  run "sh \"true\"" |> forceSeq |> ignore

              let zombies = defunctChildren ()
              Expect.equal zombies 0 "defunct children accumulated"
          }
          test "50 abandoned streams leave no zombies" {
              for _ in 1..50 do
                  run "sh \"yes weir-s1-zombie\" |> first 1" |> forceSeq |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s1-zombie") "killed children leaked"
              let zombies = defunctChildren ()
              Expect.equal zombies 0 "defunct children accumulated after kills"
          } ]

let session2Tests =
    testList
        "sh/cmd split and session cwd"
        [ test "list literal parses, types, evaluates" {
              expectParse "[1; 2; 3]" "[1; 2; 3]"
              Expect.equal (checkOk "[1; 2]").Ty (TSeq(TInt None)) "type"
              expectValue "[1; 2] |> sum" (VInt 3)
              expectValue "[\"a\"; \"b\"] |> first 1" (VSeq [ VStr "a" ])
          }
          test "empty list literal is polymorphic" {
              match (checkOk "[]").Ty with
              | TSeq(TVar _) -> ()
              | t -> failtest $"expected seq<'a>, got {formatTy t}"
          }
          test "heterogeneous list literal is rejected" {
              Expect.stringContains (checkErr "[1; \"a\"]").Message "expected int, got string" ""
          }
          test "cmd passes argv verbatim: glob stays literal" {
              Expect.equal (run "cmd \"echo\" [\"*\"]" |> forceSeq) [ VStr "*" ] ""
          }
          test "sh is the escape hatch: glob expands" {
              let dir = Path.Combine(Path.GetTempPath(), $"weir-s2-{System.Guid.NewGuid():N}")
              Directory.CreateDirectory dir |> ignore
              File.WriteAllText(Path.Combine(dir, "g1.txt"), "")
              File.WriteAllText(Path.Combine(dir, "g2.txt"), "")

              try
                  let out = run $"let d = cd \"{dir}\" in sh \"echo *.txt\"" |> forceSeq
                  Expect.equal out [ VStr "g1.txt g2.txt" ] ""
              finally
                  Weir.Session.Cwd <- System.IO.Directory.GetCurrentDirectory()
                  Directory.Delete(dir, true)
          }
          test "injection attempt is inert through cmd" {
              Expect.equal (run "cmd \"echo\" [\"; rm -rf x\"]" |> forceSeq) [ VStr "; rm -rf x" ] ""
          }
          test "cd changes the spawn cwd for cmd" {
              try
                  Expect.equal (run "let d = cd \"/tmp\" in cmd \"pwd\" []" |> forceSeq) [ VStr "/tmp" ] ""
              finally
                  Weir.Session.Cwd <- System.IO.Directory.GetCurrentDirectory()
          }
          test "cd changes the spawn cwd for sh" {
              try
                  Expect.equal (run "let d = cd \"/tmp\" in sh \"pwd\"" |> forceSeq) [ VStr "/tmp" ] ""
              finally
                  Weir.Session.Cwd <- System.IO.Directory.GetCurrentDirectory()
          }
          test "pwd builtin tracks Session.Cwd lazily" {
              try
                  Expect.equal
                      (run "let p = pwd in let d = cd \"/tmp\" in p" |> forceSeq)
                      [ VStr "/tmp" ]
                      "pwd re-reads Session.Cwd per enumeration"
              finally
                  Weir.Session.Cwd <- System.IO.Directory.GetCurrentDirectory()
          }
          test "cd on a missing directory fails at runtime" {
              Expect.throws (fun () -> run "cd \"/definitely/not/here\"" |> ignore) ""
          }
          test "cd resolves relative and dotdot" {
              try
                  Expect.equal
                      (run "let a = cd \"/tmp\" in cd \"..\"" |> ignore
                       Weir.Session.Cwd)
                      "/"
                      ""
              finally
                  Weir.Session.Cwd <- System.IO.Directory.GetCurrentDirectory()
          }
          test "cmd not found raises with a clear message" {
              Expect.throws (fun () -> run "cmd \"weir-no-such-prog\" []" |> forceSeq |> ignore) ""
          }
          // Direct-exec lifecycle duplicates: no sh in front, so the
          // exec-optimization analysis from the Session-1 tripwire does not
          // apply — tree-kill must hold on its own.
          test "direct cmd: no survivors after partial consumption" {
              run "cmd \"yes\" [\"weir-s2-direct\"] |> first 3" |> forceSeq |> ignore
              Expect.isTrue (eventuallyNoSurvivors "weir-s2-direct") "direct-exec child leaked"
          }
          test "direct cmd: 50 abandoned streams leave no zombies" {
              for _ in 1..50 do
                  run "cmd \"yes\" [\"weir-s2-dz\"] |> first 1" |> forceSeq |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s2-dz") "direct-exec children leaked"
              Expect.equal (defunctChildren ()) 0 "defunct children accumulated"
          } ]

let operatorTests =
    testList
        "Operator completeness"
        [ test "precedence: || below && below comparisons" {
              expectParse "a || b && c" "(|| a (&& b c))"
              expectParse "1 < 2 || 2 <= 3 && 4 >= 4" "(|| (< 1 2) (&& (<= 2 3) (>= 4 4)))"
          }
          test "not-equal parses as one operator" { expectParse "a <> b" "(<> a b)" }
          test "measure literal still wins over <=" { expectParse "1<mb> <= 2<mb>" "(<= 1<mb> 2<mb>)" }
          test "not-equal on values" {
              expectValue "1 <> 2" (VBool true)
              expectValue "\"a\" <> \"a\"" (VBool false)
              expectValue "Running 1 <> Stopped" (VBool true)
          }
          test "not-equal inherits equatability: seqs rejected" {
              Expect.stringContains (checkErr "nats <> nats").Message "'<>' is not defined for seq<int>" ""
          }
          test "ordered comparisons include boundaries and measures" {
              expectValue "2 >= 2" (VBool true)
              expectValue "2 <= 1" (VBool false)
              expectValue "3<mb> >= 2<mb>" (VBool true)
              Expect.stringContains (checkErr "1<mb> <= 1<gb>").Message "expected int<mb>, got int<gb>" ""
          }
          test "common filter shape works" {
              expectValue
                  "ls |> where (fun f -> f.Name <> \"a.txt\" && f.Size <= 3<mb>)"
                  (VSeq [ Weir.Builtins.file "c.log" 1 false; Weir.Builtins.file "d.iso" 3 false ])
          }
          test "not builtin" {
              expectValue "not true" (VBool false)

              expectValue
                  "ls |> where (fun f -> not f.ReadOnly) |> first 1"
                  (VSeq [ Weir.Builtins.file "a.txt" 0 false ])
          }
          test "and-or require bools" {
              Expect.stringContains (checkErr "1 && 2").Message "'&&' is not defined for int" ""
          }
          test "and-or on two unknowns default to bool" {
              Expect.equal (checkOk "fun a -> fun b -> a && b").Ty (TFun(TBool, TFun(TBool, TBool))) ""
          }
          test "&& short-circuits: right side not evaluated on false" {
              expectValue "false && (1 / 0 == 1)" (VBool false)
          }
          test "|| short-circuits: right side not evaluated on true" { expectValue "true || (1 / 0 == 1)" (VBool true) }
          test "&& is strict when the left side is true" {
              Expect.throws (fun () -> run "true && (1 / 0 == 1)" |> ignore) ""
          } ]

let adversarialTests =
    testList
        "Adversarial probes"
        [ test "wrong arity application" {
              Expect.stringContains (checkErr "double 1 2").Message "not a function taking 2" ""
          }
          test "measure mismatch rejects in both directions" {
              Expect.stringContains (checkErr "1<mb> > 1<s>").Message "expected int<mb>, got int<s>" ""
              Expect.stringContains (checkErr "1<s> > 1<mb>").Message "expected int<s>, got int<mb>" ""
          }
          test "shadowing with a different type is respected" {
              Expect.stringContains
                  (checkErr "let x = 1 in let x = \"s\" in x + 1").Message
                  "expected string, got int"
                  ""

              expectValue "let x = 1 in let x = \"s\" in x + \"!\"" (VStr "s!")
          }
          test "inferred element type contradicts use two stages later" {
              let terr = checkErr "nats |> map (fun x -> x * x) |> where (fun x -> x.ReadOnly)"
              Expect.stringContains terr.Message "only records have fields" ""
          }
          test "row constraint conflicts with the declared field measure" {
              Expect.stringContains
                  (checkErr "let g = fun r -> r.Size > 1<s> in ls |> where g").Message
                  "expected int<mb>, got int<s>"
                  ""
          }
          test "piping a lambda as data into an int function is rejected" {
              checkErr "(fun x -> x) |> double" |> ignore
          }
          test "piping a constructor as data is rejected" { checkErr "Running |> double" |> ignore }
          test "field access on a union is rejected toward the eval-unreachable arm" {
              Expect.stringContains (checkErr "match Stopped with | p -> p.Sze").Message "Proc is a union" ""
          }
          test "match scrutinee cannot be a raw constructor pattern target mismatch" {
              Expect.stringContains
                  (checkErr "match 5 with | Running n -> n | _ -> 0").Message
                  "constructor patterns need a union"
                  ""
          }
          test "1.1 self-application rejects without hanging" {
              Expect.stringContains (checkErr "fun f -> f.x f").Message "not a function" ""
          }
          test "1.2 var-var row merge unifies field types, not just names" {
              Expect.stringContains
                  (checkErr "fun f -> ((fun g -> g.A > 1) f) == ((fun h -> h.A == \"s\") f)").Message
                  "expected int, got string"
                  ""
          }
          test "1.3 intra-lambda conflicting demands on one field" {
              Expect.stringContains
                  (checkErr "fun f -> (f.X > 1) == (f.X == \"a\")").Message
                  "expected int, got string"
                  ""
          }
          test "1.4 row is closed after nominal discharge" {
              Expect.stringContains
                  (checkErr "ls |> where (fun f -> f.Size > 1<mb>) |> where (fun f -> f.Nonexistent == 1)").Message
                  "FileRow has no field 'Nonexistent'"
                  ""
          }
          test "1.5 polymorphic reuse does not phantom-reject good code" {
              expectValue "let idf = fun x -> x in (5 |> idf) + (idf 7)" (VInt 12)
          }
          test "3.1 generalized row projection instantiates freshly per use" {
              let e2 =
                  env
                  |> declare "type IntV = { V: int; Tag: bool }"
                  |> declare "type StrV = { V: string; Alt: bool }"

              let expr =
                  parse (
                      "let getV = map _.V in "
                      + "let a = nats |> take 2 |> map (fun n -> { V = n; Tag = true }) |> getV |> sum in "
                      + "let b = nats |> take 1 |> map (fun n -> { V = \"s\"; Alt = true }) |> getV in "
                      + "a"
                  )

              match Weir.Check.typecheck e2 expr with
              | Error terr -> failtest (formatError terr)
              | Ok te ->
                  Expect.equal te.Ty (TInt None) "int and string uses of getV both accepted"
                  Expect.equal (eval valueEnv te) (VInt 1) "sum of 0+1"
          }
          test "4.3 measured division does not collapse to dimensionless" {
              Expect.stringContains
                  (checkErr "ls |> map (fun f -> f.Size / f.Size)").Message
                  "'/' is not defined for int<mb>"
                  ""
          }
          test "5.2 shadowing does not leak the outer row constraint" {
              match (checkOk "fun f -> (f.A > 1) == (let f = \"s\" in f == \"s\")").Ty with
              | TFun(TRowVar(_, [ "A", TInt None ]), TBool) -> ()
              | t -> failtest $"expected {{ A: int; .. }} -> bool, got {formatTy t}"
          }
          test "porcelain unquotes C-quoted paths" {
              let src =
                  VSeq
                      [ VStr " M \"spaced name.txt\""
                        VStr "?? \"qu\\\"ote.txt\""
                        VStr "?? \"caf\\303\\251.txt\""
                        VStr "R  \"old name.txt\" -> \"new name.txt\""
                        VStr "R  plain.txt -> renamed.txt" ]

              Expect.equal
                  (runWith [ "src", src ] "src |> from porcelain |> map _.Path" |> forceSeq)
                  [ VStr "spaced name.txt"
                    VStr "qu\"ote.txt"
                    VStr "café.txt"
                    VStr "new name.txt"
                    VStr "renamed.txt" ]
                  ""
          } ]

[<Tests>]
let allTests =
    testList
        "Weir"
        [ parserTests
          checkerTests
          evalTests
          rejectedAtCheckTests
          declTests
          matchTests
          warningTests
          streamingTests
          polymorphismTests
          boundaryTests
          boundaryCheckTests
          shorthandTests
          completionTests
          rowTests
          adversarialTests
          operatorTests
          lifecycleTests
          session2Tests ]
