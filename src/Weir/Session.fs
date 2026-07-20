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

// worker lifecycle (Seq.pmap / Seq.piter)
let enterWorker (parentCwd: string) : unit = localCwd.Value <- Some parentCwd
let exitWorker () : unit = localCwd.Value <- None

let resolve (path: string) : string =
    System.IO.Path.GetFullPath(System.IO.Path.Combine(Cwd(), path))

// script argv, for the Args module scanners (script-only semantics:
// the REPL leaves this empty)
let mutable ScriptArgs: string list = []
