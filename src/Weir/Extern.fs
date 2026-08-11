module Weir.Extern

open System
open System.IO

let mutable private cache: Set<string> option = None

let refresh () = cache <- None

// Windows resolves a bare `git` to `git.exe` via PATHEXT; the name
// as-given always wins first. POSIX: the empty list — names resolve
// as themselves only. [D:windows-v1]
let private pathExts () : string list =
    if OperatingSystem.IsWindows() then
        (Environment.GetEnvironmentVariable "PATHEXT"
         |> Option.ofObj
         |> Option.defaultValue ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
    else
        []

let private pathDirs () =
    (Environment.GetEnvironmentVariable "PATH"
     |> Option.ofObj
     |> Option.defaultValue "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

let names () : Set<string> =
    match cache with
    | Some s -> s
    | None ->
        let exts = pathExts ()

        let s =
            pathDirs ()
            |> Seq.collect (fun dir ->
                try
                    Directory.EnumerateFiles dir |> Seq.map Path.GetFileName
                with _ ->
                    Seq.empty)
            |> Seq.collect (fun name ->
                // a PATHEXT file answers to its bare name too
                match
                    exts
                    |> List.tryFind (fun e -> name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                with
                | Some e -> [ name; name.Substring(0, name.Length - e.Length) ]
                | None -> [ name ])
            |> Set.ofSeq

        cache <- Some s
        s

let private isPathy (prog: string) =
    prog.Contains '/'
    || (OperatingSystem.IsWindows() && (prog.Contains '\\' || prog.Contains ':'))

/// the SPAWN-side resolution [D:windows-s2]: CreateProcess appends only
/// .exe to a bare name — a .bat/.cmd (any PATHEXT) implementation needs
/// its REAL file name handed over. Walks PATH per-dir: the name
/// as-given first (the stated rule), then each PATHEXT in order.
/// POSIX callers never need it (resolveProg passes bare names through).
let resolveFile (prog: string) : string option =
    pathDirs ()
    |> Seq.tryPick (fun dir ->
        let candidate = Path.Combine(dir, prog)

        if File.Exists candidate then
            Some candidate
        else
            pathExts ()
            |> List.tryPick (fun e ->
                if File.Exists(candidate + e) then
                    Some(candidate + e)
                else
                    None))

let exists (prog: string) : bool =
    if isPathy prog then
        let resolved = Session.resolve prog

        File.Exists resolved
        || pathExts () |> List.exists (fun e -> File.Exists(resolved + e))
    else
        match cache with
        | Some s -> s.Contains prog
        | None ->
            pathDirs ()
            |> Array.exists (fun dir ->
                let candidate = Path.Combine(dir, prog)

                File.Exists candidate
                || pathExts () |> List.exists (fun e -> File.Exists(candidate + e)))
