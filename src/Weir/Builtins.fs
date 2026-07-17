module Weir.Builtins

open System.Diagnostics
open System.IO
open Weir.Types
open Weir.Eval

let fileRow: RecordDef =
    { Name = "FileRow"
      Params = []
      Fields = [ "Name", TStr; "Size", TInt(Some "mb"); "ReadOnly", TBool ] }

let seqFileRow = TSeq(TNamed(fileRow.Name, []))

let file (name: string) (sizeMb: int) (readOnly: bool) : Value =
    VRecord(fileRow.Name, Map [ "Name", VStr name; "Size", VInt sizeMb; "ReadOnly", VBool readOnly ])

let private realLs: Value =
    VSeq(
        Seq.delay (fun () ->
            DirectoryInfo(Session.Cwd).GetFiles()
            |> Seq.map (fun f -> file f.Name (int (f.Length / 1048576L)) f.IsReadOnly))
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
            | VInt n, VSeq items -> VSeq(Seq.truncate n items)
            | _ -> unreachable "the checker rejects truncation on these arguments"))

let private mapImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun s ->
            match s with
            | VSeq items -> VSeq(items |> Seq.map (apply f))
            | v -> unreachable $"the checker rejects 'map' on {formatValue v}"))

let private sumImpl: Value =
    VBuiltin(fun s ->
        match s with
        | VSeq items ->
            VInt(
                items
                |> Seq.sumBy (fun v ->
                    match v with
                    | VInt n -> n
                    | v -> unreachable $"the checker rejects summing {formatValue v}")
            )
        | v -> unreachable $"the checker rejects 'sum' on {formatValue v}")

let private natsImpl: Value = VSeq(Seq.initInfinite VInt)

let private notImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VBool b -> VBool(not b)
        | v -> unreachable $"the checker rejects 'not' on {formatValue v}")

let changeDef: RecordDef =
    { Name = "Change"
      Params = []
      Fields = [ "Status", TStr; "Staged", TBool; "Unstaged", TBool; "Path", TStr ] }

let private shImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr cmdline -> VSeq(Proc.lines "/bin/sh" [ "-c"; cmdline ] None |> Seq.map VStr)
        | v -> unreachable $"the checker rejects 'sh' on {formatValue v}")

let private asString (v: Value) : string =
    match v with
    | VStr s -> s
    | v -> unreachable $"the checker rejects non-string command arguments: {formatValue v}"

let private cmdImpl: Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
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

            let resolved = Path.GetFullPath(Path.Combine(Session.Cwd, expanded))

            if not (Directory.Exists resolved) then
                failwith $"cd: no such directory: {resolved}"

            Session.Cwd <- resolved
            VStr resolved
        | v -> unreachable $"the checker rejects 'cd' on {formatValue v}")

let private pwdImpl: Value =
    VSeq(Seq.delay (fun () -> Seq.singleton (VStr Session.Cwd)))

let private headImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            match Seq.tryHead items with
            | Some x -> x
            | None -> failwith "head: empty sequence"
        | v -> unreachable $"the checker rejects 'head' on {formatValue v}")

let private collectImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            let materialized = List.ofSeq items
            VSeq(materialized :> seq<Value>)
        | v -> unreachable $"the checker rejects 'collect' on {formatValue v}")

let completedDef: RecordDef =
    { Name = "Completed"
      Params = []
      Fields = [ "ExitCode", TInt None; "Stdout", TSeq TStr; "Stderr", TSeq TStr ] }

let private completedImpl: Value =
    VBuiltin(fun progV ->
        VBuiltin(fun argsV ->
            match progV, argsV with
            | VStr prog, VSeq args ->
                let argv = args |> Seq.map asString |> List.ofSeq
                let code, stdout, stderr = Proc.complete (Proc.resolveProg prog) argv None

                VRecord(
                    completedDef.Name,
                    Map
                        [ "ExitCode", VInt code
                          "Stdout", VSeq(stdout |> List.map VStr :> seq<Value>)
                          "Stderr", VSeq(stderr |> List.map VStr :> seq<Value>) ]
                )
            | _ -> unreachable "the checker rejects 'completed' on these arguments"))

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
        | VSeq items -> VInt(Seq.length items)
        | v -> unreachable $"the checker rejects 'Seq.length' on {formatValue v}")

let private isEmptyImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items -> VBool(Seq.isEmpty items)
        | v -> unreachable $"the checker rejects 'isEmpty' on {formatValue v}")

let private scalarCompare (name: string) (a: Value) (b: Value) : int =
    match a, b with
    | VInt x, VInt y -> compare x y
    | VStr x, VStr y -> compare x y
    | VBool x, VBool y -> compare x y
    | v, _ -> failwith $"{name}: keys must be ints, strings or bools, got {formatValue v}"

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
        | VStr s -> VInt s.Length
        | v -> unreachable $"the checker rejects 'strLen' on {formatValue v}")

let private toIntImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr s ->
            match System.Int32.TryParse s with
            | true, n -> VInt n
            | _ -> failwith $"toInt: not an integer: \"{s}\""
        | v -> unreachable $"the checker rejects 'toInt' on {formatValue v}")

let private tryToIntImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr s ->
            match System.Int32.TryParse s with
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
                | i -> vSome (VInt i)
            | _ -> unreachable "the checker rejects 'tryIndexOf' on these arguments"))

let private substringImpl: Value =
    VBuiltin(fun start ->
        VBuiltin(fun len ->
            VBuiltin(fun subject ->
                match start, len, subject with
                | VInt st, VInt ln, VStr s ->
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

let private mapOptionImpl: Value =
    VBuiltin(fun f ->
        VBuiltin(fun opt ->
            match opt with
            | VUnion("Some", Some v) -> vSome (apply f v)
            | VUnion("None", None) -> vNone
            | v -> unreachable $"the checker rejects 'mapOption' on {formatValue v}"))

let private seqInt = TSeq(TInt None)
let private seqStr = TSeq TStr
let private tA = TVar "a"
let private tB = TVar "b"

let groupDef: RecordDef =
    { Name = "Group"
      Params = [ "k"; "v" ]
      Fields = [ "Key", TVar "k"; "Items", TSeq(TVar "v") ] }

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
                                | :? int as n -> VInt n
                                | :? string as str -> VStr str
                                | :? bool as b -> VBool b
                                | _ -> unreachable "groupBy key box"

                            VRecord(
                                groupDef.Name,
                                Map [ "Key", keyValue; "Items", VSeq(List.ofSeq group :> seq<Value>) ]
                            )))
                )
            | v -> unreachable $"the checker rejects 'groupBy' on {formatValue v}"))

let private seqMembers: (string * Ty * Value) list =
    [ "map", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tB)), mapImpl
      "where", TFun(TFun(tA, TBool), TFun(TSeq tA, TSeq tA)), whereImpl
      "first", TFun(TInt None, TFun(TSeq tA, TSeq tA)), truncateImpl
      "take", TFun(TInt None, TFun(TSeq tA, TSeq tA)), truncateImpl
      "head", TFun(TSeq tA, tA), headImpl
      "sum", TFun(seqInt, TInt None), sumImpl
      "collect", TFun(TSeq tA, TSeq tA), collectImpl
      "tryHead", TFun(TSeq tA, TNamed("Option", [ tA ])), tryHeadImpl
      "tryFind", TFun(TFun(tA, TBool), TFun(TSeq tA, TNamed("Option", [ tA ]))), tryFindImpl
      "isEmpty", TFun(TSeq tA, TBool), isEmptyImpl
      "length", TFun(TSeq tA, TInt None), seqLengthImpl
      "sortBy", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tA)), sortByImpl
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
      "length", TFun(TStr, TInt None), strLenImpl
      "sub", TFun(TInt None, TFun(TInt None, TFun(TStr, TStr))), substringImpl
      "toInt", TFun(TStr, TInt None), toIntImpl
      "tryToInt", TFun(TStr, TNamed("Option", [ TInt None ])), tryToIntImpl
      "tryIndexOf", TFun(TStr, TFun(TStr, TNamed("Option", [ TInt None ]))), tryIndexOfImpl ]

let private optionMembers: (string * Ty * Value) list =
    [ "map", TFun(TFun(tA, tB), TFun(TNamed("Option", [ tA ]), TNamed("Option", [ tB ]))), mapOptionImpl
      "defaultTo", TFun(tA, TFun(TNamed("Option", [ tA ]), tA)), defaultToImpl ]

let private moduleTable: (string * (string * Ty * Value) list) list =
    [ "Seq", seqMembers; "Str", strMembers; "Option", optionMembers ]

let private bareAliases: Set<string> =
    Set
        [ "map"
          "where"
          "first"
          "take"
          "head"
          "sum"
          "collect"
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

let private entries: (string * Ty * Value) list =
    [ "ls", seqFileRow, realLs
      "nats", seqInt, natsImpl
      "sh", TFun(TStr, seqStr), shImpl
      "cmd", TFun(TStr, TFun(TSeq TStr, seqStr)), cmdImpl
      "into", TFun(TStr, TFun(seqStr, seqStr)), intoImpl
      "cd", TFun(TStr, TStr), cdImpl
      "pwd", TSeq TStr, pwdImpl
      "not", TFun(TBool, TBool), notImpl
      "completed", TFun(TStr, TFun(TSeq TStr, TNamed(completedDef.Name, []))), completedImpl ]
    @ bareEntries

let commandCallable: Set<string> = Set [ "cd" ]

let typeEnv: TypeEnv =
    { Values = entries |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList
      Modules =
        moduleTable
        |> List.map (fun (m, members) -> m, members |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList)
        |> Map.ofList
      Types =
        Map
            [ fileRow.Name, Record fileRow
              changeDef.Name, Record changeDef
              completedDef.Name, Record completedDef
              groupDef.Name, Record groupDef ] }

let valueEnv: Env =
    let flat = entries |> List.map (fun (n, _, v) -> n, v)

    let mangled =
        moduleTable
        |> List.collect (fun (m, members) -> members |> List.map (fun (n, _, v) -> $"{m}.{n}", v))

    flat @ mangled |> Map.ofList
