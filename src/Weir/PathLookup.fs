/// Finding a program's file on a PATH [D:one-path-walk]: the one walk every
/// lookup makes — per directory, the name as given first, then each
/// PATHEXT extension — under the rule its caller holds. A script's command
/// is found by the shell's rule; a spawn weir makes on its own takes rooted
/// entries and runnable files only [D:spawn-path-only]. The rules differ on
/// purpose; the walk is one.
module Weir.PathLookup

open System
open System.IO

/// which entries of a PATH count, and which files in them
type Rule = {
    /// how a candidate is anchored: a relative entry reads from where the
    /// child starts. None skips every entry that is not rooted
    Anchor: (string -> string) option
    /// the file must carry an execute bit (POSIX; Windows has none)
    Executable: bool
}

/// the extensions a bare name answers to: Windows resolves `git` to
/// `git.exe` via PATHEXT, the name as given always first; POSIX has none
/// [D:windows-v1]
let extsOf (value: string option) : string list =
    if OperatingSystem.IsWindows() then
        (value |> Option.defaultValue ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
    else
        []

let dirsOf (value: string) : string[] =
    value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)

let private counts (rule: Rule) (path: string) : bool =
    File.Exists path
    && (not rule.Executable
        || OperatingSystem.IsWindows()
        || (File.GetUnixFileMode path
            &&& (UnixFileMode.UserExecute
                 ||| UnixFileMode.GroupExecute
                 ||| UnixFileMode.OtherExecute))
           <> enum 0)

/// a file by its own name, then under each extension
let fileOrExt (rule: Rule) (exts: string list) (path: string) : string option =
    if counts rule path then
        Some path
    else
        exts
        |> List.tryPick (fun e -> if counts rule (path + e) then Some(path + e) else None)

/// a bare program's file in the first directory that holds it
let find (rule: Rule) (exts: string list) (dirs: string seq) (prog: string) : string option =
    dirs
    |> Seq.tryPick (fun dir ->
        match rule.Anchor with
        | None when not (Path.IsPathFullyQualified dir) -> None
        | None -> fileOrExt rule exts (Path.Combine(dir, prog))
        | Some anchor -> fileOrExt rule exts (anchor (Path.Combine(dir, prog))))
