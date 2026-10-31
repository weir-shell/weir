module Weir.Lsp

// weir lsp — v1 [D:lsp-v1]: diagnostics, hover, completion over
// stdio JSON-RPC. Hand-rolled loop (Ionide.LanguageServerProtocol
// carries a reflection serializer the trimmer discipline bans).
// Whole-file re-check per didChange. The one cache is the analysis per
// (file, exact text), shared by every request until the next document
// event drops it [D:lsp-analysis-cache].

open System
open Weir.Types
open Weir.Ast
open Weir.Check

// ---- JSON reading: System.Text.Json's DOM (JsonDocument) ----------
// AOT-safe by design [D:lsp-v1]: the DOM reader is reflection-free
// and trim-annotated — the ban is on reflection serializers
// (JsonSerializer<T> over F# records), not on this.

open System.Text.Json

// the checker's fresh type variables ('a1, 'b3) never reach the editor
// [D:no-raw-tyvars]: hover and signature help rename them to 'a, 'b, …
// in first-appearance order, skipping the letters a declared parameter
// already spells — the REPL echo's renaming, minus its reach into the
// declared names a type decl's fields carry
let private isFreshVar (v: string) =
    v.Length >= 2 && Char.IsLetter v[0] && v.Substring 1 |> Seq.forall Char.IsDigit

let private displayTy (ty: Ty) : Ty =
    let declared =
        tyVars ty
        |> Set.filter (fun v -> not (isFreshVar v))
        |> Set.map (fun v -> v.TrimStart '\'')

    let mapping = Collections.Generic.Dictionary<string, string>()

    let renameVar (v: string) =
        match mapping.TryGetValue v with
        | true, r -> r
        | _ ->
            let r =
                Seq.initInfinite id
                |> Seq.map (fun i ->
                    if i < 26 then
                        string (char (int 'a' + i))
                    else
                        $"{char (int 'a' + i % 26)}{i / 26}")
                |> Seq.find (fun c -> not (declared.Contains c) && not (mapping.ContainsValue c))

            mapping[v] <- r
            r

    let rec go (t: Ty) : Ty =
        match t with
        | TVar v when isFreshVar v -> TVar(renameVar v)
        | TFun(a, b) ->
            let a' = go a
            TFun(a', go b)
        | TSeq e -> TSeq(go e)
        | TTuple ts -> TTuple(List.map go ts)
        | TNamed(n, args) -> TNamed(n, List.map go args)
        | TRowVar(r, fields) -> TRowVar(r, fields |> List.map (fun (f, ft) -> f, go ft))
        | t -> t

    go ty

let private formatTy (ty: Ty) : string = Types.formatTy (displayTy ty)

let private formatSignature (name: string) (paramNames: string list) (ty: Ty) : string =
    Types.formatSignature name paramNames (displayTy ty)

let private formatSignatureParts (name: string) (paramNames: string list) (ty: Ty) =
    Types.formatSignatureParts name paramNames (displayTy ty)

/// a `///` comment's lines as paragraphs [D:hover-reflow]: lines the author
/// wrapped join with a space (a client showing one line, or wrapping on its
/// own, must not get the source's line breaks); blank lines, list items and
/// fenced code keep their breaks
let reflowDoc (lines: string list) : string =
    let out = ResizeArray<string>()
    let para = ResizeArray<string>()
    let mutable inFence = false

    let flush () =
        if para.Count > 0 then
            out.Add(String.Join(" ", para))
            para.Clear()

    for raw in lines do
        let l = raw.Trim()

        if l.StartsWith "```" then
            flush ()
            out.Add l
            inFence <- not inFence
        elif inFence then
            out.Add(raw.TrimEnd())
        elif l = "" then
            flush ()
            out.Add ""
        elif l.StartsWith "- " || l.StartsWith "* " then
            flush ()
            para.Add l
        else
            para.Add l

    flush ()
    String.Join("\n", out)

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
// so the writer quotes them, never interpolation
type private MsgId =
    | IdNum of int
    | IdStr of string

let private writeId (w: Text.Json.Utf8JsonWriter) (id: MsgId) =
    match id with
    | IdNum n -> w.WriteNumber("id", n)
    | IdStr s -> w.WriteString("id", s)

// the last request answered — the dispatch backstop reads it so a
// handler that throws after answering is not answered twice
let mutable private lastAnswered: MsgId option = None

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

    lastAnswered <- Some id

// JSON-RPC error codes: every request is answered
// — with a result or with one of these. No id (an unparseable message)
// answers with a null id, as JSON-RPC prescribes.
let private parseError = -32700
let private invalidRequest = -32600
let private methodNotFound = -32601
let private internalError = -32603
// a request the server declines for a reason the client shows (LSP 3.17)
let private requestFailed = -32803

let private respondError (id: MsgId option) (code: int) (message: string) =
    send (
        Script.jsonBuild (fun w ->
            w.WriteStartObject()
            w.WriteString("jsonrpc", "2.0")

            match id with
            | Some i -> writeId w i
            | None -> w.WriteNull "id"

            w.WritePropertyName "error"
            w.WriteStartObject()
            w.WriteNumber("code", code)
            w.WriteString("message", message)
            w.WriteEndObject()
            w.WriteEndObject())
    )

    lastAnswered <- id

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
// visible. Mode spans only — expression land emits nothing (TextMate
// keeps lexical coloring; the server overlays the one distinction
// statics cannot make). Token types: 0 = weirCommandHead, 1 = weirArgv,
// 2 = weirSplice.

let tokenLegend = [| "weirCommandHead"; "weirArgv"; "weirSplice" |]

// the analysis cache [D:lsp-analysis-cache]: hover, completion,
// definition and tokens re-asked for the same text re-analyzed it per
// request (a 20k-line file, seconds each). Keyed by file and init mode,
// hit only on the exact same lines; every document event (open, change,
// close) drops it all, so an edited module reaches its importers.
// the entry carries the file's `///` attachments and `#sig` table as
// lazies: hover, symbols and links read them per request, and each is
// O(lines) (the sig table reads sig files from disk)
type private Analysis = {
    Result:
        Script.Diagnostic list *
        (Script.LogicalLine * Script.CheckedStatement) list *
        TypeEnv *
        Script.LogicalLine list *
        (Script.LogicalLine * Script.CheckedStatement) list
    Docs: Lazy<Script.DocAttach list>
    Sigs: Lazy<Script.SigInfo list>
}

let private analysisCache =
    Collections.Generic.Dictionary<struct (bool * string), string list * Analysis>()

let private analysisEntry (asInit: bool) (path: string) (lines: string list) : Analysis =
    let key = struct (asInit, path)

    let hit =
        lock analysisCache (fun () ->
            match analysisCache.TryGetValue key with
            | true, (ls, r) when ls = lines -> Some r
            | _ -> None)

    match hit with
    | Some r -> r
    | None ->
        let r = {
            Result = Script.analyzeLinesAs asInit path lines
            Docs = lazy (Script.docAttachments lines)
            Sigs = lazy (Script.sigInfosForFile path lines)
        }

        lock analysisCache (fun () -> analysisCache[key] <- (lines, r))
        r

let private analyzeCachedAs (asInit: bool) (path: string) (lines: string list) =
    (analysisEntry asInit path lines).Result

let analyzeCached (path: string) (lines: string list) = analyzeCachedAs false path lines

let private docAttachmentsCached (path: string) (lines: string list) =
    (analysisEntry false path lines).Docs.Force()

let private sigInfosCached (path: string) (lines: string list) =
    (analysisEntry false path lines).Sigs.Force()

/// drop every cached analysis — a document changed [D:lsp-analysis-cache]
let dropAnalyses () =
    lock analysisCache (fun () -> analysisCache.Clear())

/// the synthetic-span rule [D:semantic-tokens], one check for tokens,
/// hints, links and symbols: the logical slice [startCol, startCol+len)
/// sits verbatim at its translated physical home — Some (1-based line,
/// col) — else None, so nothing anchors on inserted join/wrap text.
/// Compared against the RAW buffer lines, never effectiveLines: an init
/// file's rewritten `#session` field (`let _nv = v`, [D:init-marker])
/// is no binder of the user's.
let verbatimAt (raw: string[]) (ll: Script.LogicalLine) (startCol: int) (len: int) : (int * int) option =
    if len > 0 && startCol >= 1 && startCol - 1 + len <= ll.Text.Length then
        let l1, c1 = Script.translate ll startCol
        let l2, c2 = Script.translate ll (startCol + len - 1)

        if l1 = l2 && c2 = c1 + len - 1 && l1 >= 1 && l1 <= raw.Length then
            let phys = raw[l1 - 1]

            if
                c1 >= 1
                && c1 - 1 + len <= phys.Length
                && phys.Substring(c1 - 1, len) = ll.Text.Substring(startCol - 1, len)
            then
                Some(l1, c1)
            else
                None
        else
            None
    else
        None

/// (0-based line, 0-based startChar, length, tokenType), sorted by
/// position — the raw form the LSP delta encoding rides on.
let semanticTokensFor (lines: string list) : (int * int * int * int) list =
    let _, stmts, _, _, _ = analyzeCached "tokens" lines
    let lineArr = List.toArray lines
    let out = ResizeArray<int * int * int * int>()

    // the synthetic-span rule [D:semantic-tokens]: emit only when the
    // logical slice appears verbatim at its translated physical home —
    // spans anchored on inserted join/wrap text emit nothing rather
    // than a mislocated token
    let emitSpan (ll: Script.LogicalLine) (startCol: int) (len: int) (ty: int) =
        match verbatimAt lineArr ll startCol len with
        | Some(l1, c1) -> out.Add(l1 - 1, c1 - 1, len, ty)
        | None -> ()

    let charAt (ll: Script.LogicalLine) (col: int) =
        if col >= 1 && col <= ll.Text.Length then
            Some ll.Text[col - 1]
        else
            None

    // the head token: a span may open on sigil glyphs ($(, $e()
    // — scan past them to the program name; a ^ force prefix rides in
    // the span. Defensive: emit only when the text really is the prog.
    let emitHead (ll: Script.LogicalLine) (spanStart: int) (prog: string) =
        let mutable j = spanStart

        (match charAt ll j with
         | Some '$' ->
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
    // (the reifier name stays lexical: grammar, not argv)
    let rec spineIsReifier (te: Check.TypedExpr) =
        match te.Kind with
        | Check.TEVar v -> (Builtins.reifierSurface v).IsSome
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
        | Check.TEApp({
                          Kind = Check.TEApp({
                                                 Kind = Check.TEVar("Seq.append" | "|seqAppend")
                                             },
                                             a)
                      },
                      b) ->
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
         | Check.TECmd(head, args, _) ->
             (match head with
              | Check.THeadLit prog -> emitHead ll te.Span.Start.Col prog
              // a dynamic head tokens as its splice [D:dynamic-head]
              | Check.THeadDyn(_, he) -> emitArg ll he)

             args |> List.iter (emitArg ll)
         | Check.TEApp({
                           Kind = Check.TEApp(inner,
                                              {
                                                  Kind = Check.TEStr prog
                                                  Span = pspan
                                              })
                       },
                       argv) when spineIsReifier inner ->
             emitHead ll pspan.Start.Col prog
             emitArgv ll argv
         | _ -> ())

        for c in Check.childExprs te do
            walk ll c

    for (ll, chk) in stmts do
        match chk.Kind with
        | Script.KType _
        | Script.KSig _
        | Script.KModule _
        | Script.KImport _ -> ()
        | Script.KLet(_, _, te)
        | Script.KLetPat(_, _, te)
        | Script.KCmd te
        | Script.KExpr te -> walk ll te

    out |> Seq.sortBy (fun (l, c, _, _) -> l, c) |> Seq.distinct |> List.ofSeq

// ---- analysis helpers ---------------------------------------------

// URIs on the wire, filesystem paths for import resolution [D:modules-v1].
// Hand-rolled both directions [D:windows-s3]: System.Uri refuses a bare
// C:\ path (a one-letter "scheme"), which would kill the server on a
// Windows refresh — and the pair must round-trip (path -> uri -> path is
// identity, both platforms) or the mirror bug survives a one-way fix.
// Drive letters: lowercase on the wire (the VS Code convention),
// uppercase on the way back (the platform's canonical spelling).
let uriToPath (uri: string) : string =
    if not (uri.StartsWith "file:") then
        uri
    else
        let rest = uri.Substring "file:".Length

        let path =
            if rest.StartsWith "//" then
                // file://HOST/PATH — clients send an empty host (file:///)
                let hostAndPath = rest.Substring 2

                match hostAndPath.IndexOf '/' with
                | -1 -> "/"
                | i -> hostAndPath.Substring i
            else
                rest

        let decoded = System.Uri.UnescapeDataString path

        if
            OperatingSystem.IsWindows()
            && decoded.Length >= 3
            && decoded[0] = '/'
            && System.Char.IsLetter decoded[1]
            && decoded[2] = ':'
        then
            (string (System.Char.ToUpperInvariant decoded[1]) + decoded.Substring 2).Replace('/', '\\')
        else
            decoded

let pathToUri (path: string) : string =
    if path.StartsWith "file:" then
        path
    else
        let encoded =
            path.Replace('\\', '/').Split '/'
            |> Array.map (fun seg ->
                if seg.Length = 2 && System.Char.IsLetter seg[0] && seg[1] = ':' then
                    string (System.Char.ToLowerInvariant seg[0]) + ":" // the drive, unencoded
                else
                    System.Uri.EscapeDataString seg)
            |> String.concat "/"

        if encoded.StartsWith "/" then
            "file://" + encoded
        else
            "file:///" + encoded

// init.weir carries init-only directives (#session, #alias) the script
// checker does not know [D:repl-init][D:session-prompt] — flagging
// `#session` at col 0 as a parse error, and its fields as bad statements.
// So for the REPL init file only, rewrite it into an equivalent script for
// diagnostics AND completion: blank the `#session {`/`}`/`#alias` lines,
// and turn each `<4-indent>key = value` field into `let <binder> = value`
// COLUMN-PRESERVING — 4-space indent out, `let ` in, a same-length binder
// (so the value stays at its exact column for hover/completion, and a
// distinct `_`-led binder neither self-references the value nor warns
// unused). The value then checks and completes as an ordinary let-RHS.
// the canonical REPL init file? — only it gets init-directive handling; a
// stray init.weir elsewhere is a normal script [D:repl-init]. Match the
// full path (<configHome>/weir/init.weir, XDG/%APPDATA% honoured).
let private isReplInitPath (path: string) : bool =
    try
        let initPath = System.IO.Path.Combine(Builtins.configDir (), "weir", "init.weir")

        let a = System.IO.Path.GetFullPath path
        let b = System.IO.Path.GetFullPath initPath
        // Windows paths are case-insensitive; POSIX is exact
        if System.OperatingSystem.IsWindows() then
            System.String.Equals(a, b, System.StringComparison.OrdinalIgnoreCase)
        else
            a = b
    with _ ->
        false

/// the lines the checker/completer should see for a document — init
/// directives rewritten for the REPL init file, the raw text otherwise
let private effectiveLines (uri: string) (text: string) : string list =
    let raw = Script.splitSourceText text

    // directives rewrite for every init file [D:init-marker]: a marked
    // one anywhere, and the canonical path even unmarked (so its only
    // actionable diagnostic, add #init, is not buried under script-law
    // noise). All downstream consumers — diagnostics, hover, completion
    // — read this stripped form, column-preserving.
    if Script.isInitMarked raw || isReplInitPath (uriToPath uri) then
        Script.stripInitDirectives raw
    else
        raw

// hover on a #session field key [D:init-marker]: the directive rewrite
// turns `env = v` into a synthesized `let _nv = v` (same-length,
// distinct binder — [D:session-prompt]), so the ordinary binder hover
// would show `_nv : <inferred>`. The key cannot be recovered from the
// rewritten binder (the first char is replaced), so read it off the
// RAW line and report the field's schema type instead — `env : seq<...>`,
// `prompt : string | unit -> string | PromptStatus -> string`. 1-based
// line/col (LSP posOf), rawLines 0-based.
let private sessionFieldHover (path: string) (rawLines: string list) (line: int) (col: int) : string option =
    let raw = List.toArray rawLines

    if
        not (isReplInitPath path || Script.isInitMarked rawLines)
        || line < 1
        || line > raw.Length
    then
        None
    else
        // in a #session block at this line? scan the lines above
        let mutable inBlock = false

        for i in 0 .. line - 2 do
            let t = raw[i].Trim()

            if t.StartsWith "#session" then
                inBlock <- t.EndsWith "{"
            elif inBlock && t = "}" then
                inBlock <- false

        if not inBlock then
            None
        else
            let m =
                System.Text.RegularExpressions.Regex.Match(
                    raw[line - 1],
                    @"^(\s{0,4})([A-Za-z_][A-Za-z0-9_]*)(\s*=\s*)(.*)$"
                )

            if not m.Success then
                None
            else
                let key = m.Groups[2].Value
                let keyStart = m.Groups[1].Value.Length // 0-based
                let colZ = col - 1

                if
                    colZ >= keyStart
                    && colZ < keyStart + key.Length
                    && Set.contains key Script.knownSessionKeys
                then
                    let tys = Script.expectedFieldTypes key |> List.map formatTy |> String.concat " | "
                    Some $"{key} : {tys}"
                else
                    None

let private analyze (uri: string) (text: string) =
    // analyze against the real path so imports resolve relative to the file;
    // diagnostics come back File-identified (the entry + its modules)
    let path = uriToPath uri
    let raw = Script.splitSourceText text
    let canonical = isReplInitPath path

    // an init file (marked anywhere, or the canonical path) checks as the
    // init it is [D:init-marker] — the shared path, so the editor reports
    // exactly what `weir check` and the loader do (checkInit [D:repl-init]),
    // the missing marker included
    let diags, stmts, env0, lls, partials =
        if canonical || Script.isInitMarked raw then
            analyzeCachedAs true path raw
        else
            analyzeCached path raw

    diags, stmts, env0, lls, partials

// is a physical line (0-based) inside a yaml block? Its nearest less
// indented line above is a yaml district marker, or is itself inside one
let private inYamlBlock (lines: string[]) (idx: int) : bool =
    let indentOf (s: string) = s.Length - s.TrimStart().Length

    let rec under (i: int) =
        let ind = indentOf lines[i]
        let mutable j = i - 1

        while j >= 0 && (lines[j].Trim() = "" || indentOf lines[j] >= ind) do
            j <- j - 1

        j >= 0
        && (Parser.isYamlMarkerPiece ((Script.stripComment lines[j]).Trim()) || under j)

    lines[idx].Trim() <> "" && under idx

// find the containing logical line among all assembled lines
let private logicalAt (lls: Script.LogicalLine list) (line: int) (col: int) =
    lls
    |> List.tryPick (fun ll ->
        ll.Segments
        |> List.tryPick (fun (js, physLine, indent) ->
            if physLine = line && col > indent then
                Some(ll, js + (col - 1 - indent) + 1)
            else
                None))

// the statements the editor answers from [D:block-recovery]: the
// checked ones and the partials (errored, with a recovered tree), in
// file order — diagnostics never read this union
let private withPartials
    (stmts: (Script.LogicalLine * Script.CheckedStatement) list)
    (partials: (Script.LogicalLine * Script.CheckedStatement) list)
    =
    match partials with
    | [] -> stmts
    | _ -> stmts @ partials |> List.sortBy (fun (ll, _) -> ll.Head)

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

/// the statement open at the cursor, cut there, with the env it checks
/// in: (env, text before the cursor, text after). The containing logical
/// line when assembly holds; else the statement still open at the cursor
/// broke assembly (a lambda's `(` across lines), so no logical line holds
/// it — its physical lines from the nearest column-0 head above (a `|`
/// line continues) assemble up to the cursor the REPL's way. Shared by
/// completion and signature help [D:signature-help]
let private statementUpTo
    (env: TypeEnv)
    (lls: Script.LogicalLine list)
    (lines: string[])
    (line: int)
    (col: int)
    : (TypeEnv * string * string) option =
    match logicalAt lls line col with
    | Some(ll, jcol) ->
        let cut = min (max 0 (jcol - 1)) ll.Text.Length
        Some(env, ll.Text.Substring(0, cut), ll.Text.Substring cut)
    | None when line - 1 < lines.Length ->
        let lineText = lines[line - 1]
        let upto = lineText.Substring(0, min (col - 1) lineText.Length)

        let isHead (l: string) =
            l.Trim() <> "" && not (Char.IsWhiteSpace l[0]) && not (l.StartsWith "|")

        let mutable start = line - 1

        while start > 0 && not (isHead lines[start]) do
            start <- start - 1

        let stmtLines = [ for i in start .. line - 2 -> lines[i] ] @ [ upto ]

        match Script.entryContext env stmtLines with
        | ctxEnv, Some stmt -> Some(ctxEnv, stmt, "")
        | _, None -> None
    | None -> None

let private teOf (chk: Script.CheckedStatement) =
    match chk.Kind with
    | Script.KType _
    | Script.KSig _
    | Script.KModule _
    | Script.KImport _ -> None
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

// is the (logical) column inside a recovered hole [D:block-recovery]
let private inHole (chk: Script.CheckedStatement) (col: int) : bool =
    let rec go (te: TypedExpr) =
        match te.Kind with
        | Check.TEHole sp -> sp.Start.Col <= col && col < sp.End.Col
        | _ -> Check.childExprs te |> List.exists go

    teOf chk |> Option.map go |> Option.defaultValue false

// ---- go-to-definition v1 [D:lsp-requests]: top-level bindings only,
// null otherwise (params/match binders/block-lets carry no binder
// spans in the checker — conservatively silent; builtins have no
// source). The binder's column is found textually in the `let`-headed
// logical line, bounded by the first `=`.

let private isWord (c: char) = Char.IsLetterOrDigit c || c = '_'

// word-bounded search for `name` in text[from..bound), 1-based col —
// the one search behind binderCol and the type-member lookup
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

// the word at the cursor when it is a let binder (the non-space text
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
// column: hover shows the bound value's type (the binder is not an
// expression node — nodeAt alone sees the enclosing let-in, whose
// type is the body's) [PLAN-diagnostics-arc A2]
let rec private innerLetType (name: string) (jcol: int) (te: Check.TypedExpr) : Ty option =
    let deeper = Check.childExprs te |> List.tryPick (innerLetType name jcol)

    match deeper with
    | Some t -> Some t
    | None ->
        match te.Kind with
        | Check.TELet(n, _, tvalue, _) when n = name && te.Span.Start.Col <= jcol && jcol < te.Span.End.Col ->
            Some tvalue.Ty
        | _ -> None

// a lambda param binder at the column shows its own type (the domain of
// the enclosing lambda), not the arrow type nodeAt would surface — the
// param binder is not an expression node, so nodeAt falls back to the
// lambda itself and shows `dom -> cod` for the parameter [D:lsp-v1]
let rec private paramTypeAt (jcol: int) (te: Check.TypedExpr) : Ty option =
    match Check.childExprs te |> List.tryPick (paramTypeAt jcol) with
    | Some t -> Some t
    | None ->
        match te.Kind, te.Ty with
        | Check.TELambda(_, pspan, _), TFun(dom, _) when pspan.Start.Col <= jcol && jcol < pspan.End.Col -> Some dom
        | _ -> None

let private binderCol (name: string) (text: string) : int option =
    let eq = text.IndexOf '='
    wordFind name text 0 (if eq >= 0 then eq else text.Length)

// lexical resolution for local binders [PLAN-diagnostics-arc C]: the
// use (the TEVar at the column) and the binder the checker's scoping
// gave it — one walk, Check.occurrences [D:lsp-rename]. Returns
// Some(Some span) = locally bound there; Some None = use found,
// top-level territory; None = the use is not in this subtree.
let private localDef (name: string) (jcol: int) (te: Check.TypedExpr) : Span option option =
    Check.occurrences te
    |> List.filter (fun o ->
        o.Name = name
        && not o.IsBinderSite
        && o.Span.Start.Col <= jcol
        && jcol < o.Span.End.Col)
    |> List.sortBy (fun o -> o.Span.End.Col - o.Span.Start.Col)
    |> List.tryHead
    |> Option.map (fun o ->
        match o.Binder with
        | Check.Local sp -> Some sp
        | Check.TopLevel _ -> None)

/// hover text at (1-based physical line, col), or None. Pure — the
/// handler and the unit pins share this. Type first, then the `///`
/// doc, when the cursor is on a documented name [D:doc-comments]. Type
/// priority: an inner-let binder shows its bound value's type; a lambda
/// param binder shows its own type (the domain, not the arrow nodeAt
/// would find); else the typed node at the column; else the statement's
/// top-level scheme [D:lsp-v1].
/// hover for a `type` declaration position (KType): the type name
/// renders its definition, a field name its type, a union case its
/// signature — so "type first" holds at the field/case/type positions
/// too [D:doc-comments].
let private declHover (decl: Ast.Decl) (word: string) : string option =
    // an ordered type shows its key in order, a numbered member its
    // position [D:derived-ord] — the hover carries the fact the members'
    // types alone cannot
    let ordPos (specs: Ast.AttrSpec list) =
        specs
        |> List.tryPick (fun a ->
            match a.AName, a.AArg with
            | "Ord", Some(Types.AInt n) -> Some n
            | _ -> None)

    let orderedBy (members: (string * Ast.AttrSpec list) list) =
        match
            members
            |> List.choose (fun (m, specs) -> ordPos specs |> Option.map (fun n -> m, n))
        with
        | [] -> ""
        | key ->
            "  (ordered by "
            + (key |> List.sortBy snd |> List.map fst |> String.concat ", ")
            + ")"

    let withPos (specs: Ast.AttrSpec list) (sig': string) =
        match ordPos specs with
        | Some n -> $"[<Ord {n}>] {sig'}"
        | None -> sig'

    match decl.Body with
    | Ast.DRecord fields ->
        if word = decl.Name then
            let body =
                fields
                |> List.map (fun (n, ty, _) -> $"{n}: {formatTy ty}")
                |> String.concat "; "

            Some($"{{ {body} }}" + orderedBy (fields |> List.map (fun (n, _, specs) -> n, specs)))
        else
            fields
            |> List.tryPick (fun (n, ty, specs) -> if n = word then Some(withPos specs (formatTy ty)) else None)
    | Ast.DUnion cases ->
        let caseSig (n, tyO) =
            match tyO with
            | Some t -> $"{n} of {formatTy t}"
            | None -> n

        if word = decl.Name then
            let validated = if decl.Validate.IsSome then "  (validated)" else ""
            // a tagged union shows its discriminator [D:wire-unions] —
            // the hover carries the wire fact the cases alone cannot
            let tagPrefix =
                decl.Attrs
                |> List.tryPick (fun a ->
                    match a.AName, a.AArg with
                    | "Tag", Some(Types.AStr t) -> Some $"[<Tag \"{t}\">] "
                    | _ -> None)
                |> Option.defaultValue ""

            Some(
                tagPrefix
                + (cases |> List.map (fun (n, tyO, _) -> caseSig (n, tyO)) |> String.concat " | ")
                + orderedBy (cases |> List.map (fun (n, _, specs) -> n, specs))
                + validated
            )
        else
            cases
            |> List.tryPick (fun (n, tyO, specs) ->
                if n = word then
                    Some(withPos specs (caseSig (n, tyO)))
                else
                    None)

/// a keyword, an operator, punctuation, whitespace, or the wildcard `_`
/// hovers as nothing — never the enclosing node's type [D:hover-silence].
/// A wrong `unit`/`int` on the most-hovered tokens teaches the user that
/// hover lies; null is the honest answer. Runs before the enclosing-node
/// fallback, scoped by what the cursor is on — identifiers, numbers, and
/// bool literals still answer.
let private onSilentToken (text: string) (jcol: int) : bool =
    if jcol < 1 || jcol > text.Length then
        true
    else
        let c = text[jcol - 1]

        if System.Char.IsLetterOrDigit c || c = '_' then
            match wordAt text jcol with
            | Some "_" -> true // the wildcard pattern
            | Some "true"
            | Some "false" -> false // bool LITERALS (in the keyword set) answer
            | Some w -> Set.contains w Parser.keywords
            | None -> false
        else
            true // operator / punctuation / whitespace

// hoverType is defined after definitionFor — it composes with it for the
// Group 1a lookup (a usage / field / case reference resolves to its
// declaration site, and the `///` doc is read there) [D:hover-completeness].

/// the type of a local binder (a pattern payload binder, an inner let)
/// read from a use of it in the typed tree — the binder position itself
/// is no expression node, but its uses carry the type [Group 2].
let rec private varUseType (name: string) (te: Check.TypedExpr) : Ty option =
    match te.Kind with
    | Check.TEVar n when n = name -> Some te.Ty
    | _ -> Check.childExprs te |> List.tryPick (varUseType name)

/// the parameter names of a curried value — the lambda chain a `let f a b`
/// desugars to; the annotated signature reads them off the binder spans
/// [D:annotated-signature]
let rec private lambdaParamNames (te: Check.TypedExpr) : string list =
    match te.Kind with
    | Check.TELambda(p, _, body) -> p :: lambdaParamNames body
    | _ -> []

/// render a let binding's hover [D:annotated-signature]: the param
/// signature (`f (x) : ret`) when there is a named param, else the flat
/// value type (`name : ty`) — matching F#, which names only named params,
/// so an all-`()` (unit) or param-less binding hovers as its value type
let private sigOrFlat (name: string) (ps: string list) (ty: Ty) : string =
    if ps |> List.filter (fun p -> p <> "()") |> List.isEmpty then
        $"{name} : {formatTy ty}"
    else
        formatSignature name ps ty

/// the annotated signature of the inner-let binding `name` at the column —
/// the sibling of innerLetType that renders names, not just the arrow type
let rec private innerLetSig (name: string) (jcol: int) (te: Check.TypedExpr) : string option =
    match Check.childExprs te |> List.tryPick (innerLetSig name jcol) with
    | Some s -> Some s
    | None ->
        match te.Kind with
        | Check.TELet(n, _, tvalue, _) when n = name && te.Span.Start.Col <= jcol && jcol < te.Span.End.Col ->
            Some(sigOrFlat n (lambdaParamNames tvalue) tvalue.Ty)
        | _ -> None

// ---- cross-file navigation [D:lsp-cross-file] ---------------------
// the server retains nothing between requests, so a cross-file target
// re-analyzes the target file's lines, read through the import channel
// (open buffers first, then disk) — the same stateless discipline as
// every other request

let private targetStmts (absPath: string) =
    Script.targetSourceLines absPath
    |> Option.map (fun lines ->
        let _, stmts, _, _, _ = analyzeCached absPath lines
        lines, stmts)

// the KType declaring `tyName` among `stmts` — file-agnostic so the
// entry file and an imported module search the same way: a member's
// column sits after the first `=`, the type name's before it (joined
// text spans the whole multi-line declaration; translate maps back to
// physical)
let private typeSiteIn
    (stmts: (Script.LogicalLine * Script.CheckedStatement) list)
    (tyName: string)
    (member_: string option)
    : (int * int * int) option =
    stmts
    |> List.tryPick (fun (ll, c) ->
        match c.Kind with
        | Script.KType(d, _) when d.Name = tyName ->
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

// the last top-level binder `n` among stmts; the entry file bounds the
// search by the use site, a module file has no use site to bound by
let private letSiteIn
    (stmts: (Script.LogicalLine * Script.CheckedStatement) list)
    (bound: int option)
    (n: string)
    : (int * int * int) option =
    let inScope =
        stmts
        |> List.filter (fun (ll, _) ->
            match bound with
            | Some b -> ll.Head < b
            | None -> true)

    let site (ll: Script.LogicalLine) =
        binderCol n ll.Text
        |> Option.map (fun bc ->
            let pl, pc = Script.translate ll bc
            (pl, pc, n.Length))

    // the signature is the declaration a member reference lands on
    // [D:module-signatures] — the API home (doc included); the impl is
    // one hop below it in the source
    let sigSite =
        inScope
        |> List.tryPick (fun (ll, c) ->
            match c.Kind with
            | Script.KSig(bn, _) when bn = n -> site ll
            | _ -> None)

    sigSite
    |> Option.orElseWith (fun () ->
        inScope
        |> List.rev
        |> List.tryPick (fun (ll, c) ->
            let binds =
                match c.Kind with
                | Script.KLet(bn, _, _) -> bn = n
                | Script.KLetPat(_, schemes, _) -> schemes |> List.exists (fun (bn, _) -> bn = n)
                | _ -> false

            if binds then site ll else None))

/// the command surface containing the column — bare TECmd or the
/// reified spine (| succeeds desugars the ECmd away; recover prog and
/// the literal argv words like the unknown-flag check does
/// [D:command-signatures]): (prog, head start col, words with spans)
let rec private cmdSurfaceAt (jcol: int) (te: Check.TypedExpr) : (string * int * (string * Span) list) option =
    match Check.childExprs te |> List.tryPick (cmdSurfaceAt jcol) with
    | Some r -> Some r
    | None when te.Span.Start.Col <= jcol && jcol < te.Span.End.Col ->
        (match te.Kind with
         | Check.TECmd(Check.THeadLit prog, args, _) ->
             Some(
                 prog,
                 te.Span.Start.Col,
                 args
                 |> List.choose (fun a ->
                     match a.Kind with
                     | Check.TEStr w -> Some(w, a.Span)
                     | _ -> None)
             )
         | _ ->
             let rec spine (e: Check.TypedExpr) acc =
                 match e.Kind with
                 | Check.TEApp(f, a) -> spine f (a :: acc)
                 // the command-reifier spine only [D:desugar-namespace] — a
                 // library desugar carries no program/argv to recover
                 | Check.TEVar v when Weir.Effects.isCommandReifier v -> Some acc
                 | _ -> None

             match spine te [] with
             | Some args ->
                 let progE =
                     args
                     |> List.choose (fun a ->
                         match a.Kind with
                         | Check.TEStr p -> Some(p, a.Span)
                         | _ -> None)
                     |> List.tryLast

                 let rec lists (e: Check.TypedExpr) =
                     match e.Kind with
                     | Check.TEList items -> items
                     | _ -> Check.childExprs e |> List.collect lists

                 let words =
                     args
                     |> List.collect lists
                     |> List.choose (fun a ->
                         match a.Kind with
                         | Check.TEStr w -> Some(w, a.Span)
                         | _ -> None)

                 progE |> Option.map (fun (p, psp) -> p, psp.Start.Col, words)
             | None -> None)
    | None -> None

/// the flag's field in its signature, resolved the way the unknown-flag
/// check resolves surfaces (record = the "" sub; union = the first
/// non-dash word): (record name, field, field type, sig lines, sig stmts)
// the record a line's flags live in: the longest run of leading sub
// words, kebab-joined — the checker's own rule [D:scoped-sigs]
let private sigRecordFor (si: Script.SigInfo) (words: string list) : (string * int) option =
    match Map.tryFind "" si.SubRecords with
    | Some rn -> Some(rn, 0)
    | None ->
        let run =
            words
            |> List.skipWhile (fun w -> w.StartsWith "-")
            |> List.takeWhile (fun w -> not (w.StartsWith "-"))
            |> List.truncate 4

        [ List.length run .. -1 .. 1 ]
        |> List.tryPick (fun n ->
            Map.tryFind (String.concat "-" (run |> List.truncate n)) si.SubRecords
            |> Option.map (fun rn -> rn, n))

let private sigFlagField (si: Script.SigInfo) (words: string list) (w: string) =
    let rn =
        match sigRecordFor si words with
        | Some(rn, _) -> Some rn
        | None ->
            // a sub-less line on a scoped sig: the flag lives in every
            // case that carries it — the first record holding the field
            // is the definition worth jumping to
            si.SubRecords |> Map.toList |> List.map snd |> List.distinct |> List.tryHead

    rn
    |> Option.bind (fun rn ->
        targetStmts si.SigPath
        |> Option.bind (fun (sigLines, sigStmts) ->
            sigStmts
            |> List.tryPick (fun (_, c) ->
                match c.Kind with
                | Script.KType(d, _) when d.Name = rn -> Map.tryFind rn c.Env.Types
                | _ -> None)
            |> Option.bind (function
                | Record rd ->
                    let field =
                        if w.StartsWith "--" then
                            let name = w.Substring(2).Split('=')[0]

                            rd.Fields
                            |> List.map fst
                            |> List.tryFind (fun f -> Weir.Argv.kebabFlag f = name)
                        elif w.Length = 2 && w.StartsWith "-" then
                            Weir.Argv.explicitShorts rd
                            |> List.tryPick (fun (f, sh) -> if sh = w.Substring 1 then Some f else None)
                        else
                            None

                    field
                    |> Option.map (fun f ->
                        let fty = rd.Fields |> List.pick (fun (fn, t) -> if fn = f then Some t else None)
                        rn, f, fty, sigLines, sigStmts)
                | _ -> None)))

/// a signed flag's `///` doc, read at its field's declaration in the sig
/// file — hover and signature help share the read
let private sigFlagDoc
    (sigPath: string)
    (rn: string)
    (f: string)
    (sigLines: string list)
    (sigStmts: (Script.LogicalLine * Script.CheckedStatement) list)
    : string option =
    typeSiteIn sigStmts rn (Some f)
    |> Option.bind (fun (dl, dc, _) ->
        docAttachmentsCached sigPath sigLines
        |> List.tryPick (fun d ->
            if d.Line = dl && d.Col = dc then
                Some(reflowDoc d.Doc)
            else
                None))

/// definition site for the identifier at (1-based physical line, col):
/// Some (target file or None for this one, physLine, physCol,
/// nameLength), or None. Pure — the handler and the unit pins share
/// this. Scope: top-level let/letpat binders; record fields (access +
/// literal), union cases (expression and pattern position), and type
/// names, resolving to the KType declaration site in the file that
/// declares it (an imported type re-analyzes its module); qualified
/// module members; the import path itself; a signed command's head
/// (the sig file) and flags (the field declaration) [D:lsp-cross-file].
// cursor within the `schema=<name>` token of a district marker
// [D:schema-hover]. The joined text's first occurrence is the head's
// (body data joins after it), so the span needs no sentinel walk.
let private onSchemaToken (name: string) (text: string) (jcol: int) : bool =
    let idx = text.IndexOf("schema=" + name)

    idx >= 0 && jcol - 1 >= idx && jcol - 1 < idx + "schema=".Length + name.Length

// the declared schema name when the cursor sits on its token: off the
// TEYaml node itself — the name is a vendored file, not an env.Types
// entry, so the type-argument arm cannot render it [D:schema-hover]
let private schemaTokenAt (chk: Script.CheckedStatement) (text: string) (jcol: int) : string option =
    teOf chk
    |> Option.bind (fun te -> nodeAt te jcol)
    |> Option.bind (fun nd ->
        match nd.Kind with
        | Check.TEYaml(_, Some name, _) when onSchemaToken name text jcol -> Some name
        | _ -> None)

// the union declaring `ctor` among env.Types
let private unionOf (env: TypeEnv) (ctor: string) =
    env.Types
    |> Map.tryPick (fun tn def ->
        match def with
        | Union d when d.Cases |> List.exists (fun (c, _) -> c = ctor) -> Some tn
        | _ -> None)

let definitionTarget
    (path: string)
    (lines: string list)
    (line: int)
    (col: int)
    : (string option * int * int * int) option =
    let _, stmts, _, _, partials = analyzeCached path lines

    let imports =
        stmts
        |> List.choose (fun (_, c) ->
            match c.Kind with
            | Script.KImport lm -> Some lm
            | _ -> None)

    // local KType first; else the import that declared the type — a
    // qualified key ("A.T") names its import's alias, a plain one is
    // unambiguous, so the name alone picks the module [D:qualified-type-names]
    let typeSite (tyName: string) (member_: string option) =
        let viaAlias =
            match tyName.Split '.' with
            | [| alias; plain |] ->
                imports
                |> List.tryFind (fun lm -> lm.Alias = alias && List.contains plain lm.TypeNames)
                |> Option.bind (fun lm ->
                    targetStmts lm.AbsPath
                    |> Option.bind (fun (_, mstmts) -> typeSiteIn mstmts plain member_)
                    |> Option.map (fun (pl, pc, len) -> Some lm.AbsPath, pl, pc, len))
            | _ -> None

        match typeSiteIn stmts tyName member_ with
        | Some(pl, pc, len) -> Some(None, pl, pc, len)
        | None when viaAlias.IsSome -> viaAlias
        | None ->
            imports
            |> List.tryPick (fun lm ->
                if lm.TypeDefs |> List.exists (fun (tn, _) -> tn = tyName) then
                    targetStmts lm.AbsPath
                    |> Option.bind (fun (_, mstmts) -> typeSiteIn mstmts tyName member_)
                    |> Option.map (fun (pl, pc, len) -> Some lm.AbsPath, pl, pc, len)
                else
                    None)

    let recordHasField (env: TypeEnv) (recName: string) (field: string) =
        match Map.tryFind recName env.Types with
        | Some(Record d) -> d.Fields |> List.exists (fun (f, _) -> f = field)
        | _ -> false

    let letSite (useHead: int) (n: string) = letSiteIn stmts (Some useHead) n

    // a PCase whose ctor word contains the column (a payload binder is
    // a local binder — the binder-span park, not this)
    // a qualified case's qualifier word answers `Choice1Of2 alias`, its case
    // word `Choice2Of2 (Some alias, ctor)` [D:qualified-case-patterns]
    let rec patCaseAt (jcol: int) (p: Ast.Pattern) : Choice<string, string option * string> option =
        let deeper =
            match p.PKind with
            | Ast.PCase(_, Some inner, _) -> patCaseAt jcol inner
            | Ast.PTuple ps -> ps |> List.tryPick (patCaseAt jcol)
            | Ast.PRecord fields -> fields |> List.tryPick (snd >> patCaseAt jcol)
            | _ -> None

        match deeper with
        | Some d -> Some d
        | None ->
            match p.PKind with
            | Ast.PCase(ctor, _, None) when p.PSpan.Start.Col <= jcol && jcol < p.PSpan.Start.Col + ctor.Length ->
                Some(Choice2Of2(None, ctor))
            | Ast.PCase(ctor, _, Some(m, mspan)) ->
                let caseCol = mspan.End.Col + 1

                if mspan.Start.Col <= jcol && jcol < mspan.End.Col then
                    Some(Choice1Of2 m)
                elif caseCol <= jcol && jcol < caseCol + ctor.Length then
                    Some(Choice2Of2(Some m, ctor))
                else
                    None
            | _ -> None

    let rec matchCaseAt (jcol: int) (te: Check.TypedExpr) : Choice<string, string option * string> option =
        let here =
            match te.Kind with
            | Check.TEMatch(_, arms) -> arms |> List.tryPick (fun (p, _, _) -> patCaseAt jcol p)
            | _ -> None

        match here with
        | Some c -> Some c
        | None -> Check.childExprs te |> List.tryPick (matchCaseAt jcol)

    toLogical (withPartials stmts partials) line col
    // inside a hole there is nothing to jump from [D:block-recovery]
    |> Option.filter (fun (_, chk, jcol) -> not (inHole chk jcol))
    |> Option.bind (fun (useLl, chk, jcol) ->
        match chk.Kind with
        | Script.KImport lm ->
            // definition on the import path opens the imported file; a
            // path that does not resolve never reaches here (the failed
            // import leaves no KImport statement) [D:lsp-cross-file]
            let q1 = useLl.Text.IndexOf '"'
            let q2 = (if q1 >= 0 then useLl.Text.IndexOf('"', q1 + 1) else -1)

            if q1 >= 0 && q2 > q1 && jcol - 1 >= q1 && jcol - 1 <= q2 then
                Some(Some lm.AbsPath, 1, 1, 0)
            else
                None
        | _ ->

            let env = chk.Env

            // a signed command's head opens its signature file; a flag jumps
            // to its field declaration; an unsigned head stays quiet
            // [D:lsp-cross-file]
            let sigSite () =
                teOf chk
                |> Option.bind (cmdSurfaceAt jcol)
                |> Option.bind (fun (prog, progCol, words) ->
                    sigInfosCached path lines
                    |> List.tryFind (fun si -> si.Tool = prog)
                    |> Option.bind (fun si ->
                        if progCol <= jcol && jcol < progCol + prog.Length then
                            Some(Some si.SigPath, 1, 1, 0)
                        else
                            match
                                words
                                |> List.tryFind (fun (w, sp) ->
                                    w.StartsWith "-" && sp.Start.Col <= jcol && jcol < sp.End.Col)
                            with
                            | Some(w, _) ->
                                sigFlagField si (List.map fst words) w
                                |> Option.bind (fun (rn, f, _, _, sigStmts) ->
                                    typeSiteIn sigStmts rn (Some f)
                                    |> Option.map (fun (pl, pc, len) -> Some si.SigPath, pl, pc, len))
                            | None ->
                                // a sub token jumps to its case's record
                                // declaration [D:scoped-sigs] — the record
                                // the checker would pick for this line
                                words
                                |> List.tryFind (fun (w, sp) ->
                                    not (w.StartsWith "-") && sp.Start.Col <= jcol && jcol < sp.End.Col)
                                |> Option.bind (fun _ -> sigRecordFor si (List.map fst words))
                                |> Option.bind (fun (rn, _) ->
                                    targetStmts si.SigPath
                                    |> Option.bind (fun (_, sigStmts) ->
                                        typeSiteIn sigStmts rn None
                                        |> Option.map (fun (pl, pc, len) -> Some si.SigPath, pl, pc, len)))))

            let fromPattern =
                teOf chk
                |> Option.bind (matchCaseAt jcol)
                |> Option.bind (function
                    | Choice1Of2 alias ->
                        imports
                        |> List.tryFind (fun lm -> lm.Alias = alias)
                        |> Option.map (fun lm -> Some lm.AbsPath, 1, 1, 0)
                    | Choice2Of2(Some alias, ctor) ->
                        env.ModuleTypes
                        |> Map.tryFind alias
                        |> Option.bind (
                            Map.tryPick (fun _ key ->
                                match Map.tryFind key env.Types with
                                | Some(Union d) when d.Cases |> List.exists (fun (c, _) -> c = ctor) -> Some key
                                | _ -> None)
                        )
                        |> Option.bind (fun tn -> typeSite tn (Some ctor))
                    | Choice2Of2(None, ctor) -> unionOf env ctor |> Option.bind (fun tn -> typeSite tn (Some ctor)))

            match fromPattern with
            | Some r -> Some r
            | None ->
                teOf chk
                |> Option.bind (fun te -> nodeAt te jcol)
                |> Option.bind (fun node ->
                    match node.Kind with
                    // a qualified module member: the member word jumps to its
                    // declaration in the module file, the alias word to the
                    // file itself [D:lsp-cross-file] (a dotted builtin matches
                    // no import and stays on its own arms)
                    | Check.TEVar n when n.Contains '.' ->
                        (match n.Split '.' with
                         | [| alias; mem |] ->
                             imports
                             |> List.tryFind (fun lm -> lm.Alias = alias)
                             |> Option.bind (fun lm ->
                                 if wordAt useLl.Text jcol = Some alias then
                                     Some(Some lm.AbsPath, 1, 1, 0)
                                 else
                                     targetStmts lm.AbsPath
                                     |> Option.bind (fun (_, mstmts) -> letSiteIn mstmts None mem)
                                     |> Option.map (fun (pl, pc, len) -> Some lm.AbsPath, pl, pc, len))
                         | _ -> None)
                    | Check.TEVar n when Types.isUserName n && not (n.Contains '.') ->
                        if Char.IsUpper n[0] then
                            // expression-position union case
                            unionOf env n |> Option.bind (fun tn -> typeSite tn (Some n))
                        else
                            // local binders first — lexical, innermost wins
                            // (params, inner lets, pattern payload binders);
                            // the top-level scan is the fallback
                            // [PLAN-diagnostics-arc C]
                            match teOf chk |> Option.bind (localDef n jcol) with
                            | Some(Some bspan) ->
                                let pl, pc = Script.translate useLl bspan.Start.Col
                                Some(None, pl, pc, n.Length)
                            | _ -> letSite useLl.Head n |> Option.map (fun (pl, pc, len) -> None, pl, pc, len)
                    | Check.TEField(target, field) when jcol > target.Span.End.Col ->
                        (match target.Ty with
                         | TNamed(tn, _) -> typeSite tn (Some field)
                         | _ -> None)
                    | Check.TERecord(recName, _) ->
                        // the literal's own words: the record name or a field;
                        // the type's env key picks the declaration
                        let key =
                            match node.Ty with
                            | TNamed(k, _) -> k
                            | _ -> recName

                        wordAt useLl.Text jcol
                        |> Option.bind (fun w ->
                            if w = recName then typeSite key None
                            elif recordHasField env key w then typeSite key (Some w)
                            else None)
                    // `from json T`: the adapter's type name jumps to its
                    // declaration [PLAN-diagnostics-arc A3]
                    | Check.TEFrom(_, top, _, _, _, _) ->
                        let topName =
                            match top with
                            | Check.TopRec d -> d.Name
                            | Check.TopUnion u -> u.Name
                            | Check.TopValue _ -> ""

                        // the slot's env key, peeled from the node's type
                        let rec keyOf (ty: Ty) =
                            match ty with
                            | TSeq t
                            | TNamed("Map", [ _; t ]) -> keyOf t
                            | TNamed(k, _) when k.EndsWith("." + topName) -> k
                            | _ -> topName

                        wordAt useLl.Text jcol
                        |> Option.bind (fun w -> if w = topName then typeSite (keyOf node.Ty) None else None)
                    | Check.TEFromYaml(tyName, _, _)
                    | Check.TEFromToml(tyName, _) ->
                        wordAt useLl.Text jcol
                        |> Option.bind (fun w -> if w = tyName then typeSite tyName None else None)
                    // `Env.load T` / `Args.load T`: the target type name jumps to
                    // its declaration — the bespoke arm absorbs the argument, so
                    // it is no TEVar; resolve it off the load node's own def
                    | Check.TEEnvLoad(def, _) ->
                        wordAt useLl.Text jcol
                        |> Option.bind (fun w -> if w = def.Name then typeSite def.Name None else None)
                    | Check.TEArgsLoad target ->
                        let tyName =
                            match target with
                            | Check.ArgsRecord def -> def.Name
                            | Check.ArgsUnion(udef, _) -> udef.Name
                            | Check.ArgsShared(outer, _, _, _) -> outer.Name

                        wordAt useLl.Text jcol
                        |> Option.bind (fun w -> if w = tyName then typeSite tyName None else None)
                    // the schema= name opens the vendored file [D:schema-hover]
                    // — the checker's own resolution; a not-vendored name
                    // stays quiet (the hover carries the teaching)
                    | Check.TEYaml(_, Some sname, _) when onSchemaToken sname useLl.Text jcol ->
                        (match Script.resolveSchemaFile path sname with
                         | Ok(_, file) -> Some(Some file, 1, 1, 0)
                         | Error _ -> None)
                    | _ -> None)
                |> Option.orElseWith sigSite
                // a type name the node walk does not claim
                // [D:lsp-typename]: declarations are not expressions —
                // the payload after `of`, a field's type in a record
                // decl — so a word-level fallback resolves any declared
                // user type name
                |> Option.orElseWith (fun () ->
                    wordAt useLl.Text jcol
                    |> Option.bind (fun w ->
                        if
                            w.Length > 0
                            && Char.IsUpper w[0]
                            && Types.isUserName w
                            && Map.containsKey w env.Types
                        then
                            typeSite w None
                        else
                            None)))

/// the single-file view of definitionTarget: Some (physLine, physCol,
/// nameLength) when the definition is in this file, None otherwise —
/// the unit pins' surface; the handler serves definitionTarget
let definitionFor (lines: string list) (line: int) (col: int) : (int * int * int) option =
    definitionTarget "defn" lines line col
    |> Option.bind (function
        | None, pl, pc, len -> Some(pl, pc, len)
        | _ -> None)

// ---- references, highlight, rename [D:lsp-rename] -------------------
// One table per request: every occurrence in the file keyed by the
// binder that owns it. Inside a statement Check.occurrences decides
// (the checker's own scoping); across statements a free name belongs
// to the last top-level binder above it — there is no `let rec`, so a
// use inside a binder's own statement is the previous binder's — and
// a signed member's signature line is a site of the same binder.
// A name no statement binds (a builtin, an imported member) keys to
// the file-less TopKey: references and highlight still list its uses
// in this file; rename refuses it.

type private BinderKey =
    // statement index + the binder's span in that statement's joined text
    | LocalKey of int * Span
    // name + the index of the binding statement; -1 = not bound in this file
    | TopKey of string * int
    // a type (None) or its record field / union case, by the type's env
    // key [D:lsp-rename-members]
    | MemberKey of string * string option

type private FileOcc = {
    OName: string
    Key: BinderKey
    OBinderSite: bool
    // 1-based physical line and column, the name's own characters —
    // the `$`/`$@` sigil and a `(x)`'s parens ride on the node's span
    // but are not the name
    OLine: int
    OCol: int
    // statement index, for the scope-extent walk
    OStmt: int
}

let private valueOccurrences (stmts: (Script.LogicalLine * Script.CheckedStatement) list) : FileOcc list =
    let arr = List.toArray stmts
    let lastTop = Collections.Generic.Dictionary<string, int>()
    let found = ResizeArray<FileOcc>()

    let isSigOf (n: string) (j: int) =
        match (snd arr[j]).Kind with
        | Script.KSig(bn, _) -> bn = n
        | _ -> false

    for i in 0 .. arr.Length - 1 do
        let ll, chk = arr[i]

        // the name's own characters inside the span: a word-bounded
        // search of the joined text, mapped back to physical
        let locate (name: string) (sp: Span) =
            let lo = max 0 (sp.Start.Col - 1)
            let hi = min ll.Text.Length (max (sp.End.Col - 1) (lo + name.Length))

            wordFind name ll.Text lo hi
            |> Option.map (fun jc ->
                let pl, pc = Script.translate ll jc
                pl, pc)

        let add (name: string) (key: BinderKey) (site: bool) (pos: (int * int) option) =
            pos
            |> Option.iter (fun (pl, pc) ->
                found.Add {
                    OName = name
                    Key = key
                    OBinderSite = site
                    OLine = pl
                    OCol = pc
                    OStmt = i
                })

        let topKey (n: string) =
            match lastTop.TryGetValue n with
            | true, j -> TopKey(n, j)
            | _ -> TopKey(n, -1)

        // uses first: a free name in this statement is the binder above
        teOf chk
        |> Option.iter (fun te ->
            for o in Check.occurrences te do
                let key =
                    match o.Binder with
                    | Check.Local sp -> LocalKey(i, sp)
                    | Check.TopLevel n -> topKey n

                add o.Name key o.IsBinderSite (locate o.Name o.Span))

        // then this statement's own top-level binder sites
        let siteOf (ll: Script.LogicalLine) (n: string) =
            binderCol n ll.Text |> Option.map (Script.translate ll)

        let bound =
            match chk.Kind with
            | Script.KLet(n, _, _) when Check.occurrenceName n ->
                // the implementation of a signed member shares its sig's key
                let key =
                    match lastTop.TryGetValue n with
                    | true, j when isSigOf n j -> TopKey(n, j)
                    | _ -> TopKey(n, i)

                add n key true (siteOf ll n)
                [ n, key ]
            | Script.KSig(n, _) when Check.occurrenceName n ->
                add n (TopKey(n, i)) true (siteOf ll n)
                [ n, TopKey(n, i) ]
            | Script.KLetPat(pat, _, _) ->
                Check.patNameSpans pat
                |> List.filter (fun (n, _) -> Check.occurrenceName n)
                |> List.map (fun (n, sp) ->
                    add n (TopKey(n, i)) true (locate n sp)
                    n, TopKey(n, i))
            | _ -> []

        for n, key in bound do
            match key with
            | TopKey(_, j) -> lastTop[n] <- j
            | LocalKey _
            | MemberKey _ -> ()

    List.ofSeq found

// ---- member occurrences [D:lsp-rename-members] -----------------------
// The second table: every occurrence of a type, a record field or a
// union case, keyed by the type's env key. Inside a statement the
// typed tree resolves each use (a TEField's target type, a literal's
// and an update's record type, a pattern's scrutinee type, the adapter
// and load forms' type name); the declaration's own words are textual
// — Ast.Decl carries no span per member or per type reference. A
// field access whose target is a row variable serves any record with
// that field, so it is no occurrence of a named one: it is listed
// apart, and renaming a same-named field refuses naming it (R1).

// string-literal positions of a joined line — the declaration searches
// skip a `[<Wire "…">]` argument that spells a member or type name
let private stringMask (text: string) : bool[] =
    let m = Array.zeroCreate text.Length
    let mutable inStr = false
    let mutable i = 0

    while i < text.Length do
        let c = text[i]

        if inStr then
            m[i] <- true

            if c = '\\' && i + 1 < text.Length then
                m[i + 1] <- true
                i <- i + 1
            elif c = '"' then
                inStr <- false
        elif c = '"' then
            inStr <- true
            m[i] <- true

        i <- i + 1

    m

// every word-bounded match of `name` in text[lo..hi) outside string
// literals, 1-based cols
let private wordFinds (name: string) (text: string) (mask: bool[]) (lo: int) (hi: int) : int list =
    let rec go from acc =
        match wordFind name text from hi with
        | None -> List.rev acc
        | Some jc -> go jc (if mask[jc - 1] then acc else jc :: acc)

    go lo []

// the index of the first non-blank char at or after idx / before idx
let private nextIdx (text: string) (idx: int) : int option =
    let mutable k = idx

    while k < text.Length && (text[k] = ' ' || text[k] = '\t') do
        k <- k + 1

    if k < text.Length then Some k else None

let private prevIdx (text: string) (idx: int) : int option =
    let mutable k = idx - 1

    while k >= 0 && (text[k] = ' ' || text[k] = '\t') do
        k <- k - 1

    if k >= 0 then Some k else None

// the word (or dotted path) `name` followed by `=` (never `==`) in
// text[lo..hi): a literal's or an update's key, never its value side —
// `{ id = id }` renames the key only
let private keyFind (name: string) (text: string) (lo: int) (hi: int) : int option =
    let rec go from =
        match wordFind name text from hi with
        | None -> None
        | Some jc ->
            match nextIdx text (jc - 1 + name.Length) with
            | Some k when text[k] = '=' && not (k + 1 < text.Length && text[k + 1] = '=') -> Some jc
            | _ -> go jc

    go lo

// a word in a declaration's text that is a field (before `:`) or a
// case (after `|`, `=` or an attribute's `]`) — the member words a
// type-reference search skips
let private isFieldWord (text: string) (jc: int) (len: int) =
    match nextIdx text (jc - 1 + len) with
    | Some k -> text[k] = ':'
    | None -> false

let private isCaseWord (text: string) (jc: int) =
    match prevIdx text (jc - 1) with
    | Some k -> text[k] = '|' || text[k] = '=' || text[k] = ']'
    | None -> false

// a declared member's type with the type's parameters bound to a use's
// arguments — a generic record's field, a generic union's payload
let private memberTy (pars: string list) (args: Ty list) (ty: Ty) : Ty =
    let m = List.zip pars (List.truncate pars.Length args) |> Map.ofList

    let rec go (t: Ty) =
        match t with
        | TVar v ->
            (match Map.tryFind v m with
             | Some r -> r
             | None -> t)
        | TFun(a, b) -> TFun(go a, go b)
        | TSeq e -> TSeq(go e)
        | TTuple ts -> TTuple(List.map go ts)
        | TNamed(n, xs) -> TNamed(n, List.map go xs)
        | TRowVar(r, fields) -> TRowVar(r, fields |> List.map (fun (f, ft) -> f, go ft))
        | t -> t

    if pars.Length = args.Length then go ty else ty

let private plainName (key: string) =
    match key.LastIndexOf '.' with
    | -1 -> key
    | i -> key.Substring(i + 1)

// the first `ch` outside string literals
let private firstUnmasked (ch: char) (text: string) (mask: bool[]) : int =
    let mutable i = 0

    while i < text.Length && (text[i] <> ch || mask[i]) do
        i <- i + 1

    if i < text.Length then i else -1

/// the member table and the row sites — a field access the typed tree
/// cannot pin to one record: name, physical line and col of the field
/// word, and the access's spelling
let private memberOccurrences
    (stmts: (Script.LogicalLine * Script.CheckedStatement) list)
    : FileOcc list * (string * int * int * string) list =
    let arr = List.toArray stmts
    let found = ResizeArray<FileOcc>()
    let rows = ResizeArray<string * int * int * string>()
    // the types this file has declared so far, for the textual search
    let declared = ResizeArray<string>()

    for i in 0 .. arr.Length - 1 do
        let ll, chk = arr[i]
        let env = chk.Env
        let text = ll.Text

        let add (name: string) (key: BinderKey) (site: bool) (jc: int option) =
            jc
            |> Option.iter (fun jc ->
                let pl, pc = Script.translate ll jc

                found.Add {
                    OName = name
                    Key = key
                    OBinderSite = site
                    OLine = pl
                    OCol = pc
                    OStmt = i
                })

        let row (name: string) (jc: int) (display: string) =
            let pl, pc = Script.translate ll jc
            rows.Add(name, pl, pc, display)

        let spanText (sp: Span) =
            let lo = max 0 (sp.Start.Col - 1)
            let hi = min text.Length (sp.End.Col - 1)
            if hi > lo then text.Substring(lo, hi - lo) else ""

        let inSpan (name: string) (sp: Span) =
            let lo = max 0 (sp.Start.Col - 1)
            wordFind name text lo (min text.Length (max (sp.End.Col - 1) (lo + name.Length)))

        let recordOf (k: string) =
            match Types.typeDefFor env k with
            | Some(Record d) -> Some d
            | _ -> None

        let isAnon (k: string) = k.StartsWith "{|"

        // the type word of an adapter or load form, keyed by the env
        // key the node's type carries
        let typeUse (key: string) (sp: Span) =
            add (plainName key) (MemberKey(key, None)) false (inSpan (plainName key) sp)

        // the record type behind a value's type: Some (key, def, args)
        // for a named record (an anonymous one has no name to rename)
        let recordTy (ty: Ty) =
            match ty with
            | TNamed(k, args) when not (isAnon k) -> recordOf k |> Option.map (fun d -> k, d, args)
            | _ -> None

        let fieldTyOf (d: RecordDef) (args: Ty list) (f: string) =
            d.Fields
            |> List.tryFind (fst >> (=) f)
            |> Option.map (snd >> memberTy d.Params args)

        // a field word resolved against the target's type: a named
        // record's field is an occurrence; a row variable's is a row
        // site; an anonymous record's is neither
        let fieldAt (ty: Ty) (f: string) (jc: int option) (display: string) : Ty option =
            match ty with
            | TNamed(k, args) when isAnon k -> recordOf k |> Option.bind (fun d -> fieldTyOf d args f)
            | TNamed(k, args) ->
                recordOf k
                |> Option.bind (fun d ->
                    if d.Fields |> List.exists (fst >> (=) f) then
                        add f (MemberKey(k, Some f)) false jc
                        fieldTyOf d args f
                    else
                        None)
            | TRowVar(_, fields) ->
                jc |> Option.iter (fun jc -> row f jc display)
                fields |> List.tryFind (fst >> (=) f) |> Option.map snd
            | _ ->
                jc |> Option.iter (fun jc -> row f jc display)
                None

        let caseUse (key: string) (ctor: string) (jc: int option) =
            add ctor (MemberKey(key, Some ctor)) false jc

        let payloadOf (d: UnionDef) (ctor: string) =
            d.Cases |> List.tryPick (fun (c, p) -> if c = ctor then p else None)

        // the union a bare case names, and its payload type for the use
        let caseOf (ty: Ty option) (ctor: string) : (string * Ty option) option =
            let viaTy =
                match ty with
                | Some(TNamed(k, args)) ->
                    (match Types.typeDefFor env k with
                     | Some(Union d) when d.Cases |> List.exists (fst >> (=) ctor) ->
                         Some(k, payloadOf d ctor |> Option.map (memberTy d.Params args))
                     | _ -> None)
                | _ -> None

            viaTy
            |> Option.orElseWith (fun () ->
                unionOf env ctor
                |> Option.map (fun k ->
                    let payload =
                        match Map.tryFind k env.Types with
                        | Some(Union d) -> payloadOf d ctor
                        | _ -> None

                    k, payload))

        let rec walkPat (ty: Ty option) (p: Pattern) : unit =
            match p.PKind with
            | PCase(ctor, arg, None) ->
                (match caseOf ty ctor with
                 | Some(k, payload) ->
                     caseUse k ctor (inSpan ctor p.PSpan)
                     arg |> Option.iter (walkPat payload)
                 | None -> arg |> Option.iter (walkPat None))
            | PCase(ctor, arg, Some(alias, mspan)) ->
                // an imported union's case [D:qualified-case-patterns]
                let key =
                    env.ModuleTypes
                    |> Map.tryFind alias
                    |> Option.bind (
                        Map.tryPick (fun _ key ->
                            match Map.tryFind key env.Types with
                            | Some(Union d) when d.Cases |> List.exists (fst >> (=) ctor) -> Some key
                            | _ -> None)
                    )

                let payload =
                    key
                    |> Option.bind (fun k -> caseOf (Some(TNamed(k, []))) ctor)
                    |> Option.bind snd

                key |> Option.iter (fun k -> caseUse k ctor (Some(mspan.End.Col + 1)))
                arg |> Option.iter (walkPat payload)
            | PRecord fields ->
                for (f, fspan), sub in fields do
                    let fty =
                        match ty with
                        | Some t -> fieldAt t f (inSpan f fspan) (spanText p.PSpan)
                        | None ->
                            inSpan f fspan |> Option.iter (fun jc -> row f jc (spanText p.PSpan))
                            None

                    walkPat fty sub
            | PTuple ps ->
                (match ty with
                 | Some(TTuple ts) when ts.Length = ps.Length -> List.iter2 (fun t p -> walkPat (Some t) p) ts ps
                 | _ -> ps |> List.iter (walkPat None))
            | PSeqList ps ->
                let e =
                    match ty with
                    | Some(TSeq e) -> Some e
                    | _ -> None

                ps |> List.iter (walkPat e)
            | PCons(h, t) ->
                (match ty with
                 | Some(TSeq e) ->
                     walkPat (Some e) h
                     walkPat ty t
                 | _ ->
                     walkPat None h
                     walkPat None t)
            | _ -> patChildren p |> List.iter (walkPat None)

        // the element a `for` binder takes from its source
        let elemTy (ty: Ty) =
            match ty with
            | TSeq e -> Some e
            | TNamed("Map", [ k; v ]) -> Some(TTuple [ k; v ])
            | _ -> None

        let rec walk (te: TypedExpr) : unit =
            match te.Kind with
            | TEField(target, f) ->
                let lo = target.Span.End.Col - 1

                let jc =
                    wordFind f text lo (min text.Length (max (te.Span.End.Col - 1) (lo + 1 + f.Length)))

                fieldAt target.Ty f jc (spanText te.Span) |> ignore
                walk target
            | TERecord(recName, fields) ->
                (match recordTy te.Ty with
                 | Some(k, _, _) ->
                     let start = te.Span.Start.Col - 1
                     let brace = text.IndexOf('{', start)

                     // a named literal's record word before the `{`
                     if brace > start then
                         let plain = plainName recName
                         add plain (MemberKey(k, None)) false (wordFind plain text start brace)

                     let mutable prevEnd = (if brace >= 0 then brace else start)

                     for f, v in fields |> List.sortBy (fun (_, v) -> v.Span.Start.Col) do
                         add f (MemberKey(k, Some f)) false (keyFind f text prevEnd (v.Span.Start.Col - 1))
                         prevEnd <- v.Span.End.Col - 1
                 | None -> ())

                fields |> List.iter (snd >> walk)
            | TEUpdate(src, ups) ->
                let mutable prevEnd = src.Span.End.Col - 1

                for path, v in ups |> List.sortBy (fun (_, v) -> v.Span.Start.Col) do
                    let pathText = String.concat "." path

                    keyFind pathText text prevEnd (v.Span.Start.Col - 1)
                    |> Option.iter (fun jc ->
                        let display = $"{{ {spanText src.Span} with {pathText} = … }}"
                        let mutable ty = Some src.Ty
                        let mutable off = jc

                        for seg in path do
                            ty <-
                                (match ty with
                                 | Some t -> fieldAt t seg (Some off) display
                                 | None ->
                                     row seg off display
                                     None)

                            off <- off + seg.Length + 1)

                    prevEnd <- v.Span.End.Col - 1

                walk src
                ups |> List.iter (snd >> walk)
            | TEVar n when Types.isUserName n && not (n.Contains '.') && Char.IsUpper n[0] ->
                caseOf None n |> Option.iter (fun (k, _) -> caseUse k n (inSpan n te.Span))
            | TEMatch(s, arms) ->
                walk s

                for p, g, b in arms do
                    walkPat (Some s.Ty) p
                    g |> Option.iter walk
                    walk b
            | TELetPat(pat, v, b) ->
                walkPat (Some v.Ty) pat
                walk v
                walk b
            | TELambdaPat(pat, b) ->
                walkPat
                    (match te.Ty with
                     | TFun(d, _) -> Some d
                     | _ -> None)
                    pat

                walk b
            | TEFrom _ ->
                // the slot's env key, peeled from the adapter's type
                let rec keyOf (ty: Ty) =
                    match ty with
                    | TFun(_, o) -> keyOf o
                    | TSeq t
                    | TNamed("Map", [ _; t ]) -> keyOf t
                    | TNamed(k, _) -> Some k
                    | _ -> None

                keyOf te.Ty |> Option.iter (fun k -> typeUse k te.Span)

                // an anonymous record adapter type names its field types
                // in the slot too (`{| users: seq<User> |}`) — each is a
                // use of that type [D:lsp-rename-members]
                let rec namedIn (seen: Set<string>) (ty: Ty) : string list =
                    match ty with
                    | TFun(_, o) -> namedIn seen o
                    | TSeq t
                    | TNamed("Map", [ _; t ]) -> namedIn seen t
                    | TNamed(k, args) when isAnon k && not (seen.Contains k) ->
                        (match recordOf k with
                         | Some d -> d.Fields |> List.collect (snd >> namedIn (seen.Add k))
                         | None -> [])
                        @ (args |> List.collect (namedIn seen))
                    | TNamed(k, args) when not (isAnon k) -> k :: (args |> List.collect (namedIn seen))
                    | TTuple ts -> ts |> List.collect (namedIn seen)
                    | _ -> []

                match keyOf te.Ty with
                | Some k when isAnon k ->
                    let mask = stringMask text
                    let lo = max 0 (te.Span.Start.Col - 1)
                    let hi = min text.Length (te.Span.End.Col - 1)

                    for inner in namedIn Set.empty (TNamed(k, [])) |> List.distinct do
                        if declared.Contains(plainName inner) then
                            for jc in wordFinds (plainName inner) text mask lo hi do
                                add (plainName inner) (MemberKey(inner, None)) false (Some jc)
                | _ -> ()
            | TEVar n when Types.isUserName n && n.Contains '.' && Char.IsUpper n[n.LastIndexOf '.' + 1] ->
                // an imported union's case in an expression (`Lib.Square p`),
                // keyed as its pattern sites are [D:qualified-case-patterns]
                let alias = n.Substring(0, n.LastIndexOf '.')
                let ctor = n.Substring(n.LastIndexOf '.' + 1)

                env.ModuleTypes
                |> Map.tryFind alias
                |> Option.bind (
                    Map.tryPick (fun _ key ->
                        match Map.tryFind key env.Types with
                        | Some(Union d) when d.Cases |> List.exists (fst >> (=) ctor) -> Some key
                        | _ -> None)
                )
                |> Option.iter (fun k ->
                    let lo = max 0 (te.Span.Start.Col - 1) + alias.Length + 1
                    caseUse k ctor (wordFind ctor text lo (min text.Length (lo + ctor.Length))))
            | TEVar n when n.StartsWith Types.createVarPrefix ->
                // `T.create` [D:validated-types]: the type word is a use of T
                match te.Ty with
                | TFun(_, TNamed(key, _)) -> typeUse key te.Span
                | _ -> ()
            | TEFromYaml(tyName, _, _)
            | TEFromToml(tyName, _) -> typeUse tyName te.Span
            | TEEnvLoad(def, _) -> typeUse def.Name te.Span
            | TEArgsLoad target ->
                typeUse
                    (match target with
                     | ArgsRecord def -> def.Name
                     | ArgsUnion(udef, _) -> udef.Name
                     | ArgsShared(outer, _, _, _) -> outer.Name)
                    te.Span
            | TEYaml(tpl, _, _) -> walkTpl tpl
            | _ -> childExprs te |> List.iter walk

        and walkTpl (tpl: TypedYamlTpl) : unit =
            match tpl with
            | TYtScalar _
            | TYtBlock _
            | TYtDrop _ -> ()
            | TYtSplice e -> walk e
            | TYtSeq(items, _) ->
                for item in items do
                    match item with
                    | TYtItem t -> walkTpl t
                    | TYtDropItem(t, _) -> walkTpl t
                    | TYtForItems(binder, src, body) ->
                        walk src
                        walkPat (elemTy src.Ty) binder

                        for i in body do
                            walkTpl (TYtSeq([ i ], Unchecked.defaultof<Ast.Span>))
            | TYtMap(entries, _) ->
                for entry in entries do
                    match entry with
                    | TYtPair(TYtKeyLit _, v) -> walkTpl v
                    | TYtPair(TYtKeySplice k, v) ->
                        walk k
                        walkTpl v
                    | TYtForEntries(binder, src, body) ->
                        walk src
                        walkPat (elemTy src.Ty) binder

                        for e in body do
                            walkTpl (TYtMap([ e ], Unchecked.defaultof<Ast.Span>))

        // type references in a declaration's or a signature's text: every
        // word match of a declared type outside the member words, the
        // type's own parameters and an imported `Lib.T` spelling
        let typeRefs (lo: int) (hi: int) (pars: string list) =
            let mask = stringMask text

            for t in declared do
                if not (List.contains t pars) then
                    for jc in wordFinds t text mask lo hi do
                        let dotted = jc >= 2 && text[jc - 2] = '.'

                        if not (dotted || isFieldWord text jc t.Length || isCaseWord text jc) then
                            add t (MemberKey(t, None)) false (Some jc)

        match chk.Kind with
        | Script.KType(d, validator) ->
            let mask = stringMask text

            let eq = firstUnmasked '=' text mask

            let bodyEnd =
                match d.Validate with
                | Some v -> v.Span.Start.Col - 1
                | None -> text.Length

            if not (declared.Contains d.Name) then
                declared.Add d.Name

            add d.Name (MemberKey(d.Name, None)) true (wordFinds d.Name text mask 0 (max eq 0) |> List.tryHead)

            if eq >= 0 then
                match d.Body with
                | DRecord fields ->
                    for f, _, _ in fields do
                        add
                            f
                            (MemberKey(d.Name, Some f))
                            true
                            (wordFinds f text mask eq bodyEnd
                             |> List.tryFind (fun jc -> isFieldWord text jc f.Length))
                | DUnion cases ->
                    for c, _, _ in cases do
                        add
                            c
                            (MemberKey(d.Name, Some c))
                            true
                            (wordFinds c text mask eq bodyEnd |> List.tryFind (fun jc -> isCaseWord text jc))

                typeRefs (eq + 1) bodyEnd d.Params

            // `validate` binds the single case's payload [D:validated-types]
            d.Validate
            |> Option.iter (fun v ->
                let payload =
                    match d.Body with
                    | DUnion [ _, p, _ ] -> p
                    | _ -> None

                walkPat payload v.Binder)

            validator |> Option.iter (snd >> walk)
        | Script.KSig _ ->
            let mask = stringMask text

            match firstUnmasked ':' text mask with
            | -1 -> ()
            | colon -> typeRefs (colon + 1) text.Length []
        | Script.KLetPat(pat, _, te) ->
            walkPat (Some te.Ty) pat
            walk te
        | _ -> teOf chk |> Option.iter walk

    List.ofSeq found, List.ofSeq rows

// one table per request: the value occurrences first — a position is
// a value's before it is a member's — then the member occurrences
let private fileOccurrences (stmts: (Script.LogicalLine * Script.CheckedStatement) list) : FileOcc list =
    valueOccurrences stmts @ fst (memberOccurrences stmts)

/// the occurrence under the cursor (physical 1-based line, col), with
/// the whole file's table
let private occurrenceAt (path: string) (lines: string list) (line: int) (col: int) : (FileOcc list * FileOcc) option =
    let _, stmts0, _, _, partials = analyzeCached path lines
    let stmts = withPartials stmts0 partials
    let table = fileOccurrences stmts

    let covering =
        table
        |> List.filter (fun o -> o.OLine = line && o.OCol <= col && col < o.OCol + o.OName.Length)

    // an imported case's expression use sits in both tables (the value
    // `Lib.Square`, the case `Square`): the case key is the one its
    // pattern sites share, so it answers [D:qualified-case-patterns]
    covering
    |> List.tryFind (fun o ->
        match o.Key with
        | MemberKey(_, Some _) -> covering |> List.exists (fun v -> v.OName.Contains '.')
        | _ -> false)
    |> Option.orElse (List.tryHead covering)
    |> Option.map (fun o -> table, o)

/// every occurrence of the binder at the cursor, as (physLine, physCol,
/// len, isBinderSite) sorted by position; None when the cursor is on no
/// name. Pure — the references and highlight handlers and the unit pins
/// share it.
let occurrencesFor (path: string) (lines: string list) (line: int) (col: int) : (int * int * int * bool) list option =
    occurrenceAt path lines line col
    |> Option.map (fun (table, o) ->
        table
        |> List.filter (fun x -> x.Key = o.Key)
        |> List.map (fun x -> x.OLine, x.OCol, x.OName.Length, x.OBinderSite)
        |> List.distinct
        |> List.sort)

/// textDocument/references: (physLine, physCol, len) per occurrence;
/// `includeDeclaration` keeps or drops the binder sites
let referencesFor
    (path: string)
    (lines: string list)
    (line: int)
    (col: int)
    (includeDeclaration: bool)
    : (int * int * int) list option =
    occurrencesFor path lines line col
    |> Option.map (
        List.choose (fun (l, c, len, site) ->
            if site && not includeDeclaration then
                None
            else
                Some(l, c, len))
    )

/// textDocument/documentHighlight: (physLine, physCol, len, kind) with
/// kind 3 (Write) at binder sites and 2 (Read) at uses
let documentHighlightFor
    (path: string)
    (lines: string list)
    (line: int)
    (col: int)
    : (int * int * int * int) list option =
    occurrencesFor path lines line col
    |> Option.map (List.map (fun (l, c, len, site) -> l, c, len, (if site then 3 else 2)))

// ---- rename [D:lsp-rename]: complete or refused. A rename that misses
// one use is the failure that must not happen, so every doubt refuses
// with a reason the editor shows under the rename box: the cursor is
// on no value binder of this file; a statement in the name's scope
// failed to check (its uses are invisible); the new name is not a
// binder name; the new name is already visible in the scope (the
// conservative collision rule — a refusal names its site, a capture
// would be silent); an init file (its directives bind loader-owned
// names) or a sig contract file (its members are read by name).

let private isSigFile (path: string) =
    path.Replace('\\', '/').Contains "/.weir/sigs/"

// the parser's identifier rule (rawWord): a letter or `_`, then
// letters, digits or `_`
let private isIdentName (n: string) =
    n.Length > 0
    && (Char.IsLetter n[0] || n[0] = '_')
    && (n |> Seq.forall (fun c -> Char.IsLetter c || (c >= '0' && c <= '9') || c = '_'))

/// the renamable name at the cursor: the file's table, the occurrence
/// under the cursor, and the scope extent as statement indices (first,
/// last inclusive) — or the refusal. A value binder's occurrence wins
/// the position; a member's answers when no value does
/// [D:lsp-rename-members]
let private renameTarget
    (path: string)
    (lines: string list)
    (line: int)
    (col: int)
    : Result<FileOcc list * FileOcc * (int * int), string> =
    let diags, stmts0, _, _, partials = analyzeCached path lines
    let stmts = withPartials stmts0 partials
    let values = valueOccurrences stmts
    let members, rowSites = memberOccurrences stmts
    let table = values @ members
    let arr = List.toArray stmts

    let under (o: FileOcc) =
        o.OLine = line && o.OCol <= col && col < o.OCol + o.OName.Length

    let errorFrom (firstLine: int) (lastLine: int) =
        diags
        |> List.tryFind (fun d ->
            d.Severity = "error"
            && d.File = path
            && d.Line >= firstLine
            && d.Line <= lastLine)

    if isSigFile path then
        Error "rename is not available in a signature file — its members are the contract a #sig reads by name"
    elif isReplInitPath path || Script.isInitMarked lines then
        Error "rename is not available in an init file — its #session and #alias lines bind names the loader owns"
    else
        match values |> List.tryFind under, members |> List.tryFind under with
        | None, None ->
            let w =
                if line >= 1 && line <= List.length lines then
                    wordAt (List.item (line - 1) lines) col
                else
                    None

            match w with
            | Some "_" -> Error "'_' is the wildcard, not a name"
            | Some w when Parser.isKeyword w -> Error $"'{w}' is a keyword, not a name"
            | _ ->
                Error
                    "nothing to rename here — put the cursor on a value, a type, a field or a case declared in this file"
        | Some o, _ ->
            match o.Key with
            | TopKey(n, -1) ->
                Error $"'{n}' is not bound in this file — a builtin or an imported name renames where it is defined"
            | key ->
                // the scope extent: a local's statement; a top-level's
                // statement through the one that rebinds the name (whose
                // right-hand side still reads this binder)
                // (first, last): the statements whose names the new name
                // must not clash with; `next` the first statement past them
                let first, last, next =
                    match key with
                    | TopKey(n, j) ->
                        let rebinding =
                            table
                            |> List.filter (fun x -> x.OBinderSite && x.OName = n && x.Key <> key && x.OStmt > j)
                            |> List.map (fun x -> x.OStmt)
                            |> List.sort
                            |> List.tryHead

                        j, (rebinding |> Option.defaultValue (arr.Length - 1)), rebinding
                    | _ -> o.OStmt, o.OStmt, Some(o.OStmt + 1)

                // physical lines of the extent: the binder's statement head
                // through the line before `next` (a statement that failed
                // to check owns no segment — the lines between cover it)
                let firstLine = (fst arr[first]).Head

                let lastLine =
                    match next with
                    | Some k when k < arr.Length -> (fst arr[k]).Head - 1
                    | _ -> List.length lines

                match errorFrom firstLine lastLine with
                | Some d ->
                    Error
                        $"a statement in '{o.OName}'s scope has an error at line {d.Line} — fix it first, so no use of the name is missed"
                | None -> Ok(table, o, (first, last))
        | None, Some o ->
            let key, member_ =
                match o.Key with
                | MemberKey(k, m) -> k, m
                | _ -> "", None

            let env = (snd arr[arr.Length - 1]).Env

            let kindWord =
                match Types.typeDefFor env key with
                | Some(Record _) -> "field"
                | Some(Union _) -> "case"
                | None -> "member"

            let declIdx =
                arr
                |> Array.tryFindIndex (fun (_, c) ->
                    match c.Kind with
                    | Script.KType(d, _) -> d.Name = key
                    | _ -> false)

            match declIdx with
            | Some first ->
                // members are file-wide: the declaration through the end
                match errorFrom (fst arr[first]).Head (List.length lines) with
                | Some d -> Error $"a statement after '{key}'s declaration has an error at line {d.Line} — fix it first"
                | None ->
                    // a row-typed access of the field's name could serve
                    // this record or any other — refused, naming the site (R1)
                    let rowSite =
                        match member_ with
                        | Some f when kindWord = "field" ->
                            rowSites
                            |> List.filter (fun (n, _, _, _) -> n = f)
                            |> List.sortBy (fun (_, l, c, _) -> l, c)
                            |> List.tryHead
                        | _ -> None

                    match rowSite with
                    | Some(f, l, _, display) ->
                        Error
                            $"`{display}` at line {l} is row-typed — it could serve `{key}` or any record with a field `{f}`; this rename cannot tell which, so it renames nothing. Annotate the call or rename by hand."
                    | None -> Ok(table, o, (first, arr.Length - 1))
            | None ->
                let plain = plainName key

                let importAlias =
                    match key.Split '.' with
                    | [| alias; _ |] -> Some alias
                    | _ ->
                        stmts
                        |> List.tryPick (fun (_, c) ->
                            match c.Kind with
                            | Script.KImport lm when List.contains key lm.TypeNames -> Some lm.Alias
                            | _ -> None)

                match member_, importAlias with
                | Some n, _ when builtinTypeNames.ContainsKey key ->
                    Error $"'{n}' is a {kindWord} of the builtin {key} — nothing in this file declares it"
                | None, _ when builtinTypeNames.ContainsKey key ->
                    Error $"'{key}' is a builtin type — nothing in this file declares it"
                | Some n, Some alias -> Error $"'{n}' is a {kindWord} of {alias}.{plain} — rename it in that module"
                | None, Some alias -> Error $"'{plain}' is a type of {alias} — rename it in that module"
                | _ ->
                    Error
                        "nothing to rename here — put the cursor on a value, a type, a field or a case declared in this file"

/// every rename site in the file as (physLine, physCol, isValueBinder):
/// value binder sites and member declaration words — the round-trip
/// property's walk (rename each, re-analyze, compare)
let binderSitesFor (path: string) (lines: string list) : (int * int * bool) list =
    let _, stmts, _, _, _ = analyzeCached path lines

    fileOccurrences stmts
    |> List.filter (fun o -> o.OBinderSite)
    |> List.map (fun o ->
        o.OLine,
        o.OCol,
        (match o.Key with
         | MemberKey _ -> false
         | _ -> true))
    |> List.distinct
    |> List.sort

/// textDocument/prepareRename: the occurrence's (physLine, physCol, len)
/// — the name is the placeholder — or the refusal. Pure; the handler and
/// the unit pins share it.
let prepareRenameFor (path: string) (lines: string list) (line: int) (col: int) : Result<int * int * int, string> =
    renameTarget path lines line col
    |> Result.map (fun (_, o, _) -> o.OLine, o.OCol, o.OName.Length)

// the new name of a type, a field or a case [D:lsp-rename-members]: a
// field takes either case but no name or wire key of its record
// ([D:wire-keys] refuses two fields on one key); a case is uppercase and
// no case of any visible union ([D:ambiguous-ctor] would refuse its
// bare use); a type is uppercase and no visible type or module; none
// may be a same-kind member word anywhere in the extent
let private memberNameCheck
    (stmts: (Script.LogicalLine * Script.CheckedStatement) list)
    (table: FileOcc list)
    (key: string)
    (member_: string option)
    (first: int)
    (newName: string)
    : Result<unit, string> =
    let arr = List.toArray stmts
    let env = (snd arr[arr.Length - 1]).Env
    let def = Types.typeDefFor env key

    let kindOf (k: string) =
        match Types.typeDefFor env k with
        | Some(Record _) -> "field"
        | Some(Union _) -> "case"
        | None -> "member"

    let kind =
        match member_, def with
        | None, _ -> "type"
        | Some _, _ -> kindOf key

    let imports =
        stmts
        |> List.choose (fun (_, c) ->
            match c.Kind with
            | Script.KImport lm -> Some lm.Alias
            | _ -> None)

    // the same-kind member word in the extent
    let used =
        table
        |> List.filter (fun x ->
            x.OName = newName
            && x.OStmt >= first
            && (match x.Key with
                | MemberKey(k, Some _) -> kind <> "type" && kindOf k = kind
                | MemberKey(_, None) -> kind = "type"
                | _ -> false))
        |> List.sortBy (fun x -> x.OLine, x.OCol)
        |> List.tryHead

    let upper = Char.IsUpper newName[0]

    match kind, def with
    | "field", Some(Record d) ->
        let wireOwner =
            d.Attrs
            |> Map.tryPick (fun f attrs ->
                if Some f = member_ then
                    None
                else
                    attrs
                    |> List.tryPick (fun (a, arg) ->
                        match a, arg with
                        | "Wire", Some(AStr w) when w = newName -> Some f
                        | _ -> None))

        if d.Fields |> List.exists (fst >> (=) newName) then
            Error $"'{newName}' is already a field of {key}"
        else
            match wireOwner with
            | Some f -> Error $"'{newName}' is already a wire key of {key} (the field '{f}')"
            | None ->
                match used with
                | Some c ->
                    Error
                        $"'{newName}' is already a field name at line {c.OLine}, column {c.OCol} — pick a name no field in this file uses"
                | None -> Ok()
    | "case", _ ->
        if not upper then
            Error $"'{newName}' is not a case name — case names start uppercase"
        else
            match unionOf env newName with
            | Some u -> Error $"'{newName}' is already a case of {u} — a bare '{newName}' could name either"
            | None ->
                match used with
                | Some c ->
                    Error
                        $"'{newName}' is already a case name at line {c.OLine}, column {c.OCol} — pick a name no case in this file uses"
                | None -> Ok()
    | "type", _ ->
        if not upper then
            Error $"'{newName}' is not a type name — type names start uppercase"
        elif builtinTypeNames.ContainsKey newName then
            Error $"'{newName}' is a builtin type — pick another name"
        elif Map.containsKey newName env.Types then
            Error $"'{newName}' is already a type visible here — pick another name"
        elif Map.containsKey newName env.Modules || List.contains newName imports then
            Error $"'{newName}' is a module name — pick another name"
        else
            match used with
            | Some c ->
                Error $"'{newName}' is already a type name at line {c.OLine}, column {c.OCol} — pick another name"
            | None -> Ok()
    | _ -> Error "nothing to rename here — put the cursor on a value, a type, a field or a case declared in this file"

/// textDocument/rename: every edit as (physLine, physCol, len), sorted,
/// or the refusal. Re-validates the position (a client may skip
/// prepareRename).
let private renameEditsFor
    (path: string)
    (lines: string list)
    (line: int)
    (col: int)
    (newName: string)
    : Result<(int * int * int) list, string> =
    renameTarget path lines line col
    |> Result.bind (fun (table, o, (first, last)) ->
        let _, stmts0, env0, _, partials = analyzeCached path lines
        let stmts = withPartials stmts0 partials
        let arr = List.toArray stmts
        let n = o.OName

        let envBefore = if first = 0 then env0 else (snd arr[first - 1]).Env

        let edits () =
            table
            |> List.filter (fun x -> x.Key = o.Key)
            |> List.map (fun x -> x.OLine, x.OCol, x.OName.Length)
            |> List.distinct
            |> List.sort

        let collision =
            table
            |> List.filter (fun x -> x.OName = newName && x.OStmt >= first && x.OStmt <= last)
            |> List.sortBy (fun x -> x.OLine, x.OCol)
            |> List.tryHead

        if newName = n then
            Error $"'{n}' is already the name"
        elif newName = "_" then
            Error "'_' is the wildcard, not a name"
        elif not (isIdentName newName) then
            Error
                $"'{newName}' is not a name — a name starts with a letter or '_' and continues with letters, digits or '_'"
        elif Parser.isKeyword newName then
            Error $"'{newName}' is a keyword, not a name"
        else
            match o.Key with
            | MemberKey(key, member_) -> memberNameCheck stmts table key member_ first newName |> Result.map edits
            | _ ->
                let noSpan = {
                    Start = { Line = 1; Col = 1 }
                    End = { Line = 1; Col = 1 }
                }

                match Check.checkBinderName noSpan newName with
                | Error e -> Error e.Message
                | Ok() when not (Check.occurrenceName newName) ->
                    // the collision rule reads the occurrence table, and the table
                    // tracks no `_`-led name: a rename onto one could capture a
                    // binding silently, so it is refused [D:lsp-rename]
                    Error
                        $"'{newName}' starts with '_', and weir tracks no occurrence of a '_'-led name — a rename onto one cannot be checked for a clash; pick a name without the leading '_'"
                | Ok() ->
                    match collision with
                    | Some c ->
                        Error
                            $"'{newName}' is already used at line {c.OLine}, column {c.OCol} — pick a name that is new in '{n}'s scope"
                    | None when Map.containsKey newName envBefore.Values ->
                        Error
                            $"'{newName}' is already bound here (a builtin, or a binding above line {(fst arr[first]).Head}) — pick a name that is new in '{n}'s scope"
                    | None -> Ok(edits ()))

/// a rename never breaks the file [D:lsp-rename]: the edits are applied
/// and the result re-checked; a new error refuses. A length-changing
/// rename inside a column-aligned layout (`let pt = { x = 1` over
/// `           y = 2 }`) is refused, not re-aligned — a rename edits
/// names, never other lines' indentation [D:lsp-rename-aligned]
let renameFor
    (path: string)
    (lines: string list)
    (line: int)
    (col: int)
    (newName: string)
    : Result<(int * int * int) list, string> =
    renameEditsFor path lines line col newName
    |> Result.bind (fun edits ->
        let arr = List.toArray lines

        let oldName =
            match edits with
            | (l, c, len) :: _ when l >= 1 && l <= arr.Length && c - 1 + len <= arr[l - 1].Length ->
                arr[l - 1].Substring(c - 1, len)
            | _ -> ""

        for l, c, len in edits |> List.sortByDescending (fun (l, c, _) -> l, c) do
            if l >= 1 && l <= arr.Length && c >= 1 && c - 1 + len <= arr[l - 1].Length then
                arr[l - 1] <- arr[l - 1].Substring(0, c - 1) + newName + arr[l - 1].Substring(c - 1 + len)

        let errorsOf (ls: string list) =
            let diags, _, _, _, _ = analyzeCached path ls

            diags
            |> List.filter (fun d -> d.Severity = "error" && d.File = path)
            |> List.countBy (fun d -> d.Code)
            |> Map.ofList

        let before = errorsOf lines
        let afterDiags, _, _, _, _ = analyzeCached path (List.ofArray arr)

        let fresh =
            afterDiags
            |> List.filter (fun d -> d.Severity = "error" && d.File = path)
            |> List.countBy (fun d -> d.Code)
            |> List.tryPick (fun (code, n) ->
                if n > defaultArg (Map.tryFind code before) 0 then
                    afterDiags
                    |> List.tryFind (fun d -> d.Severity = "error" && d.File = path && d.Code = code)
                else
                    None)

        match fresh with
        | None -> Ok edits
        | Some d when d.Code = "layout" ->
            Error
                $"renaming '{oldName}' to '{newName}' would break the column alignment at line {d.Line} — a rename does not re-align other lines; pick a name of the same length, or rename by hand and re-align"
        | Some d -> Error $"renaming '{oldName}' to '{newName}' would leave an error at line {d.Line}: {d.Message}")

// the within form answers [D:within-kinds] — one table, three
// consumers. The kind hovers its doc + binds/consumes nature; the
// `within` keyword itself answers (a form that carries weir's novelty;
// ordinary keywords keep the silence guard); a binding kind's binder
// is always the resource's type — a use-less binder must not fall
// through to the enclosing node's type (the same wrong-answer class).
let private withinFormHover (text: string) (jcol: int) : string option =
    let endsWithWord (kw: string) (s: string) =
        s.EndsWith kw
        && (s.Length = kw.Length || not (isWord s[s.Length - kw.Length - 1]))

    wordAt text jcol
    |> Option.bind (fun w ->
        let wordStart =
            let mutable st = jcol - 1

            while st > 0 && isWord text[st - 1] do
                st <- st - 1

            st

        let before = text.Substring(0, wordStart).TrimEnd()

        if w = "within" then
            let kinds =
                Ast.withinKinds
                // standalone heads (pure) are not within kinds [D:pure-stage1]
                |> List.filter (fun k -> not k.Standalone)
                |> List.map (fun k -> k.Name + (if k.Binds then " (binds)" else ""))
                |> String.concat ", "

            Some(
                "within <kind> — a scoped block: the resource holds while the block runs and reverts when it exits\nkinds: "
                + kinds
            )
        else
            match Ast.withinKinds |> List.tryFind (fun k -> k.Name = w && not k.Standalone) with
            | Some wk when endsWithWord "within" before ->
                let nature = if wk.Binds then "binds" else "consumes"
                Some $"{wk.Name} — {nature}: {wk.Doc}"
            | _ ->
                Ast.withinKinds
                |> List.tryPick (fun k ->
                    if k.Binds && endsWithWord k.Name before then
                        let b2 = before.Substring(0, before.Length - k.Name.Length).TrimEnd()
                        if endsWithWord "within" b2 then Some "string" else None
                    else
                        None))

// the from/to form answers with the discovery surface [D:form-word-hover]
// — the adapter list, which nothing else in the editor provides. The
// adapters' own words already hover (their builtinDocs entry); this fills
// only the bare keyword, and the list is derived from that same source so
// the two cannot drift. Direction-aware: `to` omits the read-only ones.
// The form-word hover rule: a keyword that names a form answers; a
// punctuation-in-word-form keyword keeps the silence guard [D:form-word-hover].
let private adapterFormHover (text: string) (jcol: int) : string option =
    wordAt text jcol
    |> Option.bind (fun w ->
        match w with
        | "from" ->
            Some(
                "from <adapter> [T] — parse a text line stream into typed values. Adapters: "
                + (Builtins.adapterNames "from" |> String.concat ", ")
            )
        | "to" ->
            Some(
                "to <adapter> — render typed values as a text line stream. Adapters: "
                + (Builtins.adapterNames "to" |> String.concat ", ")
            )
        | _ -> None)

// retry/poll/until answer as forms [D:form-word-hover] — the stated
// rule's remaining customers. The key lists derive from the prelude
// options records (env.Types Retry/Poll — the same shapes the
// key=value head desugars over), so a new key appears here with no
// second edit; a hand-written list would drift.
let private retryPollFormHover (env: TypeEnv) (text: string) (jcol: int) : string option =
    let keysOf tyName =
        match Map.tryFind tyName env.Types with
        | Some(Record d) ->
            d.Fields
            |> List.map (fun (fn, t) -> $"{fn}: {formatTy t}")
            |> String.concat ", "
        | _ -> ""

    wordAt text jcol
    |> Option.bind (fun w ->
        match w with
        | "retry" ->
            Some(
                "retry key=value … — a bounded retry loop: the body reruns until it yields true (a VALUE body adds an `until` segment); exhaustion raises\nkeys: "
                + keysOf "Retry"
            )
        | "poll" ->
            Some(
                "poll key=value … — retry's wall-clock twin: rerun until true or the timeout elapses; timing out raises\nkeys: "
                + keysOf "Poll"
            )
        | "repeat" ->
            Some(
                "repeat interval=… — the loop a stop signal ends: run the block, wait the interval, run it again; the first SIGINT/SIGTERM/SIGHUP lets the iteration in hand finish and returns (a second one tears down). An `until` segment or a bool block ends it too; a timeout raises\nkeys: "
                + keysOf "Repeat"
            )
        | "until" ->
            Some
                "until <name> — the predicate segment of a retry/poll/repeat VALUE body: names the body's binding and decides when the loop stops. After a within's block, `until stopped` holds the scope until the first stop signal, then it closes normally"
        | _ -> None)

/// a form-word hover: within/from/to/retry/poll/until and their
/// form-words — the union of the form hovers [D:form-word-hover]. Gated
/// by the one caller to code position (the same letters inside a string
/// or comment are data) — a new form hover joins this union and the
/// gate covers it; never a second path.
// `function` answers as a form [D:function-keyword]: the implicit-match
// lambda — the meaning plus the pointer to match, the fourth form word
// under the stated rule
let private functionFormHover (text: string) (jcol: int) : string option =
    wordAt text jcol
    |> Option.bind (fun w ->
        match w with
        | "function" ->
            Some
                "function | <pattern> -> <expr> | … — a one-parameter fun whose body matches that parameter (fun x -> match x with …); arms take guards exactly as match does"
        | _ -> None)

// the attribute names answer inside their brackets [D:lsp-typename]:
// [<Tag>]/[<Other>]/[<Short>]… are no expression nodes, so the word
// falls to a registry-doc lookup, gated to a position between `[<`
// and `>]` (the same letters as a binder stay silent)
let private attrHover (text: string) (jcol: int) : string option =
    wordAt text jcol
    |> Option.bind (fun w -> Map.tryFind w Builtins.attrDocs)
    |> Option.filter (fun _ ->
        let upto = text.Substring(0, min (max (jcol - 1) 0) text.Length)
        upto.LastIndexOf "[<" > upto.LastIndexOf ">]")

// the stream cardinality word answers as a form [D:form-word-hover] —
// only beside its adapter (a binding named stream stays a binding)
let private streamFormHover (text: string) (jcol: int) : string option =
    wordAt text jcol
    |> Option.bind (fun w ->
        if w <> "stream" then
            None
        else
            let wordStart =
                let mutable st = jcol - 1

                while st > 0 && isWord text[st - 1] do
                    st <- st - 1

                st

            let before = text.Substring(0, wordStart).TrimEnd()

            if before.EndsWith "from yaml" || before.EndsWith "to yaml" then
                Some
                    "stream — the cardinality word: `from yaml stream T` reads a '---' stream, one document per element -> seq<T>; `to yaml stream` writes one document per element"
            else
                None)

// `pfor` answers as a form [D:pfor]: the parallel `for` — the meaning
// plus what it inherits from Seq.piter and where the ceiling knob lives
let private pforFormHover (text: string) (jcol: int) : string option =
    wordAt text jcol
    |> Option.bind (fun w ->
        match w with
        | "pfor" ->
            Some
                "pfor <pattern> in <seq> do <body> — the parallel for: exactly `xs |> Seq.piter (fun x -> body)`. The body must be unit and arms finish in any order (their output interleaves); every arm runs even if one fails, then the first error by input order raises. The default ceiling is 64 arms (8 when nested in a fan-out, 1 deeper); `Seq.piterWith n` sets one. Effects only — collect results with `Seq.pmap`"
        | _ -> None)

let private formWordHover (env: TypeEnv) (text: string) (jcol: int) : string option =
    withinFormHover text jcol
    |> Option.orElse (pforFormHover text jcol)
    |> Option.orElse (adapterFormHover text jcol)
    |> Option.orElse (retryPollFormHover env text jcol)
    |> Option.orElse (functionFormHover text jcol)
    |> Option.orElse (streamFormHover text jcol)
    |> Option.orElse (attrHover text jcol)

/// is the (1-based) physical column inside a string literal or a trailing
/// comment on this physical line? [D:within-kinds] The form hovers run
/// before the silence guard (they answer for keywords it would silence),
/// so their string/comment exclusion lives here — on the physical line,
/// never the joined logical text whose sentinels confuse the scanner.
let private inStringOrComment (lines: string list) (line: int) (col: int) : bool =
    if line < 1 || line > List.length lines then
        false
    else
        let text = List.item (line - 1) lines

        (col >= 1 && col <= text.Length && (Script.inStringMask text)[col - 1])
        || col - 1 >= (Script.stripComment text).Length

/// the type argument's own hover: `Config` in `from json Config` (and
/// from yaml / Env.load / Args.load) is no expression node — the bespoke
/// arm absorbs it — so the enclosing adapter's arrow type would answer
/// [D:form-word-hover]. Rendered byte-equal to declHover's shape at the
/// declaration, so the two positions cannot drift.
let private typeDefHover (env: TypeEnv) (tyName: string) : string option =
    Map.tryFind tyName env.Types
    |> Option.map (fun def ->
        match def with
        | Record d ->
            let body =
                d.Fields
                |> List.map (fun (fn, t) -> $"{fn}: {formatTy t}")
                |> String.concat "; "

            $"{{ {body} }}"
        | Union d ->
            d.Cases
            |> List.map (fun (cn, tyO) ->
                match tyO with
                | Some t -> $"{cn} of {formatTy t}"
                | None -> cn)
            |> String.concat " | "
            // a validated type says so [D:validated-types]
            |> fun shape ->
                if d.Validated.IsSome then
                    shape + "  (validated)"
                else
                    shape)

/// the first line of a validated type's `validate` doc
/// [D:validated-types], read in the file that declares it: `tyName` is
/// the declared name among that file's statements
let private validateDocIn
    (lines: string list)
    (stmts: (Script.LogicalLine * Script.CheckedStatement) list)
    (tyName: string)
    : string option =
    stmts
    |> List.tryPick (fun (ll, chk) ->
        match chk.Kind with
        | Script.KType(d, Some _) when d.Name = tyName ->
            d.Validate
            |> Option.bind (fun v ->
                let pl, pc = Script.translate ll v.Span.Start.Col

                Script.docAttachments lines
                |> List.tryPick (fun a ->
                    if a.Line = pl && a.Col = pc then
                        List.tryHead a.Doc
                    else
                        None))
        | _ -> None)

// the schema= name's hover [D:schema-hover]: file facts, not type facts
// — the resolved vendored path, the lock's provenance, and whether the
// schema can catch unknown fields (the line validation working depends
// on). Sources: the lockfile and the vendored file, read per hover — no
// cache, the stateless discipline. A miss or an unusable file renders
// the checker's words for that state, never a second phrasing.
let private schemaHover (path: string) (name: string) : string option =
    match Script.resolveSchemaFile path name with
    | Error e -> Some e
    | Ok(weirDir, file) ->
        match
            (try
                Ok(IO.File.ReadAllText file)
             with ex ->
                 Error $"schema '{name}': cannot read {file} — {ex.Message}")
        with
        | Error e -> Some e
        | Ok txt ->
            match Contracts.parseSchema name txt with
            | Error e -> Some e
            | Ok _ ->
                use doc = Text.Json.JsonDocument.Parse txt

                let strictness =
                    if Contracts.anyClosedProps doc.RootElement then
                        "strict (unknown fields are caught)"
                    else
                        "permissive: no `additionalProperties: false` anywhere, so unknown-field checking will NOT fire"

                let source =
                    match Contracts.readLock weirDir with
                    | Ok entries ->
                        entries
                        |> List.tryPick (fun e ->
                            if e.Kind = "schema" && e.Name = name then
                                Some $"source: {Weir.Types.quoteData (Http.redactUrl e.Url)}"
                            else
                                None)
                    | Error _ -> None
                    |> Option.defaultValue "source: not in the lock (hand-placed)"

                let ann (key: string) =
                    if doc.RootElement.ValueKind = Text.Json.JsonValueKind.Object then
                        match doc.RootElement.TryGetProperty key with
                        | true, v when v.ValueKind = Text.Json.JsonValueKind.String -> Some(v.GetString())
                        | _ -> None
                    else
                        None

                let what =
                    match ann "title", ann "description" with
                    | Some t, Some d -> [ $"{t} — {d}" ]
                    | Some t, None -> [ t ]
                    | None, Some d -> [ d ]
                    | None, None -> []

                Some(
                    $"schema={name} — {strictness}\n\n"
                    + String.concat "\n" ([ file; source ] @ what)
                )

/// hover text at (1-based physical line, col), or None. Pure. Type first,
/// then the `///` doc. Silence guard first [D:hover-silence]; then the
/// type from the binder/param/typed-node/scheme, with a field in a literal
/// resolved to the field's type (not the record's) [Group 1c]; then the
/// doc — at the cursor for a declaration, or at the resolved declaration
/// site for a usage / field / case reference (definitionFor) [Group 1a],
/// else the builtin's [D:builtin-docs].
let hoverAt (path: string) (lines: string list) (line: int) (col: int) : string option =
    let _, stmts, _, _, partials = analyzeCached path lines

    match toLogical (withPartials stmts partials) line col with
    // a hole has no type to show [D:block-recovery] — null, the honest
    // answer [D:hover-silence]
    | Some(_, chk, jcol) when inHole chk jcol -> None
    | Some(ll, chk, jcol) when
        not (inStringOrComment lines line col)
        && (formWordHover chk.Env ll.Text jcol).IsSome
        ->
        formWordHover chk.Env ll.Text jcol
    | Some(ll, chk, jcol) when
        not (inStringOrComment lines line col)
        && (schemaTokenAt chk ll.Text jcol).IsSome
        ->
        // the schema= name: file facts off the checker's resolution
        // [D:schema-hover] — never the district's own type (an
        // enclosing-node leak otherwise)
        schemaTokenAt chk ll.Text jcol |> Option.bind (schemaHover path)
    | Some(ll, chk, jcol) when not (onSilentToken ll.Text jcol) ->
        // an inner-let binder hovers as its annotated signature (names +
        // types), degrading to the arrow when it has no named params
        let binderSig =
            letBinderAt ll.Text jcol
            |> Option.bind (fun name -> teOf chk |> Option.bind (innerLetSig name jcol))

        // a top-level pattern binder (`let key, title = …`) hovers its
        // own scheme [D:pat-binder-hover]: KLetPat carries name→scheme
        // pairs, so the lookup is by word — gated left of the `=` so an
        // RHS use of the same name keeps its expression hover
        let patBinderTy =
            match chk.Kind with
            | Script.KLetPat(_, schemes, _) ->
                wordAt ll.Text jcol
                |> Option.bind (fun w ->
                    let eq = ll.Text.IndexOf '='

                    if eq > 0 && jcol - 1 < eq then
                        schemes
                        |> List.tryPick (fun (n, sc) -> if n = w then Some(formatTy sc.Ty) else None)
                    else
                        None)
            | _ -> None

        let paramTy = teOf chk |> Option.bind (paramTypeAt jcol)
        let node = teOf chk |> Option.bind (fun te -> nodeAt te jcol)

        // Group 1c: a field name in a record literal `{ Field = … }` hovers
        // as the field's type, not the record's (nodeAt sees only TERecord)
        let fieldInLiteral =
            match node with
            | Some {
                       Kind = Check.TERecord(recName, _)
                       Ty = recTy
                   } ->
                let key =
                    match recTy with
                    | TNamed(k, _) -> k
                    | _ -> recName

                wordAt ll.Text jcol
                |> Option.bind (fun w ->
                    match Map.tryFind key chk.Env.Types with
                    | Some(Record def) ->
                        def.Fields
                        |> List.tryPick (fun (f, fty) -> if f = w then Some(formatTy fty) else None)
                    | _ -> None)
            | _ -> None

        // Group 2: a union case — in a pattern (`| Pulled n ->`) or as a
        // value (`Pulled ctx`) — hovers as its constructor signature. Keyed
        // off the word (pattern positions are no expression node); skipped
        // at the type declaration, where declHover renders it instead.
        let constructorSig =
            match chk.Kind with
            | Script.KType _ -> None
            | _ ->
                wordAt ll.Text jcol
                |> Option.bind (fun w ->
                    chk.Env.Types
                    |> Map.tryPick (fun tn def ->
                        match def with
                        | Union d ->
                            d.Cases
                            |> List.tryPick (fun (c, payload) ->
                                if c = w then
                                    match payload with
                                    | Some pty -> Some $"{c} : {formatTy pty} -> {tn}"
                                    | None -> Some $"{c} : {tn}"
                                else
                                    None)
                        | _ -> None))

        let word = wordAt ll.Text jcol

        // `T.create` [D:validated-types]: `create` hovers the derived
        // function, the type word its type
        let createKey =
            node
            |> Option.bind (fun n ->
                match n.Kind, n.Ty with
                | Check.TEVar vn, TFun(_, TNamed(key, [])) when vn.StartsWith Types.createVarPrefix -> Some(key, n.Ty)
                | _ -> None)

        let createSig =
            createKey
            |> Option.bind (fun (key, fty) ->
                if word = Some "create" then
                    Some
                        $"{key}.create : {formatTy fty}\n\nRaises validate's message when it refuses; `|> try result` for a Result."
                else
                    typeDefHover chk.Env key)

        // the validated type a hover is about, for its `validate` doc:
        // `T.create`, the declaration's name, or a reference to the type
        let validatedKey =
            match createKey, chk.Kind with
            | Some(key, _), _ -> Some key
            | None, Script.KType(d, Some _) when word = Some d.Name -> Some d.Name
            | None, _ ->
                word
                |> Option.filter (fun w ->
                    match Map.tryFind w chk.Env.Types with
                    | Some(Union u) -> u.Validated.IsSome
                    | _ -> false)

        let validateDoc =
            validatedKey
            |> Option.bind (fun key ->
                match Map.tryFind key chk.Env.TypeOrigins with
                | Some origin when origin.Contains '@' ->
                    // "Module.Name@absPath" — the declaring module's file
                    let at = origin.IndexOf '@'
                    let declared = origin.Substring(0, at)
                    let declared = declared.Substring(declared.IndexOf '.' + 1)

                    targetStmts (origin.Substring(at + 1))
                    |> Option.bind (fun (mlines, mstmts) -> validateDocIn mlines mstmts declared)
                | _ -> validateDocIn lines stmts key)
            |> Option.map (fun d -> $"validate — {d}")

        // an exact use of a name (TEVar at the cursor) wins; else a pattern
        // payload binder resolved from a use of it [Group 2]; else the
        // enclosing node's type; else the binder-name scheme / declaration.
        let exactUse =
            match node with
            | Some({ Kind = Check.TEVar vn } as n) when Some vn = word -> Some(formatTy n.Ty)
            | _ -> None

        // a builtin hovers as its annotated signature (names from the doc,
        // types from the node) [D:annotated-signature]. A zero-param value
        // (`Self.pid : int`) renders name-and-type with no parens; a function
        // with no named params degrades to the arrow (fallback below).
        let builtinSig =
            node
            |> Option.bind (fun n ->
                match n.Kind with
                | Check.TEVar name when Map.containsKey name Builtins.builtinDocs.Value ->
                    let d = Builtins.builtinDocs.Value[name]

                    match n.Ty with
                    | TFun _ when List.isEmpty d.Params -> None // unnamed function -> arrow
                    | _ -> Some(formatSignature name d.Params n.Ty)
                | _ -> None)

        // a module member at a use site hovers as its annotated signature,
        // the builtin rendering shared [D:lsp-cross-file]: params read off
        // the module's typed body; a function with no named params
        // degrades to the arrow, a value renders name-and-type
        let moduleMemberSig =
            node
            |> Option.bind (fun nd ->
                match nd.Kind with
                | Check.TEVar name when name.Contains '.' && not (Map.containsKey name Builtins.builtinDocs.Value) ->
                    (match name.Split '.' with
                     | [| alias; mem |] ->
                         stmts
                         |> List.tryPick (fun (_, c) ->
                             match c.Kind with
                             | Script.KImport lm when lm.Alias = alias -> Some lm
                             | _ -> None)
                         |> Option.bind (fun lm ->
                             lm.Body
                             |> List.tryPick (function
                                 | _, Script.CLet(bn, te) when bn = mem -> Some te
                                 | _ -> None))
                         |> Option.bind (fun te ->
                             match te.Ty, lambdaParamNames te with
                             | TFun _, [] -> None // unnamed function -> arrow
                             | mty, ps -> Some(sigOrFlat name ps mty))
                     | _ -> None)
                | _ -> None)

        // ---- signed commands [D:lsp-cross-file]: the head hovers its
        // identity off the sig file alone (the version is the recorded
        // one — no spawn, so it works with the tool off PATH); a flag
        // hovers its field's type, and the field's /// doc rides below
        let sigSurface =
            teOf chk
            |> Option.bind (cmdSurfaceAt jcol)
            |> Option.bind (fun (prog, progCol, words) ->
                sigInfosCached path lines
                |> List.tryFind (fun si -> si.Tool = prog)
                |> Option.map (fun si -> si, prog, progCol, words))

        let sigHead =
            sigSurface
            |> Option.bind (fun (si, prog, progCol, _) ->
                if progCol <= jcol && jcol < progCol + prog.Length then
                    let status = if si.Exhaustive then "exhaustive" else "partial"

                    Some(
                        $"{prog} — signed command (#sig {prog}, line {si.DeclLine}; {status} signature)",
                        $"{si.SigPath}\nversion: {si.Version}"
                    )
                else
                    None)

        let sigFlag =
            sigSurface
            |> Option.bind (fun (si, _, _, words) ->
                words
                |> List.tryFind (fun (w, sp) -> w.StartsWith "-" && sp.Start.Col <= jcol && jcol < sp.End.Col)
                |> Option.bind (fun (w, _) -> sigFlagField si (List.map fst words) w)
                |> Option.map (fun (rn, f, fty, sigLines, sigStmts) ->
                    formatTy fty, sigFlagDoc si.SigPath rn f sigLines sigStmts))

        let ty =
            (sigHead |> Option.map fst)
            |> Option.orElse (sigFlag |> Option.map fst)
            |> Option.orElse createSig
            |> Option.orElse fieldInLiteral
            |> Option.orElse constructorSig
            |> Option.orElse binderSig
            |> Option.orElse patBinderTy
            |> Option.orElse builtinSig
            |> Option.orElse moduleMemberSig
            |> Option.orElseWith (fun () -> paramTy |> Option.map formatTy)
            |> Option.orElse exactUse
            |> Option.orElseWith (fun () ->
                word
                |> Option.bind (fun w -> teOf chk |> Option.bind (varUseType w))
                |> Option.map formatTy)
            // the type argument in a boundary form hovers its own shape,
            // not the enclosing arrow [D:form-word-hover] — placed just
            // before the node fallback so nothing that answered earlier
            // moves; only the leak case changes
            |> Option.orElseWith (fun () ->
                node
                |> Option.bind (fun nd ->
                    let named tyName =
                        if word = Some tyName then
                            typeDefHover chk.Env tyName
                        else
                            None

                    match nd.Kind with
                    | Check.TEFrom(_, Check.TopRec d, _, _, _, _) -> named d.Name
                    | Check.TEFrom(_, Check.TopUnion u, _, _, _, _) -> named u.Name
                    | Check.TEFromYaml(tyName, _, _)
                    | Check.TEFromToml(tyName, _) -> named tyName
                    | Check.TEEnvLoad(def, _) -> named def.Name
                    | Check.TEArgsLoad target ->
                        (match target with
                         | Check.ArgsRecord def -> named def.Name
                         | Check.ArgsUnion(udef, _) -> named udef.Name
                         | Check.ArgsShared(outer, _, _, _) -> named outer.Name)
                    | _ -> None))
            |> Option.orElseWith (fun () -> node |> Option.map (fun n -> formatTy n.Ty))
            |> Option.orElseWith (fun () ->
                match chk.Kind with
                // a top-level let hovers as its annotated signature, on the
                // binder name only — never a fallback for an unresolved spot
                // a partial's own binder is hole-bound [D:block-recovery]:
                // its type is not known until the statement checks
                | Script.KLet(name, sch, te) when
                    word = Some name
                    && (match sch.Ty with
                        | TVar v -> not (v.StartsWith "__hole")
                        | _ -> true)
                    ->
                    // the purity badge, display stage of [D:pure]: the
                    // rare pure function is surfaced; effectful stays
                    // the unbadged norm (weir is effect-normal), and a
                    // missing badge is conservative, never a lie
                    let badge =
                        if
                            Purity.tyHasFun sch.Ty
                            && (Can.pureTopBindings stmts |> Map.tryFind name |> Option.defaultValue false)
                        then
                            "  (pure)"
                        else
                            ""

                    Some(sigOrFlat name (lambdaParamNames te) sch.Ty + badge)
                // a member signature line hovers the sig verbatim
                // [D:module-signatures]
                | Script.KSig(name, sd) when word = Some name -> Some $"{name} : {formatTy sd.Ty}"
                | Script.KType(decl, _) -> word |> Option.bind (declHover decl)
                | _ -> None)
            // a referenced type name anywhere hovers its shape
            // [D:lsp-typename]: a field's type in a record decl, the
            // payload after `of` — no expression node covers these, so
            // the word resolves against env.Types (declHover already
            // answered the declaring field/case, so this only fills
            // references it did not claim)
            |> Option.orElseWith (fun () ->
                word
                |> Option.bind (fun w ->
                    if w.Length > 0 && Char.IsUpper w[0] && Types.isUserName w then
                        typeDefHover chk.Env w
                    else
                        None))

        // the boundary-form nodes span the whole form (`Env.load TokenEnv`),
        // so gate each to the word that names it — hovering the module `Env`
        // or the type argument must not surface `load`'s doc; only `load` does
        let nodeDoc =
            node
            |> Option.bind (fun n ->
                match n.Kind with
                | Check.TEVar name -> Some(Builtins.reifierSurface name |> Option.defaultValue name)
                | Check.TEEnvLoad _ when word = Some "load" -> Some "Env.load"
                | Check.TEArgsLoad _ when word = Some "load" -> Some "Args.load"
                | Check.TEFrom(fmt, _, _, _, _, _) when word = Some "from" || word = Some fmt -> Some $"from {fmt}"
                | Check.TEFromYaml _ when word = Some "from" || word = Some "yaml" -> Some "from yaml"
                | Check.TEFromToml _ when word = Some "from" || word = Some "toml" -> Some "from toml"
                | Check.TETry _ when word = Some "try" || word = Some "result" -> Some "try"
                | Check.TEOrFail _ when word = Some "orFail" -> Some "orFail"
                | Check.TETo(fmt, _, _, _, _, _) when word = Some "to" || word = Some fmt -> Some $"to {fmt}"
                | _ -> None)
            |> Option.bind (fun key -> Map.tryFind key Builtins.builtinDocs.Value)

        let wordDoc =
            wordAt ll.Text jcol
            |> Option.bind (fun w -> Map.tryFind w Builtins.builtinDocs.Value)

        let builtinDoc =
            nodeDoc |> Option.orElse wordDoc |> Option.map Builtins.renderBuiltinDoc

        // the `///` doc: at the cursor for a declaration site
        let sourceDoc =
            docAttachmentsCached path lines
            |> List.tryPick (fun d ->
                if d.Line = line && d.Col <= col && col < d.Col + d.Len then
                    Some(reflowDoc d.Doc)
                else
                    None)

        // Group 1a: a usage / field / case reference resolves to its
        // declaration site (definitionFor), and the doc is read there —
        // shadowing falls out (definitionFor is innermost-wins)
        let usageDoc =
            match sourceDoc with
            | Some _ -> None
            | None ->
                definitionTarget path lines line col
                |> Option.bind (fun (fileOpt, dl, dc, _) ->
                    (match fileOpt with
                     | None -> Some(path, lines)
                     | Some f -> Script.targetSourceLines f |> Option.map (fun ls -> f, ls))
                    |> Option.bind (fun (docPath, docSrc) ->
                        docAttachmentsCached docPath docSrc
                        |> List.tryPick (fun d ->
                            if d.Line = dl && d.Col = dc then
                                Some(reflowDoc d.Doc)
                            else
                                None)))

        let doc =
            (sigHead |> Option.map snd)
            |> Option.orElse (sigFlag |> Option.bind snd)
            |> Option.orElse sourceDoc
            |> Option.orElse usageDoc
            |> Option.orElse builtinDoc
            // a validated type's `validate` doc rides below [D:validated-types]
            |> fun d ->
                match d, validateDoc with
                | Some d, Some v -> Some(d + "\n\n" + v)
                | None, v -> v
                | d, None -> d

        // a hole-descended type (a use of a binder whose value failed)
        // hovers nothing [D:block-recovery]: the hole convention's var
        // would render, and no type is the honest answer
        let ty = ty |> Option.filter (fun t -> not (t.Contains "'__hole"))

        match ty, doc with
        | Some t, Some d -> Some(t + "\n\n" + d) // type FIRST, then the doc
        | Some t, None -> Some t
        | None, Some d -> Some d
        | None, None -> None
    | _ -> None

/// hoverAt with no file identity — the unit pins' single-file surface
/// (imports and signatures resolve relative to the real path, so the
/// handler serves hoverAt)
let hoverType (lines: string list) (line: int) (col: int) : string option = hoverAt "hover" lines line col

// ---- editor polish: the pure functions [PLAN-lsp-editor-polish] ----
// Each reads the one cached analysis and answers through the verbatim
// rule; the handlers in `run` only decode params and write JSON.

type InlayHintOptions = {
    BindingTypes: bool
    ParameterTypes: bool
    PipelineTypes: bool
    MaxLabel: int
}

/// the defaults [D:inlay-hints]: binder, parameter and return types on,
/// pipeline-stage types off, labels cut at 40 chars
let defaultInlayOptions = {
    BindingTypes = true
    ParameterTypes = true
    PipelineTypes = false
    MaxLabel = 40
}

type InlayHint = {
    Line: int // 1-based physical insertion point
    Col: int
    Label: string // ": seq<int>"
    Tooltip: string option // the full type when Label is truncated
    Kind: int // 1 = Type, 2 = Parameter
    PaddingLeft: bool
    PaddingRight: bool
}

/// the `weir.inlayHints` settings object → options; an absent or
/// malformed key keeps its default [D:inlay-hints]
let inlayOptionsOf (json: JsonElement) : InlayHintOptions =
    let flag (k: string) (d: bool) =
        match tryProp k json with
        | Some v when v.ValueKind = JsonValueKind.True -> true
        | Some v when v.ValueKind = JsonValueKind.False -> false
        | _ -> d

    {
        BindingTypes = flag "bindingTypes" defaultInlayOptions.BindingTypes
        ParameterTypes = flag "parameterTypes" defaultInlayOptions.ParameterTypes
        PipelineTypes = flag "pipelineTypes" defaultInlayOptions.PipelineTypes
        MaxLabel =
            jNum "maxLabelLength" json
            |> Option.filter (fun n -> n > 0)
            |> Option.defaultValue defaultInlayOptions.MaxLabel
    }

// a sentinel type variable (`__hole…`, `__…`) is the checker's, not the
// user's [D:user-language-messages] — nothing renders it
let private hasSentinel (ty: Ty) =
    Types.tyVars ty |> Set.exists (fun v -> v.StartsWith "__")

// the lambda chain a `let f a b = …` desugars to: the last param's span
// and the body's (return) type
let rec private lastParam (te: Check.TypedExpr) : (Span * Ty) option =
    match te.Kind with
    | Check.TELambda(_, pspan, body) ->
        (match body.Kind with
         | Check.TELambda _ -> lastParam body
         | _ -> Some(pspan, body.Ty))
    | _ -> None

/// inlay hints for the physical lines [fromLine, toLine] (1-based)
/// [D:inlay-hints]: a binder's type after the binder, a parameter's
/// after the parameter (Kind 2), a function's return type before its
/// `=`, each the type hover shows there — never on a signed member (the
/// signature is the annotation [D:module-signatures]), never on `()`,
/// `_` or a pattern binder, never off a sentinel type, and only where
/// the word sits verbatim at its physical home (verbatimAt). A failed
/// statement hints nothing; its neighbours still do.
let inlayHintsFor
    (opts: InlayHintOptions)
    (path: string)
    (lines: string list)
    (fromLine: int)
    (toLine: int)
    : InlayHint list =
    let _, stmts, _, _, _ = analyzeCached path lines
    let raw = List.toArray lines
    let out = ResizeArray<InlayHint>()

    let signed =
        stmts
        |> List.choose (fun (_, c) ->
            match c.Kind with
            | Script.KSig(n, _) -> Some n
            | _ -> None)
        |> Set.ofList

    // the viewport filter runs before any walk: a statement is visible
    // when its physical lines overlap the range
    let overlaps (ll: Script.LogicalLine) =
        let last = ll.Segments |> List.fold (fun m (_, pl, _) -> max m pl) ll.Head
        last >= fromLine && ll.Head <= toLine

    // R2: a long label is cut with a trailing `…`, the full type rides
    // in the tooltip; an untruncated label carries none
    let place (line: int) (col: int) (ty: Ty) (kind: int) (padLeft: bool) (padRight: bool) =
        if not (hasSentinel ty) then
            let full = formatTy ty

            let label, tip =
                if full.Length > opts.MaxLabel then
                    ": " + full.Substring(0, opts.MaxLabel - 1) + "…", Some full
                else
                    ": " + full, None

            out.Add {
                Line = line
                Col = col
                Label = label
                Tooltip = tip
                Kind = kind
                PaddingLeft = padLeft
                PaddingRight = padRight
            }

    // after the word at the logical column, when it is verbatim there
    let afterWord (ll: Script.LogicalLine) (jcol: int) (word: string) (ty: Ty) (kind: int) =
        if word <> "()" && word <> "_" then
            match verbatimAt raw ll jcol word.Length with
            | Some(l, c) -> place l (c + word.Length) ty kind false false
            | None -> ()

    // the return type sits before the `=` that follows the last param
    // (so it never collides with that param's own hint); `let f = fun…`
    // has no such `=` and gets no return hint
    let returnHint (ll: Script.LogicalLine) (te: Check.TypedExpr) (bodyStart: int) =
        if opts.BindingTypes then
            match lastParam te with
            | Some(pspan, ret) ->
                let eq = ll.Text.IndexOf('=', pspan.End.Col - 1)

                if eq >= 0 && eq + 1 < bodyStart then
                    match verbatimAt raw ll (eq + 1) 1 with
                    | Some(l, c) -> place l c ret 1 false true
                    | None -> ()
            | None -> ()

    let rec walk (ll: Script.LogicalLine) (te: Check.TypedExpr) =
        match te.Kind with
        | Check.TELet(name, nameSpan, value, body) ->
            (match value.Kind with
             // an inner function: its params and return ride the lambda
             // walk, as a top-level function's do
             | Check.TELambda _ -> returnHint ll value (lambdaBodyStart value)
             | _ ->
                 if opts.BindingTypes then
                     afterWord ll nameSpan.Start.Col name value.Ty 1)

            walk ll value
            walk ll body
        | Check.TELambda(p, pspan, body) ->
            (match te.Ty with
             | TFun(dom, _) when opts.ParameterTypes -> afterWord ll pspan.Start.Col p dom 2
             | _ -> ())

            walk ll body
        // a multi-line pipeline's stage shows its type at the stage's
        // line end (opt-in: noise in a long chain); a single-line pipe
        // would land mid-line and gets nothing
        | Check.TEPipe(arg, fn) ->
            (if opts.PipelineTypes then
                 let la, _ = Script.translate ll arg.Span.Start.Col
                 let lf, _ = Script.translate ll fn.Span.Start.Col

                 if lf <> la && lf >= 1 && lf <= raw.Length then
                     let code = (Script.stripComment raw[lf - 1]).TrimEnd()
                     place lf (code.Length + 1) te.Ty 1 true false)

            walk ll arg
            walk ll fn
        | _ ->
            for c in Check.childExprs te do
                walk ll c

    // the innermost lambda body's start: the `=` must precede it
    and lambdaBodyStart (te: Check.TypedExpr) =
        match te.Kind with
        | Check.TELambda(_, _, body) -> lambdaBodyStart body
        | _ -> te.Span.Start.Col

    for (ll, chk) in stmts |> List.filter (fun (ll, _) -> overlaps ll) do
        match chk.Kind with
        | Script.KLet(name, _, _) when Set.contains name signed -> ()
        | Script.KLet(name, sch, te) ->
            (match te.Kind with
             | Check.TELambda _ -> returnHint ll te (lambdaBodyStart te)
             | _ ->
                 if opts.BindingTypes then
                     binderCol name ll.Text |> Option.iter (fun jc -> afterWord ll jc name sch.Ty 1))

            walk ll te
        | Script.KLetPat(pat, schemes, te) ->
            if opts.BindingTypes then
                for n, span in Check.patNameSpans pat do
                    schemes
                    |> List.tryFind (fun (sn, _) -> sn = n)
                    |> Option.iter (fun (_, sc) -> afterWord ll span.Start.Col n sc.Ty 1)

            walk ll te
        // the `validate s =` binder is the payload [D:validated-types]
        | Script.KType(decl, Some(_, te)) ->
            (match decl.Validate, decl.Body with
             | Some v, Ast.DUnion [ (_, Some payload, _) ] when opts.BindingTypes ->
                 (match v.Binder.PKind with
                  | Ast.PVar n -> afterWord ll v.Binder.PSpan.Start.Col n payload 1
                  | _ -> ())
             | _ -> ())

            walk ll te
        | Script.KType _
        | Script.KSig _
        | Script.KModule _
        | Script.KImport _ -> ()
        | Script.KCmd te
        | Script.KExpr te -> walk ll te

    out |> Seq.sortBy (fun h -> h.Line, h.Col) |> List.ofSeq

// ---- document symbols [D:document-symbols] ----------------------

type DocSymbol = {
    Name: string
    Detail: string option
    Kind: int // LSP SymbolKind
    Range: int * int * int * int // 1-based physical (l1, c1, l2, c2), c2 past the last char
    Selection: int * int * int * int
    Children: DocSymbol list
}

/// the outline [D:document-symbols]: one symbol per declaration-shaped
/// statement (module header, import, `#sig`, type with its members,
/// let with its nested functions, pattern-let binders), a signature
/// attached to its implementation (R4), a textual fallback for a
/// failed `let`/`type` (R5). A let's detail is the hover's first line
/// (sigOrFlat), so outline and hover never disagree.
let documentSymbolsFor (path: string) (lines: string list) : DocSymbol list =
    let _, stmts, _, lls, _ = analyzeCached path lines
    let raw = List.toArray lines

    let lineLen (l: int) =
        if l >= 1 && l <= raw.Length then raw[l - 1].Length else 0

    let lastLine (ll: Script.LogicalLine) =
        ll.Segments |> List.fold (fun m (_, pl, _) -> max m pl) ll.Head

    let rangeOf (ll: Script.LogicalLine) =
        let l2 = lastLine ll
        ll.Head, 1, l2, lineLen l2 + 1

    let lineRange (l: int) = l, 1, l, lineLen l + 1

    // the name's own span when verbatim, else the statement's first
    // column (a selection must sit inside its range)
    let selAt (ll: Script.LogicalLine) (jcol: int option) (len: int) =
        match jcol |> Option.bind (fun jc -> verbatimAt raw ll jc len) with
        | Some(l, c) -> l, c, l, c + len
        | None -> ll.Head, 1, ll.Head, 1

    let sym name detail kind range sel children = {
        Name = name
        Detail = detail
        Kind = kind
        Range = range
        Selection = sel
        Children = children
    }

    let isFun (ty: Ty) =
        match ty with
        | TFun _ -> true
        | _ -> false

    // nested functions as children (inner value lets are not) [R5]
    let rec nestedFns (ll: Script.LogicalLine) (te: Check.TypedExpr) : DocSymbol list =
        match te.Kind with
        | Check.TELet(n, nspan, value, body) when
            (match value.Kind with
             | Check.TELambda _ -> true
             | _ -> false)
            ->
            let l1, c1 = Script.translate ll nspan.Start.Col
            let l2, c2 = Script.translate ll (max nspan.Start.Col (value.Span.End.Col - 1))

            let sel =
                match verbatimAt raw ll nspan.Start.Col n.Length with
                | Some(l, c) -> l, c, l, c + n.Length
                | None -> l1, c1, l1, c1

            sym n (Some(sigOrFlat n (lambdaParamNames value) value.Ty)) 12 (l1, c1, l2, c2 + 1) sel (nestedFns ll value)
            :: nestedFns ll body
        | _ -> Check.childExprs te |> List.collect (nestedFns ll)

    // a member word after the declaration's first `=` (typeSiteIn's rule)
    let memberSym (ll: Script.LogicalLine) (eq: int) (word: string) (detail: string option) (kind: int) =
        let jc = wordFind word ll.Text (if eq >= 0 then eq else 0) ll.Text.Length

        let sel = selAt ll jc word.Length
        let l, _, _, _ = sel
        sym word detail kind (lineRange l) sel []

    // signatures attach to the implementation below them [R4]: the impl
    // symbol's range starts at the sig line, its detail is the declared
    // type; an orphan keeps its own symbol
    let sigs =
        stmts
        |> List.choose (fun (ll, c) ->
            match c.Kind with
            | Script.KSig(n, ms) -> Some(n, (ll, ms))
            | _ -> None)
        |> Map.ofList

    let checkedHeads = stmts |> List.map (fun (ll, _) -> ll.Head) |> Set.ofList

    // a failed statement that reads as a declaration keeps a textual
    // symbol (no detail) until recovery lands [R5]; a signature above
    // it still attaches
    let fallback =
        lls
        |> List.filter (fun ll -> not (Set.contains ll.Head checkedHeads))
        |> List.choose (fun ll ->
            let m =
                Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*(let|type)\s+(?:pure\s+)?([A-Za-z_][A-Za-z0-9_']*)")

            if m.Success then
                let name = m.Groups[2].Value
                let kind = if m.Groups[1].Value = "let" then 13 else 23
                let l1, c1, l2, c2 = rangeOf ll

                let range =
                    match Map.tryFind name sigs with
                    | Some(sll, _) when kind = 13 && sll.Head < ll.Head -> sll.Head, 1, l2, c2
                    | _ -> l1, c1, l2, c2

                Some(sym name None kind range (selAt ll (Some(m.Groups[2].Index + 1)) name.Length) [])
            else
                None)

    let implemented =
        (stmts
         |> List.choose (fun (_, c) ->
             match c.Kind with
             | Script.KLet(n, _, _) -> Some n
             | _ -> None))
        @ (fallback |> List.filter (fun f -> f.Kind = 13) |> List.map (fun f -> f.Name))
        |> Set.ofList

    let fromStmts =
        stmts
        |> List.collect (fun (ll, chk) ->
            match chk.Kind with
            | Script.KModule(name, kwSpan) -> [
                sym
                    (name |> Option.defaultValue "module")
                    (Some "module header")
                    2
                    (rangeOf ll)
                    (selAt ll (Some kwSpan.Start.Col) "module".Length)
                    []
              ]
            | Script.KImport lm ->
                let q1 = ll.Text.IndexOf '"'
                let q2 = (if q1 >= 0 then ll.Text.IndexOf('"', q1 + 1) else -1)

                let written =
                    if q1 >= 0 && q2 > q1 then
                        Some(ll.Text.Substring(q1, q2 - q1 + 1))
                    else
                        None

                let aliasCol = wordFind lm.Alias ll.Text (max 0 q2) ll.Text.Length

                let sel =
                    match aliasCol with
                    | Some _ -> selAt ll aliasCol lm.Alias.Length
                    | None -> selAt ll (if q1 >= 0 then Some(q1 + 1) else None) (q2 - q1 + 1)

                [ sym lm.Alias written 2 (rangeOf ll) sel [] ]
            | Script.KType(decl, validator) ->
                let eq = ll.Text.IndexOf '='

                let detail =
                    match decl.Params with
                    | [] -> None
                    | ps -> Some("<" + (ps |> List.map (fun p -> "'" + p) |> String.concat ", ") + ">")

                let members, kind =
                    match decl.Body with
                    | Ast.DRecord fields ->
                        fields |> List.map (fun (f, ty, _) -> memberSym ll eq f (Some(formatTy ty)) 8), 23
                    | Ast.DUnion cases ->
                        cases
                        |> List.map (fun (c, payload, _) -> memberSym ll eq c (payload |> Option.map formatTy) 22),
                        10

                let validate =
                    match validator, decl.Validate, decl.Body with
                    | Some _, Some v, Ast.DUnion [ (_, Some payload, _) ] ->
                        let vcol = wordFind "validate" ll.Text (max 0 eq) ll.Text.Length
                        let sel = selAt ll vcol "validate".Length
                        let l, _, _, _ = sel

                        let detail =
                            match v.Binder.PKind with
                            | Ast.PVar b -> Some $"{b}: {formatTy payload}"
                            | _ -> None

                        [ sym "validate" detail 6 (lineRange l) sel [] ]
                    | _ -> []

                [
                    sym
                        decl.Name
                        detail
                        kind
                        (rangeOf ll)
                        (selAt
                            ll
                            (wordFind decl.Name ll.Text 0 (if eq >= 0 then eq else ll.Text.Length))
                            decl.Name.Length)
                        (members @ validate)
                ]
            | Script.KLet(name, sch, te) ->
                let ps = lambdaParamNames te
                let kind = if isFun sch.Ty || not ps.IsEmpty then 12 else 13
                let l1, c1, l2, c2 = rangeOf ll

                let range, detail =
                    match Map.tryFind name sigs with
                    | Some(sll, ms) when sll.Head < ll.Head -> (sll.Head, 1, l2, c2), sigOrFlat name ps ms.Ty
                    | _ -> (l1, c1, l2, c2), sigOrFlat name ps sch.Ty

                [
                    sym name (Some detail) kind range (selAt ll (binderCol name ll.Text) name.Length) (nestedFns ll te)
                ]
            | Script.KLetPat(pat, schemes, _) ->
                Check.patNameSpans pat
                |> List.sortBy (fun (_, sp) -> sp.Start.Col)
                |> List.map (fun (n, sp) ->
                    let detail =
                        schemes
                        |> List.tryPick (fun (sn, sc) -> if sn = n then Some(formatTy sc.Ty) else None)

                    sym n detail 13 (rangeOf ll) (selAt ll (Some sp.Start.Col) n.Length) [])
            | Script.KSig(name, ms) when not (Set.contains name implemented) -> [
                sym
                    name
                    (Some "signature — no implementation")
                    (if isFun ms.Ty then 12 else 13)
                    (rangeOf ll)
                    (selAt ll (binderCol name ll.Text) name.Length)
                    []
              ]
            | Script.KSig _
            | Script.KCmd _
            | Script.KExpr _ -> [])

    // `#sig` heads are no statements: read off the sig table
    let sigHeads =
        sigInfosCached path lines
        |> List.choose (fun si ->
            if si.DeclLine >= 1 && si.DeclLine <= raw.Length then
                let line = raw[si.DeclLine - 1]
                let i = line.IndexOf si.Tool
                let l = si.DeclLine

                let dir =
                    try
                        IO.Path.GetDirectoryName path
                    with _ ->
                        ""

                let rel =
                    if String.IsNullOrEmpty dir then
                        si.SigPath
                    else
                        try
                            IO.Path.GetRelativePath(dir, si.SigPath)
                        with _ ->
                            si.SigPath

                Some(
                    sym
                        si.Tool
                        (Some rel)
                        11
                        (lineRange l)
                        (if i >= 0 then
                             (l, i + 1, l, i + 1 + si.Tool.Length)
                         else
                             (l, 1, l, 1))
                        []
                )
            else
                None)

    fromStmts @ fallback @ sigHeads
    |> List.sortBy (fun s ->
        let l, c, _, _ = s.Range
        l, c)

// ---- folding ranges [D:folding-ranges] --------------------------

/// 1-based (start, end, kind) with kind ∈ comment | imports | None,
/// whole lines [D:folding-ranges]: an assembled statement over ≥2
/// physical lines, inner blocks by indentation, match arms (a `|` line
/// to the line before the next arm or the block end), `///` and `//`
/// runs (never merged, never inside district content [D:content-bytes]),
/// `import` and `#sig` runs. The textual rules run over the raw lines,
/// so a buffer that fails assembly still folds.
let foldingRangesFor (path: string) (lines: string list) : (int * int * string option) list =
    let _, _, _, lls, _ = analyzeCached path lines
    let raw = List.toArray lines
    let n = raw.Length
    let mask = Script.districtContentMask lines
    let out = ResizeArray<int * int * string option>()
    let blank (l: int) = raw[l - 1].Trim() = ""

    let indentOf (l: int) =
        raw[l - 1].Length - raw[l - 1].TrimStart().Length

    let codeOf (l: int) =
        (Script.stripComment raw[l - 1]).TrimStart()

    // trailing blank lines never fold
    let trimEnd (s: int) (e: int) =
        let mutable e = min e n

        while e > s && blank e do
            e <- e - 1

        e

    let add (s: int) (e: int) (kind: string option) =
        let e = trimEnd s e

        if e > s then
            out.Add(s, e, kind)

    // rule 1: the assembled statement
    for ll in lls do
        let last = ll.Segments |> List.fold (fun m (_, pl, _) -> max m pl) ll.Head

        if last > ll.Head && ll.Head >= 1 && last <= n then
            add ll.Head last None

    // rules 2–3 over every line (a deeper block belongs to the line
    // above it; blank lines are transparent)
    for i in 1..n do
        if not (blank i) then
            let ind = indentOf i
            let mutable j = i + 1

            while j <= n && blank j do
                j <- j + 1

            if j <= n && indentOf j > ind then
                let mutable e = j
                let mutable k = j + 1

                while k <= n && (blank k || indentOf k > ind) do
                    if not (blank k) then
                        e <- k

                    k <- k + 1

                add i e None

            // rule 3: a match arm (never a pipe stage, never district
            // content where `|` is a block scalar)
            let code = codeOf i

            if
                not mask[i - 1]
                && code.StartsWith "|"
                && not (code.StartsWith "|>")
                && not (code.StartsWith "||")
            then
                let mutable e = i
                let mutable k = i + 1
                let mutable stop = false

                while not stop && k <= n do
                    if blank k then
                        k <- k + 1
                    elif indentOf k > ind then
                        e <- k
                        k <- k + 1
                    else
                        stop <- true

                add i e None

    // rule 4: comment runs, `///` and `//` apart
    let kindOf (l: int) =
        if mask[l - 1] || blank l then
            0
        elif raw[l - 1].TrimStart().StartsWith "///" then
            1
        elif Script.classifyLine raw[l - 1] = Script.LineKind.CommentOnly then
            2
        else
            0

    let mutable runStart = 0
    let mutable runKind = 0

    for i in 1 .. n + 1 do
        let k = if i <= n then kindOf i else 0

        if k <> runKind then
            if runKind <> 0 then
                add runStart (i - 1) (Some "comment")

            runStart <- i
            runKind <- k

    // rule 5: `import` and `#sig` runs
    let importish (l: int) =
        let t = raw[l - 1]
        t.StartsWith "import " || t.StartsWith "#sig "

    let mutable s = 0

    for i in 1 .. n + 1 do
        if i <= n && importish i then
            if s = 0 then
                s <- i
        else
            if s > 0 then
                add s (i - 1) (Some "imports")

            s <- 0

    out
    |> Seq.groupBy (fun (s, e, _) -> s, e)
    |> Seq.map (fun ((s, e), g) -> s, e, (g |> Seq.tryPick (fun (_, _, k) -> k)))
    |> Seq.sortBy (fun (s, e, _) -> s, -e)
    |> List.ofSeq

// ---- document links [D:document-links] --------------------------

/// (line, startCol, len, targetPath, tooltip), 1-based
/// [D:document-links]: resolved targets only — an import's path to the
/// module file, a `schema=` name to the vendored file, a `#sig` tool
/// (and its path override) to the signature file. A target the
/// checker could not resolve gets no link; the diagnostic says why.
let documentLinksFor (path: string) (lines: string list) : (int * int * int * string * string) list =
    let _, stmts, _, _, _ = analyzeCached path lines
    let raw = List.toArray lines
    let out = ResizeArray<int * int * int * string * string>()

    for (ll, chk) in stmts do
        match chk.Kind with
        | Script.KImport lm ->
            let q1 = ll.Text.IndexOf '"'
            let q2 = (if q1 >= 0 then ll.Text.IndexOf('"', q1 + 1) else -1)

            if q1 >= 0 && q2 > q1 + 1 then
                verbatimAt raw ll (q1 + 2) (q2 - q1 - 1)
                |> Option.iter (fun (l, c) -> out.Add(l, c, q2 - q1 - 1, lm.AbsPath, lm.AbsPath))
        | _ ->
            let rec walk (te: Check.TypedExpr) =
                (match te.Kind with
                 | Check.TEYaml(_, Some name, _) ->
                     let idx = ll.Text.IndexOf("schema=" + name)

                     if idx >= 0 then
                         match Script.resolveSchemaFile path name with
                         | Ok(_, file) ->
                             verbatimAt raw ll (idx + "schema=".Length + 1) name.Length
                             |> Option.iter (fun (l, c) -> out.Add(l, c, name.Length, file, file))
                         | Error _ -> ()
                 | _ -> ())

                for c in Check.childExprs te do
                    walk c

            teOf chk |> Option.iter walk

    for si in sigInfosCached path lines do
        if si.DeclLine >= 1 && si.DeclLine <= raw.Length then
            let m =
                Text.RegularExpressions.Regex.Match(raw[si.DeclLine - 1], "^\\s*#sig\\s+(\\S+)(?:\\s+\"([^\"]*)\")?")

            if m.Success && m.Groups[1].Value = si.Tool then
                out.Add(si.DeclLine, m.Groups[1].Index + 1, m.Groups[1].Length, si.SigPath, si.SigPath)

                if m.Groups[2].Success && m.Groups[2].Length > 0 then
                    out.Add(si.DeclLine, m.Groups[2].Index + 1, m.Groups[2].Length, si.SigPath, si.SigPath)

    out |> Seq.sortBy (fun (l, c, _, _, _) -> l, c) |> List.ofSeq

// ---- diagnostic tags [D:diagnostic-tags] ------------------------

/// LSP DiagnosticTag values for a diagnostic: Unnecessary (1) on an
/// unread binder — never the bare `_` discard, which is misnamed, not
/// unnecessary — and on an unreachable arm. Nothing is Deprecated: the
/// retired-name teachings are hard errors, not deprecations.
let diagnosticTags (d: Script.Diagnostic) : int list =
    match d.Code with
    | "unused-binding" when d.Message <> Script.bareDiscardMessage -> [ 1 ]
    | "unreachable-arm" -> [ 1 ]
    | _ -> []
// a code action [D:structured-fixes]: the editor's rendering of a
// Script.Fix — Diagnostics carries the ones it resolves, Edits the
// repair in the document the diagnostics were published for
type CodeAction = {
    Title: string
    Kind: string // "quickfix" | "source.fixAll"
    Diagnostics: Script.Diagnostic list
    Edits: Script.FixEdit list
}

// the source.fixAll titles per family [D:structured-fixes]; R8 keeps the
// `_`-prefix rename out of every family
let private fixAllTitle (family: string) =
    match family with
    | "qualify" -> "Qualify all bare names in file"
    | "doc-align" -> "Align all doc comments in file"
    | f -> $"Fix all: {f}"

/// code actions over the published diagnostics [D:structured-fixes]: a
/// quickfix per diagnostic with a Fix inside the requested line range
/// (None = whole file), plus one source.fixAll per family present
/// anywhere in the file
let codeActionsFor (diags: Script.Diagnostic list) (range: (int * int) option) : CodeAction list =
    let inRange (d: Script.Diagnostic) =
        match range with
        | Some(a, b) -> d.Line >= a && d.Line <= b
        | None -> true

    let withFix = diags |> List.choose (fun d -> d.Fix |> Option.map (fun f -> d, f))

    let quick =
        withFix
        |> List.filter (fun (d, _) -> inRange d)
        |> List.map (fun (d, f) -> {
            Title = f.Title
            Kind = "quickfix"
            Diagnostics = [ d ]
            Edits = f.Edits
        })

    let all =
        withFix
        |> List.choose (fun (d, f) -> f.Family |> Option.map (fun fam -> fam, d, f))
        |> List.groupBy (fun (fam, _, _) -> fam)
        |> List.map (fun (fam, members) -> {
            Title = fixAllTitle fam
            Kind = "source.fixAll"
            Diagnostics = members |> List.map (fun (_, d, _) -> d)
            Edits = members |> List.collect (fun (_, _, f) -> f.Edits)
        })

    quick @ all
// ---- signature help [D:signature-help] ----------------------------
// fires mid-typing, when the statement is usually broken — so the
// statement prefix is read textually (the spine at the cursor's paren
// depth) and the typed tree, when it exists, only sharpens the types.
// Flat calls only (the stated subset of [D:path-param-completion]);
// null over a guess everywhere [D:hover-silence]

type SigHelp = {
    Label: string
    Doc: string option
    // [start, end) offsets in Label and the per-parameter doc
    Params: (int * int * string option) list
    Active: int option
}

// the atoms of a statement prefix at its innermost open bracket depth,
// each with its offset — quotes and brackets are one atom (the argv
// tokenizer's rule), whitespace splits at depth 0, a sibling separator
// or newline starts over; a just-opened bracket with nothing inside is
// an atom in progress of the enclosing level
let private spineAtoms (text: string) : (string * int) list =
    let opens = Collections.Generic.Stack<int>()
    let mutable quote = ' '
    let mutable i = 0

    while i < text.Length do
        let c = text[i]

        if quote <> ' ' then
            if c = '\\' && quote = '"' then
                i <- i + 1
            elif c = quote then
                quote <- ' '
        elif c = '"' || c = '\'' then
            quote <- c
        elif c = '(' || c = '[' || c = '{' then
            opens.Push i
        elif (c = ')' || c = ']' || c = '}') && opens.Count > 0 then
            opens.Pop() |> ignore

        i <- i + 1

    // an unclosed quote: the cursor sits in a string
    if quote <> ' ' then
        []
    else
        let groupFrom, pseudo =
            if opens.Count > 0 && text.Substring(opens.Peek() + 1).Trim() = "" then
                let o = opens.Pop()
                (if opens.Count > 0 then opens.Peek() + 1 else 0), Some o
            else
                (if opens.Count > 0 then opens.Peek() + 1 else 0), None

        let stop =
            match pseudo with
            | Some o -> o
            | None -> text.Length

        let out = ResizeArray<string * int>()
        let mutable start = -1
        let mutable depth = 0
        let mutable quote = ' '
        let mutable j = groupFrom

        let flush (upTo: int) =
            if start >= 0 then
                out.Add(text.Substring(start, upTo - start), start)
                start <- -1

        while j < stop do
            let c = text[j]

            if quote <> ' ' then
                if c = '\\' && quote = '"' then
                    j <- j + 1
                elif c = quote then
                    quote <- ' '
            elif c = '"' || c = '\'' then
                (if start < 0 then
                     start <- j)

                quote <- c
            elif c = '(' || c = '[' || c = '{' then
                (if start < 0 then
                     start <- j)

                depth <- depth + 1
            elif c = ')' || c = ']' || c = '}' then
                depth <- max 0 (depth - 1)
            elif depth = 0 && (c = Parser.sibSep || c = '\n') then
                flush j
                out.Clear()
            elif depth = 0 && Char.IsWhiteSpace c then
                flush j
            elif start < 0 then
                start <- j

            j <- j + 1

        flush stop
        pseudo |> Option.iter (fun o -> out.Add("(", o))
        List.ofSeq out

// inStringOrComment's twin for a TYPING position: the cursor at a
// line's end (where the space trigger lands) is code, not a comment —
// only a `//` comment's own span is one
let private typingInStringOrComment (lines: string list) (line: int) (col: int) : bool =
    if line < 1 || line > List.length lines then
        false
    else
        let text = List.item (line - 1) lines
        let cut = Scan.commentAt text

        (col >= 1 && col <= text.Length && (Script.inStringMask text)[col - 1])
        || (cut >= 0 && col - 1 >= cut)

let private isOperatorAtom (a: string) =
    a <> "" && a |> Seq.forall (fun c -> "+-*/<>=|&!@^%:~?,;".Contains c)

let private isHeadWord (a: string) =
    Text.RegularExpressions.Regex.IsMatch(a, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")
    && not (Parser.keywords.Contains a)

/// signature help at (1-based physical line, col): one signature, its
/// parameters' label offsets and docs, and the active parameter —
/// None when nothing honest can be said. Pure — the handler and the
/// unit pins share this. The null path (a string or comment, no
/// head) runs before any walk: the space trigger is chatty.
let signatureHelpAt (path: string) (lines: string list) (line: int) (col: int) : SigHelp option =
    if typingInStringOrComment lines line col then
        None
    else
        let _, stmts, env0, lls, _ = analyzeCached path lines

        // env in scope: after the last statement above the line
        let env =
            stmts
            |> List.filter (fun (ll, _) -> ll.Head < line)
            |> List.tryLast
            |> Option.map (fun (_, c) -> c.Env)
            |> Option.defaultValue env0

        let docAt (src: string list) (dl: int) (dc: int) =
            Script.docAttachments src
            |> List.tryPick (fun d ->
                if d.Line = dl && d.Col = dc then
                    Some(reflowDoc d.Doc)
                else
                    None)

        // ---- command position [D:sig-arg-complete]: the typed line
        // normalised — head, its case words, one parameter per flag
        // present — docs from the sig's ///; the sig file alone, no
        // spawn ([D:sig-dynamic-complete] is the REPL's)
        let commandHelp (ctxEnv: TypeEnv) (pre: string) : SigHelp option =
            Complete.commandArgvAt ctxEnv pre
            |> Option.bind (fun (head, argText) ->
                Script.sigInfosForFile path lines
                |> List.tryFind (fun si -> si.Tool = head)
                |> Option.bind (fun si ->
                    let before, cur = Script.SigComplete.split argText
                    let words = before @ (if cur = "" then [] else [ cur ])
                    let isFlag (w: string) = w.Length > 1 && w[0] = '-'

                    let subRun =
                        match sigRecordFor si words with
                        | Some(_, n) ->
                            words
                            |> List.skipWhile isFlag
                            |> List.takeWhile (isFlag >> not)
                            |> List.truncate n
                        | None -> []

                    // (word index, group text, field type, doc) per flag the sig knows
                    let flags =
                        words
                        |> List.mapi (fun i w -> i, w)
                        |> List.choose (fun (i, w) ->
                            if isFlag w then
                                sigFlagField si words w
                                |> Option.map (fun (rn, f, fty, sigLines, sigStmts) ->
                                    let spelled = w.Split('=')[0]

                                    let group =
                                        match fty with
                                        | TBool -> spelled
                                        | _ -> $"{spelled} <{formatTy fty}>"

                                    i, group, fty, sigFlagDoc si.SigPath rn f sigLines sigStmts)
                            else
                                None)

                    if flags.IsEmpty then
                        None
                    else
                        let sb = Text.StringBuilder(head)

                        for s in subRun do
                            sb.Append(' ').Append(s) |> ignore

                        let ps =
                            flags
                            |> List.map (fun (_, group, _, doc) ->
                                sb.Append ' ' |> ignore
                                let start = sb.Length
                                sb.Append group |> ignore
                                start, sb.Length, doc)

                        let paramOf (wi: int) =
                            flags |> List.tryFindIndex (fun (i, _, _, _) -> i = wi)

                        let lastBefore = before.Length - 1

                        let active =
                            if cur <> "" && isFlag cur then
                                paramOf (words.Length - 1)
                            elif cur = "" then
                                (if lastBefore >= 0 && isFlag before[lastBefore] then
                                     paramOf lastBefore
                                 else
                                     None)
                            else
                                // a value word: its flag's, when the flag takes one
                                flags
                                |> List.tryFind (fun (i, _, fty, _) ->
                                    i = lastBefore && fty <> TBool && not (words[i].Contains '='))
                                |> Option.bind (fun (i, _, _, _) -> paramOf i)

                        Some {
                            Label = sb.ToString()
                            Doc = None
                            Params = ps
                            Active = active
                        }))

        // ---- expression application: the head's names, type and doc
        let resolveHead (head: string) : (string list * Ty * string option) option =
            let schemeTy () =
                match Map.tryFind head env.Values with
                | Some sch -> Some sch.Ty
                | None ->
                    match head.Split '.' with
                    | [| m; mem |] ->
                        Map.tryFind m env.Modules
                        |> Option.bind (Map.tryFind mem)
                        |> Option.map (fun s -> s.Ty)
                    | _ -> None

            match Map.tryFind head Builtins.builtinDocs.Value with
            | Some d when not d.Params.IsEmpty ->
                schemeTy ()
                |> Option.map (fun ty -> d.Params, ty, Some(Builtins.renderBuiltinDoc d))
            | Some _ -> None
            | None ->
                match head.Split '.' with
                | [| alias; mem |] ->
                    // a module member: params off the module's typed body, the
                    // doc at its declaration in the module file [D:lsp-cross-file]
                    stmts
                    |> List.tryPick (fun (_, c) ->
                        match c.Kind with
                        | Script.KImport lm when lm.Alias = alias -> Some lm
                        | _ -> None)
                    |> Option.bind (fun lm ->
                        lm.Body
                        |> List.tryPick (function
                            | _, Script.CLet(bn, te) when bn = mem -> Some te
                            | _ -> None)
                        |> Option.map (fun te ->
                            let doc =
                                targetStmts lm.AbsPath
                                |> Option.bind (fun (mlines, mstmts) ->
                                    letSiteIn mstmts None mem |> Option.bind (fun (dl, dc, _) -> docAt mlines dl dc))

                            lambdaParamNames te, te.Ty, doc))
                | _ ->
                    // a top-level function of this file: the last binder above
                    let userLet =
                        stmts
                        |> List.filter (fun (ll, _) -> ll.Head < line)
                        |> List.rev
                        |> List.tryPick (fun (_, c) ->
                            match c.Kind with
                            | Script.KLet(n, sch, te) when n = head -> Some(lambdaParamNames te, sch.Ty)
                            | _ -> None)
                        |> Option.map (fun (ps, ty) ->
                            let doc =
                                letSiteIn stmts (Some line) head
                                |> Option.bind (fun (dl, dc, _) -> docAt lines dl dc)

                            ps, ty, doc)

                    // a union case: one parameter, the payload
                    let unionCase () =
                        env.Types
                        |> Map.tryPick (fun tn def ->
                            match def with
                            | Union d ->
                                d.Cases
                                |> List.tryPick (fun (c, payload) ->
                                    match payload with
                                    | Some pty when c = head ->
                                        let doc =
                                            typeSiteIn stmts tn (Some head)
                                            |> Option.bind (fun (dl, dc, _) -> docAt lines dl dc)

                                        Some([ "value" ], TFun(pty, TNamed(tn, [])), doc)
                                    | _ -> None)
                            | _ -> None)

                    userLet |> Option.orElseWith unionCase

        let expressionHelp (pre: string) : SigHelp option =
            // `|` command chaining answers nothing in v1
            match Complete.lastStage pre with
            | Some _ -> None
            | None ->
                let atoms = spineAtoms pre

                let sepIdx =
                    atoms
                    |> List.tryFindIndexBack (fun (a, _) -> isOperatorAtom a || Parser.keywords.Contains a)

                let piped =
                    match sepIdx with
                    | Some i -> fst atoms[i] = "|>"
                    | None -> false

                match List.skip (sepIdx |> Option.map ((+) 1) |> Option.defaultValue 0) atoms with
                | (head, headAt) :: args when isHeadWord head ->
                    let inAtom = pre.Length > 0 && not (Char.IsWhiteSpace pre[pre.Length - 1])

                    if inAtom && args.IsEmpty then
                        None // still typing the head
                    else
                        resolveHead head
                        |> Option.bind (fun (ps, schemeTy, doc) ->
                            // the typed tree, when the statement checks: the
                            // instantiated type at the head replaces the scheme
                            let ty =
                                toLogical stmts line col
                                |> Option.bind (fun (_, chk, _) -> teOf chk)
                                |> Option.bind (fun te ->
                                    let rec find (n: TypedExpr) =
                                        match n.Kind with
                                        | Check.TEVar v when v = head && n.Span.Start.Col = headAt + 1 -> Some n.Ty
                                        | _ -> Check.childExprs n |> List.tryPick find

                                    find te)
                                |> Option.defaultValue schemeTy

                            let label, parts = formatSignatureParts head ps ty
                            let n = parts.Length
                            let supplied = if inAtom then args.Length - 1 else args.Length

                            if n = 0 || supplied >= n then
                                None
                            else
                                let ps =
                                    parts
                                    |> List.mapi (fun i (p, s, e) ->
                                        // the pipe rule: the piped value fills the LAST
                                        // parameter (the `|>` twin of Complete.lastStage)
                                        let pdoc =
                                            if piped && i = n - 1 then
                                                Some $"{p} — filled by the piped value"
                                            else
                                                None

                                        s, e, pdoc)

                                Some {
                                    Label = label
                                    Doc = doc
                                    Params = ps
                                    Active = Some supplied
                                })
                | _ -> None

        statementUpTo env lls (Array.ofList lines) line col
        |> Option.bind (fun (ctxEnv, pre, _) ->
            // the assembler trims a line's trailing whitespace; the space
            // the cursor follows is the trigger itself, so it is restored
            let pre =
                let phys = List.item (line - 1) lines
                let typed = phys.Substring(0, min (col - 1) phys.Length)

                if
                    typed <> ""
                    && Char.IsWhiteSpace typed[typed.Length - 1]
                    && not (pre.EndsWith " ")
                then
                    pre + " "
                else
                    pre

            commandHelp ctxEnv pre |> Option.orElseWith (fun () -> expressionHelp pre))

// ---- the server ---------------------------------------------------

// --debug [D:lsp-uri-spelling]: method dispatch and every publish to
// stderr — editors surface server stderr (VS Code: the Output panel),
// so the next blink-class mystery is a log read, not a rebuild
// a one-line `range` property from 1-based physical (line, col, len)
let private writeRange (w: Text.Json.Utf8JsonWriter) (pl: int) (pc: int) (len: int) =
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

let run (debug: bool) : int =
    let stdin' = Console.OpenStandardInput()
    let docs = Collections.Generic.Dictionary<string, string>()

    // buffer-over-disk [D:modules-v1]: an imported file open in the editor is
    // read from its (possibly unsaved) buffer, else disk (decision 14)
    Script.importSourceOverride.Value <-
        Some(fun absPath ->
            // match by decoded path, not a re-derived URI spelling — a
            // dependency open under the client's own spelling (%6C…) must
            // still read from its buffer [D:lsp-uri-spelling]
            let buffered =
                docs
                |> Seq.tryPick (fun kv -> if uriToPath kv.Key = absPath then Some kv.Value else None)

            match buffered with
            | Some text -> Some(Script.splitSourceText text)
            | None ->
                if IO.File.Exists absPath then
                    Some(IO.File.ReadAllLines absPath |> Array.toList)
                else
                    None)

    // transport hardening [D:lsp-transport-caps]: the framing layer never
    // allocates for a client-supplied length before reading — an unbounded
    // Content-Length (or an unbounded header line) is an OOM otherwise. Two
    // fixed caps bound memory, and an oversized body is drained (not
    // allocated) so the stream stays framed and the session survives.
    let maxMessageBytes = 64 * 1024 * 1024 // 64MB accepted body ceiling
    let maxHeaderLineBytes = 64 * 1024 // 64KB per header line

    // read outcome: a framed message, an oversized-and-drained no-op (drop
    // it, keep serving), or end-of-stream/malformed (stop)
    let mutable drainScratch = Array.zeroCreate 65536

    let readMessage () : Choice<string, unit, unit> =
        // headers are ASCII lines ending \r\n; blank line then body
        let mutable contentLength = -1
        let mutable line = Text.StringBuilder()
        let mutable headerDone = false
        let mutable eof = false
        // an unbounded header line cannot grow memory: once the accumulator
        // passes the cap the line is malformed — stop reading its bytes into
        // the buffer, and end the header loop malformed at the newline
        let mutable headerOverflow = false

        while not headerDone && not eof do
            let b = stdin'.ReadByte()

            if b < 0 then
                eof <- true
            elif b = int '\n' then
                if headerOverflow then
                    // an over-long header line: the frame is unusable, treat
                    // as malformed and stop (the connection is out of sync)
                    eof <- true
                else
                    let l = line.ToString().TrimEnd('\r')
                    line.Clear() |> ignore

                    if l = "" then
                        headerDone <- contentLength >= 0
                    elif l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                        match Int64.TryParse(l.Substring(15).Trim()) with
                        | true, n when n >= 0L && n <= int64 Int32.MaxValue -> contentLength <- int n
                        // a length that overflows Int32 is definitively
                        // oversized — a non-negative marker past the cap
                        // routes to the drain path below
                        | true, n when n > int64 Int32.MaxValue -> contentLength <- Int32.MaxValue
                        | _ -> ()
            elif line.Length >= maxHeaderLineBytes then
                headerOverflow <- true // stop accumulating; drop the byte
            else
                line.Append(char b) |> ignore

        if eof || contentLength < 0 then
            Choice3Of3() // EOF or malformed — stop serving
        elif contentLength > maxMessageBytes then
            // oversized: do not allocate contentLength. Drain the declared
            // body in fixed chunks to keep the stream synced, then drop the
            // message as an id-less no-op and keep serving [D:lsp-transport-caps]
            let mutable remaining = contentLength

            while remaining > 0 && not eof do
                let want = min remaining drainScratch.Length
                let n = stdin'.Read(drainScratch, 0, want)
                if n <= 0 then eof <- true else remaining <- remaining - n

            if debug then
                Console.Error.WriteLine
                    $"weir lsp: dropped oversized message (Content-Length {contentLength} > cap {maxMessageBytes})"

            if eof then Choice3Of3() else Choice2Of3()
        else
            let buf = Array.zeroCreate contentLength
            let mutable read = 0

            while read < contentLength do
                let n = stdin'.Read(buf, read, contentLength - read)

                if n <= 0 then read <- contentLength else read <- read + n

            Choice1Of3(Text.Encoding.UTF8.GetString buf)

    // URIs published with diagnostics last cycle — cleared when they go clean
    let publishedUris = Collections.Generic.HashSet<string>()

    // the last published set per URI — the code-action handler reads its
    // fixes from here [D:structured-fixes]
    let lastPublished = Collections.Generic.Dictionary<string, Script.Diagnostic list>()

    // what the client declared at initialize [D:diagnostic-tags]: tags
    // and related information go out only when it asked for them; the
    // outline is hierarchical only when it can show one
    let mutable clientTags: Set<int> = Set.empty
    let mutable clientRelated = false
    let mutable hierarchicalSymbols = false
    // the inlay hint options, from initializationOptions and every
    // didChangeConfiguration [D:inlay-hints]; editors that send nothing
    // keep the defaults
    let mutable inlayOptions = defaultInlayOptions

    // the client's own spelling when the file is an open doc
    // [D:lsp-uri-spelling]; pathToUri serves only files it never named
    let uriFor (file: string) =
        docs.Keys
        |> Seq.tryFind (fun k -> uriToPath k = file)
        |> Option.defaultValue (pathToUri file)

    let writeRange0 (w: Text.Json.Utf8JsonWriter) (l1: int) (c1: int) (l2: int) (c2: int) =
        w.WritePropertyName "range"
        w.WriteStartObject()
        w.WritePropertyName "start"
        w.WriteStartObject()
        w.WriteNumber("line", l1)
        w.WriteNumber("character", c1)
        w.WriteEndObject()
        w.WritePropertyName "end"
        w.WriteStartObject()
        w.WriteNumber("line", l2)
        w.WriteNumber("character", c2)
        w.WriteEndObject()
        w.WriteEndObject()

    let publishFor (uri: string) (diags: Script.Diagnostic list) =
        lastPublished[uri] <- diags

        if debug then
            Console.Error.WriteLine $"weir lsp: -> publish {uri} ({List.length diags} diags)"

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

                w.WriteNumber(
                    "severity",
                    (match d.Severity with
                     | "warning" -> 2
                     | "note" -> 3
                     | _ -> 1)
                )

                w.WriteString("code", d.Code)
                w.WriteString("source", "weir")
                w.WriteString("message", d.Message)

                let tags = diagnosticTags d |> List.filter (fun t -> Set.contains t clientTags)

                if not tags.IsEmpty then
                    w.WritePropertyName "tags"
                    w.WriteStartArray()

                    for t in tags do
                        w.WriteNumberValue t

                    w.WriteEndArray()

                if clientRelated && not d.Related.IsEmpty then
                    w.WritePropertyName "relatedInformation"
                    w.WriteStartArray()

                    for (rf, rl, rc, rm) in d.Related do
                        w.WriteStartObject()
                        w.WritePropertyName "location"
                        w.WriteStartObject()
                        w.WriteString("uri", uriFor rf)
                        writeRange0 w (rl - 1) (rc - 1) (rl - 1) rc
                        w.WriteEndObject()
                        w.WriteString("message", rm)
                        w.WriteEndObject()

                    w.WriteEndArray()

                w.WriteEndObject()

            w.WriteEndArray()
            w.WriteEndObject())

    // re-check every open doc, publish per URI [D:modules-v1]: a module's
    // diagnostics land on the module's own file (even unopened), and an
    // importer re-checks when a dependency it reads changes. Files that went
    // clean since last cycle are published empty (cleared).
    let refreshAll () =
        let byUri = Collections.Generic.Dictionary<string, ResizeArray<Script.Diagnostic>>()

        // the client's spelling per open path [D:lsp-uri-spelling]
        let clientUris = Collections.Generic.Dictionary<string, string>()

        for kv in docs do
            clientUris[uriToPath kv.Key] <- kv.Key

        for kv in Seq.toList docs do
            // per-doc resilience [D:windows-s3]: one bad document (a
            // malformed client URI) logs and skips — the other open docs
            // still analyze and publish; the request-level guard is only
            // the backstop
            try
                let diags, _, _, _, _ = analyze kv.Key kv.Value

                for d in diags do
                    // publish under the client's own URI string when the
                    // file is an open doc [D:lsp-uri-spelling]: clients
                    // spell URIs their way (VS Code's c%3A), and a
                    // re-derived spelling splits one document into two —
                    // the diagnostic lands on ours, the every-open-doc
                    // empty publish lands on theirs, and the squiggle
                    // blinks once and clears
                    let du =
                        match clientUris.TryGetValue d.File with
                        | true, u -> u
                        | _ -> pathToUri d.File

                    match byUri.TryGetValue du with
                    | true, b -> b.Add d
                    | _ ->
                        let b = ResizeArray()
                        b.Add d
                        byUri[du] <- b
            with ex ->
                Console.Error.WriteLine $"weir lsp: skipping '{kv.Key}': {ex.Message}"

        // one publish per relevant URI: a file with diagnostics, every open
        // doc (empty if clean), and any previously-diagnosed file now clean
        let toPublish = Collections.Generic.Dictionary<string, Script.Diagnostic list>()

        for kv in byUri do
            // the same file can be diagnosed both directly (open) and as a
            // dependency of another open doc; dedup by position+message,
            // keeping the richer span (EndCol present). An importer
            // relabels a module's error `check`, so the code is no part of
            // its identity and the module's own label wins
            // [D:module-error-once]
            toPublish[kv.Key] <-
                (kv.Value
                 |> Seq.sortByDescending (fun d -> d.EndCol.IsSome)
                 |> Seq.groupBy (fun d -> d.Line, d.Col, d.Severity, d.Message)
                 |> Seq.map (fun (_, ds) ->
                     let richest = Seq.head ds

                     match ds |> Seq.tryFind (fun d -> d.Code <> "check") with
                     | Some own -> { richest with Code = own.Code }
                     | None -> richest)
                 |> List.ofSeq)

        for kv in docs do
            if not (toPublish.ContainsKey kv.Key) then
                toPublish[kv.Key] <- []

        for uri in publishedUris do
            if not (toPublish.ContainsKey uri) then
                toPublish[uri] <- []

        for kv in toPublish do
            publishFor kv.Key kv.Value

        publishedUris.Clear()

        for kv in toPublish do
            if not (List.isEmpty kv.Value) then
                publishedUris.Add kv.Key |> ignore

    let mutable running = true
    let mutable exitCode = 0
    // after `shutdown` only `exit` is served; an `exit` without it ends
    // with 1, as the LSP lifecycle prescribes
    let mutable shutDown = false

    while running do
        match readMessage () with
        | Choice3Of3() -> running <- false // EOF or malformed transport — stop
        | Choice2Of3() -> () // oversized message drained and dropped — keep serving
        | Choice1Of3 raw ->
            use doc =
                try
                    JsonDocument.Parse raw
                with _ ->
                    respondError None parseError "Parse error: the message is not JSON"
                    JsonDocument.Parse "null"

            let msg = doc.RootElement

            if msg.ValueKind <> JsonValueKind.Object && msg.ValueKind <> JsonValueKind.Null then
                respondError None invalidRequest "Invalid Request: a message is a JSON object"

            if msg.ValueKind = JsonValueKind.Object then
                let idStr =
                    tryProp "id" msg
                    |> Option.bind (fun v ->
                        match v.ValueKind with
                        | JsonValueKind.Number -> Some(IdNum(v.GetInt32()))
                        | JsonValueKind.String -> Some(IdStr(v.GetString()))
                        | _ -> None)

                let method = jStr "method" msg |> Option.defaultValue ""

                if debug then
                    Console.Error.WriteLine $"weir lsp: <- {method}"

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

                lastAnswered <- None

                // a malformed request (bad URI, bad params) must not kill
                // the server [D:windows-s3] — one bad document becomes a
                // logged skip, the server keeps serving
                try
                    match method with
                    | "exit" ->
                        exitCode <- if shutDown then 0 else 1
                        running <- false
                    | _ when shutDown ->
                        idStr
                        |> Option.iter (fun id ->
                            respondError (Some id) invalidRequest $"Invalid Request: '{method}' after shutdown")
                    | "initialize" ->
                        // resolve relative-path command heads against the
                        // workspace root, not the server's launch cwd — editors
                        // choose that differently (Zed vs VS Code), so the same
                        // relative head can be a command in one and an unbound
                        // var in the other. rootUri (or the first
                        // workspaceFolder) is a file:// URI; on absence keep cwd.
                        (jStr "rootUri" ps
                         |> Option.orElseWith (fun () -> jFirst "workspaceFolders" ps |> Option.bind (jStr "uri")))
                        |> Option.iter (fun u ->
                            try
                                Session.setCwd (System.Uri(u).LocalPath)
                            with _ ->
                                ())

                        let textDocCaps = jObj "capabilities" ps |> Option.bind (jObj "textDocument")

                        textDocCaps
                        |> Option.bind (jObj "publishDiagnostics")
                        |> Option.iter (fun pd ->
                            clientRelated <-
                                (match tryProp "relatedInformation" pd with
                                 | Some v -> v.ValueKind = JsonValueKind.True
                                 | None -> false)

                            clientTags <-
                                jObj "tagSupport" pd
                                |> Option.bind (tryProp "valueSet")
                                |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Array)
                                |> Option.map (fun v ->
                                    v.EnumerateArray()
                                    |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.Number)
                                    |> Seq.map (fun e -> e.GetInt32())
                                    |> Set.ofSeq)
                                |> Option.defaultValue Set.empty)

                        hierarchicalSymbols <-
                            textDocCaps
                            |> Option.bind (jObj "documentSymbol")
                            |> Option.bind (tryProp "hierarchicalDocumentSymbolSupport")
                            |> Option.map (fun v -> v.ValueKind = JsonValueKind.True)
                            |> Option.defaultValue false

                        jObj "initializationOptions" ps
                        |> Option.bind (jObj "inlayHints")
                        |> Option.iter (fun j -> inlayOptions <- inlayOptionsOf j)

                        idStr
                        |> Option.iter (fun id ->
                            respond id (fun w ->
                                // serverInfo.version reads Weir.Version.current — the
                                // same source as `--version` [D:masking-mechanized], so an
                                // editor and the CLI report one stamp. The value is
                                // <tag>+<hash>, all JSON-safe chars, so the placeholder
                                // splice needs no escaping.
                                w.WriteRawValue(
                                    """{"capabilities":{"textDocumentSync":1,"hoverProvider":true,"codeActionProvider":{"codeActionKinds":["quickfix","source.fixAll"]},"definitionProvider":true,"referencesProvider":true,"documentHighlightProvider":true,"renameProvider":{"prepareProvider":true},"signatureHelpProvider":{"triggerCharacters":["("," "],"retriggerCharacters":[" "]},"documentFormattingProvider":true,"completionProvider":{"triggerCharacters":[".","-"]},"semanticTokensProvider":{"legend":{"tokenTypes":["weirCommandHead","weirArgv","weirSplice"],"tokenModifiers":[]},"full":true},"foldingRangeProvider":true,"documentSymbolProvider":true,"documentLinkProvider":{"resolveProvider":false},"inlayHintProvider":{"resolveProvider":false}},"serverInfo":{"name":"weir","version":"__WEIR_VERSION__"}}"""
                                        .Replace("__WEIR_VERSION__", Weir.Version.current)
                                )))
                    | "initialized" -> ()
                    | "shutdown" ->
                        shutDown <- true
                        idStr |> Option.iter (fun id -> respond id (fun w -> w.WriteNullValue()))
                    | "textDocument/didOpen" ->
                        (match jObj "textDocument" ps with
                         | Some td ->
                             match jStr "uri" td, jStr "text" td with
                             | Some uri, Some text ->
                                 docs[uri] <- text
                                 dropAnalyses ()
                                 // re-check all: this doc and any open importer of it
                                 refreshAll ()
                             | _ -> ()
                         | None -> ())
                    | "textDocument/didChange" ->
                        (match jObj "textDocument" ps, jFirst "contentChanges" ps with
                         | Some td, Some change ->
                             match jStr "uri" td, jStr "text" change with
                             | Some uri, Some text ->
                                 docs[uri] <- text
                                 dropAnalyses ()
                                 refreshAll ()
                             | _ -> ()
                         | _ -> ())
                    | "textDocument/didClose" ->
                        jObj "textDocument" ps
                        |> Option.bind (jStr "uri")
                        |> Option.iter (fun uri ->
                            docs.Remove uri |> ignore
                            dropAnalyses ()
                            refreshAll ())
                    | "textDocument/semanticTokens/full" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf () with
                            | Some(_, text) ->
                                let lines = Script.splitSourceText text
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
                    | "textDocument/codeAction" ->
                        // the fixes derive from the published set — no
                        // re-analysis [D:structured-fixes]; the edits are
                        // keyed by the document's own URI
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf () with
                            | Some(uri, _) ->
                                let published =
                                    match lastPublished.TryGetValue uri with
                                    | true, ds -> ds
                                    | _ -> []

                                let requestedLines =
                                    jObj "range" ps
                                    |> Option.bind (fun r ->
                                        match jObj "start" r, jObj "end" r with
                                        | Some st, Some en ->
                                            match jNum "line" st, jNum "line" en with
                                            | Some a, Some b -> Some(a + 1, b + 1)
                                            | _ -> None
                                        | _ -> None)

                                let writeRange (l1: int) (c1: int) (l2: int) (c2: int) =
                                    w.WritePropertyName "range"
                                    w.WriteStartObject()
                                    w.WritePropertyName "start"
                                    w.WriteStartObject()
                                    w.WriteNumber("line", l1 - 1)
                                    w.WriteNumber("character", c1 - 1)
                                    w.WriteEndObject()
                                    w.WritePropertyName "end"
                                    w.WriteStartObject()
                                    w.WriteNumber("line", l2 - 1)
                                    w.WriteNumber("character", c2 - 1)
                                    w.WriteEndObject()
                                    w.WriteEndObject()

                                w.WriteStartArray()

                                for a in codeActionsFor published requestedLines do
                                    w.WriteStartObject()
                                    w.WriteString("title", a.Title)
                                    w.WriteString("kind", a.Kind)
                                    w.WritePropertyName "diagnostics"
                                    w.WriteStartArray()

                                    for d in a.Diagnostics do
                                        w.WriteStartObject()

                                        writeRange
                                            d.Line
                                            d.Col
                                            (d.EndLine |> Option.defaultValue d.Line)
                                            (d.EndCol |> Option.defaultValue (d.Col + 1))

                                        w.WriteNumber(
                                            "severity",
                                            (match d.Severity with
                                             | "warning" -> 2
                                             | "note" -> 3
                                             | _ -> 1)
                                        )

                                        w.WriteString("code", d.Code)
                                        w.WriteString("source", "weir")
                                        w.WriteString("message", d.Message)
                                        w.WriteEndObject()

                                    w.WriteEndArray()
                                    w.WritePropertyName "edit"
                                    w.WriteStartObject()
                                    w.WritePropertyName "changes"
                                    w.WriteStartObject()
                                    w.WritePropertyName uri
                                    w.WriteStartArray()

                                    for e in a.Edits do
                                        w.WriteStartObject()
                                        writeRange e.Line e.Col e.EndLine e.EndCol
                                        w.WriteString("newText", e.NewText)
                                        w.WriteEndObject()

                                    w.WriteEndArray()
                                    w.WriteEndObject()
                                    w.WriteEndObject()
                                    w.WriteEndObject()

                                w.WriteEndArray()
                            | None -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/hover" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let lines = Script.splitSourceText text

                                // a #session field key reports the key + its
                                // schema type, never the rewritten binder
                                // [D:init-marker]
                                match
                                    sessionFieldHover (uriToPath uri) lines line col
                                    |> Option.orElseWith (fun () -> hoverAt (uriToPath uri) lines line col)
                                with
                                | Some t ->
                                    w.WriteStartObject()
                                    w.WritePropertyName "contents"
                                    w.WriteStartObject()
                                    w.WriteString("kind", "plaintext")
                                    // each line ends in a space [D:hover-reflow]: a
                                    // client that drops newlines (micro's
                                    // one-line info bar) still separates
                                    // the words; elsewhere it is invisible
                                    w.WriteString("value", t.Replace("\n", " \n"))
                                    w.WriteEndObject()
                                    w.WriteEndObject()
                                | None -> w.WriteNullValue()
                            | _ -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/definition" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let lines = Script.splitSourceText text

                                match definitionTarget (uriToPath uri) lines line col with
                                | Some(fileOpt, pl, pc, len) ->
                                    let targetUri =
                                        match fileOpt with
                                        | None -> uri
                                        | Some f ->
                                            // the client's own spelling when the target is
                                            // an open doc [D:lsp-uri-spelling]; pathToUri
                                            // serves only files the client never named
                                            docs.Keys
                                            |> Seq.tryFind (fun k -> uriToPath k = f)
                                            |> Option.defaultValue (pathToUri f)

                                    w.WriteStartObject()
                                    w.WriteString("uri", targetUri)
                                    writeRange w pl pc len
                                    w.WriteEndObject()
                                | None -> w.WriteNullValue()
                            | _ -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/references" ->
                        let includeDeclaration =
                            jObj "context" ps
                            |> Option.bind (tryProp "includeDeclaration")
                            |> Option.map (fun v -> v.ValueKind <> JsonValueKind.False)
                            |> Option.defaultValue true

                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let lines = Script.splitSourceText text

                                match referencesFor (uriToPath uri) lines line col includeDeclaration with
                                | Some occs ->
                                    w.WriteStartArray()

                                    for pl, pc, len in occs do
                                        w.WriteStartObject()
                                        w.WriteString("uri", uri)
                                        writeRange w pl pc len
                                        w.WriteEndObject()

                                    w.WriteEndArray()
                                | None -> w.WriteNullValue()
                            | _ -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/documentHighlight" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let lines = Script.splitSourceText text

                                match documentHighlightFor (uriToPath uri) lines line col with
                                | Some occs ->
                                    w.WriteStartArray()

                                    for pl, pc, len, kind in occs do
                                        w.WriteStartObject()
                                        writeRange w pl pc len
                                        w.WriteNumber("kind", kind)
                                        w.WriteEndObject()

                                    w.WriteEndArray()
                                | None -> w.WriteNullValue()
                            | _ -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/prepareRename" ->
                        // a refusal is a RequestFailed error whose message is
                        // the reason — the client shows it under the rename
                        // box, where a null shows nothing [D:lsp-rename]
                        let outcome =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let lines = Script.splitSourceText text

                                prepareRenameFor (uriToPath uri) lines line col
                                |> Result.map (fun (pl, pc, len) -> lines, pl, pc, len)
                            | _ -> Error "nothing to rename here — the document is not open"

                        idStr
                        |> Option.iter (fun id ->
                            match outcome with
                            | Ok(lines, pl, pc, len) ->
                                respond id (fun w ->
                                    w.WriteStartObject()
                                    writeRange w pl pc len
                                    w.WriteString("placeholder", (List.item (pl - 1) lines).Substring(pc - 1, len))
                                    w.WriteEndObject())
                            | Error reason -> respondError (Some id) requestFailed reason)
                    | "textDocument/rename" ->
                        let outcome =
                            match docOf (), posOf (), jStr "newName" ps with
                            | Some(uri, text), Some(line, col), Some newName ->
                                let lines = Script.splitSourceText text

                                renameFor (uriToPath uri) lines line col newName
                                |> Result.map (fun edits -> uri, newName, edits)
                            | _, _, None -> Error "rename needs the new name"
                            | _ -> Error "nothing to rename here — the document is not open"

                        idStr
                        |> Option.iter (fun id ->
                            match outcome with
                            | Ok(uri, newName, edits) ->
                                // the document's own URI spelling [D:lsp-uri-spelling]
                                respond id (fun w ->
                                    w.WriteStartObject()
                                    w.WritePropertyName "changes"
                                    w.WriteStartObject()
                                    w.WritePropertyName uri
                                    w.WriteStartArray()

                                    for pl, pc, len in edits do
                                        w.WriteStartObject()
                                        writeRange w pl pc len
                                        w.WriteString("newText", newName)
                                        w.WriteEndObject()

                                    w.WriteEndArray()
                                    w.WriteEndObject()
                                    w.WriteEndObject())
                            | Error reason -> respondError (Some id) requestFailed reason)
                    | "textDocument/formatting" ->
                        // client-sent text only (the security non-claim holds);
                        // editor options are ignored — weir fmt is canonical,
                        // and this is its own entry point (Fmt.formatSource):
                        // the edit is what `weir fmt` would write, and where
                        // the CLI refuses there are no edits.
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf () with
                            | Some(uri, text) ->
                                let lines = Script.splitSourceText text

                                match Fmt.formatSource (uriToPath uri) text with
                                | Ok(formatted, true) ->
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
                                    w.WriteString("newText", String.concat "\n" formatted + "\n")
                                    w.WriteEndObject()
                                    w.WriteEndArray()
                                | _ -> w.WriteNullValue() // refusal or already canonical
                            | None -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/completion" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let _, stmts, env0, allLls, partials = analyze uri text

                                // env in scope: after the last statement above the line
                                // (a partial's env is its hole-bound one [D:block-recovery])
                                let env =
                                    withPartials stmts partials
                                    |> List.filter (fun (ll, _) -> ll.Head < line)
                                    |> List.tryLast
                                    |> Option.map (fun (_, c) -> c.Env)
                                    |> Option.defaultValue env0

                                // the effective lines (init directives rewritten,
                                // column-preserving) so a #session field value
                                // completes as the let-RHS it becomes [D:session-prompt]
                                let effLines = effectiveLines uri text |> Array.ofList
                                let origLines = (Script.splitSourceText text |> Array.ofList)

                                // a #alias line is blanked in effectiveLines, so
                                // its command completes off the ORIGINAL line
                                // [D:command-head-alias]; everything else uses the
                                // rewritten lines
                                let onAliasLine =
                                    isReplInitPath (uriToPath uri)
                                    && line - 1 < origLines.Length
                                    && origLines[line - 1].TrimStart().StartsWith "#alias "

                                let lines = if onAliasLine then origLines else effLines
                                let lineText = if line - 1 < lines.Length then lines[line - 1] else ""
                                let upto = lineText.Substring(0, min (col - 1) lineText.Length)

                                // Complete's rule, not a copy of it
                                let wordStart = Complete.wordStartAt upto upto.Length

                                let word = upto.Substring wordStart

                                // #alias command slot: in the first word after `=`
                                // (the command head), offer PATH commands
                                let aliasCmd =
                                    if
                                        onAliasLine
                                        && System.Text.RegularExpressions.Regex.IsMatch(
                                            upto,
                                            @"^\s*#alias\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*[^\s]*$"
                                        )
                                    then
                                        Extern.names ()
                                        |> Seq.filter (fun n -> n.StartsWith word)
                                        |> Seq.truncate 100
                                        |> List.ofSeq
                                    else
                                        []

                                // the within kind slot [D:within-kinds] — the
                                // items come from Complete.suggest (the schema=
                                // mechanism kin); this gate only scopes the
                                // binds/consumes detail to the slot
                                let kindSlot =
                                    let beforeW = upto.Substring(0, wordStart).TrimEnd()

                                    beforeW.EndsWith "within"
                                    && (beforeW.Length = "within".Length
                                        || not (
                                            Char.IsLetterOrDigit beforeW[beforeW.Length - 7]
                                            || beforeW[beforeW.Length - 7] = '_'
                                        ))

                                // error-recovery path: a single-dot word whose
                                // head is unknown (or the `_` shorthand) —
                                // repair the (possibly broken) containing
                                // statement and read the head's type from the
                                // typed tree; the REPL's entry shares the
                                // repair [D:entry-complete]
                                let repaired =
                                    if word.Contains '.' then
                                        match statementUpTo env allLls lines line col with
                                        | Some(ctxEnv, pre, post) when pre.EndsWith word ->
                                            Script.repairedFields ctxEnv pre post
                                        | _ -> None
                                    else
                                        None

                                // sig flag completion [D:sig-flag-completion]: a
                                // `-`-word after a sig'd tool offers the sig's own
                                // longs, kebab spelling — without this, editors
                                // word-complete the sig file's camelCase field
                                // names, which the checker then rightly rejects
                                // the nearest sig'd tool to the left — the
                                // shared completer below reads from it
                                let nearestSig =
                                    let path =
                                        try
                                            Uri(uri).LocalPath
                                        with _ ->
                                            uri

                                    Script.sigInfosForFile path (List.ofArray lines)
                                    |> List.choose (fun si ->
                                        let m =
                                            Text.RegularExpressions.Regex.Matches(
                                                upto,
                                                $"\\b{Text.RegularExpressions.Regex.Escape si.Tool}\\b"
                                            )

                                        if m.Count > 0 then Some(m[m.Count - 1].Index, si) else None)
                                    |> List.sortByDescending fst
                                    |> List.tryHead

                                // the shared completer [D:sig-arg-complete]:
                                // subcommand words, the matched case's flags,
                                // a flag's value by arity — what the REPL's
                                // Tab offers too
                                let sigItems =
                                    nearestSig
                                    |> Option.bind (fun (toolAt, si) ->
                                        let argText = upto.Substring(toolAt + si.Tool.Length)

                                        if argText = "" || not (Char.IsWhiteSpace argText[0]) then
                                            None
                                        else
                                            match Script.SigComplete.complete si argText word with
                                            | Script.SigWords ws -> Some ws
                                            | Script.SigNothing -> Some []
                                            | Script.SigPaths -> None)

                                // #session key slot [D:session-prompt]: at a
                                // field-name position inside the block (init file
                                // only, nothing typed past the key yet), offer the
                                // keys rather than the general pool
                                let sessionKeys =
                                    if not (isReplInitPath (uriToPath uri)) then
                                        []
                                    else
                                        let orig = (Script.splitSourceText text |> Array.ofList)
                                        let mutable inBlock = false

                                        for i in 0 .. min (line - 2) (orig.Length - 1) do
                                            let tt = orig[i].Trim()

                                            if tt.StartsWith "#session" then
                                                inBlock <- true
                                            elif inBlock && tt = "}" then
                                                inBlock <- false

                                        let cur = if line - 1 < orig.Length then orig[line - 1] else ""
                                        let up = cur.Substring(0, min (col - 1) cur.Length)

                                        if inBlock && cur.Trim() <> "}" && not (up.Contains "=") then
                                            [ "cwd"; "echoCap"; "env"; "logLevel"; "prompt" ]
                                            |> List.filter (fun k -> k.StartsWith word)
                                        else
                                            []

                                // a `$` inside a yaml block takes the yaml
                                // district's splice law, not the argv one
                                // [D:argv-splice-complete]
                                let sigil =
                                    if wordStart >= 2 && upto.Substring(wordStart - 2, 2) = "$@" then
                                        2
                                    elif wordStart >= 1 && upto[wordStart - 1] = '$' then
                                        1
                                    else
                                        0

                                let yamlSplice =
                                    if
                                        sigil > 0
                                        && Complete.dollarLiteral
                                            lines[0 .. min (line - 2) (lines.Length - 1)]
                                            (upto.Substring(0, wordStart - sigil))
                                    then
                                        // where the `$` is literal text: nothing
                                        Some []
                                    elif
                                        wordStart >= 1
                                        && upto[wordStart - 1] = '$'
                                        && line - 1 < lines.Length
                                        && inYamlBlock lines (line - 1)
                                    then
                                        Some(Complete.yamlSplicePool env word)
                                    else
                                        None

                                let untilStopped =
                                    Complete.untilStoppedAt
                                        (lines[0 .. min (line - 2) (lines.Length - 1)] |> List.ofArray)
                                        (upto.Substring(0, wordStart))

                                let typePosition =
                                    match statementUpTo env allLls lines line col with
                                    | Some(_, pre, _) when pre.EndsWith word ->
                                        Complete.typePositionAt (pre.Substring(0, pre.Length - word.Length))
                                    | _ -> false

                                let items =
                                    match repaired with
                                    | _ when typePosition -> Complete.typeNameOffers env word
                                    | _ when untilStopped ->
                                        [ "stopped" ] |> List.filter (fun w -> w.StartsWith word && w <> word)
                                    | _ when yamlSplice.IsSome -> yamlSplice.Value
                                    | Some fields when not fields.IsEmpty -> fields
                                    | _ when not aliasCmd.IsEmpty -> aliasCmd
                                    | _ when not sessionKeys.IsEmpty -> sessionKeys
                                    | _ when sigItems.IsSome -> sigItems.Value
                                    | _ ->
                                        // binders may sit on earlier lines —
                                        // the whole doc is the binder scope
                                        let env =
                                            match statementUpTo env allLls lines line col with
                                            | Some(ctxEnv, pre, _) when sigil > 0 && pre.EndsWith word ->
                                                Script.withSpliceLocals ctxEnv pre word
                                            | _ -> env

                                        Complete.suggestScoped env text upto wordStart

                                // line-head position: PATH commands join (the
                                // command-mode classifier's territory) — but not
                                // in the #session key slot, which is closed
                                let items =
                                    if upto.Substring(0, wordStart).Trim() = "" && List.isEmpty sessionKeys then
                                        let word = upto.Substring wordStart

                                        items
                                        @ (Extern.names ()
                                           |> Seq.filter (fun n -> n.StartsWith word)
                                           |> Seq.truncate 50
                                           |> List.ofSeq)
                                    else
                                        items

                                // completion detail [D:doc-comments]: the `///`
                                // doc for a documented name. Name-keyed here (a
                                // completion item is a name; last-wins on a shared
                                // name) — the position-keyed map stays for hover
                                let docByName =
                                    Script.docAttachments (List.ofArray lines)
                                    |> List.choose (fun d ->
                                        if
                                            d.Line - 1 < lines.Length && d.Col - 1 + d.Len <= lines[d.Line - 1].Length
                                        then
                                            Some(lines[d.Line - 1].Substring(d.Col - 1, d.Len), reflowDoc d.Doc)
                                        else
                                            None)
                                    |> Map.ofList

                                w.WriteStartArray()

                                // textEdit with an explicit range: clients replace
                                // [wordStart, cursor) with the suggestion — bare
                                // labels double-insert after dots and get
                                // prefix-filtered inside parens [D:completion-textedit]
                                for label in items |> List.distinct |> List.truncate 200 do
                                    w.WriteStartObject()
                                    w.WriteString("label", label)

                                    // the annotated signature as `detail` — the
                                    // other surface names are read on
                                    // [D:annotated-signature]
                                    let kindDetail =
                                        if kindSlot then
                                            Ast.withinKinds
                                            |> List.tryPick (fun k ->
                                                if k.Name = label then
                                                    Some((if k.Binds then "binds — " else "consumes — ") + k.Doc)
                                                else
                                                    None)
                                        else
                                            None

                                    let sigDetail =
                                        Map.tryFind label Builtins.builtinDocs.Value
                                        |> Option.filter (fun d -> not (List.isEmpty d.Params))
                                        |> Option.bind (fun d ->
                                            let tyOf =
                                                match Map.tryFind label env.Values with
                                                | Some sch -> Some sch.Ty
                                                | None ->
                                                    match label.Split '.' with
                                                    | [| m; mem |] ->
                                                        Map.tryFind m env.Modules
                                                        |> Option.bind (Map.tryFind mem)
                                                        |> Option.map (fun s -> s.Ty)
                                                    | _ -> None

                                            tyOf |> Option.map (fun ty -> formatSignature label d.Params ty))

                                    match kindDetail |> Option.orElse sigDetail with
                                    | Some s -> w.WriteString("detail", s)
                                    | None -> ()

                                    // a user `///` doc wins; else the builtin's
                                    // doc (Seq.map, print, …) [D:builtin-docs]
                                    match
                                        Map.tryFind label docByName
                                        |> Option.orElse (
                                            Map.tryFind label Builtins.builtinDocs.Value
                                            |> Option.map Builtins.renderBuiltinDoc
                                        )
                                    with
                                    | Some doc -> w.WriteString("documentation", doc)
                                    | None -> ()

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
                    | "workspace/didChangeConfiguration" ->
                        // the whole `weir` section arrives [D:inlay-hints]
                        jObj "settings" ps
                        |> Option.bind (jObj "weir")
                        |> Option.bind (jObj "inlayHints")
                        |> Option.iter (fun j -> inlayOptions <- inlayOptionsOf j)
                    | "textDocument/foldingRange" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf () with
                            | Some(uri, text) ->
                                let lines = Script.splitSourceText text
                                w.WriteStartArray()

                                for (s, e, kind) in foldingRangesFor (uriToPath uri) lines do
                                    w.WriteStartObject()
                                    w.WriteNumber("startLine", s - 1)
                                    w.WriteNumber("endLine", e - 1)

                                    match kind with
                                    | Some k -> w.WriteString("kind", k)
                                    | None -> ()

                                    w.WriteEndObject()

                                w.WriteEndArray()
                            | None -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/documentSymbol" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf () with
                            | Some(uri, text) ->
                                let lines = Script.splitSourceText text
                                let syms = documentSymbolsFor (uriToPath uri) lines

                                let rec writeSymbol (sy: DocSymbol) =
                                    let l1, c1, l2, c2 = sy.Range
                                    let s1, t1, s2, t2 = sy.Selection
                                    w.WriteStartObject()
                                    w.WriteString("name", sy.Name)

                                    match sy.Detail with
                                    | Some d -> w.WriteString("detail", d)
                                    | None -> ()

                                    w.WriteNumber("kind", sy.Kind)
                                    writeRange0 w (l1 - 1) (c1 - 1) (l2 - 1) (c2 - 1)
                                    w.WritePropertyName "selectionRange"
                                    w.WriteStartObject()
                                    w.WritePropertyName "start"
                                    w.WriteStartObject()
                                    w.WriteNumber("line", s1 - 1)
                                    w.WriteNumber("character", t1 - 1)
                                    w.WriteEndObject()
                                    w.WritePropertyName "end"
                                    w.WriteStartObject()
                                    w.WriteNumber("line", s2 - 1)
                                    w.WriteNumber("character", t2 - 1)
                                    w.WriteEndObject()
                                    w.WriteEndObject()

                                    if not sy.Children.IsEmpty then
                                        w.WritePropertyName "children"
                                        w.WriteStartArray()

                                        for c in sy.Children do
                                            writeSymbol c

                                        w.WriteEndArray()

                                    w.WriteEndObject()

                                // the flat form for a client without
                                // hierarchy support: children flattened,
                                // containerName = the parent
                                let rec writeFlat (container: string option) (sy: DocSymbol) =
                                    let l1, c1, l2, c2 = sy.Range
                                    w.WriteStartObject()
                                    w.WriteString("name", sy.Name)
                                    w.WriteNumber("kind", sy.Kind)
                                    w.WritePropertyName "location"
                                    w.WriteStartObject()
                                    w.WriteString("uri", uri)
                                    writeRange0 w (l1 - 1) (c1 - 1) (l2 - 1) (c2 - 1)
                                    w.WriteEndObject()

                                    match container with
                                    | Some c -> w.WriteString("containerName", c)
                                    | None -> ()

                                    w.WriteEndObject()

                                    for c in sy.Children do
                                        writeFlat (Some sy.Name) c

                                w.WriteStartArray()

                                for sy in syms do
                                    if hierarchicalSymbols then
                                        writeSymbol sy
                                    else
                                        writeFlat None sy

                                w.WriteEndArray()
                            | None -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/documentLink" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf () with
                            | Some(uri, text) ->
                                let lines = Script.splitSourceText text
                                w.WriteStartArray()

                                for (l, c, len, target, tip) in documentLinksFor (uriToPath uri) lines do
                                    w.WriteStartObject()
                                    writeRange0 w (l - 1) (c - 1) (l - 1) (c - 1 + len)
                                    w.WriteString("target", uriFor target)
                                    w.WriteString("tooltip", tip)
                                    w.WriteEndObject()

                                w.WriteEndArray()
                            | None -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/inlayHint" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            let range =
                                jObj "range" ps
                                |> Option.bind (fun r ->
                                    match jObj "start" r, jObj "end" r with
                                    | Some st, Some en ->
                                        match jNum "line" st, jNum "line" en with
                                        | Some a, Some b -> Some(a + 1, b + 1)
                                        | _ -> None
                                    | _ -> None)

                            match docOf (), range with
                            | Some(uri, text), Some(fromLine, toLine) ->
                                let lines = Script.splitSourceText text
                                w.WriteStartArray()

                                for h in inlayHintsFor inlayOptions (uriToPath uri) lines fromLine toLine do
                                    w.WriteStartObject()
                                    w.WritePropertyName "position"
                                    w.WriteStartObject()
                                    w.WriteNumber("line", h.Line - 1)
                                    w.WriteNumber("character", h.Col - 1)
                                    w.WriteEndObject()
                                    w.WriteString("label", h.Label)
                                    w.WriteNumber("kind", h.Kind)

                                    match h.Tooltip with
                                    | Some t -> w.WriteString("tooltip", t)
                                    | None -> ()

                                    w.WriteBoolean("paddingLeft", h.PaddingLeft)
                                    w.WriteBoolean("paddingRight", h.PaddingRight)
                                    w.WriteEndObject()

                                w.WriteEndArray()
                            | _ -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "textDocument/signatureHelp" ->
                        let writeResult (w: Text.Json.Utf8JsonWriter) =
                            match docOf (), posOf () with
                            | Some(uri, text), Some(line, col) ->
                                let lines = Script.splitSourceText text

                                match signatureHelpAt (uriToPath uri) lines line col with
                                | Some sh ->
                                    w.WriteStartObject()
                                    w.WritePropertyName "signatures"
                                    w.WriteStartArray()
                                    w.WriteStartObject()
                                    w.WriteString("label", sh.Label)
                                    sh.Doc |> Option.iter (fun d -> w.WriteString("documentation", d))
                                    w.WritePropertyName "parameters"
                                    w.WriteStartArray()

                                    for s, e, d in sh.Params do
                                        w.WriteStartObject()
                                        w.WritePropertyName "label"
                                        w.WriteStartArray()
                                        w.WriteNumberValue s
                                        w.WriteNumberValue e
                                        w.WriteEndArray()
                                        d |> Option.iter (fun d -> w.WriteString("documentation", d))
                                        w.WriteEndObject()

                                    w.WriteEndArray()
                                    w.WriteEndObject()
                                    w.WriteEndArray()
                                    w.WriteNumber("activeSignature", 0)
                                    sh.Active |> Option.iter (fun a -> w.WriteNumber("activeParameter", a))
                                    w.WriteEndObject()
                                | None -> w.WriteNullValue()
                            | _ -> w.WriteNullValue()

                        idStr |> Option.iter (fun id -> respond id writeResult)
                    | "" when (tryProp "result" msg).IsSome || (tryProp "error" msg).IsSome -> () // a client response
                    | "" ->
                        idStr
                        |> Option.iter (fun id -> respondError (Some id) invalidRequest "Invalid Request: no method")
                    | _ ->
                        // unknown request: MethodNotFound; unknown notification: ignore
                        idStr
                        |> Option.iter (fun id -> respondError (Some id) methodNotFound $"Method not found: '{method}'")
                with ex ->
                    Console.Error.WriteLine $"weir lsp: request '{method}' failed: {ex.Message}"

                    // a request is always answered — a throwing handler
                    // answers with an error, never silence
                    match idStr with
                    | Some id when lastAnswered <> Some id ->
                        respondError (Some id) internalError $"Internal error: '{method}' failed: {ex.Message}"
                    | _ -> ()

    exitCode
