module TestPrelude

// The helpers every test file shares: how a test parses, checks and runs a
// program, and the fixtures a few suites build on. Opened by each suite file.

open System.Diagnostics
open System.IO
open Expecto
open Weir.Ast
open Weir.Types
open Weir.Argv
open Weir.Check
open Weir.Eval

let parse input =
    match Weir.Parser.parseExpr input with
    | Ok e -> e
    | Error msg -> failtest $"parse failed: {msg}"

// [D:sibling-sentinel] the assembler joins statement siblings with the
// machine sentinel, not ';' (command mode stops at it; a user ';' does
// not). Assembler-text pins spell the join as a readable ';'; asmSib
// rewrites that display ';' to the sentinel the assembler emits.
let asmSib (s: string) =
    s.Replace(" ; ", " " + Weir.Parser.sibSepStr + " ")

// [D:field-sep-sentinel] record fields and list elements joined across
// lines get the field sentinel, not ';' (a field value's own ';' cannot
// cross it). Same display-';'-to-sentinel rewrite as asmSib, for the
// bracket/record-continuation pins.
let asmField (s: string) =
    s.Replace(" ; ", " " + Weir.Parser.fieldSepStr + " ")

// the span-free sexpr renderer moved to Ast (shared with fmt's
// respace safety check [D:fmt-respace]); these aliases keep the pins
let show = Weir.Ast.sexpr

let showPat = Weir.Ast.sexprPat

let expectParse input expected =
    Expect.equal (show (parse input)) expected $"parse of '{input}'"

let parseDecl input =
    match Weir.Parser.parseStmt input with
    | Ok(SType d) -> d
    | other -> failtest $"expected a type declaration, got: {other}"

let declare input env =
    match Weir.Check.checkDecl env (parseDecl input) with
    | Ok env' -> env'
    | Error terr -> failtest $"declaration failed: {formatError terr}"

let declErr input env =
    match Weir.Check.checkDecl env (parseDecl input) with
    | Ok _ -> failtest "expected the declaration to be rejected"
    | Error terr -> terr

let preludeTypeEnv, preludeValueEnv =
    Weir.Prelude.extend Weir.Builtins.typeEnv Weir.Builtins.valueEnv.Value

let env =
    let e =
        preludeTypeEnv
        |> declare "type Job = Running of int | Stopped"
        // a 2-param generic union — the Result-shaped type-system
        // fixture; a local declaration, not a prelude type [D:no-result]
        |> declare "type Either<'a, 'e> = Left of 'a | Right of 'e"
        // a record with an Option<scalar> field — the JSON-boundary fixture [D:json-option]
        |> declare "type JOpt = { name: string; age: Option<int> }"
        // the json clusters' stand-in row: FileRow-shaped but jsonable —
        // FileRow.bytes became a Size, which the wire refuses [D:size]
        |> declare "type JRow = { name: string; bytes: int; readOnly: bool }"
        |> declare "type Point = { X: int; Y: int }"
        // record-update battery: two records sharing an int field N
        |> declare "type UpdP = { UpN: int; UpT: string }"
        |> declare "type UpdQ = { UpN: int }"
        |> declare "type UpdS = { UpN: string; UpS: string }"
        // attributed twin for the erasure pin [D:attributes]
        |> declare "type AttrE = { [<Short \"q\">] Q: int }"
        // the XML-boundary fixtures [D:from-xml]: an attribute-bearing
        // leaf, an optional-text property group, and the csproj-shaped root
        |> declare "type XRef = { [<Attr>] Include: string }"
        |> declare "type XPg = { IsPackable: Option<string>; IsPublishable: Option<string> }"
        |> declare
            "type XProj = { [<Elem \"PropertyGroup\">] groups: seq<XPg>; [<Elem \"ProjectReference\">] refs: seq<XRef> }"
        // the XML-boundary rejects: a numeric leaf, [<Attr>] on a non-text
        // field, [<Elem>] on a non-seq — each teaches at check time
        |> declare "type XInt = { xmlNum: int }"
        |> declare "type XBadAttr = { [<Attr>] xs: seq<string> }"
        |> declare "type XBadElem = { [<Elem \"X\">] s: string }"

    {
        e with
            Values =
                e.Values
                |> Map.add "src" (generalize (TSeq TStr))
                |> Map.add "double" (generalize (TFun(TInt, TInt)))
    }

let ctorValues =
    [
        "type Job = Running of int | Stopped"
        "type Either<'a, 'e> = Left of 'a | Right of 'e"
    ]
    |> List.collect (fun d ->
        match (parseDecl d).Body with
        | DUnion cases -> constructorValues cases
        | DRecord _ -> [])

let fakeFiles = [
    Weir.Builtins.file "a.txt" 0 false
    Weir.Builtins.file "b.bin" 5242880 true
    Weir.Builtins.file "c.log" 1048576 false
    Weir.Builtins.file "d.iso" 3145728 false
]

let doubleFixture =
    VBuiltin(fun v ->
        match v with
        | VInt n -> VInt(n * 2L)
        | v -> failwith $"double fixture applied to {formatValue v}")

let valueEnv =
    ("ls", VSeq fakeFiles) :: ("double", doubleFixture) :: ctorValues
    |> List.fold (fun vs (n, v) -> Map.add n v vs) preludeValueEnv

let checkOk input =
    match typecheck env (parse input) with
    | Ok te -> te
    | Error terr -> failtest $"expected Ok, got: {formatError terr}"

let checkErr input =
    match typecheck env (parse input) with
    | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
    | Error terr -> terr

// the type a fixture value is bound at: its own where the value says, a
// type variable where it cannot (a live seq, a mixed list, a function)
let private tyOfValue (v: Value) : Ty =
    let mutable n = 0

    let fresh () =
        n <- n + 1
        TVar $"fx{n}"

    let rec go (v: Value) : Ty =
        match v with
        | VInt _ -> TInt
        | VFloat _ -> TFloat
        | VStr _ -> TStr
        | VBool _ -> TBool
        | VUnit -> TUnit
        | VDur _ -> TDur
        | VInstant _ -> TInstant
        | VUuid _ -> TUuid
        | VSize _ -> TSize
        | VBytes _ -> TBytes
        | VSecret _ -> TSecret
        | VTuple vs -> TTuple(vs |> List.map go)
        | VUnion("Some", Some p) -> TNamed("Option", [ go p ])
        | VUnion("None", None) -> TNamed("Option", [ fresh () ])
        | VRecord(name, _) when
            (Map.tryFind name env.Types
             |> Option.exists (fun d ->
                 match d with
                 | Record r -> r.Params.IsEmpty
                 | _ -> false))
            ->
            TNamed(name, [])
        | VSeq items ->
            match box items with
            | :? (Value list) as l ->
                match l |> List.map go |> List.distinct with
                | [ t ] when Set.isEmpty (tyVarsOf t) -> TSeq t
                | _ -> TSeq(fresh ())
            | _ -> TSeq(fresh ())
        | _ -> fresh ()

    go v

let runWith (overrides: (string * Value) list) input =
    let venv = overrides |> List.fold (fun vs (n, v) -> Map.add n v vs) valueEnv

    // a value whose type cannot be told keeps the env's own binding
    let tenv =
        overrides
        |> List.fold
            (fun (e: TypeEnv) (n, v) ->
                let ty = tyOfValue v

                if Set.isEmpty (tyVarsOf ty) then
                    {
                        e with
                            Values = Map.add n (generalize ty) e.Values
                    }
                else
                    e)
            env

    match typecheck tenv (parse input) with
    | Ok te -> eval venv te
    | Error terr -> failtest $"expected Ok, got: {formatError terr}"

let run input = eval valueEnv (checkOk input)

let forceSeq v =
    match v with
    | VSeq items -> List.ofSeq items
    | v -> failtest $"expected a seq, got {formatValue v}"

let runUnder tenv input =
    match typecheck tenv (parse input) with
    | Ok te -> eval valueEnv te
    | Error terr -> failtest (formatError terr)

let checkErrUnder tenv input =
    match typecheck tenv (parse input) with
    | Ok te -> failtest $"expected a type error, got {formatTy te.Ty}"
    | Error terr -> terr

let msgOf (src: string) =
    (Expect.throwsC (fun () -> run src |> ignore) id).Message

let asm lines' =
    match Weir.Assemble.assemble (lines' |> List.mapi (fun i l -> i + 1, l)) with
    | Ok [ ll ] -> ll.Text
    | other -> failtest $"assembly: {other}"

let yamlDoc lines' =
    match Weir.Yaml.parseDocs (lines' |> List.mapi (fun i l -> i + 1, l)) with
    | Ok [ d ] -> d
    | other -> failtest $"parse: {other}"

let yamlErr lines' =
    match Weir.Yaml.parseDocs (lines' |> List.mapi (fun i l -> i + 1, l)) with
    | Error e -> e
    | Ok d -> failtest $"expected an error, got {d}"

let diagsIn file (lines: string list) =
    let ds, _, _, _, _ = Weir.Analyze.analyzeLines file lines
    ds

let errorsIn file lines =
    diagsIn file lines |> List.filter (fun d -> d.Severity = "error")

let errorMsgsIn file lines =
    errorsIn file lines |> List.map (fun d -> d.Message)

let firstErrorIn file lines =
    match errorsIn file lines with
    | e :: _ -> e
    | [] -> failtest "must refuse"

let mustSayIn file lines (needle: string) (label: string) =
    Expect.exists
        (diagsIn file lines)
        (fun d -> d.Message.Contains needle)
        $"{label}: expected '{needle}', got {diagsIn file lines |> List.map _.Message}"

let mustSayDiagIn file lines (needle: string) (label: string) =
    Expect.exists
        (diagsIn file lines)
        (fun d -> d.Message.Contains needle)
        $"{label}: expected a diagnostic containing '{needle}', got {diagsIn file lines |> List.map _.Message}"

let cleanIn file lines (label: string) =
    Expect.isEmpty (diagsIn file lines) $"{label}: expected clean, got {diagsIn file lines |> List.map _.Message}"

let errorsCleanIn file lines (label: string) =
    let diags = diagsIn file lines
    Expect.isEmpty (diags |> List.filter (fun d -> d.Severity = "error")) $"{label}: {diags |> List.map _.Message}"

let checkEqRun lines =
    let diags = diagsIn "pin.weir" lines
    Expect.isEmpty diags $"check == run: {lines}"

let tempPath (prefix: string) =
    Path.Combine(Path.GetTempPath(), $"{prefix}-{System.Guid.NewGuid():N}")

let tempDir prefix =
    let dir = tempPath prefix
    Directory.CreateDirectory dir |> ignore
    dir

let withTempDir prefix (f: string -> 'a) : 'a =
    let dir = tempDir prefix

    try
        f dir
    finally
        Directory.Delete(dir, true)

// the invariant process cwd, not Session.Cwd(): under the
// parallel test runner, Session.Cwd() may momentarily hold a
// concurrent test's temp dir, and restoring to it after that
// dir is deleted leaves the global cwd pointing at a deleted
// path — every concurrent spawn then fails "command not found"
// (Process.Start cannot chdir there). GetCurrentDirectory is
// never mutated (weir tracks cwd in Session, not the process).
let inCwd (dir: string) (f: unit -> 'a) : 'a =
    let saved = Directory.GetCurrentDirectory()
    Weir.Session.setCwd dir

    try
        f ()
    finally
        Weir.Session.setCwd saved

// the shared valueEnv shadows ls with fakeFiles — these
// pins need the real prelude ls
let runLive input =
    match typecheck env (parse input) with
    | Ok te -> eval Weir.Builtins.valueEnv.Value te
    | Error terr -> failtest (formatError terr)

let fakeExternals = Set [ "git"; "grep"; "echo"; "yes"; "true"; "ls"; "cat"; "rm" ]

let cmdResolver: Weir.Parser.Resolver = {
    IsKnown = fun n -> Map.containsKey n env.Values
    IsCommandCallable = fun n -> Weir.Builtins.commandCallable.Contains n
    IsExternal = fun p -> fakeExternals.Contains p || p = "./build.sh"
    ExternalNames = (fun () -> fakeExternals)
    BareHome = (fun _ -> None)
    AliasHead = fun _ -> None
    IsBuiltinName = fun _ -> false
}

// Windows: parse-only fixtures resolve coreutils heads (echo, sh,
// grep ...) through the real resolver, and those are cmd builtins or
// absent there — no executable to find. Resolution is File.Exists, not
// execution, so empty <name>.exe shims on a prepended PATH dir make
// every parse pin platform-independent [D:windows-v1]. Tests that run
// these tools stay skipOnWindows (an empty exe cannot run). POSIX: no-op.
let _windowsParseShims =
    if System.OperatingSystem.IsWindows() then
        let dir = tempDir "weir-shims"

        // sort deliberately absent: System32 ships a real sort.exe
        // that tests run — an empty shadow would break it
        for tool in
            [
                "echo"
                "printf"
                "grep"
                "sh"
                "yes"
                "ls"
                "cat"
                "head"
                "tail"
                "wc"
                "tr"
                "seq"
            ] do
            File.WriteAllText(Path.Combine(dir, tool + ".exe"), "")

        System.Environment.SetEnvironmentVariable(
            "PATH",
            dir
            + string Path.PathSeparator
            + System.Environment.GetEnvironmentVariable "PATH"
        )

        Weir.Extern.refresh ()

let realResolver: Weir.Parser.Resolver = {
    IsKnown = fun n -> Map.containsKey n env.Values
    IsCommandCallable = fun n -> Weir.Builtins.commandCallable.Contains n
    IsExternal = Weir.Extern.exists
    ExternalNames = (fun () -> Weir.Extern.names () :> seq<string>)
    BareHome = (fun n -> Map.tryFind n Weir.Builtins.bareAliasHomes)
    AliasHead = fun _ -> None
    IsBuiltinName = fun _ -> false
}

let parseCmd input =
    match Weir.Parser.parseLine cmdResolver input with
    | Ok(SExpr e)
    | Ok(SCmd e) -> e
    | Ok other -> failtest $"expected an expression line, got {other}"
    | Error msg -> failtest $"parse failed: {msg}"

let expectCmd input expected =
    Expect.equal (show (parseCmd input)) expected $"parse of '{input}'"

let runReal input =
    match Weir.Parser.parseLine realResolver input with
    | Error msg -> failtest $"parse failed: {msg}"
    | Ok(SExpr e)
    | Ok(SCmd e) ->
        match typecheck env e with
        | Error terr -> failtest (formatError terr)
        | Ok te -> eval valueEnv te
    | Ok other -> failtest $"unexpected: {other}"

let expectValue input expected =
    Expect.equal (run input) expected $"eval of '{input}'"

// ---- Windows-v1 harness helpers [D:windows-v1] ----------------------------
// weirPath: a native path made safe to interpolate into weir source —
// weir strings treat `\` as an escape, so a Windows temp path must ride
// as forward slashes (liberal input: Windows APIs accept them).
// Every platform path interpolated into weir source routes through
// this (a raw C:\ path in a plain string trips the \U escape teaching
// — the Windows first-run's whole fixture class); and expected
// messages pin the shape + the name, never the platform's rendering
let weirPath (p: string) = p.Replace('\\', '/')

// platformPath: a POSIX-spelled expected value converted to the
// platform's separator — the suite's spelling for the Path members'
// native-output ruling (identity on POSIX)
let platformPath (p: string) =
    p.Replace('/', System.IO.Path.DirectorySeparatorChar)

// a fixture that is about POSIX tools or absolute POSIX paths (sh, yes,
// /tmp, /bin) — marked, not shimmed; the session report carries the
// shim-candidate list for when the CI matrix can verify conversions
let skipOnWindows () =
    if System.OperatingSystem.IsWindows() then
        skiptest "POSIX fixture (sh/coreutils/absolute POSIX path)"

let acceptance = "ls |> where (fun f -> f.bytes > 1MiB) |> take 5"

let warningsOf input = Weir.Check.warnings (checkOk input)

let suggest text (wordStart: int) =
    Weir.Complete.suggest env text wordStart

let survivors (marker: string) : int =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    // pgrep rc 0 = matched, 1 = no match; anything else (or pgrep
    // missing, 127) is the probe failing — loud, never a benign zero
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

let eventuallyNoSurvivors (marker: string) : bool =
    let mutable tries = 20
    let mutable count = survivors marker

    while count > 0 && tries > 0 do
        System.Threading.Thread.Sleep 100
        tries <- tries - 1
        count <- survivors marker

    count = 0

let defunctChildrenOf (pid: int) : int =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    // BSD ps has no --ppid; `-A -o ppid=,stat=` is portable. The ps run
    // must fail loudly (exit 9): on macOS the GNU spelling errored and
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

let defunctChildren () : int =
    defunctChildrenOf System.Environment.ProcessId

// the derived-ordering fixtures [D:derived-ord]: a check env and a
// value env built from the same declarations, so a type's key registers
// as a script's `type` statement would
let ordDecls = [
    "type Pre = [<Ord 1>] Pre of string | [<Ord 2>] Release"
    "type SemVer = { [<Ord 1>] major: int; [<Ord 2>] minor: int; [<Ord 3>] patch: int; [<Ord 4>] pre: Pre }"
    "type OrdP = { [<Ord 1>] a: int; [<Ord 2>] b: string }"
    "type OrdU = [<Ord 1>] OA | [<Ord 2>] OB of int"
    "type OrdBox<'a> = { [<Ord 1>] v: 'a }"
    "type OrdTree = [<Ord 1>] Leaf | [<Ord 2>] Node of OrdTree"
    "type OrdF = { [<Ord 1>] x: float }"
    "type OrdK = { [<Ord 1>] z: int; [<Ord 2>] a: int }"
    "type OrdV = { [<Ord 1>] major: int; [<Ord 2>] minor: int }"
    "type OrdDep = { [<Ord 1>] replicas: int }"
    "[<Tag \"kind\">] type OrdTagged = [<Ord 1>] Ping | [<Ord 2>] Deployment of OrdDep"
    // the key is the numbers, not the declaration: `second` is written
    // first and numbered second; `note` is unnumbered and takes no part
    "type OrdRev = { [<Ord 2>] first: int; [<Ord 1>] second: int }"
    "type OrdPart = { [<Ord 1>] key: int; note: string; logs: seq<string> }"
    "type Plain = { p: int }"
    "type PlainU = PA | PB"
]

let ordEnv = ordDecls |> List.fold (fun e d -> declare d e) env

let ordValueEnv =
    ordDecls
    |> List.fold (fun ve d -> Weir.Eval.declare ve (parseDecl d) None) valueEnv

let runOrd input =
    match typecheck ordEnv (parse input) with
    | Ok te -> eval ordValueEnv te
    | Error terr -> failtest (formatError terr)

let ordShow input expected =
    Expect.equal (runOrd $"show ({input})") (VStr expected) $"eval of '{input}'"

let ordDeclMsg input = (declErr input ordEnv).Message

let ordErrMsg input = (checkErrUnder ordEnv input).Message

// the property's shape: F#'s derived comparison on this record IS the
// derived order (every field numbered in declaration order, None first,
// cases by position), so `compare` referees orderCompare in-process
type FxU =
    | FA
    | FB of int

type Fx = {
    fa: int
    fs: string
    fo: int option
    fu: FxU
}

let fxNorm (r: Fx) = {
    r with
        fs = (if isNull r.fs then "" else r.fs)
}

let fxValue (r: Fx) : Value =
    VRecord(
        "Fx",
        [
            "fa", VInt(int64 r.fa)
            "fs", VStr r.fs
            "fo",
            (match r.fo with
             | None -> VUnion("None", None)
             | Some n -> VUnion("Some", Some(VInt(int64 n))))
            "fu",
            (match r.fu with
             | FA -> VUnion("FA", None)
             | FB n -> VUnion("FB", Some(VInt(int64 n))))
        ]
    )

let registerFx () =
    Weir.Eval.registerOrdCases "FxU" [ "FA", 1L; "FB", 2L ]
    Weir.Eval.registerOrdFields "Fx" [ "fa"; "fs"; "fo"; "fu" ]

let readProbes =
    testList "Read probes" [
        test "(a) generic constructor inside a row constraint discharges" {
            let e2 =
                env
                |> declare "type OptHolder = { X: Option<int>; TagA: bool }"
                |> declare "type StrHolder = { X: Option<string>; TagB: bool }"

            match Weir.Check.typecheck e2 (parse "let g = fun r -> r.X == Some 1 in g { X = Some 2; TagA = true }") with
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
            skipOnWindows ()

            let marker = weirPath (tempPath "weir-sc")

            try
                Expect.equal (runReal $"false && ($(sh -c \"touch {marker}; echo x\") |> Seq.isEmpty)") (VBool false) ""

                Expect.isFalse (File.Exists marker) "right operand must not spawn"

                Expect.equal (runReal $"true && ($(sh -c \"touch {marker}; echo x\") |> Seq.isEmpty)") (VBool false) ""

                Expect.isTrue (File.Exists marker) "strict when left is true"
            finally
                if File.Exists marker then
                    File.Delete marker
        }
    ]

// [D:depth-guard]: the safe-by-design review found unbounded expression
// depth crashed the process (SEGV) — the bound is now a machine-checked
// invariant with a located diagnostic, not a prose promise.
let nestDeep opener closer n =
    String.replicate n opener + "1" + String.replicate n closer

// ---- #save DISTILL [D:repl-save] -------------------------------------
// the distill seam: transcript survivors (a `TDef` name + physical
// source) through qualify -> dedup(last) -> the check guarantee. The
// pins mirror the e2e cell but exercise the pure core directly, so a
// regression in dedup/drop/protect is caught without driving a session.
let distill (defs: (string * string) list) : string list * int =
    Weir.Repl.distillDefs realResolver (fun _ -> None) defs

// the distilled lines must themselves weir-check clean — the
// acceptance-defining guarantee, asserted at the unit seam
let distilledChecks (lines: string list) : bool =
    if List.isEmpty lines then
        true
    else
        let diags = diagsIn "#save-test" lines
        not (diags |> List.exists (fun d -> d.Severity = "error"))

// ---- command-head aliases [D:command-head-alias] ---------------------
// the alias table maps a short head to (real exe, fixed prefix args),
// REPL-only, consulted only in command-head position and before PATH;
// `^` skips it and it is single-hop by construction.

let aliasTable: Map<string, string * string list> =
    Map.ofList [
        ("k", ("kubectl", []))
        ("kb", ("kustomize", [ "build" ]))
        ("ls", ("ls", [ "--color" ]))
        ("up", ("cd", [ ".." ]))
    ]

let aliasOf n = Map.tryFind n aliasTable

// a resolver with the alias table wired into AliasHead, mirroring the
// REPL's `resolver`. `git` stands in as a definitely-present external.
let aliasResolver: Weir.Parser.Resolver = {
    realResolver with
        AliasHead = aliasOf
}

let parseWith (r: Weir.Parser.Resolver) input =
    match Weir.Parser.parseLine r input with
    | Ok(SExpr e)
    | Ok(SCmd e) -> Ok(show e)
    | Ok other -> Error $"unexpected: {other}"
    | Error msg -> Error $"parse failed: {msg}"

// hunt 3 round 2's cancellation seams; the signal, stdin and pty halves are
// e2e cells
// run `body` as an arm in a node of its own, cut (a lost race) after `ms`
// [D:scope-tree]
let asArmCutAfter (ms: int) (body: unit -> unit) =
    let n =
        Weir.Session.openNode (Weir.Session.currentNode ()) Weir.Session.NodeKind.Arm

    Weir.Session.setCurrentNode n

    try
        System.Threading.Tasks.Task.Run(fun () ->
            System.Threading.Thread.Sleep ms
            Weir.Session.cutNode n 1 Weir.Session.CutReason.RaceLost true)
        |> ignore

        body ()
    finally
        Weir.Session.setCurrentNode null
        Weir.Session.closeNode n

// hunt 3 round 3's cancellation seams; the signal, teardown and serve
// halves are e2e cells
let skipUnlessLinux () =
    if not (System.OperatingSystem.IsLinux()) then
        skiptest "Linux (the /proc tree walk, a polled FIFO)"

let growsWithin (factor: float) (label: string) (small: unit -> unit) (big: unit -> unit) =
    let time (f: unit -> unit) =
        let sw = Stopwatch.StartNew()
        f ()
        sw.Elapsed.TotalMilliseconds

    small () // warm the JIT and caches
    // best of three each, and the small side floored at 10ms: a few-ms
    // baseline is all GC pause and tier-up noise on a shared runner, while
    // the quadratic forms these guard against overshoot the floor 70x+
    let best (f: unit -> unit) =
        List.init 3 (fun _ -> time f) |> List.min

    let within (ts, tb) = tb <= factor * max ts 10.0
    // up to three rounds: a shared runner's stall can land on one big run,
    // while a real cliff fails every round
    let rec rounds n =
        let ts, tb = best small, best big

        if within (ts, tb) || n = 1 then
            (ts, tb)
        else
            rounds (n - 1)

    let ts, tb = rounds 3

    Expect.isTrue (within (ts, tb)) $"{label}: {tb:F0}ms vs {ts:F0}ms for the small input — over {factor}x"

let growsLinearly label small big = growsWithin 25.0 label small big

// the replaced Seq.distinct, kept as the reference: a list scan with
// Value's own equality
let distinctReference (xs: Value list) =
    let seen = ResizeArray<Value>()

    [
        for x in xs do
            if not (seen.Contains x) then
                seen.Add x
                yield x
    ]
