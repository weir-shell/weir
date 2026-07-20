module Weir.Session

let mutable Cwd: string = System.IO.Directory.GetCurrentDirectory()

// true inside Seq.pmap/piter workers: cd must fail loudly there rather
// than race the shared session cwd (single-threaded-session invariant)
let inParallel = new System.Threading.ThreadLocal<bool>(fun () -> false)

let resolve (path: string) : string =
    System.IO.Path.GetFullPath(System.IO.Path.Combine(Cwd, path))
