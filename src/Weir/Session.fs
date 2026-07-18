module Weir.Session

let mutable Cwd: string = System.IO.Directory.GetCurrentDirectory()

let resolve (path: string) : string =
    System.IO.Path.GetFullPath(System.IO.Path.Combine(Cwd, path))
