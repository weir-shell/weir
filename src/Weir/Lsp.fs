module Weir.Lsp

// weir lsp — v1 (2026-07-21, LSP chain 3/3): diagnostics, hover,
// completion over stdio JSON-RPC.
//
// AOT path DECISION (reported per the plan's gate): the hand-rolled
// loop, taken by prediction — Ionide.LanguageServerProtocol carries a
// reflection JSON serializer, exactly what the trimmer discipline
// bans; v1 speaks six methods with flat payloads and needs ~200 lines.
//
// NO INCREMENTALITY, on purpose: whole-file re-check per didChange —
// licensed by the pinned 10ms whole-file check median (the 6ms
// discipline's dividend). The server owns NO type state between
// checks: per-document TEXT is the only state (stale-cache bugs
// refused by construction).

open System
open Weir.Types
open Weir.Ast
open Weir.Check

// ---- JSON reading: System.Text.Json's DOM (JsonDocument) ----------
// AOT-SAFE by design: the DOM reader is reflection-free and
// trim-annotated — the plan's ban was on REFLECTION SERIALIZERS
// (JsonSerializer<T> over F# records), not on this. The first
// hand-rolled reader was over-conservative AND wrong on surrogate
// pairs (didChange carries whole documents as JSON strings; emoji in
// a script would have corrupted it) — corrected 2026-07-21.

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
                                """{"capabilities":{"textDocumentSync":1,"hoverProvider":true,"completionProvider":{"triggerCharacters":["."]}},"serverInfo":{"name":"weir"}}"""))
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
                | "textDocument/hover" ->
                    let writeResult (w: Text.Json.Utf8JsonWriter) =
                        match docOf (), posOf () with
                        | Some(uri, text), Some(line, col) ->
                            let _, stmts, _, _ = analyze uri text

                            match toLogical stmts line col with
                            | Some(_, chk, jcol) ->
                                let fromExpr = teOf chk |> Option.bind (fun te -> nodeAt te jcol)

                                let tyStr =
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

                                                let blanked =
                                                    ll.Text.Substring(0, dotIdx - head.Length)
                                                    + "\"\""
                                                    + String(' ', max 0 (span - 2))
                                                    + ll.Text.Substring(dotIdx + 1 + prefix.Length)

                                                let full = blanked + Script.closers blanked

                                                let parse t =
                                                    Parser.parseLine (Script.assumeResolver env) t

                                                Complete.fieldsAtRepaired parse env full head
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
                            // [wordStart, cursor) with the suggestion — without
                            // it, micro appends the raw label after a dot
                            // (Env.Env.fromFile) and its prefix filter hides
                            // results inside parens (user report, 2026-07-21)
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
