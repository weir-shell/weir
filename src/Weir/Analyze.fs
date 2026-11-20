/// Whole-file analysis: `weir check`'s driver and its diagnostics. One of the files Script.fs was [D:script-by-concern].
module Weir.Analyze

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

// ---- effects before the CLI parse [D:effect-before-args] ------------------
// A top-level statement BEFORE the entry script's first top-level
// `Args.load`/`Env.load` that writes the filesystem, spawns or touches the
// network still runs on `--help` or a bad argument. Advisory (a warning).
// The classification is Effects.effectClass's mutation side — console,
// exit and cd excluded — plus the network members; a function body is a
// definition (it does not run here), and the statement holding the load
// is never judged (a `within` wrapping the load is deliberate). A load
// only inside a function or a branch is a runtime fact: no judgement.

/// the effect a statement runs, as the message names it, and its span
let private firstRunEffect (isLetRhs: bool) (te: Check.TypedExpr) : (string * Span) option =
    let quiet =
        Set [ "print"; "printerr"; "Self.prompt"; "Term.title"; "exit"; "cd"; "|print" ]

    let memberEffect (n: string) =
        match n with
        | "Http.send"
        | "Http.expect"
        | "Net.portOpen" -> Some $"'{n}'"
        | _ when quiet.Contains n || (Modules.rowFor n |> Option.exists (fun r -> r.Quiet)) -> None
        | _ when Effects.isCommandReifier n -> None // the command is named at its program slot
        | _ ->
            match Effects.effectClass n with
            | Some Effects.Mutation -> Some $"'{n}'"
            | _ -> None

    let rec headOf (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEApp(g, _) -> headOf g
        | k -> k

    let rec argsOf (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEApp(g, a) -> argsOf g @ [ a ]
        | _ -> []

    let rec go (top: bool) (e: Check.TypedExpr) : (string * Span) option =
        match e.Kind with
        // a binding's lazy command spawns nothing until read
        // [D:lazy-command-lets]
        | Check.TECmd _ when top && isLetRhs -> None
        | Check.TECmd(Check.THeadLit prog, _, _) -> Some($"the command '{prog}'", e.Span)
        | Check.TECmd(Check.THeadDyn(d, _), _, _) -> Some($"the command '^{d}'", e.Span)
        // a reified command: the program rides the desugar's arguments
        | Check.TEApp _ when
            (match headOf e with
             | Check.TEVar h -> Effects.isCommandReifier h && h <> "|print"
             | _ -> false)
            ->
            // the program's slot in the desugar, as --can reads it
            let h =
                match headOf e with
                | Check.TEVar h -> h
                | _ -> ""

            let progArg =
                if h.StartsWith "|chain" then
                    match argsOf e |> List.tryItem (if h = "|chainOrFailed" then 1 else 0) with
                    | Some {
                               Kind = Check.TEList({ Kind = Check.TETuple(p :: _) } :: _)
                           } -> Some p
                    | _ -> None
                else
                    let slot =
                        if h.StartsWith "|orFailedEnv" then 2
                        elif h.StartsWith "|orFailed" then 1
                        elif h.EndsWith "Env" then 1
                        else 0

                    argsOf e |> List.tryItem slot

            let prog =
                progArg
                |> Option.bind (fun a ->
                    match a.Kind with
                    | Check.TEStr s -> Some(s, a.Span)
                    | _ -> None)

            match prog with
            | Some(p, span) -> Some($"the command '{p}'", span)
            | None -> Some("a command", e.Span)
        | Check.TEVar n -> memberEffect n |> Option.map (fun m -> m, e.Span)
        | Check.TELambda _
        | Check.TELambdaPat _ -> None
        | Check.TEWithin(kind, _, _, _, body) ->
            match kind with
            | WithinTmp -> Some("'within tmp'", e.Span)
            | WithinLock -> Some("'within lock'", e.Span)
            | WithinProc -> Some("'within proc'", e.Span)
            | WithinServe -> Some("'within serve'", e.Span)
            // a plan captures its mutations instead of running them
            | WithinPlan -> None
            | WithinCd
            | WithinEnv
            | WithinPure
            | WithinReadonly -> go false body
        | _ -> Check.tryPickChild (go false) e

    go true te

/// does a top-level statement run a typed boundary load unconditionally
/// (outside a function body or a branch)? Its message name if so.
let private topLevelLoad (te: Check.TypedExpr) : (string * Span) option =
    let named (prefix: string) (name: string) =
        if name.StartsWith "{" then prefix else $"{prefix} {name}"

    let rec go (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEArgsLoad target ->
            let name =
                match target with
                | Check.ArgsRecord d -> d.Name
                | Check.ArgsUnion(u, _) -> u.Name
                | Check.ArgsShared(o, _, _, _) -> o.Name

            Some(named "Args.load" name, e.Span)
        | Check.TEEnvLoad(def, _) -> Some(named "Env.load" def.Name, e.Span)
        | Check.TELambda _
        | Check.TELambdaPat _ -> None
        | Check.TEIf(cond, _, _) -> go cond
        | Check.TEMatch(scrut, _) -> go scrut
        | _ -> Check.tryPickChild go e

    go te

/// the warnings over a script's checked statements, in file order
let effectBeforeArgs (path: string) (stmts: (LogicalLine * CheckedKind) list) : Diagnostic list =
    let teOf (k: CheckedKind) =
        match k with
        | KLet(_, _, te) -> Some(true, te)
        | KLetPat(_, _, te) -> Some(true, te)
        | KCmd te
        | KExpr te -> Some(false, te)
        | _ -> None

    let indexed = stmts |> List.indexed

    let load =
        indexed
        |> List.tryPick (fun (i, (ll, k)) ->
            teOf k
            |> Option.bind (fun (_, te) -> topLevelLoad te)
            |> Option.map (fun (name, span) -> i, name, fst (translate ll span.Start.Col)))

    match load with
    | None -> []
    | Some(at, loadName, loadLine) ->
        let consequence =
            if loadName.StartsWith "Env.load" then
                "on a missing or bad variable it has already run; load the environment first"
            else
                "on --help or a bad argument it still runs; load the arguments first"

        indexed
        |> List.takeWhile (fun (i, _) -> i < at)
        |> List.choose (fun (_, (ll, k)) ->
            teOf k
            |> Option.bind (fun (isLet, te) -> firstRunEffect isLet te)
            |> Option.map (fun (what, span) ->
                let l, c = translate ll span.Start.Col
                let el, ec = translate ll span.End.Col

                {
                    File = path
                    Line = l
                    Col = c
                    EndLine = Some el
                    EndCol = Some(if el = l then max (c + 1) ec else ec)
                    Severity = "warning"
                    Code = "effect-before-args"
                    Related = []
                    Fix = None
                    Message = $"{what} runs before '{loadName}' (line {loadLine}) — {consequence}"
                }))

// the recovery pass [D:block-recovery], tooling-only: an errored
// statement is re-checked once for its tree, so the editor keeps
// answering inside it. The diagnostic stays the first pass's; the
// binder schemes are the hole schemes the Error branch binds, so the
// partial's kind and its env agree
let private recoverStatement (tenv: TypeEnv) (holeScheme: Scheme) (stmt: Stmt) : CheckedKind option * bool =
    let recovered (e: Expr) (wrap: Check.TypedExpr -> CheckedKind) =
        match Check.typecheckRecovering tenv e with
        | Check.RecoveredExpr(te, _) -> Some(wrap te), false
        | Check.NoRecovery -> None, false
        | Check.RecoveryBudgetExhausted -> None, true

    match stmt with
    | SLet(name, e) -> recovered e (fun te -> KLet(name, holeScheme, te))
    | SLetPat(pat, e) ->
        recovered e (fun te -> KLetPat(pat, Check.patNameSpans pat |> List.map (fun (n, _) -> n, holeScheme), te))
    | SCmd e -> recovered e KCmd
    // statement position demands unit — checkStatementCore's arming
    | SExpr e -> recovered (Check.armTail e) KExpr
    | SType _
    | SSig _
    | SModule _
    | SImport _ -> None, false

/// a `#` line past the directive head [D:directive-diagnostic]: the one
/// pass for `check` and `run`, so both name the same mistake the same way.
/// Column-0 only: a directive is a statement-position thing. Indented `#`
/// lines are continuations — inside a yaml district they are content
/// (`#!/bin/sh` in a block scalar, `#` comments) [D:block-scalars];
/// anywhere else they fail the parse. Returns (physical line, message)
/// per stray line; `run` stops at the first, `check` reports them all.
let directiveMisuses (rawLines: string list) : (int * string) list =
    let body, bodyOffset, _ = scriptBody rawLines

    body
    |> List.mapi (fun i l -> i, l.TrimEnd())
    |> List.filter (fun (_, l) -> l.StartsWith "#")
    |> List.map (fun (i, l) ->
        let word = l.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries).[0]

        let msg =
            if word = "#init" then
                "an init file is not runnable — the REPL loads it; author it anywhere with #init on the first line and symlink it to the config dir (weir/init.weir)"
            elif word = "#session" then
                "#session lives in the REPL init file (config dir, weir/init.weir) — a script takes its settings from flags and env"
            elif word = "#alias" then
                "#alias lives in the REPL init file (config dir, weir/init.weir) and at the REPL prompt — a script names its programs directly"
            elif word = "#sig" then
                "misplaced directive: #sig — #sig lines belong at the file head, before any code, comment or blank line"
            elif word.StartsWith "#!" then
                "a shebang (#!) is only the first line of the file"
            else
                $"unknown directive: {word} — a script's only directive is #sig (the REPL init file takes #init, #session and #alias)"

        bodyOffset + i + 1, msg)

/// analyzeLines, with `asInit` forcing the init-file reading (the LSP's
/// canonical init path, marked or not [D:init-marker]). The fifth
/// product is the partials [D:block-recovery]: statements that did NOT
/// check, with their recovered trees — never among `stmts`, so every
/// diagnostic consumer sees exactly the checked ones
let private analyzeLinesBare
    (asInit: bool)
    (path: string)
    (rawLines: string list)
    : Diagnostic list *
      (LogicalLine * CheckedStatement) list *
      TypeEnv *
      LogicalLine list *
      (LogicalLine * CheckedStatement) list
    =
    // an #init-marked file checks as the init it is [D:init-marker]:
    // directives rewritten for the full-file pass (hover, completion,
    // statement errors), its declarations are session exports — the
    // unused-binding judgement does not apply (the loader never judges
    // it) — and checkInit adds the loader's own rules
    let initMarked = asInit || isInitMarked rawLines

    // the #session lines are checkInit's: the full-file pass checks their
    // rewritten lets in file order, which is not the loader's (settings
    // before names, prompt after the declarations) — its errors there give
    // way
    let withInitRules (ds: Diagnostic list) : Diagnostic list =
        if initMarked then
            let ic = checkInit assumeResolver path rawLines

            (ds
             |> List.filter (fun d ->
                 not (d.Severity = "error" && d.File = path && Set.contains d.Line ic.Split.BlockLines)))
            @ ic.Diags
        else
            ds

    let rawLines =
        if initMarked then
            stripInitDirectives rawLines
        else
            rawLines

    let rawArr = List.toArray rawLines
    let body, bodyOffset, sigDecls = scriptBody rawLines
    let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)

    let typeEnv0 = Prelude.extendTypes Builtins.typeEnvStrict

    let typeEnv0 = {
        typeEnv0 with
            Modules = typeEnv0.Modules |> Map.add "Self" selfMembers
    }

    // imports resolve relative to the file being checked [D:modules-v1];
    // one cache per check dedups a diamond, one chain catches a cycle
    let analyzeImport: ImportLoader =
        fileImportLoader assumeResolver (importAnchor path)

    Extern.refresh ()

    // Check-only consumers assume unknown heads are commands, so a
    // script for uninstalled tools still parses; each head missing
    // from PATH becomes a warning (cmd-not-found). The runner keeps
    // hard resolution — same pipeline, explicitly different resolver
    // input (the gateExprs pattern) [D:assume-resolver].

    // Assembly recovery [D:assembly-recovery]: drop the line the
    // error names and retry, keeping each drop as a diagnostic. The
    // runner keeps hard assembly failure; tooling-only.
    let assemblyDiags = ResizeArray<Diagnostic>()

    let physicalIndent =
        numbered
        |> List.map (fun (n, raw) -> n, raw |> Seq.takeWhile ((=) ' ') |> Seq.length)
        |> Map.ofList

    // the lines a recovery attempt re-indented, at their source indent
    let reindented = ref Map.empty

    let rec assembleRecovering (attempts: int) (input: (int * string) list) =
        let misaligned = ref None

        match assembleWith (fun l c -> misaligned.Value <- Some(l, c)) input with
        | Ok lls -> lls
        | Error msg when attempts > 0 ->
            let line, message =
                match assemblyErrorAt input msg with
                | Some(l, _, rest) -> l, rest
                | None -> -1, msg

            // the named line's content start is the column — in the
            // source, not in an earlier attempt's re-indent
            // [D:recovery-physical-indent]
            let col =
                physicalIndent.TryFind line
                |> Option.map (fun i -> i + 1)
                |> Option.defaultValue 1

            assemblyDiags.Add {
                File = path
                Line = (if line > 0 then line else 1)
                Col = col
                EndLine = None
                EndCol = None
                Severity = "error"
                Code = "layout"
                Related = []
                Fix = None
                Message = message
            }

            match misaligned.Value with
            | Some(l, c) when l = line ->
                // re-indent onto the siblings' column: the line's closers
                // still close, so the statement checks on
                let fix (n, raw: string) =
                    if n = line then
                        n, String(' ', c) + raw.TrimStart(' ')
                    else
                        n, raw

                reindented.Value <- reindented.Value |> Map.add line physicalIndent[line]

                assembleRecovering (attempts - 1) (input |> List.map fix)
            | _ when line > 0 && input |> List.exists (fun (n, _) -> n = line) ->
                assembleRecovering (attempts - 1) (input |> List.filter (fun (n, _) -> n <> line))
            | _ -> []
        | Error _ -> []

    let logicalLines =
        numbered
        |> assembleRecovering 10 // raw lines: assemble classifies/strips internally
        |> List.map (withPhysicalIndents reindented.Value)

    (let diags0 = List.ofSeq assemblyDiags
     diags0 |> ignore)

    match Some logicalLines with
    | None -> [], [], typeEnv0, [], []
    | Some logicalLines ->
        let diags = ResizeArray<Diagnostic>()
        // a shared module's own error is reported once [D:module-error-once]:
        // a module that fails is re-checked at every import site (only a
        // loaded module is cached), so its error arrives once per site —
        // and under each spelling of its path (a symlink); identity is the
        // real path, the first spelling seen is the one printed
        let seenModuleErrors =
            System.Collections.Generic.HashSet<string * int * int * string>()

        let stmts = ResizeArray<LogicalLine * CheckedStatement>()
        let partials = ResizeArray<LogicalLine * CheckedStatement>()
        // first declaration per type name: line * came-from-import
        // [D:dup-type-decl]
        let declaredTypes = System.Collections.Generic.Dictionary<string, int * bool>()

        let warn (wl, wc, wm) =
            diags.Add {
                File = path
                Line = wl
                Col = wc
                EndLine = None
                EndCol = None
                Severity = "warning"
                Code = "warning"
                Related = []
                Fix = None
                Message = wm
            }

        let mutable tenv = {
            typeEnv0 with
                LoadTargets = loadTargetsOf typeEnv0 logicalLines
        }

        // a module file [D:modules-v1] is checkable in isolation: direct
        // `weir check` enforces the same decl-only + weak-purity rules an
        // import would (decision 10)
        let isModule =
            match logicalLines with
            | first :: _ ->
                let t = first.Text.TrimStart()
                t = "module" || t.StartsWith "module "
            | [] -> false

        let rec cmdHeads (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TECmd(Check.THeadLit prog, _, _) when not (Extern.exists prog) -> [ prog, te.Span ]
             // a typed home-path head [D:tilde]: this machine's home
             | Check.TECmd(Check.THeadDyn(d, _), _, _) when
                 d.StartsWith "~/"
                 && not (
                     System.IO.File.Exists(
                         (try
                             Builtins.homeDir ()
                          with _ ->
                              "")
                         + d.Substring 1
                     )
                 )
                   ->
                   [ d, te.Span ]
             // a reified pipeline's stages [D:chain-reifiers]
             | Check.TEApp(f, { Kind = Check.TEList stages }) when
                 (let rec chainVar (g: Check.TypedExpr) =
                     match g.Kind with
                     | Check.TEVar v -> v.StartsWith "|chain"
                     | Check.TEApp(h, _) -> chainVar h
                     | _ -> false

                  chainVar f)
                 ->
                 stages
                 |> List.choose (fun st ->
                     match st.Kind with
                     | Check.TETuple({ Kind = Check.TEStr prog; Span = sp } :: _) when not (Extern.exists prog) ->
                         Some(prog, sp)
                     | _ -> None)
             // a single reified command (`$(cmd | complete)`): the program
             // rides as the desugar's literal argument, warned like a
             // plain head [D:exit-reifiers]
             | Check.TEApp _ ->
                 let rec spine (e: Check.TypedExpr) acc =
                     match e.Kind with
                     | Check.TEApp(g, a) -> spine g (a :: acc)
                     | Check.TEVar v -> Some(v, acc)
                     | _ -> None

                 match spine te [] with
                 | Some(v, args) ->
                     match reifiedProgSlot v with
                     | Some(idx, arity) when List.length args = arity ->
                         match List.item idx args with
                         | { Kind = Check.TEStr prog; Span = sp } when not (Extern.exists prog) -> [ prog, sp ]
                         | _ -> []
                     | _ -> []
                 | None -> []
             | _ -> [])
            @ (Check.childExprs te |> List.collect cmdHeads)

        // module-signature pairing state [D:module-signatures] — module
        // files only; scripts pass None so the form refuses with its
        // explanation. The whole-file rules (orphan, doc-on-impl) are
        // judged after the fold, mirroring the module loader.
        let mutable sigState = if isModule then Some SigContext.empty else None
        // the module's command-reaching functions so far [D:modules-v1]
        let mutable moduleHelpers: Map<string, int> = Map.empty
        // aliases of imports that failed: a later "no module named" for
        // one echoes the import's own error, so it is not reported
        let mutable failedImports: Set<string> = Set.empty
        // a type declaration that errored: its later uses (`T.create`,
        // `from json T`) are echoes of the one real error, not new ones
        let mutable failedTypes: Set<string> = Set.empty
        // …and a failed record's literal, which resolves to no record now
        let mutable failedRecordLiterals: Set<string> = Set.empty
        let sigImplLines = ResizeArray<string * int>()

        // unused bindings [D:unused-bindings]: fed per statement, judged
        // after the fold with the module's Signed set
        let unusedTracker = UnusedTracker()

        // possible re-enumeration [D:reenum-warning]: fed per statement,
        // flushed after the fold; modules skip (a command-running module
        // `let` is already the module-declares-only error)
        let reenumTracker = ReenumTracker()

        // the newTempDir footgun [D:newtempdir-lint]: same per-statement
        // feed / post-fold flush; scripts only (modules cannot pair a
        // newTempDir bind with a delete)
        let tempDirTracker = TempDirTracker()

        // budget stop-at-first [D:budget-stop-first]: the per-statement
        // inference budget is a whole-file DoS when the multi-error fold
        // multiplies it across independent burning statements. A budget
        // diagnostic is a stop-and-fix (like the runner/module-loader
        // aborting on first error), so once one is seen the fold reports
        // it and processes no further statement — one burn bounds the file.
        // Ordinary type errors still all report.
        let mutable budgetStop = false

        for ll in logicalLines do
            if not budgetStop then
                // one spelling for the head warning, shared by the Ok walk
                // (typed) and the Error walk (parse-level) below
                let warnMissingHead (prog: string) (startCol: int) =
                    let wl, wc = translate ll startCol

                    // a near-miss binding bridges the check/run
                    // verdict split: the runner reads this head in
                    // expression mode and errors "unbound 'xx' —
                    // did you mean 'xr'?"; check's command reading
                    // must surface the same candidate
                    // a missing program suggests programs — externals and
                    // lowercase user bindings; a constructor (YMap) is never
                    // a plausible command (PLAN-dx-review D5)
                    let hint =
                        didYouMean
                            prog
                            (Seq.append
                                (Extern.names () :> seq<string>)
                                (Map.keys tenv.Values
                                 |> Seq.filter Types.isUserName
                                 |> Seq.filter (fun n -> n.Length > 0 && System.Char.IsLower n[0])))

                    let msg =
                        match Map.tryFind prog Builtins.bareAliasHomes with
                        | Some home ->
                            $"'{prog}' is a bare module member, not a program — spell it '{home}.{prog}' (bare names live in the REPL session)"
                        | None when prog.StartsWith "~/" || Extern.isPathy prog ->
                            $"no executable at {prog} — weir resolves commands at check time; the script runs once it exists"
                        | None ->
                            $"command not found on PATH: {prog}{hint} — weir resolves commands at check time; the script runs once it is installed"

                    diags.Add {
                        File = path
                        Line = wl
                        Col = wc
                        // the full head word squiggles, not one
                        // char [PLAN-diagnostics-arc A4]
                        EndLine = Some wl
                        EndCol = Some(wc + prog.Length)
                        Severity = "warning"
                        Code = "cmd-not-found"
                        Related = []
                        Fix = fixOf rawArr "cmd-not-found" msg wl wc (Some wl) (Some(wc + prog.Length))
                        Message = msg
                    }

                match checkStatement true sigState assumeResolver analyzeImport tenv ll with
                | Ok chk ->
                    // the statement's own minted anonymous shapes join its env
                    // now, not at the next statement's drain [D:anon-literals]:
                    // the editor reads a statement's env (completion after the
                    // last good line), and the pending table is per-thread
                    // state that would otherwise leak into the next analysis
                    // [D:lsp-analysis-cache]
                    let chk = {
                        chk with
                            Env = Check.withPendingAnonDefs chk.Env
                    }

                    chk.Warnings |> List.iter warn

                    // a duplicate type declaration is an error, not a silent
                    // replacement [D:dup-type-decl] — replacement would be
                    // retroactive (code above the redeclaration re-resolves
                    // against the winner). Scripts only; the REPL replaces
                    // by design.
                    (match chk.Kind with
                     | KType(decl, _) ->
                         match declaredTypes.TryGetValue decl.Name with
                         | true, (firstLine, viaImport) ->
                             diags.Add {
                                 File = path
                                 Line = ll.Head
                                 Col = 1
                                 EndLine = None
                                 EndCol = None
                                 Severity = "error"
                                 Code = "dup-type"
                                 Related = []
                                 Fix = None
                                 Message =
                                     if viaImport then
                                         $"type '{decl.Name}' is already provided by the import at line {firstLine}; rename one"
                                     else
                                         $"type '{decl.Name}' is already declared at line {firstLine}"
                             }
                         | _ -> declaredTypes[decl.Name] <- (ll.Head, false)
                     | KImport lm ->
                         for tn in lm.TypeNames do
                             if not (declaredTypes.ContainsKey tn) then
                                 declaredTypes[tn] <- (ll.Head, true)
                     | _ -> ())

                    (match chk.Kind with
                     | KType _
                     | KSig _
                     | KModule _
                     | KImport _ -> ()
                     | KLet(_, _, te)
                     | KLetPat(_, _, te)
                     | KCmd te
                     | KExpr te ->
                         for prog, span in cmdHeads te do
                             warnMissingHead prog span.Start.Col)

                    (if isModule then
                         let violation =
                             match chk.Kind with
                             | KCmd _
                             | KExpr _ ->
                                 Some "a module declares only — 'type' and 'let', no commands or bare expressions"
                             | KLet(_, _, te)
                             | KLetPat(_, _, te) when (Check.commandRunsNow moduleHelpers te).IsSome ->
                                 Some(commandAtImport (Check.commandRunsNow moduleHelpers te).Value)
                             | KLet(name, _, te) ->
                                 Check.eagerCommandHelper moduleHelpers te |> Option.map (helperAtImport name)
                             | KLetPat(_, _, te) ->
                                 Check.eagerCommandHelper moduleHelpers te |> Option.map (helperAtImport "f")
                             | _ -> None

                         (match chk.Kind with
                          | KLet(name, _, te) -> moduleHelpers <- Check.recordCommandBinding moduleHelpers name te
                          | _ -> ())

                         match violation with
                         | Some msg ->
                             let wl, wc = translate ll 1

                             diags.Add {
                                 File = path
                                 Line = wl
                                 Col = wc
                                 EndLine = None
                                 EndCol = None
                                 Severity = "error"
                                 Code = "module-declares-only"
                                 Related = []
                                 Fix = None
                                 Message = msg
                             }
                         | None -> ())

                    (match sigState, chk.Kind with
                     | Some sc, KSig(name, sd) ->
                         sigState <-
                             Some {
                                 sc with
                                     Pending = Map.add name sd sc.Pending
                                     Signed = Set.add name sc.Signed
                                     SigLines = Map.add name sd.Line sc.SigLines
                             }
                     | Some sc, KLet(name, _, _) ->
                         if Set.contains name sc.Signed then
                             sigImplLines.Add(name, ll.Head)

                         sigState <-
                             Some {
                                 sc with
                                     Pending = Map.remove name sc.Pending
                                     Implemented = Set.add name sc.Implemented
                             }
                     | Some sc, KLetPat(_, schemes, _) ->
                         sigState <-
                             Some {
                                 sc with
                                     Implemented = schemes |> List.fold (fun st (n, _) -> Set.add n st) sc.Implemented
                             }
                     | _ -> ())

                    unusedTracker.Feed ll chk

                    if not isModule then
                        reenumTracker.Feed ll chk
                        tempDirTracker.Feed ll chk

                    stmts.Add(ll, chk)
                    tenv <- chk.Env
                | Error d ->
                    unusedTracker.Poison()
                    reenumTracker.Poison()
                    tempDirTracker.Poison()
                    d.Warnings |> List.iter warn

                    // [PLAN-diagnostics-arc B5+B6]: an errored statement
                    // still (a) surfaces its command-head warnings — no
                    // typed tree exists, so the walk is parse-level — and
                    // (b) binds its let names to hole schemes so downstream
                    // uses don't cascade as "unbound". Suppression with
                    // deferral, deliberately: a hole unifies with anything,
                    // so a later genuine mismatch against the real type may
                    // surface only after this error is fixed — one real
                    // error beats N echoes. (The poison-type alternative —
                    // suppressing downstream errors that mention the name —
                    // needs a new type node through unify; declined as
                    // disproportionate.)
                    (match Parser.parseLine (assumeResolver tenv) ll.Text with
                     | Ok stmt ->
                         let rec eheads (e: Expr) =
                             (match e.Kind with
                              | ECmd(HeadLit prog, _, _) when not (Extern.exists prog) -> [ prog, e.Span ]
                              | _ -> [])
                             @ (chainStageHeads e |> List.filter (fst >> Extern.exists >> not))
                             @ (reifiedHead e |> Option.filter (fst >> Extern.exists >> not) |> Option.toList)
                             @ (exprChildren e |> List.collect eheads)

                         let exprs =
                             match stmt with
                             | SLet(_, v)
                             | SLetPat(_, v)
                             | SExpr v
                             | SCmd v -> [ v ]
                             | SType _
                             | SSig _
                             | SModule _
                             | SImport _ -> []

                         for prog, span in exprs |> List.collect eheads do
                             warnMissingHead prog span.Start.Col

                         let rec patVars (p: Pattern) =
                             match p.PKind with
                             | PVar n -> [ n ]
                             | PTuple ps -> ps |> List.collect patVars
                             | PRecord fields -> fields |> List.map snd |> List.collect patVars
                             | PCase(_, Some inner, _) -> patVars inner
                             | _ -> []

                         let holeScheme = {
                             Forall = Set.singleton "__hole"
                             Cs = Map.empty
                             Ty = TVar "__hole"
                             RowOrigins = Map.empty
                             HoleDefaults = []
                         }

                         let bound =
                             match stmt with
                             | SLet(name, _) -> [ name ]
                             | SLetPat(pat, _) -> patVars pat
                             // a failed union still names its cases, as a
                             // failed let names its binder [D:block-recovery]
                             | SType { Body = DUnion cases } -> cases |> List.map (fun (c, _, _) -> c)
                             | _ -> []

                         (match stmt with
                          | SType decl ->
                              failedTypes <- Set.add decl.Name failedTypes

                              match decl.Body with
                              | DRecord fields ->
                                  let names =
                                      fields |> List.map (fun (n, _, _) -> n) |> Set.ofList |> String.concat ", "

                                  failedRecordLiterals <-
                                      Set.add $"no declared record has exactly the fields: {names}" failedRecordLiterals
                              | DUnion _ -> ()
                          | _ -> ())

                         (match stmt with
                          | SImport(_, _, Some(alias, _)) -> failedImports <- Set.add alias failedImports
                          | SImport(ipath, _, None) ->
                              deriveModuleName ipath
                              |> Option.iter (fun alias -> failedImports <- Set.add alias failedImports)
                          | _ -> ())

                         // an errored implementation still discharges its
                         // pending signature — one real error beats a trailing
                         // orphan echo [D:module-signatures]
                         (match sigState with
                          | Some sc when bound |> List.exists (fun n -> Map.containsKey n sc.Pending) ->
                              sigState <-
                                  Some {
                                      sc with
                                          Pending = bound |> List.fold (fun m n -> Map.remove n m) sc.Pending
                                          Implemented = bound |> List.fold (fun st n -> Set.add n st) sc.Implemented
                                  }
                          | _ -> ())

                         // the tree, against the env the statement checked in;
                         // its env is the hole-bound one below. A budget failure
                         // is not paid twice [D:budget-stop-first]
                         let recovered, recoveryBudget =
                             if Check.isBudgetMessage d.Message then
                                 None, false
                             else
                                 recoverStatement tenv holeScheme stmt

                         for n in bound do
                             tenv <- bindValue n holeScheme tenv

                         recovered
                         |> Option.iter (fun kind ->
                             partials.Add(
                                 ll,
                                 {
                                     Kind = kind
                                     Env = tenv
                                     Warnings = []
                                 }
                             ))

                         // a spent recovery budget stops the fold exactly as a
                         // published budget diagnostic does [D:budget-stop-first]:
                         // one burn bounds the file, not one burn per statement
                         if recoveryBudget then
                             budgetStop <- true
                     | Error _ ->
                         // a statement that does not parse still names its
                         // binder [D:parse-error-binds]: harvested lexically, a
                         // `let NAME` at the statement's start only (never argv),
                         // so its later uses don't echo as "unbound"
                         let m =
                             System.Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+([A-Za-z_][A-Za-z0-9_']*)")

                         if m.Success && not (Parser.keywords.Contains m.Groups[1].Value) then
                             tenv <-
                                 bindValue
                                     m.Groups[1].Value
                                     {
                                         Forall = Set.singleton "__hole"
                                         Cs = Map.empty
                                         Ty = TVar "__hole"
                                         RowOrigins = Map.empty
                                         HoleDefaults = []
                                     }
                                     tenv)

                    let echoesFailedImport =
                        failedImports
                        |> Set.exists (fun a -> d.Message.StartsWith $"no module named '{a}'")
                        || (match d.File with
                            | Some mf -> not (seenModuleErrors.Add((realPath mf, d.PhysLine, d.PhysCol, d.Message)))
                            | None -> false)
                        || failedTypes
                           |> Set.exists (fun t ->
                               d.Message.StartsWith $"no module named '{t}'"
                               || d.Message.StartsWith $"unknown type '{t}'")
                        || failedRecordLiterals.Contains d.Message

                    // multi-file [D:modules-v1]: a module error carries File +
                    // PhysLine/Col into that other file; its Note is the "imported
                    // here" pointer at the import line in this file
                    if not echoesFailedImport then
                        let code = codeOf d.Parse d.Message

                        diags.Add {
                            File = d.File |> Option.defaultValue path
                            Line = d.PhysLine
                            Col = d.PhysCol
                            EndLine = d.PhysEnd |> Option.map fst
                            EndCol = d.PhysEnd |> Option.map snd
                            Severity = "error"
                            // the note's site rides as related too [D:diagnostic-tags]
                            Related =
                                (d.Related
                                 |> List.map (fun (l, c, m) -> d.File |> Option.defaultValue path, l, c, m))
                                @ (d.Note |> Option.map (fun (nl, nc, nmsg) -> path, nl, nc, nmsg) |> Option.toList)
                            Code = code
                            Fix =
                                fixOf
                                    (if d.File.IsSome then [||] else rawArr)
                                    code
                                    d.Message
                                    d.PhysLine
                                    d.PhysCol
                                    (d.PhysEnd |> Option.map fst)
                                    (d.PhysEnd |> Option.map snd)
                            Message = d.Message
                        }

                    (match d.Note with
                     | Some(nl, nc, nmsg) when not echoesFailedImport || d.File.IsNone ->
                         diags.Add {
                             File = path
                             Line = nl
                             Col = nc
                             EndLine = None
                             EndCol = None
                             Severity = "note"
                             Code = "imported-here"
                             Related = [ d.File |> Option.defaultValue path, d.PhysLine, d.PhysCol, d.Message ]
                             Fix = None
                             Message = nmsg
                         }
                     | _ -> ())

                    // budget stop-at-first [D:budget-stop-first]: a budget
                    // diagnostic ends the fold — no further statement is
                    // checked, so one exhaustion cannot multiply across the file
                    if Check.isBudgetMessage d.Message then
                        budgetStop <- true

        // the whole-file signature rules [D:module-signatures]: every
        // remaining pending sig is an orphan; a /// doc on a signed
        // member's implementation names its one home
        (match sigState with
         | Some sc ->
             for name, sd in sc.Pending |> Map.toList |> List.sortBy (fun (_, sd) -> sd.Line) do
                 diags.Add {
                     File = path
                     Line = sd.Line
                     Col = sd.Col
                     EndLine = None
                     EndCol = None
                     Severity = "error"
                     Code = "missing-implementation"
                     Related = []
                     Fix =
                         // under the signature's last physical line (an
                         // indented continuation belongs to it)
                         (let mutable last = sd.Line

                          while last < rawArr.Length
                                && rawArr[last].Trim() <> ""
                                && Char.IsWhiteSpace(rawArr[last].[0]) do
                              last <- last + 1

                          Some {
                              Title = "Add a stub implementation"
                              Edits = [
                                  {
                                      Line = last
                                      Col = (if last <= rawArr.Length then rawArr[last - 1].Length else 0) + 1
                                      EndLine = last
                                      EndCol = (if last <= rawArr.Length then rawArr[last - 1].Length else 0) + 1
                                      NewText = $"\nlet {name} = fail \"todo\""
                                  }
                              ]
                              Family = None
                          })
                     Message = $"signature without implementation — no 'let {name} … = …' follows in this module"
                 }

             let attaches = docAttachments rawLines

             for name, implLine in sigImplLines do
                 match attaches |> List.tryFind (fun d -> d.Line = implLine) with
                 | Some d ->
                     diags.Add {
                         File = path
                         Line = max 1 (d.Line - List.length d.Doc)
                         Col = 1
                         EndLine = None
                         EndCol = None
                         Severity = "error"
                         Code = "doc-on-implementation"
                         Related =
                             (match Map.tryFind name sc.SigLines with
                              | Some sl -> [ path, sl, 1, "the doc's one home" ]
                              | None -> [])
                         Fix =
                             // two edits against the unedited text: the doc run
                             // leaves the implementation, lands above the sig
                             (let first = max 1 (d.Line - List.length d.Doc)
                              let run = [ first .. d.Line - 1 ] |> List.map (fun l -> rawArr[l - 1])

                              match Map.tryFind name sc.SigLines with
                              | Some sigLine when
                                  run.Length = List.length d.Doc
                                  && run |> List.forall isDocLine
                                  && sigLine < first
                                  ->
                                  Some {
                                      Title = "Move doc to the signature"
                                      Edits = [
                                          {
                                              Line = sigLine
                                              Col = 1
                                              EndLine = sigLine
                                              EndCol = 1
                                              NewText = (run |> List.map (fun l -> l + "\n") |> String.concat "")
                                          }
                                          {
                                              Line = first
                                              Col = 1
                                              EndLine = d.Line
                                              EndCol = 1
                                              NewText = ""
                                          }
                                      ]
                                      Family = None
                                  }
                              | _ -> None)
                         Message =
                             $"'{name}' is signed, and its /// doc belongs on the signature — move it above 'let {name} : …' (one doc home)"
                     }
                 | None -> ()
         | None -> ())

        // the unused-binding check [D:unused-bindings]: whole-file, so it
        // judges after the fold; poisoned (any errored statement) = silent.
        // A file under .weir/sigs/ is a sig contract — its members
        // (version, exhaustive, Cmd) are read by the sig loader, so the
        // loader is the use and a direct `weir check` agrees with it
        (let isSigContract =
            let norm = path.Replace('\\', '/')
            norm.Contains "/.weir/sigs/" || norm.StartsWith ".weir/sigs/"

         let signedNames =
             sigState |> Option.map (fun sc -> sc.Signed) |> Option.defaultValue Set.empty

         for f in
             (if isSigContract then
                  []
              else
                  unusedTracker.Flush signedNames isModule) do
             diags.Add {
                 File = path
                 Line = f.ULine
                 Col = f.UCol
                 EndLine = Some f.ULine
                 EndCol = Some f.UEndCol
                 Severity = "error"
                 Code = "unused-binding"
                 Related = f.URelated |> List.map (fun (l, c, m) -> path, l, c, m)
                 Fix = fixOf rawArr "unused-binding" f.UMessage f.ULine f.UCol (Some f.ULine) (Some f.UEndCol)
                 Message = f.UMessage
             })

        // the re-enumeration warning lands on the second and later
        // enumerating uses [D:reenum-warning] — warning severity, so
        // check still exits 0
        (for f in reenumTracker.Flush() do
            diags.Add {
                File = path
                Line = f.RLine
                Col = f.RCol
                EndLine = Some f.RLine
                EndCol = Some f.REndCol
                Severity = "warning"
                Code = "re-enumeration"
                Related = []
                Fix = None
                Message = f.RMessage
            })

        // the newTempDir footgun [D:newtempdir-lint] lands on the delete
        // site — warning severity, so check still exits 0
        (for f in tempDirTracker.Flush() do
            diags.Add {
                File = path
                Line = f.TLine
                Col = f.TCol
                EndLine = Some f.TLine
                EndCol = Some f.TEndCol
                Severity = "warning"
                Code = "temp-dir-cleanup"
                Related = []
                Fix = None
                Message = f.TMessage
            })

        // effects before the CLI parse [D:effect-before-args]: the entry
        // script only (a module or init file has no Args.load), silent
        // when a statement errored — one real error beats advice
        (if
             not isModule
             && not initMarked
             && not (diags |> Seq.exists (fun d -> d.Severity = "error"))
         then
             for d in effectBeforeArgs path (stmts |> Seq.map (fun (ll, chk) -> ll, chk.Kind) |> List.ofSeq) do
                 diags.Add d)

        (let sigLoadDiags, sigInfos = loadSigs path sigDecls

         // position order, not accumulation order (rider D1): assembly
         // diagnostics are gathered before parsing, so unsorted output leads
         // with "line 4" for a file that was not plausible from byte one.
         // Sorted at the one return, so check / --json / --can / the LSP
         // all inherit it [D:one-pipeline]; stable, so same-position
         // diagnostics keep their emission order.
         List.ofSeq assemblyDiags
         @ List.ofSeq diags
         @ docMisalignments path rawLines
         @ schemaDiagnostics path (List.ofSeq stmts)
         @ sigLoadDiags
         @ sigCmdDiagnostics path sigInfos (List.ofSeq stmts)
         @ continuedCommandDiagnostics path sigInfos (List.ofSeq stmts)
         |> withInitRules
         |> List.sortWith (fun a b ->
             let byLine = compare a.Line b.Line
             if byLine <> 0 then byLine else compare a.Col b.Col)
         // init declarations are session exports [D:init-marker]: the
         // unused-binding judgement does not apply to a marked file
         |> List.filter (fun d -> not (initMarked && d.Code = "unused-binding"))),
        List.ofSeq stmts,
        typeEnv0,
        logicalLines,
        List.ofSeq partials

/// the stray directive lines report as run reports them, and the rest of
/// the file still checks with those lines blanked (line numbers kept) —
/// never an expected-token dump at the `#` — for every consumer: check,
/// fmt's parse gate, the LSP [D:directive-diagnostic]. An init-marked
/// file checks as the init it is [D:init-marker]; its directives are
/// checkInit's to judge
let analyzeLinesAs (asInit: bool) (path: string) (rawLines: string list) =
    let misuses =
        if asInit || isInitMarked rawLines then
            []
        else
            directiveMisuses rawLines

    if List.isEmpty misuses then
        analyzeLinesBare asInit path rawLines
    else
        let stray = misuses |> List.map fst |> Set.ofList

        let directiveDiags =
            misuses
            |> List.map (fun (line, msg) -> {
                File = path
                Line = line
                Col = 1
                EndLine = None
                EndCol = None
                Severity = "error"
                Code = "directive"
                Message = msg
                Related = []
                Fix = None
            })

        let checkedLines =
            rawLines |> List.mapi (fun i l -> if stray.Contains(i + 1) then "" else l)

        let diags, stmts, env, lls, partials = analyzeLinesBare asInit path checkedLines
        (directiveDiags @ diags |> List.sortBy (fun d -> d.Line)), stmts, env, lls, partials

let analyzeLines (path: string) (rawLines: string list) = analyzeLinesAs false path rawLines

/// diagnostic rendering shared by `check` and `check --can`
/// [D:can-report] — one spelling for both consumers
/// `parsedAny` is "did any statement parse" — the conservative half of the
/// not-weir heuristic (rider D4). Without it a real script with a broken
/// first line would be told it is not weir at all.
let printDiags (json: bool) (parsedAny: bool) (diags: Diagnostic list) : unit =
    if json then
        // the encoder is unchanged; a tty sink gets the safe rendering
        // [D:tty-bidi-sanitize]
        Console.WriteLine(
            jsonBuild (fun w ->
                w.WriteStartArray()
                diags |> List.iter (writeDiag w)
                w.WriteEndArray())
            |> Eval.sanitizeIfTty Console.IsOutputRedirected
        )
    else
        let c = Color.onStdout.Value

        // rider D4: a file that failed at its first statement and produced no
        // parsed statement anywhere is probably not a weir script — say so
        // once instead of leaving the reader to infer it from a list. It
        // precedes the detail rather than replacing it (a reader who wants
        // the parse errors keeps them), and never appears in --json, where a
        // gate wants real diagnostics and not a human affordance. No
        // extension rule: this also covers shebang files, stdin and
        // extensionless fixtures, which an extension check cannot.
        // a bare-member teaching is weir's own diagnosis of weir code —
        // the file is weir, so no preamble [D:bare-partition]
        let notWeir =
            not parsedAny
            && diags
               |> List.exists (fun d -> d.Line = 1 && d.Code = "parse" && d.Severity = "error")
            && not (
                diags
                |> List.exists (fun d -> d.Line = 1 && d.Message.Contains "is a bare module member")
            )

        if notWeir then
            match diags with
            | d :: _ ->
                Console.WriteLine(
                    Color.bold c $"{quoteData d.File}"
                    + ": this does not look like a weir script — the first statement could not be parsed"
                    |> Eval.sanitizeIfTty Console.IsOutputRedirected
                )
            | [] -> ()

        for d in diags do
            let sev =
                if d.Severity = "warning" then
                    Color.yellow c $"warning [{d.Code}]"
                elif d.Severity = "note" then
                    Color.bold c $"note [{d.Code}]"
                else
                    Color.red c $"error [{d.Code}]"

            // a message echoes source, paths, patterns [D:tty-diag-sanitize]
            Console.WriteLine(
                Color.bold c $"{quoteData d.File}:{d.Line}:{d.Col}" + $": {sev}: {d.Message}"
                |> Eval.sanitizeIfTty Console.IsOutputRedirected
            )

let directiveMisuse (rawLines: string list) : (int * string) option =
    directiveMisuses rawLines |> List.tryHead

let checkOnly (json: bool) (path: string) : int =
    dropModuleBodies <- true

    match readEntryScript path with
    | Error msg ->
        Console.Error.WriteLine $"weir: {msg}"
        2
    | Ok rawLines ->
        // the stray directive lines report as run reports them, and the
        // rest of the file still checks with those lines blanked (line
        // numbers kept) — never an expected-token dump at the `#`. An
        // init-marked file checks as the init it is [D:init-marker]; its
        // directives are analyzeLines' to judge
        let diags, stmts, _, _, _ = analyzeLines path rawLines
        let anyDirective = diags |> List.exists (fun d -> d.Code = "directive")
        printDiags json (not (List.isEmpty stmts) || anyDirective) diags

        if diags |> List.exists (fun d -> d.Severity = "error") then
            1
        else
            0

// [D:doc-help] the `///` first-line help for the fields of the type decl
// on `ll`, keyed by field name. A field's DocAttach sits at the field's own
// physical line, so scope by the decl's physical lines (its Segments); a
// field name is unique within a decl. An empty first line -> no help entry
// (silence beats a mystery), same as no doc.
