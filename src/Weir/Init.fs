/// The init file, aliases and `#session`. One of the files Script.fs was [D:script-by-concern].
module Weir.Init

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor
open Weir.Statements
open Weir.ModuleLoader
open Weir.Diagnostics
open Weir.Sigs

// ---- the init file [D:repl-init] ------------------------------------------
// ONE home for every static init rule [D:init-check-one-home] — `weir
// check`, the LSP and the REPL loader all run checkInit, so check refuses
// exactly what the loader refuses. The loader keeps only what needs
// evaluation (a cwd that exists, a logLevel that parses, an echoCap in
// range).

// a command-head alias [D:command-head-alias]: (real exe, fixed prefix
// args). Single-hop by construction — the exe is a real program name,
// never another alias (define-time resolution rejects alias-of-alias).
type CommandAlias = { Exe: string; Prefix: string list }

/// parse a `#alias name = cmd [args...]` directive line [D:command-head-alias].
/// The head token maps to (exe, prefix args); argv is split on whitespace
/// (a fixed prefix is literal words — no quoting/typed-argv machinery: an
/// alias is a resolution-table entry, not a script line). Returns the name
/// and its target, or a message. Single-hop is enforced later against the
/// whole table.
let parseAliasLine (body: string) : Result<string * CommandAlias, string> =
    // body is the text after `#alias`
    match System.Text.RegularExpressions.Regex.Match(body.Trim(), @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$") with
    | m when m.Success && Parser.isKeyword m.Groups.[1].Value ->
        // a keyword heads its own statement form, never a command — the
        // alias could never resolve
        let name = m.Groups.[1].Value
        Error $"#alias {name}: '{name}' is a keyword — a line starting with it is never a command; pick another name"
    | m when m.Success ->
        let name = m.Groups.[1].Value

        let rhsWords =
            m.Groups.[2].Value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        // an alias target is one program plus literal words — a chaining
        // glyph there would reach the program as an argument, never chain
        // [D:alias-no-chain]
        let chainGlyph =
            rhsWords
            |> List.tryFind (fun w -> List.contains w [ "|"; "|>"; "||"; "&&"; ";" ])

        match rhsWords, chainGlyph with
        | [], _ -> Error "#alias needs a target: '#alias name = command [args...]'"
        | _, Some g ->
            Error
                $"#alias {name}: '{g}' in an alias target is a literal argument, not a chain — an alias is one program plus fixed words; for a pipeline or a chain write a function: let {name} () = …"
        | exe :: prefix, None ->
            // `#alias ls = ls` (or `= ls --color`) is a legal shadow (bash
            // allows it), bypassable with `^ls` — register as-is
            Ok(name, { Exe = exe; Prefix = prefix })
    | _ -> Error "malformed #alias — usage: '#alias name = command [args...]' (e.g. '#alias k = kubectl')"

/// one wording for every consumer [D:repl-init]
let sessionDeclRefMessage (name: string) : string =
    $"a #session value cannot use the file's own declarations ('{name}') — settings are read before the declarations bind; only prompt may name one. Write the value inline"

let knownSessionKeys =
    Set [ "cwd"; "env"; "logLevel"; "echoCap"; "prompt"; "continuationPrompt" ]

/// the #session rules that need the value, ONE home
/// [D:session-value-one-home]: the REPL loader runs them on the evaluated
/// value, `weir check` on a literal one (a computed value waits for the
/// load). cwd's existence is the machine's, so it stays the loader's.
let sessionValueRefusal (key: string) (v: Eval.Value) : string option =
    match key, v with
    | "logLevel", Eval.VStr lvl ->
        match Builtins.parseLogLevel lvl with
        | Ok _ -> None
        | Error e -> Some(e.Replace("WEIR_LOG=", "logLevel="))
    | "echoCap", Eval.VInt n when n > int64 System.Int32.MaxValue ->
        Some $"echoCap {n} is too large (at most {System.Int32.MaxValue})"
    | "echoCap", Eval.VInt n when n <= 0L -> Some $"echoCap must be positive; got {n}"
    | "env", Eval.VSeq pairs ->
        // the key law holds here as where any env is built
        // [D:env-key-refusal]; a NUL value would be cut short at the
        // process env, so it is refused rather than truncated
        pairs
        |> Seq.tryPick (function
            | Eval.VTuple [ Eval.VStr k; Eval.VStr value ] ->
                try
                    Proc.envKeyRefusal "#session env" k

                    if value.Contains '\u0000' then
                        Some
                            $"#session env: the value for '{quoteData k}' contains a NUL byte — the environment would cut it short there; an env value is NUL-free"
                    else
                        None
                with Failure msg ->
                    Some msg
            | _ -> None)
    | _ -> None

// a value `weir check` can judge without running anything: literals and
// the operators over them (`-3` is `0 - 3`)
let rec private sessionLiteral (te: Check.TypedExpr) : bool =
    match te.Kind with
    | Check.TEStr _
    | Check.TEInt _ -> true
    | Check.TEBinOp(_, l, r) -> sessionLiteral l && sessionLiteral r
    | Check.TEList items
    | Check.TETuple items -> List.forall sessionLiteral items
    | _ -> false

let expectedFieldTypes (key: string) : Ty list =
    match key with
    | "cwd"
    | "logLevel"
    | "continuationPrompt" -> [ TStr ]
    | "echoCap" -> [ TInt ]
    | "env" -> [ TSeq(TTuple [ TStr; TStr ]) ]
    | "prompt" -> [ TStr; TFun(TUnit, TStr); TFun(TNamed("PromptStatus", []), TStr) ]
    | _ -> []

/// the init file split by role, physical line numbers kept: the #session
/// fields as synthesized lets (the prompt field apart — it checks after the
/// declarations [D:session-prompt]), the declarations with every directive
/// line blanked (transparent, so positions stay real), the #alias bodies,
/// and the lines the #session block spans
type InitSplit = {
    Fields: (int * string) list
    PromptFields: (int * string) list
    Decls: (int * string) list
    Aliases: (int * string) list
    BlockLines: Set<int>
}

/// the checked init: every static error (Diags — init rules and the
/// #session values' own errors; DeclDiags — the declarations' own statement
/// errors, kept apart because check already reports those from the full
/// file), plus what the loader applies
type InitCheck = {
    Diags: Diagnostic list
    DeclDiags: Diagnostic list
    Split: InitSplit
    Settings: (LogicalLine * string * Check.TypedExpr) list
    Decls: (LogicalLine * CheckedStatement) list
    DeclEnv: TypeEnv
    Prompt: (LogicalLine * Check.TypedExpr) option
    Aliases: (int * string * CommandAlias) list
}

let private initError (file: string) (line: int) (col: int) (endCol: int option) (code: string) (msg: string) = {
    File = file
    Line = line
    Col = col
    EndLine = endCol |> Option.map (fun _ -> line)
    EndCol = endCol
    Severity = "error"
    Code = code
    Related = []
    Fix = None
    Message = msg
}

let splitInit (file: string) (lines: string list) : InitSplit * Diagnostic list =
    let mutable fields: (int * string) list = []
    // the prompt field rides separately [D:session-prompt]: its value
    // names init declarations, so it checks after they bind — every
    // other field checks before them (settings before names)
    let mutable promptFields: (int * string) list = []
    let mutable inPromptField = false
    let mutable rest: (int * string) list = []
    // #alias directive lines, collected with their (1-based) line number;
    // the text is what follows `#alias` [D:command-head-alias]
    let mutable aliases: (int * string) list = []
    let mutable blockLines: Set<int> = Set.empty
    let mutable inBlock = false
    let mutable seen = false
    let diags = ResizeArray<Diagnostic>()
    let arr = List.toArray lines

    for i in 0 .. arr.Length - 1 do
        let l = arr.[i]
        let t = l.Trim()

        if not inBlock && t = "#init" then
            rest <- (i + 1, "") :: rest
        elif not inBlock && (t = "#alias" || t.StartsWith "#alias ") then
            // a top-level directive line; kept out of the declaration
            // stream and replaced by a blank so declaration diagnostics keep
            // their real positions (the #session discipline)
            aliases <- (i + 1, t.Substring("#alias".Length)) :: aliases
            rest <- (i + 1, "") :: rest
        elif not inBlock && t.StartsWith "#session" then
            if seen then
                diags.Add(initError file (i + 1) 1 None "session-shape" "a second #session block — the init takes one")

            seen <- true

            if t = "#session {" then
                inBlock <- true
                blockLines <- Set.add (i + 1) blockLines
            else
                diags.Add(
                    initError
                        file
                        (i + 1)
                        1
                        None
                        "session-shape"
                        "malformed #session — usage: '#session {' then 'key = value' lines then '}'"
                )

            rest <- (i + 1, "") :: rest
        elif not inBlock && l.StartsWith "#" then
            // a stray directive gets its own message, never a parse dump
            // [D:directive-diagnostic]
            let word = t.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries).[0]

            let msg =
                if word = "#sig" then
                    "#sig belongs at a script's head — an init file takes #init, #session and #alias"
                else
                    $"unknown directive: {word} — an init file takes #init, #session and #alias"

            diags.Add(initError file (i + 1) 1 None "directive" msg)
            rest <- (i + 1, "") :: rest
        elif inBlock && t = "}" then
            inBlock <- false
            blockLines <- Set.add (i + 1) blockLines
            rest <- (i + 1, "") :: rest
        elif inBlock then
            blockLines <- Set.add (i + 1) blockLines

            if t <> "" then
                // a field-start (`key = …`) dedents 4 and gains a 'let '
                // prefix — 4 chars out, 4 in, columns survive. Every
                // other line (list entries, closers) passes through
                // untouched: still indented, still a continuation of the
                // synthesized let, columns exact
                let fieldStart =
                    System.Text.RegularExpressions.Regex.Match(t, "^([A-Za-z_][A-Za-z0-9_]*)\s*=")

                // a continuation line belongs to the field it follows
                if fieldStart.Success then
                    inPromptField <- fieldStart.Groups.[1].Value = "prompt"

                let synthesized =
                    if fieldStart.Success then
                        "let " + (if l.StartsWith "    " then l.Substring 4 else t)
                    else
                        l

                if inPromptField then
                    promptFields <- (i + 1, synthesized) :: promptFields
                else
                    fields <- (i + 1, synthesized) :: fields

            rest <- (i + 1, "") :: rest
        else
            rest <- (i + 1, l) :: rest

    if inBlock then
        diags.Add(
            initError file (max 1 arr.Length) 1 None "session-shape" "unclosed #session block — a lone '}' line ends it"
        )

    {
        Fields = List.rev fields
        PromptFields = List.rev promptFields
        Decls = List.rev rest
        Aliases = List.rev aliases
        BlockLines = blockLines
    },
    List.ofSeq diags

let checkInit (mkR: TypeEnv -> Parser.Resolver) (file: string) (lines: string list) : InitCheck =
    let split, shapeDiags = splitInit file lines
    // the init file is a script, not interactive use: bare module members
    // are refused here as in any script — the session gets them, the file
    // does not [D:init-check-one-home]
    let strictTenv = Prelude.extendTypes Builtins.typeEnvStrict
    let diags = ResizeArray<Diagnostic>(shapeDiags)
    let declDiags = ResizeArray<Diagnostic>()

    // the marker is required [D:init-marker]
    if not (isInitMarked lines) then
        diags.Add(initError file 1 1 None "init-marker" "the init file declares itself — put #init on the first line")

    // imports resolve against the init file's real location [D:init-import]
    let importer = fileImportLoader mkR (importAnchor file)

    let ownLines = List.toArray lines

    let stmtDiag (d: StmtDiag) =
        let code = codeOf d.Parse d.Message

        {
            File = d.File |> Option.defaultValue file
            Line = d.PhysLine
            Col = d.PhysCol
            EndLine = d.PhysEnd |> Option.map fst
            EndCol = d.PhysEnd |> Option.map snd
            Severity = "error"
            Related =
                d.Related
                |> List.map (fun (l, c, m) -> d.File |> Option.defaultValue file, l, c, m)
            Code = code
            Fix =
                fixOf
                    (if d.File.IsSome then [||] else ownLines)
                    code
                    d.Message
                    d.PhysLine
                    d.PhysCol
                    (d.PhysEnd |> Option.map fst)
                    (d.PhysEnd |> Option.map snd)
            Message = d.Message
        }

    let assembleOrReport (numbered: (int * string) list) (into: ResizeArray<Diagnostic>) =
        match assemble numbered with
        | Ok lls -> Some lls
        | Error msg ->
            match assemblyErrorAt numbered msg with
            | Some(line, col, rest) -> into.Add(initError file line col None "layout" rest)
            | None ->
                let line = numbered |> List.tryHead |> Option.map fst |> Option.defaultValue 1
                into.Add(initError file line 1 None "layout" msg)

            None

    let keyValueOnly (line: int) =
        diags.Add(initError file line 1 None "session-shape" "a #session block takes 'key = value' lines only")

    // the #alias table [D:command-head-alias] — single-hop: a target naming
    // a different alias is refused (a self-shadow, `#alias ls = ls --color`,
    // targets the real binary and is legal)
    let parsedAliases =
        split.Aliases
        |> List.choose (fun (lineNo, body) ->
            match parseAliasLine body with
            | Error msg ->
                diags.Add(initError file lineNo 1 None "init-alias" msg)
                None
            | Ok(name, alias) -> Some(lineNo, name, alias))

    let aliasNames = parsedAliases |> List.map (fun (_, n, _) -> n) |> Set.ofList

    for lineNo, name, alias in parsedAliases do
        if alias.Exe <> name && Set.contains alias.Exe aliasNames then
            diags.Add(
                initError
                    file
                    lineNo
                    1
                    None
                    "init-alias"
                    $"#alias {name} = {alias.Exe} …: an alias resolves to a program, not to another alias ('{alias.Exe}' is itself an alias) — aliases are single-hop"
            )

    // the declarations — the module rule, applied to the prompt; checking
    // stops at the first statement error (later ones would echo it)
    let declsChecked, declEnv, declsComplete =
        match
            (if shapeDiags.IsEmpty then
                 assembleOrReport split.Decls declDiags
             else
                 None)
        with
        | None -> [], strictTenv, false
        | Some lls ->
            let rec go env acc rest =
                match rest with
                | [] -> List.rev acc, env, true
                | (ll: LogicalLine) :: tail ->
                    match checkStatement false None mkR importer env ll with
                    | Error d ->
                        declDiags.Add(stmtDiag d)
                        List.rev acc, env, false
                    | Ok chk -> go chk.Env ((ll, chk) :: acc) tail

            go strictTenv [] lls

    // a lazy command let binds and spawns nothing; forcing one at load is
    // refused [D:lazy-command-lets]
    let mutable cmdBindings: Map<string, int> = Map.empty

    for ll, chk in declsChecked do
        let refuse msg =
            diags.Add(initError file ll.Head 1 None "init-decl" msg)

        match chk.Kind with
        | KType _
        | KImport _
        | KSig _ -> ()
        | KLet(_, _, te)
        | KLetPat(_, _, te) ->
            match Check.commandRunsNow cmdBindings te, chk.Kind with
            | Some why, _ -> refuse $"an init 'let' cannot run a command at load: {why}"
            | None, KLet(name, _, _) -> cmdBindings <- Check.recordCommandBinding cmdBindings name te
            | None, _ -> ()
        | KCmd _
        | KExpr _ ->
            refuse "the init file is declaration-only — 'type' and 'let'; it configures the prompt, it cannot run"
        | KModule _ -> refuse "the init file is not a module — no 'module' marker; its names are the prompt's"

    let declNames =
        declsChecked
        |> List.collect (fun (_, chk) ->
            match chk.Kind with
            | KLet(n, _, _) -> [ n ]
            | KLetPat(p, _, _) -> Check.patNameSpans p |> List.map fst
            | _ -> [])
        |> Set.ofList

    // an alias naming a declaration is dead on arrival
    // [D:alias-binding-shadow]: the binding shadows the table
    for lineNo, name, _ in parsedAliases do
        if Set.contains name declNames then
            diags.Add(
                initError
                    file
                    lineNo
                    1
                    None
                    "init-alias"
                    $"#alias {name}: '{name}' is also declared in this file — the binding shadows the alias, so it would never resolve; pick another name"
            )

    let keyCol (lineNo: int) =
        if lineNo >= 1 && lineNo <= ownLines.Length then
            ownLines[lineNo - 1].Length - ownLines[lineNo - 1].TrimStart().Length + 1
        else
            1

    // the value's column: past `key =` on the field's head line
    let valCol (lineNo: int) (key: string) =
        if lineNo >= 1 && lineNo <= ownLines.Length then
            let m =
                System.Text.RegularExpressions.Regex.Match(ownLines[lineNo - 1], @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*")

            if m.Success then
                m.Length + 1
            else
                keyCol lineNo + key.Length
        else
            1

    // the #session fields — settings before names: each checks in the bare
    // env, so naming a declaration is unbound; say why
    let settings =
        match
            (if shapeDiags.IsEmpty then
                 assembleOrReport split.Fields diags
             else
                 None)
        with
        | None -> []
        | Some lls ->
            let mutable env = strictTenv
            let mutable seen: Set<string> = Set.empty
            let acc = ResizeArray<LogicalLine * string * Check.TypedExpr>()

            for ll in lls do
                match checkStatement false None mkR importer env ll with
                | Error d ->
                    let d' = stmtDiag d

                    let unboundDecl =
                        System.Text.RegularExpressions.Regex.Match(d.Message, @"^unbound variable '([^']+)'")

                    if unboundDecl.Success && Set.contains unboundDecl.Groups[1].Value declNames then
                        let n = unboundDecl.Groups[1].Value

                        diags.Add {
                            d' with
                                Code = "session-names-declaration"
                                Related = []
                                Fix = None
                                EndLine = Some d'.Line
                                EndCol = Some(d'.Col + n.Length)
                                Message = sessionDeclRefMessage n
                        }
                    else
                        diags.Add d'
                | Ok chk ->
                    env <- chk.Env

                    match chk.Kind with
                    | KLet(key, _, te) ->
                        let kc = keyCol ll.Head
                        let vc = valCol ll.Head key

                        let keyError (msg: string) =
                            diags.Add(initError file ll.Head kc (Some(kc + key.Length)) "session-key" msg)

                        if not (Set.contains key knownSessionKeys) then
                            let hint = didYouMean key knownSessionKeys

                            keyError
                                $"unknown #session key '{key}'{hint} (the keys: cwd, env, logLevel, echoCap, prompt, continuationPrompt)"
                        elif Set.contains key seen then
                            keyError $"duplicate #session key '{key}'"
                        elif Check.runsCommandT te then
                            // a bare name of one of the file's own
                            // declarations reads as a command head here: the
                            // values are read before the declarations exist
                            let named =
                                if ll.Head >= 1 && ll.Head <= ownLines.Length then
                                    let m =
                                        System.Text.RegularExpressions.Regex.Match(
                                            ownLines[ll.Head - 1],
                                            @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*([A-Za-z_][A-Za-z0-9_]*)"
                                        )

                                    if m.Success && Set.contains m.Groups[1].Value declNames then
                                        Some m.Groups[1].Value
                                    else
                                        None
                                else
                                    None

                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    vc
                                    None
                                    "session-command"
                                    (match named with
                                     | Some n ->
                                         $"a #session value is read before the file's declarations, so '{key}' cannot name '{n}' — write the value here (only 'prompt' may name a declaration)"
                                     | None ->
                                         "a #session value cannot run a command — settings are read, not computed by running things")
                            )
                        else
                            seen <- Set.add key seen

                            let typeMsg =
                                match key, te.Ty with
                                | ("cwd" | "logLevel" | "continuationPrompt"), TStr
                                | "echoCap", TInt
                                | "env", TSeq(TTuple [ TStr; TStr ]) -> None
                                // a generic value (an empty list -> seq<'a>)
                                // unifies at load — not a mismatch
                                | _, ty when (formatTy ty).Contains "'" -> None
                                | "cwd", ty -> Some $"cwd expects a string, got {formatTy ty}"
                                | "logLevel", ty -> Some $"logLevel expects a string, got {formatTy ty}"
                                | "continuationPrompt", ty ->
                                    Some $"continuationPrompt expects a string, got {formatTy ty}"
                                | "echoCap", ty -> Some $"echoCap expects an int, got {formatTy ty}"
                                | _, ty ->
                                    Some $"env expects seq<string * string> — pairs, order kept; got {formatTy ty}"

                            let valueMsg () =
                                if sessionLiteral te then
                                    try
                                        sessionValueRefusal key (Eval.eval Map.empty te)
                                    with _ ->
                                        None
                                else
                                    None

                            match typeMsg with
                            | Some msg -> diags.Add(initError file ll.Head vc None "session-type" msg)
                            | None ->
                                match valueMsg () with
                                | Some msg -> diags.Add(initError file ll.Head vc None "session-value" msg)
                                | None -> acc.Add(ll, key, te)
                    | _ -> keyValueOnly ll.Head

            List.ofSeq acc

    // the prompt field last [D:session-prompt]: its value names the
    // declarations, so it checks in their env — only once they all checked
    // (a broken declaration would echo here as unbound)
    let prompt =
        if not declsComplete || split.PromptFields.IsEmpty then
            None
        else
            match assembleOrReport split.PromptFields diags with
            | None -> None
            | Some lls ->
                let checkedPrompt =
                    lls
                    |> List.map (fun ll -> ll, checkStatement false None mkR importer declEnv ll)

                checkedPrompt
                |> List.iteri (fun i (ll, r) ->
                    match r with
                    | Error d -> diags.Add(stmtDiag d)
                    | Ok _ when i > 0 ->
                        let kc = keyCol ll.Head

                        diags.Add(
                            initError file ll.Head kc (Some(kc + 6)) "session-key" "duplicate #session key 'prompt'"
                        )
                    | Ok chk ->
                        match chk.Kind with
                        | KLet("prompt", _, te) when Check.runsCommandT te ->
                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    (valCol ll.Head "prompt")
                                    None
                                    "session-command"
                                    "a #session value cannot run a command — name a function here (prompt = f); the command runs when the prompt calls f"
                            )
                        | KLet("prompt", _, te) ->
                            match te.Ty with
                            | TStr
                            | TFun(TUnit, TStr) -> ()
                            | TFun(dom, TStr) when promptStatusDomain dom -> ()
                            | ty ->
                                diags.Add(
                                    initError
                                        file
                                        ll.Head
                                        (valCol ll.Head "prompt")
                                        None
                                        "session-type"
                                        $"prompt expects a string, a unit -> string, or a PromptStatus -> string function (ok, a bare command's exit code, the entry's duration), got {formatTy ty}"
                                )
                        | _ -> keyValueOnly ll.Head)

                match checkedPrompt with
                | (ll, Ok { Kind = KLet("prompt", _, te) }) :: _ -> Some(ll, te)
                | _ -> None

    let byPosition (ds: seq<Diagnostic>) =
        ds |> List.ofSeq |> List.sortBy (fun d -> d.Line, d.Col)

    {
        Diags = byPosition diags
        DeclDiags = byPosition declDiags
        Split = split
        Settings = settings
        Decls = declsChecked
        DeclEnv = declEnv
        Prompt = prompt
        Aliases = parsedAliases
    }

/// the record types a file hands to `Args.load`/`Env.load` (type ->
/// loader), read off its parsed statements — an unbound field name's
/// hint teaches loading the record first. A line that does not parse
/// contributes nothing (its own error is reported where it is checked).
let loadTargetsOf (tenv: TypeEnv) (lines: LogicalLine list) : Map<string, string> =
    let walk (root: Expr) : (string * string) list =
        let acc = ResizeArray()

        let rec go (e: Expr) : unit option =
            (match e.Kind with
             | EApp({
                        Kind = EField({ Kind = EVar(("Args" | "Env") as m) }, "load", _)
                    },
                    { Kind = EVar t }) when t.Length > 0 && System.Char.IsUpper t[0] -> acc.Add(t, m + ".load")
             | _ -> ())

            tryPickExprChild go e

        go root |> ignore
        List.ofSeq acc

    lines
    |> List.collect (fun ll ->
        // the field is read from the source, so a line without the word
        // holds no load site — and no second parse of it
        if not (ll.Text.Contains "load") then
            []
        else
            match Parser.parseLine (assumeResolver tenv) ll.Text with
            | Ok(SLet(_, e))
            | Ok(SLetPat(_, e))
            | Ok(SExpr e)
            | Ok(SCmd e) -> walk e
            | _ -> [])
    |> List.distinctBy fst
    |> Map.ofList
