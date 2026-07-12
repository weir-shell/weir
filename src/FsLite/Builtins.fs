module FsLite.Builtins

open System.Diagnostics
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

let changeDef: RecordDef =
    { Name = "Change"
      Fields = [ "Status", TStr; "Staged", TBool; "Unstaged", TBool; "Path", TStr ] }

let private procLines (cmdline: string) (input: seq<string> option) : seq<string> =
    seq {
        let psi = ProcessStartInfo("/bin/sh")
        psi.ArgumentList.Add "-c"
        psi.ArgumentList.Add cmdline
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.RedirectStandardInput <- input.IsSome
        use p = Process.Start psi

        match input with
        | Some lines ->
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    try
                        for l in lines do
                            p.StandardInput.WriteLine l
                    with _ ->
                        ()
                finally
                    p.StandardInput.Close())
            |> ignore
        | None -> ()

        try
            let out = p.StandardOutput
            let mutable line = out.ReadLine()

            while line <> null do
                yield line
                line <- out.ReadLine()

            let stderr = p.StandardError.ReadToEnd().Trim()
            p.WaitForExit()

            if p.ExitCode <> 0 then
                let detail = if stderr = "" then "" else $": {stderr}"
                failwith $"command failed with exit code {p.ExitCode}: {cmdline}{detail}"
        finally
            if not p.HasExited then
                p.Kill true
    }

let private cmdImpl: Value =
    VBuiltin(fun v ->
        match v with
        | VStr cmdline -> VSeq(procLines cmdline None |> Seq.map VStr)
        | v -> unreachable $"the checker rejects 'cmd' on {formatValue v}")

let private intoImpl: Value =
    VBuiltin(fun c ->
        VBuiltin(fun s ->
            match c, s with
            | VStr cmdline, VSeq items ->
                let lines =
                    items
                    |> Seq.map (fun v ->
                        match v with
                        | VStr s -> s
                        | v -> unreachable $"the checker rejects 'into' on non-string elements: {formatValue v}")

                VSeq(procLines cmdline (Some lines) |> Seq.map VStr)
            | _ -> unreachable "the checker rejects 'into' on these arguments"))

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
      "cmd", TFun(TStr, seqStr), cmdImpl
      "into", TFun(TStr, TFun(seqStr, seqStr)), intoImpl
      "not", TFun(TBool, TBool), notImpl ]

let typeEnv: TypeEnv =
    { Values = entries |> List.map (fun (n, ty, _) -> n, generalize ty) |> Map.ofList
      Types = Map [ fileRow.Name, Record fileRow; changeDef.Name, Record changeDef ] }

let valueEnv: Env = entries |> List.map (fun (n, _, v) -> n, v) |> Map.ofList
