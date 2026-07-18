module Weir.Builtins

open System.Diagnostics
open System.IO
open Weir.Types
open Weir.Eval

let fileRow: RecordDef =
    { Name = "FileRow"
      Params = []
      Fields =
        [ "Name", TStr
          "Size", TInt(Some "mb")
          "Bytes", TInt(Some "b")
          "ReadOnly", TBool ] }

let seqFileRow = TSeq(TNamed(fileRow.Name, []))

let file (name: string) (sizeMb: int64) (readOnly: bool) : Value =
    VRecord(
        fileRow.Name,
        Map
            [ "Name", VStr name
              "Size", VInt sizeMb
              "Bytes", VInt(sizeMb * 1048576L)
              "ReadOnly", VBool readOnly ]
    )

let fileWithBytes (name: string) (bytes: int64) (readOnly: bool) : Value =
    VRecord(
        fileRow.Name,
        Map
            [ "Name", VStr name
              "Size", VInt(bytes / 1048576L)
              "Bytes", VInt bytes
              "ReadOnly", VBool readOnly ]
    )

let private realLs: Value =
    VSeq(
        Seq.delay (fun () ->
            DirectoryInfo(Session.Cwd).GetFiles()
            |> Seq.map (fun f -> fileWithBytes f.Name f.Length f.IsReadOnly))
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

let private natsImpl: Value = VSeq(Seq.initInfinite (int64 >> VInt))

let private notImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VBool b -> VBool(not b)
        | v -> unreachable $"the checker rejects 'not' on {formatValue v}")

let changeDef: RecordDef =
    { Name = "Change"
      Params = []
      Fields = [ "Status", TStr; "Staged", TBool; "Unstaged", TBool; "Path", TStr ] }

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
                        [ "ExitCode", VInt(int64 code)
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
        | VSeq items -> VInt(int64 (Seq.length items))
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
      "iter", TFun(TFun(tA, TUnit), TFun(TSeq tA, TUnit)), iterImpl
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

let private moduleTable: (string * (string * Ty * Value) list) list =
    [ "Seq", seqMembers
      "Str", strMembers
      "Option", optionMembers
      "File", fileMembers ]

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
      "cmd", TFun(TStr, TFun(TSeq TStr, seqStr)), cmdImpl
      "into", TFun(TStr, TFun(seqStr, seqStr)), intoImpl
      "cd", TFun(TStr, TStr), cdImpl
      "pwd", TSeq TStr, pwdImpl
      "not", TFun(TBool, TBool), notImpl
      "completed", TFun(TStr, TFun(TSeq TStr, TNamed(completedDef.Name, []))), completedImpl ]
    @ bareEntries

let private printImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VSeq items ->
            writeLines items
            VUnit
        | (VStr _ | VInt _ | VBool _) as scalar ->
            System.Console.WriteLine(scalarString "print argument" scalar)
            VUnit
        | v -> unreachable $"the checker rejects 'print' on {formatValue v}")

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

let typeEnv: TypeEnv =
    { Values =
        entries
        |> List.map (fun (n, ty, _) -> n, generalize ty)
        |> Map.ofList
        |> Map.add "print" Check.printScheme
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

let typeEnvStrict: TypeEnv =
    { typeEnv with
        Values = bareAliasHomes |> Map.fold (fun vs name _ -> Map.remove name vs) typeEnv.Values }

let valueEnv: Env =
    let flat = entries |> List.map (fun (n, _, v) -> n, v)

    let mangled =
        moduleTable
        |> List.collect (fun (m, members) -> members |> List.map (fun (n, _, v) -> $"{m}.{n}", v))

    ("print", printImpl) :: flat @ mangled |> Map.ofList
