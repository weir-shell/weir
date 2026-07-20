module Weir.Proc

open System.Diagnostics
open System.IO

let lines (prog: string) (args: string list) (input: seq<string> option) : seq<string> =
    seq {
        let psi = ProcessStartInfo(prog)

        for a in args do
            psi.ArgumentList.Add a

        psi.WorkingDirectory <- Session.Cwd()
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- false
        psi.RedirectStandardInput <- input.IsSome

        use p =
            try
                Process.Start psi
            with :? System.ComponentModel.Win32Exception ->
                failwith $"command not found or not executable: {prog}"

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

            p.WaitForExit()

            if p.ExitCode <> 0 then
                let shown = String.concat " " (prog :: args)
                failwith $"command failed with exit code {p.ExitCode}: {shown}"
        finally
            try
                p.Kill true
            with _ ->
                ()

            try
                p.WaitForExit()
            with _ ->
                ()
    }

let resolveProg (prog: string) : string =
    if prog.Contains '/' then Session.resolve prog else prog

let complete (prog: string) (args: string list) (input: seq<string> option) : int * string list * string list =
    let psi = ProcessStartInfo(prog)

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- Session.Cwd()
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.RedirectStandardInput <- input.IsSome

    use p =
        try
            Process.Start psi
        with :? System.ComponentModel.Win32Exception ->
            failwith $"command not found or not executable: {prog}"

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

    let stderrTask =
        System.Threading.Tasks.Task.Run(fun () -> p.StandardError.ReadToEnd())

    let stdout =
        let acc = ResizeArray<string>()
        let mutable line = p.StandardOutput.ReadLine()

        while line <> null do
            acc.Add line
            line <- p.StandardOutput.ReadLine()

        List.ofSeq acc

    let stderr =
        stderrTask.Result.Split('\n') |> Array.toList |> List.filter (fun l -> l <> "")

    p.WaitForExit()
    p.ExitCode, stdout, stderr
