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

// the teardown kill [D:signal-main-unwind]: spawning stops, then every
// live child's tree dies exactly once. Spawns hold the gate shared from
// Process.Start through registration, so the kill (exclusive) sees every
// child a worker started; a spawn after it refuses. The main thread
// settles the same kill before exiting, so the process never exits with
// a tree half-killed (a SIGSTOPped grandchild left orphaned).
let private spawnGate =
    new System.Threading.ReaderWriterLockSlim(System.Threading.LockRecursionPolicy.SupportsRecursion)

let mutable private spawningStopped = false

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

// ---- the cancellation-scope tree [D:scope-tree] ------------------------
// Every flow (the main flow, a REPL entry, a fan-out or race arm, a serve
// handler) and every scope that has flows of its own (a race, a fan-out, a
// serve) is a node; a running cleanup is a shield node. The current node
// rides one AsyncLocal, so an arm's helpers inherit it. A node owns the
// children its flow spawned (they leave it as they exit, and a node that
// closes hands the live ones to its parent) and its cut: a cut marks the
// node and every node under it, stopping at shields, wakes their waits and,
// when it kills, tree-kills their children. A flow checks its own node and
// nothing else.
type NodeKind =
    | Root = 0
    | Flow = 1
    | Race = 2
    | Fan = 3
    | Arm = 4
    | Serve = 5
    | Handler = 6
    | Shield = 7

/// why a node was cut (flags) — what its flows raise follows from it
module CutReason =
    let Teardown = 1
    let Soft = 2
    let RaceLost = 4
    let FanExit = 8
    let ServeCut = 16
    let HandlerExit = 32
    let Repl = 64

[<AllowNullLiteral>]
type Node(parent: Node, kind: NodeKind) =
    /// 0 uncut, 1 cut, 2 cut through shields (a second REPL ^C)
    [<DefaultValue>]
    val mutable Cut: int

    [<DefaultValue>]
    val mutable Reasons: int

    // a killing cut reached it: children registering now die at once
    [<DefaultValue>]
    val mutable Killing: bool

    // takes no new arm (a won race)
    [<DefaultValue>]
    val mutable Closed: bool

    // left the tree
    [<DefaultValue>]
    val mutable Detached: bool

    [<DefaultValue>]
    val mutable Cts: System.Threading.CancellationTokenSource

    // the token a wait on a child holds: a soft teardown leaves it be
    [<DefaultValue>]
    val mutable HardCts: System.Threading.CancellationTokenSource

    [<DefaultValue>]
    val mutable Kids: System.Collections.Generic.HashSet<Node>

    [<DefaultValue>]
    val mutable internal Procs: System.Collections.Generic.HashSet<ProcEntry>

    /// the `exit n` a fan-out or serve scope raises once its flows joined
    [<DefaultValue>]
    val mutable Exit: exn

    member _.Parent = parent
    member _.Kind = kind

// a live child and the node that owns it now: a node that closes hands its
// live children to its parent, so the owner moves (under both locks)
and [<AllowNullLiteral>] internal ProcEntry(p: System.Diagnostics.Process, owner: Node) =
    member _.P = p

    [<DefaultValue>]
    val mutable Killed: int

    member val Owner: Node = owner with get, set

// the hook's second customer [D:scoped-procs]: every live child, with the
// node that owns it — the teardown's kill walks the tree, the exit's and the
// hard exit's kill them all (a spilling child holds its spill dir open, so
// the kill before the dirs go is load-bearing on Windows)
let private liveProcs =
    System.Collections.Concurrent.ConcurrentDictionary<System.Diagnostics.Process, ProcEntry>(
        HashIdentity.Reference
    )

// cut nodes still in the tree: the per-builtin check's fast path reads this
// alone
let mutable private cutsPending = 0

// the hard exit froze every flow [D:hard-exit]: a flow reaching any runtime
// point now parks there, never to run again
let mutable private hardExiting = 0

let private park () =
    System.Threading.Thread.Sleep System.Threading.Timeout.Infinite

let private rootNode = Node(null, NodeKind.Root)

let private refEq = HashIdentity.Reference<Node>

/// open a node under `parent`; it is born cut when its parent is (a shield
/// only by a cut that passes shields)
let openNode (parent: Node) (kind: NodeKind) : Node =
    let n = Node(parent, kind)

    lock parent (fun () ->
        if isNull parent.Kids then
            parent.Kids <- System.Collections.Generic.HashSet<Node>(refEq)

        parent.Kids.Add n |> ignore

        if parent.Cut > 0 && (kind <> NodeKind.Shield || parent.Cut = 2) then
            n.Cut <- parent.Cut
            n.Reasons <- parent.Reasons
            n.Killing <- parent.Killing && kind <> NodeKind.Shield
            System.Threading.Interlocked.Increment &cutsPending |> ignore)

    n

// the default flow: the main thread's (and, in-process, every evaluation's
// with no node of its own)
let private mainNode = openNode rootNode NodeKind.Flow

let private current = System.Threading.AsyncLocal<Node>()

/// the node this flow runs in
let currentNode () : Node =
    match current.Value with
    | null -> mainNode
    | n -> n

/// make `n` this flow's node (null: the default flow)
let setCurrentNode (n: Node) : unit = current.Value <- n

// a child is killed once, whoever reaches it first [D:kill-settle]
let private killEntry (e: ProcEntry) =
    if System.Threading.Interlocked.Exchange(&e.Killed, 1) = 0 then
        killTree e.P

// a node's cut [D:scope-tree]: mark it and every node under it (a shield
// only by a level-2 cut), cancel the tokens their waits hold and, when the
// cut kills, tree-kill every child they own — a child registering after it
// dies at registration. A node opening takes its parent's cut under the
// parent's lock, so no node escapes a cut in flight
let rec private cutFrom (n: Node) (level: int) (reason: int) (kill: bool) (origin: bool) =
    if origin || n.Kind <> NodeKind.Shield || level >= 2 then
        let mutable kids: Node[] = null
        let mutable procs: ProcEntry[] = null

        lock n (fun () ->
            if n.Cut = 0 && not n.Detached then
                System.Threading.Interlocked.Increment &cutsPending |> ignore

            if level > n.Cut then
                n.Cut <- level

            n.Reasons <- n.Reasons ||| reason

            if kill && not n.Killing then
                n.Killing <- true

                if not (isNull n.Procs) then
                    procs <- Array.ofSeq n.Procs

            if not (isNull n.Kids) then
                kids <- Array.ofSeq n.Kids)

        let soft =
            (n.Reasons &&& CutReason.Soft) <> 0
            && not n.Killing
            && (n.Reasons &&& ~~~(CutReason.Teardown ||| CutReason.Soft)) = 0

        for c in [ System.Threading.Volatile.Read &n.Cts
                   (if soft then null else System.Threading.Volatile.Read &n.HardCts) ] do
            if not (isNull c) then
                try
                    c.Cancel()
                with _ ->
                    ()

        if not (isNull procs) then
            for e in procs do
                killEntry e

        if not (isNull kids) then
            for k in kids do
                cutFrom k level reason kill false

/// cut `n` and its subtree (level 2 passes shields); `kill` tree-kills the
/// children they own
let cutNode (n: Node) (level: int) (reason: int) (kill: bool) : unit = cutFrom n level reason kill true

/// the children `n` owns now
let ownedCount (n: Node) : int =
    lock n (fun () -> if isNull n.Procs then 0 else n.Procs.Count)

/// has a cut reached `n`?
let isCut (n: Node) : bool = n.Cut > 0

/// the nodes under `n` now
let nodeKids (n: Node) : Node[] =
    lock n (fun () -> if isNull n.Kids then [||] else Array.ofSeq n.Kids)

/// a new arm under `parent`, or null once it takes none (won, or cut)
let tryOpenArm (parent: Node) : Node =
    let isOpen = lock parent (fun () -> not parent.Closed && parent.Cut = 0)

    if isOpen then openNode parent NodeKind.Arm else null

/// stop `n` taking arms (a race won); true for the one caller that closed it
let closeToArms (n: Node) : bool =
    lock n (fun () ->
        if n.Closed then
            false
        else
            n.Closed <- true
            true)

/// leave the tree; the live children go to the parent
let closeNode (n: Node) : unit =
    let p = n.Parent

    let detach () =
        if not n.Detached then
            n.Detached <- true

            if not (isNull p.Kids) then
                p.Kids.Remove n |> ignore

            if n.Cut > 0 then
                System.Threading.Interlocked.Decrement &cutsPending |> ignore

            // live children outlive their node (a streamed result): the
            // parent owns them now
            if not (isNull n.Procs) && n.Procs.Count > 0 then
                if isNull p.Procs then
                    p.Procs <- System.Collections.Generic.HashSet<ProcEntry>(HashIdentity.Reference)

                for e in n.Procs do
                    e.Owner <- p
                    p.Procs.Add e |> ignore

                    if p.Killing then
                        killEntry e

                n.Procs.Clear()

    lock n (fun () -> lock p detach)

/// register a child with this flow's node (killed at once when a killing
/// cut already reached it) [D:scope-tree]
let registerChild (p: System.Diagnostics.Process) : unit =
    let n = currentNode ()
    let e = ProcEntry(p, n)
    liveProcs[p] <- e

    let late =
        lock n (fun () ->
            if n.Killing then
                true
            else
                if isNull n.Procs then
                    n.Procs <- System.Collections.Generic.HashSet<ProcEntry>(HashIdentity.Reference)

                n.Procs.Add e |> ignore
                false)

    if late then
        killEntry e

// a child left: out of the index and out of whichever node owns it now
let private forgetChild (p: System.Diagnostics.Process) =
    match liveProcs.TryRemove p with
    | true, e ->
        let rec go () =
            let n = e.Owner

            let removed =
                lock n (fun () ->
                    if obj.ReferenceEquals(e.Owner, n) then
                        if not (isNull n.Procs) then
                            n.Procs.Remove e |> ignore

                        true
                    else
                        false)

            if not removed then
                go ()

        go ()
    | _ -> ()

// an `always` block in progress on this logical flow [D:teardown-always-spawns]:
// the teardown stops the body's spawns, never a cleanup's — teardown
// commands are what `always` is for
let private cleanupDepth = System.Threading.AsyncLocal<int>()

let mutable private mainThreadId = 0

let private onMainThread () =
    mainThreadId <> 0
    && System.Threading.Thread.CurrentThread.ManagedThreadId = mainThreadId

// the main thread inside an `always` cleanup [D:teardown-never-preempts]:
// the signal backstop stands by for it, and once the backstop has
// adopted the main flow no cleanup starts on the main thread
let private watchdogGate = obj ()
let mutable private mainCleanups = 0
let mutable private watchdogFired = false

/// run an `always` cleanup: spawns inside it start even after the teardown
/// kill, a parallel fan-out inside it keeps taking arms, and a requested
/// teardown never preempts it [D:teardown-never-preempts]
let runningCleanup (f: unit -> 'a) : 'a =
    if System.Threading.Volatile.Read &hardExiting <> 0 then
        park ()

    let onMain = onMainThread ()

    if onMain then
        lock watchdogGate (fun () ->
            if watchdogFired then
                // the backstop adopted the flow: it owns the cleanups
                System.Threading.Thread.Sleep System.Threading.Timeout.Infinite

            mainCleanups <- mainCleanups + 1)

    cleanupDepth.Value <- cleanupDepth.Value + 1
    // the cleanup's own node [D:scope-tree]: no cut from its parent reaches it
    let home = current.Value
    let shield = openNode (currentNode ()) NodeKind.Shield
    current.Value <- shield

    try
        f ()
    finally
        current.Value <- home
        closeNode shield
        cleanupDepth.Value <- cleanupDepth.Value - 1

        if onMain then
            lock watchdogGate (fun () -> mainCleanups <- mainCleanups - 1)

let inCleanup () = cleanupDepth.Value > 0

// ---- the main flow's frames [D:scope-tree] -----------------------------
// Each scope the main flow opens pushes its release (a cleanup, a tmp dir,
// a proc, a serve close, a lock, a cd or env restore); the scope's own exit
// claims and runs it. The one other owner is the signal backstop, which
// adopts a main flow stuck where no boundary comes: it runs the frames
// still pending, innermost first, and the main thread, if it ever returns,
// parks at its next claim — no release runs twice or concurrently with an
// enclosing one. An arm's scopes are never adopted (every wait is cut, so
// an arm always reaches its own exit): they push nothing.

/// a scope's pending release on the main flow
[<AllowNullLiteral>]
type Frame(release: unit -> unit) =
    member _.Release = release

    [<DefaultValue>]
    val mutable Claimed: bool

let private mainFrames = System.Collections.Generic.List<Frame>()

/// push a release on the main flow's frames (null off the main flow)
let pushFrame (release: unit -> unit) : Frame =
    if onMainThread () then
        let f = Frame(release)
        lock watchdogGate (fun () -> mainFrames.Add f)
        f
    else
        null

/// run `f`'s release as its scope exits; when the backstop adopted the main
/// flow, this thread never runs again
let releaseFrame (f: Frame) (release: unit -> unit) : unit =
    if isNull f then
        release ()
    else
        if System.Threading.Volatile.Read &hardExiting <> 0 then
            park ()

        let mine =
            lock watchdogGate (fun () ->
                if watchdogFired || f.Claimed then
                    false
                else
                    f.Claimed <- true
                    mainFrames.Remove f |> ignore
                    mainCleanups <- mainCleanups + 1
                    true)

        if not mine then
            System.Threading.Thread.Sleep System.Threading.Timeout.Infinite

        try
            release ()
        finally
            lock watchdogGate (fun () -> mainCleanups <- mainCleanups - 1)

// the backstop adopts the main flow: its pending releases run here, LIFO,
// each in a cleanup's node
let private adoptMain () =
    let rec next () =
        let f =
            lock watchdogGate (fun () ->
                let mutable i = mainFrames.Count - 1

                while i >= 0 && mainFrames[i].Claimed do
                    i <- i - 1

                if i < 0 then
                    null
                else
                    let f = mainFrames[i]
                    f.Claimed <- true
                    mainFrames.RemoveAt i
                    f)

        if not (isNull f) then
            // a raising cleanup says so, as on the main flow; the teardown
            // stays the outcome
            (try
                runningCleanup f.Release
             with
             | ExitRequest _ -> ()
             | ce -> eprintfn "within/always: the cleanup ALSO failed — %s (the original error wins)" ce.Message)

            next ()

    next ()


// ---- the main-thread teardown [D:signal-main-unwind] [D:stdout-epipe] --
// A signal or a closed stdout ends the script, but the teardown — every
// `always`, every scope's release — runs on the MAIN thread, never in the
// signal handler: the handler records the exit code and cuts the tree's
// root (killing what the flows wait on, sparing what cleanups own); the
// main thread raises ExitRequest at its next evaluation or wait boundary
// and unwinds through its scopes, so no statement runs after the request.
// Registered by the script runner and -e; a REPL entry stands in for it
// meanwhile, and with no flow at all the handler kills, sweeps the dirs
// and dies [D:scope-tree].

let mutable private teardownCode = 0
let mutable private teardownRaised = 0

// a second signal during teardown hard-exits [D:signal-teardown]
// [D:hard-exit] — the shell's double-Ctrl+C escape, so a slow or stuck
// cleanup can never wedge a supervisor. Set by the first signal and by a
// closed stdout's teardown
let private tearingDown = ref 0

// the REPL's evaluation is the flow a teardown unwinds (its thread stands
// in for the main thread meanwhile)
let mutable private replFlow = false

// signals the handler has received and not yet acted on — what a settle
// wait waits for [D:settle-pending]
let mutable private signalsInFlight = 0

let registerMainThread () : unit =
    mainThreadId <- System.Threading.Thread.CurrentThread.ManagedThreadId

/// record a teardown's exit code — the first request wins — and cut the
/// tree's root [D:scope-tree]: every flow outside a cleanup stops at its
/// next boundary. `soft`: the terminal's ^C reached the children with
/// weir, so a wait on a child lasts while the child does
/// [D:sigint-terminal-group]
let private requestTeardownWith (soft: bool) (code: int) : unit =
    System.Threading.Interlocked.CompareExchange(&teardownCode, code, 0) |> ignore

    let reason =
        if soft then
            CutReason.Teardown ||| CutReason.Soft
        else
            CutReason.Teardown

    cutNode rootNode 1 reason false

let requestTeardown (code: int) : unit = requestTeardownWith false code

// ---- cooperative cancellation [D:pfirst-join] [D:repl-interrupt] -------
// A cut stops a flow at its next runtime-owned boundary [D:scope-tree]: a
// race's loser, a fan-out's arms after an `exit n`, a serve handler past
// the drain, a REPL evaluation's flows after ^C, every flow after a
// teardown. Each raises an OperationCanceledException, which `try` and
// retry pass through as they pass `exit`.

/// a pfirst loser stopped at a boundary — swallowed by the race
type Cancelled() =
    inherit System.OperationCanceledException("a pfirst arm that lost the race was stopped")

/// a ^C at the REPL cut a runtime-owned wait — the evaluation ends
type Interrupted() =
    inherit System.OperationCanceledException("interrupted (SIGINT — interrupted/cancelled)")

// the cuts a flow raises Cancelled for (a teardown off the main thread too)
let private cancelReasons =
    CutReason.RaceLost
    ||| CutReason.FanExit
    ||| CutReason.ServeCut
    ||| CutReason.HandlerExit

let private cancelledBy (n: Node) =
    let r = n.Reasons

    (r &&& cancelReasons) <> 0
    || ((r &&& CutReason.Teardown) <> 0 && not (onMainThread ()))

/// raise Cancelled when this flow's node was cut (a race lost, a fan-out
/// exit, the serve cut, a teardown off the main thread) — the effect boundary
let checkCancelled () : unit =
    if System.Threading.Volatile.Read &cutsPending <> 0 then
        if System.Threading.Volatile.Read &hardExiting <> 0 then
            park ()

        let n = currentNode ()

        if n.Cut > 0 && cancelledBy n then
            raise (Cancelled())

// the REPL's interrupt, one evaluation at a time: the first ^C cuts the
// evaluation's node (every wait outside an `always` cleanup), a further ^C
// cuts through the cleanups too — a cleanup runs as written unless ^C is
// pressed again
let mutable private replInterrupts = 0
let mutable private replNode: Node = null

// an entry's statements share one evaluation: the nested begin/end of each
// statement's evaluation inside an entry's are no-ops
let mutable private replDepth = 0

let private beginReplEvalNode () =
    replInterrupts <- 0
    let n = openNode rootNode NodeKind.Flow
    System.Threading.Volatile.Write(&replNode, n)
    current.Value <- n
    replFlow <- true
    System.Threading.Volatile.Write(&mainThreadId, System.Threading.Thread.CurrentThread.ManagedThreadId)

let private endReplEvalNode () =
    match System.Threading.Interlocked.Exchange(&replNode, null) with
    | null -> ()
    | n ->
        System.Threading.Volatile.Write(&mainThreadId, 0)
        replFlow <- false
        current.Value <- null
        closeNode n

/// arm a fresh interrupt for one REPL evaluation (an entry, or a statement
/// outside one): its own node [D:scope-tree]
let beginReplEval () : unit =
    replDepth <- replDepth + 1

    if replDepth = 1 then
        beginReplEvalNode ()

/// disarm after the evaluation (a late ^C then cuts nothing)
let endReplEval () : unit =
    replDepth <- max 0 (replDepth - 1)

    if replDepth = 0 then
        endReplEvalNode ()

/// the REPL's SIGINT handler: cut the evaluation's waits
let interruptRepl () : unit =
    let k = System.Threading.Interlocked.Increment &replInterrupts

    match System.Threading.Volatile.Read &replNode with
    | null -> ()
    | n -> cutNode n (if k >= 2 then 2 else 1) CutReason.Repl false

/// has ^C cut this evaluation's waits? (a fan-out stops taking arms)
let interruptRequested () : bool =
    System.Threading.Volatile.Read &cutsPending <> 0
    && (let n = currentNode () in n.Cut > 0 && (n.Reasons &&& CutReason.Repl) <> 0)

/// raise Interrupted when ^C cut this evaluation's waits
let checkInterrupted () : unit =
    if interruptRequested () then
        raise (Interrupted())

/// raise whichever cancel stopped this flow (the race's, then ^C's)
let checkCancels () : unit =
    checkCancelled ()
    checkInterrupted ()

// a node's token, made at the first wait that needs one; cancelled by the
// node's cut. `hard`: the one a wait on a child holds — a soft teardown
// (the terminal's ^C, which reached the child too) leaves it uncancelled
let private tokenOf (n: Node) (hard: bool) : System.Threading.CancellationToken =
    lock n (fun () ->
        let soft = n.Cut > 0 && (n.Reasons &&& CutReason.Soft) <> 0 && not n.Killing && (n.Reasons &&& ~~~(CutReason.Teardown ||| CutReason.Soft)) = 0

        if hard then
            if isNull n.HardCts then
                n.HardCts <- new System.Threading.CancellationTokenSource()

                if n.Cut > 0 && not soft then
                    n.HardCts.Cancel()

            n.HardCts.Token
        else
            if isNull n.Cts then
                n.Cts <- new System.Threading.CancellationTokenSource()

                if n.Cut > 0 then
                    n.Cts.Cancel()

            n.Cts.Token)

/// `n`'s token (a join waiting for its arms to stop)
let nodeToken (n: Node) : System.Threading.CancellationToken = tokenOf n false

// the main thread unwinding a teardown: its waits run as written
// [D:signal-main-unwind] — the cleanups it runs are not cut short by the
// request they serve
let private unwinding (n: Node) =
    onMainThread ()
    && System.Threading.Volatile.Read &teardownRaised <> 0
    && (n.Reasons &&& ~~~(CutReason.Teardown ||| CutReason.Soft)) = 0

/// the token this flow's waits honour (None while the main thread unwinds)
let private waitToken (hard: bool) : System.Threading.CancellationToken =
    let n = currentNode ()

    if n.Cut > 0 && unwinding n then
        System.Threading.CancellationToken.None
    else
        tokenOf n hard

/// run `f` with the token that cancels when this flow's waits are cut —
/// for a wait the runtime hands to an API (an Http send); `checkCancels`
/// after it names why
let withWaitToken (f: System.Threading.CancellationToken -> 'a) : 'a = f (waitToken false)

/// run a process start + registration inside the spawn gate; refuses once
/// the teardown kill has run, except inside an `always` cleanup
let gatedSpawn (start: unit -> 'a) : 'a =
    // a spawn is an effect boundary [D:pfirst-join] — for a REPL ^C too,
    // so a fan-out's next arm starts nothing [D:repl-interrupt-fanout]
    checkCancels ()
    spawnGate.EnterReadLock()

    try
        // a killing cut reached this flow (outside a cleanup, whose node
        // no outer cut reaches)
        if (currentNode ()).Killing then
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

        // the teardown's kill walks the tree, sparing what cleanups own
        // [D:teardown-never-preempts]; the exit's kills every child
        if spareCleanups then
            cutNode rootNode 1 CutReason.Teardown true
        else
            for kv in liveProcs do
                killEntry kv.Value
    finally
        spawnGate.ExitWriteLock()

// pending `always` cleanups [D:within-always]: LIFO, run by the exit
// hook on signals/hard exits; a completed scope deregisters (its own
// finally already ran the cleanup). Entries are removed before running
// so a second hook firing (SIGTERM then ProcessExit) cannot run one
// twice.
let private liveAlways =
    System.Collections.Concurrent.ConcurrentDictionary<int, unit>()

let private alwaysCounter = ref 0

// the exit's sweep [D:exit-hook]: every live child killed (a spilling child
// holds its spill dir open, so the kill is load-bearing on Windows), every
// kill in flight settled, every registered dir deleted. No cleanup runs
// here: each scope's own owner ran its cleanups [D:scope-tree]. It runs at
// exit while other threads may still be about, so a vanished dir is
// benign and it never throws
let private sweepDirs () =
    for kv in liveProcs do
        killEntry kv.Value

        try
            kv.Key.WaitForExit 1000 |> ignore
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

    [<DllImport("libc", SetLastError = true)>]
    extern nativeint write(int fd, nativeint buf, unativeint count)

    // Linux statx: its struct is one layout on every architecture
    // (stx_mode is the u16 at byte 28)
    [<DllImport("libc", SetLastError = true)>]
    extern int statx(int dirfd, string path, int flags, uint32 mask, byte[] buf)

    // the special files Dir.copy recreates [D:dircopy-special]
    [<DllImport("libc", SetLastError = true)>]
    extern int mkfifo(string path, uint32 mode)

    [<DllImport("libc", EntryPoint = "mknod", SetLastError = true)>]
    extern int mknodLinux(string path, uint32 mode, uint64 dev)

    [<DllImport("libc", EntryPoint = "mknod", SetLastError = true)>]
    extern int mknodMac(string path, uint32 mode, int dev)

    // macOS's 64-bit-inode lstat: st_mode the u16 at byte 4, st_rdev the
    // i32 at byte 24
    [<DllImport("libc", EntryPoint = "lstat", SetLastError = true)>]
    extern int lstatMacArm(string path, byte[] buf)

    [<DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)>]
    extern int lstatMacX64(string path, byte[] buf)

    // the exit that runs no managed code (no ProcessExit sweep)
    [<DllImport("libc", EntryPoint = "_exit")>]
    extern void sysExit(int status)

    [<DllImport("libc", SetLastError = true)>]
    extern int fcntl(int fd, int cmd)

    [<DllImport("libc", SetLastError = true)>]
    extern int dup2(int oldfd, int newfd)

    [<DllImport("libc", SetLastError = true)>]
    extern int execv(string path, nativeint[] argv)

    // RLIMIT_CORE is 4 on Linux and macOS; a struct rlimit is two rlim_t
    [<DllImport("libc", SetLastError = true)>]
    extern int setrlimit(int resource, uint64[] rlim)

    // Linux: PR_SET_DUMPABLE 0 — a piped core_pattern ignores RLIMIT_CORE 0
    [<DllImport("libc", SetLastError = true)>]
    extern int prctl(int option, unativeint arg2, unativeint arg3, unativeint arg4, unativeint arg5)

    // struct termios fits 128 bytes on Linux and macOS
    [<DllImport("libc", SetLastError = true)>]
    extern int tcgetattr(int fd, byte[] termios)

    [<DllImport("libc", SetLastError = true)>]
    extern int tcsetattr(int fd, int optionalActions, byte[] termios)

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

    /// wait up to `ms` for room to write to fd (or an error)
    let writable (fd: int) (ms: int) : bool =
        let mutable p = PollFd()
        p.fd <- fd
        p.events <- 4s // POLLOUT
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

// ---- a cleanup's commands ignore the terminal's ^C and ^\ [D:cleanup-sigign]
// A terminal ^C reaches the terminal's foreground process group — weir's,
// and every child that shares it. A cleanup runs whole, its commands too
// [D:teardown-never-preempts], so a command an `always` cleanup starts at
// a foreground terminal starts with SIGINT and SIGQUIT ignored: an ignored
// disposition survives exec, and .NET resets only the handlers it set. The
// command stays in weir's group with the terminal and its real stdin (a
// pager works); a program that sets its own handler can still take the ^C.
// The shim is `sh`: its `exec` keeps the pid, so its pipes, its exit and
// the tree kill are the Process weir holds. Windows starts the command in
// a new process group, which Ctrl+C does not reach.
module CleanupShim =
    /// the shell that sets the dispositions and execs the command
    let shell = "/bin/sh"

    /// `sh -c <script> sh <program> <args...>`
    let script = "trap \"\" INT QUIT; exec \"$@\""

    /// Linux: has the started shim run its `trap` (INT and QUIT in its
    /// SigIgn) or exec'd past it? False when it died first: a ^C or ^\
    /// landed before the trap and the command never started, so the
    /// caller starts it again. Elsewhere that first moment stays open
    let passedTrap (p: System.Diagnostics.Process) : bool =
        if not (System.OperatingSystem.IsLinux()) then
            true
        else
            let status = $"/proc/{p.Id}/status"
            let clock = System.Diagnostics.Stopwatch.StartNew()

            let field (text: string) (name: string) =
                text.Split('\n')
                |> Array.tryPick (fun l ->
                    if l.StartsWith(name + ":") then
                        Some(l.Substring(name.Length + 1).Trim())
                    else
                        None)

            let rec go () =
                if p.HasExited then
                    p.ExitCode <> 130 && p.ExitCode <> 131
                elif clock.ElapsedMilliseconds > 2000L then
                    true
                else
                    let text =
                        try
                            System.IO.File.ReadAllText status
                        with _ ->
                            ""

                    let ignored =
                        match field text "SigIgn" with
                        | Some hex ->
                            (try
                                System.Convert.ToUInt64(hex, 16) &&& 6UL = 6UL
                             with _ ->
                                 false)
                        | None -> false

                    // a zombie is decided by its exit code, not its name
                    let alive =
                        match field text "State" with
                        | Some st -> not (st.StartsWith "Z" || st.StartsWith "X")
                        | None -> false

                    match field text "Name" with
                    | _ when ignored -> true
                    | Some name when alive && name <> "sh" -> true
                    | _ ->
                        System.Threading.Thread.Sleep(if clock.ElapsedMilliseconds < 2L then 0 else 1)
                        go ()

            go ()

    /// what execve itself runs [D:cleanup-sigign]: a native image — on Linux
    /// an ELF the kernel's loader takes (this machine's class and arch, an
    /// executable or shared-object type, whole program headers) — or a `#!`
    /// file whose interpreter execve runs in turn: on Linux up to five `#!`
    /// files deep (then ELOOP), on macOS one (no nested interpreter). The
    /// shim's `exec` would hand any other file to sh (its ENOEXEC fallback),
    /// which a direct spawn refuses; read without blocking, so a FIFO is
    /// never waited on
    let execAccepts (path: string) (cwd: string) (runnable: string -> bool) : bool =
        let O_NONBLOCK = if System.OperatingSystem.IsLinux() then 0x800 else 0x4
        let maxScripts = if System.OperatingSystem.IsLinux() then 5 else 1

        // the head and the file's length
        let headOf (path: string) =
            let fd = Libc.``open`` (path, O_NONBLOCK)

            if fd < 0 then
                None
            else
                let buf = System.Runtime.InteropServices.Marshal.AllocHGlobal 256

                try
                    let n = int (Libc.read (fd, buf, unativeint 256))
                    let bytes = Array.zeroCreate<byte> (max n 0)
                    System.Runtime.InteropServices.Marshal.Copy(buf, bytes, 0, bytes.Length)
                    Some(bytes, Libc.lseek (fd, 0L, 2))
                finally
                    System.Runtime.InteropServices.Marshal.FreeHGlobal buf
                    Libc.close fd |> ignore

        // ELF class and e_machine of this process's own images
        let native =
            match System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture with
            | System.Runtime.InteropServices.Architecture.X64 -> Some(2uy, 62)
            | System.Runtime.InteropServices.Architecture.Arm64 -> Some(2uy, 183)
            | System.Runtime.InteropServices.Architecture.X86 -> Some(1uy, 3)
            | System.Runtime.InteropServices.Architecture.Arm -> Some(1uy, 40)
            | System.Runtime.InteropServices.Architecture.RiscV64 -> Some(2uy, 243)
            | System.Runtime.InteropServices.Architecture.LoongArch64 -> Some(2uy, 258)
            | System.Runtime.InteropServices.Architecture.Ppc64le -> Some(2uy, 21)
            | System.Runtime.InteropServices.Architecture.S390x -> Some(2uy, 22)
            | _ -> None

        // the checks binfmt_elf makes before it maps anything — any that
        // fails is ENOEXEC
        let elfLoads (head: byte[]) (length: int64) =
            match native with
            | None -> true
            | Some(cls, machine) ->
                let wide = cls = 2uy
                let ehsize = if wide then 64 else 52

                head.Length >= ehsize
                && head[4] = cls
                && (head[5] = 1uy || head[5] = 2uy)
                && (let u16 (o: int) =
                        if head[5] = 1uy then
                            int head[o] ||| (int head[o + 1] <<< 8)
                        else
                            (int head[o] <<< 8) ||| int head[o + 1]

                    let u32 (o: int) =
                        if head[5] = 1uy then
                            int64 (u16 o) ||| (int64 (u16 (o + 2)) <<< 16)
                        else
                            (int64 (u16 o) <<< 16) ||| int64 (u16 (o + 2))

                    let phoff =
                        if wide then
                            if head[5] = 1uy then
                                u32 32 ||| (u32 36 <<< 32)
                            else
                                (u32 32 <<< 32) ||| u32 36
                        else
                            u32 28

                    let phentsize, phnum = if wide then u16 54, u16 56 else u16 42, u16 44
                    let size = int64 phnum * int64 phentsize

                    (u16 16 = 2 || u16 16 = 3)
                    && u16 18 = machine
                    && phentsize = (if wide then 56 else 32)
                    && size > 0L
                    && size <= 65536L
                    && phoff >= 0L
                    && phoff + size <= length)

        let rec accepts (path: string) (scripts: int) =
            match headOf path with
            | None -> false
            | Some(head, length) ->
                let starts (magic: byte list) =
                    head.Length >= magic.Length
                    && List.forall2 (fun i b -> head[i] = b) [ 0 .. magic.Length - 1 ] magic

                if starts [ 0x23uy; 0x21uy ] then
                    let line =
                        System.Text.Encoding.UTF8.GetString(head, 2, head.Length - 2).Split('\n').[0]

                    match line.Trim([| ' '; '\t' |]).Split([| ' '; '\t' |], 2).[0] with
                    | "" -> false
                    | interp ->
                        let interp = System.IO.Path.Combine(cwd, interp)
                        scripts < maxScripts && runnable interp && accepts interp (scripts + 1)
                elif starts [ 0x7Fuy; 0x45uy; 0x4Cuy; 0x46uy ] then
                    not (System.OperatingSystem.IsLinux()) || elfLoads head length
                else
                    [ [ 0xFEuy; 0xEDuy; 0xFAuy; 0xCEuy ] // Mach-O
                      [ 0xFEuy; 0xEDuy; 0xFAuy; 0xCFuy ]
                      [ 0xCEuy; 0xFAuy; 0xEDuy; 0xFEuy ]
                      [ 0xCFuy; 0xFAuy; 0xEDuy; 0xFEuy ]
                      [ 0xCAuy; 0xFEuy; 0xBAuy; 0xBEuy ] ]
                    |> List.exists starts

        accepts path 0

/// a spawn here goes through `CleanupShim`: it runs inside a cleanup and
/// weir has a controlling terminal (POSIX) — foreground or not, since a
/// background weir can be brought to the foreground while the command
/// runs [D:cleanup-sigign]
let cleanupIgnoresTerminal () : bool =
    not (System.OperatingSystem.IsWindows())
    && inCleanup ()
    && Libc.hasControllingTty ()

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
            | e -> failwith $"within lock: cannot lock {Weir.Types.quoteData fs.Name} (flock errno {e})"

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

// Linux: the runtime opens /dev/urandom before main and keeps reading it,
// so a standard fd closed at exec is where it landed — the one urandom fd
// open, sitting at 0, 1 or 2
let private runtimeTookStdio () : int option =
    let isUrandom (l: string) =
        try
            System.IO.File.ResolveLinkTarget(l, false).FullName = "/dev/urandom"
        with _ ->
            false

    if
        not (System.OperatingSystem.IsLinux())
        || not (List.exists (fun n -> isUrandom $"/proc/self/fd/{n}") [ 0; 1; 2 ])
    then
        None
    else
        try
            let urandom =
                System.IO.Directory.GetFileSystemEntries "/proc/self/fd" |> Array.filter isUrandom

            match urandom with
            | [| l |] ->
                match System.IO.Path.GetFileName l with
                | "0" -> Some 0
                | "1" -> Some 1
                | "2" -> Some 2
                | _ -> None
            | _ -> None
        with _ ->
            None

/// a closed fd 0, 1 or 2 is /dev/null before anything opens a file, so no
/// file weir (or its runtime) opens takes a standard fd: stdin reads
/// nothing, stdout and stderr write nowhere [D:console-write]. One the
/// runtime took before main is put on /dev/null and weir starts over in
/// place (the same pid and argv), where the runtime's fd lands elsewhere
let nullClosedStdio () : unit =
    let F_GETFD = 1

    if not (System.OperatingSystem.IsWindows()) then
        let taken = runtimeTookStdio ()

        // lowest first: an open takes the lowest free fd
        for target, flags in [ 0, 0; 1, 1; 2, 1 ] do
            if taken = Some target || Libc.fcntl (target, F_GETFD) = -1 then
                let fd = Libc.``open`` ("/dev/null", flags)

                if fd >= 0 && fd <> target then
                    Libc.dup2 (fd, target) |> ignore
                    Libc.close fd |> ignore

        match taken with
        | Some target ->
            // the argv bytes as exec'd, NUL-terminated each
            let cmdline =
                try
                    System.IO.File.ReadAllBytes "/proc/self/cmdline"
                with _ ->
                    [||]

            if cmdline.Length > 0 then
                let block = System.Runtime.InteropServices.Marshal.AllocHGlobal(cmdline.Length + 1)
                System.Runtime.InteropServices.Marshal.Copy(cmdline, 0, block, cmdline.Length)
                System.Runtime.InteropServices.Marshal.WriteByte(block, cmdline.Length, 0uy)

                let starts =
                    [ yield 0
                      for i in 0 .. cmdline.Length - 2 do
                          if cmdline[i] = 0uy then
                              yield i + 1 ]

                let argv = Array.append (starts |> List.map (fun o -> block + nativeint o) |> Array.ofList) [| 0n |]
                Libc.execv ("/proc/self/exe", argv) |> ignore

            // no start-over: the runtime reads its random bytes from that fd,
            // so it gets /dev/urandom back
            let fd = Libc.``open`` ("/dev/urandom", 0)

            if fd >= 0 && fd <> target then
                Libc.dup2 (fd, target) |> ignore
                Libc.close fd |> ignore
        | None -> ()

/// the terminal's mode when a script started at it as the foreground
/// group: a hard exit kills a cleanup's command that set it raw (a pager)
/// and puts it back [D:hard-exit]
let mutable private savedTerminal: byte[] = null

let saveTerminal () : unit =
    if not (System.OperatingSystem.IsWindows()) && Libc.terminalForeground () then
        let fd = Libc.``open`` ("/dev/tty", Libc.O_RDWR)

        if fd >= 0 then
            try
                let buf = Array.zeroCreate<byte> 128

                if Libc.tcgetattr (fd, buf) = 0 then
                    savedTerminal <- buf
            finally
                Libc.close fd |> ignore

/// put the saved mode back, only while weir is still the foreground group
/// (a background group's tcsetattr would stop it)
let private restoreTerminal () : unit =
    if not (isNull savedTerminal) && Libc.terminalForeground () then
        let fd = Libc.``open`` ("/dev/tty", Libc.O_RDWR)

        if fd >= 0 then
            try
                Libc.tcsetattr (fd, 0, savedTerminal) |> ignore
            finally
                Libc.close fd |> ignore

/// restores what weir changed about the terminal before it dies (the REPL's
/// raw mode) — set by the REPL
let mutable beforeDie: unit -> unit = ignore

// ends weir with a teardown's code [D:die-by-signal]: after the unwind
// (or the backstop's, or the hard exit), INT/TERM/HUP end it BY the signal,
// as an unscoped weir dies, so a calling shell sees the signal (bash's
// wait-and-cooperative-exit); the handler .NET installed is replaced by
// the default first. A closed stdout's 141 is an exit. Windows has no
// death by signal: a Ctrl+C exits as the console ends a process. Only the
// flow's own exit flushes: a backstop or a hard exit may be dying because
// a write is stuck, and a flush would wait on it
let private dieByFlushing (flush: bool) (code: int) : unit =
    if flush then
        try
            System.Console.Out.Flush()
            System.Console.Error.Flush()
        with _ ->
            ()

    settleKills ()

    (try
        beforeDie ()
     with _ ->
         ())

    if System.OperatingSystem.IsWindows() then
        System.Environment.Exit(if code = 130 then -1073741510 else code)
    else
        let signum =
            match code with
            | 130 -> 2
            | 143 -> 15
            | 129 -> 1
            | 131 -> 3
            | _ -> 0

        // a SIGQUIT's death dumps no core: the teardown ran, the dump
        // would hold nothing the user asked for [D:sigquit-teardown]
        if signum = 3 then
            if System.OperatingSystem.IsLinux() then
                Libc.prctl (4, 0un, 0un, 0un, 0un) |> ignore
            else
                Libc.setrlimit (4, [| 0UL; 0UL |]) |> ignore

        if signum <> 0 then
            Libc.signal (signum, Libc.SIG_DFL) |> ignore
            ProcTree.kill (System.Environment.ProcessId, signum) |> ignore
            System.Threading.Thread.Sleep 1000

        Libc.sysExit code

let private dieBy (code: int) : unit = dieByFlushing false code

// the thread pool has work queued or a worker taking an item (the calling
// thread aside). .NET's signal thread reads the signals in delivery order
// and queues SIGHUP's handler as a pool item before it reaps a child that
// died after the signal reached weir, so a HUP weir received and has not
// acted on is pool work when the death is seen [D:settle-pending]
let private poolBusy () =
    let mutable available = 0
    let mutable most = 0
    let mutable io = 0
    System.Threading.ThreadPool.GetAvailableThreads(&available, &io)
    System.Threading.ThreadPool.GetMaxThreads(&most, &io)

    let self =
        if System.Threading.Thread.CurrentThread.IsThreadPoolThread then
            1
        else
            0

    System.Threading.ThreadPool.PendingWorkItemCount > 0L || most - available > self

// a signal the handler is acting on: wait (bounded) until it is done
// [D:settle-pending]. A signal weir has received reaches its handler a
// fraction of a millisecond after the kernel delivered it (.NET hands it
// to a thread or a pool item first), sometimes after the dead child is
// reaped: with none in flight the wait is a 5ms grace for one to appear,
// never the 250ms. `pool`: the signal is SIGHUP, whose handler may still
// sit in the pool's queue — busy pool work counts as in flight
let private settleWhile (pool: bool) (waiting: unit -> bool) : unit =
    let clock = System.Diagnostics.Stopwatch.StartNew()

    let inFlight () =
        System.Threading.Volatile.Read &signalsInFlight > 0
        || (pool && poolBusy ())

    let mutable lastSeen = 0L
    let mutable go = true

    while go do
        let now = clock.ElapsedMilliseconds
        let pending = inFlight ()

        if pending then
            lastSeen <- now

        go <- waiting () && now < 250L && (pending || now - lastSeen < 5L)

        if go then
            System.Threading.Thread.Sleep(if pending then 1 else 0)

// a command died 129-143 since the start: the exit settles a signal still
// on its way to the handler first, so a received signal is never dropped
// [D:settle-pending]
let mutable private signalDeathSeen = 0

/// the requested exit code, or `code` when none was requested — the
/// runner's result, so a request racing a statement's normal end still
/// exits with the request's code; a signal's teardown dies by the signal
/// [D:die-by-signal]
let exitCodeOr (code: int) : int =
    if System.Threading.Volatile.Read &hardExiting <> 0 then
        park ()

    if System.Threading.Volatile.Read &signalDeathSeen <> 0 then
        settleWhile true (fun () -> System.Threading.Volatile.Read &teardownCode = 0)

    match System.Threading.Volatile.Read &teardownCode with
    | 0 -> code
    | c ->
        // a requested teardown's kill completes before the exit, every
        // other tree kill in flight with it [D:kill-settle]
        killLiveChildren false
        settleKills ()

        if c <> 141 then
            sweepDirs ()
            dieByFlushing true c

        c

/// a parallel worker stops taking arms once a teardown is requested
/// (a signal, a closed stdout) [D:teardown-stops-arms] — unless the
/// fan-out runs inside an `always` cleanup, whose node no teardown reaches
let stopTakingArms () : bool =
    System.Threading.Volatile.Read &teardownCode <> 0 && not (inCleanup ())

/// the requested teardown's exit code, or 0 when none is requested
let teardownRequested () : int = System.Threading.Volatile.Read &teardownCode

/// a cleanup's statement command that was running when the signal came
/// and died by the teardown's own signal (a group-directed TERM/HUP
/// reaches it with weir) is the signal's, not a failure: the cleanup runs
/// on whole [D:cleanup-group-signal], its fan-out arms too. A value
/// capture, a command started after the signal and a race's arm still
/// fail, so no truncated output is ever a value
let cleanupKilledByTeardown (statement: bool) (runningAtSignal: bool) (code: int) : bool =
    // up to the cleanup's shield: is this flow inside a race?
    let rec inRace (n: Node) =
        match n with
        | null -> false
        | n when n.Kind = NodeKind.Shield -> false
        | n when n.Kind = NodeKind.Race -> true
        | n -> inRace n.Parent

    statement
    && runningAtSignal
    && code > 128
    && inCleanup ()
    && code = System.Threading.Volatile.Read &teardownCode
    && not (inRace (currentNode ()))

/// a command that died of INT, HUP, QUIT or TERM while weir's handler is
/// acting on a signal most likely took it with weir (the terminal's ^C,
/// a hangup, ^\, `kill -- -pgid`) [D:sigint-terminal-group]
/// [D:teardown-kill-not-failure]: let the handler request the teardown
/// first, so the death is the signal, not a command failure
/// [D:settle-pending]
let settleInterrupt (code: int) : unit =
    if
        (code = 129 || code = 130 || code = 131 || code = 143)
        && not (System.OperatingSystem.IsWindows())
        && mainThreadId <> 0
        && not replFlow
    then
        System.Threading.Volatile.Write(&signalDeathSeen, 1)
        settleWhile (code = 129) (fun () -> System.Threading.Volatile.Read &teardownCode = 0)

/// a script about to die of a command's 129/130/131/143 failure: the
/// command may have taken a signal before weir did (a supervisor's kill
/// of each pid, a hangup reaching the child first) — the one wait on the
/// code alone, paid once, at the death: up to 250ms for weir's own signal
/// [D:settle-pending]. The teardown's code, or 0
let settleFatal (code: int) : int =
    if
        (code = 129 || code = 130 || code = 131 || code = 143)
        && not (System.OperatingSystem.IsWindows())
        && mainThreadId <> 0
        && not replFlow
    then
        let clock = System.Diagnostics.Stopwatch.StartNew()

        while System.Threading.Volatile.Read &teardownCode = 0
              && clock.ElapsedMilliseconds < 250L do
            System.Threading.Thread.Sleep 5

    System.Threading.Volatile.Read &teardownCode

/// a REPL child whose code is data (`| exitCode`, `| complete`) died of
/// SIGINT while the ^C's handler is still cutting the entry: let it, so
/// the entry ends after this statement [D:repl-entry-check]
/// [D:settle-pending]. Returns the code
let settleReplInterrupt (code: int) : int =
    if code = 130 && replFlow && not (System.OperatingSystem.IsWindows()) then
        settleWhile false (fun () -> not (interruptRequested ()))

    code

/// raise the requested teardown on the main thread, once: the unwind
/// then runs the cleanups as ordinary code (which must not be cut short
/// by the same request again). A cleanup already running is never
/// preempted [D:teardown-never-preempts]: its node is no teardown's, so
/// the request raises at the first boundary after it
let checkTeardown () : unit =
    let c = System.Threading.Volatile.Read &teardownCode

    if
        c <> 0
        && onMainThread ()
        && not (inCleanup ())
        && System.Threading.Interlocked.Exchange(&teardownRaised, 1) = 0
    then
        raise (ExitRequest c)

/// every evaluated node is a boundary, on every flow [D:scope-tree]: the
/// main thread raises a teardown's ExitRequest (once), any flow its cut's
/// Cancelled. One read while nothing is cut
let evalBoundary () : unit =
    if System.Threading.Volatile.Read &cutsPending <> 0 then
        if System.Threading.Volatile.Read &hardExiting <> 0 then
            park ()

        let n = currentNode ()

        if n.Cut > 0 then
            checkTeardown ()

            if cancelledBy n then
                raise (Cancelled())

// a main thread not yet unwinding waits interruptibly; elsewhere (a
// worker, or a cleanup running) waits run as written
let private interruptible () =
    onMainThread ()
    && System.Threading.Volatile.Read &teardownRaised = 0
    && not (inCleanup ())

// what a runtime-owned wait wakes on: its node's cut [D:scope-tree]
let private wakeHandles () : System.Threading.WaitHandle list =
    match waitToken false with
    | t when t.CanBeCanceled -> [ t.WaitHandle ]
    | _ -> []

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

/// wait for a process to exit; the flow's cut raises — the process may be
/// one no kill of the cut reaches, a `within proc` opened outside a fan-out
/// [D:scope-tree]; a soft teardown (the terminal's ^C) waits for it
/// [D:sigint-terminal-group]
let waitProcess (p: System.Diagnostics.Process) : unit =
    checkCancels ()

    match waitToken true with
    | t when not t.CanBeCanceled -> p.WaitForExit()
    | t ->
        // the exit and the cut, waited on together — no poll floor
        let exited = p.WaitForExitAsync()
        let handles = [| (exited :> System.IAsyncResult).AsyncWaitHandle; t.WaitHandle |]

        while not exited.IsCompleted do
            if t.IsCancellationRequested then
                // a cut this flow does not raise for waits on the exit alone
                raiseWoken ()
                exited.Wait()
            else
                System.Threading.WaitHandle.WaitAny handles |> ignore

        // the no-argument form also waits for redirected streams' EOF —
        // never after a cut: a descendant the kill missed may hold a pipe
        // past the exit [D:wait-after-cut]
        if not t.IsCancellationRequested then
            p.WaitForExit()

// the main thread is joining arms for a teardown: it unwinds next, so the
// signal watchdog stands down [D:teardown-joins-arms]
let mutable private mainJoining = 0

/// a pipe read a lost race, a fan-out's exit, the serve cut, a REPL ^C or a
/// teardown cuts: the kill closes a pipe only when it reaches every holder
/// — an orphaned grandchild keeps one open — so the read waits beside the
/// flow's token and a cut raises instead of waiting for EOF; the abandoned
/// read's bytes are never used. A soft teardown (the terminal's ^C) leaves
/// the read be: the child took the ^C too [D:sigint-terminal-group]
let cutRead
    (read: unit -> 'a)
    (readAsync: System.Threading.CancellationToken -> System.Threading.Tasks.ValueTask<'a>)
    : 'a =
    match waitToken true with
    | t when not t.CanBeCanceled -> read ()
    | t ->
        checkCancels ()
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
                raiseWoken ()
                raise (System.OperationCanceledException t)

/// join a reader task under the same cut as `cutRead`; a reader the
/// teardown cut (it runs in the flow's node, so the teardown cuts it) raises
/// as the joining flow's own stop — the main thread's ExitRequest
/// [D:epipe-kills-at-teardown]
let cutJoin (task: System.Threading.Tasks.Task<'a>) : 'a =
    try
        cutRead (fun () -> task.Result) (fun _ -> System.Threading.Tasks.ValueTask<'a>(task))
    with :? System.AggregateException as ae when (ae.InnerException :? System.OperationCanceledException) ->
        raiseWoken ()
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException).Throw()
        Unchecked.defaultof<'a>

/// a blocking call a cut abandons — a FIFO's open or read, which no kill
/// reaches [D:stdin-reader]: it runs off the flow and the wait raises; the
/// abandoned call's result is never used
let cutBlocking (f: unit -> 'a) : 'a =
    match waitToken false with
    | t when not t.CanBeCanceled -> f ()
    | _ ->
        let task = System.Threading.Tasks.Task.Run<'a>(System.Func<'a> f)

        try
            cutJoin task
        with :? System.AggregateException as ae when
            ae.InnerExceptions.Count = 1
            && not (ae.InnerException :? System.OperationCanceledException) ->
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException).Throw()
            Unchecked.defaultof<'a>

// a child's pipe, read so that a cut stops it [D:scope-tree]: a fill first
// asks poll(2) whether the pipe is readable — read(2) is called only then,
// so a cut never leaves a thread inside it and never consumes the bytes it
// abandons — and while the pipe is empty it polls beside the flow's node.
// The cut is checked once per fill, never per line. A soft teardown (the
// terminal's ^C, which reached the child too) leaves the read be while the
// child lives; once the child is gone (an orphan holds the pipe) it cuts
// too [D:sigint-terminal-group]. Windows waits beside the token per fill
type private CutStream(inner: System.IO.Stream, child: System.Diagnostics.Process) =
    inherit System.IO.Stream()

    let fd =
        match inner with
        | :? System.IO.Pipes.PipeStream as ps when not (System.OperatingSystem.IsWindows()) ->
            int (ps.SafePipeHandle.DangerousGetHandle())
        | _ -> -1

    // a cut this read honours: any but the soft teardown's, and that one
    // too once the child has exited
    let cutNow () =
        if System.Threading.Volatile.Read &cutsPending <> 0 then
            let n = currentNode ()

            if n.Cut > 0 && not (unwinding n) then
                let soft =
                    (n.Reasons &&& CutReason.Soft) <> 0
                    && not n.Killing
                    && (n.Reasons &&& ~~~(CutReason.Teardown ||| CutReason.Soft)) = 0

                if
                    not soft
                    || (isNull child
                        || (try
                                child.HasExited
                            with _ ->
                                true))
                then
                    raiseWoken ()

    /// the pipe's fd (POSIX), or -1: the fill waits beside the token
    member _.Fd = fd

    /// raise when a cut this read honours is made
    member _.CutCheck() = cutNow ()

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
        if count = 0 then
            0
        elif fd >= 0 then
            cutNow ()

            if not (Libc.readable fd 0) then
                while not (Libc.readable fd 50) do
                    cutNow ()

            inner.Read(buffer, offset, count)
        else
            // Windows: a pipe read is synchronous underneath and ignores
            // its token, so the fill waits beside the token instead
            cutRead
                (fun () -> inner.Read(buffer, offset, count))
                (fun t -> inner.ReadAsync(System.Memory<byte>(buffer, offset, count), t))

    // the child's pipe is the Process's to close
    override _.Dispose(_: bool) = ()

/// a thread the OS refused to start (EAGAIN: the pids cgroup's limit or
/// `ulimit -u` reached; or no memory for its stack) as a weir error that
/// names the limit, not .NET's text [D:pipe-threads]
let threadLimitError (what: string) : exn =
    let readTrim (path: string) =
        try
            System.IO.File.ReadAllText(path).Trim()
        with _ ->
            ""

    let threads =
        readTrim "/proc/self/status"
        |> fun st ->
            st.Split('\n')
            |> Array.tryFind (fun l -> l.StartsWith "Threads:")
            |> Option.map (fun l -> l.Substring(8).Trim())

    let pidsMax =
        // cgroup v2: "0::/path"
        readTrim "/proc/self/cgroup"
        |> fun cg ->
            cg.Split('\n')
            |> Array.tryFind (fun l -> l.StartsWith "0::")
            |> Option.map (fun l -> readTrim ("/sys/fs/cgroup" + l.Substring(3).TrimEnd('/') + "/pids.max"))
            |> Option.filter (fun v -> v <> "" && v <> "max")

    let limit =
        match pidsMax with
        | Some m -> $"cgroup pids.max {m}, or ulimit -u"
        | None -> "ulimit -u, or the cgroup's pids.max"

    let running =
        match threads with
        | Some n -> $"; weir runs {n} threads"
        | None -> ""

    exn
        $"cannot start a thread for {what}: the process/thread limit is reached ({limit}){running} — lower the parallel width (pmapWith/pfirstWith n) or raise the limit"

/// start a thread weir needs; a refusal is `threadLimitError`
let startThread (what: string) (t: System.Threading.Thread) : unit =
    try
        t.Start()
    with
    | :? System.OutOfMemoryException
    | :? System.Threading.ThreadStartException -> raise (threadLimitError what)

/// a child's output stream, cut as the flow is [D:scope-tree]
let cutStream (child: System.Diagnostics.Process) (s: System.IO.Stream) : System.IO.Stream =
    new CutStream(s, child) :> System.IO.Stream

/// several child output streams (each from `cutStream`) that one thread
/// can drain by poll(2) — POSIX pipes [D:pipe-threads]
let pollablePipes (streams: System.IO.Stream[]) : bool =
    streams
    |> Array.forall (fun s ->
        match s with
        | :? CutStream as c -> c.Fd >= 0
        | _ -> false)

/// wait until one of the live streams (`pollablePipes`; null = done) is
/// ready to fill — data, EOF or an error, whatever its next read reports —
/// and mark each ready one in `ready`. Cut as a CutStream fill is: checked
/// first and every 50ms while every pipe is empty [D:pipe-threads]
let waitPipes (streams: System.IO.Stream[]) (ready: bool[]) : unit =
    let live =
        streams
        |> Array.mapi (fun i s -> i, s)
        |> Array.choose (fun (i, s) ->
            match s with
            | :? CutStream as c -> Some(i, c)
            | _ -> None)

    let fds = Array.zeroCreate<Libc.PollFd> live.Length

    for k in 0 .. live.Length - 1 do
        fds[k].fd <- (snd live[k]).Fd
        fds[k].events <- 1s // POLLIN

    let poll ms =
        for k in 0 .. fds.Length - 1 do
            fds[k].revents <- 0s

        fds.Length > 0 && Libc.poll (&fds[0], unativeint fds.Length, ms) > 0

    let cutAll () =
        for _, c in live do
            c.CutCheck()

    cutAll ()

    if not (poll 0) then
        while not (poll 50) do
            cutAll ()

    System.Array.Clear ready

    for k in 0 .. live.Length - 1 do
        if fds[k].revents <> 0s then
            ready[fst live[k]] <- true

/// a reader over a child's output, cut as the flow is: the encoding the
/// Process gave it, a larger buffer
let cutReader (child: System.Diagnostics.Process) (r: System.IO.StreamReader) : System.IO.StreamReader =
    new System.IO.StreamReader(cutStream child r.BaseStream, r.CurrentEncoding, true, 65536)

/// join a task; a teardown request raises on the main thread once the
/// task's arms, cut at their next boundary, have joined
/// [D:teardown-joins-arms] — before any enclosing scope closes (pure
/// compute holds the join; a second signal hard-exits). Every wait is cut,
/// so no arm is left behind [D:scope-tree]
let waitTask (t: System.Threading.Tasks.Task) : unit =
    if interruptible () then
        System.Threading.WaitHandle.WaitAny(
            [| (t :> System.IAsyncResult).AsyncWaitHandle; (tokenOf rootNode false).WaitHandle |]
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

    // Windows: a read of fd 0 blocks and ignores cancellation, so one read
    // runs off the flow and every reader waits for it beside its node's
    // token [D:scope-tree]; a cut leaves it pending for the next reader,
    // so no byte is lost and no flow is left inside it
    let mutable private winPending: System.Threading.Tasks.Task<byte[]> = null
    let mutable private winPendingLine: System.Threading.Tasks.Task<string> = null

    let private awaitPending (t: System.Threading.Tasks.Task<'a>) : 'a =
        let tok = waitToken false

        if tok.CanBeCanceled then
            try
                t.Wait tok
            with :? System.OperationCanceledException ->
                raiseWoken ()
                t.Wait()
        else
            t.Wait()

        t.Result

    // wait until fd 0 is readable, raising when the wait is cut
    let private awaitReadable () =
        if not (System.OperatingSystem.IsWindows()) then
            if System.Threading.Volatile.Read &cutsPending <> 0 then
                raiseWoken ()

            if not (Libc.readable 0 0) then
                while not (Libc.readable 0 50) do
                    if System.Threading.Volatile.Read &cutsPending <> 0 then
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
            let t =
                match winPending with
                | null ->
                    let t =
                        System.Threading.Tasks.Task.Run<byte[]>(fun () ->
                            let b = Array.zeroCreate<byte> n
                            let got = winStdin.Force().Read(b, 0, n)
                            Array.sub b 0 (max 0 got))

                    winPending <- t
                    t
                | t -> t

            let bytes = awaitPending t
            winPending <- null

            if hi + bytes.Length > buf.Length then
                let bigger = Array.zeroCreate (max (buf.Length * 2) (hi + bytes.Length))
                System.Buffer.BlockCopy(buf, 0, bigger, 0, hi)
                buf <- bigger

            System.Buffer.BlockCopy(bytes, 0, buf, hi, bytes.Length)
            hi <- hi + bytes.Length
            bytes.Length
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
        while not (System.Threading.Monitor.TryEnter(gate, 50)) do
            raiseWoken ()

        if winTty () then
            try
                let t =
                    match winPendingLine with
                    | null ->
                        let t = System.Threading.Tasks.Task.Run<string>(fun () -> System.Console.In.ReadLine())
                        winPendingLine <- t
                        t
                    | t -> t

                let line = awaitPending t
                winPendingLine <- null
                line
            finally
                System.Threading.Monitor.Exit gate
        else
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

    /// a child about to read fd 0 while weir holds a begun line — a cut
    /// prompt's partial one [D:prompt-cut-whole-line]: the rest of that line
    /// is read first (waiting for it, cut as the flow is), so the line stays
    /// whole in weir's buffer for its next reader and the child starts at
    /// the line after — no reader sees a line that was not written
    let finishForChild () =
        if not (System.OperatingSystem.IsWindows()) && hi > lo && buf[hi - 1] <> 10uy then
            while not (System.Threading.Monitor.TryEnter(gate, 50)) do
                raiseWoken ()

            try
                let mutable go = hi > lo && buf[hi - 1] <> 10uy

                while go do
                    awaitReadable ()

                    if hi + 1 > buf.Length then
                        let bigger = Array.zeroCreate (buf.Length * 2)
                        System.Buffer.BlockCopy(buf, 0, bigger, 0, hi)
                        buf <- bigger

                    match readRaw 1 with
                    | 1 -> go <- buf[hi - 1] <> 10uy
                    | e when -e = Libc.EINTR || -e = Libc.EAGAIN -> ()
                    | _ -> go <- false
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

/// a FIFO, after symlinks (Linux; false elsewhere or when absent)
let isFifo (path: string) : bool =
    System.OperatingSystem.IsLinux()
    && (let buf = Array.zeroCreate<byte> 256
        let AT_FDCWD, STATX_TYPE = -100, 1u

        Libc.statx (AT_FDCWD, path, 0, STATX_TYPE, buf) = 0
        && (int (System.BitConverter.ToUInt16(buf, 28)) &&& 0o170000) = 0o010000)

/// a FIFO, socket or device, not followed through a link: its mode (type
/// and permission bits) and its device number; None for a regular file,
/// a directory, a link, or off POSIX [D:dircopy-special]
let specialFile (path: string) : (int * uint64) option =
    let special (mode: int) =
        match mode &&& 0o170000 with
        | 0o010000
        | 0o020000
        | 0o060000
        | 0o140000 -> true
        | _ -> false

    if System.OperatingSystem.IsLinux() then
        let buf = Array.zeroCreate<byte> 256
        let AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE_MODE = -100, 0x100, 3u

        if Libc.statx (AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_TYPE_MODE, buf) = 0 then
            let mode = int (System.BitConverter.ToUInt16(buf, 28))
            let major = uint64 (System.BitConverter.ToUInt32(buf, 128))
            let minor = uint64 (System.BitConverter.ToUInt32(buf, 132))
            // glibc's makedev
            let dev =
                ((major &&& 0xfffff000UL) <<< 32)
                ||| ((major &&& 0xfffUL) <<< 8)
                ||| ((minor &&& 0xffffff00UL) <<< 12)
                ||| (minor &&& 0xffUL)

            if special mode then Some(mode, dev) else None
        else
            None
    elif System.OperatingSystem.IsMacOS() then
        let buf = Array.zeroCreate<byte> 256

        let rc =
            if System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture = System.Runtime.InteropServices.Architecture.X64 then
                Libc.lstatMacX64 (path, buf)
            else
                Libc.lstatMacArm (path, buf)

        let mode = int (System.BitConverter.ToUInt16(buf, 4))

        if rc = 0 && special mode then
            Some(mode, uint64 (uint32 (System.BitConverter.ToInt32(buf, 24))))
        else
            None
    else
        None

/// create the special file `specialFile` described, as `cp -R` does —
/// never opened; None when made, else why not [D:dircopy-special]
let makeSpecialFile (path: string) (mode: int, dev: uint64) : string option =
    let rc =
        try
            if mode &&& 0o170000 = 0o010000 then
                Libc.mkfifo (path, uint32 (mode &&& 0o7777))
            elif System.OperatingSystem.IsMacOS() then
                Libc.mknodMac (path, uint32 mode, int (uint32 dev))
            else
                Libc.mknodLinux (path, uint32 mode, dev)
        with :? System.EntryPointNotFoundException ->
            -2

    if rc = 0 then
        None
    elif rc = -2 then
        Some "this platform's libc has no mknod"
    else
        let errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError()

        Some(
            match errno with
            | 1 -> "operation not permitted (a device needs privilege)"
            | 13 -> "permission denied"
            | 17 -> "it exists"
            | e -> $"errno {e}"
        )

// a FIFO a File op writes [D:fifo-ops-polled]: opened non-blocking —
// the open waits for a reader in short cut-checked steps (ENXIO until one
// comes) and each write waits for room beside the cut, so a lost race, a
// REPL ^C or a teardown raises at the wait and the dispose closes the fd;
// no thread is left in open(2) or write(2) to deliver after the cut
type private PolledWriteFd(fd: int) =
    inherit System.IO.Stream()
    let mutable closed = false

    let await () =
        match wakeHandles () with
        | [] ->
            while not (Libc.writable fd 1000) do
                ()
        | _ ->
            raiseWoken ()

            while not (Libc.writable fd 50) do
                raiseWoken ()

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (System.NotSupportedException())

    override _.Position
        with get () = raise (System.NotSupportedException())
        and set (_: int64) = raise (System.NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_: int64, _: System.IO.SeekOrigin) : int64 = raise (System.NotSupportedException())
    override _.SetLength(_: int64) = raise (System.NotSupportedException())
    override _.Read(_: byte[], _: int, _: int) : int = raise (System.NotSupportedException())

    override _.Write(buffer: byte[], offset: int, count: int) =
        let h =
            System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned)

        try
            let mutable off = offset
            let mutable left = count

            while left > 0 do
                await ()

                let r =
                    int (Libc.write (fd, h.AddrOfPinnedObject() + nativeint off, unativeint left))

                if r >= 0 then
                    off <- off + r
                    left <- left - r
                else
                    match System.Runtime.InteropServices.Marshal.GetLastPInvokeError() with
                    | e when e = Libc.EINTR || e = Libc.EAGAIN -> ()
                    | 32 -> raise (System.IO.IOException "the FIFO's reader closed it")
                    | e -> raise (System.IO.IOException $"write failed (errno {e})")
        finally
            h.Free()

    override _.Dispose(disposing: bool) =
        if not closed then
            closed <- true
            Libc.close fd |> ignore

        base.Dispose disposing

/// a polled write stream for a FIFO [D:fifo-ops-polled]; None for any
/// other path (or off Linux) — the caller writes it as a file
let openPolledWrite (path: string) : System.IO.Stream option =
    if not (isFifo path) then
        None
    else
        let O_WRONLY, O_NONBLOCK, O_CLOEXEC = 1, 0x800, 0x80000
        let ENXIO = 6

        let rec go () =
            let fd = Libc.``open`` (path, O_WRONLY ||| O_NONBLOCK ||| O_CLOEXEC)

            if fd >= 0 then
                fd
            else
                match System.Runtime.InteropServices.Marshal.GetLastPInvokeError() with
                | e when e = ENXIO ->
                    // no reader yet
                    sleepInterruptible (System.TimeSpan.FromMilliseconds 50.0)
                    go ()
                | e when e = Libc.EINTR -> go ()
                | 13 -> raise (System.UnauthorizedAccessException())
                | e -> raise (System.IO.IOException $"open failed (errno {e})")

        Some(new PolledWriteFd(go ()) :> System.IO.Stream)

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
                // a signal from here on is the second [D:hard-exit]
                System.Threading.Interlocked.Exchange(tearingDown, 1) |> ignore
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

// 128 + the signal number, the shell's convention
let private signalExitCode (s: System.Runtime.InteropServices.PosixSignal) =
    match s with
    | System.Runtime.InteropServices.PosixSignal.SIGTERM -> 143
    | System.Runtime.InteropServices.PosixSignal.SIGHUP -> 129
    | System.Runtime.InteropServices.PosixSignal.SIGQUIT -> 131
    | _ -> 130

// a live tmp/proc/serve/always scope: SIGINT is a tree kill only while one
// is open — a terminal ^C after every scope has closed is the unscoped one
// [D:sigint-live-scopes] (a `within proc` holds its spill dir for its
// lifetime)
let private scoped () =
    not liveTmpDirs.IsEmpty || not liveAlways.IsEmpty

// the second signal's hard exit [D:hard-exit]: no cleanup runs and none
// keeps running — every thread parks at its next runtime point, every
// child dies (a cleanup's too), the kills settle, the registered dirs go,
// and weir dies by the teardown's FIRST signal; one code, whatever thread
// wins
let private hardExit (code: int) =
    System.Threading.Volatile.Write(&hardExiting, 1)
    System.Threading.Interlocked.Add(&cutsPending, 1 <<< 20) |> ignore
    spawnGate.TryEnterWriteLock 1000 |> ignore

    for kv in liveProcs do
        killEntry kv.Value

    settleKills ()
    restoreTerminal ()

    for kv in liveTmpDirs do
        try
            System.IO.Directory.Delete(kv.Key, true)
        with _ ->
            ()

    System.Threading.Interlocked.CompareExchange(&teardownCode, code, 0) |> ignore
    dieBy (System.Threading.Volatile.Read &teardownCode)

// the signal backstop [D:teardown-never-preempts]: a main flow blocked
// where no boundary comes (a builtin's own loop, a stalled write) cannot
// unwind — after `graceMs` (and each `pollMs` after) the backstop's verdict
// holds while it runs a cleanup, closes a scope or joins; otherwise the
// backstop adopts the main flow, runs its pending frames and dies
let private startBackstop (graceMs: int) (pollMs: int) (ready: unit -> bool) (code: int) =
    let watch =
        System.Threading.Thread(fun () ->
            System.Threading.Thread.Sleep graceMs

            let rec standBy () =
                match backstopVerdict (ready ()) with
                | None ->
                    System.Threading.Thread.Sleep pollMs
                    standBy ()
                | Some f -> f

            if standBy () then
                adoptMain ()
                sweepDirs ()
                dieBy code)

    watch.IsBackground <- true
    watch.Start()

// the terminal's ^C reached the children with weir [D:sigint-terminal-group]:
// no kill — the script waits for its child (its own ^C handling runs) and
// then unwinds; without a live child to wait for, it exits after a grace
// period, held as the watchdog is (a cleanup, a scope close, a join)
// [D:terminal-sigint-teardown]
let private terminalInterrupt () =
    requestTeardownWith true 130

    let idle = ref 0

    let ready () =
        idle.Value <- (if liveProcs.IsEmpty then idle.Value + 1 else 0)
        idle.Value >= 10

    startBackstop 100 100 ready 130

// one signal dispatch [D:scope-tree]: the REPL's own ^C, the hard exit, the
// terminal's unscoped ^C, the teardown — or, with no flow to unwind (the
// REPL between entries, a subcommand), the exit's sweep and the signal's
// death
let private dispatchSignal (ctx: System.Runtime.InteropServices.PosixSignalContext) =
    let sigint = ctx.Signal = System.Runtime.InteropServices.PosixSignal.SIGINT
    let code = signalExitCode ctx.Signal

    // a SIGINT the REPL survives is not a death [D:repl-isig]: it cuts the
    // evaluation's waits [D:repl-interrupt]
    if replSurvivesSigint.Value && sigint then
        ctx.Cancel <- true
        interruptRepl ()
    elif System.Threading.Interlocked.Exchange(tearingDown, 1) = 1 then
        ctx.Cancel <- true
        hardExit code
    elif System.Threading.Volatile.Read &mainThreadId = 0 then
        ctx.Cancel <- true
        System.Threading.Interlocked.CompareExchange(&teardownCode, code, 0) |> ignore
        killLiveChildren false
        sweepDirs ()
        dieBy code
    elif sigint && not (scoped ()) && Libc.terminalForeground () then
        ctx.Cancel <- true
        terminalInterrupt ()
    else
        // first signal, a flow running: it tears down [D:signal-main-unwind]
        // — what it waits on is cut and killed, its cleanups' spared
        ctx.Cancel <- true
        requestTeardown code
        killLiveChildren true
        startBackstop 1000 1000 (fun () -> true) code

let private onSignal (ctx: System.Runtime.InteropServices.PosixSignalContext) =
    // first: a settle wait sees the signal until it is acted on
    // [D:settle-pending]
    System.Threading.Interlocked.Increment &signalsInFlight |> ignore

    try
        dispatchSignal ctx
    finally
        System.Threading.Interlocked.Decrement &signalsInFlight |> ignore

let private installExitHook () =
    // a normal process exit — the pfirst exit-race customer: a background
    // loser killed mid-finally no longer leaks its dir
    System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> sweepDirs ())

    // SIGINT/SIGTERM/SIGHUP. POSIX arm only: Create throws on Windows (the
    // SIGWINCH guard's lesson [D:windows-v1]); Windows takes
    // Console.CancelKeyPress, through the same teardown.
    if not (System.OperatingSystem.IsWindows()) then
        // the detached-SIGINT reset [D:signal-teardown], done at start too
        resetDetachedSigint.Force()

        // SIGHUP joins them [D:sighup-teardown]: a closed terminal or a
        // `kill -HUP` unwinds like INT/TERM, exit 129; an inherited
        // SIG_IGN (nohup) stays — .NET does not install over it. SIGQUIT
        // (^\) is TERM's teardown and dies by SIGQUIT [D:sigquit-teardown]
        for posixSig in
            [ System.Runtime.InteropServices.PosixSignal.SIGINT
              System.Runtime.InteropServices.PosixSignal.SIGTERM
              System.Runtime.InteropServices.PosixSignal.SIGHUP
              System.Runtime.InteropServices.PosixSignal.SIGQUIT ] do
            let reg =
                System.Runtime.InteropServices.PosixSignalRegistration.Create(posixSig, onSignal)

            hookRoots <- (reg :> obj) :: hookRoots
    else
        System.Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true

            if System.Threading.Interlocked.Exchange(tearingDown, 1) = 1 then
                hardExit 130
            elif System.Threading.Volatile.Read &mainThreadId = 0 then
                System.Threading.Interlocked.CompareExchange(&teardownCode, 130, 0) |> ignore
                killLiveChildren false
                sweepDirs ()
                dieBy 130
            else
                requestTeardown 130
                killLiveChildren true
                startBackstop 1000 1000 (fun () -> true) 130)

let private ensureHook () =
    if System.Threading.Interlocked.Exchange(hookInstalled, 1) = 0 then
        installExitHook ()

/// the signal hook now, not at the first spawn: the tty REPL's prompt
/// restores the terminal on a TERM/HUP/QUIT death [D:die-by-signal]
let installSignalHook () : unit = ensureHook ()

let registerTmpDir (dir: string) : unit =
    ensureHook ()
    liveTmpDirs[dir] <- ()

let deregisterTmpDir (dir: string) : unit = liveTmpDirs.TryRemove dir |> ignore

/// create a private dir and register it, so no signal sees the dir
/// unregistered [D:exit-hook]: the hook first (until it is installed a
/// signal kills weir outright), then both under the spawn gate, which the
/// hard exit's sweep takes first
let createRegisteredDir (dir: string) : unit =
    ensureHook ()
    spawnGate.EnterReadLock()

    try
        createPrivateDir dir
        registerTmpDir dir
    finally
        spawnGate.ExitReadLock()

let registerProc (p: System.Diagnostics.Process) : unit =
    ensureHook ()
    liveProcs.TryAdd(p, ProcEntry(p, currentNode ())) |> ignore

/// a spawned child: the signal hook is installed (POSIX), so a signal
/// tears down — a pid-targeted TERM, HUP or INT tree-kills the live
/// children [D:signal-main-unwind] [D:sigint-reaps]; the teardown is
/// requested before the kill, so a child or arm the kill ends is the
/// teardown, never a command failure [D:teardown-kill-not-failure]
let registerForeground (_: System.Diagnostics.Process) : unit =
    if not (System.OperatingSystem.IsWindows()) then
        ensureHook ()

/// remove `p` — only that very process, so a late exit event never drops
/// a newer child that reused the pid
let deregisterProcAt (_: int) (p: System.Diagnostics.Process) : unit = forgetChild p

let deregisterProc (p: System.Diagnostics.Process) : unit = deregisterProcAt p.Id p

/// an `always` scope is pending (the live-scope rule's count)
let registerAlways () : int =
    ensureHook ()
    let id = System.Threading.Interlocked.Increment alwaysCounter
    liveAlways[id] <- ()
    id

// claim-by-removal: true for the one caller that took the cleanup
// [D:within-always-any]
let deregisterAlways (id: int) : bool = fst (liveAlways.TryRemove id)
