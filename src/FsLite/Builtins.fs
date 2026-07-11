module FsLite.Builtins

open System.IO
open FsLite.Types
open FsLite.Eval

let fileRow: RecordDef =
    { Name = "FileRow"
      Fields = [ "Name", TStr; "Size", TInt(Some "mb"); "ReadOnly", TBool ] }

let seqFileRow = TSeq(TNamed fileRow.Name)

let file (name: string) (sizeMb: int) (readOnly: bool) : Value =
    VRecord(fileRow.Name, Map [ "Name", VStr name; "Size", VInt sizeMb; "ReadOnly", VBool readOnly ])

let private realLs: Value =
    VSeq(
        Seq.delay (fun () ->
            DirectoryInfo(Directory.GetCurrentDirectory()).GetFiles()
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

let private doubleImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VInt n -> VInt(n * 2)
        | v -> unreachable $"the checker rejects 'double' on {formatValue v}")

let private seqInt = TSeq(TInt None)

let private entries: (string * Ty * Value) list =
    [ "ls", seqFileRow, realLs
      "where", TFun(TFun(TNamed fileRow.Name, TBool), TFun(seqFileRow, seqFileRow)), whereImpl
      "first", TFun(TInt None, TFun(seqFileRow, seqFileRow)), truncateImpl
      "double", TFun(TInt None, TInt None), doubleImpl
      "nats", seqInt, natsImpl
      "map", TFun(TFun(TInt None, TInt None), TFun(seqInt, seqInt)), mapImpl
      "take", TFun(TInt None, TFun(seqInt, seqInt)), truncateImpl
      "sum", TFun(seqInt, TInt None), sumImpl ]

let typeEnv: TypeEnv =
    { Values = entries |> List.map (fun (n, ty, _) -> n, ty) |> Map.ofList
      Types = Map [ fileRow.Name, Record fileRow ] }

let valueEnv: Env = entries |> List.map (fun (n, _, v) -> n, v) |> Map.ofList
