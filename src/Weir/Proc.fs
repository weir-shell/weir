module Weir.Proc

open System.Diagnostics

// The spawn spec [D:spawn-spec]: one description of a child —
// Prog / Args / Env / Input — consumed by one starter. The output
// axis is the consumer function (lines / streamCode / complete),
// which carries the meaning — the reifier law restated in code. The
// public wrappers keep their signatures; they are thin constructors
// over the spec.
type Spec =
    { Prog: string
      Args: string list
      Env: (string * string) list
      Input: seq<string> option
      // the scope snapshot [D:ambient-capture] [D:ambient-capture-within]:
      // a lazy command spawn replays the enclosing `within` scope it was
      // written in; None reads the live session at spawn (outside any
      // scope, and the eager statement paths)
      Cwd: string option
      Ambient: (string * string) list option }

/// the env pairs a spawn applies, in order [D:overlay-path]: the scope
/// snapshot (else the live `within env` layers, outer-first), then the
/// command's own pairs — what `spawn` itself applies
let spawnPairs (ambient: (string * string) list option) (env: (string * string) list) =
    let amb =
        match ambient with
        | Some snap -> snap
        | None -> Session.envOverlay () |> List.rev |> List.collect id

    List.append amb env

/// the live-session ambient — the default for every eager spawn
let liveAmbient () =
    { Prog = ""
      Args = []
      Env = []
      Input = None
      Cwd = None
      Ambient = None }

// a Secret spliced into argv reaches the child in the clear, but every
// message weir renders from an argv shows it as *** [D:secret-argv-mask]:
// the splice registers the word's text; the renderer masks any word equal
// to a registered one (equal text IS the secret — the safe direction)
let private secretWords = System.Collections.Concurrent.ConcurrentDictionary<string, byte>()

let markSecretWord (s: string) : unit =
    if s <> "" then
        secretWords.TryAdd(s, 0uy) |> ignore

/// one command line for a message, Secret-spliced words masked
let showArgv (words: string list) : string =
    words
    |> List.map (fun w -> if secretWords.ContainsKey w then "***" else w)
    |> String.concat " "

// NUL refusal at the spawn hand-off [D:spawn-nul-funnel]: argv and
// env are NUL-terminated C strings, so a NUL-bearing word would
// silently truncate at the child (the prefix runs; exit 0; no
// diagnostic). The evaluator's statement path already refused, but the
// reifier builtins, the ambient `within env` overlay, and the
// dynamic head assemble argv/env downstream and skipped it, so the
// byte leaked. Refusing here — the one point every process start
// funnels through — covers all of them. Same located message the
// statement path raised.
let private nulRefusal (what: string) (s: string) : unit =
    if s.Contains '\u0000' then
        failwith
            $"{what} contains a NUL byte — it would silently truncate at the process boundary (argv and env are NUL-terminated); NUL-bearing data is binary: pass it via a file or stdin, not an argument"

/// an env key a child can receive [D:env-key-refusal]: never empty, never
/// '=' (the child splits at the first one — another variable), never NUL
let envKeyRefusal (where: string) (k: string) : unit =
    if k = "" then
        failwith $"{where}: an env key is empty — a child cannot receive an unnamed variable"
    elif k.Contains '=' then
        failwith
            $"{where}: the env key '{k}' contains '=' — a child would read it as the variable '{k.Substring(0, k.IndexOf '=')}'; an env name has no '='"
    elif k.Contains '\u0000' then
        failwith $"{where}: the env key '{Weir.Types.quoteData (k.Replace('\u0000', '?'))}' contains a NUL byte — an env name is NUL-free"

// no command runs while a module is imported [D:module-import-no-commands]:
// the check-time walk refuses the direct forms; a helper reached through
// a value, a lambda or composition lands here
let private importRefusal (prog: string) : unit =
    match Session.importGuardModule () with
    | Some m ->
        failwith
            $"'{prog}' runs a command while module {m} is imported — a module's lets must not run commands at import; wrap the value in a function: let v () = …"
    | None -> ()

// the children started after weir acted on a signal: none ran when it
// came, so none took it with weir [D:cleanup-group-signal]
let private startedAfterSignal =
    System.Runtime.CompilerServices.ConditionalWeakTable<Process, obj>()

// the single spawn point [D:spawn-spec]: psi construction, the env
// law, cwd, the not-found mapping, and the race-group registration
// every child owes [D:seq-pfirst]. Redirection is the caller's — the
// only axis the two starters differ on, so everything else lives here.
let private spawn
    (redirectOut: bool)
    (redirectErr: bool)
    (redirectIn: bool)
    (cwdO: string option)
    (ambientO: (string * string) list option)
    (prog: string)
    (args: string list)
    (env: (string * string) list)
    : Process =
    // runtime plan refusal [D:plan-proc-runtime-guard]: the syntactic
    // firstPlanRefusal follows earlier top-level helpers but not a
    // callable passed as a value or imported, so an indirect proc
    // reaches the spawn. The helper runs on the capturing thread, so this
    // thread-local guard is set — refuse here, the one point all
    // process starts funnel through.
    if Session.planGuardActive () then
        failwith
            $"'{prog}' runs a command, and commands are refused inside 'plan' — a spawned binary reads and writes opaquely, so its effects cannot be captured; plan covers weir-native mutation only (File/Dir/Http)"

    importRefusal prog

    // ambient `within env` layers apply outer-first under the explicit
    // spec env, so inner and explicit keys win [D:within-scopes] —
    // applied at the spawn so every child (reifiers, scoped
    // procs) obeys the one law, not just the Eval command paths. A lazy
    // spawn replays its scope snapshot [D:ambient-capture-within].
    let ambient =
        match ambientO with
        | Some snap -> snap
        | None -> Session.envOverlay () |> List.rev |> List.collect id

    // integrity gate [D:spawn-nul-funnel]: the program name, every
    // argument, and every env key and value — no NUL crosses. The
    // resolved ambient is checked (the live `within env` overlay, not
    // just a captured snapshot — the reifier path carries None here).
    // An empty program name and a NUL-bearing path-like name get
    // weir-shaped diagnostics too (the raw platform exception otherwise
    // leaked).
    nulRefusal "a dynamic command head" prog

    if prog = "" then
        failwith "the command program name is empty — nothing to run"

    for a in args do
        nulRefusal "a command argument" a

    for k, v in ambient do
        nulRefusal $"the env key '{k}'" k
        envKeyRefusal "a command's environment" k
        nulRefusal $"the env value for '{k}'" v

    for k, v in env do
        nulRefusal $"the env key '{k}'" k
        envKeyRefusal "a command's environment" k
        nulRefusal $"the env value for '{k}'" v

    // a bare program resolves on the PATH the child runs with
    // [D:overlay-path]: a `within env`/`PATH=` overlay's PATH is searched
    // here — .NET would search weir's own — so the spawn and the child
    // agree on what the name means; with no overlay PATH, weir's own
    // (on Windows through PATHEXT, which CreateProcess does not apply)
    let prog =
        if Extern.isPathy prog then
            prog
        else
            let pairs = List.append ambient env

            match Extern.overlayPath pairs with
            | Some pv ->
                match Extern.resolveOn pairs pv prog with
                | Some full -> full
                | None ->
                    failwith
                        $"command not found: {Weir.Types.quoteData prog} — not on the PATH it runs with (set by a `within env` or a PATH= prefix)"
            | None when System.OperatingSystem.IsWindows() -> Extern.resolveFile prog |> Option.defaultValue prog
            | None -> prog

    Session.sigpipeForChildren.Force()
    Session.resetDetachedSigint.Force()
    // a cleanup's command at a foreground terminal starts with ^C and ^\
    // ignored, through the sh shim [D:cleanup-sigign]: the program is
    // resolved here, as the start would, so a missing one fails the same way
    let viaShim = Session.cleanupIgnoresTerminal ()

    let psi =
        if viaShim then
            let runnable (path: string) =
                System.IO.File.Exists path
                && (System.IO.File.GetUnixFileMode path
                    &&& (System.IO.UnixFileMode.UserExecute
                         ||| System.IO.UnixFileMode.GroupExecute
                         ||| System.IO.UnixFileMode.OtherExecute))
                   <> enum 0

            let found =
                if prog.Contains '/' then
                    Some(Session.resolve prog)
                else
                    (match System.Environment.GetEnvironmentVariable "PATH" with
                     | null -> ""
                     | v -> v).Split(':', System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun d -> System.IO.Path.Combine(d, prog))
                    |> Array.tryFind runnable

            let cwd =
                match cwdO with
                | Some c -> c
                | None -> Session.Cwd()

            match found with
            | Some path when runnable path && Session.CleanupShim.execAccepts path cwd runnable ->
                let psi = ProcessStartInfo(Session.CleanupShim.shell)
                psi.ArgumentList.Add "-c"
                psi.ArgumentList.Add Session.CleanupShim.script
                psi.ArgumentList.Add "sh"
                psi.ArgumentList.Add path
                psi
            | _ -> failwith $"command not found or not executable: {Weir.Types.quoteData prog}"
        else
            ProcessStartInfo(prog)

    for a in args do
        psi.ArgumentList.Add a

    // Windows: a cleanup's command in a new process group, which Ctrl+C
    // does not reach [D:cleanup-sigign]
    if System.OperatingSystem.IsWindows() && Session.inCleanup () then
        psi.CreateNewProcessGroup <- true

    for k, v in ambient do
        psi.Environment[k] <- v

    for k, v in env do
        psi.Environment[k] <- v

    psi.WorkingDirectory <- (match cwdO with Some c -> c | None -> Session.Cwd())
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- redirectOut
    psi.RedirectStandardError <- redirectErr
    psi.RedirectStandardInput <- redirectIn

    // a child reading weir's stdin starts after any line weir began
    // [D:prompt-cut-whole-line]
    if not redirectIn then
        Session.Stdin.finishForChild ()

    // start through registration inside the spawn gate: the teardown kill
    // sees every started child, and none starts after it
    // [D:signal-main-unwind]
    Session.gatedSpawn (fun () ->
        // read before the start: a child forked before weir acted on a
        // signal took a group-directed one with weir [D:cleanup-group-signal]
        let afterSignal = Session.teardownRequested () <> 0

        // a shim a ^C killed before its trap never started the command:
        // start it again [D:cleanup-sigign]
        let rec startOnce (tries: int) =
            let p =
                try
                    Process.Start psi
                with :? System.ComponentModel.Win32Exception ->
                    failwith $"command not found or not executable: {Weir.Types.quoteData prog}"

            if viaShim && tries < 5 && not (Session.CleanupShim.passedTrap p) then
                p.Dispose()
                startOnce (tries + 1)
            else
                p

        let p = startOnce 0

        if afterSignal then
            startedAfterSignal.Add(p, null)

        // a racing arm's children join its group [D:seq-pfirst] — a no-op
        // outside pfirst, and the scoped child owes it too: the spill path
        // reached the exit-hook backstop without ever joining the group
        Session.registerChild p

        // every live child is the signal handler's to tree-kill
        // [D:signal-main-unwind]: a pid-targeted SIGTERM (timeout, docker
        // stop, a CI cancel) reaches weir alone, and an unregistered
        // foreground child outlived it, reparented to init. Deregistered at
        // its exit; the check after subscribing covers an exit that raced it.
        let pid = p.Id
        Session.registerForeground p
        p.Exited.Add(fun _ -> Session.deregisterProcAt pid p)
        p.EnableRaisingEvents <- true

        if p.HasExited then
            Session.deregisterProcAt pid p

        p)

// a pipe's reader or writer on its own thread, never the pool's: blocked
// on a child that never writes (a race's loser), it holds no pool thread
// a winner's reader waits for [D:pipe-threads]. The thread joins the
// flow's node, as Task.Run's work would. A thread the OS refuses is a weir
// error naming the limit
let private offPool (what: string) (f: unit -> 'a) : System.Threading.Tasks.Task<'a> =
    let done' =
        System.Threading.Tasks.TaskCompletionSource<'a>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let t =
        System.Threading.Thread(
            (fun () ->
                try
                    done'.SetResult(f ())
                with ex ->
                    done'.SetException ex),
            IsBackground = true
        )

    Session.startThread what t
    done'.Task

// the one starter: the shared spawn plus the stdin writer, which pulls
// the input seq lazily as the pipe accepts (laziness reaches inputs too).
// The second result rethrows the PRODUCER's raise (or exit) on the
// calling thread: only a failed write — the child closed its stdin, as
// `head -1` does — is the pipe's business and stays silent. Call it
// once the child has exited: the fault is recorded before stdin closes,
// so the child's EOF orders after it, and nothing waits on a producer
// that is still blocked.
let private start (redirectOut: bool) (redirectErr: bool) (s: Spec) : Process * (unit -> unit) =
    let p = spawn redirectOut redirectErr s.Input.IsSome s.Cwd s.Ambient s.Prog s.Args s.Env
    let fault = ref (null: exn)

    match s.Input with
    | Some lines ->
        try
            offPool "a command's stdin feed" (fun () ->
                try
                    try
                        // a child's stdin is data: LF on every platform
                        // [D:lf-output] — WriteLine's Environment.NewLine
                        // fed \r\n into child hashes on Windows
                        p.StandardInput.NewLine <- "\n"
                        use e = lines.GetEnumerator()
                        let mutable go = true

                        while go do
                            let more =
                                try
                                    e.MoveNext()
                                with ex ->
                                    System.Threading.Volatile.Write(&fault.contents, ex)
                                    false

                            if more then
                                try
                                    p.StandardInput.WriteLine e.Current
                                with _ ->
                                    go <- false
                            else
                                go <- false
                    with _ ->
                        ()
                finally
                    try
                        p.StandardInput.Close()
                    with _ ->
                        ())
            |> ignore
        with _ ->
            Session.killTree p
            reraise ()
    | None -> ()

    let rethrowFault () =
        match System.Threading.Volatile.Read &fault.contents with
        | null -> ()
        | ex -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()

    p, rethrowFault

// pipe reads a cut stops [D:pfirst-join] [D:scope-tree]: the reader is
// over the child's stream wrapped by Session.cutStream, so the cut is
// checked per fill, never per line
let private readLine (r: System.IO.StreamReader) : string = r.ReadLine()

let private readChars (r: System.IO.StreamReader) (buf: char[]) : int = r.Read(buf, 0, buf.Length)

let private readBytes (s: System.IO.Stream) (buf: byte[]) (off: int) (len: int) : int = s.Read(buf, off, len)

// tree-kill then reap — the lifecycle tail shared by the streaming
// consumers (complete disposes without killing: it has already read
// both pipes to the end, unless a cut stopped the reads)
let private reap (p: Process) =
    // settled before any exit [D:kill-settle]
    Session.killTree p

    try
        p.WaitForExit()
    with _ ->
        ()

/// a command's nonzero exit as a typed failure [D:repl-cmd-fail]: the
/// REPL catches this specifically to render a bare command statement's
/// failure as a quiet exit-code status (there is no `$?`, so the code
/// must show), while a script — or a value/reifier position — keeps
/// the loud raise. The Message is byte-identical to the old failwith,
/// so every existing catcher (the script runner, the top-level guard,
/// `orFail`'s siblings) renders exactly as before.
type CommandFailure(code: int, signalNote: string, shown: string) =
    inherit exn($"command failed with exit code {code}{signalNote}: {Weir.Types.quoteData shown}")
    member _.Code = code
    member _.SignalNote = signalNote
    member _.Shown = shown
    /// raised outside a cleanup, where weir's signal arriving first would
    /// have made the death the teardown's [D:settle-pending]
    member val SignalMayOwn = false with get, set

/// a script's fatal error that is a signal's after all: a command outside
/// a cleanup that died 129-143 took a signal weir's own reached later —
/// Some of the teardown's code, so the script dies by it with no message
/// [D:settle-pending]
let fatalTeardown (ex: exn) : int option =
    match ex with
    | :? CommandFailure as cf when cf.SignalMayOwn ->
        match Session.settleFatal cf.Code with
        | 0 -> None
        | c -> Some c
    | _ -> None

let private raiseFailure (s: Spec) (code: int) : unit =
    let shown = showArgv (s.Prog :: s.Args)

    let signalNote =
        // 128+N = terminated by signal N; name the common ones so a
        // cancelled fzf reads as a cancel, not a mystery number
        match code with
        | 130 -> " (SIGINT — interrupted/cancelled)"
        | 143 -> " (SIGTERM — terminated)"
        | 137 -> " (SIGKILL — killed)"
        | c when c > 128 && c < 165 -> $" (signal {c - 128})"
        | _ -> ""

    raise (CommandFailure(code, signalNote, shown, SignalMayOwn = not (Session.inCleanup ())))

// `statement`: the command's output is unused (a statement relays or
// inherits it), so a death the cleanup pass covers truncates no value
let private raiseNonzero (statement: bool) (p: Process) (s: Spec) (code: int) : unit =
    // a child killed by a teardown is the teardown's, not a failure
    // [D:signal-main-unwind] — on an arm too, which the teardown cuts
    // [D:teardown-kill-not-failure]; a terminal ^C's death likewise
    // [D:sigint-terminal-group]
    Session.settleInterrupt code
    Session.checkTeardown ()
    Session.checkCancelled ()

    // a REPL ^C's death is the interrupt, inside a scope's body too
    // [D:repl-interrupt]
    if code = 130 then
        Session.settleReplInterrupt code |> ignore
        Session.checkInterrupted ()

    // a cleanup's statement command the teardown's own signal killed (a
    // group-directed TERM/HUP) is the signal's: the cleanup runs on whole
    // [D:cleanup-group-signal]
    let running =
        let mutable o = null
        not (startedAfterSignal.TryGetValue(p, &o))

    if not (Session.cleanupKilledByTeardown statement running code) then
        raiseFailure s code

// ---- the consumers (the output axis) -------------------------------

// stdout as a lazy line seq; raise-at-force on nonzero; stderr inherits
let private linesOfWith (statement: bool) (s: Spec) : seq<string> =
    seq {
        let p, producerFault = start true false s
        use _ = p

        try
            let out = Session.cutReader p p.StandardOutput
            let mutable line = readLine out

            while line <> null do
                yield line
                line <- readLine out

            p.WaitForExit()
            producerFault ()

            if p.ExitCode <> 0 then
                raiseNonzero statement p s p.ExitCode
        finally
            reap p
    }

let linesOf (s: Spec) : seq<string> = linesOfWith false s

/// a statement command's lines (`cmd` armed as `cmd |print`): its output
/// is relayed, never a value [D:cleanup-group-signal]
let statementLinesOf (s: Spec) : seq<string> = linesOfWith true s

// the chunk-level relay [D:stream-echo]: onText receives content the
// moment it arrives — partials included, so an interactive prompt shows
// before its newline — and onBreak marks each terminator (\n, \r,
// \r\n: exactly ReadLine's split, so the segment stream is linesOf's
// lines with immediacy). Raises nonzero like every forced spawn.
let streamSegmentsOf (s: Spec) (onText: string -> unit) (onBreak: unit -> unit) : unit =
    let p, producerFault = start true false s
    use _ = p

    try
        let out = Session.cutReader p p.StandardOutput
        let buf = Array.zeroCreate<char> 4096
        let seg = System.Text.StringBuilder()

        let flushSeg () =
            if seg.Length > 0 then
                onText (seg.ToString())
                seg.Clear() |> ignore

        let mutable pendingCR = false
        let mutable n = readChars out buf

        while n > 0 do
            for i in 0 .. n - 1 do
                let c = buf[i]

                if c = '\r' then
                    flushSeg ()
                    onBreak ()
                    pendingCR <- true
                elif c = '\n' then
                    if pendingCR then
                        pendingCR <- false
                    else
                        flushSeg ()
                        onBreak ()
                else
                    pendingCR <- false
                    seg.Append c |> ignore

            flushSeg ()
            n <- readChars out buf

        p.WaitForExit()
        producerFault ()

        if p.ExitCode <> 0 then
            raiseNonzero true p s p.ExitCode
    finally
        reap p

// colour from the child [D:colour-inherit]: a bare statement command
// at a tty spawns with stdout inherited — the child sees the terminal
// (isatty true, colour on) and weir never holds the bytes, so the
// relay's guard/threshold do not apply (bash's posture: the child
// chose its bytes for a terminal it can see). Stderr was always
// inherited on this path; the raise contract is every forced spawn's.
let runInherited (s: Spec) : unit =
    let p, producerFault = start false false s
    use _ = p

    try
        p.WaitForExit()
        producerFault ()

        if p.ExitCode <> 0 then
            raiseNonzero true p s p.ExitCode
    finally
        reap p

// process replacement [D:exec] — execvp, not spawn: the current weir
// image is replaced by the command and keeps its pid, so as a container
// entrypoint (PID 1) the app receives signals directly, with no weir
// layer to forward or reap. Does not return on POSIX success. Windows
// has no execve (CreateProcess only), so it spawns the child in the
// foreground, waits, and returns the exit code for the caller to exit
// with — the same observable end (weir gone, the app's status is the
// script's) minus the pid handoff. The env overlay and cwd land on
// this process first, so the replaced image inherits them; execvp
// searches PATH for a bare name.
[<System.Runtime.InteropServices.DllImport("libc",
                                           SetLastError = true,
                                           CharSet = System.Runtime.InteropServices.CharSet.Ansi)>]
extern int private execvp(string file, string[] argv)

// execvp reads the C `environ`, which .NET's SetEnvironmentVariable
// does not sync to on Unix [D:exec] — the env overlay must land via
// libc setenv (overwrite = 1) for the replacement image to inherit it.
[<System.Runtime.InteropServices.DllImport("libc",
                                           SetLastError = true,
                                           CharSet = System.Runtime.InteropServices.CharSet.Ansi)>]
extern int private setenv(string name, string value, int overwrite)

let exec (s: Spec) : unit =
    // runtime plan refusal [D:plan-proc-runtime-guard]: process
    // replacement must obey the guard `spawn` enforces — POSIX execvp
    // does not funnel through `spawn`, so an indirect `exec` inside a
    // `plan` block (a helper that invokes it) would otherwise run and
    // replace the image instead of being refused. Applied here
    // directly, with the same message spawn raises.
    if Session.planGuardActive () then
        failwith
            $"'{s.Prog}' runs a command, and commands are refused inside 'plan' — a spawned binary reads and writes opaquely, so its effects cannot be captured; plan covers weir-native mutation only (File/Dir/Http)"

    importRefusal s.Prog

    // the same boundary spawn enforces [D:spawn-nul-funnel]
    nulRefusal "the command program name" s.Prog

    if s.Prog = "" then
        failwith "the command program name is empty — nothing to run"

    for a in s.Args do
        nulRefusal "a command argument" a

    let ambient =
        match s.Ambient with
        | Some snap -> snap
        | None -> Session.envOverlay () |> List.rev |> List.collect id

    let isWindows = System.OperatingSystem.IsWindows()

    for k, v in List.append ambient s.Env do
        nulRefusal $"the env key '{k}'" k
        nulRefusal $"the env value for '{k}'" v
        System.Environment.SetEnvironmentVariable(k, v)
        // POSIX execvp reads the C environ, not .NET's managed copy —
        // setenv so the replacement inherits the overlay; Windows spawns
        // (CreateProcess inherits the process env block set above)
        if not isWindows then
            setenv (k, v, 1) |> ignore

    match s.Cwd with
    | Some wd when System.IO.Directory.Exists wd -> System.IO.Directory.SetCurrentDirectory wd
    | Some wd -> failwith $"the working directory no longer exists: {Weir.Types.quoteData wd}"
    | None -> ()

    if System.OperatingSystem.IsWindows() then
        // no execve: run foreground, then hard-exit with the child's
        // code (no scope unwind — exec discards the image, matching
        // POSIX)
        let p, _ = start false false s
        p.WaitForExit()
        let code = p.ExitCode
        reap p
        System.Environment.Exit code
    else
        // argv[0] is the program name by convention; the array is
        // NULL-terminated (the null element marshals to a NULL pointer)
        let argv = Array.append (List.toArray (s.Prog :: s.Args)) [| null |]
        execvp (s.Prog, argv) |> ignore
        // execvp returns only on failure — success replaced the image
        failwith $"command not found or not executable: {Weir.Types.quoteData s.Prog}"

// stdout relayed to the console as it arrives; the code as the result
// [D:exit-reifiers]: output goes to the human, the code is the meaning
let streamCodeOf (s: Spec) : int =
    let p, producerFault = start true false s
    use _ = p

    try
        let out = Session.cutReader p p.StandardOutput
        let mutable line = readLine out

        while line <> null do
            System.Console.Out.WriteLine line
            line <- readLine out

        p.WaitForExit()
        producerFault ()
        Session.settleReplInterrupt p.ExitCode
    finally
        reap p

// ---- capture representation [D:capture-buffer]: one byte buffer per
// stream + line offsets; stdout/stderr are lazy views decoding a
// string per pull. Same observable seq<string>, ~2x the text in RSS
// instead of ~18x (per-string object overhead + UTF-16 were the old
// cost). Offsets are int into one array — a single capture caps at
// the .NET ~2GB array bound, where the old representation met OOM at
// a fraction of that text anyway. The view holds the buffer alive
// while referenced (the same data the old lists held).

let private utf8 = System.Text.Encoding.UTF8

// fixed-size segments are the storage — no doubling, no final
// assembly copy, so peak touched pages ≈ the text itself (a growable
// array's doubling+trim touched ~3x and MaxRSS keeps LOH pages)
let private segBits = 22 // 4MB
let private segSize = 1 <<< segBits
let private segMask = segSize - 1

type private Segments =
    { Segs: byte[][]
      Total: int }

    member this.At(g: int) : byte = this.Segs[g >>> segBits][g &&& segMask]

// one stream's capture as it fills
type private SegmentsBuilder() =
    let segs = ResizeArray<byte[]>()
    let mutable seg = Array.zeroCreate<byte> segSize
    let mutable fill = 0

    /// one read into the buffer; 0 at EOF
    member _.Fill(stream: System.IO.Stream) : int =
        let n = readBytes stream seg fill (segSize - fill)
        fill <- fill + n

        if fill = segSize then
            segs.Add seg
            seg <- Array.zeroCreate segSize
            fill <- 0

        n

    member _.Done() : Segments =
        let total = segs.Count * segSize + fill

        if fill > 0 || segs.Count = 0 then
            // the tail segment, trimmed exact (small)
            System.Array.Resize(&seg, fill)
            segs.Add seg

        { Segs = segs.ToArray(); Total = total }

let private readAllBytes (stream: System.IO.Stream) : Segments =
    let b = SegmentsBuilder()

    while b.Fill stream > 0 do
        ()

    b.Done()

// a child's output pipes read to EOF on the calling thread, poll(2)
// choosing the next fill [D:pipe-threads]: a `| complete` costs its arm
// no second thread, and a loser blocked on its child holds no thread but
// its own. Windows reads every pipe after the first on a thread of its own
let private readAllPipes (streams: System.IO.Stream[]) : Segments[] =
    if Session.pollablePipes streams then
        let builders = streams |> Array.map (fun _ -> SegmentsBuilder())
        let live = Array.copy streams
        let ready = Array.zeroCreate<bool> streams.Length
        let mutable open' = streams.Length

        while open' > 0 do
            Session.waitPipes live ready

            for i in 0 .. live.Length - 1 do
                if ready[i] && builders[i].Fill live[i] = 0 then
                    live[i] <- null
                    open' <- open' - 1

        builders |> Array.map (fun b -> b.Done())
    else
        let rest =
            streams[1..] |> Array.map (fun s -> offPool "a command's output reader" (fun () -> readAllBytes s))

        let first = readAllBytes streams[0]
        Array.append [| first |] (rest |> Array.map Session.cutJoin)

// StreamReader's BOM detection is part of today's pinned contract: a
// UTF-16/32 BOM switches decoding. Those captures take the fallback
// (a real StreamReader over the bytes — byte-for-byte the old
// behavior); everything else takes the per-line fast path.
let private nonUtf8Bom (b: Segments) =
    let len = b.Total

    (len >= 2 && b.At 0 = 0xFFuy && b.At 1 = 0xFEuy)
    || (len >= 2 && b.At 0 = 0xFEuy && b.At 1 = 0xFFuy)
    || (len >= 4 && b.At 0 = 0uy && b.At 1 = 0uy && b.At 2 = 0xFEuy && b.At 3 = 0xFFuy)

let private utf8BomSkip (b: Segments) =
    if b.Total >= 3 && b.At 0 = 0xEFuy && b.At 1 = 0xBBuy && b.At 2 = 0xBFuy then
        3
    else
        0

// the rare-path reader (encoding-switch captures are tiny in practice)
let private readerOver (b: Segments) =
    let all = Array.zeroCreate<byte> b.Total
    let mutable off = 0

    for seg in b.Segs do
        System.Array.Copy(seg, 0, all, off, seg.Length)
        off <- off + seg.Length

    new System.IO.StreamReader(new System.IO.MemoryStream(all), utf8, true)

// decode one line: within a segment it is a zero-copy GetString; the
// rare boundary-crossing line assembles a small temp first. Newline
// bytes are hard UTF-8 sequence boundaries, so per-line decoding with
// the default replacement fallback matches the streaming decoder.
let private decodeAt (b: Segments) (start: int) (len: int) : string =
    if len = 0 then
        ""
    else
        let si = start >>> segBits

        if (start + len - 1) >>> segBits = si then
            utf8.GetString(b.Segs[si], start &&& segMask, len)
        else
            let tmp = Array.zeroCreate<byte> len

            for k in 0 .. len - 1 do
                tmp[k] <- b.At(start + k)

            utf8.GetString tmp

// two passes over the bytes: count lines, then fill exact (start,len)
// arrays — no growable-array churn, the offsets cost is 8B/line flat.
// isStdout selects the oracle-pinned rule: stdout = ReadLine (\n,
// \r\n, lone \r; empties kept); stderr = \n-only split, empties
// dropped, \r retained.
let private scanLines (b: Segments) (isStdout: bool) : int[] * int[] =
    let len = b.Total
    let start0 = utf8BomSkip b

    let pass (fill: bool) (starts: int[]) (lens: int[]) : int =
        let mutable count = 0
        let mutable i = start0
        let mutable ls = start0

        let add (s: int) (l: int) =
            if isStdout || l > 0 then
                if fill then
                    starts[count] <- s
                    lens[count] <- l

                count <- count + 1

        while i < len do
            let c = b.At i

            if c = 10uy then
                add ls (i - ls)
                i <- i + 1
                ls <- i
            elif c = 13uy && isStdout then
                add ls (i - ls)
                i <- if i + 1 < len && b.At(i + 1) = 10uy then i + 2 else i + 1
                ls <- i
            else
                i <- i + 1

        if ls < len then
            add ls (len - ls)

        count

    let n = pass false Array.empty Array.empty
    let starts = Array.zeroCreate<int> n
    let lens = Array.zeroCreate<int> n
    pass true starts lens |> ignore
    starts, lens

let private linesView (b: Segments) (isStdout: bool) : seq<string> =
    if nonUtf8Bom b then
        // byte-for-byte the old StreamReader behavior, eagerly (rare)
        use r = readerOver b

        if isStdout then
            let acc = ResizeArray<string>()
            let mutable line = r.ReadLine()

            while line <> null do
                acc.Add line
                line <- r.ReadLine()

            acc :> seq<string>
        else
            r.ReadToEnd().Split('\n') |> Array.filter (fun l -> l <> "") :> seq<string>
    else
        let starts, lens = scanLines b isStdout

        seq {
            for i in 0 .. starts.Length - 1 do
                yield decodeAt b starts[i] lens[i]
        }

// both pipes captured to completion; the record's raw material
let completedOf (s: Spec) : int * seq<string> * seq<string> =
    let p, producerFault = start true true s
    use _ = p

    let outBytes, errBytes =
        try
            let read =
                readAllPipes
                    [| Session.cutStream p p.StandardOutput.BaseStream
                       Session.cutStream p p.StandardError.BaseStream |]

            read[0], read[1]
        with _ ->
            // a cut stopped the reads before EOF: the tree dies before the
            // dispose, or the cut's own kill finds the Process disposed and
            // misses it (a Windows read wakes on the token at once)
            // [D:scope-tree]
            Session.killTree p
            reraise ()

    p.WaitForExit()
    producerFault ()
    Session.settleReplInterrupt p.ExitCode, linesView outBytes true, linesView errBytes false

// ---- the byte chain [D:byte-pipes]: raw command→command hops -------
// A hop makes no value, so weir does not hold the bytes
// ([D:colour-inherit]'s own rationale, completed): stage stdout copies
// 1:1 into the next stage's stdin — no decode, no line split, no
// appended newline. Only the chain's ends are edges: the head may take
// a text stdin feed (Input on the first spec), the tail's stdout is
// read under the same line law as any single command.
// the result is true when the DOWNSTREAM stopped reading first: the
// failed write closes the upstream's stdout too, so the producer gets
// EPIPE/SIGPIPE as in a shell pipe instead of blocking on a full pipe
// [D:pipe-early-exit]
let private pumpBytes
    (src: System.IO.Stream)
    (dst: System.IO.Stream)
    (closeDst: unit -> unit)
    : System.Threading.Tasks.Task<bool> =
    offPool "a pipe between two commands" (fun () ->
        let mutable broken = false

        try
            try
                let buf = Array.zeroCreate<byte> 65536
                let mutable n = src.Read(buf, 0, buf.Length)

                while n > 0 do
                    try
                        dst.Write(buf, 0, n)
                        n <- src.Read(buf, 0, buf.Length)
                    with _ ->
                        broken <- true
                        n <- 0

                if not broken then
                    dst.Flush()
            with _ ->
                ()
        finally
            closeDst ()

            if broken then
                try
                    src.Close()
                with _ ->
                    ()

        broken)

// start every stage and wire the hops: the head owns the (optional) text
// stdin feed, every downstream stage's stdin is the raw pipe from its left
let private startChain (tailOut: bool) (allErr: bool) (specs: Spec list) =
    let lastIdx = specs.Length - 1

    let headFault = ref ignore

    let procs =
        specs
        |> List.mapi (fun i sp ->
            if i = 0 then
                let p, fault = start true allErr sp
                headFault.Value <- fault
                sp, p
            else
                sp, spawn (i < lastIdx || tailOut) allErr true sp.Cwd sp.Ambient sp.Prog sp.Args sp.Env)

    let pumps =
        try
            procs
            |> List.pairwise
            |> List.map (fun ((_, a), (_, b)) ->
                pumpBytes
                    a.StandardOutput.BaseStream
                    b.StandardInput.BaseStream
                    (fun () ->
                        try
                            b.StandardInput.Close()
                        with _ ->
                            ()))
        with _ ->
            for _, p in procs do
                Session.killTree p

            reraise ()

    procs, pumps, headFault.Value

// the chain's failure, after every stage exited: the LEFTMOST failing
// stage — where the fault began — except a stage told to stop: its
// downstream quit reading first, so its exit is the pipe closing, not a
// fault [D:pipe-early-exit]
let private chainFailure (procs: (Spec * Process) list) (pumps: System.Threading.Tasks.Task<bool> list) =
    procs
    |> List.mapi (fun i (sp, p: Process) -> i, sp, p, p.ExitCode)
    |> List.tryFind (fun (i, _, _, code) -> code <> 0 && not (i < pumps.Length && Session.cutJoin pumps[i]))
    |> Option.map (fun (_, sp, p, code) -> sp, p, code)

let private chainLinesOfWith (statement: bool) (specs: Spec list) : seq<string> =
    match specs with
    | [] -> Seq.empty
    | [ one ] -> linesOfWith statement one
    | _ ->
        seq {
            let procs, pumps, producerFault = startChain true false specs

            try
                // the tail is the command→expression edge: same line law
                // as a single command
                let _, last = List.last procs
                let out = Session.cutReader last last.StandardOutput
                let mutable line = readLine out

                while line <> null do
                    yield line
                    line <- readLine out

                for _, p in procs do
                    p.WaitForExit()

                producerFault ()

                match chainFailure procs pumps with
                | Some(sp, p, code) -> raiseNonzero statement p sp code
                | None -> ()
            finally
                for _, p in procs do
                    reap p
        }

let chainLinesOf (specs: Spec list) : seq<string> = chainLinesOfWith false specs

/// a statement chain's lines (armed as `chain |print`) [D:cleanup-group-signal]
let chainStatementLinesOf (specs: Spec list) : seq<string> = chainLinesOfWith true specs

// a reified chain's code [D:chain-reifiers]: the leftmost failing
// stage's, or 0
let private chainCode procs pumps =
    match chainFailure procs pumps with
    | Some(_, _, code) -> code
    | None -> 0

// `chain | complete`: the tail's stdout, every stage's stderr in stage
// order, the chain's code [D:chain-reifiers]
let chainCompletedOf (specs: Spec list) : int * seq<string> * seq<string> =
    match specs with
    | [ one ] -> completedOf one
    | _ ->
        let procs, pumps, producerFault = startChain true true specs

        try
            let _, last = List.last procs

            let read =
                readAllPipes (
                    Array.ofList (
                        Session.cutStream last last.StandardOutput.BaseStream
                        :: (procs |> List.map (fun (_, p) -> Session.cutStream p p.StandardError.BaseStream))
                    )
                )

            let outBytes = read[0]

            for _, p in procs do
                p.WaitForExit()

            producerFault ()

            let errLines =
                read[1..] |> List.ofArray |> List.map (fun b -> linesView b false |> List.ofSeq) |> List.concat

            Session.settleReplInterrupt(chainCode procs pumps), linesView outBytes true, (errLines :> seq<string>)
        finally
            for _, p in procs do
                reap p

// `chain | exitCode` / `| orFail` / `| succeeds`: the tail's stdout
// relayed as it arrives, the chain's code as the result
// [D:chain-reifiers]
let chainStreamCodeOf (specs: Spec list) : int =
    match specs with
    | [ one ] -> streamCodeOf one
    | _ ->
        let procs, pumps, producerFault = startChain true false specs

        try
            let _, last = List.last procs
            let out = Session.cutReader last last.StandardOutput
            let mutable line = readLine out

            while line <> null do
                System.Console.Out.WriteLine line
                line <- readLine out

            for _, p in procs do
                p.WaitForExit()

            producerFault ()

            Session.settleReplInterrupt(chainCode procs pumps)
        finally
            for _, p in procs do
                reap p

// the statement chain at a tty [D:armed-inherit]: the byte chain with
// the tail's stdout inherited, so the last stage sees the terminal
// (`git log | less` pages) — runInherited's chain-shaped twin. Hops stay
// raw pipes; the leftmost failing stage raises, as in chainLinesOf.
let runChainInherited (specs: Spec list) : unit =
    match specs with
    | [] -> ()
    | [ one ] -> runInherited one
    | _ ->
        let procs, pumps, producerFault = startChain false false specs

        try
            for _, p in procs do
                p.WaitForExit()

            producerFault ()

            match chainFailure procs pumps with
            | Some(sp, p, code) -> raiseNonzero true p sp code
            | None -> ()
        finally
            for _, p in procs do
                reap p

// ---- the public wrappers (signatures unchanged) --------------------

// Child-env overlay [D:child-env-overlay]: `lines` is the empty
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
          Input = input
          Cwd = None
          Ambient = None }

let lines (prog: string) (args: string list) (input: seq<string> option) : seq<string> = linesWith [] prog args input

let streamCode (overlay: (string * string) list) (prog: string) (args: string list) : int =
    streamCodeOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = None
          Cwd = None
          Ambient = None }

let completeWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : int * seq<string> * seq<string> =
    completedOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = input
          Cwd = None
          Ambient = None }

let complete (prog: string) (args: string list) (input: seq<string> option) : int * seq<string> * seq<string> =
    completeWith [] prog args input

// a scoped background child [D:scoped-procs]: both streams spill to
// files (the parent's terminal never interleaves; Proc.tail and the
// poll-watch errors read them back), stdin closed — a child that reads
// gets EOF. Registered with the exit hook by the caller, which creates
// both files.
/// returns the process and a bounded drain: joining the pump threads
/// guarantees the spill holds the child's last output before a reader
/// composes an error from it — a fast-exiting child raced the pumps
/// and the died-at-startup message lost its cause [D:scoped-procs]
let startSpilled
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (outPath: string)
    (errPath: string)
    : Process * (unit -> unit) =
    let p = spawn true true true None None prog args overlay

    // stdin closed here, not through the writer task: a scoped child
    // that reads must see EOF at once, with no thread between
    p.StandardInput.Close()

    // per-chunk flush: the spill must be readable while the child runs
    // (tail during the scope) — CopyTo's big buffer would sit on lines
    let pump (src: System.IO.Stream) (path: string) =
        let t =
            System.Threading.Thread(fun () ->
                try
                    // the caller created the file, owner-only [D:private-temp];
                    // opened, never created, so a swept spill dir gains no
                    // entry [D:hard-exit]
                    let opts =
                        System.IO.FileStreamOptions(
                            Mode = System.IO.FileMode.Open,
                            Access = System.IO.FileAccess.Write,
                            Share = System.IO.FileShare.ReadWrite
                        )

                    use dst = new System.IO.FileStream(path, opts)

                    let buf = Array.zeroCreate 8192
                    let mutable n = src.Read(buf, 0, buf.Length)

                    while n > 0 do
                        dst.Write(buf, 0, n)
                        dst.Flush()
                        n <- src.Read(buf, 0, buf.Length)
                with _ ->
                    ())

        t.IsBackground <- true
        t.Start()
        t

    // cut as the flow that opened the scope is: after a cut the drain never
    // waits for the EOF of a pipe a descendant the kill missed still holds
    // [D:wait-after-cut]
    let tOut = pump (Session.cutStream p p.StandardOutput.BaseStream) outPath
    let tErr = pump (Session.cutStream p p.StandardError.BaseStream) errPath

    let drain () =
        // bounded: a dead child's pipes hit EOF at once, so the join is
        // instant in the case that matters; the cap only guards a live
        // caller from a chatty child
        tOut.Join 2000 |> ignore
        tErr.Join 2000 |> ignore

    p, drain

/// tree-kill then reap, idempotent — the scope-exit tail and Proc.stop
/// share it (reap's own try/catch absorbs an already-dead child)
let stopTree (p: Process) : unit = reap p

/// the program a spec carries [D:overlay-path]: the NUL refusal and a
/// path-like name's cwd resolution; a bare name stays bare and resolves
/// at the spawn, against the PATH the child runs with
let specProg (prog: string) : string =
    nulRefusal "a dynamic command head" prog

    if prog.Contains '/' then Session.resolve prog else prog

let resolveProg (prog: string) : string =
    // the NUL refusal reaches the resolve too [D:spawn-nul-funnel]: a
    // path-like name (`/`-bearing) resolves through Path.GetFullPath
    // before the spawn funnel, which raised a raw platform exception
    // ("Null character in path") on a NUL. Refuse here with the same
    // weir-shaped diagnostic the spawn boundary produces.
    nulRefusal "a dynamic command head" prog

    if prog.Contains '/' then
        Session.resolve prog
    elif System.OperatingSystem.IsWindows() && not (prog.Contains '\\') then
        // the checker's existence gate resolves x -> x.bat via PATHEXT;
        // the spawn must hand CreateProcess the same real file (it
        // appends only .exe itself) [D:windows-s2]
        Extern.resolveFile prog |> Option.defaultValue prog
    else
        prog
