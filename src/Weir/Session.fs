module Weir.Session

// Session-as-value, arriving incrementally (the shape recorded when the
// thread-safety question first came up): the root session is process-global
// and single-threaded as ever; parallel workers FORK it — cd inside a
// worker is worker-local and dies at the join. Spawns and File ops read
// the ambient session at force time, unchanged.

let mutable private rootCwd: string =
    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Directory.GetCurrentDirectory())

// AsyncLocal, not ThreadLocal [D:tasks-underneath]: session scopes
// belong to the LOGICAL context (an arm's scope follows the arm across
// whatever thread resumes it), which is also correct under plain
// thread parallelism — each pmap arm enters/exits per invocation, so
// pool-thread reuse never leaks a scope. Value's default (null) IS
// None for an option — the unset context falls to the root.
let private localCwd = System.Threading.AsyncLocal<string option>()

let Cwd: unit -> string =
    fun () ->
        match localCwd.Value with
        | Some c -> c
        | None -> rootCwd

let setCwd (path: string) : unit =
    // NORMALISED AT ASSIGNMENT, not at cd's return: `cd gh` and `cd gh/` name
    // ONE directory, but Path.GetFullPath PRESERVES a trailing separator, so
    // the spelling of the argument leaked into a value scripts compare, store
    // and interpolate. Normalising here means every relative resolution and
    // every render inherits one shape — the return value is only the first
    // reader. The ROOT keeps its separator: TrimEndingDirectorySeparator
    // trims only beyond the root.
    let path = System.IO.Path.TrimEndingDirectorySeparator path

    match localCwd.Value with
    | Some _ -> localCwd.Value <- Some path
    | None -> rootCwd <- path

// the ambient env overlay [D:within-scopes]: `within env` pushes a
// layer; every spawn applies ambient layers OUTER-FIRST under any
// explicit sigil env, so inner (and explicit) keys win on collision.
// Same locality discipline as cwd: main thread mutates the root, a
// worker's first push forks a local stack over the root's snapshot.
let private rootEnvOverlay: (string * string) list list ref = ref []

let private localEnvOverlay =
    System.Threading.AsyncLocal<(string * string) list list option>()

/// newest layer FIRST
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

// worker lifecycle (Seq.pmap / Seq.piter)
// nesting-aware parallel ceilings [D:parallel-ladder]: the fan-out
// DEPTH rides AsyncLocal beside cwd/env/raceGroup — read at spawn time
// to pick the DEFAULT ceiling, so arms are bounded at creation and
// nothing ever waits on a slot another arm holds (the global-semaphore
// deadlock is unrepresentable)
let private parallelDepth = System.Threading.AsyncLocal<int>()

let parallelDepthNow () : int = parallelDepth.Value

let setParallelDepth (d: int) : unit = parallelDepth.Value <- d

// the plan-capture guard [D:plan-proc-runtime-guard]: a THREAD-LOCAL
// depth, not AsyncLocal — it mirrors PlanMode's capture stack (Eval),
// which is thread-local so a worker never inherits the capturing
// thread's frame. Proc's spawn point (compiled before Eval) consults
// this to REFUSE a process started while a plan captures on this
// thread — an indirect helper runs on the capturing thread, so the
// syntactic firstPlanRefusal that cannot follow the helper is backed
// by this runtime check. Kept minimal (a bool via depth) so the frame
// machinery and its Value dependency stay in Eval.
let private planGuardDepth = new System.Threading.ThreadLocal<int>(fun () -> 0)

/// is a plan capturing on THIS thread? (Proc's spawn-time refusal)
let planGuardActive () : bool = planGuardDepth.Value > 0

let enterPlanGuard () : unit = planGuardDepth.Value <- planGuardDepth.Value + 1

let exitPlanGuard () : unit = planGuardDepth.Value <- max 0 (planGuardDepth.Value - 1)

let enterWorker (parentCwd: string) : unit = localCwd.Value <- Some parentCwd

let exitWorker () : unit =
    localCwd.Value <- None
    localEnvOverlay.Value <- None

let resolve (path: string) : string =
    // the run-time root guard [D:nul-path]: a NUL in the path makes
    // Path.GetFullPath throw a raw ArgumentException (SIGABRT with no
    // diagnostic) — this is the ONE resolution funnel every File/Proc/
    // completion builtin passes through, so refusing here turns the whole
    // run-time class into a located weir error (the builtins already
    // surface raises). NUL-free paths are untouched. Parse-time never
    // reaches this: Extern.exists pre-empts a NUL-bearing head as
    // not-found [D:nul-path].
    if path.Contains '\000' then
        failwith "path contains a NUL byte — paths are NUL-free; NUL-bearing data is binary, not a path"

    System.IO.Path.GetFullPath(System.IO.Path.Combine(Cwd(), path))

// pfirst's loser tree-kill [D:seq-pfirst]: an arm registers its live
// children in its race group (AsyncLocal — the registration follows
// the arm's logical context, the same keying as cwd/env). The winner
// CONDEMNS every other group: registered trees die, and a child
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
// REGISTRATION, not scanning: the hook removes only directories THIS
// process created and still considers live — never a sweep of the temp
// root, so two concurrent weirs cannot clean up after each other. The
// hook is the BACKSTOP: a `within tmp` that exits cleanly deletes its
// dir AND its registration, leaving the hook nothing to do. Installed
// LAZILY on the first registration — the shebang path never pays for it.

let private liveTmpDirs =
    System.Collections.Concurrent.ConcurrentDictionary<string, unit>()

let private hookInstalled = ref 0

// signal registrations must stay ROOTED for the process lifetime — a
// collected registration stops handling
let mutable private hookRoots: obj list = []

// the hook's SECOND customer [D:scoped-procs]: live scoped processes,
// killed (tree) before the dirs go — a spilling child holds its spill
// dir open, so the order is load-bearing on Windows
let private liveProcs =
    System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process>()

// pending `always` cleanups [D:within-always]: LIFO, run by the exit
// hook on signals/hard exits; a completed scope deregisters (its own
// finally already ran the cleanup). Entries are REMOVED before running
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
/// it deletes LIVE within-tmp dirs, which is correct only when dying
let replSurvivesSigint = ref false

// libc, for the detached-SIGINT reset [D:signal-teardown]. `signal`
// returns the PREVIOUS disposition; we peek-and-restore to convert an
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

    let SIG_DFL: nativeint = nativeint 0
    let SIG_IGN: nativeint = nativeint 1
    let SIGINT = 2
    let O_RDWR = 2

    /// true when the process has a controlling terminal — /dev/tty opens
    /// only then (ENXIO otherwise). A `nohup weir &` KEEPS its tty, so
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

// a second signal DURING teardown hard-exits [D:signal-teardown] — the
// shell's double-Ctrl+C escape, so a slow or stuck cleanup can never
// wedge a supervisor. The first signal sets this and runs the sweep;
// default termination then carries the conventional 130/143.
let private tearingDown = ref 0

let private installExitHook () =
    // NORMAL process exit — the pfirst exit-race customer: a background
    // loser killed mid-finally no longer leaks its dir
    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> sweepLiveTmpDirs ())

    // SIGINT/SIGTERM — the Ctrl-C customer. POSIX arm only: Create
    // throws on Windows (the SIGWINCH guard's lesson [D:windows-v1]);
    // Windows takes Console.CancelKeyPress instead. After the handler,
    // default termination proceeds (Cancel stays false) — sweep, then die.
    if not (System.OperatingSystem.IsWindows()) then
        // THE DETACHED-SIGINT RESET [D:signal-teardown]: a shell
        // backgrounding weir in a non-interactive context sets SIGINT
        // (and SIGQUIT) to SIG_IGN — the job-control nohup convention —
        // and .NET HONOURS an inherited SIG_IGN, so PosixSignalRegistration
        // never installs and a `kill -INT` on a detached supervisor is a
        // no-op (only SIGKILL stops it, no scope unwind). We reset SIGINT
        // to SIG_DFL ONLY when there is no controlling terminal: a truly
        // detached process has no terminal Ctrl+C to protect against, so
        // any SIGINT it receives is an explicit stop that must tear down.
        // A `nohup weir &` KEEPS its tty and its SIG_IGN — terminal Ctrl+C
        // still cannot kill it. The REPL is interactive (has a tty), so it
        // never trips this and its [D:repl-isig] survival is untouched.
        if not (replSurvivesSigint.Value) && not (Libc.hasControllingTty ()) then
            Libc.resetSigintIfIgnored () |> ignore

        for posixSig in
            [ System.Runtime.InteropServices.PosixSignal.SIGINT
              System.Runtime.InteropServices.PosixSignal.SIGTERM ] do
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
                        // a SECOND signal mid-teardown hard-exits
                        // [D:signal-teardown] — the double-Ctrl+C escape,
                        // 130 for SIGINT / 143 for SIGTERM
                        elif System.Threading.Interlocked.Exchange(tearingDown, 1) = 1 then
                            let code =
                                if ctx.Signal = System.Runtime.InteropServices.PosixSignal.SIGTERM then
                                    143
                                else
                                    130

                            System.Environment.Exit code
                        else
                            // first signal: run the one teardown, then let
                            // default termination carry 130/143 (Cancel
                            // stays false) — the SAME path a tty Ctrl+C and
                            // a SIGTERM already take
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

let deregisterProc (p: System.Diagnostics.Process) : unit = liveProcs.TryRemove p.Id |> ignore

let registerAlways (cleanup: unit -> unit) : int =
    if System.Threading.Interlocked.Exchange(hookInstalled, 1) = 0 then
        installExitHook ()

    let id = System.Threading.Interlocked.Increment alwaysCounter
    liveAlways[id] <- cleanup
    id

let deregisterAlways (id: int) : unit = liveAlways.TryRemove id |> ignore
