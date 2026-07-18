module Weir.Extern

open System
open System.IO

let mutable private cache: Set<string> option = None

let refresh () = cache <- None

let names () : Set<string> =
    match cache with
    | Some s -> s
    | None ->
        let s =
            (Environment.GetEnvironmentVariable "PATH"
             |> Option.ofObj
             |> Option.defaultValue "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
            |> Seq.collect (fun dir ->
                try
                    Directory.EnumerateFiles dir |> Seq.map Path.GetFileName
                with _ ->
                    Seq.empty)
            |> Set.ofSeq

        cache <- Some s
        s

let private pathDirs () =
    (Environment.GetEnvironmentVariable "PATH"
     |> Option.ofObj
     |> Option.defaultValue "")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)

let exists (prog: string) : bool =
    if prog.Contains '/' then
        File.Exists(Session.resolve prog)
    else
        match cache with
        | Some s -> s.Contains prog
        | None -> pathDirs () |> Array.exists (fun dir -> File.Exists(Path.Combine(dir, prog)))
