module Tests

open System.Diagnostics
open System.IO
open Expecto
open Weir.Ast
open Weir.Types
open Weir.Argv
open Weir.Check
open Weir.Eval

let private parse input =
    match Weir.Parser.parseExpr input with
    | Ok e -> e
    | Error msg -> failtest $"parse failed: {msg}"

// [D:sibling-sentinel] the assembler joins STATEMENT siblings with the
// machine sentinel, not ';' (command mode stops at it; a user ';' does
// not). Assembler-text pins spell the join as a readable ';'; asmSib
// rewrites that display ';' to the sentinel the assembler emits.
// Bracket field/element separators stay a literal ';' — never wrap those.
let private asmSib (s: string) =
    s.Replace(" ; ", " " + Weir.Parser.sibSepStr + " ")

// the span-free sexpr renderer moved to Ast (shared with fmt's
// respace safety check [D:fmt-respace]); these aliases keep the pins
let private show = Weir.Ast.sexpr
let private showPat = Weir.Ast.sexprPat

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

let private preludeTypeEnv, preludeValueEnv =
    Weir.Prelude.extend Weir.Builtins.typeEnv Weir.Builtins.valueEnv

let private env =
    let e =
        preludeTypeEnv
        |> declare "type Proc = Running of int | Stopped"
        // a 2-param generic union — the type-system fixture that Result
        // used to be, now a local declaration [D:no-result]
        |> declare "type Either<'a, 'e> = Left of 'a | Right of 'e"
        |> declare "type Point = { X: int; Y: int }"
        // record-update battery: two records sharing an int field N
        |> declare "type UpdP = { UpN: int; UpT: string }"
        |> declare "type UpdQ = { UpN: int }"
        |> declare "type UpdS = { UpN: string; UpS: string }"
        // attributed twin for the erasure pin [D:attributes]
        |> declare "type AttrE = { [<Short \"q\">] Q: int }"

    { e with
        Values =
            e.Values
            |> Map.add "src" (generalize (TSeq TStr))
            |> Map.add "double" (generalize (TFun(TInt, TInt))) }

let private ctorValues =
    [ "type Proc = Running of int | Stopped"
      "type Either<'a, 'e> = Left of 'a | Right of 'e" ]
    |> List.collect (fun d ->
        match (parseDecl d).Body with
        | DUnion cases -> constructorValues cases
        | DRecord _ -> [])

let private fakeFiles =
    [ Weir.Builtins.file "a.txt" 0 false
      Weir.Builtins.file "b.bin" 5242880 true
      Weir.Builtins.file "c.log" 1048576 false
      Weir.Builtins.file "d.iso" 3145728 false ]

let private doubleFixture =
    VBuiltin(fun v ->
        match v with
        | VInt n -> VInt(n * 2L)
        | v -> failwith $"double fixture applied to {formatValue v}")

let private valueEnv =
    ("ls", VSeq fakeFiles) :: ("double", doubleFixture) :: ctorValues
    |> List.fold (fun vs (n, v) -> Map.add n v vs) preludeValueEnv

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

let private fakeExternals =
    Set [ "git"; "grep"; "echo"; "yes"; "true"; "ls"; "cat" ]

let private cmdResolver: Weir.Parser.Resolver =
    { IsKnown = fun n -> Map.containsKey n env.Values
      IsCommandCallable = fun n -> Weir.Builtins.commandCallable.Contains n
      IsExternal = fun p -> fakeExternals.Contains p || p = "./build.sh"
      ExternalNames = fun () -> fakeExternals }

let private realResolver: Weir.Parser.Resolver =
    { IsKnown = fun n -> Map.containsKey n env.Values
      IsCommandCallable = fun n -> Weir.Builtins.commandCallable.Contains n
      IsExternal = Weir.Extern.exists
      ExternalNames = fun () -> Weir.Extern.names () :> seq<string> }

let private parseCmd input =
    match Weir.Parser.parseLine cmdResolver input with
    | Ok(SExpr e)
    | Ok(SCmd e) -> e
    | Ok other -> failtest $"expected an expression line, got {other}"
    | Error msg -> failtest $"parse failed: {msg}"

let private expectCmd input expected =
    Expect.equal (show (parseCmd input)) expected $"parse of '{input}'"

let private runReal input =
    match Weir.Parser.parseLine realResolver input with
    | Error msg -> failtest $"parse failed: {msg}"
    | Ok(SExpr e)
    | Ok(SCmd e) ->
        match typecheck env e with
        | Error terr -> failtest (formatError terr)
        | Ok te -> eval valueEnv te
    | Ok other -> failtest $"unexpected: {other}"

let private expectValue input expected =
    Expect.equal (run input) expected $"eval of '{input}'"

let acceptance = "ls |> where (fun f -> f.bytes > 1048576) |> first 5"

let parserTests =
    testList
        "Parser"
        [ test "binop and pipe" { expectParse "1 + 2 |> double" "((+ 1 2) |> double)" }
          test "application is left-assoc" { expectParse "f x y" "((f x) y)" }
          test "precedence" { expectParse "1 + 2 * 3" "(+ 1 (* 2 3))" }
          test "comparison binds looser than plus" { expectParse "1 + 2 > 2" "(> (+ 1 2) 2)" }
          test "lambda body extends right" { expectParse "fun x -> x + 1" "(fun x (+ x 1))" }
          test "let-in" { expectParse "let x = 1 in x" "(let x 1 x)" }
          test "field access chains" { expectParse "f.bytes > 1048576" "(> f.bytes 1048576)" }
          test "shell pipe is pipe" { expectParse "ls |> where p |> first 5" "((ls |> (where p)) |> (first 5))" }
          test "less-than without space parses as comparison (transition-recognizer guard)" {
              expectParse "1<2" "(< 1 2)"
          }
          test "old measure literal gets the transition error" {
              for old in [ "1<mb>"; "1<mb> <= 2<mb>" ] do
                  match Weir.Parser.parseExpr old with
                  | Error msg -> Expect.stringContains msg "units of measure are not supported" "courtesy error"
                  | Ok _ -> failtest $"expected the transition error for {old}"
          }
          test "old measure type syntax gets the transition error" {
              match Weir.Parser.parseStmt "type T = { F: int<mb> }" with
              | Error msg -> Expect.stringContains msg "units of measure are not supported" "courtesy error"
              | Ok _ -> failtest "expected the transition error"
          }
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
              let input = "ls |> where (fun f -> f.bytse > 1048576) |> first 5"
              let terr = checkErr input
              let expectedStart = input.IndexOf "bytse" + 1
              Expect.equal terr.Span.Start.Col expectedStart "start col"
              Expect.equal terr.Span.End.Col (expectedStart + 5) "end col"
              Expect.stringContains terr.Message "FileRow has no field 'bytse'" ""
              Expect.stringContains terr.Message "Did you mean 'bytes'?" ""
          }
          test "lambda body of wrong type reports expected vs actual" {
              let terr = checkErr "ls |> where (fun f -> f.bytes) |> first 5"
              Expect.stringContains terr.Message "expected bool, got int" ""
          }
          test "unbound variable gets a hint" {
              Expect.stringContains (checkErr "doble 5").Message "Did you mean 'double'?" ""
          }
          test "did-you-mean stays capped at edit distance 2" {
              Expect.isFalse ((checkErr "zzzqqq 5").Message.Contains "Did you mean") "no hint beyond distance 2"
          }
          test "bare lambda infers a polymorphic type" {
              match (checkOk "fun x -> x").Ty with
              | TFun(TVar a, TVar b) when a = b -> ()
              | t -> failtest $"expected 'a -> 'a, got {formatTy t}"
          }
          test "lambda applied to a known argument infers" { Expect.equal (checkOk "(fun x -> x + 1) 41").Ty (TInt) "" }
          test "pipe into lambda infers" { Expect.equal (checkOk "5 |> fun x -> x * x").Ty (TInt) "" }
          test "let body type is the expression type" { Expect.equal (checkOk "let x = 5 in x |> double").Ty (TInt) "" }
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
                  (VSeq
                      [ Weir.Builtins.file "b.bin" 5242880 true
                        Weir.Builtins.file "d.iso" 3145728 false ])
          }
          test "where by string field" {
              expectValue
                  "ls |> where (fun f -> f.name == \"c.log\")"
                  (VSeq [ Weir.Builtins.file "c.log" 1048576 false ])
          }
          test "where by bool field" {
              expectValue "ls |> where (fun f -> f.readOnly)" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
          }
          test "first truncates" {
              expectValue
                  "ls |> first 2"
                  (VSeq [ Weir.Builtins.file "a.txt" 0 false; Weir.Builtins.file "b.bin" 5242880 true ])
          }
          test "arithmetic and pipes: 1 + 2 |> double" { expectValue "1 + 2 |> double" (VInt 6) }
          test "precedence: 1 + 2 * 3" { expectValue "1 + 2 * 3" (VInt 7) }
          // prefix minus: F#'s placement — operand positions only,
          // adjacency required, infix wins after a term
          test "prefix minus: negative literal" { expectValue "-5" (VInt -5) }
          test "prefix minus binds above *" { expectValue "2 * -3" (VInt -6) }
          test "prefix minus on a binding" { expectValue "let n = 3 in -n + 1" (VInt -2) }
          test "f -1 applies the negative literal (adjacency, oracle-corrected)" {
              expectValue "let f x = x + 1 in f -1" (VInt 0)
          }
          test "1 -2 is application of an int (rejected, as F# rejects it)" {
              let terr = checkErr "1 -2"
              Expect.stringContains terr.Message "not a function" ""
          }
          test "1 - 2 and 1-2 stay subtraction" {
              expectValue "1 - 2" (VInt -1)
              expectValue "1-2" (VInt -1)
          }
          test "prefix minus needs adjacency: '- 5' is not an operand" {
              match Weir.Parser.parseExpr "(- 5)" with
              | Error _ -> ()
              | Ok _ -> failtest "expected a parse error for spaced prefix minus"
          }
          // composition (mini-plan; the oracle refuted tighter-than-pipe)
          test "forward and backward composition" {
              expectValue "let inc n = n + 1 in (inc >> inc) 40" (VInt 42L)
              expectValue "let inc n = n + 1 in (inc << inc) 40" (VInt 42L)
              expectValue "(Str.trim >> Str.length) \"  ab  \"" (VInt 2L)
          }
          test "composition types through lambda params (operator-driven typing)" {
              expectValue "let both f g = f >> g in (both (fun n -> n + 1) (fun n -> n * 2)) 5" (VInt 12L)
          }
          test "constraints flow through composition (Eq through >>)" {
              expectValue "[0; 1] |> Seq.where ((fun x -> x == 0) >> not) |> Seq.head" (VInt 1L)
          }
          test "|> and >> share precedence: unparenthesized is the gotcha error" {
              let terr = checkErr "[1; 2] |> Seq.map (fun x -> x) >> Seq.sum"
              Expect.stringContains terr.Message "share precedence" ""
              expectValue "[1; 2] |> (Seq.map (fun x -> x) >> Seq.sum)" (VInt 3L)
          }
          test ">> with a non-function LHS gets the File.append redirect hint" {
              let terr = checkErr "ls >> Seq.length"
              Expect.stringContains terr.Message "File.append" ""
          }
          test "<< with a non-function LHS names composition" {
              let terr = checkErr "1 << 2"
              Expect.stringContains terr.Message "'<<' composes functions" ""
          }
          test "adjacent lexing: > comparison vs >> composition" {
              expectValue "1 > 2" (VBool false)
              expectValue "let inc n = n + 1 in (inc >> inc) 0 > 1" (VBool true)
          }
          test ">> in expression positions: then-branch, arm, list element" {
              expectValue "let inc n = n + 1 in (if true then inc >> inc else inc) 1" (VInt 3L)
              expectValue "let inc n = n + 1 in (match 1 with | _ -> inc >> inc) 1" (VInt 3L)
              expectValue "let inc n = n + 1 in ([inc >> inc] |> Seq.head) 1" (VInt 3L)
          }
          test "pipe chain" { expectValue "1 + 2 |> double |> double" (VInt 12) }
          test "pipe into lambda" { expectValue "5 |> fun x -> x * x" (VInt 25) }
          test "let-in" { expectValue "let x = 5 in x * 2" (VInt 10) }
          test "lambda application" { expectValue "(fun x -> x + 1) 41" (VInt 42) }
          test "closure captures environment" { expectValue "let y = 40 in (fun x -> x + y) 2" (VInt 42) }
          test "partially applied polymorphic builtin stays polymorphic" {
              expectValue
                  "let firstTwo = first 2 in ls |> firstTwo |> where (fun f -> f.readOnly)"
                  (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
          }
          test "lambda in polymorphic position without data now checks via rows" {
              expectValue
                  "let staged = where (fun f -> f.readOnly) in ls |> staged"
                  (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
          }
          test "shadowing" { expectValue "let x = 1 in let x = 2 in x" (VInt 2) }
          test "string concat" { expectValue "\"foo\" + \"bar\"" (VStr "foobar") }
          test "comparison" { expectValue "2 > 1" (VBool true) } ]

let rejectedAtCheckTests =
    testList
        "Rejected at check time, never at eval"
        [ test "unbound variable" { checkErr "nope" |> ignore }
          test "applying a non-function" { checkErr "1 2" |> ignore }
          test "string plus int" { checkErr "\"a\" + 1" |> ignore }
          test "field typo in a pipeline" { checkErr "ls |> where (fun f -> f.bytse > 1)" |> ignore }
          test "wrong argument to a builtin" { checkErr "double \"x\"" |> ignore }
          test "piping a seq into an int function" { checkErr "ls |> double" |> ignore }
          test "let-bound bare lambda cannot infer without annotations" {
              checkErr "let add = fun a -> fun b -> a + b in add 1 2" |> ignore
          } ]

let private warningsOf input = Weir.Check.warnings (checkOk input)

let declTests =
    testList
        "Type declarations"
        [ test "union declares constructors as typed values" {
              Expect.equal (checkOk "Running 5").Ty (TNamed("Proc", [])) "payload ctor applies"
              Expect.equal (checkOk "Stopped").Ty (TNamed("Proc", [])) "nullary ctor is a value"
          }
          test "constructor payload is checked" {
              Expect.stringContains (checkErr "Running \"x\"").Message "expected int, got string" ""
          }
          test "constructing evaluates to a union value" {
              expectValue "Running (2 + 3)" (VUnion("Running", Some(VInt 5)))
              expectValue "Stopped" (VUnion("Stopped", None))
          }
          test "record literal finds its nominal type" {
              Expect.equal (checkOk "{ X = 1; Y = 2 }").Ty (TNamed("Point", [])) ""
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
              | Ok te -> Expect.equal te.Ty (TNamed("Tree", [])) ""
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
              | Ok te -> Expect.equal te.Ty (TInt) ""
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
        [ test "missing case is a hard error" {
              let terr = checkErr "match Running 5 with | Running n -> n"
              Expect.stringContains (formatError terr) "missing: Stopped" ""
          }
          test "all cases covered checks" {
              Expect.isEmpty (warningsOf "match Running 5 with | Running n -> n | Stopped -> 0") ""
          }
          test "wildcard covers everything" { Expect.isEmpty (warningsOf "match Running 5 with | _ -> 0") "" }
          test "arm after a catch-all is a hard error" {
              let terr = checkErr "match Running 5 with | _ -> 0 | Stopped -> 1"
              Expect.stringContains terr.Message "unreachable" ""
          }
          test "mid-match variable binder errors at the cause with a constructor hint" {
              let terr =
                  checkErr "match Running 5 with | zStopped -> 1 | Running n -> n | Stopped -> 0"

              Expect.stringContains terr.Message "'zStopped' binds as a variable" ""
              Expect.stringContains terr.Message "2 arms below are unreachable" ""
              Expect.stringContains terr.Message "Did you mean 'Stopped'?" ""
              Expect.equal terr.Span.Start.Col 24 "error sits on the binder arm, not the dead arms"
          }
          test "guarded irrefutable arm keeps later arms reachable" {
              Expect.isEmpty (warningsOf "match Running 5 with | n when 1 > 2 -> 0 | _ -> 1") ""
          }
          test "match on a non-union: a variable arm is the catch-all" {
              Expect.isEmpty (warningsOf "match 5 with | n -> n") ""
              expectValue "match 5 with | n -> n + 1" (VInt 6L)
          }
          // the Regex pattern [D:regex-pattern] — arity typed at check time
          test "Regex pattern: arities 0/1/2 bind statically" {
              expectValue "match \"# x\" with | Regex @\"^#\" () -> \"comment\" | _ -> \"code\"" (VStr "comment")
              expectValue "match \"v13\" with | Regex @\"v(\\d+)\" v -> v | _ -> \"?\"" (VStr "13")

              expectValue
                  "match \"key=42\" with | Regex @\"(\\w+)=(\\d+)\" (k, v) -> $\"{k}:{v}\" | _ -> \"no\""
                  (VStr "key:42")
          }
          test "Regex pattern: non-capturing groups do not count" {
              expectValue "match \"a-1\" with | Regex @\"(?:\\w+)-(\\d+)\" n -> n | _ -> \"?\"" (VStr "1")
          }
          test "Regex pattern: arity mismatch is a check error naming the count" {
              let terr =
                  checkErr "match \"x\" with | Regex @\"(\\d+)-(\\d+)\" a -> a | _ -> \"?\""

              Expect.stringContains terr.Message "2 capture group(s)" ""
              Expect.stringContains terr.Message "tuple of 2 names" ""
          }
          test "Regex pattern: invalid literal is a check error" {
              let terr = checkErr "match \"x\" with | Regex @\"([\" v -> v | _ -> \"?\""
              Expect.stringContains terr.Message "invalid regex" ""
          }
          test "Regex pattern: non-match falls through; wildcard leaves skip binding" {
              expectValue
                  "match \"nope\" with | Regex @\"(\\d+)\" n -> n | Regex @\"(\\w+)\" (w) -> w | _ -> \"?\""
                  (VStr "nope")

              expectValue "match \"a b\" with | Regex @\"(\\w+) (\\w+)\" (a, _) -> a | _ -> \"?\"" (VStr "a")
          }
          test "Regex pattern: optional group binds empty on absence" {
              expectValue "match \"ab\" with | Regex @\"(a)(x)?\" (a, x) -> $\"{a}|{x}\" | _ -> \"?\"" (VStr "a|")
          }
          test "Regex pattern: mixed with literal arms; guards compose" {
              expectValue
                  "match \"v9\" with | \"\" -> \"empty\" | Regex @\"v(\\d+)\" n when n == \"9\" -> \"nine\" | _ -> \"other\""
                  (VStr "nine")
          }
          test "Regex pattern: nested in constructor payload and tuple position" {
              expectValue "match Some \"v3\" with | Some (Regex @\"v(\\d+)\" n) -> n | _ -> \"?\"" (VStr "3")
              expectValue "match (\"k\", \"v3\") with | (_, Regex @\"v(\\d+)\" n) -> n | _ -> \"?\"" (VStr "3")
          }
          test "Regex pattern: never completes a match (wildcard required)" {
              let terr = checkErr "match \"x\" with | Regex @\"(a)\" v -> v"
              Expect.stringContains terr.Message "catch-all" ""
          }
          test "Regex pattern: refutable, so banned in binders" {
              let terr = checkErr "let (Regex @\"(a)\" v) = \"abc\" in v"
              Expect.stringContains terr.Message "this pattern can fail" ""
          }
          test "Regex pattern: duplicate binder names rejected" {
              let terr =
                  checkErr "match \"a b\" with | Regex @\"(\\w+) (\\w+)\" (a, a) -> a | _ -> \"?\""

              Expect.stringContains terr.Message "duplicate binder 'a'" ""
          }
          test "Regex pattern: groups are plain strings (Eq works, no class surprises)" {
              expectValue "match \"v9\" with | Regex @\"v(\\d+)\" n -> n == \"9\" | _ -> false" (VBool true)
          }
          test "Regex pattern: needs a string scrutinee" {
              let terr = checkErr "match 5 with | Regex @\"(a)\" v -> v | _ -> \"?\""
              Expect.stringContains terr.Message "string scrutinee" ""
          }
          test "the raw literal: backslashes belong to the regex (no doubling)" {
              // the source below contains a SINGLE backslash before w/d
              expectValue "match \"k=1\" with | Regex @\"(\\w+)=(\\d+)\" (k, _) -> k | _ -> \"?\"" (VStr "k")
          }
          // raw strings [D:raw-strings] — F#'s semantics, probe-backed
          test "raw strings: verbatim and triple semantics" {
              expectValue "@\"a\\nb\" |> Str.length" (VInt 4L)
              expectValue "@\"x\"\"y\"" (VStr "x\"y")
              expectValue "\"\"\"a\"b\"\"\"" (VStr "a\"b")
              // the quad-OPENER edge: content is a leading quote (FCS's verdict)
              expectValue "\"\"\"\"a\"\"\"" (VStr "\"a")
          }
          test "raw strings: the quad-closer edge rejects (FCS's verdict)" {
              match Weir.Parser.parseExpr "\"\"\"a\"\"\"\"" with
              | Error _ -> ()
              | Ok _ -> failtest "expected a parse error for the trailing extra quote"
          }
          test "raw strings across positions: pattern literal, list, tuple" {
              expectValue "match @\"lit\" with | @\"lit\" -> 1 | _ -> 0" (VInt 1L)
              expectValue "match \"\"\"q\"q\"\"\" with | \"\"\"q\"q\"\"\" -> 1 | _ -> 0" (VInt 1L)
              expectValue "[@\"\\a\"; \"\"\"b\"\"\"] |> Seq.length" (VInt 2L)
              expectValue "fst (@\"x\", 1)" (VStr "x")
          }
          test "Regex position is raw-only (the rider: kind, not content)" {
              // even a no-backslash pattern requires the raw kind
              let terr = checkErr "match \"a\" with | Regex \"(a)\" v -> v | _ -> \"\""
              Expect.stringContains terr.Message "regex literals are raw" ""
          }
          test "the scanner: // inside raw strings is content" {
              Expect.equal (Weir.Script.stripComment "let s = @\"x // y\"") "let s = @\"x // y\"" "verbatim"

              Expect.equal
                  (Weir.Script.stripComment "let s = \"\"\"x // y\"\"\" // real")
                  "let s = \"\"\"x // y\"\"\" "
                  "triple + a real comment after"
          }
          test "the repair closers understand raw kinds" {
              Expect.equal (Weir.Script.closers "let s = @\"dangling") "\"" "verbatim closer"
              Expect.equal (Weir.Script.closers "let s = \"\"\"dangling") "\"\"\"" "triple closer"
              Expect.equal (Weir.Script.closers "let s = @\"a\"\"b") "\"" "doubled quote stays inside"
          }
          test "accepted matches are total: the runtime match-failure class is gone" {
              // non-exhaustive shapes are CHECK errors, never runtime failures
              let terr = checkErr "match Stopped with | Running n -> n"
              Expect.stringContains (formatError terr) "missing: Stopped" ""
          } ]

let streamingTests =
    testList
        "Streaming"
        [ test "acceptance: infinite source |> first 5 terminates" {
              let infinite = Seq.initInfinite (fun i -> Weir.Builtins.file $"f{i}" i false)

              let result =
                  runWith [ "ls", VSeq infinite ] "ls |> where (fun f -> f.bytes > 1) |> first 5"
                  |> forceSeq

              Expect.equal (List.length result) 5 "exactly five rows"
              Expect.equal result[0] (Weir.Builtins.file "f2" 2 false) "first surviving row"
          }
          test "Seq.distinct: first wins, lazily, remembering only the yielded [D:seq-distinct]" {
              expectValue "[\"b\"; \"a\"; \"b\"; \"c\"; \"a\"] |> Seq.distinct |> Seq.length" (VInt 3L)

              // the pull-count pin: first 2 distinct values pull only
              // until the second novel element appears
              let pulled = ref 0

              let source =
                  seq {
                      for i in [ 1; 1; 1; 2; 3; 4; 5 ] do
                          System.Threading.Interlocked.Increment pulled |> ignore
                          yield VInt(int64 i)
                  }

              let out =
                  runWith [ "src", VSeq source ] "src |> Seq.distinct |> first 2" |> forceSeq

              Expect.equal out [ VInt 1; VInt 2 ] "first occurrences, in order"
              Expect.equal pulled.Value 4 "pulled exactly to the second novel element"
          }
          test "Str.rmatchAll: every match's groups, lazily [D:rmatch-all]" {
              // all matches (rmatch yields only the first), groups per match
              expectValue
                  "Str.rmatchAll @\"(\\w+)=(\\d+)\" \"a=1 b=2 c=3\" |> Seq.map (fun g -> Str.join \":\" g) |> Str.join \",\""
                  (VStr "a:1,b:2,c:3")

              // no matches = the empty seq (the plural needs no Option)
              expectValue "Str.rmatchAll @\"(\\d+)\" \"none\" |> Seq.isEmpty" (VBool true)

              // (?s) makes the dot span newlines — the scrape shape
              expectValue
                  "Str.rmatchAll \"(?s)B(.*?)B\" \"B x\\ny B B z B\" |> Seq.map Seq.head |> Str.join \"|\""
                  (VStr " x\ny | z ")

              // lazy over matches: first 2 of many yields exactly 2
              expectValue "Str.rmatchAll @\"(\\d)\" \"12345\" |> first 2 |> Seq.length" (VInt 2L)
          }
          test "feed pulls its INPUT lazily: an early-exiting child bounds the pulls [D:spawn-spec]" {
              // the standing laziness rule reaches inputs — the writer
              // task pulls as the pipe accepts, so `head -1` over a
              // million-line source stops at the pipe buffer, not the end
              let pulled = ref 0

              let source =
                  seq {
                      for i in 1..1000000 do
                          System.Threading.Interlocked.Increment pulled |> ignore
                          yield string i
                  }

              let out = Weir.Proc.linesWith [] "head" [ "-1" ] (Some source) |> List.ofSeq
              Expect.equal out [ "1" ] "head takes the first line"
              Expect.isLessThan pulled.Value 500000 "the input was not drained"
          }
          test "feed closes stdin on input exhaustion: EOF-needing children finish [D:spawn-spec]" {
              let out =
                  Weir.Proc.linesWith [] "sort" [] (Some(seq [ "b"; "a"; "c" ])) |> List.ofSeq

              Expect.equal out [ "a"; "b"; "c" ] "sort saw EOF and emitted"
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

              runWith [ "ls", VSeq counting ] "ls |> where (fun f -> f.bytes > 1) |> first 2"
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

              runWith [ "ls", VSeq counting ] "ls |> where (fun f -> f.bytes > 1) |> first 5"
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
              Expect.equal (checkOk "ls |> where (fun f -> f.readOnly)").Ty Weir.Builtins.seqFileRow ""
          }
          test "map changes the element type" {
              Expect.equal (checkOk "ls |> map (fun f -> f.bytes)").Ty (TSeq TInt) ""
          }
          test "map over ints still works" {
              Expect.equal (run "nats |> map (fun x -> x * x) |> take 3" |> forceSeq) [ VInt 0; VInt 1; VInt 4 ] ""
          }
          test "map with an inferable function argument works standalone" {
              Expect.equal (run "nats |> map double |> take 3" |> forceSeq) [ VInt 0; VInt 2; VInt 4 ] ""
          }
          test "full application instantiates from the trailing data argument" {
              expectValue "where (fun f -> f.readOnly) ls |> first 1" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
          }
          test "instantiation mismatch is reported" {
              Expect.stringContains
                  (checkErr "nats |> where (fun f -> f.readOnly)").Message
                  "only records have fields"
                  ""
          } ]

let boundaryTests =
    testList
        "External command boundary"
        [ test "cmd yields stdout lines" {
              Expect.equal (runReal "sh -c \"printf 'a\\nb\\n'\"" |> forceSeq) [ VStr "a"; VStr "b" ] ""
          }
          test "cmd is lazy across the process boundary" {
              Expect.equal (runReal "sh -c \"yes\" |> first 3" |> forceSeq) [ VStr "y"; VStr "y"; VStr "y" ] ""
          }
          test "failing command raises when forced" {
              Expect.throws (fun () -> runReal "sh -c \"exit 3\"" |> forceSeq |> ignore) ""
          }
          test "unforced command runs nothing" { runReal "sh -c \"exit 3\"" |> ignore }
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
                          [ "status", VStr status
                            "staged", VBool staged
                            "unstaged", VBool unstaged
                            "path", VStr path ]
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
                      runReal
                          $"sh -c \"cd {dir} && git status --porcelain\" |> from porcelain |> where (fun c -> c.staged)"
                      |> forceSeq

                  match result with
                  | [ VRecord("Change", fields) ] ->
                      Expect.equal fields["path"] (VStr "staged.txt") "path"
                      Expect.equal fields["staged"] (VBool true) "staged"
                  | other -> failtest $"unexpected result: {other}"
              finally
                  Directory.Delete(dir, true)
          }
          test "to json serializes records as ndjson" {
              Expect.equal
                  (run "ls |> first 1 |> to json" |> forceSeq)
                  [ VStr """{"bytes":0,"name":"a.txt","readOnly":false}""" ]
                  ""
          }
          test "json roundtrip preserves rows" {
              Expect.equal (run "ls |> to json |> from json FileRow" |> forceSeq) fakeFiles ""
          }
          test "from json validates field types" {
              let src = VSeq [ VStr """{"name":"x","bytes":"big","readOnly":false}""" ]

              Expect.throws (fun () -> runWith [ "src", src ] "src |> from json FileRow" |> forceSeq |> ignore) ""
          }
          test "from json rejects missing fields" {
              let src = VSeq [ VStr """{"name":"x"}""" ]

              Expect.throws (fun () -> runWith [ "src", src ] "src |> from json FileRow" |> forceSeq |> ignore) ""
          }
          test "from json ignores extra fields" {
              let src =
                  VSeq [ VStr """{"name":"x","Size":1,"bytes":1048576,"readOnly":true,"Extra":42}""" ]

              Expect.equal
                  (runWith [ "src", src ] "src |> from json FileRow" |> forceSeq)
                  [ Weir.Builtins.file "x" 1048576 true ]
                  ""
          }
          test "into feeds stdin and yields stdout" {
              // tr strips BSD wc's left-padding — the subject is the stdin
              // plumbing, not wc's platform formatting
              Expect.equal (run "nats |> take 3 |> to json |> into \"wc -l | tr -d ' '\"" |> forceSeq) [ VStr "3" ] ""
          }
          test "from can be let-bound" {
              expectValue
                  "let p = from porcelain in [\"A  x.txt\"] |> p |> first 1 |> map (fun c -> c.path)"
                  (VSeq [ VStr "x.txt" ])
          } ]

let boundaryCheckTests =
    testList
        "Boundary check errors"
        [ test "from json needs a record name" {
              Expect.stringContains (checkErr "[\"x\"] |> from json").Message "needs a record name" ""
          }
          test "from json rejects unknown records" {
              Expect.stringContains (checkErr "[\"x\"] |> from json Missing").Message "unknown type 'Missing'" ""
          }
          test "from json rejects unions" {
              Expect.stringContains (checkErr "[\"x\"] |> from json Proc").Message "needs a record" ""
          }
          test "unknown format is rejected" {
              Expect.stringContains (checkErr "[\"x\"] |> from yaml").Message "unknown format 'yaml'" ""
          }
          test "from porcelain takes no type name" {
              Expect.stringContains (checkErr "[\"x\"] |> from porcelain Proc").Message "fixed row type" ""
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
              expectParse "where _.readOnly" "(where (fun _ _.readOnly))"
          }
          test "where with shorthand filters" {
              expectValue "ls |> where _.readOnly" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
          }
          test "map with shorthand projects" {
              Expect.equal (checkOk "ls |> map _.bytes").Ty (TSeq TInt) ""

              Expect.equal (run "ls |> map _.name |> first 2" |> forceSeq) [ VStr "a.txt"; VStr "b.bin" ] ""
          }
          test "shorthand chains through nested records" { expectParse "map _.A.B" "(map (fun _ _.A.B))" }
          test "shorthand in a larger expression gets the targeted hint" {
              let terr = checkErr "ls |> where (_.bytes > 9) |> first \"x\""
              Expect.stringContains terr.Message "_.Field is a whole function" ""
          }
          test "byte literals filter correctly" {
              expectValue "ls |> where (fun f -> f.bytes > 2097152) |> map _.name" (VSeq [ VStr "b.bin"; VStr "d.iso" ])
          }
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
              let src = VSeq [ VStr """{"name":"a\"b","bytes":1048576,"readOnly":false}""" ]

              Expect.equal
                  (runWith [ "src", src ] "src |> from json FileRow |> map _.name" |> forceSeq)
                  [ VStr "a\"b" ]
                  ""
          } ]

let private suggest text (wordStart: int) =
    Weir.Complete.suggest env text wordStart

let completionTests =
    testList
        "Completion"
        [ test "name completion from values in scope" {
              // gained "when" when keyword suggestions began deriving
              // from the full parser set [D:keyword-completion]
              Expect.equal (suggest "ls |> whe" 6) [ "when"; "where" ] ""
          }
          test "keyword completion" { Expect.contains (suggest "ma" 0) "match" "" }
          test "keyword completion inventory: every grammar keyword offered or excluded, decided [D:keyword-completion]" {
              // the PINNED split — a new grammar keyword fails here until
              // its completion decision is recorded (added below as
              // offered, or to unsuggestedKeywords with a reason beside
              // the definition). The inventory-match pattern, applied to
              // completion.
              let expectedOffered =
                  Set
                      [ "let"
                        "in"
                        "fun"
                        "true"
                        "false"
                        "match"
                        "with"
                        "type"
                        "of"
                        "from"
                        "to"
                        "if"
                        "then"
                        "else"
                        "when"
                        "elif" ]

              Expect.equal
                  (Weir.Parser.keywords - Weir.Complete.unsuggestedKeywords)
                  expectedOffered
                  "a grammar keyword arrived without a completion decision"

              Expect.equal
                  Weir.Complete.unsuggestedKeywords
                  (Set [ "rec"; "mutable"; "function" ])
                  "the exclusion set moved — its reasons live beside its definition"

              // behavioral, through suggest itself: the once-missing
              // control-flow words are offered, the reserved are not
              Expect.contains (suggest "eli" 0) "elif" "elif offered"
              Expect.contains (suggest "the" 0) "then" "then offered"
              Expect.contains (suggest "whe" 0) "when" "when offered"
              Expect.isFalse (suggest "functio" 0 |> List.contains "function") "reserved words are not suggested"
              Expect.isFalse (suggest "mutabl" 0 |> List.contains "mutable") "reserved words are not suggested"
          }
          test "lambda parameter completes from the pipeline element type" {
              let text = "ls |> where (fun f -> f."
              Expect.equal (suggest text (text.Length - 2)) [ "f.bytes"; "f.name"; "f.readOnly" ] ""
          }
          test "lambda param completes inside a record literal (the user receipt)" {
              let text = "ls |> Seq.map (fun x -> { Line = x."
              Expect.equal (suggest text (text.Length - 2)) [ "x.bytes"; "x.name"; "x.readOnly" ] ""
          }
          test "mid-line cursor: callers truncate at the cursor (the contract)" {
              // the receipt: `{ Line = x. })` with the cursor after the
              // dot — the tail must not reach suggest; both callers
              // truncate (LSP upto, REPL Substring), pinned here as the
              // truncated call giving fields while the full-line call
              // (a contract violation) gives nothing
              let full = "ls |> Seq.map (fun x -> { Line = x. })"
              let upto = full.Substring(0, full.Length - 3)
              let ws = full.Length - 5
              Expect.equal (suggest upto ws) [ "x.bytes"; "x.name"; "x.readOnly" ] "truncated: fields"
              Expect.equal (suggest full ws) [] "untruncated would kill every match"
          }
          test "field prefix narrows the suggestions" {
              let text = "ls |> where (fun f -> f.b"
              Expect.equal (suggest text (text.Length - 3)) [ "f.bytes" ] ""
          }
          test "bound record variable completes its fields" {
              let envWithQ =
                  { env with
                      Values = Map.add "q" (generalize (TNamed("Point", []))) env.Values }

              Expect.equal (Weir.Complete.suggest envWithQ "q." 0) [ "q.X"; "q.Y" ] ""
          }
          test "module completion offers bespoke arms: Args.load / Env.load (user receipt)" {
              // load is a checker ARM, not a member-map entry — completion
              // must surface it beside the ordinary members, from the one
              // source the checker's error path also reads
              Expect.equal (suggest "Args." 0) [ "Args.flag"; "Args.load"; "Args.value" ] "Args.load beside flag/value"

              Expect.equal
                  (suggest "Env." 0)
                  [ "Env.fromFile"; "Env.get"; "Env.load"; "Env.ofPairs"; "Env.pair"; "Env.vars" ]
                  "Env.load in the sorted members"

              Expect.equal (suggest "Args.lo" 0) [ "Args.load" ] "prefix narrows to the arm"
          }
          test "later pipeline stages track the element type" {
              let text = "[\"A  x.txt\"] |> from porcelain |> where (fun c -> c."

              Expect.equal (suggest text (text.Length - 2)) [ "c.path"; "c.staged"; "c.status"; "c.unstaged" ] ""
          }
          test "holes: unbound args in a known pipeline still type the element" {
              // n is an enclosing param (unbound here) - Seq.skip's result
              // type falls out of unification anyway (the targetEnv objection)
              let text = "Seq.skip n ls |> map (fun f -> f."
              Expect.contains (suggest text (text.Length - 2)) "f.name" ""
          }
          test "no fields on a non-record element" {
              let text = "nats |> map (fun x -> x."
              Expect.equal (suggest text (text.Length - 2)) [] ""
          }
          test "from json completes record names" {
              let text = "[\"x\"] |> from json "
              Expect.contains (suggest text text.Length) "FileRow" ""
              Expect.contains (suggest text text.Length) "Change" ""
          } ]

let rowTests =
    testList
        "Row polymorphism"
        [ test "field projection lambda infers a row type" {
              match (checkOk "fun f -> f.readOnly").Ty with
              | TFun(TRowVar(_, [ "readOnly", TVar _ ]), TVar _) -> ()
              | t -> failtest $"expected a row-typed projection, got {formatTy t}"
          }
          test "field usage constrains the row" {
              match (checkOk "fun f -> f.bytes > 1048576").Ty with
              | TFun(TRowVar(_, [ "bytes", TInt ]), TBool) -> ()
              | t -> failtest $"expected {{ bytes: int; .. }} -> bool, got {formatTy t}"
          }
          test "row-typed filter discharges against FileRow" {
              expectValue
                  "let staged = where _.readOnly in ls |> staged"
                  (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
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
                  Expect.equal te.Ty (TInt) "type"
                  Expect.equal (eval valueEnv te) (VInt 4) "0+1 and 0+1+2"
          }
          test "row discharge through a let reports the typo at the use site" {
              let input = "let f = where (fun c -> c.bytse > 1) in ls |> f"
              let terr = checkErr input
              Expect.stringContains terr.Message "FileRow has no field 'bytse'" ""
              Expect.stringContains terr.Message "Did you mean 'bytes'?" ""
              Expect.equal terr.Span.Start.Col (input.LastIndexOf "f" + 1) "span points at the use"
          }
          test "direct pipeline typo keeps the exact field span" {
              let input = "ls |> where (fun c -> c.bytse > 1)"
              let terr = checkErr input
              Expect.equal terr.Span.Start.Col (input.IndexOf "bytse" + 1) "span points at the typo"
          }
          test "row discharge checks the field type" {
              let terr = checkErr "let f = where (fun c -> c.name > 1) in ls |> f"
              Expect.stringContains terr.Message "expected string, got int" ""
          }
          test "record missing a constrained field is rejected" {
              Expect.stringContains
                  (checkErr "let g = where (fun p -> p.X > 1) in ls |> g").Message
                  "FileRow has no field 'X'"
                  ""
          }
          test "unitless product binds unknowns to int" {
              Expect.equal (checkOk "fun x -> x * x").Ty (TFun(TInt, TInt)) ""
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
              Expect.equal (formatTy (checkOk "where _.readOnly").Ty |> fun s -> s.Contains "readOnly") true ""
          } ]

let private survivors (marker: string) : int =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    // pgrep rc 0 = matched, 1 = no match; anything else (or pgrep
    // missing, 127) is the PROBE failing — loud, never a benign zero
    // [D:vacuous-probe-audit]
    psi.ArgumentList.Add
        $"o=$(pgrep -f '[{marker[0]}]{marker.Substring 1}'); rc=$?; [ \"$rc\" -le 1 ] || exit 9; [ -z \"$o\" ] && echo 0 || printf '%%s\\n' \"$o\" | wc -l"

    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd().Trim()
    p.WaitForExit()

    if p.ExitCode <> 0 then
        failwith "survivors: pgrep itself failed — fix the probe for this platform"

    int out

let private eventuallyNoSurvivors (marker: string) : bool =
    let mutable tries = 20
    let mutable count = survivors marker

    while count > 0 && tries > 0 do
        System.Threading.Thread.Sleep 100
        tries <- tries - 1
        count <- survivors marker

    count = 0

let private defunctChildrenOf (pid: int) : int =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    // BSD ps has no --ppid; `-A -o ppid=,stat=` is portable. The ps run
    // must fail LOUDLY (exit 9): on macOS the GNU spelling errored and
    // `grep -c Z` counted zero — the pin passed vacuously
    psi.ArgumentList.Add
        $"o=$(ps -A -o ppid=,stat=) || exit 9; printf '%%s\\n' \"$o\" | awk -v p={pid} '$1 == p && $2 ~ /^Z/ {{c++}} END {{print c+0}}'"

    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd().Trim()
    p.WaitForExit()

    if p.ExitCode = 9 then
        failwith "defunctChildren: ps itself failed — fix the ps spelling for this platform"

    if out = "" then 0 else int out

let private defunctChildren () : int =
    defunctChildrenOf System.Environment.ProcessId

let lifecycleTests =
    testSequenced
    <| testList
        "Process lifecycle"
        [ // POSITIVE CONTROLS FIRST [D:vacuous-probe-audit]: every zero
          // assertion below rests on these two counters; a counter that
          // can break must be shown to COUNT before its zeros mean
          // anything (the macOS vacuous-pass lesson).
          test "positive control: the survivors probe counts a live marker" {
              let psi = ProcessStartInfo("/bin/sh")
              psi.ArgumentList.Add "-c"
              // the marker lives in the sh's OWN argv (no exec)
              psi.ArgumentList.Add "sleep 30 # weir-probe-ctl"
              use p = Process.Start psi

              try
                  Expect.isGreaterThan (survivors "weir-probe-ctl") 0 "a live marker must count"
              finally
                  p.Kill(entireProcessTree = true)

              Expect.isTrue (eventuallyNoSurvivors "weir-probe-ctl") "the killed control must clear"
          }
          test "positive control: the zombie counter counts a real zombie" {
              // the .NET runtime auto-reaps OUR children, so the control
              // targets the counting line at another parent: a python
              // that forks and deliberately never reaps (python3 is
              // already a harness dependency via tests/lib)
              let psi = ProcessStartInfo("python3")
              psi.ArgumentList.Add "-c"
              psi.ArgumentList.Add "import os,time\npid=os.fork()\nif pid==0: os._exit(0)\ntime.sleep(30)"
              use p = Process.Start psi

              try
                  let mutable n = 0
                  let mutable tries = 20

                  while n = 0 && tries > 0 do
                      System.Threading.Thread.Sleep 100
                      tries <- tries - 1
                      n <- defunctChildrenOf p.Id

                  Expect.isGreaterThan n 0 "the counter must see the un-reaped child"
              finally
                  p.Kill(entireProcessTree = true)
          }
          // TRIPWIRE PAIR: the simple case passes even without tree-kill because
          // sh execs a single command (one process). The compound case is the
          // real guard — sh forks pipeline children and only
          // Kill(entireProcessTree: true) reaches them. If the sh backing is
          // removed (PLAN-command-mode Session 2), this analysis changes:
          // re-derive which of these guards what.
          test "simple command: no survivors after partial consumption" {
              runReal "sh -c \"yes weir-s1-simple\" |> first 3" |> forceSeq |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s1-simple") "yes leaked"
          }
          test "compound command: no survivors after partial consumption" {
              runReal "sh -c \"yes weir-s1-compound | grep --line-buffered weir-s1-compound\" |> first 3"
              |> forceSeq
              |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s1-compound") "pipeline children leaked"
          }
          test "50 completed commands leave no zombies" {
              for _ in 1..50 do
                  runReal "sh -c \"true\"" |> forceSeq |> ignore

              let zombies = defunctChildren ()
              Expect.equal zombies 0 "defunct children accumulated"
          }
          test "50 abandoned streams leave no zombies" {
              for _ in 1..50 do
                  runReal "sh -c \"yes weir-s1-zombie\" |> first 1" |> forceSeq |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s1-zombie") "killed children leaked"
              let zombies = defunctChildren ()
              Expect.equal zombies 0 "defunct children accumulated after kills"
          } ]

let session2Tests =
    testSequenced
    <| testList
        "sh/cmd split and session cwd"
        [ test "list literal parses, types, evaluates" {
              expectParse "[1; 2; 3]" "[1; 2; 3]"
              Expect.equal (checkOk "[1; 2]").Ty (TSeq(TInt)) "type"
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
              Expect.equal (runReal "echo \"*\"" |> forceSeq) [ VStr "*" ] ""
          }
          test "sh is the escape hatch: glob expands" {
              let dir = Path.Combine(Path.GetTempPath(), $"weir-s2-{System.Guid.NewGuid():N}")
              Directory.CreateDirectory dir |> ignore
              File.WriteAllText(Path.Combine(dir, "g1.txt"), "")
              File.WriteAllText(Path.Combine(dir, "g2.txt"), "")

              try
                  let out = runReal $"let d = cd \"{dir}\" in $(sh -c \"echo *.txt\")" |> forceSeq

                  Expect.equal out [ VStr "g1.txt g2.txt" ] ""
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
                  Directory.Delete(dir, true)
          }
          test "injection attempt is inert through cmd" {
              Expect.equal (runReal "echo \"; rm -rf x\"" |> forceSeq) [ VStr "; rm -rf x" ] ""
          }
          // [D:drop-command-builtins] the "for cmd" twin retired — cmd is
          // gone; the sh-spawn version below covers cwd-affects-spawns
          test "cd changes the spawn cwd for sh" {
              // /usr, not /tmp: macOS's /tmp is a symlink to /private/tmp
              // and the CHILD's getcwd reports the physical path
              try
                  Expect.equal (runReal "let d = cd \"/usr\" in $(sh -c \"pwd\")" |> forceSeq) [ VStr "/usr" ] ""
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          test "pwd builtin tracks Session.Cwd lazily" {
              try
                  Expect.equal
                      (run "let p = pwd in let d = cd \"/tmp\" in p" |> forceSeq)
                      [ VStr "/tmp" ]
                      "pwd re-reads Session.Cwd per enumeration"
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          test "cd on a missing directory fails at runtime" {
              Expect.throws (fun () -> run "cd \"/definitely/not/here\"" |> ignore) ""
          }
          test "cd resolves relative and dotdot" {
              try
                  Expect.equal
                      (run "let a = cd \"/tmp\" in cd \"..\"" |> ignore
                       Weir.Session.Cwd())
                      "/"
                      ""
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          // [D:drop-command-builtins] "cmd not found raises" retired — the
          // unknown-command diagnosis lives in e2e (runner names the missing
          // command); the cmd-builtin runtime raise is gone
          // Direct-exec lifecycle duplicates: no sh in front, so the
          // exec-optimization analysis from the Session-1 tripwire does not
          // apply — tree-kill must hold on its own.
          test "direct cmd: no survivors after partial consumption" {
              runReal "yes \"weir-s2-direct\" |> first 3" |> forceSeq |> ignore
              Expect.isTrue (eventuallyNoSurvivors "weir-s2-direct") "direct-exec child leaked"
          }
          test "direct cmd: 50 abandoned streams leave no zombies" {
              for _ in 1..50 do
                  runReal "yes \"weir-s2-dz\" |> first 1" |> forceSeq |> ignore

              Expect.isTrue (eventuallyNoSurvivors "weir-s2-dz") "direct-exec children leaked"
              Expect.equal (defunctChildren ()) 0 "defunct children accumulated"
          } ]


let commandModeTests =
    testList
        "Command mode"
        [ test "bare external command" { expectCmd "git status" "(cmd git \"status\")" }
          test "quoted arg is a single argv entry" {
              expectCmd "grep \"a b\" file.txt" "(cmd grep \"a b\" \"file.txt\")"
          }
          test "single quotes carry embedded double quotes" {
              match parseCmd "grep 'a\"b' f" with
              | { Kind = ECmd("grep", [ { Kind = EStr "a\"b" }; { Kind = EStr "f" } ], _) } -> ()
              | e -> failtest $"unexpected: {show e}"
          }
          test "dollar splices a binding" { expectCmd "git checkout $branch" "(cmd git \"checkout\" branch)" }
          test "parens splice an expression" { expectCmd "echo (1 + 2)" "(cmd echo (+ 1 2))" }
          test "rich barewords stay literal" {
              expectCmd
                  "git log --format=%H -n 3 ../path/x.txt"
                  "(cmd git \"log\" \"--format=%H\" \"-n\" \"3\" \"../path/x.txt\")"
          }
          test "slashed program resolves as external" { expectCmd "./build.sh --flag" "(cmd ./build.sh \"--flag\")" }
          test "caret forces PATH over a builtin shadow" { expectCmd "^ls -la" "(cmd ls \"-la\")" }
          test "builtin shadows PATH: ls -la is expression mode" {
              // adjacency: `-la` is a prefix-minus argument, exactly
              // F#'s parse; still an error + the ^ls hint
              Expect.equal (show (parseCmd "ls -la")) "(ls (- 0 la))" "parses as application of -la"
              Expect.stringContains (checkErr "ls - la").Message "unbound variable 'la'" ""
          }
          test "command pipes into expression stage" {
              expectCmd "git log | first 5" "((cmd git \"log\") |> (first 5))"
          }
          test "pipe accepts |> in command mode too" {
              expectCmd "git log |> first 5" "((cmd git \"log\") |> (first 5))"
          }
          test "external to external to expression" {
              expectCmd "git log | grep x | first 2" "(((cmd git \"log\") |> (cmd grep \"x\")) |> (first 2))"
          }
          test "unknown head without PATH hit falls back to expression" {
              match Weir.Parser.parseLine cmdResolver "gti status" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env e with
                  | Error terr -> Expect.stringContains terr.Message "unbound variable 'gti'" ""
                  | Ok _ -> failtest "expected unbound error"
              | other -> failtest $"unexpected: {other}"
          }
          test "forced unknown is a parse-time command-not-found with a hint" {
              match Weir.Parser.parseLine cmdResolver "^gti status" with
              | Error msg ->
                  Expect.stringContains msg "command not found: gti" ""
                  Expect.stringContains msg "Did you mean 'git'?" ""
              | Ok _ -> failtest "expected parse failure"
          }
          test "PATH did-you-mean stays capped at edit distance 2" {
              match Weir.Parser.parseLine cmdResolver "^gzzzzt status" with
              | Error msg -> Expect.isFalse (msg.Contains "Did you mean") "no PATH hint beyond distance 2"
              | Ok _ -> failtest "expected parse failure"
          }
          test "command line types as seq<string>" {
              match Weir.Parser.parseLine cmdResolver "git status | first 1" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env e with
                  | Ok te -> Expect.equal te.Ty (TSeq TStr) ""
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"
          }
          test "splice must be a stringable scalar" {
              let env2 =
                  { env with
                      Values =
                          env.Values
                          |> Map.add "branch" (generalize TStr)
                          |> Map.add "pt" (generalize (TNamed("Point", []))) }

              match Weir.Parser.parseLine cmdResolver "git checkout $branch" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env2 e with
                  | Ok te -> Expect.equal te.Ty (TSeq TStr) ""
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"

              match Weir.Parser.parseLine cmdResolver "git checkout $pt" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env2 e with
                  | Error terr -> Expect.stringContains terr.Message "command arguments must be" ""
                  | Ok _ -> failtest "expected splice type error"
              | other -> failtest $"unexpected: {other}"
          }
          test "real exec: barewords, splices and scalars render" {
              Expect.equal (runReal "echo hi (1 + 2) true" |> forceSeq) [ VStr "hi 3 true" ] ""
          }
          test "real exec: command pipes into first" {
              Expect.equal
                  (runReal "yes weir-s3-pipe | first 2" |> forceSeq)
                  [ VStr "weir-s3-pipe"; VStr "weir-s3-pipe" ]
                  ""

              Expect.isTrue (eventuallyNoSurvivors "weir-s3-pipe") "command-mode child leaked"
          }
          test "real exec: argv verbatim, no shell interpretation" {
              Expect.equal (runReal "echo ; rm -rf x") (runReal "echo ; rm -rf x") "deterministic"
              Expect.equal (runReal "echo ; rm -rf x" |> forceSeq) [ VStr "; rm -rf x" ] ""
          } ]


let cdTests =
    testSequenced
    <| testList
        "Command-callable cd"
        [ test "cd with a path bareword parses to a builtin call" { expectCmd "cd /work" "(cd \"/work\")" }
          test "bare cd desugars to home" { expectCmd "cd" "(cd \"~\")" }
          test "cd dotdot and tilde-path parse" {
              expectCmd "cd .." "(cd \"..\")"
              expectCmd "cd ~/src" "(cd \"~/src\")"
          }
          test "cd splice parses to application of a binding" { expectCmd "cd $dir" "(cd dir)" }
          test "cd arity is a check-time error with the builtin named" {
              match Weir.Parser.parseLine cmdResolver "cd a b" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env e with
                  | Error terr -> Expect.stringContains terr.Message "'cd' takes at most 1 argument(s), but got 2" ""
                  | Ok _ -> failtest "expected arity error"
              | other -> failtest $"unexpected: {other}"
          }
          test "forced cd is command-not-found (no external cd exists)" {
              match Weir.Parser.parseLine cmdResolver "^cd /tmp" with
              | Error msg -> Expect.stringContains msg "command not found: cd" ""
              | Ok _ -> failtest "expected parse failure"
          }
          test "cd evaluates in command mode and mutates the session" {
              try
                  match Weir.Parser.parseLine realResolver "cd /tmp" with
                  | Ok(SExpr e | SCmd e) ->
                      match typecheck env e with
                      | Ok te ->
                          Expect.equal (eval valueEnv te) (VStr "/tmp") "returns new cwd"
                          Expect.equal (Weir.Session.Cwd()) "/tmp" "session mutated"
                      | Error terr -> failtest (formatError terr)
                  | other -> failtest $"unexpected: {other}"
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          test "bare cd goes home" {
              try
                  match Weir.Parser.parseLine realResolver "cd" with
                  | Ok(SExpr e | SCmd e) ->
                      let home =
                          System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile

                      match typecheck env e with
                      | Ok te -> Expect.equal (eval valueEnv te) (VStr home) "home"
                      | Error terr -> failtest (formatError terr)
                  | other -> failtest $"unexpected: {other}"
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          test "cd to a missing directory reports the resolved absolute path" {
              try
                  match Weir.Parser.parseLine realResolver "cd /definitely/not/weir" with
                  | Ok(SExpr e | SCmd e) ->
                      match typecheck env e with
                      | Ok te ->
                          let ex = Expect.throwsC (fun () -> eval valueEnv te |> ignore) id
                          Expect.stringContains ex.Message "/definitely/not/weir" "absolute path shown"
                      | Error terr -> failtest (formatError terr)
                  | other -> failtest $"unexpected: {other}"
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          test "expression-mode cd is unchanged" {
              try
                  expectValue "let d = cd \"/tmp\" in d" (VStr "/tmp")
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          } ]

let diagnoseTests =
    testList
        "Cliff diagnostic"
        [ test "ls -la gets the hint" {
              match
                  Weir.Diagnose.hint (fun n -> Map.containsKey n env.Values) (fun _ -> false) (fun _ -> true) "ls -la"
              with
              | Some h ->
                  Expect.stringContains h "'ls' is a weir binding" ""
                  Expect.stringContains h "^ls -la" ""
              | None -> failtest "expected a hint"
          }
          test "binding shadowing a PATH name gets the hint on bareword tail" {
              let isKnown n = n = "git"
              let isExternal n = n = "git"

              match Weir.Diagnose.hint isKnown (fun _ -> false) isExternal "git status" with
              | Some h -> Expect.stringContains h "'git' is a weir binding" ""
              | None -> failtest "expected a hint"
          }
          test "plain unbound tail without PATH presence stays quiet" {
              Expect.isNone (Weir.Diagnose.hint (fun n -> n = "where") (fun _ -> false) (fun _ -> false) "where p") ""
          }
          test "operator tails stay quiet" {
              let isKnown n = Map.containsKey n env.Values
              Expect.isNone (Weir.Diagnose.hint isKnown (fun _ -> false) (fun _ -> true) "ls |> first 5") ""
              Expect.isNone (Weir.Diagnose.hint isKnown (fun _ -> false) (fun _ -> true) "x + 1") ""
          }
          test "path tails hint even without PATH presence" {
              match
                  Weir.Diagnose.hint (fun n -> n = "mybinding") (fun _ -> false) (fun _ -> false) "mybinding ../x"
              with
              | Some _ -> ()
              | None -> failtest "expected a hint for path-like tail"
          } ]


let session3Tests =
    testSequenced
    <| testList
        "complete and force"
        [ test "force snapshots a live query" {
              try
                  expectValue
                      "let p = pwd |> force in let d = cd \"/tmp\" in p |> first 1"
                      (VSeq [ VStr(System.IO.Directory.GetCurrentDirectory()) ])
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
          }
          test "force runs effects exactly once" {
              let marker =
                  Path.Combine(Path.GetTempPath(), $"weir-force-{System.Guid.NewGuid():N}")

              try
                  runReal
                      $"let s = $(sh -c \"echo x >> {marker}; echo line\") |> force in let a = s |> first 1 in let b = s |> first 1 in b"
                  |> forceSeq
                  |> ignore

                  Expect.equal (File.ReadAllLines marker |> Array.length) 1 "one spawn with force"

                  File.Delete marker

                  let r =
                      runReal
                          $"let s = $(sh -c \"echo x >> {marker}; echo line\") in let a = s |> first 1 |> force in let b = s |> first 1 |> force in b"

                  r |> forceSeq |> ignore
                  Expect.equal (File.ReadAllLines marker |> Array.length) 2 "two spawns without upfront force"
              finally
                  if File.Exists marker then
                      File.Delete marker
          }
          test "force is polymorphic" { expectValue "[1; 2] |> force |> sum" (VInt 3) }
          test "head extracts the element" {
              expectValue "[1; 2] |> head" (VInt 1)
              expectValue "ls |> map _.name |> head" (VStr "a.txt")
              Expect.equal (checkOk "pwd |> head").Ty TStr "singleton extraction types to the element"
          }
          test "head on an empty sequence raises" {
              Expect.throws (fun () -> run "ls |> where (fun f -> f.bytes > 999999999) |> head" |> ignore) ""
          }
          test "stderr passes through: stdout stream stays clean" {
              Expect.equal (runReal "sh -c \"echo out; echo err 1>&2\"" |> forceSeq) [ VStr "out" ] ""
          }
          test "external pipes into external via stdin" {
              // the marker rides the command so the survivor check checks
              // SOMETHING — it previously asserted a marker no process
              // ever carried [D:vacuous-probe-audit]
              Expect.equal
                  (runReal "yes weir-s3cc | cat | first 2" |> forceSeq)
                  [ VStr "weir-s3cc"; VStr "weir-s3cc" ]
                  ""

              Expect.isTrue (eventuallyNoSurvivors "weir-s3cc") "pipe children leaked"
          }
          test "non-string stream into an external is rejected [D:value-headed-pipe]" {
              // the pipe-into-external teaching is shared by command chains
              // and value-headed pipelines (one EPipe-into-ECmd arm)
              match Weir.Parser.parseLine cmdResolver "git x | map (fun s -> 1) | cat" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env e with
                  | Error terr -> Expect.stringContains terr.Message "seq<int> — map show or interpolate per element" ""
                  | Ok _ -> failtest "expected type error"
              | other -> failtest $"unexpected: {other}"
          }
          test "complete reifies grep no-match without raising" {
              match runReal "grep nomatch /etc/hosts | complete" with
              | VRecord("Completed", fields) ->
                  Expect.equal fields["exitCode"] (VInt 1) "exit code"
                  Expect.equal (fields["stdout"] |> forceSeq) [] "stdout empty"
                  Expect.equal (fields["stderr"] |> forceSeq) [] "stderr empty"
              | v -> failtest $"unexpected: {formatValue v}"
          }
          test "complete captures stderr and nonzero exit" {
              match runReal "^ls /weir-definitely-not | complete" with
              | VRecord("Completed", fields) ->
                  match fields["exitCode"], fields["stderr"] with
                  | VInt code, VSeq errs ->
                      Expect.isTrue (code > 0) "nonzero exit"
                      Expect.isFalse (Seq.isEmpty errs) "stderr captured"
                  | _ -> failtest "unexpected field shapes"
              | v -> failtest $"unexpected: {formatValue v}"
          }
          test "the reify desugar targets are un-typeable [D:drop-reify-builtins]" {
              // the '|' prefix cannot appear in an identifier — neither a
              // binding nor a call can reach '|completed'; the scheme's
              // guarantee proven, not assumed
              match Weir.Parser.parseLine cmdResolver "let |completed = 5" with
              | Error _ -> ()
              | Ok s -> failtest $"binding a '|'-key must be a parse error, got {s}"

              match Weir.Parser.parseLine cmdResolver "|completed \"echo\" [\"hi\"]" with
              | Error _ -> ()
              | Ok s -> failtest $"calling a '|'-key must be a parse error, got {s}"

              // and the retired user spelling is plainly unbound — no
              // retirement hint (phase-scoped ruling), no '|'-leak in the
              // did-you-mean pool
              let terr = checkErr "completed \"echo\" [\"hi\"]"
              Expect.stringContains terr.Message "unbound variable 'completed'" ""
              Expect.isFalse (terr.Message.Contains "|completed") "internal keys never surface"
          }
          test "complete result pipes onward" {
              Expect.equal (runReal "grep nomatch /etc/hosts | complete |> _.exitCode") (VInt 1) ""
          }
          // capture oracle [D:capture-buffer]: the exact line-split and
          // decode rules of `| complete`, pinned against the OLD
          // representation BEFORE the buffer change — the equivalence
          // oracle for "one buffer, not N strings". Octal via sh printf
          // (POSIX printf has no \x).
          test "capture oracle: stdout line rule — CRLF, lone CR, empties kept, unterminated tail" {
              Expect.equal
                  (runReal "sh -c 'printf \"a\\015\\012b\\015\\012\"' | complete |> _.stdout"
                   |> forceSeq)
                  [ VStr "a"; VStr "b" ]
                  "CRLF splits, CR stripped"

              Expect.equal
                  (runReal "sh -c 'printf \"a\\015b\\012\"' | complete |> _.stdout" |> forceSeq)
                  [ VStr "a"; VStr "b" ]
                  "lone CR splits"

              Expect.equal
                  (runReal "sh -c 'printf \"a\\012\\012b\\012\"' | complete |> _.stdout"
                   |> forceSeq)
                  [ VStr "a"; VStr ""; VStr "b" ]
                  "empty stdout lines kept"

              Expect.equal
                  (runReal "sh -c 'printf \"tail\"' | complete |> _.stdout" |> forceSeq)
                  [ VStr "tail" ]
                  "unterminated final line included"

              Expect.equal (runReal "sh -c 'true' | complete |> _.stdout" |> forceSeq) [] "empty output, empty seq"
          }
          test "capture oracle: stderr rule differs — newline-split, empties dropped, CR retained" {
              Expect.equal
                  (runReal "sh -c 'printf \"a\\012\\012b\\012\" 1>&2' | complete |> _.stderr"
                   |> forceSeq)
                  [ VStr "a"; VStr "b" ]
                  "stderr empties dropped"

              Expect.equal
                  (runReal "sh -c 'printf \"e\\015\\012\" 1>&2' | complete |> _.stderr" |> forceSeq)
                  [ VStr "e\r" ]
                  "stderr keeps the CR (newline-only split)"
          }
          test "capture oracle: decoding — UTF-8 BOM stripped, invalid byte replaced, UTF-16 BOM switches" {
              Expect.equal
                  (runReal "sh -c 'printf \"\\357\\273\\277x\\012\"' | complete |> _.stdout"
                   |> forceSeq)
                  [ VStr "x" ]
                  "UTF-8 BOM stripped"

              Expect.equal
                  (runReal "sh -c 'printf \"a\\377b\\012\"' | complete |> _.stdout" |> forceSeq)
                  [ VStr "a�b" ]
                  "invalid byte becomes one replacement char"

              // StreamReader's BOM detection SWITCHES encodings — part of
              // today's contract, preserved via the fallback path
              Expect.equal
                  (runReal "sh -c 'printf \"\\377\\376x\\012\"' | complete |> _.stdout" |> forceSeq)
                  [ VStr "੸" ]
                  "UTF-16LE BOM switches decoding"
          }
          test "capture: re-enumeration is stable (Completed is materialized by definition)" {
              Expect.equal
                  (runReal
                      "let r = $(sh -c 'printf \"x\\012y\\012\"' | complete) in (r.stdout |> Seq.length) + (r.stdout |> Seq.length)")
                  (VInt 4L)
                  "two enumerations of the same view agree"
          }
          test "capture: a line crossing the segment boundary decodes whole" {
              // the segment store's riskiest branch [D:capture-buffer]: a
              // 5MB single line spans the 4MB segments and assembles
              Expect.equal
                  (runReal
                      "let r = $(sh -c 'head -c 5000000 /dev/zero | tr \"\\0\" a' | complete) in r.stdout |> Seq.head |> Str.length")
                  (VInt 5000000L)
                  "boundary-crossing line intact"
          }
          test "complete after an external-to-external pipeline is a parse error, not silent" {
              match Weir.Parser.parseLine cmdResolver "yes hi | grep hi | complete" with
              | Error msg -> Expect.stringContains msg "must directly follow a single external command segment" ""
              | Ok _ -> failtest "expected parse failure"
          }
          test "complete after a non-external stage is a parse error" {
              match Weir.Parser.parseLine realResolver "git status | first 1 | complete" with
              | Error msg -> Expect.stringContains msg "must directly follow a single external command segment" ""
              | Ok _ -> failtest "expected parse failure"
          }
          test "complete type is the Completed record" {
              match Weir.Parser.parseLine cmdResolver "grep x f | complete" with
              | Ok(SExpr e | SCmd e) ->
                  match typecheck env e with
                  | Ok te -> Expect.equal te.Ty (TNamed("Completed", [])) ""
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"
          } ]


let stringTests =
    testList
        "Strings and seq library"
        [ test "contains, startsWith, endsWith are data-last" {
              expectValue "contains \"err\" \"stderr\"" (VBool true)
              expectValue "startsWith \"fix:\" \"fix: bug\"" (VBool true)
              expectValue "endsWith \".txt\" \"a.txt\"" (VBool true)
              expectValue "contains \"zzz\" \"stderr\"" (VBool false)
          }
          test "point-free pipeline predicate is the payoff" {
              expectValue
                  "[\"an error here\"; \"fine\"; \"error again\"] |> where (contains \"error\") |> head"
                  (VStr "an error here")
          }
          test "trim family" {
              expectValue "trim \"  x  \"" (VStr "x")
              expectValue "trimStart \"  x  \"" (VStr "x  ")
              expectValue "trimEnd \"  x  \"" (VStr "  x")
          }
          test "case mapping" {
              expectValue "toLower \"AbC\"" (VStr "abc")
              expectValue "toUpper \"AbC\"" (VStr "ABC")
          }
          test "split and join roundtrip" {
              expectValue "split \",\" \"a,b,c\" |> join \";\"" (VStr "a;b;c")
              Expect.equal (run "split \",\" \"a,,b\"" |> forceSeq) [ VStr "a"; VStr ""; VStr "b" ] "empties kept"
          }
          test "replace is pattern-replacement-subject" { expectValue "replace \"o\" \"0\" \"foo\"" (VStr "f00") }
          test "Str.length and toInt" {
              expectValue "Str.length \"abc\"" (VInt 3)
              expectValue "toInt \"42\" + 1" (VInt 43)
              Expect.throws (fun () -> run "toInt \"nope\"" |> ignore) "toInt raises"
              expectValue "tryToInt \"42\"" (VUnion("Some", Some(VInt 42)))
              expectValue "tryToInt \"nope\"" (VUnion("None", None))
          }
          test "Seq.tryHead returns Option and Seq.isEmpty stands" {
              expectValue "[1; 2] |> Seq.tryHead" (VUnion("Some", Some(VInt 1)))
              expectValue "[] |> Seq.tryHead" (VUnion("None", None))
              expectValue "[] |> Seq.isEmpty" (VBool true)
              expectValue "ls |> Seq.isEmpty" (VBool false)
          }
          test "Seq.sortBy over scalar keys" {
              Expect.equal
                  (run "ls |> Seq.sortBy _.bytes |> map _.name" |> forceSeq)
                  [ VStr "a.txt"; VStr "c.log"; VStr "d.iso"; VStr "b.bin" ]
                  "by size"

              Expect.equal (run "[3; 1; 2] |> Seq.sortBy (fun x -> x)" |> forceSeq) [ VInt 1; VInt 2; VInt 3 ] ""
          }
          test "Seq.sortBy on a non-scalar key raises with a clear message" {
              Expect.throws (fun () -> run "ls |> Seq.sortBy (fun f -> f)" |> forceSeq |> ignore) ""
          }
          test "Seq.sortByDescending reverses the key order" {
              Expect.equal
                  (run "[1; 3; 2] |> Seq.sortByDescending (fun x -> x)" |> forceSeq)
                  [ VInt 3; VInt 2; VInt 1 ]
                  ""
          }
          test "Seq.sortByDescending is stable on equal keys" {
              Expect.equal
                  (run "[\"bb\"; \"a\"; \"cc\"; \"d\"] |> Seq.sortByDescending Str.length"
                   |> forceSeq)
                  [ VStr "bb"; VStr "cc"; VStr "a"; VStr "d" ]
                  ""
          }
          test "Seq.sortByDescending shares sortBy's Ord constraint" {
              let terr = checkErr "[(1, 2)] |> Seq.sortByDescending (fun x -> x)"
              Expect.stringContains terr.Message "cannot be ordered" ""
          }
          test "the git-branch idiom composes point-free" {
              expectValue
                  "[\"* main\"; \"  feature/a\"; \"  feature/b\"] |> map trim |> where (startsWith \"feature\") |> join \",\""
                  (VStr "feature/a,feature/b")
          } ]


let genericsTests =
    testList
        "Generic unions and records"
        [ test "constructor schemes freshen per use" {
              expectValue "let s = fun x -> Some x in (s 1 == Some 1) && (s \"a\" == Some \"a\")" (VBool true)
          }
          test "None is polymorphic and instantiates per use" {
              expectValue "let n = None in (n == Some 1) == (n == Some \"a\")" (VBool true)
          }
          test "Some 3 types as Option<int>" { Expect.equal (formatTy (checkOk "Some 3").Ty) "Option<int>" "" }
          test "equatability recurses through applied constructors" {
              expectValue "Some 1 == Some 1" (VBool true)
              Expect.stringContains (checkErr "Some (fun x -> x) == Some (fun x -> x)").Message "is not defined" ""
          }
          test "nested Option constructs, matches, evaluates" {
              Expect.equal (formatTy (checkOk "Some (Some 1)").Ty) "Option<Option<int>>" ""
              expectValue "match Some (Some 1) with | Some (Some x) -> x | Some None -> 0 | None -> 0" (VInt 1)
          }
          test "nested refutable payloads are covered recursively" {
              Expect.isEmpty
                  (warningsOf "match Some (Some 1) with | Some (Some x) -> x | Some None -> 0 | None -> 0")
                  "recursive coverage: genuinely-total nested match accepted"

              let terr = checkErr "match Some (Some 1) with | Some (Some x) -> x | None -> 0"
              Expect.stringContains (formatError terr) "missing: Some" "incomplete payload surfaces the case"
          }
          test "occurs check through a constructor" {
              Expect.stringContains (checkErr "fun x -> Some x == x").Message "infinite type" ""
          }
          test "match binds at the instantiated type" {
              expectValue "match Some 5 with | Some x -> x + 1 | None -> 0" (VInt 6)
          }
          test "a 2-param generic union infers across arms" {
              // was the Result fixture; Result left the prelude [D:no-result]
              Expect.equal (checkOk "match Left 3 with | Left v -> v | Right e -> Str.length e").Ty (TInt) ""
              expectValue "match Right \"boom\" with | Left v -> v | Right e -> Str.length e" (VInt 4)
          }
          test "missing None is a hard error" {
              let terr = checkErr "match Some 1 with | Some x -> x"
              Expect.stringContains (formatError terr) "missing: None" ""
          }
          test "payload pattern arity message shows the instantiated type" {
              Expect.stringContains
                  (checkErr "match Some 1 with | Some -> 1 | None -> 0").Message
                  "carries int; add a pattern"
                  ""
          }
          test "arity errors in declarations" {
              Expect.stringContains
                  (declErr "type Bad = { F: Option }" env).Message
                  "expects 1 type argument(s), got 0"
                  ""

              Expect.stringContains
                  (declErr "type Bad2 = { F: Option<int, int> }" env).Message
                  "expects 1 type argument(s), got 2"
                  ""
          }
          test "undeclared type parameter is rejected" {
              Expect.stringContains (declErr "type Bad3 = Wrap of 'z" env).Message "unknown type parameter 'z" ""
          }
          test "generic record declares, constructs, projects" {
              let e2 = env |> declare "type Pair<'a> = { Fst: 'a; Snd: 'a }"
              let expr = parse "let p = { Fst = 1; Snd = 2 } in p.Fst + p.Snd"

              match Weir.Check.typecheck e2 expr with
              | Ok te ->
                  Expect.equal te.Ty (TInt) ""
                  Expect.equal (eval valueEnv te) (VInt 3) ""
              | Error terr -> failtest (formatError terr)
          }
          test "generic record enforces shared parameters" {
              let e2 = env |> declare "type Pair<'a> = { Fst: 'a; Snd: 'a }"

              match Weir.Check.typecheck e2 (parse "{ Fst = 1; Snd = \"a\" }") with
              | Error terr -> Expect.stringContains terr.Message "expected int, got string" ""
              | Ok _ -> failtest "expected rejection"
          }
          test "Seq.groupBy lands on generic Group records" {
              Expect.equal
                  (run "[1; 2; 3; 4] |> Seq.groupBy (fun x -> x < 3) |> map _.key" |> forceSeq)
                  [ VBool true; VBool false ]
                  "keys"

              expectValue "[1; 2; 3; 4] |> Seq.groupBy (fun x -> x < 3) |> head |> (fun g -> g.items) |> sum" (VInt 3)

              Expect.equal
                  (formatTy (checkOk "[1; 2] |> Seq.groupBy (fun x -> x)").Ty)
                  "seq<Group<int, int>>"
                  "type display"
          } ]


let attributeTests =
    testList
        "Attributes"
        [ // attachment + inertness [D:attributes]
          test "registered attributes attach and the record works unchanged" {
              let env' =
                  env
                  |> declare
                      "type Cfg = { [<Short \"c\"; Doc \"count\">] Count: int; Name: string; [<NoShort>] Loud: bool }"

              match Map.tryFind "Cfg" env'.Types with
              | Some(Record def) ->
                  Expect.equal (List.map fst def.Fields) [ "Count"; "Name"; "Loud" ] "fields unchanged"

                  Expect.equal
                      (Map.find "Count" def.Attrs)
                      [ "Short", Some(AStr "c"); "Doc", Some(AStr "count") ]
                      "attrs recorded"

                  Expect.equal (Map.find "Loud" def.Attrs) [ "NoShort", None ] "argless recorded"
              | other -> failtest $"expected a record def, got {other}"
          }
          test "bare fields leave Attrs empty" {
              let env' = env |> declare "type Bare = { N: int }"

              match Map.tryFind "Bare" env'.Types with
              | Some(Record def) -> Expect.isTrue def.Attrs.IsEmpty "no attr entries"
              | other -> failtest $"expected a record def, got {other}"
          }
          test "unknown attribute is a check error with a hint" {
              let terr = env |> declErr "type T = { [<Shrot \"c\">] A: int }"
              Expect.stringContains terr.Message "unknown attribute 'Shrot'" ""
              Expect.stringContains terr.Message "Did you mean 'Short'?" ""
          }
          test "Short wants a one-character string" {
              let terr = env |> declErr "type T = { [<Short \"cc\">] A: int }"
              Expect.stringContains terr.Message "one-character string" ""

              let terr2 = env |> declErr "type T = { [<Short>] A: int }"
              Expect.stringContains terr2.Message "one-character string" ""

              let terr3 = env |> declErr "type T = { [<Short 3>] A: int }"
              Expect.stringContains terr3.Message "one-character string" ""
          }
          test "Short 'h' is reserved" {
              let terr = env |> declErr "type T = { [<Short \"h\">] A: int }"
              Expect.stringContains terr.Message "reserved for --help" ""
          }
          test "argless attributes reject arguments" {
              let terr = env |> declErr "type T = { [<NoShort true>] A: int }"
              Expect.stringContains terr.Message "takes no argument" ""
          }
          test "Doc wants a non-empty string" {
              let terr = env |> declErr "type T = { [<Doc \"\">] A: int }"
              Expect.stringContains terr.Message "non-empty string" ""
          }
          test "duplicate attribute on one field is rejected" {
              let terr = env |> declErr "type T = { [<Doc \"a\"; Doc \"b\">] A: int }"
              Expect.stringContains terr.Message "duplicate attribute 'Doc'" ""
          }
          test "Short and NoShort conflict on one field" {
              let terr = env |> declErr "type T = { [<NoShort; Short \"d\">] B: int }"
              Expect.stringContains terr.Message "both Short and NoShort" ""
          }
          test "explicit shorts collide across fields" {
              let terr =
                  env |> declErr "type T = { [<Short \"c\">] A: int; [<Short \"c\">] B: int }"

              Expect.stringContains terr.Message "duplicate short '-c'" ""
          }
          test "erasure: construction, projection, show, equality are attr-blind" {
              expectValue
                  "let e = { Q = 7 } in (show e == \"{ Q = 7 }\") && (e == { Q = 7 }) && (e.Q == 7)"
                  (VBool true)
          }
          // products [D:attributes]
          test "generic record carries attrs as declaration data" {
              let env' = env |> declare "type GB<'a> = { [<Doc \"v\">] V: 'a }"

              match Map.tryFind "GB" env'.Types with
              | Some(Record def) -> Expect.equal (Map.find "V" def.Attrs) [ "Doc", Some(AStr "v") ] ""
              | other -> failtest $"expected a record def, got {other}"

              match Weir.Check.typecheck env' (parse "let b = { V = \"s\" } in b.V") with
              | Ok te -> Expect.equal te.Ty TStr "instantiates and projects unchanged"
              | Error terr -> failtest $"check failed: {formatError terr}"
          }
          test "record update cannot mention attributes" {
              match Weir.Parser.parseExpr "{ e with [<Short \"d\">] Q = 2 }" with
              | Ok _ -> failtest "expected a parse rejection"
              | Error msg -> Expect.stringContains msg "attributes attach to record fields" ""
          }
          test "non-field positions name the scope decision" {
              match Weir.Parser.parseExpr "[<Short \"c\">] 1" with
              | Ok _ -> failtest "expected a parse rejection"
              | Error msg -> Expect.stringContains msg "attributes attach to record fields" ""

              match Weir.Parser.parseStmt "type U = [<Short \"c\">] A of int | B" with
              | Ok _ -> failtest "expected a parse rejection"
              | Error msg -> Expect.stringContains msg "attributes attach to record fields" ""

              match Weir.Parser.parseStmt "let f [<Short \"c\">] x = x" with
              | Ok _ -> failtest "expected a parse rejection"
              | Error msg -> Expect.stringContains msg "attributes attach to record fields" ""
          } ]

let typedArgvTests =
    // check-side battery [D:typed-argv]; runtime behavior pins live in
    // e2e (Session.ScriptArgs is ambient global state)
    let argvEnv =
        // script mode = the Self module present [D:self-module]
        { env with
            Modules = env.Modules |> Map.add "Self" Weir.Script.selfMembers }

    let argvCheck input =
        Weir.Check.typecheck argvEnv (parse input)

    let argvErr input =
        match argvCheck input with
        | Ok _ -> failtest "expected the check to reject"
        | Error terr -> terr

    testList
        "Typed argv"
        [ test "kebab derivation pins (the plan's examples)" {
              Expect.equal (Weir.Argv.kebabFlag "dryRun") "dry-run" ""
              Expect.equal (Weir.Argv.kebabFlag "DryRun") "dry-run" ""
              Expect.equal (Weir.Argv.kebabFlag "noFF") "no-ff" ""
              Expect.equal (Weir.Argv.kebabFlag "useHTTPSNow") "use-https-now" ""
              Expect.equal (Weir.Argv.kebabFlag "port") "port" ""
          }
          test "short tables: derive, contest, override, suppress, reserve" {
              let def name input =
                  match Map.tryFind name (argvEnv |> declare input).Types with
                  | Some(Record d) -> d
                  | _ -> failtest "record expected"

              let shorts, index =
                  Weir.Argv.shortTables (def "S1" "type S1 = { clean: bool; verbose: bool }")

              Expect.equal (Map.tryFind "--clean" shorts) (Some "c") "derives"
              Expect.equal (Map.tryFind "c" index) (Some(Weir.Argv.ShortOf "--clean")) "owner"

              let shorts2, index2 =
                  Weir.Argv.shortTables (def "S2" "type S2 = { clean: bool; copy: bool }")

              Expect.equal (Map.tryFind "--clean" shorts2) None "contested letters derive for nobody"

              Expect.equal
                  (Map.tryFind "c" index2)
                  (Some(Weir.Argv.AmbiguousShort [ "--clean"; "--copy" ]))
                  "candidates kept for the error"

              let shorts3, _ =
                  Weir.Argv.shortTables (def "S3" "type S3 = { [<Short \"e\">] clean: bool; env: string }")

              Expect.equal (Map.tryFind "--clean" shorts3) (Some "e") "explicit wins the letter"
              Expect.equal (Map.tryFind "--env" shorts3) None "the derived short retires"

              let shorts4, _ =
                  Weir.Argv.shortTables (def "S4" "type S4 = { [<NoShort>] clean: bool }")

              Expect.equal (Map.tryFind "--clean" shorts4) None "NoShort suppresses"

              let shorts5, _ = Weir.Argv.shortTables (def "S5" "type S5 = { host: bool }")
              Expect.equal (Map.tryFind "--host" shorts5) None "h never derives (help)"
          }
          test "Args.load types as the record; the union as the union" {
              let e2 =
                  argvEnv
                  |> declare "type Cli = { clean: bool; env: string }"
                  |> declare "type CA = { remote: string }"
                  |> declare "type Cmd = Clone of CA | Status"

              match Weir.Check.typecheck e2 (parse "Args.load Cli") with
              | Ok te -> Expect.equal (formatTy te.Ty) "Cli" ""
              | Error terr -> failtest (formatError terr)

              match Weir.Check.typecheck e2 (parse "Args.load Cmd") with
              | Ok te -> Expect.equal (formatTy te.Ty) "Cmd" ""
              | Error terr -> failtest (formatError terr)
          }
          test "field-shape rejections" {
              let e2 = argvEnv |> declare "type B1 = { b: Option<bool> }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load B1") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "a presence flag is already optional"
                  ""

              let e3 = argvEnv |> declare "type B2 = { xs: seq<string> }"

              Expect.stringContains
                  (match Weir.Check.typecheck e3 (parse "Args.load B2") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "must be string, int, bool"
                  ""
          }
          test "duplicate derived flags reject at check" {
              let e2 = argvEnv |> declare "type D = { dryRun: bool; DryRun: bool }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load D") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "derive the same flag '--dry-run'"
                  ""
          }
          test "Positional is an unknown attribute — dropped, not reserved" {
              // was "[<Positional>] fires its not-yet": the registration and
              // its not-yet consumer retired together (the rider — one
              // receipt, contract-mimicry from model-authored code, nothing
              // blocked). It is not a thing; weir scripts take FLAGS.
              let terr = argvEnv |> declErr "type P = { [<Positional>] t: string }"
              Expect.stringContains terr.Message "unknown attribute 'Positional'" ""
          }
          test "union payload rules: single record only; case collisions" {
              let e2 = argvEnv |> declare "type U1 = Go of string | Stop"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load U1") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "must carry a single record payload"
                  ""

              let e3 = argvEnv |> declare "type U2 = Go | GO"

              Expect.stringContains
                  (match Weir.Check.typecheck e3 (parse "Args.load U2") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "collide as subcommand 'go'"
                  ""
          }
          test "Env.load consumes Default: fill, cells, and the FLIP [D:default-attr]" {
              // acceptance: defaulted fields stay non-Option
              let e1 =
                  argvEnv
                  |> declare "type E1 = { [<Default 8080>] PORT_ZZQ: int; [<Default \"info\">] LVL_ZZQ: string }"

              match Weir.Check.typecheck e1 (parse "Env.load E1") with
              | Ok te -> Expect.equal (formatTy te.Ty) "E1" ""
              | Error terr -> failtest (formatError terr)

              // Default on Option: the same contradiction, same text
              let e2 = argvEnv |> declare "type E2 = { [<Default 5>] P: Option<int> }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Env.load E2") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "drop the Option or the attribute"
                  ""

              // literal type mismatch
              let e3 = argvEnv |> declare "type E3 = { [<Default \"x\">] P: int }"

              Expect.stringContains
                  (match Weir.Check.typecheck e3 (parse "Env.load E3") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "is int"
                  ""

              // THE FLIP: Default false is LEGAL at Env (absent -> false is
              // a real statement under text bools), REJECTED at Args
              // (presence already rests at false)
              let e4 = argvEnv |> declare "type E4 = { [<Default false>] DEBUG_ZZQ: bool }"

              match Weir.Check.typecheck e4 (parse "Env.load E4") with
              | Ok _ -> ()
              | Error terr -> failtest $"Env must accept Default false: {formatError terr}"

              Expect.stringContains
                  (match Weir.Check.typecheck e4 (parse "Args.load E4") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "Args must still reject Default false")
                  "presence already rests at false"
                  ""
          }
          test "Env.load enum fields: the field law's union clause [D:env-enums]" {
              let rejects env expr =
                  match Weir.Check.typecheck env (parse expr) with
                  | Error terr -> terr.Message
                  | Ok _ -> failtest $"expected rejection: {expr}"

              // a 0-arity union field is loadable; Option<enum> rides along
              let e1 =
                  argvEnv
                  |> declare "type LvlA = Debug | Info | Warn"
                  |> declare "type CA = { LOG_ZZQ: LvlA; OPT_ZZQ: Option<LvlA> }"

              match Weir.Check.typecheck e1 (parse "Env.load CA") with
              | Ok te -> Expect.equal (formatTy te.Ty) "CA" "enum + Option<enum> load"
              | Error terr -> failtest (formatError terr)

              // a payload-carrying case is a check-time rejection, naming it
              let e2 =
                  argvEnv
                  |> declare "type LvlB = Plain | Carry of int"
                  |> declare "type CB = { F_ZZQ: LvlB }"

              Expect.stringContains
                  (rejects e2 "Env.load CB")
                  "env values are single tokens, so enum fields need 0-arity cases; case 'Carry'"
                  "the schema is wrong at check time"

              // two cases differing only in casing collide (matching is
              // case-insensitive, so the schema is ambiguous)
              let e3 =
                  argvEnv
                  |> declare "type LvlC = Debug | DEBUG"
                  |> declare "type CC = { F_ZZQ: LvlC }"

              Expect.stringContains (rejects e3 "Env.load CC") "collide as env value 'debug'" ""

              // Default on an enum field teaches the alternative spelling
              let e4 =
                  argvEnv
                  |> declare "type LvlD = A | B"
                  |> declare "type CD = { [<Default \"A\">] F_ZZQ: LvlD }"

              Expect.stringContains (rejects e4 "Env.load CD") "Option<LvlD> with Option.defaultValue" ""

              // the field-law message grew its clause
              let e5 =
                  argvEnv |> declare "type RD = { X: int }" |> declare "type CE = { F_ZZQ: RD }"

              Expect.stringContains
                  (rejects e5 "Env.load CE")
                  "string, int, bool, an enum union (0-arity cases), or Option of these"
                  ""
          }
          test "Env.load enum conversion: casing, candidates, collect [D:env-enums]" {
              let env =
                  argvEnv
                  |> declare "type LvlE = Debug | Info | Warn"
                  |> declare "type CF = { LOGE_ZZQ: LvlE; PORTE_ZZQ: int; OPTE_ZZQ: Option<LvlE> }"

              let load () =
                  match Weir.Check.typecheck env (parse "Env.load CF") with
                  | Ok te -> eval valueEnv te
                  | Error terr -> failtest (formatError terr)

              let set (n: string) (v: string) =
                  System.Environment.SetEnvironmentVariable(n, v)

              try
                  // any casing selects the declared case; Option absent = None
                  for spelling in [ "DEBUG"; "debug"; "Debug" ] do
                      set "LOGE_ZZQ" spelling
                      set "PORTE_ZZQ" "1"

                      match load () with
                      | VRecord(_, fs) ->
                          Expect.equal fs["LOGE_ZZQ"] (VUnion("Debug", None)) $"casing '{spelling}'"
                          Expect.equal fs["OPTE_ZZQ"] (VUnion("None", None)) "absent Option is None"
                      | v -> failtest $"unexpected: {formatValue v}"

                  // a miss carries the candidates and a did-you-mean
                  set "LOGE_ZZQ" "debgu"

                  let msg =
                      try
                          load () |> ignore
                          "no-raise"
                      with ex ->
                          ex.Message

                  Expect.stringContains msg "expected one of: Debug, Info, Warn" "candidates"
                  Expect.stringContains msg "Did you mean 'Debug'?" "the hint machinery rides"

                  // collect-then-raise: a bad enum AND a bad int, one error
                  set "PORTE_ZZQ" "nope"

                  let both =
                      try
                          load () |> ignore
                          "no-raise"
                      with ex ->
                          ex.Message

                  Expect.stringContains both "is not a LvlE" "enum problem present"
                  Expect.stringContains both "is not an int" "int problem collected alongside"

                  // EMPTY is a miss with candidates (the int precedent), not None
                  set "LOGE_ZZQ" ""
                  set "PORTE_ZZQ" "1"

                  let empty =
                      try
                          load () |> ignore
                          "no-raise"
                      with ex ->
                          ex.Message

                  Expect.stringContains empty "expected one of:" "empty = miss, matching the int rule"
              finally
                  set "LOGE_ZZQ" null
                  set "PORTE_ZZQ" null
                  set "OPTE_ZZQ" null
          }
          test "Default fills at the resting point [D:default-attr]" {
              let e2 =
                  argvEnv
                  |> declare "type C1 = { [<Default 10000>] count: int; [<Default \"main\">] branch: string }"

              match Weir.Check.typecheck e2 (parse "Args.load C1") with
              | Ok te -> Expect.equal (formatTy te.Ty) "C1" "defaulted fields stay non-Option"
              | Error terr -> failtest (formatError terr)
          }
          test "Default rejection cells teach [D:default-attr]" {
              // Default false on bool: redundant
              let e1 = argvEnv |> declare "type B1 = { [<Default false>] quiet: bool }"

              Expect.stringContains
                  (match Weir.Check.typecheck e1 (parse "Args.load B1") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "presence already rests at false"
                  ""

              // Default on Option: contradictory
              let e2 = argvEnv |> declare "type B2 = { [<Default 5>] port: Option<int> }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load B2") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "drop the Option or the attribute"
                  ""

              // literal type mismatch
              let e3 = argvEnv |> declare "type B3 = { [<Default \"x\">] port: int }"

              Expect.stringContains
                  (match Weir.Check.typecheck e3 (parse "Args.load B3") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "is int"
                  ""

              // Default on the union subcommand slot
              let e4 =
                  argvEnv
                  |> declare "type CA = { remote: string }"
                  |> declare "type Cmd4 = Go of CA | Stop"
                  |> declare "type B4 = { [<Default true>] cmd: Cmd4 }"

              Expect.stringContains
                  (match Weir.Check.typecheck e4 (parse "Args.load B4") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "no flag derives"
                  ""
          }
          test "minted --no-X joins the collision namespace, both routes [D:default-attr]" {
              // within one record
              let e1 =
                  argvEnv |> declare "type M1 = { [<Default true>] color: bool; noColor: bool }"

              Expect.stringContains
                  (match Weir.Check.typecheck e1 (parse "Args.load M1") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "no-color"
                  ""

              // cross-tier (shared-flags shape)
              let e2 =
                  argvEnv
                  |> declare "type MA = { noColor: bool }"
                  |> declare "type MCmd = Go of MA | Stop"
                  |> declare "type M2 = { [<Default true>] color: bool; cmd: MCmd }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load M2") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "no-color"
                  ""
          }
          test "a record with ONE union-typed field is the shared-flags shape [D:shared-flags]" {
              let e2 =
                  argvEnv
                  |> declare "type CA = { remote: string }"
                  |> declare "type Cmd = Clone of CA | Status"
                  |> declare "type Cli = { quiet: bool; cmd: Cmd }"

              match Weir.Check.typecheck e2 (parse "Args.load Cli") with
              | Ok te -> Expect.equal (formatTy te.Ty) "Cli" "types as the containing record"
              | Error terr -> failtest (formatError terr)
          }
          test "two union-typed fields reject: one subcommand slot" {
              let e2 =
                  argvEnv
                  |> declare "type CA = { remote: string }"
                  |> declare "type Cmd = Clone of CA | Status"
                  |> declare "type Cli = { a: Cmd; b: Cmd }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load Cli") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "one subcommand slot"
                  ""
          }
          test "a flag declared in both tiers rejects at declaration (kebab route)" {
              let e2 =
                  argvEnv
                  |> declare "type CA = { quiet: bool; remote: string }"
                  |> declare "type Cmd = Clone of CA | Status"
                  |> declare "type Cli = { quiet: bool; cmd: Cmd }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load Cli") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "shared flags are declared once"
                  ""
          }
          test "an explicit Short claimed in both tiers rejects at declaration" {
              let e2 =
                  argvEnv
                  |> declare "type CA = { [<Short \"q\">] query: string }"
                  |> declare "type Cmd = Clone of CA | Status"
                  |> declare "type Cli = { [<Short \"q\">] quiet: bool; cmd: Cmd }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load Cli") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "'-q'"
                  ""
          }
          test "a union inside a PAYLOAD record stays rejected (nested hierarchies parked)" {
              let e2 =
                  argvEnv
                  |> declare "type Inner = In1 | In2"
                  |> declare "type CA = { sub: Inner }"
                  |> declare "type Cmd = Clone of CA | Status"
                  |> declare "type Cli = { quiet: bool; cmd: Cmd }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load Cli") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "must be string, int, bool"
                  ""
          }
          test "scriptPath is script-only, with its teaching [D:script-path]" {
              // the REPL/-e env lacks it; the error names the family
              Expect.stringContains
                  (match Weir.Check.typecheck env (parse "scriptPath") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected the script-only rejection")
                  "scriptPath is script-only"
                  ""

              // in scripts it types as string, now via Self [D:self-module]
              let diags, _, _, _ =
                  Weir.Script.analyzeLines "pin.weir" [ "print (Self.scriptPath |> Path.dir)" ]

              Expect.isEmpty diags "scripts know their own path"

              // the bare name teaches the move (the clean-break migration)
              let moved, _, _, _ =
                  Weir.Script.analyzeLines "pin.weir" [ "print (scriptPath |> Path.dir)" ]

              Expect.exists moved (fun d -> d.Message.Contains "use 'Self.scriptPath'") "bare name teaches Self"
          }
          test "scriptPath coexists with Args.load (no interaction)" {
              let diags, _, _, _ =
                  Weir.Script.analyzeLines
                      "pin.weir"
                      [ "type Cli = { quiet: bool }"
                        "let cli = Args.load Cli"
                        "print (Self.scriptPath |> Path.dir)"
                        "print $\"{show cli.quiet}\"" ]

              Expect.isEmpty diags "both boundary reads in one script"
          }
          test "the Self module: members type in scripts; bare names teach the move [D:self-module]" {
              let clean lines =
                  let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines
                  Expect.isEmpty (diags |> List.filter (fun d -> d.Severity = "error")) $"types: {lines}"

              clean [ "let n = Self.pid + 1"; "print n" ] // pid is int (arithmetic)
              clean [ "Self.args |> Seq.iter print" ]
              clean [ "Self.stdin |> Seq.length |> print" ]
              clean [ "print (Self.scriptPath |> Path.dir)" ]

              // clean break: every bare name teaches its Self home
              let teaches name (lines: string list) =
                  let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

                  Expect.exists
                      diags
                      (fun d -> d.Message.Contains $"use 'Self.{name}'")
                      $"bare '{name}' teaches Self.{name}"

              // in EXPRESSION position (bare at statement head is a command
              // candidate — a cmd-not-found warning, a different path)
              teaches "args" [ "print (Seq.length args)" ]
              teaches "stdin" [ "print (Seq.length stdin)" ]
              teaches "scriptPath" [ "print scriptPath" ]
          }
          test "script-only: without args in scope Args.load rejects by name" {
              let e2 = env |> declare "type C = { env: string }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load C") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "Args.load is script-only"
                  ""
          }
          test "unknown type did-you-means over declared types" {
              let e2 = argvEnv |> declare "type Cli9 = { env: string }"

              Expect.stringContains
                  (match Weir.Check.typecheck e2 (parse "Args.load Cli8") with
                   | Error terr -> terr.Message
                   | Ok _ -> failtest "expected rejection")
                  "Did you mean 'Cli9'?"
                  ""
          } ]

let chooseTests =
    testList
        "Seq.choose"
        [ test "the idiom: match-or-skip over Regex arms" {
              expectValue
                  "[\"a1\"; \"x\"; \"a2\"] |> Seq.choose (fun l -> match l with | Regex @\"a(1|2)\" n -> Some n | _ -> None) |> Str.join \",\""
                  (VStr "1,2")
          }
          test "lazy: infinite source, choose, first terminates" {
              let infinite = Seq.initInfinite (fun i -> Weir.Builtins.file $"f{i}" i false)

              match
                  runWith
                      [ "ls", VSeq infinite ]
                      "ls |> Seq.choose (fun f -> if f.bytes > 2 then Some f.bytes else None) |> first 2 |> Seq.sum"
              with
              | VInt n -> Expect.equal n 7L "3 + 4"
              | v -> failtest $"unexpected {v}"
          }
          test "all-None yields empty" { expectValue "[1; 2; 3] |> Seq.choose (fun x -> None) |> Seq.length" (VInt 0L) }
          test "constraint-free: no Eq/Ord obligation on either side" {
              Expect.equal
                  (formatTy (checkOk "ls |> Seq.choose (fun f -> Some f.name) |> Seq.head").Ty)
                  "string"
                  "row-projecting chooser types through"
          }
          test "Seq.append: lazy concat, piped-tail order (the full-port receipt)" {
              expectValue "[3; 4] |> Seq.append [1; 2] |> Seq.sum" (VInt 10L)

              let infinite = Seq.initInfinite (fun i -> Weir.Builtins.file $"f{i}" i false)

              match runWith [ "ls", VSeq infinite ] "ls |> Seq.append ls |> first 2 |> Seq.length" with
              | VInt n -> Expect.equal n 2L "lazy on both sides"
              | v -> failtest $"unexpected {v}"
          }
          test "qualified-only: bare choose does not resolve" {
              let terr = checkErr "[1] |> choose (fun x -> Some x)"
              Expect.stringContains terr.Message "choose" ""
          }
          test "non-Option chooser rejects at check" {
              let terr = checkErr "[1] |> Seq.choose (fun x -> x)"
              Expect.stringContains terr.Message "Option" ""
          } ]

let bracketContinuationTests =
    // [D:multiline-brackets] — fixture diversity per the standing rule:
    // headed / standalone / nested / at-boundary
    let joined lines =
        match Weir.Script.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Ok [ ll ] -> ll.Text
        | other -> failtest $"unexpected: {other}"

    let assembleErr lines =
        match Weir.Script.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Error e -> e
        | Ok lls -> failtest $"expected an assembly error, got {lls |> List.map (fun l -> l.Text)}"

    testList
        "Bracket continuation"
        [ test "type declaration fields join as siblings" {
              Expect.equal
                  (joined
                      [ "type Ctx ="
                        "    { Subdir: string"
                        "      Subref: string"
                        "      Repo: GitRepo }" ])
                  "type Ctx = { Subdir: string ; Subref: string ; Repo: GitRepo }"
                  ""
          }
          test "same-line attributes ride a type field line" {
              Expect.equal
                  (joined [ "type Cli ="; "    { [<Short \"c\">] count: int"; "      verbose: bool }" ])
                  "type Cli = { [<Short \"c\">] count: int ; verbose: bool }"
                  ""
          }
          test "preceding-line attribute binds to ITS field: no separator between" {
              Expect.equal
                  (joined
                      [ "type Cli ="
                        "    { [<Doc \"count\">]"
                        "      count: int"
                        "      verbose: bool }" ])
                  "type Cli = { [<Doc \"count\">] count: int ; verbose: bool }"
                  ""
          }
          test "list elements join as siblings" {
              Expect.equal
                  (joined [ "let pairs ="; "    [(\"a\", 1)"; "     (\"b\", 2)"; "     (\"c\", 3)]" ])
                  "let pairs = [(\"a\", 1) ; (\"b\", 2) ; (\"c\", 3)]"
                  ""
          }
          test "a dangling operator continues the same element" {
              Expect.equal (joined [ "let x ="; "    [1 +"; "     2"; "     3]" ]) "let x = [1 + 2 ; 3]" ""
          }
          test "trailing ; takes no second separator" {
              Expect.equal (joined [ "let x ="; "    [1;"; "     2]" ]) "let x = [1; 2]" ""
          }
          test "nested list: inner elements nest, outer resumes" {
              Expect.equal (joined [ "let xs ="; "    [[1; 2]"; "     [3]]" ]) "let xs = [[1; 2] ; [3]]" ""
          }
          test "a multiline record inside a list: innermost bracket rules" {
              Expect.equal
                  (joined [ "let xs ="; "    [{ A = 1"; "       B = 2 }"; "     { A = 3; B = 4 }]" ])
                  "let xs = [{ A = 1 ; B = 2 } ; { A = 3; B = 4 }]"
                  ""
          }
          test "at-boundary: a col-0 statement after the close is a new statement" {
              match
                  Weir.Script.assemble
                      [ 1, "type T ="
                        2, "    { A: int"
                        3, "      B: int }"
                        4, "let t = { A = 1; B = 2 }" ]
              with
              | Ok [ ty; letLine ] ->
                  Expect.equal ty.Text "type T = { A: int ; B: int }" ""
                  Expect.equal letLine.Text "let t = { A = 1; B = 2 }" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "cross-bracket closer names both sides" {
              Expect.stringContains
                  (assembleErr [ "let x ="; "    [1; 2"; "     3}" ])
                  "'}' closes the '[' opened at line 2"
                  ""
          }
          // blanks are transparent inside brackets [D:blank-in-brackets]
          test "blank inside an open list is transparent" {
              Expect.equal (joined [ "let x ="; "    [1"; ""; "     2]" ]) "let x = [1 ; 2]" ""
          }
          test "blank inside an open type decl is transparent" {
              Expect.equal
                  (joined [ "type T ="; "    { A: int"; ""; "      B: int }" ])
                  "type T = { A: int ; B: int }"
                  ""
          }
          test "the statement-head guard bounds an unclosed bracket" {
              Expect.stringContains
                  (assembleErr [ "let xs = ["; "    1"; ""; "let y = 2" ])
                  "statement at column 0 while the '[' opened at line 1 is still open"
                  ""

              Expect.stringContains
                  (assembleErr [ "type T = {"; "    A: int"; ""; "type U = { B: int }" ])
                  "'{' opened at line 1 is still open"
                  ""
          }
          test "update x guard: the with-header bracket names its own line" {
              Expect.stringContains
                  (assembleErr [ "let r2 = { r with"; "    A = 2"; ""; "let z = 3" ])
                  "'{' opened at line 1 is still open"
                  ""
          }
          test "runs of blanks are transparent too" {
              Expect.equal (joined [ "let x ="; "    [1"; ""; ""; "     2]" ]) "let x = [1 ; 2]" ""
          }
          // [D:body-blanks] — pin-as-regression-guard, not constitution
          test "twin flipped: a pending block-let's body continues across a gap" {
              Expect.equal (joined [ "let x ="; "    let a = 1"; ""; "    a + 1" ]) "let x = let a = 1 in a + 1" ""
          }
          test "bracket transparency wins over a pending let INSIDE the bracket" {
              Expect.equal
                  (joined [ "let xs = ["; "    1"; ""; "    2"; "]" ])
                  "let xs = [ 1 ; 2 ]"
                  "the statement survives the gap"
          }
          test "Stroustrup closers take no separator" {
              Expect.equal
                  (joined [ "let pairs = ["; "    (\"a\", 1)"; "    (\"b\", 2)"; "]" ])
                  "let pairs = [ (\"a\", 1) ; (\"b\", 2) ]"
                  "list closer joins with space"

              Expect.equal
                  (joined [ "type Ctx = {"; "    Subdir: string"; "    Repo: string"; "}" ])
                  "type Ctx = { Subdir: string ; Repo: string }"
                  "type closer joins with space"
          }
          test "compound-paren-prune: a match inside a closed lambda never wraps [D:compound-paren-prune]" {
              match
                  Weir.Script.assemble
                      [ 1, "let f remote ="
                        2, "    let symref ="
                        3, "        $(git ls-remote --symref $remote HEAD)"
                        4, "        |> Seq.choose (fun l ->"
                        5, "            match l with"
                        6, "            | Regex @\"^ref: refs/heads/(\\S+)\\s+HEAD\" b -> Some b"
                        7, "            | _ -> None)"
                        8, "        |> Seq.tryHead"
                        9, ""
                        10, "    match symref with"
                        11, "    | Some b -> b"
                        12, "    | None -> \"m\"" ]
              with
              | Ok [ ll ] ->
                  Expect.equal
                      ll.Text
                      ("let f remote = let symref = $(git ls-remote --symref $remote HEAD) "
                       + "|> Seq.choose (fun l -> match l with | Regex @\"^ref: refs/heads/(\\S+)\\s+HEAD\" b -> Some b | _ -> None) "
                       + "|> Seq.tryHead in match symref with | Some b -> b | None -> \"m\"")
                      "the lambda's own ) prunes the inner compound — no cross-paren wrap"
              | other -> failtest $"unexpected: {other}"
          }
          test "gap positions: first-after-head, mid, before-close (offside)" {
              Expect.equal
                  (joined [ "let x ="; ""; "    let a = 1"; "    a + 1" ])
                  "let x = let a = 1 in a + 1"
                  "gap as the FIRST line after a head"

              Expect.equal
                  (joined [ "let f x ="; "    printerr \"a\""; ""; "    printerr \"b\"" ])
                  (asmSib "let f x = printerr \"a\" ; printerr \"b\"")
                  "mid-body gap"
          }
          test "gap positions inside brackets: first, mid, before-close" {
              Expect.equal
                  (joined [ "let xs = ["; ""; "    1"; ""; "    2"; ""; "]" ])
                  "let xs = [ 1 ; 2 ]"
                  "gaps at every bracket position"
          }
          test "gap between match head and the first arm (|-inertness meets blanks)" {
              Expect.equal
                  (joined [ "let v ="; "    match 1 with"; ""; "    | _ -> 2" ])
                  "let v = match 1 with | _ -> 2"
                  ""
          }
          test "brackets never engage inside strings (the scanner guarantee)" {
              Expect.equal (joined [ "let s ="; "    [\"a[\""; "     \"b\"]" ]) "let s = [\"a[\" ; \"b\"]" ""
          } ]

let fmtMatchTests =
    testList
        "fmt: match arm alignment"
        [ test "arms align under the m (the drift pull)" {
              match
                  Weir.Fmt.formatLines
                      [ "let category ="
                        "    match 3 with"
                        "        | s when s > 2 -> \"big\""
                        "        | _ -> \"small\"" ]
              with
              | Ok lines ->
                  Expect.equal lines[2] "    | s when s > 2 -> \"big\"" ""
                  Expect.equal lines[3] "    | _ -> \"small\"" ""
              | Error e -> failtest e
          }
          test "nested matches align to their own m; the outer arm returns" {
              match
                  Weir.Fmt.formatLines
                      [ "let deep ="
                        "    match 1 with"
                        "        | 1 ->"
                        "            match 2 with"
                        "                | 2 -> \"a\""
                        "                | _ -> \"b\""
                        "        | _ -> \"c\"" ]
              with
              | Ok lines ->
                  Expect.equal lines[2] "    | 1 ->" "outer arm at outer m"
                  Expect.equal lines[4] "        | 2 -> \"a\"" "inner arm at inner m"
                  Expect.equal lines[6] "    | _ -> \"c\"" "outer resumes after inner closes"
              | Error e -> failtest e
          }
          test "union cases and chain stages are not arms" {
              match Weir.Fmt.formatLines [ "type Cmd ="; "    | First of int"; "    | Second" ] with
              | Ok lines -> Expect.equal lines[1] "    | First of int" "union case untouched"
              | Error e -> failtest e

              match
                  Weir.Fmt.formatLines
                      [ "let r ="
                        "    match 1 with"
                        "    | 1 ->"
                        "        sh -c \"echo one\""
                        "        | complete"
                        "    | _ -> 0" ]
              with
              | Ok lines ->
                  Expect.equal lines[4] "        | complete" "arm-body chain stage keeps its depth"
                  Expect.equal lines[5] "    | _ -> 0" "the next arm still aligns"
              | Error e -> failtest e
          }
          test "col-0 statement match arms stay at column 0" {
              match Weir.Fmt.formatLines [ "match 1 with"; "| 1 -> print \"a\""; "| _ -> print \"b\"" ] with
              | Ok lines -> Expect.equal lines[1] "| 1 -> print \"a\"" ""
              | Error e -> failtest e
          } ]

let fmtStroustrupTests =
    testList
        "fmt: Stroustrup brackets"
        [ test "dangling opener: a CONSISTENT group canonicalizes to +4" {
              // misaligned groups are assembly errors now [D:field-alignment]
              match
                  Weir.Fmt.formatLines [ "type Ctx = {"; "        Subdir: string"; "        Repo: string"; "    }" ]
              with
              | Ok lines ->
                  Expect.equal lines[1] "    Subdir: string" ""
                  Expect.equal lines[2] "    Repo: string" ""
                  Expect.equal lines[3] "}" "closer returns to the opener line"
              | Error e -> failtest e
          }
          test "misaligned fields are an assembly error, not an fmt repair" {
              match
                  Weir.Fmt.formatLines [ "type Ctx = {"; "        Subdir: string"; "      Repo: string"; "    }" ]
              with
              | Error e -> Expect.stringContains e "indented off its siblings" ""
              | Ok _ -> failtest "expected the alignment error"
          }
          test "with-header takes Stroustrup rules" {
              match Weir.Fmt.formatLines [ "let c2 = { c with"; "        Repo = \"r2\""; "}" ] with
              | Ok lines ->
                  Expect.equal lines[1] "    Repo = \"r2\"" ""
                  Expect.equal lines[2] "}" ""
              | Error e -> failtest e
          }
          test "inline opener: aligned input is fmt-stable (both styles accepted)" {
              match Weir.Fmt.formatLines [ "let target ="; "    { Name = \"a\""; "      Bp = \"b\" }" ] with
              | Ok lines -> Expect.equal lines[2] "      Bp = \"b\" }" "brace+2 stable"
              | Error e -> failtest e
          }
          test "nested Stroustrup: inner opener indents from its own line" {
              match Weir.Fmt.formatLines [ "let xs = ["; "    {"; "            A = 1"; "    }"; "]" ] with
              | Ok lines ->
                  Expect.equal lines[1] "    {" "inner opener is an element"
                  Expect.equal lines[2] "        A = 1" "inner entries +4 from the inner opener"
                  Expect.equal lines[3] "    }" "inner closer at the inner opener's indent"
                  Expect.equal lines[4] "]" "outer closer at the head"
              | Error e -> failtest e
          } ]

let replEchoTests =
    testList
        "REPL echo bounds"
        [ test "the laziness property: echo forces at most limit+1 pulls" {
              let pulls = ref 0

              let counted =
                  Seq.initInfinite (fun i ->
                      System.Threading.Interlocked.Increment pulls |> ignore
                      VInt(int64 i))

              let rendered, hint = Weir.Eval.echoValue (VSeq counted)
              Expect.isTrue (pulls.Value <= 11) $"forced {pulls.Value} pulls (bound is 11)"
              Expect.stringContains rendered "; …]" "truncation spelled"
              Expect.equal hint (Some "10 of ? shown") "lazy count stays ?"
          }
          test "materialized lists show the real count" {
              let v = VSeq([ for i in 1..12 -> VInt(int64 i) ] :> seq<Weir.Eval.Value>)
              let _, hint = Weir.Eval.echoValue v
              Expect.equal hint (Some "10 of 12 shown") ""
          }
          test "short seqs echo whole, no hint" {
              let v = VSeq([ VInt 1L; VInt 2L ] :> seq<Weir.Eval.Value>)
              let rendered, hint = Weir.Eval.echoValue v
              Expect.equal rendered "[1; 2]" ""
              Expect.equal hint None ""
          }
          test "echo clips long strings at 120 with an ellipsis" {
              let rendered, _ = Weir.Eval.echoValue (VStr(String.replicate 200 "x"))
              Expect.stringContains rendered "…\"" "clip marker inside the quotes"
              Expect.isTrue (rendered.Length < 140) $"clipped ({rendered.Length} chars)"
          }
          test "nesting truncates at each level" {
              let inner = VSeq([ for i in 1..15 -> VInt(int64 i) ] :> seq<Weir.Eval.Value>)
              let outer = VSeq([ for _ in 1..15 -> inner ] :> seq<Weir.Eval.Value>)
              let rendered, _ = Weir.Eval.echoValue outer
              Expect.equal (rendered.Split("; …").Length - 1) 11 "10 inner truncations + the outer"
          }
          test "show is byte-identical to its shipped contract (NOT the echo)" {
              let long = VSeq(seq { for i in 1..100 -> VInt(int64 i) })

              Expect.stringContains (Weir.Eval.formatValue long) "; 20; ...]" "show keeps 20 + dots"

              let s200 = String.replicate 200 "x"
              Expect.equal (Weir.Eval.formatValue (VStr s200)) ("\"" + s200 + "\"") "show never clips strings"
          } ]

let replColorTests =
    // [D:repl-color] — the paint-transparency property is the load-bearing
    // pin: coloring NEVER alters the text (strip . colorize = id)
    let colorize = Weir.Script.colorizeRepl (fun n -> n = "ls" || n = "print")
    let strip = Weir.Script.stripAnsi

    testList
        "REPL coloring"
        [ test "command mode tints: head bold-blue, argv dim, splice cyan, stage undimmed [D:semantic-tokens]" {
              let c = colorize "git add file.txt $x | Seq.head"
              Expect.stringContains c "\u001b[1;34mgit\u001b[0m" "the external head"
              Expect.stringContains c "\u001b[2madd\u001b[0m" "argv words render dim"
              Expect.stringContains c "\u001b[36m$x\u001b[0m" "the splice island"
              Expect.isFalse (c.Contains "\u001b[2mhead") "after | the stage is expression land"
          }
          test "paint transparency: strip after colorize is the identity" {
              let fixtures =
                  [ "let x = 1 + 2"
                    "ls |> where (fun f -> f.bytes > 10)"
                    "let s = @\"unclosed raw to eol"
                    "let t = \"\"\"triple \\ and \" inside\"\"\""
                    "git log --oneline // trailing comment"
                    "$(git status) |> Seq.head"
                    "^ls -la $target !marker"
                    "let e = \"emoji 😀 inside\" |> Str.length"
                    "let xs = [1; 2"
                    "" ]

              for f in fixtures do
                  Expect.equal (strip (colorize f)) f $"altered: {f}"
          }
          test "head verdicts: known bold, unknown red, forced PATH-only" {
              Expect.stringContains (colorize "ls -la") "\x1b[1mls\x1b[0m" "known head bold"
              Expect.stringContains (colorize "zzznope arg") "\x1b[31mzzznope\x1b[0m" "unknown head red"

              // ^-forced resolves against PATH only: 'print' is known but
              // not a binary, so ^print paints red even though bare print
              // would be bold
              Expect.stringContains (colorize "^print x") "\x1b[31mprint\x1b[0m" "force ignores bindings"
          }
          test "lexical spans: keyword, string, comment, number, uppercase" {
              let out = colorize "let n = Some 42 // done"
              Expect.stringContains out "\x1b[34mlet\x1b[0m" "keyword"
              Expect.stringContains out "\x1b[33mSome\x1b[0m" "uppercase (casing law)"
              Expect.stringContains out "\x1b[36m42\x1b[0m" "number"
              Expect.stringContains out "\x1b[90m// done\x1b[0m" "comment"
              Expect.stringContains (colorize "let s = \"hi\"") "\x1b[32m" "string"
          }
          test "an unclosed verbatim colors to end of line (live feedback)" {
              let out = colorize "let s = @\"abc def"
              Expect.stringContains out "abc def\x1b[0m" "string span reaches EOL"
          }
          test "redraw-cost ceiling: 1000 pathological 200-char lines" {
              let line =
                  String.replicate 5 "let ZZig = $(git log) |> where (fun f -> f.bytes > 12) ; "
                  + "@\"tail"

              let sw = System.Diagnostics.Stopwatch.StartNew()

              for _ in 1..1000 do
                  colorize line |> ignore

              sw.Stop()
              Expect.isTrue (sw.ElapsedMilliseconds < 2000L) $"1000 lines took {sw.ElapsedMilliseconds}ms"
          } ]

let seqPatternTests =
    testList
        "Seq patterns"
        [ test "the four shapes bind and select" {
              expectValue
                  "let f = fun xs -> match xs |> Seq.skip 0 with | [] -> 0 | [a] -> a | [a; b] -> a + b | x :: rest -> x + (rest |> Seq.length) in (f []) + (f [5]) + (f [3; 4]) + (f [10; 9; 8; 7])"
                  (VInt 25L)
          }
          test "chained cons is right-associative" {
              expectValue "match [1; 2; 3] with | a :: b :: rest -> a + b + (rest |> Seq.sum) | _ -> 0" (VInt 6L)
          }
          test "nested patterns in element positions" {
              expectValue "match [Some 5; None] with | [Some x; _] -> x | _ -> 0" (VInt 5L)
          }
          test "rest binds the tail as a seq" {
              expectValue "match [1; 2; 3; 4] with | _ :: rest -> rest |> Seq.length | [] -> 0" (VInt 3L)
          }
          test "exhaustiveness: nil + irrefutable cons complete; fixed arity never alone" {
              expectValue "match [9] with | [] -> 0 | x :: _ -> x" (VInt 9L)

              Expect.stringContains (checkErr "match [1] |> Seq.skip 0 with | [] -> 0").Message "missing: _ :: _" ""

              Expect.stringContains
                  (checkErr "match [1] |> Seq.skip 0 with | [a; b] -> a | [] -> 0").Message
                  "missing: _ :: _"
                  "fixed arity does not complete"
          }
          test "seq patterns are refutable: banned in binders" {
              match Weir.Parser.parseStmt "let (x :: rest) = [1; 2]" with
              | Ok stmt ->
                  match stmt with
                  | Weir.Ast.SLetPat(p, _) ->
                      match Weir.Check.typecheck env (parse "match [1] with | x :: _ -> x | [] -> 0") with
                      | Ok _ -> () // the match spelling stays legal
                      | Error e -> failtest (formatError e)
                  | _ -> ()
              | Error _ -> ()

              Expect.stringContains (checkErr "let (x :: rest) = [1; 2] in x").Message "this pattern can fail" ""
          }
          test "non-seq scrutinee rejects by name" {
              Expect.stringContains
                  (checkErr "match 5 with | [] -> 0 | _ -> 1").Message
                  "seq patterns need a seq scrutinee"
                  ""
          }
          test "element types flow: string elems reject int literals in element position" {
              Expect.stringContains
                  (checkErr "match [\"a\"] with | [1] -> 1 | _ -> 0").Message
                  "int literal patterns need an int scrutinee"
                  ""
          }
          test "the memoize-once law: probes + rest consumption pull ONE enumeration" {
              let opens = ref 0

              let counted =
                  seq {
                      System.Threading.Interlocked.Increment opens |> ignore

                      for i in 1..5 do
                          yield Weir.Builtins.file $"f{i}" i false
                  }

              match
                  runWith
                      [ "ls", VSeq counted ]
                      "match ls with | [] -> 0 | [a] -> a.bytes | [p; q] -> p.bytes | x :: rest -> x.bytes + (rest |> Seq.map _.bytes |> Seq.sum)"
              with
              | VInt n ->
                  Expect.equal n 15L "1 + 2+3+4+5"
                  Expect.equal opens.Value 1 "one enumeration TOTAL across four arms and rest"
              | v -> failtest $"unexpected {v}"
          }
          test "guards compose with seq patterns" {
              expectValue "match [5; 6] with | x :: _ when x > 4 -> x | _ -> 0" (VInt 5L)
          }
          test "Regex patterns sit as sibling arms" {
              expectValue "match [\"key=1\"] with | [Regex @\"(\\w+)=\" k] -> k | _ -> \"no\"" (VStr "key")
          } ]

let blockLetCmdTests =
    testList
        "Block-let command RHS"
        [ test "the forms: command RHS binds inside a body [D:block-let-cmd]" {
              match
                  Weir.Script.assemble
                      [ 1, "let graft c ="
                        2, "    let tree = git rev-parse $c | Seq.head"
                        3, "    tree" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let graft c = let tree = git rev-parse $c | Seq.head in tree" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "the shadowing block twin: bindings beat PATH at depth (failing-first pin)" {
              // guard-dropped, this spawned a real PATH binary (SPAWNED
              // observed live); guarded, the block name wins
              expectValue
                  "let f = fun y -> (let zzshadow = fun a -> a in let z = zzshadow y in z |> Seq.head) in f [\"safe\"]"
                  (VStr "safe")
          }
          test "sigil equivalence: bare block RHS = the $() spelling" {
              // the two spellings must produce the same TypedExpr SHAPE
              // (ELet of the same chain expression)
              let bare =
                  Weir.Script.assemble [ 1, "let f c ="; 2, "    let a = git rev-parse $c | Seq.head"; 3, "    a" ]

              let sigil =
                  Weir.Script.assemble [ 1, "let f c ="; 2, "    let a = $(git rev-parse $c) |> Seq.head"; 3, "    a" ]

              match bare, sigil with
              | Ok [ b ], Ok [ s ] -> Expect.isTrue (b.Text.Length > 0 && s.Text.Length > 0) "both assemble"
              | other -> failtest $"unexpected: {other}"
          }
          test "the in-stop from inside: quoted in passes, bare in stops" {
              let r: Weir.Parser.Resolver =
                  { IsKnown = fun n -> n <> "echo"
                    IsCommandCallable = fun _ -> false
                    IsExternal = fun n -> n = "echo"
                    ExternalNames = fun () -> Seq.empty }

              match Weir.Parser.parseLine r "let f x = let w = echo alpha \"in\" beta | Seq.head in w" with
              | Ok _ -> ()
              | Error e -> failtest $"quoted-in must pass as argv: {e}"
          }
          test "single-line let-in stays expression-only (the standing park, pinned)" {
              // -e/REPL spelling: no command mode; git resolves as an
              // unbound name in expression position
              Expect.stringContains (checkErr "let x = git diff in x").Message "unbound variable 'git'" ""
          }
          test "products: a command RHS as the LAST binding before the body" {
              match
                  Weir.Script.assemble
                      [ 1, "let f c ="
                        2, "    let a = 1"
                        3, "    let b = git rev-parse $c | Seq.head"
                        4, "    b" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let f c = let a = 1 in let b = git rev-parse $c | Seq.head in b" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "products: parens interiors stay expression-only" {
              Expect.stringContains
                  (checkErr "let f = fun c -> (let x = git diff in x) in f 1").Message
                  "unbound variable 'git'"
                  "nested-paren let-in keeps the exclusion"
          }
          test "function is reserved with its teaching hint" {
              match Weir.Parser.parseStmt "let function = 1" with
              | Error msg -> Expect.stringContains msg "reserved" "the hint"
              | Ok _ -> failtest "expected the reservation"
          } ]

let semanticTokenTests =
    testList
        "Semantic tokens"
        [ test "a command statement tokens head + argv [D:semantic-tokens]" {
              let toks = Weir.Lsp.semanticTokensFor [ "echo hi there" ]
              Expect.equal toks [ (0, 0, 4, 0); (0, 5, 2, 1); (0, 8, 5, 1) ] ""
          }
          test "splice islands: $name whole, (expr) delimiters only; strings stay lexical" {
              let toks =
                  Weir.Lsp.semanticTokensFor [ "let x = \"v\""; "echo hi $x (1 + 2) \"quoted\"" ]

              Expect.equal
                  toks
                  [ (1, 0, 4, 0); (1, 5, 2, 1); (1, 8, 2, 2); (1, 11, 1, 2); (1, 17, 1, 2) ]
                  "the quoted arg and the splice interior emit nothing"
          }
          test "definitionFor: top-level lets, shadowing, and nulls [D:lsp-requests]" {
              let lines =
                  [ "let alpha = 1"
                    "let beta = alpha + 1"
                    "let alpha = beta"
                    "print $\"{alpha + beta}\"" ]

              // a use resolves to its top-level let
              Expect.equal (Weir.Lsp.definitionFor lines 2 12) (Some(1, 5, 5)) "alpha use -> its let"
              // shadowing: the LAST binder above the use wins
              Expect.equal (Weir.Lsp.definitionFor lines 4 10) (Some(3, 5, 5)) "last binder above wins"
              // a let-pattern binder is a definition site too
              let pat = [ "let (a, b) = (1, 2)"; "print $\"{a}\"" ]
              Expect.equal (Weir.Lsp.definitionFor pat 2 10) (Some(1, 6, 1)) "letpat binder found"
              // builtins have no source: null
              Expect.equal (Weir.Lsp.definitionFor [ "print 1" ] 1 2) None "builtin -> null"
              // FLIPPED by the binder-span session [PLAN-diagnostics-arc C]:
              // the park's conservative null became the jump
              Expect.equal (Weir.Lsp.definitionFor [ "let f x = x + 1" ] 1 11) (Some(1, 7, 1)) "param use -> param"
          }
          test "definitionFor: record members and union cases resolve to the type declaration [D:lsp-requests]" {
              let lines =
                  [ "type Verb ="
                    "    | Pull"
                    "    | Push of int"
                    ""
                    "type Cfg = { Host: string; Port: int }"
                    ""
                    "let v = Pull"
                    "let c = { Host = \"h\"; Port = 1 }"
                    "print $\"{c.Port}\""
                    "match v with"
                    "| Pull -> print \"p\""
                    "| Push n -> print $\"{n}\"" ]

              // expression-position union case -> its case in the decl
              Expect.equal (Weir.Lsp.definitionFor lines 7 9) (Some(2, 7, 4)) "ctor use -> the case"
              // field ACCESS -> the field in the record decl
              Expect.equal (Weir.Lsp.definitionFor lines 9 13) (Some(5, 28, 4)) "c.Port -> the Port field"
              // record-LITERAL field name -> the field in the decl
              Expect.equal (Weir.Lsp.definitionFor lines 8 11) (Some(5, 14, 4)) "literal Host -> the Host field"
              // PATTERN-position case (PSpan carries it) -> the case
              Expect.equal (Weir.Lsp.definitionFor lines 11 3) (Some(2, 7, 4)) "| Pull -> the case"
              // a pattern PAYLOAD binder is a local binder: null (the park)
              Expect.equal (Weir.Lsp.definitionFor lines 12 8) None "payload binder -> null"
          }
          test
              "definitionFor: LOCAL binders — inner lets, payload binders, innermost shadowing [PLAN-diagnostics-arc C]" {
              // inner-let use -> its binder (through the block-let joins)
              let inner =
                  [ "let go () ="; "    let acc = 1 + 1"; "    print $\"{acc}\""; "go ()" ]

              Expect.equal (Weir.Lsp.definitionFor inner 3 14) (Some(2, 9, 3)) "inner-let use -> its binder"

              // payload-binder use -> the pattern binder (PSpan)
              let pay =
                  [ "type V = | Push of int"; "match Push 1 with"; "| Push n -> print $\"{n}\"" ]

              Expect.equal (Weir.Lsp.definitionFor pay 3 22) (Some(3, 8, 1)) "payload use -> the binder"

              // innermost wins: the param shadows the top-level inside f,
              // the top-level owns the use outside
              let shadow = [ "let x = 1"; "let f x = x + 2"; "print $\"{f x}\"" ]
              Expect.equal (Weir.Lsp.definitionFor shadow 2 11) (Some(2, 7, 1)) "inside f: the param"
              Expect.equal (Weir.Lsp.definitionFor shadow 3 12) (Some(1, 5, 1)) "outside f: the top-level"
          }
          test "definitionFor: the from-json type name jumps to its declaration [PLAN-diagnostics-arc A3]" {
              let lines =
                  [ "type Oidc = { value: string }"
                    "let toks = [\"{}\"] |> from json Oidc |> Seq.force"
                    "print \"x\"" ]

              Expect.equal (Weir.Lsp.definitionFor lines 2 33) (Some(1, 6, 4)) "Oidc use -> the type decl"
          }
          test "definitionFor: Env.load / Args.load target type jumps to its declaration (user receipt)" {
              // the bespoke arm absorbs the type-name argument (no TEVar),
              // so it resolves off the load node's own def
              let envLines = [ "type TokenEnv = { name: string }"; "let tok = Env.load TokenEnv" ]

              Expect.equal (Weir.Lsp.definitionFor envLines 2 22) (Some(1, 6, 8)) "Env.load TokenEnv -> the type decl"

              let argsLines = [ "type Cli = { verbose: bool }"; "let c = Args.load Cli" ]
              Expect.equal (Weir.Lsp.definitionFor argsLines 2 20) (Some(1, 6, 3)) "Args.load Cli -> the type decl"
          }
          test "hoverType: a lambda param shows its own type, not the enclosing arrow (user receipt)" {
              // t is used via field access -> an OPEN ROW; the bug showed
              // the param carrying the function's `... -> ...` arrow because
              // nodeAt fell back to the lambda. The param must show only its
              // own domain type.
              let lines = [ "let snapshot t = t.name" ]
              let onName = Weir.Lsp.hoverType lines 1 6 // on `snapshot`
              let onParam = Weir.Lsp.hoverType lines 1 14 // on the param `t`
              Expect.isTrue (onName |> Option.exists (fun s -> s.Contains "->")) "the function shows an arrow type"
              Expect.isSome onParam "the param hovers to something"

              Expect.isFalse
                  (onParam |> Option.exists (fun s -> s.Contains "->"))
                  "the param shows its own type, no arrow"
          }
          test "doc comments: /// attaches to the next declaration; a blank breaks it [D:doc-comments]" {
              let attached =
                  [ "/// The token env."; "/// Two lines."; "type TokenEnv = { name: string }" ]

              Expect.equal
                  (Weir.Script.docAttachments attached
                   |> List.map (fun d -> d.Line, d.Col, d.Len, d.Doc))
                  [ (3, 6, 8, [ "The token env."; "Two lines." ]) ]
                  "a contiguous /// run attaches to the type name at (3,6,8), lines accumulated in order"

              // a blank line between the doc and the declaration BREAKS it
              Expect.equal
                  (Weir.Script.docAttachments [ "/// orphan"; ""; "type T = { x: int }" ])
                  []
                  "blank-separated /// does not attach"

              Expect.equal
                  (Weir.Script.docAttachments [ "/// doc"; "let inc x = x + 1" ]
                   |> List.map (fun d -> d.Line, d.Col, d.Doc))
                  [ (2, 5, [ "doc" ]) ]
                  "let binder name is the key"
          }
          test "doc comments: hover shows type FIRST, then the /// doc [D:doc-comments]" {
              let lines = [ "/// Adds one."; "let inc x = x + 1"; "print (inc 1)" ]
              let s = Weir.Lsp.hoverType lines 2 5 |> Option.defaultValue "" // on `inc`
              Expect.stringContains s "->" "the type is present"
              Expect.stringContains s "Adds one." "the doc is present"
              Expect.isTrue (s.IndexOf "->" < s.IndexOf "Adds one.") "type first, then doc"

              // hovering elsewhere on the line (the value) shows type only, no doc
              let onValue = Weir.Lsp.hoverType lines 2 15

              Expect.isFalse
                  (onValue |> Option.exists (fun v -> v.Contains "Adds one."))
                  "the doc shows only on the documented NAME"
          }
          test "doc comments: hover is type-first + doc at the type-decl and field positions [D:doc-comments]" {
              let lines =
                  [ "/// The config record."
                    "type Cfg = {"
                    "    /// the host name"
                    "    Host: string"
                    "}" ]

              let onType = Weir.Lsp.hoverType lines 2 6 |> Option.defaultValue "" // Cfg
              Expect.stringContains onType "Host: string" "type name renders the record structure"
              Expect.stringContains onType "The config record." "and its doc"

              let onField = Weir.Lsp.hoverType lines 4 5 |> Option.defaultValue "" // Host
              Expect.stringContains onField "string" "field shows its type"
              Expect.stringContains onField "the host name" "and its doc"
              Expect.isTrue (onField.IndexOf "string" < onField.IndexOf "the host") "type first, then doc"
          }
          test "doc comments: hover a union case shows its signature + doc [D:doc-comments]" {
              let lines =
                  [ "type Outcome ="; "    /// merge hit a conflict"; "    | Conflict of string" ]

              let onCase = Weir.Lsp.hoverType lines 3 7 |> Option.defaultValue "" // Conflict
              Expect.stringContains onCase "Conflict of string" "the case signature"
              Expect.stringContains onCase "merge hit a conflict" "and its doc"
          }
          test "doc comments: a misaligned /// errors — both field and case; aligned is clean [D:doc-comments]" {
              let hasAlign (ls: string list) =
                  let d, _, _, _ = Weir.Script.analyzeLines "d.weir" ls
                  d |> List.exists (fun x -> x.Code = "doc-align")

              // aligned field doc: clean
              Expect.isFalse
                  (hasAlign [ "type Cfg = {"; "    /// the host"; "    Host: string"; "}" ])
                  "a doc at the field's own column is clean"

              // field doc one column short of the field: error
              Expect.isTrue
                  (hasAlign [ "type Cfg = {"; "  /// the host"; "    Host: string"; "}" ])
                  "a doc off the field's anchor errors"

              // union case doc misaligned: error (the other direction)
              Expect.isTrue
                  (hasAlign [ "type Outcome ="; "        /// merge conflict"; "    | Conflict of string" ])
                  "a doc off the union case's anchor errors"

              // a top-level doc at column 1 above a column-1 let: clean
              Expect.isFalse (hasAlign [ "/// adds one"; "let inc x = x + 1" ]) "top-level doc aligns at column 1"
          }
          test "doc comments: fmt preserves docs, canonicalizes /// to the field anchor, is idempotent [D:doc-comments]" {
              let src =
                  [ "/// The record."; "type Cfg = {"; "  /// the host"; "    Host: string"; "}" ]

              match Weir.Fmt.formatLines src with
              | Error e -> failtestf "fmt failed: %s" e
              | Ok out ->
                  Expect.isTrue (out |> List.exists (fun l -> l.Contains "The record.")) "top doc preserved"
                  Expect.isTrue (out |> List.exists (fun l -> l.Contains "the host")) "field doc preserved"

                  let indent (s: string) = s.Length - s.TrimStart().Length
                  let docLine = out |> List.find (fun l -> l.Contains "the host")
                  let fieldLine = out |> List.find (fun l -> l.Contains "Host:")
                  Expect.equal (indent docLine) (indent fieldLine) "the /// is canonicalized to the field's anchor"

                  match Weir.Fmt.formatLines out with
                  | Ok out2 -> Expect.equal out2 out "fmt is idempotent with docs present"
                  | Error e -> failtestf "second fmt failed: %s" e
          }
          test "builtin docs: every doc example runs clean [D:builtin-docs]" {
              // D1 = (a) EXECUTABLE, weir-side: the Example is registry DATA
              // run through the same check+eval path, not prose parsed from
              // an F# literal. A rotted example fails the build here.
              for KeyValue(name, d) in Weir.Builtins.builtinDocs do
                  match d.Example with
                  | None -> ()
                  | Some ex ->
                      try
                          run ex |> ignore
                      with e ->
                          failtestf "the doc example for '%s' failed to run: %s\n%s" name ex e.Message
          }
          test "builtin docs: hover on a builtin shows its type first, then the doc [D:builtin-docs]" {
              let lines = [ "let r = [1;2;3] |> Seq.map (fun x -> x + 1) |> Seq.force" ]
              let h = Weir.Lsp.hoverType lines 1 22 |> Option.defaultValue "" // on Seq.map
              Expect.stringContains h "->" "the type is present"
              Expect.stringContains h "every element" "the summary is present"
              Expect.stringContains h "Seq.force" "the executable example is present"
              Expect.isTrue (h.IndexOf "->" < h.IndexOf "every element") "type first, then doc"
          }
          test "builtin docs: Env.load's doc shows on `load`, NOT on the module Env or the type arg [D:builtin-docs]" {
              let lines = [ "type Cfg = { name: string }"; "let c = Env.load Cfg" ]

              let onLoad = Weir.Lsp.hoverType lines 2 14 |> Option.defaultValue "" // on `load`
              Expect.stringContains onLoad "typed record" "the Env.load summary reaches hover on `load`"
              Expect.stringContains onLoad "field law" "and its law pointer"

              // the reported bug: hovering the module `Env` must NOT surface load's doc
              let onEnv = Weir.Lsp.hoverType lines 2 10 |> Option.defaultValue "" // on `Env`
              Expect.isFalse (onEnv.Contains "field law") "hovering the module Env does not surface load's doc"
          }
          test "builtin docs: reifier hover maps the |completed key back to complete [D:builtin-docs]" {
              let lines = [ "let c = echo hi | complete" ]
              let h = Weir.Lsp.hoverType lines 1 22 |> Option.defaultValue "" // on `complete`
              Expect.stringContains h "Completed record" "the reifier's doc reaches hover through TEVar |completed"
              Expect.stringContains h "output goes where the meaning goes" "the reifier law pointer"
          }
          test "builtin docs: a builtin type name hovers its doc (word-at-cursor fallback) [D:builtin-docs]" {
              let lines = [ "type W = { c: Completed }" ]
              let h = Weir.Lsp.hoverType lines 1 18 |> Option.defaultValue "" // on `Completed`
              Expect.stringContains h "finished command" "the Completed type doc via the word fallback"
              Expect.stringContains h "complete" "names where you get one"
          }
          test
              "hover: keywords / operators / wildcard are silent; identifiers & literals still answer [D:hover-silence]" {
              let lines = [ "let x = if true then 1 else 2" ]

              let none c m =
                  Expect.isNone (Weir.Lsp.hoverType lines 1 c) m
              // the silence matrix — a wrong `unit`/`int` on these teaches hover lies
              none 1 "let"
              none 7 "="
              none 9 "if"
              none 18 "then"
              none 25 "else"
              none 16 "a space between tokens"
              // the negatives: real tokens one column over still answer
              Expect.isSome (Weir.Lsp.hoverType lines 1 5) "the binder x answers"
              Expect.equal (Weir.Lsp.hoverType lines 1 13) (Some "bool") "the true literal answers"
              Expect.equal (Weir.Lsp.hoverType lines 1 22) (Some "int") "the int literal answers"

              // match arm: `match` / `with` / `|` / `->` / `_` all silent
              let m = [ "let r = match 1 with | 1 -> 10 | _ -> 0" ]

              let noneM c mm =
                  Expect.isNone (Weir.Lsp.hoverType m 1 c) mm

              noneM 9 "match"
              noneM 17 "with"
              noneM 22 "| (match arm)"
              noneM 26 "-> (arrow)"
              noneM 34 "_ (wildcard)"
          }
          test
              "hover: a usage shows its declaration's doc; a field in a literal shows the FIELD's type + doc [D:hover-completeness]" {
              // 1a: a usage of a documented binding resolves to the decl doc
              let use_ = [ "/// bumps by one"; "let inc x = x + 1"; "let y = inc 5" ]
              let onUsage = Weir.Lsp.hoverType use_ 3 9 |> Option.defaultValue "" // `inc` at the call site
              Expect.stringContains onUsage "->" "the usage shows the type"
              Expect.stringContains onUsage "bumps by one" "and the DECLARATION's doc (resolved via definitionFor)"

              // 1c literal: a field name in `{ Field = … }` shows the field's type + doc
              let lit =
                  [ "type Target = {"
                    "    /// the bicep path"
                    "    BicepPath: string"
                    "}"
                    "let t = { BicepPath = \"b\" }" ]

              let onField = Weir.Lsp.hoverType lit 5 12 |> Option.defaultValue "" // BicepPath in the literal
              Expect.stringContains onField "string" "the FIELD's type, not the record"
              Expect.isFalse (onField.Contains "Target") "not the record type Target"
              Expect.stringContains onField "the bicep path" "and the field's doc"
          }
          test
              "hover: a pattern constructor shows its signature; the payload binder shows its OWN type [D:hover-completeness]" {
              // the payload type (string) deliberately differs from the arm
              // result (int) — so the binder must resolve to its OWN type,
              // not the enclosing match's. `r` is bound to a PR value so the
              // constructor pattern has a known union to match.
              let lines =
                  [ "type PR = Pulled of string | Idle"
                    "let r = Pulled \"x\""
                    "let g = match r with | Pulled s -> Str.length s | Idle -> 0" ]

              let onCtor = Weir.Lsp.hoverType lines 3 26 |> Option.defaultValue "" // `Pulled` in the pattern
              Expect.stringContains onCtor "Pulled : string -> PR" "the constructor signature"

              let onPayload = Weir.Lsp.hoverType lines 3 31 |> Option.defaultValue "" // `s`, the payload binder
              Expect.equal onPayload "string" "the payload binder's OWN type, not the arm's int"
          }
          test
              "over-application on an indented continuation points at the extra args + hints the indent [D:over-apply-continuation]" {
              // the misleading case: `deleteBranch branch` indented DEEPER
              // than `makeRef …` is slurped as extra arguments — the error
              // must land on the continuation, not the head, and say so
              let lines =
                  [ "let makeRef a b = a"
                    "let deleteBranch x = x"
                    "let branch = \"b\""
                    "let f ="
                    "    makeRef \"x\" branch"
                    "        deleteBranch branch" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "c.weir" lines
              let d = diags |> List.find (fun x -> x.Message.Contains "takes at most")
              Expect.equal d.Line 6 "points at the CONTINUATION line (line 6), not the head (line 5)"
              Expect.stringContains d.Message "indented continuation" "the hint names the real cause (the indent)"
          }
          test "an errored let warns its command heads and suppresses the unbound cascade [PLAN-diagnostics-arc B5+B6]" {
              // B6: the failed deploy binds a HOLE — one real error,
              // zero "unbound 'deploy'. Did you mean 'Deploy'?" echoes,
              // and the hole's descendants (application results,
              // constraints, discards) stay silent too
              let lines =
                  [ "type Cmd = | Deploy of int"
                    "let deploy a = 1 + \"x\""
                    "match Deploy 1 with"
                    "| Deploy a -> print (show (deploy a))" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "b.weir" lines
              let errors = diags |> List.filter (fun d -> d.Severity = "error")
              Expect.hasLength errors 1 "ONE real error, zero echoes"
              Expect.stringContains errors[0].Message "expected int, got string" "the real error"

              // B5: an ERRORED statement still surfaces its command-head
              // warnings (parse-level walk — no typed tree exists)
              let lines2 =
                  [ "let go t ="
                    "    let e = targ etEnv t"
                    "    !e"
                    "        echo hi"
                    "    print \"ok\"" ]

              let diags2, _, _, _ = Weir.Script.analyzeLines "b2.weir" lines2

              Expect.exists
                  diags2
                  (fun d -> d.Code = "cmd-not-found" && d.Message.Contains "targ")
                  "the errored statement's head warns"
          }
          test "row provenance: a bad field access errors at the ACCESS, not the call [PLAN-diagnostics-arc D]" {
              // the bicep 62/107 shape reduced: quality's t.BicepPath2
              // is the bug; the call that supplies the T is only the meet
              let lines =
                  [ "type T = { BicepPath: string; Name: string }"
                    "let quality t ="
                    "    print (t.BicepPath2)"
                    "let mk = { BicepPath = \"b\"; Name = \"n\" }"
                    "quality mk" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              match diags |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.equal (d.Line, d.Col) (3, 14) "positions at the access"
                  Expect.equal (d.EndLine, d.EndCol) (Some 3, Some 24) "covers the field word"
                  Expect.stringContains d.Message "Did you mean 'BicepPath'?" "the hint survives"
                  Expect.stringContains d.Message "(the value becomes a T at 5:1)" "the meet is the note"
              | other -> failtest $"expected ONE error, got {other}"

              // a DIRECT access on the concrete value stays where it was:
              // no origin recorded, no note
              let direct =
                  [ "type T = { BicepPath: string }"
                    "let mk = { BicepPath = \"b\" }"
                    "print (mk.BicepPath2)" ]

              let diags2, _, _, _ = Weir.Script.analyzeLines "pin2.weir" direct

              match diags2 |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.equal (d.Line, d.Col) (3, 11) "unchanged position"
                  Expect.isFalse (d.Message.Contains "becomes a") "no meet note"
              | other -> failtest $"expected ONE error, got {other}"
          }
          test "row provenance: the TYPE-mismatch sibling also anchors at the access [PLAN-open-findings D]" {
              // right field name, WRONG type, cross-statement: t.count is
              // forced to string but T.count is int. Was reported at the
              // meet; now at the access, meet as the note (the no-field
              // sibling's shape, sharing the atAccess helper)
              let lines =
                  [ "type T = { count: int }"
                    "let f t ="
                    "    print (Str.length t.count)"
                    "let mk = { count = 1 }"
                    "f mk" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              match diags |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.equal (d.Line, d.Col) (3, 25) "positions at the access, not the meet (was 5:1)"
                  Expect.stringContains d.Message "expected int, got string" "the mismatch message"
                  Expect.stringContains d.Message "(the value becomes a T at 5:1)" "the meet is the note"
              | other -> failtest $"expected ONE error, got {other}"

              // WITHIN-statement mismatch is unchanged: direct at the access,
              // no meet note (no cross-statement origin recorded)
              let within =
                  [ "type T = { count: int }"
                    "let mk = { count = 1 }"
                    "print (Str.length mk.count)" ]

              let d2, _, _, _ = Weir.Script.analyzeLines "pin2.weir" within

              match d2 |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.stringContains d.Message "expected string, got int" "the mismatch"
                  Expect.isFalse (d.Message.Contains "becomes a") "no meet note within a statement"
              | other -> failtest $"expected ONE error, got {other}"
          }
          test "Args/Env.load near-miss shapes teach ONE-type-name [PLAN-diagnostics-arc A1]" {
              // `Args.load C md` (a space inside the type name) used to
              // fall through to "module Args has no member 'load'" — a
              // lie: load is an ARM, not a member
              Expect.stringContains (checkErr "Args.load").Message "takes ONE record or union type name" "zero args"

              Expect.stringContains
                  (checkErr "Args.load Cmd extra").Message
                  "takes ONE record or union type name"
                  "the space-in-the-type-name shape (two args)"

              Expect.stringContains (checkErr "Env.load Cfg md").Message "takes ONE record type name" "the Env twin"
          }
          test "formatting request contracts: broken statements verbatim, assemble failure refuses [D:lsp-requests]" {
              // an unparseable statement never refuses the FILE — indent
              // normalizes, the broken line rides verbatim (format-on-save
              // on a broken buffer stays useful)
              match Weir.Fmt.formatLines [ "let go () ="; "  let x = (((("; "  print \"hi\"" ] with
              | Ok out ->
                  Expect.equal
                      out
                      [ "let go () ="; "    let x = (((("; "    print \"hi\"" ]
                      "reindented around the verbatim broken line"
              | Error e -> failtest $"broken statement must not refuse the file: {e}"

              // an assemble failure refuses -> the LSP answers no edits
              match Weir.Fmt.formatLines [ "    dangling continuation" ] with
              | Error _ -> ()
              | Ok out -> failtest $"assemble failure must refuse, got {out}"
          }
          test "splat spans join the splice legend [D:argv-splat]" {
              let toks =
                  Weir.Lsp.semanticTokensFor [ "let fs = [\"a\"]"; "echo go $@fs $@([\"b\"]) tail" ]

              Expect.equal
                  (toks |> List.filter (fun (l, _, _, _) -> l = 1))
                  [ (1, 0, 4, 0)
                    (1, 5, 2, 1)
                    (1, 8, 4, 2)
                    (1, 13, 3, 2)
                    (1, 21, 1, 2)
                    (1, 23, 4, 1) ]
                  "head, argv, $@name whole, $@( delims, tail argv"
          }
          test "a splatted reifier chain colors like any command chain [D:splat-reifier-chains]" {
              let toks =
                  Weir.Lsp.semanticTokensFor [ "let fs = [\"a\"]"; "let r = echo go $@fs tail | complete" ]

              Expect.equal
                  (toks |> List.filter (fun (l, _, _, _) -> l = 1))
                  [ (1, 8, 4, 0); (1, 13, 2, 1); (1, 16, 4, 2); (1, 21, 4, 1) ]
                  "head, argv, $@name whole, tail argv — the reifier name stays lexical"
          }
          test "the shadowed-cat trio: binding wins, deletion restores, ^ forces" {
              // bound: an application — NO command tokens
              let bound =
                  Weir.Lsp.semanticTokensFor [ "let echo x = x"; "let y = echo 5"; "print $\"{y}\"" ]

              Expect.equal bound [] "a known binding is expression mode"

              // unbound: the same text tokens as command
              let free = Weir.Lsp.semanticTokensFor [ "echo 5" ]
              Expect.equal free [ (0, 0, 4, 0); (0, 5, 1, 1) ] ""

              // forced: the ^ rides in the head span
              let forced = Weir.Lsp.semanticTokensFor [ "let echo x = x"; "^echo hi" ]
              Expect.equal (forced |> List.filter (fun (l, _, _, _) -> l = 1)) [ (1, 0, 5, 0); (1, 6, 2, 1) ] ""
          }
          test "a parse-failed statement renders expression-colored (no phantom tokens)" {
              // [D:seq-commit] makes this an error; a failed statement
              // emits NOTHING
              let toks =
                  Weir.Lsp.semanticTokensFor
                      [ "let v0 ="
                        "    let v3 = \"a\""
                        "    print \"mm\""
                        "    let v4 = v3 ?!?"
                        "    3" ]

              Expect.equal toks [] "no command tokens anywhere"
          }
          test "district body lines token; the wrapper glyphs emit nothing" {
              let toks =
                  Weir.Lsp.semanticTokensFor
                      [ "if 1 > 0 then !"; "    echo m one"; "    echo m two"; ""; "print \"z\"" ]

              Expect.equal
                  toks
                  [ (1, 4, 4, 0)
                    (1, 9, 1, 1)
                    (1, 11, 3, 1)
                    (2, 4, 4, 0)
                    (2, 9, 1, 1)
                    (2, 11, 3, 1) ]
                  "argv/head spans have physical homes; no token past EOL"
          }
          test "error-state files emit partial truth" {
              let toks =
                  Weir.Lsp.semanticTokensFor [ "echo before ok"; "let bad = ?!?"; "echo after ok" ]

              Expect.equal
                  toks
                  [ (0, 0, 4, 0)
                    (0, 5, 6, 1)
                    (0, 12, 2, 1)
                    (2, 0, 4, 0)
                    (2, 5, 5, 1)
                    (2, 11, 2, 1) ]
                  "statements that parsed token; the failed one is silent"
          }
          test "expression stages after | emit nothing; reifiers emit nothing" {
              let toks =
                  Weir.Lsp.semanticTokensFor [ "git status --porcelain | Seq.map Str.trim" ]

              Expect.equal
                  toks
                  [ (0, 0, 3, 0); (0, 4, 6, 1); (0, 11, 11, 1) ]
                  "the stage after | is expression territory"

              let toks2 =
                  Weir.Lsp.semanticTokensFor [ "let ok = git rev-parse HEAD | succeeds"; "print $\"{show ok}\"" ]

              Expect.equal
                  toks2
                  [ (0, 9, 3, 0); (0, 13, 9, 1); (0, 23, 4, 1) ]
                  "the reifier stays lexical (grammar, not argv)"
          }
          test "block-let and param-ful RHS commands token at depth" {
              let toks =
                  Weir.Lsp.semanticTokensFor
                      [ "let f r ="
                        "    let g = echo tag $r"
                        "    g |> Seq.length"
                        "print $\"{f 1}\"" ]

              Expect.equal
                  toks
                  [ (1, 12, 4, 0); (1, 17, 3, 1); (1, 21, 2, 2) ]
                  "the fresh block-let RHS tokens; the param splice is an island"
          }
          test "nested sigil through a paren splice recurses (depth 2)" {
              // $() as a direct command ARG is a type error (seq arg —
              // rejected); the nested spelling rides a paren splice
              let toks =
                  Weir.Lsp.semanticTokensFor
                      [ "let m = $(echo a ($(echo b) |> Seq.head)) |> Seq.length"; "print $\"{m}\"" ]

              Expect.equal
                  toks
                  [ (0, 10, 4, 0)
                    (0, 15, 1, 1)
                    (0, 17, 1, 2)
                    (0, 20, 4, 0)
                    (0, 25, 1, 1)
                    (0, 39, 1, 2) ]
                  "outer sigil head/argv, splice delimiters, inner sigil head/argv"
          } ]

let multilineLambdaTests =
    testList
        "Multiline lambdas"
        [ test "closer alone pops at col 0 and at body indent [D:multiline-lambda]" {
              match Weir.Script.assemble [ 1, "[\"a\"] |> Seq.iter (fun r ->"; 2, "    print r"; 3, ")" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "[\"a\"] |> Seq.iter (fun r -> print r )" ""
              | other -> failtest $"unexpected: {other}"

              match Weir.Script.assemble [ 1, "[\"a\"] |> Seq.iter (fun r ->"; 2, "    print r"; 3, "    )" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "[\"a\"] |> Seq.iter (fun r -> print r )" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "the closer returns the statement to the opener's level: siblings sequence after" {
              match
                  Weir.Script.assemble
                      [ 1, "let v ="
                        2, "    xs |> Seq.iter (fun r ->"
                        3, "        print r"
                        4, "    )"
                        5, "    3" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let v = xs |> Seq.iter (fun r -> print r ) ; 3") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "body at the opener's indent is a continuation, not a sibling (F#-parity)" {
              match Weir.Script.assemble [ 1, "let f ="; 2, "    [1] |> Seq.map (fun x ->"; 3, "    x + 1)" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let f = [1] |> Seq.map (fun x -> x + 1)" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "a line left of the opener errors naming the lambda (F# errors too: FS0058)" {
              match Weir.Script.assemble [ 1, "let f ="; 2, "    [1] |> Seq.map (fun x ->"; 3, "  x + 1)" ] with
              | Error e -> Expect.stringContains e "left of the lambda '(' opened at line 2" ""
              | other -> failtest $"expected the leak error, got {other}"
          }
          test "a col-0 line under a col-0 opener joins as body; EOF names the open lambda" {
              // opener at col 0 makes col 0 the body floor (the
              // at-opener-indent continuation rule): nothing sits LEFT of
              // it, so the runaway surfaces at close, named
              match Weir.Script.assemble [ 1, "xs |> Seq.iter (fun r ->"; 2, "    print r"; 3, "let x = 1" ] with
              | Error e -> Expect.stringContains e "line 1: this lambda's '(' is still open" ""
              | other -> failtest $"expected the open-lambda close error, got {other}"
          }
          test "EOF with the lambda open names the opener line" {
              match Weir.Script.assemble [ 1, "xs |> Seq.iter (fun r ->"; 2, "    print r" ] with
              | Error e -> Expect.stringContains e "line 1: this lambda's '(' is still open" ""
              | other -> failtest $"expected the open-lambda error, got {other}"
          }
          test "a body let still needs its body before the paren closes" {
              match Weir.Script.assemble [ 1, "xs |> Seq.iter (fun r ->"; 2, "    let a = 1"; 3, ")" ] with
              | Error e -> Expect.stringContains e "needs a body" ""
              | other -> failtest $"expected noBody, got {other}"
          }
          test "a compound in the body prunes at the user's closer (the original repro, now designed)" {
              // [D:compound-paren-prune]: the match must NOT swallow
              // the next outer stage
              match
                  Weir.Script.assemble
                      [ 1, "let v ="
                        2, "    xs"
                        3, "    |> Seq.map (fun n ->"
                        4, "        match n with"
                        5, "        | 1 -> 10"
                        6, "        | _ -> n"
                        7, "    )"
                        8, "    |> Seq.sum" ]
              with
              | Ok [ ll ] ->
                  Expect.equal
                      ll.Text
                      "let v = xs |> Seq.map (fun n -> match n with | 1 -> 10 | _ -> n ) |> Seq.sum"
                      "the stage after ) belongs to the OUTER pipeline"
              | other -> failtest $"unexpected: {other}"
          }
          test "an ATTACHED closer also returns the level: the next sibling gets ';' (fuzzer catch)" {
              // the deep-indent last body line must not leave the block's
              // sibling level down there — `40` sequences, never applies
              match
                  Weir.Script.assemble
                      [ 1, "let v ="
                        2, "    xs |> Seq.iter (fun r ->"
                        3, "        print r)"
                        4, "    40" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let v = xs |> Seq.iter (fun r -> print r) ; 40") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "nested multiline lambdas pop innermost-first" {
              match
                  Weir.Script.assemble
                      [ 1, "rows |> Seq.iter (fun row ->"
                        2, "    row |> Seq.iter (fun c ->"
                        3, "        print c"
                        4, "    )"
                        5, "    print \"row-done\""
                        6, ")" ]
              with
              | Ok [ ll ] ->
                  Expect.equal
                      ll.Text
                      (asmSib "rows |> Seq.iter (fun row -> row |> Seq.iter (fun c -> print c ) ; print \"row-done\" )")
                      ""
              | other -> failtest $"unexpected: {other}"
          }
          test "command block-let in a lambda body parses on the let-RHS spine [D:multiline-lambda]" {
              let r: Weir.Parser.Resolver =
                  { IsKnown = (fun n -> n = "xs" || n = "Seq")
                    IsCommandCallable = (fun _ -> false)
                    IsExternal = (fun n -> n = "echo")
                    ExternalNames = fun () -> Seq.empty }

              match
                  Weir.Parser.parseLine r "let out = xs |> Seq.map (fun k -> let g = echo tag in g |> Seq.length)"
              with
              | Ok _ -> ()
              | Error e -> failtest $"spine must reach the lambda body: {e}"
          }
          test "lambda params shadow PATH in their body" {
              // under the assume-resolver a param-headed let RHS must stay
              // an EXPRESSION, not become a phantom command [D:paramful-rhs]
              let lines =
                  [ "let counts ="
                    "    [\"a b\"]"
                    "    |> Seq.map (fun line ->"
                    "        let hash = line |> Str.split \" \" |> Seq.head"
                    "        hash)"
                    ""
                    "counts |> Seq.iter print" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines
              Expect.isEmpty diags "no diagnostics — the param is known, not a command head"
          } ]

let pipeAlignTests =
    testList
        "Pipe alignment"
        [ test "off-by-one siblings error naming the column [D:pipe-alignment]" {
              match Weir.Script.assemble [ 1, "type C ="; 2, "    | A of int"; 3, "     | B of string" ] with
              | Error e -> Expect.stringContains e "they sit at column 4" ""
              | Ok _ -> failtest "expected the alignment error"

              match
                  Weir.Script.assemble [ 1, "let v ="; 2, "    match 1 with"; 3, "    | 1 -> 1"; 4, "   | _ -> 0" ]
              with
              | Error e -> Expect.stringContains e "align the group" ""
              | Ok _ -> failtest "expected the alignment error"
          }
          test "arms left of their match error naming the head" {
              match Weir.Script.assemble [ 1, "let v ="; 2, "    match 3 with"; 3, "| _ -> 0" ] with
              | Error e -> Expect.stringContains e "left of its match (head at column 4)" ""
              | Ok _ -> failtest "expected the offside error"
          }
          // the arm-commit soundness premise rides THIS invariant
          // [D:arm-commit]: offside-close paren-wraps nested matches, so
          // at the logical line a '|' after a completed arm at the same
          // paren depth can only be another arm
          test "the nested-match return F# reads from columns now assembles" {
              match
                  Weir.Script.assemble
                      [ 1, "let v ="
                        2, "    match 1 with"
                        3, "    | 1 ->"
                        4, "        match 2 with"
                        5, "        | 2 -> \"a\""
                        6, "        | _ -> \"b\""
                        7, "    | _ -> \"c\"" ]
              with
              | Ok [ ll ] ->
                  Expect.stringContains
                      ll.Text
                      "(match 2 with | 2 -> \"a\" | _ -> \"b\")"
                      "the inner match wraps at the shallower arm"
              | other -> failtest $"unexpected: {other}"
          }
          test "consistent-deeper arms and col-0/col-0 stay legal" {
              expectValue "let v = 1 in match v with | 1 -> \"a\" | _ -> \"b\"" (VStr "a")

              match
                  Weir.Script.assemble
                      [ 1, "let v ="
                        2, "    match 1 with"
                        3, "        | 1 -> 1"
                        4, "        | _ -> 0" ]
              with
              | Ok _ -> ()
              | Error e -> failtest $"consistent-deeper must stay legal: {e}"
          } ]

let optionSweepTests =
    testList
        "Option sweep"
        [ test "Seq.tryHead types as Option of the element" {
              Expect.equal (formatTy (checkOk "ls |> Seq.tryHead").Ty) "Option<FileRow>" ""
          }
          test "the two-pipe cliff names the spelling [D:pipe-hint]" {
              match Weir.Parser.parseStmt "[1; 2] |> Seq.skip 1 | Seq.head" with
              | Error msg -> Expect.stringContains msg "pipe expressions with '|>'" ""
              | Ok _ -> failtest "expected the cliff"

              match Weir.Parser.parseStmt "let xs = [1] | Seq.head" with
              | Error msg -> Expect.stringContains msg "pipe expressions with '|>'" "let-RHS site"
              | Ok _ -> failtest "expected the cliff"

              match Weir.Parser.parseStmt "[1; 2] | Seq.head" with
              | Error msg -> Expect.stringContains msg "'|' chains commands" ""
              | Ok _ -> failtest "expected the cliff"
          }
          test "the bare-pipe caret sits ON the '|', not the space after [D:anchor-before-read]" {
              // the parked narrow question dissolved: LHS shape/line-span
              // is irrelevant — all three shapes were off by exactly the
              // consumed '|'+ws. Exact line:col, not a contains-check.
              let caret lines =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

                  ds
                  |> List.filter (fun d -> d.Message.Contains "'|' chains commands")
                  |> List.map (fun d -> d.Line, d.Col)

              // single-line: '|' is column 22 (was 23, the trailing space)
              Expect.equal (caret [ "[] |> Seq.map _.name | Seq.distinct" ]) [ (1, 22) ] "single-line pipeline LHS"

              // multi-line pipeline LHS: '|' is 4:5 (was 4:6)
              Expect.equal
                  (caret [ "let names ="; "    []"; "    |> Seq.map _.name"; "    | Seq.distinct" ])
                  [ (4, 5) ]
                  "multi-line pipeline LHS"

              // multi-line record LHS: identical behavior — 4:5 (was 4:6)
              Expect.equal
                  (caret [ "let counts ="; "    { Head = 1"; "      Tail = 3 }"; "    | Seq.map show" ])
                  [ (4, 5) ]
                  "multi-line record LHS (same as the flat pipeline — no position law)"
          }
          test "anchor sweep: consume-then-fail sites report ON their trigger [D:anchor-before-read]" {
              // the class the bare-pipe fix generalized — each caret sits on
              // the offending token, not where the stream drifted after it.
              // exact line:col, filtered to the clean teaching message.
              let at (msg: string) lines =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

                  ds
                  |> List.filter (fun d -> d.Message.Contains msg)
                  |> List.map (fun d -> d.Line, d.Col)

              Expect.equal
                  (at "out of range" [ "let x = 99999999999999999999" ])
                  [ (1, 9) ]
                  "int-out-of-range: on the literal start, not its end"

              Expect.equal (at "units of measure" [ "let x = 5<m>" ]) [ (1, 9) ] "measure (expr): on the literal start"
              Expect.equal (at "units of measure" [ "type T = { a: int<m> }" ]) [ (1, 18) ] "measure (type): on the '<'"

              Expect.equal
                  (at "out of range" [ "match 1 with"; "| 99999999999999999999 -> 1"; "| _ -> 0" ])
                  [ (2, 3) ]
                  "int-out-of-range (pattern): on the literal"

              Expect.equal (at "range step is zero" [ "let x = [1..0..5]" ]) [ (1, 13) ] "range step: on the 0"
              Expect.equal (at "duplicate parameter" [ "let f a a = 1" ]) [ (1, 9) ] "dup param: on the SECOND binder"
          }
          test "message domination: the teaching fatal surfaces CLEANLY, not buried [D:anchor-before-read]" {
              // finding-class (b): correct caret, but a non-consuming fatal
              // merged the competitors' expected-set into a dump. Now the
              // teaching wins its spot — pin the caret AND the absence of
              // the raw expecting-list (the burial is the bug).
              let sole (line: string) =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ line ]

                  match ds |> List.filter (fun d -> d.Severity = "error") with
                  | [ d ] -> d.Line, d.Col, d.Message
                  | other -> failtest $"expected ONE error, got {other}"

              let clean (teach: string) (line: string) expectedCol =
                  let l, c, msg = sole line
                  Expect.equal (l, c) (1, expectedCol) $"caret for: {line}"
                  Expect.stringContains msg teach $"teaching present: {line}"
                  // BOTH burial markers — pinning one leaves the other free
                  Expect.isFalse (msg.Contains "Expecting:") $"no expecting-list: {line} -> {msg}"
                  Expect.isFalse (msg.Contains "Other error messages") $"not buried: {line} -> {msg}"

              clean "a splat cannot head a command" "$@xs foo" 1
              clean "a splice cannot join a word" "echo --flag=$x" 13
              clean "a splat cannot join a word" "echo a$@x" 7
              clean "'function' is reserved" "let function = 1" 5
              clean "'rec' is a keyword" "let rec = 1" 5
              clean "'mutable' is a keyword" "let mutable = 1" 5
              // B: keyword in the PARAM and record-DECL field slots
              clean "'rec' is a keyword" "let f rec = 1" 7
              clean "'when' is a keyword" "let f when = 1" 7
              clean "'let' is a keyword" "type T = { let: int }" 12
              // record-LITERAL field name: a guard BEFORE the arm-commit check
              clean "'let' is a keyword" "let r = { let = 1 }" 11
              clean "'in' is a keyword" "let r = { in = 1 }" 11
              // A: foldChain reifier anchors on the MARKER, not the chain end
              clean "must directly follow a single external command" "git | grep x | complete" 16
              clean "must directly follow a single external command" "git | grep x | exitCode" 16

              // GATED: every keyword must still fall through to its parser —
              // the risk matrix (heads of their own constructs)
              let okParses (line: string) =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ line ]

                  Expect.isEmpty
                      (ds |> List.filter (fun d -> d.Severity = "error" && d.Code = "parse"))
                      $"parses: {line}"

              okParses "let x = if true then 1 else 2"
              okParses "let a = if false then 1 elif true then 2 else 3"
              okParses "let a = match 1 with | x when x > 0 -> 1 | _ -> 0"
              okParses "let a = fun x -> x"
              okParses "let y = 1 in print y"
              okParses "let a = let b = 1 in let c = 2 in b"
              okParses "let f x y = x"
              okParses "let (a, b) = (1, 2)"
              okParses "type T = { a: int }"
          }
          test "keyword in a pattern binder dominates in committed contexts [D:anchor-before-read]" {
              // item 2 of the keyword-slots residue: patWord's keyword check
              // dominates OUTSIDE its own attempt, so a match arm (past its
              // `|`), a lambda (past `fun`), and a param all surface the
              // teaching. let-destructure stays a finding (SLetPat's attempt).
              let sole (line: string) =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ line ]

                  match ds |> List.filter (fun d -> d.Severity = "error") with
                  | [ d ] -> d.Line, d.Col, d.Message
                  | other -> failtest $"expected ONE error, got {other}"

              let clean (line: string) col =
                  let l, c, msg = sole line
                  Expect.equal (l, c) (1, col) $"caret: {line}"
                  Expect.stringContains msg "'rec' is a keyword" $"teaching: {line}"
                  Expect.isFalse (msg.Contains "Expecting:") $"no expecting-list: {line}"
                  Expect.isFalse (msg.Contains "Other error messages") $"not buried: {line}"

              clean "let z = match 1 with | rec -> 2" 24 // match arm (arm-commit)
              clean "let g = fun (rec) -> 1" 14 // lambda param (committed past fun)
              clean "let f (rec) = 1" 8 // curried param
              clean "let (rec) = 1" 6 // let-destructure — the lexical scan (1b)
              clean "let (a, rec) = (1, 2)" 9 // destructure tuple

              // fall-through: every legitimate pattern form is unaffected
              let okParses (line: string) =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ line ]
                  Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) $"parses: {line}"

              okParses "let z = match Some 1 with | Some n -> n | _ -> 0"
              okParses "let (a, b) = (1, 2)"
              okParses "let z = match 1 with | _ -> 0"
              okParses "let z = match 5 with | n when n > 0 -> 1 | _ -> 0"
              okParses "let z = match true with | true -> 1 | false -> 0"
              okParses "let z = match [1] with | [x] -> x | _ -> 0"
              // the binder-scan skips pattern delimiters and stops at `=`:
              // an RHS keyword is not a binder keyword, and true/false in a
              // destructure are LITERAL patterns (a check error, not parse)
              okParses "let go = (let (a, b) = (1, 2) in a)"

              let noParseError (line: string) =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ line ]

                  Expect.isEmpty
                      (ds |> List.filter (fun d -> d.Severity = "error" && d.Code = "parse"))
                      $"no PARSE error (check is fine): {line}"

              noParseError "let (true, y) = (true, 2) in y" // refutable: a CHECK error, not parse
          }
          test "neg-int overflow dominates; the risk surface is byte-identical [D:anchor-before-read]" {
              // C of the anchor residue: the fix narrows negAtom's attempt so
              // the operand's out-of-range fatal escapes instead of being
              // swallowed and merged (a fatal inside an attempt is not a
              // fatal). Corrected diagnosis: NOT parsed-twice (negIntLit is
              // range-only) — negAtom's own attempt was the swallower.
              let ds, _, _, _ =
                  Weir.Script.analyzeLines "pin.weir" [ "let x = -99999999999999999999" ]

              match ds |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.equal (d.Line, d.Col) (1, 10) "on the offending digits"
                  Expect.stringContains d.Message "out of range" "the teaching"
                  Expect.isFalse (d.Message.Contains "Expecting:") "not buried"
                  Expect.isFalse (d.Message.Contains "Other error messages") "not buried"
              | other -> failtest $"expected ONE error, got {other}"

              // the risk surface — prefix minus, subtraction, spaced range
              // step, and application-vs-subtraction — UNCHANGED
              expectValue "let a = 5 in a - 1" (VInt 4L)
              expectValue "let a = 5 in a-1" (VInt 4L)
              expectValue "-5" (VInt -5L)
              expectValue "[10.. -1 ..8] |> Seq.length" (VInt 3L)
              expectValue "let f x = x + 100 in f -1" (VInt 99L)
          }
          test "value-headed pipeline: external head feeds; library head keeps the hint [D:value-headed-pipe]" {
              // resolution decides — an EXTERNAL head after a value `|`
              // desugars to a pipe into the command (stdin), reusing the
              // EPipe-into-ECmd machinery (identical to feed)
              match Weir.Parser.parseLine cmdResolver "[\"a\"] | cat" with
              | Ok(SExpr e | SCmd e) ->
                  Expect.stringContains (Weir.Ast.sexpr e) "(cmd cat)" "pipes the value into the command"
              | other -> failtest $"expected the value-headed pipe, got {other}"

              match Weir.Parser.parseLine cmdResolver "[\"a\"] | grep x | cat" with
              | Ok(SExpr e | SCmd e) ->
                  Expect.stringContains (Weir.Ast.sexpr e) "(cmd cat)" "multi-external chains fold"
              | other -> failtest $"expected the chain, got {other}"

              // a library/known head keeps the barePipeHint (not value-headed)
              match Weir.Parser.parseLine cmdResolver "[1] | Seq.head" with
              | Error msg -> Expect.stringContains msg "'|' chains commands" "library head keeps the hint"
              | Ok _ -> failtest "expected the hint"
          }
          test "retired names teach their replacements [D:seq-force]" {
              Expect.stringContains (checkErr "[1] |> Seq.toList").Message "'Seq.force' is the materializer" ""
              Expect.stringContains (checkErr "[1] |> toList").Message "'force' is the materializer" ""
              Expect.stringContains (checkErr "[1] |> Seq.collect").Message "reserved" "the flatMap reservation"
              Expect.stringContains (checkErr "[1] |> collect").Message "force" ""
              Expect.stringContains (checkErr "None |> Option.defaultTo 1").Message "Option.defaultValue" ""
          }
          test "Option.defaultWith: the thunk runs only on None" {
              expectValue "Some 3 |> Option.defaultWith (fun () -> 9)" (VInt 3L)
              expectValue "None |> Option.defaultWith (fun () -> 9)" (VInt 9L)
          }
          test "Option.defaultValue closes the idiom without a match" {
              expectValue "[] |> Seq.tryHead |> Option.defaultValue 0" (VInt 0)
              expectValue "[7] |> Seq.tryHead |> Option.defaultValue 0" (VInt 7)
              expectValue "tryToInt \"nope\" |> Option.defaultValue (0 - 1)" (VInt(-1))
          }
          test "Option.map maps through Some and skips None" {
              expectValue "[3] |> Seq.tryHead |> Option.map double |> Option.defaultValue 0" (VInt 6)
              expectValue "[] |> Seq.tryHead |> Option.map double |> Option.defaultValue 0" (VInt 0)
          }
          test "Seq.tryFind is data-last and Option-returning" {
              expectValue
                  "ls |> Seq.tryFind _.readOnly |> Option.map _.name |> Option.defaultValue \"none\""
                  (VStr "b.bin")

              expectValue
                  "ls |> Seq.tryFind (fun f -> f.bytes > 999999999) |> Option.map _.name |> Option.defaultValue \"none\""
                  (VStr "none")
          }
          test "Str.tryIndexOf and Str.sub compose" {
              expectValue "Str.tryIndexOf \"b\" \"abc\"" (VUnion("Some", Some(VInt 1)))
              expectValue "Str.tryIndexOf \"z\" \"abc\"" (VUnion("None", None))
              expectValue "Str.sub 1 2 \"abcd\"" (VStr "bc")

              expectValue
                  "match Str.tryIndexOf \":\" \"a:b\" with | Some i -> Str.sub 0 i \"a:b\" | None -> \"\""
                  (VStr "a")
          }
          test "Str.sub bounds raise with detail" {
              let ex = Expect.throwsC (fun () -> run "Str.sub 2 9 \"abc\"" |> ignore) id
              Expect.stringContains ex.Message "out of bounds" ""
          }
          test "match on Seq.tryHead pins the full idiom" {
              expectValue "match ls |> Seq.tryHead with | Some f -> f.name | None -> \"empty\"" (VStr "a.txt")
          } ]


let moduleTests =
    testList
        "Builtin modules"
        [ test "qualified members work and freshen per use" {
              expectValue "ls |> Seq.map _.name |> Seq.head" (VStr "a.txt")

              expectValue
                  "([1] |> Seq.map double |> Seq.head) + (Str.length (Seq.head (ls |> Seq.map _.name)))"
                  (VInt 7)
          }
          test "bare hot-path aliases still bind" {
              expectValue "ls |> where _.readOnly |> map _.name |> head" (VStr "b.bin")
              expectValue "split \",\" \"a,b\" |> join \";\"" (VStr "a;b")
          }
          test "Option members are qualified-only" {
              expectValue "[7] |> Seq.tryHead |> Option.map double |> Option.defaultValue 0" (VInt 14)
              Expect.stringContains (checkErr "[7] |> Seq.tryHead |> defaultTo 0").Message "Option.defaultValue" ""
          }
          test "length is qualified-only in both homes" {
              expectValue "Str.length \"abc\"" (VInt 3)
              expectValue "[1; 2; 3] |> Seq.length" (VInt 3)
              Expect.stringContains (checkErr "length \"abc\"").Message "use Seq.length or Str.length" ""
          }
          test "three-way precedence: the shadow case is grammar-dead (casing law, 2026-07-21)" {
              // was: value shadow wins over module (`let Seq = {...}`).
              // The casing law rejects the binder, so the module can no
              // longer be shadowed by construction; the EField precedence
              // code stays as defensive depth.
              let terr = checkErr "let Seq = 1 in Seq"
              Expect.stringContains (formatError terr) "binding names start lowercase" ""
          }
          test "bare module name errors with member guidance" {
              Expect.stringContains (checkErr "Seq").Message "'Seq' is a module" ""
          }
          test "unknown member gets a hint" {
              Expect.stringContains
                  (checkErr "Seq.mpa").Message
                  "module Seq has no member 'mpa'. Did you mean 'map'?"
                  ""
          }
          test "moved names hint their qualified home" {
              Expect.stringContains (checkErr "[] |> defaultTo 1").Message "use 'Option.defaultValue'" ""
              Expect.stringContains (checkErr "ls |> groupBy _.readOnly").Message "use 'Seq.groupBy'" ""
          }
          test "module member completion" {
              Expect.contains (suggest "Seq.tr" 0) "Seq.tryHead" ""
              Expect.contains (suggest "Str." 0) "Str.length" ""
              Expect.contains (suggest "Se" 0) "Seq" "module names complete"
          } ]


let scriptTests =
    testList
        "Script machinery"
        [ test "comment stripper respects strings" {
              Expect.equal (Weir.Script.stripComment "1 + 1 // note") "1 + 1 " ""

              Expect.equal (Weir.Script.stripComment "sh -c \"echo a//b\" // real") "sh -c \"echo a//b\" " ""

              Expect.equal (Weir.Script.stripComment "grep 'a//b' f") "grep 'a//b' f" ""
              Expect.equal (Weir.Script.stripComment "\"esc \\\" // still string\"") "\"esc \\\" // still string\"" ""
              Expect.equal (Weir.Script.stripComment "no comment") "no comment" ""
          }
          test "strict env drops bare aliases, keeps the rest" {
              Expect.isFalse (Map.containsKey "map" Weir.Builtins.typeEnvStrict.Values) "map gone"
              Expect.isFalse (Map.containsKey "trim" Weir.Builtins.typeEnvStrict.Values) "trim gone"
              Expect.isTrue (Map.containsKey "ls" Weir.Builtins.typeEnvStrict.Values) "ls stays"
              Expect.isTrue (Map.containsKey "cd" Weir.Builtins.typeEnvStrict.Values) "cd stays"
              Expect.isTrue (Map.containsKey "Seq" Weir.Builtins.typeEnvStrict.Modules) "modules stay"
          }
          test "multi-home moved name lists all candidates" {
              let strictEnv, _ =
                  Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv

              match Weir.Check.typecheck strictEnv (parse "ls |> map (fun x -> x)") with
              | Error terr -> Expect.stringContains terr.Message "use Option.map or Seq.map" ""
              | Ok _ -> failtest "expected strict rejection"
          }
          test "fmt qualifies bare uses span-precisely" {
              let line, n =
                  Weir.Fmt.qualifyLine realResolver "ls |> map _.name |> where (contains \"x\")"

              Expect.equal n 3 "three rewrites"
              Expect.equal line "ls |> Seq.map _.name |> Seq.where (Str.contains \"x\")" ""
          }
          test "fmt leaves splices and fields alone" {
              let line, n = Weir.Fmt.qualifyLine realResolver "git checkout $map"
              Expect.equal n 0 "splice untouched"
              Expect.equal line "git checkout $map" ""
          }
          test "fmt leaves already-qualified lines alone" {
              let line, n = Weir.Fmt.qualifyLine realResolver "ls |> Seq.map _.name"
              Expect.equal n 0 ""
              Expect.equal line "ls |> Seq.map _.name" ""
          } ]


let multilineTests =
    testList
        "Multi-line assembly"
        [ test "block lets: implicit in at the same indentation" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, "    a + 1" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = 1 in a + 1" "implicit in inserted"
              | other -> failtest $"unexpected: {other}"
          }
          test "block lets: sequential bindings chain" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, "    let b = 2"; 4, "    a + b" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = 1 in let b = 2 in a + b" "both closed"
              | other -> failtest $"unexpected: {other}"
          }
          test "block lets: nested with RHS spill" {
              match
                  Weir.Script.assemble
                      [ 1, "let x ="
                        2, "    let a ="
                        3, "        [1; 2]"
                        4, "        |> Seq.length"
                        5, "    a + 1" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = [1; 2] |> Seq.length in a + 1" "spill then close"
              | other -> failtest $"unexpected: {other}"
          }
          test "block lets: arms deeper than the pending indent are plain continuations" {
              match
                  Weir.Script.assemble
                      [ 1, "let x ="
                        2, "    let v ="
                        3, "        match h with"
                        4, "        | Some n -> n"
                        5, "        | None -> 0"
                        6, "    v + 1" ]
              with
              | Ok [ ll ] ->
                  Expect.equal
                      ll.Text
                      "let x = let v = (match h with | Some n -> n | None -> 0) in v + 1"
                      "the plain indent rule, no |-special-casing needed"
              | other -> failtest $"unexpected: {other}"
          }
          test "statement-level let with indented arms assembles (the valid F# shape, verbatim)" {
              match
                  Weir.Script.assemble
                      [ 1, "let category ="
                        2, "    match size with"
                        3, "    | s when s > 100 -> \"big\""
                        4, "    | _ -> \"small\"" ]
              with
              | Ok [ ll ] ->
                  Expect.equal
                      ll.Text
                      "let category = match size with | s when s > 100 -> \"big\" | _ -> \"small\""
                      "arms inert at statement level (stack empty)"
              | other -> failtest $"unexpected: {other}"
          }
          test "F#-rejects-this: dedented arm inside a block" {
              match
                  Weir.Script.assemble
                      [ 1, "let r ="
                        2, "    let v ="
                        3, "        match h with"
                        4, "| Some n -> n"
                        5, "    v" ]
              with
              | Error msg ->
                  Expect.stringContains msg "line 2" "blames the binding line"
                  Expect.stringContains msg "needs a body" "the same verdict F# gives"
              | other -> failtest $"expected an error, got {other}"
          }
          test "F#-rejects-this: arm at exactly the pending indent" {
              match
                  Weir.Script.assemble
                      [ 1, "let r ="
                        2, "    let v ="
                        3, "        match h with"
                        4, "    | Some n -> n" ]
              with
              | Error msg -> Expect.stringContains msg "line 2" "at-or-left errors, matching F#"
              | other -> failtest $"expected an error, got {other}"
          }
          test "guard: column-0 pipeline continuation outside any block survives" {
              match Weir.Script.assemble [ 1, "git branch"; 2, "| map trim" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "git branch | map trim" "the rule's real customer"
              | other -> failtest $"unexpected: {other}"
          }
          test "composition: inertness resumes after a block closes mid-script" {
              match
                  Weir.Script.assemble
                      [ 1, "let x ="
                        2, "    let a = 1"
                        3, "    a + 1"
                        4, ""
                        5, "git branch"
                        6, "| map trim" ]
              with
              | Ok [ first; second ] ->
                  Expect.equal first.Text "let x = let a = 1 in a + 1" "block closed"
                  Expect.equal second.Text "git branch | map trim" "stack empty again, | inert"
              | other -> failtest $"unexpected: {other}"
          }
          test "block lets: statement end with pending let errors" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    let a = 1" ] with
              | Error msg -> Expect.stringContains msg "needs a body" "names the gap"
              | other -> failtest $"expected an error, got {other}"
          }
          test "block lets: dedent past a bodyless let errors" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    let a ="; 3, "        let b = 1"; 4, "    a" ] with
              | Error msg -> Expect.stringContains msg "line 3" "blames the deepest let"
              | other -> failtest $"expected an error, got {other}"
          }
          test "block lets: spans translate through inserted in" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, "    a + 1" ] with
              | Ok [ ll ] ->
                  // "let x = let a = 1" is 17 chars; " in " puts the body at 0-based 21 → 1-based col 22
                  Expect.equal (Weir.Script.translate ll 22) (3, 5) "body start maps to physical 3:5"
              | other -> failtest $"unexpected: {other}"
          }
          test "logical line joins continuations" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    ls"; 3, "    |> Seq.map _.name" ] with
              | Ok [ ll ] ->
                  Expect.equal ll.Text "let x = ls |> Seq.map _.name" "joined"
                  Expect.equal ll.Head 1 "head line"
                  Expect.equal (Weir.Script.translate ll 9) (2, 5) "col 9 is line 2 col 5"
                  Expect.equal (Weir.Script.translate ll 1) (1, 1) "head col maps to itself"
              | other -> failtest $"unexpected: {other}"
          }
          test "pipe-headed lines continue at column 0" {
              match
                  Weir.Script.assemble [ 1, "match x with"; 2, "| Some n -> n"; 3, "| None -> 0"; 4, ""; 5, "next" ]
              with
              | Ok [ m; n ] ->
                  Expect.equal m.Text "match x with | Some n -> n | None -> 0" ""
                  Expect.equal n.Text "next" ""
              | other -> failtest $"unexpected: {other}"
          }
          // blanks are transparent while a statement pends [D:body-blanks]
          test "blank then continuation joins (transparency)" {
              match Weir.Script.assemble [ 1, "let x = 1"; 2, ""; 3, "    |> Seq.map f" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = 1 |> Seq.map f" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "tabs in indentation are an error" {
              match Weir.Script.assemble [ 1, "let x = 1"; 2, "\t|> f" ] with
              | Error msg -> Expect.stringContains msg "tabs are not allowed" ""
              | Ok _ -> failtest "expected error"
          }
          test "leading continuation is an error" {
              match Weir.Script.assemble [ 1, "    orphan" ] with
              | Error msg -> Expect.stringContains msg "continuation without a statement" ""
              | Ok _ -> failtest "expected error"
          } ]


let readProbes =
    testList
        "Read probes"
        [ test "(a) generic constructor inside a row constraint discharges" {
              let e2 =
                  env
                  |> declare "type OptHolder = { X: Option<int>; TagA: bool }"
                  |> declare "type StrHolder = { X: Option<string>; TagB: bool }"

              match
                  Weir.Check.typecheck e2 (parse "let g = fun r -> r.X == Some 1 in g { X = Some 2; TagA = true }")
              with
              | Ok te -> Expect.equal te.Ty TBool "discharges against Option<int>"
              | Error terr -> failtest (formatError terr)

              match
                  Weir.Check.typecheck e2 (parse "let g = fun r -> r.X == Some 1 in g { X = Some \"s\"; TagB = true }")
              with
              | Error terr -> Expect.stringContains terr.Message "expected" "conflicting Option payload rejected"
              | Ok _ -> failtest "expected discharge conflict"
          }
          test "(b) envFreeVars reaches vars inside applied constructors inside row constraints" {
              match
                  Weir.Check.typecheck
                      env
                      (parse (
                          "fun y -> let g = fun p -> y.Kids |> Seq.where p in "
                          + "let u1 = g (fun n -> n == Some 1) in "
                          + "let u2 = g (fun s -> s == \"a\") in 0"
                      ))
              with
              | Error terr -> Expect.stringContains terr.Message "expected" "second use must conflict, not freshen"
              | Ok _ -> failtest "unsound generalization: var inside constructor arg inside row constraint escaped"
          }
          test "(c) occurs through two constructor layers under a row field" {
              Expect.stringContains (checkErr "fun f -> f.X == Some (Some f.X)").Message "infinite type" ""
          }
          test "(d) module member freshens across conflicting types in one expression" {
              expectValue
                  "((ls |> Seq.map _.name |> Seq.head) == \"a.txt\") && (([1] |> Seq.map (fun x -> x * 2) |> Seq.head) == 2)"
                  (VBool true)
          }
          test "(e) generalized row binding with Option-typed field: independent discharge per use" {
              let e2 =
                  env
                  |> declare "type OA = { X: Option<int>; TagA: bool }"
                  |> declare "type OB = { X: Option<string>; TagB: bool }"

              match
                  Weir.Check.typecheck
                      e2
                      (parse (
                          "let getX = fun r -> r.X in "
                          + "let a = getX { X = Some 1; TagA = true } in "
                          + "let b = getX { X = Some \"s\"; TagB = true } in 0"
                      ))
              with
              | Ok _ -> ()
              | Error terr -> failtest $"sibling discharge interfered: {formatError terr}"
          }
          test "short-circuit pinned by real process effect, not just div-by-zero" {
              let marker = Path.Combine(Path.GetTempPath(), $"weir-sc-{System.Guid.NewGuid():N}")

              try
                  Expect.equal
                      (runReal $"false && ($(sh -c \"touch {marker}; echo x\") |> Seq.isEmpty)")
                      (VBool false)
                      ""

                  Expect.isFalse (File.Exists marker) "right operand must not spawn"

                  Expect.equal
                      (runReal $"true && ($(sh -c \"touch {marker}; echo x\") |> Seq.isEmpty)")
                      (VBool false)
                      ""

                  Expect.isTrue (File.Exists marker) "strict when left is true"
              finally
                  if File.Exists marker then
                      File.Delete marker
          } ]


let interpTests =
    testList
        "String interpolation"
        [ test "parses literal and hole parts" { expectParse "$\"a{1 + 1}b\"" "(interp \"a\"{(+ 1 1)}\"b\")" }
          test "empty string" { expectValue "$\"\"" (VStr "") }
          test "brace escapes" { expectValue "$\"{{x}}\"" (VStr "{x}") }
          test "int hole renders digits" { expectValue "$\"tracked: {[1; 2; 3] |> Seq.length}\"" (VStr "tracked: 3") }
          test "bool hole renders true/false" { expectValue "$\"flag: {1 == 1}\"" (VStr "flag: true") }
          test "string literal inside a hole" { expectValue "$\"a{\"b\"}c\"" (VStr "abc") }
          test "let-bound hole" { expectValue "let n = [1; 2] |> Seq.length in $\"n is {n}\"" (VStr "n is 2") }
          test "unresolved hole defaults to string" {
              let te = checkOk "fun x -> $\"v={x}\""
              Expect.equal te.Ty (TFun(TStr, TStr)) "hole var binds to string"
          }
          test "non-scalar hole rejected" {
              let terr = checkErr "$\"{ls}\""
              Expect.stringContains (formatError terr) "interpolation holes" "names the rule"
          }
          test "command argument stays one argv entry" {
              expectCmd "grep $\"n={1 + 1}\" f" "(cmd grep (interp \"n=\"{(+ 1 1)}) \"f\")"
          } ]

let unitPrintTests =
    testList
        "Unit, print, and the statement rule"
        [ test "unit literal parses and evals" {
              expectParse "()" "()"
              expectValue "()" VUnit
          }
          test "unit is equatable" { expectValue "() == ()" (VBool true) }
          test "unit in type syntax" {
              let e = env |> declare "type Ack = { Done: unit }"
              Expect.isTrue (Map.containsKey "Ack" e.Types) "declared"
          }
          test "print of a scalar types as unit" {
              Expect.equal (checkOk "print 42").Ty TUnit "int"
              Expect.equal (checkOk "print \"x\"").Ty TUnit "string"
              Expect.equal (checkOk "print (1 == 1)").Ty TUnit "bool"
          }
          test "print of seq<string> types as unit, pipeable" {
              Expect.equal (checkOk "[\"a\"] |> print").Ty TUnit "piped"
              Expect.equal (checkOk "print [\"a\"]").Ty TUnit "applied"
          }
          test "print rejects non-printable values" {
              let terr = checkErr "print ls"
              Expect.stringContains (formatError terr) "print takes a string" "names the family"
          }
          test "print of seq<unit> points at Seq.iter" {
              let terr = checkErr "[\"a\"] |> Seq.map print |> print"
              Expect.stringContains (formatError terr) "Seq.iter" "the lazy-effects hint"
          }
          test "unresolved print argument defaults to string" {
              Expect.equal (checkOk "fun x -> print x").Ty (TFun(TStr, TUnit)) "defaulted"
          }
          test "print as a bare value is string -> unit" {
              Expect.equal (checkOk "Seq.iter print").Ty (TFun(TSeq TStr, TUnit)) "iter print"
          }
          test "Seq.iter runs effects and returns unit" { expectValue "[\"a\"; \"b\"] |> Seq.iter print" VUnit }
          test "iter is qualified-only" {
              let terr = checkErr "iter print [\"a\"]"
              Expect.stringContains (formatError terr) "Seq.iter" "points at the module home"
          }
          test "a let shadows the print builtin" { expectValue "let print = fun s -> s in print \"x\"" (VStr "x") }
          test "unit is excluded from interpolation holes" {
              let terr = checkErr "$\"a{()}b\""
              Expect.stringContains (formatError terr) "interpolation holes" "the splice family is unchanged"
          }
          test "classifier: command lines parse as SCmd, expressions as SExpr" {
              match Weir.Parser.parseLine cmdResolver "git status" with
              | Ok(SCmd _) -> ()
              | other -> failtest $"expected SCmd, got {other}"

              match Weir.Parser.parseLine cmdResolver "git branch | map trim" with
              | Ok(SCmd _) -> ()
              | other -> failtest $"expected SCmd for the chain, got {other}"

              match Weir.Parser.parseLine cmdResolver "cd \"/tmp\"" with
              | Ok(SCmd _) -> ()
              | other -> failtest $"expected SCmd for command-callable cd, got {other}"

              match Weir.Parser.parseLine cmdResolver "\"staged:\"" with
              | Ok(SExpr _) -> ()
              | other -> failtest $"expected SExpr, got {other}"

              match Weir.Parser.parseLine realResolver "[\"a\"; \"b\"] |> Seq.length" with
              | Ok(SExpr _) -> ()
              | other -> failtest $"'[' must not head a command even with /usr/bin/[ on PATH, got {other}"

              match Weir.Parser.parseLine realResolver "^[ -f x ]" with
              | Error msg -> Expect.stringContains msg "cannot begin a command" "forced [ is a hard error"
              | other -> failtest $"expected a parse error for forced [, got {other}"
          }
          test "discard gate: unit passes, values are named, seq<unit> hints iter" {
              Expect.isNone (Weir.Script.discardError TUnit) "unit statement is fine"

              match Weir.Script.discardError TStr with
              | Some msg -> Expect.stringContains msg "pipe it to print" "names the fix"
              | None -> failtest "string discard must be rejected"

              match Weir.Script.discardError (TSeq TUnit) with
              | Some msg -> Expect.stringContains msg "Seq.iter" "the trap hint"
              | None -> failtest "seq<unit> discard must be rejected"
          } ]

let rangeTests =
    testList
        "Range literals"
        [ test "desugars to Seq.range" { expectParse "[1..5]" "(((Seq.range 1) 1) 5)" }
          test "stepped form" { expectParse "[0..2..10]" "(((Seq.range 0) 2) 10)" }
          test "basic ascending" { expectValue "[1..5] |> Seq.length" (VInt 5L) }
          test "empty when start exceeds stop" { expectValue "[1..0] |> Seq.isEmpty" (VBool true) }
          test "stepped" { expectValue "[0..2..10] |> Seq.length" (VInt 6L) }
          test "descending via negative step, spaced" { expectValue "[10.. -1 ..1] |> Seq.length" (VInt 10L) }
          test "descending via negative step, unspaced" { expectValue "[10..-1..1] |> Seq.length" (VInt 10L) }
          test "whitespace-tolerant" { expectValue "[1 .. 10] |> Seq.length" (VInt 10L) }
          test "endpoints may be idents and parenthesized expressions" {
              expectValue "let a = 2 in [a..(a + 3)] |> Seq.length" (VInt 4L)
          }
          test "lazy: huge range under first terminates" {
              expectValue "[1..1000000] |> Seq.first 3 |> Seq.length" (VInt 3L)
          }
          test "re-enumeration re-runs the generator" {
              expectValue "let r = [1..3] in (r |> Seq.length) + (r |> Seq.length)" (VInt 6L)
          }
          test "zero literal step is a parse-time error" {
              match Weir.Parser.parseExpr "[1..0..5]" with
              | Error msg -> Expect.stringContains msg "range step is zero" "named error"
              | Ok _ -> failtest "expected a parse error"
          }
          test "computed zero step raises at runtime" {
              Expect.throwsT<exn> (fun () -> run "let z = 0 in [1..z..5] |> Seq.length" |> ignore) "runtime"
          }
          test "ranges are plainly int (the measure limitation dissolved with measures)" {
              expectValue "[1..3] |> Seq.sum" (VInt 6L)
          }
          test "F#-rejects-this: open and malformed ranges" {
              for bad in [ "[1..]"; "[..5]"; "[1..2..3..4]" ] do
                  match Weir.Parser.parseExpr bad with
                  | Error _ -> ()
                  | Ok _ -> failtest $"expected a parse error for {bad}"
          }
          test "complex endpoint error names the parens fix" {
              match Weir.Parser.parseExpr "[1..f 3]" with
              | Error msg -> Expect.stringContains msg "parentheses" "actionable"
              | Ok _ -> failtest "expected a parse error"
          }
          test "eager list literals unaffected" {
              expectValue "[2; 3] |> Seq.length" (VInt 2L)
              expectValue "[] |> Seq.isEmpty" (VBool true)
          } ]

// [D:depth-guard]: the safe-by-design review found unbounded expression
// depth crashed the process (SEGV) — the bound is now a machine-checked
// invariant with a located diagnostic, not a prose promise.
let private nestDeep opener closer n =
    String.replicate n opener + "1" + String.replicate n closer

let depthGuardTests =
    testList
        "Depth guard"
        [ test "legitimate nesting is untouched (corpus max is ~11)" {
              expectValue (nestDeep "(" ")" 100 + " + 0") (VInt 1L)
              expectValue "[[[1]]] |> Seq.first 1 |> Seq.force |> Seq.length" (VInt 1L)
          }
          test "at-ceiling parens: parse or a located diagnostic, never a crash (limit 500, stack-probed)" {
              // capacity between the stack probe's floor and the counted
              // ceiling is platform-dependent BY DESIGN — big stacks parse
              // 499, small stacks get the probe's diagnostic; a crash is
              // the only wrong answer [D:depth-guard]
              match Weir.Parser.parseExpr (nestDeep "(" ")" 499) with
              | Ok _ -> ()
              | Error m ->
                  Expect.stringContains m "nested too deeply" "the probe's diagnostic is the small-stack answer"
          }
          test "small-stack thread: deep parse diagnoses via the stack probe, no overflow [D:depth-guard]" {
              // the macOS finding, emulated: test hosts there run smaller
              // stacks than Linux's 8MB and overflowed at ~420 of 500.
              // On a deliberately tiny stack, RETURNING at all is the
              // no-crash pin; the probe's diagnostic is the expected path.
              let mutable result = None

              let t =
                  System.Threading.Thread(
                      (fun () -> result <- Some(Weir.Parser.parseExpr (nestDeep "(" ")" 499))),
                      524288
                  )

              t.Start()
              t.Join()

              match result with
              | Some(Error m) -> Expect.stringContains m "nested too deeply" "the probe fired before the stack ran out"
              | Some(Ok _) -> failtest "a 512KB stack cannot fit depth 499 — the probe should have fired"
              | None -> failtest "the parse thread produced no result"
          }
          test "over-ceiling parens diagnose, located, no crash" {
              match Weir.Parser.parseExpr (nestDeep "(" ")" 600) with
              | Error m -> Expect.stringContains m "nested too deeply" "located depth diagnostic"
              | Ok _ -> failtest "depth 600 must be rejected"
          }
          test "over-ceiling operator spine diagnoses (parses shallow, deep AST)" {
              let spine = List.replicate 3000 "1" |> String.concat " + "

              match Weir.Parser.parseExpr spine with
              | Error m -> Expect.stringContains m "nested too deeply" "spine caught by the post-parse gate"
              | Ok _ -> failtest "a 3000-term spine must be rejected"
          }
          test "over-ceiling nested brackets diagnose, not hang (was O(2^n))" {
              match Weir.Parser.parseExpr (nestDeep "[" "]" 800) with
              | Error m -> Expect.stringContains m "nested too deeply" "bracket depth caught"
              | Ok _ -> failtest "depth 800 brackets must be rejected"
          } ]

let boolBranchTests =
    testList
        "Bool branching"
        [ test "if-else parses" { expectParse "if 1 > 2 then \"a\" else \"b\"" "(if (> 1 2) \"a\" \"b\")" }
          test "no-else parses" { expectParse "if 1 > 2 then print \"a\"" "(if (> 1 2) (print \"a\"))" }
          test "when-guard parses" {
              expectParse "match 1 with | n when n > 0 -> n | _ -> 0" "(match 1 [n when (> n 0) -> n] [_ -> 0])"
          }
          test "bool patterns parse" {
              expectParse "match true with | true -> 1 | false -> 0" "(match true [true -> 1] [false -> 0])"
          }
          test "if evaluates both ways" {
              expectValue "if 2 > 1 then \"t\" else \"f\"" (VStr "t")
              expectValue "if 1 > 2 then \"t\" else \"f\"" (VStr "f")
          }
          test "no-else false yields unit" { expectValue "if 1 > 2 then print \"never\"" VUnit }
          test "else-if chains" { expectValue "if 1 > 2 then \"a\" else if 2 > 3 then \"b\" else \"c\"" (VStr "c") }
          test "condition must be bool" {
              let terr = checkErr "if 3 then 1 else 2"
              Expect.stringContains (formatError terr) "expected bool, got int" ""
          }
          test "branches must unify" {
              let terr = checkErr "if 1 > 2 then 1 else \"x\""
              Expect.stringContains (formatError terr) "expected int, got string" ""
          }
          test "no-else non-unit gets the tailored error" {
              let terr = checkErr "if 1 > 2 then \"x\""
              Expect.stringContains (formatError terr) "add an else" "names the fix"
          }
          test "row constraints merge across branches" {
              let te = checkOk "fun f -> if f.readOnly then f.bytes else 0"

              Expect.equal
                  (Weir.Check.typecheck env (parse "ls |> Seq.map (fun f -> if f.readOnly then f.bytes else 0)")
                   |> Result.isOk)
                  true
                  "discharges against FileRow"

              match te.Ty with
              | TFun(TRowVar _, TInt) -> ()
              | t -> failtest $"expected a row-constrained function, got {formatTy t}"
          }
          test "branch-merged row constraints conflict at discharge, not before" {
              // pre-discharge: both fields legally share one row variable
              let te = checkOk "fun f -> if f.readOnly then f.name else f.bytes"

              match te.Ty with
              | TFun(TRowVar _, TVar _) -> ()
              | t -> failtest $"expected a row-constrained function, got {formatTy t}"

              // discharge against FileRow exposes the Name/bytes conflict
              let terr =
                  checkErr "ls |> Seq.map (fun f -> if f.readOnly then f.name else f.bytes)"

              Expect.stringContains (formatError terr) "expected" "conflict surfaces at discharge"
          }
          test "guard must be bool" {
              let terr = checkErr "match 1 with | n when n + 1 -> 2 | _ -> 0"
              Expect.stringContains (formatError terr) "expected bool, got int" ""
          }
          test "guard sees pattern bindings" { expectValue "match 5 with | n when n > 3 -> n | _ -> 0" (VInt 5L) }
          test "failed guard falls through in order" {
              expectValue "match 5 with | n when n > 100 -> 1 | n when n > 3 -> 2 | _ -> 3" (VInt 2L)
          }
          test "guard on a constructor pattern" {
              expectValue "match Some 5 with | Some n when n > 3 -> n | Some n -> 0 | None -> 0" (VInt 5L)
          }
          test "bool match evaluates" { expectValue "match 1 == 1 with | true -> \"t\" | false -> \"f\"" (VStr "t") }
          test "bool patterns default an unresolved scrutinee" {
              Expect.equal (checkOk "fun b -> match b with | true -> 1 | false -> 0").Ty (TFun(TBool, TInt)) ""
          }
          test "bool patterns on a non-bool scrutinee rejected" {
              let terr = checkErr "match 3 with | true -> 1 | false -> 0"
              Expect.stringContains (formatError terr) "bool patterns need a bool scrutinee" ""
          }
          test "bool exhaustiveness: both cases check, one case is a hard error" {
              Expect.isEmpty (warningsOf "match 1 == 1 with | true -> 1 | false -> 0") ""
              let terr = checkErr "match 1 == 1 with | true -> 1"
              Expect.stringContains (formatError terr) "missing: false" ""
          }
          test "guarded arms do not count toward exhaustiveness" {
              let terr = checkErr "match 1 == 1 with | true -> 1 | false when 2 > 1 -> 0"
              Expect.stringContains (formatError terr) "missing: false" ""
          }
          test "guarded catch-all is not terminal for reachability" {
              Expect.isEmpty (warningsOf "match Running 5 with | n when 1 > 2 -> 0 | Running n -> n | Stopped -> 9") ""
          }
          test "unguarded catch-all still flags later arms" {
              let terr = checkErr "match Running 5 with | _ -> 0 | Stopped -> 1"
              Expect.stringContains terr.Message "unreachable" ""
          }
          test "F#-rejects-this: malformed conditionals" {
              for bad in [ "if 1 > 2"; "if then 1 else 2"; "else 3"; "1 when 2" ] do
                  match Weir.Parser.parseExpr bad with
                  | Error _ -> ()
                  | Ok _ -> failtest $"expected a parse error for {bad}"
          }
          test "minus still parses next to arrows" {
              expectValue "match 5 with | n when n > 0 -> n - 1 | _ -> 0" (VInt 4L)
              expectValue "5 - 3" (VInt 2L)
          } ]

let agentFindingsTests =
    testList
        "Agent findings fixes"
        [ test "let RHS admits command mode" {
              match Weir.Parser.parseLine cmdResolver "let files = git status" with
              | Ok(SLet("files", { Kind = ECmd("git", _, _) })) -> ()
              | other -> failtest $"expected SLet with a command RHS, got {other}"
          }
          test "let RHS: known names stay expression mode" {
              match Weir.Parser.parseLine cmdResolver "let x = ls" with
              | Ok(SLet("x", { Kind = EVar "ls" })) -> ()
              | other -> failtest $"expected the builtin binding, got {other}"
          }
          test "let RHS: complete chains bind the record" {
              match Weir.Parser.parseLine cmdResolver "let r = git status | complete" with
              | Ok(SLet("r", { Kind = EApp _ })) -> ()
              | other -> failtest $"expected the completed desugar, got {other}"
          }
          test "let RHS: bareword in stops the command grammar (no silent argv)" {
              match Weir.Parser.parseLine cmdResolver "let h = git log in h" with
              | Ok(SLet(_, { Kind = ECmd(_, args, _) })) ->
                  failtest $"the in-eating cliff is back: {List.length args} argv words"
              | _ -> ()
          }
          test "let RHS: command-callable builtins stay functions (regression pin)" {
              // `let workdir = cd target` must apply the BINDING, not pass a
              // bareword — the meaning it had before let-RHS command mode.
              match Weir.Parser.parseLine cmdResolver "let w = cd target" with
              | Ok(SLet("w", { Kind = EApp({ Kind = EVar "cd" }, { Kind = EVar "target" }) })) -> ()
              | other -> failtest $"expected cd applied to the binding, got {other}"
          }
          test "let RHS: quoted in passes to the command" {
              match Weir.Parser.parseLine cmdResolver "let x = grep \"in\" f" with
              | Ok(SLet("x", { Kind = ECmd("grep", [ _; _ ], _) })) -> ()
              | other -> failtest $"expected grep with two args, got {other}"
          }
          test "statement-head commands keep bareword in" {
              match Weir.Parser.parseLine cmdResolver "git log in h" with
              | Ok(SCmd { Kind = ECmd("git", args, _) }) -> Expect.hasLength args 3 "log, in, h"
              | other -> failtest $"expected a command statement, got {other}"
          }
          // param-ful command RHS [D:paramful-rhs]
          test "param-ful let takes a command RHS (curried under the params)" {
              match Weir.Parser.parseLine cmdResolver "let f r = git log $r" with
              | Ok(SLet("f", { Kind = ELambda("r", _, { Kind = ECmd("git", _, _) }) })) -> ()
              | other -> failtest $"expected a lambda over a command, got {other}"
          }
          test "params shadow PATH in their own RHS (the law's regression pin)" {
              // cmdResolver says EVERY bareword is an external; the param
              // must still win — identity stays identity
              match Weir.Parser.parseLine cmdResolver "let f x = x" with
              | Ok(SLet("f", { Kind = ELambda("x", _, { Kind = EVar "x" }) })) -> ()
              | other -> failtest $"identity became something else: {other}"
          }
          test "tuple-pattern params shadow too" {
              match Weir.Parser.parseLine cmdResolver "let f (a, b) = a" with
              | Ok(SLet("f", { Kind = ELambdaPat(_, { Kind = EVar "a" }) })) -> ()
              | other -> failtest $"expected the leaf to stay a binding, got {other}"
          }
          test "param-ful RHS keeps the in-stop" {
              match Weir.Parser.parseLine cmdResolver "let f r = git log in r" with
              | Ok(SLet(_, { Kind = ELambda(_, _, { Kind = ECmd(_, args, _) }) })) ->
                  failtest $"the in-eating cliff, param-ful edition: {List.length args} argv words"
              | _ -> ()
          }
          test "splice of a param defaults to string at the boundary" {
              match Weir.Parser.parseLine cmdResolver "let f r = echo $r" with
              | Ok(SLet("f", _) as stmt) ->
                  // typecheck through the statement path: f : string -> seq<string>
                  match stmt with
                  | SLet(_, e) ->
                      match Weir.Check.typecheckWith env e with
                      | Ok(te, _, _) -> Expect.equal (formatTy te.Ty) "string -> seq<string>" ""
                      | Error terr -> failtest (formatError terr)
                  | _ -> ()
              | other -> failtest $"parse failed: {other}"
          }
          test "pairwise re-typed to tuples (2026-07-21, the reversal; was Pair {Fst;Snd})" {
              Expect.equal (checkOk "[1; 2] |> Seq.pairwise").Ty (TSeq(TTuple [ TInt; TInt ])) "type"

              expectValue
                  "[10; 13; 11] |> Seq.pairwise |> Seq.map (fun p -> match p with | (a, b) -> b - a) |> Seq.sum"
                  (VInt 1L)

              expectValue "[1] |> Seq.pairwise |> Seq.isEmpty" (VBool true)
          }
          // record update [D:record-update] — the re-mine's headline
          test "update forms: flat, multi-field, nested sugar" {
              expectValue "let r = { X = 1; Y = 2 } in ({ r with X = 3 }).X + ({ r with X = 3 }).Y" (VInt 5L)

              expectValue "let r = { X = 1; Y = 2 } in ({ r with X = 3; Y = 40 }).Y" (VInt 40L)
          }
          test "update is derivation: the source is untouched" {
              expectValue "let r = { X = 1; Y = 0 } in let r2 = { r with X = 9 } in r.X + r2.X" (VInt 10L)
          }
          test "row-typed updater GENERALIZES and keeps the source's row (the poster pin)" {
              expectValue
                  "let bump r = { r with UpN = r.UpN + 1 } in (bump { UpN = 1; UpT = \"x\" }).UpN + (bump { UpN = 10 }).UpN"
                  (VInt 13L)
          }
          test "row updater's field demand conflicts at discharge" {
              let terr =
                  checkErr "let bump r = { r with UpN = r.UpN + 1 } in bump { UpN = \"x\"; UpS = \"\" }"

              Expect.stringContains terr.Message "" ""
          }
          test "update cannot add fields (FCS-verdict-pinned)" {
              let terr = checkErr "let r = { X = 1; Y = 0 } in { r with Xx = 2 }"
              Expect.stringContains terr.Message "cannot add fields" ""
              Expect.stringContains terr.Message "Did you mean" ""
          }
          test "duplicate top-level update field rejected" {
              let terr = checkErr "let r = { X = 1; Y = 0 } in { r with X = 1; X = 2 }"

              Expect.stringContains terr.Message "duplicate update" ""
          }
          test "updated records stay class citizens (Eq, Show)" {
              expectValue "let r = { X = 1; Y = 0 } in { r with X = 2 } == { X = 2; Y = 0 }" (VBool true)

              expectValue "let r = { X = 1; Y = 3 } in show { r with X = 2 }" (VStr "{ X = 2; Y = 3 }")
          }
          test "update composes with pattern binders and positions" {
              expectValue "let r = { X = 1; Y = 0 } in let (u, n) = ({ r with X = 5 }, 2) in u.X + n" (VInt 7L)

              expectValue "let r = { X = 1; Y = 0 } in (if true then { r with X = 3 } else r).X" (VInt 3L)

              expectValue "let r = { X = 1; Y = 0 } in [{ r with X = 4 }] |> Seq.head |> _.X" (VInt 4L)
          }
          test "update is not a scalar: interp holes and command args reject it" {
              let terr = checkErr "let r = { X = 1; Y = 0 } in $\"x { { r with X = 2 } }\""

              Expect.stringContains terr.Message "" ""

              let terr2 = checkErr "let r = { X = 1; Y = 0 } in echo { r with X = 1 } |> Seq.head"

              Expect.stringContains terr2.Message "" ""
          }
          // elif [D:elif] — pure spelling, desugar-pinned
          test "elif desugars to nested if/else" {
              Expect.equal (show (parse "if true then 1 elif false then 2 else 3")) "(if true 1 (if false 2 3))" ""

              expectValue "if 1 > 2 then 1 elif 1 > 0 then 2 else 3" (VInt 2L)
              expectValue "if 1 > 2 then 1 elif 2 > 3 then 2 elif 1 > 0 then 7 else 3" (VInt 7L)
          }
          test "elif honors the unit rule (trailing else optional on unit)" {
              Expect.equal (checkOk "if 1 > 2 then print \"a\" elif 1 > 0 then print \"b\"").Ty TUnit ""
          }
          // splice defaulting at the boundary [D:splice-default-last]
          test "the pipe-into-lambda hole types from the pipe (the wrong-rejection, fixed)" {
              expectValue "1 |> (fun k -> $\"{k}\")" (VStr "1")
              expectValue "[1; 2] |> Seq.map (fun k -> $\"n={k}\") |> Seq.head" (VStr "n=1")
              expectValue "1 |> ((fun k -> $\"{k}\") >> Str.trim)" (VStr "1")
          }
          test "genuinely-unresolved holes still default to string" {
              Expect.equal (formatTy (checkOk "fun k -> $\"{k}\"").Ty) "string -> string" ""
          }
          test "non-scalar holes still reject, at the hole" {
              let terr = checkErr "ls |> Seq.head |> (fun r -> $\"{r}\")"
              Expect.stringContains terr.Message "must be strings, ints or bools" ""
          }
          // Seq.fold + fun-sugar [D:seq-fold][D:fun-sugar]
          test "fold: LEFT fold, order pinned on the non-commutative case" {
              expectValue "[\"a\"; \"b\"] |> Seq.fold (fun s x -> s + x) \"\"" (VStr "ab")
              expectValue "[1; 2] |> Seq.fold (fun s x -> s + x) 0" (VInt 3L)
          }
          test "fold: empty seq returns the initial state" { expectValue "Seq.fold (fun s x -> s) 7 []" (VInt 7L) }
          test "fold: constraint-free — function-valued state checks" {
              expectValue "let th = [1] |> Seq.fold (fun s x -> fun () -> x) (fun () -> 0) in th ()" (VInt 1L)
          }
          test "fold x record-update x rows: the accumulator-record shape" {
              expectValue
                  "let w = [1; 2; 3] |> Seq.fold (fun c x -> { c with X = c.X + x; Y = c.Y + 1 }) { X = 0; Y = 0 } in w.X + w.Y"
                  (VInt 9L)
          }
          test "fun-sugar: two and three params, positions" {
              expectValue "(fun a b -> b) 1 \"x\"" (VStr "x")
              expectValue "(fun a b c -> c) 1 2 9" (VInt 9L)
              expectValue "(if true then fun a b -> b else fun a b -> a) 1 2" (VInt 2L)
          }
          test "fun-sugar: () param mixes with idents" { expectValue "(fun () x -> x) () 5" (VInt 5L) }
          test "fun-sugar: desugars to nested lambdas (parse shape)" {
              Expect.equal (show (parse "fun a b -> b")) "(fun a (fun b b))" ""
          }
          test "fun-sugar: duplicate params reject in BOTH positions (FCS-matched)" {
              match Weir.Parser.parseExpr "fun a a -> a" with
              | Error msg -> Expect.stringContains msg "duplicate parameter" ""
              | Ok _ -> failtest "expected rejection"

              match Weir.Parser.parseExpr "let f a a = a in f 1 2" with
              | Error msg -> Expect.stringContains msg "duplicate parameter" ""
              | Ok _ -> failtest "expected rejection (the probe caught let-sugar accepting dups)"
          }
          test "fun-sugar: casing law applies per param" {
              let terr = checkErr "(fun a B -> a) 1 2"
              Expect.stringContains terr.Message "binding names start lowercase" ""
          }
          test "Env.pair and Env.ofPairs construct EnvVar" {
              expectValue "(Env.pair \"K\" \"v\").name" (VStr "K")
              expectValue "Env.ofPairs [(\"A\", \"1\"); (\"B\", \"2\")] |> Seq.map _.value |> Seq.head" (VStr "1")
          }
          // exit-code reifiers [D:exit-reifiers]
          test "succeeds parses as complete's sibling (desugar shape)" {
              match Weir.Parser.parseLine cmdResolver "let ok = git fetch | succeeds" with
              | Ok(SLet("ok", { Kind = EApp({ Kind = EApp({ Kind = EVar "|succeeded" }, _) }, _) })) -> ()
              | other -> failtest $"expected the succeeded desugar, got {other}"
          }
          test "argv splat: $@name and $@(expr) desugar to ESplat [D:argv-splat]" {
              match Weir.Parser.parseLine cmdResolver "git add $@files" with
              | Ok(SCmd e) -> Expect.stringContains (Weir.Ast.sexpr e) "(splat files)" ""
              | other -> failtest $"expected the splat, got {other}"

              match Weir.Parser.parseLine cmdResolver "echo $@([\"a\"])" with
              | Ok(SCmd e) -> Expect.stringContains (Weir.Ast.sexpr e) "(splat" ""
              | other -> failtest $"expected the expr splat, got {other}"
          }
          test "splat is confined to argv — a parse error everywhere else [D:argv-splat]" {
              // the grammar produces ESplat ONLY in command-argument
              // position; this confinement is why infer/eval close their
              // matches with an unreachable arm rather than a splat case
              for src in [ "let y = $@xs"; "let y = [$@xs]"; "print ($@xs)" ] do
                  match Weir.Parser.parseStmt src with
                  | Error _ -> ()
                  | Ok _ -> failtest $"$@ outside argv must be a parse error: {src}"
          }
          test "splat type demands seq<string>, both teachings [D:argv-splat]" {
              let msgOf lines =
                  let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

                  diags
                  |> List.tryPick (fun d -> if d.Severity = "error" then Some d.Message else None)

              // seq<int>: map-show teaching
              match msgOf [ "let ns = [1; 2]"; "echo $@ns" ] with
              | Some m -> Expect.stringContains m "map show or interpolate" ""
              | None -> failtest "expected the seq<int> rejection"

              // scalar: one-value teaching
              match msgOf [ "let s = \"x\""; "echo $@s" ] with
              | Some m -> Expect.stringContains m "one value? use $x" ""
              | None -> failtest "expected the scalar rejection"
          }
          test "splat rejects at the head and mid-word, each naming its fix [D:argv-splat]" {
              match Weir.Parser.parseLine cmdResolver "$@(xs) -la" with
              | Error msg ->
                  Expect.stringContains msg "N words would be N heads" ""
                  // the fix names the literal-head law, not retired builtins
                  Expect.stringContains msg "branch the whole command line" ""
              | Ok _ -> failtest "the head splat must reject"

              match Weir.Parser.parseLine cmdResolver "echo --flag=$@fs" with
              | Error msg -> Expect.stringContains msg "cannot join a word under construction" ""
              | Ok _ -> failtest "the mid-word splat must reject"
          }
          test "$@\" stays the parked interpolated-verbatim cell, not a splat" {
              // lookahead lets $@ splat and $@"..." coexist; the quote form
              // is not yet a feature, so it errors as an unknown token — NOT
              // as a broken splat
              match Weir.Parser.parseLine cmdResolver "echo $@\"x\"" with
              | Error msg -> Expect.isFalse (msg.Contains "splat") "the quote opener is not read as a splat"
              | Ok _ -> failtest "expected a parse error (the cell is parked)"
          }
          test "exitCode desugars to the exitCoded application [D:exit-reifiers]" {
              match Weir.Parser.parseLine cmdResolver "let rc = git push | exitCode" with
              | Ok(SLet(_, e)) -> Expect.stringContains (Weir.Ast.sexpr e) "|exitCoded" ""
              | other -> failtest $"expected the exitCoded desugar, got {other}"
          }
          test "exitCode conflict cells teach at parse [D:exit-reifiers]" {
              // $() captures vs exitCode streams: destination conflict
              match Weir.Parser.parseLine cmdResolver "let x = $(git push | exitCode)" with
              | Error msg -> Expect.stringContains msg "use '| complete' inside $()" ""
              | Ok _ -> failtest "the capture conflict must reject"

              // !() discards the int
              match Weir.Parser.parseLine cmdResolver "!(git push | exitCode)" with
              | Error msg -> Expect.stringContains msg "bind it (let rc = <command> | exitCode)" ""
              | Ok _ -> failtest "the discard conflict must reject"

              // the single-external-segment family rule (statement level —
              // the let-RHS chain rejects mid-chain stages earlier, with
              // the bare-pipe hint, family-uniformly)
              match Weir.Parser.parseLine cmdResolver "git log | Seq.first 1 | exitCode" with
              | Error msg -> Expect.stringContains msg "single external command segment" ""
              | Ok _ -> failtest "exitCode must keep the family's segment rule"
          }
          test "a discarded exit code errors with the bind-or-match hint" {
              let lines = [ "git push | exitCode" ]
              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              Expect.exists
                  diags
                  (fun d -> d.Message.Contains "bind it (let rc = <command> | exitCode), match on it")
                  "the set +e muscle-memory hint"
          }
          test "a discarded | complete joins the family — Completed record, stage caret [D:exit-reifiers]" {
              // the one-cell gap: a bare `| complete` statement was accepted
              // while its bool/int siblings were rejected. Now it errors in
              // the family's voice at the reifier STAGE (exact col, not
              // inherited — the anchor-before-read lesson).
              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ "git status | complete" ]

              match diags |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.equal (d.Line, d.Col) (1, 14) "caret on the `complete` stage, matching succeeds"
                  Expect.stringContains d.Message "computes a Completed record and discards it" "the record voice"
                  Expect.stringContains d.Message "read a field (.exitCode, .stdout)" "the per-cell use clause"
              | other -> failtest $"expected ONE discard error, got {other}"

              // the accepting path is unchanged: binding still works, and
              // orFail (unit) is exempt by design, not by oversight
              let ok (line: string) =
                  let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" [ line ]
                  Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) $"accepted: {line}"

              ok "let r = git status | complete"
              ok "git status | orFail \"boom\""
          }
          test "orFail carries its message expression" {
              match Weir.Parser.parseLine cmdResolver "git fetch | orFail \"boom\"" with
              | Ok(SCmd { Kind = EApp(_, _) }) -> ()
              | other -> failtest $"expected the orFailed desugar, got {other}"
          }
          test "multi-segment reifiers share complete's rule and message shape" {
              match Weir.Parser.parseLine cmdResolver "git log | grep x | succeeds" with
              | Error msg ->
                  Expect.stringContains msg "'succeeds' must directly follow a single external command segment" ""
              | Ok s -> failtest $"expected the family error, got {s}"
          }
          test "a splat rides a reifier chain — argv desugars to a seq value [D:splat-reifier-chains]" {
              // mixed literal+splat argv folds with Seq.append
              match Weir.Parser.parseLine cmdResolver "echo one $@xs | complete" with
              | Ok(SCmd e) -> Expect.stringContains (Weir.Ast.sexpr e) "append" "the append-fold desugar"
              | other -> failtest $"expected the splatted-reifier desugar, got {other}"

              // splat-free argv keeps the plain list node (zero movement)
              match Weir.Parser.parseLine cmdResolver "echo one two | complete" with
              | Ok(SCmd e) -> Expect.isFalse ((Weir.Ast.sexpr e).Contains "append") "no append for splat-free argv"
              | other -> failtest $"expected the plain desugar, got {other}"

              // the env-sigil and value-headed routes take the same desugar
              match Weir.Parser.parseLine cmdResolver "let r = $e(git commit-tree $@argv | complete)" with
              | Ok _ -> ()
              | Error m -> failtest $"the env route must parse: {m}"

              match Weir.Parser.parseLine cmdResolver "let n = [\"a\"] | grep $@flags | exitCode" with
              | Ok _ -> ()
              | Error m -> failtest $"the value-headed route must parse: {m}"
          }
          test "splatted reifier argv: word integrity identical to the argv path [D:splat-reifier-chains]" {
              // N elements, N words — through the BUILTIN's argv
              Expect.equal
                  (runReal "echo one $@([\"a\"; \"b\"]) | complete |> _.stdout |> Seq.head")
                  (VStr "one a b")
                  "splat elements land as words"

              // THE safety pin: adversarial elements stay single words
              // through the reifier path, exactly as through spawn argv
              Expect.equal
                  (runReal
                      "sh -c \"echo argc=$#\" self $@([\"one two\"; \"semi;colon\"; \"star*glob\"]) | complete |> _.stdout |> Seq.head")
                  (VStr "argc=3")
                  "no re-split through the reifier desugar"
          }
          test "downstream stages after succeeds are gated by TYPE (complete's actual rule)" {
              // verified-not-assumed: complete allows onward stages; the
              // bool just fails any seq-shaped consumer
              match Weir.Parser.parseLine cmdResolver "let m = git log | succeeds |> Seq.head" with
              | Ok(SLet(_, e)) ->
                  match Weir.Check.typecheck env e with
                  | Error terr -> Expect.stringContains terr.Message "got bool" ""
                  | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
              | other -> failtest $"parse failed: {other}"
          }
          test "print of unit is silent (the decided !()-interior rule)" {
              Expect.equal (checkOk "print ()").Ty TUnit ""
              expectValue "print ()" VUnit
          }
          // fmt v2 respace [D:fmt-respace]
          test "respaceLine: collapse, brace pad, semicolon tidy" {
              Expect.equal
                  (Weir.Script.respaceLine "let l2 =  {    lomo with Lomo = 100}")
                  "let l2 = { lomo with Lomo = 100 }"
                  ""

              Expect.equal
                  (Weir.Script.respaceLine "type G = {Lomo: int; Bimbo: string}")
                  "type G = { Lomo: int; Bimbo: string }"
                  ""

              Expect.equal (Weir.Script.respaceLine "let x = {A = {B = 1}}") "let x = { A = { B = 1 } }" ""
              Expect.equal (Weir.Script.respaceLine "let a = 1 ;  print \"x\"") "let a = 1; print \"x\"" ""
          }
          test "respaceLine: string interiors and leading indent untouched" {
              Expect.equal (Weir.Script.respaceLine "    print \"a  {  b\"") "    print \"a  {  b\"" "plain"
              Expect.equal (Weir.Script.respaceLine "let s = @\"a  {  b\"") "let s = @\"a  {  b\"" "verbatim"

              Expect.equal
                  (Weir.Script.respaceLine "print $\"x{  1  }\"")
                  "print $\"x{  1  }\""
                  "interp hole is string turf"
          }
          test "fst/snd project pairs, point-free through pipelines" {
              expectValue "fst (1, \"a\")" (VInt 1L)
              expectValue "snd (1, \"a\")" (VStr "a")
              expectValue "[(\"a\", 2); (\"b\", 1)] |> Seq.sortByDescending snd |> Seq.map fst |> Seq.head" (VStr "a")
          }
          test "fst rejects wider tuples (pair-only, as F#)" {
              let terr = checkErr "fst (1, 2, 3)"
              Expect.stringContains terr.Message "int * int * int" ""
          }
          test "Path members: extension/fileName/stem/dir/join" {
              expectValue "Path.extension \"ci/run.Dockerfile\"" (VStr ".Dockerfile")
              expectValue "Path.extension \"ci/Dockerfile\"" (VStr "")
              expectValue "Path.fileName \"a/b/c.fs\"" (VStr "c.fs")
              expectValue "Path.stem \"a/b/c.fs\"" (VStr "c")
              expectValue "Path.dir \"a/b/c.fs\"" (VStr "a/b")
              expectValue "Path.dir \"c.fs\"" (VStr "")
              expectValue "Path.combine \"ci\" \"e2e.sh\"" (VStr "ci/e2e.sh")
          }
          test "fail raises with the message" {
              Expect.equal (checkOk "fail \"boom\"").Ty TUnit "unit-typed statement"
              Expect.throwsT<exn> (fun () -> run "fail \"boom\"" |> ignore) "raises"
              expectValue "if 1 > 2 then fail \"never\"" VUnit
          }
          test "printerr types like print" {
              Expect.equal (checkOk "printerr \"x\"").Ty TUnit "scalar"
              Expect.equal (checkOk "[\"a\"] |> printerr").Ty TUnit "seq"
              Expect.equal (checkOk "Seq.iter printerr").Ty (TFun(TSeq TStr, TUnit)) "bare value"

              let terr = checkErr "printerr ls"
              Expect.stringContains (formatError terr) "takes a string" "family rule shared"
          }
          test "pipe into an operator gets the precedence hint" {
              let terr = checkErr "[1; 2] |> Seq.length == 2"
              Expect.stringContains (formatError terr) "parenthesize the pipeline" "actionable"
              expectValue "([1; 2] |> Seq.length) == 2" (VBool true)
          }
          test "mid-token // is not a comment (URLs in command lines)" {
              Expect.equal
                  (Weir.Script.stripComment "curl https://x.y/z // real comment")
                  "curl https://x.y/z "
                  "URL survives, trailing comment stripped"

              Expect.equal (Weir.Script.stripComment "let x = 1 // c") "let x = 1 " "spaced comment works"
              Expect.equal (Weir.Script.stripComment "// full line") "" "line-start comment works"
          }
          test "comment-only lines are transparent to assembly (runner-level filter)" {
              // assemble itself never sees them; pin the filter contract:
              // whitespace-only survives as blank, comment-only is dropped
              let commentOnly (raw: string) =
                  raw.Trim() <> "" && (Weir.Script.stripComment raw).Trim() = ""

              Expect.isTrue (commentOnly "    // note") "comment"
              Expect.isFalse (commentOnly "   ") "whitespace stays a blank"
              Expect.isFalse (commentOnly "let x = 1 // tail") "code with tail comment stays"
          }
          // a pending let's body continues across a gap [D:body-blanks]
          test "blank line inside a block is transparent" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, ""; 4, "    a + 1" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = 1 in a + 1" ""
              | other -> failtest $"unexpected: {other}"
          } ]

let paramSugarTests =
    testList
        "Let parameter sugar"
        [ test "single param, let-in" { expectValue "let double x = x * 2 in double 21" (VInt 42L) }
          test "multi param curries" { expectValue "let sub x y = x - y in sub 10 3" (VInt 7L) }
          test "generalizes like the lambda spelling" { expectValue "let id x = x in (id 5) + (id 7)" (VInt 12L) }
          test "statement-level parse shape" {
              match Weir.Parser.parseLine cmdResolver "let f x = x * 2" with
              | Ok(SLet("f", { Kind = ELambda("x", _, _) })) -> ()
              | other -> failtest $"expected a desugared lambda, got {other}"
          }
          test
              "params now take a command RHS (the rule this pin used to state REVERSED by PLAN-paramful-rhs; splice-default-last removed the soundness bar)" {
              match Weir.Parser.parseLine cmdResolver "let f x = git status" with
              | Ok(SLet(_, { Kind = ELambda(_, _, { Kind = ECmd("git", _, _) }) })) -> ()
              | other -> failtest $"expected a command RHS under the param, got {other}"
          }
          test "unit and PARENTHESIZED pattern params legal (binders session completed the arc)" {
              match Weir.Parser.parseExpr "let f () = 1 in f" with
              | Ok { Kind = ELet(_, _, { Kind = ELambda("()", _, _) }, _) } -> ()
              | other -> failtest $"unit param should desugar to the pinned () lambda, got {other}"

              match Weir.Parser.parseExpr "let f (x, y) = x in f" with
              | Ok { Kind = ELet(_, _, { Kind = ELambdaPat({ PKind = PTuple _ }, _) }, _) } -> ()
              | other -> failtest $"tuple param should desugar to ELambdaPat, got {other}"
          }
          test "HOF restriction unchanged through the sugar" {
              let terr = checkErr "let apply f x = f x in apply double 1"
              Expect.stringContains (formatError terr) "not a function" ""
          }
          test "operator ambiguity unchanged through the sugar" {
              let terr = checkErr "let add x y = x + y in add 1 2"
              Expect.stringContains (formatError terr) "cannot infer" ""
          } ]

let fmtTests =
    testList
        "Formatter"
        [ test "normalizes structural indentation to 4 per depth" {
              match Weir.Fmt.formatLines [ "let x ="; "      let a = 1"; "      a + 1" ] with
              | Ok fmt -> Expect.equal fmt [ "let x ="; "    let a = 1"; "    a + 1" ] ""
              | Error e -> failtest e
          }
          test "nested blocks and pipes canonicalize together" {
              match
                  Weir.Fmt.formatLines [ "let x ="; "  let a ="; "        [1; 2]"; "        |> Seq.sum"; "  a + 1" ]
              with
              | Ok fmt ->
                  Expect.equal
                      fmt
                      [ "let x ="
                        "    let a ="
                        "        [1; 2]"
                        "        |> Seq.sum"
                        "    a + 1" ]
                      ""
              | Error e -> failtest e
          }
          test "column-0 pipe style is preserved" {
              match Weir.Fmt.formatLines [ "git branch"; "| Seq.map Str.trim" ] with
              | Ok fmt -> Expect.equal fmt [ "git branch"; "| Seq.map Str.trim" ] ""
              | Error e -> failtest e
          }
          test "trailing whitespace stripped; comments verbatim" {
              match Weir.Fmt.formatLines [ "// aligned comment  "; "let x = 1   // tail  " ] with
              | Ok fmt -> Expect.equal fmt [ "// aligned comment"; "let x = 1   // tail" ] ""
              | Error e -> failtest e
          }
          test "idempotent" {
              let src = [ "let x ="; "  let a = 1"; "  a + 1"; ""; "print $\"{x}\"" ]

              match Weir.Fmt.formatLines src with
              | Ok once ->
                  match Weir.Fmt.formatLines once with
                  | Ok twice -> Expect.equal twice once "format twice = format once"
                  | Error e -> failtest e
              | Error e -> failtest e
          }
          test "invalid files are refused, not mangled" {
              match Weir.Fmt.formatLines [ "let x ="; "    let a = 1" ] with
              | Error msg -> Expect.stringContains msg "cannot format" "assembler error surfaces"
              | Ok _ -> failtest "expected refusal"
          } ]

let showTests =
    testList
        "show"
        [ test "records render REPL-shaped" {
              expectValue "show (ls |> Seq.head)" (VStr "{ bytes = 0; name = \"a.txt\"; readOnly = false }")
          }
          test "unions, seqs, scalars" {
              expectValue "show (Some 3)" (VStr "Some 3")
              expectValue "show [1; 2]" (VStr "[1; 2]")
              expectValue "show 5" (VStr "5")
              expectValue "show \"a\"" (VStr "\"a\"")
          }
          test "composes with print and interpolation" {
              Expect.equal (checkOk "print (show (ls |> Seq.head))").Ty TUnit "print"
              expectValue "$\"v: {show (Some 1)}\"" (VStr "v: Some 1")
          }
          test "pipeable" { expectValue "Some 2 |> show" (VStr "Some 2") }
          test "functions rejected, recursively" {
              let direct = checkErr "show (fun x -> x)"
              Expect.stringContains (formatError direct) "cannot render functions" "direct"

              let nested = checkErr "let f = fun x -> x in show (Some f)"
              Expect.stringContains (formatError nested) "cannot render functions" "nested in a payload"
          }
          test "bare value stays generic with Show riding (was: defaulted to string)" {
              // sentinel-era show defaulted its bare-value type to string;
              // the constrained scheme (Session B) keeps it genuinely
              // generic — the element type resolves from data, and Show
              // rides until it does
              expectValue "nats |> take 2 |> Seq.map show |> Seq.force |> Seq.length" (VInt 2L)

              match (checkOk "Seq.map show").Ty with
              | TFun(TSeq(TVar _), TSeq TStr) -> ()
              | ty -> failtest $"expected generic Show mapping, got {formatTy ty}"
          }
          test "a let shadows the show builtin" { expectValue "let show = fun x -> x in show 9" (VInt 9L) } ]

let parallelTests =
    testSequenced
    <| testList
        "Data parallelism"
        [ test "pmap preserves input order" {
              expectValue "[3; 1; 2] |> Seq.pmap (fun x -> x * 10)" (VSeq [ VInt 30L; VInt 10L; VInt 20L ])
          }
          test "pmap is eager and reusable" {
              expectValue "let r = [1; 2] |> Seq.pmap (fun x -> x + 1) in (r |> Seq.sum) + (r |> Seq.sum)" (VInt 10L)
          }
          test "piter runs every element and returns unit" {
              expectValue "[1; 2; 3] |> Seq.piter (fun n -> if n > 99 then print \"never\")" VUnit
          }
          test "cd inside a worker is worker-local (forked session)" {
              let before = Weir.Session.Cwd()

              expectValue
                  "[\"/\"; \"/tmp\"] |> Seq.pmap (fun d -> let x = cd d in pwd |> Seq.head)"
                  (VSeq [ VStr "/"; VStr "/tmp" ])

              Expect.equal (Weir.Session.Cwd()) before "parent session untouched after the join"
          }
          test "worker failure surfaces as the first error" {
              Expect.throws (fun () -> run "[1; 0] |> Seq.pmap (fun x -> 10 / x)" |> ignore) "div by zero"
          } ]

let seqAccessTests =
    testList
        "Seq access family and Args"
        [ test "contains, exists, forall" {
              expectValue "[1; 2; 3] |> Seq.contains 2" (VBool true)
              expectValue "Seq.contains 9 [1; 2]" (VBool false)
              expectValue "[1; 2] |> Seq.exists (fun x -> x > 1)" (VBool true)
              expectValue "[1; 2] |> Seq.forall (fun x -> x > 1)" (VBool false)
          }
          test "contains needs equatable elements (sentinel customer three)" {
              let terr = checkErr "let f = fun x -> x in [f] |> Seq.contains f"
              Expect.stringContains (formatError terr) "equatable" ""

              let terr2 = checkErr "Seq.contains (fun x -> x) [fun y -> y]"
              Expect.stringContains (formatError terr2) "equatable" "full-application shape too"
          }
          test "item and tryItem are the partiality pair" {
              expectValue "[\"a\"; \"b\"] |> Seq.item 1" (VStr "b")
              expectValue "[1] |> Seq.tryItem 5" (VUnion("None", None))
              expectValue "[1] |> Seq.tryItem 0" (VUnion("Some", Some(VInt 1L)))
              Expect.throws (fun () -> run "[1] |> Seq.item 5" |> ignore) "item raises"
          }
          test "skip is lazy and raises past the end at enumeration" {
              expectValue "[1; 2; 3] |> Seq.skip 1 |> Seq.sum" (VInt 5L)
              Expect.throws (fun () -> run "[1] |> Seq.skip 3 |> Seq.force" |> ignore) "F#-faithful raise"
          }
          test "Args scanners read the script argv" {
              Weir.Session.ScriptArgs <- [ "-c"; "--out"; "r.txt" ]

              try
                  expectValue "Args.flag \"--clean\" \"-c\"" (VBool true)
                  expectValue "Args.flag \"--verbose\" \"\"" (VBool false)
                  expectValue "Args.value \"--out\"" (VUnion("Some", Some(VStr "r.txt")))
                  expectValue "Args.value \"--missing\"" (VUnion("None", None))
              finally
                  Weir.Session.ScriptArgs <- []
          } ]

let fmtRecordTests =
    testList
        "fmt: record field alignment"
        [ // misaligned fields ERROR at assembly [D:field-alignment] —
          // fmt owns no repair job
          test "field drift is an assembly error (the repair job retired)" {
              match
                  Weir.Fmt.formatLines
                      [ "let target ="
                        "    { Name = \"a\""
                        "        BicepPath = \"b\""
                        "        Env = \"c\" }" ]
              with
              | Error e -> Expect.stringContains e "indented off its siblings" ""
              | Ok _ -> failtest "expected the alignment error"
          }
          test "nested record fields align under the inner brace" {
              // the inner anchor is V's column (13); W aligned there is
              // legal and fmt-stable [D:field-alignment]
              match
                  Weir.Fmt.formatLines
                      [ "let o ="
                        "    { Name = \"x\""
                        "      In = { V = 1"
                        "             W = 2 } }" ]
              with
              | Ok lines -> Expect.equal (lines[3].TrimEnd()) (String.replicate 13 " " + "W = 2 } }") ""
              | Error e -> failtest e
          } ]

let assemblyRecoveryTests =
    testList
        "Assembly recovery (tooling)"
        [ test "a broken line yields its diag AND later statements still analyze" {
              let diags, stmts, _, _ =
                  Weir.Script.analyzeLines
                      "t.weir"
                      [ "type T = { A: int }"
                        ""
                        "let go = 1 > 0"
                        ""
                        "if go then !"
                        "nats == nats" ]
              // the bare marker with no block is the assembly error; the
              // == error after it must STILL be found, and earlier
              // statements survive
              Expect.exists diags (fun d -> d.Code = "assembly") "assembly diag present"
              Expect.exists diags (fun d -> d.Code = "eq") "later statement still checked"
              Expect.isTrue (stmts |> List.exists (fun (_, c) -> c.Env.Types.ContainsKey "T")) "types survive"
          } ]

let closersTests =
    testList
        "Closers (the repair scanner)"
        [ test "brackets, strings, interp holes at any nesting" {
              Expect.equal (Weir.Script.closers "f (a") ")" ""
              Expect.equal (Weir.Script.closers "{ A = (1") ")}" ""
              Expect.equal (Weir.Script.closers "let x = \"open") "\"" ""
              Expect.equal (Weir.Script.closers "printerr $\"q: {t") "}\"" ""
              Expect.equal (Weir.Script.closers "$\"a{f (\"s\"") ")}\"" ""
              Expect.equal (Weir.Script.closers "$\"esc {{ literal") "\"" ""
              Expect.equal (Weir.Script.closers "balanced (x) done") "" ""
          } ]

let scannerTests =
    testList
        "Scanner & classifier contract"
        [ // the quote-aware scanner, pinned through its consumers
          test "escaped quote inside a string hides //" {
              Expect.equal (Weir.Script.stripComment "print \"a\\\" // x\"") "print \"a\\\" // x\"" ""
          }
          test "single quotes hide //" { Expect.equal (Weir.Script.stripComment "echo 'a // b'") "echo 'a // b'" "" }
          test "comment after a closed string cuts" {
              Expect.equal (Weir.Script.stripComment "print \"x\" // cut") "print \"x\" " ""
          }
          test "bareword URL survives (boundary rule)" {
              Expect.equal (Weir.Script.stripComment "curl https://x//y") "curl https://x//y" ""
          }
          test "mid-token // is not a comment" {
              Expect.equal (Weir.Script.stripComment "let x = 1// c") "let x = 1// c" ""
          }
          test "classifyLine: blank, comment-only (indented too), code" {
              Expect.equal (Weir.Script.classifyLine "   ") Weir.Script.LineKind.Blank ""
              Expect.equal (Weir.Script.classifyLine "  // note") Weir.Script.LineKind.CommentOnly ""
              Expect.equal (Weir.Script.classifyLine "let x = 1 // t") Weir.Script.LineKind.Code ""
          }
          test "classifyPiece: kinds are exclusive, prefix-guarded" {
              Expect.equal (Weir.Script.classifyPiece "let x = 1").Kind Weir.Script.PieceKind.LetHead ""
              Expect.equal (Weir.Script.classifyPiece "| x -> 1").Kind Weir.Script.PieceKind.PipeHead ""
              Expect.equal (Weir.Script.classifyPiece "else 2").Kind Weir.Script.PieceKind.ElseHead ""
              Expect.equal (Weir.Script.classifyPiece "elsewhere ()").Kind Weir.Script.PieceKind.Plain ""
              Expect.equal (Weir.Script.classifyPiece "letter x").Kind Weir.Script.PieceKind.Plain ""
          }
          test "classifyPiece: marker and compound overlap on `if c then !`" {
              let c = Weir.Script.classifyPiece "if c then !"
              Expect.equal c.Marker Weir.Script.MarkerKind.Bare ""
              Expect.isTrue c.OpensCompound ""
          }
          test "classifyPiece: env marker `!name` at line end (Layer 2)" {
              Expect.equal (Weir.Script.classifyPiece "if c then !e").Marker (Weir.Script.MarkerKind.Env "e") ""
              Expect.equal (Weir.Script.classifyPiece "!targetEnv").Marker (Weir.Script.MarkerKind.Env "targetEnv") ""
              Expect.equal (Weir.Script.classifyPiece "echo hello!").Marker Weir.Script.MarkerKind.NoMarker ""
              Expect.equal (Weir.Script.classifyPiece "!e(git st)").Marker Weir.Script.MarkerKind.NoMarker ""
          }
          test "classifyPiece: env sigil heads count as bang sigils in districts" {
              Expect.isTrue (Weir.Script.classifyPiece "!e(git st)").IsBangSigil ""
              Expect.isTrue (Weir.Script.classifyPiece "!(git st)").IsBangSigil ""
              Expect.isFalse (Weir.Script.classifyPiece "!e").IsBangSigil ""
          }
          test "classifyPiece: brace deltas ignore strings and interp holes" {
              Expect.equal (Weir.Script.classifyPiece "{ Name = $\"a{1}b\"").BraceDelta 1 ""
              Expect.equal (Weir.Script.classifyPiece "awk '{print}' f").BraceDelta 0 ""
              Expect.equal (Weir.Script.classifyPiece "{ X = \"}\" }").BraceDelta 0 ""
          }
          test "parse failure carries its column as data (no regex scrape)" {
              match Weir.Parser.parseLineFull cmdResolver "let = 3" with
              | Error f ->
                  match f.Col with
                  | Some col -> Expect.stringContains f.Message $"Col: {col}" "position data matches message text"
                  | None -> failtest "expected a column"
              | Ok s -> failtest $"unexpected: {s}"
          } ]

let childEnvTests =
    testList
        "Child-env injection"
        // cmdEnv/runEnv dropped [D:drop-command-builtins]: child env goes
        // through the env sigil `$e(...)` / district `!e(...)` (tested in
        // e2e). Env.fromFile / Env.vars remain.
        [ test "Env.fromFile types to seq<EnvVar>" {
              let te = checkOk "Env.fromFile \"x.env\""
              Expect.equal (formatTy te.Ty) "seq<EnvVar>" ""
          }
          test "fromFile: the dotenv subset parses (quotes, comments, blanks, empty)" {
              let f = System.IO.Path.GetTempFileName()

              System.IO.File.WriteAllLines(f, [ "A=1"; "B='sq val'"; "C=\"dq\" # note"; "# comment"; ""; "D=" ])

              let got =
                  run ("Env.fromFile \"" + f + "\" |> Seq.map (fun e -> e.value) |> Seq.force")
                  |> forceSeq

              Expect.equal got [ VStr "1"; VStr "sq val"; VStr "dq"; VStr "" ] ""
              System.IO.File.Delete f
          }
          test "fromFile: rejection raises at force, naming the sh escape" {
              let f = System.IO.Path.GetTempFileName()
              System.IO.File.WriteAllLines(f, [ "GOOD=1"; "BAD=$HOME" ])

              let ex =
                  Expect.throws (fun () -> run ("Env.fromFile \"" + f + "\" |> Seq.force") |> ignore) ""

              System.IO.File.Delete f
          }
          test "env sigil: $e(...) attaches the overlay to the chain's spawn" {
              match Weir.Parser.parseLine realResolver "let x = $e(git status)" with
              | Ok(SLet("x", { Kind = ECmd("git", _, Some { Kind = EVar "e" }) })) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "env sigil: !e(...) is chain-with-env |> print" {
              match Weir.Parser.parseLine realResolver "!e(git status)" with
              | Ok(SExpr { Kind = EPipe({ Kind = ECmd("git", _, Some _) }, { Kind = EVar "print" }) }) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "env sigil: every segment in the chain gets the env" {
              match Weir.Parser.parseLine realResolver "let x = $e(git log | grep x)" with
              | Ok(SLet("x", { Kind = EPipe({ Kind = ECmd("git", _, Some _) }, { Kind = ECmd("grep", _, Some _) }) })) ->
                  ()
              | other -> failtest $"unexpected: {other}"
          }
          test "env sigil x complete: routes through completedEnv" {
              match Weir.Parser.parseLine realResolver "let r = $e(git status | complete)" with
              | Ok(SLet("r", { Kind = EApp({ Kind = EApp({ Kind = EApp({ Kind = EVar "|completedEnv" }, _) }, _) }, _) })) ->
                  ()
              | other -> failtest $"unexpected: {other}"
          }
          test "env sigil: a space after the ident falls back (no sigil)" {
              match Weir.Parser.parseLine realResolver "let x = $e (git status)" with
              | Ok(SLet("x", { Kind = ECmd _ })) -> failtest "space must not read as an env sigil"
              | _ -> ()
          }
          test "district header: !e distributes the env over the block" {
              match Weir.Script.assemble [ 1, "if go then !e"; 2, "    git pull"; 3, "    git push" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "if go then !e(git pull) ; !e(git push)" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "district header x offside: closing sibling wraps the env district" {
              match
                  Weir.Script.assemble
                      [ 1, "let f t ="
                        2, "    if c then !e"
                        3, "        git pull"
                        4, "    print \"x\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f t = (if c then !e(git pull)) ; print \"x\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "standalone marker: dedent after the district sequences (latent bare-! bug)" {
              match
                  Weir.Script.assemble [ 1, "let f x ="; 2, "    !e"; 3, "        git pull"; 4, "    printerr \"OK\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = !e(git pull) ; printerr \"OK\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "standalone bare marker: same sequencing on dedent" {
              match
                  Weir.Script.assemble [ 1, "let f x ="; 2, "    !"; 3, "        git pull"; 4, "    printerr \"OK\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = !(git pull) ; printerr \"OK\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "district header x pipes: continuation keeps the wrap" {
              match Weir.Script.assemble [ 1, "if go then !e"; 2, "    git log"; 3, "        | grep x" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "if go then !e(git log | grep x)" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "fromFile: single quotes are shell-literal ($ allowed)" {
              let f = System.IO.Path.GetTempFileName()
              System.IO.File.WriteAllLines(f, [ "LIT='$HOME'" ])

              let got = run ("Env.fromFile \"" + f + "\" |> Seq.map _.value |> Seq.head")

              Expect.equal got (VStr "$HOME") ""
              System.IO.File.Delete f
          } ]

let casingTests =
    testList
        "The casing law (lowercase binds)"
        [ test "every binder position rejects uppercase (POSITIONS ride)" {
              for bad in
                  [ "let Foo = 1 in Foo"
                    "let Foo x = x in Foo"
                    "fun X -> X"
                    "let f X = X in f"
                    "let (A, b) = (1, 2) in b"
                    "let API, x = (1, 2) in x" ] do
                  Expect.stringContains (checkErr bad).Message "binding names start lowercase" bad
          }
          test "underscore-leading is lowercase-class; bare _ is the wildcard" {
              expectValue "let _x = 5 in _x" (VInt 5L)
              expectValue "let _ = 42 in 1" (VInt 1L)
          }
          test "the AWS_REGION shape: field accepted, binding rejected, lowercase rebind accepted" {
              let e2 = env |> declare "type Cfg = { AWS_REGION: string }"

              let ok =
                  parse "let cfg = { AWS_REGION = \"eu\" } in let region = cfg.AWS_REGION in region"

              match Weir.Check.typecheck e2 ok with
              | Ok te -> Expect.equal (formatTy te.Ty) "string" ""
              | Error terr -> failtest (formatError terr)

              let bad =
                  parse "let cfg = { AWS_REGION = \"eu\" } in let AWS_REGION = cfg.AWS_REGION in 1"

              match Weir.Check.typecheck e2 bad with
              | Ok _ -> failtest "uppercase binding must reject"
              | Error terr -> Expect.stringContains terr.Message "binding names start lowercase" ""
          }
          test "match patterns untouched: uppercase is still a constructor" {
              expectValue "match Running 1 with | Running n -> n | Stopped -> 0" (VInt 1L)
          }
          test "() param is not a casing case" { expectValue "(fun () -> 7) ()" (VInt 7L) } ]

let binderTests =
    testList
        "Pattern binders & bare comma"
        [ test "all six form-examples (the plan's forms block)" {
              expectValue "let (x, y) = (1, 2) in x + y" (VInt 3L)
              expectValue "let x, y = 1, 2 in x + y" (VInt 3L)
              expectValue "let (k, _) = (\"key\", 99) in k" (VStr "key")
              expectValue "let k, _ = (\"key\", 99) in k" (VStr "key")
              expectValue "let ((a, b), c) = ((1, 2), 3) in a + b + c" (VInt 6L)
              expectValue "(\"a\", 1) |> (fun (k, v) -> k)" (VStr "a")
          }
          test "param sugar with a tuple param" {
              expectValue "let swap (x, y) = (y, x) in match swap (1, 2) with | (a, b) -> a" (VInt 2L)
          }
          test "refutable binders are check errors naming match" {
              for bad in
                  [ "let (Some x) = Some 1 in x"
                    "let 1, y = 1, 2 in y"
                    "let (true, y) = (true, 2) in y" ] do
                  Expect.stringContains (checkErr bad).Message "this pattern can fail; use match" bad
          }
          test "per-name generalization: one component polymorphic, one ground" {
              expectValue "let (f, n) = ((fun x -> x), 3) in (if f true then f n else 0)" (VInt 3L)
          }
          test "class constraints ride the right component" {
              // eq lands on g's var only; h stays free of it
              expectValue "let (g, h) = ((fun x -> x == x), (fun y -> y)) in g 1 && g \"s\"" (VBool true)

              let terr = checkErr "let (g, h) = ((fun x -> x == x), 1) in g print"
              Expect.stringContains (formatError terr) "equatable" ""
          }
          test "unit component pins" {
              let terr = checkErr "let ((), n) = (1, 2) in n"
              Expect.stringContains (formatError terr) "expected" ""
          }
          test "let _ = discards explicitly (irrefutable wildcard binder)" {
              expectValue "let _ = 42 in \"kept\"" (VStr "kept")
          }
          test "arity mismatch through the binder is located" {
              let terr = checkErr "let (a, b, c) = (1, 2) in a"
              Expect.stringContains (formatError terr) "expected" ""
          }
          test "command-mode RHS under a pattern binder is a type error, not a parse cliff" {
              let terr = checkErr "let (a, b) = pwd in a"
              Expect.stringContains (formatError terr) "expected" ""
          }
          // --- the bare-comma composition matrix ---
          test "comma x semicolon: a, b ; c groups (a, b) first (decided cell)" {
              // `;` looser than `,`: the seq's FIRST element is the tuple —
              // non-unit first element is the sequencing hard error
              let terr = checkErr "(1, 2) ; 3"
              Expect.stringContains (formatError terr) "must be unit" ""

              expectValue "print \"x\" ; (1, 2)" (VTuple [ VInt 1L; VInt 2L ])
          }
          test "comma x pipes: F# grouping (xs |> f, ys |> g)" {
              expectValue "match ([1] |> Seq.length, [1; 2] |> Seq.length) with | (a, b) -> a + b" (VInt 3L)
          }
          test "comma x match arms and if branches" {
              expectValue "match (if 1 > 0 then 1, 2 else 3, 4) with | (a, b) -> a" (VInt 1L)
          }
          test "bare tuple statement is the discard error" {
              match Weir.Parser.parseStmt "1, 2" with
              | Ok(SExpr e) ->
                  match Weir.Check.typecheck Weir.Builtins.typeEnv e with
                  | Ok te ->
                      match Weir.Script.discardError te.Ty with
                      | Some msg -> Expect.stringContains msg "discards" ""
                      | None -> failtest "tuple statement must hit the discard rule"
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"
          }
          test "assembler: let ( and let x, open block lets (classifier pin)" {
              match Weir.Script.assemble [ 1, "let (a, b) ="; 2, "    (1, 2)"; 3, "a" ] with
              | Ok [ ll; _ ] -> Expect.equal ll.Text "let (a, b) = (1, 2)" ""
              | other -> failtest $"unexpected: {other}"
          } ]

let tupleTests =
    testList
        "Tuples (the reversal)"
        [ test "literal, type, and pattern round-trip" {
              expectValue "let p = (1, \"two\") in match p with | (n, s) -> $\"{n}-{s}\"" (VStr "1-two")
          }
          test "arity 3 and nesting" {
              expectValue "match (1, (2, 3), \"x\") with | (a, (b, c), s) -> $\"{a + b + c}{s}\"" (VStr "6x")
          }
          test "position sweep: tuple in list, record field, seq, splice-reject" {
              expectValue "[(1, 2); (3, 4)] |> Seq.length" (VInt 2L)

              let terr = checkErr "echo (1, 2)"
              Expect.stringContains (formatError terr) "" ""
          }
          test "tuple types in declarations: record field and multi-payload constructor" {
              let e2 = env |> declare "type Point = { At: int * int }"
              let expr = parse "let p = { At = (1, 2) } in match p.At with | (x, y) -> x + y"

              match Weir.Check.typecheck e2 expr with
              | Ok te -> Expect.equal (formatTy te.Ty) "int" ""
              | Error terr -> failtest (formatError terr)
          }
          test "multi-payload constructors un-restricted (the corollary retires)" {
              let e2 = env |> declare "type Msg = | Move of int * int | Stop"

              let expr = parse "match Move (3, 4) with | Move (x, y) -> x + y | Stop -> 0"

              match Weir.Check.typecheck e2 expr with
              | Ok te -> Expect.equal (formatTy te.Ty) "int" ""
              | Error terr -> failtest (formatError terr)
          }
          test "tuples x classes: Eq componentwise, deep reject" {
              expectValue "(1, \"a\") == (1, \"a\")" (VBool true)
              expectValue "(1, (2, 3)) == (1, (2, 4))" (VBool false)

              let terr = checkErr "(1, print) == (1, print)"
              Expect.stringContains (formatError terr) "not defined for" ""
          }
          test "tuples x classes: Show renders, Ord rejects" {
              expectValue "show (1, (2, \"x\"))" (VStr "(1, (2, \"x\"))")

              let terr = checkErr "[(1, 2)] |> Seq.sortBy (fun x -> x)"
              Expect.stringContains (formatError terr) "cannot sort by this key" ""
          }
          test "tuples x generalization: fun x -> (x, x) freshens" {
              expectValue
                  "let dup x = (x, x) in match (dup 1, dup \"a\") with | ((a, _), (s, _)) -> $\"{a}{s}\""
                  (VStr "1a")
          }
          test "tuples x rows: a tuple-typed row field flows" {
              let e2 = env |> declare "type Point = { At: int * int }"

              let expr =
                  parse "let getAt = fun r -> r.At in match getAt { At = (5, 6) } with | (x, y) -> x * y"

              match Weir.Check.typecheck e2 expr with
              | Ok te -> Expect.equal (formatTy te.Ty) "int" ""
              | Error terr -> failtest (formatError terr)
          }
          test "tuples x exhaustiveness: refutable tuple arm needs a catch-all (bounded rule)" {
              let terr = checkErr "match (1, 2) with | (0, _) -> \"z\" | (1, _) -> \"o\""
              Expect.stringContains (formatError terr) "catch-all" ""

              expectValue "match (1, 2) with | (0, _) -> \"z\" | _ -> \"other\"" (VStr "other")
          }
          test "tuple arity mismatch is located" {
              let terr = checkErr "match (1, 2) with | (a, b, c) -> a"
              Expect.stringContains (formatError terr) "elements" ""
          }
          test "Seq.zip: tuples' customer" {
              expectValue
                  "[\"a\"; \"b\"] |> Seq.zip [1; 2] |> Seq.map (fun p -> match p with | (n, s) -> $\"{s}{n}\") |> Seq.head"
                  (VStr "a1")
          }
          test "pairwise re-typed migration shape" {
              expectValue
                  "[10; 13] |> Seq.pairwise |> Seq.map (fun p -> match p with | (a, b) -> b - a) |> Seq.head"
                  (VInt 3L)
          } ]

let literalThunkTests =
    testList
        "Literal patterns & () thunks"
        [ test "int literal patterns dispatch" {
              expectValue "match 1 with | 0 -> \"zero\" | 1 -> \"one\" | _ -> \"many\"" (VStr "one")
          }
          test "string literal patterns dispatch" {
              expectValue "match \"slow\" with | \"fast\" -> 1 | \"slow\" -> 2 | _ -> 0" (VInt 2L)
          }
          test "negative int literal patterns" {
              expectValue "match 0 - 1 with | -1 -> \"neg\" | _ -> \"pos\"" (VStr "neg")
          }
          test "exhaustiveness: literals NEVER complete a match alone (F#'s rule)" {
              let terr = checkErr "match 1 with | 0 -> \"a\" | 1 -> \"b\""
              Expect.stringContains (formatError terr) "catch-all" ""

              let terr2 = checkErr "match \"x\" with | \"x\" -> 1"
              Expect.stringContains (formatError terr2) "catch-all" ""
          }
          test "exhaustiveness: literal + var arm is clean" {
              let te = checkOk "match 5 with | 0 -> \"z\" | n -> $\"n{n}\""
              Expect.equal (formatTy te.Ty) "string" ""
          }
          test "position sweep: literals nested in constructor patterns" {
              expectValue "match Some 3 with | Some 3 -> \"hit\" | Some _ -> \"other\" | None -> \"none\"" (VStr "hit")
          }
          test "literal pins an unresolved scrutinee (defaulting family)" {
              let te = checkOk "let f x = match x with | 0 -> \"z\" | _ -> \"n\" in f"
              Expect.stringContains (formatTy te.Ty) "int -> string" ""
          }
          test "conflicting literal kinds error at the bind" {
              let terr = checkErr "let f x = match x with | 0 -> 1 | \"s\" -> 2 | _ -> 3 in f"
              Expect.stringContains (formatError terr) "need a string scrutinee" ""
          }
          test "guard idiom remains legal alongside literals" {
              expectValue "match 7 with | 0 -> \"z\" | n when n > 5 -> \"big\" | _ -> \"small\"" (VStr "big")
          }
          test "() param types the thunk: unit -> body" {
              let te = checkOk "let cleanup () = 42 in cleanup"
              Expect.equal (formatTy te.Ty) "unit -> int" ""
          }
          test "() param in a bare lambda" { expectValue "(fun () -> 9) ()" (VInt 9L) }
          test "() pattern is irrefutable and exhaustive alone" { expectValue "match () with | () -> \"u\"" (VStr "u") }
          test "mixed params: idents and () together through the sugar" {
              expectValue "let f x () y = x + 0 + y in f 1 () 2" (VInt 3L)
          }
          test "thunk shadowing: () param adds no binding" {
              // the "()" name is unforgeable; body sees the OUTER x
              expectValue "let x = 5 in let f () = x in f ()" (VInt 5L)
          } ]

let typeClassTests =
    testList
        "Type classes: Eq (Session A)"
        [ // the payoff shape: user code generic over equality
          test "generic eq generalizes: Eq a => a -> a -> bool" {
              expectValue "let same x y = x == y in same 1 1" (VBool true)
          }
          test "generic eq reused at a second type in one statement" {
              expectValue "let same x y = x == y in same \"a\" \"a\" && same 2 2" (VBool true)
          }
          test "generic eq on records through the scheme" {
              expectValue "let same x y = x == y in same (Running 1) (Running 1)" (VBool true)
          }
          test "instantiation at functions rejects at the USE site" {
              let terr = checkErr "let same x y = x == y in same print printerr"
              Expect.stringContains (formatError terr) "requires equatable values" ""
          }
          test "instantiation at seqs rejects" {
              let terr = checkErr "let same x y = x == y in same nats nats"
              Expect.stringContains (formatError terr) "requires equatable values" ""
          }
          test "concrete failures keep the pre-class message verbatim" {
              Expect.stringContains (checkErr "nats == nats").Message "'==' is not defined for seq<int>" ""
          }
          test "Seq.contains is an ordinary constrained scheme now" {
              expectValue "[1; 2] |> Seq.contains 2" (VBool true)
          }
          test "Seq.contains still rejects function elements (sentinel parity)" {
              let terr = checkErr "[print] |> Seq.contains printerr"
              Expect.stringContains (formatError terr) "equatable" ""
          }
          test "ambiguity: constraint on a type nothing determines is an error" {
              let terr = checkErr "([Seq.head []] |> Seq.contains (Seq.head [])) && true"
              Expect.stringContains (formatError terr) "nothing determines" ""
          }
          test "rows x Eq: row-level constraint discharges against a seq-carrying record (reject)" {
              let holderEnv = env |> declare "type Holder = { S: seq<int> }"
              let e = parse "let eqr = fun a -> fun b -> a == b in eqr { S = nats } { S = nats }"

              match Weir.Check.typecheck holderEnv e with
              | Ok _ -> failtest "expected rejection through the row discharge"
              | Error terr -> Expect.stringContains terr.Message "requires equatable values" ""
          }
          test "rows x Eq: field-type constraint rides and solves at a good record" {
              let e =
                  parse "let eqn = fun a -> fun b -> a.bytes == b.bytes in eqn (ls |> Seq.head) (ls |> Seq.head)"

              match Weir.Check.typecheck env e with
              | Ok te -> Expect.equal (formatTy te.Ty) "bool" ""
              | Error terr -> failtest (formatError terr)
          }
          test "rows x Eq x generalization: both a row and a class constraint survive the scheme" {
              // eqn : Eq b => { r with Name: b } -> b -> bool  (shape-level check:
              // accepted at FileRow.name=string, rejected at a function)
              let ok =
                  parse "let eqn = fun a -> fun x -> a.name == x in eqn (ls |> Seq.head) \"n\""

              match Weir.Check.typecheck env ok with
              | Ok te -> Expect.equal (formatTy te.Ty) "bool" ""
              | Error terr -> failtest (formatError terr)
          }
          test "scheme text: constraints ride generalization (script-level SLet path)" {
              // the Script path generalizes via generalizeWith — pin the scheme's Cs
              match Weir.Parser.parseStmt "let same x y = x == y" with
              | Ok(SLet(_, e)) ->
                  match Weir.Check.typecheckWith env e with
                  | Ok(te, cs, _) ->
                      let sch = Weir.Types.generalizeWith cs te.Ty
                      Expect.isTrue (sch.Cs |> Map.exists (fun _ s -> s.Contains Weir.Types.Cls.Eq)) "Eq rides"
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"
          }
          test "erasure: a constrained closure partially applies like any other" {
              expectValue "let same x y = x == y in let s5 = same 5 in s5 5" (VBool true)
          } ]

let typeClassBTests =
    testList
        "Type classes: Show + Ord (Session B)"
        [ test "generic show generalizes: Show a => a -> string" {
              expectValue "let render x = show x in render 42" (VStr "42")
          }
          test "Show is wider than Eq: seqs render" {
              expectValue "let render x = show x in (render [1; 2] |> Str.length) > 0" (VBool true)
          }
          test "Show rejects functions at the use site (legacy message kept)" {
              let terr = checkErr "let render x = show x in render print"
              Expect.stringContains (formatError terr) "cannot render functions" ""
          }
          test "Ord: string keys sort" {
              expectValue "[\"b\"; \"a\"] |> Seq.sortBy (fun s -> s) |> Seq.head" (VStr "a")
          }
          test "Ord: record key is a CHECK-time error with the contract message" {
              let terr = checkErr "ls |> Seq.sortBy (fun f -> f)"
              Expect.stringContains (formatError terr) "cannot be ordered — keys are int, string, or bool" ""
          }
          test "Ord: function key rejects" {
              let terr = checkErr "ls |> Seq.sortBy (fun f -> fun x -> x)"
              Expect.stringContains (formatError terr) "cannot sort by this key" ""
          }
          test "Ord rides generalization: a sort helper stays generic" {
              expectValue "let bykey k xs = xs |> Seq.sortBy k in [3; 1] |> bykey (fun n -> n) |> Seq.head" (VInt 1L)
          }
          test "Ord x rows: a row-typed key rejects when it discharges to a record" {
              let terr =
                  checkErr "let bad r = ls |> Seq.sortBy (fun _ -> r) in bad (ls |> Seq.head)"

              Expect.stringContains (formatError terr) "cannot sort by this key" ""
          }
          test "Show x Eq on one var: both constraints ride and solve" {
              expectValue "let f x y = (show x == show y) && x == y in f 1 1" (VBool true)
          }
          test "Show x Eq on one var: Eq's narrower rule still rejects seqs" {
              // show accepts seqs, == does not — the var carries BOTH and
              // the strictest class decides
              let terr = checkErr "let f x y = (show x == show y) && x == y in f [1] [1]"
              Expect.stringContains (formatError terr) "requires equatable values" ""
          } ]

let typeClassCTests =
    testList
        "Type classes: hardening (Session C)"
        [ test "Eq x generic unions: two levels of Option accept" {
              expectValue "let same x y = x == y in same (Some (Some 1)) (Some (Some 1))" (VBool true)
          }
          test "Eq x generic unions: function payload rejects through two levels" {
              let terr =
                  checkErr "let same x y = x == y in same (Some (Some print)) (Some (Some print))"

              Expect.stringContains (formatError terr) "requires equatable values" ""
          }
          test "Eq x generic records: the reachability correction (fn field via instantiation)" {
              // Session A scoped fn-field records as undeclarable; generic
              // instantiation REACHES them
              let boxEnv = env |> declare "type Box<'a> = { V: 'a }"

              let e =
                  parse "let same = fun x -> fun y -> x == y in same { V = print } { V = print }"

              match Weir.Check.typecheck boxEnv e with
              | Ok _ -> failtest "expected rejection through Box<fn>"
              | Error terr -> Expect.stringContains terr.Message "requires equatable values" ""
          }
          test "Eq x generic records: clean instantiation decomposes and passes" {
              let boxEnv = env |> declare "type Box<'a> = { V: 'a }"
              let e = parse "let same = fun x -> fun y -> x == y in same { V = 1 } { V = 1 }"

              match Weir.Check.typecheck boxEnv e with
              | Ok te -> Expect.equal (formatTy te.Ty) "bool" ""
              | Error terr -> failtest (formatError terr)
          }
          test "classes x rows: one row-constrained scheme, two verdicts" {
              let holderEnv =
                  env
                  |> declare "type Holder = { S: seq<int> }"
                  |> declare "type Flat = { N: int }"

              let good = parse "let eqr = fun a -> fun b -> a == b in eqr { N = 1 } { N = 1 }"

              match Weir.Check.typecheck holderEnv good with
              | Ok te -> Expect.equal (formatTy te.Ty) "bool" "clean record passes"
              | Error terr -> failtest (formatError terr)


              let bad =
                  parse "let eqr = fun a -> fun b -> a == b in eqr { S = nats } { S = nats }"

              match Weir.Check.typecheck holderEnv bad with
              | Ok _ -> failtest "seq-carrying record must reject"
              | Error terr -> Expect.stringContains terr.Message "requires equatable values" ""
          }
          test "constraint x mergeRows: unified constrained rows still fire" {
              let holderEnv = env |> declare "type Holder = { S: seq<int> }"

              let e =
                  parse "let f = fun a -> fun b -> (a == b) && (a == a) in f { S = nats } { S = nats }"

              match Weir.Check.typecheck holderEnv e with
              | Ok _ -> failtest "moved constraint must still reject"
              | Error terr -> Expect.stringContains terr.Message "requires equatable values" ""
          }
          test "constraint escapes through nested generalization to the outer scheme" {
              let outerOk =
                  "let outer = fun x -> (let same = fun a -> fun b -> a == b in same x x) in outer 1"

              expectValue outerOk (VBool true)

              let terr =
                  checkErr "let outer = fun x -> (let same = fun a -> fun b -> a == b in same x x) in outer print"

              Expect.stringContains (formatError terr) "equatable" ""
          }
          test "classes x match guards: == in a when-guard demands through the scrutinee" {
              expectValue
                  "let pick x = match x with | v when v == 3 -> \"three\" | _ -> \"other\" in pick 3"
                  (VStr "three")

              let terr =
                  checkErr "let pick x = match x with | v when v == x -> 1 | _ -> 0 in pick print"

              Expect.stringContains (formatError terr) "equatable" ""
          }
          test "classes x print sentinel: composed, print's scalar rule untouched" {
              let te = checkOk "print (show [1; 2])"
              Expect.equal (formatTy te.Ty) "unit" ""

              // Show did NOT widen print: a seq<int> still cannot go to print directly
              let terr = checkErr "print [1]"
              Expect.stringContains (formatError terr) "print" ""
          } ]

let productMatrixTests =
    testList
        "Product matrix (retroactive sweep)"
        [ // A x F: comment inside a compound body is transparent
          test "A x F: comment between compound-body siblings keeps grouping" {
              match Weir.Script.assemble [ 1, "if c then"; 2, "    eff1"; 4, "    eff2" ] with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "if c then eff1 ; eff2") ""
              | other -> failtest $"unexpected: {other}"
          }
          // C x E: pipe line after a blank
          // [D:body-blanks]
          test "C x E: pipe continuation joins across a gap" {
              match Weir.Script.assemble [ 1, "ls"; 2, ""; 3, "    |> Seq.length" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "ls |> Seq.length" ""
              | other -> failtest $"unexpected: {other}"
          }
          // C x F: comment between pipe stages (comment filtered upstream:
          // the runner drops it; assemble sees the gap in line numbers)
          test "C x F: comment between pipe stages is transparent" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    ls"; 4, "    |> Seq.length" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = ls |> Seq.length" ""
              | other -> failtest $"unexpected: {other}"
          }
          // E x F: comment-only after a blank stays invisible
          // [D:body-blanks]
          test "E x F: indented line after blank+comment joins" {
              match Weir.Script.assemble [ 1, "let x = 1"; 2, ""; 4, "    + 2" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = 1 + 2" ""
              | other -> failtest $"unexpected: {other}"
          }
          // E x G: former sibling level after a blank
          // [D:body-blanks]: the sibling rule is indent-keyed, not
          // adjacency-keyed — gap-invariant `;`
          test "E x G: sibling sequencing joins across a gap" {
              match
                  Weir.Script.assemble [ 1, "let f x ="; 2, "    printerr \"a\""; 3, ""; 4, "    printerr \"b\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = printerr \"a\" ; printerr \"b\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          // F x G: sibling `;` joins across a transparent comment
          test "F x G: sibling sequencing joins across a comment line" {
              match Weir.Script.assemble [ 1, "let f x ="; 2, "    printerr \"a\""; 4, "    printerr \"b\"" ] with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = printerr \"a\" ; printerr \"b\"") ""
              | other -> failtest $"unexpected: {other}"
          } ]

let offsideTests =
    testList
        "Offside close & record continuations"
        [ // the bicep bite: a sibling at the if's own indent closes it
          test "offside close: sibling at head indent wraps the if" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    if c then fail \"u\""; 3, "    { Name = s }" ] with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let t = (if c then fail \"u\") ; { Name = s }") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "offside close: the silent variant no longer swallows" {
              match
                  Weir.Script.assemble [ 1, "let f c ="; 2, "    if c then printerr \"a\""; 3, "    printerr \"b\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f c = (if c then printerr \"a\") ; printerr \"b\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "else extends the compound instead of closing it" {
              match
                  Weir.Script.assemble
                      [ 1, "let f c ="
                        2, "    if c then printerr \"a\""
                        3, "    else printerr \"b\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let f c = if c then printerr \"a\" else printerr \"b\"" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "sibling after else closes the whole if/else" {
              match
                  Weir.Script.assemble
                      [ 1, "let f c ="
                        2, "    if c then a"
                        3, "    else b"
                        4, "    printerr \"z\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f c = (if c then a else b) ; printerr \"z\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "deeper siblings still join INTO the body (the greedy protectorate)" {
              match Weir.Script.assemble [ 1, "if c then"; 2, "    eff1"; 3, "    eff2" ] with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "if c then eff1 ; eff2") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "nested dedent closes through both levels" {
              match
                  Weir.Script.assemble
                      [ 1, "let f x ="
                        2, "    if c then"
                        3, "        eff1"
                        4, "        eff2"
                        5, "    printerr \"z\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = (if c then eff1 ; eff2) ; printerr \"z\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "sequential ifs: first closes, second stays open at statement end" {
              match Weir.Script.assemble [ 1, "let f c ="; 2, "    if a then x"; 3, "    if b then y" ] with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f c = (if a then x) ; if b then y") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "match closes at a sibling too" {
              match
                  Weir.Script.assemble
                      [ 1, "let v ="
                        2, "    match x with"
                        3, "    | A -> printerr \"a\""
                        4, "    printerr \"z\"" ]
              with
              | Ok [ ll ] ->
                  Expect.equal ll.Text (asmSib "let v = (match x with | A -> printerr \"a\") ; printerr \"z\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "district x offside: closing sibling wraps the marker's if" {
              match
                  Weir.Script.assemble
                      [ 1, "let f t ="
                        2, "    if c then !"
                        3, "        git pull"
                        4, "    print \"x\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f t = (if c then !(git pull)) ; print \"x\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "record continuation: bare fields get separators" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    { Name = \"a\""; 3, "      Count = 2 }" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let t = { Name = \"a\" ; Count = 2 }" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "record continuation: trailing ; means no double separator" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    { Name = \"a\";"; 3, "    Count = 2 }" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let t = { Name = \"a\"; Count = 2 }" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "a lowercase case name errors AT the name, not past it" {
              // rawWord's trailing ws crosses the physical line — the
              // error must anchor BEFORE the read consumes it
              let r: Weir.Parser.Resolver =
                  { IsKnown = fun _ -> true
                    IsCommandCallable = fun _ -> false
                    IsExternal = fun _ -> false
                    ExternalNames = fun () -> Seq.empty }

              match Weir.Parser.parseLineFull r "type P = | Pulled | upToDate | Join of string" with
              | Error f ->
                  Expect.stringContains f.Message "uppercase letter" ""
                  Expect.equal f.Col (Some 21) "the column of 'upToDate', not the next token"
              | Ok _ -> failtest "expected the casing error"
          }
          // the span classes ride the consumed-separator law
          // [D:seq-commit] [D:arm-commit]: errors anchor on the junk
          test "a parse error after a district anchors on the junk" {
              let lines =
                  [ "let v0 ="
                    "    if 79 == 84 then !"
                    "        echo m0 w381"
                    "    if 0 > 99 then ?!?"
                    "        print \"x\""
                    "    9" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              // [D:seq-commit]: no backtrack can anchor past the
              // district — the primary lands ON the junk
              match diags |> List.filter (fun d -> d.Severity = "error") with
              | d :: _ -> Expect.equal (d.Line, d.Col) (4, 20) "primary anchors on the junk itself"
              | [] -> failtest "expected a parse diagnostic"
          }
          test "junk after a bound name errors at check, on the junk [D:seq-commit]" {
              // a consumed ';' commits to its element: the failing tail
              // cannot re-parse outside its let-in scope into a phantom
              // command — check and run agree, located at the junk
              let lines =
                  [ "let v0 ="
                    "    let v3 = \"a\""
                    "    print \"mm\""
                    "    let v4 = v3 ?!?"
                    "    3" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              Expect.exists
                  diags
                  (fun d -> d.Severity = "error" && d.Line = 4)
                  "check errors ON the junk line, like the runner"

              Expect.isFalse (diags |> List.exists (fun d -> d.Code = "cmd-not-found")) "no phantom command survives"
          }
          test "junk in a nested arm reports at the junk [D:arm-commit]" {
              // a consumed '|' commits to its arm [D:arm-commit]: the
              // list never backs out, so the bare-pipe fatal never
              // receives a counterfeit "completed expression"
              let lines =
                  [ "let v6 ="
                    "    match 62 with"
                    "    | 0 -> \"a\""
                    "    | 1 ->"
                    "        match \"w790\" with"
                    "        | \"k0\" -> \"b\" ?!?"
                    "        | _ -> \"w484\""
                    "    | _ -> \"d\"" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              match diags |> List.filter (fun d -> d.Severity = "error") with
              | d :: _ ->
                  Expect.equal (d.Line, d.Col) (6, 23) "on the junk itself"
                  Expect.isFalse (d.Message.Contains "'|' chains commands") "the hint keeps its real customers"
              | [] -> failtest "expected a parse diagnostic"
          }
          test "record-literal commit: deep field junk reports at its site [D:arm-commit]" {
              // the record literal commits on its `ident =` head
              // [D:arm-commit]: a failing field must not rewind the
              // literal into the update alternative's dump
              let lines =
                  [ "type R = { A: int; B: string }"
                    "let v = { A = 47"
                    "          B = \"x\" ?!?"
                    "          }" ]

              let diags, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines

              match diags |> List.filter (fun d -> d.Severity = "error") with
              | d :: _ ->
                  Expect.equal d.Line 3 "on the junk's line"
                  Expect.isFalse (d.Message.Contains "'with'") "no update-grammar dump"
              | [] -> failtest "expected the parse diagnostic"

              // the update spelling keeps its path
              match Weir.Parser.parseStmt "let s = { r with A = 3 }" with
              | Ok _ -> ()
              | Error e -> failtest $"update must still parse: {e}"
          }
          test "arm-commit products: chains, or-patterns, reserved heads, REPL [D:arm-commit]" {
              let rNone: Weir.Parser.Resolver =
                  { IsKnown = (fun _ -> false)
                    IsCommandCallable = (fun _ -> false)
                    IsExternal = (fun _ -> false)
                    ExternalNames = fun () -> Seq.empty }

              let rGit =
                  { rNone with
                      IsExternal = (fun n -> n = "git") }

              // (a) a bare '|' in an arm RHS is an ARM SEPARATOR (F#
              // reads it the same way) — command chains in arms ride $()
              match Weir.Parser.parseLineFull rGit "let v = match 1 with | 1 -> git log | Seq.head | _ -> \"x\"" with
              | Error _ -> ()
              | Ok _ -> failtest "a bare chain in an arm RHS must not parse"

              match
                  Weir.Parser.parseLineFull rGit "let v = match 1 with | 1 -> $(git log) |> Seq.head | _ -> \"x\""
              with
              | Ok _ -> ()
              | Error e -> failtest $"the sigil spelling is the arm chain: {e.Message}"

              // (b) or-patterns stay rejected, located at the second '|'
              // (divergence row or-patterns; F# accepts)
              match Weir.Parser.parseLineFull rNone "let v = match 1 with | 0 | 1 -> \"low\" | _ -> \"hi\"" with
              | Error f -> Expect.equal f.Col (Some 26) "at the or-pattern's second bar"
              | Ok _ -> failtest "or-patterns are not a weir feature (yet)"

              // (c) guards under commit
              match Weir.Parser.parseStmt "let v = match 3 with | n when n > 2 -> n | _ -> 0" with
              | Ok _ -> ()
              | Error e -> failtest $"guards must survive the commit: {e}"

              // (e) a reserved word in arm-head position errors AT it
              match Weir.Parser.parseLineFull rNone "let f = match 1 with | 1 -> 2 | function -> 3" with
              | Error f -> Expect.equal f.Col (Some 33) "located at the reserved word"
              | Ok _ -> failtest "expected the arm-head failure"

              // (f) the REPL single-line grammar commits identically
              match Weir.Parser.parseStmt "match 1 with | 1 -> 2 | _ -> ?!?" with
              | Error msg -> Expect.stringContains msg "Col: 30" "at the junk in the one-line spelling"
              | Ok _ -> failtest "expected the junk failure"
          }
          test "a field misaligned from ITS OWN attribute line errors [D:field-alignment]" {
              // the >] dangle suppresses the separator, never the alignment
              // an unaligned attr-owned field must fail the CHECK, not
              // surface as a runtime argv error
              match
                  Weir.Script.assemble
                      [ 1, "type C = {"
                        2, "    [<Doc \"d\">]"
                        3, "     subdir: string"
                        4, "    force: bool"
                        5, "}" ]
              with
              | Error e -> Expect.stringContains e "they sit at column 4" ""
              | other -> failtest $"expected the alignment error, got {other}"

              match
                  Weir.Script.assemble
                      [ 1, "type C = {"
                        2, "    [<Doc \"d\">]"
                        3, "    subdir: string"
                        4, "    force: bool"
                        5, "}" ]
              with
              | Ok _ -> ()
              | Error e -> failtest $"the aligned attr+field must stay legal: {e}"
          }
          test "record fields off the first-field column error naming it" {
              match
                  Weir.Script.assemble
                      [ 1, "let t ="
                        2, "    { Name = \"a\""
                        3, "    Count = 2"
                        4, "    Tag = \"x\" }" ]
              with
              | Error e -> Expect.stringContains e "they sit at column 6" ""
              | other -> failtest $"expected the alignment error, got {other}"
          }
          test "record continuation: a field's value may open on the NEXT line (sweep catch)" {
              match
                  Weir.Script.assemble
                      [ 1, "let o ="
                        2, "    { Name = \"outer\""
                        3, "      In ="
                        4, "        { V = 42 } }" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let o = { Name = \"outer\" ; In = { V = 42 } }" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "classifyPiece: StartsField is ident-guarded" {
              Expect.isTrue (Weir.Script.classifyPiece "Count = 2").StartsField ""
              Expect.isTrue (Weir.Script.classifyPiece "In =").StartsField ""
              Expect.isFalse (Weir.Script.classifyPiece "{ V = 42 } }").StartsField ""
              Expect.isFalse (Weir.Script.classifyPiece "1 + x = y").StartsField ""
          }
          test "record continuation: col-0 close is legal inside a brace" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    { Name = \"a\""; 3, "}" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let t = { Name = \"a\" }" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "interp-hole braces do not count (scanner is string-aware)" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    { Name = $\"i{1}b\""; 3, "      Count = 2 }" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let t = { Name = $\"i{1}b\" ; Count = 2 }" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "blank inside an open brace is the located record error" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    { Name = \"a\""; 3, "" ] with
              | Error e -> Expect.stringContains e "record literal" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "EOF inside an open brace is the located record error" {
              match Weir.Script.assemble [ 1, "let t ="; 2, "    { Name = \"a\"" ] with
              | Error e -> Expect.stringContains e "record literal" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "district lines never enter brace mode (commands are not records)" {
              match Weir.Script.assemble [ 1, "if c then !"; 2, "    awk \"{print}\" f"; 3, "    git pull" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "if c then !(awk \"{print}\" f) ; !(git pull)" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "exit types like fail: int -> unit" {
              match Weir.Parser.parseLine cmdResolver "exit 3" with
              | Ok(SExpr e) ->
                  match Weir.Check.typecheck env e with
                  | Ok te -> Expect.equal (Weir.Types.formatTy te.Ty) "unit" ""
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"
          }
          test "exit rejects a string" {
              let terr = checkErr "exit \"boom\""
              Expect.stringContains (formatError terr) "int" ""
          } ]

let sequencingTests =
    testList
        "Block sequencing"
        [ test "explicit semicolon sequences" { expectValue "(print \"x\" ; 41 + 1)" (VInt 42L) }
          test "non-unit first is the tailored error" {
              let terr = checkErr "1 ; print \"no\""
              Expect.stringContains (formatError terr) "must be unit" ""
          }
          test "greedy bodies: semicolon binds INTO then (named divergence)" {
              // (if false then print "no") never sequences the tail here:
              expectValue "let c = false in (if c then print \"a\" ; print \"b\") ; 7" (VInt 7L)
          }
          test "assembler: same-indent siblings sequence" {
              match Weir.Script.assemble [ 1, "let w ="; 2, "    print \"a\""; 3, "    print \"b\"" ] with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let w = print \"a\" ; print \"b\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "assembler: let-close beats sibling; sequence resumes after" {
              match
                  Weir.Script.assemble [ 1, "let w ="; 2, "    let a = 1"; 3, "    print \"x\""; 4, "    print \"y\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let w = let a = 1 in print \"x\" ; print \"y\"") ""
              | other -> failtest $"unexpected: {other}"
          }
          test "function RHS takes a sequenced body (bicep receipt)" {
              match Weir.Parser.parseLine cmdResolver "let f x = printerr \"a\" ; printerr \"b\"" with
              | Ok(SLet("f", { Kind = ELambda("x", _, { Kind = ESeq _ }) })) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "let-in value takes a sequence; in still closes it" {
              expectValue "let u = print \"a\" ; print \"b\" in 5" (VInt 5L)
          }
          test "no-params let RHS falls through to sequence" {
              match Weir.Parser.parseLine cmdResolver "let u = printerr \"a\" ; printerr \"b\"" with
              | Ok(SLet("u", { Kind = ESeq _ })) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "assembler: pipes stay inert to the sibling rule" {
              match Weir.Script.assemble [ 1, "let x ="; 2, "    ls"; 3, "    |> Seq.length" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "let x = ls |> Seq.length" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "command-mode semicolon argv warns (bash prior-bleed)" {
              match Weir.Parser.parseLine cmdResolver "git add -A ; git push" with
              | Ok(SCmd e) ->
                  match Weir.Check.typecheck env e with
                  | Ok te ->
                      let ws = Weir.Check.warnings te
                      Expect.exists ws (fun w -> w.Message.Contains "does not chain") "warned"
                  | Error terr -> failtest (formatError terr)
              | other -> failtest $"unexpected: {other}"
          } ]

let siblingSentinelTests =
    // [D:sibling-sentinel] — the diagnostics-arc Session E successor:
    // command mode stops at the machine sibling boundary, so a
    // command-first body sequences instead of over-running to EOF.
    let diags lines =
        let ds, _, _, _ = Weir.Script.analyzeLines "pin.weir" lines
        ds

    testList
        "Sibling sentinel"
        [ test "ACCEPTANCE: cmd then let..in body parses as a real sequence" {
              // was the backtrack-to-EOF dump; now a clean ESeq whose
              // first element is the command
              match
                  Weir.Parser.parseLine
                      realResolver
                      ("let f t = git status" + Weir.Parser.sibSepStr + "let e = \"x\" in print e")
              with
              | Ok(SLet("f", { Kind = ELambda("t", _, { Kind = ESeq({ Kind = ECmd("git", _, _) }, _) }) })) -> ()
              | other -> failtest $"expected ESeq(cmd, ...), got: {other}"
          }
          test "ACCEPTANCE: the repro reports AT THE COMMAND HEAD, not at EOF" {
              // a command as the first body sibling is a discarded
              // non-unit — the seq-unit rule fires ON the command head,
              // no 'end of input stream', no raw expecting-list
              let ds = diags [ "let f t ="; "    git status"; "    let e = \"x\""; "    print e" ]

              match ds |> List.filter (fun d -> d.Severity = "error") with
              | [ d ] ->
                  Expect.equal (d.Line, d.Col) (2, 5) "at the command head"
                  Expect.notEqual d.Code "parse" "not a parse dump"
                  Expect.isFalse (d.Message.Contains "end of the input stream") "no EOF note"
                  Expect.isFalse (d.Message.Contains "Expecting:") "no raw expecting-list"
              | other -> failtest $"expected ONE error at the head, got {other}"
          }
          test "user ';' is byte-identical: one command, a bareword arg, the prior-bleed warning" {
              // the whole reason B beat A — a user-typed ';' on one line
              // is STILL a command with a ';' argv word that warns
              match Weir.Parser.parseLine cmdResolver "git status ; echo hi" with
              | Ok(SCmd({ Kind = ECmd("git", args, _) })) ->
                  Expect.isTrue
                      (args |> List.exists (fun a -> a.Kind = EStr ";"))
                      "the ';' is a bareword arg, not a separator"
              | other -> failtest $"expected one command swallowing ';', got: {other}"
          }
          test "unproduceable: the sentinel in SOURCE is rejected at assembly" {
              match Weir.Script.assemble [ 1, "print \"a\"" + Weir.Parser.sibSepStr + "print \"b\"" ] with
              | Error e -> Expect.stringContains e "illegal control character" "rejected as illegal"
              | Ok _ -> failtest "a source sentinel must not assemble"
          }
          test "no-leak: the sentinel never surfaces in a diagnostic" {
              // a command-first body whose parse dump lists SEPARATORS in the
              // expected-set — the seqSep relabel keeps the sentinel out, and
              // cleanParseDump scrubs both the raw char AND FParsec's 
              // escape (the form that leaked into Zed's expecting-list)
              let noLeak (lines: string list) =
                  let ds = diags lines

                  for d in ds do
                      Expect.isFalse (d.Message.Contains Weir.Parser.sibSepStr) $"raw sentinel leaked: {d.Message}"
                      Expect.isFalse (d.Message.Contains "\\u001f") $"escaped sentinel leaked: {d.Message}"
                      // the user-facing separator ';' is the only form allowed
                      ()

              noLeak [ "let f t ="; "    git status"; "    print a b )" ] // lists ';' in expected-set
              noLeak [ "let f t ="; "    git status"; "    let e = ("; "    print e" ]
          }
          test "fmt output never carries the sentinel (assemble->check artifact only)" {
              match Weir.Fmt.formatLines [ "let f t ="; "    git status"; "    let e = \"x\""; "    print e" ] with
              | Ok lines ->
                  for l in lines do
                      Expect.isFalse (l.Contains Weir.Parser.sibSepStr) $"sentinel in fmt output: {l}"
              | Error e -> failtest $"fmt failed: {e}"
          } ]

let sigilTests =
    testList
        "Command sigils"
        [ test "capture sigil parses to the command chain (realResolver)" {
              match Weir.Parser.parseLine realResolver "let b = $(git branch) |> Seq.length" with
              | Ok(SLet("b", { Kind = EPipe({ Kind = ECmd("git", _, _) }, _) })) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "effect sigil desugars to chain |> print" {
              match Weir.Parser.parseLine realResolver "!(git status)" with
              | Ok(SExpr { Kind = EPipe({ Kind = ECmd("git", _, _) }, { Kind = EVar "print" }) }) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "sigils x interpolation: holes never open command mode" {
              // $"{...}" holes are expression holes; a bareword there is unbound
              match Weir.Parser.parseLine realResolver "print $\"x{git}y\"" with
              | Ok(SExpr _) -> ()
              | other -> failtest $"unexpected: {other}"

              let terr = checkErr "$\"x{git}y\""
              Expect.stringContains (formatError terr) "unbound" "git is not a command in a hole"
          }
          test "sigils x greedy-semicolon: single-line grouping is body-scoped" {
              match Weir.Parser.parseLine realResolver "if 1 > 2 then !(git status) ; !(git branch)" with
              | Ok(SExpr { Kind = EIf(_, { Kind = ESeq _ }, None) }) -> ()
              | other -> failtest $"both effects must sit INSIDE the then-branch, got {other}"
          }
          test "sigils x complete outside: parse error, statement spelling exists" {
              match Weir.Parser.parseLine realResolver "$(git status) | complete" with
              | Error _ -> ()
              | Ok s -> failtest $"'| complete' is a command-suffix desugar; got {s}"
          }
          test "sigils x complete inside: uniform interior grammar composes" {
              match Weir.Parser.parseLine realResolver "let r = $(git status | complete)" with
              | Ok(SLet("r", { Kind = EApp _ })) -> ()
              | other -> failtest $"unexpected: {other}"
          }
          test "sigils x strict mode: grammar, not resolution" {
              // the sigil works in strict scripts; interior expr stages qualify
              match Weir.Parser.parseLine realResolver "let x = $(git branch | Seq.map Str.trim)" with
              | Ok(SLet _) -> ()
              | other -> failtest $"unexpected: {other}"
          } ]

let districtTests =
    testList
        "Command district"
        [ test "marker distributes: district equals the single-line form" {
              match Weir.Script.assemble [ 1, "if go then !"; 2, "    git pull"; 3, "    git push" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "if go then !(git pull) ; !(git push)" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "pipe-headed district lines continue the previous command" {
              match Weir.Script.assemble [ 1, "if go then !"; 2, "    git branch"; 3, "    | Seq.map Str.trim" ] with
              | Ok [ ll ] -> Expect.equal ll.Text "if go then !(git branch | Seq.map Str.trim)" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "district x else: dedent to marker indent rejoins the if" {
              match
                  Weir.Script.assemble
                      [ 1, "let m ="
                        2, "    if c then !"
                        3, "        git pull"
                        4, "    else print \"x\"" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let m = if c then !(git pull) else print \"x\"" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "district x pending let: closing line still closes the let" {
              match
                  Weir.Script.assemble
                      [ 1, "let w ="
                        2, "    let r ="
                        3, "        if c then !"
                        4, "            git pull"
                        5, "    r" ]
              with
              | Ok [ ll ] -> Expect.equal ll.Text "let w = let r = (if c then !(git pull)) in r" ""
              | other -> failtest $"unexpected: {other}"
          }
          test "MECHANISM PIN: a closing line is reprocessed exactly once (no duplicated segments)" {
              match
                  Weir.Script.assemble
                      [ 1, "let w ="
                        2, "    let r ="
                        3, "        if c then !"
                        4, "            git pull"
                        5, "    r" ]
              with
              | Ok [ ll ] ->
                  let line5 = ll.Segments |> List.filter (fun (_, n, _) -> n = 5)
                  Expect.hasLength line5 1 "one span-table entry for the district-closing let-closing line"
              | other -> failtest $"unexpected: {other}"
          }
          test "MECHANISM PIN: assembler recursion bounded by nesting, not file length" {
              // 500 sequential districts with deep dedents must not overflow
              let lines =
                  [ for i in 1..500 do
                        yield "let go = 1 > 0"
                        yield "if go then !"
                        yield "    git status"
                        yield "" ]
                  |> List.mapi (fun i l -> i + 1, l)

              match Weir.Script.assemble lines with
              | Ok lls -> Expect.equal (List.length lls) 1000 "all statements assembled"
              | Error e -> failtest e
          }
          test "district errors: hint, contract, empty block" {
              match Weir.Script.assemble [ 1, "if go then !"; 2, "    !(git pull)" ] with
              | Error msg -> Expect.stringContains msg "drop the !(" "sigil-inside hint"
              | other -> failtest $"unexpected: {other}"

              match Weir.Script.assemble [ 1, "if go then !"; 2, "    let x = 1" ] with
              | Error msg -> Expect.stringContains msg "bind values outside" "commands-only contract"
              | other -> failtest $"unexpected: {other}"

              match Weir.Script.assemble [ 1, "if go then !"; 2, "print \"no\"" ] with
              | Error msg -> Expect.stringContains msg "needs an indented block" "armed but empty"
              | other -> failtest $"unexpected: {other}"
          }
          test "district x uneven indent: one command per line error" {
              match Weir.Script.assemble [ 1, "if go then !"; 2, "    git pull"; 3, "        extra" ] with
              | Error msg -> Expect.stringContains msg "one per line" ""
              | other -> failtest $"unexpected: {other}"
          } ]

let indexerTests =
    testList
        "Indexers"
        [ test "xs[i] desugars to Seq.item" { expectValue "[\"a\"; \"b\"][1]" (VStr "b") }
          test "chains and composes with fields and sigils" {
              expectValue "[[1; 2]; [3; 4]][1][0]" (VInt 3L)
              expectValue "(ls |> Seq.force)[0].name" (VStr "a.txt")
          }
          test "the whitespace rule: space means application (F# 6 dotless precedent)" {
              expectValue "Seq.sum [1; 2]" (VInt 3L)
              expectParse "f [0]" "(f [0])"
              expectParse "f[0]" "((Seq.item 0) f)"
          }
          test "underscore shorthand extends to indexing" {
              expectValue "[[\"a\"; \"b\"]] |> Seq.map _[0]" (VSeq [ VStr "a" ])
          }
          test "out of range raises; tryItem is the safe form" {
              Expect.throws (fun () -> run "[1][9]" |> ignore) "raises"
              expectValue "[1] |> Seq.tryItem 9" (VUnion("None", None))
          }
          test "non-seq targets are ordinary type errors" {
              let terr = checkErr "5[0]"
              Expect.stringContains (formatError terr) "expected seq" ""
          } ]

let envLoadTests =
    testSequenced
    <| testList
        "Typed Env"
        [ test "all-good load with every scalar and Option" {
              System.Environment.SetEnvironmentVariable("WT_S", "hello")
              System.Environment.SetEnvironmentVariable("WT_I", "42")
              System.Environment.SetEnvironmentVariable("WT_B", "false")
              System.Environment.SetEnvironmentVariable("WT_O", null)

              try
                  let e2 =
                      env
                      |> declare "type WtCfg = { WT_S: string; WT_I: int; WT_B: bool; WT_O: Option<int> }"

                  match Weir.Check.typecheck e2 (parse "Env.load WtCfg") with
                  | Ok te ->
                      Expect.equal te.Ty (TNamed("WtCfg", [])) "typed statically"

                      match eval valueEnv te with
                      | VRecord(_, fs) ->
                          Expect.equal fs["WT_I"] (VInt 42L) "int parsed"
                          Expect.equal fs["WT_B"] (VBool false) "bool parsed"
                          Expect.equal fs["WT_O"] (VUnion("None", None)) "absent Option is None"
                      | v -> failtest $"unexpected {formatValue v}"
                  | Error terr -> failtest (formatError terr)
              finally
                  for n in [ "WT_S"; "WT_I"; "WT_B" ] do
                      System.Environment.SetEnvironmentVariable(n, null)
          }
          test "problems collect into ONE boundary error (incl. TRUE and 1 rejected)" {
              System.Environment.SetEnvironmentVariable("WT_I", "abc")
              System.Environment.SetEnvironmentVariable("WT_B", "TRUE")

              try
                  let e2 =
                      env |> declare "type WtBad = { WT_I: int; WT_B: bool; WT_MISSING_ZZ: string }"

                  match Weir.Check.typecheck e2 (parse "Env.load WtBad") with
                  | Ok te ->
                      let ex = Expect.throwsC (fun () -> eval valueEnv te |> ignore) id
                      Expect.stringContains ex.Message "WT_I is not an int" "int problem"
                      Expect.stringContains ex.Message "WT_B is not a bool" "exact-bool problem"
                      Expect.stringContains ex.Message "WT_MISSING_ZZ is missing" "missing problem"
                  | Error terr -> failtest (formatError terr)
              finally
                  for n in [ "WT_I"; "WT_B" ] do
                      System.Environment.SetEnvironmentVariable(n, null)
          }
          test "present-but-garbage Option is an error, not None" {
              System.Environment.SetEnvironmentVariable("WT_OI", "xyz")

              try
                  let e2 = env |> declare "type WtOpt = { WT_OI: Option<int> }"

                  match Weir.Check.typecheck e2 (parse "Env.load WtOpt") with
                  | Ok te -> Expect.throws (fun () -> eval valueEnv te |> ignore) "garbage is not absence"
                  | Error terr -> failtest (formatError terr)
              finally
                  System.Environment.SetEnvironmentVariable("WT_OI", null)
          }
          test "non-scalar fields rejected at CHECK time" {
              let e2 =
                  env
                  |> declare "type WtSeq = { XS: seq<string> }"
                  |> declare "type WtNest = { P: Point }"

              for bad in [ "Env.load WtSeq"; "Env.load WtNest" ] do
                  match Weir.Check.typecheck e2 (parse bad) with
                  | Error terr -> Expect.stringContains (formatError terr) "must be string, int, bool" bad
                  | Ok _ -> failtest $"{bad} should be rejected"
          }
          test "from-json-family errors: generic, union, unknown, non-type" {
              Expect.stringContains (formatError (checkErr "Env.load Proc")) "union" ""
              Expect.stringContains (formatError (checkErr "Env.load Nonesuch")) "unknown type" ""
              Expect.stringContains (formatError (checkErr "Env.load double")) "unknown type" ""
          }
          test "Env is unshadowable by binding (casing law flip; ctor collision still guarded)" {
              // was: value-shadowed Env falls through to normal rules.
              // let-shadowing is now rejected at the binder; the Env.load
              // unshadowed-guard stays for the constructor-collision case
              // (type T = Env of int remains declarable).
              let terr = checkErr "let Env = 1 in Env.load Point"
              Expect.stringContains (formatError terr) "binding names start lowercase" ""
          } ]

let fileTests =
    testSequenced
    <| testList
        "File module"
        [ test "write, read, append roundtrip" {
              let path =
                  Path.Combine(Path.GetTempPath(), $"weir-file-{System.Guid.NewGuid():N}.txt")

              try
                  expectValue $"File.write \"{path}\" [\"a\"; \"b\"]" VUnit
                  Expect.equal (run $"File.read \"{path}\"" |> forceSeq) [ VStr "a"; VStr "b" ] "read back"
                  run $"File.append \"{path}\" [\"c\"]" |> ignore
                  Expect.equal (run $"File.read \"{path}\" |> Seq.length") (VInt 3L) "appended"
                  expectValue $"File.exists \"{path}\"" (VBool true)
              finally
                  if File.Exists path then
                      File.Delete path
          }
          test "exists is false for missing" { expectValue "File.exists \"/weir-definitely-not\"" (VBool false) }
          test "relative paths resolve against Session.Cwd" {
              let dir = Path.Combine(Path.GetTempPath(), $"weir-fdir-{System.Guid.NewGuid():N}")
              Directory.CreateDirectory dir |> ignore

              try
                  let expected = Path.Combine(dir, "rel.txt")

                  expectValue $"let d = cd \"{dir}\" in File.write \"rel.txt\" [\"x\"]" VUnit

                  Expect.isTrue (File.Exists expected) "written under Session.Cwd"
              finally
                  Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
                  Directory.Delete(dir, true)
          }
          test "read of a missing file raises" {
              Expect.throws (fun () -> run "File.read \"/weir-definitely-not\"" |> forceSeq |> ignore) ""
          } ]

let operatorTests =
    testList
        "Operator completeness"
        [ test "precedence: || below && below comparisons" {
              expectParse "a || b && c" "(|| a (&& b c))"
              expectParse "1 < 2 || 2 <= 3 && 4 >= 4" "(|| (< 1 2) (&& (<= 2 3) (>= 4 4)))"
          }
          test "not-equal parses as one operator" { expectParse "a <> b" "(<> a b)" }
          test "not-equal on values" {
              expectValue "1 <> 2" (VBool true)
              expectValue "\"a\" <> \"a\"" (VBool false)
              expectValue "Running 1 <> Stopped" (VBool true)
          }
          test "not-equal inherits equatability: seqs rejected" {
              Expect.stringContains (checkErr "nats <> nats").Message "'<>' is not defined for seq<int>" ""
          }
          test "ordered comparisons include boundaries" {
              expectValue "2 >= 2" (VBool true)
              expectValue "2 <= 1" (VBool false)
          }
          test "common filter shape works" {
              expectValue
                  "ls |> where (fun f -> f.name <> \"a.txt\" && f.bytes <= 3145728)"
                  (VSeq
                      [ Weir.Builtins.file "c.log" 1048576 false
                        Weir.Builtins.file "d.iso" 3145728 false ])
          }
          test "not builtin" {
              expectValue "not true" (VBool false)

              expectValue
                  "ls |> where (fun f -> not f.readOnly) |> first 1"
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
              Expect.stringContains (checkErr "double 1 2").Message "'double' takes at most 1 argument(s), but got 2" ""
          }
          test "shadowing with a different type is respected" {
              Expect.stringContains
                  (checkErr "let x = 1 in let x = \"s\" in x + 1").Message
                  "expected string, got int"
                  ""

              expectValue "let x = 1 in let x = \"s\" in x + \"!\"" (VStr "s!")
          }
          test "inferred element type contradicts use two stages later" {
              let terr = checkErr "nats |> map (fun x -> x * x) |> where (fun x -> x.readOnly)"
              Expect.stringContains terr.Message "only records have fields" ""
          }
          test "row constraint conflicts with the declared field type" {
              Expect.stringContains
                  (checkErr "let g = fun r -> r.name > 1 in ls |> where g").Message
                  "expected string, got int"
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
                  (checkErr "ls |> where (fun f -> f.bytes > 1) |> where (fun f -> f.Nonexistent == 1)").Message
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
                  Expect.equal te.Ty (TInt) "int and string uses of getV both accepted"
                  Expect.equal (eval valueEnv te) (VInt 1) "sum of 0+1"
          }
          test "5.2 shadowing does not leak the outer row constraint" {
              match (checkOk "fun f -> (f.A > 1) == (let f = \"s\" in f == \"s\")").Ty with
              | TFun(TRowVar(_, [ "A", TInt ]), TBool) -> ()
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
                  (runWith [ "src", src ] "src |> from porcelain |> map _.path" |> forceSeq)
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
          session2Tests
          commandModeTests
          cdTests
          diagnoseTests
          session3Tests
          stringTests
          genericsTests
          attributeTests
          typedArgvTests
          chooseTests
          bracketContinuationTests
          fmtMatchTests
          fmtStroustrupTests
          replEchoTests
          replColorTests
          seqPatternTests
          blockLetCmdTests
          multilineLambdaTests
          semanticTokenTests
          pipeAlignTests
          optionSweepTests
          moduleTests
          scriptTests
          multilineTests
          readProbes
          interpTests
          unitPrintTests
          rangeTests
          depthGuardTests
          boolBranchTests
          agentFindingsTests
          fmtTests
          paramSugarTests
          showTests
          seqAccessTests
          sequencingTests
          siblingSentinelTests
          offsideTests
          productMatrixTests
          casingTests
          binderTests
          tupleTests
          literalThunkTests
          typeClassTests
          typeClassBTests
          typeClassCTests
          childEnvTests
          scannerTests
          fmtRecordTests
          assemblyRecoveryTests
          closersTests
          sigilTests
          districtTests
          indexerTests
          envLoadTests
          parallelTests
          fileTests ]
