module Weir.Proc

open System.Diagnostics
open System.IO

// Child-env overlay [D:child-env-overlay]: `lines` IS the empty
// overlay, so cmd/cmdEnv share one path by construction.
let linesWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : seq<string> =
    seq {
        let psi = ProcessStartInfo(prog)

        for a in args do
            psi.ArgumentList.Add a

        for k, v in overlay do
            psi.Environment[k] <- v

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

                let signalNote =
                    // 128+N = terminated by signal N; name the common ones so a
                    // cancelled fzf reads as a cancel, not a mystery number
                    match p.ExitCode with
                    | 130 -> " (SIGINT — interrupted/cancelled)"
                    | 143 -> " (SIGTERM — terminated)"
                    | 137 -> " (SIGKILL — killed)"
                    | c when c > 128 && c < 165 -> $" (signal {c - 128})"
                    | _ -> ""

                failwith $"command failed with exit code {p.ExitCode}{signalNote}: {shown}"
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

let lines (prog: string) (args: string list) (input: seq<string> option) : seq<string> = linesWith [] prog args input

// stream stdout to the console as it arrives (stderr inherits), wait,
// return the exit code — the streaming reifiers' spawn path
// [D:exit-reifiers]: output goes to the human, the code is the result
let streamCode (overlay: (string * string) list) (prog: string) (args: string list) : int =
    let psi = ProcessStartInfo(prog)

    for a in args do
        psi.ArgumentList.Add a

    for k, v in overlay do
        psi.Environment[k] <- v

    psi.WorkingDirectory <- Session.Cwd()
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- false

    use p =
        try
            Process.Start psi
        with :? System.ComponentModel.Win32Exception ->
            failwith $"command not found or not executable: {prog}"

    try
        let out = p.StandardOutput
        let mutable line = out.ReadLine()

        while line <> null do
            System.Console.Out.WriteLine line
            line <- out.ReadLine()

        p.WaitForExit()
        p.ExitCode
    finally
        try
            p.Kill true
        with _ ->
            ()

        try
            p.WaitForExit()
        with _ ->
            ()

let resolveProg (prog: string) : string =
    if prog.Contains '/' then Session.resolve prog else prog

let completeWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : int * string list * string list =
    let psi = ProcessStartInfo(prog)

    for a in args do
        psi.ArgumentList.Add a

    for k, v in overlay do
        psi.Environment[k] <- v

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

let complete (prog: string) (args: string list) (input: seq<string> option) : int * string list * string list =
    completeWith [] prog args input
