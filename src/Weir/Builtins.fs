module Weir.Builtins

open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Weir.Types
open Weir.Eval

let fileRow: RecordDef =
    { Name = "FileRow"
      Params = []
      Fields = [ "name", TStr; "bytes", TInt; "readOnly", TBool ]
      Attrs = Map.empty
      Docs = Map.empty }

let seqFileRow = TSeq(TNamed(fileRow.Name, []))

let file (name: string) (bytes: int64) (readOnly: bool) : Value =
    VRecord(fileRow.Name, Map [ "name", VStr name; "bytes", VInt bytes; "readOnly", VBool readOnly ])

let private realLs: Value =
    VSeq(
        Seq.delay (fun () ->
            DirectoryInfo(Session.Cwd()).GetFiles()
            |> Seq.map (fun f -> file f.Name f.Length f.IsReadOnly))
    )

let private whereImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    items
                    |> Seq.filter (fun item ->
                        match apply pred item with
                        | VBool b -> b
                        | v -> unreachable $"the checker rejects a non-bool predicate result: {formatValue v}")
                )
            | v -> unreachable $"the checker rejects 'where' on {formatValue v}"))

let private truncateImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun s ->
            match n, s with
            | VInt n, VSeq items -> VSeq(Seq.truncate (int n) items)
            | _ -> unreachable "the checker rejects truncation on these arguments"))

let private mapImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(items |> Seq.map (apply f))
            | v -> unreachable $"the checker rejects 'map' on {formatValue v}"))

// the match-or-skip member [D:seq-choose]: lazy, constraint-free
let private chooseImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    items
                    |> Seq.choose (fun x ->
                        match apply f x with
                        | VUnion("Some", Some v) -> Some v
                        | VUnion("None", None) -> None
                        | v -> unreachable $"the checker guarantees an Option chooser, got {formatValue v}")
                )
            | v -> unreachable $"the checker rejects 'choose' on {formatValue v}"))

let private appendImpl: Value =
    VBuiltin(fun a ->
        VBuiltin(fun b ->
            match a, b with
            | VSeq xs, VSeq ys -> VSeq(Seq.append xs ys)
            | v, _ -> unreachable $"the checker rejects 'append' on {formatValue v}"))

let private sumImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            let total =
                items
                |> Seq.fold
                    (fun acc v ->
                        match v with
                        | VInt n ->
                            try
                                Checked.(+) acc n
                            with :? System.OverflowException ->
                                failwith "integer overflow in sum"
                        | v -> unreachable $"the checker rejects summing {formatValue v}")
                    0L

            VInt total
        | v -> unreachable $"the checker rejects 'sum' on {formatValue v}")

let private natsImpl: Value = VSeq(Seq.initInfinite (int64 >> VInt))

let private notImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VBool b -> VBool(not b)
        | v -> unreachable $"the checker rejects 'not' on {formatValue v}")

let changeDef: RecordDef =
    { Name = "Change"
      Params = []
      Fields = [ "status", TStr; "staged", TBool; "unstaged", TBool; "path", TStr ]
      Attrs = Map.empty
      Docs = Map.empty }

let private asString (v: Value) : string =
    match v with
    | VStr s -> s
    | v -> unreachable $"the checker rejects non-string command arguments: {formatValue v}"

let private argStrings (args: seq<Value>) : string list = args |> Seq.map asString |> List.ofSeq

let private cmdImpl: Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                if prog.Trim() = "" then
                    failwith "cmd: empty program name"

                let argv = argStrings args
                VSeq(Proc.lines (Proc.resolveProg prog) argv None |> Seq.map VStr)
            | _ -> unreachable "the checker rejects 'cmd' on these arguments"))

let private intoImpl: Value =
    VBuiltin(fun c ->
        VBuiltin(fun sv ->
            match c, sv with
            | VStr cmdline, VSeq items ->
                let lines = items |> Seq.map asString
                VSeq(Proc.lines "/bin/sh" [ "-c"; cmdline ] (Some lines) |> Seq.map VStr)
            | _ -> unreachable "the checker rejects 'into' on these arguments"))

let private cdImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr path ->
            let home =
                System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile

            let expanded =
                if path = "~" then
                    home
                elif path.StartsWith "~/" then
                    Path.Combine(home, path.Substring 2)
                else
                    path

            let resolved = Session.resolve expanded

            if not (Directory.Exists resolved) then
                failwith $"cd: no such directory: {resolved}"

            Session.setCwd resolved
            VStr resolved
        | v -> unreachable $"the checker rejects 'cd' on {formatValue v}")

let private pwdImpl: Value =
    VSeq(Seq.delay (fun () -> Seq.singleton (VStr(Session.Cwd()))))

let private headImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            match Seq.tryHead items with
            | Some x -> x
            | None -> failwith "head: empty sequence"
        | v -> unreachable $"the checker rejects 'head' on {formatValue v}")

let private windowedImpl: Value =
    VBuiltin(fun nV ->
        VBuiltin(fun v ->
            match nV, v with
            | VInt n, VSeq items ->
                if n <= 0L then
                    failwith $"windowed: the window size must be positive; got {n}"
                else
                    // lazy per the family: windows are produced as the
                    // source is pulled; a short source yields the EMPTY
                    // seq (F#'s rule — no partial final window). Windows
                    // are views over the same (memoized-once) elements.
                    VSeq(items |> Seq.windowed (int n) |> Seq.map (fun w -> VSeq(Seq.ofArray w)))
            | _ -> unreachable "the checker rejects 'windowed' on these arguments"))

let private lastImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            // ASSERTS non-empty (the X/tryX rule); forces the whole
            // source by necessity
            let mutable acc = ValueNone

            for x in items do
                acc <- ValueSome x

            match acc with
            | ValueSome x -> x
            | ValueNone -> failwith "last: empty sequence"
        | v -> unreachable $"the checker rejects 'last' on {formatValue v}")

let private tryLastImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            let mutable acc = ValueNone

            for x in items do
                acc <- ValueSome x

            match acc with
            | ValueSome x -> VUnion("Some", Some x)
            | ValueNone -> VUnion("None", None)
        | v -> unreachable $"the checker rejects 'tryLast' on {formatValue v}")

let private toListImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            let materialized = List.ofSeq items
            VSeq(materialized :> seq<Value>)
        | v -> unreachable $"the checker rejects 'toList' on {formatValue v}")

let completedDef: RecordDef =
    { Name = "Completed"
      Params = []
      Fields = [ "exitCode", TInt; "stdout", TSeq TStr; "stderr", TSeq TStr ]
      Attrs = Map.empty
      Docs = Map.empty }

// completedWith is the shared body; completed IS the empty overlay and
// completedEnv the env-sigil desugar target — the cmd/cmdEnv pattern.
let private completedWith (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                let argv = argStrings args

                let code, stdout, stderr =
                    Proc.completeWith overlay (Proc.resolveProg prog) argv None

                VRecord(
                    completedDef.Name,
                    Map
                        [ "exitCode", VInt(int64 code)
                          // lazy views over the capture buffer
                          // [D:capture-buffer] — decode per pull, stable
                          // on re-enumeration (the buffer is fixed)
                          "stdout", VSeq(stdout |> Seq.map VStr)
                          "stderr", VSeq(stderr |> Seq.map VStr) ]
                )
            | _ -> unreachable "the checker rejects 'completed' on these arguments"))

let private completedImpl: Value = completedWith []

// the exit-code reifiers [D:exit-reifiers], under the one law: output
// goes where the meaning goes. succeeds is exitCode == 0 EXACTLY,
// output captured-and-discarded (a predicate is silent); orFail and
// exitCode STREAM (their output is for the human — the result travels
// separately): orFail raises `msg (exit N)` on nonzero, exitCode
// yields the code as int and never raises.
let private succeededWith (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                let argv = argStrings args
                let code, _, _ = Proc.completeWith overlay (Proc.resolveProg prog) argv None
                VBool(code = 0)
            | _ -> unreachable "the checker rejects 'succeeded' on these arguments"))

let private orFailedWith (overlay: (string * string) list) : Value =
    VBuiltin(fun msgV ->
        VBuiltin(fun progV ->
            VBuiltin(fun argsV ->
                match msgV, progV, argsV with
                | VStr msg, VStr prog, VSeq args ->
                    let argv = argStrings args
                    let code = Proc.streamCode overlay (Proc.resolveProg prog) argv

                    if code <> 0 then
                        failwith $"{msg} (exit {code})"

                    VUnit
                | _ -> unreachable "the checker rejects 'orFailed' on these arguments")))

// feed = cmd + stdin [D:spawn-spec]: the family's first data-LAST
// member, because its data is the pipeline's subject
// (`snips |> feed "sha256sum" []`); lifecycle inherited from the one
// spawn by construction
let private feedWith (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            VBuiltin(fun inputV ->
                match progV, argsV, inputV with
                | VStr prog, VSeq args, VSeq input ->
                    let argv = argStrings args
                    let inputLines = input |> Seq.map asString

                    VSeq(
                        Seq.delay (fun () ->
                            Proc.linesWith overlay (Proc.resolveProg prog) argv (Some inputLines)
                            |> Seq.map VStr)
                    )
                | _ -> unreachable "the checker rejects 'feed' on these arguments")))

let private exitCodedWith (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                let argv = argStrings args
                VInt(int64 (Proc.streamCode overlay (Proc.resolveProg prog) argv))
            | _ -> unreachable "the checker rejects 'exitCoded' on these arguments"))

// stdin-carrying reifier twins [D:value-headed-pipe]: `xs | grep foo |
// complete` reifies the segment WITH the value as stdin. INTERNAL —
// the public expression-position spellings (completed/succeeded/…) keep
// their arities exactly; these take a trailing seq<string>. The input
// param was always on Proc.completeWith / the streaming Spec — session
// 1's named seam, now reached. (Env twins are unpopulated: a value-headed
// pipe carries no env sigil today — spawn-park pressure note in NOTES.)
let private completedWithIn (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            VBuiltin(fun stdinV ->
                match progV, argsV, stdinV with
                | VStr prog, VSeq args, VSeq stdin ->
                    let argv = argStrings args
                    let input = stdin |> Seq.map asString

                    let code, out, err =
                        Proc.completeWith overlay (Proc.resolveProg prog) argv (Some input)

                    VRecord(
                        completedDef.Name,
                        Map
                            [ "exitCode", VInt(int64 code)
                              "stdout", VSeq(out |> Seq.map VStr)
                              "stderr", VSeq(err |> Seq.map VStr) ]
                    )
                | _ -> unreachable "the checker rejects 'completedIn' on these arguments")))

let private succeededWithIn (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            VBuiltin(fun stdinV ->
                match progV, argsV, stdinV with
                | VStr prog, VSeq args, VSeq stdin ->
                    let argv = argStrings args
                    let input = stdin |> Seq.map asString
                    let code, _, _ = Proc.completeWith overlay (Proc.resolveProg prog) argv (Some input)
                    VBool(code = 0)
                | _ -> unreachable "the checker rejects 'succeededIn' on these arguments")))

let private exitCodedWithIn (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            VBuiltin(fun stdinV ->
                match progV, argsV, stdinV with
                | VStr prog, VSeq args, VSeq stdin ->
                    let argv = argStrings args
                    let input = stdin |> Seq.map asString

                    let code =
                        Proc.streamCodeOf
                            { Prog = Proc.resolveProg prog
                              Args = argv
                              Env = overlay
                              Input = Some input }

                    VInt(int64 code)
                | _ -> unreachable "the checker rejects 'exitCodedIn' on these arguments")))

let private orFailedWithIn (overlay: (string * string) list) : Value =
    VBuiltin(fun msgV ->
        VBuiltin(fun progV ->
            VBuiltin(fun argsV ->
                VBuiltin(fun stdinV ->
                    match msgV, progV, argsV, stdinV with
                    | VStr msg, VStr prog, VSeq args, VSeq stdin ->
                        let argv = argStrings args
                        let input = stdin |> Seq.map asString

                        let code =
                            Proc.streamCodeOf
                                { Prog = Proc.resolveProg prog
                                  Args = argv
                                  Env = overlay
                                  Input = Some input }

                        if code <> 0 then
                            failwith $"{msg} (exit {code})"

                        VUnit
                    | _ -> unreachable "the checker rejects 'orFailedIn' on these arguments"))))

// the expression-side regex family [D:regex-pattern] — computed
// patterns are fine here; an invalid runtime pattern joins the
// boundary-validation class (raises at the call)
let private compiledOrRaise (pat: string) =
    match Weir.Check.compileRegex pat with
    | Ok rx -> rx
    | Error msg -> failwith $"invalid regex: {msg}"

let private isMatchImpl: Value =
    VBuiltin(fun patV ->
        VBuiltin(fun subjectV ->
            match patV, subjectV with
            | VStr pat, VStr s -> VBool((compiledOrRaise pat).IsMatch s)
            | _ -> unreachable "the checker rejects 'isMatch' on these arguments"))

let private rmatchImpl: Value =
    VBuiltin(fun patV ->
        VBuiltin(fun subjectV ->
            match patV, subjectV with
            | VStr pat, VStr s ->
                let m = (compiledOrRaise pat).Match s

                if m.Success then
                    VUnion("Some", Some(VSeq [ for i in 1 .. m.Groups.Count - 1 -> VStr m.Groups[i].Value ]))
                else
                    VUnion("None", None)
            | _ -> unreachable "the checker rejects 'rmatch' on these arguments"))

// rmatchAll [D:rmatch-all]: every match's group seq, LAZILY — the
// plural of rmatch, no Option (absence IS the empty seq). Walks via
// Match/NextMatch so a match is computed only when the consumer pulls
// it (the pull-count guarantee); the inner group seq is finite per
// match. `(?s)`/`(?m)` inline flags cover DOTALL/MULTILINE, so no
// options API grows.
let private rmatchAllImpl: Value =
    VBuiltin(fun patV ->
        VBuiltin(fun subjectV ->
            match patV, subjectV with
            | VStr pat, VStr s ->
                let re = compiledOrRaise pat

                VSeq(
                    seq {
                        let mutable m = re.Match s

                        while m.Success do
                            yield VSeq [ for i in 1 .. m.Groups.Count - 1 -> VStr m.Groups[i].Value ]
                            m <- m.NextMatch()
                    }
                )
            | _ -> unreachable "the checker rejects 'rmatchAll' on these arguments"))

// Path.glob [D:path-glob] — the standard subset (`*` within-segment,
// `**` cross-segment, `?`, `[abc]`/`[!abc]`), bash's laws: `*` never
// matches dotfiles (a `.`-leading segment does); sorted per level
// (deterministic output); LAZY against the cwd at ENUMERATION (the
// cd seam — `|> Seq.force` pins the answer now); symlinked dirs
// NOT traversed by `**` (bash ≥4.3 globstar parity — loop-immune by
// law; explicit segments still follow links); unreadable dirs
// skipped (a pattern is discovery, not assertion); no matches = the
// empty seq. Hand-rolled: the FileSystemGlobbing probe found the
// library unable to express the dotfile law (and unrestorable
// offline) — the plan's fallback clause, taken and reported.
let private globSegRegex (seg: string) : System.Text.RegularExpressions.Regex =
    let sb = System.Text.StringBuilder("^")
    let mutable i = 0

    while i < seg.Length do
        (match seg[i] with
         | '*' -> sb.Append "[^/]*" |> ignore
         | '?' -> sb.Append "[^/]" |> ignore
         | '[' ->
             let close = seg.IndexOf(']', i + 1)

             if close < 0 then
                 sb.Append(System.Text.RegularExpressions.Regex.Escape "[") |> ignore
             else
                 let body = seg.Substring(i + 1, close - i - 1)
                 let body = if body.StartsWith "!" then "^" + body.Substring 1 else body
                 sb.Append('[').Append(body).Append(']') |> ignore
                 i <- close
         | c -> sb.Append(System.Text.RegularExpressions.Regex.Escape(string c)) |> ignore)

        i <- i + 1

    sb.Append "$" |> ignore
    System.Text.RegularExpressions.Regex(sb.ToString())

let private globWalk (pattern: string) : seq<string> =
    seq {
        let isAbs = pattern.StartsWith "/"

        let segs = pattern.Split('/') |> Array.filter (fun s -> s <> "") |> Array.toList

        let rootFs = if isAbs then "/" else Session.Cwd()
        let rootDisplay = if isAbs then "/" else ""

        let entriesOf (dirFs: string) =
            try
                System.IO.Directory.EnumerateFileSystemEntries dirFs
                |> Seq.map System.IO.Path.GetFileName
                |> Seq.sort
                |> List.ofSeq
            with _ ->
                []

        let hasWild (seg: string) =
            seg |> Seq.exists (fun c -> c = '*' || c = '?' || c = '[')

        // `**` never descends a symlinked dir (bash globstar's law)
        let realDir (dirFs: string) =
            try
                System.IO.Directory.Exists dirFs
                && not (System.IO.File.GetAttributes(dirFs).HasFlag System.IO.FileAttributes.ReparsePoint)
            with _ ->
                false

        let rec walk (dirFs: string) (prefix: string) (segs: string list) : seq<string> =
            seq {
                match segs with
                | [] -> ()
                | [ "**" ] ->
                    // trailing globstar: everything below, recursively
                    for name in entriesOf dirFs do
                        if not (name.StartsWith ".") then
                            let sub = System.IO.Path.Combine(dirFs, name)
                            yield prefix + name

                            if realDir sub then
                                yield! walk sub (prefix + name + "/") [ "**" ]
                | "**" :: rest ->
                    // zero directories...
                    yield! walk dirFs prefix rest

                    // ...or one-plus (dot-dirs stay unentered, bash's law)
                    for name in entriesOf dirFs do
                        if not (name.StartsWith ".") then
                            let sub = System.IO.Path.Combine(dirFs, name)

                            if realDir sub then
                                yield! walk sub (prefix + name + "/") ("**" :: rest)
                | seg :: rest when not (hasWild seg) ->
                    // literal segments echo without enumeration
                    let sub = System.IO.Path.Combine(dirFs, seg)

                    match rest with
                    | [] ->
                        if System.IO.File.Exists sub || System.IO.Directory.Exists sub then
                            yield prefix + seg
                    | _ ->
                        if System.IO.Directory.Exists sub then
                            yield! walk sub (prefix + seg + "/") rest
                | seg :: rest ->
                    let rx = globSegRegex seg
                    let dotOk = seg.StartsWith "."

                    for name in entriesOf dirFs do
                        if (dotOk || not (name.StartsWith ".")) && rx.IsMatch name then
                            let sub = System.IO.Path.Combine(dirFs, name)

                            match rest with
                            | [] -> yield prefix + name
                            | _ ->
                                if System.IO.Directory.Exists sub then
                                    yield! walk sub (prefix + name + "/") rest
            }

        yield! walk rootFs rootDisplay segs
    }

let private globImpl: Value =
    VBuiltin(fun patV ->
        match patV with
        | VStr pat -> VSeq(Seq.delay (fun () -> globWalk pat |> Seq.map VStr))
        | _ -> unreachable "the checker rejects 'glob' on this argument")

let private str1 (name: string) (f: string -> string) : Value =
    VBuiltin(fun v ->
        match v with
        | VStr s -> VStr(f s)
        | v -> unreachable $"the checker rejects '{name}' on {formatValue v}")

let private str2Bool (name: string) (f: string -> string -> bool) : Value =
    VBuiltin(fun a ->
        VBuiltin(fun b ->
            match a, b with
            | VStr x, VStr y -> VBool(f x y)
            | _ -> unreachable $"the checker rejects '{name}' on these arguments"))

let private vSome (v: Value) : Value = VUnion("Some", Some v)
let private vNone: Value = VUnion("None", None)

let private tryHeadImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            match Seq.tryHead items with
            | Some x -> vSome x
            | None -> vNone
        | v -> unreachable $"the checker rejects 'tryHead' on {formatValue v}")

let private seqLengthImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items -> VInt(int64 (Seq.length items))
        | v -> unreachable $"the checker rejects 'Seq.length' on {formatValue v}")

let private isEmptyImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items -> VBool(Seq.isEmpty items)
        | v -> unreachable $"the checker rejects 'isEmpty' on {formatValue v}")

// sortBy's key is statically Ord-constrained
// [D:inferred-type-classes], so this comparison is total over what
// remains.
let private scalarCompare (name: string) (a: Value) (b: Value) : int =
    match a, b with
    | VInt x, VInt y -> compare x y
    | VStr x, VStr y -> compare x y
    | VBool x, VBool y -> compare x y
    | VFloat x, VFloat y -> compare x y
    | VDur x, VDur y -> compare x y
    | VSize x, VSize y -> compare x y
    | v, _ -> unreachable $"the checker rejects '{name}' keys of {formatValue v}"

let private foldImpl: Value =
    VBuiltin(fun folder ->
        VBuiltin(fun init ->
            VBuiltin(fun sv ->
                match sv with
                | VSeq items -> items |> Seq.fold (fun acc v -> apply (apply folder acc) v) init
                | v -> unreachable $"the checker rejects 'fold' on {formatValue v}")))

let private sortByImpl: Value =
    VBuiltin(fun keyf ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    Seq.delay (fun () ->
                        items
                        |> Seq.map (fun item -> apply keyf item, item)
                        |> Seq.sortWith (fun (k1, _) (k2, _) -> scalarCompare "sortBy" k1 k2)
                        |> Seq.map snd)
                )
            | v -> unreachable $"the checker rejects 'sortBy' on {formatValue v}"))

// reversed comparison args keep Seq.sortWith's stability, matching
// F#'s sortByDescending (equal keys stay in input order)
let private sortByDescImpl: Value =
    VBuiltin(fun keyf ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    Seq.delay (fun () ->
                        items
                        |> Seq.map (fun item -> apply keyf item, item)
                        |> Seq.sortWith (fun (k1, _) (k2, _) -> scalarCompare "sortByDescending" k2 k1)
                        |> Seq.map snd)
                )
            | v -> unreachable $"the checker rejects 'sortByDescending' on {formatValue v}"))

let private splitImpl: Value =
    VBuiltin(fun sep ->
        VBuiltin(fun subject ->
            match sep, subject with
            | VStr sep, VStr s -> VSeq(s.Split sep |> Seq.map VStr)
            | _ -> unreachable "the checker rejects 'split' on these arguments"))

let private joinImpl: Value =
    VBuiltin(fun sep ->
        VBuiltin(fun s ->
            match sep, s with
            | VStr sep, VSeq items -> VStr(items |> Seq.map asString |> String.concat sep)
            | _ -> unreachable "the checker rejects 'join' on these arguments"))

let private replaceImpl: Value =
    VBuiltin(fun pat ->
        VBuiltin(fun rep ->
            VBuiltin(fun subject ->
                match pat, rep, subject with
                | VStr p, VStr r, VStr s -> VStr(s.Replace(p, r))
                | _ -> unreachable "the checker rejects 'replace' on these arguments")))

let private strLenImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr s -> VInt(int64 s.Length)
        | v -> unreachable $"the checker rejects 'strLen' on {formatValue v}")

let private toIntImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr s ->
            match System.Int64.TryParse s with
            | true, n -> VInt n
            | _ -> failwith $"toInt: not an integer: \"{s}\""
        | v -> unreachable $"the checker rejects 'toInt' on {formatValue v}")

let private tryToIntImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr s ->
            match System.Int64.TryParse s with
            | true, n -> vSome (VInt n)
            | _ -> vNone
        | v -> unreachable $"the checker rejects 'tryToInt' on {formatValue v}")

let private tryFindImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                let found =
                    items
                    |> Seq.tryFind (fun item ->
                        match apply pred item with
                        | VBool b -> b
                        | v -> unreachable $"the checker rejects a non-bool predicate result: {formatValue v}")

                match found with
                | Some x -> vSome x
                | None -> vNone
            | v -> unreachable $"the checker rejects 'tryFind' on {formatValue v}"))

let private tryIndexOfImpl: Value =
    VBuiltin(fun needle ->
        VBuiltin(fun subject ->
            match needle, subject with
            | VStr n, VStr s ->
                match s.IndexOf n with
                | -1 -> vNone
                | i -> vSome (VInt(int64 i))
            | _ -> unreachable "the checker rejects 'tryIndexOf' on these arguments"))

let private substringImpl: Value =
    VBuiltin(fun start ->
        VBuiltin(fun len ->
            VBuiltin(fun subject ->
                match start, len, subject with
                | VInt st64, VInt ln64, VStr s ->
                    let st, ln = int st64, int ln64

                    if st < 0 || ln < 0 || st + ln > s.Length then
                        failwith $"substring: out of bounds (start {st}, length {ln}, string length {s.Length})"
                    else
                        VStr(s.Substring(st, ln))
                | _ -> unreachable "the checker rejects 'substring' on these arguments")))

let private defaultToImpl: Value =
    VBuiltin(fun fallback ->
        VBuiltin(fun opt ->
            match opt with
            | VUnion("Some", Some v) -> v
            | VUnion("None", None) -> fallback
            | v -> unreachable $"the checker rejects 'defaultTo' on {formatValue v}"))

let private defaultWithImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun opt ->
            match opt with
            | VUnion("Some", Some v) -> v
            | VUnion("None", None) -> apply f VUnit
            | v -> unreachable $"the checker rejects 'defaultWith' on {formatValue v}"))

let private mapOptionImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun opt ->
            match opt with
            | VUnion("Some", Some v) -> vSome (apply f v)
            | VUnion("None", None) -> vNone
            | v -> unreachable $"the checker rejects 'mapOption' on {formatValue v}"))

let private seqInt = TSeq(TInt)
let private seqStr = TSeq TStr
let private tA = TVar "a"
let private tB = TVar "b"

let groupDef: RecordDef =
    { Name = "Group"
      Params = [ "k"; "v" ]
      Fields = [ "key", TVar "k"; "items", TSeq(TVar "v") ]
      Attrs = Map.empty
      Docs = Map.empty }

// pairwise/zip produce tuples [D:tuples-reversal]
let private pairwiseImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items -> VSeq(items |> Seq.pairwise |> Seq.map (fun (a, b) -> VTuple [ a; b ]))
        | v -> unreachable $"the checker rejects 'pairwise' on {formatValue v}")

let private zipImpl: Value =
    VBuiltin(fun s1 ->
        VBuiltin(fun s2 ->
            match s1, s2 with
            | VSeq a, VSeq b -> VSeq(Seq.zip a b |> Seq.map (fun (x, y) -> VTuple [ x; y ]))
            | _ -> unreachable "the checker rejects 'zip' on these arguments"))

let private groupByImpl: Value =
    VBuiltin(fun keyf ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    Seq.delay (fun () ->
                        items
                        |> Seq.groupBy (fun item ->
                            match apply keyf item with
                            | VInt n -> box n
                            | VStr str -> box str
                            | VBool b -> box b
                            | v -> failwith $"groupBy: keys must be ints, strings or bools, got {formatValue v}")
                        |> Seq.map (fun (key, group) ->
                            let keyValue =
                                match key with
                                | :? int64 as n -> VInt n
                                | :? string as str -> VStr str
                                | :? bool as b -> VBool b
                                | _ -> unreachable "groupBy key box"

                            VRecord(
                                groupDef.Name,
                                Map [ "key", keyValue; "items", VSeq(List.ofSeq group :> seq<Value>) ]
                            )))
                )
            | v -> unreachable $"the checker rejects 'groupBy' on {formatValue v}"))

let private iterImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                for item in items do
                    apply f item |> ignore

                VUnit
            | v -> unreachable $"the checker rejects 'iter' on {formatValue v}"))

let private rangeImpl: Value =
    VBuiltin(fun a ->
        VBuiltin(fun s ->
            VBuiltin(fun b ->
                match a, s, b with
                | VInt start, VInt step, VInt stop ->
                    if step = 0L then
                        failwith "range step is zero"

                    VSeq(
                        seq {
                            let mutable i = start
                            let mutable go = if step > 0L then i <= stop else i >= stop

                            while go do
                                yield VInt i
                                let next = i + step
                                // wrap past Int64 boundary = the range is done
                                let wrapped = (step > 0L && next < i) || (step < 0L && next > i)
                                i <- next
                                go <- not wrapped && (if step > 0L then i <= stop else i >= stop)
                        }
                    )
                | _ -> unreachable "the checker rejects non-int range bounds")))

// Data parallelism, NOT concurrency machinery (see the async rejection):
// eager, input-order results, ceiling-64 degree, first worker error
// rethrown. Output interleaving from piter workers is line-atomic and
// owned by the user, as with any parallel tool.
// the fan-out ceiling [D:tasks-underneath]: 64, stated — well above any
// core count because arms are I/O-bound by domain; the cap exists so an
// unbounded fan-out over 10k items is not a well-mannered fork bomb
let private parallelCeiling = 64

let private runParallelWith (degree: int) (f: Value) (items: seq<Value>) : Value array =
    if degree < 1 then
        failwith $"parallel degree must be at least 1, got {degree}"

    let arr = Seq.toArray items
    let out = Array.zeroCreate arr.Length
    // fork the ambient session: workers inherit the parent cwd; cd inside
    // a worker is worker-local and dies at the join
    let parentCwd = Session.Cwd()
    // arms BLOCK (child waits, sleeps, network) — LongRunning gives each
    // active worker a dedicated thread, sidestepping the pool's slow
    // injection heuristic; the ceiling is RESOURCE protection, not CPU
    // sizing [D:tasks-underneath]
    let workers = min degree (max 1 arr.Length)
    let mutable next = -1
    let errors = System.Collections.Concurrent.ConcurrentDictionary<int, exn>()

    let worker () =
        let mutable i = System.Threading.Interlocked.Increment &next

        while i < arr.Length do
            Session.enterWorker parentCwd

            try
                try
                    out[i] <- apply f arr[i]
                with e ->
                    // every arm still RUNS (data parallelism does not
                    // half-finish); the FIRST error by INPUT ORDER
                    // rethrows after the join
                    errors[i] <- e
            finally
                Session.exitWorker ()

            i <- System.Threading.Interlocked.Increment &next

    let tasks =
        Array.init workers (fun _ -> Task.Factory.StartNew(worker, TaskCreationOptions.LongRunning))

    Task.WaitAll(tasks: Task[])

    if not errors.IsEmpty then
        raise errors[Seq.min errors.Keys]

    out

// the race [D:seq-pfirst]: the FIRST SUCCESS wins; losers' spawned
// process trees are killed via their RaceGroup, so their failures are
// swallowed BY CONSTRUCTION. Loser arm THREADS are cooperative: the
// kill reaches processes (what arms actually wait on); a pure-compute
// loser finishes in the background and is discarded. All-failed
// rethrows the first error by INPUT ORDER; empty input raises.
let private runRaceWith (degree: int) (f: Value) (items: seq<Value>) : Value =
    if degree < 1 then
        failwith $"parallel degree must be at least 1, got {degree}"

    let arr = Seq.toArray items

    if arr.Length = 0 then
        failwith "pfirst: empty sequence"

    let parentCwd = Session.Cwd()
    let groups = Array.init arr.Length (fun _ -> Session.RaceGroup())
    let mutable won = 0
    let mutable failedCount = 0
    let errors = System.Collections.Concurrent.ConcurrentDictionary<int, exn>()

    let outcome =
        TaskCompletionSource<Value>(TaskCreationOptions.RunContinuationsAsynchronously)

    let workers = min degree arr.Length
    let mutable next = -1

    let worker () =
        let mutable i = System.Threading.Interlocked.Increment &next

        while i < arr.Length && System.Threading.Volatile.Read &won = 0 do
            Session.enterWorker parentCwd
            Session.enterRace groups[i]

            try
                try
                    let v = apply f arr[i]

                    if System.Threading.Interlocked.CompareExchange(&won, 1, 0) = 0 then
                        for j in 0 .. arr.Length - 1 do
                            if j <> i then
                                groups[j].Condemn()

                        outcome.TrySetResult v |> ignore
                with e ->
                    errors[i] <- e

                    if System.Threading.Interlocked.Increment &failedCount = arr.Length then
                        outcome.TrySetException errors[Seq.min errors.Keys] |> ignore
            finally
                Session.exitRace ()
                Session.exitWorker ()

            i <- System.Threading.Interlocked.Increment &next

    for _ in 1..workers do
        Task.Factory.StartNew(worker, TaskCreationOptions.LongRunning) |> ignore

    try
        outcome.Task.Result
    with :? System.AggregateException as ae when ae.InnerExceptions.Count = 1 ->
        raise ae.InnerExceptions[0]

let private pfirstImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> runRaceWith parallelCeiling f items
            | v -> unreachable $"the checker rejects 'pfirst' on {formatValue v}"))

let private pfirstWithImpl: Value =
    VBuiltin(fun nv ->
        VBuiltin(fun f ->
            VBuiltin(fun s ->
                match nv, s with
                | VInt n, VSeq items -> runRaceWith (int n) f items
                | v, _ -> unreachable $"the checker rejects 'pfirstWith' on {formatValue v}")))

let private pmapImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(runParallelWith parallelCeiling f items :> seq<Value>)
            | v -> unreachable $"the checker rejects 'pmap' on {formatValue v}"))

let private pmapWithImpl: Value =
    VBuiltin(fun nv ->
        VBuiltin(fun f ->
            VBuiltin(fun s ->
                match nv, s with
                | VInt n, VSeq items -> VSeq(runParallelWith (int n) f items :> seq<Value>)
                | v, _ -> unreachable $"the checker rejects 'pmapWith' on {formatValue v}")))

let private piterWithImpl: Value =
    VBuiltin(fun nv ->
        VBuiltin(fun f ->
            VBuiltin(fun s ->
                match nv, s with
                | VInt n, VSeq items ->
                    runParallelWith (int n) f items |> ignore
                    VUnit
                | v, _ -> unreachable $"the checker rejects 'piterWith' on {formatValue v}")))

let private piterImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                runParallelWith parallelCeiling f items |> ignore
                VUnit
            | v -> unreachable $"the checker rejects 'piter' on {formatValue v}"))

let private existsImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VBool(
                    items
                    |> Seq.exists (fun v ->
                        match apply pred v with
                        | VBool b -> b
                        | r -> unreachable $"the checker rejects a non-bool predicate: {formatValue r}")
                )
            | v -> unreachable $"the checker rejects 'exists' on {formatValue v}"))

let private forallImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VBool(
                    items
                    |> Seq.forall (fun v ->
                        match apply pred v with
                        | VBool b -> b
                        | r -> unreachable $"the checker rejects a non-bool predicate: {formatValue r}")
                )
            | v -> unreachable $"the checker rejects 'forall' on {formatValue v}"))

let private containsImpl: Value =
    VBuiltin(fun needle ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VBool(items |> Seq.exists (fun v -> v = needle))
            | v -> unreachable $"the checker rejects 'contains' on {formatValue v}"))

// Seq.distinct [D:seq-distinct]: lazy, first-occurrence-wins,
// remembers only what it has yielded; equality is the checker-vetted
// structural `=` (Eq-constrained — functions/seqs rejected at use)
let private distinctImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            VSeq(
                Seq.delay (fun () ->
                    seq {
                        let seen = ResizeArray<Value>()

                        for x in items do
                            if not (seen.Contains x) then
                                seen.Add x
                                yield x
                    })
            )
        | v -> unreachable $"the checker rejects 'distinct' on {formatValue v}")

let private itemImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun s ->
            match n, s with
            | VInt i, VSeq items -> items |> Seq.item (int i)
            | _ -> unreachable "the checker rejects 'item' on these arguments"))

let private tryItemImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun s ->
            match n, s with
            | VInt i, VSeq items ->
                match items |> Seq.indexed |> Seq.tryFind (fun (j, _) -> int64 j = i) with
                | Some(_, v) -> VUnion("Some", Some v)
                | None -> VUnion("None", None)
            | _ -> unreachable "the checker rejects 'tryItem' on these arguments"))

let private skipImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun s ->
            match n, s with
            | VInt i, VSeq items -> VSeq(items |> Seq.skip (int i))
            | _ -> unreachable "the checker rejects 'skip' on these arguments"))

let private seqMembers: (string * Ty * Value) list =
    [ "map", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tB)), mapImpl
      "where", TFun(TFun(tA, TBool), TFun(TSeq tA, TSeq tA)), whereImpl
      "first", TFun(TInt, TFun(TSeq tA, TSeq tA)), truncateImpl
      "take", TFun(TInt, TFun(TSeq tA, TSeq tA)), truncateImpl
      "head", TFun(TSeq tA, tA), headImpl
      "sum", TFun(seqInt, TInt), sumImpl
      "force", TFun(TSeq tA, TSeq tA), toListImpl
      "tryHead", TFun(TSeq tA, TNamed("Option", [ tA ])), tryHeadImpl
      "tryFind", TFun(TFun(tA, TBool), TFun(TSeq tA, TNamed("Option", [ tA ]))), tryFindImpl
      "isEmpty", TFun(TSeq tA, TBool), isEmptyImpl
      "length", TFun(TSeq tA, TInt), seqLengthImpl
      // fold [D:seq-fold]: STRICT (an infinite source does not
      // return); state-first folder; constraint-free by construction
      "fold", TFun(TFun(tA, TFun(tB, tA)), TFun(tA, TFun(TSeq tB, tA))), foldImpl
      "choose", TFun(TFun(tA, TNamed("Option", [ tB ])), TFun(TSeq tA, TSeq tB)), chooseImpl
      "append", TFun(TSeq tA, TFun(TSeq tA, TSeq tA)), appendImpl
      "sortBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tA)), sortByImpl
      "sortByDescending", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tA)), sortByDescImpl
      "iter", TFun(TFun(tA, TUnit), TFun(TSeq tA, TUnit)), iterImpl
      "pmap", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tB)), pmapImpl
      "piter", TFun(TFun(tA, TUnit), TFun(TSeq tA, TUnit)), piterImpl
      "pmapWith", TFun(TInt, TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tB))), pmapWithImpl
      "pfirst", TFun(TFun(tA, tB), TFun(TSeq tA, tB)), pfirstImpl
      "pfirstWith", TFun(TInt, TFun(TFun(tA, tB), TFun(TSeq tA, tB))), pfirstWithImpl
      "piterWith", TFun(TInt, TFun(TFun(tA, TUnit), TFun(TSeq tA, TUnit))), piterWithImpl
      "range", TFun(TInt, TFun(TInt, TFun(TInt, seqInt))), rangeImpl
      "windowed", TFun(TInt, TFun(TSeq tA, TSeq(TSeq tA))), windowedImpl
      "last", TFun(TSeq tA, tA), lastImpl
      "tryLast", TFun(TSeq tA, TNamed("Option", [ tA ])), tryLastImpl
      "pairwise", TFun(TSeq tA, TSeq(TTuple [ tA; tA ])), pairwiseImpl
      "zip", TFun(TSeq tA, TFun(TSeq tB, TSeq(TTuple [ tA; tB ]))), zipImpl
      "exists", TFun(TFun(tA, TBool), TFun(TSeq tA, TBool)), existsImpl
      "forall", TFun(TFun(tA, TBool), TFun(TSeq tA, TBool)), forallImpl
      "item", TFun(TInt, TFun(TSeq tA, tA)), itemImpl
      "tryItem", TFun(TInt, TFun(TSeq tA, TNamed("Option", [ tA ]))), tryItemImpl
      "skip", TFun(TInt, TFun(TSeq tA, TSeq tA)), skipImpl
      "contains", TFun(tA, TFun(TSeq tA, TBool)), containsImpl
      "distinct", TFun(TSeq tA, TSeq tA), distinctImpl
      "groupBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq(TNamed("Group", [ tB; tA ])))), groupByImpl ]

// the encoding law [D:encoding-law]: weir encodes and decodes UTF-8 at
// every boundary — what gets read, written, hashed, and base64'd is
// UTF-8 bytes (in-memory representation is not the law's business).
// Strict decode: invalid bytes are an ERROR, never U+FFFD corruption
// wearing a success.
let private utf8Strict = System.Text.UTF8Encoding(false, true)

// liberal-in: unpadded standard-alphabet base64 pads before decoding;
// encoding emits padded (the one stated default). URL-safe (-_) is
// PARKED with the JWT trigger [D:encoding-law].
let private base64Bytes (s: string) : byte[] =
    let t = s.Trim()
    System.Convert.FromBase64String(t + System.String('=', (4 - t.Length % 4) % 4))

let private fromBase64Text (name: string) (s: string) : Result<string, string> =
    let bytes =
        try
            Ok(base64Bytes s)
        with _ ->
            Error $"{name}: invalid base64: \"{s}\""

    match bytes with
    | Error e -> Error e
    | Ok(b: byte[]) ->
        try
            Ok(utf8Strict.GetString b)
        with _ ->
            Error $"{name}: the decoded content is not text (not valid UTF-8)"

let private strMembers: (string * Ty * Value) list =
    [ "contains", TFun(TStr, TFun(TStr, TBool)), str2Bool "contains" (fun needle s -> s.Contains needle)
      "startsWith", TFun(TStr, TFun(TStr, TBool)), str2Bool "startsWith" (fun p s -> s.StartsWith p)
      "endsWith", TFun(TStr, TFun(TStr, TBool)), str2Bool "endsWith" (fun p s -> s.EndsWith p)
      "trim", TFun(TStr, TStr), str1 "trim" (fun s -> s.Trim())
      "trimStart", TFun(TStr, TStr), str1 "trimStart" (fun s -> s.TrimStart())
      "trimEnd", TFun(TStr, TStr), str1 "trimEnd" (fun s -> s.TrimEnd())
      "toLower", TFun(TStr, TStr), str1 "toLower" (fun s -> s.ToLowerInvariant())
      "toUpper", TFun(TStr, TStr), str1 "toUpper" (fun s -> s.ToUpperInvariant())
      "split", TFun(TStr, TFun(TStr, TSeq TStr)), splitImpl
      "join", TFun(TStr, TFun(TSeq TStr, TStr)), joinImpl
      "replace", TFun(TStr, TFun(TStr, TFun(TStr, TStr))), replaceImpl
      "length", TFun(TStr, TInt), strLenImpl
      "sub", TFun(TInt, TFun(TInt, TFun(TStr, TStr))), substringImpl
      "toInt", TFun(TStr, TInt), toIntImpl
      "tryToInt", TFun(TStr, TNamed("Option", [ TInt ])), tryToIntImpl
      // sha256 ONLY [D:encoding-law]: md5 is broken (offering it invites
      // its use), sha1 deprecated, sha512 has no receipt — one member,
      // one algorithm, more on receipt. Lowercase hex = sha256sum parity
      // (the tool it replaces).
      "sha256",
      TFun(TStr, TStr),
      str1 "sha256" (fun s ->
          System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes s)
          |> Array.map (fun b -> b.ToString "x2")
          |> String.concat "")
      // unwrapped by construction — GNU base64 wraps at 76 cols (the -w0
      // tax); Convert.ToBase64String simply does not have the problem
      "toBase64",
      TFun(TStr, TStr),
      str1 "toBase64" (fun s -> System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes s))
      "fromBase64",
      TFun(TStr, TStr),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              match fromBase64Text "fromBase64" s with
              | Ok t -> VStr t
              | Error e -> failwith e
          | v -> unreachable $"the checker rejects 'fromBase64' on {formatValue v}")
      "tryFromBase64",
      TFun(TStr, TNamed("Option", [ TStr ])),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              match fromBase64Text "tryFromBase64" s with
              | Ok t -> VUnion("Some", Some(VStr t))
              | Error _ -> VUnion("None", None)
          | v -> unreachable $"the checker rejects 'tryFromBase64' on {formatValue v}")
      "tryIndexOf", TFun(TStr, TFun(TStr, TNamed("Option", [ TInt ]))), tryIndexOfImpl
      "isMatch", TFun(TStr, TFun(TStr, TBool)), isMatchImpl
      "rmatch", TFun(TStr, TFun(TStr, TNamed("Option", [ TSeq TStr ]))), rmatchImpl
      "rmatchAll", TFun(TStr, TFun(TStr, TSeq(TSeq TStr))), rmatchAllImpl ]

// Path — string surgery over paths, System.IO.Path underneath.
// extension keeps the dot and is "" when there is none; dir is "" at
// the top (GetDirectoryName's null coerced); combine is prefix-first
// (Path.combine dir name), matching Path.Combine — named combine, not
// join, because bare `join` is Str.join's alias (last-wins map).
let private pathCombineImpl: Value =
    VBuiltin(fun a ->
        VBuiltin(fun b ->
            match a, b with
            | VStr x, VStr y -> VStr(Path.Combine(x, y))
            | _ -> unreachable "the checker rejects 'Path.combine' on these arguments"))

let private pathMembers: (string * Ty * Value) list =
    [ "extension", TFun(TStr, TStr), str1 "extension" Path.GetExtension
      "fileName", TFun(TStr, TStr), str1 "fileName" Path.GetFileName
      "stem", TFun(TStr, TStr), str1 "stem" Path.GetFileNameWithoutExtension
      "dir",
      TFun(TStr, TStr),
      str1 "dir" (fun s ->
          match Path.GetDirectoryName s with
          | null -> ""
          | d -> d)
      "combine", TFun(TStr, TFun(TStr, TStr)), pathCombineImpl
      "glob", TFun(TStr, TSeq TStr), globImpl
      // the QUERY (pure): the system temp root, no trailing separator
      "tempRoot",
      TFun(TUnit, TStr),
      VBuiltin(fun _ ->
          VStr(
              System.IO.Path
                  .GetTempPath()
                  .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
          ))
      // the CREATOR (side effect visible in the name): a fresh unique
      // dir, `within tmp`'s spelling exactly (weir-tmp- prefix, guid);
      // cleanup is the CALLER's or the OS's — `within tmp` is the
      // scoped-cleanup spelling
      "newTempDir",
      TFun(TUnit, TStr),
      VBuiltin(fun _ ->
          let dir =
              System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-tmp-{System.Guid.NewGuid():N}")

          System.IO.Directory.CreateDirectory dir |> ignore
          VStr dir) ]

let private optionIterImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun opt ->
            match opt with
            | VUnion("Some", Some v) ->
                apply f v |> ignore
                VUnit
            | VUnion("None", None) -> VUnit
            | v -> unreachable $"the checker rejects 'Option.iter' on {formatValue v}"))

// fallback FIRST so the pipe reads data-last (F#'s order):
// `opt |> Option.orElse fallback`. Stays in Option, where
// defaultValue unwraps. The fallback is an ordinary (eager) argument;
// an orElseWith twin is PARKED on the defaultWith precedent.
let private optionOrElseImpl: Value =
    VBuiltin(fun fallback ->
        VBuiltin(fun opt ->
            match opt with
            | VUnion("Some", _) -> opt
            | VUnion("None", None) -> fallback
            | v -> unreachable $"the checker rejects 'Option.orElse' on {formatValue v}"))

let private optionMembers: (string * Ty * Value) list =
    [ "iter", TFun(TFun(tA, TUnit), TFun(TNamed("Option", [ tA ]), TUnit)), optionIterImpl
      "orElse",
      TFun(TNamed("Option", [ tA ]), TFun(TNamed("Option", [ tA ]), TNamed("Option", [ tA ]))),
      optionOrElseImpl
      "map", TFun(TFun(tA, tB), TFun(TNamed("Option", [ tA ]), TNamed("Option", [ tB ]))), mapOptionImpl
      "defaultValue", TFun(tA, TFun(TNamed("Option", [ tA ]), tA)), defaultToImpl
      "defaultWith", TFun(TFun(TUnit, tA), TFun(TNamed("Option", [ tA ]), tA)), defaultWithImpl ]

// Args — script-only scanners over the invocation argv (Session.ScriptArgs;
// empty in the REPL by design). Long-only flags: empty short form.
let private argsFlagImpl: Value =
    VBuiltin(fun l ->
        VBuiltin(fun sh ->
            match l, sh with
            | VStr long, VStr short ->
                VBool(
                    Session.ScriptArgs |> List.contains long
                    || (short <> "" && Session.ScriptArgs |> List.contains short)
                )
            | _ -> unreachable "the checker rejects 'Args.flag' on these arguments"))

let private argsValueImpl: Value =
    VBuiltin(fun l ->
        match l with
        | VStr flag ->
            let rec find =
                function
                | f :: v :: _ when f = flag -> VUnion("Some", Some(VStr v))
                | _ :: rest -> find rest
                | [] -> VUnion("None", None)

            find Session.ScriptArgs
        | v -> unreachable $"the checker rejects 'Args.value' on {formatValue v}")

let private argsMembers: (string * Ty * Value) list =
    [ "flag", TFun(TStr, TFun(TStr, TBool)), argsFlagImpl
      "value", TFun(TStr, TNamed("Option", [ TStr ])), argsValueImpl ]

let envVarDef: RecordDef =
    { Name = "EnvVar"
      Params = []
      Fields = [ "name", TStr; "value", TStr ]
      Attrs = Map.empty
      Docs = Map.empty }

// Env.fromFile parses the DOTENV SUBSET only: KEY=VALUE, optional
// single/double quotes around VALUE, # full-line and trailing
// comments, blank lines. No export keyword, no $VAR references, no
// command substitution — sourcing is shell EVALUATION; this is a
// parser, and anything needing evaluation is a per-line boundary
// error naming the sh escape. (The formalization scanner is NOT
// reused here: it speaks weir-string quote rules and lives in a later
// compile unit; dotenv's quoting is its own three-case grammar.)
let private dotenvEscape =
    "this .env line needs shell semantics; use sh -c \"set -a; . file; ...\""

let private parseDotenvLine (path: string) (lineNo: int) (raw: string) : (string * string) option =
    let bad (why: string) =
        failwith $"{path}:{lineNo}: {why} — {dotenvEscape}"

    let line = raw.Trim()

    if line = "" || line.StartsWith "#" then
        None
    elif line.StartsWith "export " then
        bad "the export keyword is shell syntax"
    else
        match line.IndexOf '=' with
        | -1 -> bad "not a KEY=VALUE line"
        | eq ->
            let key = line.Substring(0, eq).TrimEnd()

            let keyOk =
                key.Length > 0
                && (System.Char.IsLetter key[0] || key[0] = '_')
                && key |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

            if not keyOk then
                bad $"invalid key '{key}'"

            let rest = line.Substring(eq + 1).TrimStart()

            let closedQuote (q: char) =
                let close = rest.IndexOf(q, 1)

                if close < 0 then
                    bad $"unterminated {q} quote"

                let value = rest.Substring(1, close - 1)
                let tail = rest.Substring(close + 1).TrimStart()

                if tail <> "" && not (tail.StartsWith "#") then
                    bad "text after the closing quote"

                value

            let value =
                if rest.StartsWith "\"" then
                    let v = closedQuote '"'

                    if v.Contains '$' || v.Contains '`' then
                        bad "shell expansion in a double-quoted value"

                    v
                elif rest.StartsWith "'" then
                    // single quotes are shell-literal: $ is just a character
                    closedQuote '\''
                else
                    let cut =
                        match rest.IndexOf '#' with
                        | i when i > 0 && System.Char.IsWhiteSpace rest[i - 1] -> rest.Substring(0, i)
                        | _ -> rest

                    let v = cut.TrimEnd()

                    if v.Contains '$' || v.Contains '`' then
                        bad "shell expansion in an unquoted value"

                    if v |> Seq.exists System.Char.IsWhiteSpace then
                        bad "unquoted value with spaces"

                    v

            Some(key, value)

let private envFromFileImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr path ->
            VSeq(
                Seq.delay (fun () ->
                    let resolved = Session.resolve path

                    File.ReadLines resolved
                    |> Seq.indexed
                    |> Seq.choose (fun (i, raw) -> parseDotenvLine path (i + 1) raw)
                    |> Seq.map (fun (k, value) ->
                        VRecord(envVarDef.Name, Map [ "name", VStr k; "value", VStr value ])))
            )
        | v -> unreachable $"the checker rejects 'Env.fromFile' on {formatValue v}")

// Env.pair / Env.ofPairs [D:seq-fold] — inline-env construction
// for a known nominal type (NOT an anonymous-records case).
let private envPairImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun v ->
            match n, v with
            | VStr n, VStr v -> VRecord("EnvVar", Map [ "name", VStr n; "value", VStr v ])
            | _ -> unreachable "the checker rejects 'Env.pair' on these arguments"))

let private envOfPairsImpl: Value =
    VBuiltin(fun sv ->
        match sv with
        | VSeq items ->
            VSeq(
                items
                |> Seq.map (fun p ->
                    match p with
                    | VTuple [ VStr n; VStr v ] -> VRecord("EnvVar", Map [ "name", VStr n; "value", VStr v ])
                    | v -> unreachable $"the checker rejects 'Env.ofPairs' elements: {formatValue v}")
            )
        | v -> unreachable $"the checker rejects 'Env.ofPairs' on {formatValue v}")

// Env — process environment [D:typed-env]
let private envMembers: (string * Ty * Value) list =
    [ "pair", TFun(TStr, TFun(TStr, TNamed("EnvVar", []))), envPairImpl
      "ofPairs", TFun(TSeq(TTuple [ TStr; TStr ]), TSeq(TNamed("EnvVar", []))), envOfPairsImpl
      "get",
      TFun(TStr, TNamed("Option", [ TStr ])),
      VBuiltin(fun v ->
          match v with
          | VStr name ->
              match System.Environment.GetEnvironmentVariable name with
              | null -> VUnion("None", None)
              | value -> VUnion("Some", Some(VStr value))
          | v -> unreachable $"the checker rejects 'Env.get' on {formatValue v}")
      "vars",
      TSeq(TNamed(envVarDef.Name, [])),
      VSeq(
          Seq.delay (fun () ->
              System.Environment.GetEnvironmentVariables()
              |> Seq.cast<System.Collections.DictionaryEntry>
              |> Seq.map (fun e ->
                  VRecord(envVarDef.Name, Map [ "name", VStr(string e.Key); "value", VStr(string e.Value) ])))
      )
      "fromFile", TFun(TStr, TSeq(TNamed(envVarDef.Name, []))), envFromFileImpl ]

let private fileMembers: (string * Ty * Value) list =
    [ "read",
      TFun(TStr, TSeq TStr),
      VBuiltin(fun v ->
          match v with
          | VStr path -> VSeq(File.ReadAllLines(Session.resolve path) |> Seq.map VStr)
          | v -> unreachable $"the checker rejects 'File.read' on {formatValue v}")
      "write",
      TFun(TStr, TFun(TSeq TStr, TUnit)),
      VBuiltin(fun pathV ->
          VBuiltin(fun linesV ->
              match pathV, linesV with
              | VStr path, VSeq lines ->
                  File.WriteAllLines(Session.resolve path, lines |> Seq.map asString)
                  VUnit
              | _ -> unreachable "the checker rejects 'File.write' on these arguments"))
      "append",
      TFun(TStr, TFun(TSeq TStr, TUnit)),
      VBuiltin(fun pathV ->
          VBuiltin(fun linesV ->
              match pathV, linesV with
              | VStr path, VSeq lines ->
                  File.AppendAllLines(Session.resolve path, lines |> Seq.map asString)
                  VUnit
              | _ -> unreachable "the checker rejects 'File.append' on these arguments"))
      "exists",
      TFun(TStr, TBool),
      VBuiltin(fun v ->
          match v with
          | VStr path -> VBool(File.Exists(Session.resolve path))
          | v -> unreachable $"the checker rejects 'File.exists' on {formatValue v}") ]

// fail keeps exit-1 (message-carrying); `exit n` is the propagation
// spelling [D:exit-rename] — unit-typed here (F#'s is 'a), no checker
// surface.
let private exitImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VInt n -> raise (ExitRequest(int n))
        | v -> unreachable $"the checker rejects 'exit' on {formatValue v}")

// ---- the Log module [D:log-module] -----------------------------------------
// Levelled diagnostics that respect the pipeline: EVERY member writes
// to STDERR, unconditionally — stdout is DATA (what pipes carry, what
// $() captures), and that is a law, not a default. Level control is
// WEIR_LOG (trace|debug|info|warn|off), read ONCE at startup; it
// changes what is PRINTED, never what the script computes. There is
// deliberately NO Log.error: an error silenced by WEIR_LOG=off is the
// one message a user needs — unconditional messages are `printerr`,
// stopping is `fail`; `warn` is the TOP of the filterable range.

let logLevelNames = [ "trace"; "debug"; "info"; "warn"; "off" ]

let parseLogLevel (s: string) : Result<int, string> =
    match logLevelNames |> List.tryFindIndex ((=) s) with
    | Some i -> Ok i
    | None -> Error $"WEIR_LOG={s}: unknown log level (one of trace|debug|info|warn|off)"

// default info (ruled): Log.info is useful without ceremony,
// debug/trace are opt-in, WEIR_LOG=off is genuine silence
let mutable private logThreshold = 2

/// read WEIR_LOG once; Program calls this before dispatch so an
/// invalid value is a loud startup error, never a silent fallback
let initLogLevel () : Result<unit, string> =
    match System.Environment.GetEnvironmentVariable "WEIR_LOG" with
    | null
    | "" -> Ok()
    | v -> parseLogLevel v |> Result.map (fun i -> logThreshold <- i)

let private logTint (code: string) (label: string) =
    if Types.Color.onStderr.Value then
        $"\x1b[{code}m{label}\x1b[0m"
    else
        label

let private logAt (level: int) (code: string) (label: string) (msg: string) =
    if level >= logThreshold then
        System.Console.Error.WriteLine(logTint code label + " " + msg)

let private logMember (level: int) (code: string) (label: string) : Value =
    VBuiltin(fun v ->
        match v with
        | VStr msg ->
            logAt level code label msg
            VUnit
        | v -> unreachable $"the checker rejects logging {formatValue v}")

// the With twins: the thunk runs ONLY when the level passes — the
// Option.defaultWith precedent for the expensive-argument case (weir
// has no lazy argument position, stated in the docs)
let private logWithMember (level: int) (code: string) (label: string) : Value =
    VBuiltin(fun f ->
        (if level >= logThreshold then
             match apply f VUnit with
             | VStr msg -> logAt level code label msg
             | v -> unreachable $"the checker rejects a non-string log thunk: {formatValue v}")

        VUnit)

let private logMembers: (string * Ty * Value) list =
    let thunk = TFun(TUnit, TStr)

    [ "trace", TFun(TStr, TUnit), logMember 0 "90" "TRACE"
      "debug", TFun(TStr, TUnit), logMember 1 "36" "DEBUG"
      "info", TFun(TStr, TUnit), logMember 2 "32" "INFO"
      "warn", TFun(TStr, TUnit), logMember 3 "33" "WARN"
      "traceWith", TFun(thunk, TUnit), logWithMember 0 "90" "TRACE"
      "debugWith", TFun(thunk, TUnit), logWithMember 1 "36" "DEBUG"
      "infoWith", TFun(thunk, TUnit), logWithMember 2 "32" "INFO"
      "warnWith", TFun(thunk, TUnit), logWithMember 3 "33" "WARN" ]

// ---- the filesystem family [D:fs-members] ---------------------------
// copy/move take (src, dst) — the universal convention; neither arg is
// "the data", so data-last does not apply. Destinations REFUSE to
// overwrite (reject-don't-guess; the overwriting spelling is an
// explicit File.delete first). Dir.create is the ONE idempotent
// exception: an existing directory IS create's post-condition, where
// an existing copy destination is data the caller did not ask to
// destroy. Every path resolves against the session cwd.
let private fsStr2 (name: string) (f: string -> string -> unit) : Value =
    VBuiltin(fun a ->
        VBuiltin(fun b ->
            match a, b with
            | VStr src, VStr dst ->
                f (Session.resolve src) (Session.resolve dst)
                VUnit
            | _ -> unreachable $"the checker rejects '{name}' on these arguments"))

let private fsMoreFileMembers: (string * Ty * Value) list =
    [ "delete",
      TFun(TStr, TUnit),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.File.Exists r) then
                  failwith $"File.delete: no such file: {r}"

              System.IO.File.Delete r
              VUnit
          | v -> unreachable $"the checker rejects 'File.delete' on {formatValue v}")
      "copy",
      TFun(TStr, TFun(TStr, TUnit)),
      fsStr2 "File.copy" (fun src dst ->
          if not (System.IO.File.Exists src) then
              failwith $"File.copy: no such file: {src}"

          if System.IO.File.Exists dst || System.IO.Directory.Exists dst then
              failwith $"File.copy: destination exists: {dst}"

          System.IO.File.Copy(src, dst))
      "move",
      TFun(TStr, TFun(TStr, TUnit)),
      fsStr2 "File.move" (fun src dst ->
          if not (System.IO.File.Exists src) then
              failwith $"File.move: no such file: {src}"

          if System.IO.File.Exists dst || System.IO.Directory.Exists dst then
              failwith $"File.move: destination exists: {dst}"

          System.IO.File.Move(src, dst))
      "size",
      // Size, not int [D:size] — the type is the POINT: show can render
      // what the type names (the one intended break of the session)
      TFun(TStr, TSize),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.File.Exists r) then
                  failwith $"File.size: no such file: {r}"

              VSize (System.IO.FileInfo r).Length
          | v -> unreachable $"the checker rejects 'File.size' on {formatValue v}") ]

let private dirMembers: (string * Ty * Value) list =
    [ "create",
      TFun(TStr, TUnit),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              // mkdir -p: parents created, existing = the post-condition
              System.IO.Directory.CreateDirectory(Session.resolve p) |> ignore
              VUnit
          | v -> unreachable $"the checker rejects 'Dir.create' on {formatValue v}")
      "exists",
      TFun(TStr, TBool),
      VBuiltin(fun v ->
          match v with
          | VStr p -> VBool(System.IO.Directory.Exists(Session.resolve p))
          | v -> unreachable $"the checker rejects 'Dir.exists' on {formatValue v}")
      "delete",
      TFun(TStr, TUnit),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.Directory.Exists r) then
                  failwith $"Dir.delete: no such directory: {r}"

              if System.IO.Directory.EnumerateFileSystemEntries r |> Seq.isEmpty |> not then
                  failwith $"Dir.delete: not empty: {r} — Dir.deleteAll removes a tree"

              System.IO.Directory.Delete r
              VUnit
          | v -> unreachable $"the checker rejects 'Dir.delete' on {formatValue v}")
      "deleteAll",
      TFun(TStr, TUnit),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.Directory.Exists r) then
                  failwith $"Dir.deleteAll: no such directory: {r}"

              System.IO.Directory.Delete(r, true)
              VUnit
          | v -> unreachable $"the checker rejects 'Dir.deleteAll' on {formatValue v}")
      "list",
      TFun(TStr, TSeq TStr),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.Directory.Exists r) then
                  failwith $"Dir.list: no such directory: {r}"

              // full paths, files AND directories, SORTED (the glob
              // precedent), EAGER (a listing is bounded); ** recursion
              // is Path.glob's job
              VSeq(
                  System.IO.Directory.EnumerateFileSystemEntries r
                  |> Seq.sort
                  |> Seq.map VStr
                  |> Seq.cache
              )
          | v -> unreachable $"the checker rejects 'Dir.list' on {formatValue v}")
      "move",
      TFun(TStr, TFun(TStr, TUnit)),
      fsStr2 "Dir.move" (fun src dst ->
          if not (System.IO.Directory.Exists src) then
              failwith $"Dir.move: no such directory: {src}"

          if System.IO.File.Exists dst || System.IO.Directory.Exists dst then
              failwith $"Dir.move: destination exists: {dst}"

          System.IO.Directory.Move(src, dst)) ]

// ---- Float [D:floats]: finite-only; no implicit widening -----------
let private floatFn (name: string) (f: float -> Value) : Value =
    VBuiltin(fun v ->
        match v with
        | VFloat x -> f x
        | v -> unreachable $"the checker rejects 'Float.{name}' on {formatValue v}")

let private floatMembers: (string * Ty * Value) list =
    [ "ofInt",
      TFun(TInt, TFloat),
      VBuiltin(fun v ->
          match v with
          | VInt n -> VFloat(float n)
          | v -> unreachable $"the checker rejects 'Float.ofInt' on {formatValue v}")
      "toInt",
      TFun(TFloat, TInt),
      floatFn "toInt" (fun x ->
          // truncates toward zero, the int-division rule; out of the
          // 64-bit range RAISES (the checkedInt posture)
          if x >= 9.2233720368547758e18 || x <= -9.2233720368547758e18 then
              failwith $"Float.toInt: out of int range: {formatFloat x}"
          else
              VInt(int64 (truncate x)))
      "round",
      TFun(TFloat, TFloat),
      // away-from-zero, the school rule — banker's rounding surprises
      floatFn "round" (fun x -> VFloat(System.Math.Round(x, System.MidpointRounding.AwayFromZero)))
      "abs", TFun(TFloat, TFloat), floatFn "abs" (fun x -> VFloat(abs x))
      "near",
      TFun(TFloat, TFun(TFloat, TFun(TFloat, TBool))),
      floatFn "near" (fun a ->
          VBuiltin(fun v ->
              match v with
              | VFloat b ->
                  VBuiltin(fun v2 ->
                      match v2 with
                      | VFloat eps -> VBool(abs (a - b) <= eps)
                      | v2 -> unreachable $"the checker rejects 'Float.near' on {formatValue v2}")
              | v -> unreachable $"the checker rejects 'Float.near' on {formatValue v}"))
      "parse",
      TFun(TStr, TFloat),
      VBuiltin(fun v ->
          match v with
          | VStr str ->
              (match parseFloat str with
               | Ok f -> VFloat f
               | Error e -> failwith $"Float.parse: {e}")
          | v -> unreachable $"the checker rejects 'Float.parse' on {formatValue v}")
      "tryParse",
      TFun(TStr, TNamed("Option", [ TFloat ])),
      VBuiltin(fun v ->
          match v with
          | VStr str ->
              (match parseFloat str with
               | Ok f -> VUnion("Some", Some(VFloat f))
               | Error _ -> VUnion("None", None))
          | v -> unreachable $"the checker rejects 'Float.tryParse' on {formatValue v}") ]

// ---- Size [D:size]: integer bytes; decimals only in text -----------
let private sizeMembers: (string * Ty * Value) list =
    [ "bytes",
      TFun(TInt, TSize),
      VBuiltin(fun v ->
          match v with
          | VInt n -> VSize n
          | v -> unreachable $"the checker rejects 'Size.bytes' on {formatValue v}")
      "toBytes",
      TFun(TSize, TInt),
      VBuiltin(fun v ->
          match v with
          | VSize b -> VInt b
          | v -> unreachable $"the checker rejects 'Size.toBytes' on {formatValue v}")
      "parse",
      TFun(TStr, TSize),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              (match parseSize s with
               | Ok b -> VSize b
               | Error e -> failwith $"Size.parse: {e}")
          | v -> unreachable $"the checker rejects 'Size.parse' on {formatValue v}")
      "tryParse",
      TFun(TStr, TNamed("Option", [ TSize ])),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              (match parseSize s with
               | Ok b -> VUnion("Some", Some(VSize b))
               | Error _ -> VUnion("None", None))
          | v -> unreachable $"the checker rejects 'Size.tryParse' on {formatValue v}") ]

// ---- Duration [D:duration]: integer ms; decimals only in text ------
let private durCtor (name: string) (mult: int64) : Value =
    VBuiltin(fun v ->
        match v with
        | VInt n -> VDur(Checked.(*) n mult)
        | v -> unreachable $"the checker rejects 'Duration.{name}' on {formatValue v}")

let private durationMembers: (string * Ty * Value) list =
    [ "ms", TFun(TInt, TDur), durCtor "ms" 1L
      "s", TFun(TInt, TDur), durCtor "s" 1000L
      "m", TFun(TInt, TDur), durCtor "m" 60000L
      "h", TFun(TInt, TDur), durCtor "h" 3600000L
      "toMillis",
      TFun(TDur, TInt),
      VBuiltin(fun v ->
          match v with
          | VDur n -> VInt n
          | v -> unreachable $"the checker rejects 'Duration.toMillis' on {formatValue v}")
      // float-returning and LOSSLESS [D:floats] — the truncation that
      // kept it unshipped is gone; Duration's own parse/render path
      // stays integer
      "toSeconds",
      TFun(TDur, TFloat),
      VBuiltin(fun v ->
          match v with
          | VDur n -> VFloat(float n / 1000.0)
          | v -> unreachable $"the checker rejects 'Duration.toSeconds' on {formatValue v}")
      "parse",
      TFun(TStr, TDur),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              (match parseDurationMs s with
               | Ok n -> VDur n
               | Error e -> failwith $"Duration.parse: {e}")
          | v -> unreachable $"the checker rejects 'Duration.parse' on {formatValue v}")
      "tryParse",
      TFun(TStr, TNamed("Option", [ TDur ])),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              (match parseDurationMs s with
               | Ok n -> VUnion("Some", Some(VDur n))
               | Error _ -> VUnion("None", None))
          | v -> unreachable $"the checker rejects 'Duration.tryParse' on {formatValue v}")
      // the one consumer worth landing with the type — module-qualified
      // so the coreutils sleep is NEVER shadowed (bindings-beat-PATH
      // would flip `sleep 5`'s meaning)
      "sleep",
      TFun(TDur, TUnit),
      VBuiltin(fun v ->
          match v with
          | VDur n ->
              // a negative duration REJECTS, located — the deadline
              // idiom (sleep (deadline - now)) must not silently no-op
              // on a past deadline [D:duration]
              if n < 0L then
                  failwith $"Duration.sleep: negative duration ({formatDuration n})"
              elif n > 0L then
                  // integer ticks — no float anywhere [D:duration]
                  System.Threading.Thread.Sleep(System.TimeSpan.FromTicks(n * System.TimeSpan.TicksPerMillisecond))

              VUnit
          | v -> unreachable $"the checker rejects 'Duration.sleep' on {formatValue v}") ]

let private moduleTable: (string * (string * Ty * Value) list) list =
    [ "Seq", seqMembers
      "Str", strMembers
      "Path", pathMembers
      "Option", optionMembers
      "File", fileMembers @ fsMoreFileMembers
      "Dir", dirMembers
      "Args", argsMembers
      "Env", envMembers
      "Log", logMembers
      "Duration", durationMembers
      "Size", sizeMembers
      // the bounded-loop option templates [D:retry-poll]: the resting
      // values the key=value head desugars over
      "Retry",
      [ "defaults",
        TNamed("Retry", []),
        VRecord("Retry", Map [ "attempts", VInt 5L; "delay", VDur 1000L; "timeout", VUnion("None", None) ]) ]
      "Poll",
      [ "defaults", TNamed("Poll", []), VRecord("Poll", Map [ "timeout", VDur 60000L; "interval", VDur 1000L ]) ]
      "Float", floatMembers ]

// ---- builtin docs [D:builtin-docs] (PLAN-doc-comments half 2) --------
// OUT-OF-BAND, exactly as half 1: Value/Eval/Check never see a doc. The
// Example is executable DATA (run by the doc-example test), not prose
// parsed from a literal — so a builtin hover is the only doc that cannot
// rot. Rendered TYPE-FIRST by the LSP (half 1's declHover layout), so the
// Summary never restates the signature; the Pointer names the LAW or
// boundary a member obeys (quoted from SEMANTICS/DECISIONS, not memory).
type BuiltinDoc =
    { Summary: string
      Example: string option
      Pointer: string option
      // parameter names for the annotated hover signature
      // [D:annotated-signature] — a SEPARATE field, never parsed out of
      // the prose (that is D1's F#-literal trap). Empty -> arrow fallback.
      // Half 2's writing pass names every parameter; this is a sample.
      Params: string list }

let private bd (summary: string) (example: string option) (pointer: string option) : BuiltinDoc =
    { Summary = summary
      Example = example
      Pointer = pointer
      Params = [] }

/// name a member's parameters for the annotated signature (a `{ bd … with
/// Params = … }` shorthand for the sample; the full pass is half 2)
let private named (ps: string list) (d: BuiltinDoc) : BuiltinDoc = { d with Params = ps }

/// keyed as the name appears at a use site: `Seq.map`, a bare `print`,
/// `Env.load`. Filled set by set; coverage is reported as a fraction.
let builtinDocs: Map<string, BuiltinDoc> =
    Map
        [
          // ---- Seq: lazy sequences (weir has no list type) ----
          "Seq.map",
          bd
              "Apply a function to every element, lazily."
              (Some "[1; 2; 3] |> Seq.map (fun x -> x + 1) |> Seq.force")
              None
          |> named [ "f"; "xs" ]
          "Seq.where",
          bd
              "Keep the elements a predicate accepts, lazily."
              (Some "[1; 2; 3] |> Seq.where (fun x -> x > 1) |> Seq.force")
              None
          |> named [ "pred"; "xs" ]
          "Seq.choose",
          bd
              "Map and drop the None results in one lazy pass."
              (Some "[1; 2; 3] |> Seq.choose (fun x -> if x > 1 then Some x else None) |> Seq.force")
              None
          |> named [ "f"; "xs" ]
          "Seq.fold",
          bd
              "Left-fold: thread an accumulator through the elements."
              (Some "[1; 2; 3] |> Seq.fold (fun acc x -> acc + x) 0")
              None
          |> named [ "f"; "init"; "xs" ]
          "Seq.force",
          bd
              "Materialize a lazy sequence, caching it."
              (Some "[1; 2; 3] |> Seq.map (fun x -> x + 1) |> Seq.force")
              (Some "force once, then reuse freely — it memoizes (the two customers: reuse and timing).")
          |> named [ "xs" ]
          "Seq.head",
          (bd "The first element (raises on empty)." (Some "Seq.head [1; 2; 3]") None
           |> named [ "xs" ])
          "Seq.tryHead",
          (bd "The first element as an Option, None when empty." (Some "Seq.tryHead [1; 2; 3]") None
           |> named [ "xs" ])
          "Seq.tryFind",
          bd
              "The first element a predicate accepts, as an Option."
              (Some "[1; 2; 3] |> Seq.tryFind (fun x -> x > 1)")
              None
          |> named [ "pred"; "xs" ]
          "Seq.item",
          (bd "The element at a zero-based index (raises out of range)." (Some "[1; 2; 3] |> Seq.item 0") None
           |> named [ "i"; "xs" ])
          "Seq.tryItem",
          (bd "The element at an index as an Option." (Some "[1; 2; 3] |> Seq.tryItem 0") None
           |> named [ "i"; "xs" ])
          "Seq.take",
          (bd "The first n elements, lazily." (Some "[1; 2; 3] |> Seq.take 2 |> Seq.force") None
           |> named [ "n"; "xs" ])
          "Seq.first",
          (bd "The first n elements." (Some "[1; 2; 3] |> Seq.first 2 |> Seq.force") None
           |> named [ "n"; "xs" ])
          "Seq.skip",
          (bd "Drop the first n elements, keep the rest lazily." (Some "[1; 2; 3] |> Seq.skip 1 |> Seq.force") None
           |> named [ "n"; "xs" ])
          "Seq.length",
          (bd "Count the elements (forces the sequence)." (Some "Seq.length [1; 2; 3]") None
           |> named [ "xs" ])
          "Seq.isEmpty",
          (bd "True when the sequence has no elements." (Some "Seq.isEmpty [1; 2; 3]") None
           |> named [ "xs" ])
          "Seq.sum",
          (bd "Add the elements of an int sequence." (Some "Seq.sum [1; 2; 3]") None
           |> named [ "xs" ])
          "Seq.contains",
          (bd "True when an element is present." (Some "Seq.contains 2 [1; 2; 3]") None
           |> named [ "x"; "xs" ])
          "Seq.exists",
          (bd "True when any element satisfies a predicate." (Some "[1; 2; 3] |> Seq.exists (fun x -> x > 2)") None
           |> named [ "pred"; "xs" ])
          "Seq.forall",
          (bd "True when every element satisfies a predicate." (Some "[1; 2; 3] |> Seq.forall (fun x -> x > 0)") None
           |> named [ "pred"; "xs" ])
          "Seq.distinct",
          (bd "Drop duplicate elements, keeping first order." (Some "[1; 1; 2] |> Seq.distinct |> Seq.force") None
           |> named [ "xs" ])
          "Seq.append",
          (bd "Concatenate two sequences, lazily." (Some "Seq.append [1; 2] [3; 4] |> Seq.force") None
           |> named [ "xs"; "ys" ])
          "Seq.sortBy",
          (bd "Order by a key projection." (Some "[3; 1; 2] |> Seq.sortBy (fun x -> x) |> Seq.force") None
           |> named [ "key"; "xs" ])
          "Seq.sortByDescending",
          bd
              "Order by a key projection, descending."
              (Some "[1; 3; 2] |> Seq.sortByDescending (fun x -> x) |> Seq.force")
              None
          |> named [ "key"; "xs" ]
          "Seq.iter",
          (bd "Run a unit-returning effect over each element." (Some "[1; 2; 3] |> Seq.iter (fun x -> ())") None
           |> named [ "f"; "xs" ])
          "Seq.windowed",
          (bd
              "Sliding windows of size n, LAZY (produced as the source is pulled; a short source yields the EMPTY seq — no partial window; windows view the same memoized elements). Raises when n <= 0."
              (Some "[1; 2; 3] |> Seq.windowed 2 |> Seq.map Seq.force |> Seq.force")
              None
           |> named [ "n"; "xs" ])
          "Seq.last",
          (bd
              "The last element — ASSERTS the source is non-empty (the X/tryX rule; raises 'last: empty sequence'), and FORCES the whole source by necessity: an infinite source does not return."
              (Some "[1; 2; 3] |> Seq.last")
              None
           |> named [ "xs" ])
          "Seq.tryLast",
          (bd
              "The last element as an Option (None when empty) — the asking twin; forces the whole source."
              (Some "[] |> Seq.tryLast")
              None
           |> named [ "xs" ])
          "Seq.pairwise",
          (bd "Adjacent pairs: (e0,e1), (e1,e2), and so on." (Some "[1; 2; 3] |> Seq.pairwise |> Seq.force") None
           |> named [ "xs" ])
          "Seq.zip",
          bd
              "Pair two sequences element-wise, stopping at the shorter."
              (Some "Seq.zip [1; 2] [3; 4] |> Seq.force")
              None
          |> named [ "xs"; "ys" ]
          "Seq.range",
          (bd "A lazy arithmetic range: start, step, stop." (Some "Seq.range 1 1 5 |> Seq.force") None
           |> named [ "start"; "step"; "stop" ])
          "Seq.groupBy",
          bd
              "Group elements by a key into Group records."
              (Some "[1; 2; 3] |> Seq.groupBy (fun x -> x) |> Seq.force")
              None
          |> named [ "key"; "xs" ]
          "Seq.pmap",
          bd
              "Map in parallel across worker threads."
              (Some "[1; 2; 3] |> Seq.pmap (fun x -> x + 1) |> Seq.force")
              (Some "ordered, eager, at most 64 workers; the first error by input order wins.")
          |> named [ "f"; "xs" ]
          "Seq.piter",
          bd
              "Run an effect over each element in parallel."
              (Some "[1; 2; 3] |> Seq.piter (fun x -> ())")
              (Some "workers fork the session (worker-local cd, dies at join).")
          |> named [ "f"; "xs" ]
          "Seq.pfirst",
          bd
              "Race an arm over every element; the FIRST SUCCESS wins. Losers' spawned processes are tree-killed and their failures never surface. All arms failed rethrows the first error by input order; an empty sequence raises."
              (Some "[3; 1; 2] |> Seq.pfirst (fun n -> n * 10)")
              (Some "a race, not a retry: same fetch against N mirrors, first answer wins.")
          |> named [ "f"; "xs" ]
          "Seq.pfirstWith",
          bd "Seq.pfirst at an explicit concurrency ceiling." (Some "[1; 2] |> Seq.pfirstWith 2 (fun n -> n)") None
          |> named [ "degree"; "f"; "xs" ]

          // ---- Option ----
          "Option.iter",
          (bd
              "Run a unit effect on the Some value; None runs NOTHING (a Some-only side effect with no match ceremony)."
              (Some "Some \"x\" |> Option.iter print")
              None
           |> named [ "f"; "opt" ])
          "Option.orElse",
          (bd
              "The option itself when Some, else the FALLBACK (fallback first, so it pipes data-last). Stays in Option — Option.defaultValue is the one that UNWRAPS. The fallback is an ordinary eager argument (an orElseWith twin is parked on the defaultWith precedent)."
              (Some "None |> Option.orElse (Some 1)")
              None
           |> named [ "fallback"; "opt" ])
          "Option.map",
          (bd "Apply a function inside a Some, pass None through." (Some "Option.map (fun x -> x + 1) (Some 5)") None
           |> named [ "f"; "opt" ])
          "Option.defaultValue",
          bd "The Some value, or a fallback when None." (Some "Option.defaultValue 0 (Some 5)") None
          |> named [ "fallback"; "opt" ]
          "Option.defaultWith",
          bd
              "Like defaultValue, but the fallback is computed only when None."
              (Some "Option.defaultWith (fun () -> 0) None")
              None
          |> named [ "fallback"; "opt" ]

          // ---- bare / hot-path ----
          "print",
          (bd "Write a value and a trailing newline to stdout." (Some "print \"hi\"") None
           |> named [ "value" ])
          "printerr",
          (bd "Write a value and a newline to stderr." (Some "printerr \"oops\"") None
           |> named [ "value" ])
          "show",
          (bd "Render a value to its string form (total; functions show opaquely)." (Some "show [1; 2; 3]") None
           |> named [ "value" ])
          "not", (bd "Boolean negation." (Some "not true") None |> named [ "b" ])
          "force",
          (bd "Materialize a lazy sequence, caching it (the bare Seq.force)." (Some "[1; 2; 3] |> force") None
           |> named [ "xs" ])
          "fail",
          (bd "Stop with a message and exit code 1." None (Some "message-carrying; `exit n` is the bare-code spelling.")
           |> named [ "message" ])
          "exit", (bd "Exit the process with a status code." None None |> named [ "code" ])

          // ---- Str ----
          "Str.contains",
          (bd "True when a substring is present." (Some "\"abc\" |> Str.contains \"b\"") None
           |> named [ "needle"; "s" ])
          "Str.startsWith",
          (bd "True when the string starts with a prefix." (Some "\"abc\" |> Str.startsWith \"a\"") None
           |> named [ "prefix"; "s" ])
          "Str.endsWith",
          (bd "True when the string ends with a suffix." (Some "\"abc\" |> Str.endsWith \"c\"") None
           |> named [ "suffix"; "s" ])
          "Str.trim",
          (bd "Drop leading and trailing whitespace." (Some "Str.trim \"  x  \"") None
           |> named [ "s" ])
          "Str.trimStart",
          (bd "Drop leading whitespace." (Some "Str.trimStart \"  x\"") None
           |> named [ "s" ])
          "Str.trimEnd",
          (bd "Drop trailing whitespace." (Some "Str.trimEnd \"x  \"") None
           |> named [ "s" ])
          "Str.toLower",
          (bd "Lowercase (invariant culture)." (Some "Str.toLower \"ABC\"") None
           |> named [ "s" ])
          "Str.toUpper",
          (bd "Uppercase (invariant culture)." (Some "Str.toUpper \"abc\"") None
           |> named [ "s" ])
          "Str.split",
          (bd "Split on a separator into a sequence." (Some "Str.split \",\" \"a,b,c\" |> Seq.force") None
           |> named [ "sep"; "s" ])
          "Str.join",
          (bd "Join a sequence of strings with a separator." (Some "Str.join \",\" [\"a\"; \"b\"]") None
           |> named [ "sep"; "xs" ])
          "Str.replace",
          bd "Replace every occurrence of a substring." (Some "Str.replace \"a\" \"b\" \"aba\"") None
          |> named [ "old"; "new"; "s" ]
          "Str.length", (bd "The number of characters." (Some "Str.length \"abc\"") None |> named [ "s" ])
          "Str.sub",
          (bd "A substring by start index and length." (Some "Str.sub 0 2 \"abc\"") None
           |> named [ "start"; "len"; "s" ])
          "Str.toInt",
          (bd "Parse an int (raises on a non-number)." (Some "Str.toInt \"42\"") None
           |> named [ "s" ])
          "Str.tryToInt",
          (bd "Parse an int as an Option, None when it is not a number." (Some "Str.tryToInt \"42\"") None
           |> named [ "s" ])
          "Str.sha256",
          (bd
              "The SHA-256 digest of the string's UTF-8 bytes, lowercase hex (sha256sum parity). sha256 only — more algorithms on receipt."
              (Some "Str.sha256 \"hello\"")
              None
           |> named [ "s" ])
          "Str.toBase64",
          (bd
              "Base64 of the string's UTF-8 bytes — ONE unwrapped line (no 76-column MIME wrap, no -w0 tax)."
              (Some "Str.toBase64 \"caf\u00e9\"")
              None
           |> named [ "s" ])
          "Str.fromBase64",
          (bd
              "Decode standard base64 (padded or unpadded) to text; raises on invalid input OR when the bytes are not valid UTF-8 (never U+FFFD corruption)."
              (Some "Str.fromBase64 \"Y2Fmw6k=\"")
              None
           |> named [ "s" ])
          "Str.tryFromBase64",
          (bd
              "fromBase64 as an Option: None for malformed base64 and for valid-base64-of-non-text alike."
              (Some "Str.tryFromBase64 \"!!!\"")
              None
           |> named [ "s" ])
          "Str.tryIndexOf",
          (bd "The index of a substring as an Option." (Some "Str.tryIndexOf \"b\" \"abc\"") None
           |> named [ "needle"; "s" ])
          "Str.isMatch",
          bd "True when a regex matches anywhere in the string." (Some "Str.isMatch \"[0-9]+\" \"x42\"") None
          |> named [ "pattern"; "subject" ]
          "Str.rmatch",
          bd "The first regex match's groups as an Option of a sequence." (Some "Str.rmatch \"([0-9]+)\" \"x42\"") None
          |> named [ "pattern"; "s" ]
          "Str.rmatchAll",
          bd
              "Every regex match's groups, as a sequence of sequences."
              (Some "Str.rmatchAll \"[0-9]+\" \"a1b2\" |> Seq.force")
              None
          |> named [ "pattern"; "s" ]

          // ---- Path (pure string ops; glob touches the filesystem) ----
          "Path.dir",
          (bd "The directory part of a path." (Some "Path.dir \"a/b/c\"") None
           |> named [ "path" ])
          "Path.fileName",
          (bd "The final component of a path." (Some "Path.fileName \"a/b.txt\"") None
           |> named [ "path" ])
          "Path.stem",
          (bd "The file name without its extension." (Some "Path.stem \"a/b.txt\"") None
           |> named [ "path" ])
          "Path.extension",
          (bd "The extension, including the dot." (Some "Path.extension \"a.txt\"") None
           |> named [ "path" ])
          "Path.combine",
          (bd "Join two path segments." (Some "Path.combine \"a\" \"b\"") None
           |> named [ "a"; "b" ])
          "Path.tempRoot",
          (bd
              "The system temp directory (a pure query; no trailing separator, platform-native)."
              (Some "Path.tempRoot ()")
              None
           |> named [ "()" ])
          "Path.newTempDir",
          (bd
              "CREATE a fresh unique directory under the temp root and return its path (within tmp's naming). Cleanup is the caller's or the OS's — use `within tmp dir` for removal on scope exit; newTempDir when the directory must OUTLIVE the block. Neither cleans up on Ctrl+C (SIGINT runs no managed cleanup)."
              (Some "Path.newTempDir () |> Str.startsWith (Path.tempRoot ())")
              None
           |> named [ "()" ])
          "Path.glob",
          bd
              "Match a glob against the filesystem (lazy; globstar skips symlinks)."
              (Some "Path.glob \"*.nope123\" |> Seq.force")
              None
          |> named [ "pattern" ]

          // ---- File (read/write touch the filesystem — no inline example) ----
          "File.exists",
          (bd "True when a path exists." (Some "File.exists \"README.md\"") None
           |> named [ "path" ])
          "File.delete",
          (bd
              "Delete a file (raises naming the path when absent). The explicit pre-step for an overwriting copy/move."
              (Some
                  "let d = Path.newTempDir () in ([\"x\"] |> File.write $\"{d}/f.txt\") ; File.delete $\"{d}/f.txt\" ; Dir.delete d")
              None
           |> named [ "path" ])
          "File.copy",
          (bd
              "Copy src to dst — (src, dst), the universal convention (neither arg is 'the data', so data-last does not apply). REFUSES an existing destination (raises naming it); delete first to overwrite."
              (Some
                  "let d = Path.newTempDir () in ([\"x\"] |> File.write $\"{d}/a.txt\") ; File.copy $\"{d}/a.txt\" $\"{d}/b.txt\" ; Dir.deleteAll d")
              None
           |> named [ "src"; "dst" ])
          "File.move",
          (bd
              "Move (rename) src to dst — (src, dst); refuses an existing destination."
              (Some
                  "let d = Path.newTempDir () in ([\"x\"] |> File.write $\"{d}/a.txt\") ; File.move $\"{d}/a.txt\" $\"{d}/b.txt\" ; Dir.deleteAll d")
              None
           |> named [ "src"; "dst" ])
          "File.size",
          (bd
              "The file's size as a Size — compare directly (File.size p > 10MiB); Size.toBytes for the int. Raises when absent (the plain name asserts; a trySize is a park)."
              (Some
                  "let d = Path.newTempDir () in let f = $\"{d}/a.txt\" in ([\"x\"] |> File.write f) ; print $\"{File.size f}\" ; Dir.deleteAll d")
              None
           |> named [ "path" ])
          "Dir.create",
          (bd
              "Create a directory AND its parents; succeeds silently when it already exists (idempotent — an existing directory IS the post-condition; the one deliberate exception to refuse-overwrite)."
              (Some "Dir.create (Path.tempRoot ())")
              None
           |> named [ "path" ])
          "Dir.exists",
          (bd "True when the directory exists." (Some "Dir.exists \".\"") None
           |> named [ "path" ])
          "Dir.delete",
          (bd
              "Delete an EMPTY directory (refuses a non-empty one, naming Dir.deleteAll; raises when absent)."
              (Some "Dir.delete (Path.newTempDir ())")
              None
           |> named [ "path" ])
          "Dir.deleteAll",
          (bd
              "DELETE THE DIRECTORY AND EVERYTHING UNDER IT, recursively. The destructive one — there is no undo."
              (Some "let d = Path.newTempDir () in Dir.create $\"{d}/tree-a/tree-b\" ; Dir.deleteAll d")
              None
           |> named [ "path" ])
          "Dir.list",
          (bd
              "The directory's entries as FULL paths — files and directories both, SORTED (the glob precedent), eager. Filter with Seq.where + File.exists/Dir.exists; `Path.glob \"**\"` is the recursive spelling."
              (Some "let d = Path.newTempDir () in print $\"{Dir.list d |> Seq.length}\" ; Dir.delete d")
              None
           |> named [ "path" ])
          "Dir.move",
          (bd
              "Move (rename) a directory — (src, dst); refuses an existing destination."
              (Some "let d = Path.newTempDir () in Dir.move d $\"{d}-m\" ; Dir.delete $\"{d}-m\"")
              None
           |> named [ "src"; "dst" ])
          "File.read", (bd "Read a file's lines lazily." None None |> named [ "path" ])
          "File.write",
          (bd "Write a sequence of lines to a file (overwrites)." None None
           |> named [ "path"; "lines" ])
          "File.append",
          (bd "Append a sequence of lines to a file." None None
           |> named [ "path"; "lines" ])

          // ---- Env ----
          "Env.get",
          (bd "A process environment variable as an Option." (Some "Env.get \"PATH\"") None
           |> named [ "name" ])
          "Env.vars", bd "Every environment variable as EnvVar records." (Some "Env.vars |> Seq.force") None
          "Env.pair",
          (bd "Build one EnvVar from a name and value." (Some "Env.pair \"K\" \"V\"") None
           |> named [ "name"; "value" ])
          "Env.ofPairs",
          (bd "Build EnvVar records from name/value tuples." (Some "Env.ofPairs [(\"K\", \"V\")] |> Seq.force") None
           |> named [ "pairs" ])
          "Env.fromFile",
          (bd "Read `.env` lines (KEY=value) as EnvVar records." None None
           |> named [ "path" ])
          "Env.load",
          bd
              "Load the environment into a typed record (scalars, Option, bool)."
              None
              (Some "the field law: field names are verbatim; check-time validates the field TYPES.")

          // ---- Args ----
          "Args.flag",
          (bd "True when a --flag (or its short form) is present in argv." (Some "Args.flag \"verbose\"") None
           |> named [ "name" ])
          "Args.value",
          (bd "The value of a --name option as an Option." (Some "Args.value \"name\"") None
           |> named [ "name" ])
          "Args.load",
          bd
              "Parse argv into a typed record or union."
              None
              (Some "three shapes: a record, a union of subcommands, or a record containing a union.")

          // ---- Self (per-run introspection) ----
          "Self.pid", bd "This process's id." None None
          "Self.args", bd "The invoked script's argument vector (a process fact — the same in every module)." None None
          "Self.stdin", bd "This process's standard input, as lazy lines (a process fact)." None None
          "Self.scriptPath", bd "The path of the FILE reading it — a module sees its own path." None None
          "Self.entryPath",
          bd "The path of the INVOKED script (a process fact — the same in every module, unlike scriptPath)." None None

          // ---- Float: finite-only [D:floats] ---------------------------
          "Float.ofInt",
          (bd
              "An int as a float — the explicit widening (weir never widens implicitly)."
              (Some "Float.ofInt 3 / 2.0")
              None
           |> named [ "n" ])
          "Float.toInt",
          (bd
              "The integer part, truncating toward zero (raises outside the 64-bit range)."
              (Some "Float.toInt 2.9")
              None
           |> named [ "f" ])
          "Float.round",
          (bd "Round to the nearest whole, halves away from zero (2.5 rounds to 3.0)." (Some "Float.round 2.5") None
           |> named [ "f" ])
          "Float.abs", (bd "The absolute value." (Some "Float.abs (0.0 - 1.5)") None |> named [ "f" ])
          "Float.near",
          (bd
              "True when a and b differ by at most eps — the equality idiom (floats do not join '==')."
              (Some "Float.near (0.1 + 0.2) 0.3 1e-9")
              None
           |> named [ "a"; "b"; "eps" ])
          "Float.parse",
          (bd
              "Parse float text — the shape show renders (raises on anything else, including NaN/Infinity: weir floats are finite)."
              (Some "Float.parse \"1.5e-3\"")
              None
           |> named [ "text" ])
          "Float.tryParse",
          (bd "Float.parse as an Option — None instead of the raise." (Some "Float.tryParse \"nope\"") None
           |> named [ "text" ])

          // ---- retry/poll option templates [D:retry-poll] --------------
          "Retry.defaults",
          bd
              "The retry template: attempts = 5, delay = 1s, timeout = None. `retry attempts=5` is `retry { Retry.defaults with attempts = 5 }`."
              None
              None
          "Poll.defaults", bd "The poll template: timeout = 1m, interval = 1s." None None

          // ---- Size: bytes as a type [D:size] --------------------------
          "Size.bytes",
          (bd "A size of n bytes — the literal 512B, as a function." (Some "Size.bytes 512") None
           |> named [ "n" ])
          "Size.toBytes",
          (bd
              "The total bytes as an int — the exact exit (show's rendering truncates to one decimal)."
              (Some "Size.toBytes 2KiB")
              None
           |> named [ "s" ])
          "Size.parse",
          (bd
              "Parse size text: binary units at 1024 (1.5MiB), the SI spellings at powers of ten (1MB is 10^6 — the writer chose the unit), B for bytes; sub-byte precision raises."
              (Some "Size.parse \"1.5MiB\"")
              None
           |> named [ "text" ])
          "Size.tryParse",
          (bd "Size.parse as an Option — None instead of the raise." (Some "Size.tryParse \"nope\"") None
           |> named [ "text" ])

          // ---- Duration: time as a type [D:duration] -------------------
          "Duration.ms",
          (bd "A duration of n milliseconds — the literal 500ms, as a function." (Some "Duration.ms 500") None
           |> named [ "n" ])
          "Duration.s", (bd "A duration of n seconds." (Some "Duration.s 30") None |> named [ "n" ])
          "Duration.m", (bd "A duration of n minutes." (Some "Duration.m 5") None |> named [ "n" ])
          "Duration.h", (bd "A duration of n hours." (Some "Duration.h 2") None |> named [ "n" ])
          "Duration.toMillis",
          (bd "The total milliseconds as an int (ratios, JSON fields)." (Some "Duration.toMillis 2m") None
           |> named [ "d" ])
          "Duration.toSeconds",
          (bd "The total seconds as a float, lossless (2500ms is 2.5)." (Some "Duration.toSeconds 2500ms") None
           |> named [ "d" ])
          "Duration.parse",
          (bd
              "Parse duration text — the shape show renders: 1h30m, 2.5s, 500ms (raises on anything else, including sub-millisecond precision)."
              (Some "Duration.parse \"1h30m\"")
              None
           |> named [ "text" ])
          "Duration.tryParse",
          (bd
              "Duration.parse as an Option — None instead of the raise."
              (Some "Duration.tryParse \"not-a-duration\"")
              None
           |> named [ "text" ])
          "Duration.sleep",
          (bd
              "Block for the duration (zero returns immediately; a negative duration raises; OS timer granularity applies — ~15ms Windows, ~1ms Linux — so a small sleep is a floor, not a promise). Module-qualified on purpose: bare sleep stays the coreutils command."
              (Some "Duration.sleep 10ms")
              None
           |> named [ "d" ])

          // ---- boundary forms: adapters between text and typed data ----
          "from json",
          bd
              "Parse a JSON line stream into a declared record type. Fields are int/string/bool or Option of one; an Option field reads a missing key or null as None."
              None
              (Some "a pipe stage: xs |> from json Config.")
          "from porcelain",
          bd
              "Parse `git status --porcelain` lines into Change records."
              None
              (Some "a pipe stage: xs |> from porcelain.")
          "to json",
          bd
              "Render a sequence of records or primitives to JSON lines. A None field omits its key (so from json reads it back as None)."
              None
              (Some "a pipe stage: xs |> to json.")
          "from yaml",
          bd
              "Parse YAML lines (the strict subset: block maps/sequences, scalars, # comments; --- multi-doc) into a declared record TREE — nested records, seqs, seq<string * _> mappings, Option (missing/null reads None). Anchors, tags, and flow style are rejected."
              None
              (Some "a pipe stage: lines |> from yaml Deployment — yields seq<Deployment>, one per document.")
          "to yaml",
          bd
              "Render a value tree (records, seqs, scalars, Option, Yaml nodes) to YAML lines. A seq renders ---separated documents; a None field omits its key; strings that could be mis-typed (no, 007, 1e5) are quoted."
              None
              (Some "a pipe stage: deployment |> to yaml.")

          // ---- reifiers: turn a command chain into a value [D:exit-reifiers].
          // Surface names; the typed tree carries the un-typeable |completed
          // key (+ Env/In twins), mapped back by reifierSurface below. ----
          "complete",
          bd
              "Reify a command chain to a Completed record (exitCode, stdout, stderr)."
              None
              (Some "the reifier law: output goes where the meaning goes.")
          "succeeds",
          bd "Reify a command to a bool: did it exit zero?" None (Some "the reifier law: the meaning is the verdict.")
          "orFail",
          bd
              "Stream a command's output, raising with a message on a nonzero exit."
              None
              (Some "the reifier law: output streams, the exit is the meaning.")
          "exitCode",
          bd "Reify a command to its integer exit code." None (Some "the reifier law: the meaning is the code.")

          // ---- types: a hover renders the structure; the value here is
          // WHEN you get one ----
          "Completed", bd "A finished command: exitCode, stdout, stderr. You get one from `| complete`." None None
          "Change",
          bd "One `git status --porcelain` line: status, staged, unstaged, path. From `from porcelain`." None None
          "FileRow", bd "A directory entry: name, bytes, readOnly. From `ls`." None None
          "EnvVar", bd "A name/value environment pair. From `Env.vars` / `pair` / `ofPairs` / `fromFile`." None None
          "Group", bd "A key and its items, from `Seq.groupBy`." None None ]

/// the boundary adapters a direction supports, DERIVED from the doc keys
/// (`from json` / `to yaml` …) [D:form-word-hover] — the one source the
/// adapters' own hovers already read, so the `from`/`to` discovery hover,
/// the completion, and the colorizer cannot drift from it. `dir` is
/// "from" or "to"; `porcelain` is read-only, so `to` yields only what has
/// a `to <x>` doc. Map is key-sorted, so the order is stable.
let adapterNames (dir: string) : string list =
    builtinDocs
    |> Map.toList
    |> List.choose (fun (k, _) ->
        if k.StartsWith(dir + " ") then
            Some(k.Substring(dir.Length + 1))
        else
            None)
    |> List.distinct

/// every adapter word in either direction — the colorizer's membership
/// test (it has no direction context, only "is this word an adapter")
let allAdapterNames: Set<string> =
    Set.union (Set.ofList (adapterNames "from")) (Set.ofList (adapterNames "to"))

/// map a reifier's internal key (|completed, |completedEnv, |completedIn,
/// and the succeeded/orFailed/exitCoded families) back to the surface
/// name a user wrote, so hover keys the doc [D:builtin-docs].
let reifierSurface (name: string) : string option =
    if name.StartsWith "|completed" then Some "complete"
    elif name.StartsWith "|succeeded" then Some "succeeds"
    elif name.StartsWith "|orFailed" then Some "orFail"
    elif name.StartsWith "|exitCoded" then Some "exitCode"
    else None

/// the hover/completion text: summary, then example, then pointer — each
/// on its own line, in the order half 1 renders after the type.
let renderBuiltinDoc (d: BuiltinDoc) : string =
    [ Some d.Summary; d.Example; d.Pointer ] |> List.choose id |> String.concat "\n"

let private bareAliases: Set<string> =
    Set
        [ "map"
          "where"
          "first"
          "take"
          "head"
          "sum"
          "force"
          "contains"
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
          "tryToInt" ]

let private bareEntries: (string * Ty * Value) list =
    moduleTable
    // Float never flattens [D:floats]: its toInt would shadow the bare
    // toInt alias (Str's) — module-qualified only, like Option
    |> List.filter (fun (m, _) -> m <> "Option" && m <> "Float")
    |> List.collect snd
    |> List.filter (fun (n, _, _) -> bareAliases.Contains n && n <> "length")

let private failImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr msg -> failwith msg
        | v -> unreachable $"the checker rejects 'fail' on {formatValue v}")

let private printerrImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            writeLinesTo System.Console.Error items
            VUnit
        | (VStr _ | VInt _ | VFloat _ | VBool _) as scalar ->
            System.Console.Error.WriteLine(scalarString "printerr argument" scalar)
            VUnit
        | v -> unreachable $"the checker rejects 'printerr' on {formatValue v}")

// cmdEnv/runEnv [D:child-env-overlay] via Proc.linesWith (lines IS
// linesWith [] — one spawn path by construction). The overlay seq is
// forced inside the delay, so Env.fromFile boundary errors keep
// raise-at-force semantics.
let private envVarPairs (v: Value) : (string * string) list =
    match v with
    | VSeq items ->
        items
        |> Seq.map (fun item ->
            match item with
            | VRecord(_, fields) ->
                match fields["name"], fields["value"] with
                | VStr n, VStr value -> n, value
                | _ -> unreachable "the checker rejects non-EnvVar overlay entries"
            | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
        |> List.ofSeq
    | v -> unreachable $"the checker rejects 'cmdEnv' on {formatValue v}"

let private cmdEnvImpl: Value =
    VBuiltin(fun envV ->
        VBuiltin(fun progV ->
            VBuiltin(fun argsV ->
                match progV, argsV with
                | VStr prog, VSeq args ->
                    if prog.Trim() = "" then
                        failwith "cmd: empty program name"

                    let argv = argStrings args

                    VSeq(
                        Seq.delay (fun () -> Proc.linesWith (envVarPairs envV) (Proc.resolveProg prog) argv None)
                        |> Seq.map VStr
                    )
                | _ -> unreachable "the checker rejects 'cmdEnv' on these arguments")))

// fst/snd — F#'s pair projections; the pair-only typing (TTuple [a; b])
// makes wider tuples a unification error, same as F#
let private fstImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VTuple(a :: _) -> a
        | v -> unreachable $"the checker rejects 'fst' on {formatValue v}")

let private sndImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VTuple(_ :: b :: _) -> b
        | v -> unreachable $"the checker rejects 'snd' on {formatValue v}")

let private entries: (string * Ty * Value) list =
    [ "ls", seqFileRow, realLs
      "nats", seqInt, natsImpl
      "into", TFun(TStr, TFun(seqStr, seqStr)), intoImpl
      "cd", TFun(TStr, TStr), cdImpl
      "pwd", TSeq TStr, pwdImpl
      "not", TFun(TBool, TBool), notImpl
      "fst", TFun(TTuple [ tA; tB ], tA), fstImpl
      "snd", TFun(TTuple [ tA; tB ], tB), sndImpl
      // reifier desugar targets [D:drop-reify-builtins]: '|'-prefixed so
      // `| complete`/`| succeeds`/`| orFail`/`| exitCode` resolve them,
      // user code cannot (identifiers are [A-Za-z_]..). Reification at
      // expression position is `let r = cmd | complete`; a computed argv
      // splats into the chain [D:splat-reifier-chains].
      "|completed", TFun(TStr, TFun(TSeq TStr, TNamed(completedDef.Name, []))), completedImpl
      "|succeeded", TFun(TStr, TFun(TSeq TStr, TBool)), succeededWith []
      "|orFailed", TFun(TStr, TFun(TStr, TFun(TSeq TStr, TUnit))), orFailedWith []
      "|exitCoded", TFun(TStr, TFun(TSeq TStr, TInt)), exitCodedWith []
      // stdin-carrying twins — the value-headed reifier route
      // (`xs | grep | complete`) [D:value-headed-pipe]
      "|completedIn", TFun(TStr, TFun(TSeq TStr, TFun(TSeq TStr, TNamed(completedDef.Name, [])))), completedWithIn []
      "|succeededIn", TFun(TStr, TFun(TSeq TStr, TFun(TSeq TStr, TBool))), succeededWithIn []
      "|orFailedIn", TFun(TStr, TFun(TStr, TFun(TSeq TStr, TFun(TSeq TStr, TUnit)))), orFailedWithIn []
      "|exitCodedIn", TFun(TStr, TFun(TSeq TStr, TFun(TSeq TStr, TInt))), exitCodedWithIn []
      "fail", TFun(TStr, TUnit), failImpl
      "exit", TFun(TInt, TUnit), exitImpl
      // env-carrying twins — the env-sigil reifier route
      // (`$e(cmd | complete)`)
      "|completedEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TNamed(completedDef.Name, [])))),
      VBuiltin(fun envV -> completedWith (envVarPairs envV))
      "|succeededEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TBool))),
      VBuiltin(fun envV -> succeededWith (envVarPairs envV))
      "|orFailedEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TStr, TFun(TSeq TStr, TUnit)))),
      VBuiltin(fun envV -> orFailedWith (envVarPairs envV))
      "|exitCodedEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TInt))),
      VBuiltin(fun envV -> exitCodedWith (envVarPairs envV)) ]
    @ bareEntries

let private showImpl: Value = VBuiltin(formatValue >> VStr)

let private printImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            writeLines items
            VUnit
        | (VStr _ | VInt _ | VFloat _ | VBool _) as scalar ->
            System.Console.WriteLine(scalarString "print argument" scalar)
            VUnit
        // unit prints NOTHING [D:exit-reifiers] — the !()/district
        // desugar's interior may be unit (| orFail)
        | VUnit -> VUnit
        | v -> unreachable $"the checker rejects 'print' on {formatValue v}")

// run p a IS cmd p a |> print — composed from the exact same impls, so
// every lifecycle guarantee (tree-kill, raise-at-force, stderr
// passthrough, streaming) is inherited, and byte-identity is by
// construction (pinned anyway).
let private runImpl: Value =
    VBuiltin(fun prog -> VBuiltin(fun argv -> apply printImpl (apply (apply cmdImpl prog) argv)))

// runEnv e p a IS cmdEnv e p a |> print — the run/cmd desugar
// relationship applied verbatim (byte-identity pinned).
let private runEnvImpl: Value =
    VBuiltin(fun env ->
        VBuiltin(fun prog -> VBuiltin(fun argv -> apply printImpl (apply (apply (apply cmdEnvImpl env) prog) argv))))

let commandCallable: Set<string> = Set [ "cd" ]

// desugar-internal aliases [D:desugar-capture]: every name a DESUGAR
// references, re-registered under a `|`-prefixed un-typeable key (the
// reifier precedent, second use) — the SAME scheme and value OBJECTS
// as the public members, so the sugar and the manual spelling cannot
// diverge (pinned by reference equality). A user constructor named
// Seq or a shadowed print no longer changes what a rewrite means.
let internalAliases: (string * Ty * Value) list =
    let m (modName: string) (field: string) =
        moduleTable
        |> List.find (fst >> (=) modName)
        |> snd
        |> List.find (fun (n, _, _) -> n = field)
        |> fun (_, ty, v) -> ty, v

    [ for key, modName, field in
          [ "|seqIter", "Seq", "iter"
            "|seqMap", "Seq", "map"
            "|seqForce", "Seq", "force"
            "|seqAppend", "Seq", "append"
            "|seqRange", "Seq", "range"
            "|seqItem", "Seq", "item"
            "|retryDefaults", "Retry", "defaults"
            "|pollDefaults", "Poll", "defaults" ] do
          let ty, v = m modName field
          key, ty, v ]

let bareAliasHomes: Map<string, string> =
    moduleTable
    |> List.filter (fun (m, _) -> m <> "Option" && m <> "Float")
    |> List.collect (fun (m, members) ->
        members
        |> List.choose (fun (n, _, _) ->
            if bareAliases.Contains n && n <> "length" then
                Some(n, m)
            else
                None))
    |> Map.ofList

// sortBy : Ord b => (a -> b) -> seq<a> -> seq<a> — the constraint that
// killed the runtime scalar-key rule (sentinel-ledger customer four).
let private sortByScheme: Scheme =
    { Forall = Set [ "a"; "b" ]
      Cs = Map [ "b", Set [ Cls.Ord ] ]
      Ty = TFun(TFun(TVar "a", TVar "b"), TFun(TSeq(TVar "a"), TSeq(TVar "a")))
      RowOrigins = Map.empty }

let typeEnv: TypeEnv =
    { Values =
        entries @ internalAliases
        |> List.map (fun (n, ty, _) -> n, generalize ty)
        |> Map.ofList
        |> Map.add "print" Check.printScheme
        |> Map.add "printerr" Check.printScheme
        // the arming desugar's un-shadowable print [D:desugar-capture]
        |> Map.add "|print" Check.printScheme
        |> Map.add "show" Check.showScheme
      Modules =
        moduleTable
        |> List.map (fun (m, members) -> m, members |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList)
        |> Map.ofList
        |> Map.change "Seq" (Option.map (Map.add "contains" Check.containsScheme))
        |> Map.change "Seq" (Option.map (Map.add "distinct" Check.distinctScheme))
        |> Map.change "Seq" (Option.map (Map.add "sortBy" sortByScheme))
        |> Map.change "Seq" (Option.map (Map.add "sortByDescending" sortByScheme))
      Types =
        Map
            [ fileRow.Name, Record fileRow
              changeDef.Name, Record changeDef
              completedDef.Name, Record completedDef
              groupDef.Name, Record groupDef
              envVarDef.Name, Record envVarDef ]
      ModuleTypes = Map.empty }

let typeEnvStrict: TypeEnv =
    { typeEnv with
        Values = bareAliasHomes |> Map.fold (fun vs name _ -> Map.remove name vs) typeEnv.Values }

let valueEnv: Env =
    let flat = entries |> List.map (fun (n, _, v) -> n, v)

    let mangled =
        moduleTable
        |> List.collect (fun (m, members) -> members |> List.map (fun (n, _, v) -> $"{m}.{n}", v))

    ("print", printImpl)
    :: ("printerr", printerrImpl)
    :: ("show", showImpl)
    :: ("|print", printImpl)
    :: (internalAliases |> List.map (fun (n, _, v) -> n, v))
    @ flat
    @ mangled
    |> Map.ofList
