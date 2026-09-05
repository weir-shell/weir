module Weir.Builtins

open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Weir.Types
open Weir.Eval

let fileRow: RecordDef =
    // the STATED surface [D:ls-truth] — what FileSystemInfo offers and
    // which parts are in lives in the DECISIONS row; symlink is OUT
    // with its reason (Windows semantics unverifiable off-matrix)
    { Name = "FileRow"
      Params = []
      Fields =
        // DECLARATION order is display order now [D:record-order] —
        // name leads (the ls-rider's ask), path trails (the widest
        // column reads best at the edge)
        [ "name", TStr
          // kind, not isDirectory [D:filerow]: a fact, not an answer —
          // and it extends (Symlink needed no new column). `type` is a
          // keyword; `kind` is the fallback the plan named
          "kind", TNamed("FileKind", [])
          // the one fact no File.* query can answer [D:filerow]
          "target", TNamed("Option", [ TStr ])
          "bytes", TSize
          // the file's OWN fact [D:instant]: replaced age (derived,
          // snapshotted per pull, stale after binding)
          "modified", TInstant
          "hidden", TBool
          "path", TStr ]
      Attrs = Map.empty
      Docs = Map.empty }

// the row's kind axis [D:filerow]: module names are load-bearing
// (`File`/`Dir` as cases would turn Dir.create into field access on a
// union), so the cases are Regular (POSIX's own term), Directory,
// Symlink
let fileKind: UnionDef =
    { Name = "FileKind"
      Params = []
      Cases = [ "Regular", None; "Directory", None; "Symlink", None ]
      Tag = None
      CaseWires = Map.empty
      OtherCase = None }

let seqFileRow = TSeq(TNamed(fileRow.Name, []))

/// values in DECLARATION ORDER; the keys come from the def, so a key
/// mismatch cannot be written and an arity slip throws here at the
/// construction site [D:record-keys]
let recordOf (def: RecordDef) (values: Value list) : Value =
    if List.length values <> List.length def.Fields then
        unreachable
            $"builtin record {def.Name}: {List.length values} values for {List.length def.Fields} declared fields"

    VRecord(def.Name, List.map2 (fun (name, _) v -> name, v) def.Fields values)

let file (name: string) (bytes: int64) (hidden: bool) : Value =
    // the fixture-friendly constructor: test rows are plain files at
    // the cwd, modified at the epoch — real rows come from lsRow below
    // (readOnly retired with the row's reshape [D:filerow]; hidden is
    // the fixture's filterable bool now)
    recordOf
        fileRow
        [ VStr name
          VUnion("Regular", None)
          VUnion("None", None)
          VSize bytes
          VInstant 0L
          VBool hidden
          VStr name ]

let private lsRow (info: FileSystemInfo) : Value =
    let isDir = info.Attributes.HasFlag FileAttributes.Directory

    let bytes =
        // a directory's "size" is a lie on every platform — 0 with the
        // flag is honest [D:ls-truth]
        match info with
        | :? FileInfo as f when not isDir -> f.Length
        | _ -> 0L

    let hidden =
        // one meaning across platforms: the dot-name (POSIX's whole
        // convention) OR the attribute (Windows's real bit)
        info.Name.StartsWith "." || info.Attributes.HasFlag FileAttributes.Hidden

    let kind =
        // LinkTarget is the honest symlink probe (net6+): non-null even
        // when the target dangles; the reparse-point attribute alone
        // also matches junctions, which is what we want on Windows
        if not (isNull info.LinkTarget) then "Symlink"
        elif isDir then "Directory"
        else "Regular"

    recordOf
        fileRow
        [ VStr info.Name
          VUnion(kind, None)
          (match info.LinkTarget with
           | null -> VUnion("None", None)
           | t -> VUnion("Some", Some(VStr t)))
          VSize bytes
          VInstant(System.DateTimeOffset(info.LastWriteTimeUtc, System.TimeSpan.Zero).ToUnixTimeMilliseconds())
          VBool hidden
          VStr info.FullName ]

let private realLs: Value =
    VSeq(
        Seq.delay (fun () ->
            // the WHOLE directory — files AND subdirectories (GetFiles
            // silently halved the listing for a month) [D:ls-truth]
            let cwd = Session.Cwd()

            let infos =
                try
                    DirectoryInfo(cwd).GetFileSystemInfos()
                with
                | :? System.UnauthorizedAccessException -> failwith $"ls: permission denied: {cwd}"
                | :? System.IO.IOException as e -> failwith $"ls: cannot access {cwd} — {e.Message}"

            // SORTED BY NAME, ordinal [D:ls-sort]: the third discovery
            // surface joins Dir.list/Path.glob's rule (F# string compare
            // is ordinal — case-sensitive, uppercase first, never the
            // locale; coreutils ls inherits LC_COLLATE, weir does not)
            infos |> Array.sortBy (fun i -> i.Name) |> Seq.map lsRow)
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

let private asString (v: Value) : string =
    match v with
    | VStr s -> s
    | v -> unreachable $"the checker rejects non-string command arguments: {formatValue v}"

let private argStrings (args: seq<Value>) : string list = args |> Seq.map asString |> List.ofSeq

let private intoImpl: Value =
    VBuiltin(fun c ->
        VBuiltin(fun sv ->
            match c, sv with
            | VStr cmdline, VSeq items ->
                let lines = items |> Seq.map asString
                // sh resolves on PATH like every other spawn — the /bin/sh
                // hardcode had no Windows answer (Git Bash puts sh on PATH)
                VSeq(
                    Proc.lines (Proc.resolveProg "sh") [ "-c"; cmdline ] (Some lines)
                    |> Seq.map VStr
                )
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
            // return what was STORED, so cd and pwd cannot disagree on shape
            VStr(Session.Cwd())
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
        | v -> unreachable $"the checker rejects 'force' on {formatValue v}")

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

                // lazy views over the capture buffer [D:capture-buffer]
                // — decode per pull, stable on re-enumeration
                recordOf completedDef [ VInt(int64 code); VSeq(stdout |> Seq.map VStr); VSeq(stderr |> Seq.map VStr) ]
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

                    recordOf completedDef [ VInt(int64 code); VSeq(out |> Seq.map VStr); VSeq(err |> Seq.map VStr) ]
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

// the cardinality ASSERTION [D:exactly-one]: head silently accepts a
// second element, so a wrong-arity command output passes quietly at
// the boundary where it is most likely. TWO distinct messages — a
// source that produced nothing and one that produced more are
// different bugs; collapsing them wastes the member. The more-case
// stops at the SECOND element (never a count): the source may be
// infinite, and enumerating it to report a number is the hang the
// lazy law forbids.
let private exactlyOneImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            use e = items.GetEnumerator()

            if not (e.MoveNext()) then
                failwith "exactlyOne: expected exactly one element, got none"

            let x = e.Current

            if e.MoveNext() then
                failwith "exactlyOne: expected exactly one element, got more"

            x
        | v -> unreachable $"the checker rejects 'exactlyOne' on {formatValue v}")

let private tryExactlyOneImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            use e = items.GetEnumerator()

            if not (e.MoveNext()) then
                vNone
            else
                let x = e.Current
                if e.MoveNext() then vNone else vSome x
        | v -> unreachable $"the checker rejects 'tryExactlyOne' on {formatValue v}")


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
    | VInstant x, VInstant y -> compare x y
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

// split at the FIRST occurrence, tail INTACT [D:split-once] — Rust's
// split_once shape (Go's Cut, Python's partition are the same
// correction): "at most one split" is a different operation from
// "split into pieces", and Str.split + a seq pattern silently misses
// when the tail contains the separator
let private splitOnceImpl: Value =
    VBuiltin(fun sep ->
        VBuiltin(fun subject ->
            match sep, subject with
            | VStr sep, VStr s ->
                if sep = "" then
                    failwith "splitOnce: the separator cannot be empty"
                else
                    match s.IndexOf sep with
                    | -1 -> failwith $"splitOnce: no \"{sep}\" in the input"
                    | i -> VTuple [ VStr(s.Substring(0, i)); VStr(s.Substring(i + sep.Length)) ]
            | _ -> unreachable "the checker rejects 'splitOnce' on these arguments"))

let private trySplitOnceImpl: Value =
    VBuiltin(fun sep ->
        VBuiltin(fun subject ->
            match sep, subject with
            | VStr sep, VStr s ->
                if sep = "" then
                    failwith "trySplitOnce: the separator cannot be empty"
                else
                    match s.IndexOf sep with
                    | -1 -> vNone
                    | i -> vSome (VTuple [ VStr(s.Substring(0, i)); VStr(s.Substring(i + sep.Length)) ])
            | _ -> unreachable "the checker rejects 'trySplitOnce' on these arguments"))

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
            | v -> unreachable $"the checker rejects 'defaultValue' on {formatValue v}"))

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

                            recordOf groupDef [ keyValue; VSeq(List.ofSeq group :> seq<Value>) ]))
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
// WEIR_LOG (trace|debug|info|warn|off), read ONCE at startup; it
// changes what is PRINTED, never what the script computes. There is
// deliberately NO Log.error: an error silenced by WEIR_LOG=off is the
// one message a user needs — unconditional messages are `printerr`,
// stopping is `fail`; `warn` is the TOP of the filterable range.

let logLevelNames = [ "trace"; "debug"; "info"; "warn"; "off" ]

let parseLogLevel (s: string) : Result<int, string> =
    // case-insensitive like every env-loaded enum (the SKILL rule:
    // env is the channel where DEBUG/Debug/debug all mean debug)
    let lowered = s.ToLowerInvariant()

    match logLevelNames |> List.tryFindIndex ((=) lowered) with
    | Some i -> Ok i
    | None -> Error $"WEIR_LOG={s}: unknown log level (one of trace|debug|info|warn|off)"

// default info (ruled): Log.info is useful without ceremony,
// debug/trace are opt-in, WEIR_LOG=off is genuine silence
let mutable private logThreshold = 2

/// the init file's one write [D:repl-init]: #session logLevel goes
/// through parseLogLevel first, so the two spellings cannot diverge
let setLogThreshold (i: int) : unit = logThreshold <- i

/// read WEIR_LOG once; Program calls this before dispatch so an
/// invalid value is a loud startup error, never a silent fallback
let initLogLevel () : Result<unit, string> =
    match System.Environment.GetEnvironmentVariable "WEIR_LOG" with
    | null
    | "" -> Ok()
    | v -> parseLogLevel v |> Result.map (fun i -> logThreshold <- i)

/// is DEBUG (or trace) enabled? Public so a diagnostic renderer can keep
/// parser-internal detail reachable by weir's own developers without putting
/// it in front of users — the alternative to deleting the capability.
let debugEnabled () : bool = logThreshold <= 1

let private logTint (code: string) (label: string) =
    if Types.Color.onStderr.Value then
        $"\x1b[{code}m{label}\x1b[0m"
    else
        label

let private logAt (level: int) (code: string) (label: string) (msg: string) =
    if level >= logThreshold then
        System.Console.Error.WriteLine(logTint code label + " " + msg)


let private parallelCeiling = 64

// the DEFAULT ceiling is a LADDER over nesting depth [D:parallel-ladder]:
// 64 / 8 / 1 — a fan-out inside a worker gets a smaller ceiling, and
// one nested TWICE runs serially (depth 2 and beyond hit the ladder's
// 1), so the product is <= 512 at any depth
// (two reasonable call sites in different files can no longer compose
// into a width nobody chose). Measured before the constants were
// fixed: the 64x8 shape runs 512 arms in one round at ~26MB peak RSS
// (.NET commits thread stacks lazily — the 512MB reserve-math worst
// case does not materialise). An EXPLICIT `With n` is the author's
// number and is never reduced — nesting pmapWith 64 in pmapWith 64
// stays possible and stays the author's decision.
let private parallelLadder = [| parallelCeiling; 8; 1 |]

let private defaultParallelCeiling (label: string) : int =
    let d = Session.parallelDepthNow ()
    let c = parallelLadder[min d (parallelLadder.Length - 1)]

    if d > 0 then
        // diagnostics, not a warning: reducing a ceiling changes
        // timing, never meaning (order and first-error-by-input-order
        // are structural) — this line exists for whoever asks why a
        // nested fan-out is slower than they expected
        logAt 1 "36" "debug" $"{label}: nested fan-out (depth {d}) — default ceiling reduced to {c}"

    c

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

    // arms are one level deeper [D:parallel-ladder]: set before
    // StartNew so the workers capture depth+1, restored after the join
    let depth = Session.parallelDepthNow ()
    Session.setParallelDepth (depth + 1)

    try
        let tasks =
            Array.init workers (fun _ -> Task.Factory.StartNew(worker, TaskCreationOptions.LongRunning))

        Task.WaitAll(tasks: Task[])
    finally
        Session.setParallelDepth depth

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
        failwith "pfirst: empty sequence — a race needs at least one arm; guard with Seq.isEmpty"

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

    // arms are one level deeper [D:parallel-ladder] — losers that
    // outlive the winner keep the deeper context, which is correct:
    // they are still arms
    let depth = Session.parallelDepthNow ()
    Session.setParallelDepth (depth + 1)

    try
        for _ in 1..workers do
            Task.Factory.StartNew(worker, TaskCreationOptions.LongRunning) |> ignore
    finally
        Session.setParallelDepth depth

    try
        outcome.Task.Result
    with :? System.AggregateException as ae when ae.InnerExceptions.Count = 1 ->
        raise ae.InnerExceptions[0]

let private pfirstImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> runRaceWith (defaultParallelCeiling "pfirst") f items
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
            | VSeq items -> VSeq(runParallelWith (defaultParallelCeiling "pmap") f items :> seq<Value>)
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
                runParallelWith (defaultParallelCeiling "piter") f items |> ignore
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

// weir owns its runtime messages [D:message-ownership]: FSharp.Core's
// text for these two is a composite template whose {0} is the generic
// "insufficient elements" sentence, so the raw form reads as a spliced
// fragment. Same wrapping the empty-seq family already has.
let private itemImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun s ->
            match n, s with
            | VInt i, VSeq items ->
                if i < 0L then
                    failwith $"item: negative index {i}"
                else
                    match items |> Seq.tryItem (int i) with
                    | Some v -> v
                    | None -> failwith $"item: no element at index {i}"
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
            | VInt i, VSeq items ->
                // LAZY still [D:message-ownership]: the count is checked as
                // the seq is walked, not by probing its length up front
                VSeq(
                    seq {
                        let mutable seen = 0L

                        for x in items do
                            if seen >= i then
                                yield x

                            seen <- seen + 1L

                        if seen < i then
                            failwith $"skip: fewer than {i} elements to skip (the seq had {seen})"
                    }
                )
            | _ -> unreachable "the checker rejects 'skip' on these arguments"))


// ---- the Seq-gaps cohort [D:seq-gaps] ------------------------------

// lazy, F#'s collect (flatMap elsewhere): the reservation paying out
let private collectImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    items
                    |> Seq.collect (fun x ->
                        match apply f x with
                        | VSeq inner -> inner
                        | v -> unreachable $"the checker guarantees a seq-yielding mapper, got {formatValue v}")
                )
            | v -> unreachable $"the checker rejects 'collect' on {formatValue v}"))

let private concatImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            VSeq(
                items
                |> Seq.collect (fun x ->
                    match x with
                    | VSeq inner -> inner
                    | v -> unreachable $"the checker rejects concatenating {formatValue v}")
            )
        | v -> unreachable $"the checker rejects 'concat' on {formatValue v}")

// find/tryFind: the X/tryX pair completed — X asserts, tryX asks
let private findImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                match items |> Seq.tryFind (fun x -> apply pred x = VBool true) with
                | Some x -> x
                | None -> failwith "find: no matching element"
            | v -> unreachable $"the checker rejects 'find' on {formatValue v}"))

let private indexedImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items -> VSeq(items |> Seq.mapi (fun i x -> VTuple [ VInt(int64 i); x ]))
        | v -> unreachable $"the checker rejects 'indexed' on {formatValue v}")

// FORCING: reversal needs the whole input (named in when-do-I-force)
let private revImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items -> VSeq(Seq.delay (fun () -> items |> List.ofSeq |> List.rev :> seq<Value>))
        | v -> unreachable $"the checker rejects 'rev' on {formatValue v}")

let private chunkImpl: Value =
    VBuiltin(fun nV ->
        VBuiltin(fun s ->
            match nV, s with
            | VInt n, VSeq items ->
                if n <= 0L then
                    failwith $"chunkBySize: the chunk size must be positive; got {n}"
                else
                    VSeq(items |> Seq.chunkBySize (int n) |> Seq.map (fun arr -> VSeq(arr :> seq<Value>)))
            | v, _ -> unreachable $"the checker rejects 'chunkBySize' on {formatValue v}"))

let private takeWhileImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(items |> Seq.takeWhile (fun x -> apply pred x = VBool true))
            | v -> unreachable $"the checker rejects 'takeWhile' on {formatValue v}"))

let private skipWhileImpl: Value =
    VBuiltin(fun pred ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(items |> Seq.skipWhile (fun x -> apply pred x = VBool true))
            | v -> unreachable $"the checker rejects 'skipWhile' on {formatValue v}"))

// counts by projected key, first-seen key order; forces on first pull
let private countByImpl: Value =
    VBuiltin(fun keyf ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                VSeq(
                    Seq.delay (fun () ->
                        items
                        |> Seq.countBy (apply keyf)
                        |> Seq.map (fun (k, n) -> VTuple [ k; VInt(int64 n) ]))
                )
            | v -> unreachable $"the checker rejects 'countBy' on {formatValue v}"))

let private distinctByImpl: Value =
    VBuiltin(fun keyf ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(items |> Seq.distinctBy (apply keyf))
            | v -> unreachable $"the checker rejects 'distinctBy' on {formatValue v}"))

// fold without a seed: the first element is the accumulator (raises on
// empty — the head message shape)
let private reduceImpl: Value =
    VBuiltin(fun folder ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                let mutable acc = ValueNone

                for x in items do
                    acc <-
                        match acc with
                        | ValueNone -> ValueSome x
                        | ValueSome a -> ValueSome(apply (apply folder a) x)

                match acc with
                | ValueSome a -> a
                | ValueNone -> failwith "reduce: empty sequence"
            | v -> unreachable $"the checker rejects 'reduce' on {formatValue v}"))

// fold with intermediates, INITIAL STATE FIRST (F# semantics); lazy
let private scanImpl: Value =
    VBuiltin(fun folder ->
        VBuiltin(fun init ->
            VBuiltin(fun s ->
                match s with
                | VSeq items -> VSeq(items |> Seq.scan (fun acc x -> apply (apply folder acc) x) init)
                | v -> unreachable $"the checker rejects 'scan' on {formatValue v}")))

let private tryPickImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                items
                |> Seq.tryPick (fun x ->
                    match apply f x with
                    | VUnion("Some", Some v) -> Some v
                    | VUnion("None", None) -> None
                    | v -> unreachable $"the checker guarantees an Option picker, got {formatValue v}")
                |> function
                    | Some v -> VUnion("Some", Some v)
                    | None -> VUnion("None", None)
            | v -> unreachable $"the checker rejects 'tryPick' on {formatValue v}"))

let private pickImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                items
                |> Seq.tryPick (fun x ->
                    match apply f x with
                    | VUnion("Some", Some v) -> Some v
                    | VUnion("None", None) -> None
                    | v -> unreachable $"the checker guarantees an Option picker, got {formatValue v}")
                |> function
                    | Some v -> v
                    | None -> failwith "pick: no matching element"
            | v -> unreachable $"the checker rejects 'pick' on {formatValue v}"))

// set difference, F#'s argument order: the EXCLUSIONS first, the
// source last (data-last holds) — the exclusion set materializes on
// the first pull, the source streams
let private exceptImpl: Value =
    VBuiltin(fun excl ->
        VBuiltin(fun s ->
            match excl, s with
            | VSeq ex, VSeq items -> VSeq(items |> Seq.except ex)
            | v, _ -> unreachable $"the checker rejects 'except' on {formatValue v}"))

let private replicateImpl: Value =
    VBuiltin(fun nV ->
        VBuiltin(fun x ->
            match nV with
            | VInt n ->
                if n < 0L then
                    failwith $"replicate: the count must be non-negative; got {n}"
                else
                    VSeq(Seq.replicate (int n) x)
            | v -> unreachable $"the checker rejects 'replicate' on {formatValue v}"))

// max/min and the By twins: Ord-constrained, raise on empty (the head
// message shape); one strict pass, no sort
let private extremumImpl (name: string) (better: int -> bool) : Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            let mutable best = ValueNone

            for x in items do
                best <-
                    match best with
                    | ValueNone -> ValueSome x
                    | ValueSome b ->
                        if better (scalarCompare name x b) then
                            ValueSome x
                        else
                            ValueSome b

            match best with
            | ValueSome b -> b
            | ValueNone -> failwith $"{name}: empty sequence"
        | v -> unreachable $"the checker rejects '{name}' on {formatValue v}")

let private extremumByImpl (name: string) (better: int -> bool) : Value =
    VBuiltin(fun keyf ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                let mutable best = ValueNone

                for x in items do
                    let k = apply keyf x

                    best <-
                        match best with
                        | ValueNone -> ValueSome(x, k)
                        | ValueSome(bx, bk) ->
                            if better (scalarCompare name k bk) then
                                ValueSome(x, k)
                            else
                                ValueSome(bx, bk)

                match best with
                | ValueSome(bx, _) -> bx
                | ValueNone -> failwith $"{name}: empty sequence"
            | v -> unreachable $"the checker rejects '{name}' on {formatValue v}"))

// key-less sort: Ord on the elements themselves; forces on first pull
let private sortPlainImpl (name: string) (flip: bool) : Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            VSeq(
                Seq.delay (fun () ->
                    items
                    |> Seq.sortWith (fun a b ->
                        if flip then
                            scalarCompare name b a
                        else
                            scalarCompare name a b))
            )
        | v -> unreachable $"the checker rejects '{name}' on {formatValue v}")

// the mean of ints IS a float — what floats were added for; empty
// raises (absence is Option's job, and 0 would be a guess)
let private averageImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            let mutable total = 0L
            let mutable count = 0L

            for x in items do
                match x with
                | VInt n ->
                    (try
                        total <- Checked.(+) total n
                     with :? System.OverflowException ->
                         failwith "integer overflow in average")

                    count <- count + 1L
                | v -> unreachable $"the checker rejects averaging {formatValue v}"

            if count = 0L then
                failwith "average: empty sequence"
            else
                VFloat(float total / float count)
        | v -> unreachable $"the checker rejects 'average' on {formatValue v}")

// the per-type sums and means [D:seq-gaps]: Seq.sum stays seq<int> ->
// int; Float/Size/Duration own theirs (module-qualified, the
// Duration.sleep precedent) — a general numeric sum needs a class weir
// does not have
let private typedSumImpl (name: string) (get: Value -> int64) (mk: int64 -> Value) : Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            let mutable total = 0L

            for x in items do
                try
                    total <- Checked.(+) total (get x)
                with :? System.OverflowException ->
                    failwith $"{name}: overflow"

            mk total
        | v -> unreachable $"the checker rejects '{name}' on {formatValue v}")

let private typedAverageImpl (name: string) (get: Value -> int64) (mk: int64 -> Value) : Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            let mutable total = 0L
            let mutable count = 0L

            for x in items do
                (try
                    total <- Checked.(+) total (get x)
                 with :? System.OverflowException ->
                     failwith $"{name}: overflow")

                count <- count + 1L

            if count = 0L then
                failwith $"{name}: empty sequence"
            else
                mk (total / count)
        | v -> unreachable $"the checker rejects '{name}' on {formatValue v}")

let private seqMembers: (string * Ty * Value) list =
    [ "map", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tB)), mapImpl
      "where", TFun(TFun(tA, TBool), TFun(TSeq tA, TSeq tA)), whereImpl
      "take", TFun(TInt, TFun(TSeq tA, TSeq tA)), truncateImpl
      "head", TFun(TSeq tA, tA), headImpl
      "exactlyOne", TFun(TSeq tA, tA), exactlyOneImpl
      "tryExactlyOne", TFun(TSeq tA, TNamed("Option", [ tA ])), tryExactlyOneImpl
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
      "groupBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq(TNamed("Group", [ tB; tA ])))), groupByImpl
      // ---- the Seq-gaps cohort [D:seq-gaps] ----------------------
      "collect", TFun(TFun(tA, TSeq tB), TFun(TSeq tA, TSeq tB)), collectImpl
      "concat", TFun(TSeq(TSeq tA), TSeq tA), concatImpl
      "find", TFun(TFun(tA, TBool), TFun(TSeq tA, tA)), findImpl
      "indexed", TFun(TSeq tA, TSeq(TTuple [ TInt; tA ])), indexedImpl
      "rev", TFun(TSeq tA, TSeq tA), revImpl
      "chunkBySize", TFun(TInt, TFun(TSeq tA, TSeq(TSeq tA))), chunkImpl
      "takeWhile", TFun(TFun(tA, TBool), TFun(TSeq tA, TSeq tA)), takeWhileImpl
      "skipWhile", TFun(TFun(tA, TBool), TFun(TSeq tA, TSeq tA)), skipWhileImpl
      "countBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq(TTuple [ tB; TInt ]))), countByImpl
      "distinctBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tA)), distinctByImpl
      "reduce", TFun(TFun(tA, TFun(tA, tA)), TFun(TSeq tA, tA)), reduceImpl
      "scan", TFun(TFun(tA, TFun(tB, tA)), TFun(tA, TFun(TSeq tB, TSeq tA))), scanImpl
      "tryPick", TFun(TFun(tA, TNamed("Option", [ tB ])), TFun(TSeq tA, TNamed("Option", [ tB ]))), tryPickImpl
      "pick", TFun(TFun(tA, TNamed("Option", [ tB ])), TFun(TSeq tA, tB)), pickImpl
      "except", TFun(TSeq tA, TFun(TSeq tA, TSeq tA)), exceptImpl
      "replicate", TFun(TInt, TFun(tA, TSeq tA)), replicateImpl
      "max", TFun(TSeq tA, tA), extremumImpl "max" (fun c -> c > 0)
      "min", TFun(TSeq tA, tA), extremumImpl "min" (fun c -> c < 0)
      "maxBy", TFun(TFun(tA, tB), TFun(TSeq tA, tA)), extremumByImpl "maxBy" (fun c -> c > 0)
      "minBy", TFun(TFun(tA, tB), TFun(TSeq tA, tA)), extremumByImpl "minBy" (fun c -> c < 0)
      "sort", TFun(TSeq tA, TSeq tA), sortPlainImpl "sort" false
      "sortDescending", TFun(TSeq tA, TSeq tA), sortPlainImpl "sortDescending" true
      "average", TFun(seqInt, TFloat), averageImpl ]

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

// bytes -> text is the SAME gate fromBase64 wears [D:encoding-law]:
// strict UTF-8, and NUL is non-text (the byte the binary detector
// keys on, and the one that truncates at every C boundary)
let private utf8TextOf (name: string) (b: byte[]) : Result<string, string> =
    if System.Array.IndexOf(b, 0uy) >= 0 then
        Error $"{name}: the bytes are not text (they contain NUL — keep them Bytes, or Bytes.toBase64 for a text form)"
    else
        try
            Ok(utf8Strict.GetString b)
        with _ ->
            Error $"{name}: the bytes are not text (not valid UTF-8)"

let private fromBase64Text (name: string) (s: string) : Result<string, string> =
    let bytes =
        try
            Ok(base64Bytes s)
        with _ ->
            Error $"{name}: invalid base64: \"{s}\""

    match bytes with
    | Error e -> Error e
    | Ok(b: byte[]) ->
        // NUL is non-text too [D:encoding-law]: it is valid UTF-8, but
        // it is the byte the binary detector keys on [D:binary-echo],
        // and a NUL-bearing string silently truncates at every C
        // boundary (argv, env) — binary payloads wait for BYTES
        if System.Array.IndexOf(b, 0uy) >= 0 then
            Error
                $"{name}: the decoded content is not text (it contains NUL bytes — binary payloads need the BYTES type, not string)"
        else
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
      "splitOnce", TFun(TStr, TFun(TStr, TTuple [ TStr; TStr ])), splitOnceImpl
      "trySplitOnce", TFun(TStr, TFun(TStr, TNamed("Option", [ TTuple [ TStr; TStr ] ]))), trySplitOnceImpl
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
      "toUtf8",
      TFun(TStr, TBytes),
      VBuiltin(fun v ->
          match v with
          | VStr s -> VBytes(System.Text.Encoding.UTF8.GetBytes s)
          | v -> unreachable $"the checker rejects 'Str.toUtf8' on {formatValue v}")
      "fromUtf8",
      TFun(TBytes, TStr),
      VBuiltin(fun v ->
          match v with
          | VBytes b ->
              (match utf8TextOf "Str.fromUtf8" b with
               | Ok t -> VStr t
               | Error e -> failwith e)
          | v -> unreachable $"the checker rejects 'Str.fromUtf8' on {formatValue v}")
      "tryFromUtf8",
      TFun(TBytes, TNamed("Option", [ TStr ])),
      VBuiltin(fun v ->
          match v with
          | VBytes b ->
              (match utf8TextOf "Str.tryFromUtf8" b with
               | Ok t -> VUnion("Some", Some(VStr t))
               | Error _ -> VUnion("None", None))
          | v -> unreachable $"the checker rejects 'Str.tryFromUtf8' on {formatValue v}")
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

/// `Path.under` [D:path-under] — the CONFINING join. `Path.combine` keeps BCL
/// semantics (an absolute second argument WINS; `..` is not normalised) and is
/// the primitive for paths you control; `under` is the one to reach for with
/// input you do not. PURELY TEXTUAL by design: it confines the PATH, never the
/// resolved target, so a symlink inside the base pointing out is textually
/// under and is NOT confined. Following links would mean touching the disk,
/// which makes the check impure, racy (TOCTOU) and dependent on the path
/// existing — the same register as `Secret` being a rendering marker.
let private absoluteShaped (p: string) : bool =
    // refused on EVERY platform, not only where the host OS agrees: a script
    // must confine identically on Linux and Windows, and refusing the SHAPE is
    // the safe direction. Covers /x and \x, a drive root or drive-RELATIVE
    // `C:x` (the BCL treats both as rooted), and UNC `\\server\share`.
    p.StartsWith "/"
    || p.StartsWith "\\"
    || (p.Length >= 2 && System.Char.IsLetter p[0] && p[1] = ':')

let private pathUnderImpl: Value =
    VBuiltin(fun a ->
        VBuiltin(fun b ->
            match a, b with
            | VStr basePath, VStr name ->
                // the base is normalised FIRST, and a RELATIVE base resolves
                // against the session cwd at call time — Path.glob's
                // resolve-at-use rule, and the cwd every runtime surface reads
                let root = Path.TrimEndingDirectorySeparator(Session.resolve basePath)

                let escape () : Value =
                    failwith
                        $"Path.under: '{name}' escapes '{root}' — under confines a path to its base; Path.combine is the unconfined join"

                if absoluteShaped name then
                    escape ()
                else
                    // normalise THEN confine: rejecting literal `..` segments is
                    // neither sufficient (separators and encodings get past a
                    // textual scan) nor necessary (`a/b/../c` is legitimately
                    // inside). GetFullPath is lexical — it never touches disk.
                    let joined = Path.GetFullPath(Path.Combine(root, name))
                    let sep = string Path.DirectorySeparatorChar
                    let prefix = if root.EndsWith sep then root else root + sep

                    // SEGMENT-WISE, not prefix-string-wise: `/safe/uploads-evil`
                    // starts with `/safe/uploads` as a string and is not under
                    // it — the classic bug in every hand-rolled version
                    if joined = root || joined.StartsWith prefix then
                        VStr joined
                    else
                        escape ()
            | _ -> unreachable "the checker rejects 'Path.under' on these arguments"))

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
      "under", TFun(TStr, TFun(TStr, TStr)), pathUnderImpl
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

                    // the File-family guards, in Env.fromFile's own words —
                    // a missing .env leaked FileNotFoundException's text
                    // until the wider sweep [D:transport-words]
                    if Directory.Exists resolved then
                        failwith $"Env.fromFile: {resolved} is a directory"

                    if not (File.Exists resolved) then
                        failwith $"Env.fromFile: no such file: {resolved}"

                    let lines =
                        try
                            File.ReadAllLines resolved
                        with
                        | :? System.UnauthorizedAccessException ->
                            failwith $"Env.fromFile: permission denied: {resolved}"
                        | :? System.IO.IOException as e ->
                            failwith $"Env.fromFile: cannot access {resolved} — {e.Message}"

                    lines
                    |> Seq.indexed
                    |> Seq.choose (fun (i, raw) -> parseDotenvLine path (i + 1) raw)
                    |> Seq.map (fun (k, value) -> recordOf envVarDef [ VStr k; VStr value ]))
            )
        | v -> unreachable $"the checker rejects 'Env.fromFile' on {formatValue v}")

// Env.pair / Env.ofPairs [D:seq-fold] — inline-env construction
// for a known nominal type (NOT an anonymous-records case).
let private envPairImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun v ->
            match n, v with
            | VStr n, VStr v -> recordOf envVarDef [ VStr n; VStr v ]
            | _ -> unreachable "the checker rejects 'Env.pair' on these arguments"))

let private envOfPairsImpl: Value =
    VBuiltin(fun sv ->
        match sv with
        | VSeq items ->
            VSeq(
                items
                |> Seq.map (fun p ->
                    match p with
                    | VTuple [ VStr n; VStr v ] -> recordOf envVarDef [ VStr n; VStr v ]
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
              // hashtable order is noise — the sweep's one sibling of
              // the ls gap [D:ls-sort]: sorted by name, the same ordinal
              // rule. (fromFile/ofPairs stay in GIVEN order — there the
              // order is the author's information, the YMap argument.)
              System.Environment.GetEnvironmentVariables()
              |> Seq.cast<System.Collections.DictionaryEntry>
              |> Seq.sortBy (fun e -> string e.Key)
              |> Seq.map (fun e -> recordOf envVarDef [ VStr(string e.Key); VStr(string e.Value) ]))
      )
      "fromFile", TFun(TStr, TSeq(TNamed(envVarDef.Name, []))), envFromFileImpl ]

// the read-side guards [D:sized-findings]: the delete side pre-checked
// with weir-shaped messages while the read side leaked raw .NET
// exceptions (FileNotFoundException's words, not weir's) — the same
// split the floats session refused to pin. Pre-check the common
// failures with the DELETE side's shapes (the path named, no second
// family); wrap the residual (permissions, exotic IO) so no raw .NET
// message reaches a user. Encoding never throws here — ReadAllLines
// substitutes replacement chars, so is-a-directory / not-found /
// permission are the whole enumerable surface.
let private readGuard (op: string) (r: string) : unit =
    if System.IO.Directory.Exists r then
        failwith $"{op}: {r} is a directory"
    elif not (File.Exists r) then
        failwith $"{op}: no such file: {r}"

let private writeGuard (op: string) (r: string) : unit =
    if System.IO.Directory.Exists r then
        failwith $"{op}: {r} is a directory"
    else
        let parent = System.IO.Path.GetDirectoryName r

        if parent <> "" && not (System.IO.Directory.Exists parent) then
            failwith $"{op}: no such directory: {parent}"

let private ioGuarded (op: string) (r: string) (f: unit -> 'a) : 'a =
    try
        f ()
    with
    | :? System.UnauthorizedAccessException -> failwith $"{op}: permission denied: {r}"
    | :? System.IO.IOException as e -> failwith $"{op}: cannot access {r} — {e.Message}"

let private fileMembers: (string * Ty * Value) list =
    [ "read",
      TFun(TStr, TSeq TStr),
      VBuiltin(fun v ->
          match v with
          | VStr path ->
              let r = Session.resolve path
              readGuard "File.read" r
              VSeq(ioGuarded "File.read" r (fun () -> File.ReadAllLines r) |> Seq.map VStr)
          | v -> unreachable $"the checker rejects 'File.read' on {formatValue v}")
      // a token in a file is a real pattern [D:secret]: a mounted k8s /
      // docker secret IS a file. ONE member (a family would be parked):
      // the whole content is the secret, trailing newlines trimmed (the
      // tooling convention — `echo tok > f` adds one, k8s does not)
      "readSecret",
      TFun(TStr, TSecret),
      VBuiltin(fun v ->
          match v with
          | VStr path ->
              let r = Session.resolve path
              readGuard "File.readSecret" r
              VSecret((ioGuarded "File.readSecret" r (fun () -> File.ReadAllText r)).TrimEnd('\n', '\r'))
          | v -> unreachable $"the checker rejects 'File.readSecret' on {formatValue v}")
      "write",
      TFun(TStr, TFun(TSeq TStr, TUnit)),
      VBuiltin(fun pathV ->
          VBuiltin(fun linesV ->
              match pathV, linesV with
              | VStr path, VSeq lines ->
                  let r = Session.resolve path
                  writeGuard "File.write" r

                  ioGuarded "File.write" r (fun () ->
                      // LF bytes on every platform [D:lf-output] — a
                      // written file is data (hashes, sigs, diffs)
                      use w = new StreamWriter(r, false)
                      w.NewLine <- "\n"

                      for l in lines do
                          w.WriteLine(asString l))

                  VUnit
              | _ -> unreachable "the checker rejects 'File.write' on these arguments"))
      "append",
      TFun(TStr, TFun(TSeq TStr, TUnit)),
      VBuiltin(fun pathV ->
          VBuiltin(fun linesV ->
              match pathV, linesV with
              | VStr path, VSeq lines ->
                  let r = Session.resolve path
                  writeGuard "File.append" r

                  ioGuarded "File.append" r (fun () ->
                      use w = new StreamWriter(r, true)
                      w.NewLine <- "\n"

                      for l in lines do
                          w.WriteLine(asString l))

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
                let s, d = Session.resolve src, Session.resolve dst
                ioGuarded name s (fun () -> f s d)
                VUnit
            | _ -> unreachable $"the checker rejects '{name}' on these arguments"))

let private fsMoreFileMembers: (string * Ty * Value) list =
    [ "mode",
      // the narrow fact stays a QUERY, not a column [D:filerow]:
      // rwxr-xr-x shaped; None on Windows — the platform limit stated,
      // never invented [D:ls-truth]. The receipt: the 0600 check that
      // should precede File.readSecret
      TFun(TStr, TNamed("Option", [ TStr ])),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.File.Exists r || System.IO.Directory.Exists r || FileInfo(r).Exists) then
                  failwith $"File.mode: no such path: {r}"

              try
                  let m = System.IO.File.GetUnixFileMode r

                  let bit (flag: System.IO.UnixFileMode) (ch: string) = if m.HasFlag flag then ch else "-"

                  let text =
                      bit System.IO.UnixFileMode.UserRead "r"
                      + bit System.IO.UnixFileMode.UserWrite "w"
                      + bit System.IO.UnixFileMode.UserExecute "x"
                      + bit System.IO.UnixFileMode.GroupRead "r"
                      + bit System.IO.UnixFileMode.GroupWrite "w"
                      + bit System.IO.UnixFileMode.GroupExecute "x"
                      + bit System.IO.UnixFileMode.OtherRead "r"
                      + bit System.IO.UnixFileMode.OtherWrite "w"
                      + bit System.IO.UnixFileMode.OtherExecute "x"

                  VUnion("Some", Some(VStr text))
              with
              | :? System.PlatformNotSupportedException -> VUnion("None", None)
              | :? System.IO.FileNotFoundException
              | :? System.IO.DirectoryNotFoundException ->
                  // the READ follows; existence does not
                  // [D:mode-existence]: a path with a row (File.stat/ls
                  // describe the LINK) fails here for the honest reason,
                  // never as absent
                  if isNull (FileInfo(r).LinkTarget) then
                      failwith $"File.mode: no such path: {r}"
                  else
                      failwith $"File.mode: dangling symlink: {r} — no target to read a mode from"
          | v -> unreachable $"the checker rejects 'File.mode' on {formatValue v}")
      "readBytes",
      // the byte-faithful read [D:bytes]: File.read decodes leniently
      // and line-splits; this one does neither
      TFun(TStr, TBytes),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p
              readGuard "File.readBytes" r
              VBytes(ioGuarded "File.readBytes" r (fun () -> System.IO.File.ReadAllBytes r))
          | v -> unreachable $"the checker rejects 'File.readBytes' on {formatValue v}")
      "writeBytes",
      TFun(TStr, TFun(TBytes, TUnit)),
      VBuiltin(fun pathV ->
          VBuiltin(fun bytesV ->
              match pathV, bytesV with
              | VStr path, VBytes b ->
                  let r = Session.resolve path
                  writeGuard "File.writeBytes" r
                  ioGuarded "File.writeBytes" r (fun () -> System.IO.File.WriteAllBytes(r, b))
                  VUnit
              | _ -> unreachable "the checker rejects 'File.writeBytes' on these arguments"))
      "sha256",
      // STREAMS internally [D:bytes] — the value type is bounded, the
      // implementation is not required to materialise; the install
      // story hashes files bigger than a value should be
      TFun(TStr, TStr),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p
              readGuard "File.sha256" r

              VStr(
                  ioGuarded "File.sha256" r (fun () ->
                      use fs = System.IO.File.OpenRead r

                      System.Security.Cryptography.SHA256.HashData fs
                      |> Array.map (fun x -> x.ToString "x2")
                      |> String.concat "")
              )
          | v -> unreachable $"the checker rejects 'File.sha256' on {formatValue v}")
      "delete",
      TFun(TStr, TUnit),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.File.Exists r) then
                  failwith $"File.delete: no such file: {r}"

              ioGuarded "File.delete" r (fun () -> System.IO.File.Delete r)
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
          | v -> unreachable $"the checker rejects 'File.size' on {formatValue v}")
      "stat",
      // the bridge from paths to rows [D:file-stat]: ls's OWN
      // constructor over one resolved path, so the two producers cannot
      // diverge. Describes the LINK, not its target (the ls agreement);
      // raises when absent — a dangling symlink is a row, not an absence
      TFun(TStr, TNamed(fileRow.Name, [])),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              // FileInfo.Exists is the path's OWN fact (true for a
              // dangling link); a directory — or a link to one — takes
              // the DirectoryInfo shape, as the enumeration does
              let fi = FileInfo r

              if fi.Exists then
                  lsRow fi
              else
                  let di = DirectoryInfo r

                  if di.Exists then
                      lsRow di
                  else
                      failwith $"File.stat: no such path: {r}"
          | v -> unreachable $"the checker rejects 'File.stat' on {formatValue v}") ]

let private dirMembers: (string * Ty * Value) list =
    [ "create",
      TFun(TStr, TUnit),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p
              // mkdir -p: parents created, existing = the post-condition
              ioGuarded "Dir.create" r (fun () -> System.IO.Directory.CreateDirectory r |> ignore)
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

              ioGuarded "Dir.delete" r (fun () ->
                  if System.IO.Directory.EnumerateFileSystemEntries r |> Seq.isEmpty |> not then
                      failwith $"Dir.delete: not empty: {r} — Dir.deleteAll removes a tree"

                  System.IO.Directory.Delete r)

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

              ioGuarded "Dir.deleteAll" r (fun () -> System.IO.Directory.Delete(r, true))
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
                  ioGuarded "Dir.list" r (fun () ->
                      System.IO.Directory.EnumerateFileSystemEntries r |> Seq.sort |> List.ofSeq)
                  |> Seq.map VStr
              )
          | v -> unreachable $"the checker rejects 'Dir.list' on {formatValue v}")
      "stat",
      TFun(TStr, seqFileRow),
      VBuiltin(fun v ->
          match v with
          | VStr p ->
              let r = Session.resolve p

              if not (System.IO.Directory.Exists r) then
                  failwith $"Dir.stat: no such directory: {r}"

              // the ROWS form of Dir.list [D:dir-stat]: ls's own
              // enumeration and row constructor over the named
              // directory, so the discovery surfaces cannot diverge;
              // EAGER like list (a listing is bounded)
              VSeq(
                  ioGuarded "Dir.stat" r (fun () ->
                      DirectoryInfo(r).GetFileSystemInfos()
                      |> Array.sortBy (fun i -> i.Name)
                      |> Array.map lsRow
                      |> List.ofArray)
              )
          | v -> unreachable $"the checker rejects 'Dir.stat' on {formatValue v}")
      "move",
      TFun(TStr, TFun(TStr, TUnit)),
      fsStr2 "Dir.move" (fun src dst ->
          if not (System.IO.Directory.Exists src) then
              failwith $"Dir.move: no such directory: {src}"

          if System.IO.File.Exists dst || System.IO.Directory.Exists dst then
              failwith $"Dir.move: destination exists: {dst}"

          System.IO.Directory.Move(src, dst))
      // copying a directory MEANS copying its contents — there is no
      // non-recursive reading, so the delete/deleteAll naming split does
      // not repeat here (no Dir.copyAll) [D:sized-findings]. The family's
      // overwrite rule unchanged: refuse an existing destination;
      // Dir.deleteAll is the deliberate replace spelling.
      "copy",
      TFun(TStr, TFun(TStr, TUnit)),
      fsStr2 "Dir.copy" (fun src dst ->
          if not (System.IO.Directory.Exists src) then
              failwith $"Dir.copy: no such directory: {src}"

          if System.IO.File.Exists dst || System.IO.Directory.Exists dst then
              failwith $"Dir.copy: destination exists: {dst}"

          let rec go (s: string) (d: string) =
              System.IO.Directory.CreateDirectory d |> ignore

              for f in System.IO.Directory.EnumerateFiles s do
                  System.IO.File.Copy(f, System.IO.Path.Combine(d, System.IO.Path.GetFileName f))

              for sub in System.IO.Directory.EnumerateDirectories s do
                  go sub (System.IO.Path.Combine(d, System.IO.Path.GetFileName sub))

          go src dst) ]

// ---- Float [D:floats]: finite-only; no implicit widening -----------
let private floatFn (name: string) (f: float -> Value) : Value =
    VBuiltin(fun v ->
        match v with
        | VFloat x -> f x
        | v -> unreachable $"the checker rejects 'Float.{name}' on {formatValue v}")

// the X.parse/X.tryParse pair builder [D:maintenance-2]: three families
// (Float, Size, Duration) each spelled this 18-line pair with drift
// (some used the vSome/vNone helpers, some VUnion literally) — the
// dedupe re-run collapsed them onto one shape. parse RAISES with
// "{label}.parse: {e}"; tryParse wraps Option. (The toInt/fromBase64
// pairs stay separate: their message shapes differ.)
let private parsePairImpl (label: string) (parser: string -> Result<'a, string>) (ctor: 'a -> Value) =
    let parseImpl =
        VBuiltin(fun v ->
            match v with
            | VStr s ->
                (match parser s with
                 | Ok x -> ctor x
                 | Error e -> failwith $"{label}.parse: {e}")
            | v -> unreachable $"the checker rejects '{label}.parse' on {formatValue v}")

    let tryImpl =
        VBuiltin(fun v ->
            match v with
            | VStr s ->
                (match parser s with
                 | Ok x -> VUnion("Some", Some(ctor x))
                 | Error _ -> VUnion("None", None))
            | v -> unreachable $"the checker rejects '{label}.tryParse' on {formatValue v}")

    parseImpl, tryImpl

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
      "parse", TFun(TStr, TFloat), fst (parsePairImpl "Float" parseFloat VFloat)
      "tryParse", TFun(TStr, TNamed("Option", [ TFloat ])), snd (parsePairImpl "Float" parseFloat VFloat)
      // the per-type sum/mean [D:seq-gaps] — Seq.sum stays seq<int>;
      // each numeric type owns its own (no numeric class in weir)
      "sum",
      TFun(TSeq TFloat, TFloat),
      VBuiltin(fun s ->
          match s with
          | VSeq items ->
              let total =
                  items
                  |> Seq.fold
                      (fun acc v ->
                          match v with
                          | VFloat f -> acc + f
                          | v -> unreachable $"the checker rejects summing {formatValue v}")
                      0.0

              if System.Double.IsFinite total then
                  VFloat total
              else
                  failwith "Float.sum: the sum is not finite"
          | v -> unreachable $"the checker rejects 'Float.sum' on {formatValue v}")
      "average",
      TFun(TSeq TFloat, TFloat),
      VBuiltin(fun s ->
          match s with
          | VSeq items ->
              let mutable total = 0.0
              let mutable count = 0L

              for x in items do
                  match x with
                  | VFloat f ->
                      total <- total + f
                      count <- count + 1L
                  | v -> unreachable $"the checker rejects averaging {formatValue v}"

              if count = 0L then
                  failwith "Float.average: empty sequence"
              elif System.Double.IsFinite(total / float count) then
                  VFloat(total / float count)
              else
                  failwith "Float.average: the mean is not finite"
          | v -> unreachable $"the checker rejects 'Float.average' on {formatValue v}") ]

// ---- Size [D:size]: integer bytes; decimals only in text -----------
// the non-text value's members [D:bytes] — the smallest useful cut:
// every member answers a receipt (F4's closed base64 route, the NUL
// route through File.read, hashing for the install story). Bounded and
// in-memory by the capture law; unbounded data streams to a sink.
let private bytesMembers: (string * Ty * Value) list =
    [ "fromBase64",
      TFun(TStr, TBytes),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              (try
                  VBytes(base64Bytes s)
               with _ ->
                   failwith $"Bytes.fromBase64: invalid base64: \"{s}\"")
          | v -> unreachable $"the checker rejects 'Bytes.fromBase64' on {formatValue v}")
      "tryFromBase64",
      TFun(TStr, TNamed("Option", [ TBytes ])),
      VBuiltin(fun v ->
          match v with
          | VStr s ->
              (try
                  VUnion("Some", Some(VBytes(base64Bytes s)))
               with _ ->
                   VUnion("None", None))
          | v -> unreachable $"the checker rejects 'Bytes.tryFromBase64' on {formatValue v}")
      "toBase64",
      TFun(TBytes, TStr),
      VBuiltin(fun v ->
          match v with
          | VBytes b -> VStr(System.Convert.ToBase64String b)
          | v -> unreachable $"the checker rejects 'Bytes.toBase64' on {formatValue v}")
      "sha256",
      TFun(TBytes, TStr),
      VBuiltin(fun v ->
          match v with
          | VBytes b ->
              VStr(
                  System.Security.Cryptography.SHA256.HashData b
                  |> Array.map (fun x -> x.ToString "x2")
                  |> String.concat ""
              )
          | v -> unreachable $"the checker rejects 'Bytes.sha256' on {formatValue v}")
      "length",
      TFun(TBytes, TSize),
      VBuiltin(fun v ->
          match v with
          | VBytes b -> VSize(int64 b.Length)
          | v -> unreachable $"the checker rejects 'Bytes.length' on {formatValue v}") ]

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
      "parse", TFun(TStr, TSize), fst (parsePairImpl "Size" parseSize VSize)
      "tryParse", TFun(TStr, TNamed("Option", [ TSize ])), snd (parsePairImpl "Size" parseSize VSize)
      // the per-type sum/mean [D:seq-gaps]; the mean truncates to whole
      // bytes (integer division — bytes are the unit)
      "sum",
      TFun(TSeq TSize, TSize),
      typedSumImpl
          "Size.sum"
          (function
          | VSize b -> b
          | v -> unreachable $"the checker rejects summing {formatValue v}")
          VSize
      "average",
      TFun(TSeq TSize, TSize),
      typedAverageImpl
          "Size.average"
          (function
          | VSize b -> b
          | v -> unreachable $"the checker rejects averaging {formatValue v}")
          VSize ]

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
      "parse", TFun(TStr, TDur), fst (parsePairImpl "Duration" parseDurationMs VDur)
      "tryParse", TFun(TStr, TNamed("Option", [ TDur ])), snd (parsePairImpl "Duration" parseDurationMs VDur)
      // the per-type sum/mean [D:seq-gaps]; the mean truncates to whole
      // milliseconds (ms is the base unit)
      "sum",
      TFun(TSeq TDur, TDur),
      typedSumImpl
          "Duration.sum"
          (function
          | VDur d -> d
          | v -> unreachable $"the checker rejects summing {formatValue v}")
          VDur
      "average",
      TFun(TSeq TDur, TDur),
      typedAverageImpl
          "Duration.average"
          (function
          | VDur d -> d
          | v -> unreachable $"the checker rejects averaging {formatValue v}")
          VDur
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
                  Waiting.during $"sleeping {formatDuration n}" (fun () ->
                      System.Threading.Thread.Sleep(
                          System.TimeSpan.FromTicks(n * System.TimeSpan.TicksPerMillisecond)
                      ))

              VUnit
          | v -> unreachable $"the checker rejects 'Duration.sleep' on {formatValue v}") ]

let private secretMembers: (string * Ty * Value) list =
    // a marker the renderers respect [D:secret] — of ASSERTS secrecy (the
    // safe direction, for computed secrets), reveal is the one guarded exit
    [ "of",
      TFun(TStr, TSecret),
      VBuiltin(fun v ->
          match v with
          | VStr s -> VSecret s
          | v -> unreachable $"the checker rejects 'Secret.of' on {formatValue v}")
      "reveal",
      TFun(TSecret, TStr),
      VBuiltin(fun v ->
          match v with
          | VSecret s -> VStr s
          | v -> unreachable $"the checker rejects 'Secret.reveal' on {formatValue v}")
      // map keeps a derived value secret [D:secret]: `"Bearer " + reveal`
      // would defeat the type; Secret.map (fun t -> "Bearer " + t) does not
      "map",
      TFun(TFun(TStr, TStr), TFun(TSecret, TSecret)),
      VBuiltin(fun f ->
          VBuiltin(fun v ->
              match v with
              | VSecret s ->
                  (match apply f (VStr s) with
                   | VStr s' -> VSecret s'
                   | v' -> unreachable $"the checker rejects 'Secret.map' result {formatValue v'}")
              | v -> unreachable $"the checker rejects 'Secret.map' on {formatValue v}")) ]

let private httpMethodName (v: Value) : string =
    match v with
    | VUnion(m, None) -> m.ToUpperInvariant()
    | v -> unreachable $"the checker rejects a non-method {formatValue v}"

// auth is a UNION the runner encodes [D:http]: Basic is base64(user:pass),
// an ENCODING no caller should build by hand. The Secret is REVEALED here —
// the one deliberate reveal (the value reaches the socket in the clear, a
// stated non-claim, the argv analogue)
let private authHeaders (v: Value) : (string * string) list =
    match v with
    | VUnion("NoAuth", None) -> []
    | VUnion("Bearer", Some(VSecret t)) -> [ "Authorization", "Bearer " + t ]
    | VUnion("Basic", Some(VTuple [ VStr u; VSecret p ])) -> [ "Authorization", "Basic " + Http.basicToken u p ]
    | v -> unreachable $"the checker rejects this auth {formatValue v}"

let private httpBodyOf (v: Value) : (string * string) option =
    match v with
    | VUnion("NoBody", None) -> None
    // Json carries pre-serialized `to json` lines [D:http]: send joins them
    // with \n and sets the content type — BYTE-EXACT, the whole point over
    // curl -d (which strips the newlines)
    | VUnion("Json", Some(VSeq lines)) -> Some("application/json", lines |> Seq.map asString |> String.concat "\n")
    | VUnion("Text", Some(VStr s)) -> Some("text/plain", s)
    | v -> unreachable $"the checker rejects this body {formatValue v}"

let private headerPairs (v: Value) : (string * string) list =
    match v with
    | VSeq items ->
        items
        |> Seq.map (fun it ->
            match it with
            | VTuple [ VStr k; VStr hv ] -> k, hv
            // secretHeaders: revealed at send, the same deliberate reveal
            | VTuple [ VStr k; VSecret hv ] -> k, hv
            | v -> unreachable $"the checker rejects a header pair {formatValue v}")
        |> List.ofSeq
    | v -> unreachable $"the checker rejects a header seq {formatValue v}"

let private httpDefaults: Value =
    VRecord(
        "HttpRequest",
        [ "method", VUnion("Get", None)
          "url", VStr ""
          "auth", VUnion("NoAuth", None)
          "headers", VSeq Seq.empty
          "secretHeaders", VSeq Seq.empty
          "body", VUnion("NoBody", None)
          "timeout", VDur 30000L
          "insecure", VBool false ]
    )

// translate the request record to Http.Req, send, RAISE on transport
// failure (status is data — the caller decides) [D:http]
let private runRequest (reqV: Value) : Http.Resp =
    match reqV with
    | VRecord("HttpRequest", f) ->
        let get k = recGet k f

        let req: Http.Req =
            { Method = httpMethodName (get "method")
              Url =
                (match get "url" with
                 | VStr s -> s
                 | v -> unreachable $"url {formatValue v}")
              Headers =
                authHeaders (get "auth")
                @ headerPairs (get "headers")
                @ headerPairs (get "secretHeaders")
              Body = httpBodyOf (get "body")
              TimeoutMs =
                (match get "timeout" with
                 | VDur ms -> int ms
                 | v -> unreachable $"timeout {formatValue v}")
              Insecure =
                (match get "insecure" with
                 | VBool b -> b
                 | v -> unreachable $"insecure {formatValue v}") }

        let host =
            try
                System.Uri(req.Url).Host
            with _ ->
                req.Url

        match Waiting.during $"{req.Method} {host}" (fun () -> Http.send req) with
        | Ok resp -> resp
        // the TLS case names its per-request repair [D:http-s2]
        | Error(msg, Http.TlsUntrusted) -> failwith $"{msg} (a self-signed endpoint can opt out: insecure = true)"
        | Error(msg, _) -> failwith msg
    | v -> unreachable $"the checker rejects a request on {formatValue v}"

// split the response bytes back into lines, byte-exact with the join at
// send — a body pipes straight into `from json T`
let private respBodyLines (resp: Http.Resp) : seq<Value> =
    resp.Body.Split('\n') |> Array.map VStr :> seq<Value>

// status is DATA [D:http]: a 4xx/5xx binds, never raises (the `| complete`
// posture for exit codes); ONLY transport failure raises
let private httpSendImpl: Value =
    VBuiltin(fun reqV ->
        let resp = runRequest reqV

        VRecord(
            "HttpResponse",
            [ "status", VInt(int64 resp.Status)
              "headers", VSeq(resp.Headers |> List.map (fun (k, hv) -> VTuple [ VStr k; VStr hv ]))
              "body", VSeq(respBodyLines resp) ]
        ))

// a CONSTRUCTOR [D:http-s2]: `Http.get u` = `{ Http.defaults with method =
// Get; url = u }` byte-identically (pinned) — a record PRODUCER, not a
// builder combinator; names only the method, the one thing already
// enumerated. The common case stops naming the record.
let private httpCtor (methodCase: string) : Value =
    VBuiltin(fun urlV ->
        match urlV, httpDefaults with
        | VStr url, VRecord("HttpRequest", f) ->
            VRecord("HttpRequest", f |> recSet "method" (VUnion(methodCase, None)) |> recSet "url" (VStr url))
        | v, _ -> unreachable $"the checker rejects an Http constructor on {formatValue v}")

// the raising shorthand [D:http-s2]: GET, raise on non-2xx naming the
// status, body only — the `curl -sf` analogue. Two names, no boolean:
// Http.fetch RAISES, Http.send RETURNS (the same 404 send binds as data)
let private httpFetchImpl: Value =
    VBuiltin(fun urlV ->
        match urlV, httpDefaults with
        | VStr url, VRecord("HttpRequest", f) ->
            let resp = runRequest (VRecord("HttpRequest", recSet "url" (VStr url) f))

            if resp.Status < 200 || resp.Status >= 300 then
                failwith $"{url} answered {resp.Status}"
            else
                VSeq(respBodyLines resp)
        | v, _ -> unreachable $"the checker rejects 'Http.fetch' on {formatValue v}")

// the query-string builder [D:http-s2] — NAMED withQuery so it does not
// collide with `query` the METHOD constructor. Percent-encodes each key
// and value: `$"{base}/search?q={term}"` can escape a path or break on a
// raw `&`; this cannot. (The non-claim's PATH half still stands.)
let private httpWithQueryImpl: Value =
    // DATA-LAST [D:sized-findings]: the URL is the pipeline operand
    // (`url |> Http.withQuery [(k, v)]`) — the audit found it
    // operand-first, flipped while Http is young enough to be free
    VBuiltin(fun paramsV ->
        VBuiltin(fun baseV ->
            match baseV, paramsV with
            | VStr baseUrl, VSeq items ->
                let enc (x: string) = System.Uri.EscapeDataString x

                let qs =
                    items
                    |> Seq.map (fun it ->
                        match it with
                        | VTuple [ VStr k; VStr v ] -> $"{enc k}={enc v}"
                        | v -> unreachable $"the checker rejects a query pair {formatValue v}")
                    |> String.concat "&"

                if qs = "" then
                    VStr baseUrl
                else
                    let sep = if baseUrl.Contains "?" then "&" else "?"
                    VStr(baseUrl + sep + qs)
            | v, _ -> unreachable $"the checker rejects 'Http.withQuery' on {formatValue v}"))

let private httpMembers: (string * Ty * Value) list =
    let ctorTy = TFun(TStr, TNamed("HttpRequest", []))

    [ "defaults", TNamed("HttpRequest", []), httpDefaults
      "send", TFun(TNamed("HttpRequest", []), TNamed("HttpResponse", [])), httpSendImpl
      "fetch", TFun(TStr, TSeq TStr), httpFetchImpl
      "withQuery", TFun(TSeq(TTuple [ TStr; TStr ]), TFun(TStr, TStr)), httpWithQueryImpl
      "get", ctorTy, httpCtor "Get"
      "post", ctorTy, httpCtor "Post"
      "put", ctorTy, httpCtor "Put"
      "delete", ctorTy, httpCtor "Delete"
      "patch", ctorTy, httpCtor "Patch"
      "head", ctorTy, httpCtor "Head"
      "options", ctorTy, httpCtor "Options"
      "query", ctorTy, httpCtor "Query" ]


// ---- Map<string, T> [D:map-string]: the ID-keyed object ------------
// String keys ONLY: every receipt has them (JSON object keys ARE
// strings), and int keys would make Map the first Ord-constrained
// container. Data-last throughout; get asserts, tryGet asks.
let private mapTy (v: Ty) = TNamed("Map", [ TStr; v ])

let private asMap (name: string) =
    function
    | VMap m -> m
    | v -> unreachable $"the checker rejects '{name}' on {formatValue v}"

// the scoped-process surface [D:scoped-procs]: the handle is DATA —
// pid/running/wait make the lifecycle inspectable, stop is the early
// teardown (the scope's own exit is then a no-op), tail reads the
// spill (the child's last words; poll-watch errors carry it free)
let private procTy = TNamed("Proc", [])

let private asProc (who: string) (v: Value) : ProcHandle =
    match v with
    | VProc h -> h
    | v -> unreachable $"the checker rejects '{who}' on {formatValue v}"

let private procMembers: (string * Ty * Value) list =
    [ "pid", TFun(procTy, TInt), VBuiltin(fun v -> VInt(int64 (asProc "Proc.pid" v).Proc.Id))
      "running",
      TFun(procTy, TBool),
      VBuiltin(fun v ->
          let h = asProc "Proc.running" v

          VBool(
              try
                  not h.Proc.HasExited
              with _ ->
                  false
          ))
      "wait",
      TFun(procTy, TInt),
      VBuiltin(fun v ->
          let h = asProc "Proc.wait" v

          Waiting.during $"waiting for pid {h.Proc.Id}" (fun () ->
              h.Proc.WaitForExit()
              VInt(int64 h.Proc.ExitCode)))
      "stop",
      TFun(procTy, TUnit),
      VBuiltin(fun v ->
          Proc.stopTree (asProc "Proc.stop" v).Proc
          VUnit)
      "tail", TFun(procTy, TSeq TStr), VBuiltin(fun v -> VSeq(procTail (asProc "Proc.tail" v) |> List.map VStr)) ]

// Net [D:scoped-procs]: ONE readiness probe — poll's body. Remote
// hosts on a receipt; localhost is the scoped-process pattern.
let private netMembers: (string * Ty * Value) list =
    [ "portOpen",
      TFun(TInt, TBool),
      VBuiltin(fun v ->
          match v with
          | VInt port ->
              if port < 1L || port > 65535L then
                  failwith $"Net.portOpen: a port is 1..65535; got {port}"

              // a RAW v4 socket to the loopback ADDRESS — never the
              // host-string path (getaddrinfo on a loaded macOS runner
              // turned "127.0.0.1" into ~400ms per probe and the poll
              // starved past its own timeout)
              use sock =
                  new System.Net.Sockets.Socket(
                      System.Net.Sockets.AddressFamily.InterNetwork,
                      System.Net.Sockets.SocketType.Stream,
                      System.Net.Sockets.ProtocolType.Tcp
                  )

              VBool(
                  try
                      sock.ConnectAsync(System.Net.IPAddress.Loopback, int port).Wait 250
                      && sock.Connected
                  with _ ->
                      false
              )
          | v -> unreachable $"the checker rejects 'Net.portOpen' on {formatValue v}") ]

// a point on the UTC timeline [D:instant]: the boring subset — no
// local zones, no calendar arithmetic; parse in, epoch out, `-` for
// the Duration between (the binop table's arms)
let private instantMembers: (string * Ty * Value) list =
    let parseP, tryParseP = parsePairImpl "Instant" parseInstantMs VInstant

    [ "now", TFun(TUnit, TInstant), VBuiltin(fun _ -> VInstant(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
      "parse", TFun(TStr, TInstant), parseP
      "tryParse", TFun(TStr, TNamed("Option", [ TInstant ])), tryParseP
      "parseWith",
      TFun(TStr, TFun(TStr, TInstant)),
      VBuiltin(fun fmtV ->
          VBuiltin(fun v ->
              match fmtV, v with
              | VStr fmt, VStr text ->
                  (match parseInstantWithMs fmt text with
                   | Ok ms -> VInstant ms
                   | Error e -> failwith $"Instant.parseWith: {e}")
              | _ -> unreachable "the checker rejects 'Instant.parseWith' on these arguments"))
      "tryParseWith",
      TFun(TStr, TFun(TStr, TNamed("Option", [ TInstant ]))),
      VBuiltin(fun fmtV ->
          VBuiltin(fun v ->
              match fmtV, v with
              | VStr fmt, VStr text ->
                  (match parseInstantWithMs fmt text with
                   | Ok ms -> VUnion("Some", Some(VInstant ms))
                   | Error e when e.StartsWith "unknown directive" -> failwith $"Instant.tryParseWith: {e}"
                   | Error _ -> VUnion("None", None))
              | _ -> unreachable "the checker rejects 'Instant.tryParseWith' on these arguments"))
      "epochMs",
      TFun(TInstant, TInt),
      VBuiltin(fun v ->
          match v with
          | VInstant ms -> VInt ms
          | v -> unreachable $"the checker rejects 'Instant.epochMs' on {formatValue v}")
      "ofEpochMs",
      TFun(TInt, TInstant),
      VBuiltin(fun v ->
          match v with
          | VInt ms -> VInstant ms
          | v -> unreachable $"the checker rejects 'Instant.ofEpochMs' on {formatValue v}") ]

let private mapMembers: (string * Ty * Value) list =
    [ "ofPairs",
      TFun(TSeq(TTuple [ TStr; tA ]), mapTy tA),
      VBuiltin(fun v ->
          match v with
          | VSeq items ->
              // duplicate keys: LAST WINS, matching the JSON boundary's
              // stated law — never a silent first-wins split
              items
              |> Seq.fold
                  (fun m x ->
                      match x with
                      | VTuple [ VStr k; value ] -> Map.add k value m
                      | v -> unreachable $"the checker rejects a pair {formatValue v}")
                  Map.empty
              |> VMap
          | v -> unreachable $"the checker rejects 'Map.ofPairs' on {formatValue v}")
      "pairs",
      TFun(mapTy tA, TSeq(TTuple [ TStr; tA ])),
      VBuiltin(fun v ->
          (asMap "Map.pairs" v)
          |> Seq.map (fun kv -> VTuple [ VStr kv.Key; kv.Value ])
          |> VSeq)
      "keys",
      TFun(mapTy tA, TSeq TStr),
      VBuiltin(fun v -> (asMap "Map.keys" v) |> Seq.map (fun kv -> VStr kv.Key) |> VSeq)
      "values", TFun(mapTy tA, TSeq tA), VBuiltin(fun v -> (asMap "Map.values" v) |> Seq.map _.Value |> VSeq)
      "get",
      TFun(TStr, TFun(mapTy tA, tA)),
      VBuiltin(fun k ->
          VBuiltin(fun mv ->
              match k, mv with
              | VStr key, VMap m ->
                  match Map.tryFind key m with
                  | Some v -> v
                  | None -> failwith $"Map.get: no key \"{key}\""
              | _ -> unreachable "the checker rejects 'Map.get' on these arguments"))
      "tryGet",
      TFun(TStr, TFun(mapTy tA, TNamed("Option", [ tA ]))),
      VBuiltin(fun k ->
          VBuiltin(fun mv ->
              match k, mv with
              | VStr key, VMap m ->
                  match Map.tryFind key m with
                  | Some v -> VUnion("Some", Some v)
                  | None -> VUnion("None", None)
              | _ -> unreachable "the checker rejects 'Map.tryGet' on these arguments"))
      "has",
      TFun(TStr, TFun(mapTy tA, TBool)),
      VBuiltin(fun k ->
          VBuiltin(fun mv ->
              match k, mv with
              | VStr key, VMap m -> VBool(Map.containsKey key m)
              | _ -> unreachable "the checker rejects 'Map.has' on these arguments"))
      "count", TFun(mapTy tA, TInt), VBuiltin(fun v -> VInt(int64 (asMap "Map.count" v).Count))
      "add",
      TFun(TStr, TFun(tA, TFun(mapTy tA, mapTy tA))),
      VBuiltin(fun k ->
          VBuiltin(fun value ->
              VBuiltin(fun mv ->
                  match k, mv with
                  | VStr key, VMap m -> VMap(Map.add key value m)
                  | _ -> unreachable "the checker rejects 'Map.add' on these arguments")))
      "remove",
      TFun(TStr, TFun(mapTy tA, mapTy tA)),
      VBuiltin(fun k ->
          VBuiltin(fun mv ->
              match k, mv with
              | VStr key, VMap m -> VMap(Map.remove key m)
              | _ -> unreachable "the checker rejects 'Map.remove' on these arguments")) ]

let private moduleTable: (string * (string * Ty * Value) list) list =
    [ "Seq", seqMembers
      "Str", strMembers
      "Map", mapMembers
      "Instant", instantMembers
      "Proc", procMembers
      "Net", netMembers
      "Path", pathMembers
      "Option", optionMembers
      "File", fileMembers @ fsMoreFileMembers
      "Dir", dirMembers
      "Args", argsMembers
      "Env", envMembers
      "Log", logMembers
      "Duration", durationMembers
      "Size", sizeMembers
      "Bytes", bytesMembers
      "Secret", secretMembers
      // the bounded-loop option templates [D:retry-poll]: the resting
      // values the key=value head desugars over
      "Retry",
      [ "defaults",
        TNamed("Retry", []),
        VRecord("Retry", [ "attempts", VInt 5L; "delay", VDur 1000L; "timeout", VUnion("None", None) ]) ]
      "Poll", [ "defaults", TNamed("Poll", []), VRecord("Poll", [ "timeout", VDur 60000L; "interval", VDur 1000L ]) ]
      "Http", httpMembers
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
          // ---- the FORM heads [D:scoped-procs]: #help retry/poll/within
          // answer like any member — the docs live here, one source
          "retry",
          bd
              "Re-run a body until its predicate passes: `retry attempts=5 delay=30s` + an indented body. A bool body IS the predicate; a value body takes `until r` + a predicate block and yields the value. Exhaustion raises; raises inside the body propagate (make failure data with | succeeds / | complete)."
              (Some "retry attempts=2 delay=1ms true")
              (Some "options are a record underneath: retry { Retry.defaults with attempts = 3 }")
          "poll",
          bd
              "retry's time-bounded twin: `poll timeout=5m interval=10s` + a readiness body. `watch=<proc>` fails FAST when the scoped process dies (its last output rides the error) and stamps the watched state on a timeout."
              (Some "poll timeout=1s interval=1ms true")
              (Some
                  "the wait-for-ready shape: within proc srv = … then poll timeout=10s watch=srv + Net.portOpen <port>")
          "within",
          bd
              ("A scoped resource for an indented block, released on EVERY exit (normal and raise): "
               + (Ast.withinKinds
                  |> List.map (fun k -> $"`{k.Name}` — {k.Doc}")
                  |> String.concat "; ")
               + ".")
              (Some "within tmp d print d")
              (Some "within proc srv = <command> binds a Proc handle; the tree is killed and reaped at scope exit")
          // ---- Instant: the UTC point [D:instant] ----
          "Instant.now",
          bd "The current instant (UTC)." (Some "Instant.now () > Instant.parse \"2020-01-01\"") None
          |> named [ "unit" ]
          "Instant.parse",
          bd
              "An ISO 8601 timestamp (Z or a numeric offset, normalized to UTC; a bare date reads as midnight UTC). Raises on anything else — tryParse asks."
              (Some "Instant.parse \"2026-08-14T12:00:00Z\"")
              None
          |> named [ "text" ]
          "Instant.tryParse",
          bd "Some instant, or None when the text is not ISO 8601." (Some "Instant.tryParse \"not-a-time\"") None
          |> named [ "text" ]
          "Instant.parseWith",
          bd
              "Read a timestamp by a NAMED format — %Y %m %d %e (1-2 digit day) %b (Jan..Dec) %H %M %S %f %z (%% literal), other text literal. PREFIX semantics: a log line's tail rides free. No %z means UTC. Raises on mismatch, naming the position."
              (Some "Instant.parseWith \"%Y/%m/%d %H:%M:%S\" \"2026/08/14 09:15:00 GET /health\"")
              None
          |> named [ "format"; "text" ]
          "Instant.tryParseWith",
          bd
              "Some instant, or None when the line does not match (an unknown DIRECTIVE still raises — that is a format bug, not a data miss)."
              (Some "Instant.tryParseWith \"%Y-%m-%d\" \"no timestamp here\"")
              None
          |> named [ "format"; "text" ]
          "Instant.epochMs",
          bd
              "Milliseconds since the Unix epoch, as an int — the interop escape (JSON fields, date +%s%3N)."
              (Some "Instant.parse \"1970-01-01T00:00:01Z\" |> Instant.epochMs")
              None
          |> named [ "t" ]
          "Instant.ofEpochMs",
          bd "The instant at an epoch-milliseconds int." (Some "Instant.ofEpochMs 0 |> show") None
          |> named [ "ms" ]
          // ---- Map: the ID-keyed object [D:map-string] ----
          "Map.ofPairs",
          bd
              "Build a map from (key, value) pairs; duplicate keys last-win (the JSON boundary's law)."
              (Some "Map.ofPairs [(\"a\", 1); (\"b\", 2)]")
              None
          |> named [ "pairs" ]
          "Map.pairs",
          bd
              "The entries as (key, value) pairs, key-sorted."
              (Some "Map.ofPairs [(\"b\", 2); (\"a\", 1)] |> Map.pairs |> Seq.force")
              None
          |> named [ "m" ]
          "Map.keys",
          bd "The keys, sorted." (Some "Map.ofPairs [(\"b\", 2); (\"a\", 1)] |> Map.keys |> Seq.force") None
          |> named [ "m" ]
          "Map.values",
          bd
              "The values, in key-sorted order."
              (Some "Map.ofPairs [(\"b\", 2); (\"a\", 1)] |> Map.values |> Seq.force")
              None
          |> named [ "m" ]
          "Map.get",
          bd
              "The value under a key (raises naming the key when absent — tryGet asks)."
              (Some "Map.ofPairs [(\"aaa\", 1)] |> Map.get \"aaa\"")
              None
          |> named [ "key"; "m" ]
          "Map.tryGet",
          bd
              "Some value when the key is present, None when not."
              (Some "Map.ofPairs [(\"aaa\", 1)] |> Map.tryGet \"zzz\"")
              None
          |> named [ "key"; "m" ]
          "Map.has",
          bd "True when a key is present." (Some "Map.ofPairs [(\"aaa\", 1)] |> Map.has \"aaa\"") None
          |> named [ "key"; "m" ]
          "Map.count",
          bd "The number of entries." (Some "Map.ofPairs [(\"aaa\", 1)] |> Map.count") None
          |> named [ "m" ]
          "Map.add",
          bd
              "A new map with the entry set (replacing an existing key); the original is untouched."
              (Some "Map.ofPairs [(\"a\", 1)] |> Map.add \"k\" 5")
              None
          |> named [ "key"; "value"; "m" ]
          "Map.remove",
          bd
              "A new map without the key (absent is fine); the original is untouched."
              (Some "Map.ofPairs [(\"a\", 1); (\"k\", 2)] |> Map.remove \"k\"")
              None
          |> named [ "key"; "m" ]
          // ---- Proc: the scoped-process handle [D:scoped-procs] ----
          "Proc.pid",
          bd "The child's OS process id." None (Some "within proc p = <command> binds the handle; see #help within")
          |> named [ "p" ]
          "Proc.running",
          bd "True while the child has not exited." None (Some "poll watch=p checks this for you, with better errors")
          |> named [ "p" ]
          "Proc.wait",
          bd
              "Block until the child exits NATURALLY; its exit code as data. The way to let a scoped process finish — the scope's own exit kills."
              None
              None
          |> named [ "p" ]
          "Proc.stop",
          bd "Tree-kill and reap now, idempotent; the scope's exit is then a no-op." None None
          |> named [ "p" ]
          "Proc.tail",
          bd
              "The child's last ~100 output lines (stderr first) from the spill — readable while it runs."
              None
              (Some "poll-watch failures carry this automatically")
          |> named [ "p" ]
          // ---- Net: readiness probes [D:scoped-procs] ----
          "Net.portOpen",
          bd
              "True when 127.0.0.1:<port> accepts a TCP connection (250ms attempt) — poll's readiness body. Remote hosts on a receipt."
              (Some "Net.portOpen 1")
              None
          |> named [ "port" ]
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
          bd "Left-fold: thread an accumulator through the elements." (Some "[1; 2; 3] |> Seq.fold (+) 0") None
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
          "Seq.exactlyOne",
          (bd
              "The one element — a cardinality ASSERTION: raises on none AND on more (head silently accepts a second element, hiding a wrong-arity source). The spelling for command output expected to be one line."
              (Some "[\"one line\"] |> Seq.exactlyOne |> print")
              None
           |> named [ "xs" ])
          "Seq.tryExactlyOne",
          (bd
              "exactlyOne's Option twin: Some when the sequence has exactly one element, None on none or more."
              (Some "[42] |> Seq.tryExactlyOne")
              None
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
          (bd "The first n elements, lazily; pairs with Seq.skip." (Some "[1; 2; 3] |> Seq.take 2 |> Seq.force") None
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
          // ---- the Seq-gaps cohort [D:seq-gaps] ----------------------
          "Seq.collect",
          bd
              "Map each element to a sequence and flatten, lazily (F#'s collect; flatMap elsewhere)."
              (Some "[\"a<b\"; \"c\"] |> Seq.collect (Str.split \"<\") |> Seq.force")
              None
          |> named [ "f"; "xs" ]
          "Seq.concat",
          bd
              "Flatten a sequence of sequences, lazily (collect with the identity)."
              (Some "[[1; 2]; [3]] |> Seq.concat |> Seq.force")
              None
          |> named [ "xss" ]
          "Seq.find",
          bd
              "The first element a predicate accepts (raises when none match — tryFind asks)."
              (Some "[1; 5; 3] |> Seq.find (fun x -> x > 2)")
              None
          |> named [ "pred"; "xs" ]
          "Seq.indexed",
          bd
              "Pair every element with its zero-based position, lazily — mapi/iteri are `indexed |> map`/`iter` over the tuple."
              (Some "[\"a\"; \"b\"] |> Seq.indexed |> Seq.force")
              None
          |> named [ "xs" ]
          "Seq.rev",
          bd
              "Reverse. FORCES the whole input on the first pull (never an infinite seq)."
              (Some "[1; 2; 3] |> Seq.rev |> Seq.force")
              None
          |> named [ "xs" ]
          "Seq.chunkBySize",
          bd
              "Split into consecutive chunks of at most n, lazily — the batching member (the last chunk may be short)."
              (Some "[1; 2; 3; 4; 5] |> Seq.chunkBySize 2 |> Seq.map Seq.force |> Seq.force")
              None
          |> named [ "n"; "xs" ]
          "Seq.takeWhile",
          bd
              "Elements while the predicate holds, lazily; stops at the first refusal."
              (Some "[1; 2; 9; 1] |> Seq.takeWhile (fun x -> x < 5) |> Seq.force")
              None
          |> named [ "pred"; "xs" ]
          "Seq.skipWhile",
          bd
              "Drop the leading run the predicate accepts, lazily; the rest streams whole."
              (Some "[1; 2; 9; 1] |> Seq.skipWhile (fun x -> x < 5) |> Seq.force")
              None
          |> named [ "pred"; "xs" ]
          "Seq.countBy",
          bd
              "Count elements per projected key as (key, count) pairs, first-seen key order; forces on the first pull."
              (Some "[\"a\"; \"bb\"; \"c\"] |> Seq.countBy Str.length |> Seq.force")
              None
          |> named [ "key"; "xs" ]
          "Seq.distinctBy",
          bd
              "Keep the first element per projected key, lazily — distinct's projection twin."
              (Some "[\"a\"; \"bb\"; \"cc\"] |> Seq.distinctBy Str.length |> Seq.force")
              None
          |> named [ "key"; "xs" ]
          "Seq.reduce",
          bd
              "Fold without a seed: the first element starts the accumulator (raises on empty — fold takes the seed)."
              (Some "[1; 2; 3] |> Seq.reduce (+)")
              None
          |> named [ "f"; "xs" ]
          "Seq.scan",
          bd
              "Fold emitting every intermediate state, the seed first, lazily."
              (Some "[1; 2; 3] |> Seq.scan (+) 0 |> Seq.force")
              None
          |> named [ "f"; "init"; "xs" ]
          "Seq.tryPick",
          bd
              "The first Some a chooser yields, as an Option — choose-then-head in one pass."
              (Some "[\"a\"; \"12\"] |> Seq.tryPick Str.tryToInt")
              None
          |> named [ "f"; "xs" ]
          "Seq.pick",
          bd
              "The first Some a chooser yields (raises when none — tryPick asks)."
              (Some "[\"a\"; \"12\"] |> Seq.pick Str.tryToInt")
              None
          |> named [ "f"; "xs" ]
          "Seq.except",
          bd
              "Set difference: the source without the excluded values (exclusions first, source last; the exclusion set materializes on the first pull, the source streams)."
              (Some "[1; 2; 3; 4] |> Seq.except [2; 4] |> Seq.force")
              None
          |> named [ "excluded"; "xs" ]
          "Seq.replicate",
          bd
              "n copies of one value, lazily (raises on a negative count)."
              (Some "Seq.replicate 3 \"x\" |> Seq.force")
              None
          |> named [ "n"; "x" ]
          "Seq.max",
          bd "The largest element (Ord; raises on empty). One pass — no sort." (Some "Seq.max [3; 1; 2]") None
          |> named [ "xs" ]
          "Seq.min",
          bd "The smallest element (Ord; raises on empty). One pass — no sort." (Some "Seq.min [3; 1; 2]") None
          |> named [ "xs" ]
          "Seq.maxBy",
          bd
              "The element whose projected key is largest (Ord on the key; raises on empty)."
              (Some "[\"a\"; \"ccc\"] |> Seq.maxBy Str.length")
              None
          |> named [ "key"; "xs" ]
          "Seq.minBy",
          bd
              "The element whose projected key is smallest (Ord on the key; raises on empty)."
              (Some "[\"a\"; \"ccc\"] |> Seq.minBy Str.length")
              None
          |> named [ "key"; "xs" ]
          "Seq.sort",
          bd
              "Sort ascending by the elements themselves (Ord); forces on the first pull."
              (Some "[\"pear\"; \"apple\"] |> Seq.sort |> Seq.force")
              None
          |> named [ "xs" ]
          "Seq.sortDescending",
          bd
              "Sort descending by the elements themselves (Ord); forces on the first pull."
              (Some "[1; 3; 2] |> Seq.sortDescending |> Seq.force")
              None
          |> named [ "xs" ]
          "Seq.average",
          bd
              "The mean of ints AS A FLOAT (raises on empty — absence is Option's job). Float/Size/Duration own their means (Float.average …)."
              (Some "[1; 2] |> Seq.average")
              None
          |> named [ "xs" ]
          "Float.sum",
          bd
              "Sum floats (the sum must stay finite — the floats law). Seq.sum stays seq<int>; each numeric type owns its sum."
              (Some "[1.5; 2.5] |> Float.sum")
              None
          |> named [ "xs" ]
          "Float.average",
          bd "The mean of floats (raises on empty; finite-only)." (Some "[1.0; 2.0] |> Float.average") None
          |> named [ "xs" ]
          "Size.sum",
          bd "Sum sizes — total bytes as a Size." (Some "[1KiB; 512B] |> Size.sum") None
          |> named [ "xs" ]
          "Size.average",
          bd "The mean size, truncated to whole bytes (raises on empty)." (Some "[1KiB; 3KiB] |> Size.average") None
          |> named [ "xs" ]
          "Duration.sum", bd "Sum durations." (Some "[90s; 30s] |> Duration.sum") None |> named [ "xs" ]
          "Duration.average",
          bd
              "The mean duration, truncated to whole milliseconds (raises on empty)."
              (Some "[90s; 30s] |> Duration.average")
              None
          |> named [ "xs" ]
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
          "Seq.pmapWith",
          bd
              "Seq.pmap with an explicit worker count — the sizing knob for rate-limited or memory-heavy arms."
              (Some "[1; 2; 3] |> Seq.pmapWith 2 (fun x -> x + 1) |> Seq.force")
              (Some "an explicit n is never reduced by nesting; pmap's default ladder is.")
          |> named [ "n"; "f"; "xs" ]
          "Seq.piterWith",
          bd
              "Seq.piter with an explicit worker count."
              (Some "[1; 2; 3] |> Seq.piterWith 2 (fun x -> ())")
              (Some "an explicit n is never reduced by nesting; piter's default ladder is.")
          |> named [ "n"; "f"; "xs" ]
          "Seq.pfirst",
          bd
              "Race an arm over every element; the FIRST SUCCESS wins. Losers' spawned processes are tree-killed and their failures never surface. All arms failed rethrows the first error by input order; an empty sequence raises. A losing arm's failure is DISCARDED — if only one arm can succeed, the others' errors are hidden, so a misconfigured fan-out still looks healthy."
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
          (bd
              "Render a value to its string form — the SAME text an interpolation hole gives. Reach for it where a hole cannot go: point-free positions (Seq.map show) and Secrets (masked). Total; functions show opaquely."
              (Some "[1; 2; 3] |> Seq.map show |> Seq.force")
              None
           |> named [ "value" ])
          "not", (bd "Boolean negation." (Some "not true") None |> named [ "b" ])
          "cd",
          (bd
              "Change the session's directory, returning the OLD one (restore by binding: `let prev = cd \"/tmp\"`). A bare name applies a BINDING (`cd target`); `~` expands; `within cd` is the scoped spelling."
              (Some "cd \".\"")
              None
           |> named [ "path" ])
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
          "Str.splitOnce",
          (bd
              "Split at the FIRST occurrence into (before, after) — the tail stays intact, separators and all; raises when the separator is absent (trySplitOnce is the Option twin)."
              (Some "Str.splitOnce \"=\" \"key=a=b\" |> snd |> print")
              None
           |> named [ "sep"; "s" ])
          "Str.trySplitOnce",
          (bd
              "splitOnce's Option twin: Some (before, after) at the first occurrence, None when the separator is absent — the KEY=VALUE parser's shape."
              (Some "match Str.trySplitOnce \"=\" \"key=val\" with | Some (k, v) -> print k | None -> print \"no\"")
              None
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
          "Str.toUtf8",
          (bd "The string's UTF-8 bytes as a Bytes value." (Some "Str.toUtf8 \"caf\u00e9\"") None
           |> named [ "s" ])
          "Str.fromUtf8",
          (bd
              "Decode Bytes as text; raises when the bytes are not valid UTF-8 OR contain NUL (the encoding law's gate — corruption never wears a success)."
              None
              None
           |> named [ "b" ])
          "Str.tryFromUtf8",
          (bd "fromUtf8 as an Option: None for non-text bytes, NUL included." None None
           |> named [ "b" ])
          "Str.tryIndexOf",
          (bd "The index of a substring as an Option." (Some "Str.tryIndexOf \"b\" \"abc\"") None
           |> named [ "needle"; "s" ])
          "Str.isMatch",
          bd "True when a regex matches anywhere in the string." (Some "Str.isMatch \"[0-9]+\" \"x42\"") None
          |> named [ "pattern"; "subject" ]
          "Str.rmatch",
          bd
              "The first regex match's groups as an Option of a sequence (positional groups; named `(?<x>...)` rejects — weir names captures at the binder)."
              (Some "Str.rmatch \"([0-9]+)\" \"x42\"")
              None
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
          "Path.under",
          (bd
              "The confining join: combine, normalise, then RAISE if the result escapes base — segment-wise, so uploads-evil is not under uploads. combine is for paths you control; under is for paths you do not. Purely textual (never follows symlinks or touches the disk); absolute and Windows-shaped second arguments refuse on every platform."
              (Some "Path.under (Path.tempRoot ()) \"a/b\"")
              None
           |> named [ "base"; "name" ])
          "Path.tempRoot",
          (bd
              "The system temp directory (a pure query; no trailing separator, platform-native)."
              (Some "Path.tempRoot ()")
              None
           |> named [ "()" ])
          "Path.newTempDir",
          (bd
              "CREATE a fresh unique directory under the temp root and return its path (within tmp's naming). Cleanup is the caller's or the OS's — use `within tmp dir` for removal on scope exit (which the exit hook also sweeps on Ctrl+C/kill); newTempDir DELIBERATELY outlives the block and is never swept."
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
                  "let d = Path.newTempDir ()\n[\"x\"] |> File.write $\"{d}/f.txt\"\nFile.delete $\"{d}/f.txt\"\nDir.delete d")
              None
           |> named [ "path" ])
          "File.copy",
          (bd
              "Copy src to dst — (src, dst), the universal convention (neither arg is 'the data', so data-last does not apply). REFUSES an existing destination (raises naming it); delete first to overwrite."
              (Some
                  "let d = Path.newTempDir ()\n[\"x\"] |> File.write $\"{d}/a.txt\"\nFile.copy $\"{d}/a.txt\" $\"{d}/b.txt\"\nDir.deleteAll d")
              None
           |> named [ "src"; "dst" ])
          "File.move",
          (bd
              "Move (rename) src to dst — (src, dst); refuses an existing destination."
              (Some
                  "let d = Path.newTempDir ()\n[\"x\"] |> File.write $\"{d}/a.txt\"\nFile.move $\"{d}/a.txt\" $\"{d}/b.txt\"\nDir.deleteAll d")
              None
           |> named [ "src"; "dst" ])
          "File.size",
          (bd
              "The file's size as a Size — compare directly (File.size p > 10MiB); Size.toBytes for the int. Raises when absent (the plain name asserts; a trySize is a park)."
              (Some
                  "let d = Path.newTempDir ()\nlet f = $\"{d}/a.txt\"\n[\"x\"] |> File.write f\nprint $\"{File.size f}\"\nDir.deleteAll d")
              None
           |> named [ "path" ])
          "File.stat",
          (bd
              "The path's FileRow — ls's own row for ONE path, so `Path.glob ... |> Seq.map File.stat` turns strings into rows. Describes a symlink ITSELF (kind Symlink, target Some), not what it points at. Raises when absent — and a glob hit can vanish before stat reaches it (the glob TIMING seam)."
              (Some
                  "let d = Path.newTempDir ()\nlet f = $\"{d}/a.txt\"\n[\"x\"] |> File.write f\nprint (File.stat f).name\nDir.deleteAll d")
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
              (Some "let d = Path.newTempDir ()\nDir.create $\"{d}/tree-a/tree-b\"\nDir.deleteAll d")
              None
           |> named [ "path" ])
          "Dir.list",
          (bd
              "The directory's entries as FULL paths — files and directories both, SORTED (the glob precedent), eager. Filter with Seq.where + File.exists/Dir.exists; `Path.glob \"**\"` is the recursive spelling."
              (Some "let d = Path.newTempDir ()\nprint $\"{Dir.list d |> Seq.length}\"\nDir.delete d")
              None
           |> named [ "path" ])
          "Dir.stat",
          (bd
              "The directory's entries as ROWS — Dir.list's seq<FileRow> form, ls's own rows over the named directory (ls reads the cwd; Dir.stat reads elsewhere). Sorted by name, eager."
              (Some "let d = Path.newTempDir ()\nprint $\"{Dir.stat d |> Seq.length}\"\nDir.delete d")
              None
           |> named [ "path" ])
          "Dir.move",
          (bd
              "Move (rename) a directory — (src, dst); refuses an existing destination."
              (Some "let d = Path.newTempDir ()\nDir.move d $\"{d}-m\"\nDir.delete $\"{d}-m\"")
              None
           |> named [ "src"; "dst" ])
          "Dir.copy",
          (bd
              "Copy a directory and its contents — (src, dst); refuses an existing destination (Dir.deleteAll first to replace). Copying a directory MEANS its contents: there is no non-recursive form."
              (Some "let d = Path.newTempDir ()\nDir.copy d $\"{d}-c\"\nDir.deleteAll d\nDir.deleteAll $\"{d}-c\"")
              None
           |> named [ "src"; "dst" ])
          "File.read",
          (bd "Read a file's lines (eager — the whole file reads at the call)." None None
           |> named [ "path" ])
          "File.readBytes",
          (bd
              "Read a file's raw bytes — no decode, no line split (File.read substitutes U+FFFD and splits; this is the byte-faithful read). Bounded and in-memory: stream big data to a sink instead."
              None
              None
           |> named [ "path" ])
          "File.writeBytes",
          (bd "Write raw bytes to a file — the byte-faithful sink (File.write encodes lines and appends LF)." None None
           |> named [ "path"; "bytes" ])
          "File.sha256",
          (bd
              "The SHA-256 digest of a file's bytes, lowercase hex (sha256sum parity). Streams internally — never loads the file as a value."
              None
              None
           |> named [ "path" ])
          "File.readSecret",
          (bd
              "Read a file's whole content as a Secret (a mounted k8s/docker secret is a file); trailing newlines are trimmed."
              None
              None
           |> named [ "path" ])
          "File.write",
          (bd "Write a sequence of lines to a file (overwrites)." None None
           |> named [ "path"; "lines" ])
          "File.append",
          (bd "Append a sequence of lines to a file." None None
           |> named [ "path"; "lines" ])
          "File.mode",
          (bd
              "The path's permissions as rwxr-xr-x-shaped text, an Option — None on Windows (the platform limit stated, never invented). The READ follows a symlink (the File.* rule); existence does not — a dangling link raises naming the dangle. The receipt: the 0600 check before File.readSecret is File.mode p == Some \"rw-------\"."
              (Some "File.mode \".\" |> Option.defaultValue \"none\"")
              None
           |> named [ "path" ])

          // ---- Log [D:log-module]: STDERR always — stdout is DATA ----
          "Log.trace",
          (bd
              "Write a TRACE line to stderr — the innermost-detail level, hidden unless WEIR_LOG=trace."
              (Some "Log.trace \"entering the retry loop\"")
              None
           |> named [ "message" ])
          "Log.debug",
          (bd
              "Write a DEBUG line to stderr; hidden unless WEIR_LOG=debug (or trace). Stdout is DATA — every Log member writes to stderr, a law with no stream knob."
              (Some "Log.debug \"cache miss\"")
              None
           |> named [ "message" ])
          "Log.info",
          (bd
              "Write an INFO line to stderr — shown by default (WEIR_LOG=level moves the threshold, case-insensitive; off silences)."
              (Some "Log.info \"deploy starting\"")
              None
           |> named [ "message" ])
          "Log.warn",
          (bd
              "Write a WARN line to stderr — the highest level, shown unless WEIR_LOG=off."
              (Some "Log.warn \"lockfile missing, regenerating\"")
              None
           |> named [ "message" ])
          "Log.traceWith",
          (bd
              "Log.trace's thunk twin: the message computes ONLY when the level passes (weir has no lazy argument position — the Option.defaultWith precedent for the expensive argument)."
              (Some "Log.traceWith (fun () -> \"expensive detail\")")
              None
           |> named [ "thunk" ])
          "Log.debugWith",
          (bd
              "Log.debug's thunk twin: the message computes ONLY when the level passes."
              (Some "Log.debugWith (fun () -> \"expensive detail\")")
              None
           |> named [ "thunk" ])
          "Log.infoWith",
          (bd
              "Log.info's thunk twin: the message computes ONLY when the level passes."
              (Some "Log.infoWith (fun () -> \"expensive detail\")")
              None
           |> named [ "thunk" ])
          "Log.warnWith",
          (bd
              "Log.warn's thunk twin: the message computes ONLY when the level passes."
              (Some "Log.warnWith (fun () -> \"expensive detail\")")
              None
           |> named [ "thunk" ])

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
          "Http.defaults",
          bd
              "The request template RECORD: method = Get, empty url, NoAuth, no body, 30s timeout. `Http.send { Http.defaults with url = u }`. Sibling that reads alike: `Http.get url` is a CONSTRUCTOR returning one of these."
              None
              None
          "Http.send",
          (bd
              "Run a request. Status is DATA (a 404 binds; only transport failure raises). A Json body carries `to json` lines byte-exact; auth is a Secret-carrying union; show masks secrets."
              None
              None
           |> named [ "request" ])
          "Http.get",
          (bd
              "A request CONSTRUCTOR — returns an HttpRequest, makes NO request: `Http.get u` = `{ Http.defaults with method = Get; url = u }`; run it with Http.send. Add optionals with `with`: `Http.send { Http.get u with auth = Bearer t }`. The URL-in-body-out shorthand is Http.fetch."
              (Some "Http.get \"http://x/y\"")
              None
           |> named [ "url" ])
          "Http.post",
          (bd "Constructor: a Post request to the url (add body/auth with `with`)." None None
           |> named [ "url" ])
          "Http.put", (bd "Constructor: a Put request to the url." None None |> named [ "url" ])
          "Http.delete", (bd "Constructor: a Delete request to the url." None None |> named [ "url" ])
          "Http.patch", (bd "Constructor: a Patch request to the url." None None |> named [ "url" ])
          "Http.head", (bd "Constructor: a Head request to the url." None None |> named [ "url" ])
          "Http.options", (bd "Constructor: an Options request to the url." None None |> named [ "url" ])
          "Http.query",
          (bd
              "Constructor: a QUERY request (RFC 10008) — idempotent, so `retry` around it is safe by the method's definition. Almost nothing serves it yet; expect 405."
              None
              None
           |> named [ "url" ])
          "Http.fetch",
          (bd
              "The raising GET shorthand: takes a BARE URL (never a request — a built request runs through Http.send), returns body only, RAISES on non-2xx naming the status (the `curl -sf` / JS `fetch(url)` analogue). Http.fetch raises where Http.send returns — two names, no boolean."
              None
              None
           |> named [ "url" ])
          "Http.withQuery",
          (bd
              "Append a percent-encoded query string to a url — params first, the url last (data-last: `url |> Http.withQuery [(k, v)]`); keys and values are escaped, so a space or `&` cannot break the url. (NOT `Http.query` the method constructor.)"
              (Some "\"http://x/s\" |> Http.withQuery [(\"q\", \"a b\")]")
              None
           |> named [ "params"; "base" ])

          // ---- Size: bytes as a type [D:size] --------------------------
          "Bytes.fromBase64",
          (bd
              "Decode standard base64 (padded or unpadded) to Bytes; raises on malformed input. The binary door Str.fromBase64 deliberately is not."
              (Some "Bytes.fromBase64 \"iVBORw0KGgo=\"")
              None
           |> named [ "s" ])
          "Bytes.tryFromBase64",
          (bd "fromBase64 as an Option: None for malformed base64." (Some "Bytes.tryFromBase64 \"!!!\"") None
           |> named [ "s" ])
          "Bytes.toBase64",
          (bd
              "Base64 of the bytes — ONE unwrapped line; the deliberate text exit (print and the boundaries refuse raw bytes)."
              None
              None
           |> named [ "b" ])
          "Bytes.sha256",
          (bd "The SHA-256 digest of the bytes, lowercase hex (sha256sum parity)." None None
           |> named [ "b" ])
          "Bytes.length", (bd "The byte count as a Size." None None |> named [ "b" ])
          "Size.bytes",
          (bd "A size of n bytes — the literal 512B, as a function." (Some "Size.bytes 512") None
           |> named [ "n" ])
          "Size.toBytes",
          (bd
              "The total bytes as an int — the exact exit (show's rendering truncates to one decimal)."
              (Some "Size.toBytes 2KiB")
              None
           |> named [ "s" ])
          "Secret.of",
          (bd
              "ASSERT that a string is secret — the SAFE direction, for computed secrets (a generated token, a derived key). show renders ***; Secret.reveal is the one exit."
              (Some "Secret.of \"hunter2\"")
              None
           |> named [ "s" ])
          "Secret.reveal",
          (bd
              "The one guarded exit: the secret's plain value. Every use of the value (a header, a hash) is a deliberate reveal — the audit is the call site."
              (Some "Secret.reveal (Secret.of \"x\")")
              None
           |> named [ "s" ])
          "Secret.map",
          (bd
              "Transform a secret's value, keeping it secret. `Secret.map (fun t -> \"Bearer \" + t)` stays secret where reveal-then-concat would not."
              (Some "Secret.map (fun t -> \"Bearer \" + t) (Secret.of \"x\")")
              None
           |> named [ "f"; "s" ])
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
              "Parse ONE JSON document (any number of lines — a pretty-printed HTTP body pipes straight in) into a declared record. Fields are int/string/bool/float or Option of one; an Option field reads a missing key or null as None."
              None
              (Some "a pipe stage: resp.body |> from json Config.")
          "from jsonl",
          bd
              "Parse a JSON line stream — one document per element (NDJSON, the shape `to jsonl` writes) — into declared records."
              None
              (Some "a pipe stage: xs |> from jsonl Config.")
          "to json",
          bd
              "Render ONE value as ONE JSON document (minified, one line): a record is an object, a seq an array (forced — one line cannot stream). A None field omits its key (so from json reads it back as None)."
              None
              (Some "a pipe stage: payload |> to json.")
          "to jsonl",
          bd
              "Render a sequence to JSON lines — one document per element (NDJSON, the shape `from jsonl` reads), lazily."
              None
              (Some "a pipe stage: xs |> to jsonl.")
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
          "FileRow",
          bd
              "A directory entry: name, kind (Regular | Directory | Symlink — a fact, not an answer), target (Some for a symlink, None otherwise — the one fact no File.* query answers), bytes (0 B for a directory), modified (the last-write Instant — the file's own fact, stable under binding; the table renders it relatively, show keeps ISO), hidden, path. From `ls` — files AND subdirectories, SORTED by name (ordinal: case-sensitive, uppercase first; never the locale). name is for MATCHING and display; path is for handing to File.* - name derives from path, never the reverse. Narrow facts are queries, not columns: File.mode for permissions."
              None
              None
          "EnvVar", bd "A name/value environment pair. From `Env.vars` / `pair` / `ofPairs` / `fromFile`." None None
          "Group", bd "A key and its items, from `Seq.groupBy`." None None ]

/// the boundary adapters a direction supports, DERIVED from the doc keys
/// (`from json` / `to yaml` …) [D:form-word-hover] — the one source the
/// adapters' own hovers already read, so the `from`/`to` discovery hover,
/// the completion, and the colorizer cannot drift from it. `dir` is
/// "from" or "to". Map is key-sorted, so the order is stable.
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

// the ALLOWLIST [D:bare-allowlist]: only these modules contribute bare
// aliases to the REPL. Inverted from a blocklist after
// three collisions (Secret.map stole bare `map` — 22 unrelated tests
// failed naming a module they never mentioned; Http.head stole `head`;
// Option/Float were earlier rounds): a blocklist made every NEW module
// unsafe by default, a collision away from the next hot-path-named
// member. Widening this set is a deliberate act with a recorded reason,
// never a side effect of adding a module.
let bareAliasModules: Set<string> = Set [ "Seq"; "Str" ]

// THE BARE-MEMBER RULE [D:bare-partition]: unambiguous means bare — a
// member is bare iff its name has exactly ONE home among the
// allowlisted modules; a two-home name is qualified-only on both sides
// (a bare slot holds ONE value: Map.ofList silently resolved `contains`
// to Str's and the Seq hot path errored with "expected string" — the
// derivation makes that accident structurally unrepeatable). The
// curation is bareAliasModules plus the PINNED collision set: a new
// collision DEMOTES a bare name, so the gate fails until someone
// decides.
// The derivation is factored over the table so the PROPERTY is
// pinnable: a non-allowlisted module with a `map`/`head` member must
// contribute nothing, and a colliding name must vanish from the set.
let private singleHomed (table: (string * (string * Ty * Value) list) list) =
    let allowed = table |> List.filter (fun (m, _) -> bareAliasModules.Contains m)

    let count =
        allowed
        |> List.collect (fun (_, members) -> members |> List.map (fun (n, _, _) -> n))
        |> List.countBy id
        |> Map.ofList

    allowed, (fun n -> count[n] = 1)

let bareEntriesOf (table: (string * (string * Ty * Value) list) list) : (string * Ty * Value) list =
    let allowed, isSingle = singleHomed table
    allowed |> List.collect snd |> List.filter (fun (n, _, _) -> isSingle n)

let private bareEntries: (string * Ty * Value) list = bareEntriesOf moduleTable

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
                match recGet "name" fields, recGet "value" fields with
                | VStr n, VStr value -> n, value
                | _ -> unreachable "the checker rejects non-EnvVar overlay entries"
            | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
        |> List.ofSeq
    | v -> unreachable $"the checker rejects 'cmdEnv' on {formatValue v}"

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
        // unit prints NOTHING [D:exit-reifiers] — the !() sigil
        // desugar's interior may be unit (| orFail)
        | VUnit -> VUnit
        | v -> unreachable $"the checker rejects 'print' on {formatValue v}")

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

let bareAliasHomesOf (table: (string * (string * Ty * Value) list) list) : Map<string, string> =
    let allowed, isSingle = singleHomed table

    allowed
    |> List.collect (fun (m, members) ->
        members
        |> List.choose (fun (n, _, _) -> if isSingle n then Some(n, m) else None))
    |> Map.ofList

let bareAliasHomes: Map<string, string> = bareAliasHomesOf moduleTable

let bareAliases: Set<string> =
    bareAliasHomes |> Map.toSeq |> Seq.map fst |> Set.ofSeq

// the collision set, derived — every name here is qualified-only on
// both sides. The GATE pins its exact contents [D:bare-partition]: a
// new collision silently demotes a bare name, which must be decided.
let bareTwoHome: Set<string> =
    moduleTable
    |> List.filter (fun (m, _) -> bareAliasModules.Contains m)
    |> List.collect (fun (_, members) -> members |> List.map (fun (n, _, _) -> n))
    |> List.countBy id
    |> List.choose (fun (n, c) -> if c > 1 then Some n else None)
    |> Set.ofList

// sortBy : Ord b => (a -> b) -> seq<a> -> seq<a> — the constraint that
// killed the runtime scalar-key rule (sentinel-ledger customer four).
let private sortByScheme: Scheme =
    { Forall = Set [ "a"; "b" ]
      Cs = Map [ "b", Set [ Cls.Ord ] ]
      Ty = TFun(TFun(TVar "a", TVar "b"), TFun(TSeq(TVar "a"), TSeq(TVar "a")))
      RowOrigins = Map.empty
      HoleDefaults = [] }

// the cohort's constrained schemes [D:seq-gaps]: Ord on the ELEMENT for
// the key-less sorts and extrema, Ord on the KEY for the By twins
// (sortBy's shape), Eq on the KEY for the projection twins, Eq on the
// element for set difference
let private ordSeqToElem: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Ord ] ]
      Ty = TFun(TSeq(TVar "a"), TVar "a")
      RowOrigins = Map.empty
      HoleDefaults = [] }

let private ordSeqToSeq: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Ord ] ]
      Ty = TFun(TSeq(TVar "a"), TSeq(TVar "a"))
      RowOrigins = Map.empty
      HoleDefaults = [] }

let private ordByToElem: Scheme =
    { Forall = Set [ "a"; "b" ]
      Cs = Map [ "b", Set [ Cls.Ord ] ]
      Ty = TFun(TFun(TVar "a", TVar "b"), TFun(TSeq(TVar "a"), TVar "a"))
      RowOrigins = Map.empty
      HoleDefaults = [] }

let private eqKeyCountBy: Scheme =
    { Forall = Set [ "a"; "b" ]
      Cs = Map [ "b", Set [ Cls.Eq ] ]
      Ty = TFun(TFun(TVar "a", TVar "b"), TFun(TSeq(TVar "a"), TSeq(TTuple [ TVar "b"; TInt ])))
      RowOrigins = Map.empty
      HoleDefaults = [] }

let private eqKeyDistinctBy: Scheme =
    { Forall = Set [ "a"; "b" ]
      Cs = Map [ "b", Set [ Cls.Eq ] ]
      Ty = TFun(TFun(TVar "a", TVar "b"), TFun(TSeq(TVar "a"), TSeq(TVar "a")))
      RowOrigins = Map.empty
      HoleDefaults = [] }

let private eqExcept: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Eq ] ]
      Ty = TFun(TSeq(TVar "a"), TFun(TSeq(TVar "a"), TSeq(TVar "a")))
      RowOrigins = Map.empty
      HoleDefaults = [] }

// members whose signature is a CONSTRAINED scheme, not a plain
// generalization — applied at the module map AND at the bare slot
// [D:bare-partition]: a bare `sortBy` must keep its Ord key, or the
// bare spelling would be laxer than the qualified one
let private seqSchemeOverrides: (string * Scheme) list =
    [ "contains", Check.containsScheme
      "distinct", Check.distinctScheme
      "sortBy", sortByScheme
      "sortByDescending", sortByScheme
      "max", ordSeqToElem
      "min", ordSeqToElem
      "sort", ordSeqToSeq
      "sortDescending", ordSeqToSeq
      "maxBy", ordByToElem
      "minBy", ordByToElem
      "countBy", eqKeyCountBy
      "distinctBy", eqKeyDistinctBy
      "except", eqExcept ]

let typeEnv: TypeEnv =
    { Values =
        entries @ internalAliases
        |> List.map (fun (n, ty, _) -> n, generalize ty)
        |> Map.ofList
        // FileKind's constructors [D:filerow] — values like any user
        // union's cases, NOT bare members (no alias machinery)
        |> fun vs ->
            fileKind.Cases
            |> List.fold (fun acc (c, _) -> Map.add c (generalize (TNamed(fileKind.Name, []))) acc) vs
        |> fun vs ->
            seqSchemeOverrides
            |> List.fold (fun acc (n, sch) -> (if bareAliases.Contains n then Map.add n sch acc else acc)) vs
        |> Map.add "print" Check.printScheme
        |> Map.add "printerr" Check.printScheme
        // the arming desugar's un-shadowable print [D:desugar-capture]
        |> Map.add "|print" Check.printScheme
        |> Map.add "show" Check.showScheme
      Modules =
        moduleTable
        |> List.map (fun (m, members) -> m, members |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList)
        |> Map.ofList
        |> Map.change
            "Seq"
            (Option.map (fun ms -> seqSchemeOverrides |> List.fold (fun acc (n, sch) -> Map.add n sch acc) ms))
      Types =
        Map
            [ fileRow.Name, Record fileRow
              fileKind.Name, Union fileKind
              completedDef.Name, Record completedDef
              groupDef.Name, Record groupDef
              envVarDef.Name, Record envVarDef ]
      ModuleTypes = Map.empty
      AnonLitDefs = System.Collections.Generic.Dictionary() }

let typeEnvStrict: TypeEnv =
    { typeEnv with
        Values = bareAliasHomes |> Map.fold (fun vs name _ -> Map.remove name vs) typeEnv.Values }

let valueEnv: Env =
    let flat = entries |> List.map (fun (n, _, v) -> n, v)

    let mangled =
        moduleTable
        |> List.collect (fun (m, members) -> members |> List.map (fun (n, _, v) -> $"{m}.{n}", v))

    ("Regular", VUnion("Regular", None))
    :: ("Directory", VUnion("Directory", None))
    :: ("Symlink", VUnion("Symlink", None))
    :: ("print", printImpl)
    :: ("printerr", printerrImpl)
    :: ("show", showImpl)
    :: ("|print", printImpl)
    :: (internalAliases |> List.map (fun (n, _, v) -> n, v))
    @ flat
    @ mangled
    |> Map.ofList

// the reserved-binder set [D:reserve-builtins]: builtins with NO
// qualified spelling — derived from the flat entries (bare ALIASES
// keep the standing values-shadow-builtins rule: Seq.max is the
// escape `let max = …` leaves open; these have none)
// escapes that are NOT aliases [D:dir-stat]: `ls` (a value) and
// Dir.stat (a function) are not one value with two names, so no
// bareAliasHomes entry — that map MEANS alias (strict mode removes its
// names; hover claims the home). But [D:strict-only]'s criterion is an
// ESCAPE's existence, and `Dir.stat "."` is ls's way back: shadowing
// ls no longer strands the rows.
let private escapeBearers: Set<string> = Set [ "ls" ]

Check.reservedBinderNames.Value <-
    (entries |> List.map (fun (n, _, _) -> n)) @ [ "print"; "printerr"; "show" ]
    |> List.filter (fun n ->
        not (n.StartsWith "|")
        && not (n.Contains ".")
        // a bare ALIAS keeps the standing shadow rule — its qualified
        // home is the way back (`let max = …` leaves Seq.max reachable)
        && not (Map.containsKey n bareAliasHomes)
        && not (Set.contains n escapeBearers))
    |> Set.ofList
