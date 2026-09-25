module Weir.Extern

open System
open System.IO

let mutable private cache: Set<string> option = None

// per-program resolution memo [D:head-word-bound]: exists() is called once
// per command head; without it a bareword ';'-spine (20k identical heads)
// rescans PATH×PATHEXT each time — ~20s on POSIX / >800s on Windows.
// Memoise the scan result per name — O(1) amortised per distinct program,
// while a one-command line still pays a single scan (not the full-PATH
// enumeration names() does for completion). ConcurrentDictionary: heads
// resolve off worker threads. Cleared with the name cache so a mid-run
// PATH change is seen after refresh().
let mutable private existsCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

let refresh () =
    cache <- None
    existsCache <- System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

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

/// the spawn-side resolution [D:windows-s2]: CreateProcess appends only
/// .exe to a bare name — a .bat/.cmd (any PATHEXT) implementation needs
/// its real file name handed over. Walks PATH per-dir: the name
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
    // parse-time NUL guard [D:nul-path]: a NUL-bearing head is not a real
    // program, so it is not-found — this returns before Session.resolve,
    // whose raise would abort the process here (the parse resolver runs
    // outside the runtime try). The classifier then emits its ordinary
    // located missing-command diagnostic instead of a SIGABRT. The
    // run-time raise still guards every other Session.resolve caller.
    if prog.Contains '\000' then
        false
    elif isPathy prog then
        let resolved = Session.resolve prog

        File.Exists resolved
        || pathExts () |> List.exists (fun e -> File.Exists(resolved + e))
    else
        // per-program memo (not names()): a distinct name pays one scan
        // then hits the cache; a one-command line does not enumerate all
        // of PATH. Live per-name scan, so a file created after refresh()
        // is still seen on its first query [D:head-word-bound].
        existsCache.GetOrAdd(
            prog,
            fun p ->
                pathDirs ()
                |> Array.exists (fun dir ->
                    let candidate = Path.Combine(dir, p)

                    File.Exists candidate
                    || pathExts () |> List.exists (fun e -> File.Exists(candidate + e))))
