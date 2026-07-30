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
        | Script.KType _
        | Script.KModule _
        | Script.KImport _ -> ()
        | Script.KLet(_, _, te)
        | Script.KLetPat(_, _, te)
        | Script.KCmd te
        | Script.KExpr te -> walk ll te

    out |> Seq.sortBy (fun (l, c, _, _) -> l, c) |> Seq.distinct |> List.ofSeq

// ---- analysis helpers ---------------------------------------------

// URIs on the wire, filesystem paths for import resolution [D:modules-v1]
let internal uriToPath (uri: string) : string =
    if uri.StartsWith "file:" then
        System.Uri(uri).LocalPath
    else
        uri

let internal pathToUri (path: string) : string =
    if path.StartsWith "file:" then
        path
    else
        System.Uri(path).AbsoluteUri

let private analyze (uri: string) (text: string) =
    let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList
    // analyze against the real PATH so imports resolve relative to the file;
    // diagnostics come back File-identified (the entry + its modules)
    let path = uriToPath uri
    let diags, stmts, env0, lls = Script.analyzeLines path lines
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
    | Script.KType _
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
        | Check.TELet(n, _, tvalue, _) when n = name && te.Span.Start.Col <= jcol && jcol < te.Span.End.Col ->
            Some tvalue.Ty
        | _ -> None

// a lambda PARAM binder at the column shows its OWN type (the domain of
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

// pattern binders in scope, each at its own PSpan
let rec private patScope (p: Ast.Pattern) (scope: Map<string, Span>) : Map<string, Span> =
    match p.PKind with
    | Ast.PVar n -> Map.add n p.PSpan scope
    | Ast.PTuple ps -> ps |> List.fold (fun s q -> patScope q s) scope
    | Ast.PCase(_, Some inner) -> patScope inner scope
    | _ -> scope

// lexical resolution for LOCAL binders [PLAN-diagnostics-arc C]: find
// the use (the TEVar at the column) while carrying the enclosing
// binder scope — innermost wins. Returns Some(Some span) = locally
// bound there; Some None = use found, top-level territory; None = the
// use is not in this subtree.
let rec private localDef
    (scope: Map<string, Span>)
    (name: string)
    (jcol: int)
    (te: Check.TypedExpr)
    : Span option option =
    match te.Kind with
    | Check.TEVar n when n = name && te.Span.Start.Col <= jcol && jcol < te.Span.End.Col -> Some(Map.tryFind name scope)
    | Check.TELet(n, nspan, tv, body) ->
        (match localDef scope name jcol tv with
         | Some r -> Some r
         | None -> localDef (Map.add n nspan scope) name jcol body)
    | Check.TELetPat(pat, tv, body) ->
        (match localDef scope name jcol tv with
         | Some r -> Some r
         | None -> localDef (patScope pat scope) name jcol body)
    | Check.TELambda(p, pspan, body) -> localDef (Map.add p pspan scope) name jcol body
    | Check.TELambdaPat(pat, body) -> localDef (patScope pat scope) name jcol body
    | Check.TEMatch(scrut, arms) ->
        (match localDef scope name jcol scrut with
         | Some r -> Some r
         | None ->
             arms
             |> List.tryPick (fun (p, guard, body) ->
                 let s2 = patScope p scope

                 let inGuard =
                     match guard with
                     | Some g -> localDef s2 name jcol g
                     | None -> None

                 match inGuard with
                 | Some r -> Some r
                 | None -> localDef s2 name jcol body))
    | _ -> Check.childExprs te |> List.tryPick (localDef scope name jcol)

/// hover text at (1-based physical line, col), or None. Pure — the
/// handler and the unit pins share this. TYPE FIRST, then the `///`
/// doc, when the cursor is on a documented name [D:doc-comments]. Type
/// priority: an inner-let BINDER shows its bound value's type; a lambda
/// PARAM binder shows its own type (the domain, not the arrow nodeAt
/// would find); else the typed node at the column; else the statement's
/// top-level scheme [D:lsp-v1].
/// hover for a `type` declaration position (KType): the type NAME
/// renders its definition, a FIELD name its type, a union CASE its
/// signature — so "type first" holds at the field/case/type positions
/// too [D:doc-comments].
let private declHover (decl: Ast.Decl) (word: string) : string option =
    match decl.Body with
    | Ast.DRecord fields ->
        if word = decl.Name then
            let body =
                fields
                |> List.map (fun (n, ty, _) -> $"{n}: {formatTy ty}")
                |> String.concat "; "

            Some $"{{ {body} }}"
        else
            fields
            |> List.tryPick (fun (n, ty, _) -> if n = word then Some(formatTy ty) else None)
    | Ast.DUnion cases ->
        let caseSig (n, tyO) =
            match tyO with
            | Some t -> $"{n} of {formatTy t}"
            | None -> n

        if word = decl.Name then
            Some(cases |> List.map caseSig |> String.concat " | ")
        else
            cases
            |> List.tryPick (fun (n, tyO) -> if n = word then Some(caseSig (n, tyO)) else None)

/// a keyword, an operator, punctuation, whitespace, or the wildcard `_`
/// hovers as NOTHING — never the enclosing node's type [D:hover-silence].
/// A wrong `unit`/`int` on the most-hovered tokens teaches the user that
/// hover lies; null is the honest answer. Runs BEFORE the enclosing-node
/// fallback, scoped by what the cursor is ON — identifiers, numbers, and
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

// hoverType is defined AFTER definitionFor — it composes with it for the
// Group 1a lookup (a usage / field / case reference resolves to its
// declaration site, and the `///` doc is read there) [D:hover-completeness].

/// the type of a local binder (a pattern PAYLOAD binder, an inner let)
/// read from a USE of it in the typed tree — the binder position itself
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

/// the annotated signature of the inner-let binding `name` at the column —
/// the sibling of innerLetType that renders names, not just the arrow type
let rec private innerLetSig (name: string) (jcol: int) (te: Check.TypedExpr) : string option =
    match Check.childExprs te |> List.tryPick (innerLetSig name jcol) with
    | Some s -> Some s
    | None ->
        match te.Kind with
        | Check.TELet(n, _, tvalue, _) when n = name && te.Span.Start.Col <= jcol && jcol < te.Span.End.Col ->
            Some(formatSignature n (lambdaParamNames tvalue) tvalue.Ty)
        | _ -> None

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
                        // LOCAL binders first — lexical, innermost wins
                        // (params, inner lets, pattern payload binders);
                        // the top-level scan is the fallback
                        // [PLAN-diagnostics-arc C]
                        match teOf chk |> Option.bind (localDef Map.empty n jcol) with
                        | Some(Some bspan) ->
                            let pl, pc = Script.translate useLl bspan.Start.Col
                            Some(pl, pc, n.Length)
                        | _ -> letSite useLl.Head n
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
                | Check.TEFromYaml(tyName, _) ->
                    wordAt useLl.Text jcol
                    |> Option.bind (fun w -> if w = tyName then typeSite tyName None else None)
                // `Env.load T` / `Args.load T`: the target TYPE name jumps to
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
                | _ -> None))

/// hover text at (1-based physical line, col), or None. Pure. TYPE first,
/// then the `///` doc. Silence guard first [D:hover-silence]; then the
/// type from the binder/param/typed-node/scheme, with a field IN A LITERAL
/// resolved to the FIELD's type (not the record's) [Group 1c]; then the
/// doc — at the cursor for a declaration, or at the RESOLVED declaration
/// site for a usage / field / case reference (definitionFor) [Group 1a],
/// else the builtin's [D:builtin-docs].
let hoverType (lines: string list) (line: int) (col: int) : string option =
    let _, stmts, _, _ = Script.analyzeLines "hover" lines

    match toLogical stmts line col with
    | Some(ll, chk, jcol) when not (onSilentToken ll.Text jcol) ->
        // an inner-let binder hovers as its ANNOTATED signature (names +
        // types), degrading to the arrow when it has no named params
        let binderSig =
            letBinderAt ll.Text jcol
            |> Option.bind (fun name -> teOf chk |> Option.bind (innerLetSig name jcol))

        let paramTy = teOf chk |> Option.bind (paramTypeAt jcol)
        let node = teOf chk |> Option.bind (fun te -> nodeAt te jcol)

        // Group 1c: a field NAME in a record literal `{ Field = … }` hovers
        // as the FIELD's type, not the record's (nodeAt sees only TERecord)
        let fieldInLiteral =
            match node with
            | Some { Kind = Check.TERecord(recName, _) } ->
                wordAt ll.Text jcol
                |> Option.bind (fun w ->
                    match Map.tryFind recName chk.Env.Types with
                    | Some(Record def) ->
                        def.Fields
                        |> List.tryPick (fun (f, fty) -> if f = w then Some(formatTy fty) else None)
                    | _ -> None)
            | _ -> None

        // Group 2: a union CASE — in a pattern (`| Pulled n ->`) or as a
        // value (`Pulled ctx`) — hovers as its constructor signature. Keyed
        // off the word (pattern positions are no expression node); skipped
        // at the type DECLARATION, where declHover renders it instead.
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

        // an EXACT use of a name (TEVar at the cursor) wins; else a pattern
        // PAYLOAD binder resolved from a use of it [Group 2]; else the
        // enclosing node's type; else the binder-name scheme / declaration.
        let exactUse =
            match node with
            | Some({ Kind = Check.TEVar vn } as n) when Some vn = word -> Some(formatTy n.Ty)
            | _ -> None

        // a builtin hovers as its annotated signature (names from the doc,
        // types from the node) [D:annotated-signature]. A zero-param value
        // (`Self.pid : int`) renders name-and-type with no parens; a FUNCTION
        // with no named params degrades to the arrow (fallback below).
        let builtinSig =
            node
            |> Option.bind (fun n ->
                match n.Kind with
                | Check.TEVar name when Map.containsKey name Builtins.builtinDocs ->
                    let d = Builtins.builtinDocs[name]

                    match n.Ty with
                    | TFun _ when List.isEmpty d.Params -> None // unnamed function -> arrow
                    | _ -> Some(formatSignature name d.Params n.Ty)
                | _ -> None)

        let ty =
            fieldInLiteral
            |> Option.orElse constructorSig
            |> Option.orElse binderSig
            |> Option.orElse builtinSig
            |> Option.orElseWith (fun () -> paramTy |> Option.map formatTy)
            |> Option.orElse exactUse
            |> Option.orElseWith (fun () ->
                word
                |> Option.bind (fun w -> teOf chk |> Option.bind (varUseType w))
                |> Option.map formatTy)
            |> Option.orElseWith (fun () -> node |> Option.map (fun n -> formatTy n.Ty))
            |> Option.orElseWith (fun () ->
                match chk.Kind with
                // a top-level let hovers as its annotated signature, ON the
                // binder name only — never a fallback for an unresolved spot
                | Script.KLet(name, sch, te) when word = Some name ->
                    Some(formatSignature name (lambdaParamNames te) sch.Ty)
                | Script.KType decl -> word |> Option.bind (declHover decl)
                | _ -> None)

        // the boundary-form nodes span the WHOLE form (`Env.load TokenEnv`),
        // so gate each to the word that names it — hovering the module `Env`
        // or the type argument must NOT surface `load`'s doc; only `load` does
        let nodeDoc =
            node
            |> Option.bind (fun n ->
                match n.Kind with
                | Check.TEVar name -> Some(Builtins.reifierSurface name |> Option.defaultValue name)
                | Check.TEEnvLoad _ when word = Some "load" -> Some "Env.load"
                | Check.TEArgsLoad _ when word = Some "load" -> Some "Args.load"
                | Check.TEFrom(fmt, _) when word = Some "from" || word = Some fmt -> Some $"from {fmt}"
                | Check.TEFromYaml _ when word = Some "from" || word = Some "yaml" -> Some "from yaml"
                | Check.TETo fmt when word = Some "to" || word = Some fmt -> Some $"to {fmt}"
                | _ -> None)
            |> Option.bind (fun key -> Map.tryFind key Builtins.builtinDocs)

        let wordDoc =
            wordAt ll.Text jcol |> Option.bind (fun w -> Map.tryFind w Builtins.builtinDocs)

        let builtinDoc =
            nodeDoc |> Option.orElse wordDoc |> Option.map Builtins.renderBuiltinDoc

        // the `///` doc: at the cursor for a DECLARATION site (half 1's key)
        let sourceDoc =
            Script.docAttachments lines
            |> List.tryPick (fun d ->
                if d.Line = line && d.Col <= col && col < d.Col + d.Len then
                    Some(String.concat "\n" d.Doc)
                else
                    None)

        // Group 1a: a usage / field / case REFERENCE resolves to its
        // declaration site (definitionFor), and the doc is read there —
        // shadowing falls out (definitionFor is innermost-wins)
        let usageDoc =
            match sourceDoc with
            | Some _ -> None
            | None ->
                definitionFor lines line col
                |> Option.bind (fun (dl, dc, _) ->
                    Script.docAttachments lines
                    |> List.tryPick (fun d ->
                        if d.Line = dl && d.Col = dc then
                            Some(String.concat "\n" d.Doc)
                        else
                            None))

        let doc = sourceDoc |> Option.orElse usageDoc |> Option.orElse builtinDoc

        match ty, doc with
        | Some t, Some d -> Some(t + "\n\n" + d) // type FIRST, then the doc
        | Some t, None -> Some t
        | None, Some d -> Some d
        | None, None -> None
    | _ -> None

// ---- the server ---------------------------------------------------

let run () : int =
    let stdin' = Console.OpenStandardInput()
    let docs = Collections.Generic.Dictionary<string, string>()

    // buffer-over-disk [D:modules-v1]: an imported file open in the editor is
    // read from its (possibly unsaved) buffer, else disk (decision 14)
    Script.importSourceOverride.Value <-
        Some(fun absPath ->
            match docs.TryGetValue(pathToUri absPath) with
            | true, text -> Some(text.Replace("\r\n", "\n").Split('\n') |> Array.toList)
            | _ ->
                if IO.File.Exists absPath then
                    Some(IO.File.ReadAllLines absPath |> Array.toList)
                else
                    None)

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

    // URIs published with diagnostics last cycle — cleared when they go clean
    let publishedUris = Collections.Generic.HashSet<string>()

    let publishFor (uri: string) (diags: Script.Diagnostic list) =
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
                w.WriteEndObject()

            w.WriteEndArray()
            w.WriteEndObject())

    // re-check EVERY open doc, publish PER URI [D:modules-v1]: a module's
    // diagnostics land on the module's own file (even unopened), and an
    // importer re-checks when a dependency it reads changes. Files that went
    // clean since last cycle are published empty (cleared).
    let refreshAll () =
        let byUri = Collections.Generic.Dictionary<string, ResizeArray<Script.Diagnostic>>()

        for kv in Seq.toList docs do
            let diags, _, _, _ = analyze kv.Key kv.Value

            for d in diags do
                let du = pathToUri d.File

                match byUri.TryGetValue du with
                | true, b -> b.Add d
                | _ ->
                    let b = ResizeArray()
                    b.Add d
                    byUri[du] <- b

        // one publish per relevant URI: a file with diagnostics, every OPEN
        // doc (empty if clean), and any previously-diagnosed file now clean
        let toPublish = Collections.Generic.Dictionary<string, Script.Diagnostic list>()

        for kv in byUri do
            // the same file can be diagnosed both directly (open) and as a
            // dependency of another open doc; dedup by position+message,
            // keeping the richer span (EndCol present)
            toPublish[kv.Key] <-
                (kv.Value
                 |> Seq.sortByDescending (fun d -> d.EndCol.IsSome)
                 |> Seq.distinctBy (fun d -> d.Line, d.Col, d.Severity, d.Code, d.Message)
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
                    // resolve relative-path command heads against the
                    // WORKSPACE ROOT, not the server's launch cwd — which
                    // the editor chooses and Zed/VS Code choose differently,
                    // so `ci/deep-lock.sh` was a command in one and an
                    // unbound var in the other. rootUri (or the first
                    // workspaceFolder) is a file:// URI; on absence keep cwd.
                    (jStr "rootUri" ps
                     |> Option.orElseWith (fun () -> jFirst "workspaceFolders" ps |> Option.bind (jStr "uri")))
                    |> Option.iter (fun u ->
                        try
                            Session.setCwd (System.Uri(u).LocalPath)
                        with _ ->
                            ())

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
                             // re-check all: this doc AND any open importer of it
                             refreshAll ()
                         | _ -> ()
                     | None -> ())
                | "textDocument/didChange" ->
                    (match jObj "textDocument" ps, jFirst "contentChanges" ps with
                     | Some td, Some change ->
                         match jStr "uri" td, jStr "text" change with
                         | Some uri, Some text ->
                             docs[uri] <- text
                             refreshAll ()
                         | _ -> ()
                     | _ -> ())
                | "textDocument/didClose" ->
                    jObj "textDocument" ps
                    |> Option.bind (jStr "uri")
                    |> Option.iter (fun uri ->
                        docs.Remove uri |> ignore
                        refreshAll ())
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
                        | Some(_, text), Some(line, col) ->
                            let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList

                            match hoverType lines line col with
                            | Some t ->
                                w.WriteStartObject()
                                w.WritePropertyName "contents"
                                w.WriteStartObject()
                                w.WriteString("kind", "plaintext")
                                w.WriteString("value", t)
                                w.WriteEndObject()
                                w.WriteEndObject()
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

                            // completion detail [D:doc-comments]: the `///`
                            // doc for a documented name. Name-keyed HERE (a
                            // completion item IS a name; last-wins on a shared
                            // name) — the position-keyed map stays for hover
                            let docByName =
                                Script.docAttachments (List.ofArray lines)
                                |> List.choose (fun d ->
                                    if d.Line - 1 < lines.Length && d.Col - 1 + d.Len <= lines[d.Line - 1].Length then
                                        Some(lines[d.Line - 1].Substring(d.Col - 1, d.Len), String.concat "\n" d.Doc)
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
                                let sigDetail =
                                    Map.tryFind label Builtins.builtinDocs
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

                                match sigDetail with
                                | Some s -> w.WriteString("detail", s)
                                | None -> ()

                                // a user `///` doc wins; else the builtin's
                                // doc (Seq.map, print, …) [D:builtin-docs]
                                match
                                    Map.tryFind label docByName
                                    |> Option.orElse (
                                        Map.tryFind label Builtins.builtinDocs |> Option.map Builtins.renderBuiltinDoc
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
                | _ ->
                    // unknown request: respond null; unknown notification: ignore
                    idStr |> Option.iter (fun id -> respond id (fun w -> w.WriteNullValue()))

    exitCode
