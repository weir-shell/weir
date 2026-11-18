module FmtTests

// The fmt suites. A suite sits in the file of the area it mostly
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

// the one formatter entry point the CLI and the LSP share [D:fmt-v1]
let fmtSourceTests =
    testList "fmt: formatSource (CLI and LSP)" [
        test "the shebang rides verbatim, trailing spaces and all" {
            match Weir.Fmt.formatSource "t.weir" "#!/usr/bin/env weir  \nlet x=1\nprint (show x)\n" with
            | Ok(lines, changed) ->
                Expect.equal lines [ "#!/usr/bin/env weir  "; "let x = 1"; "print (show x)" ] "formatted body"
                Expect.isTrue changed "the body changed"
            | Error e -> failtest e
        }
        test "an error names the file's own line under a shebang" {
            match Weir.Fmt.formatSource "t.weir" "#!/usr/bin/env weir\nprint \"a\"\nlet x = <<<\n" with
            | Error e -> Expect.stringContains e "line 3" "counted from the shebang line"
            | Ok r -> failtestf "must refuse: %A" r
        }
        test "an indented first statement under a shebang is refused, not re-indented" {
            match Weir.Fmt.formatSource "t.weir" "#!/usr/bin/env weir\n  print \"a\"\n" with
            | Error _ -> ()
            | Ok r -> failtestf "must refuse as the CLI does: %A" r
        }
        test "a missing final newline is not canonical; CRLF is" {
            match Weir.Fmt.formatSource "t.weir" "let x = 1\nprint (show x)" with
            | Ok(_, changed) -> Expect.isTrue changed "no final newline"
            | Error e -> failtest e

            match Weir.Fmt.formatSource "t.weir" "let x = 1\r\nprint (show x)\r\n" with
            | Ok(lines, changed) ->
                Expect.isFalse changed "CRLF reads as LF"
                Expect.equal lines [ "let x = 1"; "print (show x)" ] ""
            | Error e -> failtest e

            match Weir.Fmt.formatSource "t.weir" "" with
            | Ok(lines, changed) ->
                Expect.isEmpty lines "an empty file"
                Expect.isFalse changed "is canonical"
            | Error e -> failtest e
        }
        test "a bare CR ends a line, as the script reader reads it [D:one-pipeline]" {
            // check/run split \r\n, \n and a bare \r; fmt must see the same
            // lines, or it formats a running file into a failing one
            match
                Weir.Fmt.formatSource
                    "t.weir"
                    "let f x =\n    let y = x\r            + 1\n    y * 10\nprint $\"{f 1}\"\n"
            with
            | Ok(lines, _) ->
                Expect.equal
                    lines
                    [
                        "let f x ="
                        "    let y = x"
                        "        + 1"
                        "    y * 10"
                        "print $\"{f 1}\""
                    ]
                    "the CR line break is a line break"
            | Error e -> failtest e

            match Weir.Fmt.formatSource "t.weir" "print \"a\"\rprint \"b\"\r" with
            | Ok(lines, _) -> Expect.equal lines [ "print \"a\""; "print \"b\"" ] "a CR-only file"
            | Error e -> failtest e

            Expect.equal (Weir.Script.splitSourceText "a\r\nb\rc\nd\n") [ "a"; "b"; "c"; "d"; "" ] "the one splitter"
        }
        test "a yaml block whose first line is a deeper comment formats, never crashes [D:yaml-district]" {
            // the comment is invisible to the reader's layout: the block's
            // base is its first code line
            for src in
                [
                    "let y = yaml\n          // note\n    a: 1\nprint (show y)\n"
                    "let y = yaml\n        // note\n    a: 1\nprint (show y)\n"
                ] do
                match Weir.Fmt.formatSource "t.weir" src with
                | Ok(lines, _) -> Expect.equal (lines |> List.item 2) "    a: 1" $"the block keeps its base: {lines}"
                | Error e -> failtest $"{src}: {e}"
        }
        test "a heredoc whose comment line breaks its layout is refused, located — never a crash" {
            match Weir.Fmt.formatSource "t.weir" "let m = <<<\n            //\n    a\nprint m\n" with
            | Error e ->
                Expect.equal
                    e
                    "cannot format: line 3:5: this heredoc line outdents below the block's first line (fix errors first)"
                    "the reader's own error"
            | Ok r -> failtestf "must refuse: %A" r
        }
        test "a match later on its line takes its arms' `|>` continuation [D:fmt-arm-pipe]" {
            let src = [
                "let x = 2"
                "let r = match x with"
                "        | 1 -> \"a\""
                "        | _ -> \"b\""
                "               |> Str.toUpper"
                "print r"
            ]

            match Weir.Fmt.formatLines src with
            | Ok out -> Expect.equal out src "already canonical"
            | Error e -> failtest e
        }
        test "an else between its if and a deeper then-body formats, keeping the parse [D:fmt-depth-model]" {
            match
                Weir.Fmt.formatLines [
                    "let x = 1"
                    "let v ="
                    "    if x > 0 then"
                    "          \"a\""
                    "        else"
                    "        \"b\""
                    "print v"
                ]
            with
            | Ok out ->
                Expect.equal
                    out
                    [
                        "let x = 1"
                        "let v ="
                        "    if x > 0 then"
                        "            \"a\""
                        "        else"
                        "        \"b\""
                        "print v"
                    ]
                    "the then-body steps past the else"
            | Error e -> failtest e
        }
        test "an init file's #session fields seat at 4, however they were indented [D:init-session-fields]" {
            match
                Weir.Fmt.formatSource
                    "init.weir"
                    "#init\n#session {\n      cwd = \"/tmp\"\n    logLevel = \"info\"\n}\nlet  twice x = x * 2\n"
            with
            | Ok(lines, _) ->
                Expect.equal
                    lines
                    [
                        "#init"
                        "#session {"
                        "    cwd = \"/tmp\""
                        "    logLevel = \"info\""
                        "}"
                        "let twice x = x * 2"
                    ]
                    "aligned"
            | Error e -> failtest e
        }
        test "a misplaced directive refuses fmt with check's message, not a parser dump [D:directive-diagnostic]" {
            match Weir.Fmt.formatSource "t.weir" "print \"a\"\n#sig git\n" with
            | Error e ->
                Expect.equal
                    e
                    "cannot format: line 2:1: misplaced directive: #sig — #sig lines belong at the file head, before any code, comment or blank line (fix errors first)"
                    "the directive message"
            | Ok r -> failtestf "must refuse: %A" r
        }
        test "only parser whitespace is trimmed: U+3000/U+2028/U+FEFF are tokens and stay" {
            for tail in [ "\u3000"; "\u2028"; "\uFEFF" ] do
                match Weir.Fmt.formatSource "t.weir" ("print \"a\"" + tail + "\n") with
                | Ok(lines, _) -> Expect.equal lines [ "print \"a\"" + tail ] $"kept: U+{int tail[0]:X4}"
                | Error _ -> () // a refusal also leaves the file as it is
        }
        test "a layout that would not settle is refused, never flip-flopped" {
            match Weir.Fmt.formatLines [ "match {((}"; "| _ -> 1" ] with
            | Error e -> Expect.stringContains e "does not settle" "named"
            | Ok out ->
                match Weir.Fmt.formatLines out with
                | Ok again -> Expect.equal again out "if accepted, a fixed point"
                | Error e -> failtestf "accepted once, refused after: %s" e
        }
        // [D:fmt-v1] [D:body-blanks]
        test "a formatted file ends in exactly one newline — trailing blank lines go" {
            for src in
                [
                    "print \"a\"\n\n\n"
                    "print \"a\"\n  \n\n"
                    "#!/usr/bin/env weir\nprint \"a\"\n\n"
                ] do
                match Weir.Fmt.formatSource "t.weir" src with
                | Ok(lines, changed) ->
                    Expect.isTrue changed "trailing blanks are not canonical"
                    Expect.notEqual (List.last lines) "" "no blank last line"

                    match Weir.Fmt.formatSource "t.weir" (String.concat "\n" lines + "\n") with
                    | Ok(again, changedAgain) ->
                        Expect.equal again lines "idempotent"
                        Expect.isFalse changedAgain "canonical once formatted"
                    | Error e -> failtest e
                | Error e -> failtest e
        }
        // [D:fmt-parse-gate]
        test "a file check cannot parse is refused, located — a yaml district too" {
            for src, at in
                [
                    "let d = yaml\n    a: [1\n    b:  2\nprint (show d)\n", "line 2:"
                    "let x = (1 +\nprint  x\n", "line 1:"
                    "let x =  1 +* 2\n", "line 1:"
                ] do
                match Weir.Fmt.formatSource "t.weir" src with
                | Error e ->
                    Expect.stringContains e "cannot format" "refused"
                    Expect.stringContains e at "located"
                | Ok r -> failtestf "must refuse an unparseable file: %A" r
        }
        // [D:fmt-respace] [D:content-bytes]
        test "respace never touches heredoc content or a line inside a multi-line hole" {
            let keeps (src: string list) =
                match Weir.Fmt.formatLines src with
                | Ok out -> Expect.equal out src "unchanged"
                | Error e -> failtest e

            keeps [ "let a = 1"; "let s = $<<<"; "    x {a}  q"; "    y  {a}"; "print s" ]
            keeps [ "let a = 1"; "let s = $$<<<"; "    y  {a}"; "print s" ]
            keeps [ "let a = 1"; "let s = $\"x{a +"; "    1}  y{a}  z\""; "print s" ]

            match Weir.Fmt.formatLines [ "let a = 1"; "let s = $\"x{"; "  a  }y{a}\""; "print s" ] with
            | Ok out ->
                Expect.equal out [ "let a = 1"; "let s = $\"x{"; "    a  }y{a}\""; "print s" ] "re-indented only"

                match Weir.Fmt.formatLines out with
                | Ok again -> Expect.equal again out "idempotent"
                | Error e -> failtest e
            | Error e -> failtest e
        }
    ]

// inside an open bracket fmt shifts, never re-levels [D:fmt-bracket-shift]
let fmtBracketShiftTests =
    // formats, asserts the expected layout, then that fmt is the identity
    // on its own output (formatLines' safety check pins the parse)
    let fmtTo (input: string list) (expected: string list) =
        match Weir.Fmt.formatLines input with
        | Ok out ->
            Expect.equal out expected "layout"

            match Weir.Fmt.formatLines out with
            | Ok again -> Expect.equal again out "idempotent"
            | Error e -> failtest e
        | Error e -> failtest e

    let keeps (input: string list) = fmtTo input input

    testList "fmt: bracket layout shifts, never re-levels" [
        test "a |> continuing an arm at its body column keeps that column [D:fmt-arm-pipe]" {
            keeps [ "match 5 with"; "| n -> n"; "       |> print" ]

            keeps [
                "let r = (match 5 with"
                "         | n -> n"
                "                |> show)"
                "print r"
            ]
        }
        test "a re-indented arm carries its |> continuations with its body [D:fmt-arm-pipe]" {
            fmtTo [
                "let f v ="
                "  match v with"
                "  | n -> n + 1"
                "         |> show"
                "print (f 2)"
            ] [
                "let f v ="
                "    match v with"
                "    | n -> n + 1"
                "           |> show"
                "print (f 2)"
            ]

            keeps [
                "let x ="
                "    match 5 with"
                "    | 1 -> \"one\""
                "    | n -> show n"
                "           |> Str.toUpper"
                "           |> Str.trim"
                "print x"
            ]
        }
        test "F1: a multi-line value inside a record entry keeps its layout" {
            keeps [
                "let step st line ="
                "    { A = not st.A"
                "      Kept ="
                "        (if st.A then"
                "             st.Kept"
                "         else"
                "             $\"{st.Kept}\\n{line}\") }"
            ]
        }
        test "F2: lines inside an open paren keep their paren-aligned columns" {
            keeps [
                "let s ="
                "    (if true then"
                "         \"a\""
                "     else"
                "         \"b\")"
            ]

            keeps [
                "let burned ="
                "    (File.read \"ci/release-burned.yaml\""
                "     |> from yaml {| burned: seq<{| tag: string; reason: string |}> |})"
                "        .burned"
                "    |> Seq.freeze"
            ]
        }
        test "a record inside a list keeps its entry's multi-line value" {
            keeps [
                "let xs ="
                "    [ { A = true"
                "        Kept ="
                "          (if true then"
                "               \"x\""
                "           else"
                "               \"y\") }"
                "      { A = false; Kept = \"z\" } ]"
            ]
        }
        test "a record inside a paren keeps its layout" {
            keeps [
                "let r ="
                "    ({ A = 1"
                "       B ="
                "         (if true then"
                "              2"
                "          else"
                "              3) })"
            ]
        }
        test "a Stroustrup entry's continuation keeps its depth" {
            keeps [ "let x = {"; "    A ="; "        1"; "    B = 2"; "}" ]
        }
        test "when the opener moves, the inside shifts by the same amount" {
            fmtTo [
                "let s ="
                "      (if true then"
                "           \"a\""
                "       else"
                "           \"b\")"
            ] [
                "let s ="
                "    (if true then"
                "         \"a\""
                "     else"
                "         \"b\")"
            ]

            fmtTo [ "let xs ="; "      [ [ 1"; "          2 ]"; "        [ 3 ] ]" ] [
                "let xs ="
                "    [ [ 1"
                "        2 ]"
                "      [ 3 ] ]"
            ]

            fmtTo [
                "let step st line ="
                "      { A = not st.A"
                "        Kept ="
                "          (if st.A then"
                "               st.Kept"
                "           else"
                "               \"x\") }"
            ] [
                "let step st line ="
                "    { A = not st.A"
                "      Kept ="
                "        (if st.A then"
                "             st.Kept"
                "         else"
                "             \"x\") }"
            ]
        }
        test "a pulled-back `(match` arm carries its body with it [D:fmt-match-arms]" {
            fmtTo [
                "let f x ="
                "    match x with"
                "    | Some n ->"
                "        (match n with"
                "            | 1 ->"
                "                \"a\""
                "            | _ -> \"b\")"
                "    | None -> \"c\""
            ] [
                "let f x ="
                "    match x with"
                "    | Some n ->"
                "        (match n with"
                "         | 1 ->"
                "             \"a\""
                "         | _ -> \"b\")"
                "    | None -> \"c\""
            ]
        }
        test "a line after a closed paren levels past the paren's lines" {
            keeps [
                "let r ="
                "    if true then"
                "        1"
                "    else"
                "        (r.body"
                "            |> from json {| runs: seq<{| id: int |}> |})"
                "                .runs"
                "        |> Seq.length"
            ]
        }
    ]

let fmtMatchTests =
    testList "fmt: match arm alignment" [
        test "arms align under the m (the drift pull)" {
            match
                Weir.Fmt.formatLines [
                    "let category ="
                    "    match 3 with"
                    "        | s when s > 2 -> \"big\""
                    "        | _ -> \"small\""
                ]
            with
            | Ok lines ->
                Expect.equal lines[2] "    | s when s > 2 -> \"big\"" ""
                Expect.equal lines[3] "    | _ -> \"small\"" ""
            | Error e -> failtest e
        }
        test "nested matches align to their own m; the outer arm returns" {
            match
                Weir.Fmt.formatLines [
                    "let deep ="
                    "    match 1 with"
                    "        | 1 ->"
                    "            match 2 with"
                    "                | 2 -> \"a\""
                    "                | _ -> \"b\""
                    "        | _ -> \"c\""
                ]
            with
            | Ok lines ->
                Expect.equal lines[2] "    | 1 ->" "outer arm at outer m"
                Expect.equal lines[4] "        | 2 -> \"a\"" "inner arm at inner m"
                Expect.equal lines[6] "    | _ -> \"c\"" "outer resumes after inner closes"
            | Error e -> failtest e
        }
        test "a parenthesized match aligns its arms under the m, not the line indent [D:fmt-match-arms]" {
            // `(match …` — the `m` sits one past the `(`, so the arms align at
            // that column (9 here), not at the block-depth column (12) the
            // old `piece.StartsWith \"match \"` check produced by missing the `(`
            match
                Weir.Fmt.formatLines [
                    "let f x ="
                    "    match x with"
                    "    | Some n ->"
                    "        (match n with"
                    "            | 1 -> \"a\""
                    "            | _ -> \"b\")"
                    "    | None -> \"c\""
                ]
            with
            | Ok lines ->
                Expect.equal lines[3] "        (match n with" "the parenthesized match head"
                Expect.equal lines[4] "         | 1 -> \"a\"" "inner arm under the m (col 9), not col 12"
                Expect.equal lines[5] "         | _ -> \"b\")" "inner arm under the m"
                Expect.equal lines[6] "    | None -> \"c\"" "outer arm resumes"
            | Error e -> failtest e
        }
        test "union cases and chain stages are not arms" {
            match Weir.Fmt.formatLines [ "type Cmd ="; "    | First of int"; "    | Second" ] with
            | Ok lines -> Expect.equal lines[1] "    | First of int" "union case untouched"
            | Error e -> failtest e

            match
                Weir.Fmt.formatLines [
                    "let r ="
                    "    match 1 with"
                    "    | 1 ->"
                    "        sh -c \"echo one\""
                    "        | complete"
                    "    | _ -> 0"
                ]
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
        }
    ]

let fmtStroustrupTests =
    testList "fmt: Stroustrup brackets" [
        test "dangling opener: a CONSISTENT group canonicalizes to +4" {
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
            match Weir.Fmt.formatLines [ "type Ctx = {"; "        Subdir: string"; "      Repo: string"; "    }" ] with
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
        }
    ]

let fmtTests =
    testList "Formatter" [
        test "normalizes structural indentation to 4 per depth" {
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
                    [
                        "let x ="
                        "    let a ="
                        "        [1; 2]"
                        "        |> Seq.sum"
                        "    a + 1"
                    ]
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
            | Error msg ->
                Expect.stringContains msg "cannot format" "assembler error surfaces"
                // located as a parse refusal is [D:assembly-col]
                Expect.stringContains msg "cannot format: line 2:5: this let needs a body" "line:col"
            | Ok _ -> failtest "expected refusal"
        }
    ]

let fmtRecordTests =
    testList "fmt: record field alignment" [ // misaligned fields ERROR at assembly [D:field-alignment] —
        // fmt owns no repair job
        test "field drift is an assembly error (the repair job retired)" {
            match
                Weir.Fmt.formatLines [
                    "let target ="
                    "    { Name = \"a\""
                    "        BicepPath = \"b\""
                    "        Env = \"c\" }"
                ]
            with
            | Error e -> Expect.stringContains e "indented off its siblings" ""
            | Ok _ -> failtest "expected the alignment error"
        }
        test "nested record fields align under the inner brace" {
            // the inner anchor is V's column (13); W aligned there is
            // legal and fmt-stable [D:field-alignment]
            match
                Weir.Fmt.formatLines [
                    "let o ="
                    "    { Name = \"x\""
                    "      In = { V = 1"
                    "             W = 2 } }"
                ]
            with
            | Ok lines -> Expect.equal (lines[3].TrimEnd()) (String.replicate 13 " " + "W = 2 } }") ""
            | Error e -> failtest e
        }
        test "an update header anchors on its first FIELD, not the source [D:field-alignment]" {
            let asm lines =
                Weir.Script.assemble (lines |> List.mapi (fun i l -> i + 1, l))

            match asm [ "let c = { d with host = \"x\""; "                 port = 2 }" ] with
            | Ok [ ll ] -> Expect.stringContains ll.Text "port = 2" "one statement, aligned under host"
            | other -> failtest $"expected one statement: %A{other}"

            match asm [ "let c = { d with host = \"x\""; "          port = 2 }" ] with
            | Error e -> Expect.stringContains e "they sit at column 17" "under the source is off the field"
            | Ok _ -> failtest "aligned under the source must refuse"

            // a source with parens and a string still finds its `with`
            match
                asm [
                    "let c = { (pick \"a with b\") with host = \"x\""
                    "                                 port = 2 }"
                ]
            with
            | Ok [ _ ] -> ()
            | other -> failtest $"expected one statement: %A{other}"

            // `match … with` inside a field value is not a header
            match asm [ "let r = { a = match x with"; "              | _ -> 1"; "          b = 2 }" ] with
            | Ok _ -> ()
            | Error e -> failtest e
        }
    ]
