module Weir.Session

// Session-as-value, arriving incrementally (the shape recorded when the
// thread-safety question first came up): the root session is process-global
// and single-threaded as ever; parallel workers FORK it — cd inside a
// worker is worker-local and dies at the join. Spawns and File ops read
// the ambient session at force time, unchanged.

let mutable private rootCwd: string = System.IO.Directory.GetCurrentDirectory()

let private localCwd =
    new System.Threading.ThreadLocal<string option>(fun () -> None)

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
    new System.Threading.ThreadLocal<(string * string) list list option>(fun () -> None)

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
let enterWorker (parentCwd: string) : unit = localCwd.Value <- Some parentCwd

let exitWorker () : unit =
    localCwd.Value <- None
    localEnvOverlay.Value <- None

let resolve (path: string) : string =
    System.IO.Path.GetFullPath(System.IO.Path.Combine(Cwd(), path))

// script argv, for the Args module scanners (script-only semantics:
// the REPL leaves this empty)
let mutable ScriptArgs: string list = []
