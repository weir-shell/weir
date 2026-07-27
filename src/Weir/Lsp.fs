module Weir.Lsp

// weir lsp — v1 [D:lsp-v1]: diagnostics, hover, completion over
// stdio JSON-RPC. Hand-rolled loop (Ionide.LanguageServerProtocol
// carries a reflection serializer the trimmer discipline bans).
// Whole-file re-check per didChange; the server owns NO type state
// between checks — per-document TEXT is the only state (stale-cache
// bugs refused by construction).

open System
open Weir.Types
open Weir.Ast
open Weir.Check

// ---- JSON reading: System.Text.Json's DOM (JsonDocument) ----------
// AOT-SAFE by design [D:lsp-v1]: the DOM reader is reflection-free
// and trim-annotated — the ban is on REFLECTION SERIALIZERS
// (JsonSerializer<T> over F# records), not on this.

open System.Text.Json

let private tryProp (name: string) (e: JsonElement) : JsonElement option =
    if e.ValueKind = JsonValueKind.Object then
        match e.TryGetProperty name with
        | true, v -> Some v
        | _ -> None
    else
        None

let private jStr (k: string) (e: JsonElement) : string option =
    tryProp k e
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.String then
            Some(v.GetString())
        else
            None)

let private jNum (k: string) (e: JsonElement) : int option =
    tryProp k e
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.Number then
            Some(v.GetInt32())
        else
            None)

let private jObj (k: string) (e: JsonElement) : JsonElement option =
    tryProp k e |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Object)

let private jFirst (k: string) (e: JsonElement) : JsonElement option =
    tryProp k e
    |> Option.bind (fun v ->
        if v.ValueKind = JsonValueKind.Array && v.GetArrayLength() > 0 then
            Some(v[0])
        else
            None)

// ---- framing ------------------------------------------------------

let private stdout' = Console.OpenStandardOutput()

let private send (payload: string) =
    let bytes = Text.Encoding.UTF8.GetBytes payload
    let header = Text.Encoding.ASCII.GetBytes $"Content-Length: {bytes.Length}\r\n\r\n"
    stdout'.Write(header, 0, header.Length)
    stdout'.Write(bytes, 0, bytes.Length)
    stdout'.Flush()

// message ids are numbers or strings per JSON-RPC; carried as a value
// so the WRITER quotes them, never interpolation
type private MsgId =
    | IdNum of int
    | IdStr of string

let private writeId (w: Text.Json.Utf8JsonWriter) (id: MsgId) =
    match id with
    | IdNum n -> w.WriteNumber("id", n)
    | IdStr s -> w.WriteString("id", s)

let private respond (id: MsgId) (writeResult: Text.Json.Utf8JsonWriter -> unit) =
    send (
        Script.jsonBuild (fun w ->
            w.WriteStartObject()
            w.WriteString("jsonrpc", "2.0")
            writeId w id
            w.WritePropertyName "result"
            writeResult w
            w.WriteEndObject())
    )

let private notify (method: string) (writeParams: Text.Json.Utf8JsonWriter -> unit) =
    send (
        Script.jsonBuild (fun w ->
            w.WriteStartObject()
            w.WriteString("jsonrpc", "2.0")
            w.WriteString("method", method)
            w.WritePropertyName "params"
            writeParams w
            w.WriteEndObject())
    )

// ---- semantic tokens [D:semantic-tokens]: the mode boundary made
// visible. Mode spans ONLY — expression land emits nothing (TextMate
// keeps lexical coloring; the server overlays the one distinction
// statics cannot make). Token types: 0 = weirCommandHead, 1 = weirArgv,
// 2 = weirSplice.

let tokenLegend = [| "weirCommandHead"; "weirArgv"; "weirSplice" |]

/// (0-based line, 0-based startChar, length, tokenType), sorted by
/// position — the raw form the LSP delta encoding rides on.
let semanticTokensFor (lines: string list) : (int * int * int * int) list =
    let _, stmts, _, _ = Script.analyzeLines "tokens" lines
    let lineArr = List.toArray lines
    let out = ResizeArray<int * int * int * int>()

    // the synthetic-span rule [D:semantic-tokens]: emit ONLY when the
    // logical slice appears VERBATIM at its translated physical home —
    // spans anchored on inserted join/wrap text emit nothing rather
    // than a mislocated token
    let emitSpan (ll: Script.LogicalLine) (startCol: int) (len: int) (ty: int) =
        if len > 0 && startCol >= 1 && startCol - 1 + len <= ll.Text.Length then
            let l1, c1 = Script.translate ll startCol
            let l2, c2 = Script.translate ll (startCol + len - 1)

            if l1 = l2 && c2 = c1 + len - 1 && l1 >= 1 && l1 <= lineArr.Length then
                let phys = lineArr[l1 - 1]

                if
                    c1 >= 1
                    && c1 - 1 + len <= phys.Length
                    && phys.Substring(c1 - 1, len) = ll.Text.Substring(startCol - 1, len)
                then
                    out.Add(l1 - 1, c1 - 1, len, ty)

    let charAt (ll: Script.LogicalLine) (col: int) =
        if col >= 1 && col <= ll.Text.Length then
            Some ll.Text[col - 1]
        else
            None

    // the head token: a span may open on sigil glyphs ($(, !(, $e()
    // — scan past them to the program name; a ^ force prefix rides in
    // the span. Defensive: emit only when the text really is the prog.
    let emitHead (ll: Script.LogicalLine) (spanStart: int) (prog: string) =
        let mutable j = spanStart

        (match charAt ll j with
         | Some '$'
         | Some '!' ->
             j <- j + 1

             while (match charAt ll j with
                    | Some c when System.Char.IsLetterOrDigit c || c = '_' -> true
                    | _ -> false) do
                 j <- j + 1

             if charAt ll j = Some '(' then
                 j <- j + 1
         | _ -> ())

        let forced = charAt ll j = Some '^'
        let ps = j + (if forced then 1 else 0)

        if
            ps - 1 + prog.Length <= ll.Text.Length
            && ll.Text.Substring(ps - 1, prog.Length) = prog
        then
            emitSpan ll j (prog.Length + (if forced then 1 else 0)) 0

    let emitArg (ll: Script.LogicalLine) (a: Check.TypedExpr) =
        let s = a.Span.Start.Col
        let len = a.Span.End.Col - s

        match a.Kind, charAt ll s with
        // $@ splat: the island marker — whole token for $@name, the
        // delimiters for $@(expr) [D:argv-splat]
        | Check.TESplat _, Some '$' ->
            if charAt ll (s + 2) = Some '(' then
                emitSpan ll s 3 2
                emitSpan ll (s + len - 1) 1 2
            else
                emitSpan ll s len 2
        // bareword argv (quoted/raw/interp args keep their lexical
        // string coloring — they already read as data)
        | Check.TEStr _, Some c when c <> '"' && c <> '@' && c <> '$' -> emitSpan ll s len 1
        // $name splice: the island marker, whole token
        | Check.TEVar _, Some '$' -> emitSpan ll s len 2
        // (expr) splice: delimiters only — the interior is expression
        // code and stays lexically colored
        | _, Some '(' when charAt ll (s + len - 1) = Some ')' ->
            emitSpan ll s 1 2
            emitSpan ll (s + len - 1) 1 2
        | _ -> ()

    // reified chains (| succeeds/complete/orFail) desugar the ECmd into
    // an application spine — recognize it so the command still tokens
    // (the reifier NAME stays lexical: grammar, not argv)
    let reifierHeads =
        set
            [ "|succeeded"
              "|completed"
              "|orFailed"
              "|exitCoded"
              "|succeededEnv"
              "|completedEnv"
              "|exitCodedEnv"
              "|orFailedEnv"
              "|succeededIn"
              "|completedIn"
              "|exitCodedIn"
              "|orFailedIn" ]

    let rec spineIsReifier (te: Check.TypedExpr) =
        match te.Kind with
        | Check.TEVar v -> Set.contains v reifierHeads
        | Check.TEApp(f, _) -> spineIsReifier f
        | _ -> false

    // a splatted reified chain's argv desugars to a Seq.append fold
    // [D:splat-reifier-chains] — walk it back to its parts; a non-list
    // part is a splat interior carrying the full `$@...` span, tokened
    // like the TESplat arms (whole token for $@name, delimiters for
    // $@(expr))
    let rec emitArgv (ll: Script.LogicalLine) (te: Check.TypedExpr) =
        match te.Kind with
        | Check.TEList args -> args |> List.iter (emitArg ll)
        | Check.TEApp({ Kind = Check.TEApp({ Kind = Check.TEVar "Seq.append" }, a) }, b) ->
            emitArgv ll a
            emitArgv ll b
        | _ ->
            let s = te.Span.Start.Col
            let len = te.Span.End.Col - s

            if charAt ll s = Some '$' then
                if charAt ll (s + 2) = Some '(' then
                    emitSpan ll s 3 2
                    emitSpan ll (s + len - 1) 1 2
                else
                    emitSpan ll s len 2

    let rec walk (ll: Script.LogicalLine) (te: Check.TypedExpr) =
        (match te.Kind with
         | Check.TECmd(prog, args, _) ->
             emitHead ll te.Span.Start.Col prog
             args |> List.iter (emitArg ll)
         | Check.TEApp({ Kind = Check.TEApp(inner,
                                            { Kind = Check.TEStr prog
                                              Span = pspan }) },
                       argv) when spineIsReifier inner ->
             emitHead ll pspan.Start.Col prog
             emitArgv ll argv
         | _ -> ())

        for c in Check.childExprs te do
            walk ll c

    for (ll, chk) in stmts do
        match chk.Kind with
        | Script.KType _ -> ()
        | Script.KLet(_, _, te)
        | Script.KLetPat(_, _, te)
        | Script.KCmd te
        | Script.KExpr te -> walk ll te

    out |> Seq.sortBy (fun (l, c, _, _) -> l, c) |> Seq.distinct |> List.ofSeq

// ---- analysis helpers ---------------------------------------------

let private analyze (uri: string) (text: string) =
    let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList
    let diags, stmts, env0, lls = Script.analyzeLines uri lines
    diags, stmts, env0, lls

// find the containing logical line among ALL assembled lines
let private logicalAt (lls: Script.LogicalLine list) (line: int) (col: int) =
    lls
    |> List.tryPick (fun ll ->
        ll.Segments
        |> List.tryPick (fun (js, physLine, indent) ->
            if physLine = line && col > indent then
                Some(ll, js + (col - 1 - indent) + 1)
            else
                None))

// physical (1-based line, 1-based col) -> logical (LogicalLine, 1-based joined col)
let private toLogical (stmts: (Script.LogicalLine * Script.CheckedStatement) list) (line: int) (col: int) =
    stmts
    |> List.tryPick (fun (ll, chk) ->
        ll.Segments
        |> List.tryPick (fun (js, physLine, indent) ->
            if physLine = line && col > indent then
                Some(ll, chk, js + (col - 1 - indent) + 1)
            else
                None))

let private teOf (chk: Script.CheckedStatement) =
    match chk.Kind with
    | Script.KType _ -> None
    | Script.KLet(_, _, te)
    | Script.KLetPat(_, _, te)
    | Script.KCmd te
    | Script.KExpr te -> Some te

// smallest node whose span contains the (1-based, logical) column
let private nodeAt (te: TypedExpr) (col: int) : TypedExpr option =
    let rec go (best: TypedExpr option) (node: TypedExpr) =
        if
            node.Span.Start.Col <= col
            && col < max node.Span.End.Col (node.Span.Start.Col + 1)
        then
            let best =
                match best with
                | Some b when (b.Span.End.Col - b.Span.Start.Col) <= (node.Span.End.Col - node.Span.Start.Col) -> Some b
                | _ -> Some node

            Check.childExprs node |> List.fold go best
        else
            Check.childExprs node |> List.fold go best

    go None te

// ---- go-to-definition v1 [D:lsp-requests]: top-level bindings only,
// null otherwise (params/match binders/block-lets carry no binder
// spans in the checker — conservatively silent; builtins have no
// source). The binder's column is found textually in the `let`-headed
// logical line, bounded by the first `=`.

let private isWord (c: char) = Char.IsLetterOrDigit c || c = '_'

// word-bounded search for `name` in text[from..bound), 1-based col —
// the ONE search behind binderCol and the type-member lookup (was
// written twice, one session apart)
let private wordFind (name: string) (text: string) (from: int) (bound: int) : int option =
    let mutable i = max 0 from
    let mutable found = None

    while found.IsNone && i >= 0 && i + name.Length <= bound do
        let j = text.IndexOf(name, i, bound - i)

        if j < 0 then
            i <- -1
        else
            let okL = j = 0 || not (isWord text[j - 1])
            let okR = j + name.Length >= text.Length || not (isWord text[j + name.Length])

            if okL && okR then found <- Some(j + 1) else i <- j + 1

    found

let private wordAt (text: string) (jcol: int) : string option =
    let i = jcol - 1

    if i >= 0 && i < text.Length && isWord text[i] then
        let mutable s = i

        while s > 0 && isWord text[s - 1] do
            s <- s - 1

        let mutable e = i

        while e + 1 < text.Length && isWord text[e + 1] do
            e <- e + 1

        Some(text.Substring(s, e - s + 1))
    else
        None

// the word at the cursor when it is a LET BINDER (the non-space text
// before it ends with the `let` keyword) [PLAN-diagnostics-arc A2]
let private letBinderAt (text: string) (jcol: int) : string option =
    wordAt text jcol
    |> Option.filter (fun _ ->
        let mutable s = jcol - 1

        while s > 0 && isWord text[s - 1] do
            s <- s - 1

        let before = text.Substring(0, s).TrimEnd()

        before.EndsWith "let"
        && (before.Length = 3 || not (isWord before[before.Length - 4])))

// the innermost inner-let binding `name` whose span contains the
// column: hover shows the bound VALUE's type (the binder is not an
// expression node — nodeAt alone sees the enclosing let-in, whose
// type is the body's) [PLAN-diagnostics-arc A2]
let rec private innerLetType (name: string) (jcol: int) (te: Check.TypedExpr) : Ty option =
    let deeper = Check.childExprs te |> List.tryPick (innerLetType name jcol)

    match deeper with
    | Some t -> Some t
    | None ->
        match te.Kind with
        | Check.TELet(n, tvalue, _) when n = name && te.Span.Start.Col <= jcol && jcol < te.Span.End.Col ->
            Some tvalue.Ty
        | _ -> None

let private binderCol (name: string) (text: string) : int option =
    let eq = text.IndexOf '='
    wordFind name text 0 (if eq >= 0 then eq else text.Length)

/// definition site for the identifier at (1-based physical line, col):
/// Some (physLine, physCol, nameLength), or None. Pure — the handler
/// and the unit pins share this. Scope: top-level let/letpat binders;
/// record fields (access + literal), union cases (expression AND
/// pattern position — PSpan carries the arm's location), and type
/// names, each resolving to the KType declaration site.
let definitionFor (lines: string list) (line: int) (col: int) : (int * int * int) option =
    let _, stmts, _, _ = Script.analyzeLines "defn" lines

    // the KType declaring `tyName`: a member's column sits after the
    // first `=`, the type name's before it (joined text spans the
    // whole multi-line declaration; translate maps back to physical)
    let typeSite (tyName: string) (member_: string option) =
        stmts
        |> List.tryPick (fun (ll, c) ->
            match c.Kind with
            | Script.KType d when d.Name = tyName ->
                let eq = ll.Text.IndexOf '='

                let jc =
                    match member_ with
                    | Some m -> wordFind m ll.Text (if eq >= 0 then eq else 0) ll.Text.Length
                    | None -> wordFind tyName ll.Text 0 (if eq >= 0 then eq else ll.Text.Length)

                jc
                |> Option.map (fun jcol ->
                    let pl, pc = Script.translate ll jcol
                    (pl, pc, (member_ |> Option.defaultValue tyName).Length))
            | _ -> None)

    let unionOf (env: TypeEnv) (ctor: string) =
        env.Types
        |> Map.tryPick (fun tn def ->
            match def with
            | Union d when d.Cases |> List.exists (fun (c, _) -> c = ctor) -> Some tn
            | _ -> None)

    let recordHasField (env: TypeEnv) (recName: string) (field: string) =
        match Map.tryFind recName env.Types with
        | Some(Record d) -> d.Fields |> List.exists (fun (f, _) -> f = field)
        | _ -> false

    let letSite (useHead: int) (n: string) =
        stmts
        |> List.filter (fun (ll, _) -> ll.Head < useHead)
        |> List.rev
        |> List.tryPick (fun (ll, c) ->
            let binds =
                match c.Kind with
                | Script.KLet(bn, _, _) -> bn = n
                | Script.KLetPat(_, schemes, _) -> schemes |> List.exists (fun (bn, _) -> bn = n)
                | _ -> false

            if binds then
                binderCol n ll.Text
                |> Option.map (fun bc ->
                    let pl, pc = Script.translate ll bc
                    (pl, pc, n.Length))
            else
                None)

    // a PCase whose CTOR WORD contains the column (a payload binder is
    // a local binder — the binder-span park, not this)
    let rec patCaseAt (jcol: int) (p: Ast.Pattern) : string option =
        let deeper =
            match p.PKind with
            | Ast.PCase(_, Some inner) -> patCaseAt jcol inner
            | Ast.PTuple ps -> ps |> List.tryPick (patCaseAt jcol)
            | _ -> None

        match deeper with
        | Some d -> Some d
        | None ->
            match p.PKind with
            | Ast.PCase(ctor, _) when p.PSpan.Start.Col <= jcol && jcol < p.PSpan.Start.Col + ctor.Length -> Some ctor
            | _ -> None

    let rec matchCaseAt (jcol: int) (te: Check.TypedExpr) : string option =
        let here =
            match te.Kind with
            | Check.TEMatch(_, arms) -> arms |> List.tryPick (fun (p, _, _) -> patCaseAt jcol p)
            | _ -> None

        match here with
        | Some c -> Some c
        | None -> Check.childExprs te |> List.tryPick (matchCaseAt jcol)

    toLogical stmts line col
    |> Option.bind (fun (useLl, chk, jcol) ->
        let env = chk.Env

        let fromPattern =
            teOf chk
            |> Option.bind (matchCaseAt jcol)
            |> Option.bind (fun ctor -> unionOf env ctor |> Option.bind (fun tn -> typeSite tn (Some ctor)))

        match fromPattern with
        | Some r -> Some r
        | None ->
            teOf chk
            |> Option.bind (fun te -> nodeAt te jcol)
            |> Option.bind (fun node ->
                match node.Kind with
                | Check.TEVar n when Types.isUserName n && not (n.Contains '.') ->
                    if Char.IsUpper n[0] then
                        // expression-position union case
                        unionOf env n |> Option.bind (fun tn -> typeSite tn (Some n))
                    else
                        letSite useLl.Head n
                | Check.TEField(target, field) when jcol > target.Span.End.Col ->
                    (match target.Ty with
                     | TNamed(tn, _) -> typeSite tn (Some field)
                     | _ -> None)
                | Check.TERecord(recName, _) ->
                    // the literal's own words: the record name or a field
                    wordAt useLl.Text jcol
                    |> Option.bind (fun w ->
                        if w = recName then
                            typeSite recName None
                        elif recordHasField env recName w then
                            typeSite recName (Some w)
                        else
                            None)
                // `from json T`: the adapter's type name jumps to its
                // declaration [PLAN-diagnostics-arc A3]
                | Check.TEFrom(_, rowDef) ->
                    wordAt useLl.Text jcol
                    |> Option.bind (fun w -> if w = rowDef.Name then typeSite rowDef.Name None else None)
                | _ -> None))

// ---- the server ---------------------------------------------------

let run () : int =
    let stdin' = Console.OpenStandardInput()
    let docs = Collections.Generic.Dictionary<string, string>()

    let readMessage () : string option =
        // headers are ASCII lines ending \r\n; blank line then body
        let mutable contentLength = -1
        let mutable line = Text.StringBuilder()
        let mutable headerDone = false
        let mutable eof = false

        while not headerDone && not eof do
            let b = stdin'.ReadByte()

            if b < 0 then
                eof <- true
            elif b = int '\n' then
                let l = line.ToString().TrimEnd('\r')
                line.Clear() |> ignore

                if l = "" then
                    headerDone <- contentLength >= 0
                elif l.StartsWith "Content-Length:" then
                    match Int32.TryParse(l.Substring(15).Trim()) with
                    | true, n -> contentLength <- n
                    | _ -> ()
            else
                line.Append(char b) |> ignore

        if eof || contentLength < 0 then
            None
        else
            let buf = Array.zeroCreate contentLength
            let mutable read = 0

            while read < contentLength do
                let n = stdin'.Read(buf, read, contentLength - read)

                if n <= 0 then read <- contentLength else read <- read + n

            Some(Text.Encoding.UTF8.GetString buf)

    let publishDiagnostics (uri: string) (text: string) =
        let diags, _, _, _ = analyze uri text

        notify "textDocument/publishDiagnostics" (fun w ->
            w.WriteStartObject()
            w.WriteString("uri", uri)
            w.WritePropertyName "diagnostics"
            w.WriteStartArray()

            for d in diags do
                let el = d.EndLine |> Option.defaultValue d.Line
                let ec = d.EndCol |> Option.defaultValue (d.Col + 1)
                w.WriteStartObject()
                w.WritePropertyName "range"
                w.WriteStartObject()
                w.WritePropertyName "start"
                w.WriteStartObject()
                w.WriteNumber("line", d.Line - 1)
                w.WriteNumber("character", d.Col - 1)
                w.WriteEndObject()
                w.WritePropertyName "end"
                w.WriteStartObject()
                w.WriteNumber("line", el - 1)
                w.WriteNumber("character", ec - 1)
                w.WriteEndObject()
                w.WriteEndObject()
                w.WriteNumber("severity", (if d.Severity = "warning" then 2 else 1))
                w.WriteString("code", d.Code)
                w.WriteString("source", "weir")
                w.WriteString("message", d.Message)
                w.WriteEndObject()

            w.WriteEndArray()
            w.WriteEndObject())

    let mutable running = true
    let mutable exitCode = 0

    while running do
        match readMessage () with
        | None -> running <- false
        | Some raw ->
            use doc =
                try
                    JsonDocument.Parse raw
                with _ ->
                    JsonDocument.Parse "{}"

            let msg = doc.RootElement

            if msg.ValueKind = JsonValueKind.Object then
                let idStr =
                    tryProp "id" msg
                    |> Option.bind (fun v ->
                        match v.ValueKind with
                        | JsonValueKind.Number -> Some(IdNum(v.GetInt32()))
                        | JsonValueKind.String -> Some(IdStr(v.GetString()))
                        | _ -> None)

                let method = jStr "method" msg |> Option.defaultValue ""

                let ps =
                    jObj "params" msg |> Option.defaultValue (JsonDocument.Parse("{}").RootElement)

                let docOf () =
                    jObj "textDocument" ps
                    |> Option.bind (jStr "uri")
                    |> Option.bind (fun uri ->
                        match docs.TryGetValue uri with
                        | true, text -> Some(uri, text)
                        | _ -> None)

                let posOf () =
                    jObj "position" ps
                    |> Option.bind (fun p ->
                        match jNum "line" p, jNum "character" p with
                        | Some l, Some c -> Some(l + 1, c + 1) // to 1-based
                        | _ -> None)

                match method with
                | "initialize" ->
                    idStr
                    |> Option.iter (fun id ->
                        respond id (fun w ->
                            w.WriteRawValue
                                """{"capabilities":{"textDocumentSync":1,"hoverProvider":true,"definitionProvider":true,"documentFormattingProvider":true,"completionProvider":{"triggerCharacters":["."]},"semanticTokensProvider":{"legend":{"tokenTypes":["weirCommandHead","weirArgv","weirSplice"],"tokenModifiers":[]},"full":true}},"serverInfo":{"name":"weir"}}"""))
                | "initialized" -> ()
                | "shutdown" -> idStr |> Option.iter (fun id -> respond id (fun w -> w.WriteNullValue()))
                | "exit" -> running <- false
                | "textDocument/didOpen" ->
                    (match jObj "textDocument" ps with
                     | Some td ->
                         match jStr "uri" td, jStr "text" td with
                         | Some uri, Some text ->
                             docs[uri] <- text
                             publishDiagnostics uri text
                         | _ -> ()
                     | None -> ())
                | "textDocument/didChange" ->
                    (match jObj "textDocument" ps, jFirst "contentChanges" ps with
                     | Some td, Some change ->
                         match jStr "uri" td, jStr "text" change with
                         | Some uri, Some text ->
                             docs[uri] <- text
                             publishDiagnostics uri text
                         | _ -> ()
                     | _ -> ())
                | "textDocument/didClose" ->
                    jObj "textDocument" ps
                    |> Option.bind (jStr "uri")
                    |> Option.iter (fun uri -> docs.Remove uri |> ignore)
                | "textDocument/semanticTokens/full" ->
                    let writeResult (w: Text.Json.Utf8JsonWriter) =
                        match docOf () with
                        | Some(_, text) ->
                            let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList
                            let toks = semanticTokensFor lines
                            w.WriteStartObject()
                            w.WritePropertyName "data"
                            w.WriteStartArray()

                            // the five-int delta scheme: deltaLine,
                            // deltaStartChar (line-relative resets), length,
                            // tokenType, modifiers
                            let mutable pl = 0
                            let mutable pc = 0

                            for (l, c, len, ty) in toks do
                                let dl = l - pl
                                let dc = if dl = 0 then c - pc else c
                                w.WriteNumberValue dl
                                w.WriteNumberValue dc
                                w.WriteNumberValue len
                                w.WriteNumberValue ty
                                w.WriteNumberValue 0
                                pl <- l
                                pc <- c

                            w.WriteEndArray()
                            w.WriteEndObject()
                        | None -> w.WriteNullValue()

                    idStr |> Option.iter (fun id -> respond id writeResult)
                | "textDocument/hover" ->
                    let writeResult (w: Text.Json.Utf8JsonWriter) =
                        match docOf (), posOf () with
                        | Some(uri, text), Some(line, col) ->
                            let _, stmts, _, _ = analyze uri text

                            match toLogical stmts line col with
                            | Some(ll, chk, jcol) ->
                                // an inner-let BINDER hovers as its bound
                                // value's type [PLAN-diagnostics-arc A2]
                                let binderTy =
                                    letBinderAt ll.Text jcol
                                    |> Option.bind (fun name -> teOf chk |> Option.bind (innerLetType name jcol))

                                let fromExpr = teOf chk |> Option.bind (fun te -> nodeAt te jcol)

                                let tyStr =
                                    match binderTy with
                                    | Some t -> Some(formatTy t)
                                    | None ->
                                        match fromExpr with
                                        | Some node -> Some(formatTy node.Ty)
                                        | None ->
                                            match chk.Kind with
                                            | Script.KLet(_, sch, _) -> Some(formatTy sch.Ty)
                                            | _ -> None

                                match tyStr with
                                | Some t ->
                                    w.WriteStartObject()
                                    w.WritePropertyName "contents"
                                    w.WriteStartObject()
                                    w.WriteString("kind", "plaintext")
                                    w.WriteString("value", t)
                                    w.WriteEndObject()
                                    w.WriteEndObject()
                                | None -> w.WriteNullValue()
                            | None -> w.WriteNullValue()
                        | _ -> w.WriteNullValue()

                    idStr |> Option.iter (fun id -> respond id writeResult)
                | "textDocument/definition" ->
                    let writeResult (w: Text.Json.Utf8JsonWriter) =
                        match docOf (), posOf () with
                        | Some(uri, text), Some(line, col) ->
                            let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList

                            match definitionFor lines line col with
                            | Some(pl, pc, len) ->
                                w.WriteStartObject()
                                w.WriteString("uri", uri)
                                w.WritePropertyName "range"
                                w.WriteStartObject()
                                w.WritePropertyName "start"
                                w.WriteStartObject()
                                w.WriteNumber("line", pl - 1)
                                w.WriteNumber("character", pc - 1)
                                w.WriteEndObject()
                                w.WritePropertyName "end"
                                w.WriteStartObject()
                                w.WriteNumber("line", pl - 1)
                                w.WriteNumber("character", pc - 1 + len)
                                w.WriteEndObject()
                                w.WriteEndObject()
                                w.WriteEndObject()
                            | None -> w.WriteNullValue()
                        | _ -> w.WriteNullValue()

                    idStr |> Option.iter (fun id -> respond id writeResult)
                | "textDocument/formatting" ->
                    // client-sent text only (the SECURITY non-claim holds);
                    // editor options are IGNORED — weir fmt is canonical.
                    // formatLines keeps unparseable statements verbatim, so
                    // format-on-save on a broken file still normalizes what
                    // it can; an assemble failure returns no edits.
                    let writeResult (w: Text.Json.Utf8JsonWriter) =
                        match docOf () with
                        | Some(_, text) ->
                            let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList

                            match Fmt.formatLines lines with
                            | Ok formatted when formatted <> lines ->
                                let arr = List.toArray lines
                                let lastIdx = arr.Length - 1
                                w.WriteStartArray()
                                w.WriteStartObject()
                                w.WritePropertyName "range"
                                w.WriteStartObject()
                                w.WritePropertyName "start"
                                w.WriteStartObject()
                                w.WriteNumber("line", 0)
                                w.WriteNumber("character", 0)
                                w.WriteEndObject()
                                w.WritePropertyName "end"
                                w.WriteStartObject()
                                w.WriteNumber("line", lastIdx)
                                w.WriteNumber("character", arr[lastIdx].Length)
                                w.WriteEndObject()
                                w.WriteEndObject()
                                w.WriteString("newText", String.concat "\n" formatted)
                                w.WriteEndObject()
                                w.WriteEndArray()
                            | _ -> w.WriteNullValue() // refusal or already canonical
                        | None -> w.WriteNullValue()

                    idStr |> Option.iter (fun id -> respond id writeResult)
                | "textDocument/completion" ->
                    let writeResult (w: Text.Json.Utf8JsonWriter) =
                        match docOf (), posOf () with
                        | Some(uri, text), Some(line, col) ->
                            let _, stmts, env0, allLls = analyze uri text

                            // env in scope: after the last statement ABOVE the line
                            let env =
                                stmts
                                |> List.filter (fun (ll, _) -> ll.Head < line)
                                |> List.tryLast
                                |> Option.map (fun (_, c) -> c.Env)
                                |> Option.defaultValue env0

                            let lines = text.Replace("\r\n", "\n").Split('\n')
                            let lineText = if line - 1 < lines.Length then lines[line - 1] else ""
                            let upto = lineText.Substring(0, min (col - 1) lineText.Length)

                            let wordStart =
                                let mutable i = upto.Length

                                while i > 0
                                      && (Char.IsLetterOrDigit upto[i - 1] || upto[i - 1] = '_' || upto[i - 1] = '.') do
                                    i <- i - 1

                                i

                            let word = upto.Substring wordStart

                            // error-recovery path: a single-dot word whose
                            // head is unknown — repair the (possibly broken)
                            // containing statement and read the head's
                            // inferred type from the typed tree
                            let repaired =
                                if word.Contains '.' && word.Split('.').Length = 2 then
                                    let head = word.Substring(0, word.IndexOf '.')
                                    let prefix = word.Substring(word.IndexOf '.' + 1)

                                    if
                                        head.Length > 0 && Char.IsLower head[0] && not (Map.containsKey head env.Values)
                                    then
                                        logicalAt allLls line col
                                        |> Option.bind (fun (ll, jcol) ->
                                            let dotIdx = jcol - 1 - prefix.Length - 1

                                            if
                                                dotIdx >= 0
                                                && dotIdx < ll.Text.Length
                                                && ll.Text[dotIdx] = '.'
                                                && dotIdx >= head.Length
                                                && ll.Text.Substring(dotIdx - head.Length, head.Length) = head
                                            then
                                                // blank the WHOLE head.prefix to a neutral
                                                // "" — leaving a bare row-typed head behind
                                                // broke positions with scalar rules (printerr)
                                                let span = head.Length + 1 + prefix.Length

                                                let before = ll.Text.Substring(0, dotIdx - head.Length)
                                                let after = ll.Text.Substring(dotIdx + 1 + prefix.Length)
                                                let filler = "\"\"" + String(' ', max 0 (span - 2))

                                                let parse t =
                                                    Parser.parseLine (Script.assumeResolver env) t

                                                // two repair candidates: close dangling
                                                // delimiters AT THE CURSOR (mid-statement
                                                // edits — the suffix stays outside the
                                                // string), else at the END (last-line edits)
                                                let candB =
                                                    let prefixDone = before + filler
                                                    prefixDone + Script.closers prefixDone + after

                                                let candA =
                                                    let blanked = before + filler + after
                                                    blanked + Script.closers blanked

                                                [ candB; candA ]
                                                |> List.tryPick (fun cand ->
                                                    Complete.fieldsAtRepaired parse env cand head)
                                                |> Option.map (fun fields ->
                                                    fields
                                                    |> List.filter (fun f -> f.StartsWith prefix)
                                                    |> List.sort
                                                    |> List.map (fun f -> head + "." + f))
                                            else
                                                None)
                                    else
                                        None
                                else
                                    None

                            let items =
                                match repaired with
                                | Some fields when not fields.IsEmpty -> fields
                                | _ -> Complete.suggest env upto wordStart

                            // line-head position: PATH commands join (the
                            // command-mode classifier's territory)
                            let items =
                                if upto.Substring(0, wordStart).Trim() = "" then
                                    let word = upto.Substring wordStart

                                    items
                                    @ (Extern.names ()
                                       |> Seq.filter (fun n -> n.StartsWith word)
                                       |> Seq.truncate 50
                                       |> List.ofSeq)
                                else
                                    items

                            w.WriteStartArray()

                            // textEdit with an explicit range: clients replace
                            // [wordStart, cursor) with the suggestion — bare
                            // labels double-insert after dots and get
                            // prefix-filtered inside parens [D:completion-textedit]
                            for label in items |> List.distinct |> List.truncate 200 do
                                w.WriteStartObject()
                                w.WriteString("label", label)
                                w.WritePropertyName "textEdit"
                                w.WriteStartObject()
                                w.WritePropertyName "range"
                                w.WriteStartObject()
                                w.WritePropertyName "start"
                                w.WriteStartObject()
                                w.WriteNumber("line", line - 1)
                                w.WriteNumber("character", wordStart)
                                w.WriteEndObject()
                                w.WritePropertyName "end"
                                w.WriteStartObject()
                                w.WriteNumber("line", line - 1)
                                w.WriteNumber("character", col - 1)
                                w.WriteEndObject()
                                w.WriteEndObject()
                                w.WriteString("newText", label)
                                w.WriteEndObject()
                                w.WriteEndObject()

                            w.WriteEndArray()
                        | _ ->
                            w.WriteStartArray()
                            w.WriteEndArray()

                    idStr |> Option.iter (fun id -> respond id writeResult)
                | _ ->
                    // unknown request: respond null; unknown notification: ignore
                    idStr |> Option.iter (fun id -> respond id (fun w -> w.WriteNullValue()))

    exitCode
