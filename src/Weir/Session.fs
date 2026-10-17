module Weir.Session

// Session-as-value, arriving incrementally (the shape recorded when
// the thread-safety question first came up): the root session is
// process-global and single-threaded; parallel workers fork it — cd
// inside a worker is worker-local and dies at the join. Spawns and
// File ops read the ambient session at force time, unchanged.

// `exit n`'s carrier: an intentional exit, not an error — the runner
// returns the code silently instead of printing a located message.
// Declared here so the teardown below (signals, a closed stdout) can
// raise it; Eval re-exports it.
exception ExitRequest of code: int

let mutable private rootCwd: string =
    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Directory.GetCurrentDirectory())

// AsyncLocal, not ThreadLocal [D:tasks-underneath]: session scopes
// belong to the logical context (an arm's scope follows the arm across
// whatever thread resumes it), which is also correct under plain
// thread parallelism — each pmap arm enters/exits per invocation, so
// pool-thread reuse never leaks a scope. Value's default (null) is
// None for an option — the unset context falls to the root.
let private localCwd = System.Threading.AsyncLocal<string option>()

let Cwd: unit -> string =
    fun () ->
        match localCwd.Value with
        | Some c -> c
        | None -> rootCwd

let setCwd (path: string) : unit =
    // normalised at assignment, not at cd's return: `cd gh` and `cd gh/`
    // name one directory, but Path.GetFullPath preserves a trailing
    // separator, so the spelling of the argument leaked into a value
    // scripts compare, store and interpolate. Normalising here means
    // every relative resolution and every render inherits one shape —
    // the return value is only the first reader. The root keeps its
    // separator: TrimEndingDirectorySeparator trims only beyond the root.
    let path = System.IO.Path.TrimEndingDirectorySeparator path

    match localCwd.Value with
    | Some _ -> localCwd.Value <- Some path
    | None -> rootCwd <- path

// how many `within cd` scopes enclose the evaluation [D:ambient-capture-within]:
// a lazy value captures the cwd only inside one; outside, it reads the cwd
// current at enumeration. AsyncLocal so a parallel arm forked inside a
// scope inherits it and an arm's own scope stays its own.
let private withinCdDepth = System.Threading.AsyncLocal<int>()

let enterWithinCd () : unit = withinCdDepth.Value <- withinCdDepth.Value + 1

let exitWithinCd () : unit = withinCdDepth.Value <- max 0 (withinCdDepth.Value - 1)

// the ambient env overlay [D:within-scopes]: `within env` pushes a
// layer; every spawn applies ambient layers outer-first under any
// explicit sigil env, so inner (and explicit) keys win on collision.
// Same locality discipline as cwd: main thread mutates the root, a
// worker's first push forks a local stack over the root's snapshot.
let private rootEnvOverlay: (string * string) list list ref = ref []

let private localEnvOverlay =
    System.Threading.AsyncLocal<(string * string) list list option>()

/// newest layer first
let envOverlay () : (string * string) list list =
    match localEnvOverlay.Value with
    | Some s -> s
    | None -> rootEnvOverlay.Value

let pushEnvOverlay (pairs: (string * string) list) : unit =
    match localEnvOverlay.Value with
    | Some s -> localEnvOverlay.Value <- Some(pairs :: s)
    | None ->
        if localCwd.Value.IsSome then
            // on a worker: fork over the root's snapshot
            localEnvOverlay.Value <- Some(pairs :: rootEnvOverlay.Value)
        else
            rootEnvOverlay.Value <- pairs :: rootEnvOverlay.Value

let popEnvOverlay () : unit =
    match localEnvOverlay.Value with
    | Some(_ :: rest) -> localEnvOverlay.Value <- Some rest
    | Some [] -> ()
    | None ->
        match rootEnvOverlay.Value with
        | _ :: rest -> rootEnvOverlay.Value <- rest
        | [] -> ()

// the scope state a lazy value captures at eval [D:ambient-capture-within]:
// the cwd only under an enclosing `within cd`, the overlay only under an
// enclosing `within env` (the overlay is non-empty exactly then); None
// means nothing is captured — the value reads the live session at force
let scopedCwd () : string option =
    if withinCdDepth.Value > 0 then Some(Cwd()) else None

let scopedOverlay () : (string * string) list list option =
    match envOverlay () with
    | [] -> None
    | layers -> Some layers

/// the scoped overlay flattened outer-first, the spawn's apply order
let scopedAmbient () : (string * string) list option =
    scopedOverlay () |> Option.map (List.rev >> List.collect id)

// weir's own env reads see the overlay a child would [D:env-overlay-read]:
// newest layer first, the last pair within a layer wins (the spawn's
// apply order), the process env beneath. Names compare as the platform's
// env does (case-insensitive on Windows).
let private envNameCmp =
    if System.OperatingSystem.IsWindows() then
        System.StringComparer.OrdinalIgnoreCase
    else
        System.StringComparer.Ordinal

/// one var under the given layers (newest first)
let getEnvIn (layers: (string * string) list list) (name: string) : string option =
    layers
    |> List.tryPick (fun layer -> layer |> List.rev |> List.tryFind (fun (k, _) -> envNameCmp.Equals(k, name)))
    |> Option.map snd
    |> Option.orElseWith (fun () ->
        match System.Environment.GetEnvironmentVariable name with
        | null -> None
        | v -> Some v)

/// one var under the live overlay
let getEnv (name: string) : string option = getEnvIn (envOverlay ()) name

/// every var under the given layers (newest first), unordered
let envVarsIn (layers: (string * string) list list) : (string * string) list =
    let d = System.Collections.Generic.Dictionary<string, string>(envNameCmp)

    for e in System.Environment.GetEnvironmentVariables() |> Seq.cast<System.Collections.DictionaryEntry> do
        d[string e.Key] <- string e.Value

    for k, v in layers |> List.rev |> List.collect id do
        d[k] <- v

    [ for kv in d -> kv.Key, kv.Value ]

// worker lifecycle (Seq.pmap / Seq.piter)
// nesting-aware parallel ceilings [D:parallel-ladder]: the fan-out
// depth rides AsyncLocal beside cwd/env/raceGroup — read at spawn time
// to pick the default ceiling, so arms are bounded at creation and
// nothing ever waits on a slot another arm holds (the global-semaphore
// deadlock is unrepresentable)
let private parallelDepth = System.Threading.AsyncLocal<int>()

let parallelDepthNow () : int = parallelDepth.Value

let setParallelDepth (d: int) : unit = parallelDepth.Value <- d

// the plan-capture guard [D:plan-proc-runtime-guard]: a thread-local
// depth, not AsyncLocal — it mirrors PlanMode's capture stack (Eval),
// which is thread-local so a worker never inherits the capturing
// thread's frame. Proc's spawn point (compiled before Eval) consults
// this to refuse a process started while a plan captures on this
// thread — an indirect helper runs on the capturing thread, so the
// syntactic firstPlanRefusal (which follows top-level helpers, not a
// callable passed as a value) is backed by this runtime check. Kept minimal (a bool via depth) so the frame
// machinery and its Value dependency stay in Eval.
let private planGuardDepth = new System.Threading.ThreadLocal<int>(fun () -> 0)

/// is a plan capturing on this thread? (Proc's spawn-time refusal)
let planGuardActive () : bool = planGuardDepth.Value > 0

let enterPlanGuard () : unit = planGuardDepth.Value <- planGuardDepth.Value + 1

let exitPlanGuard () : unit = planGuardDepth.Value <- max 0 (planGuardDepth.Value - 1)

// the import guard [D:module-import-no-commands]: while a module body is
// evaluated, no command runs — on any thread (a `Seq.pmap` arm at import
// runs on a worker), so process-wide, not thread-local. Imports evaluate
// before the importer's first statement, so nothing else spawns then.
// Holds the importing modules' names, innermost first.
let mutable private importGuard: string list = []

/// the module being imported right now, if any (Proc's spawn-time refusal)
let importGuardModule () : string option =
    List.tryHead (System.Threading.Volatile.Read &importGuard)

let enterImportGuard (name: string) : unit =
    System.Threading.Volatile.Write(&importGuard, name :: importGuard)

let exitImportGuard () : unit =
    System.Threading.Volatile.Write(&importGuard, (match importGuard with _ :: t -> t | [] -> []))

let enterWorker (parentCwd: string) : unit = localCwd.Value <- Some parentCwd

let exitWorker () : unit =
    localCwd.Value <- None
    localEnvOverlay.Value <- None

let resolve (path: string) : string =
    // the run-time root guard [D:nul-path]: a NUL in the path makes
    // Path.GetFullPath throw a raw ArgumentException (SIGABRT with no
    // diagnostic) — this is the one resolution funnel every File/Proc/
    // completion builtin passes through, so refusing here turns the whole
    // run-time class into a located weir error (the builtins already
    // surface raises). NUL-free paths are untouched. Parse-time never
    // reaches this: Extern.exists pre-empts a NUL-bearing head as
    // not-found [D:nul-path].
    if path.Contains '\000' then
        failwith "path contains a NUL byte — paths are NUL-free; NUL-bearing data is binary, not a path"

    System.IO.Path.GetFullPath(System.IO.Path.Combine(Cwd(), path))

/// a temp dir only its owner can enter [D:private-temp] — 0700 on POSIX,
/// mkdtemp's convention: child output and scratch content are the script's
let createPrivateDir (dir: string) : unit =
    if System.OperatingSystem.IsWindows() then
        System.IO.Directory.CreateDirectory dir |> ignore
    else
        System.IO.Directory.CreateDirectory(
            dir,
            System.IO.UnixFileMode.UserRead
            ||| System.IO.UnixFileMode.UserWrite
            ||| System.IO.UnixFileMode.UserExecute
        )
        |> ignore

// pfirst's loser tree-kill [D:seq-pfirst]: an arm registers its live
// children in its race group (AsyncLocal — the registration follows
// the arm's logical context, the same keying as cwd/env). The winner
// condemns every other group: registered trees die, and a child
// spawned after condemnation dies at registration (the race window a
// plain bag would leak).
type RaceGroup() =
    let procs =
        System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Process>()

    let mutable condemned = false

    member _.Register(p: System.Diagnostics.Process) =
        if condemned then
            try
                p.Kill true
            with _ ->
                ()
        else
            procs.Add p

    member _.Condemn() =
        condemned <- true

        for p in procs do
            try
                p.Kill true
            with _ ->
                ()

let private raceGroup = System.Threading.AsyncLocal<RaceGroup option>()

let enterRace (g: RaceGroup) : unit = raceGroup.Value <- Some g
let exitRace () : unit = raceGroup.Value <- None

let registerChild (p: System.Diagnostics.Process) : unit =
    match raceGroup.Value with
    | Some g -> g.Register p
    | None -> ()

// script argv, for the Args module scanners (script-only semantics:
// the REPL leaves this empty)
let mutable ScriptArgs: string list = []

// the invoked script's path, for the Args `--help` usage line
// [D:argv-help-slots] — script-only, like ScriptArgs; the REPL/-e
// leave it empty, where usage omits the program name
let mutable EntryPath: string = ""


// ---- the temp-dir exit hook [D:exit-hook] --------------------------
// Registration, not scanning: the hook removes only directories this
// process created and still considers live — never a sweep of the temp
// root, so two concurrent weirs cannot clean up after each other. The
// hook is the backstop: a `within tmp` that exits cleanly deletes its
// dir and its registration, leaving the hook nothing to do. Installed
// lazily on the first registration — the shebang path never pays for it.

let private liveTmpDirs =
    System.Collections.Concurrent.ConcurrentDictionary<string, unit>()

let private hookInstalled = ref 0

// signal registrations must stay rooted for the process lifetime — a
// collected registration stops handling
let mutable private hookRoots: obj list = []

// the hook's second customer [D:scoped-procs]: live scoped processes,
// killed (tree) before the dirs go — a spilling child holds its spill
// dir open, so the order is load-bearing on Windows
let private liveProcs =
    System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process>()

// the teardown kill [D:signal-main-unwind]: spawning stops, then every
// live child's tree dies exactly once. Spawns hold the gate shared from
// Process.Start through registration, so the kill (exclusive) sees every
// child a worker started; a spawn after it refuses. The main thread
// settles the same kill before exiting, so the process never exits with
// a tree half-killed (a SIGSTOPped grandchild left orphaned).
let private spawnGate =
    new System.Threading.ReaderWriterLockSlim(System.Threading.LockRecursionPolicy.SupportsRecursion)

let mutable private spawningStopped = false

let private killedProcs =
    System.Collections.Generic.HashSet<System.Diagnostics.Process>(HashIdentity.Reference)

// an `always` block in progress on this logical flow [D:teardown-always-spawns]:
// the teardown stops the body's spawns, never a cleanup's — teardown
// commands are what `always` is for
let private cleanupDepth = System.Threading.AsyncLocal<int>()

/// run an `always` cleanup: spawns inside it start even after the teardown
/// kill, and a parallel fan-out inside it keeps taking arms
let runningCleanup (f: unit -> 'a) : 'a =
    cleanupDepth.Value <- cleanupDepth.Value + 1

    try
        f ()
    finally
        cleanupDepth.Value <- cleanupDepth.Value - 1

let inCleanup () = cleanupDepth.Value > 0

// ---- the main-thread teardown [D:signal-main-unwind] [D:stdout-epipe] --
// A signal or a closed stdout ends the script, but the teardown — every
// `always`, every scope's release — runs on the MAIN thread, never in the
// signal handler: the handler records the exit code, kills the scoped
// children (what the main thread waits on) and wakes interruptible waits;
// the main thread raises ExitRequest at its next evaluation or wait
// boundary and unwinds through its scopes, so no statement runs after the
// request. Registered by the script runner and -e; without a registered
// main thread (the REPL) the handler keeps the in-handler sweep.

let mutable private mainThreadId = 0
let mutable private teardownCode = 0
let mutable private teardownRaised = 0

/// interruptible waits block on this; set when a teardown is requested
let teardownWake = new System.Threading.ManualResetEventSlim(false)

let registerMainThread () : unit =
    mainThreadId <- System.Threading.Thread.CurrentThread.ManagedThreadId

let private onMainThread () =
    mainThreadId <> 0
    && System.Threading.Thread.CurrentThread.ManagedThreadId = mainThreadId

// what a worker's waits hand to an API (an Http send) once a teardown is
// requested [D:teardown-joins-arms]
let private teardownCut = new System.Threading.CancellationTokenSource()

/// record a teardown's exit code — the first request wins
let requestTeardown (code: int) : unit =
    System.Threading.Interlocked.CompareExchange(&teardownCode, code, 0) |> ignore
    teardownWake.Set()

    try
        teardownCut.Cancel()
    with _ ->
        ()

// ---- cooperative cancellation [D:pfirst-join] [D:repl-interrupt] -------
// Three requests stop a flow at its next runtime-owned boundary: a pfirst
// arm that lost the race (its flow token, AsyncLocal — the arm's logical
// context), a ^C at the REPL (a session token, global — every wait of the
// evaluation), and a requested teardown for every flow off the main
// thread [D:teardown-joins-arms]. Each raises an
// OperationCanceledException, which `try` and retry pass through as they
// pass `exit`.

/// a pfirst loser stopped at a boundary — swallowed by the race
type Cancelled() =
    inherit System.OperationCanceledException("a pfirst arm that lost the race was stopped")

/// a ^C at the REPL cut a runtime-owned wait — the evaluation ends
type Interrupted() =
    inherit System.OperationCanceledException("interrupted (SIGINT — interrupted/cancelled)")

let private flowCancel = System.Threading.AsyncLocal<System.Threading.CancellationToken>()

// races in flight: the fast path of the per-builtin check reads this
// alone, never the AsyncLocal
let mutable private racesLive = 0

/// the flow's cancel token (CancellationToken.None outside a race)
let flowToken () : System.Threading.CancellationToken = flowCancel.Value

let enterFlow (t: System.Threading.CancellationToken) : unit = flowCancel.Value <- t
let exitFlow () : unit = flowCancel.Value <- System.Threading.CancellationToken.None

/// run a race: the counter gates the per-builtin check
let racing (f: unit -> 'a) : 'a =
    System.Threading.Interlocked.Increment &racesLive |> ignore

    try
        f ()
    finally
        System.Threading.Interlocked.Decrement &racesLive |> ignore

// a lost race stops the body, never an `always` cleanup — the race joins
// the loser, so its cleanup still runs before pfirst returns
let private flowCut () =
    System.Threading.Volatile.Read &racesLive <> 0
    && flowCancel.Value.IsCancellationRequested
    && not (inCleanup ())

// a requested teardown cuts every flow off the main thread outside an
// `always` cleanup [D:teardown-joins-arms] — the main thread unwinds by
// ExitRequest, joining the arms first
let private teardownCuts () =
    mainThreadId <> 0 && not (onMainThread ()) && not (inCleanup ())

/// raise Cancelled when this flow lost a race or is torn down — the effect
/// boundary
let checkCancelled () : unit =
    if
        flowCut ()
        || (System.Threading.Volatile.Read &teardownCode <> 0 && teardownCuts ())
    then
        raise (Cancelled())

// the REPL's interrupt, one evaluation at a time: the first ^C cancels
// `replCut` (every wait outside an `always` cleanup), a further ^C also
// `replCutCleanup` — a cleanup runs as written unless ^C is pressed again
let mutable private replCut: System.Threading.CancellationTokenSource = null
let mutable private replCutCleanup: System.Threading.CancellationTokenSource = null
let mutable private replInterrupts = 0

/// arm a fresh interrupt for one REPL evaluation
let beginReplEval () : unit =
    replInterrupts <- 0
    replCut <- new System.Threading.CancellationTokenSource()
    replCutCleanup <- new System.Threading.CancellationTokenSource()

/// disarm after the evaluation (a late ^C then cancels nothing)
let endReplEval () : unit =
    let a, b = replCut, replCutCleanup
    replCut <- null
    replCutCleanup <- null

    for c in [ a; b ] do
        if not (isNull c) then
            c.Dispose()

/// the REPL's SIGINT handler: cancel the evaluation's waits
let interruptRepl () : unit =
    let n = System.Threading.Interlocked.Increment &replInterrupts

    try
        match replCut with
        | null -> ()
        | c -> c.Cancel()

        if n >= 2 then
            match replCutCleanup with
            | null -> ()
            | c -> c.Cancel()
    with :? System.ObjectDisposedException ->
        ()

/// the REPL interrupt token for this flow's waits
let private replToken () : System.Threading.CancellationToken =
    let src = if inCleanup () then replCutCleanup else replCut

    match src with
    | null -> System.Threading.CancellationToken.None
    | c ->
        try
            c.Token
        with :? System.ObjectDisposedException ->
            System.Threading.CancellationToken.None

/// raise Interrupted when ^C cut this evaluation's waits
let checkInterrupted () : unit =
    if (replToken ()).IsCancellationRequested then
        raise (Interrupted())

/// the tokens a runtime-owned wait honours, beyond the teardown
let private waitTokens () : System.Threading.CancellationToken list =
    [ (if inCleanup () then System.Threading.CancellationToken.None else flowCancel.Value)
      replToken () ]
    |> List.filter (fun t -> t.CanBeCanceled)

/// raise whichever cancel stopped this flow (the race's, then ^C's)
let checkCancels () : unit =
    checkCancelled ()
    checkInterrupted ()

// the wait tokens plus, off the main thread, the teardown's
// [D:teardown-joins-arms]
let private cutTokens () : System.Threading.CancellationToken list =
    waitTokens ()
    @ (if teardownCuts () then [ teardownCut.Token ] else [])

/// run `f` with one token that cancels when this flow's waits are cut —
/// for a wait the runtime hands to an API (an Http send); `checkCancels`
/// after it names why
let withWaitToken (f: System.Threading.CancellationToken -> 'a) : 'a =
    match cutTokens () with
    | [] -> f System.Threading.CancellationToken.None
    | [ t ] -> f t
    | ts ->
        use linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(Array.ofList ts)
        f linked.Token

/// run a process start + registration inside the spawn gate; refuses once
/// the teardown kill has run, except inside an `always` cleanup
let gatedSpawn (start: unit -> 'a) : 'a =
    // a spawn is an effect boundary [D:pfirst-join]
    checkCancelled ()
    spawnGate.EnterReadLock()

    try
        if spawningStopped && not (inCleanup ()) then
            failwith "the script is being torn down — no new command starts"

        start ()
    finally
        spawnGate.ExitReadLock()

/// stop spawning and tree-kill every live child not already killed
let private killLiveChildren () : unit =
    spawnGate.EnterWriteLock()

    try
        spawningStopped <- true

        for kv in liveProcs do
            if killedProcs.Add kv.Value then
                try
                    kv.Value.Kill true
                with _ ->
                    ()
    finally
        spawnGate.ExitWriteLock()

// pending `always` cleanups [D:within-always]: LIFO, run by the exit
// hook on signals/hard exits; a completed scope deregisters (its own
// finally already ran the cleanup). Entries are removed before running
// so a second hook firing (SIGTERM then ProcessExit) cannot run one
// twice.
let private liveAlways =
    System.Collections.Concurrent.ConcurrentDictionary<int, unit -> unit>()

let private alwaysCounter = ref 0

let private sweepLiveTmpDirs () =
    // runs at exit while finallys may also be running: a vanished dir is
    // benign (the double-delete pin's territory), and the hook must
    // never throw during exit
    for id in liveAlways.Keys |> Seq.sortDescending do
        match liveAlways.TryRemove id with
        | true, cleanup ->
            try
                cleanup ()
            with _ ->
                ()
        | _ -> ()

    for kv in liveProcs do
        try
            kv.Value.Kill true
        with _ ->
            ()

        try
            kv.Value.WaitForExit()
        with _ ->
            ()

    for kv in liveTmpDirs do
        try
            System.IO.Directory.Delete(kv.Key, true)
        with _ ->
            ()

/// the REPL survives SIGINT (set once by Repl.run) [D:repl-isig]: the
/// exit-hook sweep must not fire for a signal the session outlives —
/// it deletes live within-tmp dirs, which is correct only when dying
let replSurvivesSigint = ref false

// libc, for the detached-SIGINT reset [D:signal-teardown]. `signal`
// returns the previous disposition; we peek-and-restore to convert an
// inherited SIG_IGN to SIG_DFL without clobbering anything else. `open`/
// `close` probe for a controlling terminal (/dev/tty).
module private Libc =
    open System.Runtime.InteropServices

    // sighandler_t is a function pointer; SIG_DFL = 0, SIG_IGN = 1,
    // SIG_ERR = -1 — the three sentinels are all we compare against, so
    // nativeint carries them faithfully without a delegate marshal.
    [<DllImport("libc", SetLastError = true)>]
    extern nativeint signal(int signum, nativeint handler)

    [<DllImport("libc", SetLastError = true)>]
    extern int ``open``(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int close(int fd)

    [<DllImport("libc", SetLastError = true)>]
    extern int flock(int fd, int operation)

    let SIG_DFL: nativeint = nativeint 0
    let SIG_IGN: nativeint = nativeint 1
    let SIGINT = 2
    let O_RDWR = 2

    /// true when the process has a controlling terminal — /dev/tty opens
    /// only then (ENXIO otherwise). A `nohup weir &` keeps its tty, so
    /// this is the gate that preserves the nohup convention.
    let hasControllingTty () : bool =
        let fd = ``open`` ("/dev/tty", O_RDWR)

        if fd >= 0 then
            close fd |> ignore
            true
        else
            false

    /// convert an inherited SIG_IGN on SIGINT to SIG_DFL, leaving every
    /// other disposition untouched (peek by setting SIG_DFL, restore if
    /// the previous was not SIG_IGN). Returns whether a reset happened.
    let resetSigintIfIgnored () : bool =
        let prev = signal (SIGINT, SIG_DFL)

        if prev = SIG_IGN then
            true // leave SIG_DFL in place — the registration can now bind
        else
            // not ignored: put the real disposition back untouched
            signal (SIGINT, prev) |> ignore
            false

/// an exclusive flock on an open file, without blocking [D:lock-advisory]:
/// true when taken, false while another open file description holds it.
/// Released when the stream closes (the kernel's, kill -9 included).
let tryFlockExclusive (fs: System.IO.FileStream) : bool =
    let LOCK_EX, LOCK_NB = 2, 4
    let EINTR = 4
    let EWOULDBLOCK = if System.OperatingSystem.IsLinux() then 11 else 35
    let fd = int (fs.SafeFileHandle.DangerousGetHandle())

    let rec go () =
        if Libc.flock (fd, LOCK_EX ||| LOCK_NB) = 0 then
            true
        else
            match System.Runtime.InteropServices.Marshal.GetLastPInvokeError() with
            | e when e = EINTR -> go ()
            | e when e = EWOULDBLOCK -> false
            | e -> failwith $"within lock: cannot lock {fs.Name} (flock errno {e})"

    go ()

// SIGPIPE handled, not ignored, so children start at the default
// [D:pipe-early-exit]: the runtime ignores SIGPIPE, an ignored signal
// stays ignored across exec, and a child told to stop by a closed pipe
// then printed "write error: Broken pipe" instead of exiting quietly as
// under a shell. A HANDLED signal resets to the default at exec. The
// handler is libc's getpid — async-signal-safe, side-effect free, and
// its ignored int argument is ABI-harmless — because a managed delegate
// is not safe to run in signal context; weir's own writes still fail
// with EPIPE (the handler returns, the write reports the error, which
// the stdout writer turns into the 141 teardown [D:stdout-epipe]).
// Installed once before the first spawn; POSIX only.
let sigpipeForChildren: Lazy<unit> =
    lazy
        (if not (System.OperatingSystem.IsWindows()) then
             let lib =
                 [ "libc.so.6"; "libc"; "/usr/lib/libSystem.B.dylib" ]
                 |> List.tryPick (fun name ->
                     match System.Runtime.InteropServices.NativeLibrary.TryLoad name with
                     | true, h -> Some h
                     | _ -> None)

             match lib with
             | Some h ->
                 match System.Runtime.InteropServices.NativeLibrary.TryGetExport(h, "getpid") with
                 | true, fp -> Libc.signal (13, fp) |> ignore
                 | _ -> ()
             | None -> ())

/// the requested exit code, or `code` when none was requested — the
/// runner's result, so a request racing a statement's normal end still
/// exits with the request's code
let exitCodeOr (code: int) : int =
    match System.Threading.Volatile.Read &teardownCode with
    | 0 -> code
    | c ->
        // a requested teardown's kill completes before the exit
        killLiveChildren ()
        c

/// a parallel worker stops taking arms once a teardown is requested
/// (a signal, a closed stdout) [D:teardown-stops-arms] — unless the
/// fan-out runs inside an `always` cleanup
let stopTakingArms () : bool =
    System.Threading.Volatile.Read &teardownCode <> 0 && not (inCleanup ())

/// the requested teardown's exit code, or 0 when none is requested
let teardownRequested () : int = System.Threading.Volatile.Read &teardownCode

/// raise the requested teardown on the main thread, once: the unwind
/// then runs the cleanups as ordinary code (which must not be cut short
/// by the same request again)
let checkTeardown () : unit =
    let c = System.Threading.Volatile.Read &teardownCode

    if
        c <> 0
        && onMainThread ()
        && System.Threading.Interlocked.Exchange(&teardownRaised, 1) = 0
    then
        raise (ExitRequest c)

// a main thread not yet unwinding waits interruptibly; elsewhere (a
// worker, or a cleanup already running for the request) waits run as
// written
let private interruptible () =
    onMainThread () && System.Threading.Volatile.Read &teardownRaised = 0

// what a runtime-owned wait wakes on: the teardown (main thread, not yet
// unwinding; any other flow outside a cleanup [D:teardown-joins-arms]), a
// lost race, a REPL ^C [D:repl-interrupt]
let private wakeHandles () : System.Threading.WaitHandle list =
    (if interruptible () then [ teardownWake.WaitHandle ] else [])
    @ (cutTokens () |> List.map (fun t -> t.WaitHandle))

let private raiseWoken () =
    checkTeardown ()
    checkCancels ()

/// sleep, cut short by a teardown request on the main thread, a lost
/// race or a REPL ^C (each then raises)
let sleepInterruptible (span: System.TimeSpan) : unit =
    checkCancels ()

    match wakeHandles () with
    | [] -> System.Threading.Thread.Sleep span
    | hs ->
        System.Threading.WaitHandle.WaitAny(Array.ofList hs, span) |> ignore
        raiseWoken ()

/// wait `span` or until `token` cancels — true when the token cancelled
/// first; a teardown request, a lost race or a REPL ^C raises
let waitOrCancel (span: System.TimeSpan) (token: System.Threading.CancellationToken) : bool =
    checkCancels ()

    match wakeHandles () with
    | [] ->
        try
            System.Threading.Tasks.Task.Delay(span, token).Wait()
            false
        with :? System.AggregateException as e when (e.InnerException :? System.OperationCanceledException) ->
            true
    | hs ->
        let i =
            System.Threading.WaitHandle.WaitAny(Array.ofList (token.WaitHandle :: hs), span)

        raiseWoken ()
        i = 0

/// wait for a process to exit; a lost race or a REPL ^C raises (a
/// teardown kills the child, which ends the wait)
let waitProcess (p: System.Diagnostics.Process) : unit =
    checkCancels ()

    match waitTokens () with
    | [] -> p.WaitForExit()
    | ts ->
        let hs = ts |> List.map (fun t -> t.WaitHandle) |> Array.ofList

        while not (p.WaitForExit 0) do
            System.Threading.WaitHandle.WaitAny(hs, 50) |> ignore
            checkCancels ()

        // the no-argument form also waits for redirected streams' EOF
        p.WaitForExit()

/// a pipe read a lost race or a REPL ^C cuts: the kill closes a pipe only
/// when it reaches every holder — an orphaned grandchild keeps one open —
/// so under a cancel the read waits beside the token and a cut raises
/// instead of waiting for EOF; the abandoned read's bytes are never used
let cutRead
    (read: unit -> 'a)
    (readAsync: System.Threading.CancellationToken -> System.Threading.Tasks.ValueTask<'a>)
    : 'a =
    match waitTokens () with
    | [] -> read ()
    | _ ->
        checkCancels ()

        withWaitToken (fun t ->
            let vt = readAsync t

            if vt.IsCompletedSuccessfully then
                vt.Result
            else
                let task = vt.AsTask()

                try
                    // Wait(t), not the read's own token: a Windows pipe read
                    // is synchronous underneath and ignores cancellation
                    task.Wait t
                    task.Result
                with
                | :? System.OperationCanceledException
                | :? System.AggregateException when t.IsCancellationRequested ->
                    checkCancels ()
                    raise (System.OperationCanceledException t))

/// join a reader task under the same cut as `cutRead`
let cutJoin (task: System.Threading.Tasks.Task<'a>) : 'a =
    cutRead (fun () -> task.Result) (fun _ -> System.Threading.Tasks.ValueTask<'a>(task))

// the main thread is joining arms for a teardown: it unwinds next, so the
// signal watchdog stands down [D:teardown-joins-arms]
let mutable private mainJoining = 0

/// join a task; a teardown request raises on the main thread once the
/// task's arms, cut at their next boundary, have joined
/// [D:teardown-joins-arms] — before any enclosing scope closes (pure
/// compute holds the join; a second signal hard-exits)
let waitTask (t: System.Threading.Tasks.Task) : unit =
    if interruptible () then
        System.Threading.WaitHandle.WaitAny(
            [| (t :> System.IAsyncResult).AsyncWaitHandle; teardownWake.WaitHandle |]
        )
        |> ignore

        if teardownRequested () <> 0 then
            System.Threading.Volatile.Write(&mainJoining, 1)

            try
                t.Wait()
            with _ ->
                ()

        checkTeardown ()

    t.Wait()

// weir's own stdout, when it is a pipe or file [D:stdout-epipe]: .NET's
// Unix console stream drops EPIPE, so a closed reader went unnoticed and
// `weir s.weir | head -1` ran to completion (or forever). Writes go to
// fd 1 directly; EPIPE requests the 141 teardown (bash's SIGPIPE status)
// and every later write is discarded — a cleanup that prints must not
// fail the unwind, and there is no reader left.
type private EpipeWriter(inner: System.IO.StreamWriter) =
    inherit System.IO.TextWriter()
    let mutable dead = false

    member private _.Guard(write: unit -> unit) =
        if not dead then
            try
                write ()
                inner.Flush()
            // EPIPE is errno 32 on Linux and macOS alike; .NET carries the
            // errno as the HResult
            with :? System.IO.IOException as e when e.HResult = 32 ->
                dead <- true
                requestTeardown 141
                checkTeardown ()

                // a worker unwinds its own arm; the main thread raises at
                // its next boundary
                if not (onMainThread ()) then
                    raise (ExitRequest 141)

    override _.Encoding = inner.Encoding

    override _.NewLine
        with get () = inner.NewLine
        and set (v: string) = inner.NewLine <- v

    override this.Write(c: char) = this.Guard(fun () -> inner.Write c)
    override this.Write(s: string) = this.Guard(fun () -> inner.Write s)

    override this.Write(buffer: char[], index: int, count: int) =
        this.Guard(fun () -> inner.Write(buffer, index, count))

    override this.WriteLine() = this.Guard(fun () -> inner.WriteLine())
    override this.WriteLine(s: string) = this.Guard(fun () -> inner.WriteLine s)
    override this.Flush() = this.Guard ignore

/// only a non-seekable stdout (pipe, FIFO, socket) can EPIPE; a seekable
/// one (a regular file) must keep the default writer — a FileStream on
/// fd 1 writes at its own cached offset and overwrites whatever stderr
/// put in a shared `> log 2>&1` file [D:stdout-epipe]
let armsStdoutFor (fd1: System.IO.Stream) : bool = not fd1.CanSeek

/// route Console.Out through EpipeWriter when stdout is redirected
/// (POSIX: a terminal never EPIPEs, and Windows keeps its console stream)
let armStdout () : unit =
    if System.Console.IsOutputRedirected && not (System.OperatingSystem.IsWindows()) then
        let fd1 =
            new System.IO.FileStream(
                new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint 1, false),
                System.IO.FileAccess.Write,
                1
            )

        if not (armsStdoutFor fd1) then
            fd1.Dispose()
        else
            let inner =
                new System.IO.StreamWriter(fd1, System.Text.UTF8Encoding(false), 4096)

            inner.NewLine <- System.Console.Out.NewLine
            System.Console.SetOut(System.IO.TextWriter.Synchronized(new EpipeWriter(inner)))

// a second signal during teardown hard-exits [D:signal-teardown] — the
// shell's double-Ctrl+C escape, so a slow or stuck cleanup can never
// wedge a supervisor. The first signal sets this and runs the sweep;
// default termination then carries the conventional 128 + signal.
let private tearingDown = ref 0

// 128 + the signal number, the shell's convention
let private signalExitCode (s: System.Runtime.InteropServices.PosixSignal) =
    match s with
    | System.Runtime.InteropServices.PosixSignal.SIGTERM -> 143
    | System.Runtime.InteropServices.PosixSignal.SIGHUP -> 129
    | _ -> 130

let private installExitHook () =
    // normal process exit — the pfirst exit-race customer: a background
    // loser killed mid-finally no longer leaks its dir
    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> sweepLiveTmpDirs ())

    // SIGINT/SIGTERM — the Ctrl-C customer. POSIX arm only: Create
    // throws on Windows (the SIGWINCH guard's lesson [D:windows-v1]);
    // Windows takes Console.CancelKeyPress instead. After the handler,
    // default termination proceeds (Cancel stays false) — sweep, then die.
    if not (System.OperatingSystem.IsWindows()) then
        // the detached-SIGINT reset [D:signal-teardown]: a shell
        // backgrounding weir in a non-interactive context sets SIGINT
        // (and SIGQUIT) to SIG_IGN — the job-control nohup convention —
        // and .NET honours an inherited SIG_IGN, so PosixSignalRegistration
        // never installs and a `kill -INT` on a detached supervisor is a
        // no-op (only SIGKILL stops it, no scope unwind). We reset SIGINT
        // to SIG_DFL only when there is no controlling terminal: a truly
        // detached process has no terminal Ctrl+C to protect against, so
        // any SIGINT it receives is an explicit stop that must tear down.
        // A `nohup weir &` keeps its tty and its SIG_IGN — terminal Ctrl+C
        // still cannot kill it. The REPL is interactive (has a tty), so it
        // never trips this and its [D:repl-isig] survival is untouched.
        if not (replSurvivesSigint.Value) && not (Libc.hasControllingTty ()) then
            Libc.resetSigintIfIgnored () |> ignore

        // SIGHUP joins them [D:sighup-teardown]: a closed terminal or a
        // `kill -HUP` unwinds like INT/TERM, exit 129; an inherited
        // SIG_IGN (nohup) stays — .NET does not install over it
        for posixSig in
            [ System.Runtime.InteropServices.PosixSignal.SIGINT
              System.Runtime.InteropServices.PosixSignal.SIGTERM
              System.Runtime.InteropServices.PosixSignal.SIGHUP ] do
            let reg =
                System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    posixSig,
                    fun ctx ->
                        // a SIGINT the REPL cancels is not a death —
                        // the dirs stay live [D:repl-isig]
                        if
                            replSurvivesSigint.Value
                            && ctx.Signal = System.Runtime.InteropServices.PosixSignal.SIGINT
                        then
                            ()
                        // a second signal mid-teardown hard-exits
                        // [D:signal-teardown] — the double-Ctrl+C escape,
                        // 128 + the signal
                        elif System.Threading.Interlocked.Exchange(tearingDown, 1) = 1 then
                            let code = signalExitCode ctx.Signal

                            System.Environment.Exit code
                        elif mainThreadId <> 0 then
                            // first signal, a script running: the main
                            // thread tears down [D:signal-main-unwind] —
                            // kill what it waits on, wake its sleeps, and
                            // keep the process alive for the unwind
                            ctx.Cancel <- true

                            let code = signalExitCode ctx.Signal

                            requestTeardown code
                            killLiveChildren ()

                            // the backstop: a main thread blocked where
                            // no boundary comes (a stdin read, a network
                            // call) cannot unwind — after a grace period
                            // the old in-handler teardown runs and the
                            // process exits; it runs no statement either.
                            // One joining arms unwinds next and is left be
                            let watchdog =
                                System.Threading.Thread(fun () ->
                                    System.Threading.Thread.Sleep 1000

                                    if
                                        System.Threading.Volatile.Read &teardownRaised = 0
                                        && System.Threading.Volatile.Read &mainJoining = 0
                                    then
                                        sweepLiveTmpDirs ()
                                        System.Environment.Exit code)

                            watchdog.IsBackground <- true
                            watchdog.Start()
                        else
                            // first signal, no script main thread (the
                            // REPL): run the one teardown, then let
                            // default termination carry 128 + signal (Cancel
                            // stays false)
                            sweepLiveTmpDirs ()
                )

            hookRoots <- (reg :> obj) :: hookRoots
    else
        System.Console.CancelKeyPress.Add(fun _ -> sweepLiveTmpDirs ())

let registerTmpDir (dir: string) : unit =
    if System.Threading.Interlocked.Exchange(hookInstalled, 1) = 0 then
        installExitHook ()

    liveTmpDirs[dir] <- ()

let deregisterTmpDir (dir: string) : unit = liveTmpDirs.TryRemove dir |> ignore

let registerProc (p: System.Diagnostics.Process) : unit =
    if System.Threading.Interlocked.Exchange(hookInstalled, 1) = 0 then
        installExitHook ()

    liveProcs[p.Id] <- p

// a foreground child alone installs only a SIGTERM/SIGHUP reaper
// [D:signal-main-unwind]: a pid-targeted TERM or HUP tree-kills the live children,
// then default termination proceeds (weir still dies BY the signal, as
// before any scope exists; a ^C reaches the child through the group
// anyway). Once a scope installs the full hook, that hook does the kill
// as part of its teardown and this one stands down. POSIX only.
let private fgHookInstalled = ref 0

let registerForeground (p: System.Diagnostics.Process) : unit =
    if
        not (System.OperatingSystem.IsWindows())
        && System.Threading.Interlocked.Exchange(fgHookInstalled, 1) = 0
    then
        // a pid-targeted HUP reaps the same way [D:sighup-teardown]
        for posixSig in
            [ System.Runtime.InteropServices.PosixSignal.SIGTERM
              System.Runtime.InteropServices.PosixSignal.SIGHUP ] do
            let reg =
                System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    posixSig,
                    fun _ ->
                        // the full hook owns the signal once installed: it must
                        // request the teardown BEFORE any child dies, or the
                        // main thread's wait returns and runs the next statement
                        if System.Threading.Volatile.Read &hookInstalled.contents = 0 then
                            for kv in liveProcs do
                                try
                                    kv.Value.Kill true
                                with _ ->
                                    ()
                )

            hookRoots <- (reg :> obj) :: hookRoots

    liveProcs[p.Id] <- p

/// remove `p` under `pid` — only that very process, so a late exit
/// event never drops a newer child that reused the pid
let deregisterProcAt (pid: int) (p: System.Diagnostics.Process) : unit =
    liveProcs.TryRemove(System.Collections.Generic.KeyValuePair(pid, p)) |> ignore

let deregisterProc (p: System.Diagnostics.Process) : unit = deregisterProcAt p.Id p

let registerAlways (cleanup: unit -> unit) : int =
    if System.Threading.Interlocked.Exchange(hookInstalled, 1) = 0 then
        installExitHook ()

    let id = System.Threading.Interlocked.Increment alwaysCounter
    liveAlways[id] <- cleanup
    id

// claim-by-removal: true when the caller took the cleanup back from the
// exit hook — the hook claims the same way, so a signal racing a raise
// cannot run one cleanup twice [D:within-always-any]
let deregisterAlways (id: int) : bool = fst (liveAlways.TryRemove id)
