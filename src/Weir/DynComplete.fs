module Weir.DynComplete

// Dynamic argument values from the tool itself, the REPL's Tab only
// [D:sig-dynamic-complete]: a sig names its mechanism (`let dynamic =
// "git"` or `"cobra"`); this module asks — a bounded child, killed on the
// timeout, the output cap or the next key — and parses the answer. Which
// slot asks, and whether a sig's mechanism is honored, is Script's.

/// one question to a tool: the mechanism parses the answer
type Request = {
    Mechanism: string
    Exe: string
    Args: string list
}

type Outcome =
    /// the candidates as printed (unfiltered); `noFiles`: when none fits,
    /// offer nothing rather than paths (cobra's NoFileComp, its Error)
    | Answer of candidates: string list * noFiles: bool
    /// no answer, a failure, the timeout, the cap: what layer 1 offers
    | Fallback
    /// a key arrived while waiting: offer nothing, the key stays queued
    | Interrupted

let timeoutMs = 300
let maxBytes = 1024 * 1024
let maxLines = 10000

// ---- cobra: `<exe> __complete <args…> <partial>` ---------------------------
// `candidate<TAB>description` lines, then `:<directive>` (a bit set:
// 1 Error, 2 NoSpace, 4 NoFileComp, 8 FilterFileExt, 16 FilterDirs,
// 32 KeepOrder). Active-help lines (`_activeHelp_ …`) are prose, never a
// candidate. The editor never adds a trailing space, so NoSpace needs
// nothing; the file filters fall back to paths.

/// a cobra `__complete` answer
let parseCobra (text: string) : Outcome =
    let lines =
        text.Split '\n'
        |> Array.map (fun l -> l.TrimEnd '\r')
        |> Array.filter (fun l -> l <> "")

    let directive =
        match Array.tryLast lines with
        | Some l when l.StartsWith ":" ->
            match System.Int32.TryParse(l.Substring 1) with
            | true, d when d >= 0 -> Some d
            | _ -> None
        | _ -> None

    match directive with
    | None -> Fallback
    | Some d when d &&& 1 <> 0 -> Answer([], true)
    | Some d when d &&& (8 ||| 16) <> 0 -> Fallback
    | Some d ->
        let cands =
            lines[.. lines.Length - 2]
            |> Array.map (fun l ->
                match l.IndexOf '\t' with
                | -1 -> l
                | i -> l.Substring(0, i))
            |> Array.filter (fun c -> c <> "" && not (c.StartsWith "_activeHelp_"))
            |> List.ofArray

        Answer(cands, d &&& 4 <> 0)

// ---- git: a fixed table, fixed read-only commands --------------------------
// Never text from the sig, never the typed words: which operand of which
// subcommand names a ref or a remote, and the one command that lists it.
// Operands are the non-flag words after the subcommand (a flag's value
// word counts as one); a typed `--`, or a global option that moves the
// repository (`-C`, `-c`, `--git-dir`, `--work-tree`, `--namespace`),
// asks nothing.

type GitWant =
    | Refs
    | Heads
    | TagNames
    | Remotes

let private refSubs =
    set [
        "checkout"
        "switch"
        "merge"
        "rebase"
        "diff"
        "log"
        "reset"
        "cherry-pick"
        "revert"
        "show"
    ]

// `git remote <sub> <name>`: the subcommands whose first operand is a remote
let private remoteSubs =
    set [
        "remove"
        "rm"
        "rename"
        "set-url"
        "get-url"
        "show"
        "prune"
        "set-head"
        "set-branches"
        "update"
    ]

/// what the operand after `before` (the words typed after `git`) names
let gitWant (before: string list) : GitWant option =
    let isFlag (w: string) = w.Length > 1 && w[0] = '-'

    let moved (w: string) =
        w = "-C"
        || w = "-c"
        || w.StartsWith "--git-dir"
        || w.StartsWith "--work-tree"
        || w.StartsWith "--namespace"

    let globals = before |> List.takeWhile isFlag

    if globals |> List.exists moved || List.contains "--" before then
        None
    else
        match before |> List.skipWhile isFlag with
        | [] -> None
        | sub :: rest ->
            let flags = rest |> List.filter isFlag
            let ops = rest |> List.filter (isFlag >> not)
            let n = ops.Length

            let has (fs: string list) =
                flags |> List.exists (fun f -> List.contains f fs)

            match sub with
            | s when Set.contains s refSubs -> Some Refs
            | "branch" when has [ "-d"; "-D"; "--delete" ] -> Some Heads
            | "branch" when n = 0 && has [ "-m"; "-M"; "--move"; "-c"; "-C"; "--copy" ] -> Some Heads
            | "tag" when has [ "-d"; "--delete" ] -> Some TagNames
            | "push"
            | "fetch"
            | "pull" when n = 0 -> Some Remotes
            | "push"
            | "pull" when n = 1 -> Some Heads
            | "remote" when n = 1 && Set.contains ops[0] remoteSubs -> Some Remotes
            | _ -> None

/// the fixed git argv that lists `w`
let gitArgs (w: GitWant) : string list =
    let refs (dirs: string list) =
        [ "for-each-ref"; "--format=%(refname:short)" ] @ dirs

    match w with
    | Refs -> refs [ "refs/heads"; "refs/remotes"; "refs/tags" ]
    | Heads -> refs [ "refs/heads" ]
    | TagNames -> refs [ "refs/tags" ]
    | Remotes -> [ "remote" ]

/// git's listing: one name a line (a remote's symbolic HEAD is no ref to
/// type)
let parseGit (text: string) : Outcome =
    let names =
        text.Split '\n'
        |> Array.map (fun l -> l.TrimEnd '\r')
        |> Array.filter (fun l -> l <> "" && not (l.EndsWith "/HEAD"))
        |> List.ofArray

    Answer(names, false)

// ---- the bounded child -----------------------------------------------------

type RunResult =
    | Done of string
    | Failed
    | Aborted

/// run `exe args` in the session's cwd and env: stdin empty, stdout read
/// up to the byte/line cap, stderr discarded, no terminal (all three
/// redirected, so .NET leaves the tty alone). The child's tree is killed
/// on the timeout, the cap, or `abort ()` (polled while waiting) — and in
/// any case before this returns, so none outlives the call
let run (exe: string) (args: string list) (timeout: int) (abort: unit -> bool) : RunResult =
    let started =
        try
            let psi = System.Diagnostics.ProcessStartInfo(Proc.resolveProg exe)

            for a in args do
                psi.ArgumentList.Add a

            for k, v in Session.envOverlay () |> List.rev |> List.collect id do
                psi.Environment[k] <- v

            psi.WorkingDirectory <- Session.Cwd()
            psi.UseShellExecute <- false
            psi.RedirectStandardInput <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true

            // registered like every child [D:signal-main-unwind]: a signal
            // that ends weir mid-wait tree-kills it too
            Some(
                Session.gatedSpawn (fun () ->
                    let p = System.Diagnostics.Process.Start psi
                    Session.registerChild p
                    Session.registerForeground p
                    p)
            )
        with _ ->
            None

    match started with
    | None -> Failed
    | Some p ->
        try
            try
                p.StandardInput.Close()
            with _ ->
                ()

            p.ErrorDataReceived.Add ignore
            p.BeginErrorReadLine()

            let buf = new System.IO.MemoryStream()
            let mutable capped = false

            let reader =
                System.Threading.Thread(
                    (fun () ->
                        let chunk = Array.zeroCreate<byte> 65536
                        let stream = p.StandardOutput.BaseStream
                        let mutable lines = 0
                        let mutable go = true

                        while go do
                            let n =
                                try
                                    stream.Read(chunk, 0, chunk.Length)
                                with _ ->
                                    0

                            if n <= 0 then
                                go <- false
                            else
                                for i in 0 .. n - 1 do
                                    if chunk[i] = '\n'B then
                                        lines <- lines + 1

                                if buf.Length + int64 n > int64 maxBytes || lines > maxLines then
                                    capped <- true
                                    go <- false
                                    Session.killTree p
                                else
                                    buf.Write(chunk, 0, n)),
                    IsBackground = true
                )

            Session.startThread "a completion reader" reader
            let clock = System.Diagnostics.Stopwatch.StartNew()
            let mutable result = None

            while result.IsNone do
                if reader.Join 5 then
                    result <-
                        Some(
                            if capped then
                                Failed
                            else
                                Done(System.Text.Encoding.UTF8.GetString(buf.GetBuffer(), 0, int buf.Length))
                        )
                elif abort () then
                    result <- Some Aborted
                elif clock.ElapsedMilliseconds >= int64 timeout then
                    result <- Some Failed

            result.Value
        finally
            Session.killTree p

            try
                p.WaitForExit 1000 |> ignore
            with _ ->
                ()

            Session.deregisterProc p
            p.Dispose()

// ---- the per-edit cache ----------------------------------------------------
// same question, same answer for the rest of the edit; an interrupted ask
// is no answer and stays uncached. The REPL clears it per edit.

let private cache = System.Collections.Generic.Dictionary<string, Outcome>()

let clearCache () = lock cache (fun () -> cache.Clear())

/// ask, through the cache, with `run` as the child runner
let askWith (runner: string -> string list -> RunResult) (req: Request) : Outcome =
    let key = String.concat "\u0000" (req.Mechanism :: req.Exe :: req.Args)

    match lock cache (fun () -> cache.TryGetValue key) with
    | true, o -> o
    | _ ->
        let argv, parse =
            match req.Mechanism with
            | "cobra" -> "__complete" :: req.Args, parseCobra
            | _ -> req.Args, parseGit

        let outcome =
            match runner req.Exe argv with
            | Done text -> parse text
            | Failed -> Fallback
            | Aborted -> Interrupted

        if outcome <> Interrupted then
            lock cache (fun () -> cache[key] <- outcome)

        outcome

/// ask the real tool, bounded, `abort` polled while waiting
let ask (abort: unit -> bool) (req: Request) : Outcome =
    askWith (fun exe argv -> run exe argv timeoutMs abort) req

/// how the REPL's Tab reaches a tool: `Run` asks, `Note` shows a one-line
/// note under the buffer
type Hooks = {
    Run: Request -> Outcome
    Note: string -> unit
}

// the hint for a refused mechanism, once per session per tool
let private hinted = System.Collections.Generic.HashSet<string>()

let hintOnce (tool: string) (emit: unit -> unit) : unit =
    if lock hinted (fun () -> hinted.Add tool) then
        emit ()
