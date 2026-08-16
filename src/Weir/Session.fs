module Weir.Session

// Session-as-value, arriving incrementally (the shape recorded when the
// thread-safety question first came up): the root session is process-global
// and single-threaded as ever; parallel workers FORK it — cd inside a
// worker is worker-local and dies at the join. Spawns and File ops read
// the ambient session at force time, unchanged.

let mutable private rootCwd: string = System.IO.Directory.GetCurrentDirectory()

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

let enterWorker (parentCwd: string) : unit = localCwd.Value <- Some parentCwd

let exitWorker () : unit =
    localCwd.Value <- None
    localEnvOverlay.Value <- None

let resolve (path: string) : string =
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

let private sweepLiveTmpDirs () =
    // runs at exit while finallys may also be running: a vanished dir is
    // benign (the double-delete pin's territory), and the hook must
    // never throw during exit
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

let private installExitHook () =
    // NORMAL process exit — the pfirst exit-race customer: a background
    // loser killed mid-finally no longer leaks its dir
    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> sweepLiveTmpDirs ())

    // SIGINT/SIGTERM — the Ctrl-C customer. POSIX arm only: Create
    // throws on Windows (the SIGWINCH guard's lesson [D:windows-v1]);
    // Windows takes Console.CancelKeyPress instead. After the handler,
    // default termination proceeds (Cancel stays false) — sweep, then die.
    if not (System.OperatingSystem.IsWindows()) then
        for posixSig in
            [ System.Runtime.InteropServices.PosixSignal.SIGINT
              System.Runtime.InteropServices.PosixSignal.SIGTERM ] do
            let reg =
                System.Runtime.InteropServices.PosixSignalRegistration.Create(posixSig, fun _ -> sweepLiveTmpDirs ())

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
