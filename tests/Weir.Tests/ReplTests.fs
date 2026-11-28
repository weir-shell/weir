module ReplTests

// The repl suites. A suite sits in the file of the area it mostly
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
open AdaptersTests
open CliTests
open FmtTests

let completionTests =
    testList "Completion" [
        test "completion works inside an interpolation hole [D:interp-hole-complete]" {
            // `$"{Path.<TAB>` must complete Path's members — the hole's
            // interior is an ordinary expression. Before the fix the opaque
            // `$"…"` made the slot read `micro $"{Path.` as command-argv and
            // try a filesystem completion of `Path.` (nothing).
            let hole = "micro $\"{Path."
            let inHole = suggest hole (Weir.Complete.wordStartAt hole hole.Length)
            let bare = suggest "Path." (Weir.Complete.wordStartAt "Path." 5)
            Expect.isNonEmpty inHole "the hole offers Path members"
            Expect.isTrue (inHole |> List.forall (fun s -> s.StartsWith "Path.")) $"all Path members: {inHole}"
            Expect.equal inHole bare "the hole completes exactly as its bare interior expression"
        }
        test "completion sees the whole entry: a binder on an earlier line types the head [D:entry-complete]" {
            // the REPL's Tab over a multi-line entry: the entry up to the
            // cursor (last line cut there), the cursor's line, the word start
            let entry (lines: string list) =
                let cur = List.last lines
                Weir.Sigs.suggestEntry Map.empty Map.empty env lines cur (Weir.Complete.wordStartAt cur cur.Length)

            let fileRow = [ "bytes"; "hidden"; "kind"; "modified"; "name"; "path"; "target" ]

            let under (head: string) =
                fileRow |> List.map (fun f -> head + "." + f)

            // the user's report: a for binder above, the cursor in a hole below
            Expect.equal
                (entry [
                    "for p in ls |> where (fun f -> f.kind == Directory) do"
                    "    print $\"THIS: {p."
                ])
                (under "p")
                "the for binder's element type completes inside the hole"

            Expect.equal
                (entry [ "for p in ls do"; "    print $\"THIS: {p.na" ])
                [ "p.name" ]
                "a prefix narrows inside the hole"

            Expect.equal
                (entry [ "ls |> Seq.map (fun x ->"; "    x." ])
                (under "x")
                "a fun binder on an earlier line, its paren still open"

            Expect.equal
                (entry [ "let rows = ls"; "rows |> Seq.map _." ])
                (under "_")
                "an earlier statement's let joins the env: the shorthand gets the element's fields"

            Expect.equal
                (entry [ "ls"; "|> where (fun f -> f.kind == Directory)"; "|> Seq.map _.na" ])
                [ "_.name" ]
                "a pipeline continued across lines types the shorthand's element"

            Expect.equal
                (entry [ "let r = {| zname = \"a\" |}"; "r.zn" ])
                [ "r.zname" ]
                "an anonymous record bound on an earlier line of the entry"

            Expect.equal (entry [ "for p in ls do"; "    print $\"{q." ]) [] "an unbound head still offers nothing"
        }
        test "the shorthand `_.` completes the pipeline's element [D:entry-complete]" {
            let fileRow = [
                "_.bytes"
                "_.hidden"
                "_.kind"
                "_.modified"
                "_.name"
                "_.path"
                "_.target"
            ]

            let ask (t: string) =
                suggest t (Weir.Complete.wordStartAt t t.Length)
            // a `for … in` header (or a let binder) leads the pipe's source
            Expect.equal (ask "for p in ls |> map _.") fileRow "a for header before the pipe"
            Expect.equal (ask "let n = ls |> Seq.map _.na") [ "_.name" ] "a let binder before the pipe"
            Expect.equal (ask "ls |> Seq.where _.hi") [ "_.hidden" ] "an argument to a pipe stage"
            Expect.equal (ask "ls |> map _.") fileRow "the stage-head spelling"
            // a bare shorthand stage over a seq never type-checks: nothing
            // (user-ruled); a record flowing in keeps its fields (the
            // [D:hole-completion] pin's `r |> _.`)
            Expect.equal (ask "for p in ls |> _.") [] "the user's report: a bare stage after a for header"
            Expect.equal (ask "ls |> _.") [] "a bare stage over a seq"
            Expect.equal (ask "ls\n|> _.") [] "a bare stage on a continuation line"
            // a long whitespace run after `for` with no ` in` answers at once
            let sw = System.Diagnostics.Stopwatch.StartNew()
            ask ("for" + String.replicate 4000 " " + "x |> map zz.") |> ignore
            Expect.isLessThan sw.ElapsedMilliseconds 2000L "the for-lead match is bounded"
            Expect.equal (ask "for p   in   ls |> map _.na") [ "_.name" ] "spaced for header"
            // the repair sees the shorthand too (the LSP's path)
            Expect.equal
                (Weir.Diagnostics.repairedFields env "ls |> where (fun f -> f.kind == Directory) |> Seq.map _." "")
                (Some fileRow)
                "repairedFields types the shorthand as a lambda over the element"

            Expect.equal
                (Weir.Diagnostics.repairedFields env "ls |> where (fun f -> f.kind == Directory) |> _." "")
                None
                "repairedFields: a bare stage over a seq types to no record"
        }
        test "argv path completion keeps the directory prefix [D:complete-argv]" {
            // the word rule is where this lived: both callers cut the word at the
            // slash, so `micro ci/e` completed against the CWD and `micro ci/`
            // listed it whole. filesystemComplete was always correct — the
            // directory half never reached it.
            Expect.equal (Weir.Complete.wordStartAt "micro ci/e" 10) 6 "a path is ONE word, slash included"
            Expect.equal (Weir.Complete.wordStartAt "micro ci/check-f" 16) 6 "a hyphen is part of a filename"
            // a Windows drive prefix belongs to its path — the CI runner's temp dir
            // is C:/…, which is how this surfaced. `:` elsewhere must not glue, or a
            // yaml key would swallow its value.
            Expect.equal (Weir.Complete.wordStartAt "micro C:/Users/x/e" 18) 6 "a drive letter leads its path"
            Expect.equal (Weir.Complete.wordStartAt "key:value" 9) 4 "a colon after a word is a separator"

            let d = tempDir "weir-comp"
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "e2e.sh"), "")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "check-fresh.sh"), "")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, ".hidden"), "")
            let dir = d.Replace("\\", "/")
            // absolute, so the pin does not depend on the runner's cwd
            let ask (w: string) =
                let text = "micro " + w
                suggest text (Weir.Complete.wordStartAt text text.Length)
            // asserted as (one match, prefix preserved, right entry) rather than
            // string equality: that still fails on the bug — completing against the
            // CWD gives many matches and drops the prefix — while surviving platform
            // separator variance, which equality does not.
            let one (label: string) (w: string) (name: string) =
                let got = ask w
                Expect.equal (List.length got) 1 (label + ": exactly one match")
                Expect.isTrue (got.Head.StartsWith w) (label + ": prefix preserved")
                Expect.isTrue (got.Head.EndsWith name) (label + ": right entry")

            one "a directory prefix scopes the search" (dir + "/e") "e2e.sh"
            one "a hyphen does not restart the word" (dir + "/check-") "check-fresh.sh"
            // the dotfile law, mirrored from Path.glob: a leading '.' is typed
            Expect.isFalse
                (ask (dir + "/") |> List.exists (fun s -> s.EndsWith ".hidden"))
                "a bare Tab does not bury entries under dotfiles"

            one "a typed dot offers them" (dir + "/.") ".hidden"
            System.IO.Directory.Delete(d, true)
        }

        test "name completion from values in scope" {
            // gained "when" when keyword suggestions began deriving
            // from the full parser set [D:keyword-completion]
            Expect.equal (suggest "ls |> whe" 6) [ "when"; "where" ] ""
        }
        test "a $name splice in argv completes session bindings [D:argv-splice-complete]" {
            // `$` is not a word char, so the word starts after it and the
            // before-text ends with `$`: `--location $l<TAB>` must offer the
            // binding `location` (bare — the $ is already typed), never
            // an empty filesystem match.
            let envL = {
                env with
                    Values =
                        env.Values
                        |> Map.add "location" (Weir.Types.mono Weir.Types.TStr)
                        |> Map.add "logLevel" (Weir.Types.mono Weir.Types.TStr)
            }

            let ask (text: string) =
                Weir.Complete.suggest envL text (Weir.Complete.wordStartAt text text.Length)

            let cands = ask "az vm list-usage --location $l"
            Expect.contains cands "location" "$l offers the location binding"
            Expect.contains cands "logLevel" "prefix $l offers every l-binding"

            Expect.isFalse
                (cands |> List.exists (fun c -> c.StartsWith "$"))
                "candidates are bare (the $ is already before the word)"

            // a bare argv word (no $) stays the paths-only pool — a binding
            // name must not leak in as an argv word
            let bare = ask "az vm list-usage --location loc"
            Expect.isFalse (List.contains "location" bare) "a non-splice argv word is not a binding pool"
        }
        test "a $ splice offers only the bindings the checker admits there, after any head [D:argv-splice-complete]" {
            // the REPL's env carries the Prelude's bare functions
            // (take, toInt, tryHead…); `git tag $t<TAB>` offered them all
            let mono = Weir.Types.mono
            let fnTy = Weir.Types.TFun(Weir.Types.TStr, Weir.Types.TStr)

            let envT = {
                env with
                    Values =
                        env.Values
                        |> Map.add "tag" (mono Weir.Types.TStr)
                        |> Map.add "tcount" (mono Weir.Types.TInt)
                        |> Map.add "tflag" (mono Weir.Types.TBool)
                        |> Map.add "tid" (mono Weir.Types.TUuid)
                        |> Map.add "tpoint" (mono (Weir.Types.TNamed("Point", [])))
                        |> Map.add "tags" (mono (Weir.Types.TSeq Weir.Types.TStr))
                        |> Map.add "tfn" (mono fnTy)
                        |> Map.add "tdur" (mono Weir.Types.TDur)
            }

            Expect.isTrue
                (Map.containsKey "take" envT.Values && Map.containsKey "tryHead" envT.Values)
                "fixture: the Prelude's functions are in scope"

            let ask (text: string) =
                Weir.Complete.suggest envT text (Weir.Complete.wordStartAt text text.Length)

            // a record offers its stem, never itself: it splices only
            // through a field [D:splice-stems]
            let scalar = [ "tag"; "tcount"; "tflag"; "tid"; "tpoint." ]

            for line in
                [
                    "git tag $t"
                    "ls -l $t"
                    "cd $t"
                    "^git tag $t"
                    "let v = git tag $t"
                    "let v = ^ls $t"
                    "git log | grep $t"
                ] do
                Expect.equal (ask line) scalar $"{line}: the $x-spliceable bindings exactly (a record as its stem)"

            for line in [ "git tag $@t"; "let v = ls -l $@t" ] do
                Expect.equal (ask line) [ "tags" ] $"{line}: $@ offers seq<string> bindings"

            // a non-$ word keeps its own pool
            Expect.isFalse (List.contains "tag" (ask "git tag t")) "a bare argv word is not a binding pool"
        }
        test "a $ splice completes only where and what the checker splices [D:argv-splice-complete]" {
            let mono = Weir.Types.mono

            let envS =
                env
                |> declare "type Rq = { name: string; tags: seq<string>; n: int; f: float }"
                |> fun e -> {
                    e with
                        Values =
                            e.Values
                            |> Map.add "qs" (mono Weir.Types.TStr)
                            |> Map.add "qt" (mono Weir.Types.TInt)
                            |> Map.add "qfz" (mono (Weir.Types.TSeq Weir.Types.TStr))
                            |> Map.add "vr" (mono (Weir.Types.TNamed("Rq", [])))
                }

            let ask (text: string) =
                Weir.Complete.suggest envS text (Weir.Complete.wordStartAt text text.Length)

            // a plain string never splices: the `$` is literal there
            Expect.equal (ask "echo \"$q") [] "inside \"…\""
            Expect.equal (ask "echo \"a b $q") [] "inside \"…\" after a space"
            Expect.equal (ask "echo 'x $q") [] "inside '…'"
            // a splice glued to a word is a word-join the parser refuses
            Expect.equal (ask "echo --x=$q") [] "after --x="
            Expect.equal (ask "echo a$q") [] "glued to a word"
            // a record splices only through a field
            Expect.isFalse (List.contains "vr" (ask "echo $v")) "a bare record"
            Expect.equal (ask "echo $vr.") [ "vr.n"; "vr.name" ] "$r. offers the fields $ admits"
            Expect.equal (ask "echo $vr.na") [ "vr.name" ] "the field prefix filters"
            Expect.equal (ask "echo $@vr.") [ "vr.tags" ] "$@r. offers the seq<string> fields"
            Expect.equal (ask "echo $qs.") [] "a non-record has no fields"
            // the same filter in every command position
            for line in
                [
                    "echo $q"
                    "let z = $(echo $q"
                    "if true then echo $q"
                    "if true then ls else echo $q"
                    "for f in [\"a\"] do echo $q"
                    "match 1 with | _ -> echo $q"
                    "echo $\"{1}\" $q"
                ] do
                Expect.equal (ask line) [ "qs"; "qt" ] line

            Expect.equal (ask "let z = $(echo $@q") [ "qfz" ] "$@ inside $(…)"
        }
        test
            "a $ completes nothing where it is literal text: comments, text blocks, yaml quotes, comments and block scalars [D:argv-splice-complete]" {
            let envS = {
                env with
                    Values = env.Values |> Map.add "qs" (Weir.Types.mono Weir.Types.TStr)
            }

            let ask (text: string) =
                Weir.Complete.suggest envS text (Weir.Complete.wordStartAt text text.Length)

            Expect.equal (ask "echo a // $q") [] "a // comment"
            Expect.equal (ask "echo a // $@q") [] "a // comment, $@"
            Expect.equal (ask "echo \"a // b\" $q") [ "qs" ] "a // inside a string is no comment"

            // the district is read off the lines above (LSP and REPL Tab)
            let literal (above: string list) (pre: string) =
                Weir.Complete.dollarLiteral (Array.ofList above) pre

            for above, pre in
                [
                    [ "let z = <<<" ], "    echo "
                    [ "let z = $<<<" ], "    echo "
                    [ "let z ="; "    <<<" ], "        a "
                    [ "let z = <<<"; "    a"; "" ], "        b "
                    [ "let z = yaml" ], "    a: \""
                    [ "let z = yaml" ], "    a: 'x "
                    [ "let z = yaml" ], "    - \""
                    [ "let z = yaml" ], "    \""
                    [ "let z = yaml" ], "    a: x # "
                    [ "let z = yaml" ], "    # "
                    [ "let z = yaml"; "    a: |" ], "      "
                    [ "let z = yaml"; "    a: |-"; "      x" ], "        "
                    [ "let z = yaml"; "    - |" ], "      x "
                ] do
                Expect.isTrue (literal above pre) $"literal: {above} / {pre}"

            for above, pre in
                [
                    [], "echo "
                    [ "let z = $$<<<" ], "    echo "
                    [ "let z = <<<"; "    a" ], "echo "
                    [ "let z = yaml" ], "    a: "
                    [ "let z = yaml" ], "    - "
                    [ "let z = yaml"; "    a:" ], "      b: "
                    [ "let z = yaml"; "    a: \"x\"" ], "    b: "
                    [ "let z = yaml"; "    a: |"; "      x" ], "b: "
                ] do
                Expect.isFalse (literal above pre) $"a splice: {above} / {pre}"
        }
        test "a line-head '#' completes the session directives, bare [D:repl-directives]" {
            // bare names: the editor's word starts after the '#', so
            // replacement yields `#help` — never `##help` or `head`
            // the closed set is the one source Complete.sessionDirectives
            // — the '#'-slot and the empty-prompt head both read it
            Expect.equal
                (suggest "#" 1)
                [
                    "alias"
                    "echo"
                    "find"
                    "help"
                    "history"
                    "infer"
                    "keys"
                    "quit"
                    "save"
                    "types"
                ]
                "the closed set"

            Expect.equal (suggest "#he" 1) [ "help" ] "the prefix filters"
            Expect.equal (suggest "#q" 1) [ "quit" ] ""
            Expect.isFalse (List.contains "head" (suggest "#he" 1)) "the general pool stays out"
            // mid-line '#' is not a directive slot
            Expect.isFalse (List.contains "help" (suggest "ls # he" 5)) "line-head only"
        }
        test "smart indent opens a level after the assembler's body openers only [D:repl-autoindent]" {
            for t in
                [
                    "let x ="
                    "if a then"
                    "else"
                    "let r = match x with"
                    "for f in fs do"
                    "| A ->"
                    "let f = fun x ->"
                    "within tmp d"
                    "within"
                    "    always"
                    "type T ="
                    "let z = // a comment"
                    "retry 3 times"
                ] do
                Expect.isTrue (Weir.Assemble.opensIndentedBody t) t

            for t in
                [
                    "let ok = 1 =="
                    "let le = a <="
                    "let ge = a >="
                    "let ne = a !="
                    "let r = { a ="
                    "let r = {| a ="
                    "let s = \"x ="
                    "let y = 1 // then"
                    "print athen"
                    "sudo"
                    "print 1"
                    "match x with"
                    "    match x with"
                    ""
                ] do
                Expect.isFalse (Weir.Assemble.opensIndentedBody t) t
        }
        test "#keys prints the key bindings, keys dim only at a tty [D:repl-keys]" {
            let plain = Weir.Repl.keysText false
            let lines = plain.Split '\n'

            for key in [ "Ctrl+Y"; "Ctrl+R"; "Alt+Enter" ] do
                Expect.isTrue (lines |> Array.exists (fun l -> l.TrimStart().StartsWith key)) $"{key} has a line"

            Expect.isFalse (plain.Contains "\u001b") "piped: no escapes"
            Expect.stringContains (Weir.Repl.keysText true) "\u001b[2mCtrl+Y" "tty: the keys dim"
            // aligned: every description starts in one column
            let starts =
                Weir.Repl.keyBindings
                |> List.map (fun (_, what) ->
                    lines
                    |> Array.find (fun l -> l.EndsWith what)
                    |> fun l -> l.Length - what.Length)
                |> List.distinct

            Expect.equal starts.Length 1 $"one description column: {starts}"
            Expect.contains Weir.Complete.sessionDirectives "keys" "#help, completion and did-you-mean see #keys"
        }
        test "an F1 tail queued behind a bare Esc reads as F1 [D:repl-f1-keys]" {
            Expect.equal (Weir.Repl.f1Tail "OP") (Some 2) "SS3 P"
            Expect.equal (Weir.Repl.f1Tail "[11~x") (Some 4) "CSI 11 ~, the next key stays queued"
            Expect.equal (Weir.Repl.f1Tail "[[A") (Some 3) "the Linux console"

            for other in [ ""; "\r"; "O"; "[1"; "[12~"; "[A"; "x" ] do
                Expect.isNone (Weir.Repl.f1Tail other) $"not F1: {other}"

            Expect.isTrue (Weir.Repl.keyBindings |> List.exists (fun (ks, _) -> ks = [ "F1" ])) "#keys lists F1"
        }
        test "docs/repl.md's key table lists exactly #keys' keys [D:repl-keys]" {
            let doc =
                System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "docs", "repl.md"))

            let table =
                doc.Substring(doc.IndexOf "| key | in the buffer |").Split('\n')
                |> Array.skip 2
                |> Array.takeWhile (fun l -> l.StartsWith "|")

            let docKeys =
                table
                |> Array.collect (fun row ->
                    let first = row.Split('|').[1]

                    System.Text.RegularExpressions.Regex.Matches(first, "<kbd>([^<]+)</kbd>")
                    |> Seq.map (fun m -> m.Groups[1].Value)
                    |> Array.ofSeq)
                |> Set.ofArray

            let codeKeys = Weir.Repl.keyBindings |> List.collect fst |> Set.ofList
            Expect.equal docKeys codeKeys "the docs table and #keys carry the same keys"
        }
        test "the `#help <arg>` slot completes the documentable universe [D:help-arg-complete]" {
            // an #infer-injected type lives only in env.Types — the general
            // pool never surfaced it, so `#help Patatas` documented it while
            // `#help Pat<TAB>` offered only Path (module) / Patch (a ctor).
            // The slot offers helpNames (modules + user forms + types), bare.
            let envP = env |> declare "type Patatas = { spuds: int }"
            let ask text pos = Weir.Complete.suggest envP text pos

            let pat = ask "#help Pat" 6
            Expect.contains pat "Patatas" "the injected type is now completable"
            // a stable module/type sibling still surfaces (not asserting an
            // exact list — a PATH exec would make that brittle)
            Expect.isFalse (List.isEmpty pat) "the slot fires"
            // bare names (so the editor yields `#help Patatas`, not doubled)
            Expect.isFalse (pat |> List.exists (fun c -> c.StartsWith "#")) "candidates are bare"

            // modules and types complete here — Contains, never an exact list
            let se = ask "#help Se" 6
            Expect.contains se "Seq" "a module"
            Expect.contains se "Secret" "another module"
            Expect.contains (ask "#help Si" 6) "Size" "…and another module"

            // the `#he` directiveSlot is unaffected — still the directive
            Expect.equal (suggest "#he" 1) [ "help" ] "directiveSlot stays as-is"

            // no general-position leak: a bare `Pat` at expression head does
            // not newly offer type names (the fix is scoped to the slot)
            Expect.isFalse
                (List.contains "Patatas" (Weir.Complete.suggest envP "Pat" 0))
                "types do not leak into the general/head pool"

            // a `Module.` prefix mirrors #help's dotted member help
            Expect.contains (ask "#help Seq.ma" 6) "Seq.map" "qualified members complete"
        }
        test "`#infer let x = <src>` completes the RHS as its let-statement [D:infer-one-shot]" {
            // the one-shot RHS is an ordinary source expression: strip the
            // `#infer ` prefix and complete `let x = <src>` — so members
            // (and bindings/heads, the same code as a normal let-RHS)
            // surface where the leading `#infer` used to hide them
            let ask (text: string) =
                suggest text (Weir.Complete.wordStartAt text text.Length)

            // a member completes in the RHS — the delegation fires
            Expect.contains (ask "#infer let y = xs |> Seq.ma") "Seq.map" "a member completes in the RHS"
            // the strip is the delta: `#infer ` off, only the let form
            Expect.equal (Weir.Complete.inferLetStrip "#infer let y = x") (Some 7) "strips '#infer '"
            Expect.equal (Weir.Complete.inferLetStrip "#infer it |> from json as N") None "only the let form strips"
            Expect.equal (Weir.Complete.inferLetStrip "let y = x") None "a plain let is not touched"
            // the general/head pool is unchanged outside the prefix
            Expect.isFalse (List.contains "Seq.map" (ask "Seq.z")) "no leak on a non-member word"
        }
        test "the `with ` slot offers the source record's fields [D:with-slot]" {
            let text = "{ Http.defaults with "
            let got = suggest text text.Length
            Expect.contains got "url" "a field of the source's record"
            Expect.contains got "timeout" "…and another"
            Expect.isFalse (List.contains "within" got) "never a keyword in the slot"

            // a typed prefix narrows to fields — the keyword-in-fragment
            // guard: `h` must not complete to `within`
            let text2 = "{ Http.get \"u\" with h"
            let got2 = suggest text2 (text2.Length - 1)
            Expect.contains got2 "headers" "the h-field"
            Expect.isFalse (List.contains "within" got2) "no keyword can enter an expression fragment"
        }
        test "the `with ` slot on a NON-record source is a closed EMPTY set [D:with-slot]" {
            // Http.get unapplied is a function — no updatable fields, so
            // nothing completes (the nats pin's reasoning; the general
            // pool would offer keywords that cannot appear there)
            let text = "{ Http.get with "
            Expect.equal (suggest text text.Length) [] "no members, no pool"
        }
        test "the typed VALUE slot: a known field type closes the candidate set [D:typed-value-slot]" {
            // bool: true/false, never the general pool's Str members
            let t1 = "{ Http.get \"u\" with insecure = tr"
            Expect.equal (suggest t1 (t1.Length - 2)) [ "true" ] "tr narrows to true alone"

            let t2 = "{ Http.get \"u\" with insecure = "
            Expect.equal (suggest t2 t2.Length) [ "false"; "true" ] "the whole closed set, empty prefix"

            // a union field offers its cases
            let t3 = "{ Http.get \"u\" with method = G"
            Expect.equal (suggest t3 (t3.Length - 1)) [ "Get" ] "the enum-shaped case"

            // a later field (after ';') still resolves
            let t4 = "{ Http.get \"u\" with url = \"x\"; insecure = "
            Expect.equal (suggest t4 t4.Length) [ "false"; "true" ] "second field, same slot"

            // a unit-typed field points at its module
            let t5 = "{ Http.get \"u\" with timeout = "
            Expect.contains (suggest t5 t5.Length) "Duration" "the Duration field's module"
        }
        test "`match x with ` is untouched by the with-slot (no brace-slice parses)" {
            let text = "match x with "
            Expect.isNonEmpty (suggest text text.Length) "the general pool still serves match"
        }
        test "keyword completion" { Expect.contains (suggest "ma" 0) "match" "" }
        test "command heads: a command-callable builtin completes at a statement head [D:repl-quality]" {
            // at a head (before empty) the command-callable set joins the pool
            Expect.contains (suggest "c" 0) "cd" "cd is command-callable at a head"
        }
        test "command heads: the let-RHS is a head slot [D:let-rhs-head]" {
            // the topLet RHS admits command mode, so its head takes the
            // same pool the statement head does — the command-callable
            // pin mirrored (never a PATH executable, the CI lesson)
            Expect.contains (suggest "let x = c" 8) "cd" "cd completes at the let-RHS head"
            // params keep the slot (`let f a b = <head>` is command-first too)
            Expect.contains (suggest "let f a b = c" 12) "cd" "a paramful binder keeps the slot"
            Expect.contains (suggest "let pure f = c" 13) "cd" "the pure modifier keeps the slot"
            // an empty word at the let-RHS stays pool-only — never the
            // whole PATH dump (the empty-prompt rationale). PATH-only
            // names (no binding/module/keyword shares the spelling) are
            // the discriminator — a pool name may legitimately coincide
            // with an executable
            let pathOnly =
                Weir.Extern.names ()
                |> Set.filter (fun n ->
                    not (Map.containsKey n env.Values)
                    && not (Map.containsKey n env.Modules)
                    && not (Weir.Parser.keywords.Contains n))

            Expect.isTrue
                (suggest "let x = " 8 |> List.forall (pathOnly.Contains >> not))
                "no PATH dump on an empty RHS word"

            // a destructuring let's RHS is expression-only (SLetPat, no
            // command mode) — the head extras stay out (PATH-only names
            // as the discriminator; the pool may share spellings)
            Expect.isTrue
                (suggest "let (a, b) = c" 13 |> List.forall (pathOnly.Contains >> not))
                "a pattern binder is not a head slot"
        }
        test "a binding head at the let-RHS keeps the general pool [D:let-rhs-head]" {
            // `let y = map wh…` is expression mode (bindings-beat-PATH):
            // the argv gate must not fire, so keywords/bindings still offer
            let got = suggest "let y = map wh" 12
            Expect.contains got "when" "keywords stay in expression mode"
            Expect.contains got "where" "…and bindings"
        }
        test "argv after a let-RHS command head follows the statement rule [D:let-rhs-head]" {
            // the binder strips and the remainder judges exactly as a
            // statement — paths, nothing else (the complete-argv gate)
            let sug (t: string) = suggest t (t.LastIndexOf ' ' + 1)
            let argv: string list = sug "let x = micro Weir.Tests."
            Expect.isTrue (argv |> List.contains "Weir.Tests.dll") "a real relative path completes in RHS argv"
            Expect.isFalse (argv |> List.exists (fun c -> c.EndsWith ".bytes")) "no fields in RHS argv"
            // the unbound-scrutinee D5 pin holds unchanged: a dotted
            // RHS head is not argv, and offers nothing
            Expect.equal (sug "let x = publish.") [] "unbound dotted head after ="
        }
        test "the ^-forced head completes PATH-only — statement head and let-RHS [D:let-rhs-head]" {
            // the pool is a subset of the PATH cache (asserting any one
            // executable would be brittle); the general pool's keywords
            // and bindings never enter behind the sigil
            let path = Weir.Extern.names ()
            let atStmt = suggest "^ma" 1
            let atRhs = suggest "let x = ^ma" 9
            Expect.isTrue (atStmt |> List.forall path.Contains) "statement-head ^ pool is PATH only"
            Expect.isTrue (atRhs |> List.forall path.Contains) "let-RHS ^ pool is PATH only"
            Expect.equal atRhs atStmt "one slot, one pool"
        }
        test "headSlotAt: the one head-slot predicate both surfaces read [D:let-rhs-head]" {
            let slot = Weir.Complete.headSlotAt
            Expect.equal (slot "") Weir.Complete.HeadSlot.Stmt "statement head"
            Expect.equal (slot "let x =") Weir.Complete.HeadSlot.LetRhs "let-RHS (completer's trimmed prefix)"
            Expect.equal (slot "let x = ") Weir.Complete.HeadSlot.LetRhs "let-RHS (colorizer's raw prefix)"
            Expect.equal (slot "^") Weir.Complete.HeadSlot.Forced "^ at the statement head"
            Expect.equal (slot "let x = ^") Weir.Complete.HeadSlot.Forced "^ at the let-RHS"
            Expect.equal (slot "let x = k") Weir.Complete.HeadSlot.No "past the head word"
            Expect.equal (slot "echo ") Weir.Complete.HeadSlot.No "argv is not a head slot"
            Expect.equal (slot "let x = f ") Weir.Complete.HeadSlot.No "RHS argv is not a head slot"

            Expect.equal
                (slot "  ")
                Weir.Complete.HeadSlot.No
                "an indented word is NOT a head to the colorizer (district bodies)"

            Expect.equal (slot "let (a, b) = ") Weir.Complete.HeadSlot.No "a pattern binder's RHS is expression-only"

            Expect.equal
                (slot "let for = ")
                Weir.Complete.HeadSlot.No
                "a keyword binder is the guard's error, not a slot"

            Expect.equal (slot "let x == ") Weir.Complete.HeadSlot.No "== is not a binder's ="
            Expect.equal (slot "git status |") Weir.Complete.HeadSlot.Stage "after a bare |"

            Expect.equal
                (slot "git status | and echo \"a|b\" | and ")
                Weir.Complete.HeadSlot.Stage
                "after | and, past a quoted |"

            Expect.equal (slot "let r = git log | ^") Weir.Complete.HeadSlot.Forced "^ at a stage head"
            Expect.equal (slot "git status | grep ") Weir.Complete.HeadSlot.No "a stage's argv"
            Expect.equal (slot "xs |>") Weir.Complete.HeadSlot.No "|> is an expression pipe"
            Expect.equal (slot "a ||") Weir.Complete.HeadSlot.No "|| is not a stage"
            Expect.equal (slot "| ") Weir.Complete.HeadSlot.No "a match arm on its own line"
            Expect.equal (slot "match x with | A -> 1 |") Weir.Complete.HeadSlot.No "an inline match arm"
        }
        test "a pipeline stage head completes programs and reifiers [D:cmd-chaining]" {
            let at (t: string) =
                suggest t (Weir.Complete.wordStartAt t t.Length)

            let chained = at "git status | and echo \"XXX\" | s"
            Expect.contains chained "succeeds" "a reifier right after |"
            Expect.contains chained "sh" "a PATH program"
            Expect.isFalse (chained |> List.contains "Seq") "no expression pool"
            let afterOr = at "git status | or s"
            Expect.contains afterOr "sh" "a program after | or"
            Expect.isFalse (afterOr |> List.contains "succeeds") "no reifier as an or's command"

            Expect.equal
                (at "git status | ")
                [
                    "and"
                    "bytes"
                    "complete"
                    "exec"
                    "exitCode"
                    "into"
                    "line"
                    "or"
                    "orFail"
                    "succeeds"
                    "text"
                ]
                "an empty stage word offers just the chain words"
        }
        test "session alias heads: known at both head slots, completable, ^-bypassed [D:command-head-alias]" {
            // the one membership [D:repl-color] gains the alias table's
            // names: `#alias k = kubectl` makes `k` a known head — at
            // the statement head and the let-RHS — through the same
            // verdict fn the live repaint uses
            let known = Weir.Repl.knownWithAliasesForTest (Set.ofList [ "k" ])
            Expect.isTrue (known "k") "an alias head is known"
            Expect.isFalse (Weir.Repl.knownForTest "k") "…and only via the alias set"

            let colorize = Weir.ReplColor.colorizeRepl known
            Expect.stringContains (colorize "k get pod -A") "\x1b[1mk\x1b[0m" "alias head bold at the statement head"
            Expect.stringContains (colorize "let x = k get pod") "\x1b[1mk\x1b[0m" "alias head bold at the let-RHS"
            // the ^ sigil skips the table [D:command-head-alias]: `^k`
            // is a PATH lookup of literal `k` — the alias must not
            // paint it (PATH-membership decides; no `k` binary here
            // means red, and never bold)
            Expect.isFalse (colorize "^k ls" |> _.Contains("\x1b[1mk\x1b[0m")) "^k ignores the alias"
            Expect.isFalse (colorize "let x = ^k ls" |> _.Contains("\x1b[1mk\x1b[0m")) "^k ignores the alias at the RHS"

            // completion: the alias name joins the head pool at both
            // slots (alias-to-anything — nothing asserts a PATH exe),
            // never the ^-forced PATH pool
            let ask = Weir.Complete.suggestSession (Set.ofList [ "kzz" ]) Map.empty env
            Expect.contains (ask "kz" 0) "kzz" "alias completes at the statement head"
            Expect.contains (ask "let x = kz" 8) "kzz" "alias completes at the let-RHS head"
            Expect.isFalse (List.contains "kzz" (ask "^kz" 1)) "the ^ pool skips the alias table"
            Expect.isFalse (List.contains "kzz" (ask "let x = ^kz" 9)) "…at the RHS too"
        }
        test "map-key completion: a bound value's keys inside a Map lookup literal [D:value-key-complete]" {
            // the stored value's own keys complete inside the open key
            // literal of a pipe-form Map lookup — reading the Values
            // table is peeking, never evaluating
            let vals: Map<string, Value> =
                Map.ofList [
                    "d", VMap(Map.ofList [ VStr "CoreCount", VStr "4"; VStr "Corge", VStr "g"; VStr "Zone", VStr "b" ])
                    "ps", VSeq([ VTuple [ VStr "CoreCount"; VStr "4" ]; VTuple [ VStr "Zone"; VStr "b" ] ]: Value list)
                    "lazyPs", VSeq(Seq.init 2 (fun i -> VTuple [ VStr $"K{i}"; VStr "v" ]))
                    "n", VInt 1L
                ]

            let ask (line: string) =
                Weir.Complete.suggestSession Set.empty vals env line (Weir.Complete.wordStartAt line line.Length)

            // a VMap binding: prefix-filtered, sorted, closes nothing
            Expect.equal (ask "d |> Map.tryGet \"Cor") [ "CoreCount"; "Corge" ] "map keys, prefix-filtered and sorted"
            Expect.equal (ask "d |> Map.get \"Zo") [ "Zone" ] "Map.get is the same lookup slot"
            Expect.equal (ask "d |> Map.has \"Zo") [ "Zone" ] "Map.has too"
            Expect.equal (ask "d |> Map.tryGet \"") [ "CoreCount"; "Corge"; "Zone" ] "an empty literal offers every key"

            // a materialized pair-seq offers exactly as a map does
            Expect.equal (ask "ps |> Map.tryGet \"Cor") [ "CoreCount" ] "a materialized pair-seq offers its keys"

            // the let-RHS spelling is the same slot [D:let-rhs-head]
            Expect.equal (ask "let x = d |> Map.tryGet \"Cor") [ "CoreCount"; "Corge" ] "the let-RHS form completes"

            // `it` is an ordinary Values entry — the receiver rule admits it
            let itVals = Map.add "it" vals["d"] Map.empty

            Expect.equal
                (Weir.Complete.suggestSession Set.empty itVals env "it |> Map.tryGet \"Cor" 18)
                [ "CoreCount"; "Corge" ]
                "it as receiver, when materialized"

            // the bind-first law: a pipeline receiver would need evaluating
            // — the slot claims and offers nothing (never the general pool)
            Expect.equal (ask "d |> Seq.map fst |> Map.tryGet \"Cor") [] "a pipeline receiver offers nothing"

            // the never-executes law's sharp edge: an unforced seq is not
            // pulled — nothing offers
            Expect.equal (ask "lazyPs |> Map.tryGet \"K") [] "an unforced receiver offers nothing"

            // a non-map binding and an unbound receiver claim silently too
            Expect.equal (ask "n |> Map.tryGet \"x") [] "a non-map receiver offers nothing"
            Expect.equal (ask "ghost |> Map.tryGet \"x") [] "an unbound receiver offers nothing"

            // the applied spelling has no receiver at the cursor — the
            // slot does not fire (pipe-form only, stated)
            Expect.isFalse (ask "Map.tryGet \"Cor" |> List.contains "CoreCount") "the applied spelling offers no keys"
        }
        test "map-key completion leaves non-lookup string positions alone [D:value-key-complete]" {
            skipOnWindows ()
            // a path literal still path-completes — the slot only claims
            // the lookup shape
            let vals: Map<string, Value> =
                Map.ofList [ "d", VMap(Map.ofList [ VStr "etc", VStr "x" ]) ]

            let line = "File.read \"/etc/hos"

            let hits =
                Weir.Complete.suggestSession Set.empty vals env line (Weir.Complete.wordStartAt line line.Length)

            Expect.isTrue
                (hits |> List.exists (fun p -> p.Contains "host"))
                $"path-in-quotes still path-completes: {hits}"
        }
        test "path completion: an explicit path lists the directory [D:repl-quality]" {
            skipOnWindows ()
            // /etc/hos* exists on every Linux box; a `/`-word is a path
            let hits = suggest "/etc/hos" 0
            Expect.isTrue (hits |> List.exists (fun p -> p.StartsWith "/etc/host")) $"path completion: {hits}"
        }
        test "path completion never runs anything: only a directory read [D:repl-quality]" {
            // completing a path yields entries without executing a program —
            // the marker file a run would leave is absent
            let marker =
                weirPath (System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-complete-marker"))

            if System.IO.File.Exists marker then
                System.IO.File.Delete marker

            suggest "/etc/hos" 0 |> ignore
            suggest "cd" 0 |> ignore
            suggest "File.read ./e" 10 |> ignore
            Expect.isFalse (System.IO.File.Exists marker) "completion must not execute"
        }
        test "expression-position path completion quotes; command-argv stays bare [D:repl-path-quote]" {
            skipOnWindows ()
            // a bare filesystem path is not a valid weir expression, so a
            // path completed as a function argument must come back quoted —
            // otherwise the line the editor builds fails to parse on Enter.
            let d = tempPath "weir-pq"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "sub")) |> ignore

            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "note.txt"), "x")
            let dir = d.Replace("\\", "/")

            try
                let ask (line: string) =
                    suggest line (Weir.Complete.wordStartAt line line.Length)

                // (a) after a function head the completion is quoted and the
                // line it produces parses — a bare path would not
                let fnLine = "File.read " + dir + "/n"
                let fnGot = ask fnLine
                Expect.equal (List.length fnGot) 1 "one match"
                Expect.isTrue (fnGot.Head.StartsWith "\"") "the expression-slot path is quoted"
                Expect.isTrue (fnGot.Head.EndsWith "note.txt\"") "…around the real entry"

                // the produced line parses (a bare path would be a parse error)
                let ws = Weir.Complete.wordStartAt fnLine fnLine.Length
                let produced = fnLine.Substring(0, ws) + fnGot.Head

                match Weir.Parser.parseExpr produced with
                | Ok _ -> ()
                | Error e -> failtestf "the completed line must parse: %s -> %s" produced e

                // (b) command-argv stays bare — cat takes an unquoted argv path
                let argvGot = ask ("cat " + dir + "/n")
                Expect.equal (List.length argvGot) 1 "one match"
                Expect.isFalse (argvGot.Head.StartsWith "\"") "an argv path is never quoted"
                Expect.isTrue (argvGot.Head.EndsWith "note.txt") "the bare entry"

                // (c) completing inside an already-open quote does not double
                let inQuote = ask ("File.read \"" + dir + "/n")
                Expect.equal (List.length inQuote) 1 "one match"
                Expect.isFalse (inQuote.Head.StartsWith "\"") "no second opening quote inside the string"
                Expect.isTrue (inQuote.Head.EndsWith "note.txt") "the entry lands inside the quotes"

                // (d) a directory keeps its trailing '/' — inside the quotes
                let dirGot = ask ("File.read " + dir + "/s")
                Expect.equal (List.length dirGot) 1 "one match"
                Expect.equal dirGot.Head ("\"" + dir + "/sub/\"") "trailing slash survives inside the quotes"
            finally
                System.IO.Directory.Delete(d, true)
        }
        test
            "a completed path is spelled for its slot: argv quotes what a bare word cannot hold, literals escape [D:repl-path-quote]" {
            skipOnWindows ()
            let d = tempPath "weir-pq2"
            System.IO.Directory.CreateDirectory d |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "my file.txt"), "x")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "qu\"ote.txt"), "x")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "br{a}ce.txt"), "x")
            let dir = d.Replace("\\", "/")

            try
                let ask (line: string) =
                    suggest line (Weir.Complete.wordStartAt line line.Length)

                let one (line: string) =
                    match ask line with
                    | [ c ] -> c
                    | got -> failtestf "one candidate for %s, got %A" line got

                // argv: a space or a quote would split/end the word — one
                // string literal is one argv word
                Expect.equal (one ("cat " + dir + "/my")) ("\"" + dir + "/my file.txt\"") "argv: a space is quoted"

                Expect.equal
                    (one ("cat " + dir + "/qu"))
                    ("\"" + dir + "/qu\\\"ote.txt\"")
                    "argv: a quote is escaped inside quotes"
                // an expression slot escapes inside its literal
                Expect.equal (one ("File.read " + dir + "/qu")) ("\"" + dir + "/qu\\\"ote.txt\"") "expression: escaped"
                // inside an open literal: escaped for that literal's kind
                Expect.equal (one ("File.read \"" + dir + "/qu")) (dir + "/qu\\\"ote.txt") "inside \"…: \\\""

                Expect.equal
                    (one ("File.read @\"" + dir + "/qu"))
                    (dir + "/qu\"\"ote.txt")
                    "inside @\"…: a doubled quote"

                Expect.equal
                    (one ("File.read $\"" + dir + "/br"))
                    (dir + "/br{{a}}ce.txt")
                    "inside $\"…: doubled braces"

                // every spelling parses back to the real path
                for line in [ "cat " + dir + "/my"; "cat " + dir + "/qu"; "File.read " + dir + "/qu" ] do
                    let ws = Weir.Complete.wordStartAt line line.Length
                    let produced = line.Substring(0, ws) + one line

                    match Weir.Parser.parseExpr produced with
                    | Ok _ -> ()
                    | Error e -> failtestf "the completed line must parse: %s -> %s" produced e
            finally
                System.IO.Directory.Delete(d, true)
        }
        test "command argv completes paths, never fields — and an unbound scrutinee offers NOTHING [D:complete-argv]" {
            let sug (t: string) =
                // the word under completion runs from after the last
                // space to the cursor
                suggest t (t.LastIndexOf ' ' + 1)

            // the repro: `micro publish.` offered every record field
            // weir knows, unioned — argv is not an expression
            let argv: string list = sug "micro publish."
            Expect.isFalse (argv |> List.exists (fun c -> c.EndsWith ".attempts")) "no Retry fields in argv"
            Expect.isFalse (argv |> List.exists (fun c -> c.EndsWith ".bytes")) "no FileRow fields in argv"

            // paths do complete there (the fallback exists): the test
            // bin dir is the cwd and its own dll is a stable entry
            let paths: string list = sug "micro Weir.Tests."
            Expect.isTrue (paths |> List.contains "Weir.Tests.dll") "a real relative path completes in argv"

            // every candidate extends the typed word — the editor
            // replaces the word, so a ./-prefixed shape the user never
            // typed re-prepends on each tab (the ././././ bug)
            Expect.isTrue
                (paths |> List.forall (fun c -> c.StartsWith "Weir.Tests."))
                $"candidates extend the typed word: {paths |> List.truncate 3}"

            // unbound scrutinee in expression position: nothing beats
            // the union of everything (the D5 rule)
            Expect.equal (sug "let x = publish.") [] "unbound head after ="
            Expect.equal (sug "publish.") [] "unbound head, statement position"

            // bound-but-unresolved keeps the declared-fields fallback:
            // the rule is about binding, not resolution
            Expect.isTrue (sug "fun q -> q." |> List.contains "q.bytes") "a lambda param keeps the high-signal union"

            // the positive twin from the same instrument: a bound,
            // resolved record still completes its own fields (the
            // where-lambda pins above assert the exact list)
            Expect.isTrue (sug "ls |> where (fun f -> f." |> List.contains "f.kind") "resolved fields still fire"
        }
        test "keyword completion inventory: every grammar keyword offered or excluded, decided [D:keyword-completion]" {
            // the pinned split — a new grammar keyword fails here until
            // its completion decision is recorded (added below as
            // offered, or to unsuggestedKeywords with a reason beside
            // the definition). The inventory-match pattern, applied to
            // completion.
            let expectedOffered =
                Set [
                    "within" // [D:within-scopes] — offered, as for/do is
                    "let"
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
                    "elif"
                    // statement starters like let/type [D:modules-v1]
                    "module"
                    "import"
                    // the effect loop's pair [D:for-do] — offered like
                    // if/then (for starts, do continues)
                    "for"
                    "do"
                    // the parallel twin starts statements as for does
                    // [D:pfor]
                    "pfor"
                    // the bounded-loop trio [D:retry-poll] — retry/poll
                    // start statements, until continues (the for/do shape)
                    "retry"
                    "poll"
                    "until"
                    // the stop-ended loop starts statements like retry
                    // [D:repeat]
                    "repeat"
                    // the bare scope's teardown segment continues,
                    // as until does [D:within-always]
                    "always"
                    // the purity assertion starts statements like
                    // within/retry [D:pure-stage1]
                    "pure"
                    // the read-only assertion starts statements too
                    // [D:pure-stage2]
                    "readonly"
                    // the plan/apply capture starts statements too
                    // [D:plan-apply]
                    "plan"
                    // the failure-as-value form, after `|>` like from/to
                    // [D:try-form]
                    "try"
                    // and its inverse, after `|>` like try [D:orfail-form]
                    "orFail"
                ]

            Expect.equal
                (Weir.Parser.keywords - Weir.Complete.unsuggestedKeywords)
                expectedOffered
                "a grammar keyword arrived without a completion decision"

            Expect.equal
                Weir.Complete.unsuggestedKeywords
                (Set [ "rec"; "mutable"; "function"; "while"; "return" ])
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

            Expect.equal
                (suggest text (text.Length - 2))
                [
                    "f.bytes"
                    "f.hidden"
                    "f.kind"
                    "f.modified"
                    "f.name"
                    "f.path"
                    "f.target"
                ]
                ""
        }
        test "lambda param completes inside a record literal (the user receipt)" {
            let text = "ls |> Seq.map (fun x -> { Line = x."

            Expect.equal
                (suggest text (text.Length - 2))
                [
                    "x.bytes"
                    "x.hidden"
                    "x.kind"
                    "x.modified"
                    "x.name"
                    "x.path"
                    "x.target"
                ]
                ""
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

            Expect.equal
                (suggest upto ws)
                [
                    "x.bytes"
                    "x.hidden"
                    "x.kind"
                    "x.modified"
                    "x.name"
                    "x.path"
                    "x.target"
                ]
                "truncated: fields"

            Expect.equal (suggest full ws) [] "untruncated would kill every match"
        }
        test "field prefix narrows the suggestions" {
            let text = "ls |> where (fun f -> f.b"
            Expect.equal (suggest text (text.Length - 3)) [ "f.bytes" ] ""
        }
        test "bound record variable completes its fields" {
            let envWithQ = {
                env with
                    Values = Map.add "q" (generalize (TNamed("Point", []))) env.Values
            }

            Expect.equal (Weir.Complete.suggest envWithQ "q." 0) [ "q.X"; "q.Y" ] ""
        }
        test "module completion offers bespoke arms: Args.load / Env.load (user receipt)" {
            // load is a checker arm, not a member-map entry — completion
            // must surface it beside the ordinary members, from the one
            // source the checker's error path also reads
            Expect.equal (suggest "Args." 0) [ "Args.flag"; "Args.load"; "Args.value" ] "Args.load beside flag/value"

            Expect.equal
                (suggest "Env." 0)
                [
                    "Env.fromFile"
                    "Env.get"
                    "Env.getOr"
                    "Env.load"
                    "Env.need"
                    "Env.ofPairs"
                    "Env.pair"
                    "Env.vars"
                ]
                "Env.load in the sorted members"

            Expect.equal (suggest "Args.lo" 0) [ "Args.load" ] "prefix narrows to the arm"
        }
        test "later pipeline stages track the element type" {
            let text = "[\"{}\"] |> from jsonl JRow |> where (fun c -> c."

            Expect.equal (suggest text (text.Length - 2)) [ "c.bytes"; "c.name"; "c.readOnly" ] ""
        }
        test "holes: unbound args in a known pipeline still type the element" {
            // n is an enclosing param (unbound here) - Seq.skip's result
            // type falls out of unification anyway (the targetEnv objection)
            let text = "Seq.skip n ls |> map (fun f -> f."
            Expect.contains (suggest text (text.Length - 2)) "f.name" ""
        }
        test "no fields on a non-record element" {
            let text = "(Seq.initInfinite id) |> map (fun x -> x."
            Expect.equal (suggest text (text.Length - 2)) [] ""
        }
        test "from json completes the record names the slot admits [D:type-name-complete]" {
            let text = "[\"x\"] |> from json "
            Expect.contains (suggest text text.Length) "EnvVar" "two strings cross"
            Expect.isFalse (suggest text text.Length |> List.contains "FileRow") "an untagged union field does not"
        }
        test "record fields complete through a pipe: scalar record, bound record, lambda param [D:hole-completion]" {
            // [D:repl-quality] pipelineElemTy: `from yaml T` yields a
            // scalar record (one document, not a seq), and a bound record
            // piped into `_.` carries itself — both must type to the
            // record. The seq-only unwrap returned None and no fields
            // surfaced. The one arm — the type flowing into the position —
            // fixes the scalar, the bound-record pipe, and the lambda-param
            // pipe (its prefix now types to the record before the fallback).
            let renv =
                env
                |> declare "type Test2 = { some: int }"
                |> fun e -> {
                    e with
                        Values = Map.add "r" (generalize (TNamed("Test2", []))) e.Values
                }

            // the word starts where the editor's word-scan lands (a
            // dotted `_.`/`row.` is one word to `wordStartAt`)
            let sg (t: string) =
                Weir.Complete.suggest renv t (Weir.Complete.wordStartAt t t.Length)

            // fix: a scalar record from `from yaml T` piped into `_.`
            Expect.equal (sg "src |> from yaml Test2 |> _.") [ "_.some" ] "scalar-record pipe into _."

            // fix: a bound record piped into `_.`
            Expect.equal (sg "r |> _.") [ "_.some" ] "bound record into _."

            // fix: the lambda param over a scalar-record pipe (was the
            // noisy all-declared-fields fallback; now resolves precisely)
            Expect.equal
                (sg "src |> from yaml Test2 |> (fun row -> row.")
                [ "row.some" ]
                "lambda param over the scalar-record pipe"

            // unchanged (the seq unwrap): `Seq.map _.` and its lambda twin
            // over a seq of records still complete
            let renvSeq = {
                renv with
                    Values = Map.add "xs" (generalize (TSeq(TNamed("Test2", [])))) renv.Values
            }

            let sgs (t: string) =
                Weir.Complete.suggest renvSeq t (Weir.Complete.wordStartAt t t.Length)

            Expect.equal (sgs "xs |> Seq.map _.") [ "_.some" ] "seq element via Seq.map _."
            Expect.equal (sgs "xs |> Seq.map (fun elt -> elt.") [ "elt.some" ] "seq element via the lambda param"

            // control: a non-record element still offers nothing
            Expect.equal (sgs "src |> Seq.map (fun e -> e.") [] "a string element offers no fields"
        }
        test "the empty prompt completes to #help, not the flood [D:empty-prompt-directives]" {
            // guards `suggest "" 0` against the `StartsWith ""` flood
            // (1130 items: 954 PATH execs + the universe). A fresh Tab
            // fills in `#help`, the list of everything else [D:help-modules]
            Expect.equal (suggest "" 0) [ "#help" ] "one candidate, so one Tab completes it"

            // filtered completion is unaffected (a real prefix at a head).
            // Assert only environment-stable facts: the `File` module is
            // offered and every candidate matches the prefix — never a
            // specific PATH binary (those vary per machine: a dev box has
            // LLVM's FileCheck-21, a CI runner does not)
            let fi = suggest "Fi" 0
            Expect.contains fi "File" "the File module is offered for prefix Fi"
            Expect.isTrue (fi |> List.forall (fun c -> c.StartsWith "Fi")) "every candidate matches the prefix"
            Expect.isFalse (List.contains "#help" fi) "a real prefix does not fall into the empty-prompt directive set"
        }
        test "a union-case constructor is not a statement head [D:constructors-not-heads]" {
            // `WriteFile …` as a head is a discarded value (a check error) —
            // constructors are subtracted from the head pool, kept in
            // expression/argument positions.
            let heads = suggest "Wr" 0
            Expect.isFalse (List.contains "WriteFile" heads) "WriteFile (an Op ctor) is not a head"

            // a function bare-name still completes at a head
            Expect.contains (suggest "wh" 0) "where" "a function bare-name still heads"

            // WriteFile / Some still complete in an argument/expression slot
            let atHead (t: string) =
                suggest t (Weir.Complete.wordStartAt t t.Length)

            Expect.contains (atHead "let x = 1 in [Wr") "WriteFile" "the ctor completes in an expression position"
            Expect.contains (atHead "let y = [Som") "Some" "Some completes in an expression position"
        }
    ]

let replEchoTests =
    testList "REPL echo bounds" [
        test "the laziness property: echo forces at most limit+1 pulls" {
            let pulls = ref 0

            let counted =
                Seq.initInfinite (fun i ->
                    System.Threading.Interlocked.Increment pulls |> ignore
                    VInt(int64 i))

            let rendered, hint = Weir.Eval.echoValue Weir.Eval.echoPipedCap (VSeq counted)
            // one cap everywhere [D:echo-cap-100]: the REPL's default
            Expect.equal Weir.Eval.echoPipedCap (Some 100) "the -e cap is the REPL's 100"
            Expect.isTrue (pulls.Value <= 101) $"forced {pulls.Value} pulls (bound is 101)"
            Expect.stringContains rendered "; …]" "truncation spelled"
            Expect.equal hint (Some(Weir.Eval.unforcedHint 100)) "the honest lever, not a rendering-changing pipe"
        }
        test "the WHOLE echo enumerates its source once [D:echo-once]" {
            // pins echoTable's probe + echoValue's rendering to a single
            // pull of the lazy source — a double pull runs a bare
            // command's child twice per echo (and its second cooked-tty
            // window eats the next Enter)
            let pulls = ref 0

            let counted =
                Seq.init 3 (fun i ->
                    System.Threading.Interlocked.Increment pulls |> ignore
                    VInt(int64 i))

            let prepped = Weir.Eval.echoPrep (VSeq counted)
            // the tty echo path: table probe first, line rendering second
            Weir.Eval.echoTable Weir.Eval.echoPipedCap None prepped |> ignore
            Weir.Eval.echoValue Weir.Eval.echoPipedCap prepped |> ignore
            Expect.equal pulls.Value 3 "one enumeration of the source across probe + render"

            // and the hint contract survives the cache (still unforced)
            let many = Weir.Eval.echoPrep (VSeq(Seq.init 102 (fun i -> VInt(int64 i))))
            Weir.Eval.echoTable Weir.Eval.echoPipedCap None many |> ignore
            let _, hint = Weir.Eval.echoValue Weir.Eval.echoPipedCap many
            Expect.equal hint (Some(Weir.Eval.unforcedHint 100)) "a cached seq is still an unforced seq to the echo"
        }
        test "the LINES form [D:echo-lines]: raw per line, count-clipped, footer honest" {
            // forced: every line, no hint; content raw (a tab stays a
            // tab — print parity; tty-only so the piped surface holds)
            let lines, hint =
                Weir.Eval.echoLines
                    Weir.Eval.echoPipedCap
                    (VSeq([ VStr "a"; VStr ""; VStr "x\ty" ] :> seq<Weir.Eval.Value>))
                |> Option.get

            Expect.equal lines [ "a"; ""; "x\ty" ] "raw, empties kept"
            Expect.equal hint None "forced carries no hint"

            // unforced: the count clip and the honest sentence
            let lines2, hint2 =
                Weir.Eval.echoLines Weir.Eval.echoPipedCap (VSeq(Seq.init 102 (fun i -> VStr(string i))))
                |> Option.get

            Expect.equal (List.length lines2) 100 "count-clipped at the cap"
            Expect.equal hint2 (Some(Weir.Eval.unforcedHint 100)) "the honest lever"

            // empty: no body, the footer alone reads fine
            Expect.equal
                (Weir.Eval.echoLines Weir.Eval.echoPipedCap (VSeq(Seq.empty)))
                (Some([], None))
                "empty seq: footer only"

            // one enumeration across the whole echo (the echo-once law):
            // prep once, lines + render pull the cache, the source once
            let pulls = ref 0

            let counted =
                Seq.init 3 (fun i ->
                    System.Threading.Interlocked.Increment pulls |> ignore
                    VStr(string i))

            let prepped = Weir.Eval.echoPrep (VSeq counted)
            Weir.Eval.echoLines Weir.Eval.echoPipedCap prepped |> ignore
            Weir.Eval.echoValue Weir.Eval.echoPipedCap prepped |> ignore
            Expect.equal pulls.Value 3 "the lines form adds no enumeration"

            // show is untouched — the literal with quotes
            Expect.equal (run "[\"a\"; \"b\"] |> show") (VStr "[\"a\"; \"b\"]") "show renders; the echo presents"
        }
        test "a FORCED seq echoes in full, no hint [D:echo-rule]" {
            let v = VSeq([ for i in 1..12 -> VInt(int64 i) ] :> seq<Weir.Eval.Value>)
            let rendered, hint = Weir.Eval.echoValue Weir.Eval.echoPipedCap v
            Expect.stringContains rendered "11; 12]" "all twelve"
            Expect.equal hint None "forcing IS the lever; nothing left to hint"
        }
        test "short seqs echo whole, no hint" {
            let v = VSeq([ VInt 1L; VInt 2L ] :> seq<Weir.Eval.Value>)
            let rendered, hint = Weir.Eval.echoValue Weir.Eval.echoPipedCap v
            Expect.equal rendered "[1; 2]" ""
            Expect.equal hint None ""
        }
        test "echo clips long strings at 120 with an ellipsis" {
            let rendered, _ =
                Weir.Eval.echoValue Weir.Eval.echoPipedCap (VStr(String.replicate 200 "x"))

            Expect.stringContains rendered "…\"" "clip marker inside the quotes"
            Expect.isTrue (rendered.Length < 140) $"clipped ({rendered.Length} chars)"
        }
        test "a forced outer renders every element; INNER seqs keep the clip" {
            // [nats]-is-forcible: full-at-top must not force the depths
            let inner = VSeq([ for i in 1..15 -> VInt(int64 i) ] :> seq<Weir.Eval.Value>)
            let outer = VSeq([ for _ in 1..15 -> inner ] :> seq<Weir.Eval.Value>)
            let rendered, hint = Weir.Eval.echoValue Weir.Eval.echoPipedCap outer
            Expect.equal hint None "the outer is forced"
            Expect.equal (rendered.Split("; …").Length - 1) 15 "all 15 outers, each inner clipped"
        }
        test "the footer reports the cap IN EFFECT — pinned at two caps [D:echo-cap]" {
            let src () =
                VSeq(Seq.init 40 (fun i -> VInt(int64 i)))

            let _, h10 = Weir.Eval.echoValue (Some 10) (src ())
            Expect.equal h10 (Some(Weir.Eval.unforcedHint 10)) "cap 10 says 10"

            let r25, h25 = Weir.Eval.echoValue (Some 25) (src ())
            Expect.equal h25 (Some(Weir.Eval.unforcedHint 25)) "cap 25 says 25 — never a hardcoded number"
            Expect.stringContains r25 "24; …]" "25 elements shown under cap 25"

            Expect.stringContains (Weir.Eval.unforcedHint 25) "first 25 of an unforced seq" "the sentence itself"

            // the lines and table forms carry the same live-cap sentence
            let lines, hl =
                Weir.Eval.echoLines (Some 3) (VSeq(Seq.init 9 (fun i -> VStr(string i))))
                |> Option.get

            Expect.equal (List.length lines) 3 "lines clip at the cap"
            Expect.equal hl (Some(Weir.Eval.unforcedHint 3)) ""
        }
        test "the cap NEVER clips a forced seq [D:echo-cap]" {
            let forced = VSeq([ for i in 1..12 -> VInt(int64 i) ] :> seq<Weir.Eval.Value>)
            let rendered, hint = Weir.Eval.echoValue (Some 5) forced
            Expect.stringContains rendered "11; 12]" "all twelve under a cap of 5"
            Expect.equal hint None "forced-ness outranks the cap"
        }
        test "no cap (#echo all): a finite unforced seq echoes whole, no hint [D:echo-cap]" {
            let rendered, hint =
                Weir.Eval.echoValue None (VSeq(Seq.init 30 (fun i -> VInt(int64 i))))

            Expect.stringContains rendered "28; 29]" "all thirty"
            Expect.equal hint None "nothing clipped, nothing to hint"

            let lines, hl =
                Weir.Eval.echoLines None (VSeq(Seq.init 30 (fun i -> VStr(string i))))
                |> Option.get

            Expect.equal (List.length lines) 30 ""
            Expect.equal hl None ""
        }
        test "the cap changes no enumeration count [D:echo-once] [D:echo-cap]" {
            let pulls = ref 0

            let counted =
                Seq.initInfinite (fun i ->
                    System.Threading.Interlocked.Increment pulls |> ignore
                    VInt(int64 i))

            let rendered, _ = Weir.Eval.echoValue (Some 25) (VSeq counted)
            Expect.isTrue (pulls.Value <= 26) $"forced {pulls.Value} pulls (bound is cap+1 = 26)"
            Expect.stringContains rendered "24; …]" ""
        }
        test "binary refuses the echo: a NUL in the pulled prefix, bounded, cache-walking [D:binary-echo]" {
            Expect.isTrue
                (Weir.Eval.echoBinary (Some 10) (VSeq([ VStr "a\u0000b" ] :> seq<Weir.Eval.Value>)))
                "a NUL line marks the value binary"

            Expect.isFalse
                (Weir.Eval.echoBinary (Some 10) (VSeq([ VStr "plain"; VStr "text" ] :> seq<Weir.Eval.Value>)))
                "text stays text"

            Expect.isTrue (Weir.Eval.echoBinary None (VStr "x\u0000")) "a scalar carries the mark too"

            // the probe is bounded and walks the cache — cap+1 pulls, max
            let pulls = ref 0

            let counted =
                Seq.initInfinite (fun i ->
                    System.Threading.Interlocked.Increment pulls |> ignore
                    VStr(string i))

            Weir.Eval.echoBinary (Some 10) (VSeq counted) |> ignore
            Expect.isTrue (pulls.Value <= 11) $"probed {pulls.Value} pulls (bound is 11)"
        }
        test "a seq of tuples tabulates under its positions [D:tuple-table]" {
            let rows =
                VSeq [
                    VTuple [ VInt 1; VStr "web"; VUnion("None", None) ]
                    VTuple [ VInt 20; VStr "db-x"; VUnion("None", None) ]
                ]

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None rows with
            | Some(lines, None) ->
                Expect.equal
                    lines
                    [ " 0  1     2"; "──  ────  ─"; " 1  web"; "20  db-x" ]
                    "positions head the columns; none hides"
            | other -> failtest $"expected a table, got {other}"

            // a component that is no cell: the literal echo stands
            Expect.isNone
                (Weir.Eval.echoTable Weir.Eval.echoPipedCap None (VSeq [ VTuple [ VInt 1; VSeq [ VInt 2 ] ] ]))
                "a seq component"
        }
        test "the width clamp: the widest column absorbs the clip, floors hold, None means untouched [D:table-polish]" {
            let rows =
                VSeq [
                    VRecord("P", [ "name", VStr "a"; "path", VStr(String.replicate 60 "x") ])
                    VRecord("P", [ "name", VStr "b"; "path", VStr "/short" ])
                ]

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap (Some 40) rows with
            | Some(lines, None) ->
                for l in lines do
                    Expect.isLessThanOrEqual l.Length 40 $"every line fits the terminal: {l}"

                Expect.stringContains lines[2] "…" "the fat column's cell ends in the ellipsis"
                Expect.stringContains lines[2] "a" "the short column is intact"
                Expect.isTrue (lines[0].StartsWith "name") "the header survives clamping"
            | other -> failtest $"expected a table, got {other}"

            // a terminal too narrow for the floors: rendered unclamped
            // (the terminal wraps — stated), never a mangled floor
            match Weir.Eval.echoTable Weir.Eval.echoPipedCap (Some 8) rows with
            | Some(lines, None) -> Expect.isGreaterThan lines[0].Length 8 "gives up rather than mangles"
            | other -> failtest $"expected a table, got {other}"

            // None = the exact pre-clamp shape (the piped/None law)
            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None rows with
            | Some(lines, None) ->
                Expect.isFalse (lines |> List.exists (fun l -> l.Contains "…")) "no clip without a width"
            | other -> failtest $"expected a table, got {other}"
        }
        test "table columns measure terminal cells: wide and combining text align, a TAB is spelled [D:str-width]" {
            let rows =
                VSeq [
                    VRecord("R", [ "name", VStr "日本語日本語"; "n", VInt 1L ])
                    VRecord("R", [ "name", VStr "e\u0301x"; "n", VInt 22L ])
                    VRecord("R", [ "name", VStr "a\tc"; "n", VInt 3L ])
                ]

            match Weir.Eval.echoTable Weir.Eval.echoPipedCap None rows with
            | Some(lines, None) ->
                Expect.equal
                    lines
                    [
                        "name           n"
                        "────────────  ──"
                        "日本語日本語   1"
                        "e\u0301x            22"
                        "a\\tc           3"
                    ]
                    "every row is 16 cells wide"

                for l in lines do
                    Expect.equal (Weir.Types.CellWidth.ofString l) 16 $"cells: {l}"
            | other -> failtest $"expected a table, got {other}"
        }
        test "show is byte-identical to its shipped contract (NOT the echo)" {
            let long = VSeq(seq { for i in 1..100 -> VInt(int64 i) })

            Expect.stringContains (Weir.Eval.formatValue long) "; 20; ...]" "show keeps 20 + dots"

            let s200 = String.replicate 200 "x"
            Expect.equal (Weir.Eval.formatValue (VStr s200)) ("\"" + s200 + "\"") "show never clips strings"
        }
        test
            "a let-bound truncated seq<string> echo carries the SAME unforced teaching as the bare echo [D:echo-teaching-consistency]" {
            // an unforced, over-cap seq<string> — the command-backed
            // `let` bind's echo shape, synthesized deterministically so
            // no PATH executable is asserted
            let unforced = VSeq(Seq.init 12 (fun i -> VStr(string i)))

            // the bare-expression echo's teaching (echoLines' hint)
            let _, bareHint = Weir.Eval.echoLines (Some 10) unforced |> Option.get
            Expect.equal bareHint (Some(Weir.Eval.unforcedHint 10)) "the bare echo teaches the unforced clip"

            // the let-echo meta line carries that same tail — the fix's
            // invariant: a clipped `let` bind never looks like it
            // silently dropped data
            let meta = Weir.Repl.letEchoMeta true "xs" (TSeq TStr) None bareHint

            Expect.equal
                meta
                $"xs : seq<string>{Weir.Eval.echoTail bareHint}"
                "the let meta ends in the unforced teaching, same tail the bare echo shows"

            Expect.stringContains meta "first 10 of an unforced seq" "the teaching is visible in the let meta"
        }
        test "a forced-seq let echo shows NO teaching and all elements (unchanged) [D:echo-rule]" {
            let forced = VSeq([ for i in 1..12 -> VStr(string i) ] :> seq<Weir.Eval.Value>)

            // forced: echoLines returns every line and no hint
            let lines, hint = Weir.Eval.echoLines (Some 10) forced |> Option.get
            Expect.equal (List.length lines) 12 "all twelve elements, the cap never clips a forced seq"
            Expect.equal hint None "forced carries no teaching"

            // the let meta with no state then has an empty tail — no
            // dangling teaching (the state annotation is the caller's,
            // via letSeqState — the frozen pin below)
            let meta = Weir.Repl.letEchoMeta true "xs" (TSeq TStr) None hint
            Expect.equal meta "xs : seq<string>" "no teaching, no trailing parenthetical"
        }
        test "the let echo states the bound seq's state — the exact spellings [D:reenum-warning]" {
            // the meta seam: state alone, hint alone, both (one
            // parenthetical, state first), neither
            Expect.equal
                (Weir.Repl.commandBackedEcho true "pods" (TSeq TStr))
                "pods : seq<string> (command-backed — runs on each use; type pods to run it)"
                "the command-backed echo, exact bytes [D:repl-let-echo-lazy]"

            Expect.equal
                (Weir.Repl.letEchoMeta true "pods" (TSeq TStr) (Some "frozen") None)
                "pods : seq<string> (frozen)"
                "the resolution annotation, exact bytes"

            Expect.equal
                (Weir.Repl.letEchoMeta true "pods" (TSeq TStr) (Some "frozen") (Some "extra"))
                "pods : seq<string> (frozen; extra)"
                "state and teaching share one parenthetical, state first"

            Expect.equal
                (Weir.Repl.letEchoMeta true "pods" (TSeq TStr) None None)
                "pods : seq<string>"
                "silence is the default"
        }
        test "types off drops the type from every meta, keeps the name and the teaching tail [D:repl-types]" {
            let hint = Some(Weir.Eval.unforcedHint 10)

            Expect.equal
                (Weir.Repl.commandBackedEcho false "pods" (TSeq TStr))
                "pods (command-backed — runs on each use; type pods to run it)"
                "command-backed keeps the name and its state"

            Expect.equal
                (Weir.Repl.letEchoMeta false "pods" (TSeq TStr) (Some "frozen") None)
                "pods (frozen)"
                "the state stays"

            Expect.equal (Weir.Repl.letEchoMeta false "pods" (TSeq TStr) None None) "pods" "the name alone"

            Expect.equal
                (Weir.Repl.exprEchoMeta false (TSeq TStr) hint)
                (Some $"({Weir.Eval.unforcedHint 10})")
                "a type-only meta keeps just its tail"

            Expect.equal (Weir.Repl.exprEchoMeta false (TSeq TStr) None) None "nothing left, no line"

            Expect.equal
                (Weir.Repl.exprEchoMeta true (TSeq TStr) hint)
                (Some $": seq<string>{Weir.Eval.echoTail hint}")
                "types on: today's bytes"
        }
        test
            "letSeqState decides frozen; letCommandBacked flags an unforced command seq, never a forced or pure one [D:repl-let-echo-lazy]" {
            let sp: Weir.Ast.Span = {
                Start = { Line = 1; Col = 1 }
                End = { Line = 1; Col = 2 }
            }

            let cmdTe: Weir.Check.TypedExpr = {
                Kind = Weir.Check.TECmd(Weir.Check.THeadLit "kubectl", [], None)
                Ty = TSeq TStr
                Span = sp
            }

            let pureTe: Weir.Check.TypedExpr = {
                Kind = Weir.Check.TEVar "squares"
                Ty = TSeq TInt
                Span = sp
            }

            let unforced = Weir.Eval.VSeq(Seq.delay (fun () -> Seq.empty))
            let forced = Weir.Eval.VSeq([ Weir.Eval.VStr "a" ] :> seq<Weir.Eval.Value>)

            Expect.isTrue
                (Weir.Repl.letCommandBacked cmdTe unforced)
                "command-backed unforced: the echo must not enumerate"

            Expect.isFalse (Weir.Repl.letCommandBacked cmdTe forced) "forced already ran: the echo shows it"
            Expect.isFalse (Weir.Repl.letCommandBacked pureTe unforced) "a pure lazy seq echoes as before"

            Expect.equal (Weir.Repl.letSeqState forced) (Some "frozen") "forced states the resolution, command or not"

            Expect.equal (Weir.Repl.letSeqState unforced) None "an unforced seq carries no state"

            Expect.equal (Weir.Repl.letSeqState (Weir.Eval.VInt 3L)) None "a non-seq value carries no state"
        }
        test "the streamed-it misuse repair carries the command verbatim [D:repl-it]" {
            Expect.equal
                (Weir.Repl.streamedItRepair (Some "kubectl get po -A -o yaml"))
                "to capture: let x = kubectl get po -A -o yaml"
                "one line: the copyable let with the recorded command"

            // a sentinel-joined (assembled) or empty source cannot ride a
            // one-line suggestion — the generic spelling instead
            for src in [ Some "cmd\u0001more"; Some "   "; None ] do
                Expect.stringContains
                    (Weir.Repl.streamedItRepair src)
                    "let x = <the command>"
                    "unclean source falls back to the generic spelling"
        }
        test "the it-rebinding matrix, FSI parity [D:repl-it]" {
            // fresh session: unbound
            Expect.equal (Weir.Repl.itSchemeForTest []) None "fresh session — it unbound"

            // a non-unit expression rebinds
            Expect.equal (Weir.Repl.itSchemeForTest [ "5" ]) (Some "int") "a non-unit expression binds it"

            // a unit expression rebinds — unit included, FSI's rule
            Expect.equal (Weir.Repl.itSchemeForTest [ "print \"x\"" ]) (Some "unit") "a unit expression binds it := ()"

            // a `let` does not rebind — `let o = 10` binds no it
            Expect.equal (Weir.Repl.itSchemeForTest [ "let o = 10" ]) None "a let binds its name, never it"

            // and a later `let` leaves an earlier binding standing
            Expect.equal
                (Weir.Repl.itSchemeForTest [ "5"; "let o = 10" ])
                (Some "int")
                "a let leaves the previous it untouched"
        }
        test "the echo normalizes type-var display: 'a1 -> 'a2 renders 'a -> 'b [D:repl-fn-echo]" {
            Expect.equal (formatEchoTy (TFun(TVar "a1", TVar "a2"))) "'a -> 'b" "fresh names rename in order"

            Expect.equal
                (formatEchoTy (TFun(TVar "a2", TFun(TVar "a2", TVar "b7"))))
                "'a -> 'a -> 'b"
                "a repeated var keeps one name"

            Expect.equal
                (formatEchoTy (TFun(TSeq(TVar "a1"), TSeq TStr)))
                "seq<'a> -> seq<string>"
                "concrete types are untouched"
        }
        test "no checker var reaches a diagnostic: fresh names rename in order [D:user-language-messages]" {
            Expect.equal
                (normalizeDiagVars "expected 'a12, got Result<int, 'a3> and 'a12 again")
                "expected 'a, got Result<int, 'b> and 'a again"
                "first-appearance order, one name per var"

            Expect.equal
                (normalizeDiagVars "'f' takes 'a -> 'r4 here; 'a1' is a binding")
                "'f' takes 'a -> 'b here; 'a1' is a binding"
                "a letter the message already uses is skipped; a quoted name stays"

            Expect.equal (normalizeDiagVars "type '__hole2 * int") "type 'a * int" "a hole var too"

            let allLetters =
                [ 'a' .. 'z' ] |> List.map (fun c -> $"'{c}") |> String.concat " -> "

            Expect.equal
                (normalizeDiagVars $"{allLetters} -> 'a12")
                $"{allLetters} -> 'a12"
                "every letter taken: the fresh name stays, never a divide by zero"

            Expect.equal
                (checkErr "Ok 1 < Error \"x\"").Message
                "operator '<' is not defined for Result<int, 'a>"
                "an operator fall-through"

            Expect.equal
                (checkErr "print None").Message
                "print takes a string, int, float, bool, Uuid, or seq<string>; this is Option<'a>"
                "print"

            Expect.equal (checkErr "show (fun x -> x)").Message "show cannot render functions; this is 'a -> 'a" "show"

            Expect.equal
                (checkErr "let f x = x < Ok 1 in 0").Message
                "operator '<' is not defined for Result<int, 'a>"
                "an open left side is no mismatch"

            Expect.equal
                (errorMsgsIn "disc.weir" [ "let f x = x"; "f" ])
                [
                    "this statement computes a 'a -> 'a and discards it — bind it, or pipe it to print"
                ]
                "the statement layer's discard"

            Expect.equal
                (errorMsgsIn "hole.weir" [ "let e = Nope.thing 1"; "[(e, 1)] |> to json |> Seq.iter print" ]
                 |> List.filter (fun m -> m.Contains "to json" || m.Contains "admitted"))
                []
                "a hole inside a tuple stays silent at the wire"
        }
        test "a lambda right of |> knows its parameter: to json/yaml see the piped type" {
            cleanIn
                "pipelam.weir"
                [
                    "type PJ = { a: int }"
                    "let cfg = { a = 1 }"
                    "cfg |> fun c -> c |> to yaml |> Seq.iter print"
                    "let r = Some 1 |> function | Some v -> v |> to json | None -> []"
                    "print (show r)"
                ]
                "the piped type reaches the boundary"

            Expect.equal
                (run "Some 1 |> function | Some v -> v |> to json |> Str.join \"\" | None -> \"\"")
                (VStr "1")
                "function too"
        }
        test "a builtin function value echoes the #help composition — one source, string-equal [D:repl-fn-echo]" {
            let te = Weir.Builtins.typeEnv

            let lines =
                Weir.Repl.functionEchoLines false 100 te (fun _ -> None) "Seq.map" |> Option.get

            let d = Map.find "Seq.map" Weir.Builtins.builtinDocs.Value
            let sch = te.Modules["Seq"]["map"]

            Expect.equal
                lines
                [ formatSignature "Seq.map" d.Params sch.Ty; (d.Summary.Split '\n')[0] ]
                "signature from formatSignature, glance from the doc's first line — the #help pieces exactly"
        }
        test "a bare-alias function value echoes its qualified home [D:repl-fn-echo]" {
            let lines =
                Weir.Repl.functionEchoLines false 100 Weir.Builtins.typeEnv (fun _ -> None) "find"
                |> Option.get

            Expect.isTrue (lines[0].StartsWith "Seq.find") "the bare alias names its home"
        }
        test
            "a session-defined function echoes its name, normalized scheme, and recorded definition line [D:repl-fn-echo]" {
            let te = {
                Weir.Builtins.typeEnv with
                    Values = Map.add "f" (Weir.Types.generalize (TFun(TInt, TInt))) Weir.Builtins.typeEnv.Values
            }

            let defs n =
                if n = "f" then Some("let f x = x + 1", false) else None

            Expect.equal
                (Weir.Repl.functionEchoLines false 100 te defs "f")
                (Some [ "f : int -> int"; "let f x = x + 1" ])
                "name : scheme, then the definition's first physical line"

            // a multi-line definition clips to its first line + …
            let te2 = {
                Weir.Builtins.typeEnv with
                    Values =
                        Map.add "g" (Weir.Types.generalize (TFun(TVar "a1", TVar "a2"))) Weir.Builtins.typeEnv.Values
            }

            let defs2 n =
                if n = "g" then Some("let g x =", true) else None

            Expect.equal
                (Weir.Repl.functionEchoLines false 100 te2 defs2 "g")
                (Some [ "g : 'a -> 'b"; "let g x = …" ])
                "multi-line shows the first line with an ellipsis; vars normalize"
        }
        test "an unnamed function value declines the mini-help — the plain echo stands [D:repl-fn-echo]" {
            Expect.equal
                (Weir.Repl.functionEchoLines false 100 Weir.Builtins.typeEnv (fun _ -> None) "nosuchname")
                None
                "nothing to name — the caller falls back to <fun>/<builtin> : ty"
        }
        test "a named function short of arguments echoes what is bound and what is missing [D:partial-echo]" {
            let builtins = Weir.Statements.builtinParamNames Weir.Builtins.typeEnv

            let lines src =
                Weir.Statements.partialApp builtins (checkOk src)
                |> Option.map (Weir.Statements.partialEchoLines plainSigStyle)

            Expect.equal
                (lines "\"k\" |> Str.toUtf8 |> Bytes.hmacSha256")
                (Some [
                    "Bytes.hmacSha256 key (msg: Bytes) : Bytes"
                    "partial application — the piped value is key; still needs msg"
                ])
                "the piped value names the parameter it landed in"

            Expect.equal
                (lines "Seq.map (fun x -> x + 1)")
                (Some [
                    "Seq.map f (xs: seq<int>) : seq<int>"
                    "partial application — f is bound; still needs xs"
                ])
                "applied, with the head's instantiated types"

            Expect.equal
                (lines "\"a\" |> Str.replace \"b\"" |> Option.map List.last)
                (Some "partial application — old is bound; the piped value is new; still needs s")
                "an applied argument, then the piped one"

            Expect.equal (lines "Bytes.hmacSha256") None "a bare name is the mini-help's"
            Expect.equal (lines "Str.toUtf8 >> Bytes.toHex") None "a composition has no named head"
            Expect.equal (lines "(fun x -> x + 1)") None "a lambda has no named head"
            Expect.equal (lines "Str.toUtf8 \"k\"") None "a full application is a value"

            // a head the resolver cannot name declines
            Expect.equal
                (Weir.Statements.partialApp (fun _ -> None) (checkOk "Seq.map (fun x -> x + 1)"))
                None
                "no parameter names, no claim"
        }
        test "the partial signature is the one composition: nothing applied is formatSignature [D:partial-echo]" {
            let d = Map.find "Seq.map" Weir.Builtins.builtinDocs.Value
            let sch = Weir.Builtins.typeEnv.Modules["Seq"]["map"]

            Expect.equal
                (formatPartialSignatureWith plainSigStyle 0 "Seq.map" d.Params sch.Ty)
                (formatSignature "Seq.map" d.Params sch.Ty)
                "applied 0 is the #help signature, byte for byte"
        }
        test "a discarded partial application names what is missing, tagged discard [D:partial-echo]" {
            Expect.equal
                (errorMsgsIn "disc.weir" [ "let k = Str.toUtf8 \"k\""; "k |> Bytes.hmacSha256" ])
                [
                    "'Bytes.hmacSha256' is partially applied — the piped value is key; still needs msg (Bytes)"
                ]
                "a builtin head names its parameters"

            Expect.equal
                (errorMsgsIn "disc.weir" [ "let f a b = a + b + 1"; "f 1" ])
                [ "'f' is partially applied — still needs 1 more argument (int)" ]
                "a script function says how many"

            Expect.equal
                (errorMsgsIn "disc.weir" [ "let cleanup () = print \"c\""; "cleanup" ])
                [
                    "this statement computes a unit -> unit and discards it"
                    + Weir.Statements.callItTail
                ]
                "an uncalled unit function keeps its own lesson"
        }
        test "a failing #infer drafted type surfaces line:col + an offending-line snippet [D:infer-diagnostic]" {
            // a deliberately un-checkable drafted type: a leading-digit
            // field name weir rejects — number the drafted lines and
            // check the physical statement, then render its diagnostic
            let drafted = [ "type PodsJson = {"; "    1a: int"; "}" ]

            let numbered =
                drafted
                |> List.mapi (fun i l -> i + 1, l)
                |> List.filter (fun (_, raw) -> Weir.Assemble.classifyLine raw <> Weir.Assemble.LineKind.CommentOnly)
                |> List.map (fun (n, raw) -> n, Weir.Assemble.stripComment raw)

            let lls =
                match Weir.Assemble.assemble numbered with
                | Ok lls -> lls
                | Error m -> failtest $"drafted did not assemble: {m}"

            let ll = List.exactlyOne lls

            let d =
                match
                    Weir.Statements.checkStatement
                        false
                        None
                        Weir.Statements.resolver
                        Weir.ModuleLoader.scriptOnlyImport
                        Weir.Builtins.typeEnvStrict
                        ll
                with
                | Error d -> d
                | Ok _ -> failtest "expected the drafted type to FAIL the check"

            let rendered = Weir.ReplColor.stripAnsi (Weir.Repl.formatDraftedDiag drafted d)

            // the offending drafted line is the second physical line
            Expect.equal d.PhysLine 2 "the error points at the drafted field line, not the header"
            Expect.stringContains rendered "at line 2, col " "line:col is surfaced"
            Expect.stringContains rendered "1a: int" "the offending drafted line is shown as a snippet"
            Expect.stringContains rendered "^" "a caret marks the column"

            // the caret sits under the reported column (snippet indented by 2)
            let snippetLines = rendered.Split('\n')

            let caretLine =
                snippetLines |> Array.find (fun l -> l.Trim() |> Seq.forall (fun c -> c = '^'))

            let caretCol = caretLine.IndexOf '^'

            Expect.equal
                caretCol
                (2 + d.PhysCol - 1)
                "the caret lands under the error column in the 2-space-indented snippet"
        }
    ]

let replColorTests =
    // [D:repl-color] — the paint-transparency property is the load-bearing
    // pin: coloring never alters the text (strip . colorize = id)
    let colorize = Weir.ReplColor.colorizeRepl (fun n -> n = "ls" || n = "print")
    let strip = Weir.ReplColor.stripAnsi

    testList "REPL coloring" [
        test "command mode tints: head bold-blue, argv dim, splice cyan, stage undimmed [D:semantic-tokens]" {
            let c = colorize "git add file.txt $x | Seq.head"
            Expect.stringContains c "\u001b[1;34mgit\u001b[0m" "the external head"
            Expect.stringContains c "\u001b[2madd\u001b[0m" "argv words render dim"
            Expect.stringContains c "\u001b[36m$x\u001b[0m" "the splice island"
            Expect.isFalse (c.Contains "\u001b[2mhead") "after | the stage is expression land"
        }
        test "a known constructor takes the constructor tint in a head slot too" {
            let tint =
                Weir.ReplColor.colorizeRepl (fun n -> n = "Node" || n = "Leaf" || n = "Seq")

            let c = tint "let t = Node [Leaf 1; Node []]"
            Expect.isFalse (c.Contains "\u001b[1mNode") "the let-RHS head is not bold"
            Expect.stringContains c "\u001b[33mNode\u001b[0m [\u001b[33mLeaf\u001b[0m" "head and nested agree"
            Expect.stringStarts (tint "Node []") "\u001b[33mNode\u001b[0m" "the statement head too"
            Expect.stringStarts (tint "Seq.length xs") "\u001b[1mSeq\u001b[0m" "a module qualifier keeps the known tint"
        }
        test "an interpolation hole paints as code, not string [D:interp-hole-complete]" {
            let c =
                colorize "for r in ls |> Seq.where (fun f -> File.exists $\"{f.path}/AGENTS.md\") do"

            Expect.equal
                (strip c)
                "for r in ls |> Seq.where (fun f -> File.exists $\"{f.path}/AGENTS.md\") do"
                "transparent"

            Expect.isFalse (c.Contains "\u001b[32m\"{f.path}/AGENTS.md\"") "the hole is not inside the string span"
            Expect.stringContains c "/AGENTS.md\"" "the literal part stays"
            Expect.stringContains c "\u001b[32m}/AGENTS.md\"\u001b[0m" "the text after the hole is string"
            // a hole in argv is an expression, not a dim argv word
            let g = colorize "git commit -m $\"wip {n + 1}\""
            Expect.isFalse (g.Contains "\u001b[2mn") "a hole in command argv is not dimmed"
            Expect.stringContains g "\u001b[36m1\u001b[0m" "a number in the hole paints as a number"
            // {{ is a literal brace, a string inside a hole stays a string
            Expect.stringContains (colorize "print $\"{{x}}\"") "\u001b[32m\"{{x}}\"\u001b[0m" "{{ stays string"
            let n = colorize "print $\"{f \"s\"}\""
            Expect.stringContains n "\u001b[32m\"s\"" "a nested string stays string"
            Expect.stringContains n "\u001b[0mf " "the hole's code is not string"
        }
        test "an uppercase stage word is never a program, whatever PATH holds" {
            // a case-insensitive filesystem finds `Seq` as seq — the casing
            // rule decides first
            let c = Weir.ReplColor.colorizeRepl (fun _ -> false) "git log | Seq.head"
            Expect.isFalse (c.Contains "\u001b[1;34mSeq") "not painted as a PATH head"
            Expect.isFalse (c.Contains "\u001b[31mSeq") "not painted as an unknown head"
        }
        test
            "form-words paint as the form: a within kind and a from/to adapter colour keyword, not identifier [D:form-word-hover]" {
            let kw = "\x1b[34m"
            let reset = "\x1b[0m"
            Expect.stringContains (colorize "within cd d") $"{kw}cd{reset}" "the within kind"
            Expect.stringContains (colorize "xs |> from json Config") $"{kw}json{reset}" "the from adapter"
            Expect.stringContains (colorize "xs |> to yaml") $"{kw}yaml{reset}" "the to adapter"
            // a binding that merely starts with an adapter word stays plain
            Expect.isFalse ((colorize "let jsonData = 1").Contains $"{kw}jsonData{reset}") "not a false match"
        }
        test "paint transparency: strip after colorize is the identity" {
            let fixtures = [
                "let x = 1 + 2"
                "within cd d"
                "xs |> from json Config"
                "ls |> where (fun f -> f.bytes > 10B)"
                "let s = @\"unclosed raw to eol"
                "let t = \"\"\"triple \\ and \" inside\"\"\""
                "git log --oneline // trailing comment"
                "$(git status) |> Seq.head"
                "^ls -la $target !marker"
                "let e = \"emoji 😀 inside\" |> Str.length"
                "let xs = [1; 2"
                ""
            ]

            for f in fixtures do
                Expect.equal (strip (colorize f)) f $"altered: {f}"
        }
        test "head verdicts: known bold, unknown red, forced PATH-only" {
            Expect.stringContains (colorize "ls -la") "\x1b[1mls\x1b[0m" "known head bold"
            Expect.stringContains (colorize "zzznope arg") "\x1b[31mzzznope\x1b[0m" "unknown head red"

            // ^-forced resolves against PATH only: 'show' is known but
            // not a binary anywhere (Windows System32 ships a legacy
            // print.exe — the old ^print fixture correctly flipped
            // there [D:windows-s2]), so ^show paints red even though
            // bare show would be bold
            Expect.stringContains (colorize "^show x") "\x1b[31mshow\x1b[0m" "force ignores bindings"
        }
        test "a head is the parser's whole command word, hyphens included [D:repl-color-words]" {
            Expect.stringContains
                (colorize "zzz-nope list")
                "\x1b[31mzzz-nope\x1b[0m"
                "an unknown hyphenated head is red as one word"

            Expect.stringContains (colorize "let v = zzz-nope list") "\x1b[31mzzz-nope\x1b[0m" "the let-RHS head too"

            Expect.equal (strip (colorize "zzz-nope list")) "zzz-nope list" "transparent"
            // a known head keeps its own extent: `ls-1` is not one word
            Expect.stringContains (colorize "ls -la") "\x1b[1mls\x1b[0m" "a known head is unchanged"
        }
        test "an argv word is one dim token: versions and flags hold no numbers or operators [D:repl-color-words]" {
            let known n = n = "print"

            let c =
                Weir.ReplColor.colorizeRepl known "sh tag v0.123.0 --some-flag -la $x | Seq.head"

            Expect.stringContains c "\x1b[2mv0.123.0\x1b[0m" "a version is one word"
            Expect.stringContains c "\x1b[2m--some-flag\x1b[0m" "a long flag is one word"
            Expect.stringContains c "\x1b[2m-la\x1b[0m" "a short flag is one word"
            Expect.stringContains c "\x1b[36m$x\x1b[0m" "a splice keeps its tint"
            Expect.isFalse (c.Contains "\x1b[2mhead") "after | the stage is expression land"
        }
        test "an alias head arms command mode: its words are dim argv [D:alias-argv-tint]" {
            let isCb n = n = "cb"
            let c = Weir.ReplColor.colorizeReplWith isCb isCb "cb BBDD-8900 | Seq.head"

            Expect.stringContains c "\x1b[1mcb\x1b[0m" "the alias keeps the known tint"
            Expect.stringContains c "\x1b[2mBBDD-8900\x1b[0m" "the argument is one argv word"
            Expect.isFalse (c.Contains "\x1b[2mhead") "after | the stage is expression land"

            Expect.isFalse
                ((Weir.ReplColor.colorizeRepl isCb "cb BBDD-8900").Contains "\x1b[2m")
                "a known name that is no alias stays an expression"
        }
        test "cd arms command mode: a path's uppercase part is no constructor [D:alias-argv-tint]" {
            let c = Weir.ReplColor.colorizeRepl (fun n -> n = "cd") "cd /Users/queil/"

            Expect.stringContains c "\x1b[1mcd\x1b[0m" "cd keeps the known tint"
            Expect.stringContains c "\x1b[2m/Users/queil/\x1b[0m" "the path is one argv word"
            Expect.isFalse (c.Contains "\x1b[33m") "no part of it takes the constructor tint"
        }
        test "the Tab listing paints a candidate without moving the columns [D:repl-color-words]" {
            let items = [ "alpha"; "be"; "gamma"; "d" ]
            let plain = Weir.Repl.completionListing 20 items

            let painted =
                Weir.Repl.completionListingWith (fun s -> "\x1b[1m" + s + "\x1b[0m") 20 items

            Expect.equal (painted |> List.map strip) plain "strip after paint is the plain listing"
            Expect.isTrue (painted |> List.forall (fun l -> l.Contains "\x1b[1m")) "every line is painted"
        }
        test "a module qualifier paints the same at the head and after a pipe [D:repl-color-words]" {
            let c =
                Weir.ReplColor.colorizeRepl (fun n -> n = "Str" || n = "Bytes") "Str.toUtf8 \"x\" |> Bytes.toHex"

            Expect.stringContains c "\x1b[1mStr\x1b[0m" "the head qualifier"
            Expect.stringContains c "\x1b[1mBytes\x1b[0m" "the piped-into qualifier, same tint"
            // an unknown uppercase word keeps the casing-law tint
            Expect.stringContains
                (Weir.ReplColor.colorizeRepl (fun _ -> false) "let n = Nope.x 1")
                "\x1b[33mNope\x1b[0m"
                "not a module: casing law"
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
        // sequenced: a wall-clock ceiling measured while the rest of the
        // suite runs in parallel times the machine's load, not colorize
        testSequenced
        <| test "redraw-cost ceiling: 1000 pathological 200-char lines" {
            let line =
                String.replicate 5 "let ZZig = $(git log) |> where (fun f -> f.bytes > 12B) ; "
                + "@\"tail"

            let sw = System.Diagnostics.Stopwatch.StartNew()

            for _ in 1..1000 do
                colorize line |> ignore

            sw.Stop()
            Expect.isTrue (sw.ElapsedMilliseconds < 2000L) $"1000 lines took {sw.ElapsedMilliseconds}ms"
        }
    ]

let headSlotCompletionTests =
    // the prelude's types: the loop heads' options records live there
    let env = Weir.Prelude.extendTypes Weir.Builtins.typeEnvStrict

    let sug (line: string) =
        Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)

    let entry (lines: string list) =
        let cur = List.last lines
        Weir.Sigs.suggestEntry Map.empty Map.empty env lines cur (Weir.Complete.wordStartAt cur cur.Length)

    testList "head option and until-stopped completion" [
        test "a loop head offers its option keys — its options record's fields [D:head-key-complete]" {
            Expect.equal (sug "repeat int") [ "interval=" ] "the reported case"
            Expect.equal (sug "repeat ") [ "interval="; "timeout=" ] "Repeat's fields"
            Expect.equal (sug "retry ") [ "attempts="; "delay="; "timeout=" ] "Retry's fields"
            Expect.equal (sug "poll ") [ "timeout="; "interval="; "watch=" ] "Poll's fields and the watch head key"
            Expect.equal (sug "retry attempts=3 ") [ "delay="; "timeout=" ] "a given key is not offered again"
            Expect.equal (sug "retry delay=(d * 2) t") [ "timeout=" ] "a parenthesized value counts as given"
            Expect.equal (sug "let n = repeat interval=10ms ") [ "timeout=" ] "the let-RHS head"
            Expect.equal (sug "    poll timeout=5m interval=1s watch=srv ") [] "all given: a closed, empty slot"
            Expect.isFalse (sug "retrying " |> List.contains "attempts=") "a word boundary: not the head"
            Expect.isFalse (sug "retry attempts=" |> List.contains "attempts=") "the value slot is not the key slot"
        }
        test "within serve offers its option keys after the server's name [D:serve-defaults]" {
            Expect.equal
                (sug "within serve s ")
                [
                    "bind="
                    "port="
                    "maxConcurrent="
                    "maxQueued="
                    "bodyTimeout="
                    "maxBody="
                    "handler="
                ]
                "ServerConfig's fields"

            Expect.equal (sug "within serve s port=8080 h") [ "handler=" ] "prefix-filtered"

            Expect.equal
                (sug "within serve s port=(Some 8080) handler=route ")
                [ "bind="; "maxConcurrent="; "maxQueued="; "bodyTimeout="; "maxBody=" ]
                "a given key is not offered again"

            Expect.isFalse (sug "within serve " |> List.contains "port=") "the name slot is not the key slot"
            Expect.isFalse (sug "within serve s port=" |> List.contains "port=") "the value slot is not the key slot"
            Expect.isFalse (sug "within serve s = " |> List.contains "port=") "the record form takes a value"
        }
        test "within lock offers timeout= after its path [D:head-key-complete]" {
            Expect.equal (sug "within lock \"state.json\" ") [ "timeout=" ] "after the path"
            Expect.equal (sug "within lock \"a b\" ti") [ "timeout=" ] "a spaced path, prefix-filtered"

            Expect.isFalse
                (sug "within lock \"p\" timeout=" |> List.contains "timeout=")
                "the value slot is not the key slot"
        }
        test "a yaml marker offers its open modifiers in canonical order [D:head-key-complete]" {
            Expect.equal (sug "let d = yaml ") [ "patch"; "schema=" ] "a fresh marker"
            Expect.equal (sug "let d = yaml pa") [ "patch" ] "prefix-filtered"
            Expect.equal (sug "let p = yaml patch ") [ "by="; "schema=" ] "after patch"
            Expect.equal (sug "let p = yaml patch by=name ") [ "schema=" ] "after by="
            Expect.equal (sug "let d = yaml schema=k8s ") [] "schema= closes the marker"
            Expect.equal (sug "let patch = yaml ") [ "patch"; "schema=" ] "a binder named patch is not the modifier"
        }
        test "until after a within's block offers stopped; a loop's until does not [D:until-stopped-complete]" {
            Expect.equal (entry [ "within"; "    print \"!\""; "until sto" ]) [ "stopped" ] "the reported case"
            Expect.equal (entry [ "within tmp d"; "    print d"; "until " ]) [ "stopped" ] "an empty word"

            Expect.equal
                (entry [ "within tmp d"; "    within"; "        print d"; "    until s" ])
                [ "stopped" ]
                "a nested within, at its own column"

            Expect.isFalse
                (entry [ "repeat interval=1s"; "    true"; "until " ] |> List.contains "stopped")
                "a repeat's until is not a hold"

            Expect.isFalse
                (entry [ "within"; "    print 1"; "always"; "    print 2"; "until " ]
                 |> List.contains "stopped")
                "after always is out of order"

            Expect.isTrue
                (Weir.Complete.untilStoppedAt [ "let x = within tmp d"; "    d" ] "until ")
                "a let-bound within"

            // a hostile line (`let` + a long space run) is bounded, never a hang
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let hostile = "let" + String.replicate 64000 " "
            Expect.isFalse (Weir.Complete.untilStoppedAt [ hostile ] "until ") "not a within head"
            Expect.isLessThan sw.ElapsedMilliseconds 2000L "the match is time-bounded"
        }
        test "an import completes a quoted module path: directories and .weir files [D:import-path-complete]" {
            skipOnWindows ()
            let d = tempPath "weir-imp"
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "lib")) |> ignore
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "m.weir"), "module")
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, "notes.txt"), "x")
            let dir = d.Replace("\\", "/")

            try
                Expect.equal
                    (sug ("import " + dir + "/"))
                    [ $"\"{dir}/lib/\""; $"\"{dir}/m.weir\"" ]
                    "quoted; the .txt is not a module"

                Expect.equal
                    (sug ("import \"" + dir + "/m"))
                    [ $"{dir}/m.weir" ]
                    "inside an open quote: no second quote"

                let line = "import " + dir + "/m"
                let got = sug line

                let produced =
                    line.Substring(0, Weir.Complete.wordStartAt line line.Length)
                    + got.Head
                    + " as M"

                match Weir.Parser.parseStmt produced with
                | Ok _ -> ()
                | Error e -> failtestf "the completed import must parse: %s -> %s" produced e
            finally
                System.IO.Directory.Delete(d, true)
        }
    ]

let completionHuntTests =
    let env = Weir.Prelude.extendTypes Weir.Builtins.typeEnvStrict

    let sug (line: string) =
        Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)

    let entry (lines: string list) =
        let cur = List.last lines
        Weir.Sigs.suggestEntry Map.empty Map.empty env lines cur (Weir.Complete.wordStartAt cur cur.Length)

    let withFiles (f: unit -> unit) =
        let d = tempPath "weir-hunt"

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(d, "dir with space"))
        |> ignore

        System.IO.File.WriteAllText(System.IO.Path.Combine(d, "dir with space", "inner.txt"), "x")

        for n in [ "plain.txt"; "mlib.weir"; "my file.txt"; "mybare.txt" ] do
            System.IO.File.WriteAllText(System.IO.Path.Combine(d, n), "x")

        try
            inCwd d f
        finally
            System.IO.Directory.Delete(d, true)

    // sequenced: the fixture moves the ambient session cwd
    testSequenced
    <| testList "completion hunt fixes" [
        test "a file name never completes bare into an expression [D:expr-paths-quoted]" {
            skipOnWindows ()

            withFiles (fun () ->
                Expect.equal (sug "File.read pl") [ "\"plain.txt\"" ] "a path parameter quotes"
                Expect.isFalse (sug "let x = 1 + pl" |> List.contains "plain.txt") "arithmetic offers no files"
                Expect.isFalse (sug "print (Seq.length mli" |> List.contains "mlib.weir") "nor an argument"
                Expect.isFalse (sug "ls dir" |> List.exists (fun c -> c.Contains "dir with")) "ls takes no argv"
                Expect.equal (sug "cat pl") [ "plain.txt" ] "a command's argv stays bare"
                Expect.equal (sug "cd pl") [ "plain.txt" ] "statement cd is argv"
                Expect.equal (sug "let x = $(cat pl") [ "plain.txt" ] "inside an open $( the command's argv"
                Expect.equal (sug "let x = $(cat dir") [ "\"dir with space/\"" ] "…quoted where a word can't hold it")
        }
        test "inside an open literal the word is its content: a spaced path continues [D:open-literal-word]" {
            skipOnWindows ()
            let line = "cat \"dir with space/"
            Expect.equal (Weir.Complete.wordStartAt line line.Length) 5 "the word starts after the quote"

            withFiles (fun () ->
                Expect.equal (sug "cat \"dir with space/i") [ "dir with space/inner.txt" ] "the re-Tab nests"
                Expect.equal (sug "File.read \"dir with space/i") [ "dir with space/inner.txt" ] "in an expression too"
                Expect.equal (sug "cat \"my f") [ "my file.txt" ] "a hand-opened quote")

            let hole = "print $\"a b {p.na"
            Expect.equal (Weir.Complete.wordStartAt hole hole.Length) (hole.Length - 4) "a hole keeps its own word"

            // one pass over the line: a long open literal is no slower than a short one
            let long = "cat \"" + String.replicate 50000 "a b "
            let sw = System.Diagnostics.Stopwatch.StartNew()
            Expect.equal (Weir.Complete.wordStartAt long long.Length) 5 "the word starts after the quote"
            Expect.isLessThan sw.ElapsedMilliseconds 1000L "linear in the line"

            let raw = "let s = \"\"\"a \"q\" b"
            Expect.equal (Weir.Complete.wordStartAt raw raw.Length) 11 "a triple-quoted literal's content"
        }
        test "a $ splice sees the statement's own binders in scope [D:splice-locals]" {
            Expect.equal (entry [ "for fname in [\"a\"] do"; "    echo $fn" ]) [ "fname" ] "a for binder"
            Expect.equal (entry [ "let greet fname = echo $fn" ]) [ "fname" ] "a function parameter"

            Expect.contains (entry [ "if true then"; "    let tag = \"x\""; "    echo $ta" ]) "tag" "a block-local let"

            Expect.isFalse
                (entry [ "for xs in [[\"a\"]] do"; "    echo $x" ] |> List.contains "xs")
                "a seq is not a $ splice"

            Expect.contains (entry [ "for xs in [[\"a\"]] do"; "    echo $@x" ]) "xs" "…it is a $@ splice"
            Expect.equal (entry [ "let f fname = 1"; "echo $fn" ]) [] "out of scope after its statement"
        }
        test "the Tab listing shows every candidate in columns, capped with a count [D:tab-listing]" {
            let items = [ for i in 1..30 -> $"item{i}" ]
            let lines = Weir.Repl.completionListing 40 items
            Expect.isTrue (lines.Length > 1) "several rows"
            Expect.isTrue (lines |> List.forall (fun l -> l.Length < 40)) "each fits the width"

            for it in items do
                Expect.isTrue (lines |> List.exists (fun l -> l.Split(' ') |> Array.contains it)) $"{it} shown"

            let many = Weir.Repl.completionListing 80 [ for i in 1..250 -> $"x{i}" ]
            Expect.stringContains (List.last many) "50 more" "past the cap, the rest is counted"
        }
        test "a word glued to a prefix offers only what needs no quotes [D:glued-argv-complete]" {
            skipOnWindows ()

            withFiles (fun () ->
                Expect.equal (sug "cat --x=my") [ "mybare.txt" ] "the spaced name would need the whole word quoted"
                Expect.equal (sug "dd if=pl") [ "plain.txt" ] "a bare-safe name completes"
                Expect.equal (sug "cat my") [ "\"my file.txt\""; "mybare.txt" ] "an unglued word still quotes")
        }
    ]

let completionHunt2Tests =
    let env = Weir.Prelude.extendTypes Weir.Builtins.typeEnvStrict

    let sug (line: string) =
        Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)

    let entry (lines: string list) =
        let cur = List.last lines
        Weir.Sigs.suggestEntry Map.empty Map.empty env lines cur (Weir.Complete.wordStartAt cur cur.Length)

    testList "completion hunt fixes, second batch" [
        test "a match arm's case binder types from the scrutinee [D:case-binder-complete]" {
            let pt = "type Pt = { x: int; y: int }"

            Expect.equal
                (entry [ pt; "let o = Some { x = 1; y = 2 }"; "match o with | Some r -> print r." ])
                [ "r.x"; "r.y" ]
                "Option's Some"

            Expect.equal
                (entry [
                    pt
                    "type Sh = Circle of Pt | Dot"
                    "match Circle { x = 1; y = 2 } with | Circle c -> print c."
                ])
                [ "c.x"; "c.y" ]
                "a user union's case"

            Expect.equal
                (entry [
                    pt
                    "let o = Some { x = 1; y = 2 }"
                    "match o with"
                    "| None -> ()"
                    "| Some r -> print r."
                ])
                [ "r.x"; "r.y" ]
                "a multi-line match, the arm under edit last"
        }
        test "an interpolation hole is an expression, never a statement head [D:hole-is-expression]" {
            let pathCmd =
                Weir.Extern.names ()
                |> Seq.tryFind (fun n -> n.Length > 3 && n |> Seq.forall System.Char.IsLetter)

            for cmd in Option.toList pathCmd do
                Expect.isFalse
                    (sug ("print $\"{" + cmd.Substring(0, 3)) |> List.contains cmd)
                    $"no command heads in a hole ({cmd})"

            Expect.isFalse
                (sug "let s = $\"{" |> List.exists (fun c -> c.StartsWith "#"))
                "no directives in an empty hole"

            Expect.contains (sug "print $\"{Path.c") "Path.combine" "members still complete"
            Expect.contains (sug "let s = $\"{tr") "true" "keywords still complete"
        }
        test "an env prefix keeps the head slot [D:env-prefix-head-complete]" {
            Expect.equal (Weir.Complete.headSlotAt "FOO=1") Weir.Complete.HeadSlot.Stmt "a statement head"
            Expect.equal (Weir.Complete.headSlotAt "NAME='a b'") Weir.Complete.HeadSlot.Stmt "a quoted value"
            Expect.equal (Weir.Complete.headSlotAt "let x = FOO=1") Weir.Complete.HeadSlot.LetRhs "a let-RHS head"
            Expect.equal (Weir.Complete.headSlotAt "FOO=") Weir.Complete.HeadSlot.No "a word glued to = is the value"
            Expect.contains (sug "FOO=1 c") "cd" "the head pool offers"
        }
    ]

let completionTypeNameTests =
    let sugIn (env: TypeEnv) (line: string) =
        Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)

    // sequenced: the fixture moves the ambient session cwd
    testSequenced
    <| testList "type-name completion [D:type-name-complete]" [
        test "an adapter slot offers only the types that cross it" {
            let run, env = Weir.Repl.sessionForTest ()
            run "type Pod = { name: string; restarts: int }" |> ignore
            run "type Job = { took: Duration }" |> ignore
            let sug = sugIn (env ())

            Expect.equal (sug "x |> from json Po") [ "Pod" ] "a readable record, never Poll"
            Expect.isFalse (sug "x |> from json S" |> List.contains "ServerConfig") "a Duration field does not cross"
            Expect.isFalse (sug "x |> from json J" |> List.contains "Job") "nor does the user's"
            Expect.isFalse (sug "x |> from json Http" |> List.contains "HttpRequest") "an untagged union field does not"
            Expect.equal (sug "x |> from json seq<Po") [ "Pod" ] "the seq wrap"
            Expect.equal (sug "x |> from toml Po") [ "Pod" ] "toml"
            Expect.equal (sug "x |> from table Po") [ "Pod" ] "table"
            Expect.contains (sug "x |> from yaml ") "stream" "yaml keeps its stream word"
        }
        test "an unresolved head's field pool is the declared records [D:row-field-pool]" {
            let run, env = Weir.Repl.sessionForTest ()
            let dir = System.IO.Directory.CreateTempSubdirectory("weir-rowpool").FullName
            let m = System.IO.Path.Combine(dir, "m.weir")
            System.IO.File.WriteAllText(m, "type Theme = { palette: string; glyph: string }\n")

            Expect.contains (sugIn (env ()) "fun q -> q.") "q.bytes" "no record declared: the prelude's"

            run $"import \"{m}\" as M" |> ignore
            Expect.isFalse (sugIn (env ()) "fun q -> q." |> List.contains "q.palette") "an import's never"

            run "type Pod = { name: string; restarts: int }" |> ignore
            run "type Job = { name: string; took: Duration }" |> ignore
            let sug = sugIn (env ())

            Expect.equal (sug "fun q -> q.") [ "q.name"; "q.restarts"; "q.took" ] "the session's records, alone"
            Expect.equal (sug "fun q -> q.took + q.") [ "q.name"; "q.took" ] "narrowed by a field already read"
            Expect.equal (sug "fun q -> q.na") [ "q.name" ] "the word being typed is not a read"
            Expect.equal (sug "fun q -> q.zzz + q.") [ "q.zzz" ] "a field no record holds stands alone"
            Expect.equal (sug "let f q = { q with ") [ "name"; "restarts"; "took" ] "the with slot draws on it too"
            System.IO.Directory.Delete(dir, true)
        }
        test "an arm's pattern slot offers the scrutinee's cases [D:match-case-complete]" {
            let run, env = Weir.Repl.sessionForTest ()
            run "type Shape = Circle of int | Square of int | Dot" |> ignore
            run "let y = Some 10" |> ignore
            run "let s = Dot" |> ignore

            let at (lines: string list) =
                let cur = List.last lines

                Weir.Sigs.suggestEntry Map.empty Map.empty (env ()) lines cur (Weir.Complete.wordStartAt cur cur.Length)

            Expect.equal (at [ "match y with"; "| N" ]) [ "None" ] "Option's, not every N in scope"
            Expect.equal (at [ "match y with"; "| " ]) [ "None"; "Some" ] "all of them at an empty word"
            Expect.equal (at [ "match y with | None -> 0 | S" ]) [ "Some" ] "on one line"
            Expect.equal (at [ "match s with"; "| Dot -> 0"; "| " ]) [ "Circle"; "Dot"; "Square" ] "a declared union"
            Expect.equal (at [ "match s with"; "| Circle _ | S" ]) [ "Square" ] "an or-pattern's alternative"
            Expect.equal (at [ "match 1 > 0 with"; "| t" ]) [ "true" ] "bool"
            Expect.equal (at [ "match y with"; "| n" ]) [] "a binder is nobody's case"
            Expect.contains (at [ "match y with"; "| None -> N" ]) "None" "an arm's body is an expression"

            Expect.isNonEmpty
                (at [ "match 3 with"; "| " ] @ at [ "match 3 with"; "| 1 -> p" ])
                "no union: the pool stands"
        }
        test "a field completes inside an open record literal [D:repair-anon-literal]" {
            let run, env = Weir.Repl.sessionForTest ()
            run "type Author = { name: string; born: int }" |> ignore
            run "type Book = { title: string; authors: seq<Author> }" |> ignore

            run "let books = [{ title = \"t\"; authors = [{ name = \"n\"; born = 1 }] }]"
            |> ignore

            let at (lines: string list) =
                let cur = List.last lines

                Weir.Sigs.suggestEntry Map.empty Map.empty (env ()) lines cur (Weir.Complete.wordStartAt cur cur.Length)

            Expect.equal
                (at [ "books"; "|> map (fun b -> {|Author = b.authors |> map _." ])
                [ "_.born"; "_.name" ]
                "the shorthand over the field's elements"

            Expect.equal
                (at [ "books |> map (fun b -> {| Title = b.title; Authors = b." ])
                [ "b.authors"; "b.title" ]
                "the lambda's own param, its record's fields alone"

            Expect.equal
                (at [ "books |> map (fun b -> {| A = b.authors |> map (fun a -> a.n" ])
                [ "a.name" ]
                "a nested lambda's param"

            // a declared record's literal checks only whole: mid-literal the
            // repair reads it as an anonymous one
            Expect.equal
                (at [ "books |> map (fun b -> { name = b.authors |> map _." ])
                [ "_.born"; "_.name" ]
                "inside a declared record's unfinished literal"

            Expect.equal
                (at [ "books |> map (fun b -> { name = b.title; born = b." ])
                [ "b.authors"; "b.title" ]
                "its lambda's param too"

            Expect.equal (at [ "let s = $\"{books |> map _." ]) [ "_.authors"; "_.title" ] "a hole's brace is no record"
        }
        test "a lambda's parameter shadows an outer binding in completion [D:complete-shadow]" {
            let run, env = Weir.Repl.sessionForTest ()
            run "type Book = { title: string; year: int }" |> ignore
            run "let items = [{ title = \"t\"; year = 1 }]" |> ignore
            run "let b = 5" |> ignore
            run "type Outer = { outer: int }" |> ignore
            run "let r = { outer = 1 }" |> ignore

            let at (line: string) =
                Weir.Sigs.suggestEntry
                    Map.empty
                    Map.empty
                    (env ())
                    [ line ]
                    line
                    (Weir.Complete.wordStartAt line line.Length)

            Expect.equal (at "items |> where (fun b -> b.") [ "b.title"; "b.year" ] "a non-record outer b"
            Expect.equal (at "items |> where (fun r -> r.") [ "r.title"; "r.year" ] "a record outer r"

            Expect.equal
                (at "items |> map (fun r -> r.year) |> map (fun n -> r.")
                [ "r.outer" ]
                "a closed lambda no longer binds"

            Expect.equal (at "r.") [ "r.outer" ] "no lambda: the binding's own fields"
            Expect.equal (at "items |> where (fun x -> r.") [ "r.outer" ] "another parameter leaves it alone"
        }
        test "a stage inside a lambda's parentheses completes as a stage [D:stage-in-group]" {
            let stage = Weir.Complete.lastStage
            Expect.equal (stage "echo a | tr a b | com") (Some("com", true)) "the statement's own level"

            Expect.equal
                (stage "xs |> iter (fun k -> echo $k | tr a b | com")
                (Some("com", true))
                "inside the open group"

            Expect.equal
                (stage "xs |> iter (fun (k, h) -> echo $k | ^head -c 1 | ")
                (Some("", true))
                "an empty stage there"

            Expect.equal (stage "echo a | f (x") None "the open group has no bar of its own"
            Expect.equal (stage "f (a | b) c") None "a closed group's bar is not the text's"
            Expect.equal (stage "xs |> map (fun x -> match x with | A") None "an arm's bar inside the group"
            Expect.equal (stage "xs |> iter (fun k -> echo \"a | b\" $k") None "a bar in a string"
            Expect.equal (stage "{| n = a") None "an anonymous record's opening bar"
            Expect.equal (stage "let r = {| a = 1 |} in r") None "and its closing one"
            Expect.equal (stage "echo {| | com") (Some("com", true)) "a real bar after one"

            Expect.equal
                (Weir.Complete.headSlotAt "xs |> iter (fun k -> echo $k | tail -c 9 |")
                Weir.Complete.HeadSlot.Stage
                "the slot is a stage head"

            let run, env = Weir.Repl.sessionForTest ()
            run "let xs = [\"a\"]" |> ignore
            let line = "xs |> iter (fun k -> echo $k | tr a b | comp"

            Expect.contains
                (Weir.Complete.suggest (env ()) line (Weir.Complete.wordStartAt line line.Length))
                "complete"
                "the reifier is offered"
        }
        test "a type position in a declaration offers type names" {
            let run, env = Weir.Repl.sessionForTest ()
            run "type Pod = { name: string }" |> ignore
            let sug = sugIn (env ())

            Expect.contains (sug "type W = { pod: Po") "Pod" "a field's type"
            Expect.contains (sug "type U = A of Po") "Pod" "a case payload"
            Expect.contains (sug "type U = A of int * Po") "Pod" "a tuple payload"
            Expect.contains (sug "type W = { xs: seq<Po") "Pod" "a generic argument"
            Expect.contains (sug "type W = { n: in") "int" "built-in names"
            Expect.isFalse (sug "type W = { [<Sh" |> List.contains "Pod") "an attribute is not a type position"

            let lines = [ "type W = {"; "    pod: Po" ]
            let cur = List.last lines

            Expect.contains
                (Weir.Sigs.suggestEntry
                    Map.empty
                    Map.empty
                    (env ())
                    lines
                    cur
                    (Weir.Complete.wordStartAt cur cur.Length))
                "Pod"
                "the multi-line field form"
        }
        test "an import's types complete; a validated type offers create, never its case" {
            withTempDir "weir-tnc" (fun dir ->
                inCwd dir (fun () ->
                    File.WriteAllLines(
                        Path.Combine(dir, "m.weir"),
                        [
                            "module M"
                            "type Cfg = { a: int }"
                            "type Mail = Email of string"
                            "    validate s = if not (Str.contains \"@\" s) then fail \"no @\""
                            "let mk : int -> Cfg"
                            "let mk n = { a = n }"
                        ]
                    )

                    let run, env = Weir.Repl.sessionForTest ()
                    Expect.stringContains (run "import \"./m.weir\"") "module M imported" "imported"
                    let sug = sugIn (env ())
                    let members = sug "M."
                    Expect.contains members "M.Cfg" "a record type"
                    Expect.contains members "M.Mail" "the validated type"
                    Expect.contains members "M.mk" "members stay"
                    Expect.isFalse (List.contains "M.Email" members) "the refused case"
                    Expect.equal (sug "M.Mail.cr") [ "M.Mail.create" ] "the qualified create"
                    Expect.contains (sug "x |> from json M.") "M.Cfg" "a qualified type in an adapter slot"

                    let out =
                        run "type Addr = Addr of string\n    validate s = if s == \"\" then fail \"empty\""

                    Expect.isFalse (out.Contains "error") $"the validated type declares: {out}"
                    Expect.equal (sugIn (env ()) "Addr.cr") [ "Addr.create" ] "a local validated type's create"))
        }
    ]

let completionPipelineTests =
    let env = Weir.Prelude.extendTypes Weir.Builtins.typeEnvStrict

    let entry (lines: string list) =
        let cur = List.last lines
        Weir.Sigs.suggestEntry Map.empty Map.empty env lines cur (Weir.Complete.wordStartAt cur cur.Length)

    let pre = [ "type R = { n: int; tag: string }"; "let xs = [{ n = 1; tag = \"a\" }]" ]

    testList "pipeline completion across lines [D:repair-hole-expr]" [
        test "a predicate lambda's param types from the source, on its own line or not" {
            Expect.equal (entry (pre @ [ "xs |> Seq.where (fun r -> r." ])) [ "r.n"; "r.tag" ] "one line"
            Expect.equal (entry (pre @ [ "xs"; "    |> Seq.where (fun r -> r." ])) [ "r.n"; "r.tag" ] "a leading |>"
            Expect.equal (entry (pre @ [ "xs |>"; "    Seq.where (fun r -> r." ])) [ "r.n"; "r.tag" ] "a trailing |>"
            Expect.equal (entry (pre @ [ "xs"; "    |> Seq.where _." ])) [ "_.n"; "_.tag" ] "the shorthand"
        }
        test "later stages, a let-bound pipeline, a lambda body on its own line" {
            Expect.equal
                (entry (
                    pre
                    @ [ "xs"; "    |> Seq.where (fun r -> r.n > 0)"; "    |> Seq.map (fun q -> q." ]
                ))
                [ "q.n"; "q.tag" ]
                "after a where"

            Expect.equal
                (entry (pre @ [ "let ys ="; "    xs"; "    |> Seq.sortBy (fun r -> r." ]))
                [ "r.n"; "r.tag" ]
                "a let's RHS"

            Expect.equal
                (entry (pre @ [ "xs"; "    |> Seq.iter (fun r ->"; "        print r." ]))
                [ "r.n"; "r.tag" ]
                "a body block"
        }
        test "in a live session: the REPL's own env, bindings from earlier entries" {
            let run, envOf = Weir.Repl.sessionForTest ()
            run "type R = { n: int; tag: string }" |> ignore
            run "let xs = [{ n = 1; tag = \"a\" }]" |> ignore

            let entryIn (lines: string list) =
                let cur = List.last lines

                Weir.Sigs.suggestEntry
                    Map.empty
                    Map.empty
                    (envOf ())
                    lines
                    cur
                    (Weir.Complete.wordStartAt cur cur.Length)

            Expect.equal (entryIn [ "xs |>"; "    Seq.where (fun r -> r." ]) [ "r.n"; "r.tag" ] "a trailing |>"
            Expect.equal (entryIn [ "xs |> Seq.where (fun r -> r." ]) [ "r.n"; "r.tag" ] "one line"

            Expect.equal
                (entryIn [ "xs |> Seq.map (fun r -> {| v = r.n |}) |>"; "    Seq.map _." ])
                [ "_.v" ]
                "an anonymous record upstream"
        }
        test "an anonymous record built mid-pipeline completes downstream [D:anon-literals]" {
            Expect.equal
                (entry (pre @ [ "xs |> Seq.map (fun r -> {| v = r.n; w = r.tag |}) |> Seq.map _." ]))
                [ "_.v"; "_.w" ]
                "one line, the shorthand"

            Expect.equal
                (entry (
                    pre
                    @ [
                        "xs"
                        "    |> Seq.map (fun r -> {| v = r.n |})"
                        "    |> Seq.where (fun q -> q."
                    ]
                ))
                [ "q.v" ]
                "across lines, a predicate"
        }
    ]

let completionBatch1Tests =
    let env = Weir.Prelude.extendTypes Weir.Builtins.typeEnvStrict

    let sugIn (env: TypeEnv) (line: string) =
        Weir.Complete.suggest env line (Weir.Complete.wordStartAt line line.Length)

    let sug = sugIn env

    testList "completion: captures, #help forms, directive args, splice stems" [
        test "a command head inside $( completes [D:capture-head-complete]" {
            Expect.equal (Weir.Complete.headSlotAt "let x = $(") Weir.Complete.HeadSlot.Stmt "the capture's head"

            Expect.equal
                (Weir.Complete.headSlotAt "print (Seq.length $(")
                Weir.Complete.HeadSlot.Stmt
                "nested in an expression"

            Expect.equal
                (Weir.Complete.headSlotAt "let x = $(git branch) + ")
                Weir.Complete.HeadSlot.No
                "a closed capture is not"

            Expect.contains (sug "let x = $(c") "cd" "the head pool"
        }
        test "#help completes the forms it documents [D:help-forms-complete]" {
            Expect.contains (sug "#help rep") "repeat" "repeat"
            Expect.contains (sug "#help wi") "within" "within"
            Expect.contains (sug "#help ret") "retry" "retry"
            Expect.contains (sug "#help po") "poll" "poll"
        }
        test "#types and #echo offer their arguments [D:directive-args-complete]" {
            Expect.equal (sug "#types ") [ "off"; "on" ] "#types"
            Expect.equal (sug "#types o") [ "off"; "on" ] "prefix-filtered, never keywords"
            Expect.equal (sug "#echo ") [ "all" ] "#echo"
        }
        test
            "a record splices through its stem; a dynamic head takes a string [D:splice-stems] [D:dynamic-head-complete]" {
            let entry (cur: string) =
                let lines = [
                    "type Cli = { tag: string; n: int }"
                    "let cli = { tag = \"a\"; n = 1 }"
                    "let tool = \"git\""
                    "let count = 1"
                    cur
                ]

                Weir.Sigs.suggestEntry Map.empty Map.empty env lines cur (Weir.Complete.wordStartAt cur cur.Length)

            Expect.contains (entry "echo $cl") "cli." "the record's stem"
            Expect.equal (entry "^$to") [ "tool" ] "a string at a dynamic head"
            Expect.isFalse (entry "^$co" |> List.contains "count") "an int is not a program"
            Expect.equal (entry "echo x^$to") [] "glued mid-word stays refused"
        }
    ]

let replHistoryUtf8Tests =
    // the history file's invalid-UTF-8 lines [D:history-utf8]: skipped from
    // recall, written back byte-for-byte in place on every rewrite
    let nl = System.Text.Encoding.UTF8.GetBytes System.Environment.NewLine
    let bad1 = [| 0xFFuy; 0xFEuy; byte ' '; byte 'x' |]
    let bad2 = [| 0xC3uy; byte '(' |]

    let file (lines: byte[] list) =
        lines
        |> List.collect (fun l -> List.ofArray l @ List.ofArray nl)
        |> Array.ofList

    let u (s: string) = System.Text.Encoding.UTF8.GetBytes s

    let entries (lines: Weir.Repl.HistoryLine seq) = [
        for l in lines do
            match l with
            | Weir.Repl.HEntry e -> e
            | Weir.Repl.HRaw _ -> ()
    ]

    testList "repl history invalid UTF-8 [D:history-utf8]" [
        test "valid lines are entries, invalid lines are held raw — never recalled" {
            let lines = Weir.Repl.historyParseForTest (file [ u "a"; bad1; u "b"; bad2 ])
            Expect.equal (entries lines) [ "a"; "b" ] "only the valid lines recall"

            Expect.equal
                [
                    for l in lines do
                        match l with
                        | Weir.Repl.HRaw r -> r
                        | _ -> ()
                ]
                [ bad1; bad2 ]
                "the bad lines are held as their bytes"
        }
        test "parse then render is bytes in == bytes out" {
            let bytes = file [ u "let s = \"é\""; bad1; u "x\\ny"; bad2; u "" ]
            Expect.equal (Weir.Repl.historyRenderForTest (Weir.Repl.historyParseForTest bytes)) bytes "byte-identical"
        }
        test "an escaped multi-line entry decodes whole; a bad byte in one is one raw line" {
            let lines =
                Weir.Repl.historyParseForTest (file [ u "match x with\\n| _ -> 1"; Array.append (u "if a\\n") bad1 ])

            Expect.equal (entries lines) [ "match x with\n| _ -> 1" ] "the valid entry keeps its newline"

            Expect.equal
                (Weir.Repl.historyRenderForTest lines)
                (file [ u "match x with\\n| _ -> 1"; Array.append (u "if a\\n") bad1 ])
                "the bad entry round-trips"
        }
        test "load compaction dedupes entries keep-last; raw lines keep their places" {
            let lines =
                Weir.Repl.historyParseForTest (file [ u "a"; bad1; u "b"; u "a"; bad1; bad2 ])

            let c = Weir.Repl.historyCompactForTest 100 true lines

            Expect.equal
                (Weir.Repl.historyRenderForTest c)
                (file [ bad1; u "b"; u "a"; bad1; bad2 ])
                "raw lines never dedupe"
        }
        test "the cap counts file lines, raw included" {
            let lines = Weir.Repl.historyParseForTest (file [ u "a"; bad1; u "b"; bad2; u "c" ])
            let c = Weir.Repl.historyCompactForTest 3 true lines
            Expect.equal (Weir.Repl.historyRenderForTest c) (file [ u "b"; bad2; u "c" ]) "the last three lines"
        }
        test "a re-entered line's rewrite keeps raw lines byte-for-byte in place" {
            let img =
                ResizeArray(Weir.Repl.historyParseForTest (file [ u "a"; bad1; u "b"; bad2 ]))

            Expect.equal (Weir.Repl.historyRememberForTest true img "a") 1 "one earlier copy erased"
            Expect.equal (Weir.Repl.historyRenderForTest img) (file [ bad1; u "b"; bad2; u "a" ]) "raw lines in place"
        }
        test "CRLF terminators and a leading BOM are not line content" {
            let bytes =
                Array.concat [ [| 0xEFuy; 0xBBuy; 0xBFuy |]; u "a\r\n"; bad1; u "\r\nb" ]

            let lines = Weir.Repl.historyParseForTest bytes
            Expect.equal (entries lines) [ "a"; "b" ] "BOM and CR stripped"

            Expect.equal
                (Weir.Repl.historyRenderForTest lines)
                (file [ u "a"; bad1; u "b" ])
                "the raw line's bytes exact"
        }
    ]

let sigTests =
    // disk fixtures: a .weir/sigs tree in a temp dir (the fs-members style)
    let withSigTree (sigSource: string list) (f: string -> 'a) : 'a =
        withTempDir "weir-sig" (fun root ->
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".weir", "sigs"))
            |> ignore

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".git"))
            |> ignore

            System.IO.File.WriteAllLines(System.IO.Path.Combine(root, ".weir", "sigs", "fixtool.weir"), sigSource)

            f root)

    let fixSig = [
        "module Fixtool"
        "let version = \"fixtool 1.2.3\""
        "type BuildArgs = {"
        "    [<Short \"o\">]"
        "    outfile: bool"
        "    [<Positional>]"
        "    target: string"
        "}"
        "type Cmd = Build of BuildArgs | Clean"
    ]

    let diagsFor (root: string) (extraSig: string list) (script: string list) =
        let p = System.IO.Path.Combine(root, "use.weir")
        System.IO.File.WriteAllLines(p, script)

        let ds = diagsIn p (List.ofArray (System.IO.File.ReadAllLines p))

        ds |> List.filter (fun d -> d.Code = "sig")

    // the flags a line warns on, in order
    let sigWarns (sigSource: string list) (line: string) : string list =
        withSigTree sigSource (fun root ->
            diagsFor root [] [ "#sig fixtool"; line ]
            |> List.map (fun d -> (d.Message.Split '\'')[1]))

    testList "Command signatures [D:command-signatures]" [
        test "unknown long flag warns with did-you-mean, naming the declaring line" {
            withSigTree fixSig (fun root ->
                let ds =
                    diagsFor root [] [ "#sig fixtool"; "fixtool build --outfil x t"; "print \"ok\"" ]

                match ds with
                | [ d ] ->
                    Expect.equal d.Severity "warning" "partial warns"
                    Expect.stringContains d.Message "unknown flag '--outfil' for fixtool" ""
                    Expect.stringContains d.Message "Did you mean '--outfile'?" ""
                    Expect.stringContains d.Message "#sig fixtool, line 1" "declared-by teaching"
                | other -> failtest $"expected one sig diagnostic, got {other.Length}")
        }
        test "exhaustive signatures ERROR; positionals derive no flag; shorts are explicit-only" {
            let exSig =
                fixSig
                |> List.map (fun l ->
                    if l.StartsWith "let version" then
                        l + "\n\nlet exhaustive = true"
                    else
                        l)

            withSigTree exSig (fun root ->
                let ds =
                    diagsFor root [] [
                        "#sig fixtool"
                        "fixtool build -o positional-word --target-is-operand-not-flag"
                        "print \"ok\""
                    ]

                match ds with
                | [ d ] ->
                    Expect.equal d.Severity "error" "exhaustive errors"

                    Expect.stringContains
                        d.Message
                        "'--target-is-operand-not-flag'"
                        "the positional field derived no flag"
                | other -> failtest $"expected one (the -o short is DECLARED), got {other.Length}")
        }
        test "a subcommand matching no case disables flag checking (L2's discipline)" {
            withSigTree fixSig (fun root ->
                let ds =
                    diagsFor root [] [ "#sig fixtool"; "fixtool frobnicate --whatever"; "print \"ok\"" ]

                Expect.equal ds [] "no operand model, no guess")
        }
        test "end-of-flags: words after a bare -- are operands" {
            withSigTree fixSig (fun root ->
                let ds =
                    diagsFor root [] [ "#sig fixtool"; "fixtool build -- --not-a-flag"; "print \"ok\"" ]

                Expect.equal ds [] "")
        }
        test "a missing signature is a located check error naming path, search root, and the fix" {
            withSigTree fixSig (fun root ->
                let ds = diagsFor root [] [ "#sig ghost"; "print \"ok\"" ]

                match ds with
                | [ d ] ->
                    Expect.equal d.Severity "error" ""
                    Expect.equal d.Line 1 "at the directive"
                    Expect.stringContains d.Message "ghost.weir" ""
                    Expect.stringContains d.Message "weir add sig ghost" "names the fix"
                | other -> failtest $"unexpected {other.Length}")
        }
        test
            "a pin per DESUGARED shape [D:desugar-capture]: for/within/retry bodies, if groups, splats plain AND reified" {
            let sig1 = [ "module Ftool"; "let version = \"ftool 1.0\""; "type Cmd = { alpha: bool }" ]

            let root = tempPath "weir-sigshape"

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".weir", "sigs"))
            |> ignore

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".git"))
            |> ignore

            System.IO.File.WriteAllLines(System.IO.Path.Combine(root, ".weir", "sigs", "ftool.weir"), sig1)

            try
                let shapes = [
                    "for-body", [ "#sig ftool"; "for x in [1] do ftool --alhpa" ]
                    "within-body", [ "#sig ftool"; "within tmp w"; "    ftool --alhpa"; "print \"x\"" ]
                    "retry-body", [ "#sig ftool"; "retry attempts=1 delay=0ms"; "    ftool --alhpa | succeeds" ]
                    "if-group",
                    [
                        "#sig ftool"
                        "let ok = true"
                        "if ok then"
                        "    ftool --alhpa"
                        "print \"x\""
                    ]
                    "splat-plain", [ "#sig ftool"; "let ws = [\"w\"]"; "ftool --alhpa $@ws" ]
                    "splat-reified", [ "#sig ftool"; "let ws = [\"w\"]"; "ftool --alhpa $@ws | orFail \"x\"" ]
                ]

                for name, script in shapes do
                    let p = System.IO.Path.Combine(root, "use.weir")
                    System.IO.File.WriteAllLines(p, script)

                    let ds = diagsIn p (List.ofArray (System.IO.File.ReadAllLines p))

                    match ds |> List.filter (fun d -> d.Code = "sig") with
                    | [ d ] -> Expect.stringContains d.Message "unknown flag '--alhpa'" name
                    | other -> failtest $"{name}: expected one sig diagnostic, got {other.Length}"
            finally
                System.IO.Directory.Delete(root, true)
        }
        test "REIFIED commands are checked too (the desugar replaces ECmd — recovered from the builtin spine)" {
            withSigTree fixSig (fun root ->
                let ds =
                    diagsFor root [] [
                        "#sig fixtool"
                        "let a = fixtool build --outfil x | exitCode"
                        "let b = fixtool build --outfil x | succeeds"
                        "fixtool build --outfil x | orFail \"boom\""
                        "print (show a)"
                    ]

                Expect.equal ds.Length 3 "each reifier shape recovers (prog is the LAST string in orFail's spine)"

                for d in ds do
                    Expect.stringContains d.Message "unknown flag '--outfil'" "")
        }
        test "a Wire attr carries a keyword flag spelling [D:sig-version-probe]" {
            // docker --type / kubectl --for: the generator emits
            // [<Wire "type">] typeFlag, and the flag set reads Wire
            withSigTree
                [
                    "module Fixtool"
                    "type Cmd = {"
                    "    [<Wire \"type\">]"
                    "    typeFlag: bool"
                    "}"
                ]
                (fun root ->
                    let ok = diagsFor root [] [ "#sig fixtool"; "fixtool --type x" ]
                    Expect.equal ok [] "--type matches through Wire"

                    let ds = diagsFor root [] [ "#sig fixtool"; "fixtool --typ x" ]
                    Expect.equal ds.Length 1 "the typo still catches"

                    Expect.stringContains
                        ds[0].Message
                        "Did you mean '--type'?"
                        "…did-you-mean speaks the WIRE spelling")
        }
        test "#sig with a quoted tool name teaches the bare word" {
            withSigTree fixSig (fun root ->
                let ds = diagsFor root [] [ "#sig \"fixtool\""; "print \"x\"" ]

                match ds with
                | [ d ] -> Expect.stringContains d.Message "drop the quotes: #sig fixtool" "the teach names the repair"
                | other -> failtest $"unexpected {other.Length}")
        }
        test "a declared-but-unused signature is INERT" {
            withSigTree fixSig (fun root ->
                let ds = diagsFor root [] [ "#sig fixtool"; "print \"never calls it\"" ]
                Expect.equal ds [] "legal, not an error")
        }
        test "the sig file needs Cmd; `let version` is OPTIONAL [D:sig-version-probe]" {
            // a tool that does not answer --version has no identity to
            // record — a version-less sig validates and its flags check
            withSigTree [ "module Fixtool"; "type Cmd = { x: bool }" ] (fun root ->
                let ds = diagsFor root [] [ "#sig fixtool"; "print \"x\"" ]
                Expect.equal ds [] "version absence is a stated fact, not a defect")

            withSigTree [ "module Fixtool"; "type Cmd = { outfile: bool }" ] (fun root ->
                let ds = diagsFor root [] [ "#sig fixtool"; "fixtool --outfil x" ]
                Expect.equal ds.Length 1 "the surface still checks without a version"
                Expect.stringContains ds[0].Message "unknown flag '--outfil'" "")

            withSigTree [ "module Fixtool"; "let version = \"v\""; "type Other = { x: bool }" ] (fun root ->
                let ds = diagsFor root [] [ "#sig fixtool"; "print \"x\"" ]

                match ds with
                | [ d ] -> Expect.stringContains d.Message "no type `Cmd`" "the surface is Cmd by name"
                | other -> failtest $"unexpected {other.Length}")
        }
        // the check's model [D:sig-arity] [D:sig-conventions]
        // [D:sig-operand-stop] [D:sig-unwalked]: each case is (sig, line,
        // the flags it must warn on — nothing else)
        test "sig file names are injective; a plain name is its own [D:sig-probe-bounds]" {
            let names = [
                "/a/b/cob"
                "_a_b_cob"
                ".weir"
                "weir"
                "..evil"
                "evil"
                ".evil"
                "a b"
                "a_b"
                "a%20b"
                "a/b"
                "a\\b"
                "ü"
                "x.weir"
            ]

            let files = names |> List.map Weir.Sigs.sigFileName
            Expect.equal (List.distinct files).Length names.Length $"collision in {files}"

            for plain in [ "curl"; "git-lfs"; "python3.12"; "add_sig"; "ü" ] do
                Expect.equal (Weir.Sigs.sigFileName plain) (plain + ".weir") plain

            for f in files do
                Expect.isFalse (f.Contains '/' || f.Contains '\\' || f.StartsWith ".") f

            // past NAME_MAX the name hashes: under 255 bytes, stable, apart
            let deep = "/" + String.concat "/" [ for i in 0..60 -> $"d{i:D2}" ] + "/ft"
            let deep2 = deep.Replace("/ft", "/fu")
            let wide = String.replicate 260 "a"

            let long =
                [ deep; deep2; wide; String.replicate 130 "ü" ]
                |> List.map Weir.Sigs.sigFileName

            for f in long do
                Expect.isLessThanOrEqual (System.Text.Encoding.UTF8.GetByteCount f) 255 f
                Expect.isFalse (f.Contains '/') f

            Expect.equal (List.distinct long).Length long.Length $"collision in {long}"
            Expect.equal (Weir.Sigs.sigFileName deep) (Weir.Sigs.sigFileName deep) "stable"
            Expect.isFalse (List.exists (fun f -> List.contains f files) long) "a hashed name meets no encoded one"

            Expect.equal
                (Weir.Sigs.sigFileName (String.replicate 250 "a"))
                (String.replicate 250 "a" + ".weir")
                "a plain name up to the limit is its own"
        }
        test "an older all-bool GNU signature checks as it always did" {
            let old = [
                "module Fixtool"
                "type Cmd = {"
                "    verbose: bool"
                "    [<Short \"o\">]"
                "    output: bool"
                "}"
            ]

            for line, want in
                [
                    "fixtool --output x --verbse", [ "--verbse" ]
                    "fixtool -o x -z", [ "-z" ]
                    "fixtool --output=x y", []
                    // a bool never takes the next word: -o does not eat --verbse
                    "fixtool -o --verbse", [ "--verbse" ]
                    "fixtool -- --verbse", []
                    // a multi-letter single-dash word stays unread, as it
                    // always was: no letter-by-letter bundle
                    "fixtool -oz -fsSL -i.bak -TERM x", []
                ] do
                Expect.equal (sigWarns old line) want line

            Expect.equal
                (sigWarns ("module Fixtool" :: "let exhaustive = true" :: List.tail old) "fixtool -Lv x")
                []
                "exhaustive too"
        }
        test "a generated all-bool signature states bundles: its multi-letter dash words are read [D:sig-help-shapes]" {
            let gen = [
                "module Fixtool"
                "let bundles = true"
                "type Cmd = {"
                "    [<Short \"d\">]"
                "    delete: bool"
                "    [<Short \"s\">]"
                "    squeeze: bool"
                "}"
            ]

            for line, want in [ "fixtool -ds a b", []; "fixtool -dQ x", [ "-Q" ]; "fixtool -Q x", [ "-Q" ] ] do
                Expect.equal (sigWarns gen line) want line
        }
        test "dashWords never swallows a single letter: `kill -x` is a typo [D:sig-help-shapes]" {
            let killSig = [
                "module Fixtool"
                "let dashWords = true"
                "type Cmd = {"
                "    [<Short \"s\">]"
                "    signal: string"
                "    [<Short \"l\">]"
                "    list: bool"
                "}"
            ]

            for line, want in
                [
                    "fixtool -TERM 1", []
                    "fixtool -s HUP 1", []
                    "fixtool -9 1", []
                    "fixtool -l", []
                    "fixtool -x 1", [ "-x" ]
                    "fixtool -a 1", [ "-a" ]
                ] do
                Expect.equal (sigWarns killSig line) want line
        }
        test "values: a valued flag takes its word, globals check at every level, an unknown subcommand ends the check" {
            let dock = [
                "module Fixtool"
                "type Globals = {"
                "    context: string"
                "    [<Short \"H\">]"
                "    host: string"
                "}"
                "type PsFlags = {"
                "    [<Short \"a\">]"
                "    all: bool"
                "}"
                "type NetFlags = { quiet: bool }"
                "type NetCreateFlags = {"
                "    [<Short \"d\">]"
                "    driver: string"
                "}"
                "type RunFlags = {"
                "    rm: bool"
                "    [<Short \"d\">]"
                "    detach: bool"
                "    name: string"
                "}"
                "let stopAtOperand = [ \"run\" ]"
                "type Cmd = Ps of PsFlags | Network of NetFlags | NetworkCreate of NetCreateFlags | Run of RunFlags"
            ]

            for line, want in
                [
                    "fixtool --context prod ps -a", []
                    "fixtool --context=prod ps -a --al", [ "--al" ]
                    "fixtool --contxt prod ps", [ "--contxt" ]
                    "fixtool ps --context x -a", []
                    // a parent's own flag is not its child's (cobra's local flags)
                    "fixtool network create -d bridge net1 --quiet", [ "--quiet" ]
                    "fixtool network --quiet create -d bridge net1", []
                    "fixtool network creat --whatever", []
                    "fixtool -H tcp://x run --rm img sh -c x --whatever", []
                    "fixtool run --nmae web img", [ "--nmae" ]
                    "fixtool run -d --rm img", []
                    "fixtool compose up -d --buld", []
                ] do
                Expect.equal (sigWarns dock line) want line
        }
        test "a spliced word holds its place: a flag's value, or an operand naming no case" {
            let dock = [
                "module Fixtool"
                "type Globals = { context: string }"
                "type PsFlags = { all: bool }"
                "type Cmd = Ps of PsFlags"
            ]

            withSigTree dock (fun root ->
                let ds =
                    diagsFor root [] [
                        "#sig fixtool"
                        "let c = \"prod\""
                        "fixtool --context $c ps --al"
                        "fixtool $c --al"
                    ]

                Expect.equal (ds |> List.map (fun d -> d.Line, (d.Message.Split '\'')[1])) [ 3, "--al" ] "")
        }
        test "an unchecked case ends the check: its flags are open" {
            let git = [
                "module Fixtool"
                "type LogFlags = { follow: bool }"
                "type CommitFlags = { amend: bool }"
                "let unchecked = [ \"log\" ]"
                "type Cmd = Log of LogFlags | Commit of CommitFlags"
            ]

            Expect.equal (sigWarns git "fixtool log --oneline --graphh") [] "log"
            Expect.equal (sigWarns git "fixtool commit --amendd") [ "--amendd" ] "commit"

            // "" names the tool itself [D:sig-open-tool]: none of its lines check
            let npm = [ "module Fixtool"; "type Cmd = { save: bool }"; "let unchecked = [ \"\" ]" ]

            Expect.equal (sigWarns npm "fixtool install --prefix /x --sav") [] "an open tool"
        }
        test "short bundles check letter by letter; a valued letter takes the rest of the word or the next" {
            let tar = [
                "module Fixtool"
                "type Cmd = {"
                "    [<Short \"x\">]"
                "    extract: bool"
                "    [<Short \"z\">]"
                "    gzip: bool"
                "    [<Short \"f\">]"
                "    file: string"
                "    inPlace: Option<string>"
                "    [<Wire \"-i\">]"
                "    shorti: Option<string>"
                "}"
            ]

            for line, want in
                [
                    "fixtool -xzf a.tgz", []
                    "fixtool -fx", []
                    "fixtool -i.bak -zi.bak x", []
                    "fixtool -xqf a", [ "-q" ]
                    "fixtool --in-place=.bak --inplace", [ "--inplace" ]
                ] do
                Expect.equal (sigWarns tar line) want line

            withSigTree tar (fun root ->
                let d = diagsFor root [] [ "#sig fixtool"; "fixtool -xqf a" ] |> List.exactlyOne
                Expect.stringContains d.Message "unknown flag '-q' (in '-xqf') for fixtool" "the bundle is quoted")
        }
        test "an option that ends the tool's options, and an interpreter's operand stop" {
            let py = [
                "module Fixtool"
                "type Cmd = {"
                "    [<Wire \"-m\">]"
                "    optm: seq<string>"
                "    [<Wire \"-B\">]"
                "    optB: bool"
                "    [<Wire \"-X\">]"
                "    optX: string"
                "    help: bool"
                "    [<Wire \"-h\">]"
                "    shorth: bool"
                "}"
                "let stopAtOperand = [ \"\" ]"
            ]

            for line, want in
                [
                    "fixtool -m pytest -x --tb=short", []
                    "fixtool -B -X importtime -h -Z x.py", [ "-Z" ]
                    "fixtool script.py --verbose", []
                    "fixtool --b x.py", [ "--b" ]
                ] do
                Expect.equal (sigWarns py line) want line
        }
        test "the Go convention: -x, -long and --long are one flag, no bundles" {
            let go = [
                "module Fixtool"
                "let convention = \"go\""
                "type Cmd = {"
                "    [<Wire \"-plaintext\">]"
                "    plaintext: bool"
                "    [<Wire \"-H\">]"
                "    header: string"
                "    [<Wire \"-max-time\">]"
                "    maxTime: string"
                "}"
            ]

            for line, want in
                [
                    "fixtool -plaintext -H x h:1 list", []
                    "fixtool --plaintext --max-time 5 --H x h:1 list", []
                    "fixtool -max-time=5 h", []
                    "fixtool -plaintextt h", [ "-plaintextt" ]
                    "fixtool -max-tme 3 h", [ "-max-tme" ]
                ] do
                Expect.equal (sigWarns go line) want line

            withSigTree go (fun root ->
                let d =
                    diagsFor root [] [ "#sig fixtool"; "fixtool --plaintextt h" ] |> List.exactlyOne

                Expect.stringContains d.Message "Did you mean '--plaintext'?" "the suggestion keeps the typed dashes")
        }
        test "the find convention: whole single-dash words; -- only where listed" {
            let find = [
                "module Fixtool"
                "let convention = \"find\""
                "type Cmd = {"
                "    [<Wire \"-name\">]"
                "    name: string"
                "    [<Wire \"-delete\">]"
                "    delete: bool"
                "    [<Wire \"-maxdepth\">]"
                "    maxdepth: string"
                "    [<Wire \"-mtime\">]"
                "    mtime: string"
                "    [<Wire \"-L\">]"
                "    follow: bool"
                "    [<Wire \"-O\">]"
                "    opt: Option<string>"
                "    [<Wire \"--help\">]"
                "    help: bool"
                "}"
            ]

            for line, want in
                [
                    "fixtool . -nmae x", [ "-nmae" ]
                    "fixtool . --maxdepth 1", [ "--maxdepth" ]
                    "fixtool -L . -name x -mtime -7 -delete", []
                    "fixtool -O3 . -maxdepth 1", []
                    "fixtool --help", []
                ] do
                Expect.equal (sigWarns find line) want line
        }
        test "abbreviations and negation are the tool's, declared" {
            let gnu = [
                "module Fixtool"
                "let abbreviations = true"
                "let negatable = true"
                "type Cmd = {"
                "    verbose: bool"
                "    version: bool"
                "    wildcards: bool"
                "    stackSize: string"
                "}"
            ]

            for line, want in
                [
                    "fixtool --wildcard --verbos x", []
                    "fixtool --ver", [ "--ver" ]
                    "fixtool --no-wildcards --no-verbose", []
                    "fixtool --no-stack-size", [ "--no-stack-size" ]
                ] do
                Expect.equal (sigWarns gnu line) want line

            let plain = gnu |> List.filter (fun l -> not (l.StartsWith "let "))

            Expect.equal
                (sigWarns plain "fixtool --wildcard --no-verbose")
                [ "--wildcard"; "--no-verbose" ]
                "absent lets: neither"
        }
        test "did-you-mean offers shorts and refuses distant matches" {
            let s = [
                "module Fixtool"
                "type Cmd = {"
                "    vv: bool"
                "    [<Short \"H\">]"
                "    header: string"
                "}"
            ]

            withSigTree s (fun root ->
                match diagsFor root [] [ "#sig fixtool"; "fixtool --H x"; "fixtool --q" ] with
                | [ h; q ] ->
                    Expect.stringContains h.Message "Did you mean '-H'?" "a short is a candidate"

                    Expect.isFalse
                        (q.Message.Contains "Did you mean")
                        $"a one-letter name borrows nothing: {q.Message}"
                | other -> failtest $"expected two, got {other.Length}")
        }
        test "an unknown convention is a load error at the directive" {
            let s = [ "module Fixtool"; "let convention = \"bsd\""; "type Cmd = { x: bool }" ]

            withSigTree s (fun root ->
                let d = diagsFor root [] [ "#sig fixtool"; "print \"x\"" ] |> List.exactlyOne
                Expect.equal d.Line 1 "at the directive"
                Expect.stringContains d.Message "names no flag convention" "")
        }
    ]

let continuedCommandTests =
    // an indented line under a command adds arguments; one whose first word
    // names a program was meant as its own command [D:continued-command]
    let hitsIn (lines: string list) =
        diagsIn "cc.weir" lines |> List.filter (fun d -> d.Code = "continued-command")

    let silent (lines: string list) (label: string) =
        Expect.isEmpty
            (hitsIn lines)
            $"{label}: expected no continued-command warning, got {hitsIn lines |> List.map _.Message}"

    let one (lines: string list) =
        match hitsIn lines with
        | [ d ] -> d
        | other -> failtest $"expected one continued-command warning, got {other |> List.map (fun d -> d.Message)}"

    let withSigTree (sigSource: string list) (script: string list) =
        withTempDir "weir-cc-sig" (fun root ->
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".weir", "sigs"))
            |> ignore

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".git"))
            |> ignore

            System.IO.File.WriteAllLines(System.IO.Path.Combine(root, ".weir", "sigs", "fixtool.weir"), sigSource)
            let p = System.IO.Path.Combine(root, "use.weir")
            System.IO.File.WriteAllLines(p, script)

            diagsIn p (List.ofArray (System.IO.File.ReadAllLines p))
            |> List.filter (fun d -> d.Code = "continued-command" || d.Code = "sig"))

    testList "continued command [D:continued-command]" [
        test "two pasted git pushes warn at the second git, as a warning with its own code" {
            let d =
                one [
                    "git push --force-with-lease origin ci/cargo-cache"
                    "    git push --force-with-lease origin fix/joey-workflow-env"
                ]

            Expect.equal (d.Line, d.Col) (2, 5) "located at the continuation's first word"
            Expect.equal (d.EndLine, d.EndCol) (Some 2, Some 8) "the word squiggles whole"
            Expect.equal d.Severity "warning" "advisory: check exits 0"

            Expect.equal
                d.Message
                "this indented line continues `git push …` — its words become its arguments, and `git` looks like a second command: dedent the line to run it on its own, or quote the word if it really is an argument"
                "the message names the command, the word and both repairs"
        }
        test "echo over an indented echo warns" {
            let d = one [ "echo a"; "    echo b" ]
            Expect.stringContains d.Message "`echo a …`" "the continued command is shown"
        }
        test "flags and plain arguments on the continuation are silent" {
            silent [ "echo a"; "    --flag x" ] "a flag"
            silent [ "echo a"; "    -n b" ] "a short flag"
            silent [ "echo a"; "    hello world" ] "plain words"
            silent [ "echo a"; "    ./bin/echo b" ] "a path"
            silent [ "let x = \"v\""; "echo a"; "    $x echo" ] "a splice"
        }
        test "a | continuation is the multi-line pipeline — silent" {
            silent [ "echo a | cat"; "    | cat" ] "a pipe stage"
            silent [ "echo a"; "    |> Seq.iter print" ] "a function stage"
        }
        test "a command that takes a command is exempt, every listed wrapper" {
            for w in Weir.Sigs.commandWrappers do
                silent [ $"{w} -x a"; "    echo b" ] w
        }
        test "the wrapper list is the brief's" {
            Expect.equal
                (Set.count Weir.Sigs.commandWrappers)
                19
                "sudo doas env xargs time nice nohup timeout exec command watch ssh parallel flock chroot strace stdbuf setsid unbuffer"
        }
        test "a quoted word stays an argument — silent" {
            silent [ "echo a"; "    \"echo\" b" ] "double-quoted"
            silent [ "echo a"; "    'echo' b" ] "single-quoted"
        }
        test "a word not on PATH is an argument — silent" { silent [ "echo a"; "    notaprogramzzq b" ] "unresolved" }
        test "an explicit ^cmd counts as a program" {
            let d = one [ "echo a"; "    ^git status" ]
            Expect.stringContains d.Message "`git` looks like a second command" "the sigil is dropped from the name"
        }
        test "inside a function body, at a deeper indent, it warns" {
            let d = one [ "let f () ="; "    echo x"; "        printf y"; "f ()" ]
            Expect.equal (d.Line, d.Col) (3, 9) "located in the body"
        }
        test "inside within, if and for bodies it warns" {
            Expect.equal (hitsIn [ "within tmp d"; "    echo inside"; "        echo nested" ]).Length 1 "within"
            Expect.equal (hitsIn [ "if 1 > 0 then"; "    echo a"; "        echo b" ]).Length 1 "if"
            Expect.equal (hitsIn [ "for f in [\"a\"] do"; "    echo $f"; "        echo b" ]).Length 1 "for"
        }
        test "sibling body lines are separate commands — silent" {
            silent [ "if 1 > 0 then"; "    echo a"; "    echo b" ] "siblings"
        }
        test "a continued pipeline flags the stage the line lands in" {
            let d = one [ "echo a | cat"; "    echo c" ]
            Expect.stringContains d.Message "`cat …`" "the last stage is continued"
        }
        test "a reified command is checked too" {
            let d = one [ "let _r = echo a"; "    echo b | complete" ]
            Expect.stringContains d.Message "`echo a …`" "the reified command"
        }
        test "heredoc content is bytes — silent" {
            silent [ "let t = <<<"; "    echo a"; "        echo b"; "t |> Seq.iter print" ] "heredoc"
            silent [ "let t = $<<<"; "    echo a"; "        echo b"; "t |> Seq.iter print" ] "interpolated heredoc"
        }
        test "a sig's stopAtOperand exempts the case it names, not its siblings" {
            let sigSrc = [
                "module Fixtool"
                "let version = \"fixtool 1.2.3\""
                "let stopAtOperand = [ \"run\" ]"
                "type RunArgs = {"
                "    [<Positional>]"
                "    image: string"
                "}"
                "type Cmd = Run of RunArgs | Clean"
            ]

            let ran = withSigTree sigSrc [ "#sig fixtool"; "fixtool run img"; "    echo b" ]
            Expect.isEmpty ran $"run takes a command: {ran |> List.map _.Message}"
            let cleaned = withSigTree sigSrc [ "#sig fixtool"; "fixtool clean"; "    echo b" ]

            Expect.equal
                (cleaned |> List.map _.Code)
                [ "continued-command" ]
                $"clean does not: {cleaned |> List.map _.Message}"
        }
        test "a sig naming the tool itself (\"\") exempts every line" {
            let sigSrc = [
                "module Fixtool"
                "let version = \"fixtool 1.2.3\""
                "let stopAtOperand = [ \"\" ]"
                "type Cmd = { verbose: bool }"
            ]

            let ds = withSigTree sigSrc [ "#sig fixtool"; "fixtool --verbose x"; "    echo b" ]
            Expect.isEmpty ds $"the tool takes a command: {ds |> List.map _.Message}"
        }
        test "the REPL refuses the entry with the same message" {
            match Weir.Repl.continuedCommandForTest "echo a\n    echo b" with
            | Some m -> Expect.stringStarts m "2:5: this indented line continues `echo a …`" "located, same message"
            | None -> failtest "the entry must be refused"
        }
        test "the REPL takes a clean entry, a pipeline continuation included" {
            Expect.isNone (Weir.Repl.continuedCommandForTest "echo a | cat\n    | cat") "a pipe stage"
            Expect.isNone (Weir.Repl.continuedCommandForTest "if 1 > 0 then\n    echo a\n    echo b") "siblings"
        }
        test "the REPL checks the whole entry before anything: a later statement's hit refuses it" {
            match Weir.Repl.continuedCommandForTest "let n = 1\nprint (show n)\necho a\n    echo b" with
            | Some m -> Expect.stringStarts m "4:5:" "the hit in the third statement"
            | None -> failtest "the entry must be refused"
        }
    ]

let tempDirLintTests =
    // the newTempDir footgun [D:newtempdir-lint]: a Path.newTempDir binding
    // deleted in the same scope is the manual (and Ctrl+C-leaky) spelling of
    // a `within tmp` block; an unmatched bind is the legitimate escaping use
    // and stays silent. Warning severity — check still exits 0.
    let diagsOf (lines: string list) =
        let ds = diagsIn "tmp.weir" lines
        ds |> List.filter (fun d -> d.Code = "temp-dir-cleanup")

    let silent (lines: string list) (label: string) =
        Expect.isEmpty
            (diagsOf lines)
            $"{label}: expected no temp-dir warning, got {diagsOf lines |> List.map _.Message}"

    testList "newTempDir footgun [D:newtempdir-lint]" [
        test "bind then Dir.deleteAll warns at the delete site — command named, within suggested" {
            match diagsOf [ "let d = Path.newTempDir ()"; "print d"; "Dir.deleteAll d" ] with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (3, 1) "located at the delete"
                Expect.equal d.Severity "warning" "advisory, never a gate"
                Expect.stringContains d.Message "Dir.deleteAll" "the delete call is named"
                Expect.stringContains d.Message "within tmp d" "the repair points at the scoped block"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "Dir.delete (non-recursive) also warns, naming the call it saw" {
            match diagsOf [ "let d = Path.newTempDir ()"; "Dir.delete d" ] with
            | [ d ] -> Expect.stringContains d.Message "Dir.delete " "the non-recursive call is named"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "warning severity is exit-0's substance: no error rides along" {
            let ds = diagsIn "tmp.weir" [ "let d = Path.newTempDir ()"; "Dir.deleteAll d" ]
            Expect.isFalse (ds |> List.exists (fun d -> d.Severity = "error")) "check exits 0 on a warning-only file"
        }
        test "an UNMATCHED bind is the escaping use — silent" {
            silent [ "let d = Path.newTempDir ()"; "print d" ] "no in-scope delete"
        }
        test "a within tmp block is the good form — silent" {
            silent [ "within tmp scratch"; "    print scratch" ] "within tmp"
        }
        test "deleting a DIFFERENT directory does not warn" {
            silent [ "let d = Path.newTempDir ()"; "print d"; "Dir.deleteAll \"/tmp/other\"" ] "unrelated delete"
        }
        test "a plain Dir.deleteAll with no newTempDir binding is silent" {
            silent [ "let p = \"/tmp/scratch\""; "Dir.deleteAll p" ] "not a temp dir"
        }
        test "block-local bind + delete warns inside its own body" {
            match
                diagsOf [
                    "let f () ="
                    "    let d = Path.newTempDir ()"
                    "    Dir.deleteAll d"
                    ""
                    "f ()"
                ]
            with
            | [ d ] ->
                Expect.equal d.Line 3 "the local delete"
                Expect.stringContains d.Message "within tmp d" "the scoped repair"
            | other -> failtest $"expected one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "one bind, one delete, one warning — a later stray delete of the name does not re-warn" {
            // the binder is resolved at its first matched delete; a second
            // delete of the (now untracked) name is not a newTempDir pairing
            match diagsOf [ "let d = Path.newTempDir ()"; "Dir.deleteAll d"; "Dir.deleteAll d" ] with
            | [ _ ] -> ()
            | other -> failtest $"expected exactly one warning, got {other |> List.map (fun d -> d.Message)}"
        }
        test "POISON: an errored statement suppresses the advisory pass" {
            silent
                [ "let d = Path.newTempDir ()"; "Dir.deleteAll d"; "print (\"a\" + 1)" ]
                "one real error beats advisory noise"
        }
    ]

let replSaveDistillTests =
    testList "#save distill [D:repl-save]" [
        test "(a) the guarantee: a distilled session weir-checks clean" {
            let lines, _ =
                distill [
                    ("Endpoint", "type Endpoint = {\n    host: string\n    port: int\n}")
                    ("manifest", "let manifest = <<<\n    apiVersion: v1\n    kind: Pod")
                    ("gobeldy", "let gobeldy = it")
                ]

            Expect.isTrue (distilledChecks lines) $"the distilled file must check clean: {lines}"
        }
        test "(b) DEDUP: a redeclared type is kept ONCE, in its LAST form" {
            let lines, dropped =
                distill [
                    ("Color", "type Color = Red | Green")
                    ("Color", "type Color = Red | Green | Blue")
                ]

            let joined = String.concat "\n" lines

            let occurrences =
                lines |> List.filter (fun l -> l.Contains "type Color") |> List.length

            Expect.equal occurrences 1 $"one surviving Color decl: {lines}"
            Expect.stringContains joined "Blue" "the LAST form survives"
            Expect.equal dropped 0 "a dedup is not a session-only drop"
        }
        test "(c) a self-contained heredoc body is PRESERVED across newlines (not flattened)" {
            let lines, _ =
                distill [
                    ("manifest", "let manifest = <<<\n    apiVersion: v1\n    kind: Pod\n    name: web")
                ]

            let joined = String.concat "\n" lines
            // the body rides as real newlines, not the assembler's sentinel
            Expect.isFalse (joined.Contains "\x1d" || joined.Contains "\x1f") "no join sentinel leaked"
            Expect.stringContains joined "apiVersion: v1" "the first body line"
            Expect.stringContains joined "name: web" "the last body line"
            Expect.isTrue (lines |> List.length >= 4) $"the heredoc spans several physical lines: {lines}"
            Expect.isTrue (distilledChecks lines) "and it still checks"
        }
        test "(d) DROP: a bare it-referencing binding is removed, with the note count" {
            let lines, dropped =
                distill [ ("keep", "let keep = \"hi\""); ("gobeldy", "let gobeldy = it") ]

            let joined = String.concat "\n" lines
            Expect.isFalse (joined.Contains "gobeldy") $"the it-binding is dropped: {lines}"
            Expect.equal dropped 1 "one session-only drop is counted for the note"
            Expect.isTrue (distilledChecks lines) "the survivor checks clean"
        }
        test "(e) a self-contained named let is KEPT (protected, not dropped)" {
            // an unused-but-self-contained binding is the product of a
            // session, not scratch — the guarantee protects it (a
            // `_`-prefix) rather than dropping it, and does not count it
            let lines, dropped = distill [ ("greeting", "let greeting = \"hello\"") ]
            let joined = String.concat "\n" lines
            Expect.stringContains joined "\"hello\"" $"the value survives: {lines}"
            Expect.equal dropped 0 "a self-contained binding is not a session-only drop"
            Expect.isTrue (distilledChecks lines) "and it checks clean"
        }
        test "(d2) CHAIN: a dependent of a redefined name keeps the definition it read [D:repl-save-chain]" {
            // the session's `b` read the FIRST `a`: the dedup alone would
            // leave it reading a name bound only below it, so the earlier
            // `a` is restored and the file computes the session's values
            let lines, dropped =
                distill [ ("a", "let a = 1"); ("b", "let b = a + 1"); ("a", "let a = \"s\"") ]

            Expect.equal lines [ "let a = 1"; "let _b = a + 1"; "let _a = \"s\"" ] "the chain survives in order"
            Expect.equal dropped 0 "nothing referenced session-only state"
            Expect.isTrue (distilledChecks lines) "and it checks clean"

            let lines, dropped =
                distill [ ("f", "let f x = x + 1"); ("g", "let g x = f x"); ("f", "let f x = x * 10") ]

            Expect.equal lines [ "let f x = x + 1"; "let _g x = f x"; "let _f x = x * 10" ] "g keeps the f it called"
            Expect.equal dropped 0 "no drop"
        }
        test "(d3) CHAIN: a shadowing chain is saved whole, not dropped [D:repl-save-chain]" {
            let lines, dropped =
                distill [ ("xs", "let xs = [1; 2]"); ("xs", "let xs = xs |> Seq.map (fun x -> x * 2)") ]

            Expect.equal lines [ "let xs = [1; 2]"; "let _xs = xs |> Seq.map (fun x -> x * 2)" ] "both links"
            Expect.equal dropped 0 "no drop"

            let lines, _ =
                distill [ ("a", "let a = 1"); ("a", "let a = a + 1"); ("a", "let a = a + 1") ]

            Expect.equal lines [ "let a = 1"; "let a = a + 1"; "let _a = a + 1" ] "a three-link chain"
        }
        test "(d4) a superseded type's values are dropped, never saved under the new type" {
            let lines, dropped =
                Weir.Repl.distillSession realResolver (fun _ -> None) [
                    Weir.Repl.SaveDef("T", "type T = A | B", [ "A"; "B" ])
                    Weir.Repl.SaveDef("t", "let t = A", [])
                    Weir.Repl.SaveDef("T", "type T = C | D", [ "C"; "D" ])
                    Weir.Repl.SaveDef("u", "let u = C", [])
                ]

            Expect.equal lines [ "type T = C | D"; "let _u = C" ] "the old-shape value is gone"
            Expect.equal dropped 1 "and counted"
        }
        test "(d5) a user `it` an echo rebound is session-only state [D:repl-it]" {
            // session: let it = 5; 3 (echo rebinds it); let y = it + 1 -> 4.
            // Saving `let it = 5` + `let y = it + 1` would compute 6
            let lines, dropped =
                Weir.Repl.distillSession realResolver (fun _ -> None) [
                    Weir.Repl.SaveDef("it", "let it = 5", [])
                    Weir.Repl.SaveEcho
                    Weir.Repl.SaveDef("y", "let y = it + 1", [])
                ]

            Expect.isFalse (lines |> List.exists (fun l -> l.Contains "it + 1")) $"the reader is dropped: {lines}"
            Expect.equal dropped 1 "and counted"

            // with no echo in between the user's `it` is the value read
            let lines, _ =
                Weir.Repl.distillSession realResolver (fun _ -> None) [
                    Weir.Repl.SaveDef("it", "let it = 5", [])
                    Weir.Repl.SaveDef("y", "let y = it + 1", [])
                ]

            Expect.equal lines [ "let it = 5"; "let _y = it + 1" ] "kept"
        }
        test "(d6) a session let named like a bare alias is the user's value, never qualified or alias-desugared" {
            let aliasOf n =
                if n = "find" then Some("echo", [ "FOUND" ]) else None

            let lines, dropped =
                Weir.Repl.distillDefs realResolver aliasOf [ ("find", "let find = 3"); ("r", "let r = find + 1") ]

            Expect.equal lines [ "let find = 3"; "let _r = find + 1" ] "no Seq.find, no echo FOUND"
            Expect.equal dropped 0 "no drop"
        }
        test "(f) PROTECT is surgical: a binder a later survivor reads keeps its name" {
            // the chained-session shape: `base` is read by `total`, only
            // `total` is unread. Protecting every binder would rename
            // `base` under its reader's feet (`let _base` + `let _total =
            // base |> …` — `base` then resolves as a phantom command), so
            // only the binder the unused finding names takes the `_`
            let lines, dropped =
                distill [
                    ("base", "let base = [\"10\"; \"20\"]")
                    ("total", "let total = base |> Seq.map Str.toInt |> Seq.sum")
                ]

            let joined = String.concat "\n" lines
            Expect.stringContains joined "let base" $"the read binder keeps its name: {lines}"
            Expect.stringContains joined "let _total" "the unread tail is protected, not dropped"
            Expect.equal dropped 0 "nothing referenced session-only state"
            Expect.isTrue (distilledChecks lines) "and it checks clean"
        }
        test "(g) PROTECT renames the unused binder inside a pattern, never the pattern" {
            let lines, dropped =
                distill [
                    ("ab", "let (a, b) = (1, 2)")
                    ("c", "let c = a")
                    ("pq", "let { x = p; y = q } = {| x = 1; y = 2 |}")
                ]

            let joined = String.concat "\n" lines
            Expect.isFalse (joined.Contains "let _(") $"a pattern is never prefixed: {lines}"
            Expect.stringContains joined "let (a, _b) = (1, 2)" "the unused pattern binder takes the `_`"
            Expect.stringContains joined "{ x = _p; y = _q }" "two findings on one line both land"
            Expect.stringContains joined "let _c = a" "a plain binder as before"
            Expect.equal dropped 0 "nothing referenced session-only state"
            Expect.isTrue (distilledChecks lines) "and it checks clean"
        }
        test "a bare alias is qualified (map -> Seq.map) in a survivor" {
            let lines, _ = distill [ ("picked", "let picked = [\"a\"] |> map (Str.toUpper)") ]
            let joined = String.concat "\n" lines
            Expect.stringContains joined "Seq.map" $"the bare alias is qualified: {lines}"
        }
        test "dedup keys on the name, not the text: two unrelated types both survive" {
            let lines, _ = distill [ ("A", "type A = X | Y"); ("B", "type B = P | Q") ]
            let joined = String.concat "\n" lines
            Expect.stringContains joined "type A" "A survives"
            Expect.stringContains joined "type B" "B survives"
        }
    ]

let aliasTests =
    testList "command-head aliases [D:command-head-alias]" [
        test "(a) a head alias resolves to its exe; the rest stays bare argv" {
            // k get po -o yaml  ->  ECmd(kubectl, [get; po; -o; yaml])
            match parseWith aliasResolver "k get po -o yaml" with
            | Ok s ->
                Expect.stringContains s "kubectl" "the head becomes the real exe"
                Expect.isFalse (s.Contains "\"k\"") "the alias name 'k' is gone from the head"
            | Error e -> failtest e
        }
        test "(b) prefix args are inserted after the exe, before user argv" {
            // kb overlays/prod  ->  ECmd(kustomize, [build; overlays/prod])
            match parseWith aliasResolver "kb overlays/prod" with
            | Ok s ->
                Expect.stringContains s "kustomize" "the exe"
                Expect.stringContains s "build" "the fixed prefix arg"
                Expect.stringContains s "overlays/prod" "the user arg, after the prefix"
                // build precedes overlays/prod
                Expect.isTrue (s.IndexOf "build" < s.IndexOf "overlays/prod") "the prefix arg comes before the user arg"
            | Error e -> failtest e
        }
        test "(c) injection-safety: `k $x` passes the splice as ONE argv entry" {
            // the alias is a resolution-table entry, not a re-lex: a
            // single splice stays a single argument.
            match Weir.Parser.parseLine aliasResolver "k get $x" with
            | Ok(SCmd {
                          Kind = ECmd(HeadLit "kubectl", args, _)
                      })
            | Ok(SExpr {
                           Kind = ECmd(HeadLit "kubectl", args, _)
                       }) ->
                // args: get (literal), then the one splice $x
                Expect.equal (List.length args) 2 "exactly two argv entries: 'get' and the single splice"
            | Ok other -> failtest $"expected an ECmd headed by kubectl, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "(d) `^` bypasses the alias table (forces the real PATH binary)" {
            // ^git is a real external; the ^ forces PATH and the alias
            // table is never consulted. A shadowing alias `ls = ls
            // --color` is bypassed by `^ls` — no prefix injected.
            match Weir.Parser.parseLine aliasResolver "^ls x" with
            | Ok(SCmd { Kind = ECmd(HeadLit "ls", args, _) })
            | Ok(SExpr { Kind = ECmd(HeadLit "ls", args, _) }) ->
                Expect.equal (List.length args) 1 "just the user arg — no --color prefix (bypassed)"
            | Ok other -> failtest $"expected a bare ls ECmd, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "(e) an alias applies to the HEAD only — a name in argv is untouched" {
            // `git k` : k in argument position is a plain word, not
            // resolved
            match Weir.Parser.parseLine aliasResolver "git add k" with
            | Ok(SCmd { Kind = ECmd(HeadLit "git", _, _) })
            | Ok(SExpr { Kind = ECmd(HeadLit "git", _, _) }) ->
                let s =
                    (parseWith aliasResolver "git add k")
                    |> function
                        | Ok v -> v
                        | Error e -> e

                Expect.stringContains s "\"k\"" "the argv 'k' stays a literal string, not kubectl"
                Expect.isFalse (s.Contains "kubectl") "no head-rewrite in argument position"
            | Ok other -> failtest $"expected a git ECmd, got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "(e2) a name bound in scope beats the alias table [D:alias-binding-shadow]" {
            // a parameter, a lambda binder, a match-arm binder and a
            // block let are names bound in scope: `k + 1` reads them,
            // never runs `kubectl + 1`
            for src in
                [
                    "let f k = k + 1"
                    "[1] |> Seq.map (fun k -> k + 1)"
                    "match 3 with | k -> k + 1"
                    "let g () = let k = 5 in k + 1"
                ] do
                match Weir.Parser.parseLine aliasResolver src with
                | Ok stmt ->
                    let s = sprintf "%A" stmt
                    Expect.isFalse (s.Contains "kubectl") $"{src}: the binding wins"
                | Error e -> failtest $"{src}: {e}"

            // and outside the binding the alias still heads a command
            match parseWith aliasResolver "k get po" with
            | Ok s -> Expect.stringContains s "kubectl" "the alias still resolves an unbound head"
            | Error e -> failtest e
        }
        test "(f) REPL-only: the base script resolver carries no aliases" {
            // realResolver (no AliasHead) — `k` is not a known external,
            // so it never resolves as an aliased command head.
            match Weir.Parser.parseLine realResolver "k get po" with
            | Ok(SCmd { Kind = ECmd(HeadLit "kubectl", _, _) })
            | Ok(SExpr { Kind = ECmd(HeadLit "kubectl", _, _) }) -> failtest "an alias leaked into the base resolver"
            | _ -> () // unbound / not-a-command is the correct outcome
        }
        test "(g) #save DESUGAR: a kept `let = k …` saves as the real invocation" {
            let lines, _ =
                Weir.Repl.distillDefs realResolver aliasOf [ ("pods", "let pods = k get po -o json") ]

            let joined = String.concat "\n" lines
            Expect.stringContains joined "kubectl get po -o json" $"the head is desugared: {lines}"
            Expect.isFalse (joined.Contains "= k ") "no bare alias head remains"
            Expect.isTrue (distilledChecks lines) "the desugared file checks clean (alias-free)"
        }
        test "(h) #save DESUGAR: prefix args are reinserted for a kept `kb`-headed let" {
            let lines, _ =
                Weir.Repl.distillDefs realResolver aliasOf [ ("m", "let m = kb overlays/prod") ]

            let joined = String.concat "\n" lines
            Expect.stringContains joined "kustomize build overlays/prod" $"exe + prefix + argv: {lines}"
            Expect.isTrue (distilledChecks lines) "and it checks clean"
        }
        test "(h2) #save DESUGAR: an alias over a builtin value forces the PATH binary (^ls)" {
            // `ls` is weir's typed value in a script; the alias meant the
            // external, so the saved head is `^ls` and its dependents stay
            let lines, dropped =
                Weir.Repl.distillDefs realResolver aliasOf [
                    ("files", "let files = ls /tmp")
                    ("n", "let n = files |> Seq.length")
                ]

            Expect.equal lines [ "let files = ^ls --color /tmp"; "let _n = files |> Seq.length" ] "desugared, kept"
            Expect.equal dropped 0 "nothing dropped"
            Expect.isTrue (distilledChecks lines) "and it checks clean"
        }
        test "(i) #save DESUGAR is span-based: a string spelling the alias is untouched" {
            let lines, _ =
                Weir.Repl.distillDefs realResolver aliasOf [ ("s", "let s = \"k is a letter\"") ]

            let joined = String.concat "\n" lines
            Expect.stringContains joined "\"k is a letter\"" $"the string literal is left intact: {lines}"
            Expect.isFalse (joined.Contains "kubectl") "no rewrite inside a string"
        }
        test "(j) parse: a malformed #alias line is rejected; a well-formed one parses" {
            Expect.isTrue (Weir.Repl.parseAliasLineForTest "k = kubectl" |> Result.isOk) "well-formed"
            Expect.isTrue (Weir.Repl.parseAliasLineForTest "kb = kustomize build" |> Result.isOk) "with prefix"
            Expect.isTrue (Weir.Repl.parseAliasLineForTest "= kubectl" |> Result.isError) "no name"
            Expect.isTrue (Weir.Repl.parseAliasLineForTest "k" |> Result.isError) "no ="

            match Weir.Repl.parseAliasLineForTest "let = echo" with
            | Error e -> Expect.stringContains e "keyword" "a keyword name is refused, named"
            | Ok _ -> failtest "#alias let = … must be refused"
        }
        test "(j2) a chaining glyph in an alias target is refused, naming the function spelling [D:alias-no-chain]" {
            for glyph in [ "|"; "|>"; "||"; "&&"; ";" ] do
                match Weir.Repl.parseAliasLineForTest $"inf = git add . {glyph} and git commit" with
                | Error e ->
                    Expect.stringContains e $"'{glyph}' in an alias target is a literal argument" glyph
                    Expect.stringContains e "let inf () =" glyph
                | Ok _ -> failtest $"'{glyph}' in an alias target must be refused"

            // a glyph glued inside a word is that word's text, not a chain
            Expect.isTrue (Weir.Repl.parseAliasLineForTest "g = grep a|b" |> Result.isOk) "glued is a literal"
        }
        test "(k) an alias to the builtin cd APPLIES it, never spawns /usr/sbin/cd [D:command-head-alias]" {
            // #alias up = cd .. — `up` must resolve cd as the builtin (the
            // application EApp(cd, "..")), not ECmd(HeadLit "cd", …), which
            // spawned the external /usr/sbin/cd with too many arguments
            match Weir.Parser.parseLine aliasResolver "up" with
            | Ok(SCmd {
                          Kind = EApp({ Kind = EVar "cd" }, { Kind = EStr ".." })
                      })
            | Ok(SExpr {
                           Kind = EApp({ Kind = EVar "cd" }, { Kind = EStr ".." })
                       }) -> ()
            | Ok other -> failtest $"expected cd applied to '..', got {other}"
            | Error e -> failtest $"parse failed: {e}"
        }
        test "(k2) the prompt sanitizer keeps SGR alone [D:session-prompt]" {
            let san = Weir.Repl.sanitizePromptForTest
            Expect.equal (san "\x1b[32mX\x1b[0m> ") "\x1b[32mX\x1b[0m> \x1b[0m" "SGR survives, reset appended"
            Expect.equal (san "\x1b]0;evil title\x07x> ") "x> " "an OSC title (BEL-terminated) drops whole"
            Expect.equal (san "\x1b]8;;http://evil\x1b\\x> ") "x> " "an OSC hyperlink (ST-terminated) drops whole"
            Expect.equal (san "\x1b]52;c;payload\x07x> ") "x> " "an OSC clipboard write drops whole"
            Expect.equal (san "\x1bP+q544e\x1b\\x> ") "x> " "a DCS drops whole"
            Expect.equal (san "\x1b[2Ax> ") "x> " "a non-SGR CSI (cursor move) drops whole"
            Expect.equal (san "x\x1b") "x" "a dangling ESC drops alone"
            Expect.equal (san "a\nb\tc") "a b c" "controls flatten to spaces"

            Expect.equal
                (san "\x1b]0;unterminated x> ")
                "]0;unterminated x> "
                "an unterminated OSC loses its ESC; the rest is inert text"
        }
        test "(k) the prompt's visible width is terminal cells [D:session-prompt]" {
            Expect.equal (Weir.Repl.visibleWidthForTest "weir> ") 6 "plain ascii"
            Expect.equal (Weir.Repl.visibleWidthForTest "\x1b[32mX\x1b[0m> ") 3 "SGR is zero-width"
            Expect.equal (Weir.Repl.visibleWidthForTest "中> ") 4 "CJK counts two cells"
            Expect.equal (Weir.Repl.visibleWidthForTest "🌿> ") 4 "an emoji counts two cells"
            Expect.equal (Weir.Repl.visibleWidthForTest "é> ") 3 "a combining mark counts none"
            Expect.equal (Weir.Repl.visibleWidthForTest " x> ") 5 "a private-use glyph counts one"
        }
        test "(k3) PromptStatus carries the entry's duration [D:prompt-duration]" {
            let dom = Weir.Statements.promptStatusDomain

            Expect.isTrue
                (dom (TRowVar("r", [ "duration", TDur ])))
                "a provider reading duration as a Duration is accepted"

            Expect.isTrue (dom (TRowVar("r", [ "ok", TBool; "duration", TVar "a" ]))) "a forwarded duration is accepted"
            Expect.isFalse (dom (TRowVar("r", [ "duration", TInt ]))) "a duration pinned to int refuses"
            expectValue "let st = { ok = true; exit = None; duration = 2500ms } in st.duration > 2s" (VBool true)
        }
        test "(k4) the waiting indicator is off until the REPL turns it on [D:waiting-repl-only]" {
            Expect.isFalse Weir.Types.Waiting.enabled "a script, -e or a test never draws it"
            Expect.equal (Weir.Types.Waiting.during "sleeping" (fun () -> 7)) 7 "the work still runs, silently"
        }
    ]

let contPromptTests =
    // the continuation marker is always present and exactly promptWidth
    // wide [D:repl-multiline] [D:cont-marker] — the bug: a short custom
    // prompt (`❯ `, width 2) fell to blank spaces, losing the hint
    let vis = Weir.Repl.visibleWidthForTest

    testList "continuation prompt [D:cont-marker]" [
        test "a wide prompt keeps ... ; a narrow one still shows a hint; width preserved" {
            let wide = Weir.Repl.deriveContPromptForTest 10
            Expect.stringContains wide "∙" "the mark, right-aligned"
            Expect.equal (vis wide) 10 "display width matches the prompt (uniform column math)"

            let narrow = Weir.Repl.deriveContPromptForTest 2
            Expect.stringContains narrow "∙" "a 2-wide prompt gets the mark, not blank"
            Expect.equal (vis narrow) 2 "display width still matches"
            Expect.notEqual (narrow.Trim()) "" "the marker is not blank (the bug)"
        }
        test "an extended key report behind an ESC is that key [D:csi-u-keys]" {
            let key (tail: string) =
                match Weir.Repl.csiKey tail with
                | Some(n, Some k) -> Some(n, k.Key, k.KeyChar, k.Modifiers)
                | _ -> None

            let shift, alt, ctrl =
                System.ConsoleModifiers.Shift, System.ConsoleModifiers.Alt, System.ConsoleModifiers.Control

            let none = enum<System.ConsoleModifiers> 0

            Expect.equal (key "[9;2u") (Some(5, System.ConsoleKey.Tab, '\t', shift)) "Shift+Tab, kitty"
            Expect.equal (key "[27;2;9~") (Some(8, System.ConsoleKey.Tab, '\t', shift)) "Shift+Tab, modifyOtherKeys"
            Expect.equal (key "[27u") (Some(4, System.ConsoleKey.Escape, '\027', none)) "Esc"
            Expect.equal (key "[13;3u") (Some(6, System.ConsoleKey.Enter, '\r', alt)) "Alt+Enter"
            Expect.equal (key "[127u") (Some(5, System.ConsoleKey.Backspace, '\127', none)) "Backspace"
            Expect.equal (key "[99;5u") (Some(6, System.ConsoleKey.C, '\003', ctrl)) "Ctrl+C"
            Expect.equal (key "[97;2u") (Some(6, System.ConsoleKey.A, 'A', shift)) "Shift+a"
            Expect.equal (key "[9;2uX") (Some(5, System.ConsoleKey.Tab, '\t', shift)) "only the report is consumed"

            Expect.equal
                (key "[9:9;2:1u")
                (Some(9, System.ConsoleKey.Tab, '\t', shift))
                "alternate keys and a press event"

            // a release, or a key the editor has none for: consumed, no key
            Expect.equal (Weir.Repl.csiKey "[9;2:3u" |> Option.map fst) (Some 7) "a release is swallowed"
            Expect.isTrue ((Weir.Repl.csiKey "[9;2:3u" |> Option.bind snd).IsNone) "and is no key"
            Expect.isTrue ((Weir.Repl.csiKey "[57358u" |> Option.bind snd).IsNone) "Caps Lock is no key"

            // everything else is not this: F1's tails, an arrow, plain text
            for other in [ "[11~"; "[[A"; "OP"; "[A"; "[Z"; "abc"; ""; "[9;2"; "[u" ] do
                Expect.isNone (Weir.Repl.csiKey other) other
        }
        test "a paste becomes buffer lines [D:repl-bracketed-paste]" {
            let lines = Weir.Repl.pasteLines

            Expect.equal (lines "a\r    |> b\r") [ "a"; "    |> b" ] "terminal line ends, the last dropped"
            Expect.equal (lines "a\r\nb\n\n") [ "a"; "b" ] "CRLF and LF alike"
            Expect.equal (lines "a\n\nb") [ "a"; ""; "b" ] "an inner blank line stays"
            Expect.equal (lines "a\tb") [ "a    b" ] "a tab is four columns"
            Expect.equal (lines "a\027[31mb\007") [ "a[31mb" ] "no control character survives"
            Expect.equal (lines "") [ "" ] "an empty paste inserts nothing"
        }
        test "a session's continuationPrompt is the prompt's width whatever it says [D:continuation-prompt]" {
            let vis (s: string) = Weir.Repl.visibleWidthForTest s
            let fits = Weir.Repl.deriveContPromptOfForTest (Some "\x1b[90m>> \x1b[0m") 8
            Expect.equal (vis fits) 8 "shorter text is padded to the prompt's width"
            Expect.isTrue (fits.StartsWith "     ") "at the right, as the default mark is"
            Expect.stringContains fits "\x1b[90m>> " "its colour kept"

            Expect.equal
                (Weir.Repl.deriveContPromptOfForTest (Some "continued> ") 4)
                "cont"
                "longer text is cut to the width"

            let safe = Weir.Repl.deriveContPromptOfForTest (Some "a\x1b]0;title\x07\nb") 6
            Expect.isFalse (safe.Contains "title") "a title escape is dropped, as in the prompt"
            Expect.isFalse (safe.Contains "\n") "a newline is flattened"
            Expect.equal (vis safe) 6 "and the width holds"
            Expect.equal (Weir.Repl.deriveContPromptOfForTest (Some "x") 0) "" "no prompt, no continuation prompt"

            // a cut never splits a character the terminal draws as one, and
            // what is kept measures the prompt's width as a whole
            for own in [ "👨‍👩‍👧 abcdefgh"; "🇵🇱🇵🇱🇵🇱🇵🇱"; "❤️❤️❤️❤️❤️"; "日本語のプロンプト" ] do
                let got = Weir.Repl.deriveContPromptOfForTest (Some own) 6
                Expect.equal (vis got) 6 $"six cells: {own}"

            let flags = Weir.Repl.deriveContPromptOfForTest (Some "🇵🇱🇵🇱🇵🇱🇵🇱") 6

            Expect.isFalse
                (flags.TrimStart().Length % 4 <> 0)
                "whole flags only (two code points, four UTF-16 units each)"
        }
    ]

let tildeLiteralTests =
    // the line shows what runs [D:tilde-literal]: Tab completes a leading
    // `~` to the literal home path, and cd's string argument is data
    let te = preludeTypeEnv

    let suggestAt (text: string) =
        Weir.Complete.suggest te text (Weir.Complete.wordStartAt text text.Length)

    let home =
        System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile

    testList "literal home [D:tilde-literal]" [
        test "Tab expands a leading ~ to the home path — bare in argv, quoted in an expression" {
            skipOnWindows ()

            let dirs =
                System.IO.Directory.GetDirectories home
                |> Array.map System.IO.Path.GetFileName
                |> Array.filter (fun n -> not (n.StartsWith "."))
                |> Array.sort

            if dirs.Length > 0 then
                let d = dirs[0]
                let want = $"{home}/{d}/"
                Expect.contains (suggestAt $"cat ~/{d}") want "argv: the literal path, bare"
                Expect.contains (suggestAt $"let x = File.read ~/{d}") $"\"{want}\"" "expression: quoted"
                Expect.isFalse (suggestAt $"cat ~/{d}" |> List.exists (fun c -> c.StartsWith "~")) "no ~ survives"
        }
        test "cd's argument is data; bare cd still goes home" {
            skipOnWindows ()

            Expect.throws (fun () -> run "cd \"~/weir-no-such-dir\"" |> ignore) "a quoted ~ is a literal name, not home"

            let before = Weir.Session.Cwd()

            let runCmd line =
                match Weir.Parser.parseLine realResolver line with
                | Ok(SExpr e | SCmd e) ->
                    match typecheck env e with
                    | Ok t -> eval valueEnv t |> ignore
                    | Error terr -> failtest (formatError terr)
                | other -> failtest $"unexpected: {other}"

            try
                runCmd "cd"
                Expect.equal (Weir.Session.Cwd()) home "bare cd goes home"
            finally
                runCmd $"cd \"{before}\""
        }
    ]

let envPrefixTests =
    // bash's one-line child env [D:env-prefix]: NAME=value words before a
    // command head join that segment's overlay; argv `CC=gcc` and
    // conditions like `x==1` are untouched
    let shape line =
        match Weir.Parser.parseLineFull cmdResolver line with
        | Ok s -> Weir.Ast.sexprStmt s
        | Error f -> failtest $"parse failed: {f.Message}"

    let parseErr line =
        match Weir.Parser.parseLineFull cmdResolver line with
        | Error f -> f.Message
        | Ok s -> failtest $"expected a refusal, got {Weir.Ast.sexprStmt s}"

    testList "env prefix [D:env-prefix]" [
        test "an env-prefix value is one inert word for the colorizer [D:repl-color-words]" {
            let c =
                Weir.ReplColor.colorizeRepl (fun n -> n = "rooz") "ROOZ_ALLOW=dkr@22aed5c9f6 rooz -H lambda update"

            Expect.stringContains c "ROOZ_ALLOW=dkr@22aed5c9f6 " "the value carries no tint"
            Expect.isFalse (c.Contains "\x1b[31m") "nothing in it reads as a head that would fail"

            Expect.stringContains
                (Weir.ReplColor.colorizeRepl (fun n -> n = "rooz") "A=1 B=x2y rooz")
                "A=1 B=x2y "
                "each prefix's value"
        }
        test "prefixes become the segment's overlay" {
            Expect.equal
                (shape "EDITOR=nano git commit")
                "(scmd (cmdenv [((|envPair \"EDITOR\") \"nano\")] git \"commit\"))"
                "one"

            Expect.equal
                (shape "A=1 B= git")
                "(scmd (cmdenv [((|envPair \"A\") \"1\"); ((|envPair \"B\") \"\")] git ))"
                "several, and an empty value"

            Expect.equal (shape "A=$x git") "(scmd (cmdenv [((|envPair \"A\") x)] git ))" "a spliced value"

            Expect.equal
                (shape "A=\"two words\" git")
                "(scmd (cmdenv [((|envPair \"A\") \"two words\")] git ))"
                "a quoted value"
        }
        test "each chain stage takes its own prefix" {
            Expect.equal
                (shape "A=1 git log | B=2 grep x")
                "(scmd ((cmdenv [((|envPair \"A\") \"1\")] git \"log\") |> (cmdenv [((|envPair \"B\") \"2\")] grep \"x\")))"
                "per stage, as in bash"
        }
        test "a sigil env and a prefix compose, the prefix last" {
            Expect.stringContains
                (shape "let x = $e(A=1 git)")
                "(|seqAppend e) [((|envPair \"A\") \"1\")]"
                "sigil first, prefix appended"
        }
        test "after the head, NAME=value is argv; x==1 is a comparison" {
            Expect.equal (shape "git CC=gcc") "(scmd (cmd git \"CC=gcc\"))" "argv"
            Expect.stringContains (shape "let x = 1 in x==1") "(== x 1)" "never an env prefix"
        }
        test "a prefix with no command teaches" {
            Expect.stringContains (parseErr "FOO=1") "'FOO=…' is not an assignment" "top level"

            Expect.stringContains
                (parseErr "if true then FOO=1 else ()")
                "'FOO=…' is not an assignment"
                "a block statement"
        }
        test "the head slot looks through prefixes (tint and Tab agree)" {
            Expect.equal (Weir.Complete.headSlotAt "EDITOR=nano ") Weir.Complete.HeadSlot.Stmt "statement"

            Expect.equal
                (Weir.Complete.headSlotAt "let v = A=\"x y\" ")
                Weir.Complete.HeadSlot.LetRhs
                "let-RHS, quoted value"

            Expect.equal (Weir.Complete.headSlotAt "git ") Weir.Complete.HeadSlot.No "argv is not a head"
        }
    ]

let helpUxTests =
    testList "help glance + #find [D:help-glance] [D:help-find]" [
        test "(a) module blurbs are COMPLETE, two ways (the gen-lexical pattern)" {
            // every derived REPL module has a blurb (a new module without
            // one fails loud), and every blurb names a live module (a
            // retired module cannot leave a stale line behind)
            let mods = Weir.Builtins.typeEnv.Modules |> Map.keys |> Set.ofSeq
            let blurbed = Weir.Builtins.moduleBlurbs |> Map.keys |> Set.ofSeq

            Expect.equal
                (Set.difference mods blurbed)
                Set.empty
                "module(s) missing a blurb — add to Builtins.moduleBlurbs"

            Expect.equal
                (Set.difference blurbed mods)
                Set.empty
                "blurb(s) for retired module(s) — remove from Builtins.moduleBlurbs"

            for KeyValue(m, b) in Weir.Builtins.moduleBlurbs do
                Expect.isTrue (b.Trim() <> "") $"the blurb for {m} is non-empty"
        }
        test "(a2) user-rendered text carries NO ledger citations" {
            // a decision citation is the ledger's address space, not user vocabulary
            // — a doc string reaches #help/hover/#find verbatim, so a
            // citation there is archaeology leaking. Comments are the
            // citations' home; the docs are enumerable, so pin them all.
            for KeyValue(name, d) in Weir.Builtins.builtinDocs.Value do
                Expect.isFalse (d.Summary.Contains "[D:") $"{name}: summary cites the ledger"

                match d.Pointer with
                | Some p -> Expect.isFalse (p.Contains "[D:") $"{name}: pointer cites the ledger"
                | None -> ()

                match d.Example with
                | Some e -> Expect.isFalse (e.Contains "[D:") $"{name}: example cites the ledger"
                | None -> ()

            for KeyValue(m, b) in Weir.Builtins.moduleBlurbs do
                Expect.isFalse (b.Contains "[D:") $"{m}: blurb cites the ledger"
        }
        test "(a3) user-rendered prose carries no shouting caps" {
            // uppercase emphasis is not house style for user-facing text
            // (the a2 posture: docs are enumerable, so pin the class).
            // An all-caps word (3+ uppercase letters) in doc prose is
            // either rewritten in normal case or a real acronym — the
            // allowlist below, one line per addition with its reason.
            // Backtick spans are code quoting identifiers/syntax and are
            // exempt; so are underscore names (WEIR_LOG-style env vars).
            // Examples are executable code, not prose — out of scope.
            let allowed =
                Set [
                    "JSON" // wire formats
                    "YAML"
                    "TOML"
                    "XML"
                    "HTML"
                    "NDJSON" // newline-delimited JSON, `jsonl`'s long name
                    "UTF-8" // encodings and character notation
                    "U+FFFD" // the replacement character
                    "NUL" // the byte / device name
                    "ISO" // ISO 8601
                    "UTC"
                    "RFC" // RFC 10008
                    "SHA-256" // hash algorithm
                    "SHA-1" // Uuid.v5's hash, fixed by RFC 9562
                    "HMAC-SHA256" // keyed-hash mac, Bytes.hmacSha256's algorithm
                    "HTTP" // protocol + methods
                    "GET"
                    "QUERY"
                    "TCP"
                    "TLS" // HttpRequest's insecure: skip TLS verification
                    "URL"
                    "EOF" // end of file
                    "BOM" // byte-order mark, the File.write preservation
                    "FIFO" // Frontier.fold's queue discipline
                    "MIME" // MIME wrap (base64)
                    "POSIX"
                    "PATH" // the environment variable
                    "PATHEXT" // the Windows environment variable [D:path-which]
                    "REPL" // tool surfaces
                    "TRACE" // WEIR_LOG level names, as the lines print them
                    "DEBUG"
                    "INFO"
                    "WARN"
                ]

            let codeSpan = System.Text.RegularExpressions.Regex @"`[^`\n]+`"

            let capsWord =
                System.Text.RegularExpressions.Regex @"(?<![A-Za-z0-9])[A-Z][A-Z0-9_+-]+[A-Z0-9]"

            let offenders (where: string) (s: string) = [
                for m in capsWord.Matches(codeSpan.Replace(s, " ")) do
                    let t = m.Value
                    let uppers = t |> Seq.filter System.Char.IsUpper |> Seq.length

                    if uppers >= 3 && not (t.Contains "_") && not (allowed.Contains t) then
                        yield $"{where}: {t}"
            ]

            let hits = [
                for KeyValue(name, d) in Weir.Builtins.builtinDocs.Value do
                    yield! offenders $"{name} summary" d.Summary

                    match d.Pointer with
                    | Some p -> yield! offenders $"{name} pointer" p
                    | None -> ()
                for KeyValue(m, b) in Weir.Builtins.moduleBlurbs do
                    yield! offenders $"{m} blurb" b
                for KeyValue(a, doc) in Weir.Builtins.attrDocs do
                    yield! offenders $"[<{a}>] doc" doc
                yield! offenders "bare #help" (Weir.Repl.helpTextForTest "")
            ]

            Expect.equal hits [] "shouting caps in user-facing prose — rewrite, or allowlist a real acronym"
        }
        test "#help on a bare alias answers as its qualified member, named [D:bare-partition]" {
            let t = Weir.Repl.helpTextForTest "map"
            Expect.stringContains t "map is Seq.map" "names the member it aliases"
            Expect.stringContains t "Apply a function" "with Seq.map's own doc"
        }
        test "#help on a two-home name lists its homes, never 'unknown name' [D:bare-partition]" {
            Expect.equal
                (Weir.Repl.helpTextForTest "contains")
                "'contains' is module-qualified here; use Seq.contains or Str.contains"
                "contains"

            Expect.equal
                (Weir.Repl.helpTextForTest "length")
                "'length' is module-qualified here; use Seq.length or Str.length"
                "length"
        }
        test "(b) #help Module: ONE member per line, name + the doc's first line" {
            let t = Weir.Repl.helpTextForTest "Option"
            let lines = t.Split '\n'

            Expect.isTrue
                (lines
                 |> Array.exists (fun l -> l.StartsWith "  bind " && l.Contains "Apply a function"))
                $"the bind row carries its glance: {t}"

            Expect.isTrue
                (lines
                 |> Array.exists (fun l -> l.StartsWith "  defaultValue " && l.Contains "The Some value"))
                "the defaultValue row carries its glance"

            let memberRows = lines |> Array.filter (fun l -> l.StartsWith "  ") |> Array.length
            Expect.equal memberRows 8 "one row per member, exactly"
        }
        test "(c) bare #help is the directives alone; #help modules lists the modules with blurbs [D:help-modules]" {
            let t = Weir.Repl.helpTextForTest ""
            Expect.stringContains t "#find" "#find is in the directive list"
            Expect.stringContains t "#help modules" "the directive list points at the modules"
            Expect.isFalse (t.Contains "Modules:") "no module list under the directives"

            let m = Weir.Repl.helpTextForTest "modules"

            Expect.isTrue
                (m.Split '\n'
                 |> Array.exists (fun l -> l.TrimStart().StartsWith "Seq " && l.Contains "lazy sequence"))
                $"the Seq line carries its blurb: {m}"

            Expect.isFalse (m.Contains "Imported:") "no imports, no Imported section"
        }
        test "a bare module name echoes its help when it fits, else the member grid [D:module-echo]" {
            let full =
                "Bytes (2 members):\n  a  doc\n  b  doc\n\n#help Bytes.<member> shows one member's doc"

            Expect.equal (Weir.Repl.moduleEchoText full (Some 24) 80 "Bytes" [ "a"; "b" ]) full "fits: the #help text"
            Expect.equal (Weir.Repl.moduleEchoText full None 80 "Bytes" [ "a"; "b" ]) full "no terminal: the #help text"

            Expect.equal
                (Weir.Repl.moduleEchoText full (Some 5) 80 "Bytes" [ "a"; "b" ])
                "Bytes (2 members):\n  a  b\n\n#help Bytes for descriptions, #help Bytes.<member> for one"
                "too tall: names only, and where the descriptions are"
        }
        test "Reply builds the ordinary response record [D:reply]" {
            let body src =
                run
                    $"match ({src}).body with | Text t -> t | NoBody -> \"<none>\" | RawJson ls -> Str.join \"\" ls | _ -> \"?\""

            Expect.equal (run "(Reply.text 201 \"made\").status") (VInt 201L) "text status"
            Expect.equal (body "Reply.text 201 \"made\"") (VStr "made") "text body"
            Expect.equal (body "Reply.empty 404") (VStr "<none>") "empty"
            // the value renders at the call site [D:json-value]
            Expect.equal (body "Reply.json 200 {| ok = 10 |}") (VStr "{\"ok\":10}") "a value, applied"
            Expect.equal (body "[ \"a\" ] |> Reply.json 200") (VStr "[\"a\"]") "a value, piped"
            Expect.equal (body "[1; 2] |> to json |> Reply.rawJson 200") (VStr "[1,2]") "rendered lines"

            Expect.stringContains
                (checkErr "Reply.json 200 (fun x -> x + 1)").Message
                "is not admitted"
                "the jsonable law, at the call site"

            Expect.stringContains (checkErr "Reply.json 200").Message "takes the value" "no function value"

            // rendered lines are a value like any other [D:json-plain]
            Expect.equal
                (body "Reply.json 200 ([ 1 ] |> to json)")
                (VStr "[\"[1]\"]")
                "lines given to json are an array of strings"

            // the hover names the member written, not the runtime one
            match Weir.Lsp.hoverAt "/tmp/h.weir" [ "let r = Reply.json 200 [ 1 ]"; "print r.status" ] 1 16 with
            | Some h ->
                Expect.stringContains
                    h
                    "Reply.json (status: int) (value: seq<int>) : HttpServerResponse"
                    "Reply.json's own signature, at the value's type"

                Expect.isFalse (h.Contains "rawJson") "never the raw member's doc"
            | None -> failtest "Reply.json must hover"

            // [D:reply-members]
            let headers src =
                run $"({src}).headers |> Seq.map (fun (k, v) -> k + \"=\" + v) |> Str.join \";\""

            Expect.equal
                (headers "Reply.html 200 \"<p>\" |> Reply.header \"a\" \"b\"")
                (VStr "content-type=text/html; charset=utf-8;a=b")
                "html types itself; header appends"

            Expect.equal (body "Reply.html 200 \"<p>\"") (VStr "<p>") "html body"
            Expect.equal (headers "Reply.redirect 302 \"/login\"") (VStr "location=/login") "redirect"
            Expect.equal (body "Reply.redirect 302 \"/login\"") (VStr "<none>") "redirect has no body"

            Expect.equal
                (run "match ([ \"a\"; \"b\" ] |> Reply.stream 200).body with | Stream xs -> Seq.length xs | _ -> 0")
                (VInt 2L)
                "stream"

            Expect.equal (run "(Reply.empty 404).headers |> Seq.length") (VInt 0L) "no headers"

            Expect.equal
                (run "({ Reply.text 200 \"ok\" with headers = [(\"a\", \"b\")] }).headers |> Seq.length")
                (VInt 1L)
                "headers are a record update"
        }
        test "the /// run above the module line is the module's doc [D:module-doc]" {
            Expect.equal
                (Weir.Assemble.moduleDocOf [
                    "/// Topic pub/sub."
                    "/// Call init first."
                    "module Flume"
                    ""
                    "let x = 1"
                ])
                [ "Topic pub/sub."; "Call init first." ]
                "the contiguous run"

            Expect.equal
                (Weir.Assemble.moduleDocOf [ "/// stray"; ""; "module Flume" ])
                []
                "a blank line breaks the run"

            Expect.equal
                (Weir.Assemble.moduleDocOf [ "module Flume"; "/// doubles"; "let twice : int -> int" ])
                []
                "a member's doc is not the module's"
        }
        test "(d) #find fallback: case-insensitive substring, deterministic (the piped path)" {
            let t = Weir.Repl.findFallbackForTest "SHA256"
            Expect.stringContains t "Str.sha256" "matches by member name, case-insensitively"
            Expect.stringContains t "File.sha256" "all homes surface"
            Expect.stringContains t "Bytes.sha256" "all homes surface (Bytes)"
            Expect.stringContains (Weir.Repl.findFallbackForTest "zzznotathing") "no matches" "a miss says so"
            Expect.stringContains (Weir.Repl.findFallbackForTest "") "#find <query>" "bare #find teaches usage"
        }
        test "(e) the candidate lines are the one help source: modules + members" {
            let cs = Weir.Repl.findCandidatesForTest ()

            Expect.contains cs "Option — presence and absence: map, bind, defaults" "a module line is name — blurb"

            Expect.isTrue
                (cs |> List.exists (fun l -> l.StartsWith "Option.defaultValue — "))
                "a member line is Module.member — glance"
        }
        test "(f) the headless doc render prints the exact #help text (one source)" {
            Expect.equal (Weir.Repl.replDocText "Option") (Weir.Repl.helpTextForTest "Option") "same bytes"
            Expect.equal (Weir.Repl.replDocText "Seq.collect") (Weir.Repl.helpTextForTest "Seq.collect") "member too"
        }
        test "(e2) #find lists types too, the ones #help documents [D:find-types]" {
            let cs = Weir.Repl.findCandidatesForTest ()

            for t in
                [
                    "HttpServerResponse"
                    "HttpServerRequest"
                    "ServerConfig"
                    "Completed"
                    "FileRow"
                ] do
                Expect.isTrue (cs |> List.exists (fun l -> l.StartsWith(t + " — "))) $"{t} is a #find candidate"

            Expect.isTrue
                (cs |> List.exists (fun l -> l.StartsWith "Completed — A finished command"))
                "a documented type carries its glance"

            Expect.stringContains
                (Weir.Repl.findFallbackForTest "HttpServer")
                "HttpServerResponse"
                "the fallback finds it"

            // every prelude type explains itself [D:prelude-type-docs]
            let bare = cs |> List.filter (fun l -> l.EndsWith " — type")
            Expect.isEmpty bare "a type without a description"
        }
        test "(g) #find Tab-completes: sessionDirectives carries it" {
            Expect.contains Weir.Complete.sessionDirectives "find" "find is a session directive"
        }
        test "(h) tty help render [D:help-tint]: code spans tint, backticks drop" {
            let t = Weir.Repl.renderHelpText true "run `Seq.map` then `ls`"
            Expect.equal t "run \x1b[36mSeq.map\x1b[0m then \x1b[36mls\x1b[0m" "both spans tint, no backticks"

            // an unpaired backtick is not a span — left literal, never eaten
            Expect.equal (Weir.Repl.renderHelpText true "a ` stray") "a ` stray" "stray backtick untouched"

            // a span never crosses a line — the two halves stay literal
            Expect.equal (Weir.Repl.renderHelpText true "a`\nb`c") "a`\nb`c" "no cross-line span"
        }
        test "(i) piped/stripped help keeps the literal backticks (the pinned bytes)" {
            // color off (piped, NO_COLOR, TERM=dumb) is the identity:
            // the doc bytes are the pinned surface, backticks included —
            // when the tint is stripped, the span boundary must survive
            let doc = Weir.Repl.replDocText "Yaml.merge"
            Expect.equal (Weir.Repl.renderHelpText false doc) doc "color off is byte-identity"
            Expect.stringContains doc "`yaml patch`" "the literal span spelling is the piped surface"
            Expect.isFalse (doc.Contains "\x1b") "no ANSI in the piped bytes"
        }
        test "(j) tty signature tints structurally [D:help-tint]: name bold, types yellow, punctuation dim" {
            let t = Weir.Repl.helpTintedForTest "Yaml.inferShape"

            Expect.equal
                (t.Split '\n').[0]
                ("\x1b[1mYaml.inferShape\x1b[0m \x1b[2m(\x1b[0mlines\x1b[2m: \x1b[0m\x1b[33mseq<string>\x1b[0m\x1b[2m)\x1b[0m\x1b[2m : \x1b[0m\x1b[33mstring\x1b[0m")
                "the composed signature carries the input colorizer's palette"

            // the style is the colorizer's palette — Color functions, not
            // restated codes; a hardcoded escape here would let them drift
            Expect.equal (Weir.Types.sigTintStyle.Name "n") (Weir.Types.Color.bold true "n") "name = the head tint"

            Expect.equal
                (Weir.Types.sigTintStyle.Ty "t")
                (Weir.Types.Color.yellow true "t")
                "types = the casing-law tint"

            Expect.equal (Weir.Types.sigTintStyle.Punct "p") (Weir.Types.Color.dim true "p") "punctuation = dim"
        }
        test "(k) the example block IS the input colorizer's render [D:help-tint] — one brain" {
            let ex = Weir.Builtins.builtinDocs.Value["Yaml.inferShape"].Example |> Option.get

            let expected =
                ex.Split '\n'
                |> Array.map (Weir.ReplColor.colorizeRepl Weir.Repl.knownForTest)
                |> String.concat "\n"

            Expect.stringContains
                (Weir.Repl.helpTintedForTest "Yaml.inferShape")
                expected
                "the tty example is colorizeRepl's own output, line for line"

            Expect.isTrue (expected.Contains "\x1b[") "the pin is vacuous unless the example actually tints"
        }
        test "(l) piped signature and example carry zero ANSI (the pinned bytes)" {
            let doc = Weir.Repl.replDocText "Yaml.inferShape"
            Expect.stringContains doc "Yaml.inferShape (lines: seq<string>) : string" "the plain signature spelling"
            Expect.stringContains doc "print (Yaml.inferShape sample)" "the plain example spelling"
            Expect.isFalse (doc.Contains "\x1b") "no ANSI in the piped bytes"
        }
    ]

let sweepFindingTests =
    let msgs file lines =
        diagsIn file lines |> List.map (fun d -> d.Message)

    testList "sweep findings [D:sweep-findings]" [
        test "[<Positional>] on an Args.load field is refused, not silently inert" {
            let m =
                msgs "p.weir" [
                    "type Cli = {"
                    "    [<Positional>]"
                    "    target: string"
                    "}"
                    "let cli = Args.load Cli"
                    "print cli.target"
                ]

            Expect.exists m (fun s -> s.Contains "has [<Positional>], which only describes a tool's operands") "refused"
        }
        // B31: `--help` belongs to the usage text
        test "an Args.load field deriving --help is refused as reserved" {
            let m =
                msgs "h.weir" [
                    "type Cli = { help: bool; name: string }"
                    "let cli = Args.load Cli"
                    "print cli.name"
                ]

            Expect.exists m (fun s -> s.Contains "'help' would derive '--help', which is reserved") "refused"
        }
        test "a command inside plan says commands are refused, not proc" {
            let m =
                msgs "p.weir" [ "let p = plan"; "    echo hi"; "print (Plan.preview p |> Str.join \"\")" ]

            Expect.exists m (fun s -> s.Contains "commands are refused inside 'plan'") "the message"
            Expect.isFalse (m |> List.exists (fun s -> s.Contains "'proc' is refused")) "no proc wording"
        }
        test "an unknown qualifier suggests modules, never a constructor" {
            let m = msgs "q.weir" [ "M.f ()" ]
            Expect.exists m (fun s -> s.Contains "no module named 'M'") "names the module"
            Expect.isFalse (m |> List.exists (fun s -> s.Contains "'Ok'")) "no constructor suggestion"

            Expect.exists
                (msgs "q.weir" [ "print (List.length [1])" ])
                (fun s -> s.Contains "'Seq'")
                "the List teaching still leads"
        }
        test "the removed `!` is never offered as an expected token" {
            let m = msgs "w.weir" [ "within readonly"; "    print \"x\"" ]
            Expect.isNonEmpty m "a parse error"
            Expect.isFalse (m |> List.exists (fun s -> s.Contains "'!'")) "no '!' in the expectation list"
        }
    ]

// signature-driven argument completion [D:sig-arg-complete]: the shared
// completer over sigs `weir add sig` generated from the tests/sigs samples
// (snapshots in tests/fixtures/sigs — regenerate with exe/ first on PATH:
// `weir add sig <sample>`), one per help format
let sigCompleteTests =
    let fixtureDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", "sigs")

    let loadAt (path: string) (tool: string) =
        match
            Weir.Sigs.loadSigs (path + ".from") [
                {
                    Tool = tool
                    Override = Some path
                    Line = 1
                }
            ]
        with
        | [], [ si ] -> si
        | ds, _ -> failtest $"{tool}: sig did not load: {ds}"

    let fx tool =
        loadAt (Path.Combine(fixtureDir, tool + ".weir")) tool

    // complete at the end of `<tool><argText>`, the editor's word rule
    let at (si: Weir.Sigs.SigInfo) (argText: string) =
        let ws = Weir.Complete.wordStartAt argText argText.Length
        Weir.Sigs.SigComplete.complete si argText (argText.Substring ws)

    let words si argText =
        match at si argText with
        | Weir.Sigs.SigWords ws -> ws
        | other -> failtest $"'{argText}': expected words, got {other}"

    let isPaths si argText =
        Expect.equal (at si argText) Weir.Sigs.SigPaths $"'{argText}' defers to paths"

    let isNothing si argText =
        Expect.equal (at si argText) Weir.Sigs.SigNothing $"'{argText}' offers nothing"

    let has (xs: string list) (ys: string list) =
        for y in ys do
            Expect.contains xs y $"offers {y} in {xs}"

    let lacks (xs: string list) (ys: string list) =
        for y in ys do
            Expect.isFalse (List.contains y xs) $"never {y} in {xs}"

    testList "sig argument completion" [
        test "git: subcommand words, by case, at every depth" {
            let g = fx "gitfx"
            Expect.equal (words g " ") [ "commit"; "log"; "remote"; "stash"; "status" ] "top"
            Expect.equal (words g " com") [ "commit" ] "prefix"
            Expect.equal (words g " st") [ "stash"; "status" ] "two"
            // a lone nested case is one word: `remote set-head`, never `set`
            Expect.equal (words g " remote ") [ "add"; "set-head" ] "nested"
            Expect.equal (words g " stash p") [ "pop"; "push" ] "nested prefix"
            // a global's value word is stepped over
            Expect.equal (words g " -C /tmp st") [ "stash"; "status" ] "after -C <path>"
        }
        test "git: a case's flags — longs, shorts, the case only" {
            let g = fx "gitfx"
            Expect.equal (words g " commit --am") [ "--amend" ] "--am"
            let dash = words g " commit -"
            has dash [ "-a"; "-m"; "-F"; "--amend"; "--message"; "--no-verify"; "--git-dir"; "-C" ]
            lacks dash [ "--index"; "--keep-index"; "--porcelain" ]
            // the top level: the Globals
            let top = words g " -"
            has top [ "-C"; "--git-dir"; "--bare"; "--work-tree" ]
            lacks top [ "--amend"; "--porcelain" ]
            has (words g " status --") [ "--porcelain"; "--short"; "--bare" ]
        }
        test "git: values by arity — path-ish, else nothing" {
            let g = fx "gitfx"
            // -F/--file: `read message from file`
            isPaths g " commit -F "
            isPaths g " commit --file="
            isNothing g " commit -m "
            isNothing g " commit --message="
            isNothing g " commit --author "
            // a switch takes no value: the next word is a subcommand/operand
            isPaths g " commit --amend "
            // -am: a bundle whose last letter takes the next word
            isNothing g " commit -am "
        }
        test "git: unchecked case, unknown subcommand, -- : paths only" {
            let g = fx "gitfx"
            isPaths g " log --"
            isPaths g " log -"
            isPaths g " stash show -"
            isPaths g " frobnicate --"
            isPaths g " commit -- "
            isPaths g " commit -- --am"
        }
        test "cobra: grouped words, stopAtOperand, globals in a case" {
            let c = fx "cobrafx"
            Expect.equal (words c " ") [ "compose"; "container"; "ls"; "pr"; "ps"; "run" ] "top"
            Expect.equal (words c " container ") [ "ls"; "run" ] "group"
            Expect.equal (words c " pr ") [ "create"; "new"; "view" ] "pr"
            has (words c " run -") [ "-d"; "--detach"; "--name"; "--context"; "-H" ]
            isPaths c " run --name x img -"
            isPaths c " container run img --"
            isPaths c " compose -f "
            isNothing c " run --name "
        }
        test "kill: abbreviations offer full names; a dash word is not a flag" {
            let k = fx "killfx"
            Expect.equal (words k " --sig") [ "--signal" ] "full name"
            has (words k " -") [ "-l"; "-s"; "-L"; "--list"; "--signal" ]
            Expect.equal (words k " -TE") [] "a -<signal> word"
        }
        test "find: predicates are single-dash words" {
            let f = fx "findfx"
            Expect.equal (words f " . -na") [ "-name" ] "-na"
            has (words f " -") [ "-name"; "-newer"; "-print0"; "-H"; "--help" ]
            // -exec takes the rest: its first word, then paths
            isNothing f " . -exec "
            isPaths f " . -exec rm -"
        }
        test "go: single-dash longs, doubled on --" {
            let g = fx "gofx"
            Expect.equal (words g " -im") [ "-import-path" ] "-im"
            Expect.equal (words g " --im") [ "--import-path" ] "--im"
            isPaths g " -import-path "
            isNothing g " -max-time "
        }
        test "node: --no-x when negatable; the script ends node's options" {
            let n = fx "nodefx"
            has (words n " --no-") [ "--no-warnings"; "--no-trace-warnings"; "--no-test" ]
            lacks (words n " --no-") [ "--no-eval"; "--no-no-warnings" ]
            // a bare -- lists the real flags only
            let bare = words n " --"
            has bare [ "--warnings"; "--no-warnings"; "--trace-warnings" ]
            lacks bare [ "--no-trace-warnings"; "--no-test" ]
            lacks (words n " --no") [ "--no-trace-warnings" ]
            isNothing n " --input-type "
            isPaths n " script.js -"
        }
        test "clap and fish: a Wire long, a seq value" {
            let c = fx "clapfx"
            has (words c " --t") [ "--type" ]
            isNothing c " -x "
            isPaths c " -x ls -"
            let f = fx "fishfx"
            has (words f " -") [ "-A"; "-o"; "--after-context"; "-plain" ]
        }
        test "choices from a bare-case union; --flag= completes the value part" {
            withTempDir "weir-sigch" (fun d ->
                let p = Path.Combine(d, "chtool.weir")

                File.WriteAllLines(
                    p,
                    [
                        "module Chtool"
                        "type Color = | Always | Never | Auto"
                        "type Mode = | Fast | SlowMotion"
                        "type Cmd = {"
                        "    color: Option<Color>"
                        "    mode: Mode"
                        "    /// read settings from file"
                        "    config: string"
                        "    /// a note"
                        "    note: string"
                        "}"
                    ]
                )

                let si = loadAt p "chtool"
                Expect.equal (words si " --color=a") [ "always"; "auto" ] "attached value"
                Expect.equal (words si " --color=") [ "always"; "auto"; "never" ] "all"
                Expect.equal (words si " --mode ") [ "fast"; "slow-motion" ] "next word"
                Expect.equal (words si " --mode s") [ "slow-motion" ] "prefix"
                // Option takes an attached value only
                isPaths si " --color "
                // the doc names a file; a plain value offers nothing
                isPaths si " --config "
                isNothing si " --note ")
        }
        test "an untrusted sig's spellings: only bare words are offered, and -= never throws [D:sig-arg-complete]" {
            withTempDir "weir-sigsp" (fun d ->
                let p = Path.Combine(d, "sptool.weir")

                File.WriteAllLines(
                    p,
                    [
                        "module Sptool"
                        "let dashWords = true"
                        "type Cmd = {"
                        "    [<Wire \"ok-flag\">] ok: bool"
                        "    [<Wire \"x y\">] spaced: bool"
                        "    [<Wire \"q\\\"uote\">] quoted: bool"
                        "    [<Wire \"nl\\nprint 1\">] newline: bool"
                        "}"
                    ]
                )

                let si = loadAt p "sptool"
                let offered = words si " --"
                Expect.contains offered "--ok-flag" "a bare word is offered"

                Expect.isFalse
                    (offered
                     |> List.exists (fun w ->
                         w
                         |> Seq.exists (fun c -> System.Char.IsWhiteSpace c || System.Char.IsControl c || c = '"')))
                    $"no spaced/quoted/control spelling: {offered}"
                // a one-character flag name under dashWords
                at si " -=" |> ignore
                at si " -= " |> ignore)
        }
        test "lookup: project sigs, then the user dir; mtime-keyed reload" {
            withTempDir "weir-siglk" (fun root ->
                let proj = Path.Combine(root, "proj")
                let user = Path.Combine(root, "user")
                Directory.CreateDirectory(Path.Combine(proj, ".git")) |> ignore
                Directory.CreateDirectory(Path.Combine(proj, ".weir", "sigs")) |> ignore
                Directory.CreateDirectory user |> ignore
                let sub = Path.Combine(proj, "deep", "er")
                Directory.CreateDirectory sub |> ignore

                let sigOf (flag: string) = [ "module Lk"; "type Cmd = {"; $"    {flag}: bool"; "}" ]

                let longs () =
                    match Weir.Sigs.SigLookup.findIn sub user "lktool" with
                    | Some si -> si.Surfaces[""].Longs |> Map.keys |> List.ofSeq
                    | None -> []

                Expect.equal (longs ()) [] "nothing found"
                File.WriteAllLines(Path.Combine(user, "lktool.weir"), sigOf "fromUser")
                Expect.equal (longs ()) [ "from-user" ] "the user dir"
                let projSig = Path.Combine(proj, ".weir", "sigs", "lktool.weir")
                File.WriteAllLines(projSig, sigOf "fromProject")
                Expect.equal (longs ()) [ "from-project" ] "the project wins"
                File.WriteAllLines(projSig, sigOf "edited")
                File.SetLastWriteTimeUtc(projSig, System.DateTime.UtcNow.AddMinutes 1.0)
                Expect.equal (longs ()) [ "edited" ] "a new mtime reloads"
                // a sig that does not load falls through to the user dir
                File.WriteAllLines(projSig, [ "module Lk"; "let x =" ])
                File.SetLastWriteTimeUtc(projSig, System.DateTime.UtcNow.AddMinutes 2.0)
                Expect.equal (longs ()) [ "from-user" ] "a broken project sig")
        }
        test "the argv slot: head and args through env prefixes, ^, let, stages" {
            let at before = Weir.Complete.commandArgvAt env before
            Expect.equal (at "gitfx ") (Some("gitfx", " ")) "bare"
            Expect.equal (at "gitfx commit --") (Some("gitfx", " commit --")) "args"
            Expect.equal (at "FOO=1.2 gitfx ") (Some("gitfx", " ")) "env prefix"
            Expect.equal (at "^gitfx ") (Some("^gitfx", " ")) "forced, the sigil kept"
            Expect.equal (at "let r = gitfx ") (Some("gitfx", " ")) "let RHS"
            Expect.equal (at "ls | gitfx ") (Some("gitfx", " ")) "stage"
            Expect.equal (at "gitfx") None "the head itself"
            Expect.equal (at "xs |> gitfx ") None "a |> pipeline"
        }
        test "REPL Tab: a sig'd head completes from the session cwd's project" {
            withTempDir "weir-sigtab" (fun d ->
                Directory.CreateDirectory(Path.Combine(d, ".git")) |> ignore
                Directory.CreateDirectory(Path.Combine(d, ".weir", "sigs")) |> ignore
                File.Copy(Path.Combine(fixtureDir, "gitfx.weir"), Path.Combine(d, ".weir", "sigs", "gitfx.weir"))

                inCwd d (fun () ->
                    let tabWith aliases (line: string) =
                        Weir.Sigs.suggestEntry
                            aliases
                            Map.empty
                            env
                            [ line ]
                            line
                            (Weir.Complete.wordStartAt line line.Length)

                    let tab = tabWith Map.empty

                    Expect.equal (tab "gitfx com") [ "commit" ] "subcommand"
                    Expect.equal (tab "gitfx commit --am") [ "--amend" ] "flag"
                    Expect.equal (tab "let r = gitfx commit --am") [ "--amend" ] "let RHS"
                    Expect.equal (tab "GIT_X=1 gitfx st") [ "stash"; "status" ] "env prefix"
                    Expect.equal (tab "gitfx commit -m ") [] "a message offers nothing"
                    Expect.equal (tab "^gitfx com") [ "commit" ] "forced"
                    // a session alias expands first [D:command-head-alias]:
                    // its exe's sig, its prefix words as typed
                    let aliases =
                        Map [
                            "co", ("gitfx", [ "commit" ])
                            "g", ("gitfx", [])
                            "ed", ("nosigtool", [ "-x" ])
                        ]

                    Expect.equal (tabWith aliases "co --am") [ "--amend" ] "alias with a prefix"
                    Expect.contains (tabWith aliases "co -") "-m" "the prefix's case flags"
                    Expect.isFalse (List.contains "--keep-index" (tabWith aliases "co -")) "never another case's"
                    Expect.equal (tabWith aliases "g st") [ "stash"; "status" ] "alias without a prefix"
                    Expect.equal (tabWith aliases "^co com") [] "^ skips the alias table"
                    // no sig for the head: today's path completion
                    File.WriteAllText(Path.Combine(d, "commit-notes.txt"), "")
                    Expect.equal (tab "nosigtool comm") [ "commit-notes.txt" ] "paths"

                    Expect.equal
                        (tabWith aliases "ed comm")
                        [ "commit-notes.txt" ]
                        "an alias to a sig-less tool: paths"))
        }
    ]

// dynamic values from the tool itself [D:sig-dynamic-complete]: the cobra
// answer parser, git's fixed position table, the trust rule, the
// `let dynamic` setting, the composition over fake hooks, the bounded
// child, and `add sig` writing the line
let sigDynamicTests =
    let sampleDir = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "sigs"))

    let loadAt (path: string) (tool: string) =
        Weir.Sigs.loadSigs (path + ".from") [
            {
                Tool = tool
                Override = Some path
                Line = 1
            }
        ]

    let sigText (dynamicLine: string) = [
        "module Dy"
        dynamicLine
        "type Globals = {"
        "    verbose: bool"
        "    /// read the config from file"
        "    config: string"
        "    name: string"
        "}"
        "type Cmd ="
        "    | Get"
        "    | Config"
        "    | ConfigView"
    ]

    let answer cs nf = Weir.DynComplete.Answer(cs, nf)

    // a fake runner: every request recorded, one canned outcome
    let fakeHooks (outcome: Weir.DynComplete.Outcome) =
        let asked = ResizeArray<Weir.DynComplete.Request>()
        let notes = ResizeArray<string>()

        let hooks: Weir.DynComplete.Hooks = {
            Run =
                fun r ->
                    asked.Add r
                    outcome
            Note = notes.Add
        }

        hooks, asked, notes

    let tabIn hooks user aliases (line: string) =
        Weir.Sigs.sigArgvCompleteWith (Some hooks) user aliases env line (Weir.Complete.wordStartAt line line.Length)

    testSequenced
    <| testList "sig dynamic completion" [
        test "cobra: candidates, descriptions, directives, CRLF, a malformed last line" {
            let p = Weir.DynComplete.parseCobra
            Expect.equal (p "web-1\tRunning\nweb-2\tPending\n:4\n") (answer [ "web-1"; "web-2" ] true) "NoFileComp"
            Expect.equal (p "a\r\nb\tdoc\r\n:0\r\n") (answer [ "a"; "b" ] false) "CRLF, default"
            Expect.equal (p "a\n:2\n") (answer [ "a" ] false) "NoSpace changes nothing"
            Expect.equal (p "a\n:1\n") (answer [] true) "Error: nothing at all"
            Expect.equal (p "yaml\n:8\n") Weir.DynComplete.Fallback "FilterFileExt: paths"
            Expect.equal (p ":16\n") Weir.DynComplete.Fallback "FilterDirs: paths"
            Expect.equal (p "a\nb\n") Weir.DynComplete.Fallback "no directive line"
            Expect.equal (p "a\n:x\n") Weir.DynComplete.Fallback "a directive that is no number"
            Expect.equal (p "a\n:-3\n") Weir.DynComplete.Fallback "a negative directive"
            Expect.equal (p "") Weir.DynComplete.Fallback "empty"
            Expect.equal (p "_activeHelp_ pick a pod\npod-a\n:4") (answer [ "pod-a" ] true) "active help is prose"
            Expect.equal (p "\tdoc only\n:0") (answer [] false) "an empty candidate"
        }
        test "git: the fixed position table" {
            let w (s: string) =
                Weir.DynComplete.gitWant (s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

            let open' = Weir.DynComplete.Refs
            Expect.equal (w "checkout") (Some open') "checkout"
            Expect.equal (w "checkout -b feat") (Some open') "a start point"
            Expect.equal (w "switch") (Some open') "switch"
            Expect.equal (w "log main") (Some open') "log, any operand"

            for s in [ "merge"; "rebase"; "diff"; "reset"; "cherry-pick"; "revert"; "show" ] do
                Expect.equal (w s) (Some open') s

            Expect.equal (w "branch") None "branch makes a name"
            Expect.equal (w "branch -d") (Some Weir.DynComplete.Heads) "branch -d"
            Expect.equal (w "branch -D x") (Some Weir.DynComplete.Heads) "branch -D, more"
            Expect.equal (w "branch -m") (Some Weir.DynComplete.Heads) "rename: the old name"
            Expect.equal (w "branch -m old") None "rename: the new name"
            Expect.equal (w "tag -d") (Some Weir.DynComplete.TagNames) "tag -d"
            Expect.equal (w "tag") None "tag makes a name"
            Expect.equal (w "push") (Some Weir.DynComplete.Remotes) "push: a remote"
            Expect.equal (w "fetch") (Some Weir.DynComplete.Remotes) "fetch: a remote"
            Expect.equal (w "pull -r") (Some Weir.DynComplete.Remotes) "pull: a remote past a flag"
            Expect.equal (w "push origin") (Some Weir.DynComplete.Heads) "push: then a branch"
            Expect.equal (w "fetch origin") None "fetch: nothing more"
            Expect.equal (w "remote") None "remote: its own subcommand"
            Expect.equal (w "remote remove") (Some Weir.DynComplete.Remotes) "remote remove"
            Expect.equal (w "remote set-url") (Some Weir.DynComplete.Remotes) "remote set-url"
            Expect.equal (w "remote add") None "remote add names a new one"
            Expect.equal (w "checkout --") None "a typed -- is paths"
            Expect.equal (w "-C /x checkout") None "-C moves the repository"
            Expect.equal (w "-c a=b checkout") None "-c can run anything"
            Expect.equal (w "--git-dir=/x log") None "--git-dir"
            Expect.equal (w "--no-pager log") (Some open') "a harmless global"
            Expect.equal (w "") None "no subcommand yet"
            Expect.equal (w "commit") None "commit"

            Expect.equal
                (Weir.DynComplete.gitArgs Weir.DynComplete.Refs)
                [
                    "for-each-ref"
                    "--format=%(refname:short)"
                    "refs/heads"
                    "refs/remotes"
                    "refs/tags"
                ]
                "the fixed ref listing"

            Expect.equal (Weir.DynComplete.gitArgs Weir.DynComplete.Remotes) [ "remote" ] "the remote listing"

            Expect.equal
                (Weir.DynComplete.parseGit "main\norigin/HEAD\norigin/main\r\n\n")
                (answer [ "main"; "origin/main" ] false)
                "a remote's HEAD is no ref"
        }
        test "the dynamic setting: git, cobra, absent; anything else is a load error" {
            withTempDir "weir-dyset" (fun d ->
                let load (line: string) =
                    let p = Path.Combine(d, "dy.weir")
                    File.WriteAllLines(p, sigText line)
                    loadAt p "dy"

                for m in [ "git"; "cobra" ] do
                    match load $"let dynamic = \"{m}\"" with
                    | [], [ si ] -> Expect.equal si.Dynamic m m
                    | ds, _ -> failtest $"{m}: {ds}"

                match load "" with
                | [], [ si ] -> Expect.equal si.Dynamic "" "absent"
                | ds, _ -> failtest $"absent: {ds}"

                for bad, says in
                    [
                        "let dynamic = \"bash\"", "names no completion mechanism"
                        "let dynamic = \"\"", "names no completion mechanism"
                        "let dynamic = true", "takes a mechanism's name"
                    ] do
                    match load bad with
                    | [ dg ], [ si ] ->
                        Expect.stringContains dg.Message says bad
                        Expect.stringContains dg.Message "\"git\" or \"cobra\"" bad
                        Expect.equal si.Dynamic "" $"{bad}: nothing honored"
                    | ds, _ -> failtest $"{bad}: expected one error, got {ds}")
        }
        test "trust: cobra only from the user dir, git from anywhere" {
            withTempDir "weir-dytrust" (fun root ->
                let user = Path.Combine(root, "cfg", "weir", "sigs")
                let proj = Path.Combine(root, "proj", ".weir", "sigs")
                Directory.CreateDirectory user |> ignore
                Directory.CreateDirectory proj |> ignore

                let at dir line =
                    let p = Path.Combine(dir, "dy.weir")
                    File.WriteAllLines(p, sigText line)

                    match loadAt p "dy" with
                    | [], [ si ] -> Weir.Sigs.dynamicFor user si
                    | ds, _ -> failtest $"{ds}"

                Expect.equal (at user "let dynamic = \"cobra\"") Weir.Sigs.DynCobra "user-dir cobra"
                Expect.equal (at proj "let dynamic = \"cobra\"") Weir.Sigs.DynRefused "project cobra"
                Expect.equal (at user "let dynamic = \"git\"") Weir.Sigs.DynGit "user-dir git"
                Expect.equal (at proj "let dynamic = \"git\"") Weir.Sigs.DynGit "project git"
                Expect.equal (at proj "") Weir.Sigs.DynOff "none")
        }
        test "Tab: project cobra never runs the tool, user-dir cobra does, layer 1 stays" {
            withTempDir "weir-dytab" (fun root ->
                let user = Path.Combine(root, "cfg")
                let proj = Path.Combine(root, "proj")
                Directory.CreateDirectory user |> ignore
                Directory.CreateDirectory(Path.Combine(proj, ".git")) |> ignore
                Directory.CreateDirectory(Path.Combine(proj, ".weir", "sigs")) |> ignore

                File.WriteAllLines(
                    Path.Combine(proj, ".weir", "sigs", "dyproj.weir"),
                    sigText "let dynamic = \"cobra\""
                )

                File.WriteAllLines(Path.Combine(user, "dyuser.weir"), sigText "let dynamic = \"cobra\"")
                File.WriteAllText(Path.Combine(proj, "zqfile.txt"), "")

                inCwd proj (fun () ->
                    // a project sig asking for cobra: nothing runs, one hint, layer 1
                    let hooks, asked, notes = fakeHooks (answer [ "pod-a" ] true)
                    Expect.isNone (tabIn hooks user Map.empty "dyproj get ") "paths, as layer 1"
                    Expect.isNone (tabIn hooks user Map.empty "dyproj get zq") "again"
                    Expect.equal asked.Count 0 "the tool never ran"
                    Expect.equal (tabIn hooks user Map.empty "dyproj g") (Some [ "get" ]) "subcommands stay"
                    Expect.equal notes.Count 1 "one hint"
                    Expect.stringContains notes[0] "weir add sig --global dyproj" "the hint names the fix"
                    let _ = tabIn hooks user Map.empty "dyproj config "
                    Expect.equal notes.Count 1 "once per session per tool"

                    // the user dir's: asked with the typed words and the partial
                    let hooks, asked, notes =
                        fakeHooks (answer [ "pod-a"; "pod-b"; "other"; "bad word"; "x$(y)" ] true)

                    Expect.equal
                        (tabIn hooks user Map.empty "dyuser get ")
                        (Some [ "pod-a"; "pod-b"; "other" ])
                        "candidates, unquotable ones dropped"

                    Expect.equal
                        (asked |> Seq.map (fun r -> r.Mechanism, r.Exe, r.Args) |> List.ofSeq)
                        [ "cobra", "dyuser", [ "get"; "" ] ]
                        "an empty partial is \"\""

                    Expect.equal
                        (tabIn hooks user Map.empty "dyuser get po")
                        (Some [ "pod-a"; "pod-b" ])
                        "filtered by the partial"

                    Expect.equal asked[1].Args [ "get"; "po" ] "the partial"

                    Expect.equal
                        (tabIn hooks user Map.empty "dyuser --verbose get --name ")
                        (Some [ "pod-a"; "pod-b"; "other" ])
                        "a flag's value"

                    Expect.equal asked[2].Args [ "--verbose"; "get"; "--name"; "" ] "the words as typed"
                    // static positions spawn nothing
                    let n = asked.Count
                    Expect.equal (tabIn hooks user Map.empty "dyuser g") (Some [ "get" ]) "a subcommand"
                    Expect.equal (tabIn hooks user Map.empty "dyuser get --verb") (Some [ "--verbose" ]) "a flag name"
                    let _ = tabIn hooks user Map.empty "dyuser get -"
                    Expect.equal asked.Count n "nothing asked at static positions"
                    Expect.equal notes.Count 0 "no hint for a trusted sig"

                    // `--flag=`: cobra answers the values; offers replace the editor word
                    let hooks, asked, _ = fakeHooks (answer [ "alpha"; "beta" ] false)

                    Expect.equal
                        (tabIn hooks user Map.empty "dyuser get --name=a")
                        (Some [ "alpha" ])
                        "the value part"

                    Expect.equal asked[0].Args [ "get"; "--name=a" ] "the whole word is the partial"

                    // none fits: NoFileComp offers nothing, else layer 1
                    let hooks, _, _ = fakeHooks (answer [ "zz" ] true)
                    Expect.equal (tabIn hooks user Map.empty "dyuser get q") (Some []) "NoFileComp, no path fallback"
                    let hooks, _, _ = fakeHooks (answer [ "zz" ] false)
                    Expect.isNone (tabIn hooks user Map.empty "dyuser get q") "default: paths"

                    Expect.equal
                        (tabIn hooks user Map.empty "dyuser get --name q")
                        (Some [])
                        "default: layer 1's nothing"

                    let hooks, _, _ = fakeHooks Weir.DynComplete.Fallback
                    Expect.isNone (tabIn hooks user Map.empty "dyuser get ") "a failure: paths"
                    Expect.equal (tabIn hooks user Map.empty "dyuser get --name ") (Some []) "a failure: nothing"
                    let hooks, _, _ = fakeHooks Weir.DynComplete.Interrupted
                    Expect.equal (tabIn hooks user Map.empty "dyuser get ") (Some []) "a key arrived: nothing"
                    // a file value keeps layer 1's paths when the tool has none
                    let hooks, asked, _ = fakeHooks Weir.DynComplete.Fallback
                    Expect.isNone (tabIn hooks user Map.empty "dyuser --config ") "a path value"
                    Expect.equal asked.Count 1 "asked first"
                    // a word weir would not pass as typed asks nothing
                    let hooks, asked, _ = fakeHooks (answer [ "a" ] false)
                    let _ = tabIn hooks user Map.empty "dyuser get \"x y\" "
                    let _ = tabIn hooks user Map.empty "dyuser get $x "
                    Expect.equal asked.Count 0 "quoted or spliced words: no ask"
                    // a `/` word asks too (pod/web), a `~` word never
                    let hooks, asked, _ = fakeHooks (answer [ "pod/web"; "svc/db" ] false)
                    Expect.equal (tabIn hooks user Map.empty "dyuser get pod/") (Some [ "pod/web" ]) "a slash word"
                    let _ = tabIn hooks user Map.empty "dyuser get ~/"
                    Expect.equal asked.Count 1 "never a ~ word"
                    // no hooks (the LSP's and layer 1's path): nothing asked
                    Expect.isNone (Weir.Sigs.sigArgvCompleteWith None user Map.empty env "dyuser get " 11) "no hooks"))
        }
        test "Tab: git from any sig, through an alias — co <TAB> lists branches" {
            withTempDir "weir-dygit" (fun root ->
                let proj = Path.Combine(root, "proj")
                Directory.CreateDirectory(Path.Combine(proj, ".git")) |> ignore
                Directory.CreateDirectory(Path.Combine(proj, ".weir", "sigs")) |> ignore

                File.WriteAllLines(
                    Path.Combine(proj, ".weir", "sigs", "git.weir"),
                    [
                        "module Git"
                        "let dynamic = \"git\""
                        "type Globals = {"
                        "    [<Wire \"-C\">] dir: string"
                        "}"
                        "type CheckoutFlags = {"
                        "    [<Wire \"-b\">] branch: string"
                        "    force: bool"
                        "}"
                        "type Cmd ="
                        "    | Checkout of CheckoutFlags"
                        "    | Commit"
                        "    | Push"
                        "    | Remote"
                        "    | RemoteRemove"
                    ]
                )

                inCwd proj (fun () ->
                    let user = Path.Combine(root, "nouser")
                    let aliases = Map [ "co", ("git", [ "checkout" ]) ]
                    let hooks, asked, notes = fakeHooks (answer [ "main"; "feature/x"; "fix-1" ] false)
                    Expect.equal (tabIn hooks user aliases "co ") (Some [ "main"; "feature/x"; "fix-1" ]) "co <TAB>"

                    Expect.equal
                        (asked |> Seq.map (fun r -> r.Mechanism, r.Exe) |> List.ofSeq)
                        [ "git", "git" ]
                        "weir's own git"

                    Expect.equal
                        asked[0].Args
                        (Weir.DynComplete.gitArgs Weir.DynComplete.Refs)
                        "the fixed listing, no typed text"

                    Expect.equal (tabIn hooks user aliases "co f") (Some [ "feature/x"; "fix-1" ]) "filtered"
                    Expect.equal (tabIn hooks user aliases "co feature/") (Some [ "feature/x" ]) "a slash word"
                    Expect.equal (tabIn hooks user Map.empty "git checkout m") (Some [ "main" ]) "unaliased"

                    Expect.equal
                        (tabIn hooks user Map.empty "git push ")
                        (Some [ "main"; "feature/x"; "fix-1" ])
                        "push: a remote, asked"

                    Expect.equal asked[asked.Count - 1].Args [ "remote" ] "git remote"
                    let n = asked.Count
                    Expect.isNone (tabIn hooks user Map.empty "git commit ") "commit: no ref operand"
                    Expect.equal (tabIn hooks user aliases "co -b ") (Some []) "a flag's value: git asks nothing"
                    Expect.equal (tabIn hooks user aliases "co --fo") (Some [ "--force" ]) "flags stay static"
                    Expect.equal asked.Count n "nothing asked off the table"
                    Expect.equal notes.Count 0 "git needs no hint"))
        }
        test "the bounded child: answer, timeout, cap, a key, and no child left" {
            if not (System.OperatingSystem.IsWindows()) then
                withTempDir "weir-dyrun" (fun d ->
                    let never () = false

                    let r =
                        Weir.DynComplete.run "sh" [ "-c"; "printf 'a\\tdoc\\nb\\n:4\\n'" ] 2000 never

                    Expect.equal r (Weir.DynComplete.Done "a\tdoc\nb\n:4\n") "an answer"

                    Expect.equal
                        (Weir.DynComplete.run "weir-no-such-tool-dy" [] 300 never)
                        Weir.DynComplete.Failed
                        "not found"

                    let pidFile = Path.Combine(d, "pids")

                    // gone or a zombie awaiting its reaper, within a second
                    let gone () =
                        let alive (pid: string) =
                            try
                                let stat = File.ReadAllText $"/proc/{pid.Trim()}/stat"
                                stat.Substring(stat.LastIndexOf ')' + 2, 1) <> "Z"
                            with _ ->
                                false

                        let pids = File.ReadAllLines pidFile |> Array.filter (fun l -> l.Trim() <> "")

                        Expect.isNonEmpty pids "the child wrote its pid"
                        let clock = System.Diagnostics.Stopwatch.StartNew()

                        while Array.exists alive pids && clock.ElapsedMilliseconds < 1000L do
                            System.Threading.Thread.Sleep 20

                        not (Array.exists alive pids)

                    let hung = $"echo $$ >> '{pidFile}'; sleep 30 & echo $! >> '{pidFile}'; wait"
                    let clock = System.Diagnostics.Stopwatch.StartNew()

                    Expect.equal
                        (Weir.DynComplete.run "sh" [ "-c"; hung ] 300 never)
                        Weir.DynComplete.Failed
                        "the timeout"

                    Expect.isLessThan clock.ElapsedMilliseconds 1500L "returns at the bound"
                    Expect.isTrue (gone ()) "the child and its grandchild are gone"

                    File.Delete pidFile

                    let flood =
                        $"echo $$ >> '{pidFile}'; while :; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; done"

                    Expect.equal
                        (Weir.DynComplete.run "sh" [ "-c"; flood ] 5000 never)
                        Weir.DynComplete.Failed
                        "the cap"

                    Expect.isTrue (gone ()) "the flooder is gone"

                    File.Delete pidFile
                    let mutable polls = 0

                    let soon () =
                        polls <- polls + 1
                        polls > 40

                    Expect.equal (Weir.DynComplete.run "sh" [ "-c"; hung ] 5000 soon) Weir.DynComplete.Aborted "a key"
                    Expect.isTrue (gone ()) "killed on the key"

                    // the cache: one run per question per edit; an
                    // interrupted ask stays uncached
                    Weir.DynComplete.clearCache ()
                    let mutable runs = 0

                    let runner result =
                        fun (_: string) (_: string list) ->
                            runs <- runs + 1
                            result

                    let req: Weir.DynComplete.Request = {
                        Mechanism = "cobra"
                        Exe = "t"
                        Args = [ "x" ]
                    }

                    Expect.equal
                        (Weir.DynComplete.askWith (runner Weir.DynComplete.Aborted) req)
                        Weir.DynComplete.Interrupted
                        "interrupted"

                    Expect.equal
                        (Weir.DynComplete.askWith (runner (Weir.DynComplete.Done "a\n:0")) req)
                        (answer [ "a" ] false)
                        "asked again"

                    Expect.equal
                        (Weir.DynComplete.askWith (runner Weir.DynComplete.Failed) req)
                        (answer [ "a" ] false)
                        "cached"

                    Expect.equal runs 2 "two runs"
                    Weir.DynComplete.clearCache ()

                    Expect.equal
                        (Weir.DynComplete.askWith (runner Weir.DynComplete.Failed) req)
                        Weir.DynComplete.Fallback
                        "cleared"

                    Weir.DynComplete.clearCache ())
        }
        test "add sig writes `let dynamic` for cobra and git sources, nothing for others" {
            if not (System.OperatingSystem.IsWindows()) then
                withTempDir "weir-dyadd" (fun d ->
                    let gen sample =
                        let tool = Path.Combine(sampleDir, sample, "exe", sample)

                        match Weir.SigGen.generateTo (Some d) (Path.Combine(d, ".weir")) tool with
                        | Ok _ -> File.ReadAllLines(Path.Combine(d, Weir.Sigs.sigFileName tool)) |> List.ofArray
                        | Error e -> failtest $"{sample}: {e}"

                    let cobra = gen "cobrafx"
                    Expect.contains cobra "let dynamic = \"cobra\"" "a cobra source"
                    let git = gen "gitfx"
                    Expect.contains git "let dynamic = \"git\"" "git's helper source"
                    let fish = gen "fishfx"
                    Expect.isFalse (fish |> List.exists (fun l -> l.StartsWith "let dynamic")) "a fish source: none")
        }
    ]
