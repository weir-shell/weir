module Weir.Complete

open Weir.Types

// keyword suggestions derive from the parser's set — one source of
// truth [D:keyword-completion]; a hand-kept copy silently under-offers
// newly added keywords. The exclusions: rec and mutable are reserved
// words with no meaning (offering them suggests a spelling whose only
// outcome is the reserved-word teaching error); function is reserved
// for the parked match-lambda sugar; the foreign control-flow words
// (PLAN-dx-review D4) exist only to teach weir's spelling — none of
// them can ever parse.
let unsuggestedKeywords = Set [ "rec"; "mutable"; "function"; "while"; "return" ]

let private keywords = Weir.Parser.keywords - unsuggestedKeywords |> Set.toList

/// the `#infer ` prefix on a one-shot `#infer let <name> = <src>` line
/// [D:infer-one-shot]: dropping it leaves an ordinary `let <name> = <src>`
/// statement, so coloring and completion strip `#infer ` and treat the
/// rest as that statement — the RHS keeps its value/head context, which
/// stripping to the bare source would lose. Returns the offset of `let`,
/// or None when this is not a one-shot line.
let inferLetStrip (line: string) : int option =
    let m =
        System.Text.RegularExpressions.Regex.Match(line, @"^\s*#infer\s+(?=let\s+[A-Za-z_][A-Za-z0-9_]*\s*=)")

    if m.Success then Some m.Length else None

/// the interior start of the innermost still-open interpolation hole in
/// `text` (everything up to the cursor), or None when the cursor is not
/// inside a `$"{ … }` hole. The hole interior is an ordinary expression,
/// so completion recurses on it — `$"{Path.<TAB>` completes Path's
/// members, not a filesystem path. A stated subset (like the map-key
/// scanner): a nested string inside a hole is skipped by balanced `"`,
/// escapes are not tracked.
let interpHoleStart (text: string) : int option =
    let mutable i = 0
    let mutable inStr = false // inside a $"…" interpolation string
    let mutable depth = 0 // brace nesting inside the current hole
    let mutable holeStart = -1 // interior start of the innermost open hole

    while i < text.Length do
        if not inStr then
            if i + 1 < text.Length && text[i] = '$' && text[i + 1] = '"' then
                inStr <- true
                i <- i + 2
            else
                i <- i + 1
        elif depth = 0 then
            // inside the string's literal text, between holes
            match text[i] with
            | '"' -> inStr <- false
            | '{' ->
                depth <- 1
                holeStart <- i + 1
            | _ -> ()

            i <- i + 1
        else
            // inside a hole (an expression)
            match text[i] with
            | '{' -> depth <- depth + 1
            | '}' ->
                depth <- depth - 1

                if depth = 0 then
                    holeStart <- -1
            | '"' ->
                // skip a nested string literal so its braces/quotes don't
                // confuse the scan; an unterminated one runs to the end
                i <- i + 1

                while i < text.Length && text[i] <> '"' do
                    i <- i + 1
            | _ -> ()

            i <- i + 1

    if inStr && depth > 0 then Some holeStart else None

// the session directives, one source [D:repl-directives] — the set the
// REPL's dispatch string-matches (Repl.fs) and #help documents, `#help`
// first. Both completion slots read this: the line-head '#' slot (bare
// names — the editor's word starts after the '#') and the empty-prompt
// head [D:empty-prompt-directives] (the '#'-prefixed teaching set).
//
// Each entry carries its #help lines, so the list `#help` prints, the
// dispatch's did-you-mean and completion are the one table
let sessionDirectiveHelp: (string * string list) list = [
    "help",
    [
        "#help                 // this list"
        "#help <name>          // documentation for a module or member"
    ]
    "keys", [ "#keys                 // the editor's key bindings" ]
    "find",
    [
        "#find [query]         // fuzzy-search modules and members (fzf + live preview)"
    ]
    "echo",
    [
        "#echo [<n> | all]     // the unforced-echo cap (default 100); bare reports;"
        "                      //   all = no cap — an infinite seq will hang (Ctrl+C)"
    ]
    "types",
    [
        "#types [on | off]     // show the echo's types (default off; config \"types\"); bare reports"
    ]
    "infer",
    [
        "#infer [<src>] from <json|jsonl|yaml|table> as <Name|_>"
        "                      //   draft named types from a sample (src defaults to 'it'; _ auto-names)"
        "#infer let <x> = <src> |> from <fmt> as <Name|_>"
        "                      //   one shot: draft the types and bind the parsed value (runs once)"
    ]
    "save",
    [
        "#save <path>          // dump the session's accepted lines to a runnable .weir"
    ]
    "history",
    [
        "#history [<n>]        // show history (bare = all, <n> = last n); prints the file path"
    ]
    "alias",
    [
        "#alias [name = cmd …] // bare lists; a command-head alias (init.weir is canonical)"
    ]
    "quit", [ "#quit                 // leave the REPL (Ctrl+D works too)" ]
]

let sessionDirectives = sessionDirectiveHelp |> List.map fst

// the `#help` documentable universe, one source [D:help-arg-complete]:
// the bare names `#help <name>` can document — modules, top-level
// forms/members (user values), and types (records and unions, the
// #infer-injected ones included). Both `#help`'s unknown-name pool
// (Repl.fs) and the #help-arg completion slot read this, so what
// `#help X` documents and what `#help <TAB>` offers cannot drift.
// Modules also document their
// members via `#help Module.member`; the qualified names live in the
// slot itself, keyed off env.Modules.
let helpNames (env: TypeEnv) : string list =
    List.concat [
        Map.keys env.Modules |> List.ofSeq
        Map.keys env.Values |> Seq.filter Types.isUserName |> List.ofSeq
        Map.keys env.Types |> List.ofSeq
    ]
    |> List.distinct

// an anonymous record's def may live only in the mid-statement table
// [D:anon-literals] — typeDefFor reads both
let private recordFields (env: TypeEnv) (ty: Ty) : (string * Ty) list option =
    match ty with
    | TNamed(n, _) ->
        match Types.typeDefFor env n with
        | Some(Record def) -> Some def.Fields
        | _ -> None
    | _ -> None

// unbound lowercase names in the prefix become holes (fresh type
// vars): the pipe source often mentions enclosing params
// (`targetEnv t |> ...`) whose values are unknown but irrelevant —
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
        (fun (te: TypeEnv) (n, sch) -> {
            te with
                Values = Map.add n sch te.Values
        })
        env

// the type flowing into this position: the type of the value the pipe
// carries to the completion word. A `seq` unwraps to its element (the
// `_.`/lambda-param over each row); a scalar flows through as itself —
// `from yaml T` yields one document `TNamed(name,[])`, not a seq, so a
// record piped into `_.`/`r |> _.` must type to the record, not `None`.
// The sole caller routes through `recordFields`, which returns `None`
// for a non-record, so the catch-all offers nothing where nothing fits.
//
// The pipe's left side may open with statement furniture the
// expression parser refuses — a `for <pat> in` header or a `let x =`
// binder (`for p in ls |> _.`): that lead is cut first, the rest is the
// source [D:entry-complete]
//
// A bare shorthand stage (`ls |> _.`, the stage is nothing but `_.f`)
// applies the record lambda to the whole value: over a seq that never
// type-checks, so the seq does not unwrap there and nothing is offered
// (user-ruled [D:entry-complete]); a record flowing in still offers its
// fields, and an argument (`|> map _.`, `Seq.where _.`) unwraps as before
// the `for` binder is whole words (`\S+\s+` units split a space run one
// way only, so a long run never backtracks), and the match is time-bounded
// besides: completion runs on the user's text
let private pipeSourceLead =
    System.Text.RegularExpressions.Regex(
        @"^(?:for\s+(?:\S+\s+)+?in\s+|let\s+[A-Za-z_]\w*\s*=(?!=)\s*)",
        System.Text.RegularExpressions.RegexOptions.Singleline,
        System.TimeSpan.FromMilliseconds 100.0
    )

let private pipelineElemTy (env: TypeEnv) (bareStage: bool) (text: string) : Ty option =
    match text.LastIndexOf '|' with
    | -1 -> None
    | i ->
        let prefix =
            let p = text.Substring(0, i).Trim()

            let m =
                try
                    pipeSourceLead.Match p
                with :? System.Text.RegularExpressions.RegexMatchTimeoutException ->
                    System.Text.RegularExpressions.Match.Empty

            if m.Success then p.Substring m.Length else p

        match Weir.Parser.parseExpr prefix with
        | Error _ -> None
        | Ok e ->
            let envWithHoles = withHoles env e

            match Weir.Check.typecheck envWithHoles e with
            | Ok te ->
                match te.Ty with
                | TSeq elem when not bareStage -> Some elem
                | t -> Some t
            | Error _ -> None

/// the word sits alone in a pipeline stage: the text before it ends with
/// the pipe itself (`ls |> _.`, a `|> _.` continuation line)
let bareStageBefore (before: string) : bool = before.TrimEnd().EndsWith "|>"

/// text ends at the cursor (both callers truncate — the LSP's `upto`,
/// the REPL's Substring): the word runs from wordStart to the end
// filesystem completion [D:repl-quality] for an explicit path word (has a
// `/` or leads with `~`): list the directory, keep prefix matches,
// trailing `/` on directories. A leading `~` completes to the literal home
// path [D:tilde-literal] — the line shows what will actually run. Never runs anything — a directory
// read only. Callers pass words that already look like paths.
let private filesystemComplete (word: string) : string list =
    let expanded =
        if word.StartsWith "~" then
            (try
                Builtins.homeDir ()
             with _ ->
                 "~")
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
        // the session cwd, not the process cwd [F1]: weir never chdir's the
        // process, so a relative dir here resolves against the startup
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
            // typed to be offered, so a bare Tab does not bury real entries
            // under .git/.DS_Store
            name.StartsWith prefix && (prefix.StartsWith "." || not (name.StartsWith ".")))
        |> Array.map (fun e ->
            // the candidate must extend the typed word — the editor
            // replaces the word with it, so a shape the user never
            // typed (./x for a bare name) re-prepends on every tab
            // [D:complete-argv]
            let typed = if word.StartsWith "~" then expanded else word

            let shaped =
                if slash < 0 then
                    System.IO.Path.GetFileName e
                else
                    typed.Substring(0, typed.Length - prefix.Length) + System.IO.Path.GetFileName e

            if System.IO.Directory.Exists e then
                shaped + "/"
            else
                shaped)
        |> Array.sort
        |> Array.toList
    with _ -> []

// a completed path spelled for where it lands [D:repl-path-quote]. Inside
// an open literal it is escaped for that literal's kind (`@"` doubles a
// quote, `$"` doubles a brace, `"""` has no escapes); as a fresh string
// literal it is quoted and escaped; as an argv word it stays bare unless a
// character would end or split the word, then it becomes a string literal
// (one argv word, `"my file.txt"`).
let private escapeFor (opener: string) (s: string) : string =
    match opener with
    | "@\"" -> s.Replace("\"", "\"\"")
    | "\"\"\"" -> s
    | "$\"" -> s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}")
    | _ -> s.Replace("\\", "\\\\").Replace("\"", "\\\"")

let private stringLiteral (s: string) = "\"" + escapeFor "\"" s + "\""

let bareArgvSafe (s: string) =
    s <> ""
    && s
       |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || "._-/~+,=:%@^".IndexOf c >= 0)

let private argvWord (s: string) =
    if bareArgvSafe s then s else stringLiteral s

// the literal a completion word sits inside, read from its opening quote
let private openerAt (text: string) (wordStart: int) : string =
    let ends (o: string) =
        wordStart >= o.Length && text.Substring(wordStart - o.Length, o.Length) = o

    if ends "\"\"\"" then "\"\"\""
    elif ends "@\"" then "@\""
    elif ends "$\"" then "$\""
    else "\""

/// where the completion word starts, scanning back from the cursor. One
/// rule, consulted by the REPL and the LSP alike — diverging copies once
/// cut the word at the slash, completing `micro ci/e` against the cwd. A
/// path separator is part of the word; `~` leads a home path; `-` is
/// ordinary in real filenames (`ci/check-fresh.sh` is in this repo) —
/// without it the word restarts at the hyphen for every hyphenated name.
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

    // inside an open string literal (not a hole) the word is the
    // literal's content so far [D:open-literal-word]
    let pre = text.Substring(0, min pos text.Length)

    // one pass: the opener is the first quote after the last character
    // outside any string
    if i > 0 && Weir.Scan.endsInsideString pre && (interpHoleStart pre).IsNone then
        let lastOut = Weir.Scan.foldOutsideStrings (fun _ j _ -> j) -1 pre
        let q = pre.IndexOf('"', lastOut + 1)

        if q >= 0 then
            let content =
                if System.String.CompareOrdinal(pre, q, "\"\"\"", 0, 3) = 0 then
                    q + 3
                else
                    q + 1

            if content < i then
                i <- content

    // a Windows drive prefix belongs to the path it introduces: `:` is not a
    // word char (it separates a record field from its type, and a yaml key
    // from its value), so the scan stops after `C:` and leaves a driveless
    // `/Users/…` that resolves nowhere. Extend across exactly the drive
    // shape — one letter, itself preceded by a non-word char — so `key:value`
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
// A stated subset (matching pathParamAt): identifier and `()`
// params only — a parenthesized/record param pattern falls through,
// and a keyword in a binder slot is the letKeywordGuard's error, not
// a head slot. `let pure` leads legally; a lone `pure` does not bind.
let private letBinder =
    System.Text.RegularExpressions.Regex @"^let\s+(?:[A-Za-z_]\w*|\(\s*\))(?:\s+(?:[A-Za-z_]\w*|\(\s*\)))*\s*="

/// the text after a top-level let binder's `=`, or None when `stmt`
/// does not open with one
let private letRhsCut (stmt: string) : string option =
    let m = letBinder.Match stmt

    if not m.Success then
        None
    else
        let names =
            match
                m.Value
                    .TrimEnd([| '='; ' '; '\t' |])
                    .Split([| ' '; '\t'; '('; ')' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
            with
            | "let" :: "pure" :: (_ :: _ as rest) -> rest
            | "let" :: rest -> rest
            | _ -> []

        if not names.IsEmpty && names |> List.forall (Weir.Parser.keywords.Contains >> not) then
            Some(stmt.Substring m.Length)
        else
            None

// the command head slot, one source [D:let-rhs-head]: where a live
// head verdict and the head-completion pool apply — the statement
// start, and a top-level let's RHS. Both surfaces read this predicate
// (the Tab pool here, the live colorizer in Script.colorizeRepl), so
// tint and completion cannot disagree about where a head stands. The
// two head flavours stay distinct for the colorizer's red: an unknown
// uppercase statement head would fail ([D:constructors-not-heads]),
// the same word at the let-RHS is a legal constructor application.
[<RequireQualifiedAccess>]
type HeadSlot =
    | No
    | Stmt
    | LetRhs
    /// behind the `^` force-PATH sigil at a head slot: PATH only
    | Forced
    /// a pipeline stage's head: after a bare `|`, or after `| and`/`| or`
    | Stage

// leading `NAME=value ` words ride before a command head [D:env-prefix]:
// the head slot looks through them (a quoted value may hold spaces)
let private envPrefixRx =
    System.Text.RegularExpressions.Regex("^(?:[A-Za-z_][A-Za-z0-9_]*=(?:\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*'|\\S*)\\s+)+")

let stripEnvPrefixes (s: string) : string =
    let m = envPrefixRx.Match s
    if m.Success then s.Substring m.Length else s

/// the text after the last bare `|` of a statement — not `|>`, not
/// `||`, not inside quotes or parens — with a leading `and`/`or` chain
/// word stripped. The flag is true when the stage follows the `|`
/// directly, where a reifier or `and`/`or` may stand [D:cmd-chaining].
let lastStage (stmt: string) : (string * bool) option =
    let mutable last = -1
    let mutable quote = ' '
    let mutable depth = 0
    let mutable i = 0

    while i < stmt.Length do
        let c = stmt[i]

        if quote <> ' ' then
            if c = '\\' && quote = '"' then
                i <- i + 1
            elif c = quote then
                quote <- ' '
        elif c = '"' || c = '\'' then
            quote <- c
        elif c = '(' then
            depth <- depth + 1
        elif c = ')' then
            depth <- max 0 (depth - 1)
        elif c = '|' && depth = 0 then
            if i + 1 < stmt.Length && (stmt[i + 1] = '>' || stmt[i + 1] = '|') then
                i <- i + 1
            elif i > 0 && stmt[i - 1] = '|' then
                ()
            else
                last <- i

        i <- i + 1

    // a `|` with nothing before it, or in a match/function, is an arm
    let isArm () =
        stmt.Substring(0, last).Trim() = ""
        || System.Text.RegularExpressions.Regex.IsMatch(stmt.Substring(0, last), "\\b(match|function)\\b")

    if last < 0 || isArm () then
        None
    else
        let rest = stmt.Substring(last + 1).TrimStart()
        let m = System.Text.RegularExpressions.Regex.Match(rest, "^(and|or)(\\s+|$)")

        if m.Success then
            Some(rest.Substring m.Length, false)
        else
            Some(rest, true)

/// the slot verdict for the word starting right after `before`
/// [D:let-rhs-head]. Statement head = an empty statement prefix,
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

    // `before` arrives trimmed: the prefix's own blank is restored for
    // the strip; a word glued to `=` is the value [D:env-prefix-head-complete]
    let envOnly (s: string) =
        not (s.TrimEnd().EndsWith "=") && (stripEnvPrefixes (s + " ")).Trim() = ""

    if core = "" || (envOnly core && not (core.StartsWith " ")) then
        if forced then HeadSlot.Forced else HeadSlot.Stmt
    else
        match letRhsCut (core.TrimStart()) with
        | Some rest when rest.Trim() = "" || envOnly (rest.TrimStart()) ->
            if forced then HeadSlot.Forced else HeadSlot.LetRhs
        | _ ->
            match lastStage core with
            | Some(rest, _) when rest.Trim() = "" || envOnly rest -> if forced then HeadSlot.Forced else HeadSlot.Stage
            | _ -> HeadSlot.No

// every type name a program can spell [D:type-name-complete]: the
// built-in names the type grammar reads, declared and imported types,
// and each import's qualified `M.T`
let private builtinTypeNames = [
    "int"
    "string"
    "bool"
    "unit"
    "float"
    "Duration"
    "Instant"
    "Uuid"
    "Size"
    "Bytes"
    "Secret"
    "seq"
    "Option"
    "Result"
    "Map"
]

let private declaredTypeNames (env: TypeEnv) : string list =
    let declared =
        env.Types
        |> Map.keys
        |> Seq.filter (fun n -> n <> "" && System.Char.IsUpper n[0])

    let qualified =
        env.ModuleTypes
        |> Map.toSeq
        |> Seq.collect (fun (m, tys) -> tys |> Map.keys |> Seq.map (fun t -> m + "." + t))

    Seq.append declared qualified |> Seq.distinct |> List.ofSeq

/// a type position inside a `type` declaration: the statement so far
/// opens with `type` and ends where a type is written
let typePositionAt (stmtBefore: string) : bool =
    let s = stmtBefore.TrimStart()
    let t = s.TrimEnd()

    s.StartsWith "type "
    && not (t.EndsWith "[<")
    && (t.EndsWith ":"
        || t.EndsWith "*"
        || t.EndsWith "<"
        || t.EndsWith ","
        || t.EndsWith "->"
        || t.EndsWith "("
        || System.Text.RegularExpressions.Regex.IsMatch(t, @"\sof$"))

/// type names for a type position, prefix-filtered
let typeNameOffers (env: TypeEnv) (word: string) : string list =
    builtinTypeNames @ declaredTypeNames env
    |> List.filter (fun n -> n.StartsWith word && n <> word)
    |> List.distinct
    |> List.sort

// an adapter's type slot: `from <fmt> [stream] [seq<|Map<string, ]`
let private adapterSlot =
    System.Text.RegularExpressions.Regex(
        @"\bfrom\s+(json|jsonl|yaml|toml|xml|table)(\s+stream)?(\s+(seq\s*<|Map\s*<\s*string\s*,))?\s*$",
        System.Text.RegularExpressions.RegexOptions.None,
        System.TimeSpan.FromMilliseconds 100.0
    )

/// the types a `from` slot admits — the checker decides, by checking
/// the slot with each candidate [D:type-name-complete]
let private adapterOffers (env: TypeEnv) (slot: string) (word: string) : string list =
    let closer =
        if slot.TrimEnd().EndsWith "<" || slot.TrimEnd().EndsWith "," then
            ">"
        else
            ""

    let admits (n: string) =
        match Weir.Parser.parseExpr ("[\"\"] |> " + slot.Trim() + " " + n + closer) with
        | Ok e -> (Weir.Check.typecheck env e) |> Result.isOk
        | Error _ -> false

    declaredTypeNames env
    |> List.filter (fun n -> n.StartsWith word && n <> word && admits n)
    |> List.sort

// a validated type's derived `create` [D:validated-types]: the one way in
let private isValidated (env: TypeEnv) (typeKey: string) : bool =
    match Types.typeDefFor env typeKey with
    | Some(Union u) -> u.Validated.IsSome
    | _ -> false

// a keyed head's open option keys [D:head-key-complete]
let private keyValueAtom = @"(?:""(?:[^""\\]|\\.)*""|\([^()]*\)|[^\s()""]+)"

let private loopHeadKeys =
    System.Text.RegularExpressions.Regex(@"^(retry|poll|repeat)((?:\s+[A-Za-z_]\w*=" + keyValueAtom + @")*)$")

let private lockHeadKeys =
    System.Text.RegularExpressions.Regex(@"^within\s+lock\s+" + keyValueAtom + @"$")

let headKeyOffers (env: TypeEnv) (before: string) : string list option =
    let stmt =
        let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
        (if i >= 0 then before.Substring(i + 1) else before).Trim()

    let stmt = letRhsCut stmt |> Option.map _.Trim() |> Option.defaultValue stmt
    let m = loopHeadKeys.Match stmt

    if m.Success then
        let head = m.Groups[1].Value

        let fields =
            match Map.tryFind (string (System.Char.ToUpperInvariant head[0]) + head.Substring 1) env.Types with
            | Some(Record d) -> d.Fields |> List.map fst
            | _ -> []

        let keys = if head = "poll" then fields @ [ "watch" ] else fields

        let given =
            System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"\s([A-Za-z_]\w*)=")
            |> Seq.map _.Groups[1].Value
            |> Set.ofSeq

        Some(keys |> List.filter (given.Contains >> not) |> List.map (fun k -> k + "="))
    elif lockHeadKeys.IsMatch stmt then
        Some [ "timeout=" ]
    else
        None

// a `within` head, bare or as a `let … = within` RHS — precompiled and
// time-bounded: `\s+` and `.*` overlap, so a long space run after `let`
// re-scans the line once per give-back (quadratic), and the line is the
// document's, exactly as pipeSourceLead above
let private untilWithinHead =
    System.Text.RegularExpressions.Regex(
        @"^(let\s+.*=\s*)?within\b",
        System.Text.RegularExpressions.RegexOptions.None,
        System.TimeSpan.FromMilliseconds 100.0
    )

// `until stopped` after a within's block [D:until-stopped-complete]
let untilStoppedAt (above: string list) (before: string) : bool =
    let indent (s: string) = s.Length - s.TrimStart().Length

    before.Trim() = "until"
    && (let col = indent before

        above
        |> List.rev
        |> List.filter (fun l -> l.Trim() <> "" && not (l.TrimStart().StartsWith "//"))
        |> List.tryFind (fun l -> indent l <= col)
        |> Option.exists (fun l ->
            indent l = col
            && (try
                    untilWithinHead.IsMatch(l.TrimStart())
                with :? System.Text.RegularExpressions.RegexMatchTimeoutException ->
                    false)))

// the yaml marker's open modifiers, canonical order [D:head-key-complete]
let private yamlModifierOffers (marker: string) : string list =
    let toks =
        marker.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)

    let mods =
        match Array.tryFindIndexBack ((=) "yaml") toks with
        | Some i -> toks[i + 1 ..]
        | None -> [||]

    let has (p: string -> bool) = mods |> Array.exists p
    let patch = has ((=) "patch")
    let by = has (fun t -> t.StartsWith "by=")
    let schema = has (fun t -> t.StartsWith "schema=")

    [
        if not patch && not by && not schema then
            yield "patch"
        if patch && not by && not schema then
            yield "by="
        if not schema then
            yield "schema="
    ]

// a word in command argv completes as a path [D:complete-argv]: after
// a literal command head everything is an argv word — fields, members,
// and the keyword pool are expression furniture (`micro publish.`
// offered every record field weir knows, unioned). The head test is
// the resolver's own approximation: an identifier- or path-shaped
// first token that no binding, builtin, or module claims — or a ^
// forced external. `anyHead` drops the claimed-name test: a `$`
// splice word is argv-like after any head [D:argv-splice-complete].
let private commandArgvStmt (env: TypeEnv) (anyHead: bool) (before: string) : string option =
    // inside an unclosed `$(`, the captured command [D:expr-paths-quoted]
    let before =
        let opens =
            Weir.Scan.foldOutsideStrings
                (fun (st: int list) i c ->
                    if c = '(' then
                        i :: st
                    elif c = ')' then
                        (match st with
                         | _ :: rest -> rest
                         | [] -> [])
                    else
                        st)
                []
                before

        match opens with
        | i :: _ when i > 0 && before[i - 1] = '$' -> before.Substring(i + 1)
        | _ -> before

    let stmt =
        let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
        (if i >= 0 then before.Substring(i + 1) else before).TrimStart()

    // a top-level let's RHS is a first-class command position
    // [D:let-rhs-head]: strip the binder and judge the remainder
    // exactly as a statement — argv after the RHS head follows the
    // statement-head rule. A yaml district marker RHS keeps the old
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
    // an unclosed '(' puts the cursor in expression position — a
    // command's parenthesized argument is the interior grammar
    let parenDepth =
        stmt
        |> Seq.fold
            (fun d c ->
                if c = '(' then d + 1
                elif c = ')' then d - 1
                else d)
            0

    // a pipeline's argv belongs to its last stage; an empty stage is a
    // head slot, never argv
    let stmt =
        match lastStage stmt with
        | Some(rest, _) when not (stmt.Contains "|>") -> rest
        | _ -> stmt

    if stmt.Contains "|>" || stmt.Contains " = " || parenDepth > 0 then
        None
    else
        let argv =
            match
                (stripEnvPrefixes (stmt + " ")).Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries)
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
                && (anyHead
                    || not (Map.containsKey h env.Values) && not (Map.containsKey h env.Modules))

        if argv then Some stmt else None

let private commandArgvPosition (env: TypeEnv) (anyHead: bool) (before: string) : bool =
    (commandArgvStmt env anyHead before).IsSome

/// the command whose argv the word after `before` sits in: its head as
/// written (env prefixes off, a `^` sigil kept) and the argv text after
/// it — None off an argv position [D:sig-arg-complete]
let commandArgvAt (env: TypeEnv) (before: string) : (string * string) option =
    match commandArgvStmt env false before with
    | None -> None
    | Some stmt ->
        let s = stripEnvPrefixes stmt
        let headEnd = s.IndexOfAny [| ' '; '\t' |]

        if headEnd <= 0 then
            None
        else
            let head = s.Substring(0, headEnd)

            if head.TrimStart '^' = "" then
                None
            else
                Some(head, s.Substring headEnd)

// a word at a path parameter position [D:path-param-completion]:
// cd is a builtin, so the argv gate correctly does not apply — the
// question that generalises is "does this parameter want a path?".
// The registry is builtinDocs' named params — every module member has
// an entry (the docs-coverage pin), so a param named path/src/dst
// (base within Path) marks the position and a new member's docs enrol
// it automatically. A stated subset: flat calls only — word-count
// resolves the argument index, so a quoted argument containing spaces
// or a nested call miscounts and falls through to the general pool.
let private pathParamAt (before: string) : bool =
    let seg =
        let cutAfter (marker: string) (s: string) =
            // Ordinal, load-bearing: culture-sensitive LastIndexOf
            // treats the U+001F sibling separator as ignorable and
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

        match Map.tryFind head Weir.Builtins.builtinDocs.Value with
        | Some d when d.Params.Length > args.Length ->
            match List.tryItem args.Length d.Params with
            | Some p -> p = "path" || p = "src" || p = "dst" || (p = "base" && head.StartsWith "Path.")
            | None -> false
        | _ -> false

// binder evidence in the raw text [D:complete-argv]: lambda params,
// let/for/within binders are lexically visible even when no typed tree
// exists — the declared-fields fallback keys on this, so an unbound
// scrutinee offers nothing (a wrong suggestion is worse than none, the
// D5 rule) while a mid-edit param keeps the high-signal union
let private lexicallyBound (name: string) (text: string) : bool =
    let re (pat: string) = [
        for m in System.Text.RegularExpressions.Regex.Matches(text, pat) -> m.Groups[1].Value
    ]

    let funParams =
        re @"\bfun\s+([A-Za-z_]\w*(?:\s+[A-Za-z_]\w*)*)"
        |> List.collect (fun g -> g.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList)

    // a let's params are binders too: `let quality t =` binds t
    let letParams =
        re @"\blet\s+[A-Za-z_]\w*((?:\s+[A-Za-z_]\w*)+)\s*="
        |> List.collect (fun g -> g.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList)

    funParams
    @ letParams
    @ re @"\blet\s+([A-Za-z_]\w*)"
    @ re @"\bfor\s+([A-Za-z_]\w*)"
    @ re @"\bwithin\s+\w+\s+([A-Za-z_]\w*)"
    |> List.contains name

// the `$` / `$@` sigil right before the completion word, if any
let private spliceSigil (text: string) (wordStart: int) : string option =
    if
        wordStart >= 2
        && wordStart <= text.Length
        && text.Substring(wordStart - 2, 2) = "$@"
    then
        Some "$@"
    elif wordStart >= 1 && wordStart <= text.Length && text[wordStart - 1] = '$' then
        Some "$"
    else
        None

// the innermost command a `$` belongs to [D:argv-splice-complete]: the
// text after the last `$(`, `then`, `else`, `do` or `->` outside strings
let private spliceSegment (pre: string) : string =
    let kwAt (i: int) (k: string) =
        i + k.Length <= pre.Length
        && System.String.CompareOrdinal(pre, i, k, 0, k.Length) = 0
        && (i = 0 || System.Char.IsWhiteSpace pre[i - 1])
        && (i + k.Length = pre.Length || System.Char.IsWhiteSpace pre[i + k.Length])

    let cut =
        Weir.Scan.foldOutsideStrings
            (fun cut i c ->
                if
                    (c = '$' && i + 1 < pre.Length && pre[i + 1] = '(')
                    || (c = '-' && i + 1 < pre.Length && pre[i + 1] = '>')
                then
                    i + 2
                elif kwAt i "then" || kwAt i "else" then
                    i + 4
                elif kwAt i "do" then
                    i + 2
                else
                    cut)
            0
            pre

    pre.Substring cut

// a `$name` splice completes exactly what the checker admits there
// [D:argv-splice-complete]: after `$`, the Check.argvSpliceable
// bindings; after `$@`, seq<string>; a record only through its fields
// ([D:field-splices] — `$r.<TAB>` offers r's fields the slot admits),
// never bare. Candidates are bare: the sigil is already typed
let private splicePool (env: TypeEnv) (sigil: string) (word: string) : string list =
    let admits (ty: Ty) =
        if sigil = "$@" then
            ty = TSeq TStr
        else
            Weir.Check.argvSpliceable ty

    if word.Contains '.' then
        let segs = word.Split '.'
        let prefix = segs[segs.Length - 1]
        let stem = word.Substring(0, word.Length - prefix.Length)

        let finalTy =
            segs[1 .. segs.Length - 2]
            |> Array.fold
                (fun acc seg ->
                    acc
                    |> Option.bind (recordFields env)
                    |> Option.bind (List.tryFind (fst >> (=) seg))
                    |> Option.map snd)
                (Map.tryFind segs[0] env.Values |> Option.map _.Ty)

        match finalTy |> Option.bind (recordFields env) with
        | Some fields ->
            fields
            |> List.filter (fun (f, ty) -> f.StartsWith prefix && admits ty)
            |> List.map (fun (f, _) -> stem + f)
            |> List.sort
        | None -> []
    else
        env.Values
        |> Map.toList
        |> List.choose (fun (n, sch) ->
            if Types.isUserName n && n.StartsWith word && n <> word && admits sch.Ty then
                Some n
            else
                None)
        |> List.sort

/// the `$` pool inside a yaml block [D:argv-splice-complete]: the
/// bindings the yaml district's splice law admits (Check.yamlSpliceableWith)
let yamlSplicePool (env: TypeEnv) (word: string) : string list =
    env.Values
    |> Map.toList
    |> List.choose (fun (n, sch) ->
        if
            Types.isUserName n
            && n.StartsWith word
            && n <> word
            && sch.Forall.IsEmpty
            && Weir.Check.yamlSpliceableWith id sch.Ty
        then
            Some n
        else
            None)
    |> List.sort

// a `$` the cursor line ends in (`pre`, the line up to it) is literal
// text, never a splice [D:argv-splice-complete]: in a `//` comment; in a
// `<<<` or `$<<<` body (a `$$<<<` body splices); in a yaml block, inside a
// quoted scalar or key, a `#` comment, or a `|` block scalar's content.
// `above`: the lines above the cursor's, in order — the district is read
// off its ancestors by indentation
let dollarLiteral (above: string[]) (pre: string) : bool =
    let indentOf (s: string) = s.Length - s.TrimStart().Length

    let piece (s: string) =
        let c = Weir.Scan.commentAt s
        (if c >= 0 then s.Substring(0, c) else s).Trim()

    // a yaml line's value: past its `- ` item marks and its `key:`
    let valueOf (t: string) =
        let mutable v = t.TrimStart()

        while v = "-" || v.StartsWith "- " do
            v <- if v = "-" then "" else v.Substring(2).TrimStart()

        match Yaml.splitKey v with
        | Some(_, rest) -> rest.TrimStart()
        | None -> v

    let yamlLiteral (chain: string list) =
        let quoted (v: string) = v.StartsWith "\"" || v.StartsWith "'"
        let t = pre.TrimStart()
        let bare = pre.TrimEnd(' ', '\t')

        chain
        |> List.exists (fun l -> Yaml.blockHeader (valueOf (Yaml.stripDistrictComment l)) |> Option.isSome)
        || t.StartsWith "#"
        || (Yaml.stripDistrictComment pre).Length < bare.Length
        || quoted t
        || quoted (valueOf t)

    // the nearest less indented line above, until a district marker
    let rec walk (ind: int) (j: int) (chain: string list) =
        let mutable k = j

        while k >= 0 && (above[k].Trim() = "" || indentOf above[k] >= ind) do
            k <- k - 1

        if k < 0 then
            false
        else
            let p = piece above[k]

            if Parser.isHeredocMarkerPiece p then
                not (p = "$$<<<" || p.EndsWith " $$<<<")
            elif Parser.isYamlMarkerPiece p then
                yamlLiteral chain
            else
                walk (indentOf above[k]) (k - 1) (above[k] :: chain)

    Weir.Scan.commentAt pre >= 0 || walk (indentOf pre) (above.Length - 1) []

// the binder scope can be wider than the completion text: the LSP
// completes one line but its binders (a let's params two lines up)
// live in the whole document [D:complete-argv]
// `aliasHeads` [D:command-head-alias]: the session's alias names — they
// resolve in command-head position only, so they join exactly the
// head-slot pool (never argv, never the `^`-forced PATH pool: the
// sigil skips the table). Scripts and the LSP pass none (REPL-only).
let rec suggestScopedWith
    (aliasHeads: Set<string>)
    (env: TypeEnv)
    (binderScope: string)
    (text: string)
    (wordStart: int)
    : string list =
    // a one-shot `#infer let x = <src>` completes as the `let x = <src>`
    // statement it wraps [D:infer-one-shot]: strip `#infer ` and recurse,
    // so the RHS keeps its let-value context (bare names and heads both
    // surface; the leading `#infer` was hiding it). The stripped text is
    // a plain `let`, so this recurses at most once.
    match inferLetStrip text with
    | Some off when wordStart >= off ->
        let stmt = text.Substring off
        suggestScopedWith aliasHeads env stmt stmt (wordStart - off)
    | _ ->

        // a cursor inside a `$"{ … }` hole completes the hole's interior as an
        // ordinary expression [D:interp-hole-complete] — otherwise the opaque
        // `$"…"` makes the slot detector read `micro $"{Path.` as command-argv
        // and try a filesystem completion of `Path.`. Strip to the interior
        // and recurse (innermost hole, so this recurses at most once per hole).
        match interpHoleStart text with
        | Some off when wordStart >= off ->
            // a hole is an expression, never a statement head: the `(`
            // closes the head slot and the directives [D:hole-is-expression]
            let inner = "(" + text.Substring off
            // the outer text stays the binder scope: a hole's head is
            // bound outside it (`for p in … do print $"{p.`) [D:entry-complete]
            suggestScopedWith aliasHeads env binderScope inner (wordStart - off + 1)
        | _ ->

            let word =
                if wordStart >= text.Length then
                    ""
                else
                    text.Substring wordStart

            let before = text.Substring(0, min wordStart text.Length).TrimEnd()

            // the word butts against an open quote [D:repl-path-quote]: a
            // completion word inside a string literal has no space before it —
            // wordStartAt stops at the `"` (not a word char), so the char at
            // wordStart-1 is the opening quote. `File.read "./w<TAB>` completes
            // within the quotes; the path-quoting below must not add a second.
            let insideOpenQuote =
                wordStart > 0 && wordStart <= text.Length && text[wordStart - 1] = '"'

            // session directives complete at a line-head '#' [D:repl-directives]:
            // '#' is not a word char to the editor, so the word starts after it.
            // Bare names are returned so the editor's replacement yields `#help`,
            // never `##help`. The set mirrors Repl's dispatch (session directives
            // only — #sig is a file directive and the unknown-directive
            // teaching already routes it).
            let directiveSlot =
                let raw = text.Substring(0, min wordStart text.Length)
                raw.TrimStart() = "#"

            // the `#help <arg>` slot [D:help-arg-complete]: a sibling to
            // directiveSlot — it fires when the text before the word is
            // `#help` (completing `#help <TAB>`) or `#help <partial>`, and
            // offers the #help documentable universe (helpNames — modules,
            // types, user forms) bare, so the editor yields `#help Patatas`
            // without doubling the `#help `. This fills the gap where an
            // #infer'd type lives only in env.Types, which the general/head
            // pool never surfaces — documentable but not completable.
            // Qualified `#help Module.member` mirrors #help's dotted arm when
            // a module prefix is typed. Scoped to this slot — the general pool
            // is untouched.
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


                // the record-update `with` slot [D:with-slot]: after `{ source with `,
                // the closed candidate set is the source's record fields — never the
                // general pool (a keyword cannot appear in an expression fragment).
                // The source is found by slicing back to each `{` and letting the
                // parser validate the slice (no second quote machine [D:one-scanner]);
                // `match x with` has no parsing brace-slice, so it falls through
                // untouched.
                // resolve `{ <source> ...` back to its record def: slice at each
                // '{' and let the parser validate (the with-slot machinery, shared)
                let sourceDefOf (upto: string) : RecordDef option =
                    [
                        for i in 0 .. upto.Length - 1 do
                            if upto[i] = '{' then
                                yield i
                    ]
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

                // the typed value slot [D:typed-value-slot]: `{ src with field =
                // <prefix>` — the field's declared type is known, so the candidate
                // set is closed where the type is: a union's cases, bool's two
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

                        [
                            for i in 0 .. uptoWith.Length - 1 do
                                if uptoWith[i] = '{' then
                                    yield i
                        ]
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
                                                System.Text.RegularExpressions.Regex.IsMatch(
                                                    slice.Trim(),
                                                    @"^[A-Za-z_]\w*$"
                                                )
                                                && not (lexicallyBound (slice.Trim()) binderScope)
                                            )
                                            ->
                                            // unresolved source — the declared-fields
                                            // fallback, as the dotted arm does;
                                            // a bare unbound identifier source gets
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
                                            // a known non-record has no updatable
                                            // fields — a closed set with no members
                                            // beats the general pool (the nats pin's
                                            // reasoning)
                                            | None -> Some [])

                // expression-position path completion quotes [D:repl-path-quote]:
                // a bare filesystem path is not a valid weir expression, so
                // `File.read ./x<TAB>` must yield `File.read "./x"` (a string
                // literal parses; a bare path does not). The distinction is the
                // slot's, reusing the existing head/argv machinery: a command-argv
                // path stays bare (`cat ./x`, `ls ./dir`, `./script`), and a path a
                // builtin's string parameter wants (File.read/cd/Path.*) — or one in
                // plain expression furniture (after `=`, a `|>` stage, a `(`) — gets
                // quoted. An already-open quote never doubles: the completion lands
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

                // `cd` heading a statement is the command form: its argument is
                // an argv word, so a path completes bare like `cat ./x`
                // [D:cd-bare-completion]; `let d = cd …`, `within cd …` and
                // `(cd …)` are expressions and keep the quotes
                let cdCommandArg (before: string) : bool =
                    let stmt =
                        let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
                        (if i >= 0 then before.Substring(i + 1) else before)

                    stmt.Split([| ' '; '\t' |], System.StringSplitOptions.RemoveEmptyEntries) = [| "cd" |]

                let opener = openerAt text wordStart

                // a word glued to a prefix (`--file=`, `if=`) cannot be
                // quoted alone [D:glued-argv-complete]
                let glued =
                    not insideOpenQuote
                    && wordStart > 0
                    && wordStart <= text.Length
                    && not (System.Char.IsWhiteSpace text[wordStart - 1])

                let argvWords (candidates: string list) : string list =
                    if glued then
                        candidates |> List.filter bareArgvSafe
                    else
                        candidates |> List.map argvWord

                let quoteFor (before: string) (candidates: string list) : string list =
                    if insideOpenQuote then
                        candidates |> List.map (escapeFor opener)
                    elif cdCommandArg before then
                        argvWords candidates
                    elif expressionSlot before then
                        candidates |> List.map stringLiteral
                    else
                        argvWords candidates

                let argvPaths (candidates: string list) : string list =
                    if insideOpenQuote then
                        candidates |> List.map (escapeFor opener)
                    else
                        argvWords candidates

                // a `$name` splice slot decides alone [D:argv-splice-complete]:
                // inside a string the `$` is literal, glued to a word it is a
                // word-join the parser refuses — nothing there; in a command's
                // argv (any head, inside `$(…)` and if/for/match bodies) the
                // checker's pool
                let splice =
                    match spliceSigil text wordStart with
                    | Some sigil ->
                        let at = wordStart - sigil.Length
                        let pre = text.Substring(0, at)

                        if Weir.Scan.endsInsideString pre || Weir.Scan.commentAt pre >= 0 then
                            Some []
                        elif at > 0 && not (pre[at - 1] = ' ' || pre[at - 1] = '\t') then
                            Some []
                        elif commandArgvPosition env true (spliceSegment pre + sigil) then
                            Some(splicePool env sigil word)
                        else
                            None
                    | None -> None

                // the import path slot [D:import-path-complete]: a string
                // literal naming a module — directories and .weir files
                let importArg =
                    let stmt =
                        let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
                        (if i >= 0 then before.Substring(i + 1) else before).Trim()

                    System.Text.RegularExpressions.Regex.IsMatch(stmt, "^import\\s*\"?$")

                let headKeys = headKeyOffers env before

                let adapter =
                    try
                        let m = adapterSlot.Match before
                        if m.Success then Some m.Value else None
                    with :? System.Text.RegularExpressions.RegexMatchTimeoutException ->
                        None

                let stmtBefore =
                    let i = max (before.LastIndexOf Weir.Parser.sibSep) (before.LastIndexOf '\n')
                    if i >= 0 then before.Substring(i + 1) else before

                if splice.IsSome then
                    splice.Value
                elif importArg then
                    filesystemComplete word
                    |> List.filter (fun c -> c.EndsWith "/" || c.EndsWith ".weir")
                    |> List.map (if insideOpenQuote then escapeFor opener else stringLiteral)
                elif headKeys.IsSome then
                    headKeys.Value |> List.filter (fun k -> k.StartsWith word && k <> word)
                elif adapter.IsSome then
                    // yaml's slot also takes the stream cardinality word
                    // [D:wire-unions]
                    let stream =
                        if
                            System.Text.RegularExpressions.Regex.IsMatch(adapter.Value, @"^from\s+yaml\s*$")
                            && "stream".StartsWith word
                            && word <> "stream"
                        then
                            [ "stream" ]
                        else
                            []

                    stream @ adapterOffers env adapter.Value word
                elif typePositionAt stmtBefore then
                    typeNameOffers env word
                elif word.StartsWith "~" || word.Contains '/' then
                    // an explicit path word — filesystem entries [D:repl-quality],
                    // quoted where the slot is an expression [D:repl-path-quote]
                    filesystemComplete word |> quoteFor before
                elif headSlotAt before = HeadSlot.Stage then
                    // a pipeline stage head [D:cmd-chaining]: programs only — the
                    // expression pool is noise here — and right after the `|` the
                    // reifiers and the and/or chain words; an empty word offers
                    // just those, never the whole PATH
                    let direct =
                        match lastStage before with
                        | Some(_, true) -> Ast.reifierNames @ [ "and"; "or" ]
                        | _ -> []

                    let programs =
                        if word = "" then
                            []
                        else
                            (Extern.names () |> Set.toList)
                            @ (Builtins.commandCallable |> Set.toList)
                            @ (aliasHeads |> Set.toList)

                    direct @ programs
                    |> List.filter (fun n -> n.StartsWith word && n <> word)
                    |> List.distinct
                    |> List.sort
                elif headSlotAt before = HeadSlot.Forced && word <> "" then
                    // the `^`-forced head [D:let-rhs-head]: the sigil skips
                    // bindings and the alias table, so the pool is exactly the
                    // PATH cache — bare (the word starts after the sigil), at
                    // the statement head and the let-RHS alike
                    Extern.names ()
                    |> Set.filter (fun n -> n.StartsWith word && n <> word)
                    |> Set.toList
                elif commandArgvPosition env false before then
                    // argv position [D:complete-argv]: paths, nothing else — the
                    // pool, fields, and members are expression furniture
                    filesystemComplete word |> argvPaths
                elif pathParamAt before then
                    // a path position wants paths and string bindings — `cd
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

                    // this branch fires for a bare word (no `/`) at a builtin path
                    // param — it stays unquoted [D:repl-path-quote]: `cd w`/`File.copy
                    // … dst` complete the bare stem (the [D:path-param-completion]
                    // pins), and a string binding here is already a valid expression.
                    // An explicit path word (`File.read ./x`, with a `/` or `~`) took
                    // the quoting branch above before reaching here. A stem a bare
                    // word cannot spell (a space, a quote) still comes back quoted.
                    let paths =
                        filesystemComplete word
                        |> List.filter (fun c -> c.StartsWith word && c <> word)
                        |> quoteFor before

                    (paths
                     @ (stringBindings |> List.filter (fun c -> c.StartsWith word && c <> word)))
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

                    // an import's types beside its members, a validated
                    // type's `create` in place of its refused case
                    // [D:type-name-complete]
                    let moduleTypes = Map.tryFind head env.ModuleTypes |> Option.defaultValue Map.empty

                    match moduleMembers with
                    | Some _ when
                        segments.Length = 3
                        && (moduleTypes |> Map.tryFind segments[1] |> Option.exists (isValidated env))
                        ->
                        if "create".StartsWith prefix then
                            [ $"{head}.{segments[1]}.create" ]
                        else
                            []
                    | Some members ->
                        let prefix = word.Substring(head.Length + 1)

                        // bespoke arms (`Args.load`/`Env.load`) are not in the member
                        // map — offer them too, from the one source the checker uses
                        let special =
                            Weir.Check.specialModuleMembers |> Map.tryFind head |> Option.defaultValue []

                        let refusedCases =
                            moduleTypes
                            |> Map.toList
                            |> List.collect (fun (_, key) ->
                                match Types.typeDefFor env key with
                                | Some(Union u) when u.Validated.IsSome -> u.Cases |> List.map fst
                                | _ -> [])
                            |> Set.ofList

                        Seq.append (Map.keys members) special
                        |> Seq.filter (refusedCases.Contains >> not)
                        |> Seq.append (Map.keys moduleTypes)
                        |> Seq.filter (fun m -> m.StartsWith prefix)
                        |> Seq.distinct
                        |> Seq.sort
                        |> Seq.map (fun m -> $"{head}.{m}")
                        |> List.ofSeq
                    | None when segments.Length = 2 && isValidated env head ->
                        if "create".StartsWith prefix then
                            [ $"{head}.create" ]
                        else
                            []
                    | None ->

                        let headTy =
                            match Map.tryFind head env.Values with
                            | Some sch -> Some sch.Ty
                            | None ->
                                pipelineElemTy env (head = "_" && bareStageBefore before) (text.Substring(0, wordStart))

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
                            // resolved head: fields if a record, nothing if a known
                            // non-record (the nats pin — the fallback must not fire)
                            match recordFields env ty with
                            | Some fields -> render (fields |> List.map fst)
                            | None -> []
                        | None when lexicallyBound head binderScope ->
                            // unresolvable but lexically bound head (a lambda
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
                            // unbound head: no binding, no type, no basis for any
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
                    // the within kind slot [D:within-kinds]: a closed set off the one
                    // table — the kinds and nothing else (an identifier cannot sit
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
                    // the from/to adapter slot [D:form-word-hover]: direction-aware, off
                    // the one source (builtinDocs keys) — `to ` never offers a read-only
                    // adapter; a closed set, so nothing else completes here (the within
                    // and schema= slots' third sibling — closed-set slot completion)
                    let dir = if (before.TrimEnd()).EndsWith "from" then "from" else "to"

                    Weir.Builtins.adapterNames dir
                    |> List.filter (fun a -> a.StartsWith word && a <> word)
                elif
                    before.EndsWith "schema="
                    && Weir.Parser.isYamlMarkerPiece (before.Substring(0, before.Length - 7).TrimEnd())
                then
                    // the vendored schema names [D:yaml-schemas] — `schema` itself is
                    // marker-local, deliberately not a Parser.keywords member (that
                    // would reserve the identifier); the district marker context
                    // offers it and its completions instead
                    // the session cwd, as above — a bare `.` would be the startup dir
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
                elif Weir.Parser.isYamlMarkerPiece (before.TrimEnd()) then
                    yamlModifierOffers before
                    |> List.filter (fun m -> m.StartsWith word && m <> word)
                elif before.EndsWith "to yaml" && "stream".StartsWith word && word <> "stream" then
                    // the write-side stream word [D:yaml-seq-doc] — the one thing
                    // that follows a to-adapter
                    [ "stream" ]
                elif word = "" && before = "" then
                    // an empty prompt at a statement head [D:empty-prompt-directives]:
                    // matching the whole pool (hundreds of PATH execs plus the
                    // modules/keywords/constructors universe) is unusable, so a
                    // fresh Tab teaches the REPL's affordances instead: the session
                    // directives, `#help` first (which itself lists the modules and
                    // members). The argv-position empty Tab is unaffected — its cwd
                    // listing is useful, and it keys on `before` being non-empty below.
                    sessionDirectives |> List.map (fun d -> "#" + d)
                else
                    // command heads at a head slot (the statement head and the
                    // let-RHS, headSlotAt's verdict [D:let-rhs-head]): PATH
                    // executables + command-callable builtins join the name pool
                    // [D:repl-quality]; a file name is never an expression
                    // [D:expr-paths-quoted]
                    let extra =
                        match headSlotAt before with
                        | (HeadSlot.Stmt | HeadSlot.LetRhs) when word <> "" ->
                            // both head slots take the same pool [D:let-rhs-head]:
                            // the statement head and the let-RHS — PATH, the
                            // command-callable builtins, and the session's alias
                            // heads [D:command-head-alias]. An empty word at the
                            // let-RHS stays pool-only (the empty-prompt rationale
                            // — never the whole-pool dump); the empty statement
                            // head routed to the directive teaching above
                            (Extern.names () |> Set.toList)
                            @ (Builtins.commandCallable |> Set.toList)
                            @ (aliasHeads |> Set.toList)
                        | _ -> []

                    // a union-case constructor never starts a statement
                    // [D:constructors-not-heads]: `WriteFile …`/`Bearer …` as a head
                    // is a discarded value (a check error). They pass isUserName, so
                    // the pool can't tell one from a function — subtract them at the
                    // statement head (before empty) only, derived from env.Types'
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

                    (List.ofSeq (
                        Map.keys env.Values
                        |> Seq.filter Types.isUserName
                        |> Seq.filter (unionCases.Contains >> not)
                     )
                     @ List.ofSeq (Map.keys env.Modules)
                     @ keywords
                     @ extra)
                    |> List.filter (fun n -> n.StartsWith word && n <> word)
                    |> List.distinct
                    |> List.sort

/// the LSP's entry: no session aliases (scripts never see the table)
let suggestScoped (env: TypeEnv) (binderScope: string) (text: string) (wordStart: int) : string list =
    suggestScopedWith Set.empty env binderScope text wordStart

// value-aware map-key completion [D:value-key-complete]: an open
// map's keys are data (`data: seq<string * string>` from #infer), so
// no static pool can know them — but the session's Values table can.
// Inside the open string literal keying a pipe-form Map lookup
// (get/tryGet/has — the Map surface's lookup members), when the
// receiver is a bare session binding (`it` included) whose stored
// value is a map or a materialized pair-seq, the value's own keys
// complete. Reading the table is peeking, never evaluating: an
// unforced seq offers nothing (pulling a command-backed seq would run
// its command — the never-executes law's sharp edge), and a pipeline
// receiver (`cm |> from json … |> _.data |> Map.tryGet "`) offers
// nothing (its value would need evaluating; bind first — `let d = …`
// — and the keys complete). Once the lookup shape is detected the
// slot is claimed: keys or nothing, never the general pool (a keyword
// inside a key literal is the wrong-suggestion class). Pipe-form
// only, stated: in the applied spelling (`Map.get "k" m`) the
// receiver follows the key, so nothing exists to peek at while the
// literal is typed. A stated subset: the literal scan is a naive quote
// count — an escaped quote in the key falls outside it (the slot then
// does not fire). The completion inserts the key bare, inside the
// quotes — the user owns the closer, the path-in-quotes convention
// [D:repl-path-quote].
let private mapLookupHead =
    System.Text.RegularExpressions.Regex @"^(.*?)\s*\|>\s*Map\.(?:get|tryGet|has)$"

let private valueKeySlot (values: Map<string, Weir.Eval.Value>) (text: string) (wordStart: int) : string list option =
    let segStart = max (text.LastIndexOf Weir.Parser.sibSep) (text.LastIndexOf '\n') + 1

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
                    | Some(Weir.Eval.VMap entries) ->
                        // only a string key is offered as a literal [D:map-keys]
                        entries
                        |> Seq.choose (fun kv ->
                            match kv.Key with
                            | Weir.Eval.VStr k -> Some k
                            | _ -> None)
                        |> List.ofSeq
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

            let word =
                if wordStart >= text.Length then
                    ""
                else
                    text.Substring wordStart
            // the candidate must extend the typed word [D:complete-argv]:
            // the literal may reach left of the word (a space inside a
            // typed key) — strip that stem from each offered key
            let stem = literal.Substring(0, literal.Length - word.Length)

            keys
            |> List.filter (fun k -> k.StartsWith literal && k <> literal && not (k.Contains '"'))
            |> List.map (fun k -> k.Substring stem.Length)
            |> List.distinct
            |> List.sort
            |> Some

// Error-recovery completion [D:repair-completion]: the caller repairs
// the broken statement (dangling `.prefix` blanked, closers appended)
// and this types the repaired text — holes for stragglers — then reads
// the head identifier's inferred type at its column. Row types from
// the statement's other uses of the param surface here.
/// the single-text entry: the completion text is the binder scope
/// (the REPL's one logical line)
let suggest (env: TypeEnv) (text: string) (wordStart: int) : string list = suggestScoped env text text wordStart

/// the REPL's entry: the session's alias heads join the head-slot
/// pool [D:command-head-alias], and the session values feed the
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

// the type a binder SITE gives `head` [D:entry-complete]: a lambda /
// for param (the `for` desugar is a lambda — its domain), a let, or a
// match arm's pattern — for a head whose only occurrence was the
// blanked cursor word (`for p in ls do print $"{p.`)
let rec private patBinderTy (env: TypeEnv) (head: string) (pat: Weir.Ast.Pattern) (ty: Ty) : Ty option =
    match pat.PKind, ty with
    | Weir.Ast.PVar n, _ when n = head -> Some ty
    | Weir.Ast.PTuple ps, TTuple ts when ps.Length = ts.Length ->
        List.zip ps ts |> List.tryPick (fun (p, t) -> patBinderTy env head p t)
    | Weir.Ast.PRecord fs, _ ->
        recordFields env ty
        |> Option.bind (fun fields ->
            fs
            |> List.tryPick (fun ((f, _), p) ->
                fields
                |> List.tryFind (fst >> (=) f)
                |> Option.bind (fun (_, ft) -> patBinderTy env head p ft)))
    // a case's payload, under the union's type arguments [D:case-binder-complete]
    | Weir.Ast.PCase(ctor, Some arg, _), TNamed(n, targs) ->
        let subst (ps: string list) (t: Ty) =
            if ps.Length <> targs.Length then
                t
            else
                let m = List.zip ps targs |> Map.ofList

                let rec go t =
                    match t with
                    | TVar v -> Map.tryFind v m |> Option.defaultValue t
                    | TFun(a, b) -> TFun(go a, go b)
                    | TSeq e -> TSeq(go e)
                    | TTuple ts -> TTuple(ts |> List.map go)
                    | TNamed(n, a) -> TNamed(n, a |> List.map go)
                    | t -> t

                go t

        let payload =
            match n, ctor, targs with
            | "Option", "Some", [ t ] -> Some t
            | "Result", "Ok", [ t; _ ] -> Some t
            | "Result", "Error", [ _; e ] -> Some e
            | _ ->
                match Types.typeDefFor env n with
                | Some(Union u) ->
                    u.Cases
                    |> List.tryFind (fst >> (=) ctor)
                    |> Option.bind snd
                    |> Option.map (subst u.Params)
                | _ -> None

        payload |> Option.bind (patBinderTy env head arg)
    | _ -> None

let private binderSiteTy (env: TypeEnv) (head: string) (te: Weir.Check.TypedExpr) : Ty option =
    let domain (t: Ty) =
        match t with
        | TFun(d, _) -> Some d
        | _ -> None

    // the last site in pre-order wins: the innermost, latest binder —
    // the one in scope at the cursor in the common shapes
    let rec find (best: Ty option) (node: Weir.Check.TypedExpr) =
        let here =
            match node.Kind with
            | Weir.Check.TELambda(p, _, _) when p = head -> domain node.Ty
            | Weir.Check.TELambdaPat(pat, _) -> domain node.Ty |> Option.bind (patBinderTy env head pat)
            | Weir.Check.TELet(n, _, value, _) when n = head -> Some value.Ty
            | Weir.Check.TELetPat(pat, value, _) -> patBinderTy env head pat value.Ty
            | Weir.Check.TEMatch(scrut, arms) ->
                arms |> List.tryPick (fun (pat, _, _) -> patBinderTy env head pat scrut.Ty)
            | _ -> None

        let best = if here.IsSome then here else best
        Weir.Check.childExprs node |> List.fold find best

    find None te

/// the repaired statement's type for `head` [D:repair-completion]: any
/// occurrence (a param's uses share one type, and the cursor's own
/// occurrence was blanked away), else its binder site
/// [D:entry-complete]
let tyAtRepaired
    (parse: string -> Result<Weir.Ast.Stmt, string>)
    (env: TypeEnv)
    (repaired: string)
    (head: string)
    : Ty option =
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
                let rec find (best: Ty option) (node: Weir.Check.TypedExpr) =
                    let best =
                        match node.Kind with
                        | Weir.Check.TEVar n when n = head -> Some node.Ty
                        | _ -> best

                    Weir.Check.childExprs node |> List.fold find best

                match find None te with
                | Some t -> Some t
                | None -> binderSiteTy envH head te)

// the statement's own binders in scope at the hole [D:splice-locals]
let bindersAtRepaired
    (parse: string -> Result<Weir.Ast.Stmt, string>)
    (env: TypeEnv)
    (repaired: string)
    (hole: string)
    : (string * Ty) list option =
    let rec patNames (p: Weir.Ast.Pattern) : string list =
        match p.PKind with
        | Weir.Ast.PVar n -> [ n ]
        | Weir.Ast.PTuple ps -> ps |> List.collect patNames
        | Weir.Ast.PRecord fs -> fs |> List.collect (snd >> patNames)
        | Weir.Ast.PCase(_, Some arg, _) -> patNames arg
        | _ -> []

    let patBinds (pat: Weir.Ast.Pattern) (ty: Ty) =
        patNames pat
        |> List.choose (fun n -> patBinderTy env n pat ty |> Option.map (fun t -> n, t))

    let domain (t: Ty) =
        match t with
        | TFun(d, _) -> Some d
        | _ -> None

    match parse repaired with
    | Ok(Weir.Ast.SLet(_, e))
    | Ok(Weir.Ast.SLetPat(_, e))
    | Ok(Weir.Ast.SExpr e)
    | Ok(Weir.Ast.SCmd e) ->
        match Weir.Check.typecheck (withHoles env e) e with
        | Error _ -> None
        | Ok te ->
            let rec walk (scope: (string * Ty) list) (node: Weir.Check.TypedExpr) =
                let orElse (f: unit -> (string * Ty) list option) (r: (string * Ty) list option) =
                    if r.IsSome then r else f ()

                match node.Kind with
                | Weir.Check.TEVar n when n = hole -> Some scope
                | Weir.Check.TELambda(p, _, body) ->
                    walk (scope @ (domain node.Ty |> Option.map (fun d -> p, d) |> Option.toList)) body
                | Weir.Check.TELambdaPat(pat, body) ->
                    walk (scope @ (domain node.Ty |> Option.map (patBinds pat) |> Option.defaultValue [])) body
                | Weir.Check.TELet(n, _, value, body) ->
                    walk scope value |> orElse (fun () -> walk (scope @ [ n, value.Ty ]) body)
                | Weir.Check.TELetPat(pat, value, body) ->
                    walk scope value |> orElse (fun () -> walk (scope @ patBinds pat value.Ty) body)
                | Weir.Check.TEMatch(scrut, arms) ->
                    walk scope scrut
                    |> orElse (fun () ->
                        arms
                        |> List.tryPick (fun (pat, guard, body) ->
                            let s = scope @ patBinds pat scrut.Ty
                            guard |> Option.bind (walk s) |> orElse (fun () -> walk s body)))
                | _ -> Weir.Check.childExprs node |> List.tryPick (walk scope)

            walk [] te
    | _ -> None

/// a resolved type's completable field names: a record's fields, an
/// open row's plus the declared records it fits [D:open-row-compat]
let fieldsOfTy (env: TypeEnv) (ty: Ty) : string list option =
    match ty with
    | TRowVar(_, fields) ->
        // an open row (the `..` tail) is compatible with any
        // declared record it fits inside — offer those records'
        // full field sets too, so editing the one line that
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
    | _ -> recordFields env ty |> Option.map (List.map fst)

let fieldsAtRepaired
    (parse: string -> Result<Weir.Ast.Stmt, string>)
    (env: TypeEnv)
    (repaired: string)
    (head: string)
    : string list option =
    tyAtRepaired parse env repaired head |> Option.bind (fieldsOfTy env)
