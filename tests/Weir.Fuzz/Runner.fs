module Fuzz.Runner

// Executes generated programs against the AOT binary. The stamp gate is
// the standing mechanism [D:masking-mechanized]: assert binary stamp ==
// HEAD (plus source-mtime freshness) ONCE before any run — a fuzzer
// reporting against a stale binary is a masked-failure factory.

open System
open System.Diagnostics
open System.IO

let binPath =
    Environment.GetEnvironmentVariable "WEIR_BIN"
    |> Option.ofObj
    |> Option.defaultValue (
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "bin", "weir")
    )

let private capture (prog: string) (args: string list) (timeoutMs: int) =
    use p = new Process()
    p.StartInfo.FileName <- prog

    for a in args do
        p.StartInfo.ArgumentList.Add a

    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError <- true
    p.StartInfo.UseShellExecute <- false
    p.Start() |> ignore
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()

    if p.WaitForExit timeoutMs then
        {| Rc = p.ExitCode
           Out = stdout.Result
           Err = stderr.Result
           TimedOut = false |}
    else
        try
            p.Kill true
        with _ ->
            ()

        {| Rc = -1
           Out = ""
           Err = ""
           TimedOut = true |}

let private git (args: string list) =
    try
        let r = capture "git" args 10000
        if r.Rc = 0 then Some(r.Out.Trim()) else None
    with _ ->
        None

// walk up from the test binary to the repo root (bin/Debug/net10.0 -> project -> tests -> root)
let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            None
        elif File.Exists(Path.Combine(d.FullName, "weir.slnx")) then
            Some d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let stampGate =
    lazy
        (if not (File.Exists binPath) then
             failwith $"no weir binary at {binPath} — build with ./publish.sh (or set WEIR_BIN)"

         match
             repoRoot
             |> Option.bind (fun root -> git [ "-C"; root; "rev-parse"; "--short"; "HEAD" ])
         with
         | None -> () // no git — the e2e gate skips too
         | Some head ->
             let stamp = (capture binPath [ "--version" ] 10000).Out.Trim()
             // the stamp is <tag>+<hash>; the hash is the part after the
             // last '+' (a bare stamp with no '+' passes through whole)
             let hash =
                 match stamp.LastIndexOf '+' with
                 | -1 -> stamp
                 | i -> stamp.Substring(i + 1)

             if not (hash.StartsWith head) then
                 failwith
                     $"STALE BINARY: {binPath} stamps '{stamp}' (hash '{hash}'), HEAD is '{head}' — rebuild with ./publish.sh"

         match repoRoot with
         | Some root ->
             let binTime = File.GetLastWriteTimeUtc binPath

             let newer =
                 Directory.EnumerateFiles(Path.Combine(root, "src", "Weir"), "*.fs", SearchOption.TopDirectoryOnly)
                 |> Seq.tryFind (fun f -> File.GetLastWriteTimeUtc f > binTime)

             match newer with
             | Some f -> failwith $"STALE BINARY: {binPath} is older than {f} — rebuild with ./publish.sh"
             | None -> ()
         | None -> ())

type RunResult =
    { Rc: int
      Out: string
      Err: string
      TimedOut: bool }

let runProgram (lines: string list) : RunResult =
    stampGate.Force()
    let file = Path.Combine(Path.GetTempPath(), $"fuzz-{Guid.NewGuid():N}.weir")
    File.WriteAllLines(file, lines)

    try
        let r = capture binPath [ file ] 15000

        { Rc = r.Rc
          Out = r.Out
          Err = r.Err
          TimedOut = r.TimedOut }
    finally
        try
            File.Delete file
        with _ ->
            ()
