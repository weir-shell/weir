module BuiltinsTests

// The builtins suites. A suite sits in the file of the area it mostly
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

let polymorphismTests =
    testList "Pipe-directed instantiation" [
        test "where instantiates from the piped seq" {
            Expect.equal (checkOk "ls |> where (fun f -> f.hidden)").Ty Weir.Builtins.seqFileRow ""
        }
        test "map changes the element type" { Expect.equal (checkOk "ls |> map (fun f -> f.bytes)").Ty (TSeq TSize) "" }
        test "map over ints still works" {
            Expect.equal
                (run "(Seq.initInfinite id) |> map (fun x -> x * x) |> take 3" |> forceSeq)
                [ VInt 0; VInt 1; VInt 4 ]
                ""
        }
        test "map with an inferable function argument works standalone" {
            Expect.equal (run "(Seq.initInfinite id) |> map double |> take 3" |> forceSeq) [ VInt 0; VInt 2; VInt 4 ] ""
        }
        test "full application instantiates from the trailing data argument" {
            expectValue "where (fun f -> f.hidden) ls |> take 1" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
        }
        test "instantiation mismatch is reported" {
            Expect.stringContains
                (checkErr "(Seq.initInfinite id) |> where (fun f -> f.readOnly)").Message
                "only records have fields"
                ""
        }
    ]

let pathParamCompletionTests =
    // path-parameter positions [D:path-param-completion]: the registry
    // is builtinDocs' named params (path/src/dst; base within Path) —
    // paths and string bindings offer, keywords and bare members do
    // not. Sequenced: the fixture moves the ambient session cwd
    testSequenced
    <| testList "path-parameter completion [D:path-param-completion]" [
        test "a bare stem inside an open quote completes paths, and names no binding" {
            let d = tempPath "weir-qp"
            System.IO.Directory.CreateDirectory d |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "signup1020.jsonl"), "")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "signup1030.jsonl"), "")

            try
                let envS = {
                    Weir.Builtins.typeEnvStrict with
                        Values = Map.add "signupDir" (mono TStr) Weir.Builtins.typeEnvStrict.Values
                }

                let ask (line: string) =
                    Weir.Complete.suggest envS line (Weir.Complete.wordStartAt line line.Length)

                inCwd d (fun () ->
                    let both = [ "signup1020.jsonl"; "signup1030.jsonl" ]
                    Expect.equal (ask "File.read \"") both "an empty word lists the directory"
                    Expect.equal (ask "File.read \"sig") both "a stem completes, unquoted: the quote is open"

                    Expect.equal
                        (ask "let signup1030 = File.read \"signup10")
                        both
                        "after a let, past the common prefix"

                    Expect.equal (ask "File.read \"signup102") [ "signup1020.jsonl" ] "one match"
                    Expect.contains (ask "File.read sig") "signupDir" "outside a quote the string binding still offers"

                    Expect.isFalse
                        (ask "print \"sig" |> List.exists (fun c -> c.EndsWith ".jsonl"))
                        "a string that is no path parameter offers no files")
            finally
                System.IO.Directory.Delete(d, true)
        }
        test "a literal offers no names, except an environment variable's to Env.get [D:env-name-complete]" {
            System.Environment.SetEnvironmentVariable("WEIR_ENVC_ALPHA_ZZ", "1")
            System.Environment.SetEnvironmentVariable("weir_envc_lower_zz", "1")

            let envS = {
                Weir.Builtins.typeEnvStrict with
                    Values = Map.add "weirBinding" (mono TStr) Weir.Builtins.typeEnvStrict.Values
            }

            let ask (line: string) =
                Weir.Complete.suggest envS line (Weir.Complete.wordStartAt line line.Length)

            for head in [ "Env.get"; "Env.need"; "Env.getOr"; "let v = Env.need"; "print (Env.getOr" ] do
                Expect.contains (ask (head + " \"WEIR_ENVC_AL")) "WEIR_ENVC_ALPHA_ZZ" head

            Expect.contains (ask "Env.get \"weir_envc_l") "weir_envc_lower_zz" "a lowercase-named variable too"
            Expect.isEmpty (ask "Env.getOr \"X\" \"WEIR_ENVC_AL") "getOr's second literal is the default"
            Expect.isEmpty (ask "MyEnv.get \"WEIR_ENVC_AL") "another module's get is no slot"

            for line in
                [
                    "print \"weirB"
                    "let s = \"weirB"
                    "print \"pri"
                    "print \"Str.tr"
                    "print \"a weirB"
                ] do
                Expect.isEmpty (ask line) $"a literal names nothing: {line}"

            Expect.contains (ask "print weirB") "weirBinding" "outside a literal the binding offers"
            Expect.contains (ask "print $\"{weirB") "weirBinding" "and inside a hole"
        }
        test "the registry positions offer paths, bindings, and nothing else" {
            let d = tempPath "weir-ppc"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "weir-sub"))
            |> ignore

            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "tool.txt"), "x")

            try
                let sug env (line: string) =
                    Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)

                inCwd d (fun () ->
                    // the repro: cd offers the one real candidate, and
                    // none of `when where windowed with within`
                    let got = sug Weir.Builtins.typeEnvStrict "cd w"
                    Expect.equal got [ "weir-sub/" ] "the path, alone"

                    // the positive twin hard-filtering would break:
                    // `cd target` applies the binding, so a string
                    // binding offers beside the matching path
                    let envT = {
                        Weir.Builtins.typeEnvStrict with
                            Values = Map.add "target" (mono TStr) Weir.Builtins.typeEnvStrict.Values
                    }

                    let withBinding = sug envT "cd t"
                    Expect.contains withBinding "target" "the binding offers"
                    Expect.contains withBinding "tool.txt" "the path offers"

                    for bad in [ "when"; "where"; "windowed"; "with"; "within" ] do
                        Expect.isFalse (List.contains bad withBinding) $"'{bad}' is not a path argument"

                    // a second-argument position: File.copy's dst — an
                    // expression, so the path is a literal [D:expr-paths-quoted]
                    Expect.equal
                        (sug Weir.Builtins.typeEnvStrict "File.copy tool.txt w")
                        [ "\"weir-sub/\"" ]
                        "dst is a path"

                    // the position is param-keyed, not member-keyed:
                    // File.write's second param is lines, so the
                    // general pool (keywords included) returns
                    Expect.contains
                        (sug Weir.Builtins.typeEnvStrict "File.write tool.txt wh")
                        "when"
                        "lines is not a path"

                    // within cd — the one path-typed within kind
                    Expect.equal
                        (sug Weir.Builtins.typeEnvStrict "within cd w")
                        [ "\"weir-sub/\"" ]
                        "within cd is a path position"

                    // keywords still complete where they should — the
                    // standing rule: 'keywords removed' must not be
                    // satisfiable by removing them everywhere
                    Expect.contains (sug Weir.Builtins.typeEnvStrict "wh") "when" "statement start keeps keywords"

                    // "when", not "where": bare module members are
                    // partitioned out of the strict pool [D:bare-partition]
                    Expect.contains
                        (sug Weir.Builtins.typeEnvStrict "xs |> wh")
                        "when"
                        "a pipeline elem keeps the pool"

                    // the quoted context keeps working — the regression
                    // guard (string-literal path completion predates this)
                    Expect.contains
                        (sug Weir.Builtins.typeEnvStrict "File.read \"./")
                        "./tool.txt"
                        "quoted paths unchanged")
            finally
                System.IO.Directory.Delete(d, true)
        }
    ]

let moduleTests =
    testList "Builtin modules" [
        test "qualified members work and freshen per use" {
            expectValue "ls |> Seq.map _.name |> Seq.head" (VStr "a.txt")

            expectValue "([1] |> Seq.map double |> Seq.head) + (Str.length (Seq.head (ls |> Seq.map _.name)))" (VInt 7)
        }
        test "bare hot-path aliases still bind" {
            expectValue "ls |> where _.hidden |> map _.name |> head" (VStr "b.bin")
            expectValue "split \",\" \"a,b\" |> join \";\"" (VStr "a;b")
        }
        test "the bare-alias ALLOWLIST: a new module CANNOT steal a bare alias [D:bare-allowlist]" {
            // the property, not the outcome: a hypothetical module with
            // hot-path-named members contributes nothing — safe by
            // construction, no blocklist entry to remember (the guard
            // three collisions bought: Secret.map, Http.head, Float.toInt)
            let evil = [
                "Evil",
                [
                    "map", Weir.Types.TInt, VUnit
                    "head", Weir.Types.TInt, VUnit
                    "trim", Weir.Types.TInt, VUnit
                ]
            ]

            Expect.equal (Weir.Builtins.bareAliasHomesOf evil) Map.empty "a non-allowlisted module contributes no homes"

            Expect.isEmpty (Weir.Builtins.bareEntriesOf evil) "and no entries"

            // with Seq present beside it, bare map still homes to Seq
            let both = evil @ [ "Seq", [ "map", Weir.Types.TInt, VUnit ] ]
            Expect.equal (Weir.Builtins.bareAliasHomesOf both) (Map [ "map", "Seq" ]) "Seq keeps the alias"
        }
        test "the allowlist is RULED as exactly Seq + Str — widening it is a deliberate test edit, never a side effect" {
            Expect.equal Weir.Builtins.bareAliasModules (Set [ "Seq"; "Str" ]) "the ruling"

            for KeyValue(alias, home) in Weir.Builtins.bareAliasHomes do
                Expect.isTrue (home = "Seq" || home = "Str") $"bare '{alias}' homes to {home} — outside the allowlist"
        }
        test "Option members are qualified-only" {
            expectValue "[7] |> Seq.tryHead |> Option.map double |> Option.defaultValue 0" (VInt 14)
            Expect.stringContains (checkErr "[7] |> Seq.tryHead |> defaultTo 0").Message "Option.defaultValue" ""
        }
        test "length is qualified-only in both homes" {
            expectValue "Str.length \"abc\"" (VInt 3)
            expectValue "[1; 2; 3] |> Seq.length" (VInt 3)

            Expect.stringContains (checkErr "length \"abc\"").Message "use Bytes.length or Seq.length or Str.length" ""
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
            Expect.stringContains (checkErr "Seq.mpa").Message "module Seq has no member 'mpa'. Did you mean 'map'?" ""
        }
        test "moved names hint their qualified home" {
            Expect.stringContains (checkErr "[] |> defaultTo 1").Message "use 'Option.defaultValue'" ""
            // groupBy is bare now [D:bare-partition] — the hint pin
            // moves to strict mode, where members stay qualified
            let strictEnv, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            match Weir.Check.typecheck strictEnv (parse "[true] |> groupBy (fun x -> x)") with
            | Error terr -> Expect.stringContains terr.Message "use 'Seq.groupBy'" ""
            | Ok _ -> failtest "strict mode must not resolve bare groupBy"
        }
        test "module member completion" {
            Expect.contains (suggest "Seq.tr" 0) "Seq.tryHead" ""
            Expect.contains (suggest "Str." 0) "Str.length" ""
            Expect.contains (suggest "Se" 0) "Seq" "module names complete"
        }
    ]

let scriptTests =
    testList "Script machinery" [
        test "comment stripper respects strings" {
            Expect.equal (Weir.Assemble.stripComment "1 + 1 // note") "1 + 1" ""

            Expect.equal (Weir.Assemble.stripComment "sh -c \"echo a//b\" // real") "sh -c \"echo a//b\"" ""

            Expect.equal (Weir.Assemble.stripComment "grep 'a//b' f") "grep 'a//b' f" ""
            Expect.equal (Weir.Assemble.stripComment "\"esc \\\" // still string\"") "\"esc \\\" // still string\"" ""
            Expect.equal (Weir.Assemble.stripComment "no comment") "no comment" ""
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
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            match Weir.Check.typecheck strictEnv (parse "ls |> map (fun x -> x)") with
            | Error terr ->
                Expect.stringContains terr.Message "use Option.map or Result.map or Secret.map or Seq.map" ""
            | Ok _ -> failtest "expected strict rejection"
        }
        test "a stray directive: check and run share one message, naming unknown vs misplaced [D:directive-diagnostic]" {
            Expect.equal
                (Weir.Analyze.directiveMisuses [ "#loose"; "print (show 1)" ])
                [
                    1,
                    "unknown directive: #loose — a script's only directive is #sig (the REPL init file takes #init, #session and #alias)"
                ]
                "a head-position unknown directive is unknown, not misplaced"

            Expect.equal
                (Weir.Analyze.directiveMisuses [ "print \"a\""; "#sig git"; "#bogus x" ])
                [
                    2,
                    "misplaced directive: #sig — #sig lines belong at the file head, before any code, comment or blank line"
                    3,
                    "unknown directive: #bogus — a script's only directive is #sig (the REPL init file takes #init, #session and #alias)"
                ]
                "a late #sig is misplaced"

            Expect.equal
                (Weir.Analyze.directiveMisuses [
                    "#!/usr/bin/env weir"
                    "#sig git"
                    "print 1"
                    "    # indented is a continuation"
                ])
                []
                "shebang + head #sig are the head"
        }
        test
            "every consumer of the analysis sees the directive message, never a dump (fmt, the LSP, init files) [D:directive-diagnostic]" {
            let diags, _, _, _, _ =
                Weir.Analyze.analyzeLines "d.weir" [ "print \"a\""; "#sig git"; "print \"b\"" ]

            Expect.equal
                (diags |> List.map (fun d -> d.Line, d.Code, d.Message))
                [
                    2,
                    "directive",
                    "misplaced directive: #sig — #sig lines belong at the file head, before any code, comment or blank line"
                ]
                "analyzeLines names it, and the rest checks"

            let diags, _, _, _, _ =
                Weir.Analyze.analyzeLines "init.weir" [ "#init"; "#loose"; "#sig \"abc\""; "let x = 1" ]

            Expect.equal
                (diags |> List.map (fun d -> d.Line, d.Code, d.Message))
                [
                    2, "directive", "unknown directive: #loose — an init file takes #init, #session and #alias"
                    3, "directive", "#sig belongs at a script's head — an init file takes #init, #session and #alias"
                ]
                "an init file names its own"
        }
        test "#loose is GONE: the directive is unknown, files are always strict [D:bare-partition]" {
            // no migration message — a mode that never executed gets
            // whatever the generic stray-directive path does (a parse
            // error under check, the directive error on run); the pin
            // is that it errors with no bespoke farewell
            let diags = diagsIn "pin.weir" [ "#loose"; "print (show 1)" ]

            Expect.isNonEmpty
                (diags |> List.filter (fun d -> d.Severity = "error"))
                "a #loose file must not check clean"

            Expect.isFalse
                (diags |> List.exists (fun d -> d.Message.Contains "removed"))
                "no migration message for a mode that never executed"
        }
        test "a bare module member in a strict pipe teaches the qualified spelling [D:bare-partition]" {
            let diags =
                diagsIn "pin.weir" [ "let xs = [1] |> where (fun n -> n > 0)"; "print (show (Seq.length xs))" ]

            Expect.exists
                diags
                (fun d -> d.Message.Contains "spell it 'Seq.where'" && d.Message.Contains "REPL session")
                $"the pipe path must teach, got: {diags |> List.map _.Message}"
        }
        test "a bare module member at command head teaches, not 'install the tool' [D:bare-partition]" {
            // sortBy, not where: the head teaching fires only when the
            // name is not on PATH (a real tool of that name must stay
            // runnable — resolution integrity), and Windows ships
            // where.exe, so `where` resolves there and never reaches
            // the cmd-not-found path
            let diags =
                diagsIn "pin.weir" [ "let xs = sortBy (fun n -> n) [1]"; "print (show 1)" ]

            Expect.exists
                diags
                (fun d -> d.Message.Contains "spell it 'Seq.sortBy'")
                $"the head path must teach, got: {diags |> List.map _.Message}"

            Expect.isFalse
                (diags |> List.exists (fun d -> d.Message.Contains "install"))
                "a bare member is never an installable tool"
        }
        test "the REPL keeps bare names: typeEnv carries them, strict does not [D:bare-allowlist]" {
            Expect.isTrue (Map.containsKey "where" Weir.Builtins.typeEnv.Values) "REPL env keeps bare where"
            Expect.isFalse (Map.containsKey "where" Weir.Builtins.typeEnvStrict.Values) "strict env has no bare where"
        }
    ]

let recordKeysTests =
    // builtin record keys derive from the declaration [D:record-keys]:
    // a mismatch is impossible to write and an arity slip is loud at
    // the construction site
    testList "builtin record keys [D:record-keys]" [
        test "the deliberate wrong-arity attempt fails loud, naming the record" {
            let ex =
                Expect.throwsC (fun () -> Weir.Builtins.recordOf Weir.Builtins.fileRow [ VInt 1L ] |> ignore) id

            Expect.stringContains ex.Message "FileRow" "names the record"
            Expect.stringContains ex.Message "1 values for 7 declared fields" "names the slip"
        }
        test "prelude-declared records: the hand-built defaults agree with the parsed defs" {
            // HttpRequest/Retry/Poll declare in Prelude.fs as weir
            // source — the helper cannot reach their defs, so the
            // agreement is pinned instead (HttpResponse is covered by
            // the e2e pins that access every field on a live response)
            let keysOf (v: Value) =
                match v with
                | VRecord(_, fs) -> fs |> List.map fst
                | v -> failtest $"expected a record, got {v}"

            let declared name =
                match Map.tryFind name env.Types with
                | Some(Record d) -> d.Fields |> List.map fst |> List.sort
                | _ -> failtest $"{name} must be declared"

            for path, tyName in
                [
                    "Http.defaults", "HttpRequest"
                    "Retry.defaults", "Retry"
                    "Poll.defaults", "Poll"
                ] do
                Expect.equal (keysOf (run path) |> List.sort) (declared tyName) $"{path} vs the {tyName} declaration"
        }
        test "the FileRow doc names every declared field (the one accepted copy, drift-pinned)" {
            let doc =
                match Map.tryFind "FileRow" Weir.Builtins.builtinDocs.Value with
                | Some d -> string d
                | None -> failtest "FileRow must be documented"

            for name, _ in Weir.Builtins.fileRow.Fields do
                Expect.stringContains doc name $"the doc must mention '{name}'"
        }
    ]

let bareRuleTests =
    // the bare-member rule [D:bare-rule] widened to a derivation
    // [D:bare-partition]: unambiguous means bare — the gate asserts the
    // partition matches the derivation, and pins the collision set so a
    // new collision (which demotes a bare name) is a decision
    testList "the bare-member rule [D:bare-rule] [D:bare-partition]" [
        test "THE GATE: a member is bare IFF its name is single-home among the allowlisted modules" {
            let homes name =
                [ "Seq"; "Str" ]
                |> List.filter (fun m -> Weir.Builtins.typeEnv.Modules[m] |> Map.containsKey name)
                |> List.length

            let members =
                [ "Seq"; "Str" ]
                |> List.collect (fun m -> Weir.Builtins.typeEnv.Modules[m] |> Map.keys |> List.ofSeq)
                |> Set.ofList

            let derived = members |> Set.filter (fun n -> homes n = 1)

            Expect.equal Weir.Builtins.bareAliases derived "bare-ness disagrees with the home count"
        }
        test "THE GATE: the collision set is PINNED — a new collision demotes a bare name, which is a decision" {
            // `replicate` joined 2026-09 (Str.replicate beside
            // Seq.replicate, the width-members batch): qualified-only
            // everywhere now — the derived partition's own rule.
            // `isEmpty` joined with Str.isEmpty beside Seq.isEmpty
            // [D:id-builtin]: Seq.isEmpty / Str.isEmpty, never bare
            Expect.equal
                Weir.Builtins.bareTwoHome
                (Set [ "contains"; "isEmpty"; "length"; "replicate" ])
                "the two-home scan moved: decide the new name (qualified-only), then update this pin"
        }
        test "no formerly-bare name lost its slot in the widening (the monotonicity check the plan demanded)" {
            // `first` left the set by ruling, not accident — the
            // retirement [D:first-retired] is the allowed exit this
            // pin guards against happening silently; `force` became
            // `freeze` by the same door [D:freeze-rename]
            let before =
                Set [
                    "map"
                    "where"
                    "take"
                    "head"
                    "sum"
                    "freeze"
                    "collect"
                    "startsWith"
                    "endsWith"
                    "trim"
                    "trimStart"
                    "trimEnd"
                    "toLower"
                    "toUpper"
                    "split"
                    "join"
                    "replace"
                    "toInt"
                    "tryToInt"
                ]

            Expect.isTrue (Set.isSubset before Weir.Builtins.bareAliases) "a live contains-shaped accident"
        }
        test "a promoted name carries its CONSTRAINED scheme — bare sort/sortBy are exactly their qualified selves" {
            Expect.equal
                (run "[3; 1; 2] |> sortBy (fun x -> x) |> freeze")
                (run "[3; 1; 2] |> Seq.sortBy (fun x -> x) |> Seq.freeze")
                ""

            let bare = (checkErr "[(fun x -> x)] |> sort").Message
            let qualified = (checkErr "[(fun x -> x)] |> Seq.sort").Message
            Expect.equal bare qualified "the Ord refusal is identical through the bare slot"
        }
        test "collect is bare; contains is qualified BOTH sides (the accident stays closed)" {
            Expect.equal
                (run "[\"a<b\"; \"c\"] |> collect (Str.split \"<\") |> Seq.length")
                (VInt 3L)
                "the live sitting's reach, still legal"

            let m = (checkErr "[1; 2] |> contains 1").Message
            Expect.stringContains m "module-qualified" "contains is a decision away, not a silent Str"
            Expect.stringContains m "Seq.contains" "…and both homes are named"
            Expect.stringContains m "Str.contains" ""

            Expect.equal (run "[1; 2] |> Seq.contains 1") (VBool true) "the qualified spellings work"
            Expect.equal (run "\"abc\" |> Str.contains \"b\"") (VBool true) ""
        }
        test "no message claims a member moved when it did not (strict mode, where names stay qualified)" {
            let strictEnv, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            match Weir.Check.typecheck strictEnv (parse "[1] |> rev") with
            | Error terr ->
                Expect.stringContains terr.Message "module-qualified; use 'Seq.rev'" "the truthful wording"
                Expect.isFalse (terr.Message.Contains "moved") "no false history"
            | Ok _ -> failtest "strict mode must not resolve bare rev"
        }
        test "bare `dir` teaches the listing too [D:dir-teach]" {
            // DOS muscle memory wants ls; Path.dir is the parent-of-a-
            // path function — the teach carries both readings.
            // Argument position forces pure expression mode: a bare
            // `dir` elsewhere resolves as the coreutils command on
            // Linux (the platform split that makes this teach
            // Windows/macOS-facing)
            let m = (checkErr "[1] |> Seq.map dir").Message
            Expect.stringContains m "use 'Path.dir'" "the qualified home"
            Expect.stringContains m "for a directory listing, use ls" "the muscle-memory half"

            // the suffix is dir's alone
            Expect.isFalse ((checkErr "[1] |> Seq.map rev").Message.Contains "listing") "rev keeps the plain form"
        }
    ]

let logLevelTests =
    testList "Log level parse" [
        test "WEIR_LOG casings all select the level" {
            Expect.equal (Weir.Builtins.parseLogLevel "debug") (Ok 1) "lower"
            Expect.equal (Weir.Builtins.parseLogLevel "DEBUG") (Ok 1) "upper"
            Expect.equal (Weir.Builtins.parseLogLevel "Debug") (Ok 1) "title"
            Expect.isTrue (Weir.Builtins.parseLogLevel "loud" |> Result.isError) "unknown still refuses"
        }
    ]

let docsBinaryTests =
    let stmtMsg (line: string) =
        let ll =
            match Weir.Assemble.assemble [ 1, line ] with
            | Ok [ ll ] -> ll
            | other -> failtest $"assembly: {other}"

        match
            Weir.Statements.checkStatement
                false
                None
                Weir.Statements.resolver
                Weir.ModuleLoader.scriptOnlyImport
                Weir.Builtins.typeEnvStrict
                ll
        with
        | Error d -> d.Message
        | Ok _ -> failtest $"expected {line} to fail"

    testList "docs vs binary" [
        test "a dashed missing head names the program, not its first segment [D:missing-command-diagnosis]" {
            let m = stmtMsg "no-such-cmd-zz a"
            Expect.stringContains m "unknown command 'no-such-cmd-zz'" "the program"
            Expect.isFalse (m.Contains "unbound variable") "no subtraction reading"
        }
        test "a glued arithmetic typo stays an unbound variable [D:missing-command-diagnosis]" {
            Expect.stringContains (stmtMsg "let y = lenth-1") "unbound variable" "arithmetic, not a program"
        }
        test "an exact reifier in a refused position teaches $(), never 'did you mean' itself [D:statement-lets]" {
            let m = stmtMsg "let n = echo a | complete in n.exitCode"
            Expect.stringContains m "'complete' is a reifier" "names the reifier"
            Expect.stringContains m "$(<command> | complete)" "the repair"
            Expect.isFalse (m.Contains "Did you mean") "no self-suggestion"
        }
        test "a seekable stdout keeps the default writer; a pipe arms EPIPE [D:stdout-epipe]" {
            let path = System.IO.Path.GetTempFileName()

            try
                use f = new System.IO.FileStream(path, System.IO.FileMode.Open)
                Expect.isFalse (Weir.Session.armsStdoutFor f) "a regular file shares its offset with stderr"
            finally
                System.IO.File.Delete path

            use pipe =
                new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out)

            Expect.isTrue (Weir.Session.armsStdoutFor pipe) "a pipe can EPIPE"
        }
    ]

// the starship port's confirmed findings: each repro, pinned
let portFindingsTests =
    let stmtDiag (line: string) =
        let ll =
            match Weir.Assemble.assemble [ 1, line ] with
            | Ok [ ll ] -> ll
            | other -> failtest $"assembly: {other}"

        match
            Weir.Statements.checkStatement
                false
                None
                Weir.Statements.resolver
                Weir.ModuleLoader.scriptOnlyImport
                Weir.Builtins.typeEnvStrict
                ll
        with
        | Error d -> d.Message
        | Ok _ -> failtest $"expected {line} to fail"

    testList "starship port findings" [
        test "an importer checks a module under its own (assume) resolver [D:assume-resolver]" {
            withTempDir "weir-port-imp" (fun dir ->
                File.WriteAllLines(
                    Path.Combine(dir, "modmiss.weir"),
                    [|
                        "module"
                        ""
                        "let ver : unit -> seq<string>"
                        "let ver () ="
                        "    let r = $(weirnosuchtoolzz -Vv | complete)"
                        "    r.stdout"
                    |]
                )

                File.WriteAllLines(
                    Path.Combine(dir, "modmiss2.weir"),
                    [|
                        "module"
                        ""
                        "let ver : unit -> seq<string>"
                        "let ver () = weirnosuchtoolzz -Vv"
                    |]
                )

                for m in [ "modmiss.weir"; "modmiss2.weir" ] do
                    let root = Path.Combine(dir, "root.weir")
                    let lines = [ $"import \"{m}\" as M"; "M.ver () |> Seq.iter print" ]
                    errorsCleanIn root lines $"importing {m} checks like the lone module")
        }
        test "a missing `$(cmd | complete)` head warns like a plain head [D:exit-reifiers]" {
            let ds =
                diagsIn "fnmiss.weir" [
                    "let f () ="
                    "    let r = $(weirnosuchtoolzz -Vv | complete)"
                    "    r.stdout"
                    "f () |> Seq.iter print"
                ]

            Expect.exists
                ds
                (fun d -> d.Code = "cmd-not-found" && d.Message.Contains "weirnosuchtoolzz" && d.Line = 2)
                $"the reified head warns: {ds |> List.map _.Message}"

            Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) "a warning, never an error"
        }
        test "a bare member after a command chain's `|>` gets the bare-member teaching [D:bare-partition]" {
            for line, name, home in
                [
                    "    git rev-parse --abbrev-ref origin/HEAD | line |> split \"/\" |> _[0]", "split", "Str"
                    "    git log --oneline -3 | head -2 |> map (fun l -> l)", "map", "Seq"
                    "    git log --oneline -3 | line |> where (fun l -> l <> \"\")", "where", "Seq"
                ] do
                let ds = diagsIn "sp.weir" [ "let b () ="; line; "print (show (b ()))" ]
                let want = $"'{name}' is a bare module member — spell it '{home}.{name}'"
                Expect.exists ds (fun d -> d.Message.Contains want) $"{name}: {ds |> List.map _.Message}"

                Expect.isFalse
                    (ds |> List.exists (fun d -> d.Message.Contains "feed a program"))
                    $"{name}: never the program reading"

            // a real program after `|>` keeps the glyph teaching
            mustSayIn
                "sp2.weir"
                [ "git log -1 | head -1 |> grep x" ]
                "'|>' applies functions; feed a program with '|'"
                "grep"
        }
        test "a two-home name after `|>` gets the runner's module-qualified teaching at check too [D:assume-resolver]" {
            let line = "let ok = [\"a\"] |> contains \"a\""
            let want = "'contains' is module-qualified here; use Seq.contains or Str.contains"
            let ds = diagsIn "c1.weir" [ line; "print (show ok)" ]
            Expect.exists ds (fun d -> d.Message = want) $"check: {ds |> List.map _.Message}"

            Expect.isFalse
                (ds |> List.exists (fun d -> d.Message.Contains "feed a program"))
                "never the program reading"

            Expect.equal (stmtDiag line) want "run says the same"
        }
        test "a missing head at run time gets the unknown-command teaching [D:missing-command-diagnosis]" {
            for line in
                [
                    "let r = $(weirnosuchtoolzz -Vv | complete)"
                    "let f () = weirnosuchtoolzz -Vv"
                    "let (a, b) = (1, $(weirnosuchtoolzz -V | line))"
                ] do
                let m = stmtDiag line
                Expect.stringContains m "unknown command 'weirnosuchtoolzz'" line
                Expect.stringContains m "^$tool" $"{line}: the run-time spelling"
                Expect.isFalse (m.Contains "Expecting") $"{line}: no FParsec list"
                Expect.isFalse (m.Contains "unbound variable") $"{line}: no unbound reading"
        }
    ]
