module Weir.Builtins

open System.Diagnostics
open System.IO
open Weir.Types
open Weir.Eval

let fileRow: RecordDef =
    { Name = "FileRow"
      Params = []
      Fields = [ "Name", TStr; "Bytes", TInt; "ReadOnly", TBool ]
      Attrs = Map.empty }

let seqFileRow = TSeq(TNamed(fileRow.Name, []))

let file (name: string) (bytes: int64) (readOnly: bool) : Value =
    VRecord(fileRow.Name, Map [ "Name", VStr name; "Bytes", VInt bytes; "ReadOnly", VBool readOnly ])

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
      Fields = [ "Status", TStr; "Staged", TBool; "Unstaged", TBool; "Path", TStr ]
      Attrs = Map.empty }

let private asString (v: Value) : string =
    match v with
    | VStr s -> s
    | v -> unreachable $"the checker rejects non-string command arguments: {formatValue v}"

let private cmdImpl: Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                if prog.Trim() = "" then
                    failwith "cmd: empty program name"

                let argv = args |> Seq.map asString |> List.ofSeq
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
      Fields = [ "ExitCode", TInt; "Stdout", TSeq TStr; "Stderr", TSeq TStr ]
      Attrs = Map.empty }

// completedWith is the shared body; completed IS the empty overlay and
// completedEnv the env-sigil desugar target — the cmd/cmdEnv pattern.
let private completedWith (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                let argv = args |> Seq.map asString |> List.ofSeq

                let code, stdout, stderr =
                    Proc.completeWith overlay (Proc.resolveProg prog) argv None

                VRecord(
                    completedDef.Name,
                    Map
                        [ "ExitCode", VInt(int64 code)
                          "Stdout", VSeq(stdout |> List.map VStr :> seq<Value>)
                          "Stderr", VSeq(stderr |> List.map VStr :> seq<Value>) ]
                )
            | _ -> unreachable "the checker rejects 'completed' on these arguments"))

let private completedImpl: Value = completedWith []

// the exit-code reifiers [D:exit-reifiers], under the one law: output
// goes where the meaning goes. succeeds is ExitCode == 0 EXACTLY,
// output captured-and-discarded (a predicate is silent); orFail and
// exitCode STREAM (their output is for the human — the result travels
// separately): orFail raises `msg (exit N)` on nonzero, exitCode
// yields the code as int and never raises.
let private succeededWith (overlay: (string * string) list) : Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                let argv = args |> Seq.map asString |> List.ofSeq
                let code, _, _ = Proc.completeWith overlay (Proc.resolveProg prog) argv None
                VBool(code = 0)
            | _ -> unreachable "the checker rejects 'succeeded' on these arguments"))

let private orFailedWith (overlay: (string * string) list) : Value =
    VBuiltin(fun msgV ->
        VBuiltin(fun progV ->
            VBuiltin(fun argsV ->
                match msgV, progV, argsV with
                | VStr msg, VStr prog, VSeq args ->
                    let argv = args |> Seq.map asString |> List.ofSeq
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
                let argv = args |> Seq.map asString |> List.ofSeq
                VInt(int64 (Proc.streamCode overlay (Proc.resolveProg prog) argv))
            | _ -> unreachable "the checker rejects 'exitCoded' on these arguments"))

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
      Fields = [ "Key", TVar "k"; "Items", TSeq(TVar "v") ]
      Attrs = Map.empty }

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
                                Map [ "Key", keyValue; "Items", VSeq(List.ofSeq group :> seq<Value>) ]
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
// eager, input-order results, ProcessorCount degree, first worker error
// rethrown. Output interleaving from piter workers is line-atomic and
// owned by the user, as with any parallel tool.
let private runParallel (f: Value) (items: seq<Value>) : Value array =
    let arr = Seq.toArray items
    let out = Array.zeroCreate arr.Length
    // fork the ambient session: workers inherit the parent cwd; cd inside
    // a worker is worker-local and dies at the join
    let parentCwd = Session.Cwd()

    try
        System.Threading.Tasks.Parallel.For(
            0,
            arr.Length,
            fun i ->
                Session.enterWorker parentCwd

                try
                    out[i] <- apply f arr[i]
                finally
                    Session.exitWorker ()
        )
        |> ignore
    with :? System.AggregateException as ae ->
        raise (ae.Flatten().InnerExceptions[0])

    out

let private pmapImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(runParallel f items :> seq<Value>)
            | v -> unreachable $"the checker rejects 'pmap' on {formatValue v}"))

let private piterImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items ->
                runParallel f items |> ignore
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
      "range", TFun(TInt, TFun(TInt, TFun(TInt, seqInt))), rangeImpl
      "pairwise", TFun(TSeq tA, TSeq(TTuple [ tA; tA ])), pairwiseImpl
      "zip", TFun(TSeq tA, TFun(TSeq tB, TSeq(TTuple [ tA; tB ]))), zipImpl
      "exists", TFun(TFun(tA, TBool), TFun(TSeq tA, TBool)), existsImpl
      "forall", TFun(TFun(tA, TBool), TFun(TSeq tA, TBool)), forallImpl
      "item", TFun(TInt, TFun(TSeq tA, tA)), itemImpl
      "tryItem", TFun(TInt, TFun(TSeq tA, TNamed("Option", [ tA ]))), tryItemImpl
      "skip", TFun(TInt, TFun(TSeq tA, TSeq tA)), skipImpl
      "contains", TFun(tA, TFun(TSeq tA, TBool)), containsImpl
      "groupBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq(TNamed("Group", [ tB; tA ])))), groupByImpl ]

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
      "tryIndexOf", TFun(TStr, TFun(TStr, TNamed("Option", [ TInt ]))), tryIndexOfImpl
      "isMatch", TFun(TStr, TFun(TStr, TBool)), isMatchImpl
      "rmatch", TFun(TStr, TFun(TStr, TNamed("Option", [ TSeq TStr ]))), rmatchImpl ]

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
      "combine", TFun(TStr, TFun(TStr, TStr)), pathCombineImpl ]

let private optionMembers: (string * Ty * Value) list =
    [ "map", TFun(TFun(tA, tB), TFun(TNamed("Option", [ tA ]), TNamed("Option", [ tB ]))), mapOptionImpl
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
      Fields = [ "Name", TStr; "Value", TStr ]
      Attrs = Map.empty }

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
                        VRecord(envVarDef.Name, Map [ "Name", VStr k; "Value", VStr value ])))
            )
        | v -> unreachable $"the checker rejects 'Env.fromFile' on {formatValue v}")

// Env.pair / Env.ofPairs [D:seq-fold] — inline-env construction
// for a known nominal type (NOT an anonymous-records case).
let private envPairImpl: Value =
    VBuiltin(fun n ->
        VBuiltin(fun v ->
            match n, v with
            | VStr n, VStr v -> VRecord("EnvVar", Map [ "Name", VStr n; "Value", VStr v ])
            | _ -> unreachable "the checker rejects 'Env.pair' on these arguments"))

let private envOfPairsImpl: Value =
    VBuiltin(fun sv ->
        match sv with
        | VSeq items ->
            VSeq(
                items
                |> Seq.map (fun p ->
                    match p with
                    | VTuple [ VStr n; VStr v ] -> VRecord("EnvVar", Map [ "Name", VStr n; "Value", VStr v ])
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
                  VRecord(envVarDef.Name, Map [ "Name", VStr(string e.Key); "Value", VStr(string e.Value) ])))
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

let private moduleTable: (string * (string * Ty * Value) list) list =
    [ "Seq", seqMembers
      "Str", strMembers
      "Path", pathMembers
      "Option", optionMembers
      "File", fileMembers
      "Args", argsMembers
      "Env", envMembers ]

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
    |> List.filter (fun (m, _) -> m <> "Option")
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
        | (VStr _ | VInt _ | VBool _) as scalar ->
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
                match fields["Name"], fields["Value"] with
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

                    let argv = args |> Seq.map asString |> List.ofSeq

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
      "cmd", TFun(TStr, TFun(TSeq TStr, seqStr)), cmdImpl
      "into", TFun(TStr, TFun(seqStr, seqStr)), intoImpl
      "cd", TFun(TStr, TStr), cdImpl
      "pwd", TSeq TStr, pwdImpl
      "not", TFun(TBool, TBool), notImpl
      "fst", TFun(TTuple [ tA; tB ], tA), fstImpl
      "snd", TFun(TTuple [ tA; tB ], tB), sndImpl
      "completed", TFun(TStr, TFun(TSeq TStr, TNamed(completedDef.Name, []))), completedImpl
      "succeeded", TFun(TStr, TFun(TSeq TStr, TBool)), succeededWith []
      "orFailed", TFun(TStr, TFun(TStr, TFun(TSeq TStr, TUnit))), orFailedWith []
      "exitCoded", TFun(TStr, TFun(TSeq TStr, TInt)), exitCodedWith []
      "fail", TFun(TStr, TUnit), failImpl
      "exit", TFun(TInt, TUnit), exitImpl
      "cmdEnv", TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TSeq TStr))), cmdEnvImpl
      "completedEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TNamed(completedDef.Name, [])))),
      VBuiltin(fun envV -> completedWith (envVarPairs envV))
      "succeededEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TBool))),
      VBuiltin(fun envV -> succeededWith (envVarPairs envV))
      "orFailedEnv",
      TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TStr, TFun(TSeq TStr, TUnit)))),
      VBuiltin(fun envV -> orFailedWith (envVarPairs envV))
      "exitCodedEnv",
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
        | (VStr _ | VInt _ | VBool _) as scalar ->
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

let bareAliasHomes: Map<string, string> =
    moduleTable
    |> List.filter (fun (m, _) -> m <> "Option")
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
      Ty = TFun(TFun(TVar "a", TVar "b"), TFun(TSeq(TVar "a"), TSeq(TVar "a"))) }

let typeEnv: TypeEnv =
    { Values =
        entries
        |> List.map (fun (n, ty, _) -> n, generalize ty)
        |> Map.ofList
        |> Map.add "print" Check.printScheme
        |> Map.add "printerr" Check.printScheme
        |> Map.add "show" Check.showScheme
        |> Map.add "run" (generalize (TFun(TStr, TFun(TSeq TStr, TUnit))))
        |> Map.add "runEnv" (generalize (TFun(TSeq(TNamed("EnvVar", [])), TFun(TStr, TFun(TSeq TStr, TUnit)))))
      Modules =
        moduleTable
        |> List.map (fun (m, members) -> m, members |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList)
        |> Map.ofList
        |> Map.change "Seq" (Option.map (Map.add "contains" Check.containsScheme))
        |> Map.change "Seq" (Option.map (Map.add "sortBy" sortByScheme))
        |> Map.change "Seq" (Option.map (Map.add "sortByDescending" sortByScheme))
      Types =
        Map
            [ fileRow.Name, Record fileRow
              changeDef.Name, Record changeDef
              completedDef.Name, Record completedDef
              groupDef.Name, Record groupDef
              envVarDef.Name, Record envVarDef ] }

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
    :: ("run", runImpl)
    :: ("runEnv", runEnvImpl)
    :: flat
    @ mangled
    |> Map.ofList
