module Weir.Proc

open System.Diagnostics

// The spawn spec [D:spawn-spec]: ONE description of a child —
// Prog / Args / Env / Input — consumed by one starter. The output
// axis is the CONSUMER function (lines / streamCode / complete): the
// reifier law restated in code — the consumer IS the meaning. The
// public wrappers keep their signatures; they are thin constructors
// over the spec.
type Spec =
    { Prog: string
      Args: string list
      Env: (string * string) list
      Input: seq<string> option }

// the one starter: psi construction, env overlay, cwd, the not-found
// mapping, and the stdin writer — which PULLS the input seq lazily as
// the pipe accepts (laziness reaches inputs too)
let private start (redirectOut: bool) (redirectErr: bool) (s: Spec) : Process =
    let psi = ProcessStartInfo(s.Prog)

    for a in s.Args do
        psi.ArgumentList.Add a

    for k, v in s.Env do
        psi.Environment[k] <- v

    psi.WorkingDirectory <- Session.Cwd()
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- redirectOut
    psi.RedirectStandardError <- redirectErr
    psi.RedirectStandardInput <- s.Input.IsSome

    let p =
        try
            Process.Start psi
        with :? System.ComponentModel.Win32Exception ->
            failwith $"command not found or not executable: {s.Prog}"

    match s.Input with
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

    p

// tree-kill then reap — the lifecycle tail shared by the streaming
// consumers (complete disposes without killing: it has already read
// both pipes to the end)
let private reap (p: Process) =
    try
        p.Kill true
    with _ ->
        ()

    try
        p.WaitForExit()
    with _ ->
        ()

let private raiseNonzero (s: Spec) (code: int) =
    let shown = String.concat " " (s.Prog :: s.Args)

    let signalNote =
        // 128+N = terminated by signal N; name the common ones so a
        // cancelled fzf reads as a cancel, not a mystery number
        match code with
        | 130 -> " (SIGINT — interrupted/cancelled)"
        | 143 -> " (SIGTERM — terminated)"
        | 137 -> " (SIGKILL — killed)"
        | c when c > 128 && c < 165 -> $" (signal {c - 128})"
        | _ -> ""

    failwith $"command failed with exit code {code}{signalNote}: {shown}"

// ---- the consumers (the output axis) -------------------------------

// stdout as a lazy line seq; raise-at-force on nonzero; stderr inherits
let linesOf (s: Spec) : seq<string> =
    seq {
        use p = start true false s

        try
            let out = p.StandardOutput
            let mutable line = out.ReadLine()

            while line <> null do
                yield line
                line <- out.ReadLine()

            p.WaitForExit()

            if p.ExitCode <> 0 then
                raiseNonzero s p.ExitCode
        finally
            reap p
    }

// stdout relayed to the console as it arrives; the code as the result
// [D:exit-reifiers]: output goes to the human, the code is the meaning
let streamCodeOf (s: Spec) : int =
    use p = start true false s

    try
        let out = p.StandardOutput
        let mutable line = out.ReadLine()

        while line <> null do
            System.Console.Out.WriteLine line
            line <- out.ReadLine()

        p.WaitForExit()
        p.ExitCode
    finally
        reap p

// both pipes captured to completion; the record's raw material
let completedOf (s: Spec) : int * string list * string list =
    use p = start true true s

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

// ---- the public wrappers (signatures unchanged) --------------------

// Child-env overlay [D:child-env-overlay]: `lines` IS the empty
// overlay, so cmd/cmdEnv share one path by construction.
let linesWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : seq<string> =
    linesOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = input }

let lines (prog: string) (args: string list) (input: seq<string> option) : seq<string> = linesWith [] prog args input

let streamCode (overlay: (string * string) list) (prog: string) (args: string list) : int =
    streamCodeOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = None }

let completeWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : int * string list * string list =
    completedOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = input }

let complete (prog: string) (args: string list) (input: seq<string> option) : int * string list * string list =
    completeWith [] prog args input

let resolveProg (prog: string) : string =
    if prog.Contains '/' then Session.resolve prog else prog
