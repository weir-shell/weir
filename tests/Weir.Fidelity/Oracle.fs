module Oracle

// The F# oracle: dotnet/fsharp (via FSharp.Compiler.Service) referees weir's
// fidelity claims mechanically. SHAPES only — accept/reject at parse+check on
// small self-contained script snippets; semantics, inference internals, and
// error text are out of scope permanently (the languages differ by design).
// Test-side dependency ONLY: FCS never approaches the shipping binary.

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open Weir.Types

type Verdict =
    | Accept
    | Reject

let private checker = FSharpChecker.Create()

// Snippets carry no #r, so one manually-built options set serves them all.
// Script-mode resolution (GetProjectOptionsFromScript) is NOT used: its
// legacy-fsi default references are broken in sandboxed containers (a
// WebClient reference error poisoned every verdict). The runtime's own
// trusted-assembly list is the complete, correct reference set.
let private scriptFile = "/oracle/Snippet.fs"

let private baseOptions =
    lazy
        (let tpa = System.AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" :?> string

         let refs =
             tpa.Split System.IO.Path.PathSeparator
             |> Array.filter (fun p -> p.EndsWith ".dll")
             |> Array.map (fun p -> "-r:" + p)

         { ProjectFileName = "/oracle/oracle.fsproj"
           ProjectId = None
           SourceFiles = [| scriptFile |]
           OtherOptions = Array.append [| "--noframework"; "--targetprofile:netcore"; "--langversion:latest" |] refs
           ReferencedProjects = [||]
           IsIncompleteTypeCheckEnvironment = false
           UseScriptResolutionRules = false
           LoadTime = System.DateTime.MinValue
           UnresolvedReferences = None
           OriginalLoadReferences = []
           Stamp = None })

let private cache = ConcurrentDictionary<string, Verdict>()

// FCS is not safe under concurrent checks of one virtual filename (Expecto
// runs tests in parallel); serialize the oracle — it is fast enough.
let private gate = obj ()

let fsharpVerdict (src: string) : Verdict =
    cache.GetOrAdd(
        src,
        fun _ ->
            lock gate (fun () ->
                let sourceText = SourceText.ofString src

                let parseRes, checkAnswer =
                    checker.ParseAndCheckFileInProject(scriptFile, src.GetHashCode(), sourceText, baseOptions.Value)
                    |> Async.RunSynchronously

                let parseErrors =
                    parseRes.Diagnostics
                    |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

                let checkErrors =
                    match checkAnswer with
                    | FSharpCheckFileAnswer.Succeeded res ->
                        res.Diagnostics
                        |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                    | FSharpCheckFileAnswer.Aborted -> true

                if System.Environment.GetEnvironmentVariable "WEIR_ORACLE_DEBUG" <> null then
                    let all =
                        match checkAnswer with
                        | FSharpCheckFileAnswer.Succeeded res -> Array.append parseRes.Diagnostics res.Diagnostics
                        | FSharpCheckFileAnswer.Aborted -> parseRes.Diagnostics

                    let dump =
                        all
                        |> Array.map (fun d -> $"[oracle] {d.Severity} {d.Message}")
                        |> String.concat "\n"

                    System.IO.File.AppendAllText("/tmp/oracle-debug.log", $"--- snippet:\n{src}\n{dump}\n")

                if parseErrors || checkErrors then Reject else Accept)
    )

// Weir's verdict replicates the script runner's check phase exactly:
// assemble, parse strict, typecheck with env threading, statement rule —
// but no external resolution (fidelity shapes are pure grammar) and no eval.
let weirVerdict (src: string) : Verdict =
    let lines =
        src.Replace("\r\n", "\n").Split('\n')
        |> Array.toList
        |> List.mapi (fun i l -> i + 1, Weir.Script.stripComment l)

    match Weir.Script.assemble lines with
    | Error _ -> Reject
    | Ok logicalLines ->
        let typeEnv0, _ =
            Weir.Prelude.extend Weir.Builtins.typeEnvStrict Weir.Builtins.valueEnv

        let typeEnv0 =
            { typeEnv0 with
                Values =
                    typeEnv0.Values
                    |> Map.add "args" (generalize (TSeq TStr))
                    |> Map.add "stdin" (generalize (TSeq TStr)) }

        let resolver: Weir.Parser.Resolver =
            { IsKnown = fun n -> Map.containsKey n typeEnv0.Values || Map.containsKey n typeEnv0.Modules
              IsCommandCallable = Weir.Builtins.commandCallable.Contains
              IsExternal = fun _ -> false
              ExternalNames = fun () -> Seq.empty }

        let step env (ll: Weir.Script.LogicalLine) =
            match env with
            | Error() -> Error()
            | Ok tenv ->
                match Weir.Parser.parseLine resolver ll.Text with
                | Error _ -> Error()
                | Ok(Weir.Ast.SType decl) ->
                    match Weir.Check.checkDecl tenv decl with
                    | Error _ -> Error()
                    | Ok tenv' -> Ok tenv'
                | Ok(Weir.Ast.SLet(name, e)) ->
                    match Weir.Check.typecheck tenv e with
                    | Error _ -> Error()
                    | Ok te ->
                        Ok
                            { tenv with
                                Values = Map.add name (generalize te.Ty) tenv.Values }
                | Ok(Weir.Ast.SCmd e) ->
                    match Weir.Check.typecheck tenv e with
                    | Error _ -> Error()
                    | Ok _ -> Ok tenv
                | Ok(Weir.Ast.SExpr e) ->
                    match Weir.Check.typecheck tenv e with
                    | Error _ -> Error()
                    | Ok te ->
                        match Weir.Script.discardError te.Ty with
                        | Some _ -> Error()
                        | None -> Ok tenv

        match logicalLines |> List.fold step (Ok typeEnv0) with
        | Ok _ -> Accept
        | Error() -> Reject

// The named-divergence artifact: ids parsed from the markdown table.
let divergenceIds: Set<string> =
    let path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "divergences.md")

    System.IO.File.ReadAllLines path
    |> Array.choose (fun line ->
        let t = line.Trim()

        if t.StartsWith "| " && not (t.StartsWith "| id") && not (t.StartsWith "|--") then
            match t.Split('|') |> Array.map (fun c -> c.Trim()) with
            | cols when cols.Length > 2 && cols[1] <> "" && not (cols[1].StartsWith "-") -> Some cols[1]
            | _ -> None
        else
            None)
    |> Set.ofArray
