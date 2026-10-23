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
let private pathExtsOf (value: string option) : string list =
    if OperatingSystem.IsWindows() then
        (value |> Option.defaultValue ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
    else
        []

let private pathExts () : string list =
    pathExtsOf (Environment.GetEnvironmentVariable "PATHEXT" |> Option.ofObj)

let private pathDirsOf (value: string) =
    value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

let private pathDirs () =
    pathDirsOf (
        Environment.GetEnvironmentVariable "PATH"
        |> Option.ofObj
        |> Option.defaultValue ""
    )

// the env a command runs with, as pairs applied in order (the `within env`
// layers outer-first, then its own NAME=value prefix): the last pair for a
// name wins, compared as the platform's env does
let private lastPair (pairs: (string * string) list) (name: string) : string option =
    let cmp =
        if OperatingSystem.IsWindows() then
            StringComparer.OrdinalIgnoreCase
        else
            StringComparer.Ordinal

    pairs |> List.rev |> List.tryFind (fun (k, _) -> cmp.Equals(k, name)) |> Option.map snd

let private isPathyName (prog: string) =
    prog.Contains '/'
    || (OperatingSystem.IsWindows() && (prog.Contains '\\' || prog.Contains ':'))

/// the PATH a command's env sets, when it differs from weir's own — None
/// means the child searches the PATH weir resolves on [D:overlay-path]
let overlayPath (pairs: (string * string) list) : string option =
    match lastPair pairs "PATH" with
    | Some v when v <> (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "") -> Some v
    | _ -> None

/// a bare program on the given PATH (and the env's PATHEXT on Windows):
/// the name as-given first in each dir, then each extension; a relative
/// entry reads from weir's working directory, where the child starts
let resolveOn (pairs: (string * string) list) (pathValue: string) (prog: string) : string option =
    let exts =
        match lastPair pairs "PATHEXT" with
        | Some v -> pathExtsOf (Some v)
        | None -> pathExts ()

    pathDirsOf pathValue
    |> Seq.tryPick (fun dir ->
        let candidate = Session.resolve (Path.Combine(dir, prog))

        if File.Exists candidate then
            Some candidate
        else
            exts
            |> List.tryPick (fun e -> if File.Exists(candidate + e) then Some(candidate + e) else None))

/// the file a command head names [D:path-which]: a bare name on the
/// PATH its env sets, else weir's own — the spawn's rule [D:overlay-path]
/// — and a path-like name is itself when it exists; PATHEXT on Windows
let which (pairs: (string * string) list) (prog: string) : string option =
    if prog = "" || prog.Contains '\000' then
        None
    elif isPathyName prog then
        let exts =
            match lastPair pairs "PATHEXT" with
            | Some v -> pathExtsOf (Some v)
            | None -> pathExts ()

        let r = Session.resolve prog

        if File.Exists r then
            Some r
        else
            exts |> List.tryPick (fun e -> if File.Exists(r + e) then Some(r + e) else None)
    else
        let pathValue =
            match lastPair pairs "PATH" with
            | Some v -> v
            | None -> Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""

        resolveOn pairs pathValue prog

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

let isPathy (prog: string) = isPathyName prog

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

/// `exists` under a command's env [D:overlay-path]: a PATH the env sets
/// is the one searched, else weir's own
let existsUnder (pairs: (string * string) list) (prog: string) : bool =
    if prog.Contains '\000' || isPathy prog then
        exists prog
    else
        match overlayPath pairs with
        | Some pv -> (resolveOn pairs pv prog).IsSome
        | None -> exists prog
