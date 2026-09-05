module Weir.Ast

open Weir.Types

type Pos = { Line: int; Col: int }

type Span = { Start: Pos; End: Pos }

module Span =
    let union (a: Span) (b: Span) = { Start = a.Start; End = b.End }

type Pattern = { PKind: PatternKind; PSpan: Span }

and PatternKind =
    | PWildcard
    | PVar of string
    | PBool of bool
    | PInt of int64
    | PStr of string
    | PUnit
    | PTuple of Pattern list
    // irrefutable record patterns [D:record-patterns]: field name +
    // its span (the provenance anchor) + the sub-pattern; partial
    // field mention is the point (the row's native semantics)
    | PRecord of ((string * Span) * Pattern) list
    | PCase of ctor: string * arg: Pattern option
    // the bespoke Regex pattern [D:regex-pattern] — one pattern kind,
    // NOT a general active-pattern mechanism. The literal is kept
    // verbatim (its backslashes belong to the regex engine); litSpan
    // aims check errors at the literal, not the whole pattern.
    | PRegex of pattern: string * litSpan: Span * raw: bool * binder: Pattern
    // seq patterns [D:seq-patterns]: F#'s list spelling, seq semantics,
    // bounded force at the match site
    | PSeqNil
    | PCons of head: Pattern * tail: Pattern
    | PSeqList of elems: Pattern list

type InterpPart<'e> =
    | IStr of string
    | IExpr of 'e

// the `within` kinds as DATA — one table, three consumers (the
// parser's dispatch, hover, completion) [D:within-kinds]. Binds is the
// form's central asymmetry: tmp PRODUCES a binder, cd/env CONSUME an
// atom. The editor grammars list the same closed set by necessity
// (separate files); the inventory guard pins them to this table.
type WithinKind =
    { Name: string
      Binds: bool
      Doc: string }

let withinKinds: WithinKind list =
    [ { Name = "tmp"
        Binds = true
        Doc = "a fresh directory, removed when the block exits" }
      { Name = "cd"
        Binds = false
        Doc = "the working directory for the block, restored after" }
      { Name = "env"
        Binds = false
        Doc = "an environment overlay for the block's children" }
      // the no-orphan law [D:scoped-procs]: the scope IS the lifetime
      { Name = "proc"
        Binds = true
        Doc = "a background process, tree-killed and reaped when the block exits" }
      // advisory file lock [D:within-lock] — the one kind whose
      // guarantee survives kill -9 (the kernel releases it)
      { Name = "lock"
        Binds = false
        Doc = "an advisory file lock, held for the block, released on every exit (kill -9 included)" } ]

/// "tmp, cd, or env" — the teaching list, derived so a new kind
/// cannot miss the message
let withinKindList =
    match withinKinds |> List.map (fun k -> k.Name) with
    | [] -> ""
    | [ one ] -> one
    | names ->
        let front = names |> List.take (names.Length - 1) |> String.concat ", "
        $"{front}, or {List.last names}"

/// the exit-code discard teaching [D:exit-reifiers], ONE string for every
/// site that refuses a discarded `| exitCode`: the parser's sigil and
/// sequence guards and the checker's else-less if. Shared because the copies
/// drifted — two spellings of the dash across four sites, none of them pinned
let exitCodeDiscardMsg =
    "this discards the exit code — bind it (let rc = <command> | exitCode), match on it, or drop '| exitCode'"

// the adapter type slot's payload [D:anon-records]: a declared NAME,
// or an anonymous field list `{| f: ty; … |}` (adapter slot only —
// there is no anonymous literal, and tySyn does not nest it)
type FromShape =
    | FromName of string
    | FromAnon of fields: (string * Ty) list
    // the ID-keyed object [D:map-string]: Map<string, Name|{|…|}> in
    // the adapter slot — the inner shape is a record, string keys only
    | FromMap of inner: FromShape

type Expr = { Kind: ExprKind; Span: Span }

and ExprKind =
    | EInt of value: int64
    // a duration literal: 30s / 250ms, stored as ms [D:duration]
    | EDur of ms: int64
    | EFloat of value: float
    | ESize of bytes: int64
    // retry/poll [D:retry-poll]: a two-segment compound — options
    // record, block body yielding 'a, optional `until` binder+predicate
    // block (absent = the body IS the predicate, form yields unit)
    | ERetry of
        poll: bool *
        opts: Expr *
        // poll's watched handle [D:scoped-procs]: a HEAD key, never an
        // options field — a live handle is per-call, not configuration,
        // so the record form deliberately cannot spell it
        watch: Expr option *
        body: Expr *
        until: ((string * Span) * Expr) option
    | EStr of string
    | EBool of bool
    | EUnit
    | EVar of string
    | ELet of name: string * nameSpan: Span * value: Expr * body: Expr
    | ELambda of param: string * paramSpan: Span * body: Expr
    | EApp of fn: Expr * arg: Expr
    | EPipe of arg: Expr * fn: Expr
    | EField of target: Expr * field: string * fieldSpan: Span
    | EBinOp of op: string * left: Expr * right: Expr
    // an operator as a VALUE, unapplied only [D:operator-values] —
    // the checker desugars it to `fun a b -> a op b` verbatim
    | EOpValue of op: string
    | ERecord of fields: (string * Span * Expr) list
    // an anonymous record literal [D:anon-literals] — typed as its
    // canonical synthetic-nominal name; the TYPED node is TERecord
    | EAnonRecord of fields: (string * Span * Expr) list
    | EMatch of scrutinee: Expr * arms: (Pattern * Expr option * Expr) list
    | EIf of cond: Expr * thn: Expr * els: Expr option
    | ESeq of first: Expr * rest: Expr
    | EFrom of format: string * shape: FromShape option * seqOf: bool * streamOf: bool
    | ETo of format: string
    | EList of items: Expr list
    | ETuple of items: Expr list
    | ELetPat of binder: Pattern * value: Expr * body: Expr
    | ELambdaPat of binder: Pattern * body: Expr
    // within <kind> … + block [D:within-scopes]: a scoped resource.
    // The kinds are ASYMMETRIC by design: tmp PRODUCES a path (binder,
    // no arg); cd and env CONSUME one (arg, no binder) — which is why
    // the form is `within <kind> <args…>`, not one fixed shape
    | EWithin of kind: string * binder: (string * Span) option * arg: Expr option * opts: Expr option * body: Expr
    // the bare scope [D:within-always]: no resource, just the exit
    // discipline — body, then the always block on EVERY exit path
    | EAlways of body: Expr * cleanup: Expr
    // the $() capture assertion [D:district-retirement]: $() means
    // CAPTURE in every position — the wrapper marks the chain so
    // statement arming never touches it; erased at check
    | ECapture of Expr
    | ECmd of prog: string * args: Expr list * env: Expr option
    // $@xs / $@(expr) — N argv words [D:argv-splat]
    | ESplat of Expr
    // copy-and-update [D:record-update]: paths carry nested sugar
    // (I.X); the checker walks them, eval overlays — source ONCE
    | EUpdate of source: Expr * updates: ((string * Span) list * Expr) list
    | EInterp of parts: InterpPart<Expr> list
    // the yaml district [D:yaml-district]: a checked block literal — the
    // template tree parsed at CHECK time; splices and `for` sources are
    // ordinary Exprs, so typing/hover/eval ride existing machinery
    | EYaml of tpl: YamlTpl * schema: string option

and YamlTpl =
    | YtScalar of raw: string * quoted: bool * span: Span
    // a literal block scalar [D:block-scalars]: content is BYTES,
    // consumed before the splice/for scanners run, already chomped
    | YtBlock of text: string * span: Span
    | YtSplice of Expr
    | YtSeq of items: YamlTplItem list * span: Span
    | YtMap of entries: YamlTplEntry list * span: Span

and YamlTplEntry =
    | YtPair of key: YamlTplKey * value: YamlTpl
    // `for p in xs` under a MAPPING: the body yields entries per element
    | YtForEntries of binder: Pattern * source: Expr * body: YamlTplEntry list

and YamlTplItem =
    | YtItem of YamlTpl
    // `for p in xs` under a SEQUENCE: the body yields items per element
    | YtForItems of binder: Pattern * source: Expr * body: YamlTplItem list

and YamlTplKey =
    // the key SPAN feeds schema validation's located errors [D:yaml-schemas]
    | YtKeyLit of string * span: Span
    | YtKeySplice of Expr

// [<Name arg>] attachment [D:attributes] — check-time, fully erased
type AttrSpec =
    { AName: string
      AArg: AttrArg option
      ASpan: Span }

type DeclBody =
    | DRecord of fields: (string * Ty * AttrSpec list) list
    | DUnion of cases: (string * Ty option * AttrSpec list) list

type Decl =
    { Name: string
      Params: string list
      Body: DeclBody
      // declaration-level attributes [D:attr-positions] — union decls
      // host [<Tag>]; validated at checkDecl, bound at consumption
      Attrs: AttrSpec list
      Span: Span }

// every Expr embedded in a yaml template — splices, key splices, and
// `for` sources; tooling walks reach inside districts through this
let rec yamlTplExprs (tpl: YamlTpl) : Expr list =
    match tpl with
    | YtScalar _ -> []
    | YtBlock _ -> []
    | YtSplice e -> [ e ]
    | YtSeq(items, _) ->
        items
        |> List.collect (function
            | YtItem t -> yamlTplExprs t
            | YtForItems(_, src, body) ->
                src
                :: (body
                    |> List.collect (fun i -> yamlTplExprs (YtSeq([ i ], Unchecked.defaultof<Span>)))))
    | YtMap(entries, _) ->
        entries
        |> List.collect (function
            | YtPair(YtKeyLit _, v) -> yamlTplExprs v
            | YtPair(YtKeySplice k, v) -> k :: yamlTplExprs v
            | YtForEntries(_, src, body) ->
                src
                :: (body
                    |> List.collect (fun e -> yamlTplExprs (YtMap([ e ], Unchecked.defaultof<Span>)))))

// the pattern tree's child list — the depth gate walks patterns the
// way it walks expressions (a bare cons chain is a spine that parses
// shallow) [D:depth-guard]
let patChildren (p: Pattern) : Pattern list =
    match p.PKind with
    | PWildcard
    | PVar _
    | PBool _
    | PInt _
    | PStr _
    | PUnit
    | PSeqNil -> []
    | PTuple ps
    | PSeqList ps -> ps
    | PRecord fields -> fields |> List.map snd
    | PCase(_, arg) -> Option.toList arg
    | PRegex(_, _, _, binder) -> [ binder ]
    | PCons(h, t) -> [ h; t ]

// every Pattern embedded in a yaml template — the `for` binders
let rec yamlTplPats (tpl: YamlTpl) : Pattern list =
    match tpl with
    | YtScalar _
    | YtBlock _
    | YtSplice _ -> []
    | YtSeq(items, _) ->
        items
        |> List.collect (function
            | YtItem t -> yamlTplPats t
            | YtForItems(b, _, body) ->
                b
                :: (body
                    |> List.collect (fun i -> yamlTplPats (YtSeq([ i ], Unchecked.defaultof<Span>)))))
    | YtMap(entries, _) ->
        entries
        |> List.collect (function
            | YtPair(_, v) -> yamlTplPats v
            | YtForEntries(b, _, body) ->
                b
                :: (body
                    |> List.collect (fun e -> yamlTplPats (YtMap([ e ], Unchecked.defaultof<Span>)))))

// the patterns attached directly to an expression node — the depth
// gate pairs this with exprChildren to cover both trees
let exprPats (e: Expr) : Pattern list =
    match e.Kind with
    | EMatch(_, arms) -> arms |> List.map (fun (p, _, _) -> p)
    | ELetPat(p, _, _)
    | ELambdaPat(p, _) -> [ p ]
    | EYaml(tpl, _) -> yamlTplPats tpl
    | _ -> []

// the expression tree's child list — tooling walks share this (the
// TypedExpr twin lives in Check.childExprs)
let exprChildren (e: Expr) : Expr list =
    match e.Kind with
    | EInt _
    | EDur _
    | EFloat _
    | ESize _
    | EStr _
    | EBool _
    | EUnit
    | EVar _
    | EFrom _
    | ETo _ -> []
    | ELet(_, _, v, b) -> [ v; b ]
    | ELetPat(_, v, b) -> [ v; b ]
    | ELambda(_, _, b) -> [ b ]
    | ELambdaPat(_, b) -> [ b ]
    | EWithin(_, _, arg, opts, b) -> Option.toList arg @ Option.toList opts @ [ b ]
    | EAlways(b, c) -> [ b; c ]
    | ECapture e -> [ e ]
    | EApp(f, x) -> [ f; x ]
    | EPipe(x, f) -> [ x; f ]
    | EField(t, _, _) -> [ t ]
    | EBinOp(_, l, r) -> [ l; r ]
    | EOpValue _ -> []
    | ERecord fields
    | EAnonRecord fields -> fields |> List.map (fun (_, _, v) -> v)
    | EMatch(s, arms) -> s :: (arms |> List.collect (fun (_, g, b) -> (g |> Option.toList) @ [ b ]))
    | EIf(c, t, e) -> c :: t :: Option.toList e
    | ESeq(a, b) -> [ a; b ]
    | EList items -> items
    | ETuple items -> items
    | ECmd(_, args, envO) -> args @ Option.toList envO
    | ESplat e -> [ e ]
    | EUpdate(src, ups) -> src :: (ups |> List.map snd)
    | ERetry(_, opts, watch, body, until) ->
        [ opts ]
        @ Option.toList watch
        @ [ body ]
        @ (until |> Option.map (snd >> List.singleton) |> Option.defaultValue [])
    | EInterp parts ->
        parts
        |> List.choose (function
            | IExpr e -> Some e
            | IStr _ -> None)
    | EYaml(tpl, _) -> yamlTplExprs tpl

type Stmt =
    | SLet of name: string * value: Expr
    | SLetPat of binder: Pattern * value: Expr
    | SExpr of Expr
    | SCmd of Expr
    | SType of Decl
    // the module marker [D:modules-v1] — `module` (name from filename) or
    // `module Name`; kwSpan aims the running-a-module and ordering errors
    | SModule of name: string option * kwSpan: Span
    // `import "path"` / `import "path" as Name` [D:modules-v1] — path is a
    // literal string; alias (uppercase) is the namespace override
    | SImport of path: string * pathSpan: Span * alias: (string * Span) option

// Span-free sexpr rendering — the parse-SHAPE language. Two consumers:
// the test suite's parse pins and fmt's respace safety check (a
// formatted statement must sexpr-match its original) [D:fmt-respace].
let rec sexprPat (p: Pattern) : string =
    match p.PKind with
    | PWildcard -> "_"
    | PBool b -> if b then "true" else "false"
    | PInt n -> string n
    | PStr s -> $"\"{s}\""
    | PUnit -> "()"
    | PVar x -> x
    | PTuple ps -> "(" + (ps |> List.map sexprPat |> String.concat ", ") + ")"
    | PRecord fields ->
        "{ "
        + (fields
           |> List.map (fun ((f, _), sub) -> $"{f} = {sexprPat sub}")
           |> String.concat "; ")
        + " }"
    | PCase(c, None) -> c
    | PCase(c, Some arg) -> $"({c} {sexprPat arg})"
    | PRegex(pat, _, _, binder) -> $"(regex \"{pat}\" {sexprPat binder})"
    | PSeqNil -> "[]"
    | PCons(h, t) -> $"({sexprPat h} :: {sexprPat t})"
    | PSeqList ps -> "[" + (ps |> List.map sexprPat |> String.concat "; ") + "]"

let rec sexpr (e: Expr) : string =
    match e.Kind with
    | EInt n -> string n
    | EDur n -> formatDuration n
    | EFloat f -> formatFloat f
    | ESize b -> formatSize b
    | ERetry(isPoll, opts, watch, body, until) ->
        let head = if isPoll then "poll" else "retry"

        let w =
            watch |> Option.map (fun e -> $" (watch {sexpr e})") |> Option.defaultValue ""

        match until with
        | Some((b, _), pred) -> $"({head} {sexpr opts}{w} {sexpr body} (until {b} {sexpr pred}))"
        | None -> $"({head} {sexpr opts}{w} {sexpr body})"
    | EStr s -> $"\"{s}\""
    | EBool b -> if b then "true" else "false"
    | EVar x -> x
    | EUnit -> "()"
    | ELet(n, _, v, b) -> $"(let {n} {sexpr v} {sexpr b})"
    | ELetPat(p, v, b) -> $"(letpat {sexprPat p} {sexpr v} {sexpr b})"
    | ELambda(p, _, b) -> $"(fun {p} {sexpr b})"
    | ELambdaPat(p, b) -> $"(funpat {sexprPat p} {sexpr b})"
    | EWithin(k, binder, arg, opts, b) ->
        let bn = binder |> Option.map fst |> Option.defaultValue ""
        let av = arg |> Option.map sexpr |> Option.defaultValue ""

        let ov =
            opts |> Option.map (fun o -> $"timeout={sexpr o}") |> Option.defaultValue ""

        let ba = [ bn; av; ov ] |> List.filter ((<>) "") |> String.concat " "
        $"(within {k} {ba} {sexpr b})"
    | EAlways(b, c) -> $"(within {sexpr b} (always {sexpr c}))"
    | ECapture e -> $"(capture {sexpr e})"
    | EApp(f, a) -> $"({sexpr f} {sexpr a})"
    | EPipe(a, f) -> $"({sexpr a} |> {sexpr f})"
    | EField(t, f, _) -> $"{sexpr t}.{f}"
    | EBinOp(op, l, r) -> $"({op} {sexpr l} {sexpr r})"
    | EOpValue op -> $"({op})"
    | ERecord fields ->
        let body =
            fields |> List.map (fun (n, _, v) -> $"{n} = {sexpr v}") |> String.concat "; "

        "{" + body + "}"
    | EAnonRecord fields ->
        let body =
            fields |> List.map (fun (n, _, v) -> $"{n} = {sexpr v}") |> String.concat "; "

        "{|" + body + "|}"
    | EUpdate(src, ups) ->
        let body =
            ups
            |> List.map (fun (path, v) -> (path |> List.map fst |> String.concat ".") + $" = {sexpr v}")
            |> String.concat "; "

        "{" + sexpr src + " with " + body + "}"
    | EMatch(scrut, arms) ->
        let showArm (p, g, b) =
            match g with
            | None -> $"[{sexprPat p} -> {sexpr b}]"
            | Some g -> $"[{sexprPat p} when {sexpr g} -> {sexpr b}]"

        let armsStr = arms |> List.map showArm |> String.concat " "
        $"(match {sexpr scrut} {armsStr})"
    | EIf(c, t, None) -> $"(if {sexpr c} {sexpr t})"
    | EIf(c, t, Some e) -> $"(if {sexpr c} {sexpr t} {sexpr e})"
    | ESeq(a, b) -> $"(seq {sexpr a} {sexpr b})"
    | EFrom(fmt, None, _, _) -> $"(from {fmt})"
    | EFrom(fmt, Some(FromName ty), false, true) -> $"(from {fmt} stream {ty})"
    | EFrom(fmt, Some(FromName ty), false, _) -> $"(from {fmt} {ty})"
    | EFrom(fmt, Some(FromName ty), true, _) -> $"(from {fmt} seq<{ty}>)"
    | EFrom(fmt, Some(FromAnon fields), s, _) ->
        let shape = anonRecordName fields

        if s then
            $"(from {fmt} seq<{shape}>)"
        else
            $"(from {fmt} {shape})"
    | EFrom(fmt, Some(FromMap inner), _, _) ->
        let shape =
            match inner with
            | FromName n -> n
            | FromAnon fields -> anonRecordName fields
            | FromMap _ -> "…"

        $"(from {fmt} Map<string, {shape}>)"
    | ETo fmt -> $"(to {fmt})"
    | EList items ->
        let body = items |> List.map sexpr |> String.concat "; "
        $"[{body}]"
    | ETuple items -> "(tuple " + (items |> List.map sexpr |> String.concat ", ") + ")"
    | EInterp parts ->
        let body =
            parts
            |> List.map (function
                | IStr s -> $"\"{s}\""
                | IExpr e -> "{" + sexpr e + "}")
            |> String.concat ""

        $"(interp {body})"
    | ESplat e -> $"(splat {sexpr e})"
    | ECmd(prog, [], None) -> $"(cmd {prog})"
    | ECmd(prog, args, None) ->
        let body = args |> List.map sexpr |> String.concat " "
        $"(cmd {prog} {body})"
    | ECmd(prog, args, Some envE) ->
        let body = args |> List.map sexpr |> String.concat " "
        $"(cmdenv {sexpr envE} {prog} {body})"
    | EYaml(tpl, schema) ->
        let s =
            match schema with
            | Some n -> $" schema={n}"
            | None -> ""

        $"(yaml{s} {sexprYamlTpl tpl})"

and sexprYamlTpl (tpl: YamlTpl) : string =
    let space = " "

    match tpl with
    | YtScalar(raw, quoted, _) ->
        let q = "\""
        if quoted then q + raw + q else raw
    | YtBlock(text, _) ->
        let escaped = text.Replace("\n", "\\n")
        "(block " + escaped + ")"
    | YtSplice e ->
        let inner = sexpr e
        "$(" + inner + ")"
    | YtSeq(items, _) ->
        let body =
            items
            |> List.map (function
                | YtItem t -> sexprYamlTpl t
                | YtForItems(p, src, body) ->
                    let b =
                        body
                        |> List.map (fun i -> sexprYamlTpl (YtSeq([ i ], Unchecked.defaultof<Span>)))
                        |> String.concat space

                    let hd = sexprPat p
                    let srcS = sexpr src
                    "(for " + hd + " in " + srcS + " " + b + ")")
            |> String.concat "; "

        "[" + body + "]"
    | YtMap(entries, _) ->
        let body =
            entries
            |> List.map (function
                | YtPair(YtKeyLit(k, _), v) -> k + ": " + sexprYamlTpl v
                | YtPair(YtKeySplice e, v) ->
                    let ks = sexpr e
                    "$(" + ks + "): " + sexprYamlTpl v
                | YtForEntries(p, src, body) ->
                    let b =
                        body
                        |> List.map (fun e -> sexprYamlTpl (YtMap([ e ], Unchecked.defaultof<Span>)))
                        |> String.concat space

                    let hd = sexprPat p
                    let srcS = sexpr src
                    "(for " + hd + " in " + srcS + " " + b + ")")
            |> String.concat "; "

        "{" + body + "}"

let sexprStmt (s: Stmt) : string =
    match s with
    | SLet(n, e) -> $"(slet {n} {sexpr e})"
    | SLetPat(p, e) -> $"(sletpat {sexprPat p} {sexpr e})"
    | SExpr e -> $"(sexpr {sexpr e})"
    | SCmd e -> $"(scmd {sexpr e})"
    | SType d -> $"(stype {d.Name})"
    | SModule(None, _) -> "(module)"
    | SModule(Some n, _) -> $"(module {n})"
    | SImport(path, _, None) -> $"(import \"{path}\")"
    | SImport(path, _, Some(a, _)) -> $"(import \"{path}\" as {a})"
