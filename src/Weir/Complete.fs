module Weir.Complete

open Weir.Types

// keyword suggestions DERIVE from the parser's set — one source of
// truth [D:keyword-completion]; the hand-kept copy here predated
// if/then/else/elif/when and silently under-offered them for six
// weeks. The exclusions, each with its reason:
//   rec, mutable — reserved words with NO meaning (offering them
//     suggests a spelling whose only outcome is the reserved-word
//     teaching error)
//   function — reserved for the parked match-lambda sugar; same fate
// rec/mutable/function are reserved-for-teaching; the foreign
// control-flow words (PLAN-dx-review D4) exist only to teach weir's
// spelling — offering any of them would suggest a word that can never
// parse
let unsuggestedKeywords =
    Set [ "rec"; "mutable"; "function"; "while"; "return"; "try"; "def" ]

let private keywords = Weir.Parser.keywords - unsuggestedKeywords |> Set.toList

// the SESSION DIRECTIVES, one source [D:repl-directives] — the set the
// REPL's dispatch string-matches (Repl.fs) and #help documents, `#help`
// first. Both completion slots read this: the line-head '#' slot (bare
// names — the editor's word starts after the '#') and the empty-prompt
// head [D:empty-prompt-directives] (the '#'-prefixed teaching set).
let sessionDirectives = [ "help"; "find"; "echo"; "infer"; "save"; "history"; "quit" ]

// the `#help` DOCUMENTABLE universe, one source [D:help-arg-complete]:
// the bare names `#help <name>` can DOCUMENT — modules, top-level
// forms/members (user values), and TYPES (records AND unions, the
// #infer-injected ones included). Both `#help`'s unknown-name pool
// (Repl.fs) and the #help-arg completion slot read this, so what
// `#help X` documents and what `#help <TAB>` offers cannot drift
// (the sessionDirectives precedent). Modules also document their
// members via `#help Module.member`; the qualified names live in the
// slot itself, keyed off env.Modules.
let helpNames (env: TypeEnv) : string list =
    List.concat
        [ Map.keys env.Modules |> List.ofSeq
          Map.keys env.Values |> Seq.filter Types.isUserName |> List.ofSeq
          Map.keys env.Types |> List.ofSeq ]
    |> List.distinct

let private recordFields (env: TypeEnv) (ty: Ty) : (string * Ty) list option =
    match ty with
    | TNamed(n, _) ->
        match Map.tryFind n env.Types with
        | Some(Record def) -> Some def.Fields
        | _ -> None
    | _ -> None

// unbound lowercase names in the prefix become HOLES (fresh type
// vars): the pipe source often mentions enclosing params
// (`targetEnv t |> ...`) whose VALUES are unknown but irrelevant —
// a known function's result type falls out of unification anyway
// [D:hole-completion]
let private holeNames (env: TypeEnv) (e: Weir.Ast.Expr) : string list =
    let acc = System.Collections.Generic.HashSet<string>()

    let rec walk (e: Weir.Ast.Expr) =
        (match e.Kind with
         | Weir.Ast.EVar n when n.Length > 0 && System.Char.IsLower n[0] && not (Map.containsKey n env.Values) ->
             acc.Add n |> ignore
         | _ -> ())

        Weir.Ast.exprChildren e |> List.iter walk

    walk e
    List.ofSeq acc

// bind every hole name as a fresh TVar — the shared setup of the
// pipeline-elem and repaired-statement typing paths
let private withHoles (env: TypeEnv) (e: Weir.Ast.Expr) : TypeEnv =
    holeNames env e
    |> List.mapi (fun i n -> n, mono (TVar $"__hole{i}"))
    |> List.fold
        (fun (te: TypeEnv) (n, sch) ->
            { te with
                Values = Map.add n sch te.Values })
        env

// the TYPE FLOWING INTO this position: the type of the value the pipe
// carries to the completion word. A `seq` unwraps to its element (the
// `_.`/lambda-param over each row); a SCALAR flows through as itself —
// `from yaml T` yields one document `TNamed(name,[])`, not a seq, so a
// record piped into `_.`/`r |> _.` must type to the record, not `None`.
// The sole caller routes through `recordFields`, which returns `None`
// for a non-record, so the catch-all offers nothing where nothing fits.
let private pipelineElemTy (env: TypeEnv) (text: string) : Ty option =
    match text.LastIndexOf '|' with
    | -1 -> None
    | i ->
        let prefix = text.Substring(0, i).Trim()

        match Weir.Parser.parseExpr prefix with
        | Error _ -> None
        | Ok e ->
            let envWithHoles = withHoles env e

            match Weir.Check.typecheck envWithHoles e with
            | Ok te ->
                match te.Ty with
                | TSeq elem -> Some elem
                | t -> Some t
            | Error _ -> None

/// text ENDS AT THE CURSOR (both callers truncate — the LSP's `upto`,
/// the REPL's Substring): the word runs from wordStart to the end
// filesystem completion [D:repl-quality] for an explicit path word (has a
// `/` or leads with `~`): list the directory, keep prefix matches, expand
// `~`, trailing `/` on directories. NEVER runs anything — a directory read
// only. Callers pass words that already look like paths.
let private filesystemComplete (word: string) : string list =
    let expanded =
        if word.StartsWith "~" then
            System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
            + word.Substring 1
        else
            word

    let slash = expanded.LastIndexOf '/'

    let dir =
        if slash < 0 then "."
        elif slash = 0 then "/"
        else expanded.Substring(0, slash)

    let prefix = expanded.Substring(slash + 1)

    try
        // the SESSION cwd, not the process cwd [F1]: weir never chdir's the
        // process, so a relative dir here resolves against the STARTUP
        // directory and goes stale the moment a script or the REPL cd's —
        // completion then vouches for a path File.read immediately rejects.
        // Session.resolve is what File.read and Path.glob already use.
        System.IO.Directory.GetFileSystemEntries(
            if System.IO.Path.IsPathRooted dir then
                dir
            else
                Session.resolve dir
        )
        |> Array.filter (fun e ->
            let name = System.IO.Path.GetFileName e
            // the dotfile law, mirrored from Path.glob: a leading `.` must be
            // TYPED to be offered, so a bare Tab does not bury real entries
            // under .git/.DS_Store
            name.StartsWith prefix && (prefix.StartsWith "." || not (name.StartsWith ".")))
        |> Array.map (fun e ->
            // the candidate must EXTEND the typed word — the editor
            // replaces the word with it, so a shape the user never
            // typed (./x for a bare name) re-prepends on every tab
            // [D:complete-argv]
            let shaped =
                if slash < 0 then
                    System.IO.Path.GetFileName e
                else
                    word.Substring(0, word.Length - prefix.Length) + System.IO.Path.GetFileName e

            if System.IO.Directory.Exists e then
                shaped + "/"
            else
                shaped)
        |> Array.sort
        |> Array.toList
    with _ ->
        []

/// where the completion WORD starts, scanning back from the cursor. ONE rule,
/// consulted by the REPL and the LSP alike: each had a verbatim copy, and when
/// argv path completion landed only `filesystemComplete` learned about `/` —
/// the callers kept cutting the word AT the slash, so `micro ci/e` completed
/// against the CWD and `micro ci/` listed it whole. A path separator is part
/// of the word; `~` leads a home path; `-` is ordinary in real filenames
/// (`ci/check-fresh.sh` is in this repo, and without it the word restarts at
/// the hyphen and the bug returns for every hyphenated name).
let wordStartAt (text: string) (pos: int) : int =
    let isWordChar (c: char) =
        System.Char.IsLetterOrDigit c
        || c = '_'
        || c = '.'
        || c = '/'
        || c = '~'
        || c = '-'

    let mutable i = min pos text.Length

    while i > 0 && isWordChar text[i - 1] do
        i <- i - 1

    // a Windows DRIVE PREFIX belongs to the path it introduces: `:` is not a
    // word char (it separates a record field from its type, and a yaml key
    // from its value), so the scan stops after `C:` and leaves a driveless
    // `/Users/…` that resolves nowhere. Extend across exactly the drive
    // shape — ONE letter, itself preceded by a non-word char — so `key:value`
    // and `{ a: 1 }` are untouched.
    if
        i >= 2
        && text[i - 1] = ':'
        && System.Char.IsLetter text[i - 2]
        && (i = 2 || not (isWordChar text[i - 3]))
    then
        i - 2
    else
        i

// the topLet binder up to its `=`, lexically [D:let-rhs-head] — the
// shape whose RHS the grammar grants command mode (topLet's
// command-first RHS; a destructuring let's RHS is expression-only).
// Subset, stated (the pathParamAt precedent): identifier and `()`
// params only — a parenthesized/record param pattern falls through,
// and a keyword in a binder slot is the letKeywordGuard's error, not
// a head slot. `let pure` leads legally; a lone `pure` does not bind.
let private letBinder =
    System.Text.RegularExpressions.Regex @"^let\s+(?:[A-Za-z_]\w*|\(\s*\))(?:\s+(?:[A-Za-z_]\w*|\(\s*\)))*\s*="

/// the text AFTER a top-level let binder's `=`, or None when `stmt`
/// does not open with one
let private letRhsCut (stmt: string) : string option =
    let m = letBinder.Match stmt

    if not m.Success then
        None
    else
        let names =
            match
                m.Value.TrimEnd([| '='; ' '; '\t' |]).Split([| ' '; '\t'; '('; ')' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
            with
            | "let" :: "pure" :: (_ :: _ as rest) -> rest
            | "let" :: rest -> rest
            | _ -> []

        if
            not names.IsEmpty
            && names |> List.forall (Weir.Parser.keywords.Contains >> not)
        then
            Some(stmt.Substring m.Length)
        else
            None

// the command HEAD SLOT, one source [D:let-rhs-head]: where a live
// head verdict and the head-completion pool apply — the statement
// start, and a top-level let's RHS. BOTH surfaces read this predicate
// (the Tab pool here, the live colorizer in Script.colorizeRepl), so
// tint and completion cannot disagree about where a head stands. The
// two head flavours stay distinct for the colorizer's red: an unknown
// UPPERCASE statement head would fail ([D:constructors-not-heads]),
// the same word at the let-RHS is a legal constructor application.
[<RequireQualifiedAccess>]
type HeadSlot =
    | No
    | Stmt
    | LetRhs
    /// behind the `^` force-PATH sigil at a head slot: PATH only
    | Forced

/// the slot verdict for the word starting right after `before`
/// [D:let-rhs-head]. Statement head = an EMPTY statement prefix,
/// deliberately untrimmed: the colorizer's continuation lines (a yaml
/// district body among them) must not read as heads, while the
/// completer's own TrimEnd already collapses a whitespace prefix.
let headSlotAt (before: string) : HeadSlot =
    let stmt =
        let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
        if i >= 0 then before.Substring(i + 1) else before

    let core, forced =
        if stmt.EndsWith "^" then
            stmt.Substring(0, stmt.Length - 1), true
        else
            stmt, false

    if core = "" then
        if forced then HeadSlot.Forced else HeadSlot.Stmt
    else
        match letRhsCut (core.TrimStart()) with
        | Some rest when rest.Trim() = "" -> if forced then HeadSlot.Forced else HeadSlot.LetRhs
        | _ -> HeadSlot.No

// a word in command ARGV completes as a PATH [D:complete-argv]: after
// a literal command head everything is an argv word — fields, members,
// and the keyword pool are expression furniture (`micro publish.`
// offered every record field weir knows, unioned). The head test is
// the resolver's own approximation: an identifier- or path-shaped
// first token that no binding, builtin, or module claims — or a ^
// forced external.
let private commandArgvPosition (env: TypeEnv) (before: string) : bool =
    let stmt =
        let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
        (if i >= 0 then before.Substring(i + 1) else before).TrimStart()

    // a top-level let's RHS is a first-class command position
    // [D:let-rhs-head]: strip the binder and judge the remainder
    // exactly as a statement — argv after the RHS head follows the
    // statement-head rule. A yaml district marker RHS keeps the OLD
    // path (the ` = ` disqualifier below): `let d = yaml …` is form,
    // not a command, and its schema=/modifier slots sit later in the
    // chain [D:yaml-schemas]
    let stmt =
        match letRhsCut stmt with
        | Some rest when
            (let r = rest.Trim()

             not (
                 Weir.Parser.isYamlMarkerPiece r
                 || (r.EndsWith "schema="
                     && Weir.Parser.isYamlMarkerPiece (r.Substring(0, r.Length - 7).TrimEnd()))
             ))
            ->
            rest.TrimStart()
        | _ -> stmt

    // |> and a spaced = are expression furniture — `xs |> from` is a
    // pipeline whose head happens to be unbound, not a command
    // an unclosed '(' puts the cursor in EXPRESSION position — a
    // command's parenthesized argument is the interior grammar
    let parenDepth =
        stmt
        |> Seq.fold
            (fun d c ->
                if c = '(' then d + 1
                elif c = ')' then d - 1
                else d)
            0

    if stmt.Contains "|>" || stmt.Contains " = " || parenDepth > 0 then
        false
    else
        match
            stmt.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
        with
        | None -> false
        | Some h when h.StartsWith "^" -> true
        | Some h when h.StartsWith "./" || h.StartsWith "/" -> true
        | Some h ->
            h.Length > 0
            && (System.Char.IsLetter h[0] || h[0] = '_')
            && not (h.Contains '.')
            && not (Weir.Parser.keywords.Contains h)
            && not (Map.containsKey h env.Values)
            && not (Map.containsKey h env.Modules)

// a word at a path PARAMETER position [D:path-param-completion]:
// `cd w` offered keywords and bare members around the one real
// candidate — cd is a builtin, so the argv gate correctly does not
// apply; the question that generalises is "does this parameter want a
// path?". The registry IS builtinDocs' named params — every module
// member has an entry (the docs-coverage pin), so a param named
// path/src/dst (base within Path) marks the position and a new
// member's docs enrol it automatically. SUBSET, stated: flat calls
// only — word-count resolves the argument index, so a quoted argument
// containing spaces or a nested call miscounts and falls through to
// the general pool.
let private pathParamAt (before: string) : bool =
    let seg =
        let cutAfter (marker: string) (s: string) =
            // Ordinal, load-bearing: culture-sensitive LastIndexOf
            // treats the U+001F sibling separator as IGNORABLE and
            // "matches" past the end
            match s.LastIndexOf(marker, System.StringComparison.Ordinal) with
            | -1 -> s
            | i -> s.Substring(i + marker.Length)

        before
        |> cutAfter Weir.Parser.sibSepStr
        |> cutAfter "\n"
        |> cutAfter " = "
        |> cutAfter "|>"
        |> _.TrimStart()

    match
        seg.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    with
    | [] -> false
    // within cd <path-or-binding> — the one path-typed within kind
    | "within" :: args -> args = [ "cd" ]
    | head :: args ->
        let head = head.TrimStart '('

        match Map.tryFind head Weir.Builtins.builtinDocs with
        | Some d when d.Params.Length > args.Length ->
            match List.tryItem args.Length d.Params with
            | Some p -> p = "path" || p = "src" || p = "dst" || (p = "base" && head.StartsWith "Path.")
            | None -> false
        | _ -> false

// binder evidence in the raw TEXT [D:complete-argv]: lambda params,
// let/for/within binders are lexically visible even when no typed tree
// exists — the declared-fields fallback keys on THIS, so an unbound
// scrutinee offers nothing (a wrong suggestion is worse than none, the
// D5 rule) while a mid-edit param keeps the high-signal union
let private lexicallyBound (name: string) (text: string) : bool =
    let re (pat: string) =
        [ for m in System.Text.RegularExpressions.Regex.Matches(text, pat) -> m.Groups[1].Value ]

    let funParams =
        re @"\bfun\s+([A-Za-z_]\w*(?:\s+[A-Za-z_]\w*)*)"
        |> List.collect (fun g -> g.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList)

    // a let's PARAMS are binders too: `let quality t =` binds t
    let letParams =
        re @"\blet\s+[A-Za-z_]\w*((?:\s+[A-Za-z_]\w*)+)\s*="
        |> List.collect (fun g -> g.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList)

    funParams
    @ letParams
    @ re @"\blet\s+([A-Za-z_]\w*)"
    @ re @"\bfor\s+([A-Za-z_]\w*)"
    @ re @"\bwithin\s+\w+\s+([A-Za-z_]\w*)"
    |> List.contains name

// the binder SCOPE can be wider than the completion text: the LSP
// completes one line but its binders (a let's params two lines up)
// live in the whole document [D:complete-argv]
// `aliasHeads` [D:command-head-alias]: the session's alias names — they
// resolve in command-head position only, so they join exactly the
// head-slot pool (never argv, never the `^`-forced PATH pool: the
// sigil skips the table). Scripts and the LSP pass none (REPL-only).
let suggestScopedWith
    (aliasHeads: Set<string>)
    (env: TypeEnv)
    (binderScope: string)
    (text: string)
    (wordStart: int)
    : string list =
    let word =
        if wordStart >= text.Length then
            ""
        else
            text.Substring wordStart

    let before = text.Substring(0, min wordStart text.Length).TrimEnd()

    // the word butts against an OPEN quote [D:repl-path-quote]: a
    // completion word inside a string literal has no space before it —
    // wordStartAt stops at the `"` (not a word char), so the char at
    // wordStart-1 is the opening quote. `File.read "./w<TAB>` completes
    // WITHIN the quotes; the path-quoting below must not add a second.
    let insideOpenQuote =
        wordStart > 0 && wordStart <= text.Length && text[wordStart - 1] = '"'

    // session directives complete at a line-head '#' [D:repl-directives]:
    // '#' is not a word char to the editor, so the word starts AFTER it
    // — `#he` used to complete to `head` (the '#' ignored), `#` listed
    // the general pool. Bare names returned so the editor's replacement
    // yields `#help`, never `##help`. The set mirrors Repl's dispatch
    // (session directives only — #sig/#schema are file directives and
    // the unknown-directive teaching already routes them).
    let directiveSlot =
        let raw = text.Substring(0, min wordStart text.Length)
        raw.TrimStart() = "#"

    // the `#help <arg>` slot [D:help-arg-complete]: a sibling to
    // directiveSlot — it fires when the text before the word is
    // `#help` (completing `#help <TAB>`) or `#help <partial>`, and
    // offers the #help DOCUMENTABLE universe (helpNames — modules,
    // types, user forms) BARE (so the editor yields `#help Patatas`,
    // never doubling the `#help `). This is the gap: an #infer'd type
    // lives only in env.Types, which the general/head pool never
    // surfaces, so it was documentable but not completable. Qualified
    // `#help Module.member` mirrors #help's dotted arm when a module
    // prefix is typed. Scoped to this slot — the general pool is
    // untouched.
    let helpArgSlot =
        // the whole line up to the cursor, trimmed of the word — must
        // reduce to exactly `#help` (the directive owns the rest of its
        // own line, so a sibling separator or newline earlier bars it)
        let before = text.Substring(0, min wordStart text.Length)
        before.TrimStart() = "#help "

    if directiveSlot then
        sessionDirectives |> List.filter (fun d -> d.StartsWith word) |> List.sort
    elif helpArgSlot then
        // a `Module.` prefix completes that module's members — the same
        // qualified names `#help Module.member` documents
        match word.Split '.' with
        | [| head; _ |] when Map.containsKey head env.Modules ->
            let memberPrefix = word.Substring(head.Length + 1)

            let special =
                Weir.Check.specialModuleMembers |> Map.tryFind head |> Option.defaultValue []

            Seq.append (Map.keys env.Modules[head]) special
            |> Seq.filter (fun m -> m.StartsWith memberPrefix)
            |> Seq.distinct
            |> Seq.map (fun m -> $"{head}.{m}")
            |> Seq.sort
            |> List.ofSeq
        | _ ->
            helpNames env
            |> List.filter (fun n -> n.StartsWith word && n <> word)
            |> List.distinct
            |> List.sort
    else


        // the record-update WITH slot [D:with-slot]: after `{ source with `,
        // the closed candidate set is the SOURCE's record fields — never the
        // general pool (typing `h` there completed to `within`, a keyword
        // that cannot appear in an expression fragment). The source is found
        // by slicing back to each `{` and letting the PARSER validate the
        // slice (no second quote machine [D:one-scanner]); `match x with`
        // has no parsing brace-slice, so it falls through untouched.
        // resolve `{ <source> ...` back to its record def: slice at each
        // '{' and let the PARSER validate (the with-slot machinery, shared)
        let sourceDefOf (upto: string) : RecordDef option =
            [ for i in 0 .. upto.Length - 1 do
                  if upto[i] = '{' then
                      yield i ]
            |> List.rev
            |> List.tryPick (fun bi ->
                let slice = upto.Substring(bi + 1).Trim()

                if slice = "" then
                    None
                else
                    match Weir.Parser.parseExpr slice with
                    | Error _ -> None
                    | Ok e ->
                        match Weir.Check.typecheck (withHoles env e) e with
                        | Error _ -> None
                        | Ok te ->
                            match te.Ty with
                            | TNamed(n, _) ->
                                match Map.tryFind n env.Types with
                                | Some(Record d) -> Some d
                                | _ -> None
                            | _ -> None)

        // the typed VALUE slot [D:typed-value-slot]: `{ src with field =
        // <prefix>` — the field's DECLARED type is known, so the candidate
        // set is CLOSED where the type is: a union's cases, bool's two
        // values (plus bool bindings), a unit type's module and bindings.
        // Other types fall through to the general pool.
        let valueSlotCandidates: string list option =
            let b = before.TrimEnd()

            if not (b.EndsWith "=") || b.EndsWith "==" then
                None
            else
                let beforeEq = b.Substring(0, b.Length - 1).TrimEnd()

                let fieldStart =
                    let mutable i = beforeEq.Length

                    while i > 0 && (System.Char.IsLetterOrDigit beforeEq[i - 1] || beforeEq[i - 1] = '_') do
                        i <- i - 1

                    i

                let field = beforeEq.Substring fieldStart

                if field = "" then
                    None
                else
                    // the nearest preceding `with` keyword bounds the source
                    let head = beforeEq.Substring(0, fieldStart)
                    let wi = head.LastIndexOf "with"

                    let isWordBounded =
                        wi >= 0
                        && (wi = 0 || not (System.Char.IsLetterOrDigit head[wi - 1] || head[wi - 1] = '_'))
                        && (wi + 4 >= head.Length
                            || not (System.Char.IsLetterOrDigit head[wi + 4] || head[wi + 4] = '_'))

                    if not isWordBounded then
                        None
                    else
                        sourceDefOf (head.Substring(0, wi))
                        |> Option.bind (fun def -> def.Fields |> List.tryFind (fst >> (=) field))
                        |> Option.bind (fun (_, fty) ->
                            let typedBindings (t: Ty) =
                                env.Values
                                |> Map.toList
                                |> List.choose (fun (n, sch) ->
                                    if Types.isUserName n && sch.Forall.IsEmpty && sch.Ty = t then
                                        Some n
                                    else
                                        None)

                            match fty with
                            | TBool -> Some([ "false"; "true" ] @ typedBindings TBool)
                            | TNamed(n, []) ->
                                match Map.tryFind n env.Types with
                                | Some(Union u) -> Some(u.Cases |> List.map fst)
                                | _ -> None
                            | TDur -> Some("Duration" :: typedBindings TDur)
                            | TSize -> Some("Size" :: typedBindings TSize)
                            | TBytes -> Some("Bytes" :: typedBindings TBytes)
                            | TSecret -> Some("Secret" :: typedBindings TSecret)
                            | _ -> None)

        let withSlotFields: string list option =
            let endsWithWord (kw: string) (b: string) =
                b.EndsWith kw
                && (b.Length = kw.Length
                    || not (
                        System.Char.IsLetterOrDigit b[b.Length - kw.Length - 1]
                        || b[b.Length - kw.Length - 1] = '_'
                    ))

            if not (endsWithWord "with" before) then
                None
            else
                let uptoWith = before.Substring(0, before.Length - 4)

                [ for i in 0 .. uptoWith.Length - 1 do
                      if uptoWith[i] = '{' then
                          yield i ]
                |> List.rev
                |> List.tryPick (fun bi ->
                    let slice = uptoWith.Substring(bi + 1).Trim()

                    if slice = "" then
                        None
                    else
                        match Weir.Parser.parseExpr slice with
                        | Error _ -> None
                        | Ok e ->
                            match Weir.Check.typecheck (withHoles env e) e with
                            | Error _ -> None
                            | Ok te ->
                                match te.Ty with
                                | TVar _ when
                                    not (
                                        System.Text.RegularExpressions.Regex.IsMatch(slice.Trim(), @"^[A-Za-z_]\w*$")
                                        && not (lexicallyBound (slice.Trim()) binderScope)
                                    )
                                    ->
                                    // unresolved source — the declared-fields
                                    // fallback, the dotted arm's precedent;
                                    // a bare UNBOUND identifier source gets
                                    // nothing instead [D:complete-argv]
                                    env.Types
                                    |> Map.toList
                                    |> List.collect (fun (_, def) ->
                                        match def with
                                        | Record d -> d.Fields |> List.map fst
                                        | Union _ -> [])
                                    |> List.distinct
                                    |> Some
                                | TVar _ -> Some []
                                | ty ->
                                    match recordFields env ty with
                                    | Some fields -> Some(fields |> List.map fst)
                                    // a known NON-record has no updatable
                                    // fields — a closed set with no members
                                    // beats the general pool (the nats pin's
                                    // reasoning)
                                    | None -> Some [])

        // EXPRESSION-position path completion QUOTES [D:repl-path-quote]:
        // a bare filesystem path is not a valid weir expression, so
        // `File.read ./x<TAB>` must yield `File.read "./x"` (a string
        // literal parses; a bare path does not). The distinction is the
        // slot's, reusing the existing head/argv machinery: a COMMAND-argv
        // path stays BARE (`cat ./x`, `ls ./dir`, `./script`), and a path a
        // builtin's string PARAMETER wants (File.read/cd/Path.*) — or one in
        // plain expression furniture (after `=`, a `|>` stage, a `(`) — gets
        // quoted. An already-OPEN quote never doubles: the completion lands
        // inside it. A directory keeps its trailing `/` inside the quotes.
        let expressionSlot (before: string) : bool =
            pathParamAt before
            || (let stmt =
                    let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
                    (if i >= 0 then before.Substring(i + 1) else before)

                stmt.Contains "|>"
                || stmt.Contains " = "
                || stmt.Contains '('
                || stmt.Contains '[')

        let quoteFor (before: string) (candidates: string list) : string list =
            if not insideOpenQuote && expressionSlot before then
                candidates |> List.map (fun c -> "\"" + c + "\"")
            else
                candidates

        if word.StartsWith "~" || word.Contains '/' then
            // an explicit path word — filesystem entries [D:repl-quality],
            // quoted where the slot is an expression [D:repl-path-quote]
            filesystemComplete word |> quoteFor before
        elif headSlotAt before = HeadSlot.Forced && word <> "" then
            // the `^`-forced head [D:let-rhs-head]: the sigil skips
            // bindings and the alias table, so the pool is exactly the
            // PATH cache — bare (the word starts after the sigil), at
            // the statement head and the let-RHS alike
            Extern.names () |> Set.filter (fun n -> n.StartsWith word && n <> word) |> Set.toList
        elif commandArgvPosition env before then
            // argv position [D:complete-argv]: paths, nothing else — the
            // pool, fields, and members are expression furniture
            filesystemComplete word
        elif pathParamAt before then
            // a path position wants paths AND string bindings — `cd
            // target` applies the binding, so hard-removing identifiers
            // would break documented weir; keywords and bare members
            // cannot be arguments here [D:path-param-completion]
            let stringBindings =
                env.Values
                |> Map.toList
                |> List.choose (fun (n, sch) ->
                    if Types.isUserName n && sch.Forall.IsEmpty && sch.Ty = TStr then
                        Some n
                    else
                        None)

            // this branch fires for a BARE word (no `/`) at a builtin path
            // param — it stays UNQUOTED [D:repl-path-quote]: `cd w`/`File.copy
            // … dst` complete the bare stem (the [D:path-param-completion]
            // pins), and a string BINDING here IS already a valid expression.
            // An EXPLICIT path word (`File.read ./x`, with a `/` or `~`) took
            // the quoting branch above before reaching here.
            (filesystemComplete word @ stringBindings)
            |> List.filter (fun c -> c.StartsWith word && c <> word)
            |> List.distinct
            |> List.sort
        elif word.Contains '.' then
            let segments = word.Split '.'
            let head = segments[0]
            let path = segments[1 .. segments.Length - 2] |> Array.toList
            let prefix = segments[segments.Length - 1]

            let moduleMembers =
                if not (Map.containsKey head env.Values) then
                    Map.tryFind head env.Modules
                else
                    None

            match moduleMembers with
            | Some members ->
                let prefix = word.Substring(head.Length + 1)

                // bespoke ARMS (`Args.load`/`Env.load`) are not in the member
                // map — offer them too, from the one source the checker uses
                let special =
                    Weir.Check.specialModuleMembers |> Map.tryFind head |> Option.defaultValue []

                Seq.append (Map.keys members) special
                |> Seq.filter (fun m -> m.StartsWith prefix)
                |> Seq.distinct
                |> Seq.sort
                |> Seq.map (fun m -> $"{head}.{m}")
                |> List.ofSeq
            | None ->

                let headTy =
                    match Map.tryFind head env.Values with
                    | Some sch -> Some sch.Ty
                    | None -> pipelineElemTy env (text.Substring(0, wordStart))

                let finalTy =
                    path
                    |> List.fold
                        (fun acc seg ->
                            acc
                            |> Option.bind (recordFields env)
                            |> Option.bind (List.tryFind (fst >> (=) seg))
                            |> Option.map snd)
                        headTy

                let render (fields: string list) =
                    let stem = word.Substring(0, word.Length - prefix.Length)

                    fields
                    |> List.filter (fun f -> f.StartsWith prefix)
                    |> List.sort
                    |> List.map (fun f -> stem + f)

                match finalTy with
                | Some ty ->
                    // resolved head: fields if a record, NOTHING if a known
                    // non-record (the nats pin — the fallback must not fire)
                    match recordFields env ty with
                    | Some fields -> render (fields |> List.map fst)
                    | None -> []
                | None when lexicallyBound head binderScope ->
                    // UNRESOLVABLE but lexically BOUND head (a lambda
                    // param, a mid-edit binder): no typed tree, but the
                    // nominal records keep the fallback high-signal —
                    // every declared record's fields
                    // [D:declared-fields-fallback]
                    env.Types
                    |> Map.toList
                    |> List.collect (fun (_, def) ->
                        match def with
                        | Record d -> d.Fields |> List.map fst
                        | Union _ -> [])
                    |> List.distinct
                    |> render
                | None ->
                    // UNBOUND head: no binding, no type, no basis for any
                    // candidate — nothing beats the union of everything
                    // [D:complete-argv]
                    []
        elif valueSlotCandidates.IsSome then
            valueSlotCandidates.Value
            |> List.filter (fun c -> c.StartsWith word && c <> word)
            |> List.distinct
            |> List.sort
        elif withSlotFields.IsSome then
            withSlotFields.Value
            |> List.filter (fun f -> f.StartsWith word && f <> word)
            |> List.sort
        elif
            (let b = before.TrimEnd() in

             b.EndsWith "within"
             && (b.Length = "within".Length
                 || not (System.Char.IsLetterOrDigit b[b.Length - 7] || b[b.Length - 7] = '_')))
        then
            // the within KIND slot [D:within-kinds]: a closed set off the one
            // table — the kinds and NOTHING else (an identifier cannot sit
            // there); the schema= shape, its mechanism kin
            Weir.Ast.withinKinds
            // a standalone head (pure) is not a `within` kind [D:pure-stage1]
            |> List.filter (fun k -> not k.Standalone)
            |> List.map (fun k -> k.Name)
            |> List.filter (fun k -> k.StartsWith word && k <> word)
        elif
            (let b = before.TrimEnd() in

             (b.EndsWith "from" || b.EndsWith "to")
             && (let kw = if b.EndsWith "from" then "from" else "to" in

                 b.Length = kw.Length
                 || not (
                     System.Char.IsLetterOrDigit b[b.Length - kw.Length - 1]
                     || b[b.Length - kw.Length - 1] = '_'
                 )))
        then
            // the from/to ADAPTER slot [D:form-word-hover]: direction-aware, off
            // the one source (builtinDocs keys) — `to ` never offers a read-only
            // adapter; a closed set, so NOTHING else completes here (the within
            // and schema= slots' third sibling — closed-set slot completion)
            let dir = if (before.TrimEnd()).EndsWith "from" then "from" else "to"

            Weir.Builtins.adapterNames dir
            |> List.filter (fun a -> a.StartsWith word && a <> word)
        elif
            before.EndsWith "schema="
            && Weir.Parser.isYamlMarkerPiece (before.Substring(0, before.Length - 7).TrimEnd())
        then
            // the vendored schema NAMES [D:yaml-schemas] — `schema` itself is
            // MARKER-LOCAL, deliberately not a Parser.keywords member (that
            // would reserve the identifier); the district marker context
            // offers it and its completions instead
            // the session cwd, as above — `.` here is the startup dir forever
            match Weir.Contracts.findWeirDir (Session.Cwd()) with
            | Ok weirDir ->
                let dir = System.IO.Path.Combine(weirDir, "schemas")

                if System.IO.Directory.Exists dir then
                    System.IO.Directory.GetFiles(dir, "*.json")
                    |> Array.map System.IO.Path.GetFileNameWithoutExtension
                    |> Array.filter (fun n -> n.StartsWith word && n <> word)
                    |> Array.sort
                    |> Array.toList
                else
                    []
            | Error _ -> []
        elif word = "" && Weir.Parser.isYamlMarkerPiece (before.TrimEnd()) then
            [ "schema=" ]
        elif before.EndsWith "to yaml" && "stream".StartsWith word && word <> "stream" then
            // the write-side stream word [D:yaml-seq-doc] — the one thing
            // that follows a to-adapter
            [ "stream" ]
        elif
            before.EndsWith "from json"
            || before.EndsWith "from jsonl"
            || before.EndsWith "from yaml"
            || before.EndsWith "from yaml stream"
        then
            // record names complete in the type slot; yaml's slot also
            // offers the stream cardinality word [D:wire-unions]
            let names =
                env.Types
                |> Map.toList
                |> List.choose (fun (n, def) ->
                    match def with
                    | Record _ when n.StartsWith word -> Some n
                    | _ -> None)
                |> List.sort

            if before.EndsWith "from yaml" && "stream".StartsWith word && word <> "stream" then
                "stream" :: names
            else
                names
        elif word = "" && before = "" then
            // an EMPTY prompt at a statement head [D:empty-prompt-directives]:
            // `StartsWith ""` used to match the WHOLE pool — 954 PATH execs +
            // the modules/keywords/constructors universe, sorted, unusable. A
            // fresh Tab now teaches the REPL's affordances instead: the session
            // directives, `#help` first (which itself lists the modules and
            // members). The argv-position empty Tab is unaffected — its cwd
            // listing is useful, and it keys on `before` being non-empty below.
            sessionDirectives |> List.map (fun d -> "#" + d)
        else
            // command HEADS at a head slot (the statement head and the
            // let-RHS, headSlotAt's verdict [D:let-rhs-head]): PATH
            // executables + command-callable builtins join the name pool;
            // in argv position cwd files join instead — the two
            // interactive contexts completion could not serve before
            // [D:repl-quality]
            let cwdEntries () =
                try
                    System.IO.Directory.GetFileSystemEntries(Session.Cwd())
                    |> Array.map (fun e ->
                        let n = System.IO.Path.GetFileName e
                        if System.IO.Directory.Exists e then n + "/" else n)
                    |> Array.toList
                with _ ->
                    []

            let extra =
                match headSlotAt before with
                | (HeadSlot.Stmt | HeadSlot.LetRhs) when word <> "" ->
                    // both head slots take the SAME pool [D:let-rhs-head]:
                    // the statement head and the let-RHS — PATH, the
                    // command-callable builtins, and the session's alias
                    // heads [D:command-head-alias]. An empty word at the
                    // let-RHS stays pool-only (the empty-prompt rationale
                    // — never the 900-name dump); the empty statement
                    // head routed to the directive teaching above
                    (Extern.names () |> Set.toList)
                    @ (Builtins.commandCallable |> Set.toList)
                    @ (aliasHeads |> Set.toList)
                | _ when word <> "" -> cwdEntries ()
                | _ -> []

            // a union-case CONSTRUCTOR never starts a statement
            // [D:constructors-not-heads]: `WriteFile …`/`Bearer …` as a head
            // is a discarded value (a check error). They pass isUserName, so
            // the pool can't tell one from a function — subtract them AT THE
            // STATEMENT HEAD (before empty) only, derived from env.Types'
            // Union cases (the one source). An expression/argument position
            // (before non-empty — a list literal, a `let … in`) keeps them,
            // where a constructor is valid.
            let unionCases =
                if before = "" then
                    env.Types
                    |> Map.toList
                    |> List.collect (fun (_, def) ->
                        match def with
                        | Union u -> u.Cases |> List.map fst
                        | Record _ -> [])
                    |> Set.ofList
                else
                    Set.empty

            (List.ofSeq (Map.keys env.Values |> Seq.filter Types.isUserName |> Seq.filter (unionCases.Contains >> not))
             @ List.ofSeq (Map.keys env.Modules)
             @ keywords
             @ extra)
            |> List.filter (fun n -> n.StartsWith word && n <> word)
            |> List.distinct
            |> List.sort

/// the LSP's entry: no session aliases (scripts never see the table)
let suggestScoped (env: TypeEnv) (binderScope: string) (text: string) (wordStart: int) : string list =
    suggestScopedWith Set.empty env binderScope text wordStart

// value-aware MAP-KEY completion [D:value-key-complete]: an open
// map's keys are DATA (`data: seq<string * string>` from #infer), so
// no static pool can know them — but the session's Values table can.
// Inside the OPEN string literal keying a pipe-form Map lookup
// (get/tryGet/has — the Map surface's lookup members), when the
// receiver is a BARE session binding (`it` included) whose stored
// value is a map or a MATERIALIZED pair-seq, the value's own keys
// complete. Reading the table is peeking, never evaluating: an
// unforced seq offers NOTHING (pulling a command-backed seq would run
// its command — the never-executes law's sharp edge), and a pipeline
// receiver (`cm |> from json … |> _.data |> Map.tryGet "`) offers
// nothing (its value would need evaluating; bind first — `let d = …`
// — and the keys complete). Once the lookup shape is detected the
// slot is CLAIMED: keys or nothing, never the general pool (a keyword
// inside a key literal is the wrong-suggestion class). Pipe-form
// only, stated: in the applied spelling (`Map.get "k" m`) the
// receiver FOLLOWS the key, so nothing exists to peek at while the
// literal is typed. Subset, stated: the literal scan is a naive quote
// count — an escaped quote in the key falls outside it (the slot then
// does not fire). The completion inserts the key bare, inside the
// quotes — the user owns the closer, the path-in-quotes convention
// [D:repl-path-quote].
let private mapLookupHead =
    System.Text.RegularExpressions.Regex @"^(.*?)\s*\|>\s*Map\.(?:get|tryGet|has)$"

let private valueKeySlot (values: Map<string, Weir.Eval.Value>) (text: string) (wordStart: int) : string list option =
    let segStart =
        max (text.LastIndexOf Weir.Parser.sibSep) (text.LastIndexOf '\n') + 1

    let seg = text.Substring segStart
    let qi = seg.LastIndexOf '"'

    if qi < 0 || (seg |> Seq.filter ((=) '"') |> Seq.length) % 2 = 0 then
        None
    else
        let head =
            let h = seg.Substring(0, qi).Trim()

            // the let-RHS is the same slot [D:let-rhs-head]: strip the
            // binder and judge the remainder as a statement
            match letRhsCut h with
            | Some rest -> rest.Trim()
            | None -> h

        let m = mapLookupHead.Match head

        if not m.Success then
            None
        else
            let recv = m.Groups[1].Value.Trim()

            let keys =
                if System.Text.RegularExpressions.Regex.IsMatch(recv, @"^[A-Za-z_]\w*$") then
                    match Map.tryFind recv values with
                    | Some(Weir.Eval.VMap entries) -> Map.keys entries |> List.ofSeq
                    | Some(Weir.Eval.VSeq items) ->
                        // the one materialization probe — forced offers,
                        // unforced offers nothing [D:value-key-complete]
                        match Weir.Eval.forcedItems items with
                        | Some list ->
                            let pairKeys =
                                list
                                |> List.choose (function
                                    | Weir.Eval.VTuple [ Weir.Eval.VStr k; _ ] -> Some k
                                    | _ -> None)

                            if pairKeys.Length = list.Length then pairKeys else []
                        | None -> []
                    | _ -> []
                else
                    []

            let literal = text.Substring(segStart + qi + 1)
            let word = if wordStart >= text.Length then "" else text.Substring wordStart
            // the candidate must EXTEND the typed word [D:complete-argv]:
            // the literal may reach left of the word (a space inside a
            // typed key) — strip that stem from each offered key
            let stem = literal.Substring(0, literal.Length - word.Length)

            keys
            |> List.filter (fun k -> k.StartsWith literal && k <> literal && not (k.Contains '"'))
            |> List.map (fun k -> k.Substring stem.Length)
            |> List.distinct
            |> List.sort
            |> Some

// Error-recovery completion [D:repair-completion]: the caller
// REPAIRS the broken statement (dangling
// `.prefix` blanked, closers appended) and this types the repaired
// text — holes for stragglers — then reads the head identifier's
// INFERRED type at its column. Row types from the statement's other
// uses of the param surface here.
/// the single-text entry: the completion text IS the binder scope
/// (the REPL's one logical line)
let suggest (env: TypeEnv) (text: string) (wordStart: int) : string list = suggestScoped env text text wordStart

/// the REPL's entry: the session's alias heads join the head-slot
/// pool [D:command-head-alias], and the session VALUES feed the
/// map-key slot [D:value-key-complete] — keys when the slot claims,
/// the general machinery otherwise
let suggestSession
    (aliasHeads: Set<string>)
    (values: Map<string, Weir.Eval.Value>)
    (env: TypeEnv)
    (text: string)
    (wordStart: int)
    : string list =
    match valueKeySlot values text wordStart with
    | Some candidates -> candidates
    | None -> suggestScopedWith aliasHeads env text text wordStart

let fieldsAtRepaired
    (parse: string -> Result<Weir.Ast.Stmt, string>)
    (env: TypeEnv)
    (repaired: string)
    (head: string)
    : string list option =
    match parse repaired with
    | Error _ -> None
    | Ok stmt ->
        let exprOf =
            match stmt with
            | Weir.Ast.SLet(_, e) -> Some e
            | Weir.Ast.SLetPat(_, e) -> Some e
            | Weir.Ast.SExpr e
            | Weir.Ast.SCmd e -> Some e
            | Weir.Ast.SType _
            | Weir.Ast.SSig _
            | Weir.Ast.SModule _
            | Weir.Ast.SImport _ -> None

        exprOf
        |> Option.bind (fun e ->
            let envH = withHoles env e

            match Weir.Check.typecheck envH e with
            | Error _ -> None
            | Ok te ->
                // ANY occurrence serves: a param's uses share one type,
                // and the cursor's own occurrence was blanked away
                let rec find (best: Ty option) (node: Weir.Check.TypedExpr) =
                    let best =
                        match node.Kind with
                        | Weir.Check.TEVar n when n = head -> Some node.Ty
                        | _ -> best

                    Weir.Check.childExprs node |> List.fold find best

                find None te)
        |> Option.bind (fun ty ->
            match ty with
            | TRowVar(_, fields) ->
                // an OPEN row (the `..` tail) is compatible with any
                // declared record it fits inside — offer those records'
                // FULL field sets too, so editing the one line that
                // demanded a field does not hide it [D:open-row-compat]
                let known = fields |> List.map fst

                let compatible =
                    env.Types
                    |> Map.toList
                    |> List.collect (fun (_, def) ->
                        match def with
                        | Record d ->
                            let fits =
                                fields
                                |> List.forall (fun (f, ft) ->
                                    match d.Fields |> List.tryFind (fst >> (=) f) with
                                    | None -> false
                                    | Some(_, rt) ->
                                        (match ft with
                                         | TVar _ -> true
                                         | _ -> formatTy ft = formatTy rt))

                            if fits then d.Fields |> List.map fst else []
                        | Union _ -> [])

                Some(known @ compatible |> List.distinct)
            | _ -> recordFields env ty |> Option.map (List.map fst))
