/// Running a script: what a statement binds, the break prompt, the trace, and `run`. The other concerns that lived here sit in the files before it [D:script-by-concern].
module Weir.Script

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor
open Weir.Statements
open Weir.ModuleLoader
open Weir.Diagnostics
open Weir.Sigs
open Weir.Init
open Weir.Analyze

/// what a statement binds [D:statement-exec]: the one place a `let`, a
/// pattern, a type or an import changes the values a run holds, for every
/// front end that runs statements. `names` are the binds a `let` or a
/// pattern made, for a front end that shows them
type StatementBinds =
    | Binds of values: Eval.Env * names: (string * Eval.Value) list
    /// a command or an expression: nothing is bound, and its value is the
    /// front end's to stream, echo or drop
    | Runs of te: Check.TypedExpr * command: bool
    /// a module line or a signature: nothing to run
    | Declares

/// a checked statement as a run and a module body hold it
let checkedOf (kind: CheckedKind) : CheckedStmt =
    match kind with
    | KType(decl, validator) -> CType(decl, validator)
    | KLet(name, _, te) -> CLet(name, te)
    | KLetPat(pat, _, te) -> CLetPat(pat, te)
    | KCmd te -> CCmd te
    | KExpr te -> CExpr te
    | KImport lm -> CImport lm
    | KModule _
    | KSig _ -> CNoop

let bindChecked (venv: Eval.Env) (stmt: CheckedStmt) : StatementBinds =
    match stmt with
    | CImport lm -> Binds(importInto venv lm, [])
    | CCmd te -> Runs(te, true)
    | CExpr te -> Runs(te, false)
    | CNoop -> Declares
    | CType _
    | CLet _
    | CLetPat _ ->
        match bindsOf venv stmt with
        | Some(bound, names) -> Binds(bound, names)
        | None -> Declares

let bindStatement (venv: Eval.Env) (kind: CheckedKind) : StatementBinds = bindChecked venv (checkedOf kind)

// the prompt `Debug.break ()` opens [D:debug-aids]: one line an entry,
// checked against the scope the break stands in and run over its values;
// a name bound here lasts until the script goes on
let private breakPrompt (at: string) (tenv0: TypeEnv) (venv0: Eval.Env) : unit =
    if Console.IsInputRedirected then
        Console.Error.WriteLine $"Debug.break at {at}: skipped — stdin is not a terminal"
    else
        let savedNode = Eval.RunPos.Current
        let savedExact = Eval.RunPos.Exact
        Console.Out.Flush()

        Console.Error.WriteLine
            $"break at {at} — an empty line or #continue goes on, #vars lists the names, #quit ends the script"

        let mutable tenv = tenv0
        let mutable venv = venv0
        let mutable go = true

        while go do
            Console.Error.Write "break> "

            match Console.In.ReadLine() with
            | null ->
                Console.Error.WriteLine ""
                go <- false
            | line ->
                match line.Trim() with
                | ""
                | "#continue" -> go <- false
                | "#quit" -> raise (Eval.ExitRequest 1)
                | "#vars" ->
                    for KeyValue(n, _) in venv do
                        if not (n.StartsWith "Self.") then
                            match Map.tryFind n tenv.Values with
                            | Some sch -> Console.Error.WriteLine $"{n} : {formatEchoTy sch.Ty}"
                            | None -> Console.Error.WriteLine n
                | text ->
                    match
                        checkStatement false None resolver sessionImportLoader tenv (singleLine (stripComment text))
                    with
                    | Error d -> Console.Error.WriteLine $"error: {d.Message}"
                    | Ok chk ->
                        try
                            match bindStatement venv chk.Kind with
                            | Binds(bound, _) ->
                                venv <- bound
                                tenv <- chk.Env
                            | Runs(te, true) -> printResult (Eval.eval venv te)
                            | Runs(te, false) ->
                                let v = Eval.eval venv te

                                if v <> Eval.VUnit then
                                    let shown = Eval.echoPrep v

                                    // binary refuses the terminal here as at every echo
                                    // [D:binary-echo]
                                    if not Console.IsErrorRedirected && Eval.echoBinary Eval.echoPipedCap shown then
                                        Console.Error.WriteLine
                                            $": {formatEchoTy te.Ty} (binary output — the echo refuses a terminal; print it deliberately)"
                                    else
                                        let struct (rendered, tail) =
                                            Eval.echoShown Console.IsErrorRedirected Eval.echoPipedCap shown

                                        Console.Error.WriteLine $"{rendered} : {formatEchoTy te.Ty}{tail}"
                            | Declares -> Console.Error.WriteLine "error: a module line belongs in a file"
                        with
                        | Eval.ExitRequest _ -> reraise ()
                        | ex -> Console.Error.WriteLine $"error: {ex.Message}"

        Eval.RunPos.Current <- savedNode
        Eval.RunPos.Exact <- savedExact

/// `--trace` or WEIR_TRACE=1 [D:command-trace]: a run tells stderr each
/// command it starts
let traceCommands: bool ref =
    ref (Environment.GetEnvironmentVariable "WEIR_TRACE" = "1")

let run (path: string) (scriptArgs: string list) : int =
    // this run's own hooks and break scopes [D:run-hooks], put back at its end
    let hooks = Session.RunHooks()
    use _hooks = Session.withRunHooks hooks
    use _breaks = Check.capturingBreaks ()

    match readEntryScript path with
    | Error msg ->
        Console.Error.WriteLine $"weir: {msg}"
        2
    | Ok rawLines ->

        let body, bodyOffset, runSigDecls = scriptBody rawLines

        match directiveMisuse rawLines with
        | Some(line, msg) ->
            Console.Error.WriteLine(located path line msg)
            1
        | None ->
            // the script's own absolute path [D:script-path]: resolved
            // against the startup cwd, before any cd; symlinks stay
            // unresolved (the bash-$0 behavior)
            let absScriptPath = IO.Path.GetFullPath path

            let typeEnv0, valueEnv0 = baseEnvs scriptArgs absScriptPath

            Extern.refresh ()

            let rawByLine = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> Map.ofList

            // comment-only lines are transparent [D:comment-transparency]
            let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)
            let assembled = numbered |> assemble // raw lines: assemble classifies/strips internally

            match assembled with
            | Error msg ->
                Console.Error.WriteLine(assemblyErrorLocated path numbered msg)
                1
            | Ok logicalLines ->

                // a module file is not runnable [D:modules-v1] — the marker
                // is what makes this message possible (an empty script is a
                // different, "nothing to run", situation)
                let moduleMarker =
                    logicalLines
                    |> List.tryFind (fun ll ->
                        let t = ll.Text.TrimStart()
                        t = "module" || t.StartsWith "module ")

                // the entry's import loader, bound to its directory; one cache
                // per run dedups a diamond, one chain catches a cycle
                let entryImport: ImportLoader =
                    fileImportLoader resolver (importAnchor absScriptPath)

                // signatures load once, before the check fold; a load
                // failure (missing/malformed sig) is a check error —
                // absence is loud, never a silent fallback
                // [D:command-signatures]
                let sigLoadDiags, runSigInfos = loadSigs path runSigDecls
                let runDeclaredTypes = System.Collections.Generic.Dictionary<string, int * bool>()

                // unused bindings gate the run too [D:unused-bindings] —
                // check error = zero side effects, this check included
                let runUnused = UnusedTracker()

                // node -> physical position, for runtime errors [D:runtime-position]
                let runPositions =
                    System.Collections.Generic.Dictionary<obj, string * int * int * int>(HashIdentity.Reference)

                // where a node was written, `file:line`, or "" for one the run
                // never placed [D:debug-aids] [D:command-trace]
                let whereOf (n: obj) : string =
                    match n with
                    | null -> ""
                    | n ->
                        match runPositions.TryGetValue n with
                        | true, (file, _, l, _) ->
                            let shown =
                                if file = path then
                                    file
                                else
                                    let rel = IO.Path.GetRelativePath(Environment.CurrentDirectory, file)

                                    if rel.StartsWith ".." || IO.Path.IsPathRooted rel then
                                        file
                                    else
                                        rel

                            $"{quoteData shown}:{l}"
                        | _ -> ""

                hooks.PositionOf <- System.Func<obj, string>(whereOf)

                // an editor's debugger, when one started this run
                // [D:step-debugger]: it places a node by its full path
                hooks.PlaceOf <-
                    System.Func<obj, struct (string * int)>(fun n ->
                        match n with
                        | null -> struct ("", 0)
                        | n ->
                            match runPositions.TryGetValue n with
                            | true, (file, _, l, _) -> struct (IO.Path.GetFullPath file, l)
                            | _ -> struct ("", 0))

                Debugger.attachIfAsked hooks

                hooks.OnBreak <-
                    System.Action<int64, obj>(fun scope venv ->
                        match Check.settledBreakScope scope with
                        | Some tenv -> breakPrompt (whereOf Eval.RunPos.Current) tenv (venv :?> Eval.Env)
                        | None -> ())

                // each command before it starts, on stderr [D:command-trace]:
                // where it was written, then the words it is given
                if traceCommands.Value then
                    hooks.OnSpawn <-
                        System.Action<string, string list>(fun prog args ->
                            let at =
                                match whereOf Eval.RunPos.Current with
                                | "" -> ""
                                | w -> w + ": "

                            Console.Out.Flush()
                            Console.Error.WriteLine("+ " + at + Proc.traceArgv (prog :: args)))

                // a module's statements, with ITS file, so a function defined
                // there reports its own lines [D:runtime-position]
                let rec recordModule (lm: LoadedModule) =
                    for ll, stmt in lm.Body do
                        match stmt with
                        | CLet(_, te)
                        | CLetPat(_, te)
                        | CExpr te
                        | CCmd te -> recordPositions lm.AbsPath ll.Head ll te runPositions
                        | CImport nested -> recordModule nested
                        | _ -> ()

                // the checked kinds, for the whole-file effect-before-args
                // warning [D:effect-before-args]
                let runKinds = ResizeArray<LogicalLine * CheckedKind>()

                let checkedProgram =
                    logicalLines
                    |> List.fold
                        (fun state ll ->
                            match state with
                            | Error e -> Error e
                            | Ok(tenv, acc) ->
                                match checkStatement true None resolver entryImport tenv ll with
                                | Error d ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in d.Warnings do
                                        Console.Error.WriteLine(
                                            $"{quoteData path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    let sameFileMsg =
                                        if d.Parse then
                                            if d.HasCol then
                                                // the original source line + caret — never
                                                // the assembled text
                                                let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                                let caret = Color.red c (String(' ', max 0 (d.PhysCol - 1)) + "^")

                                                Color.bold c $"{quoteData path}:{d.PhysLine}:{d.PhysCol}"
                                                + ": "
                                                + Color.red c "parse error"
                                                + $":\n{src}\n{caret}\n{d.Message}"
                                            else
                                                located path d.PhysLine d.Message
                                        else
                                            // same source-line treatment as parse
                                            // errors, with the span underlined
                                            let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                            let width =
                                                match d.PhysEnd with
                                                | Some(el, ec) when el = d.PhysLine -> max 1 (ec - d.PhysCol)
                                                | _ -> 1

                                            let underline =
                                                Color.red c (String(' ', max 0 (d.PhysCol - 1)) + String('^', width))

                                            Color.bold c $"{quoteData path}:{d.PhysLine}:{d.PhysCol}"
                                            + ": "
                                            + Color.red c "type error"
                                            + $":\n{src}\n{underline}\n{d.Message}"

                                    // multi-file [D:modules-v1]: a module error
                                    // renders at its own file + an "imported here"
                                    // note at the import line
                                    let locatedMsg =
                                        match d.File with
                                        | Some mf ->
                                            let note =
                                                match d.Note with
                                                | Some(nl, nc, nmsg) ->
                                                    "\n"
                                                    + Color.bold c $"{quoteData path}:{nl}:{nc}"
                                                    + ": note: "
                                                    + nmsg
                                                | None -> ""

                                            Color.bold c $"{quoteData mf}:{d.PhysLine}:{d.PhysCol}"
                                            + ": "
                                            + Color.red c "error"
                                            + $": {d.Message}"
                                            + note
                                        | None -> sameFileMsg

                                    Error locatedMsg
                                | Ok chk ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in chk.Warnings do
                                        Console.Error.WriteLine(
                                            $"{quoteData path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    runKinds.Add(ll, chk.Kind)

                                    // signature warnings print, signature errors
                                    // gate exactly as schemas do
                                    // [D:command-signatures]
                                    let sigDs = sigCmdDiagnostics path runSigInfos [ (ll, chk) ]

                                    // a continued command warns and runs, as every
                                    // check warning does here [D:continued-command]
                                    for d in
                                        (sigDs |> List.filter (fun d -> d.Severity = "warning"))
                                        @ continuedCommandDiagnostics path runSigInfos [ (ll, chk) ] do
                                        Console.Error.WriteLine(
                                            $"{quoteData path}:{d.Line}:{d.Col}: "
                                            + Color.yellow c "warning"
                                            + $": {d.Message}"
                                        )

                                    // schema contracts gate the run too — check
                                    // before effects [D:yaml-schemas]
                                    match
                                        (sigDs |> List.filter (fun d -> d.Severity = "error"))
                                        @ schemaDiagnostics path [ (ll, chk) ]
                                    with
                                    | d :: _ ->
                                        let src = rawByLine |> Map.tryFind d.Line |> Option.defaultValue ""

                                        let width =
                                            match d.EndLine, d.EndCol with
                                            | Some el, Some ec when el = d.Line -> max 1 (ec - d.Col)
                                            | _ -> 1

                                        let underline =
                                            Color.red c (String(' ', max 0 (d.Col - 1)) + String('^', width))

                                        Error(
                                            Color.bold c $"{quoteData path}:{d.Line}:{d.Col}"
                                            + ": "
                                            + Color.red c "schema error"
                                            + $":\n{src}\n{underline}\n{d.Message}"
                                        )
                                    | [] ->

                                        // duplicate type declarations gate the
                                        // run too [D:dup-type-decl]
                                        let dupError =
                                            match chk.Kind with
                                            | KType(decl, _) ->
                                                match runDeclaredTypes.TryGetValue decl.Name with
                                                | true, (firstLine, viaImport) ->
                                                    Some(
                                                        located
                                                            path
                                                            ll.Head
                                                            (if viaImport then
                                                                 $"type '{decl.Name}' is already provided by the import at line {firstLine}; rename one"
                                                             else
                                                                 $"type '{decl.Name}' is already declared at line {firstLine}")
                                                    )
                                                | _ ->
                                                    runDeclaredTypes[decl.Name] <- (ll.Head, false)
                                                    None
                                            | KImport lm ->
                                                for tn in lm.TypeNames do
                                                    if not (runDeclaredTypes.ContainsKey tn) then
                                                        runDeclaredTypes[tn] <- (ll.Head, true)

                                                None
                                            | _ -> None

                                        match dupError with
                                        | Some e -> Error e
                                        | None ->
                                            runUnused.Feed ll chk

                                            // a module marker is caught before the fold, and a
                                            // script's signature never checks (scripts refuse
                                            // the form) [D:module-signatures]: neither is here
                                            let stmt = checkedOf chk.Kind

                                            // enrich a record's (or union's) Docs from
                                            // the `///` field (case) docs, so --help
                                            // reads them [D:doc-help]
                                            // [D:help-case-docs]; the Args.load arm (checked
                                            // later, the type comes first) captures it
                                            let env' =
                                                match chk.Kind with
                                                | KType(decl, _) ->
                                                    let docs =
                                                        Map.fold
                                                            (fun m k v -> Map.add k v m)
                                                            (fieldDocsFor rawLines ll)
                                                            (typeDocFor rawLines ll)

                                                    if Map.isEmpty docs then
                                                        chk.Env
                                                    else
                                                        {
                                                            chk.Env with
                                                                Types =
                                                                    chk.Env.Types
                                                                    |> Map.change
                                                                        decl.Name
                                                                        (Option.map (function
                                                                            | Record rd ->
                                                                                Record { rd with Docs = docs }
                                                                            | Union ud -> Union { ud with Docs = docs }))
                                                        }
                                                | _ -> chk.Env

                                            match stmt with
                                            | CLet(_, te)
                                            | CLetPat(_, te)
                                            | CExpr te
                                            | CCmd te -> recordPositions path ll.Head ll te runPositions
                                            | CImport lm -> recordModule lm
                                            | _ -> ()

                                            Ok(env', (ll.Head, stmt) :: acc))
                        (Ok(
                            {
                                typeEnv0 with
                                    LoadTargets = loadTargetsOf typeEnv0 logicalLines
                            },
                            []
                        ))

                match
                    (match moduleMarker with
                     | Some ll ->
                         Error(
                             located
                                 path
                                 ll.Head
                                 "a module declares; it does not run. To run a script from a script, invoke it as a command"
                         )
                     | None ->
                         // a signature that fails to load is a check
                         // error — nothing runs [D:command-signatures]
                         match sigLoadDiags with
                         | d :: _ -> Error(located path d.Line d.Message)
                         | [] -> checkedProgram)
                with
                | Error msg ->
                    Console.Error.WriteLine msg
                    1
                | Ok _ when not (runUnused.Flush Set.empty false |> List.isEmpty) ->
                    // the unused-binding check [D:unused-bindings]: judged after
                    // the whole-file fold, before any line runs
                    let c = Color.onStderr.Value

                    for f in runUnused.Flush Set.empty false do
                        let src = rawByLine |> Map.tryFind f.ULine |> Option.defaultValue ""

                        let underline =
                            Color.red c (String(' ', max 0 (f.UCol - 1)) + String('^', max 1 (f.UEndCol - f.UCol)))

                        Console.Error.WriteLine(
                            Color.bold c $"{quoteData path}:{f.ULine}:{f.UCol}"
                            + ": "
                            + Color.red c "check error"
                            + $":\n{src}\n{underline}\n{f.UMessage}"
                        )

                    1
                | Ok _ when not (docMisalignments path rawLines).IsEmpty ->
                    // a check error means nothing runs [D:check-error-no-run],
                    // the doc-alignment lint included
                    let c = Color.onStderr.Value

                    for d in docMisalignments path rawLines do
                        let src = rawByLine |> Map.tryFind d.Line |> Option.defaultValue ""
                        let caret = Color.red c (String(' ', max 0 (d.Col - 1)) + "^")

                        Console.Error.WriteLine(
                            Color.bold c $"{quoteData path}:{d.Line}:{d.Col}"
                            + ": "
                            + Color.red c "check error"
                            + $":\n{src}\n{caret}\n{d.Message}"
                        )

                    1
                | Ok(_, revStmts) ->
                    let stmts = List.rev revStmts

                    // advisory, before any line runs [D:effect-before-args]
                    (let c = Color.onStderr.Value

                     for d in effectBeforeArgs path (List.ofSeq runKinds) do
                         Console.Error.WriteLine(
                             $"{quoteData path}:{d.Line}:{d.Col}: "
                             + Color.yellow c "warning"
                             + $": {d.Message}"
                         ))

                    // a runtime error's position [D:runtime-position]: the
                    // blamed node (a command at its own column, any other
                    // node at its line's first non-blank column), then one
                    // `called from` line per call of a function defined
                    // elsewhere, innermost first; the statement's line when
                    // nothing is known
                    let sourceLines = System.Collections.Generic.Dictionary<string, string[]>()

                    let firstCol (file: string) (l: int) =
                        let text =
                            if file = path then
                                Map.tryFind l rawByLine
                            else
                                let lines =
                                    match sourceLines.TryGetValue file with
                                    | true, ls -> ls
                                    | _ ->
                                        let ls =
                                            (try
                                                IO.File.ReadAllLines file
                                             with _ -> [||])

                                        sourceLines[file] <- ls
                                        ls

                                if l >= 1 && l <= lines.Length then
                                    Some lines[l - 1]
                                else
                                    None

                        match text with
                        | Some t -> t.Length - t.TrimStart().Length + 1
                        | None -> 1

                    // the entry as the user wrote it; a module relative to the
                    // working directory when it is below it
                    // a file name is data [D:error-data-escape]
                    let shownFile (file: string) =
                        quoteData (
                            if file = path then
                                file
                            else
                                let rel = IO.Path.GetRelativePath(Environment.CurrentDirectory, file)

                                if rel.StartsWith ".." || IO.Path.IsPathRooted rel then
                                    file
                                else
                                    rel
                        )

                    let posOf (exact: bool) (n: obj) =
                        if isNull n then
                            None
                        else
                            match runPositions.TryGetValue n with
                            | true, (file, _, l, c) -> Some(shownFile file, l, (if exact then c else firstCol file l))
                            | _ -> None

                    let runtimeErrorAt (lineNo: int) (ex: exn) : string =
                        let origin, frames =
                            match Eval.blameOf ex with
                            | Some b -> posOf b.Exact b.Node, b.Frames |> Seq.choose (posOf false) |> List.ofSeq
                            | None -> posOf Eval.RunPos.Exact Eval.RunPos.Current, []

                        // an unmapped origin falls back to the innermost frame
                        let origin, frames =
                            match origin, frames with
                            | Some o, fs -> Some o, fs
                            | None, f :: fs -> Some f, fs
                            | None, [] -> None, []

                        match origin with
                        | Some(file, l, c) ->
                            let safe = Eval.sanitizeIfTty Console.IsErrorRedirected ex.Message

                            let head =
                                $"{file}:{l}:{c}: " + Color.red Color.onStderr.Value "error" + $": {safe}"

                            let shown = frames |> List.truncate 10

                            let more =
                                if frames.Length > shown.Length then
                                    [ $"  … {frames.Length - shown.Length} more" ]
                                else
                                    []

                            head :: (shown |> List.map (fun (f, fl, fc) -> $"  called from {f}:{fl}:{fc}"))
                            @ more
                            |> String.concat Environment.NewLine
                        | None -> runtimeErrorLine path lineNo ex.Message

                    // statements are width, not depth: a loop, each step in
                    // its own try [D:block-width]
                    let fail (lineNo: int) (ex: exn) : Result<Eval.Env, int> =
                        match ex with
                        | Eval.ExitRequest code -> Error code
                        // the script user's usage error: no weir location,
                        // exit 2 [D:args-usage-error]
                        | :? Eval.ArgsUsageError ->
                            Console.Error.WriteLine(Eval.sanitizeIfTty Console.IsErrorRedirected ex.Message)
                            Error 2
                        | ex ->
                            // a command death the signal owns ends silently
                            // [D:settle-pending]
                            match Proc.fatalTeardown ex with
                            | Some code -> Error code
                            | None ->
                                Console.Error.WriteLine(runtimeErrorAt lineNo ex)
                                Error 1

                    let step (venv: Eval.Env) (lineNo: int) (stmt: CheckedStmt) : Result<Eval.Env, int> =
                        // each top-level statement starts at its own root; a
                        // closure from this statement is not a call
                        Eval.RunPos.SameStatement <-
                            System.Func<obj, bool>(fun body ->
                                match runPositions.TryGetValue body with
                                | true, (f, s, _, _) -> f = path && s = lineNo
                                | _ -> false)

                        match stmt with
                        | CLet(_, te)
                        | CLetPat(_, te)
                        | CExpr te
                        | CCmd te -> Eval.RunPos.Current <- box te
                        // an import's module code has no positions here:
                        // the statement's own line, never a stale node
                        | _ -> Eval.RunPos.Current <- null

                        Eval.RunPos.Exact <- false

                        match stmt with
                        | CNoop -> Ok venv
                        // an import replays the module (its Body, including any
                        // nested imports) and exposes its members as
                        // `alias.member` [D:modules-v1]; the process facts
                        // ride from the entry and the module keeps its own
                        // scriptPath. Every bind is the shared step's
                        // [D:statement-exec]
                        | CImport _
                        | CType _
                        | CLetPat _
                        | CLet _ ->
                            try
                                match bindChecked venv stmt with
                                | Binds(bound, _) -> Ok bound
                                | Runs _
                                | Declares -> Ok venv
                            with ex ->
                                fail lineNo ex
                        | CCmd te ->
                            try
                                // the bare command statement at a tty
                                // inherits stdout [D:colour-inherit]: the
                                // child sees the terminal (isatty true,
                                // colour on) and weir never holds the
                                // bytes; redirected output keeps the
                                // batched path — byte-identical, decided
                                // at spawn
                                (match te.Kind with
                                 | _ when Eval.inheritsStdout te ->
                                     // no mid-line tidy: a DSR query
                                     // hangs under a non-answering
                                     // terminal — bash's posture, wart
                                     // and all
                                     Console.Out.Flush()
                                     Eval.inheritCommandStatement venv te
                                 | _ -> printResult (Eval.eval venv te))

                                Ok venv
                            with ex ->
                                fail lineNo ex
                        | CExpr te ->
                            try
                                Eval.eval venv te |> ignore
                                Ok venv
                            with ex ->
                                fail lineNo ex

                    let mutable venv = valueEnv0
                    let mutable rest = stmts
                    let mutable code = None

                    while code.IsNone && not (List.isEmpty rest) do
                        let (lineNo, stmt) = List.head rest
                        rest <- List.tail rest

                        match step venv lineNo stmt with
                        | Ok venv' -> venv <- venv'
                        | Error c -> code <- Some c

                    defaultArg code 0
