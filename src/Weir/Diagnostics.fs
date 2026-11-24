/// Diagnostics as data: completion in context, fixes, the JSON writer, doc and schema findings. One of the files Script.fs was [D:script-by-concern].
module Weir.Diagnostics

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor
open Weir.Statements
open Weir.ModuleLoader

// ---- completion in context [D:entry-complete] ---------------------------
// The REPL's multi-line entry and the LSP's document share one repair:
// the cursor's whole logical statement (every physical line of it up to
// the cursor) is repaired and typed [D:repair-completion], so a binder
// on an earlier line of the statement — a for/fun/let/match binder —
// types the head, and an earlier statement's bindings join the env.
// Checked, never run (the never-executes law).
let private completeParam = "weirCompleteParam"
let private completeHole = "weirCompleteHole"

// the hole as an expression: bare, a lowercase word in a body reads as
// a command head and types seq<string> [D:repair-hole-expr]
let private holeExpr = "(" + completeHole + ")"

/// `head.prefix` fields for the word ending `pre` (the statement text up
/// to the cursor; `post` the rest of it, "" when cut at the cursor).
/// A lowercase head the env does not bind repairs to a blank filler; the
/// `_` shorthand repairs to a lambda whose param is the element — a seq
/// flowing in unwraps to its element, the pipeline-elem rule — except
/// in a bare `|> _.` stage, where a seq never type-checks and offers
/// nothing. None when the word is no such head or no repair types to a
/// record.
let repairedFields (env: TypeEnv) (pre: string) (post: string) : string list option =
    let ws = Complete.wordStartAt pre pre.Length
    let word = pre.Substring ws
    let dot = word.IndexOf '.'

    if dot <= 0 || word.IndexOf('.', dot + 1) >= 0 then
        None
    else
        let head = word.Substring(0, dot)
        let prefix = word.Substring(dot + 1)
        let shorthand = head = "_"

        if
            not shorthand
            && not (Char.IsLower head[0] && not (Map.containsKey head env.Values))
        then
            None
        else
            let before = pre.Substring(0, ws)
            let parse t = Parser.parseLine (assumeResolver env) t

            // the neutral `""` first (a bare row-typed head left behind
            // breaks positions with scalar rules — printerr); a hole
            // (a fresh type var [D:hole-completion]) where a string
            // cannot stand (a predicate's bool)
            let fillers =
                if shorthand then
                    [ $"(fun {completeParam} -> {holeExpr})" ]
                else
                    [ "\"\"" + String(' ', max 0 (word.Length - 2)); holeExpr ]

            let target = if shorthand then completeParam else head
            let unwrap = shorthand && not (Complete.bareStageBefore before)

            // two repair candidates per filler: close dangling delimiters
            // at the cursor (mid-statement edits — the suffix stays
            // outside the string), else at the end (last-line edits)
            // an arm under edit leaves its match incomplete, which fails
            // the check: a catch-all arm completes it [D:case-binder-complete]
            let openMatch =
                Text.RegularExpressions.Regex.IsMatch(before, @"\b(match|function)\b")

            fillers
            |> List.collect (fun filler ->
                let prefixDone = before + filler
                let blanked = before + filler + post

                [
                    prefixDone + closers prefixDone + post
                    blanked + closers blanked
                    if openMatch then
                        let armed = prefixDone + " | _ -> " + holeExpr
                        armed + closers armed + post
                ])
            |> List.tryPick (fun cand ->
                Complete.tyAtRepaired parse env cand target
                |> Option.map (fun ty ->
                    match ty with
                    | TSeq elem when unwrap -> elem
                    | t -> t)
                // a record the repair minted mid-statement (`{| … |}` in an
                // earlier stage) is pending, not yet in env [D:anon-literals]
                |> Option.bind (Complete.fieldsOfTy (Check.withPendingAnonDefs env)))
            |> Option.map (fun fields ->
                fields
                |> List.filter (fun f -> f.StartsWith prefix)
                |> List.distinct
                |> List.sort
                |> List.map (fun f -> head + "." + f))

/// `env` plus the binders in scope at a `$` splice word ending `pre`
/// [D:splice-locals]
let withSpliceLocals (env: TypeEnv) (pre: string) (word: string) : TypeEnv =
    let before = pre.Substring(0, pre.Length - word.Length)

    if not (before.EndsWith "$" || before.EndsWith "$@") then
        env
    else
        let probe = before + completeHole
        let parse t = Parser.parseLine (assumeResolver env) t

        match Complete.bindersAtRepaired parse env (probe + closers probe) completeHole with
        | Some binds -> {
            env with
                // an unconstrained binder splices as a string
                Values =
                    binds
                    |> List.fold
                        (fun m (n, t) ->
                            Map.add
                                n
                                (mono (
                                    match t with
                                    | TVar _ -> TStr
                                    | t -> t
                                ))
                                m)
                        env.Values
          }
        | None -> env

/// a multi-line entry's context at the cursor: `lines` is the entry up
/// to the cursor (its last line cut there). The statements before the
/// cursor's extend `env` (checked, never run; one that fails to check is
/// skipped); returns that env and the cursor statement's text, None when
/// the entry does not assemble (the caller falls back to the line)
let entryContext (env: TypeEnv) (lines: string list) : TypeEnv * string option =
    let numbered =
        lines
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> classifyLine raw <> LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, stripComment raw)

    // a statement still open at the cursor (a lambda's `(` across lines)
    // refuses to assemble: close it at the cursor, then cut the closers
    // back off the statement's text
    let assembled =
        match assemble numbered with
        | Ok lls -> Some(lls, "")
        | Error _ ->
            let suffix = closers (numbered |> List.map snd |> String.concat "\n")

            match List.rev numbered with
            | (n, last) :: earlierRev when suffix <> "" ->
                match assemble (List.rev ((n, last + suffix) :: earlierRev)) with
                | Ok lls -> Some(lls, suffix)
                | Error _ -> None
            | _ -> None

    match assembled with
    | None -> env, None
    | Some(lls, suffix) ->
        match List.rev lls with
        | last :: earlier when
            last.Segments |> List.exists (fun (_, phys, _) -> phys = lines.Length)
            && last.Text.EndsWith suffix
            ->
            let env' =
                earlier
                |> List.rev
                |> List.fold
                    (fun te ll ->
                        match checkStatement false None assumeResolver scriptOnlyImport te ll with
                        // an anonymous record it minted completes at once
                        // [D:anon-literals], as between REPL statements
                        | Ok chk -> Check.withPendingAnonDefs chk.Env
                        | Error _ -> te)
                    env

            env', Some(last.Text.Substring(0, last.Text.Length - suffix.Length))
        | _ -> env, None


// ---------------------------------------------------------------------------
// weir check [--json] [D:check-lsp-chain]. Check-everything, no
// evaluation by construction (this function cannot reach Eval).
// Statement-level error recovery: a failed statement records its diag
// and checking continues with the env unchanged, so a multi-error file
// reports every independent error. Codes are seeded from the message
// families (structured codes at error origin are the parked upgrade).

// a machine-applicable repair [D:structured-fixes]: 1-based physical
// positions in Diagnostic.File, end-exclusive, every edit relative to
// the unedited text; Family keys the editor's source.fixAll grouping
type FixEdit = {
    Line: int
    Col: int
    EndLine: int
    EndCol: int
    NewText: string
}

type Fix = {
    Title: string
    Edits: FixEdit list
    Family: string option
}

type Diagnostic = {
    File: string
    Line: int
    Col: int
    EndLine: int option
    EndCol: int option
    Severity: string // "error" | "warning"
    Code: string
    Message: string
    // related sites (file, line, col, message) — the signature a
    // mismatch is measured against, the import an imported error
    // came through, the rebinding that shadows an unread binder
    // [D:diagnostic-tags]; additive, so every consumer stays
    // position-identical [D:one-pipeline]
    Related: (string * int * int * string) list
    Fix: Fix option
}

let private replaceEdit (line: int) (col: int) (len: int) (text: string) : FixEdit = {
    Line = line
    Col = col
    EndLine = line
    EndCol = col + len
    NewText = text
}

let internal codeOf (parse: bool) (msg: string) : string =
    if parse then
        "parse"
    elif msg.StartsWith "binding names start lowercase" then
        "lowercase-binding"
    elif msg.Contains "discards it" || msg.Contains partialDiscardMark then
        "discard"
    elif msg.StartsWith "a sequenced expression must be unit" then
        "non-unit-statement"
    elif msg.StartsWith "this pattern can fail" then
        "refutable-binder"
    elif msg.StartsWith "match is not exhaustive" || msg.Contains "needs a catch-all" then
        "non-exhaustive"
    elif msg.Contains "unreachable" then
        "unreachable-arm"
    elif msg.StartsWith "invalid regex" then
        "regex"
    elif msg.StartsWith "this regex has" then
        "regex-binder-count"
    elif msg.StartsWith "cannot sort by this key" then
        "unsortable-key"
    elif msg.Contains "equatable" || msg.Contains "cannot be compared" then
        "not-equatable"
    elif msg.Contains "cannot render functions" then
        "unrenderable-function"
    elif msg.StartsWith "unbound variable" then
        "unbound"
    elif msg.Contains "nothing determines" then
        "undetermined-type"
    else
        "check"

// Replace with 'X' [D:structured-fixes]: the typo named by a did-you-mean
// message, located inside the span's own text; a span that does not
// carry the typo verbatim gets no fix
let internal replaceWithFix (spanText: string) (msg: string) (line: int) (col: int) : Fix option =
    didYouMeanTarget msg
    |> Option.bind (fun (typo, suggestion) ->
        let m =
            Text.RegularExpressions.Regex.Match(
                spanText,
                @"(?<![\w-])" + Text.RegularExpressions.Regex.Escape typo + @"(?![\w-])"
            )

        if m.Success then
            Some {
                Title = $"Replace with '{suggestion}'"
                Edits = [ replaceEdit line (col + m.Index) typo.Length suggestion ]
                Family = None
            }
        else
            None)

// the fix inverse [D:structured-fixes]: the one conversion point reads a
// repair back out of (code, message, span) — each message's inverse
// lives beside its producer (didYouMeanTarget, qualifyTarget, the
// tails). `lines` is the raw physical text of the diagnostic's own file
// (empty when the diagnostic points into another file), read for the
// edits that need the source's shape: indents, the words under the span
let fixOf
    (lines: string[])
    (code: string)
    (msg: string)
    (line: int)
    (col: int)
    (endLine: int option)
    (endCol: int option)
    : Fix option =
    let lineText (l: int) =
        if l >= 1 && l <= lines.Length then lines[l - 1] else ""

    let insertAt l c text = {
        Line = l
        Col = c
        EndLine = l
        EndCol = c
        NewText = text
    }

    let one title edits =
        Some {
            Title = title
            Edits = edits
            Family = None
        }

    let indentOf (s: string) = s.Length - s.TrimStart().Length

    // the span's own text when it sits on one line
    let spanText =
        match endLine, endCol with
        | Some el, Some ec when el = line && ec > col && ec - 1 <= (lineText line).Length ->
            Some((lineText line).Substring(col - 1, ec - col))
        | _ -> None

    let afterSpan (el: int) (ec: int) =
        let t = lineText el
        (if ec - 1 <= t.Length then t.Substring(ec - 1) else "").TrimStart()

    let beforeSpan () =
        (lineText line).Substring(0, min (lineText line).Length (col - 1)).TrimEnd()

    // the rest of the span's last line past the span: blank or a comment
    // means the span closes its line
    let closesLine (el: int) (ec: int) =
        let after = afterSpan el ec
        after = "" || after.StartsWith "//"

    let placeholder = "fail \"todo\""

    match code with
    | "unused-binding" ->
        let m =
            Text.RegularExpressions.Regex.Match(msg, @"^'([^']+)' is bound but never used")

        if m.Success then
            one $"Rename to '_{m.Groups[1].Value}'" [ insertAt line col "_" ]
        else
            None
    | "lowercase-binding" ->
        let name =
            match spanText with
            | Some t -> t
            | None ->
                let t = lineText line

                let m =
                    Text.RegularExpressions.Regex.Match(t.Substring(min t.Length (col - 1)), @"^[A-Za-z_][\w']*")

                if m.Success then m.Value else ""

        if name.Length > 0 && Char.IsUpper name[0] then
            // an all-caps name lowercases whole; otherwise the first letter
            let renamed =
                if name |> Seq.forall (fun c -> not (Char.IsLower c)) then
                    name.ToLowerInvariant()
                else
                    string (Char.ToLowerInvariant name[0]) + name.Substring 1

            one $"Rename to '{renamed}'" [ replaceEdit line col name.Length renamed ]
        else
            None
    | "non-exhaustive" ->
        match endLine, endCol with
        | Some el, Some ec ->
            let missing =
                let m =
                    Text.RegularExpressions.Regex.Match(msg, @"^match is not exhaustive; missing: (.+)$")

                if m.Success then
                    // split on top-level commas: a tuple witness keeps its own
                    let parts = ResizeArray<string>()
                    let cur = Text.StringBuilder()
                    let mutable depth = 0

                    for ch in m.Groups[1].Value do
                        match ch with
                        | '('
                        | '[' ->
                            depth <- depth + 1
                            cur.Append ch |> ignore
                        | ')'
                        | ']' ->
                            depth <- depth - 1
                            cur.Append ch |> ignore
                        | ',' when depth = 0 ->
                            parts.Add(cur.ToString().Trim())
                            cur.Clear() |> ignore
                        | _ -> cur.Append ch |> ignore

                    parts.Add(cur.ToString().Trim())
                    parts |> Seq.filter (fun p -> p <> "") |> List.ofSeq
                elif msg.EndsWith "needs a catch-all pattern" then
                    [ "_" ]
                else
                    []

            if missing.IsEmpty then
                None
            else
                let arms = missing |> List.map (fun c -> $"| {c} -> {placeholder}")

                let title =
                    (if missing.Length = 1 then
                         "Add missing arm: "
                     else
                         "Add missing arms: ")
                    + String.concat ", " missing

                // the first arm's indent: the first line in the span whose
                // code starts with '|' (a nested match's arms come later)
                let armIndent =
                    [ line..el ]
                    |> List.tryPick (fun l ->
                        let t = lineText l

                        if t.TrimStart().StartsWith "|" then
                            Some(indentOf t)
                        else
                            None)

                match armIndent with
                | Some i when el > line && closesLine el ec ->
                    let pad = String(' ', i)

                    one title [
                        insertAt
                            el
                            ((lineText el).Length + 1)
                            (arms |> List.map (fun a -> "\n" + pad + a) |> String.concat "")
                    ]
                | _ -> one title [ insertAt el ec (arms |> List.map (fun a -> " " + a) |> String.concat "") ]
        | _ -> None
    | "check" when msg.StartsWith "an if without an else" ->
        match endLine, endCol with
        | Some el, Some ec ->
            let before = beforeSpan ()

            if before.EndsWith "then" then
                // `if c then x` — the else rides the same line
                one "Add else" [ insertAt el ec $" else {placeholder}" ]
            else
                // the then-branch on its own lines: the else aligns with
                // the line that opened it
                [ line - 1 .. -1 .. 1 ]
                |> List.tryFind (fun l -> (stripComment (lineText l)).TrimEnd().EndsWith "then")
                |> Option.bind (fun l ->
                    let pad = String(' ', indentOf (lineText l))
                    one "Add else" [ insertAt el ((lineText el).Length + 1) $"\n{pad}else {placeholder}" ])
        | _ -> None
    | "discard" when msg.EndsWith callItTail ->
        match spanText, endLine, endCol with
        // a name (or dotted member) alone — anything else would need parens
        | Some t, Some el, Some ec when Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z_][\w.']*$") ->
            one $"Call it: {t} ()" [ insertAt el ec " ()" ]
        | _ -> None
    | "check" when msg.EndsWith Check.orFailTail ->
        match endLine, endCol with
        | Some el, Some ec ->
            let before = beforeSpan ()

            let opensRhs =
                before = ""
                || (before.EndsWith "="
                    && not (
                        before.EndsWith "=="
                        || before.EndsWith "<="
                        || before.EndsWith ">="
                        || before.EndsWith "!="
                    ))
                || [ "then"; "else"; "->"; "("; "["; "|>"; ","; ";"; "{" ]
                   |> List.exists before.EndsWith

            let after = afterSpan el ec

            let closes =
                after = "" || [ ")"; "]"; "}"; "//"; ";"; "," ] |> List.exists after.StartsWith

            // a span that is itself parenthesised takes the pipe inside
            let parenthesised =
                match spanText with
                | Some t when t.StartsWith "(" && t.EndsWith ")" ->
                    let mutable depth = 0
                    let mutable closesEarly = false

                    t
                    |> Seq.iteri (fun i ch ->
                        if ch = '(' then
                            depth <- depth + 1
                        elif ch = ')' then
                            depth <- depth - 1

                            if depth = 0 && i < t.Length - 1 then
                                closesEarly <- true)

                    not closesEarly
                | _ -> false

            if parenthesised then
                one "Unwrap with |> orFail" [ insertAt el (ec - 1) " |> orFail" ]
            elif opensRhs && closes then
                one "Unwrap with |> orFail" [ insertAt el ec " |> orFail" ]
            else
                // mid-expression: parenthesised, so the pipe binds to the
                // value alone
                one "Unwrap with |> orFail" [ insertAt line col "("; insertAt el ec " |> orFail)" ]
        | _ -> None
    | _ ->
        match Check.qualifyTarget msg with
        | Some(bare, qualified) ->
            Some {
                Title = $"Qualify: {qualified}"
                Edits = [ replaceEdit line col bare.Length qualified ]
                Family = Some "qualify"
            }
        | None -> spanText |> Option.bind (fun t -> replaceWithFix t msg line col)

// AOT-safe JSON writing: Utf8JsonWriter (reflection-free, the write
// twin of the JsonDocument reader) — escaping is the library's job,
// never string interpolation's; the encoder is Types.RawBmpEncoder
// [D:json-relaxed-escaping]
let jsonBuild (build: System.Text.Json.Utf8JsonWriter -> unit) : string =
    use ms = new IO.MemoryStream()
    use w = new System.Text.Json.Utf8JsonWriter(ms, Types.rawJsonWriterOptions.Value)
    build w
    w.Flush()
    Text.Encoding.UTF8.GetString(ms.ToArray())

let writeDiag (w: System.Text.Json.Utf8JsonWriter) (d: Diagnostic) =
    (Json.jobj {
        // one file identity per document [D:ci-matrix-triage round 26]: an
        // imported module's diag carried the resolved path while the
        // importer's carried argv's spelling verbatim — on Windows the same
        // dir under two spellings (8.3 vs long, / vs \), breaking any
        // consumer that groups by file. GetFullPath is the one spelling.
        "file",
        (try
            IO.Path.GetFullPath d.File
         with _ ->
             d.File)

        "line", d.Line
        "col", d.Col

        match d.EndLine, d.EndCol with
        | Some el, Some ec ->
            "endLine", el
            "endCol", ec
        | _ -> ()

        "severity", d.Severity
        "code", d.Code
        "message", d.Message

        // additive [D:diagnostic-tags]: present only when there is a site
        if not d.Related.IsEmpty then
            "related",
            [
                for (rf, rl, rc, rm) in d.Related ->
                    Json.jobj {
                        "file", rf
                        "line", rl
                        "col", rc
                        "message", rm
                    }
            ]
        // the repair, when one is known [D:structured-fixes] — additive
        match d.Fix with
        | Some fix ->
            "fix",
            Json.jobj {
                "title", fix.Title

                "edits",
                [
                    for e in fix.Edits ->
                        Json.jobj {
                            "line", e.Line
                            "col", e.Col
                            "endLine", e.EndLine
                            "endCol", e.EndCol
                            "newText", e.NewText
                        }
                ]
            }
        | None -> ()
    })
        w

// doc-comment alignment lint [D:doc-comments]: a `///` attaches to the
// declaration below it, so it must sit at that declaration's indent —
// the entry anchor, exactly as an attribute line does. Docs are inert
// (dropped before assembly), so a misaligned one cannot mis-parse: this
// is a lint, not the parse-time attribute machinery.
let internal docMisalignments (path: string) (lines: string list) : Diagnostic list =
    let indent (s: string) = s.Length - s.TrimStart().Length
    let arr = List.toArray lines
    let diags = ResizeArray<Diagnostic>()
    let mutable runStart = -1 // index of the first `///` in the pending run

    let masked = districtContentMask lines

    lines
    |> List.iteri (fun idx raw ->
        if masked[idx] then
            () // district content is bytes — the lint has no claim [D:content-bytes]
        elif isDocLine raw then
            (if runStart < 0 then
                 runStart <- idx)
        elif isAttributeOnlyLine raw then
            () // transparent: the doc rides through to the declaration below
        elif raw.Trim() = "" || (stripComment raw).Trim() = "" then
            runStart <- -1 // a blank / plain-// breaks the run — no attachment, no claim
        else
            (if runStart >= 0 then
                 match declName (stripComment raw) with
                 | Some _ ->
                     let anchor = indent raw

                     for k in runStart .. idx - 1 do
                         let di = indent arr[k]

                         if di <> anchor then
                             diags.Add {
                                 File = path
                                 Line = k + 1
                                 Col = di + 1
                                 EndLine = None
                                 EndCol = None
                                 Severity = "error"
                                 Code = "doc-align"
                                 Related = []
                                 Fix =
                                     Some {
                                         Title = "Align doc comment"
                                         Edits = [
                                             {
                                                 Line = k + 1
                                                 Col = 1
                                                 EndLine = k + 1
                                                 EndCol = di + 1
                                                 NewText = String(' ', anchor)
                                             }
                                         ]
                                         Family = Some "doc-align"
                                     }
                                 Message =
                                     $"this /// doc sits at column {di + 1}, but the declaration it documents is at column {anchor + 1} — a doc aligns with what it describes"
                             }
                 | None -> ())

            runStart <- -1)

    List.ofSeq diags

// full analysis for tooling (the LSP re-frames this): diagnostics and
// the successfully-checked statements with their logical lines — plus
// the initial env, so consumers can pick the in-scope env per position
// external contracts: schema validation [D:yaml-schemas]. Walks every
// typed district carrying a `schema=` declaration; structural checks
// always, value checks where the splice's type permits. Check-time
// only, and it reads vendored files exclusively — check never fetches
// from the network.
/// resolve a schema name from a script's path to Ok (weirDir, vendored
/// file) — the walk the checker uses, with its restore-vs-add guidance
/// on a miss; the schema= hover and definition resolve through this
/// function so the editor and the checker cannot diverge [D:schema-hover]
let resolveSchemaFile (path: string) (name: string) : Result<string * string, string> =
    let fromDir =
        try
            let full = IO.Path.GetFullPath path
            let d = IO.Path.GetDirectoryName full
            if String.IsNullOrEmpty d then "." else d
        with _ ->
            "."

    match Contracts.findWeirDir fromDir with
    | Error e -> Error $"schema '{name}': {e}"
    | Ok weirDir ->
        let file = IO.Path.Combine(weirDir, "schemas", name + ".json")

        if not (IO.File.Exists file) then
            // the checker can tell restore from add: a lock
            // entry means the artifact was declared (a fresh
            // clone restores); no entry means it never was
            let locked =
                match Contracts.readLock weirDir with
                | Ok entries -> entries |> List.exists (fun e -> e.Kind = "schema" && e.Name = name)
                | Error _ -> false

            if locked then
                Error $"schema '{name}': no {file} (searched from {weirDir}) — the lock records it; run `weir restore`"
            else
                Error
                    $"schema '{name}': no {file} (searched from {weirDir}) — add it: weir add schema <url> --as {name}"
        else
            Ok(weirDir, file)

let schemaDiagnostics (path: string) (pairs: (LogicalLine * CheckedStatement) list) : Diagnostic list =
    let cache =
        System.Collections.Generic.Dictionary<string, Result<Contracts.SchemaDoc, string>>()

    let loadSchema (name: string) : Result<Contracts.SchemaDoc, string> =
        match cache.TryGetValue name with
        | true, r -> r
        | _ ->
            let r =
                resolveSchemaFile path name
                |> Result.bind (fun (_, file) ->
                    (try
                        Ok(Contracts.readArtifactText file)
                     with ex ->
                         Error $"schema '{quoteData name}': cannot read {quoteData file} — {quoteData ex.Message}")
                    |> Result.bind (Contracts.parseSchema name))

            cache[name] <- r
            r

    pairs
    |> List.collect (fun (ll, chk) ->
        let roots =
            match chk.Kind with
            | KLet(_, _, te)
            | KLetPat(_, _, te)
            | KCmd te
            | KExpr te -> [ te ]
            | KType _
            | KSig _
            | KModule _
            | KImport _ -> []

        let rec districts (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TEYaml(tpl, Some name, _) -> [ te.Span, name, tpl ]
             | _ -> [])
            @ (Check.childExprs te |> List.collect districts)

        roots
        |> List.collect districts
        |> List.collect (fun (dspan, name, tpl) ->
            let mk (sp: Span) (msg: string) =
                let l1, c1 = translate ll sp.Start.Col
                let l2, c2 = translate ll (max sp.Start.Col (sp.End.Col - 1))

                {
                    File = path
                    Line = l1
                    Col = c1
                    EndLine = Some l2
                    EndCol = Some(c2 + 1)
                    Severity = "error"
                    Code = "schema"
                    Related = []
                    Fix = None
                    Message = msg
                }

            match loadSchema name with
            | Error e -> [ mk dspan e ]
            | Ok doc ->
                Contracts.validateTpl name doc.Defs "" doc.Root tpl
                |> List.map (fun (sp, m) -> mk sp m)))
