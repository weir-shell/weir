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

/// a filesystem builtin's path [D:fs-dotdot-physical]: `..` steps up
/// from where the path so far physically is — the symlinks before it
/// followed, as the kernel (and so `cat L/../x`) resolves it — where
/// `resolve` collapses `..` lexically (right for a `cd`, which a shell
/// tracks logically). A path without `..` is resolved as `resolve` does;
/// components after the last `..` stay as written
let resolveFs (path: string) : string =
    let combined =
        if path.Contains '\000' then
            resolve path // the NUL refusal
        else
            System.IO.Path.Combine(Cwd(), path)

    let seps = [| '/'; System.IO.Path.DirectorySeparatorChar |]

    let segsOf (p: string) =
        let root = System.IO.Path.GetPathRoot p
        root, p.Substring(root.Length).Split(seps, System.StringSplitOptions.RemoveEmptyEntries)

    let _, segs = segsOf combined

    if not (segs |> Array.contains "..") then
        System.IO.Path.GetFullPath combined
    else
        let mutable hops = 0

        let parentOf (p: string) =
            match System.IO.Path.GetDirectoryName p with
            | null -> p
            | parent -> parent

        // every symlink in `p` followed (40 hops, the kernel's bound)
        let rec realOf (p: string) : string =
            let root, segs = segsOf p
            let mutable acc = root

            for seg in segs do
                if seg = "." then
                    ()
                elif seg = ".." then
                    acc <- parentOf acc
                else
                    let next = System.IO.Path.Combine(acc, seg)

                    let target =
                        try
                            System.IO.FileInfo(next).LinkTarget
                        with _ ->
                            null

                    if not (isNull target) && hops < 40 then
                        hops <- hops + 1
                        acc <- realOf (System.IO.Path.Combine(acc, target))
                    else
                        acc <- next

            acc

        let root, segs = segsOf combined
        let mutable cur = root

        for seg in segs do
            if seg = "." then ()
            elif seg = ".." then cur <- parentOf (realOf cur)
            else cur <- System.IO.Path.Combine(cur, seg)

        System.IO.Path.GetFullPath cur

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

let mutable private mainThreadId = 0

let private onMainThread () =
    mainThreadId <> 0
    && System.Threading.Thread.CurrentThread.ManagedThreadId = mainThreadId

// the main thread inside an `always` cleanup [D:teardown-never-preempts]:
// the signal watchdog stands down for it, and once the watchdog has
// fired no cleanup starts on the main thread (the sweep runs it)
let private watchdogGate = obj ()
let mutable private mainCleanups = 0
let mutable private watchdogFired = false

/// run an `always` cleanup: spawns inside it start even after the teardown
/// kill, a parallel fan-out inside it keeps taking arms, and a requested
/// teardown never preempts it [D:teardown-never-preempts]
let runningCleanup (f: unit -> 'a) : 'a =
    let onMain = onMainThread ()

    if onMain then
        lock watchdogGate (fun () ->
            if watchdogFired then
                // the process is exiting and the sweep owns the cleanups
                System.Threading.Thread.Sleep System.Threading.Timeout.Infinite

            mainCleanups <- mainCleanups + 1)

    cleanupDepth.Value <- cleanupDepth.Value + 1

    try
        f ()
    finally
        cleanupDepth.Value <- cleanupDepth.Value - 1

        if onMain then
            lock watchdogGate (fun () -> mainCleanups <- mainCleanups - 1)

let inCleanup () = cleanupDepth.Value > 0

// the cleanup depth a race, fan-out or serve flow was opened at
// [D:cleanup-owns-its-cuts]: a cleanup entered inside the flow is spared
// its cut, while a scope opened inside a cleanup still cuts its own flows
let private flowBase = System.Threading.AsyncLocal<int>()

let private inFlowCleanup () = cleanupDepth.Value > flowBase.Value

// children an `always` cleanup started: the teardown's kill spares them
// [D:teardown-never-preempts] — a cleanup runs whole, its commands too;
// the exit and a second signal still kill them
let private cleanupProcs =
    System.Collections.Concurrent.ConcurrentDictionary<System.Diagnostics.Process, unit>(HashIdentity.Reference)

// a tree kill in flight [D:kill-settle]: .NET's Kill(true) on Unix
// stops the tree, then kills it, so a process exiting between the two
// leaves SIGSTOPped orphans — every exit path settles the kills in flight
let mutable private killsInFlight = 0

// the tree walk [D:tree-kill-walk]: .NET's Kill(true) reads every
// process on the box to find one tree, so killing n trees cost n × the
// process table. On Linux the kernel lists each task's children
// (/proc/PID/task/TID/children), so the walk costs the tree alone. It
// covers what Kill(true) covers — every descendant still linked to the
// child by parent pid, a `setsid` child included; a daemon that forked
// away from its parent was reparented and is out of reach of both.
// Elsewhere (or a kernel without the file) Kill(true) stays.
module private ProcTree =
    open System.Runtime.InteropServices

    [<DllImport("libc", SetLastError = true)>]
    extern int kill(int pid, int signal)

    let SIGKILL = 9
    let SIGSTOP = 19

    let available =
        lazy
            (System.OperatingSystem.IsLinux()
             && System.IO.File.Exists(
                 $"/proc/{System.Environment.ProcessId}/task/{System.Environment.ProcessId}/children"
             ))

    let childrenOf (pid: int) : int list =
        try
            [ for task in System.IO.Directory.EnumerateDirectories $"/proc/{pid}/task" do
                  let listed =
                      try
                          System.IO.File.ReadAllText(System.IO.Path.Combine(task, "children"))
                      with _ ->
                          ""

                  for tok in listed.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) do
                      match System.Int32.TryParse tok with
                      | true, n -> yield n
                      | _ -> () ]
        with _ ->
            []

    // stop each process before listing its children (a stopped parent
    // forks no more and reaps none, so no pid in the walk is reused),
    // then kill leaves first, as Kill(true) stops then kills
    let killWalk (root: int) : unit =
        let order = System.Collections.Generic.List<int>()
        let seen = System.Collections.Generic.HashSet<int>()

        let rec walk (pid: int) =
            if seen.Add pid then
                kill (pid, SIGSTOP) |> ignore
                order.Add pid

                for c in childrenOf pid do
                    walk c

        try
            walk root
        finally
            for i in order.Count - 1 .. -1 .. 0 do
                kill (order[i], SIGKILL) |> ignore

/// tree-kill `p` (a dead or reaped process is no error)
let killTree (p: System.Diagnostics.Process) : unit =
    System.Threading.Interlocked.Increment &killsInFlight |> ignore

    try
        try
            if ProcTree.available.Value then
                if not p.HasExited then
                    ProcTree.killWalk p.Id
            else
                p.Kill true
        with _ ->
            ()
    finally
        System.Threading.Interlocked.Decrement &killsInFlight |> ignore

/// wait (bounded) for every tree kill in flight to finish
let settleKills () : unit =
    let clock = System.Diagnostics.Stopwatch.StartNew()

    while System.Threading.Volatile.Read &killsInFlight > 0
          && clock.ElapsedMilliseconds < 2000L do
        System.Threading.Thread.Sleep 5

// pfirst's loser tree-kill [D:seq-pfirst]: an arm registers its live
// children in its race group (AsyncLocal — the registration follows
// the arm's logical context, the same keying as cwd/env). The winner
// condemns every other group: registered trees die, and a child
// spawned after condemnation dies at registration (the race window a
// plain bag would leak). A child an `always` cleanup starts never joins
// the group — the cut stops the body, never its cleanup
// [D:teardown-always-spawns]; the race joins the arm, so it joins them.
// A nested group registers its children with the enclosing arm's group
// too, so a condemned arm's nested races and fan-outs lose theirs.
// The bag is made at the first child: every fan-out arm has a group.
type RaceGroup(parent: RaceGroup option) =
    let mutable procs: System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Process> =
        null

    let mutable condemned = false

    new() = RaceGroup(None)

    member this.Register(p: System.Diagnostics.Process) =
        if inFlowCleanup () then
            ()
        else
            match parent with
            | Some g -> g.Register p
            | None -> ()

            // under the lock with Condemn: a child registered as the group
            // is condemned is either in the bag Condemn kills or killed here
            let late =
                lock this (fun () ->
                    if condemned then
                        true
                    else
                        if isNull procs then
                            procs <- System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Process>()

                        procs.Add p
                        false)

            if late then
                killTree p

    member this.Condemn() =
        match
            lock this (fun () ->
                condemned <- true
                procs)
        with
        | null -> ()
        | b ->
            for p in b do
                killTree p

let private raceGroup = System.Threading.AsyncLocal<RaceGroup option>()

let enterRace (g: RaceGroup) : unit = raceGroup.Value <- Some g
let exitRace () : unit = raceGroup.Value <- None

/// the group a race or fan-out opened here nests in (none inside a cleanup)
let enclosingGroup () : RaceGroup option =
    if inFlowCleanup () then None else raceGroup.Value

let registerChild (p: System.Diagnostics.Process) : unit =
    match raceGroup.Value with
    | Some g -> g.Register p
    | None -> ()

// ---- the main-thread teardown [D:signal-main-unwind] [D:stdout-epipe] --
// A signal or a closed stdout ends the script, but the teardown — every
// `always`, every scope's release — runs on the MAIN thread, never in the
// signal handler: the handler records the exit code, kills the scoped
// children (what the main thread waits on) and wakes interruptible waits;
// the main thread raises ExitRequest at its next evaluation or wait
// boundary and unwinds through its scopes, so no statement runs after the
// request. Registered by the script runner and -e; without a registered
// main thread (the REPL) the handler keeps the in-handler sweep.

let mutable private teardownCode = 0
let mutable private teardownRaised = 0

/// interruptible waits block on this; set when a teardown is requested
let teardownWake = new System.Threading.ManualResetEventSlim(false)

let registerMainThread () : unit =
    mainThreadId <- System.Threading.Thread.CurrentThread.ManagedThreadId

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
// arm that lost the race or a serve handler past its scope (its flow
// token, AsyncLocal — the logical context), a ^C at the REPL (a session
// token, global — every wait of the evaluation), and a requested teardown
// for every flow off the main thread [D:teardown-joins-arms]. Each raises
// an OperationCanceledException, which `try` and retry pass through as
// they pass `exit`.

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

// a pmap/piter arm's cut: an `exit n` in a sibling arm stops the fan-out
// [D:fanout-exit]. Kept apart from the race token so a fan-out arm's reads
// and process waits stay as they are; sleeps, Http sends and builtin calls
// honour it, and the arm's children die by its group
let private fanCancel = System.Threading.AsyncLocal<System.Threading.CancellationToken>()

/// the flow's cancel token (CancellationToken.None outside a race, or
/// inside a cleanup the flow entered)
let flowToken () : System.Threading.CancellationToken =
    if inFlowCleanup () then
        System.Threading.CancellationToken.None
    else
        flowCancel.Value

/// the enclosing fan-out's cut, as `flowToken`
let fanToken () : System.Threading.CancellationToken =
    if inFlowCleanup () then
        System.Threading.CancellationToken.None
    else
        fanCancel.Value

let enterFlow (t: System.Threading.CancellationToken) : unit =
    flowCancel.Value <- t
    fanCancel.Value <- System.Threading.CancellationToken.None
    flowBase.Value <- cleanupDepth.Value

/// enter a fan-out arm: the enclosing flow's token (taken by `flowToken`
/// where the fan-out opened) plus the fan-out's own cut
let enterFan (flow: System.Threading.CancellationToken) (fan: System.Threading.CancellationToken) : unit =
    flowCancel.Value <- flow
    fanCancel.Value <- fan
    flowBase.Value <- cleanupDepth.Value

let exitFlow () : unit =
    flowCancel.Value <- System.Threading.CancellationToken.None
    fanCancel.Value <- System.Threading.CancellationToken.None
    flowBase.Value <- 0

/// a flow token may now be cancelled: the per-builtin check reads it
let cutsLive () : unit =
    System.Threading.Interlocked.Increment &racesLive |> ignore

let cutsDone () : unit =
    System.Threading.Interlocked.Decrement &racesLive |> ignore

/// run a race (or a serve scope's handlers): the counter gates the
/// per-builtin check
let racing (f: unit -> 'a) : 'a =
    cutsLive ()

    try
        f ()
    finally
        cutsDone ()

// a lost race stops the body, never an `always` cleanup — the race joins
// the loser, so its cleanup still runs before pfirst returns
let private flowCut () =
    System.Threading.Volatile.Read &racesLive <> 0
    && (flowCancel.Value.IsCancellationRequested
        || fanCancel.Value.IsCancellationRequested)
    && not (inFlowCleanup ())

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

/// has ^C cut this evaluation's waits? (a fan-out stops taking arms)
let interruptRequested () : bool = (replToken ()).IsCancellationRequested

/// raise Interrupted when ^C cut this evaluation's waits
let checkInterrupted () : unit =
    if interruptRequested () then
        raise (Interrupted())

/// the tokens a runtime-owned wait honours, beyond the teardown
let private waitTokens () : System.Threading.CancellationToken list =
    [ flowToken (); replToken () ]
    |> List.filter (fun t -> t.CanBeCanceled)

/// raise whichever cancel stopped this flow (the race's, then ^C's)
let checkCancels () : unit =
    checkCancelled ()
    checkInterrupted ()

// the enclosing fan-out's cut, for the waits that honour it
let private fanTokens () : System.Threading.CancellationToken list =
    match fanToken () with
    | t when t.CanBeCanceled -> [ t ]
    | _ -> []

// the wait tokens plus, off the main thread, the teardown's
// [D:teardown-joins-arms]
let private cutTokens () : System.Threading.CancellationToken list =
    waitTokens ()
    @ (if teardownCuts () then [ teardownCut.Token ] else [])
    @ fanTokens ()

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
    // a spawn is an effect boundary [D:pfirst-join] — for a REPL ^C too,
    // so a fan-out's next arm starts nothing [D:repl-interrupt-fanout]
    checkCancels ()
    spawnGate.EnterReadLock()

    try
        if spawningStopped && not (inCleanup ()) then
            failwith "the script is being torn down — no new command starts"

        start ()
    finally
        spawnGate.ExitReadLock()

/// stop spawning and tree-kill every live child not already killed; the
/// teardown spares a cleanup's children [D:teardown-never-preempts]
let private killLiveChildren (spareCleanups: bool) : unit =
    spawnGate.EnterWriteLock()

    try
        spawningStopped <- true

        for kv in liveProcs do
            if not (spareCleanups && cleanupProcs.ContainsKey kv.Value)
               && killedProcs.Add kv.Value then
                killTree kv.Value
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
        killTree kv.Value

        try
            kv.Value.WaitForExit()
        with _ ->
            ()

    // a race's kill still walking a tree on another thread finishes
    // before the process does [D:kill-settle]
    settleKills ()

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
    extern int tcgetpgrp(int fd)

    [<DllImport("libc", SetLastError = true)>]
    extern int getpgrp()

    [<DllImport("libc", SetLastError = true)>]
    extern int flock(int fd, int operation)

    [<DllImport("libc", SetLastError = true)>]
    extern nativeint read(int fd, nativeint buf, unativeint count)

    [<DllImport("libc", SetLastError = true)>]
    extern int64 lseek(int fd, int64 offset, int whence)

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type PollFd =
        val mutable fd: int
        val mutable events: int16
        val mutable revents: int16

    [<DllImport("libc", SetLastError = true)>]
    extern int poll(PollFd& fds, unativeint nfds, int timeout)

    let EINTR = 4
    let EAGAIN = if System.OperatingSystem.IsLinux() then 11 else 35

    /// wait up to `ms` for fd to be readable (data, EOF or an error —
    /// whatever the next read reports); EINTR is a timeout
    let readable (fd: int) (ms: int) : bool =
        let mutable p = PollFd()
        p.fd <- fd
        p.events <- 1s // POLLIN
        poll (&p, 1un, ms) > 0

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

    /// true when weir's process group is its controlling terminal's
    /// foreground group — a SIGINT then is the terminal's ^C, delivered
    /// to the whole group [D:sigint-terminal-group]
    let terminalForeground () : bool =
        let fd = ``open`` ("/dev/tty", O_RDWR)

        if fd < 0 then
            false
        else
            try
                let fg = tcgetpgrp fd
                fg > 0 && fg = getpgrp ()
            finally
                close fd |> ignore

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

/// the detached-SIGINT reset [D:signal-teardown], once, before the first
/// SIGINT registration and the first spawn — .NET reads the inherited
/// disposition when it first starts a process [D:sigint-reaps]
let resetDetachedSigint: Lazy<unit> =
    lazy
        (if
            not (System.OperatingSystem.IsWindows())
            && not (replSurvivesSigint.Value)
            && not (Libc.hasControllingTty ())
         then
             Libc.resetSigintIfIgnored () |> ignore)

// a terminal ^C weir waited out ends weir BY the SIGINT, as before any
// wait [D:sigint-terminal-group] — a calling shell sees the ^C, not an exit
let mutable private dieBySigint = false

let private raiseSigint () =
    // the tree kills in flight settle first [D:kill-settle]
    settleKills ()
    Libc.signal (Libc.SIGINT, Libc.SIG_DFL) |> ignore
    ProcTree.kill (System.Environment.ProcessId, Libc.SIGINT) |> ignore
    System.Threading.Thread.Sleep 1000
    System.Environment.Exit 130

/// the requested exit code, or `code` when none was requested — the
/// runner's result, so a request racing a statement's normal end still
/// exits with the request's code
let exitCodeOr (code: int) : int =
    match System.Threading.Volatile.Read &teardownCode with
    | 0 -> code
    | c ->
        // a requested teardown's kill completes before the exit, every
        // other tree kill in flight with it [D:kill-settle]
        killLiveChildren false
        settleKills ()

        if dieBySigint then
            System.Console.Out.Flush()
            raiseSigint ()

        c

/// a parallel worker stops taking arms once a teardown is requested
/// (a signal, a closed stdout) [D:teardown-stops-arms] — unless the
/// fan-out runs inside an `always` cleanup
let stopTakingArms () : bool =
    System.Threading.Volatile.Read &teardownCode <> 0 && not (inCleanup ())

/// the requested teardown's exit code, or 0 when none is requested
let teardownRequested () : int = System.Threading.Volatile.Read &teardownCode

/// a command that died of SIGINT while weir is the terminal's foreground
/// group most likely took the terminal's ^C with weir [D:sigint-terminal-group]:
/// give the signal handler a moment to request the teardown, so the death
/// is the ^C, not a command failure
let settleInterrupt (code: int) : unit =
    if
        code = 130
        && not (System.OperatingSystem.IsWindows())
        && mainThreadId <> 0
        && System.Threading.Volatile.Read &teardownCode = 0
        && Libc.terminalForeground ()
    then
        let clock = System.Diagnostics.Stopwatch.StartNew()

        while System.Threading.Volatile.Read &teardownCode = 0
              && clock.ElapsedMilliseconds < 250L do
            System.Threading.Thread.Sleep 5

/// raise the requested teardown on the main thread, once: the unwind
/// then runs the cleanups as ordinary code (which must not be cut short
/// by the same request again). A cleanup already running is never
/// preempted [D:teardown-never-preempts]: the request raises at the
/// first boundary after it
let checkTeardown () : unit =
    let c = System.Threading.Volatile.Read &teardownCode

    if
        c <> 0
        && onMainThread ()
        && not (inCleanup ())
        && System.Threading.Interlocked.Exchange(&teardownRaised, 1) = 0
    then
        raise (ExitRequest c)

// a main thread not yet unwinding waits interruptibly; elsewhere (a
// worker, or a cleanup running) waits run as written
let private interruptible () =
    onMainThread ()
    && System.Threading.Volatile.Read &teardownRaised = 0
    && not (inCleanup ())

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

/// wait for a process to exit; a lost race, a fan-out's exit or a REPL ^C
/// raises — the process may be one no group kill reaches, a `within proc`
/// opened outside the fan-out [D:fanout-exit-reads] (a signal's or a closed
/// stdout's teardown kills the child, which ends the wait; a terminal ^C's
/// waits for it [D:sigint-terminal-group])
let waitProcess (p: System.Diagnostics.Process) : unit =
    checkCancels ()

    match waitTokens () @ fanTokens () with
    | [] -> p.WaitForExit()
    | ts ->
        let hs = ts |> List.map (fun t -> t.WaitHandle) |> Array.ofList

        while not (p.WaitForExit 0) do
            System.Threading.WaitHandle.WaitAny(hs, 50) |> ignore
            checkCancels ()

        // the no-argument form also waits for redirected streams' EOF
        p.WaitForExit()

// each running fan-out worker, marked while parked in a blocking call no
// cut reaches [D:teardown-abandons-reads]: a join stops waiting once every
// arm it waits on is parked. One mark per worker thread, so a parked read
// costs no shared write while no cut is live
[<AllowNullLiteral>]
type private ArmMark() =
    [<DefaultValue>]
    val mutable Parked: int

    // 0 live, 1 a join proposes to abandon the arm, 2 abandoned: an arm
    // leaving a parked call under a cut waits out a proposal, and an
    // abandoned arm never runs again — its cleanups are its join's
    // [D:fanout-exit-reads]
    [<DefaultValue>]
    val mutable Abandon: int

    // the arm whose flow opened this one's fan-out or race (null at top)
    [<DefaultValue>]
    val mutable Parent: ArmMark

let private armMarks =
    System.Collections.Concurrent.ConcurrentDictionary<ArmMark, byte>(HashIdentity.Reference)

let private armMark = new System.Threading.ThreadLocal<ArmMark>()

// the arm a flow runs in, inherited by the workers it opens
let private armOwner = System.Threading.AsyncLocal<ArmMark>()

/// run one fan-out worker (on its own thread), marked for the teardown's join
let armRunning (f: unit -> 'a) : 'a =
    let m = ArmMark()
    m.Parent <- armOwner.Value
    let prev = armMark.Value
    let prevOwner = armOwner.Value
    armMark.Value <- m
    armOwner.Value <- m
    armMarks[m] <- 0uy

    try
        f ()
    finally
        armMarks.TryRemove m |> ignore
        armMark.Value <- prev
        armOwner.Value <- prevOwner

// a cut is live, so a join may be abandoning parked arms
let private cutPending () =
    System.Threading.Volatile.Read &teardownCode <> 0
    || System.Threading.Volatile.Read &racesLive <> 0

let private parking (f: unit -> 'a) : 'a =
    match armMark.Value with
    | null -> f ()
    | m ->
        m.Parked <- m.Parked + 1

        try
            f ()
        finally
            if cutPending () then
                System.Threading.Interlocked.Decrement &m.Parked |> ignore

                while System.Threading.Volatile.Read &m.Abandon = 1 do
                    System.Threading.Thread.Sleep 1

                if System.Threading.Volatile.Read &m.Abandon = 2 then
                    System.Threading.Thread.Sleep System.Threading.Timeout.Infinite
            else
                m.Parked <- m.Parked - 1

// abandon `marks` if every one is still parked: propose, check, then commit
// or withdraw, so no arm runs on past a commit
let private tryAbandon (marks: ArmMark list) : bool =
    for m in marks do
        System.Threading.Interlocked.Exchange(&m.Abandon, 1) |> ignore

    let all =
        marks
        |> List.forall (fun m -> System.Threading.Volatile.Read &m.Parked > 0)

    for m in marks do
        System.Threading.Volatile.Write(&m.Abandon, (if all then 2 else 0))

    all

// the main thread is joining arms for a teardown: it unwinds next, so the
// signal watchdog stands down [D:teardown-joins-arms]
let mutable private mainJoining = 0

// wait for `t`; abandon the arms `live` lists once they are all parked, twice
// in a row — the abandoned ones, or none when `t` ended
let private abandonParked (t: System.Threading.Tasks.Task) (live: unit -> ArmMark list) : ArmMark list =
    let mutable streak = 0
    let mutable abandoned = []

    try
        while List.isEmpty abandoned && not (t.Wait 50) do
            if interruptible () && teardownRequested () <> 0 then
                System.Threading.Volatile.Write(&mainJoining, 1)

            let ms = live ()

            if
                not (List.isEmpty ms)
                && ms |> List.forall (fun m -> System.Threading.Volatile.Read &m.Parked > 0)
            then
                streak <- streak + 1

                if streak >= 2 && tryAbandon ms then
                    abandoned <- ms
            else
                streak <- 0
    with _ ->
        ()

    abandoned

// the pending `always` cleanups an arm registered, with the arm's context:
// when a join abandons the arm, the joining thread runs them before it
// unwinds, so none lands after an enclosing scope released
// [D:abandoned-arm-cleanup]
let private armAlways =
    System.Collections.Concurrent.ConcurrentDictionary<int, ArmMark * System.Threading.ExecutionContext>()

let private runAbandonedCleanups (owned: ArmMark list) =
    for id in armAlways.Keys |> Seq.sortDescending do
        match armAlways.TryGetValue id with
        | true, (m, ctx) when owned |> List.exists (fun o -> obj.ReferenceEquals(o, m)) ->
            match liveAlways.TryRemove id with
            | true, cleanup ->
                armAlways.TryRemove id |> ignore

                try
                    match ctx with
                    | null -> cleanup ()
                    | c -> System.Threading.ExecutionContext.Run(c, (fun _ -> cleanup ()), null)
                with _ ->
                    ()
            | _ -> ()
        | _ -> ()

// the enclosing fan-out's cut has fired on this flow
let private fanCut () =
    System.Threading.Volatile.Read &racesLive <> 0
    && (fanToken ()).IsCancellationRequested

// a fan-out arm outside a cleanup it entered: its uncut calls are parked,
// so the fan-out's join can abandon them once its cut fires
// [D:fanout-exit-reads]
let private fanParks () =
    not (isNull armMark.Value)
    && not (inFlowCleanup ())
    && fanCancel.Value.CanBeCanceled

// a blocking call on a flow only the teardown or a fan-out's cut stops:
// parked while it blocks, and a cut landing meanwhile raises as it
// returns — its result unused
let private parked (f: unit -> 'a) : 'a =
    let cut () =
        if
            (System.Threading.Volatile.Read &teardownCode <> 0 && teardownCuts ())
            || fanCut ()
        then
            raise (Cancelled())

    cut ()
    let v = parking f
    cut ()
    v

/// a pipe read a lost race or a REPL ^C cuts: the kill closes a pipe only
/// when it reaches every holder — an orphaned grandchild keeps one open —
/// so under a cancel the read waits beside the token and a cut raises
/// instead of waiting for EOF; the abandoned read's bytes are never used.
/// An arm's read only a teardown or its fan-out's exit could cut is parked,
/// and that join abandons it [D:teardown-abandons-reads] [D:fanout-exit-reads]
let cutRead
    (read: unit -> 'a)
    (readAsync: System.Threading.CancellationToken -> System.Threading.Tasks.ValueTask<'a>)
    : 'a =
    match waitTokens () with
    | [] when teardownCuts () || fanParks () -> parked read
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

/// join a reader task under the same cut as `cutRead`; a reader the
/// teardown cut (it runs off the flow, so the teardown cuts it) raises as
/// the joining flow's own stop — the main thread's ExitRequest
/// [D:epipe-kills-at-teardown]
let cutJoin (task: System.Threading.Tasks.Task<'a>) : 'a =
    try
        cutRead (fun () -> task.Result) (fun _ -> System.Threading.Tasks.ValueTask<'a>(task))
    with :? System.AggregateException as ae when (ae.InnerException :? System.OperationCanceledException) ->
        raiseWoken ()
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException).Throw()
        Unchecked.defaultof<'a>

/// a blocking call a lost race or a REPL ^C abandons — a FIFO's open or
/// read, which no kill reaches [D:stdin-reader]: under a cancel it runs off
/// the flow and the wait raises; the abandoned call's result is never used.
/// Only a teardown or a fan-out's exit could cut it: parked
/// [D:teardown-abandons-reads]
let cutBlocking (f: unit -> 'a) : 'a =
    match waitTokens () with
    | [] when teardownCuts () || fanParks () -> parked f
    | [] -> f ()
    | _ ->
        let task = System.Threading.Tasks.Task.Run<'a>(System.Func<'a> f)

        try
            cutJoin task
        with :? System.AggregateException as ae when
            ae.InnerExceptions.Count = 1
            && not (ae.InnerException :? System.OperationCanceledException) ->
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException).Throw()
            Unchecked.defaultof<'a>

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

            // every live arm parked in a call no cut reaches, twice in a
            // row: the join abandons them [D:teardown-abandons-reads]
            match abandonParked t (fun () -> List.ofSeq armMarks.Keys) with
            | [] -> ()
            | ms -> runAbandonedCleanups ms

        checkTeardown ()

    // an arm joining its own arms is parked with them
    if teardownCuts () then parking t.Wait else t.Wait()

/// the workers of one fan-out, for its own join [D:fanout-exit-reads]
type FanArms(n: int) =
    let marks: ArmMark[] = Array.zeroCreate n

    /// run worker `w`, marked as one of this fan-out's arms
    member _.Run(w: int, f: unit -> unit) : unit =
        armRunning (fun () ->
            System.Threading.Volatile.Write(&marks[w], armMark.Value)

            try
                f ()
            finally
                System.Threading.Volatile.Write(&marks[w], null))

    /// join the workers' task. Once the fan-out's cut fires, workers parked
    /// in a call no cut reaches are abandoned and their pending cleanups
    /// run here, before the join returns; true when it abandoned any
    member _.Join(t: System.Threading.Tasks.Task, cut: System.Threading.CancellationToken) : bool =
        let cutFirst =
            if interruptible () then
                System.Threading.WaitHandle.WaitAny(
                    [| (t :> System.IAsyncResult).AsyncWaitHandle
                       teardownWake.WaitHandle
                       cut.WaitHandle |]
                )
                |> ignore

                not t.IsCompleted
                && teardownRequested () = 0
                && cut.IsCancellationRequested
            else
                try
                    if teardownCuts () then
                        parking (fun () -> t.Wait cut)
                    else
                        t.Wait cut

                    false
                with :? System.OperationCanceledException ->
                    true

        if cutFirst then
            // this fan-out's arms and every arm nested under them: an arm
            // parked joining its own fan-out counts only with that one's
            // arms parked too
            let live () =
                let own =
                    marks
                    |> Array.choose (fun m -> if isNull m then None else Some m)
                    |> List.ofArray

                let mine (m: ArmMark) = own |> List.exists (fun o -> obj.ReferenceEquals(o, m))

                let rec under (m: ArmMark) =
                    not (isNull m) && (mine m || under m.Parent)

                if List.isEmpty own then
                    []
                else
                    own
                    @ [ for m in armMarks.Keys do
                            if not (mine m) && under m.Parent then
                                yield m ]

            match abandonParked t live with
            | [] -> false
            | ms ->
                runAbandonedCleanups ms
                true
        else
            waitTask t
            false

/// join a thread (a serve handler) whole; a teardown requested meanwhile
/// stands the signal watchdog down, as `waitTask` does
/// [D:serve-cut-joins-cleanup]
let joinThread (th: System.Threading.Thread) : unit =
    while not (th.Join 50) do
        if interruptible () && teardownRequested () <> 0 then
            System.Threading.Volatile.Write(&mainJoining, 1)

// the main thread closing a scope whose close waits on its flows (the serve
// drain, then its joins): a teardown landing meanwhile holds the signal
// backstops until the close ends [D:serve-drain-signal]
let mutable private mainClosing = 0

/// run a scope's close; on the main thread the signal backstops stand by
/// for it, as they do for a cleanup
let closingScope (f: unit -> 'a) : 'a =
    if onMainThread () then
        System.Threading.Interlocked.Increment &mainClosing |> ignore

        try
            f ()
        finally
            System.Threading.Interlocked.Decrement &mainClosing |> ignore
    else
        f ()

// the signal backstops' verdict (the watchdog, the terminal ^C's grace):
// stand down for good when the main thread unwinds next (raised, or joining
// arms), stand by while it runs a cleanup or closes a scope or `ready` is
// false, else fire — and no cleanup starts on the main thread after
// [D:teardown-never-preempts] [D:teardown-joins-arms]
let private backstopVerdict (ready: bool) : bool option =
    lock watchdogGate (fun () ->
        if
            System.Threading.Volatile.Read &teardownRaised <> 0
            || System.Threading.Volatile.Read &mainJoining <> 0
        then
            Some false
        elif mainCleanups > 0 || System.Threading.Volatile.Read &mainClosing > 0 || not ready then
            None
        else
            watchdogFired <- true
            Some true)

// ---- weir's one stdin reader [D:stdin-reader] --------------------------
// Self.prompt, Self.stdin and the piped REPL read fd 0 through one buffer,
// so no reader hides input from another, and bytes are read only while a
// reader waits. A prompt takes one byte at a time and stops at its
// newline, so a child spawned later reads the rest; a stream read
// (Self.stdin, the piped REPL) takes what is there. A wait the runtime
// cuts (a lost race, a REPL ^C, a teardown) polls beside its tokens and
// raises without reading further: a line it had begun stays buffered
// for the next weir reader, and a cut never consumes a line. Windows
// reads block, uncut.
module Stdin =
    // one reader at a time; a waiting one stays cuttable
    let private gate = obj ()
    // fd 0's bytes read, not yet handed out: buf[lo..hi)
    let mutable private buf: byte[] = Array.zeroCreate 4096
    let mutable private lo = 0
    let mutable private hi = 0
    // where the terminator scan resumes, so a long line scans once
    let mutable private scanned = 0

    let private winStdin = lazy (System.Console.OpenStandardInput())

    let private winTty () =
        System.OperatingSystem.IsWindows() && not System.Console.IsInputRedirected

    // wait until fd 0 is readable, raising when the wait is cut
    let private awaitReadable () =
        if not (System.OperatingSystem.IsWindows()) && not (List.isEmpty (wakeHandles ())) then
            raiseWoken ()

            while not (Libc.readable 0 50) do
                raiseWoken ()

    // one read(2) of up to `n` bytes at buf[hi]: the count, or -errno
    let private readRaw (n: int) : int =
        let h =
            System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned)

        try
            let r = int (Libc.read (0, h.AddrOfPinnedObject() + nativeint hi, unativeint n))

            if r >= 0 then
                hi <- hi + r
                r
            else
                -System.Runtime.InteropServices.Marshal.GetLastPInvokeError()
        finally
            h.Free()

    // read up to `n` more bytes into the buffer; 0 at end of input (a
    // read error is the end too)
    let private fill (n: int) : int =
        if lo = hi then
            lo <- 0
            hi <- 0
            scanned <- 0
        elif hi + n > buf.Length && lo > 0 then
            System.Buffer.BlockCopy(buf, lo, buf, 0, hi - lo)
            hi <- hi - lo
            scanned <- scanned - lo
            lo <- 0

        if hi + n > buf.Length then
            let bigger = Array.zeroCreate (max (buf.Length * 2) (hi + n))
            System.Buffer.BlockCopy(buf, 0, bigger, 0, hi)
            buf <- bigger

        if System.OperatingSystem.IsWindows() then
            let got = winStdin.Force().Read(buf, hi, n)
            hi <- hi + got
            got
        else
            let rec go () =
                awaitReadable ()

                match readRaw n with
                | r when r >= 0 -> r
                | e when -e = Libc.EINTR -> go ()
                | e when -e = Libc.EAGAIN ->
                    // an inherited non-blocking fd 0
                    Libc.readable 0 50 |> ignore
                    go ()
                | _ -> 0

            go ()

    // a cut landing with a line begun in the buffer [D:prompt-cut-whole-line]:
    // the bytes fd 0 already holds finish that line, so a child spawned next
    // never starts mid-line; the line stays buffered, unconsumed. Bytes not
    // yet written are not waited for
    let private finishBegunLine (stream: bool) =
        if not (System.OperatingSystem.IsWindows()) && hi > lo then
            let ended () =
                let span = System.ReadOnlySpan<byte>(buf, lo, hi - lo)

                if stream then
                    System.MemoryExtensions.IndexOfAny(span, 10uy, 13uy) >= 0
                else
                    System.MemoryExtensions.IndexOf(span, 10uy) >= 0

            let mutable go = not (ended ())

            while go && Libc.readable 0 0 do
                if hi + 1 > buf.Length then
                    let bigger = Array.zeroCreate (buf.Length * 2)
                    System.Buffer.BlockCopy(buf, 0, bigger, 0, hi)
                    buf <- bigger

                match readRaw 1 with
                | 1 -> go <- not (ended ())
                | e when -e = Libc.EINTR -> ()
                | _ -> go <- false

        scanned <- lo

    // the next whole line in the buffer: a stream read breaks at \n, \r
    // and \r\n (a StreamReader's lines), a prompt at \n with one \r before
    // it dropped. None when more bytes are needed (at the end, the rest)
    let private takeLine (stream: bool) (atEnd: bool) : string option =
        let span = System.ReadOnlySpan<byte>(buf, scanned, hi - scanned)

        let at =
            if stream then
                System.MemoryExtensions.IndexOfAny(span, 10uy, 13uy)
            else
                System.MemoryExtensions.IndexOf(span, 10uy)

        let decode (n: int) =
            System.Text.Encoding.UTF8.GetString(buf, lo, n)

        if at >= 0 then
            let j = scanned + at

            if buf[j] = 13uy && j + 1 = hi && not atEnd then
                // a \r at the end may begin a \r\n: read on
                scanned <- j
                None
            else
                let line =
                    if not stream && j > lo && buf[j - 1] = 13uy then
                        decode (j - 1 - lo)
                    else
                        decode (j - lo)

                lo <- if buf[j] = 13uy && j + 1 < hi && buf[j + 1] = 10uy then j + 2 else j + 1
                scanned <- lo
                Some line
        elif atEnd && lo < hi then
            let line = decode (hi - lo)
            lo <- hi
            scanned <- hi
            Some line
        else
            scanned <- hi
            None

    let private readWith (stream: bool) : string =
        if winTty () then
            System.Console.In.ReadLine()
        else
            while not (System.Threading.Monitor.TryEnter(gate, 50)) do
                raiseWoken ()

            try
                let chunk = if stream then 65536 else 1
                // where the line taken begins (a fill may move the buffer)
                let mutable start = lo
                let mutable line = takeLine stream false
                let mutable atEnd = false
                let mutable waited = false

                while line.IsNone && not atEnd do
                    waited <- true

                    let got =
                        try
                            fill chunk
                        with _ ->
                            finishBegunLine stream
                            reraise ()

                    if got = 0 then
                        atEnd <- true

                    start <- lo
                    line <- takeLine stream atEnd

                match line with
                | Some l when waited ->
                    // a cut landing as the line completes leaves it too
                    try
                        raiseWoken ()
                    with _ ->
                        lo <- start
                        scanned <- start
                        reraise ()

                    l
                | Some l -> l
                | None -> null
            finally
                System.Threading.Monitor.Exit gate

    /// one line for Self.prompt [D:prompt], reading nothing past its
    /// newline; null at the end of input
    let readPromptLine () : string = readWith false

    /// the next line of the stream (Self.stdin, the piped REPL); null at
    /// the end of input
    let readLine () : string = readWith true

    /// put a line read ahead back in front of the buffer, so the next weir
    /// reader — a `Self.prompt` included — takes it [D:repl-lookahead-unread]
    let unread (line: string) : unit =
        lock gate (fun () ->
            let bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n")
            let n = bytes.Length

            if lo >= n then
                lo <- lo - n
            else
                let bigger = Array.zeroCreate (max buf.Length (n + hi - lo))
                System.Buffer.BlockCopy(buf, lo, bigger, n, hi - lo)
                hi <- n + hi - lo
                lo <- 0
                buf <- bigger

            System.Buffer.BlockCopy(bytes, 0, buf, lo, n)
            scanned <- lo)

    /// a terminal answer without the key sequences cooked mode passes
    /// through [D:prompt-tty-keys]: CSI (ESC [ … final) and SS3 (ESC O x) —
    /// an arrow, Home or Delete is ignored
    let dropKeySequences (line: string) : string =
        if not (line.Contains '\u001b') then
            line
        else
            let sb = System.Text.StringBuilder(line.Length)
            let mutable i = 0

            while i < line.Length do
                let c = line[i]

                if c = '\u001b' && i + 1 < line.Length && line[i + 1] = '[' then
                    // parameters and intermediates, then one final byte
                    let mutable j = i + 2

                    while j < line.Length && line[j] >= '\u0020' && line[j] <= '\u003f' do
                        j <- j + 1

                    i <- if j < line.Length && line[j] >= '\u0040' && line[j] <= '\u007e' then j + 1 else j
                elif c = '\u001b' && i + 1 < line.Length && line[i + 1] = 'O' then
                    i <- min line.Length (i + 3)
                else
                    sb.Append c |> ignore
                    i <- i + 1

            sb.ToString()

// an unseekable path File.read opens — a FIFO, /dev/stdin on a pipe, a
// tty [D:fifo-read-polled]: opened non-blocking (so the open waits for no
// writer) and polled beside the cut, so a lost race, a REPL ^C or a
// teardown raises at the poll and the dispose closes the fd — no thread is
// left inside read(2) to take a later writer's bytes. Linux, where a
// fresh reader's poll stays quiet until a writer arrives
type private PolledFd(fd: int) =
    inherit System.IO.Stream()
    let mutable closed = false

    // wait until fd is readable (data, EOF or an error), raising when the
    // wait is cut
    let await () =
        match wakeHandles () with
        | [] ->
            while not (Libc.readable fd 1000) do
                ()
        | _ ->
            raiseWoken ()

            while not (Libc.readable fd 50) do
                raiseWoken ()

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (System.NotSupportedException())

    override _.Position
        with get () = raise (System.NotSupportedException())
        and set (_: int64) = raise (System.NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_: int64, _: System.IO.SeekOrigin) : int64 = raise (System.NotSupportedException())
    override _.SetLength(_: int64) = raise (System.NotSupportedException())
    override _.Write(_: byte[], _: int, _: int) = raise (System.NotSupportedException())

    override _.Read(buffer: byte[], offset: int, count: int) : int =
        let rec go () =
            await ()

            let h =
                System.Runtime.InteropServices.GCHandle.Alloc(
                    buffer,
                    System.Runtime.InteropServices.GCHandleType.Pinned
                )

            let r =
                try
                    int (Libc.read (fd, h.AddrOfPinnedObject() + nativeint offset, unativeint count))
                finally
                    h.Free()

            if r >= 0 then
                r
            else
                match System.Runtime.InteropServices.Marshal.GetLastPInvokeError() with
                | e when e = Libc.EINTR || e = Libc.EAGAIN -> go ()
                | e -> raise (System.IO.IOException $"read failed (errno {e})")

        if count = 0 then 0 else go ()

    override _.Dispose(disposing: bool) =
        if not closed then
            closed <- true
            Libc.close fd |> ignore

        base.Dispose disposing

/// a polled stream for an unseekable path [D:fifo-read-polled]; None for a
/// seekable one (or off Linux, or a path open(2) refuses) — the caller opens
/// it as a file
let openPolled (path: string) : System.IO.Stream option =
    if not (System.OperatingSystem.IsLinux()) then
        None
    else
        let O_NONBLOCK, O_CLOEXEC = 0x800, 0x80000
        let fd = Libc.``open`` (path, O_NONBLOCK ||| O_CLOEXEC)

        if fd < 0 then
            None
        elif Libc.lseek (fd, 0L, 1) >= 0L then
            Libc.close fd |> ignore
            None
        else
            Some(new PolledFd(fd) :> System.IO.Stream)

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
                // the signal teardown's kill: what the flows wait on dies
                // now, not at the exit [D:epipe-kills-at-teardown]
                killLiveChildren true
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

// a live tmp/proc/serve/always scope: the full hook's SIGINT is a tree
// kill only while one is open — a terminal ^C after every scope has
// closed is the unscoped one [D:sigint-live-scopes] (a `within proc`
// holds its spill dir for its lifetime)
let private scoped () =
    not liveTmpDirs.IsEmpty || not liveAlways.IsEmpty

// the second signal's hard exit [D:signal-teardown]: the sweep (the
// foreground reaper alone has no ProcessExit hook to run it), then 128 +
// the signal; a second ^C after a terminal ^C dies by the SIGINT
let private hardExit (code: int) =
    sweepLiveTmpDirs ()

    if dieBySigint && code = 130 then
        raiseSigint ()

    System.Environment.Exit code

// the terminal's ^C reached the children with weir [D:sigint-terminal-group]:
// no kill — the script waits for its child (its own ^C handling runs) and
// then unwinds; without a live child to wait for, it exits after a grace
// period, held as the watchdog is (a cleanup, a scope close, a join)
// [D:terminal-sigint-teardown]
let private terminalInterrupt (ctx: System.Runtime.InteropServices.PosixSignalContext) =
    ctx.Cancel <- true
    dieBySigint <- true
    requestTeardown 130

    let watch =
        System.Threading.Thread(fun () ->
            let rec standBy idle =
                System.Threading.Thread.Sleep 100
                let idle = if liveProcs.IsEmpty then idle + 1 else 0

                match backstopVerdict (idle >= 10) with
                | None -> standBy idle
                | Some f -> f

            if standBy 0 then
                raiseSigint ())

    watch.IsBackground <- true
    watch.Start()

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
        resetDetachedSigint.Force()

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
                            hardExit (signalExitCode ctx.Signal)
                        elif
                            mainThreadId <> 0
                            && ctx.Signal = System.Runtime.InteropServices.PosixSignal.SIGINT
                            && not (scoped ())
                            && Libc.terminalForeground ()
                        then
                            terminalInterrupt ctx
                        elif mainThreadId <> 0 then
                            // first signal, a script running: the main
                            // thread tears down [D:signal-main-unwind] —
                            // kill what it waits on, wake its sleeps, and
                            // keep the process alive for the unwind
                            ctx.Cancel <- true

                            let code = signalExitCode ctx.Signal

                            requestTeardown code
                            killLiveChildren true

                            // the backstop: a main thread blocked where
                            // no boundary comes (a network call) cannot
                            // unwind — after a grace period the old
                            // in-handler teardown runs and the process
                            // exits; it runs no statement either. One
                            // joining arms unwinds next and is left be; one
                            // in a cleanup or closing a scope runs it whole
                            // first [D:teardown-never-preempts]
                            let watchdog =
                                System.Threading.Thread(fun () ->
                                    let rec standBy () =
                                        System.Threading.Thread.Sleep 1000

                                        match backstopVerdict true with
                                        | None -> standBy ()
                                        | Some f -> f

                                    if standBy () then
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

// a foreground child alone installs only a reaper [D:signal-main-unwind]:
// a pid-targeted TERM, HUP or INT tree-kills the live children, then
// default termination proceeds (weir still dies BY the signal, as before
// any scope exists). SIGINT joins them [D:sigint-reaps]: a `kill -INT` of
// weir's pid reaches no child, and a terminal ^C that already reached the
// group is the same stop; the REPL survives its ^C and is skipped, an
// inherited SIG_IGN (a backgrounded weir) keeps the handler unbound. In a
// script the teardown is requested before the kill, so a child or arm the
// kill ends is the teardown, never a command failure [D:teardown-kill-not-failure].
// Once a scope installs the full hook, that hook does the kill as part of
// its teardown, and takes the terminal ^C while no scope is live; this one
// stands down. POSIX only.
let private fgHookInstalled = ref 0

let registerForeground (p: System.Diagnostics.Process) : unit =
    if
        not (System.OperatingSystem.IsWindows())
        && System.Threading.Interlocked.Exchange(fgHookInstalled, 1) = 0
    then
        resetDetachedSigint.Force()

        // a pid-targeted HUP reaps the same way [D:sighup-teardown]
        for posixSig in
            [ System.Runtime.InteropServices.PosixSignal.SIGTERM
              System.Runtime.InteropServices.PosixSignal.SIGHUP
              System.Runtime.InteropServices.PosixSignal.SIGINT ] do
            let reg =
                System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    posixSig,
                    fun ctx ->
                        // the full hook owns the signal once installed: it must
                        // request the teardown BEFORE any child dies, or the
                        // main thread's wait returns and runs the next statement
                        let sigint = ctx.Signal = System.Runtime.InteropServices.PosixSignal.SIGINT

                        if
                            System.Threading.Volatile.Read &hookInstalled.contents = 0
                            && not (replSurvivesSigint.Value && sigint)
                        then
                            // a second signal mid-teardown hard-exits
                            // [D:signal-teardown], as the full hook's does
                            if System.Threading.Interlocked.Exchange(tearingDown, 1) = 1 then
                                hardExit (signalExitCode ctx.Signal)
                            elif sigint && Libc.terminalForeground () then
                                if mainThreadId <> 0 then
                                    terminalInterrupt ctx
                            else
                                if mainThreadId <> 0 then
                                    requestTeardown (signalExitCode ctx.Signal)

                                killLiveChildren true
                                settleKills ()
                )

            hookRoots <- (reg :> obj) :: hookRoots

    if inCleanup () then
        cleanupProcs[p] <- ()

    liveProcs[p.Id] <- p

/// remove `p` under `pid` — only that very process, so a late exit
/// event never drops a newer child that reused the pid
let deregisterProcAt (pid: int) (p: System.Diagnostics.Process) : unit =
    liveProcs.TryRemove(System.Collections.Generic.KeyValuePair(pid, p)) |> ignore
    cleanupProcs.TryRemove p |> ignore

let deregisterProc (p: System.Diagnostics.Process) : unit = deregisterProcAt p.Id p

let registerAlways (cleanup: unit -> unit) : int =
    if System.Threading.Interlocked.Exchange(hookInstalled, 1) = 0 then
        installExitHook ()

    let id = System.Threading.Interlocked.Increment alwaysCounter

    match armMark.Value with
    | null -> ()
    | m -> armAlways[id] <- (m, System.Threading.ExecutionContext.Capture())

    liveAlways[id] <- cleanup
    id

// claim-by-removal: true when the caller took the cleanup back from the
// exit hook — the hook claims the same way, so a signal racing a raise
// cannot run one cleanup twice [D:within-always-any]
let deregisterAlways (id: int) : bool =
    armAlways.TryRemove id |> ignore
    fst (liveAlways.TryRemove id)
