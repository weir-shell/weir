/// Checking one statement: the statement types, the base environments, positions, and the whole-file lints threaded through a check. One of the files Script.fs was [D:script-by-concern].
module Weir.Statements

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor

// a `#sig` head directive [D:command-signatures]: tool + optional
// override path + its physical line (so errors can name the declaration)
type SigDecl = {
    Tool: string
    Override: string option
    Line: int
}

// shebang/#sig peeling — one derivation for the runner and the
// check-side analyzeLines. The head block takes #sig lines; a #-line
// past it stays the misplaced-directive error.
let internal scriptBody (rawLines: string list) : string list * int * SigDecl list =
    let afterShebang, shebangOffset =
        match rawLines with
        | first :: rest when first.StartsWith "#!" -> rest, 1
        | _ -> rawLines, 0

    let mutable rest = afterShebang
    let mutable off = shebangOffset
    let sigs = ResizeArray<SigDecl>()
    let mutable go = true

    while go do
        match rest with
        | first :: tail when first.TrimStart().StartsWith "#sig" ->
            let t = (stripComment first).Trim()

            let decl =
                match t.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
                | [ "#sig"; tool ] -> {
                    Tool = tool
                    Override = None
                    Line = off + 1
                  }
                | [ "#sig"; tool; path ] when path.StartsWith "\"" && path.EndsWith "\"" && path.Length > 1 -> {
                    Tool = tool
                    Override = Some(path.Substring(1, path.Length - 2))
                    Line = off + 1
                  }
                | _ ->
                    // malformed: carry the raw for the diagnostic
                    {
                        Tool = ""
                        Override = Some t
                        Line = off + 1
                    }

            sigs.Add decl
            rest <- tail
            off <- off + 1
        | _ -> go <- false

    rest, off, List.ofSeq sigs

type CheckedStmt =
    | CLet of name: string * te: Check.TypedExpr
    | CLetPat of binder: Weir.Ast.Pattern * te: Check.TypedExpr
    | CExpr of te: Check.TypedExpr
    | CCmd of te: Check.TypedExpr
    // validator: a validated type's id and typed `validate` function
    // [D:validated-types] — the run registers its `create` from them
    | CType of decl: Decl * validator: (string * Check.TypedExpr) option
    | CNoop
    // an imported module's replayable body [D:modules-v1] — evaluated once
    // at exec into the module's own venv, exposed as `alias.member`
    | CImport of LoadedModule

// a checked, imported module [D:modules-v1]. Its check-time contributions
// (Members -> Modules[Alias], TypeDefs -> Types, TypeNames -> ModuleTypes)
// merge into the importer's tenv; Body replays at exec to build the
// module's values. AbsPath is the path as reached; the identity is its
// real path [D:module-once].
and LoadedModule = {
    Alias: string
    // the module's own name (declared or filename-derived), independent
    // of a site's `as` — a cached module re-aliases from this [D:modules-v1]
    NaturalName: string
    AbsPath: string
    TypeDefs: (string * TypeDef) list
    Members: (string * Scheme) list
    // unsigned members [D:module-signatures]: never resolvable from an
    // importer — carried so the import-of-private error can suggest
    // the signature to add (type included)
    PrivateMembers: (string * Scheme) list
    TypeNames: string list
    // each TypeDefs entry's identity [D:transitive-types]
    TypeOrigins: (string * string) list
    // each statement with its logical line [D:can-report]: the line
    // carries the segment table, so a capability inside a module can
    // name its own file:line:col like any diagnostic
    Body: (LogicalLine * CheckedStmt) list
    // each signed member's /// doc, read from its signature [D:module-signatures]
    MemberDocs: Map<string, string list>
    // the /// run above the `module` line [D:module-doc]
    ModuleDoc: string list
    // a prompt import may take an alias an earlier import holds — the
    // session's reload [D:repl-import]; scripts and -e never set it
    MayRebind: bool
}

// an import failure [D:modules-v1]. File=Some for a module-content error —
// reported at the module's own site (Line/Col into that file) plus an
// "imported here" note at the import line; File=None for an import-statement
// error (self-import, missing file, not-a-module) reported at the import line.
and ImportError = {
    File: string option
    Line: int
    Col: int
    Message: string
}

// resolves an `import` to a checked module, or an ImportError [D:modules-v1].
// The caller binds it to the importing file's directory; the script-only
// (-e/REPL) and nested-import variants just return Error. Args: the literal
// path, its span (for error placement), the optional `as` alias.
and ImportLoader = string -> Span -> string option -> Result<LoadedModule, ImportError>

// the Self module [D:self-module]: script/process introspection grouped
// under one name, freeing the bare `args`/`stdin`/`scriptPath` for users.
// Members are per-run, so they inject here (not in static Builtins); the
// type side is a Modules entry, the value side mangled "Self.member" keys
// — module access checks `EField {EVar "Self"} field` -> TEVar "Self.field"
let selfMembers: Map<string, Scheme> =
    Map [
        "pid", generalize TInt
        "args", generalize (TSeq TStr)
        "stdin", generalize (TSeq TStr)
        // scriptPath = the file's own path (a module sees its own);
        // entryPath = the invoked script's, a process fact like args/stdin
        // — the same for every file in the run [D:modules-v1] (decision 12)
        "scriptPath", generalize TStr
        "entryPath", generalize TStr
        // the interactive read [D:prompt] — static (Builtins holds the
        // value under the mangled key); grouped here, not bare
        "prompt", generalize (TFun(TStr, TStr))
    ]

// the #session prompt's status domain [D:prompt-status-arg]: records
// are nominal and init.weir admits no signatures, so a provider's
// param types as a ROW ({ ok: bool; .. } — field access on a bare
// param) — accepted when every named field is PromptStatus's own,
// exactly typed; a bare unused param ('a) is accepted and simply
// ignores the status. One predicate, read by the loader and the LSP.
let promptStatusDomain (dom: Ty) : bool =
    match dom with
    | TNamed("PromptStatus", []) -> true
    | TVar _ -> true
    | TRowVar(_, fields) ->
        fields
        |> List.forall (fun (f, t) ->
            // an unpinned var is a field the body only forwards (show,
            // isSome) — the runtime value satisfies it; a WRONGLY pinned
            // field (exit as Option<string>, a hole-defaulted ok) refuses
            match f, t with
            | "ok", (TBool | TVar _) -> true
            | "exit", (TNamed("Option", [ TInt ]) | TNamed("Option", [ TVar _ ]) | TVar _) -> true
            | "duration", (TDur | TVar _) -> true
            | _ -> false)
    | _ -> false

let internal baseEnvs (scriptArgs: string list) (scriptPath: string) =
    let typeEnv = Builtins.typeEnvStrict

    let typeEnv, valueEnv = Prelude.extend typeEnv Builtins.valueEnv.Value

    let typeEnv = {
        typeEnv with
            Modules = typeEnv.Modules |> Map.add "Self" selfMembers
    }

    let stdinStream =
        // one enumeration [D:prompt]: stdin is a live stream — a second
        // GetEnumerator cannot rewind the fd, and yielding empty there
        // silently produces wrong data; raise with the repair instead.
        // 0 untouched, 1 an enumeration began, 2 it read: one cut before
        // its first read (a race's loser) read nothing, so it leaves stdin
        // untouched [D:stdin-reader]
        let consumed = ref 0

        let firstLine () =
            try
                let line = Session.Stdin.readLine ()
                consumed.Value <- 2
                line
            with _ ->
                System.Threading.Interlocked.CompareExchange(&consumed.contents, 0, 1) |> ignore
                reraise ()

        Eval.VSeq(
            Seq.delay (fun () ->
                if System.Threading.Interlocked.CompareExchange(&consumed.contents, 1, 0) <> 0 then
                    failwith
                        "Self.stdin is a live stream and was already consumed — bind ONE enumeration (let lines = Self.stdin |> Seq.freeze), or read a line per interaction with `Self.prompt`"

                // weir's one stdin reader [D:stdin-reader]
                seq {
                    let mutable line = firstLine ()

                    while line <> null do
                        yield Eval.VStr line
                        line <- Session.Stdin.readLine ()
                })
        )

    Session.ScriptArgs <- scriptArgs
    Session.EntryPath <- scriptPath

    let valueEnv =
        Eval.splitBase valueEnv
        |> Map.add "Self.pid" (Eval.VInt(int64 System.Environment.ProcessId))
        |> Map.add "Self.args" (Eval.VSeq(scriptArgs |> List.map Eval.VStr :> seq<Eval.Value>))
        |> Map.add "Self.stdin" stdinStream
        // the entry is the invoked script, so its own path and the entry
        // path coincide here; a module later overrides scriptPath with its
        // own while entryPath rides along as a process fact
        |> Map.add "Self.scriptPath" (Eval.VStr scriptPath)
        |> Map.add "Self.entryPath" (Eval.VStr scriptPath)

    typeEnv, valueEnv

// the base resolver over a type env — one constructor behind the
// script/fmt/REPL/CLI call sites
let resolver (typeEnv: TypeEnv) : Parser.Resolver = {
    IsKnown = fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules
    IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
    IsExternal = Extern.exists
    ExternalNames = (fun () -> Extern.names () :> seq<string>)
    BareHome = (fun n -> Map.tryFind n Builtins.bareAliasHomes)
    // scripts/fmt/CLI carry no aliases (REPL-only) — the REPL overrides
    // this field with its session alias table [D:command-head-alias]
    AliasHead = fun _ -> None
    IsBuiltinName = fun n -> Map.containsKey n Builtins.typeEnv.Values || Map.containsKey n typeEnv.Modules
}

let internal located (path: string) (lineNo: int) (msg: string) : string =
    let msg =
        if msg.StartsWith "[1:" then
            $"[{lineNo}:" + msg.Substring 3
        else
            msg

    // a file name is data [D:error-data-escape]
    $"{quoteData path}:{lineNo}: {msg}"

// the runtime-error line [D:binary-echo]: an error message may interpolate
// data (a hostile filename, a tenant name), and error text is a tty-bound
// renderer — so sanitize the message when stderr is a tty, the same guard
// print's data path uses. weir's own "error" colour word is added around
// the sanitized message, so colouring is untouched; redirected stderr
// stays byte-faithful.
let internal runtimeErrorLine (path: string) (lineNo: int) (message: string) : string =
    let safe = Eval.sanitizeIfTty Console.IsErrorRedirected message
    located path lineNo (Color.red Color.onStderr.Value "error" + $": {safe}")

// every node of a statement's typed tree -> its physical (line, col), via
// that statement's OWN line map [D:runtime-position] (a logical span means
// nothing through another statement's map — [D:row-provenance]). Keyed by
// node identity; iterative, so a wide tree cannot overflow the stack.
let internal recordPositions
    (file: string)
    (stmt: int)
    (ll: LogicalLine)
    (root: Check.TypedExpr)
    (into: System.Collections.Generic.Dictionary<obj, string * int * int * int>)
    =
    let stack = System.Collections.Generic.Stack<Check.TypedExpr>()
    stack.Push root

    while stack.Count > 0 do
        let n = stack.Pop()
        let key = box n

        if not (into.ContainsKey key) then
            if n.Span.Start.Col >= 1 then
                try
                    let l, c = translate ll n.Span.Start.Col
                    into[key] <- (file, stmt, l, c)
                with _ ->
                    ()

            Check.iterChildren stack.Push n

// Streaming output for command-mode statements — the single exempt form.
// The seq case goes through Eval.writeLines, the same renderer print uses.
let printResult (v: Eval.Value) =
    match v with
    | Eval.VStr s -> Console.WriteLine s
    | Eval.VSeq items -> Eval.writeLines items
    // unit-valued command statements (| orFail) print nothing —
    // the assert idiom is silent on success [D:exit-reifiers]
    | Eval.VUnit -> ()
    | other -> Console.WriteLine(Eval.formatValue other)

// ---- partial application, named [D:partial-echo]: an expression that
// applies a named function to fewer arguments than it names. One
// analysis for the REPL echo, the -e echo, and the statement gate.
type PartialApp = {
    Name: string
    Params: string list
    HeadTy: Ty
    Applied: int
    LastPiped: bool
}

/// a builtin head's display name (a bare alias names its home) and its
/// documented parameters; None when the name is rebound in `tenv`
let builtinParamNames (tenv: TypeEnv) (name: string) : (string * string list) option =
    let schemeIn (te: TypeEnv) =
        match name.Split '.' with
        | [| m; mem |] -> te.Modules |> Map.tryFind m |> Option.bind (Map.tryFind mem)
        | _ -> Map.tryFind name te.Values

    let qualified =
        match Map.tryFind name Builtins.bareAliasHomes with
        | Some m when not (name.Contains ".") -> $"{m}.{name}"
        | _ -> name

    match schemeIn tenv, schemeIn Builtins.typeEnv with
    | Some mine, Some builtin when obj.ReferenceEquals(mine, builtin) ->
        Map.tryFind qualified Builtins.builtinDocs.Value
        |> Option.map (fun d -> qualified, d.Params)
    | _ -> None

let private arrowParts (ty: Ty) : Ty list =
    let rec go t =
        match t with
        | TFun(dom, cod) -> dom :: go cod
        | _ -> []

    go ty

/// `names` resolves a head to its display name and parameter names
let partialApp (names: string -> (string * string list) option) (te: Check.TypedExpr) : PartialApp option =
    match te.Ty with
    | TFun _ ->
        Check.partialSpine te
        |> Option.bind (fun (head, headTy, applied, piped) ->
            names head
            |> Option.bind (fun (shown, ps) ->
                let ps = List.truncate (arrowParts headTy).Length ps

                if ps.Length > applied then
                    Some {
                        Name = shown
                        Params = ps
                        HeadTy = headTy
                        Applied = applied
                        LastPiped = piped
                    }
                else
                    None))
    | _ -> None

/// the echo's two lines: the signature with the bound parameters bare,
/// then what is bound and what is missing
let partialEchoLines (st: SigStyle) (p: PartialApp) : string list = [
    formatPartialSignatureWith st p.Applied p.Name p.Params (normalizeEchoVars p.HeadTy)
    "partial application — "
    + partialClauses (List.truncate p.Applied p.Params) (List.skip p.Applied p.Params) p.LastPiped
]

// the partial-application lesson's marker — codeOf reads it back
let partialDiscardMark = " is partially applied — "

/// the statement gate's lesson for a discarded partial application; a
/// head with no known parameter names still says how many are missing
let partialDiscard (tenv: TypeEnv) (te: Check.TypedExpr) : string option =
    match partialApp (builtinParamNames tenv) te with
    | Some p ->
        let missing =
            List.zip p.Params (List.truncate p.Params.Length (arrowParts p.HeadTy))
            |> List.skip p.Applied
            |> List.map (fun (n, t) -> $"{n} ({formatTy t})")

        Some(
            $"'{p.Name}'{partialDiscardMark}"
            + partialClauses (List.truncate p.Applied p.Params) missing p.LastPiped
        )
    | None ->
        match Check.partialSpine te, arrowParts te.Ty with
        | Some(head, _, _, _), (_ :: _ as rest) ->
            let tys = rest |> List.map formatTy |> String.concat ", "
            let noun = if rest.Length = 1 then "argument" else "arguments"
            Some $"'{head}'{partialDiscardMark}still needs {rest.Length} more {noun} ({tys})"
        | _ -> None

// The statement rule: a pure expression statement must have type unit.
// Classification is the parser's mode decision alone (SCmd vs SExpr) — no
// name lookup, no type direction; the removed form-2 exemption (bare
// sh/cmd applications) must not creep back in here.
// the uncalled-function tail — fixOf reads it back [D:structured-fixes]
let callItTail = " — that is the function itself, not its result: call it with ()"

let discardError (ty: Ty) : string option =
    match ty with
    | TUnit -> None
    // a unit -> X value as a statement is a function nobody called
    // [D:structured-fixes]: the lesson is the call, not the binding
    | TFun(TUnit, _) as ty -> Some $"this statement computes a {formatTy ty} and discards it{callItTail}"
    // a hole-typed statement descends from an already-reported failed
    // let — silent, not a second complaint [PLAN-diagnostics-arc B6]
    | TVar v when v.StartsWith "__hole" -> None
    | TSeq TUnit ->
        Some "this statement computes a seq<unit> and discards it — a lazy effect sequence never runs; use Seq.iter"
    | TSeq(TNamed("FileRow", [])) as ty ->
        Some(
            $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"
            + " (for a plain listing, ^ls runs the real program)"
        )
    // an unresolved statement type [D:exit-polymorphic]: nothing
    // determines it — almost always a helper whose body ends in
    // exit/fail (diverging, so polymorphic — never unit). A bare
    // "computes a 'a1" names no cause; this names it and the repair.
    | TVar _ ->
        Some(
            $"this statement computes a value of undetermined type and discards it — an unresolved type here usually means the helper ends in exit or fail, which makes it polymorphic, not unit: return the exit code and exit at the call site (exit (helper …)), or bind the value"
        )
    | ty -> Some $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"

// ---------------------------------------------------------------------------
// The checked-statement pipeline — one owner [D:one-pipeline]:
// parse -> statement dispatch -> check -> statement-rule gate,
// physical spans computed inside. Every consumer (runner, REPL, -e,
// check/LSP, the oracle mirror) calls this and only renders.

// a module member signature [D:module-signatures] — the export
// declaration. Line/Col are the sig's own physical site (mismatch and
// orphan errors name it); Scheme is the sig type generalized (the
// paired implementation's check refines its Cs).
type MemberSig = {
    Ty: Ty
    Scheme: Scheme
    Line: int
    Col: int
}

// the module-signature context a module check threads [D:module-signatures]:
// None = script/REPL/-e, where the form refuses with the teaching.
// Pending = declared, not yet implemented; Signed = every name ever
// signed (re-implementation guard); Implemented = every module-own
// binder so far (the sig-after-impl guard); SigLines = each signature's
// line, where its /// doc lives (one doc home).
type SigContext = {
    Pending: Map<string, MemberSig>
    Signed: Set<string>
    Implemented: Set<string>
    SigLines: Map<string, int>
}

module SigContext =
    let empty = {
        Pending = Map.empty
        Signed = Set.empty
        Implemented = Set.empty
        SigLines = Map.empty
    }

type CheckedKind =
    | KType of decl: Decl * validator: (string * Check.TypedExpr) option
    | KLet of name: string * scheme: Scheme * te: Check.TypedExpr
    | KLetPat of binder: Pattern * schemes: (string * Scheme) list * te: Check.TypedExpr
    | KCmd of te: Check.TypedExpr
    | KExpr of te: Check.TypedExpr
    // the module marker [D:modules-v1] — makes the file a module (decl-only,
    // not runnable); the runner turns it into the running-a-module error
    | KModule of name: string option * kwSpan: Span
    // a resolved import [D:modules-v1] — its Env merge already applied
    | KImport of LoadedModule
    // a member signature [D:module-signatures] — binds nothing yet; the
    // caller pairs it with its implementation (and errors the orphan)
    | KSig of name: string * decl: MemberSig

[<RequireQualifiedAccess>]
type StmtTag =
    | Type
    | Let
    | LetPat
    | Cmd
    | Expr

type StmtDiag = {
    PhysLine: int
    PhysCol: int
    PhysEnd: (int * int) option // physical end of the span, when known
    Tag: StmtTag option // None for parse failures (kind unknown)
    HasCol: bool // false only for col-less parse failures
    Span: Span option // None for parse failures
    Parse: bool // parse error (FParsec text) vs type error (message)
    Message: string
    // multi-file [D:modules-v1]: File=Some when PhysLine/PhysCol point
    // into another file (a module's own error site); Note carries an
    // extra (line, col, message) in the current file (the import-line
    // "imported here"). Both default to None (single-file).
    File: string option
    Note: (int * int * string) option
    // related sites in the SAME file as the primary [D:diagnostic-tags]
    Related: (int * int * string) list
    // the runner prints warnings even when the discard gate then
    // errors (standing behavior) — they travel with the diag
    Warnings: (int * int * string) list
}

type CheckedStatement = {
    Kind: CheckedKind
    Env: TypeEnv // the env AFTER this statement (bindings added)
    Warnings: (int * int * string) list
} // physical line, col, message

// gateExprs: scripts apply the statement rule (values must be bound or
// printed); the REPL and -e echo values instead — the same pipeline,
// one explicit switch, never a re-derivation
// assume only command-shaped words (letter-initial, ident chars +
// dashes; never keywords, never dotted): the expression grammar must
// keep claiming Env.load, from-adapters, and punctuation heads
let private caseNameSets =
    System.Runtime.CompilerServices.ConditionalWeakTable<Map<string, TypeDef>, Set<string>>()

let private caseNameSet =
    System.Runtime.CompilerServices.ConditionalWeakTable<Map<string, TypeDef>, Set<string>>.CreateValueCallback
        (fun types ->
            types
            |> Seq.collect (fun (KeyValue(_, def)) ->
                match def with
                | Union u -> u.Cases |> List.map fst
                | Record _ -> [])
            |> Set.ofSeq)

let assumeResolver (tenv: TypeEnv) : Parser.Resolver =
    // a union case of any known type (an ambiguous imported one
    // included) is an expression head, never a program — so check
    // reaches the runner's "module-qualified here" verdict [D:assume-resolver]
    {
        resolver tenv with
            IsExternal =
                fun n ->
                    Extern.exists n
                    // a path head that does not exist yet (`./gen.sh`, built
                    // earlier in the script) is the PATH rule's: check warns,
                    // run errors [D:path-head-warn]. Only path-led words — no
                    // expression starts with '/' or '.', while `total/2` is
                    // arithmetic
                    || ((n.StartsWith "./" || n.StartsWith "../" || n.StartsWith "/")
                        && not (n.Contains '\000'))
                    || (n.Length > 0
                        && System.Char.IsLetter n[0]
                        && not (Parser.isKeyword n)
                        // a declared type or module is never a command — else the
                        // named literal `Ctx { .. }` / `Paths.Ctx { .. }` mis-parses
                        // under check's assume-command rule [D:modules-v1]
                        && not (Map.containsKey n tenv.Types)
                        && not (Map.containsKey n tenv.Modules)
                        // an import's type, its bare name ambiguous [D:qualified-type-names]
                        && not (tenv.ModuleTypes |> Map.exists (fun _ owned -> owned.ContainsKey n))
                        && not (caseNameSets.GetValue(tenv.Types, caseNameSet).Contains n)
                        && n |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '-'))
    }

// mkR builds the resolver from the current env per statement, so
// script-defined names are known at parse time — bindings shadow PATH
// commands by construction (`let cat = ...` then `cat x` is an
// application; ^cat forces the binary) [D:assume-resolver].
// FParsec dumps embed the assembled logical line — never what the user
// wrote. Strip every snippet+caret block, keep the diagnostic text,
// and translate embedded positions to physical line/col
// [D:clean-parse-dump].
let private cleanParseDump (ll: LogicalLine) (msg: string) : string =
    // no-leak [D:sibling-sentinel]: FParsec may echo the assembled line or
    // list the sentinel as an expected token; the machine sentinel must
    // never surface — render both the raw char and FParsec's  escape
    // (its expected-set rendering) as ';'
    let msg =
        msg
            .Replace(Parser.sibSepStr, ";")
            .Replace("\\u001f", ";")
            .Replace(Parser.fieldSepStr, ";")
            .Replace("\\u001d", ";")

    let lines = msg.Replace("\r\n", "\n").Split('\n') |> Array.toList

    let isCaret (l: string) =
        l.Trim() <> "" && l.Trim() |> Seq.forall ((=) '^')

    let translateErrorLine (l: string) : string option =
        let m = System.Text.RegularExpressions.Regex.Match(l, @"Error in Ln: 1 Col: (\d+)")

        if m.Success then
            let pl, pc = translate ll (int m.Groups[1].Value)
            Some($"at line {pl}, col {pc}:")
        else
            None

    let rec go (first: bool) (acc: string list) (rest: string list) =
        match rest with
        | [] -> List.rev acc
        | l :: tail ->
            match translateErrorLine l with
            | Some pos ->
                // drop the snippet rows up to and including the caret
                let indent = l |> Seq.takeWhile ((=) ' ') |> Seq.length

                let rec dropSnippet r =
                    match r with
                    | [] -> []
                    | x :: xs when isCaret x -> xs
                    | _ :: xs -> dropSnippet xs

                // the first position is the diag's own — consumers render
                // it with the source line; only backtrack positions stay
                let acc' = if first then acc else (String(' ', indent) + pos) :: acc
                go false acc' (dropSnippet tail)
            | None -> go first (l :: acc) tail

    // internal backtrack labels never surface [D:label-leaks]: drop any
    // line carrying the marker (raw or FParsec-escaped), then a heading
    // whose whole section emptied
    // openers (header/position lines ending with ':') whose section
    // emptied are dropped too — bottom-up: an opener survives only if
    // the next surviving line is its content (>= indent, non-opener) or
    // a nested opener (> indent)
    let isOpener (l: string) = l.TrimEnd().EndsWith ":"

    let indentOf (l: string) =
        l |> Seq.takeWhile ((=) ' ') |> Seq.length

    let rec dropEmptyOpeners (ls: string list) =
        match ls with
        | [] -> []
        | h :: tail ->
            let kept = dropEmptyOpeners tail

            if isOpener h then
                match kept with
                | next :: _ when
                    (isOpener next && indentOf next > indentOf h)
                    || (not (isOpener next) && indentOf next >= indentOf h)
                    ->
                    h :: kept
                | _ -> kept
            else
                h :: kept

    // rider D3: the foreign words (`while`/`return`/`try`/`def`) are reserved
    // only to explain that weir lacks them, so offering them as tokens the
    // parser expects is backwards. Strip them, consulting Parser's one list.
    // FParsec wraps a long list across lines, so rejoin first — otherwise a
    // token straddling the break is missed, and the list reads better whole.
    // `!` likewise: a guard that only teaches `not` (prefix `!` is not weir)
    let bannedTokens =
        Parser.foreignKeywordWords
        |> List.map (fun w -> "'" + w + "'")
        |> Set.ofList
        |> Set.add "'!'"

    let rebuildExpecting (ls: string list) : string list =
        // FParsec wraps the list at whatever width it likes — sometimes
        // comma-first, sometimes token-first — so a prefix test misses
        // continuations and the banned words survive on the tail line.
        // A continuation is instead any following line that is not a
        // section opener: openers end with ':'.
        let isCont (l: string) =
            l.Trim() <> "" && not (l.TrimEnd().EndsWith ":")

        let rec walk acc rest =
            match rest with
            | [] -> List.rev acc
            | (l: string) :: tail when l.Contains "Expecting:" ->
                let conts = tail |> List.takeWhile isCont
                let after = tail |> List.skip (List.length conts)

                let joined =
                    String.concat " " (l :: (conts |> List.map (fun (c: string) -> c.Trim())))
                // normalise the seam so ", " splitting works wherever it wrapped
                let whole =
                    System.Text.RegularExpressions.Regex.Replace(joined, @"\s+", " ").Replace(" ,", ",")

                let idx = whole.IndexOf "Expecting:"
                let head = whole.Substring(0, idx + "Expecting:".Length)
                let body = whole.Substring(idx + "Expecting:".Length)

                let toks =
                    body.Split([| ", "; " or " |], System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun (t: string) -> t.Trim())
                    |> Array.filter (fun t -> t <> "" && not (bannedTokens.Contains t))
                    |> List.ofArray
                    // two alternatives can carry the same relabelled token
                    // [D:clean-parse-dump]: the separator renders `';'` from
                    // more than one branch, so the set read `';' or ';'` —
                    // collapse to distinct, first spelling kept
                    |> List.distinct

                let rendered =
                    match toks with
                    | [] -> head
                    | [ one ] -> head + " " + one
                    | many ->
                        head
                        + " "
                        + String.concat ", " (List.truncate (many.Length - 1) many)
                        + " or "
                        + List.last many

                walk (rendered :: acc) after
            | l :: tail -> walk (l :: acc) tail

        walk [] ls

    // rider D5: FParsec's backtracking trace is the parser talking to its
    // author. `go` already dropped the diagnostic's own position (consumers
    // render that with the source line), so any position line still standing
    // is a backtrack position — drop those and FParsec's EOF note, and the
    // "The parser backtracked after:" opener empties and dropEmptyOpeners
    // takes it.
    //
    // Kept under WEIR_LOG=debug: backtrack positions tell weir's own
    // developers where the grammar gave up — the wrong part was the
    // audience, not the information, so this hides rather than deletes.
    // `Unknown Error(s)` is FParsec claiming an error while having
    // nothing to say — dropped too, since that content alone would keep
    // the backtrack section (and its opener) alive after the position
    // lines go.
    let isBacktrackNoise (l: string) =
        let t = l.Trim()

        t.StartsWith "Note: The error occurred"
        || t = "Unknown Error(s)"
        || System.Text.RegularExpressions.Regex.IsMatch(t, @"^at line \d+, col \d+:$")

    go true [] lines
    |> rebuildExpecting
    |> List.filter (fun l ->
        not (l.Contains Parser.internalLabelMarker || l.Contains "\\u0006")
        && not (not (Builtins.debugEnabled ()) && isBacktrackNoise l)
        && l.Trim() <> "")
    |> dropEmptyOpeners
    // the dump quotes source text (a head, an excerpt) [D:error-data-escape]
    |> List.map quoteData
    |> String.concat "\n"

// ---- transitive types [D:transitive-types] -------------------------------
// a type reached only through another module's signatures holds its plain
// name while nothing else wants it; a local declaration or a direct
// import of another same-named type takes the name, and the transitive
// one moves to its origin-qualified name ("A.Pt") everywhere in the env,
// so its values keep their type

let private renameTy (m: System.Collections.Generic.IReadOnlyDictionary<string, string>) (ty: Ty) : Ty =
    let same (a: obj) (b: obj) = obj.ReferenceEquals(a, b)

    let rec go ty =
        match ty with
        | TNamed(n, args) ->
            let n' =
                match m.TryGetValue n with
                | true, k -> k
                | _ -> n

            let args' = goList args

            if same n' n && same args' args then
                ty
            else
                TNamed(n', args')
        | TFun(a, b) ->
            let a' = go a
            let b' = go b
            if same a' a && same b' b then ty else TFun(a', b')
        | TSeq t ->
            let t' = go t
            if same t' t then ty else TSeq t'
        | TTuple ts ->
            let ts' = goList ts
            if same ts' ts then ty else TTuple ts'
        | TRowVar(r, fields) ->
            let fields' = goFields fields
            if same fields' fields then ty else TRowVar(r, fields')
        | t -> t

    and goList (ts: Ty list) : Ty list = Weir.Check.mapShared go ts

    and goFields (fs: (string * Ty) list) : (string * Ty) list =
        fs
        |> Weir.Check.mapShared (fun ((f, t) as x) ->
            let t' = go t
            if same t' t then x else (f, t'))

    if m.Count = 0 then ty else go ty

let private renameScheme (m: System.Collections.Generic.IReadOnlyDictionary<string, string>) (sch: Scheme) : Scheme =
    let ty = renameTy m sch.Ty

    if obj.ReferenceEquals(ty, sch.Ty) then
        sch
    else
        { sch with Ty = ty }

let private renameDef (m: System.Collections.Generic.IReadOnlyDictionary<string, string>) (def: TypeDef) : TypeDef =
    let changed (a: Ty) (b: Ty) = not (obj.ReferenceEquals(a, b))

    match def with
    | Record rd ->
        let fields = rd.Fields |> List.map (fun (f, t) -> f, renameTy m t)

        if List.exists2 (fun (_, a) (_, b) -> changed a b) fields rd.Fields then
            Record { rd with Fields = fields }
        else
            def
    | Union u ->
        let cases = u.Cases |> List.map (fun (c, p) -> c, p |> Option.map (renameTy m))

        let caseChanged (_, a) (_, b) =
            match a, b with
            | Some a, Some b -> changed a b
            | _ -> false

        if List.exists2 caseChanged cases u.Cases then
            Union { u with Cases = cases }
        else
            def

let private renameEnvTypes (m: Map<string, string>) (tenv: TypeEnv) : TypeEnv =
    if Map.isEmpty m then
        tenv
    else
        let key n =
            Map.tryFind n m |> Option.defaultValue n

        {
            tenv with
                Values = tenv.Values |> Map.map (fun _ sch -> renameScheme m sch)
                Modules =
                    tenv.Modules
                    |> Map.map (fun _ ms -> ms |> Map.map (fun _ sch -> renameScheme m sch))
                ModulePrivate =
                    tenv.ModulePrivate
                    |> Map.map (fun _ ms -> ms |> Map.map (fun _ sch -> renameScheme m sch))
                Types =
                    tenv.Types
                    |> Map.toList
                    |> List.map (fun (n, d) -> key n, renameDef m d)
                    |> Map.ofList
                ModuleTypes = tenv.ModuleTypes |> Map.map (fun _ owned -> owned |> Map.map (fun _ k -> key k))
                TypeOrigins =
                    tenv.TypeOrigins
                    |> Map.toList
                    |> List.map (fun (n, o) -> key n, o)
                    |> Map.ofList
        }

// "A.Pt@/abs/a.weir" -> "A.Pt", made unique against the env's types
let private qualifiedFree (tenv: TypeEnv) (origin: string) : string =
    let at = origin.IndexOf '@'
    let q = if at > 0 then origin.Substring(0, at) else origin

    let rec free (c: string) =
        if Map.containsKey c tenv.Types then free (c + "'") else c

    free q

let private isTransitive (tenv: TypeEnv) (n: string) =
    tenv.TypeOrigins.ContainsKey n
    && not (
        tenv.ModuleTypes
        |> Map.exists (fun _ owned -> owned |> Map.exists (fun _ k -> k = n))
    )

/// a transitive type named `n` gives its plain name up (see above)
let yieldTransitive (n: string) (tenv: TypeEnv) : TypeEnv =
    if isTransitive tenv n then
        renameEnvTypes (Map [ n, qualifiedFree tenv tenv.TypeOrigins[n] ]) tenv
    else
        tenv

// "A" + "T" -> "A.T", made unique against the env's types and `taken`
let private qualifiedKey (tenv: TypeEnv) (taken: Set<string>) (alias: string) (n: string) : string =
    let rec free (c: string) =
        if Map.containsKey c tenv.Types || taken.Contains c then
            free (c + "'")
        else
            c

    free (alias + "." + n)

/// an imported type holding the plain name `n` gives it up: a transitive
/// one to its origin-qualified name, an import's own one to its
/// alias-qualified key [D:qualified-type-names]
let yieldImported (n: string) (tenv: TypeEnv) : TypeEnv =
    let tenv = yieldTransitive n tenv

    match
        tenv.ModuleTypes
        |> Map.tryFindKey (fun _ owned -> owned |> Map.exists (fun plain k -> plain = n && k = n))
    with
    | Some holder when tenv.TypeOrigins.ContainsKey n ->
        renameEnvTypes (Map [ n, qualifiedKey tenv Set.empty holder n ]) tenv
    | _ -> tenv

// merge a loaded module into the importer's env [D:modules-v1]: values
// (and union ctors) under Modules[Alias], types under their key, and the
// alias's name -> key table under ModuleTypes. An own type keys by its
// plain name while no other import declares that name; when one does,
// both move to their qualified keys ("A.T", "B.T") and the bare name
// resolves to neither [D:qualified-type-names]; a local declaration keeps
// the bare name and the import's type is reached qualified. Its
// transitive types [D:transitive-types] join an existing entry of the
// same origin, else take the plain name if free, else the qualified one.
let private mergeModule (tenv: TypeEnv) (lm: LoadedModule) : TypeEnv =
    let originOf n =
        lm.TypeOrigins |> List.tryFind (fst >> (=) n) |> Option.map snd

    let own = Set.ofList lm.TypeNames

    // the first key, in key order, holding origin o
    let keyOfOrigin (t: TypeEnv) (o: string) =
        t.TypeOrigins |> Map.tryPick (fun k held -> if held = o then Some k else None)

    // the alias holding a type named n under its plain key, if any
    let plainHolder (t: TypeEnv) (n: string) =
        t.ModuleTypes
        |> Map.tryPick (fun a owned ->
            match Map.tryFind n owned with
            | Some k when k = n -> Some a
            | _ -> None)

    // whether any alias declares a type named n
    let anyOwner (t: TypeEnv) (n: string) =
        t.ModuleTypes |> Map.exists (fun _ owned -> Map.containsKey n owned)

    // each own type claims its key, moving another import's holder of the
    // plain name to that holder's qualified key
    let tenv, ownKeys, ownTaken =
        lm.TypeNames
        |> List.fold
            (fun (t: TypeEnv, keys: Map<string, string>, taken: Set<string>) n ->
                let claim (t: TypeEnv) k =
                    let keys' = Map.add n k keys

                    let taken' =
                        if Map.containsKey n keys then
                            keys' |> Map.values |> Set.ofSeq
                        else
                            Set.add k taken

                    t, keys', taken'

                match originOf n |> Option.bind (keyOfOrigin t) with
                | Some existing -> claim t existing
                | None ->
                    let t = yieldTransitive n t

                    if Map.containsKey n t.Types then
                        match plainHolder t n with
                        | Some holder ->
                            let t = renameEnvTypes (Map [ n, qualifiedKey t taken holder n ]) t
                            claim t (qualifiedKey t taken lm.Alias n)
                        | None -> claim t (qualifiedKey t taken lm.Alias n)
                    elif anyOwner t n then
                        claim t (qualifiedKey t taken lm.Alias n)
                    else
                        claim t n)
            (tenv, Map.empty, Set.empty)

    let subst =
        let acc = System.Collections.Generic.Dictionary<string, string>()
        let taken = System.Collections.Generic.HashSet<string>(ownTaken)

        // an anonymous shape keys by its canonical spelling everywhere —
        // one type, never qualified [D:anon-literals]
        for n, _ in lm.TypeDefs do
            if not (own.Contains n) && not (n.StartsWith "{|") then
                let o = originOf n |> Option.defaultValue n

                match keyOfOrigin tenv o with
                | Some existing -> acc[n] <- existing
                | None when
                    not (Map.containsKey n tenv.Types)
                    && not (own.Contains n)
                    && not (taken.Contains n)
                    && not (tenv.ModuleTypes |> Map.exists (fun _ owned -> Map.containsKey n owned))
                    ->
                    taken.Add n |> ignore
                | None ->
                    let q = qualifiedFree tenv o

                    let rec unique (c: string) =
                        if taken.Contains c then unique (c + "'") else c

                    let q = unique q
                    acc[n] <- q
                    taken.Add q |> ignore

        for KeyValue(n, k) in ownKeys do
            if n <> k then
                acc[n] <- k

        acc

    let rename n =
        match subst.TryGetValue n with
        | true, k -> k
        | _ -> n

    let typeDefs =
        lm.TypeDefs
        |> List.choose (fun (n, d) ->
            let n' = rename n

            // joined an existing same-origin entry: nothing to add
            match Map.tryFind n' tenv.TypeOrigins, originOf n with
            | Some held, Some o when held = o -> None
            | _ when n.StartsWith "{|" && Map.containsKey n tenv.Types -> None
            | _ when n' <> n && Map.containsKey n' tenv.Types && not (own.Contains n) -> None
            | _ -> Some(n', renameDef subst d))

    let origins = lm.TypeOrigins |> List.map (fun (n, o) -> rename n, o)

    {
        tenv with
            Modules =
                Map.add
                    lm.Alias
                    (lm.Members |> List.map (fun (n, s) -> n, renameScheme subst s) |> Map.ofList)
                    tenv.Modules
            Types = typeDefs |> List.fold (fun ts (n, d) -> Map.add n d ts) tenv.Types
            ModuleTypes = Map.add lm.Alias ownKeys tenv.ModuleTypes
            TypeOrigins = origins |> List.fold (fun m (n, o) -> Map.add n o m) tenv.TypeOrigins
            ModulePrivate =
                Map.add
                    lm.Alias
                    (lm.PrivateMembers
                     |> List.map (fun (n, s) -> n, renameScheme subst s)
                     |> Map.ofList)
                    tenv.ModulePrivate
    }

let rec private checkStatementCore
    (gateExprs: bool)
    (sigCtx: SigContext option)
    (mkR: TypeEnv -> Parser.Resolver)
    (loadImport: ImportLoader)
    (tenv: TypeEnv)
    (ll: LogicalLine)
    : Result<CheckedStatement, StmtDiag> =
    let r = mkR tenv

    let typed (tag: StmtTag) (terr: Check.TypeError) =
        // an Origin is already physical, recorded by the statement that
        // owned the access [D:row-provenance] — it bypasses translate
        let physLine, physCol, physEnd =
            match terr.Origin with
            | Some(ol, oc, len) -> ol, oc, Some(ol, oc + len)
            | None ->
                let pl, pc = translate ll terr.Span.Start.Col
                pl, pc, Some(translate ll terr.Span.End.Col)

        {
            PhysLine = physLine
            PhysCol = physCol
            PhysEnd = physEnd
            Tag = Some tag
            HasCol = true
            Span = Some terr.Span
            Parse = false
            Message = terr.Message
            File = None
            Note = None
            Related = []
            Warnings = []
        }

    let warningsOf te = [
        for w in Check.warnings te do
            let physLine, physCol = translate ll w.Span.Start.Col
            physLine, physCol, w.Message
    ]

    // physical translator for the checker's boundary-crossing
    // positions [D:row-provenance]; reset so non-statement consumers
    // (Complete, tests) never see a stale statement
    Check.toPhys.Value <- Some(translate ll)

    // a command head not on PATH, named precisely: the parse path's dump
    // and the runner's "unbound variable" for an unknown statement head both
    // come down to a missing program [D:missing-command-diagnosis]
    let missingCommandDiagWith (exact: bool) (onlyName: (string * bool) option) : StmtDiag option =
        let missingHeads =
            match Parser.parseLineFull (assumeResolver tenv) ll.Text with
            | Error _ -> []
            | Ok stmt ->
                let e =
                    match stmt with
                    | SLet(_, e)
                    | SLetPat(_, e)
                    | SExpr e
                    | SCmd e -> Some e
                    | SType _
                    | SSig _
                    | SModule _
                    | SImport _ -> None

                let rec heads (e: Expr) =
                    (match e.Kind with
                     | ECmd(HeadLit prog, _, _) when not (Extern.exists prog) -> [ prog, e.Span ]
                     | _ -> [])
                    @ (chainStageHeads e |> List.filter (fst >> Extern.exists >> not))
                    @ (reifiedHead e |> Option.filter (fst >> Extern.exists >> not) |> Option.toList)
                    @ (exprChildren e |> List.collect heads)

                e |> Option.map heads |> Option.defaultValue []

        // the runner reads a dashed head as subtraction (`no-such-tool` as
        // `no - …`) and reports its first segment unbound — no weir name has
        // a dash, so it is the program even past a near-miss suggestion
        // [D:missing-command-diagnosis]
        let namesHead (n: string, suggested: bool) (prog: string) =
            (n = prog && not suggested) || (not exact && prog.StartsWith(n + "-"))

        match missingHeads with
        | (prog, span) :: rest when
            (match onlyName with
             | Some n -> namesHead n prog
             | None -> true)
            ->
            let physLine, physCol = translate ll span.Start.Col

            let others =
                match rest |> List.map fst |> List.distinct |> List.filter ((<>) prog) with
                | [] -> ""
                | more ->
                    let joined = String.concat ", " more
                    $" (also missing: {joined})"

            Some {
                PhysLine = physLine
                PhysCol = physCol
                PhysEnd = Some(translate ll span.End.Col)
                Tag = None
                HasCol = true
                Span = Some span
                Parse = true
                Message =
                    (match Map.tryFind prog Builtins.bareAliasHomes with
                     | Some home ->
                         $"'{prog}' is a bare module member, not a program — spell it '{home}.{prog}' (bare names live in the REPL session)"
                     | None ->
                         // a misspelled reifier after `|` reads as a program [D:reifier-term]
                         let afterPipe =
                             let at = min ll.Text.Length (max 0 (span.Start.Col - 1))
                             ll.Text.Substring(0, at).TrimEnd().EndsWith "|"

                         let reifier =
                             Ast.reifierNames
                             |> List.map (fun c -> c, editDistance prog c)
                             |> List.filter (fun (_, d) -> d <= 2)
                             |> List.sortBy snd
                             |> List.tryHead

                         match reifier with
                         // an exact reifier read as a program: the position
                         // refuses the chain, not the spelling [D:statement-lets]
                         | Some(c, 0) when afterPipe ->
                             $"'{c}' is a reifier, but this position is an expression — wrap the chain: $(<command> | {c}); bare command lets belong to statement bodies (top level, if/for/match arms, within blocks)"
                         | Some(c, _) when afterPipe ->
                             $"unknown command '{prog}' — not found on PATH. Did you mean the reifier '{c}'? (`<command> | {c}`)"
                         // a path head [D:path-head-warn]: nothing to install
                         | _ when Extern.isPathy prog ->
                             $"no executable at {prog}{others} — weir resolves command names before running; if the script creates it, name it at run time — `let tool = \"{prog}\"` then `^$tool`"
                         | _ ->
                             $"unknown command '{prog}' — not found on PATH{others}. weir resolves command names before running: install the tool, or, if it only exists when the script runs, name it at run time — `let tool = \"{prog}\"` then `^$tool`")
                File = None
                Note = None
                Related = []
                Warnings = []
            }
        | _ -> None

    let missingCommandDiag = missingCommandDiagWith false

    // an "unbound variable" that is really a missing program — a let's
    // command body inside a function reads as an application under the
    // runner's hard resolver — gets the unknown-command teaching; the
    // name must be the whole head, so `let y = lenth-1` stays arithmetic
    // [D:missing-command-diagnosis]
    let typedOrMissing (tag: StmtTag) (terr: Check.TypeError) =
        let unboundName =
            if terr.Message.StartsWith "unbound variable '" then
                Some(terr.Message.Substring(18).Split('\'').[0], terr.Message.Contains "Did you mean")
            else
                None

        match unboundName |> Option.bind (fun n -> missingCommandDiagWith true (Some n)) with
        | Some d -> Error d
        | None -> Error(typed tag terr)

    try
        match Parser.parseLineFull r ll.Text with
        | Error f ->
            // FParsec's primary error is often irrelevant when the real cause
            // is an unresolvable command head (the backtrack note buries it).
            // Retry under the assume-resolver: if that parses, the failure is
            // missing commands — name them precisely instead of the dump.
            // check reads a word that is no program as a command head
            // (assume-resolver); the runner reads it as an expression, so
            // a `|>`-into-a-program refusal of it is check's alone — take
            // the runner's verdict instead, so check and run agree
            // (`xs |> contains x` is module-qualified, not a program)
            // [D:assume-resolver]
            let runnersVerdict =
                if f.Message.StartsWith "'|>' applies functions" then
                    match Parser.parseLineFull (resolver tenv) ll.Text with
                    | Ok _ ->
                        match checkStatementCore gateExprs sigCtx resolver loadImport tenv ll with
                        | Error d -> Some d
                        | Ok _ -> None
                    | Error _ -> None
                else
                    None

            match missingCommandDiag None |> Option.orElse runnersVerdict with
            | Some d -> Error d
            | None ->
                let physLine, physCol, hasCol =
                    match f.Col with
                    | Some col ->
                        let l, c = translate ll col
                        l, c, true
                    | None -> ll.Head, 1, false

                Error {
                    PhysLine = physLine
                    PhysCol = physCol
                    PhysEnd = None
                    Tag = None
                    HasCol = hasCol
                    Span = None
                    Parse = true
                    Message = cleanParseDump ll f.Message
                    File = None
                    Note = None
                    Related = []
                    Warnings = []
                }
        | Ok(SType decl) ->
            // a local declaration takes its name from a transitive type
            // [D:transitive-types], and in the REPL from an import's own
            // type, which stays reachable qualified [D:qualified-type-names]
            // (a script refuses the redeclaration: dup-type-decl)
            match Check.checkDecl (yieldImported decl.Name tenv) decl with
            | Error terr -> Error(typed StmtTag.Type terr)
            | Ok tenv' ->
                // the `validate` block types against the declared type
                // [D:validated-types] — its purity is the post-check layer's
                let validator =
                    match decl.Validate, Types.typeDefFor tenv' decl.Name with
                    | Some v, Some(Union { Validated = Some id }) ->
                        Check.typecheckValidator tenv' decl.Name v
                        |> Result.map (fun te -> Some(id, te))
                    | _ -> Ok None

                match validator with
                | Error terr -> Error(typed StmtTag.Type terr)
                | Ok validator ->
                    Ok {
                        Kind = KType(decl, validator)
                        Env = tenv'
                        Warnings = []
                    }
        | Ok(SSig(name, ty, nameSpan)) ->
            let sigErr msg =
                Error(
                    typed StmtTag.Let {
                        Span = nameSpan
                        Message = msg
                        Origin = None
                    }
                )

            match sigCtx with
            | None ->
                // scripts infer [D:module-signatures] — the API-surface rule
                // is a module rule; a script's lets need no export marker
                sigErr
                    $"signatures belong to module APIs — scripts infer: drop the signature (let {name} = …), or move this into a module"
            | Some sc ->
                match Check.checkBinderName nameSpan name with
                | Error terr -> Error(typed StmtTag.Let terr)
                | Ok() ->
                    if Map.containsKey name sc.Pending then
                        sigErr $"duplicate signature for '{name}'"
                    elif Set.contains name sc.Signed then
                        sigErr $"'{name}' already has a signature and an implementation — one signature per member"
                    elif Set.contains name sc.Implemented then
                        sigErr
                            $"the signature for '{name}' comes after its implementation — declare the signature above it"
                    else
                        match Check.validateSigTy tenv nameSpan ty with
                        | Error terr -> Error(typed StmtTag.Let terr)
                        | Ok(tenv', ty) ->
                            let line, col = translate ll nameSpan.Start.Col

                            Ok {
                                Kind =
                                    KSig(
                                        name,
                                        {
                                            Ty = ty
                                            Scheme = generalize ty
                                            Line = line
                                            Col = col
                                        }
                                    )
                                Env = tenv'
                                Warnings = []
                            }
        | Ok(SLetPat({ PKind = PWildcard } as pat, _)) when gateExprs ->
            // a bare `_` binder discards anonymously — binding is how
            // weir silences raise-on-nonzero, so the discard gets a name
            // [D:unused-bindings]; gateExprs scopes the refusal with the
            // discard family (the REPL and -e keep their echo regime)
            Error(
                typed StmtTag.LetPat {
                    Span = pat.PSpan
                    Message =
                        "a bare '_' binder discards its value anonymously — name the discard (let _r = …) so the intent survives a grep"
                    Origin = None
                }
            )
        | Ok(SLetPat(pat, e)) ->
            // a destructuring let cannot implement a signature
            // [D:module-signatures] — pairing is by the plain name
            let sigClash =
                sigCtx
                |> Option.bind (fun sc ->
                    let rec patVars (p: Pattern) =
                        match p.PKind with
                        | PVar n -> [ n ]
                        | PTuple ps -> ps |> List.collect patVars
                        | PRecord fields -> fields |> List.map snd |> List.collect patVars
                        | PCase(_, Some inner, _) -> patVars inner
                        | _ -> []

                    patVars pat
                    |> List.tryFind (fun n -> Map.containsKey n sc.Pending || Set.contains n sc.Signed))

            match sigClash with
            | Some n ->
                Error(
                    typed StmtTag.LetPat {
                        Span = pat.PSpan
                        Message =
                            $"'{n}' has a signature, and a signature pairs with a plain implementation — write 'let {n} … = …'"
                        Origin = None
                    }
                )
            | None ->
                // anonymous adapter shapes persist their defs [D:anon-records]
                let tenv = Check.withAnonDefs tenv e

                match Check.typecheckBinder tenv pat e with
                | Error terr -> typedOrMissing StmtTag.LetPat terr
                | Ok(te, schemes) ->
                    Ok {
                        Kind = KLetPat(pat, schemes, te)
                        Env = schemes |> List.fold (fun e (n, sch) -> bindValue n sch e) tenv
                        Warnings = warningsOf te
                    }
        | Ok(SLet(name, e)) ->
            // SLet carries the name as a bare string, so re-derive its own
            // columns from the statement text (grammar: ws `let` ws name)
            // [D:squiggle-on-binder]
            let nameSpan =
                let m = Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+")

                if m.Success then
                    {
                        Start = { Line = 1; Col = m.Length + 1 }
                        End = {
                            Line = 1
                            Col = m.Length + 1 + name.Length
                        }
                    }
                else
                    e.Span

            let tenv = Check.withAnonDefs tenv e

            match sigCtx |> Option.bind (fun sc -> Map.tryFind name sc.Pending) with
            | Some sd ->
                // check-against-sig [D:module-signatures]: the signature's
                // types flow into the implementation's checking; the
                // exported scheme is the signature's (plus the
                // implementation's constraint residue)
                // the signature line rides as related [D:diagnostic-tags]
                let sigErrAt (span: Span) msg =
                    Error {
                        typed StmtTag.Let {
                            Span = span
                            Message = msg
                            Origin = None
                        } with
                            Related = [ sd.Line, sd.Col, $"the signature declares {formatTy sd.Ty}" ]
                    }

                match Check.checkBinderName nameSpan name with
                | Error terr -> Error(typed StmtTag.Let terr)
                | Ok() ->
                    match Check.typecheckAgainstSig tenv sd.Ty e with
                    | Error(Check.SigBody terr) -> typedOrMissing StmtTag.Let terr
                    | Error(Check.SigShape(implTy, span)) ->
                        // a body ending in a command returns its output
                        // where the signature says unit [D:module-signatures]
                        let rec result (t: Ty) =
                            match t with
                            | TFun(_, r) -> result r
                            | t -> t

                        let rec endsInCommand (x: Expr) =
                            match x.Kind with
                            | ELambda(_, _, b)
                            | ELambdaPat(_, b)
                            | ELet(_, _, _, b)
                            | ELetPat(_, _, b)
                            | ESeq(_, b) -> endsInCommand b
                            | ECmd _ -> true
                            | EPipe(_, stage) ->
                                (match stage.Kind with
                                 | ECmd _ -> true
                                 | _ -> false)
                            | _ -> false

                        let hint =
                            match result sd.Ty, result implTy with
                            | TUnit, TSeq TStr when endsInCommand e ->
                                " — its last line is a command, so it returns the command's output (seq<string>); end the body with `()`"
                            | _ -> ""

                        sigErrAt
                            span
                            ($"the implementation of '{name}' does not match its signature: "
                             + $"the signature (line {sd.Line}) declares {formatTy sd.Ty}, but the implementation is {formatTy implTy}"
                             + hint)
                    | Error(Check.SigPinned(v, pinnedTy, span)) ->
                        sigErrAt
                            span
                            ($"the implementation of '{name}' is less general than its signature: "
                             + $"'{v} (line {sd.Line}) is pinned to {formatTy pinnedTy} here — generalize the implementation, or narrow the signature")
                    | Error(Check.SigMerged(v1, v2, span)) ->
                        sigErrAt
                            span
                            ($"the implementation of '{name}' is less general than its signature: "
                             + $"'{v1} and '{v2} (line {sd.Line}) are forced to one type here — the signature promises they stay independent")
                    | Ok(te, scheme) ->
                        Ok {
                            Kind = KLet(name, scheme, te)
                            Env = bindValue name scheme tenv
                            Warnings = warningsOf te
                        }
            | None ->
                match sigCtx with
                | Some sc when Set.contains name sc.Signed ->
                    // paired already — a second implementation would let the
                    // last binding win at replay while the export keeps the
                    // signature's, so the split is refused
                    Error(
                        typed StmtTag.Let {
                            Span = nameSpan
                            Message =
                                $"'{name}' already implements its signature — one implementation per signed member"
                            Origin = None
                        }
                    )
                | _ ->
                    match
                        Check.checkBinderName nameSpan name
                        |> Result.bind (fun () -> Check.typecheckWith tenv e)
                    with
                    | Error terr -> typedOrMissing StmtTag.Let terr
                    | Ok(te, cs, origins, holeDefaults) ->
                        let scheme = generalizeWithOrigins cs origins holeDefaults te.Ty

                        Ok {
                            Kind = KLet(name, scheme, te)
                            Env = bindValue name scheme tenv
                            Warnings = warningsOf te
                        }
        | Ok(SCmd e) ->
            let tenv = Check.withAnonDefs tenv e

            match Check.typecheck tenv e with
            | Error terr ->
                // a near-miss binding suggestion means a likely typo of a
                // weir name: keep it; otherwise the head is a missing program
                let unboundName =
                    if terr.Message.StartsWith "unbound variable '" then
                        Some(terr.Message.Substring(18).Split('\'').[0], terr.Message.Contains "Did you mean")
                    else
                        None

                match unboundName |> Option.bind (fun n -> missingCommandDiag (Some n)) with
                | Some d -> Error d
                | None -> Error(typed StmtTag.Cmd terr)
            | Ok te ->
                // a bool-valued chain (| succeeds) as a bare statement is a
                // discarded value, not a stream — the discard family
                // [D:exit-reifiers]. `| complete` (a Completed record) joins
                // it: same mistake, keyed on the distinctive value type.
                let rec exitCodeSpine (t: Check.TypedExpr) =
                    match t.Kind with
                    | Check.TEVar("|exitCoded" | "|exitCodedEnv") -> true
                    | Check.TEApp(f, _) -> exitCodeSpine f
                    | _ -> false

                // the reifier desugar's span starts at the reifier stage
                // (the caret's place) but ends where the command did — before
                // its start; it runs on to the statement's last node
                let wholeSpan =
                    let rec spans (t: Check.TypedExpr) =
                        t.Span :: (Check.childExprs t |> List.collect spans)

                    let key (p: Pos) = p.Line, p.Col
                    let all = spans te |> List.filter (fun sp -> sp.Start.Line > 0)

                    match all with
                    | [] -> te.Span
                    | _ ->
                        let last = all |> List.map (fun sp -> sp.End) |> List.maxBy key

                        if key last > key te.Span.End then
                            { te.Span with End = last }
                        else
                            te.Span

                // where a value is echoed (the REPL, -e) a reified command
                // is a value like any other: `cmd | complete` shows its
                // record, as the value-headed spelling already did
                let reified =
                    match te.Ty with
                    | TBool
                    | TNamed("Completed", _) -> true
                    | TInt -> exitCodeSpine te
                    | _ -> false

                match te.Ty with
                | _ when reified && not gateExprs ->
                    Ok {
                        Kind = KExpr te
                        Env = tenv
                        Warnings = warningsOf te
                    }
                | TBool ->
                    Error(
                        typed StmtTag.Cmd {
                            Span = wholeSpan
                            Message =
                                "this statement computes a bool and discards it — bind it "
                                + "(let ok = ... | succeeds) or use it in a condition"
                            Origin = None
                        }
                    )
                // `set +e; cmd; rc=$?` muscle memory lands here [D:exit-reifiers]
                | TInt when exitCodeSpine te ->
                    Error(
                        typed StmtTag.Cmd {
                            Span = wholeSpan
                            Message =
                                "this statement computes the exit code and discards it — bind it "
                                + "(let rc = <command> | exitCode), match on it, or drop '| exitCode' if you don't need the code"
                            Origin = None
                        }
                    )
                // `| complete` captures a Completed record — reading it is
                // the point, so discarding it is the family mistake
                | TNamed("Completed", _) ->
                    Error(
                        typed StmtTag.Cmd {
                            Span = wholeSpan
                            Message =
                                "this statement computes a Completed record and discards it — bind it "
                                + "(let r = ... | complete) or read a field (.exitCode, .stdout)"
                            Origin = None
                        }
                    )
                | _ ->
                    Ok {
                        Kind = KCmd te
                        Env = tenv
                        Warnings = warningsOf te
                    }
        | Ok(SExpr e) ->
            // statement position demands unit, so a commandish tail arms
            // [D:within-scopes] — reaching through scopes and let-ins.
            // The REPL (gateExprs=false) arms block tails too, so a block's
            // final command inherits the terminal as a top-level one does
            // [D:armed-inherit]; a bare top-level chain stays unarmed for
            // the REPL's own inherit path and its streamed teaching
            let e =
                match e.Kind with
                | _ when gateExprs -> Check.armTail e
                | ECmd _
                | EPipe(_, { Kind = ECmd _ }) -> e
                | _ -> Check.armTail e

            let tenv = Check.withAnonDefs tenv e

            match Check.typecheck tenv e with
            | Error terr ->
                // a near-miss binding suggestion means a likely typo of a
                // weir name: keep it; otherwise the head is a missing program
                let unboundName =
                    if terr.Message.StartsWith "unbound variable '" then
                        Some(terr.Message.Substring(18).Split('\'').[0], terr.Message.Contains "Did you mean")
                    else
                        None

                match unboundName |> Option.bind (fun n -> missingCommandDiag (Some n)) with
                | Some d -> Error d
                | None -> Error(typed StmtTag.Expr terr)
            | Ok te ->
                // the likeliest intent behind a discarded $(cmd) is "run
                // it" — the wrapper is what is in the way, so the error
                // names the drop [D:district-retirement] (the wrap-it
                // hint's principle, inverted)
                let dropClause = Check.captureDropHint e

                // a diverging tail makes no value to discard
                // [D:fail-bottom]: its fresh var passes the statement gate
                let gated =
                    match te.Ty with
                    | TVar _ when Check.divergesTo e -> None
                    // a discarded partial application names what is
                    // missing [D:partial-echo]; an uncalled unit function
                    // keeps its own lesson
                    | TFun(dom, _) as ty when gateExprs && dom <> TUnit ->
                        partialDiscard tenv te |> Option.orElse (discardError ty)
                    | ty -> if gateExprs then discardError ty else None

                match gated with
                | Some msg ->
                    let msg = msg + dropClause

                    Error {
                        typed StmtTag.Expr {
                            Span = e.Span
                            Message = msg
                            Origin = None
                        } with
                            Warnings = warningsOf te
                    }
                | None ->
                    Ok {
                        Kind = KExpr te
                        Env = tenv
                        Warnings = warningsOf te
                    }
        | Ok(SModule(nameOpt, kwSpan)) ->
            // the marker adds no bindings; decl-only enforcement and the
            // running-a-module error are the caller's (loadModule / run)
            Ok {
                Kind = KModule(nameOpt, kwSpan)
                Env = tenv
                Warnings = []
            }
        | Ok(SImport(path, pathSpan, aliasOpt)) ->
            let importLine, importCol = translate ll pathSpan.Start.Col

            // a module-content error (File=Some) reports at the module's own
            // site [D:modules-v1], with an "imported here" note at the import
            // line; an import-statement error reports at the import line
            let importDiag (e: ImportError) =
                match e.File with
                | Some mf -> {
                    PhysLine = e.Line
                    PhysCol = e.Col
                    PhysEnd = None
                    Tag = None
                    HasCol = true
                    Span = None
                    Parse = false
                    Message = e.Message
                    File = Some mf
                    // point at this level's import; the outermost (entry)
                    // level runs last, so its note (in the entry file) wins
                    Note = Some(importLine, importCol, "imported here")
                    Related = []
                    Warnings = []
                  }
                | None -> {
                    PhysLine = importLine
                    PhysCol = importCol
                    PhysEnd = Some(translate ll pathSpan.End.Col)
                    Tag = None
                    HasCol = true
                    Span = Some pathSpan
                    Parse = false
                    Message = e.Message
                    File = None
                    Note = None
                    Related = []
                    Warnings = []
                  }

            let stmtErr msg =
                importDiag {
                    File = None
                    Line = 0
                    Col = 0
                    Message = msg
                }

            match loadImport path pathSpan (aliasOpt |> Option.map fst) with
            | Error e -> Error(importDiag e)
            | Ok lm ->
                // a session re-import [D:repl-import]: the alias an earlier
                // import holds is unbound first, then the import proceeds as
                // fresh — its types, members and privates replace the old. Its
                // own type keys leave Types/TypeOrigins too, else the new
                // module's same-origin types would join the stale defs
                // [D:qualified-type-names]; a key another import still names
                // stays
                let tenv =
                    if lm.MayRebind && tenv.ModuleTypes.ContainsKey lm.Alias then
                        let others = tenv.ModuleTypes |> Map.remove lm.Alias

                        let dropped =
                            tenv.ModuleTypes[lm.Alias]
                            |> Map.values
                            |> Seq.filter (fun k ->
                                tenv.TypeOrigins.ContainsKey k
                                && not (others |> Map.exists (fun _ owned -> owned |> Map.exists (fun _ k' -> k' = k))))
                            |> Set.ofSeq

                        {
                            tenv with
                                Types = tenv.Types |> Map.filter (fun k _ -> not (dropped.Contains k))
                                TypeOrigins = tenv.TypeOrigins |> Map.filter (fun k _ -> not (dropped.Contains k))
                                Modules = Map.remove lm.Alias tenv.Modules
                                ModuleTypes = Map.remove lm.Alias tenv.ModuleTypes
                                ModulePrivate = Map.remove lm.Alias tenv.ModulePrivate
                        }
                    else
                        tenv

                match lm with
                | lm when Map.containsKey lm.Alias tenv.Modules ->
                    Error(
                        stmtErr
                            $"the name '{lm.Alias}' is already a module in scope; import it under a different name with 'as'"
                    )
                // an alias spelled like a union case in scope (`as Some`) would
                // read as the constructor at every use — refused, as a second
                // module of one name is
                | lm when
                    tenv.Types
                    |> Map.exists (fun _ def ->
                        match def with
                        | Union u -> u.Cases |> List.exists (fun (c, _) -> c = lm.Alias)
                        | _ -> false)
                    ->
                    Error(
                        stmtErr
                            $"the name '{lm.Alias}' is a union case in scope; import the module under a different name with 'as'"
                    )
                | lm ->
                    // a type name another import or a local declaration holds is
                    // no clash: mergeModule keys it [D:qualified-type-names]
                    Ok {
                        Kind = KImport lm
                        Env = mergeModule tenv lm
                        Warnings = []
                    }
    finally
        Check.toPhys.Value <- None

// [D:pure-stage1]: the pure region's rule, enforced as a post-check
// layer here — the classifier needs the closed builtin surface, so it
// lives after Builtins (Purity.fs), out of Check.fs's compile reach;
// and every consumer — runner, REPL, -e, modules — flows through this
// pipeline [D:one-pipeline], so the layer covers them all. A KLet
// registers its purity into the env the next statement threads:
// transitivity through earlier bindings is one forward pass (no
// `let rec` exists).
let checkStatement
    (gateExprs: bool)
    (sigCtx: SigContext option)
    (mkR: TypeEnv -> Parser.Resolver)
    (loadImport: ImportLoader)
    (tenv: TypeEnv)
    (ll: LogicalLine)
    : Result<CheckedStatement, StmtDiag> =
    checkStatementCore gateExprs sigCtx mkR loadImport tenv ll
    // no checker var reaches the author [D:user-language-messages]
    |> Result.mapError (fun d -> {
        d with
            Message = Types.normalizeDiagVars d.Message
    })
    |> Result.bind (fun st ->
        let teTag =
            match st.Kind with
            | KLet(_, _, te) -> Some(te, StmtTag.Let)
            | KLetPat(_, _, te) -> Some(te, StmtTag.LetPat)
            | KCmd te -> Some(te, StmtTag.Cmd)
            | KExpr te -> Some(te, StmtTag.Expr)
            | KType(_, Some(_, te)) -> Some(te, StmtTag.Type)
            | KType _
            | KSig _
            | KModule _
            | KImport _ -> None

        match teTag with
        | None -> Ok st
        | Some(te, tag) ->
            // pureViolation returns the complete located message
            // (pure's "…forbids effects, but…" or readonly's
            // "…forbids external mutation, but…") [D:pure-stage2]; a
            // validator is a pure region of its own [D:validated-types]
            let violation =
                match st.Kind with
                | KType _ -> Purity.validatorViolation tenv.PureBindings te
                | _ ->
                    Purity.pureViolation tenv.PlanRefused tenv.PureBindings te
                    |> Option.orElseWith (fun () -> Purity.execScopeViolation tenv.ExecHelpers te)

            match violation with
            | Some(span, message) ->
                let physLine, physCol = translate ll span.Start.Col

                Error {
                    PhysLine = physLine
                    PhysCol = physCol
                    PhysEnd = Some(translate ll span.End.Col)
                    Tag = Some tag
                    HasCol = true
                    Span = Some span
                    Parse = false
                    Message = message
                    File = None
                    Note = None
                    Related = []
                    Warnings = []
                }
            | None ->
                match st.Kind with
                | KLet(name, _, te) ->
                    Ok {
                        st with
                            Env = {
                                st.Env with
                                    PureBindings =
                                        Map.add name (Purity.isPureExpr tenv.PureBindings te) st.Env.PureBindings
                                    PlanRefused =
                                        match Purity.planRefusalOf tenv.PlanRefused te with
                                        | Some why -> Map.add name why st.Env.PlanRefused
                                        | None -> Map.remove name st.Env.PlanRefused
                                    ExecHelpers =
                                        if Purity.execHelperOf tenv.ExecHelpers te then
                                            Set.add name st.Env.ExecHelpers
                                        else
                                            Set.remove name st.Env.ExecHelpers
                            }
                    }
                | _ -> Ok st)

// ---- unused bindings [D:unused-bindings]: whole-file threading ------------
// The check is a whole-file judgement (a binder's readers may sit any
// number of statements later), so it applies post-fold — beside the
// missing-implementation check — in every consumer that folds a complete file:
// analyzeLines (check / --can / LSP), the runner's check phase, the
// module loader, and the fidelity mirror. Per-statement consumers
// (REPL, -e, Complete) never see it. An errored statement poisons the
// pass for the whole file — one real error beats N echoes (matching
// the hole scheme).

type UnusedFinding = {
    ULine: int
    UCol: int
    UEndCol: int
    UMessage: string
    // the rebinding site of the shadow variant [D:diagnostic-tags]
    URelated: (int * int * string) list
}

/// the bare `_` binder's message — the one unused-binding variant that is
/// misnamed, not unnecessary: the structured marker tag consumers test
/// against [D:diagnostic-tags]
let bareDiscardMessage =
    "a bare '_' binder discards its value anonymously — name the discard (let _r = …) so the intent survives a grep"

type UnusedTracker() =
    // pending top-level binders: name -> (physLine, physCol, physEndCol, used)
    let pending =
        System.Collections.Generic.Dictionary<string, int * int * int * bool>()

    let found = ResizeArray<UnusedFinding>()
    let mutable poisoned = false

    let plainMsg (n: string) =
        $"'{n}' is bound but never used — read it, or name it '_{n}' to keep it deliberately"

    member _.Poison() = poisoned <- true

    member _.Uses(names: seq<string>) =
        for n in names do
            match pending.TryGetValue n with
            | true, (l, c, e, false) -> pending[n] <- (l, c, e, true)
            | _ -> ()

    member _.Locals (ll: LogicalLine) (locals: Check.UnusedLocal list) =
        for u in locals do
            let l, c = translate ll u.USpan.Start.Col
            let _, ec = translate ll u.USpan.End.Col

            found.Add {
                ULine = l
                UCol = c
                UEndCol = max (c + 1) ec
                UMessage =
                    if u.UBareWildcard then
                        bareDiscardMessage
                    else
                        plainMsg u.UName
                URelated = []
            }

    /// register a top-level binder; an unread pending binder of the
    /// same name errors at the earlier binder (the shadow rule —
    /// it catches the classic copy-paste bug)
    member _.Binder (ll: LogicalLine) (name: string) (startCol: int) (endCol: int) =
        if not (Check.unusedExempt name) then
            let l, c = translate ll startCol
            let _, ec = translate ll endCol

            (match pending.TryGetValue name with
             | true, (ol, oc, oe, false) ->
                 found.Add {
                     ULine = ol
                     UCol = oc
                     UEndCol = oe
                     UMessage =
                         $"'{name}' is bound but never used before being rebound at line {l} — read it, or name it '_{name}' to keep it deliberately"
                     URelated = [ l, c, $"'{name}' is rebound here" ]
                 }
             | _ -> ())

            pending[name] <- (l, c, max (c + 1) ec, false)

    /// the KLet binder's own span, re-derived from the statement text
    /// exactly as the squiggle does [D:squiggle-on-binder]
    member this.LetBinder (ll: LogicalLine) (name: string) =
        let m = Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+(?:pure\s+)?")

        if m.Success then
            this.Binder ll name (m.Length + 1) (m.Length + 1 + name.Length)
        else
            this.Binder ll name 1 (1 + name.Length)

    /// one checked statement, in file order: mark its uses, collect its
    /// block-local findings, then register its own binders (a
    /// same-statement read of the same name reads the outer binding —
    /// no `let rec` exists)
    member this.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        // a validator reads like a function body [D:validated-types]
        | KType(_, Some(_, te)) ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals
            this.LetBinder ll name
        | KLetPat(pat, _, te) ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals

            for n, sp in Check.patNameSpans pat do
                this.Binder ll n sp.Start.Col sp.End.Col
        | KCmd te
        | KExpr te ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals

    /// end of file: every still-unread binder errors — signed module
    /// members exempt (the signature is the use); an unsigned module
    /// member unread at home is dead private code
    member _.Flush (signedNames: Set<string>) (isModule: bool) : UnusedFinding list =
        if poisoned then
            []
        else
            let flushed = [
                for KeyValue(name, (l, c, e, used)) in pending do
                    if not used && not (Set.contains name signedNames) then
                        {
                            ULine = l
                            UCol = c
                            UEndCol = e
                            UMessage =
                                if isModule then
                                    $"'{name}' is bound but never used — an unsigned module member is private dead code: use it, export it with a signature (let {name} : …), or name it '_{name}'"
                                else
                                    plainMsg name
                            URelated = []
                        }
            ]

            (List.ofSeq found) @ flushed |> List.sortBy (fun f -> f.ULine, f.UCol)

// ---- possible re-enumeration [D:reenum-warning]: whole-file threading -----
// The hazard is a whole-file judgement like the unused check (a second
// pull may sit any number of statements later), so it applies
// post-fold in analyzeLines — check / --json / --can / the LSP all
// inherit it; the REPL states the same hazard on its binding echo
// instead. Warning severity: advisory, never a gate — check still
// exits 0. Poisoned (any errored statement) = silent, the unused
// check's rule: one real error beats advisory noise.

type ReenumFinding = {
    RLine: int
    RCol: int
    REndCol: int
    RMessage: string
}

type ReenumTracker() =
    // top-level tracked names -> their pulls [D:reenum-indirect];
    // id -> (command, repair)
    let tracked = System.Collections.Generic.Dictionary<string, Check.ReenumCarry>()
    let info = System.Collections.Generic.Dictionary<int, string * string>()
    let counts = System.Collections.Generic.Dictionary<int, int>()
    let found = ResizeArray<ReenumFinding>()
    let mutable nextId = 0
    let mutable poisoned = false

    let fresh () =
        let id = nextId
        nextId <- nextId + 1
        id

    let tracked0 () =
        tracked |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// count one statement's events: enumerating uses of the tracked set
    /// (top-level and any qualifying block-locals), a warning at the
    /// second and later sites — the first pull is the binding's point,
    /// the second is where the command silently runs again
    let consume (ll: LogicalLine) (events: Check.ReenumEvent list) =
        for ev in events do
            match ev with
            | Check.ReenumBind(id, _, cmd) ->
                // a block-local binder has no clean one-line source to
                // ride the repair — the generic spelling instead
                if not (info.ContainsKey id) then
                    info[id] <- (cmd, "add '|> Seq.freeze' at the binding")

                counts[id] <- 0
            | Check.ReenumUse(id, name, note, span) ->
                let n =
                    (match counts.TryGetValue id with
                     | true, c -> c
                     | _ -> 0)
                    + 1

                counts[id] <- n

                if n >= 2 then
                    let cmd, repair = info[id]
                    let l, c = translate ll span.Start.Col
                    let _, ec = translate ll span.End.Col

                    found.Add {
                        RLine = l
                        RCol = c
                        REndCol = max (c + 1) ec
                        RMessage =
                            $"possible re-enumeration: '{name}'{note} is command-backed and unforced — each pull re-runs '{cmd}'; snapshot one run: {repair}"
                    }

    member _.Poison() = poisoned <- true

    member _.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            // RHS first (a same-name rebind reads the outer binding — no
            // `let rec` exists)
            let carry, events = Check.reenumLet fresh (tracked0 ()) name te

            // the binding's own command: the repair rides its source when
            // it is one clean line (as streamed-it does); assembled
            // (sentinel-joined) text falls back to the generic spelling
            (match Check.commandBackedUnforced te, carry.Pulls with
             | Some cmd, (id, n) :: _ when n = name ->
                 let src = ll.Text.Trim()

                 let repair =
                     if src.StartsWith "let " && not (src |> Seq.exists System.Char.IsControl) then
                         $"{src} |> Seq.freeze"
                     else
                         "add '|> Seq.freeze' at the binding"

                 info[id] <- (cmd, repair)
             | _ -> ())

            consume ll events
            tracked.Remove name |> ignore

            if not carry.Pulls.IsEmpty || carry.Forced then
                tracked[name] <- carry
        | KLetPat(pat, _, te) ->
            consume ll (Check.reenumEvents fresh (tracked0 ()) te)

            for n, _ in Check.patNameSpans pat do
                tracked.Remove n |> ignore
        | KCmd te
        | KExpr te -> consume ll (Check.reenumEvents fresh (tracked0 ()) te)

    member _.Flush() : ReenumFinding list =
        if poisoned then [] else List.ofSeq found

// ---- the newTempDir footgun [D:newtempdir-lint]: whole-file threading -----
// A newTempDir binding and its delete can sit any number of statements
// apart (`let d = Path.newTempDir ()` … work … `Dir.deleteAll d`), so the
// pairing is a whole-file judgement — the re-enumeration tracker's shape
// exactly. Fed post-check per statement, flushed after the fold; warning
// severity (advisory, check still exits 0); poisoned on any errored
// statement (one real error beats advisory noise). Modules never bind a
// command/effect `let`, so the pairing cannot arise there — the tracker is
// only fed for scripts.

type TempDirFinding = {
    TLine: int
    TCol: int
    TEndCol: int
    TMessage: string
}

type TempDirTracker() =
    // top-level newTempDir binders: name -> id
    let tracked = System.Collections.Generic.Dictionary<string, int>()
    // every bind id seen (top-level + block-local) still awaiting a delete
    let openIds = System.Collections.Generic.HashSet<int>()
    let found = ResizeArray<TempDirFinding>()
    let mutable nextId = 0
    let mutable poisoned = false

    let fresh () =
        let id = nextId
        nextId <- nextId + 1
        id

    /// walk one statement tree: a delete of an open binder (top-level or a
    /// qualifying block-local) warns once at the delete site
    let consume (ll: LogicalLine) (te: Check.TypedExpr) =
        let tracked0 = tracked |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

        for ev in Check.tempDirEvents fresh tracked0 te do
            match ev with
            | Check.TempBind(id, _) -> openIds.Add id |> ignore
            | Check.TempDelete(id, name, span, isAll) ->
                if openIds.Remove id then
                    // a matched top-level binder is resolved — drop it so a
                    // later stray delete of a reused name does not re-warn
                    for kv in tracked |> Seq.filter (fun kv -> kv.Value = id) |> Seq.toList do
                        tracked.Remove kv.Key |> ignore

                    let l, c = translate ll span.Start.Col
                    let _, ec = translate ll span.End.Col
                    let deleteCall = if isAll then "Dir.deleteAll" else "Dir.delete"

                    found.Add {
                        TLine = l
                        TCol = c
                        TEndCol = max (c + 1) ec
                        TMessage =
                            $"'{name}' is a Path.newTempDir directory later removed with {deleteCall} — "
                            + $"use a 'within tmp {name}' block instead: it removes the directory on scope exit "
                            + "AND on Ctrl+C/kill, which a manual delete misses (newTempDir is for a directory "
                            + "that must OUTLIVE the scope)"
                    }

    member _.Poison() = poisoned <- true

    member _.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            consume ll te
            tracked.Remove name |> ignore

            if Check.isNewTempDirCall te then
                let id = fresh ()
                tracked[name] <- id
                openIds.Add id |> ignore
        | KLetPat(pat, _, te) ->
            consume ll te

            for n, _ in Check.patNameSpans pat do
                tracked.Remove n |> ignore
        | KCmd te
        | KExpr te -> consume ll te

    member _.Flush() : TempDirFinding list =
        if poisoned then [] else List.ofSeq found
