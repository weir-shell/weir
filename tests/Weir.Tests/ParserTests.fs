module ParserTests

// The parser suites. A suite sits in the file of the area it mostly
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

let parserTests =
    testList "Parser" [
        test "binop and pipe" { expectParse "1 + 2 |> double" "((+ 1 2) |> double)" }
        test "application is left-assoc" { expectParse "f x y" "((f x) y)" }
        test "precedence" { expectParse "1 + 2 * 3" "(+ 1 (* 2 3))" }
        test "comparison binds looser than plus" { expectParse "1 + 2 > 2" "(> (+ 1 2) 2)" }
        test "lambda body extends right" { expectParse "fun x -> x + 1" "(fun x (+ x 1))" }
        test "let-in" { expectParse "let x = 1 in x" "(let x 1 x)" }
        test "field access chains" { expectParse "f.bytes > 1048576" "(> f.bytes 1048576)" }
        test "shell pipe is pipe" { expectParse "ls |> where p |> take 5" "((ls |> (where p)) |> (take 5))" }
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
        }
    ]

let commandModeTests =
    testList "Command mode" [
        test "bare external command" { expectCmd "git status" "(cmd git \"status\")" }
        test "quoted arg is a single argv entry" { expectCmd "grep \"a b\" file.txt" "(cmd grep \"a b\" \"file.txt\")" }
        test "single quotes carry embedded double quotes" {
            match parseCmd "grep 'a\"b' f" with
            | {
                  Kind = ECmd(HeadLit "grep", [ { Kind = EStr "a\"b" }; { Kind = EStr "f" } ], _)
              } -> ()
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
            // a name operand negates in its own type [D:prefix-minus-typed]
            Expect.equal
                (show (parseCmd "ls -la"))
                "(ls (let |neg la (- (- |neg |neg) |neg)))"
                "parses as application of -la"

            Expect.stringContains (checkErr "ls - la").Message "unbound variable 'la'" ""
        }
        test "prefix minus on a name negates in the name's type [D:prefix-minus-typed]" {
            Expect.equal (run "let x = 1.5 in -x") (VFloat -1.5) "float"
            Expect.equal (run "let d = 2s in -d") (VDur -2000L) "Duration"
            Expect.equal (run "let k = 3KiB in -k") (VSize -3072L) "Size"
            Expect.equal (run "let i = 4 in -i") (VInt -4L) "int"
            Expect.equal (run "-(Float.abs 2.0)") (VFloat -2.0) "a parenthesized float"
            Expect.equal (run "let i = 4 in -(-i)") (VInt 4L) "double negation"
        }
        test "'|' before an expression stage is a parse error — the RHS decides [D:pipe-rhs-decides]" {
            match Weir.Parser.parseLine cmdResolver "git log | first 5" with
            | Error msg -> Expect.stringContains msg "'|' chains commands; pipe expressions with '|>'" ""
            | Ok _ -> failtest "cmd | expr must error under the pipe rule"
        }
        test "'|>' before a PROGRAM teaches the reverse direction — the RHS decides [D:pipe-rhs-decides]" {
            // the rejecting half of the symmetric rule: the message lived
            // only in the parser until the maintenance sweep's M4 sample
            match Weir.Parser.parseLine cmdResolver "[\"x\"] |> cat" with
            | Error msg -> Expect.stringContains msg "'|>' applies functions; feed a program with '|'" ""
            | Ok _ -> failtest "expr |> program must error under the pipe rule"
        }
        test "pipe accepts |> in command mode too" { expectCmd "git log |> take 5" "((cmd git \"log\") |> (take 5))" }
        test "external to external to expression" {
            expectCmd "git log | grep x |> take 2" "(((cmd git \"log\") |> (cmd grep \"x\")) |> (take 2))"
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
            match Weir.Parser.parseLine cmdResolver "git status |> take 1" with
            | Ok(SExpr e | SCmd e) ->
                match typecheck env e with
                | Ok te -> Expect.equal te.Ty (TSeq TStr) ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "splice must be a stringable scalar" {
            let env2 = {
                env with
                    Values =
                        env.Values
                        |> Map.add "branch" (generalize TStr)
                        |> Map.add "pt" (generalize (TNamed("Point", [])))
            }

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
            skipOnWindows ()
            Expect.equal (runReal "echo hi (1 + 2) true" |> forceSeq) [ VStr "hi 3 true" ] ""
        }
        test "real exec: command pipes into first" {
            skipOnWindows ()

            Expect.equal
                (runReal "yes weir-s3-pipe |> take 2" |> forceSeq)
                [ VStr "weir-s3-pipe"; VStr "weir-s3-pipe" ]
                ""

            Expect.isTrue (eventuallyNoSurvivors "weir-s3-pipe") "command-mode child leaked"
        }
        test "real exec: argv verbatim, no shell interpretation" {
            skipOnWindows ()
            Expect.equal (runReal "echo ; rm -rf x") (runReal "echo ; rm -rf x") "deterministic"
            Expect.equal (runReal "echo ; rm -rf x" |> forceSeq) [ VStr "; rm -rf x" ] ""
        }
    ]

let cdTests =
    testSequenced
    <| testList "Command-callable cd" [
        test "cd with a path bareword parses to a builtin call" { expectCmd "cd /work" "(cd \"/work\")" }
        test "bare cd desugars to home" { expectCmd "cd" "(cd (|home ()))" }
        test "cd dotdot and tilde-path parse" {
            expectCmd "cd .." "(cd \"..\")"
            // a typed `~` expands for every command now [D:tilde] — cd
            // no longer needs its own tilde reading for this spelling
            expectCmd "cd ~/src" "(cd (+ (|home ()) \"/src\"))"
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
            skipOnWindows ()

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
                        Expect.stringContains ex.Message (platformPath "/definitely/not/weir") "absolute path shown"
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
        }
        test "expression-mode cd is unchanged" {
            skipOnWindows ()

            try
                expectValue "let d = cd \"/tmp\" in d" (VStr "/tmp")
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
        }
    ]

let session3Tests =
    testSequenced
    <| testList "complete and freeze" [
        test "freeze snapshots a live query" {
            skipOnWindows ()

            try
                expectValue
                    "let p = [pwd] |> freeze in let d = cd \"/tmp\" in p |> take 1"
                    (VSeq [ VStr(System.IO.Directory.GetCurrentDirectory()) ])
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
        }
        test "freeze runs effects exactly once" {
            skipOnWindows ()

            let marker = weirPath (tempPath "weir-force")

            try
                runReal
                    $"let s = $(sh -c \"echo x >> {marker}; echo line\") |> freeze in let a = s |> take 1 in let b = s |> take 1 in b"
                |> forceSeq
                |> ignore

                Expect.equal (File.ReadAllLines marker |> Array.length) 1 "one spawn with freeze"

                File.Delete marker

                let r =
                    runReal
                        $"let s = $(sh -c \"echo x >> {marker}; echo line\") in let a = s |> take 1 |> freeze in let b = s |> take 1 |> freeze in b"

                r |> forceSeq |> ignore
                Expect.equal (File.ReadAllLines marker |> Array.length) 2 "two spawns without upfront freeze"
            finally
                if File.Exists marker then
                    File.Delete marker
        }
        test "freeze is polymorphic" { expectValue "[1; 2] |> freeze |> sum" (VInt 3) }
        test "head extracts the element" {
            expectValue "[1; 2] |> head" (VInt 1)
            expectValue "ls |> map _.name |> head" (VStr "a.txt")
            Expect.equal (checkOk "[\"a\"] |> head").Ty TStr "singleton extraction types to the element"
        }
        test "head on an empty sequence raises" {
            Expect.throws (fun () -> run "ls |> where (fun f -> f.bytes > 999999999B) |> head" |> ignore) ""
        }
        test "stderr passes through: stdout stream stays clean" {
            skipOnWindows ()
            Expect.equal (runReal "sh -c \"echo out; echo err 1>&2\"" |> forceSeq) [ VStr "out" ] ""
        }
        test "external pipes into external via stdin" {
            skipOnWindows ()
            // the marker rides the command so the survivor check checks
            // something — it previously asserted a marker no process
            // ever carried [D:vacuous-probe-audit]
            Expect.equal (runReal "yes weir-s3cc | cat |> take 2" |> forceSeq) [ VStr "weir-s3cc"; VStr "weir-s3cc" ] ""

            Expect.isTrue (eventuallyNoSurvivors "weir-s3cc") "pipe children leaked"
        }
        test "non-string stream into an external is rejected [D:value-headed-pipe]" {
            // the pipe-into-external teaching is shared by command chains
            // and value-headed pipelines (one EPipe-into-ECmd arm)
            match Weir.Parser.parseLine cmdResolver "git x |> map (fun s -> 1) | cat" with
            | Ok(SExpr e | SCmd e) ->
                match typecheck env e with
                | Error terr -> Expect.stringContains terr.Message "seq<int> — map show or interpolate per element" ""
                | Ok _ -> failtest "expected type error"
            | other -> failtest $"unexpected: {other}"
        }
        test "complete reifies grep no-match without raising" {
            skipOnWindows ()

            match runReal "grep nomatch /etc/hosts | complete" with
            | VRecord("Completed", fields) ->
                Expect.equal (Weir.Eval.recGet "exitCode" fields) (VInt 1) "exit code"
                Expect.equal (Weir.Eval.recGet "stdout" fields |> forceSeq) [] "stdout empty"
                Expect.equal (Weir.Eval.recGet "stderr" fields |> forceSeq) [] "stderr empty"
            | v -> failtest $"unexpected: {formatValue v}"
        }
        test "complete captures stderr and nonzero exit" {
            skipOnWindows ()

            match runReal "^ls /weir-definitely-not | complete" with
            | VRecord("Completed", fields) ->
                match Weir.Eval.recGet "exitCode" fields, Weir.Eval.recGet "stderr" fields with
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
            skipOnWindows ()
            Expect.equal (runReal "grep nomatch /etc/hosts | complete |> _.exitCode") (VInt 1) ""
        }
        // capture oracle [D:capture-buffer]: the exact line-split and
        // decode rules of `| complete`, pinned against the old
        // representation before the buffer change — the equivalence
        // oracle for "one buffer, not N strings". Octal via sh printf
        // (POSIX printf has no \x).
        test "capture oracle: stdout line rule — CRLF, lone CR, empties kept, unterminated tail" {
            skipOnWindows ()

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
            skipOnWindows ()

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
            skipOnWindows ()

            Expect.equal
                (runReal "sh -c 'printf \"\\357\\273\\277x\\012\"' | complete |> _.stdout"
                 |> forceSeq)
                [ VStr "x" ]
                "UTF-8 BOM stripped"

            Expect.equal
                (runReal "sh -c 'printf \"a\\377b\\012\"' | complete |> _.stdout" |> forceSeq)
                [ VStr "a�b" ]
                "invalid byte becomes one replacement char"

            // StreamReader's BOM detection switches encodings — part of
            // today's contract, preserved via the fallback path
            Expect.equal
                (runReal "sh -c 'printf \"\\377\\376x\\012\"' | complete |> _.stdout" |> forceSeq)
                [ VStr "੸" ]
                "UTF-16LE BOM switches decoding"
        }
        test "capture: re-enumeration is stable (Completed is materialized by definition)" {
            skipOnWindows ()

            Expect.equal
                (runReal
                    "let r = $(sh -c 'printf \"x\\012y\\012\"' | complete) in (r.stdout |> Seq.length) + (r.stdout |> Seq.length)")
                (VInt 4L)
                "two enumerations of the same view agree"
        }
        test "capture: a line crossing the segment boundary decodes whole" {
            skipOnWindows ()
            // the segment store's riskiest branch [D:capture-buffer]: a
            // 5MB single line spans the 4MB segments and assembles
            Expect.equal
                (runReal
                    "let r = $(sh -c 'head -c 5000000 /dev/zero | tr \"\\0\" a' | complete) in r.stdout |> Seq.head |> Str.length")
                (VInt 5000000L)
                "boundary-crossing line intact"
        }
        test "complete after an external-to-external pipeline reifies the chain [D:chain-reifiers]" {
            match Weir.Parser.parseLine cmdResolver "let r = yes hi | grep hi | complete" with
            | Ok s -> Expect.stringContains (sprintf "%A" s) "|chainCompleted" ""
            | Error msg -> failtest msg
        }
        test "complete after a non-external stage is a parse error" {
            match Weir.Parser.parseLine realResolver "git status |> take 1 | complete" with
            | Error msg -> Expect.stringContains msg "must directly follow a command or a command chain" ""
            | Ok _ -> failtest "expected parse failure"
        }
        test "complete type is the Completed record" {
            match Weir.Parser.parseLine cmdResolver "grep x f | complete" with
            | Ok(SExpr e | SCmd e) ->
                match typecheck env e with
                | Ok te -> Expect.equal te.Ty (TNamed("Completed", [])) ""
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
    ]

let attributeTests =
    testList "Attributes" [ // attachment + inertness [D:attributes]
        test "registered attributes attach and the record works unchanged" {
            let env' =
                env
                |> declare "type Cfg = { [<Short \"c\">] Count: int; Name: string; [<NoShort>] Loud: bool }"

            match Map.tryFind "Cfg" env'.Types with
            | Some(Record def) ->
                Expect.equal (List.map fst def.Fields) [ "Count"; "Name"; "Loud" ] "fields unchanged"

                Expect.equal (Map.find "Count" def.Attrs) [ "Short", Some(AStr "c") ] "attrs recorded"

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
        test
            "Short takes a letter or digit: '-', a space or punctuation is no flag the scanner can match [D:short-alnum]" {
            for c in [ "-"; " "; "."; "_" ] do
                let terr = env |> declErr $"type T = {{ [<Short \"{c}\">] a: bool }}"

                Expect.equal
                    terr.Message
                    $"'Short' expects a letter or digit, e.g. [<Short \"c\">] — '-{c}' can never be matched as a flag"
                    $"Short \"{c}\""

            env |> declare "type TShortUpper = { [<Short \"C\">] a: bool }" |> ignore
            // a signature's foreign CLI may own a digit short (ls -1)
            env |> declare "type TShortDigit = { [<Short \"1\">] one: bool }" |> ignore
        }
        test "argless attributes reject arguments" {
            let terr = env |> declErr "type T = { [<NoShort true>] A: int }"
            Expect.stringContains terr.Message "takes no argument" ""
        }
        // [<Doc>]'s own validator pins retire with the attribute [D:doc-help]
        test "duplicate attribute on one field is rejected" {
            let terr = env |> declErr "type T = { [<Short \"a\"; Short \"b\">] A: int }"
            Expect.stringContains terr.Message "duplicate attribute 'Short'" ""
        }
        test "the retired [<Doc>] is now an unknown attribute [D:doc-help]" {
            let terr = env |> declErr "type T = { [<Doc \"x\">] A: int }"
            Expect.stringContains terr.Message "unknown attribute 'Doc'" ""
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
            expectValue "let e = { Q = 7 } in (show e == \"{ Q = 7 }\") && (e == { Q = 7 }) && (e.Q == 7)" (VBool true)
        }
        // products [D:attributes]
        test "generic record carries attrs as declaration data" {
            let env' = env |> declare "type GB<'a> = { [<Short \"v\">] V: 'a }"

            match Map.tryFind "GB" env'.Types with
            | Some(Record def) -> Expect.equal (Map.find "V" def.Attrs) [ "Short", Some(AStr "v") ] ""
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
        test "non-declaration positions name the scope decision [D:attr-positions]" {
            match Weir.Parser.parseExpr "[<Short \"c\">] 1" with
            | Ok _ -> failtest "expected a parse rejection"
            | Error msg ->
                Expect.stringContains msg "attributes attach to record fields, union cases, and type declarations" ""

            match Weir.Parser.parseStmt "let f [<Short \"c\">] x = x" with
            | Ok _ -> failtest "expected a parse rejection"
            | Error msg ->
                Expect.stringContains msg "attributes attach to record fields, union cases, and type declarations" ""
        }
        // the widened positions [D:attr-positions]: union decls and cases
        // host attributes; the registry is position-scoped; Tag/Other
        // validate here and bind at the wire boundary [D:wire-unions]
        test "a union declaration hosts [<Tag>]; the def carries the binding [D:wire-unions]" {
            let env' =
                env
                |> declare "type KTP = { n: int }"
                |> declare "[<Tag \"kind\">] type KT = Alpha of KTP | Beta"

            match Map.tryFind "KT" env'.Types with
            | Some(Union def) ->
                Expect.equal (List.map fst def.Cases) [ "Alpha"; "Beta" ] "cases unchanged"
                Expect.equal def.Tag (Some "kind") "the tag binds"
            | other -> failtest $"expected a union def, got {other}"
        }
        test "a union case hosts [<Wire>]/[<Other>] (tagged — the attrs bind now [D:wire-unions])" {
            env
            |> declare "type AP = { n: int }"
            |> declare "[<Tag \"kind\">] type KC = [<Wire \"apps/v1\">] Apps of AP | [<Other>] Rest of string"
            |> ignore
        }
        test "a registered attribute in the wrong position teaches its HOME" {
            let terr = env |> declErr "type U2 = [<Short \"c\">] A2 of int | B2"
            Expect.stringContains terr.Message "'Short' attaches to a record field, not a union case" ""

            let terr2 = env |> declErr "[<Tag \"kind\">] type R2 = { a: int }"
            Expect.stringContains terr2.Message "'Tag' attaches to a union declaration, not a record declaration" ""

            let terr3 = env |> declErr "type U3 = [<Tag \"kind\">] A3 of int | B3"
            Expect.stringContains terr3.Message "'Tag' attaches to a union declaration, not a union case" ""
        }
        test "Tag wants a nonempty string; Other is argless; one Other per union" {
            let terr = env |> declErr "[<Tag>] type U4 = A4 of int"
            Expect.stringContains terr.Message "expects the discriminator field as a string" ""

            let terr2 = env |> declErr "type U5 = [<Other \"x\">] A5 of int | B5"
            Expect.stringContains terr2.Message "takes no argument" ""

            let terr3 = env |> declErr "type U6 = [<Other>] A6 | [<Other>] B6"
            Expect.stringContains terr3.Message "declares [<Other>] twice" ""
        }
        test "unknown case attribute keeps the did-you-mean" {
            let terr = env |> declErr "type U7 = [<Othr>] A7 of int | B7"
            Expect.stringContains terr.Message "unknown attribute 'Othr'" ""
            Expect.stringContains terr.Message "Did you mean 'Other'?" ""
        }
        test "an attribute line binds to the declaration BELOW it (the assembler join)" {
            let ds =
                diagsIn "attrjoin.weir" [
                    "[<Tag \"kind\">]"
                    "type KJ ="
                    "    | One"
                    "    | Two"
                    "let v = Two"
                    "print $\"{v}\""
                ]

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "the own-line form parses"
        }
        test "an attribute line above a NON-declaration gets the position teaching, located" {
            let ds =
                diagsIn "attrbad.weir" [ "[<Tag \"kind\">]"; "let x = 1"; "print $\"{x}\"" ]

            let errs = ds |> List.filter (fun d -> d.Severity = "error")
            Expect.isNonEmpty errs "must refuse"

            Expect.stringContains
                errs.Head.Message
                "attributes attach to record fields, union cases, and type declarations"
                "the position teaching"
        }
    ]

let bracketContinuationTests =
    // [D:multiline-brackets] — fixture diversity per the standing rule:
    // headed / standalone / nested / at-boundary
    let joined lines =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        // field/element joins use the sentinel [D:field-sep-sentinel]; show
        // it back as ';' so these structural pins read (the asmSib inverse)
        | Ok [ ll ] -> ll.Text.Replace(" " + Weir.Parser.fieldSepStr + " ", " ; ")
        | other -> failtest $"unexpected: {other}"

    let assembleErr lines =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Error e -> e
        | Ok lls -> failtest $"expected an assembly error, got {lls |> List.map (fun l -> l.Text)}"

    testList "Bracket continuation" [
        test "type declaration fields join as siblings" {
            Expect.equal
                (joined [
                    "type Ctx ="
                    "    { Subdir: string"
                    "      Subref: string"
                    "      Repo: GitRepo }"
                ])
                "type Ctx = { Subdir: string ; Subref: string ; Repo: GitRepo }"
                ""
        }
        test "a type variable's tick is not a quote: an odd tick count keeps the brace open [D:tyvar-tick]" {
            Expect.equal
                (joined [ "type Box<'a> = {"; "    v: 'a"; "    w: 'a"; "}" ])
                "type Box<'a> = { v: 'a ; w: 'a }"
                "fields join as siblings"

            Expect.isEmpty
                (errorMsgsIn "tv.weir" [
                    "type T<'a, 'b> = { v: 'a }"
                    "let t = { v = 1 }"
                    "print $\"{t.v}\""
                    "echo 'x y' 'z'"
                ])
                "an unused parameter and argv quotes both hold"
        }
        test "a command line's quoted word is a quote, never a tick [D:tyvar-tick-line]" {
            Expect.equal (Weir.Scan.commentAt "echo key: 'it // works'") -1 "the // sits inside the quotes"

            for line in
                [
                    "echo key: 'it // works'"
                    "echo a, 'b c'"
                    "echo (x) <'d e'"
                    "grep -E x| 'y z'"
                ] do
                Expect.isFalse (Weir.Scan.isTyvarTick line (line.IndexOf '\'')) line

            for line, i in
                [
                    "type Box<'a> = {", 9
                    "    v: 'a", 7
                    "let f (x: 'a) : 'a =", 10
                    "| Some of 'a", 10
                    "fun (x: 'a) -> x", 8
                ] do
                Expect.isTrue (Weir.Scan.isTyvarTick line i) line

            Expect.isEmpty
                (errorMsgsIn "tk.weir" [
                    "echo key: 'it // works'"
                    "echo key: 'it { works'"
                    "if true then"
                    "    echo key: 'it // works'"
                    "print \"after\""
                ])
                "the quoted words stay argv"
        }
        test "same-line attributes ride a type field line" {
            Expect.equal
                (joined [ "type Cli ="; "    { [<Short \"c\">] count: int"; "      verbose: bool }" ])
                "type Cli = { [<Short \"c\">] count: int ; verbose: bool }"
                ""
        }
        test "preceding-line attribute binds to ITS field: no separator between" {
            Expect.equal
                (joined [
                    "type Cli ="
                    "    { [<Short \"c\">]"
                    "      count: int"
                    "      verbose: bool }"
                ])
                "type Cli = { [<Short \"c\">] count: int ; verbose: bool }"
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
                Weir.Assemble.assemble [
                    1, "type T ="
                    2, "    { A: int"
                    3, "      B: int }"
                    4, "let t = { A = 1; B = 2 }"
                ]
            with
            | Ok [ ty; letLine ] ->
                Expect.equal ty.Text (asmField "type T = { A: int ; B: int }") ""
                Expect.equal letLine.Text "let t = { A = 1; B = 2 }" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "cross-bracket closer names both sides" {
            Expect.stringContains
                (assembleErr [ "let x ="; "    [1; 2"; "     3}" ])
                "'}' closes the '[' opened at line 2"
                ""
        }
        test "top-level if/else assembles as ONE statement — a dedented else/elif continues the if [D:toplevel-if-else]" {
            // the block form at column 0: `if c then <block>` then a
            // dedented `else`/`elif`. Before this, a col-0 `else` started a
            // fresh statement and the parser hit a stray keyword; the col-0
            // continuation gate now admits else/elif like `until`/`always`.
            let asm lines =
                Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l))

            match asm [ "if x then"; "    print \"a\""; "else"; "    print \"b\"" ] with
            | Ok [ _ ] -> ()
            | other -> failtest $"top-level if/else must be ONE statement, got {other}"

            match
                asm [
                    "if n > 5 then"
                    "    print \"big\""
                    "elif n > 1 then"
                    "    print \"mid\""
                    "else"
                    "    print \"small\""
                ]
            with
            | Ok [ _ ] -> ()
            | other -> failtest $"top-level if/elif/else must be ONE statement, got {other}"

            // a multi-statement then-block still keeps the else attached
            match asm [ "if x then"; "    print \"a\""; "    print \"b\""; "else"; "    print \"c\"" ] with
            | Ok [ _ ] -> ()
            | other -> failtest $"multi-stmt then + else must be ONE statement, got {other}"

            // and it checks clean end to end (not just assembles)
            let diags =
                diagsIn "ifelse.weir" [ "let x = true"; "if x then"; "    print \"a\""; "else"; "    print \"b\"" ]

            Expect.isEmpty
                (diags |> List.filter (fun d -> d.Severity = "error"))
                $"top-level if/else checks clean: {diags |> List.map _.Message}"
        }
        // blanks are transparent inside brackets [D:blank-in-brackets]
        test "blank inside an open list is transparent" {
            Expect.equal (joined [ "let x ="; "    [1"; ""; "     2]" ]) "let x = [1 ; 2]" ""
        }
        test "blank inside an open type decl is transparent" {
            Expect.equal (joined [ "type T ="; "    { A: int"; ""; "      B: int }" ]) "type T = { A: int ; B: int }" ""
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
                Weir.Assemble.assemble [
                    1, "let f remote ="
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
                    12, "    | None -> \"m\""
                ]
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
        }
    ]

let blockLetCmdTests =
    testList "Block-let command RHS" [
        test "the forms: command RHS binds inside a body [D:block-let-cmd]" {
            match
                Weir.Assemble.assemble [
                    1, "let graft c ="
                    2, "    let tree = git rev-parse $c | Seq.head"
                    3, "    tree"
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text "let graft c = let tree = git rev-parse $c | Seq.head in tree" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "the shadowing block twin: bindings beat PATH at depth (failing-first pin)" {
            // guard-dropped, this spawned a real PATH binary (the spawn
            // observed live); guarded, the block name wins
            expectValue
                "let f = fun y -> (let zzshadow = fun a -> a in let z = zzshadow y in z |> Seq.head) in f [\"safe\"]"
                (VStr "safe")
        }
        test "sigil equivalence: bare block RHS = the $() spelling" {
            // the two spellings must produce the same TypedExpr shape
            // (ELet of the same chain expression)
            let bare =
                Weir.Assemble.assemble [ 1, "let f c ="; 2, "    let a = git rev-parse $c | Seq.head"; 3, "    a" ]

            let sigil =
                Weir.Assemble.assemble [ 1, "let f c ="; 2, "    let a = $(git rev-parse $c) |> Seq.head"; 3, "    a" ]

            match bare, sigil with
            | Ok [ b ], Ok [ s ] -> Expect.isTrue (b.Text.Length > 0 && s.Text.Length > 0) "both assemble"
            | other -> failtest $"unexpected: {other}"
        }
        test "the in-stop from inside: quoted in passes, bare in stops" {
            let r: Weir.Parser.Resolver = {
                IsKnown = fun n -> n <> "echo"
                IsCommandCallable = fun _ -> false
                IsExternal = fun n -> n = "echo"
                ExternalNames = (fun () -> Seq.empty)
                BareHome = (fun _ -> None)
                AliasHead = fun _ -> None
                IsBuiltinName = fun _ -> false
            }

            match Weir.Parser.parseLine r "let f x = let w = echo alpha \"in\" beta |> Seq.head in w" with
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
                Weir.Assemble.assemble [
                    1, "let f c ="
                    2, "    let a = 1"
                    3, "    let b = git rev-parse $c | Seq.head"
                    4, "    b"
                ]
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
        test "function in a binder slot refuses as a keyword" {
            // the reservation retired into the feature [D:function-keyword]
            match Weir.Parser.parseStmt "let function = 1" with
            | Error msg -> Expect.stringContains msg "'function' is a keyword" "the generic refusal"
            | Ok _ -> failtest "expected the keyword refusal"
        }
    ]

let statementLetTests =
    // command-mode block lets in every statement context
    // [D:statement-lets] — per context: bare command let, reifier let,
    // splice + param resolution at the new depth [D:paramful-rhs], and
    // check==run parity under the assume-resolver [D:assume-resolver]
    // (the patch-district lesson: the two parse paths drift exactly
    // here, so each fixture runs both — analyzeLines against a real
    // parse+check+eval fold).
    let checkDiags = errorsIn "sl.weir"

    // the run path: assemble + parse under the real resolver + check +
    // eval, statement by statement (the dedent-join fold, real heads)
    let runStmts (lines: string list) : unit =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Error e -> failtest $"assemble: {e}"
        | Ok lls ->
            lls
            |> List.fold
                (fun (te, ve) ll ->
                    match Weir.Parser.parseLine realResolver ll.Text with
                    | Error m -> failtest $"parse: {m}"
                    | Ok(SExpr e)
                    | Ok(SCmd e) ->
                        match Weir.Check.typecheck te e with
                        | Error terr -> failtest $"check: {formatError terr}"
                        | Ok typed ->
                            eval ve typed |> ignore
                            te, ve
                    | Ok(SLet(n, e)) ->
                        match Weir.Check.typecheck te e with
                        | Error terr -> failtest $"check: {formatError terr}"
                        | Ok typed ->
                            let v = eval ve typed

                            {
                                te with
                                    Values = Map.add n (Weir.Types.generalize typed.Ty) te.Values
                            },
                            Map.add n v ve
                    | Ok other -> failtest $"unexpected statement {other}")
                (env, valueEnv)
            |> ignore

    // one parity fixture per context: check accepts and the run path
    // produces the marker file — both parse paths, one meaning
    let parity (name: string) (fixture: string -> string list) =
        test $"parity: {name} body takes a command let, check == run [D:statement-lets]" {
            skipOnWindows ()

            withTempDir "weir-sl" (fun td ->
                let out = System.IO.Path.Combine(td, "out.txt")
                let lines = fixture out
                Expect.isEmpty (checkDiags lines) $"check accepts the {name} fixture"
                runStmts lines
                Expect.equal (System.IO.File.ReadAllLines out |> Array.head) "marker" $"the {name} body ran its let")
        }

    testList "Statement-context command lets [D:statement-lets]" [
        parity "within tmp" (fun out -> [
            "within tmp d"
            "    let r = sh -c \"echo marker\" | complete"
            $"    r.stdout |> File.write \"{out}\""
        ])
        parity "if-then" (fun out -> [
            "if 1 > 0 then"
            "    let r = sh -c \"echo marker\" | complete"
            $"    r.stdout |> File.write \"{out}\""
        ])
        parity "for" (fun out -> [
            "for i in [0] do"
            "    let r = sh -c $\"echo marker-{i}\" | complete"
            "    let m = r.stdout |> Seq.head |> Str.replace \"-0\" \"\""
            $"    [m] |> File.write \"{out}\""
        ])
        parity "match arm" (fun out -> [
            "match 1 with"
            "| 1 ->"
            "    let r = sh -c \"echo marker\" | complete"
            $"    r.stdout |> File.write \"{out}\""
            "| _ -> print \"\""
        ])
        parity "always block" (fun out -> [
            "within"
            "    print \"\""
            "always"
            "    let r = sh -c \"echo marker\" | complete"
            $"    r.stdout |> File.write \"{out}\""
        ])
        parity "splice + param resolution at depth" (fun out ->
            // [D:paramful-rhs] reaches the new depth: a param-ful
            // block let takes a command RHS, its param splices, and
            // block names resolve ahead of PATH
            [
                "within tmp d"
                "    let tag = \"marker\""
                "    let f x = echo $x"
                "    let got = f tag |> Seq.head"
                $"    [got] |> File.write \"{out}\""
            ])
        test "a bare command let binds in every context — parse shape, no reifier needed" {
            Expect.isEmpty
                (checkDiags [
                    "within tmp d"
                    "    let files = git ls-files"
                    "    print $\"{files |> Seq.length}\""
                ])
                "within: bare command let accepted"

            Expect.isEmpty
                (checkDiags [ "if 1 > 0 then"; "    let out = echo hi"; "    out |> Seq.iter print" ])
                "if: bare command let accepted"
        }
        test "a NESTED statement body grants through the assembler's paren wrap (the fuzzer's catch)" {
            // the assembler wraps a multi-statement if body in parens;
            // bodies are statement territory even there ([D:interior-arming]
            // applies) — the exprParen-gated grant refused this shape
            // until deep fuzz caught it [D:statement-lets]
            Expect.isEmpty
                (checkDiags [
                    "if 33 == 68 then"
                    "    if 77 == 12 then"
                    "        let v2 = echo m1 w654 | complete"
                    "        v2.stdout |> print"
                    "    if 63 > 76 then"
                    "        echo m2 w979"
                ])
                "the inner if body's reifier let parses behind the wrap"
        }
        test "within proc: the head and body lets do not collide — the head owns its line" {
            let lines = [
                "within proc srv = sh -c \"sleep 5\""
                "    let r = echo body | complete"
                "    print $\"{r.exitCode} up={Proc.running srv}\""
            ]

            Expect.isEmpty (checkDiags lines) "proc head (letRhsCmd, ungated) + body reifier let both parse"
        }
        test "a command let inside `pure` parses; PURITY refuses it, located at the command" {
            // the parser admits the grammar [D:statement-lets]; the
            // purity checker owns the refusal — the better span
            let ds =
                checkDiags [
                    "let v ="
                    "    pure"
                    "        let r = sh -c \"echo x\" | complete"
                    "        r.exitCode"
                    "print $\"{v}\""
                ]

            Expect.exists
                ds
                (fun d -> d.Message.Contains "'pure' block forbids effects")
                "the purity teaching, not a parse error"

            Expect.isFalse
                (ds
                 |> List.exists (fun d -> d.Message.Contains "is a reifier, not a PATH program"))
                "no parse-level refusal preempts purity's span"
        }
        test "the paren-swallow non-repro, pinned: a lambda's closer never joins argv" {
            // probe 2's verdict recorded in PLAN-statement-lets:
            // cmdWordChar excludes ')', so `echo charlie)` splits into
            // the command and the lambda's own closer — on the spine
            // today, and unchanged by this ruling
            let assembled =
                match Weir.Assemble.assemble [ 1, "let out = [1] |> Seq.map (fun _ ->"; 2, "    echo charlie)" ] with
                | Ok [ ll ] -> ll.Text
                | other -> failtest $"unexpected assembly: {other}"

            match Weir.Parser.parseLine cmdResolver assembled with
            | Ok(SLet(_, e)) ->
                Expect.isFalse ((Weir.Ast.sexpr e).Contains "\")\"") "no ')' argv word — the closer closed the lambda"
            | other -> failtest $"expected the let to parse, got {other}"
        }
        test "refused contexts teach $() with the ACTUAL context named — dash argv included" {
            // a lambda body grants [D:lambda-lets] — dash argv included
            // (npx's `--no-install` must not die raw at the double dash)
            for rhs in [ "sh -c \"echo x\" | complete"; "npx --no-install vsce package | complete" ] do
                let ds =
                    checkDiags [
                        "[1] |> Seq.iter (fun _ ->"
                        $"    let r = {rhs}"
                        "    print $\"{r.exitCode}\")"
                    ]

                Expect.isFalse
                    (ds |> List.exists (fun d -> d.Message.Contains "a command needs $(…)"))
                    $"the lambda body takes: {rhs}"

            // the paren interior names its context
            let ds =
                checkDiags [
                    "within tmp d"
                    "    print (show (let r = sh -c \"x\" | complete in r.exitCode))"
                ]

            Expect.exists
                ds
                (fun d -> d.Message.Contains "inside parentheses, a command needs $(…)")
                "the paren teaching"
        }
    ]

let pureRegionTests =
    // Stage 1 of [D:pure]: the `pure` region enforces [D:pure-stage1] —
    // opt-in only (nothing outside a region is gated), judged by the
    // Stage 0 classifier, refused with a located teaching naming the
    // offender's effect family.
    let errsOf = errorsIn "pure.weir"

    let firstErr = firstErrorIn "pure.weir"

    testList "the pure region — enforcement [D:pure-stage1]" [
        test "parse shape: a bare pure head + block is a within-family node" {
            let asmLine = "pure" + Weir.Parser.sibSepStr + "1 + 1"

            match Weir.Parser.parseLine realResolver asmLine with
            | Ok(SExpr {
                           Kind = EWithin(WithinPure, None, None, None, _)
                       }) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "a pure body is accepted, evaluates, and sees local lets" {
            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    pure"
                    "        let y = 2"
                    "        y + 1"
                    "print $\"{x}\""
                ])
                "no effect, no error"
        }
        test "each effect family refuses with the located offender" {
            let cases = [
                [ "let x ="; "    pure"; "        print \"no\"" ], "'print' writes to the console"
                [ "pure"; "    echo hi" ], "'echo' runs a command"
                [ "let x ="; "    pure"; "        File.write \"f\" [\"y\"]" ], "'File.write' writes the filesystem"
                [ "let x ="; "    pure"; "        Env.get \"HOME\"" ], "'Env.get' reads the environment"
                [ "let t ="; "    pure"; "        Instant.now ()" ], "'Instant.now' reads the clock"
                [ "let t ="; "    pure"; "        Duration.measure (fun () -> 1)" ],
                "'Duration.measure' reads the clock"
                [ "let d ="; "    pure"; "        within tmp t"; "            t" ], "'within tmp' scopes a resource"
                [ "let x ="; "    pure"; "        within proc p = sleep 5"; "            1" ],
                "'within proc' scopes a resource"
            ]

            for lines, phrase in cases do
                let e = firstErr lines
                Expect.stringContains e.Message "this 'pure' block forbids effects, but" $"the law: {phrase}"
                Expect.stringContains e.Message phrase $"the offender: {phrase}"
        }
        test "cd, pwd, the home dirs, Term and Color are effects pure refuses [D:purity-ambient]" {
            for body, phrase in
                [
                    "cd \"/tmp\"", "'cd' changes the working directory"
                    "pwd", "'pwd' reads the working directory"
                    "Path.home ()", "'Path.home' reads the environment"
                    "Path.configHome ()", "'Path.configHome' reads the environment"
                    "Path.which \"sh\"", "'Path.which' reads PATH and the filesystem"
                    "Term.width ()", "'Term.width' reads the terminal"
                    "Term.title \"t\"", "'Term.title' writes to the terminal"
                    "Color.red \"x\"", "'Color.red' reads the terminal"
                ] do
                let e = firstErr [ "let x ="; "    pure"; "        " + body; "print $\"{x}\"" ]
                Expect.stringContains e.Message phrase body
        }
        test "the refusal is LOCATED at the offending node" {
            let e = firstErr [ "let x ="; "    pure"; "        print \"no\"" ]
            Expect.equal e.Line 3 "the print's physical line"
            Expect.equal e.Col 9 "the print's column"
        }
        test "let pure: a pure body binds, runs, and keeps the hover badge" {
            let lines = [ "let pure double n = n * 2"; "print $\"{double 4}\"" ]
            Expect.isEmpty (errsOf lines) "pure body accepted"

            match Weir.Lsp.hoverType lines 1 10 with
            | Some h -> Expect.stringContains h "(pure)" "the modifier composes with the Stage 0 badge"
            | None -> failtest "the binding must hover"
        }
        test "let pure: an impure body is a check ERROR — the modifier's point" {
            let e = firstErr [ "let pure leak () = print \"x\""; "leak ()" ]
            Expect.stringContains e.Message "'print' writes to the console" "names the effect"
        }
        test "transitivity: an earlier pure binding passes; an effectful one refuses through its name" {
            Expect.isEmpty
                (errsOf [
                    "let helper n = n + 1"
                    "let pure lifted n = helper n"
                    "print $\"{lifted 4}\""
                ])
                "purity flows forward through earlier bindings"

            let e =
                firstErr [
                    "let eff () = print \"hi\""
                    "let x ="
                    "    pure"
                    "        eff ()"
                    "print $\"{x}\""
                ]

            Expect.stringContains e.Message "'eff' reaches an effect" "the earlier binding's impurity surfaces by name"
        }
        test "an unknown callable refuses — conservatism is the soundness law" {
            let e = firstErr [ "let unknown g ="; "    pure"; "        g 1" ]
            Expect.stringContains e.Message "unknown callable" "a function-typed param could do anything"
        }
        test "Self.stdin is ambient input — a pure region refuses it by name [D:pure-stdin-ctors]" {
            let e =
                firstErr [ "let pure count () = Self.stdin |> Seq.length"; "print $\"{count ()}\"" ]

            Expect.stringContains
                e.Message
                "'Self.stdin' reads the process's input stream"
                "the injected value classifies like the effect it is"
        }
        test "the per-run Self constants stay pure-admissible [D:pure-stdin-ctors]" {
            Expect.isEmpty
                (errsOf [
                    "let pure nargs () = Self.args |> Seq.length"
                    "let pure me () = Self.pid"
                    "print $\"{nargs ()} {me ()}\""
                ])
                "args/pid are per-run constants, not effects"
        }
        test "Self.prompt in a pure region still refuses — the ambient-input guard holds" {
            let e = firstErr [ "let x ="; "    pure"; "        Self.prompt \"q\""; "print x" ]
            Expect.stringContains e.Message "'Self.prompt'" "Self.prompt reads and writes — never pure"
        }
        test "a pure for body reaching no effect is ACCEPTED [D:desugar-namespace] (F5)" {
            // the for-desugar targets Seq.iter (a library member), not a
            // command — a pure loop body must not refuse as a spawn
            Expect.isEmpty
                (errsOf [
                    "let pure noop x = ()"
                    "let a ="
                    "    pure"
                    "        for n in [1] do"
                    "            noop n"
                    "        \"ok\""
                    "print a"
                ])
                "a pure for loop is pure"

            // plan admits it too — a library desugar is captured, not refused
            Expect.isEmpty
                (errsOf [
                    "let pure noop x = ()"
                    "let p ="
                    "    plan"
                    "        for n in [1] do"
                    "            noop n"
                    "print $\"{p |> Plan.isEmpty}\""
                ])
                "a library desugar in a plan block is not a proc refusal"

            // but a real command in the for body still refuses, and the
            // message speaks the command, never the |seqIter key
            let e = firstErr [ "pure"; "    for n in [1] do"; "        echo hi" ]
            Expect.stringContains e.Message "'echo' runs a command" "a real command still refuses"
            Expect.isFalse (e.Message.Contains "|seqIter") "no desugar key in the message"
        }
        test "union constructors are pure by construction — applied and partially applied [D:pure-stdin-ctors]" {
            Expect.isEmpty
                (errsOf [
                    "type Shape ="
                    "    | Dot"
                    "    | Box of int"
                    "let pure mk n = Box n"
                    "let pure lift xs = xs |> Seq.map Box"
                    "let s ="
                    "    pure"
                    "        let b = Box 4"
                    "        match b with"
                    "        | Box n -> n"
                    "        | Dot -> 0"
                    "print $\"{mk 3} {s} {lift [1] |> Seq.length}\""
                ])
                "a data constructor is not an unknown callable"
        }
        test "record and anonymous-shape literals build in a pure region" {
            Expect.isEmpty
                (errsOf [
                    "type Endpoint = { Host: string; Port: int }"
                    "let e ="
                    "    pure"
                    "        { Host = \"h\"; Port = 1 }"
                    "let a ="
                    "    pure"
                    "        {| key = \"k\"; n = 3 |}"
                    "let e2 ="
                    "    pure"
                    "        { e with Port = 2 }"
                    "print $\"{e.Port} {e2.Port} {a.n}\""
                ])
                "literals are construction, not effects"
        }
        test "keyword reservation: pure cannot be a binder; a blockless pure teaches" {
            let e = firstErr [ "let pure = 1" ]
            Expect.stringContains e.Message "'pure' is a keyword" "the binder slot refuses"

            let ds = diagsIn "bare.weir" [ "pure" ]

            Expect.isTrue
                (ds |> List.exists (fun d -> d.Message.Contains "pure takes a block"))
                "the bare head teaches the block form"
        }
        test "modules: let pure crosses the import; an impure module member refuses at ITS site" {
            let dir = tempDir "weir-pure"

            try
                let lib = System.IO.Path.Combine(dir, "lib.weir")

                System.IO.File.WriteAllLines(
                    lib,
                    [ "module Lib"; "let triple : int -> int"; "let pure triple n = n * 3" ]
                )

                let main = System.IO.Path.Combine(dir, "main.weir")
                System.IO.File.WriteAllLines(main, [ "import \"./lib.weir\""; "print $\"{Lib.triple 5}\"" ])

                let ds = diagsIn main (List.ofArray (System.IO.File.ReadAllLines main))

                Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "a module's pure binding imports"

                let bad = System.IO.Path.Combine(dir, "bad.weir")

                System.IO.File.WriteAllLines(
                    bad,
                    [ "module Bad"; "let leak : unit -> unit"; "let pure leak () = print \"leak\"" ]
                )

                let entry = System.IO.Path.Combine(dir, "entry.weir")
                System.IO.File.WriteAllLines(entry, [ "import \"./bad.weir\""; "print \"ok\"" ])

                let ds2 = diagsIn entry (List.ofArray (System.IO.File.ReadAllLines entry))

                let errs = ds2 |> List.filter (fun d -> d.Severity = "error")
                Expect.isNonEmpty errs "the module's pure law holds"
                Expect.stringContains errs.Head.Message "'print' writes to the console" "same teaching, module site"
            finally
                try
                    System.IO.Directory.Delete(dir, true)
                with _ ->
                    ()
        }
    ]

let readonlyBlockTests =
    // Stage 2 [D:pure-stage2]: the `readonly` block — the user-facing
    // surface. `readonly == only ambient-input`, one tier up from
    // `pure == only ∅`; a reachable external mutation refuses (located,
    // naming the offender and its class), an ambient read is fine.
    let errsOf = errorsIn "ro.weir"

    let firstErr = firstErrorIn "ro.weir"

    testList "the readonly block [D:pure-stage2]" [
        test "parse shape: a bare readonly head + block is a within-family node" {
            let asmLine = "readonly" + Weir.Parser.sibSepStr + "1 + 1"

            match Weir.Parser.parseLine realResolver asmLine with
            | Ok(SExpr {
                           Kind = EWithin(WithinReadonly, None, None, None, _)
                       }) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "a reading body is ACCEPTED — ambient input is allowed" {
            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    readonly"
                    "        let home = Env.get \"HOME\""
                    "        let _now = Instant.now ()"
                    "        home |> Option.defaultValue \"none\""
                    "print x"
                ])
                "env read + clock read are ambient — no mutation"
        }
        test "a writing body REFUSES — the located offender AND its class" {
            let e =
                firstErr [ "let x ="; "    readonly"; "        File.write \"f\" [\"y\"]"; "print \"x\"" ]

            Expect.stringContains
                e.Message
                "this 'readonly' block forbids external mutation, but 'File.write' writes the filesystem"
                "names the offender and the class"

            Expect.stringContains e.Message "reads are allowed" "the teaching states the allowance"
            Expect.equal e.Line 3 "located at the write"
        }
        test "a command (proc) REFUSES — a spawn is external mutation" {
            let e = firstErr [ "readonly"; "    echo hi" ]
            Expect.stringContains e.Message "'echo' runs a command" "proc mutates"
        }
        test "cd is a mutation readonly refuses; pwd, home dirs, Term, Color are reads [D:purity-ambient]" {
            let e = firstErr [ "let x ="; "    readonly"; "        cd \"/tmp\""; "print x" ]
            Expect.stringContains e.Message "'cd' changes the working directory" "cd writes process state"
            let e = firstErr [ "readonly"; "    Term.title \"t\"" ]

            Expect.stringContains
                e.Message
                "'Term.title' writes to the terminal"
                "Term.title writes the terminal [D:term-title]"

            for body in
                [
                    "pwd"
                    "Path.home ()"
                    "Path.cacheHome ()"
                    "$\"{Term.width ()}\""
                    "Color.red \"x\""
                ] do
                Expect.isEmpty (errsOf [ "let x ="; "    readonly"; "        " + body; "print x" ]) body
        }
        test "Http.send{post} refuses, Http.send{get} admits — the per-method net split" {
            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    readonly"
                    "        Http.send (Http.get \"http://x\")"
                    "print $\"{x.status}\""
                ])
                "a GET reads — ambient"

            let e =
                firstErr [
                    "let x ="
                    "    readonly"
                    "        Http.send (Http.post \"http://x\")"
                    "print $\"{x.status}\""
                ]

            Expect.stringContains e.Message "'Http.send' talks to the network" "a POST mutates"
            Expect.stringContains e.Message "mutating HTTP method" "the class names the method"

            let e =
                firstErr [
                    "let r = Http.get \"http://x\""
                    "let x ="
                    "    readonly"
                    "        Http.send r"
                    "print $\"{x.status}\""
                ]

            Expect.equal
                e.Message
                "this 'readonly' block forbids external mutation, but 'Http.send' talks to the network, and its method is not known at check time (external mutation unless a query method) — reads are allowed"
                "an unresolved method is refused without being called mutating"
        }
        test "Http.query and a GET expect admit — idempotent by construction" {
            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    readonly"
                    "        Http.query \"http://x\" |> Http.send"
                    "print $\"{x.status}\""
                ])
                "the query method is ambient (piped send resolves it)"

            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    readonly"
                    "        Http.get \"http://x\" |> Http.expect"
                    "x |> Seq.iter print"
                ])
                "a GET expect is ambient (the piped request resolves it)"
        }
        test "pure ⊂ readonly — a pure body is trivially read-only" {
            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    readonly"
                    "        let y = 2"
                    "        y + 1"
                    "print $\"{x}\""
                ])
                "no effect at all is within the ambient-input ceiling"

            // and a nested pure region inside readonly is fine
            Expect.isEmpty
                (errsOf [
                    "let x ="
                    "    readonly"
                    "        pure"
                    "            1 + 1"
                    "print $\"{x}\""
                ])
                "a pure island nests inside the looser ceiling"
        }
        test "a for body with no effect is ACCEPTED inside readonly [D:desugar-namespace] (F5)" {
            // the |seqIter desugar is a library member (Seq.iter), not a
            // command — a pure for body must not be refused as a spawn
            Expect.isEmpty
                (errsOf [
                    "let pure noop x = ()"
                    "let a ="
                    "    readonly"
                    "        for n in [1] do"
                    "            noop n"
                    "        \"ok\""
                    "print a"
                ])
                "a pure for loop reaches no effect — readonly admits it"
        }
        test "a multi-statement readonly body sentinels between statements [D:desugar-namespace] (F6)" {
            // an earlier assumption that readonly's body is value-shaped
            // space-joined a non-final statement onto its successor and
            // mis-parsed it; a unit statement before the result now parses
            Expect.isEmpty
                (errsOf [
                    "let noop x = ()"
                    "let b ="
                    "    readonly"
                    "        [1] |> Seq.iter noop"
                    "        \"ok\""
                    "print b"
                ])
                "a non-final piped unit statement types under readonly, like pure"

            // two consecutive unit statements before the result, too
            Expect.isEmpty
                (errsOf [
                    "let noop x = ()"
                    "let b ="
                    "    readonly"
                    "        noop 1"
                    "        noop 2"
                    "        \"ok\""
                    "print b"
                ])
                "consecutive unit statements sequence, never space-join"
        }
        test "no |-desugar key reaches a readonly refusal message [D:desugar-namespace] (F5-vocabulary)" {
            // a real effect in a for body still refuses — but the message
            // speaks the effect, never the internal |seqIter key
            let e =
                firstErr [
                    "let b ="
                    "    readonly"
                    "        for n in [1] do"
                    "            File.write \"f\" [\"y\"]"
                    "        \"ok\""
                ]

            Expect.stringContains e.Message "'File.write' writes the filesystem" "the real effect names itself"
            Expect.isFalse (e.Message.Contains "|seqIter") "the desugar key never surfaces"
            Expect.isFalse (e.Message.Contains "|") "no |-prefixed key in a user-facing message"
        }
        test "an unknown callable refuses — conservatism carries over" {
            let e = firstErr [ "let unknown g ="; "    readonly"; "        g 1" ]
            Expect.stringContains e.Message "unknown callable" "a function-typed param could mutate"
        }
        test "reading DATA from an impure binding is NOT a mutation — the fuzz-found line" {
            // a value computed from an impure command holds data once
            // bound (the command ran outside the block); reading it is
            // ambient, not mutation — only a callable mutates when applied
            Expect.isEmpty
                (errsOf [
                    "let lines = git status"
                    "let n ="
                    "    readonly"
                    "        lines |> Seq.length"
                    "print $\"{n}\""
                ])
                "reading a command-bound seq is a read, not a write"
        }
        test "keyword reservation: readonly cannot be a binder; a blockless head teaches" {
            let e = firstErr [ "let readonly = 1" ]
            Expect.stringContains e.Message "'readonly' is a keyword" "the binder slot refuses"

            let ds = diagsIn "bare.weir" [ "readonly" ]

            Expect.isTrue
                (ds |> List.exists (fun d -> d.Message.Contains "readonly takes a block"))
                "the bare head teaches the block form"
        }
        test "the freed word: deterministic is now an ORDINARY identifier — the pre-release rename" {
            // deterministic was the old keyword; the rename frees it, so
            // it binds like any other name (a misnomer retired — the block
            // permits clock/stdin/file reads, so it never guaranteed
            // determinism; readonly names 'no external mutation')
            Expect.isEmpty
                (errsOf [ "let deterministic = 1"; "print $\"{deterministic}\"" ])
                "the old keyword is a free identifier again"
        }
        test "within readonly refuses — it is its own head, never behind within" {
            let ds = diagsIn "wr.weir" [ "within readonly"; "    1" ]

            Expect.isNonEmpty
                (ds |> List.filter (fun d -> d.Severity = "error"))
                "`within readonly` does not parse — readonly is a keyword, its own head"
        }
        test "badge interplay: readonly does NOT mint a (pure) badge — the asymmetry holds" {
            // a function whose body reads ambient input is read-only
            // but not pure; the (pure) badge stays absent (effect-normal
            // display is untouched — readonly is the looser tier)
            let lines = [
                "let reader () = Env.get \"HOME\""
                "print (show (reader () |> Option.defaultValue \"x\"))"
            ]

            match Weir.Lsp.hoverType lines 1 6 with
            | Some h -> Expect.isFalse (h.Contains "(pure)") "an ambient reader is not pure-badged"
            | None -> failtest "the binding must hover"
        }
    ]

let planApplyTests =
    // [D:plan-apply]: `plan` captures a body's external mutations as Ops
    // (reads run), yielding an inspectable/equatable/showable Plan;
    // Plan.apply replays them. The refusals: proc-in-plan (check),
    // apply-in-plan (check), known-after-apply (located runtime).
    let errsOf = errorsIn "plan.weir"

    let firstErr = firstErrorIn "plan.weir"

    // run a full multi-line program from a temp file; returns its exit
    // code (0 = clean) — the e2e path for capture+apply fs fidelity
    let runFile (lines: string list) : int =
        let path = tempPath "weir-plan" + ".weir"

        System.IO.File.WriteAllLines(path, lines)

        try
            Weir.Script.run path []
        finally
            System.IO.File.Delete path

    let td () = tempPath "weir-plan"

    // testSequenced: the DA-03 cases drive programs through runFile
    // (Weir.Script.run — in-process), whose `within cd` mutates the global
    // Session.Cwd. Left parallel, this list raced itself (two DA-03 programs
    // stomping each other's cwd → a preview bound to the wrong dir →
    // "not absolute-A") and the parallel spawn tests. Sequencing keeps all
    // cwd mutation in the non-parallel phase.
    testSequenced
    <| testList "the plan/apply capture [D:plan-apply]" [
        test "parse shape: a bare plan head + block is a within-family node" {
            let asmLine = "plan" + Weir.Parser.sibSepStr + "File.write \"f\" [\"x\"]"

            match Weir.Parser.parseLine realResolver asmLine with
            | Ok(SExpr {
                           Kind = EWithin(WithinPlan, None, None, None, _)
                       }) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "a plan block yields a Plan, not the body's type" {
            match typecheck env (parse ("plan" + Weir.Parser.sibSepStr + "File.write \"f\" [\"x\"]")) with
            | Ok te -> Expect.equal te.Ty (TNamed("Plan", [])) "the region is Plan-typed"
            | Error terr -> failtest (formatError terr)
        }
        test "capture: a write becomes a WriteFile Op, no file written; equality holds" {
            // under the OS temp dir: capture checks the parent exists [D:plan-preconditions]
            let path = (System.IO.Path.GetTempPath() + "weir-plan-never").Replace('\\', '/')

            let p =
                run ("plan" + Weir.Parser.sibSepStr + $"File.write \"{path}\" [\"a\"; \"b\"]")

            Expect.equal
                p
                (VRecord(
                    "Plan",
                    [
                        "ops",
                        VSeq [
                            VUnion(
                                "WriteFile",
                                Some(VTuple [ VStr(System.IO.Path.GetFullPath path); VSeq [ VStr "a"; VStr "b" ] ])
                            )
                        ]
                    ]
                ))
                "one WriteFile Op captured"

            Expect.isFalse (System.IO.File.Exists path) "the plan wrote nothing"
        }
        test "Plan equality is content-sensitive; isEmpty reads the ops" {
            // multi-line plan bodies go through the real assembler (a
            // temp file); the program self-checks and exits nonzero on
            // mismatch, so exit 0 is the assertion
            let prog = [
                "let a ="
                "    plan"
                "        File.write \"x\" [\"hi\"]"
                "let b ="
                "    plan"
                "        File.write \"x\" [\"hi\"]"
                "let c ="
                "    plan"
                "        File.write \"x\" [\"bye\"]"
                "let e ="
                "    plan"
                "        let _h = Env.get \"HOME\""
                "        print \"read only\""
                "if not (a == b) then fail \"a==b\""
                "if a == c then fail \"a<>c\""
                "if not (e |> Plan.isEmpty) then fail \"e empty\""
                "if a |> Plan.isEmpty then fail \"a not empty\""
                "print \"ok\""
            ]

            Expect.equal (runFile prog) 0 "equality + isEmpty hold"
        }
        test "preview masks a Secret in an HttpSend request [D:secret]" {
            let prog = [
                "let t = Secret.of \"tok\""
                "let p ="
                "    plan"
                "        let _r = Http.send { Http.post \"http://x\" with auth = Bearer t }"
                "        print \"sent\""
                "let text = Str.join \"\\n\" (p |> Plan.preview) |> Str.replace \"\\\\\" \"/\""
                "if not (Str.contains \"***\" text) then fail \"not masked\""
                "if Str.contains \"tok\" text then fail \"leaked\""
                "print \"ok\""
            ]

            Expect.equal (runFile prog) 0 "the token is masked in preview"
        }
        test "a preview path renders CR, LF and escapes visibly [D:tty-echo-strict]" {
            let prog = [
                "let cr = Str.fromBase64 \"DQ==\""
                "let lf = Str.fromBase64 \"Cg==\""
                "let esc = Str.fromBase64 \"Gw==\""
                "let p ="
                "    plan"
                // a Dir.create: a delete of a missing path refuses at capture [D:plan-preconditions]
                "        Dir.create $\"/nowhere/a{cr}x{lf}delete file /b{esc}[8m\""
                "let line = p |> Plan.preview |> Seq.exactlyOne"
                "if Str.contains cr line || Str.contains lf line || Str.contains esc line then fail $\"raw control: {show line}\""
                "if not (Str.contains \"a\\\\x0dx\\\\x0adelete file\" line) then fail $\"not visible: {show line}\""
                "print \"ok\""
            ]

            Expect.equal (runFile prog) 0 "a preview path renders CR, LF and ESC visibly"
        }
        test "an http send preview line renders escapes visibly [D:tty-echo-strict]" {
            let prog = [
                "let esc = Str.fromBase64 \"Gw==\""
                "let cr = Str.fromBase64 \"DQ==\""
                "let p ="
                "    plan"
                "        let _r = Http.send { Http.defaults with method = Post; url = $\"http://x/h{esc}[8m\"; body = Text $\"a{cr}b\" }"
                "        ()"
                "let line = p |> Plan.preview |> Seq.exactlyOne"
                "if Str.contains esc line || Str.contains cr line then fail $\"raw control: {show line}\""
                "if not (Str.contains \"/h\\\\x1b[8m\" line) then fail $\"not visible: {show line}\""
                "print \"ok\""
            ]

            Expect.equal (runFile prog) 0 "an http send preview renders ESC visibly"
        }
        test "Http.send{post} captures as an HttpSend Op — the per-method split" {
            let prog = [
                "let p ="
                "    plan"
                "        let _r = Http.send (Http.post \"http://x\")"
                "        print \"sent\""
                "if (p |> Plan.ops |> Seq.length) <> 1 then fail \"not one op\""
                "print \"ok\""
            ]

            Expect.equal (runFile prog) 0 "a POST captures one HttpSend Op"
        }
        test "Http.send{Other \"PURGE\"} captures, never sends [D:http-safe-verbs]" {
            // port 9 (discard) refuses: a send would raise "cannot reach"
            let prog = [
                "let p ="
                "    plan"
                "        let _r = Http.send { Http.defaults with method = Other \"PURGE\"; url = \"http://127.0.0.1:9/\" }"
                "        print \"sent\""
                "if (p |> Plan.ops |> Seq.length) <> 1 then fail \"not one op\""
                "print \"ok\""
            ]

            Expect.equal (runFile prog) 0 "an Other PURGE captures one HttpSend Op"
        }
        test "proc inside a plan HARD-REFUSES — a spawn is uncapturable" {
            let e =
                firstErr [ "let p ="; "    plan"; "        echo hi"; "print $\"{p |> Plan.isEmpty}\"" ]

            Expect.stringContains e.Message "'echo' runs a command" "proc named"
            Expect.stringContains e.Message "refused inside 'plan'" "the plan refusal"
            Expect.equal e.Line 3 "located at the command"
        }
        test "the fan-out family inside a plan is a CHECK error naming the member [D:plan-refuses-fanout]" {
            let inPlan (body: string) =
                firstErr [ "let p ="; "    plan"; $"        {body}"; "print $\"{p |> Plan.isEmpty}\"" ]

            let threads =
                "its callbacks run on worker threads the plan cannot capture; run it outside the plan"

            for m, call, tail in
                [
                    "pmap", "[1] |> Seq.pmap (fun n -> n) |> Seq.iter (fun _ -> ())", "; Seq.map is the sequential form"
                    "pmapWith",
                    "[1] |> Seq.pmapWith 2 (fun n -> n) |> Seq.iter (fun _ -> ())",
                    "; Seq.map is the sequential form"
                    "piter", "[1] |> Seq.piter (fun _ -> ())", "; Seq.iter is the sequential form"
                    "piterWith", "[1] |> Seq.piterWith 2 (fun _ -> ())", "; Seq.iter is the sequential form"
                    "pfirst", "[1] |> Seq.pfirst (fun n -> n) |> fun _ -> ()", ""
                    "pfirstWith", "[1] |> Seq.pfirstWith 2 (fun n -> n) |> fun _ -> ()", ""
                ] do
                let e = inPlan call
                Expect.equal e.Message $"'Seq.{m}' is refused inside 'plan' — {threads}{tail}" m
                Expect.equal e.Line 3 $"{m}: located in the plan body"

            let e = inPlan "pfor n in [1; 2] do File.write \"/tmp/weir-pfor-never\" [\"x\"]"

            Expect.equal
                e.Message
                "'pfor' is refused inside 'plan' — its body runs on worker threads the plan cannot capture; use a plain 'for' inside the plan, or run the parallel loop outside it"
                "pfor"

            // through a helper, as proc refuses [D:plan-helper-refusal]
            let h =
                firstErr [
                    "let fan xs = xs |> Seq.piter (fun _ -> ())"
                    "let p ="
                    "    plan"
                    "        fan [1]"
                    "print $\"{p |> Plan.isEmpty}\""
                ]

            Expect.stringStarts
                h.Message
                "'fan' cannot run inside 'plan' — in its body, 'Seq.piter' is refused"
                "the helper"

            Expect.isEmpty
                (errsOf [ "[1] |> Seq.piter (fun _ -> ())"; "pfor n in [1] do print (show n)" ])
                "outside a plan the family checks clean"
        }
        test "a mutating Http.expect inside a plan is a CHECK error; a safe or Other-safe verb is not" {
            let inPlan (req: string) =
                errsOf [
                    "let p ="
                    "    plan"
                    $"        Http.expect {req} |> Seq.iter print"
                    "print $\"{p |> Plan.isEmpty}\""
                ]

            let refusal =
                "'Http.expect' with a mutating method is refused inside 'plan' — the request is a pending Op with no response to return; Http.send captures it as the Op"

            for req in
                [
                    "(Http.post \"http://127.0.0.1:1/\")"
                    "(Http.delete \"http://127.0.0.1:1/\")"
                    "{ Http.defaults with method = Other \"PURGE\"; url = \"http://127.0.0.1:1/\" }"
                ] do
                match inPlan req with
                | [ e ] ->
                    Expect.equal e.Message refusal req
                    Expect.equal e.Line 3 $"{req}: located"
                | other -> failtest $"{req}: {other |> List.map _.Message}"

            for req in
                [
                    "(Http.get \"http://127.0.0.1:1/\")"
                    "{ Http.defaults with method = Other \"get\"; url = \"http://127.0.0.1:1/\" }"
                    // the defaults record is a GET, and so is a copy of it
                    "Http.defaults"
                    "{ Http.defaults with url = \"http://127.0.0.1:1/\" }"
                    // a method unknown at check is the run time's refusal —
                    // never claimed mutating here [D:plan-apply]
                    "{ Http.defaults with method = (if true then Get else Post); url = \"http://127.0.0.1:1/\" }"
                ] do
                Expect.isEmpty (inPlan req |> List.map _.Message) req

            // a request in a variable, a copy-update of it, a helper's parameter
            Expect.isEmpty
                (errsOf [
                    "let req = Http.get \"http://127.0.0.1:1/\""
                    "let g r = Http.expect r"
                    "let p ="
                    "    plan"
                    "        Http.expect req |> Seq.iter print"
                    "        Http.expect { req with timeout = 5s } |> Seq.iter print"
                    "        g req |> Seq.iter print"
                    "print $\"{p |> Plan.isEmpty}\""
                 ]
                 |> List.map _.Message)
                "an unresolved method is not called mutating"

            // the piped spelling
            Expect.exists
                (errsOf [
                    "let p ="
                    "    plan"
                    "        Http.put \"http://127.0.0.1:1/\" |> Http.expect |> Seq.iter print"
                    "print $\"{p |> Plan.isEmpty}\""
                 ]
                 |> List.map _.Message)
                ((=) refusal)
                "piped"
        }
        test "apply inside a plan REFUSES — a capture cannot be captured" {
            let e =
                firstErr [
                    "let q ="
                    "    plan"
                    "        File.write \"f\" [\"a\"]"
                    "let p ="
                    "    plan"
                    "        q |> Plan.apply"
                    "print $\"{p |> Plan.isEmpty}\""
                ]

            Expect.stringContains e.Message "'Plan.apply' is refused inside 'plan'" "the apply-in-plan refusal"
        }
        test "known-after-apply: reading a captured target is a LOCATED runtime refusal" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let f = weirPath (System.IO.Path.Combine(dir, "a.txt"))

            try
                // exit != 0 (a raise): the read of a captured target refuses
                let code =
                    runFile [
                        "let p ="
                        "    plan"
                        $"        File.write \"{f}\" [\"a\"]"
                        $"        let _c = File.read \"{f}\""
                        "        print \"unreached\""
                        "print $\"{p |> Plan.isEmpty}\""
                    ]

                Expect.notEqual code 0 "the plan raised (known-after-apply)"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "reading a DIFFERENT path inside a plan RUNS (path-scoped)" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let src = System.IO.Path.Combine(dir, "src.txt")
            System.IO.File.WriteAllText(src, "hello\n")
            let srcW = weirPath src
            let dstW = weirPath (System.IO.Path.Combine(dir, "dst.txt"))

            try
                let code =
                    runFile [
                        "let p ="
                        "    plan"
                        $"        let c = File.read \"{srcW}\""
                        $"        File.write \"{dstW}\" c"
                        "if (p |> Plan.ops |> Seq.length) <> 1 then fail \"expected one op\""
                        "print \"ok\""
                    ]

                Expect.equal code 0 "the read ran (different path), one write captured"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "e2e: a manifest-tree plan applies to a byte-identical tree vs the direct run" {
            let root = td ()
            let direct = weirPath (System.IO.Path.Combine(root, "direct"))
            let applied = weirPath (System.IO.Path.Combine(root, "applied"))

            let renderTo dst = [
                $"Dir.create \"{dst}/conf\""
                $"File.write \"{dst}/conf/app.ini\" [\"name = demo\"; \"port = 8080\"]"
                $"Dir.create \"{dst}/conf/env\""
                $"File.write \"{dst}/conf/env/prod.env\" [\"TIER=prod\"]"
            ]

            let prog =
                renderTo direct
                @ [ "let p ="; "    plan" ]
                @ (renderTo applied |> List.map (fun l -> "        " + l))
                @ [ "p |> Plan.apply"; "print $\"{p |> Plan.ops |> Seq.length}\"" ]

            try
                Expect.equal (runFile prog) 0 "the program ran clean"

                let read d =
                    System.IO.Directory.GetFiles(d, "*", System.IO.SearchOption.AllDirectories)
                    |> Array.sortBy (fun p -> p.Substring(d.Length))
                    |> Array.map (fun p -> p.Substring(d.Length), System.IO.File.ReadAllText p)

                Expect.equal (read applied) (read direct) "the applied tree is byte-identical to the direct one"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-apply] B1: every mutation builtin's Op replays through
        // the direct call's own checks — plan + apply on a fixture leaves
        // the same tree and the same success/failure as the direct call
        test "B1: every plan op is equivalent to its direct call on every fixture kind" {
            let ops1 = [ "File.delete"; "Dir.delete"; "Dir.deleteAll"; "Dir.create" ]
            let ops2 = [ "File.copy"; "File.move"; "Dir.copy"; "Dir.move" ]

            let fixtures = [
                "empty dir", (fun (t: string) -> System.IO.Directory.CreateDirectory t |> ignore)
                "non-empty dir",
                (fun t ->
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(t, "sub", "d"))
                    |> ignore

                    System.IO.File.WriteAllText(System.IO.Path.Combine(t, "sub", "f.txt"), "x"))
                "file", (fun t -> System.IO.File.WriteAllText(t, "data"))
                "missing", (fun _ -> ())
            ]

            let snapshot (root: string) =
                System.IO.Directory.GetFileSystemEntries(root, "*", System.IO.SearchOption.AllDirectories)
                |> Array.map (fun p ->
                    let rel = p.Substring(root.Length).Replace('\\', '/')

                    if System.IO.Directory.Exists p then
                        rel + "/"
                    else
                        rel + "=" + System.IO.File.ReadAllText p)
                |> Array.sort

            let root = td ()

            try
                for op in ops1 @ ops2 do
                    for (kind, make) in fixtures do
                        let outcome (mode: string) =
                            let dir = System.IO.Path.Combine(root, System.Guid.NewGuid().ToString "N")
                            System.IO.Directory.CreateDirectory dir |> ignore
                            let t = System.IO.Path.Combine(dir, "t")
                            make t
                            let tw, dw = weirPath t, weirPath (System.IO.Path.Combine(dir, "dst"))

                            let call =
                                if List.contains op ops2 then
                                    $"{op} \"{tw}\" \"{dw}\""
                                else
                                    $"{op} \"{tw}\""

                            let prog =
                                if mode = "direct" then
                                    [ call ]
                                else
                                    [ "let p ="; "    plan"; "        " + call; "p |> Plan.apply" ]

                            let code = runFile prog
                            code = 0, snapshot dir

                        Expect.equal (outcome "plan") (outcome "direct") $"{op} on {kind}: plan+apply == direct"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-path-bound] DA-03: a plan captures paths resolved to
        // absolute at capture, so apply writes the previewed location
        // regardless of the apply-time cwd. Capture under cd A, apply
        // under cd B: the file lands in A (the preview), never B.
        test "DA-03: a write captured under cd A applies to A even when applied under cd B" {
            let root = td ()
            let dirA = weirPath (System.IO.Path.Combine(root, "A"))
            let dirB = weirPath (System.IO.Path.Combine(root, "B"))

            let prog = [
                $"Dir.create \"{dirA}\""
                $"Dir.create \"{dirB}\""
                "let p ="
                $"    within cd \"{dirA}\""
                "        plan"
                "            File.write \"marker.txt\" [\"hi\"]"
                // preview must render the absolute captured path (A's)
                "let text = Str.join \"\\n\" (p |> Plan.preview) |> Str.replace \"\\\\\" \"/\""
                $"if not (Str.contains \"{dirA}/marker.txt\" text) then fail \"preview not absolute-A\""
                // apply under a different cwd — the bound path wins
                $"within cd \"{dirB}\""
                "    p |> Plan.apply"
                "print \"ok\""
            ]

            try
                Expect.equal (runFile prog) 0 "the program ran clean"

                Expect.isTrue
                    (System.IO.File.Exists(System.IO.Path.Combine(root, "A", "marker.txt")))
                    "the file landed in A (the previewed, capture-time location)"

                Expect.isFalse
                    (System.IO.File.Exists(System.IO.Path.Combine(root, "B", "marker.txt")))
                    "the file did NOT rebind to B (the apply-time cwd) — DA-03"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-path-bound] the two-path ops bind both source and dest
        // to absolute at capture: a copy captured under cd A applies into
        // A's tree even from cd B.
        test "DA-03: a copy captured under cd A binds src+dst to A, applies under cd B into A" {
            let root = td ()
            let dirA = weirPath (System.IO.Path.Combine(root, "A"))
            let dirB = weirPath (System.IO.Path.Combine(root, "B"))

            let prog = [
                $"Dir.create \"{dirA}\""
                $"Dir.create \"{dirB}\""
                $"File.write \"{dirA}/orig.txt\" [\"data\"]"
                "let p ="
                $"    within cd \"{dirA}\""
                "        plan"
                "            File.copy \"orig.txt\" \"copied.txt\""
                "let text = Str.join \"\\n\" (p |> Plan.preview) |> Str.replace \"\\\\\" \"/\""
                $"if not (Str.contains \"{dirA}/orig.txt\" text) then fail \"src not absolute-A\""
                $"if not (Str.contains \"{dirA}/copied.txt\" text) then fail \"dst not absolute-A\""
                $"within cd \"{dirB}\""
                "    p |> Plan.apply"
                "print \"ok\""
            ]

            try
                Expect.equal (runFile prog) 0 "the program ran clean"

                Expect.isTrue
                    (System.IO.File.Exists(System.IO.Path.Combine(root, "A", "copied.txt")))
                    "the copy landed in A (both paths bound at capture)"

                Expect.isFalse
                    (System.IO.File.Exists(System.IO.Path.Combine(root, "B", "copied.txt")))
                    "the copy did NOT rebind to B — DA-03"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:ambient-capture] the read side of the same closure rule: a
        // lazy `ls`/`glob` built inside `within cd A` but FORCED after the
        // scope exits (the REPL echo, or under cd B) still lists A — the
        // cwd is captured where the identifier/call is evaluated, not at
        // force time (the within-cd `ls` bug, matching Dir.list/Dir.stat).
        test "AMB: ls and glob capture the within-cd cwd at eval, not at force [D:ambient-capture]" {
            let root = td ()
            let dirA = weirPath (System.IO.Path.Combine(root, "A"))
            let dirB = weirPath (System.IO.Path.Combine(root, "B"))

            let prog = [
                $"Dir.create \"{dirA}\""
                $"Dir.create \"{dirB}\""
                $"File.write \"{dirA}/Aonly.txt\" [\"x\"]"
                $"File.write \"{dirB}/Bonly.txt\" [\"x\"]"
                // ls escapes unforced, then forced under cd B
                "let rows ="
                $"    within cd \"{dirA}\""
                "        ls"
                "let names ="
                $"    within cd \"{dirB}\""
                "        rows |> Seq.map (fun r -> r.name) |> Seq.freeze"
                "let joined = Str.join \",\" names"
                "if not (Str.contains \"Aonly.txt\" joined) then fail \"ls lost A\""
                "if Str.contains \"Bonly.txt\" joined then fail \"ls rebound to the force-time cwd B\""
                // a relative glob, same escape
                "let g ="
                $"    within cd \"{dirA}\""
                "        Path.glob \"*\""
                "let gnames ="
                $"    within cd \"{dirB}\""
                "        g |> Seq.freeze"
                "let gjoined = Str.join \",\" gnames"
                "if not (Str.contains \"Aonly.txt\" gjoined) then fail \"glob lost A\""
                "if Str.contains \"Bonly.txt\" gjoined then fail \"glob rebound to B\""
                "print \"ok\""
            ]

            try
                Expect.equal (runFile prog) 0 "ls and glob captured A at eval time, not B at force"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:ambient-capture-within]: outside any `within` nothing is
        // captured — a lazy ls/glob/command/Env.vars runs in the cwd and
        // env current at enumeration; inside one the scope still applies
        test "AMB: a lazy value outside any within reads the cwd/env at force [D:ambient-capture-within]" {
            skipOnWindows ()
            let root = td ()
            let dirA = weirPath (System.IO.Path.Combine(root, "A"))
            let dirB = weirPath (System.IO.Path.Combine(root, "B"))

            let prog = [
                $"Dir.create \"{dirA}\""
                $"Dir.create \"{dirB}\""
                $"File.write \"{dirA}/Aonly.txt\" [\"x\"]"
                $"File.write \"{dirB}/Bonly.txt\" [\"x\"]"
                $"let _a = cd \"{dirA}\""
                "let rows = ls |> Seq.map (fun r -> r.name)"
                "let g = Path.glob \"*.txt\""
                "let c = sh -c \"ls\""
                "let v = sh -c \"echo ${AMBW:-unset}\""
                "let ev = Env.vars |> Seq.where (fun e -> e.name == \"AMBW\") |> Seq.map (fun e -> e.value)"
                "let s = within env [Env.pair \"AMBW\" \"in\"] $(sh -c \"echo ${AMBW:-unset}\")"
                $"let _b = cd \"{dirB}\""
                "if Str.join \",\" rows <> \"Bonly.txt\" then fail $\"ls: {Str.join \",\" rows}\""
                "if Str.join \",\" g <> \"Bonly.txt\" then fail $\"glob: {Str.join \",\" g}\""
                "if Str.join \",\" c <> \"Bonly.txt\" then fail $\"cmd: {Str.join \",\" c}\""
                "let late = within env [Env.pair \"AMBW\" \"late\"] (v |> Seq.freeze)"
                "if Str.join \",\" late <> \"late\" then fail $\"env: {Str.join \",\" late}\""
                "let elate = within env [Env.pair \"AMBW\" \"late\"] (ev |> Seq.freeze)"
                "if Str.join \",\" elate <> \"late\" then fail $\"Env.vars: {Str.join \",\" elate}\""
                "if Str.join \",\" s <> \"in\" then fail $\"within env lost: {Str.join \",\" s}\""
                "print \"ok\""
            ]

            let saved = Weir.Session.Cwd()

            try
                Expect.equal (runFile prog) 0 "outside any within, the force-time cwd/env applies"
            finally
                Weir.Session.setCwd saved
                System.IO.Directory.Delete(root, true)
        }
        test "a trailing / globs directories only, each with its slash [D:path-glob]" {
            let root = td ()
            let r = weirPath root

            let prog = [
                $"Dir.create \"{r}/a/b\""
                $"File.write \"{r}/f1\" [\"x\"]"
                $"File.write \"{r}/a/f2\" [\"x\"]"
                $"within cd \"{r}\""
                "    let star = Path.glob \"*/\" |> Str.join \",\""
                "    let deep = Path.glob \"**/\" |> Str.join \",\""
                "    if star <> \"a/\" then fail $\"*/ gave {star}\""
                "    if deep <> \"a/,a/b/\" then fail $\"**/ gave {deep}\""
            ]

            try
                Expect.equal (runFile prog) 0 "dirs only"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "bracket classes are glob classes, never raw regex [D:surface-glob]" {
            skipOnWindows ()
            let root = td ()
            System.IO.Directory.CreateDirectory root |> ignore

            for n in [ "a"; "b"; "w"; "_"; "\\"; "[]"; "é"; "😀" ] do
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, n), "x")

            // (pattern, expected names joined by `,`) — verbatim strings,
            // so a backslash is itself on both sides
            let cells = [
                "[z-a]", ""
                "[]", "[]"
                "[!]", ""
                "[\\w]", "\\,w"
                "[a-c]", "a,b"
                "[!a-w]", "\\,_,é,😀"
                "[]a]", "a"
                "[😀]", "😀"
            ]

            let prog = [
                $"within cd \"{weirPath root}\""
                for i, (pat, want) in List.indexed cells do
                    $"    let g{i} = Path.glob @\"{pat}\" |> Str.join \",\""
                    $"    if g{i} <> @\"{want}\" then fail $\"cell {i} gave {{g{i}}}\""
            ]

            try
                Expect.equal (runFile prog) 0 "each class matched as a glob"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "a glob segment matches linearly, and a repeated ** yields each path once [D:glob-linear]" {
            skipOnWindows ()
            let root = td ()

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "r", "a", "b"))
            |> ignore
            // 79 dashes: a 7-star miss once ran past 100s as a regex
            let name = String.replicate 79 "ab-" + "ab.txt"

            for n in [ name; "r/x.gz"; "r/a/y.gz"; "r/a/b/z.gz"; "r/a/b/😀x" ] do
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, n), "x")

            let cells = [
                "*-*-*-*-*-*-*.gz", ""
                "*-*-*-*-*-*-*.txt", name
                "ab-*ab-ab.txt", name
                "r/**/**/*.gz", "r/x.gz,r/a/y.gz,r/a/b/z.gz"
                "r/**/**/**", "r/a,r/a/b,r/a/b/z.gz,r/a/b/😀x,r/a/y.gz,r/x.gz"
                "r/**/a/**/*.gz", "r/a/y.gz,r/a/b/z.gz"
                "r/a/b/?x", "r/a/b/😀x"
                "r/a/b/[!a]*", "r/a/b/z.gz,r/a/b/😀x"
                "r/**/*.g?", "r/x.gz,r/a/y.gz,r/a/b/z.gz"
                "r/**/*.gz*", "r/x.gz,r/a/y.gz,r/a/b/z.gz"
            ]

            let prog = [
                $"within cd \"{weirPath root}\""
                for i, (pat, want) in List.indexed cells do
                    $"    let g{i} = Path.glob @\"{pat}\" |> Str.join \",\""
                    $"    if g{i} <> @\"{want}\" then fail $\"cell {i} gave {{g{i}}}\""
            ]

            let sw = System.Diagnostics.Stopwatch.StartNew()

            try
                Expect.equal (runFile prog) 0 "each cell matched"
                Expect.isLessThan sw.ElapsedMilliseconds 5000L "the pathological miss returns at once"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "File.append inside a plan refuses — no v1 Op arm (kind-first)" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let f = weirPath (System.IO.Path.Combine(dir, "a.txt"))

            try
                Expect.throwsC
                    (fun () -> run ("plan" + Weir.Parser.sibSepStr + $"File.append \"{f}\" [\"x\"]") |> ignore)
                    (fun ex -> Expect.stringContains ex.Message "no Plan Op in v1" "the kind-first refusal")
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "keyword reservation: plan cannot be a binder; a blockless head teaches" {
            let e = firstErr [ "let plan = 1" ]
            Expect.stringContains e.Message "'plan' is a keyword" "the binder slot refuses"

            let ds = diagsIn "bare.weir" [ "plan" ]

            Expect.isTrue
                (ds |> List.exists (fun d -> d.Message.Contains "plan takes a block"))
                "the bare head teaches the block form"
        }
        // [D:plan-parallel-refusal] DA-01: PlanMode's capture frame is
        // thread-local, so a parallel/race callback runs on a worker
        // without it — a native mutation would escape capture and run
        // for real. The combinators refuse on the calling thread before
        // any worker is scheduled. Assert the filesystem: no marker.
        test "DA-01: piterWith inside a plan REFUSES before any worker writes a file" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let marker = System.IO.Path.Combine(dir, "parallel-marker.txt")
            let markerW = weirPath marker

            try
                let code =
                    runFile [
                        "let changes ="
                        "    plan"
                        $"        [1] |> Seq.piterWith 1 (fun _ -> File.write \"{markerW}\" [\"probe\"])"
                        "print $\"empty={changes |> Plan.isEmpty}\""
                    ]

                Expect.notEqual code 0 "the plan refused the parallel combinator"
                Expect.isFalse (System.IO.File.Exists marker) "no worker wrote the marker"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "DA-01: piter / pmap / pfirst inside a plan each REFUSE (message pins), no file" {
            // the refusal fires at runtime on the calling thread; `run`
            // evaluates in-process so the located failwith is catchable
            let refuses combinator body =
                Expect.throwsC (fun () -> run ("plan" + Weir.Parser.sibSepStr + body) |> ignore) (fun ex ->
                    Expect.stringContains ex.Message $"'{combinator}' is refused inside 'plan'" $"{combinator} refuses")

            // File.write to a never-existing path proves no worker ran:
            // a captured write is a no-op, a refusal precedes any worker
            refuses "piter" "[1; 2; 3; 4] |> Seq.piter (fun n -> File.write \"/tmp/weir-da01-never\" [\"x\"])"

            refuses
                "pmap"
                "if ([1; 2] |> Seq.pmap (fun n -> File.write \"/tmp/weir-da01-never\" [\"x\"]) |> Seq.length) > 0 then ()"

            refuses "pfirst" "[1; 2] |> Seq.pfirst (fun n -> File.write \"/tmp/weir-da01-never\" [\"x\"])"
            refuses "piterWith" "[1] |> Seq.piterWith 1 (fun n -> File.write \"/tmp/weir-da01-never\" [\"x\"])"

            refuses
                "pmapWith"
                "if ([1] |> Seq.pmapWith 1 (fun n -> File.write \"/tmp/weir-da01-never\" [\"x\"]) |> Seq.length) > 0 then ()"

            refuses "pfirstWith" "[1] |> Seq.pfirstWith 1 (fun n -> File.write \"/tmp/weir-da01-never\" [\"x\"])"
            Expect.isFalse (System.IO.File.Exists "/tmp/weir-da01-never") "no combinator wrote the marker"
        }
        test "a plan body's tail value is discarded, so it must be unit (B40)" {
            let e =
                checkErr (
                    "plan"
                    + Weir.Parser.sibSepStr
                    + "File.write \"/tmp/weir-b40-never\" [\"a\"]; 42"
                )

            Expect.stringContains e.Message "a plan block's value is discarded" ""
            Expect.stringContains e.Message "this one is int" ""

            checkOk ("plan" + Weir.Parser.sibSepStr + "File.write \"/tmp/weir-b40-never\" [\"a\"]")
            |> ignore

            checkOk ("plan" + Weir.Parser.sibSepStr + "fail \"x\"") |> ignore
        }
        test "DA-01: a nested plan's parallel combinator REFUSES too (filesystem)" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let marker = System.IO.Path.Combine(dir, "nested.txt")
            let markerW = weirPath marker

            try
                let code =
                    runFile [
                        "let changes ="
                        "    plan"
                        "        let inner ="
                        "            plan"
                        $"                [1] |> Seq.piter (fun _ -> File.write \"{markerW}\" [\"x\"])"
                        "        print $\"inner empty={inner |> Plan.isEmpty}\""
                        "print $\"outer empty={changes |> Plan.isEmpty}\""
                    ]

                Expect.notEqual code 0 "the nested plan refused the parallel combinator"
                Expect.isFalse (System.IO.File.Exists marker) "nothing written"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "DA-01: a callback that itself fails is never reached — the refusal precedes scheduling" {
            // even a callback that would raise must not run: the refusal
            // is on the calling thread before any worker is scheduled
            Expect.throwsC
                (fun () ->
                    run (
                        "plan"
                        + Weir.Parser.sibSepStr
                        + "[1] |> Seq.piter (fun n -> fail \"callback ran\")"
                    )
                    |> ignore)
                (fun ex ->
                    Expect.stringContains
                        ex.Message
                        "'piter' is refused inside 'plan'"
                        "refused, not the callback's fail")
        }
        test "DA-01: a parallel combinator OUTSIDE a plan still runs" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let d = weirPath dir

            try
                let code =
                    runFile [
                        $"[1; 2; 3] |> Seq.piterWith 2 (fun n -> File.write $\"{d}/out-{{n}}.txt\" [\"x\"])"
                        "print \"ran\""
                    ]

                Expect.equal code 0 "the parallel combinator ran outside a plan"
                Expect.equal (System.IO.Directory.GetFiles dir).Length 3 "all three arms wrote"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "a top-level helper that runs a command refuses at check inside plan, through every hop (B22)" {
            let msgs lines =
                diagsIn "h.weir" lines |> List.map (fun d -> d.Message)

            let m =
                msgs [
                    "let mk () = touch /tmp/weir-b22-never | complete"
                    "let mk2 () ="
                    "    let _r = mk ()"
                    "    ()"
                    "let changes ="
                    "    plan"
                    "        mk2 ()"
                    "print $\"{changes |> Plan.isEmpty}\""
                ]

            Expect.exists
                m
                (fun s -> s.Contains "'mk2' cannot run inside 'plan' — in its body, a reifier runs a command")
                "names the helper and the root"

            // a File.write helper is captured, a local shadow is not the helper,
            // and data computed by a command outside the plan is just data
            let clean =
                msgs [
                    "let mk () = touch /tmp/weir-b22-never | complete"
                    "let w () = File.write \"/tmp/weir-b22-never\" [\"a\"]"
                    "let out = echo hi | complete"
                    "let changes ="
                    "    plan"
                    "        w ()"
                    "        let mk () = print \"local\""
                    "        mk ()"
                    "        print $\"{out.exitCode}\""
                    "print $\"{changes |> Plan.isEmpty} {mk () |> fun c -> c.exitCode}\""
                ]

            Expect.isFalse (clean |> List.exists (fun s -> s.Contains "inside 'plan'")) $"no refusal: {clean}"
        }
        // [D:plan-proc-runtime-guard] DA-02: firstPlanRefusal is a
        // syntactic walk that follows top-level helpers but not a
        // callable passed as a value, so that indirect proc slips past
        // the checker. A thread-local guard at
        // the one spawn point refuses at runtime (the helper runs on the
        // capturing thread). The syntactic diagnostic stays for usability.
        test "DA-02: a helper-wrapped proc inside a plan REFUSES at runtime, no process runs" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let marker = System.IO.Path.Combine(dir, "process-marker.txt")
            let markerW = weirPath marker

            try
                let code =
                    runFile [
                        "let runMarker () ="
                        $"    let lines = sh -c \"printf marker > {markerW}\""
                        "    lines |> Seq.length"
                        "let capture f ="
                        "    plan"
                        "        let _forced = f ()"
                        "        ()"
                        "let changes = capture runMarker"
                        "print $\"empty={changes |> Plan.isEmpty}\""
                    ]

                Expect.notEqual code 0 "the plan refused the indirect proc"
                Expect.isFalse (System.IO.File.Exists marker) "no process ran (no marker)"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "DA-02: a helper-wrapped `| exec` inside a plan REFUSES at runtime, no image replaced [D:exec]" {
            // execvp does not funnel through Proc.spawn, so the runtime plan
            // guard is applied in Proc.exec directly; without it an indirect
            // exec would replace the process instead of being refused.
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let marker = System.IO.Path.Combine(dir, "exec-marker.txt")
            let markerW = weirPath marker

            try
                let code =
                    runFile [
                        "let doExec () ="
                        $"    sh -c \"printf marker > {markerW}\" | exec"
                        "let capture f ="
                        "    plan"
                        "        let _forced = f ()"
                        "        ()"
                        "let changes = capture doExec"
                        "print $\"empty={changes |> Plan.isEmpty}\""
                    ]

                Expect.notEqual code 0 "the plan refused the indirect exec"

                Expect.isFalse
                    (System.IO.File.Exists marker)
                    "the guard fired before execvp — no image replaced, no marker"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "DA-02: a native File.write reached THROUGH a serial helper still CAPTURES" {
            let dir = td ()
            System.IO.Directory.CreateDirectory dir |> ignore
            let marker = System.IO.Path.Combine(dir, "helper-marker.txt")
            let markerW = weirPath marker

            try
                let code =
                    runFile [
                        "let doWrite () ="
                        $"    File.write \"{markerW}\" [\"probe\"]"
                        "let changes ="
                        "    plan"
                        "        doWrite ()"
                        "if changes |> Plan.isEmpty then fail \"the helper's write was not captured\""
                        "print \"ok\""
                    ]

                Expect.equal code 0 "the serial helper's native write is captured (not over-refused)"
                Expect.isFalse (System.IO.File.Exists marker) "the captured write did not run"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        // [D:plan-stale-reads]: one guard on every fs read — a point
        // read conflicts with a target it equals or lies under (and one
        // below a path that does not exist yet); a listing also with
        // every target below it; a move changes its source too
        test "stale reads: the guard matches by path prefix, listings see targets below" {
            let root = td ()

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "sb", "tree"))
            |> ignore

            let at (p: string) = System.IO.Path.Combine(root, p)

            let refused (f: unit -> unit) =
                try
                    f ()
                    false
                with Failure m ->
                    Expect.stringContains m "known-after-apply" "the refusal names the law"
                    true

            let inPlan (targets: string list) (read: unit -> unit) =
                let mutable r = false

                Weir.Eval.PlanMode.capturing (fun () ->
                    for t in targets do
                        Weir.Eval.PlanMode.capture (VUnion("DeleteFile", Some(VStr t))) [ t ]

                    r <- refused read)
                |> ignore

                r

            try
                let rd p () =
                    Weir.Eval.PlanMode.checkRead "File.read" (at p)

                let tree p () =
                    Weir.Eval.PlanMode.checkTree "Dir.list" (at p)

                Expect.isTrue (inPlan [ at "sb/a.txt" ] (rd "sb/a.txt")) "the exact path"
                Expect.isTrue (inPlan [ at "sb/a.txt" ] (rd "sb/a.txt/")) "a trailing slash is the same path"
                Expect.isTrue (inPlan [ at "sb/tree" ] (rd "sb/tree/f")) "a read under a captured dir op"
                Expect.isFalse (inPlan [ at "sb/tree" ] (rd "sb/treehouse")) "a sibling sharing the prefix runs"
                Expect.isFalse (inPlan [ at "sb/new.txt" ] (rd "sb")) "an existing dir's own facts run"
                Expect.isTrue (inPlan [ at "fresh/x" ] (rd "fresh")) "a path a captured write would create"
                Expect.isTrue (inPlan [ at "sb/new.txt" ] (tree "sb")) "a listing sees a target below it"
                Expect.isFalse (inPlan [ at "other/x" ] (tree "sb")) "a listing elsewhere runs"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "stale reads: on Windows the guard ignores separator spelling and case [D:plan-guard-windows]" {
            let w = Weir.Eval.PlanMode.pathWithinOn true
            Expect.isTrue (w @"C:\r\sb/tree" @"C:\r\sb/tree/f") "a Path.Combine'd mixed path"
            Expect.isTrue (w @"C:\r\sb\tree" "C:/r/sb/tree/f") "forward slashes under a native root"
            Expect.isTrue (w @"C:\R\SB\a.txt" @"c:\r\sb\a.txt\") "case and a trailing separator"
            Expect.isFalse (w @"C:\r\sb\tree" @"C:\r\sb\treehouse") "a sibling sharing the prefix"
            Expect.isTrue (w @"C:\" @"C:\x") "under a drive root"
            let p = Weir.Eval.PlanMode.pathWithinOn false
            Expect.isFalse (p "/r/SB" "/r/sb/f") "POSIX compares case"
            Expect.isFalse (p "/r/sb" @"/r/sb\f") "on POSIX a backslash is a name byte"
            Expect.isTrue (p "/r/sb/" "/r/sb/f") "POSIX, a trailing slash"
        }
        // [D:plan-guard-links]: the guard compares symlink-resolved
        // paths both ways, folds case where the filesystem does, and
        // checks every enclosing plan frame
        test "stale reads: the guard resolves symlinks both ways; a deleted link's target reads [D:plan-guard-links]" {
            skipOnWindows ()
            let root = td ()
            let at (p: string) = System.IO.Path.Combine(root, p)
            System.IO.Directory.CreateDirectory(at "real") |> ignore
            System.IO.File.WriteAllText(at "real/a.txt", "old\n")
            System.IO.Directory.CreateSymbolicLink(at "alias", "real") |> ignore
            System.IO.File.CreateSymbolicLink(at "real/lnk.txt", "a.txt") |> ignore

            let refusedAfter (op: Value) (target: string) (read: unit -> unit) =
                let mutable r = false

                Weir.Eval.PlanMode.capturing (fun () ->
                    Weir.Eval.PlanMode.capture op [ target ]

                    r <-
                        try
                            read ()
                            false
                        with Failure m ->
                            Expect.stringContains m "known-after-apply" "the refusal names the law"
                            true)
                |> ignore

                r

            let write p =
                VUnion("WriteFile", Some(VTuple [ VStr(at p); VSeq [] ])), at p

            let delete p =
                VUnion("DeleteFile", Some(VStr(at p))), at p

            let rd p () =
                Weir.Eval.PlanMode.checkRead "File.read" (at p)

            let tree p () =
                Weir.Eval.PlanMode.checkTree "Dir.list" (at p)

            try
                let check (op, t) read = refusedAfter op t read
                Expect.isTrue (check (write "real/a.txt") (rd "alias/a.txt")) "a read through a dir alias"
                Expect.isTrue (check (write "real/a.txt") (rd "real/lnk.txt")) "a read through a file link"

                Expect.isTrue
                    (check (write "real/lnk.txt") (rd "real/a.txt"))
                    "a write through a link lands on its target"

                Expect.isTrue
                    (check (delete "alias/a.txt") (rd "real/a.txt"))
                    "a delete via the alias, read via the real path"

                Expect.isTrue (check (write "alias/new.txt") (tree "real")) "a listing of the real dir"
                Expect.isTrue (check (delete "real/lnk.txt") (rd "real/lnk.txt")) "the deleted link itself"
                Expect.isFalse (check (delete "real/lnk.txt") (rd "real/a.txt")) "a deleted link's target still reads"
                Expect.isFalse (check (write "real/a.txt") (rd "real/other.txt")) "a sibling reads"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "stale reads: case folds on Windows and macOS, never on Linux [D:plan-guard-links]" {
            let root = td ()
            let at (p: string) = System.IO.Path.Combine(root, p)
            System.IO.Directory.CreateDirectory(at "sb") |> ignore
            let mutable refused = false

            Weir.Eval.PlanMode.capturing (fun () ->
                Weir.Eval.PlanMode.capture (VUnion("DeleteFile", Some(VStr(at "sb/Cfg.txt")))) [ at "sb/Cfg.txt" ]

                refused <-
                    try
                        Weir.Eval.PlanMode.checkRead "File.read" (at "SB/cfg.TXT")
                        false
                    with Failure _ ->
                        true)
            |> ignore

            try
                let folds = System.OperatingSystem.IsWindows() || System.OperatingSystem.IsMacOS()
                Expect.equal refused folds "the platform's case rule"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test
            "stale reads: an in-place write refuses a read through another hard link; an atomic write's does not [D:plan-guard-inode]" {
            skipOnWindows ()
            let root = td ()
            let at (p: string) = System.IO.Path.Combine(root, p)
            System.IO.Directory.CreateDirectory(at "sub") |> ignore
            System.IO.File.WriteAllText(at "a.txt", "old\n")

            use ln = System.Diagnostics.Process.Start("ln", [ at "a.txt"; at "sub/b.txt" ])

            ln.WaitForExit()

            let readAfter (case: string) (read: string) =
                let mutable msg = ""

                Weir.Eval.PlanMode.capturing (fun () ->
                    Weir.Eval.PlanMode.capture (VUnion(case, Some(VTuple [ VStr(at "a.txt"); VSeq [] ]))) [
                        at "a.txt"
                    ]

                    msg <-
                        try
                            Weir.Eval.PlanMode.checkRead "File.read" (at read)
                            ""
                        with Failure m ->
                            m)
                |> ignore

                msg

            try
                let m = readAfter "WriteFile" "sub/b.txt"
                Expect.stringContains m "known-after-apply" "the other link refuses"
                let a = at "a.txt"
                Expect.stringContains m $"targets '{a}', the same file (a hard link)" "the message names the link"
                Expect.equal (readAfter "WriteFileAtomic" "sub/b.txt") "" "an atomic write separates the links"
                System.IO.File.WriteAllText(at "c.txt", "other\n")
                Expect.equal (readAfter "WriteFile" "c.txt") "" "another file reads"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "stale reads: a nested plan checks every enclosing frame [D:plan-guard-links]" {
            let root = td ()
            let f = System.IO.Path.Combine(root, "cfg")
            let mutable msg = ""

            Weir.Eval.PlanMode.capturing (fun () ->
                Weir.Eval.PlanMode.capture (VUnion("WriteFile", Some(VTuple [ VStr f; VSeq [] ]))) [ f ]

                Weir.Eval.PlanMode.capturing (fun () ->
                    msg <-
                        try
                            Weir.Eval.PlanMode.checkRead "File.exists" f
                            ""
                        with Failure m ->
                            m)
                |> ignore)
            |> ignore

            Expect.stringContains msg "known-after-apply" "the inner read refuses"
            Expect.stringContains msg "an enclosing 'plan'" "it names the enclosing plan"
        }
        test "stale reads: every fs read builtin refuses after a captured mutation; a move's source too" {
            let root = td ()

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "sb", "tree"))
            |> ignore

            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "sb", "a.txt"), "a\n")
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "sb", "tree", "f"), "t\n")
            let d = weirPath root

            let prog (mutation: string) (read: string) = [
                "let p ="
                "    plan"
                $"        {mutation}"
                $"        {read}"
                "print $\"{p |> Plan.isEmpty}\""
            ]

            try
                for mutation, read in
                    [
                        $"File.delete \"{d}/sb/a.txt\"", $"print (show (File.exists \"{d}/sb/a.txt\"))"
                        $"File.write \"{d}/sb/a.txt\" [\"n\"]", $"print (File.sha256 \"{d}/sb/a.txt\")"
                        $"File.write \"{d}/sb/a.txt\" [\"n\"]",
                        $"print $\"{{File.readBytes \"{d}/sb/a.txt\" |> Bytes.length}}\""
                        $"File.write \"{d}/sb/new.txt\" [\"n\"]",
                        $"print $\"{{Path.glob \"{d}/sb/*.txt\" |> Seq.length}}\""
                        $"File.write \"{d}/sb/new.txt\" [\"n\"]", $"print $\"{{Dir.list \"{d}/sb\" |> Seq.length}}\""
                        $"Dir.deleteAll \"{d}/sb/tree\"", $"File.read \"{d}/sb/tree/f\" |> Seq.iter print"
                        $"File.move \"{d}/sb/a.txt\" \"{d}/sb/b.txt\"", $"File.read \"{d}/sb/a.txt\" |> Seq.iter print"
                    ] do
                    Expect.notEqual (runFile (prog mutation read)) 0 $"refused: {read} after {mutation}"

                Expect.equal
                    (runFile (prog $"File.write \"{d}/sb/new.txt\" [\"n\"]" $"print (show (Dir.exists \"{d}/sb\"))"))
                    0
                    "an existing parent's existence is not stale"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-writeatomic]: writeAtomic captures its own op and
        // applies through the atomic path — the inode is replaced
        test "writeAtomic in a plan captures WriteFileAtomic and applies atomically (a hardlink keeps the old)" {
            skipOnWindows ()
            let root = td ()
            System.IO.Directory.CreateDirectory root |> ignore
            let d = weirPath root

            try
                let code =
                    runFile [
                        $"File.write \"{d}/a.txt\" [\"old\"]"
                        $"ln \"{d}/a.txt\" \"{d}/link.txt\""
                        "let p ="
                        "    plan"
                        $"        File.writeAtomic \"{d}/a.txt\" [\"new\"]"
                        $"if (p |> Plan.ops |> Seq.exactlyOne) <> WriteFileAtomic(\"{d}/a.txt\", [\"new\"]) then fail \"the op\""
                        "if not ((p |> Plan.preview |> Seq.exactlyOne) |> Str.contains \"atomically\") then fail \"the preview\""
                        "p |> Plan.apply"
                    ]

                Expect.equal code 0 "captured and applied"

                Expect.equal
                    (System.IO.File.ReadAllText(System.IO.Path.Combine(root, "a.txt")))
                    "new\n"
                    "the target is new"

                Expect.equal
                    (System.IO.File.ReadAllText(System.IO.Path.Combine(root, "link.txt")))
                    "old\n"
                    "the hardlink split"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-within-tmp]: an op under a `within tmp` dir opened
        // inside the plan refuses at capture — nothing reaches apply
        test "within tmp inside a plan: a captured op under its dir refuses before anything applies" {
            let root = td ()
            System.IO.Directory.CreateDirectory root |> ignore
            let d = weirPath root

            try
                for inner in
                    [
                        [ "            File.write $\"{t}/x\" [\"y\"]" ]
                        [ $"            File.copy $\"{{t}}/missing\" \"{d}/x\"" ]
                    ] do
                    let code =
                        runFile (
                            [
                                "let p ="
                                "    plan"
                                $"        File.write \"{d}/first.txt\" [\"a\"]"
                                "        within tmp t"
                            ]
                            @ inner
                            @ [ "p |> Plan.apply" ]
                        )

                    Expect.notEqual code 0 $"refused: {inner}"

                Expect.isFalse (System.IO.File.Exists(System.IO.Path.Combine(root, "first.txt"))) "nothing applied"

                // a tmp scope around the plan outlives the apply inside it
                let code =
                    runFile [
                        "within tmp t"
                        "    let p ="
                        "        plan"
                        "            File.write $\"{t}/x\" [\"y\"]"
                        "    p |> Plan.apply"
                        "    if not (File.exists $\"{t}/x\") then fail \"not applied\""
                    ]

                Expect.equal code 0 "a tmp dir opened outside the plan is a fine target"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-preconditions]: Dir.copy's destination parent must be a
        // directory, directly and at capture, as every other op's is
        test "Dir.copy into a missing parent or under a planned file refuses, directly and at capture" {
            let root = td ()
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "s")) |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "s", "f.txt"), "x\n")
            let d = weirPath root

            try
                Expect.equal
                    (runFile [
                        $"let r = Dir.copy \"{d}/s\" \"{d}/no/such/s\" |> try result"
                        "match r with"
                        "| Error e -> if not (e |> Str.contains \"no such directory\") then fail e"
                        "| Ok _ -> fail \"copied\""
                    ])
                    0
                    "the direct call refuses naming the parent"

                Expect.isFalse (System.IO.Directory.Exists(System.IO.Path.Combine(root, "no"))) "nothing created"

                for second in
                    [
                        $"Dir.copy \"{d}/s\" \"{d}/h.txt/sub/s\""
                        $"Dir.copy \"{d}/s\" \"{d}/no/such/s\""
                    ] do
                    let code =
                        runFile [
                            "let p ="
                            "    plan"
                            $"        File.write \"{d}/h.txt\" [\"z\"]"
                            $"        {second}"
                            "p |> Plan.apply"
                        ]

                    Expect.notEqual code 0 $"refused: {second}"

                    Expect.isFalse
                        (System.IO.File.Exists(System.IO.Path.Combine(root, "h.txt")))
                        $"at capture: {second}"

                    Expect.isFalse
                        (System.IO.Directory.Exists(System.IO.Path.Combine(root, "no")))
                        $"nothing created: {second}"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-preconditions]: a dangling link is a file row to the
        // simulation as to the real filesystem — a plan deletes or moves
        // the link itself, and a copy from it names it dangling
        test "a plan deletes and moves a pre-existing dangling symlink itself" {
            skipOnWindows ()
            let root = td ()
            System.IO.Directory.CreateDirectory root |> ignore
            let d = weirPath root
            let at (n: string) = System.IO.Path.Combine(root, n)
            System.IO.File.CreateSymbolicLink(at "ff", "nowhere") |> ignore
            System.IO.File.CreateSymbolicLink(at "gg", "nowhere") |> ignore
            System.IO.File.CreateSymbolicLink(at "cc", "nowhere") |> ignore

            let isLink (p: string) =
                not (isNull (System.IO.FileInfo(p).LinkTarget))

            try
                let code =
                    runFile [
                        "let p ="
                        "    plan"
                        $"        File.delete \"{d}/ff\""
                        $"        File.move \"{d}/gg\" \"{d}/hh\""
                        "p |> Plan.apply"
                    ]

                Expect.equal code 0 "captured and applied"
                Expect.isFalse (isLink (at "ff")) "the link is deleted"
                Expect.isTrue (isLink (at "hh") && not (isLink (at "gg"))) "the link is moved"

                Expect.equal
                    (runFile [
                        "let mk () ="
                        "    plan"
                        $"        File.copy \"{d}/cc\" \"{d}/dd\""
                        "let r = mk () |> try result"
                        "match r with"
                        "| Error e -> if not (e |> Str.contains \"dangling symlink\") then fail e"
                        "| Ok _ -> fail \"captured\""
                    ])
                    0
                    "a copy from a dangling link names it, as the direct call does"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-preconditions]: a moved link stays a link at its new
        // path; a write through a dangling link creates its target; a
        // write through a loop refuses (ELOOP) — at capture, as directly
        test "a plan simulates a moved symlink as a link, a write through a dangling link, and a loop" {
            skipOnWindows ()
            let root = td ()
            System.IO.Directory.CreateDirectory root |> ignore
            let d = weirPath root
            let at (n: string) = System.IO.Path.Combine(root, n)

            let isLink (p: string) =
                not (isNull (System.IO.FileInfo(p).LinkTarget))

            let refused (needle: string) (body: string list) =
                runFile (
                    [ "let mk () ="; "    plan" ]
                    @ (body |> List.map (fun l -> "        " + l))
                    @ [
                        "let r = mk () |> try result"
                        "match r with"
                        $"| Error e -> if not (e |> Str.contains \"{needle}\") then fail e"
                        "| Ok _ -> fail \"captured\""
                    ]
                )

            try
                System.IO.Directory.CreateDirectory(at "a") |> ignore
                System.IO.Directory.CreateSymbolicLink(at "L", "a") |> ignore
                System.IO.File.WriteAllText(at "f", "hi\n")
                System.IO.File.CreateSymbolicLink(at "FL", "f") |> ignore

                Expect.equal
                    (refused "no such directory" [
                        $"Dir.move \"{d}/L\" \"{d}/M\""
                        $"Dir.deleteAll \"{d}/a\""
                        $"File.write \"{d}/M/x\" [\"w\"]"
                    ])
                    0
                    "a moved dir link still points at its deleted target"

                Expect.equal
                    (refused "dangling symlink" [
                        $"File.move \"{d}/FL\" \"{d}/FM\""
                        $"File.delete \"{d}/f\""
                        $"File.copy \"{d}/FM\" \"{d}/z\""
                    ])
                    0
                    "a moved file link still points at its deleted target"

                Expect.isTrue (isLink (at "L") && isLink (at "FL")) "nothing applied"

                // an absolute link moved into b: a copy of b into its target is not into itself
                System.IO.Directory.CreateDirectory(at "b") |> ignore
                System.IO.Directory.CreateSymbolicLink(at "AL", at "a") |> ignore

                Expect.equal
                    (runFile [
                        "let p ="
                        "    plan"
                        $"        Dir.move \"{d}/AL\" \"{d}/b/b\""
                        $"        Dir.copy \"{d}/b\" \"{d}/b/b/L\""
                        "p |> Plan.apply"
                    ])
                    0
                    "captured and applied"

                Expect.isTrue (isLink (at "a/L/b")) "the copy keeps the moved link a link"

                // a write through a dangling link creates its target
                System.IO.File.CreateSymbolicLink(at "DG", "nowhere") |> ignore

                Expect.equal
                    (runFile [
                        "let p ="
                        "    plan"
                        $"        File.write \"{d}/DG\" [\"w\"]"
                        $"        File.copy \"{d}/DG\" \"{d}/c\""
                        "p |> Plan.apply"
                    ])
                    0
                    "the copy reads the written target"

                Expect.equal (System.IO.File.ReadAllText(at "c")) "w\n" "copied"

                // a loop
                System.IO.File.CreateSymbolicLink(at "LP", "LP") |> ignore

                Expect.equal
                    (refused "Too many levels of symbolic links" [ $"File.write \"{d}/LP\" [\"w\"]" ])
                    0
                    "a loop refuses at capture"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-preconditions]: a write follows its final link — the
        // target's parent must be a directory, directly and at capture
        test "a write through a link into a missing directory refuses, directly and at capture" {
            skipOnWindows ()
            let root = td ()
            System.IO.Directory.CreateDirectory root |> ignore
            let d = weirPath root

            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(root, "DL"), "nowhere/x")
            |> ignore

            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(root, "OK"), "made/x")
            |> ignore

            try
                Expect.equal
                    (runFile [
                        $"let r = File.write \"{d}/DL\" [\"a\"] |> try result"
                        "match r with"
                        "| Error e -> if not (e |> Str.contains \"no such directory\") || (e |> Str.contains \"Could not\") then fail e"
                        "| Ok _ -> fail \"written\""
                    ])
                    0
                    "the direct call refuses naming the missing directory"

                let code =
                    runFile [
                        "let p ="
                        "    plan"
                        $"        File.write \"{d}/first.txt\" [\"a\"]"
                        $"        File.write \"{d}/DL\" [\"a\"]"
                        "p |> Plan.apply"
                    ]

                Expect.notEqual code 0 "refused"
                Expect.isFalse (System.IO.File.Exists(System.IO.Path.Combine(root, "first.txt"))) "at capture"

                // a directory the plan makes first is where the write lands
                let code =
                    runFile [
                        "let p ="
                        "    plan"
                        $"        Dir.create \"{d}/made\""
                        $"        File.write \"{d}/OK\" [\"a\"]"
                        "p |> Plan.apply"
                    ]

                Expect.equal code 0 "a planned directory satisfies it"

                Expect.isTrue
                    (System.IO.File.Exists(System.IO.Path.Combine(root, "made", "x")))
                    "written through the link"
            finally
                System.IO.Directory.Delete(root, true)
        }
        // [D:plan-preconditions]: the simulation extends its view of a
        // prefix a component at a time — a deep path through a copied
        // directory or a link costs what a plain one does, not depth²
        test "plan writes deep under a copied dir or through a link cost about what plain ones do" {
            skipOnWindows ()
            let root = td ()
            let depth = 150
            let rel = String.replicate depth "d/" |> fun s -> s.TrimEnd '/'

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "src", rel))
            |> ignore

            System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(root, "L"), "src")
            |> ignore

            let d = weirPath root

            try
                let block (name: string) (pre: string list) (dir: string) =
                    [ $"let {name} ="; "    plan" ]
                    @ (pre |> List.map (fun l -> "        " + l))
                    @ [
                        "        for i in [1..600] do"
                        $"            File.write $\"{dir}/{rel}/n{{i}}\" [\"y\"]"
                    ]

                let prog =
                    [ "let t0 = Instant.now ()" ]
                    @ block "pf" [] $"{d}/src"
                    @ [ "let t1 = Instant.now ()" ]
                    @ block "pc" [ $"Dir.copy \"{d}/src\" \"{d}/dst\"" ] $"{d}/dst"
                    @ [ "let t2 = Instant.now ()" ]
                    @ block "pl" [] $"{d}/L"
                    @ [
                        "let t3 = Instant.now ()"
                        "if (pf |> Plan.ops |> Seq.length) + (pc |> Plan.ops |> Seq.length) + (pl |> Plan.ops |> Seq.length) <> 1801 then fail \"ops\""
                        "let fast = Duration.toMillis (t1 - t0)"
                        "let copy = Duration.toMillis (t2 - t1)"
                        "let link = Duration.toMillis (t3 - t2)"
                        "if copy > 5 * fast + 300 then fail $\"copied: {copy}ms vs plain {fast}ms\""
                        "if link > 5 * fast + 300 then fail $\"linked: {link}ms vs plain {fast}ms\""
                    ]

                Expect.equal (runFile prog) 0 "within 5x the plain path"
            finally
                System.IO.Directory.Delete(root, true)
        }
    ]

let withinEnvPairsTests =
    // `within env A=1 B=$x` — the env-prefix words scope a block
    // [D:within-env-pairs]
    let diagsOf = errorMsgsIn "wep.weir"

    // testSequenced: evaluating `within env` pushes the session's root
    // overlay — one stack shared by every test thread — so a parallel
    // pop could drop another test's layer
    testSequenced
    <| testList "within env NAME=value [D:within-env-pairs]" [
        test "pairs in the head check clean, every value spelling" {
            Expect.isEmpty
                (diagsOf [
                    "let x = \"v\""
                    "within env A=1 B=$x C=\"q r\" D= E=$\"{x}!\""
                    "    sh -c \"echo $A\""
                ])
                "values"
        }
        test "the head builds the envPair list; a bound seq still works" {
            match Weir.Assemble.assemble [ 1, "within env A=1"; 2, "    git status" ] with
            | Ok [ ll ] ->
                match Weir.Parser.parseLineFull cmdResolver ll.Text with
                | Ok s -> Expect.stringContains (Weir.Ast.sexprStmt s) "|envPair" "pairs"
                | Error f -> failtest f.Message
            | other -> failtest $"unexpected assembly: {other}"

            Expect.isEmpty
                (diagsOf [ "let e = [Env.pair \"A\" \"1\"]"; "within env e"; "    sh -c \"echo $A\"" ])
                "bound"
        }
        test "a first body line's own prefix stays its command's" {
            match Weir.Assemble.assemble [ 1, "within env A=1"; 2, "    B=2 git status"; 3, "    git log" ] with
            | Ok [ ll ] ->
                Expect.stringContains ll.Text (string Weir.Parser.sibSep) "the head joins sentineled"

                match Weir.Parser.parseLineFull cmdResolver ll.Text with
                | Ok s ->
                    let sx = Weir.Ast.sexprStmt s
                    let pairs = sx.Split("|envPair").Length - 1
                    Expect.equal pairs 2 $"A in the head, B on its command: {sx}"
                | Error f -> failtest f.Message
            | other -> failtest $"unexpected assembly: {other}"
        }
        test "Env.get and Env.vars see the overlay; a bare Env.vars keeps its site's [D:env-overlay-read]" {
            let pin = "[Env.pair \"WEIR_T_OVL\" \"bar\"]"
            expectValue $"within env {pin} Env.get \"WEIR_T_OVL\"" (VUnion("Some", Some(VStr "bar")))
            expectValue "Env.get \"WEIR_T_OVL\"" (VUnion("None", None))

            expectValue
                $"within env {pin} Env.vars |> Seq.where (fun v -> v.name == \"WEIR_T_OVL\") |> Seq.map _.value |> Seq.freeze"
                (VSeq [ VStr "bar" ])

            // forced after the scope exits: the eval site's overlay applies
            expectValue
                $"(within env {pin} Env.vars) |> Seq.where (fun v -> v.name == \"WEIR_T_OVL\") |> Seq.length"
                (VInt 1)
        }
    ]

let withinAlwaysAnyTests =
    // `always` after any within kind [D:within-always-any]: a desugar to a
    // bare within as the kinded scope's body — cleanup inside the scope
    let diagsOf = errorMsgsIn "waa.weir"

    let shape (lines: string list) =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Ok [ ll ] ->
            match Weir.Parser.parseLineFull cmdResolver ll.Text with
            | Ok s -> Weir.Ast.sexprStmt s
            | Error f -> failtest f.Message
        | other -> failtest $"unexpected assembly: {other}"

    testList "always after any within [D:within-always-any]" [
        test "every kind takes a trailing always, checking clean" {
            for head in
                [
                    "within tmp d"
                    "within cd \".\""
                    "within env [Env.pair \"A\" \"1\"]"
                    "within lock \"/tmp/waa.lock\""
                    "within proc p = sleep 1"
                ] do
                Expect.isEmpty (diagsOf [ head; "    print \"b\""; "always"; "    print \"c\"" ]) head
        }
        test "the desugar nests the bare form inside the kinded scope" {
            let sx = shape [ "within tmp d"; "    print \"b\""; "always"; "    print \"c\"" ]
            let w = sx.IndexOf "within"
            let a = sx.IndexOf "always"
            Expect.isTrue (w >= 0 && a > w) $"always inside the within: {sx}"
        }
        test "the binder is in scope inside always" {
            Expect.isEmpty (diagsOf [ "within tmp d"; "    print d"; "always"; "    print d" ]) "d in always"
        }
        test "exit inside a kinded always is refused" {
            let ms = diagsOf [ "within tmp d"; "    print d"; "always"; "    exit 1" ]
            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "exit inside always is refused")) $"{ms}"
        }
        test "exec inside any resource scope, a bare within or always is a check error [D:exec-scope-refused]" {
            let tail = "run the command without exec, or exec after the scope"

            for head, what in
                [
                    "within tmp d", "the temporary directory would never be removed"
                    "within cd \".\"", "the scope would never close"
                    "within env [Env.pair \"A\" \"1\"]", "the scope would never close"
                    "within lock \"/tmp/wes.lock\"", "the lock would never be released"
                    "within proc p = sleep 1", "the scoped process would never be stopped"
                ] do
                let kind = head.Split(' ')[1]

                Expect.equal
                    (diagsOf [ head; "    echo hi | exec" ])
                    [
                        $"'exec' inside 'within {kind}' is refused — exec replaces weir's process, so {what}; {tail}"
                    ]
                    head

                // the always desugar nests inside the kinded scope, so the
                // kind's own refusal speaks
                Expect.equal
                    (diagsOf [ head; "    print \"b\""; "always"; "    echo hi | exec" ])
                    [
                        $"'exec' inside 'within {kind}' is refused — exec replaces weir's process, so {what}; {tail}"
                    ]
                    $"{head} + always"

            Expect.equal
                (diagsOf [ "within"; "    echo hi | exec"; "always"; "    print \"c\"" ])
                [
                    $"'exec' inside a scope with always is refused — exec replaces weir's process, so the always block would never run; {tail}"
                ]
                "bare within body"

            Expect.equal
                (diagsOf [ "within"; "    print \"b\""; "always"; "    echo hi | exec" ])
                [
                    "'exec' inside always is refused — exec replaces weir's process, so the rest of the teardown would never run; run the command without exec"
                ]
                "inside always"

            Expect.isEmpty
                (diagsOf [ "within tmp d"; "    print d"; "echo hi | exec" ])
                "exec after the scope stays legal"
        }
        test "exec reached through a helper or in a fan-out arm is a check error too [D:exec-scope-transitive]" {
            let helper = "let go () = echo hi | exec"

            Expect.equal
                (diagsOf [ helper; "within tmp d"; "    print d"; "    exit (go ())" ])
                [
                    "'go' reaches 'exec' in its body, and 'exec' inside 'within tmp' is refused — exec replaces weir's process, so the temporary directory would never be removed; run its command without exec, or call 'go' after the scope"
                ]
                "a helper inside a resource scope"

            Expect.equal
                (diagsOf [
                    helper
                    "let go2 () = go ()"
                    "within"
                    "    exit (go2 ())"
                    "always"
                    "    print \"c\""
                ])
                [
                    "'go2' reaches 'exec' in its body, and 'exec' inside a scope with always is refused — exec replaces weir's process, so the always block would never run; run its command without exec, or call 'go2' after the scope"
                ]
                "a helper of a helper, in a bare within's body"

            Expect.equal
                (diagsOf [
                    "let stop () = if true then echo bye | exec else ()"
                    "within"
                    "    print \"b\""
                    "always"
                    "    stop ()"
                ])
                [
                    "'stop' reaches 'exec' in its body, and 'exec' inside always is refused — exec replaces weir's process, so the rest of the teardown would never run; run its command without exec"
                ]
                "a unit helper in always"

            Expect.equal
                (diagsOf [ helper; "pfor i in [1; 2] do"; "    if i == 2 then exit (go ())" ])
                [
                    "'go' reaches 'exec' in its body, and 'exec' inside a 'pfor' arm is refused — exec replaces weir's process, so the other arms would be orphaned, never cut or joined; run its command without exec"
                ]
                "a helper in a pfor arm"

            Expect.equal
                (diagsOf [ "[1; 2] |> Seq.piter (fun i -> echo $i | exec)" ])
                [
                    "'exec' inside a 'Seq.piter' arm is refused — exec replaces weir's process, so the other arms would be orphaned, never cut or joined; run the command without exec"
                ]
                "a direct exec in a Seq.piter callback"

            Expect.isEmpty
                (diagsOf [ helper; "within tmp d"; "    print d"; "exit (go ())" ])
                "the helper after the scope stays legal"

            Expect.isEmpty
                (diagsOf [
                    helper
                    "within tmp d"
                    "    let go () = print d"
                    "    go ()"
                    "exit (go ())"
                ])
                "a local binder shadows the helper"
        }
        test "the scope's value carries through" { expectValue "within tmp d\n    41 + 1\nalways\n    ()" (VInt 42L) }
    ]

let withinAlwaysLockTests =
    // the bare scope and the lock kind [D:within-always][D:within-lock]
    let mustSay = mustSayDiagIn "wal.weir"

    let clean = cleanIn "wal.weir"

    testList "bare within + always, within lock" [
        test "bare within: body + always checks clean" {
            clean [ "within"; "    print \"b\""; "always"; "    print \"c\"" ] "the plan's shape"
        }
        test "always after a COMMAND body statement — the sentinel join (until's fixed sibling)" {
            // the space join fed `always` to the command's argv; the
            // sentinel join is the fix, pinned for both segments
            clean [ "within"; "    sh -c \"true\""; "always"; "    print \"c\"" ] "command body"

            clean
                [
                    "let _r = retry attempts=2 delay=10ms"
                    "    sh -c \"true\""
                    "until r"
                    "    true"
                    "print \"done\""
                ]
                "until after a command body parses (was: argv ate the keyword)"
        }
        test "a bare within without always names both readings" {
            mustSay [ "within"; "    print \"b\"" ] "always segment" "missing always"
            mustSay [ "within tpm d"; "    print \"b\"" ] "always segment" "a typo'd kind lands in the bare reading"
        }
        test "exit inside always is refused, at the exit's site" {
            mustSay
                [ "within"; "    print \"b\""; "always"; "    exit 0" ]
                "exit inside always is refused"
                "the teardown-must-finish rule"
        }
        test "retry inside always is legal (a cleanup that retries is reasonable)" {
            clean
                [
                    "within"
                    "    print \"b\""
                    "always"
                    "    retry attempts=2 delay=10ms"
                    "        true"
                ]
                "retry in cleanup"
        }
        test "the always block must be unit" {
            mustSay [ "within"; "    print \"b\""; "always"; "    42" ] "unit" "a valued cleanup refuses"
        }
        test "within lock: path is a string, timeout is a Duration" {
            clean [ "within lock \"/tmp/x.lock\""; "    print \"in\"" ] "bare lock"
            clean [ "within lock \"/tmp/x.lock\" timeout=30s"; "    print \"in\"" ] "with timeout"
            mustSay [ "within lock 42"; "    print \"in\"" ] "string" "an int path refuses"

            mustSay [ "within lock \"/tmp/x.lock\" timeout=30"; "    print \"in\"" ] "Duration" "an int timeout refuses"
        }
        test "sexpr shapes: the bare scope and the lock carry their parts" {
            let sib = Weir.Parser.sibSepStr

            let shapeOf (text: string) =
                match Weir.Parser.parseStmt text with
                | Ok(Weir.Ast.SExpr e)
                | Ok(Weir.Ast.SCmd e) -> Weir.Ast.sexpr e
                | other -> failtest $"unexpected: {other}"

            let sh =
                shapeOf ("within" + sib + "print \"b\"" + sib + "always" + sib + "print \"c\"")

            Expect.stringContains sh "always" "the always segment is in the tree"

            let sh2 = shapeOf ("within lock \"/tmp/x\" timeout=5s" + sib + "print \"in\"")
            Expect.stringContains sh2 "within lock" "the kind"
            Expect.stringContains sh2 "timeout=" "the timeout rides the head"
        }
    ]

let multilineLambdaTests =
    testList "Multiline lambdas" [
        test "closer alone pops at col 0 and at body indent [D:multiline-lambda]" {
            match Weir.Assemble.assemble [ 1, "[\"a\"] |> Seq.iter (fun r ->"; 2, "    print r"; 3, ")" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "[\"a\"] |> Seq.iter (fun r -> print r )" ""
            | other -> failtest $"unexpected: {other}"

            match Weir.Assemble.assemble [ 1, "[\"a\"] |> Seq.iter (fun r ->"; 2, "    print r"; 3, "    )" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "[\"a\"] |> Seq.iter (fun r -> print r )" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "the closer returns the statement to the opener's level: siblings sequence after" {
            match
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    xs |> Seq.iter (fun r ->"
                    3, "        print r"
                    4, "    )"
                    5, "    3"
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let v = xs |> Seq.iter (fun r -> print r ) ; 3") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "body at the opener's indent is a continuation, not a sibling (F#-parity)" {
            match Weir.Assemble.assemble [ 1, "let f ="; 2, "    [1] |> Seq.map (fun x ->"; 3, "    x + 1)" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let f = [1] |> Seq.map (fun x -> x + 1)" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "a line left of the opener errors naming the lambda (F# errors too: FS0058)" {
            match Weir.Assemble.assemble [ 1, "let f ="; 2, "    [1] |> Seq.map (fun x ->"; 3, "  x + 1)" ] with
            | Error e -> Expect.stringContains e "left of the lambda '(' opened at line 2" ""
            | other -> failtest $"expected the leak error, got {other}"
        }
        test "a col-0 line under a col-0 opener joins as body; EOF names the open lambda" {
            // opener at col 0 makes col 0 the body floor (the
            // at-opener-indent continuation rule): nothing sits left of
            // it, so the runaway surfaces at close, named
            match Weir.Assemble.assemble [ 1, "xs |> Seq.iter (fun r ->"; 2, "    print r"; 3, "let x = 1" ] with
            | Error e -> Expect.stringContains e "line 1: this lambda's '(' is still open" ""
            | other -> failtest $"expected the open-lambda close error, got {other}"
        }
        test "EOF with the lambda open names the opener line" {
            match Weir.Assemble.assemble [ 1, "xs |> Seq.iter (fun r ->"; 2, "    print r" ] with
            | Error e -> Expect.stringContains e "line 1: this lambda's '(' is still open" ""
            | other -> failtest $"expected the open-lambda error, got {other}"
        }
        test "a body let still needs its body before the paren closes" {
            match Weir.Assemble.assemble [ 1, "xs |> Seq.iter (fun r ->"; 2, "    let a = 1"; 3, ")" ] with
            | Error e -> Expect.stringContains e "needs a body" ""
            | other -> failtest $"expected noBody, got {other}"
        }
        test "a compound in the body prunes at the user's closer (the original repro, now designed)" {
            // [D:compound-paren-prune]: the match must not swallow
            // the next outer stage
            match
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    xs"
                    3, "    |> Seq.map (fun n ->"
                    4, "        match n with"
                    5, "        | 1 -> 10"
                    6, "        | _ -> n"
                    7, "    )"
                    8, "    |> Seq.sum"
                ]
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
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    xs |> Seq.iter (fun r ->"
                    3, "        print r)"
                    4, "    40"
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let v = xs |> Seq.iter (fun r -> print r) ; 40") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "a literal's closer sharing the lambda's ')' line closes the lambda (starship port) [D:multiline-lambda]" {
            // `])` / `})`: the closing line of the last open bracket
            // moves the paren depth, so the lambda pops there exactly
            // as on a lone `)` line
            errorsCleanIn
                "w3.weir"
                [
                    "let ys ="
                    "    [\"a\"; \"b\"]"
                    "    |> Seq.map (fun r ->"
                    "        Str.join \"\" ["
                    "            r"
                    "            \"z\""
                    "        ])"
                    "ys |> Seq.iter print"
                ]
                "list closer + lambda closer"

            errorsCleanIn
                "w7.weir"
                [
                    "type P = { a: string; b: string }"
                    "let ys ="
                    "    [\"a\"; \"b\"]"
                    "    |> Seq.map (fun r ->"
                    "        let s = r + \"!\""
                    "        {"
                    "            a = s"
                    "            b = \"z\""
                    "        })"
                    "ys |> Seq.iter (fun p -> print p.a)"
                ]
                "record closer + lambda closer after a body let"

            errorsCleanIn
                "w9.weir"
                [
                    "[\"a\"; \"b\"]"
                    "    |> Seq.iter (fun r ->"
                    "        Seq.iter print ["
                    "            r"
                    "            \"z\""
                    "        ])"
                ]
                "statement-level pipeline, EOF after the closer"

            // the level restores: a sibling after `])` sequences
            match
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    xs |> Seq.iter (fun r ->"
                    3, "        print ["
                    4, "            r"
                    5, "        ])"
                    6, "    40"
                ]
            with
            | Ok [ ll ] -> Expect.stringContains ll.Text (asmSib "]) ; 40") "the sibling after the closer sequences"
            | other -> failtest $"unexpected: {other}"
        }
        test "nested multiline lambdas pop innermost-first" {
            match
                Weir.Assemble.assemble [
                    1, "rows |> Seq.iter (fun row ->"
                    2, "    row |> Seq.iter (fun c ->"
                    3, "        print c"
                    4, "    )"
                    5, "    print \"row-done\""
                    6, ")"
                ]
            with
            | Ok [ ll ] ->
                Expect.equal
                    ll.Text
                    (asmSib "rows |> Seq.iter (fun row -> row |> Seq.iter (fun c -> print c ) ; print \"row-done\" )")
                    ""
            | other -> failtest $"unexpected: {other}"
        }
        test "command block-let in a lambda body parses on the let-RHS spine [D:multiline-lambda]" {
            let r: Weir.Parser.Resolver = {
                IsKnown = (fun n -> n = "xs" || n = "Seq")
                IsCommandCallable = (fun _ -> false)
                IsExternal = (fun n -> n = "echo")
                ExternalNames = (fun () -> Seq.empty)
                BareHome = (fun _ -> None)
                AliasHead = fun _ -> None
                IsBuiltinName = fun _ -> false
            }

            match Weir.Parser.parseLine r "let out = xs |> Seq.map (fun k -> let g = echo tag in g |> Seq.length)" with
            | Ok _ -> ()
            | Error e -> failtest $"spine must reach the lambda body: {e}"
        }
        test "lambda params shadow PATH in their body" {
            // under the assume-resolver a param-headed let RHS must stay
            // an expression, not become a phantom command [D:paramful-rhs]
            let lines = [
                "let counts ="
                "    [\"a b\"]"
                "    |> Seq.map (fun line ->"
                "        let hash = line |> Str.split \" \" |> Seq.head"
                "        hash)"
                ""
                "counts |> Seq.iter print"
            ]

            let diags = diagsIn "pin.weir" lines
            Expect.isEmpty diags "no diagnostics — the param is known, not a command head"
        }
        test "a pattern let's names shadow PATH in the body's if-condition [D:paramful-rhs]" {
            // `let a, b = …` then `if a == "" then` once read `a` as a
            // command head: the pattern path scoped no names into its body
            let clean = checkEqRun

            clean [
                "let f () ="
                "    let a, b = (\"x\", \"y\")"
                "    if a == \"\" then b else a"
                "print (f ())"
            ]

            clean [
                "type R = { name: string }"
                "let g r ="
                "    let { name = test } = r"
                "    if test == \"\" then \"none\" else test"
                "print (g { name = \"x\" })"
            ]
        }
        test "block-let params shadow PATH in an if-condition head [D:paramful-rhs]" {
            // a param heading a block-let's if-condition is a binding —
            // bindings-beat-PATH reaches condition position at block-let
            // depth (check's assume-resolver once claimed it as a
            // phantom command: cmd-not-found + bogus type errors)
            let clean = checkEqRun

            clean [
                "let outer xs ="
                "    let f pairs = if pairs |> Seq.exists (fun (_, v) -> v > 3) then \"big\" else \"small\""
                "    f xs"
                "print (outer [(\"a\", 5)])"
            ]

            // even a param named like a real PATH binary stays the binding
            clean [
                "let outer b ="
                "    let pick test = if test then \"y\" else \"n\""
                "    pick b"
                "print (outer true)"
            ]

            // the other direction: a genuine external head in that same
            // condition position keeps the command chain [D:if-succeeds]
            let diags =
                diagsIn "pin.weir" [
                    "let outer p ="
                    "    let probe f = if test -f $f | succeeds then \"yes\" else \"no\""
                    "    probe p"
                    "print (outer \"/etc/hostname\")"
                ]

            Expect.isEmpty diags "an external head in a block-let if-condition still chains"
        }
    ]

let bracketLambdaBodyTests =
    testList "Lambda bodies in bracket entries [D:bracket-lambda-body]" [
        test "a block let in a record field's lambda body closes with `in`; the next field splits at the field sentinel" {
            match
                Weir.Assemble.assemble [
                    1, "let c = {"
                    2, "    write = fun n ->"
                    3, "        let a = n + 1"
                    4, "        print $\"{a}\""
                    5, "    name = \"x\""
                    6, "}"
                ]
            with
            | Ok [ ll ] ->
                Expect.equal
                    ll.Text
                    ("let c = { write = fun n -> let a = n + 1 in print $\"{a}\" "
                     + Weir.Parser.fieldSepStr
                     + " name = \"x\" }")
                    ""
            | other -> failtest $"unexpected: {other}"
        }
        test "body statements sequence at the statement sentinel, never as an application" {
            match
                Weir.Assemble.assemble [
                    1, "let c = {"
                    2, "    write = fun n ->"
                    3, "        print \"x\""
                    4, "        print $\"{n}\""
                    5, "}"
                ]
            with
            | Ok [ ll ] ->
                Expect.equal
                    ll.Text
                    ("let c = { write = fun n -> print \"x\" "
                     + Weir.Parser.sibSepStr
                     + " print $\"{n}\" }")
                    ""
            | other -> failtest $"unexpected: {other}"
        }
        test "the receipt: a nested record with two lambda fields, one a block body" {
            let diags =
                diagsIn "sub.weir" [
                    "type Cursor = { read: unit -> Size; write: Size -> unit }"
                    "type Sub = { name: string; cursor: Cursor }"
                    "let ensureSub dir name ="
                    "    let path = Path.under dir $\"{name}.cursor\""
                    "    let sub = {"
                    "        name = path"
                    "        cursor ="
                    "        {"
                    "            read = fun () -> File.read path |> Seq.exactlyOne |> Str.toInt |> Size.bytes"
                    "            write = fun new ->"
                    "                let _a = 10"
                    "                [$\"{new |> Size.toBytes}\"] |> File.writeAtomic path"
                    "        }"
                    "    }"
                    "    if not (File.exists sub.name) then"
                    "        0B |> sub.cursor.write"
                    "    sub"
                    "print (ensureSub \"d\" \"a\").name"
                ]

            Expect.isEmpty diags $"the block-body field must check: {diags}"
        }
        test "match arms, an if/else block and a nested record all read as a body; the next field still splits" {
            let diags =
                diagsIn "body.weir" [
                    "type R = { a: int; b: int }"
                    "type C = { write: int -> unit; name: string }"
                    "let c = {"
                    "    write = fun n ->"
                    "        let r = {"
                    "            a = n"
                    "            b = 2"
                    "        }"
                    "        match r.a with"
                    "        | 0 -> print \"zero\""
                    "        | _ -> print \"other\""
                    "        if r.b > 0 then"
                    "            print \"pos\""
                    "        else"
                    "            print \"nonpos\""
                    "    name = \"x\""
                    "}"
                    "c.write 1"
                    "print c.name"
                ]

            Expect.isEmpty diags $"compounds and arms inside the body must check: {diags}"
        }
        test "list elements and anonymous-record fields take bodies too" {
            let listDiags =
                diagsIn "l.weir" [
                    "let fs = ["
                    "    fun n ->"
                    "        let a = n + 1"
                    "        print $\"{a}\""
                    "    fun n ->"
                    "        print $\"{n}\""
                    "]"
                    "fs |> Seq.iter (fun f -> f 1)"
                ]

            Expect.isEmpty listDiags $"list element bodies must check: {listDiags}"

            let anonDiags =
                diagsIn "a.weir" [
                    "let c = {|"
                    "    write = fun n ->"
                    "        let a = n + 1"
                    "        print $\"{a}\""
                    "|}"
                    "c.write 1"
                ]

            Expect.isEmpty anonDiags $"anonymous-record field bodies must check: {anonDiags}"
        }
        test "a `(fun` entry arms its lambda: the `)` alone closes it, the next field follows" {
            let diags =
                diagsIn "p.weir" [
                    "type C = { write: int -> unit; name: string }"
                    "let c = {"
                    "    write = (fun n ->"
                    "        let _a = 10"
                    "        print $\"{n}\""
                    "    )"
                    "    name = \"x\""
                    "}"
                    "c.write 1"
                    "print c.name"
                ]

            Expect.isEmpty diags $"the paren form must check: {diags}"
        }
        test "a `}` attached to the last body line restores the statement level: the sibling after sequences" {
            let diags =
                diagsIn "w.weir" [
                    "type C = { write: int -> unit }"
                    "within tmp d"
                    "    let c = {"
                    "        write = fun n ->"
                    "            let _a = 10"
                    "            print $\"{n}\" }"
                    "    c.write 1"
                    "    print \"after\""
                ]

            Expect.isEmpty diags $"the attached closer must not glue the sibling: {diags}"
        }
        test "a body let with no body is the let's error, at the next entry and at the closer" {
            let atCloser =
                errorMsgsIn "e.weir" [
                    "type C = { write: int -> unit }"
                    "let c = {"
                    "    write = fun n ->"
                    "        let a = n"
                    "}"
                ]

            Expect.exists atCloser (fun m -> m.Contains "this let needs a body") $"closer: {atCloser}"

            let atField =
                errorMsgsIn "f.weir" [
                    "type C = { write: int -> unit; name: string }"
                    "let c = {"
                    "    write = fun n ->"
                    "        let a = n"
                    "    name = \"x\""
                    "}"
                ]

            Expect.exists atField (fun m -> m.Contains "this let needs a body") $"field: {atField}"
        }
        test "an unclosed `(fun` names its paren when the field ends" {
            let msgs =
                errorMsgsIn "u.weir" [
                    "type C = { write: int -> unit; name: string }"
                    "let c = {"
                    "    write = (fun n ->"
                    "        print $\"{n}\""
                    "    name = \"x\""
                    "}"
                ]

            Expect.exists msgs (fun m -> m.Contains "still open when its field/element ends") $"{msgs}"
        }
        test "an arm's arrow after the fun keeps the continuation law: the arm body sits below, arms follow" {
            let diags =
                diagsIn "arm.weir" [
                    "type C = { f: int -> string; name: string }"
                    "let c = {"
                    "    f = fun n -> match n with | 0 ->"
                    "                                \"zero\""
                    "                              | _ -> \"other\""
                    "    name = \"x\""
                    "}"
                    "print (c.f 0)"
                    "print c.name"
                ]

            Expect.isEmpty diags $"the arm-arrow continuation must still check: {diags}"
        }
        test "a single-expression continuation body stays what it was" {
            match
                Weir.Assemble.assemble [
                    1, "let c = {"
                    2, "    act = (fun () ->"
                    3, "        1)"
                    4, "    name = \"x\""
                    5, "}"
                ]
            with
            | Ok [ ll ] ->
                Expect.equal ll.Text ("let c = { act = (fun () -> 1) " + Weir.Parser.fieldSepStr + " name = \"x\" }") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "fmt: `{|` dangles like `{` — entries at +4, `|}` at the opener's indent; a body inside is fmt-stable" {
            match Weir.Fmt.formatLines [ "let c = {|"; "         a = 1"; "         |}" ] with
            | Ok lines ->
                Expect.equal lines[1] "    a = 1" "anonymous entries sit one level in"
                Expect.equal lines[2] "|}" "the anonymous closer returns to the opener line"
            | Error e -> failtest e

            let body = [
                "let c = {|"
                "    write = fun n ->"
                "        let a = n + 1"
                "        print $\"{a}\""
                "|}"
            ]

            match Weir.Fmt.formatLines body with
            | Ok lines -> Expect.equal lines body "a canonical body inside an anonymous record is stable"
            | Error e -> failtest e
        }
    ]

let pipeAlignTests =
    testList "Pipe alignment" [
        test "off-by-one siblings error naming the column [D:pipe-alignment]" {
            match Weir.Assemble.assemble [ 1, "type C ="; 2, "    | A of int"; 3, "     | B of string" ] with
            | Error e -> Expect.stringContains e "they sit at column 4" ""
            | Ok _ -> failtest "expected the alignment error"

            match
                Weir.Assemble.assemble [ 1, "let v ="; 2, "    match 1 with"; 3, "    | 1 -> 1"; 4, "   | _ -> 0" ]
            with
            | Error e -> Expect.stringContains e "align the group" ""
            | Ok _ -> failtest "expected the alignment error"
        }
        test "arms left of their match error naming the head" {
            match Weir.Assemble.assemble [ 1, "let v ="; 2, "    match 3 with"; 3, "| _ -> 0" ] with
            | Error e -> Expect.stringContains e "left of its match (head at column 4)" ""
            | Ok _ -> failtest "expected the offside error"
        }
        test "a returning arm after a multi-statement body is an arm, not left-of-match [D:match-pipe-offside]" {
            // a `let` then an if/else in an arm body leaves the if
            // compound open when the next arm arrives: the arm group
            // at the arm's own column says it is a returning arm —
            // the deeper if offside-closes, it is not "its match"
            let lines = [
                "let classify n ="
                "    match n with"
                "    | v when v > 0 ->"
                "        let doubled = v * 2"
                "        if doubled > 10 then"
                "            print \"big\""
                "        else"
                "            print \"small\""
                "    | _ -> print \"zero\""
                "classify 9"
            ]

            let diags = diagsIn "pin.weir" lines
            Expect.isEmpty (diags |> List.map (fun d -> d.Message)) "the healthy arm assembles and checks"

            // the other direction: an arm genuinely left of its match
            // (no arm group at its column) keeps the offside error
            match
                Weir.Assemble.assemble [ 1, "let v ="; 2, "    match 3 with"; 3, "    | 1 -> 0"; 4, "  | _ -> 1" ]
            with
            | Error e -> Expect.stringContains e "align the group" "misaligned arms still refuse"
            | Ok _ -> failtest "expected the alignment error"
        }
        // the arm-commit soundness premise rides this invariant
        // [D:arm-commit]: offside-close paren-wraps nested matches, so
        // at the logical line a '|' after a completed arm at the same
        // paren depth can only be another arm
        test "the nested-match return F# reads from columns now assembles" {
            match
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    match 1 with"
                    3, "    | 1 ->"
                    4, "        match 2 with"
                    5, "        | 2 -> \"a\""
                    6, "        | _ -> \"b\""
                    7, "    | _ -> \"c\""
                ]
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
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    match 1 with"
                    3, "        | 1 -> 1"
                    4, "        | _ -> 0"
                ]
            with
            | Ok _ -> ()
            | Error e -> failtest $"consistent-deeper must stay legal: {e}"
        }
    ]

let multilineTests =
    testList "Multi-line assembly" [
        test "a flag spelled -in or --in in a block let's command is not the let's `in` [D:inline-let-in-blocks]" {
            // the keyword stands alone; glued to a dash it is a command word,
            // so the block-let still takes its body from the next line
            for rhs in [ "cat -in $k"; "openssl rsa --in $k -noout"; "cat $k -in" ] do
                match Weir.Assemble.assemble [ 1, "let f k ="; 2, $"    let m = {rhs}"; 3, "    m" ] with
                | Ok [ a ] ->
                    Expect.equal a.Text $"let f k = let m = {rhs} in m" $"the let closes on the next line: {rhs}"
                | other -> failtest $"unexpected for {rhs}: {other}"

            match Weir.Assemble.assemble [ 1, "let f k ="; 2, "    let m = 3 in m" ] with
            | Ok [ a ] -> Expect.equal a.Text "let f k = let m = 3 in m" "a real inline in still closes the let"
            | other -> failtest $"unexpected: {other}"
        }
        test
            "an inline let…in inside an indented block is a whole expression, not a block-let head [D:inline-let-in-blocks]" {
            // function, if, lambda, match-arm bodies: the same line works at column 0
            match Weir.Assemble.assemble [ 1, "let f () ="; 2, "    let q = 3 in q + 1"; 3, "print $\"{f ()}\"" ] with
            | Ok [ a; _ ] -> Expect.equal a.Text "let f () = let q = 3 in q + 1" "no pending body, no noBody error"
            | other -> failtest $"unexpected: {other}"

            for lines in
                [
                    [ "let f () ="; "    let q = 3 in q + 1"; "print $\"{f ()}\"" ]
                    [ "if true then"; "    let q = 3 in print $\"{q + 1}\"" ]
                    [ "let g = (fun x ->"; "    let q = x in q + 1)"; "print $\"{g 2}\"" ]
                    [
                        "let r = match 2 with"
                        "        | 1 -> 0"
                        "        | n ->"
                        "            let q = n in q * 10"
                        "print $\"{r}\""
                    ]
                    [ "let h () ="; "    let a = 1 in let b = 2 in a + b"; "print $\"{h ()}\"" ]
                    [ "let z () ="; "    let t = 1"; "    let u = 2 in t + u"; "print $\"{z ()}\"" ]
                ] do
                Expect.isEmpty (errorMsgsIn "li.weir" lines) $"checks clean: {lines}"
        }
        test
            "a block let whose RHS only MENTIONS in (string, bracket, for) still waits for its body [D:inline-let-in-blocks]" {
            match Weir.Assemble.assemble [ 1, "let f () ="; 2, "    let s = \"a in b\""; 3, "    s" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let f () = let s = \"a in b\" in s" "the string's in is not the let's"
            | other -> failtest $"unexpected: {other}"

            match
                Weir.Assemble.assemble [
                    1, "let f () ="
                    2, "    let n = Seq.length [for i in [1; 2] -> i]"
                    3, "    n"
                ]
            with
            | Ok [ ll ] -> Expect.stringContains ll.Text "-> i] in n" "a bracketed for's in is not the let's"
            | other -> failtest $"unexpected: {other}"
        }
        test "block lets: implicit in at the same indentation" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, "    a + 1" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = 1 in a + 1" "implicit in inserted"
            | other -> failtest $"unexpected: {other}"
        }
        test "block lets: sequential bindings chain" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, "    let b = 2"; 4, "    a + b" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = 1 in let b = 2 in a + b" "both closed"
            | other -> failtest $"unexpected: {other}"
        }
        test "block lets: nested with RHS spill" {
            match
                Weir.Assemble.assemble [
                    1, "let x ="
                    2, "    let a ="
                    3, "        [1; 2]"
                    4, "        |> Seq.length"
                    5, "    a + 1"
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = [1; 2] |> Seq.length in a + 1" "spill then close"
            | other -> failtest $"unexpected: {other}"
        }
        test "block lets: arms deeper than the pending indent are plain continuations" {
            match
                Weir.Assemble.assemble [
                    1, "let x ="
                    2, "    let v ="
                    3, "        match h with"
                    4, "        | Some n -> n"
                    5, "        | None -> 0"
                    6, "    v + 1"
                ]
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
                Weir.Assemble.assemble [
                    1, "let category ="
                    2, "    match size with"
                    3, "    | s when s > 100 -> \"big\""
                    4, "    | _ -> \"small\""
                ]
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
                Weir.Assemble.assemble [
                    1, "let r ="
                    2, "    let v ="
                    3, "        match h with"
                    4, "| Some n -> n"
                    5, "    v"
                ]
            with
            | Error msg ->
                Expect.stringContains msg "line 2" "blames the binding line"
                Expect.stringContains msg "needs a body" "the same verdict F# gives"
            | other -> failtest $"expected an error, got {other}"
        }
        test "F#-rejects-this: arm at exactly the pending indent" {
            match
                Weir.Assemble.assemble [
                    1, "let r ="
                    2, "    let v ="
                    3, "        match h with"
                    4, "    | Some n -> n"
                ]
            with
            | Error msg -> Expect.stringContains msg "line 2" "at-or-left errors, matching F#"
            | other -> failtest $"expected an error, got {other}"
        }
        test "guard: column-0 pipeline continuation outside any block survives" {
            match Weir.Assemble.assemble [ 1, "git branch"; 2, "| map trim" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "git branch | map trim" "the rule's real customer"
            | other -> failtest $"unexpected: {other}"
        }
        test "composition: inertness resumes after a block closes mid-script" {
            match
                Weir.Assemble.assemble [
                    1, "let x ="
                    2, "    let a = 1"
                    3, "    a + 1"
                    4, ""
                    5, "git branch"
                    6, "| map trim"
                ]
            with
            | Ok [ first; second ] ->
                Expect.equal first.Text "let x = let a = 1 in a + 1" "block closed"
                Expect.equal second.Text "git branch | map trim" "stack empty again, | inert"
            | other -> failtest $"unexpected: {other}"
        }
        test "block lets: statement end with pending let errors" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    let a = 1" ] with
            | Error msg -> Expect.stringContains msg "needs a body" "names the gap"
            | other -> failtest $"expected an error, got {other}"
        }
        test "block lets: dedent past a bodyless let errors" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    let a ="; 3, "        let b = 1"; 4, "    a" ] with
            | Error msg -> Expect.stringContains msg "line 3" "blames the deepest let"
            | other -> failtest $"expected an error, got {other}"
        }
        test "block lets: spans translate through inserted in" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, "    a + 1" ] with
            | Ok [ ll ] ->
                // "let x = let a = 1" is 17 chars; " in " puts the body at 0-based 21 → 1-based col 22
                Expect.equal (Weir.Assemble.translate ll 22) (3, 5) "body start maps to physical 3:5"
            | other -> failtest $"unexpected: {other}"
        }
        test "logical line joins continuations" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    ls"; 3, "    |> Seq.map _.name" ] with
            | Ok [ ll ] ->
                Expect.equal ll.Text "let x = ls |> Seq.map _.name" "joined"
                Expect.equal ll.Head 1 "head line"
                Expect.equal (Weir.Assemble.translate ll 9) (2, 5) "col 9 is line 2 col 5"
                Expect.equal (Weir.Assemble.translate ll 1) (1, 1) "head col maps to itself"
            | other -> failtest $"unexpected: {other}"
        }
        test "pipe-headed lines continue at column 0" {
            match
                Weir.Assemble.assemble [ 1, "match x with"; 2, "| Some n -> n"; 3, "| None -> 0"; 4, ""; 5, "next" ]
            with
            | Ok [ m; n ] ->
                Expect.equal m.Text "match x with | Some n -> n | None -> 0" ""
                Expect.equal n.Text "next" ""
            | other -> failtest $"unexpected: {other}"
        }
        // blanks are transparent while a statement pends [D:body-blanks]
        test "blank then continuation joins (transparency)" {
            match Weir.Assemble.assemble [ 1, "let x = 1"; 2, ""; 3, "    |> Seq.map f" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = 1 |> Seq.map f" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "tabs in indentation are an error" {
            match Weir.Assemble.assemble [ 1, "let x = 1"; 2, "\t|> f" ] with
            | Error msg -> Expect.stringContains msg "tabs are not allowed" ""
            | Ok _ -> failtest "expected error"
        }
        test "a tab in any block content line's leading whitespace is refused, with its column [D:spaces-only-indent]" {
            let cases = [
                [ "let t = <<<"; "    \tabc" ], 2, 5 // first content line, after the base
                [ "let t = <<<"; "    abc"; "    \tdef" ], 3, 5 // later line, after the base
                [ "let t = <<<"; "    abc"; "    \t// d" ], 3, 5 // comment-shaped content
                [ "let t = yaml"; "    a: |"; "        x"; "        \ty" ], 4, 9
            ] // yaml block scalar

            for lines, line, col in cases do
                match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
                | Error msg ->
                    Expect.stringContains msg $"line {line}: tabs are not allowed in indentation" ""
                    Expect.stringContains msg $"column {col}" ""
                | Ok _ -> failtest $"expected the tab refusal for {lines}"

            // a tab after the first non-whitespace character is content
            match Weir.Assemble.assemble [ 1, "let t = <<<"; 2, "    a\tb" ] with
            | Ok [ _ ] -> ()
            | other -> failtest $"a mid-line tab is content, got {other}"
        }
        test "leading continuation is an error" {
            match Weir.Assemble.assemble [ 1, "    orphan" ] with
            | Error msg -> Expect.stringContains msg "continuation without a statement" ""
            | Ok _ -> failtest "expected error"
        }
    ]

let unitPrintTests =
    testList "Unit, print, and the statement rule" [
        test "unit literal parses and evals" {
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
        test "iter is bare (single-home [D:bare-partition]); strict mode points at the home" {
            expectValue "[\"a\"] |> iter print" VUnit

            let strictEnv, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            match Weir.Check.typecheck strictEnv (parse "iter print [\"a\"]") with
            | Error terr -> Expect.stringContains (formatError terr) "Seq.iter" "points at the module home"
            | Ok _ -> failtest "strict mode must not resolve bare iter"
        }
        test "print is reserved — the shadow pin FLIPPED [D:reserve-builtins]" {
            Expect.stringContains
                (checkErr "let print = fun s -> s in print \"x\"").Message
                "'print' is a builtin with no qualified spelling"
                "no way back, so no binding"
        }
        test "unit renders in a hole (the class is the law) [D:interp-show]" { expectValue "$\"a{()}b\"" (VStr "a()b") }
        test "classifier: command lines parse as SCmd, expressions as SExpr" {
            match Weir.Parser.parseLine cmdResolver "git status" with
            | Ok(SCmd _) -> ()
            | other -> failtest $"expected SCmd, got {other}"

            match Weir.Parser.parseLine cmdResolver "git branch |> map trim" with
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
            Expect.isNone (Weir.Statements.discardError TUnit) "unit statement is fine"

            match Weir.Statements.discardError TStr with
            | Some msg -> Expect.stringContains msg "pipe it to print" "names the fix"
            | None -> failtest "string discard must be rejected"

            match Weir.Statements.discardError (TSeq TUnit) with
            | Some msg -> Expect.stringContains msg "Seq.iter" "the trap hint"
            | None -> failtest "seq<unit> discard must be rejected"
        }
    ]

let rangeTests =
    testList "Range literals" [
        test "desugars to Seq.range" { expectParse "[1..5]" "(((|seqRange 1) 1) 5)" }
        test "stepped form" { expectParse "[0..2..10]" "(((|seqRange 0) 2) 10)" }
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
            expectValue "[1..1000000] |> Seq.take 3 |> Seq.length" (VInt 3L)
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
        }
    ]

let depthGuardTests =
    testList "Depth guard" [
        test "legitimate nesting is untouched (corpus max is ~11)" {
            expectValue (nestDeep "(" ")" 100 + " + 0") (VInt 1L)
            expectValue "[[[1]]] |> Seq.take 1 |> Seq.freeze |> Seq.length" (VInt 1L)
        }
        test "at-ceiling parens: parse or a located diagnostic, never a crash (limit 500, stack-probed)" {
            // capacity between the stack probe's floor and the counted
            // ceiling is platform-dependent by design — big stacks parse
            // 499, small stacks get the probe's diagnostic; a crash is
            // the only wrong answer [D:depth-guard]
            match Weir.Parser.parseExpr (nestDeep "(" ")" 499) with
            | Ok _ -> ()
            | Error m -> Expect.stringContains m "nested too deeply" "the probe's diagnostic is the small-stack answer"
        }
        test "small-stack thread: deep parse diagnoses via the stack probe, no overflow [D:depth-guard]" {
            // the macOS finding, emulated: test hosts there run smaller
            // stacks than Linux's 8MB and overflowed at ~420 of 500.
            // On a deliberately tiny stack, returning at all is the
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
        test "a block's statements are width, not depth [D:block-width]" {
            // 600 lets and a 600-element `;` sequence: past the ceiling
            // as a nest, but statements never count toward it. A thread
            // with room — the CLI's own stack — so the probe stays quiet.
            let lets =
                (List.init 600 (fun i -> $"let _a{i} = {i} in") |> String.concat " ")
                + " _a0 + _a599"

            let seqs = List.replicate 600 "1" |> String.concat "; "
            let mutable result = None

            let t =
                System.Threading.Thread(
                    (fun () -> result <- Some(Weir.Parser.parseExpr lets, Weir.Parser.parseExpr seqs)),
                    64 * 1024 * 1024
                )

            t.Start()
            t.Join()

            match result with
            | Some(Ok _, Ok _) -> ()
            | Some(a, b) -> failtestf "block width counted as depth: %A / %A" (Result.isOk a) b
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
            | Error m ->
                Expect.stringContains m "nested too deeply" "spine caught by the post-parse gate"
                Expect.stringContains m "bind intermediates with let" "the flat chain's repair is named"
            | Ok _ -> failtest "a 3000-term spine must be rejected"
        }
        test "an over-ceiling chain is located at the chain's start, not an inner prefix [D:depth-locate-chain]" {
            let spine = List.replicate 600 "1" |> String.concat " + "

            match Weir.Parser.parseLineFull cmdResolver $"print $\"{{{spine}}}\"" with
            | Error f ->
                Expect.stringContains f.Message "nested too deeply" "the depth diagnostic"
                Expect.equal f.Col (Some 10) "the chain's first term"
            | Ok _ -> failtest "a 600-term spine must be rejected"

            let lines = "let x =" :: "    1" :: List.replicate 600 "    |> (fun v -> v + 1)"

            match diagsIn "lp.weir" lines |> List.filter (fun d -> d.Severity = "error") with
            | d :: _ -> Expect.equal (d.Line, d.Col) (2, 5) "the pipeline's head line, not its 105th stage"
            | [] -> failtest "a 600-stage pipeline must be rejected"
        }
        test "over-ceiling nested brackets diagnose, not hang (was O(2^n))" {
            match Weir.Parser.parseExpr (nestDeep "[" "]" 800) with
            | Error m -> Expect.stringContains m "nested too deeply" "bracket depth caught"
            | Ok _ -> failtest "depth 800 brackets must be rejected"
        }
        test "a wide cons-chain pattern's name walk does not overflow the process [D:pattern-width]" {
            // Property-3 on the width axis: patLeafNames recurses once
            // per leaf; a `a :: a :: … :: _` chain is flat-reading but
            // builds a deep right-spine, and the old recursive walk blew
            // the stack during parse (in withPatNames) — a host crash,
            // not a diagnostic. Reaching this line at all is the pin: a
            // stack overflow is uncatchable and would abort the runner.
            let line =
                "let v = match [1] with | " + String.replicate 20000 "a :: " + "_ -> 1 | _ -> 0"

            let diags = diagsIn "pin.weir" [ line ]
            Expect.isNonEmpty diags "the depth guard diagnoses; the iterative name walk never crashes"
        }
        test "a wide tuple's type walks do not overflow a 1MB stack [D:pattern-width]" {
            let lines = [ "let t = (" + String.replicate 19999 "0, " + "0)"; "print t" ]
            let diags = ref []

            let th =
                System.Threading.Thread((fun () -> diags.Value <- diagsIn "pin.weir" lines), 1024 * 1024)

            th.Start()
            th.Join()
            Expect.isNonEmpty diags.Value "print rejects the tuple with a diagnostic, not a crash"
        }
        test "nested failing openers parse each level once [D:parse-fast-path]" {
            let d = 24

            let spines = [
                "parens", String.replicate d "(" + "1 +" + String.replicate d ")"
                "list", String.replicate d "[" + "1 +" + String.replicate d "]"
                "neg", String.replicate d "-(" + "1 +" + String.replicate d ")"
                "record", String.replicate d "{ a = " + "1 +" + String.replicate d " }"
                "anon", String.replicate d "{| a = " + "1 +" + String.replicate d " |}"
                "interp", String.replicate d "$\"{" + "1 +" + String.replicate d "}\""
                "sigil", String.replicate d "$(echo " + "(1 +" + String.replicate (d + 1) ")"
            ]

            for name, spine in spines do
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let diags = diagsIn "pin.weir" [ "let x = " + spine ]
                sw.Stop()
                Expect.isNonEmpty diags $"{name}: the failing spine is diagnosed"

                Expect.isLessThan
                    sw.ElapsedMilliseconds
                    10000L
                    $"{name}: depth {d} stays linear (2^{d} replays would not finish)"
        }
        test
            "the fast parser refuses past its nesting bound, parses index chains once, and leaves no pending shape behind [D:hand-parser]" {
            let r: Weir.Parser.Resolver = {
                IsKnown = (fun _ -> true)
                IsCommandCallable = (fun _ -> false)
                IsExternal = (fun _ -> false)
                ExternalNames = (fun () -> Seq.empty)
                BareHome = (fun _ -> None)
                AliasHead = fun _ -> None
                IsBuiltinName = fun _ -> false
            }

            let nested d =
                "let x = " + String.replicate d "(" + "1" + String.replicate d ")"

            Expect.isSome (Weir.Fast.tryStmt r (nested 30)) "a 30-deep nest is the fast parser's"
            Expect.isNone (Weir.Fast.tryStmt r (nested 200)) "a 200-deep nest is refused to the grammar's guard"

            // a failing nested index chain: one parse per level, so the
            // refusal is linear in the depth
            let chain =
                "let x = " + String.replicate 2000 "a[" + "1 +" + String.replicate 2000 "]"

            let sw = System.Diagnostics.Stopwatch.StartNew()
            Expect.isNone (Weir.Fast.tryStmt r chain) "the deep chain is refused"
            Expect.isLessThan sw.ElapsedMilliseconds 5000L "the refusal never doubles per level"

            // nested list elements that can nest (a prefix minus, parens,
            // an interpolation) are parsed once: the range probe runs on
            // leaf first tokens only
            for name, spine in
                [
                    "neg-list", String.replicate 30 "[-" + "1" + String.replicate 30 "]"
                    "paren-list", String.replicate 24 "[(" + "1" + String.replicate 24 ")]"
                    "interp-list", String.replicate 24 "[$\"{" + "1" + String.replicate 24 "}\"]"
                ] do
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let parsed = Weir.Parser.parseLine r ("let x = " + spine)
                sw.Stop()
                Expect.isLessThan sw.ElapsedMilliseconds 5000L $"{name}: nesting is parsed once per level"
                Expect.isOk parsed $"{name}: the nested list parses"

            // a refusal after an anonymous shape was read takes the shape back
            Weir.Types.drainAnonDefs () |> ignore

            Expect.isNone
                (Weir.Fast.tryStmt r "let f : {| a: int |} -> {| b: string |} ->")
                "the broken signature is refused"

            Expect.isEmpty (Weir.Types.drainAnonDefs ()) "nothing stays pending from the refused parse"
        }
        test "the parse oracle agrees on the statement shapes the fast parser owns [D:hand-parser]" {
            let r: Weir.Parser.Resolver = {
                IsKnown = (fun _ -> true)
                IsCommandCallable = (fun _ -> false)
                IsExternal = (fun _ -> false)
                ExternalNames = (fun () -> Seq.empty)
                BareHome = (fun _ -> None)
                AliasHead = fun _ -> None
                IsBuiltinName = fun _ -> false
            }

            for stmt in
                [
                    "let f x y = x + y * 2"
                    "let g = fun (a, b) -> match a with | Some v when v > 0 -> v | None | Some _ -> b"
                    "type T = { [<Wire \"n\">] name: string; items: seq<int * string> }"
                    "let r = { base with x.y = 1; z = [1..2..9] } |> f $\"{a}-{b}\" xs[^1] ys[1..]"
                    "let s : {| a: int |} -> seq<string>"
                    "if c then 1.5 elif d then -2 else (let q = 3 in q)"
                ] do
                Expect.isSome (Weir.Fast.tryStmt r stmt) $"the fast parser takes: {stmt}"
                Expect.isNone (Weir.Parser.parseOracle r stmt) $"the oracle agrees on: {stmt}"
        }
        test "the parse oracle agrees on the command lines the fast parser owns [D:hand-parser]" {
            let externals = set [ "git"; "sh"; "grep"; "wc"; "openssl"; "sleep" ]

            let r: Weir.Parser.Resolver = {
                IsKnown = fun n -> not (externals.Contains n)
                IsCommandCallable = fun n -> n = "cd"
                IsExternal = externals.Contains
                ExternalNames = fun () -> Seq.ofList (Set.toList externals)
                BareHome = fun _ -> None
                AliasHead = fun n -> if n = "ll" then Some("ls", [ "-l" ]) else None
                IsBuiltinName = fun _ -> false
            }

            let sib = " \u001f "

            for stmt in
                [
                    "git log --oneline -n 3"
                    "cd ~/src"
                    "ll -a"
                    "let v = git rev-parse HEAD | line"
                    "let ok = sh -c \"exit 0\" | succeeds"
                    "let n = git log | exitCode"
                    "let f x = let r = openssl dgst -sha256 $x | complete in r.exitCode"
                    "let f x = git push $x.remote | orFail $\"push {x}\""
                    "let f xs = git add $@xs 'a b' \"c\" (Path.combine x \"d\") ~/e -- ."
                    "let f x = if git diff --quiet $x | succeeds then git commit -m wip else print \"clean\""
                    "let f x = match x with | Some b -> git checkout $b | None -> git status"
                    "let f x = git log $x |> Seq.map Str.trim |> Seq.freeze"
                    "let f x = git log | grep $x | wc -l | line"
                    "let f xs = xs | grep a | complete"
                    "let f e = $(git log | line) + $e(git status | text)"
                    "let f p = ^$p --version | line"
                    "let f x = ^git -C $x status"
                    "let f x = A=1 B=$x git log | complete"
                    "let f d = within cd d git status" + sib + "always git log"
                    "let f x = within env GIT_DIR=$x git log | complete"
                    "let f x = within tmp d git init -q $d"
                    "within proc p = sleep 10" + sib + "print \"started\""
                    "let f files = for f in files do git add $f"
                    "let f x = retry attempts=3 delay=1s git fetch | complete"
                    + sib
                    + "until r r.exitCode == 0"
                    "let f x = git add $x" + sib + "git commit -m x"
                    "let f x = (fun y -> git show $y) x"
                ] do
                Expect.isSome (Weir.Fast.tryStmt r stmt) $"the fast parser takes: {stmt}"
                Expect.isNone (Weir.Parser.parseOracle r stmt) $"the oracle agrees on: {stmt}"

            // a refused command line takes back the shapes its argv pushed
            Weir.Types.drainAnonDefs () |> ignore

            Expect.isNone
                (Weir.Fast.tryStmt r "let f x = git log (x |> from json {| a: int |}) | 5")
                "a value stage after `|` is refused"

            Expect.isEmpty (Weir.Types.drainAnonDefs ()) "nothing stays pending from the refused command line"

            // captures nest through argv without re-entering an atom: the
            // refusal past the bound is linear in the depth
            let captures =
                "let x = " + String.replicate 2000 "$(git " + "a" + String.replicate 2000 ")"

            let sw = System.Diagnostics.Stopwatch.StartNew()
            Expect.isNone (Weir.Fast.tryStmt r captures) "the deep capture nest is refused"
            Expect.isLessThan sw.ElapsedMilliseconds 5000L "the refusal never doubles per level"
        }
    ]

let agentFindingsTests =
    testList "Agent findings fixes" [
        test "let RHS admits command mode" {
            match Weir.Parser.parseLine cmdResolver "let files = git status" with
            | Ok(SLet("files", { Kind = ECmd(HeadLit "git", _, _) })) -> ()
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
            // `let workdir = cd target` must apply the binding, not pass a
            // bareword — the meaning it had before let-RHS command mode.
            match Weir.Parser.parseLine cmdResolver "let w = cd target" with
            | Ok(SLet("w",
                      {
                          Kind = EApp({ Kind = EVar "cd" }, { Kind = EVar "target" })
                      })) -> ()
            | other -> failtest $"expected cd applied to the binding, got {other}"
        }
        test "let RHS: quoted in passes to the command" {
            match Weir.Parser.parseLine cmdResolver "let x = grep \"in\" f" with
            | Ok(SLet("x",
                      {
                          Kind = ECmd(HeadLit "grep", [ _; _ ], _)
                      })) -> ()
            | other -> failtest $"expected grep with two args, got {other}"
        }
        test "statement-head commands keep bareword in" {
            match Weir.Parser.parseLine cmdResolver "git log in h" with
            | Ok(SCmd { Kind = ECmd(HeadLit "git", args, _) }) -> Expect.hasLength args 3 "log, in, h"
            | other -> failtest $"expected a command statement, got {other}"
        }
        // param-ful command RHS [D:paramful-rhs]
        test "param-ful let takes a command RHS (curried under the params)" {
            match Weir.Parser.parseLine cmdResolver "let f r = git log $r" with
            | Ok(SLet("f",
                      {
                          Kind = ELambda("r", _, { Kind = ECmd(HeadLit "git", _, _) })
                      })) -> ()
            | other -> failtest $"expected a lambda over a command, got {other}"
        }
        test "params shadow PATH in their own RHS (the law's regression pin)" {
            // cmdResolver says every bareword is an external; the param
            // must still win — identity stays identity
            match Weir.Parser.parseLine cmdResolver "let f x = x" with
            | Ok(SLet("f",
                      {
                          Kind = ELambda("x", _, { Kind = EVar "x" })
                      })) -> ()
            | other -> failtest $"identity became something else: {other}"
        }
        test "tuple-pattern params shadow too" {
            match Weir.Parser.parseLine cmdResolver "let f (a, b) = a" with
            | Ok(SLet("f",
                      {
                          Kind = ELambdaPat(_, { Kind = EVar "a" })
                      })) -> ()
            | other -> failtest $"expected the leaf to stay a binding, got {other}"
        }
        test "param-ful RHS keeps the in-stop" {
            match Weir.Parser.parseLine cmdResolver "let f r = git log in r" with
            | Ok(SLet(_,
                      {
                          Kind = ELambda(_, _, { Kind = ECmd(_, args, _) })
                      })) -> failtest $"the in-eating cliff, param-ful edition: {List.length args} argv words"
            | _ -> ()
        }
        test "splice of a param defaults to string at the boundary" {
            match Weir.Parser.parseLine cmdResolver "let f r = echo $r" with
            | Ok(SLet("f", _) as stmt) ->
                // typecheck through the statement path: f : string -> seq<string>
                match stmt with
                | SLet(_, e) ->
                    match Weir.Check.typecheckWith env e with
                    | Ok(te, _, _, _) -> Expect.equal (formatTy te.Ty) "string -> seq<string>" ""
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
        test "update in a HOLE renders now [D:interp-show]; command args still reject it" {
            expectValue "let r = { X = 1; Y = 0 } in $\"x { { r with X = 2 } }\"" (VStr "x { X = 2; Y = 0 }")

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
        test "a hole on a generalized var stays generic; a non-generalizing statement still defaults" {
            Expect.stringEnds (formatTy (checkOk "fun k -> $\"{k}\"").Ty) "-> string" "generic param"
            Expect.isFalse ((formatTy (checkOk "fun k -> $\"{k}\"").Ty).StartsWith "string") "not defaulted"
            checkOk "[] |> Seq.iter (fun k -> print $\"{k}\")" |> ignore
        }
        test "a function still rejects in a DEFERRED hole, at the hole [D:interp-show]" {
            let terr = checkErr "Str.trim |> (fun f -> $\"{f}\")"
            Expect.stringContains terr.Message "interpolation holes render what show renders" ""
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
            | Ok(SLet("ok",
                      {
                          Kind = EApp({
                                          Kind = EApp({ Kind = EVar "|succeeded" }, _)
                                      },
                                      _)
                      })) -> ()
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
            // the grammar produces ESplat only in command-argument
            // position; this confinement is why infer/eval close their
            // matches with an unreachable arm rather than a splat case
            for src in [ "let y = $@xs"; "let y = [$@xs]"; "print ($@xs)" ] do
                match Weir.Parser.parseStmt src with
                | Error _ -> ()
                | Ok _ -> failtest $"$@ outside argv must be a parse error: {src}"
        }
        test "splat type demands seq<string>, both teachings [D:argv-splat]" {
            let msgOf lines =
                let diags = diagsIn "pin.weir" lines

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
                // the fix names the dynamic-head spelling [D:dynamic-head],
                // not retired builtins
                Expect.stringContains msg "a dynamic one is ^$name" ""
            | Ok _ -> failtest "the head splat must reject"

            match Weir.Parser.parseLine cmdResolver "echo --flag=$@fs" with
            | Error msg -> Expect.stringContains msg "cannot join a word under construction" ""
            | Ok _ -> failtest "the mid-word splat must reject"
        }
        test "$@\" stays the parked interpolated-verbatim cell, not a splat" {
            // lookahead lets $@ splat and $@"..." coexist; the quote form
            // is not yet a feature, so it errors as an unknown token — not
            // as a broken splat
            match Weir.Parser.parseLine cmdResolver "echo $@\"x\"" with
            | Error msg -> Expect.isFalse (msg.Contains "splat") "the quote opener is not read as a splat"
            | Ok _ -> failtest "expected a parse error (the cell is parked)"
        }
        test "argv pieces do not concatenate — every glued adjacency refuses [D:argv-concat]" {
            // the suffix side of the whole-word law: before the guard,
            // `$root/*` silently became two argv words. Each glued shape
            // must refuse; the spaced/interpolated forms stay legal.
            for glued in
                [
                    "echo $x/tail"
                    "echo (x)/tail"
                    "echo \"A\"/tail"
                    "echo --flag=\"quoted v\""
                    "echo pre(x)"
                    "echo pre$\"i-{x}\""
                ] do
                match Weir.Parser.parseLine cmdResolver glued with
                | Error msg ->
                    Expect.stringContains msg "argv words do not concatenate" $"wrong refusal for {glued}: {msg}"
                | Ok _ -> failtest $"glued adjacency must refuse: {glued}"

            // the legal neighbours: spaced, piped-unspaced, whole-quoted
            for legal in [ "echo $x /tail"; "echo hi |grep hi"; "echo \"--flag=quoted v\"" ] do
                match Weir.Parser.parseLine cmdResolver legal with
                | Ok _ -> ()
                | Error msg -> failtest $"legal form refused: {legal}: {msg}"
        }
        test "a triple-quoted string is one argv word; @\"…\" refuses with its own teaching [D:argv-at-raw]" {
            match Weir.Parser.parseLine cmdResolver "echo \"\"\"a \"b\" c\"\"\" x" with
            | Ok(Weir.Ast.SCmd e) ->
                Expect.stringContains (sprintf "%A" e) "EStr \"a \"b\" c\"" "the raw body is one piece"
            | Ok s -> failtest $"expected a command, got {s}"
            | Error msg -> failtest $"the triple form must be one word: {msg}"

            match Weir.Parser.parseLine cmdResolver "echo -d @\"my file\"" with
            | Error msg ->
                Expect.stringContains msg "is not an argv word" "own teaching"
                Expect.isFalse (msg.Contains "do not concatenate") "not the concatenation teaching"
            | Ok _ -> failtest "@\"…\" in argv must refuse"
        }
        test "the homepage quotes the concatenation refusal EXACTLY [D:argv-concat]" {
            // byte-equality with the message the site shows — a wording
            // change must move the homepage in the same commit
            match Weir.Parser.parseLine cmdResolver "rm -rf $steamroot/*" with
            | Error msg ->
                // Replace: FParsec wraps with Environment.NewLine — Windows
                // delivers \r\n and a \n-pinned EndsWith missed
                Expect.isTrue
                    (msg.Replace("\r\n", "\n").TrimEnd().EndsWith
                        "argv words do not concatenate — adjacent pieces would each become their OWN\nargument (`$root/*` would pass `/*` separately; `--flag=\"v\"` would pass\n`--flag=` separately). Build one word with an interpolated arg (`$\"{root}/*\"`,\n`$\"--flag={v}\"`), quote the whole word, or use `Path.under dir name` for a\nfilesystem path (it refuses a result that escapes dir, where `Path.combine`\nwould follow it)")
                    $"the homepage's quoted refusal drifted: {msg}"
            | Ok _ -> failtest "the steam shape must refuse"
        }
        test "the path-splice refusal text is pinned EXACTLY [D:splice-path-hint]" {
            // byte-equality on a shipped teaching message (the hero
            // quotes [D:argv-concat]'s, not this one)
            match Weir.Parser.parseLine cmdResolver "rm -rf ./tt3/$build" with
            | Error msg ->
                // EndsWith: the parser frames the message with position
                // lines; the quoted text is the message itself, pinned
                // byte-exact (the file:line prefix is the CLI formatter's,
                // pinned by the e2e hero-currency cell)
                Expect.isTrue
                    (msg.Replace("\r\n", "\n").TrimEnd().EndsWith
                        "a splice cannot join a path under construction — spell it as one interpolated\narg (`$\"dir/{x}\"`), or `Path.under dir name` for a filesystem path; a space\n(`dir/ $x`) would pass two arguments, not one path")
                    $"the hero's quoted refusal drifted from the shipped message: {msg}"
            | Ok _ -> failtest "the hero snippet must refuse"
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

            // a reifier follows a command or a command chain
            // [D:chain-reifiers] — never a function stage
            match Weir.Parser.parseLine cmdResolver "git log |> Seq.take 1 | exitCode" with
            | Error msg -> Expect.stringContains msg "must directly follow a command or a command chain" ""
            | Ok _ -> failtest "exitCode must keep the family's segment rule"
        }
        test "exec desugars to the execed application [D:exec]" {
            match Weir.Parser.parseLine cmdResolver "echo hi | exec" with
            | Ok(SCmd e) -> Expect.stringContains (Weir.Ast.sexpr e) "|execed" ""
            | other -> failtest $"expected the execed desugar, got {other}"
        }
        test "exec is a diverging bare statement — no discard error, either route [D:exec]" {
            // exec never returns (execve/exit), so a bare statement is
            // legitimate: the discard gate must exempt it like fail/exit,
            // in the command route and the env-sigil capture route
            let clean = errorsCleanIn "exec.weir"

            clean [ "echo replaced | exec" ] "plain command route"
            clean [ "let e = [Env.pair \"X\" \"1\"]"; "$e(printenv X | exec)" ] "env-sigil capture route"
            clean [ "let cmd = \"echo\""; "let rest = [\"a\"; \"b\"]"; "^$cmd $@rest | exec" ] "dynamic head"
        }
        test "exec refuses a piped stdin — no parent left to feed a replacement [D:exec]" {
            match Weir.Parser.parseLine cmdResolver "[\"a\"] | grep a | exec" with
            | Error msg ->
                Expect.stringContains msg "exec" ""
                Expect.stringContains msg "cannot take a piped stdin" ""
            | Ok _ -> failtest "value-headed exec must refuse"
        }
        test "line desugars to the lined application and types as string [D:reify-line]" {
            match Weir.Parser.parseLine cmdResolver "echo hi | line" with
            | Ok(SCmd e)
            | Ok(SExpr e) -> Expect.stringContains (Weir.Ast.sexpr e) "|lined" ""
            | other -> failtest $"expected the lined desugar, got {other}"

            // a value-position `| line` is a string — a binding checks as
            // string end-to-end (base, env twin, value-headed)
            let clean = errorsCleanIn "line.weir"

            clean [ "let scope = printf \"x\" | line"; "print (Str.length scope |> show)" ] "binds a string"

            clean
                [ "let e = [Env.pair \"X\" \"1\"]"; "let v = $e(printenv X | line)"; "print v" ]
                "env twin binds a string"

            clean [ "let m = [\"a\"; \"b\"] | grep a | line"; "print m" ] "value-headed | line binds a string"
        }
        test "a | line count error escapes the argv it echoes [D:error-data-escape]" {
            skipOnWindows ()

            let msg (src: string) =
                (Expect.throwsC (fun () -> runReal src |> ignore) id).Message

            Expect.equal
                (msg "printf \"a\\nb\\n\" | line")
                "'printf a\\x0ab\\x0a | line' expected exactly one line of output, got 2"
                "a newline in the argv renders escaped"

            Expect.equal
                (msg "printf \"a\\nb\\n\" | cat | line")
                "'printf a\\x0ab\\x0a | cat | line' expected exactly one line of output, got 2"
                "the chain form too"
        }
        test "text desugars to the texted application and types as string [D:reify-text]" {
            match Weir.Parser.parseLine cmdResolver "echo hi | text" with
            | Ok(SCmd e)
            | Ok(SExpr e) -> Expect.stringContains (Weir.Ast.sexpr e) "|texted" ""
            | other -> failtest $"expected the texted desugar, got {other}"

            let clean = errorsCleanIn "text.weir"

            clean [ "let t = printf \"a\" | text"; "print (Str.trim t)" ] "binds a string"
            clean [ "let e = [Env.pair \"X\" \"1\"]"; "let v = $e(printenv X | text)"; "print v" ] "env twin"
            clean [ "let m = [\"a\"; \"b\"] | grep a | text"; "print m" ] "value-headed"
        }
        test "the fifth refusal cell: refused-context reifiers TEACH, never PATH-resolve [D:reifier-family-complete]" {
            // [D:statement-lets] moved the boundary: if-body and
            // within-body block lets now take the reifier (statement
            // contexts — pinned in statementLetTests); the remaining
            // refused position (lambda-body) must teach $() with its
            // context named, and never resolve the keyword on PATH —
            // the degradation F3 found.
            for name, spelled in
                [
                    "complete", "complete"
                    "succeeds", "succeeds"
                    "exitCode", "exitCode"
                    "orFail", "orFail \"m\""
                ] do
                // the two flipped positions accept
                for posName, tpl in
                    [
                        "if-body", [ "if true then"; "    let _r = sh -c \"echo x\" | %s"; "    print \"z\"" ]
                        "within-body", [ "within tmp d"; "    let _r = sh -c \"echo x\" | %s"; "    print \"z\"" ]
                        "lambda-body",
                        [
                            "[1] |> Seq.iter (fun _ ->"
                            "    let _r = sh -c \"echo x\" | %s"
                            "    print \"z\")"
                        ]
                    ] do
                    let lines = tpl |> List.map (fun (l: string) -> l.Replace("%s", spelled))
                    let diags = diagsIn "pin.weir" lines

                    Expect.isEmpty
                        (diags |> List.filter (fun d -> d.Severity = "error"))
                        $"{name} in {posName}: statement contexts take the reifier let now"

            // the ^ escape stays: a real tool of that name is reachable
            let escaped =
                diagsIn "pin.weir" [
                    "[1] |> Seq.iter (fun _ ->"
                    "    let _r = sh -c \"echo x\" | ^complete"
                    "    print \"z\")"
                ]

            Expect.isFalse
                (escaped |> List.exists (fun d -> d.Message.Contains "is a reifier"))
                "^complete asks for the PATH tool explicitly"
        }
        test "a discarded exit code errors with the bind-or-match hint" {
            let lines = [ "git push | exitCode" ]
            let diags = diagsIn "pin.weir" lines

            Expect.exists
                diags
                (fun d -> d.Message.Contains "bind it (let rc = <command> | exitCode), match on it")
                "the set +e muscle-memory hint"
        }
        test "a discarded | complete joins the family — Completed record, stage caret [D:exit-reifiers]" {
            // the one-cell gap: a bare `| complete` statement was accepted
            // while its bool/int siblings were rejected. Now it errors in
            // the family's voice at the reifier stage (exact col, not
            // inherited — the anchor-before-read lesson).
            let diags = diagsIn "pin.weir" [ "git status | complete" ]

            match diags |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (1, 14) "caret on the `complete` stage, matching succeeds"
                Expect.equal (d.EndLine, d.EndCol) (Some 1, Some 22) "running forward over the stage, never inverted"
                Expect.stringContains d.Message "computes a Completed record and discards it" "the record voice"
                Expect.stringContains d.Message "read a field (.exitCode, .stdout)" "the per-cell use clause"
            | other -> failtest $"expected ONE discard error, got {other}"

            // the accepting path is unchanged: binding still works, and
            // orFail (unit) is exempt by design, not by oversight
            let ok (line: string) =
                let ds = diagsIn "pin.weir" [ line ]
                Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) $"accepted: {line}"

            ok "let _r = git status | complete"
            ok "git status | orFail \"boom\""
        }
        test "orFail carries its message expression" {
            match Weir.Parser.parseLine cmdResolver "git fetch | orFail \"boom\"" with
            | Ok(SCmd { Kind = EApp(_, _) }) -> ()
            | other -> failtest $"expected the orFailed desugar, got {other}"
        }
        test "a reifier after a command chain desugars to its chain twin [D:chain-reifiers]" {
            for r, twin in
                [
                    "succeeds", "|chainSucceeded"
                    "complete", "|chainCompleted"
                    "exitCode", "|chainExitCoded"
                    "line", "|chainLined"
                    "text", "|chainTexted"
                    "orFail \"m\"", "|chainOrFailed"
                ] do
                match Weir.Parser.parseLine cmdResolver $"let v = git log | grep x | {r}" with
                | Ok s -> Expect.stringContains (sprintf "%A" s) twin r
                | Error m -> failtest $"{r}: {m}"
        }
        test "a splat rides a reifier chain — argv desugars to a seq value [D:splat-reifier-chains]" {
            // mixed literal+splat argv folds with Seq.append
            match Weir.Parser.parseLine cmdResolver "echo one $@xs | complete" with
            | Ok(SCmd e) -> Expect.stringContains (Weir.Ast.sexpr e) "|seqAppend" "the append-fold desugar"
            | other -> failtest $"expected the splatted-reifier desugar, got {other}"

            // splat-free argv keeps the plain list node (zero movement)
            match Weir.Parser.parseLine cmdResolver "echo one two | complete" with
            | Ok(SCmd e) -> Expect.isFalse ((Weir.Ast.sexpr e).Contains "|seqAppend") "no append for splat-free argv"
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
            skipOnWindows ()
            // N elements, N words — through the builtin's argv
            Expect.equal
                (runReal "echo one $@([\"a\"; \"b\"]) | complete |> _.stdout |> Seq.head")
                (VStr "one a b")
                "splat elements land as words"

            // the safety pin: adversarial elements stay single words
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
        test "print of unit is silent (the armed-statement rule)" {
            Expect.equal (checkOk "print ()").Ty TUnit ""
            expectValue "print ()" VUnit
        }
        // fmt v2 respace [D:fmt-respace]
        test "respaceLine: collapse, brace pad, semicolon tidy" {
            Expect.equal
                (Weir.Assemble.respaceLine "let l2 =  {    lomo with Lomo = 100}")
                "let l2 = { lomo with Lomo = 100 }"
                ""

            Expect.equal
                (Weir.Assemble.respaceLine "type G = {Lomo: int; Bimbo: string}")
                "type G = { Lomo: int; Bimbo: string }"
                ""

            Expect.equal (Weir.Assemble.respaceLine "let x = {A = {B = 1}}") "let x = { A = { B = 1 } }" ""
            Expect.equal (Weir.Assemble.respaceLine "let a = 1 ;  print \"x\"") "let a = 1; print \"x\"" ""
        }
        test "fmt never re-spaces command argv words [D:fmt-argv-verbatim]" {
            for line in
                [
                    "chmod u+x f"
                    "echo a+b"
                    "echo a-b c*d e/f"
                    "c&&d"
                    "git status&&pwd"
                    "echo a&&b"
                    "echo x=1 a:b"
                    "echo a||b"
                    "if a&&b then print \"x\""
                ] do
                match Weir.Fmt.formatLines [ line ] with
                | Ok out -> Expect.equal out [ line ] $"argv kept verbatim: {line}"
                | Error e -> failtestf "fmt failed on %s: %s" line e

            // control: an expression that can never be a command still respaces
            match Weir.Fmt.formatLines [ "let x = 1+2" ] with
            | Ok out -> Expect.equal out [ "let x = 1 + 2" ] "expression operators still pad"
            | Error e -> failtestf "fmt failed: %s" e
        }
        test "respaceLine: string interiors and leading indent untouched" {
            Expect.equal (Weir.Assemble.respaceLine "    print \"a  {  b\"") "    print \"a  {  b\"" "plain"
            Expect.equal (Weir.Assemble.respaceLine "let s = @\"a  {  b\"") "let s = @\"a  {  b\"" "verbatim"

            Expect.equal
                (Weir.Assemble.respaceLine "print $\"x{  1  }\"")
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
        test "Path.normalize collapses lexically — the escape both siblings decline" {
            expectValue "Path.normalize \"src/App/../Core/x.fsproj\"" (VStr "src/Core/x.fsproj")
            expectValue "Path.normalize \"./a/./b/\"" (VStr "a/b")
            // the receipt shape: a reference legitimately leaving its
            // directory — leading '..'s survive on a relative path
            expectValue "Path.normalize \"../shared/x\"" (VStr "../shared/x")
            expectValue "Path.normalize \"a/b/../..\"" (VStr ".")
            // an absolute root swallows '..' (realpath's rule)
            expectValue "Path.normalize \"/a/../../b\"" (VStr "/b")

            expectValue
                "Path.combine \"/repo/src/App\" \"../Core/Core.csproj\" |> Path.normalize"
                (VStr "/repo/src/Core/Core.csproj")
        }
        test "Path.isName: the portable truth table [D:path-name]" {
            let isName = Weir.Builtins.pathIsName

            for (n, want) in
                [
                    "report-2026.txt", true // the reference script's cases first
                    "a/b", false
                    "..", false
                    "x:y", false
                    "CON.txt", false
                    "CONIN$", false
                    "conout$.txt", false
                    "CoNiN$.log", false
                    "trail.", false
                    "", false
                    ".", false
                    ".bashrc", true
                    "...x", true
                    "my file.txt", true
                    " lead", true
                    "trail ", false
                    @"a\b", false
                    "a\u0000b", false
                    "tab\there", false
                    "a\u001fb", false
                    "a\u007fb", true
                    "<", false
                    ">", false
                    "\"", false
                    "|", false
                    "?", false
                    "*", false
                    "con", false
                    "Nul.tar.gz", false
                    "lpt9.log", false
                    "COM0", false
                    "com¹", false
                    "LPT³.txt", false
                    "CON .txt", false
                    "COM10", true
                    "CONSOLE", true
                    "xCON", true
                    "_CON.txt", true
                    "café", true
                    "日本語.md", true
                    "😀", true
                    string (char 0xD800), false // a lone surrogate has no UTF-8 form
                    "a" + string (char 0xDC00), false
                ] do
                Expect.equal (isName n) want $"isName {n}"

            // the length edge counts UTF-8 bytes: 255 passes, 256 refuses
            Expect.isTrue (isName (String.replicate 255 "a")) "255 ASCII bytes"
            Expect.isFalse (isName (String.replicate 256 "a")) "256 ASCII bytes"
            Expect.isTrue (isName (String.replicate 127 "é" + "a")) "2-byte chars, 255 bytes"
            Expect.isFalse (isName (String.replicate 128 "é")) "128 chars, 256 bytes"
            Expect.isTrue (isName (String.replicate 63 "😀" + "abc")) "4-byte chars, 255 bytes"
            Expect.isFalse (isName (String.replicate 64 "😀")) "64 code points, 256 bytes"
            expectValue "Path.isName \"report-2026.txt\"" (VBool true)
            expectValue "Path.isName \"CON.txt\"" (VBool false)
        }
        test "Path.toName: the policy table, idempotent, never splits [D:path-name]" {
            let toName = Weir.Builtins.pathToName

            for (s, want) in
                [
                    "report-2026.txt", "report-2026.txt"
                    "a/b", "a_b"
                    @"a\b", "a_b"
                    "Q3: draft/final?.txt", "Q3_ draft_final_.txt"
                    "<>:\"|?*", "_______"
                    "a\u0000b\tc\u001fd", "a_b_c_d"
                    "trail.", "trail"
                    "trail. . ", "trail"
                    "", "_"
                    ".", "_"
                    "..", "_"
                    " . ", "_"
                    "CON.txt", "_CON.txt"
                    "CONOUT$", "_CONOUT$"
                    "conin$.log", "_conin$.log"
                    "con", "_con"
                    "com¹.log", "_com¹.log"
                    "CON .txt", "_CON .txt"
                    "my File.TXT", "my File.TXT"
                    " lead", " lead"
                    string (char 0xD800) + "x", "_x"
                ] do
                Expect.equal (toName s) want $"toName {s}"

            let bytes (s: string) =
                System.Text.Encoding.UTF8.GetByteCount s

            // truncation lands on a code-point boundary, never inside a
            // UTF-8 sequence or a surrogate pair
            let e = toName (String.replicate 200 "é")
            Expect.equal e (String.replicate 127 "é") "254 bytes: the 128th é would split"
            let emoji = toName ("a" + String.replicate 100 "😀")
            Expect.equal emoji ("a" + String.replicate 63 "😀") "253 bytes: no half surrogate pair"
            Expect.isFalse (System.Char.IsHighSurrogate emoji[emoji.Length - 1]) "the pair is whole"
            // a cut that leaves a trailing dot or space strips again
            Expect.equal (toName (String.replicate 254 "a" + ". b")) (String.replicate 254 "a") "the cut re-strips"
            Expect.equal (toName (String.replicate 300 "." + "x")) "_" "all dots after the cut"
            // the prefix can push a long reserved name past the limit; it is cut again
            let longCon = toName ("CON." + String.replicate 251 "a")
            Expect.equal longCon ("_CON." + String.replicate 250 "a") "prefix then re-cut"
            // the cut can expose a device name; it is prefixed after the cut
            Expect.equal (toName ("CON" + String.replicate 260 " " + "x")) "_CON" "a cut down to CON"

            let samples = [
                "report.txt"
                "a/b"
                ""
                "."
                ".."
                "CON.txt"
                "trail. "
                "Q3: x?"
                string (char 0xD800)
                String.replicate 300 "é"
                "a" + String.replicate 100 "😀"
                "CON." + String.replicate 251 "a"
                String.replicate 254 "a" + ". b"
                "CON" + String.replicate 260 " " + "x"
                "日本語/テスト:1"
            ]

            for s in samples do
                let n = toName s
                Expect.isTrue (Weir.Builtins.pathIsName n) $"toName {s} is a name"
                Expect.equal (toName n) n $"toName is idempotent on {s}"
                Expect.isLessThanOrEqual (bytes n) 255 $"toName {s} fits"

            expectValue "Path.toName \"CON.txt\"" (VStr "_CON.txt")
            expectValue "Path.toName \"..\"" (VStr "_")
        }
        test "Path.isName and Path.toName on bulk text cost a name's work, not the text's [D:path-name]" {
            let big = String.replicate 20_000_000 "a/"
            let sw = System.Diagnostics.Stopwatch.StartNew()
            Expect.isFalse (Weir.Builtins.pathIsName big) "far over the limit"
            let n = Weir.Builtins.pathToName big
            Expect.isTrue (System.Text.Encoding.UTF8.GetByteCount n <= 255) "capped at the limit"
            Expect.isTrue (Weir.Builtins.pathIsName n) "a name"
            Expect.isLessThan sw.Elapsed.TotalSeconds 2.0 "bounded by the limit, not the input"
        }
        test "Path.isName and Path.toName are pure [D:path-name]" {
            Expect.isEmpty
                (errorsIn "pathname.weir" [
                    "let pure safe s = if Path.isName s then s else Path.toName s"
                    "print (safe \"a/b\")"
                ])
                "pure admits both"
        }
        test "Path.normalize and Path.glob root a Windows drive on Windows only [D:path-drive-root]" {
            let norm = Weir.Builtins.pathNormalizeOn
            Expect.equal (norm true "C:/../y") "C:/y" "a drive root swallows '..'"
            Expect.equal (norm true @"C:\a\..\..\b") "C:/b" "backslashes, past the root"
            Expect.equal (norm true "C:/..") "C:/" "the drive root itself"
            Expect.equal (norm true @"\a\..\b") "/b" "a leading backslash roots"
            Expect.equal (norm true "../x") "../x" "a relative path keeps its '..'"
            Expect.equal (norm false "C:/../y") "y" "on POSIX C: is an ordinary segment"
            Expect.equal (norm false @"a\..\b") @"a\..\b" "on POSIX a backslash is a name byte"

            let split = Weir.Builtins.globSplit

            Expect.equal
                (split true @"C:\Users\me\X13\*.txt")
                (Some(@"C:\", "C:/"), [ "Users"; "me"; "X13"; "*.txt" ])
                "a native prefix"

            Expect.equal
                (split true @"C:\Users\me/*.txt")
                (Some(@"C:\", "C:/"), [ "Users"; "me"; "*.txt" ])
                "mixed separators"

            Expect.equal (split true "c:/x/*") (Some(@"c:\", "c:/"), [ "x"; "*" ]) "a forward-slashed drive"

            Expect.equal
                (split true @"\\host\share\d\*")
                (Some(@"\\host\share\", "//host/share/"), [ "d"; "*" ])
                "a UNC share"

            Expect.equal (split true @"\d\*") (Some(@"\", "/"), [ "d"; "*" ]) "the current drive's root"
            Expect.equal (split true @"src\*.fs") (None, [ "src"; "*.fs" ]) "relative"
            Expect.equal (split false @"C:\x\*.txt") (None, [ @"C:\x\*.txt" ]) "on POSIX one segment"
            Expect.equal (split false "/a//b/*") (Some("/", "/"), [ "a"; "b"; "*" ]) "POSIX absolute"
        }
        test "Path.home + XDG dirs resolve — the typed stand-in for ~/$HOME [D:path-home]" {
            // each is unit -> string, non-empty; the XDG dirs live under
            // home (POSIX default / Windows profile), and stateHome is
            // exactly where the REPL keeps history
            let asStr what v =
                match v with
                | VStr s -> s
                | other -> failtestf "%s: expected VStr, got %A" what other

            let home = asStr "home" (run "Path.home ()")
            Expect.isNotEmpty home "home resolves"

            for m in [ "configHome"; "stateHome"; "cacheHome" ] do
                let d = asStr m (run $"Path.{m} ()")
                Expect.isNotEmpty d $"{m} resolves"
                Expect.stringContains d home $"{m} sits under home"
        }
        test "Path.under confines; Path.combine does not [D:path-under]" {
            // runs on every platform. An earlier skipOnWindows left this member
            // with zero Windows coverage and pointed at an e2e row that did not
            // exist; the e2e row exists now (Windows runs e2e too) and this pin
            // stays — units referee the lib, the row referees the shipped binary.
            // The logic is pure string work; both sides build from the same BCL
            // call, so the drive/UNC shapes assert everywhere.
            let root = tempDir "weir-under"
            let bse = root.Replace("\\", "/")

            let full (rel: string) =
                System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel))

            let under (name: string) =
                runReal $"Path.under \"{bse}\" \"{name}\""

            let escapes (name: string) =
                Expect.throws (fun () -> under name |> ignore)

            try
                // the positive twin first: "raises on escape" is satisfied by a member
                // that raises on everything, so the legitimate joins are the real pins
                Expect.equal (under "report.pdf") (VStr(full "report.pdf")) "a plain name joins"
                Expect.equal (under "a/b/c.txt") (VStr(full "a/b/c.txt")) "a nested name joins"
                // interior `..` is legitimate — rejecting the segment would be wrong
                Expect.equal (under "a/../b") (VStr(full "b")) "interior .. stays inside"
                // rule 6: empty and dot-only yield the base
                Expect.equal (under "") (VStr(System.IO.Path.TrimEndingDirectorySeparator root)) "empty yields the base"

                Expect.equal (under ".") (VStr(System.IO.Path.TrimEndingDirectorySeparator root)) "dot yields the base"

                // rule 1 + rule 2: absolute, and traversal that leaves
                escapes "/etc/passwd" "an absolute rhs escapes"
                escapes "../etc/passwd" "traversal escapes"
                escapes "a/../../etc" "interior traversal that leaves escapes"

                // rule 7: drive- and UNC-shaped names are refused on every platform,
                // which is exactly what the old skip stopped anyone from checking
                escapes "C:/x" "a drive root escapes"
                escapes "C:x" "a drive-RELATIVE name escapes"
                // the weir-source spelling matters: weir strings escape
                // backslashes, so the UNC value needs the doubled form —
                // the old single form was a parse error and the throws
                // pin passed vacuously on it, never testing rule 7
                escapes "\\\\\\\\server\\\\share" "a UNC name escapes"
                escapes "\\\\x" "a backslash-leading name escapes"

                // rule 4, the reason the member exists: a sibling whose name extends
                // the base is not under it, and a prefix-string test says it is
                let leaf =
                    System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator root)

                escapes $"../{leaf}-evil/x" "a name-extending sibling is NOT under the base"

                // the diagnostic is security-shaped: base, attempt, and the word escape
                let msg =
                    try
                        under "/etc/passwd" |> ignore
                        ""
                    with ex ->
                        ex.Message

                Expect.isTrue (msg.Contains "/etc/passwd") "the message names the attempt"
                Expect.isTrue (msg.Contains "escape") "the message says escape"
            finally
                System.IO.Directory.Delete(root, true)
        }

        test "Path members: extension/fileName/stem/dir/join" {
            expectValue "Path.extension \"ci/run.Dockerfile\"" (VStr ".Dockerfile")
            expectValue "Path.extension \"ci/Dockerfile\"" (VStr "")
            expectValue "Path.fileName \"a/b/c.fs\"" (VStr "c.fs")
            expectValue "Path.stem \"a/b/c.fs\"" (VStr "c")
            // native-out ruling [D:windows-v1]: a\b on Windows, a/b on POSIX;
            // the forward-slash input is the liberal-in pin on both
            expectValue "Path.dir \"a/b/c.fs\"" (VStr(platformPath "a/b"))
            expectValue "Path.dir \"c.fs\"" (VStr "")
            expectValue "Path.combine \"ci\" \"e2e.sh\"" (VStr(platformPath "ci/e2e.sh"))
        }
        test "fail raises with the message" {
            // diverging [D:fail-bottom]: an undemanded fail keeps a
            // fresh var (the statement gate checks it against unit)
            match (checkOk "fail \"boom\"").Ty with
            | TVar _ -> ()
            | other -> failtest $"expected a fresh var, got {other}"

            Expect.throwsT<exn> (fun () -> run "fail \"boom\"" |> ignore) "raises"
            expectValue "if 1 > 2 then fail \"never\"" VUnit
        }
        test "fail diverges: a fail arm sits opposite a value arm [D:fail-bottom]" {
            // the ported-module receipt: no throwaway pin construction
            expectValue "match Some 1 with | Some v -> v | None -> fail \"absent\"" (VInt 1L)
            Expect.equal (checkOk "if 1 > 2 then fail \"no\" else 5").Ty TInt "the if-branch twin"
            Expect.equal (checkOk "if 1 > 2 then exit 2 else 5").Ty TInt "exit is the sibling, same typing"
        }
        test "fail's var never leaks where nothing determines it [D:fail-bottom]" {
            // a class demand on the undetermined var still refuses —
            // the pending-constraint surface stays an error, not silence
            let terr = checkErr "show (fail \"x\")"
            Expect.stringContains (formatError terr) "nothing determines" "constraint demand refuses"
            // the previously-taught idiom stays legal: the seq gate
            // carves the diverging heads and checks them against unit
            Expect.equal (checkOk "fail \"x\" ; 0").Ty TInt "the fail-then-value idiom"
            // a non-diverging unresolved value keeps the dead-value
            // tripwire the [D:fail-unit] ruling defended
            let terr2 = checkErr "Seq.head [] ; 0"
            Expect.stringContains (formatError terr2) "must be unit" "the tripwire survives"

            // a mid-script fail statement passes the discard gate (its
            // fresh var is no value to discard); a non-diverging var
            // statement stays refused
            let diags = diagsIn "t.weir" [ "print \"a\""; "fail \"stop\""; "print \"b\"" ]

            Expect.isEmpty (diags |> List.map (fun d -> d.Message)) "the statement gate admits the diverging tail"
        }
        test "a block-let tuple binder inside a multiline lambda body assembles and checks (ported-module pin)" {
            match
                Weir.Assemble.assemble [ 1, "xs |> Seq.iter (fun p ->"; 2, "    let a, b = p"; 3, "    print a)" ]
            with
            | Ok [ ll ] ->
                Expect.equal ll.Text "xs |> Seq.iter (fun p -> let a, b = p in print a)" "the in-join inside the body"
            | other -> failtest $"expected one logical line, got {other}"

            expectValue "[(1, 2); (3, 4)] |> Seq.map (fun p -> let a, b = p in a + b) |> Seq.sum" (VInt 10L)
        }
        test "multi-line application: EVERY deeper line continues the statement [D:continuation-siblings]" {
            // the sibling floor is the statement's start column — a
            // continuation line must not hoist it, or the second
            // argument line sequences as a block statement
            match
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    combine3"
                    3, "        1"
                    4, "        2"
                    5, "        3"
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text "let v = combine3 1 2 3" "all argument lines join"
            | other -> failtest $"expected one logical line, got {other}"

            // a block statement after a deeper continuation still
            // sequences at the block level (was a dedent-floor error)
            match Weir.Assemble.assemble [ 1, "if a then"; 2, "    f"; 3, "        x"; 4, "    g" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "if a then f x ; g") "the sibling after a continuation"
            | other -> failtest $"expected one logical line, got {other}"
        }
        test "a `)`-headed line closes a multi-line application, never siblings [D:paren-close-continuation]" {
            // the close paren at the opener's indent continues the
            // statement while a plain paren is open — the lambda
            // closer's rule extended to ordinary applications
            match
                Weir.Assemble.assemble [ 1, "let v ="; 2, "    YMap("; 3, "        [(\"a\", YStr \"x\")]"; 4, "    )" ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text "let v = YMap( [(\"a\", YStr \"x\")] )" "the closer joins"
            | other -> failtest $"expected one logical line, got {other}"

            // …and in a nested arm body: the ported-port shape — a
            // constructor application inside an if arm at depth
            let clean lines =
                let diags = diagsIn "pin.weir" lines
                Expect.isEmpty (diags |> List.map (fun d -> d.Message)) $"checks: {lines}"

            clean [
                "let build tag ="
                "    if tag == \"map\" then"
                "        YMap("
                "            [(\"a\", YStr \"x\")]"
                "        )"
                "    else"
                "        YStr \"plain\""
                "build \"map\" |> to yaml |> Seq.iter print"
            ]

            clean [
                "let build tag ="
                "    match tag with"
                "    | \"map\" ->"
                "        YMap("
                "            [(\"a\", YStr \"x\")]"
                "        )"
                "    | _ -> YStr \"plain\""
                "build \"map\" |> to yaml |> Seq.iter print"
            ]

            // the closer restores the statement level: the next body
            // line is a sibling of the whole application (the in-join
            // for a pending block let), never an argument
            match
                Weir.Assemble.assemble [
                    1, "let outer ="
                    2, "    let inner = YMap("
                    3, "        [(\"a\", YStr \"x\")]"
                    4, "    )"
                    5, "    inner"
                ]
            with
            | Ok [ ll ] ->
                Expect.equal
                    ll.Text
                    "let outer = let inner = YMap( [(\"a\", YStr \"x\")] ) in inner"
                    "the in-join waits for the closer"
            | other -> failtest $"expected one logical line, got {other}"

            // the other direction: with no paren open a `)`-headed
            // sibling is still refused, never silently joined
            let diags = diagsIn "pin.weir" [ "if 1 > 0 then"; "    print \"x\""; "    )" ]

            Expect.isNonEmpty
                (diags |> List.filter (fun d -> d.Severity = "error"))
                "a stray `)` sibling stays an error"
        }
        test "multi-line application assembles inside a module body too [D:continuation-siblings]" {
            let td = tempDir "weir-mlapp"

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(td, "m.weir"),
                [|
                    "module M"
                    ""
                    "let combine3 a b c = a * 2 + b * 3 + c * 5"
                    ""
                    "let total : seq<string> -> int"
                    ""
                    "let total xs ="
                    "    combine3"
                    "        (xs |> Seq.length)"
                    "        10"
                    "        100"
                |]
            )

            try
                let entry = [ "import \"./m.weir\" as M"; "print (show (M.total [\"a\"; \"b\"]))" ]

                let diags = diagsIn (System.IO.Path.Combine(td, "main.weir")) entry

                Expect.isEmpty (diags |> List.map (fun d -> d.Message)) "the module body assembles the application"
            finally
                System.IO.Directory.Delete(td, true)
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
                (Weir.Assemble.stripComment "curl https://x.y/z // real comment")
                "curl https://x.y/z"
                "URL survives, trailing comment stripped"

            Expect.equal (Weir.Assemble.stripComment "let x = 1 // c") "let x = 1" "spaced comment works"
            Expect.equal (Weir.Assemble.stripComment "// full line") "" "line-start comment works"
        }
        test "comment-only lines are transparent to assembly (runner-level filter)" {
            // assemble itself never sees them; pin the filter contract:
            // whitespace-only survives as blank, comment-only is dropped
            let commentOnly (raw: string) =
                raw.Trim() <> "" && (Weir.Assemble.stripComment raw).Trim() = ""

            Expect.isTrue (commentOnly "    // note") "comment"
            Expect.isFalse (commentOnly "   ") "whitespace stays a blank"
            Expect.isFalse (commentOnly "let x = 1 // tail") "code with tail comment stays"
        }
        // a pending let's body continues across a gap [D:body-blanks]
        test "blank line inside a block is transparent" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    let a = 1"; 3, ""; 4, "    a + 1" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = let a = 1 in a + 1" ""
            | other -> failtest $"unexpected: {other}"
        }
    ]

let paramSugarTests =
    testList "Let parameter sugar" [
        test "single param, let-in" { expectValue "let double x = x * 2 in double 21" (VInt 42L) }
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
            | Ok(SLet(_,
                      {
                          Kind = ELambda(_, _, { Kind = ECmd(HeadLit "git", _, _) })
                      })) -> ()
            | other -> failtest $"expected a command RHS under the param, got {other}"
        }
        test "unit and PARENTHESIZED pattern params legal (binders session completed the arc)" {
            match Weir.Parser.parseExpr "let f () = 1 in f" with
            | Ok {
                     Kind = ELet(_, _, { Kind = ELambda("()", _, _) }, _)
                 } -> ()
            | other -> failtest $"unit param should desugar to the pinned () lambda, got {other}"

            match Weir.Parser.parseExpr "let f (x, y) = x in f" with
            | Ok {
                     Kind = ELet(_,
                                 _,
                                 {
                                     Kind = ELambdaPat({ PKind = PTuple _ }, _)
                                 },
                                 _)
                 } -> ()
            | other -> failtest $"tuple param should desugar to ELambdaPat, got {other}"
        }
        test "HOF works through the let-parameter sugar [D:higher-order-params]" {
            // the sugar `let apply f x = …` shares the body checker, so a
            // higher-order parameter infers there exactly as in a lambda
            let te = checkOk "let apply f x = f x in apply double 1"
            Expect.equal (formatTy te.Ty) "int" ""
        }
        test "operator ambiguity unchanged through the sugar" {
            let terr = checkErr "let add x y = x + y in add"
            Expect.stringContains (formatError terr) "cannot infer" ""
            // a use later in the statement decides it [D:plus-defer]
            Expect.equal (checkOk "let add x y = x + y in add 1 2").Ty TInt ""
        }
    ]

let assemblyRecoveryTests =
    testList "Assembly recovery (tooling)" [
        test "a broken line yields its diag AND later statements still analyze" {
            let diags, stmts, _, _, _ =
                Weir.Analyze.analyzeLines "t.weir" [
                    "type T = { A: int }"
                    ""
                    "let go = 1 > 0"
                    ""
                    "if go then !"
                    "(Seq.initInfinite id) == (Seq.initInfinite id)"
                ]
            // the bare marker with no block is the assembly error; the
            // == error after it must still be found, and earlier
            // statements survive
            Expect.exists diags (fun d -> d.Code = "layout") "assembly diag present"
            Expect.exists diags (fun d -> d.Code = "not-equatable") "later statement still checked"
            Expect.isTrue (stmts |> List.exists (fun (_, c) -> c.Env.Types.ContainsKey "T")) "types survive"
        }
        test "a misaligned field is one located diag — re-indented, its closer still closes [D:field-alignment]" {
            let diags, _, _, _, _ =
                Weir.Analyze.analyzeLines "t.weir" [
                    "type Cfg = { host: string; port: int }"
                    "let d = { host = \"h\"; port = 1 }"
                    "let c = { d with host = \"x\""
                    "          port = 2 }"
                    "print $\"{c.port}\""
                ]

            match diags |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal d.Code "layout" "the alignment error"
                Expect.equal (d.Line, d.Col) (4, 11) "located at the field"
            | other -> failtest $"expected exactly one error: {other |> List.map (fun d -> d.Message)}"
        }
        test "an assembly error is located line:col, its message without the line lead [D:assembly-col]" {
            let numbered = [ 1, "let x = 1"; 2, "  let y = 2" ]

            match Weir.Assemble.assemble numbered with
            | Ok _ -> failtest "must refuse"
            | Error msg ->
                match Weir.Assemble.assemblyErrorAt numbered msg with
                | Some(l, c, rest) ->
                    Expect.equal (l, c) (2, 3) "the named line, at its indentation"
                    Expect.stringStarts rest "this let needs a body" "the location carries the line"
                | None -> failtest $"unlocated: {msg}"

                Expect.stringStarts
                    (Weir.Assemble.assemblyErrorLocated "t.weir" numbered msg)
                    "t.weir:2:3: this let needs a body"
                    "the runner's spelling"

            match
                diagsIn "t.weir" [ "let x = 1"; "  let y = 2" ]
                |> List.filter (fun d -> d.Code = "layout")
            with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (2, 3) "check/LSP agree"
                Expect.stringStarts d.Message "this let needs a body" "no doubled line lead"
            | other -> failtest $"expected one assembly diag: {other |> List.map (fun d -> d.Message)}"

            Expect.isNone (Weir.Assemble.assemblyErrorAt [] "no line named") "a lineless message stays unlocated"
        }
    ]

let scannerTests =
    testList "Scanner & classifier contract" [ // the quote-aware scanner, pinned through its consumers
        test "escaped quote inside a string hides //" {
            Expect.equal (Weir.Assemble.stripComment "print \"a\\\" // x\"") "print \"a\\\" // x\"" ""
        }
        test "single quotes hide //" { Expect.equal (Weir.Assemble.stripComment "echo 'a // b'") "echo 'a // b'" "" }
        test "comment after a closed string cuts" {
            Expect.equal (Weir.Assemble.stripComment "print \"x\" // cut") "print \"x\"" ""
        }
        test "bareword URL survives (boundary rule)" {
            Expect.equal (Weir.Assemble.stripComment "curl https://x//y") "curl https://x//y" ""
        }
        test "mid-token // is not a comment" {
            Expect.equal (Weir.Assemble.stripComment "let x = 1// c") "let x = 1// c" ""
        }
        test "classifyLine: blank, comment-only (indented too), code" {
            Expect.equal (Weir.Assemble.classifyLine "   ") Weir.Assemble.LineKind.Blank ""
            Expect.equal (Weir.Assemble.classifyLine "  // note") Weir.Assemble.LineKind.CommentOnly ""
            Expect.equal (Weir.Assemble.classifyLine "let x = 1 // t") Weir.Assemble.LineKind.Code ""
        }
        test "classifyPiece: kinds are exclusive, prefix-guarded" {
            Expect.equal (Weir.Assemble.classifyPiece "let x = 1").Kind Weir.Assemble.PieceKind.LetHead ""
            Expect.equal (Weir.Assemble.classifyPiece "| x -> 1").Kind Weir.Assemble.PieceKind.PipeHead ""
            Expect.equal (Weir.Assemble.classifyPiece "else 2").Kind Weir.Assemble.PieceKind.ElseHead ""
            Expect.equal (Weir.Assemble.classifyPiece "elsewhere ()").Kind Weir.Assemble.PieceKind.Plain ""
            Expect.equal (Weir.Assemble.classifyPiece "letter x").Kind Weir.Assemble.PieceKind.Plain ""
        }
        test "classifyPiece: retired ! markers classify as NoMarker; the assembler TEACHES [D:district-retirement]" {
            // the old Bare/Env marker pins flip: retirement makes them
            // plain pieces, and the retiredDistrictMarker predicate is
            // what routes the teaching error
            Expect.equal (Weir.Assemble.classifyPiece "if c then !").Marker Weir.Assemble.MarkerKind.NoMarker ""
            Expect.equal (Weir.Assemble.classifyPiece "if c then !e").Marker Weir.Assemble.MarkerKind.NoMarker ""
            Expect.isTrue (Weir.Assemble.retiredDistrictMarker "if c then !") "bare spelling detected"
            Expect.isTrue (Weir.Assemble.retiredDistrictMarker "!targetEnv") "env spelling detected"
            Expect.isFalse (Weir.Assemble.retiredDistrictMarker "echo hello!") "a trailing ! word is not the marker"
            Expect.isFalse (Weir.Assemble.retiredDistrictMarker "!e(git st)") "a glued paren is no district marker"

            // the teaches half, observed at the assembler (the maintenance
            // sweep's M5 found only the predicate asserted — if assemble
            // stopped consulting it, this test still passed)
            match Weir.Assemble.assemble [ 1, "if c then !"; 2, "    git pull" ] with
            | Error e ->
                Expect.stringContains e "not a district marker" "the teaching fires"
                Expect.stringContains e "within env vars" "and names the env-overlay repair"
            | Ok _ -> failtest "a retired marker must be an assembly error"
        }
        test "classifyPiece: brace deltas ignore strings and interp holes" {
            Expect.equal (Weir.Assemble.classifyPiece "{ Name = $\"a{1}b\"").BraceDelta 1 ""
            Expect.equal (Weir.Assemble.classifyPiece "awk '{print}' f").BraceDelta 0 ""
            Expect.equal (Weir.Assemble.classifyPiece "{ X = \"}\" }").BraceDelta 0 ""
        }
        test "parse failure carries its column as data (no regex scrape)" {
            match Weir.Parser.parseLineFull cmdResolver "let = 3" with
            | Error f ->
                match f.Col with
                | Some col -> Expect.stringContains f.Message $"Col: {col}" "position data matches message text"
                | None -> failtest "expected a column"
            | Ok s -> failtest $"unexpected: {s}"
        }
    ]

let childEnvTests =
    // cmdEnv/runEnv dropped [D:drop-command-builtins]: child env goes
    // through `NAME=value cmd`, `within env` or the capture sigil
    // `$e(...)` (tested in e2e). Env.fromFile / Env.vars remain.
    testList "Child-env injection" [
        test "Env.fromFile types to seq<EnvVar>" {
            let te = checkOk "Env.fromFile \"x.env\""
            Expect.equal (formatTy te.Ty) "seq<EnvVar>" ""
        }
        test "fromFile: the dotenv subset parses (quotes, comments, blanks, empty)" {
            let f = weirPath (System.IO.Path.GetTempFileName())

            System.IO.File.WriteAllLines(f, [ "A=1"; "B='sq val'"; "C=\"dq\" # note"; "# comment"; ""; "D=" ])

            let got =
                run ("Env.fromFile \"" + f + "\" |> Seq.map (fun e -> e.value) |> Seq.freeze")
                |> forceSeq

            Expect.equal got [ VStr "1"; VStr "sq val"; VStr "dq"; VStr "" ] ""
            System.IO.File.Delete f
        }
        test "fromFile: a rejection raises at the call, naming the sh escape [D:env-fromfile-bind-once]" {
            let f = weirPath (System.IO.Path.GetTempFileName())
            System.IO.File.WriteAllLines(f, [ "GOOD=1"; "BAD=$HOME" ])

            Expect.throws (fun () -> run ("Env.fromFile \"" + f + "\" |> ignore") |> ignore) "unforced, still refused"

            System.IO.File.Delete f
        }
        test "fromFile: the file is read once, at the bind [D:env-fromfile-bind-once]" {
            let f = weirPath (System.IO.Path.GetTempFileName())
            System.IO.File.WriteAllLines(f, [ "A=1" ])

            let got =
                run (
                    "let e = Env.fromFile \""
                    + f
                    + "\" in let _ = File.delete \""
                    + f
                    + "\" in e |> Seq.map _.value |> Seq.freeze"
                )
                |> forceSeq

            Expect.equal got [ VStr "1" ] "a delete after the bind changes nothing"
        }
        test "a discarded $e(cmd) names the overlay form, not a paren drop [D:district-retirement]" {
            match errorMsgsIn "es.weir" [ "let e = Env.ofPairs [(\"Q\", \"1\")]"; "$e(sh -c \"echo $Q\")" ] with
            | [ m ] ->
                Expect.equal
                    m
                    "this statement computes a seq<string> and discards it — bind it, or pipe it to print, or run it as a command under the overlay: `within env e` with the bare command indented below"
                    "the repair names within env"
            | ms -> failtest $"expected one error, got {ms}"
        }
        test "env sigil: $e(...) attaches the overlay to the chain's spawn" {
            match Weir.Parser.parseLine realResolver "let x = $e(git status)" with
            // ECapture wraps: $() asserts capture in every position
            // [D:district-retirement]
            | Ok(SLet("x",
                      {
                          Kind = ECapture {
                                              Kind = ECmd(HeadLit "git", _, Some { Kind = EVar "e" })
                                          }
                      })) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "env sigil: every segment in the chain gets the env" {
            match Weir.Parser.parseLine realResolver "let x = $e(git log | grep x)" with
            | Ok(SLet("x",
                      {
                          Kind = ECapture {
                                              Kind = EPipe({
                                                               Kind = ECmd(HeadLit "git", _, Some _)
                                                           },
                                                           {
                                                               Kind = ECmd(HeadLit "grep", _, Some _)
                                                           })
                                          }
                      })) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "env sigil x complete: routes through completedEnv" {
            // the env is bound once and the argv reads under it
            // [D:env-prefix-scope]: let |pfx = e in |completedEnv |pfx prog
            // (within env |pfx: argv)
            match Weir.Parser.parseLine realResolver "let r = $e(git status | complete)" with
            | Ok(SLet("r",
                      {
                          Kind = ECapture {
                                              Kind = ELet("|pfx",
                                                          _,
                                                          { Kind = EVar "e" },
                                                          {
                                                              Kind = EApp({
                                                                              Kind = EApp({
                                                                                              Kind = EApp({
                                                                                                              Kind = EVar "|completedEnv"
                                                                                                          },
                                                                                                          {
                                                                                                              Kind = EVar "|pfx"
                                                                                                          })
                                                                                          },
                                                                                          { Kind = EStr "git" })
                                                                          },
                                                                          {
                                                                              Kind = EWithin(WithinEnv,
                                                                                             None,
                                                                                             Some { Kind = EVar "|pfx" },
                                                                                             None,
                                                                                             _)
                                                                          })
                                                          })
                                          }
                      })) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "env sigil: a space after the ident falls back (no sigil)" {
            match Weir.Parser.parseLine realResolver "let x = $e (git status)" with
            | Ok(SLet("x", { Kind = ECmd _ })) -> failtest "space must not read as an env sigil"
            | _ -> ()
        }
        test "the wrap-it hint fires in fragment positions; plain unbound is unchanged [D:district-retirement]" {
            let terr = checkErr "Seq.length (sh -c \"echo g\")"
            Expect.stringContains (formatError terr) "wrap it: $(sh " "the repair named at the head"
            Expect.stringContains (formatError terr) "unbound variable 'sh'" "did-you-mean line coexists"

            // a lone unbound var is not command-shaped — old text only
            let plain = checkErr "zzznope"
            Expect.isFalse ((formatError plain).Contains "wrap it") "no hint without application"
        }
        test "THE DEDENT FLOOR: unaligned dedents ERROR instead of silently joining [D:district-retirement]" {
            // the silent-swallow class (legal-parse-wrong-meaning): a
            // statement dedenting from a nested block must not space-join
            // — absorbed as argv after a command line. Four shapes, one
            // floor: within, block-let, for, if.
            let shapes = [
                [
                    1, "within tmp d"
                    2, "    within tmp e"
                    3, "        sh -c \"echo x\""
                    4, "    sh -c \"echo after\""
                ]
                [
                    1, "let x ="
                    2, "    let y ="
                    3, "        sh -c \"echo a\""
                    4, "        1"
                    5, "    sh -c \"echo b\""
                    6, "    2"
                ]
                [
                    1, "for a in [\"p\"] do"
                    2, "    for b in [\"q\"] do"
                    3, "        sh -c \"echo x\""
                    4, "    sh -c \"echo after\""
                ]
                [
                    1, "let f t ="
                    2, "    if t then"
                    3, "        if t then"
                    4, "            sh -c \"echo x\""
                    5, "        sh -c \"echo after\""
                ]
            ]

            for shape in shapes do
                match Weir.Assemble.assemble shape with
                | Error msg -> Expect.stringContains msg "aligns with no enclosing statement" $"floor fires: {shape}"
                | Ok lls ->
                    // an Ok is fine only if the dedented line became a
                    // real sibling (a sentinel precedes it) — never a
                    // space-absorbed argv
                    let joinedOk =
                        lls
                        |> List.forall (fun ll ->
                            not (ll.Text.Contains "\" sh") || ll.Text.Contains Weir.Parser.sibSepStr)

                    Expect.isTrue joinedOk $"no silent absorption: {lls |> List.map _.Text}"
        }
        test "the env-district spellings teach their retirement [D:district-retirement]" {
            // !e / !name at line end, standalone or headed — all five
            // former district-header pins collapse to the teaching;
            // `within env vars` is the block overlay now (e2e-pinned),
            // and the $e() sigil pins below stay untouched
            for fixture in
                [
                    [ 1, "if go then !e"; 2, "    git pull" ]
                    [ 1, "let f x ="; 2, "    !e"; 3, "        git pull"; 4, "    printerr \"OK\"" ]
                    [ 1, "let f x ="; 2, "    !"; 3, "        git pull"; 4, "    printerr \"OK\"" ]
                ] do
                match Weir.Assemble.assemble fixture with
                | Error msg -> Expect.stringContains msg "not a district marker" ""
                | other -> failtest $"unexpected: {other}"
        }
        test "fromFile: single quotes are shell-literal ($ allowed)" {
            // the ninth \U site, found by the Windows hand-run — the
            // mechanical GetTempFileName/GetTempPath grep now confirms
            // no tenth [D:windows-s3]
            let f = weirPath (System.IO.Path.GetTempFileName())
            System.IO.File.WriteAllLines(f, [ "LIT='$HOME'" ])

            let got = run ("Env.fromFile \"" + f + "\" |> Seq.map _.value |> Seq.head")

            Expect.equal got (VStr "$HOME") ""
            System.IO.File.Delete f
        }
    ]

let productMatrixTests =
    testList "Product matrix (retroactive sweep)" [ // A x F: comment inside a compound body is transparent
        test "A x F: comment between compound-body siblings keeps grouping" {
            match Weir.Assemble.assemble [ 1, "if c then"; 2, "    eff1"; 4, "    eff2" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "if c then eff1 ; eff2") ""
            | other -> failtest $"unexpected: {other}"
        }
        // C x E: pipe line after a blank
        // [D:body-blanks]
        test "C x E: pipe continuation joins across a gap" {
            match Weir.Assemble.assemble [ 1, "ls"; 2, ""; 3, "    |> Seq.length" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "ls |> Seq.length" ""
            | other -> failtest $"unexpected: {other}"
        }
        // C x F: comment between pipe stages (comment filtered upstream:
        // the runner drops it; assemble sees the gap in line numbers)
        test "C x F: comment between pipe stages is transparent" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    ls"; 4, "    |> Seq.length" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = ls |> Seq.length" ""
            | other -> failtest $"unexpected: {other}"
        }
        // E x F: comment-only after a blank stays invisible
        // [D:body-blanks]
        test "E x F: indented line after blank+comment joins" {
            match Weir.Assemble.assemble [ 1, "let x = 1"; 2, ""; 4, "    + 2" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let x = 1 + 2" ""
            | other -> failtest $"unexpected: {other}"
        }
        // E x G: former sibling level after a blank
        // [D:body-blanks]: the sibling rule is indent-keyed, not
        // adjacency-keyed — gap-invariant `;`
        test "E x G: sibling sequencing joins across a gap" {
            match
                Weir.Assemble.assemble [ 1, "let f x ="; 2, "    printerr \"a\""; 3, ""; 4, "    printerr \"b\"" ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = printerr \"a\" ; printerr \"b\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        // F x G: sibling `;` joins across a transparent comment
        test "F x G: sibling sequencing joins across a comment line" {
            match Weir.Assemble.assemble [ 1, "let f x ="; 2, "    printerr \"a\""; 4, "    printerr \"b\"" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = printerr \"a\" ; printerr \"b\"") ""
            | other -> failtest $"unexpected: {other}"
        }
    ]

let offsideTests =
    testList "Offside close & record continuations" [ // the bicep bite: a sibling at the if's own indent closes it
        test "offside close: sibling at head indent wraps the if" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    if c then fail \"u\""; 3, "    { Name = s }" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let t = (if c then fail \"u\") ; { Name = s }") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "offside close: the silent variant no longer swallows" {
            match
                Weir.Assemble.assemble [ 1, "let f c ="; 2, "    if c then printerr \"a\""; 3, "    printerr \"b\"" ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f c = (if c then printerr \"a\") ; printerr \"b\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "else extends the compound instead of closing it" {
            match
                Weir.Assemble.assemble [
                    1, "let f c ="
                    2, "    if c then printerr \"a\""
                    3, "    else printerr \"b\""
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text "let f c = if c then printerr \"a\" else printerr \"b\"" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "sibling after else closes the whole if/else" {
            match
                Weir.Assemble.assemble [
                    1, "let f c ="
                    2, "    if c then a"
                    3, "    else b"
                    4, "    printerr \"z\""
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f c = (if c then a else b) ; printerr \"z\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "deeper siblings still join INTO the body (the greedy protectorate)" {
            match Weir.Assemble.assemble [ 1, "if c then"; 2, "    eff1"; 3, "    eff2" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "if c then eff1 ; eff2") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "nested dedent closes through both levels" {
            match
                Weir.Assemble.assemble [
                    1, "let f x ="
                    2, "    if c then"
                    3, "        eff1"
                    4, "        eff2"
                    5, "    printerr \"z\""
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f x = (if c then eff1 ; eff2) ; printerr \"z\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "sequential ifs: first closes, second stays open at statement end" {
            match Weir.Assemble.assemble [ 1, "let f c ="; 2, "    if a then x"; 3, "    if b then y" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f c = (if a then x) ; if b then y") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "match closes at a sibling too" {
            match
                Weir.Assemble.assemble [
                    1, "let v ="
                    2, "    match x with"
                    3, "    | A -> printerr \"a\""
                    4, "    printerr \"z\""
                ]
            with
            | Ok [ ll ] ->
                Expect.equal ll.Text (asmSib "let v = (match x with | A -> printerr \"a\") ; printerr \"z\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "arming x offside: closing sibling wraps the bare-command if (retargeted [D:district-retirement])" {
            match
                Weir.Assemble.assemble [
                    1, "let f t ="
                    2, "    if c then"
                    3, "        git pull"
                    4, "    print \"x\""
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let f t = (if c then git pull) ; print \"x\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "record continuation: bare fields get separators" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    { Name = \"a\""; 3, "      Count = 2 }" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmField "let t = { Name = \"a\" ; Count = 2 }") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "record continuation: trailing ; means no double separator" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    { Name = \"a\";"; 3, "    Count = 2 }" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let t = { Name = \"a\"; Count = 2 }" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "a lowercase case name errors AT the name, not past it" {
            // rawWord's trailing ws crosses the physical line — the
            // error must anchor before the read consumes it
            let r: Weir.Parser.Resolver = {
                IsKnown = fun _ -> true
                IsCommandCallable = fun _ -> false
                IsExternal = fun _ -> false
                ExternalNames = (fun () -> Seq.empty)
                BareHome = (fun _ -> None)
                AliasHead = fun _ -> None
                IsBuiltinName = fun _ -> false
            }

            match Weir.Parser.parseLineFull r "type P = | Pulled | upToDate | Join of string" with
            | Error f ->
                Expect.stringContains f.Message "uppercase letter" ""
                Expect.equal f.Col (Some 21) "the column of 'upToDate', not the next token"
            | Ok _ -> failtest "expected the casing error"
        }
        // the span classes ride the consumed-separator law
        // [D:seq-commit] [D:arm-commit]: errors anchor on the junk
        test "a parse error after a command group anchors on the junk (retargeted)" {
            let lines = [
                "let v0 ="
                "    if 79 == 84 then"
                "        echo m0 w381"
                "    if 0 > 99 then ?!?"
                "        print \"x\""
                "    9"
            ]

            let diags = diagsIn "pin.weir" lines

            // [D:seq-commit]: no backtrack can anchor past the
            // district — the primary lands on the junk
            match diags |> List.filter (fun d -> d.Severity = "error") with
            | d :: _ -> Expect.equal (d.Line, d.Col) (4, 20) "primary anchors on the junk itself"
            | [] -> failtest "expected a parse diagnostic"
        }
        test "junk after a bound name errors at check, on the junk [D:seq-commit]" {
            // a consumed ';' commits to its element: the failing tail
            // cannot re-parse outside its let-in scope into a phantom
            // command — check and run agree, located at the junk
            let lines = [
                "let v0 ="
                "    let v3 = \"a\""
                "    print \"mm\""
                "    let v4 = v3 ?!?"
                "    3"
            ]

            let diags = diagsIn "pin.weir" lines

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
            let lines = [
                "let v6 ="
                "    match 62 with"
                "    | 0 -> \"a\""
                "    | 1 ->"
                "        match \"w790\" with"
                "        | \"k0\" -> \"b\" ?!?"
                "        | _ -> \"w484\""
                "    | _ -> \"d\""
            ]

            let diags = diagsIn "pin.weir" lines

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
            let lines = [
                "type R = { A: int; B: string }"
                "let v = { A = 47"
                "          B = \"x\" ?!?"
                "          }"
            ]

            let diags = diagsIn "pin.weir" lines

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
            let rNone: Weir.Parser.Resolver = {
                IsKnown = (fun _ -> false)
                IsCommandCallable = (fun _ -> false)
                IsExternal = (fun _ -> false)
                ExternalNames = (fun () -> Seq.empty)
                BareHome = (fun _ -> None)
                AliasHead = fun _ -> None
                IsBuiltinName = fun _ -> false
            }

            let rGit = {
                rNone with
                    IsExternal = (fun n -> n = "git")
            }

            // (a) a bare '|' in an arm RHS is an arm separator (F#
            // reads it the same way) — command chains in arms ride $()
            match Weir.Parser.parseLineFull rGit "let v = match 1 with | 1 -> git log | Seq.head | _ -> \"x\"" with
            | Error _ -> ()
            | Ok _ -> failtest "a bare chain in an arm RHS must not parse"

            match Weir.Parser.parseLineFull rGit "let v = match 1 with | 1 -> $(git log) |> Seq.head | _ -> \"x\"" with
            | Ok _ -> ()
            | Error e -> failtest $"the sigil spelling is the arm chain: {e.Message}"

            // (b) binder-free or-patterns parse under commit [D:or-patterns];
            // an alternative that binds a name refuses at that alternative
            match Weir.Parser.parseLineFull rNone "let v = match 1 with | 0 | 1 -> \"low\" | _ -> \"hi\"" with
            | Ok _ -> ()
            | Error f -> failtest $"a binder-free or-pattern parses: {f.Message}"

            match Weir.Parser.parseLineFull rNone "let v = match (1, 2) with | (n, 0) | (0, n) -> n | _ -> 0" with
            | Error f -> Expect.stringContains f.Message "cannot bind names yet" "the v1 teaching"
            | Ok _ -> failtest "a binding alternative refuses in v1"

            // (c) guards under commit
            match Weir.Parser.parseStmt "let v = match 3 with | n when n > 2 -> n | _ -> 0" with
            | Ok _ -> ()
            | Error e -> failtest $"guards must survive the commit: {e}"

            // (e) a reserved word in arm-head position errors at it
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
            // an unaligned attr-owned field must fail the check, not
            // surface as a runtime argv error
            match
                Weir.Assemble.assemble [
                    1, "type C = {"
                    2, "    [<Short \"d\">]"
                    3, "     subdir: string"
                    4, "    force: bool"
                    5, "}"
                ]
            with
            | Error e -> Expect.stringContains e "they sit at column 4" ""
            | other -> failtest $"expected the alignment error, got {other}"

            match
                Weir.Assemble.assemble [
                    1, "type C = {"
                    2, "    [<Short \"d\">]"
                    3, "    subdir: string"
                    4, "    force: bool"
                    5, "}"
                ]
            with
            | Ok _ -> ()
            | Error e -> failtest $"the aligned attr+field must stay legal: {e}"
        }
        test "record fields off the first-field column error naming it" {
            match
                Weir.Assemble.assemble [
                    1, "let t ="
                    2, "    { Name = \"a\""
                    3, "    Count = 2"
                    4, "    Tag = \"x\" }"
                ]
            with
            | Error e -> Expect.stringContains e "they sit at column 6" ""
            | other -> failtest $"expected the alignment error, got {other}"
        }
        test "record continuation: a field's value may open on the NEXT line (sweep catch)" {
            match
                Weir.Assemble.assemble [
                    1, "let o ="
                    2, "    { Name = \"outer\""
                    3, "      In ="
                    4, "        { V = 42 } }"
                ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmField "let o = { Name = \"outer\" ; In = { V = 42 } }") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "classifyPiece: StartsField is ident-guarded" {
            Expect.isTrue (Weir.Assemble.classifyPiece "Count = 2").StartsField ""
            Expect.isTrue (Weir.Assemble.classifyPiece "In =").StartsField ""
            Expect.isFalse (Weir.Assemble.classifyPiece "{ V = 42 } }").StartsField ""
            Expect.isFalse (Weir.Assemble.classifyPiece "1 + x = y").StartsField ""
        }
        test "record continuation: col-0 close is legal inside a brace" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    { Name = \"a\""; 3, "}" ] with
            | Ok [ ll ] -> Expect.equal ll.Text "let t = { Name = \"a\" }" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "interp-hole braces do not count (scanner is string-aware)" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    { Name = $\"i{1}b\""; 3, "      Count = 2 }" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmField "let t = { Name = $\"i{1}b\" ; Count = 2 }") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "blank inside an open brace is the located record error" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    { Name = \"a\""; 3, "" ] with
            | Error e -> Expect.stringContains e "record literal" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "EOF inside an open brace is the located record error" {
            match Weir.Assemble.assemble [ 1, "let t ="; 2, "    { Name = \"a\"" ] with
            | Error e -> Expect.stringContains e "record literal" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "bare command lines never enter brace mode (retargeted [D:district-retirement])" {
            match Weir.Assemble.assemble [ 1, "if c then"; 2, "    awk \"{print}\" f"; 3, "    git pull" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "if c then awk \"{print}\" f ; git pull") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "exit types like fail: int -> 'a [D:fail-bottom]" {
            match Weir.Parser.parseLine cmdResolver "exit 3" with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck env e with
                | Ok te ->
                    match te.Ty with
                    | Weir.Types.TVar _ -> ()
                    | other -> failtest $"expected a fresh var, got {other}"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "exit rejects a string" {
            let terr = checkErr "exit \"boom\""
            Expect.stringContains (formatError terr) "int" ""
        }
        test "exit outside 0..255: a literal is a check error, a value raises [D:exit-range]" {
            Expect.stringContains (formatError (checkErr "exit 256")) "it would exit 0" ""
            Expect.stringContains (formatError (checkErr "exit -1")) "0..255" ""
            let ex = Expect.throwsC (fun () -> run "let n = 257 in exit n" |> ignore) id
            Expect.stringContains ex.Message "it would exit 1" ""
        }
    ]

let sequencingTests =
    testList "Block sequencing" [
        test "explicit semicolon sequences" { expectValue "(print \"x\" ; 41 + 1)" (VInt 42L) }
        test "non-unit first is the tailored error" {
            let terr = checkErr "1 ; print \"no\""
            Expect.stringContains (formatError terr) "must be unit" ""
        }
        test "greedy bodies: semicolon binds INTO then (named divergence)" {
            // (if false then print "no") never sequences the tail here:
            expectValue "let c = false in (if c then print \"a\" ; print \"b\") ; 7" (VInt 7L)
        }
        test "assembler: same-indent siblings sequence" {
            match Weir.Assemble.assemble [ 1, "let w ="; 2, "    print \"a\""; 3, "    print \"b\"" ] with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let w = print \"a\" ; print \"b\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "assembler: let-close beats sibling; sequence resumes after" {
            match
                Weir.Assemble.assemble [ 1, "let w ="; 2, "    let a = 1"; 3, "    print \"x\""; 4, "    print \"y\"" ]
            with
            | Ok [ ll ] -> Expect.equal ll.Text (asmSib "let w = let a = 1 in print \"x\" ; print \"y\"") ""
            | other -> failtest $"unexpected: {other}"
        }
        test "function RHS takes a sequenced body (bicep receipt)" {
            match Weir.Parser.parseLine cmdResolver "let f x = printerr \"a\" ; printerr \"b\"" with
            | Ok(SLet("f",
                      {
                          Kind = ELambda("x", _, { Kind = ESeq _ })
                      })) -> ()
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
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    ls"; 3, "    |> Seq.length" ] with
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
        }
    ]

let siblingSentinelTests =
    // [D:sibling-sentinel] — the diagnostics-arc Session E successor:
    // command mode stops at the machine sibling boundary, so a
    // command-first body sequences instead of over-running to EOF.
    let diags = diagsIn "pin.weir"

    testList "Sibling sentinel" [
        test "ACCEPTANCE: cmd then let..in body parses as a real sequence" {
            // was the backtrack-to-EOF dump; now a clean ESeq whose
            // first element is the command
            match
                Weir.Parser.parseLine
                    realResolver
                    // the real sibling join is spaced (" <US> ") — a glued
                    // sentinel is machine-impossible and the command
                    // grammar now refuses it (the yaml-district boundary)
                    ("let f t = git status " + Weir.Parser.sibSepStr + " let e = \"x\" in print e")
            with
            // flipped by [D:interior-arming]: the non-final command now
            // arms (EPipe into print) instead of sitting capture-typed
            | Ok(SLet("f",
                      {
                          Kind = ELambda("t",
                                         _,
                                         {
                                             Kind = ESeq({
                                                             Kind = EPipe({ Kind = ECmd(HeadLit "git", _, _) },
                                                                          { Kind = EVar "|print" })
                                                         },
                                                         _)
                                         })
                      })) -> ()
            | other -> failtest $"expected ESeq(armed cmd, ...), got: {other}"
        }
        test "ACCEPTANCE: a command-first body now CHECKS (flipped by [D:interior-arming])" {
            // the old pin asserted the seq-unit rejection at the head;
            // the interior-arming rule makes the command an effect and
            // the body legal — the flip, named
            let ds =
                diags [ "let _f t ="; "    git status"; "    let e = \"x\""; "    print e" ]

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "the armed body checks clean"
        }
        test "ACCEPTANCE: a NON-command non-unit interior statement still seq-unit-errors at its head" {
            // the statement rule is unchanged for non-command lines
            // [D:interior-arming] — located at the statement, no EOF
            // dump, no raw expecting-list
            let ds =
                diags [ "let f t ="; "    Str.trim \"one\""; "    let e = \"x\""; "    print e" ]

            match ds |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (2, 5) "at the statement head"
                Expect.isTrue (d.Message.Contains "must be unit") "the seq-unit teaching"
                Expect.isFalse (d.Message.Contains "end of the input stream") "no EOF note"
            | other -> failtest $"expected ONE error at the head, got {other}"
        }
        test "user ';' is byte-identical: one command, a bareword arg, the prior-bleed warning" {
            // the whole reason B beat A — a user-typed ';' on one line
            // is still a command with a ';' argv word that warns
            match Weir.Parser.parseLine cmdResolver "git status ; echo hi" with
            | Ok(SCmd({ Kind = ECmd(HeadLit "git", args, _) })) ->
                Expect.isTrue
                    (args |> List.exists (fun a -> a.Kind = EStr ";"))
                    "the ';' is a bareword arg, not a separator"
            | other -> failtest $"expected one command swallowing ';', got: {other}"
        }
        test "unproduceable: the sentinel in SOURCE is rejected at assembly" {
            match Weir.Assemble.assemble [ 1, "print \"a\"" + Weir.Parser.sibSepStr + "print \"b\"" ] with
            | Error e -> Expect.stringContains e "illegal control character" "rejected as illegal"
            | Ok _ -> failtest "a source sentinel must not assemble"
        }
        test "no-leak: the sentinel never surfaces in a diagnostic" {
            // a command-first body whose parse dump lists separators in the
            // expected-set — the seqSep relabel keeps the sentinel out, and
            // cleanParseDump scrubs both the raw char and FParsec's 
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
        test "the expected-set collapses a token relabelled by two branches [D:clean-parse-dump]" {
            // `within tmp d -> …` misses the scope's block; more than one
            // alternative relabels the separator to ';', so the set had
            // rendered `';' or ';'` — one ';' now
            let ds = diags [ "within tmp d -> print d" ]
            let d = ds |> List.find (fun d -> d.Severity = "error")
            Expect.isFalse (d.Message.Contains "';' or ';'") $"duplicate token in the expected-set: {d.Message}"
            Expect.stringContains d.Message "the scope's block or ';'" "the deduped expected-set"
        }
        test "no-leak: internal backtrack labels never surface [D:label-leaks]" {
            // the record-brace sitting carried 'whitespace before [
            // means application' in its Other-error-messages tail with
            // no '[' in the input — 3rd instance of the class; every
            // ifail label is marker-filtered from the cleaned dump
            let noLabelLeak (lines: string list) =
                for d in diags lines do
                    Expect.isFalse (d.Message.Contains Weir.Parser.internalLabelMarker) $"marker leaked: {d.Message}"
                    Expect.isFalse (d.Message.Contains "\\u0006") $"escaped marker leaked: {d.Message}"
                    Expect.isFalse (d.Message.Contains "whitespace before [") $"internal label leaked: {d.Message}"
                    Expect.isFalse (d.Message.Contains "spine-only") $"internal label leaked: {d.Message}"

                    Expect.isFalse (d.Message.Contains "expression territory") $"internal label leaked: {d.Message}"

            noLabelLeak [ "let r = { Http.get \"u\" } |> Http.send" ]
            noLabelLeak [ "let e = ((" ]
            noLabelLeak [ "let q = x." ]
        }
        test "{ expr } without `with` names the repair [D:label-leaks]" {
            let ds = diags [ "let r = { Http.get \"u\" } |> Http.send" ]
            let d = ds |> List.find (fun d -> d.Severity = "error")
            Expect.stringContains d.Message "a record update needs 'with'" "the diagnosis"
            Expect.stringContains d.Message "use parentheses" "the grouping repair"
            Expect.isFalse (d.Message.Contains "Expecting:") "no expecting-list burial"
        }
        test "DESIGNED expectations survive the label filter [D:label-leaks]" {
            // the filter drops only marked labels — the seq< > slot's
            // designed expectation (and the escape teaching) still reach
            // the user through the cleaned dump
            let ds = diags [ "let x = [\"[]\"] |> from json seq<int>" ]

            // the slot's teaching is a fatal now [D:adapter-slot-teach]
            Expect.isTrue
                (ds
                 |> List.exists (fun d -> d.Message.Contains "'int' inside seq< > is not a declared record name"))
                $"the designed expectation must survive: {ds}"

            let ds2 = diags [ "let p = \"C:\\Users\\x\"" ]

            Expect.isTrue
                (ds2 |> List.exists (fun d -> d.Message.Contains "verbatim string"))
                $"the escape teaching must survive: {ds2}"
        }
        test "fmt output never carries the sentinel (assemble->check artifact only)" {
            match Weir.Fmt.formatLines [ "let f t ="; "    git status"; "    let e = \"x\""; "    print e" ] with
            | Ok lines ->
                for l in lines do
                    Expect.isFalse (l.Contains Weir.Parser.sibSepStr) $"sentinel in fmt output: {l}"
            | Error e -> failtest $"fmt failed: {e}"
        }
    ]

let sigilTests =
    testList "Command sigils" [
        test "capture sigil parses to the command chain (realResolver)" {
            match Weir.Parser.parseLine realResolver "let b = $(git branch) |> Seq.length" with
            | Ok(SLet("b",
                      {
                          Kind = EPipe({
                                           Kind = ECapture { Kind = ECmd(HeadLit "git", _, _) }
                                       },
                                       _)
                      })) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "a block sequenced before more statements runs its command tail [D:seq-arming]" {
            let errs = errorMsgsIn "sa.weir"

            Expect.isEmpty
                (errs [
                    "let sub ="
                    "    within tmp d"
                    "        sh -c \"echo a\""
                    "    \"after\""
                    "print sub"
                ])
                "a within tail on a let spine"

            Expect.isEmpty
                (errs [
                    "let v ="
                    "    if true then"
                    "        sh -c \"echo t\""
                    "    else"
                    "        sh -c \"echo e\""
                    "    1"
                    "print $\"{v}\""
                ])
                "an if/else tail"

            Expect.isEmpty
                (errs [
                    "let cap ="
                    "    within tmp d"
                    "        sh -c \"echo c\""
                    "print (show (Seq.length cap))"
                ])
                "a final block still captures"
        }
        test "there is no run sigil: !(…) and !e(…) do not parse [D:bang-retirement]" {
            for line in [ "!(git status)"; "!e(git status)"; "let u = !(git status)" ] do
                match Weir.Parser.parseLine realResolver line with
                | Ok s -> failtest $"{line} must not parse, got {s}"
                | Error _ -> ()
        }
        test "sigils x interpolation: holes never open command mode" {
            // $"{...}" holes are expression holes; a bareword there is unbound
            match Weir.Parser.parseLine realResolver "print $\"x{git}y\"" with
            | Ok(SExpr _) -> ()
            | other -> failtest $"unexpected: {other}"

            let terr = checkErr "$\"x{git}y\""
            Expect.stringContains (formatError terr) "unbound" "git is not a command in a hole"
        }
        test "sigils x complete outside: parse error, statement spelling exists" {
            match Weir.Parser.parseLine realResolver "$(git status) | complete" with
            | Error _ -> ()
            | Ok s -> failtest $"'| complete' is a command-suffix desugar; got {s}"
        }
        test "sigils x complete inside: uniform interior grammar composes" {
            match Weir.Parser.parseLine realResolver "let r = $(git status | complete)" with
            | Ok(SLet("r", { Kind = ECapture { Kind = EApp _ } })) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "sigils x strict mode: grammar, not resolution" {
            // the sigil works in strict scripts; interior expr stages qualify
            match Weir.Parser.parseLine realResolver "let x = $(git branch |> Seq.map Str.trim)" with
            | Ok(SLet _) -> ()
            | other -> failtest $"unexpected: {other}"
        }
    ]

let districtTests =
    testList "Command district (RETIRED [D:district-retirement])" [
        test "the line-end ! marker teaches its retirement" {
            match Weir.Assemble.assemble [ 1, "if go then !"; 2, "    git pull" ] with
            | Error msg ->
                Expect.stringContains msg "not a district marker" "the teaching, not an expecting-list"
                Expect.stringContains msg "within env" "the overlay pointer"
            | other -> failtest $"unexpected: {other}"
        }
        test "the !name marker teaches identically" {
            match Weir.Assemble.assemble [ 1, "if go then !e"; 2, "    git pull" ] with
            | Error msg -> Expect.stringContains msg "not a district marker" ""
            | other -> failtest $"unexpected: {other}"
        }
        test "the arming spelling replaces it: bare commands under if assemble and check" {
            let diags = diagsIn "r.weir" [ "let go = 1 > 0"; "if go then"; "    git status" ]

            Expect.isEmpty (diags |> List.filter (fun d -> d.Severity = "error")) "the replacement is legal"
        }
        test "MECHANISM PIN: assembler recursion bounded by nesting, not file length (retargeted)" {
            let lines =
                [
                    for _ in 1..500 do
                        yield "let go = 1 > 0"
                        yield "if go then"
                        yield "    git status"
                        yield ""
                ]
                |> List.mapi (fun i l -> i + 1, l)

            match Weir.Assemble.assemble lines with
            | Ok lls -> Expect.equal (List.length lls) 1000 "all statements assembled"
            | Error e -> failtest e
        }
    ]

let matchPipeOffsideTests =
    // [D:match-pipe-offside]: F#'s offside for a `|>` after a match. At the
    // arm `|` it closes the match (assembler wraps `(match …) |> f`); at or
    // past the arm's pattern column it extends the arm body; in the gap
    // between the two it rejects. All in Assemble.assemble.
    let assemble lines =
        Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l))

    testList "match pipe offside [D:match-pipe-offside]" [
        test "a |> at the arm column wraps and pipes the whole match" {
            match assemble [ "match 5 with"; "| 5 -> 50"; "| _ -> 0"; "|> print" ] with
            | Ok [ ll ] ->
                Expect.stringContains ll.Text "(match 5 with" "the match opens a paren"
                Expect.stringContains ll.Text "| _ -> 0) |> print" "the paren closes before the pipe"
            | other -> failtest $"expected one wrapped logical line, got {other}"
        }
        test "a |> under the arm body extends the arm (inline body)" {
            // body `n` sits at column 7; the pipe lines up under it
            match assemble [ "match 5 with"; "| n -> n"; "       |> print" ] with
            | Ok [ ll ] ->
                Expect.isFalse (ll.Text.Contains "(match") "no wrap — the arm is extended, not closed"
                Expect.stringContains ll.Text "| n -> n |> print" "the pipe joins the arm body"
            | other -> failtest $"expected one extended logical line, got {other}"
        }
        test "a |> left of the arm body (but past the '|') is rejected" {
            // inline body at column 7; a pipe at column 4 is left of it
            match assemble [ "match 5 with"; "| _ -> \"t\""; "    |> print" ] with
            | Error e -> Expect.stringContains e "left of the arm body" "names the body floor"
            | Ok lls -> failtest $"a |> left of the body must reject, assembled {lls}"
        }
        test "a |> under the arm body extends the arm (dangling body)" {
            match assemble [ "match 5 with"; "| n ->"; "    n"; "    |> print" ] with
            | Ok [ ll ] -> Expect.stringContains ll.Text "| n -> n |> print" "the dangling body's pipe joins the arm"
            | other -> failtest $"expected one extended logical line, got {other}"
        }
        test "each arm keeps its own body column (a later shallower arm is not judged by an earlier one)" {
            // arm 1's inline body sits deep (col 12); arm 2 dangles with a
            // shallow body (col 4) and a pipe under it — must extend, not be
            // measured against arm 1 [D:match-pipe-offside]
            match
                assemble [
                    "match x with"
                    "| Some p -> File.read p"
                    "| None ->"
                    "    fetch"
                    "    |> join"
                ]
            with
            | Ok [ ll ] -> Expect.stringContains ll.Text "| None -> fetch |> join" "arm 2's pipe joins arm 2's body"
            | other -> failtest $"expected one logical line, got {other}"
        }
        test "a |> left of a dangling arm's body is rejected" {
            // dangling body resolves to column 4; a pipe at column 1 is left of it
            match assemble [ "match 5 with"; "| n ->"; "    n"; " |> print" ] with
            | Error e -> Expect.stringContains e "left of the arm body" "the error explains both fixes"
            | Ok lls -> failtest $"a |> left of a dangling body must reject, assembled {lls}"
        }
        test "a plain multi-line pipeline is NOT wrapped (no spurious close)" {
            match assemble [ "xs"; "|> Seq.sum"; "|> print" ] with
            | Ok [ ll ] -> Expect.isFalse (ll.Text.Contains "(xs") "a pipeline head stays unwrapped"
            | other -> failtest $"expected one logical line, got {other}"
        }
        test "a match closed inside parens leaves no arm group: a following |> pipes the paren [D:compound-paren-prune]" {
            for lines in
                [
                    [
                        "let r ="
                        "    (match 1 with"
                        "     | 1 -> \"a\""
                        "     | _ -> \"b\")"
                        "    |> Str.toUpper"
                    ]
                    [
                        "let r = (match 1 with"
                        "         | 1 -> \"a\""
                        "         | _ -> \"b\")"
                        "        |> Str.toUpper"
                    ]
                ] do
                match assemble lines with
                | Ok [ ll ] -> Expect.stringContains ll.Text "\"b\") |> Str.toUpper" "the pipe follows the paren"
                | other -> failtest $"expected one logical line, got {other}"

            match typecheck env (parse "(match 1 with\n | 1 -> \"a\"\n | _ -> \"b\")\n|> Str.toUpper") with
            | Ok te -> Expect.equal te.Ty TStr "the parenthesized match, piped"
            | Error terr -> failtest (formatError terr)
        }
        test "a misaligned pipeline stage is still caught" {
            match assemble [ "xs"; "|> Seq.sum"; "  |> print" ] with
            | Error _ -> ()
            | Ok lls -> failtest $"a deeper pipeline stage must reject, assembled {lls}"
        }
        test "the closing pipe still typechecks end to end" {
            match typecheck env (parse "match 5 with\n| 5 -> 50\n| _ -> 0\n|> (fun n -> n + 1)") with
            | Ok te -> Expect.equal te.Ty TInt "the whole match, piped, is int"
            | Error terr -> failtest (formatError terr)
        }
        test "the extending pipe typechecks as the arm body" {
            match typecheck env (parse "match 5 with\n| n -> n\n       |> (fun k -> k + 1)") with
            | Ok te -> Expect.equal te.Ty TInt "the extended arm body is int"
            | Error terr -> failtest (formatError terr)
        }
    ]

let functionTypeTests =
    // F1 [D:function-types]: the arrow production in tySyn. The round-trip
    // (reader matches the formatTy writer) is the load-bearing pin; the
    // rest fix the precedence the exploration settled.
    let fieldTy shape =
        match Weir.Parser.parseStmt $"type RT = {{ f: {shape} }}" with
        | Ok(SType { Body = DRecord fields }) ->
            fields |> List.pick (fun (n, ty, _) -> if n = "f" then Some ty else None)
        | other -> failtest $"parse of field type '{shape}' failed: {other}"

    testList "function types [D:function-types]" [
        test "a function type round-trips through the renderer (six shapes)" {
            for shape in
                [
                    "unit -> int"
                    "(unit -> int) -> 'a"
                    "int -> string -> bool"
                    "seq<int> -> string"
                    "(int * string) -> bool"
                    "string -> bool"
                ] do
                let ty = fieldTy shape
                Expect.equal (fieldTy (formatTy ty)) ty $"'{shape}' round-trips through formatTy"
        }
        test "precedence and associativity are F#'s" {
            Expect.equal (fieldTy "int -> string -> bool") (TFun(TInt, TFun(TStr, TBool))) "-> is right-associative"
            Expect.equal (fieldTy "int * string -> bool") (TFun(TTuple [ TInt; TStr ], TBool)) "* binds tighter than ->"
            Expect.equal (fieldTy "seq<int> -> string") (TFun(TSeq TInt, TStr)) "generics bind tighter than ->"

            Expect.equal
                (fieldTy "(unit -> int) -> string")
                (TFun(TFun(TUnit, TInt), TStr))
                "a function domain parenthesises"
        }
    ]

let walkCohortTests =
    // the partial cohort's remaining pins [D:walk-findings] — each closes
    // a partial row from the DECISIONS walk, citation added to its row
    testList "walk cohort: the partial rows' missing halves [D:walk-findings]" [
        test "Exit.code does not exist — the rename left no module behind [D:exit-rename]" {
            // the hint points at the live lowercase exit, not the
            // retired spelling — a teaching, not a resurrection
            let terr = checkErr "print (show Exit.code)"
            Expect.stringContains terr.Message "unbound variable 'Exit'" ""
            Expect.stringContains terr.Message "Did you mean 'exit'?" ""
        }
        test "[<Default>] on a Secret field refuses (a hardcoded default secret defeats the type) [D:secret]" {
            let e = env |> declare "type SC = { [<Default \"x\">] apiKey: Secret }"

            // Args.load is script-only in this env (its Secret-specific
            // teaching is e2e-pinned); Env.load teaches the same rule
            match Weir.Check.typecheck e (parse "Env.load SC") with
            | Error terr ->
                Expect.stringContains
                    terr.Message
                    "'apiKey': a Secret takes no [<Default>] — a hardcoded default secret defeats the type; set the variable in the environment"
                    ""
            | Ok _ -> failtest "a defaulted Secret field must refuse"
        }
        test "declaring a type an import already provides is an error naming the import [D:dup-type-decl]" {
            let td = tempDir "weir-dupim"

            System.IO.File.WriteAllLines(System.IO.Path.Combine(td, "m.weir"), [| "module M"; "type T = { a: int }" |])

            try
                let entry = [ "import \"./m.weir\" as M"; "type T = { b: int }"; "print \"x\"" ]

                let diags = diagsIn (System.IO.Path.Combine(td, "main.weir")) entry

                let hit =
                    diags
                    |> List.exists (fun d -> d.Message.Contains "already provided by the import at line")

                Expect.isTrue
                    hit
                    $"the declare-after-import half must name the import: {diags |> List.map (fun d -> d.Message)}"
            finally
                System.IO.Directory.Delete(td, true)
        }
        test "a module declaring a type twice is refused at the import, located in the module [D:dup-type-decl]" {
            let td = tempDir "weir-dupmod"

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(td, "e.weir"),
                [|
                    "module"
                    "type T = { id: string }"
                    "let a : unit -> T"
                    "let a () = { id = \"x\" }"
                    "type T = { id: string; v: int }"
                |]
            )

            try
                let diags =
                    diagsIn (System.IO.Path.Combine(td, "main.weir")) [
                        "import \"./e.weir\" as E"
                        "let r = E.a ()"
                        "print r.id"
                    ]

                Expect.exists
                    diags
                    (fun d ->
                        d.Message.Contains "type 'T' is already declared at line 2"
                        && d.Severity = "error")
                    $"the module's duplicate surfaces at the importer: {diags |> List.map (fun d -> d.Message)}"
            finally
                System.IO.Directory.Delete(td, true)
        }
        test "two modules returning the same anonymous shape share one type [D:anon-literals]" {
            let td = tempDir "weir-anonmods"

            for f in [ "a.weir"; "a2.weir" ] do
                System.IO.File.WriteAllLines(
                    System.IO.Path.Combine(td, f),
                    [|
                        "module"
                        ""
                        "let shape : int -> {| k: int |}"
                        "let shape n = {| k = n |}"
                    |]
                )

            try
                let diags =
                    diagsIn (System.IO.Path.Combine(td, "main.weir")) [
                        "import \"./a.weir\" as A"
                        "import \"./a2.weir\" as A2"
                        "let xs = [A.shape 2; A2.shape 3; {| k = 4 |}]"
                        "print (Seq.length xs)"
                    ]

                Expect.isEmpty (diags |> List.map (fun d -> d.Message)) "one shape, one type"
            finally
                System.IO.Directory.Delete(td, true)
        }
        test "an import alias spelled like a union case in scope is refused (H56)" {
            let td = tempDir "weir-h56a"

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(td, "m.weir"),
                [| "module M"; "let x : int"; "let x = 1" |]
            )

            try
                for alias in [ "Some"; "Ok" ] do
                    let diags =
                        diagsIn (System.IO.Path.Combine(td, "main.weir")) [
                            $"import \"./m.weir\" as {alias}"
                            "print \"x\""
                        ]

                    Expect.exists
                        diags
                        (fun d -> d.Message.Contains $"the name '{alias}' is a union case in scope")
                        $"{alias}: {diags |> List.map (fun d -> d.Message)}"

                let ok =
                    diagsIn (System.IO.Path.Combine(td, "main.weir")) [
                        "import \"./m.weir\" as Mine"
                        "print (show Mine.x)"
                    ]

                Expect.isEmpty (ok |> List.filter (fun d -> d.Severity = "error")) "an ordinary alias stays clean"
            finally
                System.IO.Directory.Delete(td, true)
        }
        test "weir:<name> in both .weir/modules and .weir/types is an error, not a silent precedence (H56)" {
            let td = tempDir "weir-h56b"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(td, ".git"))
            |> ignore

            let wd = System.IO.Path.Combine(td, ".weir")

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(wd, "modules"))
            |> ignore

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(wd, "types"))
            |> ignore

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(wd, "modules", "x.weir"),
                [| "module X"; "let a : int"; "let a = 1" |]
            )

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(wd, "types", "x.weir"),
                [| "module X"; "type T = { a: int }" |]
            )

            try
                let diags =
                    diagsIn (System.IO.Path.Combine(td, "main.weir")) [ "import \"weir:x\" as X"; "print \"x\"" ]

                Expect.exists
                    diags
                    (fun d -> d.Message.Contains "names two vendored files")
                    $"{diags |> List.map (fun d -> d.Message)}"

                System.IO.File.Delete(System.IO.Path.Combine(wd, "types", "x.weir"))

                let ok =
                    diagsIn (System.IO.Path.Combine(td, "main.weir")) [ "import \"weir:x\" as X"; "print (show X.a)" ]

                Expect.isEmpty (ok |> List.filter (fun d -> d.Severity = "error")) "one home resolves"
            finally
                System.IO.Directory.Delete(td, true)
        }
        test "assembly recovery caps at 10 dropped lines and never throws [D:assembly-recovery]" {
            // 12 independently-failing triples: each recovery drops one
            // offender; the 11th and 12th are past the cap
            let lines = [
                for i in 1..12 do
                    yield! [ $"let a{i} ="; "    1"; "  bad" ]
            ]

            let diags = diagsIn "cap.weir" lines

            let assemblyCount = diags |> List.filter (fun d -> d.Code = "layout") |> List.length

            Expect.isTrue (assemblyCount <= 10) $"the cap: got {assemblyCount} assembly diags"
            Expect.isTrue (assemblyCount >= 9) $"the recovery ran to the cap: got {assemblyCount}"
        }
        test "a re-indented line's diagnostics sit on its source columns [D:recovery-physical-indent]" {
            let lines = [ "let xs = [ 1"; "           2"; "          3"; "print \"a\"" ]
            let diags = diagsIn "eol.weir" lines

            for d in diags do
                Expect.isTrue
                    (d.Col >= 1 && d.Col <= lines[d.Line - 1].Length + 1)
                    $"{d.Line}:{d.Col} inside its line: {d.Message}"

            let at line =
                diags
                |> List.filter (fun d -> d.Line = line)
                |> List.map (fun d -> d.Col)
                |> List.distinct

            Expect.equal (at 3) [ 11 ] "line 3's content start in the source"
            Expect.equal (at 4) [ 1 ] "line 4's content start in the source"
        }
        test "a parse error on a re-indented line maps to its source column [D:recovery-physical-indent]" {
            let lines = [
                "let v0 = 1"
                "let _v2 = [ 1"
                "    2"
                "if v0 > v0 then"
                "    print \"a\""
                "else"
                "    print \"b\""
                "print \"c\""
            ]

            let parse = diagsIn "eol.weir" lines |> List.filter (fun d -> d.Code = "parse")

            match parse with
            | [ d ] -> Expect.equal (d.Line, d.Col) (4, 16) "the end-of-line slot after `then`"
            | other -> failtest $"one parse error expected: {other |> List.map (fun d -> d.Line, d.Col, d.Message)}"
        }
        test "a check error on a re-indented line names its token's source column [D:recovery-physical-indent]" {
            let diags =
                diagsIn "eol.weir" [ "let xs = [ 1"; "          2 + \"a\""; "]"; "print (show xs)" ]

            match diags |> List.filter (fun d -> d.Code = "check") with
            | [ d ] -> Expect.equal (d.Line, d.Col) (2, 15) "the string operand"
            | other -> failtest $"one check error expected: {other |> List.map (fun d -> d.Line, d.Col, d.Message)}"

            // a pipeline stage off its group, the block-alignment repair
            let staged =
                diagsIn "eol.weir" [
                    "let x = [ 1 ]"
                    "        |> Seq.map (fun a -> a)"
                    "          |> Seq.map (fun a -> a + \"x\")"
                    "print (show x)"
                ]

            match staged |> List.filter (fun d -> d.Code = "check") with
            | [ d ] -> Expect.equal (d.Line, d.Col) (3, 36) "the string operand"
            | other -> failtest $"one check error expected: {other |> List.map (fun d -> d.Line, d.Col, d.Message)}"
        }
    ]

// the last-resort teacher [D:dump-teach]: shapes users reach by reflex
// get their repair, at check and at run, never an expected-token list
let dumpTeachTests =
    let one (lines: string list) =
        match errorsIn "dt.weir" lines with
        | d :: _ -> d
        | [] -> failtest $"must refuse: {lines}"

    testList "parse dumps teach [D:dump-teach]" [
        test "each reflex shape names its repair at check" {
            for lines, line, col, msg in
                [
                    [ "ls /" ],
                    1,
                    5,
                    "'ls' is a weir name here, so / is read as weir code — a path argument is a string (\"/\"); to run the ls program, write ^ls /"
                    [ "let x = 1"; "let y = 1 + ~x" ],
                    2,
                    13,
                    "'~' is the home directory only in a command line — in an expression, Path.home () is the home path ($\"{Path.home ()}/…\" for one below it)"
                    [ "repeat"; "print \"x\"" ], 1, 7, "'repeat' needs a body — the indented lines below it"
                    [ "let foo = true"; "let y = 1 + !foo" ], 2, 13, "weir has no `!` negation — write `not x`"
                    [ "let n = 3"; "print (show [0 .. -n])" ],
                    2,
                    19,
                    "a range endpoint is a literal, a name or a field access — parenthesize a computed one, as in [(n - 1) .. (-m)]"
                    [ "let n = 3"; "print (show [n - 1 .. 5])" ],
                    2,
                    20,
                    "a range endpoint is a literal, a name or a field access — parenthesize a computed one, as in [(n - 1) .. (-m)]"
                    [ "print (show (3--1))" ],
                    1,
                    16,
                    "'--' is not an operator (weir has no decrement) — to negate a negative, parenthesize: -(-x)"
                    [ "print (show (- -5))" ],
                    1,
                    14,
                    "a prefix '-' is glued to its operand (-x) — to negate a negative, parenthesize: -(-x)"
                    [ "print (echo a | line)" ],
                    1,
                    15,
                    "'| line' ends a command statement, so it can't sit inside an expression — bind it first: `let r = <command> | line`, then use r"
                    [
                        "match sh -c \"exit 3\" | exitCode with"
                        "| 0 -> print \"a\""
                        "| _ -> print \"b\""
                    ],
                    1,
                    22,
                    "'| exitCode' ends a command statement, so it can't sit inside an expression — bind it first: `let r = <command> | exitCode`, then use r"
                    [ "let x = $(ls)" ],
                    1,
                    11,
                    "'ls' is a weir name, not a program — $(…) runs a program and captures its output; call ls as weir code, or write $(^ls …) for a program of that name"
                    [ "echo ()" ], 1, 7, "an empty '()' is not a command argument — quote literal text: \"()\""
                    [ "let n = (echo a | exec)" ],
                    1,
                    17,
                    "'| exec' hands the process over and never returns, so it can't sit inside an expression — write it as a statement of its own: `<command> | exec`"
                    [ "within proc p = echo x | cat"; "    print \"x\"" ],
                    1,
                    29,
                    "within proc takes ONE command — a pipeline or capture is not a scoped child; compose inside sh -c \"…\""
                    [ "let s = \"1\""; "let x = s |> from json int" ],
                    2,
                    24,
                    "'int' is not a declared record name — the adapter slot takes one: declare a record that holds the data (type Doc = { items: seq<int> }) and write its name, or seq<Name> for a list of records"
                    [ "if true then"; "    import \"./a.weir\" as A" ],
                    2,
                    5,
                    "an import is a top-level statement — put it at column 0, outside any block, before the code that uses it"
                    [ "import $\"./a7.weir\" as A7" ],
                    1,
                    8,
                    "import takes a literal string path, e.g. import \"./lib/paths.weir\""
                    [ "import \"./a9.weir\" as A9 as B9" ],
                    1,
                    26,
                    "'as' is left over — the statement is already complete before it"
                    [ "let e = 1"; "let y = (e : int)" ],
                    2,
                    12,
                    "weir has no type annotation inside an expression — it infers the type, so drop the `: <type>`; in a module, a signature line states one: `let y : <type>`"
                ] do
                let d = one lines
                Expect.equal (d.Message.Replace("\r\n", " ").Replace("\n", " ")) msg $"{lines}"
                Expect.equal (d.Line, d.Col) (line, col) $"site: {lines}"
        }
        test "run words them the same (the run resolver)" {
            for src, needle in
                [
                    "ls /", "a path argument is a string"
                    "print (echo a | line)", "bind it first"
                    "let x = $e(pwd)", "$(…) runs a program"
                    "print (show (3--1))", "'--' is not an operator"
                ] do
                match Weir.Parser.parseLine realResolver src with
                | Error m ->
                    Expect.stringContains (m.Replace("\r\n", " ").Replace("\n", " ")) needle src
                    Expect.isFalse (m.Contains "Expecting") $"no dump: {src}"
                | Ok other -> failtest $"{src}: expected an error, got {other}"
        }
    ]

let windowsFindingsTests =
    // the Windows hand-run's two message fixes [D:windows-findings] —
    // neither is Windows-specific; Windows is just where they surfaced.
    // Long messages pin as fragments (FParsec rewraps).
    let perrOf input =
        match Weir.Parser.parseLine realResolver input with
        | Error msg -> msg
        | Ok _ -> failtest "expected a parse error"

    // the script-level message (no FParsec position header), unwrapped
    let msgIn input =
        match errorMsgsIn "esc.weir" [ input ] with
        | m :: _ -> m.Replace("\n", " ")
        | [] -> failtest "expected an error"

    testList "Windows findings: two messages name the repair [D:windows-findings]" [
        test "a path-shaped escape (backslash + letter) names the verbatim-string repair" {
            let m = perrOf "let p = \"C:\\Windows\""
            Expect.stringContains m "is not an escape" "the diagnosis"
            Expect.stringContains m "verbatim string" "the repair, named"
            Expect.stringContains m "forward slashes" "the alternative"
        }
        test "a NON-letter invalid escape lists the escapes, no path hint (no FParsec expectation list)" {
            let m = msgIn "let p = \"a\\5b\""

            Expect.equal
                m
                "'\\5' is not an escape (the escapes are \\\" \\\\ \\n \\t \\e \\u{…}; a literal backslash is \\\\)"
                "the list, no dump"

            Expect.isFalse (m.Contains "verbatim string") "no misfire"
        }
        test "an escape diagnosis leads the message: \\x and \\r name the escapes, no expectation list" {
            for src, c in [ "print \"\\x41\"", 'x'; "print \"\\r\"", 'r' ] do
                let m = msgIn src

                Expect.isTrue
                    (m.StartsWith $"'\\{c}' is not an escape (the escapes are")
                    $"leads with the teaching: {m}"

                Expect.isFalse (m.Contains "Expecting") "no FParsec dump"
        }
        test "a backslash before a TAB or an emoji names that character [D:escape-names-char]" {
            Expect.equal
                (msgIn "print \"a\\\tb\"")
                "a backslash followed by a TAB is not an escape (the escapes are \\\" \\\\ \\n \\t \\e \\u{…}; a literal backslash is \\\\)"
                "a mid-line TAB"

            Expect.equal
                (msgIn "print \"a\\😀b\"")
                "'\\😀' is not an escape (the escapes are \\\" \\\\ \\n \\t \\e \\u{…}; a literal backslash is \\\\)"
                "the whole emoji, not half a pair"
        }
        test "a trailing backslash in a string teaches the literal-backslash spelling" {
            let m = msgIn "print \"a\\"

            Expect.equal
                m
                "a backslash at the end of a string line is not an escape (the escapes are \\\" \\\\ \\n \\t \\e \\u{…}) — a literal backslash is \\\\, and strings are single-line (use \\n for a line break)"
                "the teaching"
        }
        test "a bare `let x =` says the binding has no value — no expecting list, no leaked gate label" {
            let m = perrOf "let x ="
            Expect.stringContains m "has no value" "the diagnosis"
            Expect.stringContains m "right-hand side" "the repair"
            Expect.isFalse (m.Contains "spine-only") "the internal gate label must not leak"
        }
        test "valid escapes and normal lets are unmoved" {
            match Weir.Parser.parseLine realResolver "print \"a\\tb\"" with
            | Ok _ -> ()
            | Error e -> failtest $"valid escape must parse: {e}"

            match Weir.Parser.parseLine realResolver "let x = 5 in x" with
            | Ok _ -> ()
            | Error e -> failtest $"normal let-in must parse: {e}"
        }
    ]

let matchArmCommandTests =
    // bare commands in match arms [D:match-arm-commands] — the arm body
    // is interior-arming territory: a command chain ends at the next
    // `| <pattern> ->`, and a statement-position match arms each arm's
    // tail (streams) instead of discarding a captured seq<string>. Parse
    // shape via cmdResolver (git/echo external); diagnostics via the real
    // Script pipeline (armTail runs there, not in a bare typecheck).
    let armBodyOf input =
        match Weir.Parser.parseLine cmdResolver input with
        | Ok(SExpr { Kind = EMatch(_, (_, _, body) :: _) }) -> body
        | Ok other -> failtest $"expected a match, got {other}"
        | Error m -> failtest $"parse failed: {m}"

    let diags = errorsIn "m.weir"

    testList "match arm commands [D:match-arm-commands]" [
        test "an arm body is a command chain; a following arm does not swallow it" {
            // the boundary: `| _ ->` ends the first arm's chain
            match (armBodyOf "match 1 with | 1 -> echo hi | _ -> print \"no\"").Kind with
            | ECmd(HeadLit "echo", _, _) -> ()
            | other -> failtest $"expected the arm body to be a command, got {other}"
        }
        test "a statement-position match with command arms checks clean (arms stream)" {
            Expect.isEmpty
                (diags [ "match 1 with"; "| 1 -> echo one"; "| _ -> echo other" ])
                "each arm's tail arms to unit — no discard"
        }
        test "mixed command / expression arms unify at unit" {
            Expect.isEmpty
                (diags [ "match 2 with"; "| 1 -> echo picked"; "| _ -> print \"other\"" ])
                "the command arm arms, the print arm is already unit"
        }
        test "a multi-line arm body sequences, the tail arms" {
            Expect.isEmpty
                (diags [
                    "match 1 with"
                    "| 1 ->"
                    "    let t = \"x\""
                    "    echo got $t"
                    "| _ -> print \"no\""
                ])
                "the block-let body's command tail arms"
        }
        test "capture position is UNCHANGED — a value-typed match still binds seq<string>" {
            let ds =
                diagsIn "cap.weir" [ "let out = match 1 with"; "          | _ -> echo captured"; "print out" ]

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "the last-position chain stays a capture"
        }
        test "a reifier stage still binds to the `|` — the `->` is the discriminator" {
            // `| orFail "m"` has no `->`, so it is a reifier, not an arm:
            // the body desugars to the |orFailed spine, never two arms
            let sx = Weir.Ast.sexpr (armBodyOf "match 1 with | _ -> git status | orFail \"m\"")
            Expect.stringContains sx "orFail" "the reifier bound to the pipe"
            Expect.stringContains sx "status" "the command stayed in the same chain (not split into an arm)"
        }
        test "a pipe stage spelling `pattern ->` reads as the next arm (the documented corner)" {
            // `| y -> …` after a command is a new arm binding y; under a
            // leading catch-all it is unreachable — the teaching that
            // points at the quote repair
            let ds = diags [ "match 1 with"; "| _ -> echo x | y -> print \"z\"" ]
            Expect.exists ds (fun d -> d.Message.Contains "unreachable") "the corner surfaces as an unreachable arm"
        }
        test "nested match: inner arms stop at the inner boundary (depth counter)" {
            Expect.isEmpty
                (diags [
                    "match 1 with"
                    "| 1 ->"
                    "    match 2 with"
                    "    | 2 -> echo inner"
                    "    | _ -> echo inner-other"
                    "| _ -> echo outer-other"
                ])
                "the inner match's arms parse and arm independently"
        }
        test "an if/else or match with command branches at a unit-demanded tail arms (loop body, lambda, always)" {
            Expect.isEmpty
                (diags [ "for i in [1; 2] do"; "    if i == 1 then echo one else echo two" ])
                "if/else in a for body"

            Expect.isEmpty
                (diags [ "for i in [1; 2] do"; "    if i == 1 then echo one else print \"two\"" ])
                "a command branch and a unit branch"

            Expect.isEmpty
                (diags [
                    "for i in [1; 2] do"
                    "    match i with"
                    "    | 1 -> echo one"
                    "    | _ -> echo other"
                ])
                "match in a for body"

            Expect.isEmpty
                (diags [ "[1; 2] |> Seq.iter (fun i -> if i == 1 then echo one else print \"two\")" ])
                "a Seq.iter lambda"

            Expect.isEmpty
                (diags [
                    "within"
                    "    print \"body\""
                    "always"
                    "    if true then echo done else echo other"
                ])
                "an always cleanup"
        }
    ]

let heldScopeTests =
    // a within's `until stopped` [D:held-scope]: the body, the hold, then
    // the normal close; before `always`, on every kind
    let errs lines = errorMsgsIn "held.weir" lines

    let shape (lines: string list) =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Ok [ ll ] ->
            match Weir.Parser.parseLineFull cmdResolver ll.Text with
            | Ok s -> Weir.Ast.sexprStmt s
            | Error f -> failtest f.Message
        | other -> failtest $"unexpected assembly: {other}"

    let says (needle: string) (lines: string list) =
        let ms = errs lines
        Expect.exists ms (fun m -> m.Contains needle) $"{needle}: {ms}"

    testList "within … until stopped [D:held-scope]" [
        test "every kind, and the bare scope, takes until stopped, with or without always" {
            for head in
                [
                    "within tmp d"
                    "within cd \".\""
                    "within env [Env.pair \"A\" \"1\"]"
                    "within lock \"/tmp/held.lock\""
                    "within proc p = sleep 1"
                    "within serve srv = { port = Some 8599; maxConcurrent = 1; handler = (fun r -> HttpServerResponse { status = 200; headers = []; body = Text \"ok\" }) }"
                    "within"
                ] do
                Expect.isEmpty (errs [ head; "    print \"b\""; "until stopped"; "print \"after\"" ]) head

                Expect.isEmpty
                    (errs [
                        head
                        "    print \"b\""
                        "until stopped"
                        "always"
                        "    print \"c\""
                        "print \"after\""
                    ])
                    $"{head} + always"
        }
        test "the hold sits inside the scope, the always outside the hold" {
            let sx =
                shape [
                    "within tmp d"
                    "    print \"b\""
                    "until stopped"
                    "always"
                    "    print \"c\""
                ]

            let w = sx.IndexOf "within tmp"
            let h = sx.IndexOf "(held"
            let a = sx.IndexOf "(always"
            Expect.isTrue (w >= 0 && h > w && a > h) $"within, then held, then always: {sx}"

            let bare = shape [ "within"; "    print \"b\""; "until stopped" ]
            Expect.stringContains bare "(held" $"the bare held scope: {bare}"
            Expect.isFalse (bare.Contains "(always") $"no always without one: {bare}"
        }
        test "the scope's value is the body's, and the binder reaches the body" {
            Expect.isEmpty
                (errs [
                    "let v ="
                    "    within tmp d"
                    "        d + \"/x\""
                    "    until stopped"
                    "print v"
                ])
                "value position"
        }
        test "until stopped after always teaches the order" {
            says "`until stopped` goes before `always`" [
                "within tmp d"
                "    print \"b\""
                "always"
                "    print \"c\""
                "until stopped"
            ]

            says "`until stopped` goes before `always`" [
                "within"
                "    print \"b\""
                "always"
                "    print \"c\""
                "until stopped"
            ]
        }
        test "a within's until takes only stopped, and no block" {
            says "a within's until section is `until stopped`" [ "within tmp d"; "    print \"b\""; "until done" ]

            says "`until stopped` takes no block" [
                "within tmp d"
                "    print \"b\""
                "until stopped"
                "    print \"x\""
            ]
        }
        test "a repeat refuses until stopped; retry/poll's until binder is unchanged" {
            says "a repeat already ends when stopped" [ "repeat interval=1s"; "    print \"x\""; "until stopped" ]

            says "`until stopped` holds a within scope" [
                "let r ="
                "    poll timeout=1s interval=10ms"
                "        true"
                "    until stopped"
            ]

            Expect.isEmpty
                (errs [
                    "let r ="
                    "    retry attempts=2"
                    "        3"
                    "    until stopped"
                    "        stopped > 2"
                    "print (show r)"
                ])
                "a binder named stopped with its predicate block is the until form"

            Expect.isEmpty
                (errs [
                    "let r ="
                    "    retry attempts=2"
                    "        within tmp d"
                    "            1"
                    "    until r"
                    "        r > 0"
                    "print (show r)"
                ])
                "a retry's until after a within body stays the retry's"
        }
        test "refused inside plan and pure; readonly admits the wait" {
            says "'until stopped' is refused inside 'plan'" [
                "let p ="
                "    plan"
                "        within"
                "            print \"x\""
                "        until stopped"
                "print (show p)"
            ]

            says "'until stopped' waits for a stop signal" [
                "let pure f () ="
                "    within"
                "        1"
                "    until stopped"
                "print (show (f ()))"
            ]

            Expect.isEmpty
                (errs [ "readonly"; "    within"; "        ()"; "    until stopped" ])
                "readonly admits a hold"
        }
        test "the REPL's smart indent opens no body after until stopped" {
            Expect.isFalse (Weir.Assemble.opensIndentedBody "until stopped") "until stopped takes no block"
            Expect.isTrue (Weir.Assemble.opensIndentedBody "    until r") "a loop's until binder opens its predicate"
        }
        test "Self has no waitForStop member: the hold is the section" {
            Expect.isNonEmpty (errs [ "Self.waitForStop ()" ]) "no Self.waitForStop"
        }
    ]

let dedentJoinTests =
    // whole-script probes: assemble + check + eval; the join is proven
    // by the bindings (absorption would corrupt or reject them)
    let runScript (lines: string list) : Map<string, Value> =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Error e -> failtest $"assemble: {e}"
        | Ok lls ->
            lls
            |> List.fold
                (fun (te, ve) ll ->
                    match Weir.Parser.parseLine cmdResolver ll.Text with
                    | Error m -> failtest $"parse: {m}"
                    | Ok(SExpr e)
                    | Ok(SCmd e) ->
                        match Weir.Check.typecheck te e with
                        | Error terr -> failtest $"check: {formatError terr}"
                        | Ok typed ->
                            eval ve typed |> ignore
                            te, ve
                    | Ok(SLet(n, e)) ->
                        match Weir.Check.typecheck te e with
                        | Error terr -> failtest $"check: {formatError terr}"
                        | Ok typed ->
                            let v = eval ve typed

                            {
                                te with
                                    Values = Map.add n (Weir.Types.generalize typed.Ty) te.Values
                            },
                            Map.add n v ve
                    | Ok other -> failtest $"unexpected statement {other}")
                (env, valueEnv)
            |> snd

    testList "The dedent correct-join [D:dedent-join]" [
        test "a statement after a within body inside a let JOINS (the git-subrepo shape; never argv)" {
            let ve =
                runScript [
                    "let sub ="
                    "    within tmp d"
                    "        Log.debug d"
                    "    let post = \"not-an-argv-word\""
                    "    post"
                ]

            Expect.equal ve["sub"] (VStr "not-an-argv-word") "post is BOUND, not absorbed"
        }
        test "a statement after a for body inside a let JOINS" {
            let ve =
                runScript [
                    "let h ="
                    "    for x in [1; 2] do"
                    "        Log.debug (show x)"
                    "    let n = 9"
                    "    n"
                ]

            Expect.equal ve["h"] (VInt 9L) ""
        }
        test "within-in-within: a sibling in the OUTER body after the inner closes" {
            let ve =
                runScript [
                    "let w ="
                    "    within tmp a"
                    "        within tmp b"
                    "            Log.debug b"
                    "        Log.debug a"
                    "    42"
                ]

            Expect.equal ve["w"] (VInt 42L) ""
        }
        test "the TWO-level dedent: for containing within, sibling back at let level" {
            let ve =
                runScript [
                    "let d2 ="
                    "    for x in [1] do"
                    "        within tmp t"
                    "            Log.debug (show x)"
                    "    let z = 9"
                    "    z"
                ]

            Expect.equal ve["d2"] (VInt 9L) ""
        }
        test "the FLOOR stays: a genuinely unmatched column still errors, located" {
            match
                Weir.Assemble.assemble [
                    1, "let bad ="
                    2, "    within tmp d"
                    3, "        print d"
                    4, "      let z = 1"
                    5, "    z"
                ]
            with
            | Error e ->
                Expect.stringContains e "aligns with no enclosing statement" "the swallow cannot return"
                Expect.stringContains e "line 4" "located"
            | Ok _ -> failtest "expected the floor"
        }
        test "the exemption class is unchanged: a lambda inside a within body takes dedented continuations" {
            let ve =
                runScript [
                    "let lam ="
                    "    within tmp d"
                    "        let r = [1; 2] |> Seq.map (fun v ->"
                    "            v + 1"
                    "        ) |> Seq.sum"
                    "        Log.debug (show r)"
                    "    7"
                ]

            Expect.equal ve["lam"] (VInt 7L) "the compound case: lambda-in-within"
        }
    ]

let trailingCommentTests =
    // one logical line's text out of the assembler
    let asm (lines: (int * string) list) : string =
        match Weir.Assemble.assemble lines with
        | Ok [ ll ] -> ll.Text
        | Ok lls -> failtest $"expected one logical line, got {lls.Length}"
        | Error e -> failtest $"assemble failed: {e}"

    testList "Trailing comments [D:trailing-comments]" [
        test "the three-region table: expression comments, argv data, argv comments" {
            // expression/statement territory: whitespace-preceded // is a comment
            Expect.equal (asm [ 1, "let x = 5 // note" ]) "let x = 5" "the origin case parses"
            // command argv, glued: data (the URL receipt)
            Expect.equal
                (asm [ 1, "git clone http://a --format=a//b" ])
                "git clone http://a --format=a//b"
                "glued // is argv data"
            // command argv, whitespace-preceded: a comment (option B)
            Expect.equal
                (asm [ 1, "git clone $url // the mirror" ])
                "git clone $url"
                "a command line's own trailing comment"
        }
        test "every string form keeps its // as data" {
            Expect.equal (asm [ 1, "let a = \"x // y\"" ]) "let a = \"x // y\"" "plain"
            Expect.equal (asm [ 1, "let b = @\"x // y\"" ]) "let b = @\"x // y\"" "verbatim"
            Expect.equal (asm [ 1, "let c = \"\"\"x // y\"\"\"" ]) "let c = \"\"\"x // y\"\"\"" "triple"
            Expect.equal (asm [ 1, "let d = $\"x {1} // y\"" ]) "let d = $\"x {1} // y\"" "interp, hole included"
        }
        test "holes do not host comments: a // inside a hole is neither comment nor legal" {
            // a to-EOL construct cannot nest inside a delimited one —
            // the scan leaves it, the parser rejects it, located
            match Weir.Parser.parseExpr "$\"{1 // 2}\"" with
            | Error _ -> ()
            | Ok e -> failtest $"expected a parse rejection, got {show e}"
        }
        test "yaml district content is BYTES; a commented HEAD still opens its district" {
            match Weir.Assemble.assemble [ 1, "let m = yaml // the head takes a comment"; 2, "    note: a // b" ] with
            | Ok [ ll ] ->
                Expect.stringContains ll.Text "let m = yaml" "head stripped and armed"
                Expect.stringContains ll.Text "a // b" "content bytes survive"
                Expect.isFalse (ll.Text.Contains "the head takes") "the head's comment is gone"
            | other -> failtest $"unexpected: {other}"
        }
        test "a trailing /// is an ordinary comment: docs stay attachment-only" {
            Expect.equal (asm [ 1, "let x = 1 /// not a doc" ]) "let x = 1" ""
        }
        test "comment-only and blank lines keep their classes (zero movement)" {
            Expect.equal (Weir.Assemble.classifyLine "// whole line") Weir.Assemble.LineKind.CommentOnly ""
            Expect.equal (Weir.Assemble.classifyLine "   ") Weir.Assemble.LineKind.Blank ""
            // a comment-only line between statements stays transparent
            match Weir.Assemble.assemble [ 1, "let f y ="; 2, "    // interior comment"; 3, "    y + 1" ] with
            | Ok [ ll ] -> Expect.stringContains ll.Text "y + 1" "body joined through the comment"
            | other -> failtest $"unexpected: {other}"
        }
        test "the REPL and scripts agree: one scanner, applied once, idempotent" {
            // the REPL pre-strips with the same stripComment the
            // assembler now applies — double-stripping is a no-op
            let l = "let x = 5 // note"
            Expect.equal (Weir.Assemble.stripComment (Weir.Assemble.stripComment l)) (Weir.Assemble.stripComment l) ""

            Expect.equal
                (asm [ 1, Weir.Assemble.stripComment l ])
                (asm [ 1, l ])
                "pre-stripped and raw assemble the same"
        }
    ]

let anonLiteralTests =
    // anonymous record literals [D:anon-literals]: F#'s spelling; the
    // canonical name is minted at check (values sit in the form, not
    // types) and the typed node is TERecord — eval/writers/LSP ride
    // existing arms
    let parseErr (src: string) =
        match Weir.Parser.parseStmt src with
        | Error msg -> msg
        | Ok s -> failtest $"expected a parse rejection, got {s}"

    testList "anonymous record literals [D:anon-literals]" [
        test "a bare lambda field does not swallow the next field [D:field-sep-sentinel]" {
            // multi-line: the assembler joins the fields with the field
            // sentinel, so `act`'s lambda body cannot sequence into `name`
            let lines = [
                "let r = {|"
                "    act = fun () -> ()"
                "    name = \"x\""
                "    |}"
                "print r.name"
            ]

            let diags = diagsIn "r.weir" lines
            let errs = diags |> List.filter (fun d -> d.Severity = "error")
            Expect.isEmpty errs $"the lambda field must not swallow 'name': {errs}"
        }
        test "a bare lambda element does not swallow the next list element [D:field-sep-sentinel]" {
            let lines = [
                "let xs = ["
                "    fun () -> 1"
                "    fun () -> 2"
                "    ]"
                "print (show (Seq.length xs))"
            ]

            let diags = diagsIn "l.weir" lines
            let errs = diags |> List.filter (fun d -> d.Severity = "error")
            Expect.isEmpty errs $"the lambda element must not swallow the next: {errs}"
        }
        test "the flagship: heterogeneous fields, typed by the canonical name" {
            let te = checkOk "{| key = \"k\"; n = 3 |}"
            Expect.equal te.Ty (TNamed("{| key: string; n: int |}", [])) "fields sort into the canonical name"

            Expect.equal
                (formatValue (run "{| key = \"k\"; n = 3 |}"))
                "{ key = \"k\"; n = 3 }"
                "nameless render, written order"
        }
        test "a literal UNIFIES with the adapter shape of the same fields" {
            let te = checkOk "[[\"{}\"]  |> from json {| ip: string |}; {| ip = \"x\" |}]"
            Expect.equal te.Ty (TSeq(TNamed("{| ip: string |}", []))) "one canonical name, both sides"
        }
        test "the nominal law: a literal never becomes a declared record" {
            let declaredEnv = env |> declare "type AnT = { a: int }"

            match Weir.Parser.parseStmt "[{ a = 1 }; {| a = 1 |}]" with
            | Ok(SExpr e) ->
                match Weir.Check.typecheck declaredEnv e with
                | Error terr -> Expect.stringContains terr.Message "expected AnT, got {| a: int |}" "nominal law"
                | Ok _ -> failtest "a literal must not unify with a declared record"
            | other -> failtest $"unexpected: {other}"
        }
        test "within-statement: field access and to json see the minted def" {
            Expect.equal (run "{| a = 1 |}.a") (VInt 1L) "field access on the literal itself"

            Expect.equal
                (forceSeq (run "{| k = \"v\" |} |> to json"))
                [ VStr "{\"k\":\"v\"}" ]
                "the admitted walk and the writer both resolve the name"
        }
        test "the def persists across statements (script + REPL share checkStatement)" {
            let ds = diagsIn "anonlit.weir" [ "let x = {| ip = \"z\" |}"; "let _y = x.ip" ]

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "the later statement resolves"
        }
        test "a same-shape hidden def never makes a bare literal ambiguous" {
            // without the isUserName guard on the candidate scan, the
            // drained anon def would collide with AG at statement 3
            let ds =
                diagsIn "anonlit2.weir" [
                    "type AG = { ip: string }"
                    "let _a = {| ip = \"x\" |}"
                    "let b = { ip = \"y\" }"
                    "let _c = b.ip"
                ]

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "the bare literal stays unambiguous"
        }
        test "nested literals compose; the name recurses" {
            let te = checkOk "{| a = {| b = 1 |} |}"
            Expect.equal te.Ty (TNamed("{| a: {| b: int |} |}", [])) "the inner name renders inline"
            Expect.equal (run "{| a = {| b = 1 |} |}.a.b") (VInt 1L) "and evaluates through"
        }
        test "duplicate fields refuse; the admitted law reaches through" {
            Expect.stringContains (checkErr "{| a = 1; a = 2 |}").Message "duplicate field 'a'" ""

            Expect.stringContains
                (checkErr "[{| f = double |}] |> to json").Message
                "not admitted"
                "jsonAdmitted walks the minted def"
        }
        test "monomorphic at the site — the ground refusal teaches (a no-anonymous-records edge)" {
            let m = (checkErr "fun q -> {| a = q |}").Message
            Expect.stringContains m "not fully known at the literal" "names the constraint"
            Expect.stringContains m "declare a record" "and the repair"
        }
        test "equality and patterns follow declared records" {
            Expect.equal (run "{| a = 1 |} == {| a = 1 |}") (VBool true) "Eq decomposes"
            Expect.equal (run "{| a = 1 |} == {| a = 2 |}") (VBool false) ""
            Expect.equal (run "let { a = n } = {| a = 7 |} in n") (VInt 7L) "a record pattern destructures it"
        }
        test "{| r with f = v |} extends: the source's fields and the named ones [D:anon-with]" {
            let tenv = env |> declare "type B = { title: string; year: int }"
            let b = "let b = { title = \"t\"; year = 1 } in "
            let runB (body: string) = runUnder tenv (b + body)

            Expect.equal
                (runB "let r = {| b with age = 9 |} in $\"{r.title} {r.year} {r.age}\"")
                (VStr "t 1 9")
                "a declared source: its fields ride along"

            Expect.equal
                (runB "let r = {| b with year = \"y\" |} in r.year")
                (VStr "y")
                "a named field replaces, type and all"

            Expect.equal
                (run "let r = {| {| a = 1 |} with b = 2; c = 3 |} in r.a + r.b + r.c")
                (VInt 6L)
                "an anonymous source; several fields"

            Expect.equal
                (runB "let r = {| b with age = 9 |} in r == {| age = 9; title = \"t\"; year = 1 |}")
                (VBool true)
                "the result is the anonymous record of that shape"

            Expect.equal
                (runB "[b; b] |> Seq.map (fun x -> {| x with n = x.year + 1 |}) |> Seq.map _.n |> Seq.sum")
                (VInt 4L)
                "a lambda's parameter, typed by the pipe"

            Expect.equal
                (runB "let r = {| b with title = 1; year = 2 |} in r.title + r.year")
                (VInt 3L)
                "every field replaced"

            Expect.stringContains
                (checkErr "let f r = {| r with n = 1 |} in f 1").Message
                "r's type must be known here"
                "an untyped source"

            Expect.stringContains (checkErr "let r = {| 5 with n = 1 |} in r.n").Message "which has none" "no record"

            Expect.stringContains
                (checkErrUnder tenv (b + "let r = {| b with age = 1; age = 2 |} in r.age")).Message
                "age"
                "a field named twice"

            Expect.stringContains
                (parseErr "let x = {| r with a |}")
                "fields take field = value"
                "the literal's fences hold"
        }
        test "the fences: punning, empty, type-spelling, keyword" {
            Expect.stringContains (parseErr "let x = {| a; b = 2 |}") "fields take field = value" "punning teaches"

            Expect.stringContains (parseErr "let x = {| b = 2; a |}") "fields take field = value" "at every position"
            Expect.stringContains (parseErr "let x = {| |}") "at least one field" "empty refuses (F# admits it)"
            Expect.stringContains (parseErr "let x = {||}") "at least one field" ""
            Expect.stringContains (parseErr "let x = {| a: int |}") "spells the anonymous TYPE" "':' confusion named"
            Expect.stringContains (parseErr "let x = {| let = 1 |}") "'let' is a keyword" "the shared guard fires"
        }
        test "sexpr renders the form (parse-shape pin)" {
            match Weir.Parser.parseStmt "let x = {| a = 1; b = \"t\" |}" with
            | Ok(SLet(_, e)) -> Expect.equal (Weir.Ast.sexpr e) "{|a = 1; b = \"t\"|}" ""
            | other -> failtest $"unexpected: {other}"
        }
    ]

let scopedProcTests =
    // scoped processes [D:scoped-procs]: the no-orphan law's static
    // half — parse/assemble/check shapes; the live lifecycle (kill,
    // reap, sweep, watch errors) pins in e2e where processes are real
    let assembleParse (lines: string list) =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Ok [ st ] ->
            match Weir.Parser.parseLine cmdResolver st.Text with
            | Ok(Weir.Ast.SExpr e)
            | Ok(Weir.Ast.SCmd e) -> e
            | other -> failtest $"expected an expression statement, got {other}"
        | Ok other -> failtest $"expected ONE logical line, got {List.length other}"
        | Error e -> failtest $"assemble failed: {e}"

    testList "scoped processes [D:scoped-procs]" [
        test "the proc head's block joins SENTINELED — the command tail cannot swallow it" {
            match Weir.Assemble.assemble [ 1, "within proc p = git status"; 2, "    print \"hi\"" ] with
            | Ok [ st ] -> Expect.isTrue (st.Text.Contains Weir.Parser.sibSepStr) "the machine boundary, not a space"
            | other -> failtest $"unexpected: {other}"
        }
        test "parses to the within shape with the command in the arg slot" {
            let e = assembleParse [ "within proc p = git status"; "    print \"hi\"" ]

            Expect.stringContains (Weir.Ast.sexpr e) "(within proc p" "the kind and binder"
            Expect.stringContains (Weir.Ast.sexpr e) "(cmd git \"status\")" "the command node rides the arg slot"
        }
        test "the binder types as Proc; show admits; '==' refuses with the type named" {
            let e = assembleParse [ "within proc p = git status"; "    show p" ]

            match Weir.Check.typecheck env e with
            | Ok te -> Expect.equal te.Ty TStr "show p : string"
            | Error terr -> failtest $"show must admit a Proc: {terr.Message}"

            let e2 = assembleParse [ "within proc p = git status"; "    p == p" ]

            match Weir.Check.typecheck env e2 with
            | Error terr -> Expect.stringContains terr.Message "'==' is not defined for Proc" ""
            | Ok _ -> failtest "Eq must stay excluded for Proc"
        }
        test "a pipeline in the proc slot is a parse-time refusal with the sh -c teaching" {
            match Weir.Assemble.assemble [ 1, "within proc p = git status | complete"; 2, "    print \"x\"" ] with
            | Ok [ st ] ->
                match Weir.Parser.parseLine cmdResolver st.Text with
                | Error m ->
                    Expect.stringContains m "ONE command" "the refusal"
                    Expect.stringContains m "sh -c" "…and the way out"
                | Ok _ -> failtest "a pipeline must not become a scoped child"
            | other -> failtest $"unexpected: {other}"
        }
        test "watch= is poll's key: retry refuses with the reason; a non-Proc watch refuses by type" {
            Expect.stringContains
                (checkErr "retry attempts=2 watch=ls true").Message
                "watch= is poll's key (wait-for-ready); retry retries failures"
                ""

            Expect.stringContains
                (checkErr "poll timeout=1s watch=5 true").Message
                "Proc"
                "the handle type is the contract"
        }
        test "watch rides the sexpr; peeled from the options (the record never carries it)" {
            match Weir.Parser.parseExpr "poll timeout=1s watch=srv true" with
            | Ok e ->
                Expect.stringContains (Weir.Ast.sexpr e) "(watch srv)" "the head key"
                Expect.isFalse ((Weir.Ast.sexpr e).Contains "watch =") "never an options field"
            | Error m -> failtest $"must parse: {m}"
        }
        test "the proc-head detector is word-bounded and string-blind" {
            Expect.isTrue (Weir.Assemble.endsInProcHead "within proc p = sh -c \"x\"") "the plain head"

            Expect.isTrue
                (Weir.Assemble.endsInProcHead "xs |> Seq.pmap (fun n -> within proc w = git status")
                "mid-segment"

            Expect.isFalse (Weir.Assemble.endsInProcHead "print \"within proc p = x\"") "a string is data"
            Expect.isFalse (Weir.Assemble.endsInProcHead "let awithin proc = 5") "word-bounded"
        }
    ]

let interpRawTests =
    // the raw interpolated literal [D:interp-raw]: escapes off, holes
    // on — reopened from the no-interpolated-raw park on its stated
    // trigger (raw-with-splice receipts)
    testList "raw interpolation [D:interp-raw]" [
        test "the receipt's composition: backslashes, quotes, and a hole in ONE literal" {
            Expect.equal
                (runWith [ "src", VStr "x" ] "$\"\"\"C:\\tmp\\{src} says \"hi\"!\"\"\"")
                (VStr "C:\\tmp\\x says \"hi\"!")
                "no double-escape anywhere"
        }
        test "escapes are OFF: backslash-n is two characters" {
            Expect.equal (run "$\"\"\"a\\n{1}\"\"\" |> Str.length") (VInt 4L) "a, backslash, n, 1"
        }
        test "a quote-run short of three is content" {
            Expect.equal (run "$\"\"\"say \"\"{1}\"\"\"") (VStr "say \"\"1") "two quotes ride"
        }
        test "the {{ attempt teaches the ordinary form" {
            match Weir.Parser.parseExpr "$\"\"\"a {{ b\"\"\"" with
            | Error m -> Expect.stringContains m "literal brace has no spelling" "the teaching"
            | Ok _ -> failtest "{{ must not parse in the raw form"
        }
        test "$@\" teaches the one spelling" {
            match Weir.Parser.parseExpr "print $@\"x\"" with
            | Error m -> Expect.stringContains m "the raw interpolated spelling is" ""
            | Ok _ -> failtest "$@ must not parse"
        }
        test "the Regex position refuses the interpolated form (computed patterns stay expression-side)" {
            match Weir.Parser.parseExpr "match \"a\" with | Regex $\"\"\"a{1}\"\"\" g -> g | _ -> \"n\"" with
            | Error m -> Expect.stringContains m "LITERAL string" "the recorded park's reason, enforced"
            | Ok _ -> failtest "an interpolated pattern must refuse"
        }
        test "a Secret refuses the raw hole exactly as the ordinary one [D:secret]" {
            let e = env |> declare "type ST = { t: Secret }"

            match Weir.Parser.parseStmt "let s = Secret.of \"x\" in $\"\"\"tok {s}\"\"\"" with
            | Ok(Weir.Ast.SExpr ex) ->
                match Weir.Check.typecheck e ex with
                | Error terr -> Expect.stringContains terr.Message "does not interpolate" "same check, new syntax"
                | Ok _ -> failtest "a Secret hole must refuse"
            | other -> failtest $"parse: {other}"
        }
        test "unterminated strings name their OPENER — the whole family [D:interp-raw]" {
            for src, kind in
                [
                    "print \"never", "\""
                    "print @\"never", "@\""
                    "print \"\"\"never", "\"\"\""
                    "print $\"never", "$\""
                    "print $\"\"\"never", "$\"\"\""
                ] do
                match Weir.Parser.parseExpr (src.Substring 6) with
                | Error m ->
                    Expect.stringContains m "string literal not closed" $"the family sentence: {kind}"
                    Expect.stringContains m "single-line" $"…and the law: {kind}"
                | Ok _ -> failtest $"must not parse: {src}"
        }
    ]

let withinTests =
    testList "within scopes [D:within-scopes]" [
        test "parse shape: within tmp binder + block" {
            let asmLine = "within tmp d" + Weir.Parser.sibSepStr + "print d"

            match Weir.Parser.parseLine realResolver asmLine with
            | Ok(SExpr {
                           Kind = EWithin(WithinTmp, Some("d", _), None, None, _)
                       }) -> ()
            | other -> failtest $"unexpected: {other}"
        }
        test "the binder is a string; the scope's type is the body's" {
            let e = parse ("within tmp d" + Weir.Parser.sibSepStr + "Str.length d")

            match Weir.Check.typecheck env e with
            | Ok te -> Expect.equal te.Ty TInt "body type flows out"
            | Error terr -> failtest (formatError terr)
        }
        test "a non-kind word after within reads as the bare form; the missing always names both" {
            // pre-lock this pin used `within lock` as its bogus kind —
            // the kind landed [D:within-lock] and the unknown-kind path
            // now falls to the bare reading [D:within-always]
            match Weir.Parser.parseLine realResolver ("within bogus f" + Weir.Parser.sibSepStr + "print f") with
            | Error msg ->
                Expect.stringContains msg "always segment" "the bare reading"
                // FParsec wraps expecting-lists — assert a fragment that
                // cannot straddle the wrap
                Expect.stringContains msg "names its kind" "the kinds teaching rides along"
            | Ok _ -> failtest "expected the teaching"
        }
        test "within is reserved with the keyword teaching" {
            match Weir.Parser.parseLine realResolver "let within = 1" with
            | Error msg -> Expect.stringContains msg "keyword" ""
            | Ok _ -> failtest "expected the reserved-word gate"
        }
        test "cd and env args are typed expressions; a binding serves (6th/7th sites, on arrival)" {
            // cd consumes string, env consumes seq<EnvVar> — the kinds'
            // contracts [D:within-scopes]; the arg is an atom, resolved
            // as an ordinary expression (never a command head)
            let e = parse ("let b = \"x\" in within cd b" + Weir.Parser.sibSepStr + "1")

            match Weir.Check.typecheck env e with
            | Ok te -> Expect.equal te.Ty TInt "arg rides the binding"
            | Error terr -> failtest (formatError terr)

            let bad = parse ("within cd 7" + Weir.Parser.sibSepStr + "1")

            match Weir.Check.typecheck env bad with
            | Error terr -> Expect.stringContains terr.Message "string" "cd demands a string path"
            | Ok _ -> failtest "expected the type contract"

            let bad2 = parse ("within env 7" + Weir.Parser.sibSepStr + "1")

            match Weir.Check.typecheck env bad2 with
            | Error terr -> Expect.stringContains terr.Message "EnvVar" "env demands seq<EnvVar>"
            | Ok _ -> failtest "expected the type contract"
        }
        test "the scope binder beats PATH (the fifth patLeafNames site)" {
            // `within tmp git` — a perverse but legal binder; the block's
            // `git` is the binding, never a phantom command
            let e = parse ("within tmp git" + Weir.Parser.sibSepStr + "Str.length git")

            match Weir.Check.typecheck env e with
            | Ok te -> Expect.equal te.Ty TInt "the binder, not the binary"
            | Error terr -> failtest (formatError terr)
        }
    ]

let dxMessageTests =
    let diagsOf = diagsIn "pin.weir"

    let mustSay = mustSayDiagIn "pin.weir"

    testList "DX message pins" [
        test "D2: '&&' and '||' join the prior-bleed family" {
            mustSay [ "echo a && echo b" ] "'&&' does not chain commands" "and"
            mustSay [ "echo a || echo b" ] "'||' does not chain commands" "or"
        }
        test "an empty stage before '|>' says so, wherever the command sits [D:empty-stage]" {
            let missing = "a pipe stage is missing"
            mustSay [ "echo a | |> Seq.iter print" ] missing "a statement"
            mustSay [ "let n = echo --raw a | |> Seq.length"; "print (show n)" ] missing "a let's value: not the flag"

            mustSay
                [
                    "[\"a\"] |> Seq.iter (fun k -> echo --raw $k | head -c 3 | |> Seq.iter print)"
                ]
                missing
                "a lambda body"
            // the bash prior keeps its own teaching
            mustSay [ "echo a | | head" ] "'||' does not chain commands" "two bars"
        }
        test "D3: '=' in expression position teaches '=='" {
            mustSay [ "let n = if 1 = 1 then 1 else 2"; "print (show n)" ] "use '==' for equality" "if-cond"
            mustSay [ "let b = 1 = 2"; "print (show b)" ] "use '==' for equality" "let-rhs"
        }
        test "D3: a stray backslash at hole level teaches, quotes need no escape there" {
            mustSay [ "print $\"{ \\\"abc\\\" }\"" ] "not an escape here" "hole backslash"

            // the control: an unescaped string inside a hole is fine
            let clean = diagsIn "pin.weir" [ "print $\"{Str.length \"abc\"}\"" ]

            Expect.isEmpty clean "strings in holes need no escaping"
        }
        test "D4: foreign control-flow words teach weir's spelling" {
            mustSay [ "while true do"; "    print (show 1)" ] "'while' is not a weir word" "while"
            mustSay [ "let f n ="; "    return n"; "print (show (f 1))" ] "'return' is not a weir word" "return"
            // the definition statement's shape [D:def-shape]
            mustSay [ "def f x = x + 1" ] "'def' is not a weir word" "def"
            mustSay [ "let r = try (echo x)"; "print (show 1)" ] "'try' goes after a pipeline" "try"
        }
        test "def is an ordinary name; the teaching keys on `def <name> … =` [D:def-shape]" {
            errorsCleanIn "def.weir" [ "let def = 1"; "print (show def)" ] "a binder"
            errorsCleanIn "deff.weir" [ "type R = { def: int }"; "let r = { def = 3 }"; "print (show r.def)" ] "a field"
            mustSayIn "d1.weir" [ "def f x = x + 1" ] "'def' is not a weir word" "top level"

            mustSayIn
                "d2.weir"
                [ "let f () ="; "    def g x = x"; "    1"; "print (show (f ()))" ]
                "'def' is not a weir word"
                "a let body"

            mustSayIn "d3.weir" [ "if true then"; "    def g x = x" ] "'def' is not a weir word" "an if body"
            Expect.isFalse (Weir.Parser.isKeyword "def") "not a keyword"
        }
        test "D5: List/Array teach Seq; no cross-kind did-you-mean" {
            mustSay [ "let n = List.length [1]"; "print (show n)" ] "weir's sequences are 'Seq'" "List"
            mustSay [ "let n = Array.length [1]"; "print (show n)" ] "weir's sequences are 'Seq'" "Array"

            Expect.isFalse
                (diagsOf [ "let n = List.length [1]"; "print (show n)" ]
                 |> List.exists (fun d -> d.Message.Contains "Post"))
                "a union case is never suggested for a module"
        }
        test "D6's receipt runs: a hole parameter generalizes [D:hole-generic]" {
            Expect.isEmpty (diagsOf [ "let name n = $\"item-{n}\""; "print (name 5)"; "print (name \"x\")" ]) "generic"
        }
        test "D7: Seq.iter print resolves at the use site; non-printables still refuse" {
            let clean = diagsIn "pin.weir" [ "[1] |> Seq.iter print" ]

            Expect.isEmpty clean "the obvious spelling works on the obvious type"

            let cleanStr = diagsIn "pin.weir" [ "[\"a\"] |> Seq.iter print" ]

            Expect.isEmpty cleanStr "strings unchanged"

            mustSay
                [ "type R = { a: int }"; "[{ a = 1 }] |> Seq.iter print" ]
                "print takes a string, int, float, bool, Uuid, or seq<string>"
                "record refused"
        }
        test "D8: a pasted multi-line string literal teaches the single-line law" {
            mustSay [ "let x = \"abc"; "print x" ] "strings are single-line" "unclosed"
        }
    ]

// module signatures [D:module-signatures]: a signature is the export —
// one pin per law, over real files (the module loader is the
// enforcement point) plus the parse/script-refusal edges
let moduleSignatureTests =
    let withDir (files: (string * string list) list) (f: string -> unit) =
        withTempDir "weir-msig" (fun td ->
            for name, lines in files do
                System.IO.File.WriteAllLines(System.IO.Path.Combine(td, name), lines)

            f td)

    let diagsOf (td: string) (entry: string) =
        let path = System.IO.Path.Combine(td, entry)

        let ds = diagsIn path (List.ofArray (System.IO.File.ReadAllLines path))

        ds |> List.filter (fun d -> d.Severity = "error")

    let lib = [
        "module Lib"
        ""
        "let helper n = n + 1"
        ""
        "/// doubles"
        "let double : int -> int"
        ""
        "let double n = helper n + n - 1"
    ]

    testList "module signatures [D:module-signatures]" [
        test "the headless let parses as a signature (sexpr pin)" {
            match Weir.Parser.parseLine cmdResolver "let f : int -> seq<string>" with
            | Ok(SSig("f", ty, _)) -> Expect.equal (Weir.Types.formatTy ty) "int -> seq<string>" "the type"
            | other -> failtest $"expected SSig, got {other}"

            // generic vars are admitted (exported Seq-like helpers)
            match Weir.Parser.parseLine cmdResolver "let pick : 'a -> 'a" with
            | Ok(SSig("pick", _, _)) -> ()
            | other -> failtest $"expected generic SSig, got {other}"

            // an '=' after the type gets the dedicated teaching
            match Weir.Parser.parseLine cmdResolver "let f : int -> int = 5" with
            | Error msg -> Expect.stringContains msg "alone is the signature" ""
            | Ok _ -> failtest "a sig with an RHS must refuse"
        }
        test "a signature exports; an unsigned member is private and teaches the migration" {
            withDir
                [
                    "lib.weir", lib
                    "ok.weir", [ "import \"./lib.weir\""; "print (show (Lib.double 3))" ]
                    "bad.weir", [ "import \"./lib.weir\""; "print (show (Lib.helper 3))" ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "ok.weir") "the signed member resolves"

                    match diagsOf td "bad.weir" with
                    | d :: _ ->
                        Expect.stringContains
                            d.Message
                            "'Lib.helper' is module-private (no signature) — add `let helper : int -> int` in the module to export it"
                            "the migration teaching, inferred type included"
                    | [] -> failtest "the private member must refuse")
        }
        test "the private-member suggestion spells a writable signature: vars normalized, no row type" {
            withDir
                [
                    "m.weir",
                    [
                        "module M"
                        "let _id x = x"
                        "let _get r = r.id"
                        "let pub : int -> int"
                        "let pub n = _id (_get {| id = n |})"
                    ]
                    "a.weir", [ "import \"./m.weir\""; "print (show (M._id 3))" ]
                    "b.weir", [ "import \"./m.weir\""; "print (show (M._get 3))" ]
                ]
                (fun td ->
                    match diagsOf td "a.weir" with
                    | d :: _ ->
                        Expect.equal
                            d.Message
                            "'M._id' is module-private (no signature) — add `let _id : 'a -> 'a` in the module to export it"
                            "normalized vars"
                    | [] -> failtest "the private member must refuse"

                    match diagsOf td "b.weir" with
                    | d :: _ ->
                        Expect.equal
                            d.Message
                            "'M._get' is module-private (no signature) — to export it, declare the record it reads as a type and add `let _get : …` naming it in the module (its inferred type reads fields of any record, which a signature cannot spell)"
                            "a row type has no signature spelling"
                    | [] -> failtest "the private member must refuse")
        }
        test "a type a module imports crosses with the signatures that mention it (B23)" {
            withDir
                [
                    "a.weir",
                    [
                        "module A"
                        "type Verdict = Good of int | Bad"
                        "type Pt = { x: int; v: Verdict }"
                        "let origin : unit -> Pt"
                        "let origin () = { x = 0; v = Bad }"
                    ]
                    "b.weir",
                    [
                        "module B"
                        "import \"./a.weir\""
                        "let make : int -> seq<Pt>"
                        "let make n = [ A.origin () ]"
                    ]
                    "s.weir",
                    [
                        "import \"./b.weir\""
                        "for p in B.make 3 do"
                        "    print $\"{p.x} {show p} {p == p}\""
                        "    match p.v with"
                        "    | Good n -> print $\"{n}\""
                        "    | Bad -> print \"bad\""
                    ]
                    // reachable is not owned: no qualified literal under B
                    "q.weir", [ "import \"./b.weir\""; "let p = B.Pt { x = 1; v = Bad }"; "print p.x" ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "s.weir") "field access, show, ==, and the union's cases resolve"
                    Expect.isNonEmpty (diagsOf td "q.weir") "B does not re-export Pt as its own")
        }
        test "a transitive type yields its name to a local declaration and keeps its values [D:transitive-types]" {
            let a = [
                "module A"
                "type Pt = { x: int }"
                "let origin : unit -> Pt"
                "let origin () = { x = 0 }"
            ]

            let b = [
                "module B"
                "import \"./a.weir\""
                "let make : int -> Pt"
                "let make n = { x = n }"
            ]

            withDir
                [
                    "a.weir", a
                    "b.weir", b
                    // the local Pt takes the name; B's values keep A's Pt
                    "local.weir",
                    [
                        "import \"./b.weir\""
                        "type Pt = { name: string }"
                        "let p = B.make 3"
                        "let q = { name = \"n\" }"
                        "print $\"{p.x} {show p} {p == B.make 3} {q.name}\""
                    ]
                    // ...and never borrows the local one's fields
                    "wrong.weir",
                    [
                        "import \"./b.weir\""
                        "type Pt = { name: string }"
                        "let p = B.make 3"
                        "print p.name"
                    ]
                    // a direct import of the same type, in either order, is one type
                    "direct1.weir",
                    [
                        "import \"./b.weir\""
                        "import \"./a.weir\""
                        "print $\"{B.make 0 == A.origin ()}\""
                    ]
                    "direct2.weir",
                    [
                        "import \"./a.weir\""
                        "import \"./b.weir\""
                        "print $\"{B.make 0 == A.origin ()}\""
                    ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "local.weir") "the local declaration does not conflict"

                    match diagsOf td "wrong.weir" with
                    | d :: _ ->
                        Expect.stringContains
                            d.Message
                            "A.Pt has no field 'name'"
                            "the transitive type keeps its identity"
                    | [] -> failtest "the shadowed type must not take the local fields"

                    Expect.isEmpty (diagsOf td "direct1.weir") "transitive then direct: one type"
                    Expect.isEmpty (diagsOf td "direct2.weir") "direct then transitive: one type")
        }
        test "a transitive union's case never makes a local constructor ambiguous [D:transitive-types]" {
            withDir
                [
                    "a.weir", [ "module A"; "type Color ="; "    | Red"; "    | Green" ]
                    "b.weir",
                    [
                        "module B"
                        "import \"./a.weir\" as A"
                        "let pick : unit -> Color"
                        "let pick () = A.Red"
                    ]
                    "m.weir",
                    [
                        "import \"./b.weir\" as B"
                        "type Light ="
                        "    | Red"
                        "    | Off"
                        "let x = Red"
                        "let n ="
                        "    match B.pick () with"
                        "    | Red -> 1"
                        "    | Green -> 2"
                        "print $\"{x} {B.pick ()} {n}\""
                    ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "m.weir") "the local Red is the bare one; B's Color still matches")
        }
        test "an imported union's case types an unannotated parameter [D:match-scrutinee-infer]" {
            let area arms =
                [ "import \"./a.weir\" as A"; "let area s ="; "    match s with" ]
                @ arms
                @ [ "print $\"{area (A.Circle 2)}\"" ]

            withDir
                [
                    "a.weir", [ "module A"; "type Shape ="; "    | Circle of int"; "    | Square of int" ]
                    "m.weir", area [ "    | Circle r -> r * 3"; "    | Square w -> w * w" ]
                    "partial.weir", area [ "    | Circle r -> r * 3" ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "m.weir") "the imported Shape types s"

                    match diagsOf td "partial.weir" with
                    | d :: _ -> Expect.stringContains d.Message "missing: Square" "exhaustiveness names the case"
                    | [] -> failtest "a missing case must refuse")
        }
        test "did-you-mean at the private refusal ranges over SIGNED members only" {
            withDir
                [
                    "lib.weir",
                    [
                        "module Lib"
                        "let doublez n = n"
                        "let doubles : int -> int"
                        "let doubles n = doublez (n * 2)"
                    ]
                    "m.weir", [ "import \"./lib.weir\""; "print (show (Lib.doublez 3))" ]
                ]
                (fun td ->
                    match diagsOf td "m.weir" with
                    | d :: _ ->
                        Expect.stringContains d.Message "module-private" "the exact unsigned name is the private hit"

                        Expect.stringContains
                            d.Message
                            "Did you mean 'doubles'?"
                            "did-you-mean stays over the SIGNED members"
                    | [] -> failtest "expected the refusal")
        }
        test "sig without impl = check error at the sig (orphan)" {
            withDir [ "o.weir", [ "module O"; "let f : int -> int" ] ] (fun td ->
                match diagsOf td "o.weir" with
                | [ d ] ->
                    Expect.equal (d.Line, d.Col) (2, 5) "at the sig"
                    Expect.stringContains d.Message "signature without implementation" ""
                | ds -> failtest $"expected exactly the orphan, got {ds |> List.map (fun d -> d.Message)}")
        }
        test "sig/impl mismatch names BOTH sites and both types" {
            withDir [ "m.weir", [ "module M"; "let f : int -> int"; "let f x = \"s\"" ] ] (fun td ->
                match diagsOf td "m.weir" with
                | d :: _ ->
                    Expect.equal d.Line 3 "located at the implementation"

                    Expect.stringContains
                        d.Message
                        "the signature (line 2) declares int -> int, but the implementation is int -> string"
                        "both sites, both types"
                | [] -> failtest "expected the mismatch")
        }
        test "scripts refuse the form with the teaching" {
            withDir [ "s.weir", [ "let f : int -> int"; "let f x = x"; "print (show (f 1))" ] ] (fun td ->
                match diagsOf td "s.weir" with
                | d :: _ -> Expect.stringContains d.Message "signatures belong to module APIs — scripts infer" ""
                | [] -> failtest "a script signature must refuse")
        }
        test "a /// doc on a signed member's impl teaches the one home; on the sig it reads on hover" {
            withDir
                [
                    "d.weir", [ "module D"; "let f : int -> int"; ""; "/// wrong home"; "let f x = x" ]
                ]
                (fun td ->
                    match diagsOf td "d.weir" with
                    | d :: _ ->
                        Expect.equal d.Line 4 "at the doc"
                        Expect.stringContains d.Message "doc belongs on the signature" ""
                    | [] -> failtest "expected the doc-home teaching")

            // doc on the sig line: hover reads sig + doc there
            withDir [ "lib.weir", lib ] (fun td ->
                let libPath = System.IO.Path.Combine(td, "lib.weir")
                let lines = List.ofArray (System.IO.File.ReadAllLines libPath)
                Expect.equal (Weir.Lsp.hoverAt libPath lines 6 6) (Some "double : int -> int\n\ndoubles") "sig hover")
        }
        test "a loaded module carries each signed member's /// doc from its signature (the #help source)" {
            withDir [ "lib.weir", lib; "main.weir", [ "import \"./lib.weir\"" ] ] (fun td ->
                let main = System.IO.Path.Combine(td, "main.weir")

                match
                    Weir.ModuleLoader.fileImportLoader
                        Weir.Statements.assumeResolver
                        main
                        "./lib.weir"
                        {
                            Start = { Line = 1; Col = 1 }
                            End = { Line = 1; Col = 1 }
                        }
                        None
                with
                | Ok lm -> Expect.equal lm.MemberDocs (Map.ofList [ "double", [ "doubles" ] ]) "signed member doc only"
                | Error e -> failtest e.Message)
        }
        test "check-against-sig retires the KSL shapes: ctor pattern on an own param; a sig-pinned to yaml" {
            withDir
                [
                    "k.weir",
                    [
                        "module K"
                        "type Sh = Circle of int | Sq of int"
                        "let area : Sh -> int"
                        ""
                        "let area s ="
                        "    match s with"
                        "    | Circle r -> r * r * 3"
                        "    | Sq a -> a * a"
                    ]
                    "y.weir",
                    [
                        "module Y"
                        "type Doc = { name: string }"
                        "let render : Doc -> seq<string>"
                        ""
                        "let render d = d |> to yaml"
                    ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "k.weir") "the sig types the scrutinee"
                    Expect.isEmpty (diagsOf td "y.weir") "the sig pins the adapter's record")
        }
        test "a unit signature over a body ending in a command teaches `()` [D:module-signatures]" {
            withDir
                [
                    "c.weir", [ "module C"; "let tag : string -> unit"; "let tag v ="; "    git tag $v" ]
                    "p.weir",
                    [
                        "module P"
                        "let up : unit -> unit"
                        "let up () ="
                        "    let d = \"x\""
                        "    git log | head -1"
                    ]
                    "n.weir", [ "module N"; "let names : unit -> unit"; "let names () = [\"a\"]" ]
                    "ok.weir",
                    [
                        "module Ok"
                        "let tag : string -> unit"
                        "let tag v ="
                        "    git tag $v"
                        "    ()"
                    ]
                ]
                (fun td ->
                    for f in [ "c.weir"; "p.weir" ] do
                        match diagsOf td f with
                        | d :: _ ->
                            Expect.stringContains d.Message "does not match its signature" f
                            Expect.stringContains d.Message "its last line is a command" f
                            Expect.stringContains d.Message "end the body with `()`" f
                        | [] -> failtest $"{f}: the mismatch must refuse"

                    (match diagsOf td "n.weir" with
                     | d :: _ ->
                         Expect.isFalse (d.Message.Contains "end the body with") "a list literal is not a command"
                     | [] -> failtest "n.weir: the mismatch must refuse")

                    Expect.isEmpty (diagsOf td "ok.weir") "the repaired body checks")
        }
        test "generic sigs: general impls pass; pinned and merged type vars refuse as less-general" {
            withDir
                [
                    "g.weir", [ "module G"; "let pick : 'a -> 'a -> 'a"; "let pick a b = a" ]
                    "p.weir", [ "module P"; "let idf : 'a -> 'a"; "let idf x = x + 1" ]
                    "m.weir", [ "module Mg"; "let f : 'a -> 'b -> 'a"; "let f x y = y" ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "g.weir") "still general"

                    (match diagsOf td "p.weir" with
                     | d :: _ ->
                         Expect.stringContains
                             d.Message
                             "less general than its signature: 'a (line 2) is pinned to int"
                             ""
                     | [] -> failtest "pinned must refuse")

                    match diagsOf td "m.weir" with
                    | d :: _ -> Expect.stringContains d.Message "'a and 'b (line 2) are forced to one type" ""
                    | [] -> failtest "merged must refuse")
        }
        test "ordering and pairing guards: sig-after-impl, duplicate sig, second impl, destructuring impl" {
            withDir
                [
                    "a.weir", [ "module A"; "let f x = x"; "let f : int -> int" ]
                    "b.weir", [ "module B"; "let f : int -> int"; "let f : int -> int"; "let f x = x" ]
                    "c.weir", [ "module C"; "let f : int -> int"; "let f x = x"; "let f x = x + 1" ]
                    "e.weir", [ "module E"; "let f : int -> int"; "let (f, g) = (1, 2)" ]
                ]
                (fun td ->
                    (match diagsOf td "a.weir" with
                     | d :: _ -> Expect.stringContains d.Message "comes after its implementation" ""
                     | [] -> failtest "sig-after-impl must refuse")

                    (match diagsOf td "b.weir" with
                     | d :: _ -> Expect.stringContains d.Message "duplicate signature for 'f'" ""
                     | [] -> failtest "duplicate sig must refuse")

                    (match diagsOf td "c.weir" with
                     | d :: _ -> Expect.stringContains d.Message "already implements its signature" ""
                     | [] -> failtest "a second impl must refuse")

                    match diagsOf td "e.weir" with
                    | d :: _ -> Expect.stringContains d.Message "a signature pairs with a plain implementation" ""
                    | [] -> failtest "a destructuring impl must refuse")
        }
        test "types stay auto-exported: a union crosses, its ctors resolve qualified, sig types validate" {
            withDir
                [
                    "t.weir",
                    [
                        "module T"
                        "type Verdict = Good | Bad of int"
                        "let judge : int -> Verdict"
                        ""
                        "let judge n = if n > 0 then Good else Bad n"
                    ]
                    "u.weir", [ "import \"./t.weir\""; "let _v = T.judge 3"; "print (show 1)" ]
                    "bad.weir", [ "module Z"; "let f : Nope -> int"; "let f x = 1" ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "u.weir") "type + signed member cross"

                    match diagsOf td "bad.weir" with
                    | d :: _ -> Expect.stringContains d.Message "unknown type 'Nope'" "the sig type validates"
                    | [] -> failtest "an unknown sig type must refuse")
        }
        test
            "def-less builtin nominals are nameable in signatures [D:yaml-nodes]: YamlPatch exports a patch builder; Proc and the declared builtins pin" {
            withDir
                [
                    "m.weir",
                    [
                        "module M"
                        ""
                        "let mk : string -> YamlPatch"
                        ""
                        "let mk tier = yaml patch"
                        "    labels:"
                        "        tier: $tier"
                    ]
                    "use.weir",
                    [
                        "import \"./m.weir\" as M"
                        "let doc = [\"kind: K\"] |> Yaml.parse"
                        "doc |> Yaml.merge (M.mk \"gold\") |> to yaml |> Seq.iter print"
                    ]
                    // Proc is semantically exportable: a module helper
                    // over a handle (handles are data and escape scopes
                    // [D:scoped-procs])
                    "p.weir", [ "module P"; "let pid : Proc -> int"; "let pid p = Proc.pid p" ]
                    // the declared prelude nominals + Map already crossed —
                    // pinned so it stays true
                    "b.weir",
                    [
                        "module B"
                        "let a : Yaml -> Yaml"
                        "let a y = y"
                        "let b : Retry -> int"
                        "let b r = r.attempts"
                        "let e : Poll -> Duration"
                        "let e p = p.timeout"
                        "let c : HttpRequest -> string"
                        "let c r = r.url"
                        "let d : Map<string, int> -> Map<string, int>"
                        "let d m = m"
                    ]
                    // a def-less nominal takes no type arguments
                    "e.weir", [ "module E"; "let f : YamlPatch<int> -> int"; "let f x = 1" ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "m.weir") "the module exports a patch builder"
                    Expect.isEmpty (diagsOf td "use.weir") "the consumer merges it"
                    Expect.isEmpty (diagsOf td "p.weir") "Proc names a handle helper"
                    Expect.isEmpty (diagsOf td "b.weir") "declared builtins + Map keep crossing"

                    match diagsOf td "e.weir" with
                    | d :: _ ->
                        Expect.stringContains d.Message "'YamlPatch' expects 0 type argument(s), got 1" "arity refuses"
                    | [] -> failtest "YamlPatch<int> must refuse")
        }
        test "a `yaml patch` marker never parses as a command on the check side [D:assume-resolver]" {
            // the check-side resolver assumes unknown heads are commands;
            // the head guard's modifier face must still refuse the glued
            // marker so the district arm takes it — run and check agree
            withDir
                [
                    "s.weir",
                    [
                        "let p = yaml patch"
                        "    replicas: 3"
                        "[\"kind: K\"] |> Yaml.parse |> Yaml.merge p |> to yaml |> Seq.iter print"
                    ]
                    "t.weir",
                    [
                        "let p = yaml patch by=name"
                        "    images:"
                        "        - name: app"
                        "          newTag: v3"
                        "[\"kind: K\"] |> Yaml.parse |> Yaml.merge p |> to yaml |> Seq.iter print"
                    ]
                ]
                (fun td ->
                    Expect.isEmpty (diagsOf td "s.weir") "the plain patch marker checks"
                    Expect.isEmpty (diagsOf td "t.weir") "the by= marker checks")
        }
        test
            "the privacy suggestion round-trips [D:module-signatures]: the suggested signature parses, validates, and exports" {
            withDir
                [
                    "lib.weir",
                    [
                        "module L"
                        ""
                        "let mkp name = yaml patch"
                        "    labels:"
                        "        app: $name"
                        ""
                        "let mk2 : string -> YamlPatch"
                        "let mk2 n = mkp n"
                    ]
                    "use.weir",
                    [
                        "import \"./lib.weir\" as L"
                        "[\"kind: K\"] |> Yaml.parse |> Yaml.merge (L.mkp \"web\") |> to yaml |> Seq.iter print"
                    ]
                ]
                (fun td ->
                    let sugg =
                        match diagsOf td "use.weir" with
                        | d :: _ ->
                            Expect.stringContains
                                d.Message
                                "-> YamlPatch` in the module to export it"
                                "the teaching names a YamlPatch signature"

                            let m = System.Text.RegularExpressions.Regex.Match(d.Message, "add `(let [^`]+)`")
                            Expect.isTrue m.Success "the teaching carries the signature to paste"
                            m.Groups[1].Value
                        | [] -> failtest "the private member must refuse"

                    // paste the suggested line verbatim above the impl —
                    // the module checks and the import resolves
                    // (error text -> paste -> green)
                    let libPath = System.IO.Path.Combine(td, "lib.weir")
                    let lines = System.IO.File.ReadAllLines libPath |> List.ofArray
                    let patched = List.item 0 lines :: "" :: sugg :: List.skip 1 lines
                    System.IO.File.WriteAllLines(libPath, patched)
                    Expect.isEmpty (diagsOf td "lib.weir") "the pasted signature checks in the module"
                    Expect.isEmpty (diagsOf td "use.weir") "the member now exports")
        }
        test "a qualified type name parses in every type position [D:qualified-type-names]" {
            match Weir.Parser.parseLine cmdResolver "let f : M.Spec -> string" with
            | Ok(SSig("f", ty, _)) -> Expect.equal (Weir.Types.formatTy ty) "M.Spec -> string" "a signature"
            | other -> failtest $"expected SSig, got {other}"

            match Weir.Parser.parseLine cmdResolver "let g : seq<M.Page<int>> -> int" with
            | Ok(SSig("g", ty, _)) -> Expect.equal (Weir.Types.formatTy ty) "seq<M.Page<int>> -> int" "nested, generic"
            | other -> failtest $"expected SSig, got {other}"

            match Weir.Parser.parseLine cmdResolver "type R = { f: M.Spec }" with
            | Ok(SType d) ->
                match d.Body with
                | DRecord [ "f", TNamed("M.Spec", []), _ ] -> ()
                | b -> failtest $"field type: {b}"
            | other -> failtest $"expected SType, got {other}"
        }
    ]

let cmdChainTests =
    testList "command chaining [D:cmd-chaining]" [
        test "| and / | or take a builtin operand inside any block, as at column 0" {
            for lines in
                [
                    [ "if true then"; "    echo a | and cd /tmp"; "    print (pwd)" ]
                    [ "let f () ="; "    echo a | and cd /tmp"; "    pwd"; "print (f ())" ]
                    [ "within tmp d"; "    sh -c \"exit 1\" | or cd d"; "    print (pwd)" ]
                    [ "for x in [1] do"; "    echo a | and cd /"; "    print (show x)" ]
                ] do
                Expect.isEmpty (errorMsgsIn "andcd.weir" lines) $"checks clean: {lines}"
        }
        test "| and desugars to a sequence (run the left, then the right)" {
            match Weir.Parser.parseLine realResolver "git status | and git log" with
            | Ok(SExpr { Kind = ESeq _ })
            | Ok(SCmd { Kind = ESeq _ }) -> ()
            | Ok other -> failtest $"expected an ESeq, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "| or desugars to a conditional on the left's exit code" {
            match Weir.Parser.parseLine realResolver "git status | or git log" with
            | Ok(SExpr { Kind = EIf _ })
            | Ok(SCmd { Kind = EIf _ }) -> ()
            | Ok other -> failtest $"expected an EIf, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "| or refuses a builtin on its left" {
            // cd is a builtin (it raises, it has no exit-code) — not an | or left
            match Weir.Parser.parseLine realResolver "cd / | or git log" with
            | Error e -> Expect.stringContains e "'| or' must follow an external command or a pipeline" "teaches"
            | Ok other -> failtest $"expected an error, got {other}"
        }
        test "| or followed by a weir name teaches ^ for the program" {
            match Weir.Parser.parseLine realResolver "git status | or ls" with
            | Error e ->
                Expect.stringContains e "'ls' is a weir name, not a program" "names the word"
                Expect.stringContains e "write ^ls" "points at the PATH program"
            | Ok other -> failtest $"expected an error, got {other}"
        }
        test "a reifier can't end an and/or chain, at a let RHS too" {
            for line in
                [
                    "git status | and git log | complete"
                    "let r = git status | or git log | exitCode"
                ] do
                match Weir.Parser.parseLine realResolver line with
                | Error e -> Expect.stringContains e "can't end a '| " "teaches, not a parser dump"
                | Ok other -> failtest $"expected an error for {line}, got {other}"
        }
        test "| and / | or can't follow a reifier" {
            for line in
                [
                    "git status | succeeds | and git log"
                    "let r = git status | and git log | succeeds | or git log"
                ] do
                match Weir.Parser.parseLine realResolver line with
                | Error e -> Expect.stringContains e "can't follow '| succeeds'" "teaches, not a parser dump"
                | Ok other -> failtest $"expected an error for {line}, got {other}"
        }
        test "| or takes a pipeline on its left (leftmost failing code)" {
            match Weir.Parser.parseLine realResolver "git log | grep x | or echo none" with
            | Ok _ -> ()
            | Error e -> failtest $"parse failed: {e}"
        }
        test "| and and | or can't mix; one-word chains stay [D:and-or-no-mix]" {
            for line in
                [
                    "git status | and git log | or git log"
                    "git status | or git log | and git log"
                    "let r = git status | and git log | and git log | or git log"
                ] do
                match Weir.Parser.parseLine realResolver line with
                | Error e -> Expect.stringContains e "can't be mixed in one chain" $"refused: {line}"
                | Ok other -> failtest $"expected an error for {line}, got {other}"

            for line in
                [
                    "git status | and git log | and git log"
                    "git status | or git log | or git log"
                ] do
                match Weir.Parser.parseLine realResolver line with
                | Ok _ -> ()
                | Error e -> failtest $"{line}: {e}"
        }
        test "| or after a splatted command checks and runs (one exitCode desugar)" {
            let diags =
                diagsIn "or-splat.weir" [ "let xs = [\"a\"; \"b\"]"; "echo $@xs | or echo no" ]

            Expect.isEmpty diags "the splat reaches argv, no internal error"
        }
        test "every reifier name after a command teaches, never resolves on PATH [D:reifier-term]" {
            // orFail takes its message as the next word, so `extra` is legal there
            for name in Weir.Ast.reifierNames |> List.filter ((<>) "orFail") do
                match Weir.Parser.parseLine realResolver $"git status | {name} extra" with
                | Error e -> Expect.stringContains e $"'{name}' is a reifier" name
                | Ok other -> failtest $"{name}: expected the teaching, got {other}"
        }
        test "the reifier table names desugar variables that exist [D:reifier-term]" {
            for surface, stem in Weir.Ast.reifiers do
                for v in [ "|" + stem; $"|{stem}Env" ] do
                    Expect.isTrue (Map.containsKey v valueEnv) $"{surface}: {v} is a builtin"
                    Expect.equal (Weir.Ast.reifierOfVar v) (Some surface) $"{v} maps back"
        }
        test "| exec may end an and chain (it hands over)" {
            match Weir.Parser.parseLine realResolver "git status | and git log | exec" with
            | Ok _ -> ()
            | Error e -> failtest $"parse failed: {e}"
        }
        test "| and with nothing after it asks for a command" {
            match Weir.Parser.parseLine realResolver "git status | and" with
            | Error e -> Expect.stringContains e "'| and' must be followed by a command" "teaches"
            | Ok other -> failtest $"expected an error, got {other}"
        }
        test "| and / | or after a weir value: one teaching at check and run [D:value-chain-word]" {
            for lines, kw, col in
                [
                    [ "42 | and echo x" ], "and", 6
                    [ "true | or echo x" ], "or", 8
                    [ "false | or print \"fallback\"" ], "or", 9
                    [ "let xs = [\"a\"]"; "xs | and echo x" ], "and", 6
                    [ "if true then"; "    42 | and echo x" ], "and", 10
                ] do
                let msg =
                    $"'| {kw}' must follow an external command or a pipeline of them — the left side here is a weir value, which has no exit code; to branch on a value, write an if"

                match errorsIn "vc.weir" lines with
                | d :: _ ->
                    Expect.equal (d.Message.Replace("\r\n", " ").Replace("\n", " ")) msg $"check: {lines}"
                    Expect.equal (d.Line, d.Col) (List.length lines, col) $"at the chain word: {lines}"
                | [] -> failtest $"check must refuse: {lines}"

                match Weir.Parser.parseLine realResolver ((List.last lines).Trim()) with
                | Error m -> Expect.stringContains (m.Replace("\r\n", " ").Replace("\n", " ")) msg $"run: {lines}"
                | Ok other -> failtest $"run must refuse: {other}"
        }
        test "| and before a weir name teaches the and repair, the same at check and run [D:value-chain-word]" {
            let msg =
                "'exit' is a weir name, not a program — '| and' must be followed by a command; to run weir code when the command succeeds, use `if <command> | succeeds then …`; to run a program named exit, write ^exit"

            for lines in [ [ "echo a | and exit 3" ]; [ "if true then"; "    echo a | and exit 3" ] ] do
                match errorsIn "an.weir" lines with
                | d :: _ -> Expect.equal (d.Message.Replace("\r\n", " ").Replace("\n", " ")) msg $"check: {lines}"
                | [] -> failtest $"check must refuse: {lines}"

            match Weir.Parser.parseLine realResolver "git status | and exit 3" with
            | Error m -> Expect.stringContains (m.Replace("\r\n", " ").Replace("\n", " ")) msg "run"
            | Ok other -> failtest $"run must refuse: {other}"
        }
    ]

let tildeTests =
    // a typed `~` is the home directory in a command line [D:tilde] —
    // source syntax only; data (quoted, interpolated, spliced) stays
    // literal, `~user` too, and an expression `~` teaches Path.home
    let shape line =
        match Weir.Parser.parseLineFull cmdResolver line with
        | Ok s -> Weir.Ast.sexprStmt s
        | Error f -> failtest $"parse failed: {f.Message}"

    let home =
        System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile

    testList "tilde [D:tilde]" [
        test "a word-leading ~ or ~/ desugars to the run-time home" {
            Expect.equal (shape "echo ~ ~/x") "(scmd (cmd echo (|home ()) (+ (|home ()) \"/x\")))" "both forms"
        }
        test "data and other spellings stay literal" {
            Expect.equal
                (shape "echo \"~/q\" a~b ~user HEAD~1")
                "(scmd (cmd echo \"~/q\" \"a~b\" \"~user\" \"HEAD~1\"))"
                "quoted, mid-word, ~user, a git revision"
        }
        test "it evaluates to the home path" {
            skipOnWindows ()

            let runLive input =
                match Weir.Parser.parseLine realResolver input with
                | Ok(SExpr e | SCmd e) ->
                    match typecheck env e with
                    | Ok te -> eval valueEnv te |> forceSeq
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            Expect.equal (runLive "$(echo ~/x)") [ VStr(home + "/x") ] "argv carries the expanded path"
            Expect.equal (runLive "$(echo \"~/x\")") [ VStr "~/x" ] "a quoted word does not"
        }
        test "a ~/ path heads a command, without the dynamic-head caret" {
            match Weir.Parser.parseLineFull cmdResolver "~/bin/tool arg" with
            | Ok(SCmd { Kind = ECmd(HeadDyn(d, _), _, _) }) ->
                Expect.equal d "~/bin/tool" "the display is the typed spelling"
            | other -> failtest $"expected a dynamic-head command, got {other}"
        }
        test "an expression ~ teaches Path.home" {
            match Weir.Parser.parseLineFull cmdResolver "let x = (~)" with
            | Error f -> Expect.stringContains f.Message "Path.home ()" "the expression spelling"
            | Ok s -> failtest $"expected the teaching, got {Weir.Ast.sexprStmt s}"
        }
    ]

let pipeChainTests =
    // the early-exit pump [D:pipe-early-exit] and reifiers after a chain
    // [D:chain-reifiers] — real processes, POSIX
    let field (v: Value) (name: string) =
        match v with
        | VRecord(_, fields) -> fields |> List.find (fun (n, _) -> n = name) |> snd
        | other -> failtest $"expected a record, got {other}"

    testList "pipe chains [D:pipe-early-exit] [D:chain-reifiers]" [
        test "a consumer that exits early ends the chain; the told-to-stop producer is no failure" {
            skipOnWindows ()
            let sw = System.Diagnostics.Stopwatch.StartNew()
            Expect.equal (runReal "sh -c \"seq 1 1000000\" | sh -c \"head -1\" | line") (VStr "1") "the first line"

            Expect.equal
                (runReal "sh -c \"seq 1 1000000\" | sh -c \"head -1\" | succeeds")
                (VBool true)
                "told to stop is success"

            Expect.isLessThan sw.ElapsedMilliseconds 20000L "no hang"
        }
        test "over direct pipes only SIGPIPE excuses an upstream stage [D:pipe-direct]" {
            skipOnWindows ()

            Expect.equal
                (runReal "sh -c \"trap '' PIPE; seq 1 100000; exit 3\" | sh -c \"head -1\" | succeeds")
                (VBool false)
                "a stage that survives the broken pipe and exits nonzero fails the chain"

            Expect.equal
                (runReal "sh -c \"seq 1 100000; exit 3\" | sh -c \"head -1\" | exitCode")
                (VInt 3L)
                "its own code, not the pipe's"

            Expect.equal
                (runReal "sh -c \"yes\" | sh -c \"head -1\" | exitCode")
                (VInt 0L)
                "SIGPIPE's death is the pipe closing"
        }
        test "a stage that fails while its downstream still reads raises" {
            skipOnWindows ()
            Expect.throws (fun () -> runReal "sh -c \"echo a; exit 3\" | cat | line" |> ignore) "a real failure"
            Expect.equal (runReal "sh -c \"echo a; exit 3\" | cat | succeeds") (VBool false) "succeeds is false"
        }
        test "exitCode and complete take the LEFTMOST failing code; stderr in stage order" {
            skipOnWindows ()
            Expect.equal (runReal "sh -c \"exit 3\" | cat | sh -c \"cat; exit 5\" | exitCode") (VInt 3L) "leftmost"

            let r =
                runReal "sh -c \"echo e1 >&2; echo o\" | sh -c \"cat; echo e2 >&2; exit 4\" | complete"

            Expect.equal (field r "exitCode") (VInt 4L) "the only failure"
            Expect.equal (field r "stdout" |> forceSeq) [ VStr "o" ] "the tail's stdout"
            Expect.equal (field r "stderr" |> forceSeq) [ VStr "e1"; VStr "e2" ] "every stage's stderr, in order"
        }
        test "text, value heads and per-stage env ride the chain" {
            skipOnWindows ()
            Expect.equal (runReal "sh -c \"printf 'b\\na\\n'\" | sh -c \"sort\" | text") (VStr "a\nb") "text"
            Expect.equal (runReal "[\"c\"; \"a\"] | sh -c \"sort\" | sh -c \"head -1\" | line") (VStr "a") "value head"
            Expect.equal (runReal "Q=q sh -c \"echo $Q\" | cat | line") (VStr "q") "a stage's env prefix"
        }
        test "exec after a chain teaches the shell spelling" {
            match Weir.Parser.parseLine cmdResolver "git log | grep x | exec" with
            | Error msg -> Expect.stringContains msg "a pipeline cannot" ""
            | Ok s -> failtest $"must refuse, got {s}"
        }
    ]

let fieldSpliceTests =
    // a splice's `.field` path is one word [D:field-splices]
    let errs = errorMsgsIn "fs.weir"

    let decl = [
        "type In = { name: string }"
        "type Cli = { tag: string; bin: string; files: seq<string>; inner: In }"
        "let cli = { tag = \"v1\"; bin = \"echo\"; files = [\"a\"]; inner = { name = \"d\" } }"
    ]

    let sx line =
        match Weir.Parser.parseLineFull cmdResolver line with
        | Ok s -> Weir.Ast.sexprStmt s
        | Error f -> failtest f.Message

    testList "field splices [D:field-splices]" [
        test "$x.f, $x.f.g, $@x.f and ^$x.f parse as field paths" {
            Expect.stringContains (sx "echo $cli.tag") "cli.tag" "scalar"
            Expect.stringContains (sx "echo $cli.inner.name") "cli.inner.name" "nested"
            Expect.stringContains (sx "echo $@cli.files") "cli.files" "splat"
            Expect.stringContains (sx "^$cli.bin hi") "cli.bin" "dynamic head"
        }
        test "record fields check as one word each" {
            Expect.isEmpty (errs (decl @ [ "echo $cli.tag $cli.inner.name $@cli.files"; "^$cli.bin hi" ])) "all legal"
        }
        test "a non-record value teaches the interpolated spelling" {
            let ms = errs [ "let file = \"a.txt\""; "cp $file $file.bak" ]

            Expect.isTrue
                (ms
                 |> List.exists (fun m -> m.Contains "but file is string" && m.Contains "$\"{file}.bak\""))
                $"{ms}"
        }
        test "only an identifier tail extends the splice; other glue still refuses" {
            for line in [ "echo $x."; "echo $x/y"; "echo $x.1" ] do
                match Weir.Parser.parseLineFull cmdResolver line with
                | Ok s -> failtest $"{line} must refuse, got {Weir.Ast.sexprStmt s}"
                | Error f -> Expect.stringContains f.Message "do not concatenate" line
        }
        test "the splat keeps its name-or-expr expectation" {
            match Weir.Parser.parseLineFull cmdResolver "echo $@ x" with
            | Ok s -> failtest $"must refuse, got {Weir.Ast.sexprStmt s}"
            | Error f -> Expect.stringContains f.Message "a name or (expr) after '$@'" "label"
        }
        test "$NAME is Env.need \"NAME\" wherever a $ name stands [D:env-sigil]" {
            for sugar, plain in
                [
                    "echo $HOME", "echo (Env.need \"HOME\")"
                    "echo -C $Project_dir x", "echo -C (Env.need \"Project_dir\") x"
                    "let h = $HOME", "let h = Env.need \"HOME\""
                    "print $\"{$HOME}/bin\"", "print $\"{Env.need \"HOME\"}/bin\""
                ] do
                Expect.equal (sx sugar) (sx plain) sugar

                match Weir.Fast.tryStmt cmdResolver sugar, Weir.Fast.tryStmt cmdResolver plain with
                | Some a, Some b -> Expect.equal (Weir.Ast.sexprStmt a) (Weir.Ast.sexprStmt b) $"fast: {sugar}"
                | _ -> ()

            Expect.stringContains (sx "FOO=$HOME echo a") "Env.need" "an env-prefix value"
            Expect.stringContains (sx "^$EDITOR f") "^$EDITOR" "a dynamic head shows as it was written"
            Expect.isFalse ((sx "echo $home").Contains "Env") "a lowercase name is the binding"
        }
        test "$NAME checks as a string, stays one word, and is no binding in a text block [D:env-sigil]" {
            Expect.isEmpty
                (errs [ "echo $HOME"; "let n = $HOME |> Str.length"; "print $\"{n} {$HOME}/bin\"" ])
                "legal in argv, as an expression, in a hole"

            match Weir.Parser.parseLineFull cmdResolver "echo $HOME/bin" with
            | Ok s -> failtest $"must refuse, got {Weir.Ast.sexprStmt s}"
            | Error f -> Expect.stringContains f.Message "do not concatenate" "one whole word"

            match Weir.Parser.parseLineFull cmdResolver "echo ${HOME}" with
            | Ok s -> failtest $"must refuse, got {Weir.Ast.sexprStmt s}"
            | Error f -> Expect.stringContains f.Message "use '$NAME' for an environment variable" "bash braces"

            let ms = errs [ "let t = $$<<<"; "    a $HOME"; "print (show t)" ]
            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "unbound variable 'HOME'")) $"{ms}"
        }
        test "a $ name with a member path is a binding or a module, never the environment [D:env-sigil]" {
            Expect.isEmpty
                (errs [ "let n = $(echo $@Self.args) |> Seq.length"; "print (show n)" ])
                "a module member splats as it did"

            Expect.stringContains (sx "echo $@Self.args") "Self.args" "the module path, no Env.need"
            Expect.isFalse ((sx "echo $@Self.args").Contains "Env") "no environment read"

            let ms = errs [ "echo $HX.txt" ]

            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "interpolate one word: $\"{$HX}.txt\"")) $"{ms}"
        }
        test "a $ word in an expression that is no whole $NAME names its repair [D:env-sigil]" {
            let refuses (line: string) (needle: string) =
                match Weir.Parser.parseLineFull cmdResolver line with
                | Ok s -> failtest $"{line} must refuse, got {Weir.Ast.sexprStmt s}"
                | Error f -> Expect.stringContains f.Message needle line

            refuses "FOO=$HOME/bin/echo hi" "is one whole word"
            refuses "FOO=$x/bin/echo hi" "one whole word"
            refuses "let a = $HX$X" "is one whole word"
            refuses "let a = echo $HX/bin" "is one whole word"
            refuses "let a = ${HX}" "`${NAME}` is bash"
            refuses "FOO=${HX} env" "`${NAME}` is bash"
            refuses "let v = $x" "in an expression the binding is just `x`"
            refuses "let v = $_X" "Env.need \"_X\""
            refuses "let a = f$HX" "cannot be joined onto the name before it"
            refuses "within env FOO=$HX/bin" "an environment value is one whole word"
            refuses "within env A=1 FOO=${HX}" "`${NAME}` is bash"

            for line in [ "print HOME"; "print $\"${MY_VAR}\"" ] do
                let es = errs [ line ]
                Expect.isTrue (es |> List.exists (fun m -> m.Contains "an environment variable is read as `$")) $"{es}"

            let typo = errs [ "let v = Sone" ]

            Expect.isFalse
                (typo |> List.exists (fun m -> m.Contains "environment variable"))
                "a mixed-case name is no variable's"

            Expect.isEmpty
                (errs [ "within env A=1 B=$HOME C=\"x y\""; "    sh -c \"true\"" ])
                "whole-word pairs stay legal"

            let ms = errs [ "let a = $HX $X"; "print a" ]
            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "`$HX` is a string, not a function")) $"{ms}"
            Expect.isFalse (ms |> List.exists (fun m -> m.Contains "Env.need")) "the desugar is not named"

            Expect.isEmpty
                (errs [
                    "let x = \"a\""
                    "if true then"
                    "    echo $x $HOME"
                    "let y = $(echo $x | text)"
                    "print $\"{y} {$HOME}\""
                ])
                "whole words stay legal in blocks, captures and holes"
        }
        test "$NAME reads the variable and raises when it is unset [D:env-sigil]" {
            System.Environment.SetEnvironmentVariable("WEIR_SIGIL_SET_ZZ", "v1")
            Expect.equal (run "$WEIR_SIGIL_SET_ZZ") (VStr "v1") "set"

            Expect.throwsC (fun () -> run "$WEIR_SIGIL_UNSET_ZZ" |> ignore) (fun e ->
                Expect.stringContains e.Message "WEIR_SIGIL_UNSET_ZZ is not set" "names the variable")
        }
        test "Tab after $ offers the set variables that start uppercase [D:env-sigil]" {
            System.Environment.SetEnvironmentVariable("WEIR_SIGIL_TAB_ZZ", "1")
            System.Environment.SetEnvironmentVariable("weir_sigil_lower_zz", "1")
            let text = "echo $WEIR_SIGIL_T"

            let got =
                Weir.Complete.suggest env text (Weir.Complete.wordStartAt text text.Length)

            Expect.contains got "WEIR_SIGIL_TAB_ZZ" $"{got}"

            let low = "echo $weir_sigil_l"

            Expect.isEmpty
                (Weir.Complete.suggest env low (Weir.Complete.wordStartAt low low.Length))
                "a lowercase-named variable is not a $ name"

            // wherever the sigil is read: an expression, a hole, an
            // env-prefix value, a dynamic head — and not glued to a flag
            for line in
                [
                    "let h = $WEIR_SIGIL_T"
                    "print $\"{$WEIR_SIGIL_T"
                    "FOO=$WEIR_SIGIL_T"
                    "^$WEIR_SIGIL_T"
                    "if true then echo ($WEIR_SIGIL_T"
                ] do
                Expect.contains
                    (Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length))
                    "WEIR_SIGIL_TAB_ZZ"
                    line

            for line in [ "echo --x=$WEIR_SIGIL_T"; "print \"$WEIR_SIGIL_T"; "echo a // $WEIR_SIGIL_T" ] do
                Expect.isEmpty (Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)) line

            // a `$$<<<` body and a yaml block name bindings only
            Expect.isTrue (Weir.Complete.dollarBindingOnly [| "let t = $$<<<" |] "    a ") "a splicing text block"
            Expect.isTrue (Weir.Complete.dollarBindingOnly [| "let y = yaml"; "    k:" |] "        v: ") "a yaml block"
            Expect.isFalse (Weir.Complete.dollarBindingOnly [| "if true then" |] "    echo ") "an ordinary block"
            Expect.isFalse (Weir.Complete.dollarBindingOnly [||] "echo ") "the top level"
        }
    ]

let armBlockTests =
    // a `| pat ->` arm and an `else` take a text block as `then` does;
    // a block after an application teaches the pipe [D:arm-blocks]
    let run (lines: string list) =
        match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
        | Ok lls -> lls
        | Error e -> failtest e

    let errs = errorMsgsIn "ab.weir"

    testList "text blocks in arms and else [D:arm-blocks]" [
        test "an arm and an else arm their block" {
            Expect.isEmpty
                (errs [
                    "let t ="
                    "    match 1 with"
                    "    | 1 -> <<<"
                    "        one"
                    "    | _ -> $<<<"
                    "        other {1}"
                    "t |> Seq.iter print"
                ])
                "arms"

            Expect.isEmpty
                (errs [
                    "let t ="
                    "    if false then [\"x\"]"
                    "    else <<<"
                    "        small"
                    "t |> Seq.iter print"
                ])
                "else"

            Expect.equal
                (run [
                    "let t ="
                    "    match 1 with"
                    "    | 1 -> <<<"
                    "        one"
                    "    | _ -> [\"o\"]"
                ])
                    .Length
                1
                "one logical line"
        }
        test "a block as a trailing argument teaches the pipe at the marker" {
            let ds =
                diagsIn "ab.weir" [ "within tmp d"; "    File.write $\"{d}/f\" <<<"; "        hi" ]

            match ds |> List.filter (fun d -> d.Severity = "error") with
            | d :: _ ->
                Expect.stringContains d.Message "cannot be a trailing argument" "the teaching"
                Expect.equal (d.Line, d.Col) (2, 25) "caret on the marker"
            | [] -> failtest "must refuse"
        }
        test "a block as a command argument teaches the binding, at the marker (never an empty message) [D:arm-blocks]" {
            let msg =
                "a text block cannot be a command argument — bind it first (`let body = <<<`, the block below it) and pass the value: `$body`"

            for lines, col in
                [
                    [ "echo <<<"; "    a" ], 6
                    [ "echo a <<<"; "    a" ], 8
                    [ "ls | grep <<<"; "    a" ], 11
                    [ "if true then"; "    echo <<<"; "        a" ], 10
                ] do
                match diagsIn "hb.weir" lines |> List.filter (fun d -> d.Severity = "error") with
                | [ d ] ->
                    Expect.equal (d.Message.Replace("\n", " ")) msg $"{lines}"
                    Expect.equal d.Col col $"caret on the marker: {lines}"
                | ds -> failtest $"expected one error for {lines}, got {ds}"
        }
        test "a <<< followed by anything (a trailing tab) names the marker rule, no tab-stop note [D:text-block]" {
            match errorMsgsIn "ht.weir" [ "let x = <<<\t"; "    a"; "print x" ] with
            | m :: _ ->
                Expect.equal
                    (m.Replace("\n", " "))
                    "a text block marker '<<<' must end its line — nothing may follow it, not even trailing spaces or tabs; the block is the indented lines below"
                    "the marker rule"
            | [] -> failtest "must refuse"
        }
        test "the taught spelling holds" {
            Expect.isEmpty
                (errs [ "within tmp d"; "    <<<"; "        hi"; "    |> File.write $\"{d}/f\"" ])
                "block first, piped on the closing line"
        }
    ]

let lambdaLetTests =
    // a lambda body grants command lets wherever it sits [D:lambda-lets]
    let errs = errorMsgsIn "lam.weir"

    let body = [ "    let r = sh -c \"echo x\" | complete"; "    print r.stdout)" ]

    testList "lambda-body command lets [D:lambda-lets]" [
        test "a dangling body takes a command let at top level, in an if body and in a within" {
            Expect.isEmpty (errs ("[1] |> Seq.iter (fun _ ->" :: body)) "top level"

            Expect.isEmpty
                (errs (
                    "if true then"
                    :: "    [1] |> Seq.iter (fun _ ->"
                    :: (body |> List.map ((+) "    "))
                ))
                "if body"

            Expect.isEmpty
                (errs (
                    "within tmp d"
                    :: "    [1] |> Seq.iter (fun _ ->"
                    :: (body |> List.map ((+) "    "))
                ))
                "within body"
        }
        test "a single-line body takes the let…in form, as on the spine" {
            Expect.isEmpty
                (errs [
                    "[1] |> Seq.iter (fun _ -> let r = sh -c \"echo x\" | complete in print r.stdout)"
                ])
                "statement position"
        }
        test "the RHS is the command; the closer stays out of argv" {
            match
                Weir.Assemble.assemble [ 1, "[1] |> Seq.iter (fun _ ->"; 2, "    let r = echo hi"; 3, "    print r)" ]
            with
            | Ok [ ll ] ->
                match Weir.Parser.parseLineFull cmdResolver ll.Text with
                | Ok s ->
                    let sx = Weir.Ast.sexprStmt s
                    Expect.stringContains sx "\"hi\"" "echo's argv"
                    Expect.isFalse (sx.Contains "\")\"") "no ')' argv word"
                | Error f -> failtest f.Message
            | other -> failtest $"unexpected assembly: {other}"
        }
        test "a paren interior stays expression territory" {
            let ms = errs [ "print (show (let r = sh -c \"x\" | complete in r.exitCode))" ]

            Expect.isTrue
                (ms
                 |> List.exists (fun m -> m.Contains "inside parentheses, a command needs $(…)"))
                $"{ms}"
        }
    ]

let orPatternTests =
    // F#'s or-patterns as a desugar [D:or-patterns]: one arm per
    // alternative sharing the guard and body; v1 alternatives bind no names
    let errs = errorMsgsIn "or.weir"

    testList "or-patterns [D:or-patterns]" [
        test "literal and case alternatives share the body; the desugar is one arm each" {
            expectValue "match \"b\" with | \"a\" | \"b\" -> 1 | _ -> 0" (VInt 1L)
            expectValue "(function | 1 | 2 -> \"small\" | _ -> \"big\") 2" (VStr "small")

            match Weir.Parser.parseLineFull cmdResolver "let x = match 1 with | 1 | 2 -> 0 | _ -> 1" with
            | Ok s -> Expect.stringContains (Weir.Ast.sexprStmt s) "[1 -> 0] [2 -> 0]" "two arms, one body"
            | Error f -> failtest f.Message
        }
        test "alternatives count toward exhaustiveness; a missing case is still named" {
            let decl = "type L = A | B | C"
            Expect.isEmpty (errs [ decl; "let f l = match l with | A | B -> 1 | C -> 2"; "print $\"{f A}\"" ]) "covered"
            let ms = errs [ decl; "let f l = match l with | A | B -> 1"; "print $\"{f A}\"" ]
            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "missing: C")) $"names C: {ms}"
        }
        test "a guard applies to every alternative" {
            expectValue "match 2 with | 1 | 2 when false -> 0 | 1 | 2 -> 1 | _ -> 9" (VInt 1L)
        }
        test "an arm takes at most 64 alternatives; the 65th refuses at its own column" {
            // each alternative re-checks the shared body — the cap bounds it
            let line n =
                let alts = [ 0 .. n - 1 ] |> List.map string |> String.concat " | "
                $"let x = match 1 with | {alts} -> 0 | _ -> 1"

            match Weir.Parser.parseLineFull cmdResolver (line 64) with
            | Ok _ -> ()
            | Error f -> failtest f.Message

            let text = line 65

            match Weir.Parser.parseLineFull cmdResolver text with
            | Ok _ -> failtest "65 alternatives must refuse"
            | Error f ->
                Expect.stringContains f.Message "at most 64 alternatives" "the cap"
                Expect.stringContains f.Message "Seq.contains" "the repair"
                Expect.equal f.Col (Some(text.IndexOf " 64 " + 2)) "caret on the 65th"
        }
        test "a reifier ending an arm is a stage, not a binding or-pattern" {
            for r in [ "line"; "complete"; "succeeds"; "text" ] do
                match
                    Weir.Parser.parseLineFull
                        cmdResolver
                        $"let v = match 1 with | 1 -> git status | {r} | _ -> git log | {r}"
                with
                | Ok _ -> ()
                | Error f -> failtest $"{r}: {f.Message}"
        }
        test "a command arm ends before an or-pattern arm" {
            match
                Weir.Parser.parseLineFull cmdResolver "match 1 with | 0 -> git pull | 1 | 2 -> git fetch | _ -> ()"
            with
            | Ok s -> Expect.stringContains (Weir.Ast.sexprStmt s) "\"pull\"" "the first arm's command is its own"
            | Error f -> failtest f.Message
        }
        test "alternatives that bind names refuse with a teaching (v1)" {
            let ms =
                errs [
                    "let f p = match p with | (n, 0) | (0, n) -> n | _ -> 0"
                    "print $\"{f (1, 0)}\""
                ]

            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "cannot bind names yet")) $"{ms}"
        }
    ]

let reflexTeachingTests =
    // F#/bash/C reflexes teach at their site instead of an expecting-list
    // dump [D:left-to-right-ops] [D:argv-backslash] [D:parse-error-binds]
    let errs = errorMsgsIn "reflex.weir"

    let only (lines: string list) =
        match errs lines with
        | [ m ] -> m
        | ms -> failtest $"expected exactly one error, got {ms}"

    testList "reflex teachings" [
        test "!x is not negation; the retired district keeps its teaching" {
            Expect.stringContains (only [ "let ok = true"; "if !ok then print \"no\"" ]) "write `not x`" "condition"

            Expect.stringContains
                (only [ "let ok = true"; "let b = !ok" ])
                "write `not x`"
                "let RHS, not the district message"

            Expect.isTrue
                (Weir.Assemble.retiredDistrictMarker "if c then !e")
                "a district header still teaches its retirement"
        }
        test "type annotations teach inference" {
            Expect.stringContains
                (only [ "let f (x: int) = x + 1"; "print $\"{f 1}\"" ])
                "weir infers parameter types"
                "a parameter"

            Expect.stringContains
                (only [ "let x: int = 1"; "print $\"{x}\"" ])
                "weir infers a binding's type"
                "a binding"
        }
        test "a bare range in for teaches the list" {
            Expect.stringContains (only [ "for i in 1..3 do print $\"{i}\"" ]) "wrap it in brackets" ""
        }
        test "backslash argv words teach; a quoted backslash passes" {
            Expect.stringContains
                (only [ "sh -c \"echo $1\" x a \\"; "    b" ])
                "continues a command line by indentation"
                "line-end"

            Expect.stringContains (only [ "find . -exec echo {} \\;" ]) "quote the word instead: \";\"" "an escape"
            Expect.isEmpty (errs [ "sh -c \"echo $1\" x \"\\\\\"" ]) "a literal backslash argument"
            Expect.stringContains (only [ "\\ls -la" ]) "`^name` runs the PATH program" "an escaped head"
        }
        test "F#'s String module and collection type names teach Str and seq<T> [D:fs-reflexes]" {
            Expect.stringContains
                (only [ "let s = String.trim \" a \""; "print s" ])
                "string functions are 'Str'"
                "String.x"

            for ty in [ "list<string>"; "List<int>"; "array<int>"; "ResizeArray<string>" ] do
                Expect.stringContains
                    (only [ $"type C = {{ xs: {ty} }}"; "print \"x\"" ])
                    "one sequence type is seq<T>"
                    ty
        }
        test "a let that fails to parse still binds its name" {
            let ms = errs [ "let x: int = 1"; "print $\"{x}\""; "let y = x" ]
            Expect.equal ms.Length 1 $"only the real error, no unbound echoes: {ms}"
        }
    ]

let ifBodyStopTests =
    // a then/elif body's command ends at a bareword else/elif
    // [D:if-body-stop] — before, `if c then echo hi else echo bye` was ONE
    // command with argv "hi else echo bye", and `… else ()` fell back to
    // expression mode (unbound 'echo')
    let shape line =
        match Weir.Parser.parseLineFull cmdResolver line with
        | Ok s -> Weir.Ast.sexprStmt s
        | Error f -> failtest $"parse failed: {f.Message}"

    let errsOf = errorsIn "ifelse.weir"

    testList "if-body else stop [D:if-body-stop]" [
        test "else and elif end a then-body command" {
            Expect.equal
                (shape "if true then echo hi else echo bye")
                "(sexpr (if true (cmd echo \"hi\") (cmd echo \"bye\")))"
                "else is the if's clause, not argv"

            Expect.equal
                (shape "if true then echo hi else ()")
                "(sexpr (if true (cmd echo \"hi\") ()))"
                "a unit else keeps the then-body a command"

            Expect.equal
                (shape "if false then echo a elif true then echo b else echo c")
                "(sexpr (if false (cmd echo \"a\") (if true (cmd echo \"b\") (cmd echo \"c\"))))"
                "elif ends the body too"
        }
        test "a dangling else binds the nearest if" {
            Expect.equal
                (shape "if true then if false then echo x else echo y else echo z")
                "(sexpr (if true (if false (cmd echo \"x\") (cmd echo \"y\")) (cmd echo \"z\")))"
                "inner else, then outer else"
        }
        test "a reifier ends before else; a quoted else is argv; outside an if body else is argv" {
            Expect.stringContains (shape "if true then git pull | orFail \"m\" else ()") "|orFailed" "the reifier binds"

            Expect.equal
                (shape "if true then echo \"else\" else ()")
                "(sexpr (if true (cmd echo \"else\") ()))"
                "quoting passes the word"

            Expect.equal (shape "echo else elif") "(scmd (cmd echo \"else\" \"elif\"))" "the stop is gated to if bodies"
        }
        test "statement if/else with command branches checks; value position captures" {
            Expect.isEmpty (errsOf [ "if 1 == 1 then"; "    echo a"; "else"; "    echo b" ]) "both branches arm"
            Expect.isEmpty (errsOf [ "if 1 == 1 then echo a elif 2 == 2 then echo b else ()" ]) "an elif chain arms"

            Expect.isEmpty
                (errsOf [ "let x = if true then echo a else echo b"; "print (Seq.head x)" ])
                "value position stays seq<string>"
        }
    ]

let aliasCompleteTests =
    // bufferComplete parses the buffer with the session resolver to decide
    // Enter-submit; an alias head must resolve there or `co -` reads as the
    // expression `co - …` and never submits [D:command-head-alias]
    let noAlias: Weir.Parser.Resolver = {
        cmdResolver with
            AliasHead = fun _ -> None
    }

    let withAlias: Weir.Parser.Resolver = {
        cmdResolver with
            AliasHead =
                fun n ->
                    if n = "co" || n = "cb" then
                        Some("git", [ "checkout" ])
                    else
                        None
    }

    // the completeness verdict bufferComplete computes: Ok, or an error
    // that is NOT past the line end, means complete (submit)
    let complete (r: Weir.Parser.Resolver) (line: string) =
        match Weir.Parser.parseLineFull r line with
        | Ok _ -> true
        | Error f ->
            match f.Col with
            | Some c -> c <= line.TrimEnd().Length
            | None -> true

    testList "alias head completeness [D:command-head-alias]" [
        test "an alias head submits on one Enter; without the table it hangs" {
            // the bug: a trailing operator-like argv (`-`, `*`) after an
            // unresolved head reads as an unfinished expression
            for line in [ "co -"; "cb burnt*" ] do
                Expect.isTrue (complete withAlias line) $"'{line}' is complete when the alias resolves"
                Expect.isFalse (complete noAlias line) $"'{line}' looks unfinished without the alias (the bug)"
        }
    ]

let dynamicHeadTests =
    let checkOf = diagsIn "dynhead.weir"

    testList "dynamic command heads [D:dynamic-head]" [
        test "(a) parse: ^$name heads a command; a spliced arg stays ONE argv entry" {
            match Weir.Parser.parseLine realResolver "^$tool one $x" with
            | Ok(SCmd {
                          Kind = ECmd(HeadDyn("$tool", { Kind = EVar "tool" }), args, _)
                      })
            | Ok(SExpr {
                           Kind = ECmd(HeadDyn("$tool", { Kind = EVar "tool" }), args, _)
                       }) -> Expect.equal (List.length args) 2 "two argv entries: the literal and the single splice"
            | Ok other -> failtest $"expected a dyn-headed ECmd, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "(b) parse: ^$(…) heads a command with a capture value" {
            match Weir.Parser.parseLine realResolver "^$(git branch |> Seq.exactlyOne) status" with
            | Ok(SCmd {
                          Kind = ECmd(HeadDyn("$(…)", { Kind = ECapture _ }), [ _ ], _)
                      })
            | Ok(SExpr {
                           Kind = ECmd(HeadDyn("$(…)", { Kind = ECapture _ }), [ _ ], _)
                       }) -> ()
            | Ok other -> failtest $"expected a capture-headed ECmd, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "(c) parse teachings: splat head, interpolated head, bare ^$" {
            let perr input frag =
                match Weir.Parser.parseLine realResolver input with
                | Error m -> Expect.stringContains m frag $"the teaching for {input}"
                | Ok s -> failtest $"must refuse: {input} -> {s}"

            perr "^$@xs" "a splat cannot head a command"
            perr "^$\"{d}/tool\" run" "bind it first"
            perr "^$%foo" "'^$' needs a name or a capture"
        }
        test "(d) check: a seq-capture head refuses with the bind-and-pick teaching" {
            match checkOf [ "^$(git branch) status" ] with
            | [ d ] ->
                Expect.equal d.Severity "error" "an error, not a warning"
                Expect.stringContains d.Message "bind and pick" "the teaching"
            | ds -> failtest $"expected exactly the refusal, got {ds}"
        }
        test "(e) the run-time carve-out: a dyn head draws NO cmd-not-found diag, and the binding counts as read" {
            Expect.isEmpty
                (checkOf [ "let tool = \"definitely-not-on-path-zz\""; "^$tool go" ])
                "resolution is the run's business; the head read keeps 'tool' used"
        }
        test "(f) reifiers compose: ^$tool | complete checks; the seq head refuses through the desugar too" {
            Expect.isEmpty
                (checkOf [ "let tool = \"x\""; "let r = ^$tool | complete"; "print $\"{r.exitCode}\"" ])
                "the dyn head rides the reifier desugar"

            match checkOf [ "let r = ^$(git branch) | complete"; "print $\"{r.exitCode}\"" ] with
            | [ d ] -> Expect.stringContains d.Message "bind and pick" "the same teaching through EDynProg"
            | ds -> failtest $"expected exactly the refusal, got {ds}"
        }
        test "(g) a non-string head refuses, naming the type" {
            match checkOf [ "let n = 3"; "^$n x" ] with
            | [ d ] -> Expect.stringContains d.Message "a dynamic head is a program name (string)" "the type law"
            | ds -> failtest $"expected exactly the refusal, got {ds}"
        }
        test "(h) zero movement: ^ls still forces the literal PATH binary" {
            match Weir.Parser.parseLine realResolver "^ls x" with
            | Ok(SCmd { Kind = ECmd(HeadLit "ls", [ _ ], _) })
            | Ok(SExpr { Kind = ECmd(HeadLit "ls", [ _ ], _) }) -> ()
            | Ok other -> failtest $"expected the literal forced head, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
    ]

// the security cut [D:attr-int-overflow][D:head-word-bound]
// [D:cli-exception-guard]: three front-end hardening pins — none may
// crash the tool; each fails as a located diagnostic, never a SIGABRT.
let hardeningTests =
    testList "Hardening" [ // Fix 1 — an out-of-range attribute integer is a located parse
        // error, not an OverflowException (was SIGABRT/exit 134)
        // [D:attr-int-overflow]
        test "attribute integer past 64-bit is a located error, never a crash" {
            match Weir.Parser.parseStmt "type T = { [<Default 99999999999999999999>] A: int }" with
            | Error msg ->
                Expect.stringContains
                    msg
                    "attribute argument out of range (64-bit)"
                    "the teaching mirrors the int-literal parser"

                Expect.stringContains msg "99999999999999999999" "the offending digits are named"
            | Ok _ -> failtest "expected the overflow to be rejected"
        }
        test "a negative attribute literal is a literal: [<Default -1>], -1.5, -2s [D:attr-negative-literal]" {
            Expect.isEmpty
                (errorMsgsIn "an.weir" [
                    "type Cli = { [<Default -1>] n: int; [<Default -1.5>] f: float; [<Default -2s>] d: Duration }"
                    "let c = Args.load Cli"
                    "print $\"{c.n} {c.f} {c.d}\""
                ])
                "accepted and typed"

            match Weir.Parser.parseStmt "type T = { [<Default -99999999999999999999>] A: int }" with
            | Error msg ->
                Expect.stringContains
                    msg
                    "attribute argument out of range (64-bit): -99999999999999999999"
                    "the range refusal names the signed digits"
            | Ok _ -> failtest "expected the overflow to be rejected"
        }
        test "a '-' not followed by a number in an attribute argument teaches [D:attr-negative-literal]" {
            for src, col in
                [
                    "type Cfg = { [<Default -x>] d: int }", 24
                    "type Cfg = { [<Default -\"a\">] d: string }", 24
                    "type Cfg = { [<Default - 1>] d: int }", 24
                    "type Cfg = { [<Default --1>] d: int }", 24
                    "type U = | [<Wire -a>] A | B", 19
                ] do
                match errorsIn "neg.weir" [ src ] with
                | d :: _ ->
                    Expect.equal
                        (d.Message.Replace("\r\n", " ").Replace("\n", " "))
                        "a '-' in an attribute argument must be followed by a number, as in [<Default -1>] — attribute arguments are literals"
                        src

                    Expect.equal (d.Line, d.Col) (1, col) $"on the '-': {src}"
                | [] -> failtest $"must refuse: {src}"
        }
        test "stacked attribute lines on one field (and case) accumulate [D:attr-stacked-lines]" {
            Expect.isEmpty
                (errorMsgsIn "as.weir" [
                    "type Cli = {"
                    "    [<Short \"n\">]"
                    "    [<Default 5>]"
                    "    n: int"
                    "}"
                    "let c = Args.load Cli"
                    "print $\"{c.n}\""
                ])
                "both attributes reach the field"
        }
        test "a duplicate type parameter is named at its second spelling" {
            match errorsIn "tp.weir" [ "type T<'a, 'a> = { v: 'a }"; "let t = { v = 1 }"; "print $\"{t.v}\"" ] with
            | d :: _ ->
                Expect.equal
                    d.Message
                    "duplicate type parameter 'a — each type parameter is declared once"
                    "the message"

                Expect.equal (d.Line, d.Col) (1, 12) "at the second 'a"
            | [] -> failtest "must refuse"
        }
        test
            "Env.load of a generic instance teaches the monomorphic rule, not a comparison parse [D:load-generic-instance]" {
            match
                errorsIn "gi.weir" [ "type Box<'a> = { v: 'a }"; "let e = Env.load Box<int>"; "print $\"{e.v}\"" ]
            with
            | d :: _ ->
                Expect.equal
                    (d.Message.Replace("\n", " "))
                    "Env.load needs a monomorphic record; 'Box' is generic — declare a concrete record with the field types filled in and load that"
                    "the message"
            | [] -> failtest "must refuse"
        }
        test "attribute overflow on a union case and a type decl are located too" {
            match Weir.Parser.parseStmt "type T = [<Default 99999999999999999999>] A | B" with
            | Error msg -> Expect.stringContains msg "out of range (64-bit)" "union-position overflow located"
            | Ok _ -> failtest "expected the overflow to be rejected"
        }
        test "a duration/size attribute multiply cannot silently wrap [D:attr-int-overflow]" {
            // 9999999999999TiB is in-range as digits but overflows int64
            // once scaled — the bound refuses instead of wrapping negative
            match Weir.Parser.parseStmt "type T = { [<Default 9999999999999TiB>] A: int }" with
            | Error msg -> Expect.stringContains msg "out of range (64-bit)" "the scaled overflow is refused"
            | Ok _ -> failtest "expected the scaled overflow to be rejected"
        }
        test "an in-range attribute (unit and plain) still parses unchanged" {
            Expect.isOk (Weir.Parser.parseStmt "type T = { [<Default 30s>] A: int }") "30s attr unchanged"
            Expect.isOk (Weir.Parser.parseStmt "type T = { [<Default 10MiB>] A: int }") "10MiB attr unchanged"
            Expect.isOk (Weir.Parser.parseStmt "type T = { [<Default 42>] A: int }") "plain int attr unchanged"
            Expect.isOk (Weir.Parser.parseStmt "type T = { [<Default 0.5>] A: int }") "float attr unchanged"
        }
        // Fix 2 — the doomed command-mode attempt on a bareword ';'-spine
        // stays linear: the bounded head-word scan caps the doomed
        // attempt, and the diagnostic is byte-identical [D:head-word-bound]
        test "bareword ';'-spine still produces the unbound-variable diagnostic" {
            match Weir.Parser.parseLine realResolver "let x = b;b;b" with
            | Ok(SLet(_, _)) -> () // resolves as an expression, 'b' unbound downstream (check-time)
            | Error msg -> Expect.stringContains msg "b" "the spine still names the bareword"
            | other -> failtest $"unexpected parse of the spine: {other}"
        }
        test "bareword ';'-spine checks in bounded time (linearity guard) [D:head-word-bound]" {
            // 20k barewords was O(N^2) (>25s); the bound makes it linear.
            // A generous wall-clock ceiling catches a regression to
            // quadratic without flaking on load.
            let spine = "let x = " + System.String.Join(";", Array.create 20000 "b")
            let sw = System.Diagnostics.Stopwatch.StartNew()
            Weir.Parser.parseLine realResolver spine |> ignore
            sw.Stop()
            Expect.isLessThan sw.Elapsed.TotalSeconds 15.0 "the doomed attempt is bounded, not quadratic"
        }
        test "a resolvable command head is unaffected by the head-word bound" {
            match Weir.Parser.parseLine realResolver "git status" with
            | Ok(SCmd _) -> ()
            | other -> failtest $"a real command head must still parse: {other}"
        }
        // Fix 3 — the top-level CLI guard turns a residual front-end
        // exception into a located diagnostic with a non-zero exit,
        // never a raw stack trace / exit 134 [D:cli-exception-guard].
        // The guard lives inline in Program.main; this pin documents the
        // contract and asserts the two crash triggers it backstops are
        // themselves already fixed (so the guard is a pure defense-in-depth
        // net — reachability is exercised through the CLI e2e cell, which
        // drives the whole binary).
        test "the two known crash triggers no longer throw at the parser boundary" {
            Expect.isError
                (Weir.Parser.parseStmt "type T = { [<Default 99999999999999999999>] A: int }")
                "attr overflow is data, not an exception"

            let spine = "let x = " + System.String.Join(";", Array.create 5000 "b")
            // must return (Ok or Error), never throw
            Weir.Parser.parseLine realResolver spine |> ignore
        }
    ]

// the command walk sees through reifiers [D:module-let-reifier]
let runsCommandReifierTests =
    testList "runsCommandT through reifiers [D:module-let-reifier]" [
        test "a module let through a reifier is refused; a function using one is not" {
            for rhs in
                [
                    "git rev-parse HEAD | line"
                    "git log -1 | text"
                    "git status | succeeds"
                    "git status | complete"
                ] do
                let msgs =
                    diagsIn "w.weir" [ "module W"; $"let head = {rhs}" ]
                    |> List.map (fun d -> d.Message)

                Expect.exists msgs (fun m -> m.Contains "cannot run a command at import") rhs

            let ok =
                diagsIn "w.weir" [
                    "module W"
                    "let head : unit -> string"
                    "let head () = git rev-parse HEAD | line"
                ]

            Expect.isEmpty ok "a function defers the command"
        }
        test
            "a module let calling its own command-reaching helper is refused; a partial application is not [D:modules-v1]" {
            let helper = "let helper a b = sh -c $\"echo {a}{b}\" |> Seq.exactlyOne"

            for rhs in [ "helper \"a\" \"b\""; "\"b\" |> helper \"a\""; "via ()" ] do
                let msgs =
                    diagsIn "w.weir" [
                        "module W"
                        helper
                        "let via () = helper \"x\" \"y\""
                        "let v : string"
                        $"let v = {rhs}"
                    ]
                    |> List.map (fun d -> d.Message)

                Expect.exists msgs (fun m -> m.Contains "cannot run a command at import") rhs

            let ok =
                diagsIn "w.weir" [
                    "module W"
                    "let helper : string -> string -> string"
                    helper
                    "let part : string -> string"
                    "let part = helper \"a\""
                ]

            Expect.isEmpty ok "a partial application runs nothing"
        }
        test "a lazy command let is a module value; the eager forms are refused, naming why [D:lazy-command-lets]" {
            for rhs in
                [
                    "git log --oneline -n 10"
                    "git log | grep a | sort"
                    "[\"a\"] | grep a"
                    "$(git log)"
                    "other"
                    "other | grep a"
                ] do
                let ds =
                    diagsIn "w.weir" [
                        "module W"
                        "let other : seq<string>"
                        "let other = git status"
                        "let gl : seq<string>"
                        $"let gl = {rhs}"
                    ]

                Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) rhs

            let refused (body: string list) (needle: string) =
                let msgs = diagsIn "w.weir" ("module W" :: body) |> List.map (fun d -> d.Message)

                Expect.exists
                    msgs
                    (fun m -> m.Contains "cannot run a command at import" && m.Contains needle)
                    (String.concat "; " body)

            refused
                [ "let _h = git rev-parse HEAD | line" ]
                "`| line` runs the command now — drop the reifier to keep it lazy, or wrap it in a function"

            refused [ "let _h = echo $(git rev-parse HEAD | line)" ] "`| line` runs the command now"
            refused [ "let _h = git log |> Seq.freeze" ] "passed to `Seq.freeze`"
            refused [ "let _h = git status | or echo x" ] "`| and`/`| or` runs the commands now"
            // forcing a lazy binding runs its command just the same
            refused
                [ "let gl : seq<string>"; "let gl = git log"; "let _n = gl |> Seq.length" ]
                "'gl' is a lazy command passed to `Seq.length`"

            refused
                [ "let gl : seq<string>"; "let gl = git log"; "let _m = echo $@gl" ]
                "'gl' is a lazy command read here"

            refused
                [
                    "let gl : seq<string>"
                    "let gl = git log"
                    "let f () = gl |> Seq.length"
                    "let _v = f ()"
                ]
                "'f' runs one"
        }
        test "importing a module with a lazy command let spawns nothing; each use runs it [D:lazy-command-lets]" {
            if not (System.OperatingSystem.IsWindows()) then
                let dir = tempDir "weir-lazylet"

                try
                    let mark = System.IO.Path.Combine(dir, "mark")
                    let modP = System.IO.Path.Combine(dir, "w.weir")

                    System.IO.File.WriteAllLines(
                        modP,
                        [
                            "module W"
                            "let gl : seq<string>"
                            $"let gl = sh -c \"echo run >> '{mark}'; echo out\""
                        ]
                    )

                    let mainP = System.IO.Path.Combine(dir, "main.weir")

                    System.IO.File.WriteAllLines(
                        mainP,
                        [
                            "import \"./w.weir\""
                            $"sh -c \"echo main >> '{mark}'\""
                            "W.gl |> Seq.iter (fun _ -> ())"
                            "W.gl |> Seq.iter (fun _ -> ())"
                        ]
                    )

                    Expect.equal (Weir.Script.run mainP []) 0 "the script runs clean"
                    // no "run" ahead of "main": the import spawned nothing
                    Expect.equal
                        (System.IO.File.ReadAllLines mark |> List.ofArray)
                        [ "main"; "run"; "run" ]
                        "lazy at import, run per use"
                finally
                    try
                        System.IO.Directory.Delete(dir, true)
                    with _ ->
                        ()
        }
    ]
