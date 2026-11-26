module EvalTests

// The eval suites. A suite sits in the file of the area it mostly
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

let evalTests =
    testList "Eval" [
        test "acceptance pipeline evaluates over records" {
            expectValue
                acceptance
                (VSeq [
                    Weir.Builtins.file "b.bin" 5242880 true
                    Weir.Builtins.file "d.iso" 3145728 false
                ])
        }
        test "where by string field" {
            expectValue "ls |> where (fun f -> f.name == \"c.log\")" (VSeq [ Weir.Builtins.file "c.log" 1048576 false ])
        }
        test "where by bool field" {
            expectValue "ls |> where (fun f -> f.hidden)" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
        }
        test "first truncates" {
            expectValue
                "ls |> take 2"
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
        test "forward composition" {
            expectValue "let inc n = n + 1 in (inc >> inc) 40" (VInt 42L)
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
        test "the reversed operators teach the forward ones [D:left-to-right-ops]" {
            let inc = "let inc n = n + 1 in "
            Expect.stringContains (checkErr (inc + "(inc << inc) 40")).Message "`f << g` is `g >> f`" "<< is gone"
            Expect.stringContains (checkErr "1 << 2").Message "`f << g` is `g >> f`" "taught before the operands"
            Expect.stringContains (checkErr (inc + "inc <| 40")).Message "weir has no `<|`" "<| is refused"
            Expect.stringContains (checkErr "1 != 2").Message "weir's inequality is `<>`" "!= teaches <>"
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
                "let firstTwo = take 2 in ls |> firstTwo |> where (fun f -> f.hidden)"
                (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
        }
        test "lambda in polymorphic position without data now checks via rows" {
            expectValue
                "let staged = where (fun f -> f.hidden) in ls |> staged"
                (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
        }
        test "shadowing" { expectValue "let x = 1 in let x = 2 in x" (VInt 2) }
        test "string concat" { expectValue "\"foo\" + \"bar\"" (VStr "foobar") }
        test "comparison" { expectValue "2 > 1" (VBool true) }
    ]

let warningTests =
    testList "Exhaustiveness warnings" [
        test "missing case is a hard error" {
            let terr = checkErr "match Running 5 with | Running n -> n"
            Expect.stringContains (formatError terr) "missing: Stopped" ""
        }
        test "all cases covered checks" {
            Expect.isEmpty (warningsOf "match Running 5 with | Running n -> n | Stopped -> 0") ""
        }
        test "wildcard covers everything" { Expect.isEmpty (warningsOf "match Running 5 with | _ -> 0") "" }
        test "a lone bare tuple in a list warns; parens and pair lists do not [D:list-comma-warning]" {
            let msgs src = warningsOf src |> List.map _.Message
            Expect.exists (msgs "[1, 2]") (fun m -> m.Contains "separated by ';'") "the comma habit"
            Expect.isEmpty (msgs "[(1, 2)]") "a parenthesized tuple is deliberate"
            Expect.isEmpty (msgs "[\"a\", 1; \"b\", 2]") "a list of pairs is the F# idiom"
            Expect.isEmpty (msgs "[1; 2]") ""
        }
        test
            "a pair alone on its line below the '[' is the one-per-line layout, not the comma habit [D:list-comma-warning]" {
            let warns lines =
                diagsIn "pairs.weir" lines
                |> List.filter (fun d -> d.Message.Contains "this list has one item, a tuple")

            Expect.isEmpty (warns [ "let b = ["; "    \"x\", 1"; "]"; "print (show (Seq.length b))" ]) "multi-line"
            Expect.isNonEmpty (warns [ "let a = [\"x\", 1]"; "print (show (Seq.length a))" ]) "single-line still warns"
        }
        test "check refuses what the init loader refuses in #session [D:repl-init]" {
            let codes lines =
                errorsIn "init.weir" ("#init" :: "" :: lines)
                |> List.map (fun d -> d.Code, d.Line, d.Col)

            Expect.equal
                (codes [
                    "#session {"
                    "    env = ["
                    "        \"X\", $(cat /etc/hostname | line)"
                    "    ]"
                    "}"
                ])
                [ "session-command", 4, 11 ]
                "a value that runs a command"

            Expect.equal
                (codes [
                    "let name = \"h\""
                    "#session {"
                    "    env = ["
                    "        \"X\", name"
                    "    ]"
                    "}"
                ])
                [ "session-names-declaration", 6, 14 ]
                "a value naming a declaration — anchored on the name"

            Expect.equal
                (codes [ "#session {"; "    foo = 1"; "    echoCap = \"x\""; "    echoCap = 3"; "}" ])
                [ "session-key", 4, 5; "session-type", 5, 15; "session-key", 6, 5 ]
                "unknown, mistyped and duplicate keys"

            Expect.isEmpty
                (codes [
                    "let prompt () = \"> \""
                    "#session {"
                    "    env = ["
                    "        \"X\", File.read \"/etc/hostname\" |> Seq.exactlyOne"
                    "    ]"
                    "    prompt = prompt"
                    "}"
                ])
                "a file read is not a command, and prompt may name a declaration"

            // the declaration, alias and block-shape rules ride the same
            // path [D:init-check-one-home]
            let codesOf lines =
                codes lines |> List.map (fun (c, _, _) -> c)

            Expect.equal (codesOf [ "print \"x\"" ]) [ "init-decl" ] "declaration-only"

            Expect.isNonEmpty
                (codesOf [ "let evens () = [1; 2] |> where (fun n -> n > 1)" ])
                "bare names are for the prompt, not the init file"

            Expect.equal
                (codesOf [ "#alias inf = git add . | and git commit" ])
                [ "init-alias" ]
                "a chaining glyph in an alias"

            Expect.equal (codesOf [ "let b = git log -n 1 | line" ]) [ "init-decl" ] "an eager command let"
            Expect.isEmpty (codesOf [ "let gl = git log -n 1" ]) "a lazy command let binds"
            Expect.equal (codesOf [ "#alias a = git"; "#alias b = a status" ]) [ "init-alias" ] "alias-of-alias"
            Expect.equal (codesOf [ "#alias gl = git log"; "let gl = 1" ]) [ "init-alias" ] "an alias a let shadows"
            Expect.equal (codesOf [ "#session {"; "    echoCap = 3" ]) [ "session-shape" ] "unclosed block"

            Expect.equal
                (codesOf [ "#session {"; "    prompt = p"; "}"; "let p () = \"> \"" ])
                []
                "prompt checks after the declarations, wherever they sit"
        }
        test "an unmarked init file names the marker [D:init-marker]" {
            let ic =
                Weir.Init.checkInit Weir.Statements.assumeResolver "init.weir" [ "let x = 1" ]

            Expect.equal (ic.Diags |> List.map _.Code) [ "init-marker" ] ""
        }
        test "check refuses the #session values the loader refuses [D:session-value-one-home]" {
            let msgs (setting: string) =
                errorsIn "init.weir" [ "#init"; "#session {"; "    " + setting; "}" ]
                |> List.map (fun d -> d.Code, d.Message)

            let one setting (msg: string) =
                Expect.equal (msgs setting) [ "session-value", msg ] setting

            one "logLevel = \"loud\"" "logLevel=loud: unknown log level (one of trace|debug|info|warn|off)"
            one "echoCap = 0" "echoCap must be positive; got 0"
            one "echoCap = -3" "echoCap must be positive; got -3"
            one "echoCap = 3000000000" "echoCap 3000000000 is too large (at most 2147483647)"

            one
                "env = [ (\"A=B\", \"x\") ]"
                "#session env: the env key 'A=B' contains '=' — a child would read it as the variable 'A'; an env name has no '='"

            one
                "env = [ (\"\", \"x\") ]"
                "#session env: an env key is empty — a child cannot receive an unnamed variable"

            one
                "env = [ (\"A\\u{0}B\", \"x\") ]"
                "#session env: the env key 'A\\x00B' contains a NUL byte — an env name is NUL-free"

            one
                "env = [ \"OK\", \"x\\u{0}y\" ]"
                "#session env: the value for 'OK' contains a NUL byte — the environment would cut it short there; an env value is NUL-free"

            Expect.isEmpty (msgs "logLevel = Str.trim \" loud \"") "a computed value waits for the load"
            Expect.isEmpty (msgs "echoCap = 5") "an in-range literal"
        }
        test "a comma between record-literal fields teaches the separator" {
            for src in
                [
                    "let v = { a = 1, b = 2 }"
                    "let v = {| a = 1, b = 2 |}"
                    "let v = { T.zero with a = 1, b = 2 }"
                ] do
                let e =
                    firstErrorIn "comma.weir" [ "type T = { a: int; b: int }"; src; "print $\"{v.a}\"" ]

                Expect.stringContains e.Message "record fields are separated by ';' or a newline, not ','" src
                Expect.isFalse (e.Message.Contains "no declared record") src

            // a tuple-typed field keeps its tuple
            errorsCleanIn
                "comma.weir"
                [ "type P = { a: int * int }"; "let v = { a = 1, 2 }"; "print $\"{v.a}\"" ]
                "a tuple value"
        }
        test "arm after a catch-all is a hard error" {
            let terr = checkErr "match Running 5 with | _ -> 0 | Stopped -> 1"
            Expect.stringContains terr.Message "unreachable" ""
        }
        test "a catch-all inside an or-pattern names its own later alternatives, arms counted once [D:or-patterns]" {
            let alt = checkErr "match Some 1 with | Some 1 -> 1 | _ | None -> 2"
            Expect.stringContains alt.Message "the or-pattern's alternative after it is unreachable" "same arm"
            Expect.isFalse (alt.Message.Contains "arm below") "no arm below"

            let arms = checkErr "match Some 1 with | _ -> 1 | Some 2 | Some 3 -> 2"
            Expect.stringContains arms.Message "the arm below is unreachable" "one arm, two alternatives"
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
        test "named groups REJECT with the binder teaching [D:no-named-groups]" {
            // .NET numbers positionals first, named after — the outlier
            // among engines; weir names captures at the binder
            let terr =
                checkErr "match \"ab\" with | Regex @\"(?<x>a)(b)\" (p, q) -> p | _ -> \"?\""

            Expect.stringContains terr.Message "named group '(?<x>'" "names the group"
            Expect.stringContains terr.Message "names captures at the binder" "the design statement"

            // the (?'name') spelling rejects through the same table
            let m2 =
                (checkErr "match \"a\" with | Regex @\"(?'y'a)\" p -> p | _ -> \"?\"").Message

            Expect.stringContains m2 "named group '(?<y>'" "the quote spelling, same refusal"
        }
        test "a Python (?P<name>) group gets the named-group teaching [D:no-named-groups]" {
            let terr =
                checkErr "match \"a=1\" with | Regex @\"(?P<k>\\w+)=(\\d+)\" (k, v) -> k + v | _ -> \"\""

            Expect.stringContains terr.Message "named group '(?P<k>'" "at check time"
            Expect.stringContains terr.Message "names captures at the binder" "the teaching"
            Expect.isFalse (terr.Message.Contains "Unrecognized grouping") "never .NET's raw message"

            Expect.stringContains
                (msgOf "Str.isMatch @\"(?P<k>\\w+)\" \"abc\"")
                "named group '(?P<k>'"
                "expression side"
            // an escaped paren is no group: a literal `(`, `?` on it
            expectValue "Str.isMatch @\"\\(?P<k>\" \"P<k>\"" (VBool true)
        }
        test "the detection's ONLY risk, pinned: lookbehinds and (?: still work [D:no-named-groups]" {
            // (?<= and (?<! share three characters with (?<name> — the
            // engine's own group table is the detector, so zero-width
            // constructs cannot false-positive
            expectValue "match \"ab\" with | Regex @\"(?<=a)(b)\" b -> b | _ -> \"?\"" (VStr "b")
            expectValue "match \"cb\" with | Regex @\"(?<!a)(b)\" b -> b | _ -> \"?\"" (VStr "b")
            expectValue "match \"ababc\" with | Regex @\"((?:ab)+)c\" g -> g | _ -> \"?\"" (VStr "abab")

            // zero movement on backreferences and inline flags
            expectValue "match \"aa\" with | Regex @\"(a)\\1\" a -> a | _ -> \"?\"" (VStr "a")
            expectValue "match \"AB\" with | Regex @\"(?i)(ab)\" g -> g | _ -> \"?\"" (VStr "AB")
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
            Expect.stringContains terr.Message "string value" ""
        }
        test "the raw literal: backslashes belong to the regex (no doubling)" {
            // the source below contains a single backslash before w/d
            expectValue "match \"k=1\" with | Regex @\"(\\w+)=(\\d+)\" (k, _) -> k | _ -> \"?\"" (VStr "k")
        }
        // raw strings [D:raw-strings] — F#'s semantics, probe-backed
        test "raw strings: verbatim and triple semantics" {
            expectValue "@\"a\\nb\" |> Str.length" (VInt 4L)
            expectValue "@\"x\"\"y\"" (VStr "x\"y")
            expectValue "\"\"\"a\"b\"\"\"" (VStr "a\"b")
            // the quad-opener edge: content is a leading quote (FCS's verdict)
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
            Expect.equal (Weir.Assemble.stripComment "let s = @\"x // y\"") "let s = @\"x // y\"" "verbatim"

            Expect.equal
                (Weir.Assemble.stripComment "let s = \"\"\"x // y\"\"\" // real")
                "let s = \"\"\"x // y\"\"\""
                "triple + a real comment after"
        }
        test "the scanner: interpolation holes nest their own strings [D:one-scanner]" {
            let src = "print $\"{\"a\" + \" // x\"}\""
            Expect.equal (Weir.Assemble.stripComment src) src "a // inside a hole's string is content"

            Expect.equal
                (Weir.Assemble.stripComment "print $\"{\"\\\"\"}\" // c")
                "print $\"{\"\\\"\"}\""
                "an escaped quote in a hole's string, then a real comment"

            Expect.equal
                (Weir.Assemble.stripComment "print $\"\"\"{ \"}\" } // y\"\"\" // z")
                "print $\"\"\"{ \"}\" } // y\"\"\""
                "raw interpolated: a brace in a hole's string does not close the hole"

            Expect.isFalse (Weir.Assemble.endsInsideString "print $\"{\"}\"}\"") "closed"
            Expect.isTrue (Weir.Assemble.endsInsideString "print $\"{f \"a\"") "an open hole is inside the string"
            expectValue "$\"{\"a\" + \" // x\"}\"" (VStr "a // x")
        }
        test "the repair closers understand raw kinds" {
            Expect.equal (Weir.Assemble.closers "let s = @\"dangling") "\"" "verbatim closer"
            Expect.equal (Weir.Assemble.closers "let s = \"\"\"dangling") "\"\"\"" "triple closer"
            Expect.equal (Weir.Assemble.closers "let s = @\"a\"\"b") "\"" "doubled quote stays inside"
            Expect.equal (Weir.Assemble.closers "xs |> map (fun b -> {| a = b") " |})" "an anonymous record's closer"
            Expect.equal (Weir.Assemble.closers "f {| a = 1 |} (g") ")" "a closed one leaves nothing"
            Expect.equal (Weir.Assemble.closers "{| a = { b = (1") ")} |}" "nested in order"
            Expect.equal (Weir.Assemble.closers "$\"a {f (1") ")}\"" "a hole closes as a brace"

            Expect.equal
                (Weir.Assemble.anonOpened "f { a = { b = 1 }; c = { d = g (")
                (Some "f {| a = { b = 1 }; c = {| d = g (")
                "open record braces only"

            Expect.equal (Weir.Assemble.anonOpened "$\"{x") None "a hole is no record"
        }
        test "accepted matches are total: the runtime match-failure class is gone" {
            // non-exhaustive shapes are check errors, never runtime failures
            let terr = checkErr "match Stopped with | Running n -> n"
            Expect.stringContains (formatError terr) "missing: Stopped" ""
        }
    ]

let streamingTests =
    testList "Streaming" [
        test "acceptance: infinite source |> take 5 terminates" {
            let infinite = Seq.initInfinite (fun i -> Weir.Builtins.file $"f{i}" i false)

            let result =
                runWith [ "ls", VSeq infinite ] "ls |> where (fun f -> f.bytes > 1B) |> take 5"
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

            let out = runWith [ "src", VSeq source ] "src |> Seq.distinct |> take 2" |> forceSeq

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
            expectValue "Str.rmatchAll @\"(\\d)\" \"12345\" |> take 2 |> Seq.length" (VInt 2L)
        }
        test "catastrophic backtracking raises the named regex timeout [D:regex-timeout]" {
            let ex =
                Expect.throwsC (fun () -> run "Str.isMatch @\"^(a+)+$\" (Str.replicate 40 \"a\" + \"!\")" |> ignore) id

            Expect.stringContains ex.Message "regex timeout" ""
            Expect.equal Weir.Check.regexTimeout (System.TimeSpan.FromSeconds 5.0) "the documented bound"
        }
        test "a timeout on a large input names its size and the per-line spelling [D:regex-timeout-size]" {
            let timedOut input =
                Expect.throwsC
                    (fun () ->
                        Weir.Check.regexGuarded "a|b" input (fun () ->
                            raise (System.Text.RegularExpressions.RegexMatchTimeoutException()))
                        |> ignore)
                    (fun ex -> ex.Message)

            let big = timedOut (System.String('x', Weir.Check.regexLargeInput))
            Expect.stringContains big $"on a {Weir.Check.regexLargeInput}-char input" "names the size"
            Expect.stringContains big "Seq.where (Str.isMatch pat)" "names the per-line spelling"
            Expect.isFalse (big.Contains "backtracks catastrophically on this input") "does not assert backtracking"

            let small = timedOut (System.String('x', Weir.Check.regexLargeInput - 1))

            Expect.stringContains
                small
                "the pattern backtracks catastrophically on this input"
                "a small input keeps the backtracking words"
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

            skipOnWindows () // runs real `head`
            let out = Weir.Proc.linesWith [] "head" [ "-1" ] (Some source) |> List.ofSeq
            Expect.equal out [ "1" ] "head takes the first line"
            Expect.isLessThan pulled.Value 500000 "the input was not drained"
        }
        test "feed closes stdin on input exhaustion: EOF-needing children finish [D:spawn-spec]" {
            // bare `sort` (sh does not resolve in the Windows unit
            // context — every sh-spawning unit test is skipped there),
            // with an odd total byte count: System32's sort.exe runs
            // IsTextUnicode on stdin, and a short even-length LF-only
            // buffer misdetects as UTF-16 (the Bush-hid-the-facts
            // class, '?' output); an odd length cannot
            let out =
                Weir.Proc.linesWith [] "sort" [] (Some(seq [ "banana"; "apples"; "kiwi" ]))
                |> List.ofSeq

            Expect.equal out [ "apples"; "banana"; "kiwi" ] "sort saw EOF and emitted"
        }
        test "acceptance: first 5 pulls exactly 5 elements from the source" {
            let pulled = ref 0

            let counting =
                Seq.initInfinite (fun i ->
                    System.Threading.Interlocked.Increment pulled |> ignore
                    Weir.Builtins.file $"f{i}" i false)

            runWith [ "ls", VSeq counting ] "ls |> take 5" |> forceSeq |> ignore
            Expect.equal pulled.Value 5 "no over-pulling"
        }
        test "where pulls only what the filter and take demand" {
            let pulled = ref 0

            let counting =
                Seq.initInfinite (fun i ->
                    System.Threading.Interlocked.Increment pulled |> ignore
                    Weir.Builtins.file $"f{i}" i false)

            runWith [ "ls", VSeq counting ] "ls |> where (fun f -> f.bytes > 1B) |> take 2"
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

            runWith [ "ls", VSeq counting ] "ls |> where (fun f -> f.bytes > 1B) |> take 5"
            |> ignore

            Expect.equal pulled.Value 0 "evaluation alone must not enumerate"
        }
        test "(Seq.initInfinite id) through map and take" {
            Expect.equal
                (run "(Seq.initInfinite id) |> map (fun x -> x * x) |> take 5" |> forceSeq)
                [ VInt 0; VInt 1; VInt 4; VInt 9; VInt 16 ]
                ""
        }
        test "sum consumes a finite stream" { expectValue "(Seq.initInfinite id) |> take 5 |> sum" (VInt 10) }
        test "lambda pipe stage stays lazy" {
            expectValue "(Seq.initInfinite id) |> map (fun x -> x + 1) |> take 3 |> sum" (VInt 6)
        }
        test "equality on seqs is rejected" {
            Expect.stringContains
                (checkErr "(Seq.initInfinite id) == (Seq.initInfinite id)").Message
                "'==' is not defined for seq<int>"
                ""
        }
        test "equality through a seq-carrying record is rejected" {
            let holderEnv = env |> declare "type Holder = { S: seq<int> }"
            let e = parse "let h = { S = (Seq.initInfinite id) } in h == h"

            match Weir.Check.typecheck holderEnv e with
            | Ok _ -> failtest "expected rejection"
            | Error terr -> Expect.stringContains terr.Message "'==' is not defined for Holder" ""
        }
        test "equality on union values still works" {
            expectValue "Running 1 == Running 1" (VBool true)
            expectValue "Running 1 == Stopped" (VBool false)
        }
        test "one non-Eq field poisons a MIXED record (mutation-spike survivor)" {
            // decompose must demand Eq of every field; an Eq-able
            // sibling must not carry the record [D:inferred-type-classes]
            let mixedEnv = env |> declare "type Mixed = { A: int; B: seq<int> }"
            let e = parse "let m = { A = 1; B = (Seq.initInfinite id) } in m == m"

            match Weir.Check.typecheck mixedEnv e with
            | Ok _ -> failtest "expected rejection"
            | Error terr -> Expect.stringContains terr.Message "'==' is not defined for Mixed" ""
        }
    ]

let shorthandTests =
    testList "Underscore shorthand and escapes" [
        test "underscore field access desugars to a lambda" {
            expectParse "where _.readOnly" "(where (fun _ _.readOnly))"
        }
        test "where with shorthand filters" {
            expectValue "ls |> where _.hidden" (VSeq [ Weir.Builtins.file "b.bin" 5242880 true ])
        }
        test "map with shorthand projects" {
            Expect.equal (checkOk "ls |> map _.bytes").Ty (TSeq TSize) ""

            Expect.equal (run "ls |> map _.name |> take 2" |> forceSeq) [ VStr "a.txt"; VStr "b.bin" ] ""
        }
        test "shorthand chains through nested records" { expectParse "map _.A.B" "(map (fun _ _.A.B))" }
        test "shorthand in a larger expression gets the targeted hint" {
            let terr = checkErr "ls |> where (_.bytes > 9B) |> take \"x\""
            Expect.stringContains terr.Message "_.Field is a whole function" ""
        }
        test "byte literals filter correctly" {
            expectValue "ls |> where (fun f -> f.bytes > 2MiB) |> map _.name" (VSeq [ VStr "b.bin"; VStr "d.iso" ])
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
        test "\\e and \\u{…} escapes, every escaping string form [D:string-escapes]" {
            expectValue "\"\\e[1m\"" (VStr "\u001b[1m")
            expectValue "\"\\u{41}\\u{e9}\\u{1F600}\"" (VStr "A\u00e9\U0001F600")
            expectValue "\"\\u{000041}\"" (VStr "A")
            expectValue "Str.length \"\\u{1F600}\"" (VInt 1L)
            // interpolation: the braces belong to the escape, never a hole
            expectValue "$\"\\u{41}{1 + 1}\\e\"" (VStr "A2\u001b")
            // a pattern literal decodes the same way
            expectValue "match \"\\u{e9}\" with | \"\\u{e9}\" -> 1 | _ -> 0" (VInt 1L)
            // raw kinds stay raw
            expectValue "@\"\\u{41}\"" (VStr "\\u{41}")
            expectValue "\"\"\"\\e\"\"\"" (VStr "\\e")
        }
        test "a bad \\u{…} teaches; an unknown escape lists the escapes [D:string-escapes]" {
            let perr input =
                match Weir.Parser.parseLine realResolver input with
                | Error msg -> msg
                | Ok _ -> failtest $"expected a parse error: {input}"

            Expect.stringContains
                (perr "let s = \"\\u{D800}\"")
                "is a UTF-16 surrogate, not a character"
                "a surrogate refuses"

            Expect.stringContains (perr "let s = \"\\u{DFFF}\"") "surrogate" "the top of the surrogate range"

            Expect.stringContains
                (perr "let s = \"\\u{110000}\"")
                "past the last Unicode code point, \\u{10FFFF}"
                "past 10FFFF refuses"

            Expect.stringContains (perr "let s = \"\\u{}\"") "takes 1 to 6 hex digits" "no digits"
            Expect.stringContains (perr "let s = \"\\u{1234567}\"") "takes 1 to 6 hex digits" "seven digits"
            Expect.stringContains (perr "let s = \"\\u{12g}\"") "takes 1 to 6 hex digits" "a non-hex digit"
            Expect.stringContains (perr "let s = \"\\u0041\"") "takes its code point in braces" "the brace-less form"
            Expect.stringContains (perr "let s = $\"\\u{D83D}{1}\"") "surrogate" "interpolation refuses too"
            let m = perr "let s = \"\\x41\""
            Expect.stringContains m "'\\x' is not an escape" "an unknown escape"
            Expect.stringContains m "\\e \\u{…}" "the list names the new escapes"
            Expect.stringContains m "verbatim string" "the Windows repair stays"
            Expect.stringContains (perr "let s = \"\\U0001F600\"") "'\\U' is not an escape" "uppercase U is unknown"
        }
        test "the scanners skip a \\u{…} whole — its braces open no hole [D:string-escapes]" {
            let c = Weir.ReplColor.colorizeRepl (fun _ -> false) "print $\"\\u{41}x\""
            Expect.stringContains c "\u001b[32m\"\\u{41}x\"\u001b[0m" "the escape paints as string, not as a hole"
            Expect.equal (Weir.Assemble.closers "print $\"\\u{41}") "\"" "only the quote is open"
        }
        test "escaped strings survive a json roundtrip" {
            let src = VSeq [ VStr """{"name":"a\"b","bytes":1048576,"readOnly":false}""" ]

            Expect.equal (runWith [ "src", src ] "src |> from jsonl JRow |> map _.name" |> forceSeq) [ VStr "a\"b" ] ""
        }
    ]

let lifecycleTests =
    testSequenced
    <| testList "Process lifecycle" [ // POSITIVE CONTROLS FIRST [D:vacuous-probe-audit]: every zero
        // assertion below rests on these two counters; a counter that
        // can break must be shown to count before its zeros mean
        // anything (the macOS vacuous-pass lesson).
        test "positive control: the survivors probe counts a live marker" {
            skipOnWindows ()
            let psi = ProcessStartInfo("/bin/sh")
            psi.ArgumentList.Add "-c"
            // the marker rides $0 and the compound suffix keeps sh from
            // exec-ing the sleep — macOS bash-as-sh replaces a lone
            // simple command, vaporizing a comment-borne marker
            psi.ArgumentList.Add "sleep 30; :"
            psi.ArgumentList.Add "weir-probe-ctl"
            use p = Process.Start psi

            try
                Expect.isGreaterThan (survivors "weir-probe-ctl") 0 "a live marker must count"
            finally
                p.Kill(entireProcessTree = true)

            Expect.isTrue (eventuallyNoSurvivors "weir-probe-ctl") "the killed control must clear"
        }
        test "positive control: the zombie counter counts a real zombie" {
            skipOnWindows ()
            // the .NET runtime auto-reaps our children, so the control
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
        // tripwire pair: the simple case passes even without tree-kill because
        // sh execs a single command (one process). The compound case is the
        // real guard — sh forks pipeline children and only
        // Kill(entireProcessTree: true) reaches them. If the sh backing is
        // removed (PLAN-command-mode Session 2), this analysis changes:
        // re-derive which of these guards what.
        test "simple command: no survivors after partial consumption" {
            skipOnWindows ()
            runReal "sh -c \"yes weir-s1-simple\" |> take 3" |> forceSeq |> ignore

            Expect.isTrue (eventuallyNoSurvivors "weir-s1-simple") "yes leaked"
        }
        test "compound command: no survivors after partial consumption" {
            skipOnWindows ()

            runReal "sh -c \"yes weir-s1-compound | grep --line-buffered weir-s1-compound\" |> take 3"
            |> forceSeq
            |> ignore

            Expect.isTrue (eventuallyNoSurvivors "weir-s1-compound") "pipeline children leaked"
        }
        test "50 completed commands leave no zombies" {
            skipOnWindows ()

            for _ in 1..50 do
                runReal "sh -c \"true\"" |> forceSeq |> ignore

            let zombies = defunctChildren ()
            Expect.equal zombies 0 "defunct children accumulated"
        }
        test "50 abandoned streams leave no zombies" {
            skipOnWindows ()

            for _ in 1..50 do
                runReal "sh -c \"yes weir-s1-zombie\" |> take 1" |> forceSeq |> ignore

            Expect.isTrue (eventuallyNoSurvivors "weir-s1-zombie") "killed children leaked"
            let zombies = defunctChildren ()
            Expect.equal zombies 0 "defunct children accumulated after kills"
        }
    ]

let session2Tests =
    testSequenced
    <| testList "sh/cmd split and session cwd" [
        test "list literal parses, types, evaluates" {
            expectParse "[1; 2; 3]" "[1; 2; 3]"
            Expect.equal (checkOk "[1; 2]").Ty (TSeq(TInt)) "type"
            expectValue "[1; 2] |> sum" (VInt 3)
            expectValue "[\"a\"; \"b\"] |> take 1" (VSeq [ VStr "a" ])
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
            skipOnWindows ()
            Expect.equal (runReal "echo \"*\"" |> forceSeq) [ VStr "*" ] ""
        }
        test "sh is the escape hatch: glob expands" {
            skipOnWindows ()

            let dir = weirPath (tempPath "weir-s2")

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
            skipOnWindows ()
            Expect.equal (runReal "echo \"; rm -rf x\"" |> forceSeq) [ VStr "; rm -rf x" ] ""
        }
        // [D:drop-command-builtins] the "for cmd" twin retired — cmd is
        // gone; the sh-spawn version below covers cwd-affects-spawns
        test "cd changes the spawn cwd for sh" {
            skipOnWindows ()
            // /usr, not /tmp: macOS's /tmp is a symlink to /private/tmp
            // and the child's getcwd reports the physical path
            try
                Expect.equal (runReal "let d = cd \"/usr\" in $(sh -c \"pwd\")" |> forceSeq) [ VStr "/usr" ] ""
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
        }
        test "completion follows cd, and an offered path opens [F1]" {
            skipOnWindows ()
            // completion resolved relative paths against the process cwd,
            // which weir never chdir's — so it froze at the startup
            // directory and vouched for paths File.read immediately
            // rejected.
            let root = tempPath "weir-cwd"

            let a = Path.Combine(root, "a")
            let bdir = Path.Combine(root, "b")
            Directory.CreateDirectory a |> ignore
            Directory.CreateDirectory bdir |> ignore
            File.WriteAllText(Path.Combine(a, "marker-a.txt"), "A")
            File.WriteAllText(Path.Combine(bdir, "marker-b.txt"), "B")

            let offer () =
                let text = "File.read \"./m"
                Weir.Complete.suggest Weir.Builtins.typeEnvStrict text (Weir.Complete.wordStartAt text text.Length)

            try
                // the positive twin: correct before any cd, so the fix is not
                // "disable relative completion"
                Weir.Session.setCwd a
                Expect.equal (offer ()) [ "./marker-a.txt" ] "completion reads the session cwd"

                // the transcript's failing half, inverted
                Weir.Session.setCwd bdir
                let after = offer ()
                Expect.equal after [ "./marker-b.txt" ] "completion follows cd"

                // the property no existing completion pin asserts: an
                // offered path opens
                Expect.isTrue
                    (File.Exists(Weir.Session.resolve (List.head after)))
                    "an offered path resolves to a real file"
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
                Directory.Delete(root, true)
        }

        test "the stored cwd is normalised, so cd and pwd agree [F2]" {
            skipOnWindows ()

            try
                // normalised at assignment, so no reader ever sees the
                // spelling of the argument — Path.GetFullPath preserves
                // a trailing separator
                Weir.Session.setCwd "/tmp/"
                Expect.equal (Weir.Session.Cwd()) "/tmp" "a trailing separator is trimmed"
                Weir.Session.setCwd "/"
                Expect.equal (Weir.Session.Cwd()) "/" "the root keeps its separator"

                for spelling in [ "/tmp"; "/tmp/"; "/tmp/./"; "/usr/../tmp" ] do
                    Expect.equal (runReal $"cd \"{spelling}\"") (VStr "/tmp") $"cd {spelling} normalises"

                // two builtins reporting one fact must not disagree on shape
                Expect.equal (runReal "let c = cd \"/tmp/\" in pwd") (VStr "/tmp") "pwd agrees with cd"
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
        }

        test "pwd captures the cwd where it is evaluated, not at force time [D:ambient-capture]" {
            skipOnWindows ()

            try
                // bound BEFORE the cd, pwd keeps the cwd at its evaluation —
                // the read is snapshotted at reference [D:pwd-string]
                Weir.Session.setCwd "/"

                Expect.equal
                    (run "let p = pwd in let d = cd \"/tmp\" in p")
                    (VStr "/")
                    "pwd is bound to the cwd at its evaluation, before the cd"

                // bound AFTER the cd, it sees the new dir
                Weir.Session.setCwd "/"

                Expect.equal (run "let d = cd \"/tmp\" in pwd") (VStr "/tmp") "pwd bound after the cd sees the new cwd"
            finally
                Weir.Session.setCwd (System.IO.Directory.GetCurrentDirectory())
        }
        test "cd on a missing directory fails at runtime" {
            Expect.throws (fun () -> run "cd \"/definitely/not/here\"" |> ignore) ""
        }
        test "cd resolves relative and dotdot" {
            skipOnWindows ()

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
            skipOnWindows ()
            runReal "yes \"weir-s2-direct\" |> take 3" |> forceSeq |> ignore
            Expect.isTrue (eventuallyNoSurvivors "weir-s2-direct") "direct-exec child leaked"
        }
        test "direct cmd: 50 abandoned streams leave no zombies" {
            skipOnWindows ()

            for _ in 1..50 do
                runReal "yes \"weir-s2-dz\" |> take 1" |> forceSeq |> ignore

            Expect.isTrue (eventuallyNoSurvivors "weir-s2-dz") "direct-exec children leaked"
            Expect.equal (defunctChildren ()) 0 "defunct children accumulated"
        }
        // [D:nul-path] the run-time root: Session.resolve is the one
        // funnel every File/Proc/completion builtin shares. A NUL in the
        // path made Path.GetFullPath throw a raw ArgumentException (a
        // SIGABRT reached through the parser, a raw .NET message elsewhere);
        // it now raises a clear weir-shaped located error the builtins
        // surface. NUL-free paths still resolve unchanged.
        test "Session.resolve refuses a NUL-bearing path with a weir message" {
            Expect.throwsC (fun () -> Weir.Session.resolve "a\000b" |> ignore) (fun ex ->
                Expect.stringContains ex.Message "NUL byte" "the message names the NUL, not the framework")
            // and the negative twin: a NUL-free path still resolves
            Expect.isTrue
                (System.IO.Path.IsPathRooted(Weir.Session.resolve "a/b"))
                "an ordinary path resolves unchanged"
        }
        // [D:nul-path] the parse-time pre-empt: a NUL-bearing program head
        // is not-found (false) before Extern.exists reaches Session.resolve,
        // whose raise would abort the parse resolver (it runs outside the
        // runner's try). So the classifier emits its ordinary
        // missing-command diagnostic instead of a crash.
        test "Extern.exists reports a NUL-bearing head as not-found, not a raise" {
            skipOnWindows ()
            // pathy (contains '/') and NUL-bearing: without the guard this
            // is exactly the parse-time crash path
            Expect.isFalse (Weir.Extern.exists "./a\000b") "a NUL-in-slash head is not found"
            Expect.isFalse (Weir.Extern.exists "a\000b") "a bare NUL head is not found"
        }
    ]

let stringTests =
    testList "Strings and seq library" [
        test "sha256: exact digests, the encoding law under test [D:encoding-law]" {
            // sha256sum parity for ASCII; the UTF-8 bytes rule for non-ASCII
            expectValue "Str.sha256 \"hello\"" (VStr "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")

            expectValue "Str.sha256 \"café\"" (VStr "850f7dc43910ff890f8879c0ed26fe697c93a067ad93a7d50f466a7028a9bf4e")
        }
        test "Str.width counts terminal columns [D:str-width]" {
            let cases = [
                "", 0, "empty"
                "abc", 3, "ASCII"
                "日本語", 6, "CJK ideographs are wide"
                "한글", 4, "Hangul syllables are wide"
                "ｱｲ", 2, "halfwidth katakana is narrow"
                "ＡＢ", 4, "fullwidth forms are wide"
                "e\u0301", 1, "a combining acute adds nothing"
                "\u1100\u1161", 2, "a conjoining Hangul vowel joins the leading consonant"
                "a\u200Bb", 2, "a zero-width space"
                "a\tb", 2, "a control character takes no cell"
                "\u00AD", 1, "the soft hyphen prints"
                "😀", 2, "an emoji-presentation character"
                "⌚", 2, "a BMP emoji-presentation character"
                "❤", 1, "a text-presentation character"
                "❤\uFE0F", 2, "VS16 asks for emoji presentation"
                "👍\U0001F3FD", 2, "a skin-tone modifier merges"
                "👨\u200D👩\u200D👧", 2, "a ZWJ sequence is one glyph"
                "🇯🇵", 2, "a flag's two regional indicators"
                "🇯🇵🇺🇸", 4, "two flags"
                "\u001b[31mred\u001b[0m", 3, "SGR colour takes no cell"
                "\u001b]0;title\u0007x", 1, "an OSC takes no cell"
                "\uE000", 1, "private use is narrow"
                "a😀b", 4, "mixed"
            ]

            for text, cells, why in cases do
                Expect.equal (CellWidth.ofString text) cells why

            expectValue "Str.width \"日本\"" (VInt 4L)
            expectValue "Str.width \"\\u{1F600}x\"" (VInt 3L)
            expectValue "Str.length \"日本\"" (VInt 2L)
        }
        test "sha1: exact digests, sha1sum parity, UTF-8 bytes [D:sha1]" {
            expectValue "Str.sha1 \"hello\"" (VStr "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d")
            expectValue "Str.sha1 \"café\"" (VStr "f424452a9673918c6f09b0cdd35b20be8e6ae7d7")
            expectValue "Str.sha1 \"\"" (VStr "da39a3ee5e6b4b0d3255bfef95601890afd80709")
            expectValue "Bytes.sha1 (Str.toUtf8 \"hello\")" (VStr "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d")
        }
        test "base64: exact form, round-trip, liberal unpadded input [D:encoding-law]" {
            expectValue "Str.toBase64 \"café\"" (VStr "Y2Fmw6k=")
            expectValue "Str.toBase64 \"café\" |> Str.fromBase64" (VStr "café")
            expectValue "Str.fromBase64 \"Y2Fmw6k\"" (VStr "café") // unpadded accepted
        }
        test "fromBase64 rejects malformed AND non-text; the try twin is None for both [D:encoding-law]" {
            let ex = Expect.throwsC (fun () -> run "Str.fromBase64 \"!!!\"" |> ignore) id
            Expect.stringContains ex.Message "invalid base64" "malformed raises"

            // //79 is valid base64 of FF FE FD — bytes, not text:
            // corruption must not wear a success
            let ex2 = Expect.throwsC (fun () -> run "Str.fromBase64 \"//79\"" |> ignore) id
            Expect.stringContains ex2.Message "not text" "non-UTF-8 raises with the reason"

            expectValue "Str.tryFromBase64 \"!!!\"" (VUnion("None", None))
            expectValue "Str.tryFromBase64 \"//79\"" (VUnion("None", None))
        }
        test "fromBase64: NUL is non-text — valid UTF-8, but the byte the binary detector keys on [D:encoding-law]" {
            // YQBi = "a NUL b"; AAA= = two NULs — the gate must agree
            // with [D:binary-echo]'s NUL probe about what binary means
            let ex = Expect.throwsC (fun () -> run "Str.fromBase64 \"YQBi\"" |> ignore) id
            Expect.stringContains ex.Message "it contains NUL bytes" "NUL raises with the reason"
            expectValue "Str.tryFromBase64 \"YQBi\"" (VUnion("None", None))
            expectValue "Str.tryFromBase64 \"AAA=\"" (VUnion("None", None))

            // a real PNG header still refuses (the case that already worked)
            expectValue "Str.tryFromBase64 \"iVBORw0KGgo=\"" (VUnion("None", None))
        }
        test "contains, startsWith, endsWith are data-last" {
            // bare `contains` retired [D:bare-rule]: two homes, one
            // slot — qualified both sides; the data-last law is the
            // member's, unchanged
            expectValue "Str.contains \"err\" \"stderr\"" (VBool true)
            expectValue "startsWith \"fix:\" \"fix: bug\"" (VBool true)
            expectValue "endsWith \".txt\" \"a.txt\"" (VBool true)
            expectValue "Str.contains \"zzz\" \"stderr\"" (VBool false)
        }
        test "point-free pipeline predicate is the payoff" {
            expectValue
                "[\"an error here\"; \"fine\"; \"error again\"] |> where (Str.contains \"error\") |> head"
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
        test "splitOnce splits at the FIRST separator, tail intact [D:split-once]" {
            // the receipt class: the tail contains the separator —
            // Str.split + a [k; v] pattern silently misses here
            expectValue "Str.splitOnce \"=\" \"KEY=a=b\"" (VTuple [ VStr "KEY"; VStr "a=b" ])
            // destructures straight into names (the tuple answer)
            expectValue "let (k, v) = Str.splitOnce \"=\" \"k=v\" in k + v" (VStr "kv")
            // multi-char separator consumed whole
            expectValue "Str.splitOnce \"::\" \"a::b\"" (VTuple [ VStr "a"; VStr "b" ])
            // edges are empty strings, never absences
            expectValue "Str.splitOnce \"=\" \"=v\"" (VTuple [ VStr ""; VStr "v" ])
            expectValue "Str.splitOnce \"=\" \"k=\"" (VTuple [ VStr "k"; VStr "" ])

            Expect.equal
                (msgOf "Str.splitOnce \":\" \"abc\"")
                "splitOnce: no \":\" in the input"
                "the raising form names the separator"

            Expect.equal
                (msgOf "Str.splitOnce \"\" \"abc\"")
                "splitOnce: the separator cannot be empty"
                "empty separator refused"

            Expect.equal
                (msgOf "Str.trySplitOnce \"\" \"abc\"")
                "trySplitOnce: the separator cannot be empty"
                "…on the try side too (absence is Option's job, malformed args are not)"

            // the Option twin, and absence as None
            expectValue "Str.trySplitOnce \"=\" \"k=v\"" (VUnion("Some", Some(VTuple [ VStr "k"; VStr "v" ])))
            expectValue "Str.trySplitOnce \"=\" \"none\"" (VUnion("None", None))
        }
        test "rreplace replaces every regex match, expanding $1 ${1} $0 $$ [D:str-rreplace]" {
            expectValue
                "Str.rreplace @\"(\\w+)@(\\w+)\" \"$2 at $1\" \"me@host, you@box\""
                (VStr "host at me, box at you")

            expectValue "Str.rreplace @\"(\\d)\" \"${1}0\" \"a1b2\"" (VStr "a10b20")
            expectValue "Str.rreplace @\"\\d+\" \"<$0>\" \"a12b3\"" (VStr "a<12>b<3>")
            expectValue "Str.rreplace @\"\\.\" \"$$\" \"1.5\"" (VStr "1$5")
            expectValue "Str.rreplace \"x\" \"y\" \"abc\"" (VStr "abc")
            // an unmatched optional group is empty; empty matches advance
            expectValue "Str.rreplace @\"a(b)?\" \"[$1]\" \"ab a\"" (VStr "[b] []")
            expectValue "Str.rreplace \"x*\" \"-\" \"ab\"" (VStr "-a-b-")
            // literal sibling stays literal
            expectValue "Str.replace \"$1\" \"z\" \"a$1\"" (VStr "az")

            Expect.stringContains
                (msgOf "Str.rreplace \"(a)\" \"$2\" \"a\"")
                "refers to $2, but the pattern has 1 group"
                "a missing group"

            Expect.stringContains
                (msgOf "Str.rreplace \"a\" \"${1}\" \"b\"")
                "refers to ${1}, but the pattern has no groups"
                "checked before any match"

            Expect.stringContains (msgOf "Str.rreplace \"a\" \"$\" \"a\"") "write $1 or ${1} for a group" "a lone $"

            Expect.stringContains
                (msgOf "Str.rreplace \"a\" \"$&\" \"a\"")
                "$$ for a literal '$'"
                ".NET's $& is not weir's"

            Expect.stringContains
                (msgOf "Str.rreplace \"(a)\" \"${x}\" \"a\"")
                "weir patterns have no named groups"
                "${name} teaches"

            Expect.stringContains (msgOf "Str.rreplace \"(a)\" \"${1\" \"a\"") "needs its closing '}'" "an open ${"
            Expect.stringStarts (msgOf "Str.rreplace \"(\" \"x\" \"a\"") "invalid regex:" "the r-family's error class"
            Expect.stringContains (msgOf "Str.rreplace \"(?P<n>a)\" \"x\" \"a\"") "named" "the (?P<name>) teaching"
        }
        // the r-family counts code points [D:regex-code-points]: `.`, a
        // negated class and \S take a whole pair; no match or capture
        // splits one; an astral literal is one unit for a quantifier
        test "regex members never split a surrogate pair [D:regex-code-points]" {
            expectValue "Str.rreplace \".\" \"*\" \"😀\"" (VStr "*")
            expectValue "Str.rreplace \"\" \"-\" \"😀\"" (VStr "-😀-")
            expectValue "Str.isMatch \"^.$\" \"😀\"" (VBool true)
            expectValue "Str.isMatch \"^..$\" \"😀\"" (VBool false)
            expectValue "Str.rsplit \"\" \"😀\" |> Seq.length" (VInt 3L)
            expectValue "Str.rreplace \"[^a]\" \"*\" \"a😀b\"" (VStr "a**")
            expectValue "Str.rreplace @\"\\S\" \"*\" \"a😀\"" (VStr "**")
            expectValue "Str.rreplace \"[^😀]\" \"*\" \"a😀🎉\"" (VStr "*😀*")
            expectValue "Str.rreplace \"[😀b]\" \"*\" \"a😀b🎉\"" (VStr "a**🎉")
            expectValue "Str.rreplace \"😀+\" \"*\" \"a😀😀b\"" (VStr "a*b")
            expectValue "Str.rmatch \"(.)\" \"😀x\"" (VUnion("Some", Some(VSeq [ VStr "😀" ])))
            expectValue "Str.rmatchAll \"(.)\" \"😀x\" |> Seq.length" (VInt 2L)
            expectValue "Str.rreplace \"(?i)A.\" \"_\" \"xa😀Ab\"" (VStr "x__")

            expectValue "match \"ab😀cd\" with | Regex @\"^(.{3})\" (h) -> h | _ -> \"\"" (VStr "ab😀")
            // the user's own pattern is what an error quotes
            Expect.stringStarts (msgOf "Str.rreplace \"[\" \"x\" \"a\"") "invalid regex:" "still the r-family's class"
        }
        test "rsplitOnce splits at the LAST separator [D:split-once]" {
            // the from-the-right receipt: user@host:port, [::1]:port
            expectValue "Str.rsplitOnce \":\" \"[::1]:8080\"" (VTuple [ VStr "[::1]"; VStr "8080" ])
            expectValue "Str.rsplitOnce \"@\" \"a@b@host\"" (VTuple [ VStr "a@b"; VStr "host" ])
            // multi-char, and empty edges (never absences)
            expectValue "Str.rsplitOnce \"::\" \"a::b::c\"" (VTuple [ VStr "a::b"; VStr "c" ])
            expectValue "Str.rsplitOnce \"/\" \"x/\"" (VTuple [ VStr "x"; VStr "" ])

            Expect.equal
                (msgOf "Str.rsplitOnce \":\" \"abc\"")
                "rsplitOnce: no \":\" in the input"
                "raises, naming the separator"

            Expect.equal
                (msgOf "Str.rsplitOnce \"\" \"abc\"")
                "rsplitOnce: the separator cannot be empty"
                "empty separator refused"
            // the Option twin
            expectValue "Str.tryRsplitOnce \":\" \"a:b:c\"" (VUnion("Some", Some(VTuple [ VStr "a:b"; VStr "c" ])))
            expectValue "Str.tryRsplitOnce \":\" \"none\"" (VUnion("None", None))
        }
        test "Option.orElseWith runs the fallback thunk only on None [D:gap-a-remainder]" {
            // Some short-circuits — the thunk never fires
            expectValue "(Some 1) |> Option.orElseWith (fun () -> Some 2)" (VUnion("Some", Some(VInt 1L)))
            // None takes the thunk's result
            expectValue "None |> Option.orElseWith (fun () -> Some 2)" (VUnion("Some", Some(VInt 2L)))
            // laziness: a raising thunk is untouched when the option is Some
            expectValue "(Some 1) |> Option.orElseWith (fun () -> fail \"boom\")" (VUnion("Some", Some(VInt 1L)))
        }
        test "Seq.tryReduce is reduce's Option twin — None on empty [D:try-convention]" {
            expectValue "[1; 2; 3; 4] |> Seq.tryReduce (fun a b -> a + b)" (VUnion("Some", Some(VInt 10L)))
            // empty (from a filter) is None, where reduce would raise
            expectValue
                "[1; 2] |> Seq.where (fun n -> n > 9) |> Seq.tryReduce (fun a b -> a + b)"
                (VUnion("None", None))
        }
        test "Str.fields: whitespace runs collapse, empties never appear [D:str-fields]" {
            // tabs and spaces mixed — one whitespace class (trim's)
            Expect.equal
                (run "Str.fields \" a\\t b  c\\t\\t\"" |> forceSeq)
                [ VStr "a"; VStr "b"; VStr "c" ]
                "runs collapse; leading and trailing whitespace produce nothing"

            // the no-empties law's degenerate ends
            expectValue "Str.fields \"\" |> Seq.isEmpty" (VBool true)
            expectValue "Str.fields \" \\t \" |> Seq.isEmpty" (VBool true)
        }
        test "acceptance: a kubectl-style column via Str.fields [D:str-fields]" {
            // the spelling this member retires:
            // l |> Str.rmatchAll @"(\S+)" |> Seq.map Seq.head
            expectValue "\"weir-7d9f4c   1/1   Running   0   12m\" |> Str.fields |> Seq.item 1" (VStr "1/1")
        }
        test "Str.rsplit: split on every regex match, split's empties law [D:str-fields]" {
            // the shared law, pinned as an equality: on a literal-shaped
            // pattern the two splitters answer identically (empties kept)
            Expect.equal
                (run "Str.rsplit \",\" \"a,,b\"" |> forceSeq)
                (run "Str.split \",\" \"a,,b\"" |> forceSeq)
                "one empties law, two splitters"

            Expect.equal (run "Str.rsplit \",\" \"\"" |> forceSeq) [ VStr "" ] "the empty subject mirrors split"

            // a multi-char pattern: the whole match is the separator
            Expect.equal
                (run "Str.rsplit @\"\\s*,\\s*\" \"a , b,c\"" |> forceSeq)
                [ VStr "a"; VStr "b"; VStr "c" ]
                "the match spans the run"

            // capture groups never add pieces (the between-match law)
            Expect.equal
                (run "Str.rsplit \"(,)\" \"a,b\"" |> forceSeq)
                [ VStr "a"; VStr "b" ]
                "groups do not interleave"

            // a bad pattern raises in the r-family's error class
            let msg =
                Expect.throwsC (fun () -> run "Str.rsplit \"[unclosed\" \"x\"" |> ignore) id
                |> _.Message

            Expect.stringStarts msg "invalid regex:" "the rmatch family's raise"
        }
        test "Str.length and toInt" {
            expectValue "Str.length \"abc\"" (VInt 3)
            expectValue "toInt \"42\" + 1" (VInt 43)
            Expect.throws (fun () -> run "toInt \"nope\"" |> ignore) "toInt raises"

            let raised (src: string) =
                (Expect.throwsC (fun () -> run src |> ignore) id).Message

            Expect.stringContains (raised "toInt \"nope\"") "not an integer" "text that is no number"

            Expect.stringContains
                (raised "toInt \"99999999999999999999\"")
                "out of range for a 64-bit integer"
                "digits past the range"

            Expect.stringContains
                (raised "toInt \"-99999999999999999999\"")
                "out of range for a 64-bit integer"
                "signed too"

            expectValue "tryToInt \"42\"" (VUnion("Some", Some(VInt 42)))
            expectValue "tryToInt \"nope\"" (VUnion("None", None))
        }
        test "exactlyOne asserts cardinality with DISTINCT messages [D:exactly-one]" {
            expectValue "[42] |> Seq.exactlyOne" (VInt 42L)

            // ruling 2: none and more are different causes — the two
            // messages pin separately, one pin covering both would
            // defeat the member's advantage over head
            Expect.equal
                (msgOf "[] |> Seq.exactlyOne")
                "exactlyOne: expected exactly one element, got none"
                "the produced-nothing cause"

            Expect.equal
                (msgOf "[1; 2] |> Seq.exactlyOne")
                "exactlyOne: expected exactly one element, got more"
                "the produced-extra cause"

            // the try twin: None for both failure shapes, and it must
            // stop at the second element — an infinite source proves it
            expectValue "[7] |> Seq.tryExactlyOne" (VUnion("Some", Some(VInt 7L)))
            expectValue "[] |> Seq.tryExactlyOne" (VUnion("None", None))
            expectValue "[1; 2] |> Seq.tryExactlyOne" (VUnion("None", None))
            expectValue "(Seq.initInfinite id) |> Seq.tryExactlyOne" (VUnion("None", None))
        }
        test "Seq.first is retired; take stands [D:first-retired]" {
            // the reversal's receipt: the teaching, both spellings
            Expect.stringContains
                (checkErr "[1] |> Seq.first 2").Message
                "'Seq.first' is retired: weir's first is 'Seq.take'"
                "the qualified teach"

            Expect.stringContains (checkErr "[1] |> first 2").Message "'first' is retired" "the bare teach"

            // the survivor, and the freed name binds
            expectValue "[1; 2; 3] |> Seq.take 2 |> Seq.length" (VInt 2L)
            expectValue "let first = 9 in first" (VInt 9L)
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
        }
    ]

let chooseTests =
    testList "Seq.choose" [
        test "the idiom: match-or-skip over Regex arms" {
            expectValue
                "[\"a1\"; \"x\"; \"a2\"] |> Seq.choose (fun l -> match l with | Regex @\"a(1|2)\" n -> Some n | _ -> None) |> Str.join \",\""
                (VStr "1,2")
        }
        test "lazy: infinite source, choose, first terminates" {
            let infinite = Seq.initInfinite (fun i -> Weir.Builtins.file $"f{i}" i false)

            match
                runWith
                    [ "ls", VSeq infinite ]
                    "ls |> Seq.choose (fun f -> if f.bytes > 2B then Some (Size.toBytes f.bytes) else None) |> take 2 |> Seq.sum"
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

            match runWith [ "ls", VSeq infinite ] "ls |> Seq.append ls |> take 2 |> Seq.length" with
            | VInt n -> Expect.equal n 2L "lazy on both sides"
            | v -> failtest $"unexpected {v}"
        }
        // deep lazy chains walk iteratively [D:seq-depth-iterative]: a fold
        // nesting Seq.append / Seq.map 200k deep overflowed on the first
        // pull — reaching the asserts is the pin
        test "a 200k-deep Seq.append fold enumerates, both nestings [D:seq-depth-iterative]" {
            expectValue "[1..200000] |> Seq.fold (fun acc i -> Seq.append acc [i]) [] |> Seq.sum" (VInt 20000100000L)
            expectValue "[1..200000] |> Seq.fold (fun acc i -> Seq.append [i] acc) [] |> Seq.head" (VInt 200000L)
        }
        test "a 200k-deep Seq.map fold applies innermost first [D:seq-depth-iterative]" {
            expectValue
                "[1..200000] |> Seq.fold (fun acc i -> Seq.map (fun x -> x + 1) acc) [0] |> Seq.head"
                (VInt 200000L)

            expectValue "[1; 2] |> Seq.map (fun x -> x + 1) |> Seq.map (fun x -> x * 10) |> Seq.sum" (VInt 50L)
        }
        test "alternating append/map nests 100k deep [D:seq-depth-iterative]" {
            expectValue
                "[1..100000] |> Seq.fold (fun acc i -> Seq.append (Seq.map (fun x -> x) acc) [i]) [] |> Seq.head"
                (VInt 1L)
        }
        test "a 200k-deep Seq.where / Seq.choose fold enumerates [D:seq-depth-iterative]" {
            expectValue
                "[1..200000] |> Seq.fold (fun acc i -> Seq.where (fun x -> x <> i) acc) [0; 300000] |> Seq.map (fun x -> $\"{x}\") |> Str.join \",\""
                (VStr "0,300000")

            expectValue
                "[1..200000] |> Seq.fold (fun acc _ -> Seq.choose (fun x -> if x > 0 then Some(x + 1) else None) acc) [-1; 1] |> Seq.sum"
                (VInt 200001L)
        }
        test "200k-deep stateful and splicing combinators enumerate [D:seq-depth-iterative]" {
            expectValue
                "[1..200000] |> Seq.fold (fun acc _ -> Seq.take 2 acc) (Seq.initInfinite id) |> Seq.sum"
                (VInt 1L)

            expectValue
                "[1..200000] |> Seq.fold (fun acc _ -> Seq.collect (fun x -> [x]) acc) [7] |> Seq.head"
                (VInt 7L)

            expectValue "[1..200000] |> Seq.fold (fun acc _ -> Seq.concat [acc]) [7] |> Seq.head" (VInt 7L)

            expectValue
                "[1..200000] |> Seq.fold (fun acc _ -> Seq.skipWhile (fun x -> x < 0) acc) [-1; 5] |> Seq.head"
                (VInt 5L)

            expectValue
                "[1..200000] |> Seq.fold (fun acc _ -> Seq.takeWhile (fun x -> x > 0) acc) (Seq.append [1; 2; 0] (Seq.initInfinite id)) |> Seq.length"
                (VInt 2L)

            expectValue "[1..200000] |> Seq.fold (fun acc _ -> Seq.distinct acc) [1; 1; 2] |> Seq.length" (VInt 2L)
            expectValue "[1..200000] |> Seq.fold (fun acc _ -> Seq.skip 0 acc) [5] |> Seq.head" (VInt 5L)
        }
        test "a mixed 200k-deep where/choose/map/append chain enumerates [D:seq-depth-iterative]" {
            expectValue
                "[1..200000] |> Seq.fold (fun acc i -> match i % 4 with | 0 -> Seq.where (fun x -> x % 2 == 1) acc | 1 -> Seq.choose (fun x -> Some x) acc | 2 -> Seq.map (fun x -> x + 2) acc | _ -> if i > 199990 then Seq.append acc [i] else Seq.append acc []) [] |> Seq.map (fun x -> $\"{x}\") |> Str.join \",\""
                (VStr "199995,199997,199999")
        }
        test "chain stages keep per-enumeration state and F#'s pull counts [D:seq-depth-iterative]" {
            // take stops without pulling past its last element
            expectValue "(Seq.initInfinite id) |> Seq.map (fun x -> 10 / (2 - x)) |> Seq.take 2 |> Seq.sum" (VInt 15L)
            // stateful stages restart on a second enumeration
            expectValue "let xs = [1; 1; 2] |> Seq.distinct |> Seq.indexed in Seq.length xs + Seq.length xs" (VInt 4L)

            expectValue
                "[1; 2; 3] |> Seq.scan (fun a x -> a + x) 0 |> Seq.append [9] |> Seq.map (fun x -> $\"{x}\") |> Str.join \",\""
                (VStr "9,0,1,3,6")

            expectValue
                "[1..5] |> Seq.chunkBySize 2 |> Seq.map Seq.sum |> Seq.map (fun x -> $\"{x}\") |> Str.join \",\""
                (VStr "3,7,5")

            expectValue
                "[1; 2; 3] |> Seq.collect (fun x -> (Seq.initInfinite id) |> Seq.take x) |> Seq.take 4 |> Seq.map (fun x -> $\"{x}\") |> Str.join \",\""
                (VStr "0,0,1,0")

            expectValue "[1; 2; 3; 2] |> Seq.pairwise |> Seq.length" (VInt 3L)
        }
        test "bare choose resolves — single-home means bare [D:bare-partition]" {
            Expect.equal (run "[1] |> choose (fun x -> Some x) |> Seq.length") (VInt 1L) ""
        }
        test "bare append resolves — single-home means bare [D:bare-partition]" {
            Expect.equal (run "[1] |> append [2] |> Seq.length") (VInt 2L) ""
        }
        test "non-Option chooser rejects at check" {
            let terr = checkErr "[1] |> Seq.choose (fun x -> x)"
            Expect.stringContains terr.Message "Option" ""
        }
    ]

let envSugarTests =
    // Env.need / Env.getOr [D:env-need-getor] and the inline anonymous
    // type in Env.load / Args.load [D:env-inline-type]
    let parseErr (src: string) =
        match Weir.Parser.parseStmt src with
        | Error msg -> msg
        | Ok s -> failtest $"expected a parse rejection, got {s}"

    let withVar (name: string) (value: string) (f: unit -> unit) =
        System.Environment.SetEnvironmentVariable(name, value)

        try
            f ()
        finally
            System.Environment.SetEnvironmentVariable(name, null)

    // testSequenced: the overlay cases push the shared root overlay (see
    // withinEnvPairsTests)
    testSequenced
    <| testList "env sugar" [
        test "Env.need reads a set var and raises naming an unset one [D:env-need-getor]" {
            withVar "WEIR_T_NEED" "tok" (fun () -> expectValue "Env.need \"WEIR_T_NEED\"" (VStr "tok"))
            Expect.equal (msgOf "Env.need \"WEIR_T_NEED_ABSENT\"") "Env.need: WEIR_T_NEED_ABSENT is not set" ""
        }
        test "Env.getOr gives the default only when unset [D:env-need-getor]" {
            expectValue "Env.getOr \"WEIR_T_GETOR_ABSENT\" \"8080\"" (VStr "8080")
            withVar "WEIR_T_GETOR" "9" (fun () -> expectValue "Env.getOr \"WEIR_T_GETOR\" \"8080\"" (VStr "9"))
        }
        test "set-but-empty is set: need and getOr both return \"\" [D:env-need-getor]" {
            // .NET's SetEnvironmentVariable(n, "") UNSETS, so the empty
            // value rides the overlay — the same read path Session.getEnv owns
            let pin = "[Env.pair \"WEIR_T_EMPTY\" \"\"]"
            expectValue $"within env {pin} Env.need \"WEIR_T_EMPTY\"" (VStr "")
            expectValue $"within env {pin} Env.getOr \"WEIR_T_EMPTY\" \"d\"" (VStr "")
        }
        test "both see the within env overlay [D:env-overlay-read]" {
            let pin = "[Env.pair \"WEIR_T_OVL2\" \"bar\"]"
            expectValue $"within env {pin} Env.need \"WEIR_T_OVL2\"" (VStr "bar")
            expectValue $"within env {pin} Env.getOr \"WEIR_T_OVL2\" \"d\"" (VStr "bar")
            expectValue "Env.getOr \"WEIR_T_OVL2\" \"d\"" (VStr "d")
        }
        test "both are string-typed and read the environment (pure refuses)" {
            Expect.equal (checkOk "Env.need \"X\"").Ty TStr ""
            Expect.equal (checkOk "Env.getOr \"X\" \"d\"").Ty TStr ""

            mustSayIn
                "es.weir"
                [ "let x ="; "    pure"; "        Env.need \"HOME\"" ]
                "'Env.need' reads the environment"
                "purity classifies it as an env read"
        }
        test "Env.load takes an inline anonymous type [D:env-inline-type]" {
            withVar "WEIR_T_PORT" "8081" (fun () ->
                expectValue
                    "let e = Env.load {| WEIR_T_PORT: int; WEIR_T_CI: Option<string> |} in $\"{e.WEIR_T_PORT} {e.WEIR_T_CI}\""
                    (VStr "8081 None"))

            cleanIn
                "ei.weir"
                [
                    "let env = Env.load {| HOME: string; PORT: int; CI: Option<string> |}"
                    "print $\"{env.HOME} {env.PORT} {env.CI}\""
                ]
                "the plan's example checks"
        }
        test "the inline form reports missing vars like the named form [D:env-inline-type]" {
            let m = msgOf "Env.load {| WEIR_T_MISS_A: int; WEIR_T_MISS_B: string |}"
            Expect.stringContains m "WEIR_T_MISS_A" "every missing var in one error"
            Expect.stringContains m "WEIR_T_MISS_B" ""
        }
        test "the inline form's field law matches the named form's message [D:env-inline-type]" {
            let named =
                errorMsgsIn "en.weir" [ "type C = { xs: seq<string> }"; "let c = Env.load C"; "print (show c)" ]

            let inline' =
                errorMsgsIn "ea.weir" [ "let c = Env.load {| xs: seq<string> |}"; "print (show c)" ]

            Expect.equal inline' named "same validation, same message"
            Expect.stringContains (List.head inline') "Env.load fields must be" ""
        }
        test "Args.load takes the inline type too [D:env-inline-type]" {
            cleanIn
                "aa.weir"
                [
                    "let cli = Args.load {| verbose: bool; out: Option<string> |}"
                    "print $\"{cli.verbose} {cli.out}\""
                ]
                "a flag record, inline"

            mustSayIn
                "ab.weir"
                [
                    "let cli = Args.load {| [<Short \"x\">] verbose: bool |}"
                    "print (show cli.verbose)"
                ]
                "field attributes need a named type"
                "attributes refuse on Args too"
        }
        test "no attributes on an anonymous type, anywhere — the teaching names a declared type [D:env-inline-type]" {
            for src in
                [
                    "let e = Env.load {| [<Default 3>] PORT: int |}"
                    "let x = [\"{}\"] |> from json {| [<Default 3>] a: int |}"
                ] do
                let m = parseErr src
                Expect.stringContains m "field attributes need a named type" src
                Expect.stringContains m "type T = {" "names the repair"
        }
        test
            "a value literal in the slot teaches the ':' spelling; elsewhere ':' still teaches the literal [D:env-inline-type]" {
            Expect.stringContains (checkErr "Env.load {| HOME = \"x\" |}").Message "Env.load {| PORT: int |}" ""

            Expect.stringContains
                (parseErr "let x = {| a: int |}")
                "spells the anonymous TYPE"
                "outside the slot, unchanged"
        }
    ]

let moduleErrorOnceTests =
    testList "a shared module's error is reported once [D:module-error-once]" [
        test "a diamond, a re-alias and a symlink report one error and one imported-here note" {
            withTempDir "weir-modonce" (fun dir ->
                let w (n: string) (ls: string list) =
                    System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, n), ls)

                w "shared.weir" [ "module Shared"; "let v : int"; "let v = \"x\"" ]
                w "left.weir" [ "module Left"; "import \"./shared.weir\" as S"; "let s : int"; "let s = 1" ]

                System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(dir, "link.weir"), "shared.weir")
                |> ignore

                let entry = System.IO.Path.Combine(dir, "main.weir")

                let script = [
                    "import \"./left.weir\" as L"
                    "import \"./shared.weir\" as S1"
                    "import \"./shared.weir\" as S2"
                    "import \"./link.weir\" as S3"
                    "print (show L.s)"
                ]

                System.IO.File.WriteAllLines(entry, script)
                let ds = diagsIn entry script
                let errs = ds |> List.filter (fun d -> d.Severity = "error")

                Expect.equal
                    (errs |> List.map (fun d -> System.IO.Path.GetFileName d.File, d.Line))
                    [ "shared.weir", 3 ]
                    "one error, at the real file"

                Expect.equal (ds |> List.filter (fun d -> d.Code = "imported-here") |> List.length) 1 "one note")
        }
    ]

let optionSweepTests =
    testList "Option sweep" [
        test "Seq.tryHead types as Option of the element" {
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
                let ds = diagsIn "pin.weir" lines

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
                let ds = diagsIn "pin.weir" lines

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
            // teaching wins its spot — pin the caret and the absence of
            // the raw expecting-list (the burial is the bug).
            let sole (line: string) =
                let ds = diagsIn "pin.weir" [ line ]

                match ds |> List.filter (fun d -> d.Severity = "error") with
                | [ d ] -> d.Line, d.Col, d.Message
                | other -> failtest $"expected ONE error, got {other}"

            let clean (teach: string) (line: string) expectedCol =
                let l, c, msg = sole line
                Expect.equal (l, c) (1, expectedCol) $"caret for: {line}"
                Expect.stringContains msg teach $"teaching present: {line}"
                // both burial markers — pinning one leaves the other free
                Expect.isFalse (msg.Contains "Expecting:") $"no expecting-list: {line} -> {msg}"
                Expect.isFalse (msg.Contains "Other error messages") $"not buried: {line} -> {msg}"

            clean "a splat cannot head a command" "$@xs foo" 1
            clean "a splice cannot join a word" "echo --flag=$x" 13
            clean "a splat cannot join a word" "echo a$@x" 7
            clean "'function' is a keyword" "let function = 1" 5
            clean "'rec' is a keyword" "let rec = 1" 5
            clean "'mutable' is a keyword" "let mutable = 1" 5
            // B: keyword in the param and record-decl field slots
            clean "'rec' is a keyword" "let f rec = 1" 7
            clean "'when' is a keyword" "let f when = 1" 7
            clean "'let' is a keyword" "type T = { let: int }" 12
            // record-literal field name: a guard before the arm-commit check
            clean "'let' is a keyword" "let r = { let = 1 }" 11
            clean "'in' is a keyword" "let r = { in = 1 }" 11
            // A: foldChain reifier anchors on the marker, not the chain end
            clean "a pipeline cannot" "git | grep x | exec" 16

            // gated: every keyword must still fall through to its parser —
            // the risk matrix (heads of their own constructs)
            let okParses (line: string) =
                let ds = diagsIn "pin.weir" [ line ]

                Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error" && d.Code = "parse")) $"parses: {line}"

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
            // dominates outside its own attempt, so a match arm (past its
            // `|`), a lambda (past `fun`), and a param all surface the
            // teaching. let-destructure stays a finding (SLetPat's attempt).
            let sole (line: string) =
                let ds = diagsIn "pin.weir" [ line ]

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
                let ds = diagsIn "pin.weir" [ line ]
                Expect.isEmpty (ds |> List.filter (fun d -> d.Severity = "error")) $"parses: {line}"

            okParses "let _z = match Some 1 with | Some n -> n | _ -> 0"
            okParses "let (_a, _b) = (1, 2)"
            okParses "let _z = match 1 with | _ -> 0"
            okParses "let _z = match 5 with | n when n > 0 -> 1 | _ -> 0"
            okParses "let _z = match true with | true -> 1 | false -> 0"
            okParses "let _z = match [1] with | [x] -> x | _ -> 0"
            // the binder-scan skips pattern delimiters and stops at `=`:
            // an RHS keyword is not a binder keyword, and true/false in a
            // destructure are literal patterns (a check error, not parse)
            okParses "let _go = (let (a, _b) = (1, 2) in a)"

            let noParseError (line: string) =
                let ds = diagsIn "pin.weir" [ line ]

                Expect.isEmpty
                    (ds |> List.filter (fun d -> d.Severity = "error" && d.Code = "parse"))
                    $"no PARSE error (check is fine): {line}"

            noParseError "let (true, y) = (true, 2) in y" // refutable: a CHECK error, not parse
        }
        test "neg-int overflow dominates; the risk surface is byte-identical [D:anchor-before-read]" {
            // C of the anchor residue: the fix narrows negAtom's attempt so
            // the operand's out-of-range fatal escapes instead of being
            // swallowed and merged (a fatal inside an attempt is not a
            // fatal). Corrected diagnosis: not parsed-twice (negIntLit is
            // range-only) — negAtom's own attempt was the swallower.
            let ds = diagsIn "pin.weir" [ "let x = -99999999999999999999" ]

            match ds |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (1, 10) "on the offending digits"
                Expect.stringContains d.Message "out of range" "the teaching"
                Expect.isFalse (d.Message.Contains "Expecting:") "not buried"
                Expect.isFalse (d.Message.Contains "Other error messages") "not buried"
            | other -> failtest $"expected ONE error, got {other}"

            // the risk surface — prefix minus, subtraction, spaced range
            // step, and application-vs-subtraction — unchanged
            expectValue "let a = 5 in a - 1" (VInt 4L)
            expectValue "let a = 5 in a-1" (VInt 4L)
            expectValue "-5" (VInt -5L)
            expectValue "[10.. -1 ..8] |> Seq.length" (VInt 3L)
            expectValue "let f x = x + 100 in f -1" (VInt 99L)
        }
        test "value-headed pipeline: external head feeds; library head keeps the hint [D:value-headed-pipe]" {
            // resolution decides — an external head after a value `|`
            // desugars to a pipe into the command (stdin), reusing the
            // EPipe-into-ECmd machinery (identical to feed)
            match Weir.Parser.parseLine cmdResolver "[\"a\"] | cat" with
            | Ok(SExpr e | SCmd e) ->
                Expect.stringContains (Weir.Ast.sexpr e) "(cmd cat)" "pipes the value into the command"
            | other -> failtest $"expected the value-headed pipe, got {other}"

            match Weir.Parser.parseLine cmdResolver "[\"a\"] | grep x | cat" with
            | Ok(SExpr e | SCmd e) -> Expect.stringContains (Weir.Ast.sexpr e) "(cmd cat)" "multi-external chains fold"
            | other -> failtest $"expected the chain, got {other}"

            // a library/known head keeps the barePipeHint (not value-headed)
            match Weir.Parser.parseLine cmdResolver "[1] | Seq.head" with
            | Error msg -> Expect.stringContains msg "'|' chains commands" "library head keeps the hint"
            | Ok _ -> failtest "expected the hint"
        }
        test "a body-line pipe belongs to the body statement [D:within-tail-pipe]" {
            // the inline `| cmd` on a within block's tail line stays
            // inside the scope — it parsed as `(within …) | cmd`, the
            // command spawning after the scope restored
            match Weir.Parser.parseLine cmdResolver "within cd \"/d\" [\"x\"] | cat" with
            | Ok(SExpr e | SCmd e) ->
                Expect.equal (Weir.Ast.sexpr e) "(within cd \"/d\" ([\"x\"] |> (cmd cat)))" "the pipe rides the body"
            | other -> failtest $"expected the scoped pipe, got {other}"

            // a non-final value-headed pipe is a body statement too (was
            // a bare parse error at the sibling boundary) — and armSeq
            // arms it exactly like a command-headed chain
            let joined =
                match
                    Weir.Assemble.assemble [ 1, "within cd \"/d\""; 2, "    [\"x\"] | cat"; 3, "    print \"after\"" ]
                with
                | Ok [ ll ] -> ll.Text
                | other -> failtest $"expected one logical line, got {other}"

            match Weir.Parser.parseLine cmdResolver joined with
            | Ok(SExpr e | SCmd e) ->
                Expect.equal
                    (Weir.Ast.sexpr e)
                    "(within cd \"/d\" (seq (([\"x\"] |> (cmd cat)) |> |print) (print \"after\")))"
                    "the mid-body pipe arms in place"
            | other -> failtest $"expected the armed body, got {other}"
        }
        test "a dedented pipe below a within block closes it [D:within-tail-pipe]" {
            // the offside face: at (or left of) the head column the pipe
            // takes the whole scope — the assembler wraps `(within …)`,
            // the match-close shape [D:match-pipe-offside]
            let dedentBar =
                match Weir.Assemble.assemble [ 1, "within cd \"/d\""; 2, "    [\"x\"]"; 3, "| cat" ] with
                | Ok [ ll ] -> ll.Text
                | other -> failtest $"expected one logical line, got {other}"

            Expect.equal dedentBar "(within cd \"/d\" [\"x\"]) | cat" "the dedented | wraps the scope"

            let dedentFwd =
                match Weir.Assemble.assemble [ 1, "within cd \"/d\""; 2, "    [\"x\"]"; 3, "|> Seq.length" ] with
                | Ok [ ll ] -> ll.Text
                | other -> failtest $"expected one logical line, got {other}"

            Expect.equal dedentFwd "(within cd \"/d\" [\"x\"]) |> Seq.length" "the dedented |> wraps the scope"

            // a pipe at body indent extends the body statement (no wrap)
            let bodyPipe =
                match Weir.Assemble.assemble [ 1, "within cd \"/d\""; 2, "    [\"x\"]"; 3, "    | cat" ] with
                | Ok [ ll ] -> ll.Text
                | other -> failtest $"expected one logical line, got {other}"

            Expect.equal bodyPipe "within cd \"/d\" [\"x\"] | cat" "a body-indent pipe stays inside"
        }
        test "retired names teach their replacements [D:seq-force]" {
            Expect.stringContains (checkErr "[1] |> Seq.toList").Message "'Seq.freeze' is the materializer" ""
            Expect.stringContains (checkErr "[1] |> toList").Message "'freeze' is the materializer" ""
            Expect.stringContains (checkErr "None |> Option.defaultTo 1").Message "Option.defaultValue" ""

            // the rename teach [D:freeze-rename]: a stray old spelling
            // names the new one at both lookup sites (edit distance is
            // 4, so the generic did-you-mean could never carry this)
            Expect.stringContains (checkErr "[1] |> Seq.force").Message "renamed 'Seq.freeze'" ""
            Expect.stringContains (checkErr "[1] |> force").Message "renamed 'freeze'" ""

            // the collect reservation paid out [D:seq-gaps]: the member
            // exists with F#'s semantics and no retirement text remains
            Expect.isFalse ((checkOk "[1] |> Seq.collect (fun x -> [x])").Ty = TUnit) "Seq.collect is a live member"
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
        test "Option.bind chains Option-returning steps" {
            expectValue "Some 3 |> Option.bind (fun x -> Some (x + 1))" (VUnion("Some", Some(VInt 4L)))
            expectValue "None |> Option.bind (fun x -> Some (x + 1))" (VUnion("None", None))
        }
        test "Option.flatten collapses a nested Option" {
            expectValue "Option.flatten (Some (Some 5))" (VUnion("Some", Some(VInt 5L)))
            expectValue "Option.flatten (Some None)" (VUnion("None", None))
            expectValue "Option.flatten None" (VUnion("None", None))
        }
        test "Frontier.fold: a diamond+cycle folds each node ONCE, FIFO breadth-first [D:structural-walk]" {
            // 1 -> {2, 3}; 2 -> 4; 3 -> 4; 4 -> 1 (a cycle): the visited
            // set makes the sum each-once and the order pin is the FIFO law
            let graph =
                "(fun acc n -> (acc + show n, if n == 1 then [2; 3] elif n == 2 then [4] elif n == 3 then [4] else [1]))"

            expectValue $"[1] |> Frontier.fold (fun n -> show n) \"\" {graph}" (VStr "1234")
        }
        test "Frontier.fold: a \"\" key opts the node out of dedup (the tree convention) [D:structural-walk]" {
            expectValue "[1; 1] |> Frontier.fold (fun n -> \"\") 0 (fun acc n -> (acc + n, []))" (VInt 2L)
        }
        test "Frontier.fold: the step budget turns a non-finite walk into a teaching error [D:structural-walk]" {
            let ex =
                Expect.throwsC
                    (fun () ->
                        run "[1] |> Frontier.fold (fun n -> show n) 0 (fun acc n -> (acc, [n + 1]))"
                        |> ignore)
                    id

            Expect.stringContains ex.Message "exceeded 100000 steps" "bounded, never a hang"
        }
        test "Frontier.fold: a step that is not (acc, children) rejects at check [D:structural-walk]" {
            Expect.stringContains
                (checkErr "[1] |> Frontier.fold (fun n -> show n) 0 (fun acc n -> acc + n)").Message
                "expected int * seq<int>, got int"
                ""
        }
        test "Graph.reach: reachable nodes, cycle-safe, each once, breadth-first [D:structural-walk]" {
            expectValue
                "Graph.reach (fun n -> show n) (fun n -> if n == 1 then [2; 3] elif n == 2 then [4] elif n == 3 then [4] else [1]) 1"
                (VSeq [ VInt 1L; VInt 2L; VInt 3L; VInt 4L ])
        }
        test "a '()' lambda parameter is unit in argument position too" {
            Expect.stringContains
                (checkErr "[1; 2] |> Seq.map (fun () -> \"X\")").Message
                "expected int, got unit"
                "the pushed domain meets unit"

            Expect.stringContains
                (checkErr "[1; 2] |> Seq.fold (fun acc () -> acc) 0").Message
                "expected int, got unit"
                "a nested parameter too"

            expectValue "[(); ()] |> Seq.map (fun () -> 1)" (VSeq [ VInt 1L; VInt 1L ])
            expectValue "Option.defaultWith (fun () -> 3) None" (VInt 3L)
        }
        test "Tree.walk: unit-valued, parent before children; a non-seq step rejects at check [D:structural-walk]" {
            expectValue "1 |> Tree.walk (fun n -> if n < 3 then [n + 1] else [])" VUnit
            Expect.stringContains (checkErr "1 |> Tree.walk (fun n -> n)").Message "expected seq<int>, got int" ""
        }
        test "Tree.fold: children before their parent, results in child order [D:tree-fold]" {
            expectValue
                "Tree.fold (fun n -> if n < 4 then [n * 2; n * 2 + 1] else []) (fun n rs -> n + Seq.sum rs) 1"
                (VInt 28L)

            expectValue
                "Tree.fold (fun n -> if n < 3 then [n + 1; n + 1] else []) (fun n rs -> $\"({n}{Str.join \"\" rs})\") 1"
                (VStr "(1(2(3)(3))(2(3)(3)))")

            expectValue "Tree.fold (fun n -> []) (fun n rs -> n + Seq.length rs) 7" (VInt 7L)
        }
        test "Tree.fold: depth costs no host stack; an endless tree meets the budget [D:tree-fold]" {
            expectValue
                "Tree.fold (fun n -> if n < 50000 then [n + 1] else []) (fun n rs -> 1 + Seq.sum rs) 1"
                (VInt 50000L)

            let ex =
                Expect.throwsC (fun () -> run "Tree.fold (fun n -> [n + 1]) (fun n rs -> 1) 1" |> ignore) id

            Expect.stringContains ex.Message "Tree.fold: exceeded 100000 nodes" "bounded, never a hang"

            Expect.stringContains
                (checkErr "Tree.fold (fun n -> [n]) (fun n rs -> rs) 1").Message
                "infinite type"
                "combine returns one result, not the children's seq"
        }
        test "Seq.tryFind is data-last and Option-returning" {
            expectValue "ls |> Seq.tryFind _.hidden |> Option.map _.name |> Option.defaultValue \"none\"" (VStr "b.bin")

            expectValue
                "ls |> Seq.tryFind (fun f -> f.bytes > 999999999B) |> Option.map _.name |> Option.defaultValue \"none\""
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
        // [D:str-code-points]: an astral char is one position everywhere
        test "strings count code points, never split a surrogate pair" {
            expectValue "Str.length \"a😀b\"" (VInt 3)
            expectValue "Str.sub 0 2 \"a😀b\"" (VStr "a😀")
            expectValue "Str.sub 1 1 \"a😀b\"" (VStr "😀")
            expectValue "\"a😀b\"[0..1]" (VStr "a😀")
            expectValue "\"a😀b\"[^2..]" (VStr "😀b")
            expectValue "Str.tryIndexOf \"b\" \"😀😀b\"" (VUnion("Some", Some(VInt 2)))
            expectValue "Str.padLeft 3 \"😀\"" (VStr "  😀")
            expectValue "Str.padRight 3 \"😀\"" (VStr "😀  ")
            let ex = Expect.throwsC (fun () -> run "Str.sub 0 4 \"a😀b\"" |> ignore) id
            Expect.stringContains ex.Message "string length 3" ""
        }
        test "a long string's code-point index agrees with the walk [D:str-code-points]" {
            // the reference: positions by a plain walk, a pair one position
            let walk (s: string) = [|
                let mutable i = 0

                while i < s.Length do
                    yield i
                    i <- i + (if System.Char.IsSurrogatePair(s, i) then 2 else 1)

                yield s.Length
            |]

            let lone = string (char 0xD800)

            for s in
                [
                    String.replicate 1000 "a"
                    String.replicate 300 "a😀"
                    String.replicate 130 "😀" + lone + String.replicate 200 "b"
                    String.replicate 64 "a" + "😀" + String.replicate 400 "c"
                ] do
                let offs = walk s
                let n = offs.Length - 1
                Expect.equal (CodePoints.length s) n $"length of a {s.Length}-unit string"

                for cp in [ 0; 1; 63; 64; 65; 127; 128; n - 1; n; n + 5 ] do
                    let want = offs[min (max cp 0) n]
                    Expect.equal (CodePoints.offset s cp) want $"offset {cp}"

                for cp in [ 0; 64; n - 1 ] do
                    Expect.equal (CodePoints.ofOffset s offs[cp]) cp $"ofOffset at code point {cp}"

                Expect.equal (CodePoints.sub s (n - 1) 1) (s.Substring(offs[n - 1])) "the last code point"

            expectValue "Str.sub 599 1 (Str.replicate 300 \"a😀\")" (VStr "😀")
            expectValue "(Str.replicate 300 \"a😀\")[^1..]" (VStr "😀")
            expectValue "Str.length (Str.replicate 300 \"a😀\")" (VInt 600)
        }
        test "a long string with no surrogate adds no index entry [D:str-cp-plain]" {
            let before = CodePoints.indexedForTest ()

            for i in 1..2000 do
                let s = string i + String.replicate 300 "a"
                Expect.equal (CodePoints.length s) s.Length "a plain length is its UTF-16 length"
                Expect.equal (CodePoints.sub s 3 2) (s.Substring(3, 2)) "a plain cut is a UTF-16 cut"
                Expect.equal (CodePoints.ofOffset s 7) 7 "a plain position is its offset"

            // parallel suites may index a few strings of their own
            Expect.isLessThan (CodePoints.indexedForTest () - before) 100 "2000 plain strings left the table alone"

            // the per-thread memo never answers for another string
            let plain = String.replicate 400 "b"
            let astral = String.replicate 300 "😀"

            for _ in 1..3 do
                Expect.equal (CodePoints.length plain) 400 "the plain string"
                Expect.equal (CodePoints.length astral) 300 "the astral string, between plain lookups"
                Expect.equal (CodePoints.sub astral 299 1) "😀" "its index still cuts on a pair"
        }
        test "match on Seq.tryHead pins the full idiom" {
            expectValue "match ls |> Seq.tryHead with | Some f -> f.name | None -> \"empty\"" (VStr "a.txt")
        }
    ]

let interpTests =
    testList "String interpolation" [
        test "parses literal and hole parts" { expectParse "$\"a{1 + 1}b\"" "(interp \"a\"{(+ 1 1)}\"b\")" }
        test "empty string" { expectValue "$\"\"" (VStr "") }
        test "brace escapes" { expectValue "$\"{{x}}\"" (VStr "{x}") }
        test "int hole renders digits" { expectValue "$\"tracked: {[1; 2; 3] |> Seq.length}\"" (VStr "tracked: 3") }
        test "bool hole renders true/false" { expectValue "$\"flag: {1 == 1}\"" (VStr "flag: true") }
        test "string literal inside a hole" { expectValue "$\"a{\"b\"}c\"" (VStr "abc") }
        test "let-bound hole" { expectValue "let n = [1; 2] |> Seq.length in $\"n is {n}\"" (VStr "n is 2") }
        test "an unresolved hole generalizes [D:hole-generic]" {
            let te = checkOk "fun x -> $\"v={x}\""

            match te.Ty with
            | TFun(TVar _, TStr) -> ()
            | t -> failtest $"expected 'a -> string, got {formatTy t}"
        }
        test "a function still rejects in a hole [D:interp-show]" {
            let terr = checkErr "$\"{Str.trim}\""
            Expect.stringContains (formatError terr) "interpolation holes render what show renders" "names the law"
            Expect.stringContains (formatError terr) "functions never render" ""
        }
        test "command argument stays one argv entry" {
            expectCmd "grep $\"n={1 + 1}\" f" "(cmd grep (interp \"n=\"{(+ 1 1)}) \"f\")"
        }
    ]

let showTests =
    testList "show" [
        test "records render REPL-shaped" {
            expectValue
                "show (ls |> Seq.head)"
                (VStr
                    "{ name = \"a.txt\"; kind = Regular; target = None; bytes = 0 B; modified = 1970-01-01T00:00:00Z; hidden = false; path = \"a.txt\" }")
        }
        test "unions, seqs, scalars" {
            expectValue "show (Some 3)" (VStr "Some 3")
            expectValue "show (Some (1, 2))" (VStr "Some (1, 2)")
            expectValue "show (Some [1])" (VStr "Some ([1])")
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

            // a mixed tuple: the int component must not carry it
            // (mutation-spike survivor — the Eq twin was already pinned)
            let tupled = checkErr "show (1, (fun x -> x + 1))"
            Expect.stringContains (formatError tupled) "cannot render functions" "mixed tuple"
        }
        test "bare value stays generic with Show riding (was: defaulted to string)" {
            // sentinel-era show defaulted its bare-value type to string;
            // the constrained scheme (Session B) keeps it genuinely
            // generic — the element type resolves from data, and Show
            // rides until it does
            expectValue "(Seq.initInfinite id) |> take 2 |> Seq.map show |> Seq.freeze |> Seq.length" (VInt 2L)

            match (checkOk "Seq.map show").Ty with
            | TFun(TSeq(TVar _), TSeq TStr) -> ()
            | ty -> failtest $"expected generic Show mapping, got {formatTy ty}"
        }
        test "show is reserved — the shadow pin FLIPPED [D:reserve-builtins]" {
            Expect.stringContains
                (checkErr "let show = fun x -> x in show 9").Message
                "'show' is a builtin with no qualified spelling"
                "no way back, so no binding"
        }
    ]

let parallelTests =
    testSequenced
    <| testList "Data parallelism" [
        test "pmap preserves input order" {
            expectValue "[3; 1; 2] |> Seq.pmap (fun x -> x * 10)" (VSeq [ VInt 30L; VInt 10L; VInt 20L ])
        }
        test "pmap is eager and reusable" {
            expectValue "let r = [1; 2] |> Seq.pmap (fun x -> x + 1) in (r |> Seq.sum) + (r |> Seq.sum)" (VInt 10L)
        }
        test "piter runs every element and returns unit" {
            expectValue "[1; 2; 3] |> Seq.piter (fun n -> if n > 99 then print \"never\")" VUnit
        }
        test "cd inside a worker is worker-local (forked session)" {
            skipOnWindows ()
            let before = Weir.Session.Cwd()

            expectValue "[\"/\"; \"/tmp\"] |> Seq.pmap (fun d -> let x = cd d in pwd)" (VSeq [ VStr "/"; VStr "/tmp" ])

            Expect.equal (Weir.Session.Cwd()) before "parent session untouched after the join"
        }
        test "worker failure surfaces as the first error" {
            Expect.throws (fun () -> run "[1; 0] |> Seq.pmap (fun x -> 10 / x)" |> ignore) "div by zero"
        }
    ]

let seqAccessTests =
    testList "Seq access family and Args" [
        test "contains, exists, forall" {
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
            Expect.throws (fun () -> run "[1] |> Seq.skip 3 |> Seq.freeze" |> ignore) "F#-faithful raise"
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
        }
    ]

let closersTests =
    testList "Closers (the repair scanner)" [
        test "brackets, strings, interp holes at any nesting" {
            Expect.equal (Weir.Assemble.closers "f (a") ")" ""
            Expect.equal (Weir.Assemble.closers "{ A = (1") ")}" ""
            Expect.equal (Weir.Assemble.closers "let x = \"open") "\"" ""
            Expect.equal (Weir.Assemble.closers "printerr $\"q: {t") "}\"" ""
            Expect.equal (Weir.Assemble.closers "$\"a{f (\"s\"") ")}\"" ""
            Expect.equal (Weir.Assemble.closers "$\"esc {{ literal") "\"" ""
            Expect.equal (Weir.Assemble.closers "balanced (x) done") "" ""
        }
    ]

let binderTests =
    testList "Pattern binders & bare comma" [
        test "all six form-examples (the plan's forms block)" {
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
                [
                    "let (Some x) = Some 1 in x"
                    "let 1, y = 1, 2 in y"
                    "let (true, y) = (true, 2) in y"
                ] do
                Expect.stringContains (checkErr bad).Message "this pattern can fail; use match" bad
        }
        test "a single-case union's constructor destructures — params, lets, lambdas, nested" {
            let w = "type W = W of int"
            let b = "type Box<'a> = Box of 'a"

            for lines in
                [
                    [ w; "let un (W x) = x"; "print $\"{un (W 3)}\"" ]
                    [ w; "let (W y) = W 4"; "print $\"{y}\"" ]
                    [ w; "let f = fun (W z) -> z + 1"; "print $\"{f (W 5)}\"" ]
                    [ b; "let un (Box x) = x"; "print $\"{un (Box 3)} {un (Box \"s\")}\"" ]
                    [ b; "let ((Box a), c) = (Box 1, 2)"; "print $\"{a + c}\"" ]
                ] do
                checkEqRun lines

            // two cases can fail
            let e =
                firstErrorIn "two.weir" [ "type V = A of int | B"; "let un (A x) = x"; "print $\"{un (A 1)}\"" ]

            Expect.stringContains e.Message "this pattern can fail; use match" "two cases"
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
            // `;` looser than `,`: the seq's first element is the tuple —
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
                    match Weir.Statements.discardError te.Ty with
                    | Some msg -> Expect.stringContains msg "discards" ""
                    | None -> failtest "tuple statement must hit the discard rule"
                | Error terr -> failtest (formatError terr)
            | other -> failtest $"unexpected: {other}"
        }
        test "assembler: let ( and let x, open block lets (classifier pin)" {
            match Weir.Assemble.assemble [ 1, "let (a, b) ="; 2, "    (1, 2)"; 3, "a" ] with
            | Ok [ ll; _ ] -> Expect.equal ll.Text "let (a, b) = (1, 2)" ""
            | other -> failtest $"unexpected: {other}"
        }
    ]

let tupleTests =
    testList "Tuples (the reversal)" [
        test "literal, type, and pattern round-trip" {
            expectValue "let p = (1, \"two\") in match p with | (n, s) -> $\"{n}-{s}\"" (VStr "1-two")
        }
        test "bare commas in match: scrutinee, arm pattern, guard outside [D:bare-comma]" {
            // the showcase's finding, closed: one rule, four positions
            expectValue "match Some 1, 2 with | Some d, _ -> d | _ -> 0" (VInt 1L)
            expectValue "match 1, 2 with | a, b when a < b -> \"lt\" | _ -> \"ge\"" (VStr "lt")
        }
        test "the imported precedence footgun is UNCHANGED: f a, b is (f a), b" {
            expectValue "let f = fun x -> x * 10 in f 1, 2" (VTuple [ VInt 10L; VInt 2L ])
        }
        test "a comma inside a list is a tuple element ([a, b] is one element)" {
            expectValue "[1, 2] |> Seq.length" (VInt 1L)
        }
        test "arm-body binders are KNOWN at parse — never phantom commands [D:interior-arming]" {
            // `| t :: _ -> t` read t as an external under the assume
            // resolver until arm bodies got the bindings-beat-PATH
            // extension lambda params always had (found by the corpus
            // sweep on repo-report.weir)
            let e = parse "match [\"a\"] with | t :: _ -> t | _ -> \"d\""

            match Weir.Check.typecheck env e with
            | Ok te -> Expect.equal te.Ty TStr "the binder, not a phantom command"
            | Error terr -> failtest (formatError terr)
        }
        test "the bare for-binder already rode commaPats (confirmed, pinned)" {
            runReal "for k, v in [(\"a\", 1)] do print $\"{k}={v}\"" |> ignore
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
            // Shift/Stop, not Move — Move/Copy are now prelude Op ctors
            // [D:plan-apply] (like Some/Get, they occupy the bare
            // namespace, so a user union spelling one collides
            // [D:ambiguous-ctor])
            let e2 = env |> declare "type Msg = | Shift of int * int | Stop"

            let expr = parse "match Shift (3, 4) with | Shift (x, y) -> x + y | Stop -> 0"

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
        test "tuples x exhaustiveness: int literal components never complete (per-component)" {
            let terr = checkErr "match (1, 2) with | (0, _) -> \"z\" | (1, _) -> \"o\""
            Expect.stringContains (formatError terr) "missing: (_, _)" ""

            expectValue "match (1, 2) with | (0, _) -> \"z\" | _ -> \"other\"" (VStr "other")
        }
        test "tuple coverage composes per component (starship port) [D:exhaustiveness-hard-error]" {
            // the matrix walk: every Option x Option / bool x bool case
            // listed is total, no catch-all owed
            errorsCleanIn
                "tup.weir"
                [
                    "let f a b ="
                    "    match (a, b) with"
                    "    | Some x, Some y -> x + y"
                    "    | Some x, None -> x"
                    "    | None, Some y -> y"
                    "    | None, None -> 0"
                    "print (show (f (Some 1) None))"
                ]
                "Option<int> * Option<int>"

            errorsCleanIn
                "tup5.weir"
                [
                    "let a = true"
                    "let b = false"
                    "let r ="
                    "    match (a, b) with"
                    "    | (true, true) -> 1"
                    "    | (true, false) -> 2"
                    "    | (false, true) -> 3"
                    "    | (false, false) -> 4"
                    "print (show r)"
                ]
                "bool * bool"

            expectValue "match (false, true) with | (true, _) -> 1 | (false, true) -> 2 | (false, false) -> 3" (VInt 2L)
            // nested through a payload's tuple, and a wildcard column
            expectValue
                "match (Some (1, true), 0) with | (Some (_, true), _) -> 1 | (Some (_, false), _) -> 2 | (None, _) -> 3"
                (VInt 1L)
        }
        test "a tuple gap names one missing tuple pattern" {
            let msg (src: string) = (checkErr src).Message

            Expect.stringContains
                (msg "match (Some 1, Some 2) with | (Some x, Some y) -> x | (None, None) -> 0 | (Some x, None) -> x")
                "missing: (None, Some _)"
                "the uncovered component pair"

            Expect.stringContains
                (msg
                    "match (true, Some (1, false)) with | (true, _) -> 1 | (false, Some (_, true)) -> 2 | (false, None) -> 3")
                "missing: (false, Some (_, false))"
                "the walk recurses through a payload's tuple"

            Expect.stringContains
                (msg "match (true, false) with | (true, true) -> 1 | (false, _) -> 2 | (true, false) when false -> 3")
                "missing: (true, false)"
                "a guarded arm never covers"
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
        }
    ]

let indexerTests =
    testList "Indexers" [
        test "xs[i] desugars to Seq.item" { expectValue "[\"a\"; \"b\"][1]" (VStr "b") }
        test "chains and composes with fields and sigils" {
            expectValue "[[1; 2]; [3; 4]][1][0]" (VInt 3L)
            expectValue "(ls |> Seq.freeze)[0].name" (VStr "a.txt")
        }
        test "the whitespace rule: space means application (F# 6 dotless precedent)" {
            expectValue "Seq.sum [1; 2]" (VInt 3L)
            expectParse "f [0]" "(f [0])"
            expectParse "f[0]" "((|seqItem 0) f)"
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
        }
    ]

let fileTests =
    testSequenced
    <| testList "File module" [
        test "write, read, append roundtrip" {
            let path = weirPath (tempPath "weir-file" + ".txt")

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
            let dir = weirPath (tempPath "weir-fdir")

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
        }
        test "File.readBytesAt: a range, clamped at the end, never the whole file [D:file-readbytesat]" {
            let dir = weirPath (Directory.CreateTempSubdirectory("weir-rba").FullName)
            let path = weirPath (Path.Combine(dir, "d.bin"))
            File.WriteAllBytes(path, [| for i in 0..199 -> byte i |])

            let at (off: int) (len: int) =
                match run $"File.readBytesAt \"{path}\" (Size.bytes {off}) (Size.bytes {len})" with
                | VBytes b -> List.ofArray b
                | v -> failtest $"expected Bytes, got {formatValue v}"

            Expect.equal (at 0 4) [ 0uy; 1uy; 2uy; 3uy ] "a header"
            Expect.equal (at 100 3) [ 100uy; 101uy; 102uy ] "mid-file"
            Expect.equal (at 198 10) [ 198uy; 199uy ] "short where the file ends"
            Expect.equal (at 200 5) [] "at the end: none"
            Expect.equal (at 5000 5) [] "past the end: none, no raise"
            Expect.equal (at 7 0) [] "a zero length"
            Expect.equal (at 0 100000 |> List.length) 200 "a length past the end is the whole file"

            Expect.equal
                (run $"File.readBytesAt \"{path}\" 10B 2B |> Bytes.toHex")
                (VStr "0a0b")
                "Size literals; the result is Bytes"

            Expect.stringContains
                (Expect.throwsC (fun () -> run $"File.readBytesAt \"{path}\" (0B - 1B) 2B" |> ignore) id).Message
                "neither can be negative"
                "a negative offset"

            Expect.stringContains
                (Expect.throwsC (fun () -> run $"File.readBytesAt \"{dir}/absent\" 0B 2B" |> ignore) id).Message
                "File.readBytesAt"
                "a missing file is this member's error"

            Directory.Delete(dir, true)
        }
        // File.readFrom [D:file-readfrom]: complete lines from a byte
        // offset up to the size at the read's start, plus the next offset
        test "File.readFrom: offsets 0 and mid-file, next, partial tail, CRLF, UTF-8, BOM, edges" {
            let path = weirPath (tempPath "weir-readfrom" + ".txt")

            let chunk (off: int) =
                match run $"File.readFrom \"{path}\" (Size.bytes {off})" with
                | VRecord("FileChunk", [ "lines", VSeq ls; "next", VSize n ]) ->
                    (ls
                     |> Seq.map (fun v ->
                         match v with
                         | VStr s -> s
                         | v -> failtest $"{v}")
                     |> List.ofSeq),
                    n
                | v -> failtest $"unexpected {v}"

            let raises (off: int) (frag: string) =
                let ex =
                    Expect.throwsC (fun () -> run $"File.readFrom \"{path}\" (Size.bytes {off})" |> ignore) id

                Expect.stringContains ex.Message frag $"offset {off}"

            let put (b: byte[]) = File.WriteAllBytes(path, b)
            let utf8 (s: string) = System.Text.Encoding.UTF8.GetBytes s

            try
                put (utf8 "one\ntwo\nthree\n")
                Expect.equal (chunk 0) ([ "one"; "two"; "three" ], 14L) "offset 0 reads all; next = size"
                Expect.equal (chunk 4) ([ "two"; "three" ], 14L) "mid-file on a boundary"
                Expect.equal (chunk 14) ([], 14L) "offset = size: empty, next unchanged"
                raises 15 "the offset 15 is past the end"
                raises 15 "(14 bytes)"
                raises 5 "the offset 5 is not at a line start"
                raises (-1) "negative"

                // the snapshot ends mid-line: the partial line waits
                put (utf8 "a\nhalf")
                Expect.equal (chunk 0) ([ "a" ], 2L) "a partial trailing line is not returned"
                Expect.equal (chunk 2) ([], 2L) "only a partial line: empty, next unchanged"
                File.AppendAllText(path, "-done\nb\n")
                Expect.equal (chunk 2) ([ "half-done"; "b" ], 14L) "completed on the next poll, once"

                // CRLF counts in the offsets; a CR alone at the end waits for its LF
                put (utf8 "a\r\nbb\r\nc\r")
                Expect.equal (chunk 0) ([ "a"; "bb" ], 7L) "CRLF lines, next past the LF"
                Expect.equal (chunk 3) ([ "bb" ], 7L) "from after the first CRLF"

                // multibyte UTF-8: next counts bytes, not chars
                put (utf8 "\u00E9\uD83D\uDE00\nx\n")
                Expect.equal (chunk 0) ([ "\u00E9\uD83D\uDE00"; "x" ], 9L) "2 + 4 + 1 + 2 bytes"
                Expect.equal (chunk 7) ([ "x" ], 9L) "from the second line"
                raises 1 "not at a line start"

                // a BOM is skipped at offset 0 only, and counted in next
                put (Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (utf8 "x\ny\n"))
                Expect.equal (chunk 0) ([ "x"; "y" ], 7L) "the BOM is not part of the first line"
                Expect.equal (chunk 5) ([ "y" ], 7L) "mid-file"

                put (Array.concat [ utf8 "x\n"; [| 0xEFuy; 0xBBuy; 0xBFuy |]; utf8 "y\n" ])
                Expect.equal (chunk 2) ([ "\uFEFFy" ], 7L) "a BOM past offset 0 is content, as File.read keeps it"
                Expect.equal (run $"File.read \"{path}\" |> Seq.item 1") (VStr "\uFEFFy") "File.read agrees"

                put (Array.append [| 0xFFuy; 0xFEuy |] (System.Text.Encoding.Unicode.GetBytes "x\n"))
                raises 0 "UTF-16/32 byte order mark"
            finally
                if File.Exists path then
                    File.Delete path
        }
        test "File.readFrom is eager: lines appended after the call are not in its result [D:file-readfrom]" {
            let path = weirPath (tempPath "weir-readfrom-eager" + ".txt")

            try
                File.WriteAllText(path, "a\nb\n")
                let v = run $"File.readFrom \"{path}\" 0B"
                File.AppendAllText(path, "c\nd\n")

                match v with
                | VRecord(_, [ "lines", VSeq ls; "next", VSize n ]) ->
                    Expect.equal (forceSeq (VSeq ls) |> List.length) 2 "the snapshot at the call"
                    Expect.equal n 4L "next matches the snapshot"
                | v -> failtest $"unexpected {v}"

                Expect.equal (run $"(File.readFrom \"{path}\" (Size.bytes 4)).lines |> Seq.length") (VInt 2L) "the rest"
            finally
                if File.Exists path then
                    File.Delete path
        }
        test "File.readFrom: a missing file raises as File.read does; readonly admits it, pure refuses it" {
            let ex =
                Expect.throwsC (fun () -> run "File.readFrom \"/weir-definitely-not\" 0B" |> ignore) id

            Expect.stringContains ex.Message "File.readFrom: no such file:" "File.read's shape"

            Expect.isEmpty
                (errorsIn "ro.weir" [
                    "let x ="
                    "    readonly"
                    "        (File.readFrom \"f\" 0B).next"
                    "print $\"{x}\""
                ])
                "a read"

            let e = firstErrorIn "pure.weir" [ "let pure f p = File.readFrom p 0B" ]
            Expect.stringContains e.Message "'File.readFrom' reads the filesystem" "pure refuses a read"
        }
    ]

let operatorTests =
    testList "Operator completeness" [
        test "precedence: || below && below comparisons" {
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
            Expect.stringContains
                (checkErr "(Seq.initInfinite id) <> (Seq.initInfinite id)").Message
                "'<>' is not defined for seq<int>"
                ""
        }
        test "ordered comparisons include boundaries" {
            expectValue "2 >= 2" (VBool true)
            expectValue "2 <= 1" (VBool false)
        }
        test "common filter shape works" {
            expectValue
                "ls |> where (fun f -> f.name <> \"a.txt\" && f.bytes <= 3MiB)"
                (VSeq [
                    Weir.Builtins.file "c.log" 1048576 false
                    Weir.Builtins.file "d.iso" 3145728 false
                ])
        }
        test "not builtin" {
            expectValue "not true" (VBool false)

            expectValue "ls |> where (fun f -> not f.hidden) |> take 1" (VSeq [ Weir.Builtins.file "a.txt" 0 false ])
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
        }
    ]

// ---- Windows v1, session 1 [D:windows-v1] — both-ways platform pins:
// each asserts its own platform's semantics, so the suite is meaningful
// on Linux today and on Windows when the CI matrix arrives
let fsMemberTests =
    testList "Filesystem members [D:fs-members]" [
        test "the four error shapes, each naming its path" {
            let d =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            // missing source
            let ex =
                Expect.throwsC (fun () -> run $"File.copy \"{d}/absent\" \"{d}/x\"" |> ignore) id

            Expect.stringContains ex.Message "File.copy: no such file:" "missing source"
            Expect.stringContains ex.Message "absent" "path named (separator-agnostic)"

            // existing destination refuses
            run $"[\"a\"] |> File.write \"{d}/a.txt\"" |> ignore
            run $"[\"b\"] |> File.write \"{d}/b.txt\"" |> ignore

            let ex2 =
                Expect.throwsC (fun () -> run $"File.copy \"{d}/a.txt\" \"{d}/b.txt\"" |> ignore) id

            Expect.stringContains ex2.Message "destination exists:" "refuse-overwrite"

            // non-empty refusal points at deleteAll
            let ex3 = Expect.throwsC (fun () -> run $"Dir.delete \"{d}\"" |> ignore) id
            Expect.stringContains ex3.Message "not empty:" "non-empty refusal"
            Expect.stringContains ex3.Message "Dir.deleteAll" "the pointer"

            run $"Dir.deleteAll \"{d}\"" |> ignore
            Expect.isFalse (System.IO.Directory.Exists d) "tree gone"

            // missing directory
            let ex4 = Expect.throwsC (fun () -> run $"Dir.deleteAll \"{d}\"" |> ignore) id
            Expect.stringContains ex4.Message "no such directory:" "absent asserts"
        }
        test "Dir.create is the idempotent exception; Dir.list is full-path sorted both-kinds" {
            let d =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            run $"Dir.create \"{d}/sub\"" |> ignore
            run $"Dir.create \"{d}/sub\"" |> ignore // twice: the post-condition
            run $"[\"x\"] |> File.write \"{d}/a.txt\"" |> ignore

            match run $"Dir.list \"{d}\" |> Seq.freeze" with
            | VSeq items ->
                let got =
                    items
                    |> Seq.map (function
                        | VStr s -> s
                        | _ -> "?")
                    |> List.ofSeq

                Expect.equal got (List.sort got) "sorted"
                Expect.isTrue (got |> List.forall (fun p -> (weirPath p).StartsWith d)) "full paths"
                Expect.equal got.Length 2 "files and dirs both"
            | v -> failtest $"unexpected {v}"

            run $"Dir.deleteAll \"{d}\"" |> ignore
        }
    ]

let durationTests =
    // parse-fatal teaching errors surface as Error from parseExpr
    let perr input =
        match Weir.Parser.parseExpr input with
        | Error msg -> msg
        | Ok e -> failtest $"expected a parse rejection, got {show e}"

    testList "Duration [D:duration]" [
        test "literal lexing: the four suffixes land in ms" {
            Expect.equal (run "500ms") (VDur 500L) ""
            Expect.equal (run "30s") (VDur 30000L) ""
            Expect.equal (run "2m") (VDur 120000L) ""
            Expect.equal (run "1h") (VDur 3600000L) ""
        }
        test "command-mode 30s stays an argv WORD (the sharpest pin)" {
            // the literal exists only in expression position; a command
            // argument is a word like any other
            expectCmd "echo 30s" "(cmd echo \"30s\")"
            // % is inert in argv [D:modulo] — a literal word char
            expectCmd "echo 50%" "(cmd echo \"50%\")"
        }
        test "show is the Go shape; parse round-trips it both directions" {
            Expect.equal (run "show 0ms") (VStr "0s") "zero is 0s"
            Expect.equal (run "show 90500ms") (VStr "1m30.5s") "decimals appear only in the rendering"
            Expect.equal (run "show (0s - 90500ms)") (VStr "-1m30.5s") "one leading sign"
            Expect.equal (run "show 3661000ms") (VStr "1h1m1s") ""
            Expect.equal (run "show 5ms") (VStr "5ms") "sub-second is ms"
            Expect.equal (run "Duration.parse (show 90500ms) == 90500ms") (VBool true) "value -> text -> value"
            Expect.equal (run "show (Duration.parse \"1h1m1.5s\")") (VStr "1h1m1.5s") "text -> value -> text"
            // the minimum value has no int64 magnitude — show renders it
            // anyway, as Size's show does
            Expect.equal
                (run "show (Duration.ms (0 - 9223372036854775807 - 1))")
                (VStr "-2562047788015h12m55.808s")
                "the minimum Duration shows"
        }
        test "the closed algebra: + - over Dur, * / against int, ints never mix implicitly" {
            Expect.equal (run "30s + 500ms") (VDur 30500L) ""
            Expect.equal (run "10s - 30s") (VDur -20000L) "negatives are legal"
            Expect.equal (run "30s * 2") (VDur 60000L) ""
            Expect.equal (run "2 * 30s") (VDur 60000L) ""
            Expect.equal (run "1h / 7") (VDur 514285L) "integer ms division"
            Expect.stringContains (checkErr "30s + 5").Message "expected Duration, got int" "no implicit ms"
        }
        test "Dur / Dur is rejected naming the toMillis ratio" {
            Expect.stringContains
                (checkErr "30s / 15s").Message
                "duration ÷ duration has no unit — Duration.toMillis d1 / Duration.toMillis d2 gives the integer ratio"
                ""
        }
        test "Eq, Show, Ord all admit Duration (the class widening)" {
            Expect.equal (run "30s == 30000ms") (VBool true) "Eq — same ms, same value"
            Expect.equal (run "Duration.m 2 > 90s") (VBool true) "Ord"

            Expect.equal
                (run "[3s; 1s; 2h] |> Seq.sortBy (fun d -> d) |> Seq.head")
                (VDur 1000L)
                "sortBy takes duration keys"
        }
        test "the constructors and toMillis are total; parse raises, tryParse options" {
            Expect.equal (run "Duration.ms 500") (VDur 500L) ""
            Expect.equal (run "Duration.s 30") (VDur 30000L) ""
            Expect.equal (run "Duration.toMillis 1m") (VInt 60000L) ""
            Expect.equal (run "Duration.parse \"2.5s\"") (VDur 2500L) "decimals live in TEXT"
            Expect.equal (run "Duration.parse \"-1h30m\"") (VDur -5400000L) ""
            Expect.equal (run "Duration.tryParse \"nope\"") (VUnion("None", None)) ""
            Expect.equal (run "Duration.tryParse \"90s\"") (VUnion("Some", Some(VDur 90000L))) ""

            let ex = Expect.throwsC (fun () -> run "Duration.parse \"1.0001s\"" |> ignore) id

            Expect.stringContains ex.Message "sub-millisecond" "the precision floor"
        }
        test "parse reads the extremes show prints; exact zero padding; ASCII digits only" {
            // the minimum reads back: the sign applies before the range check
            Expect.equal
                (run
                    "Duration.parse (show (Duration.ms (0 - 9223372036854775807 - 1))) == Duration.ms (0 - 9223372036854775807 - 1)")
                (VBool true)
                "the minimum Duration round-trips"

            Expect.equal (run "Size.parse \"-9223372036854775808B\"") (VSize System.Int64.MinValue) "the minimum Size"

            Expect.stringContains
                (msgOf "Size.parse \"9223372036854775808B\"")
                "beyond the 64-bit byte range"
                "one past the maximum"

            Expect.stringContains
                (msgOf "Duration.parse \"-9223372036854775809ms\"")
                "beyond the 64-bit millisecond range"
                "one past the minimum"
            // zero padding of any length is exact, not an overflow
            Expect.equal (run "Duration.parse \"1.0000000000000000000s\"") (VDur 1000L) "19 zero digits"
            Expect.equal (run "Size.parse \"1.00000000000000000000000KiB\"") (VSize 1024L) "23 zero digits"

            Expect.stringContains
                (msgOf "Duration.parse \"1.0000000000000000001s\"")
                "sub-millisecond precision"
                "a real remainder"
            // non-ASCII digits are not digits: a parse error, None from the twin
            Expect.stringContains
                (msgOf "Duration.parse \"１s\"")
                "not a duration: '１s' — expected digits at position 1"
                "fullwidth"

            Expect.equal (run "Duration.tryParse \"١s\"") (VUnion("None", None)) "Arabic-Indic: None, no raise"
            Expect.stringContains (msgOf "Duration.parse \"1.١s\"") "a decimal point needs digits" "in the fraction too"
        }
        test "the teaching rejections, each located" {
            Expect.stringContains
                (perr "2.5s")
                "decimal duration literals do not exist"
                "decimal-with-suffix names the ms form"

            Expect.stringContains
                (perr "2.")
                "float literals need digits on both sides of the point"
                "the 1. spelling teaches"

            Expect.stringContains (perr "2d") "durations have no 'd' suffix" "days are not uniform"

            Expect.stringContains (perr "1m30s") "compound durations are text" "compound spellings point at parse"
        }
        test
            "Duration.sleep: zero returns immediately, a negative duration raises (the deadline idiom must not silently no-op)" {
            Expect.equal (run "Duration.sleep 0ms") VUnit "zero is immediate"

            let ex = Expect.throwsC (fun () -> run "Duration.sleep (0s - 5s)" |> ignore) id

            Expect.stringContains ex.Message "Duration.sleep: negative duration (-5s)" "located, naming the value"
        }
        // B35: a Duration past Int32.MaxValue ms sleeps (in slices)
        // instead of leaking .NET's argument error
        test "Duration.sleep accepts a duration longer than 24.8 days" {
            let mutable failed: exn option = None

            let t =
                System.Threading.Thread(
                    (fun () ->
                        try
                            run "Duration.sleep (Duration.h 1000)" |> ignore
                        with e ->
                            failed <- Some e),
                    IsBackground = true
                )

            t.Start()
            Expect.isFalse (t.Join 300) "still sleeping"
            Expect.isNone failed "no raise"
        }
        // B35: overflow and range failures in weir's words, never .NET's
        test "Duration/Size/Instant overflow and Str.replace \"\" raise in weir's words" {
            for src, want in
                [
                    "1h * 9223372036854775807", "Duration overflow"
                    "Duration.h 9223372036854775807", "Duration.h: 9223372036854775807 overflows a Duration"
                    "1h / 0", "division by zero"
                    "(Size.bytes 2) * 9223372036854775807", "Size overflow"
                    "(Size.bytes 1) / 0", "division by zero"
                    "Instant.ofEpochMs 9223372036854775807", "Instant.ofEpochMs: instant out of range"
                    "(Instant.ofEpochMs 253402300799999) + 1ms", "instant out of range"
                    "(Instant.ofEpochMs 0) - 1h * 999999999", "instant out of range"
                    "Str.replace \"\" \"x\" \"abc\"", "Str.replace: the text to replace is empty"
                ] do
                let m = msgOf src
                Expect.stringContains m want src
                Expect.isFalse (m.Contains "Arithmetic operation") "no raw .NET words"

            expectValue "Instant.ofEpochMs 253402300799999 |> show" (VStr "9999-12-31T23:59:59.999Z")
            expectValue "Size.bytes 9223372036854775807 |> show" (VStr "8388607.9 TiB")
        }
        test "JSON is parked: the boundary rejects Duration naming the conversion" {
            let e = env |> declare "type DurJ = { dj: Duration }"

            match Weir.Check.typecheck e (parse "[{ dj = 5s }] |> to json") with
            | Error terr ->
                Expect.stringContains terr.Message "Duration is not representable in JSON" ""
                Expect.stringContains terr.Message "Duration.toMillis into an int field" "the conversion named"
            | Ok _ -> failtest "expected the park rejection"
        }
        test "[<Default 30s>] literal law: match admits, mismatch names the field type" {
            // Args.load is script-only: the Self module marks script mode
            let scriptEnv = {
                env with
                    Modules = env.Modules |> Map.add "Self" Weir.Statements.selfMembers
            }

            let e =
                scriptEnv
                |> declare "type DurA = { [<Default 30s>] timeout: Duration; name: string }"

            match Weir.Check.typecheck e (parse "Args.load DurA") with
            | Ok te -> Expect.equal (formatTy te.Ty) "DurA" ""
            | Error terr -> failtest $"expected Ok, got {terr.Message}"

            let e2 = scriptEnv |> declare "type DurB = { [<Default 30s>] port: int }"

            match Weir.Check.typecheck e2 (parse "Args.load DurB") with
            | Error terr -> Expect.stringContains terr.Message "the Default literal does not match the field" ""
            | Ok _ -> failtest "expected the literal-law rejection"
        }
    ]

let dupTypeTests =
    let analyze (lines: string list) =
        let p = tempPath "weir-dup" + ".weir"

        System.IO.File.WriteAllLines(p, lines)

        try
            let ds = diagsIn p (List.ofArray (System.IO.File.ReadAllLines p))

            ds |> List.filter (fun d -> d.Severity = "error")
        finally
            System.IO.File.Delete p

    testList "Duplicate type declarations [D:dup-type-decl]" [
        test "a duplicate record errors at the SECOND declaration, naming the first's line" {
            match
                analyze [
                    "type T = { a: int }"
                    "let _x = { a = 1 }"
                    "type T = { b: string }"
                    "print \"n\""
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 3 "located at the second"
                Expect.equal d.Message "type 'T' is already declared at line 1" "exact text, both sites"
            | other -> failtest $"expected one error, got {other.Length}"
        }
        test "unions too — every type-declaring form rides the one KType arm" {
            match analyze [ "type U = Go of int | Halt"; "type U = Stop"; "print \"n\"" ] with
            | [ d ] -> Expect.stringContains d.Message "type 'U' is already declared at line 1" ""
            | other -> failtest $"expected one error, got {other.Length}"
        }
        test "the loader surprise dissolves: a duplicate Cli errors at the declaration, not at Args.load" {
            match
                analyze [
                    "type Cli = { name: string }"
                    "type Cli = { name: string; extra: bool }"
                    "print \"n\""
                ]
            with
            | [ d ] -> Expect.equal d.Line 2 ""
            | other -> failtest $"expected one error, got {other.Length}"
        }
    ]

let fileRowSizeTests =
    // FileRow.bytes : Size — the Size session's own argument applied to
    // the member it missed [D:size]; File.size and ls now agree on the
    // quantity's type
    testList "FileRow.bytes is a Size [D:size]" [
        test "the acceptance: f.bytes > 10MiB typechecks and filters" {
            expectValue "ls |> where (fun f -> f.bytes > 4MiB) |> map _.name" (VSeq [ VStr "b.bin" ])
        }
        test "show of a row renders the size in binary units" {
            expectValue
                "show (ls |> where (fun f -> f.name == \"b.bin\") |> Seq.head)"
                (VStr
                    "{ name = \"b.bin\"; kind = Regular; target = None; bytes = 5 MiB; modified = 1970-01-01T00:00:00Z; hidden = true; path = \"b.bin\" }")
        }
        test "sortBy crosses the class boundary: Ord admits Size" {
            expectValue "ls |> Seq.sortByDescending _.bytes |> take 1 |> map _.name" (VSeq [ VStr "b.bin" ])
        }
    ]

let replTableTests =
    // the REPL's table echo [D:repl-table]: presentation only — a pure
    // function over values, tty-gated at the call site (the piped REPL
    // keeps the line rendering, pinned in the pty harness)
    testList "repl table [D:repl-table]" [
        test "same-shaped scalar records tabulate: header, rule, aligned rows" {
            let rows =
                VSeq [
                    VRecord("FileRow", [ "name", VStr "core.dump"; "bytes", VSize 4194304L; "readOnly", VBool false ])
                    VRecord("FileRow", [ "name", VStr "n.txt"; "bytes", VSize 7L; "readOnly", VBool true ])
                ]

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None rows with
            | Some(lines, None) ->
                Expect.equal lines[0] "name       bytes  readOnly" "declaration-order headers [D:record-order]"
                Expect.stringContains lines[1] "─" "the rule line"
                Expect.equal lines[2] "core.dump  4 MiB  false" "unquoted strings, show's scalar spellings"
                Expect.equal lines[3] "n.txt        7 B  true" "numeric right-aligned"
            | other -> failtest $"expected a table, got {other}"
        }
        test "an UNFORCED seq's table caps with the honest hint; a FORCED one shows every row [D:echo-rule]" {
            let row i = VRecord("R", [ "n", VInt(int64 i) ])

            let lazyRows = VSeq(Seq.initInfinite (fun i -> row i))

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None lazyRows with
            | Some(lines, Some hint) ->
                Expect.equal hint (Weir.Eval.unforcedHint 100) "the lever that works"
                Expect.equal (List.last lines) "…" "the in-table signal"
            | other -> failtest $"expected a capped table, got {other}"

            let forcedRows = VSeq [ for i in 1..15 -> row i ]

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None forcedRows with
            | Some(lines, None) -> Expect.equal (List.length lines) 17 "header + rule + all 15 rows"
            | other -> failtest $"a forced table shows every row: {other}"
        }
        test "non-uniform and non-scalar shapes decline — the line rendering stands" {
            let mixed = VSeq [ VRecord("A", [ "x", VInt 1L ]); VRecord("B", [ "y", VInt 2L ]) ]

            Expect.isTrue (Weir.Eval.echoTable Weir.Eval.echoPipedCap None mixed |> Option.isNone) "different shapes"

            let nested = VSeq [ VRecord("N", [ "inner", VSeq [ VInt 1L ] ]) ]

            Expect.isTrue
                (Weir.Eval.echoTable Weir.Eval.echoPipedCap None nested |> Option.isNone)
                "a seq cell is not a scalar"

            Expect.isTrue
                (Weir.Eval.echoTable Weir.Eval.echoPipedCap None (VSeq [ VInt 1L ])
                 |> Option.isNone)
                "scalars keep the list echo"

            Expect.isTrue
                (Weir.Eval.echoTable Weir.Eval.echoPipedCap None (VSeq []) |> Option.isNone)
                "empty keeps [] : seq<T>"
        }
        test "Option cells: Some inlines, None is blank; a Secret cell masks" {
            let rows =
                VSeq [
                    VRecord("P", [ "note", VUnion("Some", Some(VStr "hi")); "tok", VSecret "s3cr3t" ])
                    VRecord("P", [ "note", VUnion("None", None); "tok", VSecret "x" ])
                ]

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None rows with
            | Some(lines, None) ->
                Expect.stringContains lines[2] "hi" ""
                Expect.stringContains lines[2] "***" "the rendering marker holds in cells"
                Expect.isFalse (lines |> List.exists (fun l -> l.Contains "s3cr3t")) "never the reveal"
            | other -> failtest $"expected a table, got {other}"
        }
    ]

let lsSortTests =
    // ls joins the sorted discovery surfaces [D:ls-sort]: by name,
    // ordinal — case-sensitive, uppercase first, never the locale
    testList "ls is sorted [D:ls-sort]" [
        test "Env.vars is name-sorted (the sweep's one sibling); fromFile keeps FILE order" {
            let names =
                run "Env.vars |> Seq.map _.name"
                |> forceSeq
                |> List.map (fun v ->
                    match v with
                    | VStr s -> s
                    | _ -> failtest "string expected")

            Expect.equal
                names
                (List.sortWith (fun (a: string) b -> System.String.CompareOrdinal(a, b)) names)
                "sorted ordinal"

            Expect.isTrue (names.Length > 3) "a real environment"
        }
        test "the FileRow table's column order is DECLARATION order — name leads, path trails [D:record-order]" {
            // supersedes the alphabetical pin (that order was the Map
            // container's accident; the table-polish decline was about
            // a reorder knob, not about order being carried)
            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None (VSeq(fakeFiles |> List.toSeq)) with
            | Some(header :: _, _) ->
                Expect.isTrue (header.StartsWith "name") "name leads"

                // target is absent by the hiding rule (all fixture rows
                // carry None) [D:filerow] — its reappearance is its own pin
                for col in [ "bytes"; "hidden"; "kind"; "modified"; "name"; "path" ] do
                    Expect.stringContains header col $"column present: {col}"

                Expect.isTrue
                    (header.IndexOf "name" < header.IndexOf "bytes"
                     && header.IndexOf "bytes" < header.IndexOf "modified"
                     && header.IndexOf "readOnly" < header.IndexOf "path")
                    "declaration order holds"
            | other -> failtest $"expected a table, got {other}"
        }
    ]

let fileRowReshapeTests =
    // the FileRow reshape [D:filerow]: kind not isDirectory, target for
    // symlinks, readOnly retired, relative table cell, all-None hiding
    testList "FileRow reshape" [
        test "the retired fields TEACH, not 'no field'" {
            Expect.stringContains
                (checkErr "ls |> Seq.map _.isDirectory").Message
                "replaced by 'kind'"
                "isDirectory teaches its replacement"

            Expect.stringContains
                (checkErr "ls |> Seq.map _.readOnly").Message
                "File.mode"
                "readOnly names the query that superseded it"
        }
        test "kind compares against the union cases; target is an Option" {
            expectValue "ls |> where (fun f -> f.kind == Regular) |> Seq.length" (VInt 4L)
            expectValue "ls |> Seq.map _.target |> Seq.choose (fun t -> t) |> Seq.length" (VInt 0L)
        }
        test "the table renders modified RELATIVELY while show keeps ISO — one pin, both halves" {
            // the Duration split's twin [D:filerow]: assert together or
            // the split is not pinned
            let nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let row = Weir.Builtins.file "a.txt" 0 false

            match Weir.Eval.echoTable None None (VSeq [ row ]) with
            | Some(header :: _ :: body :: _, _) ->
                Expect.stringContains header "modified" "the column is shown"
                Expect.stringContains body "years ago" "epoch-modified renders relatively in the CELL"
            | other -> failtest $"no table: {other}"

            match runWith [] "show (ls |> Seq.head)" with
            | VStr shown -> Expect.stringContains shown "1970-01-01T00:00:00Z" "show keeps ISO"
            | v -> failtest $"show: {v}"

            Expect.equal (Weir.Eval.relativeInstant nowMs (nowMs - 5000L)) "just now" "seconds"
            Expect.equal (Weir.Eval.relativeInstant nowMs (nowMs - 300_000L)) "5 minutes ago" "minutes"
            Expect.equal (Weir.Eval.relativeInstant nowMs (nowMs - 7L * 86_400_000L)) "a week ago" "the nushell line"
            Expect.equal (Weir.Eval.relativeInstant nowMs (nowMs + 3L * 86_400_000L)) "in 3 days" "the future mirrors"
        }
        test "an all-None optional column HIDES; one Some brings it back" {
            let plain = Weir.Builtins.file "a.txt" 0 false

            let linked =
                Weir.Eval.recSet
                    "target"
                    (Weir.Eval.VUnion("Some", Some(Weir.Eval.VStr "/etc/hostname")))
                    (match plain with
                     | Weir.Eval.VRecord(_, fs) -> fs
                     | _ -> failtest "fixture")
                |> fun fs -> Weir.Eval.VRecord("FileRow", fs)

            match Weir.Eval.echoTable None None (VSeq [ plain ]) with
            | Some(header :: _, _) -> Expect.isFalse (header.Contains "target") "all-None hides"
            | other -> failtest $"no table: {other}"

            match Weir.Eval.echoTable None None (VSeq [ plain; linked ]) with
            | Some(header :: _, _) -> Expect.stringContains header "target" "one Some restores the column"
            | other -> failtest $"no table: {other}"
        }
    ]

let lsTruthTests =
    // ls tells the whole truth [D:ls-truth]: files and directories, the
    // stated seven-field surface.
    // testSequenced [D:unused-bindings adjacent]: this list mutates the
    // global Session.Cwd (setCwd into a temp dir, then deletes it). Every
    // other cwd-mutating list is already sequenced; left parallel, this one
    // raced the parallel spawn tests — a concurrent spawn snapshots the temp
    // cwd, this list deletes it before Process.Start, and the child dies
    // "command not found" (the chdir fails). Sequencing moves it out of the
    // parallel phase so no spawn ever sees a vanishing cwd.
    testSequenced
    <| testList "ls tells the whole truth [D:ls-truth]" [
        test "directories join the rows: isDirectory filters, bytes is 0 B there" {
            let d = tempPath "weir-ls"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "sub")) |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "f.txt"), "x")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, ".dot"), "")
            // for the sort pin [D:ls-sort]: a case pair proves ordinal
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "B.txt"), "x")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "a.txt"), "x")

            try
                inCwd d (fun () ->
                    match
                        runLive "ls |> Seq.where (fun f -> f.kind == Directory) |> Seq.map _.name"
                        |> forceSeq
                    with
                    | [ VStr "sub" ] -> ()
                    | other -> failtest $"the subdirectory must list: {other}"

                    match runLive "(ls |> Seq.where (fun f -> f.kind == Directory) |> Seq.head).bytes" with
                    | VSize 0L -> ()
                    | other -> failtest $"a directory's bytes is 0 B, honestly: {other}"

                    match runLive "ls |> Seq.where _.hidden |> Seq.map _.name" |> forceSeq with
                    | [ VStr ".dot" ] -> ()
                    | other -> failtest $"the dot-name is hidden: {other}"

                    // sorted by name, ordinal [D:ls-sort]: B(66) before
                    // a(97); the locale is never consulted
                    match runLive "ls |> Seq.map _.name" |> forceSeq with
                    | [ VStr ".dot"; VStr "B.txt"; VStr "a.txt"; VStr "f.txt"; VStr "sub" ] -> ()
                    | other -> failtest $"ls must sort ordinal by name: {other}"

                    match runLive "ls |> Seq.where (fun f -> Instant.now () - f.modified < 1h) |> Seq.length" with
                    | VInt 5L -> ()
                    | other -> failtest $"fresh files filter by age: {other}"

                    match runLive "(ls |> Seq.head).path" with
                    | VStr p when p.StartsWith d -> ()
                    | other -> failtest $"path is absolute: {other}")
            finally
                System.IO.Directory.Delete(d, true)
        }
    ]

let accessorTeachingTests =
    // the indexer's diagnostics [D:accessor-teaching] + weir owning its
    // runtime text [D:message-ownership] — the slicing costing's three
    // side findings, each independent of the parked feature
    testList "accessor teachings [D:accessor-teaching]" [
        test "range slicing checks — type-directed on strings and sequences [D:range-slicing]" {
            // was refused with "no range indexing"; now the first
            // type-directed node — the target's type picks str vs seq
            for src in
                [
                    "let xs = [1; 2; 3] in xs[1..2]"
                    "let xs = [1; 2; 3] in xs[..2]"
                    "let xs = [1; 2; 3] in xs[1..]"
                ] do
                Expect.stringContains (formatTy (checkOk src).Ty) "seq" $"'{src}' slices to a seq"

            Expect.equal (formatTy (checkOk "\"abcdef\"[1..3]").Ty) "string" "a string slices to a string"
        }
        test "Bytes slices to Bytes — the third type-directed target [D:range-slicing]" {
            for src in
                [
                    "let b = Bytes.fromBase64 \"AAECAwQF\" in b[1..3]"
                    "let b = Bytes.fromBase64 \"AAECAwQF\" in b[..2]"
                    "let b = Bytes.fromBase64 \"AAECAwQF\" in b[3..]"
                ] do
                Expect.equal (formatTy (checkOk src).Ty) "Bytes" $"'{src}' slices to Bytes"

            // inclusive, clamping — the same contract as string/seq
            expectValue "Bytes.toBase64 ((Bytes.fromBase64 \"AAECAwQF\")[1..3])" (VStr "AQID") // bytes 1,2,3 of 0..5
            expectValue "Bytes.toBase64 ((Bytes.fromBase64 \"AAEC\")[10..20])" (VStr "") // out of range
            expectValue "Bytes.toBase64 ((Bytes.fromBase64 \"AAEC\")[3..1])" (VStr "") // reversed
        }
        test "b[i] on Bytes teaches the byte forms — indexing stays parked [D:range-slicing]" {
            let msg = (checkErr "let b = Bytes.fromBase64 \"AAEC\" in b[0]").Message
            Expect.stringContains msg "no b[i] index on Bytes" "names the refusal"
            Expect.stringContains msg "b[a..b]" "points at slicing"
            Expect.stringContains msg "Bytes.sub" "and the window form"
            Expect.isFalse (msg.Contains "expected seq") "not the leaky desugar type"
        }
        test "s[i] on a string teaches the character forms; mismatches never print a generated var" {
            Expect.equal
                (checkErr "let s = \"hello\" in s[0]").Message
                "no s[i] index on a string — slice it (s[i..i], inclusive) or take characters with Str.sub start len"
                "the string index"

            let m = (checkErr "[1] |> Seq.map (fun (a, b) -> a)").Message
            Expect.isFalse (System.Text.RegularExpressions.Regex.IsMatch(m, "'[a-z]+[0-9]")) $"no raw var: {m}"
            Expect.equal m "expected 'a * 'b, got int" "the vars read 'a, 'b"

            Expect.equal
                (errorMsgsIn "disc.weir" [ "let f () = exit 1"; "f ()" ])
                [
                    "this statement computes a value of undetermined type and discards it — an unresolved type here usually means the helper ends in exit or fail, which makes it polymorphic, not unit: return the exit code and exit at the call site (exit (helper …)), or bind the value"
                ]
                "the discard names no var"
        }
        test "from-the-end indexing and slicing (^n) work [D:range-slicing]" {
            // ^n is length - n (F#'s spelling); reverses the earlier decline
            expectValue "let xs = [1; 2; 3] in xs[^1]" (VInt 3L) // last
            expectValue "let xs = [1; 2; 3] in xs[^2]" (VInt 2L)
            // slice bounds: all-but-last, last-two, both-ends from end
            expectValue "let xs = [1; 2; 3] in Seq.sum xs[..^2]" (VInt 3L) // [1;2]
            expectValue "let xs = [1; 2; 3] in Seq.sum xs[^2..]" (VInt 5L) // [2;3]
            expectValue "let xs = [1; 2; 3] in Seq.sum xs[^3..^1]" (VInt 6L) // [1;2;3]
            expectValue "\"abcde\"[^2..]" (VStr "de")
            expectValue "\"abcde\"[..^2]" (VStr "abcd")
        }
        // B34: an out-of-range ^n names itself and the length, not index 0
        test "an out-of-range xs[^n] names the ^n and the length" {
            for n in [ 0L; 4L; 4294967296L ] do
                Expect.stringContains
                    (msgOf $"let xs = [1; 2; 3] in xs[^{n}]")
                    $"index ^{n} is out of range for length 3"
                    $"^{n}"

            Expect.stringContains
                (checkErr "let m = Map.ofPairs [(\"a\", 1)] in m[^1]").Message
                "no m[k] indexing"
                "the Map teaching covers the from-end form"
        }
        test "the F# dotted indexer teaches the dotless spelling" {
            match Weir.Parser.parseExpr "let xs = [1; 2; 3] in xs.[0]" with
            | Ok _ -> failtest "xs.[0] must be refused"
            | Error m ->
                Expect.stringContains m "without the dot" "names the repair"
                Expect.stringContains m "xs[i]" "shows the spelling"
        }
        test "the ordinary indexer is untouched — the guards only catch their shapes" {
            // the positive twin: a teaching that fires on everything is
            // indistinguishable from a broken indexer
            expectValue "let xs = [1; 2; 3] in xs[1]" (VInt 2L)
            expectValue "let xs = [1; 2; 3] in xs[1 + 1]" (VInt 3L)
            expectValue "let f = fun x -> x[0] in f [7; 8]" (VInt 7L)
            // a space means application, still (the F# 6 whitespace rule)
            Expect.equal (show (parse "f [1; 2]")) "(f [1; 2])" "spaced brackets stay an application"
        }
        test "m[k] teaches Map.get — the doc's promise, now real" {
            // SKILL has said "no m[k] indexing — Map.get is the spelling"
            // while the binary raised a raw unification error on the key
            let msg = (checkErr "let m = Map.ofPairs [(\"a\", 1)] in m[\"a\"]").Message
            Expect.stringContains msg "no m[k] indexing" "names the refusal"
            Expect.stringContains msg "Map.get" "names the spelling"
            Expect.stringContains msg "Map.tryGet" "and the asking form"

            // the index type must not decide it: an int index on a Map
            // reaches the same teaching (the raw error fired here first)
            Expect.stringContains
                (checkErr "let m = Map.ofPairs [(\"a\", 1)] in m[0]").Message
                "no m[k] indexing"
                "the target decides, not the index"
        }
        test "weir owns the seq runtime messages [D:message-ownership]" {
            // FSharp.Core's own text reached users here: Seq.item's
            // "seq was short by {1} {2}" and Seq.skip's composite
            // "tried to skip {0} {1} past the end of the seq"
            let item = msgOf "[1] |> Seq.item 5"
            Expect.stringContains item "item: no element at index 5" "weir's text"
            Expect.isFalse (item.Contains "insufficient") "not FSharp.Core's"

            let skip = msgOf "[1] |> Seq.skip 5 |> Seq.freeze"
            Expect.stringContains skip "skip: fewer than 5 elements" "weir's text"
            Expect.isFalse (skip.Contains "tried to skip") "not FSharp.Core's"

            Expect.equal (msgOf "1 / 0") "division by zero" "the int twin of the float message"

            // the positive twins: the members still work, and skip stays
            // lazy (an infinite source must not be probed for length)
            expectValue "[1; 2; 3] |> Seq.item 1" (VInt 2L)
            expectValue "(Seq.initInfinite id) |> Seq.skip 2 |> Seq.head" (VInt 2L)
        }
        test "a frozen seq indexes without walking, its messages unchanged [D:seq-index-o1]" {
            match run "[1; 2; 3] |> Seq.freeze" with
            | VSeq(:? (Value[])) -> ()
            | v -> failtest $"a frozen seq is an array: {v}"

            let xs = "let xs = [10; 20; 30] |> Seq.freeze in "
            expectValue (xs + "Seq.length xs") (VInt 3L)
            expectValue (xs + "xs[1] + Seq.item 2 xs") (VInt 50L)
            expectValue (xs + "xs[^1] + Seq.last xs") (VInt 60L)
            expectValue (xs + "Seq.tryItem 2 xs") (VUnion("Some", Some(VInt 30L)))
            expectValue (xs + "Seq.tryItem 3 xs") (VUnion("None", None))
            expectValue (xs + "Seq.tryItem (-1) xs") (VUnion("None", None))
            expectValue (xs + "Seq.tryLast xs") (VUnion("Some", Some(VInt 30L)))
            Expect.stringContains (msgOf (xs + "Seq.item 5 xs")) "item: no element at index 5" "the lazy path's text"
            Expect.stringContains (msgOf (xs + "xs[^4]")) "index ^4 is out of range for length 3" "the from-end text"

            let empty = "let e = [1] |> Seq.where (fun x -> x > 1) |> Seq.freeze in "
            Expect.stringContains (msgOf (empty + "Seq.last e")) "last: empty sequence" "empty last raises"
            expectValue (empty + "Seq.tryLast e") (VUnion("None", None))
        }
        // B5 [D:int-narrowing]: a 64-bit int past int32 never wraps —
        // a position is out of range, an amount too large; slices clamp
        test "int arguments past int32 raise instead of wrapping" {
            for n in [ 2147483648L; 4294967296L; 4294967297L ] do
                for (src, want) in
                    [
                        $"[10; 20] |> Seq.item {n}", "out of range"
                        $"let xs = [10; 20] in xs[{n}]", "out of range"
                        $"[1; 2] |> Seq.take {n} |> Seq.length", "too large"
                        $"Str.replicate {n} \"a\"", "too large"
                        $"Str.padLeft {n} \"a\"", "too large"
                        $"Str.padRight {n} \"a\"", "too large"
                        $"[1] |> Seq.replicate {n} |> Seq.length", "too large"
                        $"[1] |> Seq.windowed {n} |> Seq.length", "too large"
                        $"[1] |> Seq.chunkBySize {n} |> Seq.length", "too large"
                        $"Str.sub {n} 1 \"abc\"", "out of bounds"
                        $"Str.sub 1 {n} \"abc\"", "out of bounds"
                        $"Str.toUtf8 \"abc\" |> Bytes.sub 1 {n}", "out of bounds"
                    ] do
                    Expect.stringContains (msgOf src) want src

                expectValue $"[1; 2; 3][0..{n}] |> Seq.length" (VInt 3L)
                expectValue $"\"abc\"[1..{n}]" (VStr "bc")
                expectValue $"[1; 2; 3][{n}..] |> Seq.length" (VInt 0L)

            // -1 keeps each member's own negative rule
            Expect.stringContains (msgOf "[10; 20] |> Seq.item -1") "negative index" "item"
            Expect.stringContains (msgOf "Str.replicate -1 \"a\"") "non-negative" "Str.replicate"
            Expect.stringContains (msgOf "Str.sub -1 1 \"abc\"") "out of bounds" "Str.sub"
            Expect.stringContains (msgOf "Str.replicate 1000000000 \"ab\"") "too large" "the string ceiling"
            // a builder past the ceiling is refused in weir's words, sized
            Expect.equal
                (msgOf "Str.join \"\" (Seq.replicate 2000000 (Str.replicate 1000 \"a\"))")
                "Str.join: a 2000000000-char result is too large (at most 1073741791)"
                "Str.join"

            Expect.equal
                (Weir.Types.strTooLarge "string +" 1200000000L)
                "string +: a 1200000000-char result is too large (at most 1073741791)"
                "the shared refusal string + and interpolation raise"

            // show's escaping past the ceiling: refused, sized, before building
            Expect.equal
                (msgOf "Str.replicate 540000000 \"\\\"\" |> show |> Str.length")
                "show: a 1080000002-char result is too large (at most 1073741791)"
                "show"
            // to json writes a string past System.Text.Json's single-token
            // ceiling in segments, a pair never split between them
            expectValue
                "let s = Str.replicate 16777215 \"a\" + \"😀\" + Str.replicate 167000000 \"b\" in {| s = s |} |> to json |> from json {| s: string |} |> fun r -> r.s == s"
                (VBool true)

            expectValue "[1] |> Seq.chunkBySize 2147483647 |> Seq.length" (VInt 1L)
        }
        // the gate: no builtin narrows a user int with a bare `int` cast —
        // toIntArg is the one narrowing (the exit code is the triaged
        // exception: a process exit status is the OS's own wrap)
        test "no bare int cast on a VInt argument in the runtime [D:int-narrowing]" {
            let src = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "Weir")

            let offenders =
                [
                    for file in [ "Builtins.fs"; "Eval.fs"; "Repl.fs" ] do
                        let lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(src, file))

                        for i in 0 .. lines.Length - 1 do
                            for m in System.Text.RegularExpressions.Regex.Matches(lines[i], @"VInt (\w+)") do
                                let v = m.Groups[1].Value
                                let cast = System.Text.RegularExpressions.Regex($@"\bint\s*\(?\s*{v}\b")

                                for j in i .. min (i + 25) (lines.Length - 1) do
                                    if cast.IsMatch lines[j] && not (lines[j].Contains "ExitRequest") then
                                        yield $"{file}:{j + 1}: {lines[j].Trim()}"
                ]
                |> List.distinct

            Expect.equal offenders [] "narrow through toIntArg"
        }
        test "modulo [D:modulo]: truncated, int-only, /'s zero discipline" {
            // truncated (F#/.NET): the sign follows the dividend —
            // only the negative cases assert the ruling
            expectValue "7 % 3" (VInt 1L)
            expectValue "-7 % 3" (VInt -1L)
            expectValue "7 % -3" (VInt 1L)
            expectValue "-7 % -3" (VInt -1L)

            // precedence: */'s level
            expectValue "1 + 2 % 3" (VInt 3L)

            Expect.equal (msgOf "7 % 0") "modulo by zero" "/'s discipline, %'s word"

            Expect.stringContains
                (checkErr "let x = 7.5 % 2.0 in 0").Message
                "'%' is integer-only"
                "floats refused — finite-only cannot hold NaN remainder"

            // the checked corner /'s family shares
            Expect.stringContains (msgOf "(0 - 9223372036854775807 - 1) % (0 - 1)") "integer overflow" "MinValue % -1"
        }
    ]

let fileStatTests =
    // the bridge from paths to rows [D:file-stat]: ls's own constructor
    // over one path — the agreement between the two producers is the
    // property, so it is pinned directly. Sequenced: the session cwd is
    // ambient and lsTruthTests moves it too
    testSequenced
    <| testList "File.stat [D:file-stat]" [
        test "the agreement pin: ls and File.stat produce identical rows" {
            let d = tempPath "weir-stat"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "sub")) |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "f.txt"), "x")

            try
                inCwd d (fun () ->
                    match
                        runLive "show (ls |> Seq.find (fun f -> f.name == \"f.txt\")) == show (File.stat \"f.txt\")"
                    with
                    | VBool true -> ()
                    | other -> failtest $"the file rows must be identical: {other}"

                    match runLive "show (ls |> Seq.find (fun f -> f.name == \"sub\")) == show (File.stat \"sub\")" with
                    | VBool true -> ()
                    | other -> failtest $"the directory rows must be identical: {other}"

                    // a relative argument resolves against the session
                    // cwd and yields the documented absolute path
                    match runLive "(File.stat \"f.txt\").path" with
                    | VStr p when p.StartsWith d && p.EndsWith "f.txt" -> ()
                    | other -> failtest $"a relative argument must yield an absolute path: {other}")
            finally
                System.IO.Directory.Delete(d, true)
        }
        test "raises when absent, naming the member and the resolved path" {
            let ex =
                Expect.throwsC (fun () -> run "File.stat \"weir-stat-no-such-path-xyz\"" |> ignore) id

            Expect.stringContains ex.Message "File.stat: no such path:" "the family's raise shape"
            Expect.stringContains ex.Message "weir-stat-no-such-path-xyz" "names the path"
        }
    ]

let dirStatTests =
    // the rows form of Dir.list [D:dir-stat]: ls's enumeration over a
    // named directory. The agreement — identical rows in identical
    // order — is the property; the halves are implementation.
    // Sequenced: the session cwd is ambient (the fileStatTests reason)
    testSequenced
    <| testList "Dir.stat [D:dir-stat]" [
        test "the agreement pin: ls and Dir.stat \".\" produce identical rows in identical order" {
            let d = tempPath "weir-dstat"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "sub")) |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "B.txt"), "x")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "a.txt"), "x")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, ".dot"), "x")

            try
                inCwd d (fun () ->
                    match
                        runLive
                            "(ls |> Seq.map show |> Str.join \"|\") == (Dir.stat \".\" |> Seq.map show |> Str.join \"|\")"
                    with
                    | VBool true -> ()
                    | other -> failtest $"rows and order must agree: {other}"

                    // a relative argument yields absolute path fields
                    // (File.stat's pin, the seq form)
                    match runLive "(Dir.stat \".\" |> Seq.head).path" with
                    | VStr p when p.StartsWith d -> ()
                    | other -> failtest $"a relative argument must yield absolute paths: {other}")
            finally
                System.IO.Directory.Delete(d, true)
        }
        test "empty gives []; absent raises naming Dir.stat and the path" {
            withTempDir "weir-dstat" (fun d ->
                match run $"Dir.stat \"{d.Replace('\\', '/')}\" |> Seq.length" with
                | VInt 0L -> ()
                | other -> failtest $"empty must be zero rows: {other}")

            let ex =
                Expect.throwsC (fun () -> run "Dir.stat \"weir-dstat-no-such-dir-xyz\"" |> ignore) id

            Expect.stringContains ex.Message "Dir.stat: no such directory:" "names the member"
            Expect.stringContains ex.Message "weir-dstat-no-such-dir-xyz" "names the path"
        }
    ]

let invariantModeTests =
    // the string semantics the shipped binary runs [D:invariant-strings]:
    // Weir.fsproj sets InvariantGlobalization, where every culture
    // comparison is ordinal; the test hosts must match or the referee
    // tests different semantics than the subject. Under ICU culture
    // rules U+001F — and NUL, ZWJ, the soft hyphen — are ignorable:
    // "a<US>b".StartsWith "ab" is true, and LastIndexOf "matches" past
    // the end (the [D:path-param-completion] crash, tests-only). Str's
    // startsWith/endsWith are these very calls, so this pins the
    // language surface too.
    testList "globalization-invariant hosts [D:invariant-strings]" [
        test "ignorable characters do not vanish from comparisons" {
            let sib = "\u001F"
            let hay = "a" + sib + "b"
            Expect.isFalse (hay.StartsWith "ab") "U+001F is not ignorable at StartsWith"
            Expect.isFalse (hay.EndsWith "ab") "nor at EndsWith"
            Expect.equal ("abc".LastIndexOf sib) -1 "an absent sentinel is ABSENT, never past-the-end"
            Expect.isFalse ("a\u0000b".StartsWith "ab") "NUL is not ignorable"
            Expect.isFalse ("a\u200Db".StartsWith "ab") "ZWJ is not ignorable"
            Expect.isFalse ("a\u00ADb".StartsWith "ab") "the soft hyphen is not ignorable"
        }
    ]

let sizedFindingsTests =
    // the sized findings [D:sized-findings]: the read side's weir shapes
    // (fragments — FParsec never touches these but the fragment habit
    // holds), Dir.copy's refusals, withQuery's data-last order
    testList "sized findings: fs read shapes, Dir.copy, withQuery [D:sized-findings]" [
        test "File.read failure shapes are weir's, the path named — never raw .NET" {
            let m1 =
                try
                    run "File.read \"/nope/missing.txt\" |> Seq.length" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m1 "File.read: no such file:" "not-found shape"
            Expect.stringContains m1 "missing.txt" "the path named (platform's own spelling)"
            Expect.isFalse (m1.Contains "Could not find") "no raw .NET words"

            let dd =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            let m2 =
                try
                    run $"File.read \"{dd}\" |> Seq.length" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m2 "File.read:" "the member named"
            Expect.stringContains m2 "is a directory" "is-a-directory"
        }
        // B33: a dangling link and a directory get their own words
        test "dangling symlinks and directories are named as such by the File readers" {
            withTempDir "weir-b33" (fun root ->
                let w = weirPath root
                Directory.CreateDirectory(Path.Combine(root, "d")) |> ignore

                let msg (src: string) =
                    try
                        run src |> ignore
                        ""
                    with e ->
                        e.Message

                for call in [ "File.size"; "File.delete" ] do
                    Expect.stringContains (msg $"{call} \"{w}/d\"") "is a directory" $"{call} on a directory"

                if not (System.OperatingSystem.IsWindows()) then
                    File.CreateSymbolicLink(Path.Combine(root, "dang"), "nowhere") |> ignore
                    File.WriteAllText(Path.Combine(root, "f"), "abc")
                    File.CreateSymbolicLink(Path.Combine(root, "fl"), "f") |> ignore
                    Expect.equal (run $"File.exists \"{w}/dang\"") (VBool true) "the link itself exists"

                    for src in
                        [
                            $"File.read \"{w}/dang\" |> Seq.length"
                            $"File.readBytes \"{w}/dang\""
                            $"File.size \"{w}/dang\""
                            $"File.copy \"{w}/dang\" \"{w}/x\""
                        ] do
                        let m = msg src
                        Expect.stringContains m "dangling symlink" src
                        Expect.isFalse (m.Contains "Could not find") "no raw .NET words"

                    Expect.equal (run $"File.size \"{w}/fl\"") (VSize 3L) "a link's size is its target's")
        }
        test "File.write to a missing parent names the directory (the delete side's shape)" {
            let m =
                try
                    run "File.write \"/nope/deep/x.txt\" [\"a\"]" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m "File.write: no such directory:" "the shape"
            Expect.stringContains m "deep" "the directory named"
        }
        test "Dir.copy refuses per the family rule; recursive by nature" {
            let m1 =
                try
                    run "Dir.copy \"/nope\" \"/tmp/x-sfx\"" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m1 "Dir.copy: no such directory:" "the shape"
            Expect.stringContains m1 "nope" "the source named"

            let src =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            let dst =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            let m2 =
                try
                    run $"Dir.copy \"{src}\" \"{dst}\"" |> ignore
                    ""
                with e ->
                    e.Message

            Expect.stringContains m2 "Dir.copy: destination exists:" "the family overwrite rule"
        }
        // B26: links copy as links (dangling ones too), and a
        // destination inside the source refuses instead of recursing
        test "Dir.copy copies symlinks as links and refuses a destination inside the source" {
            if not (System.OperatingSystem.IsWindows()) then
                withTempDir "weir-dircopy" (fun root ->
                    let src = Path.Combine(root, "src")
                    Directory.CreateDirectory(Path.Combine(src, "sub")) |> ignore
                    File.WriteAllText(Path.Combine(src, "sub", "f"), "a")
                    File.CreateSymbolicLink(Path.Combine(src, "dl"), "sub") |> ignore
                    File.CreateSymbolicLink(Path.Combine(src, "dang"), "nowhere") |> ignore
                    Directory.CreateSymbolicLink(Path.Combine(root, "lnk"), src) |> ignore
                    let w = weirPath root
                    run $"Dir.copy \"{w}/src\" \"{w}/dst\"" |> ignore
                    Expect.equal (FileInfo(Path.Combine(root, "dst", "dl")).LinkTarget) "sub" "a dir link stays a link"

                    Expect.equal
                        (FileInfo(Path.Combine(root, "dst", "dang")).LinkTarget)
                        "nowhere"
                        "a dangling link copies"

                    Expect.equal (File.ReadAllText(Path.Combine(root, "dst", "sub", "f"))) "a" "files copy"

                    for (s, d) in [ "src", "src/inner"; "lnk", "src/inner"; "src", "lnk/inner" ] do
                        let m =
                            try
                                run $"Dir.copy \"{w}/{s}\" \"{w}/{d}\"" |> ignore
                                ""
                            with e ->
                                e.Message

                        Expect.stringContains m "is inside the source" $"{s} -> {d} refuses"

                    Expect.isFalse (Directory.Exists(Path.Combine(src, "inner"))) "nothing was copied into the source")
        }
        // EXDEV: a move across filesystems copies the tree, then deletes
        // the source [D:dir-move-cross-device]. /dev/shm is its own mount on
        // Linux; where the temp dir shares it, rename runs — same outcome
        test "Dir.move across filesystems lands the tree and removes the source" {
            if not (System.OperatingSystem.IsWindows()) && Directory.Exists "/dev/shm" then
                withTempDir "weir-xdev" (fun root ->
                    let src = Path.Combine(root, "tree")
                    Directory.CreateDirectory(Path.Combine(src, "sub")) |> ignore
                    File.WriteAllText(Path.Combine(src, "sub", "f"), "a")
                    File.CreateSymbolicLink(Path.Combine(src, "l"), "sub/f") |> ignore
                    let dst = $"/dev/shm/weir-xdev-{System.Guid.NewGuid():N}"

                    try
                        run $"Dir.move \"{weirPath src}\" \"{dst}\"" |> ignore
                        Expect.equal (File.ReadAllText(Path.Combine(dst, "sub", "f"))) "a" "files arrive"
                        Expect.equal (FileInfo(Path.Combine(dst, "l")).LinkTarget) "sub/f" "a link stays a link"
                        Expect.isFalse (Directory.Exists src) "the source is gone"
                    finally
                        if Directory.Exists dst then
                            Directory.Delete(dst, true))
        }
        // a source link moves as the link, as the rename would
        // [D:dir-move-cross-device]: nothing is read through it, so a
        // destination inside its target cannot copy the tree into itself
        test "Dir.move across filesystems moves a source link as the link" {
            if not (System.OperatingSystem.IsWindows()) && Directory.Exists "/dev/shm" then
                withTempDir "weir-xdev-link" (fun root ->
                    let target = $"/dev/shm/weir-xdev-t-{System.Guid.NewGuid():N}"
                    Directory.CreateDirectory(Path.Combine(target, "sub")) |> ignore
                    File.WriteAllText(Path.Combine(target, "sub", "f"), "a")
                    let link = Path.Combine(root, "link")
                    Directory.CreateSymbolicLink(link, target) |> ignore
                    let dst = Path.Combine(target, "inner")

                    try
                        run $"Dir.move \"{weirPath link}\" \"{dst}\"" |> ignore

                        Expect.equal
                            (DirectoryInfo(dst).LinkTarget)
                            target
                            "the destination is the link, its target verbatim"

                        Expect.isFalse (Directory.Exists link) "the source link is gone"
                        Expect.equal (File.ReadAllText(Path.Combine(target, "sub", "f"))) "a" "the target is untouched"

                        Expect.sequenceEqual
                            (Directory.GetFileSystemEntries target |> Array.sort)
                            [| dst; Path.Combine(target, "sub") |]
                            "nothing was copied into the target"
                    finally
                        Directory.Delete(target, true))
        }
        // directories keep their permission bits through the copy, as
        // `cp -R` / `mv` keep them; the move keeps directory mtimes too
        // [D:dir-copy-modes]
        test "Dir.copy and Dir.move across filesystems keep directory modes" {
            if not (System.OperatingSystem.IsWindows()) && Directory.Exists "/dev/shm" then
                withTempDir "weir-dirmodes" (fun root ->
                    let mk (top: string) =
                        Directory.CreateDirectory(Path.Combine(top, "priv")) |> ignore
                        File.WriteAllText(Path.Combine(top, "priv", "f"), "a")
                        File.SetUnixFileMode(Path.Combine(top, "priv"), enum<UnixFileMode> 0o700)
                        File.SetUnixFileMode(top, enum<UnixFileMode> 0o750)

                    let src = Path.Combine(root, "src")
                    mk src
                    let old = System.DateTime(2020, 1, 2, 3, 4, 5, System.DateTimeKind.Utc)
                    Directory.SetLastWriteTimeUtc(Path.Combine(src, "priv"), old)
                    let w = weirPath root
                    run $"Dir.copy \"{w}/src\" \"{w}/cp\"" |> ignore
                    Expect.equal (int (File.GetUnixFileMode(Path.Combine(root, "cp")))) 0o750 "the root keeps 0750"

                    Expect.equal
                        (int (File.GetUnixFileMode(Path.Combine(root, "cp", "priv"))))
                        0o700
                        "a private dir stays 0700"

                    let dst = $"/dev/shm/weir-dirmodes-{System.Guid.NewGuid():N}"

                    try
                        run $"Dir.move \"{w}/src\" \"{dst}\"" |> ignore
                        Expect.equal (int (File.GetUnixFileMode dst)) 0o750 "the moved root keeps 0750"

                        Expect.equal
                            (int (File.GetUnixFileMode(Path.Combine(dst, "priv"))))
                            0o700
                            "the moved private dir stays 0700"

                        Expect.equal
                            (Directory.GetLastWriteTimeUtc(Path.Combine(dst, "priv")))
                            old
                            "the move keeps the dir mtime"

                        Expect.isFalse (Directory.Exists src) "the source is gone"
                    finally
                        if Directory.Exists dst then
                            Directory.Delete(dst, true))
        }
        // a symlink moves as the LINK across filesystems, its target text
        // verbatim, dangling included [D:file-move-link]
        test "File.move across filesystems moves a link as the link" {
            if not (System.OperatingSystem.IsWindows()) && Directory.Exists "/dev/shm" then
                withTempDir "weir-fmove-link" (fun root ->
                    File.WriteAllText(Path.Combine(root, "t"), "data")
                    File.CreateSymbolicLink(Path.Combine(root, "l"), "t") |> ignore
                    File.CreateSymbolicLink(Path.Combine(root, "dang"), "nowhere") |> ignore
                    let w = weirPath root
                    let tag = System.Guid.NewGuid().ToString "N"
                    let d1, d2 = $"/dev/shm/weir-fl-{tag}", $"/dev/shm/weir-fd-{tag}"

                    try
                        run $"File.move \"{w}/l\" \"{d1}\"" |> ignore
                        run $"File.move \"{w}/dang\" \"{d2}\"" |> ignore
                        Expect.equal (FileInfo(d1).LinkTarget) "t" "a link, its target text verbatim"
                        Expect.equal (FileInfo(d2).LinkTarget) "nowhere" "a dangling link moves too"

                        Expect.isFalse
                            (File.Exists(Path.Combine(root, "l")) || FileInfo(Path.Combine(root, "l")).Exists)
                            "the source link is gone"

                        Expect.isNull (FileInfo(Path.Combine(root, "dang")).LinkTarget) "the dangling source is gone"
                        Expect.equal (File.ReadAllText(Path.Combine(root, "t"))) "data" "the target is untouched"
                        // same filesystem: still the rename
                        run $"File.move \"{d1}\" \"{d1}-2\"" |> ignore
                        Expect.equal (FileInfo(d1 + "-2").LinkTarget) "t" "a same-fs move renames the link"
                    finally
                        for f in [ d1; d1 + "-2"; d2 ] do
                            File.Delete f)
        }
        test "Http.withQuery is data-last: the url pipes in" {
            Expect.equal
                (run "\"http://x/s\" |> Http.withQuery [(\"q\", \"a b\")]")
                (VStr "http://x/s?q=a%20b")
                "url |> withQuery params"
        }
    ]

let sizeTests =
    let perr input =
        match Weir.Parser.parseExpr input with
        | Error msg -> msg
        | Ok e -> failtest $"expected a parse rejection, got {show e}"

    testList "Size [D:size]" [
        test "literals: the binary suffixes land in bytes; prefix minus folds" {
            Expect.equal (run "512B") (VSize 512L) ""
            Expect.equal (run "1KiB") (VSize 1024L) ""
            Expect.equal (run "2MiB") (VSize 2097152L) ""
            Expect.equal (run "1GiB") (VSize 1073741824L) ""
            Expect.equal (run "-1MiB") (VSize -1048576L) "negatives fold into the literal"
        }
        test "command-mode 10MiB stays an argv WORD (the sharpest pin, inherited)" {
            expectCmd "echo 10MiB" "(cmd echo \"10MiB\")"
        }
        test "show renders binary units, one TRUNCATED decimal above bytes, none for bytes" {
            Expect.equal (run "show 847B") (VStr "847 B") "bytes take no decimal"
            Expect.equal (run "show 1536KiB") (VStr "1.5 MiB") ""
            Expect.equal (run "show (10MiB + 512KiB)") (VStr "10.5 MiB") ""
            Expect.equal (run "show 2MiB") (VStr "2 MiB") "a zero decimal drops"
            Expect.equal (run "show (0B - 1536KiB)") (VStr "-1.5 MiB") "one leading sign"
        }
        test "show/parse round-trips: text-value-text, and the representable value cases" {
            Expect.equal
                (run "Size.parse (show 1536KiB) == 1536KiB")
                (VBool true)
                "value -> text -> value (representable)"

            Expect.equal (run "show (Size.parse \"1.5 MiB\")") (VStr "1.5 MiB") "text -> value -> text"
            Expect.equal (run "Size.toBytes (Size.parse \"1.5MiB\")") (VInt 1572864L) "the blessed non-round case"
        }
        test "parse reads FOREIGN units: SI at powers of ten (the asymmetry ruling)" {
            Expect.equal (run "Size.toBytes (Size.parse \"1MB\")") (VInt 1000000L) "MB is 10^6 in parse"
            Expect.equal (run "Size.toBytes (Size.parse \"1KB\")") (VInt 1000L) ""

            let ex = Expect.throwsC (fun () -> run "Size.parse \"1.3GiB\"" |> ignore) id

            Expect.stringContains ex.Message "sub-byte precision" "the sub-ms rule's mirror"
        }
        test "the closed algebra; Size / Size names BOTH alternatives" {
            Expect.equal (run "2MiB / 2") (VSize 1048576L) ""
            Expect.equal (run "1KiB * 3") (VSize 3072L) ""

            Expect.stringContains
                (checkErr "1MiB / 512KiB").Message
                "Size.toBytes a / Size.toBytes b gives the integer ratio (Float.ofInt each for a fraction)"
                ""

            Expect.stringContains (checkErr "1MiB + 5").Message "expected Size, got int" "no implicit bytes"
        }
        test "the class trio admits Size; the hole renders with ZERO interp edits (property, 2nd confirmation)" {
            Expect.equal (run "1MiB == 1024KiB") (VBool true) "Eq"
            Expect.equal (run "[3MiB; 1MiB; 2MiB] |> Seq.sortBy (fun s -> s) |> Seq.head") (VSize 1048576L) "Ord"
            expectValue "$\"cap {2MiB}\"" (VStr "cap 2 MiB")
        }
        test "the teaching rejections, each located" {
            Expect.stringContains
                (perr "1MB")
                "'MB' is ambiguous (10^n in SI, 2^n in common usage)"
                "SI refuses to guess"

            Expect.stringContains (perr "1MB") "(MiB)" "names the fix (FParsec wraps the line)"
            Expect.stringContains (perr "1.5MiB") "decimal size literals do not exist" ""
            Expect.stringContains (perr "1GiB512MiB") "sizes take a single unit" "nobody writes compound sizes"
        }
        test "a unit-suffixed literal past int64 (bytes / ms) is a parse error, never a wrap" {
            Expect.stringContains (perr "9999999999TiB") "size literal out of range" "TiB overflow"
            Expect.stringContains (perr "9999999999999999h") "duration literal out of range" "h overflow"
            Expect.stringContains (perr "99999999999999999999ms") "duration literal out of range" "digits past int64"
            Expect.equal (run "8388607TiB") (VSize(8388607L * 1024L * 1024L * 1024L * 1024L)) "the largest TiB fits"
        }
        test "File.size returns Size — the one intended break, and it compares" {
            let d =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            run $"[\"12345\"] |> File.write \"{d}/f.txt\"" |> ignore
            Expect.equal (run $"File.size \"{d}/f.txt\" > 5B") (VBool true) "direct comparison"

            Expect.stringContains
                (checkErr $"File.size \"{d}/f.txt\" > 6").Message
                "expected Size, got int"
                "the break errors legibly"

            run $"Dir.deleteAll \"{d}\"" |> ignore
        }
        test "a unit-type district splice teaches its exits; the cascade never leaks a hole name" {
            let terrs =
                let p = tempPath "weir-leak" + ".weir"

                System.IO.File.WriteAllLines(
                    p,
                    [ "let d = 5s"; "let m = yaml"; "    wait: $d"; "m |> to yaml |> print" ]
                )

                try
                    let ds = diagsIn p (List.ofArray (System.IO.File.ReadAllLines p))

                    ds |> List.filter (fun d -> d.Severity = "error")
                finally
                    System.IO.File.Delete p

            match terrs with
            | [ d ] ->
                Expect.stringContains d.Message "got Duration — convert explicitly (Duration.toMillis" "the splice hint"
            | other -> failtest $"expected ONE error (the cascade is suppressed), got {other.Length}"
        }
        test "the JSON and YAML parks each name Size.toBytes" {
            let e = env |> declare "type SJ = { sz: Size }"

            match Weir.Check.typecheck e (parse "[{ sz = 1MiB }] |> to json") with
            | Error terr ->
                // a field's park names the codec first [D:wire-codecs]; the
                // explicit conversion stays in the message
                Expect.stringContains terr.Message "Size is not representable in JSON" ""
                Expect.stringContains terr.Message "Size.toBytes into an int field, or show for a string" ""
            | Ok _ -> failtest "expected the park"

            match Weir.Check.typecheck e (parse "[{ sz = 1MiB }] |> to yaml") with
            | Error terr -> Expect.stringContains terr.Message "Size is not representable in yaml" ""
            | Ok _ -> failtest "expected the yaml park"

            // and Duration's yaml park, tailored to match (it had only
            // the generic rejection — the seam this session's misfit
            // report surfaced)
            let e2 = env |> declare "type DY = { d: Duration }"

            match Weir.Check.typecheck e2 (parse "[{ d = 5s }] |> to yaml") with
            | Error terr ->
                Expect.stringContains terr.Message "Duration is not representable in yaml" ""
                Expect.stringContains terr.Message "Duration.toMillis into an int field, or show for a string" ""
            | Ok _ -> failtest "expected Duration's yaml park"
        }
        test "[<Default 10MiB>] end to end (attrArgLit's third reminder)" {
            let scriptEnv = {
                env with
                    Modules = env.Modules |> Map.add "Self" Weir.Statements.selfMembers
            }

            let e = scriptEnv |> declare "type SzCli = { [<Default 10MiB>] max: Size }"

            match Weir.Check.typecheck e (parse "Args.load SzCli") with
            | Ok te -> Expect.equal (formatTy te.Ty) "SzCli" ""
            | Error terr -> failtest $"expected Ok, got {terr.Message}"
        }
    ]

let stopRequestTests =
    testList "the stop request's state machine [D:stop-request]" [
        test "no stop-aware construct live: every signal is the teardown" {
            let m = Weir.Session.StopMachine()
            Expect.equal (m.Signal()) Weir.Session.Teardown "first signal, nothing live"
            Expect.equal (m.Signal()) Weir.Session.Teardown "second signal, nothing live"
            Expect.isFalse m.Requested "no request recorded"
            Expect.isFalse m.Token.IsCancellationRequested "no wait woken"
        }
        test "one live: the first signal is the request (waits woken), the second the teardown" {
            let m = Weir.Session.StopMachine()
            m.Enter()
            let t = m.Token
            Expect.equal (m.Signal()) Weir.Session.StopRequested "the first signal requests the stop"
            Expect.isTrue m.Requested "the request is set"
            Expect.isTrue t.IsCancellationRequested "the stop-aware waits wake"
            Expect.equal (m.Signal()) Weir.Session.Teardown "a second signal is today's teardown"
            Expect.equal (m.Signal()) Weir.Session.Teardown "and every one after"
            m.Exit()
        }
        test "the request is sticky: after the construct ends it stays set, and a later one's signal tears down" {
            let m = Weir.Session.StopMachine()
            m.Enter()
            m.Signal() |> ignore
            m.Exit()
            Expect.isTrue m.Requested "sticky past the construct [D:stop-request]"
            Expect.equal m.Depth 0 "nothing live"
            Expect.equal (m.Signal()) Weir.Session.Teardown "no construct live: the teardown"
            m.Enter()
            Expect.isTrue m.Token.IsCancellationRequested "a later construct sees the request at once"
            Expect.equal (m.Signal()) Weir.Session.Teardown "already requested: the teardown"
            m.Exit()
        }
        test "nested and parallel constructs share one request" {
            let m = Weir.Session.StopMachine()
            m.Enter()
            m.Enter()
            Expect.equal m.Depth 2 "two live"
            Expect.equal (m.Signal()) Weir.Session.StopRequested "one request for both"
            m.Exit()
            Expect.equal (m.Signal()) Weir.Session.Teardown "the request stays one-shot while the outer runs"
            m.Exit()
            m.Exit()
            Expect.equal m.Depth 0 "an extra exit never goes negative"
        }
        test "Clear (the REPL's entry end) forgets the request; the next construct gets a fresh token" {
            let m = Weir.Session.StopMachine()
            m.Enter()
            let t1 = m.Token
            m.Signal() |> ignore
            m.Exit()
            m.Clear()
            Expect.isFalse m.Requested "cleared [D:repl-stop-request]"
            Expect.isTrue t1.IsCancellationRequested "the old token stays cancelled"
            Expect.isFalse m.Token.IsCancellationRequested "a fresh token"
            m.Enter()
            Expect.equal (m.Signal()) Weir.Session.StopRequested "a new construct's first signal is a request again"
            m.Exit()
        }
        test "the REPL's stop-request ^C goes only to a child the terminal's did not reach [D:repl-stop-request]" {
            let target = Weir.Session.interruptTarget
            Expect.equal (target 200 100 100 (Some 100)) None "in the terminal's foreground group: never sent twice"
            Expect.equal (target 200 200 100 (Some 100)) (Some -200) "a setsid child leads its own group: the group"
            Expect.equal (target 200 300 100 (Some 100)) (Some 200) "in a group it does not lead: the child alone"

            Expect.equal
                (target 200 100 100 (Some 50))
                (Some 200)
                "in weir's group, not the foreground: the child, never weir's group"

            Expect.equal (target 200 200 100 None) (Some -200) "no foreground group to compare: its own group"
            Expect.equal (target 200 -1 100 (Some 100)) (Some 200) "a pgid weir cannot read: the child alone"
        }
    ]

let retryPollTests =
    let sx (input: string) = show (parse input)

    testList "retry / poll [D:retry-poll]" [
        test "the desugar targets the INTERNAL key; sugar and manual spelling are VALUE-equivalent [D:desugar-capture]" {
            // the old byte-identical AST pin moved by design: the sugar
            // now references |retryDefaults (un-shadowable), the manual
            // spelling references Retry.defaults (the user's own name).
            // Equivalence is guaranteed by both naming the same object
            // (pinned below by reference) — pin both sexpr forms:
            Expect.equal
                (sx "retry attempts=5 delay=30s (1 == 1)")
                "(retry {|retryDefaults with attempts = 5; delay = 30s} (== 1 1))"
                "the sugar's form"

            Expect.equal
                (sx "retry { Retry.defaults with attempts = 5; delay = 30s } (1 == 1)")
                "(retry {Retry.defaults with attempts = 5; delay = 30s} (== 1 1))"
                "the manual form stays writable, unchanged"

            // behavioral equivalence, both members
            expectValue "retry attempts=2 delay=0ms (1 + 1) until r (r == 2)" (VInt 2L)
            expectValue "retry { Retry.defaults with attempts = 2; delay = 0ms } (1 + 1) until r (r == 2)" (VInt 2L)
        }
        test "the public name and the internal key are the SAME OBJECT (a divergence would out-bug the bug)" {
            Expect.isTrue
                (obj.ReferenceEquals(
                    Weir.Builtins.valueEnv.Value["|retryDefaults"],
                    Weir.Builtins.valueEnv.Value["Retry.defaults"]
                ))
                "Retry"

            Expect.isTrue
                (obj.ReferenceEquals(
                    Weir.Builtins.valueEnv.Value["|pollDefaults"],
                    Weir.Builtins.valueEnv.Value["Poll.defaults"]
                ))
                "Poll"
        }
        test "the CAPTURE is a non-event: the showcase's original union beside the sugar [D:desugar-capture]" {
            let e = env |> declare "type CaptV = CPass of int | Retry of string * int"

            match Weir.Check.typecheck e (parse "retry attempts=2 delay=0ms (1 == 1)") with
            | Ok te -> Expect.equal te.Ty TUnit "the sugar survives a user constructor named Retry"
            | Error terr -> failtest $"captured: {terr.Message}"

            // the user's own reference to the shadowed name stays an
            // ordinary error — the fix protects the desugar only
            match Weir.Check.typecheck e (parse "{ Retry.defaults with attempts = 2 }") with
            | Error terr -> Expect.stringContains terr.Message "only records have fields" "their name, their shadow"
            | Ok _ -> failtest "expected ordinary shadowing"
        }
        test "a built-in type name cannot be redeclared (the TYPE half of the capture)" {
            for bad in
                [
                    "type Retry = { x: int }"
                    "type Poll = { x: int }"
                    "type Option = { x: int }"
                ] do
                let terr = env |> declErr bad
                Expect.stringContains terr.Message "is a built-in type — pick another name" bad
        }
        test "every Seq-referencing desugar survives a user constructor named Seq [D:desugar-capture]" {
            let e = env |> declare "type SeqCap = Seq of int"

            for src in
                [
                    "for x in [1; 2] do print (show x)"
                    "[for v in [3; 4] -> v * 2]"
                    "[1..3]"
                    "[9; 8][0]"
                ] do
                match Weir.Check.typecheck e (parse src) with
                | Ok _ -> ()
                | Error terr -> failtest $"'{src}' captured: {terr.Message}"
        }
        test "ruling 5's table, all four rows" {
            // bool body, no until: yields unit — legal as a bare statement
            Expect.equal (checkOk "retry attempts=2 delay=0ms (1 == 1)").Ty TUnit "row 1"

            // bool body with until: rejected naming the fix and the
            // alternative
            let terr = checkErr "retry attempts=2 delay=0ms (1 == 1) until r r"
            Expect.stringContains terr.Message "it IS the predicate — drop the until segment" "row 2 fix"
            Expect.stringContains terr.Message "a different condition belongs in the body" "row 2 alternative"

            // value body with until: yields 'a
            Expect.equal (checkOk "retry attempts=2 delay=0ms (1 + 1) until r (r > 0)").Ty TInt "row 3"

            // value body without until: rejected for having no predicate
            let terr2 = checkErr "retry attempts=2 delay=0ms (1 + 1)"
            Expect.stringContains terr2.Message "needs a bool body (the body IS the predicate)" "row 4"
            Expect.stringContains terr2.Message "add `until r` to bind the value" "row 4 names the fix"
        }
        test "retry yields the successful attempt's value; poll the same (they differ by BOUND)" {
            expectValue "retry attempts=3 delay=0ms (2 + 2) until r (r == 4)" (VInt 4L)
            expectValue "poll timeout=2s interval=1ms (2 + 2) until r (r == 4)" (VInt 4L)
        }
        test "poll refuses interval=0s, as it refuses timeout=0s — retry's delay=0ms stays" {
            let ex =
                Expect.throwsC (fun () -> run "poll timeout=1s interval=0s (1 == 2)" |> ignore) id

            Expect.stringContains ex.Message "poll: interval must be positive" "named"
            Expect.isFalse (ex.Message.Contains "attempt(s)") "refused up front, never looped"
            expectValue "retry attempts=2 delay=0ms (1 == 1)" VUnit
        }
        test "exhaustion raises with attempts AND elapsed; poll with its timeout" {
            let ex =
                Expect.throwsC (fun () -> run "retry attempts=3 delay=0ms (1 == 2)" |> ignore) id

            Expect.stringContains ex.Message "retry: exhausted 3 attempt(s) over" "attempts + elapsed"

            // a timeout ends it early: the attempts actually made, never the configured count
            let ex1 =
                Expect.throwsC
                    (fun () -> run "retry attempts=50 delay=100ms timeout=(Some 250ms) (1 == 2)" |> ignore)
                    id

            Expect.stringContains ex1.Message "retry: timed out after" "the timeout named"
            Expect.stringContains ex1.Message " attempt(s) made of 50 allowed)" "made of configured"
            Expect.isFalse (ex1.Message.Contains "exhausted 50") "not the configured count"

            let ex2 =
                Expect.throwsC (fun () -> run "poll timeout=40ms interval=10ms (1 == 2)" |> ignore) id

            Expect.stringContains ex2.Message "poll: timed out after" ""
        }
        test "unbounded is UNREPRESENTABLE; the floor validates: attempts=0 raises naming it" {
            let ex =
                Expect.throwsC (fun () -> run "retry attempts=0 delay=0ms (1 == 1)" |> ignore) id

            Expect.stringContains ex.Message "attempts must be at least 1, got 0" ""

            let ex2 =
                Expect.throwsC (fun () -> run "retry attempts=2 delay=(0s - 1s) (1 == 1) until r (r > 9)" |> ignore) id

            Expect.stringContains ex2.Message "" "negative delay raises"
        }
        test "a duplicate key is a located parse error naming the key" {
            match Weir.Parser.parseExpr "retry attempts=2 attempts=3 delay=0ms (1 == 1)" with
            | Error m -> Expect.stringContains m "duplicate key 'attempts' — each option is given once" ""
            | Ok e -> failtest $"expected rejection, got {show e}"
        }
        test "until's binder is scoped to the PREDICATE segment only" {
            let terr = checkErr "retry attempts=2 delay=0ms (r + 1) until r (r > 0)"
            Expect.stringContains terr.Message "unbound variable 'r'" "the body cannot see the binder"
        }
        test "computed options work: the underlying record form is writable" {
            expectValue "let fast = { Retry.defaults with attempts = 2; delay = 0ms } in retry fast (1 == 1)" VUnit
        }
        test "poll's timeout CANCELS a pending interval instead of waiting it out" {
            let sw = System.Diagnostics.Stopwatch.StartNew()

            Expect.throwsC (fun () -> run "poll timeout=80ms interval=10s (1 == 2)" |> ignore) id
            |> ignore

            sw.Stop()

            Expect.isTrue
                (sw.ElapsedMilliseconds < 2000L)
                $"waited {sw.ElapsedMilliseconds}ms — the 10s interval was not cancelled"
        }
        // every valid Duration waits [D:duration]: past the timers'
        // Int32.MaxValue ms the wait runs in slices, never a fake timeout
        test "a delay/interval past Int32.MaxValue ms really waits until the timeout" {
            for src, msg in
                [
                    "retry attempts=2 delay=2000h timeout=(Some 300ms) (1 == 2)", "retry: timed out after"
                    "poll timeout=300ms interval=2000h (1 == 2)", "poll: timed out"
                ] do
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let ex = Expect.throwsC (fun () -> run src |> ignore) id
                sw.Stop()
                Expect.stringContains ex.Message msg src
                Expect.isTrue (sw.ElapsedMilliseconds >= 250L) $"{src}: gave up after {sw.ElapsedMilliseconds}ms"
                Expect.isTrue (sw.ElapsedMilliseconds < 5000L) $"{src}: the timeout did not cut the wait"
        }
        test "a timeout past Int32.MaxValue ms is a valid ceiling" {
            expectValue "poll timeout=1440h interval=1ms (1 == 1)" VUnit
            expectValue "retry attempts=2 delay=1ms timeout=(Some 1440h) (1 == 1)" VUnit
        }
    ]

let tasksUnderneathTests =
    testList "Parallel fan-out for I/O-bound arms [D:tasks-underneath]" [
        test "input order is preserved (the contract, unchanged)" {
            expectValue
                "[1; 2; 3; 4; 5] |> Seq.pmap (fun x -> x * 10) |> Seq.freeze"
                (VSeq [ VInt 10L; VInt 20L; VInt 30L; VInt 40L; VInt 50L ])
        }
        test "every arm runs; the FIRST error by INPUT ORDER rethrows after the join" {
            let ex =
                Expect.throwsC
                    (fun () ->
                        run
                            "[1; 2; 3] |> Seq.pmap (fun x -> if x > 1 then Float.parse $\"{x}z\" else 0.5) |> Seq.freeze"
                        |> ignore)
                    id

            Expect.stringContains ex.Message "not a float: '2z'" "index 1's error, not index 2's"
        }
        test "128 blocking arms complete in ~two 64-rounds, not sixteen core-rounds" {
            let sw = System.Diagnostics.Stopwatch.StartNew()
            run "[1..128] |> Seq.piter (fun i -> Duration.sleep 100ms)" |> ignore
            sw.Stop()
            // 2 rounds ≈ 200ms; the old ProcessorCount sizing needs
            // 100ms × ceil(128/cores) — ≥ 3200ms on the ≤4-core CI
            // runners, ≥ 800ms on anything ≤ 16 cores. 1500ms keeps a
            // 2x margin below the worst regression floor this pin can
            // meet while giving shared-VM noise real room (a macOS
            // runner stretched the 200ms design to 750ms live)
            Expect.isTrue (sw.ElapsedMilliseconds < 1500L) $"took {sw.ElapsedMilliseconds}ms — the ceiling regressed"
        }
        test "pmapWith takes an explicit ceiling; degree < 1 raises naming the constraint" {
            expectValue
                "[1; 2; 3] |> Seq.pmapWith 2 (fun x -> x + 1) |> Seq.freeze"
                (VSeq [ VInt 2L; VInt 3L; VInt 4L ])

            let ex =
                Expect.throwsC (fun () -> run "[1] |> Seq.pmapWith 0 (fun x -> x) |> Seq.freeze" |> ignore) id

            Expect.stringContains ex.Message "parallel degree must be at least 1" ""
        }
        test "within cd inside piter HOLDS at the raised ceiling (100 concurrent scope forks)" {
            // the load-bearing pin, re-run above ProcessorCount: every
            // arm forks its own cwd, the parent's survives untouched
            let before =
                match run "pwd" with
                | VStr s -> s
                | v -> failtest $"unexpected {v}"

            let scope =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected {v}"

            run $"[1..100] |> Seq.piter (fun i -> within cd \"{scope}\" Log.debug (pwd) ; ())"
            |> ignore

            let after =
                match run "pwd" with
                | VStr s -> s
                | v -> failtest $"unexpected {v}"

            Expect.equal after before "the parent cwd is untouched after 100 forks"
        }
    ]

let seqPfirstTests =
    testList "The race: Seq.pfirst [D:seq-pfirst]" [
        test "the first SUCCESS wins; a failing arm is swallowed by construction" {
            expectValue
                "[1; 2] |> Seq.pfirst (fun n -> match n with | 1 -> fail \"one dies\" ; 0 | _ -> n * 10)"
                (VInt 20L)
        }
        test "a slow loser loses: the call returns on the winner, not the join" {
            let sw = System.Diagnostics.Stopwatch.StartNew()

            expectValue
                "[1; 2] |> Seq.pfirst (fun n -> match n with | 1 -> Duration.sleep 2s ; 999 | _ -> n * 10)"
                (VInt 20L)

            sw.Stop()
            Expect.isTrue (sw.ElapsedMilliseconds < 1500L) $"took {sw.ElapsedMilliseconds}ms — waited for a loser"
        }
        test "ALL arms failed: the first error by INPUT ORDER rethrows, not the first to arrive" {
            let ex =
                Expect.throwsC
                    (fun () ->
                        run
                            "[7; 8] |> Seq.pfirst (fun n -> match n with | 7 -> Duration.sleep 300ms ; fail \"seven dies\" | _ -> fail \"eight dies\")"
                        |> ignore)
                    id

            Expect.stringContains ex.Message "seven dies" "index 0's error, though index 1 failed first"
        }
        test "an empty sequence raises the family shape AND names the guard" {
            let ex = Expect.throwsC (fun () -> run "[] |> Seq.pfirst (fun n -> n)" |> ignore) id
            Expect.stringContains ex.Message "pfirst: empty sequence" "the family shape"
            // the reject-don't-guess full form: there is no tryPfirst, so
            // the message points at the pre-check
            Expect.stringContains ex.Message "guard with Seq.isEmpty" "names the guard"
        }
        test "pfirstWith: degree 1 degrades to sequential fallback; degree < 1 raises" {
            expectValue
                "[1; 2] |> Seq.pfirstWith 1 (fun n -> match n with | 1 -> fail \"one dies\" ; 0 | _ -> n * 100)"
                (VInt 200L)

            let ex =
                Expect.throwsC (fun () -> run "[1] |> Seq.pfirstWith 0 (fun n -> n)" |> ignore) id

            Expect.stringContains ex.Message "parallel degree must be at least 1" ""
        }
    ]

let floatTests =
    let perr input =
        match Weir.Parser.parseExpr input with
        | Error msg -> msg
        | Ok e -> failtest $"expected a parse rejection, got {show e}"

    testList "Floats, finite-only [D:floats]" [
        test "literals: decimals, exponents, prefix minus folds" {
            Expect.equal (run "0.5 + 0.5") (VFloat 1.0) ""
            Expect.equal (run "1e5") (VFloat 100000.0) ""
            Expect.equal (run "1.5e-3") (VFloat 0.0015) ""
            Expect.equal (run "-0.5") (VFloat -0.5) "prefix minus folds into the literal (0 - e would mix types)"
            Expect.equal (run "-30s") (VDur -30000L) "the same fold fixes the Duration gap"
            Expect.equal (run "3 / 2") (VInt 1L) "int division UNCHANGED"
        }
        test "non-finite results RAISE, each naming what happened" {
            let ex = Expect.throwsC (fun () -> run "1e308 * 10.0" |> ignore) id
            Expect.stringContains ex.Message "produced a non-finite float" "overflow named"

            let ex2 = Expect.throwsC (fun () -> run "1.0 / 0.0" |> ignore) id
            Expect.stringContains ex2.Message "float division by zero" ""

            let ex3 = Expect.throwsC (fun () -> run "0.0 / 0.0" |> ignore) id
            Expect.stringContains ex3.Message "float division by zero" "0/0 is the divisor's error, not NaN"
        }
        test "-0.0 is unrepresentable: results normalize" {
            Expect.equal (run "-1.0 * 0.0") (VFloat 0.0) ""
            Expect.equal (run "show (-1.0 * 0.0)") (VStr "0.0") "rendering never shows a signed zero"
            Expect.equal (run "show -0.0") (VStr "0.0") "the literal normalizes too"
            Expect.equal (run "show [-0.0; -0.0e3]") (VStr "[0.0; 0.0]") "inside data and with an exponent"
        }
        test "mixed arithmetic errors naming Float.ofInt" {
            Expect.stringContains
                (checkErr "3 / 2.0").Message
                "needs both sides the same type; wrap the int: Float.ofInt"
                ""

            Expect.stringContains (checkErr "1 + 0.5").Message "Float.ofInt" ""
            Expect.stringContains (checkErr "0.5 < 1").Message "Float.ofInt" ""
            Expect.equal (run "Float.ofInt 3 / 2.0") (VFloat 1.5) "the named spelling works"
        }
        test "Eq excluded: == and its riders teach Float.near" {
            Expect.stringContains
                (checkErr "0.1 == 0.2").Message
                "floats do not join '==' (0.1 + 0.2 is not 0.3); use Float.near a b eps"
                ""

            Expect.stringContains
                (checkErr "[0.5] |> Seq.distinct").Message
                "Float.near"
                "distinct rides the same teaching"

            Expect.stringContains (checkErr "[0.5] |> Seq.contains 0.5").Message "Float.near" ""
            Expect.equal (run "Float.near (0.1 + 0.2) 0.3 1e-9") (VBool true) "the idiom"
        }
        test "a record or union holding a float names Float.near at ==, not 'sequences and functions'" {
            for decl in
                [
                    "type P = { x: float }"
                    "type P = { inner: Q }"
                    "type P = | A of float | B"
                ] do
                let lit =
                    if decl.Contains "|" then "A 1.0"
                    elif decl.Contains "Q" then "{ inner = { y = 1.0 } }"
                    else "{ x = 1.0 }"

                let e =
                    firstErrorIn "feq.weir" [ "type Q = { y: float }"; decl; $"let a = {lit}"; "print $\"{a == a}\"" ]

                Expect.stringContains e.Message "Float.near" decl
                Expect.isFalse (e.Message.Contains "sequences and functions") decl
                Expect.equal e.Code "not-equatable" "the eq diagnostic code"
        }
        test "Ord admitted: total because finite" {
            Expect.equal (run "[3.5; 1.5; 2.5] |> Seq.sortBy (fun x -> x) |> Seq.head") (VFloat 1.5) ""
            Expect.equal (run "0.5 < 1.5") (VBool true) ""
        }
        test "show is shortest-round-trippable; 1.0 keeps its decimal; parse round-trips" {
            Expect.equal (run "show 1.0") (VStr "1.0") "an integral float never renders as an int"
            Expect.equal (run "show 0.1") (VStr "0.1") "shortest form"

            Expect.equal
                (run "Float.near (Float.parse (show 1.5e-3)) 1.5e-3 0.0")
                (VBool true)
                "value -> text -> value, exact"

            Expect.equal (run "show (Float.parse \"87.5\")") (VStr "87.5") "text -> value -> text"

            let ex = Expect.throwsC (fun () -> run "Float.parse \"NaN\"" |> ignore) id
            Expect.stringContains ex.Message "not a finite float" "boundaries reject non-finite too"
        }
        test "Duration.measure and Completed.elapsed time a run on the monotonic clock [D:measure]" {
            skipOnWindows ()

            Expect.equal
                (runReal
                    "let (v, took) = Duration.measure (fun () -> $(sh -c \"sleep 0.2; echo hi\" | text)) in (v, took >= 200ms && took < 30s)")
                (VTuple [ VStr "hi"; VBool true ])
                "the result rides with the time"

            Expect.equal
                (runReal
                    "let r = $(sh -c \"sleep 0.2; exit 3\" | complete) in (r.exitCode, r.elapsed >= 200ms && r.elapsed < 30s)")
                (VTuple [ VInt 3L; VBool true ])
                "a command's own time"

            Expect.equal
                (runReal "let r = $(sh -c \"sleep 0.2; echo a\" | cat | complete) in r.elapsed >= 200ms")
                (VBool true)
                "a chain's time is the whole chain's"

            Expect.equal
                (runReal
                    "let (xs, took) = Duration.measure (fun () -> $(sh -c \"sleep 2\")) in (Seq.length xs, took < 1s)")
                (VTuple [ VInt 0L; VBool true ])
                "a lazy capture is not forced: the time is the function's"

            Expect.equal
                (runReal "$(sh -c \"exit 0\" | complete) |> to json |> Seq.head |> Str.contains \"\\\"elapsed\\\":\"")
                (VBool true)
                "elapsed crosses json as integer milliseconds"
        }
        test "Duration.toSeconds is lossless; Duration internals stay integer" {
            Expect.equal (run "Duration.toSeconds 2500ms") (VFloat 2.5) "the defect that prompted the session"
            Expect.equal (run "Duration.toSeconds 1ms") (VFloat 0.001) ""
            Expect.equal (run "Duration.parse \"2.5s\"") (VDur 2500L) "duration text still parses on the integer path"
        }
        test "the hole renders with ZERO interpolation edits (the interp-show property, confirmed)" {
            expectValue "$\"pct={100.0 * 7.0 / 8.0}\"" (VStr "pct=87.5")
        }
        test "the literal teachings, each located" {
            Expect.stringContains (perr "1.") "float literals need digits on both sides of the point (write 1.0)" ""
            Expect.stringContains (perr "(.5)") "float literals need a digit before the point (write 0.5)" ""
            Expect.stringContains (perr "1e999") "float literal out of range" ""

            Expect.stringContains (perr "2.5s") "decimal duration literals do not exist" "the duration teaching stands"
        }
        test "other notations glued to a literal are named, not applied" {
            Expect.stringContains (perr "0x10") "int literals are decimal only" "hex"
            Expect.stringContains (perr "0x10") "write 16" "the decimal value is computed"
            Expect.stringContains (perr "1_000") "no digit separators — write 1000" "separators"
            Expect.stringContains (perr "1.5.2") "one decimal point at most" "version text"
            Expect.stringContains (perr "1.5.2") "(\"1.5.2\")" "the quoted spelling"
        }
        test "a literal's optional unit/exponent tails never reach an Expecting list" {
            let msg = perr "(1"
            Expect.stringContains msg "Expecting:" "a plain expecting list"

            for leaked in [ "'KiB'"; "'ms'"; "'TB'"; "'e'"; "'<'" ] do
                Expect.isFalse (msg.Contains leaked) $"{leaked} leaked into: {msg}"

            Expect.equal (run "[1..3] |> Seq.length") (VInt 3L) "ranges unaffected"
            Expect.equal (run "1.5e2") (VFloat 150.0) "exponent unaffected"
        }
        test "ranges and field shorthand keep their dots" {
            Expect.equal (run "[1..10] |> Seq.length") (VInt 10L) "1.. stays a range"

            Expect.equal
                (formatTy (checkOk "fun p -> p.name").Ty |> fun s -> s.Contains "name")
                true
                "accessor unaffected"
        }
        test "print takes floats; bare toInt is still Str's" {
            Expect.equal (checkOk "print 0.5").Ty TUnit ""
            Expect.equal (run "toInt \"42\"") (VInt 42L) "Float never flattens to bare aliases"
        }
    ]

let seqGapsTests =
    // the Seq-gaps cohort [D:seq-gaps] — every lazy member gets a
    // pull-count pin (the standing rule); every asserting member's
    // message follows Seq.head's shape
    let counted () =
        let pulled = ref 0

        let countingId =
            VBuiltin(fun v ->
                System.Threading.Interlocked.Increment pulled |> ignore
                v)

        pulled, countingId

    // the counting source is Seq.initInfinite id with `id` swapped for a
    // counting identity — called once per element pulled [D:init-infinite]
    let pullPin (label: string) (bound: int) (expr: string) =
        test $"{label} is LAZY (pull-count pin)" {
            let pulled, countingId = counted ()
            runWith [ "id", countingId ] expr |> forceSeq |> ignore
            Expect.isTrue (pulled.Value <= bound) $"{label}: pulled {pulled.Value} (bound {bound})"
        }

    testList "the Seq-gaps cohort [D:seq-gaps]" [
        pullPin "collect" 3 "(Seq.initInfinite id) |> Seq.collect (fun x -> [x; x]) |> Seq.take 3"
        pullPin "concat" 3 "[Seq.initInfinite id] |> Seq.concat |> Seq.take 2"
        pullPin "indexed" 3 "(Seq.initInfinite id) |> Seq.indexed |> Seq.map (fun (i, x) -> i) |> Seq.take 2"
        pullPin "chunkBySize" 4 "(Seq.initInfinite id) |> Seq.chunkBySize 3 |> Seq.take 1 |> Seq.map Seq.freeze"
        pullPin "takeWhile" 4 "(Seq.initInfinite id) |> Seq.takeWhile (fun x -> x < 2)"
        pullPin "skipWhile" 4 "(Seq.initInfinite id) |> Seq.skipWhile (fun x -> x < 2) |> Seq.take 1"
        pullPin "scan" 3 "(Seq.initInfinite id) |> Seq.scan (fun acc x -> acc + x) 0 |> Seq.take 2"
        pullPin "distinctBy" 3 "(Seq.initInfinite id) |> Seq.distinctBy (fun x -> x) |> Seq.take 2"
        pullPin "except" 3 "(Seq.initInfinite id) |> Seq.except [999] |> Seq.take 2"
        test "the receipt pipeline: collect over split, F# semantics" {
            Expect.equal
                (run "[\"a<b\"; \"\"; \"c\"] |> Seq.collect (Str.split \"<\")" |> forceSeq)
                [ VStr "a"; VStr "b"; VStr ""; VStr "c" ]
                "empty inners contribute their (single empty-string) splits; empty OUTER handled below"

            Expect.equal (run "[] |> Seq.collect (fun x -> [x])" |> forceSeq) [] "empty outer is empty"

            Expect.equal
                (run "[[1]; []; [2]] |> Seq.concat" |> forceSeq)
                [ VInt 1L; VInt 2L ]
                "empty inner seqs skipped"
        }
        test "asserting members follow the head message shape; try twins ask" {
            Expect.equal
                (msgOf "[1] |> Seq.where (fun _ -> false) |> Seq.find (fun _ -> true)")
                "find: no matching element"
                ""

            Expect.equal
                (msgOf "[1] |> Seq.where (fun _ -> false) |> Seq.reduce (fun a b -> a)")
                "reduce: empty sequence"
                ""

            Expect.equal (msgOf "[\"a\"] |> Seq.pick Str.tryToInt") "pick: no matching element" ""
            Expect.equal (msgOf "[1] |> Seq.where (fun _ -> false) |> Seq.max") "max: empty sequence" ""
            Expect.equal (msgOf "[1] |> Seq.where (fun _ -> false) |> Seq.average") "average: empty sequence" ""

            Expect.equal
                (msgOf "[1.0] |> Seq.where (fun _ -> false) |> Float.average")
                "Float.average: empty sequence"
                ""

            Expect.equal (msgOf "[1] |> Seq.chunkBySize 0") "chunkBySize: the chunk size must be positive; got 0" ""

            Expect.equal
                (msgOf "Seq.replicate (0 - 1) \"x\" |> Seq.freeze")
                "replicate: the count must be non-negative; got -1"
                ""

            Expect.equal (msgOf "[1; 2] |> Seq.take -1 |> Seq.freeze") "take: the count must be non-negative; got -1" ""
            Expect.equal (msgOf "[1; 2] |> Seq.skip -1 |> Seq.freeze") "skip: the count must be non-negative; got -1" ""
        }
        test "the sum ruling: Seq.sum stays seq<int>; each numeric type owns its own [D:seq-gaps]" {
            Expect.equal (checkOk "[1; 2] |> Seq.average").Ty TFloat "the mean of ints IS a float"
            Expect.equal (run "[1.5; 2.5] |> Float.sum") (VFloat 4.0) ""
            Expect.equal (run "[1KiB; 1KiB; 1KiB] |> Size.average") (VSize 1024L) ""
            Expect.equal (run "[90s; 30s] |> Duration.sum") (VDur 120000L) ""

            let m =
                (Expect.throwsC (fun () -> run "[1e308; 1e308] |> Float.sum" |> ignore) id).Message

            Expect.stringContains m "not finite" "the floats law reaches the sum"
        }
        test "extrema and key-less sorts are Ord-constrained (records refuse)" {
            let m = (checkErr "ls |> Seq.sort").Message
            Expect.stringContains m "cannot be ordered" "the Ord teaching"
            Expect.stringContains (checkErr "ls |> Seq.max").Message "cannot be ordered" ""
            checkOk "ls |> Seq.maxBy (fun r -> r.bytes)" |> ignore
        }
        test "one name per operation: filter and flatMap teach theirs [D:seq-gaps]" {
            // (first joined this family by reversal [D:first-retired] —
            // its teach pins beside take's survival in the strings list)
            Expect.stringContains
                (checkErr "[1] |> Seq.filter (fun _ -> true)").Message
                "'Seq.where'"
                "one name per operation"

            Expect.stringContains
                (checkErr "[1] |> Seq.flatMap (fun x -> [x])").Message
                "'Seq.collect'"
                "the F# spelling wins"
        }
        test "scan emits the seed first; countBy counts in first-seen key order" {
            Expect.equal
                (run "[1; 2] |> Seq.scan (fun acc x -> acc + x) 10" |> forceSeq)
                [ VInt 10L; VInt 11L; VInt 13L ]
                ""

            Expect.equal
                (run "[\"b\"; \"a\"; \"b\"] |> Seq.countBy (fun x -> x)" |> forceSeq)
                [ VTuple [ VStr "b"; VInt 2L ]; VTuple [ VStr "a"; VInt 1L ] ]
                ""
        }
    ]

let instantTests =
    // Instant [D:instant]: the boring subset — instants only, UTC
    // inside, no calendar arithmetic; `-` between points is Duration
    testList "Instant [D:instant]" [
        test "the arithmetic: points subtract to Duration; Duration shifts a point (both spellings); points never add" {
            Expect.equal
                (run "Instant.parse \"2026-08-14T12:00:00Z\" - Instant.parse \"2026-08-14\"")
                (VDur 43200000L)
                "12h between noon and midnight UTC"

            Expect.equal
                (run "(Instant.parse \"2026-08-14\" + 12h) == Instant.parse \"2026-08-14T12:00:00Z\"")
                (VBool true)
                "shift right"

            Expect.equal
                (run "(12h + Instant.parse \"2026-08-14\") == Instant.parse \"2026-08-14T12:00:00Z\"")
                (VBool true)
                "addition commutes"

            Expect.stringContains
                (checkErr "Instant.now () + Instant.now ()").Message
                "two points don't add"
                "the teaching"

            Expect.stringContains (checkErr "5m - Instant.now ()").Message "flip it" "Duration minus a point teaches"
        }
        test "parse: offsets normalize to UTC; bare date is midnight UTC; fractional keeps ms; tryParse asks" {
            Expect.equal
                (run "Instant.parse \"2026-08-14T14:00:00+02:00\" == Instant.parse \"2026-08-14T12:00:00Z\"")
                (VBool true)
                "the offset is normalized on the way in"

            Expect.equal
                (run "Instant.parse \"2026-08-14\" |> Instant.epochMs")
                (run "Instant.parse \"2026-08-14T00:00:00Z\" |> Instant.epochMs")
                "midnight UTC"

            Expect.equal
                (run "show (Instant.parse \"2026-08-14T12:00:00.250Z\")")
                (VStr "2026-08-14T12:00:00.250Z")
                "ms shown when nonzero"

            Expect.equal
                (run "show (Instant.parse \"2026-08-14T12:00:00Z\")")
                (VStr "2026-08-14T12:00:00Z")
                "…omitted when zero"

            Expect.equal (run "Instant.tryParse \"not-a-time\"") (VUnion("None", None)) ""

            let m =
                try
                    run "Instant.parse \"14/08/2026\"" |> ignore
                    "no error"
                with e ->
                    e.Message

            Expect.stringContains m "ISO 8601" "the reader's law is named"
        }
        test "parse: RFC 3339's space separator and nanosecond fractions read (TOML offset datetimes) (B26)" {
            for src, want in
                [
                    "1979-05-27 07:32:00Z", "1979-05-27T07:32:00Z"
                    "1979-05-27T00:32:00.999999999-07:00", "1979-05-27T07:32:00.999Z"
                    "1979-05-27 00:32:00.123456789+01:00", "1979-05-26T23:32:00.123Z"
                    "2026-08-14T12:34:56.1234567Z", "2026-08-14T12:34:56.123Z"
                ] do
                Expect.equal (run $"show (Instant.parse \"{src}\")") (VStr want) src

            Expect.equal (run "Instant.tryParse \"2026-08-14  12:00:00Z\"") (VUnion("None", None)) "one space only"

            Expect.equal
                (run "Instant.tryParse \"2026-08-14 \"")
                (run "Instant.tryParse \"2026-08-14\"")
                "a bare date is unchanged"
        }
        test "parse: a fraction dot with no digits is malformed" {
            for src in
                [
                    "2026-08-14T12:34:56.Z"
                    "2026-08-14T12:34:56."
                    "2026-08-14 12:34:56.+02:00"
                ] do
                Expect.equal (run $"Instant.tryParse \"{src}\"") (VUnion("None", None)) src

            let ex =
                Expect.throwsC (fun () -> run "Instant.parse \"2026-08-14T12:34:56.Z\"" |> ignore) id

            Expect.stringContains ex.Message "not an ISO 8601 instant" "refused like any malformed instant"

            Expect.equal
                (run "show (Instant.parse \"2026-08-14T12:34:56.5Z\")")
                (VStr "2026-08-14T12:34:56.500Z")
                "one digit reads"
        }
        test "parseWith: the named-format reader — prefix semantics, %b/%e, position errors, year-less refusal" {
            Expect.equal
                (run "Instant.parseWith \"%Y/%m/%d %H:%M:%S\" \"2026/08/14 09:15:00 GET /health\" |> show")
                (VStr "2026-08-14T09:15:00Z")
                "the log line's tail rides free"

            Expect.equal
                (run "Instant.parseWith \"notAfter=%b %e %H:%M:%S %Y\" \"notAfter=Aug  4 12:34:56 2027 GMT\" |> show")
                (VStr "2027-08-04T12:34:56Z")
                "openssl's enddate spelling (month name, padded day)"

            Expect.equal
                (run "Instant.parseWith \"%Y-%m-%dT%H:%M:%S%z\" \"2026-08-14T14:00:00+02:00\" |> show")
                (VStr "2026-08-14T12:00:00Z")
                "%z normalizes"

            let mismatch =
                try
                    run "Instant.parseWith \"%Y-%m-%d\" \"2026-xx-14\"" |> ignore
                    "no error"
                with e ->
                    e.Message

            Expect.stringContains mismatch "position 6" "the diverging position is named"

            let unknown =
                try
                    run "Instant.parseWith \"%q\" \"x\"" |> ignore
                    "no error"
                with e ->
                    e.Message

            Expect.stringContains unknown "unknown directive '%q'" "a format bug is not a data miss"

            let yearless =
                try
                    run "Instant.parseWith \"%H:%M\" \"09:15\"" |> ignore
                    "no error"
                with e ->
                    e.Message

            Expect.stringContains yearless "full date" "year-less formats refuse with the reason"

            Expect.equal
                (run "Instant.tryParseWith \"%Y-%m-%d\" \"no timestamp\"")
                (VUnion("None", None))
                "a data miss is None"

            // the offset is named when it is what fails
            Expect.equal
                (msgOf "Instant.parseWith \"%Y-%m-%d %z\" \"2026-01-02 +99:99\"")
                "Instant.parseWith: '+99:99' is not a valid %z offset — at most ±14:00, minutes below 60"
                "a bad offset names the offset, not the date"

            Expect.equal
                (msgOf "Instant.parseWith \"%Y-%m-%d%z\" \"0001-01-01+01:00\"")
                "Instant.parseWith: 0001-01-01 00:00:00 +01:00 is outside the Instant range once shifted to UTC (years 0001-9999)"
                "an offset shifting past the range names both"

            Expect.equal
                (msgOf "Instant.parseWith \"%Y-%m-%d\" \"2026-02-30\"")
                "Instant.parseWith: not a real date/time: 2026-02-30 00:00:00"
                "an impossible date still blames the date"

            // both FORMAT defects raise from the Option twin too
            Expect.equal
                (msgOf "Instant.tryParseWith \"%H:%M:%S\" \"03:04:05\"")
                "Instant.tryParseWith: the format must carry a full date (%Y %m %d) — a year-less log format needs the year supplied elsewhere"
                "a year-less format is a format defect, not None"

            // a format defect raises whatever the input — checked before
            // any input is read
            for fmt, want in
                [
                    "%Y-%m-%d %q", "unknown directive '%q'"
                    "%Y-%m-%d %", "a lone '%' ends the format — write %% for a literal percent"
                    "%H:%M:%S", "the format must carry a full date"
                ] do
                for input in [ "garbage"; "2026-01-02 x"; "03:04:05"; "" ] do
                    Expect.stringContains
                        (msgOf $"Instant.tryParseWith \"{fmt}\" \"{input}\"")
                        want
                        $"{fmt} on '{input}'"

            Expect.stringContains
                (msgOf "Instant.parseWith \"%Y-%m-%d %q\" \"2026-01-02\"")
                "unknown directive '%q'"
                "parseWith names the directive, not the input"

            // non-ASCII digits are an input miss, never .NET's text
            Expect.equal
                (run "Instant.tryParseWith \"%Y-%m-%d\" \"٢٠٢٦-01-01\"")
                (VUnion("None", None))
                "Arabic-Indic digits: None"

            Expect.equal
                (msgOf "Instant.parseWith \"%Y-%m-%d %H:%M:%S.%f\" \"2026-01-01 00:00:00.١\"")
                "Instant.parseWith: input does not match the format at position 21: expected digits for %f"
                "%f reads ASCII digits"

            Expect.equal
                (msgOf "Instant.parseWith \"%Y-%m-%e\" \"2026-01-١\"")
                "Instant.parseWith: input does not match the format at position 9: expected a day for %e"
                "%e reads ASCII digits"
        }
        test "epoch round-trip; Ord sorts; Eq holds" {
            Expect.equal (run "Instant.ofEpochMs 1500 |> Instant.epochMs") (VInt 1500L) ""

            Expect.equal
                (run "[Instant.parse \"2027-01-01\"; Instant.parse \"2026-01-01\"] |> Seq.sort |> Seq.head |> show")
                (VStr "2026-01-01T00:00:00Z")
                "Ord admits (before/after IS comparison)"
        }
        test "the JSON boundary refuses with both conversions named [D:instant]" {
            let e = env |> declare "type TInstR = { t: Instant }"

            match Weir.Check.typecheck e (parse "[\"{}\"] |> from json TInstR") with
            | Error terr ->
                Expect.stringContains terr.Message "not representable in JSON" ""
                Expect.stringContains terr.Message "Instant.epochMs" "the int conversion"
                Expect.stringContains terr.Message "show for an ISO 8601 string" "the string conversion"
            | Ok _ -> failtest "an Instant field must refuse the JSON boundary"
        }
    ]

let gapATests =
    testList "Gap audit session A remainder" [
        test "windowed is LAZY: first 1 over infinite pulls exactly the window (pull-count pin)" {
            let pulled = ref 0

            let countingId =
                VBuiltin(fun v ->
                    System.Threading.Interlocked.Increment pulled |> ignore
                    v)

            runWith
                [ "id", countingId ]
                "(Seq.initInfinite id) |> Seq.windowed 2 |> Seq.take 1 |> Seq.iter (fun w -> w |> Seq.iter (fun _ -> print \"\"))"
            |> ignore

            Expect.isLessThan pulled.Value 4 "a window of 2 must not pull the world"
        }
        test "windowed: short source is EMPTY; bad n raises the exact text" {
            expectValue "[1; 2] |> Seq.windowed 3 |> Seq.length" (VInt 0L)

            let ex =
                Expect.throwsC (fun () -> run "[1] |> Seq.windowed 0 |> Seq.freeze" |> ignore) id

            Expect.equal ex.Message "windowed: the window size must be positive; got 0" "exact"
        }
        test "last raises the family's exact text; tryLast asks; both force" {
            let ex = Expect.throwsC (fun () -> run "[] |> Seq.last" |> ignore) id
            Expect.equal ex.Message "last: empty sequence" "the head-message model"
            expectValue "[] |> Seq.tryLast" (VUnion("None", None))

            let pulled = ref 0

            let countingId =
                VBuiltin(fun v ->
                    System.Threading.Interlocked.Increment pulled |> ignore
                    v)

            runWith [ "id", countingId ] "Seq.initInfinite id |> Seq.take 5 |> Seq.last"
            |> ignore

            Expect.equal pulled.Value 5 "reached the end"
        }
        test "Option.iter: None runs NOTHING (side-effect absence pin)" {
            let ran = ref 0

            let probe =
                VBuiltin(fun _ ->
                    System.Threading.Interlocked.Increment ran |> ignore
                    VUnit)

            // override print's value with the counter (probe needs a
            // typed name; print's scheme fits iter's consumer)
            runWith [ "print", probe ] "Some \"x\" |> Option.iter print" |> ignore
            Expect.equal ran.Value 1 "Some runs once"
            runWith [ "print", probe ] "None |> Option.iter print" |> ignore
            Expect.equal ran.Value 1 "None ran nothing"
        }
        test "orElse: fallback-first pipes data-last (the F# order, ruled)" {
            expectValue "None |> Option.orElse (Some 1)" (VUnion("Some", Some(VInt 1L)))
            expectValue "Some 2 |> Option.orElse (Some 1)" (VUnion("Some", Some(VInt 2L)))
        }
        test "tempRoot is a pure trimmed query; newTempDir creates with within tmp's spelling" {
            let root =
                match run "Path.tempRoot ()" with
                | VStr s -> s
                | v -> failtest $"unexpected: {v}"

            Expect.isFalse (root.EndsWith(string System.IO.Path.DirectorySeparatorChar)) "no trailing separator"

            Expect.equal
                root
                (System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar))
                "the platform's own"

            let d =
                match run "Path.newTempDir ()" with
                | VStr s -> weirPath s
                | v -> failtest $"unexpected: {v}"

            Expect.isTrue (System.IO.Directory.Exists d) "created"
            Expect.stringContains d "weir-tmp-" "within tmp's naming"
            System.IO.Directory.Delete d
        }
    ]

// PLAN-dx-review D2-D8: the message-family completions, pinned — each
// mistake census case that moved buckets has its message here
// WEIR_LOG follows the env-enum convention: case-insensitive (the
// skill rule for the channel — DEBUG in a CI config must not be a
// startup error)
// [D:binary-echo] recurses through containers: the `| complete`
// record held the bytes the seq echo refused (the pty probe's exact
// finding); Secret and Bytes render mask/summary so they never leak
let echoBinaryTests =
    let nul = "a\u0000b"

    testList "Binary echo probe" [
        test "containers carrying NUL are binary: record, tuple, union, map, nested seq" {
            let vSeq = Weir.Eval.VSeq [ Weir.Eval.VStr nul ]
            Expect.isTrue (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VRecord("R", [ "out", vSeq ]))) "record"

            Expect.isTrue
                (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VTuple [ Weir.Eval.VInt 1L; Weir.Eval.VStr nul ]))
                "tuple"

            Expect.isTrue (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VUnion("Some", Some(Weir.Eval.VStr nul)))) "union"

            Expect.isTrue
                (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VMap(Map [ Weir.Eval.VStr "k", Weir.Eval.VStr nul ])))
                "map"
        }
        test "a 200k-deep value drains to the depth bound, never overflows the probe [D:eq-depth]" {
            // the tty binary-echo probe was the last unbounded value walk:
            // a recursive record is finite in width but not depth, so an
            // unbounded descent stack-overflows uncatchably. Bounded at 100
            // like show/eq; a checker-accepted deep value must return, not
            // abort the process (strix retest residual sink).
            let deep =
                [ 1..200000 ]
                |> List.fold
                    (fun acc i ->
                        Weir.Eval.VRecord(
                            "Node",
                            [ "depth", Weir.Eval.VInt(int64 i); "next", Weir.Eval.VUnion("Some", Some acc) ]
                        ))
                    (Weir.Eval.VRecord("Node", [ "depth", Weir.Eval.VInt 0L; "next", Weir.Eval.VUnion("None", None) ]))

            Expect.isFalse
                (Weir.Eval.echoBinary None deep)
                "no NUL, and the 200k-deep walk returns instead of overflowing"
        }
        test "clean containers are not binary; mask/summary renderers are never probed" {
            Expect.isFalse
                (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VRecord("R", [ "out", Weir.Eval.VStr "plain" ])))
                "clean record"

            Expect.isFalse (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VSecret nul)) "Secret renders ***"
            Expect.isFalse (Weir.Eval.echoBinary (Some 10) (Weir.Eval.VBytes [| 0uy |])) "Bytes renders a summary"
        }
    ]

// F13 [D:binary-echo]: data bound for a tty is neutralized — escape
// introducers and C0/C1 controls render as visible \xNN so a hostile
// filename cannot clear the screen, set the title, or hide behind CR
let ttySanitizeTests =
    testList "tty data sanitize [D:binary-echo]" [
        test "the terminal-driving families become visible \\xNN; SGR colour survives; TAB and LF pass [D:tty-color]" {
            // the review's own payload: clear-screen, colour, OSC title,
            // and the quiet CR that hides the real name
            let esc = string (char 0x1b)
            let bel = string (char 0x07)
            let hostile = esc + "[2Jcleared" + esc + "[31mred" + esc + "]0;PWNED" + bel + "a\rb"
            let safe = Weir.Eval.sanitizeTtyData hostile
            // SGR (display only) survives verbatim — colour is safe
            Expect.stringContains safe (esc + "[31mred") "SGR colour survives raw"
            // the terminal-driving families are neutralized to visible text
            Expect.stringContains safe "\\x1b[2J" "clear-screen CSI defanged (ESC visible)"
            Expect.stringContains safe "\\x1b]0;PWNED" "OSC title/clipboard defanged"
            Expect.isFalse (safe.Contains(char 0x07)) "no raw BEL survives"
            // a bare CR passes [D:tty-cr-pass]: progress lines rewrite in place
            Expect.stringContains safe "a\rb" "a bare CR passes raw"
            Expect.stringContains safe "cleared" "the visible text is preserved"
            // the ONLY raw ESC left is the SGR's; the two dangerous
            // introducers became text
            Expect.equal (safe.Split(char 0x1b).Length - 1) 1 "exactly one raw ESC — the SGR"

            // TAB and LF are not hostile — ordinary layout survives
            let layout = "a\tb\nc"
            Expect.equal (Weir.Eval.sanitizeTtyData layout) layout "TAB and LF pass through"

            // a C1 control (0x9b, the 8-bit CSI) is neutralized
            Expect.stringContains (Weir.Eval.sanitizeTtyData (string (char 0x9b))) "\\x9b" "C1 CSI renders as \\x9b"

            // a lone ESC not starting an SGR is defanged
            Expect.stringContains (Weir.Eval.sanitizeTtyData (esc + "X")) "\\x1bX" "a lone ESC is visible"

            // a clean string is returned unchanged
            Expect.equal (Weir.Eval.sanitizeTtyData "plain-name.txt") "plain-name.txt" "clean text untouched"
        }
        test "a bare CR passes; every other C0 control stays visible [D:tty-cr-pass]" {
            Expect.equal (Weir.Eval.sanitizeTtyData "50%\r100%") "50%\r100%" "CR passes"
            Expect.equal (Weir.Eval.sanitizeTtyData "a\r\nb") "a\r\nb" "CRLF passes"

            for n in [ 0x00; 0x07; 0x08; 0x0b; 0x0c; 0x0e; 0x1f ] do
                let shown = Weir.Eval.sanitizeTtyData (string (char n))
                Expect.equal shown ("\\x" + n.ToString("x2")) $"0x{n:x2} stays visible"
        }
        test "stderr at a tty rides the sanitizer: escapes visible, SGR and CR pass [D:tty-diag-sanitize]" {
            let esc = string (char 0x1b)
            let bel = string (char 0x07)
            use sink = new System.IO.StringWriter()
            sink.NewLine <- "\n"
            use w = new Weir.Eval.TtyErrWriter(sink)
            w.WriteLine("check.weir:1: no file at x" + esc + "]52;c;cm0=" + bel + ".weir")
            w.Write(esc + "[31merror" + esc + "[0m")
            w.Write('\r')
            w.Write([| 'a'; char 0x1b; 'b' |], 0, 3)
            w.Write(char 0x1b)
            w.WriteLine()
            let out = sink.ToString()

            Expect.stringContains
                out
                "x\\x1b]52;c;cm0=\\x07.weir\n"
                "the OSC renders visible, the newline is the inner's"

            Expect.stringContains out (esc + "[31merror" + esc + "[0m") "weir's SGR colour passes"
            Expect.stringContains out "\ra\\x1bb\\x1b\n" "CR passes; buffer and char writes are sanitized too"
            Expect.equal (out.Split(char 0x1b).Length - 1) 2 "the only raw ESCs are the SGR pair"
        }
        test "the DATA renderers escape a bare CR and SGR too [D:tty-echo-strict]" {
            let esc = string (char 0x1b)

            let echo =
                Weir.Eval.sanitizeTtyEcho ("a\rb" + esc + "[8mhidden" + esc + "]0;T\u0007")

            Expect.equal echo "a\\x0db\\x1b[8mhidden\\x1b]0;T\\x07" "CR, SGR and OSC all visible"
            Expect.equal (Weir.Eval.sanitizeTtyEcho "a\tb\nc\u202E") "a\tb\nc\\u202E" "TAB/LF pass, bidi visible"
            // print's sanitizer keeps both [D:tty-cr-pass] [D:tty-color]
            Expect.equal
                (Weir.Eval.sanitizeTtyData ("a\r" + esc + "[8m"))
                ("a\r" + esc + "[8m")
                "print keeps CR and SGR"
            // show escapes CR like LF, piped or not
            expectValue "show (Str.fromBase64 \"YQ1i\")" (VStr "\"a\\rb\"")
        }
        test "sanitizeIfTty leaves redirected output byte-faithful, sanitizes a tty" {
            let hostile = string (char 0x1b) + "X"
            Expect.equal (Weir.Eval.sanitizeIfTty true hostile) hostile "redirected: raw (a pipe/file is byte-faithful)"
            Expect.stringContains (Weir.Eval.sanitizeIfTty false hostile) "\\x1b" "tty: sanitized"
        }
        test "bidi controls render as visible \\uXXXX at a tty; redirected stays raw [D:tty-bidi-sanitize]" {
            // Trojan Source: an RLO makes the displayed name differ from the bytes
            let rlo = "a\u202Etxt.exe"
            Expect.equal (Weir.Eval.sanitizeTtyData rlo) "a\\u202Etxt.exe" "RLO renders visible, uppercase hex"

            let all = [
                0x202A
                0x202B
                0x202C
                0x202D
                0x202E
                0x2066
                0x2067
                0x2068
                0x2069
                0x200E
                0x200F
                0x061C
            ]

            for n in all do
                let c = string (char n)
                let shown = Weir.Eval.sanitizeTtyData c
                Expect.equal shown ("\\u" + n.ToString("X4")) $"U+{n:X4} renders visible"

            // neighbours of the ranges are ordinary text
            for n in [ 0x2029; 0x202F; 0x2065; 0x206A; 0x200D; 0x2010; 0x061B ] do
                let c = string (char n)
                Expect.equal (Weir.Eval.sanitizeTtyData c) c $"U+{n:X4} untouched"

            Expect.equal (Weir.Eval.sanitizeIfTty true rlo) rlo "redirected: bidi bytes pass through"
        }
        test "Color helpers auto-off when colour is off (piped/NO_COLOR) [D:tty-color]" {
            // the test process's stdout is captured, so onStdout is false —
            // the helpers return the plain string, no escapes (the colour
            // path — real SGR at a tty — is pinned in e2e). This is the
            // 'sane' half: piping a coloured script yields plain text.
            Expect.equal (run "Color.green \"ok\"") (VStr "ok") "green off → plain"
            Expect.equal (run "Color.bold (Color.red \"x\")") (VStr "x") "nested off → plain"
            Expect.equal (run "Color.sgr \"38;5;208\" \"orange\"") (VStr "orange") "raw sgr off → plain"
            // and they type as string -> string (composable, print-ready)
            Expect.equal (formatTy (checkOk "Color.green").Ty) "string -> string" "the helper's type"
            Expect.equal (formatTy (checkOk "Color.sgr").Ty) "string -> string -> string" "sgr takes a code then text"
        }
        test "Table.render lays a seq of records into aligned columns [D:tty-color]" {
            let src =
                "[{| name = \"web\"; n = 0 |}; {| name = \"db-x\"; n = 3 |}] |> Table.render |> Seq.freeze"

            let lines =
                run src
                |> forceSeq
                |> List.map (fun v ->
                    match v with
                    | VStr s -> s
                    | _ -> "")
            // a header row, a rule row, then one row per record
            Expect.equal lines.Length 4 "header + rule + two data rows"
            Expect.stringContains lines[0] "name" "the header names the fields"
            Expect.stringContains lines[0] "n" "…and the int column"
            // columns align: the two data rows share the name-column width
            Expect.stringContains lines[2] "web" "first record's cell"
            Expect.stringContains lines[3] "db-x" "second record's cell"
            // a non-record seq is a located error, not a silent nothing
            Expect.throwsC (fun () -> run "[1; 2; 3] |> Table.render |> Seq.freeze" |> ignore) id
            |> _.Message
            |> fun m -> Expect.stringContains m "Table.render needs a seq of" "the repair is named"

            // a seq of tuples renders under its positions [D:tuple-table]
            Expect.equal
                (run "[(1, \"a\"); (2, \"b\")] |> Table.render |> Str.join \"|\"")
                (VStr "0  1|─  ─|1  a|2  b")
                "tuples"
        }
        test "Term.width falls back to 80 with no terminal [D:tty-color]" {
            // the test process's stdout is captured — no terminal — so the
            // fallback path answers (a real tty width is pinned in e2e); it
            // never raises, which is the point for a piped tool
            Expect.equal (run "Term.width ()") (VInt 80L) "no terminal → 80"
            Expect.equal (formatTy (checkOk "Term.width").Ty) "unit -> int" "queried live, so a function"
        }
        test "Term.title drops controls, escapes and bidi, caps at 256 code points [D:term-title]" {
            let san = Weir.Builtins.sanitizeTermTitle
            Expect.equal (san "a\u001b]0;evil\u0007b") "a]0;evilb" "ESC and BEL dropped: no sequence can end or start"
            Expect.equal (san "x\u009d0;y\u009cz") "x0;yz" "C1 (8-bit OSC/ST) dropped"
            Expect.equal (san "t\u0000\t\n\r\u007fu") "tu" "C0 and DEL dropped"
            Expect.equal (san "a\u202eb\u2066c\u2069\u200ed") "abcd" "bidi controls dropped"
            Expect.equal (san "~/src (main) ≋ 日本") "~/src (main) ≋ 日本" "plain text untouched"
            Expect.equal (san (System.String('x', 300))).Length 256 "capped at 256"
            let emoji = String.replicate 300 "😀"
            Expect.equal (san emoji) (String.replicate 256 "😀") "the cap counts code points, never splits a pair"
            Expect.equal (formatTy (checkOk "Term.title").Ty) "string -> unit" "string -> unit"
            // the test process has no terminal: a quiet no-op, never a raise
            Expect.equal (run "Term.title \"t\"") VUnit "no terminal → nothing"
        }
    ]

// [D:bytes] — the non-text value: sources/sinks/ops, and every
// refusal law with its named exit
let bytesTests =
    testList "Bytes" [
        test "utf8 round-trip and byte equality" {
            expectValue "Str.fromUtf8 (Str.toUtf8 \"caf\u00e9\")" (VStr "caf\u00e9")
            expectValue "(Str.toUtf8 \"a\") == (Str.toUtf8 \"a\")" (VBool true)
            expectValue "(Str.toUtf8 \"a\") == (Str.toUtf8 \"b\")" (VBool false)
        }
        test "base64 door: malformed raises/None; binary decodes (what F4 closed, reopened in the right place)" {
            let ex = Expect.throwsC (fun () -> run "Bytes.fromBase64 \"!!!\"" |> ignore) id
            Expect.stringContains ex.Message "invalid base64" ""
            expectValue "Bytes.tryFromBase64 \"!!!\"" (VUnion("None", None))
            // a PNG header decodes as bytes — Str.fromBase64 refuses it
            expectValue "Bytes.length (Bytes.fromBase64 \"iVBORw0KGgo=\")" (VSize 8L)
        }
        test "fromUtf8 wears the encoding law's gate, NUL included [D:encoding-law]" {
            let ex =
                Expect.throwsC (fun () -> run "Str.fromUtf8 (Bytes.fromBase64 \"YQBi\")" |> ignore) id

            Expect.stringContains ex.Message "NUL" "the gate names the byte"
            expectValue "Str.tryFromUtf8 (Bytes.fromBase64 \"YQBi\")" (VUnion("None", None))
            expectValue "Str.tryFromUtf8 (Bytes.fromBase64 \"//79\")" (VUnion("None", None))
        }
        test "length is a Size; sha256 is sha256sum-parity hex" {
            expectValue "Bytes.length (Str.toUtf8 \"abc\")" (VSize 3L)

            expectValue
                "Bytes.sha256 (Str.toUtf8 \"hello\")"
                (VStr "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
        }
        test "every renderer shows a SUMMARY, never content [D:binary-echo]" {
            expectValue "show (Str.toUtf8 \"hello\")" (VStr "<5 B>")
            expectValue "$\"{Str.toUtf8 \"hello\"}\"" (VStr "<5 B>")
        }
        test "the refusal laws, each naming its exit" {
            let terr = checkErr "print (Str.toUtf8 \"x\")"
            Expect.stringContains terr.Message "Bytes.toBase64" "print names the exit"

            let ord = checkErr "(Str.toUtf8 \"a\") > (Str.toUtf8 \"b\")"
            Expect.stringContains ord.Message "not defined for Bytes" "no Ord"
        }
        test "file round-trip is byte-identical; File.sha256 agrees with Bytes.sha256" {
            let tmp = System.IO.Path.GetTempFileName()
            let out = System.IO.Path.GetTempFileName()

            try
                let payload = [| 0x89uy; 0x50uy; 0uy; 255uy; 254uy; 10uy; 13uy; 26uy |]
                System.IO.File.WriteAllBytes(tmp, payload)

                runReal $"File.writeBytes \"{out.Replace('\\', '/')}\" (File.readBytes \"{tmp.Replace('\\', '/')}\")"
                |> ignore

                Expect.equal (System.IO.File.ReadAllBytes out) payload "byte-identical through the round trip"

                let viaFile = runReal $"File.sha256 \"{tmp.Replace('\\', '/')}\""
                let viaBytes = runReal $"Bytes.sha256 (File.readBytes \"{tmp.Replace('\\', '/')}\")"
                Expect.equal viaFile viaBytes "the streaming hash agrees with the value hash"

                Expect.equal
                    (runReal $"File.sha1 \"{tmp.Replace('\\', '/')}\"")
                    (runReal $"Bytes.sha1 (File.readBytes \"{tmp.Replace('\\', '/')}\")")
                    "File.sha1 agrees with Bytes.sha1 [D:sha1]"
            finally
                System.IO.File.Delete tmp
                System.IO.File.Delete out
        }
    ]

// the port-driven member batch [D:port-members] — each gap
// cited from the asdf/acme findings, each edge pinned; plus the two
// ports' diagnostic teachings
let portMembersTests =
    let mustSay = mustSayIn "pm.weir"

    let clean = cleanIn "pm.weir"

    testList "port members (v0.0.46)" [
        test "Str.replicate: n copies concatenated; 0 empty; negative refuses (Seq.replicate's convention)" {
            expectValue "Str.replicate 3 \"ab\"" (VStr "ababab")
            expectValue "Str.replicate 0 \"ab\"" (VStr "")
            expectValue "Str.replicate 2 \"\"" (VStr "")

            let ex = Expect.throwsC (fun () -> run "Str.replicate (0 - 1) \"x\"" |> ignore) id
            Expect.stringContains ex.Message "the count must be non-negative" "Seq.replicate's exact posture"
        }
        test "Str.padLeft/padRight: total width, spaces, already-longer unchanged (.NET Pad semantics)" {
            expectValue "Str.padLeft 5 \"42\"" (VStr "   42")
            expectValue "Str.padRight 5 \"42\"" (VStr "42   ")
            expectValue "Str.padLeft 2 \"abcd\"" (VStr "abcd")
            expectValue "Str.padRight 2 \"abcd\"" (VStr "abcd")
            expectValue "Str.padLeft 4 \"\"" (VStr "    ")
            expectValue "Str.padLeft 0 \"x\"" (VStr "x")

            let ex = Expect.throwsC (fun () -> run "Str.padRight (0 - 2) \"x\"" |> ignore) id
            Expect.stringContains ex.Message "the width must be non-negative" ""
        }
        test "Seq.equal: element-wise, length-sensitive" {
            expectValue "[1; 2; 3] |> Seq.equal [1; 2; 3]" (VBool true)
            expectValue "[1; 2; 3] |> Seq.equal [1; 2]" (VBool false)
            expectValue "[1; 2] |> Seq.equal [1; 2; 3]" (VBool false)
            expectValue "([] |> Seq.where (fun x -> x > 0)) |> Seq.equal []" (VBool true)
            // the lossy-join trap the member closes: a separator inside
            // an element fools join-then-compare, never Seq.equal
            expectValue "[\"a;b\"] |> Seq.equal [\"a\"; \"b\"]" (VBool false)
        }
        test "Seq.equal is lazy-safe: short-circuits, never forces beyond need" {
            // Seq.initInfinite id is infinite — lockstep stops where the finite side ends
            expectValue "[0; 1] |> Seq.equal (Seq.initInfinite id)" (VBool false)
            expectValue "(Seq.initInfinite id) |> Seq.equal [5]" (VBool false)
        }
        test "Seq.equal wears Seq.contains's Eq constraint" {
            let terr = checkErr "Seq.equal [fun x -> x] [fun y -> y]"
            Expect.stringContains terr.Message "==" "functions never compare"

            Expect.stringContains
                (checkErr "[0.5] |> Seq.equal [0.5]").Message
                "Float.near"
                "floats name the epsilon door"
        }
        test "Bytes.fromHex/toHex: lowercase out, either case in, round-trips" {
            expectValue "Bytes.fromHex \"0a1B\" |> Bytes.toHex" (VStr "0a1b")
            expectValue "Bytes.fromHex \"\" |> Bytes.toHex" (VStr "")
            expectValue "Str.toUtf8 \"hi\" |> Bytes.toHex" (VStr "6869")
            expectValue "Bytes.fromHex (Bytes.sha256 (Str.toUtf8 \"x\")) |> Bytes.length" (VSize 32L)
        }
        test "Bytes.fromHex refuses odd length and non-hex, each teaching (fromBase64's posture)" {
            let odd = Expect.throwsC (fun () -> run "Bytes.fromHex \"abc\"" |> ignore) id
            Expect.stringContains odd.Message "odd-length hex" "the length half"
            Expect.stringContains odd.Message "two hex digits" "the repair"

            let bad = Expect.throwsC (fun () -> run "Bytes.fromHex \"zz\"" |> ignore) id
            Expect.stringContains bad.Message "invalid hex" "the alphabet half"
        }
        test "Bytes.sub mirrors Str.sub: start, length, data last; out of range raises with detail" {
            expectValue "Str.toUtf8 \"abcd\" |> Bytes.sub 1 2 |> Str.fromUtf8" (VStr "bc")
            expectValue "Str.toUtf8 \"abcd\" |> Bytes.sub 0 0 |> Bytes.length" (VSize 0L)
            expectValue "Str.toUtf8 \"abcd\" |> Bytes.sub 4 0 |> Bytes.length" (VSize 0L)

            let ex =
                Expect.throwsC (fun () -> run "Str.toUtf8 \"abc\" |> Bytes.sub 2 9" |> ignore) id

            Expect.stringContains ex.Message "out of bounds (start 2, length 9, byte length 3)" "Str.sub's detail shape"
        }
        test "Bytes.hmacSha256: key then message; RFC 4231 vectors" {
            // TC1: key = 0x0b x 20, data "Hi There" — fromHex feeds the key
            expectValue
                "Bytes.hmacSha256 (Bytes.fromHex \"0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b\") (Str.toUtf8 \"Hi There\") |> Bytes.toHex"
                (VStr "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7")
            // the widely-pinned text vector
            expectValue
                "Bytes.hmacSha256 (Str.toUtf8 \"key\") (Str.toUtf8 \"The quick brown fox jumps over the lazy dog\") |> Bytes.toHex"
                (VStr "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")
        }
        test "File.isExecutable: the owner x bit as a bool; missing raises; Windows answers by extension" {
            let tmp = System.IO.Path.GetTempFileName()

            try
                if System.OperatingSystem.IsWindows() then
                    // the stated posture: extension-based, no execute
                    // bit exists
                    let cmd = tmp + ".cmd"
                    System.IO.File.WriteAllText(cmd, "")

                    try
                        Expect.equal (runReal $"File.isExecutable \"{cmd.Replace('\\', '/')}\"") (VBool true) ".cmd"
                        Expect.equal (runReal $"File.isExecutable \"{tmp.Replace('\\', '/')}\"") (VBool false) ".tmp"
                    finally
                        System.IO.File.Delete cmd
                else
                    System.IO.File.SetUnixFileMode(
                        tmp,
                        System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserWrite
                    )

                    Expect.equal (runReal $"File.isExecutable \"{tmp}\"") (VBool false) "no x bit"

                    System.IO.File.SetUnixFileMode(
                        tmp,
                        System.IO.UnixFileMode.UserRead
                        ||| System.IO.UnixFileMode.UserWrite
                        ||| System.IO.UnixFileMode.UserExecute
                    )

                    Expect.equal (runReal $"File.isExecutable \"{tmp}\"") (VBool true) "the owner x bit"

                let ex =
                    Expect.throwsC (fun () -> runReal "File.isExecutable \"./no-such-path-zz\"" |> ignore) id

                Expect.stringContains ex.Message "no such path" "File.mode's absence posture"

                // "" is a no-such-path, not the cwd [D:isexecutable-empty]:
                // Session.resolve "" folds to the cwd (which exists and
                // carries the dir's x bit), so an unguarded read answered
                // true — it must raise the missing-path error instead
                let exEmpty =
                    Expect.throwsC (fun () -> runReal "File.isExecutable \"\"" |> ignore) id

                Expect.stringContains exEmpty.Message "no such path" "the empty string raises like a missing path"
            finally
                System.IO.File.Delete tmp
        }
        // ---- the two ports' diagnostics, whole-script level ----------
        test "a local type as a case qualifier names the bare spelling [D:qualified-case-patterns]" {
            mustSay
                [
                    "type Spec = System | Ref"
                    "match System with"
                    "| Spec.System -> print \"s\""
                    "| _ -> print \"o\""
                ]
                "'Spec' is a type, not a module — a case is qualified by its import's name; a case of a union declared here is written bare: 'System'"
                "the repair is spelled"
        }
        test "a qualified type in the adapter slot names its import [D:qualified-type-names]" {
            mustSay
                [ "let p = [\"{}\"] |> from json Acme.Pod"; "print \"x\"" ]
                "unknown type 'Acme.Pod' — no import is named 'Acme'"
                "an unknown qualifier is named"
        }
        test "'as' in the adapter slot teaches the #infer spelling [D:infer-one-shot]" {
            // the fatal's long message hard-wraps in the rendered
            // diagnostic, so the pin is a fragment no wrap can straddle
            mustSay
                [ "let z = [\"{}\"] |> from json as Zeds"; "print \"x\"" ]
                "#infer spelling"
                "the directive-in-a-file misuse is taught"
        }
        test "a weir name heading $( ) names the program spellings, never an expectation dump" {
            let diags = diagsIn "d.weir" [ "let date = 3"; "print $(date)" ]

            match diags |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                let m = d.Message.Replace("\r\n", " ").Replace("\n", " ")

                Expect.stringContains
                    m
                    "'date' is a weir name, not a program — $(…) runs a program and captures its output"
                    "named"

                Expect.stringContains m "write $(^date …) for a program of that name" "the program spelling"
                Expect.isFalse (m.Contains "Expecting") "no dump"
            | other -> failtest $"expected one error, got {other}"
        }
        test "a capture sigil x reifier teaches the in-parens spelling [D:exit-reifiers]" {
            mustSay
                [
                    "let e = Env.ofPairs [(\"A\", \"1\")]"
                    "let r = $e(sh -c \"x\") | complete"
                    "print r.stdout"
                ]
                "$e(cmd | complete)"
                "the composed spelling is named"
        }
        test "a block-local binder piped into inside the same statement is NOT a phantom command [D:statement-lets]" {
            // the acme false positive: pad is statement-local, and under
            // check's assume-command rule every command-shaped word claims
            // IsExternal — bindings beat PATH, block-locals included
            clean
                [
                    "let f () ="
                    "    let pad s = \"0\" + s"
                    "    let ints = sh -c \"echo 1 2\" |> Seq.collect Str.fields"
                    "    let x = ints |> Seq.item 0 |> pad"
                    "    print x"
                    "f ()"
                ]
                "block-local |> target"
        }
        test "a helper ending in exit discards with the polymorphic-cause teaching [D:exit-polymorphic]" {
            mustSay
                [ "let runCmd name ="; "    print name"; "    exit 0"; "runCmd \"where\"" ]
                "ends in exit or fail"
                "the cause is named"

            match Weir.Statements.discardError (TVar "a1") with
            | Some m -> Expect.stringContains m "exit at the call site" "the repair is named"
            | None -> failtest "an unresolved statement type still errors"
        }
    ]

let versionStampTests =
    testList "Version stamp" [
        test "current is <tag>+<hash>, dev-tagged when untagged [D:masking-mechanized]" {
            let v = Weir.Version.current
            // always tag+hash — the tag leads for humans, the sha rides
            Expect.stringContains v "+" "the stamp joins a tag and a hash with '+'"
            // every dev/CI-gate build is untagged and tags 0.0.0-dev: an
            // honest marker that sorts below any release, never an empty tag
            Expect.isTrue (v.StartsWith "0.0.0-dev+") $"an untagged build tags 0.0.0-dev, got '{v}'"
            // the hash rides after the last '+' — the exact substring the
            // freshness gates read — and is never empty
            let hash = v.Substring(v.LastIndexOf '+' + 1)
            Expect.isFalse (System.String.IsNullOrEmpty hash) "the hash component is present"
        }
    ]

// imported and qualified cases in patterns [D:qualified-case-patterns]:
// every binder resolves a case as a match arm does, imported unions
// included, and `A.Case` names the import whose union declares it
let qualifiedCasePatternTests =
    let withDir (files: (string * string list) list) (f: string -> unit) =
        withTempDir "weir-qcp" (fun td ->
            for name, lines in files do
                System.IO.File.WriteAllLines(System.IO.Path.Combine(td, name), lines)

            f td)

    let diags (td: string) (entry: string) =
        let path = System.IO.Path.Combine(td, entry)

        diagsIn path (List.ofArray (System.IO.File.ReadAllLines path))
        |> List.filter (fun d -> d.Severity = "error")

    let clean td entry label =
        Expect.isEmpty (diags td entry |> List.map _.Message) $"{label}: expected clean"

    let says td entry (needle: string) label =
        let es = diags td entry |> List.map _.Message
        Expect.exists es (fun m -> m.Contains needle) $"{label}: expected '{needle}', got {es}"

    let runs td entry label =
        Expect.equal (Weir.Script.run (System.IO.Path.Combine(td, entry)) []) 0 $"{label}: runs clean"

    let contract = [ "module Contract"; ""; "type Ghost = Ghost of string" ]
    let spirit = [ "module Spirit"; ""; "type Spook = Ghost of int" ]

    let a = [
        "module A"
        ""
        "type Ghost = Ghost of string"
        "type Multi = One of int | Two"
        "type Box = Box of Ghost"
    ]

    let b = [ "module B"; ""; "type Ghost = Ghost of int" ]
    let both = [ "import \"a.weir\""; "import \"b.weir\"" ]

    let tree (extra: (string * string list) list) f =
        withDir
            ([ "contract.weir", contract; "spirit.weir", spirit; "a.weir", a; "b.weir", b ]
             @ extra)
            f

    testSequenced
    <| testList "qualified case patterns [D:qualified-case-patterns]" [
        test "an imported case binds in let and parameter patterns (the journal/j2 repros)" {
            tree
                [
                    "journal.weir",
                    [
                        "import \"contract.weir\""
                        ""
                        "let g = Contract.Ghost \"S\""
                        "let (Ghost ghost) = g"
                        "if ghost <> \"S\" then fail ghost"
                    ]
                    "j2.weir",
                    [
                        "import \"contract.weir\""
                        ""
                        "let g = Contract.Ghost \"S\""
                        "let v = match g with | Ghost x -> x"
                        "let f (Ghost y) = y"
                        "if v + f g <> \"SS\" then fail v"
                    ]
                ]
                (fun td ->
                    clean td "journal.weir" "let pattern"
                    clean td "j2.weir" "parameter pattern"
                    runs td "journal.weir" "let pattern"
                    runs td "j2.weir" "parameter pattern")
        }
        test "an imported multi-case union is refutable in a binder" {
            tree
                [
                    "multi.weir", [ "import \"a.weir\""; "let (One k) = A.One 1"; "print $\"{k}\"" ]
                    "lam.weir",
                    [
                        "import \"a.weir\""
                        "let f = fun (A.One k) -> k"
                        "print $\"{f (A.One 1)}\""
                    ]
                ]
                (fun td ->
                    for f in [ "multi.weir"; "lam.weir" ] do
                        match diags td f with
                        | [ d ] ->
                            Expect.stringStarts d.Message "this pattern can fail; use match" f
                            Expect.equal d.Code "refutable-binder" f
                        | ds -> failtest $"{f}: {ds |> List.map _.Message}")
        }
        test "a qualified case in a match arm, let, parameter, lambda and nested pattern" {
            tree
                [
                    "q.weir",
                    [
                        "import \"contract.weir\" as C"
                        "import \"a.weir\""
                        "let g = C.Ghost \"s\""
                        "let m = match g with | C.Ghost x -> x"
                        "let (C.Ghost l) = g"
                        "let p (C.Ghost y) = y"
                        "let lam = [g] |> Seq.map (fun (C.Ghost z) -> z) |> Seq.exactlyOne"
                        "let n ="
                        "    match (A.Box (A.Ghost \"n\"), Some A.Two) with"
                        "    | (A.Box (A.Ghost s), Some A.Two) -> s"
                        "    | _ -> \"?\""
                        "let (A.Box (Ghost deep)) = A.Box (A.Ghost \"d\")"
                        "if m + l + p g + lam + n + deep <> \"ssssnd\" then fail m"
                    ]
                ]
                (fun td ->
                    clean td "q.weir" "every position"
                    runs td "q.weir" "every position")
        }
        test "a wrong qualifier is a type error naming both types" {
            let both =
                "'Spirit.Ghost' is a case of Spirit.Spook, but this value has type Contract.Ghost"

            tree
                [
                    "m.weir",
                    [
                        "import \"contract.weir\""
                        "import \"spirit.weir\""
                        "let v = match Contract.Ghost \"s\" with | Spirit.Ghost n -> n"
                        "print $\"{v}\""
                    ]
                    "l.weir",
                    [
                        "import \"contract.weir\""
                        "import \"spirit.weir\""
                        "let (Spirit.Ghost n) = Contract.Ghost \"s\""
                        "print $\"{n}\""
                    ]
                    "nomod.weir",
                    [
                        "import \"contract.weir\""
                        "let v = match Contract.Ghost \"s\" with | Nope.Ghost n -> n"
                        "print v"
                    ]
                    "nocase.weir",
                    [
                        "import \"contract.weir\""
                        "let v = match Contract.Ghost \"s\" with | Contract.Gost n -> n"
                        "print v"
                    ]
                ]
                (fun td ->
                    says td "m.weir" both "match arm"
                    says td "l.weir" both "let pattern"
                    says td "nomod.weir" "no module named 'Nope'" "not a module"

                    says
                        td
                        "nocase.weir"
                        "module 'Contract' declares no case 'Gost'. Did you mean 'Ghost'?"
                        "no such case")
        }
        test "same-named unions from two imports resolve by qualifier, and a typed value settles a bare case" {
            tree
                [
                    "two.weir",
                    both
                    @ [
                        "let a = A.Ghost \"s\""
                        "let b = B.Ghost 3"
                        "let (A.Ghost x) = a"
                        "let (B.Ghost y) = b"
                        "let f (A.Ghost s) = s"
                        "let g = fun (B.Ghost n) -> n + 1"
                        "let m = match b with | B.Ghost n -> n"
                        "let (Ghost typed) = a"
                        "let bare = match b with | Ghost n -> n"
                        "let piped = [b] |> Seq.map (fun (Ghost n) -> n) |> Seq.sum"
                        "if x + f a + typed <> \"sss\" || y + g b + m + bare + piped <> 16 then fail x"
                    ]
                ]
                (fun td ->
                    clean td "two.weir" "two imports, one case name"
                    runs td "two.weir" "two imports, one case name")
        }
        test "a bare case two imports declare, with nothing typing the value, names both spellings" {
            let ambiguous =
                "case 'Ghost' is ambiguous — more than one import declares it; write the qualified case: 'A.Ghost' or 'B.Ghost'"

            tree
                [
                    "param.weir", both @ [ "let h (Ghost z) = z"; "print $\"{h (A.Ghost \"s\")}\"" ]
                    "arm.weir",
                    both
                    @ [ "let k x = match x with | Ghost s -> s"; "print $\"{k (A.Ghost \"s\")}\"" ]
                ]
                (fun td ->
                    says td "param.weir" ambiguous "parameter"
                    says td "arm.weir" ambiguous "match arm on an untyped value")
        }
        test
            "a bare ambiguous case CONSTRUCTED: check gives the runner's verdict, no command-not-found [D:assume-resolver]" {
            tree [ "ctor.weir", both @ [ "let v = Ghost \"s\""; "print $\"{v}\"" ] ] (fun td ->
                let path = System.IO.Path.Combine(td, "ctor.weir")
                let all = diagsIn path (List.ofArray (System.IO.File.ReadAllLines path))

                Expect.equal
                    (all |> List.map (fun d -> d.Severity, d.Line, d.Message))
                    [ "error", 3, "'Ghost' is module-qualified here; use A.Ghost or B.Ghost" ]
                    "one error, the run message")
        }
    ]

// the entry's env holds the Self facts its text names; a module's facts
// do not depend on what the entry wrote
let selfFactTests =
    testList "Self facts reach a module whatever the entry names" [
        test "an entry that never writes Self imports a module that reads the process facts" {
            withTempDir "weir-selffacts" (fun td ->
                let write (name: string) (lines: string list) =
                    System.IO.File.WriteAllLines(System.IO.Path.Combine(td, name), lines)

                write "facts.weir" [
                    "module Facts"
                    ""
                    "let count : int"
                    "let count = Self.args |> Seq.length"
                    "let own : string"
                    "let own = Self.scriptPath"
                    "let invoked : string"
                    "let invoked = Self.entryPath"
                    "let live : bool"
                    "let live = Self.pid > 0"
                ]

                let checks = [
                    "import \"./facts.weir\" as F"
                    "if F.count <> 2 then fail $\"count {F.count}\""
                    "if not (Str.endsWith \"facts.weir\" F.own) then fail $\"own {F.own}\""
                    "if not F.live then fail \"live\""
                ]

                write
                    "plain.weir"
                    (checks
                     @ [ "if not (Str.endsWith \"plain.weir\" F.invoked) then fail F.invoked" ])

                write
                    "named.weir"
                    (checks
                     @ [
                         "if F.invoked <> Self.scriptPath then fail F.invoked"
                         "if (Self.args |> Seq.length) <> 2 then fail \"own count\""
                     ])

                for entry in [ "plain.weir"; "named.weir" ] do
                    Expect.equal (Weir.Script.run (System.IO.Path.Combine(td, entry)) [ "a"; "b" ]) 0 entry)
        }
    ]

let scopeCellTests =
    testList "a name reads the nearest bind, in a function body and in a parallel arm" [
        test "a parameter, a let, a pattern and a match arm shadow the top level and leave it as it was" {
            withTempDir "weir-scope" (fun td ->
                let path = System.IO.Path.Combine(td, "scope.weir")

                System.IO.File.WriteAllLines(
                    path,
                    [
                        "type P = P of int * int"
                        "let k = 10"
                        "let add a b = a + b + k"
                        "let shadow k = k + 1"
                        "let viaLet n ="
                        "    let k = n * 2"
                        "    let g = fun x -> x + k"
                        "    let k = 100"
                        "    g 1 + k"
                        "let pat (P (a, b)) = a - b"
                        "let pick o ="
                        "    match o with"
                        "    | Some k -> k"
                        "    | None -> k"
                        "let viaAsync n ="
                        "    let k = n * 2"
                        "    let out = $(git --version) |> Seq.length"
                        "    match Some (k + out) with"
                        "    | Some k -> k + n"
                        "    | None -> k"
                        "let row n = $\"{add n 1}|{shadow n}|{viaLet n}|{pat (P (n, 1))}|{pick (Some n)}|{pick None}|{viaAsync n}|{k}\""
                        "if row 3 <> \"14|4|107|2|3|10|10|10\" then fail (row 3)"
                        "let par = [3; 3] |> Seq.pmap row |> Str.join \",\""
                        "if par <> \"14|4|107|2|3|10|10|10,14|4|107|2|3|10|10|10\" then fail par"
                    ]
                )

                Expect.equal (Weir.Script.run path []) 0 "every read saw its own bind")
        }
    ]

let unusedBindingTests =
    // the unused-binding law [D:unused-bindings]: an unread let binder is
    // a hard check error — the strictness family (statement rule,
    // exhaustiveness, unreachable arms); `_name` is the escape
    let errsOf = errorsIn "ub.weir"

    let clean (lines: string list) (label: string) =
        Expect.isEmpty (errsOf lines) $"{label}: expected clean, got {errsOf lines |> List.map _.Message}"

    testList "unused bindings [D:unused-bindings]" [
        test "an unread top-level binder errors, located at the binder, code unused-binding" {
            match errsOf [ "let tmp = 5"; "print \"x\"" ] with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (1, 5) "at the binder"
                Expect.equal d.Code "unused-binding" "the code"

                Expect.equal
                    d.Message
                    "'tmp' is bound but never used — read it, or name it '_tmp' to keep it deliberately"
                    "the teaching voice, escape included"
            | other -> failtest $"expected one error, got {other}"
        }
        test "collected, not first-only: every unused binder reports" {
            let ds = errsOf [ "let a = 1"; "let b = 2"; "print \"x\"" ]
            Expect.equal (ds |> List.length) 2 "both"
            Expect.equal (ds |> List.map (fun d -> d.Line)) [ 1; 2 ] "position order"
        }
        test "the receipt class: a bound-and-forgotten | complete is the swallowed failure" {
            match errsOf [ "let r = git status | complete"; "print \"done\"" ] with
            | [ d ] -> Expect.stringContains d.Message "'r' is bound but never used" "the swallow surfaces"
            | other -> failtest $"expected one error, got {other}"
        }
        test "a block-local unread binder errors at its own site" {
            match errsOf [ "let f () ="; "    let a = 1"; "    2"; "print (show (f ()))" ] with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (2, 9) "at the block binder"
                Expect.stringContains d.Message "'a' is bound but never used" ""
            | other -> failtest $"expected one error, got {other}"
        }
        test "destructuring judges each NAME; a wildcard component is fine" {
            match errsOf [ "let (a, b) = (1, 2)"; "print (show a)" ] with
            | [ d ] -> Expect.stringContains d.Message "'b' is bound but never used" "per-name"
            | other -> failtest $"expected one error, got {other}"

            clean [ "let (a, _) = (1, 2)"; "print (show a)" ] "wildcard component"
        }
        test "shadow without a read errors AT THE EARLIER binder, naming the rebind line" {
            match errsOf [ "let cfg = 1"; "let cfg = 2"; "print (show cfg)" ] with
            | [ d ] ->
                Expect.equal d.Line 1 "the earlier binder"
                Expect.stringContains d.Message "never used before being rebound at line 2" "the rebind cited"
            | other -> failtest $"expected one error, got {other}"

            // the RHS reads the outer binding (no let rec): a read-through
            // rebind is clean
            clean [ "let x = 1"; "let x = x + 1"; "print (show x)" ] "read-through rebind"
        }
        test "the exempt classes: params, arm binders, for/until/within" {
            clean [ "let f x = 1"; "print (show (f 2))" ] "unused param"
            clean [ "[1] |> Seq.iter (fun n -> print \"y\")" ] "unused lambda param"

            clean [ "let v = match Some 1 with | Some n -> 9 | None -> 0"; "print (show v)" ] "unused arm binder"

            clean [ "for x in [1; 2] do print \"y\"" ] "unused for binder"
            clean [ "within tmp d"; "    print \"b\"" ] "unused within binder"

            clean
                [
                    "let v ="
                    "    retry attempts=1 delay=1ms"
                    "        7"
                    "    until r"
                    "        true"
                    "print (show v)"
                ]
                "unused until binder"
        }
        test "the escape: a '_'-prefixed name never errors, top-level and block" {
            clean [ "let _keep = 5"; "print \"x\"" ] "top-level _name"
            clean [ "let f () ="; "    let _a = 1"; "    2"; "print (show (f ()))" ] "block _name"
            // and a _name stays readable — the prefix only switches the
            // judgement off
            clean [ "let _n = 5"; "print (show _n)" ] "_name read back"
        }
        test "a bare '_' let binder refuses — the anonymous swallow" {
            match errsOf [ "let _ = sh -c \"exit 3\" | complete"; "print \"x\"" ] with
            | [ d ] -> Expect.stringContains d.Message "a bare '_' binder discards its value anonymously" "the teaching"
            | other -> failtest $"expected one error, got {other}"

            match errsOf [ "let f () ="; "    let _ = 1"; "    2"; "print (show (f ()))" ] with
            | [ d ] -> Expect.stringContains d.Message "a bare '_' binder" "block form too"
            | other -> failtest $"expected one error, got {other}"
        }
        test "POISON: an errored statement suppresses the unused pass (one real error beats echoes)" {
            match errsOf [ "let x = 5"; "let y = \"a\" + 1"; "print \"x\"" ] with
            | [ d ] -> Expect.stringContains d.Message "expected string, got int" "only the real error"
            | other -> failtest $"expected exactly the type error, got {other |> List.map (fun d -> d.Message)}"
        }
        test "module: an unsigned member unread at home is dead private code; signed is exempt" {
            match
                errsOf [
                    "module M"
                    "let grade : int -> int"
                    "let dead n = n * 2"
                    "let grade n = n + 1"
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 3 "at the dead member"
                Expect.stringContains d.Message "an unsigned module member is private dead code" "the module voice"
                Expect.stringContains d.Message "export it with a signature" "the export repair"
            | other -> failtest $"expected one error, got {other |> List.map (fun d -> d.Message)}"

            // the signature is the use — a signed member unread at home is
            // the export case, pinned
            clean [ "module M"; "let grade : int -> int"; "let grade n = n + 1" ] "signed member exempt"

            // an unsigned member the module reads is the private-helper
            // case, clean
            clean
                [
                    "module M"
                    "let helper n = n * 2"
                    "let grade : int -> int"
                    "let grade n = helper n + 1"
                ]
                "used private member"
        }
    ]

let reenumWarningTests =
    // possible re-enumeration [D:reenum-warning]: a command-backed,
    // unforced seq binding enumerated at two or more sites warns at the
    // second and later sites — advisory (warning severity, check still
    // exits 0), conservative, and stated
    let allOf = diagsIn "re.weir"

    let diagsOf (lines: string list) =
        allOf lines |> List.filter (fun d -> d.Code = "re-enumeration")

    let silent (lines: string list) (label: string) =
        Expect.isEmpty
            (diagsOf lines)
            $"{label}: expected no re-enumeration warning, got {diagsOf lines |> List.map _.Message}"

    testList "possible re-enumeration [D:reenum-warning]" [
        test "the warning fires on the SECOND enumerating use — exact text, command and repair included" {
            match
                diagsOf [
                    "let pods = git ls-files"
                    "print $\"{pods |> Seq.length}\""
                    "pods |> Seq.iter print"
                ]
            with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (3, 1) "located at the second use"
                Expect.equal d.Severity "warning" "advisory, never a gate"

                Expect.equal
                    d.Message
                    ("possible re-enumeration: 'pods' is command-backed and unforced — "
                     + "each pull re-runs 'git ls-files'; snapshot one run: let pods = git ls-files |> Seq.freeze")
                    "the exact sentence: the hazard, the command, the repair"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "warning severity is exit-0's substance: no error-severity diagnostic rides along" {
            let ds =
                allOf [
                    "let pods = git ls-files"
                    "print $\"{pods |> Seq.length}\""
                    "pods |> Seq.iter print"
                ]

            Expect.isFalse (ds |> List.exists (fun d -> d.Severity = "error")) "check exits 0 on a warning-only file"
        }
        test "second AND later: three pulls warn twice (never the first)" {
            let ds =
                diagsOf [
                    "let pods = git ls-files"
                    "pods |> Seq.iter print"
                    "print $\"{pods |> Seq.length}\""
                    "print $\"{pods |> Seq.sort |> Seq.length}\""
                ]

            Expect.equal (ds |> List.map (fun d -> d.Line)) [ 3; 4 ] "the first pull is the binding's point"
        }
        test "a single enumerating use is silent" {
            silent [ "let pods = git ls-files"; "pods |> Seq.iter print" ] "single use"
        }
        test "the recognized-forced set: a Seq.freeze tail is silent" {
            silent
                [
                    "let pods = git ls-files |> Seq.freeze"
                    "print $\"{pods |> Seq.length}\""
                    "pods |> Seq.iter print"
                ]
                "piped force tail"

            silent
                [
                    "let pods = Seq.freeze $(git ls-files)"
                    "print $\"{pods |> Seq.length}\""
                    "pods |> Seq.iter print"
                ]
                "applied force head"
        }
        test "the recognized-forced set: the eager list literal and the comprehension are silent" {
            silent
                [
                    "let xs = [$(git ls-files) |> Seq.length]"
                    "print $\"{xs |> Seq.length}\""
                    "xs |> Seq.iter (fun n -> print (show n))"
                ]
                "eager literal"

            silent
                [
                    "let xs = [for f in $(git ls-files) -> f]"
                    "print $\"{xs |> Seq.length}\""
                    "xs |> Seq.iter print"
                ]
                "comprehension (its desugar ends in the force)"
        }
        test "a pure lazy seq is silent — command-backed only" {
            silent
                [
                    "let xs = [1; 2; 3] |> Seq.map (fun n -> n * 2)"
                    "print $\"{xs |> Seq.length}\""
                    "print $\"{xs |> Seq.sum}\""
                ]
                "pure lazy"
        }
        test "a | complete binding is silent — the record is already captured" {
            silent
                [
                    "let r = git status | complete"
                    "print $\"{r.exitCode}\""
                    "print $\"{r.exitCode}\""
                ]
                "complete record"
        }
        test "an argv splat counts as a pull — spawning expands the seq" {
            match diagsOf [ "let files = git ls-files"; "git add $@files"; "git add $@files" ] with
            | [ d ] ->
                Expect.equal d.Line 3 "the second splat"
                Expect.stringContains d.Message "re-runs 'git ls-files'" "the command named"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "an alias carries the pulls: a split use across it warns [D:reenum-indirect]" {
            match
                diagsOf [
                    "let pods = git ls-files"
                    "let extra = pods"
                    "pods |> Seq.iter print"
                    "extra |> Seq.iter print"
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 4 "the alias's use is the second pull"
                Expect.stringContains d.Message "'pods' (through 'extra')" "the indirection named"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"

            silent
                [ "let pods = git ls-files"; "let extra = pods"; "extra |> Seq.iter print" ]
                "one pull through the alias"
        }
        test "through a function: each call replays the body's pulls [D:reenum-indirect]" {
            match
                diagsOf [
                    "let pods = git ls-files"
                    "let count () = pods |> Seq.length"
                    "print $\"{count ()}\""
                    "print $\"{count ()}\""
                ]
            with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (4, 10) "the second call"

                Expect.equal
                    d.Message
                    ("possible re-enumeration: 'pods' (through 'count') is command-backed and unforced — "
                     + "each pull re-runs 'git ls-files'; snapshot one run: let pods = git ls-files |> Seq.freeze")
                    "the exact sentence"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"

            // defining the function pulls nothing; one call is one pull
            silent
                [
                    "let pods = git ls-files"
                    "let count () = pods |> Seq.length"
                    "print $\"{count ()}\""
                ]
                "a single call"
        }
        test "in a loop: a read inside a for body or a Seq function's lambda repeats [D:reenum-indirect]" {
            match
                diagsOf [
                    "let pods = git ls-files"
                    "for i in [1; 2] do"
                    "    print $\"{pods |> Seq.length}\""
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 3 "the read in the body"
                Expect.stringContains d.Message "'pods' (read in a loop)" "the loop named"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"

            match
                diagsOf [
                    "let pods = git ls-files"
                    "[1; 2] |> Seq.map (fun i -> pods |> Seq.length) |> Seq.iter (fun n -> print (show n))"
                ]
            with
            | [ _ ] -> ()
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"

            // the loop's own source is one pull; a binding made inside
            // the body is fresh per iteration
            silent [ "let pods = git ls-files"; "for p in pods do"; "    print p" ] "the loop source"

            silent
                [
                    "for i in [1; 2] do"
                    "    let out = git ls-files"
                    "    out |> Seq.iter print"
                ]
                "a per-iteration binding"
        }
        test "a derived lazy binding carries the pulls; deriving alone pulls nothing [D:reenum-indirect]" {
            match
                diagsOf [
                    "let pods = git ls-files"
                    "let named = pods |> Seq.where (Str.contains \"a\")"
                    "print $\"{named |> Seq.length}\""
                    "named |> Seq.iter print"
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 4 "the derived binding's second use"
                Expect.stringContains d.Message "'pods' (through 'named')" "the source named"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"

            silent
                [
                    "let pods = git ls-files"
                    "let named = pods |> Seq.where (Str.contains \"a\")"
                    "named |> Seq.iter print"
                ]
                "derive then read once"

            // a forced derivation is a snapshot: one pull, then data
            silent
                [
                    "let pods = git ls-files"
                    "let named = pods |> Seq.where (Str.contains \"a\") |> Seq.freeze"
                    "print $\"{named |> Seq.length}\""
                    "named |> Seq.iter print"
                ]
                "a forced derivation"
        }
        test "a block-local binding warns inside its own body, generic repair" {
            match
                diagsOf [
                    "let f () ="
                    "    let inner = git ls-files"
                    "    print $\"{inner |> Seq.length}\""
                    "    inner |> Seq.iter print"
                    ""
                    "f ()"
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 4 "the second local pull"

                Expect.stringContains
                    d.Message
                    "add '|> Seq.freeze' at the binding"
                    "no clean one-line source — the generic repair"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "a rebinding consumes the old tracking; its RHS pull counts once" {
            // the second statement's RHS reads the outer pods (one
            // pull); the rebound name is a plain pipeline, untracked
            silent
                [
                    "let pods = git ls-files"
                    "let pods = pods |> Seq.where (Str.contains \"a\")"
                    "pods |> Seq.iter print"
                ]
                "read-through rebind"
        }
        test "POISON: an errored statement suppresses the advisory pass" {
            silent
                [
                    "let pods = git ls-files"
                    "pods |> Seq.iter print"
                    "pods |> Seq.iter print"
                    "print (\"a\" + 1)"
                ]
                "one real error beats advisory noise"
        }
    ]

// ---- dynamic command heads [D:dynamic-head] --------------------------
// `^` gains a `$`-splice alternative: ^$name / ^$(…) force-external a
// value head — one program, string exactly, resolved at run; argv stays
// typed argv, so the injection law holds for computed programs too.

let jsonEscapingTests =
    testList "json escaping [D:json-relaxed-escaping]" [
        test "a Private-Use glyph emits raw, not a \\u escape, and round-trips" {
            // the LSP/CLI writer must send a Powerline/Nerd-Font prompt
            // icon as UTF-8 — a naive client leaves a \u escape as literal
            let glyph = System.Char.ConvertFromUtf32 0xE0A0
            let out = Weir.Diagnostics.jsonBuild (fun w -> w.WriteStringValue glyph)
            Expect.stringContains out glyph "the PUA glyph is raw in the JSON"
            Expect.isFalse (out.ToLower().Contains "\\ue0a0") "no \\u escape"
            Expect.equal (System.Text.Json.JsonSerializer.Deserialize<string> out) glyph "round-trips"
        }
        test "the JSON-mandatory escapes and astral pairs stay valid" {
            let rt (s: string) =
                let out = Weir.Diagnostics.jsonBuild (fun w -> w.WriteStringValue s)
                System.Text.Json.JsonSerializer.Deserialize<string> out

            // quote/backslash/control must escape; a BMP glyph stays raw;
            // an astral char round-trips (escaped surrogate pair) — all
            // valid JSON either way
            for s in
                [
                    "a\"b"
                    "a\\b"
                    "a\nb"
                    "a\u0001b"
                    "⋱…❯"
                    System.Char.ConvertFromUtf32 0x1F525
                ] do
                Expect.equal (rt s) s $"round-trips: {s}"
        }
    ]

// an arm's waits suspend [D:async-flows] [D:async-seq]: what the arm
// computes is what the blocking evaluator computed — real processes, POSIX.
// The transcript fixtures (tests/async) carry the breadth; these pin each
// path's entry
let asyncFlowTests =
    let arm (body: string) =
        runReal $"[1] |> Seq.pmap (fun _ -> {body}) |> Seq.head"

    testList "async flows [D:async-flows] [D:async-seq]" [
        test "a lazy command's lines flow through stages and a terminal" {
            skipOnWindows ()

            Expect.equal
                (arm "$(sh -c \"echo a; echo b\") |> Seq.map Str.toUpper |> Str.join \"\"")
                (VStr "AB")
                "map then join"

            Expect.equal (arm "$(sh -c \"seq 1 100\") |> Seq.length") (VInt 100L) "length"
        }
        test "an early stop ends the command; the lines are still lazy" {
            skipOnWindows ()
            let sw = System.Diagnostics.Stopwatch.StartNew()

            Expect.equal
                (arm "$(sh -c \"yes\") |> Seq.take 2 |> Str.join \"\"")
                (VStr "yy")
                "two lines of an endless command"

            Expect.equal (arm "$(sh -c \"yes\") |> Seq.head") (VStr "y") "head"
            Expect.isLessThan sw.ElapsedMilliseconds 20000L "no hang"
        }
        test "a command's failure still raises where its lines are forced" {
            skipOnWindows ()
            Expect.throws (fun () -> arm "$(sh -c \"echo a; exit 3\") |> Str.join \"\"" |> ignore) "the nonzero exit"

            Expect.equal
                (arm "$(sh -c \"echo a; exit 3\") |> Seq.head")
                (VStr "a")
                "a consumer that stops first never sees it"
        }
        test "a stage's lambda may wait" {
            skipOnWindows ()

            Expect.equal
                (arm
                    "[1; 2] |> Seq.map (fun x -> (Duration.sleep 2ms; x + ($(sh -c \"echo l\") |> Seq.length))) |> Seq.sum")
                (VInt 5L)
                "a sleep and a command inside map"
        }
        test "waits inside operators, interpolation, tuples and try" {
            skipOnWindows ()

            Expect.equal
                (arm "$\"a{(Duration.sleep 1ms; 1) + ($(sh -c \"echo x\") |> Seq.length)}\"")
                (VStr "a2")
                "an operator inside a hole"

            Expect.equal
                (arm "((Duration.sleep 1ms; 5), ($(sh -c \"echo 6\") |> Str.join \"\"))")
                (VTuple [ VInt 5L; VStr "6" ])
                "a tuple"

            Expect.equal
                (arm "($(sh -c \"exit 4\") |> Seq.length) |> try")
                (VUnion("None", None))
                "try catches a suspended command's failure"
        }
        test "a chain's lines and a sort over a command" {
            skipOnWindows ()
            Expect.equal (arm "sh -c \"echo b; echo a\" | sh -c \"sort\" |> Str.join \"\"") (VStr "ab") "a chain"
            Expect.equal (arm "$(sh -c \"printf 'b\\na\\n'\") |> Seq.sort |> Str.join \"\"") (VStr "ab") "a sort"
        }
        test "a fold or a probe over a long command keeps one value, as the blocking form does" {
            skipOnWindows ()
            let n = "$(sh -c \"seq 1 20000\") |> Seq.map Str.toInt"
            Expect.equal (arm $"{n} |> Seq.sum") (VInt 200010000L) "sum across chunks"
            Expect.equal (arm $"{n} |> Seq.last") (VInt 20000L) "last"
            Expect.equal (arm $"{n} |> Seq.tryLast") (VUnion("Some", Some(VInt 20000L))) "tryLast"
            Expect.equal (arm $"{n} |> Seq.max") (VInt 20000L) "max"
            Expect.equal (arm $"{n} |> Seq.min") (VInt 1L) "min"
            Expect.equal (arm $"{n} |> Seq.reduce (fun a b -> if b > a then b else a)") (VInt 20000L) "reduce"
            Expect.equal (arm "$(sh -c \"true\") |> Seq.tryLast") (VUnion("None", None)) "an empty source"
            Expect.throws (fun () -> arm "$(sh -c \"true\") |> Seq.map Str.toInt |> Seq.max" |> ignore) "max of none"
            Expect.equal (arm $"{n} |> Seq.exists (fun x -> x < 0)") (VBool false) "a probe that never matches"
            Expect.equal (arm $"{n} |> Seq.forall (fun x -> x > 0)") (VBool true) "forall to the end"
            Expect.equal (arm $"{n} |> Seq.find (fun x -> x == 15000)") (VInt 15000L) "find"
            Expect.equal (arm $"{n} |> Seq.contains 19999") (VBool true) "contains"
        }
        test "pfirst's winner returns while a loser waits on a command" {
            skipOnWindows ()
            let sw = System.Diagnostics.Stopwatch.StartNew()

            Expect.equal
                (runReal
                    "[1; 2] |> Seq.pfirst (fun i -> if i == 2 then (Duration.sleep 20ms; \"w\") else ($(sh -c \"sleep 20; echo l\") |> Str.join \"\"))")
                (VStr "w")
                "the winner"

            Expect.isLessThan sw.ElapsedMilliseconds 10000L "the loser is cut, not waited out"
        }
    ]

let holeGenericTests =
    // a hole on a generalized var carries Render — Show minus a top-level
    // Secret — instead of the string default [D:hole-generic]
    let run = errorMsgsIn "hg.weir"

    testList "generic interpolation holes [D:hole-generic]" [
        test "a row-typed field hole works at its caller" {
            Expect.isEmpty
                (run [
                    "type Spec = { name: string; port: int }"
                    "let addr s = $\"{s.name}:{s.port}\""
                    "print (addr { name = \"a\"; port = 1 })"
                ])
                "the survey's receipt"
        }
        test "one function renders int, string, float, record and seq" {
            expectValue "let f x = $\"<{x}>\" in f 1 + f \"s\" + f [1; 2]" (VStr "<1><s><[1; 2]>")
        }
        test "a top-level Secret refuses at the call; a nested one renders masked" {
            let ms = run [ "let f x = $\"{x}\""; "print (f (Secret.of \"a\"))" ]
            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "a Secret does not interpolate")) $"{ms}"

            Expect.isEmpty
                (run [
                    "type R = { s: Secret }"
                    "let f x = $\"{x}\""
                    "print (f { s = Secret.of \"a\" })"
                ])
                "nested"
        }
        test "a function argument refuses with the show law" {
            let ms = run [ "let f x = $\"{x}\""; "print (f (fun y -> y))" ]
            Expect.isTrue (ms |> List.exists (fun m -> m.Contains "cannot be shown")) $"{ms}"
        }
        test "an earlier generic let's var names never pin a later hole" {
            // statement ctxs restart their fresh counter: a closed env
            // scheme must not be resolved through this ctx's subst
            Expect.isEmpty
                (run [
                    "let dash n = $\"-{n}\""
                    "type Spec = { name: string; port: int }"
                    "let addr s = $\"{s.name}:{s.port}\""
                    "print (addr { name = \"a\"; port = 1 } + dash 1)"
                ])
                "ordering-independent"
        }
        test "an inner let generalizes too" {
            expectValue "let lbl n = $\"({n})\" in lbl 1 + lbl \"a\"" (VStr "(1)(a)")
        }
        test "argv splices stay scalar-exact" {
            let ms = run [ "let f x = echo $x"; "f [1]" ]
            Expect.isNonEmpty ms "a seq is not one argv word"
        }
    ]

// `e |> try` / `e |> try result` [D:try-form] — a failure as a value
let tryTests =
    testList "|> try [D:try-form]" [
        test "the form types as Option, or Result with a string error" {
            Expect.equal (formatTy (checkOk "3 |> try").Ty) "Option<int>" "Option"
            Expect.equal (formatTy (checkOk "3 |> try result").Ty) "Result<int, string>" "Result"
        }
        test "a raise becomes None / Error with the message" {
            Expect.equal (run "(if 1 == 1 then fail \"boom\" else 3) |> try") (VUnion("None", None)) "None"

            Expect.equal
                (run "(if 1 == 1 then fail \"boom\" else 3) |> try result")
                (VUnion("Error", Some(VStr "boom")))
                "Error"

            Expect.equal (run "3 |> try result") (VUnion("Ok", Some(VInt 3L))) "Ok"
        }
        test "the whole pipeline to the left is captured" {
            Expect.equal
                (run "[\"a = 1\"; \"a = 2\"] |> from toml {| a: int |} |> try")
                (VUnion("None", None))
                "a parse error"
        }
        test "a lazy result is forced inside the capture" {
            // the command's output is only read when the seq is forced;
            // its failure must land in the capture, not after it
            match runReal "$(sh -c \"echo a; exit 3\") |> try" with
            | VUnion("None", None) -> ()
            | other -> failtest $"expected None, got {other}"
        }
        test "try anywhere but after |> teaches the form" {
            for src in [ "let x = try"; "let y = [ 1 ] |> Seq.map try" ] do
                let msgs = diagsIn "stray.weir" [ src ] |> List.map (fun d -> d.Message)
                Expect.exists msgs (fun m -> m.Contains "'try' goes after a pipeline") src
        }
        test "a command chain's |> try teaches the reifier, in every position" {
            for src in
                [
                    "let r = sh -c \"exit 3\" |> try"
                    "let r = sh -c \"exit 3\" |> Seq.length |> try result"
                    "sh -c \"exit 3\" |> try"
                ] do
                let msgs =
                    diagsIn "trycmd.weir" [ src; "print \"x\"" ] |> List.map (fun d -> d.Message)

                Expect.exists msgs (fun m -> m.Contains "a command's failure is data through a reifier") src
                Expect.isFalse (msgs |> List.exists (fun m -> m.Contains "Expecting")) src

            errorsCleanIn
                "trycmd.weir"
                [ "let r = $(sh -c \"exit 3\") |> try"; "print $\"{r}\"" ]
                "the capture is an expression"
        }
        test "a script's own Ok/Error cases shadow the prelude's" {
            let diags =
                diagsIn "shadow.weir" [ "type Status ="; "    | Ok"; "    | Error"; "let s = Ok"; "print (show s)" ]

            Expect.isEmpty diags "no ambiguity with Result's cases"
        }
        test "match arms resolve by the union owning ALL their cases, not the first arm's" {
            // Ok alone prefers the script's Outcome; Ok + Error together
            // only fit Result — in either arm order
            for arms in
                [
                    "| Ok n -> show (n + 1) | Error e -> e"
                    "| Error e -> e | Ok n -> show (n + 1)"
                ] do
                let diags =
                    diagsIn "arms.weir" [
                        "type Outcome = Ok of int | Bad"
                        $"let g r = match r with {arms}"
                        "print (g (Error \"x\"))"
                    ]

                Expect.isEmpty diags arms

            // Ok + Bad: only Outcome owns both
            Expect.isEmpty
                (diagsIn "arms2.weir" [
                    "type Outcome = Ok of int | Bad"
                    "let h o = match o with | Ok n -> show n | Bad -> \"bad\""
                    "print (h Bad)"
                ])
                "the local union owning both"

            // no one union owns them all: say so, naming each case's owner
            let msgs =
                diagsIn "arms3.weir" [
                    "type A = X | Y"
                    "type B = Z | W"
                    "let g r = match r with | X -> 1 | Z -> 2 | _ -> 3"
                    "print (show (g X))"
                ]
                |> List.map (fun d -> d.Message)

            Expect.exists
                msgs
                (fun m -> m.Contains "no one union declares them all" && m.Contains "'Z' (B)")
                "the mixed arms"
        }
        test "Result members" {
            Expect.equal
                (run "(if 1 == 1 then fail \"x\" else 3) |> try result |> Result.defaultValue 7")
                (VInt 7L)
                "defaultValue"

            Expect.equal
                (run "3 |> try result |> Result.map (fun n -> n + 1) |> Result.toOption")
                (VUnion("Some", Some(VInt 4L)))
                "map + toOption"

            Expect.equal (run "3 |> try result |> Result.isOk") (VBool true) "isOk"
        }
    ]

// the GUIDE sweep's findings, pinned [D:sweep-findings]
let teardownSeamTests =
    // signal-path behaviour (a teardown requested by SIGINT/SIGTERM/EPIPE)
    // is process-global and one-way, so its pins are e2e cells; these pin
    // the seams it rests on
    testList "teardown seams" [
        test "runningCleanup marks the flow, nests, restores on a raise, and reaches tasks [D:teardown-always-spawns]" {
            Expect.isFalse (Weir.Session.inCleanup ()) "outside any always"

            let inner =
                Weir.Session.runningCleanup (fun () ->
                    Weir.Session.runningCleanup (fun () -> ()) |> ignore

                    let viaTask =
                        System.Threading.Tasks.Task.Run(fun () -> Weir.Session.inCleanup ()).Result

                    Weir.Session.inCleanup (), viaTask)

            Expect.equal inner (true, true) "inside, after a nested cleanup, and on a worker task"

            Expect.throws
                (fun () -> Weir.Session.runningCleanup (fun () -> failwith "boom") |> ignore)
                "the cleanup's raise propagates"

            Expect.isFalse (Weir.Session.inCleanup ()) "restored after a raise"
        }
        test "without a teardown, workers take every arm and spawns start [D:teardown-stops-arms]" {
            Expect.isFalse (Weir.Session.stopTakingArms ()) "no teardown requested"
            Expect.equal (Weir.Session.teardownRequested ()) 0 "no code"
            Expect.equal (Weir.Session.gatedSpawn (fun () -> 7)) 7 "the gate is open"
            expectValue "[1..200] |> Seq.pmapWith 16 (fun i -> i) |> Seq.length" (VInt 200L)
        }
    ]

let argsSecretEchoTests =
    // Script.run sets the ambient Session.ScriptArgs — sequenced
    let runArgs (lines: string list) (args: string list) : int =
        let path = tempPath "weir-args-secret" + ".weir"
        System.IO.File.WriteAllLines(path, lines)

        try
            Weir.Script.run path args
        finally
            System.IO.File.Delete path

    testSequenced
    <| testList "Args.load secret echo" [
        test "Args.load never echoes a Secret's value or an unknown flag's [D:args-secret-echo]" {
            let probe = [
                "type Cli = { token: Secret; port: int }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"h3-hunter\" m -> exit 3"
                "| Error m when Str.contains \"--token=***\" m && Str.contains \"write '--token <value>'\" m && Str.contains \"--tokn=…\" m -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs probe [ "--token=h3-hunter2"; "--tokn=h3-hunter3"; "--port"; "1" ]) 0 "masked"

            let optional = [
                "type Cli = { token: Option<Secret> }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"h3-hunter\" m -> exit 3"
                "| Error _ -> exit 0"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs optional [ "--token=h3-hunter2" ]) 0 "an Option<Secret> field is masked too"

            let plain = [
                "type Cli = { port: int }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"write '--port 8'\" m -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs plain [ "--port=8" ]) 0 "a plain field still teaches with its value"
        }
        test "Args.load never echoes a Secret's attached short value (-t=x, -tx) [D:args-secret-echo]" {
            let probe = [
                "type Cli = { tok: Secret; n: Option<int> }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"hunter\" m -> exit 3"
                "| Error m when Str.contains \"-t***\" m && Str.contains \"write '-t <value>'\" m -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs probe [ "-t=hunter2" ]) 0 "-t=value masked"
            Expect.equal (runArgs probe [ "-thunter2" ]) 0 "-tvalue masked"

            let unknown = [
                "type Cli = { tok: Secret }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"hunter\" m -> exit 3"
                "| Error m when Str.contains \"'-x…'\" m -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs unknown [ "-xhunter2" ]) 0 "an unknown letter may be a misspelled Secret"

            let shared = [
                "type GoArgs = { tok: Secret }"
                "type Sub = Go of GoArgs | Stop"
                "type Cli = { verbose: bool; cmd: Sub }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"hunter\" m -> exit 3"
                "| Error m when Str.contains \"-t***\" m -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs shared [ "go"; "-thunter2" ]) 0 "the shared-flags load masks too"

            let plain = [
                "type Cli = { n: int }"
                "match Args.load Cli |> try result with"
                "| Error m when Str.contains \"write '-n 8'\" m -> exit 0"
                "| Error m -> fail m"
                "| Ok _ -> exit 4"
            ]

            Expect.equal (runArgs plain [ "-n=8" ]) 0 "a plain field still teaches with its value"
        }
    ]

// the third hunt's behaviour fixes, pinned at their seams; the signal and
// pty halves are e2e cells
let hunt3BehaviourTests =
    testSequenced
    <| testList "hunt-3 behaviour" [
        test "after pfirst returns no loser effect lands; the loser stops at its next boundary [D:pfirst-join]" {
            withTempDir "weir-pfirst" (fun d ->
                let f = weirPath (Path.Combine(d, "out.txt"))

                expectValue
                    $"[1; 2] |> Seq.pfirst (fun n -> match n with | 1 -> Duration.sleep 600ms ; File.write \"{f}\" [\"loser\"] ; 1 | _ -> File.write \"{f}\" [\"winner\"] ; 2)"
                    (VInt 2L)

                System.Threading.Thread.Sleep 900

                Expect.equal
                    (File.ReadAllText(Path.Combine(d, "out.txt")).Trim())
                    "winner"
                    "the loser's write never ran")
        }
        test "exit n in a pfirst arm ends the race with that code [D:pfirst-join]" {
            let ex =
                Expect.throwsC
                    (fun () ->
                        run
                            "[1; 2] |> Seq.pfirst (fun n -> match n with | 1 -> exit 3 ; 0 | _ -> Duration.sleep 300ms ; n)"
                        |> ignore)
                    id

            match ex with
            | Weir.Session.ExitRequest 3 -> ()
            | e -> failtest $"expected exit 3, got {e}"
        }
        test "a race cancelled by an enclosing race ends without hanging [D:pfirst-join]" {
            expectValue
                "[1; 2] |> Seq.pfirst (fun i -> [10; 20] |> Seq.pfirst (fun j -> match i with | 1 -> (if j == 10 then Duration.sleep 50ms else Duration.sleep 2s) ; i * 100 + j | _ -> Duration.sleep 5s ; 0))"
                (VInt 110L)
        }
        test "a REPL ^C cuts a runtime-owned wait, through try; a cleanup's wait needs a second ^C [D:repl-interrupt]" {
            Weir.Session.beginReplEval ()

            try
                let sw = System.Diagnostics.Stopwatch.StartNew()

                System.Threading.Tasks.Task.Run(fun () ->
                    System.Threading.Thread.Sleep 200
                    Weir.Session.interruptRepl ())
                |> ignore

                Expect.throwsT<Weir.Session.Interrupted>
                    (fun () -> run "Duration.sleep 10s |> try result" |> ignore)
                    "the wait raises the interrupt and try passes it"

                Expect.isLessThan sw.ElapsedMilliseconds 5000L "cut, not waited out"

                Weir.Session.runningCleanup (fun () ->
                    Weir.Session.sleepInterruptible (System.TimeSpan.FromMilliseconds 50.0))

                Weir.Session.interruptRepl ()

                Expect.throwsT<Weir.Session.Interrupted>
                    (fun () ->
                        Weir.Session.runningCleanup (fun () ->
                            Weir.Session.sleepInterruptible (System.TimeSpan.FromSeconds 10.0)))
                    "a second ^C cuts the cleanup's wait"
            finally
                Weir.Session.endReplEval ()

            expectValue "Duration.sleep 10ms" VUnit
        }
        test "an env key that is empty or holds '=' is refused where it is built [D:env-key-refusal]" {
            let msg input =
                (Expect.throwsC (fun () -> run input |> forceSeq |> ignore) id).Message

            Expect.stringContains (msg "[Env.pair \"FOO=BAR\" \"x\"]") "the env key 'FOO=BAR' contains '='" "Env.pair"
            Expect.stringContains (msg "Env.ofPairs [(\"PATH=/x\", \"y\")]") "Env.ofPairs" "Env.ofPairs names itself"
            Expect.stringContains (msg "Env.ofPairs [(\"\", \"y\")]") "an env key is empty" "empty"
            // NUL renders as the error-data rule escapes it [D:error-data-escape]
            Expect.equal
                (msg "Env.ofPairs [(\"A\\u{0}B\", \"y\")]")
                "Env.ofPairs: the env key 'A\\x00B' contains a NUL byte — an env name is NUL-free"
                "NUL escaped, not '?'"

            // refused where built: binding the env raises, no reader needed
            Expect.stringContains
                (Expect.throwsC (fun () -> run "let e = Env.ofPairs [(\"A=B\", \"x\")] in 1" |> ignore) id).Message
                "Env.ofPairs: the env key 'A=B' contains '='"
                "the build raises, before any read"

            Expect.equal (run "Env.ofPairs [(\"A\", \"1\")] |> Seq.length") (VInt 1L) "a plain key builds"
        }
        test "weir's temp dirs are owner-only [D:private-temp]" {
            skipOnWindows ()

            match run "Path.newTempDir ()" with
            | VStr d ->
                let mode = File.GetUnixFileMode d
                Directory.Delete d
                Expect.equal mode (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) "0700"
            | v -> failtest $"expected a path, got {v}"
        }
        test "the within-lock holder reads and writes the locked file [D:lock-advisory]" {
            skipOnWindows ()

            withTempDir "weir-lock" (fun d ->
                let f = weirPath (Path.Combine(d, "state.json"))
                File.WriteAllText(Path.Combine(d, "state.json"), "{}\n")

                expectValue
                    $"within lock \"{f}\" timeout=5s (File.write \"{f}\" [\"1\"] ; File.read \"{f}\" |> Seq.head)"
                    (VStr "1"))
        }
    ]

let hunt3r2CancelTests =
    testSequenced
    <| testList "hunt-3r2 cancellation" [
        test "a cut node spares a child its cleanup starts [D:teardown-always-spawns] [D:scope-tree]" {
            skipOnWindows ()

            let n =
                Weir.Session.openNode (Weir.Session.currentNode ()) Weir.Session.NodeKind.Arm

            Weir.Session.cutNode n 1 Weir.Session.CutReason.RaceLost true

            let sleeper () =
                System.Diagnostics.Process.Start("sleep", "10")

            let spared = sleeper ()
            let killed = sleeper ()
            Weir.Session.setCurrentNode n

            try
                Weir.Session.registerChild killed
                Expect.isTrue (killed.WaitForExit 5000) "a body's late child dies at registration"

                Weir.Session.runningCleanup (fun () ->
                    Weir.Session.registerChild spared
                    Expect.isFalse (spared.WaitForExit 300) "a cleanup's child runs on")
            finally
                Weir.Session.setCurrentNode null
                Weir.Session.closeNode n

                for p in [ spared; killed ] do
                    Weir.Session.deregisterProc p

                    try
                        p.Kill true
                    with _ ->
                        ()
        }
        test "a node's children leave it as they exit; a closing node hands the live ones up [D:scope-tree]" {
            skipOnWindows ()

            let n =
                Weir.Session.openNode (Weir.Session.currentNode ()) Weir.Session.NodeKind.Arm

            Weir.Session.setCurrentNode n

            try
                for _ in 1..20 do
                    let p = System.Diagnostics.Process.Start("true")
                    Weir.Session.registerChild p
                    p.WaitForExit()
                    Weir.Session.deregisterProc p

                Expect.equal (Weir.Session.ownedCount n) 0 "exited children are gone"
                let live = System.Diagnostics.Process.Start("sleep", "10")
                Weir.Session.registerChild live
                Expect.equal (Weir.Session.ownedCount n) 1 "the live one is owned"
                Weir.Session.setCurrentNode null
                Weir.Session.closeNode n
                Expect.equal (Weir.Session.ownedCount n) 0 "handed up"
                Weir.Session.deregisterProc live
                live.Kill true
            finally
                Weir.Session.setCurrentNode null
        }
        test "after a REPL ^C a fan-out takes no arm and ends as the interrupt [D:repl-interrupt-fanout]" {
            withTempDir "weir-c4" (fun d ->
                let f = weirPath (Path.Combine(d, "arms.txt"))
                Weir.Session.beginReplEval ()

                try
                    Weir.Session.interruptRepl ()

                    for src in
                        [
                            $"[1..40] |> Seq.piterWith 2 (fun i -> File.append \"{f}\" [show i] ; fail \"arm\")"
                            $"[1..40] |> Seq.pmap (fun i -> File.append \"{f}\" [show i] ; fail \"arm\") |> Seq.length"
                            $"[1..40] |> Seq.pfirst (fun i -> File.append \"{f}\" [show i] ; fail \"arm\")"
                        ] do
                        Expect.throwsT<Weir.Session.Interrupted> (fun () -> run src |> ignore) src

                    Expect.throwsT<Weir.Session.Interrupted>
                        (fun () -> Weir.Session.gatedSpawn (fun () -> 7) |> ignore)
                        "a spawn is a ^C boundary"
                finally
                    Weir.Session.endReplEval ()

                Expect.isFalse (File.Exists(Path.Combine(d, "arms.txt"))) "no arm started"
                expectValue "[1..4] |> Seq.pmap (fun i -> i) |> Seq.length" (VInt 4L))
        }
        test "a lost race abandons a blocking call; an unraced one keeps its raise [D:stdin-reader]" {
            asArmCutAfter 200 (fun () ->
                let sw = System.Diagnostics.Stopwatch.StartNew()

                Expect.throwsT<Weir.Session.Cancelled>
                    (fun () ->
                        Weir.Session.cutBlocking (fun () ->
                            System.Threading.Thread.Sleep 10000
                            1)
                        |> ignore)
                    "the cut raises"

                Expect.isLessThan sw.ElapsedMilliseconds 5000L "abandoned, not waited out")

            asArmCutAfter 60000 (fun () ->
                Expect.throwsT<IOException>
                    (fun () -> Weir.Session.cutBlocking (fun () -> raise (IOException "fifo")) |> ignore)
                    "the call's own raise, unwrapped"

                Expect.equal (Weir.Session.cutBlocking (fun () -> 5)) 5 "a value passes")
        }
    ]

let hunt3r3TreeKillTests =
    let gone (pid: int) =
        let stat = $"/proc/{pid}/stat"

        not (File.Exists stat)
        || (try
                let st = File.ReadAllText stat
                st.Substring(st.LastIndexOf ')' + 2, 1) = "Z"
            with _ ->
                true)

    let childrenOf (pid: int) =
        try
            File
                .ReadAllText($"/proc/{pid}/task/{pid}/children")
                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.map int
            |> List.ofArray
        with _ -> []

    testList "hunt-3r3 tree kill" [
        test "a tree kill walks the tree: grandchildren, a setsid child included [D:tree-kill-walk]" {
            skipUnlessLinux ()

            use p =
                System.Diagnostics.Process.Start("sh", [| "-c"; "setsid sleep 31 & sleep 32 & wait" |])

            let clock = System.Diagnostics.Stopwatch.StartNew()
            let mutable kids = []

            while List.length kids < 2 && clock.ElapsedMilliseconds < 5000L do
                System.Threading.Thread.Sleep 20
                kids <- childrenOf p.Id

            try
                Expect.equal (List.length kids) 2 "both children started"
                Weir.Session.killTree p
                Expect.isTrue (p.WaitForExit 5000) "the child dies"
                System.Threading.Thread.Sleep 200

                for k in kids do
                    Expect.isTrue (gone k) $"grandchild {k} dies with the tree"
            finally
                for k in kids do
                    try
                        (System.Diagnostics.Process.GetProcessById k).Kill()
                    with _ ->
                        ()
        }
    ]

let hunt3r3FifoTests =
    testSequenced
    <| testList "hunt-3r3 polled FIFO" [
        test "a FIFO read is polled: a cut raises and closes it, the next reader gets the data [D:fifo-read-polled]" {
            skipUnlessLinux ()

            withTempDir "weir-fifo" (fun d ->
                let reg = Path.Combine(d, "plain.txt")
                File.WriteAllText(reg, "x")
                Expect.isNone (Weir.Session.openPolled reg) "a regular file opens as a file"
                let fifo = Path.Combine(d, "f")

                use mk = System.Diagnostics.Process.Start("mkfifo", [| fifo |])
                mk.WaitForExit()

                asArmCutAfter 200 (fun () ->
                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    Expect.throwsT<Weir.Session.Cancelled>
                        (fun () ->
                            match Weir.Session.openPolled fifo with
                            | Some s ->
                                use s = s
                                use r = new StreamReader(s)
                                r.ReadToEnd() |> ignore
                            | None -> failtest "a FIFO is polled")
                        "the cut raises"

                    Expect.isLessThan sw.ElapsedMilliseconds 3000L "no writer is waited for")

                let writer =
                    System.Threading.Tasks.Task.Run(fun () -> File.WriteAllText(fifo, "D1\nD2\n"))

                match Weir.Session.openPolled fifo with
                | Some s ->
                    use s = s
                    use r = new StreamReader(s)
                    Expect.equal (r.ReadToEnd()) "D1\nD2\n" "the next reader gets every byte"
                | None -> failtest "a FIFO is polled"

                Expect.isTrue (writer.Wait 5000) "the writer finished")
        }
    ]

let hunt3r3PromptKeyTests =
    testList "hunt-3r3 tty prompt keys" [
        test "a tty answer drops CSI and SS3 key sequences [D:prompt-tty-keys]" {
            let drop = Weir.Session.Stdin.dropKeySequences
            Expect.equal (drop "ab\u001b[Dc") "abc" "a left arrow"
            Expect.equal (drop "\u001bOAx\u001b[1;5Cy\u001b[3~") "xy" "SS3 up, ctrl-right, delete"
            Expect.equal (drop "plain é") "plain é" "plain text untouched"
            Expect.equal (drop "a\u001b") "a\u001b" "a lone ESC is kept"
        }
    ]

let hunt3r3UnreadTests =
    testSequenced
    <| testList "hunt-3r3 stdin put-back" [
        test "a line put back is the next prompt's, in order [D:repl-lookahead-unread]" {
            Weir.Session.Stdin.unread "two"
            Weir.Session.Stdin.unread "one"
            Expect.equal (Weir.Session.Stdin.readPromptLine ()) "one" "the last put back is first"
            Expect.equal (Weir.Session.Stdin.readPromptLine ()) "two" "then the earlier one"
        }
    ]

let hunt3r4RaceTests =
    testSequenced
    <| testList "hunt-3r4 races" [
        test "a race opened inside a cleanup cuts its own loser [D:cleanup-owns-its-cuts]" {
            let sw = System.Diagnostics.Stopwatch.StartNew()

            let v =
                Weir.Session.runningCleanup (fun () ->
                    run "[1; 2] |> Seq.pfirst (fun i -> match i with | 1 -> (Duration.sleep 5s ; 0) | _ -> i)")

            Expect.equal v (VInt 2L) "the fast arm wins"
            Expect.isLessThan sw.ElapsedMilliseconds 3000L "the loser is cut, not waited out"
        }
        test "exit n in a fan-out arm stops it and is never masked [D:fanout-exit]" {
            withTempDir "weir-fx" (fun d ->
                let f = weirPath (Path.Combine(d, "arms.txt"))

                for src in
                    [
                        $"[1; 2; 3] |> Seq.pmap (fun i -> match i with | 1 -> fail \"one\" | 2 -> (Duration.sleep 200ms ; exit 7) | _ -> (Duration.sleep 5s ; File.append \"{f}\" [\"late\"] ; i)) |> Seq.length"
                        $"[1; 2; 3] |> Seq.piterWith 3 (fun i -> match i with | 1 -> fail \"one\" | 2 -> (Duration.sleep 200ms ; exit 7) | _ -> (Duration.sleep 5s ; File.append \"{f}\" [\"late\"]))"
                    ] do
                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    match
                        (try
                            Ok(run src)
                         with e ->
                             Error e)
                    with
                    | Error(Weir.Session.ExitRequest 7) -> ()
                    | other -> failtest $"expected exit 7, got {other}: {src}"

                    Expect.isLessThan sw.ElapsedMilliseconds 3000L $"the in-flight arm is cut: {src}"

                Expect.isFalse (File.Exists(Path.Combine(d, "arms.txt"))) "no cut arm's write lands"

                match
                    (try
                        Ok(
                            run
                                $"[1; 2; 3] |> Seq.piterWith 1 (fun i -> File.append \"{f}\" [show i] ; match i with | 1 -> exit 4 | _ -> ())"
                        )
                     with e ->
                         Error e)
                with
                | Error(Weir.Session.ExitRequest 4) -> ()
                | other -> failtest $"expected exit 4, got {other}"

                Expect.equal (File.ReadAllLines(Path.Combine(d, "arms.txt"))) [| "1" |] "no new arm starts"
                expectValue "[1..4] |> Seq.pmap (fun i -> i) |> Seq.length" (VInt 4L))
        }
        test "exit n abandons an arm whose read no kill ends, nested fan-outs too [D:fanout-exit-reads]" {
            skipOnWindows ()

            withTempDir "weir-fxr" (fun d ->
                let f = weirPath (Path.Combine(d, "late.txt"))
                let orphan = "(sleep 4 &) ; sleep 4"

                for src in
                    [
                        $"[1; 2] |> Seq.pmap (fun i -> match i with | 1 -> (Duration.sleep 300ms ; exit 7) | _ -> (File.append \"{f}\" [show ($(sh -c \"{orphan}\") |> Seq.length)] ; 0)) |> Seq.length"
                        $"[1; 2] |> Seq.pmap (fun i -> match i with | 1 -> (Duration.sleep 300ms ; exit 7) | _ -> ([1; 2] |> Seq.pmap (fun _ -> (File.append \"{f}\" [show ($(sh -c \"{orphan}\") |> Seq.length)] ; 0)) |> Seq.length)) |> Seq.length"
                    ] do
                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    match
                        (try
                            Ok(runReal src)
                         with e ->
                             Error e)
                    with
                    | Error(Weir.Session.ExitRequest 7) -> ()
                    | Error e -> failtest $"expected exit 7, got {e.Message}: {src}"
                    | Ok v -> failtest $"expected exit 7, got {v}: {src}"

                    Expect.isLessThan sw.ElapsedMilliseconds 2500L $"the orphan-held read is abandoned: {src}"

                // the abandoned reads return when the orphans exit: nothing lands
                System.Threading.Thread.Sleep 4500
                Expect.isFalse (File.Exists(Path.Combine(d, "late.txt"))) "an abandoned arm never runs on")
        }
    ]

// hunt 3 round 5 stage A: the cleanup shim's start, the console-write
// policy, the FIFO probe; the signal and terminal halves are e2e cells
let hunt3r5Tests =
    let shim (script: string) (args: string list) =
        let psi = System.Diagnostics.ProcessStartInfo(Weir.Session.CleanupShim.shell)
        psi.ArgumentList.Add "-c"
        psi.ArgumentList.Add script
        psi.ArgumentList.Add "sh"

        for a in args do
            psi.ArgumentList.Add a

        System.Diagnostics.Process.Start psi

    let sigint (pid: int) =
        use k = System.Diagnostics.Process.Start("kill", $"-INT {pid}")
        k.WaitForExit()

    testSequenced
    <| testList "hunt-3r5 stage A" [
        test "a shim that died before its trap is reported, one past it is not [D:cleanup-sigign]" {
            skipUnlessLinux ()
            use early = shim "kill -INT $$; sleep 5" []
            Expect.isFalse (Weir.Session.CleanupShim.passedTrap early) "killed by INT before any trap"

            use slow = shim "sleep 0.3; trap \"\" INT QUIT; exec sleep 0.2" []
            System.Threading.Thread.Sleep 50
            sigint slow.Id
            Expect.isFalse (Weir.Session.CleanupShim.passedTrap slow) "a ^C before the trap"

            use ok = shim Weir.Session.CleanupShim.script [ "/bin/sleep"; "0.3" ]
            Expect.isTrue (Weir.Session.CleanupShim.passedTrap ok) "the trap ran"
            sigint ok.Id
            ok.WaitForExit()
            Expect.equal ok.ExitCode 0 "the command ignores the ^C"
        }
        test "a stderr write refused as EBADF marks the writer gone, never raises [D:console-write]" {
            let closed = {
                new System.IO.TextWriter() with
                    member _.Encoding = System.Text.Encoding.UTF8

                    member _.Write(s: string) : unit =
                        raise (System.UnauthorizedAccessException())

                    member _.WriteLine(s: string) : unit =
                        raise (System.UnauthorizedAccessException())
            }

            let w = new HangupWriter(closed)
            w.WriteLine "error: x"
            w.Write "more"
            Expect.isTrue w.Gone "the writer is gone after the first failure"
        }
        test "isFifo tells a FIFO from a file and an absent path [D:fifo-ops-polled]" {
            skipUnlessLinux ()

            withTempDir "weir-fifo" (fun d ->
                let f = Path.Combine(d, "p")
                let r = Path.Combine(d, "r")
                File.WriteAllText(r, "x")
                let mk = System.Diagnostics.Process.Start("mkfifo", f)
                mk.WaitForExit()
                Expect.isTrue (Weir.Session.isFifo f) "a FIFO"
                Expect.isFalse (Weir.Session.isFifo r) "a regular file"
                Expect.isFalse (Weir.Session.isFifo (Path.Combine(d, "none"))) "an absent path")
        }
    ]

// hunt 3 round 7 stage A: the shim's exec check follows the #! chain as
// execve does [D:cleanup-sigign]; the signal and thread halves are e2e cells
let hunt3r7Tests =
    testList "hunt-3r7 stage A" [
        test "the shim's exec check follows the #! chain to a loadable native image, as execve does [D:cleanup-sigign]" {
            skipUnlessLinux ()

            withTempDir "weir-exec" (fun d ->
                let file (name: string) (text: string) =
                    let p = Path.Combine(d, name)
                    File.WriteAllText(p, text)
                    File.SetUnixFileMode(p, enum<UnixFileMode> 0o755)
                    p

                let accepts p =
                    Weir.Session.CleanupShim.execAccepts p d (fun f -> File.Exists f)

                let sh = Weir.Session.CleanupShim.shell
                Expect.isTrue (accepts sh) "a native image"
                Expect.isTrue (accepts (file "s" "#!/bin/sh\necho s\n")) "a script of a native interpreter"
                let plain = file "plain" "echo plain\n"
                let gelf = file "gelf" "\u007fELF-not-an-image"
                Expect.isFalse (accepts plain) "a shebang-less file"
                Expect.isFalse (accepts gelf) "an ELF magic with no image behind it"
                Expect.isFalse (accepts (file "o1" $"#!{plain}\n")) "a script whose interpreter is shebang-less"
                Expect.isFalse (accepts (file "o2" $"#!{gelf}\n")) "a script whose interpreter is a garbage ELF"

                // five #! files then a native image run; a sixth is ELOOP
                let mutable top = sh

                for i in 1..6 do
                    top <- file $"c{i}" $"#!{top}\n"

                    if i <= 5 then
                        Expect.isTrue (accepts top) $"a chain of {i} #! files"
                    else
                        Expect.isFalse (accepts top) "a chain of six #! files")
        }
    ]

let hunt3PerfTests =
    let rec nest depth (leaf: Value) =
        if depth = 0 then
            leaf
        else
            VRecord("Node", [ "next", VUnion("Some", Some(nest (depth - 1) leaf)); "tag", VStr "t" ])

    // a value zoo: equal-but-differently-built pairs across every Eq kind
    let zoo = [
        VInt 1L
        VInt 1L
        VFloat 1.0
        VStr "a"
        VStr "a"
        VSecret "a"
        VSecret "a"
        VSecret "b"
        VBool true
        VUnit
        VDur 5L
        VSize 5L
        VInstant 5L
        VBytes [| 1uy; 2uy |]
        VBytes [| 1uy; 2uy |]
        VBytes [| 2uy; 1uy |]
        VTuple [ VStr "h"; VInt 1L ]
        VTuple [ VStr "h"; VInt 1L ]
        VTuple [ VInt 1L; VStr "h" ]
        VRecord("R", [ "a", VInt 1L; "b", VStr "x" ])
        VRecord("R", [ "b", VStr "x"; "a", VInt 1L ])
        VRecord("R", [ "a", VInt 2L; "b", VStr "x" ])
        VRecord("S", [ "a", VInt 1L; "b", VStr "x" ])
        VUnion("Some", Some(VInt 1L))
        VUnion("Some", Some(VInt 1L))
        VUnion("None", None)
        VMap(Map [ VStr "k", VInt 1L; VStr "j", VInt 2L ])
        VMap(Map [ VStr "j", VInt 2L; VStr "k", VInt 1L ])
        VMap(Map [ VStr "k", VInt 1L ])
        nest 7 (VInt 1L)
        nest 7 (VInt 1L)
        nest 7 (VInt 2L)
    ]

    testList "Hunt-3 performance cliffs" [
        test "Value hashes agree with Value equality across the zoo [D:value-hash]" {
            for a in zoo do
                for b in zoo do
                    if a = b then
                        Expect.equal
                            (a.GetHashCode())
                            (b.GetHashCode())
                            $"{formatValue a} = {formatValue b} but hashes differ"
        }
        test "a 200k-deep record hashes without recursion past the bound [D:value-hash] [D:eq-depth]" {
            let rec build n acc =
                if n = 0 then
                    acc
                else
                    build (n - 1) (VRecord("Node", [ "next", VUnion("Some", Some acc) ]))

            let a = build 200000 (VUnion("None", None))
            let b = build 200000 (VUnion("None", None))
            Expect.equal (a.GetHashCode()) (b.GetHashCode()) "equal deep values hash alike"

            Expect.equal
                (runWith [ "src", VSeq [ a; b ] ] "src |> Seq.distinct |> Seq.length")
                (VInt 1L)
                "distinct collapses the equal pair"
        }
        test "Seq.distinct matches the list-scan reference: order, Secret, record field order, depth [D:seq-distinct]" {
            let rng = System.Random 1789
            let pick () = zoo[rng.Next zoo.Length]

            for _ in 1..200 do
                let xs = List.init (rng.Next 40) (fun _ -> pick ())
                let got = runWith [ "src", VSeq xs ] "src |> Seq.distinct" |> forceSeq
                Expect.equal got (distinctReference xs) "same elements, same first-occurrence order"
                // structural equality is not enough here: the FIRST
                // spelling must survive (a record keeps its field order)
                List.iter2
                    (fun g r -> Expect.isTrue (obj.ReferenceEquals(g, r)) "the first occurrence itself")
                    got
                    (distinctReference xs)

            Expect.equal
                (runWith [ "src", VSeq [ VSecret "s"; VStr "s"; VSecret "s" ] ] "src |> Seq.distinct"
                 |> forceSeq)
                [ VSecret "s"; VStr "s" ]
                "a Secret equals only a Secret of the same text"
        }
        test "Seq.distinct grows linearly in its distinct count" {
            let distinctOver n =
                fun () ->
                    run $"[1..{n}] |> Seq.map (fun x -> ($\"host-{{x}}\", x %% 7)) |> Seq.distinct |> Seq.length"
                    |> ignore

            growsLinearly "Seq.distinct over tuples" (distinctOver 3000) (distinctOver 30000)
        }
        test "the chunked string state equals a rescan of the whole prefix [D:one-scanner]" {
            let rng = System.Random 1790
            let alphabet = [| "\""; "'"; "@"; "$"; "{"; "}"; "{{"; "\\"; "\"\"\""; "a"; " "; "//"; "$\"" |]

            for _ in 1..3000 do
                let lines =
                    List.init (1 + rng.Next 5) (fun _ ->
                        String.concat "" (List.init (rng.Next 8) (fun _ -> alphabet[rng.Next alphabet.Length])))

                let mutable prefix = ""
                let mutable state = Weir.Assemble.stringStart

                for l in lines do
                    Expect.equal
                        (Weir.Assemble.isInsideString state)
                        (Weir.Assemble.endsInsideString prefix)
                        $"after {prefix}"

                    prefix <- prefix + "\n" + l
                    state <- Weir.Assemble.stringStep state ("\n" + l)

                Expect.equal
                    (Weir.Assemble.isInsideString state)
                    (Weir.Assemble.endsInsideString prefix)
                    $"after {prefix}"
        }
        test "fmt grows linearly in a statement's physical line count" {
            let heredoc n =
                fun () ->
                    let src =
                        [ "let h = <<<" ]
                        @ List.init n (fun i -> $"    line {i} text {{x}} \"q\"")
                        @ [ "h |> Seq.iter print" ]

                    match Weir.Fmt.formatLines src with
                    | Ok out -> Expect.equal out src "already formatted"
                    | Error e -> failtest e

            growsLinearly "fmt of a heredoc" (heredoc 600) (heredoc 6000)
        }
        test "the env's open-scheme index equals a full filter through adds and shadowing [D:env-open-index]" {
            let closed = generalize (TFun(TVar "a", TVar "a"))
            let opened = mono (TVar "t9")

            let isOpen (sch: Scheme) =
                not (Set.isEmpty (tyVars sch.Ty - sch.Forall))

            let rng = System.Random 1791
            let mutable env = Weir.Builtins.typeEnv

            for _ in 1..2000 do
                let name = $"v{rng.Next 50}"
                env <- bindValue name (if rng.Next 3 = 0 then opened else closed) env

                Expect.isTrue
                    (env.OpenIndex
                     |> Option.exists (fun ix -> obj.ReferenceEquals(ix.For, env.Values)))
                    "bindValue leaves a current index"

                Expect.equal (openSchemes env) (env.Values |> Map.filter (fun _ s -> isOpen s)) "index = filter"

            // Values moved without bindValue: the stale index is ignored
            let other = {
                env with
                    Values = Map.add "w" opened env.Values
            }

            Expect.equal (openSchemes other) (other.Values |> Map.filter (fun _ s -> isOpen s)) "stale index rescans"
            Expect.equal (openSchemes (indexed other)) (openSchemes other) "indexed agrees"
        }
        test "a statement's check cost does not grow with the env's size [D:env-open-index]" {
            // the script fold's shape: each top-level binding extends
            // the env through bindValue, then the next statement checks
            let envOf n =
                [ 1..n ]
                |> List.fold (fun e i -> bindValue $"a{i}" (generalize TInt) e) Weir.Builtins.typeEnv

            let checks (tenv: TypeEnv) =
                let e = parse "fun x -> (x, a1 + 1)"

                fun () ->
                    for _ in 1..1000 do
                        match typecheck tenv e with
                        | Ok _ -> ()
                        | Error terr -> failtest (formatError terr)

            growsWithin 6.0 "1000 checks against a 100x larger env" (checks (envOf 500)) (checks (envOf 50000))
        }
        test "a line's position index follows its own segments; equality ignores it [D:line-pos-cache]" {
            match Weir.Assemble.assemble [ 1, "let x ="; 2, "    ls"; 3, "    |> Seq.map _.name" ] with
            | Ok [ ll ] ->
                Expect.equal (Weir.Assemble.translate ll 9) (2, 5) "col 9 is line 2 col 5"
                // re-pointed indents: the copy must not read the original's index
                let moved = Weir.Assemble.withPhysicalIndents (Map [ 2, 8 ]) ll
                Expect.equal (Weir.Assemble.translate moved 9) (2, 9) "the moved line reads its own indents"
                Expect.equal (Weir.Assemble.translate ll 9) (2, 5) "the original is untouched"
                Expect.notEqual moved ll "different segments, different lines"
                Expect.equal { ll with Ends = [||]; PosIndex = null } ll "equality is text, head and segments"
            | other -> failtest $"unexpected: {other}"
        }
        test "a plan's stale-read check costs O(depth), not O(captured ops) [D:plan-guard-index]" {
            let root = tempPath "weir-planidx"

            let loop n () =
                Weir.Eval.PlanMode.capturing (fun () ->
                    for i in 1..n do
                        let p = System.IO.Path.Combine(root, $"s{i % 100}", $"f{i}")
                        Weir.Eval.PlanMode.checkRead "File.exists" p
                        Weir.Eval.PlanMode.capture (VUnion("WriteFile", Some(VTuple [ VStr p; VSeq [] ]))) [ p ])
                |> ignore

            growsLinearly "write-if-absent, 10k vs 1k" (loop 1000) (loop 10000)
        }
        test "a plan's stale-read check costs O(depth) per path, not O(depth²) [D:plan-guard-linear]" {
            let root = tempPath "weir-plandepth"

            let loop depth () =
                let mid = System.IO.Path.Combine([| for i in 1..depth -> $"d{i}" |])

                Weir.Eval.PlanMode.capturing (fun () ->
                    for i in 1..2000 do
                        let p = System.IO.Path.Combine(root, mid, $"s{i % 100}", $"f{i}")
                        Weir.Eval.PlanMode.checkRead "File.exists" p
                        Weir.Eval.PlanMode.capture (VUnion("WriteFile", Some(VTuple [ VStr p; VSeq [] ]))) [ p ])
                |> ignore

            growsLinearly "write-if-absent, depth 300 vs 30" (loop 30) (loop 300)
        }
        test "refusing deep JSON is linear in its depth [D:json-deep-linear]" {
            let deep n =
                String.replicate n "{\"a\":" + "1" + String.replicate n "}"

            let refuse (text: string) () =
                match Weir.Types.parseJsonBounded text with
                | Error(Some _) -> ()
                | _ -> failtest "a deep document names the depth"

            growsLinearly "100k vs 10k deep" (refuse (deep 10000)) (refuse (deep 100000))
        }
    ]

// importing at the prompt [D:repl-import]: the session cwd anchors a
// relative path; the module rules are a script import's; a re-import
// reloads. Sequenced: the session cwd and Console.Out are process-wide
let replImportTests =
    testSequenced
    <| testList "REPL import" [
        let modText (v: string) = [
            "module M"
            "/// the greeting"
            "let greet : string -> string"
            "let greet n = $\"hi {n}\""
            "let v : int"
            $"let v = {v}"
        ]

        let write (path: string) (lines: string list) = File.WriteAllLines(path, lines)

        test "absolute, relative after cd, and weir: paths resolve against the session" {
            withTempDir "weir-replimp" (fun dir ->
                inCwd dir (fun () ->
                    let sub = Path.Combine(dir, "sub")
                    Directory.CreateDirectory(Path.Combine(dir, ".weir", "modules")) |> ignore
                    Directory.CreateDirectory sub |> ignore
                    write (Path.Combine(dir, "abs.weir")) (modText "1")
                    write (Path.Combine(sub, "rel.weir")) (modText "2")
                    write (Path.Combine(dir, ".weir", "modules", "vend.weir")) (modText "3")
                    let run, _ = Weir.Repl.sessionForTest ()
                    // forward slashes: a Windows path's `\` would be a string escape
                    let absPath = Path.Combine(dir, "abs.weir").Replace('\\', '/')

                    Expect.stringContains
                        (run $"import \"{absPath}\" as A")
                        "module A imported (2 members: greet, v)"
                        "absolute"

                    Expect.stringContains (run "A.v") "1 : int" "absolute value"
                    // cd moves the anchor: the same relative spelling meets sub/
                    Expect.stringContains (run "import \"./rel.weir\" as R") "no file at" "not in the start dir"
                    run "cd sub" |> ignore
                    Expect.stringContains (run "import \"./rel.weir\" as R") "module R imported" "relative after cd"
                    Expect.stringContains (run "R.v") "2 : int" "relative value"
                    // weir: walks up from the session cwd to dir/.weir
                    Expect.stringContains (run "import \"weir:vend\" as V") "module V imported" "weir: from a subdir"
                    Expect.stringContains (run "V.v") "3 : int" "weir: value"))
        }

        test "a non-module, a command at import and a module type error bind nothing" {
            withTempDir "weir-replimp" (fun dir ->
                inCwd dir (fun () ->
                    write (Path.Combine(dir, "plain.weir")) [ "let x = 1" ]
                    write (Path.Combine(dir, "cmd.weir")) [ "module C"; "let x : string"; "let x = echo hi | line" ]

                    write (Path.Combine(dir, "late.weir")) [
                        "module W"
                        "let helper () = sh -c \"echo val\""
                        "let _h = helper"
                        "let v : int"
                        "let v = (fun () -> helper ()) () |> Seq.length"
                    ]

                    write (Path.Combine(dir, "bad.weir")) [ "module B"; "let x : int"; "let x = \"s\"" ]
                    let run, env = Weir.Repl.sessionForTest ()
                    Expect.stringContains (run "import \"./plain.weir\" as P") "is not a module" "non-module"

                    Expect.stringContains
                        (run "import \"./cmd.weir\"")
                        "cannot run a command at import"
                        "check-time guard"

                    Expect.stringContains
                        (run "import \"./late.weir\"")
                        "runs a command while module W is imported"
                        "run-time guard"

                    let bad = run "import \"./bad.weir\""
                    Expect.stringContains bad "bad.weir:3:" "a module error names the module's own site"
                    Expect.stringContains bad "does not match its signature" "full typecheck"

                    for alias in [ "P"; "C"; "W"; "B" ] do
                        Expect.isFalse (Map.containsKey alias (env ()).Modules) $"{alias} bound nothing"

                    Expect.stringContains (run "W.v") "error" "the run-time refusal left no values"))
        }

        test "a re-import after an edit reloads; a new alias binds beside the old" {
            withTempDir "weir-replimp" (fun dir ->
                inCwd dir (fun () ->
                    let m = Path.Combine(dir, "m.weir")
                    write m (modText "1")
                    let run, env = Weir.Repl.sessionForTest ()
                    Expect.stringContains (run "import \"./m.weir\"") "module M imported" "first import"
                    Expect.stringContains (run "M.v") "1 : int" "first value"
                    write m ((modText "42") @ [ "let w : string"; "let w = \"new\"" ])

                    Expect.stringContains
                        (run "import \"./m.weir\"")
                        "module M reloaded (3 members"
                        "same alias reloads"

                    Expect.stringContains (run "M.v") "42 : int" "new value"
                    Expect.stringContains (run "M.w") "\"new\"" "new member"
                    write m (modText "7")
                    Expect.stringContains (run "import \"./m.weir\" as N") "module N imported" "a new alias"
                    Expect.stringContains (run "N.v") "7 : int" "the new alias reads the edit"
                    Expect.stringContains (run "M.w") "\"new\"" "the old alias stays"
                    // a re-import whose module changed a type's shape replaces it
                    write m [ "module M"; "type R = { a: int }"; "let r : R"; "let r = { a = 1 }" ]
                    run "import \"./m.weir\"" |> ignore

                    write m [
                        "module M"
                        "type R = { a: int; b: string }"
                        "let r : R"
                        "let r = { a = 2; b = \"x\" }"
                    ]

                    Expect.stringContains (run "import \"./m.weir\"") "module M reloaded" "the type changed"
                    Expect.stringContains (run "M.r.b") "\"x\"" "the new shape"
                    Expect.isFalse (Map.containsKey "w" (env ()).Modules["M"]) "the dropped member is gone"
                    // a builtin module name is never rebound
                    Expect.stringContains (run "import \"./m.weir\" as Str") "already a module in scope" "builtin"))
        }

        test "a reloaded type a second import also declares keeps its qualified key [D:qualified-type-names]" {
            withTempDir "weir-replimp" (fun dir ->
                inCwd dir (fun () ->
                    let m = Path.Combine(dir, "m.weir")
                    write m [ "module M"; "type R = { a: int }"; "let r : R"; "let r = { a = 1 }" ]

                    write (Path.Combine(dir, "o.weir")) [
                        "module O"
                        "type R = { c: bool }"
                        "let r : R"
                        "let r = { c = true }"
                    ]

                    let run, env = Weir.Repl.sessionForTest ()
                    Expect.stringContains (run "import \"./m.weir\"") "module M imported" "first provider"
                    Expect.stringContains (run "import \"./o.weir\"") "module O imported" "second provider"

                    write m [
                        "module M"
                        "type R = { a: int; b: string }"
                        "let r : R"
                        "let r = { a = 2; b = \"x\" }"
                    ]

                    Expect.stringContains (run "import \"./m.weir\"") "module M reloaded" "reload"
                    Expect.equal ((env ()).ModuleTypes["M"]["R"]) "M.R" "M's R stays qualified"
                    Expect.equal ((env ()).ModuleTypes["O"]["R"]) "O.R" "O's R stays qualified"
                    Expect.isFalse (Map.containsKey "R" (env ()).Types) "the bare name names neither"
                    Expect.stringContains (run "M.r.b") "\"x\"" "the new shape"
                    Expect.stringContains (run "O.r.c") "true" "the other import's type is untouched"
                    run "let q = M.R { a = 3; b = \"y\" }" |> ignore
                    Expect.stringContains (run "q.b") "\"y\"" "the qualified literal takes the new shape"
                    Expect.stringContains (run "M.R { a = 4 }") "error" "the old shape is gone"
                    Expect.stringContains (run "O.R { a = 5; b = \"z\" }") "error" "M.R and O.R stay distinct"))
        }

        test "#help and completion see a prompt import's members" {
            withTempDir "weir-replimp" (fun dir ->
                inCwd dir (fun () ->
                    write (Path.Combine(dir, "m.weir")) (modText "1")
                    let run, env = Weir.Repl.sessionForTest ()
                    run "import \"./m.weir\" as G" |> ignore
                    let help = run "#help G.greet"
                    Expect.stringContains help "G.greet : string -> string" "the signature"
                    Expect.stringContains help "the greeting" "the /// doc"

                    let offers = Weir.Sigs.suggestEntry Map.empty Map.empty (env ()) [ "G.gr" ] "G.gr" 0

                    Expect.contains offers "G.greet" "Tab completes the member"))
        }
    ]

// ---- receipts [D:receipts-read-only]: Completed.ok, and the three
// runtime-minted records refusing a literal and an update ----------------
// the import DAG's siblings check on worker threads, reported in load
// order [D:parallel-imports]: the diagnostics of a run are the sequential
// loader's, whichever thread checked which module
let parallelImportTests =
    testSequenced
    <| testList "parallel imports" [
        let write (dir: string) (name: string) (lines: string list) =
            let p = Path.Combine(dir, name)
            File.WriteAllLines(p, lines)
            p

        let errorsOf (entry: string) =
            let ds, _, _, _, _ =
                Weir.Analyze.analyzeLines entry (File.ReadAllLines entry |> List.ofArray)

            ds
            |> List.filter (fun d -> d.Severity = "error")
            |> List.map (fun d -> d.File, d.Line, d.Col, d.Message)

        let twoFailing (dir: string) =
            let a =
                write dir "a.weir" [ "module A"; "let bad : int -> int"; "let bad n = show n" ]

            let b =
                write dir "b.weir" [ "module B"; "let worse : int -> int"; "let worse n = n + \"x\"" ]

            let entry =
                write dir "two.weir" [
                    "import \"./a.weir\" as A"
                    "import \"./b.weir\" as B"
                    "print (show (A.bad 1))"
                    "print (show (B.worse 1))"
                ]

            a, b, entry

        test "two failing sibling modules report in load order, every run of 50" {
            withTempDir "weir-par-two" (fun dir ->
                let a, b, entry = twoFailing dir
                let first = errorsOf entry
                Expect.equal (List.length first) 2 "both modules' errors surface (the fold recovers)"
                let (fa, la, _, ma), (fb, lb, _, mb) = first[0], first[1]
                Expect.equal (fa, la) (a, 3) "a's error first, at its site"
                Expect.stringContains ma "does not match its signature" "a's message"
                Expect.equal (fb, lb) (b, 3) "b's error second"
                Expect.equal mb "expected int, got string" "b's message"

                for i in 2..50 do
                    Expect.equal (errorsOf entry) first $"run {i} reports the same diagnostics in the same order")
        }

        test "a slow earlier module keeps its place before a fast failing later one" {
            withTempDir "weir-par-slow" (fun dir ->
                let slowLines =
                    [ "module Slow" ]
                    @ ([ 1..200 ]
                       |> List.collect (fun i -> [ $"let f{i} : int -> int"; $"let f{i} n = n + {i}" ]))
                    @ [ "let tail : int -> int"; "let tail n = show n" ]

                let slow = write dir "slow.weir" slowLines

                let fast =
                    write dir "fast.weir" [ "module Fast"; "let worse : int -> int"; "let worse n = n + \"x\"" ]

                let entry =
                    write dir "sf.weir" [
                        "import \"./slow.weir\" as S"
                        "import \"./fast.weir\" as F"
                        "print (show (S.tail 1))"
                        "print (show (F.worse 1))"
                    ]

                // diagnostics publish sorted by file, so the fast module's
                // line leads; the slow one's is there, at its own site
                let first = errorsOf entry
                let (f1, _, _, _), (f2, l2, _, _) = first[0], first[1]
                Expect.equal f1 fast "the fast module's error"
                Expect.equal (f2, l2) (slow, List.length slowLines) "the slow module's error, at its site"

                for i in 2..20 do
                    Expect.equal (errorsOf entry) first $"run {i} reports the same diagnostics in the same order")
        }

        test "one worker is the sequential loader: the same diagnostics as the pool's" {
            withTempDir "weir-par-one" (fun dir ->
                let _, _, entry = twoFailing dir
                let pooled = errorsOf entry
                let saved = Weir.ModuleLoader.importWorkerBound
                Weir.ModuleLoader.importWorkerBound <- 1

                try
                    Expect.equal (errorsOf entry) pooled "the single-worker run"
                finally
                    Weir.ModuleLoader.importWorkerBound <- saved)
        }

        test "a cycle and a missing file keep the loader's messages" {
            withTempDir "weir-par-cycle" (fun dir ->
                write dir "ca.weir" [ "module Ca"; "import \"./cb.weir\" as Cb"; "let x : int"; "let x = Cb.y" ]
                |> ignore

                let cb =
                    write dir "cb.weir" [ "module Cb"; "import \"./ca.weir\" as Ca"; "let y : int"; "let y = Ca.x" ]

                let cyc = write dir "cyc.weir" [ "import \"./ca.weir\" as Ca"; "print (show Ca.x)" ]

                Expect.equal
                    (errorsOf cyc)
                    [ cb, 2, 8, "import cycle: ca.weir → cb.weir → ca.weir" ]
                    "the cycle, at its closing edge"

                let miss =
                    write dir "miss.weir" [ "import \"./nope.weir\" as N"; "print (show N.x)" ]

                let nope = Path.Combine(dir, "nope.weir")

                Expect.equal
                    (errorsOf miss)
                    [ miss, 1, 8, $"cannot resolve import: no file at {nope}" ]
                    "the missing file, at the import line")
        }

        test "a diamond checks once: both importers share one module body" {
            withTempDir "weir-par-diamond" (fun dir ->
                write dir "core.weir" [ "module Core"; "let one : int"; "let one = 1" ]
                |> ignore

                write dir "l.weir" [ "module L"; "import \"./core.weir\" as C"; "let a : int"; "let a = C.one" ]
                |> ignore

                write dir "r.weir" [ "module R"; "import \"./core.weir\" as C"; "let b : int"; "let b = C.one" ]
                |> ignore

                let entry =
                    write dir "d.weir" [
                        "import \"./l.weir\" as L"
                        "import \"./r.weir\" as R"
                        "import \"./core.weir\" as C"
                        "print (show (L.a + R.b + C.one))"
                    ]

                let ds, stmts, _, _, _ =
                    Weir.Analyze.analyzeLines entry (File.ReadAllLines entry |> List.ofArray)

                Expect.isEmpty ds "clean"

                let bodies =
                    stmts
                    |> List.collect (fun (_, c) ->
                        match c.Kind with
                        | Weir.Statements.KImport lm ->
                            (lm.Body
                             |> List.choose (function
                                 | _, Weir.Statements.CImport inner -> Some inner.Body
                                 | _ -> None))
                            @ [ lm.Body ]
                        | _ -> [])

                let coreBodies =
                    bodies
                    |> List.filter (fun b ->
                        b
                        |> List.exists (function
                            | _, Weir.Statements.CLet("one", _) -> true
                            | _ -> false))

                Expect.equal (List.length coreBodies) 3 "core reached through L, R and directly"

                Expect.isTrue
                    (coreBodies |> List.forall (fun b -> obj.ReferenceEquals(b, coreBodies[0])))
                    "one checked body, replayed once [D:module-once]")
        }
    ]

// one renderer, its limits explicit [D:one-value-renderer]: show and the
// echo force what they show, a debugger's look forces nothing
let valueRendererTests =
    testList "one value renderer under explicit limits" [
        test "a look forces nothing, at any depth; show and the echo still do" {
            let mutable pulled = 0

            let lazySeq () =
                VSeq(
                    seq {
                        pulled <- pulled + 1
                        yield VInt 1L
                        yield VInt 2L
                    }
                )

            let inRecord = VRecord("R", [ "n", VInt 7L; "xs", lazySeq () ])
            Expect.equal (Weir.Eval.inspectValue (lazySeq ())) "<lazy seq>" "an unforced seq, on its own"
            Expect.equal (Weir.Eval.inspectValue inRecord) "{ n = 7; xs = <lazy seq> }" "and inside a record"
            Expect.equal pulled 0 "nothing was enumerated"

            Expect.equal
                (Weir.Eval.inspectValue (VSeq [ for i in 1L .. 25L -> VInt i ]))
                ("[" + (String.concat "; " [ for i in 1..20 -> string i ]) + "; …]")
                "a forced seq shows, clipped"

            Expect.equal (Weir.Eval.formatValue (lazySeq ())) "[1; 2]" "show forces"
            Expect.equal pulled 1 "once"
        }
        test "an echo's text and its footer, for a terminal and for a pipe" {
            let struct (piped, noTail) = Weir.Eval.echoShown true (Some 100) (VStr "a\u001bb")

            let struct (atTty, _) = Weir.Eval.echoShown false (Some 100) (VStr "a\u001bb")
            Expect.equal piped "\"a\u001bb\"" "redirected: the bytes"
            Expect.isFalse (atTty.Contains "\u001b") "at a terminal: no raw escape"
            Expect.equal noTail "" "nothing clipped, no footer"

            let struct (clipped, tail) =
                Weir.Eval.echoShown true (Some 2) (VSeq(Seq.initInfinite (fun i -> VInt(int64 i))))

            Expect.equal clipped "[0; 1; …]" "an unforced seq shows its cap"
            Expect.equal tail " (first 2 of an unforced seq — Seq.freeze to echo everything)" "and the footer names it"
        }
    ]
