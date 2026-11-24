module LspTests

// The lsp suites. A suite sits in the file of the area it mostly
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
open ReplTests

let derivedOrdTests =
    let cmp = Weir.Eval.orderCompare "sort"

    let onlyTail =
        "only int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, and records or unions numbered with [<Ord n>] order"

    let keysTail =
        "keys are int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, or a record or union numbered with [<Ord n>]"

    let tupleTail =
        "tuples have no order; name the parts in a record and number them with [<Ord n>]"

    testList "Derived Ord [D:derived-ord]" [ // ---- the declaration law ----
        test "[<Ord n>] members form the def's key by ascending n; declaration order means nothing" {
            let keyOf name =
                match Map.tryFind name ordEnv.Types with
                | Some(Record r) -> r.OrdKey
                | Some(Union u) -> u.OrdKey
                | None -> failtest $"no def {name}"

            Expect.equal (keyOf "OrdP") [ "a"; "b" ] "record"
            Expect.equal (keyOf "OrdU") [ "OA"; "OB" ] "union"
            Expect.equal (keyOf "OrdRev") [ "second"; "first" ] "the numbers, not the declaration"
            Expect.equal (keyOf "OrdPart") [ "key" ] "the unnumbered take no part"
            Expect.equal (keyOf "Plain") [] "no numbers, no order"
            Expect.equal (keyOf "PlainU") [] "no numbers, no order (union)"
        }
        test "an Option field, a nested ordered field, a self-typed payload and a gap in the numbering admit" {
            ordEnv
            |> declare "type OrdOpt = { [<Ord 1>] o: Option<int>; [<Ord 2>] p: Option<OrdP>; [<Ord 5>] s: SemVer }"
            |> ignore
        }
        test "every refused member kind names the field, its position and the repair (byte-pinned)" {
            let field n f ty tail =
                $"[<Ord {n}>] on 'R': field '{f}' is {ty}, which cannot be ordered — {tail}"

            Expect.equal
                (ordDeclMsg "type R = { name: string; [<Ord 1>] logs: seq<string> }")
                (field 1 "logs" "seq<string>" onlyTail)
                "seq"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1>] m: Map<string, int> }")
                (field 1 "m" "Map<string, int>" onlyTail)
                "Map"

            Expect.equal (ordDeclMsg "type R = { [<Ord 1>] b: Bytes }") (field 1 "b" "Bytes" onlyTail) "Bytes"
            Expect.equal (ordDeclMsg "type R = { [<Ord 1>] s: Secret }") (field 1 "s" "Secret" onlyTail) "Secret"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1>] f: int -> int }")
                (field 1 "f" "int -> int" onlyTail)
                "function"

            Expect.equal (ordDeclMsg "type R = { [<Ord 1>] u: unit }") (field 1 "u" "unit" onlyTail) "unit"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 3>] t: int * string }")
                (field 3 "t" "int * string" tupleTail)
                "tuple"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1>] p: Plain }")
                (field 1 "p" "Plain" "number its fields with [<Ord n>] to order it too")
                "a record without a key"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1>] u: PlainU }")
                (field 1 "u" "PlainU" "number its cases with [<Ord n>] to order it too")
                "a union without a key"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1>] o: Option<seq<int>> }")
                (field 1 "o" "Option<seq<int>>" onlyTail)
                "the innermost offender decides the tail"

            Expect.equal
                (ordDeclMsg "type R = [<Ord 1>] C of int * string | [<Ord 2>] D")
                $"[<Ord 1>] on 'R': case 'C' carries int * string, which cannot be ordered — {tupleTail}"
                "a tuple payload"
        }
        test "the key laws: one member per position, every case or none, a positive int" {
            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1>] a: int; b: int; [<Ord 1>] c: int }")
                "[<Ord 1>] on 'R': fields 'a' and 'c' share position 1 — number each field once"
                "a shared position names both fields"

            Expect.equal
                (ordDeclMsg "type U = [<Ord 2>] A | [<Ord 2>] B")
                "[<Ord 2>] on 'U': cases 'A' and 'B' share position 2 — number each case once"
                "a shared position names both cases"

            Expect.equal
                (ordDeclMsg "type U = [<Ord 1>] A | B of int | C")
                "[<Ord n>] on 'U': case 'B' has no position — number every case or none"
                "a half-numbered union names an unplaced case"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 0>] a: int }")
                "'Ord' expects a positive int, e.g. [<Ord 1>]"
                "zero"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord>] a: int }")
                "'Ord' expects a positive int, e.g. [<Ord 1>]"
                "no argument"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord \"1\">] a: int }")
                "'Ord' expects a positive int, e.g. [<Ord 1>]"
                "a string"

            Expect.equal
                (ordDeclMsg "type R = { [<Ord 1; Ord 2>] a: int }")
                "duplicate attribute 'Ord' on field 'a'"
                "twice on one field"

            Expect.equal
                (ordDeclMsg "[<Ord>] type R = { a: int }")
                "'Ord' attaches to a record field or a union case, not a record declaration"
                "the declaration position teaches the homes"

            Expect.equal
                (ordDeclMsg "[<Ord 1>] type U = A | B")
                "'Ord' attaches to a record field or a union case, not a union declaration"
                "the union declaration likewise"
        }
        test "the collision law: two ordered unions may not share a case name (Option's included)" {
            Expect.equal
                (ordDeclMsg "type R = [<Ord 1>] None | [<Ord 2>] X")
                "[<Ord n>] on 'R': case 'None' is also a case of the ordered union 'Option' — a value carries only its case name, so two ordered unions cannot share one; rename it"
                "the prelude's Option"

            Expect.equal
                (ordDeclMsg "type R = [<Ord 1>] X | [<Ord 2>] OA")
                "[<Ord n>] on 'R': case 'OA' is also a case of the ordered union 'OrdU' — a value carries only its case name, so two ordered unions cannot share one; rename it"
                "a user union"

            // without a key the name is free — only ordered unions register
            ordEnv |> declare "type Free = OA | X" |> ignore
            ordEnv |> declare "type AlsoFree = [<Ord 1>] PA | [<Ord 2>] Y" |> ignore
        }
        test "the collision law for records: an imported ordered record of the same name must agree on the key" {
            let imported key =
                Record {
                    Name = "Ver"
                    Params = []
                    Fields = [ "major", TInt; "minor", TInt ]
                    Attrs = Map.empty
                    OrdKey = key
                    Docs = Map.empty
                }

            let under key = {
                ordEnv with
                    Types = Map.add "A.Ver" (imported key) ordEnv.Types
            }

            Expect.equal
                (declErr "type Ver = { [<Ord 1>] major: int; [<Ord 2>] minor: int }" (under [ "minor"; "major" ]))
                    .Message
                "[<Ord n>] on 'Ver': the imported record 'A.Ver' is ordered by minor, major — a value carries only its record name, so two ordered records named 'Ver' must agree on the key; rename it"
                "a different key refuses"

            under [ "major"; "minor" ]
            |> declare "type Ver = { [<Ord 1>] major: int; [<Ord 2>] minor: int }"
            |> ignore

            under [] |> declare "type Ver = { [<Ord 1>] major: int; minor: int }" |> ignore
        }
        // ---- admission at the use ----
        test "sort, sortDescending, sortBy keys, min/max and the By twins admit an ordered record" {
            ordShow
                "[{ a = 2; b = \"x\" }; { a = 1; b = \"y\" }] |> Seq.sort"
                "[{ a = 1; b = \"y\" }; { a = 2; b = \"x\" }]"

            ordShow
                "[{ a = 1; b = \"y\" }; { a = 2; b = \"x\" }] |> Seq.sortDescending"
                "[{ a = 2; b = \"x\" }; { a = 1; b = \"y\" }]"

            let pairs = "[(\"x\", { a = 2; b = \"a\" }); (\"y\", { a = 1; b = \"z\" })]"
            Expect.equal (runOrd $"{pairs} |> Seq.sortBy snd |> Seq.head |> fst") (VStr "y") "sortBy"

            Expect.equal
                (runOrd $"{pairs} |> Seq.sortByDescending snd |> Seq.head |> fst")
                (VStr "x")
                "sortByDescending"

            Expect.equal (runOrd $"{pairs} |> Seq.minBy snd |> fst") (VStr "y") "minBy"
            Expect.equal (runOrd $"{pairs} |> Seq.maxBy snd |> fst") (VStr "x") "maxBy"
            ordShow "[{ a = 2; b = \"x\" }; { a = 1; b = \"y\" }] |> Seq.min" "{ a = 1; b = \"y\" }"
            ordShow "[{ a = 2; b = \"x\" }; { a = 1; b = \"y\" }] |> Seq.max" "{ a = 2; b = \"x\" }"
        }
        test "the SemVer receipt: a record holding a union sorts, Seq.last picks the top, < compares" {
            let versions =
                "[{ major = 1; minor = 0; patch = 0; pre = Release }; { major = 1; minor = 0; patch = 0; pre = Pre \"alpha\" }; { major = 0; minor = 9; patch = 2; pre = Release }]"

            ordShow
                $"{versions} |> Seq.sort |> Seq.map (fun v -> $\"{{v.major}}.{{v.minor}}.{{v.patch}}-{{show v.pre}}\")"
                "[\"0.9.2-Release\"; \"1.0.0-Pre \\\"alpha\\\"\"; \"1.0.0-Release\"]"

            Expect.equal (runOrd $"({versions} |> Seq.sort |> Seq.last).major") (VInt 1L) "last"
            Expect.equal (runOrd "Pre \"alpha\" < Release") (VBool true) "a union orders by its case positions"
            Expect.equal (runOrd "Pre \"b\" > Pre \"a\"") (VBool true) "then by payload"

            Expect.equal
                (runOrd $"({versions} |> Seq.sort |> Seq.last) < {{ major = 2; minor = 0; patch = 0; pre = Release }}")
                (VBool true)
                "a record < a record"
        }
        test "reorder immunity: the key is the numbers, so a field written first but numbered second breaks ties only" {
            ordShow
                "[{ first = 1; second = 2 }; { first = 2; second = 1 }] |> Seq.sort"
                "[{ first = 2; second = 1 }; { first = 1; second = 2 }]"

            Expect.equal (runOrd "{ first = 9; second = 1 } < { first = 0; second = 2 }") (VBool true) "second decides"
            Expect.equal (runOrd "{ first = 1; second = 1 } < { first = 2; second = 1 }") (VBool true) "then first"
        }
        test
            "the partial-key law: unnumbered fields take no part — equal keys are neither < nor > nor ==, sorts keep input order, min/max the first" {
            let a = "{ key = 1; note = \"a\"; logs = [\"x\"] }"
            let b = "{ key = 1; note = \"b\"; logs = [] }"
            Expect.equal (runOrd $"{a} < {b}") (VBool false) "<"
            Expect.equal (runOrd $"{a} > {b}") (VBool false) ">"
            Expect.equal (runOrd $"{a} <= {b} && {a} >= {b}") (VBool true) "<= and >="

            Expect.equal
                (runOrd $"[{b}; {a}] |> Seq.sort |> Seq.map (fun r -> r.note) |> Str.join \"\"")
                (VStr "ba")
                "Seq.sort is stable"

            Expect.equal
                (runOrd $"[{b}; {a}] |> Seq.sortDescending |> Seq.map (fun r -> r.note) |> Str.join \"\"")
                (VStr "ba")
                "sortDescending too"

            Expect.equal (runOrd $"([{b}; {a}] |> Seq.max).note") (VStr "b") "max returns the first"
            Expect.equal (runOrd $"([{b}; {a}] |> Seq.min).note") (VStr "b") "min returns the first"
            Expect.equal (runOrd $"{{ key = 2; note = \"\"; logs = [] }} > {a}") (VBool true) "the key still decides"
            Expect.stringContains (ordErrMsg $"{a} == {b}") "seq" "== stays whole-value: the seq field refuses Eq"
        }
        test "Option orders everywhere, None first; < compares it as the sort does [D:ord-scalar-operators]" {
            expectValue "show ([Some 1; None; Some 0] |> Seq.sort)" (VStr "[None; Some 0; Some 1]")
            expectValue "[Some \"b\"; None] |> Seq.sortBy (fun o -> o) |> Seq.head |> show" (VStr "None")
            expectValue "Some 1 < None" (VBool false)
            expectValue "None < Some 0" (VBool true)
            expectValue "Some \"a\" <= Some \"b\"" (VBool true)
            expectValue "Some (Some 2) > Some None" (VBool true)
            expectValue "let f o = o >= Some 3 in f (Some 4)" (VBool true)

            Expect.equal
                (checkErr "Some [1] < None").Message
                $"operator '<' is not defined for Option<seq<int>> — {keysTail}"
                "an unorderable content names the set"
        }
        test "string and bool compare by the sort's order; Uuid and tuples stay refused [D:ord-scalar-operators]" {
            expectValue "\"a\" < \"b\"" (VBool true)
            expectValue "\"B\" < \"a\"" (VBool true) // ordinal, as Seq.sort
            expectValue "\"ab\" >= \"ab\"" (VBool true)
            expectValue "false < true" (VBool true)
            expectValue "true <= false" (VBool false)
            expectValue "let lt a b = a + \"\" < b in lt \"x\" \"y\"" (VBool true) // a string-anchored helper
            expectValue "let lt a b = a < b in lt 1 2" (VBool true) // unanchored: int, as before
            expectValue "[\"b\"; \"a\"] |> Seq.where (fun s -> s > \"a\") |> Seq.length" (VInt 1L)

            Expect.equal (checkErr "(1, 2) < (1, 3)").Message "operator '<' is not defined for int * int" "tuples"

            Expect.equal
                (checkErr "Some Uuid.nil < None").Message
                "operator '<' is not defined for Option<Uuid>; a Uuid is an identifier, not a quantity — Seq.sort orders Uuids"
                "an Option of a Uuid"

            // the refusal carries through a generic [D:uuid-unordered-generic]
            Expect.equal
                (checkErr "let before a b = Some a < Some b in before Uuid.nil Uuid.max").Message
                "this use compares values with '<', '>', '<=' or '>=', which are not defined for Uuid; a Uuid is an identifier, not a quantity — Seq.sort orders Uuids"
                "a generic helper instantiated at Uuid"

            Expect.equal
                (checkErr "let f x = x < None in f (Some Uuid.nil)").Message
                "this use compares values with '<', '>', '<=' or '>=', which are not defined for Uuid; a Uuid is an identifier, not a quantity — Seq.sort orders Uuids"
                "an Option-typed parameter instantiated at Option<Uuid>"

            expectValue "let before a b = Some a < Some b in before 1 2" (VBool true)

            expectValue
                "let before a b = Some a < Some b in before (Instant.ofEpochMs 0) (Instant.ofEpochMs 1)"
                (VBool true)

            Expect.equal (checkErr "\"a\" < 1").Message "expected string, got int" "a mismatch"
            Expect.equal (checkErr "Some 1 < 2").Message "expected Option<int>, got int" "an Option against its content"
        }
        test "a generic ordered type is conditional: admits at int, refuses at a function naming the key" {
            ordShow "[{ v = 1 }; { v = 0 }] |> Seq.sort |> Seq.head" "{ v = 0 }"
            let m = ordErrMsg "[{ v = print }] |> Seq.sort |> Seq.length"
            Expect.stringStarts m "cannot sort by this key: OrdBox<" "names the key"
            Expect.stringEnds m keysTail "the admitted set"
        }
        test "the constraint rides a generic helper and a row var, and discharges at an ordered record" {
            Expect.equal
                (runOrd "let best xs = xs |> Seq.max in (best [{ a = 1; b = \"a\" }; { a = 3; b = \"b\" }]).a")
                (VInt 3L)
                "a generic helper (pipe)"

            Expect.equal
                (runOrd
                    "let bykey k xs = xs |> Seq.sortBy k in [{ a = 3; b = \"b\" }; { a = 1; b = \"a\" }] |> bykey (fun r -> r) |> Seq.head |> fun r -> r.a")
                (VInt 1L)
                "the precedent's shape, at a record"

            Expect.equal
                (runOrd "let best xs = Seq.max xs in best [1; 3]")
                (VInt 3L)
                "a generic helper (application, ints)"

            Expect.equal
                (runOrd "let good r = [1; 2] |> Seq.sortBy (fun _ -> r) in good { a = 1; b = \"\" } |> Seq.length")
                (VInt 2L)
                "the Ord x rows pin's shape, admitting at an ordered record"
        }
        test "the four operators on two ordered values; a mismatch; an unknown side binds" {
            Expect.equal (runOrd "{ a = 1; b = \"b\" } < { a = 1; b = \"c\" }") (VBool true) "<"

            Expect.equal
                (runOrd "{ a = 2; b = \"a\" } > { a = 1; b = \"z\" }")
                (VBool true)
                "> (the first position decides)"

            Expect.equal (runOrd "{ a = 1; b = \"a\" } <= { a = 1; b = \"a\" }") (VBool true) "<="
            Expect.equal (runOrd "{ a = 1; b = \"a\" } >= { a = 1; b = \"b\" }") (VBool false) ">="
            Expect.equal (runOrd "OA < OB 0 && OB 1 < OB 2") (VBool true) "union: position, then payload"
            Expect.equal (ordErrMsg "{ a = 1; b = \"\" } < 2") "expected OrdP, got int" "a mismatch"
            Expect.equal (runOrd "let f x = x < { a = 1; b = \"\" } in f { a = 0; b = \"\" }") (VBool true) "x binds"
        }
        // ---- refusals at the use (byte-pinned) ----
        test "a key without numbers names the repair; a tuple the record spelling; the rest the set" {
            Expect.equal
                (ordErrMsg "[{ p = 1 }] |> Seq.sort |> Seq.length")
                "cannot sort by this key: Plain cannot be ordered — number its fields with [<Ord n>] to order it"
                "a record"

            Expect.equal
                (ordErrMsg "[PA; PB] |> Seq.max |> show")
                "cannot sort by this key: PlainU cannot be ordered — number its cases with [<Ord n>] to order it"
                "a union, through max"

            Expect.equal
                (checkErr "[(1, 2)] |> Seq.sortBy (fun x -> x) |> Seq.length").Message
                $"cannot sort by this key: int * int cannot be ordered — {tupleTail}"
                "a tuple key"

            Expect.equal
                (checkErr "ls |> Seq.sort |> Seq.length").Message
                $"cannot sort by this key: FileRow cannot be ordered — {keysTail}"
                "a built-in record: no field to number, so the set"

            Expect.equal
                (checkErr "[{| a = 1 |}] |> Seq.sort |> Seq.length").Message
                "cannot sort by this key: {| a: int |} cannot be ordered — an anonymous record has no order; declare the shape as a record and number its fields with [<Ord n>]"
                "an anonymous record"

            Expect.stringEnds
                (checkErr "ls |> Seq.sortBy (fun f -> fun x -> x) |> Seq.length").Message
                keysTail
                "a function key"
        }
        test "the operators on a type without a key teach it; seqs keep the bare refusal" {
            Expect.equal
                (ordErrMsg "{ p = 1 } < { p = 2 }")
                "operator '<' is not defined for Plain — number its fields with [<Ord n>] to compare it"
                "a record"

            Expect.equal
                (ordErrMsg "PA >= PB")
                "operator '>=' is not defined for PlainU — number its cases with [<Ord n>] to compare it"
                "a union"

            Expect.equal (checkErr "[1] < [2]").Message "operator '<' is not defined for seq<int>" "seqs"
        }
        // ---- the runtime ----
        test "orderCompare: records by their registered key, the first position decides, equal prefixes fall through" {
            let r a b =
                VRecord("OrdP", [ "a", VInt a; "b", VStr b ])

            Expect.equal (cmp (r 1L "z") (r 2L "a")) -1 "first position"
            Expect.equal (cmp (r 1L "a") (r 1L "b")) -1 "second position"
            Expect.equal (cmp (r 1L "a") (r 1L "a")) 0 "equal"
            Expect.equal (cmp (r 3L "a") (r 2L "z")) 1 "greater"

            // the carried order is not the key: a value spelled fields-reversed compares the same
            let rev a b =
                VRecord("OrdRev", [ "first", VInt a; "second", VInt b ])

            let rev' a b =
                VRecord("OrdRev", [ "second", VInt b; "first", VInt a ])

            Expect.equal (cmp (rev 9L 1L) (rev' 0L 2L)) -1 "second decides, whatever the carried order"
        }
        test "orderCompare: unions by case position then payload; None before Some; nested through a record" {
            Expect.isTrue (ordValueEnv.ContainsKey "OA") "the fixture env registered its unions"
            Expect.isTrue (cmp (VUnion("OA", None)) (VUnion("OB", Some(VInt 0L))) < 0) "position"
            Expect.isTrue (cmp (VUnion("OB", Some(VInt 2L))) (VUnion("OB", Some(VInt 1L))) > 0) "payload"
            Expect.isTrue (cmp (VUnion("None", None)) (VUnion("Some", Some(VInt -5L))) < 0) "None first"

            let v (pre: Value) =
                VRecord("SemVer", [ "major", VInt 1L; "minor", VInt 0L; "patch", VInt 0L; "pre", pre ])

            Expect.isTrue
                (cmp (v (VUnion("Pre", Some(VStr "alpha")))) (v (VUnion("Release", None))) < 0)
                "record > union > payload"
        }
        test "a self-recursive ordered value 100k deep compares without overflowing [D:eq-depth]" {
            let deep n =
                Seq.fold (fun acc _ -> VUnion("Node", Some acc)) (VUnion("Leaf", None)) (seq { 1..n })

            Expect.equal (cmp (deep 100000) (deep 100001)) -1 "the shorter chain reaches Leaf first"
            Expect.equal (cmp (deep 100000) (deep 100000)) 0 "equal chains"
        }
        test
            "the registries store the listed positions, forget on the REPL's replace, and trip on a disagreeing re-registration" {
            Weir.Eval.registerOrdCases "ReplR" [ "RA", 1L; "RB", 7L ]
            Expect.isTrue (cmp (VUnion("RA", None)) (VUnion("RB", None)) < 0) "listed positions"
            Weir.Eval.forgetOrd "ReplR"
            Weir.Eval.registerOrdCases "ReplR" [ "RA", 7L; "RB", 1L ]
            Expect.isTrue (cmp (VUnion("RA", None)) (VUnion("RB", None)) > 0) "reversed"

            Expect.throws
                (fun () -> Weir.Eval.registerOrdCases "OtherR" [ "RA", 2L ])
                "another owner at another position trips"

            Weir.Eval.registerOrdCases "OtherR" [ "RA", 7L ]
            Weir.Eval.forgetOrd "ReplR"
            Weir.Eval.forgetOrd "OtherR"

            Weir.Eval.registerOrdFields "ReplF" [ "a"; "b" ]
            Weir.Eval.registerOrdFields "ReplF" [ "a"; "b" ]

            Expect.throws
                (fun () -> Weir.Eval.registerOrdFields "ReplF" [ "b"; "a" ])
                "another key for the same record name trips"

            Weir.Eval.forgetOrd "ReplF"
            Weir.Eval.registerOrdFields "ReplF" [ "b"; "a" ]
            Weir.Eval.forgetOrd "ReplF"
        }
        test "sortBy with equal ordered keys keeps input order (stable, like sortByDescending's pin)" {
            ordShow
                "[(\"b\", 1); (\"a\", 1); (\"c\", 0)] |> Seq.sortBy (fun (_, k) -> { a = k; b = \"\" }) |> Seq.map fst"
                "[\"c\"; \"b\"; \"a\"]"
        }
        // ---- composition cells ----
        test "x [<Tag>]: a tagged ordered union sorts and still crosses the wire" {
            ordShow "[Deployment { replicas = 1 }; Ping] |> Seq.sort" "[Ping; Deployment ({ replicas = 1 })]"

            Expect.equal
                (runOrd "[Deployment { replicas = 1 }; Ping] |> Seq.sort |> to json |> Seq.head")
                (VStr "[{\"kind\":\"Ping\"},{\"kind\":\"Deployment\",\"replicas\":1}]")
                "the wire reads the tag, the order reads the positions"
        }
        test "x from json: the read record sorts by its key, whatever the wire's field order" {
            ordShow
                "[\"[{\\\"minor\\\": 1, \\\"major\\\": 2}, {\\\"minor\\\": 9, \\\"major\\\": 1}]\"] |> from json seq<OrdV> |> Seq.sort"
                "[{ major = 1; minor = 9 }; { major = 2; minor = 1 }]"
        }
        test "x record update: `with` keeps the value's shape, so the order is stable" {
            ordShow
                "let v = { major = 1; minor = 5 } in [{ v with major = 2 }; { v with minor = 0 }; v] |> Seq.sort"
                "[{ major = 1; minor = 0 }; { major = 1; minor = 5 }; { major = 2; minor = 5 }]"
        }
        test "x Map keys: an ordered record keys a Map in name order while it sorts by its key (R8)" {
            ordShow
                "Map.ofPairs [({ z = 1; a = 2 }, \"x\"); ({ z = 2; a = 1 }, \"y\")]"
                "map [({ z = 2; a = 1 }, \"y\"); ({ z = 1; a = 2 }, \"x\")]"

            ordShow "[{ z = 1; a = 2 }; { z = 2; a = 1 }] |> Seq.sort" "[{ z = 1; a = 2 }; { z = 2; a = 1 }]"
        }
        test "x floats: an ordered record with a float field orders; == on it still refuses" {
            ordShow "[{ x = 2.5 }; { x = 1.5 }] |> Seq.sort" "[{ x = 1.5 }; { x = 2.5 }]"
            Expect.stringContains (ordErrMsg "{ x = 1.0 } == { x = 1.0 }") "it holds a float" ""
        }
        test "a top-level binder hovers its own signature, not an inner let that shadows it" {
            let lines = [
                "let myFunc () ="
                "    let myFunc = 10"
                "    print $\"{myFunc}\""
                "    ()"
            ]

            Expect.equal (Weir.Lsp.hoverType lines 1 5) (Some "myFunc : unit -> unit") "the statement's own binder"
            Expect.equal (Weir.Lsp.hoverType lines 2 9) (Some "myFunc : int") "the inner binder keeps its own"
            Expect.equal (Weir.Lsp.hoverType lines 3 14) (Some "int") "a use resolves to the inner one"
        }
        test "a field name in a record pattern hovers the field's type, not the match's" {
            let lines = [
                "let repro () ="
                "    let handler rq ="
                "      match rq with"
                "      | { method = Get; path = \"/health\" } -> { status = 200; body = Text \"ok\"; headers = [] }"
                "      | _ -> { status = 404; body = Text \"no\"; headers = [] }"
                "    handler"
            ]

            let at (word: string) = lines[3].IndexOf word + 2
            Expect.equal (Weir.Lsp.hoverType lines 4 (at "method")) (Some "HttpMethod") "method"
            Expect.equal (Weir.Lsp.hoverType lines 4 (at "path")) (Some "string") "path"
            Expect.equal (Weir.Lsp.hoverType lines 4 (at "status")) (Some "int") "a literal's field is unchanged"
        }
        test "the attribute's doc hovers; a type's hover shows its key in order, a member's its position" {
            Expect.stringStarts
                (Map.find "Ord" Weir.Builtins.attrDocs)
                "[<Ord n>] — a position in the type's ordering key"
                "attrDocs"

            let recLines = [ "type P = { [<Ord 2>] a: int; [<Ord 1>] b: string; c: bool }"; "print \"x\"" ]

            match Weir.Lsp.hoverType recLines 1 6 with
            | Some h -> Expect.stringStarts h "{ a: int; b: string; c: bool }  (ordered by b, a)" "the record hover"
            | None -> failtest "no hover on the record name"

            match Weir.Lsp.hoverType recLines 1 22 with
            | Some h -> Expect.stringStarts h "[<Ord 2>] int" "the field hover"
            | None -> failtest "no hover on the field"

            match Weir.Lsp.hoverType recLines 1 51 with
            | Some h -> Expect.stringStarts h "bool" "an unnumbered field hovers as before"
            | None -> failtest "no hover on the unnumbered field"

            let uniLines = [ "type U = [<Ord 2>] A | [<Ord 1>] B of int"; "print \"x\"" ]

            match Weir.Lsp.hoverType uniLines 1 6 with
            | Some h -> Expect.stringStarts h "A | B of int  (ordered by B, A)" "the union hover"
            | None -> failtest "no hover on the union name"

            match Weir.Lsp.hoverType uniLines 1 34 with
            | Some h -> Expect.stringStarts h "[<Ord 1>] B of int" "the case hover"
            | None -> failtest "no hover on the case"
        }
        // ---- the property: F#'s derived comparison referees ----
        testProperty
            "orderCompare is total, antisymmetric, transitive, zero exactly at equality, and F#'s compare"
            (fun (a: Fx) (b: Fx) (c: Fx) ->
                registerFx ()
                let a, b, c = fxNorm a, fxNorm b, fxNorm c
                let va, vb, vc = fxValue a, fxValue b, fxValue c
                let ab, ba, bc, ac = cmp va vb, cmp vb va, cmp vb vc, cmp va vc

                sign ab = -(sign ba)
                && (not (ab <= 0 && bc <= 0) || ac <= 0)
                && ((ab = 0) = (va = vb))
                && ((ab = 0) = (a = b))
                && sign ab = sign (compare a b))
        testProperty "Seq.sort's order is sorted, a permutation of its input, and F#'s List.sort" (fun (xs: Fx list) ->
            registerFx ()
            let xs = xs |> List.map fxNorm
            let sorted = xs |> List.map fxValue |> List.sortWith cmp

            sorted |> List.pairwise |> List.forall (fun (x, y) -> cmp x y <= 0)
            && sorted = (xs |> List.sort |> List.map fxValue))
    ]

let lspCrossFileTests =
    // real files on disk: cross-file targets re-analyze the target file
    // through the import channel [D:lsp-cross-file]
    let withTree (f: string -> string list -> string -> string -> unit) =
        let td = tempPath "weir-lspx"

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(td, ".weir", "sigs"))
        |> ignore

        let lib = System.IO.Path.Combine(td, "lib.weir")

        System.IO.File.WriteAllLines(
            lib,
            [|
                "module Lib"
                ""
                "/// the context row"
                "type Ctx = {"
                "    /// the display name"
                "    Name: string"
                "    Port: int"
                "}"
                ""
                "/// outcome marker"
                "type Verdict ="
                "    | Good"
                "    | Bad of int"
                ""
                "/// doubles a number"
                "let double : int -> int"
                ""
                "let double n = n * 2"
                ""
                "/// a plain constant"
                "let origin : string"
                ""
                "let origin = \"here\""
                ""
                "let sample : Verdict"
                ""
                "let sample = Bad 7"
            |]
        )

        let sigp = System.IO.Path.Combine(td, ".weir", "sigs", "mytool.weir")

        System.IO.File.WriteAllLines(
            sigp,
            [|
                "module Mytool"
                "let version = \"mytool 1.0\""
                "let exhaustive = true"
                "type Cmd = {"
                "    /// run without side effects"
                "    dryRun: bool"
                "}"
            |]
        )

        let entry = System.IO.Path.Combine(td, "main.weir")

        let entryLines = [
            "#sig mytool"
            "import \"./lib.weir\" as Lib"
            ""
            "let x = Lib.double 21"
            "let who = Lib.origin"
            "let c = { Name = \"a\"; Port = 1 }"
            "let st = mytool --dry-run"
            "let e = echo hi"
            "let k = match Lib.sample with | Bad n -> n | _ -> 0"
            "print (show c)"
        ]

        try
            f entry entryLines lib sigp
        finally
            System.IO.Directory.Delete(td, true)

    testList "LSP cross-file navigation [D:lsp-cross-file]" [
        test "a module member hovers as its annotated signature + /// doc — the LOCAL format (type, blank, doc)" {
            withTree (fun entry lines _ _ ->
                Expect.equal
                    (Weir.Lsp.hoverAt entry lines 4 14)
                    (Some "Lib.double (n: int) : int\n\ndoubles a number")
                    "the function member"

                Expect.equal
                    (Weir.Lsp.hoverAt entry lines 5 16)
                    (Some "Lib.origin : string\n\na plain constant")
                    "the zero-param value renders name-and-type")
        }
        test "an imported record's field in a literal hovers the FIELD's doc, read from the module file" {
            withTree (fun entry lines _ _ ->
                Expect.equal (Weir.Lsp.hoverAt entry lines 6 12) (Some "string\n\nthe display name") "")
        }
        test "definition crosses files: member, field, union case, each to the declaring module" {
            withTree (fun entry lines lib _ ->
                Expect.equal (Weir.Lsp.definitionTarget entry lines 4 14) (Some(Some lib, 16, 5, 6)) "Lib.double"
                Expect.equal (Weir.Lsp.definitionTarget entry lines 6 12) (Some(Some lib, 6, 5, 4)) "field Name"
                // imported cases reach expressions only through patterns (a bare
                // `Bad 3` reads as a command head — cases do not merge into
                // value scope); the pattern position resolves cross-file
                Expect.equal (Weir.Lsp.definitionTarget entry lines 9 33) (Some(Some lib, 13, 7, 3)) "case Bad")
        }
        test "definition on the import path opens the module file; an unresolved path stays quiet" {
            withTree (fun entry lines lib _ ->
                Expect.equal (Weir.Lsp.definitionTarget entry lines 2 12) (Some(Some lib, 1, 1, 0)) ""

                let broken = [ "import \"./nope.weir\" as N"; "print 1" ]
                Expect.equal (Weir.Lsp.definitionTarget entry broken 1 12) None "unresolved: quiet, never bogus")
        }
        test
            "a signed head hovers identity + RECORDED version (the tool is NOT on PATH — no spawn) and opens its sig file" {
            withTree (fun entry lines _ sigp ->
                Expect.equal
                    (Weir.Lsp.hoverAt entry lines 7 11)
                    (Some
                        $"mytool — signed command (#sig mytool, line 1; exhaustive signature)\n\n{sigp}\nversion: mytool 1.0")
                    "head hover"

                Expect.equal (Weir.Lsp.definitionTarget entry lines 7 11) (Some(Some sigp, 1, 1, 0)) "head def")
        }
        test "a signed flag hovers its field type + /// doc from the sig file, and jumps to the field" {
            withTree (fun entry lines _ sigp ->
                Expect.equal (Weir.Lsp.hoverAt entry lines 7 20) (Some "bool\n\nrun without side effects") ""
                Expect.equal (Weir.Lsp.definitionTarget entry lines 7 20) (Some(Some sigp, 6, 5, 6)) "flag def")
        }
        test "an UNSIGNED command head stays quiet — no bogus location" {
            withTree (fun entry lines _ _ -> Expect.equal (Weir.Lsp.definitionTarget entry lines 8 9) None "")
        }
        test
            "signature help in command position [D:signature-help]: the typed line, one param per flag with its /// doc — the tool is NOT on PATH (no spawn); an unsigned head answers nothing" {
            withTree (fun entry lines _ _ ->
                // the space after `--dry-run` is the trigger: the flag just typed is the active parameter
                let typed = lines |> List.mapi (fun i l -> if i = 6 then l + " " else l)
                let sh = (Weir.Lsp.signatureHelpAt entry typed 7 27).Value
                Expect.equal sh.Label "mytool --dry-run" "the typed line normalised"

                Expect.equal
                    sh.Params
                    [ (7, 16, Some "run without side effects") ]
                    "one parameter per flag present, its doc the field's ///"

                Expect.equal sh.Active (Some 0) "the flag just typed"
                // the flag itself under the cursor, no space yet
                Expect.equal (Weir.Lsp.signatureHelpAt entry lines 7 26).Value.Active (Some 0) "the flag being typed"
                Expect.equal (Weir.Lsp.signatureHelpAt entry lines 8 16) None "unsigned head: nothing")
        }
    ]

let signatureHelpTests =
    // [D:signature-help]: one signature built from the textual spine at
    // the cursor's paren depth, the head resolved in the env in scope,
    // the active parameter = the arguments supplied; null over a guess
    let at (lines: string list) (line: int) (col: int) =
        Weir.Lsp.signatureHelpAt "sighelp" lines line col

    let group (sh: Weir.Lsp.SigHelp) (i: int) =
        let s, e, _ = sh.Params[i]
        sh.Label.Substring(s, e - s)

    testList "Signature help [D:signature-help]" [
        test
            "a builtin head: the three named params off builtinDocs, offsets cutting the label at each (p: t) group, active = supplied" {
            let sh = (at [ "let r = Seq.fold " ] 1 18).Value
            Expect.stringStarts sh.Label "Seq.fold (f: " "the annotated signature"
            Expect.equal sh.Params.Length 3 "f, init, xs"
            Expect.stringStarts (group sh 1) "(init: " "the second group's offsets"
            Expect.stringStarts (group sh 2) "(xs: seq<" "the third group's offsets"
            Expect.equal sh.Active (Some 0) "nothing supplied yet"
            Expect.isTrue (sh.Doc.Value.StartsWith "Combine the elements") "the builtin's doc"
            Expect.equal (at [ "let r = Seq.fold f 0 " ] 1 22).Value.Active (Some 2) "two supplied"
            Expect.equal (at [ "let r = Seq.fold f 0" ] 1 21).Value.Active (Some 1) "the cursor inside the second atom"

            Expect.equal
                (at [ "let r = Seq.fold (" ] 1 19).Value.Active
                (Some 0)
                "the ( trigger opens the first argument"
        }
        test "the pipe rule: the piped value fills the LAST parameter, whose doc says so; explicit slots come first" {
            let sh = (at [ "let xs = [1; 2]"; "let r = xs |> Seq.fold f " ] 2 26).Value
            Expect.equal sh.Active (Some 1) "f supplied, init next"
            let _, _, lastDoc = sh.Params[2]
            Expect.equal lastDoc (Some "xs — filled by the piped value") "the pipe mark"

            Expect.equal
                (at [ "let xs = [1; 2]"; "let r = xs |> Seq.fold f 0 " ] 2 28).Value.Active
                (Some 2)
                "all explicit slots supplied: the piped one"

            Expect.equal (at [ "let xs = [1; 2]"; "let r = xs |> Seq.fold f 0 1 " ] 2 30) None "over-applied: nothing"
        }
        test "when the statement checks, the instantiated type replaces the scheme" {
            let sh =
                (at [ "let xs = [1; 2]"; "let r = xs |> Seq.fold (fun a b -> a + b) 0" ] 2 44).Value

            Expect.equal (group sh 0) "(f: int -> int -> int)" "the node type, not 'a -> 'b -> 'a"
            Expect.equal sh.Active (Some 1) "the cursor inside the second argument"
        }
        test "a user function: names from its lambda chain, the /// doc at its declaration" {
            let sh =
                (at [ "/// adds two numbers"; "let add a b = a + b + 0"; "let r = add 1 " ] 3 15).Value

            Expect.equal sh.Label "add (a: int) (b: int) : int" "the hover's own text"
            Expect.equal sh.Doc (Some "adds two numbers") "the ///"
            Expect.equal (sh.Params |> List.map (fun (s, e, _) -> s, e)) [ (4, 12); (13, 21) ] "exact offsets"
            Expect.equal sh.Active (Some 1) ""
            // partial truth: a function whose statement failed has no typed body — nothing, while a neighbour still answers
            let broken = [ "let add a b = a + b"; "let r = add 1 "; "let s = Seq.fold " ]
            Expect.equal (at broken 2 15) None "the failed statement's binder"
            Expect.equal (at broken 3 18).Value.Active (Some 0) "the neighbour"
        }
        test "the paren depth: inside `print (Str.replace \"a\" ` the inner call answers, active 1" {
            let sh = (at [ "print (Str.replace \"a\" " ] 1 24).Value
            Expect.stringStarts sh.Label "Str.replace (old: string)" ""
            Expect.equal sh.Active (Some 1) ""
        }
        test "a union case: one parameter, the payload, the case's /// doc" {
            let sh =
                (at
                    [
                        "type V ="
                        "    | Good"
                        "    /// a bad one"
                        "    | Bad of int"
                        "let k = Some (Bad "
                    ]
                    5
                    19)
                    .Value

            Expect.equal sh.Label "Bad (value: int) : V" ""
            Expect.equal sh.Doc (Some "a bad one") ""
            Expect.equal sh.Active (Some 0) ""
        }
        test "a multi-line statement: the spine reads the joined text; an inner statement starts the spine over" {
            Expect.equal (at [ "let r ="; "    Seq.fold f " ] 2 16).Value.Active (Some 1) "continuation line"

            Expect.equal
                (at [ "let f x ="; "    let a = 1"; "    Seq.fold f " ] 3 16).Value.Active
                (Some 1)
                "after a sibling"
        }
        test
            "null over a guess: a string, a comment, over-application, a keyword/form head, a lambda, | chaining, an unknown head" {
            Expect.equal (at [ "print \"Seq.fold \"" ] 1 16) None "inside a string"
            Expect.equal (at [ "let r = Seq.fold f 0 // c " ] 1 26) None "inside a comment"
            Expect.equal (at [ "let r = Seq.fold f 0 xs " ] 1 25) None "over-applied"
            Expect.equal (at [ "retry times=3 " ] 1 15) None "a form head"
            Expect.equal (at [ "let r = fun x -> " ] 1 18) None "a lambda"
            Expect.equal (at [ "let r = { Name = " ] 1 18) None "a record literal"
            Expect.equal (at [ "ls | Seq.length " ] 1 17) None "| command chaining"
            Expect.equal (at [ "echo hi " ] 1 9) None "an unknown head"
            Expect.equal (at [ "let r = Seq.fol" ] 1 16) None "still typing the head"
        }
    ]

let letBindingHoverTests =
    // [D:annotated-signature] a binding with a named param hovers as its
    // param signature; an all-`()` (unit) or param-less binding hovers as
    // the flat value type, like F# (which names only named params)
    testList "let binding hover: signature vs flat value type" [
        test "a value bound to an all-unit lambda hovers the flat type, not a () signature" {
            Expect.equal
                (Weir.Lsp.hoverType [ "let fun2 = fun () -> fun () -> 1"; "let y = fun2" ] 1 8)
                (Some "fun2 : unit -> unit -> int  (pure)")
                "flat value type — matches F#'s val fun2 : unit -> unit -> int; pure, so badged [D:pure]"
        }
        test "a function with a NAMED param keeps its signature" {
            match Weir.Lsp.hoverType [ "let apply f x = f x"; "let z = apply" ] 1 9 with
            | Some h -> Expect.stringContains h "apply (f" "the named-param signature is kept"
            | None -> failtest "apply must hover"
        }
    ]

let purityBadgeTests =
    // the display stage of [D:pure]: the rare pure function gets a
    // hover badge; effectful stays the unbadged norm. The badge is
    // conservative — missing is allowed, lying is not.
    let hoverOf (lines: string list) (line: int) (col: int) =
        match Weir.Lsp.hoverType lines line col with
        | Some h -> h
        | None -> failtest "binding must hover"

    testList "the (pure) hover badge [D:pure]" [
        test "a pure transform badges; an effectful sibling does not" {
            let lines = [
                "let slug s = s |> Str.toLower |> Str.replace \" \" \"-\""
                "let logIt s = print (slug s)"
                "print (slug \"A B\")"
                "logIt \"x\""
            ]

            Expect.stringContains (hoverOf lines 1 5) "(pure)" "Str-only body is pure"
            Expect.isFalse ((hoverOf lines 2 5).Contains "(pure)") "print is an effect — no badge"
        }
        test "purity is transitive through earlier bindings" {
            let lines = [
                "let slug s = s |> Str.toLower"
                "let shout s = slug s |> Str.toUpper"
                "let noisy s = print s"
                "let loud s = noisy s"
                "print (shout \"a\")"
                "loud \"b\""
            ]

            Expect.stringContains (hoverOf lines 2 5) "(pure)" "a caller of a pure binding stays pure"
            Expect.isFalse ((hoverOf lines 4 5).Contains "(pure)") "a caller of an effectful binding is not"
        }
        test "an unknown callable forfeits the badge; an inline lambda does not" {
            let lines = [
                "let ap f x = f x"
                "let addOne xs = xs |> Seq.map (fun x -> x + 1)"
                "print (show (ap (fun y -> y) 1))"
                "print (show (addOne [1] |> Seq.length))"
            ]

            Expect.isFalse ((hoverOf lines 1 5).Contains "(pure)") "a function-typed param could do anything"
            Expect.stringContains (hoverOf lines 2 5) "(pure)" "an inline lambda is analyzable"
        }
        test "boundary reads are effects; data bindings carry no badge" {
            let lines = [
                "let readName p = File.read p |> Seq.length"
                "let x = 1"
                "print (show (readName \"f\"))"
                "print (show x)"
            ]

            Expect.isFalse ((hoverOf lines 1 5).Contains "(pure)") "File.read is fs — no badge"
            Expect.isFalse ((hoverOf lines 2 5).Contains "(pure)") "data is trivially pure — the badge is for functions"
        }
        test "a constructor-building function keeps the badge; a Self.stdin toucher loses it [D:pure-stdin-ctors]" {
            let lines = [
                "type Shape ="
                "    | Dot"
                "    | Box of int"
                "let mk n = Box n"
                "let drain () = Self.stdin |> Seq.length"
                "print $\"{mk 1} {drain ()}\""
            ]

            Expect.stringContains (hoverOf lines 4 5) "(pure)" "a data constructor is pure by construction"
            Expect.isFalse ((hoverOf lines 5 5).Contains "(pure)") "Self.stdin drains the input stream — no badge"
        }
    ]

let withinKindsTests =
    // one table [D:within-kinds], three consumers: hover, completion,
    // and the grammar inventories (the last checked mechanically in
    // ci/e2e.sh, alongside the existing micro-vs-tmLanguage guard)
    let ws (t: string) =
        let mutable i = t.Length

        while i > 0
              && (System.Char.IsLetterOrDigit t[i - 1] || t[i - 1] = '_' || t[i - 1] = '.') do
            i <- i - 1

        i

    let sug (line: string) =
        Weir.Complete.suggest Weir.Builtins.typeEnvStrict line (ws line)

    testList "within kinds — one table, three consumers [D:within-kinds]" [
        test "hover on a kind gives its doc AND its binds/consumes nature — never unit" {
            let lines = [ "let d = \"x\""; "within cd d"; "    print \"in\"" ]

            Expect.equal
                (Weir.Lsp.hoverType lines 2 8)
                (Some "cd — consumes: the working directory for the block, restored after\nwithin cd <dir>")
                "the consuming kind"

            let t = [ "within tmp scratch"; "    print scratch" ]

            Expect.equal
                (Weir.Lsp.hoverType t 1 8)
                (Some "tmp — binds: a fresh directory, removed when the block exits\nwithin tmp <name>")
                "the binding kind"
        }
        test "hover on `within` itself answers (the form carries weir's novelty); an ordinary keyword stays silent" {
            let t = [ "within tmp scratch"; "    print scratch" ]

            match Weir.Lsp.hoverType t 1 3 with
            | Some h ->
                Expect.stringContains h "scoped block" "the form's meaning"
                Expect.stringContains h "tmp" "names the kinds"
            | None -> failtest "within must answer"

            Expect.equal (Weir.Lsp.hoverType [ "match 1 with | _ -> 0" ] 1 3) None "an ordinary keyword stays silent"
        }
        test "the BINDER hovers its resource type, even UNUSED — the wrong-answer class the session closes" {
            let t = [ "within tmp ghost"; "    print \"x\""; "print \"done\"" ]
            Expect.equal (Weir.Lsp.hoverType t 1 13) (Some "string") "the binder is the dir path, not unit"
        }
        test "completion after `within ` offers the kinds and NOTHING else; a boundary non-match stays normal" {
            Expect.equal (sug "within ") [ "tmp"; "cd"; "env"; "proc"; "serve"; "lock" ] "the closed set"
            Expect.equal (sug "within c") [ "cd" ] "prefix-filtered"
            Expect.equal (sug "within t") [ "tmp" ] ""
            Expect.equal (sug "within se") [ "serve" ] "prefix-filtered to serve"

            Expect.isFalse
                (sug "notwithin " = [ "tmp"; "cd"; "env"; "proc"; "serve"; "lock" ])
                "boundary: notwithin is not within"

            Expect.isFalse (sug "within cd " = [ "tmp"; "cd"; "env"; "proc" ]) "the arg slot is not the kind slot"
        }
        test "the teaching list derives from the table (a new kind cannot miss the message)" {
            Expect.equal Weir.Ast.withinKindList "tmp, cd, env, proc, serve, or lock" "derived, not hand-written"
        }
        test "a form word inside a STRING or a COMMENT is data, not a form — no hover [D:within-kinds]" {
            // the form hovers run before the silence guard, so the
            // string/comment exclusion is theirs to enforce
            Expect.equal
                (Weir.Lsp.hoverType [ "let m = \"run within cd first\""; "print m" ] 1 18)
                None
                "within in a string"

            Expect.equal
                (Weir.Lsp.hoverType [ "let x = 1 // within cd here"; "print x" ] 1 15)
                None
                "within in a comment"
        }
    ]

let adapterFormTests =
    // the from/to adapters — the within kinds' sibling [D:form-word-hover].
    // The adapter list is derived from the one source (builtinDocs keys)
    // that already backs each adapter's own hover, so they cannot drift.
    let ws (t: string) =
        let mutable i = t.Length

        while i > 0
              && (System.Char.IsLetterOrDigit t[i - 1] || t[i - 1] = '_' || t[i - 1] = '.') do
            i <- i - 1

        i

    let sug (line: string) =
        Weir.Complete.suggest Weir.Builtins.typeEnvStrict line (ws line)

    testList "from/to adapters — the discovery surface [D:form-word-hover]" [
        test "`from`/`to` hover the FORM plus the available adapters (the discovery surface)" {
            let lines = [ "type Config = { name: string }"; "let rows = [\"{}\"] |> from json Config" ]

            match Weir.Lsp.hoverType lines 2 22 with
            | Some h ->
                Expect.stringContains h "from <adapter>" "the form"

                Expect.stringContains
                    h
                    "json, jsonl, table, toml, xml, yaml"
                    "every from-adapter, derived (xml and table read, never write)"
            | None -> failtest "from must answer"

            let t = [ "let back = rows |> to yaml" ]

            match Weir.Lsp.hoverType t 1 20 with
            | Some h ->
                Expect.stringContains h "to <adapter>" "the form"
                Expect.stringContains h "json, jsonl, yaml" "the to-adapters"
            | None -> failtest "to must answer"
        }
        test "the adapter WORD's own hover is unchanged — signature + doc, not touched" {
            let lines = [ "type Config = { name: string }"; "let rows = [\"{}\"] |> from json Config" ]

            match Weir.Lsp.hoverType lines 2 27 with
            | Some h ->
                Expect.stringContains h "seq<string> -> Config" "the adapter's own type"
                Expect.stringContains h "Parse one JSON document" "the adapter's own doc"
            | None -> failtest "the adapter word must still hover"
        }
        test "completion after `from `/`to ` is direction-aware and offers NOTHING else" {
            Expect.equal
                (sug "xs |> from ")
                [ "json"; "jsonl"; "table"; "toml"; "xml"; "yaml" ]
                "every from-adapter (xml and table read)"

            Expect.equal (sug "xs |> from j") [ "json"; "jsonl" ] "prefix-filtered"
            Expect.equal (sug "xs |> to ") [ "json"; "jsonl"; "yaml" ] "every to-adapter (no to xml)"

            Expect.isFalse
                (sug "xs |> into " = [ "json"; "jsonl"; "table"; "toml"; "xml"; "yaml" ])
                "boundary: into is not from"
        }
        test "the adapter lists derive from the one source (builtinDocs keys), never a parallel table" {
            Expect.equal
                (Weir.Builtins.adapterNames "from")
                [ "json"; "jsonl"; "table"; "toml"; "xml"; "yaml" ]
                "from (xml and table read)"

            Expect.equal (Weir.Builtins.adapterNames "to") [ "json"; "jsonl"; "yaml" ] "to (no to xml)"
        }
        test "`from`/`to` inside a string or comment are data — no discovery hover [D:form-word-hover]" {
            Expect.equal (Weir.Lsp.hoverType [ "let m = \"read from json\""; "print m" ] 1 15) None "from in a string"
            Expect.equal (Weir.Lsp.hoverType [ "let x = 1 // from json T"; "print x" ] 1 15) None "from in a comment"
        }
    ]

let hoverResidueTests =
    // the form-word rule's remaining customers + the type-argument leak
    // [D:form-word-hover] — reported-not-fixed by the adapter session,
    // fixed here
    testList "hover residue: retry/poll/until forms + the type argument [D:form-word-hover]" [
        test "hover and signature help name generic types 'a, 'b — never the checker's 'a1 [D:no-raw-tyvars]" {
            let lines = [ "let pair x y = (x, y)"; "print (show (pair 1 \"a\"))" ]

            match Weir.Lsp.hoverType lines 1 5 with
            | Some hv ->
                Expect.stringContains hv "pair (x: 'a) (y: 'b) : 'a * 'b" "the signature"
                Expect.isFalse (hv.Contains "'a1" || hv.Contains "'a2") $"no raw var: {hv}"
            | None -> failtest "pair must answer"

            match Weir.Lsp.hoverType [ "type Box<'b> = { v: 'b }"; "let b = { v = 1 }"; "print (show b.v)" ] 1 6 with
            | Some hv -> Expect.stringContains hv "v: 'b" $"a declared parameter keeps its name: {hv}"
            | None -> failtest "Box must answer"
        }
        test "retry and poll hover their meaning + KEYS derived from the prelude options records" {
            let retryLines = [ "let out = retry attempts=2 delay=0ms (1 == 1)"; "print (show out)" ]

            match Weir.Lsp.hoverType retryLines 1 12 with
            | Some hv ->
                Expect.stringContains hv "bounded retry loop" "the form's meaning"
                // the expectation derives from the same source the hover
                // reads — a new Retry key appears in both with no edit here
                let expected =
                    match Map.tryFind "Retry" preludeTypeEnv.Types with
                    | Some(Weir.Types.Record d) ->
                        d.Fields
                        |> List.map (fun (fn, t) -> $"{fn}: {Weir.Types.formatTy t}")
                        |> String.concat ", "
                    | _ -> failtest "Retry missing from the prelude"

                Expect.stringContains hv expected "keys read from the options record, not a second list"
            | None -> failtest "retry must answer"

            match
                Weir.Lsp.hoverType [ "let out = poll timeout=50ms interval=10ms (1 == 1)"; "print (show out)" ] 1 12
            with
            | Some hv ->
                Expect.stringContains hv "wall-clock twin" "poll's meaning"
                Expect.stringContains hv "timeout: Duration, interval: Duration" "poll's keys"
            | None -> failtest "poll must answer"
        }
        test "pfor hovers as the parallel for, naming Seq.piter and the ceiling knob [D:pfor]" {
            match Weir.Lsp.hoverType [ "pfor x in [1; 2] do print $\"{x}\"" ] 1 2 with
            | Some hv ->
                Expect.stringContains hv "Seq.piter" "the desugar named"
                Expect.stringContains hv "Seq.piterWith n" "the explicit ceiling"
                Expect.stringContains hv "Seq.pmap" "where collecting lives"
            | None -> failtest "pfor must answer"
        }
        test "until hovers the predicate segment's meaning" {
            let lines = [
                "let out = retry attempts=2 delay=0ms"
                "    let r = 1 + 1"
                "    r"
                "until r"
                "    r == 2"
                "print (show out)"
            ]

            match Weir.Lsp.hoverType lines 4 2 with
            | Some hv -> Expect.stringContains hv "names the body's binding" "the binder explanation"
            | None -> failtest "until must answer"
        }
        test "the string/comment gate covers the three new words (the one gate, no second path)" {
            Expect.equal
                (Weir.Lsp.hoverType [ "let m = \"retry it later\""; "print m" ] 1 12)
                None
                "retry in a string is data"

            Expect.equal
                (Weir.Lsp.hoverType [ "let x = 1 // poll again"; "print x" ] 1 15)
                None
                "poll in a comment is data"
        }
        test "T in `from json T` hovers the TYPE's own shape, EQUAL to hovering it at its declaration" {
            let lines = [
                "/// the config row"
                "type Config = { name: string; count: int }"
                "let rows = [\"{}\"] |> from json Config"
            ]

            let atUse = Weir.Lsp.hoverType lines 3 34
            let atDecl = Weir.Lsp.hoverType lines 2 7
            Expect.isSome atUse "the type argument answers"
            Expect.equal atUse atDecl "the two positions cannot drift (shape AND /// doc)"

            match atUse with
            | Some hv ->
                Expect.stringContains
                    hv
                    "{ name: string; count: int }"
                    "the record's own shape (declared field order), not the adapter's arrow"

                Expect.stringContains hv "the config row" "the /// doc rides below"
                Expect.isFalse (hv.Contains "seq<string> ->") "the enclosing arrow no longer leaks"
            | None -> ()
        }
        test "the sibling positions share the fix: from yaml, Env.load, Args.load (record and union)" {
            Expect.equal
                (Weir.Lsp.hoverType [ "type Doc = { title: string }"; "let d = [\"title: x\"] |> from yaml Doc" ] 2 36)
                (Some "{ title: string }")
                "from yaml T"

            Expect.equal
                (Weir.Lsp.hoverType [ "type Cfg = { HOME: string }"; "let c = Env.load Cfg"; "print c.HOME" ] 2 19)
                (Some "{ HOME: string }")
                "Env.load T"

            Expect.equal
                (Weir.Lsp.hoverType [ "type Cli = { flag: bool }"; "let c = Args.load Cli"; "print (show c.flag)" ] 2 20)
                (Some "{ flag: bool }")
                "Args.load T (record)"

            Expect.equal
                (Weir.Lsp.hoverType
                    [
                        "type UpArgs = { fast: bool }"
                        "type Cmd = Up of UpArgs | Down"
                        "let c = Args.load Cmd"
                        "print \"x\""
                    ]
                    3
                    20)
                (Some "Up of UpArgs | Down")
                "Args.load T (union front door)"
        }
    ]

let schemaHoverTests =
    // the schema= name hovers its file facts [D:schema-hover] — a
    // vendored file, not an env.Types entry, so the type-argument arm
    // cannot render it; guards against the district's type leaking
    let withSchemas (f: string -> string list -> string -> unit) =
        let td = tempPath "weir-schema-hover"

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(td, ".weir", "schemas"))
        |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(td, ".weir", "schemas", "six.json"),
            """{ "type": "object", "additionalProperties": false, "title": "Six", "description": "a test shape", "properties": { "kind": { "type": "string" } } }"""
        )

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(td, ".weir", "schemas", "loose.json"),
            """{ "type": "object", "properties": { "kind": { "type": "string" } } }"""
        )

        System.IO.File.WriteAllText(System.IO.Path.Combine(td, ".weir", "schemas", "bad.json"), "{ nope")

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(td, ".weir", "lock.json"),
            """{ "artifacts": [ { "kind": "schema", "name": "six", "url": "https://example.com/six.json", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "path": "schemas/six.json" }, { "kind": "schema", "name": "ghost", "url": "https://example.com/ghost.json", "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", "path": "schemas/ghost.json" } ] }"""
        )

        let entry = System.IO.Path.Combine(td, "main.weir")

        let lines name = [
            $"let d = yaml schema={name}"
            "    kind: Service"
            ""
            "d |> to yaml |> print"
        ]

        let write name =
            System.IO.File.WriteAllLines(entry, lines name)
            lines name

        try
            f entry (write "six") td
        finally
            System.IO.Directory.Delete(td, true)

    testList "schema= hover: the vendored file's facts [D:schema-hover]" [
        test
            "one analysis per text: a repeat request reuses it, a change or a document event re-analyzes [D:lsp-analysis-cache]" {
            let path = "lsp-cache-probe.weir"
            let lines = [ "let probe = 1"; "print (show probe)" ]
            let a = Weir.Lsp.analyzeCached path lines
            let b = Weir.Lsp.analyzeCached path lines
            Expect.isTrue (obj.ReferenceEquals(a, b)) "the same text reuses the analysis"
            let c = Weir.Lsp.analyzeCached path (lines @ [ "print \"x\"" ])
            Expect.isFalse (obj.ReferenceEquals(a, c)) "changed text re-analyzes"
            let d = Weir.Lsp.analyzeCached path (lines @ [ "print \"x\"" ])
            Expect.isTrue (obj.ReferenceEquals(c, d)) "and is cached in turn"
            Weir.Lsp.dropAnalyses ()
            let e = Weir.Lsp.analyzeCached path (lines @ [ "print \"x\"" ])
            Expect.isFalse (obj.ReferenceEquals(d, e)) "a document event drops it"
        }
        test "a vendored strict schema hovers path + provenance + strictness + title, VALUE-pinned" {
            withSchemas (fun entry lines td ->
                let file = System.IO.Path.Combine(td, ".weir", "schemas", "six.json")

                let expected =
                    "schema=six — strict (unknown fields are caught)\n\n"
                    + $"{file}\nsource: https://example.com/six.json\nSix — a test shape"

                // the name and the schema= piece answer alike; `yaml`
                // keeps the district's own hover — zero movement
                Expect.equal (Weir.Lsp.hoverAt entry lines 1 22) (Some expected) "on the name"
                Expect.equal (Weir.Lsp.hoverAt entry lines 1 15) (Some expected) "on the schema= piece"
                Expect.equal (Weir.Lsp.hoverAt entry lines 1 10) (Some "Yaml") "the marker word is untouched")
        }
        test "a permissive schema says unknown-field checking will NOT fire — the other way of the pair" {
            withSchemas (fun entry lines td ->
                let ll = [
                    "let d = yaml schema=loose"
                    "    kind: Service"
                    ""
                    "d |> to yaml |> print"
                ]

                System.IO.File.WriteAllLines(entry, ll)

                match Weir.Lsp.hoverAt entry ll 1 22 with
                | Some hv ->
                    Expect.stringContains
                        hv
                        "permissive: no `additionalProperties: false` anywhere, so unknown-field checking will NOT fire"
                        "the inert kind is named"

                    Expect.stringContains hv "source: not in the lock (hand-placed)" "no lock entry, said plainly"
                | None -> failtest "the permissive schema must answer")
        }
        test "locked-but-missing teaches restore, never-added teaches add — the CHECKER's words" {
            withSchemas (fun entry lines td ->
                let ll = [
                    "let d = yaml schema=ghost"
                    "    kind: Service"
                    ""
                    "d |> to yaml |> print"
                ]

                System.IO.File.WriteAllLines(entry, ll)

                match Weir.Lsp.hoverAt entry ll 1 22 with
                | Some hv -> Expect.stringContains hv "the lock records it; run `weir restore`" "restore taught"
                | None -> failtest "a missing vendored file is exactly when a reader hovers"

                let ln = [
                    "let d = yaml schema=never"
                    "    kind: Service"
                    ""
                    "d |> to yaml |> print"
                ]

                System.IO.File.WriteAllLines(entry, ln)

                match Weir.Lsp.hoverAt entry ln 1 22 with
                | Some hv -> Expect.stringContains hv "add it: weir add schema <url> --as never" "add taught"
                | None -> failtest "an undeclared schema must answer")
        }
        test "a corrupt vendored file says not valid JSON — never the district's type" {
            withSchemas (fun entry lines td ->
                let lb = [ "let d = yaml schema=bad"; "    kind: Service"; ""; "d |> to yaml |> print" ]

                System.IO.File.WriteAllLines(entry, lb)

                match Weir.Lsp.hoverAt entry lb 1 22 with
                | Some hv ->
                    Expect.stringContains hv "not valid JSON" "says why"
                    Expect.isFalse (hv = "Yaml") "the leak stays retired"
                | None -> failtest "an unreadable schema says why, or nothing — it answered nothing")
        }
        test "definition on the name opens the vendored file; not-vendored stays quiet" {
            withSchemas (fun entry lines td ->
                let file = System.IO.Path.Combine(td, ".weir", "schemas", "six.json")

                Expect.equal (Weir.Lsp.definitionTarget entry lines 1 22) (Some(Some file, 1, 1, 0)) "opens it"
                Expect.equal (Weir.Lsp.definitionTarget entry lines 1 10) None "the marker word: quiet as before"

                let ln = [
                    "let d = yaml schema=never"
                    "    kind: Service"
                    ""
                    "d |> to yaml |> print"
                ]

                System.IO.File.WriteAllLines(entry, ln)
                Expect.equal (Weir.Lsp.definitionTarget entry ln 1 22) None "no file to open")
        }
    ]

let lspRenameMemberTests =
    let refs (path: string) (lines: string list) (line: int) (col: int) =
        Weir.Lsp.referencesFor path lines line col true

    let ren (path: string) (lines: string list) (line: int) (col: int) (nn: string) =
        Weir.Lsp.renameFor path lines line col nn

    let applyEdits (lines: string list) (edits: (int * int * int) list) (newName: string) =
        let arr = List.toArray lines

        for l, c, len in edits |> List.sortDescending do
            let t = arr[l - 1]
            arr[l - 1] <- t.Substring(0, c - 1) + newName + t.Substring(c - 1 + len)

        List.ofArray arr

    let nothing =
        "nothing to rename here — put the cursor on a value, a type, a field or a case declared in this file"

    testList "Rename, references and highlight over types, fields and cases [D:lsp-rename-members]" [
        test
            "fields: the declaration word, accesses, literal keys, updates, interpolation, `_.f`; the type: declaration, named literal, adapter slot" {
            let lines = [
                "type Inner = { n: int }"
                "type Pkg = {"
                "    [<Wire \"ID\">]"
                "    id: string"
                "    inner: Option<Inner>"
                "}"
                "let i = { n = 1 }"
                "let p = Pkg { id = \"a\"; inner = Some i }"
                "let q = { p with id = \"b\" }"
                "print p.id"
                "print (show q.inner)"
                "let id = \"x\""
                "let r = { id = id; inner = None }"
                "print r.id"
                "let w = [\"{}\"] |> from json seq<Pkg>"
                "print (show (Seq.length w))"
                "echo $p.id $\"{p.id}\""
                "let s = p |> _.id"
                "print s"
            ]

            let f = "fields.weir"

            let idSites = [
                (4, 5, 2)
                (8, 15, 2)
                (9, 18, 2)
                (10, 9, 2)
                (13, 11, 2)
                (14, 9, 2)
                (17, 9, 2)
                (17, 17, 2)
                (18, 16, 2)
            ]

            Expect.equal
                (refs f lines 4 5)
                (Some idSites)
                "the field from its declaration: every form, never the value side of `{ id = id }`"

            Expect.equal (refs f lines 10 9) (Some idSites) "from an access: the same set"

            Expect.equal
                (refs f lines 13 16)
                (Some [ (12, 5, 2); (13, 16, 2) ])
                "the value `id` in `{ id = id }` is the value binder's"

            Expect.equal (ren f lines 4 5 "key") (Ok idSites) "rename edits every site"

            Expect.equal
                (Weir.Lsp.documentHighlightFor f lines 10 9)
                (Some(idSites |> List.map (fun (l, c, len) -> l, c, len, (if l = 4 then 3 else 2))))
                "highlight: Write at the declaration, Read elsewhere"

            Expect.equal (Weir.Lsp.prepareRenameFor f lines 10 9) (Ok(10, 9, 2)) "prepare: the access's own range"

            Expect.equal
                (refs f lines 5 5)
                (Some [ (5, 5, 5); (8, 25, 5); (11, 15, 5); (13, 20, 5) ])
                "a second field: its own sites"

            Expect.equal
                (refs f lines 2 6)
                (Some [ (2, 6, 3); (8, 9, 3); (15, 33, 3) ])
                "the type: declaration, named literal head, adapter slot"

            Expect.equal
                (refs f lines 1 6)
                (Some [ (1, 6, 5); (5, 19, 5) ])
                "a type referenced inside another declaration"

            Expect.equal (refs f lines 5 19) (Some [ (1, 6, 5); (5, 19, 5) ]) "from the reference: the same set"
            Expect.equal (ren f lines 2 6 "Package") (Ok [ (2, 6, 3); (8, 9, 3); (15, 33, 3) ]) "rename the type"
            Expect.equal (ren f lines 4 5 "ID") (Ok idSites) "the renamed field's own wire key is not a collision"
            Expect.equal (ren f lines 4 5 "_id") (Ok idSites) "a `_`-led field name is a wire name, allowed"
            Expect.equal (ren f lines 4 5 "Key") (Ok idSites) "a field takes either case"

            Expect.equal
                (ren f lines 4 5 "n")
                (Error "'n' is already a field name at line 7, column 11 — pick a name no field in this file uses")
                "another record's field word in the extent (the declaration above it is outside)"

            Expect.equal
                (ren f lines 5 5 "ID")
                (Error "'ID' is already a wire key of Pkg (the field 'id')")
                "another field's wire key (R3)"

            Expect.equal (ren f lines 4 5 "inner") (Error "'inner' is already a field of Pkg") "an existing field"

            Expect.equal
                (ren f lines 2 6 "pkg")
                (Error "'pkg' is not a type name — type names start uppercase")
                "type casing"

            Expect.equal
                (ren f lines 2 6 "Inner")
                (Error "'Inner' is already a type visible here — pick another name")
                "an existing type"

            Expect.equal
                (ren f lines 2 6 "Completed")
                (Error "'Completed' is a builtin type — pick another name")
                "a builtin type"

            Expect.equal (ren f lines 2 6 "Seq") (Error "'Seq' is a module name — pick another name") "a module name"
        }
        test
            "cases: the declaration word, expression position, match patterns; a builtin case refuses; the ambiguous-ctor law" {
            let lines = [
                "type U = | A | B of int"
                "let u = B 1"
                "let k = match u with | A -> 0 | B n -> n"
                "print (show k)"
                "let o = Some k"
                "print (show o)"
            ]

            let f = "cases.weir"

            Expect.equal
                (refs f lines 1 12)
                (Some [ (1, 12, 1); (3, 24, 1) ])
                "a payload-less case: declaration and pattern"

            Expect.equal
                (refs f lines 1 16)
                (Some [ (1, 16, 1); (2, 9, 1); (3, 33, 1) ])
                "a case with a payload: declaration, construction, pattern"

            Expect.equal
                (refs f lines 2 9)
                (Some [ (1, 16, 1); (2, 9, 1); (3, 33, 1) ])
                "from the construction: the same set"

            Expect.equal (ren f lines 1 16 "Bb") (Ok [ (1, 16, 1); (2, 9, 1); (3, 33, 1) ]) "rename the case"

            Expect.equal
                (ren f lines 1 12 "b")
                (Error "'b' is not a case name — case names start uppercase")
                "case casing"

            Expect.equal
                (ren f lines 1 12 "B")
                (Error "'B' is already a case of U — a bare 'B' could name either")
                "a sibling case"

            Expect.equal
                (ren f lines 1 12 "None")
                (Error "'None' is already a case of Option — a bare 'None' could name either")
                "a builtin union's case [D:ambiguous-ctor]"

            Expect.equal (refs f lines 5 9) (Some [ (5, 9, 4) ]) "a builtin case lists this file's uses (R2)"

            Expect.equal
                (ren f lines 5 9 "Sm")
                (Error "'Some' is a case of the builtin Option — nothing in this file declares it")
                "a builtin case refuses"
        }
        test "nested paths: `o.inner.n` and `{ o with inner.n = … }` resolve step-wise" {
            let lines = [
                "type Inner = { n: int }"
                "type Outer = { inner: Inner }"
                "let o = { inner = { n = 1 } }"
                "let o2 = { o with inner.n = 2 }"
                "print (show o.inner.n)"
                "print (show o2)"
            ]

            let f = "nested.weir"

            Expect.equal
                (ren f lines 1 16 "m")
                (Ok [ (1, 16, 1); (3, 21, 1); (4, 25, 1); (5, 21, 1) ])
                "the inner field through both paths"

            Expect.equal
                (ren f lines 2 16 "inside")
                (Ok [ (2, 16, 5); (3, 11, 5); (4, 19, 5); (5, 15, 5) ])
                "the outer field: the path head"
        }
        test "adapter and load slots, a case payload, a sig line: type references" {
            let lines = [
                "type Cfg = { port: int }"
                "type Wrap = | W of Cfg | N"
                "type Pair = { one: Cfg; cfgs: seq<Cfg> }"
                "let c = Env.load Cfg"
                "let a = Args.load Cfg"
                "print (show (c.port + a.port))"
                "let w = W c"
                "print (show w)"
            ]

            let f = "slots.weir"
            let cfgSites = [ (1, 6, 3); (2, 20, 3); (3, 20, 3); (3, 35, 3); (4, 18, 3); (5, 19, 3) ]

            Expect.equal
                (refs f lines 1 6)
                (Some cfgSites)
                "declaration, payload, field types, generic arg, Env.load, Args.load"

            Expect.equal (ren f lines 1 6 "Conf") (Ok cfgSites) "rename the type everywhere"

            Expect.equal
                (ren f lines 1 14 "PORT")
                (Ok [ (1, 14, 4); (6, 16, 4); (6, 25, 4) ])
                "a loaded field renames — the flag and the variable derive from it (R4)"

            Expect.equal
                (ren f lines 1 6 "Pair")
                (Error "'Pair' is already a type visible here — pick another name")
                "a type declared below"

            let m = [ "module"; "type T = { f: int }"; "let mk : int -> T"; "let mk n = { f = n }" ]

            Expect.equal
                (ren "m.weir" m 2 6 "Rec")
                (Ok [ (2, 6, 1); (3, 17, 1) ])
                "a module's signature line references the type"

            Expect.equal (ren "m.weir" m 2 12 "g") (Ok [ (2, 12, 1); (4, 14, 1) ]) "a module's field"
        }
        test "an anonymous record's field is no occurrence and does not refuse; a generic record's field" {
            let lines = [
                "type R = { id: int }"
                "let r = { id = 1 }"
                "let z = {| id = 2 |}.id"
                "print (show (r.id + z))"
            ]

            let f = "anon.weir"

            Expect.equal
                (ren f lines 1 12 "key")
                (Ok [ (1, 12, 2); (2, 11, 2); (4, 16, 2) ])
                "the named record's sites only"

            Expect.equal (refs f lines 3 13) None "the anonymous literal's key"
            Expect.equal (ren f lines 3 23 "key") (Error nothing) "the anonymous access"

            let g = [ "type Box<'a> = { v: 'a }"; "let b = { v = 1 }"; "print (show b.v)" ]

            Expect.equal
                (ren "generic.weir" g 1 18 "w")
                (Ok [ (1, 18, 1); (2, 11, 1); (3, 15, 1) ])
                "a generic record's field"

            Expect.equal (refs "generic.weir" g 1 21) None "a type parameter is no occurrence"
        }
        test "a row-typed access of the field's name refuses, naming the first site (R1)" {
            let lines = [
                "type R = { id: int }"
                "let f r = r.id"
                "let r = { id = 1 }"
                "print (show (f r))"
                "let g = fun { id = x } -> x"
                "print (show (g r))"
            ]

            let f = "row.weir"

            Expect.equal
                (refs f lines 1 12)
                (Some [ (1, 12, 2); (3, 11, 2) ])
                "references: the row-typed sites are not listed"

            Expect.equal (refs f lines 2 13) None "the row-typed access is no occurrence"

            Expect.equal
                (Weir.Lsp.prepareRenameFor f lines 1 12)
                (Error
                    "`r.id` at line 2 is row-typed — it could serve `R` or any record with a field `id`; this rename cannot tell which, so it renames nothing. Annotate the call or rename by hand.")
                "prepare refuses, naming the first site"

            Expect.equal (ren f lines 1 6 "S") (Ok [ (1, 6, 1) ]) "the type itself still renames"
        }
        test "refusals: builtin member, imported member, error in the extent, comment, keyword, wildcard" {
            let dir =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-rename-m-" + string (System.Guid.NewGuid()))

            System.IO.Directory.CreateDirectory dir |> ignore

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(dir, "lib.weir"),
                [ "module"; "type T = { f: int }"; "let mk : int -> T"; "let mk n = { f = n }" ]
            )

            let main = System.IO.Path.Combine(dir, "main.weir")
            let imp = [ "import \"./lib.weir\" as Lib"; "let t = Lib.mk 1"; "print (show t.f)" ]
            Expect.equal (refs main imp 3 15) (Some [ (3, 15, 1) ]) "an imported field lists this file's uses (R2)"

            Expect.equal
                (ren main imp 3 15 "g")
                (Error "'f' is a field of Lib.T — rename it in that module")
                "an imported field"

            let b = [ "let c = echo hi | complete"; "print (show c.exitCode)" ]

            Expect.equal
                (ren "b.weir" b 2 15 "code")
                (Error "'exitCode' is a field of the builtin Completed — nothing in this file declares it")
                "a builtin record's field"

            let e = [
                "type R = { id: int }"
                "let r = { id = 1 }"
                "print (show r.id)"
                "let bad = 1 + \"s\""
            ]

            Expect.equal
                (ren "e.weir" e 1 12 "key")
                (Error "a statement after 'R's declaration has an error at line 4 — fix it first")
                "an error anywhere after the declaration (R5)"

            let c = [
                "type R = { id: int } // the R id"
                "let r = { id = 1 }"
                "print (show r.id)"
            ]

            Expect.equal (ren "c.weir" c 1 25 "x") (Error nothing) "a comment mention of the type"
            Expect.equal (ren "c.weir" c 1 29 "x") (Error nothing) "a comment mention of the field"
            Expect.equal (ren "c.weir" c 2 10 "x") (Error nothing) "punctuation"
            Expect.equal (ren "c.weir" c 1 1 "x") (Error "'type' is a keyword, not a name") "a keyword"

            Expect.equal
                (ren "c.weir" c 1 12 "_")
                (Error "'_' is the wildcard, not a name")
                "the wildcard as the new name"

            Expect.equal (ren "c.weir" c 1 12 "id") (Error "'id' is already the name") "the same name"

            Expect.equal
                (ren "c.weir" c 1 12 "9x")
                (Error
                    "'9x' is not a name — a name starts with a letter or '_' and continues with letters, digits or '_'")
                "not an identifier"

            Expect.equal (ren "c.weir" c 1 12 "if") (Error "'if' is a keyword, not a name") "a keyword as the new name"
        }
        test "a [<Wire>]-bearing field renames with its attribute line untouched and `to json` unchanged" {
            let lines = [
                "type Pkg = {"
                "    [<Wire \"ID\">]"
                "    id: string"
                "}"
                "let p = { id = \"a\" }"
                "p |> to json |> print"
            ]

            let edits = ren "wire.weir" lines 3 5 "key"
            Expect.equal edits (Ok [ (3, 5, 2); (5, 11, 2) ]) "the declaration word and the literal key"
            let renamed = applyEdits lines (Result.defaultValue [] edits) "key"
            Expect.equal (List.item 1 renamed) "    [<Wire \"ID\">]" "the attribute line is untouched"
            Expect.equal (List.item 2 renamed) "    key: string" "the field word"

            let runCapture (ls: string list) =
                let path = tempPath "weir-rename-wire" + ".weir"
                System.IO.File.WriteAllLines(path, ls)
                let out = new System.IO.StringWriter()
                let prior = System.Console.Out
                System.Console.SetOut out

                try
                    let code = Weir.Script.run path []
                    code, out.ToString().Replace("\r\n", "\n")
                finally
                    System.Console.SetOut prior
                    System.IO.File.Delete path

            Expect.equal (runCapture lines) (0, "{\"ID\":\"a\"}\n") "before: the wire key"
            Expect.equal (runCapture renamed) (0, "{\"ID\":\"a\"}\n") "after: the same wire"
        }
        test "binderSitesFor lists member declaration words beside value binders" {
            let lines = [
                "type U = | A | B of int"
                "type R = { id: int }"
                "let r = { id = 1 }"
                "print (show r.id)"
            ]

            Expect.equal
                (Weir.Lsp.binderSitesFor "sites.weir" lines)
                [
                    (1, 6, false)
                    (1, 12, false)
                    (1, 16, false)
                    (2, 6, false)
                    (2, 12, false)
                    (3, 5, true)
                ]
                "types, cases and fields are member sites; the let is a value site"
        }
    ]

let lspRenameTests =
    let refs (lines: string list) (line: int) (col: int) =
        Weir.Lsp.referencesFor "refs" lines line col true

    testList "References, highlight, rename [D:lsp-rename]" [
        test
            "referencesFor: a top-level binder owns its uses up to the next rebinding; a param shadows inside its lambda" {
            let lines = [
                "let x = 1"
                "print (show x)"
                "let x = 2"
                "print (show x)"
                "let f x = x + 1"
                "print (show (f x))"
                "// x in a comment"
            ]

            Expect.equal
                (refs lines 1 5)
                (Some [ (1, 5, 1); (2, 13, 1) ])
                "the first x: its let and the use before the rebinding"

            Expect.equal
                (refs lines 4 13)
                (Some [ (3, 5, 1); (4, 13, 1); (6, 16, 1) ])
                "the second x: never the first's"

            Expect.equal (refs lines 5 11) (Some [ (5, 7, 1); (5, 11, 1) ]) "inside f: the param's set only"
            Expect.equal (refs lines 6 16) (Some [ (3, 5, 1); (4, 13, 1); (6, 16, 1) ]) "outside f: the top-level's"
            Expect.equal (refs lines 7 4) None "a comment is no occurrence"

            Expect.equal
                (Weir.Lsp.referencesFor "refs" lines 1 5 false)
                (Some [ (2, 13, 1) ])
                "includeDeclaration=false drops the binder site"

            Expect.equal
                (Weir.Lsp.documentHighlightFor "refs" lines 1 5)
                (Some [ (1, 5, 1, 3); (2, 13, 1, 2) ])
                "highlight: Write at the binder, Read at the use"
        }
        test "referencesFor: interpolation holes, heredocs, argv splices and splats — sigil-stripped columns" {
            let lines = [
                "let s = \"v\""
                "let xs = [\"a\"]"
                "echo $s $@xs (s) $\"{s}\""
                "let h = $<<<"
                "    {s}"
                "    >>>"
                "let h2 = $$<<<"
                "    $s ${s}"
                "    >>>"
                "print h"
                "print h2"
            ]

            Expect.equal
                (refs lines 1 5)
                (Some [
                    (1, 5, 1)
                    (3, 7, 1)
                    (3, 15, 1)
                    (3, 21, 1)
                    (5, 6, 1)
                    (8, 6, 1)
                    (8, 10, 1)
                ])
                "$s names the s after the sigil; (s) the s inside the parens; every hole counts"

            Expect.equal (refs lines 2 5) (Some [ (2, 5, 2); (3, 11, 2) ]) "$@xs names the xs after the sigil"
        }
        test "referencesFor: yaml district splices, key splices and for binders" {
            let lines = [
                "let name = \"n\""
                "let items = [1; 2]"
                "let pairs = [(\"k\", 1)]"
                "let y = yaml"
                "    kind: $name"
                "    $name: 1"
                "    list:"
                "        for i in items"
                "            - $i"
                "    labels:"
                "        for (k, v) in pairs"
                "            $k: $v"
                "    e: $(name)"
                "y |> to yaml |> print"
            ]

            Expect.equal
                (refs lines 1 5)
                (Some [ (1, 5, 4); (5, 12, 4); (6, 6, 4); (13, 10, 4) ])
                "value splice, key splice and $(expr) splice"

            Expect.equal (refs lines 9 16) (Some [ (8, 13, 1); (9, 16, 1) ]) "the sequence for binder and its item"
            Expect.equal (refs lines 11 14) (Some [ (11, 14, 1); (12, 14, 1) ]) "the map for's tuple binder and its key"
        }
        test "referencesFor: pattern binders — let-tuple, case payload, lambda tuple, for tuple, regex captures" {
            let lines = [
                "type V = | Push of int | Pop"
                "let (a, b) = (1, 2)"
                "print (show (a + b))"
                "match Push 1 with"
                "| Push n -> print (show n)"
                "| Pop -> ()"
                "let g = fun (k, v) -> show k + show v"
                "print (g (1, 2))"
                "for (k, v) in [(1, 2)] do"
                "    print (show (k + v))"
                "match \"a=1\" with"
                "| Regex @\"(\\w+)=(\\d+)\" (key, num) -> print $\"{key}{num}\""
                "| _ -> ()"
            ]

            Expect.equal (refs lines 2 6) (Some [ (2, 6, 1); (3, 14, 1) ]) "let (a, b): a"
            Expect.equal (refs lines 5 8) (Some [ (5, 8, 1); (5, 25, 1) ]) "| Push n: n"
            Expect.equal (refs lines 7 14) (Some [ (7, 14, 1); (7, 28, 1) ]) "fun (k, v): k"
            Expect.equal (refs lines 9 6) (Some [ (9, 6, 1); (10, 18, 1) ]) "for (k, v): k — not the lambda's k"
            Expect.equal (refs lines 12 25) (Some [ (12, 25, 3); (12, 47, 3) ]) "a regex capture binder"
        }
        test "referencesFor: `with` source counts, `_.field` is silent, a builtin lists this file's uses (R2)" {
            let lines = [
                "type R = { a: int }"
                "let r = { a = 1 }"
                "let r2 = { r with a = 2 }"
                "print (show r2.a)"
                "[r] |> Seq.map _.a |> Seq.iter print"
            ]

            Expect.equal (refs lines 2 5) (Some [ (2, 5, 1); (3, 12, 1); (5, 2, 1) ]) "the update's source is a use"
            Expect.equal (refs lines 5 16) None "_.a: the wildcard is no name"
            Expect.equal (refs lines 4 1) (Some [ (4, 1, 5); (5, 32, 5) ]) "print: no binder here, the uses still list"
            Expect.equal (refs lines 5 8) (Some [ (5, 8, 7) ]) "Seq.map likewise"

            Expect.equal
                (refs lines 4 16)
                (Some [ (1, 12, 1); (2, 11, 1); (3, 19, 1); (4, 16, 1); (5, 18, 1) ])
                "a field: its declaration, literal key, update key and accesses [D:lsp-rename-members]"
        }
        test "referencesFor: within/until binders, and a signed member's signature line" {
            let within = [ "let d = \"top\""; "within tmp d"; "    print d"; "print d" ]
            Expect.equal (refs within 2 12) (Some [ (2, 12, 1); (3, 11, 1) ]) "the within binder's scope"
            Expect.equal (refs within 1 5) (Some [ (1, 5, 1); (4, 7, 1) ]) "the top-level d outside it"

            let m = [
                "module"
                "/// doc"
                "let f : int -> int"
                "let f x = x + 1"
                "let g = f 2"
                "let _ = g"
            ]

            Expect.equal (refs m 3 5) (Some [ (3, 5, 1); (4, 5, 1); (5, 9, 1) ]) "from the sig: sig, impl, use"
            Expect.equal (refs m 5 9) (Some [ (3, 5, 1); (4, 5, 1); (5, 9, 1) ]) "from the use: the same set"
        }
        test "renameFor: a shadowed name edits only its scope; edits cover every occurrence form" {
            let ren (lines: string list) (line: int) (col: int) (nn: string) =
                Weir.Lsp.renameFor "rename.weir" lines line col nn

            let sh = [
                "let x = 1"
                "let f x = x + 1"
                "print (show (f x))"
                "let x = 3"
                "print (show x)"
            ]

            Expect.equal (ren sh 2 7 "y") (Ok [ (2, 7, 1); (2, 11, 1) ]) "the param: its lambda only"
            Expect.equal (ren sh 1 5 "y") (Ok [ (1, 5, 1); (3, 16, 1) ]) "the first top-level: up to the rebinding"
            Expect.equal (ren sh 4 5 "y") (Ok [ (4, 5, 1); (5, 13, 1) ]) "the rebinding: its own uses"

            let cov = [
                "let s = \"v\""
                "let xs = [s]"
                "echo $s $@xs $\"{s}\""
                "let y = yaml"
                "    k: $s"
                "    $s: 1"
                "y |> to yaml |> print"
                "// s stays"
            ]

            Expect.equal
                (ren cov 1 5 "t")
                (Ok [ (1, 5, 1); (2, 11, 1); (3, 7, 1); (3, 17, 1); (5, 9, 1); (6, 6, 1) ])
                "binder, list use, splice, hole, yaml splice and key splice — never the comment"

            Expect.equal (ren cov 2 5 "ys") (Ok [ (2, 5, 2); (3, 11, 2) ]) "the splat's name after $@"

            let m = [
                "module"
                "let s : string"
                "let s = \"v\""
                "let shout : unit -> string"
                "let shout () = $\"{s}!\""
            ]

            Expect.equal
                (ren m 3 5 "t")
                (Ok [ (2, 5, 1); (3, 5, 1); (5, 19, 1) ])
                "a signed member: the sig line renames too"

            Expect.equal (Weir.Lsp.prepareRenameFor "rename.weir" sh 3 16) (Ok(3, 16, 1)) "prepare: the use's own range"
        }
        test "renameFor refusals, each with its reason [D:lsp-rename]" {
            let dir =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-rename-" + string (System.Guid.NewGuid()))

            System.IO.Directory.CreateDirectory dir |> ignore

            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(dir, "lib.weir"),
                [ "module"; "let f : int -> int"; "let f x = x + 1" ]
            )

            let main = System.IO.Path.Combine(dir, "main.weir")

            let lines = [
                "import \"./lib.weir\" as Lib"
                "type R = { a: int }"
                "type U = | A | B"
                "let r = { a = 1 }"
                "print (show (Lib.f r.a))"
                "let u = A"
                "print (show u)"
                "let k = if r.a > 0 then 1 else 2"
                "print (show k)"
            ]

            let ren (line: int) (col: int) (nn: string) =
                Weir.Lsp.renameFor main lines line col nn

            Expect.equal
                (ren 5 1 "q")
                (Error "'print' is not bound in this file — a builtin or an imported name renames where it is defined")
                "builtin"

            // a `_`-led new name: the occurrence table hides `_` binders, so
            // a clash with one is invisible — refused rather than guessed
            Expect.equal
                (ren 4 5 "_r")
                (Error
                    "'_r' starts with '_', and weir tracks no occurrence of a '_'-led name — a rename onto one cannot be checked for a clash; pick a name without the leading '_'")
                "underscore target"

            Expect.equal
                (ren 5 14 "q")
                (Error "'Lib.f' is not bound in this file — a builtin or an imported name renames where it is defined")
                "module member"

            Expect.equal
                (ren 5 22 "q")
                (Ok [ (2, 12, 1); (4, 11, 1); (5, 22, 1); (8, 14, 1) ])
                "a field renames from a use [D:lsp-rename-members]"

            Expect.equal (ren 6 9 "q") (Error "'q' is not a case name — case names start uppercase") "a case"
            Expect.equal (ren 2 6 "q") (Error "'q' is not a type name — type names start uppercase") "a type name"

            Expect.equal (ren 8 9 "q") (Error "'if' is a keyword, not a name") "a keyword under the cursor"
            Expect.equal (ren 4 5 "_") (Error "'_' is the wildcard, not a name") "target _"
            Expect.equal (ren 4 5 "if") (Error "'if' is a keyword, not a name") "keyword target"

            Expect.equal
                (ren 4 5 "9x")
                (Error
                    "'9x' is not a name — a name starts with a letter or '_' and continues with letters, digits or '_'")
                "not an identifier"

            Expect.isTrue
                (match ren 4 5 "Q" with
                 | Error m -> m.StartsWith "binding names start lowercase"
                 | Ok _ -> false)
                "uppercase target: the casing teaching"

            Expect.isTrue
                (match ren 4 5 "print" with
                 | Error m -> m.StartsWith "'print' is a builtin with no qualified spelling"
                 | Ok _ -> false)
                "reserved target: the reserve teaching"

            Expect.equal
                (ren 4 5 "k")
                (Error "'k' is already used at line 8, column 5 — pick a name that is new in 'r's scope")
                "collision: the new name is bound inside the scope"

            Expect.equal (ren 4 5 "r") (Error "'r' is already the name") "the same name"
            Expect.equal (ren 4 5 "z") (Ok [ (4, 5, 1); (5, 20, 1); (8, 12, 1) ]) "a fresh name renames"

            let inner = [
                "let a = 1"
                "let f b = b + 1"
                "print (show (f a))"
                "let g c = c + a"
                "print (show (g 2))"
            ]

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" inner 1 5 "b")
                (Error "'b' is already used at line 2, column 7 — pick a name that is new in 'a's scope")
                "collision: the new name is bound in an inner scope"

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" inner 2 7 "a")
                (Error
                    "'a' is already bound here (a builtin, or a binding above line 2) — pick a name that is new in 'b's scope")
                "collision: the new name is in the env at the binder"

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" inner 4 7 "a")
                (Error "'a' is already used at line 4, column 15 — pick a name that is new in 'c's scope")
                "collision: the new name is used inside the scope"

            let part = [ "let a = 1"; "let bad = a + \"s\""; "print (show a)" ]

            Expect.equal
                (Weir.Lsp.prepareRenameFor "rename.weir" part 1 5)
                (Error
                    "a statement in 'a's scope has an error at line 2 — fix it first, so no use of the name is missed")
                "a statement in scope failed to check: prepare refuses"

            Expect.equal
                (Weir.Lsp.renameFor
                    "rename.weir"
                    [ "let a = 1"; "print (show a)"; "let a = 2"; "let bad = a + \"s\"" ]
                    1
                    5
                    "z")
                (Ok [ (1, 5, 1); (2, 13, 1) ])
                "an error past the rebinding is outside the scope"

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" [ "#init"; "let a = 1" ] 2 5 "z")
                (Error
                    "rename is not available in an init file — its #session and #alias lines bind names the loader owns")
                "init-marked file"

            Expect.equal
                (Weir.Lsp.renameFor
                    (System.IO.Path.Combine(dir, ".weir", "sigs", "tool.weir"))
                    [ "type T = { a: int }" ]
                    1
                    6
                    "z")
                (Error "rename is not available in a signature file — its members are the contract a #sig reads by name")
                "sig contract file"

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" [ "let a = 1 // a here"; "print (show a)" ] 1 14 "z")
                (Error
                    "nothing to rename here — put the cursor on a value, a type, a field or a case declared in this file")
                "a comment mention"
        }
        test "rename reaches T.create and a type inside an anonymous adapter type [D:lsp-rename-members]" {
            let email = [
                "type Email = Email of string"
                "    validate s = if not (Str.contains \"@\" s) then fail $\"bad: {s}\""
                "let (Email raw) = Email.create \"a@b\""
                "print raw"
            ]

            Expect.equal (Weir.Lsp.renameFor "rename.weir" email 1 6 "Mail") (Ok [ (1, 6, 5); (3, 19, 5) ]) "T.create"

            let anon = [
                "type User = { name: string }"
                "let r = [\"{\\\"users\\\": []}\"] |> from json {| users: seq<User> |}"
                "print $\"{r.users}\""
            ]

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" anon 1 6 "Person")
                (Ok [ (1, 6, 4); (2, 56, 4) ])
                "the adapter slot"
        }
        test
            "a length-changing rename inside a column-aligned layout is refused, never left broken [D:lsp-rename-aligned]" {
            let aligned = [
                "type P = { x: int; y: int }"
                "let pt = { x = 1"
                "           y = 2 }"
                "print $\"{pt.x}\""
            ]

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" aligned 2 5 "point")
                (Error
                    "renaming 'pt' to 'point' would break the column alignment at line 3 — a rename does not re-align other lines; pick a name of the same length, or rename by hand and re-align")
                "refused, located"

            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" aligned 2 5 "pq")
                (Ok [ (2, 5, 2); (4, 10, 2) ])
                "same length renames"
        }
        test "rename takes the parser's digits only: a non-ASCII digit is no name" {
            Expect.equal
                (Weir.Lsp.renameFor "rename.weir" [ "let x = 1"; "print (show x)" ] 1 5 "x\u0661")
                (Error
                    "'x\u0661' is not a name — a name starts with a letter or '_' and continues with letters, digits or '_'")
                "Arabic-Indic one"
        }
        test "references on an imported case meet across expression and pattern uses [D:qualified-case-patterns]" {
            let dir =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-refs-{System.Guid.NewGuid():N}")

            System.IO.Directory.CreateDirectory dir |> ignore

            try
                System.IO.File.WriteAllLines(
                    System.IO.Path.Combine(dir, "lib.weir"),
                    [
                        "module"
                        ""
                        "type Pt = { x: int; y: int }"
                        ""
                        "type Shape ="
                        "    | Circle of int"
                        "    | Square of Pt"
                    ]
                )

                let main = System.IO.Path.Combine(dir, "main.weir")

                let lines = [
                    "import \"./lib.weir\" as Lib"
                    "let a = { x = 1; y = 2 }"
                    "let s1 = Lib.Square a"
                    "let s2 = Lib.Square a"
                    "let f sh ="
                    "    match sh with"
                    "    | Lib.Square q -> q.x"
                    "    | Lib.Circle r -> r"
                    "print $\"{f s1}{f s2}\""
                ]

                let all = Some [ (3, 14, 6); (4, 14, 6); (7, 11, 6) ]
                Expect.equal (Weir.Lsp.referencesFor main lines 3 15 true) all "from an expression use"
                Expect.equal (Weir.Lsp.referencesFor main lines 7 12 true) all "from a pattern use"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "corpus: bindingUsage's free set equals occurrences' TopLevel set, statement by statement" {
            let root = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..")

            let files =
                [ "examples"; "ci"; "tools" ]
                |> List.collect (fun d ->
                    System.IO.Directory.GetFiles(System.IO.Path.Combine(root, d), "*.weir")
                    |> Array.toList)

            Expect.isGreaterThan files.Length 20 "the corpus is there"
            let mutable statements = 0

            for f in files do
                let lines = System.IO.File.ReadAllLines f |> Array.toList
                let _, stmts, _, _, _ = Weir.Analyze.analyzeLines f lines

                for _, chk in stmts do
                    match chk.Kind with
                    | Weir.Statements.KLet(_, _, te)
                    | Weir.Statements.KLetPat(_, _, te)
                    | Weir.Statements.KCmd te
                    | Weir.Statements.KExpr te ->
                        statements <- statements + 1
                        let free = Weir.Check.bindingUsage te |> fst |> Set.ofSeq

                        let top =
                            Weir.Check.occurrences te
                            |> List.choose (fun o ->
                                match o.Binder with
                                | Weir.Check.TopLevel n -> Some n
                                | Weir.Check.Local _ -> None)
                            |> Set.ofList

                        Expect.equal top (Set.filter Weir.Check.occurrenceName free) $"{f}: the two walks agree"
                    | _ -> ()

            Expect.isGreaterThan statements 200 "the corpus has statements"
        }
    ]

// ---- editor polish [PLAN-lsp-editor-polish]: inlay hints, symbols,
// folding, links, tags — unit pins through the pure functions
let lspPolishTests =
    let hints lines =
        Weir.Lsp.inlayHintsFor Weir.Lsp.defaultInlayOptions "hints" lines 1 1000

    let brief (h: Weir.Lsp.InlayHint) = h.Line, h.Col, h.Label, h.Kind

    // a project tree: a module, a sig, a vendored schema, the entry
    let withPolishTree (f: string -> string list -> string -> string -> string -> unit) =
        let td = tempPath "weir-polish"

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(td, ".weir", "sigs"))
        |> ignore

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(td, ".weir", "schemas"))
        |> ignore

        let lib = System.IO.Path.Combine(td, "lib.weir")

        System.IO.File.WriteAllLines(
            lib,
            [|
                "module Lib"
                ""
                "/// doubles a number"
                "let double : int -> int"
                ""
                "let double n = n * 2"
            |]
        )

        let sigp = System.IO.Path.Combine(td, ".weir", "sigs", "mytool.weir")

        System.IO.File.WriteAllLines(
            sigp,
            [|
                "module Mytool"
                "let version = \"mytool 1.0\""
                "let exhaustive = true"
                "type Cmd = {"
                "    /// run without side effects"
                "    dryRun: bool"
                "}"
            |]
        )

        let schema = System.IO.Path.Combine(td, ".weir", "schemas", "six.json")

        System.IO.File.WriteAllText(
            schema,
            """{ "type": "object", "properties": { "a": { "type": "integer" } }, "additionalProperties": false }"""
        )

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(td, ".weir", "lock.json"),
            """{ "artifacts": [ { "kind": "schema", "name": "six", "url": "https://example.com/six.json", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "path": "schemas/six.json" } ] }"""
        )

        let entry = System.IO.Path.Combine(td, "main.weir")

        let entryLines = [
            "#sig mytool"
            "module Main"
            "import \"./lib.weir\" as Lib"
            ""
            "/// the config"
            "type Cfg = { Host: string; Port: int }"
            ""
            "type Verdict ="
            "    | Good"
            "    | Bad of int"
            ""
            "type Email = Email of string"
            "    validate s ="
            "        if not (Str.contains \"@\" s) then fail \"no\""
            ""
            "/// doubles twice"
            "let quad : int -> int"
            "let quad n = Lib.double (Lib.double n)"
            ""
            "let answer : int"
            "let answer = 42"
            ""
            "let orphan : string"
            ""
            "let pair : int * int"
            "let pair = 1, 2"
            ""
            "let outer : int -> int"
            "let outer x ="
            "    let inner y = y + x"
            "    inner 1"
            ""
            "let broken : int"
            "let broken = 1 + \"a\""
        ]

        try
            f entry entryLines lib sigp schema
        finally
            System.IO.Directory.Delete(td, true)

    testList "LSP editor polish [PLAN-lsp-editor-polish]" [
        test "inlay hints: a value let shows its type after the binder [D:inlay-hints]" {
            Expect.equal
                (hints [ "let x = [1]" ])
                [
                    {
                        Line = 1
                        Col = 6
                        Label = ": seq<int>"
                        Tooltip = None
                        Kind = 1
                        PaddingLeft = false
                        PaddingRight = false
                    }
                ]
                ""
        }
        test "inlay hints: a function hints each param (Kind 2) and the return before `=`" {
            let hs = hints [ "let f x y = Str.length x + y"; "print (show (f \"a\" 2))" ]

            Expect.equal
                (hs |> List.map brief)
                [ (1, 8, ": string", 2); (1, 10, ": int", 2); (1, 11, ": int", 1) ]
                "two param hints, one return hint"

            Expect.isTrue (hs |> List.last).PaddingRight "the return hint pads before the ="
        }
        test "inlay hints: inner let and a lambda param over ls [D:inlay-hints]" {
            let lines = [
                "let go () ="
                "    let xs = ls"
                "    xs |> Seq.iter (fun r -> print r.name)"
                "go ()"
            ]

            Expect.equal
                (hints lines |> List.map brief)
                [ (1, 11, ": unit", 1); (2, 11, ": seq<FileRow>", 1); (3, 26, ": FileRow", 2) ]
                ""
        }
        test "inlay hints: a signed module member gets no hint at all [D:module-signatures]" {
            Expect.equal
                (hints [
                    "module M"
                    "let double : int -> int"
                    "let double n = n * 2"
                    "let v : int"
                    "let v = 1"
                ])
                []
                "the signature is the annotation"
        }
        test "inlay hints: a broken statement hints nothing, its neighbours still do (partial truth)" {
            Expect.equal
                (hints [ "let a = 1 + \"s\""; "let b = 2"; "print (show b)" ] |> List.map brief)
                [ (2, 6, ": int", 1) ]
                ""
        }
        test "inlay hints: the range excludes a statement outside it (range discipline)" {
            let lines = [ "let a = 1"; "let b = \"s\""; "print $\"{a}{b}\"" ]

            Expect.equal
                (Weir.Lsp.inlayHintsFor Weir.Lsp.defaultInlayOptions "hints" lines 2 2
                 |> List.map brief)
                [ (2, 6, ": string", 1) ]
                ""
        }
        test "inlay hints: a long type truncates at MaxLabel with the full type as tooltip [R2]" {
            let lines = [
                "let r = {| ip = \"1\"; name = \"n\"; port = 1; zone = \"z\"; host = \"h\" |}"
                "print (show r)"
            ]

            let full = Weir.Lsp.hoverType lines 1 5 |> Option.get
            let full = full.Substring("r : ".Length)
            Expect.isGreaterThan full.Length 40 "the fixture is long"

            match hints lines with
            | [ h ] ->
                Expect.equal h.Label (": " + full.Substring(0, 39) + "…") "cut with an ellipsis"
                Expect.equal h.Tooltip (Some full) "the full type rides in the tooltip"
            | other -> failtest $"one hint expected: {other}"
        }
        test "inlay hints: a multi-line let hints on the binder's physical line (verbatim anchor)" {
            Expect.equal
                (hints [ "let x ="; "    [1; 2]"; "print (show x)" ] |> List.map brief)
                [ (1, 6, ": seq<int>", 1) ]
                ""
        }
        test
            "inlay hints: a rewritten #session field is no binder — no hint (verbatim against RAW lines) [D:init-marker]" {
            let lines = [ "#init"; "let p () = \"> \""; ""; "#session {"; "    prompt = p"; "}" ]

            let hs = hints lines
            Expect.isFalse (hs |> List.exists (fun h -> h.Line = 5)) $"no hint on the field line: {hs}"
            Expect.equal (hs |> List.map brief) [ (2, 10, ": string", 1) ] "the declaration still hints"
        }
        test "inlay hints: pipeline-stage hints are off by default, opt-in on multi-line chains only [R1]" {
            let lines = [
                "let n ="
                "    [1; 2]"
                "    |> Seq.map (fun i -> i + 1)"
                "    |> Seq.length"
                "let m = [1] |> Seq.length"
                "print $\"{n + m}\""
            ]

            Expect.isFalse (hints lines |> List.exists (fun h -> h.PaddingLeft)) "no stage hints by default"

            let on =
                Weir.Lsp.inlayHintsFor
                    {
                        Weir.Lsp.defaultInlayOptions with
                            PipelineTypes = true
                    }
                    "hints"
                    lines
                    1
                    100

            Expect.equal
                (on |> List.filter (fun h -> h.PaddingLeft) |> List.map brief)
                [ (3, 32, ": seq<int>", 1); (4, 18, ": int", 1) ]
                "each multi-line stage at its line end; the single-line pipe gets nothing"
        }
        test "inlay hints: stages sharing a line give that line one hint, its last stage's type [D:pipeline-hint-line]" {
            let lines = [
                "let n ="
                "    [1; 2]"
                "    |> Seq.map (fun i -> i + 1) |> Seq.map show |> Seq.length"
                "print $\"{n}\""
            ]

            let on =
                Weir.Lsp.inlayHintsFor
                    {
                        Weir.Lsp.defaultInlayOptions with
                            PipelineTypes = true
                    }
                    "hints"
                    lines
                    1
                    100

            Expect.equal
                (on |> List.filter (fun h -> h.PaddingLeft) |> List.map (fun h -> h.Label))
                [ ": int" ]
                "one hint, not one per stage"
        }
        test "invariant: hint == hover at the same binder" {
            let lines = [
                "let f x y = Str.length x + y"
                "let go () ="
                "    let xs = ls"
                "    xs |> Seq.iter (fun r -> print r.name)"
                "print (show (f \"a\" 2))"
                "go ()"
            ]

            for h in hints lines do
                if not h.PaddingRight then
                    // the hint sits after the word; hover on the word's last char
                    let hover = Weir.Lsp.hoverType lines h.Line (h.Col - 1) |> Option.get
                    let hover = hover.Split('\n').[0]
                    let ty = h.Label.Substring 2

                    Expect.isTrue
                        (hover = ty || hover.EndsWith(" : " + ty))
                        $"hint {h.Label} at {h.Line}:{h.Col} vs hover {hover}"
        }
        test "inlayOptionsOf: absent keys keep the defaults, present ones override" {
            let j =
                System.Text.Json.JsonDocument.Parse("""{"bindingTypes":false,"maxLabelLength":12}""")

            Expect.equal
                (Weir.Lsp.inlayOptionsOf j.RootElement)
                {
                    Weir.Lsp.defaultInlayOptions with
                        BindingTypes = false
                        MaxLabel = 12
                }
                ""

            Expect.equal
                (Weir.Lsp.inlayOptionsOf (System.Text.Json.JsonDocument.Parse("{}").RootElement))
                Weir.Lsp.defaultInlayOptions
                "empty = defaults"
        }
        test "document symbols: the outline of a module [D:document-symbols]" {
            withPolishTree (fun entry lines _ _ _ ->
                let syms = Weir.Lsp.documentSymbolsFor entry lines
                let names = syms |> List.map (fun s -> s.Name, s.Kind)

                Expect.equal
                    names
                    [
                        ("mytool", 11)
                        ("Main", 2)
                        ("Lib", 2)
                        ("Cfg", 23)
                        ("Verdict", 10)
                        ("Email", 10)
                        ("quad", 12)
                        ("answer", 13)
                        ("orphan", 13)
                        ("pair", 13)
                        ("outer", 12)
                        ("broken", 13)
                    ]
                    "names and kinds, in file order"

                let by n = syms |> List.find (fun s -> s.Name = n)

                Expect.equal
                    (by "mytool").Detail
                    (Some(System.IO.Path.Combine(".weir", "sigs", "mytool.weir")))
                    "the sig file, relative"

                Expect.equal (by "Main").Detail (Some "module header") ""
                Expect.equal (by "Lib").Detail (Some "\"./lib.weir\"") "the path as written"

                Expect.equal
                    ((by "Cfg").Children |> List.map (fun c -> c.Name, c.Kind, c.Detail))
                    [ ("Host", 8, Some "string"); ("Port", 8, Some "int") ]
                    "record fields"

                Expect.equal
                    ((by "Verdict").Children |> List.map (fun c -> c.Name, c.Kind, c.Detail))
                    [ ("Good", 22, None); ("Bad", 22, Some "int") ]
                    "union cases"

                Expect.equal
                    ((by "Email").Children |> List.map (fun c -> c.Name, c.Kind))
                    [ ("Email", 22); ("validate", 6) ]
                    "the validate block is a method"

                // R4: the signature attaches — the range starts at the sig line
                let q = by "quad"
                let l1, _, l2, _ = q.Range
                Expect.equal (l1, l2) (17, 18) "sig line .. impl line"
                Expect.equal q.Selection (18, 5, 18, 9) "the binder on the impl line"
                Expect.equal q.Detail (Some "quad (n: int) : int") "the declared type, annotated"
                // an orphan keeps its own symbol
                Expect.equal (by "orphan").Detail (Some "signature — no implementation") ""
                // R5: a nested function is a child; the broken let is a textual fallback
                Expect.equal
                    ((by "outer").Children |> List.map (fun c -> c.Name, c.Kind, c.Detail))
                    [ ("inner", 12, Some "inner (y: int) : int") ]
                    ""

                Expect.equal (by "broken").Detail None "no detail for a failed statement"
                // invariant: detail == hover's first line for the lets
                for n in [ "quad"; "answer"; "pair"; "outer" ] do
                    let s = by n
                    let _, _, l2, _ = s.Range
                    let sl, sc, _, _ = s.Selection
                    let hover = Weir.Lsp.hoverAt entry lines sl sc |> Option.get
                    // the purity badge is hover residue, not the signature
                    let first = hover.Split('\n').[0].Replace("  (pure)", "")
                    Expect.equal (Some first) s.Detail $"detail == hover for {n} (line {l2})")
        }
        test "document symbols: a script's value let, pattern let and nested function" {
            let lines = [
                "let answer = 42"
                "let a, b = 1, \"s\""
                "let outer x ="
                "    let inner y = y + x"
                "    let k = 1"
                "    inner k"
                "print $\"{answer + a + outer 1}{b}\""
            ]

            let syms = Weir.Lsp.documentSymbolsFor "syms" lines

            Expect.equal
                (syms |> List.map (fun s -> s.Name, s.Kind, s.Detail, s.Range))
                [
                    ("answer", 13, Some "answer : int", (1, 1, 1, 16))
                    ("a", 13, Some "int", (2, 1, 2, 18))
                    ("b", 13, Some "string", (2, 1, 2, 18))
                    ("outer", 12, Some "outer (x: int) : int", (3, 1, 6, 12))
                ]
                ""

            Expect.equal
                ((syms |> List.last).Children
                 |> List.map (fun c -> c.Name, c.Kind, c.Range, c.Selection))
                [ ("inner", 12, (4, 9, 4, 24), (4, 9, 4, 14)) ]
                "the inner function only — the inner value let is not a symbol"
        }
        test "folding ranges: statements, arms, comment runs, imports, districts [D:folding-ranges]" {
            let lines = [
                "/// doc one"
                "/// doc two"
                "// plain one"
                "// plain two"
                "import \"./a.weir\" as A"
                "import \"./b.weir\" as B"
                "import \"./c.weir\" as C"
                "let v ="
                "    match 1 with"
                "    | 1 ->"
                "        print \"one\""
                "    | _ ->"
                "        print \"other\""
                "let y = yaml"
                "    a:"
                "      b: 1"
                "    c: 2"
                "let h = <<<"
                "    // not a comment"
                "    // nor this"
                ""
                "print $\"{v}{show y}{h}\""
            ]

            let folds = Weir.Lsp.foldingRangesFor "fold" lines

            Expect.equal
                folds
                [
                    (1, 2, Some "comment")
                    (3, 4, Some "comment")
                    (5, 7, Some "imports")
                    (8, 13, None)
                    (10, 11, None)
                    (12, 13, None)
                    (14, 17, None)
                    (15, 16, None)
                    (18, 20, None)
                ]
                "two comment folds (never merged), one imports fold, statement + arm folds, the yaml map, the heredoc with no comment fold inside"
        }
        test "folding ranges: a buffer that breaks assembly still folds by indentation" {
            let lines = [
                "let go = 1 > 0"
                "if go then !"
                "let v ="
                "    match 1 with"
                "    | 1 -> 1"
                "    | _ -> 2"
                "print $\"{v}\""
            ]

            let folds = Weir.Lsp.foldingRangesFor "fold" lines
            Expect.contains folds (3, 6, None) "the indented block folds"
        }
        test "document links: import, #sig and schema= resolve; missing targets link nothing [D:document-links]" {
            withPolishTree (fun entry lines lib sigp schema ->
                let links = Weir.Lsp.documentLinksFor entry lines

                Expect.equal
                    (links |> List.map (fun (l, c, len, target, _) -> l, c, len, target))
                    [ (1, 6, 6, sigp); (3, 9, 10, lib) ]
                    "the #sig tool word and the quoted import path"

                // a yaml schema= on the entry: the vendored file
                let yl = [
                    "let d = yaml schema=six"
                    "    a: 1"
                    "let e = yaml schema=ghost"
                    "    a: 1"
                    "print $\"{show d}{show e}\""
                ]

                System.IO.File.WriteAllLines(entry, yl)
                let ylinks = Weir.Lsp.documentLinksFor entry yl

                Expect.equal
                    (ylinks |> List.map (fun (l, c, len, t, _) -> l, c, len, t))
                    [ (1, 21, 3, schema) ]
                    "the vendored schema only"
                // a missing import: no link
                let ml = [ "import \"./nope.weir\" as N"; "print \"x\"" ]
                Expect.equal (Weir.Lsp.documentLinksFor entry ml) [] "an unresolved import links nothing")
        }
        test
            "diagnostic tags: unused-binding is Unnecessary, the bare `_` variant is not; unreachable-arm is [D:diagnostic-tags]" {
            let tagsOf lines =
                diagsIn "t.weir" lines |> List.map (fun d -> d.Code, Weir.Lsp.diagnosticTags d)

            Expect.equal (tagsOf [ "let x = 1" ]) [ ("unused-binding", [ 1 ]) ] "an unread binder"

            Expect.equal
                (tagsOf [ "let go () ="; "    let _ = 1"; "    ()"; "go ()" ])
                [ ("unused-binding", []) ]
                "the bare _ discard is misnamed, not unnecessary"

            Expect.exists
                (tagsOf [ "let v = match 1 with | _ -> 1 | 2 -> 2"; "print (show v)" ])
                (fun (c, t) -> c = "unreachable-arm" && t = [ 1 ])
                "an unreachable arm"
        }
        test
            "related information: a sig mismatch points at the signature line; the shadow variant at the rebinding [D:diagnostic-tags]" {
            let ds = diagsIn "m.weir" [ "module M"; "let f : int -> int"; "let f n = show n" ]

            let mismatch =
                ds |> List.find (fun d -> d.Message.Contains "does not match its signature")

            Expect.equal mismatch.Related [ ("m.weir", 2, 5, "the signature declares int -> int") ] ""

            let shadow =
                diagsIn "s.weir" [ "let x = 1"; "let x = 2"; "print (show x)" ]
                |> List.find (fun d -> d.Code = "unused-binding")

            Expect.equal shadow.Related [ ("s.weir", 2, 5, "'x' is rebound here") ] ""
        }
        test
            "related information: a module error carries its import line; the imported-here note carries the error site (R13, additive)" {
            withPolishTree (fun entry lines lib _ _ ->
                System.IO.File.WriteAllLines(
                    lib,
                    [| "module Lib"; ""; "let double : int -> int"; ""; "let double n = show n" |]
                )

                let script = [ "import \"./lib.weir\" as Lib"; "print (show (Lib.double 1))" ]
                System.IO.File.WriteAllLines(entry, script)
                let ds = diagsIn entry script
                let primary = ds |> List.find (fun d -> d.File = lib)
                Expect.equal primary.Related [ (entry, 1, 8, "imported here") ] "the import line"
                let note = ds |> List.find (fun d -> d.Code = "imported-here")
                Expect.equal note.Related [ (lib, primary.Line, primary.Col, primary.Message) ] "the error site"
                Expect.equal note.Line 1 "the note is still published (R13)")
        }
    ]

let semanticTokenTests =
    testList "Semantic tokens" [
        test "a command statement tokens head + argv [D:semantic-tokens]" {
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
            let lines = [
                "let alpha = 1"
                "let beta = alpha + 1"
                "let alpha = beta"
                "print $\"{alpha + beta}\""
            ]

            // a use resolves to its top-level let
            Expect.equal (Weir.Lsp.definitionFor lines 2 12) (Some(1, 5, 5)) "alpha use -> its let"
            // shadowing: the last binder above the use wins
            Expect.equal (Weir.Lsp.definitionFor lines 4 10) (Some(3, 5, 5)) "last binder above wins"
            // a let-pattern binder is a definition site too
            let pat = [ "let (a, b) = (1, 2)"; "print $\"{a}\"" ]
            Expect.equal (Weir.Lsp.definitionFor pat 2 10) (Some(1, 6, 1)) "letpat binder found"
            // builtins have no source: null
            Expect.equal (Weir.Lsp.definitionFor [ "print 1" ] 1 2) None "builtin -> null"
            // flipped by the binder-span session [PLAN-diagnostics-arc C]:
            // the park's conservative null became the jump
            Expect.equal (Weir.Lsp.definitionFor [ "let f x = x + 1" ] 1 11) (Some(1, 7, 1)) "param use -> param"
        }
        test "definitionFor: record members and union cases resolve to the type declaration [D:lsp-requests]" {
            let lines = [
                "type Verb ="
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
                "| Push n -> print $\"{n}\""
            ]

            // expression-position union case -> its case in the decl
            Expect.equal (Weir.Lsp.definitionFor lines 7 9) (Some(2, 7, 4)) "ctor use -> the case"
            // field access -> the field in the record decl
            Expect.equal (Weir.Lsp.definitionFor lines 9 13) (Some(5, 28, 4)) "c.Port -> the Port field"
            // record-literal field name -> the field in the decl
            Expect.equal (Weir.Lsp.definitionFor lines 8 11) (Some(5, 14, 4)) "literal Host -> the Host field"
            // pattern-position case (PSpan carries it) -> the case
            Expect.equal (Weir.Lsp.definitionFor lines 11 3) (Some(2, 7, 4)) "| Pull -> the case"
            // a pattern payload binder is a local binder: null (the park)
            Expect.equal (Weir.Lsp.definitionFor lines 12 8) None "payload binder -> null"
        }
        test "definitionFor: LOCAL binders — inner lets, payload binders, innermost shadowing [PLAN-diagnostics-arc C]" {
            // inner-let use -> its binder (through the block-let joins)
            let inner = [ "let go () ="; "    let acc = 1 + 1"; "    print $\"{acc}\""; "go ()" ]

            Expect.equal (Weir.Lsp.definitionFor inner 3 14) (Some(2, 9, 3)) "inner-let use -> its binder"

            // payload-binder use -> the pattern binder (PSpan)
            let pay = [ "type V = | Push of int"; "match Push 1 with"; "| Push n -> print $\"{n}\"" ]

            Expect.equal (Weir.Lsp.definitionFor pay 3 22) (Some(3, 8, 1)) "payload use -> the binder"

            // innermost wins: the param shadows the top-level inside f,
            // the top-level owns the use outside
            let shadow = [ "let x = 1"; "let f x = x + 2"; "print $\"{f x}\"" ]
            Expect.equal (Weir.Lsp.definitionFor shadow 2 11) (Some(2, 7, 1)) "inside f: the param"
            Expect.equal (Weir.Lsp.definitionFor shadow 3 12) (Some(1, 5, 1)) "outside f: the top-level"
        }
        test "definitionFor: within and until binders resolve, and shadow a top-level [D:lsp-rename]" {
            let within = [ "let d = \"top\""; "within tmp d"; "    print d"; "print d" ]

            Expect.equal (Weir.Lsp.definitionFor within 3 11) (Some(2, 12, 1)) "inside the scope: the within binder"
            Expect.equal (Weir.Lsp.definitionFor within 4 7) (Some(1, 5, 1)) "outside: the top-level"

            let until = [
                "let r = 1"
                "let out = retry attempts=3 delay=1s"
                "    echo hi | complete"
                "until r"
                "    r.exitCode == 0"
                "print r"
            ]

            Expect.equal (Weir.Lsp.definitionFor until 5 5) (Some(4, 7, 1)) "in the predicate: the until binder"
            Expect.equal (Weir.Lsp.definitionFor until 6 7) (Some(1, 5, 1)) "outside: the top-level"
        }
        test "definitionFor: the from-json type name jumps to its declaration [PLAN-diagnostics-arc A3]" {
            let lines = [
                "type Oidc = { value: string }"
                "let toks = [\"{}\"] |> from json Oidc"
                "print \"x\""
            ]

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
        test "definitionFor: a type NAME inside a declaration jumps to its decl [D:lsp-typename] (user receipt)" {
            // a field's type in a record decl, and the payload after `of` —
            // neither is an expression node, so the word-level fallback owns them
            let recLines = [ "type Spec = { replicas: int }"; "type Deployment = { spec: Spec }" ]

            Expect.equal (Weir.Lsp.definitionFor recLines 2 28) (Some(1, 6, 4)) "field type Spec -> its decl"

            let unionLines = [
                "type Rollout = { n: int }"
                "[<Tag \"kind\">]"
                "type K = Deployment of Rollout"
            ]

            Expect.equal (Weir.Lsp.definitionFor unionLines 3 24) (Some(1, 6, 7)) "payload after `of` -> its decl"
        }
        test "hoverType: a type name inside a declaration hovers its shape [D:lsp-typename] (user receipt)" {
            let lines = [ "type Spec = { replicas: int }"; "type Deployment = { spec: Spec }" ]

            Expect.equal (Weir.Lsp.hoverType lines 2 29) (Some "{ replicas: int }") "the field's type shows its shape"
        }
        test "hoverType: Tag/Other and the stream word answer [D:lsp-typename] (user receipt)" {
            let attrLines = [
                "type R = { a: int }"
                "[<Tag \"kind\">]"
                "type K ="
                "    | A of R"
                "    | [<Other>] X of string"
            ]

            Expect.isTrue
                (Weir.Lsp.hoverType attrLines 2 4
                 |> Option.exists (fun s -> s.Contains "wire-tagged"))
                "Tag hovers its meaning"

            Expect.isTrue
                (Weir.Lsp.hoverType attrLines 5 11
                 |> Option.exists (fun s -> s.Contains "open-world"))
                "Other hovers its meaning"

            let streamLines = [
                "type R = { a: int }"
                "[<Tag \"k\">]"
                "type K = A of R"
                "let d = [\"x\"] |> from yaml stream K"
            ]

            Expect.isTrue
                (Weir.Lsp.hoverType streamLines 4 31
                 |> Option.exists (fun s -> s.Contains "cardinality"))
                "stream hovers its cardinality meaning"
        }
        test "hoverType: pattern-let binders hover their own types [D:pat-binder-hover] (user receipt)" {
            // `let key, title = Str.splitOnce …` — both names hover;
            // the RHS keeps its expression hover past the `=`
            let lines = [ "let key, title = Str.splitOnce \"=\" \"k=v\"" ]
            let onKey = Weir.Lsp.hoverType lines 1 6 // on `key`
            let onTitle = Weir.Lsp.hoverType lines 1 11 // on `title`
            Expect.isTrue (onKey |> Option.exists (fun s -> s.Contains "string")) "key : string"
            Expect.isTrue (onTitle |> Option.exists (fun s -> s.Contains "string")) "title : string"
        }
        test "hoverType: a lambda param shows its own type, not the enclosing arrow (user receipt)" {
            // t is used via field access -> an open row; the bug showed
            // the param carrying the function's `... -> ...` arrow because
            // nodeAt fell back to the lambda. The param must show only its
            // own domain type.
            let lines = [ "let snapshot t = t.name" ]
            let onName = Weir.Lsp.hoverType lines 1 6 // on `snapshot`
            let onParam = Weir.Lsp.hoverType lines 1 14 // on the param `t`
            // the function shows its (annotated) signature naming the param
            Expect.isTrue
                (onName |> Option.exists (fun s -> s.Contains "snapshot (t:"))
                "the function shows its signature"

            Expect.isSome onParam "the param hovers to something"

            Expect.isFalse
                (onParam |> Option.exists (fun s -> s.Contains "->"))
                "the param shows its own type, no arrow"
        }
        test "doc comments: /// attaches to the next declaration; a blank breaks it [D:doc-comments]" {
            let attached = [ "/// The token env."; "/// Two lines."; "type TokenEnv = { name: string }" ]

            Expect.equal
                (Weir.Assemble.docAttachments attached
                 |> List.map (fun d -> d.Line, d.Col, d.Len, d.Doc))
                [ (3, 6, 8, [ "The token env."; "Two lines." ]) ]
                "a contiguous /// run attaches to the type name at (3,6,8), lines accumulated in order"

            // a blank line between the doc and the declaration breaks it
            Expect.equal
                (Weir.Assemble.docAttachments [ "/// orphan"; ""; "type T = { x: int }" ])
                []
                "blank-separated /// does not attach"

            Expect.equal
                (Weir.Assemble.docAttachments [ "/// doc"; "let inc x = x + 1" ]
                 |> List.map (fun d -> d.Line, d.Col, d.Doc))
                [ (2, 5, [ "doc" ]) ]
                "let binder name is the key"
        }
        test "doc comments: an attribute line is transparent — both doc/attribute orders attach [D:doc-help]" {
            // F#'s canonical order: /// doc, then [<...>], then the field
            Expect.equal
                (Weir.Assemble.docAttachments [
                    "type C = {"
                    "    /// the beta"
                    "    [<Default 3>]"
                    "    beta: int"
                    "}"
                 ]
                 |> List.map (fun d -> d.Line, d.Doc))
                [ (4, [ "the beta" ]) ]
                "the doc rides THROUGH the attribute to the field line"

            // the pre-existing attribute-then-doc order keeps working
            Expect.equal
                (Weir.Assemble.docAttachments [
                    "type C = {"
                    "    [<Default 3>]"
                    "    /// the beta"
                    "    beta: int"
                    "}"
                 ]
                 |> List.map (fun d -> d.Line, d.Doc))
                [ (4, [ "the beta" ]) ]
                "attribute-then-doc still attaches at the field line"
        }
        test "doc comments: a same-line attribute is transparent — the doc keys the field after it [D:doc-help]" {
            Expect.equal
                (Weir.Assemble.docAttachments [ "type C = {"; "    /// the n"; "    [<Short \"n\">] n: int"; "}" ]
                 |> List.map (fun d -> d.Line, d.Col, d.Len, d.Doc))
                [ (3, 19, 1, [ "the n" ]) ]
                "the field name after the attribute list"

            Expect.equal
                (Weir.Assemble.docAttachments [ "type U ="; "    /// the a"; "    | [<Wire \"a\">] A"; "    | B" ]
                 |> List.map (fun d -> d.Line, d.Col, d.Doc))
                [ (3, 20, [ "the a" ]) ]
                "a union case after its attribute"
        }
        test "doc comments: hover shows type FIRST, then the /// doc [D:doc-comments]" {
            let lines = [ "/// Adds one."; "let inc x = x + 1"; "print (inc 1)" ]
            let s = Weir.Lsp.hoverType lines 2 5 |> Option.defaultValue "" // on `inc`
            Expect.stringContains s "inc (x: int) : int" "the annotated signature is present"
            Expect.stringContains s "Adds one." "the doc is present"
            Expect.isTrue (s.IndexOf "inc (x: int)" < s.IndexOf "Adds one.") "type first, then doc"

            // hovering elsewhere on the line (the value) shows type only, no doc
            let onValue = Weir.Lsp.hoverType lines 2 15

            Expect.isFalse
                (onValue |> Option.exists (fun v -> v.Contains "Adds one."))
                "the doc shows only on the documented NAME"
        }
        test "doc comments: hover is type-first + doc at the type-decl and field positions [D:doc-comments]" {
            let lines = [
                "/// The config record."
                "type Cfg = {"
                "    /// the host name"
                "    Host: string"
                "}"
            ]

            let onType = Weir.Lsp.hoverType lines 2 6 |> Option.defaultValue "" // Cfg
            Expect.stringContains onType "Host: string" "type name renders the record structure"
            Expect.stringContains onType "The config record." "and its doc"

            let onField = Weir.Lsp.hoverType lines 4 5 |> Option.defaultValue "" // Host
            Expect.stringContains onField "string" "field shows its type"
            Expect.stringContains onField "the host name" "and its doc"
            Expect.isTrue (onField.IndexOf "string" < onField.IndexOf "the host") "type first, then doc"
        }
        test "doc comments: hover a union case shows its signature + doc [D:doc-comments]" {
            let lines = [ "type Outcome ="; "    /// merge hit a conflict"; "    | Conflict of string" ]

            let onCase = Weir.Lsp.hoverType lines 3 7 |> Option.defaultValue "" // Conflict
            Expect.stringContains onCase "Conflict of string" "the case signature"
            Expect.stringContains onCase "merge hit a conflict" "and its doc"
        }
        test "doc comments: a misaligned /// errors — both field and case; aligned is clean [D:doc-comments]" {
            let hasAlign (ls: string list) =
                let d = diagsIn "d.weir" ls
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
            let src = [ "/// The record."; "type Cfg = {"; "  /// the host"; "    Host: string"; "}" ]

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
        test "fmt keeps a block-less serve head and its until stopped at one column [D:serve-defaults]" {
            let canon = [
                "within serve s port=8080"
                "until stopped"
                "always"
                "    print \"bye\""
                ""
                "let run () ="
                "    within serve s"
                "    until stopped"
                ""
                "run ()"
            ]

            let messy = [
                "within serve s port=8080"
                "until stopped"
                "always"
                "  print \"bye\""
                ""
                "let run () ="
                "  within serve s"
                "  until stopped"
                ""
                "run ()"
            ]

            Expect.equal (Weir.Fmt.formatLines messy) (Ok canon) "re-indented, the edge kept under its head"
            Expect.equal (Weir.Fmt.formatLines canon) (Ok canon) "idempotent"
        }
        test "doc comments: fmt never anchors a /// inside a heredoc/yaml block into its content [D:content-bytes]" {
            match Weir.Fmt.formatLines [ "let t = yaml"; "    a: 1"; "/// doc"; "    b: 2"; "print t" ] with
            | Ok out ->
                Expect.equal
                    out
                    [ "let t = yaml"; "    a: 1"; "/// doc"; "    b: 2"; "print t" ]
                    "the /// line stays where it is"
            | Error e -> failtestf "fmt failed: %s" e

            // in a heredoc the comment is content, and outdented it breaks
            // the block's layout — check refuses it, so fmt does, located
            match Weir.Fmt.formatLines [ "let t = <<<"; "    abc"; "/// doc"; "    def"; "print t" ] with
            | Error e ->
                Expect.stringContains e "line 3:1: this comment line outdents below the heredoc's first line" "located"
            | Ok out -> failtestf "must refuse: %A" out
        }
        test "fmt: a yaml district keeps relative indentation, re-anchors the base [D:yaml-district]" {
            // the block's relative indentation is semantic (JYamlLine rel),
            // so fmt re-anchors the base to marker+1 depth and preserves
            // every line's offset from it — never rewriting the yaml's own
            // nesting, which is the user's data
            let src = [
                "let d = yaml"
                "        a: 1"
                "        b:"
                "                c: two"
                ""
                "d |> to yaml |> print"
            ]

            match Weir.Fmt.formatLines src with
            | Error e -> failtestf "fmt failed: %s" e
            | Ok out ->
                let indent (s: string) = s.Length - s.TrimStart().Length

                Expect.equal
                    (indent (out |> List.find (fun l -> l.Contains "a: 1")))
                    4
                    "the base re-anchors to marker+1"

                Expect.equal
                    (indent (out |> List.find (fun l -> l.Contains "c: two")))
                    12
                    "a nested line keeps its +8 offset from the base"

                match Weir.Fmt.formatLines out with
                | Ok out2 -> Expect.equal out2 out "fmt is idempotent on nested districts"
                | Error e -> failtestf "second fmt failed: %s" e
        }
        test "fmt: a comment outdented past a district's base clamps inside the district" {
            // a yaml block only: in a heredoc the comment is content and the
            // outdent a layout error, refused (the test above)
            for marker, a, b in [ "yaml", "a: 1", "b: 2" ] do
                let src = [ $"let t = {marker}"; $"          {a}"; "  // c"; $"          {b}"; "print t" ]

                match Weir.Fmt.formatLines src with
                | Error e -> failtestf "fmt failed (%s): %s" marker e
                | Ok out ->
                    Expect.equal out [ $"let t = {marker}"; $"    {a}"; " // c"; $"    {b}"; "print t" ] marker

                    match Weir.Fmt.formatLines out with
                    | Ok out2 -> Expect.equal out2 out $"idempotent ({marker})"
                    | Error e -> failtestf "second fmt failed: %s" e
        }
        test "fmt: a next-line district marker merges onto the binding line [D:district-canonical]" {
            // canonical layout puts the marker on the binding line; the
            // next-line spelling stays parseable at the assembler and fmt
            // rewrites it — both spellings converge on one canonical text
            let next = [
                "let x ="
                "    <<<"
                "        alpha"
                ""
                "        beta"
                "            deep"
                "x |> Seq.iter print"
            ]

            let canonical = [
                "let x = <<<"
                "    alpha"
                ""
                "    beta"
                "        deep"
                "x |> Seq.iter print"
            ]

            match Weir.Fmt.formatLines next, Weir.Fmt.formatLines canonical with
            | Ok a, Ok b ->
                Expect.equal a canonical "the next-line spelling rewrites to the canonical text"
                Expect.equal b canonical "the canonical spelling is a fixpoint"

                match Weir.Fmt.formatLines a with
                | Ok a2 -> Expect.equal a2 a "the rewrite is idempotent"
                | Error e -> failtestf "second fmt failed: %s" e
            | Error e, _
            | _, Error e -> failtestf "fmt failed: %s" e
        }
        test "fmt: every marker kind merges — $<<< holes and yaml marker modifiers ride [D:district-canonical]" {
            let cases = [
                [ "let t ="; "    $<<<"; "        literal $HOME rides"; "t |> Seq.iter print" ],
                [ "let t = $<<<"; "    literal $HOME rides"; "t |> Seq.iter print" ]

                [ "let d ="; "    yaml"; "        a: 1"; "d |> to yaml |> Seq.iter print" ],
                [ "let d = yaml"; "    a: 1"; "d |> to yaml |> Seq.iter print" ]

                [
                    "let p ="
                    "    yaml patch by=name"
                    "        images:"
                    "            - name: app"
                    "print \"ok\""
                ],
                [
                    "let p = yaml patch by=name"
                    "    images:"
                    "        - name: app"
                    "print \"ok\""
                ]
            ]

            for next, canonical in cases do
                match Weir.Fmt.formatLines next with
                | Ok out -> Expect.equal out canonical "converges to the marker-on-binding-line layout"
                | Error e -> failtestf "fmt failed: %s" e
        }
        test
            "fmt: the district-pipe close outdents with the marker; the canonical close is untouched [D:district-canonical]" {
            let next = [
                "let n ="
                "    <<<"
                "        a"
                "        b"
                "    |> Seq.length"
                "print (show n)"
            ]

            let canonical = [ "let n = <<<"; "    a"; "    b"; "|> Seq.length"; "print (show n)" ]

            match Weir.Fmt.formatLines next with
            | Ok out ->
                Expect.equal out canonical "the close line rides the marker's outdent — never swallowed as content"

                match Weir.Fmt.formatLines canonical with
                | Ok out2 -> Expect.equal out2 canonical "the canonical district-pipe close survives untouched"
                | Error e -> failtestf "canonical fmt failed: %s" e
            | Error e -> failtestf "fmt failed: %s" e
        }
        test "fmt: district canonicalization preserves the VALUE byte-for-byte [D:district-canonical]" {
            // eval the same program before and after fmt; the heredoc's
            // joined text must be identical — blank interior lines, deeper
            // indents and $<<< holes included
            let evalJoined (lines: string list) : Value =
                match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
                | Error e -> failtest $"assemble: {e}"
                | Ok lls ->
                    lls
                    |> List.fold
                        (fun (te, ve) ll ->
                            match Weir.Parser.parseLine cmdResolver ll.Text with
                            | Error m -> failtest $"parse: {m}"
                            | Ok(SLet(n, e)) ->
                                match Weir.Check.typecheck te e with
                                | Error terr -> failtest $"check: {formatError terr}"
                                | Ok typed ->
                                    let v = eval ve typed

                                    {
                                        te with
                                            Values = Map.add n (generalize typed.Ty) te.Values
                                    },
                                    Map.add n v ve
                            | Ok other -> failtest $"unexpected statement {other}")
                        (env, valueEnv)
                    |> snd
                    |> Map.find "j"

            let src = [
                "let region = \"eu-1\""
                "let s ="
                "    $<<<"
                "        endpoint {region}"
                ""
                "            deeper literal $HOME"
                "let j = s |> Str.join \"\\n\""
            ]

            match Weir.Fmt.formatLines src with
            | Error e -> failtestf "fmt failed: %s" e
            | Ok out ->
                Expect.isTrue (out |> List.exists (fun l -> l.Contains "let s = $<<<")) "the marker merged"
                Expect.equal (evalJoined out) (evalJoined src) "the district value is byte-identical across fmt"
        }
        test "fmt: a commented binding line keeps the next-line marker; nested positions merge [D:district-canonical]" {
            // no seat remains for the comment once the marker takes the
            // line end, so the next-line spelling stays as written there
            let commented = [ "let x = // why"; "    <<<"; "        a"; "x |> Seq.iter print" ]

            match Weir.Fmt.formatLines commented with
            | Ok out -> Expect.equal out commented "not merged; left formatted as written"
            | Error e -> failtestf "fmt failed: %s" e

            let nested = [
                "let f v ="
                "    match v with"
                "    | 1 ->"
                "        let y ="
                "            <<<"
                "                hi"
                "        y"
                "    | _ -> [\"other\"]"
                "f 1 |> Seq.iter print"
            ]

            match Weir.Fmt.formatLines nested with
            | Ok out ->
                Expect.isTrue
                    (out |> List.exists (fun l -> l.TrimStart().StartsWith "let y = <<<"))
                    "a match-arm binding merges too — the rewrite is position-general"
            | Error e -> failtestf "fmt failed: %s" e
        }
        test "the prelude env ignores a thread's pending anonymous shapes and leaves them pending [D:anon-nesting]" {
            let names (te: Weir.Types.TypeEnv) = te.Types |> Map.keys |> Set.ofSeq

            let clean, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            Weir.Types.pushAnonDef "PendingShapeProbe" [ ("a", Weir.Types.TInt) ]

            let withPending, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            let after = Weir.Types.drainAnonDefs ()
            Expect.equal (names withPending) (names clean) "no pending shape enters the prelude env"
            Expect.equal (after |> List.map fst) [ "PendingShapeProbe" ] "the caller's pending shape survives"
        }
        test "SKILL.md's inventory lists every bare builtin and prelude type, both ways [D:surface-gate]" {
            // ground truth is the checker's own env, not #help: the
            // module gate (ci/skill-surface.sh) reads #help, which never
            // listed the bare builtins — the gap `into` and `nats` lived in
            let strict, _ =
                Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv.Value

            let cases =
                strict.Types
                |> Map.toList
                |> List.collect (fun (_, d) ->
                    match d with
                    | Weir.Types.Union u -> u.Cases |> List.map fst
                    | _ -> [])
                |> Set.ofList

            let bare =
                strict.Values
                |> Map.keys
                |> Seq.filter (fun k -> not (k.StartsWith "|") && not (Set.contains k cases))
                |> Set.ofSeq

            let skill =
                System.IO.File.ReadAllText(
                    System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "skills", "weir", "SKILL.md")
                )

            let inventory = skill.Substring(skill.IndexOf "## Surface inventory")

            let ticked (s: string) =
                System.Text.RegularExpressions.Regex.Matches(s, @"`([A-Za-z][A-Za-z0-9]*)`")
                |> Seq.map (fun m -> m.Groups[1].Value)
                |> Set.ofSeq

            let lines = inventory.Split('\n')

            let bareLine =
                lines
                |> Array.tryFind (fun l -> l.StartsWith "- bare builtins: ")
                |> Option.map ticked
                |> Option.defaultValue Set.empty

            Expect.equal bareLine bare "the bare-builtins line is exactly the env's bare builtins"

            let typeLines =
                lines
                |> Array.choose (fun l ->
                    let m =
                        System.Text.RegularExpressions.Regex.Match(l, @"^- type `([A-Za-z]+)`: (.*)$")

                    if m.Success then
                        Some(m.Groups[1].Value, ticked m.Groups[2].Value)
                    else
                        None)
                |> Map.ofArray

            Expect.equal
                (typeLines |> Map.keys |> Set.ofSeq)
                (strict.Types |> Map.keys |> Set.ofSeq)
                "a type line per prelude type"

            for KeyValue(n, d) in strict.Types do
                let expected =
                    match d with
                    | Weir.Types.Union u -> u.Cases |> List.map fst |> Set.ofList
                    | Weir.Types.Record r -> r.Fields |> List.map fst |> Set.ofList

                Expect.equal (Map.tryFind n typeLines) (Some expected) $"type {n}: its cases/fields"

            // every bare builtin explains itself on hover and #help
            let undocumented =
                bare
                |> Set.filter (fun b -> not (Map.containsKey b Weir.Builtins.builtinDocs.Value))

            Expect.isEmpty undocumented "every bare builtin has a builtinDocs entry"
        }
        test "every registered attribute has hover docs, and only those [D:surface-gate]" {
            Expect.equal
                (Weir.Builtins.attrDocs |> Map.keys |> Set.ofSeq)
                Weir.Check.attributeNames
                "attrDocs = the registry"
        }
        test "builtin docs: every module member has an entry — hover coverage is CHECKED [D:docs-coverage]" {
            // the skill-surface rule applied to builtinDocs: a member
            // the binary ships that hover/completion cannot explain is
            // the same class as one SKILL.md does not mention — found
            // by accident three times before this check existed
            let omitted: Map<string, string> = Map []

            let members =
                env.Modules
                |> Map.toList
                |> List.collect (fun (m, mems) -> mems |> Map.toList |> List.map (fun (name, _) -> $"{m}.{name}"))

            // the enumeration must be real before zero-missing can pass
            Expect.isGreaterThan (List.length members) 100 "the enumeration broke, not the docs"

            let missing =
                members
                |> List.filter (fun q ->
                    not (Map.containsKey q Weir.Builtins.builtinDocs.Value)
                    && not (Map.containsKey q omitted))

            Expect.equal
                missing
                []
                "every module member has a builtinDocs entry (or an explicit omission with its reason)"

            // the omissions list sweeps both ways: documented or
            // vanished = stale, fails too (the skill-omitted rule)
            for KeyValue(q, _) in omitted do
                Expect.isFalse
                    (Map.containsKey q Weir.Builtins.builtinDocs.Value)
                    $"'{q}' is omitted but documented — stale"

                Expect.contains members q $"'{q}' is omitted but not shipped — stale"
        }
        test "builtin docs: every doc example runs clean [D:builtin-docs]" {
            // D1 = (a) executable, weir-side: the Example is registry data
            // run through the same check+eval path, not prose parsed from
            // an F# literal. A rotted example fails the build here.
            // Statement-style examples [D:no-let-in-examples]: a multi-line
            // example assembles and folds statement by statement, so hover
            // teaches the style scripts actually use — never `let … in`
            let runExample (ex: string) =
                let lines = ex.Split '\n' |> Array.toList

                match Weir.Assemble.assemble (lines |> List.mapi (fun i l -> i + 1, l)) with
                | Error e -> failwith $"assemble: {e}"
                | Ok lls ->
                    lls
                    |> List.fold
                        (fun (te, ve) ll ->
                            match Weir.Parser.parseLine cmdResolver ll.Text with
                            | Error m -> failwith $"parse: {m}"
                            | Ok(SExpr e)
                            | Ok(SCmd e) ->
                                match Weir.Check.typecheck te e with
                                | Error terr -> failwith $"check: {formatError terr}"
                                | Ok typed ->
                                    eval ve typed |> ignore
                                    te, ve
                            | Ok(SLet(n, e)) ->
                                match Weir.Check.typecheck te e with
                                | Error terr -> failwith $"check: {formatError terr}"
                                | Ok typed ->
                                    let v = eval ve typed

                                    {
                                        te with
                                            Values = Map.add n (Weir.Types.generalize typed.Ty) te.Values
                                    },
                                    Map.add n v ve
                            | Ok other -> failwith $"doc examples are let/command/expression statements, got {other}")
                        (env, valueEnv)
                    |> ignore

            for KeyValue(name, d) in Weir.Builtins.builtinDocs.Value do
                match d.Example with
                | None -> ()
                | Some ex ->
                    // the style guard: statement lets only — an example
                    // teaching `let … in` teaches a spelling scripts
                    // never use (for…in is not a let)
                    for line in ex.Split '\n' do
                        if line.Contains "let " && line.Contains " in " && not (line.Contains "for ") then
                            failtestf "the doc example for '%s' uses let…in — write it statement-style: %s" name line

                    try
                        runExample ex
                    with e ->
                        failtestf "the doc example for '%s' failed to run: %s\n%s" name ex e.Message
        }
        test "builtin docs: hover on a builtin shows its type first, then the doc [D:builtin-docs]" {
            let lines = [ "let r = [1;2;3] |> Seq.map (fun x -> x + 1) |> Seq.freeze" ]
            let h = Weir.Lsp.hoverType lines 1 22 |> Option.defaultValue "" // on Seq.map
            Expect.stringContains h "->" "the type is present"
            Expect.stringContains h "every element" "the summary is present"
            Expect.stringContains h "Seq.freeze" "the executable example is present"
            Expect.isTrue (h.IndexOf "->" < h.IndexOf "every element") "type first, then doc"
        }
        test
            "hover: annotated signature for named builtins + user decls; arrow fallback when unnamed [D:annotated-signature]" {
            // a builtin with named params (sampled) renders the annotated form
            let b = [ "let r = Str.isMatch \"[0-9]+\" \"x42\"" ]
            let onBuiltin = Weir.Lsp.hoverType b 1 14 |> Option.defaultValue "" // Str.isMatch

            Expect.stringContains
                onBuiltin
                "Str.isMatch (pattern: string) (subject: string) : bool"
                "annotated builtin signature"

            // a user top-level let: names from the binder spans
            let u = [ "let deploy a b = Str.contains a b"; "let z = deploy \"x\" \"y\"" ]
            let onUser = Weir.Lsp.hoverType u 1 8 |> Option.defaultValue "" // the `deploy` binder
            Expect.stringContains onUser "deploy (a: string) (b: string) : bool" "annotated user declaration"

            // an inner let, likewise (the params get concrete types from use)
            let il = [ "let f x ="; "    let g a b = Str.contains a b"; "    g x x" ]
            let onInner = Weir.Lsp.hoverType il 2 9 |> Option.defaultValue "" // inner `g`
            Expect.stringContains onInner "g (a: string) (b: string) : bool" "annotated inner let"

            // fallback: a usage of a user function (names are reached at
            // declarations, not use sites) -> the arrow type
            let fb = [ "let myFn a b = Str.contains a b"; "let z = myFn \"x\" \"y\"" ]
            let onFallback = Weir.Lsp.hoverType fb 2 10 |> Option.defaultValue "" // `myFn` at the USE

            Expect.equal onFallback "string -> string -> bool" "a usage -> arrow fallback, no annotated parens"
        }
        test "annotated signature is presentation-only: formatTy (errors) stays the arrow [D:annotated-signature]" {
            // the arrow formatter is untouched — errors quote what the
            // checker computed; the annotated form is hover presentation
            let ty = TFun(TStr, TFun(TStr, TBool))

            Expect.equal
                (Weir.Types.formatTy ty)
                "string -> string -> bool"
                "formatTy: the arrow, what errors byte-identically quote"

            Expect.equal
                (Weir.Types.formatSignature "Str.isMatch" [ "pattern"; "subject" ] ty)
                "Str.isMatch (pattern: string) (subject: string) : bool"
                "formatSignature: the annotated form, hover/completion only"

            // a zero-param value renders name-and-type, no parens
            Expect.equal (Weir.Types.formatSignature "Self.pid" [] TInt) "Self.pid : int" "zero-param: no empty parens"

            // a real type error still quotes the arrow (byte-identical)
            let terr = checkErr "double 1 2"

            Expect.stringContains
                terr.Message
                "'double' takes at most 1 argument(s), but got 2"
                "the error text is unchanged"
        }
        test "builtin docs: every named member's Params count fits its arrow depth [D:annotated-signature]" {
            // a name count exceeding the arrow depth renders a broken
            // signature — verify names pair with real parameters
            let rec arrowDepth t =
                match t with
                | TFun(_, cod) -> 1 + arrowDepth cod
                | _ -> 0

            let typeOf (name: string) =
                match Map.tryFind name env.Values with
                | Some sch -> Some sch.Ty
                | None ->
                    match name.Split '.' with
                    | [| m; mem |] ->
                        Map.tryFind m env.Modules
                        |> Option.bind (Map.tryFind mem)
                        |> Option.map (fun s -> s.Ty)
                    | _ -> None

            for KeyValue(name, d) in Weir.Builtins.builtinDocs.Value do
                if not (List.isEmpty d.Params) then
                    match typeOf name with
                    | Some ty ->
                        Expect.isLessThanOrEqual
                            d.Params.Length
                            (arrowDepth ty)
                            $"'{name}': {d.Params.Length} names but arrow depth {arrowDepth ty}"
                    | None -> () // boundary/reifier forms are not plain values — skip
        }
        test "builtin docs: Env.load's doc shows on `load`, NOT on the module Env or the type arg [D:builtin-docs]" {
            let lines = [ "type Cfg = { name: string }"; "let c = Env.load Cfg" ]

            let onLoad = Weir.Lsp.hoverType lines 2 14 |> Option.defaultValue "" // on `load`
            Expect.stringContains onLoad "typed record" "the Env.load summary reaches hover on `load`"
            Expect.stringContains onLoad "exactly that name" "and its law pointer"

            // the reported bug: hovering the module `Env` must not surface load's doc
            let onEnv = Weir.Lsp.hoverType lines 2 10 |> Option.defaultValue "" // on `Env`
            Expect.isFalse (onEnv.Contains "exactly that name") "hovering the module Env does not surface load's doc"
        }
        test "builtin docs: reifier hover maps the |completed key back to complete [D:builtin-docs]" {
            let lines = [ "let c = echo hi | complete" ]
            let h = Weir.Lsp.hoverType lines 1 22 |> Option.defaultValue "" // on `complete`
            Expect.stringContains h "`Completed` record" "the reifier's doc reaches hover through TEVar |completed"
            Expect.stringContains h "exit code and the output together" "the reifier law pointer"
        }
        test "builtin docs: a builtin type name hovers its doc (word-at-cursor fallback) [D:builtin-docs]" {
            let lines = [ "type W = { c: Completed }" ]
            let h = Weir.Lsp.hoverType lines 1 18 |> Option.defaultValue "" // on `Completed`
            Expect.stringContains h "finished command" "the Completed type doc via the word fallback"
            Expect.stringContains h "complete" "names where you get one"
        }
        test "hover: keywords / operators / wildcard are silent; identifiers & literals still answer [D:hover-silence]" {
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
            let lit = [
                "type Target = {"
                "    /// the bicep path"
                "    BicepPath: string"
                "}"
                "let t = { BicepPath = \"b\" }"
            ]

            let onField = Weir.Lsp.hoverType lit 5 12 |> Option.defaultValue "" // BicepPath in the literal
            Expect.stringContains onField "string" "the FIELD's type, not the record"
            Expect.isFalse (onField.Contains "Target") "not the record type Target"
            Expect.stringContains onField "the bicep path" "and the field's doc"
        }
        test
            "hover: a pattern constructor shows its signature; the payload binder shows its OWN type [D:hover-completeness]" {
            // the payload type (string) deliberately differs from the arm
            // result (int) — so the binder must resolve to its own type,
            // not the enclosing match's. `r` is bound to a PR value so the
            // constructor pattern has a known union to match.
            let lines = [
                "type PR = Pulled of string | Idle"
                "let r = Pulled \"x\""
                "let g = match r with | Pulled s -> Str.length s | Idle -> 0"
            ]

            let onCtor = Weir.Lsp.hoverType lines 3 26 |> Option.defaultValue "" // `Pulled` in the pattern
            Expect.stringContains onCtor "Pulled : string -> PR" "the constructor signature"

            let onPayload = Weir.Lsp.hoverType lines 3 31 |> Option.defaultValue "" // `s`, the payload binder
            Expect.equal onPayload "string" "the payload binder's OWN type, not the arm's int"
        }
        test
            "over-application on an indented continuation points at the extra args + hints the indent [D:over-apply-continuation]" {
            // the misleading case: `deleteBranch branch` indented deeper
            // than `makeRef …` is slurped as extra arguments — the error
            // must land on the continuation, not the head, and say so
            let lines = [
                "let makeRef a b = a"
                "let deleteBranch x = x"
                "let branch = \"b\""
                "let f ="
                "    makeRef \"x\" branch"
                "        deleteBranch branch"
            ]

            let diags = diagsIn "c.weir" lines
            let d = diags |> List.find (fun x -> x.Message.Contains "takes at most")
            Expect.equal d.Line 6 "points at the CONTINUATION line (line 6), not the head (line 5)"
            Expect.stringContains d.Message "indented continuation" "the hint names the real cause (the indent)"
        }
        test "an errored let warns its command heads and suppresses the unbound cascade [PLAN-diagnostics-arc B5+B6]" {
            // B6: the failed deploy binds a hole — one real error,
            // zero "unbound 'deploy'. Did you mean 'Deploy'?" echoes,
            // and the hole's descendants (application results,
            // constraints, discards) stay silent too
            let lines = [
                "type Cmd = | Deploy of int"
                "let deploy a = 1 + \"x\""
                "match Deploy 1 with"
                "| Deploy a -> print (show (deploy a))"
            ]

            let diags = diagsIn "b.weir" lines
            let errors = diags |> List.filter (fun d -> d.Severity = "error")
            Expect.hasLength errors 1 "ONE real error, zero echoes"
            Expect.stringContains errors[0].Message "expected int, got string" "the real error"

            // B5: an errored statement still surfaces its command-head
            // warnings (parse-level walk — no typed tree exists)
            let lines2 = [ "let go t ="; "    let e = targ etEnv t"; "    echo hi"; "    print \"ok\"" ]

            let diags2 = diagsIn "b2.weir" lines2

            Expect.exists
                diags2
                (fun d -> d.Code = "cmd-not-found" && d.Message.Contains "targ")
                "the errored statement's head warns"
        }
        test "a path head that does not exist yet warns at check, as a PATH command does [D:path-head-warn]" {
            match diagsIn "ph.weir" [ "./weir-not-yet-built.sh --flag" ] with
            | [ d ] ->
                Expect.equal d.Severity "warning" "a warning, not a parse error"
                Expect.equal d.Code "cmd-not-found" "the PATH rule's code"

                Expect.equal
                    d.Message
                    "no executable at ./weir-not-yet-built.sh — weir resolves commands at check time; the script runs once it exists"
                    "named as a path"
            | other -> failtest $"expected one warning, got {other |> List.map _.Message}"

            cleanIn
                "ph2.weir"
                [ "let total = 4"; "print (show (total/2))" ]
                "a slash inside an expression stays arithmetic"
        }
        test "row provenance: a bad field access errors at the ACCESS, not the call [PLAN-diagnostics-arc D]" {
            // the bicep 62/107 shape reduced: quality's t.BicepPath2
            // is the bug; the call that supplies the T is only the meet
            let lines = [
                "type T = { BicepPath: string; Name: string }"
                "let quality t ="
                "    print (t.BicepPath2)"
                "let mk = { BicepPath = \"b\"; Name = \"n\" }"
                "quality mk"
            ]

            let diags = diagsIn "pin.weir" lines

            match diags |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (3, 14) "positions at the access"
                Expect.equal (d.EndLine, d.EndCol) (Some 3, Some 24) "covers the field word"
                Expect.stringContains d.Message "Did you mean 'BicepPath'?" "the hint survives"
                Expect.stringContains d.Message "(the value becomes a T at 5:1)" "the meet is the note"
            | other -> failtest $"expected ONE error, got {other}"

            // a direct access on the concrete value stays where it was:
            // no origin recorded, no note
            let direct = [
                "type T = { BicepPath: string }"
                "let mk = { BicepPath = \"b\" }"
                "print (mk.BicepPath2)"
            ]

            let diags2 = diagsIn "pin2.weir" direct

            match diags2 |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (3, 11) "unchanged position"
                Expect.isFalse (d.Message.Contains "becomes a") "no meet note"
            | other -> failtest $"expected ONE error, got {other}"
        }
        test "row provenance: the TYPE-mismatch sibling also anchors at the access [PLAN-open-findings D]" {
            // right field name, wrong type, cross-statement: t.count is
            // forced to string but T.count is int. Was reported at the
            // meet; now at the access, meet as the note (the no-field
            // sibling's shape, sharing the atAccess helper)
            let lines = [
                "type T = { count: int }"
                "let f t ="
                "    print (Str.length t.count)"
                "let mk = { count = 1 }"
                "f mk"
            ]

            let diags = diagsIn "pin.weir" lines

            match diags |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.equal (d.Line, d.Col) (3, 25) "positions at the access, not the meet (was 5:1)"
                Expect.stringContains d.Message "expected int, got string" "the mismatch message"
                Expect.stringContains d.Message "(the value becomes a T at 5:1)" "the meet is the note"
            | other -> failtest $"expected ONE error, got {other}"

            // within-statement mismatch is unchanged: direct at the access,
            // no meet note (no cross-statement origin recorded)
            let within = [
                "type T = { count: int }"
                "let mk = { count = 1 }"
                "print (Str.length mk.count)"
            ]

            let d2 = diagsIn "pin2.weir" within

            match d2 |> List.filter (fun d -> d.Severity = "error") with
            | [ d ] ->
                Expect.stringContains d.Message "expected string, got int" "the mismatch"
                Expect.isFalse (d.Message.Contains "becomes a") "no meet note within a statement"
            | other -> failtest $"expected ONE error, got {other}"
        }
        test "Args/Env.load near-miss shapes teach ONE-type-name [PLAN-diagnostics-arc A1]" {
            // `Args.load C md` (a space inside the type name) must not
            // fall through to "module Args has no member 'load'" — a
            // lie: load is an arm, not a member
            Expect.stringContains (checkErr "Args.load").Message "takes ONE record or union type name" "zero args"

            Expect.stringContains
                (checkErr "Args.load Cmd extra").Message
                "takes ONE record or union type name"
                "the space-in-the-type-name shape (two args)"

            Expect.stringContains (checkErr "Env.load Cfg md").Message "takes ONE record type name" "the Env twin"
        }
        test "formatting request contracts: broken statements verbatim, assemble failure refuses [D:lsp-requests]" {
            // an unparseable statement never refuses the file — indent
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
                [
                    (1, 0, 4, 0)
                    (1, 5, 2, 1)
                    (1, 8, 4, 2)
                    (1, 13, 3, 2)
                    (1, 21, 1, 2)
                    (1, 23, 4, 1)
                ]
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
            // bound: an application — no command tokens
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
            // emits nothing
            let toks =
                Weir.Lsp.semanticTokensFor [
                    "let v0 ="
                    "    let v3 = \"a\""
                    "    print \"mm\""
                    "    let v4 = v3 ?!?"
                    "    3"
                ]

            Expect.equal toks [] "no command tokens anywhere"
        }
        test "armed command-group lines token (retargeted [D:district-retirement])" {
            let toks =
                Weir.Lsp.semanticTokensFor [ "if 1 > 0 then"; "    echo m one"; "    echo m two"; ""; "print \"z\"" ]

            Expect.equal
                toks
                [
                    (1, 4, 4, 0)
                    (1, 9, 1, 1)
                    (1, 11, 3, 1)
                    (2, 4, 4, 0)
                    (2, 9, 1, 1)
                    (2, 11, 3, 1)
                ]
                "argv/head spans have physical homes; no token past EOL"
        }
        test "error-state files emit partial truth" {
            let toks =
                Weir.Lsp.semanticTokensFor [ "echo before ok"; "let bad = ?!?"; "echo after ok" ]

            Expect.equal
                toks
                [
                    (0, 0, 4, 0)
                    (0, 5, 6, 1)
                    (0, 12, 2, 1)
                    (2, 0, 4, 0)
                    (2, 5, 5, 1)
                    (2, 11, 2, 1)
                ]
                "statements that parsed token; the failed one is silent"
        }
        test "expression stages after | emit nothing; reifiers emit nothing" {
            let toks =
                Weir.Lsp.semanticTokensFor [ "git status --porcelain |> Seq.map Str.trim" ]

            Expect.equal toks [ (0, 0, 3, 0); (0, 4, 6, 1); (0, 11, 11, 1) ] "the stage after | is expression territory"

            let toks2 =
                Weir.Lsp.semanticTokensFor [ "let ok = git rev-parse HEAD | succeeds"; "print $\"{show ok}\"" ]

            Expect.equal
                toks2
                [ (0, 9, 3, 0); (0, 13, 9, 1); (0, 23, 4, 1) ]
                "the reifier stays lexical (grammar, not argv)"
        }
        test "block-let and param-ful RHS commands token at depth" {
            let toks =
                Weir.Lsp.semanticTokensFor [
                    "let f r ="
                    "    let g = echo tag $r"
                    "    g |> Seq.length"
                    "print $\"{f 1}\""
                ]

            Expect.equal
                toks
                [ (1, 12, 4, 0); (1, 17, 3, 1); (1, 21, 2, 2) ]
                "the fresh block-let RHS tokens; the param splice is an island"
        }
        test "nested sigil through a paren splice recurses (depth 2)" {
            // $() as a direct command argument is a type error (seq arg —
            // rejected); the nested spelling rides a paren splice
            let toks =
                Weir.Lsp.semanticTokensFor [
                    "let m = $(echo a ($(echo b) |> Seq.head)) |> Seq.length"
                    "print $\"{m}\""
                ]

            Expect.equal
                toks
                [
                    (0, 10, 4, 0)
                    (0, 15, 1, 1)
                    (0, 17, 1, 2)
                    (0, 20, 4, 0)
                    (0, 25, 1, 1)
                    (0, 39, 1, 2)
                ]
                "outer sigil head/argv, splice delimiters, inner sigil head/argv"
        }
    ]

let blockRecoveryTests =
    // the receipt shape [D:block-recovery]: a multi-line let with one bad
    // inner line, then statements using it
    let receipt = [
        "let sub a b ="
        "    let total = a * b"
        "    let bad = total + \"x\""
        "    let doubled = total * 2"
        "    print (show bad)"
        "    print (show doubled)"
        "    doubled"
        ""
        "let r = sub 1 2"
        "print (show r)"
    ]

    testList "Block-level recovery [D:block-recovery]" [
        test "a hole never reaches a wire-boundary message; the cascade stays silent" {
            for tail in
                [
                    "r |> to json |> Seq.iter print"
                    "r |> to jsonl |> Seq.iter print"
                    "r |> to yaml |> Seq.iter print"
                    "r |> to yaml stream |> Seq.iter print"
                ] do
                Expect.equal
                    (errorMsgsIn "hole.weir" [ "let r = { a = 1 }"; tail ])
                    [ "no declared record has exactly the fields: a" ]
                    tail

            Expect.equal
                (errorMsgsIn "hole.weir" [
                    "let b = [\"{}\"] |> from json Nope"
                    "print (b |> to json |> Str.join \"\")"
                ])
                [ "unknown type 'Nope'" ]
                "a value read as an unknown type"
        }
        test "a failed type declaration reports once; its later uses do not cascade" {
            let only (lines: string list) =
                diagsIn "decl.weir" lines |> List.map (fun d -> d.Line, d.Message)

            match
                only [
                    "type E = E of string"
                    "    validate s = if Env.get s == None then fail \"unset\""
                    "print $\"{E.create \"HOME\"}\""
                ]
            with
            | [ (2, m) ] -> Expect.stringStarts m "'validate' must be pure" "the validate error alone"
            | other -> failtest $"validate: {other}"

            Expect.equal
                (only [ "[<Tag \"k\">]"; "type T<'a> = A of 'a | B"; "let x = A 1"; "print $\"{x}\"" ])
                [
                    1, "a tagged union is monomorphic — 'T' is generic, and the wire boundary needs concrete cases"
                ]
                "a failed union's case: no cmd-not-found, no unbound"

            Expect.equal
                (only [
                    "type R = { a: Nope }"
                    "let r = { a = 1 }"
                    "r |> to json |> Seq.iter print"
                ])
                [ 1, "unknown type 'Nope'" ]
                "a failed record's literal"

            Expect.equal
                (only [
                    "type W = { [<Wire \"\">] kind: string }"
                    "let w = [\"{}\"] |> from json W"
                    "print w.kind"
                ])
                [
                    1,
                    "'Wire' refuses an empty wire key — name the key the field reads and writes, e.g. [<Wire \"type\">] kind: string"
                ]
                "a failed type named at a boundary"
        }
        test "a pipeline type error blames the failing stage, not the source" {
            let line = "let n = [1; 2; 3] |> Seq.map show |> Seq.sum"
            let e = firstErrorIn "pipe.weir" [ line; "print $\"{n}\"" ]
            Expect.equal (e.Line, e.Col) (1, line.IndexOf "Seq.sum" + 1) "at Seq.sum"
            Expect.equal e.Message "expected int, got string" "the mismatch"

            let m =
                firstErrorIn "pipe.weir" [
                    "let n ="
                    "    [1; 2; 3]"
                    "    |> Seq.map show"
                    "    |> Seq.sum"
                    "print $\"{n}\""
                ]

            Expect.equal (m.Line, m.Col) (4, 8) "the multi-line chain: the |> Seq.sum line"
        }
        test "the receipt: one error at the bad line, the statement is a partial, never a stmt" {
            let diags, stmts, _, _, partials = Weir.Analyze.analyzeLines "t.weir" receipt

            Expect.equal
                (diags |> List.map (fun d -> d.Line, d.Col, d.Code, d.Message))
                [ 3, 23, "check", "expected int, got string" ]
                "exactly the first pass's one error"

            Expect.equal (stmts |> List.map (fun (ll, _) -> ll.Head)) [ 9; 10 ] "the errored statement is no stmt"
            Expect.equal (partials |> List.map (fun (ll, _) -> ll.Head)) [ 1 ] "…it is a partial"

            match partials with
            | [ (_,
                 {
                     Kind = Weir.Statements.KLet("sub", sch, _)
                     Env = env
                 }) ] ->
                Expect.isTrue
                    (match sch.Ty with
                     | TVar v -> v.StartsWith "__hole"
                     | _ -> false)
                    "the partial's binder scheme is the hole scheme"

                Expect.isTrue (env.Values.ContainsKey "sub") "its env is the hole-bound one"
            | other -> failtest $"expected the KLet partial, got {other}"
        }
        test "hover inside the broken statement: siblings above and below answer, the bad line and its binder do not" {
            let h l c = Weir.Lsp.hoverType receipt l c
            Expect.equal (h 2 9) (Some "total : int") "the inner binder above the bad line"
            Expect.equal (h 2 17) (Some "int") "a use above"
            Expect.equal (h 4 9) (Some "doubled : int") "the inner binder below the bad line"
            Expect.equal (h 4 19) (Some "int") "a use below"
            Expect.equal (h 7 5) (Some "int") "the block's tail"
            Expect.equal (h 3 15) None "inside the hole: null"
            Expect.equal (h 3 17) None "inside the hole, the operator: null"
            Expect.equal (h 3 9) None "the hole-bound binder: null"
            Expect.equal (h 5 17) None "a use of the hole-bound binder: null"
            Expect.equal (h 1 5) None "the partial's own top-level binder: null (its type is unknown)"
            Expect.equal (h 9 9) None "a later use of the partial's binder: null (hole-descended)"
        }
        test "definition inside the broken statement jumps; from inside the hole it does not" {
            Expect.equal
                (Weir.Lsp.definitionFor receipt 4 19)
                (Some(2, 9, 5))
                "a use below the bad line -> the binder above it"

            Expect.equal (Weir.Lsp.definitionFor receipt 6 17) (Some(4, 9, 7)) "a use -> a binder below the bad line"
            Expect.equal (Weir.Lsp.definitionFor receipt 2 17) (Some(1, 9, 1)) "a param use -> the param"
            Expect.equal (Weir.Lsp.definitionFor receipt 3 15) None "inside the hole: null"
        }
        test "a statement whose body is one failing expression stays treeless (no false tree)" {
            let _, stmts, _, _, partials =
                Weir.Analyze.analyzeLines "t.weir" [ "let x = 1 + \"a\""; "print (show x)" ]

            Expect.equal (stmts |> List.map (fun (ll, _) -> ll.Head)) [ 2 ] "the later statement checks"
            Expect.isEmpty partials "nothing to recover"
        }
        test "both twins and all three arms: a checked lambda body, a sequenced element, a pattern let" {
            // the check twin: the lambda is checked against the pushed domain
            let lam = [
                "let ys ="
                "    [1; 2] |> Seq.map (fun x ->"
                "        let y = x * 2"
                "        let bad = y + \"a\""
                "        y + 1)"
                "print (show ys)"
            ]

            let diags, _, _, _, partials = Weir.Analyze.analyzeLines "t.weir" lam
            Expect.equal (diags |> List.map (fun d -> d.Line, d.Severity)) [ 4, "error" ] "one error"
            Expect.equal (partials |> List.map (fun (ll, _) -> ll.Head)) [ 1 ] "recovered through the check twin"
            Expect.equal (Weir.Lsp.hoverType lam 5 9) (Some "int") "the line below the bad one answers"

            // a sequenced element (unit position) fails: hole, the block continues
            let seqEl = [
                "let go ="
                "    let a = 1"
                "    print \"x\" |> Seq.length"
                "    print (show a)"
                "go"
            ]

            let diags, _, _, _, partials = Weir.Analyze.analyzeLines "t.weir" seqEl
            Expect.equal (diags |> List.map (fun d -> d.Line, d.Severity)) [ 3, "error" ] "one error"
            Expect.equal (partials |> List.map (fun (ll, _) -> ll.Head)) [ 1 ] "recovered"
            Expect.equal (Weir.Lsp.hoverType seqEl 4 17) (Some "int") "the line below answers"
            Expect.equal (Weir.Lsp.hoverType seqEl 3 7) None "inside the hole: null"

            // a pattern let fails: every leaf is hole-bound
            let pat = [
                "let go ="
                "    let (p, q) = (1, \"a\")"
                "    let (m, n) = p + \"a\""
                "    print (show p)"
                "    print (show m)"
                "go"
            ]

            let diags, _, _, _, partials = Weir.Analyze.analyzeLines "t.weir" pat
            Expect.equal (diags |> List.map (fun d -> d.Line, d.Severity)) [ 3, "error" ] "one error"
            Expect.equal (partials |> List.map (fun (ll, _) -> ll.Head)) [ 1 ] "recovered"
            Expect.equal (Weir.Lsp.hoverType pat 4 17) (Some "int") "the earlier leaf answers"
            Expect.equal (Weir.Lsp.hoverType pat 5 17) None "a hole-bound leaf's use: null"
        }
        test "a recovery burn stops the fold: one budget bounds the file [D:budget-stop-first]" {
            // the recovery pass is armed with a fresh budget; a statement
            // whose first error is cheap but whose block burns must not
            // let every such statement pay a burn of its own
            let burn (b: int) = [
                $"let f{b} x ="
                "    let bad = 1 + \"x\""
                "    let g0 y = (y, y)"
                "    let g1 y = g0 (g0 y)"
                "    let g2 y = g1 (g1 y)"
                "    let g3 y = g2 (g2 y)"
                "    let g4 y = g3 (g3 y)"
                "    let g5 y = g4 (g4 y)"
                "    let v = g5 1"
                "    print (show bad)"
                "    print (show v)"
                "    x"
            ]

            let diags, _, _, _, partials =
                Weir.Analyze.analyzeLines "burn.weir" (burn 0 @ burn 1 @ [ "let later = 1 + \"y\"" ])

            let errors = diags |> List.filter (fun d -> d.Severity = "error")
            Expect.equal (errors |> List.map (fun d -> d.Line)) [ 2 ] "the first statement's own error, then the stop"
            Expect.isFalse (Weir.Check.isBudgetMessage errors.Head.Message) "the published error is the cheap one"
            Expect.isEmpty partials "a burnt recovery yields no tree"
        }
    ]

let quickFixTests =
    let diagsOf path (lines: string list) =
        let ds, _, _, _, _ = Weir.Analyze.analyzeLines path lines
        ds

    // every edit is against the unedited text [D:structured-fixes], so
    // they apply last-first
    let applyEdits (lines: string list) (edits: Weir.Diagnostics.FixEdit list) : string list =
        let arr = List.toArray lines

        let offset (l: int) (c: int) =
            let mutable o = 0

            for i in 0 .. l - 2 do
                o <- o + arr[i].Length + 1

            o + c - 1

        let mutable t = String.concat "\n" lines

        for e in edits |> List.sortByDescending (fun e -> e.Line, e.Col) do
            t <-
                t.Substring(0, offset e.Line e.Col)
                + e.NewText
                + t.Substring(offset e.EndLine e.EndCol)

        t.Split('\n') |> Array.toList

    let fixFor path lines (code: string) : Weir.Diagnostics.Fix =
        match diagsOf path lines |> List.tryFind (fun d -> d.Code = code) with
        | Some d ->
            match d.Fix with
            | Some f -> f
            | None -> failtest $"no fix on {code}: {d.Message}"
        | None -> failtest $"no {code} diagnostic: {diagsOf path lines |> List.map _.Message}"

    let edit l c el ec (text: string) : Weir.Diagnostics.FixEdit = {
        Line = l
        Col = c
        EndLine = el
        EndCol = ec
        NewText = text
    }

    // the invariant: a fix fixes — the code is gone and nothing NEW errors
    let fixes path lines (code: string) : Weir.Diagnostics.Fix =
        let before = diagsOf path lines
        let f = fixFor path lines code
        let after = diagsOf path (applyEdits lines f.Edits)

        Expect.isFalse
            (after |> List.exists (fun d -> d.Code = code))
            $"{f.Title}: {code} must be gone, got {after |> List.map _.Message}"

        for d in after do
            Expect.isTrue
                (before |> List.exists (fun b -> b.Message = d.Message))
                $"{f.Title}: a new diagnostic appeared [{d.Code}] {d.Message}"

        f

    let todo = "fail \"todo\""

    // a #sig'd tool on disk, its script path only (never written)
    let withSig (f: string -> unit) =
        let td = tempPath "weir-qfix"
        let sigs = System.IO.Path.Combine(td, ".weir", "sigs")
        System.IO.Directory.CreateDirectory sigs |> ignore

        System.IO.File.WriteAllLines(
            System.IO.Path.Combine(sigs, "mytool.weir"),
            [|
                "module Mytool"
                "let version = \"mytool 1.0\""
                "let exhaustive = true"
                "type Cmd = {"
                "    /// run without side effects"
                "    dryRun: bool"
                "}"
            |]
        )

        try
            f (System.IO.Path.Combine(td, "main.weir"))
        finally
            System.IO.Directory.Delete(td, true)

    testList "Quick fixes [D:structured-fixes]" [
        test "Replace with 'X': every did-you-mean site replaces the typo inside its span" {
            let f = fixes "q.weir" [ "let count = 1"; "print $\"{cont}\"" ] "unbound"
            Expect.equal f.Title "Replace with 'count'" ""
            Expect.equal f.Edits [ edit 2 10 2 14 "count" ] "the variable"
            Expect.equal f.Family None "no family"

            let f =
                fixes "q.weir" [ "type C = { Host: string }"; "let c = { Host = \"h\" }"; "print c.Hots" ] "check"

            Expect.equal (f.Title, f.Edits) ("Replace with 'Host'", [ edit 3 9 3 13 "Host" ]) "the field"

            let f =
                fixes
                    "q.weir"
                    [
                        "type C = { Host: string }"
                        "let c = { Host = \"h\" }"
                        "let d = { c with Hots = \"x\" }"
                        "print d.Host"
                    ]
                    "check"

            Expect.equal (f.Title, f.Edits) ("Replace with 'Host'", [ edit 3 18 3 22 "Host" ]) "the record update"

            let f =
                fixes
                    "q.weir"
                    [
                        "type V = Pull | Push of int"
                        "let v = Pull"
                        "let k = match v with | Pul -> 1 | _ -> 0"
                        "print $\"{k}\""
                    ]
                    "check"

            Expect.equal (f.Title, f.Edits) ("Replace with 'Pull'", [ edit 3 24 3 27 "Pull" ]) "the case"

            // the attribute's span covers its argument: only the name moves
            let f =
                fixes
                    "q.weir"
                    [
                        "type Cli = {"
                        "    [<Shrot \"n\">]"
                        "    name: string"
                        "}"
                        "let cli = Args.load Cli"
                        "print cli.name"
                    ]
                    "check"

            Expect.equal (f.Title, f.Edits) ("Replace with 'Short'", [ edit 2 7 2 12 "Short" ]) "the attribute"

            // a command head (the warning carries the typo unquoted); the
            // suggestion comes from the machine's PATH, so the pin reads it
            // from the message rather than naming one
            let target =
                diagsOf "q.weir" [ "ecoh hi" ]
                |> List.tryPick (fun d ->
                    if d.Code = "cmd-not-found" then
                        Weir.Types.didYouMeanTarget d.Message
                    else
                        None)
                |> Option.map snd
                |> Option.defaultWith (fun () -> failtest "no did-you-mean on the command head")

            let f = fixes "q.weir" [ "ecoh hi" ] "cmd-not-found"
            Expect.equal (f.Title, f.Edits) ($"Replace with '{target}'", [ edit 1 1 1 5 target ]) "the command"
        }
        test "Replace with 'X': an unknown flag replaces the word; no span, no fix" {
            withSig (fun entry ->
                let f = fixes entry [ "#sig mytool"; "mytool --dry-rnu" ] "sig"
                Expect.equal (f.Title, f.Edits) ("Replace with '--dry-run'", [ edit 2 8 2 17 "--dry-run" ]) "")

            Expect.equal
                (Weir.Diagnostics.fixOf [||] "unbound" "unbound variable 'cont'. Did you mean 'count'?" 2 10 None None)
                None
                "a did-you-mean without a span offers nothing"

            Expect.equal (Weir.Types.didYouMeanTarget "unbound variable 'cont'") None "no tail, no target"
        }
        test "Qualify: the bare-name teaching, family 'qualify'" {
            let lines = [
                "let xs = [1; 2]"
                "let ys = xs |> where (fun x -> x > 1)"
                "print $\"{ys |> Seq.length}\""
            ]

            let f = fixes "q.weir" lines "parse"
            Expect.equal f.Title "Qualify: Seq.where" ""
            Expect.equal f.Edits [ edit 2 16 2 21 "Seq.where" ] ""
            Expect.equal f.Family (Some "qualify") ""

            // the same teaching on a command head (a bare statement, so
            // the qualified member is then the discard lesson — fixFor)
            // `sortBy`, not `where`: where.exe is a program on Windows
            let f = fixFor "q.weir" [ "sortBy" ] "cmd-not-found"

            Expect.equal
                (f.Title, f.Edits, f.Family)
                ("Qualify: Seq.sortBy", [ edit 1 1 1 7 "Seq.sortBy" ], Some "qualify")
                ""
        }
        test "Add missing arms: one line per case under the arms, or appended on a single line" {
            let f =
                fixes
                    "q.weir"
                    [
                        "type V = Pull | Push of int | Stop"
                        "let v = Stop"
                        "let k ="
                        "    match v with"
                        "    | Stop -> 0"
                        "print $\"{k}\""
                    ]
                    "non-exhaustive"

            Expect.equal f.Title "Add missing arms: Pull, Push _" "a payload case names its pattern"
            Expect.equal f.Edits [ edit 5 16 5 16 $"\n    | Pull -> {todo}\n    | Push _ -> {todo}" ] ""

            let f =
                fixes
                    "q.weir"
                    [
                        "type V = Pull | Push of int"
                        "let v = Push 1"
                        "let k = match v with | Pull -> 0"
                        "print $\"{k}\""
                    ]
                    "non-exhaustive"

            Expect.equal
                (f.Title, f.Edits)
                ("Add missing arm: Push _", [ edit 3 33 3 33 $" | Push _ -> {todo}" ])
                "single line"

            let f =
                fixes
                    "q.weir"
                    [ "let n = 3"; "let k ="; "    match n with"; "    | 1 -> 0"; "print $\"{k}\"" ]
                    "non-exhaustive"

            Expect.equal
                (f.Title, f.Edits)
                ("Add missing arm: _", [ edit 4 13 4 13 $"\n    | _ -> {todo}" ])
                "catch-all"

            let f =
                fixes
                    "q.weir"
                    [
                        "let p = (true, 1)"
                        "let k ="
                        "    match p with"
                        "    | (true, _) -> 0"
                        "print $\"{k}\""
                    ]
                    "non-exhaustive"

            Expect.equal
                (f.Title, f.Edits)
                ("Add missing arm: (false, _)", [ edit 4 21 4 21 $"\n    | (false, _) -> {todo}" ])
                "a tuple witness is one arm"
        }
        test "Add else: on the if's line, or aligned under the line that opened the branch" {
            let f =
                fixes "q.weir" [ "let c = true"; "let x = if c then 1"; "print $\"{x}\"" ] "check"

            Expect.equal (f.Title, f.Edits) ("Add else", [ edit 2 20 2 20 $" else {todo}" ]) ""

            let f =
                fixes "q.weir" [ "let c = true"; "let x ="; "    if c then"; "        1"; "print $\"{x}\"" ] "check"

            Expect.equal (f.Title, f.Edits) ("Add else", [ edit 4 10 4 10 $"\n    else {todo}" ]) ""
        }
        test "Rename to '_x': every named unused variant; the bare '_' and fixAll never" {
            let f = fixes "q.weir" [ "let tmp = 1"; "print \"x\"" ] "unused-binding"
            Expect.equal (f.Title, f.Edits, f.Family) ("Rename to '_tmp'", [ edit 1 5 1 5 "_" ], None) ""

            let f =
                fixes "q.weir" [ "let a = 1"; "let a = 2"; "print $\"{a}\"" ] "unused-binding"

            Expect.equal (f.Title, f.Edits) ("Rename to '_a'", [ edit 1 5 1 5 "_" ]) "the shadow variant"

            match diagsOf "q.weir" [ "let _ = 1"; "print \"x\"" ] with
            | [ d ] -> Expect.equal d.Fix None "a bare '_' is misnamed, not unused"
            | ds -> failtest $"{ds}"
        }
        test "Rename to 'foo': the binder only (its uses are the next diagnostic)" {
            let f = fixFor "q.weir" [ "let Foo = 1"; "print $\"{Foo}\"" ] "lowercase-binding"
            Expect.equal (f.Title, f.Edits) ("Rename to 'foo'", [ edit 1 5 1 8 "foo" ]) ""

            let after =
                diagsOf "q.weir" (applyEdits [ "let Foo = 1"; "print $\"{Foo}\"" ] f.Edits)

            Expect.isFalse (after |> List.exists (fun d -> d.Code = "lowercase-binding")) "the code is gone"
            Expect.equal (after |> List.map _.Code) [ "unbound" ] "the use is now the lesson"

            let f = fixFor "q.weir" [ "let FOO = 1"; "print $\"{FOO}\"" ] "lowercase-binding"
            Expect.equal f.Title "Rename to 'foo'" "all-caps lowercases whole"
        }
        test "Align doc comment: the /// line takes the declaration's indent, family 'doc-align'" {
            let f =
                fixes "q.weir" [ "/// doc for f"; "  /// second"; "let f x = x + 1"; "print $\"{f 1}\"" ] "doc-align"

            Expect.equal (f.Title, f.Edits, f.Family) ("Align doc comment", [ edit 2 1 2 3 "" ], Some "doc-align") ""
        }
        test "Move doc to the signature: two edits against the unedited text" {
            let lines = [ "module"; "let f : int -> int"; "/// the doc"; "let f x = x + 1" ]
            let f = fixes "lib.weir" lines "doc-on-implementation"
            Expect.equal f.Title "Move doc to the signature" ""
            Expect.equal f.Edits [ edit 2 1 2 1 "/// the doc\n"; edit 3 1 4 1 "" ] ""

            Expect.equal
                (applyEdits lines f.Edits)
                [ "module"; "/// the doc"; "let f : int -> int"; "let f x = x + 1" ]
                ""
        }
        test "Add a stub implementation under the signature" {
            let f = fixes "lib.weir" [ "module"; "let g : string" ] "missing-implementation"
            Expect.equal (f.Title, f.Edits) ("Add a stub implementation", [ edit 2 15 2 15 $"\nlet g = {todo}" ]) ""
        }
        test "Call it: a unit -> X statement names the function, not its result [D:structured-fixes]" {
            Expect.equal
                (Weir.Statements.discardError (TFun(TUnit, TInt)))
                (Some
                    "this statement computes a unit -> int and discards it — that is the function itself, not its result: call it with ()")
                "the teaching"

            let f = fixes "q.weir" [ "let helper () = print \"x\""; "helper" ] "discard"
            Expect.equal (f.Title, f.Edits) ("Call it: helper ()", [ edit 2 7 2 7 " ()" ]) ""

            // only a plain name takes the call; a branch would need parens
            match diagsOf "q.weir" [ "let h () = 1"; "let c = true"; "if c then h else h" ] with
            | [ d ] -> Expect.equal d.Fix None "no fix on a compound"
            | ds -> failtest $"{ds}"
        }
        test "Unwrap with |> orFail: the Option/Result mismatch teaches the unwrap, placed where the pipe binds" {
            Expect.equal
                (checkErr "Str.length (Seq.tryHead [\"a\"])").Message
                "expected string, got Option<string> — `|> orFail` unwraps it (raising on None/Error)"
                "the teaching"

            Expect.equal
                (checkErr "Str.length (Seq.tryHead [1])").Message
                "expected string, got Option<int>"
                "a different payload is not an unwrap away"

            // a parenthesised span takes the pipe inside
            let f =
                fixes "q.weir" [ "let n = Str.length (Seq.tryHead [\"a\"])"; "print $\"{n}\"" ] "check"

            Expect.equal (f.Title, f.Edits) ("Unwrap with |> orFail", [ edit 1 38 1 38 " |> orFail" ]) ""

            // mid-expression: parenthesised so the pipe binds to the value
            let f =
                fixes "q.weir" [ "let x = 1"; "let n = x + Seq.tryHead [1]"; "print $\"{n}\"" ] "check"

            Expect.equal f.Edits [ edit 2 13 2 13 "("; edit 2 28 2 28 " |> orFail)" ] ""

            // a whole right-hand side appends
            let f =
                fixes
                    "q.weir"
                    [
                        "let s = \"a\""
                        "let n = if true then s else Seq.tryHead [s]"
                        "print $\"{n}\""
                    ]
                    "check"

            Expect.equal f.Edits [ edit 2 44 2 44 " |> orFail" ] ""
        }
        test "codeActionsFor: a quickfix per fix in range, one source.fixAll per family [D:structured-fixes]" {
            let lines = [
                "let xs = [1; 2]"
                "let a = xs |> where (fun x -> x > 1)"
                "let b = xs |> where (fun x -> x > 0)"
                "let Foo = 1"
                "print $\"{a |> Seq.length} {b |> Seq.length}\""
            ]

            let diags = diagsOf "q.weir" lines
            let acts = Weir.Lsp.codeActionsFor diags None

            Expect.equal
                (acts |> List.map (fun a -> a.Title, a.Kind))
                [
                    "Qualify: Seq.where", "quickfix"
                    "Qualify: Seq.where", "quickfix"
                    "Rename to 'foo'", "quickfix"
                    "Qualify all bare names in file", "source.fixAll"
                ]
                "whole file"

            let all = acts |> List.find (fun a -> a.Kind = "source.fixAll")
            Expect.equal (all.Diagnostics |> List.map _.Line) [ 2; 3 ] "fixAll carries every member"
            Expect.equal all.Edits [ edit 2 15 2 20 "Seq.where"; edit 3 15 3 20 "Seq.where" ] ""
            Expect.equal (acts[0].Diagnostics |> List.map _.Code) [ "parse" ] "a quickfix carries its diagnostic"

            Expect.equal
                (Weir.Lsp.codeActionsFor diags (Some(4, 4)) |> List.map _.Title)
                [ "Rename to 'foo'"; "Qualify all bare names in file" ]
                "the range filters quickfixes; fixAll stays whole-file"

            // the doc-align family; the _-rename has none (R8)
            let acts =
                Weir.Lsp.codeActionsFor
                    (diagsOf "q.weir" [ "  /// a"; "let f x = x"; "  /// b"; "let g x = x"; "print $\"{f 1 + g 1}\"" ])
                    None

            Expect.equal
                (acts |> List.map _.Title)
                [ "Align doc comment"; "Align doc comment"; "Align all doc comments in file" ]
                ""

            let acts =
                Weir.Lsp.codeActionsFor (diagsOf "q.weir" [ "let a = 1"; "let b = 2"; "print \"x\"" ]) None

            Expect.equal
                (acts |> List.map (fun a -> a.Title, a.Kind))
                [ "Rename to '_a'", "quickfix"; "Rename to '_b'", "quickfix" ]
                ""
        }
        test "parity: check --json's fix is the LSP's edit [D:one-pipeline]" {
            let diags = diagsOf "q.weir" [ "let count = 1"; "print $\"{cont}\"" ]
            let d = List.exactlyOne diags

            use doc =
                System.Text.Json.JsonDocument.Parse(
                    Weir.Diagnostics.jsonBuild (fun w -> Weir.Diagnostics.writeDiag w d)
                )

            let fix = doc.RootElement.GetProperty "fix"
            Expect.equal (fix.GetProperty("title").GetString()) "Replace with 'count'" ""

            let fromJson = [
                for e in fix.GetProperty("edits").EnumerateArray() ->
                    edit
                        (e.GetProperty("line").GetInt32())
                        (e.GetProperty("col").GetInt32())
                        (e.GetProperty("endLine").GetInt32())
                        (e.GetProperty("endCol").GetInt32())
                        (e.GetProperty("newText").GetString())
            ]

            Expect.equal fromJson (Weir.Lsp.codeActionsFor diags None |> List.head).Edits ""

            // no fix, no key
            let d = List.exactlyOne (diagsOf "q.weir" [ "let n = 1 + \"a\""; "print $\"{n}\"" ])

            use doc =
                System.Text.Json.JsonDocument.Parse(
                    Weir.Diagnostics.jsonBuild (fun w -> Weir.Diagnostics.writeDiag w d)
                )

            Expect.isFalse (doc.RootElement.TryGetProperty("fix") |> fst) ""
        }
    ]

let functionKeywordTests =
    // `function` — the implicit-match lambda [D:function-keyword]: every
    // pin here holds the form to its own definition, `fun x -> match x
    // with` — same values, same diagnostics, spelling preserved
    testList "function keyword [D:function-keyword]" [
        test "byte-identical to its desugar, value-level" {
            let a = run "(function | 0 -> \"z\" | n when n > 0 -> \"p\" | _ -> \"n\") 5"

            let b =
                run "(fun v -> match v with | 0 -> \"z\" | n when n > 0 -> \"p\" | _ -> \"n\") 5"

            Expect.equal a b "guarded arms agree"
            Expect.equal a (VStr "p") "and produce the guard's arm"
        }
        test "the choose idiom — the receipt's own shape" {
            expectValue
                "[\"a1\"; \"nope\"; \"b2\"] |> Seq.choose (function | Regex @\"([a-z])(\\d)\" (l, d) -> Some $\"{l}{d}\" | _ -> None) |> freeze"
                (VSeq [ VStr "a1"; VStr "b2" ])
        }
        test "the first | is optional, as in F#" {
            let a = run "(function 0 -> \"z\" | _ -> \"n\") 0"
            Expect.equal a (VStr "z") ""
        }
        test "exhaustiveness is match's, message included" {
            let a = (checkErr "(function | 0 -> \"a\") 1").Message
            let b = (checkErr "(fun v -> match v with | 0 -> \"a\") 1").Message
            Expect.equal a b "the two spellings share one diagnostic"
            Expect.stringContains a "needs a catch-all pattern" ""
        }
        test "fmt round-trips the spelling — the trailing (function layout" {
            let src = [
                "let out ="
                "    [\"a1\"; \"b2\"]"
                "    |> Seq.choose (function"
                "        | Regex @\"([a-z])(\\d)\" (l, d) -> Some $\"{l}{d}\""
                "        | _ -> None)"
                ""
                "out |> print"
            ]

            match Weir.Fmt.formatLines src with
            | Ok lines -> Expect.equal lines src "the layout survives fmt"
            | Error e -> failtest e
        }
        test "the reservation is retired: the form parses, the old teaching is gone" {
            match Weir.Parser.parseExpr "(function | 0 -> 1 | _ -> 2) 3" with
            | Ok _ -> ()
            | Error m -> failtest $"the form must parse: {m}"

            // a keyword position still refuses, with the generic message —
            // the reserved-teaching wording no longer exists anywhere
            match Weir.Parser.parseStmt "let function = 1" with
            | Error m ->
                Expect.stringContains m "'function' is a keyword" "the generic refusal"
                Expect.isFalse (m.Contains "reserved") "the reservation wording retired"
            | Ok _ -> failtest "'function' as a binder must reject"
        }
        test "the form-word hover answers with the meaning and the pointer to match" {
            let lines = [ "let f = function"; "    | 0 -> 1"; "    | _ -> 2"; ""; "print (f 0)" ]

            match Weir.Lsp.hoverType lines 1 11 with
            | Some h -> Expect.stringContains h "fun x -> match x with" "the pointer"
            | None -> failtest "function must answer as a form"
        }
        test "`function` inside a string or comment is data — no discovery hover" {
            let inStr = Weir.Lsp.hoverType [ "let m = \"a function word\""; "print m" ] 1 14

            match inStr with
            | Some h -> Expect.isFalse (h.Contains "fun x -> match x with") "a string is data"
            | None -> ()

            Expect.equal
                (Weir.Lsp.hoverType [ "let x = 1 // function | arms"; "print x" ] 1 15)
                None
                "a comment is data"
        }
    ]

// hover text for doc comments [D:hover-reflow]
let hoverReflowTests =
    testList "hover reflow [D:hover-reflow]" [
        test "wrapped doc lines join into one paragraph" {
            Expect.equal
                (Weir.Lsp.reflowDoc [
                    "the badge for the nearest project at or above `dir`, followed by a"
                    "space; \"\" outside any project"
                ])
                "the badge for the nearest project at or above `dir`, followed by a space; \"\" outside any project"
                "one paragraph"
        }
        test "blank lines, list items and fenced code keep their breaks" {
            let doc = [
                "first paragraph"
                "continues"
                ""
                "- one"
                "- two"
                "```"
                "let x ="
                "    1"
                "```"
            ]

            Expect.equal
                (Weir.Lsp.reflowDoc doc)
                "first paragraph continues\n\n- one\n- two\n```\nlet x =\n    1\n```"
                "structure kept"
        }
    ]

// the framing the language server and the debug adapter share
// [D:one-framing]: its bounds are the transport's [D:lsp-transport-caps]
let framingTests =
    testList "Content-Length framing, one reader for both servers" [
        test "a message, an oversized body dropped with the stream still framed, and the end" {
            let frame (body: string) =
                System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n{body}")

            let stream =
                new MemoryStream(
                    Array.concat [
                        frame "{\"a\":1}"
                        frame (System.String('x', 64))
                        System.Text.Encoding.ASCII.GetBytes "content-length: 2\r\nX-Other: y\r\n\r\nok"
                    ]
                )

            let text (r: Weir.Framing.Read) =
                match r with
                | Weir.Framing.Message b -> "message " + System.Text.Encoding.ASCII.GetString b
                | Weir.Framing.Dropped n -> $"dropped {n}"
                | Weir.Framing.End -> "end"

            Expect.equal (text (Weir.Framing.read stream 32)) "message {\"a\":1}" "a body under the cap"
            Expect.equal (text (Weir.Framing.read stream 32)) "dropped 64" "a body over it is read past, never held"

            Expect.equal
                (text (Weir.Framing.read stream 32))
                "message ok"
                "the next frame is intact; the header's case and its neighbours do not matter"

            Expect.equal (text (Weir.Framing.read stream 32)) "end" "the stream's end"
        }
        test "a body cut short, a header with no length and a header line past its bound all end the read" {
            let ends (bytes: byte[]) =
                Weir.Framing.read (new MemoryStream(bytes)) 1024 = Weir.Framing.End

            Expect.isTrue
                (ends (System.Text.Encoding.ASCII.GetBytes "Content-Length: 10\r\n\r\nshort"))
                "a body cut short"

            Expect.isTrue (ends (System.Text.Encoding.ASCII.GetBytes "X: y\r\n\r\nbody")) "no Content-Length"

            Expect.isTrue
                (ends (Array.append (Array.create (Weir.Framing.maxHeaderLineBytes + 10) (byte 'h')) "\r\n\r\n"B))
                "a header line past its bound"
        }
        test "a written message reads back" {
            let out = new MemoryStream()
            let body = System.Text.Encoding.UTF8.GetBytes "{\"é\":true}"
            Weir.Framing.write out body body.Length
            out.Position <- 0L

            match Weir.Framing.read out 1024 with
            | Weir.Framing.Message b -> Expect.equal b body "the same bytes"
            | other -> failtest $"{other}"
        }
    ]

// the REPL's tint and the editor's tokens say the same of a command line
// [D:tint-parity]: one reads the text as typed, the other the checked
// tree, so no code is shared — this corpus is what holds them together
let tintParityTests =
    testList "the REPL's tint and the semantic tokens agree in command mode" [
        test "head, argv and splice: every token the editor emits is tinted so, and nothing else is argv" {
            let replClasses (line: string) : string =
                let painted = Weir.ReplColor.colorizeRepl (fun _ -> false) line
                let out = System.Text.StringBuilder()
                let mutable i = 0
                let mutable code = ""

                while i < painted.Length do
                    if painted[i] = '\u001b' then
                        let close = painted.IndexOf('m', i)
                        let c = painted.Substring(i + 2, close - i - 2)
                        code <- (if c = "0" then "" else c)
                        i <- close + 1
                    else
                        out.Append(
                            match code with
                            | "2" -> 'a'
                            | "36" -> 's'
                            | "1"
                            | "34"
                            | "1;34" -> 'h'
                            | _ -> '.'
                        )
                        |> ignore

                        i <- i + 1

                out.ToString()

            let tokenClasses (lines: string list) (lineNo: int) : string =
                let classes = Array.create lines[lineNo].Length '.'

                for (l, c, len, ty) in Weir.Lsp.semanticTokensFor lines do
                    if l = lineNo then
                        for k in c .. c + len - 1 do
                            classes[k] <-
                                (match ty with
                                 | 0 -> 'h'
                                 | 1 -> 'a'
                                 | _ -> 's')

                System.String classes

            for pre, line in
                [
                    [], "git log --oneline -n 5"
                    [], "git tag v0.123.0"
                    [ "let x = \"a\"" ], "git tag $x"
                    [], "let h = git log |> Seq.head"
                    [], "git commit -m \"msg\""
                    [], "A=1 git status"
                    [], "A=dkr@22ae git status"
                    [], "git log --format=%h"
                    [ "let files = [\"a\"]" ], "git add $@files"
                    [ "let files = [\"a\"]" ], "git add $@(files) -v"
                    [], "let n = $(git rev-parse HEAD) |> Seq.length"
                    [], "^git status --short"
                    [], "git diff -- path/to/file.txt"
                    [], "let ok = git status | succeeds"
                    [ "let n = 3" ], "git log -n $n --stat"
                    [ "let d = \"x\"" ], "git -C (d) status"
                    [ "let d = \"x\"" ], "git -C (Path.combine d (\"y\")) status"
                    [], "let r = git status | complete"
                    [], "git log --since=2.weeks ./src"
                    [], "git -C $HOME status"
                    [ "let n = 3" ], "git log $PWD -n $n"
                ] do
                let repl = replClasses line
                let tokens = tokenClasses (pre @ [ line ]) (List.length pre)
                Expect.equal repl.Length tokens.Length line

                for k in 0 .. line.Length - 1 do
                    let t, r = tokens[k], repl[k]

                    // the force prefix rides in the editor's head span; the
                    // REPL tints it as the sigil it is
                    let agrees = (t = '.' && r <> 'a') || t = r || (t = 'h' && line[k] = '^' && r = 's')

                    if not agrees then
                        failtest $"column {k + 1} of `{line}`: the editor says {t}, the REPL {r}\n  {tokens}\n  {repl}"
        }
    ]
