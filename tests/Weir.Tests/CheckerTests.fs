module CheckerTests

// The checker suites. A suite sits in the file of the area it mostly
// exercises; Tests.fs lists every suite for the runner.

open System.Diagnostics
open System.IO
open Expecto
open Weir.Ast
open Weir.Types
open Weir.Argv
open Weir.Check
open Weir.Eval
open TestPrelude
open ParserTests

let checkerTests =
    testList "Check" [
        test "acceptance pipeline type-checks to seq<FileRow>" {
            Expect.equal (checkOk acceptance).Ty Weir.Builtins.seqFileRow ""
        }
        test "update of a non-record blames the SOURCE, not the field [D:update-span]" {
            let input = "{ Http.get with url = \"y\" }"
            let terr = checkErr input
            Expect.equal terr.Span.Start.Col 3 "the caret sits on Http.get"
            Expect.equal terr.Span.End.Col 11 "…and covers it"
            Expect.stringContains terr.Message "only records have updatable fields" "the diagnosis"
            Expect.stringContains terr.Message "apply it first" "a function in update position names the repair"

            let terr2 = checkErr "{ 5 with url = \"y\" }"
            Expect.equal terr2.Span.Start.Col 3 "the non-record literal is blamed"
            Expect.isFalse (terr2.Message.Contains "apply it first") "the repair hint is the function case's own"
        }
        test "a DEEP non-record hop blames the previous field, not the assignment [D:update-span]" {
            // r.answered is int — updating r.answered.x must blame
            // 'answered' (the thing that is not a record)
            let te = env |> declare "type R = { answered: int }"
            let input = "let r = R { answered = 1 } in { r with answered.x = 2 }"

            match Weir.Check.typecheck te (parse input) with
            | Error terr ->
                let expectedStart = input.IndexOf "answered.x" + 1
                Expect.equal terr.Span.Start.Col expectedStart "the caret sits on the non-record hop"
                Expect.stringContains terr.Message "only records have updatable fields" ""
            | Ok _ -> failtest "expected a type error"
        }
        test "typo in field is rejected with exact span and a hint" {
            let input = "ls |> where (fun f -> f.bytse > 1048576) |> take 5"
            let terr = checkErr input
            let expectedStart = input.IndexOf "bytse" + 1
            Expect.equal terr.Span.Start.Col expectedStart "start col"
            Expect.equal terr.Span.End.Col (expectedStart + 5) "end col"
            Expect.stringContains terr.Message "FileRow has no field 'bytse'" ""
            Expect.stringContains terr.Message "Did you mean 'bytes'?" ""
        }
        test "lambda body of wrong type reports expected vs actual" {
            let terr = checkErr "ls |> where (fun f -> f.bytes) |> take 5"
            Expect.stringContains terr.Message "expected bool, got Size" ""
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
        }
    ]

let rejectedAtCheckTests =
    testList "Rejected at check time, never at eval" [
        test "unbound variable" { checkErr "nope" |> ignore }
        test "applying a non-function" { checkErr "1 2" |> ignore }
        test "string plus int" { checkErr "\"a\" + 1" |> ignore }
        test "field typo in a pipeline" { checkErr "ls |> where (fun f -> f.bytse > 1)" |> ignore }
        test "wrong argument to a builtin" { checkErr "double \"x\"" |> ignore }
        test "piping a seq into an int function" { checkErr "ls |> double" |> ignore }
        test "let-bound bare lambda cannot infer when nothing in the statement decides it [D:plus-defer]" {
            checkErr "let add = fun a -> fun b -> a + b in add" |> ignore
        }
    ]

let declTests =
    testList "Type declarations" [
        test "union declares constructors as typed values" {
            Expect.equal (checkOk "Running 5").Ty (TNamed("Job", [])) "payload ctor applies"
            Expect.equal (checkOk "Stopped").Ty (TNamed("Job", [])) "nullary ctor is a value"
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
            expectValue "{ X = 1; Y = 2 }" (VRecord("Point", [ "X", VInt 1; "Y", VInt 2 ]))
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
        test "redeclaration is allowed" { env |> declare "type Job = Running of int | Stopped" |> ignore }
    ]

let matchTests =
    testList "Match" [
        test "match on a union types and evaluates" {
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
            Expect.stringContains terr.Message "Job has no case 'Leaf'" ""
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
        }
    ]

let rowTests =
    testList "Row polymorphism" [
        test "field projection lambda infers a row type" {
            match (checkOk "fun f -> f.readOnly").Ty with
            | TFun(TRowVar(_, [ "readOnly", TVar _ ]), TVar _) -> ()
            | t -> failtest $"expected a row-typed projection, got {formatTy t}"
        }
        test "an Eq constraint rides a row var (mutation-spike survivor)" {
            // demanding Eq on a still-open row must pend, not refuse —
            // discharge happens when the row resolves [D:inferred-type-classes]
            (checkOk "fun r -> r.A == 1 && r == r").Ty |> ignore
        }
        test "field usage constrains the row" {
            match (checkOk "fun f -> f.bytes > 1MiB").Ty with
            | TFun(TRowVar(_, [ "bytes", TSize ]), TBool) -> ()
            | t -> failtest $"expected {{ bytes: Size; .. }} -> bool, got {formatTy t}"
        }
        test "row-typed filter discharges against FileRow" {
            expectValue "let staged = where _.hidden in ls |> staged" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
        }
        test "one row-polymorphic projection reused across two record types" {
            let vecEnv = env |> declare "type Vec = { X: int; Z: int }"

            let e =
                parse (
                    "let xs = map _.X in "
                    + "let a = (Seq.initInfinite id) |> take 2 |> map (fun n -> { X = n; Y = n }) |> xs |> sum in "
                    + "let b = (Seq.initInfinite id) |> take 3 |> map (fun n -> { X = n; Z = n }) |> xs |> sum in "
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
        test "applying a variable as a function infers the arrow [D:higher-order-params]" {
            Expect.stringContains (formatTy (checkOk "fun f -> f 1").Ty) "int ->" ""
        }
        test "generalization does not capture enclosing lambda parameters" {
            checkErr "fun y -> let g = fun x -> y in (g 1 + 1) + (g 2 + \"s\")" |> ignore
        }
        test "row types survive the REPL display" {
            Expect.equal (formatTy (checkOk "where _.readOnly").Ty |> fun s -> s.Contains "readOnly") true ""
        }
    ]

let genericsTests =
    testList "Generic unions and records" [
        test "constructor schemes freshen per use" {
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
        test "Seq.groupBy lands on (key, items) pairs — F#'s shape [D:groupby-pairs]" {
            Expect.equal
                (run "[1; 2; 3; 4] |> Seq.groupBy (fun x -> x < 3) |> map fst" |> forceSeq)
                [ VBool true; VBool false ]
                "keys, first-appearance order"

            expectValue "[1; 2; 3; 4] |> Seq.groupBy (fun x -> x < 3) |> head |> snd |> sum" (VInt 3)

            // the pair destructure every F# hand writes
            expectValue
                "[\"aa\"; \"ab\"; \"b\"] |> Seq.groupBy (Str.sub 0 1) |> Seq.map (fun (k, g) -> $\"{k}:{g |> Seq.length}\") |> Str.join \",\""
                (VStr "a:2,b:1")

            // pair-currency composition: string keys feed Map.ofPairs directly
            expectValue
                "[\"aa\"; \"ab\"; \"b\"] |> Seq.groupBy (Str.sub 0 1) |> Seq.map (fun (k, g) -> (k, g |> Seq.length)) |> Map.ofPairs |> Map.get \"a\""
                (VInt 2L)

            Expect.equal
                (formatTy (checkOk "[1; 2] |> Seq.groupBy (fun x -> x)").Ty)
                "seq<int * seq<int>>"
                "type display"
        }
        test "Seq.groupBy groups by any Eq key — tuples, options, records [D:groupby-eq-keys]" {
            expectValue "[1; 2; 3; 4] |> Seq.groupBy (fun n -> (n % 2, n > 2)) |> Seq.length" (VInt 4L)

            expectValue
                "[1; 2; 3] |> Seq.groupBy (fun n -> if n > 1 then Some 0 else None) |> Seq.map (fun (k, g) -> Seq.length g) |> sum"
                (VInt 3L)

            expectValue
                "[(\"a\", Ok 1); (\"a\", Ok 1); (\"b\", Error \"e\")] |> Seq.groupBy (fun p -> p) |> Seq.map (fun (k, g) -> Seq.length g) |> Seq.max"
                (VInt 2L)
        }
    ]

let typedArgvTests =
    // check-side battery [D:typed-argv]; runtime behavior pins live in
    // e2e (Session.ScriptArgs is ambient global state)
    let argvEnv =
        // script mode = the Self module present [D:self-module]
        {
            env with
                Modules = env.Modules |> Map.add "Self" Weir.Script.selfMembers
        }

    let argvCheck input =
        Weir.Check.typecheck argvEnv (parse input)

    let argvErr input =
        match argvCheck input with
        | Ok _ -> failtest "expected the check to reject"
        | Error terr -> terr

    testList "Typed argv" [
        test "kebab derivation pins (the plan's examples)" {
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

            let shorts6, index6 = Weir.Argv.shortTables (def "S6" "type S6 = { __y: bool }")
            Expect.equal (Map.tryFind "--__y" shorts6) None "punctuation never derives [D:short-alnum]"
            Expect.equal (Map.tryFind "_" index6) None "and owns no letter"
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
                "must be string, int, float, bool"
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
        test "Positional RETURNED for signatures — inert on weir's own CLIs [D:command-signatures]" {
            // the drop's archaeology stands (weir scripts take flags;
            // its one receipt was contract-mimicry) — but a signature
            // describes a foreign tool, and foreign CLIs have operands.
            // Registered again: legal-and-inert here (the attribute
            // law), meaningful only inside .weir/sigs files
            let e = argvEnv |> declare "type WithPos = { [<Positional>] target: string }"
            Expect.isTrue (Map.containsKey "WithPos" e.Types) "declares clean"

            let terr = argvEnv |> declErr "type BadPos = { [<Positional 3>] t: string }"
            Expect.stringContains terr.Message "takes no argument" "the arg law still checks"
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

            // the flip: Default false is legal at Env (absent -> false is
            // a real statement under text bools), rejected at Args
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
                "string, int, float, bool, Duration, Size, Instant, Secret, an enum union (0-arity cases), or Option of these"
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
                        Expect.equal (Weir.Eval.recGet "LOGE_ZZQ" fs) (VUnion("Debug", None)) $"casing '{spelling}'"
                        Expect.equal (Weir.Eval.recGet "OPTE_ZZQ" fs) (VUnion("None", None)) "absent Option is None"
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

                // collect-then-raise: a bad enum and a bad int, one error
                set "PORTE_ZZQ" "nope"

                let both =
                    try
                        load () |> ignore
                        "no-raise"
                    with ex ->
                        ex.Message

                Expect.stringContains
                    both
                    "LOGE_ZZQ: not a LvlE: 'debgu' — expected one of: Debug, Info, Warn. Did you mean 'Debug'?"
                    "enum problem present"

                Expect.stringContains both "PORTE_ZZQ: not an int: 'nope'" "int problem collected alongside"

                // empty is a miss with candidates (as with ints), not None
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
                "must be string, int, float, bool"
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
            let diags = diagsIn "pin.weir" [ "print (Self.scriptPath |> Path.dir)" ]

            Expect.isEmpty diags "scripts know their own path"

            // the bare name teaches the move (the clean-break migration)
            let moved = diagsIn "pin.weir" [ "print (scriptPath |> Path.dir)" ]

            Expect.exists moved (fun d -> d.Message.Contains "use 'Self.scriptPath'") "bare name teaches Self"
        }
        test "scriptPath coexists with Args.load (no interaction)" {
            let diags =
                diagsIn "pin.weir" [
                    "type Cli = { quiet: bool }"
                    "let cli = Args.load Cli"
                    "print (Self.scriptPath |> Path.dir)"
                    "print $\"{show cli.quiet}\""
                ]

            Expect.isEmpty diags "both boundary reads in one script"
        }
        test "the Self module: members type in scripts; bare names teach the move [D:self-module]" {
            let clean lines =
                let diags = diagsIn "pin.weir" lines
                Expect.isEmpty (diags |> List.filter (fun d -> d.Severity = "error")) $"types: {lines}"

            clean [ "let n = Self.pid + 1"; "print n" ] // pid is int (arithmetic)
            clean [ "Self.args |> Seq.iter print" ]
            clean [ "Self.stdin |> Seq.length |> print" ]
            clean [ "print (Self.scriptPath |> Path.dir)" ]

            // clean break: every bare name teaches its Self home
            let teaches name (lines: string list) =
                let diags = diagsIn "pin.weir" lines

                Expect.exists
                    diags
                    (fun d -> d.Message.Contains $"use 'Self.{name}'")
                    $"bare '{name}' teaches Self.{name}"

            // in expression position (bare at statement head is a command
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
        }
    ]

let seqPatternTests =
    testList "Seq patterns" [
        test "the four shapes bind and select" {
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
        test "a chained cons names the short seqs it leaves out, not _ :: _ [D:seq-patterns]" {
            Expect.equal
                (checkErr "match [1] |> Seq.map id with | [] -> 0 | a :: b :: rest -> a + b").Message
                "match is not exhaustive; missing: [_]"
                "the one-element seq"

            Expect.equal
                (checkErr "match [1] |> Seq.map id with | a :: b :: c :: rest -> a").Message
                "match is not exhaustive; missing: [], [_], [_; _]"
                "every length below the chain"

            expectValue "match [1] |> Seq.map id with | [] -> 0 | [a] -> a | a :: b :: _ -> a + b" (VInt 1L)
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
                "seq patterns need a seq value"
                ""
        }
        test "element types flow: string elems reject int literals in element position" {
            Expect.stringContains
                (checkErr "match [\"a\"] with | [1] -> 1 | _ -> 0").Message
                "int literal patterns need an int value"
                ""
        }
        test
            "the bare-comma precedence footgun: `code, _ :: rest` vs a seq NAMES the grouping [D:user-language-messages]" {
            let m =
                (checkErr "match [\"a\"; \"b\"] with | code, _ :: rest -> code | _ -> \"z\"").Message
            // FParsec wraps long messages — pin fragments, not the joined sentence
            Expect.stringContains m "groups looser than" "names the precedence cause"
            Expect.stringContains m "(code, _) :: rest" "names the repair, not the category"
        }
        test "the did-you-mean fires ONLY on the unambiguous shape — a plain tuple vs a seq stays quiet" {
            let m = (checkErr "match [\"a\"; \"b\"] with | a, b -> a | _ -> \"z\"").Message
            Expect.stringContains m "tuple patterns need a tuple value" "the plain message"
            Expect.isFalse (m.Contains "groups looser than") "no precedence hint on a non-cons tuple pattern"
        }
        test "a tuple pattern types an unresolved value as a tuple of its arity (B21)" {
            expectValue "let f = function | (a, b) -> $\"{a}{b}\" in f (1, \"x\")" (VStr "1x")
            expectValue "let f = fun (a, b) -> a in f (1, 2)" (VInt 1L)

            expectValue "let g p = match p with | (0, s) -> s | (n, s) -> $\"{n}{s}\" in g (1, \"x\")" (VStr "1x")

            expectValue "let m o = match o with | Some (a, b) -> b | None -> 0 in m (Some (1, 2))" (VInt 2L)
            // the arity is fixed by the pattern
            checkErr "let h p = match p with | (a, b) -> a in h (1, 2, 3)" |> ignore
        }
        test "no user-facing pattern message says 'scrutinee' (the vocabulary sweep)" {
            // a representative spread across the moved messages (bool, int
            // literal, seq, unit) — none may carry the jargon
            for input in
                [
                    "match 5 with | [] -> 0 | _ -> 1" // seq vs int
                    "match 5 with | () -> 0 | _ -> 1" // unit vs int
                    "match [\"a\"] with | [1] -> 1 | _ -> 0"
                ] do // int-lit vs string
                match typecheck env (parse input) with
                | Error e -> Expect.isFalse (e.Message.Contains "scrutinee") $"jargon leaked: {e.Message}"
                | Ok _ -> failtest $"expected a type error for: {input}"
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
                    "match ls with | [] -> 0 | [a] -> Size.toBytes a.bytes | [p; q] -> Size.toBytes p.bytes | x :: rest -> Size.toBytes x.bytes + (rest |> Seq.map (fun f -> Size.toBytes f.bytes) |> Seq.sum)"
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
        }
    ]

let effectPartitionTests =
    // Stage 2 of [D:pure]: the ambient/mutation partition [D:pure-stage2]
    // — effectClass over the effect-label table, plus the per-method net
    // split, consultable at both check time (Effects.effectClass) and
    // eval time (Builtins.effectClassOfCall). This is [PLAN-plan-apply]'s
    // load-bearing dependency: probe it directly, both ends.
    let A = Weir.Effects.Ambient
    let M = Weir.Effects.Mutation

    // a minimal HttpRequest value carrying a method case — the shape the
    // interpreter holds at an Http.send call site
    let reqWith (methodCase: string) =
        VRecord(
            "HttpRequest",
            [
                "method", VUnion(methodCase, None)
                "url", VStr "http://x"
                "auth", VUnion("NoAuth", None)
                "headers", VSeq Seq.empty
                "secretHeaders", VSeq Seq.empty
                "body", VUnion("NoBody", None)
                "timeout", VDur 30000L
                "insecure", VBool false
            ]
        )

    testList "the ambient/mutation partition [D:pure-stage2]" [
        test "effectClass per label — the fixed-class names split as the table says" {
            // ambient input (reads, changes nothing)
            for n in
                [
                    "File.read"
                    "File.size"
                    "Dir.list"
                    "Path.glob"
                    "Env.get"
                    "Args.load"
                    "Instant.now"
                    "Self.stdin"
                    "Net.portOpen"
                    "Http.query"
                ] do
                Expect.equal (Weir.Effects.effectClass n) (Some A) $"{n} is ambient input"
            // external mutation (changes the world)
            for n in
                [
                    "File.write"
                    "File.append"
                    "File.copy"
                    "Dir.create"
                    "Dir.delete"
                    "Dir.deleteAll"
                    "Proc.stop"
                    "print"
                    "printerr"
                    "Log.info"
                    "exit"
                    "Path.newTempDir"
                ] do
                Expect.equal (Weir.Effects.effectClass n) (Some M) $"{n} is external mutation"
        }
        test "the |-name family split: library desugar vs command reifier [D:desugar-namespace]" {
            // library desugars target a plain member (Seq.iter/…) — not
            // commands, not effectful
            for k in
                [
                    "|seqIter"
                    "|seqMap"
                    "|seqFreeze"
                    "|seqAppend"
                    "|seqRange"
                    "|seqItem"
                    "|retryDefaults"
                    "|pollDefaults"
                ] do
                Expect.isFalse (Weir.Effects.isCommandReifier k) $"{k} is a library desugar, not a command"
                Expect.isFalse (Weir.Effects.effectfulName k) $"{k} reads as its pure target member"
                Expect.equal (Weir.Effects.effectClass k) None $"{k} has no effect class"

            // |seqIter resolves to its target member
            Expect.equal (Weir.Effects.libraryDesugarTarget "|seqIter") (Some "Seq.iter") "the desugar names its target"

            Expect.equal
                (Weir.Effects.libraryDesugarTarget "|orFailed")
                None
                "a command reifier is not a library desugar"

            // command reifiers stay spawns (effectful, mutation)
            for k in [ "|completed"; "|succeeded"; "|orFailed"; "|print"; "|exitCoded" ] do
                Expect.isTrue (Weir.Effects.isCommandReifier k) $"{k} targets a spawn"
                Expect.isTrue (Weir.Effects.effectfulName k) $"{k} is effectful"
                Expect.equal (Weir.Effects.effectClass k) (Some M) $"{k} is external mutation (a command)"
        }
        test "Http.send is method-dependent — effectClass alone cannot place it (None)" {
            Expect.equal (Weir.Effects.effectClass "Http.send") None "the name defers to the request value"
        }
        test "the net split: query methods are ambient, mutating verbs are mutation" {
            for m in [ "GET"; "HEAD"; "OPTIONS"; "QUERY"; "Get"; "get" ] do
                Expect.equal (Weir.Effects.httpMethodClass m) A $"{m} is idempotent → ambient"

            for m in [ "POST"; "PUT"; "DELETE"; "PATCH"; "Post"; "patch" ] do
                Expect.equal (Weir.Effects.httpMethodClass m) M $"{m} mutates → mutation"
        }
        test "an Other verb is mutation unless it is a safe verb, any casing [D:http-safe-verbs]" {
            for m in [ "PURGE"; "purge"; "LINK"; "post"; "MKCOL"; "" ] do
                Expect.equal (Weir.Effects.httpMethodClass m) M $"Other \"{m}\" → mutation"

            for m in [ "TRACE"; "trace"; "get"; "Head"; "options"; "query" ] do
                Expect.equal (Weir.Effects.httpMethodClass m) A $"Other \"{m}\" → ambient"

            let other m =
                match reqWith "Get" with
                | VRecord(n, fs) ->
                    VRecord(
                        n,
                        fs
                        |> List.map (fun (k, v) ->
                            if k = "method" then
                                k, VUnion("Other", Some(VStr m))
                            else
                                k, v)
                    )
                | v -> v

            Expect.equal
                (Weir.Builtins.effectClassOfCall "Http.send" [ other "PURGE" ])
                (Some M)
                "send{Other PURGE} mutates"

            Expect.equal (Weir.Builtins.effectClassOfCall "Http.send" [ other "get" ]) (Some A) "send{Other get} reads"
        }
        test "EVAL-TIME resolution: Http.send{post} = Mutation, Http.send{get} = Ambient (probe 2)" {
            // the critical probe — plan/apply intercepts at eval; the
            // class of a builtin call must resolve where the interpreter
            // runs, from the runtime request Value
            Expect.equal (Weir.Builtins.effectClassOfCall "Http.send" [ reqWith "Post" ]) (Some M) "send{post} mutates"
            Expect.equal (Weir.Builtins.effectClassOfCall "Http.send" [ reqWith "Get" ]) (Some A) "send{get} reads"

            Expect.equal
                (Weir.Builtins.effectClassOfCall "Http.send" [ reqWith "Delete" ])
                (Some M)
                "send{delete} mutates"

            Expect.equal
                (Weir.Builtins.effectClassOfCall "Http.send" [ reqWith "Query" ])
                (Some A)
                "send{query} is idempotent"
        }
        test "Http.expect mirrors send [D:http-dx]: None by name, per-method at eval" {
            Expect.equal (Weir.Effects.effectClass "Http.expect") None "the name defers to the request value"

            Expect.equal
                (Weir.Builtins.effectClassOfCall "Http.expect" [ reqWith "Post" ])
                (Some M)
                "read{post} mutates"

            Expect.equal (Weir.Builtins.effectClassOfCall "Http.expect" [ reqWith "Get" ]) (Some A) "read{get} reads"
        }
        test "Form urlencodes with the shared escaper [D:http-dx]" {
            Expect.equal
                (Weir.Http.formUrlEncode [ "grant type", "a&b"; "x", "y z" ])
                "grant%20type=a%26b&x=y%20z"
                "keys and values percent-encode; pairs join with &"
        }
        test "eval-time resolution agrees with the name map for fixed-class calls" {
            Expect.equal (Weir.Builtins.effectClassOfCall "File.write" []) (Some M) "fs.write is mutation at eval"
            Expect.equal (Weir.Builtins.effectClassOfCall "File.read" []) (Some A) "fs.read is ambient at eval"

            Expect.equal
                (Weir.Builtins.effectClassOfCall "Http.query" [])
                (Some A)
                "the query method is ambient at eval"

            Expect.equal (Weir.Builtins.effectClassOfCall "Str.trim" []) None "a pure builtin has no class"
        }
        test "the partition is TOTAL over every classified-effectful name effectPhrase names" {
            // every name the teaching vocabulary classifies effectful
            // must also get a class — the two cannot drift (Http.send is
            // the one deliberate None, resolved per-request)
            let names = [
                "File.read"
                "File.write"
                "Dir.create"
                "Dir.list"
                "Env.get"
                "Args.load"
                "Proc.stop"
                "Net.portOpen"
                "Http.query"
                "Log.info"
                "print"
                "printerr"
                "exit"
                "ls"
                "glob"
                "Path.glob"
                "Path.tempRoot"
                "Path.newTempDir"
                "Instant.now"
                "Duration.sleep"
                "Self.stdin"
            ]

            for n in names do
                Expect.isSome (Weir.Effects.effectClass n) $"{n} is classified"
        }
    ]

let reserveBuiltinTests =
    // builtins with no qualified spelling are reserved binders
    // [D:reserve-builtins]; bare aliases keep the standing shadow rule
    testList "reserved builtins" [
        test "the message, verbatim — every binding position" {
            let expected =
                "'pwd' is a builtin with no qualified spelling — a binding would shadow it for the whole file with no way back; pick another name"

            Expect.equal (checkErr "let pwd = 1 in pwd").Message expected "let"

            Expect.stringContains
                (checkErr "[1] |> Seq.map (fun pwd -> pwd)").Message
                "'pwd' is a builtin"
                "lambda param"


            Expect.stringContains
                (checkErr "match (1, 2) with | (cd, _) -> cd").Message
                "'cd' is a builtin"
                "pattern binder"
        }
        test "the positive twins: ordinary and module-shaped names bind; aliases keep their escape" {
            expectValue "let file = 1 in file" (VInt 1L)
            expectValue "let seq = 2 in seq" (VInt 2L)
            // the alias distinction is the ruling's edge: Seq.max is
            // the way back, so `max` stays bindable
            expectValue "let max = 3 in max + Seq.max [1; 2]" (VInt 5L)
            // the escape-bearer, not an alias [D:dir-stat]: Dir.stat
            // "." is ls's way back, so ls binds
            expectValue "let ls = 3 in ls" (VInt 3L)
        }
        test "into is deleted: an ordinary unbound, bindable name [D:into-deleted]" {
            expectValue "let into = 4 in into" (VInt 4L)
            Expect.stringContains (checkErr "[\"a\"] |> into \"cat\"").Message "into" "unbound into"
        }
    ]

let boolBranchTests =
    testList "Bool branching" [
        test "if-else parses" { expectParse "if 1 > 2 then \"a\" else \"b\"" "(if (> 1 2) \"a\" \"b\")" }
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
            let te = checkOk "fun f -> if f.hidden then f.bytes else 0B"

            Expect.equal
                (Weir.Check.typecheck env (parse "ls |> Seq.map (fun f -> if f.hidden then f.bytes else 0B)")
                 |> Result.isOk)
                true
                "discharges against FileRow"

            match te.Ty with
            | TFun(TRowVar _, TSize) -> ()
            | t -> failtest $"expected a row-constrained function, got {formatTy t}"
        }
        test "branch-merged row constraints conflict at discharge, not before" {
            // pre-discharge: both fields legally share one row variable
            let te = checkOk "fun f -> if f.hidden then f.name else f.bytes"

            match te.Ty with
            | TFun(TRowVar _, TVar _) -> ()
            | t -> failtest $"expected a row-constrained function, got {formatTy t}"

            // discharge against FileRow exposes the Name/bytes conflict
            let terr = checkErr "ls |> Seq.map (fun f -> if f.hidden then f.name else f.bytes)"

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
            Expect.stringContains (formatError terr) "bool patterns need a bool value" ""
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
        }
    ]

let casingTests =
    testList "The casing law (lowercase binds)" [
        test "every binder position rejects uppercase (POSITIONS ride)" {
            for bad in
                [
                    "let Foo = 1 in Foo"
                    "let Foo x = x in Foo"
                    "fun X -> X"
                    "let f X = X in f"
                    "let (A, b) = (1, 2) in b"
                    "let API, x = (1, 2) in x"
                ] do
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
        test "diagnostic codes say what they are [D:diagnostic-code-names]" {
            let cases = [
                [ "let Total = 1"; "print (show Total)" ], "lowercase-binding"
                [ "let f () ="; "    1"; "    print \"x\""; "f ()" ], "non-unit-statement"
                [ "let xs = [(1, 2)] |> Seq.sortBy (fun x -> x)"; "print (show xs)" ], "unsortable-key"
                [ "let render x = show x"; "print (render print)" ], "unrenderable-function"
                [ "print (show (fail \"x\"))" ], "undetermined-type"
                [
                    "let x = match \"a\" with | Regex @\"(\\d+)-(\\d+)\" a -> a | _ -> \"\""
                    "print x"
                ],
                "regex-binder-count"
                [ "let _x = 1"; "  let y = 2" ], "layout"
                [ "module M"; "let f : int -> int" ], "missing-implementation"
                [ "module M"; "let f : int -> int"; "/// doc"; "let f n = n" ], "doc-on-implementation"
                [ "module M"; "echo hi" ], "module-declares-only"
            ]

            for lines, code in cases do
                Expect.equal (firstErrorIn "codes.weir" lines).Code code (String.concat " / " lines)

            Expect.stringContains
                (firstErrorIn "codes.weir" [ "let Total = 1"; "print (show Total)" ]).Message
                "(a record field keeps its name: let level = cfg.LOG_LEVEL)"
                "the casing example"
        }
        test "match patterns untouched: uppercase is still a constructor" {
            expectValue "match Running 1 with | Running n -> n | Stopped -> 0" (VInt 1L)
        }
        test "() param is not a casing case" { expectValue "(fun () -> 7) ()" (VInt 7L) }
    ]

let literalThunkTests =
    testList "Literal patterns & () thunks" [
        test "int literal patterns dispatch" {
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
            Expect.stringContains (formatError terr) "need a string value" ""
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
            // the "()" name is unforgeable; body sees the outer x
            expectValue "let x = 5 in let f () = x in f ()" (VInt 5L)
        }
    ]

let typeClassTests =
    testList "Type classes: Eq (Session A)" [ // the payoff shape: user code generic over equality
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
            let terr =
                checkErr "let same x y = x == y in same (Seq.initInfinite id) (Seq.initInfinite id)"

            Expect.stringContains (formatError terr) "requires equatable values" ""
        }
        test "concrete failures keep the pre-class message verbatim" {
            Expect.stringContains
                (checkErr "(Seq.initInfinite id) == (Seq.initInfinite id)").Message
                "'==' is not defined for seq<int>"
                ""
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

            let e =
                parse
                    "let eqr = fun a -> fun b -> a == b in eqr { S = (Seq.initInfinite id) } { S = (Seq.initInfinite id) }"

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
                | Ok(te, cs, _, _) ->
                    let sch = Weir.Types.generalizeWith cs te.Ty
                    Expect.isTrue (sch.Cs |> Map.exists (fun _ s -> s.Contains Weir.Types.Cls.Eq)) "Eq rides"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "erasure: a constrained closure partially applies like any other" {
            expectValue "let same x y = x == y in let s5 = same 5 in s5 5" (VBool true)
        }
        // value equality is iterative [D:eq-depth]: a legally-built
        // recursive-record value (an Option-linked record folded deep
        // via Seq.fold — the checker accepts it) once crashed the whole
        // process with an uncatchable StackOverflow on `==`. The walk is
        // now an explicit heap work-list; merely reaching these asserts
        // is the pin (an overflow aborts the runner, it cannot be
        // caught). Depths are past the recursive walk's crash point.
        test "a 200k-deep VRecord chain compares equal iteratively, no overflow [D:eq-depth]" {
            let build () =
                [ 1..200000 ]
                |> List.fold
                    (fun acc i -> VRecord("Node", [ "depth", VInt(int64 i); "next", VUnion("Some", Some acc) ]))
                    (VRecord("Node", [ "depth", VInt 0L; "next", VUnion("None", None) ]))

            Expect.isTrue (build().Equals(build ())) "two independent 200k-deep chains are structurally equal"
        }
        test "deep-vs-shallow VRecord is false, drained not crashed [D:eq-depth]" {
            let chain n =
                [ 1..n ]
                |> List.fold
                    (fun acc i -> VRecord("Node", [ "depth", VInt(int64 i); "next", VUnion("Some", Some acc) ]))
                    (VRecord("Node", [ "depth", VInt 0L; "next", VUnion("None", None) ]))

            Expect.isFalse ((chain 200000).Equals(chain 100000)) "different depths mismatch and drain the work-list"
        }
        test "a 200k-element VSeq compares LOCKSTEP true; a tail mismatch is false [D:eq-depth]" {
            let xs () =
                VSeq(seq { for i in 1..200000 -> VInt(int64 i) })

            Expect.isTrue ((xs ()).Equals(xs ())) "lockstep enumerators, no double materialization"

            let ys =
                VSeq(seq { for i in 1..200000 -> VInt(if i = 200000 then -1L else int64 i) })

            Expect.isFalse ((xs ()).Equals(ys)) "a single tail element differs → false"
        }
        test "deep value renders FINITE through show, no overflow [D:eq-depth]" {
            let deep =
                [ 1..200000 ]
                |> List.fold
                    (fun acc i -> VRecord("Node", [ "depth", VInt(int64 i); "next", VUnion("Some", Some acc) ]))
                    (VRecord("Node", [ "depth", VInt 0L; "next", VUnion("None", None) ]))

            let rendered = Weir.Eval.formatValue deep
            Expect.stringContains rendered "…" "the depth bound teaches with an ellipsis rather than crashing"
        }
    ]

let typeClassBTests =
    testList "Type classes: Show + Ord (Session B)" [
        test "generic show generalizes: Show a => a -> string" {
            expectValue "let render x = show x in render 42" (VStr "42")
        }
        test "Show is wider than Eq: seqs render" {
            expectValue "let render x = show x in (render [1; 2] |> Str.length) > 0" (VBool true)
        }
        test "Show rejects functions at the use site (legacy message kept)" {
            let terr = checkErr "let render x = show x in render print"
            Expect.stringContains (formatError terr) "cannot render functions" ""
        }
        test "Ord: string keys sort" { expectValue "[\"b\"; \"a\"] |> Seq.sortBy (fun s -> s) |> Seq.head" (VStr "a") }
        test "Ord: record key is a CHECK-time error with the contract message" {
            let terr = checkErr "ls |> Seq.sortBy (fun f -> f)"

            Expect.stringContains
                (formatError terr)
                "cannot be ordered — keys are int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, or a record or union numbered with [<Ord n>]"
                ""
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
            // show accepts seqs, == does not — the var carries both and
            // the strictest class decides
            let terr = checkErr "let f x y = (show x == show y) && x == y in f [1] [1]"
            Expect.stringContains (formatError terr) "requires equatable values" ""
        }
    ]

let typeClassCTests =
    testList "Type classes: hardening (Session C)" [
        test "Eq x generic unions: two levels of Option accept" {
            expectValue "let same x y = x == y in same (Some (Some 1)) (Some (Some 1))" (VBool true)
        }
        test "Eq x generic unions: function payload rejects through two levels" {
            let terr =
                checkErr "let same x y = x == y in same (Some (Some print)) (Some (Some print))"

            Expect.stringContains (formatError terr) "requires equatable values" ""
        }
        test "Eq x generic records: the reachability correction (fn field via instantiation)" {
            // Session A scoped fn-field records as undeclarable; generic
            // instantiation reaches them
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
                parse
                    "let eqr = fun a -> fun b -> a == b in eqr { S = (Seq.initInfinite id) } { S = (Seq.initInfinite id) }"

            match Weir.Check.typecheck holderEnv bad with
            | Ok _ -> failtest "seq-carrying record must reject"
            | Error terr -> Expect.stringContains terr.Message "requires equatable values" ""
        }
        test "constraint x mergeRows: unified constrained rows still fire" {
            let holderEnv = env |> declare "type Holder = { S: seq<int> }"

            let e =
                parse
                    "let f = fun a -> fun b -> (a == b) && (a == a) in f { S = (Seq.initInfinite id) } { S = (Seq.initInfinite id) }"

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

            // Show did not widen print: a seq<int> still cannot go to print directly
            let terr = checkErr "print [1]"
            Expect.stringContains (formatError terr) "print" ""
        }
    ]

let envLoadTests =
    testSequenced
    <| testList "Typed Env" [
        test "all-good load with every scalar and Option" {
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
                        Expect.equal (Weir.Eval.recGet "WT_I" fs) (VInt 42L) "int parsed"
                        Expect.equal (Weir.Eval.recGet "WT_B" fs) (VBool false) "bool parsed"
                        Expect.equal (Weir.Eval.recGet "WT_O" fs) (VUnion("None", None)) "absent Option is None"
                    | v -> failtest $"unexpected {formatValue v}"
                | Error terr -> failtest (formatError terr)
            finally
                for n in [ "WT_S"; "WT_I"; "WT_B" ] do
                    System.Environment.SetEnvironmentVariable(n, null)
        }
        test "Env.load HONOURS [<Wire>]; Args.load rejects it [D:wire-keys]" {
            // accepted at declaration and silently ignored at the boundary — and the
            // miss named the field, reinforcing the wrong model at the exact moment
            // the author had just renamed the variable.
            System.Environment.SetEnvironmentVariable("WT_WIRE_ZZ", "hello")

            try
                let e = env |> declare "type WCfg = { [<Wire \"WT_WIRE_ZZ\">] token: string }"

                match Weir.Check.typecheck e (parse "Env.load WCfg") with
                | Ok te ->
                    match eval valueEnv te with
                    | VRecord(_, fs) ->
                        Expect.equal (Weir.Eval.recGet "token" fs) (VStr "hello") "reads the wired variable"
                    | v -> failtest $"unexpected {formatValue v}"
                | Error terr -> failtest (formatError terr)

                // exact-name: only enum values are case-insensitive, never names
                let eCase = env |> declare "type WCase = { [<Wire \"wt_wire_zz\">] token: string }"

                match Weir.Check.typecheck eCase (parse "Env.load WCase") with
                | Ok te2 ->
                    // env lookup case-sensitivity is the platform's, not weir's: weir does
                    // an exact lookup and never case-maps, but the Windows environment block
                    // is itself case-insensitive, so the same exact lookup finds WT_WIRE_ZZ
                    // there. Pinned two-sided so neither half can be 'fixed' away.
                    if System.OperatingSystem.IsWindows() then
                        match eval valueEnv te2 with
                        | VRecord(_, fs) ->
                            Expect.equal
                                (Weir.Eval.recGet "token" fs)
                                (VStr "hello")
                                "the Windows env block is case-insensitive"
                        | v -> failtest $"unexpected {formatValue v}"
                    else
                        Expect.throws (fun () -> eval valueEnv te2 |> ignore) "on POSIX a lowercase wire must NOT match"
                | Error terr -> failtest (formatError terr)

                // the miss names the variable, not the field — the defect itself
                let eMiss =
                    env |> declare "type WMiss = { [<Wire \"WT_ABSENT_ZZ\">] token: string }"

                match Weir.Check.typecheck eMiss (parse "Env.load WMiss") with
                | Ok te3 ->
                    let msg =
                        try
                            eval valueEnv te3 |> ignore
                            ""
                        with ex ->
                            ex.Message

                    Expect.isTrue (msg.Contains "WT_ABSENT_ZZ") "the miss names the VARIABLE"
                    Expect.isFalse (msg.Contains "token") "the miss must not name the field"
                | Error terr -> failtest (formatError terr)

                // [<Default>] composes with [<Wire>]
                let eDef =
                    env
                    |> declare "type WDef = { [<Wire \"WT_ABSENT_ZZ\"; Default \"fb\">] token: string }"

                match Weir.Check.typecheck eDef (parse "Env.load WDef") with
                | Ok te4 ->
                    match eval valueEnv te4 with
                    | VRecord(_, fs) ->
                        Expect.equal (Weir.Eval.recGet "token" fs) (VStr "fb") "Default fills a wired miss"
                    | v -> failtest $"unexpected {formatValue v}"
                | Error terr -> failtest (formatError terr)

                // Args.load refuses the same shape — at the call site, so a
                // Wire-carrying record stays legal for Env.load and the adapters.
                // Script mode (Self present) or Args.load refuses for that reason first.
                let scriptEnv = {
                    env with
                        Modules = env.Modules |> Map.add "Self" Weir.Script.selfMembers
                }

                let eArgs = scriptEnv |> declare "type WArgs = { [<Wire \"X\">] flag: string }"

                match Weir.Check.typecheck eArgs (parse "Args.load WArgs") with
                | Ok _ -> failtest "Args.load must refuse [<Wire>]"
                | Error terr ->
                    let m = formatError terr
                    Expect.isTrue (m.Contains "Wire") "the teaching names the attribute"
                    Expect.isTrue (m.Contains "NoShort") "the teaching names the argv naming controls"
            finally
                System.Environment.SetEnvironmentVariable("WT_WIRE_ZZ", null)
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
                    Expect.stringContains ex.Message "WT_I: not an int: 'abc'" "int problem"

                    Expect.stringContains
                        ex.Message
                        "WT_B: not a bool: 'TRUE' — exactly true or false"
                        "exact-bool problem"

                    Expect.stringContains ex.Message "WT_MISSING_ZZ is missing" "missing problem"
                | Error terr -> failtest (formatError terr)
            finally
                for n in [ "WT_I"; "WT_B" ] do
                    System.Environment.SetEnvironmentVariable(n, null)
        }
        test "every bad value reads 'NAME: not a <type>: <value>', all collected into ONE error [D:boundary-bad-value]" {
            let vars = [
                "WT_PI", "1.5"
                "WT_PF", "x"
                "WT_PN", "Infinity"
                "WT_PB", "TRUE"
                "WT_PD", "2d"
                "WT_PS", "?"
                "WT_PZ", "lots"
            ]

            for n, v in vars do
                System.Environment.SetEnvironmentVariable(n, v)

            try
                let e2 =
                    env
                    |> declare
                        "type WtAll = { WT_PI: int; WT_PF: float; WT_PN: float; WT_PB: bool; WT_PD: Duration; WT_PS: Instant; WT_PZ: Size; WT_PK: Secret }"

                match Weir.Check.typecheck e2 (parse "Env.load WtAll") with
                | Ok te ->
                    let ex = Expect.throwsC (fun () -> eval valueEnv te |> ignore) id

                    Expect.equal
                        ex.Message
                        (String.concat "; " [
                            "Env.load WtAll: WT_PI: not an int: '1.5'"
                            "WT_PF: not a float: 'x'"
                            "WT_PN: not a finite float: 'Infinity'"
                            "WT_PB: not a bool: 'TRUE' — exactly true or false"
                            "WT_PD: not a duration: '2d' — units are ms, s, m, h"
                            "WT_PS: not an ISO 8601 instant: '?' — e.g. 2026-08-14T12:34:56Z; offsets allowed, a bare date reads as midnight UTC"
                            "WT_PZ: not a size: 'lots' — expected digits then a unit (512B, 1.5MiB)"
                            "WT_PK is missing"
                        ])
                        "one shape, every field's problem in one error"
                | Error terr -> failtest (formatError terr)
            finally
                for n, _ in vars do
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
                | Error terr -> Expect.stringContains (formatError terr) "must be string, int, float, bool" bad
                | Ok _ -> failtest $"{bad} should be rejected"
        }
        test "from-json-family errors: generic, union, unknown, non-type" {
            Expect.stringContains (formatError (checkErr "Env.load Job")) "union" ""
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
        }
    ]

let adversarialTests =
    testList "Adversarial probes" [
        test "wrong arity application" {
            Expect.stringContains (checkErr "double 1 2").Message "'double' takes at most 1 argument(s), but got 2" ""
        }
        test "over-application through a returned function applies the result (B20)" {
            expectValue "id (fun x -> x + 1) 2" (VInt 3L)
            expectValue "let t = (1, fun x -> x * 10) in (snd t) 5" (VInt 50L)
            expectValue "let t = (1, fun x -> x * 10) in 5 |> snd t" (VInt 50L)

            expectValue "let handlers = Map.ofPairs [ (\"k\", fun x -> x + 1) ] in Map.get \"k\" handlers 4" (VInt 5L)

            // a result that is not a function keeps the arity teaching
            Expect.stringContains
                (checkErr "let f x = x in f 1 2").Message
                "'f' takes at most 1 argument(s), but got 2"
                ""
        }
        test "checker messages name what is actually wrong (B38)" {
            Expect.stringContains
                (checkErr "Some [1] < None").Message
                "operator '<' is not defined for Option<seq<int>>"
                "not a mismatch"

            Expect.stringContains (checkErr "show []").Message "a rendering requirement" "Show is not equality"

            Expect.stringContains
                (checkErr "[] |> Seq.sort |> Seq.length").Message
                "an ordering requirement"
                "Ord is not equality"

            Expect.stringContains
                (checkErr "[] |> Seq.distinct |> Seq.length").Message
                "an equality requirement"
                "Eq keeps its word"

            Expect.isFalse (Weir.Check.budgetMsgSized.Contains "annotate") "weir has no annotations to offer"

            let errs lines =
                diagsIn (tempPath "weir-b38" + ".weir") lines
                |> List.filter (fun d -> d.Severity = "error")
                |> List.map (fun d -> d.Message)

            let m =
                errs [
                    "let pl ="
                    "    plan"
                    "        File.write \"x\" [\"a\"]"
                    "print $\"{pl.ops}\""
                ]

            Expect.exists
                m
                (fun s -> s.Contains "Plan has no fields — it is an opaque value")
                $"opaque, not unknown: {m}"

            let p = errs [ "let p ="; "    yaml patch"; "        a: 1"; "print $\"{p}\"" ]
            Expect.exists p (fun s -> s.Contains "a patch does not render") $"no functions wording: {p}"

            let j =
                errs [
                    "within proc srv = sh -c \"sleep 1\""
                    "    [srv] |> to json |> Seq.iter print"
                ]

            Expect.exists
                j
                (fun s -> s.Contains "Proc is an opaque value with no JSON form")
                $"opaque at the boundary, not unknown: {j}"

            match errs [ "import \"./weir-b38-nope.weir\" as N"; "print (N.f 1)" ] with
            | [ only ] -> Expect.stringContains only "cannot resolve import" "the import's own error"
            | other -> failtest $"a failed import must not echo 'no module named': {other}"
        }
        test "shadowing with a different type is respected" {
            Expect.stringContains (checkErr "let x = 1 in let x = \"s\" in x + 1").Message "expected string, got int" ""

            expectValue "let x = 1 in let x = \"s\" in x + \"!\"" (VStr "s!")
        }
        test "inferred element type contradicts use two stages later" {
            let terr =
                checkErr "(Seq.initInfinite id) |> map (fun x -> x * x) |> where (fun x -> x.readOnly)"

            Expect.stringContains terr.Message "only records have fields" ""
        }
        test "row constraint conflicts with the declared field type" {
            Expect.stringContains
                (checkErr "let g = fun r -> r.name > 1 in ls |> where g").Message
                "expected string, got int"
                ""
        }
        test "piping a lambda as data into an int function is rejected" { checkErr "(fun x -> x) |> double" |> ignore }
        test "piping a constructor as data is rejected" { checkErr "Running |> double" |> ignore }
        test "field access on a union is rejected toward the eval-unreachable arm" {
            Expect.stringContains (checkErr "match Stopped with | p -> p.Sze").Message "Job is a union" ""
        }
        test "match scrutinee cannot be a raw constructor pattern target mismatch" {
            Expect.stringContains
                (checkErr "match 5 with | Running n -> n | _ -> 0").Message
                "constructor patterns need a union"
                ""
        }
        test "1.1 self-application rejects without hanging" {
            // [D:higher-order-params]: funParams no longer masks this as
            // "not a function" — the occurs check catches the cycle
            // directly (and still terminates to reach this pin)
            Expect.stringContains (checkErr "fun f -> f.x f").Message "infinite type" ""
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
                (checkErr "ls |> where (fun f -> f.bytes > 1B) |> where (fun f -> f.Nonexistent == 1)").Message
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
                    + "let a = (Seq.initInfinite id) |> take 2 |> map (fun n -> { V = n; Tag = true }) |> getV |> sum in "
                    + "let b = (Seq.initInfinite id) |> take 1 |> map (fun n -> { V = \"s\"; Alt = true }) |> getV in "
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
    ]

let ambiguousCtorTests =
    let analyze (lines: string list) =
        let p = tempPath "weir-amb" + ".weir"

        System.IO.File.WriteAllLines(p, lines)

        try
            let ds = diagsIn p (List.ofArray (System.IO.File.ReadAllLines p))

            ds |> List.filter (fun d -> d.Severity = "error")
        finally
            System.IO.File.Delete p

    testList "Ambiguous constructors [D:ambiguous-ctor]" [
        test "a bare case declared by two unions is refused, naming both" {
            match analyze [ "type B = C"; "type Z = C"; "let x = C"; "print \"n\"" ] with
            | [ d ] ->
                Expect.equal d.Line 3 "the USE site, not the second declaration"

                Expect.equal
                    d.Message
                    "ambiguous constructor 'C'; it is declared by: B, Z — rename one of the cases"
                    "exact text, both candidates, and a repair"
            | other -> failtest $"expected one error, got {other.Length}"
        }
        // arity is not a disambiguator: resolving a constructor by how it is
        // applied is overload resolution, which this language declines. Both
        // spellings are ambiguous, and the message does not vary with them.
        test "arity does not disambiguate — bare or applied, both refuse" {
            match analyze [ "type B = C of int"; "type Z = C"; "let x = C"; "print \"n\"" ] with
            | [ d ] -> Expect.stringContains d.Message "ambiguous constructor 'C'" "bare use"
            | other -> failtest $"bare: expected one error, got {other.Length}"

            match analyze [ "type B = C of int"; "type Z = C"; "let x = C 1"; "print \"n\"" ] with
            | [ d ] -> Expect.stringContains d.Message "ambiguous constructor 'C'" "applied use"
            | other -> failtest $"applied: expected one error, got {other.Length}"
        }
        test "the repair works: renaming one case resolves the other" {
            Expect.isEmpty (analyze [ "type B = D"; "type Z = C"; "let _x = C"; "print \"n\"" ]) "renamed"
        }
        test "one declaration still resolves bare" {
            Expect.isEmpty (analyze [ "type Z = C"; "let _x = C"; "print \"n\"" ]) "single owner"
        }
        test "a collision that is never USED bare still checks" {
            Expect.isEmpty (analyze [ "type B = C"; "type Z = C"; "print \"n\"" ]) "use-site rule"
        }
        // patterns need no twin: PCase resolves against the scrutinee's type,
        // never by name, and an unresolved scrutinee is already refused — so
        // the ambiguity cannot arise there. Pinned so the claim is checked
        // rather than remembered.
        test "a pattern resolves by the scrutinee's type even with a live collision" {
            // Elsewise avoids the prelude's HttpMethod.Other [D:serve-method]
            Expect.isEmpty
                (analyze [
                    "type B = C"
                    "type Z = C | Elsewise"
                    "let v = Elsewise"
                    "let s = match v with | C -> \"c\" | Elsewise -> \"other\""
                    "print s"
                ])
                "type-directed"
        }
        // the half that already worked, guarded: imported types live flat in
        // env.Types so signatures and field access resolve, but their cases
        // are not in scope bare — so an import must not make a local
        // declaration look ambiguous. The first cut of this check scanned
        // every declared type and broke exactly this.
        test "an imported union does not make a local case ambiguous" {
            withTempDir "weir-ambx" (fun dir ->
                let lib = System.IO.Path.Combine(dir, "lib.weir")
                System.IO.File.WriteAllLines(lib, [ "module Lib"; "type Status = Ok | Failed" ])
                let main = System.IO.Path.Combine(dir, "main.weir")

                System.IO.File.WriteAllLines(
                    main,
                    [
                        "import \"./lib.weir\""
                        "type Mine = Failed of int"
                        "let _s = Failed 1"
                        "print \"n\""
                    ]
                )

                let ds = diagsIn main (List.ofArray (System.IO.File.ReadAllLines main))

                Expect.isEmpty
                    (ds |> List.filter (fun d -> d.Severity = "error"))
                    "the local declaration wins; the imported case was never a bare candidate")
        }
        test "a single-owner ctor types the scrutinee; an ambiguous one still refuses [D:match-scrutinee-infer]" {
            // single owner Z: the scrutinee is Z, the match checks clean
            match
                analyze [ "type Z = C"; "let f x = match x with | C -> 1"; "print (show (f C))" ]
                |> List.filter (fun d -> d.Severity = "error")
            with
            | [] -> ()
            | ds -> failtest $"a single-owner ctor should type the scrutinee: {ds}"

            // two owners for one case name: nothing picks the union, so the
            // scrutinee stays unresolved and the teaching fires
            match
                analyze [
                    "type A = Dup"
                    "type B = Dup"
                    "let f x = match x with | Dup -> 1"
                    "print \"n\""
                ]
                |> List.filter (fun d -> d.Message.Contains "params are not typed from patterns")
            with
            | d :: _ ->
                Expect.stringContains d.Message "inline the lambda at its use site" ""
                Expect.stringContains d.Message "match on already-typed data" ""
            | [] -> failtest "an ambiguous-owner scrutinee must still be refused"
        }
        test "scrutinee inference reaches nested payloads and keeps exhaustiveness [D:match-scrutinee-infer]" {
            // a nested constructor resolves the payload's union (Option's
            // param here) across arms — the shared scrutinee var carries it
            match
                analyze [
                    "type R = Ok of int | Err"
                    "let g x = match x with | Some Err -> 0 | Some (Ok n) -> n | None -> -1"
                    "print (show (g (Some (Ok 9))))"
                ]
                |> List.filter (fun d -> d.Severity = "error")
            with
            | [] -> ()
            | ds -> failtest $"nested payload inference should check: {ds}"

            // inference types the scrutinee, then exhaustiveness still bites
            match
                analyze [
                    "type Color = Red | Green | Blue"
                    "let h x = match x with | Red -> 1 | Green -> 2"
                    "print \"n\""
                ]
                |> List.filter (fun d -> d.Code = "non-exhaustive")
            with
            | d :: _ -> Expect.stringContains d.Message "Blue" "the missing case is named after inference"
            | [] -> failtest "exhaustiveness must still fire once the scrutinee is inferred"
        }
        test "a refutable payload under an inferred scrutinee owes its catch-all (B6)" {
            // the payload resolves only after the arms are checked; a
            // payload still unresolved is never covered by refutable arms
            for src in
                [
                    "let f o = match o with | Some 0 -> \"zero\" | None -> \"none\" in f (Some 5)"
                    "let f o = match o with | Some (Some n) -> n | None -> 0 in f (Some None)"
                    "let f o = match o with | Ok (Some n) -> n | Error e -> 0 in f (Ok None)"
                    "let f o = match o with | Some [] -> 0 | None -> 1 in f (Some [1])"
                    "let f o = match o with | Some 0 -> 1 | None -> 0 in 2"
                ] do
                Expect.stringContains (checkErr src).Message "not exhaustive" src

            for src in
                [
                    "let f o = match o with | Some 0 -> 1 | Some _ -> 2 | None -> 0 in f (Some 5)"
                    "let f o = match o with | Some (Some n) -> n | Some None -> 0 | None -> 0 in f None"
                    "let f o = match o with | Ok (Some n) -> n | Ok None -> 0 | Error _ -> 1 in f (Ok None)"
                ] do
                checkOk src |> ignore
        }
    ]

let refutableRecordPatternTests =
    // refutable children [D:refutable-record-patterns]: the park's reason
    // was a mispricing — coverage over field values is never asked,
    // because a refutable child makes the pattern refutable and a
    // refutable pattern never completes a match (the literal rule)
    let renv =
        env
        |> declare "type RfIn = { deep: int }"
        |> declare "type RfOut = { inner: RfIn; pair: int * string }"
        |> declare "type RfS = { state: string; items: seq<int> }"
        |> declare "type RfO = { t: Option<string> }"

    let runR = runUnder renv

    let errR = checkErrUnder renv

    testList "refutable record patterns [D:refutable-record-patterns]" [
        test "every shape matches, each closed by a catch-all" {
            Expect.equal
                (runR "match { state = \"running\"; items = [] } with | { state = \"running\" } -> 1 | _ -> 0")
                (VInt 1L)
                "a literal under a field"

            Expect.equal
                (runR "match { t = Some \"jh\" } with | { t = Some \"jh\" } -> 1000 | _ -> 0")
                (VInt 1000L)
                "constructor + literal — the probe that started this"

            Expect.equal
                (runR "match { state = \"s\"; items = [7; 8] } with | { items = x :: rest } -> x | _ -> 0")
                (VInt 7L)
                "a seq pattern under a field"

            Expect.equal
                (runR "match { inner = { deep = 3 }; pair = (1, \"a\") } with | { inner = { deep = 3 } } -> 1 | _ -> 0")
                (VInt 1L)
                "a nested record with a literal"

            Expect.equal
                (runR "match { inner = { deep = 0 }; pair = (1, \"a\") } with | { pair = (1, y) } -> y | _ -> \"no\"")
                (VStr "a")
                "a tuple with a literal"
        }
        test "a refutable record pattern NEVER completes a match — the coverage rule does the work" {
            // the pin that proves the rule is carrying this feature: no
            // field-value analysis exists, so the arm simply never exhausts
            for src in
                [
                    "match { state = \"x\"; items = [] } with | { state = \"running\" } -> 1"
                    "match { t = None } with | { t = Some \"jh\" } -> 1"
                    "match { inner = { deep = 1 }; pair = (0, \"\") } with | { inner = { deep = 3 } } -> 1"
                ] do
                Expect.stringContains (errR src).Message "not exhaustive" $"'{src}' must not exhaust"
        }
        test "composition: a refutable arm then an irrefutable one completes" {
            Expect.equal
                (runR "match { state = \"x\"; items = [] } with | { state = \"running\" } -> 1 | { state = s } -> 2")
                (VInt 2L)
                "no wildcard needed — the same reasoning as Some 1 / Some n"
        }
        test "binder positions still demand irrefutable — by children, no second site" {
            for src in
                [
                    "let { state = \"running\" } = { state = \"x\"; items = [] } in 0"
                    "let f { state = \"running\" } = 1 in f { state = \"x\"; items = [] }"
                ] do
                Expect.stringContains (errR src).Message "can fail" $"'{src}' rejects in a binder"

            // the twin: the irrefutable binder still binds
            Expect.equal
                (runR "let { state = s } = { state = \"ok\"; items = [] } in s")
                (VStr "ok")
                "an irrefutable record binder is untouched"
        }
    ]

let recordPatternRowTests =
    // match position gets the row [D:record-pattern-rows]: the ctor law
    // was inherited, not justified — `Some` names a case from a closed
    // set (nominal identity required); a field pattern names a field
    let renv =
        env
        |> declare "type RwA = { nm: string; ag: int }"
        |> declare "type RwB = { nm: string; fl: bool }"

    let runR = runUnder renv

    let errR = checkErrUnder renv

    testList "record patterns in match position [D:record-pattern-rows]" [
        test "a match on an unresolved param types by row, and GENERALISES" {
            // the rider's motivating case: `h` does what `g` does, and
            // the only difference was which side of `match` it sat on
            Expect.equal
                (runR
                    "let h p = match p with | { nm = n } -> n in h { nm = \"a\"; ag = 1 } + h { nm = \"b\"; fl = true }")
                (VStr "ab")
                "two unrelated records through one match"
        }
        test "refutable children work on a row scrutinee, and still never complete" {
            Expect.equal
                (runR
                    "let h p = match p with | { nm = \"johnson\" } -> \"him\" | { nm = n } -> n in h { nm = \"johnson\"; ag = 0 }")
                (VStr "him")
                "the literal arm"

            Expect.equal
                (runR
                    "let h p = match p with | { nm = \"johnson\" } -> \"him\" | { nm = n } -> n in h { nm = \"zed\"; ag = 0 }")
                (VStr "zed")
                "the binding arm"

            // the coverage rule is still the thing doing the work
            Expect.stringContains
                (errR "let h p = match p with | { nm = \"j\" } -> 1 in h { nm = \"j\"; ag = 0 }").Message
                "catch-all"
                "a refutable record arm alone never completes"
        }
        test "arms ACCUMULATE fields — intersection, not union" {
            // the row gathers every mentioned field, exactly as a body
            // reading p.nm and p.ag would
            Expect.equal
                (runR "let h p = match p with | { nm = \"x\" } -> 1 | { ag = a } -> a in h { nm = \"y\"; ag = 3 }")
                (VInt 3L)
                "a record carrying both satisfies both arms"

            let missing =
                errR "let h p = match p with | { nm = \"x\" } -> 1 | { ag = a } -> a in h { nm = \"y\"; fl = true }"

            Expect.stringContains missing.Message "no field 'ag'" "a record missing one is refused"
            // the becomes-a note needs the ambient physical translator
            // (script-only), so its half is an e2e pin [D:row-provenance]
            Expect.stringContains missing.Message "Did you mean 'nm'" "with the field did-you-mean"
        }
        test "a single-owner constructor types the scrutinee [D:match-scrutinee-infer]" {
            // `Some` names exactly one union (Option), so an unresolved
            // scrutinee is typed from it — the match checks, no annotation
            match typecheck renv (parse "fun p -> match p with | Some 1 -> \"y\" | _ -> \"n\"") with
            | Ok _ -> ()
            | Error terr -> failtest $"a single-owner ctor should type the scrutinee: {formatError terr}"
        }
    ]

let pinsWalkTests =
    // the full DECISIONS-to-pins walk's cheap pins [D:pins-walk]: every
    // test here asserts a teaching message that existed only in the
    // source (the grep pass's list) — fragments, not joined sentences
    testList "pins walk: source-only teaching messages [D:pins-walk]" [
        test "groupBy refuses a non-Eq key at check [D:groupby-eq-keys]" {
            let e = checkErr "[(fun x -> x)] |> Seq.groupBy (fun f -> f) |> Seq.freeze"
            Expect.stringContains e.Message "requires equatable values" ""
        }
        test "Float.toInt out of range names the value" {
            let m =
                try
                    run "Float.toInt 1e300" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m "Float.toInt: out of int range" ""
        }
        test "Float.toInt reaches the int minimum; Float.round never yields -0.0 [D:floats]" {
            expectValue "Float.toInt -9223372036854775808.0" (VInt System.Int64.MinValue)

            Expect.stringContains
                (msgOf "Float.toInt 9223372036854775808.0")
                "out of int range"
                "2^63 is past the maximum"

            expectValue "$\"{Float.round -0.4}\"" (VStr "0.0")
            expectValue "Float.round -0.4 |> show" (VStr "0.0")
            expectValue "Float.round -0.5" (VFloat -1.0)
        }
        test "print seq<unit> teaches Seq.iter" {
            Expect.stringContains
                (checkErr "print ([1] |> Seq.map (fun x -> ()))").Message
                "a lazy effect sequence never runs; use Seq.iter"
                ""
        }
        test "a union's field access teaches match; a generic record refuses the loaders" {
            let e = env |> declare "type U2 = A2 | B2"

            let m =
                match Weir.Check.typecheck e (parse "A2.field") with
                | Error terr -> terr.Message
                | Ok _ -> failtest "field access on a union must error"

            Expect.stringContains m "is a union; match on it instead of accessing fields" ""

            let g = env |> declare "type G2<'g> = { gx: 'g }"

            match Weir.Check.typecheck g (parse "Env.load G2") with
            | Error terr -> Expect.stringContains terr.Message "needs a monomorphic record" ""
            | Ok _ -> failtest "generic Env.load must refuse"

            // Args.load is script-only in this env ("Self.args is not
            // available here" fires first) — Env.load and from json
            // carry the monomorphic-refusal class
            match Weir.Check.typecheck g (parse "[\"x\"] |> from json G2") with
            | Error terr -> Expect.stringContains terr.Message "'G2' is generic — name the instance" ""
            | Ok _ -> failtest "generic from json must refuse"
        }
        test "'to json' takes ONE value; 'to jsonl' demands a seq; unknown formats teach [D:to-jsonl]" {
            // the reshape's flagship: a scalar is a document now
            expectValue "5 |> to json" (VSeq [ VStr "5" ])

            Expect.stringContains
                (checkErr "5 |> to jsonl").Message
                "'to jsonl' needs a seq (a document per element), got int — one document is 'to json'"
                ""

            Expect.stringContains
                (checkErr "[1] |> to xml").Message
                "'to xml' does not exist — XML is read-only ('from xml T')"
                ""
        }
        test "an else-less if with a non-unit then-branch teaches add-an-else" {
            Expect.stringContains (checkErr "if true then 5").Message "an if without an else is unit-valued" ""
        }
        test "a union match missing a case NAMES it" {
            let e = env |> declare "type U3 = A3 | B3"

            match Weir.Check.typecheck e (parse "match A3 with | A3 -> 1") with
            | Error terr -> Expect.stringContains terr.Message "missing: B3" "the missing-case list"
            | Ok _ -> failtest "non-exhaustive union match must error"
        }
        test "File.move/File.size not-found shapes match the family" {
            let m1 =
                try
                    run "File.move \"/nope/a\" \"/nope/b\"" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m1 "File.move: no such file:" "the shape"
            Expect.stringContains m1 "nope" "the path named"

            let m2 =
                try
                    run "File.size \"/nope/a\"" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m2 "File.size: no such file:" "the shape"
            Expect.stringContains m2 "nope" "the path named"
        }
        test "walk candidates: parser reject sides (parens-required params, destructuring pipe-hint, range endpoints)" {
            match Weir.Parser.parseExpr "let f x, y = x in f" with
            | Error _ -> ()
            | Ok _ -> failtest "unparenthesized pattern params must reject"

            match Weir.Parser.parseStmt "let (a, b) = [1; 2] | Seq.head" with
            | Error m -> Expect.stringContains m "pipe expressions with '|>'" "the destructuring-RHS pipe-hint site"
            | Ok _ -> failtest "| before a function must error in the destructuring RHS"

            match Weir.Parser.parseExpr "[[1]..[3]]" with
            | Error _ -> ()
            | Ok _ -> failtest "a bracket can never open a range endpoint"
        }
        test "the dropped command builtins are GONE — pure subtraction, no retirement hint [D:drop-command-builtins]" {
            for name in [ "feed"; "feedEnv"; "cmdEnv"; "runEnv" ] do
                match Weir.Check.typecheck Weir.Builtins.typeEnvStrict (parse $"[\"x\"] |> {name} \"cat\"") with
                | Error terr ->
                    Expect.stringContains terr.Message "unbound" $"{name} must not resolve"
                    Expect.isFalse (terr.Message.Contains "retired") $"{name}: no retirement hint — pure subtraction"
                | Ok _ -> failtest $"'{name}' must not resolve"
        }
        test "renamed members are gone WITHOUT aliases or hints [D:duration-rename]" {
            let m = (checkErr "Duration.toS 1s").Message
            Expect.stringContains m "module Duration has no member 'toS'" "no alias survives"
            Expect.isFalse (m.Contains "Did you mean") "and no hint resurrects it"

            // Result is back as the type `|> try result` produces [D:try-form]
            Expect.equal (run "Ok 1") (VUnion("Ok", Some(VInt 1L))) "Ok constructs a Result"
        }
        test "PascalCase field access teaches the lowercase rename (the free teaching [D:builtin-fields-lowercase])" {
            // a builtin row's field, PascalCase typo (distance 1 — the
            // did-you-mean pays for the lowercase field law)
            let terr = checkErr "(ls |> Seq.head).Name"
            Expect.stringContains terr.Message "Did you mean 'name'?" ""
        }
        test "File.append's missing-parent shape matches the family [D:sized-findings]" {
            let m =
                try
                    run "File.append \"/nope/deep/x.txt\" [\"a\"]" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m "File.append: no such directory:" "the shape"
            Expect.stringContains m "deep" "the directory named"
        }
        test "the moved-into-a-module teaching carries its PREFIX (the suffix list was pinned, the prefix was not)" {
            match Weir.Check.typecheck Weir.Builtins.typeEnvStrict (parse "[1] |> map (fun x -> x)") with
            | Error terr -> Expect.stringContains terr.Message "is module-qualified here" "the prefix fragment"
            | Ok _ -> failtest "bare map must not resolve in the strict env"
        }
    ]

let ifSucceedsTests =
    // the inline command condition [D:if-succeeds] — the let-RHS
    // acceptance gate one position over; `then` terminates the chain's
    // argv only inside a condition. The real resolver (PATH heads), not
    // the bare parse helper: the gate is resolver-driven by design.
    let parseReal input =
        match Weir.Parser.parseLine realResolver input with
        | Ok(SExpr e)
        | Ok(SCmd e) -> e
        | Ok other -> failtest $"expected an expression line, got {other}"
        | Error msg -> failtest $"parse failed: {msg}"

    let runReal input =
        match Weir.Check.typecheck env (parseReal input) with
        | Ok typed -> eval valueEnv typed
        | Error terr -> failtest $"check failed: {formatError terr}"

    let checkErrReal input =
        match Weir.Check.typecheck env (parseReal input) with
        | Error terr -> terr
        | Ok _ -> failtest "expected a type error"

    testList "if cmd | succeeds then [D:if-succeeds]" [
        test "the inline form parses and evaluates, both branches" {
            skipOnWindows ()
            Expect.equal (runReal "if test -f /etc/hosts | succeeds then \"yes\" else \"no\"") (VStr "yes") ""

            Expect.equal (runReal "if test -f /nonexistent-weir-xyz | succeeds then \"yes\" else \"no\"") (VStr "no") ""
        }
        test "elif takes the inline form too (the asymmetry would be worse than neither)" {
            skipOnWindows ()

            Expect.equal
                (runReal
                    "if test -f /nonexistent-weir-xyz | succeeds then \"a\" elif test -f /etc/hosts | succeeds then \"b\" else \"c\"")
                (VStr "b")
                ""
        }
        test "bindings beat PATH at the condition head — `test` bound stays expression mode" {
            Expect.equal (runReal "let test = true in if test then \"shadow\" else \"b\"") (VStr "shadow") ""
        }
        test "`if true then` is the bool literal, never /usr/bin/true (keyword heads refuse)" {
            Expect.equal (runReal "if true then \"t\" else \"f\"") (VStr "t") ""
        }
        test "a quoted \"then\" is ordinary argv inside a condition; the STOP is the bareword only" {
            // POSIX tools drive the condition — the parse shims make
            // these resolve on Windows but not run [D:windows-v1]
            skipOnWindows ()
            // grep for the literal word then in a file that contains it
            Expect.equal
                (runReal "if grep -q \"then\" /etc/hostname | succeeds then \"found\" else \"absent\"")
                (VStr "absent")
                ""
        }
        test "a non-bool chain teaches at the CHECKER, not the parser: streaming, complete, exitCode, orFail" {
            Expect.stringContains
                (checkErrReal "if git ls-files then 1 else 2").Message
                "expected bool, got seq<string>"
                ""

            Expect.stringContains
                (checkErrReal "if git status | complete then 1 else 2").Message
                "expected bool, got Completed"
                ""

            Expect.stringContains
                (checkErrReal "if test -f /x | exitCode then 1 else 2").Message
                "expected bool, got int"
                ""

            Expect.stringContains
                (checkErrReal "if git status | orFail \"m\" then 1 else 2").Message
                "expected bool, got unit"
                ""
        }
    ]

let repeatTests =
    let sx (input: string) = show (parse input)

    let stopCheck lines =
        diagsIn "rep.weir" lines
        |> List.filter (fun d -> d.Severity = "error")
        |> List.map _.Message

    testList "repeat [D:repeat]" [
        test "a let-headed loop's body lines sequence exactly as under a bare head [D:let-loop-body-sequences]" {
            for head in [ "retry attempts=1 delay=0ms"; "poll interval=10ms"; "repeat interval=10ms" ] do
                match
                    Weir.Script.assemble [
                        1, $"let n = {head}"
                        2, "    echo first"
                        3, "    echo second"
                        4, "until r"
                        5, "    true"
                    ]
                with
                | Ok [ ll ] ->
                    Expect.stringContains
                        ll.Text
                        $"echo first {Weir.Parser.sibSepStr} echo second"
                        $"{head}: the two commands are siblings, not one argv"
                | other -> failtest $"{head}: unexpected: {other}"

            let ds =
                diagsIn "u1.weir" [
                    "let n = retry attempts=1 delay=0ms"
                    "    echo first"
                    "    echo second"
                    "until r"
                    "    true"
                    "n |> Seq.iter print"
                ]

            Expect.isEmpty ds "no continued-command warning, no error"

            Expect.isEmpty
                (stopCheck [
                    "let k = retry attempts=1 delay=0ms"
                    "    print \"a\""
                    "    5"
                    "until r"
                    "    r == 5"
                    "print $\"{k}\""
                ])
                "a unit line then the value sequence (each but the last unit)"
        }
        test "a bare repeat (no interval=) is the missing-interval check error, at the head [D:repeat-missing-interval]" {
            for lines in
                [
                    [ "repeat"; "    true" ]
                    [ "repeat"; "    print \"x\""; "    false" ]
                    [ "let _x = repeat"; "    true" ]
                ] do
                let errs = diagsIn "rep.weir" lines |> List.filter (fun d -> d.Severity = "error")

                match errs with
                | [ d ] ->
                    Expect.equal
                        d.Message
                        "a repeat needs interval= — the wait between iterations (repeat interval=1s)"
                        "names the gap"

                    Expect.equal d.Line 1 "at the head line, not the body"
                | _ -> failtest $"expected one error for {lines}, got {errs}"
        }
        test "the key=value head desugars over the internal base; Repeat is the options record" {
            Expect.equal
                (sx "repeat interval=1s (1 == 1)")
                "(repeat {|repeatDefaults with interval = 1s} (== 1 1))"
                "the sugar's form"

            Expect.equal
                (sx "repeat interval=1s timeout=(Some 2m) (1 == 1)")
                "(repeat {|repeatDefaults with interval = 1s; timeout = (Some 2m)} (== 1 1))"
                "timeout rides the update"

            match Map.tryFind "Repeat" preludeTypeEnv.Types with
            | Some(Weir.Types.Record d) ->
                Expect.equal
                    (d.Fields |> List.map (fun (f, t) -> $"{f}: {Weir.Types.formatTy t}"))
                    [ "interval: Duration"; "timeout: Option<Duration>" ]
                    "the record's keys"
            | _ -> failtest "Repeat missing from the prelude"

            Expect.isFalse (Weir.Effects.isCommandReifier "|repeatDefaults") "a library desugar, not a command"
            Expect.isFalse (Weir.Effects.effectfulName "|repeatDefaults") "pure"
        }
        test "interval= is required, and a literal non-positive one refuses at check" {
            let missing = stopCheck [ "repeat timeout=(Some 1s)"; "    print \"x\"" ]
            Expect.exists missing (fun m -> m.Contains "a repeat needs interval=") $"missing: {missing}"

            for lit in [ "0ms"; "-1s" ] do
                let errs = stopCheck [ $"repeat interval={lit}"; "    print \"x\"" ]
                Expect.exists errs (fun m -> m.Contains "interval must be positive") $"{lit}: {errs}"

            let ok = stopCheck [ "repeat interval=1s"; "    print \"x\""; "print \"after\"" ]
            Expect.isEmpty ok "a unit block checks"
        }
        test "the block is unit or a bool; a value needs until (poll's rule), and watch= is poll's" {
            Expect.isEmpty (stopCheck [ "repeat interval=1s"; "    1 == 1" ]) "a bool block is its own end"

            let v = stopCheck [ "repeat interval=1s"; "    5" ]
            Expect.exists v (fun m -> m.Contains "add `until r`") $"value block: {v}"

            Expect.isEmpty
                (stopCheck [
                    "let n = repeat interval=1s"
                    "    5"
                    "until n"
                    "    n > 1"
                    "print (show n)"
                ])
                "until binds the value and the form yields it"

            let w =
                stopCheck [
                    "within proc p = sleep 1"
                    "    repeat interval=1s watch=p"
                    "        print \"x\""
                ]

            Expect.exists w (fun m -> m.Contains "watch= is poll's key") $"watch: {w}"
        }
        test "refused inside plan and pure" {
            let pl =
                stopCheck [
                    "let p ="
                    "    plan"
                    "        repeat interval=1s"
                    "            print \"x\""
                    "print (show p)"
                ]

            Expect.exists pl (fun m -> m.Contains "'repeat' is refused inside 'plan'") $"plan: {pl}"

            let pu =
                stopCheck [
                    "let n ="
                    "    pure"
                    "        repeat interval=1s"
                    "            1 == 1"
                    "        1"
                    "print (show n)"
                ]

            Expect.exists pu (fun m -> m.Contains "'repeat' waits on the clock") $"pure: {pu}"
        }
        test "eval: a bool block ends the loop, until yields the value, a timeout raises as poll's does" {
            expectValue "repeat interval=1ms (1 == 1)" VUnit
            expectValue "repeat interval=1ms (1 + 1) until r (r == 2)" (VInt 2L)

            let ex =
                Expect.throwsC (fun () -> run "repeat interval=5ms timeout=(Some 30ms) (1 == 2)" |> ignore) id

            Expect.stringContains ex.Message "repeat: timed out after" "the timeout's message"
        }
        test "a computed non-positive interval refuses at run time" {
            Expect.stringContains
                (msgOf "repeat (Repeat { interval = Duration.ms 0; timeout = None }) (1 == 1)")
                "repeat: interval must be positive"
                "run-time refusal"
        }
        test "repeat hovers its meaning + keys from the Repeat record" {
            match Weir.Lsp.hoverType [ "repeat interval=10ms"; "    1 == 1" ] 1 3 with
            | Some hv ->
                Expect.stringContains hv "the loop a stop signal ends" "repeat's meaning"
                Expect.stringContains hv "interval: Duration, timeout: Option<Duration>" "repeat's keys"
            | None -> failtest "repeat must answer"
        }
    ]

let interpShowTests =
    testList "Interpolation holes consult Show [D:interp-show]" [
        test "a Duration hole renders (the rider's origin)" { expectValue "$\"took {90500ms}\"" (VStr "took 1m30.5s") }
        test "a hole renders what show renders: seq and record forms" {
            expectValue "$\"xs={[1; 2]}\"" (VStr "xs=[1; 2]")
            // strings inside a structure render quoted (show's form);
            // only the bare top-level string hole stays raw
            expectValue "$\"{[\"a\"]}\"" (VStr "[\"a\"]")
            expectValue "$\"a{\"b\"}c\"" (VStr "abc")
        }
        test "a seq of functions still rejects in a hole" {
            let terr = checkErr "$\"{[Str.trim]}\""
            Expect.stringContains terr.Message "interpolation holes render what show renders" ""
        }
        test "command-argument splices do NOT inherit: Duration teaches the deliberate spellings" {
            match Weir.Check.typecheck env (parseCmd "echo (30s)") with
            | Error terr ->
                Expect.stringContains
                    terr.Message
                    "a Duration's argv form depends on the program; pass Duration.toMillis d or show d deliberately"
                    ""
            | Ok _ -> failtest "expected the splice rejection"

            // both named spellings check clean
            match Weir.Check.typecheck env (parseCmd "echo (show 30s)") with
            | Ok _ -> ()
            | Error terr -> failtest $"show spelling: {terr.Message}"

            match Weir.Check.typecheck env (parseCmd "echo (Duration.toMillis 30s)") with
            | Ok _ -> ()
            | Error terr -> failtest $"toMillis spelling: {terr.Message}"
        }
        test "command-argument splices keep the scalar list for other types" {
            match Weir.Check.typecheck env (parseCmd "echo (ls)") with
            | Error terr -> Expect.stringContains terr.Message "command arguments must be strings, ints or bools" ""
            | Ok _ -> failtest "expected the splice rejection"
        }
    ]

let uuidTests =
    // Uuid [D:uuid]: every vector below is RFC 9562's own (Appendix A/B,
    // Table 3), never recalled
    let call (name: string) (arg: Value) =
        match Map.tryFind name valueEnv with
        | Some(VBuiltin f) -> f arg
        | _ -> failtest $"no builtin '{name}'"

    let guidOf (v: Value) =
        match v with
        | VUuid g -> g
        | v -> failtest $"expected a uuid, got {formatValue v}"

    let bytesOf (g: System.Guid) = g.ToByteArray(bigEndian = true)
    let versionBits (g: System.Guid) = int (bytesOf g).[6] >>> 4
    let variantBits (g: System.Guid) = int (bytesOf g).[8] >>> 6
    let v7 () = call "Uuid.v7" VUnit |> guidOf

    let strictlyIncreasing (xs: System.Guid[]) =
        xs |> Array.pairwise |> Array.forall (fun (a, b) -> a.CompareTo b < 0)

    let textIncreasing (xs: System.Guid[]) =
        xs
        |> Array.pairwise
        |> Array.forall (fun (a, b) -> System.String.CompareOrdinal(formatUuid a, formatUuid b) < 0)

    let runIn te input =
        match Weir.Check.typecheck te (parse input) with
        | Ok t -> eval valueEnv t
        | Error terr -> failtest (formatError terr)

    let a6 = "017f22e2-79b0-7cc3-98c4-dc0c0c07398f"

    testList "Uuid [D:uuid]" [
        test "every generator sets its version and the RFC 9562 variant (10xx)" {
            let gens = [
                "v4", 4, (fun () -> call "Uuid.v4" VUnit |> guidOf)
                "v5", 5, (fun () -> run "Uuid.v5 Uuid.ns.dns \"x\"" |> guidOf)
                "v7", 7, v7
                "v7At", 7, (fun () -> call "Uuid.v7At" (VInstant 1645557742000L) |> guidOf)
            ]

            for name, ver, gen in gens do
                for _ in 1..200 do
                    let g = gen ()
                    Expect.equal (versionBits g) ver $"{name} version nibble"
                    Expect.equal (variantBits g) 2 $"{name} variant bits are 0b10"
        }
        test "v5 reproduces RFC 9562 A.4; the namespaces are Table 3's" {
            Expect.equal
                (run "Uuid.v5 Uuid.ns.dns \"www.example.com\" |> Uuid.toString")
                (VStr "2ed6657d-e927-568b-95e1-2665a8aea6a2")
                "A.4: DNS namespace + www.example.com"

            Expect.equal
                (run "[Uuid.ns.dns; Uuid.ns.url; Uuid.ns.oid; Uuid.ns.x500] |> Seq.map Uuid.toString |> Seq.freeze")
                (VSeq [
                    VStr "6ba7b810-9dad-11d1-80b4-00c04fd430c8"
                    VStr "6ba7b811-9dad-11d1-80b4-00c04fd430c8"
                    VStr "6ba7b812-9dad-11d1-80b4-00c04fd430c8"
                    VStr "6ba7b814-9dad-11d1-80b4-00c04fd430c8"
                ])
                "Table 3 (6ba7b813 is unassigned)"

            Expect.equal
                (run "Uuid.v5 Uuid.ns.url \"a\" == Uuid.v5 Uuid.ns.url \"a\"")
                (VBool true)
                "deterministic: same name, same namespace"
        }
        test "parse reads every RFC 9562 version and form; version reports it" {
            for text, ver in
                [
                    "C232AB00-9414-11EC-B3C8-9F6BDECED846", 1 // A.1
                    "5df41881-3aed-3515-88a7-2f4a814cf09e", 3 // A.2
                    "919108f7-52d1-4320-9bac-f847db4148a8", 4 // A.3
                    "2ed6657d-e927-568b-95e1-2665a8aea6a2", 5 // A.4
                    "1EC9414C-232A-6B00-B3C8-9F6BDECED846", 6 // A.5
                    a6, 7 // A.6
                    "2489E9AD-2EE2-8E00-8EC9-32D5F69181C0", 8 // B.1
                    "00000000-0000-0000-0000-000000000000", 0 // nil
                    "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", 15
                ] do // max
                Expect.equal (run $"Uuid.parse \"{text}\" |> Uuid.version") (VInt(int64 ver)) $"{text}"

                Expect.equal
                    (run $"Uuid.parse \"{text}\" |> Uuid.toString")
                    (VStr(text.ToLowerInvariant()))
                    "toString is the lowercase canonical form"

            let canon = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6" // RFC 9562 Figure 1

            for form in
                [
                    canon
                    canon.ToUpperInvariant()
                    "F81d4FAE-7dec-11D0-a765-00A0C91E6bf6"
                    canon.Replace("-", "")
                    "urn:uuid:" + canon // Figure 4
                    "URN:UUID:" + canon.ToUpperInvariant()
                ] do
                Expect.equal (run $"Uuid.parse \"{form}\" |> Uuid.toString") (VStr canon) $"'{form}' reads"
        }
        test "invalid text: tryParse returns None and parse raises a teaching — never an exception escaping tryParse" {
            let bad = [
                ""
                "x"
                "{f81d4fae-7dec-11d0-a765-00a0c91e6bf6}" // the platform's braces
                "(f81d4fae-7dec-11d0-a765-00a0c91e6bf6)"
                "{0x6ba7b810,0x9dad,0x11d1,{0x80,0xb4,0x00,0xc0,0x4f,0xd4,0x30,0xc8}}"
                "f81d4fae-7dec-11d0-a765-00a0c91e6bf" // 35
                "f81d4fae-7dec-11d0-a765-00a0c91e6bf6a" // 37
                "f81d4fae7dec-11d0-a765-00a0c91e6bf6-" // hyphens misplaced
                "g81d4fae-7dec-11d0-a765-00a0c91e6bf6" // non-hex
                "f81d4fae-7dec-11d0-a765-00a0c91e6bf６" // a non-ASCII digit
                "urn:uuid:"
                "urn:uuid:f81d4fae7dec11d0a76500a0c91e6bf"
                // surrounding whitespace is no spelling [D:uuid-strict]
                " f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
                "f81d4fae-7dec-11d0-a765-00a0c91e6bf6 "
                "f81d4fae-7dec-11d0-a765-00a0c91e6bf6\n"
            ]

            for text in bad do
                Expect.equal (call "Uuid.tryParse" (VStr text)) (VUnion("None", None)) $"tryParse '{text}'"

                match parseUuid text with
                | Error e -> Expect.stringContains e "not a uuid" $"the teaching for '{text}'"
                | Ok g -> failtest $"'{text}' must not parse (got {g})"

            Expect.throws (fun () -> call "Uuid.parse" (VStr "nope") |> ignore) "parse raises"
        }
        test "100k v7 in a tight loop strictly increase, as uuids and as text" {
            let xs = Array.init 100_000 (fun _ -> v7 ())
            Expect.isTrue (strictlyIncreasing xs) "uuid order"
            Expect.isTrue (textIncreasing xs) "string sort"
        }
        test "v7 across threads: each thread's run increases, none collide" {
            let per = 20_000
            let results = Array.init 8 (fun _ -> Array.zeroCreate<System.Guid> per)

            let threads = [|
                for t in 0..7 ->
                    System.Threading.Thread(fun () ->
                        for i in 0 .. per - 1 do
                            results[t][i] <- v7 ())
            |]

            threads |> Array.iter (fun t -> t.Start())
            threads |> Array.iter (fun t -> t.Join())

            for r in results do
                Expect.isTrue (strictlyIncreasing r) "per-thread uuid order"
                Expect.isTrue (textIncreasing r) "per-thread text order"

            let all = Array.concat results
            Expect.equal (all |> Array.distinct |> Array.length) all.Length "no duplicates"
        }
        test "v7At: instant round-trips truncated to ms; the epoch is the floor" {
            Expect.equal
                (run "Uuid.v7At (Instant.parse \"2026-08-14T12:00:00.2509Z\") |> Uuid.instant")
                (VUnion("Some", Some(run "Instant.parse \"2026-08-14T12:00:00.250Z\"")))
                "sub-ms truncates"

            Expect.equal
                (run "Uuid.v7At (Instant.ofEpochMs 0) |> Uuid.instant")
                (VUnion("Some", Some(VInstant 0L)))
                "the epoch itself encodes"

            Expect.equal
                (run $"Uuid.parse \"{a6}\" |> Uuid.instant")
                (VUnion("Some", Some(VInstant 1645557742000L)))
                "A.6's timestamp (0x017F22E279B0)"

            Expect.equal (run "Uuid.v4 () |> Uuid.instant") (VUnion("None", None)) "v4 carries no time"
            Expect.equal (run "Uuid.max |> Uuid.instant") (VUnion("None", None)) "max is not a v7"

            Expect.throws
                (fun () -> run "Uuid.v7At (Instant.parse \"1969-12-31T23:59:59Z\")" |> ignore)
                "before the epoch raises"
        }
        // sequenced: v7 state is per-process [D:uuid] — a parallel test's
        // v7 can move the last ms past `now`, and v7At(now) then takes
        // the earlier-ms branch by design
        testSequenced
        <| test "v7At after a v7 in the same session still orders; an earlier instant gets randomness, not a bent clock" {
            let a = v7 ()

            // a burst (the threaded test) can leave the counter's ms ahead
            // of the wall clock; v7At joins only for last <= ms <= now
            let aMs =
                match call "Uuid.instant" (VUuid a) with
                | VUnion("Some", Some(VInstant ms)) -> ms
                | v -> failtest $"a v7 carries its instant, got {v}"

            let deadline = System.Diagnostics.Stopwatch.StartNew()

            while System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < aMs
                  && deadline.ElapsedMilliseconds < 1000L do
                System.Threading.Thread.Sleep 1

            let now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let b = call "Uuid.v7At" (VInstant now) |> guidOf
            let c = v7 ()
            Expect.isTrue (a.CompareTo b < 0 && b.CompareTo c < 0) "a shared-state v7At sits in the sequence"

            let past = call "Uuid.v7At" (VInstant 1645557742000L) |> guidOf

            Expect.equal
                (call "Uuid.instant" (VUuid past))
                (VUnion("Some", Some(VInstant 1645557742000L)))
                "the requested timestamp is kept exactly"

            Expect.isTrue ((v7 ()).CompareTo c > 0) "a past v7At does not disturb v7's sequence"
        }
        test "ordering is the canonical text's order across mixed versions" {
            let ids = [
                for _ in 1..50 -> call "Uuid.v4" VUnit
                for _ in 1..50 -> v7 () |> VUuid
                for i in 1..50 -> run $"Uuid.v5 Uuid.ns.oid \"{i}\""
                for t in
                    [
                        "C232AB00-9414-11EC-B3C8-9F6BDECED846"
                        a6
                        "1EC9414C-232A-6B00-B3C8-9F6BDECED846"
                    ] -> run $"Uuid.parse \"{t}\""
                yield run "Uuid.nil"
                yield run "Uuid.max"
                // high bits set at every field boundary
                for t in
                    [
                        "80000000-0000-0000-0000-000000000000"
                        "00000000-8000-0000-0000-000000000000"
                        "00000000-0000-8000-0000-000000000000"
                        "00000000-0000-0000-8000-000000000000"
                        "7fffffff-ffff-ffff-ffff-ffffffffffff"
                    ] -> run $"Uuid.parse \"{t}\""
            ]

            let sorted =
                match call "Seq.sort" (VSeq ids) with
                | VSeq s -> s |> Seq.map (guidOf >> formatUuid) |> List.ofSeq
                | v -> failtest $"sort returned {formatValue v}"

            let byText =
                ids
                |> List.map (guidOf >> formatUuid)
                |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

            Expect.equal sorted byText "Seq.sort agrees with ordinal text order"

            Expect.equal
                (run "[Uuid.max; Uuid.nil] |> Seq.min |> show")
                (VStr "00000000-0000-0000-0000-000000000000")
                "min"
        }
        test "toBytes/fromBytes: 16 big-endian bytes, round-tripping; a wrong length raises" {
            Expect.equal
                (run $"Uuid.parse \"{a6}\" |> Uuid.toBytes")
                (VBytes(System.Convert.FromHexString "017F22E279B07CC398C4DC0C0C07398F"))
                "network order — the text's order"

            Expect.equal
                (run $"(Uuid.parse \"{a6}\" |> Uuid.toBytes |> Uuid.fromBytes) == Uuid.parse \"{a6}\"")
                (VBool true)
                "round trip"

            for _ in 1..100 do
                let g = call "Uuid.v4" VUnit
                Expect.equal (call "Uuid.fromBytes" (call "Uuid.toBytes" g)) g "random round trip"

            Expect.throws (fun () -> call "Uuid.fromBytes" (VBytes [| 1uy; 2uy |]) |> ignore) "2 bytes raise"
        }
        test "JSON: a Uuid field crosses as its canonical string and round-trips inside records" {
            let te =
                env
                |> declare "type JUuidRow = { id: Uuid; parent: Option<Uuid>; tags: seq<Uuid>; note: string }"

            Expect.equal
                (runIn
                    te
                    $"[{{ id = Uuid.parse \"{a6}\"; parent = None; tags = [Uuid.nil]; note = \"n\" }}] |> to jsonl |> Seq.freeze")
                (VSeq [
                    VStr $"{{\"id\":\"{a6}\",\"tags\":[\"00000000-0000-0000-0000-000000000000\"],\"note\":\"n\"}}"
                ])
                "the wire shape"

            Expect.equal
                (runIn
                    te
                    "let r = { id = Uuid.v7 (); parent = Some(Uuid.v4 ()); tags = [Uuid.max; Uuid.v5 Uuid.ns.url \"x\"]; note = \"x\" } in show ([r] |> to jsonl |> from jsonl JUuidRow |> Seq.freeze |> Seq.head) == show r")
                (VBool true)
                "a record round-trips (show: a seq field rules out Eq)"

            Expect.equal
                (runIn
                    te
                    "[\"{\\\"id\\\":\\\"URN:UUID:F81D4FAE-7DEC-11D0-A765-00A0C91E6BF6\\\",\\\"parent\\\":null,\\\"tags\\\":[],\\\"note\\\":\\\"\\\"}\"] |> from jsonl JUuidRow |> Seq.map (fun r -> Uuid.toString r.id) |> Seq.freeze")
                (VSeq [ VStr "f81d4fae-7dec-11d0-a765-00a0c91e6bf6" ])
                "the reader accepts every parse form"

            Expect.throws
                (fun () ->
                    runIn
                        te
                        "[\"{\\\"id\\\":\\\"nope\\\",\\\"parent\\\":null,\\\"tags\\\":[],\\\"note\\\":\\\"\\\"}\"] |> from jsonl JUuidRow |> Seq.freeze"
                    |> ignore)
                "a malformed uuid string raises"
        }
        test "effects: v4/v7/v7At are ambient like Instant.now; v5 is pure" {
            for n in [ "Uuid.v4"; "Uuid.v7"; "Uuid.v7At" ] do
                Expect.equal (Weir.Effects.effectClass n) (Some Weir.Effects.Ambient) $"{n} is ambient input"

            for n in [ "Uuid.v5"; "Uuid.parse"; "Uuid.version"; "Uuid.toBytes" ] do
                Expect.isFalse (Weir.Effects.effectfulName n) $"{n} is pure"

            let errsOf = errorsIn "uuid.weir"

            for call, phrase in
                [
                    "Uuid.v4 ()", "'Uuid.v4' draws randomness"
                    "Uuid.v7 ()", "'Uuid.v7' reads the clock and draws randomness"
                    "Uuid.v7At (Instant.ofEpochMs 0)", "'Uuid.v7At' draws randomness"
                ] do
                match errsOf [ "let x ="; "    pure"; $"        {call}"; "print (Uuid.toString x)" ] with
                | e :: _ -> Expect.stringContains e.Message phrase $"pure refuses {call}"
                | [] -> failtest $"pure must refuse {call}"

            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    pure"
                    "        Uuid.v5 Uuid.ns.dns \"a\""
                    "print (Uuid.toString x)"
                ])
                "pure admits v5"

            Expect.isEmpty
                (errsOf [ "let x ="; "    readonly"; "        Uuid.v7 ()"; "print (Uuid.toString x)" ])
                "readonly admits the ambient generators"
        }
        test "plan mode: a generator runs inside a plan and is not captured as an op" {
            // under the OS temp dir: capture checks the parent exists [D:plan-preconditions]
            let path =
                (System.IO.Path.GetTempPath() + "weir-uuid-plan-never").Replace('\\', '/')

            match
                run (
                    "plan"
                    + Weir.Parser.sibSepStr
                    + $"File.write \"{path}\" [Uuid.toString (Uuid.v7 ())]"
                )
            with
            | VRecord("Plan", [ "ops", VSeq ops ]) ->
                match List.ofSeq ops with
                | [ VUnion("WriteFile", Some(VTuple [ _; VSeq content ])) ] ->
                    match List.ofSeq content with
                    | [ VStr s ] ->
                        Expect.equal
                            (parseUuid s |> Result.map versionBits)
                            (Ok 7)
                            "the v7 ran and its value is in the op"
                    | other -> failtest $"unexpected content {other}"
                | other -> failtest $"expected exactly the WriteFile op, got {other}"
            | v -> failtest $"expected a Plan, got {formatValue v}"

            Expect.isFalse (System.IO.File.Exists path) "the plan wrote nothing"
        }
        test "print takes a Uuid, as the wire table's canonical types [D:print-canonical]" {
            let ds = diagsIn "print.weir" [ "print (Uuid.v7 ())"; "printerr Uuid.nil" ]
            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "print and printerr check"

            Expect.stringContains
                (checkErr "print (Instant.now ())").Message
                "print takes a string, int, float, bool, Uuid"
                "Instant still refuses"
        }
        test "the type: declarable, Eq + Show + Ord, argv-spliceable, refused where Instant is" {
            Expect.equal (run "Uuid.nil == Uuid.parse \"00000000000000000000000000000000\"") (VBool true) "Eq"
            Expect.equal (run "show Uuid.max") (VStr "ffffffff-ffff-ffff-ffff-ffffffffffff") "Show is canonical"
            Expect.equal (run "$\"id={Uuid.nil}\"") (VStr "id=00000000-0000-0000-0000-000000000000") "interpolation"

            Expect.equal
                (run "[Uuid.max; Uuid.nil] |> Seq.sortBy (fun u -> u) |> Seq.map Uuid.toString |> Seq.freeze")
                (VSeq [
                    VStr "00000000-0000-0000-0000-000000000000"
                    VStr "ffffffff-ffff-ffff-ffff-ffffffffffff"
                ])
                "sortBy keys"

            env |> declare "type UuidRec = { id: Uuid }" |> ignore

            Expect.stringContains
                (checkErr "Uuid.nil < Uuid.max").Message
                "not defined for Uuid"
                "no < on identifiers (as strings)"

            Expect.stringContains (checkErr "Uuid.parse 5").Message "expected string" "parse takes text"
        }
    ]

let operatorValueTests =
    // (op): an operator as a value, unapplied only [D:operator-values]
    // — the desugar contract is byte-parity with the spelled lambda
    testList "operators as values [D:operator-values]" [
        test "the receipt: reduce/scan/fold take (+), context resolves the overload" {
            Expect.equal (run "[1; 2; 3] |> Seq.reduce (+)") (VInt 6L) ""
            Expect.equal (run "[1.5; 2.5] |> Seq.reduce (+)") (VFloat 4.0) "floats by context"
            Expect.equal (run "[\"a\"; \"b\"] |> Seq.reduce (+)") (VStr "ab") "concat by context"
            Expect.equal (run "[1s; 2s] |> Seq.reduce (+)") (VDur 3000L) "Durations by context"
            Expect.equal (run "[1; 2; 3] |> Seq.fold (+) 100") (VInt 106L) ""
            Expect.equal (run "[100; 7; 4] |> Seq.reduce (%)") (VInt 2L) "(%) is a value too"

            Expect.equal (run "[1; 2; 3] |> Seq.scan (+) 0" |> forceSeq) [ VInt 0L; VInt 1L; VInt 3L; VInt 6L ] ""

            Expect.equal (run "[5; 2] |> Seq.reduce (-)") (VInt 3L) "(-) is the operator, not unary minus"
        }
        test "PARITY: (op) and its lambda agree — including the failure" {
            Expect.equal
                (run "[1; 2; 3] |> Seq.reduce (+)")
                (run "[1; 2; 3] |> Seq.reduce (fun a b -> a + b)")
                "the desugar is verbatim"

            // a later argument pins the operands for both spellings
            // [D:plus-defer]
            Expect.equal
                (run "Seq.fold (+) 100 [1; 2; 3]")
                (run "Seq.fold (fun a b -> a + b) 100 [1; 2; 3]")
                "a later anchor decides both"

            // both spellings fail identically where nothing pins the
            // operands — the (op) form must not be better either
            let m1 = (checkErr "fun xs -> Seq.reduce (+) xs").Message
            let m2 = (checkErr "fun xs -> Seq.reduce (fun a b -> a + b) xs").Message
            Expect.equal m1 m2 "failure parity, same message"
        }
        test "(==) as a value carries its Eq constraint (the class-in-value-position answer)" {
            Expect.equal (run "let eq = (==) in eq 1 1") (VBool true) "generalizes with Eq"
            let m = (checkErr "let eq = (==) in eq Seq.head Seq.head").Message
            Expect.stringContains m "'=='" "the Eq refusal reaches through the value"
        }
        test "partial application refuses, showing both directions" {
            let m = (checkErr "[1; 2] |> Seq.map ((>) 10)").Message
            Expect.stringContains m "cannot be partially applied" "the rule"
            Expect.stringContains m "fun x -> v > x" "the backwards reading, shown"
            Expect.stringContains m "fun x -> x > v" "the likely-intended direction, shown"

            // a pipe is the same half application
            Expect.stringContains
                (checkErr "[1; 2] |> Seq.where (10 |> (>))").Message
                "cannot be partially applied"
                "piped in"

            Expect.stringContains (checkErr "let f = 1 |> (+) in f 2").Message "cannot be partially applied" "bound"
        }
        test "a run's hooks are its own, and gone when it ends [D:run-hooks]" {
            let seen = System.Collections.Concurrent.ConcurrentBag<string>()

            let run (tag: string) =
                System.Threading.Tasks.Task.Run(fun () ->
                    let hooks = Weir.Session.RunHooks()
                    hooks.PositionOf <- System.Func<obj, string>(fun _ -> tag)
                    use _installed = Weir.Session.withRunHooks hooks
                    System.Threading.Thread.Sleep 50

                    // a thread the run starts sees the run's hooks
                    let inner =
                        System.Threading.Tasks.Task.Run(fun () ->
                            Weir.Session.currentRunHooks().PositionOf.Invoke null)

                    seen.Add(tag + "=" + Weir.Session.currentRunHooks().PositionOf.Invoke null + inner.Result))

            System.Threading.Tasks.Task.WaitAll [| run "a"; run "b" |]
            Expect.equal (seen |> Seq.sort |> List.ofSeq) [ "a=aa"; "b=bb" ] "each run read its own"
            Expect.isNull (Weir.Session.currentRunHooks ()) "nothing is left installed"
        }
        test "one PATH walk, two rules [D:one-path-walk]" {
            withTempDir "weir-pathwalk" (fun td ->
                if not (System.OperatingSystem.IsWindows()) then
                    let bin = System.IO.Path.Combine(td, "bin")
                    System.IO.Directory.CreateDirectory bin |> ignore
                    let plain = System.IO.Path.Combine(bin, "plain")
                    let tool = System.IO.Path.Combine(bin, "tool")
                    System.IO.File.WriteAllText(plain, "x")
                    System.IO.File.WriteAllText(tool, "x")

                    System.IO.File.SetUnixFileMode(
                        tool,
                        System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserExecute
                    )

                    let shell: Weir.PathLookup.Rule = {
                        Anchor = Some(fun p -> System.IO.Path.GetFullPath(p, td))
                        Executable = false
                    }

                    let own: Weir.PathLookup.Rule = { Anchor = None; Executable = true }
                    let find rule dirs prog = Weir.PathLookup.find rule [] dirs prog

                    // the shell's rule: a relative entry counts, read from the anchor, and a file need only exist
                    Expect.equal (find shell [ "bin" ] "plain") (Some plain) "a relative entry, anchored"

                    Expect.equal
                        (find shell [ "/nonexistent-weir"; bin ] "tool")
                        (Some tool)
                        "the first directory that holds it"
                    // weir's own spawns: rooted entries and runnable files only
                    Expect.equal (find own [ "bin" ] "tool") None "a relative entry is skipped"
                    Expect.equal (find own [ bin ] "plain") None "a file with no execute bit is no tool"
                    Expect.equal (find own [ bin ] "tool") (Some tool) "a rooted entry's runnable file"
                    Expect.equal (find own [ bin ] "absent") None "nothing there")
        }
        test "one redirect rule for both clients [D:one-redirect-rule]" {
            let secret = Set.ofList [ "authorization"; "private-token" ]
            let headers = [ "Authorization", "t"; "PRIVATE-TOKEN", "p"; "Accept", "x" ]

            let hop hops code (loc: string) =
                Weir.Http.nextHop
                    3
                    secret
                    hops
                    "https://a.example/x/y"
                    headers
                    code
                    (Some(System.Uri(loc, System.UriKind.RelativeOrAbsolute)))

            Expect.equal
                (hop 0 302 "https://a.example/z")
                (Weir.Http.Follow("https://a.example/z", headers))
                "the same origin keeps every header"

            Expect.equal
                (hop 0 302 "/z")
                (Weir.Http.Follow("https://a.example/z", headers))
                "a relative Location, against the current URL"

            Expect.equal
                (hop 0 307 "https://b.example/z")
                (Weir.Http.Follow("https://b.example/z", [ "Accept", "x" ]))
                "another origin: the credentials stay behind, whatever their case"

            Expect.equal
                (hop 0 302 "http://a.example/z")
                (Weir.Http.Follow("http://a.example/z", [ "Accept", "x" ]))
                "a downgrade to http is another origin"

            Expect.equal (hop 3 302 "https://a.example/z") Weir.Http.Exhausted "the hops are spent"
            Expect.equal (hop 0 200 "https://a.example/z") Weir.Http.Final "not a redirect status"

            Expect.equal
                (Weir.Http.nextHop 3 secret 0 "https://a.example/" headers 302 None)
                Weir.Http.Final
                "no Location"
        }
        test "the module table is where a module's effect and its words come from [D:module-table]" {
            Expect.equal
                Weir.Effects.effectfulModules
                (Set.ofList [ "File"; "Dir"; "Env"; "Args"; "Proc"; "Net"; "Log"; "Debug"; "Term"; "Color" ])
                "the modules whose every member has an effect"

            let cls n = Weir.Effects.effectClass n

            for name, expected in
                [
                    "File.read", Some Weir.Effects.Ambient
                    "File.write", Some Weir.Effects.Mutation
                    "Env.get", Some Weir.Effects.Ambient
                    "Proc.stop", Some Weir.Effects.Mutation
                    "Net.portOpen", Some Weir.Effects.Ambient
                    "Term.width", Some Weir.Effects.Ambient
                    "Term.title", Some Weir.Effects.Mutation
                    "Log.info", Some Weir.Effects.Mutation
                    "Debug.peek", Some Weir.Effects.Mutation
                    "Http.query", Some Weir.Effects.Ambient
                    "Str.trim", None
                    "Http.get", None
                ] do
                Expect.equal (cls name) expected name

            // one row a module, none twice, and Builtins holds members for each
            let names = Weir.Modules.rows |> List.map (fun r -> r.Name)
            Expect.equal (List.length (List.distinct names)) (List.length names) "no module has two rows"

            Expect.equal
                (Set.ofList names)
                (Weir.Builtins.moduleBlurbs |> Map.keys |> Set.ofSeq)
                "the blurbs are the table's"
        }
        test "both evaluators enter a node through one step [D:eval-twin-entry]" {
            let src =
                System.IO.File.ReadAllLines(
                    System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Weir", "Eval.fs")
                )

            let count (needle: string) (lines: string seq) =
                lines |> Seq.filter (fun l -> l.Contains needle) |> Seq.length

            // the boundary is crossed in enterNode and nowhere else: an entry
            // written out by hand would miss whatever the step gains next
            Expect.equal (count "Session.evalBoundary ()" src) 1 "one crossing of the boundary, inside enterNode"

            // every node kind the flow evaluator spells out enters through it
            let from = src |> Array.findIndex (fun l -> l.TrimEnd().EndsWith "] evalAsync")
            let until = src |> Array.findIndex (fun l -> l.TrimEnd().EndsWith "] applyAsync")
            let twin = src[from..until]

            let arms =
                twin
                |> Seq.filter (fun l -> l.StartsWith "                    | TE")
                |> Seq.length

            Expect.isGreaterThan arms 20 "the flow evaluator's arms were found"
            Expect.equal (count "enterNode env te" twin) arms "each of its arms enters through the shared step"

            // and evaluates its children through an arm both evaluators share
            // [D:eval-one-arm]: the flow evaluator awaits a child by hand in
            // five places only — orFail, a pure region and always (the body
            // and its cleanup, twice) — so a kind spelled out in it alone
            // moves this count and is sent to the shared arms
            Expect.equal
                (count "awaitFrame flow (evalAsync flow env" twin)
                5
                "the flow evaluator's own child evaluations"

            Expect.isGreaterThan
                (count "(fun e t -> awaitFrame flow (evalAsync flow e t))" twin)
                15
                "its arms are the shared ones, given how a flow evaluates a child"
        }
        test "the trace bounds every word, and peek hands its value on [D:command-trace] [D:debug-aids]" {
            Expect.equal
                (Weir.Proc.traceArgv [ "git"; "tag"; "v 1"; ""; "--file=a/b.txt"; "it's" ])
                "git tag 'v 1' '' --file=a/b.txt 'it'\\''s'"
                "a spaced, an empty and a quoted word"

            Weir.Proc.markSecretWord "trace-test-secret"
            Expect.equal (Weir.Proc.traceArgv [ "curl"; "trace-test-secret" ]) "curl ***" "a Secret word"
            Expect.equal (run "[1; 2; 3] |> Seq.map (fun n -> n * 2) |> Debug.peek |> Seq.sum") (VInt 12L) "a seq"
            Expect.equal (run "let x = 5 |> Debug.peek in x + 1") (VInt 6L) "a scalar"
            Expect.equal (run "Debug.break ()") VUnit "no run, no prompt"
        }
        test "a lambda that is an int expression answers as the walk does" {
            Expect.equal
                (run "[1; 2; 3; 4; 5; 6] |> Seq.where (fun n -> n * 2 % 3 == 0) |> Seq.length")
                (VInt 2L)
                "nested operators"

            Expect.equal
                (run "let k = 10 in [1; 2] |> Seq.map (fun n -> (n + k) * (n - 1) / 2) |> Seq.sum")
                (VInt 6L)
                "a capture"

            Expect.equal
                (run "[1; 2] |> Seq.map (fun n -> 7 / n - 7 % n) |> Seq.sum")
                (VInt 9L)
                "division and remainder"

            Expect.equal (run "[2000; 3000] |> Seq.map (fun n -> n + 1) |> Seq.sum") (VInt 5002L) "past the small ints"

            Expect.equal
                (run "[1.5; 2.5] |> Seq.map (fun x -> x * 2.0 + 1.0) |> Seq.reduce (+)")
                (VFloat 10.0)
                "floats take the walk"

            Expect.stringContains
                (Expect.throwsC (fun () -> run "[1; 0] |> Seq.map (fun n -> 10 % n + 1) |> Seq.length" |> ignore) id)
                    .Message
                "modulo by zero"
                "a zero divisor takes the walk"

            Expect.stringContains
                (Expect.throwsC
                    (fun () ->
                        run "[9223372036854775807] |> Seq.map (fun n -> n + 1 - 1) |> Seq.length"
                        |> ignore)
                    id)
                    .Message
                "integer overflow"
                "an overflow in the middle"
        }
        test "a one-operator lambda over ints answers as the walk does" {
            Expect.equal (run "[1; 2; 3] |> Seq.reduce (+)") (VInt 6L) "an operator value"
            Expect.equal (run "[1; 2; 3] |> Seq.fold (fun a n -> a - n) 10") (VInt 4L) "a spelled lambda"
            Expect.equal (run "[1; 5] |> Seq.where (fun n -> n > 1) |> Seq.length") (VInt 1L) "a literal operand"
            Expect.equal (run "[\"a\"; \"b\"] |> Seq.reduce (+)") (VStr "ab") "not ints: the walk"
            Expect.equal (run "let k = 3 in [1; 2] |> Seq.map (fun n -> n * k) |> Seq.reduce (+)") (VInt 9L) "a capture"

            Expect.stringContains
                (Expect.throwsC (fun () -> run "[1; 2] |> Seq.map (fun n -> n / 0) |> Seq.length" |> ignore) id).Message
                "division by zero"
                "a raise takes the walk"
        }
        test "partial-application teach: a commutative op offers the directions as alternatives" {
            let m = (checkErr "[1; 2] |> Seq.map ((==) 1)").Message
            Expect.stringContains m "fun x -> x == v (or fun x -> v == x)" "== is symmetric — the or-form holds"
        }
        test "partial-application teach: a non-commutative op says the directions differ" {
            let m = (checkErr "[1; 2] |> Seq.map ((-) 1)").Message
            Expect.stringContains m "fun x -> x - v and fun x -> v - x differ" "- has no or-form"
            let m2 = (checkErr "[1; 2] |> Seq.map ((<) 1)").Message
            Expect.stringContains m2 "fun x -> x < v and fun x -> v < x differ" "< has no or-form"
            Expect.isFalse (m2.Contains "(or ") "the or-form must never reach a non-commutative op"
        }
        test "the refused set refuses with reasons; sigils cannot be caught" {
            let perr (src: string) =
                match Weir.Parser.parseLine realResolver src with
                | Error m -> m
                | Ok _ -> failtest $"expected a parse refusal: {src}"

            Expect.stringContains (perr "Seq.reduce (&&)") "short-circuits" "the eager-value reason"
            Expect.stringContains (perr "Seq.reduce (||)") "short-circuits" ""
            Expect.stringContains (perr "Seq.reduce (|>)") "grammar, not a function" ""
            Expect.stringContains (perr "Seq.reduce (>>)") "composition already yields" ""
            Expect.stringContains (perr "Seq.reduce (=)") "write `(==)`" "not a weir operator"
            Expect.stringContains (perr "Seq.reduce (!=)") "write `(<>)`" ""
            Expect.stringContains (perr "Seq.reduce (<|)") "no `<|`" ""
            // ^ and $ are not operators in this sense — the form cannot catch them
            perr "let f = (^) in 1" |> ignore
        }
        test "ordinary infix and parens are untouched" {
            Expect.equal (run "(1 + 2) * 3") (VInt 9L) ""
            Expect.equal (run "10 > 5") (VBool true) ""
            Expect.equal (run "(0 - 5)") (VInt -5L) "a parenthesized subtraction is not (-)"
        }
    ]

let lsCommandTeachTests =
    testList "ls/pwd command teaching [D:ls-command-teach]" [
        test "a command-name value applied to args points at ^ls, not a bare type error" {
            let m = (checkErr "ls -la").Message
            Expect.stringContains m "not the shell command" "names the confusion"
            Expect.stringContains m "^ls" "points at the force-external escape"
            // a user value applied to a flag keeps the generic message
            let g = (checkErr "let xs = [1; 2] in xs -5").Message
            Expect.stringContains g "not a function taking" "a user value is unaffected"
            Expect.isFalse (g.Contains "shell command") "no ^-advice for a non-command value"
        }
        test "a user's own `ls` binding is named as the binding it is, with no #alias advice" {
            match
                diagsIn "ls.weir" [ "let ls = \"x\""; "ls -la" ]
                |> List.filter (fun d -> d.Severity = "error")
            with
            | [ d ] ->
                Expect.equal
                    d.Message
                    "'ls' is bound here to a string value, not the shell command — to run the external 'ls' with arguments, force it external: ^ls …"
                    "no 'weir's typed', no #alias in a script"
            | other -> failtest $"expected one error, got {other}"
        }
    ]

let unquotedPathTeachTests =
    testList "unquoted-path teaching [D:unquoted-path-teach]" [
        test "an unbound '/'-RHS with a leading space teaches quoting the path" {
            // `cd /work` in an expression parses as `cd / work`; the hint
            // must append to the unbound error, not replace it
            let m = (checkErr "let x = cd /work in x").Message
            Expect.stringContains m "unbound variable 'work'" "keeps the unbound diagnosis"
            Expect.stringContains m "quote it (cd \"/work\")" "teaches the quoted path"
        }
        test "a glued arithmetic typo keeps its plain did-you-mean" {
            let m =
                (checkErr "let total = 10 in let count = 2 in let n = total/coutn in n").Message

            Expect.stringContains m "Did you mean 'count'?" "arithmetic typo keeps did-you-mean"
            Expect.isFalse (m.Contains "filesystem path") "no path hint for glued division"
        }
    ]

// `e |> orFail [msg]` [D:orfail-form] — the inverse of try
let orFailTests =
    let raisesWith (src: string) =
        try
            run src |> ignore
            failtest $"expected a raise: {src}"
        with Failure m when not (m.StartsWith "expected a raise") ->
            m

    testList "|> orFail [D:orfail-form]" [
        test "the form folds everything to its left into one node" {
            expectParse "x |> orFail" "(orFail x)"
            expectParse "x |> orFail \"m\"" "(orFail x \"m\")"
            expectParse "x |> orFail m" "(orFail x m)"
            expectParse "x |> f |> orFail" "(orFail (x |> f))"
            expectParse "x |> orFail \"m\" |> g" "((orFail x \"m\") |> g)"
            expectParse "x |> orFail $\"{n} bad\" |> g" "((orFail x (interp {n}\" bad\")) |> g)"
        }
        test "the form types as the payload of a Result<'T, string> or an Option<'T>" {
            Expect.equal (formatTy (checkOk "Some 3 |> orFail").Ty) "int" "Option"
            Expect.equal (formatTy (checkOk "Str.tryToInt \"3\" |> orFail \"m\"").Ty) "int" "Option, with a message"
            Expect.equal (formatTy (checkOk "3 |> try result |> orFail").Ty) "int" "Result"
            Expect.equal (formatTy (checkOk "Ok \"a\" |> orFail").Ty) "string" "an open error type becomes string"
        }
        test "anything else is refused, naming both accepted shapes" {
            for src in [ "3 |> orFail"; "[1] |> orFail \"m\""; "\"a\" |> orFail" ] do
                let m = (checkErr src).Message
                Expect.stringContains m "orFail takes a Result<'T, string> or an Option<'T>" src

            Expect.stringContains
                (checkErr "Some 1 |> orFail 5").Message
                "orFail's message is a string"
                "a non-string message"

            Expect.stringContains
                (checkErr "let f r = r |> orFail in f").Message
                "not known yet"
                "an undetermined left side"
        }
        test "a Result whose Error is not a string is refused" {
            let diags =
                diagsIn "re.weir" [
                    "type R = { n: int }"
                    "let r = (if 1 > 0 then Error { n = 1 } else Ok 2) |> orFail"
                    "print (show r)"
                ]
                |> List.map (fun d -> d.Message)

            Expect.exists diags (fun m -> m.Contains "its Error must carry a string") "Result<int, R>"
        }
        test "Ok and Some unwrap" {
            Expect.equal (run "Some 3 |> orFail") (VInt 3L) "Some"
            Expect.equal (run "3 |> try result |> orFail \"m\"") (VInt 3L) "Ok"
        }
        test "each message shape" {
            let err = "(if 1 == 1 then Error \"bad\" else Ok 1)"
            Expect.equal (raisesWith $"{err} |> orFail") "bad" "Error, no message"
            Expect.equal (raisesWith $"{err} |> orFail \"small\"") "small: bad" "Error, a message"
            Expect.equal (raisesWith "Str.tryToInt \"x\" |> orFail") "orFail: no value (None)" "None, no message"
            Expect.equal (raisesWith "Str.tryToInt \"x\" |> orFail \"no port\"") "no port" "None, a message"
            Expect.equal (raisesWith "Str.tryToInt \"x\" |> orFail $\"no {1 + 1}\"") "no 2" "a computed message"
        }
        test "an Error's text is data: controls render visibly [D:error-data-escape]" {
            Expect.equal
                (raisesWith "(if 1 == 1 then Error \"a\\nb\" else Ok 1) |> orFail \"m\"")
                "m: a\\x0ab"
                "LF escaped"
        }
        test "the raise points at the orFail site" {
            let src = "Some 1 |> Option.bind (fun _ -> Str.tryToInt \"x\") |> orFail \"gone\""
            Expect.equal (raisesWith src) "gone" "raises"
            Expect.isTrue RunPos.Exact "an exact column"

            match RunPos.Current with
            | :? TypedExpr as n -> Expect.equal n.Span.Start.Col (src.IndexOf "orFail" + 1) "the form's column"
            | other -> failtest $"expected the site node, got {other}"
        }
        test "|> try catches it like any raise" {
            Expect.equal (run "(Str.tryToInt \"x\" |> orFail \"m\") |> try") (VUnion("None", None)) "try"

            Expect.equal
                (run "Str.tryToInt \"x\" |> orFail \"m\" |> try result")
                (VUnion("Error", Some(VStr "m")))
                "try result, left-grouped"

            Expect.equal
                (run "(if 1 == 1 then fail \"boom\" else 3) |> try result |> orFail \"step\" |> try result")
                (VUnion("Error", Some(VStr "step: boom")))
                "the round trip"
        }
        test "composition: the unwrapped value pipes on" {
            Expect.equal (run "Str.tryToInt \"41\" |> orFail |> (fun n -> n + 1)") (VInt 42L) "pipes on"

            Expect.equal
                (run "[Some 1; Some 2] |> Seq.map (fun o -> o |> orFail) |> Seq.sum")
                (VInt 3L)
                "inside a lambda"

            Expect.equal (run "Some (3 |> try result) |> orFail |> orFail") (VInt 3L) "nested unwraps chain"
        }
        test "pure admits it — raising is not an effect" {
            errorsCleanIn
                "pure.weir"
                [
                    "let pure port s = Str.tryToInt s |> orFail \"not a port\""
                    "print (show (port \"80\"))"
                ]
                "pure"
        }
        test "orFail is reserved, and misplaced it teaches the form" {
            for src in [ "let x = orFail"; "let y = [ Some 1 ] |> Seq.map orFail" ] do
                let msgs = diagsIn "stray.weir" [ src ] |> List.map (fun d -> d.Message)
                Expect.exists msgs (fun m -> m.Contains "'orFail' goes after a pipeline") src

            Expect.isTrue (Weir.Parser.isKeyword "orFail") "reserved"
        }
        test "a command chain's |> orFail teaches the reifier; the reifier is untouched" {
            for src in [ "echo a |> orFail \"m\""; "echo a |> orFail"; "let r = echo a |> orFail" ] do
                let msgs =
                    diagsIn "cmd.weir" [ src; "print \"x\"" ] |> List.map (fun d -> d.Message)

                Expect.exists msgs (fun m -> m.Contains "use the reifier: `<command> | orFail \"msg\"`") src
                Expect.isFalse (msgs |> List.exists (fun m -> m.Contains "Expecting")) src

            errorsCleanIn "reifier.weir" [ "echo a | orFail \"m\"" ] "the reifier still parses"

            let msgs =
                diagsIn "val.weir" [ "let x = Some 1 | orFail \"m\"" ]
                |> List.map (fun d -> d.Message)

            Expect.exists msgs (fun m -> m.Contains "after a Result or an Option") "a value before the single pipe"
        }
    ]

// --help over case and type docs, the argv usage error, the bare-field
// hint, and effects before the CLI parse [D:help-case-docs]
// [D:args-usage-error] [D:effect-before-args]. Sequenced: help and the
// usage error name the entry script (the ambient Session.EntryPath), and
// Script.run sets it
let argsHelpTests =
    let union (name: string) cases docs : UnionDef = {
        Name = name
        Params = []
        Cases = cases
        Tag = None
        CaseWires = Map.empty
        OtherCase = None
        Validated = None
        OrdKey = []
        Docs = Map.ofList docs
    }

    let record (name: string) fields docs : RecordDef = {
        Name = name
        Params = []
        Fields = fields
        Attrs = Map.empty
        OrdKey = []
        Docs = Map.ofList docs
    }

    let withEntry (f: unit -> 'a) : 'a =
        let prior = Weir.Session.EntryPath
        Weir.Session.EntryPath <- "/scripts/j3.weir"

        try
            f ()
        finally
            Weir.Session.EntryPath <- prior

    let help target argv =
        withEntry (fun () -> Weir.Eval.argvUsage target argv)

    let usageErr target argv =
        withEntry (fun () ->
            try
                Weir.Eval.argvLoadWith target argv |> ignore
                failtest "must refuse"
            with :? Weir.Eval.ArgsUsageError as e ->
                e.Message)

    let mode =
        union "Mode" [ "Pub", None; "Sub", None ] [
            "Pub", "publishes a single message from prompt"
            "Sub", "subscribes with a handler"
        ]

    let cliOf (u: UnionDef) fields docs =
        record "Cli" (fields @ [ "mode", TNamed(u.Name, []) ]) docs

    let j3 = Weir.Check.ArgsShared(cliOf mode [] [], "mode", mode, Map.empty)

    let deployArgs =
        record "DeployArgs" [ "env", TStr ] [ "env", "target env"; "", "Ships builds." ]

    let cmd =
        union "Cmd" [ "Deploy", Some(TNamed("DeployArgs", [])); "Status", None ] [ "Status", "show status" ]

    let cmdUnion = Weir.Check.ArgsUnion(cmd, Map.ofList [ "Deploy", deployArgs ])

    let runCapture (lines: string list) (args: string list) : int * string =
        let path = tempPath "weir-args-help" + ".weir"
        System.IO.File.WriteAllLines(path, lines)
        let out = new System.IO.StringWriter()
        let prior = System.Console.Out
        System.Console.SetOut out

        try
            let code = Weir.Script.run path args
            // line-ending-neutral: Console.Out writes CRLF on Windows
            code, out.ToString().Replace("\r\n", "\n")
        finally
            System.Console.SetOut prior
            System.IO.File.Delete path

    testSequenced
    <| testList "Args help, usage errors, field hint, effect before args" [
        test "--help: case docs beside the commands, no empty section, no empty usage slot [D:help-case-docs]" {
            Expect.equal
                (help j3 [ "--help" ])
                (String.concat "\n" [
                    "usage: j3.weir <command>"
                    "commands:"
                    "  pub   publishes a single message from prompt"
                    "  sub   subscribes with a handler"
                ])
                "the j3 shape"

            Expect.equal
                (help j3 [ "pub"; "--help" ])
                "usage: j3.weir pub\n\npublishes a single message from prompt"
                "the case doc is the case-scoped description"
        }
        test "--help: globals keep their section and slot; a doc-less case shows the bare token [D:help-case-docs]" {
            let m = union "Mode" [ "Pub", None; "Sub", None ] [ "Pub", "publish" ]
            let cli = cliOf m [ "verbose", TBool ] [ "verbose", "talk more"; "", "The tool." ]
            let target = Weir.Check.ArgsShared(cli, "mode", m, Map.empty)

            Expect.equal
                (help target [ "--help" ])
                (String.concat "\n" [
                    "usage: j3.weir [global flags] <command>"
                    ""
                    "The tool."
                    ""
                    "global options:"
                    "  -v, --verbose               talk more"
                    "commands:"
                    "  pub   publish"
                    "  sub"
                ])
                "globals, the program description, a bare case"
        }
        test "--help: a flags-only record, with and without a description [D:help-case-docs]" {
            let plain = record "Cli" [ "clean", TBool ] [ "clean", "clean first" ]

            Expect.equal
                (help (Weir.Check.ArgsRecord plain) [ "--help" ])
                "usage: j3.weir [flags]\n  -c, --clean                 clean first"
                "unchanged without a type doc"

            let described = {
                plain with
                    Docs = plain.Docs |> Map.add "" "Cleans.\nThoroughly."
            }

            Expect.equal
                (help (Weir.Check.ArgsRecord described) [ "--help" ])
                "usage: j3.weir [flags]\n\nCleans.\nThoroughly.\n\n  -c, --clean                 clean first"
                "the first paragraph under the usage line"
        }
        test "--help: a payload case — its flags block, and the payload's doc when the case has none [D:help-case-docs]" {
            Expect.equal
                (help cmdUnion [ "--help" ])
                (String.concat "\n" [
                    "usage: j3.weir <command> [flags]"
                    "commands:"
                    "  deploy   Ships builds."
                    "  status   show status"
                    "deploy flags:"
                    "  -e, --env <string>          required — target env"
                ])
                "top-level"

            Expect.equal
                (help cmdUnion [ "deploy"; "--help" ])
                "usage: j3.weir deploy [flags]\n\nShips builds.\n\ndeploy flags:\n  -e, --env <string>          required — target env"
                "case-scoped: the payload doc"

            let caseDoc = {
                cmd with
                    Docs = cmd.Docs |> Map.add "Deploy" "ship it"
            }

            Expect.stringStarts
                (help (Weir.Check.ArgsUnion(caseDoc, Map.ofList [ "Deploy", deployArgs ])) [ "deploy"; "--help" ])
                "usage: j3.weir deploy [flags]\n\nship it\n"
                "the case doc wins"
        }
        test
            "usage error: missing and unknown commands carry the commands list; a did-you-mean replaces it [D:args-usage-error]" {
            let listed =
                "  pub   publishes a single message from prompt\n  sub   subscribes with a handler\n"

            Expect.equal
                (usageErr j3 [])
                ("j3.weir: missing command — one of:\n"
                 + listed
                 + "run 'j3.weir --help' for usage")
                "missing"

            Expect.equal
                (usageErr j3 [ "pbu" ])
                "j3.weir: unknown command 'pbu'. Did you mean 'pub'?\nrun 'j3.weir --help' for usage"
                "did-you-mean"

            Expect.equal
                (usageErr j3 [ "zzzz" ])
                ("j3.weir: unknown command 'zzzz' — one of:\n"
                 + listed
                 + "run 'j3.weir --help' for usage")
                "unknown, no suggestion"

            Expect.equal
                (usageErr (Weir.Check.ArgsUnion(mode, Map.empty)) [])
                ("j3.weir: missing command — one of:\n"
                 + listed
                 + "run 'j3.weir --help' for usage")
                "the bare-union twin"
        }
        test
            "usage error: one line per problem, the named flags' help lines, one problem per refused flag [D:args-usage-error]" {
            let def = record "Cli" [ "retries", TInt; "target", TStr ] []

            Expect.equal
                (usageErr (Weir.Check.ArgsRecord def) [ "--bogus"; "--retries"; "x" ])
                (String.concat "\n" [
                    "j3.weir: unknown flag '--bogus'"
                    "j3.weir: --retries: not an int: 'x'"
                    "j3.weir: missing required flag '--target'"
                    "  -r, --retries <int>         required"
                    "  -t, --target <string>       required"
                    "run 'j3.weir --help' for usage"
                ])
                "scan order then fills; a refused value is not also missing"

            Expect.equal
                (usageErr cmdUnion [ "deploy" ])
                "j3.weir: missing required flag '--env'\n  -e, --env <string>          required — target env\nrun 'j3.weir deploy --help' for usage"
                "a case's problem points at the case's help"

            Expect.equal
                (usageErr j3 [ "--mode"; "pub" ])
                "j3.weir: unknown flag '--mode' — the command is written bare, without a flag: 'j3.weir pub'\nrun 'j3.weir pub --help' for usage"
                "the command slot is no flag"
        }
        test
            "--help: a flag longer than the column wraps its text; string defaults render as data [D:args-help-column] [D:args-help-default]" {
            let long =
                record "Cli" [
                    "veryLongFlagNameThatExceedsTheColumn", TNamed("Option", [ TStr ])
                    "z", TNamed("Option", [ TSize ])
                ] [ "veryLongFlagNameThatExceedsTheColumn", "a long one" ]

            Expect.equal
                (help (Weir.Check.ArgsRecord long) [ "--help" ])
                (String.concat "\n" [
                    "usage: j3.weir [flags]"
                    "  -v, --very-long-flag-name-that-exceeds-the-column <string>"
                    "                              optional — a long one"
                    "  -z, --z <size>              optional"
                ])
                "the long flag's text wraps to the column; short rows keep it"

            let defaults = {
                record "Cli" [ "a", TStr; "b", TStr; "c", TStr ] [] with
                    Attrs =
                        Map.ofList [
                            "a", [ "Default", Some(AStr "x\ny") ]
                            "b", [ "Default", Some(AStr "") ]
                            "c", [ "Default", Some(AStr "  ") ]
                        ]
            }

            Expect.equal
                (help (Weir.Check.ArgsRecord defaults) [ "--help" ])
                (String.concat "\n" [
                    "usage: j3.weir [flags]"
                    "  -a, --a <string>            default: x\\x0ay"
                    "  -b, --b <string>            default: \"\""
                    "  -c, --c <string>            default: \"  \""
                ])
                "a newline renders visibly; an empty or blank default is quoted"
        }
        test "--help: an anonymous type lists its flags alphabetically [D:env-inline-type]" {
            let fields = [ "zeta", TBool; "alpha", TNamed("Option", [ TInt ]); "Mid", TStr ]
            let anon = record (Weir.Types.anonRecordName fields) (List.sortBy fst fields) []

            Expect.equal
                (help (Weir.Check.ArgsRecord anon) [ "--help" ])
                (String.concat "\n" [
                    "usage: j3.weir [flags]"
                    "  -a, --alpha <int>           optional"
                    "  -m, --mid <string>          required"
                    "  -z, --zeta"
                ])
                "by flag, not by the ordinal field sort (uppercase first)"
        }
        test
            "usage error: the value after an unknown flag is reported, never echoed; '--' gets no did-you-mean [D:args-unknown-flag-no-echo]" {
            let def = record "Cli" [ "tok", TSecret; "v2", TBool ] []

            Expect.equal
                (usageErr (Weir.Check.ArgsRecord def) [
                    "--tokn"
                    "hunter2"
                    "-x"
                    "hunter3"
                    "--tok"
                    "s"
                    "--"
                    "stray"
                ])
                (String.concat "\n" [
                    "j3.weir: unknown flag '--tokn'. Did you mean '--tok'?"
                    "j3.weir: unexpected argument after '--tokn'"
                    "j3.weir: unknown flag '-x'"
                    "j3.weir: unexpected argument after '-x'"
                    "j3.weir: unexpected '--' — weir's argv takes no end-of-options marker: every argument is a flag or its value"
                    "j3.weir: unexpected argument 'stray'"
                    "run 'j3.weir --help' for usage"
                ])
                "record twin"

            Expect.equal
                (usageErr (Weir.Check.ArgsRecord def) [ "--tokn=hunter2"; "--tokn"; "--tok"; "s" ])
                (String.concat "\n" [
                    "j3.weir: unknown flag '--tokn=…'. Did you mean '--tok'?"
                    "j3.weir: unknown flag '--tokn'. Did you mean '--tok'?"
                    "run 'j3.weir --help' for usage"
                ])
                "an attached value stays masked; a flag after an unknown flag is not swallowed"

            let goArgs = record "GoArgs" [ "tok", TSecret ] []
            let sub = union "Sub" [ "Go", Some(TNamed("GoArgs", [])); "Stop", None ] []
            let cli = record "Cli" [ "verbose", TBool; "cmd", TNamed("Sub", []) ] []
            let shared = Weir.Check.ArgsShared(cli, "cmd", sub, Map.ofList [ "Go", goArgs ])

            Expect.equal
                (usageErr shared [ "--verbos"; "hunter2"; "go"; "--tokn"; "hunter3"; "--tok"; "s" ])
                (String.concat "\n" [
                    "j3.weir: unknown flag '--verbos'. Did you mean '--verbose'?"
                    "j3.weir: unexpected argument after '--verbos'"
                    "j3.weir: unknown flag '--tokn'. Did you mean '--tok'?"
                    "j3.weir: unexpected argument after '--tokn'"
                    "run 'j3.weir go --help' for usage"
                ])
                "shared twin: the withheld value never anchors as the command"
        }
        test "usage error: every unknown flag spelling withholds the token after it [D:args-unknown-flag-no-echo]" {
            let def = record "Cli" [ "tok", TSecret; "n", TInt ] []

            Expect.equal
                (usageErr (Weir.Check.ArgsRecord def) [ "-tok"; "hunter2"; "-n"; "5"; "-zz"; "hunter3"; "--tok"; "s" ])
                (String.concat "\n" [
                    "j3.weir: unknown flag '-t***' — a flag's value is the next argument: write '-t <value>'"
                    "j3.weir: unexpected argument after '-t…'"
                    "j3.weir: unexpected argument '-z…'"
                    "j3.weir: unexpected argument after '-z…'"
                    "run 'j3.weir --help' for usage"
                ])
                "single-dash long and attached short"

            Expect.equal
                (usageErr cmdUnion [ "status"; "--tok"; "hunter2"; "-pw=x"; "hunter3"; "plain" ])
                (String.concat "\n" [
                    "j3.weir: unexpected argument '--tok' — 'status' takes nothing after it"
                    "j3.weir: unexpected argument after '--tok'"
                    "j3.weir: unexpected argument '-p…' — 'status' takes nothing after it"
                    "j3.weir: unexpected argument after '-p…'"
                    "j3.weir: unexpected argument 'plain' — 'status' takes nothing after it"
                    "run 'j3.weir status --help' for usage"
                ])
                "a bare case of a pure-union load"

            Expect.equal
                (usageErr cmdUnion [ "--tokk=hunter2"; "hunter3" ])
                (String.concat "\n" [
                    "j3.weir: unexpected flag '--tokk=…' — the command comes first, one of:"
                    "  deploy   Ships builds."
                    "  status   show status"
                    "run 'j3.weir --help' for usage"
                ])
                "a flag where a pure union's command goes"
        }
        test "usage error: an unknown flag never swallows a command word [D:args-unknown-flag-command]" {
            let deploy = record "Deploy" [ "tok", TSecret; "n", TInt ] []
            let sub = union "Sub" [ "Deploy", Some(TNamed("Deploy", [])); "Status", None ] []
            let cli = record "Cli" [ "verbose", TBool; "cmd", TNamed("Sub", []) ] []
            let shared = Weir.Check.ArgsShared(cli, "cmd", sub, Map.ofList [ "Deploy", deploy ])

            Expect.equal
                (usageErr shared [ "--verbos"; "deploy"; "--tok"; "x"; "-n"; "5" ])
                "j3.weir: unknown flag '--verbos'. Did you mean '--verbose'?\nrun 'j3.weir deploy --help' for usage"
                "only the did-you-mean; the payload flags bind"
        }
        test "usage error: every bad value reads '--flag: not a <type>: <value>' [D:boundary-bad-value]" {
            let def =
                record "Cli" [ "n", TInt; "r", TFloat; "q", TFloat; "t", TDur; "s", TInstant; "m", TSize ] []

            Expect.equal
                (usageErr (Weir.Check.ArgsRecord def) [
                    "-n"
                    "x"
                    "-r"
                    "abc"
                    "-q"
                    "inf"
                    "-t"
                    "2d"
                    "-s"
                    "yesterday"
                    "-m"
                    "lots"
                ])
                (String.concat "\n" [
                    "j3.weir: -n: not an int: 'x'"
                    "j3.weir: -r: not a float: 'abc'"
                    "j3.weir: -q: not a finite float: 'inf'"
                    "j3.weir: -t: not a duration: '2d' — units are ms, s, m, h"
                    "j3.weir: -s: not an ISO 8601 instant: 'yesterday' — e.g. 2026-08-14T12:34:56Z; offsets allowed, a bare date reads as midnight UTC"
                    "j3.weir: -m: not a size: 'lots' — expected digits then a unit (512B, 1.5MiB)"
                    "  -n, --n <int>               required"
                    "  -r, --r <float>             required"
                    "  -q, --q <float>             required"
                    "  -t, --t <duration>          required"
                    "  -s, --s <instant>           required"
                    "  -m, --m <size>              required"
                    "run 'j3.weir --help' for usage"
                ])
                "one shape for every field type"
        }
        test "usage error: a run exits 2 with no weir location; try still captures it [D:args-usage-error]" {
            let script = [
                "type Mode ="
                "    /// publishes a single message from prompt"
                "    | Pub"
                "    | Sub"
                "type Cli = { mode: Mode }"
                "let args = Args.load Cli"
                "print $\"{args}\""
            ]

            let code, _ = runCapture script []
            Expect.equal code 2 "a usage error exits 2"

            let caught = [
                "type Mode ="
                "    /// publishes a single message from prompt"
                "    | Pub"
                "    | Sub"
                "type Cli = { mode: Mode }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"pub   publishes a single message from prompt\" m && not (Str.contains \"Args.load\" m) -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            let code, _ = runCapture caught []
            Expect.equal code 0 "the case doc reached the run's UnionDef; try captured the usage text"
        }
        test "--help over a run: the type doc's first paragraph, the case docs [D:help-case-docs]" {
            let script = [
                "/// My great tool for doing nothing"
                "/// (still the first paragraph)"
                "///"
                "/// hover-only"
                "type Mode ="
                "    /// publishes"
                "    | Pub"
                "    | Sub"
                "let args = Args.load Mode"
                "print $\"{args}\""
            ]

            let code, out = runCapture script [ "--help" ]
            Expect.equal code 0 "--help exits 0"

            Expect.stringContains
                out
                "<command>\n\nMy great tool for doing nothing\n(still the first paragraph)\n\ncommands:\n  pub   publishes\n  sub\n"
                "the description block and the commands"

            Expect.isFalse (out.Contains "hover-only") "the rest of the doc is hover-only"
        }
        test "field hint: a bound value, a load target, a bare record; a module member follows" {
            let msgOf lines = (firstErrorIn "h.weir" lines).Message

            Expect.equal
                (msgOf [
                    "type Mode = Pub | Sub"
                    "type Cli = { mode: Mode }"
                    "let args = Args.load Cli"
                    "match mode with"
                    "| Pub -> print \"p\""
                    "| Sub -> print \"s\""
                ])
                "unbound 'mode' — did you mean 'args.mode'? ('mode' is a field of 'Cli')"
                "a bound value of the record: confident, File.mode dropped"

            Expect.equal
                (msgOf [
                    "type Mode = Pub | Sub"
                    "type Cli = { mode: Mode }"
                    "print $\"{mode}\""
                    "let args = Args.load Cli"
                    "print $\"{args}\""
                ])
                "unbound 'mode' — it is a field of 'Cli': load it first ('let cli = Args.load Cli') and write 'cli.mode'; or did you mean 'File.mode'?"
                "a load target with no value yet"

            Expect.equal
                (msgOf [
                    "type Cfg = { PORT: int }"
                    "print (show PORT)"
                    "let e = Env.load Cfg"
                    "print $\"{e}\""
                ])
                "unbound 'PORT' — it is a field of 'Cfg': load it first ('let env = Env.load Cfg') and write 'env.PORT'"
                "the Env.load twin"

            Expect.equal
                (msgOf [
                    "type Mode = Pub | Sub"
                    "type Cli = { mode: Mode }"
                    "print $\"{mode}\""
                    "let args ="
                    "    Args.load"
                    "        Cli"
                    "print $\"{args}\""
                ])
                "unbound 'mode' — it is a field of 'Cli': load it first ('let cli = Args.load Cli') and write 'cli.mode'; or did you mean 'File.mode'?"
                "a multi-line Args.load call is read off the parsed statement"

            Expect.equal
                (msgOf [ "type Cfg = { mode: string }"; "print mode"; "print \"Args.load Cfg\"" ])
                "unbound 'mode' — it is a field of 'Cfg' — write 'x.mode' on a 'Cfg' value; or did you mean 'File.mode'?"
                "text in a string is no load"

            Expect.equal
                (msgOf [ "type Cfg = { mode: string }"; "print mode" ])
                "unbound 'mode' — it is a field of 'Cfg' — write 'x.mode' on a 'Cfg' value; or did you mean 'File.mode'?"
                "a declared record, nothing loaded"

            Expect.equal
                (msgOf [ "print (show mode)" ])
                "'mode' is module-qualified; use 'File.mode'"
                "no record field: the module member as before"

            Expect.equal
                (msgOf [ "print (show name)" ])
                "unbound variable 'name'"
                "a prelude record's field (FileRow.name) never answers"
        }
        test
            "effect before args: writes, commands, network and scopes warn; reads, print, functions and wrappers do not [D:effect-before-args]" {
            let warns lines =
                diagsIn "e.weir" lines
                |> List.filter (fun d -> d.Code = "effect-before-args")
                |> List.map (fun d -> d.Line, d.Col, d.Severity)

            let cli = "type Cli = { n: Option<int> }"
            let load = "let cli = Args.load Cli"
            let usage = "print $\"{cli}\""

            Expect.equal (warns [ cli; "Dir.create \"x\""; load; usage ]) [ 2, 1, "warning" ] "a filesystem write"

            let msg =
                diagsIn "e.weir" [ cli; "Dir.create \"x\""; load; usage ]
                |> List.find (fun d -> d.Code = "effect-before-args")
                |> fun d -> d.Message

            Expect.equal
                msg
                "'Dir.create' runs before 'Args.load Cli' (line 3) — on --help or a bad argument it still runs; load the arguments first"
                "the message names the effect and the load line"

            Expect.equal (warns [ cli; load; "Dir.create \"x\""; usage ]) [] "after the load: silent"
            Expect.equal (warns [ cli; "git status"; load; usage ]) [ 2, 1, "warning" ] "a statement command"

            Expect.equal
                (warns [ cli; "let r = git status | complete"; load; "print $\"{cli} {r.exitCode}\"" ])
                [ 2, 9, "warning" ]
                "a reified command, at its program"

            Expect.equal
                (warns [ cli; "let fs = git ls-files"; load; "print $\"{cli} {fs |> Seq.length}\"" ])
                []
                "a lazy command let spawns nothing"

            Expect.equal
                (warns [
                    cli
                    "let r = Http.send (Http.get \"http://localhost:1\")"
                    load
                    "print $\"{cli} {r.status}\""
                ])
                [ 2, 9, "warning" ]
                "the network"

            Expect.equal
                (warns [ cli; "if not (Dir.exists \"x\") then Dir.create \"x\""; load; usage ])
                [ 2, 30, "warning" ]
                "a branch's write still runs on --help"

            Expect.equal
                (warns [ cli; "within tmp d"; "    print d"; load; usage ])
                [ 2, 1, "warning" ]
                "a within tmp before the load"

            Expect.equal
                (warns [
                    cli
                    "let h = Path.stateHome ()"
                    "let e = Env.get \"HOME\""
                    "print h"
                    "printerr \"starting\""
                    "let setup () = Dir.create \"x\""
                    load
                    "print $\"{cli} {e}\""
                    "setup ()"
                ])
                []
                "reads, console output and a function definition"

            Expect.equal
                (warns [
                    cli
                    "within tmp d"
                    "    let cli = Args.load Cli"
                    "    print $\"{cli} {d}\""
                ])
                []
                "a within wrapping the load"

            Expect.equal
                (warns [
                    cli
                    "Dir.create \"x\""
                    "let go () ="
                    "    let cli = Args.load Cli"
                    "    print $\"{cli}\""
                    "go ()"
                ])
                []
                "a load only inside a function: the order is a runtime fact"

            Expect.equal
                (warns [
                    "type Cfg = { HOME: string }"
                    "File.write \"x\" [\"a\"]"
                    "let c = Env.load Cfg"
                    "print c.HOME"
                ])
                [ 2, 1, "warning" ]
                "Env.load is a parse point too"

            Expect.equal
                (warns [
                    "module"
                    "type Cli = { n: Option<int> }"
                    "let f : unit -> unit"
                    "let f () = Dir.create \"x\""
                ])
                []
                "a module never judges"
        }
    ]

// the overlay findings run whole scripts: `within env` pushes the
// session's root overlay, one stack every test thread shares
let portOverlayTests =
    let runFile (lines: string list) : int =
        let path = tempPath "weir-port" + ".weir"
        File.WriteAllLines(path, lines)

        try
            Weir.Script.run path []
        finally
            File.Delete path

    testSequenced
    <| testList "starship port findings: the env overlay" [
        test "home and the XDG dirs read the within-env overlay [D:home-overlay]" {
            skipOnWindows ()
            let home = Weir.Builtins.homeDir ()

            Expect.equal
                (runFile [
                    "within env HOME=\"/fake/home\" XDG_CONFIG_HOME=\"\" XDG_CACHE_HOME=\"/fake/cache\""
                    "    if Path.home () <> \"/fake/home\" then exit 3"
                    "    if Path.configHome () <> \"/fake/home/.config\" then exit 4"
                    "    if Path.cacheHome () <> \"/fake/cache\" then exit 5"
                    "    let typed = echo ~/x |> Seq.exactlyOne"
                    "    if typed <> \"/fake/home/x\" then exit 6"
                    $"if Path.home () <> \"{weirPath home}\" then exit 7"
                ])
                0
                "Path.home, configHome, cacheHome and a typed ~ follow the overlay; outside it, the process home"
        }
        test "a dynamic head resolves on the PATH the child runs with [D:overlay-path]" {
            skipOnWindows ()

            withTempDir "weir-port-path" (fun dir ->
                let tool = Path.Combine(dir, "weirporttoolzz")
                File.WriteAllText(tool, "#!/bin/sh\necho ran-from-overlay\n")

                File.SetUnixFileMode(
                    tool,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                let p = System.Environment.GetEnvironmentVariable "PATH"
                let path = $"{weirPath dir}:{p}"

                Expect.equal
                    (runFile [
                        "let tool = \"weirporttoolzz\""
                        $"within env PATH=\"{path}\""
                        "    let r = ^$tool | line"
                        "    if r <> \"ran-from-overlay\" then exit 3"
                        $"let r2 = PATH=\"{path}\" ^$tool | line"
                        "if r2 <> \"ran-from-overlay\" then exit 4"
                    ])
                    0
                    "within env's PATH and a per-command PATH= prefix"

                // weir's own PATH lacks it: off the overlay it is not found
                Expect.equal (runFile [ "let tool = \"weirporttoolzz\""; "^$tool" ]) 1 "not found off the overlay")
        }
        test "a literal head under an overlay PATH that lacks it is not found, as the child would see [D:overlay-path]" {
            skipOnWindows ()

            Expect.equal
                (runFile [ "within env PATH=\"/weir-no-such-dir\""; "    sh -c \"echo hi\"" ])
                1
                "the overlay PATH decides at run time"
        }
        test "Path.which finds what a command head would run, on the overlay PATH too [D:path-which]" {
            skipOnWindows ()

            withTempDir "weir-which" (fun dir ->
                let tool = Path.Combine(dir, "weirwhichtoolzz")
                File.WriteAllText(tool, "#!/bin/sh\necho hi\n")

                File.SetUnixFileMode(
                    tool,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                let p = System.Environment.GetEnvironmentVariable "PATH"
                let path = $"{weirPath dir}:{p}"
                let full = weirPath tool

                Expect.equal
                    (runFile [
                        "if Path.which \"weirwhichtoolzz\" <> None then exit 3"
                        $"within env PATH=\"{path}\""
                        $"    if Path.which \"weirwhichtoolzz\" <> Some \"{full}\" then exit 4"
                        "    if Path.which \"sh\" == None then exit 5"
                        $"if Path.which \"{full}\" <> Some \"{full}\" then exit 6"
                        "if Path.which \"weir-no-such-toolzz\" <> None then exit 7"
                        "if Path.which \"\" <> None then exit 8"
                        "within env PATH=\"/weir-no-such-dir\""
                        "    if Path.which \"sh\" <> None then exit 9"
                    ])
                    0
                    "the overlay PATH decides; weir's own PATH outside it; a path names itself")

            // an ambient read: readonly admits it (pure refuses it, pinned with the home dirs)
            Expect.equal (Weir.Effects.effectClass "Path.which") (Some Weir.Effects.Ambient) "ambient"
        }
    ]

// the list-free child walk agrees with childExprs — same children, same
// order, same first hit — over trees covering the expression kinds
let childWalkTests =
    testList "typed-tree child walk" [
        test "iterChildren visits childExprs' children in order; tryPickChild returns List.tryPick's hit" {
            let snippets = [
                "let f x = x + 1 in [ f 1; f 2 ] |> Seq.map (fun y -> y * 2) |> Seq.freeze"
                "let r = P { X = 1; Y = 2 } in { r with X = 3 }.X + 1"
                "match (if true then Running 1 else Stopped) with | Running n when n > 0 -> n | Running n -> n | Stopped -> 0"
                "$\"a {1 + 2} b {show [1; 2]}\""
                "let xs = [1; 2; 3] in xs[1..2], xs[^1], xs[1], Seq.length xs"
                "git rev-parse HEAD | line"
                "echo hi there | line"
                "[1] |> Seq.tryHead |> orFail \"empty\""
                "(print \"a\"; 2)"
            ]

            let mutable nodes = 0

            let rec walk (te: Weir.Check.TypedExpr) =
                nodes <- nodes + 1
                let listed = Weir.Check.childExprs te
                let visited = ResizeArray()
                Weir.Check.iterChildren visited.Add te
                Expect.sequenceEqual (List.ofSeq visited) listed "iterChildren = childExprs"

                let isVar (c: Weir.Check.TypedExpr) =
                    match c.Kind with
                    | Weir.Check.TEVar v -> Some v
                    | _ -> None

                Expect.equal (Weir.Check.tryPickChild isVar te) (List.tryPick isVar listed) "first hit"

                Expect.equal
                    (Weir.Check.existsChild (isVar >> Option.isSome) te)
                    (List.exists (isVar >> Option.isSome) listed)
                    "exists"

                listed |> List.iter walk

            let failures =
                snippets
                |> List.choose (fun src ->
                    match Weir.Parser.parseLine cmdResolver src with
                    | Error msg -> Some $"{src}: parse: {msg}"
                    | Ok(SExpr e)
                    | Ok(SCmd e) ->
                        match Weir.Check.typecheck (env |> declare "type P = { X: int; Y: int }") e with
                        | Ok te ->
                            walk te
                            None
                        | Error e -> Some $"{src}: {e.Message}"
                    | Ok other -> Some $"{src}: not an expression line: {other}")

            Expect.isEmpty failures (String.concat "\n" failures)
            Expect.isGreaterThan nodes 80 "the samples cover enough of the tree"
        }
    ]
