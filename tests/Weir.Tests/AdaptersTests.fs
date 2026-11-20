module AdaptersTests

// The adapters suites. A suite sits in the file of the area it mostly
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
open CheckerTests
open EvalTests
open BuiltinsTests

let boundaryTests =
    testList "External command boundary" [
        test "cmd yields stdout lines" {
            skipOnWindows ()
            Expect.equal (runReal "sh -c \"printf 'a\\nb\\n'\"" |> forceSeq) [ VStr "a"; VStr "b" ] ""
        }
        test "cmd is lazy across the process boundary" {
            skipOnWindows ()
            Expect.equal (runReal "sh -c \"yes\" |> take 3" |> forceSeq) [ VStr "y"; VStr "y"; VStr "y" ] ""
        }
        test "failing command raises when forced" {
            skipOnWindows ()
            Expect.throws (fun () -> runReal "sh -c \"exit 3\"" |> forceSeq |> ignore) ""
        }
        test "unforced command runs nothing" {
            skipOnWindows ()
            runReal "sh -c \"exit 3\"" |> ignore
        }
        // the spawn-boundary NUL funnel [D:spawn-nul-funnel]: the NUL
        // refusal lives at Proc.spawn, the one point every process start
        // funnels through, so the four downstream paths that skipped the
        // evaluator's statement-path refusal (the reifiers, the ambient
        // `within env` overlay, `into`, the dynamic head) inherit it. The
        // guard runs before Process.Start, so a raise proves no child
        // spawned. Each pin drives one public Proc entry with a NUL-bearing
        // word and asserts the located refusal; a control proves a clean
        // spawn still runs.
        let nul = "a\u0000b"

        let nulMsg (f: unit -> unit) (what: string) =
            let ex = Expect.throwsC f id
            Expect.stringContains ex.Message "NUL byte" $"{what}: the NUL diagnostic"
            Expect.stringContains ex.Message "silently truncate" $"{what}: the truncation reason"

        test "funnel: a NUL command ARGUMENT refuses (lines/cmd path) [D:spawn-nul-funnel]" {
            skipOnWindows ()
            nulMsg (fun () -> Weir.Proc.lines "echo" [ nul ] None |> Seq.iter ignore) "arg"
        }

        test "funnel: a NUL argument refuses in the REIFIER path (complete) [D:spawn-nul-funnel]" {
            skipOnWindows ()
            // the exact seam the reifier builtins reach — completeWith
            nulMsg (fun () -> Weir.Proc.complete "echo" [ nul ] None |> ignore) "reifier arg"
        }

        test "funnel: a NUL argument refuses in the exitCode/streamCode path [D:spawn-nul-funnel]" {
            skipOnWindows ()
            nulMsg (fun () -> Weir.Proc.streamCode [] "echo" [ nul ] |> ignore) "streamCode arg"
        }

        test "funnel: a NUL env VALUE refuses (ambient/$e overlay) [D:spawn-nul-funnel]" {
            skipOnWindows ()
            nulMsg (fun () -> Weir.Proc.linesWith [ "FOO", nul ] "echo" [ "hi" ] None |> Seq.iter ignore) "env value"

            let ex =
                Expect.throwsC (fun () -> Weir.Proc.linesWith [ "FOO", nul ] "echo" [ "hi" ] None |> Seq.iter ignore) id

            Expect.stringContains ex.Message "the env value for 'FOO'" "the env value refusal names the key"
        }

        test "funnel: a NUL env KEY refuses [D:spawn-nul-funnel]" {
            skipOnWindows ()

            let ex =
                Expect.throwsC (fun () -> Weir.Proc.linesWith [ nul, "v" ] "echo" [ "hi" ] None |> Seq.iter ignore) id

            Expect.stringContains ex.Message "the env key" "the env key refusal names the key"
            Expect.stringContains ex.Message "NUL byte" "the env key refusal is the NUL diagnostic"
        }

        test "funnel: a NUL in the `into` cmdline refuses (into path) [D:spawn-nul-funnel]" {
            skipOnWindows ()
            // into spawns `sh -c <cmdline>` — the cmdline is arg[1]
            nulMsg
                (fun () -> Weir.Proc.lines "sh" [ "-c"; $"echo before{nul}after" ] None |> Seq.iter ignore)
                "sh -c cmdline"
        }

        test "funnel: a NUL PROGRAM name refuses (dynamic head ^$name) [D:spawn-nul-funnel]" {
            skipOnWindows ()
            // the dynamic-head worst case: a NUL-bearing head would
            // resolve to the prefix program and run it
            nulMsg (fun () -> Weir.Proc.lines $"echo{nul}junk" [ "hi" ] None |> Seq.iter ignore) "program name"
        }

        test
            "funnel: a NUL PATH-like program name refuses at resolve, not a raw platform exception [D:spawn-nul-funnel]" {
            skipOnWindows ()
            // resolveProg runs before the spawn funnel and called
            // Path.GetFullPath, which raised a raw 'Null character in
            // path' — the funnel's diagnostic covers the resolve too
            let ex =
                Expect.throwsC (fun () -> Weir.Proc.resolveProg $"./bin{nul}/x" |> ignore) id

            Expect.stringContains ex.Message "NUL byte" "the resolve refusal is the NUL diagnostic"
            Expect.isFalse (ex.Message.Contains "Null character in path") "the raw platform exception no longer leaks"
        }

        test "funnel: an EMPTY program name refuses with a weir diagnostic [D:spawn-nul-funnel]" {
            skipOnWindows ()

            let ex =
                Expect.throwsC (fun () -> Weir.Proc.lines "" [ "hi" ] None |> Seq.iter ignore) id

            Expect.stringContains ex.Message "program name is empty" "the empty-program refusal"
        }

        test "funnel control: a CLEAN command still spawns (no false refusal) [D:spawn-nul-funnel]" {
            skipOnWindows ()
            Expect.equal (Weir.Proc.lines "echo" [ "ok" ] None |> List.ofSeq) [ "ok" ] "a clean spawn is untouched"
        }
        // the single-capture cap [D:capture-cap-error]: reaching it is a
        // weir error naming the cap and the command — the 2 GiB cap's
        // own run is the e2e cell; here a small cap drives the same path
        let capSpec prog args : Weir.Proc.Spec = {
            Prog = prog
            Args = args
            Env = []
            Input = None
            Cwd = None
            Ambient = None
        }

        test "a capture reaching its cap is a weir error naming the cap and the command [D:capture-cap-error]" {
            skipOnWindows ()

            let ex =
                Expect.throwsC
                    (fun () ->
                        Weir.Proc.completedCapped 1024L true (capSpec "sh" [ "-c"; "head -c 5000 /dev/zero" ])
                        |> ignore)
                    id

            Expect.equal
                ex.Message
                "the output of 'sh -c head -c 5000 /dev/zero' reached the 1 KiB single-capture cap — a capture holds the whole output in memory; stream it instead: run the command bare, iterate its lines (`cmd |> Seq.iter …`), or write them to a file (`cmd |> File.write path`) and read back what you need"
                "the cap and the command, never a silent truncation"

            let chainEx =
                Expect.throwsC
                    (fun () ->
                        Weir.Proc.chainCompletedCapped 1024L true [
                            capSpec "sh" [ "-c"; "head -c 5000 /dev/zero" ]
                            capSpec "cat" []
                        ]
                        |> ignore)
                    id

            Expect.stringStarts
                chainEx.Message
                "the output of 'sh -c head -c 5000 /dev/zero | cat' reached the 1 KiB single-capture cap"
                "a chain names every stage"

            Expect.stringStarts
                (Weir.Proc.captureCapMessage Weir.Proc.captureCap "x")
                "the output of 'x' reached the 2 GiB single-capture cap"
                "the real cap reads 2 GiB"
        }

        test "a capture under its cap is whole; a code-only capture holds nothing and owes no cap [D:capture-cap-error]" {
            skipOnWindows ()

            let code, out, _ =
                Weir.Proc.completedCapped 1024L true (capSpec "sh" [ "-c"; "printf 'a\\nb\\n'" ])

            Expect.equal (code, List.ofSeq out) (0, [ "a"; "b" ]) "under the cap"

            let code, out, _ =
                Weir.Proc.completedCapped 1024L false (capSpec "sh" [ "-c"; "head -c 50000 /dev/zero" ])

            Expect.equal (code, List.ofSeq out) (0, []) "drained, unheld"
        }
        // E2BIG is the command's size, not a missing program [D:start-failure-errno]
        test "an argument list past the OS limit is named, not 'command not found' [D:start-failure-errno]" {
            skipOnWindows ()
            let big = System.String('a', 3_000_000)

            let ex =
                Expect.throwsC (fun () -> Weir.Proc.lines "echo" [ big ] None |> Seq.iter ignore) id

            Expect.equal
                ex.Message
                "cannot start echo: its arguments and environment are too large for the OS (argument list too long; 2.8 MiB of them in this command alone) — pass big data on stdin (`xs | cmd`) or through a file, not as an argument"
                "E2BIG named with the size and the way out"

            Expect.equal
                (Weir.Proc.startFailureMessage "nosuch" [] [] 2)
                "command not found or not executable: nosuch"
                "ENOENT keeps the not-found message"
        }
        // a failed exec leaves the session as it found it
        // [D:exec-failure-restores]; the program never exists, so no
        // path can replace the test process
        test "a failed exec restores its env prefix and the process cwd [D:exec-failure-restores]" {
            skipOnWindows ()
            let key = "WEIR_UNIT_EXEC_LEAK"
            let cwd = System.IO.Directory.GetCurrentDirectory()

            Expect.throws
                (fun () ->
                    Weir.Proc.exec {
                        capSpec "weir-unit-no-such-prog" [] with
                            Env = [ key, "leaked" ]
                            Cwd = Some "/"
                    })
                "the exec failed"

            Expect.isNull (System.Environment.GetEnvironmentVariable key) "the prefix did not leak"
            Expect.equal (System.IO.Directory.GetCurrentDirectory()) cwd "the cwd did not leak"
        }
        // the runtime backstop [D:exec-scope-transitive]
        test "exec refuses at run time inside a parallel arm and while an always is live [D:exec-scope-transitive]" {
            skipOnWindows ()

            let execNoSuch () =
                Weir.Proc.exec (capSpec "weir-unit-no-such-prog" [])

            let depth = Weir.Session.parallelDepthNow ()
            Weir.Session.setParallelDepth (depth + 1)

            let arm =
                try
                    Expect.throwsC execNoSuch id
                finally
                    Weir.Session.setParallelDepth depth

            Expect.equal
                arm.Message
                "'exec' inside a parallel arm (pfor, Seq.pmap, Seq.piter, Seq.pfirst) is refused — exec replaces weir's process, so the other arms would be orphaned and the join would never run; run the command without exec, or exec after the fan-out"
                "an arm"

            let hooked = Weir.Session.registerAlways ()

            let scope =
                try
                    Expect.throwsC execNoSuch id
                finally
                    Weir.Session.deregisterAlways hooked |> ignore

            Expect.equal
                scope.Message
                "'exec' while a scope is open (a within or always block) is refused — exec replaces weir's process, so the scope's teardown would never run; run the command without exec, or exec after the scope"
                "a live always"
        }

        test "statement-path control stays green: a NUL arg refuses via the evaluator too [D:spawn-nul-funnel]" {
            skipOnWindows ()
            // the pre-existing evaluator refusal must not regress — a
            // command-output NUL (YQBi = "a\0b") fed to a plain command
            // statement's argument still refuses at force
            let ex =
                Expect.throwsC
                    (fun () ->
                        runReal "echo $(sh -c \"printf YQBi | base64 -d\" |> Seq.exactlyOne)"
                        |> forceSeq
                        |> ignore)
                    id

            Expect.stringContains ex.Message "NUL byte" "the statement path still refuses"
        }

        test "to jsonl serializes records as ndjson" {
            let src =
                VSeq [
                    VRecord("JRow", [ "name", VStr "a.txt"; "bytes", VInt 0L; "readOnly", VBool false ])
                ]

            Expect.equal
                (runWith [ "src", src ] "src |> to jsonl" |> forceSeq)
                [ VStr """{"name":"a.txt","bytes":0,"readOnly":false}""" ]
                ""
        }

        test "ls rows no longer cross the wire: Size is non-representable there [D:size]" {
            // an int bytes field would roundtrip here — the Size type
            // is a wire-boundary change, stated and pinned
            let m = (checkErr "ls |> to json").Message

            // the first refusing field moved with the reshape: kind's
            // union now leads (declaration order) [D:filerow]
            Expect.stringContains m "'FileKind' is an untagged union" "the refusal names the union field"
        }

        test "json roundtrip preserves rows (the jsonable stand-in)" {
            let src =
                VSeq [
                    VRecord("JRow", [ "name", VStr "a"; "bytes", VInt 5L; "readOnly", VBool false ])
                ]

            Expect.equal
                (runWith [ "src", src ] "src |> to jsonl |> from jsonl JRow"
                 |> forceSeq
                 |> List.length)
                1
                ""
        }

        test "from jsonl validates field types" {
            let src = VSeq [ VStr """{"name":"x","bytes":"big","readOnly":false}""" ]

            Expect.throws (fun () -> runWith [ "src", src ] "src |> from jsonl JRow" |> forceSeq |> ignore) ""
        }

        test "from jsonl rejects missing fields" {
            let src = VSeq [ VStr """{"name":"x"}""" ]

            Expect.throws (fun () -> runWith [ "src", src ] "src |> from jsonl JRow" |> forceSeq |> ignore) ""
        }

        test "from jsonl ignores extra fields" {
            let src =
                VSeq [ VStr """{"name":"x","Size":1,"bytes":1048576,"readOnly":true,"Extra":42}""" ]

            Expect.equal
                (runWith [ "src", src ] "src |> from jsonl JRow" |> forceSeq)
                [
                    VRecord("JRow", [ "name", VStr "x"; "bytes", VInt 1048576L; "readOnly", VBool true ])
                ]
                ""
        }

        test "from jsonl skips blank lines — a trailing newline is not a document (B27)" {
            let row = """{"name":"x","bytes":1,"readOnly":true}"""
            let src = VSeq [ VStr ""; VStr row; VStr "   "; VStr row; VStr "\r"; VStr "" ]

            Expect.equal
                (runWith [ "src", src ] "src |> from jsonl JRow |> Seq.length")
                (VInt 2L)
                "blank and whitespace-only lines are skipped"

            let bad = VSeq [ VStr row; VStr "x" ]

            Expect.throws
                (fun () -> runWith [ "src", bad ] "src |> from jsonl JRow" |> forceSeq |> ignore)
                "a non-blank bad line still fails"
        }

        test "from jsonl: an Option field reads present as Some, missing AND null as None [D:json-option]" {
            let src =
                VSeq [
                    VStr """{"name":"a","age":5}"""
                    VStr """{"name":"b"}"""
                    VStr """{"name":"c","age":null}"""
                ]

            Expect.equal
                (runWith [ "src", src ] "src |> from jsonl JOpt |> Seq.map _.age" |> forceSeq)
                [ VUnion("Some", Some(VInt 5L)); VUnion("None", None); VUnion("None", None) ]
                "present -> Some; missing and null both -> None"
        }

        test "to jsonl omits a None field; the Option roundtrip holds [D:json-option]" {
            let src = VSeq [ VStr """{"name":"a","age":5}"""; VStr """{"name":"b"}""" ]

            Expect.equal
                (runWith [ "src", src ] "src |> from jsonl JOpt |> to jsonl" |> forceSeq)
                [ VStr """{"name":"a","age":5}"""; VStr """{"name":"b"}""" ]
                "Some writes the scalar; None OMITS its key (the fork)"

            let once = runWith [ "src", src ] "src |> from jsonl JOpt" |> forceSeq

            let twice =
                runWith [ "src", src ] "src |> from jsonl JOpt |> to jsonl |> from jsonl JOpt"
                |> forceSeq

            Expect.equal twice once "to jsonl |> from jsonl is identity with a None field"
        }

        test "to json writes ONE document: a seq is an array, strict at the call [D:to-jsonl]" {
            let pulled = ref 0

            let src =
                VSeq(
                    Seq.init 2 (fun i ->
                        incr pulled
                        VInt(int64 i))
                )

            let v = runWith [ "src", src ] "src |> to json"
            Expect.equal pulled.Value 2 "one line cannot stream — the array build forces the source"
            Expect.equal (forceSeq v) [ VStr "[0,1]" ] "one array document"
        }

        test "the empty seq: to json writes '[]', to jsonl writes zero lines [D:to-jsonl]" {
            Expect.equal
                (run "[\"x\"] |> where (fun _ -> false) |> to json" |> forceSeq)
                [ VStr "[]" ]
                "an empty array is still one document"

            Expect.equal
                (run "[\"x\"] |> where (fun _ -> false) |> to jsonl" |> forceSeq)
                []
                "no elements, no documents"
        }

        test "roundtrips pair by name [D:to-jsonl]: an array document reads back via from json seq<T>" {
            let src =
                VSeq [
                    VRecord("JRow", [ "name", VStr "a"; "bytes", VInt 1L; "readOnly", VBool false ])
                ]

            Expect.equal
                (runWith [ "src", src ] "src |> to json |> from json seq<JRow> |> Seq.length")
                (VInt 1L)
                "to json |> from json seq<T> — every adapter pairs with its own name"
        }

        test "json boundary: null-in-required teaches Option; nested Option and Option-of-record reject [D:json-option]" {
            // null in a required field -> runtime error naming the fix
            let srcNull = VSeq [ VStr """{"name":null,"age":5}""" ]

            let msg =
                try
                    runWith [ "src", srcNull ] "src |> from jsonl JOpt" |> forceSeq |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains msg "declare it Option" "null-in-required names the remedy"

            // nested Option<Option<int>> rejects at check, naming the flatten
            let eNested = env |> declare "type JN = { z: Option<Option<int>> }"

            match typecheck eNested (parse "src |> from json JN") with
            | Error terr ->
                Expect.stringContains terr.Message "Option<Option<" "nested Option rejected"
                Expect.stringContains terr.Message "flatten" "the repair"
            | Ok _ -> failtest "nested Option should reject"

            // Option of a record is admitted under the recursive law
            // [D:recursive-fields] (the flat-row refusal retired)
            let eRec = env |> declare "type JR = { r: Option<Point> }"

            match typecheck eRec (parse "src |> from json JR") with
            | Ok _ -> ()
            | Error terr -> failtest $"Option of a record must be admitted now: {terr.Message}"
        }

        test "module marker parses: bare and named [D:modules-v1]" {
            match Weir.Parser.parseLine cmdResolver "module Git" with
            | Ok(SModule(Some "Git", _)) -> ()
            | other -> failtest $"expected SModule Git, got {other}"

            match Weir.Parser.parseLine cmdResolver "module" with
            | Ok(SModule(None, _)) -> ()
            | other -> failtest $"expected bare SModule, got {other}"
        }

        test "import parses: plain and aliased; path is a literal string [D:modules-v1]" {
            match Weir.Parser.parseLine cmdResolver "import \"./lib/paths.weir\"" with
            | Ok(SImport("./lib/paths.weir", _, None)) -> ()
            | other -> failtest $"expected SImport, got {other}"

            match Weir.Parser.parseLine cmdResolver "import \"./x.weir\" as P" with
            | Ok(SImport("./x.weir", _, Some("P", _))) -> ()
            | other -> failtest $"expected aliased SImport, got {other}"

            Expect.isError (Weir.Parser.parseLine cmdResolver "import foo") "a bare word is not a literal path"
        }

        test "module and import are reserved words [D:modules-v1]" {
            Expect.isError (Weir.Parser.parseLine cmdResolver "let import = 1") "import reserved"
            Expect.isError (Weir.Parser.parseLine cmdResolver "let module = 1") "module reserved"
        }

        test "for/do desugars to Seq.iter — the typed tree never sees `for` [D:for-do]" {
            expectParse "for x in [1; 2] do print (show x)" "([1; 2] |> (|seqIter (funpat x (print (show x)))))"
            // tuple binder rides binderPat
            expectParse "for (k, v) in pairs do print k" "(pairs |> (|seqIter (funpat (k, v) (print k))))"
        }

        test "for/do equivalence: both spellings evaluate identically [D:for-do]" {
            skipOnWindows ()

            Expect.equal
                (runReal "for x in [\"a\"; \"b\"] do print x" |> ignore
                 runReal "[\"a\"; \"b\"] |> Seq.iter (fun x -> print x)")
                (runReal "for x in [\"a\"; \"b\"] do print x")
                "byte-identical desugar"
        }

        test "a bare command body is an armed statement — pipes into print [D:for-do]" {
            match Weir.Parser.parseLine cmdResolver "for f in xs do git add $f" with
            | Ok(SExpr e | SCmd e) ->
                Expect.stringContains (show e) "(cmd git \"add\" f) |> |print" "the command body wraps as effect"
            | other -> failtest $"unexpected: {other}"
        }

        test "the comprehension desugars to Seq.map |> Seq.freeze, bypassing EList [D:for-do]" {
            // the session finding: same desugar path as the statement form —
            // list-literal inference (empty-list var, unification) untouched
            expectParse "[for x in xs -> x * 2]" "(|seqFreeze (xs |> (|seqMap (funpat x (* x 2)))))"
            Expect.equal (run "[for x in [1; 2; 3] -> x * 10]" |> forceSeq) [ VInt 10L; VInt 20L; VInt 30L ] ""
        }

        test "the for binder TYPES from its source — a constructor match resolves [D:for-binder]" {
            // the desugar is the pipe shape, so the source infers before
            // the body — surfaced by the wire-unions flagship loop, where
            // `for d in docs do match d with | Case …` refused with
            // \"this one has type 'a1\" while the piped spelling worked
            let e = env |> declare "type FB = FA of int | FBu"

            match
                Weir.Check.typecheck
                    e
                    (parse "for d in [FA 1; FBu] do (match d with | FA n -> print $\"{n}\" | FBu -> print \"b\")")
            with
            | Ok _ -> ()
            | Error terr -> failtest $"the binder must type from the source: {formatError terr}"

            match Weir.Check.typecheck e (parse "[for d in [FA 1; FBu] -> match d with | FA n -> n | FBu -> 0]") with
            | Ok _ -> ()
            | Error terr -> failtest $"the comprehension binder must type too: {formatError terr}"
        }

        test "for and do are reserved words; sudo at EOL does not dangle [D:for-do]" {
            Expect.isError (Weir.Parser.parseLine cmdResolver "let for = 1") "for reserved"
            Expect.isError (Weir.Parser.parseLine cmdResolver "let do = 1") "do reserved"
            // the word-boundary guard: a line ending in 'do' opens a block,
            // a line ending in 'sudo' does not
            Expect.isTrue (Weir.Assemble.dangleOpensBlock "for x in xs do") "do dangles"
            Expect.isFalse (Weir.Assemble.dangleOpensBlock "run sudo") "sudo must not dangle"
        }

        test "for/do multiline body: the layout is the then-body rule, pinned not analogized [D:for-do]" {
            match Weir.Assemble.assemble [ 1, "for x in [1; 2] do"; 2, "    print \"a\""; 3, "    print (show x)" ] with
            | Ok [ ll ] ->
                Expect.stringContains ll.Text "for x in [1; 2] do" "the head survives"
                Expect.stringContains ll.Text "print \"a\"" "body line 1 joins"
            | other -> failtest $"expected one logical line, got {other}"
        }

        test "pfor desugars to Seq.piter — for's shape over another target [D:pfor]" {
            expectParse "pfor x in [1; 2] do print (show x)" "([1; 2] |> (|seqPiter (funpat x (print (show x)))))"
            expectParse "pfor (k, v) in pairs do print k" "(pairs |> (|seqPiter (funpat (k, v) (print k))))"

            match Weir.Parser.parseLine cmdResolver "pfor f in xs do git add $f" with
            | Ok(SExpr e | SCmd e) ->
                Expect.stringContains (show e) "(cmd git \"add\" f) |> |print" "a bare command body arms as for's does"
            | other -> failtest $"unexpected: {other}"

            Expect.equal (Weir.Effects.libraryDesugarTarget "|seqPiter") (Some "Seq.piter") "classified as Seq.piter"
            Expect.equal (checkOk "pfor x in [1; 2] do print (show x)").Ty TUnit "a unit statement"
        }

        test "pfor's body must be unit, blamed at the body, not the desugar [D:pfor]" {
            let terr = checkErr "pfor x in [1; 2] do x + 1"
            Expect.stringContains terr.Message "expected unit" "the piter callback's unit law"
            Expect.equal terr.Span.Start.Col 21 "the caret sits on the body"
        }

        test "pfor's binder types from its source — a constructor match resolves [D:pfor]" {
            let e = env |> declare "type PB = PA of int | PBu"

            match
                Weir.Check.typecheck
                    e
                    (parse "pfor d in [PA 1; PBu] do (match d with | PA n -> print $\"{n}\" | PBu -> print \"b\")")
            with
            | Ok _ -> ()
            | Error terr -> failtest $"the binder must type from the source: {formatError terr}"
        }

        test "pfor collects nothing: -> e, a yield body and [pfor …] teach Seq.pmap, located [D:pfor]" {
            for src, col in
                [
                    "pfor x in xs -> x", 14
                    "pfor x in xs do yield x", 17
                    "let ys = [pfor x in xs -> x]", 24
                ] do
                match Weir.Parser.parseLine cmdResolver src with
                | Ok other -> failtest $"'{src}' must refuse, got {other}"
                | Error msg ->
                    Expect.stringContains msg "Seq.pmap" $"'{src}' points at the collecting spelling"
                    Expect.stringContains msg "effects only" $"'{src}' names the rule"

                    Expect.stringContains
                        (msg.Replace("\r\n", "\n"))
                        $"Col: {col}\n"
                        $"'{src}' is located at the collecting token"

            expectParse "[for x in xs -> x]" "(|seqFreeze (xs |> (|seqMap (funpat x x))))"
        }

        test "pfor is reserved; its head opens a block; a bare range teaches the list [D:pfor]" {
            Expect.isError (Weir.Parser.parseLine cmdResolver "let pfor = 1") "pfor reserved"
            Expect.isTrue (Weir.Assemble.dangleOpensBlock "pfor x in xs do") "the head dangles"

            match Weir.Parser.parseLine cmdResolver "pfor i in 1..3 do print i" with
            | Error msg -> Expect.stringContains msg "pfor i in [a..b] do" "the teaching spells pfor"
            | Ok other -> failtest $"a bare range must refuse, got {other}"

            match
                Weir.Assemble.assemble [
                    1, "pfor x in [1; 2] do"
                    2, "    print \"a\""
                    3, "    print (show x)"
                    4, "print \"z\""
                ]
            with
            | Ok [ ll; next ] ->
                Expect.stringContains ll.Text "print \"a\"" "body line 1 joins"
                Expect.stringContains next.Text "print \"z\"" "the dedented line is its own statement"
            | other -> failtest $"expected two logical lines, got {other}"
        }

        test "pfor inside a plan refuses, naming pfor, before any arm runs [D:pfor] [D:plan-parallel-refusal]" {
            Expect.throwsC
                (fun () ->
                    run (
                        "plan"
                        + Weir.Parser.sibSepStr
                        + "pfor n in [1; 2] do File.write \"/tmp/weir-pfor-never\" [\"x\"]"
                    )
                    |> ignore)
                (fun ex ->
                    Expect.stringContains ex.Message "'pfor' is refused inside 'plan'" "names the spelling written")

            Expect.isFalse (System.IO.File.Exists "/tmp/weir-pfor-never") "no arm ran"
        }

        test "the named record literal constructs a specific type, no field-set inference [D:modules-v1]" {
            // Point { .. } resolves to Point directly (EApp(EVar Point, ERecord)
            // is intercepted since a record type name is never a value)
            Expect.equal
                (run "Point { X = 1; Y = 2 }")
                (VRecord("Point", [ "X", VInt 1L; "Y", VInt 2L ]))
                "named literal builds the record"

            match typecheck env (parse "Point { X = 1; Y = 2 }") with
            | Ok te -> Expect.equal te.Ty (TNamed("Point", [])) "typed as Point"
            | Error terr -> failtest (formatError terr)

            // a partial named literal names the missing field
            match typecheck env (parse "Point { X = 1 }") with
            | Error terr -> Expect.stringContains terr.Message "missing" "partial named literal names the gap"
            | Ok _ -> failtest "a partial named literal should reject"
        }

        test "a constructor applied to a bare record still applies (zero movement) [D:modules-v1]" {
            // Some { .. } is not a named record — Some is a ctor, not a type
            match typecheck env (parse "Some { X = 1; Y = 2 }") with
            | Ok te -> Expect.equal te.Ty (TNamed("Option", [ TNamed("Point", []) ])) "Some applied to a Point"
            | Error terr -> failtest (formatError terr)
        }

        test "yaml value-domain answers, pinned: Show renders, Eq refuses by the no-seq rule [D:yaml-v1]" {
            // the bless-note questions, answered by the existing machinery
            Expect.equal (run "show (YMap [(\"a\", YInt 1)])") (VStr "YMap ([(\"a\", YInt 1)])") "Show recurses"

            Expect.stringContains
                (checkErr "YStr \"a\" == YStr \"a\"").Message
                "cannot be compared"
                "Eq refuses via no-seq"
        }

        test "to yaml: reverse-Norway quoting — no/007/1e5/mid-#/colon quote; multiline is a BLOCK [D:yaml-v1]" {
            // the f case moved deliberately with [D:block-scalars]: a
            // multiline string is a block scalar, not quoted — the
            // quoting law governs single-line strings only
            Expect.equal
                (run
                    "[(\"a\", \"no\"); (\"b\", \"007\"); (\"c\", \"1e5\"); (\"d\", \"x # y\"); (\"e\", \"k: v\"); (\"f\", \"l1\\nl2\"); (\"g\", \"plain\")] |> to yaml"
                 |> forceSeq)
                [
                    VStr "a: \"no\""
                    VStr "b: \"007\""
                    VStr "c: \"1e5\""
                    VStr "d: \"x # y\""
                    VStr "e: \"k: v\""
                    VStr "f: |-"
                    VStr "  l1"
                    VStr "  l2"
                    VStr "g: plain"
                ]
                "a YAML reader cannot mis-type any of these"
        }

        test "to yaml: the float specials quote — a reader would type .inf/.nan as floats [D:yaml-v1]" {
            Expect.equal
                (run "[(\"a\", \".inf\"); (\"b\", \".nan\"); (\"c\", \"-.INF\")] |> to yaml"
                 |> forceSeq)
                [ VStr "a: \".inf\""; VStr "b: \".nan\""; VStr "c: \"-.INF\"" ]
                "the .inf/.nan family joins the reverse-Norway set"
        }

        test "to yaml: every 1.1/1.2 resolver form quotes — +.5, y/n, <<, = [D:yaml-plain-resolvers]" {
            Expect.equal
                (run
                    "[(\"a\", \"+.5\"); (\"b\", \"+.5e3\"); (\"c\", \"-.5\"); (\"d\", \"._5\"); (\"e\", \"y\"); (\"f\", \"N\"); (\"g\", \"<<\"); (\"h\", \"=\"); (\"i\", \".\")] |> to yaml"
                 |> forceSeq)
                [
                    VStr "a: \"+.5\""
                    VStr "b: \"+.5e3\""
                    VStr "c: \"-.5\""
                    VStr "d: \"._5\""
                    VStr "e: \"y\""
                    VStr "f: \"N\""
                    VStr "g: \"<<\""
                    VStr "h: \"=\""
                    VStr "i: ."
                ]
                "no reader types these as a float, bool, merge or value key"

            Expect.equal
                (run "[(\"y\", 1); (\"<<\", 2)] |> to yaml" |> forceSeq)
                [ VStr "\"y\": 1"; VStr "\"<<\": 2" ]
                "keys follow the same law"

            Expect.equal
                (run "show (YMap [(\"v\", YStr \"+.5\")] |> to yaml |> from yaml {| v: Yaml |})")
                (VStr "{ v = YStr \"+.5\" }")
                "weir's own reader reads the string back"
        }

        test "to yaml: a None pair value writes null, the key kept [D:yaml-pair-none]" {
            Expect.equal
                (run "[(\"a\", None); (\"b\", Some 1)] |> to yaml" |> forceSeq)
                [ VStr "a: null"; VStr "b: 1" ]
                "a pair's None is its data, as to json writes it"

            Expect.equal
                (run
                    "show ({| m = [(\"a\", None); (\"b\", Some 1)] |} |> to yaml |> from yaml {| m: seq<string * Option<int>> |})")
                (VStr "{ m = [(\"a\", None); (\"b\", Some 1)] }")
                "the key reads back"
        }

        test "an integer past int64 in a Yaml node keeps its digits on write [D:yaml-node-bigint]" {
            Expect.equal
                (run
                    "[\"{\\\"id\\\": 18446744073709551615, \\\"n\\\": -12345678901234567890123}\"] |> from json {| id: Yaml; n: Yaml |} |> to json |> Str.join \"\"")
                (VStr "{\"id\":18446744073709551615,\"n\":-12345678901234567890123}")
                "json -> json keeps the digits"

            Expect.equal
                (run "[\"id: 18446744073709551615\"] |> from yaml {| id: Yaml |} |> to yaml |> Str.join \"\"")
                (VStr "id: 18446744073709551615")
                "yaml -> yaml keeps the digits"

            Expect.equal
                (run "show ([\"id: 18446744073709551615\"] |> from yaml {| id: Yaml |})")
                (VStr "{ id = YFloat (1.8446744073709552e+19) }")
                "the node still matches as its nearest float"

            Expect.equal
                (run "YMap [(\"f\", YFloat 1e19)] |> to yaml |> Str.join \"\"")
                (VStr "f: 1.0e+19")
                "a node built in weir writes its float (a 1.1 float: dot, signed exponent)"
        }

        test "from yaml: a lone-surrogate escape refuses, as from json does [D:encoding-law]" {
            Expect.stringContains
                (Expect.wantError (Weir.Yaml.scalarCore "\"\\ud800\"") "a lone high surrogate")
                "lone surrogate"
                "names the cause"

            Expect.isError (Weir.Yaml.scalarCore "\"\\udc00x\"") "a lone low surrogate"
            Expect.isError (Weir.Yaml.scalarCore "\"\\ud800\\u0041\"") "a high surrogate before a non-low"

            Expect.equal (Weir.Yaml.scalarCore "\"\\ud83d\\ude00\"") (Ok(Some("\U0001F600", true))) "a pair is text"
        }

        test "to yaml: CR/NEL/LS/PS and controls ESCAPE in quoted scalars — raw they are line breaks [D:yaml-v1]" {
            Expect.equal
                (run "[(\"a\", Str.fromBase64 \"eA15\"); (\"b\", Str.fromBase64 \"YcKFYg==\")] |> to yaml"
                 |> forceSeq)
                [ VStr "a: \"x\\ry\""; VStr "b: \"a\\Nb\"" ]
                "a YAML reader must see the escape, not the raw byte"
        }

        test
            "to yaml: a leading-whitespace content line FALLS BACK to quotes — block form there needs the rejected indicator [D:block-scalars]" {
            Expect.equal
                (run "[(\"a\", \" lead\\nrest\")] |> to yaml" |> forceSeq)
                [ VStr "a: \" lead\\nrest\"" ]
                "weir must not write block YAML weir refuses to read"
        }

        test "from yaml: a whitespace-bearing block line beyond the indent is CONTENT, not dropped [D:block-scalars]" {
            // "a\n " under |- : the 3-space line carries one space past
            // the 2-space indent (PyYAML agrees byte-for-byte)
            let env2 = env |> declare "type KV = { k: string }"
            let prog = "([\"k: |-\"; \"  a\"; \"   \"] |> from yaml KV).k"

            match Weir.Parser.parseStmt prog with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck env2 e with
                | Ok te ->
                    Expect.equal
                        (Weir.Eval.eval valueEnv te)
                        (VStr "a\n ")
                        "the read side must not lose bytes the write side kept"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "to yaml: YMap preserves order; record fields render alphabetically [D:yaml-v1]" {
            Expect.equal
                (run "YMap [(\"zeta\", YStr \"z\"); (\"alpha\", YInt 1)] |> to yaml" |> forceSeq)
                [ VStr "zeta: z"; VStr "alpha: 1" ]
                "YMap is the user-controlled order escape"
        }

        test "yaml roundtrip: a nested tree with Option and pair-seq labels survives [D:yaml-v1]" {
            let env2 =
                env
                |> declare "type YMeta = { name: string; labels: seq<string * string> }"
                |> declare "type YSpec = { replicas: int; paused: Option<bool> }"
                |> declare "type YDep = { kind: string; metadata: YMeta; spec: YSpec }"

            let prog =
                "let d = YDep { kind = \"D\"; metadata = YMeta { name = \"app\"; labels = [(\"app\", \"web\")] }; spec = YSpec { replicas = 3; paused = None } } in "
                + "(d |> to yaml |> from yaml YDep) == d"

            // Eq on records of scalars+pair-seqs? pair-seq is a seq — Eq
            // refuses; compare fields instead
            let prog2 =
                "let d = YDep { kind = \"D\"; metadata = YMeta { name = \"app\"; labels = [(\"app\", \"web\")] }; spec = YSpec { replicas = 3; paused = None } } in "
                + "let back = d |> to yaml |> from yaml YDep in "
                + "show (back.metadata.name, back.spec.replicas, back.spec.paused)"

            match Weir.Parser.parseStmt prog2 with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck env2 e with
                | Ok te -> Expect.equal (Weir.Eval.eval valueEnv te) (VStr "(\"app\", 3, None)") "roundtrip holds"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "from yaml: plain null/Null/NULL/~ are null, quoted forms are strings (B8) [D:yaml-core-null]" {
            let env2 =
                env
                |> declare
                    "type YN = { a: Option<string>; b: Option<int>; c: Option<string>; d: string; e: Option<string> }"
                |> declare "type YO = { ys: seq<Option<int>>; zs: Option<seq<string>>; s: seq<string> }"
                |> declare "type YR = { name: string }"

            let evalStr prog =
                match Weir.Parser.parseStmt prog with
                | Ok(SExpr e) ->
                    match Weir.Check.typecheck env2 e with
                    | Ok te -> Weir.Eval.eval valueEnv te
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            Expect.equal
                (evalStr
                    "show ([\"a: null\"; \"b: ~\"; \"c: \\\"null\\\"\"; \"d: 'NULL'\"; \"e: Null\"] |> from yaml YN)")
                (VStr "{ a = None; b = None; c = Some \"null\"; d = \"NULL\"; e = None }")
                "core-schema nulls read None; quoted stay strings"

            for v in [ "Some 1; None"; "None"; "" ] do
                for s in [ "\"null\"; \"~\"; \"Null\"; \"NULL\""; "" ] do
                    let o = $"{{ ys = [{v}]; zs = None; s = [{s}] }}"

                    Expect.equal
                        (evalStr $"let o = YO {o} in show (o |> to yaml |> from yaml YO) == show o")
                        (VBool true)
                        $"roundtrip {o}"

            Expect.equal
                (evalStr "show ([\"x: NULL\"; \"y: Nullx\"; \"z: ~/a\"] |> Yaml.parse)")
                (VStr "YMap ([(\"x\", YNull); (\"y\", YStr \"Nullx\"); (\"z\", YStr \"~/a\")])")
                "only the exact words are null"

            let ex =
                Expect.throwsC (fun () -> evalStr "[\"name: null\"] |> from yaml YR" |> ignore) id

            Expect.stringContains ex.Message "field 'name' is null" "a required field receiving null errors"
        }

        test "from yaml: a --- stream TEACHES — one document only [D:yaml-seq]" {
            let env2 = env |> declare "type YD = { kind: string }"
            let src = "[\"kind: A\"; \"---\"; \"kind: B\"] |> from yaml YD"

            match Weir.Parser.parseStmt src with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck env2 e with
                | Ok te ->
                    let ex = Expect.throwsC (fun () -> Weir.Eval.eval valueEnv te |> ignore) id

                    Expect.stringContains
                        ex.Message
                        "from yaml: reads one document; this input has 2 documents — read a stream with 'from yaml stream T'"
                        "the retirement teaches, names the count and the route"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "from xml: the csproj shape reads — attributes, optional text, repeated children [D:from-xml]" {
            let doc =
                VSeq [
                    VStr "<Project Sdk=\"Microsoft.NET.Sdk\">"
                    VStr "  <PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>"
                    VStr "  <PropertyGroup><IsPublishable>true</IsPublishable></PropertyGroup>"
                    VStr "  <ProjectReference Include=\"../Core/Core.csproj\" />"
                    VStr "  <ProjectReference Include=\"../Util/Util.csproj\" />"
                    VStr "</Project>"
                ]

            Expect.equal
                (runWith [ "src", doc ] "src |> from xml XProj")
                (VRecord(
                    "XProj",
                    [
                        "groups",
                        VSeq [
                            VRecord(
                                "XPg",
                                [
                                    "IsPackable", VUnion("Some", Some(VStr "false"))
                                    "IsPublishable", VUnion("None", None)
                                ]
                            )
                            VRecord(
                                "XPg",
                                [
                                    "IsPackable", VUnion("None", None)
                                    "IsPublishable", VUnion("Some", Some(VStr "true"))
                                ]
                            )
                        ]
                        "refs",
                        VSeq [
                            VRecord("XRef", [ "Include", VStr "../Core/Core.csproj" ])
                            VRecord("XRef", [ "Include", VStr "../Util/Util.csproj" ])
                        ]
                    ]
                ))
                "field names match child elements, [<Attr>] reads an attribute, [<Elem>] repeats"
        }

        test "from xml: a default xmlns is stripped — fields match by LOCAL name [D:from-xml]" {
            let env2 = env |> declare "type XNs = { AssemblyName: string }"

            let env3 =
                env2 |> declare "type XRoot = { [<Elem \"PropertyGroup\">] groups: seq<XNs> }"

            let doc =
                VSeq [
                    VStr "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
                    VStr "  <PropertyGroup><AssemblyName>Legacy</AssemblyName></PropertyGroup>"
                    VStr "</Project>"
                ]

            match Weir.Parser.parseStmt "src |> from xml XRoot" with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck env3 e with
                | Ok te ->
                    Expect.equal
                        (Weir.Eval.eval (Map.add "src" doc valueEnv) te)
                        (VRecord("XRoot", [ "groups", VSeq [ VRecord("XNs", [ "AssemblyName", VStr "Legacy" ]) ] ]))
                        "namespaces are ignored so field names stay plain"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "from xml: a missing required attribute fails, naming the element and path [D:from-xml]" {
            let doc = VSeq [ VStr "<Project><ProjectReference /></Project>" ]

            let ex =
                Expect.throwsC (fun () -> runWith [ "src", doc ] "src |> from xml XProj" |> ignore) id

            Expect.stringContains ex.Message "missing attribute 'Include'" "the runtime names the absent attribute"
            Expect.stringContains ex.Message "field 'refs[0].Include'" "the first element is refs[0]"

            let second =
                VSeq [
                    VStr "<Project><ProjectReference Include=\"a\" /><ProjectReference /></Project>"
                ]

            let ex2 =
                Expect.throwsC (fun () -> runWith [ "src", second ] "src |> from xml XProj" |> ignore) id

            Expect.stringContains
                ex2.Message
                "field 'refs[1].Include'"
                "paths index from zero, as JSON's do (B30) [D:paths-zero-based]"
        }

        test "from xml: invalid input and empty input each TEACH [D:from-xml]" {
            let bad =
                Expect.throwsC
                    (fun () -> runWith [ "src", VSeq [ VStr "not xml <" ] ] "src |> from xml XProj" |> ignore)
                    id

            Expect.stringContains bad.Message "not valid XML" "never System.Xml's words"

            let empty =
                Expect.throwsC (fun () -> runWith [ "src", VSeq [ VStr "" ] ] "src |> from xml XProj" |> ignore) id

            Expect.stringContains empty.Message "empty input — expected one XML document" ""
        }

        test
            "from xml: bytes decode per the BOM or declaration; text refuses a non-UTF-8 one; unknown refuses [D:xml-encoding]" {
            let xenv = env |> declare "type XT = { Title: string }"

            let b64 (enc: System.Text.Encoding) (decl: string) (title: string) =
                System.Convert.ToBase64String(
                    Array.append
                        (enc.GetPreamble())
                        (enc.GetBytes($"<?xml version=\"1.0\"{decl}?>\n<Root><Title>{title}</Title></Root>"))
                )

            let readBytes (b: string) =
                runUnder xenv $"show (Bytes.fromBase64 \"{b}\" |> from xml XT)"

            Expect.equal
                (readBytes (b64 System.Text.Encoding.Latin1 " encoding=\"ISO-8859-1\"" "café"))
                (VStr "{ Title = \"café\" }")
                "ISO-8859-1"


            Expect.equal
                (readBytes (b64 (System.Text.UnicodeEncoding(false, true)) " encoding=\"UTF-16\"" "café"))
                (VStr "{ Title = \"café\" }")
                "UTF-16 with its BOM"

            Expect.equal
                (readBytes (b64 (System.Text.UTF8Encoding(true)) "" "café"))
                (VStr "{ Title = \"café\" }")
                "a UTF-8 BOM, no declaration"

            let msg (src: string) =
                (Expect.throwsC (fun () -> runUnder xenv src |> ignore) id).Message

            // non-ASCII bytes: pure ASCII reads under any label [D:xml-labels]
            let klingon = b64 System.Text.Encoding.Latin1 " encoding=\"x-klingon\"" "café"
            let cp1252 = b64 System.Text.Encoding.Latin1 " encoding=\"windows-1252\"" "café"

            Expect.stringContains
                (msg $"Bytes.fromBase64 \"{cp1252}\" |> from xml XT")
                "declares encoding 'windows-1252', which weir cannot decode"
                "a code page outside CoreLib refuses [D:xml-encoding-builtin]"

            Expect.stringContains
                (msg $"Bytes.fromBase64 \"{klingon}\" |> from xml XT")
                "declares encoding 'x-klingon', which weir cannot decode"
                "an unknown encoding refuses (bytes)"

            Expect.stringContains
                (msg "[\"<?xml version='1.0' encoding='x-klingon'?><Root><Title>café</Title></Root>\"] |> from xml XT")
                "which weir cannot decode"
                "an unknown encoding refuses (text)"

            Expect.stringContains
                (msg "[\"<?xml version='1.0' encoding='ISO-8859-1'?><Root><Title>café</Title></Root>\"] |> from xml XT")
                "File.readBytes path |> from xml XT"
                "non-ASCII text under a non-UTF-8 declaration teaches the bytes read"

            Expect.equal
                (runUnder
                    xenv
                    "show ([\"<?xml version='1.0' encoding='ISO-8859-1'?><Root><Title>ab</Title></Root>\"] |> from xml XT)")
                (VStr "{ Title = \"ab\" }")
                "ASCII text reads under any ASCII-compatible declaration"

            Expect.stringContains
                (msg "Bytes.fromBase64 \"PFJvb3Q+PFRpdGxlPmNhZulgPC9UaXRsZT48L1Jvb3Q+\" |> from xml XT")
                "not valid utf-8"
                "no declaration: strict UTF-8, never U+FFFD"
        }

        test "from xml: a DTD refuses — no entity expands or resolves [D:xml-no-dtd]" {
            let refuse (doc: string list) =
                (Expect.throwsC
                    (fun () -> runWith [ "src", VSeq(doc |> List.map VStr) ] "src |> from xml XProj" |> ignore)
                    id)
                    .Message

            let ext =
                refuse [
                    "<!DOCTYPE Project [<!ENTITY e SYSTEM \"file:///etc/hostname\">]>"
                    "<Project>&e;</Project>"
                ]

            Expect.stringContains
                ext
                "a DTD (<!DOCTYPE ...>) is outside the XML subset"
                "an external entity is never silently dropped"

            let inner =
                refuse [ "<!DOCTYPE Project [<!ENTITY e \"X\">]>"; "<Project>&e;</Project>" ]

            Expect.stringContains inner "outside the XML subset" "an internal entity never expands"

            Expect.stringContains (refuse [ "<!DOCTYPE Project>"; "<Project/>" ]) "remove it" "a bare DOCTYPE too"

            Expect.equal
                (runWith
                    [
                        "src", VSeq [ VStr "<?xml version=\"1.0\"?>"; VStr "<Project>&amp;&#65;</Project>" ]
                    ]
                    "src |> from xml XProj")
                (VRecord("XProj", [ "groups", VSeq []; "refs", VSeq [] ]))
                "the predefined entities and character references still read"
        }

        test "from xml: a repeated element into a scalar or record field refuses, teaching the seq (triage 16b)" {
            let env2 =
                env
                |> declare "type XOne = { AssemblyName: string }"
                |> declare "type XOpt = { AssemblyName: Option<string> }"
                |> declare "type XRec = { PropertyGroup: XOne }"

            let refuse ty (doc: string) =
                match Weir.Parser.parseStmt $"src |> from xml {ty}" with
                | Ok(SExpr e) ->
                    match Weir.Check.typecheck env2 e with
                    | Ok te ->
                        (Expect.throwsC
                            (fun () -> Weir.Eval.eval (Map.add "src" (VSeq [ VStr doc ]) valueEnv) te |> ignore)
                            id)
                            .Message
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            let twice = "<P><AssemblyName>a</AssemblyName><AssemblyName>b</AssemblyName></P>"

            Expect.stringContains
                (refuse "XOne" twice)
                "element <AssemblyName> repeats in <P> for field 'AssemblyName' — declare seq<string>"
                "never silently the first"

            Expect.stringContains (refuse "XOpt" twice) "declare seq<string>" "Option<string> too"

            Expect.stringContains
                (refuse "XRec" "<P><PropertyGroup/><PropertyGroup/></P>")
                "declare [<Elem \"PropertyGroup\">] seq<XOne>"
                "a record field names the [<Elem>] the seq needs"
        }

        test "from xml: a text field over mixed content refuses, naming the element and field [D:from-xml-mixed]" {
            let env2 =
                env
                |> declare "type XOne = { AssemblyName: string }"
                |> declare "type XOpt = { AssemblyName: Option<string> }"
                |> declare "type XMany = { [<Elem \"AssemblyName\">] names: seq<string> }"

            let read ty (doc: string) =
                match Weir.Parser.parseStmt $"src |> from xml {ty}" with
                | Ok(SExpr e) ->
                    match Weir.Check.typecheck env2 e with
                    | Ok te -> fun () -> Weir.Eval.eval (Map.add "src" (VSeq [ VStr doc ]) valueEnv) te
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            let refuse ty doc =
                (Expect.throwsC (fun () -> read ty doc () |> ignore) id).Message

            let mixed = "<P><AssemblyName>a<b/>c</AssemblyName></P>"

            Expect.stringContains
                (refuse "XOne" mixed)
                "element <AssemblyName> for field 'AssemblyName' holds a child element <b>"
                "never the concatenated 'ac'"

            Expect.stringContains (refuse "XOpt" mixed) "holds a child element <b>" "Option<string> too"

            Expect.stringContains
                (refuse "XMany" "<P><AssemblyName>x</AssemblyName><AssemblyName>a<b/></AssemblyName></P>")
                "for field 'names[1]'"
                "a seq<string> element names its index"

            // text-only content still reads: entities and CDATA are text
            Expect.equal
                (read "XOne" "<P><AssemblyName>a&amp;<![CDATA[<b/>]]></AssemblyName></P>" ())
                (VRecord("XOne", [ "AssemblyName", VStr "a&<b/>" ]))
                "CDATA is text, not an element"
        }

        test "from xml: check-time laws — text-only leaves, [<Attr>]/[<Elem>] fit, no wrap, read-only [D:from-xml]" {
            Expect.stringContains
                (checkErr "src |> from xml XInt").Message
                "XML values are text"
                "numbers must be declared string"

            Expect.stringContains
                (checkErr "src |> from xml XBadAttr").Message
                "[<Attr>] reads an attribute's text — the field must be string or Option<string>"
                ""

            Expect.stringContains
                (checkErr "src |> from xml XBadElem").Message
                "[<Elem>] names a repeated child element — the field must be a seq"
                ""

            Expect.stringContains
                (checkErr "src |> from xml seq<XProj>").Message
                "reads one document's root element — write from xml XProj"
                "no seq/stream/Map wrap: XML has a single root"

            Expect.stringContains (checkErr "src |> from xml").Message "'from xml' needs a record name" ""

            Expect.stringContains
                (checkErr "[{ Include = \"x\" }] |> to xml").Message
                "'to xml' does not exist — XML is read-only ('from xml T')"
                ""
        }

        test "the yaml district assembles: sentinel-glued verbatim lines [D:yaml-district]" {
            match
                Weir.Assemble.assemble [
                    1, "let d = yaml"
                    2, "    kind: Pod"
                    3, "    metadata:"
                    4, "        name: app"
                ]
            with
            | Ok [ ll ] ->
                Expect.stringContains ll.Text "let d = yaml" "the marker word survives"
                Expect.stringContains ll.Text "kind: Pod" "block lines ride verbatim"

                Expect.stringContains
                    ll.Text
                    (Weir.Parser.sibSepStr + "    name: app")
                    "relative indent is preserved behind the sentinel"
            | other -> failtest $"expected one logical line, got {other}"
        }

        test "a command head glued to the sentinel never parses as a command [D:yaml-district]" {
            // the machine boundary: only the assembler's yaml wrap glues them
            match Weir.Assemble.assemble [ 1, "let d = yaml"; 2, "    x: 1" ] with
            | Ok [ ll ] ->
                match Weir.Parser.parseLine cmdResolver ll.Text with
                | Ok(SLet("d", { Kind = EYaml _ })) -> ()
                | other -> failtest $"expected EYaml under the let, got {other}"
            | other -> failtest $"unexpected assembly: {other}"
        }

        test "a command line ending in `yaml` is argv, not an armed district [D:yaml-district]" {
            // the reported bug: `-o yaml`/`--format yaml` are everyday
            // argv; the district arm must not fire for them, so the
            // line assembles clean (no armed-district demand for a
            // block below) and check == run at the assembly boundary.
            // Assert the assembly verdict (armed-error vs clean), never
            // a PATH completion — these are command-shaped lines.
            let assemblesClean line =
                match Weir.Assemble.assemble [ 1, line ] with
                | Ok [ ll ] -> Expect.equal ll.Text line $"'{line}' assembles as one plain command line, unarmed"
                | Ok other -> failtest $"'{line}': expected one logical line, got {other}"
                | Error e -> failtest $"'{line}' must not arm a district: {e}"

            assemblesClean "echo -o yaml"
            assemblesClean "kubectl get po -o yaml"
            assemblesClean "docker x --format yaml"
            assemblesClean "echo a b yaml"

            // and the district still arms for the real forms: a bare
            // `= yaml` with no block below is the armed-district error
            // (proves the arm fires, the block demand included)
            match Weir.Assemble.assemble [ 1, "let d = yaml" ] with
            | Error e -> Expect.stringContains e "indented block" "a bare `= yaml` arms and demands its block"
            | Ok other -> failtest $"expected the armed-district error, got {other}"
        }

        test "Yaml.parse: the typeless read — structure whole, scalars self-type [D:yaml-nodes]" {
            expectValue
                "[\"replicas: 3\"; \"name: web\"; \"live: true\"] |> Yaml.parse"
                (VUnion(
                    "YMap",
                    Some(
                        VSeq [
                            VTuple [ VStr "replicas"; VUnion("YInt", Some(VInt 3L)) ]
                            VTuple [ VStr "name"; VUnion("YStr", Some(VStr "web")) ]
                            VTuple [ VStr "live"; VUnion("YBool", Some(VBool true)) ]
                        ]
                    )
                ))
        }

        test "Yaml.parse: empty and multi-document inputs TEACH [D:yaml-nodes]" {
            let empty = Expect.throwsC (fun () -> run "[\"\"] |> Yaml.parse" |> ignore) id

            Expect.stringContains empty.Message "expected one YAML document" ""

            let multi =
                Expect.throwsC (fun () -> run "[\"a: 1\"; \"---\"; \"b: 2\"] |> Yaml.parse" |> ignore) id

            Expect.stringContains multi.Message "reads one document; this input has 2" "count and route"
        }

        test "Yaml.parse: a deep ladder hits the nesting cap; legal depth still reads [D:yaml-depth]" {
            // an `a:` ladder one space deeper each line — the cubic-hang
            // shape. Past the cap it is a located diagnostic, never a hang.
            let ladder n =
                [ for i in 0 .. n - 1 -> System.String(' ', i) + "a:" ]
                @ [ System.String(' ', n) + "v: 1" ]

            let parse lines' =
                Weir.Yaml.parseDocs (lines' |> List.mapi (fun i l -> i + 1, l))

            match parse (ladder 600) with
            | Error msg ->
                Expect.stringContains msg "yaml nesting is too deep (limit 500)" "the cap teaches"
                Expect.stringContains msg "line " "and locates"
            | Ok _ -> failtest "a 600-deep ladder must hit the cap"

            // 250 deep is well inside the cap — real manifests read
            match parse (ladder 250) with
            | Ok [ _ ] -> ()
            | other -> failtest $"a 250-deep ladder must parse: {other}"
        }

        test "to yaml: a value deeper than the cap teaches instead of crashing [D:yaml-depth]" {
            // a Seq.fold-nested YSeq is the StackOverflow shape (exit 134)
            // — past the emitter cap it is a clean boundary error
            let ex =
                Expect.throwsC
                    (fun () ->
                        run "[1..2000] |> Seq.fold (fun acc _ -> YSeq [acc]) (YInt 1) |> to yaml"
                        |> ignore)
                    id

            Expect.stringContains ex.Message "nests deeper than 100" "the cap teaches"

            // 50 deep renders fine — the cap sits above real trees
            let ok =
                run "[1..50] |> Seq.fold (fun acc _ -> YSeq [acc]) (YInt 1) |> to yaml"
                |> forceSeq

            Expect.isNonEmpty ok "a 50-deep value renders"
        }

        test "yaml patch: types as YamlPatch; tombstones scoped; schema= refuses [D:yaml-nodes]" {
            let checkOf src =
                match Weir.Parser.parseLine realResolver src with
                | Ok(SLet(_, e)) -> typecheck env e
                | other -> failtest $"unexpected: {other}"

            match checkOf (asm [ "let p = yaml patch by=name"; "    images:"; "        - name: app" ]) with
            | Ok te -> Expect.equal te.Ty (TNamed("YamlPatch", [])) "a patch district types as YamlPatch"
            | Error terr -> failtest (formatError terr)

            match checkOf (asm [ "let d = yaml"; "    gone: $-" ]) with
            | Error terr ->
                Expect.stringContains
                    (formatError terr)
                    "a tombstone ($-) means something only inside a `yaml patch` district"
                    ""
            | Ok _ -> failtest "a plain district must refuse a tombstone"

            match checkOf (asm [ "let p = yaml patch schema=k8s"; "    a: 1" ]) with
            | Error terr ->
                Expect.stringContains (formatError terr) "a patch is partial — schema= validates whole documents" ""
            | Ok _ -> failtest "patch x schema= must refuse"
        }

        test "Self.prompt is string -> string, qualified and effectful [D:prompt]" {
            Expect.equal (formatTy (checkOk "Self.prompt").Ty) "string -> string" "the interactive read's type"
        }

        test "the marker law learns the patch modifiers [D:yaml-nodes]" {
            Expect.isTrue (Weir.Parser.isYamlMarkerPiece "let p = yaml patch") "patch arms"
            Expect.isTrue (Weir.Parser.isYamlMarkerPiece "let p = yaml patch by=name") "patch by= arms"
            Expect.isTrue (Weir.Parser.isYamlMarkerPiece "let d = yaml schema=k8s") "schema= still arms"
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "run patch") "a command ending in patch stays a command"
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "xs |> to yaml patch") "to yaml never arms"
        }

        test "the marker arms TOKEN-PRECISELY: bare `yaml` or a `= yaml` RHS, never argv [D:yaml-district]" {
            // arms: the two legitimate shapes
            Expect.isTrue (Weir.Parser.isYamlMarkerPiece "yaml") "bare yaml arms (next-line form)"
            Expect.isTrue (Weir.Parser.isYamlMarkerPiece "let d = yaml") "a let RHS arms"
            Expect.isTrue (Weir.Parser.isYamlMarkerPiece "d = yaml") "an assignment RHS arms"
            // does not arm: `yaml` preceded by argv — the everyday
            // `-o yaml`/`--format yaml` ops lines, a trailing bare word
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "echo -o yaml") "-o yaml is argv, not a marker"
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "kubectl get po -o yaml") "kubectl -o yaml is a command"
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "docker x --format yaml") "--format yaml is argv"

            Expect.isFalse
                (Weir.Parser.isYamlMarkerPiece "echo a b yaml")
                "a trailing bare word after argv is a command"
            // the to/from adapters are subsumed by the `= yaml` rule
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "foo | to yaml") "to yaml never arms"
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "data |> from yaml T") "from yaml never arms"
            // `>= yaml`/`== yaml`: the token before yaml is `>=`/`==`, not `=`
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "x >= yaml") ">= yaml does not arm"
            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "x == yaml") "== yaml does not arm"
        }

        test "any blank run before the marker arms; a trailing blank teaches at check and run [D:yaml-marker-blanks]" {
            for piece in [ "let y =  yaml"; "let y =\tyaml"; "let y= yaml"; "let y = yaml patch" ] do
                Expect.isTrue (Weir.Parser.isYamlMarkerPiece piece) piece

            Expect.isFalse (Weir.Parser.isYamlMarkerPiece "make FOO= yaml") "a glued name= arms only on a let"

            for head in [ "let y =  yaml"; "let y= yaml"; "let y =\tyaml" ] do
                checkEqRun [ head; "    a: 1"; "print (show y)" ]

            let teach =
                "a yaml block marker must end its line — nothing may follow it, not even trailing spaces or tabs; the block is the indented lines below"

            for head in [ "let y = yaml "; "let y = yaml patch\t" ] do
                let lines = [ head; "    a: 1"; "print (show y)" ]

                match errorsIn "ym.weir" lines with
                | d :: _ ->
                    Expect.equal (d.Message.Replace("\r\n", " ").Replace("\n", " ")) teach $"check: {head}"
                    Expect.equal (d.Line, d.Col) (1, 9) "on the marker"
                | [] -> failtest $"must refuse: {head}"

                match Weir.Parser.parseLine realResolver (asm (List.take 2 lines)) with
                | Error m -> Expect.stringContains (m.Replace("\r\n", " ").Replace("\n", " ")) teach $"run: {head}"
                | Ok _ -> failtest $"run must refuse: {head}"
        }

        test "YamlPatch is a built-in type name — undeclarable [D:yaml-nodes]" {
            match Weir.Check.checkDecl env (parseDecl "type YamlPatch = { x: int }") with
            | Error terr -> Expect.stringContains terr.Message "'YamlPatch' is a built-in type" ""
            | Ok _ -> failtest "declaring YamlPatch must refuse"
        }

        test
            "check parses patch districts exactly as run — the assume-resolver never claims the marker head [D:yaml-nodes]" {
            // check's assume-resolver reads any command-shaped head
            // as external, `yaml` included — commandSegment must
            // refuse the ` patch [by=] [schema=]` face glued to the
            // sentinel so the district arm parses, as run does
            let clean = checkEqRun

            // a $name splice in scalar value position
            clean [
                "let n = \"api\""
                "let _p = yaml patch by=name"
                "    images:"
                "        - name: $n"
                "print \"ok\""
            ]
            // a multi-line mapping as a seq item
            clean [
                "let _p = yaml patch by=name"
                "    images:"
                "        - name: api"
                "          newTag: v2"
                "print \"ok\""
            ]
            // bare `patch`, single scalar entry
            clean [ "let _p = yaml patch"; "    replicas: 3"; "print \"ok\"" ]

            // the patch x schema= refusal still fires — the guard routes
            // to the district arm, never past its own checks
            let diags =
                diagsIn "pin.weir" [ "let p = yaml patch by=name schema=k8s"; "    a: 1"; "print \"ok\"" ]

            Expect.exists
                diags
                (fun d -> d.Message.Contains "a patch is partial")
                "patch x schema= still refuses via the district arm"

            // the other direction: an unarmed line ending in the modifier
            // words stays a command (no sentinel glue, no district)
            let cmdDiags =
                diagsIn "pin.weir" [ "let x = echo patch by=name"; "x |> Seq.iter print" ]

            Expect.isEmpty cmdDiags "a command with 'patch by=name' argv stays a command"
        }

        test "budget stop-at-first: one exhaustion bounds the whole file [D:budget-stop-first]" {
            // the per-statement inference budget is a whole-file DoS when
            // the multi-error fold multiplies it across independent burning
            // statements. A budget diagnostic is a stop-and-fix: the fold
            // reports the first one and checks no further statement.
            let burnBlock (b: int) = [
                $"let g{b}_0 x = (x, x)"
                $"let g{b}_1 x = g{b}_0 (g{b}_0 x)"
                $"let g{b}_2 x = g{b}_1 (g{b}_1 x)"
                $"let g{b}_3 x = g{b}_2 (g{b}_2 x)"
                $"let g{b}_4 x = g{b}_3 (g{b}_3 x)"
                $"let g{b}_5 x = g{b}_4 (g{b}_4 x)"
                $"let v{b} = g{b}_5 1"
            ]

            // eight independent budget-burning blocks
            let lines =
                [
                    for b in 0..7 do
                        yield! burnBlock b
                ]
                @ [ "print \"done\"" ]

            let diags = diagsIn "burn.weir" lines

            let budgetDiags =
                diags |> List.filter (fun d -> Weir.Check.isBudgetMessage d.Message)

            // exactly one budget diagnostic — the fold stopped at the first
            Expect.equal
                (List.length budgetDiags)
                1
                "one burn bounds the file: exactly one budget diagnostic, not one per block"

            // and it is the only error reported (stop-and-fix)
            let errors = diags |> List.filter (fun d -> d.Severity = "error")
            Expect.equal (List.length errors) 1 "the budget stop suppresses downstream statements"
        }

        test "budget stop does NOT swallow ordinary type errors [D:budget-stop-first]" {
            // three independent plain type errors must all report — the
            // stop applies only to budget exhaustion, never ordinary errors
            let diags =
                diagsIn "three.weir" [
                    "let a = 1 + \"x\""
                    "let b = 2 + \"y\""
                    "let c = 3 + \"z\""
                    "print \"done\""
                ]

            let errors = diags |> List.filter (fun d -> d.Severity = "error")
            Expect.equal (List.length errors) 3 "all three plain type errors still report"

            Expect.isEmpty
                (errors |> List.filter (fun d -> Weir.Check.isBudgetMessage d.Message))
                "no plain type error is a budget diagnostic"
        }

        test "isBudgetMessage recognizes exactly the two budget strings [D:budget-stop-first]" {
            Expect.isTrue (Weir.Check.isBudgetMessage Weir.Check.budgetMsgSized) "sized message matches"
            Expect.isTrue (Weir.Check.isBudgetMessage Weir.Check.budgetMsgUnsized) "unsized message matches"
            Expect.isFalse (Weir.Check.isBudgetMessage "expected int, got string") "a plain error does not match"
        }

        test "district templates check: splice law, key-splice string, for binder [D:yaml-district]" {
            // a record splice violates the liftable law
            let bad = asm [ "let d = yaml"; "    x: $((Seq.initInfinite id))" ] // seq<int> IS liftable

            match Weir.Parser.parseLine realResolver bad with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te -> Expect.equal te.Ty (TNamed("Yaml", [])) "a district types as Yaml"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"

            // the effect-loop reflex teaches [D:yaml-district]: a district
            // `for` is bodyless, so a trailing `do` is named, not dumped
            let withDo =
                asm [ "let d = yaml"; "    xs:"; "        for x in [1] do"; "            - $x" ]

            match Weir.Parser.parseLine realResolver withDo with
            | Error e -> Expect.stringContains e "takes no `do`" "the district for names the do mistake"
            | other -> failtest $"expected the do-teaching error, got {other}"
        }

        test "district eval: splices lift, for instantiates, Option omits [D:yaml-district]" {
            let src =
                asm [
                    "let d = yaml"
                    "    kind: Pod"
                    "    count: $(1 + 2)"
                    "    gone: $(None)"
                    "    labels:"
                    "        for (k, v) in [(\"a\", \"x\")]"
                    "            $k: $v"
                ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VUnion("YMap", Some(Weir.Eval.VSeq pairs)) ->
                        let keys =
                            pairs
                            |> Seq.map (fun p ->
                                match p with
                                | Weir.Eval.VTuple [ Weir.Eval.VStr k; _ ] -> k
                                | _ -> failtest "bad pair")
                            |> List.ofSeq

                        Expect.equal keys [ "kind"; "count"; "labels" ] "None omitted; for nested under labels"
                    | v -> failtest $"expected YMap, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        // the district reads what Yaml.parse reads: `- - x`
        // [D:yaml-inline-nested-seq], a flush-left sequence under a key
        // and the dash gap's 4-space style [D:yaml-seq]
        test "district: nested dash items, flush-left and 4-space sequences read as Yaml.parse reads them" {
            let lit (lines: string list) =
                "["
                + (lines
                   |> List.map (fun l -> "\"" + l.Replace("\"", "\\\"") + "\"")
                   |> String.concat "; ")
                + "]"

            for body in
                [
                    [ "m:"; "  - - c"; "    - d" ]
                    [ "list:"; "- k: v"; "- j: w"; "other: 1" ]
                    [ "list:"; "  -   x: y"; "      z: w" ]
                    [ "- - - a"; "    - b" ]
                ] do
                let src = asm ("let d = yaml" :: (body |> List.map (fun l -> "    " + l)))

                match Weir.Parser.parseLine realResolver src with
                | Ok(SLet(_, e)) ->
                    match typecheck env e with
                    | Ok te ->
                        Expect.equal
                            (formatValue (Weir.Eval.eval valueEnv te))
                            (formatValue (run (lit body + " |> Yaml.parse")))
                            $"{body}"
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"{body}: {other}"
        }

        test "a quoted district key is literal, like a quoted value; a bare $name key splices [D:yaml-district]" {
            let src =
                asm [
                    "let d = yaml"
                    "    \"$(z)\": 1"
                    "    m:"
                    "        for (k, v) in [(\"a\", \"x\")]"
                    "            $k: $v"
                    "            \"$k\": $v"
                    "            '$v': \"$k\""
                    "    l:"
                    "        - \"$q\": 2"
                ]

            let rec keys (v: Weir.Eval.Value) : string list =
                match v with
                | Weir.Eval.VUnion("YMap", Some(Weir.Eval.VSeq pairs)) ->
                    pairs
                    |> Seq.collect (fun p ->
                        match p with
                        | Weir.Eval.VTuple [ Weir.Eval.VStr k; inner ] -> k :: keys inner
                        | _ -> failtest "bad pair")
                    |> List.ofSeq
                | Weir.Eval.VUnion("YSeq", Some(Weir.Eval.VSeq items)) -> items |> Seq.collect keys |> List.ofSeq
                | Weir.Eval.VUnion("YStr", Some(Weir.Eval.VStr s)) -> [ $"={s}" ]
                | Weir.Eval.VUnion("YInt", Some(Weir.Eval.VInt n)) -> [ $"={n}" ]
                | _ -> []

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    Expect.equal
                        (keys (Weir.Eval.eval valueEnv te))
                        [ "$(z)"; "=1"; "m"; "a"; "=x"; "$k"; "=x"; "$v"; "=$k"; "l"; "$q"; "=2" ]
                        "quoted keys stay text, as quoted values do"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "the heredoc district assembles exactly as yaml's does [D:text-block]" {
            match Weir.Assemble.assemble [ 1, "let t = <<<"; 2, "    echo $HOME {x}"; 3, ""; 4, "        deep" ] with
            | Ok [ ll ] ->
                Expect.stringContains ll.Text "let t = <<<" "the marker glyph survives"
                Expect.stringContains ll.Text "echo $HOME {x}" "content is bytes"

                Expect.stringContains
                    ll.Text
                    (Weir.Parser.sibSepStr + "    deep")
                    "relative indent is preserved behind the sentinel"
            | other -> failtest $"expected one logical line, got {other}"

            // the negative pin [D:text-block]: a glyph marker steals no
            // identifier — `text` binds and ends a line freely (the
            // word-marker's soft-reserve, retired by the glyph ruling)
            match Weir.Assemble.assemble [ 1, "let text = \"x\""; 2, "print text" ] with
            | Ok [ _; ll ] ->
                Expect.isFalse (ll.Text.Contains Weir.Parser.sibSepStr) "no district arms on a binding named text"
            | other -> failtest $"expected two logical lines, got {other}"
        }

        test "a comment-only line is heredoc bytes, leading included [D:block-scalars]" {
            // comment transparency once swallowed a `//`-leading FIRST
            // content line (the district was not yet active), so a
            // heredoc's `// header` line vanished from the value
            let evalStrs src =
                match Weir.Parser.parseLine realResolver src with
                | Ok(SLet(_, e)) ->
                    match typecheck env e with
                    | Ok te ->
                        match Weir.Eval.eval valueEnv te with
                        | Weir.Eval.VSeq items ->
                            items
                            |> Seq.map (fun v ->
                                match v with
                                | Weir.Eval.VStr s -> s
                                | v -> failtest $"bad item {v}")
                            |> List.ofSeq
                        | v -> failtest $"expected VSeq, got {v}"
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"parse: {other}"

            Expect.equal
                (evalStrs (asm [ "let t = <<<"; "    // header"; "    real" ]))
                [ "// header"; "real" ]
                "a leading comment-only line arms the district as its first byte line"

            Expect.equal
                (evalStrs (asm [ "let t = <<<"; "    first"; "    // mid"; "    tail // stays" ]))
                [ "first"; "// mid"; "tail // stays" ]
                "mid comment-only and trailing-comment bytes both survive"
        }

        test "an outdented comment inside a heredoc is an outdent error, after it a comment ends the block" {
            let assembled lines =
                Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l))

            for comment in [ "  // outdented"; "// col0" ] do
                match assembled [ "let t = <<<"; "    one"; comment; "    two"; "print t" ] with
                | Error e ->
                    Expect.stringContains e "line 3: this comment line outdents below the heredoc's first line" comment
                | Ok _ -> failtest $"a comment inside the block must not vanish: {comment}"

            match assembled [ "let t = <<<"; "    one"; "// after the block"; ""; "print t" ] with
            | Ok lls -> Expect.equal lls.Length 2 "a comment after the block stays transparent"
            | Error e -> failtest $"a comment after the block is fine: {e}"
        }

        test "a heredoc block is a seq<string>: verbatim lines, blanks survive [D:text-block]" {
            let src = asm [ "let t = <<<"; "    a $HOME {x}"; ""; "        deep" ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    Expect.equal te.Ty (TSeq TStr) "a heredoc block types as seq<string>"

                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VSeq items ->
                        let strs =
                            items
                            |> Seq.map (fun v ->
                                match v with
                                | Weir.Eval.VStr s -> s
                                | v -> failtest $"bad item {v}")
                            |> List.ofSeq

                        Expect.equal
                            strs
                            [ "a $HOME {x}"; ""; "    deep" ]
                            "$ and { are bytes; blanks survive; indent is relative to the first line"
                    | v -> failtest $"expected VSeq, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"

            // trailing blanks clip (the block-scalar ruling [D:block-scalars]):
            // the blank separating a block from the next statement is layout
            match Weir.Parser.parseLine realResolver (asm [ "let t = <<<"; "    a"; ""; "" ]) with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VSeq items ->
                        Expect.equal (List.ofSeq items) [ Weir.Eval.VStr "a" ] "trailing blank elements clip"
                    | v -> failtest $"expected VSeq, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "$<<< lines carry holes: {expr} evaluates, {{}} escape, $ stays a byte [D:text-block]" {
            let src = asm [ "let t = $<<<"; "    n={1 + 2} {{b}} $HOME" ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VSeq items ->
                        Expect.equal
                            (List.ofSeq items)
                            [ Weir.Eval.VStr "n=3 {b} $HOME" ]
                            "the string forms' hole rules, line by line"
                    | v -> failtest $"expected VSeq, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "$$<<< eval: braces literal, ${expr} substitutes, $$ -> $, $1 literal [D:heredoc-splice]" {
            // one statement: the block is the `let` RHS. `${expr}` exercises
            // the same IExpr path a bare `$name` splice does (both parse to
            // IExpr); a bare `$name` needs a binding, covered by the e2e.
            let src = asm [ "let t = $$<<<"; "    {a} ${1 + 2} $$ $1 \"quoted\"" ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VSeq items ->
                        Expect.equal
                            (List.ofSeq items)
                            [ Weir.Eval.VStr "{a} 3 $ $1 \"quoted\"" ]
                            "braces/quotes literal; ${1+2} splices; $$ is one $; $1 stays literal"
                    | v -> failtest $"expected VSeq, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "$$<<< a malformed ${…} splice is a parse error, never literal text [D:heredoc-splice]" {
            for line in [ "v=${1 +}"; "v=${name"; "v=${}" ] do
                match Weir.Parser.parseLine realResolver (asm [ "let t = $$<<<"; $"    {line}" ]) with
                | Error _ -> ()
                | other -> failtest $"{line}: expected a parse error, got {other}"

            // the whole Expecting list survives, not the tail of a wrapped line
            match Weir.Parser.parseLine realResolver (asm [ "let t = $$<<<"; "    v=${1 +}" ]) with
            | Error e -> Expect.stringContains e "Expecting: identifier" "the list's head is kept"
            | other -> failtest $"expected a parse error, got {other}"
        }

        test "$<<< dedents IDENTICALLY to <<<: only the hole differs [D:text-block]" {
            // the regression [D:text-block]: $<<< once left-trimmed every
            // line (runFragmentAt's `ws`), flattening deeper-indented lines
            // to column 0 while <<< kept the relative indent. The twins must
            // differ only in whether {holes} interpolate — indentation,
            // interior blanks, deeper indent and trailing-clip byte-identical.
            let evalBlock src =
                match Weir.Parser.parseLine realResolver src with
                | Ok(SLet(_, e)) ->
                    match typecheck env e with
                    | Ok te ->
                        match Weir.Eval.eval valueEnv te with
                        | Weir.Eval.VSeq items ->
                            items
                            |> Seq.map (fun v ->
                                match v with
                                | Weir.Eval.VStr s -> s
                                | v -> failtest $"bad item {v}")
                            |> List.ofSeq
                        | v -> failtest $"expected VSeq, got {v}"
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            // same content, plain vs interp — deeper indent, a hole at the
            // start of a deeper line, an interior blank, and a trailing blank
            let plainSrc =
                asm [
                    "let t = <<<"
                    "    a:"
                    "        b: 1"
                    "            c: 1"
                    ""
                    "        1"
                    "    e: 1"
                    ""
                ]

            let interpSrc =
                asm [
                    "let t = $<<<"
                    "    a:"
                    "        b: {1}"
                    "            c: {1}"
                    ""
                    "        {1}"
                    "    e: {1}"
                    ""
                ]

            let expected = [ "a:"; "    b: 1"; "        c: 1"; ""; "    1"; "e: 1" ]

            Expect.equal (evalBlock plainSrc) expected "plain <<< keeps relative + deeper indent"

            Expect.equal
                (evalBlock interpSrc)
                expected
                "$<<< dedents byte-identically to <<<: deeper indent kept, a leading-hole line keeps its indent, interior blank kept, trailing blank clipped"
        }

        test "heredoc errors: no block, outdent, the }-teaching, empty [D:text-block]" {
            match Weir.Assemble.assemble [ 1, "let t = <<<"; 2, "print \"x\"" ] with
            | Error e -> Expect.stringContains e "'<<<' needs an indented block" "the no-block error names the glyph"
            | other -> failtest $"expected an assembly error, got {other}"

            // a district that closes mid-statement composes [D:district-terminates]:
            // before, a pipe after the block glued into the last content
            // line as bytes (`alpha |> Seq.length`, silent corruption);
            // now the assembler terminates the content and the pipe pipes
            match Weir.Assemble.assemble [ 1, "let t = <<<"; 2, "    alpha"; 3, "|> Seq.length" ] with
            | Ok [ ll ] ->
                Expect.isTrue (ll.Text.Contains Weir.Parser.districtClose) "the content carries the close terminator"

                match Weir.Parser.parseLine realResolver ll.Text with
                | Ok(SLet(_, e)) ->
                    match typecheck env e with
                    | Ok te ->
                        Expect.equal (Weir.Eval.eval valueEnv te) (Weir.Eval.VInt 1L) "the pipe composes: one line"
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"expected SLet, got {other}"
            | other -> failtest $"expected the block to compose, got {other}"

            // the yaml twin composes the same way
            match Weir.Assemble.assemble [ 1, "let d = yaml"; 2, "    k: v"; 3, "|> to yaml" ] with
            | Ok [ ll ] ->
                Expect.isTrue (ll.Text.Contains Weir.Parser.districtClose) "the yaml block carries the terminator too"
            | other -> failtest $"expected the yaml block to compose, got {other}"

            match Weir.Assemble.assemble [ 1, "let t = $<<<"; 2, "        a"; 3, "    b" ] with
            | Error e -> Expect.stringContains e "this heredoc line outdents" "the outdent error names the form"
            | other -> failtest $"expected an assembly error, got {other}"

            match Weir.Assemble.assemble [ 1, "let t = $<<<"; 2, "    a } b" ] with
            | Ok [ ll ] ->
                match Weir.Parser.parseLine cmdResolver ll.Text with
                | Error e -> Expect.stringContains e "a literal } in a $<<< line is }}" "the brace teaching"
                | other -> failtest $"expected a parse error, got {other}"
            | other -> failtest $"assembly: {other}"

            // all-blank can only be handcrafted — the assembler never
            // arms on blanks — but the parser still refuses it
            match Weir.Parser.parseLine cmdResolver ("let t = <<<" + Weir.Parser.sibSepStr) with
            | Error e -> Expect.stringContains e "this heredoc block is empty" "the defensive empty error"
            | other -> failtest $"expected a parse error, got {other}"
        }

        test
            "piped-REPL accumulation: statementCount + pipedAttaches are the assembler's own boundary [D:repl-multiline]" {
            // the count is the assembler's statement count, comment-filtered.
            // A pending/open buffer is not-yet-countable (None).
            Expect.equal (Weir.Assemble.statementCount [ "let x = 1" ]) (Some 1) "one complete statement"
            Expect.equal (Weir.Assemble.statementCount [ "let x = 1"; "x + 1" ]) (Some 2) "two independent statements"
            // assemble reports structure: an armed district with no body
            // is an assembly error (None); an open `match with` is
            // structurally one statement (Some 1) — the parse-completeness
            // half is bufferComplete's, layered on top in the REPL
            Expect.equal (Weir.Assemble.statementCount [ "let block = <<<" ]) None "an armed heredoc with no body pends"

            // a leading-|> continuation merges: the count does not rise ->
            // pipedAttaches true (the pipeline stays one statement)
            Expect.isTrue (Weir.Assemble.pipedAttaches [ "xs" ] "|> Seq.sum") "a |> tail attaches to the value above"
            // a heredoc body line attaches (district content keeps count 1)
            Expect.isTrue
                (Weir.Assemble.pipedAttaches [ "let block = <<<"; "    a" ] "    b")
                "a second heredoc body line attaches"
            // an offside else attaches to the pending if under a let
            Expect.isTrue
                (Weir.Assemble.pipedAttaches [ "let r ="; "  if 1 > 0 then"; "    \"y\"" ] "  else")
                "an offside else attaches"

            // an independent statement does not attach — the count rises
            Expect.isFalse
                (Weir.Assemble.pipedAttaches [ "let x = 1" ] "x + 1")
                "an independent statement does not attach"

            Expect.isFalse
                (Weir.Assemble.pipedAttaches [ "type P = {"; "  x: int"; "}" ] "{ x = 1 }.x")
                "a value after a complete type is its own statement"
            // a blank/comment line breaks a completed statement (no attach)
            Expect.isFalse (Weir.Assemble.pipedAttaches [ "let x = 1" ] "") "a blank line breaks a completed statement"

            Expect.isFalse
                (Weir.Assemble.pipedAttaches [ "let x = 1" ] "// c")
                "a comment line breaks a completed statement"
            // a directive is one line: it never joins a block whose next
            // line could still continue it (match arms, a |> chain)
            Expect.isFalse
                (Weir.Assemble.pipedAttaches [ "match 1 with"; "| 1 -> \"one\""; "| _ -> \"other\"" ] "#echo")
                "a directive after match arms does not attach"

            Expect.isFalse
                (Weir.Assemble.pipedAttaches [ "let xs ="; "    [1]"; "    |> Seq.map id" ] "#help")
                "a directive after a |> chain does not attach"

            Expect.isTrue
                (Weir.Assemble.pipedAttaches [ "let block = <<<"; "    a" ] "    #b")
                "an indented # line in a heredoc body still attaches"
            // a column-0 opener that pends on its own starts its own
            // statement: the line above never joins its entry
            Expect.isFalse
                (Weir.Assemble.pipedAttaches [ "let a = nope" ] "let t = <<<")
                "a heredoc opener does not glue the line above"

            Expect.isFalse (Weir.Assemble.pipedAttaches [ "1 + 1" ] "let y = yaml") "nor does a yaml opener"
        }

        test "block scalars read: | and |- chomp semantically; content is bytes [D:block-scalars]" {
            let blockText d =
                match d with
                | Weir.Yaml.NMap([ (_, Weir.Yaml.NBlock(t, _)) ], _) -> t
                | other -> failtest $"expected one block entry, got {other}"

            // the form follows the value: | ends with exactly one newline,
            // |- with none — chomping is YAML's spelling for a distinction
            // weir strings already have
            Expect.equal (blockText (yamlDoc [ "k: |"; "    a"; "    b" ])) "a\nb\n" "| keeps one trailing newline"
            Expect.equal (blockText (yamlDoc [ "k: |-"; "    a"; "    b" ])) "a\nb" "|- strips it"

            // content is bytes: blank lines are newlines, ` #` is not a
            // comment, more-indented lines keep their extra indentation,
            // trailing blanks clip (|+ is the rejected keep-them form)
            Expect.equal
                (blockText (yamlDoc [ "k: |"; "    a # kept"; ""; "        deep"; "    z"; "" ]))
                "a # kept\n\n    deep\nz\n"
                "blanks, comments, extra indent all survive; trailing blank clips"

            // a `- |` item and a whole-document block scalar
            let itemText =
                match yamlDoc [ "- |-"; "    item text" ] with
                | Weir.Yaml.NSeq([ Weir.Yaml.NBlock(t, _) ], _) -> t
                | other -> failtest $"expected seq of block, got {other}"

            Expect.equal itemText "item text" "item-position block"

            let docText =
                match yamlDoc [ "|"; "  whole doc" ] with
                | Weir.Yaml.NBlock(t, _) -> t
                | other -> failtest $"expected doc block, got {other}"

            Expect.equal docText "whole doc\n" "document-position block"

            // the named errors, each with its line
            Expect.stringContains (yamlErr [ "k: |" ]) "needs an indented block" "header without a block"
            Expect.stringContains (yamlErr [ "k: >"; "    a" ]) "folded block scalars (>)" "folded rejected"
            Expect.stringContains (yamlErr [ "k: |+"; "    a" ]) "'|+'" "|+ rejected"
            Expect.stringContains (yamlErr [ "k: |0"; "    a" ]) "1-9" "a 0 indicator rejected"
            Expect.stringContains (yamlErr [ "k: |2+"; "    a" ]) "'|+'" "|+ with an indicator still rejected"

            Expect.stringContains
                (yamlErr [ "k: |3"; "  a" ])
                "left of the block scalar's content indentation"
                "content left of the stated indent"

            // an indentation indicator states the content indent relative to
            // the parent (key or dash column), so the first line may carry
            // leading spaces — PyYAML's reading [D:yaml-indent-indicator]
            Expect.equal (blockText (yamlDoc [ "k: |2"; "    lead"; "  base" ])) "  lead\nbase\n" "|2"
            Expect.equal (blockText (yamlDoc [ "k: |2-"; "    lead"; "  base" ])) "  lead\nbase" "|2- strips"
            Expect.equal (blockText (yamlDoc [ "k: |-2"; "    lead"; "  base" ])) "  lead\nbase" "|-2, either order"

            let seqItem =
                match yamlDoc [ "- |2"; "    lead"; "  base" ] with
                | Weir.Yaml.NSeq([ Weir.Yaml.NBlock(t, _) ], _) -> t
                | other -> failtest $"expected seq of block, got {other}"

            Expect.equal seqItem "  lead\nbase\n" "relative to the dash"
            Expect.stringContains (yamlErr [ "k: | x" ]) "no inline content" "inline content rejected"

            Expect.stringContains
                (yamlErr [ "k: |"; "        a"; "    b" ])
                "left of the block scalar's content indentation"
                "left-of-content line named"
        }

        test "zero-indent block sequences read: kubectl's same-column form, nested; malformed still errors [D:yaml-seq]" {
            // a valueless mapping key whose block sequence sits at the
            // same column (kubectl's zero-indent form): the sequence is
            // the value, with the compact `- key: v` first entry
            match yamlDoc [ "items:"; "- apiVersion: v1"; "  kind: Pod" ] with
            | Weir.Yaml.NMap([ ("items", Weir.Yaml.NSeq([ Weir.Yaml.NMap(fields, _) ], _)) ], _) ->
                Expect.equal
                    (fields |> List.map fst)
                    [ "apiVersion"; "kind" ]
                    "the same-column seq is items' value; the compact dash entry parses"
            | other -> failtest $"zero-indent seq did not become items' value: {other}"

            // both indent styles read to the same node — the read side
            // accepts kubectl's flush form and the classic indented form
            Expect.equal
                (yamlDoc [ "items:"; "- a: 1"; "- a: 2" ])
                (yamlDoc [ "items:"; "  - a: 1"; "  - a: 2" ])
                "flush and indented block sequences read equal"

            // nested: a seq item's map contains its own same-column seq
            match
                yamlDoc [
                    "items:"
                    "- metadata:"
                    "    ownerReferences:"
                    "    - apiVersion: apps/v1"
                    "      kind: ReplicaSet"
                ]
            with
            | Weir.Yaml.NMap([ ("items",
                                Weir.Yaml.NSeq([ Weir.Yaml.NMap([ ("metadata",
                                                                   Weir.Yaml.NMap([ ("ownerReferences",
                                                                                     Weir.Yaml.NSeq([ Weir.Yaml.NMap(inner,
                                                                                                                     _) ],
                                                                                                    _)) ],
                                                                                  _)) ],
                                                                _) ],
                                               _)) ],
                             _) ->
                Expect.equal
                    (inner |> List.map fst)
                    [ "apiVersion"; "kind" ]
                    "a same-column ownerReferences seq nested under metadata reads"
            | other -> failtest $"nested zero-indent seq failed: {other}"

            // a zero-indent seq followed by a sibling mapping key: the
            // sequence's extent stops at the sibling
            match yamlDoc [ "items:"; "- a: 1"; "kind: List" ] with
            | Weir.Yaml.NMap([ ("items", Weir.Yaml.NSeq(_, _)); ("kind", Weir.Yaml.NScalar("List", _, _)) ], _) -> ()
            | other -> failtest $"sibling key after a zero-indent seq: {other}"

            // the regression: a genuine inline-value + nested-block (the
            // nested block is more indented, not a same-column seq) still
            // errors — the fix must not swallow this
            match Weir.Yaml.parseDocs ([ "k: value"; "  nested: x" ] |> List.mapi (fun i l -> i + 1, l)) with
            | Error e ->
                Expect.stringContains
                    e
                    "has both an inline value and a nested block"
                    "malformed inline+block still fires"
            | Ok d -> failtest $"malformed input must error, got {d}"
        }

        test "a line indented between a block and its parent errors with its line, never drops (B9)" {
            let left = "indented less than the block it belongs to"

            Expect.stringContains
                (yamlErr [ "b:"; "  c: 2"; " d: 3"; "e: 5" ])
                $"line 3: this line is {left}"
                "map entry between levels"

            Expect.stringContains
                (yamlErr [ "- a: 1"; "  b: 2"; " c: 3" ])
                "line 3:"
                "compact item entry between levels"

            Expect.stringContains
                (yamlErr [ "a:"; "    - x"; "  - y" ])
                $"line 3: this line is {left}"
                "seq item between levels"

            Expect.stringContains
                (yamlErr [ "a:"; "  - x"; "  b: 1" ])
                "line 3: expected a '- ' sequence item"
                "key after a seq at the seq's column"

            Expect.stringContains
                (yamlErr [ "- x"; "y: 1" ])
                "line 2: expected a '- ' sequence item"
                "root seq then a key"

            // control: the zero-indent seq form keeps its sibling key
            match yamlDoc [ "a:"; "- x"; "b: 1" ] with
            | Weir.Yaml.NMap([ ("a", Weir.Yaml.NSeq(_, _)); ("b", _) ], _) -> ()
            | other -> failtest $"zero-indent seq + sibling: {other}"
        }

        test "a plain value holding ': ' refuses like every other yaml reader (triage 15a)" {
            Expect.stringContains
                (yamlErr [ "f: a: b" ])
                "line 1: mapping values are not allowed here — quote the value"
                "inner ': '"

            Expect.stringContains (yamlErr [ "f: a:" ]) "quote the value" "trailing ':'"
            Expect.stringContains (yamlErr [ "- x"; "- a: b: c" ]) "line 2:" "inside a compact item"

            match yamlDoc [ "f: \"a: b\""; "u: http://x:80/p" ] with
            | Weir.Yaml.NMap([ ("f", Weir.Yaml.NScalar("a: b", true, _))
                               ("u", Weir.Yaml.NScalar("http://x:80/p", false, _)) ],
                             _) -> ()
            | other -> failtest $"quoted ': ' and a bare ':' stay values: {other}"
        }

        test "a compact sequence map takes its column from the dash gap (triage 15b)" {
            Expect.equal
                (yamlDoc [ "items:"; "-   x: 1"; "    y: 2"; "-   x: 3"; "    y: 4" ])
                (yamlDoc [ "items:"; "- x: 1"; "  y: 2"; "- x: 3"; "  y: 4" ])
                "the 4-indent formatter style reads as the 2-indent one"

            Expect.stringContains
                (yamlErr [ "-   x: 1"; "  y: 2" ])
                "line 2:"
                "a sibling left of the gap column still errors"
        }

        test "'...' ends a document (triage 15c)" {
            match yamlDoc [ "f: a"; "..." ] with
            | Weir.Yaml.NMap([ ("f", Weir.Yaml.NScalar("a", false, _)) ], _) -> ()
            | other -> failtest $"trailing '...': {other}"

            match Weir.Yaml.parseDocs ([ "a: 1"; "..."; "---"; "b: 2"; "..." ] |> List.mapi (fun i l -> i + 1, l)) with
            | Ok [ _; _ ] -> ()
            | other -> failtest $"'...' then '---' is two documents, no empty one: {other}"

            match yamlDoc [ "f: \"...\"" ] with
            | Weir.Yaml.NMap([ ("f", Weir.Yaml.NScalar("...", true, _)) ], _) -> ()
            | other -> failtest $"'...' as a value stays data: {other}"
        }

        test "quoted yaml keys unescape like values; every key to yaml writes reads back (B25)" {
            let keysOf d =
                match d with
                | Weir.Yaml.NMap(es, _) -> es |> List.map fst
                | other -> failtest $"expected a mapping, got {other}"

            Expect.equal
                (keysOf (
                    yamlDoc [
                        "\"a\\tb\": 1"
                        "\"n\\nl\": 2"
                        "\"q\\\"\": 3"
                        "\"\": 4"
                        "'it''s': 5"
                        "\"\\u00e9\": 6"
                    ]
                ))
                [ "a\tb"; "n\nl"; "q\""; ""; "it's"; "é" ]
                "escapes resolve in quoted keys; \"\" is the empty key"

            Expect.stringContains
                (yamlErr [ "\"a\\q\": 1" ])
                "line 1: in this key: unsupported escape"
                "a bad key escape is located"

            Expect.stringContains
                (yamlErr [ "k: 1"; ": 2" ])
                "line 2: expected 'key: value'"
                "a bare ':' is still no key"

            let keys = [ "a\tb"; "n\nl"; "q\""; ""; "null"; "it's"; "back\\"; " lead" ]

            let written =
                let lit (k: string) =
                    k.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\t", "\\t").Replace("\n", "\\n")

                run (
                    "["
                    + (keys |> List.map (fun k -> "(\"" + lit k + "\", 1)") |> String.concat "; ")
                    + "] |> to yaml"
                )
                |> forceSeq
                |> List.map (function
                    | VStr s -> s
                    | other -> failtest $"{other}")

            Expect.equal (keysOf (yamlDoc written)) keys "to yaml keys read back verbatim"
        }

        test
            "empty flow collections read: {} and [] as values (map + seq position); populated flow still rejects [D:yaml-empty-flow]" {
            // `{}` → the empty mapping, `[]` → the empty sequence, as a
            // map value (inner whitespace tolerated)
            match yamlDoc [ "resources: {}"; "args: []"; "sc: { }" ] with
            | Weir.Yaml.NMap([ ("resources", Weir.Yaml.NMap([], _))
                               ("args", Weir.Yaml.NSeq([], _))
                               ("sc", Weir.Yaml.NMap([], _)) ],
                             _) -> ()
            | other -> failtest $"empty flow as map values: {other}"

            // and as a sequence item
            match yamlDoc [ "- {}"; "- []" ] with
            | Weir.Yaml.NSeq([ Weir.Yaml.NMap([], _); Weir.Yaml.NSeq([], _) ], _) -> ()
            | other -> failtest $"empty flow as seq items: {other}"

            // the narrow exception: populated flow still rejects with the
            // block-only teaching — the ambiguity that justifies it fires
            // at one-or-more elements, in both map and seq position
            Expect.stringContains
                (yamlErr [ "m: {a: 1}" ])
                "flow style is outside the yaml subset"
                "populated flow map rejects"

            Expect.stringContains
                (yamlErr [ "s: [1, 2]" ])
                "flow style is outside the yaml subset"
                "populated flow seq rejects"

            Expect.stringContains
                (yamlErr [ "- {a: 1}" ])
                "flow style is outside the yaml subset"
                "populated flow in seq item rejects"
        }

        test "multi-line quoted scalars read: flow folding, both quote styles, both positions [D:quoted-fold]" {
            let scalarOf d =
                match d with
                | Weir.Yaml.NMap([ (_, Weir.Yaml.NScalar(t, q, _)) ], _) -> t, q
                | other -> failtest $"expected one scalar entry, got {other}"

            // the folding law (PyYAML-refereed set): one break folds to
            // one space; each empty continuation line contributes a
            // newline; continuation indentation strips; trailing space
            // before the closing quote is content
            Expect.equal (scalarOf (yamlDoc [ "k: 'a"; "  b'" ])) ("a b", true) "one break folds to a space"
            Expect.equal (scalarOf (yamlDoc [ "k: 'a"; ""; "  b'" ])) ("a\nb", true) "an empty line folds to a newline"

            Expect.equal
                (scalarOf (yamlDoc [ "k: 'a"; ""; ""; "  b'" ]))
                ("a\n\nb", true)
                "two empty lines, two newlines"

            Expect.equal
                (scalarOf (yamlDoc [ "k: 'trail"; "  end. '" ]))
                ("trail end. ", true)
                "trailing space inside the quote survives"

            Expect.equal
                (scalarOf (yamlDoc [ "k: 'it''s"; "  ok, it''s'" ]))
                ("it's ok, it's", true)
                "'' escapes mid-continuation"
            // double-quoted: the single-line escape set extends across
            // the fold (a \-escaped line break is not in the subset)
            Expect.equal
                (scalarOf (yamlDoc [ "k: \"a\\n x"; "  b\"" ]))
                ("a\n x b", true)
                "double-quoted folds, escapes resolve"

            // the kubectl message form: colons inside, deeper continuations
            Expect.equal
                (scalarOf (
                    yamlDoc [
                        "k: 'The node was low on resource: ephemeral-storage. Threshold quantity:"
                        "    25462616238, available: 24515828Ki. '"
                    ]
                ))
                ("The node was low on resource: ephemeral-storage. Threshold quantity: 25462616238, available: 24515828Ki. ",
                 true)
                "the kubectl eviction message reads"

            // sequence-item position; the plain sibling stays plain
            match yamlDoc [ "- 'a: x"; "  b'"; "- two" ] with
            | Weir.Yaml.NSeq([ Weir.Yaml.NScalar("a: x b", true, 1); Weir.Yaml.NScalar("two", false, _) ], _) -> ()
            | other -> failtest $"seq-item multiline: {other}"

            // zero-indent-nested position: kubectl's List shape, the
            // continuation consumed by the scalar, the sibling key intact
            match
                yamlDoc [
                    "items:"
                    "- status:"
                    "    message: 'a:"
                    "      b'"
                    "    reason: Evicted"
                    "kind: List"
                ]
            with
            | Weir.Yaml.NMap([ ("items",
                                Weir.Yaml.NSeq([ Weir.Yaml.NMap([ ("status",
                                                                   Weir.Yaml.NMap([ ("message",
                                                                                     Weir.Yaml.NScalar("a: b", true, _))
                                                                                    ("reason",
                                                                                     Weir.Yaml.NScalar("Evicted",
                                                                                                       false,
                                                                                                       _)) ],
                                                                                  _)) ],
                                                                _) ],
                                               _))
                               ("kind", Weir.Yaml.NScalar("List", false, _)) ],
                             _) -> ()
            | other -> failtest $"zero-indent nested multiline: {other}"

            // the errors: unterminated (dedent or EOF before the close)
            // names the opening line in the unclosed family; content
            // after the closing quote names its line; a deeper line
            // past the close has no owner
            Expect.stringContains
                (yamlErr [ "a: 1"; "k: 'x"; "  y"; "z: 1" ])
                "line 2: unclosed single-quoted scalar"
                "a dedent before the close errors at the opening line"

            Expect.stringContains
                (yamlErr [ "k: \"x" ])
                "line 1: unclosed double-quoted scalar"
                "EOF before the close, same family"

            Expect.stringContains
                (yamlErr [ "k: 'x"; "  y' junk" ])
                "line 2: content after the closing '"
                "content after the close errors with its line"

            Expect.stringContains
                (yamlErr [ "k: 'x"; "  y'"; "  z: 1" ])
                "line 3: this line sits inside the quoted scalar's indentation but after its closing quote"
                "a deeper line past the close is named"
        }

        test
            "multi-line quoted: Norway stays string; the write side keeps its own spelling and roundtrips [D:quoted-fold]" {
            let env2 = env |> declare "type QKV = { k: string }"

            let evalStr prog =
                match Weir.Parser.parseStmt prog with
                | Ok(SExpr e) ->
                    match Weir.Check.typecheck env2 e with
                    | Ok te -> Weir.Eval.eval valueEnv te
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            // the read holds the folded content
            Expect.equal
                (evalStr "([\"k: 'a: b\"; \"  c\"; \"\"; \"  d '\"] |> from yaml QKV).k")
                (VStr "a: b c\nd ")
                "the string field holds the folded content"

            // `to yaml` re-emits weir's own spelling — the block scalar
            // for a multiline string [D:block-scalars], never a
            // multi-line quoted form — and reading that back yields the
            // same string
            Expect.equal
                (evalStr
                    "let d = [\"k: 'a: b\"; \"  c\"; \"\"; \"  d '\"] |> from yaml QKV in d |> to yaml |> Seq.freeze |> show")
                (VStr "[\"k: |-\"; \"  a: b c\"; \"  d \"]")
                "the write side's own spelling, unchanged"

            Expect.equal
                (evalStr
                    "let d = [\"k: 'a: b\"; \"  c\"; \"\"; \"  d '\"] |> from yaml QKV in (d |> to yaml |> from yaml QKV).k == d.k")
                (VBool true)
                "the roundtrip pin"

            // the Norway law across the fold: a folded quoted no-like
            // value is a string even at a bool field
            let env3 = env |> declare "type QB = { k: bool }"

            match Weir.Parser.parseStmt "([\"k: 'no\"; \"  way'\"] |> from yaml QB).k" with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck env3 e with
                | Ok te ->
                    let ex = Expect.throwsC (fun () -> Weir.Eval.eval valueEnv te |> ignore) id

                    Expect.stringContains
                        ex.Message
                        "a quoted scalar is a string"
                        "quotedness stays load-bearing across the fold"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test
            "#infer sanitizes non-identifier keys: [<Wire>] over a valid ident; clean keys stay bare; collisions dedupe [D:infer-wire-sanitize]" {
            let inferStr (v: Value) =
                match v with
                | VStr s -> s
                | v -> failtest $"expected a string, got {formatValue v}"

            // moved pin [D:repl-infer]: uniform dirty-key objects draft
            // the open mapping now, so the record sanitizer is pinned on
            // a mixed-value object (map detection needs one value shape)
            // — hyphen/dot/slash keys ride [<Wire>] over a camelCased
            // identifier; the already-legal key stays bare
            let out =
                inferStr (
                    run
                        "Json.inferShape [\"{\\\"k8s-app\\\":\\\"a\\\",\\\"node.kubernetes.io/os\\\":2,\\\"clean\\\":\\\"c\\\"}\"]"
                )

            Expect.stringContains out "[<Wire \"k8s-app\">]" "the dirty key carries its wire attribute"
            Expect.stringContains out "k8sApp: string" "the sanitized identifier is a legal field name"
            Expect.stringContains out "nodeKubernetesIoOs: int" "dots and slashes camelCase into one identifier"
            Expect.stringContains out "clean: string" "an already-legal key stays a bare field"
            Expect.isFalse (out.Contains "clean\">]") "a clean key carries NO wire attribute"

            // the uniform dirty-key object is the open mapping — keys
            // are data, no [<Wire>] rides at all [D:repl-infer]
            let map =
                inferStr (
                    run
                        "Json.inferShape [\"{\\\"labels\\\":{\\\"k8s-app\\\":\\\"a\\\",\\\"node.kubernetes.io/os\\\":\\\"b\\\",\\\"clean\\\":\\\"c\\\"}}\"]"
                )

            Expect.stringContains map "labels: seq<string * string>" "the label object drafts as the mapping"
            Expect.isFalse (map.Contains "Wire") "mapping keys are data — no wire attribute is drafted"
            Expect.stringContains map "mostly non-identifier keys" "the map note rides as a // line"

            // a collision: `aB` (clean) reserves its name; the two dirty
            // keys that would also land on `aB` take `aB2`/`aB3` (the
            // int value keeps the object off the map path)
            let col =
                inferStr (run "Json.inferShape [\"{\\\"a-b\\\":\\\"1\\\",\\\"a.b\\\":\\\"2\\\",\\\"aB\\\":3}\"]")

            Expect.stringContains col "aB2: string" "the first colliding sanitized key disambiguates"
            Expect.stringContains col "aB3: string" "the second colliding sanitized key disambiguates"
            Expect.stringContains col "\n    aB: int" "the clean key keeps its bare name"

            // the historical reserved-word landings are unchanged
            let ty = inferStr (run "Json.inferShape [\"{\\\"type\\\":\\\"x\\\"}\"]")
            Expect.stringContains ty "[<Wire \"type\">]" "a reserved word still rides Wire"
            Expect.stringContains ty "kind: string" "type still lands on 'kind'"
        }

        test "inferShape drafts float for an integer past int64, as the readers type it (B29)" {
            let inferStr (v: Value) =
                match v with
                | VStr s -> s
                | v -> failtest $"expected a string, got {formatValue v}"

            let j =
                inferStr (
                    run
                        "Json.inferShape [\"{\\\"big\\\":99999999999999999999,\\\"min\\\":-9223372036854775808,\\\"n\\\":5}\"]"
                )

            Expect.stringContains j "big: float" "json: past int64 drafts float"
            Expect.stringContains j "min: int" "json: int64's edge stays int"
            Expect.stringContains j "n: int" "json: a small integer stays int"

            let y = inferStr (run "Yaml.inferShape [\"big: 99999999999999999999\"; \"n: 5\"]")
            Expect.stringContains y "big: float" "yaml: past int64 drafts float"
            Expect.stringContains y "n: int" "yaml: a small integer stays int"
        }

        test "inferShape refusals speak weir and name the caller, not #infer" {
            Expect.equal
                (msgOf "Json.inferShape [\"{bad\"]")
                "Json.inferShape: the sample is not valid JSON (line 1, byte 2)"
                "bad"

            Expect.equal
                (msgOf "Json.inferShape []")
                "Json.inferShape: the sample is empty — there is no JSON to draft from"
                "empty"

            Expect.equal (msgOf "Yaml.inferShape []") "Yaml.inferShape: the yaml sample is empty" "yaml empty"
        }

        test "block scalars render: the form follows the value, both directions [D:block-scalars]" {
            // no policy exists: | means ends-with-one-newline, |- means
            // ends-with-none — read and write agree by construction
            Expect.equal
                (run "[(\"k\", \"a\\nb\\n\")] |> to yaml" |> forceSeq)
                [ VStr "k: |"; VStr "  a"; VStr "  b" ]
                "trailing newline renders |"

            Expect.equal
                (run "[(\"k\", \"a\\nb\")] |> to yaml" |> forceSeq)
                [ VStr "k: |-"; VStr "  a"; VStr "  b" ]
                "no trailing newline renders |-"

            // multiple trailing newlines have no block form (|+ is
            // rejected) — the quoted-with-escapes fallback keeps every
            // legal string renderable, exactly [D:content-bytes]
            Expect.equal
                (run "[(\"k\", \"a\\n\\n\")] |> to yaml" |> forceSeq)
                [ VStr "k: \"a\\n\\n\"" ]
                "the quoted fallback: valid, exact, round-trips"

            // the quoting-law boundary: one-line \"007\" quotes; a
            // multiline string starting 007 is a block scalar, unquoted
            Expect.equal
                (run "[(\"k\", \"007\\nx\")] |> to yaml" |> forceSeq)
                [ VStr "k: |-"; VStr "  007"; VStr "  x" ]
                "the block side of the boundary"
        }

        test "district comments: a SHALLOW comment between a key and its block stays transparent [D:district-hash]" {
            // the yaml fuzz production's first catch: `// noise` at an
            // indent between the key's and its content's made
            // firstContentRel derive the nested indent from the comment
            let src =
                asm [
                    "let d = yaml"
                    "    k0:"
                    "     // fuzz-shaped noise"
                    "        n0: 65"
                    "    k1: 56"
                ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, { Kind = EYaml _ })) -> ()
            | other -> failtest $"the shallow comment must stay transparent: {other}"

            // the second face (same session): a comment at the unit's own
            // indent must not close a key's nested extent
            let src2 =
                asm [ "let d = yaml"; "    k2:"; "    // noise at unit indent"; "        n0: w1" ]

            match Weir.Parser.parseLine realResolver src2 with
            | Ok(SLet(_, { Kind = EYaml _ })) -> ()
            | other -> failtest $"the unit-indent comment must ride the extent: {other}"
        }

        test
            "schema completion: marker-local `schema=`, vendored names after it, adapters offer nothing [D:yaml-schemas]" {
            // `schema` is deliberately not a Parser.keywords member — a
            // keyword would reserve the identifier; the marker context
            // offers it instead
            Expect.isFalse (Weir.Parser.keywords.Contains "schema") "marker-local, not a keyword"

            let env = Weir.Builtins.typeEnvStrict
            let line1 = "let d = yaml "
            Expect.contains (Weir.Complete.suggest env line1 line1.Length) "schema=" "the marker line offers schema="

            let line3 = "x |> to yaml "

            Expect.isFalse
                (Weir.Complete.suggest env line3 line3.Length |> List.contains "schema=")
                "the adapter is not a marker — no offer"
        }

        test "contracts: .weir discovery walks up, stops at the first, bounded by .git [D:contracts-spine]" {
            let root = weirPath (tempPath "weirdisc")

            let deep = System.IO.Path.Combine(root, "repo", "a", "b")
            System.IO.Directory.CreateDirectory deep |> ignore

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "repo", ".git"))
            |> ignore

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".weir"))
            |> ignore

            // no .weir inside the repo: the walk must stop at .git, never
            // reaching the grandparent's .weir outside the boundary
            let bounded =
                match Weir.Contracts.findWeirDir deep with
                | Error e -> e
                | Ok d -> failtest $"crossed the .git boundary to {d}"

            Expect.stringContains bounded "repo root" "bounded by .git"

            // a .weir inside the repo wins from anywhere under it
            let inner = System.IO.Path.Combine(root, "repo", ".weir")
            System.IO.Directory.CreateDirectory inner |> ignore

            let found =
                match Weir.Contracts.findWeirDir deep with
                | Ok d -> System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName d)
                | Error e -> failtest e

            Expect.equal found "repo" "first .weir wins"

            System.IO.Directory.Delete(root, true)
        }

        test "contracts: the schema subset parses the corpus shapes and rejects the rest with teaching [D:yaml-schemas]" {
            let ok =
                Weir.Contracts.parseSchema
                    "t"
                    """{ "type": "object", "properties": { "n": { "type": "integer" }, "e": { "enum": ["a", "b"] }, "u": { "oneOf": [{ "type": ["integer", "null"] }, { "type": ["string", "null"] }] } }, "required": ["n"], "additionalProperties": false, "description": "ignored", "x-kubernetes-map-type": "ignored" }"""

            let propsReq =
                match ok with
                | Ok {
                         Root = Weir.Contracts.SObject(props, req, Weir.Contracts.Closed)
                     } -> List.map fst props, req
                | other -> failtest $"expected SObject, got {other}"

            Expect.equal (fst propsReq) [ "n"; "e"; "u" ] "props in order"
            Expect.equal (snd propsReq) [ "n" ] "required"

            let errOf src =
                match Weir.Contracts.parseSchema "t" src with
                | Error e -> e
                | Ok s -> failtest $"expected rejection, got {s}"

            // an in-document $ref is in the subset now [D:schema-types];
            // a dangling one still refuses, naming the resolution law
            Expect.stringContains
                (errOf """{ "$ref": "#/definitions/x" }""")
                "does not resolve"
                "a dangling in-document $ref refuses at parse"

            Expect.stringContains
                (errOf """{ "$ref": "_definitions.json#/definitions/x" }""")
                "standalone"
                "a cross-file $ref teaching names the way out"

            Expect.stringContains (errOf """{ "allOf": [] }""") "composition" "empty allOf rejected"

            Expect.stringContains
                (errOf """{ "allOf": [{ "type": "string" }, { "type": "object" }] }""")
                "composition"
                "allOf of two substantive schemas rejected"

            Expect.stringContains
                (errOf """{ "type": "object", "properties": { "p": { "patternProperties": {} } } }""")
                "at properties.p.: 'patternProperties'"
                "unknown keyword named WITH its JSON path"

            Expect.stringContains
                (errOf """{ "oneOf": [{ "type": "object" }] }""")
                "IntOrString"
                "oneOf restricted to scalar alternatives"
        }

        test "contracts: the subset's k8s-OpenAPI growth — $ref/defs, allOf-of-one, anyOf, nullable [D:schema-types]" {
            let parsed src =
                match Weir.Contracts.parseSchema "t" src with
                | Ok d -> d
                | Error e -> failtest e

            // in-document $ref resolves against the root holders; allOf
            // of one-ref-plus-annotations flattens to the ref
            let doc =
                parsed
                    """{ "definitions": { "io.k8s.Meta": { "type": "object", "properties": { "name": { "type": "string" } } } },
                           "type": "object",
                           "properties": { "metadata": { "allOf": [{ "$ref": "#/definitions/io.k8s.Meta" }, { "description": "annotation-only" }] } } }"""

            let metaProp =
                match doc.Root with
                | Weir.Contracts.SObject([ ("metadata", sub) ], [], _) -> sub
                | other -> failtest $"expected one metadata prop, got {other}"

            Expect.equal metaProp (Weir.Contracts.SRef "io.k8s.Meta") "allOf-of-one-ref flattens to the ref"
            Expect.isTrue (Map.containsKey "io.k8s.Meta" doc.Defs) "the root holder collects"

            // $defs and components.schemas are the same holder, and a
            // JSON-pointer escape unescapes in the ref name
            let d2 =
                parsed """{ "$defs": { "a~b": { "type": "integer" } }, "$ref": "#/$defs/a~0b" }"""

            Expect.equal d2.Root (Weir.Contracts.SRef "a~b") "the pointer unescapes"

            let d3 =
                parsed
                    """{ "components": { "schemas": { "P": { "type": "object" } } }, "$ref": "#/components/schemas/P" }"""

            Expect.equal d3.Root (Weir.Contracts.SRef "P") "components.schemas is a holder"

            // anyOf: all-scalar folds (the IntOrString idiom, k8s's
            // OpenAPI spelling); mixed alternatives keep as SChoice
            let folded =
                match (parsed """{ "anyOf": [{ "type": "integer" }, { "type": "string" }] }""").Root with
                | Weir.Contracts.SScalar kinds -> kinds
                | other -> failtest $"expected the scalar fold, got {other}"

            Expect.equal folded (Set.ofList [ "integer"; "string" ]) "anyOf all-scalar folds"

            let choice =
                match (parsed """{ "anyOf": [{ "type": "object" }, { "type": "string" }] }""").Root with
                | Weir.Contracts.SChoice [ Weir.Contracts.SObject _; Weir.Contracts.SScalar _ ] -> true
                | _ -> false

            Expect.isTrue choice "mixed anyOf keeps SChoice in document order"

            // the nullable spellings: type-array on a scalar folds into
            // the kind set; `nullable: true` folds the same way; a
            // nullable object (either spelling) wraps
            let kindsOf src =
                match (parsed src).Root with
                | Weir.Contracts.SScalar kinds -> kinds
                | other -> failtest $"expected SScalar, got {other}"

            Expect.isTrue ((kindsOf """{ "type": ["string", "null"] }""").Contains "null") "type-array null"

            Expect.isTrue
                ((kindsOf """{ "type": "string", "nullable": true }""").Contains "null")
                "nullable: true folds into the kind set"

            let wrapped src =
                match (parsed src).Root with
                | Weir.Contracts.SNullable(Weir.Contracts.SObject _) -> true
                | _ -> false

            Expect.isTrue (wrapped """{ "type": "object", "nullable": true }""") "nullable object wraps"
            Expect.isTrue (wrapped """{ "type": ["object", "null"] }""") "type-array null object wraps"

            // a pure ref cycle derefs to SAny — validation relaxes,
            // nothing loops
            let cyc =
                parsed
                    """{ "definitions": { "a": { "$ref": "#/definitions/b" }, "b": { "$ref": "#/definitions/a" } }, "$ref": "#/definitions/a" }"""

            Expect.equal
                (Weir.Contracts.deref cyc.Defs Set.empty cyc.Root)
                Weir.Contracts.SAny
                "ref cycle lands on SAny"

            // the holders are root-only — a nested one names the law
            let nested =
                match
                    Weir.Contracts.parseSchema
                        "t"
                        """{ "type": "object", "properties": { "p": { "definitions": {} } } }"""
                with
                | Error e -> e
                | Ok _ -> failtest "expected rejection"

            Expect.stringContains nested "ROOT" "nested holder teaching"
        }

        test "schema-types: the mapping rules, pinned one by one [D:schema-types]" {
            let gen src =
                match Weir.Contracts.parseSchema "t" src with
                | Error e -> failtest e
                | Ok doc ->
                    match
                        Weir.SchemaTypes.moduleText
                            Weir.Parser.keywords
                            Set.empty
                            "t"
                            None
                            "0123456789abcdef"
                            "http://src"
                            doc
                    with
                    | Ok g -> g
                    | Error e -> failtest e

            let g =
                gen
                    """{ "type": "object",
                           "required": ["name", "deadline"],
                           "properties": {
                             "name": { "type": "string" },
                             "image": { "type": "string" },
                             "deadline": { "type": ["integer", "null"] },
                             "hostIP": { "type": "string", "nullable": true },
                             "labels": { "type": "object", "additionalProperties": { "type": "string" } },
                             "phase": { "type": "string", "enum": ["Pending", "Running"] },
                             "port": { "anyOf": [{ "type": "integer" }, { "type": "string" }] },
                             "type": { "type": "string" },
                             "replicas": { "type": "integer" },
                             "ratio": { "type": "number" },
                             "ready": { "type": "boolean" } } }"""

            // required member → plain field; absent from required → Option
            Expect.stringContains g.Text "\n    name: string\n" "required string stays plain"
            Expect.stringContains g.Text "image: Option<string>" "optional → Option — the fact no sample carries"
            // type ["X","null"] and nullable: true → Option, never doubled
            Expect.stringContains
                g.Text
                "deadline: Option<int>"
                "the type-array null spelling → Option<int>, wrapped once"

            Expect.stringContains g.Text "hostIP: Option<string>" "nullable: true → Option"
            // additionalProperties object → the open mapping
            Expect.stringContains g.Text "labels: Option<seq<string * string>>" "additionalProperties → seq<string * V>"
            // enum → string + a note listing the values
            Expect.stringContains g.Text "phase: Option<string>" "enum drafts string"
            Expect.stringContains g.Text "an enum of 'Pending', 'Running'" "the enum note lists the values"
            // anyOf int-or-string → the opaque node + a note naming the cases
            // [D:schema-int-or-string]
            Expect.stringContains g.Text "port: Option<Yaml>" "IntOrString drafts Yaml"
            Expect.stringContains g.Text "allows integer/string — drafted Yaml" "the IntOrString note names both kinds"
            // a keyword wire key rides the sanitizer
            Expect.stringContains g.Text "[<Wire \"type\">]\n    kind: Option<string>" "keyword keys ride [<Wire>]"
            // scalars
            Expect.stringContains g.Text "replicas: Option<int>" "integer → int"
            Expect.stringContains g.Text "ratio: Option<float>" "number → float"
            Expect.stringContains g.Text "ready: Option<bool>" "boolean → bool"
            // fields are alphabetical by wire key — the determinism law
            let namePos = g.Text.IndexOf "\n    name:"
            let imagePos = g.Text.IndexOf "\n    image:"
            Expect.isTrue (imagePos < namePos && imagePos > 0) "fields sort alphabetically"
        }

        test "schema-types: refs name types, allOf flattens, arrays nest, cycles omit the field [D:schema-types]" {
            let genAs asName src =
                match Weir.Contracts.parseSchema "t" src with
                | Error e -> failtest e
                | Ok doc ->
                    Weir.SchemaTypes.moduleText
                        Weir.Parser.keywords
                        Set.empty
                        "k8s-pod"
                        asName
                        "0123456789abcdef"
                        "http://src"
                        doc

            let podSrc =
                """{ "$ref": "#/definitions/io.k8s.api.core.v1.Pod",
                       "definitions": {
                         "io.k8s.api.core.v1.Pod": {
                           "type": "object", "required": ["spec"],
                           "properties": {
                             "metadata": { "allOf": [{ "$ref": "#/definitions/io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta" }, { "description": "std" }] },
                             "spec": { "$ref": "#/definitions/io.k8s.api.core.v1.PodSpec" } } },
                         "io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta": {
                           "type": "object", "properties": { "name": { "type": "string" } } },
                         "io.k8s.api.core.v1.PodSpec": {
                           "type": "object", "required": ["containers"],
                           "properties": { "containers": { "type": "array", "items": { "$ref": "#/definitions/io.k8s.api.core.v1.Container" } } } },
                         "io.k8s.api.core.v1.Container": {
                           "type": "object", "required": ["name"],
                           "properties": { "name": { "type": "string" }, "env": { "type": "array", "items": { "type": "string" } } } } } }"""

            let g =
                match genAs None podSrc with
                | Ok g -> g
                | Error e -> failtest e

            // a $ref's definition key names the type by its last segment
            Expect.stringContains g.Text "type PodSpec = {" "def name → last dot-segment"
            Expect.stringContains g.Text "type Container = {" "container named from its def"
            // allOf of one-ref-plus-annotations flattens to the ref
            Expect.stringContains g.Text "metadata: Option<ObjectMeta>" "allOf-of-one-ref flattens; optional wraps"
            // array of refs
            Expect.stringContains g.Text "containers: seq<Container>" "required array of refs"
            Expect.stringContains g.Text "env: Option<seq<string>>" "optional scalar array"
            // the top name defaults to the root ref's stem; --as overrides
            Expect.equal g.TopType "Pod" "root ref names the top"
            Expect.stringContains g.Text "module K8sPod" "module name derives from the schema name"

            let g2 =
                match genAs (Some "Manifest") podSrc with
                | Ok g -> g
                | Error e -> failtest e

            Expect.equal g2.TopType "Manifest" "--as names the top type"
            Expect.stringContains g2.Text "type Manifest = {" "--as lands on the declaration"

            // determinism: same locked schema → byte-identical output
            let g3 =
                match genAs None podSrc with
                | Ok g -> g
                | Error e -> failtest e

            Expect.equal g3.Text g.Text "generate twice, byte-equal"

            // a cycle (JSONSchemaProps' shape) keeps the opaque posture
            let cyc =
                match
                    genAs
                        None
                        """{ "$ref": "#/definitions/Props",
                               "definitions": { "Props": { "type": "object", "properties": {
                                 "name": { "type": "string" },
                                 "items": { "type": "array", "items": { "$ref": "#/definitions/Props" } } } } } }"""
                with
                | Ok g -> g
                | Error e -> failtest e

            // a self-ref field drafts the opaque Yaml node with a note, and
            // `from json` reads the subtree whole [D:json-yaml-field]
            Expect.stringContains cyc.Text "items: Option<seq<Yaml>>" cyc.Text
            Expect.stringContains cyc.Text "name: Option<string>" "the finite fields stay"

            Expect.stringContains
                cyc.Text
                "items: refers back to its own definition — drafted Yaml"
                "the cycle note fires"

            let ds =
                diagsIn
                    "h19.weir"
                    ((cyc.Text.Split '\n'
                      |> List.ofArray
                      |> List.filter (fun l -> not (l.StartsWith "module ")))
                     @ [
                         "let x = [\"{\\\"name\\\":\\\"a\\\",\\\"items\\\":[{\\\"name\\\":\\\"b\\\"}]}\"] |> from json Props"
                         "print (show x.name)"
                     ])

            Expect.isEmpty
                (ds |> List.filter (fun d -> d.Severity = "error"))
                $"from json admits the generated type: {ds}"

            // a definition that is nothing but its own cycle still drafts:
            // the cycle is a Yaml node
            match
                genAs
                    None
                    """{ "$ref": "#/definitions/Loop",
                           "definitions": { "Loop": { "type": "object", "properties": {
                             "next": { "$ref": "#/definitions/Loop" } } } } }"""
            with
            | Ok g -> Expect.stringContains g.Text "next: Option<Yaml>" g.Text
            | Error e -> failtest e

            // an unrepresentable top level refuses with the located reason
            let refusal =
                match genAs None """{ "type": "string" }""" with
                | Error e -> e
                | Ok g -> failtest $"expected a refusal, got {g.Text}"

            Expect.stringContains refusal "no record to generate" "a scalar top refuses, teaching"

            let mapTop =
                match genAs None """{ "type": "object", "additionalProperties": { "type": "string" } }""" with
                | Error e -> e
                | Ok g -> failtest $"expected a refusal, got {g.Text}"

            Expect.stringContains mapTop "seq<string * string>" "an open-mapping top names the direct read spelling"
        }

        test "district mid-line #: the five cases — comment cut, quoted/hole/glued data, block bytes [D:district-hash]" {
            let src =
                asm [
                    "let d = yaml"
                    "    image: nginx:latest # pinned by ops"
                    "    quoted: \"a # b\""
                    "    glued: a#b"
                    "    joined: $(\"x # y\") # hole hash is data"
                    "    script: |"
                    "        echo hi # bytes"
                ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VUnion("YMap", Some(Weir.Eval.VSeq pairs)) ->
                        let vals =
                            pairs
                            |> Seq.map (fun p ->
                                match p with
                                | Weir.Eval.VTuple [ Weir.Eval.VStr k; Weir.Eval.VUnion("YStr", Some(Weir.Eval.VStr v)) ] ->
                                    k, v
                                | other -> failtest $"bad pair {other}")
                            |> Map.ofSeq

                        Expect.equal vals["image"] "nginx:latest" "the mid-line comment cut — YAML's rule"
                        Expect.equal vals["quoted"] "a # b" "a quoted # is data"
                        Expect.equal vals["glued"] "a#b" "no whitespace before # — part of the scalar"
                        Expect.equal vals["joined"] "x # y" "a # inside a splice hole is expression text"
                        Expect.equal vals["script"] "echo hi # bytes\n" "block content stays bytes"
                    | v -> failtest $"expected YMap, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "district block scalars: content is LITERAL — consumed before the splice/for scanners [D:block-scalars]" {
            skipOnWindows ()

            let src =
                asm [
                    "let d = yaml"
                    "    data:"
                    "        s.sh: |"
                    "            #!/bin/sh"
                    "            echo $name and $(1 + 2)"
                    ""
                    "            for x in xs"
                    "            key: value shaped"
                    "                deeper"
                ]

            match Weir.Parser.parseLine realResolver src with
            | Ok(SLet(_, e)) ->
                match typecheck env e with
                | Ok te ->
                    match Weir.Eval.eval valueEnv te with
                    | Weir.Eval.VUnion("YMap", Some(Weir.Eval.VSeq pairs)) ->
                        let text =
                            match Seq.head pairs with
                            | Weir.Eval.VTuple [ Weir.Eval.VStr "data"
                                                 Weir.Eval.VUnion("YMap", Some(Weir.Eval.VSeq inner)) ] ->
                                match Seq.head inner with
                                | Weir.Eval.VTuple [ Weir.Eval.VStr "s.sh"
                                                     Weir.Eval.VUnion("YStr", Some(Weir.Eval.VStr t)) ] -> t
                                | other -> failtest $"expected s.sh block, got {other}"
                            | other -> failtest $"expected data mapping, got {other}"

                        Expect.equal
                            text
                            "#!/bin/sh\necho $name and $(1 + 2)\n\nfor x in xs\nkey: value shaped\n    deeper\n"
                            "splice-lookalikes, for-lines, blanks, key-shapes — all bytes"
                    | v -> failtest $"expected YMap, got {v}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }

        test "fmt: a district block scalar re-anchors but its content is never re-indented [D:block-scalars]" {
            let src = [
                "let d = yaml"
                "        data:"
                "                s: |"
                "                        #!/bin/sh"
                ""
                "                        echo hi"
                ""
                "d |> to yaml |> print"
            ]

            match Weir.Fmt.formatLines src with
            | Error e -> failtestf "fmt failed: %s" e
            | Ok out ->
                let indent (s: string) = s.Length - s.TrimStart().Length
                Expect.equal (indent (out |> List.find (fun l -> l.Contains "data:"))) 4 "base re-anchors"

                Expect.equal
                    (indent (out |> List.find (fun l -> l.Contains "#!/bin/sh")))
                    20
                    "content keeps its offset from the base — never re-indented"

                Expect.isTrue (out |> List.exists (fun l -> l = "")) "blank content lines survive fmt"

                match Weir.Fmt.formatLines out with
                | Ok out2 -> Expect.equal out2 out "idempotent with a block scalar inside"
                | Error e -> failtestf "second fmt failed: %s" e
        }

        test "from can be let-bound" {
            expectValue
                "let p = from jsonl JRow in [\"{\\\"name\\\": \\\"x\\\", \\\"bytes\\\": 1, \\\"readOnly\\\": false}\"] |> p |> take 1 |> map (fun f -> f.name)"
                (VSeq [ VStr "x" ])
        }
    ]

let wireUnionTests =
    // tagged unions at the wire [D:wire-unions] — the tag field picks
    // the case (internal tagging), [<Other>] is the declared open-world
    // posture, writers reinsert the tag first.
    // `declare` extends the type env only — the eval env needs the
    // constructor values beside it (the run-helper's REPL env does this
    // for inline declarations)
    let kvenv =
        valueEnv
        |> Map.add "Deployment" (VBuiltin(fun v -> VUnion("Deployment", Some v)))
        |> Map.add "Service" (VBuiltin(fun v -> VUnion("Service", Some v)))
        |> Map.add "Ping" (VUnion("Ping", None))
        |> Map.add "Unknown" (VBuiltin(fun v -> VUnion("Unknown", Some v)))
        |> Map.add "Dep" (VBuiltin(fun v -> VUnion("Dep", Some v)))

    let evalWith te input =
        match Weir.Check.typecheck te (parse input) with
        | Ok typed -> eval kvenv typed
        | Error terr -> failtest $"check failed: {formatError terr}"

    let checkErrIn te input =
        match Weir.Check.typecheck te (parse input) with
        | Error terr -> terr
        | Ok _ -> failtest "expected a type error"

    let kenv =
        env
        |> declare "type DepSpec = { replicas: int }"
        |> declare "type SvcSpec = { port: int }"
        |> declare
            "[<Tag \"kind\">] type KDoc = Deployment of DepSpec | Service of SvcSpec | Ping | [<Other>] Unknown of string"

    testList "wire unions [D:wire-unions]" [
        test "declaration laws refuse, each teaching" {
            let e1 = env |> declErr "[<Tag \"kind\">] type B1 = C1 of int"
            Expect.stringContains e1.Message "carries a declared record (or nothing) — 'C1' carries int" ""

            let e2 =
                env
                |> declare "type BadP = { kind: string }"
                |> declErr "[<Tag \"kind\">] type B2 = C2 of BadP"

            Expect.stringContains e2.Message "the tag rides the union, not the payload" ""

            let e3 =
                env |> declErr "[<Tag \"k\">] type B3 = [<Wire \"x\">] C3 | [<Wire \"x\">] D3"

            Expect.stringContains e3.Message "share the tag value 'x'" ""

            let e4 = env |> declErr "[<Tag \"k\">] type B4<'a> = C4 of int"
            Expect.stringContains e4.Message "a tagged union is monomorphic" ""

            let e5 = env |> declErr "[<Tag \"k\">] type B5 = Some of string | C5"
            Expect.stringContains e5.Message "is the wire spelling of a builtin" ""
        }
        test "json read dispatches on the tag; payload, nullary, and Other all land" {
            let v =
                evalWith
                    kenv
                    "[\"{\\\"kind\\\":\\\"Deployment\\\",\\\"replicas\\\":3}\"; \"{\\\"kind\\\":\\\"Ping\\\"}\"; \"{\\\"kind\\\":\\\"CronJob\\\"}\"] |> from jsonl KDoc"

            match forceSeq v with
            | [ VUnion("Deployment", Some(VRecord("DepSpec", [ "replicas", VInt 3L ])))
                VUnion("Ping", None)
                VUnion("Unknown", Some(VStr "CronJob")) ] -> ()
            | other -> failtest $"dispatch broke: {other}"
        }
        test "a missing tag is malformed (never Other's); an unmatched tag without Other names the cases" {
            let ex =
                Expect.throwsC (fun () -> evalWith kenv "[\"{\\\"replicas\\\":1}\"] |> from json KDoc" |> ignore) id

            Expect.stringContains ex.Message "missing tag field 'kind' (KDoc)" ""

            let closed =
                env
                |> declare "type CP = { n: int }"
                |> declare "[<Tag \"k\">] type Closed = OnlyCase of CP"

            let ex2 =
                Expect.throwsC
                    (fun () ->
                        evalWith closed "[\"{\\\"k\\\":\\\"Nope\\\",\\\"n\\\":1}\"] |> from json Closed"
                        |> ignore)
                    id

            Expect.stringContains ex2.Message "matches no case of Closed (cases: OnlyCase)" ""
        }
        test "yaml read dispatches; Other catches; missing tag teaches" {
            let v = evalWith kenv "[\"kind: Service\"; \"port: 80\"] |> from yaml KDoc"

            Expect.equal v (VUnion("Service", Some(VRecord("SvcSpec", [ "port", VInt 80L ])))) "dispatch"

            let o = evalWith kenv "[\"kind: CronJob\"; \"x: 1\"] |> from yaml KDoc"
            Expect.equal o (VUnion("Unknown", Some(VStr "CronJob"))) "the fallback carries the tag"

            let ex =
                Expect.throwsC (fun () -> evalWith kenv "[\"port: 80\"] |> from yaml KDoc" |> ignore) id

            Expect.stringContains ex.Message "missing tag field 'kind' (KDoc)" ""
        }
        test "the array document and the field/Map positions admit" {
            let n =
                evalWith
                    kenv
                    "[\"[{\\\"kind\\\":\\\"Ping\\\"},{\\\"kind\\\":\\\"Deployment\\\",\\\"replicas\\\":1}]\"] |> from json seq<KDoc> |> Seq.length"

            Expect.equal n (VInt 2L) "seq<KDoc> reads a mixed array"

            let wenv = kenv |> declare "type Wrap = { name: string; doc: KDoc }"

            let w =
                evalWith
                    wenv
                    "[\"{\\\"name\\\":\\\"x\\\",\\\"doc\\\":{\\\"kind\\\":\\\"Ping\\\"}}\"] |> from json Wrap |> _.doc"

            Expect.equal w (VUnion("Ping", None)) "a union FIELD dispatches"

            let m =
                evalWith
                    kenv
                    "[\"{\\\"a\\\":{\\\"kind\\\":\\\"Ping\\\"}}\"] |> from json Map<string, KDoc> |> Map.get \"a\""

            Expect.equal m (VUnion("Ping", None)) "a Map VALUE dispatches"
        }
        test "writers reinsert the tag FIRST; wire overrides and nullary hold; roundtrips pair" {
            Expect.equal
                (evalWith kenv "Deployment { replicas = 2 } |> to json" |> forceSeq)
                [ VStr "{\"kind\":\"Deployment\",\"replicas\":2}" ]
                "tag first, payload after"

            Expect.equal
                (evalWith kenv "Ping |> to json" |> forceSeq)
                [ VStr "{\"kind\":\"Ping\"}" ]
                "a nullary case is a tag-only document"

            Expect.equal
                (evalWith kenv "[Ping; Service { port = 1 }] |> to jsonl" |> forceSeq)
                [ VStr "{\"kind\":\"Ping\"}"; VStr "{\"kind\":\"Service\",\"port\":1}" ]
                "mixed NDJSON writes"

            Expect.equal
                (evalWith kenv "Service { port = 443 } |> to yaml" |> forceSeq)
                [ VStr "kind: Service"; VStr "port: 443" ]
                "yaml: the tag entry leads"

            let wire =
                env
                |> declare "type WP = { n: int }"
                |> declare "[<Tag \"kind\">] type KW = [<Wire \"apps/v1.Dep\">] Dep of WP"

            Expect.equal
                (evalWith wire "Dep { n = 1 } |> to json" |> forceSeq)
                [ VStr "{\"kind\":\"apps/v1.Dep\",\"n\":1}" ]
                "the [<Wire>] value writes"

            Expect.equal
                (evalWith
                    kenv
                    "(Deployment { replicas = 5 } |> to json |> from json KDoc) == Deployment { replicas = 5 }")
                (VBool true)
                "to json |> from json KDoc is identity"
        }
        test "the [<Other>] value refuses to WRITE — nothing faithful" {
            let ex =
                Expect.throwsC (fun () -> evalWith kenv "Unknown \"CronJob\" |> to json" |> forceSeq |> ignore) id

            Expect.stringContains ex.Message "nothing faithful can be written" ""
        }
        // ---- session S [D:wire-unions]: the stream cardinality ----
        test "from yaml stream reads N documents, each as T — the bundle is stream over the union" {
            let mixed =
                evalWith
                    kenv
                    "[\"kind: Deployment\"; \"replicas: 3\"; \"---\"; \"kind: Service\"; \"port: 80\"; \"---\"; \"kind: X\"] |> from yaml stream KDoc"

            match forceSeq mixed with
            | [ VUnion("Deployment", Some _); VUnion("Service", Some _); VUnion("Unknown", Some(VStr "X")) ] -> ()
            | other -> failtest $"the bundle broke: {other}"

            Expect.equal
                (evalWith kenv "[\"kind: Ping\"] |> from yaml stream KDoc |> Seq.length")
                (VInt 1L)
                "one document is a one-element stream"

            Expect.equal
                (evalWith kenv "[\"x\"] |> Seq.where (fun _ -> false) |> from yaml stream KDoc |> Seq.length")
                (VInt 0L)
                "an empty stream is zero documents, not an error"
        }
        test "stream composes with records and seq-documents; the write roundtrips" {
            let henv = env |> declare "type SRow = { a: int }"

            Expect.equal
                (evalWith henv "[\"a: 1\"; \"---\"; \"a: 2\"] |> from yaml stream SRow |> Seq.length")
                (VInt 2L)
                "a homogeneous stream is legal (the [D:yaml-seq] clause U falsified)"

            Expect.equal
                (evalWith henv "([{ a = 1 }; { a = 2 }] |> to yaml stream |> from yaml stream SRow) |> Seq.length")
                (VInt 2L)
                "to yaml stream's write reads back through the stream read"

            Expect.equal
                (evalWith
                    henv
                    "[\"- a: 1\"; \"- a: 2\"; \"---\"; \"- a: 3\"] |> from yaml stream seq<SRow> |> Seq.length")
                (VInt 2L)
                "stream seq<T>: each document is a sequence document"
        }
        test "to yaml writes ONE document: a seq is a SEQUENCE document; the stream takes the word [D:yaml-seq-doc]" {
            Expect.equal
                (run "[1; 2; 3] |> to yaml" |> forceSeq)
                [ VStr "- 1"; VStr "- 2"; VStr "- 3" ]
                "a scalar seq is one sequence document, not a stream"

            let henv = env |> declare "type YR = { a: int }"

            Expect.equal
                (evalWith henv "[{ a = 1 }; { a = 2 }] |> to yaml" |> forceSeq)
                [ VStr "- a: 1"; VStr "- a: 2" ]
                "a record seq is one sequence document"

            Expect.equal
                (evalWith henv "([{ a = 1 }; { a = 2 }] |> to yaml |> from yaml seq<YR>) |> Seq.length")
                (VInt 2L)
                "to yaml |> from yaml seq<T> — the pairing that was crossed until now"

            Expect.equal
                (evalWith henv "[{ a = 9 }] |> to yaml stream" |> forceSeq)
                [ VStr "a: 9" ]
                "the stream word writes documents (a one-element stream has no separator)"

            Expect.equal
                (run "[(\"k\", \"v\")] |> to yaml" |> forceSeq)
                [ VStr "k: v" ]
                "a pair-seq stays ONE mapping document"

            Expect.stringContains
                (checkErrIn henv "{ a = 1 } |> to yaml stream").Message
                "'to yaml stream' needs a seq (a document per element), got YR — one document is 'to yaml'"
                ""

            expectParse "xs |> to yaml stream" "(xs |> (to yaml stream))"
        }
        test "the stream fences and the re-pointed teaching" {
            Expect.stringContains
                (checkErr "[\"x\"] |> from json stream JRow").Message
                "'from json stream' does not exist — NDJSON is 'from jsonl JRow'"
                ""

            Expect.stringContains
                (checkErr "[\"x\"] |> from yaml stream Map<string, JRow>").Message
                "Map< > has no place in the stream slot"
                ""

            Expect.stringContains
                (checkErr "[1] |> to json stream").Message
                "'to json stream' does not exist — one document per element is 'to jsonl'"
                ""

            let henv = env |> declare "type SR2 = { a: int }"

            let ex =
                Expect.throwsC
                    (fun () ->
                        evalWith henv "[\"a: 1\"; \"---\"; \"a: 2\"] |> from yaml SR2"
                        |> forceSeq
                        |> ignore)
                    id

            Expect.stringContains ex.Message "read a stream with 'from yaml stream T'" "the teaching re-points"
        }
        test "two reachable tagged unions sharing a case name refuse at the write site" {
            // `Shared` is ambiguous at bare use [D:ambiguous-ctor], so the
            // colliding value arrives via a read — the closure still
            // reaches both unions, and the writer cannot key the case
            let e2 =
                env
                |> declare "type P1 = { a: int }"
                |> declare "[<Tag \"k\">] type U1 = Shared of P1"
                |> declare "[<Tag \"k\">] type UU2 = [<Wire \"other\">] Shared2 of P1 | Shared"
                |> declare "type Both = { x: U1; y: UU2 }"

            let terr =
                match
                    Weir.Check.typecheck
                        e2
                        (parse
                            "let x = [\"{\\\"k\\\":\\\"Shared\\\",\\\"a\\\":1}\"] |> from json U1 in ({ x = x; y = Shared2 { a = 2 } } |> to json)")
                with
                | Error terr -> terr
                | Ok _ -> failtest "a shared case name across reachable unions must refuse"

            Expect.stringContains terr.Message "belongs to tagged unions 'U1' and 'UU2'" ""
        }
    ]

let boundaryCheckTests =
    testList "Boundary check errors" [
        test "from json needs a record name" {
            Expect.stringContains (checkErr "[\"x\"] |> from json").Message "needs a record name" ""
        }
        test "from json rejects unknown records" {
            Expect.stringContains (checkErr "[\"x\"] |> from json Missing").Message "unknown type 'Missing'" ""
        }
        test "from json rejects unions" {
            Expect.stringContains (checkErr "[\"x\"] |> from json Job").Message "is an untagged union" ""
        }
        test "unknown format is rejected" {
            Expect.stringContains (checkErr "[\"x\"] |> from ini").Message "unknown format 'ini'" ""
        }
        test "from yaml needs a record name [D:yaml-v1]" {
            Expect.stringContains (checkErr "[\"x\"] |> from yaml").Message "'from yaml' needs a record name" ""
        }
        test "piping a non-string seq into from is rejected" {
            Expect.stringContains
                (checkErr "(Seq.initInfinite id) |> from json JRow").Message
                "expected string, got int"
                ""
        }
        test "to json on a union seq is rejected" {
            let e =
                "let xs = (Seq.initInfinite id) |> map (fun n -> Running n) in xs |> to json"

            Expect.stringContains (checkErr e).Message "is an untagged union" ""
        }
        test "to json standalone is rejected" { Expect.stringContains (checkErr "to json").Message "pipe stage" "" }
        test "to json/yaml on a generic value teaches where to call it, not the type variable" {
            for fmt in [ "json"; "yaml" ] do
                let m = (checkErr $"let dump x = x |> to {fmt} in dump 1").Message
                Expect.stringContains m $"'to {fmt}' needs a concrete type" fmt
                Expect.stringContains m "where the type is known" fmt
        }
    ]

let wireKeyTests =
    // [<Wire "…">] — reserved words and illegal identifiers as adapter
    // keys [D:wire-keys]
    let mustSay = mustSayIn "wk.weir"

    let clean = cleanIn "wk.weir"

    testList "wire keys" [
        test "the keyword teaching names the repair" {
            mustSay [ "type B = { type: string }" ] "[<Wire" "the attribute is the named fix"
        }
        test "declaration with Wire checks clean; json and yaml admission hold" {
            clean
                [
                    "type B = { [<Wire \"type\">] kind: string }"
                    "let b = [\"{}\"] |> from json B"
                    "print b.kind"
                ]
                "json admission"
        }
        test "wire collisions refuse at the declaration, naming both fields" {
            mustSay [ "type C = { [<Wire \"x\">] a: string; x: string }" ] "share the wire key 'x'" "attr-vs-plain"

            mustSay
                [ "type D = { [<Wire \"k\">] a: string; [<Wire \"k\">] b: string }" ]
                "share the wire key 'k'"
                "attr-vs-attr"
        }
        test "Wire demands a nonempty string; unknown attrs did-you-mean it" {
            mustSay [ "type E = { [<Wire>] kind: string }" ] "expects the wire key as a string" "no arg"

            mustSay
                [ "type G = { [<Wire \"\">] kind: string }" ]
                "'Wire' refuses an empty wire key — name the key the field reads and writes, e.g. [<Wire \"type\">] kind: string"
                "an empty key is its own refusal"

            mustSay [ "type F = { [<Wier \"x\">] kind: string }" ] "Wire" "did-you-mean reaches it"
        }
    ]

let secretTests =
    // Secret [D:secret] — a marker the renderers respect; the coverage is
    // the feature, so every rendering site is pinned, the containing-record
    // case first (the likeliest miss a naive implementation makes)
    let evalWith te input =
        match Weir.Check.typecheck te (parse input) with
        | Ok typed -> eval valueEnv typed
        | Error terr -> failtest $"check failed: {formatError terr}"

    let checkErrIn te input =
        match Weir.Check.typecheck te (parse input) with
        | Error terr -> terr
        | Ok _ -> failtest "expected a type error"

    testList "Secret [D:secret]" [
        test "the CONTAINING-RECORD case: a Secret field inside a shown record renders *** (the load-bearing pin)" {
            let e = env |> declare "type Cfg = { user: string; token: Secret }"
            let v = evalWith e "{ user = \"admin\"; token = Secret.of \"hunter\" }"

            Expect.equal
                (Weir.Eval.formatValue v)
                "{ user = \"admin\"; token = *** }"
                "the secret field is ***, the public is not"
        }
        test "show of a bare Secret, and inside seq/tuple, all render ***" {
            Expect.equal (run "show (Secret.of \"x\")") (VStr "***") "bare"
            Expect.equal (run "show [Secret.of \"a\"; Secret.of \"b\"]") (VStr "[***; ***]") "seq"
            Expect.equal (run "show (Secret.of \"a\", \"pub\")") (VStr "(***, \"pub\")") "tuple"
        }
        test "a Secret spliced into argv is clear for the child, *** in a rendered command line [D:secret-argv-mask]" {
            let word = Weir.Eval.scalarString "command argument" (VSecret "unit-argv-tok")
            Expect.equal word "unit-argv-tok" "the child receives the value"

            Expect.equal
                (Weir.Proc.showArgv [ "curl"; "-H"; "unit-argv-tok"; "https://x" ])
                "curl -H *** https://x"
                "the message masks the spliced word, nothing else"

            Expect.equal
                (Weir.Proc.showArgv [ "echo"; "unit-argv-tok-longer" ])
                "echo unit-argv-tok-longer"
                "a word is masked only when it IS the secret's text"
        }
        test "reveal is the one exit; map keeps the derived value secret" {
            Expect.equal (run "Secret.reveal (Secret.of \"hunter\")") (VStr "hunter") "reveal returns the plain value"

            Expect.equal
                (run "Secret.reveal (Secret.map (fun t -> \"Bearer \" + t) (Secret.of \"xyz\"))")
                (VStr "Bearer xyz")
                "map transforms under the wrapper"

            Expect.equal
                (run "show (Secret.map (fun t -> t) (Secret.of \"x\"))")
                (VStr "***")
                "the map result is still secret"
        }
        test
            "a raise inside Secret.map withholds the message, keeping the kind and the builtin's name [D:secret-map-withheld]" {
            let errOf (code: string) =
                match run (code + " |> try result") with
                | VUnion("Error", Some(VStr m)) -> m
                | other -> failtest $"expected an Error, got {other}"

            let b64 = errOf "Secret.map Str.fromBase64 (Secret.of \"S3CRET!\")"
            Expect.stringStarts b64 "fromBase64: failed inside Secret.map" b64
            Expect.stringContains b64 "the message is withheld" b64
            Expect.isFalse (b64.Contains "S3CRET") b64

            let toInt = errOf "Secret.map (fun t -> show (Str.toInt t)) (Secret.of \"S3CRET\")"
            Expect.stringStarts toInt "toInt: failed inside Secret.map" toInt
            Expect.isFalse (toInt.Contains "S3CRET") toInt

            let hex =
                errOf "Secret.map (fun t -> Bytes.toBase64 (Bytes.fromHex t)) (Secret.of \"S3CRET\")"

            Expect.stringStarts hex "Bytes.fromHex: failed inside Secret.map" hex

            // a lead that is not a builtin's name is not kept: it may be the secret
            let own = errOf "Secret.map (fun t -> fail $\"{t}: bad\") (Secret.of \"S3CRET\")"
            Expect.stringStarts own "Secret.map: the function raised" own
            Expect.isFalse (own.Contains "S3CRET") own

            // the kind survives: still an ordinary failure
            try
                run "Secret.map Str.fromBase64 (Secret.of \"S3CRET!\")" |> ignore
                failtest "expected a raise"
            with
            | Failure m -> Expect.isFalse (m.Contains "S3CRET") m
            | ex -> failtest $"expected a Failure, got {ex.GetType().Name}"
        }
        test "interpolation REFUSES, naming reveal (a secret must not reach a log line)" {
            let m = (checkErr "let s = Secret.of \"x\" in $\"tok: {s}\"").Message
            Expect.stringContains m "does not interpolate" "refused"
            Expect.stringContains m "Secret.reveal" "names the exit"
        }
        test "the wire boundaries REFUSE, naming reveal" {
            let e = env |> declare "type Cfg = { token: Secret }"
            let j = (checkErrIn e "[{ token = Secret.of \"x\" }] |> to json").Message
            Expect.stringContains j "must not cross to JSON" ""
            Expect.stringContains j "Secret.reveal" "names the exit"
            let y = (checkErrIn e "{ token = Secret.of \"x\" } |> to yaml").Message
            Expect.stringContains y "must not cross to yaml" ""
        }
        test "reading INTO a Secret is admitted; the value stays secret [D:secret-read]" {
            let doc = "[\"{\\\"token\\\": \\\"s3\\\", \\\"alt\\\": null}\"]"
            let read = doc + " |> from json {| token: Secret; alt: Option<Secret> |}"
            Expect.equal (run (read + " |> _.token |> Secret.reveal")) (VStr "s3") "json wraps on the way in"
            Expect.equal (run (read + " |> _.token |> show")) (VStr "***") "and shows as ***"
            Expect.equal (run (read + " |> _.alt |> (fun o -> o == None)")) (VBool true) "Option<Secret> reads null"

            Expect.equal
                (run "[\"token: y3\"] |> from yaml {| token: Secret |} |> _.token |> Secret.reveal")
                (VStr "y3")
                "yaml wraps on the way in"
        }
        test "a from json/jsonl/toml error withholds the excerpt when the type holds a Secret [D:from-secret-excerpt]" {
            let errOf (code: string) =
                match run (code + " |> try result") with
                | VUnion("Error", Some(VStr m)) -> m
                | other -> failtest $"expected an Error, got {other}"

            let withheld (m: string) (what: string) =
                Expect.stringContains m "<excerpt withheld: the type holds a Secret>" $"{what}: says it is withheld"
                Expect.isFalse (m.Contains "S3CRET") $"{what}: no secret in the message: {m}"

            let doc = "[\"{\\\"token\\\": \\\"S3CRET\\\", \\\"ttl\\\": \\\"3600\\\"}\"]"
            withheld (errOf (doc + " |> from json {| token: Secret; ttl: int |}")) "a field mismatch"
            withheld (errOf (doc + " |> from jsonl {| token: Secret; ttl: int |}")) "jsonl"

            withheld (errOf "[\"{\\\"token\\\": \\\"S3CRET\\\"\"] |> from json {| token: Secret |}") "malformed JSON"

            withheld
                (errOf (doc + " |> from json {| ttl: int; inner: Option<seq<{| key: Secret |}>> |}"))
                "a Secret at depth"

            // no Secret in the type: the excerpt is cited as before
            Expect.stringContains
                (errOf (doc + " |> from json {| token: string; ttl: int |}"))
                "in: {\"token\": \"S3CRET\", \"ttl\": \"3600\"}"
                "no Secret, the excerpt stays"

            // a toml parse detail quotes the document; the line survives
            let t = errOf "[\"token = S3CRET\"] |> from toml {| token: Secret |}"

            Expect.stringContains
                t
                "from toml: line 1: not a valid TOML document (detail withheld: the type holds a Secret)"
                t

            Expect.isFalse (t.Contains "S3CRET") t

            Expect.stringContains
                (errOf "[\"token = S3CRET\"] |> from toml {| token: string |}")
                "'S3CRET' is not a TOML value"
                "no Secret, the toml detail stays"
        }
        test "print refuses a Secret (show s -> ***), and Secret is not arithmetic" {
            Expect.stringContains (checkErr "print (Secret.of \"x\")").Message "will not render a Secret" ""

            Expect.stringContains
                (checkErr "Secret.of \"x\" + Secret.of \"y\"").Message
                "'+' is not defined for Secret"
                ""
        }
        test "Eq compares two secrets; Ord refuses (sorting secrets is meaningless)" {
            Expect.equal (run "show (Secret.of \"x\" == Secret.of \"x\")") (VStr "true") "equal"
            Expect.equal (run "show (Secret.of \"x\" == Secret.of \"y\")") (VStr "false") "unequal"

            Expect.stringContains
                (checkErr "[Secret.of \"x\"] |> Seq.sortBy (fun s -> s) |> Seq.freeze").Message
                "cannot be ordered"
                "Ord refused"
        }
        test "the argv splice is ALLOWED — a Secret reaches argv in the clear (the ruling)" {
            Expect.equal (Weir.Eval.scalarString "command argument" (VSecret "tok")) "tok" "argv gets the raw value"
        }
        test "a reified command and an env prefix take a Secret too — the argv law, not seq<string>" {
            skipOnWindows ()
            let clean = errorsCleanIn "secret-argv.weir"
            let s = "let s = Secret.of \"tok\""
            clean [ s; "let r = echo $s | complete"; "print (r.stdout |> Str.join \"\")" ] "| complete"

            clean
                [
                    s
                    "let e = Env.ofPairs [(\"A\", \"1\")]"
                    "let l = $e(echo $s | line)"
                    "print l"
                ]
                "$e( )"

            clean [ s; "A=$s sh -c \"echo $A\"" ] "NAME=$s cmd"
            clean [ s; "within env A=$s"; "    sh -c \"echo $A\"" ] "within env A=$s"
            clean [ "let u = Uuid.nil"; "let n = 3"; "let r = echo $u $n | text"; "print r" ] "mixed argv words"

            mustSayIn
                "secret-argv.weir"
                [ "let f = 1.5"; "let r = echo $f | line"; "print r" ]
                "command arguments must be"
                "float refused"

            mustSayIn
                "secret-argv.weir"
                [ "let d = 3s"; "let r = echo $d | line"; "print r" ]
                "Duration.toMillis"
                "Duration teaches"

            mustSayIn
                "secret-argv.weir"
                [ "let p = Env.pair \"A\" (Secret.of \"x\")"; "print (show p)" ]
                "Secret"
                "Env.pair stays string-only"

            // the words reach the child as the bare command's would
            let path = tempPath "weir-secret-argv" + ".weir"

            System.IO.File.WriteAllLines(
                path,
                [
                    s
                    "let a = echo $s | line"
                    "let b = A=$s sh -c \"echo $A\" | line"
                    "let e = Env.ofPairs [(\"B\", \"1\")]"
                    "let u = Uuid.nil"
                    "let c = $e(echo $s $u | text)"
                    "if a <> \"tok\" || b <> \"tok\" || c <> \"tok 00000000-0000-0000-0000-000000000000\" then"
                    "    exit 3"
                ]
            )

            try
                Expect.equal (Weir.Script.run path []) 0 "every reified form spliced the clear value"
            finally
                System.IO.File.Delete path
        }
    ]

let httpTests =
    // Http [D:http] — the typed request boundary. The network behavior
    // (mangling round-trip, status-is-data, transport-raises, auth-reaches,
    // pmap) is pinned offline in e2e against a local server; here the pure
    // and render-level guarantees, the show-masking foremost.
    let evalWith te input =
        match Weir.Check.typecheck te (parse input) with
        | Ok typed -> eval valueEnv typed
        | Error terr -> failtest $"check failed: {formatError terr}"

    let strOf =
        function
        | VStr s -> s
        | v -> failtest $"not a string: {v}"

    // a raw one-response-per-connection server on a free loopback port:
    // `answer` maps the request head to the exact response bytes, so a
    // test controls repeated headers and charsets byte-for-byte; the
    // request heads land in the returned list
    let rawServe (answer: string -> string) : int * System.Collections.Concurrent.ConcurrentQueue<string> =
        let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        l.Start()
        let seen = System.Collections.Concurrent.ConcurrentQueue<string>()

        let loop () =
            try
                while true do
                    use c = l.AcceptTcpClient()
                    use st = c.GetStream()
                    let buf = Array.zeroCreate<byte> 65536
                    let sb = System.Text.StringBuilder()

                    while not (sb.ToString().Contains "\r\n\r\n") do
                        let n = st.Read(buf, 0, buf.Length)

                        if n = 0 then
                            failwith "eof"

                        sb.Append(System.Text.Encoding.Latin1.GetString(buf, 0, n)) |> ignore

                    let head = sb.ToString()
                    seen.Enqueue head
                    let bytes = System.Text.Encoding.Latin1.GetBytes(answer head)
                    st.Write(bytes, 0, bytes.Length)
            with _ ->
                ()

        let t = System.Threading.Thread(loop)
        t.IsBackground <- true
        t.Start()
        (l.LocalEndpoint :?> System.Net.IPEndPoint).Port, seen

    testList "Http [D:http]" [
        test
            "the response body reads under weir's line law [D:http-body-lines]: no trailing empty, CRLF normalized, empty is empty" {
            let lines s = Weir.Builtins.bodyLines s
            Expect.equal (lines "v0.0.33\n") [ "v0.0.33" ] "trailing newline is a terminator, not a separator"
            Expect.equal (lines "v0.0.33") [ "v0.0.33" ] "no trailing newline, same one element"
            Expect.equal (lines "a\r\nb\r\n") [ "a"; "b" ] "CRLF terminators strip whole"
            Expect.equal (lines "a\n\nb\n") [ "a"; ""; "b" ] "interior blank survives"
            Expect.equal (lines "") [] "empty body is empty"
        }
        test "show of a request MASKS its Secret — auth union AND secret headers (formatWith recursion, confirmed)" {
            let r =
                run
                    "{ Http.defaults with auth = Bearer (Secret.of \"tok\"); secretHeaders = [(\"X-API-Key\", Secret.of \"k\")] }"

            let s = Weir.Eval.formatValue r
            Expect.stringContains s "Bearer (***)" "the auth token is masked"
            Expect.stringContains s "(\"X-API-Key\", ***)" "the secret header value is masked"
            Expect.isFalse (s.Contains "tok") "the raw token never renders"
        }
        test "Http.defaults is the template: Get, empty url, NoAuth, NoBody, 30s" {
            Expect.equal (run "Http.defaults.method") (VUnion("Get", None)) "method"
            Expect.equal (run "Http.defaults.url") (VStr "") "url"
            Expect.equal (run "Http.defaults.auth") (VUnion("NoAuth", None)) "auth"
            Expect.equal (run "Http.defaults.body") (VUnion("NoBody", None)) "body"
            Expect.equal (run "Http.defaults.timeout") (VDur 30000L) "the mandatory 30s default"
            Expect.equal (run "Http.defaults.insecure") (VBool false) "TLS verification ON by default"
        }
        test "insecure is a LOUD per-request field, off by default [D:http-s2]" {
            Expect.equal
                (run "{ Http.get \"u\" with insecure = true }.insecure")
                (VBool true)
                "opt-in, visible at the call site"

            Expect.equal (run "(Http.get \"u\").insecure") (VBool false) "a constructor is secure by default"
        }
        test "File.write/append emit LF bytes on every platform [D:lf-output]" {
            // a written file is data (hashes, sigs, diffs) — the
            // content-bytes input ruling's dual
            let p =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-lf-{System.Environment.ProcessId}.txt")

            try
                run $"[\"a\"; \"b\"] |> File.write \"{p.Replace('\\', '/')}\"" |> ignore
                run $"[\"c\"] |> File.append \"{p.Replace('\\', '/')}\"" |> ignore
                let bytes = System.IO.File.ReadAllBytes p
                Expect.isFalse (bytes |> Array.contains 13uy) "no CR byte anywhere"
                Expect.equal (System.IO.File.ReadAllText p) "a\nb\nc\n" "LF-joined, trailing newline"
            finally
                System.IO.File.Delete p
        }
        test "a timeout past Int32.MaxValue ms is honoured, not refused [D:long-waits]" {
            // 30 days > the 24.8-day timer ceiling: the send runs and fails
            // on the transport (nothing listens on port 9), not the timeout
            let ex =
                Expect.throwsC
                    (fun () ->
                        run
                            "Http.send { Http.defaults with url = \"http://127.0.0.1:9/\"; timeout = 720h } |> _.status"
                        |> ignore)
                    id

            Expect.stringContains ex.Message "refused" ex.Message

            let zero =
                Expect.throwsC
                    (fun () ->
                        run "Http.send { Http.defaults with url = \"http://127.0.0.1:9/\"; timeout = 0s }"
                        |> ignore)
                    id

            Expect.stringContains zero.Message "the timeout must be positive" ""

            // the sliced deadline re-arms past each slice and fires at the total
            use cts = new System.Threading.CancellationTokenSource()
            let sw = System.Diagnostics.Stopwatch.StartNew()
            Weir.Http.cancelAfterSliced 40L cts 150L
            Expect.isTrue (cts.Token.WaitHandle.WaitOne 5000) "the deadline fires"
            Expect.isGreaterThanOrEqual sw.ElapsedMilliseconds 140L "not at the first slice"
        }
        test "Basic auth is base64(user:pass) — an ENCODING the runner does, not a caller" {
            Expect.equal (Weir.Http.basicToken "alice" "s3cr3t") "YWxpY2U6czNjcjN0" ""
        }
        test "transport classifier: type-driven, all four probed shapes [D:transport-words]" {
            let classify ms ex = Weir.Http.classifyTransport ms 443 ex

            // the shapes as HttpClient nests them (probed): Aggregate →
            // HttpRequestException → the discriminating leaf
            let nested (leaf: exn) =
                System.AggregateException(System.Net.Http.HttpRequestException("outer", leaf)) :> exn

            Expect.equal
                (classify 30000 (nested (System.Threading.Tasks.TaskCanceledException())))
                (Weir.Http.Timeout 30000L)
                "cancellation IS timeout — weir passes no tokens"

            Expect.equal
                (classify
                    30000
                    (nested (System.Net.Sockets.SocketException(int System.Net.Sockets.SocketError.HostNotFound))))
                Weir.Http.NoSuchHost
                "DNS by SocketErrorCode, not message text"

            Expect.equal
                (classify
                    30000
                    (nested (System.Net.Sockets.SocketException(int System.Net.Sockets.SocketError.ConnectionRefused))))
                (Weir.Http.Refused 443)
                "refused carries the port"

            Expect.equal
                (classify 30000 (nested (System.Security.Authentication.AuthenticationException "chain")))
                Weir.Http.TlsUntrusted
                "TLS by AuthenticationException"

            match classify 30000 (nested (IOException "wire dropped")) with
            | Weir.Http.OtherTransport root -> Expect.equal root "wire dropped" "the residual keeps the ROOT message"
            | e -> failtest $"unclassified shape must fall to OtherTransport, got {e}"
        }
        test "transport messages: each case its own words, the repair nameable" {
            let msg = Weir.Http.transportMessage "weir.sh"
            Expect.equal (msg (Weir.Http.Timeout 30000L)) "timed out after 30s reaching weir.sh" "whole seconds"

            Expect.equal (msg (Weir.Http.Timeout 1500L)) "timed out after 1500ms reaching weir.sh" "sub-second stays ms"

            Expect.equal (msg Weir.Http.NoSuchHost) "cannot resolve weir.sh — no such host" ""

            Expect.equal
                (msg (Weir.Http.Refused 8443))
                "weir.sh:8443 refused the connection — nothing is listening there"
                ""

            Expect.stringContains (msg Weir.Http.TlsUntrusted) "certificate is not trusted" ""

            Expect.equal
                (msg (Weir.Http.OtherTransport "boom"))
                "cannot reach weir.sh — the request failed before any response arrived: boom"
                "the umbrella survives for the residual, framed by the host and what weir knows"

            Expect.equal
                (msg Weir.Http.TlsHandshake)
                "cannot establish TLS with weir.sh — the server did not speak TLS on this port (a plain http:// endpoint, or one offering no TLS version weir accepts); the certificate was never checked"
                "a broken handshake is not a trust verdict [D:tls-handshake-vs-trust]"

            Expect.equal
                (msg Weir.Http.InvalidResponse)
                "weir.sh answered with something that is not HTTP — its status line could not be read"
                "a garbage status line in weir's words"
        }
        test "a URL's userinfo is Basic auth, each part percent-decoded to its bytes [D:url-redact]" {
            let b64 (s: byte[]) =
                "Basic " + System.Convert.ToBase64String s

            let utf8 (s: string) = System.Text.Encoding.UTF8.GetBytes s

            Expect.equal
                (Weir.Http.userinfoAuth "http://bob:S3cr3t@127.0.0.1:1/echo")
                (Some("Authorization", "Basic Ym9iOlMzY3IzdA=="))
                "curl's header for bob:S3cr3t"

            Expect.equal
                (Weir.Http.userinfoAuth "http://b%40b:p%3Ass@h/")
                (Some("Authorization", b64 (utf8 "b@b:p:ss")))
                "an escaped @ or : stays in its part"

            Expect.equal
                (Weir.Http.userinfoAuth "http://tok@h/")
                (Some("Authorization", b64 (utf8 "tok:")))
                "no password: empty, as curl sends it"

            Expect.equal
                (Weir.Http.userinfoAuth "http://u:%FF@h/")
                (Some("Authorization", b64 (Array.append (utf8 "u:") [| 0xFFuy |])))
                "an escape is the byte it names, not UTF-8 text"

            Expect.equal (Weir.Http.userinfoAuth "http://h/p@q") None "no userinfo, no header"
            Expect.equal (Weir.Http.userinfoAuth "not a url") None "unparseable, no header"
        }
        test "redactUrl masks userinfo, leaves a credential-free URL whole [D:url-redact]" {
            // the leak: a credential in user:pass@host would print verbatim
            Expect.equal
                (Weir.Http.redactUrl "http://user:s3cr3t@host:8080/p?q=1")
                "http://***@host:8080/p?q=1"
                "the whole user:pass span becomes ***, host and path kept"

            Expect.equal
                (Weir.Http.redactUrl "https://tok@api.example.com/v1")
                "https://***@api.example.com/v1"
                "a userinfo with no colon is redacted too"

            // a credential-free URL is returned unchanged — the target is
            // still named in full
            Expect.equal
                (Weir.Http.redactUrl "http://127.0.0.1:8792/x")
                "http://127.0.0.1:8792/x"
                "no userinfo: the URL is unchanged"

            // an '@' in the path/query is not a userinfo separator
            Expect.equal
                (Weir.Http.redactUrl "https://host/path@v2?to=a@b")
                "https://host/path@v2?to=a@b"
                "an @ after the authority is data, not a credential"

            // purely textual — it redacts a URL Uri could not parse (the
            // transport fallback's exact case)
            Expect.equal
                (Weir.Http.redactUrl "http://user:pw@ nohost")
                "http://***@ nohost"
                "an unparseable URL still gets its userinfo masked"

            // a schemeless string has no authority — left alone
            Expect.equal (Weir.Http.redactUrl "not-a-url") "not-a-url" "no :// — unchanged"
        }
        test "the wider raw-leak sweep's finds stay closed [D:transport-words]" {
            // each of these leaked a naked .NET message before the sweep
            let durOver = msgOf "Duration.parse \"99999999999999999999999s\""
            Expect.stringContains durOver "beyond the 64-bit millisecond range" "overflow in weir's words"
            Expect.isFalse (durOver.Contains "Int64") "never Int64.Parse's text"

            // 9999999999GiB fits Int64.Parse but wraps the multiply —
            // the silent-wrap case the checked ops now catch
            let sizeWrap = msgOf "Size.parse \"9999999999GiB\""
            Expect.stringContains sizeWrap "beyond the 64-bit byte range" "checked multiply, own words"

            let envMissing =
                msgOf "Env.fromFile \"/definitely/not/here.env\" |> Seq.iter (fun v -> print v.name)"

            Expect.stringContains envMissing "Env.fromFile: no such file:" "the File-family guard wording"
            Expect.isFalse (envMissing.Contains "Could not find") "never FileNotFoundException's text"
        }
        test "excerpt bounds a huge invalid input; a short one is quoted whole [D:excerpt]" {
            // the helper in isolation: the length is named, the head kept
            Expect.equal (Weir.Types.excerpt "abc") "abc" "<= 64 chars: unchanged"

            Expect.equal
                (Weir.Types.excerpt (String.replicate 64 "x"))
                (String.replicate 64 "x")
                "exactly 64: unchanged"

            let long = Weir.Types.excerpt (String.replicate 200000 "z")
            Expect.stringContains long "(200000 chars)" "the TRUE length is named"
            Expect.isTrue (long.Length < 120) "the rendered excerpt is bounded, not the input"
        }
        test "a multi-MB invalid parse/decode input yields a BOUNDED error [D:excerpt]" {
            // the flood: a 2MB invalid input embedded whole was ~2MB of
            // stderr — now bounded to a head + the length
            let big = String.replicate 2_000_000 "!"

            for src in
                [
                    $"Str.toInt \"{big}\""
                    $"Str.fromBase64 \"{big}\""
                    $"Bytes.fromBase64 \"{big}\""
                    $"Bytes.fromHex \"{big}\""
                    $"Duration.parse \"{big}\""
                    $"Size.parse \"{big}\""
                    $"Float.parse \"{big}\""
                    $"Instant.parse \"{big}\""
                ] do
                let m = msgOf src
                Expect.isTrue (m.Length < 200) $"error stays under ~200 bytes for {src[..12]}… (was {m.Length})"
                Expect.stringContains m "(2000000 chars)" "the true length is named"
                Expect.isFalse (m.Contains(String.replicate 200 "!")) "the whole input is NOT echoed"

            // a short invalid input is still quoted in full, unchanged
            let shortMsg = msgOf "Str.toInt \"notanum\""
            Expect.stringContains shortMsg "\"notanum\"" "a short input stays fully readable"
        }
        test "a request in a string slot names its repair [D:fetch-naming] [D:fetch-retired]" {
            // a built request where a URL string is expected: the type
            // error names the two run spellings
            let m = (checkErr "Http.get \"u\" |> Str.trim").Message
            Expect.stringContains m "expected string, got HttpRequest" "the mismatch"
            Expect.stringContains m "runs through Http.expect (raising) or Http.send" "the repair names both postures"

            // the correct pipeline spelling stays silent
            checkOk "Http.get \"u\" |> Http.send" |> ignore
        }
        test "a Secret does not interpolate into a url — the draft's Bearer spelling is now illegal" {
            let m = (checkErr "let t = Secret.of \"x\" in $\"Bearer {t}\"").Message
            Expect.stringContains m "does not interpolate" "the auth union carries the Secret whole instead"
        }
        test "the request body carries pre-serialized `to json` lines (no generic, no Jsonable class)" {
            // RawJson is a content-type tag over seq<string>; the caller's
            // `to json` is the typed serialization [D:http]
            let e = env |> declare "type P = { name: string }"

            let v =
                evalWith e "{ Http.defaults with body = RawJson ({ name = \"a\" } |> to json) }"

            match v with
            | VRecord("HttpRequest", f) ->
                match Weir.Eval.recGet "body" f with
                | VUnion("RawJson", Some(VSeq lines)) ->
                    Expect.equal (lines |> Seq.map strOf |> List.ofSeq) [ "{\"name\":\"a\"}" ] "one document line"
                | v -> failtest $"body not RawJson: {v}"
            | v -> failtest $"not a request: {v}"
        }
        test "a constructor equals its record form BYTE-IDENTICALLY [D:http-s2] — a producer, not a drift" {
            // `==` cannot compare a record with a seq field; the pin is
            // value-level (the constructor cannot drift from the record)
            Expect.equal
                (run "Http.post \"http://x/y\"")
                (run "{ Http.defaults with method = Post; url = \"http://x/y\" }")
                "Http.post u IS its record form"

            Expect.equal (run "(Http.query \"u\").method") (VUnion("Query", None)) "the QUERY method exists"
            Expect.equal (run "(Http.delete \"u\").method") (VUnion("Delete", None)) "delete"
            Expect.equal (run "(Http.get \"u\").method") (VUnion("Get", None)) "get"
        }
        test "Http.withQuery percent-encodes keys and values; withQuery != query the constructor [D:http-s2]" {
            Expect.equal
                (run "\"http://x/s\" |> Http.withQuery [(\"q\", \"a b\"); (\"t\", \"x&y\")]")
                (VStr "http://x/s?q=a%20b&t=x%26y")
                "spaces and ampersands are encoded, not path-escaping"

            Expect.equal (run "\"http://x\" |> Http.withQuery []") (VStr "http://x") "no params, no ?"

            Expect.equal
                (run "\"http://x?a=1\" |> Http.withQuery [(\"b\", \"2\")]")
                (VStr "http://x?a=1&b=2")
                "an existing query gets & not ?"
        }
        test "Http.withQuery inserts before a #fragment; a bare trailing ? takes no & [D:http-s2]" {
            let q url =
                run $"\"{url}\" |> Http.withQuery [(\"a\", \"1\")]"

            Expect.equal (q "http://h/p#frag") (VStr "http://h/p?a=1#frag") "the query precedes the fragment"

            Expect.equal
                (q "http://h/p?x=1#frag")
                (VStr "http://h/p?x=1&a=1#frag")
                "an existing query, then the fragment"

            Expect.equal (q "http://h/p?") (VStr "http://h/p?a=1") "a bare ? is not followed by &"
        }
        test "a URL Http cannot address is refused before any connection, in weir's words [D:http-url-refused]" {
            let why u =
                Weir.Http.urlUnsendable u |> Option.defaultValue "<none>"

            Expect.equal
                (why "not a url")
                "'not a url' is not a URL weir can send to — an absolute http(s) URL is needed (https://host/path)"
                "a relative/garbage URL"

            Expect.equal
                (why "file:///etc/hostname")
                "'file:///etc/hostname' uses the 'file' scheme — Http sends to http:// and https:// URLs only"
                "a non-http scheme is named"

            Expect.equal
                (why "/etc/hostname")
                "'/etc/hostname' is not a URL weir can send to — an absolute http(s) URL is needed (https://host/path)"
                "a bare path is not called a file: URL"

            Expect.equal
                (why "ftp://u:pw@h/x")
                "'ftp://***@h/x' uses the 'ftp' scheme — Http sends to http:// and https:// URLs only"
                "the userinfo is redacted [D:url-redact]"

            Expect.isNone (Weir.Http.urlUnsendable "https://h/p?q=1") "an http(s) URL passes"

            let ex =
                Expect.throwsC (fun () -> run "Http.send (Http.get \"not a url\")" |> ignore) id

            Expect.equal
                ex.Message
                "'not a url' is not a URL weir can send to — an absolute http(s) URL is needed (https://host/path)"
                "Http.send raises it whole — no BaseAddress, no 'cannot reach'"
        }
        test "an Other method that is not a token is refused before send [D:http-method-token]" {
            let refuse m =
                (Expect.throwsC
                    (fun () ->
                        run $"Http.send {{ Http.get \"http://127.0.0.1:9/\" with method = Other \"{m}\" }}"
                        |> ignore)
                    id)
                    .Message

            Expect.equal (refuse "") "Http: the method is empty — Other needs a method token such as \"PURGE\"" "empty"

            Expect.equal
                (refuse "BAD METHOD")
                "Http: the method 'BAD METHOD' cannot be sent — it carries a space, which a method token cannot (letters, digits and !#$%&'*+-.^_`|~ only)"
                "a space"

            Expect.equal
                (refuse "pürge")
                "Http: the method 'pürge' cannot be sent — it carries 'ü', which a method token cannot (letters, digits and !#$%&'*+-.^_`|~ only)"
                "non-ASCII"

            Expect.isNone (Weir.Http.methodUnsendable "PURGE") "a token passes"
        }
        test "a Transfer-Encoding request header is refused as framing [D:http-header-unsendable]" {
            let ex =
                Expect.throwsC
                    (fun () ->
                        run
                            "Http.send { Http.get \"http://127.0.0.1:9/\" with headers = [(\"transfer-encoding\", \"chunked\")] }"
                        |> ignore)
                    id

            Expect.equal
                ex.Message
                "request header 'transfer-encoding' cannot be sent — it frames the body, which the transport does on its own (a body is sent with its Content-Length); drop it"
                "refused by name before the send"
        }
        test "an explicit Content-Length request header is refused as framing [D:http-header-unsendable]" {
            for v in [ "5"; "0"; "abc" ] do
                let ex =
                    Expect.throwsC
                        (fun () ->
                            run
                                $"Http.send {{ Http.post \"http://127.0.0.1:9/\" with body = Text \"a\"; headers = [(\"Content-Length\", \"{v}\")] }}"
                            |> ignore)
                        id

                Expect.equal
                    ex.Message
                    "request header 'Content-Length' cannot be sent — it frames the body, which the transport does on its own (the length is taken from the body itself); drop it"
                    $"refused by name before the send ({v})"
        }
        test "an explicit Content-Type replaces the body kind's: exactly one on the wire [D:http-content-type-override]" {
            let port, seen =
                rawServe (fun _ -> "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")

            let send (headers: (string * string) list) (body: string * string) =
                let req: Weir.Http.Req = {
                    Method = "POST"
                    Url = $"http://127.0.0.1:{port}/"
                    Headers = headers
                    SensitiveHeaders = Set.empty
                    Body = Some body
                    TimeoutMs = 10000L
                    Insecure = false
                }

                match Weir.Http.send System.Threading.CancellationToken.None req with
                | Ok _ ->
                    let mutable head = ""

                    while not (seen.TryDequeue(&head)) do
                        System.Threading.Thread.Sleep 10

                    head.Split("\r\n")
                    |> Array.filter (fun l -> l.StartsWith("content-type:", System.StringComparison.OrdinalIgnoreCase))
                    |> List.ofArray
                | Error(m, _) -> failtest m

            Expect.equal
                (send [ "Content-Type", "text/csv" ] ("text/plain", "x"))
                [ "Content-Type: text/csv" ]
                "Text + header"

            Expect.equal
                (send [ "content-type", "application/vnd.x+json" ] ("application/json", "{}"))
                [ "Content-Type: application/vnd.x+json" ]
                "any casing"

            Expect.equal
                (send [] ("application/x-www-form-urlencoded", "a=b"))
                [ "Content-Type: application/x-www-form-urlencoded" ]
                "no header: the body kind's default"
        }
        test
            "https to a plain port is a broken handshake, never 'not trusted' — insecure or not [D:tls-handshake-vs-trust]" {
            let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
            l.Start()

            let loop () =
                try
                    while true do
                        use c = l.AcceptTcpClient()
                        use st = c.GetStream()
                        st.Read(Array.zeroCreate<byte> 4096, 0, 4096) |> ignore
                        let b = System.Text.Encoding.ASCII.GetBytes "HTTP/1.1 400 Bad Request\r\n\r\n"
                        st.Write(b, 0, b.Length)
                with _ ->
                    ()

            let t = System.Threading.Thread(loop)
            t.IsBackground <- true
            t.Start()
            let port = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port

            try
                for insecure in [ false; true ] do
                    let req: Weir.Http.Req = {
                        Method = "GET"
                        Url = $"https://127.0.0.1:{port}/"
                        Headers = []
                        SensitiveHeaders = Set.empty
                        Body = None
                        TimeoutMs = 10000L
                        Insecure = insecure
                    }

                    match Weir.Http.send System.Threading.CancellationToken.None req with
                    | Error(_, Weir.Http.TlsHandshake) -> ()
                    | other -> failtest $"insecure={insecure}: expected TlsHandshake, got {other}"

                let ex =
                    Expect.throwsC
                        (fun () ->
                            run $"Http.send {{ Http.get \"https://127.0.0.1:{port}/\" with insecure = true }}"
                            |> ignore)
                        id

                Expect.isFalse
                    (ex.Message.Contains "insecure = true")
                    $"no repair naming a field already set: {ex.Message}"
            finally
                l.Stop()
        }
        test "a garbage status line is its own error, never 'cannot reach' + .NET text" {
            let port, _ = rawServe (fun _ -> "not http at all\r\n\r\n")

            let ex =
                Expect.throwsC (fun () -> run $"Http.send (Http.get \"http://127.0.0.1:{port}/\")" |> ignore) id

            Expect.equal
                ex.Message
                "127.0.0.1 answered with something that is not HTTP — its status line could not be read"
                ""
        }
        test "response headers: one pair per received line, values verbatim (H14)" {
            let port, _ =
                rawServe (fun _ ->
                    "HTTP/1.1 200 OK\r\nServer: BaseHTTP/0.6 Python/3.14.4\r\nX-Rep: a\r\nX-Rep: b, c\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")

            let r = $"Http.send (Http.get \"http://127.0.0.1:{port}/\")"

            Expect.equal
                (run $"{r} |> Http.header \"server\"")
                (VUnion("Some", Some(VStr "BaseHTTP/0.6 Python/3.14.4")))
                "a single value is never re-joined"

            Expect.equal
                (run $"{r} |> Http.headerAll \"x-rep\" |> Str.join \"|\"")
                (VStr "a|b, c")
                "one entry per header line, the list value untouched"

            Expect.equal
                (run $"{r} |> Http.header \"X-Rep\"")
                (VUnion("Some", Some(VStr "a")))
                "header is the first line"
        }
        test "an undecodable response charset is its own error, never 'cannot reach' (H15)" {
            let port, _ =
                rawServe (fun head ->
                    let cs = if head.Contains "/latin" then "iso-8859-1" else "x-bogus"
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset={cs}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n\u00e9!")

            let ex =
                Expect.throwsC (fun () -> run $"Http.send (Http.get \"http://127.0.0.1:{port}/\")" |> ignore) id

            Expect.stringContains ex.Message "charset 'x-bogus'" ex.Message
            Expect.isFalse (ex.Message.Contains "cannot reach") ex.Message

            Expect.equal
                (run $"(Http.send (Http.get \"http://127.0.0.1:{port}/latin\")).body |> Str.join \"\"")
                (VStr "\u00e9!")
                "a known charset still decodes"
        }
        test "auth plus an explicit Authorization header is refused — two credentials (H55)" {
            let ex =
                Expect.throwsC
                    (fun () ->
                        run
                            "Http.send { Http.get \"http://127.0.0.1:9/\" with auth = Basic (\"u\", Secret.of \"p\"); headers = [(\"authorization\", \"Bearer x\")] }"
                        |> ignore)
                    id

            Expect.stringContains ex.Message "two credentials" ex.Message
        }
        test "a plain Cookie/Authorization header is dropped on a cross-origin redirect (H55)" {
            let portB, seenB =
                rawServe (fun _ -> "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")

            let portA, _ =
                rawServe (fun _ ->
                    $"HTTP/1.1 302 Found\r\nLocation: http://localhost:{portB}/b\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")

            Expect.equal
                (run
                    $"(Http.send {{ Http.get \"http://127.0.0.1:{portA}/a\" with headers = [(\"Cookie\", \"sid=SECRET\"); (\"Authorization\", \"Bearer SECRET\"); (\"X-Control\", \"kept\")] }}).status")
                (VInt 200L)
                "the redirect is followed"

            let head = String.concat "" seenB
            Expect.isFalse (head.Contains "SECRET") $"no credential crosses origins: {head}"
            Expect.stringContains head "X-Control: kept" "an ordinary header crosses"
        }
        test "a redirect loop is a named error, never a 3xx handed back as data (H55)" {
            let port, _ =
                rawServe (fun _ ->
                    "HTTP/1.1 302 Found\r\nLocation: /again\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")

            let ex =
                Expect.throwsC (fun () -> run $"Http.send (Http.get \"http://127.0.0.1:{port}/\")" |> ignore) id

            Expect.stringContains ex.Message "redirect loop" ex.Message
            Expect.stringContains ex.Message "50 hops" ex.Message
        }
    ]

let recursiveFieldTests =
    // the field law goes recursive [D:recursive-fields]: nested records
    // and seq fields; cycles refuse naming their path; flat boundaries
    // (Args.load / Env.load) deliberately unmoved
    testList "recursive field law [D:recursive-fields]" [
        test "the acceptance: a nested record and a seq of records read" {
            let e =
                env
                |> declare "type Entity = { entityid: string }"
                |> declare "type Doc = { id: string; entityids: Entity; tags: seq<string> }"

            let src =
                "{\"id\":\"11831032\",\"entityids\":{\"entityid\":\"0033x\"},\"tags\":[\"a\",\"b\"]}"

            match Weir.Parser.parseStmt "src |> from json Doc |> _.entityids.entityid" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e expr with
                | Ok te ->
                    let venv = Map.add "src" (VSeq [ VStr src ]) valueEnv
                    Expect.equal (Weir.Eval.eval venv te) (VStr "0033x") "three deep"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "to json writes the CLI's encoding: no HTML escapes, BMP raw, astral as a pair (B28) [D:json-raw-user]" {
            let src = "{| a = \"say \\\"hi\\\" it's \u00e9 <b> & \u65e5\u672c \U0001F389\" |}"

            let expected =
                "{\"a\":\"say \\\"hi\\\" it's \u00e9 <b> & \u65e5\u672c \\uD83C\\uDF89\"}"

            Expect.equal (run (src + " |> to json") |> forceSeq) [ VStr expected ] "to json"
            Expect.equal (run ("[" + src + "] |> to jsonl") |> forceSeq) [ VStr expected ] "to jsonl"

            Expect.equal
                (run (
                    "let a = "
                    + src
                    + " in [a] |> to jsonl |> from jsonl {| a: string |} |> Seq.map _.a"
                ))
                (run ("[(" + src + ").a]"))
                "the text round-trips through from json"
        }
        test "to json round-trips nested records and seq fields" {
            let e =
                env
                |> declare "type Inner = { v: int }"
                |> declare "type Outer = { name: string; inner: Inner; xs: seq<int> }"

            let src = "{\"name\":\"a\",\"inner\":{\"v\":5},\"xs\":[1,2]}"

            match Weir.Parser.parseStmt "src |> from json Outer |> to json" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e expr with
                | Ok te ->
                    let venv = Map.add "src" (VSeq [ VStr src ]) valueEnv

                    Expect.equal
                        (Weir.Eval.eval venv te |> forceSeq)
                        [ VStr src ]
                        "byte-identical round-trip (alphabetical fields)"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "a direct cycle refuses NAMING ITS PATH; a mutual one is undeclarable" {
            let e = env |> declare "type T = { next: T }"

            match Weir.Check.typecheck e (parse "[\"{}\"] |> from json T") with
            | Error terr ->
                Expect.stringContains terr.Message "the type cycle T → T" "the path, not just 'recursive'"
                Expect.stringContains terr.Message "finite trees" "the reason"
            | Ok _ -> failtest "a self-referential record must refuse the boundary"

            // the mutual pair A → B → A cannot even be declared (no
            // forward type references) — unrepresentable by
            // construction, stated here so no audit reads it as a gap
            Expect.throws (fun () -> env |> declare "type A = { b: BNotYet }" |> ignore) "no forward refs"
        }
        test "the offending field is named BY PATH, category and why" {
            let e =
                env
                |> declare "type U = Red | Green"
                |> declare "type Mid = { u: U }"
                |> declare "type Top = { mid: Mid }"

            match Weir.Check.typecheck e (parse "[\"{}\"] |> from json Top") with
            | Error terr ->
                Expect.stringContains terr.Message "field 'mid.u'" "the dotted path locates the failure"
                Expect.stringContains terr.Message "is an untagged union" "the why"
                Expect.stringContains terr.Message "record of admitted fields, seq of an admitted" "the categories"
            | Ok _ -> failtest "a union field must refuse"
        }
        test "Option<Rec> and seq<Option<int>> work; null means None at EVERY depth" {
            let e =
                env
                |> declare "type R = { v: int }"
                |> declare "type H = { r: Option<R>; xs: seq<Option<int>> }"

            let read (src: string) (access: string) =
                match Weir.Parser.parseStmt $"src |> from json H |> {access}" with
                | Ok(SExpr expr) ->
                    match Weir.Check.typecheck e expr with
                    | Ok te -> Weir.Eval.eval (Map.add "src" (VSeq [ VStr src ]) valueEnv) te
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            Expect.equal
                (read "{\"r\":{\"v\":5},\"xs\":[1,null,3]}" "_.xs" |> forceSeq)
                [
                    VUnion("Some", Some(VInt 1L))
                    VUnion("None", None)
                    VUnion("Some", Some(VInt 3L))
                ]
                "null array elements are None"

            Expect.equal (read "{\"r\":null,\"xs\":[]}" "_.r") (VUnion("None", None)) "a null record is None"
        }
        test "a MISSING array is an error, not a silent [] — absence is Option's job" {
            let e = env |> declare "type W = { tags: seq<string> }"

            match Weir.Parser.parseStmt "src |> from json W |> _.tags" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e expr with
                | Ok te ->
                    let venv = Map.add "src" (VSeq [ VStr "{}" ]) valueEnv
                    let ex = Expect.throwsC (fun () -> Weir.Eval.eval venv te |> ignore) id
                    Expect.stringContains ex.Message "missing field 'tags'" "the required-field rule, unchanged"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "deep-but-legal reads (8 levels); the 64-deep parser cap is the only limit" {
            let e =
                [ 1..8 ]
                |> List.fold
                    (fun te i ->
                        te
                        |> declare (
                            if i = 1 then
                                "type L1 = { v: int }"
                            else
                                $"type L{i} = {{ n: L{i - 1} }}"
                        ))
                    env

            let json = (String.replicate 7 "{\"n\":") + "{\"v\":9}" + String.replicate 7 "}"
            let access = "_." + (String.concat "." (List.replicate 7 "n")) + ".v"

            match Weir.Parser.parseStmt $"src |> from json L8 |> {access}" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e expr with
                | Ok te -> Expect.equal (Weir.Eval.eval (Map.add "src" (VSeq [ VStr json ]) valueEnv) te) (VInt 9L) ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "anonymous shapes NEST — everywhere a type is written [D:anon-nesting]" {
            // the one-level rule reversed on the REPL shape-exploration
            // receipt: field-of-anon, seq-element, Map-value, and a
            // declared record's field all admit the form; registration
            // drains the parser's pending table at the existing seams
            let v =
                runWith
                    [ "src", VSeq [ VStr "{\"a\": {\"b\": 7}}" ] ]
                    "src |> from json {| a: {| b: int |} |} |> _.a |> _.b"

            Expect.equal v (VInt 7L) "nested field access, end to end"

            let v2 =
                runWith
                    [ "src", VSeq [ VStr "{\"rows\": [{\"id\": \"z\"}]}" ] ]
                    "src |> from json {| rows: seq<{| id: string |}> |} |> _.rows |> Seq.map _.id |> Seq.head"

            Expect.equal v2 (VStr "z") "seq-wrapped nesting"

            // same nested shape = same canonical name = one type
            let te =
                checkOk "[[\"{}\"]  |> from json {| a: {| b: int |} |}; [\"{}\"]  |> from json {| a: {| b: int |} |}]"

            Expect.equal te.Ty (TSeq(TNamed("{| a: {| b: int |} |}", []))) "canonical names recurse"

            // a refused type buried two deep still refuses with the dotted path
            let m = (checkErr "[\"\"] |> from json {| t: {| x: Bytes |} |}").Message
            Expect.stringContains m "t.x" "the path reaches through the nesting"
            Expect.stringContains m "not representable in JSON" ""
        }
        test "an anonymous shape composes with a nested DECLARED record [D:anon-records]" {
            let e = env |> declare "type Entity = { entityid: string }"

            match Weir.Parser.parseStmt "src |> from json {| entityids: Entity |} |> _.entityids.entityid" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e expr with
                | Ok te ->
                    let venv =
                        Map.add "src" (VSeq [ VStr "{\"entityids\":{\"entityid\":\"z9\"}}" ]) valueEnv

                    Expect.equal (Weir.Eval.eval venv te) (VStr "z9") "the top can be anonymous, the nesting declared"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "Args.load and Env.load STAY FLAT — a nested field refuses with the old messages" {
            let e =
                env
                |> declare "type Inner = { v: int }"
                |> declare "type Cfg = { inner: Inner }"

            match Weir.Check.typecheck e (parse "Env.load Cfg") with
            | Error terr -> Expect.stringContains terr.Message "Env.load fields must be" "the flat enumeration, intact"
            | Ok _ -> failtest "Env.load must refuse a nested record"

            // Args.load's law lives in Argv.fieldProblems (its own
            // module — the boundaries never shared a law); pin it there
            match Map.tryFind "Cfg" e.Types with
            | Some(Record cfgDef) ->
                match Weir.Argv.fieldProblems "" cfgDef with
                | Some msg -> Expect.stringContains msg "Args.load fields must be" "the flat enumeration, intact"
                | None -> failtest "Args.load must refuse a nested record"
            | _ -> failtest "Cfg must be declared"
        }
    ]

let jsonBoundaryTests =
    // the json boundary [D:from-jsonl] [D:json-boundary]: from json reads
    // one document -> T (joins its elements internally); from jsonl reads
    // one document per element -> seq<T>; every failure speaks weir's
    // words, never System.Text.Json's
    testList "json boundary [D:from-jsonl]" [
        test "from json reads a pretty-printed document ACROSS elements — no join, no head" {
            let doc =
                VSeq [
                    VStr "{"
                    VStr "  \"name\": \"x\","
                    VStr "  \"bytes\": 1,"
                    VStr "  \"readOnly\": false"
                    VStr "}"
                ]

            match runWith [ "src", doc ] "(src |> from json JRow).name" with
            | VStr "x" -> ()
            | other -> failtest $"the acceptance shape must hold: {other}"
        }
        test "a top-level array errors in weir's words and points at jsonl for lines" {
            let m =
                try
                    runWith [ "src", VSeq [ VStr "[{\"a\":1}]" ] ] "(src |> from json JRow).name"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "from json: the top level is a JSON array, not an object" ""
            Expect.stringContains m "declare seq<JRow> to read it" "the pointer is real now [D:from-json-seq]"
            Expect.isFalse (m.Contains "requested operation") "no System.Text.Json words"
        }
        test "a field's wrong value and a lone surrogate escape speak weir's words" {
            let msg (doc: string) =
                try
                    runWith [ "src", VSeq [ VStr doc ] ] "(src |> from json JRow).name" |> ignore
                    "no error"
                with e ->
                    e.Message

            let bytesTrue = msg "{\"name\": \"x\", \"bytes\": true, \"readOnly\": false}"
            Expect.stringContains bytesTrue "field 'bytes' expected int, got bool" "bool, not True"
            let bytesStr = msg "{\"name\": \"x\", \"bytes\": \"s\", \"readOnly\": false}"
            Expect.stringContains bytesStr "field 'bytes' expected int, got string" "string, not String"
            let nameArr = msg "{\"name\": [1], \"bytes\": 1, \"readOnly\": false}"
            Expect.stringContains nameArr "field 'name' expected string, got an array" "an array, not Array"

            let lone = msg "{\"name\": \"\\ud800\", \"bytes\": 1, \"readOnly\": false}"
            Expect.stringContains lone "from json: a string escape is a lone surrogate" "named"
            Expect.isFalse (lone.Contains "UTF-16") "no System.Text.Json words"
        }
        test "every non-object top level is named (number, string, boolean, null) — both adapters" {
            for lit, kind in [ "42", "number"; "\"s\"", "string"; "true", "boolean"; "null", "null" ] do
                let mJson =
                    try
                        runWith [ "src", VSeq [ VStr lit ] ] "(src |> from json JRow).name" |> ignore
                        "no error"
                    with e ->
                        e.Message

                Expect.stringContains mJson $"the top level is a JSON {kind}, not an object" $"json kind for {lit}"

                let mJsonl =
                    try
                        runWith [ "src", VSeq [ VStr lit ] ] "src |> from jsonl JRow"
                        |> forceSeq
                        |> ignore

                        "no error"
                    with e ->
                        e.Message

                Expect.stringContains mJsonl $"the top level is a JSON {kind}, not an object" $"jsonl kind for {lit}"
                Expect.stringContains mJsonl "one object per element" "jsonl names its contract"
        }
        test "invalid and empty documents error located, in weir's words" {
            let bad =
                try
                    runWith [ "src", VSeq [ VStr "not json" ] ] "(src |> from json JRow).name"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains bad "from json: not valid JSON: not json" ""

            let empty =
                try
                    runWith [ "src", VSeq [ VStr "" ] ] "(src |> from json JRow).name" |> ignore
                    "no error"
                with e ->
                    e.Message

            Expect.stringContains empty "from json: empty input — expected one JSON document" ""
        }
    ]

let formatSurfaceTests =
    // the format-surface audit's probe pins [D:format-surface-json]: cells
    // converted from unexamined to stated, each with its message half
    testList "format surface [D:format-surface-json]" [
        test "an integer-shaped overflow is not called a decimal" {
            let m =
                try
                    runWith
                        [
                            "src",
                            VSeq [
                                VStr "{\"name\": \"x\", \"bytes\": 99999999999999999999, \"readOnly\": false}"
                            ]
                        ]
                        "(src |> from json JRow).bytes"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "is out of int range (int64) — declare it Yaml" ""
            Expect.isFalse (m.Contains "decimal") "the overflow is named for what it is"
        }
        test "a decimal into int still teaches float" {
            let m =
                try
                    runWith
                        [
                            "src", VSeq [ VStr "{\"name\": \"x\", \"bytes\": 1.5, \"readOnly\": false}" ]
                        ]
                        "(src |> from json JRow).bytes"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "got a decimal number — declare it float" ""
        }
        test "over-deep nesting rejects with the teaching, never a raw parser word [D:json-depth]" {
            let deep = String.replicate 101 "{\"a\":" + "1" + String.replicate 101 "}"

            let m =
                try
                    runWith [ "src", VSeq [ VStr deep ] ] "(src |> from json JRow).name" |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "from json: valid JSON, but it nests deeper than 100 levels" ""
            Expect.isFalse (m.Contains "MaxDepth") "no System.Text.Json words"
        }
    ]

let fromJsonSeqTests =
    // from json seq<Name> [D:from-json-seq]: the declared type decides
    // what the top level must be — never the input; only json admits
    // the wrap
    testList "from json seq<Name> [D:from-json-seq]" [
        test "reads a pretty-printed top-level array across elements — the acceptance" {
            let doc =
                VSeq [
                    VStr "["
                    VStr "  {\"name\": \"a\", \"bytes\": 1, \"readOnly\": false},"
                    VStr "  {\"name\": \"b\", \"bytes\": 2, \"readOnly\": true}"
                    VStr "]"
                ]

            match runWith [ "src", doc ] "src |> from json seq<JRow> |> map _.name" |> forceSeq with
            | [ VStr "a"; VStr "b" ] -> ()
            | other -> failtest $"two rows expected: {other}"
        }
        test "an object document under seq<T> refuses, naming both shapes" {
            let m =
                try
                    runWith [ "src", VSeq [ VStr "{\"a\":1}" ] ] "src |> from json seq<JRow>"
                    |> forceSeq
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "expected an array (the declared type is seq<JRow>); got an object" ""
            Expect.stringContains m "write from json JRow" "the way back"
        }
        test "a non-object array element refuses naming kind and position" {
            let m =
                try
                    runWith
                        [
                            "src", VSeq [ VStr "[{\"name\": \"a\", \"bytes\": 1, \"readOnly\": false}, 7]" ]
                        ]
                        "src |> from json seq<JRow>"
                    |> forceSeq
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "array element [1] is a JSON number, not an object" ""
        }
        test "json error paths index arrays from zero, as weir's xs[i] does (B30)" {
            let msgOf' (json: string) prog =
                try
                    runWith [ "src", VSeq [ VStr json ] ] prog |> forceSeq |> ignore
                    "no error"
                with e ->
                    e.Message

            let row = "{\"name\": \"a\", \"bytes\": 1, \"readOnly\": false}"
            let bad = "{\"name\": \"b\", \"bytes\": \"x\", \"readOnly\": false}"

            Expect.stringContains
                (msgOf' ("[" + row + ", " + bad + "]") "src |> from json seq<JRow>")
                "field '[1].bytes'"
                "a top-level element's field names its zero-based index"

            let e = env |> declare "type JXs = { xs: seq<int> }"

            match Weir.Check.typecheck e (parse "[\"{\\\"xs\\\": [1, \\\"two\\\"]}\"] |> from json JXs") with
            | Ok te ->
                let ex = Expect.throwsC (fun () -> eval valueEnv te |> ignore) id
                Expect.stringContains ex.Message "field 'xs[1]'" "the second element is xs[1]"
            | Error terr -> failtest (formatError terr)
        }
        test "the wrap is json+yaml: jsonl alone gates with the category teaching [D:yaml-seq]" {

            Expect.stringContains
                (checkErr "[\"{}\"] |> from jsonl seq<FileRow>").Message
                "'from jsonl T' already yields seq<T> — write from jsonl FileRow"
                ""
        }
        test "the inner-name gate speaks in expectations, never an internal label" {
            match Weir.Parser.parseExpr "[\"[]\"] |> from json seq<int>" with
            | Error m ->
                Expect.stringContains
                    m
                    "'int' inside seq< > is not a declared record name"
                    "the designed teaching [D:adapter-slot-teach]"

                Expect.isFalse (m.Contains "type name") "the old leaked label is retired"
            | Ok _ -> failtest "seq<int> must not parse into the slot"
        }
        test "the bare teaching names the seq form for json only" {
            Expect.stringContains (checkErr "[\"x\"] |> from json").Message "or seq<FileRow> for a top-level array" ""

            Expect.isFalse
                ((checkErr "[\"x\"] |> from jsonl").Message.Contains "seq<FileRow>")
                "jsonl's teaching stays line-shaped"
        }
        test "hover on the inner name renders the record's own shape (the enclosing-leak class)" {
            let lines = [
                "type Peer = { host: string; port: int }"
                "let xs = [\"[]\"] |> from json seq<Peer>"
                "print (show (xs |> Seq.length))"
            ]

            match Weir.Lsp.hoverType lines 2 34 with
            | Some h -> Expect.stringContains h "host: string" "the record shape, not the district's or seq's"
            | None -> failtest "the inner name must hover"
        }
        test "completion after `from json ` offers record names, not the seq form (decided: the hover teaches it)" {
            let text = "[\"x\"] |> from json "
            Expect.contains (suggest text text.Length) "EnvVar" ""
            Expect.isFalse (List.exists (fun (s: string) -> s.StartsWith "seq") (suggest text text.Length)) ""
        }
    ]

let yamlSeqTests =
    // from yaml seq<Name> + the stream retirement [D:yaml-seq]: one
    // document, the declared type names the top level, streams teach
    testList "from yaml seq<Name> [D:yaml-seq]" [
        test "from yaml splits joined elements, line numbers through the joined document" {
            let v =
                runUnder
                    (env |> declare "type YJ = { a: int; b: string }")
                    "[Str.join \"\\n\" [\"a: 1\"; \"b: x\"]] |> from yaml YJ |> fun r -> r.b"

            Expect.equal v (VStr "x") "a joined element reads like two lines"

            Expect.equal
                (Weir.Yaml.numberLines [ "a: 1\r\nb: x"; "c: 2" ])
                [ 1, "a: 1"; 2, "b: x"; 3, "c: 2" ]
                "CRLF splits too; numbering runs on"

            let msg =
                (Expect.throwsC
                    (fun () ->
                        runUnder
                            (env |> declare "type YJ = { a: int; b: string }")
                            "[\"a: 1\\nb: x\"; \"b: y\"] |> from yaml YJ"
                        |> ignore)
                    id)
                    .Message

            Expect.stringContains msg "line 3: duplicate key 'b'" "the joined document's line"

            let parsed = runLive "[\"a: 1\\nb: x\"] |> Yaml.parse |> show"
            Expect.equal parsed (VStr "YMap ([(\"a\", YInt 1); (\"b\", YStr \"x\")])") "Yaml.parse too"

            Expect.stringContains
                (Weir.Infer.inferShapeText Set.empty Set.empty Weir.Infer.Yaml "Root" [ "a: 1\nb: x" ])
                "b: string"
                "and inferShape"
        }
        test "reads a top-level sequence document — the acceptance" {
            let e2 = env |> declare "type YH = { host: string }"

            let src = "[\"- host: a\"; \"- host: b\"] |> from yaml seq<YH> |> Seq.map _.host"

            match Weir.Parser.parseStmt src with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e2 expr with
                | Ok te -> Expect.equal (Weir.Eval.eval valueEnv te |> forceSeq) [ VStr "a"; VStr "b" ] ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "a mapping under seq<T> points at the bare form" {
            let e2 = env |> declare "type YH = { host: string }"

            match Weir.Parser.parseStmt "[\"host: a\"] |> from yaml seq<YH>" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e2 expr with
                | Ok te ->
                    let ex =
                        Expect.throwsC (fun () -> Weir.Eval.eval valueEnv te |> forceSeq |> ignore) id

                    Expect.stringContains
                        ex.Message
                        "expected a sequence at the top level (the declared type is seq<…>); got a mapping — write from yaml T"
                        ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "a sequence under bare T points at seq<T> — the audit's message trued" {
            let e2 = env |> declare "type YH = { host: string }"

            match Weir.Parser.parseStmt "([\"- host: a\"] |> from yaml YH).host" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e2 expr with
                | Ok te ->
                    let ex = Expect.throwsC (fun () -> Weir.Eval.eval valueEnv te |> ignore) id

                    Expect.stringContains
                        ex.Message
                        "the top level is a sequence, not a mapping — declare seq<YH> to read it"
                        ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "the bare teaching names the seq form; empty input errors located" {
            Expect.stringContains
                (checkErr "[\"x\"] |> from yaml").Message
                "or seq<Deployment> for a top-level sequence"
                ""

            let e2 = env |> declare "type YH = { host: string }"

            match Weir.Parser.parseStmt "([] |> from yaml YH).host" with
            | Ok(SExpr expr) ->
                match Weir.Check.typecheck e2 expr with
                | Ok te ->
                    let ex = Expect.throwsC (fun () -> Weir.Eval.eval valueEnv te |> ignore) id
                    Expect.stringContains ex.Message "from yaml: empty input — expected one document" ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "hover on the inner name renders the record's shape — the LSP lift holds" {
            let lines = [
                "type YH = { host: string }"
                "let xs = [\"- host: a\"] |> from yaml seq<YH>"
                "print (show (xs |> Seq.length))"
            ]

            match Weir.Lsp.hoverType lines 2 41 with
            | Some h -> Expect.stringContains h "host: string" "the record shape at the inner name"
            | None -> failtest "the inner name must hover"
        }
    ]

let recordPatternTests =
    // irrefutable record patterns [D:record-patterns]: the row seat
    // carries params (generality!), checkBinderName is reached by
    // construction, and the refusals each have a shape
    let renv =
        env
        |> declare "type RIn = { deep: int }"
        |> declare "type ROut = { inner: RIn; pair: int * string }"

    let runR = runUnder renv

    let errR = checkErrUnder renv

    testList "record patterns [D:record-patterns]" [
        test "the positions bind: let, param, lambda, match-with-guard" {
            Expect.equal (runR "let { UpN = n; UpT = t } = { UpN = 3; UpT = \"x\" } in n") (VInt 3L) "let binder"

            Expect.equal
                (runR "let f { UpN = n; UpT = t } = n + 1 in f { UpN = 7; UpT = \"y\" }")
                (VInt 8L)
                "the param position — the motivating case"

            Expect.equal
                (runR "[{ UpN = 1; UpT = \"z\" }] |> Seq.map (fun { UpN = n } -> n) |> Seq.head")
                (VInt 1L)
                "lambda param"

            Expect.equal
                (runR
                    "match { UpN = 2; UpT = \"running\" } with | { UpT = s; UpN = k } when s == \"running\" -> k | _ -> 0")
                (VInt 2L)
                "match arm; the guard does the testing"
        }
        test "nesting: record-in-record, tuple-in-record, record-in-tuple" {
            Expect.equal
                (runR
                    "let { inner = { deep = d }; pair = (x, q) } = { inner = { deep = 9 }; pair = (1, \"s\") } in d + x")
                (VInt 10L)
                "record and tuple under the record"

            Expect.equal (runR "let (a, { deep = d }) = (5, { deep = 2 }) in a + d") (VInt 7L) "record in a tuple"
        }
        test "the GENERALITY pin: one destructuring serves two unrelated records" {
            // the row seat's capability — the same generality copy-and-
            // update already has; no pre-existing spelling could say it
            Expect.equal
                (runR "let f { UpN = n } = n in f { UpN = 1; UpT = \"t\" } + f { UpN = 2 }")
                (VInt 3L)
                "UpdP and UpdQ through one function"
        }
        test "anonymous records are FREE: the pattern never names a type" {
            Expect.equal
                (runR "(fun { id = i } -> i) ([\"{\\\"id\\\": \\\"seven\\\"}\"] |> from json {| id: string |})")
                (VStr "seven")
                "a pattern over an anonymous shape"
        }
        test "the refusals: empty, duplicate, unknown field, refutable sub, row-in-match" {
            Expect.stringContains
                (errR "let { } = { UpN = 1 } in 0" |> _.Message)
                "binds nothing"
                "empty refuses with the repair"

            Expect.stringContains
                (errR "match { UpN = 1 } with | { UpN = a; UpN = b } -> a" |> _.Message)
                "duplicate field 'UpN'"
                "duplicates name the field"

            Expect.stringContains
                (errR "let { UpM = n } = { UpN = 1 } in n" |> _.Message)
                "Did you mean 'UpN'"
                "unknown field reaches did-you-mean"

            // the row-in-match refusal inverted [D:record-pattern-rows]:
            // a field pattern needs a row, and match position now emits
            // the one the binder position always did
            Expect.equal
                (runR "(fun x -> match x with | { UpN = n } -> n) { UpN = 4 }")
                (VInt 4L)
                "a record pattern types an unresolved scrutinee by row"

            // the stale-message casualty: a ctor pattern on a record
            // now teaches the record-pattern repair
            Expect.stringContains
                (errR "match { UpN = 1 } with | Running { UpN = n } -> n | _ -> 0" |> _.Message)
                "record pattern ({ field = binder })"
                "a ctor pattern on a record names the new repair"
        }
        test "checkBinderName is reached BY CONSTRUCTION: casing and the builtin reservation" {
            Expect.stringContains
                (errR "let { UpN = Total } = { UpN = 1 } in 0" |> _.Message)
                "start lowercase"
                "the casing law reaches record-pattern binders"

            Expect.stringContains
                (errR "let { UpN = pwd } = { UpN = 1 } in 0" |> _.Message)
                "builtin with no qualified spelling"
                "the builtin reservation reaches record-pattern binders"
        }
        test "provenance: a pattern-introduced field anchors the meet note" {
            // Phase 0's deliverable message: the wrong record names the
            // missing field; the becomes-a note needs the ambient
            // physical translator (script-only — "empty when no
            // translator is ambient"), so the note half is an e2e pin
            let msg = errR "let g { deep = d } = d in g { UpN = 1 }" |> _.Message
            Expect.stringContains msg "no field 'deep'" "names the missing field"
        }
    ]

let floatBoundaryTests =
    testList "Floats at the boundaries [D:floats-boundaries]" [
        test "from json: floats read, integer-shaped numbers WIDEN (a parse, not weir arithmetic)" {
            let e = env |> declare "type FJ = { rate: float }"

            let read line =
                match
                    Weir.Check.typecheck e (parse $"[\"{line}\"] |> from json FJ")
                    |> Result.map (eval valueEnv)
                with
                | Ok(VRecord(_, fs)) -> Weir.Eval.recGet "rate" fs
                | other -> failtest $"unexpected {other}"

            Expect.equal (read "{\\\"rate\\\": 1.5}") (VFloat 1.5) "float reads"
            Expect.equal (read "{\\\"rate\\\": 3}") (VFloat 3.0) "3 and 3.0 are the same JSON token class"
        }
        test "from json: a decimal into an int field teaches float (no silent truncation)" {
            let e = env |> declare "type FI = { n: int }"

            let te =
                match Weir.Check.typecheck e (parse "[\"{\\\"n\\\": 1.5}\"] |> from json FI") with
                | Ok te -> te
                | Error terr -> failtest terr.Message

            let ex = Expect.throwsC (fun () -> eval valueEnv te |> ignore) id
            Expect.stringContains ex.Message "expected int, got a decimal number — declare it float" ""
        }
        test "to json: 1.0 emits as 1.0 (the show shape; a float field is not an integer field)" {
            let e = env |> declare "type FE = { r: float }"

            match Weir.Check.typecheck e (parse "{ r = 1.0 } |> to json |> Seq.head") with
            | Ok te -> Expect.equal (eval valueEnv te) (VStr "{\"r\":1.0}") ""
            | Error terr -> failtest terr.Message
        }
        test "yaml: the float/float-string PAIR — value unquoted, lookalike string quoted, both round-trip" {
            let e = env |> declare "type FY = { rate: float; label: string }"

            let rendered =
                match Weir.Check.typecheck e (parse "{ rate = 1.5; label = \"1.5\" } |> to yaml |> Seq.freeze") with
                | Ok te -> eval valueEnv te
                | Error terr -> failtest terr.Message

            match rendered with
            | VSeq lines ->
                let ls =
                    lines
                    |> Seq.map (function
                        | VStr s -> s
                        | _ -> "?")
                    |> List.ofSeq

                Expect.contains ls "rate: 1.5" "the float renders UNQUOTED"
                Expect.contains ls "label: \"1.5\"" "the lookalike string renders QUOTED (reverse-Norway)"
            | v -> failtest $"unexpected {v}"

            // and back: the reader honors the same quotedness split
            let read =
                match Weir.Check.typecheck e (parse "[\"rate: 1.5\"; \"label: \\\"1.5\\\"\"] |> from yaml FY") with
                | Ok te -> eval valueEnv te
                | Error terr -> failtest terr.Message

            match read with
            | VRecord(_, fs) ->
                Expect.equal (Weir.Eval.recGet "rate" fs) (VFloat 1.5) "unquoted is the number"
                Expect.equal (Weir.Eval.recGet "label" fs) (VStr "1.5") "quoted is the string"
            | v -> failtest $"unexpected {v}"
        }
        test "yaml: .inf/.nan are REJECTED on the way in — no value exists to read into" {
            let e = env |> declare "type FN = { r: float }"

            let readErr line =
                match Weir.Check.typecheck e (parse $"[\"{line}\"] |> from yaml FN") with
                | Ok te -> (Expect.throwsC (fun () -> eval valueEnv te |> ignore) id).Message
                | Error terr -> failtest terr.Message

            Expect.stringContains (readErr "r: .nan") "not representable — weir floats are finite" ""
            Expect.stringContains (readErr "r: .inf") "not representable" ""

            Expect.stringContains
                (readErr "r: nope")
                "expected float, got 'nope'"
                "plain garbage keeps the plain message"
        }
        test "[<Default 0.5>] end to end: attrArgLit lexes the float, help renders it" {
            let scriptEnv = {
                env with
                    Modules = env.Modules |> Map.add "Self" Weir.Statements.selfMembers
            }

            let e = scriptEnv |> declare "type FCli = { [<Default 0.5>] rate: float }"

            match Weir.Check.typecheck e (parse "Args.load FCli") with
            | Ok te -> Expect.equal (formatTy te.Ty) "FCli" ""
            | Error terr -> failtest $"expected Ok, got {terr.Message}"

            let e2 = scriptEnv |> declare "type FBad = { [<Default 0.5>] port: int }"

            match Weir.Check.typecheck e2 (parse "Args.load FBad") with
            | Error terr -> Expect.stringContains terr.Message "the Default literal does not match the field" ""
            | Ok _ -> failtest "expected the literal-law rejection"
        }
    ]

let anonRecordTests =
    // anonymous record types in the adapter slot [D:anon-records]:
    // synthetic-nominal — the canonical name is the rendered shape, so
    // same-shape anons unify and a declared record stays distinct
    testList "anonymous record types [D:anon-records]" [
        test "the acceptance: _.field on an anonymous shape resolves at check time" {
            let v =
                runWith [ "src", VSeq [ VStr "{\"ip\": \"1.2.3.4\"}" ] ] "src |> from json {| ip: string |} |> _.ip"

            Expect.equal v (VStr "1.2.3.4") "typechecks and runs"

            Expect.equal
                (checkOk "[\"{}\"]  |> from json {| ip: string |} |> _.ip").Ty
                TStr
                "the field's type, at check time"
        }
        test "validation IS the declared-record validation — same messages, the shape as the name" {
            let declaredEnv = env |> declare "type P = { ip: string; port: int }"

            let msgOf (te) (src: string) =
                match Weir.Parser.parseStmt src with
                | Ok(SExpr e) ->
                    match Weir.Check.typecheck te e with
                    | Ok t ->
                        (try
                            Weir.Eval.eval valueEnv t |> Weir.Eval.formatValue |> ignore
                            None
                         with ex ->
                             Some ex.Message)
                    | Error terr -> Some terr.Message
                | other -> failtest $"unexpected: {other}"

            // missing field in the data: byte-identical modulo the name
            let declared =
                msgOf declaredEnv "[\"{\\\"ip\\\": \\\"x\\\"}\"] |> from json P |> _.ip"

            let anon =
                msgOf env "[\"{\\\"ip\\\": \\\"x\\\"}\"] |> from json {| ip: string; port: int |} |> _.ip"

            Expect.equal anon declared "the anonymous shape validates exactly as a declared record"

            // wrong field at check time: the declared-record wording with
            // the shape standing where the name stands
            let m = (checkErr "[\"{}\"]  |> from json {| ip: string |} |> _.nope").Message
            Expect.stringContains m "{| ip: string |} has no field 'nope'" "the declared-record message shape"
        }
        test "seq<{| … |}> composes — the array-of-anonymous-objects case" {
            let v =
                runWith
                    [ "src", VSeq [ VStr "[{\"a\": 1}, {\"a\": 2}]" ] ]
                    "src |> from json seq<{| a: int |}> |> Seq.map _.a"

            Expect.equal (forceSeq v) [ VInt 1L; VInt 2L ] ""
        }
        test "same shape unifies; field order canonicalizes; a declared record stays DISTINCT" {
            // two spellings, one type — order is canonical
            let te =
                checkOk "[[\"{}\"]  |> from json {| b: int; a: int |}; [\"{}\"]  |> from json {| a: int; b: int |}]"

            Expect.equal te.Ty (TSeq(TNamed("{| a: int; b: int |}", []))) "one canonical name"

            // a declared record with the same shape is a different type
            let declaredEnv = env |> declare "type T = { ip: string }"

            match Weir.Parser.parseStmt "[[\"{}\"]  |> from json T; [\"{}\"]  |> from json {| ip: string |}]" with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck declaredEnv e with
                | Error terr ->
                    Expect.stringContains terr.Message "expected T, got {| ip: string |}" "nominal law untouched"
                | Ok _ -> failtest "a declared record must not unify with an anonymous shape"
            | other -> failtest $"unexpected: {other}"
        }
        test "jsonl takes the shape; its seq<> refusal renders it" {
            let v =
                runWith
                    [ "src", VSeq [ VStr "{\"a\": 1}"; VStr "{\"a\": 2}" ] ]
                    "src |> from jsonl {| a: int |} |> Seq.map _.a"

            Expect.equal (forceSeq v) [ VInt 1L; VInt 2L ] ""

            let m = (checkErr "[\"x\"] |> from jsonl seq<{| a: int |}>").Message
            Expect.stringContains m "write from jsonl {| a: int |}" "the refusal names the bare form"
        }
        test "yaml takes the shape" {
            let v =
                runWith [ "src", VSeq [ VStr "ip: 9.9.9.9" ] ] "src |> from yaml {| ip: string |} |> _.ip"

            Expect.equal v (VStr "9.9.9.9") ""
        }
        test "duplicate fields reject; the echo renders the nameless record with the shape type" {
            let m = (checkErr "[\"x\"] |> from json {| a: int; a: string |}").Message
            Expect.stringContains m "duplicate field 'a'" ""

            let v =
                runWith [ "src", VSeq [ VStr "{\"ip\": \"x\"}" ] ] "src |> from json {| ip: string |}"

            Expect.equal (Weir.Eval.formatValue v) "{ ip = \"x\" }" "nameless, exactly as a declared record renders"
        }
        test "the shape persists across statements (script + REPL share checkStatement)" {
            let ds =
                diagsIn "anon.weir" [ "let x = [\"{}\"]  |> from json {| ip: string |}"; "let _y = x.ip" ]

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "the later statement resolves the field"
        }
    ]

let recordOrderTests =
    // field order carried, never semantic [D:record-order]
    testList "record order [D:record-order]" [
        test "an update preserves the field's POSITION" {
            let e = env |> declare "type RO = { z: int; a: int }"

            match Weir.Parser.parseStmt "RO { z = 1; a = 2 } |> (fun r -> { r with z = 9 }) |> to json" with
            | Ok(Weir.Ast.SExpr ex) ->
                match Weir.Check.typecheck e ex with
                | Ok te ->
                    Expect.equal (Weir.Eval.eval valueEnv te |> forceSeq) [ VStr """{"z":9,"a":2}""" ] "z stays first"
                | Error terr -> failtest terr.Message
            | other -> failtest $"parse: {other}"
        }
        test "equality ignores order; hash agrees" {
            let a = VRecord("P", [ "x", VInt 1L; "y", VInt 2L ])
            let b = VRecord("P", [ "y", VInt 2L; "x", VInt 1L ])
            Expect.equal a b "two spellings of one record"
            Expect.equal (hash a) (hash b) "hash is order-blind (name only)"
        }
        test "the JSON roundtrip: an anonymous read carries WIRE order back out" {
            Expect.equal
                (runWith
                    [ "src", VSeq [ VStr """{"z": 2, "a": 1}""" ] ]
                    "src |> from json {| a: int; z: int |} |> to json"
                 |> forceSeq)
                [ VStr """{"z":2,"a":1}""" ]
                "read-modify-nothing writes the input's order"
        }
    ]

let wireTableTests =
    // the wire table [D:wire-table]: one stance per type, read by both
    // boundaries in both directions — the four cells of a refused scalar
    // differ only in the format name, field prefix included
    let firstErr (lines: string list) =
        match errorsIn "wire.weir" lines with
        | d :: _ -> d.Message
        | [] -> failtest $"expected a refusal: {lines}"

    let cells (ty: string) (value: string) =
        [
            "json read", [ $"type R = {{ t: {ty} }}"; "let _x = [\"\"] |> from json R" ]
            "json write",
            [
                $"type R = {{ t: {ty} }}"
                $"let r = {{ t = {value} }}"
                "let _j = [r] |> to json |> Seq.freeze"
            ]
            "yaml read", [ $"type R = {{ t: {ty} }}"; "let _x = [\"t: x\"] |> from yaml R" ]
            "yaml write",
            [
                $"type R = {{ t: {ty} }}"
                $"let r = {{ t = {value} }}"
                "let _y = r |> to yaml |> Seq.freeze"
            ]
        ]
        |> List.map (fun (cell, lines) -> cell, (firstErr lines).Replace("JSON", "<fmt>").Replace("yaml", "<fmt>"))

    testList "the wire table [D:wire-table]" [
        test "a refused scalar teaches identically in all four cells" {
            for ty, value in
                [
                    "Instant", "Instant.now ()"
                    "Duration", "5s"
                    "Size", "Size.parse \"1KiB\""
                    "Bytes", "Str.toUtf8 \"x\""
                ] do
                let msgs = cells ty value
                let distinct = msgs |> List.map snd |> List.distinct
                Expect.equal distinct.Length 1 $"{ty}: one text across the cells, got {msgs}"
                Expect.stringContains distinct.Head "field 't': " $"{ty}: every cell names the field"
        }
        test "Secret refuses both write cells identically and reads in both [D:secret-read]" {
            let write fmt = [
                "type R = { t: Secret }"
                "let r = { t = Secret.of \"x\" }"
                $"let _j = [r] |> to {fmt} |> Seq.freeze"
            ]

            let j = (firstErr (write "json")).Replace("JSON", "<fmt>")
            let y = (firstErr (write "yaml")).Replace("yaml", "<fmt>")
            Expect.equal j y "one text across the write cells"
            Expect.stringContains j "field 't': a Secret must not cross" "the write refusal"

            for read in [ "let _x = [\"\"] |> from json R"; "let _x = [\"t: x\"] |> from yaml R" ] do
                Expect.isEmpty (errorsIn "wire.weir" [ "type R = { t: Secret }"; read ]) read
        }
        test "Uuid crosses yaml both ways, as it crosses JSON" {
            let lines = [
                "type E = { id: Uuid; parent: Option<Uuid>; note: string }"
                "let e = { id = Uuid.parse \"017f22e2-79b0-7cc3-98c4-dc0c0c07398f\"; parent = Some Uuid.nil; note = \"x\" }"
                "let back = e |> to yaml |> from yaml E"
                "print (show back)"
            ]

            let ds = diagsIn "wire.weir" lines
            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "checks"

            let te = env |> declare "type WUuidRow = { id: Uuid; parent: Option<Uuid> }"

            let v =
                match
                    Weir.Check.typecheck
                        te
                        (parse "[\"id: 017F22E2-79B0-7CC3-98C4-DC0C0C07398F\"] |> from yaml WUuidRow |> show")
                with
                | Ok t -> eval valueEnv t
                | Error terr -> failtest (formatError terr)

            Expect.equal
                v
                (VStr "{ id = 017f22e2-79b0-7cc3-98c4-dc0c0c07398f; parent = None }")
                "reads through the strict parser"
        }
        test "a malformed uuid in yaml raises with its line" {
            let te = env |> declare "type WUuidBad = { id: Uuid }"

            match Weir.Check.typecheck te (parse "[\"id: nope\"] |> from yaml WUuidBad |> show") with
            | Ok t ->
                try
                    eval valueEnv t |> ignore
                    failtest "a malformed uuid must raise"
                with ex ->
                    Expect.stringContains ex.Message "line 1: not a uuid" "names the line and the parse teaching"
            | Error terr -> failtest (formatError terr)
        }
    ]

let wireCodecTests =
    // declared codecs [D:wire-codecs]: option (a) — an unannotated
    // Instant/Duration/Size still refuses; a codec attribute is the
    // author naming the consumer's convention, read by both directions
    let checkMsgs = errorMsgsIn "codec.weir"

    let runIn te input =
        match Weir.Check.typecheck te (parse input) with
        | Ok t -> eval valueEnv t
        | Error terr -> failtest (formatError terr)

    let te =
        env
        |> declare
            "type CodecEv = { [<Iso8601>] at: Instant; [<EpochMs>] ms: Instant; [<EpochSec>] sec: Instant; [<Millis>] took: Duration; [<Seconds>] every: Duration; [<ByteCount; Wire \"bytes\">] size: Size; [<Iso8601>] seen: Option<Instant>; [<EpochMs>] marks: seq<Instant> }"
        |> declare
            "type PlainEv = { at: Instant; ms: Instant; sec: Instant; took: Duration; every: Duration; size: Size; seen: Option<Instant>; marks: seq<Instant> }"

    let value =
        "CodecEv { at = Instant.parse \"2026-08-14T12:00:00.250Z\"; ms = Instant.parse \"2026-08-14T12:00:00.250Z\"; sec = Instant.parse \"2026-08-14T12:00:00Z\"; took = 1500ms; every = 2m; size = Size.parse \"1KiB\"; seen = None; marks = [Instant.ofEpochMs 5] }"

    testList "declared codecs [D:wire-codecs]" [
        test "every codec round-trips through JSON and YAML; the wire shape is the declared encoding" {
            Expect.equal
                (runIn te $"[ {value} ] |> to jsonl |> Seq.freeze")
                (VSeq [
                    VStr
                        "{\"at\":\"2026-08-14T12:00:00.250Z\",\"ms\":1786708800250,\"sec\":1786708800,\"took\":1500,\"every\":120,\"bytes\":1024,\"marks\":[5]}"
                ])
                "ISO text, epoch ms/s, ms, s, bytes (under its wire key), a seq element-wise; None omitted"

            let viaJson =
                runIn
                    te
                    $"let e = {value} in show ([e] |> to jsonl |> from jsonl CodecEv |> Seq.freeze |> Seq.head) == show e"

            let viaYaml =
                runIn te $"let e = {value} in show (e |> to yaml |> from yaml CodecEv) == show e"

            Expect.equal viaJson (VBool true) "json round trip"
            Expect.equal viaYaml (VBool true) "yaml round trip"
        }
        test "JSON and YAML carry the same value per codec" {
            let y =
                match runIn te $"{value} |> to yaml |> Seq.freeze" with
                | VSeq lines ->
                    lines
                    |> Seq.map (function
                        | VStr l -> l
                        | v -> failtest $"{v}")
                    |> List.ofSeq
                | v -> failtest $"{v}"

            for line in
                [
                    "at: \"2026-08-14T12:00:00.250Z\""
                    "ms: 1786708800250"
                    "sec: 1786708800"
                    "took: 1500"
                    "every: 120"
                    "bytes: 1024"
                ] do
                Expect.contains y line $"yaml carries {line}, matching the JSON value"
        }
        test "a lossy write raises instead of truncating" {
            let tes =
                env
                |> declare "type SecEv = { [<EpochSec>] t: Instant; [<Seconds>] d: Duration }"

            for expr, want in
                [
                    "[{ t = Instant.parse \"2026-01-01T00:00:00.5Z\"; d = 1s }] |> to jsonl |> Seq.freeze",
                    "round it, or declare [<EpochMs>]"
                    "{ t = Instant.parse \"2026-01-01T00:00:00Z\"; d = 1500ms } |> to yaml |> Seq.freeze",
                    "round it, or declare [<Millis>]"
                ] do
                try
                    runIn tes expr |> ignore
                    failtest $"must raise: {expr}"
                with ex ->
                    Expect.stringContains ex.Message want "names the exact codec"
        }
        test "fit is checked at the declaration, naming what fits" {
            let one decl = checkMsgs [ decl ] |> List.head

            Expect.stringContains
                (one "type R = { [<EpochMs>] name: string }")
                "string crosses as itself, with no codec"
                "a canonical type"

            Expect.stringContains
                (one "type R = { [<Seconds>] t: Instant }")
                "its encodings: [<Iso8601>], [<EpochMs>] or [<EpochSec>]"
                "did-you-mean lists the fitting codecs"

            Expect.stringContains
                (one "type R = { [<Iso8601; EpochMs>] t: Instant }")
                "one codec per field"
                "two encodings"

            Expect.stringContains (one "type R = { [<Isoo8601>] t: Instant }") "Did you mean 'Iso8601'?" "a misspelling"
        }
        test "Secret refuses any codec, for the type's own reason" {
            let m = checkMsgs [ "type R = { [<Iso8601>] s: Secret }" ] |> List.head
            Expect.stringContains m "a Secret must not cross to JSON or YAML" "the type, not the codec"
        }
        test "option (a) holds: an unannotated field still refuses, naming the attributes first" {
            let m =
                checkMsgs [ "type R = { t: Instant }"; "let _x = [\"\"] |> from json R" ]
                |> List.head

            Expect.stringContains
                m
                "declare the field's encoding ([<Iso8601>], [<EpochMs>] or [<EpochSec>])"
                "attributes first"

            Expect.stringContains m "Instant.epochMs" "the explicit conversion stays"

            let top =
                checkMsgs [ "let _j = [Instant.now ()] |> to jsonl |> Seq.freeze" ] |> List.head

            Expect.isFalse (top.Contains "[<") "an element cannot carry an attribute — no attribute advice there"
        }
        test "show and == do not see the codec" {
            let plainValue = value.Replace("CodecEv {", "PlainEv {")

            Expect.equal
                (runIn te $"show ({value})")
                (runIn te $"show ({plainValue})")
                "the annotated type shows exactly as its unannotated twin"
            // == needs a seq-free record (a seq field rules out Eq)
            let teq =
                env |> declare "type EqEv = { [<EpochMs>] t: Instant; [<Seconds>] d: Duration }"

            Expect.equal
                (runIn
                    teq
                    "EqEv { t = Instant.ofEpochMs 1; d = 1500ms } == EqEv { t = Instant.ofEpochMs 1; d = 1500ms }")
                (VBool true)
                "== compares values, not encodings — 1500ms is not cut to 1s for equality"
        }
        test "the codec union and the wire table agree" {
            for c in Weir.Types.allCodecs do
                let ty = Weir.Check.codecTy c
                Expect.contains (Weir.Check.codecsOfTy ty) c $"{Weir.Types.codecName c} is listed under {formatTy ty}"
        }
    ]

let mapStringTests =
    // Map<string, T> [D:map-string]: the ID-keyed object — keys are
    // data, not schema; string keys only; the adapter slot's third form
    let checkErrIn te input =
        match Weir.Check.typecheck te (parse input) with
        | Error terr -> terr
        | Ok _ -> failtest "expected a type error"

    let wbDoc =
        VSeq [
            VStr
                "{\"bbb\": {\"name\": \"b\", \"bytes\": 2, \"readOnly\": true}, \"aaa\": {\"name\": \"a\", \"bytes\": 1, \"readOnly\": false}}"
        ]

    testList "Map<string, T> [D:map-string]" [
        test "the acceptance: from json Map<string, T> reads the ID-keyed object; pairs walk sorted" {
            let v =
                runWith
                    [ "src", wbDoc ]
                    "src |> from json Map<string, JRow> |> Map.pairs |> Seq.map (fun (k, d) -> k + \"=\" + d.name)"

            Expect.equal (forceSeq v) [ VStr "aaa=a"; VStr "bbb=b" ] "sorted by key, the row typed"

            Expect.equal
                (checkOk "src |> from json Map<string, JRow>").Ty
                (TNamed("Map", [ TStr; TNamed("JRow", []) ]))
                "the declared type, at check time"
        }
        test "the round-trip: to json writes the map back as ONE keyed object" {
            let v = runWith [ "src", wbDoc ] "src |> from json Map<string, JRow> |> to json"

            Expect.equal
                (forceSeq v)
                [
                    VStr
                        "{\"aaa\":{\"name\":\"a\",\"bytes\":1,\"readOnly\":false},\"bbb\":{\"name\":\"b\",\"bytes\":2,\"readOnly\":true}}"
                ]
                ""
        }
        test "an anonymous shape composes in the value slot — the two-features-in-one-slot bar" {
            let v =
                runWith
                    [ "src", VSeq [ VStr "{\"k\": {\"ip\": \"9\"}}" ] ]
                    "src |> from json Map<string, {| ip: string |}> |> Map.get \"k\" |> _.ip"

            Expect.equal v (VStr "9") ""
        }
        test "duplicate keys LAST-WIN — the boundary's stated law" {
            let v =
                runWith
                    [
                        "src",
                        VSeq [
                            VStr
                                "{\"k\": {\"name\": \"x\", \"bytes\": 1, \"readOnly\": false}, \"k\": {\"name\": \"y\", \"bytes\": 2, \"readOnly\": false}}"
                        ]
                    ]
                    "let m = src |> from json Map<string, JRow> in (m |> Map.get \"k\" |> _.name, m |> Map.count)"

            Expect.equal v (VTuple [ VStr "y"; VInt 1L ]) "one key, the second value"
        }
        test "errors name the KEYED path — a null field cites [\"k\"].name" {
            let m =
                try
                    runWith
                        [
                            "src", VSeq [ VStr "{\"k\": {\"name\": null, \"bytes\": 1, \"readOnly\": false}}" ]
                        ]
                        "src |> from json Map<string, JRow>"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "field '[\"k\"].name' is null" "the map key IS the path segment"
        }
        test "a non-object value under a key refuses naming key and kind" {
            let m =
                try
                    runWith [ "src", VSeq [ VStr "{\"k\": 42}" ] ] "src |> from json Map<string, JRow>"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "key \"k\" expected an object (JRow), got number" ""
        }
        test "an object where an array is declared says 'got object'" {
            let m =
                try
                    runWith [ "src", VSeq [ VStr "{\"xs\":{}}" ] ] "src |> from json {| xs: seq<int> |}"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "field 'xs' expected an array (seq<int>), got object in:" ""
        }
        test "a top-level array under Map refuses, pointing at seq<T>" {
            let m =
                try
                    runWith [ "src", VSeq [ VStr "[1, 2]" ] ] "src |> from json Map<string, JRow>"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "the declared type is Map<string, JRow> — declare seq<JRow> to read an array" ""
        }
        test "jsonl refuses the wrap with the shape teaching; yaml states the follow-up" {
            Expect.stringContains
                (checkErr "[\"\"] |> from jsonl Map<string, JRow>").Message
                "'from jsonl' reads one object per line; a Map is ONE object — write from json Map<string, JRow>"
                ""

            Expect.stringContains
                (checkErr "[\"\"] |> from yaml Map<string, JRow>").Message
                "'from yaml' does not take Map<string, JRow> yet — 'from json' does"
                ""
        }
        test "the bare teaching now names the Map form for json" {
            Expect.stringContains
                (checkErr "[\"x\"] |> from json").Message
                "Map<string, FileRow> for an ID-keyed object"
                ""
        }
        test "string keys ONLY at the wire: the anonymous path teaches the slot's law [D:map-keys]" {
            Expect.stringContains
                (checkErr "[\"\"] |> from json {| m: Map<int, string> |}").Message
                "field 'm': JSON object keys are strings — a Map<int, …> can't cross"
                "one helper"
        }
        test "the slot's key teaches like a field [D:map-values] [D:map-keys]" {
            Expect.stringContains
                (checkErr "[\"\"] |> from json Map<int, JRow>").Message
                "JSON object keys are strings — a Map<int, …> can't cross; convert with Map.pairs |> Seq.map (fun (k, v) -> (show k, v)) |> Map.ofPairs"
                "a record value"

            Expect.stringContains
                (checkErr "[\"\"] |> from json Map<int, int>").Message
                "JSON object keys are strings"
                "a scalar value"
        }
        test "a whole document reads as Map<string, V> for any field-admitted V [D:map-values]" {
            let one src code =
                runWith [ "src", VSeq [ VStr src ] ] code

            Expect.equal
                (one "{\"b\": 2, \"a\": 1}" "src |> from json Map<string, int> |> Map.values"
                 |> forceSeq)
                [ VInt 1L; VInt 2L ]
                "int values"

            Expect.equal
                (checkOk "src |> from json Map<string, int>").Ty
                (TNamed("Map", [ TStr; TInt ]))
                "typed at check"

            Expect.equal
                (one
                    "{\"k\": [\"x\"], \"n\": null}"
                    "src |> from json Map<string, Option<seq<string>>> |> Map.get \"n\"")
                (VUnion("None", None))
                "Option of seq, null is None"

            Expect.equal
                (one
                    "{\"k\": {\"j\": 3}}"
                    "src |> from json Map<string, Map<string, int>> |> Map.get \"k\" |> Map.get \"j\"")
                (VInt 3L)
                "a Map nests"

            Expect.equal
                (one
                    "{\"k\": [{\"ip\": \"9\"}]}"
                    "src |> from json Map<string, seq<{| ip: string |}>> |> Map.get \"k\" |> Seq.head |> _.ip")
                (VStr "9")
                "anonymous shape under seq"

            Expect.equal
                (one "{\"b\": 2, \"a\": 1}" "src |> from json Map<string, int> |> to json"
                 |> forceSeq)
                [ VStr "{\"a\":1,\"b\":2}" ]
                "round-trips"
        }
        test "a value-type Map refuses as a field would, paths through the key [D:map-values]" {
            let m =
                try
                    runWith [ "src", VSeq [ VStr "{\"k\": \"x\"}" ] ] "src |> from json Map<string, int>"
                    |> ignore

                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "field '[\"k\"]' expected int, got string" "the key is the path"

            Expect.stringContains
                (checkErr "[\"\"] |> from json Map<string, int -> int>").Message
                "type int -> int is not admitted"
                "the field law"

            Expect.stringContains
                (checkErr "[\"\"] |> from yaml Map<string, int>").Message
                "'from yaml' does not take Map<string, int> yet"
                "yaml keeps its follow-up"

            Expect.stringContains
                (checkErr "[\"\"] |> from jsonl Map<string, int>").Message
                "a Map is ONE object"
                "jsonl keeps its refusal"
        }
        test "show renders sorted pairs and masks a Secret value" {
            Expect.equal
                (run "show (Map.ofPairs [(\"b\", 2); (\"a\", 1)])")
                (VStr "map [(\"a\", 1); (\"b\", 2)]")
                "sorted"

            Expect.equal (run "show (Map.ofPairs [(\"k\", Secret.of \"x\")])") (VStr "map [(\"k\", ***)]") "masked"
        }
        test "Eq stays excluded: '==' refuses with the type named" {
            Expect.stringContains
                (checkErr "Map.ofPairs [(\"a\", 1)] == Map.ofPairs [(\"a\", 1)]").Message
                "'==' is not defined for Map<string, int>"
                ""
        }
        test "Env.load and Args.load refuse a Map field, naming it" {
            // Args.load is script-only: the Self module marks script mode
            let scriptEnv = {
                env with
                    Modules = env.Modules |> Map.add "Self" Weir.Statements.selfMembers
            }

            let e = scriptEnv |> declare "type MCfg = { home: Map<string, int> }"

            Expect.stringContains (checkErrIn e "Env.load MCfg").Message "'home' is Map<string, int>" ""
            Expect.stringContains (checkErrIn e "Args.load MCfg").Message "'home' is Map<string, int>" ""
        }
        test "the member surface: ofPairs last-wins, get raises naming the key, tryGet/has/add/remove/count/keys/values" {
            Expect.equal (run "Map.ofPairs [(\"k\", 1); (\"k\", 9)] |> Map.get \"k\"") (VInt 9L) "last wins"

            let m =
                try
                    run "Map.ofPairs [(\"k\", 1)] |> Map.get \"zz\"" |> ignore
                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "Map.get: no key \"zz\"" "the key is named"

            Expect.equal (run "Map.ofPairs [(\"k\", 1)] |> Map.tryGet \"z\"") (VUnion("None", None)) ""
            Expect.equal (run "Map.ofPairs [(\"k\", 1)] |> Map.tryGet \"k\"") (VUnion("Some", Some(VInt 1L))) ""
            Expect.equal (run "Map.ofPairs [(\"k\", 1)] |> Map.has \"k\"") (VBool true) ""
            Expect.equal (run "Map.ofPairs [(\"k\", 1)] |> Map.add \"z\" 5 |> Map.count") (VInt 2L) ""
            Expect.equal (run "Map.ofPairs [(\"k\", 1)] |> Map.remove \"k\" |> Map.count") (VInt 0L) ""

            Expect.equal
                (run "Map.ofPairs [(\"b\", 2); (\"a\", 1)] |> Map.keys" |> forceSeq)
                [ VStr "a"; VStr "b" ]
                "sorted"

            Expect.equal
                (run "Map.ofPairs [(\"b\", 2); (\"a\", 1)] |> Map.values" |> forceSeq)
                [ VInt 1L; VInt 2L ]
                "key-sorted order"
        }
    ]

let mapKeysTests =
    // Map<K, V> [D:map-keys]: any equatable key inside a program, a total
    // deterministic order, string keys only at the wire
    let ctorsOf (decl: string) =
        match (parseDecl decl).Body with
        | DUnion cases -> constructorValues cases
        | DRecord _ -> []

    let within (decls: string list) =
        let te = decls |> List.fold (fun e d -> declare d e) env

        let ve =
            decls
            |> List.collect ctorsOf
            |> List.fold (fun vs (n, v) -> Map.add n v vs) valueEnv

        te, ve

    let runIn (te, ve) input =
        match typecheck te (parse input) with
        | Ok t -> eval ve t
        | Error terr -> failtest (formatError terr)

    let errIn (te, _) input =
        match typecheck te (parse input) with
        | Ok t -> failtest $"expected a type error, got {formatTy t.Ty}"
        | Error terr -> terr

    let showOf scope input =
        match runIn scope $"show ({input})" with
        | VStr s -> s
        | v -> failtest $"expected a string, got {formatValue v}"

    let raised scope input =
        try
            runIn scope input |> ignore
            "no error"
        with e ->
            e.Message

    let tiers =
        within [
            "type MkTier = MkZinc | MkMid of int | MkAlpha"
            "type MkPt = { mx: int; my: string }"
            "type MkFl = { mf: float }"
        ]

    testList "Map<K, V> [D:map-keys]" [
        test "each equatable key kind builds, looks up and walks in order" {
            Expect.equal (showOf tiers "Map.ofPairs [(3, \"c\"); (1, \"a\")]") "map [(1, \"a\"); (3, \"c\")]" "int"
            Expect.equal (showOf tiers "Map.ofPairs [(\"b\", 1); (\"a\", 2)]") "map [(\"a\", 2); (\"b\", 1)]" "string"
            Expect.equal (showOf tiers "Map.ofPairs [(true, 1); (false, 0)]") "map [(false, 0); (true, 1)]" "bool"
            Expect.equal (showOf tiers "Map.ofPairs [(2s, 1); (500ms, 0)]") "map [(500ms, 0); (2s, 1)]" "Duration"

            Expect.equal
                (showOf tiers "Map.ofPairs [(Instant.parse \"2026-01-02\", 1); (Instant.parse \"2026-01-01\", 0)]")
                "map [(2026-01-01T00:00:00Z, 0); (2026-01-02T00:00:00Z, 1)]"
                "Instant"

            Expect.equal
                (showOf tiers "Map.ofPairs [(Uuid.max, 1); (Uuid.nil, 0)] |> Map.values")
                "[0; 1]"
                "Uuid, byte order"

            Expect.equal (showOf tiers "Map.ofPairs [(2KiB, 1); (1KiB, 0)] |> Map.values") "[0; 1]" "Size"

            Expect.equal
                (showOf tiers "Map.ofPairs [((2, \"a\"), 1); ((1, \"z\"), 2); ((1, \"b\"), 3)] |> Map.values")
                "[3; 2; 1]"
                "tuple, element by element"

            Expect.equal
                (showOf
                    tiers
                    "Map.ofPairs [({ mx = 2; my = \"a\" }, 1); ({ mx = 1; my = \"z\" }, 2)] |> Map.get { mx = 1; my = \"z\" }")
                "2"
                "record"

            Expect.equal (showOf tiers "Map.ofPairs [(Some 2, 1); (None, 0)] |> Map.tryGet None") "Some 0" "Option"
        }
        test "union keys order by case NAME, then payload" {
            Expect.equal
                (showOf tiers "Map.ofPairs [(MkAlpha, 1); (MkMid 9, 2); (MkZinc, 3); (MkMid 2, 4)] |> Map.keys")
                "[MkAlpha; MkMid 2; MkMid 9; MkZinc]"
                "name order, not declaration order"

            Expect.equal
                (showOf tiers "Map.ofPairs [(Ok 1, 1); (Error \"e\", 2); (Ok 0, 3)] |> Map.keys")
                "[Error \"e\"; Ok 0; Ok 1]"
                "Result"
        }
        test "the whole surface takes any key: last-win, has, add, remove, count, pairs" {
            Expect.equal (runIn tiers "Map.ofPairs [(MkZinc, 1); (MkZinc, 9)] |> Map.get MkZinc") (VInt 9L) "last wins"
            Expect.equal (runIn tiers "Map.ofPairs [(1, 1)] |> Map.has 1") (VBool true) ""
            Expect.equal (runIn tiers "Map.ofPairs [(1, 1)] |> Map.add 2 5 |> Map.count") (VInt 2L) ""
            Expect.equal (runIn tiers "Map.ofPairs [(1, 1)] |> Map.remove 1 |> Map.count") (VInt 0L) ""

            Expect.equal
                (runIn tiers "Map.ofPairs [(2, \"b\"); (1, \"a\")] |> Map.pairs" |> forceSeq)
                [ VTuple [ VInt 1L; VStr "a" ]; VTuple [ VInt 2L; VStr "b" ] ]
                "pairs, key-sorted"

            Expect.equal
                (showOf tiers "[\"a.md\"; \"b.txt\"; \"c.md\"] |> Seq.countBy Path.extension |> Map.ofPairs")
                "map [(\".md\", 2); (\".txt\", 1)]"
                "countBy feeds ofPairs"
        }
        test "Map.get raises naming a non-string key as show renders it" {
            Expect.stringContains (raised tiers "Map.ofPairs [(1, 1)] |> Map.get 7") "Map.get: no key 7" "int"

            Expect.stringContains
                (raised tiers "Map.ofPairs [(MkZinc, 1)] |> Map.get (MkMid 3)")
                "Map.get: no key MkMid 3"
                "union"

            Expect.stringContains
                (raised tiers "Map.ofPairs [(Secret.of \"a\", 1)] |> Map.get (Secret.of \"b\")")
                "Map.get: no key ***"
                "a Secret masks"

            Expect.stringContains
                (raised tiers "Map.ofPairs [(\"k\", 1)] |> Map.get \"zz\"")
                "Map.get: no key \"zz\""
                "string keeps quotes"
        }
        test "a float key refuses — floats have no equality [D:floats]" {
            Expect.stringContains (errIn tiers "Map.ofPairs [(1.5, 1)]").Message "floats do not join '=='" "use site"

            Expect.stringContains
                (declErr "type MkBad = { m: Map<float, int> }" env).Message
                "a Map key needs equality, and float has none"
                "declaration"
        }
        test "a record holding a float refuses as a key, at the use and the declaration" {
            Expect.stringContains
                (errIn tiers "Map.ofPairs [({ mf = 1.0 }, 1)]").Message
                "got MkFl — it holds a float"
                "use site"

            Expect.stringContains
                (declErr "type MkBad = { m: Map<MkFl, int> }" (fst tiers)).Message
                "a Map key needs equality, and MkFl has none — it holds a float"
                "declaration"
        }
        test "seq, function and Map keys refuse; a generic key meets the class at the call" {
            Expect.stringContains (errIn tiers "Map.ofPairs [([1], 1)]").Message "equatable" "seq"
            Expect.stringContains (errIn tiers "Map.ofPairs [(Map.ofPairs [(1, 1)], 1)]").Message "equatable" "Map"

            Expect.stringContains
                (errIn tiers "let idx xs = xs |> Seq.map (fun x -> (x, 1)) |> Map.ofPairs in idx [1.5]").Message
                "floats do not join '=='"
                "generic helper"
        }
        test "a declared Map field takes a non-string key inside the program" {
            let scope = within [ "type MkIdx = { byId: Map<int, string> }" ]
            Expect.equal (runIn scope "{ byId = Map.ofPairs [(1, \"a\")] }.byId |> Map.get 1") (VStr "a") ""
        }
        test "every wire boundary refuses a non-string key with the one teaching" {
            let teach = "JSON object keys are strings — a Map<int, …> can't cross"
            let scope = within [ "type MkW = { m: Map<int, int> }" ]

            Expect.stringContains (errIn scope "Map.ofPairs [(1, 2)] |> to json").Message teach "to json"

            Expect.stringContains
                (errIn scope "{ m = Map.ofPairs [(1, 2)] } |> to json").Message
                $"field 'm': {teach}"
                "to json, field"

            Expect.stringContains
                (errIn scope "[\"{}\"] |> from json MkW").Message
                $"field 'm': {teach}"
                "from json, field"

            Expect.stringContains (errIn scope "[\"{}\"] |> from json Map<int, int>").Message teach "from json, slot"
            Expect.stringContains (errIn scope "[\"{}\"] |> from jsonl Map<int, int>").Message teach "from jsonl, slot"

            Expect.stringContains
                (errIn scope "[\"{}\"] |> from json {| m: Map<int, int> |}").Message
                teach
                "anonymous shape"

            Expect.stringContains
                (errIn scope "Http.send { Http.post \"http://127.0.0.1:1/\" with body = Json (Map.ofPairs [(1, 2)]) }")
                    .Message
                teach
                "an Http Json body"

            Expect.stringContains
                (errIn scope "Map.ofPairs [(1, 2)] |> to yaml").Message
                "cannot cross the yaml boundary"
                "to yaml"

            Expect.stringContains
                (errIn scope "[\"{}\"] |> from yaml MkW").Message
                "cannot cross the yaml boundary"
                "from yaml"
        }
        test "argv refuses a Map splice, any key" {
            Expect.stringContains
                (errorMsgsIn "m.weir" [ "let m = Map.ofPairs [(1, 2)]"; "echo $m" ]
                 |> String.concat "\n")
                "command arguments must be strings, ints or bools"
                ""
        }
    ]

// within serve — the scoped HTTP listener [D:http-serve]. Unit pins for
// the seams a unit test can reach: the parse/check contract (the head
// form, the shared body union, the type errors) and the Serve leg's own
// socket lifecycle (start/stop idempotency, the port frees). The three
// primitives' runtime behaviour (incremental stream, concurrency ceiling,
// signal teardown) are the e2e battery's server cells — a listener needs
// a real socket and a client process, out of a unit's reach.
let serveTests =
    let clean = cleanIn "serve.weir"

    let mustSay = mustSayIn "serve.weir"

    let okHandler =
        "let h = fun req -> HttpServerResponse { status = 200; headers = []; body = Text \"ok\" }"

    let shape (lines: string list) =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Ok [ ll ] ->
            match Weir.Parser.parseLineFull cmdResolver ll.Text with
            | Ok s -> Weir.Ast.sexprStmt s
            | Error f -> failtest f.Message
        | other -> failtest $"unexpected assembly: {other}"

    let freePort () =
        let probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        probe.Start()
        let port = (probe.LocalEndpoint :?> System.Net.IPEndPoint).Port
        probe.Stop()
        port

    let limits (maxBody: int64) : Weir.Serve.Limits = {
        BodyTimeoutMs = 5000
        MaxBody = maxBody
        MaxQueued = 1024
    }

    // serve `answer` on a fresh port for the duration of `f port`
    // the suite runs in parallel on the thread pool and these clients
    // block pool threads in Poll while Kestrel needs the same pool for
    // the request delegate; a 2-core runner starts with two workers and
    // grows by about one per second when starved, so a fast request can
    // wait seconds for a thread and a timing bound reads that as the
    // server's fault — a floor keeps the harness out of the measurement
    let poolFloor =
        lazy
            (let w, io = System.Threading.ThreadPool.GetMinThreads()
             System.Threading.ThreadPool.SetMinThreads(max w 32, max io 32) |> ignore)

    let serving (maxBody: int64) (answer: Weir.Serve.SReq -> Weir.Serve.SResp) (f: int -> 'a) : 'a =
        poolFloor.Force()
        let port = freePort ()
        let h = Weir.Serve.start port (limits maxBody)

        let loop () =
            let mutable go = true

            while go do
                match Weir.Serve.accept h with
                | None -> go <- false
                | Some ctx ->
                    let resp =
                        try
                            answer (Weir.Serve.readRequest ctx)
                        with
                        | Weir.Serve.BodyTooLarge -> {
                            Status = 413
                            Headers = []
                            Body = Weir.Serve.RText "too large"
                          }
                        | ex -> {
                            Status = 599
                            Headers = []
                            Body = Weir.Serve.RText ex.Message
                          }

                    Weir.Serve.writeResponse ctx resp ignore
                    Weir.Serve.finish ctx

        let t = System.Threading.Thread(loop)
        t.IsBackground <- true
        t.Start()

        try
            f port
        finally
            Weir.Serve.stop h 0

    // read until `fin` holds on what arrived, the peer closes, or it goes
    // quiet: the first byte may wait on a cold server (JIT, a loaded CI
    // runner); once a reply flows, a `quietMs` gap ends it. Readiness
    // is polled, never a receive timeout — a timed-out recv leaves a
    // Windows socket indeterminate for the next exchange. Date dropped.
    let readUntil (quietMs: int) (fin: string -> bool) (c: System.Net.Sockets.TcpClient) : string =
        let buf = Array.zeroCreate<byte> 65536
        let sb = System.Text.StringBuilder()
        let mutable wait = 10000

        (try
            let mutable go = true

            while go && not (fin (sb.ToString())) do
                if c.Client.Poll(wait * 1000, System.Net.Sockets.SelectMode.SelectRead) then
                    let n = c.Client.Receive buf

                    if n = 0 then
                        go <- false
                    else
                        sb.Append(System.Text.Encoding.Latin1.GetString(buf, 0, n)) |> ignore
                        wait <- quietMs
                else
                    go <- false
         with _ ->
             ())

        System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "Date: [^\r]*\r\n", "")

    let readQuiet = readUntil 700 (fun _ -> false)

    let send (c: System.Net.Sockets.TcpClient) (raw: string) =
        let b = System.Text.Encoding.Latin1.GetBytes raw
        c.GetStream().Write(b, 0, b.Length)

    // drive the Serve leg over a real loopback socket: `answer` maps the
    // read request to a response, each raw request is sent in turn on ONE
    // kept-alive connection, and each reply's raw bytes (Date dropped)
    // come back — the wire view a unit can pin
    // each exchange's read ends early once its `fin` holds on the reply
    let wireFor
        (maxBody: int64)
        (answer: Weir.Serve.SReq -> Weir.Serve.SResp)
        (exchanges: (string * (string -> bool)) list)
        : string list =
        serving maxBody answer (fun port ->
            use c = new System.Net.Sockets.TcpClient("127.0.0.1", port)

            [
                for raw, fin in exchanges do
                    send c raw
                    yield readUntil 10000 fin c
            ])

    let wireWith (maxBody: int64) (answer: Weir.Serve.SReq -> Weir.Serve.SResp) (raws: string list) : string list =
        serving maxBody answer (fun port ->
            use c = new System.Net.Sockets.TcpClient("127.0.0.1", port)

            [
                for raw in raws do
                    send c raw
                    yield readQuiet c
            ])

    // the status lines of a raw exchange, in wire order
    let statusLines (reply: string) : string list =
        System.Text.RegularExpressions.Regex.Matches(reply, "HTTP/1\\.[01] [0-9]{3}[^\r]*")
        |> Seq.map (fun m -> m.Value)
        |> List.ofSeq

    let wire = wireWith (16L * 1024L * 1024L)

    let get (m: string) (path: string) =
        $"{m} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"

    testList "within serve [D:http-serve]" [
        test "the head form checks clean: binder = config record + handler, body sequences with lets" {
            clean
                [
                    okHandler
                    "within serve srv = { port = Some 8080; maxConcurrent = 4; handler = h }"
                    "    print $\"on {Server.port srv}\""
                    "    let x = 1"
                    "    print $\"{x}\""
                ]
                "the canonical form"
        }
        test "an inline config record's ; does not hide the head (the assembler fix)" {
            // { port = ...; maxConcurrent = ... } on the head line — the
            // `;` must not defeat the body attach (endsInServeHead is
            // `;`-blind, unlike isWithinHead's `;`-guarded path)
            clean
                [
                    okHandler
                    "within serve srv = { port = Some 8080; maxConcurrent = 2; handler = h }"
                    "    print \"up\""
                    "    let y = 2"
                    "    print $\"{y}\""
                ]
                "inline-; config"
        }
        test "the binder is a Server handle: port/running typecheck, a bad member does not" {
            clean
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; handler = h }"
                    "    print $\"{Server.running srv}\""
                ]
                "Server.running on the binder"

            mustSay
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; handler = h }"
                    "    print $\"{Proc.pid srv}\""
                ]
                "expected Proc, got Server"
                "a Proc member on a Server binder is a type error"
        }
        test "the handler must be HttpServerRequest -> HttpServerResponse" {
            // a wrong return type: a plain int handler names the expected
            // response type — the server response (body: HttpBody) is not
            // the client HttpResponse (body: seq<string>), the checker
            // holds the boundary
            mustSay
                [
                    "let bad = fun req -> 5"
                    "within serve srv = { port = Some 80; maxConcurrent = 1; handler = bad }"
                    "    print \"ok\""
                ]
                "HttpServerResponse"
                "an int-returning handler names the expected response type"
        }
        test "the config must be a ServerConfig record" {
            mustSay
                [ "within serve srv = 8080"; "    print \"ok\"" ]
                "ServerConfig"
                "a bare int config names the record"
        }
        test "port is Option<int>: None and Some n check; a bare int names the repair [D:serve-port-option]" {
            clean
                [
                    okHandler
                    "within serve srv = { port = None; maxConcurrent = 1; handler = h }"
                    "    print $\"{Server.port srv}\""
                ]
                "None picks a free port"

            mustSay
                [
                    okHandler
                    "within serve srv = { port = 8080; maxConcurrent = 1; handler = h }"
                    "    print \"ok\""
                ]
                "expected Option<int>, got int — serve's port is optional: `port = Some 8080` for that port, `port = None` for a free one"
                "the pre-Option spelling names its repair"
        }
        test "Server.defaults is the resting config, and the internal key is the same object [D:serve-defaults]" {
            let v = Weir.Builtins.valueEnv.Value["Server.defaults"]

            Expect.isTrue
                (obj.ReferenceEquals(Weir.Builtins.valueEnv.Value["|serveDefaults"], v))
                "the key=value base is Server.defaults itself"

            match v with
            | VRecord("ServerConfig", f) ->
                let get n = f |> List.find (fst >> (=) n) |> snd
                Expect.equal (get "port") (VUnion("None", None)) "a free port"

                Expect.equal
                    (get "maxConcurrent")
                    (VInt(int64 (max 8 System.Environment.ProcessorCount)))
                    "the processor count, floored at 8"

                Expect.equal
                    (get "maxQueued")
                    (VInt(int64 (16 * max 8 System.Environment.ProcessorCount)))
                    "sixteen waiting per default slot [D:serve-queue]"

                Expect.equal (get "bodyTimeout") (VDur 30000L) "30s"
                Expect.equal (get "maxBody") (VSize(16L * 1024L * 1024L)) "16MiB"
            | other -> failtest $"not a ServerConfig: {other}"

            Expect.isFalse (Weir.Effects.isCommandReifier "|serveDefaults") "a library desugar, not a command"
            Expect.isFalse (Weir.Effects.effectfulName "|serveDefaults") "pure"
        }
        test "the default handler: GET / is 200, anything else 404, both carrying the hint [D:serve-defaults]" {
            let handler =
                match Weir.Builtins.valueEnv.Value["Server.defaults"] with
                | VRecord(_, f) ->
                    match f |> List.find (fst >> (=) "handler") |> snd with
                    | VBuiltin h -> h
                    | other -> failtest $"not a builtin: {other}"
                | other -> failtest $"not a record: {other}"

            let ask (m: string) (path: string) =
                let req =
                    VRecord(
                        "HttpServerRequest",
                        [
                            "method", VUnion(m, None)
                            "path", VStr path
                            "query", VStr ""
                            "headers", VSeq []
                            "body", VStr ""
                        ]
                    )

                match handler req with
                | VRecord("HttpServerResponse", f) ->
                    let get n = f |> List.find (fst >> (=) n) |> snd

                    match get "status", get "headers", get "body" with
                    | VInt st, VSeq hs, VUnion("Text", Some(VStr body)) ->
                        match List.ofSeq hs with
                        | [ VTuple [ VStr "Content-Type"; VStr ct ] ] -> st, ct, body
                        | other -> failtest $"unexpected headers: {other}"
                    | other -> failtest $"unexpected response: {other}"
                | other -> failtest $"not a response: {other}"

            let st, ct, body = ask "Get" "/"
            Expect.equal st 200L "GET / answers"
            Expect.equal ct "text/plain; charset=utf-8" "as plain text"
            Expect.stringContains body "no handler is set" "says what is missing"
            Expect.stringContains body "within serve s handler=route" "and shows the line that sets one"

            for m, path in [ "Get", "/health"; "Post", "/" ] do
                let st2, _, body2 = ask m path
                Expect.equal st2 404L $"{m} {path} is not found"
                Expect.equal body2 body "with the same hint"
        }
        test "the handler is a ServerConfig field; a literal may leave it and the bounds out [D:serve-defaults]" {
            clean
                [
                    okHandler
                    "let cfg = { Server.defaults with port = Some 8080; handler = h }"
                    "within serve srv = { cfg with handler = h }"
                    "    print $\"{Server.port srv}\""
                ]
                "an update of the defaults is a ServerConfig"

            clean
                [
                    okHandler
                    "within serve srv = { port = None; maxConcurrent = 1; handler = h }"
                    "    print \"ok\""
                ]
                "the literal takes handler like its other optional fields"

            mustSay
                [
                    "within serve srv = { port = None; maxConcurrent = 1; handler = 5 }"
                    "    print \"ok\""
                ]
                "HttpServerRequest -> HttpServerResponse"
                "a handler field of the wrong type names the function type"

            clean
                [
                    okHandler
                    "let cfg = { port = None; maxConcurrent = 1; maxQueued = 8; bodyTimeout = 1s; maxBody = 1MiB; handler = h }"
                    "within serve srv = { cfg with handler = h }"
                    "    print \"ok\""
                ]
                "the full record is an ordinary literal"
        }
        test "key=value options are an update of the defaults, by construction [D:serve-defaults]" {
            Expect.equal
                (shape [ "within serve s port=(Some 8080) handler=h"; "    print \"b\"" ])
                "(sexpr (within serve s {|serveDefaults with port = (Some 8080); handler = h} (print \"b\")))"
                "the pairs desugar over the internal key"

            Expect.equal
                (shape [ "within serve s"; "    print \"b\"" ])
                "(sexpr (within serve s |serveDefaults (print \"b\")))"
                "no options is the defaults themselves"

            clean
                [
                    okHandler
                    "within serve s port=(Some 8080) maxConcurrent=2 bodyTimeout=5s maxBody=1MiB handler=h"
                    "    print $\"{Server.port s}\""
                ]
                "every key"

            clean
                [ "within serve s port=None"; "    FOO=1 sh -c \"true\""; "    print \"b\"" ]
                "a first block line shaped NAME=value cmd is the block's, not one more option"

            mustSay
                [ "within serve s prot=None"; "    print \"b\"" ]
                "ServerConfig has no field 'prot'. Did you mean 'port'?"
                "an unknown key names the nearest one"

            mustSay
                [ "within serve s port=None port=None"; "    print \"b\"" ]
                "duplicate key 'port' — each option is given once"
                "a repeated key"

            mustSay
                [ "within serve s maxConcurrent=\"4\""; "    print \"b\"" ]
                "expected int, got string"
                "a value of the wrong type"
        }
        test
            "in key=value options a plain T for an Option<T> key means Some; a written record stays strict [D:serve-defaults]" {
            clean
                [
                    "let p = 8081"
                    "within serve s port=8080"
                    "    print \"a\""
                    "within serve s port=p"
                    "    print \"b\""
                ]
                "a plain int is Some of it"

            clean
                [
                    "let q = Some 8080"
                    "within serve s port=(Some 8080)"
                    "    print \"a\""
                    "within serve s port=None"
                    "    print \"b\""
                    "within serve s port=q"
                    "    print \"c\""
                ]
                "the Option spellings still check"

            mustSay
                [ "within serve s port=\"8080\""; "    print \"a\"" ]
                "expected Option<int>, got string"
                "neither T nor Option<T> names the key's type"

            mustSay
                [ "let cfg = { Server.defaults with port = 8080 }" ]
                "expected Option<int>, got int"
                "the public record update is strict"

            mustSay
                [ "within serve s = { port = 8080; maxConcurrent = 1 }"; "    print \"a\"" ]
                "serve's port is optional: `port = Some 8080`"
                "the literal is strict and names its repair [D:serve-port-option]"

            clean [ "retry attempts=2 timeout=2m"; "    true" ] "retry's timeout"
            clean [ "repeat interval=1s timeout=5m"; "    true" ] "repeat's timeout"
            clean [ "retry attempts=2 timeout=(Some 2m)"; "    true" ] "the Some spelling stays"

            mustSay
                [ "let o = { Retry.defaults with timeout = 2m }" ]
                "expected Option<Duration>, got Duration"
                "Retry's written update is strict"

            clean
                [ "type T = Some of string | Nope"; "retry attempts=1 timeout=1s"; "    true" ]
                "a user case named Some does not capture the lift"

            expectValue "retry attempts=2 delay=0ms timeout=2m (1 + 1) until r (r == 2)" (VInt 2L)

            let ex =
                Expect.throwsC (fun () -> run "repeat interval=5ms timeout=30ms (1 == 2)" |> ignore) id

            Expect.stringContains ex.Message "repeat: timed out after" "the lifted timeout is the one that runs"
        }
        test "a form word hovers at token level: the statement need not parse [D:serve-defaults]" {
            let hover lines line col =
                Weir.Lsp.hoverType lines line col |> Option.defaultValue "<none>"

            let n = max 8 System.Environment.ProcessorCount

            for lines in
                [
                    [ "within serve" ]
                    [ "within serve s port=" ]
                    [ "let f () ="; "    within serve"; "f ()" ]
                ] do
                let line = List.length lines |> min 2
                let col = (List.item (line - 1) lines).IndexOf "serve" + 2
                let hv = hover lines line col

                Expect.stringContains
                    hv
                    "serve — binds: an HTTP server that runs until the block exits"
                    $"the kind's doc: {lines}"

                Expect.stringContains
                    hv
                    "within serve <name> [key=value …]  (or `within serve <name> = <config>`), then a block or `until stopped`"
                    $"the shape line: {lines}"

                Expect.stringContains
                    hv
                    "keys: port: Option<int>, maxConcurrent: int, maxQueued: int, bodyTimeout: Duration, maxBody: Size, handler: HttpServerRequest -> HttpServerResponse"
                    $"the keys, off ServerConfig: {lines}"

                Expect.stringContains
                    hv
                    $"defaults: port = None, maxConcurrent = {n}, maxQueued = {16 * n}, bodyTimeout = 30s, maxBody = 16 MiB, handler = <landing page>"
                    $"the defaults, off Server.defaults: {lines}"

            Expect.stringContains (hover [ "within" ] 1 3) "within <kind> — a scoped block" "the keyword alone"

            Expect.stringContains
                (hover [ "retry" ] 1 3)
                "keys: attempts: int, delay: Duration, timeout: Option<Duration>\ndefaults: attempts = 5, delay = 1s, timeout = None"
                "a half-typed retry"

            Expect.stringContains
                (hover [ "poll timeout=" ] 1 2)
                "keys: timeout: Duration, interval: Duration\ndefaults: timeout = 1m, interval = 1s"
                "a half-typed poll"

            Expect.stringContains
                (hover [ "let n = repeat interval=" ] 1 11)
                "keys: interval: Duration, timeout: Option<Duration>\ndefaults: timeout = None (interval is required)"
                "a half-typed repeat"

            Expect.stringContains
                (hover [ "within lock \"p\""; "    print \"a\"" ] 1 9)
                "within lock <path> [timeout=<duration>]"
                "every kind carries its shape line"

            Expect.equal
                (hover [ okHandler; "within serve srv port=8080 handler=h"; "    print \"x\"" ] 2 15)
                "Server"
                "the binder is the handle, in the options head"

            Expect.equal (hover [ "within serve srv"; "until stopped" ] 1 15) "Server" "and in the block-less form"

            Expect.equal (hover [ "let x = \"within serve\" +" ] 1 18) "<none>" "the same letters in a string are data"
            Expect.equal (hover [ "within serve // retry" ] 1 18) "<none>" "and in a comment"
        }
        test "= config is the record form: one record, the handler its field [D:serve-defaults] [D:serve-one-record]" {
            Expect.equal
                (shape [ "within serve s = cfg"; "    print \"b\"" ])
                "(sexpr (within serve s cfg (print \"b\")))"
                "one atom: the record carries the handler"

            mustSay
                [ okHandler; "within serve s = Server.defaults h"; "    print \"b\"" ]
                "within serve takes one record after `=` — the handler is its `handler` field"
                "a handler word after the record is refused"

            mustSay
                [ okHandler; "within serve s = Server.defaults h"; "    print \"b\"" ]
                "handler=route"
                "the refusal shows the option spelling"

            clean
                [
                    okHandler
                    "let cfg = { Server.defaults with port = Some 8080; handler = h }"
                    "within serve s = cfg"
                    "    print $\"{Server.port s}\""
                ]
                "a ServerConfig value alone"

            clean
                [ "within serve s = { port = None; maxConcurrent = 2 }"; "    print \"b\"" ]
                "the short literal alone: its handler rests at the default"

            mustSay [ "within serve s = 8080"; "    print \"b\"" ] "expected ServerConfig, got int" "not a record"
        }
        test "no block: until stopped and/or always directly under the head [D:serve-defaults]" {
            Expect.equal
                (shape [ "within serve s"; "until stopped" ])
                "(sexpr (within serve s |serveDefaults (held ())))"
                "the held server with no block"

            Expect.equal
                (shape [ "within serve s port=None"; "until stopped"; "always"; "    print \"c\"" ])
                "(sexpr (within serve s {|serveDefaults with port = None} (within (held ()) (always (print \"c\")))))"
                "with a cleanup"

            Expect.equal
                (shape [ "within serve s = cfg"; "always"; "    print \"c\"" ])
                "(sexpr (within serve s cfg (within () (always (print \"c\")))))"
                "always alone"

            clean [ "within serve s"; "until stopped"; "print \"after\"" ] "checks clean"

            clean
                [
                    okHandler
                    "within serve s = { Server.defaults with handler = h }"
                    "until stopped"
                ]
                "after the record head too"

            clean
                [
                    "within serve s"
                    "until stopped"
                    "always"
                    "    print $\"{Server.running s}\""
                ]
                "the binder reaches the cleanup"
        }
        test "a serve head with no block and no until stopped names the shape [D:serve-defaults]" {
            mustSay
                [ "within serve s" ]
                "within serve needs an indented block, or `until stopped` on the next line"
                "the head alone"

            mustSay
                [ "let f () ="; "    within serve s port=None"; "f ()" ]
                "within serve needs an indented block, or `until stopped` on the next line"
                "the head alone, nested"

            mustSay
                [ "within serve"; "until stopped" ]
                "within serve takes a name for the server — `within serve s port=8080"
                "no binder"

            mustSay
                [ "within serve port=None handler=h"; "until stopped" ]
                "within serve takes a name for the server before its options"
                "options where the name goes"

            mustSay
                [ "within serve port=None"; "until stopped" ]
                "within serve takes a name for the server before its options — `within serve s port=…`"
                "one option where the name goes"
        }
        test "Serve.start 0 binds a free port and the handle reports the BOUND one [D:serve-port-option]" {
            let h = Weir.Serve.start 0 (limits 1024L)

            try
                Expect.isTrue (h.Port > 0) $"a real port, not 0: {h.Port}"
                use c = new System.Net.Sockets.TcpClient()
                c.Connect(System.Net.IPAddress.Loopback, h.Port)
                Expect.isTrue c.Connected "the reported port is the one listening"
            finally
                Weir.Serve.stop h 0
        }
        test "the shared body union: Stream is a new case beside NoBody/Json/Text" {
            clean
                [
                    "let h = fun req -> HttpServerResponse { status = 200; headers = []; body = Stream [\"a\"; \"b\"] }"
                    "within serve srv = { port = Some 80; maxConcurrent = 1; handler = h }"
                    "    print \"ok\""
                ]
                "a Stream response body"
        }
        test "Serve.start binds the port; stop frees it; stop is idempotent" {
            let lim = limits (16L * 1024L * 1024L)
            let h = Weir.Serve.start 8199 lim
            Expect.equal h.Port 8199 "the handle carries the port"
            Expect.isFalse h.Closed "open after start"

            // loopback only [D:serve-loopback-names]: 127.0.0.1, and ::1
            // where the host has it — never an all-interfaces bind
            Expect.contains h.Bound "127.0.0.1" "the v4 loopback address"

            Expect.isTrue
                (h.Bound |> List.forall (fun a -> a = "127.0.0.1" || a = "::1"))
                $"no all-interfaces bind — the loopback posture holds: {h.Bound}"

            // a second bind on the held port fails (the socket is taken)
            Expect.throwsC (fun () -> Weir.Serve.start 8199 lim |> ignore) (fun ex ->
                Expect.equal
                    ex.Message
                    "serve: cannot listen on 127.0.0.1:8199 — the port is already in use (another process holds it, or a server on it is still closing)"
                    "the bind error names the port and why, in weir's words")
            |> ignore

            Weir.Serve.stop h 0
            Expect.isTrue h.Closed "closed after stop"
            // idempotent: a second stop is benign
            Weir.Serve.stop h 0

            // the port frees — a fresh bind on it now succeeds
            let h2 = Weir.Serve.start 8199 lim
            Expect.isFalse h2.Closed "the second bind succeeds after the first freed"
            Weir.Serve.stop h2 0
        }
        test "pipelined requests on one connection are each answered, in order [D:serve-kestrel]" {
            let answer (r: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = []
                Body = Weir.Serve.RText $"<{r.Path.TrimStart('/')}>"
            }

            let two = get "GET" "/p1" + get "GET" "/p2"
            let three = get "GET" "/q1" + get "POST" "/q2" + get "GET" "/q3"

            // each read waits for its last body (or a close), so responses
            // arriving in separate segments with gaps all count
            let has (tag: string) (r: string) = r.Contains tag

            match wireFor (16L * 1024L * 1024L) answer [ two, has "<p2>"; three, has "<q3>" ] with
            | [ a; b ] ->
                Expect.equal (statusLines a |> List.length) 2 $"two responses for two pipelined requests: {a}"
                Expect.isTrue (a.IndexOf "<p1>" >= 0 && a.IndexOf "<p1>" < a.IndexOf "<p2>") $"p1 then p2: {a}"
                Expect.equal (statusLines b |> List.length) 3 $"three responses for three: {b}"

                Expect.isTrue
                    (b.IndexOf "<q1>" >= 0
                     && b.IndexOf "<q1>" < b.IndexOf "<q2>"
                     && b.IndexOf "<q2>" < b.IndexOf "<q3>")
                    $"q1, q2, q3 in order: {b}"
            | other -> failtest $"two exchanges expected: {other}"
        }
        test "three duplicate request headers reach the handler as three pairs in wire order (F10) [D:serve-kestrel]" {
            // three, not two: two cannot tell last-wins from a merge
            let answer (r: Weir.Serve.SReq) : Weir.Serve.SResp =
                let xs =
                    r.Headers
                    |> List.filter (fun (k, _) -> k.ToLowerInvariant() = "x-forwarded-for")
                    |> List.map snd

                {
                    Status = 200
                    Headers = []
                    Body = Weir.Serve.RText(String.concat "," xs)
                }

            let req =
                "GET /d HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Forwarded-For: 10.0.0.1\r\nX-Forwarded-For: 10.0.0.2\r\nX-Forwarded-For: 10.0.0.3\r\n\r\n"

            match wire answer [ req ] with
            | [ r ] -> Expect.isTrue (r.EndsWith "10.0.0.1,10.0.0.2,10.0.0.3") $"all three, in order: {r}"
            | other -> failtest $"{other}"
        }
        test
            "a refused request gets exactly one response; the next request on the connection pairs with its own (H3-E6)" {
            let answer (r: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = []
                Body = Weir.Serve.RText $"<{r.Path.TrimStart('/')}>"
            }

            // a foreign Host and an absolute-form URI naming a foreign
            // host are refused before the handler [D:serve-kestrel]; the
            // connection stays in frame, so the follow-up reads its own
            // answer, never an unsolicited one
            let foreign = "GET /a HTTP/1.1\r\nHost: example.com\r\n\r\n"
            let absForeign = "GET http://evil.example/x HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"

            for bad in [ foreign; absForeign ] do
                match wire answer [ bad; get "GET" "/next" ] with
                | [ r1; r2 ] ->
                    Expect.equal (statusLines r1 |> List.length) 1 $"one response to the refused request: {r1}"
                    Expect.isFalse (r1.StartsWith "HTTP/1.1 200") $"refused, not served: {r1}"
                    Expect.isFalse (r1.Contains "<") $"the handler did not run: {r1}"

                    if r2 <> "" then
                        Expect.equal (statusLines r2) [ "HTTP/1.1 200 OK" ] $"the follow-up gets its own answer: {r2}"
                        Expect.isTrue (r2.EndsWith "<next>") r2
                | other -> failtest $"{other}"

            // a version the server does not speak: one 505, then the
            // connection closes — no unsolicited 200 behind it
            serving (16L * 1024L * 1024L) answer (fun port ->
                use c = new System.Net.Sockets.TcpClient("127.0.0.1", port)
                send c "GET / HTTP/9.9\r\nHost: 127.0.0.1\r\n\r\n"
                let r = readQuiet c

                Expect.equal
                    (statusLines r |> List.map (fun l -> l.Substring(0, 12)))
                    [ "HTTP/1.1 505" ]
                    $"exactly one 505: {r}")

            // the loopback names all reach the handler [D:serve-loopback-names]
            for host in [ "localhost"; "127.0.0.1"; "[::1]" ] do
                serving (16L * 1024L * 1024L) answer (fun port ->
                    use c = new System.Net.Sockets.TcpClient("127.0.0.1", port)
                    send c $"GET /n HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"
                    let r = readQuiet c
                    Expect.isTrue (r.EndsWith "<n>") $"Host: {host} reaches the handler: {r}")
        }
        test "a request over [::1] reaches the handler (H3-A3) [D:serve-loopback-names]" {
            let answer (_: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = []
                Body = Weir.Serve.RText "weir-handler"
            }

            let port = freePort ()
            let h = Weir.Serve.start port (limits 1024L)
            let v6 = h.Bound |> List.contains "::1"
            Weir.Serve.stop h 0

            // a host without IPv6 loopback binds 127.0.0.1 alone
            if v6 then
                serving 1024L answer (fun port ->
                    use c =
                        new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6)

                    c.Connect(System.Net.IPAddress.IPv6Loopback, port)
                    send c $"GET / HTTP/1.1\r\nHost: [::1]:{port}\r\n\r\n"
                    let r = readQuiet c

                    Expect.isTrue
                        (r.StartsWith "HTTP/1.1 200" && r.EndsWith "weir-handler")
                        $"[::1] reaches the handler: {r}")
        }
        test
            "a slow request body is read before the request is accepted — a fast request overtakes it (H3-E5) [D:serve-kestrel]" {
            let answer (r: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = []
                Body = Weir.Serve.RText $"<{r.Path.TrimStart('/')}>"
            }

            // the harness accepts one request at a time, like a single
            // handler slot: a body still arriving must not hold it
            serving 1024L answer (fun port ->
                use slow = new System.Net.Sockets.TcpClient("127.0.0.1", port)
                send slow "POST /slow HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\na"
                System.Threading.Thread.Sleep 300
                let clock = System.Diagnostics.Stopwatch.StartNew()
                use fast = new System.Net.Sockets.TcpClient("127.0.0.1", port)
                send fast (get "GET" "/fast")
                let r = readQuiet fast
                Expect.isTrue (r.EndsWith "<fast>") $"the fast request is served: {r}"
                // readQuiet's own quiet gap is 700ms; waiting out the slow
                // body (bodyTimeout 5000ms, sent 300ms before the clock)
                // measures ~5400ms, so the bound sits between the two and
                // leaves a loaded runner its margin
                Expect.isLessThan clock.ElapsedMilliseconds 4500L "served without waiting out the slow body")
        }
        test "no body on 204, 304 or HEAD — a kept-alive connection stays in frame (H16)" {
            let answer (r: Weir.Serve.SReq) : Weir.Serve.SResp =
                match r.Path with
                | "/nc" -> {
                    Status = 204
                    Headers = []
                    Body = Weir.Serve.RText "LEAK204"
                  }
                | "/nm" -> {
                    Status = 304
                    Headers = []
                    Body = Weir.Serve.RJson "LEAK304"
                  }
                | _ -> {
                    Status = 200
                    Headers = []
                    Body = Weir.Serve.RText "hello"
                  }

            let replies =
                wire answer [ get "GET" "/nc"; get "HEAD" "/x"; get "GET" "/nm"; get "GET" "/x" ]

            match replies with
            | [ nc; head; nm; ok ] ->
                Expect.stringContains nc "204" nc
                Expect.isFalse (nc.Contains "LEAK204") $"204 carries no body: {nc}"
                Expect.isTrue (head.EndsWith "\r\n\r\n") $"HEAD ends at the head: {head}"
                Expect.stringContains head "Content-Length: 5" "HEAD declares the GET length"
                Expect.isFalse (nm.Contains "LEAK304") $"304 carries no body: {nm}"
                Expect.isTrue (ok.StartsWith "HTTP/1.1 200" && ok.EndsWith "hello") $"the next reply is in frame: {ok}"
            | other -> failtest $"four replies expected: {other}"
        }
        test "a handler's framing and hop-by-hop headers are dropped; others pass (H17)" {
            let answer (_: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = [
                    "Transfer-Encoding", "chunked"
                    "content-length", "99"
                    "Connection", "close"
                    "Keep-Alive", "timeout=5"
                    "Upgrade", "h2c"
                    "X-Ok", "kept"
                ]
                Body = Weir.Serve.RText "hello"
            }

            match wire answer [ get "GET" "/a"; get "GET" "/b" ] with
            | [ a; b ] ->
                let lower = a.ToLowerInvariant()

                for bad in
                    [
                        "transfer-encoding"
                        "content-length: 99"
                        "connection: close"
                        "keep-alive"
                        "upgrade"
                    ] do
                    Expect.isFalse (lower.Contains bad) $"'{bad}' is dropped: {a}"

                Expect.stringContains a "Content-Length: 5" "the listener's own length"
                Expect.stringContains a "X-Ok: kept" "an ordinary header passes"
                Expect.isTrue (b.EndsWith "hello") $"the connection stays in frame: {b}"
            | other -> failtest $"two replies expected: {other}"
        }
        test "a Stream element's newlines become data: lines of ONE event — no forged events (H18)" {
            let answer (_: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = []
                Body =
                    Weir.Serve.RStream(
                        seq {
                            "a\n\nevent: evil\ndata: x"
                            "b\r\nc\rd"
                            "e"
                        }
                    )
            }

            match wire answer [ get "GET" "/s" ] with
            | [ r ] ->
                let body =
                    System.Text.RegularExpressions.Regex.Replace(r, "\r\n[0-9a-fA-F]+\r\n", "")

                Expect.stringContains
                    body
                    "data: a\ndata: \ndata: event: evil\ndata: data: x\n\ndata: b\ndata: c\ndata: d\n\ndata: e\n\n"
                    r

                Expect.isFalse (body.Contains "\nevent: evil") "no forged field line"
            | other -> failtest $"one reply expected: {other}"
        }
        test "a Text body leaves as text/plain unless the handler types it [D:serve-text-type]" {
            let get = "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"

            let reply (headers: (string * string) list) (body: Weir.Serve.SBody) =
                wire
                    (fun _ -> {
                        Status = 200
                        Headers = headers
                        Body = body
                    })
                    [ get ]
                |> List.head

            Expect.stringContains
                (reply [] (Weir.Serve.RText "<b>x</b>"))
                "Content-Type: text/plain; charset=utf-8"
                "an untyped Text body is never left for the browser to sniff"

            let typed = reply [ "Content-Type", "text/html" ] (Weir.Serve.RText "<b>x</b>")
            Expect.stringContains typed "Content-Type: text/html" "the handler's type stands"
            Expect.isFalse (typed.Contains "text/plain") "and is the only one"
            Expect.isFalse ((reply [] Weir.Serve.RNoBody).Contains "Content-Type") "no body, no type"
        }
        test "maxBody: a body past the bound is 413 whether declared or chunked; within it reads whole (H20)" {
            let echo (r: Weir.Serve.SReq) : Weir.Serve.SResp = {
                Status = 200
                Headers = []
                Body = Weir.Serve.RText $"got:{r.Body}"
            }

            let post (body: string) =
                $"POST /p HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {body.Length}\r\n\r\n{body}"

            let chunked (body: string) =
                $"POST /p HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\n\r\n{body.Length:x}\r\n{body}\r\n0\r\n\r\n"

            match wireWith 10L echo [ post "0123456789" ] with
            | [ r ] -> Expect.isTrue (r.EndsWith "got:0123456789") $"exactly maxBody reads: {r}"
            | other -> failtest $"{other}"

            match wireWith 10L echo [ post "0123456789X" ] with
            | [ r ] -> Expect.stringContains r "HTTP/1.1 413" r
            | other -> failtest $"{other}"

            match wireWith 10L echo [ chunked "0123456789XYZ" ] with
            | [ r ] -> Expect.stringContains r "HTTP/1.1 413" r
            | other -> failtest $"{other}"

            match wireWith 10L echo [ chunked "abc" ] with
            | [ r ] -> Expect.isTrue (r.EndsWith "got:abc") $"a small chunked body reads: {r}"
            | other -> failtest $"{other}"
        }
        test "maxBody is an optional Size field of the serve config literal (H20)" {
            clean
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; maxBody = 1MiB; handler = h }"
                    "    print \"ok\""
                ]
                "maxBody alone"

            clean
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; bodyTimeout = 2s; maxBody = 64KiB; handler = h }"
                    "    print \"ok\""
                ]
                "both optional fields"

            mustSay
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; maxBody = 5; handler = h }"
                    "    print \"ok\""
                ]
                "Size"
                "maxBody is a Size"
        }
        // ---- F9: the method boundary [D:serve-method] ----
        test "HttpMethod carries a well-formed unlisted verb as Other; an inlined handler routes on it and on Query" {
            // Other is an open-world inbound case; the handler is inlined
            // so its param types from the serve head (params are not typed
            // from patterns — the standing weir rule, not a serve concern)
            clean
                [
                    "within serve srv = { port = Some 80; maxConcurrent = 1; handler = (fun req -> match req.method with | Other v -> HttpServerResponse { status = 405; headers = []; body = Text v } | Query -> HttpServerResponse { status = 200; headers = []; body = Text \"q\" } | _ -> HttpServerResponse { status = 200; headers = []; body = Text \"ok\" }) }"
                    "    print \"ok\""
                ]
                "an inlined handler matching Other and Query"
        }
        test "methodTokenOk accepts well-formed verbs and refuses malformed tokens" {
            // well-formed tchar tokens (listed and unlisted) pass
            for ok in [ "GET"; "QUERY"; "TRACE"; "FROBNICATE"; "M-SEARCH"; "PATCH" ] do
                Expect.isTrue (Weir.Serve.methodTokenOk ok) $"'{ok}' is a well-formed token"

            // control chars, whitespace, and separators are refused — the
            // byte-class refusal at the boundary (400)
            for bad in [ ""; "BAD METH"; "BAD\tX"; "GE\rT"; "GET\n"; "ab"; "GET/x" ] do
                Expect.isFalse (Weir.Serve.methodTokenOk bad) $"'{bad}' is malformed"
        }
        // ---- F3: the header-injection byte class [D:http-header-bytes] ----
        test "headerInjection flags CR/LF/NUL in a header name or value, and passes a clean pair" {
            Expect.isNone (Weir.Http.headerInjection ("X-Benign", "present")) "a clean pair does not inject"

            Expect.equal
                (Weir.Http.headerInjection ("X-Evil", "a\r\nInjected: yes"))
                (Some("CR", "value"))
                "a CR in the value is caught (CR precedes the LF)"

            Expect.equal
                (Weir.Http.headerInjection ("X-Evil", "a\nb"))
                (Some("LF", "value"))
                "a bare LF in the value is caught"

            Expect.equal
                (Weir.Http.headerInjection ("X-Evil", "a b"))
                (Some("NUL", "value"))
                "a NUL in the value is caught"

            Expect.equal
                (Weir.Http.headerInjection ("X-\r\nBad", "v"))
                (Some("CR", "name"))
                "a CR in the name is caught, located to the name"
        }
        test "refuseHeaderInjection raises the located message for the first injecting pair, both directions" {
            let outErr =
                Expect.throwsC
                    (fun () ->
                        Weir.Http.refuseHeaderInjection "request header" failwith [
                            ("X-Ok", "fine")
                            ("X-Evil", "a\r\nInjected: yes")
                        ])
                    (fun ex -> ex.Message)

            Expect.stringContains outErr "request header 'X-Evil'" "names the crossing and header"
            Expect.stringContains outErr "CR byte in its value" "names the byte and position"
            Expect.stringContains outErr "cannot contain CR, LF or NUL" "names the whole byte class"

            let inErr =
                Expect.throwsC
                    (fun () -> Weir.Http.refuseHeaderInjection "response header" failwith [ ("X-Crlf", "a\nb") ])
                    (fun ex -> ex.Message)

            Expect.stringContains inErr "response header 'X-Crlf'" "names the serve crossing and header"
            Expect.stringContains inErr "LF byte in its value" "names the byte"

            Weir.Http.refuseHeaderInjection "request header" failwith [ ("X-A", "1"); ("X-B", "2") ]
        }
        // ---- S1: the cross-origin credential drop [D:secret-redirect] ----
        test "sameOrigin: scheme, host and port must all match; a port change is a different origin" {
            let u s = System.Uri(s: string)

            Expect.isTrue
                (Weir.Http.sameOrigin (u "http://127.0.0.1:8503/a") (u "http://127.0.0.1:8503/b"))
                "same scheme/host/port is one origin (path differs)"

            Expect.isFalse
                (Weir.Http.sameOrigin (u "http://127.0.0.1:8503/a") (u "http://127.0.0.1:8504/a"))
                "a PORT change is a different origin (the F4 case: 8503 -> 8504)"

            Expect.isFalse
                (Weir.Http.sameOrigin (u "http://127.0.0.1:80/a") (u "http://localhost:80/a"))
                "127.0.0.1 and localhost are different origins (host differs)"

            Expect.isFalse
                (Weir.Http.sameOrigin (u "http://example.com/a") (u "https://example.com/a"))
                "a scheme change is a different origin"

            Expect.isTrue
                (Weir.Http.sameOrigin (u "http://EXAMPLE.com/a") (u "http://example.com/b"))
                "host compares case-insensitively"
        }
        test "isRedirect covers the BCL redirect set and nothing else" {
            for code in [ 301; 302; 303; 307; 308 ] do
                Expect.isTrue (Weir.Http.isRedirect code) $"{code} is a redirect"

            for code in [ 200; 201; 204; 300; 304; 400; 404; 500 ] do
                Expect.isFalse (Weir.Http.isRedirect code) $"{code} is not a follow-redirect"
        }
        test "a failing redirect target is the host the error names, for Http.send and the contract fetch" {
            // 127.0.0.1 (healthy) 302s to localhost:1 (nothing listens):
            // the refusal belongs to the target, never the origin
            let port = 8631
            let a = new System.Net.HttpListener()
            a.Prefixes.Add $"http://127.0.0.1:{port}/"
            a.Start()

            let loop =
                System.Threading.Thread(fun () ->
                    try
                        while true do
                            let ctx = a.GetContext()
                            ctx.Response.StatusCode <- 302
                            ctx.Response.Headers.Add("Location", "http://localhost:1/x")
                            ctx.Response.Close()
                    with _ ->
                        ())

            loop.IsBackground <- true
            loop.Start()

            try
                let req: Weir.Http.Req = {
                    Method = "GET"
                    Url = $"http://127.0.0.1:{port}/start"
                    Headers = []
                    SensitiveHeaders = Set.empty
                    Body = None
                    TimeoutMs = 10000L
                    Insecure = false
                }

                match Weir.Http.send System.Threading.CancellationToken.None req with
                | Error(msg, Weir.Http.Refused 1) ->
                    Expect.stringContains msg "localhost:1 refused the connection" "Http.send names the target"
                | other -> failtest $"Http.send should be refused at the target: {other}"

                match Weir.Contracts.fetchBytesWith [] $"http://127.0.0.1:{port}/start" with
                | Error msg ->
                    Expect.stringContains msg "localhost:1 refused the connection" "the contract fetch names the target"
                | Ok _ -> failtest "the contract fetch should be refused at the target"
            finally
                a.Stop()
                (a :> System.IDisposable).Dispose()
        }
        // ---- F12: the request-body read timeout [D:serve-body-timeout] ----
        test "the serve config accepts bodyTimeout, and omitting it stays clean" {
            // omitted — rests at the default, existing scripts unbroken
            clean
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; handler = h }"
                    "    print \"ok\""
                ]
                "bodyTimeout omitted"

            // supplied as a Duration
            clean
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; bodyTimeout = 5s; handler = h }"
                    "    print \"ok\""
                ]
                "bodyTimeout supplied"

            // a non-Duration bodyTimeout is a type error naming Duration
            mustSay
                [
                    okHandler
                    "within serve srv = { port = Some 80; maxConcurrent = 1; bodyTimeout = 5; handler = h }"
                    "    print \"ok\""
                ]
                "Duration"
                "an int bodyTimeout names Duration"
        }
        // ---- F8: the stream-producer failure channel [D:serve-stream] ----
        test "a Stream producer raise records on the handle, not a raise out of the handler" {
            let h = Weir.Serve.start 8207 (limits 1024L)
            Expect.isEmpty (Weir.Serve.streamErrors h) "no errors before a failure"
            Weir.Serve.recordStreamError h "producer died"
            Weir.Serve.recordStreamError h "another"

            Expect.equal
                (Weir.Serve.streamErrors h)
                [ "producer died"; "another" ]
                "the designated channel records failures in order"

            Weir.Serve.stop h 0
        }
        // ---- F8: the client sees the truncation [D:serve-stream-truncate] ----
        test
            "a Stream producer raise ends the chunked body WITHOUT its terminator; a clean stream keeps it [D:serve-stream-truncate]" {
            let stream (lines: seq<string>) : Weir.Serve.SReq -> Weir.Serve.SResp =
                fun _ -> {
                    Status = 200
                    Headers = []
                    Body = Weir.Serve.RStream lines
                }

            let dead =
                wire
                    (stream (
                        seq {
                            yield "first"
                            failwith "producer died"
                        }
                    ))
                    [ get "GET" "/" ]
                |> List.head

            Expect.stringContains dead "data: first" "the element before the raise was sent"
            Expect.isFalse (dead.EndsWith "0\r\n\r\n") $"no terminating chunk after a producer raise: {dead}"

            let whole = wire (stream [ "a"; "b" ]) [ get "GET" "/" ] |> List.head
            Expect.isTrue (whole.EndsWith "0\r\n\r\n") $"a clean stream ends with its terminator: {whole}"
        }
        // ---- F11: an unsendable header is refused by name [D:http-header-unsendable] ----
        test
            "headerUnsendable names a non-token name, a control or non-ASCII value byte; a clean pair passes [D:http-header-unsendable]" {
            Expect.isNone
                (Weir.Http.headerUnsendable ("X-Ok", "a\tb ~"))
                "a token name and a visible-ASCII/tab value pass"

            let reason pair =
                Weir.Http.headerUnsendable pair |> Option.defaultValue "<none>"

            Expect.equal (reason ("", "v")) "its name is empty" "an empty name"
            Expect.stringContains (reason ("Bad Name", "v")) "its name carries a space" "a space in the name"
            Expect.stringContains (reason ("Bad:Name", "v")) "its name carries ':'" "a colon in the name"
            Expect.stringContains (reason ("X", "a\u0007b")) "control character U+0007" "a control byte in the value"
            Expect.stringContains (reason ("X", "café")) "non-ASCII character 'é'" "a non-ASCII value"
        }
        test "a secret header value is located by byte offset, its character never named [D:secret-header-offset]" {
            let secret pair =
                Weir.Http.headerUnsendableWith true pair |> Option.defaultValue "<none>"

            let r = secret ("X-Tok", "aéßb")

            Expect.stringContains
                r
                "a non-ASCII character at byte offset 1 of the secret value"
                "UTF-8 offset of the first offender"

            Expect.isFalse (r.Contains "é" || r.Contains "ß") $"no character of the secret: {r}"

            Expect.stringContains
                (secret ("X-Tok", "ab\u0007"))
                "a control character at byte offset 2 of the secret value"
                "a control byte too"

            Expect.isFalse ((secret ("X-Tok", "ab\u0007")).Contains "U+0007") "the control code is not named"

            Expect.stringContains
                (secret ("X-Tok", "üb\u0001"))
                "control character at byte offset 3"
                "offset counts UTF-8 bytes"

            let refused =
                Expect.throwsC
                    (fun () ->
                        Weir.Http.refuseUnsendableRequestHeader failwith false (Set.ofList [ "x-tok" ]) [
                            ("X-Plain", "ok")
                            ("X-TOK", "sßcret")
                        ])
                    (fun ex -> ex.Message)

            Expect.stringContains refused "request header 'X-TOK'" "the header is named"
            Expect.isFalse (refused.Contains "ß") "the name match is case-insensitive"
        }
        test
            "a request header is refused by name, never skipped — a body header needs a body [D:http-header-unsendable]" {
            let refuse hasBody headers =
                Expect.throwsC
                    (fun () -> Weir.Http.refuseUnsendableRequestHeader failwith hasBody Set.empty headers)
                    (fun ex -> ex.Message)

            Expect.equal
                (refuse false [ ("X-Ok", "1"); ("Bad Name", "v") ])
                "request header 'Bad Name' cannot be sent — its name carries a space, which a header name cannot (letters, digits and !#$%&'*+-.^_`|~ only)"
                "a non-token name is refused, named"

            Expect.equal
                (refuse false [ ("Content-Type", "application/json") ])
                "request header 'Content-Type' cannot be sent — it is a body header and this request has no body (give the request a body or drop the header)"
                "a body header on a bodyless request has no place"

            // with a body the content header lands on the content
            Weir.Http.refuseUnsendableRequestHeader failwith true Set.empty [
                ("Content-Type", "application/json")
                ("Expires", "0")
            ]

            Weir.Http.refuseUnsendableRequestHeader failwith false Set.empty [ ("X-A", "1"); ("Host", "h") ]
        }
        test "a response header the listener cannot send is refused by name; framing headers stay the listener's" {
            let refused headers =
                try
                    Weir.Serve.refuseUnsendableResponseHeaders headers
                    None
                with Weir.Serve.ResponseHeaderRefused m ->
                    Some m

            Expect.equal
                (refused [ ("X-Ok", "1"); ("Bad:Name", "v") ])
                (Some
                    "response header 'Bad:Name' cannot be sent — its name carries ':', which a header name cannot (letters, digits and !#$%&'*+-.^_`|~ only)")
                "a non-token name is refused, named"

            Expect.isSome (refused [ ("X-U", "café") ]) "a non-ASCII value is refused"

            Expect.isNone
                (refused [
                    ("WWW-Authenticate", "Basic realm=x")
                    ("Keep-Alive", "\u0007")
                    ("Content-Type", "text/plain")
                ])
                "a sendable header passes; a framing header is dropped by design, not refused"
        }
    ]

// validated types [D:validated-types]: a single-case union's `validate`
// block is the type's one gate — `T.create` builds, the case only reads,
// and every wire boundary runs the block
let validatedTypeTests =
    let email = [
        "type Email = Email of string"
        "    /// must hold an @"
        "    validate s ="
        "        if not (Str.contains \"@\" s) then fail $\"not an email: {s}\""
    ]

    let emailModule = [ "module Mail" ] @ email

    let errs lines = errorMsgsIn "v.weir" lines

    let says lines (needle: string) label =
        let es = errs lines
        Expect.exists es (fun (m: string) -> m.Contains needle) $"{label}: expected '{needle}', got {es}"

    let clean lines label =
        Expect.isEmpty (errs lines) $"{label}: {errs lines}"

    let withFiles (files: (string * string list) list) (f: string -> unit) =
        withTempDir "weir-vt" (fun td ->
            for name, lines in files do
                File.WriteAllLines(Path.Combine(td, name), lines)

            f td)

    let errsAt (td: string) (entry: string) =
        let path = Path.Combine(td, entry)
        errorMsgsIn path (List.ofArray (File.ReadAllLines path))

    // a self-checking script: it `fail`s on any wrong value
    let runsIn (files: (string * string list) list) (entry: string) (args: string list) label =
        withFiles files (fun td ->
            Expect.isEmpty (errsAt td entry) $"{label}: checks clean, got {errsAt td entry}"
            Expect.equal (Weir.Script.run (Path.Combine(td, entry)) args) 0 $"{label}: runs clean")

    let runs lines label =
        runsIn [ "v.weir", lines ] "v.weir" [] label

    let expectIs (expr: string) (expected: string) =
        $"if {expr} <> {expected} then fail (show ({expr}))"

    // a Result whose Ok side may not compare (a seq, a Map): its Error text
    let expectErr (expr: string) (expected: string) =
        $"match {expr} with | Error m when m == {expected} -> () | other -> fail (show other)"

    testSequenced
    <| testList "validated types [D:validated-types]" [
        test "the validate clause parses onto the declaration; 'validate' stays an ordinary name" {
            match Weir.Parser.parseLine cmdResolver "type S = S of int validate x = ()" with
            | Ok(SType d as st) ->
                Expect.isSome d.Validate "the clause rides the declaration"
                Expect.stringContains (sexprStmt st) "(stype S (validate x" "the sexpr carries the validator"
            | other -> failtest $"expected SType, got {other}"

            clean [ "let validate x = x + 1"; "print $\"{validate 1}\"" ] "a function named validate"
        }
        test
            "create stores the payload exactly as given; a refusal raises the validator's message, try result keeps it intact" {
            runs
                (email
                 @ [
                     "let (Email raw) = Email.create \"A@B.C\""
                     "if raw <> \"A@B.C\" then fail raw"
                     expectIs "(Email.create \"nope\" |> try result)" "Error \"not an email: nope\""
                     expectIs "(Email.create \"nope\" |> try)" "None"
                     "let all = [\"x@y\"; \"z\"] |> Seq.map (fun s -> Email.create s |> try result) |> Seq.map Result.isOk |> Seq.freeze"
                     expectIs "Seq.length (all |> Seq.where id)" "1"
                 ])
                "create"

            withFiles
                [
                    "v.weir", email @ [ "let ok = Email.create \"a@b\""; "let bad = Email.create \"nope\"" ]
                ]
                (fun td ->
                    Expect.notEqual
                        (Weir.Script.run (Path.Combine(td, "v.weir")) [])
                        0
                        "an uncaught refusal fails the script")
        }
        test
            "any raise inside validate refuses with its message — fail, a failing builtin, orFail; boundaries report it as their own" {
            runs
                [
                    "type Pos = Pos of string"
                    "    validate s = if Str.toInt s <= 0 then fail \"not positive\""
                    "type Dig = Dig of string"
                    "    validate s = Str.tryToInt s |> orFail \"not digits\" |> fun _ -> ()"
                    "type Row = { p: Pos }"
                    expectIs "(Pos.create \"0\" |> try result)" "Error \"not positive\""
                    expectIs "(Pos.create \"x\" |> try result)" "Error \"toInt: not an integer: \\\"x\\\"\""
                    expectIs "(Dig.create \"x\" |> try result)" "Error \"not digits\""
                    "let r = [\"{\\\"p\\\": \\\"x\\\"}\"] |> from json Row |> try result"
                    expectErr "r" "\"from json: p: toInt: not an integer: \\\"x\\\"\""
                ]
                "raising validators"

            says
                [ "type X = X of int"; "    validate n = if n <= 0 then exit 3" ]
                "'validate' must be pure"
                "exit is no refusal: the purity check refuses it"
        }
        test "the case never constructs — in the declaring module, outside it, as a value; located at the use" {
            match errorsIn "v.weir" (email @ [ "let e = Email \"x\"" ]) with
            | d :: _ ->
                Expect.equal
                    d.Message
                    "'Email' is validated — build it with Email.create (it raises when validate refuses; `|> try result` for a Result)"
                    "the teaching"

                Expect.equal (d.Line, d.Col) (5, 9) "located at the use"
            | [] -> failtest "the constructor must refuse"

            says (email @ [ "let f = [\"a\"] |> Seq.map Email" ]) "build it with Email.create" "as a function value"

            says
                (email @ [ "type Wrap = Wrap of Email"; "let w = Wrap (Email \"x\")" ])
                "build it with Email.create"
                "nested in another constructor"

            withFiles
                [
                    "mail.weir", emailModule @ [ "let mk : string -> Email"; "let mk s = Email s" ]
                    "ok.weir", emailModule @ [ "let mk : string -> Email"; "let mk s = Email.create s" ]
                    "use.weir", [ "import \"./ok.weir\" as Mail"; "let e = Mail.Email \"a@b\"" ]
                ]
                (fun td ->
                    Expect.exists
                        (errsAt td "mail.weir")
                        (fun m -> m.Contains "'Email' is validated — build it with Email.create")
                        "inside the declaring module"

                    Expect.exists
                        (errsAt td "use.weir")
                        (fun m ->
                            m =
                                "'Mail.Email' is validated — build it with Mail.Email.create (it raises when validate refuses; `|> try result` for a Result)")
                        "qualified, from an importer")
        }
        test "validate belongs to a single-case union with a payload — elsewhere a teaching error" {
            says
                [ "type R = { a: int }"; "    validate r = ()" ]
                "'validate' guards a single-case union (type Email = Email of string), not a record"
                "a record"

            says
                [ "type U = A of int | B"; "    validate x = ()" ]
                "'U' has 2 cases; a union of several cases composes validated payloads"
                "multi-case"

            says
                [ "type U = A"; "    validate x = ()" ]
                "'validate' checks a payload, and case 'A' carries none"
                "no payload"

            says [ "type G<'a> = G of 'a"; "    validate x = ()" ] "a validated type is monomorphic" "generic"

            says
                [ "[<Tag \"kind\">]"; "type T = T of int"; "    validate x = ()" ]
                "[<Tag>] and 'validate' do not combine"
                "tagged"
        }
        test "a binder or return mismatch names both types" {
            says
                [ "type S = S of int"; "    validate x = if x > 0 then \"x\" else fail \"n\"" ]
                "expected unit, got string"
                "return"

            says
                [ "type S = S of int"; "    validate x = Some x" ]
                "expected unit, got Option<int>"
                "not a Result at all"

            says
                [ "type S = S of int"; "    validate (a, b) = ()" ]
                "'validate' binds S's payload, which is int, but this binder is a 2-tuple pattern"
                "binder"
        }
        test "validate is pure: an effect refuses at the offending call, a helper's too; create is pure" {
            match
                errorsIn "v.weir" [
                    "type D = D of string"
                    "    validate p = if not (Dir.exists p) then fail \"no\""
                ]
            with
            | d :: _ ->
                Expect.stringStarts d.Message "'validate' must be pure" "the teaching"
                Expect.stringContains d.Message "'Dir.exists' reads the filesystem" "names the offender"
                Expect.equal (d.Line, d.Col) (2, 26) "located at the call"
            | [] -> failtest "an effectful validator must refuse"

            says
                [
                    "let leak s = File.exists s"
                    "type P = P of string"
                    "    validate s = if not (leak s) then fail \"x\""
                ]
                "'leak' reaches an effect"
                "through a helper"

            clean
                [
                    "let norm s = Str.toLower s"
                    "type P = P of string"
                    "    validate s = if norm s == \"\" then fail \"empty\""
                    "let pure mk s = P.create s"
                    "print $\"{mk \"A\"}\""
                ]
                "a pure helper inside, create inside a pure function"
        }
        test "validate cannot call its own create" {
            says
                [
                    "type S = S of int"
                    "    validate x = if x > 0 then () else S.create (x + 1) |> fun _ -> ()"
                ]
                "'validate' cannot call S.create — that is recursion"
                "self-call"
        }
        test "patterns read the value: match, let, parameters, lambdas" {
            runs
                (email
                 @ [
                     "let e = Email.create \"q@w\""
                     "let (Email a) = e"
                     "let f (Email b) = b"
                     "let g = fun (Email c) -> c"
                     "let d = match e with | Email d -> d"
                     "if a + f e + g e + d <> \"q@wq@wq@wq@w\" then fail a"
                 ])
                "reading"
        }
        test "composition: a multi-case union and a record of validated payloads; a nested type takes the inner TYPE" {
            let work =
                email
                @ [
                    "type Phone = Phone of string"
                    "    validate s = if not (Str.startsWith \"+\" s) then fail $\"not a phone: {s}\""
                    "type Contact ="
                    "    | Mail of Email"
                    "    | Call of Phone"
                    "type Card = { who: string; reach: Contact }"
                    "type Work = Work of Email"
                    "    validate e ="
                    "        let (Email raw) = e"
                    "        if not (Str.endsWith \"@corp\" raw) then fail $\"not a work address: {raw}\""
                ]

            runs
                (work
                 @ [
                     "let card = { who = \"a\"; reach = Call (Phone.create \"+1\") }"
                     "let shown ="
                     "    match card.reach with"
                     "    | Mail (Email e) -> e"
                     "    | Call (Phone p) -> p"
                     "if shown <> \"+1\" then fail shown"
                     "let (Work (Email w)) = Work.create (Email.create \"X@corp\")"
                     "if w <> \"X@corp\" then fail w"
                     expectIs
                         "(Work.create (Email.create \"x@home\") |> try result)"
                         "Error \"not a work address: x@home\""
                 ])
                "composition"

            says
                (work @ [ "let w = Work.create \"x@corp\"" ])
                "expected Email, got string"
                "create takes the inner type"
        }
        test
            "json/jsonl: fields, seq elements, Option, Map values, anonymous shapes and the document validate, naming the path" {
            runs
                (email
                 @ [
                     "type User = { name: string; email: Email; cc: Option<Email> }"
                     "let u = [\"{\\\"name\\\": \\\"a\\\", \\\"email\\\": \\\"Q@W.E\\\"}\"] |> from json User"
                     "let (Email got) = u.email"
                     "if got <> \"Q@W.E\" then fail got"
                     "let deep = [\"{\\\"users\\\": [{\\\"name\\\": \\\"a\\\", \\\"email\\\": \\\"x\\\"}]}\"] |> from json {| users: seq<User> |} |> try result"
                     expectErr "deep" "\"from json: users[0].email: not an email: x\""
                     "let opt = [\"{\\\"name\\\": \\\"a\\\", \\\"email\\\": \\\"a@b\\\", \\\"cc\\\": \\\"c\\\"}\"] |> from json User |> try result"
                     expectErr "opt" "\"from json: cc: not an email: c\""
                     "let doc = [\"[\\\"M@N\\\", \\\"bad\\\"]\"] |> from json seq<Email> |> try result"
                     expectErr "doc" "\"from json: [1]: not an email: bad\""
                     "let one = [\"\\\"M@N\\\"\"] |> from json Email"
                     "let (Email m) = one"
                     "if m <> \"M@N\" then fail m"
                     "let keyed = [\"{\\\"k\\\": \\\"bad\\\"}\"] |> from json Map<string, Email> |> try result"
                     expectErr "keyed" "\"from json: [\\\"k\\\"]: not an email: bad\""
                     "let lines = [\"{\\\"name\\\": \\\"a\\\", \\\"email\\\": \\\"x\\\"}\"] |> from jsonl User |> Seq.freeze |> try result"
                     expectErr "lines" "\"from jsonl: email: not an email: x\""
                 ])
                "json"
        }
        test "yaml and toml validate, naming the line and the path" {
            runs
                (email
                 @ [
                     "type User = { name: string; email: Email }"
                     "let ys = [\"users:\"; \"  - name: a\"; \"    email: A@B\"; \"  - name: b\"; \"    email: zz\"] |> from yaml {| users: seq<User> |} |> try result"
                     expectErr "ys" "\"from yaml: line 5: users[1].email: not an email: zz\""
                     "let ok = [\"name: a\"; \"email: A@B\"] |> from yaml User"
                     "let (Email e) = ok.email"
                     "if e <> \"A@B\" then fail e"
                     "let tl = [\"name = \\\"a\\\"\"; \"email = \\\"bad\\\"\"] |> from toml User |> try result"
                     expectErr "tl" "\"from toml: line 2: email: not an email: bad\""
                 ])
                "yaml/toml"
        }
        test "to json / to yaml write the payload as read: a round trip is byte-identical" {
            runs
                (email
                 @ [
                     "type User = { name: string; email: Email }"
                     "let u = [\"{\\\"name\\\": \\\"a\\\", \\\"email\\\": \\\"Q@W\\\"}\"] |> from json User"
                     expectIs
                         "(u |> to json |> Seq.exactlyOne)"
                         "\"{\\\"name\\\":\\\"a\\\",\\\"email\\\":\\\"Q@W\\\"}\""
                     expectIs "(u |> to yaml |> Str.join \"|\")" "\"name: a|email: Q@W\""
                     "let wire = \"{\\\"name\\\":\\\" a \\\",\\\"email\\\":\\\"Mixed.Case@Example.ORG\\\"}\""
                     expectIs "([wire] |> from json User |> to json |> Seq.exactlyOne)" "wire"
                     expectIs "(([u] |> to json |> from json seq<User> |> Seq.exactlyOne) == u)" "true"
                 ])
                "writers"
        }
        test "Args.load parses the payload then validates, naming the flag; a Default validates too" {
            let cli =
                email
                @ [
                    "type Cli = {"
                    "    recipient: Email"
                    "    cc: Option<Email>"
                    "    [<Default \"d@x\">]"
                    "    sender: Email"
                    "}"
                ]

            runsIn
                [
                    "v.weir",
                    cli
                    @ [
                        "let c = Args.load Cli"
                        "let (Email r) = c.recipient"
                        "let (Email s) = c.sender"
                        "if r + s <> \"A@Bd@x\" || c.cc <> None then fail r"
                    ]
                ]
                "v.weir"
                [ "--recipient"; "A@B" ]
                "good flags"

            runsIn
                [
                    "v.weir",
                    cli
                    @ [
                        "let c = Args.load Cli |> try result"
                        expectErr
                            "c"
                            "\"v.weir: --recipient: not an email: x\\nv.weir: --cc: not an email: y\\n  -r, --recipient <Email>     required\\n  -c, --cc <Email>            optional\\nrun 'v.weir --help' for usage\""
                    ]
                ]
                "v.weir"
                [ "--recipient"; "x"; "--cc"; "y" ]
                "a bad flag names it"

            runsIn
                [
                    "v.weir",
                    email
                    @ [
                        "type Cli = { [<Default \"bad\">] sender: Email }"
                        "let c = Args.load Cli |> try result"
                        expectErr
                            "c"
                            "\"v.weir: --sender: not an email: bad\\n  -s, --sender <Email>        default: bad\\nrun 'v.weir --help' for usage\""
                    ]
                ]
                "v.weir"
                []
                "a Default literal validates"
        }
        test "Env.load parses the payload then validates, naming the variable" {
            runs
                [
                    "type Port = Port of int"
                    "    validate p = if not (p > 0 && p < 65536) then fail $\"{p} is no port\""
                    "within env VT_PORT=0 VT_P2=99999"
                    "    let e = Env.load {| VT_PORT: Port; VT_P2: Option<Port> |} |> try result"
                    "    "
                    + expectErr
                        "e"
                        "\"Env.load {| VT_P2: Option<Port>; VT_PORT: Port |}: VT_P2: 99999 is no port; VT_PORT: 0 is no port\""
                    "within env VT_PORT=8080"
                    "    let e = Env.load {| VT_PORT: Port |}"
                    "    let (Port p) = e.VT_PORT"
                    "    if p <> 8080 then fail $\"{p}\""
                ]
                "env"
        }
        test "a flat boundary refuses a validated field over an optional payload, at check time" {
            let optPort = [ "type Port = Port of Option<int>"; "    validate p = ()" ]

            let refusal (field: string) (ty: string) (innermost: string) =
                $"field '{field}': '{ty}' is validated over an optional payload — a flag/variable cannot tell \"absent\" from \"absent payload\"; declare the field `Option<{ty}>` over a validated `{innermost} of int` instead"

            says
                (optPort
                 @ [ "type Cli = { port: Port }"; "let c = Args.load Cli"; "print $\"{c}\"" ])
                (refusal "port" "Port" "Port")
                "Args.load"

            says
                (optPort
                 @ [
                     "type Cli = { port: Option<Port> }"
                     "let c = Args.load Cli"
                     "print $\"{c}\""
                 ])
                (refusal "port" "Port" "Port")
                "Args.load, an Option field"

            says
                (optPort
                 @ [
                     "type Run = { port: Port }"
                     "type Cmd = Run of Run | Stop"
                     "let c = Args.load Cmd"
                     "print $\"{c}\""
                 ])
                ("case 'Run': " + refusal "port" "Port" "Port")
                "Args.load, a subcommand's field"

            says
                (optPort @ [ "let e = Env.load {| VT_P: Port |}"; "print $\"{e}\"" ])
                (refusal "VT_P" "Port" "Port")
                "Env.load"

            says
                (optPort
                 @ [
                     "type Cfg = { VT_P: Option<Port> }"
                     "let e = Env.load Cfg"
                     "print $\"{e}\""
                 ])
                (refusal "VT_P" "Port" "Port")
                "Env.load, an Option field"

            let nested = optPort @ [ "type Admin = Admin of Port"; "    validate p = ()" ]

            says
                (nested
                 @ [ "type Cli = { port: Admin }"; "let c = Args.load Cli"; "print $\"{c}\"" ])
                (refusal "port" "Admin" "Port")
                "nested, Args.load"

            says
                (nested @ [ "let e = Env.load {| VT_P: Admin |}"; "print $\"{e}\"" ])
                (refusal "VT_P" "Admin" "Port")
                "nested, Env.load"

            clean
                (optPort @ [ "let p = [\"null\"] |> from json Port"; "print $\"{p}\"" ])
                "from json keeps an optional payload"
        }
        test "flat boundaries: a scalar payload, Option over it (absent never runs create), validated over validated" {
            let strict = [ "type Strict = Strict of int"; "    validate n = fail $\"ran on {n}\"" ]

            runsIn
                [
                    "v.weir",
                    strict
                    @ [
                        "type Cli = { port: Option<Strict> }"
                        "let c = Args.load Cli"
                        "if c.port <> None then fail \"some\""
                    ]
                ]
                "v.weir"
                []
                "Args: an absent Option field is None, create unrun"

            runsIn
                [
                    "v.weir",
                    strict
                    @ [
                        "type Cli = { port: Option<Strict> }"
                        "let c = Args.load Cli |> try result"
                        expectErr
                            "c"
                            "\"v.weir: --port: ran on 3\\n  -p, --port <Strict>         optional\\nrun 'v.weir --help' for usage\""
                    ]
                ]
                "v.weir"
                [ "--port"; "3" ]
                "Args: a present Option field runs create"

            runs
                (strict
                 @ [
                     "within env VT_S=3"
                     "    let e = Env.load {| VT_S: Option<Strict>; VT_NONE_SET: Option<Strict> |} |> try result"
                     "    "
                     + expectErr
                         "e"
                         "\"Env.load {| VT_NONE_SET: Option<Strict>; VT_S: Option<Strict> |}: VT_S: ran on 3\""
                     "let none = Env.load {| VT_NONE_SET: Option<Strict> |}"
                     "if none.VT_NONE_SET <> None then fail \"some\""
                 ])
                "Env: absent is None, present runs create"

            let admin = [
                "type Port = Port of int"
                "    validate p = if p <= 0 then fail $\"{p} is no port\""
                "type Admin = Admin of Port"
                "    validate p ="
                "        let (Port n) = p"
                "        if n >= 1024 then fail $\"not privileged: {n}\""
                "type Cli = { port: Admin; spare: Option<Admin> }"
            ]

            runsIn
                [
                    "v.weir",
                    admin
                    @ [
                        "let c = Args.load Cli"
                        "let (Admin (Port n)) = c.port"
                        "if n <> 80 || c.spare <> None then fail $\"{n}\""
                    ]
                ]
                "v.weir"
                [ "--port"; "80" ]
                "nested scalar bottoms build"

            runsIn
                [
                    "v.weir",
                    admin
                    @ [
                        "let c = Args.load Cli |> try result"
                        expectErr
                            "c"
                            "\"v.weir: --port: 0 is no port\\nv.weir: --spare: not privileged: 8080\\n  -p, --port <Admin>          required\\n  -s, --spare <Admin>         optional\\nrun 'v.weir --help' for usage\""
                    ]
                ]
                "v.weir"
                [ "--port"; "0"; "--spare"; "8080" ]
                "nested: the inner create runs first, then the outer"

            runs
                (admin
                 @ [
                     "within env VT_A=8080"
                     "    let e = Env.load {| VT_A: Admin |} |> try result"
                     "    "
                     + expectErr "e" "\"Env.load {| VT_A: Admin |}: VT_A: not privileged: 8080\""
                 ])
                "nested, Env.load"
        }
        test "a [<Tag>] name never reads as a validated entry: unusual tag fields write and read back" {
            for tag in [ "|valid"; "|"; "|validName"; "|create:0" ] do
                let quoted = tag.Replace("\"", "\\\"")

                runs
                    (email
                     @ [
                         "type Body = { text: Email }"
                         $"[<Tag \"{quoted}\">] type Ev = Start of Body | Stop"
                         "let e = Start { text = Email.create \"A@B\" }"
                         expectIs
                             "(e |> to json |> Seq.exactlyOne)"
                             $"\"{{\\\"{quoted}\\\":\\\"Start\\\",\\\"text\\\":\\\"A@B\\\"}}\""
                         expectIs "(([e] |> to json |> from json seq<Ev> |> Seq.exactlyOne) == e)" "true"
                         expectIs "((e |> to yaml |> from yaml Ev) == e)" "true"
                         expectIs "(([Stop] |> to json |> from json seq<Ev> |> Seq.exactlyOne) == Stop)" "true"
                         expectIs "((Stop |> to yaml |> from yaml Ev) == Stop)" "true"
                     ])
                    $"tag field '{tag}'"
        }
        test "the writer's case table: a validated case and a tagged case of one name refuse; two validated share" {
            let other = [
                "module Other"
                "type Email = Email of int"
                "    validate n = if n <= 0 then fail \"neg\""
            ]

            withFiles
                [
                    "mail.weir", emailModule
                    "other.weir", other
                    "clash.weir",
                    [
                        "import \"./mail.weir\" as A"
                        "type Body = { who: A.Email }"
                        "[<Tag \"|valid\">] type Ev = Email of Body | Stop"
                        "Stop |> to json |> Seq.iter print"
                    ]
                    "share.weir",
                    [
                        "import \"./mail.weir\" as A"
                        "import \"./other.weir\" as B"
                        "type Pair = { a: A.Email; b: B.Email }"
                        "let p = [\"{\\\"a\\\": \\\"x@y\\\", \\\"b\\\": 3}\"] |> from json Pair"
                        "p |> to json |> Seq.iter print"
                    ]
                ]
                (fun td ->
                    let es = errsAt td "clash.weir"

                    Expect.exists
                        es
                        (fun (m: string) -> m.Contains "case 'Email' belongs to tagged unions")
                        $"a tagged '|valid' case sharing a validated case's name refuses, got {es}"

                    Expect.isEmpty (errsAt td "share.weir") "two validated types share a case name")
        }
        test "qualified access from another module; an ambiguous bare create names both spellings" {
            let other = [
                "module Other"
                "type Email = Email of int"
                "    validate n = if n <= 0 then fail \"neg\""
            ]

            runsIn
                [
                    "mail.weir",
                    emailModule
                    @ [
                        "type Contact = { name: string; email: Email }"
                        "let mk : string -> Email"
                        "let mk s = Email.create s"
                    ]
                    "use.weir",
                    [
                        "import \"./mail.weir\""
                        "let (Mail.Email raw) = Mail.Email.create \"X@Y\""
                        "if raw <> \"X@Y\" then fail raw"
                        "let (Email bare) = Email.create \"Z@W\""
                        "if bare <> \"Z@W\" then fail bare"
                        expectIs "(Mail.mk \"nope\" |> try result)" "Error \"not an email: nope\""
                        "let c = [\"{\\\"name\\\": \\\"n\\\", \\\"email\\\": \\\"bad\\\"}\"] |> from json Mail.Contact |> try result"
                        expectErr "c" "\"from json: email: not an email: bad\""
                    ]
                ]
                "use.weir"
                []
                "qualified and bare"

            withFiles
                [
                    "mail.weir", emailModule
                    "other.weir", other
                    "amb.weir",
                    [
                        "import \"./mail.weir\" as A"
                        "import \"./other.weir\" as B"
                        "let x = Email.create \"a@b\""
                    ]
                    "two.weir",
                    [
                        "import \"./mail.weir\" as A"
                        "import \"./other.weir\" as B"
                        "let x = A.Email.create \"a@b\""
                        "let y = B.Email.create 3"
                        "print $\"{x} {y}\""
                    ]
                ]
                (fun td ->
                    Expect.contains
                        (errsAt td "amb.weir")
                        "'Email.create' is ambiguous — more than one import declares 'Email'; write 'A.Email.create' or 'B.Email.create'"
                        "ambiguous bare create"

                    Expect.isEmpty (errsAt td "two.weir") "each reached qualified")
        }
        test "the unused-binding law sees a validator's reads" {
            clean
                [
                    "let norm s = Str.toLower s"
                    "type P = P of string"
                    "    validate s = if norm s == \"\" then fail \"empty\""
                    "print $\"{P.create \"A\"}\""
                ]
                "a helper only the validator reads"
        }
        test "hover shows the validate doc on the type and on T.create" {
            let lines = [
                "/// an address"
                "type Email = Email of string"
                "    /// must hold an @"
                "    validate s = if not (Str.contains \"@\" s) then fail \"no\""
                "let e = Email.create \"a@b\""
                "print $\"{e}\""
            ]

            match Weir.Lsp.hoverType lines 2 7 with
            | Some h ->
                Expect.stringStarts h "Email of string  (validated)" "the type, badged"
                Expect.stringContains h "an address" "the type's own doc"
                Expect.stringContains h "validate — must hold an @" "the validate doc"
            | None -> failtest "the type hovers"

            match Weir.Lsp.hoverType lines 5 16 with
            | Some h ->
                Expect.stringStarts h "Email.create : string -> Email" "create's signature"

                Expect.stringContains
                    h
                    "Raises validate's message when it refuses; `|> try result` for a Result."
                    "the raise note"

                Expect.stringContains h "validate — must hold an @" "the validate doc"
            | None -> failtest "create hovers"
        }
        test "a signature field whose TYPE name says path/file/dir completes paths" {
            withFiles
                [
                    "vt.weir",
                    [
                        "module Vt"
                        "type ConfigFile = ConfigFile of string"
                        "    validate s = ()"
                        "type Cmd = {"
                        "    conf: ConfigFile"
                        "    label: string"
                        "}"
                    ]
                ]
                (fun td ->
                    let path = Path.Combine(td, "vt.weir")

                    match
                        Weir.Sigs.loadSigs (path + ".from") [
                            {
                                Tool = "vt"
                                Override = Some path
                                Line = 1
                            }
                        ]
                    with
                    | [], [ si ] ->
                        Expect.equal
                            (Weir.Sigs.SigComplete.complete si " --conf " "")
                            Weir.Sigs.SigPaths
                            "ConfigFile → paths"

                        Expect.equal
                            (Weir.Sigs.SigComplete.complete si " --label " "")
                            Weir.Sigs.SigNothing
                            "string → nothing"
                    | ds, _ -> failtest $"the sig did not load: {ds}")
        }
    ]

// qualified type names [D:qualified-type-names]: two imports may declare
// one type name; each is reached as `A.T`, a bare name resolves while one
// import provides it, and identity follows the declaring module
let qualifiedTypeNameTests =
    let withDir (files: (string * string list) list) (f: string -> unit) =
        withTempDir "weir-qtn" (fun td ->
            for name, lines in files do
                System.IO.File.WriteAllLines(System.IO.Path.Combine(td, name), lines)

            f td)

    let errs (td: string) (entry: string) =
        let path = System.IO.Path.Combine(td, entry)

        diagsIn path (List.ofArray (System.IO.File.ReadAllLines path))
        |> List.filter (fun d -> d.Severity = "error")
        |> List.map _.Message

    let clean td entry label =
        Expect.isEmpty (errs td entry) $"{label}: expected clean, got {errs td entry}"

    let says td entry (needle: string) label =
        let es = errs td entry
        Expect.exists es (fun m -> m.Contains needle) $"{label}: expected '{needle}', got {es}"

    // the starship shape (tmp/starship-repro/c5)
    let a = [
        "module"
        ""
        "type LangDefaults = { symbol: string }"
        ""
        "type Shape ="
        "    | Circle of int"
        "    | Square of int"
        ""
        "let make : string -> LangDefaults"
        "let make s = { symbol = s }"
        ""
        "let area : Shape -> int"
        "let area s ="
        "    match s with"
        "    | Circle r -> r * 3"
        "    | Square w -> w * w"
    ]

    let b = [
        "module"
        ""
        "type LangDefaults = { symbol: string; style: string }"
        ""
        "type Shape ="
        "    | Circle of int"
        "    | Tri of int"
        ""
        "let make : string -> LangDefaults"
        "let make s = { symbol = s; style = \"bold\" }"
    ]

    let both = [ "import \"a.weir\" as A"; "import \"b.weir\" as B" ]

    let tree (extra: (string * string list) list) f =
        withDir ([ "a.weir", a; "b.weir", b ] @ extra) f

    testList "qualified type names [D:qualified-type-names]" [
        test "two imports declaring one type name check clean (the c5 repro)" {
            tree
                [
                    "root.weir",
                    both
                    @ [
                        "let x = A.make \"a\""
                        "let y = B.make \"b\""
                        "print $\"{x.symbol} {y.symbol} {y.style}\""
                    ]
                    "root2.weir",
                    both
                    @ [
                        "let x = A.LangDefaults { symbol = \"a\" }"
                        "let y = B.LangDefaults { symbol = \"b\"; style = \"s\" }"
                        "print $\"{x.symbol} {y.style}\""
                    ]
                ]
                (fun td ->
                    clean td "root.weir" "members returning each type"
                    clean td "root2.weir" "qualified literals")
        }
        test "the qualified name works in every type position" {
            tree
                [
                    "m.weir",
                    [ "module M" ]
                    @ both
                    @ [
                        "type Both = { a: A.LangDefaults; b: seq<B.LangDefaults> }"
                        "type Pick ="
                        "    | First of A.LangDefaults"
                        "    | Second of B.LangDefaults"
                        "let pair : string -> Both"
                        "let pair s = { a = A.make s; b = [ B.make s ] }"
                        "let name : B.Shape -> string"
                        "let name s ="
                        "    match s with"
                        "    | Circle r -> $\"c{r}\""
                        "    | Tri t -> $\"t{t}\""
                    ]
                    "slot.weir",
                    both
                    @ [
                        "let one = [\"{\\\"symbol\\\": \\\"s\\\"}\"] |> from json A.LangDefaults"
                        "let many = [\"[]\"] |> from json seq<B.LangDefaults>"
                        "let keyed = [\"{}\"] |> from json Map<string, A.LangDefaults>"
                        "let anon = [\"{\\\"c\\\": {\\\"symbol\\\": \\\"w\\\"}}\"] |> from json {| c: A.LangDefaults |}"
                        "let y = [\"symbol: a\"; \"style: b\"] |> from yaml B.LangDefaults"
                        "print $\"{one.symbol} {many |> Seq.length} {Map.count keyed} {anon.c.symbol} {y.style}\""
                    ]
                    "cases.weir",
                    both
                    @ [
                        "let n = A.area (A.Circle 2)"
                        "let t ="
                        "    match B.Tri 3 with"
                        "    | Circle r -> r"
                        "    | Tri t -> t"
                        "print $\"{n} {t}\""
                    ]
                    "load.weir",
                    both
                    @ [
                        "let e = Env.load B.LangDefaults"
                        "let c = Args.load A.LangDefaults"
                        "print $\"{e.style} {c.symbol}\""
                    ]
                ]
                (fun td ->
                    clean td "m.weir" "record fields, union payloads, signatures, patterns"
                    clean td "slot.weir" "the adapter slot, wrapped and nested"
                    clean td "cases.weir" "union cases construct qualified, patterns match bare"
                    clean td "load.weir" "Env.load / Args.load")
        }
        test "a bare name two imports declare is an error naming both spellings" {
            let ambiguous =
                "type 'LangDefaults' is ambiguous — more than one import declares it; write the qualified name: 'A.LangDefaults' or 'B.LangDefaults'"

            tree
                [
                    "lit.weir", both @ [ "let x = LangDefaults { symbol = \"a\" }"; "print x.symbol" ]
                    "slot.weir", both @ [ "let x = [\"{}\"] |> from json LangDefaults"; "print x.symbol" ]
                    "sig.weir",
                    [ "module S" ]
                    @ both
                    @ [ "let f : LangDefaults -> string"; "let f d = d.symbol" ]
                    "field.weir", [ "module F" ] @ both @ [ "type W = { d: LangDefaults }" ]
                    "load.weir", both @ [ "let e = Env.load LangDefaults"; "print e.symbol" ]
                ]
                (fun td ->
                    for f in [ "lit.weir"; "slot.weir"; "sig.weir"; "field.weir"; "load.weir" ] do
                        says td f ambiguous f)
        }
        test "a bare name one import declares still resolves, and so does its qualified spelling" {
            tree
                [
                    "bare.weir",
                    [
                        "import \"a.weir\" as A"
                        "let x = LangDefaults { symbol = \"a\" }"
                        "let y = [\"{}\"] |> from json seq<LangDefaults>"
                        "let z = { symbol = \"z\" }"
                        "let q = A.LangDefaults { symbol = \"q\" }"
                        "print $\"{x == A.make \"a\"} {y |> Seq.length} {z == q}\""
                    ]
                    "sig.weir",
                    [
                        "module S"
                        "import \"a.weir\" as A"
                        "let f : LangDefaults -> A.LangDefaults"
                        "let f d = d"
                    ]
                ]
                (fun td ->
                    clean td "bare.weir" "bare and qualified are one type"
                    clean td "sig.weir" "both spellings in one signature")
        }
        test "A.T and B.T are different types, even field for field" {
            withDir
                [
                    "a.weir",
                    [
                        "module A"
                        "type Cfg = { name: string }"
                        "let mk : string -> Cfg"
                        "let mk s = { name = s }"
                    ]
                    "b.weir",
                    [
                        "module B"
                        "type Cfg = { name: string }"
                        "let mk : string -> Cfg"
                        "let mk s = { name = s }"
                    ]
                    "eq.weir", both @ [ "let same = A.mk \"x\" == B.mk \"x\""; "print $\"{same}\"" ]
                    "arg.weir",
                    [ "module G" ]
                    @ both
                    @ [
                        "let f : A.Cfg -> string"
                        "let f c = c.name"
                        "let g : unit -> string"
                        "let g () = f (B.mk \"x\")"
                    ]
                    "lit.weir", both @ [ "let c = { name = \"x\" }"; "print c.name" ]
                    "ok.weir", both @ [ "let c = B.Cfg { name = \"x\" }"; "print $\"{c == B.mk \"x\"}\"" ]
                ]
                (fun td ->
                    says td "eq.weir" "expected A.Cfg, got B.Cfg" "== across origins"
                    says td "arg.weir" "A.Cfg" "an argument of the other type"

                    says
                        td
                        "lit.weir"
                        "ambiguous record literal; it matches: A.Cfg, B.Cfg"
                        "the bare literal names both"

                    clean td "ok.weir" "the qualified literal is B's type")
        }
        test "a module name with no such type, and an unknown qualifier, are named" {
            tree
                [
                    "nope.weir", both @ [ "let x = [\"{}\"] |> from json A.Nope"; "print x.symbol" ]
                    "noalias.weir", both @ [ "let x = [\"{}\"] |> from json C.LangDefaults"; "print x.symbol" ]
                ]
                (fun td ->
                    says td "nope.weir" "module 'A' declares no type 'Nope'" "the alias, the missing type"
                    says td "noalias.weir" "unknown type 'C.LangDefaults' — no import is named 'C'" "no such import")
        }
        test "a module's exported type over two same-named imports crosses to its importer [D:transitive-types]" {
            tree
                [
                    "m.weir",
                    [ "module M" ]
                    @ both
                    @ [
                        "type Both = { a: A.LangDefaults; b: B.LangDefaults }"
                        "let pair : string -> Both"
                        "let pair s = { a = A.make s; b = B.make s }"
                    ]
                    "only.weir",
                    [
                        "import \"m.weir\" as M"
                        "let p = M.pair \"x\""
                        "print $\"{p.a.symbol} {p.b.style} {show p}\""
                    ]
                    "mixed.weir", [ "import \"m.weir\" as M"; "let p = M.pair \"x\""; "let wrong = p.a == p.b" ]
                    "joined.weir",
                    [ "import \"m.weir\" as M" ]
                    @ both
                    @ [
                        "let p = M.pair \"x\""
                        "print $\"{p.a == A.make \"x\"} {p.b == B.make \"x\"}\""
                    ]
                ]
                (fun td ->
                    clean td "only.weir" "the transitive types read through M"
                    says td "mixed.weir" "expected A.LangDefaults, got B.LangDefaults" "they stay distinct"
                    clean td "joined.weir" "a direct import joins the same types")
        }
        test "a local declaration keeps the bare name; the import's type is reached qualified" {
            tree
                [
                    "local.weir",
                    [
                        "type LangDefaults = { mine: bool }"
                        "import \"a.weir\" as A"
                        "let l = { mine = true }"
                        "let x = A.make \"a\""
                        "let q = A.LangDefaults { symbol = \"q\" }"
                        "print $\"{l.mine} {x == q}\""
                    ]
                    "cross.weir",
                    [
                        "type LangDefaults = { mine: bool }"
                        "import \"a.weir\" as A"
                        "let bad = A.make \"a\" == LangDefaults { mine = true }"
                    ]
                    "after.weir", [ "import \"a.weir\" as A"; "type LangDefaults = { mine: bool }" ]
                ]
                (fun td ->
                    clean td "local.weir" "import after a local declaration"
                    says td "cross.weir" "expected A.LangDefaults, got LangDefaults" "two types"

                    says
                        td
                        "after.weir"
                        "already provided by the import at line 1"
                        "a declaration after the import still refuses [D:dup-type-decl]")
        }
        test "one record name, two wire tables, in one write refuses [D:wire-keys]" {
            withDir
                [
                    "a.weir", [ "module A"; "type Cfg = {"; "    [<Wire \"nm\">]"; "    name: string"; "}" ]
                    "b.weir", [ "module B"; "type Cfg = { name: string }" ]
                    "w.weir",
                    both
                    @ [
                        "type Two = { a: A.Cfg; b: B.Cfg }"
                        "let v = { a = A.Cfg { name = \"x\" }; b = B.Cfg { name = \"y\" } }"
                        "v |> to json |> Seq.iter print"
                    ]
                    "one.weir", both @ [ "A.Cfg { name = \"x\" } |> to json |> Seq.iter print" ]
                ]
                (fun td ->
                    says
                        td
                        "w.weir"
                        "'A.Cfg' and 'B.Cfg' share a declared name but not their wire keys"
                        "the writer cannot tell them apart"

                    clean td "one.weir" "one of them alone writes")
        }
        test "hover and definition name the qualified type" {
            withTempDir "weir-qtn-lsp" (fun td ->
                System.IO.File.WriteAllLines(System.IO.Path.Combine(td, "a.weir"), a)
                System.IO.File.WriteAllLines(System.IO.Path.Combine(td, "b.weir"), b)
                let entry = System.IO.Path.Combine(td, "main.weir")

                let lines =
                    both
                    @ [
                        "let x = A.make \"a\""
                        "let y = B.LangDefaults { symbol = \"b\"; style = \"s\" }"
                        "print $\"{x.symbol} {y.style}\""
                    ]

                Expect.equal (Weir.Lsp.hoverAt entry lines 3 5) (Some "x : A.LangDefaults") "a value of A's type"
                Expect.equal (Weir.Lsp.hoverAt entry lines 4 5) (Some "y : B.LangDefaults") "a value of B's type"

                Expect.equal
                    (Weir.Lsp.definitionTarget entry lines 4 12)
                    (Some(Some(System.IO.Path.Combine(td, "b.weir")), 3, 6, 12))
                    "the qualified literal's type jumps to B's declaration")
        }
    ]

let fromTableTests =
    // the aligned-table boundary [D:from-table]: fixtures build by
    // padded widths so header offsets and cell offsets agree by
    // construction — the tabwriter reality both kubectl and docker emit
    let row (ws: int list) (cs: string list) =
        (List.zip ws cs
         |> List.map (fun (w, c: string) -> c.PadRight w)
         |> String.concat "")
            .TrimEnd()

    let tenv =
        env
        |> declare "type TPod = { name: string; ready: string; status: string; restarts: int; age: string }"
        |> declare "type TCtr = { containerId: string; status: string; command: string }"
        |> declare
            "type TNode = { name: string; [<Wire \"ROLES\">] roles: Option<string>; cpu: Option<float>; ready: bool; podTemplateHash: string }"
        |> declare "type TReq = { name: string; restarts: int }"
        |> declare "type TWireSame = { [<Wire \"name\">] name: string }"
        |> declare "type TPod3 = { name: string; ready: string; status: string }"
        |> declare "type TBadSeq = { name: seq<string> }"
        |> declare "type TBadOpt = { name: Option<seq<int>> }"
        |> declare "type TUni = A of int | B"

    let checkOkT input =
        match typecheck tenv (parse input) with
        | Ok te -> te
        | Error terr -> failtest $"expected Ok, got: {formatError terr}"

    let checkErrT input =
        match typecheck tenv (parse input) with
        | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
        | Error terr -> terr

    let runT (overrides: (string * Value) list) input =
        let vals = overrides |> List.fold (fun vs (n, v) -> Map.add n v vs) valueEnv
        eval vals (checkOkT input)

    let podWidths = [ 8; 8; 19; 11; 0 ]

    let podSample =
        VSeq [
            VStr(row podWidths [ "NAME"; "READY"; "STATUS"; "RESTARTS"; "AGE" ])
            VStr(row podWidths [ "web-1"; "1/1"; "Running"; "0"; "2d1h" ])
            VStr ""
            VStr(row podWidths [ "db-0"; "1/2"; "CrashLoopBackOff"; "3"; "5h" ])
        ]

    testList "from table [D:from-table]" [
        test "header-offset slicing reads kubectl-shaped rows; blank lines skip; the type is seq<T>" {
            let te = checkOkT "src |> from table TPod"
            Expect.equal te.Ty (TSeq(TNamed("TPod", []))) "from table T : seq<string> -> seq<T>"

            Expect.equal
                (runT [ "src", podSample ] "src |> from table TPod")
                (VSeq [
                    VRecord(
                        "TPod",
                        [
                            "name", VStr "web-1"
                            "ready", VStr "1/1"
                            "status", VStr "Running"
                            "restarts", VInt 0L
                            "age", VStr "2d1h"
                        ]
                    )
                    VRecord(
                        "TPod",
                        [
                            "name", VStr "db-0"
                            "ready", VStr "1/2"
                            "status", VStr "CrashLoopBackOff"
                            "restarts", VInt 3L
                            "age", VStr "5h"
                        ]
                    )
                ])
                "columns slice at header offsets; int cells convert; blank interior lines skip"
        }
        test "header offsets count code points — an astral cell does not shift the cut [D:str-code-points]" {
            // tabwriter pads by rune count: 6 emoji + 2 spaces fill an 8-wide column
            let sample =
                VSeq [
                    VStr "NAME    READY   STATUS             RESTARTS   AGE"
                    VStr "😀😀😀😀😀😀  1/1     Running            0          5m"
                ]

            match runT [ "src", sample ] "src |> from table TPod" with
            | VSeq rows ->
                match List.ofSeq rows with
                | [ VRecord(_, fs) ] ->
                    Expect.equal
                        (fs |> List.map snd)
                        [ VStr "😀😀😀😀😀😀"; VStr "1/1"; VStr "Running"; VInt 0L; VStr "5m" ]
                        ""
                | other -> failtest $"unexpected rows {other}"
            | v -> failtest $"unexpected {v}"
        }
        test "cells slice like per-column offsets: astral header, astral cells, a short line" {
            let header = "N😀ME  READY  😀STATUS  AGE"
            let cols = Weir.Table.columns header

            for line in [ "😀😀😀😀😀😀  1/1    Run😀ning  5m"; "plain   1/1    Running  5m"; "😀😀"; "" ] do
                let naive =
                    cols
                    |> List.mapi (fun i c ->
                        let s = CodePoints.offset line c.Start

                        let e =
                            match List.tryItem (i + 1) cols with
                            | Some n -> CodePoints.offset line n.Start
                            | None -> line.Length

                        line.Substring(s, max 0 (e - s)).Trim())

                Expect.equal (Weir.Table.cells cols line) naive $"one walk = per-column offsets: {line}"
        }
        // sequenced: a wall-clock ceiling under the parallel suite's load
        // times the machine, not the walk
        testSequenced
        <| test "a wide table slices in linear time per line (2000 columns x 200 rows) [D:str-code-points]" {
            let n = 2000
            let header = [ for i in 0 .. n - 1 -> $"C{i:D4}" ] |> String.concat "   "
            let row = [ for i in 0 .. n - 1 -> $"v{i:D4}" ] |> String.concat "   "
            let cols = Weir.Table.columns header
            let sw = System.Diagnostics.Stopwatch.StartNew()

            for _ in 1..200 do
                Weir.Table.cells cols row |> ignore

            sw.Stop()
            Expect.equal (List.last (Weir.Table.cells cols row)) "v1999" "last cell"
            // a per-column whole-line scan took ~60s here; one walk takes milliseconds
            Expect.isTrue (sw.ElapsedMilliseconds < 5000L) $"200 rows took {sw.ElapsedMilliseconds}ms"
        }
        test "az `-o table` dashes separator under the header is skipped, never a garbage row [D:from-table-az]" {
            // az (and tabulate-style tools) draw `----  ----` between the
            // header and the data; it must not parse as a data row. The
            // separator rides at the header offsets like any aligned row.
            let azSample =
                VSeq [
                    VStr(row podWidths [ "NAME"; "READY"; "STATUS"; "RESTARTS"; "AGE" ])
                    VStr(row podWidths [ "------"; "-----"; "-------"; "--------"; "---" ])
                    VStr(row podWidths [ "web-1"; "1/1"; "Running"; "0"; "2d1h" ])
                ]

            Expect.equal
                (runT [ "src", azSample ] "src |> from table TPod")
                (VSeq [
                    VRecord(
                        "TPod",
                        [
                            "name", VStr "web-1"
                            "ready", VStr "1/1"
                            "status", VStr "Running"
                            "restarts", VInt 0L
                            "age", VStr "2d1h"
                        ]
                    )
                ])
                "the dashes row is dropped; only the real row reads (no `------` cell became data)"
        }
        test "a real first row is NOT skipped — the separator drop is conditional [D:from-table-az]" {
            // a row with a lone `-` cell (a tool's own 'none' spelling) is
            // not all-dashes, so it stays data — the skip fires only on a
            // row that is entirely dashes and spaces
            let ws = [ 8; 8; 0 ]

            let sample =
                VSeq [
                    VStr(row ws [ "name"; "ready"; "status" ])
                    VStr(row ws [ "web-1"; "-"; "Running" ])
                ]

            Expect.equal
                (runT [ "src", sample ] "src |> from table TPod3")
                (VSeq [
                    VRecord("TPod3", [ "name", VStr "web-1"; "ready", VStr "-"; "status", VStr "Running" ])
                ])
                "a first row that only LOOKS dash-ish (one `-` cell) is real data, kept"
        }
        test "a two-word single-space header is ONE column; spaced values and a spaced last column survive" {
            // docker's reality: `CONTAINER ID` is one header (single
            // space), columns pad with 3+ spaces, STATUS holds
            // `Up 2 hours` and the last column runs to end of line
            let ws = [ 15; 15; 0 ]

            let sample =
                VSeq [
                    VStr(row ws [ "CONTAINER ID"; "STATUS"; "COMMAND" ])
                    VStr(row ws [ "3f4e5d6a7b8c"; "Up 2 hours"; "/bin/sh -c run" ])
                ]

            Expect.equal
                (runT [ "src", sample ] "src |> from table TCtr")
                (VSeq [
                    VRecord(
                        "TCtr",
                        [
                            "containerId", VStr "3f4e5d6a7b8c"
                            "status", VStr "Up 2 hours"
                            "command", VStr "/bin/sh -c run"
                        ]
                    )
                ])
                "a single space stays inside one header; offsets, not whitespace runs, bound the cells"
        }
        test
            "matching: Wire hits the raw header verbatim; normalized names match case-insensitively; Option reads empty and <none>; extra columns are ignored" {
            let ws = [ 8; 22; 8; 8; 8; 19; 0 ]

            let sample =
                VSeq [
                    VStr(row ws [ "NAME"; "POD-TEMPLATE-HASH"; "ROLES"; "CPU"; "READY"; "EXTRA"; "AGE" ])
                    VStr(row ws [ "n1"; "abc123"; "worker"; "1.5"; "true"; "ignored"; "9d" ])
                    VStr(row ws [ "n2"; "def456"; "<none>"; ""; "false"; "ignored"; "9d" ])
                ]

            Expect.equal
                (runT [ "src", sample ] "src |> from table TNode")
                (VSeq [
                    VRecord(
                        "TNode",
                        [
                            "name", VStr "n1"
                            "roles", VUnion("Some", Some(VStr "worker"))
                            "cpu", VUnion("Some", Some(VFloat 1.5))
                            "ready", VBool true
                            "podTemplateHash", VStr "abc123"
                        ]
                    )
                    VRecord(
                        "TNode",
                        [
                            "name", VStr "n2"
                            "roles", VUnion("None", None)
                            "cpu", VUnion("None", None)
                            "ready", VBool false
                            "podTemplateHash", VStr "def456"
                        ]
                    )
                ])
                "the kubectl idioms: <none>/empty read None under Option; undeclared columns cost nothing"
        }
        test "[<Wire>] spelled like the field name still matches the header verbatim" {
            let m =
                Expect.throwsC
                    (fun () ->
                        runT [ "src", VSeq [ VStr "NAME   X"; VStr "a      1" ] ] "src |> from table TWireSame"
                        |> ignore)
                    id

            Expect.stringContains
                m.Message
                "from table: missing column 'name' for field 'name' — headers seen: NAME, X"
                "NAME is not name"

            Expect.equal
                (runT [ "src", VSeq [ VStr "name   X"; VStr "a      1" ] ] "src |> from table TWireSame")
                (VSeq [ VRecord("TWireSame", [ "name", VStr "a" ]) ])
                "the exact header matches"
        }
        test "runtime errors are located, each teaching" {
            // a missing declared column names itself and the headers seen
            let missing =
                Expect.throwsC
                    (fun () ->
                        runT [ "src", VSeq [ VStr "NAME   AGE"; VStr "n1     1d" ] ] "src |> from table TReq"
                        |> ignore)
                    id

            Expect.stringContains
                missing.Message
                "from table: missing column 'restarts' for field 'restarts' — headers seen: NAME, AGE"
                "the absent column and the header inventory"

            // a cell that fails its field's type carries line and col
            let badInt =
                Expect.throwsC
                    (fun () ->
                        runT [ "src", VSeq [ VStr "NAME   RESTARTS"; VStr "n1     often" ] ] "src |> from table TReq"
                        |> ignore)
                    id

            Expect.stringContains
                badInt.Message
                "from table: line 2, col 8: column 'RESTARTS': expected int, got 'often'"
                "row line + column offset + header name"

            // a required field refuses an absent cell, naming Option
            let absent =
                Expect.throwsC
                    (fun () ->
                        runT [ "src", VSeq [ VStr "NAME   RESTARTS"; VStr "n1" ] ] "src |> from table TReq"
                        |> ignore)
                    id

            Expect.stringContains
                absent.Message
                "an empty cell — declare Option<int> to read it as None"
                "absence has a typed spelling; the error names it"

            // duplicate normalized headers cannot be told apart
            let dup =
                Expect.throwsC
                    (fun () ->
                        runT
                            [ "src", VSeq [ VStr "POD-TEMPLATE-HASH   PODTEMPLATEHASH"; VStr "a  b" ] ]
                            "src |> from table TReq"
                        |> ignore)
                    id

            Expect.stringContains
                dup.Message
                "columns 'POD-TEMPLATE-HASH' and 'PODTEMPLATEHASH' match the same name"
                "the match key is the normalized form, so a dup is unanswerable"

            // no header at all
            let emptyIn =
                Expect.throwsC
                    (fun () -> runT [ "src", VSeq [ VStr ""; VStr "   " ] ] "src |> from table TReq" |> ignore)
                    id

            Expect.stringContains
                emptyIn.Message
                "from table: empty input — expected a header row"
                "nothing to slice by"
        }
        test "a header-only table reads as the empty seq (docker ps with nothing running)" {
            Expect.equal
                (runT [ "src", VSeq [ VStr "NAME   RESTARTS" ] ] "src |> from table TReq |> Seq.length")
                (VInt 0L)
                "a header with no rows is an answer, not an error"
        }
        test "check-time laws: flat scalar rows only; no seq/stream/Map wrap; no union; no to table" {
            Expect.stringContains
                (checkErrT "src |> from table TBadSeq").Message
                "field 'name': type seq<string> is not admitted; table cells are string, int, float, bool, or Option of one"
                "a cell is one aligned column's text"

            Expect.stringContains
                (checkErrT "src |> from table TBadOpt").Message
                "a table cell reads Option of a scalar, not Option<seq<int>>"
                ""

            Expect.stringContains
                (checkErrT "src |> from table seq<TPod>").Message
                "'from table TPod' already yields seq<TPod> — write from table TPod"
                "the jsonl precedent"

            Expect.stringContains
                (checkErrT "src |> from table stream TPod").Message
                "'from table stream' does not exist — a table is already rows"
                ""

            Expect.stringContains
                (checkErrT "src |> from table Map<string, TPod>").Message
                "a table reads rows, not a keyed object"
                ""

            Expect.stringContains
                (checkErrT "src |> from table TUni").Message
                "'from table' needs a row record; 'TUni' is a union — a table row has no tag convention"
                ""

            Expect.stringContains
                (checkErrT "src |> from table").Message
                "'from table' needs a row record name, e.g. from table Pod"
                ""

            Expect.stringContains
                (checkErrT "[1] |> to table").Message
                "'to table' does not exist — the table boundary is read-only ('from table T'); write JSON or YAML instead"
                "no write side in v1"
        }
        test "'table' stays an ordinary identifier outside adapter position" {
            // the adapter-word law: `json` binds, and so does `table`
            Expect.equal (run "let table = 5 in table + 1") (VInt 6L) "no new reserved word"
        }
    ]

// ---- Http DX [D:http-dx]: Json-takes-the-value, Http.expect, Form ------
let httpDxTests =
    let checkOf = diagsIn "httpdx.weir"

    testList "Http DX [D:http-dx]" [
        test "Json takes the value: a record payload checks (the to json law at the call site)" {
            Expect.isEmpty
                (checkOf [
                    "type P = { a: int }"
                    "let r = Http.send { Http.post \"http://x\" with body = Json { a = 1 } }"
                    "print $\"{r.status}\""
                ])
                "a jsonable payload is admitted"
        }
        test "rendered lines are RawJson; Json has no bare or pattern form [D:json-value]" {
            Expect.isEmpty
                (checkOf [
                    "type P = { a: int }"
                    "let r = Http.send { Http.post \"http://x\" with body = RawJson ({ a = 1 } |> to json) }"
                    "print $\"{r.status}\""
                ])
                "a seq<string> payload is the pre-rendered document"

            let refusal src =
                match checkOf [ src ] with
                | d :: _ -> d.Message
                | [] -> failtest $"expected a refusal: {src}"

            Expect.stringContains (refusal "print ([ [ 1 ] ] |> Seq.map Json)") "takes the value" "no function value"

            Expect.stringContains
                (refusal "print (match (Reply.empty 200).body with | Json _ -> 1 | _ -> 0)")
                "the case to match is `RawJson lines`"
                "no Json pattern"
        }
        test "Json renders every value, a list of strings and the empty list included [D:json-value]" {
            let lines src =
                run $"match ({src}) with | RawJson ls -> Str.join \"\" ls | _ -> \"?\""

            Expect.equal (lines "Json [ \"a\"; \"b\" ]") (VStr "[\"a\",\"b\"]") "strings are an array"
            Expect.equal (lines "Json []") (VStr "[]") "the empty array"
            Expect.equal (lines "{| k = 1 |} |> Json") (VStr "{\"k\":1}") "piped"
        }
        test "the jsonable law refuses an unjsonable payload, in to json's words" {
            match
                checkOf [
                    "let f () = print \"x\""
                    "let r = Http.send { Http.post \"http://x\" with body = Json {| g = f |} }"
                    "print $\"{r.status}\""
                ]
            with
            | [ d ] -> Expect.stringContains d.Message "not admitted" "the refusal is to json's own"
            | ds -> failtest $"expected the one refusal, got {ds}"
        }
        test "Http.expect checks as HttpRequest -> seq<string>, and readonly admits only a query method" {
            Expect.isEmpty
                (checkOf [
                    "let v ="
                    "    readonly"
                    "        Http.expect (Http.get \"http://x\") |> Seq.length"
                    "print $\"{v}\""
                ])
                "a GET read is ambient"

            match
                checkOf [
                    "let v ="
                    "    readonly"
                    "        Http.expect (Http.post \"http://x\") |> Seq.length"
                    "print $\"{v}\""
                ]
            with
            | [ d ] -> Expect.stringContains d.Message "Http.expect" "the refusal names the callable"
            | ds -> failtest $"expected the one refusal, got {ds}"
        }
        test "Form is an HttpBody case: pairs check, a non-pair payload refuses" {
            Expect.isEmpty
                (checkOf [
                    "let r = Http.send { Http.post \"http://x\" with body = Form [(\"a\", \"b\")] }"
                    "print $\"{r.status}\""
                ])
                "pairs are the Form payload"

            Expect.isNonEmpty
                (checkOf [
                    "let r = Http.send { Http.post \"http://x\" with body = Form \"a=b\" }"
                    "print $\"{r.status}\""
                ])
                "a raw string is not a Form payload"
        }
    ]

// from toml [D:from-toml] — the vendored toml-test suite (TOML 1.1.0 file
// list), truncation robustness, and the yaml-binder path
let tomlTests =
    let suiteRoot =
        let rec up (d: System.IO.DirectoryInfo) =
            if isNull d then
                failwith "tests/toml-test not found above the test binary"
            else
                let candidate = System.IO.Path.Combine(d.FullName, "tests", "toml-test")

                if System.IO.Directory.Exists candidate then
                    candidate
                else
                    up d.Parent

        lazy (up (System.IO.DirectoryInfo System.AppContext.BaseDirectory))

    let suiteFiles () =
        System.IO.File.ReadAllLines(System.IO.Path.Combine(suiteRoot.Value, "files-toml-1.1.0"))
        |> Array.filter (fun l -> l.EndsWith ".toml")

    // an undecodable file never reaches the parser (File.read's job)
    let strictUtf8 = System.Text.UTF8Encoding(false, true)

    let readSuite (rel: string) =
        try
            Some(strictUtf8.GetString(System.IO.File.ReadAllBytes(System.IO.Path.Combine(suiteRoot.Value, rel))))
        with _ ->
            None

    let isTagged (e: System.Text.Json.JsonElement) =
        let mutable t = Unchecked.defaultof<System.Text.Json.JsonElement>
        let mutable v = Unchecked.defaultof<System.Text.Json.JsonElement>

        e.TryGetProperty("type", &t)
        && e.TryGetProperty("value", &v)
        && t.ValueKind = System.Text.Json.JsonValueKind.String
        && Seq.length (e.EnumerateObject()) = 2

    // the node tree against toml-test's tagged JSON; datetimes compare as
    // "read as text" only — weir keeps their source spelling
    let rec mismatch (n: Weir.Yaml.Node) (e: System.Text.Json.JsonElement) : string option =
        let inv = System.Globalization.CultureInfo.InvariantCulture

        match n, e.ValueKind with
        | Weir.Yaml.NMap(entries, _), System.Text.Json.JsonValueKind.Object when not (isTagged e) ->
            let props = e.EnumerateObject() |> Seq.map (fun p -> p.Name, p.Value) |> Map.ofSeq

            if Set.ofList (List.map fst entries) <> Set.ofSeq props.Keys then
                Some $"keys {List.map fst entries} vs {List.ofSeq props.Keys}"
            else
                entries
                |> List.tryPick (fun (k, v) -> mismatch v props[k] |> Option.map (fun m -> $"{k}: {m}"))
        | Weir.Yaml.NSeq(items, _), System.Text.Json.JsonValueKind.Array ->
            let es = List.ofSeq (e.EnumerateArray())

            if items.Length <> es.Length then
                Some "array length"
            else
                List.zip items es |> List.tryPick (fun (a, b) -> mismatch a b)
        | Weir.Yaml.NScalar(raw, quoted, _), System.Text.Json.JsonValueKind.Object ->
            let ty = e.GetProperty("type").GetString()
            let v = e.GetProperty("value").GetString()

            let ok =
                match ty with
                | "string" -> quoted && raw = v
                | "integer" -> not quoted && System.Int64.Parse raw = System.Int64.Parse v
                | "float" when v = "inf" || v = "-inf" || v = "nan" ->
                    not quoted && (raw = v || (v = "nan" && raw.EndsWith "nan"))
                | "float" -> not quoted && System.Double.Parse(raw, inv) = System.Double.Parse(v, inv)
                | "bool" -> not quoted && raw = v
                | _ -> quoted

            if ok then None else Some $"{ty} {v} vs {raw}"
        | n, k -> Some $"{n} vs {k}"

    testList "from toml [D:from-toml]" [
        test "toml-test 1.1.0: every valid document matches, every invalid one is refused" {
            let failures =
                suiteFiles ()
                |> Array.choose (fun rel ->
                    let valid = rel.StartsWith "valid/"

                    match readSuite rel with
                    | None -> if valid then Some $"{rel}: not UTF-8" else None
                    | Some text ->
                        match Weir.Toml.parse text, valid with
                        | Ok node, true ->
                            let expected =
                                System.IO.File.ReadAllText(
                                    System.IO.Path.Combine(
                                        suiteRoot.Value,
                                        System.IO.Path.ChangeExtension(rel, ".json")
                                    )
                                )
                                |> System.Text.Json.JsonDocument.Parse

                            mismatch node expected.RootElement |> Option.map (fun m -> $"{rel}: {m}")
                        | Error e, true -> Some $"{rel}: refused: {e}"
                        | Ok _, false -> Some $"{rel}: accepted"
                        | Error _, false -> None)

            Expect.isGreaterThan (suiteFiles().Length) 600 "the suite is present"
            Expect.isEmpty failures "conformance"
        }
        test "deep dotted keys, header paths and inline nesting are refused, never a stack overflow" {
            let seg n = String.replicate n "a." + "a"

            for doc in
                [
                    seg 50000 + " = 1"
                    "[" + seg 50000 + "]"
                    "[[" + seg 50000 + "]]"
                    "x = "
                    + String.replicate 300 ("{ " + seg 300 + " = ")
                    + "1"
                    + String.replicate 300 " }"
                ] do
                match Weir.Toml.parse doc with
                | Error e -> Expect.stringContains e "nesting is too deep" "refused with the limit"
                | Ok _ -> failtest "a 50k-deep document must be refused"

            // the deepest legal tree still parses
            match Weir.Toml.parse ("[" + seg 499 + "]\n" + seg 499 + " = 1") with
            | Ok _ -> ()
            | Error e -> failtest $"within the limit: {e}"
        }
        test "a truncated document is a result or an error, never a crash" {
            for rel in suiteFiles () |> Array.filter (fun r -> r.StartsWith "valid/") do
                match readSuite rel with
                | Some text ->
                    for cut in 0 .. text.Length - 1 do
                        try
                            Weir.Toml.parse (text.Substring(0, cut)) |> ignore
                        with ex ->
                            failtest $"{rel} cut at {cut}: {ex.GetType().Name}: {ex.Message}"
                | None -> ()
        }
        test "the yaml binder reads toml: records, Option, seq, arrays of tables" {
            let src =
                VSeq(
                    [
                        "title = \"x\""
                        "[owner]"
                        "name = 'Ada'"
                        "[[item]]"
                        "n = 1_000"
                        "[[item]]"
                        "n = 0x10"
                        "tags = [\"a\", \"b\",]"
                    ]
                    |> List.map VStr
                    |> List.toSeq
                )

            let shape =
                "{| title: string; owner: {| name: string; nick: Option<string> |}; item: seq<{| n: int; tags: Option<seq<string>> |}> |}"

            Expect.equal (runWith [ "src", src ] $"src |> from toml {shape} |> _.owner.name") (VStr "Ada") "a table"

            Expect.equal
                (runWith [ "src", src ] $"src |> from toml {shape} |> _.item |> Seq.map _.n |> Seq.sum")
                (VInt 1016L)
                "arrays of tables, normalised ints"
        }
        test "errors carry the line and say from toml" {
            let raises (lines: string list) (shape: string) (needle: string) =
                let src = VSeq(lines |> List.map VStr |> List.toSeq)

                try
                    runWith [ "src", src ] $"src |> from toml {shape}" |> ignore
                    failtest "expected a raise"
                with ex ->
                    Expect.stringContains ex.Message needle needle

            raises [ "a = 1"; "a = 2" ] "{| a: int |}" "from toml: line 2: the key 'a' is defined twice"
            raises [ "a = \"x\"" ] "{| a: int |}" "the value is a string"
            raises [ "r = inf" ] "{| r: float |}" "weir floats are finite"
            raises [ "[t]"; "[t]" ] "{| t: {| x: Option<int> |} |}" "defined twice"
        }
        test "from toml refuses a seq, stream or Map top level" {
            Expect.stringContains
                (checkErr "[\"a = 1\"] |> from toml seq<{| a: int |}>").Message
                "its top level is a table"
                ""
        }
    ]

// hunt 3 round 4's race seams; the signal, serve and REPL halves are e2e
// cells
let hunt3r4SafetyTests =
    testList "hunt-3r4 safety" [
        test "NO_PROXY matches as curl does [D:noproxy-curl]" {
            let cases = [
                "example.com", "sub.example.com", true
                "example.com", "example.com", true
                "example.com.", "sub.example.com", true
                ".example.com", "example.com", true
                "example.com", "sub.example.com.", true
                "EXAMPLE.com", "a.b.Example.COM", true
                "example.com", "notexample.com", false
                "*.example.com", "sub.example.com", false
                "example.com:443", "example.com", false
                "x.org, example.com", "sub.example.com", true
                "x.org example.com", "sub.example.com", false
                "a.com,*", "anything.net", true
                "localhost", "127.0.0.1", false
                "127.0.0.1", "127.0.0.1", true
                ".0.0.1", "127.0.0.1", false
                ".1", "127.0.0.1", false
                "127.0.0.0/8", "127.0.0.1", true
                "127.9.9.9/8", "127.0.0.1", true
                "127.0.0.0/0", "127.0.0.1", false
                "0.0.0.0/0", "127.0.0.1", false
                "127.0.0.0/ 8", "127.0.0.1", false
                "127.0.0.1/33", "127.0.0.1", false
                "127.0.0.2/31", "127.0.0.3", true
                "127.0.0.1:8080", "127.0.0.1", false
                "::1", "[::1]", true
                "::1/128", "[::1]", true
                "[::1]", "[::1]", false
                "[::1]:8080", "[::1]", false
                "[::1]/128", "[::1]", false
                "fd00::/8", "[fd00::1]", true
                "fd00::/8", "[fe80::1]", false
            ]

            for np, host, want in cases do
                Expect.equal (Weir.Http.noProxyMatch np host) want $"NO_PROXY={np} vs {host}"
        }
        test "a proxy's refusal and close classify by kind, never by text [D:proxy-refusal-words]" {
            let tunnel =
                System.Net.Http.HttpRequestException(
                    System.Net.Http.HttpRequestError.ProxyTunnelError,
                    "The proxy tunnel request to proxy 'http://u:S3cr3t@h:1/' failed with status code '407'.",
                    null,
                    System.Nullable System.Net.HttpStatusCode.ProxyAuthenticationRequired
                )

            Expect.equal
                (Weir.Http.classifyTransport 1000L 1 (System.AggregateException(tunnel :> exn)))
                (Weir.Http.ProxyRefused 407)
                "a CONNECT refusal carries its status"

            let ended =
                System.Net.Http.HttpRequestException(System.Net.Http.HttpRequestError.ResponseEnded, "x")

            Expect.equal
                (Weir.Http.classifyTransport 1000L 1 (System.AggregateException(ended :> exn)))
                Weir.Http.ConnectionEnded
                "an early close is its own case"

            let other =
                Weir.Http.transportMessage "h" (Weir.Http.OtherTransport "proxy 'http://u:S3cr3t@h:1/' \u001b[8m")

            Expect.isFalse (other.Contains "S3cr3t") $"userinfo redacted: {other}"
            Expect.isFalse (other.Contains "\u001b") $"escape visible: {other}"

            Expect.equal
                (Weir.Http.redactUrlsIn "at 'http://a:b@h/x' and https://t@k/")
                "at 'http://***@h/x' and https://***@k/"
                "every URL"
        }
        test "a runtime error escapes the data it quotes [D:error-data-escape]" {
            // X = "a<CR>b<ESC>[8mc"
            let x = "(Str.fromBase64 \"YQ1iG1s4bWM=\")"

            for src in
                [
                    $"Float.parse {x}"
                    $"Duration.parse {x}"
                    $"Size.parse {x}"
                    $"Instant.parse {x}"
                    $"Uuid.parse {x}"
                    $"File.read {x} |> Seq.length"
                    $"Env.need {x}"
                    $"[{x}] |> from json {{| a: int |}}"
                    $"[\"a: \" + {x}] |> from yaml {{| a: int |}}"
                    "[\"a = \" + Str.fromBase64 \"MRtbOG0Neg==\"] |> from toml {| a: int |}"
                ] do
                let msg =
                    try
                        run src |> ignore
                        failtest $"expected a raise: {src}"
                    with e ->
                        e.Message

                Expect.isFalse (msg.Contains "\u001b" || msg.Contains "\r") $"raw CR/ESC in: {msg}"
                Expect.stringContains msg "\\x1b" $"the ESC renders visibly: {src}"

            Expect.equal (excerpt "a\nb") "a\\x0ab" "an excerpt shows LF visibly"
            Expect.equal (quoteData "ok") "ok" "plain data is unchanged"
        }
        test "a failed console write marks the writer gone, never raises [D:console-hangup]" {
            let dead = {
                new System.IO.TextWriter() with
                    member _.Encoding = System.Text.Encoding.UTF8
                    member _.Write(c: char) : unit = raise (System.IO.IOException "EIO")
                    member _.Write(s: string) : unit = raise (System.IO.IOException "EIO")
                    member _.WriteLine(s: string) : unit = raise (System.IO.IOException "EIO")
                    member _.Flush() : unit = raise (System.IO.IOException "EIO")
            }

            let w = new HangupWriter(dead)
            w.Write "x"
            w.WriteLine "y"
            w.Flush()
            Expect.isTrue w.Gone "the writer is gone after the first failure"
        }
    ]

// hunt 3 round 5's safety items; the Http.expect body, add schema and
// Args.load halves are e2e cells
let hunt3r5SafetyTests =
    testList "hunt-3r5 safety" [
        test "NO_PROXY matches a non-ASCII host by its punycode, an entry as written [D:noproxy-idn]" {
            let cases = [
                "xn--bcher-kva.example", "bücher.example", true
                "bücher.example", "xn--bcher-kva.example", false
                "bücher.example", "bücher.example", false
                ".xn--bcher-kva.example", "a.bücher.example", true
                "example", "bücher.example", true
                "xn--bcher-kva.example", "bucher.example", false
                "bücher.example", "xn--bcher-kva.example.org", false
            ]

            for np, host, want in cases do
                Expect.equal (Weir.Http.noProxyMatch np host) want $"NO_PROXY={np} vs {host}"
        }
        test "a request's show masks the URL's userinfo, the value unchanged [D:url-redact-show]" {
            let req =
                VRecord(
                    "HttpRequest",
                    [
                        "method", VUnion("Post", None)
                        "url", VStr "http://bob:S3cr3tPass@127.0.0.1:9/echo"
                        "auth", VUnion("Bearer", Some(VSecret "tok"))
                    ]
                )

            let shown = formatValue req
            Expect.stringContains shown "url = \"http://***@127.0.0.1:9/echo\"" "the userinfo is masked, host kept"
            Expect.isFalse (shown.Contains "S3cr3t" || shown.Contains "bob") $"no credential in {shown}"

            Expect.stringContains
                (formatValue (VRecord("Other", [ "url", VStr "http://bob:pw@h/" ])))
                "bob:pw@h"
                "only a request's url is a display of a credential"
        }
        test "stripUserinfo drops the credential for what weir commits [D:lock-no-userinfo]" {
            Expect.equal
                (Weir.Http.stripUserinfo "http://bob:S3cr3t@127.0.0.1:1/s.json")
                "http://127.0.0.1:1/s.json"
                "userinfo dropped"

            Expect.equal
                (Weir.Http.stripUserinfo "https://host/p@v2?to=a@b")
                "https://host/p@v2?to=a@b"
                "an @ after the authority is data"
        }
        test "a header refusal escapes the name it quotes [D:error-data-escape]" {
            let name = "a\u001b]52;c;ZXZp\u0007\rb"
            let shown = Weir.Http.headerInjectionMessage "request header" name "CR" "name"
            Expect.stringContains shown "'a\\x1b]52;c;ZXZp\\x07\\x0db'" "rendered visibly"
            Expect.isFalse (shown.IndexOfAny [| '\u001b'; '\u0007'; '\r' |] >= 0) $"raw control in {shown}"

            let shown =
                Weir.Http.headerUnsendableMessage "response header" "a\u001bb" "its name carries U+001B"

            Expect.isFalse (shown.Contains '\u001b') $"raw ESC in {shown}"
        }
    ]

let hunt2CheckerTests =
    // the second hunt's checker/adapter backlog: `+` decided by a later
    // anchor [D:plus-defer], generic records at the wire
    // [D:wire-generics], a `Yaml` field read by `from json`
    // [D:json-yaml-field]
    let text v =
        forceSeq v
        |> List.map (function
            | VStr s -> s
            | v -> failtest $"expected text, got {formatValue v}")
        |> String.concat "\n"

    let genv =
        env
        |> declare "type GB<'a> = { v: 'a; [<Wire \"type\">] kind: string }"
        |> declare "type GW = { one: GB<int>; two: GB<string>; p: Option<GB<bool>> }"
        |> declare "type GL<'a> = { h: 'a; next: Option<GL<'a>> }"
        |> declare "type YR = { name: string; extra: Yaml; more: Option<Yaml> }"

    // a JSON document as the weir source of a one-element string list
    let doc (json: string) =
        "[\"" + json.Replace("\"", "\\\"") + "\"]"

    testList "hunt2 checker backlog" [
        test "a later anchor decides `+` over two unknowns [D:plus-defer]" {
            Expect.equal (checkOk "fun a -> fun b -> a + b + 0").Ty (TFun(TInt, TFun(TInt, TInt))) "a + b + 0"
            Expect.equal (checkOk "fun a -> fun b -> 0 + a + b").Ty (TFun(TInt, TFun(TInt, TInt))) "0 + a + b"

            Expect.equal
                (checkOk "fun a -> fun b -> fun c -> a + b + c + \"x\"").Ty
                (TFun(TStr, TFun(TStr, TFun(TStr, TStr))))
                "a chain resolves back to its first link"

            Expect.equal
                (checkOk "fun a -> fun b -> Str.length (a + b)").Ty
                (TFun(TStr, TFun(TStr, TInt)))
                "the result's use decides"

            Expect.equal (checkOk "fun a -> fun b -> (a + b) + 1.5").Ty (TFun(TFloat, TFun(TFloat, TFloat))) "float"

            Expect.equal
                (checkOk "fun x -> (let h y = x + y in h 1)").Ty
                (TFun(TInt, TInt))
                "an undecided + stays monomorphic until a use decides it"

            Expect.equal (run "let f a b = a + b + 0 in f 1 2") (VInt 3L) "runs"
        }
        test "an undecided `+` keeps the cannot-infer teaching; a non-addable anchor refuses [D:plus-defer]" {
            Expect.stringContains
                (checkErr "fun a -> fun b -> a + b").Message
                "cannot infer the operand types of '+'"
                ""

            Expect.stringContains
                (checkErr "fun a -> fun b -> a + b + true").Message
                "operator '+' is not defined for bool"
                ""

            // Instant + Duration or Duration + Instant: still ambiguous
            Expect.stringContains
                (checkErr "fun a -> fun b -> (a + b) - Instant.now ()").Message
                "cannot infer the operand types of '+'"
                ""
        }
        test "a generic record crosses json/jsonl/yaml once instantiated [D:wire-generics]" {
            let one =
                runUnder genv (doc """{"v":1,"type":"k"}""" + " |> from json GB<int> |> _.v")

            Expect.equal one (VInt 1L) "from json GB<int>"

            let many =
                runUnder
                    genv
                    (doc """[{"v":"a","type":"k"}]"""
                     + " |> from json seq<GB<string>> |> Seq.map _.v |> Seq.head")

            Expect.equal many (VStr "a") "from json seq<GB<string>> parses and reads"

            let w =
                "{ one = { v = 1; kind = \"a\" }; two = { v = \"s\"; kind = \"b\" }; p = Some { v = true; kind = \"c\" } }"

            Expect.equal
                (runUnder genv $"{w} |> to json" |> text)
                """{"one":{"v":1,"type":"a"},"two":{"v":"s","type":"b"},"p":{"v":true,"type":"c"}}"""
                "instances write under their declared name's wire keys"

            Expect.equal
                (runUnder genv $"{w} |> to json |> from json GW |> fun r -> r == {w}")
                (VBool true)
                "json roundtrip"

            Expect.equal
                (runUnder genv $"{w} |> to yaml |> from yaml GW |> fun r -> r == {w}")
                (VBool true)
                "yaml roundtrip"

            Expect.equal (runUnder genv $"[{w}] |> to jsonl |> from jsonl GW |> Seq.length") (VInt 1L) "jsonl"
        }
        test
            "an uninstantiated generic teaches the instance; a cycle and the other adapters still refuse [D:wire-generics]" {
            Expect.stringContains
                (checkErrUnder genv (doc "{}" + " |> from json GB")).Message
                "name the instance the document holds, e.g. from json GB<int>"
                ""

            Expect.stringContains
                (checkErrUnder genv (doc "{}" + " |> from json GL<int>")).Message
                "the type cycle GL<int> → GL<int> cannot cross the JSON boundary"
                ""

            Expect.stringContains
                (checkErrUnder genv (doc "{}" + " |> from toml GB<int>")).Message
                "'from toml' needs a monomorphic record"
                ""

            Expect.stringContains
                (checkErrUnder genv "fun x -> { v = x; kind = \"k\" } |> to json").Message
                "'to json' needs a concrete type, but this value is generic (GB<"
                ""
        }
        test "from json reads a Yaml field whole; to json writes it back [D:json-yaml-field]" {
            let src = doc """{"name":"a","extra":{"k":[1,2.5,"s",true,null,{}]}}"""

            Expect.equal
                (runUnder genv $"{src} |> from json YR |> to json" |> text)
                """{"name":"a","extra":{"k":[1,2.5,"s",true,null,{}]}}"""
                "json -> Yaml -> json"

            Expect.equal
                (runUnder genv $"{src} |> from json YR |> to yaml" |> text)
                "name: a\nextra:\n  k:\n    - 1\n    - 2.5\n    - s\n    - true\n    - null\n    - {}"
                "the same node renders as yaml"

            match runUnder genv (doc """{"name":"a","extra":null}""" + " |> from json YR") with
            | VRecord("YR", [ _; "extra", VUnion("YNull", None); "more", VUnion("None", None) ]) -> ()
            | v ->
                failtest
                    $"null is a YNull node (as from yaml reads it), a missing Option<Yaml> is None: {formatValue v}"
        }
    ]

let hunt3BehaviorTests =
    // the third hunt's formats / generators / checker items: JSON depth
    // [D:json-depth], TOML local date-times [D:toml-local-instant],
    // duplicate JSON keys into Yaml [D:json-yaml-dupkeys], schema drafting
    // [D:schema-int-or-string] [D:schema-int-enum]
    // [D:schema-validation-keys], the bare within's always in a body
    // [D:within-always], forced calls [D:reenum-forced-call], inline
    // Env.load duplicates [D:env-inline-type], Instant + unknown
    // [D:instant-plus-infer]
    let text v =
        forceSeq v
        |> List.map (function
            | VStr s -> s
            | v -> failtest $"expected text, got {formatValue v}")
        |> String.concat "\n"

    let henv =
        env
        |> declare "type HN = { a: Yaml }"
        |> declare "type HT = { [<Iso8601>] start: Instant }"
        |> declare "type HS = { start: string }"

    let doc (json: string) =
        "[\"" + json.Replace("\"", "\\\"") + "\"]"

    let deep n =
        String.replicate n "{\"a\":" + "1" + String.replicate n "}"

    let gen src =
        match Weir.Contracts.parseSchema "t" src with
        | Error e -> failtest e
        | Ok d ->
            match
                Weir.SchemaTypes.moduleText Weir.Parser.keywords Set.empty "t" None "0123456789abcdef" "http://src" d
            with
            | Ok g -> g.Text
            | Error e -> failtest e

    testList "hunt3 behaviour" [
        test "from json reads a document up to weir's 100-level bound; past it names the depth [D:json-depth]" {
            Expect.equal (runUnder henv (doc (deep 99) + " |> from json HN |> fun _ -> 1")) (VInt 1L) "99 deep reads"

            let msg =
                (Expect.throwsC (fun () -> runUnder henv (doc (deep 101) + " |> from json HN") |> ignore) id).Message

            Expect.stringContains msg "nests deeper than 100 levels" "the depth named"
            Expect.isFalse (msg.Contains "not valid JSON") "never 'not valid JSON' for valid JSON"

            let bad =
                (Expect.throwsC (fun () -> runUnder henv (doc "{\"a\": " + " |> from json HN") |> ignore) id).Message

            Expect.stringContains bad "not valid JSON" "invalid JSON keeps its own error"
        }
        test "the depth refusal tells valid from invalid in one reader pass [D:json-deep-linear]" {
            let kind (text: string) =
                match Weir.Types.parseJsonBounded text with
                | Ok d ->
                    d.Dispose()
                    "ok"
                | Error(Some _) -> "deep"
                | Error None -> "invalid"

            Expect.equal (kind (deep 100)) "ok" "100 deep is within the bound"
            Expect.equal (kind (deep 101)) "deep" "101 deep names the depth"
            Expect.equal (kind ("[" + deep 100 + "]")) "deep" "an array level counts"
            Expect.equal (kind (deep 5000)) "deep" "far past the bound"
            Expect.equal (kind (deep 5000 + "}")) "invalid" "a deep document with trailing junk is invalid"
            Expect.equal (kind (String.replicate 5000 "{\"a\":" + "1")) "invalid" "an unclosed deep document is invalid"
            // a BOM the parser accepts is skipped by the scan too
            if kind ("\uFEFF" + deep 99) = "ok" then
                Expect.equal (kind ("\uFEFF" + deep 101)) "deep" "a BOM'd deep document"

            match Weir.Types.parseJsonBytesBounded (System.Text.Encoding.UTF8.GetBytes(deep 101)) with
            | Error(Some _) -> ()
            | other -> failtest $"bytes: {other}"
        }
        test "a schema nested past 64 JSON levels parses [D:json-depth]" {
            let schema =
                String.replicate 32 "{\"type\":\"object\",\"properties\":{\"c\":"
                + "{\"type\":\"integer\"}"
                + String.replicate 32 "}}"

            match Weir.Contracts.parseSchema "t" schema with
            | Ok _ -> ()
            | Error e -> failtest e
        }
        test "from toml refuses an offset-less datetime into an Instant; an offset reads [D:toml-local-instant]" {
            let read v =
                $"[\"start = {v}\"] |> from toml HT |> fun r -> show r.start"

            Expect.equal (runUnder henv (read "2024-03-01T10:00:00+02:00")) (VStr "2024-03-01T08:00:00Z") "offset"
            Expect.equal (runUnder henv (read "2024-03-01T10:00:00Z")) (VStr "2024-03-01T10:00:00Z") "Z"

            for v, what in
                [
                    "2024-03-01T10:00:00", "a local date-time"
                    "2024-03-01 10:00:00", "a local date-time"
                    "2024-03-01", "a local date"
                    "10:00:00", "a local time"
                ] do
                let msg = (Expect.throwsC (fun () -> runUnder henv (read v) |> ignore) id).Message
                Expect.stringContains msg $"{v} is {what}" $"refused: {v}"
                Expect.stringContains msg "from toml: line 1" "located, toml-labelled"

            Expect.equal
                (runUnder henv "[\"start = 2024-03-01T10:00:00\"] |> from toml HS |> fun r -> r.start")
                (VStr "2024-03-01T10:00:00")
                "a string field keeps the text"
        }
        test "from toml and Instant.parse take lowercase t/z and a seconds-less time [D:instant-rfc3339-forms]" {
            let read v =
                $"[\"start = {v}\"] |> from toml HT |> fun r -> show r.start"

            for v, want in
                [
                    "1979-05-27t07:32:00z", "1979-05-27T07:32:00Z"
                    "1979-05-27T07:32Z", "1979-05-27T07:32:00Z"
                    "1979-05-27 07:32+02:00", "1979-05-27T05:32:00Z"
                    "1979-05-27t07:32:00.5z", "1979-05-27T07:32:00.500Z"
                ] do
                Expect.equal (runUnder henv (read v)) (VStr want) v

            Expect.equal
                (run "show (Instant.parse \"1979-05-27t07:32:00z\")")
                (VStr "1979-05-27T07:32:00Z")
                "Instant.parse: lowercase"

            Expect.equal
                (runUnder henv "[\"start = 1979-05-27T07:32\"] |> from toml HS |> fun r -> r.start")
                (VStr "1979-05-27T07:32")
                "a seconds-less local date-time keeps its text"

            Expect.throwsC (fun () -> runUnder henv (read "1979-05-27T07:32") |> ignore) (fun e ->
                Expect.stringContains e.Message "a local date-time" "still offset-less")
        }
        test "duplicate JSON keys into a Yaml field last-win at the first position [D:json-yaml-dupkeys]" {
            let src = doc """{"a": {"x": 1, "y": 2, "x": 3}}"""
            Expect.equal (runUnder henv $"{src} |> from json HN |> to json" |> text) """{"a":{"x":3,"y":2}}""" "json"

            Expect.equal
                (runUnder henv $"{src} |> from json HN |> to yaml |> from yaml HN |> to yaml"
                 |> text)
                "a:\n  x: 3\n  \"y\": 2"
                "the yaml round trip holds"
        }
        test "a mapping last-wins on read; to json and to yaml refuse a key twice [D:mapping-dupkeys]" {
            let penv = henv |> declare "type HP = { labels: seq<string * string> }"
            let src = doc """{"labels": {"a": "1", "b": "x", "a": "2"}}"""

            Expect.equal
                (runUnder penv $"{src} |> from json HP |> to json" |> text)
                """{"labels":{"a":"2","b":"x"}}"""
                "the last value at the first position"

            for write in [ "to json"; "to yaml" ] do
                for v in
                    [
                        "{ labels = [(\"k\", \"1\"); (\"k\", \"2\")] }"
                        "{ a = YMap [(\"k\", YInt 1); (\"j\", YInt 2); (\"k\", YInt 3)] }"
                    ] do
                    let msg =
                        (Expect.throwsC (fun () -> runUnder penv $"{v} |> {write}" |> text |> ignore) id).Message

                    Expect.stringContains
                        msg
                        $"{write}: the mapping holds the key 'k' twice"
                        $"{write} names the key: {v}"
        }
        test
            "to yaml writes an all-None record as {} and an empty top-level seq as []; a stream keeps every document [D:yaml-empty-doc]" {
            let yenv =
                henv
                |> declare "type YR = { cpu: Option<string>; memory: Option<string> }"
                |> declare "type YC = { name: string; resources: YR }"

            let none = "{ cpu = None; memory = None }"
            Expect.equal (runUnder yenv $"{none} |> to yaml" |> text) "{}" "a top-level all-None record"

            Expect.equal
                (runUnder yenv $"{{ name = \"web\"; resources = {none} }} |> to yaml" |> text)
                "name: web\nresources: {}"
                "nested"

            Expect.equal (runUnder yenv $"[{none}] |> to yaml" |> text) "- {}" "an element"

            Expect.equal
                (runUnder yenv $"{none} |> to yaml |> from yaml YR |> fun r -> r == {none}")
                (VBool true)
                "it reads back"

            Expect.equal
                (runUnder
                    yenv
                    $"{{ name = \"web\"; resources = {none} }} |> to yaml |> from yaml YC |> fun c -> c.resources == {none}")
                (VBool true)
                "nested reads back"

            let empty = $"[{none}] |> Seq.where (fun r -> r.cpu == Some \"9\")"
            Expect.equal (runUnder yenv $"{empty} |> to yaml" |> text) "[]" "an empty top-level seq"

            Expect.equal
                (runUnder yenv $"{empty} |> to yaml |> from yaml seq<YR> |> Seq.length")
                (VInt 0L)
                "it reads back"

            let xs =
                $"[{{ cpu = Some \"1\"; memory = None }}; {none}; {{ cpu = Some \"2\"; memory = None }}]"

            Expect.equal
                (runUnder yenv $"{xs} |> to yaml stream" |> text)
                "cpu: \"1\"\n---\n{}\n---\ncpu: \"2\""
                "the stream form"

            Expect.equal
                (runUnder yenv $"{xs} |> to yaml stream |> from yaml stream YR |> Seq.length")
                (VInt 3L)
                "no document dropped"
        }
        test
            "to yaml writes a top-level null as null, round-trips it, and keeps a null stream document [D:yaml-empty-doc]" {
            let yenv =
                henv |> declare "type YP = { a: Option<string>; b: seq<string * Option<int>> }"

            Expect.equal (runUnder yenv "[\"null\"] |> Yaml.parse |> to yaml" |> text) "null" "a top-level null"

            Expect.equal
                (runUnder
                    yenv
                    "[\"null\"] |> Yaml.parse |> to yaml |> Yaml.parse |> fun y -> match y with | YNull -> true | _ -> false")
                (VBool true)
                "it reads back"

            Expect.equal
                (runUnder yenv "[YNull; YStr \"x\"] |> to yaml stream" |> text)
                "null\n---\nx"
                "a stream document"

            Expect.equal
                (runUnder yenv "{ a = None; b = [(\"k\", None)] } |> to yaml" |> text)
                "b:\n  k: null"
                "a None field is omitted; a None pair value keeps its key"
        }
        test
            "to yaml writes an empty seq [] and an empty pair-seq {} at any depth; nested seqs nest block-wise [D:yaml-empty-typed]" {
            let kenv =
                henv
                |> declare
                    "type KC = { name: string; args: Option<seq<string>>; env: Option<seq<string * string>>; ports: seq<int> }"
                |> declare "type KM = { labels: Option<seq<string * string>>; annotations: seq<string * string> }"
                |> declare
                    "type KD = { metadata: KM; containers: seq<KC>; grid: seq<seq<seq<int>>>; ms: seq<seq<string * int>> }"
                |> declare "type KN = { n: Yaml }"

            let d =
                "{ metadata = { labels = Some []; annotations = [] }; containers = [{ name = \"a\"; args = Some []; env = Some []; ports = [] }]; grid = [[[1]; []]; []]; ms = [[]; [(\"k\", 1)]] }"

            Expect.equal
                (runUnder kenv $"{d} |> to yaml" |> text)
                "metadata:\n  labels: {}\n  annotations: {}\ncontainers:\n  - name: a\n    args: []\n    env: {}\n    ports: []\ngrid:\n  -\n    -\n      - 1\n    - []\n  - []\nms:\n  - {}\n  - k: 1"
                "Some [] is [], an empty mapping {}, a nested seq under a bare -"

            Expect.equal
                (runUnder kenv $"{d} |> to yaml |> from yaml KD |> to json" |> text)
                (runUnder kenv $"{d} |> to json" |> text)
                "it reads back: Some [] stays Some [], not None"

            Expect.equal
                (runUnder kenv $"[{d}; {d}] |> to yaml stream |> from yaml stream KD |> to json"
                 |> text)
                (runUnder kenv $"[{d}; {d}] |> to json" |> text)
                "the stream form too"

            let n =
                "{ n = YSeq [YSeq [YInt 1; YSeq [YStr \"x\"]]; YMap [(\"a\", YSeq [YInt 2])]; YSeq []] }"

            Expect.equal
                (runUnder kenv $"{n} |> to yaml" |> text)
                "\"n\":\n  -\n    - 1\n    -\n      - x\n  - a:\n      - 2\n  - []"
                "a nested YSeq"

            Expect.equal
                (runUnder kenv $"{n} |> to yaml |> from yaml KN |> to json" |> text)
                (runUnder kenv $"{n} |> to json" |> text)
                "it reads back"
        }
        test
            "schema drafting: int-or-string is Yaml, an integer enum is int [D:schema-int-or-string][D:schema-int-enum]" {
            let g =
                gen
                    """{ "type": "object", "required": ["level"],
                           "properties": {
                             "port": { "anyOf": [{ "type": "integer" }, { "type": "string" }], "x-kubernetes-int-or-string": true },
                             "bare": { "x-kubernetes-int-or-string": true },
                             "level": { "type": "integer", "enum": [1, 2, 3] },
                             "mixed": { "enum": [1, "two"] } } }"""

            Expect.stringContains g "port: Option<Yaml>" "anyOf int/string → Yaml"
            Expect.stringContains g "bare: Option<Yaml>" "the marker alone → Yaml"
            Expect.stringContains g "YInt n, a string as YStr s" "the note names the cases"
            Expect.stringContains g "\n    level: int\n" "an integer enum drafts int"
            Expect.stringContains g "an enum of 1, 2, 3 — drafted int" "its values in the note"
            Expect.stringContains g "mixed: Option<Yaml>" "a mixed enum drafts Yaml [D:schema-enum-kind]"
        }
        test "schema drafting: number-or-string and format int-or-string are Yaml [D:schema-num-or-string]" {
            let g =
                gen
                    """{ "type": "object",
                           "properties": {
                             "cpu": { "oneOf": [{ "type": ["string", "null"] }, { "type": ["number", "null"] }] },
                             "target": { "anyOf": [{ "type": "number" }, { "type": "integer" }, { "type": "string" }] },
                             "port": { "type": "string", "format": "int-or-string" },
                             "maybe": { "type": ["string", "null"], "format": "int-or-string" },
                             "name": { "type": "string", "format": "hostname" } } }"""

            Expect.stringContains g "cpu: Option<Yaml>" "number-or-string → Yaml"
            Expect.stringContains g "target: Option<Yaml>" "number/integer/string → Yaml"

            Expect.stringContains
                g
                "a number reads as YInt n or YFloat f, a string as YStr s"
                "the note names the cases"

            Expect.stringContains g "port: Option<Yaml>" "format int-or-string → Yaml"
            Expect.stringContains g "maybe: Option<Yaml>" "a nullable one is Option<Yaml> once"

            Expect.stringContains
                g
                "// note: port: format int-or-string — drafted Yaml"
                "the format case has its own note"

            Expect.stringContains g "name: Option<string>" "another format drafts its type"
            Expect.stringContains g "// note: not enforced: format on name" "another format is noted, not enforced"
            Expect.isFalse (g.Contains "format on port") "int-or-string is not also 'not enforced'"
        }
        test "schema drafting: boolean beside string or number is Yaml [D:schema-bool-or-string]" {
            let g =
                gen
                    """{ "type": "object",
                           "properties": {
                             "cancel": { "type": ["boolean", "string"] },
                             "bn": { "type": ["boolean", "number"] },
                             "bis": { "type": ["boolean", "integer", "string"] },
                             "one": { "oneOf": [{ "type": "boolean" }, { "type": "string" }] },
                             "nb": { "type": ["boolean", "string", "null"] } } }"""

            for f in [ "cancel"; "bn"; "bis"; "one"; "nb" ] do
                Expect.stringContains g $"{f}: Option<Yaml>" $"{f} drafts Yaml"

            Expect.stringContains
                g
                "// note: cancel: the schema allows boolean/string — drafted Yaml; a bool reads as YBool b, a string as YStr s"
                "the note names the cases"

            Expect.stringContains
                g
                "bn: the schema allows boolean/number — drafted Yaml; a bool reads as YBool b, a number as YInt n or YFloat f"
                "bool|number"

            Expect.stringContains
                g
                "bis: the schema allows boolean/integer/string — drafted Yaml; a bool reads as YBool b, an int as YInt n, a string as YStr s"
                "bool|int|string"

            Expect.isFalse (g.Contains "drafted string") "never string"
        }
        test
            "schema drafting: a type array mixing a scalar with a container is Yaml, never a silent pick [D:schema-mixed-kinds]" {
            let g =
                gen
                    """{ "type": "object", "required": ["cmd"],
                           "properties": {
                             "cmd": { "type": ["string", "array"], "items": { "type": "string" } },
                             "runs": { "type": ["string", "array", "null"], "items": { "type": "string" } },
                             "env": { "type": ["object", "string"], "additionalProperties": { "type": "string" } },
                             "both": { "type": ["object", "array"] },
                             "plain": { "type": ["array", "null"], "items": { "type": "string" } } } }"""

            Expect.stringContains g "\n    cmd: Yaml\n" "[string, array] → Yaml"
            Expect.stringContains g "runs: Option<Yaml>" "nullable once"
            Expect.stringContains g "env: Option<Yaml>" "[object, string] → Yaml"
            Expect.stringContains g "both: Option<Yaml>" "[object, array] → Yaml"
            Expect.stringContains g "plain: Option<seq<string>>" "a nullable array stays an array"

            Expect.stringContains
                g
                "// note: cmd: the schema allows array/string — drafted Yaml; a string reads as YStr s, a sequence as YSeq items"
                "the note names the cases"

            Expect.stringContains
                g
                "both: the schema allows array/object — drafted Yaml; a sequence reads as YSeq items, a mapping as YMap entries"
                "both containers"
        }
        test
            "schema drafting: an anyOf mixing a scalar with an array drafts Yaml (starship port) [D:schema-anyof-mixed]" {
            // trimmed from starship's config-schema.json: custom.shell is
            // string-or-array, python.python_binary nests it once more
            let g =
                gen
                    """{ "type": "object",
                           "$defs": {
                             "VecOr_string": { "anyOf": [{ "type": "string" }, { "type": "array", "items": { "type": "string" } }] },
                             "VecOr_VecOr_string": { "anyOf": [{ "$ref": "#/$defs/VecOr_string" }, { "type": "array", "items": { "$ref": "#/$defs/VecOr_string" } }] },
                             "Base": { "type": "object", "properties": { "x": { "type": "string" } }, "required": ["x"] },
                             "Other": { "type": "object", "properties": { "y": { "type": "integer" } }, "required": ["y"] } },
                           "properties": {
                             "shell": { "$ref": "#/$defs/VecOr_string", "default": [] },
                             "python_binary": { "$ref": "#/$defs/VecOr_VecOr_string" },
                             "objs": { "anyOf": [{ "$ref": "#/$defs/Base" }, { "$ref": "#/$defs/Other" }] } } }"""

            Expect.stringContains g "\n    shell: Option<Yaml>\n" "string-or-array drafts Yaml"
            Expect.stringContains g "\n    python_binary: Option<Yaml>\n" "the nested anyOf through a $ref too"

            Expect.stringContains
                g
                "// note: VecOr_string: anyOf allows array/string — drafted Yaml; a string reads as YStr s, a sequence as YSeq items"
                "the note names each kind's case"

            Expect.stringContains
                g
                "// note: VecOr_VecOr_string: anyOf allows array/string — drafted Yaml"
                "the nested one"

            Expect.stringContains
                g
                "objs: anyOf — drafted the FIRST variant only"
                "two objects keep the first-variant rule"
        }
        test "schema drafting: a diamond of $refs under anyOf costs its definitions, not 2^depth" {
            // D0 is string-or-array; each Dk is anyOf [Dk-1; Dk-1] — 2^30
            // reference paths in a ~2 KB schema
            let defs =
                [
                    "\"D0\": { \"anyOf\": [{ \"type\": \"string\" }, { \"type\": \"array\", \"items\": { \"type\": \"string\" } }] }"
                ]
                @ [
                    for k in 1..30 ->
                        $"\"D{k}\": {{ \"anyOf\": [{{ \"$ref\": \"#/$defs/D{k - 1}\" }}, {{ \"$ref\": \"#/$defs/D{k - 1}\" }}] }}"
                ]
                |> String.concat ", "

            let sw = Stopwatch.StartNew()

            let g =
                gen
                    $"{{ \"type\": \"object\", \"$defs\": {{ {defs} }}, \"properties\": {{ \"top\": {{ \"$ref\": \"#/$defs/D30\" }} }} }}"

            Expect.isLessThan sw.Elapsed.TotalSeconds 5.0 "bounded by the definition count"
            Expect.stringContains g "\n    top: Option<Yaml>\n" "the mixed kinds still draft Yaml"
        }
        test "schema drafting: a typed enum drafts its type, a mixed enum Yaml [D:schema-enum-kind]" {
            let g =
                gen
                    """{ "type": "object", "required": ["on", "ratio", "n", "mixed", "untyped"],
                           "properties": {
                             "on": { "type": "boolean", "enum": [true] },
                             "ratio": { "type": "number", "enum": [0.5, 1.5] },
                             "n": { "type": "number", "enum": [1, 2] },
                             "untyped": { "enum": [1, 2.5] },
                             "flag": { "enum": [true, false] },
                             "mixed": { "enum": ["a", 1, true] },
                             "s": { "type": "string", "enum": ["a", "b"] } } }"""

            Expect.stringContains g "\n    on: bool\n" "a boolean enum drafts bool"
            Expect.stringContains g "\n    ratio: float\n" "a number enum drafts float"
            Expect.stringContains g "\n    n: float\n" "the declared number type wins over integer values"
            Expect.stringContains g "\n    untyped: float\n" "integers among numbers are numbers"
            Expect.stringContains g "flag: Option<bool>" "an untyped boolean enum drafts bool"
            Expect.stringContains g "\n    mixed: Yaml\n" "a mixed enum drafts Yaml"
            Expect.stringContains g "an enum mixing kinds (a, 1, true) — drafted Yaml" "its note"
            Expect.stringContains g "s: Option<string>" "a string enum stays string"
            Expect.stringContains g "an enum of 0.5, 1.5 — drafted float" "the values in the note"
        }
        test
            "schema drafting: [X, {type: null}] in either order is Option<X> for a $ref, array or object [D:schema-null-alt]" {
            let g =
                gen
                    """{ "type": "object", "required": ["a", "b", "c", "d", "e"],
                           "$defs": { "Base": { "type": "object", "properties": { "x": { "type": "string" } }, "required": ["x"] } },
                           "properties": {
                             "a": { "anyOf": [{ "$ref": "#/$defs/Base" }, { "type": "null" }] },
                             "b": { "anyOf": [{ "type": "null" }, { "$ref": "#/$defs/Base" }] },
                             "c": { "oneOf": [{ "type": "array", "items": { "type": "string" } }, { "type": "null" }] },
                             "d": { "anyOf": [{ "type": ["null"] }, { "type": "object", "properties": { "y": { "type": "integer" } } }] },
                             "e": { "anyOf": [{ "type": "integer" }, { "type": "null" }] } } }"""

            Expect.stringContains g "\n    a: Option<Base>\n" "ref, null"
            Expect.stringContains g "\n    b: Option<Base>\n" "null, ref"
            Expect.stringContains g "\n    c: Option<seq<string>>\n" "array, null (oneOf)"
            Expect.stringContains g "\n    d: Option<D>\n" "null, object"
            Expect.stringContains g "\n    e: Option<int>\n" "a scalar as before"
            Expect.isFalse (g.Contains "FIRST variant") "no first-variant guess"
            Expect.isFalse (g.Contains "only null") "never 'allows only null'"
        }
        test "schema drafting: a $ref's sibling properties/required merge [D:schema-ref-siblings]" {
            let g =
                gen
                    """{ "type": "object", "required": ["item"],
                           "$defs": { "Base": { "type": "object", "properties": { "x": { "type": "string" } }, "required": ["x"] } },
                           "properties": {
                             "item": { "$ref": "#/$defs/Base", "properties": { "y": { "type": "integer" } }, "required": ["y"] },
                             "plain": { "$ref": "#/$defs/Base", "description": "annotations stay annotations" },
                             "odd": { "$ref": "#/$defs/Base", "items": { "type": "string" } } } }"""

            Expect.stringContains g "type Item = {\n    x: string\n    y: int\n}" "merged, required kept"
            Expect.stringContains g "a $ref with sibling properties — drafted the merged record" "noted"
            Expect.stringContains g "plain: Option<Base>" "an annotation sibling is the plain ref"
            Expect.stringContains g "odd: a $ref with sibling keywords that are not both objects" "never a silent drop"
        }
        test "schema: deep in-document pointers and the root # resolve [D:schema-deep-refs]" {
            let g =
                gen
                    """{ "type": "object",
                           "definitions": { "opts": { "type": "object", "properties": { "inner": { "type": "object", "properties": { "a": { "type": "string" } } } } } },
                           "properties": {
                             "o": { "$ref": "#/definitions/opts" },
                             "copy": { "$ref": "#/definitions/opts/properties/inner" },
                             "self": { "$ref": "#" } } }"""

            Expect.stringContains g "copy: Option<Inner>" "a deep pointer drafts its target"
            Expect.stringContains g "inner: Option<Inner>" "one type for both routes"
            Expect.stringContains g "self: Option<Yaml>" "# is the root: a cycle drafts the node"

            match
                Weir.Contracts.parseSchema "t" """{ "type": "object", "properties": { "x": { "$ref": "#/nope/a" } } }"""
            with
            | Error e -> Expect.stringContains e "\"#/nope/a\" does not resolve" "a pointer to nothing refuses"
            | Ok _ -> failtest "a dangling pointer must refuse"

            match
                Weir.Contracts.parseSchema
                    "t"
                    """{ "type": "object", "properties": { "x": { "$ref": "other.json#/a" } } }"""
            with
            | Error e -> Expect.stringContains e "does not point into this document" "cross-file stays refused"
            | Ok _ -> failtest "a cross-file ref must refuse"
        }
        test "schema drafting: bool/int-or-string oneOf with a $ref side drafts as inline [D:schema-bool-or-string]" {
            let g =
                gen
                    """{ "type": "object",
                           "definitions": { "expr": { "type": "string", "pattern": "^\\$" }, "s": { "type": "string" },
                                            "level": { "type": "object", "properties": { "a": { "type": "string" } } } },
                           "properties": {
                             "cancel": { "oneOf": [{ "type": "boolean" }, { "$ref": "#/definitions/expr" }] },
                             "port": { "oneOf": [{ "type": "integer" }, { "$ref": "#/definitions/s" }] },
                             "perm": { "oneOf": [{ "type": "string", "enum": ["read-all"] }, { "$ref": "#/definitions/level" }] },
                             "any": { "oneOf": [{ "type": "string" }, { "type": "number" }, { "type": "object" }, { "type": "array" }] } } }"""

            Expect.stringContains g "cancel: Option<Yaml>" "bool | $ref string"
            Expect.stringContains g "cancel: the schema allows boolean/string — drafted Yaml" "the inline note"
            Expect.stringContains g "port: Option<Yaml>" "int | $ref string"

            Expect.stringContains
                g
                "perm: Option<Yaml>"
                "one alternative per kind is the mixed draft [D:schema-mixed-kinds]"

            Expect.stringContains g "any: Option<Yaml>" "any JSON value"
        }
        test "schema drafting: an enum containing null drafts Option<T>; the note says null [D:schema-enum-null]" {
            let g =
                gen
                    """{ "type": "object", "required": ["d", "g", "h", "n"],
                           "properties": {
                             "d": { "enum": ["x", null] },
                             "g": { "enum": [null] },
                             "h": { "type": ["string", "null"], "enum": ["a", null] },
                             "n": { "enum": [1, 2, null] } } }"""

            Expect.stringContains g "\n    d: Option<string>\n" "d"
            Expect.stringContains g "\n    g: Option<string>\n" "g"
            Expect.stringContains g "\n    h: Option<string>\n" "h"
            Expect.stringContains g "\n    n: Option<int>\n" "n"
            Expect.stringContains g "d: an enum of 'x', null — drafted Option<string>" "null unquoted"
            Expect.stringContains g "n: an enum of 1, 2, null — drafted Option<int>" "a numeric one too"
            Expect.isFalse (g.Contains "'null'") "never 'null'"
        }
        test "schema: a root $id, const and the content keywords are admitted and noted [D:schema-const-format]" {
            let g =
                gen
                    """{ "$id": "https://example.com/app.json", "type": "object",
                           "properties": {
                             "kind": { "type": "string", "const": "Deployment" },
                             "v": { "const": 2 },
                             "blob": { "type": "string", "contentEncoding": "base64", "contentMediaType": "image/png" } } }"""

            Expect.stringContains g "kind: Option<string>" "const drafts the declared type"
            Expect.stringContains g "v: Option<int>" "an untyped const drafts its value's type"
            Expect.stringContains g "// note: not enforced: const on kind, v" "const is noted"
            Expect.stringContains g "// note: not enforced: contentEncoding on blob" "contentEncoding"
            Expect.stringContains g "// note: not enforced: contentMediaType on blob" "contentMediaType"
            Expect.isFalse (g.Contains "$id") "the root $id is ignored"

            match
                Weir.Contracts.parseSchema
                    "t"
                    """{ "type": "object", "properties": { "p": { "$id": "x", "type": "string" } } }"""
            with
            | Error e -> Expect.stringContains e "'$id'" "a nested $id still refuses"
            | Ok _ -> failtest "a nested $id must refuse"
        }
        test "schema validation keywords are admitted and noted, never enforced [D:schema-validation-keys]" {
            let g =
                gen
                    """{ "type": "object", "$comment": "c",
                           "properties": {
                             "name": { "type": "string", "minLength": 1, "maxLength": 9, "pattern": "^a" },
                             "n": { "type": "integer", "minimum": 0, "readOnly": true },
                             "tags": { "type": "array", "items": { "type": "string", "minLength": 2 }, "maxItems": 3, "uniqueItems": true, "deprecated": true } } }"""

            Expect.stringContains g "name: Option<string>" "drafts as without the keywords"

            Expect.stringContains
                g
                "// note: not enforced: minLength on name, tags[]"
                "one note per keyword, every place"

            Expect.stringContains g "// note: not enforced: pattern on name" "pattern"
            Expect.stringContains g "// note: not enforced: readOnly on n" "readOnly"
            Expect.stringContains g "// note: not enforced: uniqueItems on tags" "uniqueItems"
            Expect.isFalse (g.Contains "$comment") "a comment is documentation, not noted"

            // shape-changing keywords still refuse
            match
                Weir.Contracts.parseSchema
                    "t"
                    """{ "type": "object", "properties": { "p": { "not": { "type": "string" } } } }"""
            with
            | Error e -> Expect.stringContains e "'not'" "not refuses"
            | Ok _ -> failtest "not must refuse"
        }
        test "schema validation notes cover only the definitions the module drafts [D:schema-validation-keys]" {
            let g =
                gen
                    """{ "$ref": "#/definitions/a.v1.Used",
                           "definitions": {
                             "a.v1.Used": { "type": "object", "properties": { "s": { "type": "string", "maxLength": 3 } } },
                             "a.v1.Unused": { "type": "object", "properties": { "t": { "type": "string", "pattern": "x" } } } } }"""

            Expect.stringContains g "// note: not enforced: maxLength on Used.s" "a reached definition's keyword"
            Expect.isFalse (g.Contains "pattern") "an unreached definition stays quiet"
        }
        test "an indented bare within … always followed by a statement assembles [D:within-always]" {
            errorsCleanIn
                "wa.weir"
                [
                    "let f () ="
                    "    within"
                    "        print \"body\""
                    "    always"
                    "        print \"cleanup\""
                    "    print \"after\""
                    "f ()"
                    "if true then"
                    "    within"
                    "        within"
                    "            print \"inner\""
                    "        always"
                    "            print \"ic\""
                    "        print \"mid\""
                    "    always"
                    "        print \"oc\""
                    "    print \"after\""
                ]
                "nested and sibling statements"
        }
        test "a call of a function that freezes carries nothing [D:reenum-forced-call]" {
            let reenum lines =
                diagsIn "re.weir" lines
                |> List.filter (fun d -> d.Message.Contains "re-enumeration")

            Expect.isEmpty
                (reenum [
                    "let xs = git ls-files"
                    "let f () = xs |> Seq.freeze"
                    "let a = f ()"
                    "print $\"{a |> Seq.length}\""
                    "print $\"{a |> Seq.length}\""
                ])
                "the result is a snapshot"

            Expect.isEmpty
                (reenum [
                    "let xs = git ls-files"
                    "let g n = xs |> Seq.take n |> Seq.freeze"
                    "let a = 2 |> g"
                    "print $\"{a |> Seq.length}\""
                    "print $\"{a |> Seq.length}\""
                ])
                "a piped call too"

            // each call still pulls once
            Expect.equal
                (reenum [
                    "let xs = git ls-files"
                    "let f () = xs |> Seq.freeze"
                    "let a = f ()"
                    "let b = f ()"
                    "print $\"{a |> Seq.length}\""
                 ]
                 |> List.length)
                1
                "two calls are two pulls"
        }
        test "inline Env.load / Args.load refuse a duplicate field [D:env-inline-type]" {
            match errorsIn "e.weir" [ "let e = Env.load {| H3F_P: string; H3F_P: int |}"; "print e.H3F_P" ] with
            | e :: _ -> Expect.stringContains e.Message "duplicate field 'H3F_P' in the anonymous record" "Env.load"
            | [] -> failtest "Env.load must refuse"

            match errorsIn "a.weir" [ "let a = Args.load {| v: bool; v: string |}"; "print a.v" ] with
            | e :: _ -> Expect.stringContains e.Message "duplicate field 'v' in the anonymous record" "Args.load"
            | [] -> failtest "Args.load must refuse"
        }
        test "Instant + an unknown infers the unknown as a Duration [D:instant-plus-infer]" {
            Expect.equal (checkOk "fun d -> Instant.now () + d").Ty (TFun(TDur, TInstant)) "point + unknown"
            Expect.equal (checkOk "fun d -> d + Instant.now ()").Ty (TFun(TDur, TInstant)) "unknown + point"

            Expect.equal
                (checkOk "fun a -> fun b -> a + b + Instant.now ()").Ty
                (TFun(TDur, TFun(TDur, TInstant)))
                "a deferred + decided by the point: both operands are Durations"

            Expect.stringContains
                (checkErr "Instant.now () + Instant.now ()").Message
                "two points don't add"
                "two real points"

            Expect.isEmpty
                (errorsIn "f5.weir" [
                    "let at d = Instant.now () + d"
                    "print $\"{at 5m}\""
                    "let f a b = a + b + Instant.now ()"
                    "print $\"{f 1s 2s}\""
                ])
                "f5a and f5b check clean"
        }
    ]

// ---- hunt-3 performance cliffs: semantics pinned, growth guarded ---------
// [D:value-hash] [D:env-open-index] [D:one-scanner]. The growth
// guards time one input and its 10x in process; linear work grows ~10x,
// the quadratic forms these replaced grew ~100x. 25x is the acceptance
// harness's rule (tools/hunt3-repro.weir), wide enough for a noisy runner.
let hunt3r5FormatTests =
    // hunt-3 round 5, formats: YAML number text (ruling R3)
    // [D:yaml-number-text], exponent floats [D:yaml-interop], tabs and
    // Unicode white space [D:yaml-ws], block-scalar `%` and U+2028
    // [D:block-scalars], XML labels (ruling R4) [D:xml-labels] and
    // whitespace text [D:xml-whitespace], try's bigint digits
    // [D:yaml-node-bigint], schema drafting [D:schema-required-nullable]
    // [D:schema-uint64] [D:schema-enum-null]
    let text v =
        forceSeq v
        |> List.map (function
            | VStr s -> s
            | v -> failtest $"expected text, got {formatValue v}")
        |> String.concat "\n"

    let lit (lines: string list) =
        "["
        + (lines
           |> List.map (fun l -> "\"" + l.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
           |> String.concat "; ")
        + "]"

    let gen src =
        match Weir.Contracts.parseSchema "t" src with
        | Error e -> failtest e
        | Ok d ->
            match
                Weir.SchemaTypes.moduleText Weir.Parser.keywords Set.empty "t" None "0123456789abcdef" "http://src" d
            with
            | Ok g -> g.Text
            | Error e -> failtest e

    let xenv =
        env
        |> declare "type XW = { Sep: string; Tab: string; Two: string; Word: string; Empty: string }"

    let xb (bytes: byte[]) =
        $"Bytes.fromBase64 \"{System.Convert.ToBase64String bytes}\""

    let xdoc (label: string) (title: byte[]) =
        Array.concat [
            System.Text.Encoding.ASCII.GetBytes($"<?xml version=\"1.0\" encoding=\"{label}\"?><Root><Title>caf")
            title
            System.Text.Encoding.ASCII.GetBytes "</Title></Root>"
        ]

    let tenv = xenv |> declare "type XT = { Title: string }"

    testList "hunt3r5 formats" [
        test
            "yaml: a leading-zero number keeps its text; a typed int read refuses, teaching 1.1's octal (R3) [D:yaml-number-text]" {
            Expect.equal
                (run (lit [ "mode: 0644"; "neg: -007"; "d: 644" ] + " |> Yaml.parse |> to yaml")
                 |> text)
                "mode: 0644\nneg: -007\nd: 644"
                "a read-modify-write leaves the text alone"

            Expect.equal
                (run ("show (" + lit [ "mode: 0644" ] + " |> Yaml.parse)"))
                (VStr "YMap ([(\"mode\", YInt 644)])")
                "the node is still YAML 1.2's decimal int"

            Expect.equal
                (run (lit [ "mode: 0644" ] + " |> Yaml.parse |> to json") |> text)
                "{\"mode\":644}"
                "json has no octal: the value writes"

            Expect.equal
                (run (
                    lit [ "mode: 0644" ]
                    + " |> Yaml.parse |> try |> Option.map (fun n -> n |> to yaml |> Str.join \"\") |> Option.defaultValue \"\""
                ))
                (VStr "mode: 0644")
                "the text rides try's force too"

            let m = msgOf (lit [ "mode: 0644" ] + " |> from yaml {| mode: int |}")
            Expect.stringContains m "'0644' has a leading zero" "names the literal"
            Expect.stringContains m "octal (420)" "says what a 1.1 reader sees"
            Expect.stringContains m "0o644 for octal" "teaches the 1.2 octal spelling"
            Expect.stringContains m "quote it" "and the string spelling"

            Expect.stringContains
                (msgOf (lit [ "n: 089" ] + " |> from yaml {| n: int |}"))
                "read it as a string"
                "an 8/9 digit is a string to 1.1 readers"

            Expect.equal
                (run (
                    "show ("
                    + lit [ "z: 0"; "q: \"0644\"" ]
                    + " |> from yaml {| z: int; q: string |})"
                ))
                (VStr "{ q = \"0644\"; z = 0 }")
                "zero and the quoted spelling read as before"
        }
        test
            "yaml: a 0x/0o scalar's int64 check stops at the first digit past range — linear in the digit run [D:yaml-number-text]" {
            let f = Weir.Eval.yamlCoreRadixInt
            Expect.equal (f "0x7fffffffffffffff") (Some(Some System.Int64.MaxValue)) "int64 max fits"
            Expect.equal (f "0x8000000000000000") (Some None) "one past is out of range"
            Expect.equal (f "0o777777777777777777777") (Some(Some System.Int64.MaxValue)) "octal max fits"
            Expect.equal (f "0o1000000000000000000000") (Some None) "octal one past"
            Expect.equal (f ("0x" + String.replicate 5000 "0" + "1f")) (Some(Some 31L)) "leading zeros never overflow"
            Expect.equal (f "0x") None "no digits is no number"
            let sw = System.Diagnostics.Stopwatch.StartNew()
            Expect.equal (f ("0x" + String.replicate 400000 "f")) (Some None) "a 400k-digit run is out of range"
            Expect.isLessThan sw.ElapsedMilliseconds 2000L "and costs linear time"
        }
        test
            "yaml: 1.2 core non-decimal numbers read as numbers and keep their text; 1.1-only forms stay strings [D:yaml-number-text]" {
            Expect.equal
                (run (
                    lit [ "mask: 0x1F"; "num: 0o17"; "max: .inf"; "low: -.Inf"; "nn: .nan" ]
                    + " |> Yaml.parse |> to yaml"
                 )
                 |> text)
                "mask: 0x1F\nnum: 0o17\nmax: .inf\nlow: -.Inf\nnn: .nan"
                "unchanged and unquoted"

            Expect.equal
                (run ("show (" + lit [ "mask: 0x1F"; "num: 0o17"; "max: .inf" ] + " |> Yaml.parse)"))
                (VStr "YMap ([(\"mask\", YInt 31); (\"num\", YInt 15); (\"max\", YStr \".inf\")])")
                "ints are YInt; a non-finite float has no weir value, its text rides YStr"

            Expect.equal
                (run (
                    "show ("
                    + lit [ "mask: 0x1F"; "num: 0o17" ]
                    + " |> from yaml {| mask: int; num: int |})"
                ))
                (VStr "{ mask = 31; num = 15 }")
                "a typed int reads the core forms"

            Expect.equal
                (run (lit [ "big: 1_000"; "bits: 0b101"; "time: 1:20" ] + " |> Yaml.parse |> to yaml")
                 |> text)
                "big: \"1_000\"\nbits: \"0b101\"\ntime: \"1:20\""
                "1.1-only forms are 1.2 strings, quoted for 1.1 readers"

            Expect.equal
                (run "YMap [(\"s\", YStr \".inf\")] |> to yaml |> Str.join \"\"")
                (VStr "s: \".inf\"")
                "a YStr built in weir stays a quoted string"
        }
        test "to yaml: an exponent float carries a dot and a signed exponent [D:yaml-interop]" {
            Expect.equal
                (run "{| a = 0.00001; b = 1e21; c = 1.5e300; d = 123.0 |} |> to yaml" |> text)
                "a: 1.0e-05\nb: 1.0e+21\nc: 1.5e+300\nd: 123.0"
                "YAML 1.1 floats"

            Expect.equal (run "{| v = YFloat 1e-7 |} |> to yaml |> Str.join \"\"") (VStr "v: 1.0e-07") "a Yaml node too"

            Expect.equal
                (run "show ({| a = 0.00001; b = 1e21 |} |> to yaml |> from yaml {| a: float; b: float |})")
                (VStr "{ a = 1e-05; b = 1e+21 }")
                "weir reads them back"
        }
        test "yaml: a tab where indentation is read refuses; a tab before # starts a comment [D:yaml-ws]" {
            let tabbed = lit [ "name: web"; "spec:"; "\treplicas: 3" ]
            Expect.stringContains (msgOf (tabbed + " |> Yaml.parse")) "line 3: a tab indents this line" "Yaml.parse"

            Expect.stringContains
                (msgOf (tabbed + " |> from yaml {| name: string; spec: Option<{| replicas: int |}> |}"))
                "YAML indents with spaces only"
                "from yaml"

            Expect.stringContains
                (msgOf (lit [ "a:"; "  \tb: 1" ] + " |> Yaml.parse"))
                "a tab indents"
                "spaces then a tab"

            Expect.equal
                (run (
                    "show ("
                    + lit [ "name: web\t# the app"; "port: 8080\t# http"; "tag: x\ty" ]
                    + " |> from yaml {| name: string; port: int; tag: string |})"
                ))
                (VStr "{ name = \"web\"; port = 8080; tag = \"x\\ty\" }")
                "comments cut; a tab inside a value is content"

            Expect.equal
                (run (
                    "show ("
                    + lit [ "a: |"; "  \tindented"; "  x" ]
                    + " |> from yaml {| a: string |})"
                ))
                (VStr "{ a = \"\\tindented\\nx\\n\" }")
                "a tab inside block content is content"
        }
        test "yaml: a plain scalar or key trims only space and tab [D:yaml-ws]" {
            Expect.equal
                (run (
                    lit [ "s: x\u00a0"; "t: \u3000y"; "\u3000k: v"; "u:   z  " ]
                    + " |> Yaml.parse |> to json"
                 )
                 |> text)
                "{\"s\":\"x\u00a0\",\"t\":\"\u3000y\",\"\u3000k\":\"v\",\"u\":\"z\"}"
                "NBSP and U+3000 are content"
        }
        test
            "yaml: Unicode white space survives keys, quoted continuations and block lines; weir reads its own output [D:yaml-ws]" {
            Expect.equal
                (run (lit [ "- \u00a0x: 1"; "- 'x\u00a0"; "  y'" ] + " |> Yaml.parse |> to json")
                 |> text)
                "[{\"\u00a0x\":1},\"x\u00a0 y\"]"
                "a leading NBSP key, a trailing NBSP before a fold"

            for v in [ "\"x\\n\u00a0\""; "\"x\\n\u3000\""; "\"\u2003\\ny\"" ] do
                Expect.equal
                    (run $"{{| a = {v} |}} |> to yaml |> from yaml {{| a: string |}} |> fun b -> b.a == {v}")
                    (VBool true)
                    $"reads back: {v}"
        }
        test "yaml: `- - x` nests a sequence on the dash line, any depth, as PyYAML reads it [D:yaml-inline-nested-seq]" {
            let json lines =
                run (lit lines + " |> Yaml.parse |> to json") |> text

            Expect.equal (json [ "- - 1" ]) "[[1]]" "one level"

            Expect.equal
                (json [ "- - - a"; "    - b"; "  - c"; "- d" ])
                "[[[\"a\",\"b\"],\"c\"],\"d\"]"
                "three levels, siblings at each column"

            Expect.equal (json [ "- - k: v"; "    j: w" ]) "[[{\"k\":\"v\",\"j\":\"w\"}]]" "a compact map inside"
            Expect.equal (json [ "- -"; "- -   x" ]) "[[null],[\"x\"]]" "an empty inner item; the dash gap"
            Expect.equal (json [ "m:"; "- - a" ]) "{\"m\":[[\"a\"]]}" "under a flush-left key"

            // depth x line length is bounded: each level keeps its own copy of the line
            let ladder depth tail =
                String.replicate depth "- " + String.replicate tail "a"

            match Weir.Yaml.parseDocs [ 1, ladder 300 400000 ] with
            | Error msg ->
                Expect.equal
                    msg
                    "line 1: yaml nesting is too deep for a line this long — split the item over several lines"
                    "a deep ladder on a long line refuses"
            | Ok _ -> failtest "a 300-deep ladder on a 400k line must refuse, not allocate depth x length"

            Expect.isOk (Weir.Yaml.parseDocs [ 1, ladder 300 1000 ]) "a deep ladder on a short line still reads"
        }
        test
            "yaml: quotes — `''` inside a single-quoted key or item, an escaped quote ending a folded line [D:quoted-fold]" {
            let json lines =
                run (lit lines + " |> Yaml.parse |> to json") |> text

            Expect.equal (json [ "- 'it''s: here'" ]) "[\"it's: here\"]" "an item, not a mapping"
            Expect.equal (json [ "'a''b: c': d" ]) "{\"a'b: c\":\"d\"}" "a quoted key with '' and ': '"
            Expect.equal (json [ "r: 'a''"; "  b'" ]) "{\"r\":\"a' b\"}" "'' ends the first line"

            Expect.equal
                (json [ "r: \"a\\\""; "  b\"" ])
                "{\"r\":\"a\\\" b\"}"
                "an escaped double quote ends the first line"

            Expect.equal
                (msgOf (lit [ "r: 'a''" ] + " |> Yaml.parse"))
                "Yaml.parse: line 1: unclosed single-quoted scalar — the closing ' must appear before the block dedents to the key's column"
                "the unclosed one-liner refuses"

            Expect.equal
                (msgOf (lit [ "r: 'a'b'" ] + " |> Yaml.parse"))
                "Yaml.parse: line 1: content after the closing ' — the quoted value ends at its closing quote"
                "text after the closing quote refuses"
        }
        test "to yaml quotes a leading `...` and U+FEFF so they read back [D:yaml-v1]" {
            Expect.equal (run "\"...\" |> to yaml" |> text) "\"...\"" "a document end otherwise"
            Expect.equal (run "\"\\u{FEFF}a\" |> to yaml" |> text) "\"\\uFEFFa\"" "a byte-order mark otherwise"

            for v in [ "\"...\""; "\"... x\""; "\"\\u{FEFF}a\"" ] do
                Expect.equal
                    (run $"{v} |> to yaml |> Yaml.parse |> fun y -> match y with | YStr t -> t == {v} | _ -> false")
                    (VBool true)
                    $"round-trips: {v}"

            Expect.equal
                (run "[\"a\"; \"...\"; \"b\"] |> to yaml stream |> Str.join \"|\"")
                (VStr "a|---|\"...\"|---|b")
                "no stream element lost"
        }
        test
            "yaml: a block-scalar line starting with % is content; a column-0 directive still refuses [D:block-scalars]" {
            Expect.equal
                (run (
                    "show ("
                    + lit [ "data:"; "  fmt: |"; "    LogFormat"; "    %h %l %u %t" ]
                    + " |> from yaml {| data: {| fmt: string |} |})"
                ))
                (VStr "{ data = { fmt = \"LogFormat\\n%h %l %u %t\\n\" } }")
                "an Apache LogFormat ConfigMap"

            Expect.equal
                (run "show ({| s = \"a\\n%b\" |} |> to yaml |> from yaml {| s: string |})")
                (VStr "{ s = \"a\\n%b\" }")
                "weir reads its own output"

            Expect.stringContains
                (msgOf (lit [ "%YAML 1.2"; "---"; "a: 1" ] + " |> Yaml.parse"))
                "directives are outside the yaml subset"
                "a directive"
        }
        test "to yaml: U+2028/U+2029 never go out raw inside a block scalar [D:block-scalars]" {
            let ls = "(Str.fromUtf8 (Bytes.fromHex \"e280a8\"))"
            let ps = "(Str.fromUtf8 (Bytes.fromHex \"e280a9\"))"

            for v in [ $"(\"x\\ny\" + {ls})"; $"({ls} + \"-\\tz\\n\")"; $"(\"\\n\" + {ps})" ] do
                let out = run $"{{| s = {v} |}} |> to yaml" |> text
                Expect.isFalse (out.Contains "\u2028" || out.Contains "\u2029") $"escaped: {out}"

                Expect.equal
                    (run $"{{| s = {v} |}} |> to yaml |> from yaml {{| s: string |}} |> fun b -> b.s == {v}")
                    (VBool true)
                    $"reads back equal: {out}"

            Expect.equal
                (run "{| s = \"x\\ny\" |} |> to yaml" |> text)
                "s: |-\n  x\n  y"
                "a plain multi-line string keeps the block form"
        }
        test "from xml keeps whitespace-only text, as xml.etree reads it [D:xml-whitespace]" {
            let doc =
                lit [
                    "<Project><PropertyGroup>"
                    "<Sep>&#32;</Sep><Tab>&#9;</Tab><Two>  </Two><Word> a </Word><Empty></Empty>"
                    "</PropertyGroup></Project>"
                ]

            let xenv2 = xenv |> declare "type XP = { PropertyGroup: XW }"

            Expect.equal
                (runUnder xenv2 $"show ({doc} |> from xml XP).PropertyGroup")
                (VStr "{ Sep = \" \"; Tab = \"\\t\"; Two = \"  \"; Word = \" a \"; Empty = \"\" }")
                "text"

            Expect.equal
                (runUnder xenv2 $"show ({doc} |> Str.join \"\" |> Str.toUtf8 |> from xml XP).PropertyGroup")
                (VStr "{ Sep = \" \"; Tab = \"\\t\"; Two = \"  \"; Word = \" a \"; Empty = \"\" }")
                "bytes"
        }
        test
            "from xml: pure ASCII reads under any declared label; utf8/Latin-1 aliases; non-ASCII under an unknown label refuses (R4) [D:xml-labels]" {
            let read (label: string) (title: byte[]) =
                runUnder tenv $"show ({xb (xdoc label title)} |> from xml XT)"

            Expect.equal
                (read "Windows-1252" "e"B)
                (VStr "{ Title = \"cafe\" }")
                "ASCII under windows-1252 (a VS2008 .vcproj)"

            Expect.equal (read "x-klingon" "e"B) (VStr "{ Title = \"cafe\" }") "ASCII under any label"
            Expect.equal (read "UTF8" [| 0xC3uy; 0xA9uy |]) (VStr "{ Title = \"café\" }") "UTF8"
            Expect.equal (read "utf8" [| 0xC3uy; 0xA9uy |]) (VStr "{ Title = \"café\" }") "utf8"
            Expect.equal (read "Latin-1" [| 0xE9uy |]) (VStr "{ Title = \"café\" }") "Latin-1"

            Expect.equal
                (runUnder
                    tenv
                    "show ([\"<?xml version='1.0' encoding='Windows-1252'?><Root><Title>cafe</Title></Root>\"] |> from xml XT)")
                (VStr "{ Title = \"cafe\" }")
                "ASCII text under windows-1252"

            let refuse (label: string) (title: byte[]) =
                (Expect.throwsC (fun () -> runUnder tenv $"{xb (xdoc label title)} |> from xml XT" |> ignore) id)
                    .Message

            let m = refuse "windows-1252" [| 0xE9uy |]

            Expect.stringContains
                m
                "declares encoding 'windows-1252', which weir cannot decode"
                "a real code-page byte refuses"

            Expect.stringContains m "change the declaration to encoding=\"UTF-8\"" "the hint names the declaration too"

            Expect.stringContains
                ((Expect.throwsC
                    (fun () ->
                        runUnder
                            tenv
                            "[\"<?xml version='1.0' encoding='windows-1252'?><Root><Title>café</Title></Root>\"] |> from xml XT"
                        |> ignore)
                    id)
                    .Message)
                "which weir cannot decode"
                "non-ASCII text under an unknown label"

            for label in [ " windows-1252"; "x id"; "ebcdic;" ] do
                Expect.stringContains
                    (refuse label [| 0xC3uy; 0xA9uy |])
                    "encoding name"
                    $"a malformed label '{label}' refuses, never reads as UTF-8 [E13]"
        }
        test "try and try result keep a Yaml node's bigint digits [D:yaml-node-bigint]" {
            let src = "[\"{\\\"id\\\": 18446744073709551616}\"] |> from json {| id: Yaml |}"

            Expect.equal
                (run
                    $"{src} |> try result |> Result.map (fun w -> w |> to json |> Str.join \"\") |> Result.defaultValue \"\"")
                (VStr "{\"id\":18446744073709551616}")
                "try result"

            Expect.equal
                (run $"{src} |> try |> Option.map (fun w -> w |> to json |> Str.join \"\") |> Option.defaultValue \"\"")
                (VStr "{\"id\":18446744073709551616}")
                "try"
        }
        test "an int past int64 into an int field points at Yaml, never float [D:yaml-node-bigint]" {
            let m =
                msgOf "([\"{\\\"id\\\": 18446744073709551615}\"] |> from json {| id: int |}).id"

            Expect.stringContains m "declare it Yaml to keep its digits" "json"
            Expect.isFalse (m.Contains "declare it float") "float would round it"

            Expect.stringContains
                (msgOf (lit [ "id: 18446744073709551615" ] + " |> from yaml {| id: int |}"))
                "declare the field Yaml to keep its digits"
                "yaml"
        }
        test "gen types: format uint64 drafts the digit-keeping Yaml node [D:schema-uint64]" {
            let g =
                gen
                    """{ "type": "object", "required": ["n", "k"],
                           "properties": { "n": { "type": "integer", "format": "uint64" }, "k": { "type": "integer" },
                                           "m": { "type": ["integer", "null"], "format": "uint64" } } }"""

            Expect.stringContains g "\n    n: Yaml\n" "uint64"
            Expect.stringContains g "\n    k: int\n" "a plain integer stays int"
            Expect.stringContains g "\n    m: Option<Yaml>\n" "nullable uint64"
            Expect.stringContains g "n: format uint64 — drafted Yaml" "the note"
        }
        test
            "gen types: a required + nullable field is noted — None writes the key omitted [D:schema-required-nullable]" {
            let g =
                gen
                    """{ "type": "object", "required": ["name", "parent", "note", "legacy"],
                           "properties": { "name": { "type": "string" }, "parent": { "type": ["string", "null"] },
                                           "note": { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                                           "legacy": { "type": "string", "nullable": true },
                                           "opt": { "type": ["string", "null"] } } }"""

            for f in [ "parent"; "note"; "legacy" ] do
                Expect.stringContains g $"// note: {f}: required and nullable — drafted Option<string>" f

            Expect.isFalse (g.Contains "opt: required") "an optional nullable field is plain Option"
            Expect.isFalse (g.Contains "name: required") "a required non-null field is plain"
        }
        test
            "gen types: the enum note lists null only when the enum holds it and the type allows it [D:schema-enum-null]" {
            let g =
                gen
                    """{ "type": "object",
                           "properties": { "kind": { "type": ["string", "null"], "enum": ["Pod"] },
                                           "lvl": { "type": "string", "enum": ["a", "b"] },
                                           "hn": { "type": "string", "enum": ["x", null] },
                                           "both": { "type": ["string", "null"], "enum": ["y", null] } } }"""

            Expect.stringContains g "kind: an enum of 'Pod' — drafted" "type-only null"
            Expect.stringContains g "lvl: an enum of 'a', 'b' — drafted" "no null"
            Expect.stringContains g "hn: an enum of 'x' — drafted" "the type forbids null"
            Expect.stringContains g "both: an enum of 'y', null — drafted" "listed and allowed"
        }
    ]

let hunt3r6FormatTests =
    // hunt-3 round 6, formats: number text for floats, radix bigints and
    // overflow [D:yaml-number-text] [D:yaml-node-bigint], Unicode white
    // space in a value slot [D:yaml-ws], XML label contradiction
    // [D:xml-encoding] and depth [D:xml-depth], draft escaping
    // [D:infer-wire-sanitize], nullable uint64 [D:schema-uint64]
    let text v =
        forceSeq v
        |> List.map (function
            | VStr s -> s
            | v -> failtest $"expected text, got {formatValue v}")
        |> String.concat "\n"

    let lit (lines: string list) =
        "["
        + (lines
           |> List.map (fun l -> "\"" + l.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")
           |> String.concat "; ")
        + "]"

    let gen src =
        match Weir.Contracts.parseSchema "t" src with
        | Error e -> failtest e
        | Ok d ->
            match
                Weir.SchemaTypes.moduleText Weir.Parser.keywords Set.empty "t" None "0123456789abcdef" "http://src" d
            with
            | Ok g -> g.Text
            | Error e -> failtest e

    let str (v: Value) =
        match v with
        | VStr s -> s
        | v -> failtest $"expected a string, got {formatValue v}"

    testList "hunt3r6 formats" [
        test "yaml: a float weir would write differently keeps its text; to json writes the value [D:yaml-number-text]" {
            let doc = [ "sha: 1e5"; "a: 1E5"; "b: 1e+5"; "c: +.5"; "v: 3.10"; "p: 1.5" ]
            Expect.equal (run (lit doc + " |> Yaml.parse |> to yaml") |> text) (String.concat "\n" doc) "as written"

            Expect.equal
                (run (lit doc + " |> Yaml.parse |> to json") |> text)
                "{\"sha\":100000.0,\"a\":100000.0,\"b\":100000.0,\"c\":0.5,\"v\":3.1,\"p\":1.5}"
                "json writes the values"

            Expect.equal
                (run "YMap [(\"f\", YFloat 1e5)] |> to yaml |> Str.join \"\"")
                (VStr "f: 100000.0")
                "a YFloat built in weir writes its float"

            Expect.equal
                (run "[\"{\\\"f\\\": 1e5}\"] |> from json {| f: Yaml |} |> to yaml |> Str.join \"\"")
                (VStr "f: 100000.0")
                "a JSON number keeps no float text — 1e5 is a string to a 1.1 reader"
        }
        test "yaml: a 1.2 number past float range keeps its text plain [D:yaml-number-text]" {
            Expect.equal
                (run (
                    lit [ "big: 1e400"; "huge: 1.5e309"; "s: \"1e400\"" ]
                    + " |> Yaml.parse |> to yaml"
                 )
                 |> text)
                "big: 1e400\nhuge: 1.5e309\ns: \"1e400\""
                "plain stays plain, quoted stays quoted"
        }
        test "yaml: a leading-zero or radix integer past int64 writes decimal digits to json [D:yaml-node-bigint]" {
            let doc = [
                "k: 012345678901234567890"
                "h: 0x8000000000000000"
                "o: 0o1000000000000000000000"
                "d: 9223372036854775808"
            ]

            Expect.equal
                (run (lit doc + " |> Yaml.parse |> to json") |> text)
                "{\"k\":12345678901234567890,\"h\":9223372036854775808,\"o\":9223372036854775808,\"d\":9223372036854775808}"
                "digits, never .NET text"

            Expect.equal
                (run (lit doc + " |> Yaml.parse |> to yaml") |> text)
                (String.concat "\n" doc)
                "yaml keeps the text"

            Expect.equal
                (run ("show (" + lit [ "h: 0x8000000000000000" ] + " |> Yaml.parse)"))
                (VStr "YMap ([(\"h\", YFloat (9.223372036854776e+18))])")
                "a radix int past int64 reads as the decimal does"
        }
        test "yaml: a value of only NBSP / U+3000 is that string, not null [D:yaml-ws]" {
            Expect.equal
                (run (
                    "show ("
                    + lit [ "a: \u00a0"; "b: \u3000"; "c: \u00a0 # a comment" ]
                    + " |> from yaml {| a: string; b: string; c: string |})"
                ))
                (VStr "{ a = \"\u00a0\"; b = \"\u3000\"; c = \"\u00a0\" }")
                "mapping values"

            Expect.equal
                (run ("show (" + lit [ "s:"; "- \u00a0" ] + " |> from yaml {| s: seq<string> |})"))
                (VStr "{ s = [\"\u00a0\"] }")
                "a sequence item"

            Expect.equal
                (run ("show (" + lit [ "a:   " ] + " |> from yaml {| a: Option<string> |})"))
                (VStr "{ a = None }")
                "spaces alone are still null"
        }
        test "yaml: a float field refuses a leading-zero literal, as int does (R3) [D:yaml-number-text]" {
            for ty in [ "float"; "Option<float>" ] do
                let m = msgOf (lit [ "mode: 0644" ] + $" |> from yaml {{| mode: {ty} |}}")
                Expect.stringContains m "'0644' has a leading zero" ty
                Expect.stringContains m "octal (420)" ty
                Expect.isFalse (m.Contains "0o644") $"{ty}: a float field cannot read 0o"

            Expect.equal
                (run (
                    "show ("
                    + lit [ "a: 644"; "b: 0.5"; "c: 0"; "d: 0644.5" ]
                    + " |> from yaml {| a: float; b: float; c: float; d: float |})"
                ))
                (VStr "{ a = 644.0; b = 0.5; c = 0.0; d = 644.5 }")
                "decimal forms read"

            Expect.stringContains
                (msgOf (lit [ "n: -0644" ] + " |> from yaml {| n: int |}"))
                "octal (-420)"
                "the sign rides the 1.1 reading"
        }
        test "from xml: a UTF-16/32 label on 8-bit bytes says the declaration contradicts them [D:xml-encoding]" {
            let xenv = env |> declare "type XT = { a: string }"

            let bytes =
                System.Text.Encoding.UTF8.GetBytes "<?xml version=\"1.0\" encoding=\"utf-16\"?><XT><a>café</a></XT>"

            let m =
                (Expect.throwsC
                    (fun () ->
                        runUnder xenv $"Bytes.fromBase64 \"{System.Convert.ToBase64String bytes}\" |> from xml XT"
                        |> ignore)
                    id)
                    .Message

            Expect.stringContains m "declares encoding 'utf-16'" "names the label"
            Expect.stringContains m "contradicts" "says the bytes disagree"
            Expect.isFalse (m.Contains "not valid XML") "never the mojibake parse"

            let le = System.Text.Encoding.Unicode

            let u16 =
                Array.append
                    [| 0xFFuy; 0xFEuy |]
                    (le.GetBytes "<?xml version=\"1.0\" encoding=\"utf-16\"?><XT><a>café</a></XT>")

            Expect.equal
                (runUnder xenv $"show (Bytes.fromBase64 \"{System.Convert.ToBase64String u16}\" |> from xml XT)")
                (VStr "{ a = \"café\" }")
                "real UTF-16 still reads"
        }
        test "from xml: element nesting past 500 refuses; 500 reads [D:xml-depth]" {
            let xenv = env |> declare "type XR = { x: Option<string> }"

            let doc n =
                let o, c = String.replicate (n - 1) "<a>", String.replicate (n - 1) "</a>"
                $"[\"<r>{o}{c}</r>\"]"

            Expect.equal (runUnder xenv $"show ({doc 500} |> from xml XR)") (VStr "{ x = None }") "at the bound"

            let sw = System.Diagnostics.Stopwatch.StartNew()

            let m =
                (Expect.throwsC (fun () -> runUnder xenv $"{doc 30000} |> from xml XR" |> ignore) id).Message

            Expect.stringContains m "deeper than 500 levels" "past it"
            Expect.isLessThan sw.ElapsedMilliseconds 5000L "and stops early"
        }
        test
            "inferShape drafts what the typed read takes back: 0644/.inf/radix past int64 Yaml, 0x1F/0o17 int, +.5 float [D:yaml-number-text]" {
            let d =
                run
                    "Yaml.inferShape [\"mode: 0644\"; \"mask: 0x1F\"; \"num: 0o17\"; \"max: .inf\"; \"h: 0x8000000000000000\"; \"f: +.5\"; \"big: 1e400\"; \"name: web\"]"
                |> str

            for f, ty in
                [
                    "mode", "Yaml"
                    "mask", "int"
                    "num", "int"
                    "max", "Yaml"
                    "h", "Yaml"
                    "f", "float"
                    "big", "Yaml"
                    "name", "string"
                ] do
                Expect.stringContains d $"\n    {f}: {ty}\n" f

            Expect.stringContains d "'mode': 0644 has a leading zero" "the note says why"

            Expect.equal
                (run "[\"mode: 0644\"] |> from yaml {| mode: Yaml |} |> to yaml |> Str.join \"\"")
                (VStr "mode: 0644")
                "the drafted Yaml field reads its sample and keeps the text"
        }
        test "gen types escapes wire keys, drops a key a literal cannot spell, quotes notes [D:infer-wire-sanitize]" {
            let g =
                gen
                    "{ \"type\": \"object\", \"additionalProperties\": false, \"properties\": { \"p\\\"q\": { \"type\": \"string\" }, \"r\\\\s\": { \"type\": \"string\" }, \"a\\u001b]52;c;x\\u0007b\": { \"type\": \"string\" }, \"x\": { \"enum\": [\"a\", \"b\\r\"] } } }"

            Expect.stringContains g "[<Wire \"p\\\"q\">]" "a quote escapes"
            Expect.stringContains g "[<Wire \"r\\\\s\">]" "a backslash escapes"
            Expect.isFalse (g |> Seq.exists (fun c -> c = '\u001b' || c = '\u0007' || c = '\r')) "no raw ESC/BEL/CR"
            Expect.stringContains g "is DROPPED from the draft" "the ESC key is dropped, noted"
            Expect.stringContains g "\\x0d" "the CR in the enum note shows visibly"

            // the drafted decl on one line (the test parser's form)
            let body =
                g.Substring(g.IndexOf "type T = {" + 10).Split('\n')
                |> Array.map (fun l -> l.Trim())
                |> Array.filter (fun l -> l <> "" && l <> "}")
                |> Array.map (fun l -> if l.StartsWith "[<" then l + " " else l + "; ")
                |> String.concat ""

            let tenv = env |> declare ("type T = { " + body.TrimEnd(' ', ';') + " }")

            Expect.equal
                (runUnder
                    tenv
                    "[\"{\\\"p\\\\\\\"q\\\": \\\"v\\\", \\\"r\\\\\\\\s\\\": \\\"w\\\"}\"] |> from json T |> to json |> Str.join \"\"")
                (VStr "{\"p\\\"q\":\"v\",\"r\\\\s\":\"w\"}")
                "the escaped keys read and write back"
        }
        test
            "yaml: a key the document wrote plain writes back plain; quoted or weir-built keys keep the law [D:yaml-key-spelling]" {
            let doc = [
                "responses:"
                "  200: ok"
                "  \"404\": missing"
                "flags:"
                "  true: x"
                "  null: none"
                "modes:"
                "  0644: rw"
                "list:"
                "  - 8080: http"
            ]

            Expect.equal
                (run (lit doc + " |> Yaml.parse |> to yaml") |> text)
                (String.concat "\n" doc)
                "untouched round trip"

            Expect.equal
                (run "YMap [(\"200\", YStr \"a\"); (\"true\", YStr \"b\")] |> to yaml" |> text)
                "\"200\": a\n\"true\": b"
                "keys built in weir are quoted"

            Expect.equal
                (run (lit [ "200: a" ] + " |> Yaml.parse |> to json") |> text)
                "{\"200\":\"a\"}"
                "the key is still the string 200"
        }
        test "gen types: format uint64 under a nullable anyOf/oneOf drafts Option<Yaml> [D:schema-uint64]" {
            let g =
                gen
                    """{ "type": "object",
                           "properties": { "a": { "anyOf": [{ "type": "integer", "format": "uint64" }, { "type": "null" }] },
                                           "o": { "oneOf": [{ "type": "null" }, { "type": "integer", "format": "uint64" }] } } }"""

            Expect.stringContains g "\n    a: Option<Yaml>\n" "anyOf"
            Expect.stringContains g "\n    o: Option<Yaml>\n" "oneOf"
        }
    ]

let hunt3r7SafetyTests =
    // hunt-3 round 7, safety: userinfo masked through the last @
    // [D:url-redact], a "" key in gen types and a bidi wire key
    // [D:infer-wire-escape], the .weir/ writer lock and whole-file
    // replace [D:weir-dir-lock], schema messages escape their data
    // [D:error-data-escape]
    let gen src =
        match Weir.Contracts.parseSchema "t" src with
        | Error e -> failtest e
        | Ok d ->
            match
                Weir.SchemaTypes.moduleText Weir.Parser.keywords Set.empty "t" None "0123456789abcdef" "http://src" d
            with
            | Ok g -> g.Text
            | Error e -> failtest e

    testList "hunt3r7 safety" [
        test "redactUrl masks through the last @ — a raw / ? # @ in the password never shows [D:url-redact]" {
            for u, shown in
                [
                    "http://deploy:Tr0ub/4dor@127.0.0.1:1/x", "http://***@127.0.0.1:1/x"
                    "http://deploy:pa#ss@127.0.0.1:1/x", "http://***@127.0.0.1:1/x"
                    "http://deploy:p@ssw0rd@127.0.0.1:1/x", "http://***@127.0.0.1:1/x"
                    "http://deploy:q?wd@h.example/x?y=1", "http://***@h.example/x?y=1"
                    "https://tok/en+==@github.com/o/r", "https://***@github.com/o/r"
                    "http://u:a/b@c@registry/v2", "http://***@registry/v2"
                    "//u:pw@h:99/x", "//***@h:99/x"
                    "u:p/w@h:3128", "***@h:3128"
                    // `user:<digits>` before the raw delimiter reads as host:port
                    "http://u:2024/pw@intranet/x", "http://***@intranet/x"
                    "http://deploy:2024/01/15@intranet/x", "http://***@intranet/x"
                    "http://deploy:8080?4dor@gitea/x", "http://***@gitea/x"
                    "http://u:2024#pw@gitlab/x", "http://***@gitlab/x"
                ] do
                Expect.equal (Weir.Http.redactUrl u) shown u

            // an @ in a path or query of a credential-free URL stays
            for u in
                [
                    "https://discord.com/api/users/@me"
                    "https://host/path@v2?to=a@b"
                    "http://127.0.0.1:8792/x"
                ] do
                Expect.equal (Weir.Http.redactUrl u) u u

            Expect.equal
                (Weir.Http.redactUrlsIn "proxy 'http://u:S3/cr@t@h:1/' failed; see //a:b@k/x")
                "proxy 'http://***@h:1/' failed; see //***@k/x"
                "free text masks every URL through its last @"

            Expect.equal (Weir.Http.redactProxyValue "u:SEC/RET#PW@h:99999") "***@h:99999" "scheme-less"
            Expect.equal (Weir.Http.redactProxyValue "//u:hunter2@h:99999") "//***@h:99999" "scheme-relative"
            Expect.equal (Weir.Http.redactProxyValue "http://u:a@b/c@h:1") "http://***@h:1" "the runtime's last @"
            Expect.equal (Weir.Http.redactProxyValue "http://h:99999") "http://h:99999" "no userinfo"
        }
        test "stripUserinfo keeps the client's parse for what the lock records [D:lock-no-userinfo]" {
            Expect.equal
                (Weir.Http.stripUserinfo "http://u:p@ss@h/s.json")
                "http://h/s.json"
                "the last @ of the authority"

            Expect.equal (Weir.Http.stripUserinfo "https://h/users/@me") "https://h/users/@me" "a path @ is data"
        }
        test "gen types drops a \"\" property key with a note, as #infer does [D:infer-wire-escape]" {
            let t =
                gen """{"type":"object","properties":{"":{"type":"string"},"ok":{"type":"string"}}}"""

            Expect.stringContains t "an empty-string key under" "noted"
            Expect.stringContains t "ok: Option<string>" "the other field drafted"
            Expect.isFalse (t.Contains "[<Wire \"\">]") "no empty wire key"
        }
        test "a wire key holding a bidi control is dropped with a note, never spelled raw [D:infer-wire-escape]" {
            let t =
                gen
                    "{\"type\":\"object\",\"properties\":{\"a\\u202eb\":{\"type\":\"string\"},\"ok\":{\"type\":\"string\"}}}"

            Expect.isFalse (t.Contains "\u202e") $"no raw U+202E: {t}"
            Expect.stringContains t "a\\u202Eb" "the note names it visibly"
            Expect.stringContains t "ok: Option<string>" "the other field drafted"
        }
        test "a remote schema's unresolvable $ref is quoted visibly [D:error-data-escape]" {
            match
                Weir.Contracts.parseSchema
                    "t"
                    "{\"type\":\"object\",\"properties\":{\"e\":{\"$ref\":\"#/x\\u001b]52;c;QQ==\\u0007\"}}}"
            with
            | Ok _ -> failtest "an unresolvable $ref refuses"
            | Error e ->
                Expect.isFalse (e.Contains "\u001b" || e.Contains "\u0007") $"nothing raw: {e}"
                Expect.stringContains e "#/x\\x1b]52;c;QQ==\\x07" "rendered visibly"
        }
        test "lock.json is replaced whole, keeping its mode [D:weir-dir-lock]" {
            let dir =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-atomic-{System.Guid.NewGuid():N}")

            System.IO.Directory.CreateDirectory dir |> ignore

            try
                let p = System.IO.Path.Combine(dir, "lock.json")
                System.IO.File.WriteAllText(p, "old")

                if not (System.OperatingSystem.IsWindows()) then
                    System.IO.File.SetUnixFileMode(p, enum<System.IO.UnixFileMode> 0o600)

                Weir.Contracts.writeLock dir []
                Expect.stringContains (System.IO.File.ReadAllText p) "\"artifacts\"" "written"

                if not (System.OperatingSystem.IsWindows()) then
                    Expect.equal (int (System.IO.File.GetUnixFileMode p)) 0o600 "mode kept"

                Expect.equal (System.IO.Directory.GetFiles dir |> Array.length) 1 "no temp left"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "the .weir/ writer lock serializes two holders [D:weir-dir-lock]" {
            let root =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-dirlock-{System.Guid.NewGuid():N}")

            let weirDir = System.IO.Path.Combine(root, ".weir")
            System.IO.Directory.CreateDirectory root |> ignore

            try
                let inside = [| 0 |]
                let overlap = ref false

                let hold () =
                    Weir.Contracts.AtomicFile.withWeirDirLock weirDir (fun () ->
                        if System.Threading.Interlocked.Increment(&inside[0]) > 1 then
                            overlap.Value <- true

                        System.Threading.Thread.Sleep 200
                        System.Threading.Interlocked.Decrement(&inside[0]) |> ignore
                        Ok())

                let ts = [ for _ in 1..3 -> System.Threading.Tasks.Task.Run(fun () -> hold ()) ]

                System.Threading.Tasks.Task.WaitAll(
                    ts |> List.map (fun t -> t :> System.Threading.Tasks.Task) |> Array.ofList
                )

                for t in ts do
                    Expect.equal t.Result (Ok()) "every holder ran"

                Expect.isFalse overlap.Value "never two holders at once"
                Expect.isFalse (System.IO.Directory.Exists weirDir) "the lock creates no .weir/"
            finally
                System.IO.Directory.Delete(root, true)
        }
    ]

let receiptTests =
    let completedLiteral =
        "a Completed is what '| complete' returns — run a command to get one (sh -c \"exit 3\" | complete); a function over its fields takes any record with them, so a test can pass {| exitCode = 3 |}"

    let fileRowLiteral =
        "a FileRow is what 'ls', 'Dir.stat' and 'File.stat' return — stat a path to get one (File.stat \"a.txt\"); a function over its fields takes any record with them, so a test can pass {| name = \"a.txt\"; path = \"a.txt\" |}"

    let httpResponseLiteral =
        "an HttpResponse is what 'Http.send' returns — send a request to get one (Http.get url |> Http.send); a function over its fields takes any record with them, so a test can pass {| status = 200 |}"

    let completedUpdate =
        "a Completed is read-only — it reports a run that happened (ok follows exitCode), so no field changes after the fact; to carry different values build a record of your own, {| exitCode = 3 |}"

    let fileRowUpdate =
        "a FileRow is read-only — it reports a path as it was seen, so no field changes after the fact; to carry different values build a record of your own, {| name = \"a.txt\"; path = \"a.txt\" |}"

    let httpResponseUpdate =
        "an HttpResponse is read-only — it reports a response that arrived, so no field changes after the fact; to carry different values build a record of your own, {| status = 200 |}"

    let refuses (lines: string list) (expected: string) (label: string) =
        let e = firstErrorIn "receipt.weir" lines
        Expect.equal e.Message expected label

    testList "receipts" [
        test "ok is exitCode == 0, exactly — the succeeds law as a field" {
            skipOnWindows ()

            let okOf src =
                match runReal src with
                | VRecord("Completed", fields) -> Weir.Eval.recGet "ok" fields, Weir.Eval.recGet "exitCode" fields
                | v -> failtest $"unexpected: {formatValue v}"

            Expect.equal (okOf "sh -c \"exit 0\" | complete") (VBool true, VInt 0L) "exit 0"
            Expect.equal (okOf "sh -c \"exit 3\" | complete") (VBool false, VInt 3L) "exit 3"
            // grep's no-match is a meaningful 1: ok is false, the code reads it
            Expect.equal (okOf "grep nomatch /etc/hosts | complete") (VBool false, VInt 1L) "grep's 1"
        }
        test "ok leads the record: show and to json carry it (the REPL echo is pinned in e2e)" {
            skipOnWindows ()

            match runReal "show $(sh -c \"exit 3\" | complete)" with
            | VStr shown ->
                Expect.stringStarts shown "{ ok = false; exitCode = 3; stdout = []; stderr = []; elapsed = " "show"
            | v -> failtestf "show gives a string: %A" v

            match runReal "sh -c \"exit 3\" | complete |> to json" |> forceSeq with
            | [ VStr json ] ->
                Expect.stringStarts
                    json
                    "{\"ok\":false,\"exitCode\":3,\"stdout\":[],\"stderr\":[],\"elapsed\":"
                    "to json"
            | v -> failtestf "to json gives one line: %A" v
        }
        test "every receipt is a registered prelude record" {
            let strict, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            for KeyValue(name, r) in Weir.Types.receipts do
                match Map.tryFind name strict.Types with
                | Some(Weir.Types.Record def) ->
                    let fields = def.Fields |> List.map fst |> Set.ofList
                    Expect.isTrue (Set.isSubset (Set.ofList r.Derived) fields) $"{name}: derived fields are fields"
                | other -> failtest $"{name}: expected a prelude record, got {other}"
        }
        test "a receipt literal is refused, naming where the value comes from" {
            refuses
                [
                    "let r = { exitCode = 3; stdout = []; stderr = [] }"
                    "print (show r.exitCode)"
                ]
                completedLiteral
                "the bare literal without the derived field"

            refuses
                [
                    "let r = { ok = false; exitCode = 3; stdout = []; stderr = [] }"
                    "print (show r.exitCode)"
                ]
                completedLiteral
                "the bare literal with it"

            refuses
                [
                    "let r = Completed { exitCode = 3; stdout = []; stderr = [] }"
                    "print (show r.exitCode)"
                ]
                completedLiteral
                "the named literal"

            refuses
                [
                    "let r = { name = \"a\"; kind = Regular; target = None; bytes = 0 B; modified = Instant.now (); path = \"a\" }"
                    "print r.name"
                ]
                fileRowLiteral
                "FileRow, bare"

            refuses
                [
                    "let r = FileRow { name = \"a\"; kind = Regular; target = None; bytes = 0 B; modified = Instant.now (); hidden = false; path = \"a\" }"
                    "print r.name"
                ]
                fileRowLiteral
                "FileRow, named"

            refuses
                [
                    "let r = HttpResponse { status = 200; headers = []; body = [] }"
                    "print (show r.status)"
                ]
                httpResponseLiteral
                "HttpResponse, named"

            // the refusal is located on the literal
            let e =
                firstErrorIn "receipt.weir" [
                    "let r = Completed { exitCode = 3; stdout = []; stderr = [] }"
                    "print (show r.exitCode)"
                ]

            Expect.equal (e.Line, e.Col) (1, 9) "caret on the literal"
        }
        test "a same-shaped record the author declares wins; HttpServerResponse's bare literal is no longer ambiguous" {
            cleanIn
                "receipt.weir"
                [
                    "type Run = { exitCode: int; stdout: seq<string>; stderr: seq<string> }"
                    "let r = { exitCode = 3; stdout = []; stderr = [] }"
                    "print (show r.exitCode)"
                ]
                "the user's record"

            cleanIn
                "receipt.weir"
                [
                    "let r = { status = 200; headers = []; body = Text \"x\" }"
                    "print (show r.status)"
                ]
                "the server response, the one a script writes"
        }
        test "a receipt update is refused — the record is read-only" {
            refuses
                [
                    "let r = sh -c \"exit 3\" | complete"
                    "let s = { r with exitCode = 0 }"
                    "print (show s.exitCode)"
                ]
                completedUpdate
                "Completed"

            refuses
                [ "let r = ls |> Seq.head"; "let s = { r with hidden = true }"; "print s.name" ]
                fileRowUpdate
                "FileRow"

            refuses
                [
                    "let r = Http.get \"http://h\" |> Http.send"
                    "let s = { r with status = 1 }"
                    "print (show s.status)"
                ]
                httpResponseUpdate
                "HttpResponse"

            // a nested path reaching a receipt is the same write
            refuses
                [
                    "let r = sh -c \"exit 3\" | complete"
                    "let w = {| inner = r |}"
                    "let s = { w with inner.exitCode = 0 }"
                    "print (show s.inner.exitCode)"
                ]
                completedUpdate
                "nested path"

            let e =
                firstErrorIn "receipt.weir" [
                    "let r = sh -c \"exit 3\" | complete"
                    "let s = { r with exitCode = 0 }"
                    "print (show s.exitCode)"
                ]

            Expect.equal (e.Line, e.Col) (2, 11) "caret on the source"
        }
        test
            "a function over the fields is row-typed: it takes the anonymous record the message names, and a real Completed" {
            skipOnWindows ()

            Expect.equal
                (runReal "let f { exitCode = c } = c in f {| exitCode = 3 |}")
                (VInt 3L)
                "the message's example"

            Expect.equal
                (runReal "let f { exitCode = c } = c in f $(sh -c \"exit 3\" | complete)")
                (VInt 3L)
                "a real receipt"
            // why the example carries no `[]`: an anonymous literal is
            // ground-typed [D:anon-literals]
            let e =
                firstErrorIn "receipt.weir" [
                    "let f { exitCode = c } = c"
                    "print (show (f {| exitCode = 3; stdout = []; stderr = [] |}))"
                ]

            Expect.stringContains
                e.Message
                "not fully known at the literal"
                "the empty-seq spelling is refused at the literal"
        }
        test "a row-typed updater that reaches a receipt only at its call is refused at run time, same words" {
            skipOnWindows ()

            match runReal "let bump r = { r with exitCode = 0 } in bump {| exitCode = 3 |}" with
            | VRecord(_, fields) ->
                Expect.equal (Weir.Eval.recGet "exitCode" fields) (VInt 0L) "an ordinary record updates"
            | v -> failtest $"unexpected: {formatValue v}"

            let raised =
                Expect.throwsC
                    (fun () ->
                        runReal "let bump r = { r with exitCode = 0 } in bump $(sh -c \"exit 3\" | complete)"
                        |> ignore)
                    id

            Expect.equal raised.Message completedUpdate "the receipt refuses"
        }
    ]
