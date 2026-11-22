/// The module loader: reading sources, loading and replaying modules, imports, and a vendored closure. One of the files Script.fs was [D:script-by-concern].
module Weir.ModuleLoader

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor
open Weir.Statements

// ---- the module loader [D:modules-v1] ------------------------------------
// A module's own base env: builtins (strict) + prelude + Self, with
// Self.scriptPath = the module's own path. Pure — no stdin/args/Session
// wiring, so loading a module never disturbs the entry's process facts.
// the type side alone — the check's reads; a check never builds the
// builtin value table
let private moduleBaseTypeEnv (absPath: string) : TypeEnv =
    let te = Prelude.extendTypes Builtins.typeEnvStrict

    {
        te with
            Modules = te.Modules |> Map.add "Self" selfMembers
    }

let private moduleBaseEnvs (absPath: string) : TypeEnv * Eval.Env =
    let te, ve = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv.Value

    {
        te with
            Modules = te.Modules |> Map.add "Self" selfMembers
    },
    Eval.splitBase ve |> Map.add "Self.scriptPath" (Eval.VStr absPath)

// the lock as `weir:` resolution reads it [D:add-module-closure]: a
// closure module's entry file is recorded there, so the checker and the
// LSP read lock.json — once per (path, mtime, length), not per import
let private lockReads =
    System.Collections.Concurrent.ConcurrentDictionary<
        string,
        DateTime * int64 * Result<Contracts.LockEntry list, string>
     >()

let private readLockCached (weirDir: string) : Result<Contracts.LockEntry list, string> =
    let p = IO.Path.Combine(weirDir, "lock.json")

    let stamp =
        try
            let fi = IO.FileInfo p

            if fi.Exists then
                fi.LastWriteTimeUtc, fi.Length
            else
                DateTime.MinValue, -1L
        with _ ->
            DateTime.MinValue, -1L

    match lockReads.TryGetValue p with
    | true, (t, n, r) when (t, n) = stamp -> r
    | _ ->
        let r = Contracts.readLock weirDir
        lockReads[p] <- (fst stamp, snd stamp, r)
        r

// a closure module's entry file [D:add-module-closure]: the lock entry
// for a .weir/modules/<name>/ directory names it, confined like every
// lock path; a directory the lock does not record is an error, never a
// guessed entry
let private vendoredDirEntry (weirDir: string) (name: string) : Result<string, string> =
    let dir = IO.Path.Combine(weirDir, "modules", name)

    match readLockCached weirDir with
    | Error e -> Error $"'{quoteData dir}' is a vendored module directory, and the lock cannot be read: {e}"
    | Ok entries ->
        match entries |> List.tryFind (fun e -> e.Kind = "module" && e.Name = name) with
        | Some({ Closure = Some c } as e) -> Contracts.closureFileDest weirDir e c.Entry
        | Some _ ->
            Error
                $"'{quoteData dir}' is a vendored module directory, but the lock records module '{quoteData name}' as a single file — re-add it: weir add module <host>/<org>/<repo>//<file>@<ref> --as {quoteData name}"
        | None ->
            Error
                $"'{quoteData dir}' is a vendored module directory the lock has no entry for, so its entry file is unknown — re-add it (weir add module <host>/<org>/<repo>//<file>@<ref> --as {quoteData name}) or remove the directory"

let private resolveImportPathRaw (importingAbsPath: string) (path: string) : string =
    let dir = IO.Path.GetDirectoryName importingAbsPath

    if path.StartsWith "weir:" then
        // the vendored namespace [D:add-module] — shape-scoped, never a
        // fallback: `weir:` always resolves via the .weir/ walk (a file
        // literally containing a colon keeps the ./ spelling). Both bare
        // import shapes were already meaningful, so the vendored
        // spelling had to be distinct.
        let name = path.Substring 5

        match Contracts.findWeirDir dir with
        | Ok wd ->
            let modPath = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name + ".weir"))

            let modDir = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name))

            if IO.File.Exists modPath then
                modPath
            elif IO.Directory.Exists modDir then
                // a closure module [D:add-module-closure]: its entry per
                // the lock (a fault is vendoredFault's error)
                match vendoredDirEntry wd name with
                | Ok entry -> entry
                | Error _ -> modPath
            else
                // one namespace, two homes [D:schema-types]: vendored
                // modules first, then generated types (.weir/types/ —
                // `weir gen types` output); a name in both is refused at
                // import (vendoredCollision), a missing name still errors
                // through the modules path below
                let typesPath = IO.Path.GetFullPath(IO.Path.Combine(wd, "types", name + ".weir"))

                if IO.File.Exists typesPath then typesPath else modPath
        | Error _ ->
            // no .weir/ anywhere: a display path for the not-found error
            IO.Path.GetFullPath(IO.Path.Combine(dir, ".weir", "modules", name + ".weir"))
    else
        IO.Path.GetFullPath(IO.Path.Combine(dir, path))

// a module's identity is its real path [D:module-once]: every symlinked
// component resolved, so two links to one file import one module
let rec internal realPath (full: string) : string =
    match IO.Path.GetDirectoryName full with
    | null -> full
    | parent ->
        let here = IO.Path.Combine(realPath parent, IO.Path.GetFileName full)

        try
            match IO.FileInfo(here).ResolveLinkTarget true with
            | null -> here
            | target -> realPath target.FullName
        with _ ->
            here


// one `weir:` name, two vendored homes: a module under .weir/modules and a
// generated types module under .weir/types both named <name> — an error,
// never a silent precedence (the modules file would shadow the types one).
// A module's two forms, <name>.weir and <name>/, are the same error
// [D:add-module-closure], and so is a <name>/ the lock does not record
let private vendoredFault (importingAbsPath: string) (path: string) : string option =
    if path.StartsWith "weir:" then
        let name = path.Substring 5

        match Contracts.findWeirDir (IO.Path.GetDirectoryName importingAbsPath) with
        | Ok wd ->
            let m = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name + ".weir"))
            let md = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name))
            let t = IO.Path.GetFullPath(IO.Path.Combine(wd, "types", name + ".weir"))
            let hasDir = IO.Directory.Exists md

            if IO.File.Exists m && hasDir then
                Some
                    $"'{quoteData path}' names two vendored forms, {quoteData m} and {quoteData md} — a module is one file or one directory; re-add it (weir add module … --as {quoteData name}) to settle the form, or remove the stale one"
            elif (IO.File.Exists m || hasDir) && IO.File.Exists t then
                let m = if hasDir then md else m

                Some
                    $"'{quoteData path}' names two vendored files, {quoteData m} and {quoteData t} — a vendored module and a generated types module cannot share a name; regenerate the types under another name (weir gen types --schema <schema> --out .weir/types/<other>.weir) or re-add the module with another --as"
            elif hasDir then
                match vendoredDirEntry wd name with
                | Error e -> Some e
                | Ok _ -> None
            else
                None
        | Error _ -> None
    else
        None

// F#'s filename fallback for a bare `module`: capitalize the base name. A
// non-identifier filename has no derivation (name it, or import `as`).
let internal deriveModuleName (absPath: string) : string option =
    let bare = IO.Path.GetFileNameWithoutExtension absPath

    if
        bare.Length > 0
        && System.Char.IsLetter bare[0]
        && bare |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')
    then
        Some(string (System.Char.ToUpper bare[0]) + bare.Substring 1)
    else
        None

// does evaluating this RHS at import run a command? — the weak purity rule
// [D:modules-v1]. Eager positions only, stops at lambdas: a command in a
// lambda body is deferred, so a param-ful `let f r = git …` is a function,
// not an import-time effect. A module/init let refines it with
// Check.commandRunsNow (a lazy command seq passes, [D:lazy-command-lets]).
// The walk lives in Check beside the re-enumeration machinery that
// shares it [D:reenum-warning].
let runsCommandT: Check.TypedExpr -> bool = Check.runsCommandT

// a module let that calls a command-reaching helper of its own runs the
// command at import, as a direct command would [D:modules-v1]
let internal helperAtImport (name: string) (h: string) =
    $"a module 'let' cannot run a command at import — '{h}' runs one, and this calls it now; wrap this in a function too (let {name} () = …), the command runs when a script calls it"

// a module let whose RHS runs a command now; a lazy command seq is a
// value and passes [D:lazy-command-lets]
let internal commandAtImport (why: string) =
    $"a module 'let' cannot run a command at import: {why}"

// a source file's read, once into a result: an entry script or an import
// that is missing, a directory, unreadable or not a regular file (a device
// that never ends) is a weir-worded refusal, never a raw .NET exception
type SourceFault =
    | NotFound
    | IsDirectory
    | PermissionDenied
    | NotRegular
    | IoFault of string
    // the first invalid byte's 1-based line and column [D:source-utf8]
    | InvalidUtf8 of line: int * col: int

// a zero-length stat is a pipe, a procfs file or a device: read it with a
// ceiling, so /dev/zero refuses instead of filling memory
let private unsizedCap = 16 * 1024 * 1024

let private readSourceBytes (path: string) : byte[] =
    use fs =
        new IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)

    let sized =
        try
            fs.Length
        with _ ->
            0L

    if sized > 0L then
        use ms = new IO.MemoryStream(int sized)
        fs.CopyTo ms
        ms.ToArray()
    else
        use ms = new IO.MemoryStream()
        let buf = Array.zeroCreate<byte> 65536
        let mutable n = fs.Read(buf, 0, buf.Length)

        while n > 0 do
            if ms.Length + int64 n > int64 unsizedCap then
                raise (IO.InvalidDataException "unsized")

            ms.Write(buf, 0, n)
            n <- fs.Read(buf, 0, buf.Length)

        ms.ToArray()

// a source file is UTF-8 [D:source-utf8]: strict decoding, so the first
// invalid byte is a located refusal, never a silent U+FFFD. One leading
// BOM is skipped; lines split as ReadAllLines splits (\r\n, \n, \r).
exception private BadUtf8 of line: int * col: int

/// the one line splitter [D:one-pipeline]: a line ends at \r\n, \n or a
/// bare \r — as ReadAllLines reads a script — so fmt and the LSP see
/// the lines check and run see. Every piece is kept: a text ending in a
/// break yields a final "" (an editor's last, empty line)
let splitSourceText (text: string) : string list =
    let acc = Collections.Generic.List<string>()
    let mutable start = 0
    let mutable i = 0

    while i < text.Length do
        match text[i] with
        | '\r' ->
            acc.Add(text.Substring(start, i - start))

            i <-
                if i + 1 < text.Length && text[i + 1] = '\n' then
                    i + 2
                else
                    i + 1

            start <- i
        | '\n' ->
            acc.Add(text.Substring(start, i - start))
            i <- i + 1
            start <- i
        | _ -> i <- i + 1

    acc.Add(text.Substring start)
    List.ofSeq acc

/// does the text end in a line break (any of the three)?
let endsWithLineBreak (text: string) : bool =
    text.EndsWith "\n" || text.EndsWith "\r"

let private strictUtf8 = Text.UTF8Encoding(false, true)

let private decodeSourceLines (bytes: byte[]) : string list =
    let start =
        if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
            3
        else
            0

    let text =
        try
            strictUtf8.GetString(bytes, start, bytes.Length - start)
        with :? Text.DecoderFallbackException as ex ->
            // the bad byte's offset; its line is the newlines before it,
            // its column the code points before it on that line, plus one
            let at = start + max 0 ex.Index
            let lineStart = (Array.LastIndexOf(bytes, 10uy, max 0 (at - 1)) + 1) |> max start

            let line =
                1 + (bytes[start .. at - 1] |> Array.sumBy (fun b -> if b = 10uy then 1 else 0))

            let prefix = Text.Encoding.UTF8.GetString(bytes, lineStart, at - lineStart)
            let col = 1 + (prefix.EnumerateRunes() |> Seq.length)
            raise (BadUtf8(line, col))

    use sr = new IO.StringReader(text)
    let acc = Collections.Generic.List<string>()
    let mutable line = sr.ReadLine()

    while not (isNull line) do
        acc.Add line
        line <- sr.ReadLine()

    List.ofSeq acc

let readSourceFile (path: string) : Result<string list, SourceFault> =
    if IO.Directory.Exists path then
        Error IsDirectory
    else
        try
            Ok(decodeSourceLines (readSourceBytes path))
        with
        | :? IO.FileNotFoundException
        | :? IO.DirectoryNotFoundException -> Error NotFound
        | :? UnauthorizedAccessException -> Error PermissionDenied
        | :? IO.InvalidDataException -> Error NotRegular
        | BadUtf8(l, c) -> Error(InvalidUtf8(l, c))
        | :? IO.IOException as ex -> Error(IoFault ex.Message)

/// the entry script's read, worded for the CLI: `weir: <message>`, exit 2
let readEntryScript (path: string) : Result<string list, string> =
    readSourceFile path
    |> Result.mapError (function
        | NotFound -> $"no such script: {quoteData path}"
        | IsDirectory -> $"{quoteData path} is a directory, not a script"
        | PermissionDenied -> $"cannot read {quoteData path}: permission denied"
        | NotRegular -> $"cannot read {quoteData path}: not a regular file — a script is a text file"
        | IoFault m -> $"cannot read {quoteData path}: {quoteData m}"
        | InvalidUtf8(l, c) ->
            $"{quoteData path}:{l}:{c}: invalid UTF-8 — a source file must be UTF-8; this byte is not")

// buffer-over-disk [D:modules-v1]: when the LSP sets this, an imported file's
// content is read through it (open editor buffers first, then disk) so an
// unsaved dependency checks against what the user sees; None -> CLI, plain
// disk (decision 14).
let importSourceOverride: (string -> string list option) option ref = ref None

// the import read, once into a Result [D:lockfile-confinement]: an
// unguarded Exists-then-ReadAllLines crashes on an unreadable file
// (mode 000), and the two-call shape (isNone then get) races a file
// deleted between them. Absent -> None (the "no file at …" message);
// present-but-unreadable -> Error naming the path (a located
// diagnostic, never a crash). The buffer override (LSP) is absent-or-present
// by its own contract, so it maps to Ok.
type internal ImportRead =
    | ISource of string list
    | IAbsent
    | IUnreadable of string
    | IBadUtf8 of line: int * col: int

let internal readImportSource (absPath: string) : ImportRead =
    // before the buffer override too: the editor's read of a file that is
    // not open falls to the disk [D:artifact-not-a-pipe]
    if Contracts.pipeLike absPath then
        IUnreadable "not a regular file — a module is a text file"
    else

        match importSourceOverride.Value with
        | Some f ->
            match f absPath with
            | Some lines -> ISource lines
            | None -> IAbsent
        | None ->
            // present but unreadable — permission denied, a directory, a
            // device, an IO fault; name the path and the reason, located
            match readSourceFile absPath with
            | Ok lines -> ISource lines
            | Error NotFound -> IAbsent
            | Error IsDirectory -> IUnreadable "it is a directory, not a module"
            | Error PermissionDenied -> IUnreadable "permission denied"
            | Error NotRegular -> IUnreadable "not a regular file — a module is a text file"
            | Error(IoFault m) -> IUnreadable m
            | Error(InvalidUtf8(l, c)) -> IBadUtf8(l, c)

// one run's module loads [D:parallel-imports]: the per-key memo (real
// path -> result) the loader always had, now shared by worker threads —
// the import DAG is discovered before any module checks (discoverImports
// below), and `Schedule` hands its acyclic modules to at most
// `importWorkerBound` workers, each taking the ready module earliest in
// load order. `Run` is the single-flight door every load goes through:
// a module is claimed by whoever asks first (a worker, or the importer's
// fold reaching its import line) and everyone else waits for that one
// result, so a module checks exactly once and its Body is one object
// [D:module-once]. Results surface at the import statement in load
// order, so diagnostics are the sequential loader's to the byte. An
// Error is remembered with the path it was reached by: a second site
// spelling the path differently re-checks, as the sequential loader did.
// a module's source as the check takes it, read and assembled once
// [D:parallel-imports]: discovery reads a file for its import lines and
// the check that follows takes the same body instead of reading again
type internal PreparedSource = {
    Read: ImportRead
    // for ISource: the numbered body and its assembly
    Body: ((int * string) list * Result<LogicalLine list, string>) option
}

let internal prepareSource (absPath: string) : PreparedSource =
    let read = readImportSource absPath

    match read with
    | ISource rawLines ->
        let body, bodyOffset, _ = scriptBody rawLines
        let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)

        {
            Read = read
            Body = Some(numbered, assemble numbered)
        } // raw lines: assemble classifies/strips internally [D:content-bytes]
    | _ -> { Read = read; Body = None }

type private LoadState =
    | LPending
    // the claiming thread's id: its own nested asks compute, every other
    // thread waits on the event
    | LRunning of System.Threading.ManualResetEventSlim * owner: int
    | LDone of (unit -> Result<LoadedModule, ImportError>) * absPath: string

// the worker bound: the processor count, capped at 8 — a prompt script
// should not fan 64 threads, and past 8 a DAG rarely has that many ready
// siblings; one processor is the sequential loader. Tests set it, never
// a flag or an env var [D:parallel-imports]
let mutable importWorkerBound = min 8 Environment.ProcessorCount

type ModuleLoads() =
    let gate = obj ()
    let states = Collections.Generic.Dictionary<string, LoadState>()
    // (key, absPath, deps, run) in load order; a worker takes the first
    // still pending whose deps are all done
    let plan =
        ResizeArray<string * string * string list * (unit -> Result<LoadedModule, ImportError>)>()

    let sources = Collections.Generic.Dictionary<string, PreparedSource>()
    let parentCwd = Session.Cwd()
    let mutable active = 0

    let isDone key =
        match states.TryGetValue key with
        | true, LDone _ -> true
        | _ -> false

    let pickable () =
        plan
        |> Seq.filter (fun (key, _, deps, _) ->
            (match states.TryGetValue key with
             | true, LPending -> true
             | _ -> false)
            && deps |> List.forall isDone)

    let me () =
        Threading.Thread.CurrentThread.ManagedThreadId

    // under the gate
    let finish (key: string) (absPath: string) (result: unit -> Result<LoadedModule, ImportError>) =
        match states.TryGetValue key with
        | true, LRunning(ev, _) ->
            states[key] <- LDone(result, absPath)
            ev.Set()
        | _ -> ()

    let capture (compute: unit -> Result<LoadedModule, ImportError>) : unit -> Result<LoadedModule, ImportError> =
        try
            let r = compute ()
            fun () -> r
        with ex ->
            let info = Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex

            fun () ->
                info.Throw()
                Unchecked.defaultof<_>

    // the worker claims at pick time (under the gate), so two workers never
    // take one module; the load's own Run sees its thread as the owner and
    // computes. A load that returns without passing Run (none does, by
    // discovery's filter) is finished here all the same
    member private this.Worker() =
        Session.enterWorker parentCwd

        try
            let mutable go = true

            while go do
                let next =
                    lock gate (fun () ->
                        match pickable () |> Seq.tryHead with
                        | Some(key, absPath, _, run) ->
                            states[key] <- LRunning(new Threading.ManualResetEventSlim(false), me ())
                            Some(key, absPath, run)
                        | None ->
                            active <- active - 1
                            None)

                match next with
                | Some(key, absPath, run) ->
                    let result = capture run

                    lock gate (fun () ->
                        finish key absPath result
                        this.Pump())
                | None -> go <- false
        finally
            Session.exitWorker ()

    // under the gate: workers up to the bound while ready modules
    // outnumber them. The reserved stack is the main thread's
    // [D:block-width]; a refused thread leaves the rest to the askers
    member private this.Pump() =
        let ready = pickable () |> Seq.length
        let mutable spawning = true

        while spawning && active < min importWorkerBound ready do
            let t = Threading.Thread((fun () -> this.Worker()), 256 * 1024 * 1024)
            t.IsBackground <- true

            try
                t.Start()
                active <- active + 1
            with _ ->
                spawning <- false

    // discovery's read, kept for the check (the file read outside the gate;
    // a race reads twice, harmlessly)
    member internal this.Prepare(absPath: string) : PreparedSource =
        match lock gate (fun () -> sources.TryGetValue absPath) with
        | true, p -> p
        | _ ->
            let p = prepareSource absPath
            lock gate (fun () -> sources[absPath] <- p)
            p

    // the check's read: discovery's if it prepared this path, else fresh
    member internal this.TakeSource(absPath: string) : PreparedSource =
        match
            lock gate (fun () ->
                match sources.TryGetValue absPath with
                | true, p ->
                    sources.Remove absPath |> ignore
                    Some p
                | _ -> None)
        with
        | Some p -> p
        | None -> prepareSource absPath

    member this.Schedule(nodes: (string * string * string list * (unit -> Result<LoadedModule, ImportError>)) list) =
        lock gate (fun () ->
            for (key, _, _, _) as node in nodes do
                if not (states.ContainsKey key) then
                    states[key] <- LPending
                    plan.Add node

            this.Pump())

    member this.Run
        (key: string)
        (absPath: string)
        (importAs: string option)
        (compute: unit -> Result<LoadedModule, ImportError>)
        : Result<LoadedModule, ImportError> =
        let realias (r: Result<LoadedModule, ImportError>) =
            match r with
            | Ok cached ->
                Ok {
                    cached with
                        Alias = importAs |> Option.defaultValue cached.NaturalName
                }
            | Error _ -> r

        let claim =
            lock gate (fun () ->
                match states.TryGetValue key with
                | true, LDone(result, at) -> Choice1Of3(result, at)
                | true, LRunning(ev, owner) when owner <> me () -> Choice2Of3 ev
                | true, LRunning(ev, _) -> Choice3Of3 ev
                | _ ->
                    let ev = new Threading.ManualResetEventSlim(false)
                    states[key] <- LRunning(ev, me ())
                    Choice3Of3 ev)

        match claim with
        | Choice1Of3(result, at) ->
            match result () with
            | Error _ when at <> absPath -> compute ()
            | r -> realias r
        | Choice2Of3 ev ->
            ev.Wait()
            this.Run key absPath importAs compute
        | Choice3Of3 _ ->
            let result = capture compute

            lock gate (fun () ->
                finish key absPath result
                this.Pump())

            realias (result ())

// a `weir:` import names a vendored module, never a path [D:add-module]
let private vendoredNameFault (path: string) : bool =
    path.StartsWith "weir:"
    && (let n = path.Substring 5 in n = "" || n.Contains "/" || n.Contains "\\" || n.Contains "..")

// resolve + check an imported module to a LoadedModule, or an ImportError.
// The graph [D:modules-v1]: `loads` (real path -> result) checks a shared
// module once (diamonds) and schedules the DAG's siblings on workers
// [D:parallel-imports]; `chain` is the current DFS path of importing
// files, so a repeat is a cycle. Transitive: a reached module's own imports
// resolve too, sharing the cache with this module pushed on the chain.
// sigContract [D:unused-bindings]: a #sig file is a module whose
// members (version, exhaustive, Cmd) are read by the sig loader, not by
// the module's own text — the loader is the use, so the unused check
// stays off for that one consumer
// a check-only run keeps no module body [D:check-drops-bodies]: the
// typed trees are read while the module loads and never again (no
// replay, no capability walk, no editor), so holding them until exit
// only makes every collection copy them; a #sig load keeps its body —
// the sig loader reads it after the load
let mutable dropModuleBodies = false

let rec loadModuleCachedWith
    (sigContract: bool)
    (mkR: TypeEnv -> Parser.Resolver)
    (loads: ModuleLoads)
    (chain: string list)
    (importingAbsPath: string)
    (path: string)
    (importAs: string option)
    : Result<LoadedModule, ImportError> =
    // the path as the user reaches it (diagnostics, the LSP's locations)
    // and the identity [D:module-once] apart: on macOS /var is a link to
    // /private/var, and an editor must get back the path it opened
    let absPath = resolveImportPathRaw importingAbsPath path
    let key = realPath absPath

    // an import-statement error reports at the import line (File=None)
    let stmt msg =
        Error {
            File = None
            Line = 0
            Col = 0
            Message = msg
        }

    let notAModule =
        stmt $"{quoteData absPath} is not a module; add `module` at the top, or invoke it as a command"

    if vendoredNameFault path then
        stmt "a weir: import names a vendored module, never a path — weir:<name>, no separators"
    elif (vendoredFault importingAbsPath path).IsSome then
        stmt (vendoredFault importingAbsPath path).Value
    elif key = realPath importingAbsPath then
        stmt "a file cannot import itself"
    elif List.contains key chain then
        // a cycle — same detector as self-import, different rendering
        // (decision 9): render the loop by file name
        let recent = chain |> List.takeWhile ((<>) key)

        let loop =
            (key :: (List.rev recent @ [ key ]))
            |> List.map IO.Path.GetFileName
            |> String.concat " → "

        stmt $"import cycle: {quoteData loop}"
    else

        // a diamond's shared module is checked once (re-aliased per import
        // site), by whichever thread asks first [D:parallel-imports]
        loads.Run key absPath importAs (fun () ->

            // read the source once [D:lockfile-confinement] — no Exists/read race,
            // and present-but-unreadable is its own located diagnostic, not a crash
            let prepared = loads.TakeSource absPath

            match prepared.Read with
            | IAbsent ->
                if path.StartsWith "weir:" then
                    let n = path.Substring 5

                    stmt
                        $"no vendored module '{quoteData n}' ({quoteData absPath}) — vendor it: weir add module {quoteData n} (an official library module, its latest release; @<tag> pins one), or <host>/<org>/<repo>//<file>@<ref> --as {quoteData n} (a generated types module lands here via: weir gen types --schema {quoteData n})"
                else
                    stmt $"cannot resolve import: no file at {quoteData absPath}"
            | IUnreadable reason -> stmt $"cannot read import {quoteData absPath}: {quoteData reason}"
            | IBadUtf8(l, c) ->
                Error {
                    File = Some absPath
                    Line = l
                    Col = c
                    Message = "invalid UTF-8 — a source file must be UTF-8; this byte is not"
                }
            | ISource rawLines ->
                let numbered, assembled = prepared.Body.Value

                match assembled with
                | Error msg -> stmt (assemblyErrorLocated absPath numbered msg)
                | Ok [] -> notAModule
                | Ok(first :: rest) ->
                    let baseTenv = moduleBaseTypeEnv absPath

                    match Parser.parseLineFull (mkR baseTenv) first.Text with
                    | Ok(SModule(declName, _)) ->
                        let natural = declName |> Option.orElseWith (fun () -> deriveModuleName absPath)

                        match importAs |> Option.orElse natural with
                        | None ->
                            stmt
                                $"cannot derive a module name from '{quoteData (IO.Path.GetFileName absPath)}'; name it (module Name) or import it as a name (import \"…\" as Name)"
                        | Some alias ->
                            // transitive: a reached module's own imports resolve, sharing
                            // the cache, with this module pushed on the chain
                            let childLoader: ImportLoader =
                                fun p _ a -> loadModuleCachedWith false mkR loads (key :: chain) absPath p a

                            // a module-content error reports at the module's own site
                            let at line col msg : Result<_, ImportError> =
                                Error {
                                    File = Some absPath
                                    Line = line
                                    Col = col
                                    Message = msg
                                }

                            // unused bindings in the module's own text
                            // [D:unused-bindings]: signed members exempt at flush
                            let moduleUnused = UnusedTracker()

                            // first declaration per type name: line * came-from-import
                            // — a module refuses a duplicate as a script does
                            // [D:dup-type-decl]
                            let declaredTypes = System.Collections.Generic.Dictionary<string, int * bool>()

                            // helpers: the module's command-reaching functions and
                            // lazy command values so far, in declaration order
                            let rec go
                                (tenv: TypeEnv)
                                (sc: SigContext)
                                (helpers: Map<string, int>)
                                (implLines: (string * int) list)
                                (accBody: (LogicalLine * CheckedStmt) list)
                                (stmts: LogicalLine list)
                                =
                                match stmts with
                                | [] -> Ok(tenv, sc, List.rev implLines, List.rev accBody)
                                | (ll: LogicalLine) :: tail ->
                                    match checkStatement true (Some sc) mkR childLoader tenv ll with
                                    | Error d ->
                                        // a deeper module's error (File already set)
                                        // propagates unchanged; this module's own error
                                        // takes this module's site
                                        match d.File with
                                        | Some _ ->
                                            Error {
                                                File = d.File
                                                Line = d.PhysLine
                                                Col = d.PhysCol
                                                Message = d.Message
                                            }
                                        | None -> at d.PhysLine d.PhysCol d.Message
                                    | Ok chk ->
                                        let eagerHelper =
                                            match chk.Kind with
                                            | KLet(_, _, te)
                                            | KLetPat(_, _, te) -> Check.eagerCommandHelper helpers te
                                            | _ -> None

                                        let runsNow =
                                            match chk.Kind with
                                            | KLet(_, _, te)
                                            | KLetPat(_, _, te) -> Check.commandRunsNow helpers te
                                            | _ -> None

                                        match chk.Kind with
                                        | KType(decl, _) when declaredTypes.ContainsKey decl.Name ->
                                            let firstLine, viaImport = declaredTypes[decl.Name]

                                            at
                                                ll.Head
                                                1
                                                (if viaImport then
                                                     $"type '{decl.Name}' is already provided by the import at line {firstLine}; rename one"
                                                 else
                                                     $"type '{decl.Name}' is already declared at line {firstLine}")
                                        | KType(decl, validator) ->
                                            declaredTypes[decl.Name] <- (ll.Head, false)

                                            go
                                                chk.Env
                                                sc
                                                helpers
                                                implLines
                                                ((ll, CType(decl, validator)) :: accBody)
                                                tail
                                        | KImport lm ->
                                            for tn in lm.TypeNames do
                                                if not (declaredTypes.ContainsKey tn) then
                                                    declaredTypes[tn] <- (ll.Head, true)

                                            go chk.Env sc helpers implLines ((ll, CImport lm) :: accBody) tail
                                        | KSig(name, sd) ->
                                            // the sig binds nothing yet [D:module-signatures];
                                            // the paired implementation is the value
                                            let sc' = {
                                                sc with
                                                    Pending = Map.add name sd sc.Pending
                                                    Signed = Set.add name sc.Signed
                                                    SigLines = Map.add name sd.Line sc.SigLines
                                            }

                                            go chk.Env sc' helpers implLines accBody tail
                                        | KLet _
                                        | KLetPat _ when runsNow.IsSome -> at ll.Head 1 (commandAtImport runsNow.Value)
                                        | KLet(name, _, _) when eagerHelper.IsSome ->
                                            at ll.Head 1 (helperAtImport name eagerHelper.Value)
                                        | KLetPat _ when eagerHelper.IsSome ->
                                            at ll.Head 1 (helperAtImport "f" eagerHelper.Value)
                                        | KLet(name, _, te) ->
                                            let sc' = {
                                                sc with
                                                    Pending = Map.remove name sc.Pending
                                                    Implemented = Set.add name sc.Implemented
                                            }

                                            let implLines' =
                                                if Set.contains name sc.Signed then
                                                    (name, ll.Head) :: implLines
                                                else
                                                    implLines

                                            moduleUnused.Feed ll chk

                                            go
                                                chk.Env
                                                sc'
                                                (Check.recordCommandBinding helpers name te)
                                                implLines'
                                                ((ll, CLet(name, te)) :: accBody)
                                                tail
                                        | KLetPat(pat, schemes, te) ->
                                            let sc' = {
                                                sc with
                                                    Implemented =
                                                        schemes
                                                        |> List.fold (fun s (n, _) -> Set.add n s) sc.Implemented
                                            }

                                            moduleUnused.Feed ll chk
                                            go chk.Env sc' helpers implLines ((ll, CLetPat(pat, te)) :: accBody) tail
                                        | KModule _ ->
                                            at ll.Head 1 "a file has at most one 'module' marker, and it comes first"
                                        | KCmd _
                                        | KExpr _ ->
                                            at
                                                ll.Head
                                                1
                                                "a module declares only — 'type' and 'let', no commands or bare expressions"

                            match go baseTenv SigContext.empty Map.empty [] [] rest with
                            | Error e -> Error e
                            | Ok(finalTenv, sc, implLines, moduleBody) ->
                                // sig without impl = check error at the sig
                                // [D:module-signatures]; impl without sig = private
                                let orphan =
                                    sc.Pending |> Map.toList |> List.sortBy (fun (_, sd) -> sd.Line) |> List.tryHead

                                // a signed member's /// doc belongs on the signature
                                // (one home) [D:module-signatures]
                                let docOnImpl =
                                    lazy
                                        (docAttachments rawLines
                                         |> List.tryPick (fun d ->
                                             implLines
                                             |> List.tryFind (fun (_, implLine) -> d.Line = implLine)
                                             |> Option.map (fun (n, _) -> n, d)))

                                let lawError =
                                    match orphan with
                                    | Some(name, sd) ->
                                        Some(
                                            sd.Line,
                                            sd.Col,
                                            $"signature without implementation — no 'let {name} … = …' follows in this module"
                                        )
                                    | None ->
                                        match docOnImpl.Value with
                                        | Some(name, d) ->
                                            Some(
                                                max 1 (d.Line - List.length d.Doc),
                                                1,
                                                $"'{name}' is signed, and its /// doc belongs on the signature — move it above 'let {name} : …' (one doc home)"
                                            )
                                        | None when not sigContract ->
                                            // an unsigned member the module never reads is
                                            // dead private code [D:unused-bindings]
                                            match moduleUnused.Flush sc.Signed true |> List.tryHead with
                                            | Some f -> Some(f.ULine, f.UCol, f.UMessage)
                                            | None -> None
                                        | None -> None

                                match lawError with
                                | Some(l, c, msg) -> at l c msg
                                | None ->

                                    // a module's own types (from its Body's decls); what it
                                    // imported crosses only as reachableDefs below, never as
                                    // its own [D:transitive-types]; Members already exclude
                                    // imported ones (those live under Modules[·], not Values)
                                    let typeDefs =
                                        moduleBody
                                        |> List.choose (function
                                            | _, CType(decl, _) ->
                                                Map.tryFind decl.Name finalTenv.Types
                                                |> Option.map (fun d -> decl.Name, d)
                                            | _ -> None)

                                    // the export split [D:module-signatures]: the signature
                                    // is the export — signed members and declared types'
                                    // constructors cross; unsigned members stay private
                                    // (carried for the import-of-private error only)
                                    let ctorNames =
                                        typeDefs
                                        |> List.collect (fun (_, d) ->
                                            match d with
                                            | Union u -> u.Cases |> List.map fst
                                            | Record _ -> [])
                                        |> Set.ofList

                                    // own = declared in this body, a builtin's name
                                    // included — exported, reachable qualified
                                    // (`Doc.Text`); the bare name stays the builtin
                                    // [D:module-builtin-names]
                                    let declared =
                                        moduleBody
                                        |> List.collect (function
                                            | _, CLet(n, _) -> [ n ]
                                            | _, CLetPat(p, _) -> Parser.patLeafNames p
                                            | _ -> [])
                                        |> Set.ofList
                                        |> Set.union ctorNames

                                    let ownMembers =
                                        finalTenv.Values
                                        |> Map.toList
                                        |> List.filter (fun (n, _) -> declared.Contains n)

                                    let members =
                                        ownMembers
                                        |> List.filter (fun (n, _) ->
                                            Set.contains n sc.Signed || Set.contains n ctorNames)

                                    let privateMembers =
                                        ownMembers
                                        |> List.filter (fun (n, _) ->
                                            not (Set.contains n sc.Signed) && not (Set.contains n ctorNames))

                                    // an imported type reachable from what this module
                                    // exports (a signature, an own type's field or case)
                                    // crosses with it, so an importer can read, show and
                                    // compare the values it receives; it is not this
                                    // module's type (TypeNames: no qualified literal, no
                                    // re-export under this alias) [D:transitive-types]
                                    let reachableDefs =
                                        let own = typeDefs |> List.map fst |> Set.ofList
                                        let seen = System.Collections.Generic.HashSet<string>(own)

                                        let rec walk (ty: Ty) =
                                            match ty with
                                            | TFun(a, b) ->
                                                walk a
                                                walk b
                                            | TSeq t -> walk t
                                            | TTuple ts -> List.iter walk ts
                                            | TRowVar(_, fields) ->
                                                for _, t in fields do
                                                    walk t
                                            | TNamed(n, args) ->
                                                List.iter walk args

                                                if not (seen.Contains n) && not (Map.containsKey n baseTenv.Types) then
                                                    match Map.tryFind n finalTenv.Types with
                                                    | Some def ->
                                                        seen.Add n |> ignore
                                                        defTys def |> List.iter walk
                                                    | None -> ()
                                            | _ -> ()

                                        and defTys (def: TypeDef) =
                                            match def with
                                            | Record rd -> rd.Fields |> List.map snd
                                            | Union u -> u.Cases |> List.choose snd

                                        for _, sch in members do
                                            walk sch.Ty

                                        for _, def in typeDefs do
                                            defTys def |> List.iter walk

                                        seen
                                        |> Seq.filter (fun n -> not (own.Contains n))
                                        |> Seq.sort
                                        |> List.ofSeq
                                        |> List.choose (fun n ->
                                            Map.tryFind n finalTenv.Types |> Option.map (fun d -> n, d))

                                    let loaded = {
                                        Alias = alias
                                        NaturalName = natural |> Option.defaultValue alias
                                        AbsPath = absPath
                                        TypeDefs = typeDefs @ reachableDefs
                                        Members = members
                                        PrivateMembers = privateMembers
                                        MemberDocs =
                                            let byLine =
                                                docAttachments rawLines
                                                |> List.map (fun d -> d.Line, d.Doc)
                                                |> Map.ofList

                                            sc.SigLines
                                            |> Map.toList
                                            |> List.choose (fun (n, l) ->
                                                Map.tryFind l byLine |> Option.map (fun d -> n, d))
                                            |> Map.ofList
                                        ModuleDoc = moduleDocOf rawLines
                                        TypeNames = typeDefs |> List.map fst
                                        TypeOrigins =
                                            let ownOrigin n =
                                                $"{natural |> Option.defaultValue alias}.{n}@{absPath}"

                                            (typeDefs |> List.map (fun (n, _) -> n, ownOrigin n))
                                            @ (reachableDefs
                                               // an anonymous shape carries no origin: same-shape
                                               // anonymous types are one type, from any module
                                               // [D:anon-literals]
                                               |> List.filter (fun (n, _) -> not (n.StartsWith "{|"))
                                               |> List.map (fun (n, _) ->
                                                   n,
                                                   Map.tryFind n finalTenv.TypeOrigins
                                                   |> Option.defaultValue (ownOrigin n)))
                                        Body =
                                            (if dropModuleBodies && not sigContract then
                                                 []
                                             else
                                                 moduleBody)
                                        MayRebind = false
                                    }

                                    Ok loaded
                    | Ok _ -> notAModule
                    | Error _ -> notAModule)

// the module-body replayer [D:modules-v1]: evaluate a checked module's Body
// in its own clean env (Self.scriptPath is the module's; process facts ride
// from the entry), exposing a nested import's members as `alias.member` for
// this module's own lets. Returns the module's venv (bindings under bare
// names); the caller exposes this module's Members qualified.
//
// A module's body runs once per program [D:module-once]: keyed by its
// identity, and by the checked Body itself — a reloaded module (the REPL
// re-importing an edited file) is a new body and runs afresh.
let private replayed =
    System.Collections.Generic.Dictionary<string, (LogicalLine * CheckedStmt) list * Eval.Env>()

/// what a `let`, a pattern or a type binds [D:statement-exec] — the part
/// of a statement's effect a module body and a run share; the values, and
/// the names a `let` or a pattern made. None for every other statement
let internal bindsOf (venv: Eval.Env) (stmt: CheckedStmt) : (Eval.Env * (string * Eval.Value) list) option =
    match stmt with
    | CType(decl, validator) -> Some(Eval.declare venv decl validator, [])
    | CLet(name, te) ->
        let v = Eval.eval venv te
        Some(Map.add name v venv, [ name, v ])
    | CLetPat(pat, te) ->
        let bindings = Eval.bindPattern pat (Eval.eval venv te)
        Some(bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv, bindings)
    | CExpr _
    | CCmd _
    | CNoop
    | CImport _ -> None

let rec replayModule (procFacts: (string * Eval.Value) list) (lm: LoadedModule) : Eval.Env =
    let key = realPath lm.AbsPath

    match replayed.TryGetValue key with
    | true, (body, venv) when obj.ReferenceEquals(body, lm.Body) -> venv
    | _ ->
        Session.enterImportGuard lm.NaturalName

        let venv =
            try
                replayModuleBody procFacts lm
            finally
                Session.exitImportGuard ()

        replayed[key] <- (lm.Body, venv)
        venv

and private replayModuleBody (procFacts: (string * Eval.Value) list) (lm: LoadedModule) : Eval.Env =
    let _, mBase0 = moduleBaseEnvs lm.AbsPath
    let mBase = procFacts |> List.fold (fun m (k, v) -> Map.add k v m) mBase0

    let expose (alias: string) (members: (string * Scheme) list) (from: Eval.Env) (into: Eval.Env) =
        members
        |> List.fold
            (fun acc (n, _) ->
                match Map.tryFind n from with
                | Some v -> Map.add $"{alias}.{n}" v acc
                | None -> acc)
            into

    let rec replay (mv: Eval.Env) body =
        match body with
        | [] -> mv
        | (_, CImport nested) :: t ->
            let nestedVenv = replayModule procFacts nested
            replay (expose nested.Alias nested.Members nestedVenv mv) t
        | (_, stmt) :: t ->
            match bindsOf mv stmt with
            | Some(bound, _) -> replay bound t
            | None -> replay mv t

    replay mBase lm.Body

// a session import's values [D:repl-import]: replay the module with the
// session's process facts and bind `Alias.member`, as the runner does
let importInto (venv: Eval.Env) (lm: LoadedModule) : Eval.Env =
    let procFacts =
        [ "Self.pid"; "Self.args"; "Self.stdin"; "Self.entryPath" ]
        |> List.choose (fun k -> Map.tryFind k venv |> Option.map (fun v -> k, v))

    let moduleVenv = replayModule procFacts lm

    lm.Members
    |> List.fold
        (fun acc (n, _) ->
            match Map.tryFind n moduleVenv with
            | Some v -> Map.add $"{lm.Alias}.{n}" v acc
            | None -> acc)
        venv

/// add-time validation for a single fetched module file [D:add-module]:
/// the same loader imports use — is-a-module, the module purity rules, a
/// full typecheck — with imports refused first: a single file vendors as
/// a leaf (a file with imports vendors its closure, [D:add-module-closure]).
/// Returns the member count for the add's report line.
let checkVendoredModule (absPath: string) : Result<int, string> =
    match readImportSource absPath with
    | IAbsent -> Error $"cannot read {absPath}"
    | IUnreadable reason -> Error $"cannot read {absPath}: {reason}"
    | IBadUtf8(l, c) -> Error $"{absPath}:{l}:{c}: invalid UTF-8 — a source file must be UTF-8; this byte is not"
    | ISource raws ->
        if raws |> List.exists (fun l -> l.StartsWith "import ") then
            Error
                "the module imports — a single file vendors as a leaf; vendor a module with imports by its repo path, <host>/<org>/<repo>//<file>@<ref>, so its imports are fetched with it"
        else
            let loads = ModuleLoads()
            // a phantom importer in the same directory: never equal to
            // absPath, so the self-import guard stays quiet
            let phantom = absPath + ".add"

            match loadModuleCachedWith false resolver loads [] phantom absPath None with
            | Ok lm -> Ok(List.length lm.Members)
            | Error e ->
                let where =
                    match e.File with
                    | Some f -> $"{quoteData f}:{e.Line}:{e.Col}: "
                    | None -> ""

                Error $"{where}{e.Message}"

// ---- vendoring a module's closure [D:add-module-closure] ------------------

/// the import statements of a module source, located (line, col, path),
/// read by the real assembler and parser; a source that does not decode
/// or assemble yields none here, and validation reports it located
let importsOfSource (bytes: byte[]) : (int * int * string) list =
    try
        let body, off, _ = scriptBody (decodeSourceLines bytes)
        let numbered = body |> List.mapi (fun i l -> off + i + 1, l)

        match assemble numbered with
        | Error _ -> []
        | Ok lls ->
            let r = resolver (moduleBaseTypeEnv "/vendored.weir")

            lls
            |> List.choose (fun ll ->
                if ll.Text.TrimStart().StartsWith "import" then
                    match Parser.parseLineFull r ll.Text with
                    | Ok(SImport(p, span, _)) ->
                        let l, c = translate ll span.Start.Col
                        Some(l, c, p)
                    | _ -> None
                else
                    None)
    with _ -> []

let private repoSegments (p: string) = p.Split '/' |> List.ofArray

// '.'/'..'/empty segments folded; None when `..` climbs past the root
let private normRepoPath (segs: string list) : string list option =
    (Some [], segs)
    ||> List.fold (fun acc seg ->
        match acc, seg with
        | None, _ -> None
        | Some st, ("" | ".") -> Some st
        | Some [], ".." -> None
        | Some(_ :: t), ".." -> Some t
        | Some st, s -> Some(s :: st))
    |> Option.map List.rev

/// walk a module's imports from its repo path, fetching each file at the
/// same commit: relative imports (every spelling that resolves relative
/// today) join the closure; a `weir:` import, an absolute path and a path
/// escaping the repo root refuse, located at file:line:col. A file is
/// fetched once (a cycle is the loader's error at validation). Returns
/// (repo path, bytes) in discovery order, the entry first.
let walkVendorClosure
    (fetch: string -> Result<byte[], string>)
    (entry: string)
    : Result<(string * byte[]) list, string> =
    match normRepoPath (repoSegments entry) with
    | None
    | Some [] -> Error $"'{quoteData entry}' is not a file inside the repo"
    | Some entrySegs ->
        let entryPath = String.concat "/" entrySegs
        let seen = Collections.Generic.HashSet<string>([ entryPath ])

        let rec go (acc: (string * byte[]) list) (queue: (string * string option) list) =
            match queue with
            | [] -> Ok(List.rev acc)
            | (file, from) :: rest ->
                match fetch file with
                | Error e ->
                    match from with
                    | Some at -> Error $"{quoteData file} (imported at {quoteData at}): {e}"
                    | None -> Error e
                | Ok bytes ->
                    let dirSegs = repoSegments file |> List.take (List.length (repoSegments file) - 1)

                    let rec classify (found: (string * string) list) imports =
                        match imports with
                        | [] -> Ok(List.rev found)
                        | (l, c, p: string) :: t ->
                            let at = $"{file}:{l}:{c}"

                            if p.StartsWith "weir:" then
                                Error
                                    $"{quoteData at}: imports '{quoteData p}' — a weir: import inside a vendored module is not vendored with it (cross-remote vendoring is a current boundary, not a refusal of the idea): inline the dependency, or vendor both and import each from your own code"
                            elif
                                p.StartsWith "/"
                                || p.StartsWith "\\"
                                || (p.Length >= 2 && Char.IsLetter p[0] && p[1] = ':')
                            then
                                Error
                                    $"{quoteData at}: imports the absolute path '{quoteData p}' — a vendored module reaches only files of its own repo, by relative path"
                            else
                                match normRepoPath (dirSegs @ repoSegments p) with
                                | None
                                | Some [] ->
                                    Error
                                        $"{quoteData at}: imports '{quoteData p}', which resolves outside the repo root — a vendored module reaches only files inside its repo"
                                | Some segs -> classify ((String.concat "/" segs, at) :: found) t

                    match classify [] (importsOfSource bytes) with
                    | Error e -> Error e
                    | Ok targets ->
                        let fresh = targets |> List.filter (fun (t, _) -> seen.Add t)
                        go ((file, bytes) :: acc) (rest @ (fresh |> List.map (fun (t, at) -> t, Some at)))

        go [] [ entryPath, None ]

/// the closure's layout [D:add-module-closure]: the common ancestor
/// directory of every file ("" or "a/b/"), so files keep their relative
/// positions under modules/<name>/ and imports need no rewriting
let closureAncestor (paths: string list) : string =
    let dirs =
        paths
        |> List.map (fun p ->
            let segs = repoSegments p
            List.take (List.length segs - 1) segs)

    let rec common (a: string list) (b: string list) =
        match a, b with
        | x :: xs, y :: ys when x = y -> x :: common xs ys
        | _ -> []

    match dirs with
    | [] -> ""
    | d :: ds ->
        match List.fold common d ds with
        | [] -> ""
        | segs -> String.concat "/" segs + "/"

/// add-time validation for a closure written under `dir` [D:add-module-closure]:
/// the entry loads through the normal loader, which checks every file it
/// reaches transitively with the module purity rules (a cycle is its
/// cycle error). Returns the entry's member count.
let checkVendoredClosure (dir: string) (entryRel: string) : Result<int, string> =
    let entryAbs = IO.Path.GetFullPath(IO.Path.Combine(dir, entryRel))
    let loads = ModuleLoads()

    match loadModuleCachedWith false resolver loads [] (entryAbs + ".add") entryAbs None with
    | Ok lm -> Ok(List.length lm.Members)
    | Error e ->
        let where =
            match e.File with
            | Some f -> $"{quoteData f}:{e.Line}:{e.Col}: "
            | None -> ""

        Error $"{where}{e.Message}"

// where a file's imports resolve from: its real location, through a
// symlink [D:init-import] — a linked dotfile or ~/bin script finds its
// neighbours. Self.scriptPath keeps the link [D:script-path]
let importAnchor (path: string) : string =
    let full = IO.Path.GetFullPath path

    try
        match IO.FileInfo(full).ResolveLinkTarget true with
        | null -> full
        | target -> target.FullName
    with _ ->
        full

// the DAG before the check [D:parallel-imports]: the entry's import
// lines, transitively, in the sequential loader's order (depth-first,
// lines top to bottom) — each module's first site, the chain at it, its
// imports — each handed to the loads' workers as soon as its own imports
// are known (post-order: a leaf checks while the walk goes on). A cycle
// makes no node and taints every module on the path reaching it, and so
// does a module importing a tainted one; a missing, unreadable or
// malformed import makes no node either: those modules stay with the
// loader's own path, which reports them as it always did. Sources are
// read once, for the check to take; an entry that cannot be read
// schedules nothing.
let private discoverImports (mkR: TypeEnv -> Parser.Resolver) (loads: ModuleLoads) (entryAbs: string) : unit =
    match
        (if importWorkerBound > 1 then
             (loads.Prepare entryAbs).Body
         else
             None)
    with
    | Some(_, Ok entryLines) ->
        let r = mkR (moduleBaseTypeEnv entryAbs)

        let sitesOf (lls: LogicalLine list) : (string * string option) list =
            lls
            |> List.choose (fun ll ->
                if ll.Text.TrimStart().StartsWith "import" then
                    match
                        (try
                            Some(Parser.parseLineFull r ll.Text)
                         with _ ->
                             None)
                    with
                    | Some(Ok(SImport(p, _, alias))) -> Some(p, alias |> Option.map fst)
                    | _ -> None
                else
                    None)

        let visited = Collections.Generic.HashSet<string>()
        let tainted = Collections.Generic.HashSet<string>()

        // the importer's dep keys, or None once it reaches a cycle
        let rec visit (chain: string list) (importerAbs: string) (importerKey: string) (lls: LogicalLine list) =
            let deps = ResizeArray<string>()
            let mutable taint = false

            for path, alias in sitesOf lls do
                try
                    let absPath = resolveImportPathRaw importerAbs path
                    let key = realPath absPath

                    if
                        vendoredNameFault path
                        || (vendoredFault importerAbs path).IsSome
                        || key = importerKey
                    then
                        ()
                    elif List.contains key chain then
                        chain |> List.iter (tainted.Add >> ignore)
                        taint <- true
                    elif visited.Contains key then
                        if tainted.Contains key then taint <- true else deps.Add key
                    else
                        visited.Add key |> ignore

                        match (loads.Prepare absPath).Body with
                        | Some(_, Ok mlls) ->
                            match visit (key :: chain) absPath key mlls with
                            | Some childDeps ->
                                deps.Add key

                                loads.Schedule [
                                    key,
                                    absPath,
                                    childDeps,
                                    fun () -> loadModuleCachedWith false mkR loads chain importerAbs path alias
                                ]
                            | None -> taint <- true
                        | _ -> ()
                with _ ->
                    ()

            if taint || tainted.Contains importerKey then
                tainted.Add importerKey |> ignore
                None
            else
                Some(List.ofSeq deps)

        visit [ entryAbs ] entryAbs (realPath entryAbs) entryLines |> ignore
    | _ -> ()

// a file's import loader [D:modules-v1]: imports resolve against the
// importing file; one cache per loader dedups a diamond, the chain catches
// a cycle. The REPL init file uses it too [D:init-import]
// mkR is the importer's own resolver: a check reads an imported module
// under assumeResolver exactly as it reads the importing file, the runner
// under the hard one [D:assume-resolver]
let fileImportLoader (mkR: TypeEnv -> Parser.Resolver) (importingAbsPath: string) : ImportLoader =
    let loads = ModuleLoads()
    // the DAG is discovered at the first import line, not at creation: a
    // script with no imports pays nothing [D:parallel-imports]
    let discovered = lazy (discoverImports mkR loads importingAbsPath)

    fun p _ alias ->
        discovered.Force()
        loadModuleCachedWith false mkR loads [ importingAbsPath ] importingAbsPath p alias

// the session import loader [D:repl-import]: the prompt and -e resolve an
// import against the session cwd at the moment it runs — a phantom
// importer in that directory (never a file, so the self-import guard stays
// quiet). A fresh cache per import, so a re-import re-reads the file
let sessionImportLoader: ImportLoader =
    fun p _ alias ->
        let phantom = IO.Path.Combine(Session.Cwd(), "(session)")
        loadModuleCachedWith false resolver (ModuleLoads()) [] phantom p alias

// the no-import loader: a form with no place for an import statement (an
// #infer source, a completion repair, a test harness) refuses it
let scriptOnlyImport: ImportLoader =
    fun _ _ _ ->
        Error {
            File = None
            Line = 0
            Col = 0
            Message = "import is a statement of its own — not available here"
        }
