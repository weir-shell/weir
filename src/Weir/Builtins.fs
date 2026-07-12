module Weir.Builtins

open System.Diagnostics
open System.IO
open Weir.Types
open Weir.Eval

let fileRow: RecordDef =
    { Name = "FileRow"
      Fields = [ "Name", TStr; "Size", TInt(Some "mb"); "ReadOnly", TBool ] }

let seqFileRow = TSeq(TNamed fileRow.Name)

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

let private doubleImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VInt n -> VInt(n * 2)
        | v -> unreachable $"the checker rejects 'double' on {formatValue v}")

let changeDef: RecordDef =
    { Name = "Change"
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

let private seqInt = TSeq(TInt None)
let private seqStr = TSeq TStr
let private tA = TVar "a"
let private tB = TVar "b"

let private entries: (string * Ty * Value) list =
    [ "ls", seqFileRow, realLs
      "where", TFun(TFun(tA, TBool), TFun(TSeq tA, TSeq tA)), whereImpl
      "first", TFun(TInt None, TFun(TSeq tA, TSeq tA)), truncateImpl
      "double", TFun(TInt None, TInt None), doubleImpl
      "nats", seqInt, natsImpl
      "map", TFun(TFun(tA, tB), TFun(TSeq tA, TSeq tB)), mapImpl
      "take", TFun(TInt None, TFun(TSeq tA, TSeq tA)), truncateImpl
      "sum", TFun(seqInt, TInt None), sumImpl
      "sh", TFun(TStr, seqStr), shImpl
      "cmd", TFun(TStr, TFun(TSeq TStr, seqStr)), cmdImpl
      "into", TFun(TStr, TFun(seqStr, seqStr)), intoImpl
      "cd", TFun(TStr, TStr), cdImpl
      "pwd", TSeq TStr, pwdImpl
      "not", TFun(TBool, TBool), notImpl ]

let typeEnv: TypeEnv =
    { Values = entries |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList
      Types = Map [ fileRow.Name, Record fileRow; changeDef.Name, Record changeDef ] }

let valueEnv: Env = entries |> List.map (fun (n, _, v) -> n, v) |> Map.ofList
