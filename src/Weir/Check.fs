module Weir.Check

open Weir.Ast
open Weir.Types

// Origin [D:row-provenance]: a physical (line, col, len) override for
// where the error points — set when the true cause lives in another
// statement, whose logical spans this statement cannot translate.
type TypeError = {
    Span: Span
    Message: string
    Origin: (int * int * int) option
}

let formatError (e: TypeError) : string =
    $"[{e.Span.Start.Line}:{e.Span.Start.Col}-{e.Span.End.Col}] type error: {e.Message}"

type Warning = { Span: Span; Message: string }

let formatWarning (w: Warning) : string =
    $"[{w.Span.Start.Line}:{w.Span.Start.Col}-{w.Span.End.Col}] warning: {w.Message}"

// The logical-col -> (physLine, physCol) translator for the statement
// being checked [D:row-provenance]: Script sets it around each
// checkStatement; None for non-statement consumers (Complete, tests),
// where provenance is not recorded.
let toPhys: System.Threading.ThreadLocal<(int -> int * int) option> =
    new System.Threading.ThreadLocal<_>(fun () -> None)

// Args.load targets [D:typed-argv] — a record is the flags shape, a
// union is the subcommand entry point (case -> payload def)
type ArgsTarget =
    | ArgsRecord of RecordDef
    | ArgsUnion of def: UnionDef * payloads: Map<string, RecordDef>
    // shared flags by containment [D:shared-flags]: the outer record's
    // scalar fields are shared flags; its single union-typed field is
    // the subcommand slot
    | ArgsShared of outer: RecordDef * unionField: string * udef: UnionDef * payloads: Map<string, RecordDef>

// retired names teach their replacement [D:seq-force] — one table
// serves both lookup sites
let private retiredMember (m: string) (field: string) : string option =
    match m, field with
    | "Seq", "toList" -> Some "weir has no list type; 'Seq.freeze' is the materializer"
    // renamed [D:freeze-rename]: the durable fact is the frozen
    // snapshot (pulls never re-run); 'force' named only the moment
    | "Seq", "force" -> Some "renamed 'Seq.freeze' — the result is a frozen snapshot (pulls never re-run)"
    | "Option", "defaultTo" -> Some "renamed 'Option.defaultValue' (F# parity); a lazy default is 'Option.defaultWith'"
    // one operation, one name [D:seq-gaps]: the F# spelling wins where
    // one exists (collect); otherwise the weir spelling stays (where)
    | "Seq", "flatMap" -> Some "F# parity names it 'Seq.collect'"
    | "Seq", "filter" -> Some "weir's filter is 'Seq.where' — one name per operation"
    // a reversed preference [D:first-retired]: the synonym's
    // readability argument still holds but is outweighed by the same
    // one-name rule that retired filter
    | "Seq", "first" -> Some "weir's first is 'Seq.take' — one name per operation"
    | _ -> None

// retired fields teach their replacement at the access site
// [D:filerow] — "no field" on a renamed field is the wrong lesson
let private retiredField (record: string) (field: string) : string option =
    match record, field with
    | "FileRow", "isDirectory" ->
        Some
            "replaced by 'kind' — a FileKind fact, not a bool answer: f.kind == Directory (cases: Regular | Directory | Symlink)"
    | "FileRow", "readOnly" ->
        Some
            "removed — it was one permission bit dressed as a column; File.mode p gives the whole string (rw-r--r--), None on Windows"
    | _ -> None

let private retiredBare (name: string) : string option =
    match name with
    | "toList" -> Some "weir has no list type; 'freeze' is the materializer"
    | "force" ->
        Some "renamed 'freeze' (bare; qualified: Seq.freeze) — the result is a frozen snapshot (pulls never re-run)"
    | "defaultTo" -> Some "renamed: use 'Option.defaultValue' (or 'Option.defaultWith' for a thunk)"
    | "first" -> Some "weir's first is 'Seq.take' (bare: 'take') — one name per operation"
    | _ -> None

// the bare-name inverse [D:structured-fixes]: (bare, qualified) read
// back from the two bare-partition teachings — the module-qualified
// arm below and the parser's "spell it" form [D:bare-partition]
let qualifyTarget (msg: string) : (string * string) option =
    let m =
        System.Text.RegularExpressions.Regex.Match(
            msg,
            "^'(\\w+)' is (?:a bare module member(?:, not a program)? — spell it|module-qualified; use) '(\\w+\\.\\w+)'"
        )

    if m.Success then
        Some(m.Groups[1].Value, m.Groups[2].Value)
    else
        None

/// Members that live as bespoke checker arms, not as entries in a
/// module's member map: `Args.load`/`Env.load` resolve a record/union
/// type name, so their scheme is not a plain member. Completion must
/// still offer them, and the "no member" error must read them as
/// known-but-misused, not a typo — one source keeps the two in step.
let specialModuleMembers: Map<string, string list> =
    Map [ "Args", [ "load" ]; "Env", [ "load" ] ]

let private isSpecialMember (m: string) (field: string) : bool =
    specialModuleMembers
    |> Map.tryFind m
    |> Option.defaultValue []
    |> List.contains field

// the adapter slot's top level [D:wire-unions]: a declared record, or
// a tagged union dispatching on its wire discriminator
type JsonTop =
    | TopRec of RecordDef
    | TopUnion of UnionDef
    // a non-record Map value under `from json Map<string, V>`
    // [D:map-values]: each key's value reads as a field of V would
    | TopValue of Ty

/// which value a slice runs on [D:range-slicing] — the type-directed
/// choice the checker resolved, so the evaluator dispatches without
/// re-inspecting: substring, subsequence, or sub-bytes
type SliceOn =
    | SliceStr
    | SliceSeq
    | SliceBytes

type TypedExpr = { Kind: TypedKind; Ty: Ty; Span: Span }

and TypedKind =
    | TEInt of value: int64
    | TEStr of string
    | TEBool of bool
    | TEUnit
    // the recovery pass's stand-in for a block element that failed to
    // check [D:block-recovery] — tooling-only, so eval never sees one
    | TEHole of Span
    | TEVar of string
    | TELet of name: string * nameSpan: Span * value: TypedExpr * body: TypedExpr
    | TELambda of param: string * paramSpan: Span * body: TypedExpr
    | TEApp of fn: TypedExpr * arg: TypedExpr
    | TEPipe of arg: TypedExpr * fn: TypedExpr
    | TEField of target: TypedExpr * field: string
    // a slice [D:range-slicing]; `on` picks the eval (substring,
    // subsequence, or sub-bytes) — the type-directed choice the checker
    // resolved
    | TESlice of
        target: TypedExpr *
        lo: TypedExpr option *
        hi: TypedExpr option *
        on: SliceOn *
        loFromEnd: bool *
        hiFromEnd: bool
    | TEBinOp of op: string * left: TypedExpr * right: TypedExpr
    | TERecord of record: string * fields: (string * TypedExpr) list
    | TEMatch of scrutinee: TypedExpr * arms: (Pattern * TypedExpr option * TypedExpr) list
    | TEIf of cond: TypedExpr * thn: TypedExpr * els: TypedExpr option
    | TEDur of ms: int64
    | TEFloat of value: float
    | TERetry of
        kind: LoopKind *
        opts: TypedExpr *
        watch: TypedExpr option *
        body: TypedExpr *
        until: ((string * Span) * TypedExpr) option
    | TESize of bytes: int64
    | TESeq of first: TypedExpr * rest: TypedExpr
    | TEWithin of
        kind: WithinKindId *
        binder: (string * Span) option *
        arg: TypedExpr option *
        opts: TypedExpr option *
        body: TypedExpr
    | TEAlways of body: TypedExpr * cleanup: TypedExpr
    | TEHold of body: TypedExpr
    | TEEnvLoad of def: RecordDef * enums: Map<string, string list>
    | TEArgsLoad of target: ArgsTarget
    | TEFrom of
        format: string *
        top: JsonTop *
        defs: Map<string, RecordDef> *
        // tagged unions reachable from the declared shape [D:wire-unions]
        // — the reader's dispatch tables (eval has no env.Types)
        udefs: Map<string, UnionDef> *
        seqOf: bool *
        mapOf: bool
    // from yaml T [D:yaml-v1]: eval has no env.Types, so the checker packs
    // the resolved target tree (the pattern [D:env-enums] set)
    | TEFromYaml of tyName: string * shape: Yaml.Shape * stream: bool
    // from toml T [D:from-toml]: the yaml shape, read through the toml parser
    | TEFromToml of tyName: string * shape: Yaml.Shape
    // `e |> try [result]` [D:try-form]: e evaluated inside a capture
    | TETry of body: TypedExpr * asResult: bool
    // `e |> orFail [msg]` [D:orfail-form]: `site` is a unit node at the
    // form, so a raise there has a mapped position
    | TEOrFail of body: TypedExpr * message: TypedExpr option * site: TypedExpr
    | TETo of
        format: string *
        renames: Map<string, Map<string, string>> *
        // record -> field -> declared codec [D:wire-codecs], the rename
        // table's sibling (attrs never reach values)
        codecs: Map<string, Map<string, Codec>> *
        // caseName -> its WireCase for every tagged or validated
        // union reachable from the serialized type [D:wire-unions] —
        // a VUnion value carries no type name, so the writer keys cases
        unions: Map<string, WireCase> *
        // `to yaml stream` [D:yaml-seq-doc]: one document per element
        stream: bool *
        // the yaml writer's type guide [D:yaml-empty-typed]: the
        // to-side's shape of one document (None when it has no read
        // shape, and for json)
        yamlShape: Yaml.Shape option
    | TEList of items: TypedExpr list
    | TECmd of head: TCmdHead * args: TypedExpr list * env: TypedExpr option
    | TESplat of TypedExpr
    | TEUpdate of source: TypedExpr * updates: (string list * TypedExpr) list
    | TETuple of TypedExpr list
    | TELetPat of binder: Pattern * value: TypedExpr * body: TypedExpr
    | TELambdaPat of binder: Pattern * body: TypedExpr
    | TEInterp of parts: InterpPart<TypedExpr> list
    // the yaml district's typed template [D:yaml-district]; patchBy
    // [D:yaml-nodes]: Some by = a `yaml patch` district (YamlPatch)
    | TEYaml of TypedYamlTpl * schema: string option * patchBy: string option option

// the typed command head [D:dynamic-head]: literal, or a `^$`-spliced
// value (string-typed) resolved at run — display keeps the source
// spelling for messages
and TCmdHead =
    | THeadLit of string
    | THeadDyn of display: string * head: TypedExpr

and TypedYamlTpl =
    | TYtScalar of raw: string * quoted: bool * span: Ast.Span
    // literal block scalar content [D:block-scalars] — bytes, no checking
    | TYtBlock of text: string * span: Ast.Span
    | TYtSplice of TypedExpr
    | TYtSeq of TypedYamlTplItem list * span: Ast.Span
    | TYtMap of TypedYamlTplEntry list * span: Ast.Span
    // the tombstone [D:yaml-nodes] — patch districts only, checker-scoped
    | TYtDrop of span: Ast.Span

and TypedYamlTplEntry =
    | TYtPair of TypedYamlKey * TypedYamlTpl
    | TYtForEntries of binder: Pattern * source: TypedExpr * body: TypedYamlTplEntry list

and TypedYamlTplItem =
    | TYtItem of TypedYamlTpl
    | TYtForItems of binder: Pattern * source: TypedExpr * body: TypedYamlTplItem list
    | TYtDropItem of TypedYamlTpl * span: Ast.Span

and TypedYamlKey =
    | TYtKeyLit of string * span: Ast.Span
    | TYtKeySplice of TypedExpr

/// a head's message name [D:dynamic-head]: the program, or the `^$`
/// source spelling
let theadDisplay (h: TCmdHead) : string =
    match h with
    | THeadLit p -> p
    // a typed home path carries no `^` [D:tilde]
    | THeadDyn(d, _) when d.StartsWith "~/" -> d
    | THeadDyn(d, _) -> $"^{d}"

type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return x = Ok x
    member _.ReturnFrom(r: Result<_, _>) = r

let private result = ResultBuilder()

let private err (span: Span) (msg: string) : Result<'a, TypeError> =
    Error {
        Span = span
        Message = normalizeDiagVars msg
        Origin = None
    }

// the tombstone scope law [D:yaml-nodes]: only a `yaml patch` district
// admits $- — a plain district refuses at the tombstone's own span
let rec private firstTombstone (tpl: YamlTpl) : Span option =
    match tpl with
    | YtDrop sp -> Some sp
    | YtScalar _
    | YtBlock _
    | YtSplice _ -> None
    | YtSeq(items, _) ->
        items
        |> List.tryPick (function
            | YtDropItem(_, sp) -> Some sp
            | YtItem t -> firstTombstone t
            | YtForItems(_, _, body) ->
                body
                |> List.tryPick (fun i -> firstTombstone (YtSeq([ i ], Unchecked.defaultof<Span>))))
    | YtMap(entries, _) ->
        entries
        |> List.tryPick (function
            | YtPair(_, v) -> firstTombstone v
            | YtForEntries(_, _, body) ->
                body
                |> List.tryPick (fun e -> firstTombstone (YtMap([ e ], Unchecked.defaultof<Span>))))

// the unwrap teaching's tail — fixOf reads it back [D:structured-fixes]
let orFailTail = " — `|> orFail` unwraps it (raising on None/Error)"

// the wrapped value where its payload is wanted [D:structured-fixes]:
// the unwrap is one pipe away, so the message names it
let private unwrapHint (expected: Ty) (actual: Ty) =
    match expected, actual with
    | t, TNamed("Option", [ inner ])
    | t, TNamed("Result", [ inner; _ ]) when inner = t -> orFailTail
    | _ -> ""

let private mismatch (span: Span) (expected: Ty) (actual: Ty) =
    // a built request where a URL string is expected — name the two
    // run spellings instead of leaving a bare type mismatch
    // [D:fetch-naming] [D:fetch-retired]
    let hint =
        match expected, actual with
        | TStr, TNamed("HttpRequest", _) ->
            " — a request runs through Http.expect (raising) or Http.send (status as data)"
        | _ -> unwrapHint expected actual

    // a raw generated var never reaches a message [D:user-language-messages]:
    // both sides rename together, in first-appearance order ('a, 'b, …)
    let expected, actual =
        match normalizeEchoVars (TTuple [ expected; actual ]) with
        | TTuple [ e; a ] -> e, a
        | _ -> expected, actual

    err span $"expected {formatTy expected}, got {formatTy actual}{hint}"

let private allOk (items: 'a list) (f: 'a -> Result<unit, TypeError>) : Result<unit, TypeError> =
    items |> List.fold (fun acc x -> Result.bind (fun () -> f x) acc) (Ok())

let private firstDup (xs: string list) : string option =
    xs
    |> List.countBy id
    |> List.tryPick (fun (x, n) -> if n > 1 then Some x else None)

// written type names [D:qualified-type-names]: `A.T` names import A's
// type T, a bare name resolves as it always did — unless two imports each
// provide one, when neither holds the bare name and a use must qualify
type TypeNameRes =
    | TypeKey of string
    // the qualified spellings that would each resolve it
    | TypeAmbiguous of string list
    | TypeNotInModule of alias: string * name: string * owned: string list
    | TypeUnresolved

let private typeSpellings (env: TypeEnv) (name: string) : (string * string) list =
    env.ModuleTypes
    |> Map.toList
    |> List.choose (fun (alias, owned) -> Map.tryFind name owned |> Option.map (fun key -> alias + "." + name, key))

let resolveTypeName (env: TypeEnv) (written: string) : TypeNameRes =
    let dot = written.IndexOf '.'

    if dot > 0 then
        let m = written.Substring(0, dot)
        let t = written.Substring(dot + 1)

        match Map.tryFind m env.ModuleTypes |> Option.bind (Map.tryFind t) with
        | Some key -> TypeKey key
        | None when (typeDefFor env written).IsSome -> TypeKey written
        | None ->
            match Map.tryFind m env.ModuleTypes with
            | Some owned -> TypeNotInModule(m, t, owned |> Map.keys |> List.ofSeq)
            | None -> TypeUnresolved
    elif (typeDefFor env written).IsSome then
        TypeKey written
    else
        let spellings = typeSpellings env written

        if (spellings |> List.map snd |> List.distinct |> List.length) > 1 then
            TypeAmbiguous(spellings |> List.map fst |> List.sort)
        else
            TypeUnresolved

// 'A.T', 'B.T' or 'C.T'
let private quotedOrList (spellings: string list) : string =
    let quoted = spellings |> List.map (fun q -> $"'{q}'")

    match List.rev quoted with
    | last :: rest when not rest.IsEmpty -> (List.rev rest |> String.concat ", ") + " or " + last
    | _ -> String.concat ", " quoted

let private ambiguousTypeMsg (name: string) (spellings: string list) : string =
    $"type '{name}' is ambiguous — more than one import declares it; write the qualified name: {quotedOrList spellings}"

let private notInModuleMsg (m: string) (t: string) (owned: string list) : string =
    $"module '{m}' declares no type '{t}'{didYouMean t owned}"

/// the unknown-type message for a written name, naming the qualified
/// spellings when the bare name is ambiguous [D:qualified-type-names]
let unknownTypeMsg (env: TypeEnv) (n: string) : string =
    match resolveTypeName env n with
    | TypeAmbiguous qs -> ambiguousTypeMsg n qs
    | TypeNotInModule(m, t, owned) -> notInModuleMsg m t owned
    | _ when n.IndexOf '.' > 0 ->
        let m = n.Substring(0, n.IndexOf '.')
        $"unknown type '{n}' — no import is named '{m}'{didYouMean m (Map.keys env.ModuleTypes)}"
    | _ -> $"unknown type '{n}'{didYouMean n (Map.keys env.Types)}"

/// a written type with every name resolved to its env key
/// [D:qualified-type-names]; a name nothing resolves is left as written
/// for the caller's own unknown-type teaching. An anonymous shape whose
/// fields resolve differently is re-minted under its canonical name.
let rec canonTyKeeping (self: string) (env: TypeEnv) (span: Span) (ty: Ty) : Result<Ty, TypeError> =
    let canonTy = canonTyKeeping self

    let all (ts: Ty list) =
        ts
        |> List.fold
            (fun acc t ->
                acc
                |> Result.bind (fun xs -> canonTy env span t |> Result.map (fun t' -> t' :: xs)))
            (Ok [])
        |> Result.map List.rev

    let fieldsOf (fs: (string * Ty) list) =
        all (fs |> List.map snd) |> Result.map (List.zip (fs |> List.map fst))

    match ty with
    | TFun(a, b) -> all [ a; b ] |> Result.map (fun ts -> TFun(ts[0], ts[1]))
    | TSeq t -> canonTy env span t |> Result.map TSeq
    | TTuple ts -> all ts |> Result.map TTuple
    | TRowVar(r, fs) -> fieldsOf fs |> Result.map (fun fs' -> TRowVar(r, fs'))
    | TNamed(n, []) when n.StartsWith "{|" ->
        match typeDefFor env n with
        | Some(Record rd) ->
            fieldsOf rd.Fields
            |> Result.map (fun fs' ->
                if fs' = rd.Fields then
                    ty
                else
                    let n' = anonRecordName fs'

                    env.AnonLitDefs[n'] <-
                        Record {
                            rd with
                                Name = n'
                                Fields = List.sortBy fst fs'
                        }

                    Types.pushAnonDef n' fs'
                    TNamed(n', []))
        | _ -> Ok ty
    | TNamed(n, args) when n = self -> all args |> Result.map (fun args' -> TNamed(n, args'))
    | TNamed(n, args) ->
        all args
        |> Result.bind (fun args' ->
            match resolveTypeName env n with
            | TypeKey k -> Ok(TNamed(k, args'))
            | TypeAmbiguous qs -> err span (ambiguousTypeMsg n qs)
            | TypeNotInModule(m, t, owned) -> err span (notInModuleMsg m t owned)
            | TypeUnresolved -> Ok(TNamed(n, args')))
    | t -> Ok t

let canonTy (env: TypeEnv) (span: Span) (ty: Ty) : Result<Ty, TypeError> = canonTyKeeping "" env span ty

/// the adapter slot's payload with its names resolved
/// [D:qualified-type-names]: `from json A.Config` reads module A's type
let rec private canonShape (env: TypeEnv) (span: Span) (shape: FromShape) : Result<FromShape, TypeError> =
    let named (n: string) (args: Ty list) =
        canonTy env span (TNamed(n, args))
        |> Result.map (function
            | TNamed(k, []) -> FromName k
            | TNamed(k, args') -> FromApp(k, args')
            | _ -> shape)

    match shape with
    | FromName n -> named n []
    | FromApp(n, args) -> named n args
    | FromTy ty -> canonTy env span ty |> Result.map FromTy
    | FromMap(k, inner) ->
        canonTy env span k
        |> Result.bind (fun k' -> canonShape env span inner |> Result.map (fun i -> FromMap(k', i)))
    | FromAnon fields ->
        let n = anonRecordName fields

        match typeDefFor env n with
        | Some _ ->
            canonTy env span (TNamed(n, []))
            |> Result.map (function
                | TNamed(n', []) when n' <> n ->
                    match typeDefFor env n' with
                    | Some(Record rd) -> FromAnon rd.Fields
                    | _ -> shape
                | _ -> shape)
        | None -> Ok shape

// an inline `Env.load {| … |}` / `Args.load {| … |}` shape registers
// under its canonical name with every field as written; a repeated
// name is refused as a declared record's is [D:env-inline-type]
let private loadShapeDup (env: TypeEnv) (tyName: string) : string option =
    if tyName.StartsWith "{|" then
        match typeDefFor env tyName with
        | Some(Record def) -> firstDup (def.Fields |> List.map fst)
        | _ -> None
    else
        None

let private bindParams (env: TypeEnv) (bindings: (string * Ty) list) : TypeEnv =
    bindings |> List.fold (fun e (n, t) -> bindValue n (mono t) e) env

let private substParams (ps: string list) (args: Ty list) (ty: Ty) : Ty =
    let m = List.zip ps args |> Map.ofList

    let rec go ty =
        match ty with
        | TVar v -> Map.tryFind v m |> Option.defaultValue ty
        | TFun(a, b) -> TFun(go a, go b)
        | TSeq t -> TSeq(go t)
        | TTuple ts -> TTuple(ts |> List.map go)
        | TNamed(n, targs) -> TNamed(n, targs |> List.map go)
        | TRowVar(r, fields) -> TRowVar(r, fields |> List.map (fun (f, t) -> f, go t))
        | t -> t

    go ty

// The two splice sites [D:interp-show]: a hole renders for a human
// (Show is the rule), a command argument becomes an argv word for a
// program (scalar-exact — no guessing at the child's parser)
type private SpliceSite =
    | Hole
    | CmdArg

// A pending class constraint on an unresolved type/row var: the
// demanding site's span and message formatter travel with it, so a
// later discharge failure reads at the place that demanded it.
type private Pending = {
    Cls: Cls
    Span: Span
    Describe: TypeEnv -> Ty -> string
}

// == on floats compares representations, which weir does not vouch
// for [D:floats]; the Eq exclusion's message names the idiom
let rec private tyHasFloat (ty: Ty) : bool =
    match ty with
    | TFloat -> true
    | TSeq t -> tyHasFloat t
    | TFun(a, b) -> tyHasFloat a || tyHasFloat b
    | TTuple ts -> ts |> List.exists tyHasFloat
    | TNamed(_, targs) -> targs |> List.exists tyHasFloat
    | _ -> false

let private floatEqTeaching =
    "floats do not join '==' (0.1 + 0.2 is not 0.3); use Float.near a b eps, or compare after Float.round"

// a float reached through a declared record/union [D:floats]: the type
// names no float itself, so the teaching names the field it holds
let private floatInsideNamed (env: TypeEnv) (ty: Ty) : bool =
    let rec go (seen: Set<string>) (t: Ty) =
        tyHasFloat t
        || (match t with
            | TSeq t -> go seen t
            | TTuple ts -> ts |> List.exists (go seen)
            | TNamed(n, targs) when not (seen.Contains n) ->
                let seen = seen.Add n

                targs |> List.exists (go seen)
                || (match typeDefFor env n with
                    | Some(Record d) -> d.Fields |> List.exists (snd >> go seen)
                    | Some(Union u) -> u.Cases |> List.exists (snd >> Option.exists (go seen))
                    | _ -> false)
            | _ -> false)

    go Set.empty ty

type private Ctx = {
    mutable Fresh: int
    // grow-only, read by key, never snapshotted: dictionaries, so a
    // bind allocates nothing and a resolve hashes once
    Subst: System.Collections.Generic.Dictionary<string, Ty>
    Rows: System.Collections.Generic.Dictionary<string, Map<string, Ty * Span>>
    // physical field-access origins for instantiated row vars
    // [D:row-provenance], rehydrated from the scheme's RowOrigins
    mutable RowOrigins: Map<string, (string * int * int * int) list>
    mutable Cons: Map<string, Pending list>
    // splice/hole vars whose scalar defaulting is deferred to the
    // statement boundary [D:splice-default-last]: early defaulting
    // rejected `1 |> (fun k -> $"{k}")` — the issue is order, not
    // the rule; the shape check defers with it
    mutable PendingSplices: (string * Span * SpliceSite) list
    // the inference budget, [D:depth-guard]'s post-parse sibling:
    // HM inference is DEXPTIME (a 7-line file can double its type
    // per line), and never answering is as wrong as a crash — so
    // resolution work is counted and exhaustion becomes a located
    // diagnostic. The ceiling is a cost bound, orders of magnitude
    // above any real program, revisable given evidence
    mutable Work: int
    // the span the exhaustion diagnostic anchors on — the innermost
    // unification site's, kept current by bind (resolve has no span)
    mutable WorkSpan: Span
    // the innermost bind's operands, for the evidence probe: at
    // exhaustion these are the types whose size is or is not the cause
    mutable WorkTys: Ty * Ty
    // point-free print uses (Seq.iter print) defer their sentinel to
    // the statement boundary like splices do [D:splice-default-last]:
    // a use-site-determined type passes printArgTy there, an
    // undetermined one keeps the string default (PLAN-dx-review D7)
    mutable PendingPrints: (string * Span) list
    // physical positions of bare holes whose var took the string
    // default this statement (PLAN-dx-review D6) — ride the let's
    // scheme so a later call-site mismatch can name the anchor
    mutable HoleDefaulted: (int * int) list
    // `+` over two unknowns, deferred [D:plus-defer]: retries keyed
    // by the vars that can decide them; the open set (by creation
    // order: span + operand/result types) is stranded at the boundary
    mutable Adds: Map<string, (int * (unit -> Result<unit, TypeError>)) list>
    mutable AddsOpen: Map<int, Span * Ty list>
    // block-level recovery [D:block-recovery]: Some = a failed block
    // element becomes a hole (recorded here with its span) and the
    // block continues; None = the failure propagates, as every
    // reporting consumer needs
    mutable Recovered: (TypeError * Span) list option
}

// exhaustion carries whether type size is actually in evidence — the
// message must not claim "your type grew too large" merely because the
// budget ran out (a future second non-termination shape gets an honest
// generic answer, not a confidently wrong specific one)
exception private BudgetExceeded of Span * sizeEvidenced: bool

let private workBudget = 5_000_000
let private sizeEvidenceFloor = 50_000

let private newCtx () = {
    Fresh = 0
    Subst = System.Collections.Generic.Dictionary()
    Rows = System.Collections.Generic.Dictionary()
    RowOrigins = Map.empty
    Cons = Map.empty
    PendingSplices = []
    Work = 0
    WorkSpan = {
        Start = { Line = 1; Col = 1 }
        End = { Line = 1; Col = 1 }
    }
    WorkTys = TUnit, TUnit
    PendingPrints = []
    HoleDefaulted = []
    Adds = Map.empty
    AddsOpen = Map.empty
    Recovered = None
}

let private freshName (ctx: Ctx) (prefix: string) : string =
    ctx.Fresh <- ctx.Fresh + 1
    $"{prefix}{ctx.Fresh}"

// resolve is the step every type walk pays (occurs, finalTy, bind,
// row merges), so it is where the budget ticks; the raise site
// disarms the counter first so the evidence probe below can walk
// without re-triggering
let rec private resolveNoTick (ctx: Ctx) (ty: Ty) : Ty =
    match ty with
    | TVar v ->
        match ctx.Subst.TryGetValue v with
        | true, t -> resolveNoTick ctx t
        | _ -> ty
    | TRowVar(r, _) ->
        match ctx.Subst.TryGetValue r with
        | true, t -> resolveNoTick ctx t
        | _ -> ty
    | t -> t

// a row var's fields so far (none yet: the empty map)
let private rowsOf (ctx: Ctx) (r: string) : Map<string, Ty * Span> =
    match ctx.Rows.TryGetValue r with
    | true, m -> m
    | _ -> Map.empty

let private tryRows (ctx: Ctx) (r: string) : Map<string, Ty * Span> option =
    match ctx.Rows.TryGetValue r with
    | true, m -> Some m
    | _ -> None

// a bounded size probe for the budget's evidence check: walks at most
// `floor` nodes and answers "is this type at least that big" — never
// O(actual size), because at exhaustion the actual size can be 2^n
let private typeAtLeast (ctx: Ctx) (floor: int) (ty: Ty) : bool =
    let stack = System.Collections.Generic.Stack<Ty>()
    stack.Push ty
    let mutable count = 0

    while count < floor && stack.Count > 0 do
        count <- count + 1

        match resolveNoTick ctx (stack.Pop()) with
        | TFun(a, b) ->
            stack.Push a
            stack.Push b
        | TSeq t -> stack.Push t
        | TTuple ts -> ts |> List.iter stack.Push
        | TNamed(_, args) -> args |> List.iter stack.Push
        | TRowVar(_, fields) -> fields |> List.iter (snd >> stack.Push)
        | _ -> ()

    count >= floor

let private budgetRaise (ctx: Ctx) (ty: Ty) : 'a =
    ctx.Work <- System.Int32.MinValue // disarm: the probe walks freely
    let l, r = ctx.WorkTys

    let sized =
        typeAtLeast ctx sizeEvidenceFloor ty
        || typeAtLeast ctx sizeEvidenceFloor l
        || typeAtLeast ctx sizeEvidenceFloor r

    raise (BudgetExceeded(ctx.WorkSpan, sized))

let private resolve (ctx: Ctx) (ty: Ty) : Ty =
    ctx.Work <- ctx.Work + 1

    if ctx.Work > workBudget then
        budgetRaise ctx ty
    else
        resolveNoTick ctx ty

let private same (a: obj) (b: obj) = obj.ReferenceEquals(a, b)

let mapShared (f: 'a -> 'a) (xs: 'a list) : 'a list =
    let mutable out: 'a[] = null
    let mutable i = 0

    for x in xs do
        let y = f x

        if not (isNull out) then
            out[i] <- y
        elif not (obj.ReferenceEquals(y, x)) then
            out <- Array.zeroCreate (List.length xs)
            let mutable p = xs

            for j in 0 .. i - 1 do
                out[j] <- p.Head
                p <- p.Tail

            out[i] <- y

        i <- i + 1

    if isNull out then xs else List.ofArray out

// the finalizer's walk takes its arguments explicitly — no closure per
// call (finalizeExpr runs it on every typed node) — and a type with
// nothing to resolve comes back as itself
let rec private finalGo (ctx: Ctx) (seen: Set<string>) (ty: Ty) : Ty =
    match resolve ctx ty with
    | TFun(a, b) as t ->
        let a' = finalGo ctx seen a
        let b' = finalGo ctx seen b
        if same a' a && same b' b then t else TFun(a', b')
    | TSeq e as t ->
        let e' = finalGo ctx seen e
        if same e' e then t else TSeq e'
    | TTuple ts as t ->
        let ts' = finalGoList ctx seen ts
        if same ts' ts then t else TTuple ts'
    | TNamed(n, args) as t ->
        let args' = finalGoList ctx seen args
        if same args' args then t else TNamed(n, args')
    | TRowVar(r, []) as t when seen.Contains r -> t
    | TRowVar(r, _) when seen.Contains r -> TRowVar(r, [])
    | TRowVar(r, old) as t ->
        let seen' = Set.add r seen

        let fields =
            rowsOf ctx r
            |> Map.toList
            |> List.map (fun (f, (t, _)) -> f, finalGo ctx seen' t)

        let rec unchanged (xs: (string * Ty) list) (ys: (string * Ty) list) =
            match xs, ys with
            | [], [] -> true
            | (f, t) :: xs, (g, u) :: ys -> f = g && same t u && unchanged xs ys
            | _ -> false

        if unchanged fields old then t else TRowVar(r, fields)
    | t -> t

// mapShared's loop with the walk's arguments passed, not closed over
and private finalGoList (ctx: Ctx) (seen: Set<string>) (ts: Ty list) : Ty list =
    let mutable out: Ty[] = null
    let mutable i = 0

    for x in ts do
        let y = finalGo ctx seen x

        if not (isNull out) then
            out[i] <- y
        elif not (same y x) then
            out <- Array.zeroCreate (List.length ts)
            let mutable p = ts

            for j in 0 .. i - 1 do
                out[j] <- p.Head
                p <- p.Tail

            out[i] <- y

        i <- i + 1

    if isNull out then ts else List.ofArray out

let private finalTy (ctx: Ctx) (ty: Ty) : Ty = finalGo ctx Set.empty ty

// an unresolved var anywhere, without building the var set
let rec private hasVars (ctx: Ctx) (ty: Ty) : bool =
    match resolve ctx ty with
    | TVar _
    | TRowVar _ -> true
    | TFun(a, b) -> hasVars ctx a || hasVars ctx b
    | TSeq t -> hasVars ctx t
    | TTuple ts
    | TNamed(_, ts) -> ts |> List.exists (hasVars ctx)
    | _ -> false

let private occurs (ctx: Ctx) (v: string) (ty: Ty) : bool =
    let seenRows = System.Collections.Generic.HashSet<string>()

    let rec go ty =
        match resolve ctx ty with
        | TVar u -> u = v
        | TRowVar(r, _) -> r = v || (seenRows.Add r && rowsOf ctx r |> Map.exists (fun _ (t, _) -> go t))
        | TFun(a, b) -> go a || go b
        | TSeq t -> go t
        | TTuple ts -> ts |> List.exists go
        | TNamed(_, args) -> args |> List.exists go
        | _ -> false

    go ty

// the def-less builtin nominals beside Map [D:scoped-procs][D:yaml-nodes]:
// type constructors with no Record/Union entry. One list — Prelude's
// built-in-name registration and validateTy's arity table both read it,
// so a nominal cannot be registered-but-unnameable (otherwise the
// privacy teaching could suggest a signature validateTy refuses).
// Plan is an opaque nominal [D:plan-apply]: its runtime rep is a record
// of captured Ops, but the value surface is the `Plan.*` members
// (ops/preview/apply/isEmpty) plus `==` — never a field, so a user
// `plan.ops` field-access is not offered. Def-less like Proc/YamlPatch.
// Server is def-less like Proc [D:http-serve]: its runtime rep is a
// listener handle, its value surface the `Server.*` members (port),
// never a field — so a user `srv.port` field-access is not offered.
let deflessBuiltinNominals = [ "Proc"; "YamlPatch"; "Plan"; "Server" ]

// an opaque built-in value: no fields, never rendered
let private opaqueNominal (t: Ty) : string option =
    match t with
    | TNamed(n, _) when List.contains n deflessBuiltinNominals -> Some n
    | _ -> None

// what an opaque value offers instead of fields
let private opaqueNoFields (name: string) : string =
    match name with
    | "YamlPatch" -> "a patch has no fields — it is instructions for a merge; Yaml.merge applies it"
    | n -> $"{n} has no fields — it is an opaque value; use the {n}.* members"

// the hole law's refusals [D:interp-show] [D:hole-generic] — one text
// for the eager check and an instantiated Render constraint
let private holeDescribe (t: Ty) : string =
    match t with
    // a Secret does not interpolate [D:secret]: `$"token: {s}"` is how a
    // secret reaches a log line. show renders ***; using the value is
    // the deliberate Secret.reveal
    | TSecret -> "a Secret does not interpolate — Secret.reveal s to use its value, or show s to render ***"
    | TNamed("YamlPatch", _) ->
        "a patch does not render — it is instructions for a merge, not data; Yaml.merge applies it"
    | t when (opaqueNominal t).IsSome ->
        $"interpolation holes render what show renders; {formatTy t} is an opaque value and cannot be shown"
    | t -> $"interpolation holes render what show renders; {formatTy t} cannot be shown (functions never render)"

// a class requirement left on a type nothing determines, named by its
// class — show/interpolation and ordering are not equality
let private strandedMessage (cls: Cls) : string =
    let req =
        match cls with
        | Cls.Eq -> "an equality"
        | Cls.Ord
        | Cls.Cmp -> "an ordering"
        | Cls.Show
        | Cls.Render -> "a rendering"

    $"this leaves {req} requirement on a type nothing determines — pipe in data or use a concrete value"

// the built-in type names, registered once by Prelude.extend
// [D:desugar-capture]: a user redeclaring one would silently retype
// every builtin referencing it (type Retry = { x: int } would break
// the retry sugar through the type even with the value un-shadowable).
// Concurrent because tests build envs in parallel — a plain HashSet
// corrupts under simultaneous extends.
let builtinTypeNames: System.Collections.Concurrent.ConcurrentDictionary<string, byte> =
    System.Collections.Concurrent.ConcurrentDictionary<string, byte>()

// a declared type's ordering key [D:derived-ord]: the `[<Ord n>]`
// members by ascending n — empty when nothing is numbered, so the
// type does not order
let private ordKeyOf (def: TypeDef) : string list =
    match def with
    | Record d -> d.OrdKey
    | Union d -> d.OrdKey

let private ordDeclared (env: TypeEnv) (n: string) : bool =
    match typeDefFor env n with
    | Some def -> not (ordKeyOf def).IsEmpty
    | None -> false

let private defParams (def: TypeDef) : string list =
    match def with
    | Record d -> d.Params
    | Union d -> d.Params

// the key's member types, under the declaration's own parameters: a
// record's numbered fields (the unnumbered ones take no part — the
// partial-key law [D:derived-ord]), a union's payloads (every case is
// numbered or none)
let private ordKeyTys (def: TypeDef) : Ty list =
    match def with
    | Record d -> d.OrdKey |> List.map (fun f -> d.Fields |> List.find (fst >> (=) f) |> snd)
    | Union d -> d.Cases |> List.choose snd

// the derived-ordering law [D:derived-ord]: an ordered member is a
// scalar of the Ord set, an `Option` of one, another ordered type (a
// self-reference admits through `seen`, as Eq's decompose does), or a
// type parameter (the use decides, with the argument concrete);
// anything else is the offender — the innermost one, so a message can
// name the repair
let rec private ordOffender (env: TypeEnv) (seen: Set<string>) (ty: Ty) : Ty option =
    match ty with
    | TInt
    | TFloat
    | TStr
    | TBool
    | TDur
    | TSize
    | TInstant
    | TUuid
    | TVar _ -> None
    | TNamed("Option", [ t ]) -> ordOffender env seen t
    | TNamed(n, targs) ->
        let key = formatTy ty

        if seen.Contains key then
            None
        else
            let seen = Set.add key seen

            match typeDefFor env n with
            | Some def when not (ordKeyOf def).IsEmpty ->
                ordKeyTys def
                |> List.tryPick (fun mt -> ordOffender env seen (substParams (defParams def) targs mt))
            | _ -> Some ty
    | _ -> Some ty

// a record or union the author declared — the one kind of type an
// `[<Ord n>]` repair can name (not a built-in or prelude type, not an
// anonymous shape)
let private authorDeclared (env: TypeEnv) (n: string) : string option =
    if n.StartsWith "{|" || builtinTypeNames.ContainsKey n then
        None
    else
        match typeDefFor env n with
        | Some(Record _) -> Some "fields"
        | Some(Union _) -> Some "cases"
        | None -> None

// the repair for an unordered key or member [D:derived-ord], by its
// innermost offender: a declared type names the attribute, a tuple the
// record spelling, anything else the admitted set
let private ordTail (env: TypeEnv) (atUse: bool) (offender: Ty) : string =
    match offender with
    | TNamed(n, _) when n.StartsWith "{|" ->
        "an anonymous record has no order; declare the shape as a record and number its fields with [<Ord n>]"
    | TNamed(n, _) when (authorDeclared env n).IsSome ->
        if atUse then
            $"number its {(authorDeclared env n).Value} with [<Ord n>] to order it"
        else
            $"number its {(authorDeclared env n).Value} with [<Ord n>] to order it too"
    | TTuple _ -> "tuples have no order; name the parts in a record and number them with [<Ord n>]"
    | _ ->
        if atUse then
            "keys are int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, or a record or union numbered with [<Ord n>]"
        else
            "only int, float, string, bool, Duration, Size, Instant, Uuid, an Option of those, and records or unions numbered with [<Ord n>] order"

// the comparison operators' refusal [D:uuid-unordered-generic]: one text
// for the operator site and a Cmp constraint met at instantiation (`op`
// is None there — the operator sits in the generic's body)
let private cmpDescribe (op: string option) (env: TypeEnv) (t: Ty) : string =
    let rec holdsUuid t =
        match t with
        | TUuid -> true
        | TNamed("Option", [ inner ]) -> holdsUuid inner
        | _ -> false

    let subject =
        match op with
        | Some op -> $"operator '{op}' is not defined for {formatTy t}"
        | None -> $"this use compares values with '<', '>', '<=' or '>=', which are not defined for {formatTy t}"

    if holdsUuid t then
        $"{subject}; a Uuid is an identifier, not a quantity — Seq.sort orders Uuids"
    else
        let off = ordOffender env Set.empty t |> Option.defaultValue t
        $"{subject} — {ordTail env true off}"

let private instantiate (ctx: Ctx) (span: Span) (sch: Scheme) : Ty =
    if Set.isEmpty sch.Forall then
        sch.Ty
    else
        let rec rowNames ty =
            match ty with
            | TRowVar(r, fields) -> fields |> List.fold (fun acc (_, t) -> acc + rowNames t) (Set.singleton r)
            | TFun(a, b) -> rowNames a + rowNames b
            | TSeq t -> rowNames t
            | TTuple ts -> ts |> List.fold (fun acc t -> acc + rowNames t) Set.empty
            | _ -> Set.empty

        let rec hasRowVar ty =
            match ty with
            | TRowVar _ -> true
            | TFun(a, b) -> hasRowVar a || hasRowVar b
            | TSeq t -> hasRowVar t
            | TTuple ts -> ts |> List.exists hasRowVar
            | _ -> false

        let rows = if hasRowVar sch.Ty then rowNames sch.Ty else Set.empty

        let mapping =
            sch.Forall
            |> Set.toList
            |> List.map (fun v ->
                // the hole prefix survives instantiation so the
                // application arm can stay silent on hole-bound heads
                // [PLAN-diagnostics-arc B6]
                let stem =
                    if v.StartsWith "__hole" then "__hole"
                    elif rows.Contains v then "r"
                    else "a"

                v, freshName ctx stem)
            |> Map.ofList

        let rec rename ty =
            match ty with
            | TVar v ->
                match Map.tryFind v mapping with
                | Some v' -> TVar v'
                | None -> ty
            | TRowVar(r, fields) ->
                let fields' = fields |> List.map (fun (f, t) -> f, rename t)

                match Map.tryFind r mapping with
                | Some r' ->
                    ctx.Rows[r'] <- fields' |> List.map (fun (f, t) -> f, (t, span)) |> Map.ofList

                    // provenance rides the instantiation [D:row-provenance]
                    match Map.tryFind r sch.RowOrigins with
                    | Some os -> ctx.RowOrigins <- Map.add r' os ctx.RowOrigins
                    | None -> ()

                    TRowVar(r', fields')
                | None -> TRowVar(r, fields')
            // a subtree without a quantified var is shared, not copied
            | TFun(a, b) ->
                let a' = rename a
                let b' = rename b
                if same a' a && same b' b then ty else TFun(a', b')
            | TSeq t ->
                let t' = rename t
                if same t' t then ty else TSeq t'
            | TTuple ts ->
                let ts' = mapShared rename ts
                if same ts' ts then ty else TTuple ts'
            | TNamed(n, args) ->
                let args' = mapShared rename args
                if same args' args then ty else TNamed(n, args')
            | t -> t

        // constraints freshen with the vars (deep-copy discipline): the
        // instantiation site's span becomes the demanding site
        for KeyValue(v, clss) in sch.Cs do
            match Map.tryFind v mapping with
            | Some v' ->
                let ps =
                    clss
                    |> Set.toList
                    |> List.map (fun cls -> {
                        Cls = cls
                        Span = span
                        Describe =
                            match cls with
                            | Cls.Eq ->
                                fun env t ->
                                    if tyHasFloat t then
                                        floatEqTeaching
                                    elif floatInsideNamed env t then
                                        $"this use requires equatable values, got {formatTy t} — it holds a float, and floats cannot be compared with '==' (0.1 + 0.2 is not 0.3)"
                                    elif (opaqueNominal t).IsSome then
                                        $"this use requires equatable values, got {formatTy t} — an opaque value cannot be compared with '=='"
                                    else
                                        $"this use requires equatable values, got {formatTy t} — sequences and functions cannot be compared with '=='"
                            | Cls.Show ->
                                fun _ t ->
                                    match t with
                                    // a patch is opaque by design [D:yaml-nodes]
                                    | TNamed("YamlPatch", _) ->
                                        "a patch does not show — it is instructions for a merge, not data; Yaml.merge applies it"
                                    | t when (opaqueNominal t).IsSome ->
                                        $"show cannot render {formatTy t} — it is an opaque value"
                                    | _ -> $"show cannot render functions; this is {formatTy t}"
                            | Cls.Ord ->
                                fun env t ->
                                    let off = ordOffender env Set.empty t |> Option.defaultValue t
                                    $"cannot sort by this key: {formatTy t} cannot be ordered — {ordTail env true off}"
                            | Cls.Cmp -> cmpDescribe None
                            | Cls.Render -> fun _ t -> holeDescribe t
                    })

                ctx.Cons <- Map.add v' (ps @ (Map.tryFind v' ctx.Cons |> Option.defaultValue [])) ctx.Cons
            | None -> ()

        rename sch.Ty

// a fully quantified scheme is closed — and usually from another
// statement's ctx, whose var names this ctx's subst may reuse, so it
// must not be resolved here [D:hole-generic]
// [D:env-open-index]: only the env's non-closed schemes are walked
let private envFreeVars (ctx: Ctx) (env: TypeEnv) : Set<string> =
    let fromEnv =
        openSchemes env
        |> Map.fold (fun acc _ (sch: Scheme) -> acc + (tyVars (finalTy ctx sch.Ty) - sch.Forall)) Set.empty
    // an undecided `+` keeps its vars monomorphic [D:plus-defer]: a
    // later use in the statement may still decide it
    ctx.AddsOpen
    |> Map.fold (fun acc _ (_, tys) -> tys |> List.fold (fun a t -> a + tyVars (finalTy ctx t)) acc) fromEnv

// The class solver. Concrete types run the shape predicate; applied
// constructors decompose structurally; bare vars (and row vars) carry
// the constraint forward — bind discharges them the moment they
// resolve. Failure formats the original demanded type (matching the
// pre-class message families). Fully erased: the classes have no
// runtime presence.
let private demand (ctx: Ctx) (env: TypeEnv) (p0: Pending) (ty0: Ty) : Result<unit, TypeError> =
    let pend (p: Pending) (name: string) =
        ctx.Cons <- Map.add name (p :: (Map.tryFind name ctx.Cons |> Option.defaultValue [])) ctx.Cons

    let rec okAs (p: Pending) (seen: Set<string>) (ty: Ty) : bool =
        let ok = okAs p

        match resolve ctx ty with
        | TVar v ->
            pend p v
            true
        | TRowVar(r, _) ->
            // rides the row; discharges when the row does (all fields
            // then satisfy the class, recursively)
            pend p r
            true
        // Render is Show with the top-level Secret refused
        // [D:hole-generic]; below the top a Secret renders masked
        | TSecret when p.Cls = Cls.Render -> false
        | t when p.Cls = Cls.Render -> okAs { p with Cls = Cls.Show } seen t
        // Cmp is Ord with Uuid refused, through Option too
        // [D:uuid-unordered-generic]; a declared [<Ord n>] key may hold
        // one (the declaration orders, not the Uuid)
        | TUuid when p.Cls = Cls.Cmp -> false
        | TNamed("Option", [ inner ]) when p.Cls = Cls.Cmp -> okAs p seen inner
        | t when p.Cls = Cls.Cmp -> okAs { p with Cls = Cls.Ord } seen t
        | t ->
            let decompose (n: string) (targs: Ty list) =
                let key = formatTy t

                seen.Contains key
                || (match typeDefFor env n with
                    | Some(Record def) ->
                        def.Fields
                        |> List.forall (fun (_, ft) -> ok (Set.add key seen) (substParams def.Params targs ft))
                    | Some(Union def) ->
                        def.Cases
                        |> List.forall (fun (_, payload) ->
                            payload
                            |> Option.forall (fun pt -> ok (Set.add key seen) (substParams def.Params targs pt)))
                    | None -> false)

            match p.Cls, t with
            // Eq: no function or seq anywhere, recursively
            // Secret admits Eq [D:secret] (did the token change?) but
            // not Ord — sorting secrets is meaningless; constant-time is
            // not claimed (weir is not a crypto library)
            | Cls.Eq, (TInt | TStr | TBool | TUnit | TDur | TSize | TInstant | TUuid | TBytes | TSecret) -> true
            // floats are excluded from Eq [D:floats] — finite-only kept
            // equality reflexive; representation (0.1 + 0.2) is the trap
            // that remains, and weir does not vouch for it
            | Cls.Eq, TFloat -> false
            | Cls.Eq, (TFun _ | TSeq _) -> false
            | Cls.Eq, TTuple ts -> ts |> List.forall (ok seen)
            // plan/apply's Op and Plan are Eq-admitted by design
            // [D:plan-apply]: the testing story is `plan <block> ==
            // [WriteFile("x", c)]`, and Op carries a seq (file content)
            // that the generic decompose would reject. The value-level Eq
            // (Value.Equals) forces the seqs and compares them; the Secret
            // in HttpSend compares via VSecret's own Eq. Explicit carve-out
            // (as Proc-Show does — a named type gets its own arm).
            | Cls.Eq, TNamed(("Op" | "Plan"), _) -> true
            | Cls.Eq, TNamed(n, targs) -> decompose n targs
            // Show: no function anywhere; seqs render fine
            | Cls.Show, (TInt | TFloat | TStr | TBool | TUnit | TDur | TSize | TInstant | TUuid | TBytes | TSecret) ->
                true
            | Cls.Show, TFun _ -> false
            | Cls.Show, TSeq elem -> ok seen elem
            | Cls.Show, TTuple ts -> ts |> List.forall (ok seen)
            // Map renders (sorted) iff its keys and values do
            // [D:map-keys]; Eq stays excluded like seq (no conditional
            // instances — the closed class system is untouched)
            | Cls.Show, TNamed("Map", [ k; inner ]) -> ok seen k && ok seen inner
            // a handle shows (pid + state) [D:scoped-procs]; Eq/Ord
            // stay excluded by construction (the decompose fall-through)
            | Cls.Show, TNamed("Proc", []) -> true
            // a Plan shows (its ops, Secrets masked) [D:plan-apply] —
            // preview/diff needs it; def-less, so it gets its own arm
            | Cls.Show, TNamed("Plan", []) -> true
            | Cls.Show, TNamed(n, targs) -> decompose n targs
            // Ord: the scalar set below, plus the two decompositions
            // under it. Duration joins Ord [D:duration] —
            // elapsed > timeout is the point; floats join Ord
            // [D:floats], total because finite-only; Instant joins Ord
            // [D:instant] — before/after is comparison (Duration's own
            // admission argument, one type later); Uuid joins Ord
            // [D:uuid] — byte order, which is time order for v7
            | Cls.Ord, (TInt | TFloat | TStr | TBool | TDur | TSize | TInstant | TUuid) -> true
            // `Option` orders by its content, `None` first; a declared
            // record or union only through its `[<Ord n>]` key — the
            // numbered members, never the declaration [D:derived-ord]
            | Cls.Ord, TNamed("Option", [ t ]) -> ok seen t
            | Cls.Ord, TNamed(n, targs) when ordDeclared env n ->
                let key = formatTy t

                seen.Contains key
                || (let def = (typeDefFor env n).Value

                    ordKeyTys def
                    |> List.forall (fun mt -> ok (Set.add key seen) (substParams (defParams def) targs mt)))
            | Cls.Ord, _ -> false
            // vars and row vars are consumed by the outer match arms, and
            // Render by its delegation; the compiler cannot see that
            // through this nesting
            | _, (TVar _ | TRowVar _) -> true
            | (Cls.Render | Cls.Cmp), _ -> false

    if okAs p0 Set.empty ty0 then
        Ok()
    else
        err p0.Span (p0.Describe env (finalTy ctx ty0))

/// the scalar-exact list a `$x` command argument admits — spliceAdmit's
/// CmdArg site, and the REPL's `$` completion pool [D:argv-splice-complete]
let argvSpliceable (ty: Ty) : bool =
    match ty with
    | TStr
    | TInt
    | TBool
    // a Secret splices to argv [D:secret]: `curl -H $auth` must
    // work — that is what the type exists for. The value reaches
    // argv in the clear (visible in `ps` — the platform's property,
    // a stated non-claim), which is why this is deliberate, not silent
    | TSecret
    // one spelling, so no program-dependent form [D:uuid]: the
    // canonical lowercase text is the word
    | TUuid -> true
    | _ -> false

/// the yaml district's splice law over a type, `res` resolving each
/// level — the checker's yaml splice and the LSP's `$` pool inside a
/// yaml block [D:argv-splice-complete]
let yamlSpliceableWith (res: Ty -> Ty) (ty: Ty) : bool =
    let rec go ty =
        match res ty with
        | TInt
        | TFloat
        | TStr
        | TBool
        | TNamed("Yaml", []) -> true
        // an unresolved var (a template parameter) defers to the value-driven
        // lift at eval — the sortBy posture: no type-class constraint exists,
        // so concrete violations check-error and polymorphic ones become a
        // located runtime failure naming the law
        | TVar _ -> true
        | TNamed("Option", [ TNamed("Option", _) ]) -> false
        | TNamed("Option", [ inner ]) -> go inner
        | TSeq elem -> go elem
        | _ -> false

    go ty

// the splice law, shared by the eager check and the deferred
// discharge [D:interp-show]: holes consult the Show class (one arm
// asking beats a second list — the next Show type needs no edit
// here); command arguments stay the scalar-exact list, with the
// Duration arm naming the deliberate spellings
let private spliceAdmit (ctx: Ctx) (env: TypeEnv) (site: SpliceSite) (span: Span) (ty: Ty) : Result<unit, TypeError> =
    match ty with
    | TStr
    | TInt
    | TBool -> Ok()
    | ty ->
        match site with
        | Hole ->
            demand
                ctx
                env
                {
                    Cls = Cls.Render
                    Span = span
                    Describe = fun _ t -> holeDescribe t
                }
                ty
        | CmdArg ->
            match ty with
            | ty when argvSpliceable ty -> Ok()
            | TDur ->
                err
                    span
                    "a Duration's argv form depends on the program; pass Duration.toMillis d or show d deliberately"
            | TSize -> err span "a Size's argv form depends on the program; pass Size.toBytes s or show s deliberately"
            | TBytes ->
                err
                    span
                    "Bytes cannot splice into argv — bytes are not text; Bytes.toBase64 b for a text form, or File.writeBytes and pass the path"
            | TInstant ->
                err span "an Instant's argv form depends on the program; pass Instant.epochMs t or show t deliberately"
            | ty -> err span $"command arguments must be strings, ints or bools; this one is {formatTy ty}"

// vars discharge their pendings the moment they resolve — no trial
// resolution exists anywhere in the checker, so a discharge is final
let private dischargeCons (ctx: Ctx) (env: TypeEnv) (v: string) (t: Ty) : Result<unit, TypeError> =
    match Map.tryFind v ctx.Cons with
    | None -> Ok()
    | Some ps ->
        ctx.Cons <- mapDrop v ctx.Cons
        allOk ps (fun p -> demand ctx env p t)

// a deferred `+` retries the moment one of its vars resolves [D:plus-defer]
let private dischargeAdds (ctx: Ctx) (v: string) : Result<unit, TypeError> =
    match Map.tryFind v ctx.Adds with
    | None -> Ok()
    | Some fs ->
        ctx.Adds <- mapDrop v ctx.Adds
        allOk (List.rev fs) (fun (_, f) -> f ())

let rec private bind (ctx: Ctx) (env: TypeEnv) (span: Span) (expected: Ty) (actual: Ty) : Result<unit, TypeError> =
    // the budget ticks in resolve (the step every walk pays); bind
    // keeps the anchor and the evidence operands current so exhaustion
    // deep in a walk points at the innermost unification site
    ctx.WorkSpan <- span
    ctx.WorkTys <- expected, actual
    let expected = resolve ctx expected
    let actual = resolve ctx actual

    match expected, actual with
    | e, a when e = a -> Ok()
    | TVar v, t
    | t, TVar v ->
        if occurs ctx v t then
            err span $"cannot construct the infinite type '{v} = {formatTy (finalTy ctx t)}"
        else
            ctx.Subst[v] <- t

            dischargeCons ctx env v t |> Result.bind (fun () -> dischargeAdds ctx v)
    | TNamed(n1, a1), TNamed(n2, a2) when n1 = n2 && List.length a1 = List.length a2 ->
        allOk (List.zip a1 a2) (fun (x, y) -> bind ctx env span x y)
    | TRowVar(r, _), TNamed(n, targs)
    | TNamed(n, targs), TRowVar(r, _) -> dischargeRow ctx env span r n targs
    | TRowVar(r1, _), TRowVar(r2, _) -> mergeRows ctx env r1 r2
    | (TRowVar _ as rv), t
    | t, (TRowVar _ as rv) -> err span $"expected {formatTy (finalTy ctx rv)}, got {formatTy (finalTy ctx t)}"
    | TFun(e1, e2), TFun(a1, a2) -> bind ctx env span e1 a1 |> Result.bind (fun () -> bind ctx env span e2 a2)
    | TSeq e, TSeq a -> bind ctx env span e a
    | TTuple es, TTuple asx when List.length es = List.length asx ->
        allOk (List.zip es asx) (fun (x, y) -> bind ctx env span x y)
    | e, a -> mismatch span (finalTy ctx e) (finalTy ctx a)

and private dischargeRow
    (ctx: Ctx)
    (env: TypeEnv)
    (span: Span)
    (r: string)
    (name: string)
    (targs: Ty list)
    : Result<unit, TypeError> =
    match typeDefFor env name with
    | Some(Record def) ->
        let constraints = rowsOf ctx r |> Map.toList

        ctx.Subst[r] <- (TNamed(name, targs))

        dischargeCons ctx env r (TNamed(name, targs))
        |> Result.bind (fun () ->

            // cross-statement provenance [D:row-provenance]: point at
            // the recorded access, note the meet in the message —
            // shared by the no-field and field-type-mismatch arms
            let atAccess field fspan (baseMsg: string) : Result<unit, TypeError> =
                let origin =
                    Map.tryFind r ctx.RowOrigins
                    |> Option.bind (List.tryFind (fun (f, _, _, _) -> f = field))

                match origin, toPhys.Value with
                | Some(_, ol, oc, len), Some tr ->
                    let ml, mc = tr fspan.Start.Col

                    Error {
                        Span = fspan
                        Message = $"{baseMsg} (the value becomes a {name} at {ml}:{mc})"
                        Origin = Some(ol, oc, len)
                    }
                | _ ->
                    Error {
                        Span = fspan
                        Message = baseMsg
                        Origin = None
                    }

            allOk constraints (fun (field, (ft, fspan)) ->
                match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
                | Some(_, declTy) ->
                    match bind ctx env fspan (substParams def.Params targs declTy) ft with
                    | Ok() -> Ok()
                    // the mismatch's own message ("expected X, got Y"),
                    // re-anchored at the access like its no-field sibling
                    | Error e -> atAccess field fspan e.Message
                | None ->
                    match retiredField name field with
                    | Some teach -> atAccess field fspan $"'{field}' is retired: {teach}"
                    | None ->
                        let hint = didYouMean field (List.map fst def.Fields)
                        atAccess field fspan $"{name} has no field '{field}'{hint}"))
    | Some(Union _) -> err span $"{name} is a union; only records have fields"
    | None when List.contains name deflessBuiltinNominals -> err span (opaqueNoFields name)
    | None -> err span $"unknown type '{name}'"

and private mergeRows (ctx: Ctx) (env: TypeEnv) (r1: string) (r2: string) : Result<unit, TypeError> =
    if r1 = r2 then
        Ok()
    else
        let fields1 = rowsOf ctx r1
        ctx.Subst[r1] <- (TRowVar(r2, []))

        match Map.tryFind r1 ctx.Cons with
        | Some ps ->
            ctx.Cons <-
                ctx.Cons
                |> Map.remove r1
                |> Map.add r2 (ps @ (Map.tryFind r2 ctx.Cons |> Option.defaultValue []))
        | None -> ()

        // origins merge with the rows [D:row-provenance]; r2's win per field
        match Map.tryFind r1 ctx.RowOrigins with
        | Some os1 ->
            let os2 = Map.tryFind r2 ctx.RowOrigins |> Option.defaultValue []

            let merged =
                os2
                @ (os1
                   |> List.filter (fun (f, _, _, _) -> os2 |> List.forall (fun (g, _, _, _) -> g <> f)))

            ctx.RowOrigins <- ctx.RowOrigins |> Map.remove r1 |> Map.add r2 merged
        | None -> ()

        allOk (Map.toList fields1) (fun (field, (ft, fspan)) ->
            let fields2 = rowsOf ctx r2

            match Map.tryFind field fields2 with
            | Some(ft2, _) -> bind ctx env fspan ft2 ft
            | None ->
                ctx.Rows[r2] <- (Map.add field (ft, fspan) fields2)
                Ok())

// The sentinel scheme registered for the print builtin. The quantified
// name is unforgeable through declarations (ctx-fresh names are aN/rN),
// so a structural comparison against this scheme means exactly "print,
// unshadowed". This is the one place a builtin's ergonomics buys
// checker complexity (the sentinel + printArgTy + the three
// special-cased arms) — justified for the most-used builtin; do not
// extend it to a second builtin without an explicit ruling.
let printScheme: Scheme = {
    Forall = Set.singleton "__print"
    Cs = Map.empty
    Ty = TFun(TVar "__print", TUnit)
    RowOrigins = Map.empty
    HoleDefaults = []
}

let private isPrintFamily (env: TypeEnv) (name: string) =
    // "|print" is the arming desugar's un-typeable alias
    // [D:desugar-capture]: always the builtin, shadowable by no one
    name = "|print"
    || ((name = "print" || name = "printerr")
        && Map.tryFind name env.Values = Some printScheme)

// show : Show a => a -> string — the debugging renderer (REPL-shaped,
// lossy) [D:inferred-type-classes]: an ordinary constrained scheme
// on the normal instantiate/apply path; showable = no function
// anywhere, recursively (seqs render fine — Show is wider than Eq).
let showScheme: Scheme = {
    Forall = Set.singleton "a"
    Cs = Map [ "a", Set [ Cls.Show ] ]
    Ty = TFun(TVar "a", TStr)
    RowOrigins = Map.empty
    HoleDefaults = []
}

// Seq.contains — an ordinary constrained scheme
// `Eq a => a -> seq<a> -> bool` [D:inferred-type-classes], served by
// the normal instantiate/apply path.
let containsScheme: Scheme = {
    Forall = Set.singleton "a"
    Cs = Map [ "a", Set [ Cls.Eq ] ]
    Ty = TFun(TVar "a", TFun(TSeq(TVar "a"), TBool))
    RowOrigins = Map.empty
    HoleDefaults = []
}

// Seq.distinct : Eq a => seq<a> -> seq<a> [D:seq-distinct]
let distinctScheme: Scheme = {
    Forall = Set.singleton "a"
    Cs = Map [ "a", Set [ Cls.Eq ] ]
    Ty = TFun(TSeq(TVar "a"), TSeq(TVar "a"))
    RowOrigins = Map.empty
    HoleDefaults = []
}


// the sequenced-unit message, one home for its two arms — the infer
// and check-mode ESeq twins share it so they cannot drift
// [D:maintenance-2]
// the discarded-capture repair, one home for the statement gate and the
// sequenced-unit arms [D:district-retirement]: `$(cmd)` drops its parens;
// the env sigil `$e(cmd)` has none to drop — its overlay form is
// `within env e` over the bare command
let captureDropHint (e: Expr) : string =
    let envName (env: Expr option) =
        match env with
        | Some { Kind = EVar n } -> n
        | _ -> "e"

    match e.Kind with
    | ECapture { Kind = ECmd(_, _, Some env) }
    | ECapture {
                   Kind = EPipe(_, { Kind = ECmd(_, _, Some env) })
               } ->
        $", or run it as a command under the overlay: `within env {envName (Some env)}` with the bare command indented below"
    | ECapture { Kind = ECmd _ }
    | ECapture { Kind = EPipe(_, { Kind = ECmd _ }) } -> ", or drop the $( ) to run it as a command"
    | _ -> ""

let private seqUnitError (first: Expr) (ty: Ty) : string =
    $"a sequenced expression must be unit; this one is {formatTy ty} — bind it or print it{captureDropHint first}"

// `| exec` inside a resource scope is refused [D:exec-scope-refused]:
// exec replaces weir's process, so the scope's teardown (and an always
// block) would never run. Lexical here, like exit inside always — the
// first exec site under the expression, if any; an exec reached through
// a helper is the post-check layer's (Purity.execScopeViolation)
// [D:exec-scope-transitive]
let private execSite (e: Expr) : Ast.Span option =
    let rec go (e: Expr) =
        match e.Kind with
        | EVar v when v.StartsWith "|execed" -> Some e.Span
        | _ -> Ast.exprChildren e |> List.tryPick go

    go e

/// what a scope loses to an exec inside it; None for the kinds that
/// refuse every command already (pure/readonly/plan)
let execScopeLoss (kind: WithinKindId) : string option =
    match kind with
    | WithinTmp -> Some "the temporary directory would never be removed"
    | WithinProc -> Some "the scoped process would never be stopped"
    | WithinLock -> Some "the lock would never be released"
    | WithinServe -> Some "the server would never be stopped"
    | WithinCd
    | WithinEnv -> Some "the scope would never close"
    | _ -> None

// the refusal's subject: the exec itself, or the helper whose body
// reaches one [D:exec-scope-transitive]
let private execSubject (helper: string option) : string =
    match helper with
    | None -> "'exec'"
    | Some h -> $"'{h}' reaches 'exec' in its body, and 'exec'"

// `after`: the scope's own repair — exec once the scope has closed
let private execRepair (helper: string option) (after: bool) : string =
    match helper, after with
    | None, false -> "run the command without exec"
    | None, true -> "run the command without exec, or exec after the scope"
    | Some _, false -> "run its command without exec"
    | Some h, true -> $"run its command without exec, or call '{h}' after the scope"

/// the scope refusal [D:exec-scope-refused] [D:exec-scope-transitive]
let execInScopeMessage (kind: WithinKindId) (what: string) (helper: string option) : string =
    $"{execSubject helper} inside 'within {withinKindName kind}' is refused — exec replaces weir's process, so {what}; {execRepair helper true}"

/// the always refusals: in the guarded body, or in the cleanup itself
let execInAlwaysMessage (inCleanup: bool) (helper: string option) : string =
    if inCleanup then
        $"{execSubject helper} inside always is refused — exec replaces weir's process, so the rest of the teardown would never run; {execRepair helper false}"
    else
        $"{execSubject helper} inside a scope with always is refused — exec replaces weir's process, so the always block would never run; {execRepair helper true}"

let private execInScope (kind: WithinKindId) (body: Expr) : Result<unit, TypeError> =
    match execScopeLoss kind, execSite body with
    | Some what, Some sp ->
        Error {
            Message = execInScopeMessage kind what None
            Span = sp
            Origin = None
        }
    | _ -> Ok()

let private execInAlways (body: Expr) (cleanup: Expr) : Result<unit, TypeError> =
    match execSite body, execSite cleanup with
    | Some sp, _ ->
        Error {
            Message = execInAlwaysMessage false None
            Span = sp
            Origin = None
        }
    | None, Some sp ->
        Error {
            Message = execInAlwaysMessage true None
            Span = sp
            Origin = None
        }
    | None, None -> Ok()

// a diverging tail [D:fail-bottom]: every path out of the expression
// ends in fail/exit (reserved names, un-shadowable), so its value is a
// fresh var no position should have to name. Shared by the unit-position
// carves (ESeq head, else-less if) and the statement gate.
let rec divergesTo (x: Expr) : bool =
    // exec replaces the process [D:exec] — a diverging reifier like
    // fail/exit; its desugar is a `|execed`/`|execedEnv` application spine
    let rec execSpine (e: Expr) =
        match e.Kind with
        | EVar v when v.StartsWith "|execed" -> true
        | EApp(f, _) -> execSpine f
        | _ -> false

    match x.Kind with
    | EApp({ Kind = EVar("fail" | "exit") }, _) -> true
    | EApp _ when execSpine x -> true
    | ESeq(_, b)
    | ELet(_, _, _, b)
    | ELetPat(_, _, b)
    | EWithin(_, _, _, _, b)
    | EAlways(b, _)
    | EHold b
    | ECapture b -> divergesTo b
    | EIf(_, t, Some e) -> divergesTo t && divergesTo e
    | EMatch(_, arms) -> arms |> List.forall (fun (_, _, b) -> divergesTo b)
    | _ -> false

// the wire table [D:wire-table]: each type's stance at the JSON and
// YAML boundaries, in one match with no wildcard — a new Ty case does
// not compile until it says how it crosses, and both boundaries (both
// directions) read the same answer. A Canonical type has one encoding
// and crosses as itself; a Refused one teaches the explicit conversion
// (its encoding is the consumer's convention [D:unit-types-wire]); a
// Walk type is the admission walk's own (compounds, unit, variables).
type private WireStance =
    // the yaml read shape rides along, so a canonical type cannot exist
    // without saying how a document reads it
    | Canonical of yaml: Yaml.Shape
    // refused on its own, crossing under a declared codec [D:wire-codecs]
    | Encoded of codecs: Codec list * subject: string * repair: string
    | Refused of teach: (string -> string)
    | Walk

let private wireStance (ty: Ty) : WireStance =
    let unrepresentable (subject: string) (repair: string) =
        Refused(fun fmt -> $"{subject} is not representable in {fmt} — {repair}")

    match ty with
    | TInt -> Canonical Yaml.SInt
    | TFloat -> Canonical Yaml.SFloat
    | TStr -> Canonical Yaml.SStr
    | TBool -> Canonical Yaml.SBool
    | TUuid -> Canonical Yaml.SUuid
    | TInstant ->
        Encoded(
            [ Iso8601; EpochMs; EpochSec ],
            "an Instant",
            "convert explicitly (Instant.epochMs into an int field, or show for an ISO 8601 string)"
        )
    | TDur ->
        Encoded(
            [ Millis; Seconds ],
            "Duration",
            "convert explicitly (Duration.toMillis into an int field, or show for a string)"
        )
    | TSize ->
        Encoded([ ByteCount ], "Size", "convert explicitly (Size.toBytes into an int field, or show for a string)")
    | TBytes -> unrepresentable "Bytes" "Bytes.toBase64 into a string field"
    // the type itself refuses, not an encoding [D:secret]
    | TSecret ->
        Refused(fun fmt ->
            $"a Secret must not cross to {fmt} — Secret.reveal it into a string first if you truly mean to write it")
    | TUnit
    | TFun _
    | TSeq _
    | TTuple _
    | TNamed _
    | TVar _
    | TRowVar _ -> Walk

// the table as patterns, so each boundary's walk stays one match whose
// first arms are the table's answer
let private (|WireCanonical|WireRefused|WireWalk|) (ty: Ty) =
    match wireStance ty with
    | Canonical shape -> WireCanonical shape
    | Encoded(_, subject, repair) -> WireRefused(fun fmt -> $"{subject} is not representable in {fmt} — {repair}")
    | Refused teach -> WireRefused teach
    | Walk -> WireWalk

let private printArgTy (ctx: Ctx) (env: TypeEnv) (span: Span) (ty: Ty) : Result<Ty, TypeError> =
    match resolve ctx ty with
    | TVar _ as v -> bind ctx env span TStr v |> Result.map (fun () -> TStr)
    // the wire table's canonical types print as their one text
    // [D:print-canonical] — the next canonical scalar prints unasked
    | WireCanonical _ as t -> Ok t
    // unit prints as nothing [D:exit-reifiers]: the armed-statement
    // desugar wraps commands in print, and `| orFail` interiors are
    // unit — one rule instead of a shadow drain builtin
    | TUnit -> Ok TUnit
    | TSeq inner ->
        (match resolve ctx inner with
         | TVar _ as v -> bind ctx env span TStr v |> Result.map (fun () -> TSeq TStr)
         | TStr -> Ok(TSeq TStr)
         | TUnit -> err span "print cannot take seq<unit> — a lazy effect sequence never runs; use Seq.iter"
         | t -> err span $"print takes a string, int, float, bool, Uuid, or seq<string>; this is {formatTy (TSeq t)}")
    | TSecret ->
        // a Secret refuses print [D:secret] — show s prints ***; the value
        // is the deliberate Secret.reveal
        err span "print will not render a Secret — show s prints ***, or Secret.reveal s to print its value"
    | TBytes ->
        // Bytes refuses print [D:bytes] — raw bytes wreck a terminal
        // (gzip output, say); the alternatives are deliberate
        err span "print will not render Bytes — Bytes.toBase64 b for text, or File.writeBytes path b for a file"
    | t -> err span $"print takes a string, int, float, bool, Uuid, or seq<string>; this is {formatTy t}"

// the (op) desugar target [D:operator-values]: `fun a b -> a op b`
// verbatim — params carry the un-typeable '|' prefix so nothing can
// capture them; both the infer and check arms delegate here
let private opValueLambda (op: string) (sp: Span) : Expr =
    let v n = { Kind = EVar n; Span = sp }

    {
        Kind =
            ELambda(
                "|opL",
                sp,
                {
                    Kind =
                        ELambda(
                            "|opR",
                            sp,
                            {
                                Kind = EBinOp(op, v "|opL", v "|opR")
                                Span = sp
                            }
                        )
                    Span = sp
                }
            )
        Span = sp
    }

let rec private typeBinOp
    (ctx: Ctx)
    (env: TypeEnv)
    (opSpan: Span)
    (op: string)
    (l: TypedExpr)
    (r: TypedExpr)
    : Result<Ty, TypeError> =
    let retryAfter binding =
        binding |> Result.bind (fun () -> typeBinOp ctx env opSpan op l r)

    match op, resolve ctx l.Ty, resolve ctx r.Ty with
    // '=' parses only to carry this teaching (PLAN-dx-review D3): the
    // docs' most-emphasised difference deserves a real message
    | "=", _, _ -> err opSpan "use '==' for equality; '=' binds in let and record fields"
    // composition [D:composition-operators] — fully parametric, typed
    // like a builtin scheme: (a -> b) >> (b -> c) : a -> c (left to
    // right only [D:left-to-right-ops]). First in the match: the scalar-defaulting arms below
    // must never touch function operands. A non-function LHS on `>>`
    // gets the redirect-aware message (bash muscle memory).
    | ">>", lt, _ ->
        let a = TVar(freshName ctx "a")
        let b = TVar(freshName ctx "b")
        let c = TVar(freshName ctx "c")

        match op, lt with
        | ">>", (TFun _ | TVar _) ->
            bind ctx env l.Span (TFun(a, b)) l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span (TFun(b, c)) r.Ty)
            |> Result.map (fun () -> TFun(a, c))
        | ">>", ty ->
            err
                l.Span
                ($"'>>' composes functions, and this expression has type {formatTy ty}; "
                 + "to append command output to a file, pipe it: cmd |> File.append \"out.txt\"")
        | _, ty -> err l.Span $"'<<' composes functions, and this expression has type {formatTy ty}"
    | ("*" | "/" | "%" | "-" | ">" | "<" | ">=" | "<="), TVar _, TVar _ ->
        retryAfter (
            bind ctx env l.Span (TInt) l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span (TInt) r.Ty)
        )
    | ("&&" | "||"), TVar _, TVar _ ->
        retryAfter (
            bind ctx env l.Span TBool l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span TBool r.Ty)
        )
    // a point plus an unknown is a shift [D:instant-plus-infer]: only a
    // Duration adds to an Instant, so the unknown side is one — the rule
    // the deferred `+` already applies once a side resolves to Instant
    | "+", TVar _, TInstant -> retryAfter (bind ctx env l.Span TDur l.Ty)
    | "+", TInstant, TVar _ -> retryAfter (bind ctx env r.Span TDur r.Ty)
    | _, TVar _, ((TInt | TFloat | TStr | TBool | TDur | TSize | TInstant | TUuid) as t) ->
        retryAfter (bind ctx env l.Span t l.Ty)
    | _, ((TInt | TFloat | TStr | TBool | TDur | TSize | TInstant | TUuid) as t), TVar _ ->
        retryAfter (bind ctx env r.Span t r.Ty)
    | ("==" | "<>"), a, b ->
        // Eq via the class solver: concrete failures keep the
        // pre-class message verbatim; unresolved operands defer (the
        // constraint rides the var) instead of rejecting at the operator
        bind ctx env opSpan a b
        |> Result.bind (fun () ->
            let p = {
                Cls = Cls.Eq
                Span = opSpan
                Describe =
                    fun _ t ->
                        if tyHasFloat t then
                            floatEqTeaching
                        elif floatInsideNamed env t then
                            $"'{op}' is not defined for {formatTy t}: it holds a float, and floats cannot be compared with '==' (0.1 + 0.2 is not 0.3) — compare the float fields with Float.near a b eps, or after Float.round"
                        elif (opaqueNominal t).IsSome then
                            $"'{op}' is not defined for {formatTy t}; an opaque value cannot be compared"
                        else
                            $"'{op}' is not defined for {formatTy t}; sequences and functions cannot be compared"
            }

            demand ctx env p a |> Result.map (fun () -> TBool))
    // `+` on two unknowns DEFERS [D:plus-defer]: int, float, string,
    // Size, Duration (and Instant + Duration) all add, so either
    // operand or the result — whichever resolves first, anywhere later
    // in the statement — decides it (`a + b + 0` as `0 + a + b`). Still
    // undecided at the statement boundary: the cannot-infer teaching
    | "+", TVar _, TVar _ ->
        let res = TVar(freshName ctx "a")
        let id = ctx.Fresh
        ctx.AddsOpen <- Map.add id (opSpan, [ l.Ty; r.Ty; res ]) ctx.AddsOpen

        let rec attempt () : Result<unit, TypeError> =
            if not (ctx.AddsOpen.ContainsKey id) then
                Ok()
            else
                let close () =
                    ctx.AddsOpen <- Map.remove id ctx.AddsOpen

                let retry () =
                    typeBinOp ctx env opSpan op l r
                    |> Result.bind (fun t -> bind ctx env opSpan res t)

                let pin (side: TypedExpr) t =
                    close ()
                    bind ctx env side.Span t side.Ty |> Result.bind retry

                match resolve ctx l.Ty, resolve ctx r.Ty, resolve ctx res with
                | TVar _, TVar _, (TVar _ | TInstant) -> park ()
                // the result decides both operands (each addable type
                // adds only to itself); anything else gets the operator's
                // own refusal
                | TVar _, TVar _, t ->
                    close ()

                    bind ctx env l.Span t l.Ty
                    |> Result.bind (fun () -> bind ctx env r.Span t r.Ty)
                    |> Result.bind (fun () -> typeBinOp ctx env opSpan op l r |> Result.map ignore)
                // a point shifts only by a Duration, and only a point
                // plus a Duration is a point
                | TInstant, TVar _, _ -> pin r TDur
                | TVar _, TInstant, _ -> pin l TDur
                | TDur, TVar _, TInstant -> pin r TInstant
                | TVar _, TDur, TInstant -> pin l TInstant
                | _ ->
                    close ()
                    retry ()

        and park () =
            for t in [ l.Ty; r.Ty; res ] do
                match resolve ctx t with
                | TVar v ->
                    let fs = Map.tryFind v ctx.Adds |> Option.defaultValue []

                    if not (fs |> List.exists (fun (k, _) -> k = id)) then
                        ctx.Adds <- Map.add v ((id, attempt) :: fs) ctx.Adds
                | _ -> ()

            Ok()

        park () |> Result.map (fun () -> res)
    | _, TVar _, TVar _ -> err opSpan $"cannot infer the operand types of '{op}'; pipe data in or use concrete values"
    | _, TRowVar _, _
    | _, _, TRowVar _ -> err opSpan $"operator '{op}' is not defined for records"
    // modulo is int-only [D:modulo]: weir floats are finite-only and
    // IEEE `x % 0.0` is NaN — float modulo would need a divergent
    // raise, declined until its own ruling; the message names the exits
    | "%", TFloat, _
    | "%", _, TFloat ->
        err
            opSpan
            "'%' is integer-only — weir floats are finite-only, and IEEE float remainder can produce NaN; Float.toInt one side, or keep the cycle arithmetic in int"
    | "%", TInt, TInt -> Ok TInt
    | ("+" | "-" | "*" | "/"), TFloat, TFloat -> Ok TFloat
    | (">" | "<" | ">=" | "<="), TFloat, TFloat -> Ok TBool
    // no implicit widening [D:floats] — the one deliberate footgun,
    // and its message is the most likely first stumble
    | ("+" | "-" | "*" | "/" | ">" | "<" | ">=" | "<="), TInt, TFloat
    | ("+" | "-" | "*" | "/" | ">" | "<" | ">=" | "<="), TFloat, TInt ->
        err opSpan $"'{op}' needs both sides the same type; wrap the int: Float.ofInt x {op} y"
    | ("+" | "-"), TSize, TSize -> Ok TSize
    | "*", TSize, TInt -> Ok TSize
    | "*", TInt, TSize -> Ok TSize
    | "/", TSize, TInt -> Ok TSize
    | "/", TSize, TSize ->
        err
            opSpan
            "size ÷ size has no unit — Size.toBytes a / Size.toBytes b gives the integer ratio (Float.ofInt each for a fraction)"
    | (">" | "<" | ">=" | "<="), TSize, TSize -> Ok TBool
    | ("+" | "-"), TDur, TDur -> Ok TDur
    | "*", TDur, TInt -> Ok TDur
    | "*", TInt, TDur -> Ok TDur
    | "/", TDur, TInt -> Ok TDur
    | "/", TDur, TDur ->
        err
            opSpan
            "duration ÷ duration has no unit — Duration.toMillis d1 / Duration.toMillis d2 gives the integer ratio"
    | (">" | "<" | ">=" | "<="), TDur, TDur -> Ok TBool
    // Instant arithmetic [D:instant]: two points subtract to the
    // Duration between them; a point shifts by a Duration (both
    // spellings — addition commutes); points never add
    | "-", TInstant, TInstant -> Ok TDur
    | "+", TInstant, TDur -> Ok TInstant
    | "+", TDur, TInstant -> Ok TInstant
    | "-", TInstant, TDur -> Ok TInstant
    | "+", TInstant, TInstant ->
        err opSpan "two points don't add — subtract them ('-') for the Duration between, or shift one by a Duration"
    | "-", TDur, TInstant -> err opSpan "a Duration minus a point has no meaning; flip it: instant - duration"
    | (">" | "<" | ">=" | "<="), TInstant, TInstant -> Ok TBool
    | ("+" | "-"), TInt, TInt -> Ok TInt
    | "+", TStr, TStr -> Ok TStr
    | ("*" | "/"), TInt, TInt -> Ok(TInt)
    | (">" | "<" | ">=" | "<="), TInt, TInt -> Ok TBool
    | ("&&" | "||"), TBool, TBool -> Ok TBool
    // the operators admit what sorts [D:ord-scalar-operators]: string
    // (ordinal) and bool (false first) compare as Seq.sort orders them;
    // an Option by its content, None first. Uuid stays out — an
    // identifier is not a quantity [D:uuid]
    | (">" | "<" | ">=" | "<="), TStr, TStr
    | (">" | "<" | ">=" | "<="), TBool, TBool -> Ok TBool
    | (">" | "<" | ">=" | "<="), (TNamed("Option", [ _ ]) as a), (TVar _ | TNamed("Option", [ _ ]))
    | (">" | "<" | ">=" | "<="), TVar _, (TNamed("Option", [ _ ]) as a) ->
        bind ctx env r.Span (resolve ctx l.Ty) (resolve ctx r.Ty)
        |> Result.bind (fun () ->
            let p = {
                Cls = Cls.Cmp
                Span = opSpan
                Describe = cmpDescribe (Some op)
            }

            demand ctx env p a)
        |> Result.map (fun () -> TBool)
    // an Option against anything else is an operand mismatch — the
    // operators are defined for an Option [D:ord-scalar-operators]
    | (">" | "<" | ">=" | "<="), (TNamed("Option", [ _ ]) as a), b -> mismatch r.Span a b
    // an ordered type compares with its own kind [D:derived-ord] — a
    // declared type is a quantity by declaration (the Instant side of
    // the Instant/Uuid split); an unknown other side binds to it, the
    // scalar defaulting arms' shape
    | (">" | "<" | ">=" | "<="), (TNamed(n, _) as a), TVar _ when ordDeclared env n ->
        retryAfter (bind ctx env r.Span a r.Ty)
    | (">" | "<" | ">=" | "<="), TVar _, (TNamed(n, _) as b) when ordDeclared env n ->
        retryAfter (bind ctx env l.Span b l.Ty)
    | (">" | "<" | ">=" | "<="), (TNamed(n, _) as a), (TNamed(m, _) as b) when n = m && ordDeclared env n ->
        bind ctx env r.Span a b |> Result.map (fun () -> TBool)
    | (">" | "<" | ">=" | "<="), (TNamed(n, _) as a), b when ordDeclared env n -> mismatch r.Span a b
    // the author's own record or union without a key names the
    // repair; the prelude's (Option) and anonymous shapes keep the bare
    // refusal
    | (">" | "<" | ">=" | "<="), (TNamed(n, _) as a), _ when (authorDeclared env n).IsSome ->
        err
            opSpan
            $"operator '{op}' is not defined for {formatTy a} — number its {(authorDeclared env n).Value} with [<Ord n>] to compare it"
    // …nor on the right with the left still open: the unknown side is
    // no mismatch to report
    | _, TVar _, ((TNamed _ | TSeq _ | TTuple _ | TUnit) as b) ->
        err opSpan $"operator '{op}' is not defined for {formatTy b}"
    // no operator here is defined for these on the left, whatever the
    // right side is — say so before any operand mismatch
    | _, ((TNamed _ | TSeq _ | TTuple _ | TUnit) as a), _ ->
        err opSpan $"operator '{op}' is not defined for {formatTy a}"
    | _, (TInt as a), (TInt as b) when a <> b -> mismatch r.Span a b
    | _, a, b when a <> b ->
        let shorthandHint =
            let isShorthand (side: TypedExpr) =
                match side.Kind with
                | TELambda("_", _, _) -> true
                | _ -> false

            if isShorthand l || isShorthand r then
                " (note: _.Field is a whole function; to compare the field, write fun x -> x.Field ...)"
            else
                unwrapHint a b

        err r.Span $"expected {formatTy a}, got {formatTy b}{shorthandHint}"
    | _, a, _ -> err opSpan $"operator '{op}' is not defined for {formatTy a}"

let rec private funParamsGo (ctx: Ctx) (arity: int) (acc: Ty list) (ty: Ty) : (Ty list * Ty) option =
    if arity = 0 then
        Some(List.rev acc, ty)
    else
        match resolve ctx ty with
        | TFun(dom, cod) -> funParamsGo ctx (arity - 1) (dom :: acc) cod
        | _ -> None

let private funParams (ctx: Ctx) (arity: int) (ty: Ty) : (Ty list * Ty) option = funParamsGo ctx arity [] ty

let rec private spine (e: Expr) : Expr * Expr list =
    match e.Kind with
    | EApp(fn, arg) ->
        let head, args = spine fn
        head, args @ [ arg ]
    | _ -> e, []

// the recursive json field law [D:recursive-fields]: a field is
// admitted if it is a scalar (int, float, string, bool), an Option of
// an admitted type, a record whose fields are all admitted, or a seq
// of an admitted type. `seen` is the ordered declaration path (the
// yaml guard's job, but here a cycle can name its path); `path`
// locates the offending field from the top — a recursive law's
// failures are deep and a category list alone will not find them.

// the scalar a codec on a field encodes [D:wire-codecs]: the field type
// itself, or the element inside one Option or seq wrapper
let private codecTarget (ty: Ty) : Ty =
    match ty with
    | TNamed("Option", [ inner ])
    | TSeq inner -> inner
    | t -> t

let private codecsOf (ty: Ty) : Codec list =
    match wireStance (codecTarget ty) with
    | Encoded(cs, _, _) -> cs
    | _ -> []

let private codecAttrs (cs: Codec list) : string =
    match cs |> List.map (fun c -> $"[<{codecName c}>]") with
    | [ one ] -> one
    | many ->
        (many |> List.take (many.Length - 1) |> String.concat ", ")
        + " or "
        + List.last many

// an uncoded field whose type needs one: the refusal names the
// attributes first [D:wire-codecs] — at a field, where one can attach
let private uncodedFieldTeach (fmt: string) (fty: Ty) : string option =
    match wireStance (codecTarget fty) with
    | Encoded(cs, subject, repair) ->
        Some
            $"{subject} is not representable in {fmt} on its own — declare the field's encoding ({codecAttrs cs}), or {repair}"
    | _ -> None

// a generic value at a write boundary: serialization is resolved at
// check time from the type, so a helper's unconstrained parameter has
// nothing to resolve — teach the repair, not the type variable
let private genericWireTeach (fmt: string) (ty: Ty) : string =
    $"'to {fmt}' needs a concrete type, but this value is generic ({formatTy ty}) — the encoding is chosen from the type at check time; call 'to {fmt}' where the type is known (at the call site, passing the rendered text on)"

// a generic record at the wire [D:wire-generics]: every fully
// instantiated generic record the type reaches becomes a monomorphic
// def keyed by its rendered type (`B<int>`) in a wire-local env, so the
// field law runs after substitution and the readers/writers see only
// monomorphic names. The def keeps its declared name — values, renames
// and codecs stay B's own. A var left in the args stays generic and
// meets the boundary's refusal; the depth cap stops polymorphic
// recursion (`L<'a>` holding `L<L<'a>>`) expanding forever.
let private wireMono (ctx: Ctx) (env: TypeEnv) (ty: Ty) : Ty * TypeEnv =
    let mutable types = env.Types
    let visited = System.Collections.Generic.HashSet<string>()

    let rec go (depth: int) (ty: Ty) =
        match resolve ctx ty with
        | TSeq t -> TSeq(go depth t)
        | TTuple ts -> TTuple(ts |> List.map (go depth))
        | TNamed(n, (_ :: _ as args)) as t ->
            let args = args |> List.map (go depth)
            let t' = TNamed(n, args)

            match typeDefFor env n with
            | Some(Record def) when
                def.Params.Length = args.Length
                && depth < 32
                && Set.isEmpty (tyVars (finalTy ctx t))
                ->
                let key = formatTy t'

                if not (types.ContainsKey key) then
                    // a placeholder first: a cycle back to this instance
                    // meets the boundary's cycle refusal, not a loop
                    types <- Map.add key (Record { def with Params = [] }) types

                    let fields =
                        def.Fields
                        |> List.map (fun (f, fty) -> f, go (depth + 1) (substParams def.Params args fty))

                    types <-
                        Map.add
                            key
                            (Record {
                                def with
                                    Params = []
                                    Fields = fields
                            })
                            types

                TNamed(key, [])
            | _ -> t'
        // a monomorphic record or tagged union may hold an instance in
        // a field or payload: its def is rewritten in the wire-local env
        | TNamed(n, []) as t when not (visited.Contains n) ->
            visited.Add n |> ignore

            match typeDefFor env n with
            | Some(Record def) when def.Params.IsEmpty ->
                let fields = def.Fields |> List.map (fun (f, fty) -> f, go depth fty)

                if fields <> def.Fields then
                    types <- Map.add n (Record { def with Fields = fields }) types
            | Some(Union udef) when udef.Params.IsEmpty ->
                let cases = udef.Cases |> List.map (fun (c, p) -> c, p |> Option.map (go depth))

                if cases <> udef.Cases then
                    types <- Map.add n (Union { udef with Cases = cases }) types
            | _ -> ()

            t
        | t -> t

    let ty' = go 0 ty
    ty', { env with Types = types }

// one teaching for every route a non-string Map key reaches the wire by
// [D:map-keys]: a declared field, an anonymous shape, the adapter slot
let private mapKeyTeach (k: Ty) =
    $"JSON object keys are strings — a Map<{formatTy k}, …> can't cross; convert with Map.pairs |> Seq.map (fun (k, v) -> (show k, v)) |> Map.ofPairs"

// a written Map key needs equality [D:map-keys] — the class `==` asks,
// so a float key (or a record holding one) refuses as `==` would; a
// type variable passes here and meets the class at the use site
let private mapKeyEq (env: TypeEnv) (span: Span) (k: Ty) : Result<unit, TypeError> =
    demand
        (newCtx ())
        env
        {
            Cls = Cls.Eq
            Span = span
            Describe =
                fun env t ->
                    let why =
                        if t = TFloat then
                            "floats cannot be compared with '==' (0.1 + 0.2 is not 0.3)"
                        elif tyHasFloat t || floatInsideNamed env t then
                            "it holds a float, and floats cannot be compared with '==' (0.1 + 0.2 is not 0.3)"
                        elif (opaqueNominal t).IsSome then
                            "an opaque value cannot be compared"
                        else
                            "sequences, functions and Maps cannot be compared"

                    $"a Map key needs equality, and {formatTy t} has none — {why}; key by an equatable form instead (a string, an int, a tuple of those)"
        }
        k

let private jsonAdmittedSet =
    "json fields are int, float, string, bool, Uuid, Option of an admitted type, a record of admitted fields, seq of an admitted type, a seq<string * T> mapping, Map<string, T> of one, or an opaque Yaml node"

let rec private jsonAdmitted
    (reading: bool)
    (span: Span)
    (env: TypeEnv)
    (seen: string list)
    (path: string)
    (ty: Ty)
    : Result<unit, TypeError> =
    let at = if path = "" then "" else $"field '{path}': "

    match ty with
    | WireCanonical _ -> Ok()
    // reading INTO a Secret is the safe direction [D:secret-read]: the
    // string wraps on the way in, as Env.load wraps
    | TSecret when reading -> Ok()
    | WireRefused teach -> err span (at + teach "JSON")
    | TNamed("Option", [ TNamed("Option", _) ]) ->
        err span $"{at}Option<Option<…>> has no JSON reading; flatten the type"
    | TNamed("Option", [ inner ]) -> jsonAdmitted reading span env seen path inner
    // the open mapping seq<string * V> — yaml's [D:yaml-v1] pair-seq law
    // at the json boundary [D:repl-infer]: a JSON object whose keys are
    // data reads/writes as pairs, admitted iff V is
    | TSeq(TTuple [ TStr; v ]) -> jsonAdmitted reading span env seen path v
    | TSeq elem -> jsonAdmitted reading span env seen path elem
    // the ID-keyed object [D:map-string]: Map<string, T> is a JSON
    // object whose keys are data — admitted iff T is
    | TNamed("Map", [ TStr; inner ]) -> jsonAdmitted reading span env seen path inner
    // cascade suppression [PLAN-diagnostics-arc B6]: a hole stands for
    // an already-reported error — never print its name
    | TVar v
    | TNamed("Map", [ TVar v; _ ]) when v.StartsWith "__hole" -> Ok()
    // a key still open where the value is written: the generic teaching
    | TNamed("Map", [ TVar v; _ ]) when not reading && not (v.StartsWith "__hole") ->
        err span (at + genericWireTeach "json" ty)
    // any other key stays in the program [D:map-keys] — the slot's
    // teaching, through declared fields and anonymous shapes alike
    | TNamed("Map", [ k; _ ]) -> err span (at + mapKeyTeach k)
    | TNamed("Map", args) -> err span $"{at}Map takes two type arguments (Map<K, V>); got {args.Length}"
    // an opaque `Yaml` node holds any JSON subtree whole — JSON is a
    // YAML subset [D:json-yaml-field]
    | TNamed("Yaml", []) -> Ok()
    | TNamed(n, []) when seen |> List.contains n ->
        let cycle =
            ((seen |> List.rev |> List.skipWhile ((<>) n)) @ [ n ]) |> String.concat " → "

        err span $"{at}the type cycle {cycle} cannot cross the JSON boundary — it needs finite trees"
    | TNamed(n, []) ->
        match typeDefFor env n with
        | Some(Record def) when def.Params.IsEmpty ->
            allOk def.Fields (fun (fn, fty) ->
                jsonField reading span env (n :: seen) def (if path = "" then fn else $"{path}.{fn}") fn fty)
        | Some(Record _) -> err span $"{at}'{n}' is generic; the JSON boundary needs monomorphic records"
        // a tagged union crosses [D:wire-unions]: every case's payload
        // record must admit; the [<Other>] case is string-or-nothing by
        // declaration, admitted by construction
        | Some(Union udef) when udef.Tag.IsSome ->
            allOk udef.Cases (fun (c, payload) ->
                match payload with
                | Some pty when Some c <> udef.OtherCase ->
                    jsonAdmitted reading span env (n :: seen) (if path = "" then c else $"{path}.{c}") pty
                | _ -> Ok())
        // a validated type crosses as its payload [D:validated-types]
        | Some(Union({ Cases = [ (_, Some pty) ] } as udef)) when udef.Validated.IsSome ->
            jsonAdmitted reading span env (n :: seen) path pty
        | Some(Union _) ->
            err
                span
                $"{at}'{n}' is an untagged union — a union crosses the wire with [<Tag \"field\">] on its declaration; {jsonAdmittedSet}"
        | None when List.contains n deflessBuiltinNominals ->
            err span $"{at}{n} is an opaque value with no JSON form — encode the fields you need into a record"
        | None -> err span $"{at}{unknownTypeMsg env n}"
    // cascade suppression [PLAN-diagnostics-arc B6] through a tuple or
    // a generic argument too: the hole's error is already reported
    | ty when tyVars ty |> Set.exists (fun v -> v.StartsWith "__hole") -> Ok()
    | TVar v when not reading && not (v.StartsWith "__hole") -> err span (at + genericWireTeach "json" ty)
    // a generic record left uninstantiated [D:wire-generics]
    | TNamed(_, _ :: _) when not reading && not (Set.isEmpty (tyVars ty)) -> err span (at + genericWireTeach "json" ty)
    | ty -> err span $"{at}type {formatTy ty} is not admitted; {jsonAdmittedSet}"

// one field's admission, for the record walk and the top-level record
// alike: a coded field crosses under its codec [D:wire-codecs]
and private jsonField
    (reading: bool)
    (span: Span)
    (env: TypeEnv)
    (seen: string list)
    (def: RecordDef)
    (fpath: string)
    (fn: string)
    (fty: Ty)
    : Result<unit, TypeError> =
    match fieldCodec def fn, uncodedFieldTeach "JSON" fty with
    | Some _, _ -> Ok()
    | None, Some teach -> err span $"field '{fpath}': {teach}"
    | None, None -> jsonAdmitted reading span env seen fpath fty

let private jsonableElem (reading: bool) (span: Span) (env: TypeEnv) (elem: Ty) : Result<unit, TypeError> =
    jsonAdmitted reading span env [] "" elem

// the read-only XML boundary [D:from-xml]: XML carries text, so every
// leaf is a string — an element's inner text or, with [<Attr>], one of
// its attributes. seq< > reads repeated child elements; a record reads a
// nested element. No numbers/bools (declare string, convert with
// Str.toInt), no Map/union (XML has no keyed-object or tag convention).
let private xmlAdmittedSet =
    "XML fields are string, Option<string>, a record, or seq of a string or record — every leaf is text"

// [<Attr>] reads an attribute's text and [<Elem>] names a repeated child;
// each only fits certain field types, checked where the def is in hand
let private xmlFieldOk
    (span: Span)
    (def: RecordDef)
    (name: string)
    (fty: Ty)
    (fpath: string)
    : Result<unit, TypeError> =
    let isAttr = (xmlAttr def name).IsSome

    let hasElem =
        match Map.tryFind name def.Attrs with
        | Some specs -> specs |> List.exists (fun (n, _) -> n = "Elem")
        | None -> false

    if isAttr then
        match fty with
        | TStr
        | TNamed("Option", [ TStr ]) -> Ok()
        | _ ->
            err
                span
                $"field '{fpath}': [<Attr>] reads an attribute's text — the field must be string or Option<string>, not {formatTy fty}"
    elif hasElem then
        match fty with
        | TSeq _ -> Ok()
        | _ ->
            err
                span
                $"field '{fpath}': [<Elem>] names a repeated child element — the field must be a seq, not {formatTy fty}"
    else
        Ok()

let rec private xmlAdmitted
    (span: Span)
    (env: TypeEnv)
    (seen: string list)
    (path: string)
    (ty: Ty)
    : Result<unit, TypeError> =
    let at = if path = "" then "" else $"field '{path}': "

    match ty with
    | TStr -> Ok()
    | TInt
    | TFloat
    | TBool ->
        err
            span
            $"{at}XML values are text — declare {formatTy ty} as string (convert with Str.toInt / Str.toFloat / Str.toBool)"
    | TNamed("Option", [ TStr ]) -> Ok()
    | TNamed("Option", [ inner ]) ->
        err
            span
            $"{at}XML reads Option<string> only (a present-or-absent element or attribute); got Option<{formatTy inner}>"
    | TSeq elem ->
        match elem with
        | TStr -> Ok()
        | TNamed(n, []) when
            (match typeDefFor env n with
             | Some(Record _) -> true
             | _ -> false)
            ->
            xmlAdmitted span env seen path elem
        | _ -> err span $"{at}an XML seq holds strings or records; got seq<{formatTy elem}>"
    | TNamed(n, []) when seen |> List.contains n ->
        let cycle =
            ((seen |> List.rev |> List.skipWhile ((<>) n)) @ [ n ]) |> String.concat " → "

        err span $"{at}the type cycle {cycle} cannot cross the XML boundary — it needs finite trees"
    | TNamed(n, []) ->
        match typeDefFor env n with
        | Some(Record def) when def.Params.IsEmpty ->
            allOk def.Fields (fun (fn, fty) ->
                let fpath = if path = "" then fn else $"{path}.{fn}"

                xmlFieldOk span def fn fty fpath
                |> Result.bind (fun () -> xmlAdmitted span env (n :: seen) fpath fty))
        | Some(Record _) -> err span $"{at}'{n}' is generic; the XML boundary needs monomorphic records"
        | Some(Union _) -> err span $"{at}'{n}' is a union — XML has no tagged-union reading; {xmlAdmittedSet}"
        | None when List.contains n deflessBuiltinNominals ->
            err span $"{at}{n} is an opaque value with no XML form — encode the fields you need into a record"
        | None -> err span $"{at}{unknownTypeMsg env n}"
    | ty -> err span $"{at}type {formatTy ty} is not admitted; {xmlAdmittedSet}"

let private xmlableRecord (span: Span) (env: TypeEnv) (def: RecordDef) : Result<unit, TypeError> =
    allOk def.Fields (fun (name, ty) ->
        xmlFieldOk span def name ty name
        |> Result.bind (fun () -> xmlAdmitted span env [ def.Name ] name ty))

// the aligned-table boundary [D:from-table]: a row is flat — each field
// reads one column's cell, so the admitted set is the scalars and Option
// of one (an empty or `<none>` cell reads as None). No nested records,
// seqs, or Maps: a cell is one aligned column's text.
let private tableAdmittedSet =
    "table cells are string, int, float, bool, or Option of one — each field reads one aligned column's cell"

let private tableableRecord (span: Span) (def: RecordDef) : Result<unit, TypeError> =
    allOk def.Fields (fun (name, ty) ->
        match ty with
        | TStr
        | TInt
        | TFloat
        | TBool
        | TNamed("Option", [ TStr | TInt | TFloat | TBool ]) -> Ok()
        | TNamed("Option", [ inner ]) ->
            err
                span
                $"field '{name}': a table cell reads Option of a scalar, not Option<{formatTy inner}> — {tableAdmittedSet}"
        | ty -> err span $"field '{name}': type {formatTy ty} is not admitted; {tableAdmittedSet}")

// the defs a shape reaches, for the reader [D:recursive-fields]: eval
// converts nested objects without an env, so the closure rides the
// typed node (the yamlShape pattern, by table instead of by tree).
// Also the write-side rename table [D:wire-keys]: attrs are erased
// from values, so `to json`/`to yaml` carry their record->field->wire
// map on the node, computed here at check (the attribute binds at
// consumption, per the attribute law)
let rec wireRenamesOf (env: TypeEnv) (ty: Ty) : Map<string, Map<string, string>> =
    let defs = jsonDefsClosure env Map.empty ty

    // keyed by the declared name a value carries — a generic record's
    // instances share it [D:wire-generics]
    defs
    |> Map.toList
    |> List.choose (fun (_, def) ->
        let n = def.Name

        let renamed =
            def.Fields
            |> List.choose (fun (f, _) ->
                let w = wireName def f
                if w <> f then Some(f, w) else None)

        if renamed.IsEmpty then
            None
        else
            Some(n, Map.ofList renamed))
    |> Map.ofList

// the write-side codec table [D:wire-codecs], from the same closure
and wireCodecsOf (env: TypeEnv) (ty: Ty) : Map<string, Map<string, Codec>> =
    jsonDefsClosure env Map.empty ty
    |> Map.toList
    |> List.choose (fun (_, def) ->
        let coded =
            def.Fields
            |> List.choose (fun (f, _) -> fieldCodec def f |> Option.map (fun c -> f, c))

        if coded.IsEmpty then
            None
        else
            Some(def.Name, Map.ofList coded))
    |> Map.ofList

and private jsonDefsClosure (env: TypeEnv) (acc: Map<string, RecordDef>) (ty: Ty) : Map<string, RecordDef> =
    match ty with
    | TNamed("Option", [ inner ]) -> jsonDefsClosure env acc inner
    // the mapping's value type carries the defs [D:repl-infer]
    | TSeq(TTuple [ TStr; inner ]) -> jsonDefsClosure env acc inner
    | TSeq elem -> jsonDefsClosure env acc elem
    | TNamed("Map", [ TStr; inner ]) -> jsonDefsClosure env acc inner
    | TNamed(n, []) when not (acc.ContainsKey n) ->
        match typeDefFor env n with
        | Some(Record def) ->
            def.Fields
            |> List.fold (fun a (_, fty) -> jsonDefsClosure env a fty) (Map.add n def acc)
        // the closure walks through a tagged union [D:wire-unions]: its
        // payload records ride so the reader/renames reach them (payloads
        // cannot reference the union back — declaration order forbids it)
        | Some(Union udef) when udef.Tag.IsSome || udef.Validated.IsSome ->
            udef.Cases
            |> List.fold
                (fun a (_, payload) ->
                    match payload with
                    | Some pty -> jsonDefsClosure env a pty
                    | None -> a)
                acc
        | _ -> acc
    | _ -> acc

// the tagged unions a shape reaches [D:wire-unions] — the reader's
// dispatch tables ride the TEFrom node (eval has no env)
let rec private jsonUnionsClosure (env: TypeEnv) (acc: Map<string, UnionDef>) (ty: Ty) : Map<string, UnionDef> =
    match ty with
    | TNamed("Option", [ inner ]) -> jsonUnionsClosure env acc inner
    // the mapping's value type carries the unions [D:repl-infer]
    | TSeq(TTuple [ TStr; inner ]) -> jsonUnionsClosure env acc inner
    | TSeq elem -> jsonUnionsClosure env acc elem
    | TNamed("Map", [ TStr; inner ]) -> jsonUnionsClosure env acc inner
    | TNamed(n, []) when not (acc.ContainsKey n) ->
        match typeDefFor env n with
        | Some(Record def) -> def.Fields |> List.fold (fun a (_, fty) -> jsonUnionsClosure env a fty) acc
        // a validated type rides too: its reader runs `create`, its
        // writer drops the case [D:validated-types]
        | Some(Union udef) when udef.Tag.IsSome || udef.Validated.IsSome ->
            udef.Cases
            |> List.fold
                (fun a (_, payload) ->
                    match payload with
                    | Some pty -> jsonUnionsClosure env a pty
                    | None -> a)
                (Map.add n udef acc)
        | _ -> acc
    | _ -> acc

// the writer's case table [D:wire-unions]: caseName -> WireCase
// over every tagged or validated union the serialized type
// reaches — a VUnion value carries no type name, so cases key the
// table, and two reachable unions sharing a case name refuse here
// (declarable under [D:ambiguous-ctor], unwritable in one closure)
let private unionWriteTable (span: Span) (env: TypeEnv) (ty: Ty) : Result<Map<string, WireCase>, TypeError> =
    let udefs = jsonUnionsClosure env Map.empty ty

    // keyed by the env key: two imports' same-named unions are two
    // unions [D:qualified-type-names]
    let entries =
        udefs
        |> Map.toList
        |> List.collect (fun (key, udef) ->
            let wire c =
                if udef.Validated.IsSome then
                    WireValidated
                else
                    WireTagged(udef.Tag |> Option.defaultValue "", caseWire udef c, Some c = udef.OtherCase)

            udef.Cases |> List.map (fun (c, _) -> c, (key, wire c)))

    let rec go (seen: Map<string, string * WireCase>) acc entries =
        match entries with
        | [] -> Ok acc
        | (c, (uname: string, wire)) :: rest ->
            match Map.tryFind c seen with
            // two validated cases write alike — the payload, no tag
            // [D:validated-types]
            | Some(prior, priorWire) when prior <> uname && not (priorWire = WireValidated && wire = WireValidated) ->
                err
                    span
                    $"case '{c}' belongs to tagged unions '{prior}' and '{uname}', both reachable from this type — the writer keys cases by name; rename one"
            | _ -> go (Map.add c (uname, wire) seen) (Map.add c wire acc) rest

    // a record value carries its declared name, which keys the rename
    // and codec tables — two imports' same-named records whose tables
    // differ cannot both be written by one closure [D:qualified-type-names]
    let tablesOf (def: RecordDef) =
        def.Fields |> List.map (fun (f, _) -> f, wireName def f, fieldCodec def f)

    let clash =
        jsonDefsClosure env Map.empty ty
        |> Map.toList
        |> List.groupBy (fun (_, def) -> def.Name)
        |> List.tryPick (fun (_, group) ->
            match group |> List.distinctBy (fun (_, def) -> tablesOf def) with
            | (k1, _) :: (k2, _) :: _ -> Some(k1, k2)
            | _ -> None)

    match clash with
    | Some(k1, k2) ->
        err
            span
            $"'{k1}' and '{k2}' share a declared name but not their wire keys or codecs, and both are reachable from this type — the writer keys records by that name; rename one"
    | None -> go Map.empty Map.empty entries

// ---- the yaml tree law [D:yaml-v1] — richer than json's flat-row law
// because YAML is a document format, not a row stream: scalars, nested
// monomorphic records, seq<elem>, seq<string * elem> (an open mapping),
// Option anywhere (None omits / null reads None). `seen` guards
// declaration cycles.

// the from-side: build the shape eval will convert through
let rec private yamlShape
    (span: Span)
    (env: TypeEnv)
    (seen: Set<string>)
    (path: string)
    (ty: Ty)
    : Result<Yaml.Shape, TypeError> =
    // the field path, as yamlableOut threads it — both directions name
    // the offending field [D:wire-table]
    let at = if path = "" then "" else $"field '{path}': "

    let sub (name: string) =
        if path = "" then name else $"{path}.{name}"

    match ty with
    | WireCanonical shape -> Ok shape
    // yamlShape is the read side: reading INTO a Secret is the safe
    // direction [D:secret-read]
    | TSecret -> Ok Yaml.SSecret
    | WireRefused teach -> err span (at + teach "yaml")
    // the opaque `Yaml` node reads structure whole [D:yaml-empty-flow] —
    // the read sibling of yamlableOut's `Yaml`-renders-directly case
    | TNamed("Yaml", []) -> Ok Yaml.SNode
    | TNamed("Option", [ TNamed("Option", _) ]) ->
        err span $"{at}Option<Option<…>> has no yaml reading; flatten the type"
    | TNamed("Option", [ inner ]) -> yamlShape span env seen path inner |> Result.map Yaml.SOpt
    | TSeq(TTuple [ TStr; v ]) -> yamlShape span env seen path v |> Result.map Yaml.SPairs
    | TSeq elem -> yamlShape span env seen path elem |> Result.map Yaml.SSeq
    | TNamed(n, []) ->
        if seen.Contains n then
            err span $"{at}'{n}' is recursive; the yaml boundary needs finite trees"
        else
            match typeDefFor env n with
            | Some(Record def) when def.Params.IsEmpty ->
                def.Fields
                |> List.fold
                    (fun acc (fname, fty) ->
                        acc
                        |> Result.bind (fun fs ->
                            // a coded field reads through its codec [D:wire-codecs]
                            let fieldShape =
                                match fieldCodec def fname, uncodedFieldTeach "yaml" fty with
                                | Some c, _ ->
                                    Ok(
                                        match fty with
                                        | TNamed("Option", [ _ ]) -> Yaml.SOpt(Yaml.SCodec c)
                                        | TSeq _ -> Yaml.SSeq(Yaml.SCodec c)
                                        | _ -> Yaml.SCodec c
                                    )
                                | None, Some teach -> err span $"field '{sub fname}': {teach}"
                                | None, None -> yamlShape span env (seen.Add n) (sub fname) fty

                            fieldShape |> Result.map (fun s -> (fname, wireName def fname, s) :: fs)))
                    (Ok [])
                |> Result.map (fun fs -> Yaml.SRec(def.Name, List.rev fs))
            | Some(Record _) -> err span $"{at}'{n}' is generic; the yaml boundary needs monomorphic records"
            // a tagged union dispatches on its wire discriminator
            // [D:wire-unions]; case payloads shape recursively
            | Some(Union udef) when udef.Tag.IsSome ->
                udef.Cases
                |> List.fold
                    (fun acc (c, payload) ->
                        acc
                        |> Result.bind (fun cs ->
                            if Some c = udef.OtherCase then
                                Ok cs
                            else
                                match payload with
                                | Some pty ->
                                    yamlShape span env (seen.Add n) (sub c) pty
                                    |> Result.map (fun s -> (c, caseWire udef c, Some s) :: cs)
                                | None -> Ok((c, caseWire udef c, None) :: cs)))
                    (Ok [])
                |> Result.map (fun cs ->
                    let other =
                        udef.OtherCase
                        |> Option.map (fun oc ->
                            let hasPayload = udef.Cases |> List.exists (fun (c, p) -> c = oc && p.IsSome)

                            oc, hasPayload)

                    Yaml.SUnion(n, udef.Tag.Value, List.rev cs, other))
            // a validated type reads its payload, then runs `create`
            // [D:validated-types]
            | Some(Union({ Cases = [ (_, Some pty) ] } as udef)) when udef.Validated.IsSome ->
                yamlShape span env (seen.Add n) path pty
                |> Result.map (fun inner -> Yaml.SValid(n, udef.Validated.Value, inner))
            | Some(Union _) ->
                err
                    span
                    $"{at}'{n}' is an untagged union — a union crosses the wire with [<Tag \"field\">] on its declaration"
            | None when List.contains n deflessBuiltinNominals ->
                err span $"{at}{n} is an opaque value with no YAML form — encode the fields you need into a record"
            | None -> err span $"{at}{unknownTypeMsg env n}"
    | TVar v when v.StartsWith "__hole" ->
        // cascade suppression [PLAN-diagnostics-arc B6]: the hole means
        // an earlier error was already reported — never print its name
        Ok Yaml.SStr
    | ty ->
        err
            span
            $"{at}type {formatTy ty} cannot cross the yaml boundary (scalars, records, seqs, seq<string * _>, Option)"

// the to-side: the same law, plus `Yaml` nodes render directly
let rec private yamlableOut
    (span: Span)
    (env: TypeEnv)
    (seen: Set<string>)
    (path: string)
    (ty: Ty)
    : Result<unit, TypeError> =
    // path threads the field name so a function (or any un-yamlable type)
    // nested in a record/union is named, matching the JSON law
    // [D:function-types] — the two boundaries admit different sets, but
    // both point at the offending field, not just its type
    let at = if path = "" then "" else $"field '{path}': "

    match ty with
    | WireCanonical _ -> Ok()
    | WireRefused teach -> err span (at + teach "yaml")
    | TNamed("Yaml", []) -> Ok()
    | TNamed("Option", [ TNamed("Option", _) ]) ->
        err span $"{at}Option<Option<…>> has no yaml rendering; flatten the type"
    | TNamed("Option", [ inner ]) -> yamlableOut span env seen path inner
    | TSeq(TTuple [ TStr; v ]) -> yamlableOut span env seen path v
    | TSeq elem -> yamlableOut span env seen path elem
    | TNamed(n, []) ->
        if seen.Contains n then
            err span $"{at}'{n}' is recursive; the yaml boundary needs finite trees"
        else
            match typeDefFor env n with
            | Some(Record def) when def.Params.IsEmpty ->
                def.Fields
                |> List.fold
                    (fun acc (fn, fty) ->
                        acc
                        |> Result.bind (fun () ->
                            let fpath = if path = "" then fn else $"{path}.{fn}"

                            match fieldCodec def fn, uncodedFieldTeach "yaml" fty with
                            | Some _, _ -> Ok()
                            | None, Some teach -> err span $"field '{fpath}': {teach}"
                            | None, None -> yamlableOut span env (seen.Add n) fpath fty))
                    (Ok())
            | Some(Record _) -> err span $"{at}'{n}' is generic; the yaml boundary needs monomorphic records"
            // a tagged union renders [D:wire-unions] — every payload must
            | Some(Union udef) when udef.Tag.IsSome ->
                udef.Cases
                |> List.fold
                    (fun acc (c, payload) ->
                        acc
                        |> Result.bind (fun () ->
                            match payload with
                            | Some pty when Some c <> udef.OtherCase ->
                                yamlableOut span env (seen.Add n) (if path = "" then c else $"{path}.{c}") pty
                            | _ -> Ok()))
                    (Ok())
            // a validated type writes its payload [D:validated-types]
            | Some(Union({ Cases = [ (_, Some pty) ] } as udef)) when udef.Validated.IsSome ->
                yamlableOut span env (seen.Add n) path pty
            | Some(Union _) ->
                err
                    span
                    $"{at}'{n}' is an untagged union — a union crosses the wire with [<Tag \"field\">] on its declaration"
            | None when List.contains n deflessBuiltinNominals ->
                err span $"{at}{n} is an opaque value with no YAML form — encode the fields you need into a record"
            | None -> err span $"{at}unknown type '{n}'"
    // cascade suppression [PLAN-diagnostics-arc B6], through a generic
    // argument or a tuple too
    | ty when tyVars ty |> Set.exists (fun v -> v.StartsWith "__hole") -> Ok()
    | TVar _ -> err span (at + genericWireTeach "yaml" ty)
    | TNamed(_, _ :: _) when not (Set.isEmpty (tyVars ty)) -> err span (at + genericWireTeach "yaml" ty)
    | ty ->
        err
            span
            $"{at}type {formatTy ty} cannot cross the yaml boundary (scalars, records, seqs, seq<string * _>, Option, Yaml)"

/// an `exit` argument's literal value: `n`, or `-n` (prefix minus
/// desugars to `0 - n`)
let private exitLiteral (e: Expr) : int64 option =
    match e.Kind with
    | EInt n -> Some n
    | EBinOp("-", { Kind = EInt 0L }, { Kind = EInt n }) -> Some -n
    | _ -> None

// One Regex instance per distinct literal, shared by check and eval
// (same shape as the snippet-hash-cache). Interpreted mode only —
// RegexOptions.Compiled is Reflection.Emit, banned by the AOT rule
// [D:regex-pattern].
let private regexCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex>()

// every match carries a timeout [D:regex-timeout]: catastrophic
// backtracking (`^(a+)+$` on a long near-miss) hung the script, and a
// `within serve` handler matching request input made it a DoS.
// NonBacktracking would forbid lookbehind, which weir admits. 5s per
// match call is far beyond any sane match on real input (a linear scan
// covers hundreds of MB in that time) and short enough to fail loudly.
let regexTimeout = System.TimeSpan.FromSeconds 5.0

// past this many chars a timeout may be the input's size, not
// backtracking [D:regex-timeout-size]: a 10-way alternation scans ~16M
// chars/s here, so 5s covers ~80M; 1M leaves an 80x margin for heavier
// linear patterns, and below it only backtracking can spend 5s
[<Literal>]
let regexLargeInput = 1048576

/// run a match of `pat` over `input`, turning the engine's timeout into
/// weir's named error [D:regex-timeout]
let regexGuarded (pat: string) (input: string) (f: unit -> 'a) : 'a =
    try
        f ()
    with :? System.Text.RegularExpressions.RegexMatchTimeoutException ->
        let ran =
            $"regex timeout: matching @\"{Types.excerpt pat}\" ran past {int regexTimeout.TotalSeconds}s"

        if input.Length >= regexLargeInput then
            failwith
                $"{ran} on a {input.Length}-char input — at this size the scan alone can take that long; match line by line instead of one joined string (`File.read p |> Seq.where (Str.isMatch pat)`), or, if the pattern nests repetition like (a+)+ or (a|a)*, rewrite it so each character can match only one way"
        else
            failwith
                $"{ran} — the pattern backtracks catastrophically on this input (nested repetition like (a+)+ or (a|a)*); rewrite it so each character can match only one way"

let private namedGroupMsg (opener: string) =
    $"named group '{opener}' — weir names captures at the binder, not in the pattern: use a plain (…) group and name it in the binder list"

// the first unescaped `(?P<name>` opener, if any
let private pythonNamedGroup (pat: string) : string option =
    let m =
        System.Text.RegularExpressions.Regex.Match(pat, @"(?<!\\)(?:\\\\)*\(\?P<([A-Za-z_][A-Za-z0-9_]*)>")

    if m.Success then Some $"(?P<{m.Groups[1].Value}>" else None

let compileRegex (pat: string) : Result<System.Text.RegularExpressions.Regex, string> =
    match regexCache.TryGetValue pat with
    | true, rx -> Ok rx
    | _ ->
        try
            let rx =
                System.Text.RegularExpressions.Regex(
                    pat,
                    System.Text.RegularExpressions.RegexOptions.None,
                    regexTimeout
                )

            // the user's pattern validated above; the engine runs its
            // code-point form [D:regex-code-points]
            let rx =
                try
                    System.Text.RegularExpressions.Regex(
                        Types.regexCodePoints pat,
                        System.Text.RegularExpressions.RegexOptions.None,
                        regexTimeout
                    )
                with _ ->
                    rx

            // named groups are rejected [D:no-named-groups]: .NET
            // numbers positional groups first and named ones after —
            // the outlier among engines, so binders would follow
            // neither the pattern's reading order nor the user's
            // Perl/Python experience. Weir names captures at the
            // binder instead. Detection uses the engine's own group
            // table — lookbehind (?<= (?<! and non-capturing (?:
            // create no groups, so they cannot false-positive here
            // (the one place a hand-rolled scan could go wrong).
            let named =
                rx.GetGroupNames()
                |> Array.tryFind (fun n -> not (n |> Seq.forall System.Char.IsDigit))

            match named with
            | Some n -> Error(namedGroupMsg $"(?<{n}>")
            | None ->
                regexCache[pat] <- rx
                Ok rx
        with ex ->
            // Python's `(?P<name>…)` is a named group too
            // [D:no-named-groups]: .NET cannot parse it, so its raw
            // "unrecognized grouping construct" would hide the teaching
            match pythonNamedGroup pat with
            | Some opener -> Error(namedGroupMsg opener)
            | None -> Error ex.Message

let casingError (span: Span) (name: string) : Result<'a, TypeError> =
    err
        span
        ($"binding names start lowercase; uppercase names are types, modules, and constructors"
         + $" — bind '{name.ToLowerInvariant()}' (a record field keeps its name: let level = cfg.LOG_LEVEL)")

// builtins without a qualified spelling are reserved as binder names
// [D:reserve-builtins]: shadowing one removes the capability for the
// whole file with no way back — unlike a PATH command (^x forces) or a
// bare alias (Seq.max survives `let max = …`). Populated by Builtins
// at construction: the derived no-home set, never a hand copy.
let reservedBinderNames: Set<string> ref = ref Set.empty

// the known builtin name that is both a module and a builtin value
// [D:repl-infer]: `Json` names HttpBody's `Json` constructor and the
// Json module (Json.inferShape). A constructor value has no record
// fields, so `Json.<member>` can only mean the module — but a user
// union case shadowing a module name must still error ordinarily
// [D:desugar-capture], so the module preference is scoped to this
// fixed builtin set, never a general value/module tie-break.
let moduleValueOverlap: Set<string> = Set [ "Json" ]

let rec private isIrrefutablePat (p: Pattern) =
    match p.PKind with
    | PWildcard
    | PVar _
    | PUnit -> true
    | PTuple ps -> ps |> List.forall isIrrefutablePat
    // judged by children, like tuples [D:refutable-record-patterns]:
    // one refutable field pattern makes the whole record pattern
    // refutable, which is what keeps it out of binder positions and
    // out of completing a match
    | PRecord fields -> fields |> List.forall (snd >> isIrrefutablePat)
    | PBool _
    | PInt _
    | PStr _
    | PRegex _
    | PSeqNil
    | PCons _
    | PSeqList _
    | PCase _ -> false

let private recordPatDups (span: Span) (fields: ((string * Span) * Pattern) list) : Result<unit, TypeError> =
    if List.isEmpty fields then
        err span "a record pattern must name at least one field — { } binds nothing"
    else

        let dup =
            fields
            |> List.map (fst >> fst)
            |> List.countBy id
            |> List.tryFind (fun (_, n) -> n > 1)

        match dup with
        | Some(f, _) ->
            let span = fields |> List.find (fun ((n, _), _) -> n = f) |> fst |> snd
            err span $"duplicate field '{f}' in a record pattern"
        | None -> Ok()

let checkBinderName (span: Span) (name: string) : Result<unit, TypeError> =
    if name.Length > 0 && System.Char.IsUpper name[0] then
        casingError span name
    elif (reservedBinderNames.Value).Contains name then
        err
            span
            $"'{name}' is a builtin with no qualified spelling — a binding would shadow it for the whole file with no way back; pick another name"
    else
        Ok()

// every imported type's key in this env — own and transitive alike
// [D:transitive-types] [D:qualified-type-names]
let private isImportedType (env: TypeEnv) (key: string) : bool =
    Map.containsKey key env.TypeOrigins
    || env.ModuleTypes
       |> Map.exists (fun _ owned -> owned |> Map.exists (fun _ k -> k = key))

let private fieldSetKey (names: Set<string>) : string =
    let sb = System.Text.StringBuilder()

    for n in names do
        sb.Append(n.Length).Append(':').Append(n) |> ignore

    sb.ToString()

type private TypesIndex
    (
        caseOwners: System.Collections.Generic.Dictionary<string, string list>,
        recordsByFields: Lazy<System.Collections.Generic.Dictionary<string, (string * RecordDef) list>>
    ) =
    member _.CaseOwners(name: string) : string list =
        match caseOwners.TryGetValue name with
        | true, owners -> owners
        | _ -> []

    member _.RecordsWithFields(names: Set<string>) : (string * RecordDef) list =
        match recordsByFields.Value.TryGetValue(fieldSetKey names) with
        | true, recs -> recs
        | _ -> []

let private typesIndexes =
    System.Runtime.CompilerServices.ConditionalWeakTable<Map<string, TypeDef>, TypesIndex>()

let private recordFieldKeys =
    System.Runtime.CompilerServices.ConditionalWeakTable<RecordDef, string>()

let private recordFieldKey =
    System.Runtime.CompilerServices.ConditionalWeakTable<RecordDef, string>.CreateValueCallback(fun r ->
        fieldSetKey (Set.ofList (List.map fst r.Fields)))

let private buildTypesIndex (types: Map<string, TypeDef>) : TypesIndex =
    let caseOwners = System.Collections.Generic.Dictionary<string, string list>()

    Map.foldBack
        (fun tn def () ->
            match def with
            | Union u ->
                for c, _ in u.Cases do
                    match caseOwners.TryGetValue c with
                    | true, (owner :: _) when owner = tn -> ()
                    | true, owners -> caseOwners[c] <- tn :: owners
                    | _ -> caseOwners[c] <- [ tn ]
            | Record _ -> ())
        types
        ()

    let recordsByFields =
        lazy
            (let byFields =
                System.Collections.Generic.Dictionary<string, (string * RecordDef) list>()

             Map.foldBack
                 (fun tn def () ->
                     match def with
                     | Record r when isUserName tn && not (isReceipt tn) ->
                         let fields = recordFieldKeys.GetValue(r, recordFieldKey)

                         match byFields.TryGetValue fields with
                         | true, recs -> byFields[fields] <- (tn, r) :: recs
                         | _ -> byFields[fields] <- [ tn, r ]
                     | _ -> ())
                 types
                 ()

             byFields)

    TypesIndex(caseOwners, recordsByFields)

let private typesIndexBuilder =
    System.Runtime.CompilerServices.ConditionalWeakTable<Map<string, TypeDef>, TypesIndex>.CreateValueCallback
        buildTypesIndex

let private typesIndex (env: TypeEnv) : TypesIndex =
    typesIndexes.GetValue(env.Types, typesIndexBuilder)

// an unbound name that is a field of a declared (or imported) record:
// the repair it most likely needs, and whether it is confident (a value
// of the record is bound — `args.mode`). The prelude's records are not
// the script's vocabulary, so they never answer.
let fieldHint (env: TypeEnv) (name: string) : (string * bool) option =
    let owners =
        env.Types
        |> Map.toList
        |> List.choose (fun (n, d) ->
            match d with
            | Record rd when
                isUserName n
                && not (builtinTypeNames.ContainsKey n)
                && rd.Params.IsEmpty
                && rd.Fields |> List.exists (fun (f, _) -> f = name)
                ->
                Some n
            | _ -> None)

    let boundOf (owner: string) =
        env.Values
        |> Map.toList
        |> List.tryPick (fun (v, sch) ->
            match sch.Ty with
            | TNamed(n, []) when n = owner && isUserName v && not (v.Contains '.') -> Some v
            | _ -> None)

    match owners |> List.tryPick (fun o -> boundOf o |> Option.map (fun v -> o, v)) with
    | Some(owner, v) -> Some($"did you mean '{v}.{name}'? ('{name}' is a field of '{owner}')", true)
    | None ->
        match owners |> List.tryFind (fun o -> Map.containsKey o env.LoadTargets) with
        | Some owner ->
            let loader = env.LoadTargets[owner]
            let binder = if loader = "Env.load" then "env" else "cli"

            Some(
                $"it is a field of '{owner}': load it first ('let {binder} = {loader} {owner}') and write '{binder}.{name}'",
                false
            )
        | None ->
            match owners with
            | owner :: _ -> Some($"it is a field of '{owner}' — write 'x.{name}' on a '{owner}' value", false)
            | [] -> None

/// constructor is already overwritten; ownership must be asked of the
/// type table, not the value table.
let rec ctorOwners (env: TypeEnv) (name: string) : string list = preferLocal (ctorOwnersAll env name)

// a script's own case shadows a prelude case of the same name
// [D:try-form] — the import rule: a local declaration takes
// precedence (`type Status = Ok | Error` predates Result)
and private preferLocal (owners: string list) : string list =
    match owners |> List.filter (fun o -> not (builtinTypeNames.ContainsKey o)) with
    | [] -> owners
    | local -> local

// every in-scope union declaring the case, local and prelude alike
and ctorOwnersAll (env: TypeEnv) (name: string) : string list =
    // uppercase only: the casing law makes every constructor uppercase, so
    // this skips the scan for ordinary bindings
    if name.Length = 0 || not (System.Char.IsUpper name[0]) then
        []
    else
        // imported types are excluded: they live in `Types` so signatures
        // and field access resolve, but their cases are not in scope bare
        // (access is always qualified), so they are not candidates for this
        // name and must not make a local declaration look ambiguous — a type
        // reached only transitively [D:transitive-types] included
        let owners =
            (typesIndex env).CaseOwners name
            |> List.filter (fun tn -> not (isImportedType env tn))

        owners

// the imported unions declaring a case — the owners ctorOwnersAll
// leaves out (their cases are not in scope bare), consulted only where
// a pattern types its scrutinee [D:match-scrutinee-infer]
let private importedCtorOwners (env: TypeEnv) (name: string) : string list =
    (typesIndex env).CaseOwners name |> List.filter (isImportedType env)

// the unions a bare case pattern can name with nothing typing the value:
// the in-scope owners, else — their cases being not in scope bare — the
// imported unions declaring it, as construction reaches them qualified
// [D:match-scrutinee-infer] [D:qualified-case-patterns]
let private caseOwners (env: TypeEnv) (ctor: string) : string list =
    match ctorOwners env ctor with
    | [] -> importedCtorOwners env ctor
    | local -> local

// the qualifier a module-qualified name needs is an import's alias
// [D:module-qualifier]; a type name used as one says it is a type
let private noModuleMsg (env: TypeEnv) (m: string) : string =
    if Map.containsKey m env.Types then
        $"'{m}' is a type, not a value — read a field from a value of it, like `x.field`"
    else
        let hint = didYouMean m (Map.keys env.Modules)
        $"no module named '{m}'{hint} — a module comes from `import \"./file.weir\" as {m}` (or `weir:name` for a vendored one)"

// a type's written spelling: `A.T` for an imported type, so a message
// naming two same-named unions tells them apart [D:qualified-case-patterns]
let private typeSpelling (env: TypeEnv) (ty: Ty) : string =
    match ty with
    | TNamed(key, _) ->
        env.ModuleTypes
        |> Map.toList
        |> List.tryPick (fun (alias, owned) ->
            owned
            |> Map.tryPick (fun name k -> if k = key then Some(alias + "." + name) else None))
        |> Option.defaultValue (formatTy ty)
    | ty -> formatTy ty

// the unions import `m` declares with case `ctor` — a qualified case
// pattern's candidates [D:qualified-case-patterns]; Error is the message
let private qualifiedCaseOwners (env: TypeEnv) (m: string) (ctor: string) : Result<string list, string> =
    match Map.tryFind m env.ModuleTypes with
    | Some owned ->
        let unions =
            owned
            |> Map.values
            |> Seq.distinct
            |> Seq.choose (fun key ->
                match typeDefFor env key with
                | Some(Union u) -> Some(key, u)
                | _ -> None)
            |> List.ofSeq

        match
            unions
            |> List.filter (fun (_, u) -> u.Cases |> List.exists (fun (c, _) -> c = ctor))
        with
        | [] ->
            let cases = unions |> List.collect (fun (_, u) -> List.map fst u.Cases)
            Error $"module '{m}' declares no case '{ctor}'{didYouMean ctor cases}"
        | hits -> Ok(hits |> List.map fst |> List.sort)
    | None when Map.containsKey m env.Modules -> Error $"module '{m}' declares no case '{ctor}'"
    | None when Map.containsKey m env.Types ->
        Error
            $"'{m}' is a type, not a module — a case is qualified by its import's name; a case of a union declared here is written bare: '{ctor}'"
    | None -> Error(noModuleMsg env m)

// a qualified case whose union is not the value's type
let private wrongQualifierMsg (env: TypeEnv) (m: string) (ctor: string) (owners: string list) (ty: Ty) : string =
    let named =
        owners
        |> List.map (fun o -> typeSpelling env (TNamed(o, [])))
        |> String.concat "/"

    $"'{m}.{ctor}' is a case of {named}, but this value has type {typeSpelling env ty}"

// a bare case two imports declare, with nothing typing the value: the
// qualified spellings settle it [D:qualified-case-patterns]
let private ambiguousCaseMsg (env: TypeEnv) (ctor: string) (owners: string list) : string =
    let spellings =
        env.ModuleTypes
        |> Map.toList
        |> List.choose (fun (alias, owned) ->
            if owned |> Map.exists (fun _ key -> List.contains key owners) then
                Some(alias + "." + ctor)
            else
                None)
        |> List.sort

    $"case '{ctor}' is ambiguous — more than one import declares it; write the qualified case: {quotedOrList spellings}"

let rec private checkPattern (ctx: Ctx) (env: TypeEnv) (ty: Ty) (p: Pattern) : Result<(string * Ty) list, TypeError> =
    // a pattern whose shape determines a type binds an unresolved
    // scrutinee instead of demanding one [D:record-pattern-rows] — the
    // arms unify against the row a record pattern emits, so a literal
    // under a field works exactly as it does under a known record.
    // Constructor patterns are absent by design: `Some` names a case
    // from a closed set, which is the one requirement that needs the
    // nominal type first.
    let determined =
        match p.PKind with
        | PBool _ -> Some TBool
        | PInt _ -> Some TInt
        | PStr _
        | PRegex _ -> Some TStr
        | PUnit -> Some TUnit
        | PSeqNil
        | PCons _
        | PSeqList _ -> Some(TSeq(TVar(freshName ctx "a")))
        // a tuple pattern fixes the arity, its elements stay open
        | PTuple ps -> Some(TTuple(ps |> List.map (fun _ -> TVar(freshName ctx "a"))))
        | _ -> None

    let pre =
        match resolve ctx ty, determined with
        | TVar _ as v, Some t -> bind ctx env p.PSpan t v
        | _ -> Ok()

    match pre with
    | Error e -> Error e
    | Ok() ->

        // the arms read the resolved type: the prebind above lands in
        // ctx.Subst, so the raw parameter would still read as a bare var
        let ty = resolve ctx ty

        match p.PKind with
        | PWildcard -> Ok []
        | PVar name -> checkBinderName p.PSpan name |> Result.map (fun () -> [ name, ty ])
        | PBool _ ->
            match ty with
            | TBool -> Ok []
            | ty -> err p.PSpan $"bool patterns need a bool value; this one has type {formatTy ty}"
        | PInt _ ->
            match ty with
            | TInt -> Ok []
            | ty -> err p.PSpan $"int literal patterns need an int value; this one has type {formatTy ty}"
        | PStr _ ->
            match ty with
            | TStr -> Ok []
            | ty -> err p.PSpan $"string literal patterns need a string value; this one has type {formatTy ty}"
        | PSeqNil ->
            (match ty with
             | TSeq _ -> Ok []
             | ty -> err p.PSpan $"seq patterns need a seq value; this one has type {formatTy ty}")
        | PCons(h, t) ->
            (match ty with
             | TSeq elem ->
                 result {
                     let! hb = checkPattern ctx env elem h
                     let! tb = checkPattern ctx env ty t
                     return hb @ tb
                 }
             | ty -> err p.PSpan $"seq patterns need a seq value; this one has type {formatTy ty}")
        | PSeqList ps ->
            (match ty with
             | TSeq elem ->
                 ps
                 |> List.fold
                     (fun acc sub ->
                         acc
                         |> Result.bind (fun bs -> checkPattern ctx env elem sub |> Result.map (fun b -> bs @ b)))
                     (Ok [])
             | ty -> err p.PSpan $"seq patterns need a seq value; this one has type {formatTy ty}")
        | PRegex(pat, litSpan, raw, binder) ->
            // check-time compilation [D:regex-pattern]: an invalid literal
            // is a check error, and the binder's arity must equal the
            // engine's own capture count (non-capturing groups excluded by
            // the engine's numbering) — the F# ParseRegex silent-non-match
            // hole, closed statically
            match ty with
            | TStr when not raw ->
                // the raw-only rider [D:raw-strings]: the double-escape
                // footgun is unrepresentable at the one checked-regex
                // position; the kind is rejected, casing-law-style
                err litSpan "regex literals are raw: use @\"...\" (or \"\"\"...\"\"\" for patterns containing quotes)"
            | TStr ->
                match compileRegex pat with
                | Error msg -> err litSpan $"invalid regex: {msg}"
                | Ok rx ->
                    let arity = rx.GetGroupNumbers().Length - 1

                    let isLeaf (sp: Pattern) =
                        match sp.PKind with
                        | PVar _
                        | PWildcard -> true
                        | _ -> false

                    let leaves =
                        match arity, binder.PKind with
                        | 0, PUnit -> Ok []
                        | 1, (PVar _ | PWildcard) -> Ok [ binder ]
                        | n, PTuple ps when List.length ps = n && List.forall isLeaf ps -> Ok ps
                        | n, _ ->
                            let expected =
                                match n with
                                | 0 -> "'()'"
                                | 1 -> "one lowercase name (or _)"
                                | n -> $"a tuple of {n} names"

                            err binder.PSpan $"this regex has {n} capture group(s); the binder must be {expected}"

                    leaves
                    |> Result.bind (fun ls ->
                        let names =
                            ls
                            |> List.choose (fun sp ->
                                match sp.PKind with
                                | PVar n -> Some n
                                | _ -> None)

                        match firstDup names with
                        | Some d -> err binder.PSpan $"duplicate binder '{d}'"
                        | None -> Ok(names |> List.map (fun n -> n, TStr)))
            | ty -> err p.PSpan $"Regex patterns need a string value; this one has type {formatTy ty}"
        | PUnit ->
            match ty with
            | TUnit -> Ok []
            | ty -> err p.PSpan $"'()' patterns need a unit value; this one has type {formatTy ty}"
        | PRecord fields ->
            // record patterns [D:record-patterns]: partial field
            // mention is the point, and children may be refutable
            // [D:refutable-record-patterns] — coverage needs no
            // field-value analysis because a refutable child makes the
            // whole pattern refutable, and a refutable pattern never
            // completes a match (the literal rule)
            result {
                do! recordPatDups p.PSpan fields

                match ty with
                | TNamed(typeName, targs) ->
                    match typeDefFor env typeName with
                    | Some(Record def) ->
                        return!
                            fields
                            |> List.fold
                                (fun acc ((f, fspan), sub) ->
                                    acc
                                    |> Result.bind (fun bs ->
                                        match def.Fields |> List.tryFind (fun (fd, _) -> fd = f) with
                                        | Some(_, fieldTy) ->
                                            checkPattern ctx env (substParams def.Params targs fieldTy) sub
                                            |> Result.map (fun b -> bs @ b)
                                        | None ->
                                            match retiredField typeName f with
                                            | Some teach -> err fspan $"'{f}' is retired: {teach}"
                                            | None ->
                                                let hint = didYouMean f (List.map fst def.Fields)
                                                err fspan $"{typeName} has no field '{f}'{hint}"))
                                (Ok [])
                    | Some(Union _) -> return! err p.PSpan $"{typeName} is a union; match on its cases, not fields"
                    | None -> return! err p.PSpan $"unknown type '{typeName}'"
                | ty ->
                    // an unresolved scrutinee gets the row the binder position
                    // already emits [D:record-pattern-rows]: a field pattern
                    // names a field, so the requirement is a row carrying it —
                    // nominal identity is the constructor pattern's need (a
                    // closed case set to validate and cover against), and that
                    // law does not transfer. Mirrors EField's TVar/TRowVar arms,
                    // so unification and [D:row-provenance] are inherited.
                    let r =
                        match ty with
                        | TRowVar(r, _) -> r
                        | TVar v ->
                            let r = freshName ctx "r"
                            ctx.Subst[v] <- (TRowVar(r, []))
                            r
                        | _ -> ""

                    if r = "" then
                        return! err p.PSpan $"record patterns need a record value; this one has type {formatTy ty}"
                    else
                        let existing = rowsOf ctx r

                        // each mentioned field joins the row (an arm mentioning
                        // a new field accumulates, exactly as a body reading
                        // p.name and p.age does), keyed by the field-name span
                        let fieldTys =
                            fields
                            |> List.map (fun ((f, fspan), _) ->
                                match Map.tryFind f existing with
                                | Some(t, _) -> f, (t, fspan)
                                | None -> f, (TVar(freshName ctx "a"), fspan))

                        ctx.Rows[r] <- (fieldTys |> List.fold (fun m (f, e) -> Map.add f e m) existing)

                        return!
                            List.zip fields fieldTys
                            |> List.fold
                                (fun acc ((_, sub), (_, (ft, _))) ->
                                    acc
                                    |> Result.bind (fun bs ->
                                        checkPattern ctx env ft sub |> Result.map (fun b -> bs @ b)))
                                (Ok [])
            }
        | PTuple ps ->
            match ty with
            | TTuple ts when List.length ps = List.length ts ->
                List.zip ps ts
                |> List.fold
                    (fun acc (subP, subTy) ->
                        acc
                        |> Result.bind (fun bs -> checkPattern ctx env subTy subP |> Result.map (fun b -> bs @ b)))
                    (Ok [])
            | TTuple ts ->
                err p.PSpan $"this tuple pattern has {List.length ps} elements; the value has {List.length ts}"
            | ty ->
                // the bare-comma precedence footgun [D:user-language-messages]:
                // `code, _ :: rest` parses as a tuple whose last element is a
                // cons (`,` groups looser than `::`), so a seq value lands here,
                // not a tuple. The repair is the grouping, so name it — but only
                // on the unambiguous shape (a trailing cons against a seq); any
                // other tuple-vs-non-tuple keeps the plain message.
                let hint =
                    match ty, List.tryLast ps with
                    | TSeq _, Some { PKind = PCons(h, t) } ->
                        let front = ps |> List.take (List.length ps - 1)

                        let loose =
                            (front |> List.map sexprPat |> String.concat ", ")
                            + ", "
                            + sexprPat h
                            + " :: "
                            + sexprPat t

                        let grouped = (front @ [ h ]) |> List.map sexprPat |> String.concat ", "
                        $" — `,` groups looser than `::`, so `{loose}` is a tuple; did you mean `({grouped}) :: {sexprPat t}`?"
                    | _ -> ""

                err p.PSpan $"tuple patterns need a tuple value; this one has type {formatTy ty}{hint}"
        | PCase(ctor, argPat, qual) ->
            // a qualifier names the import whose union declares the case
            // [D:qualified-case-patterns]: it admits that import's owners
            let admitted =
                match qual with
                | Some(m, mspan) ->
                    match qualifiedCaseOwners env m ctor with
                    | Ok os -> Ok(Some os)
                    | Error msg -> err mspan msg
                | None -> Ok None

            match admitted, ty with
            | Error e, _ -> Error e
            | Ok(Some os), TNamed(typeName, _) when not (List.contains typeName os) ->
                err p.PSpan (wrongQualifierMsg env (fst qual.Value) ctor os ty)
            | Ok _, TNamed(typeName, targs) ->
                match typeDefFor env typeName with
                | Some(Union def) ->
                    match def.Cases |> List.tryFind (fun (c, _) -> c = ctor) with
                    | None ->
                        let hint = didYouMean ctor (List.map fst def.Cases)
                        err p.PSpan $"{typeName} has no case '{ctor}'{hint}"
                    | Some(_, None) ->
                        match argPat with
                        | None -> Ok []
                        | Some ap -> err ap.PSpan $"'{ctor}' has no payload"
                    | Some(_, Some payloadTy) ->
                        let payloadTy = substParams def.Params targs payloadTy

                        match argPat with
                        | Some ap -> checkPattern ctx env payloadTy ap
                        | None -> err p.PSpan $"'{ctor}' carries {formatTy payloadTy}; add a pattern for it"
                | Some(Record _) ->
                    // records have a pattern form [D:record-patterns],
                    // so the repair names it
                    err
                        p.PSpan
                        $"{typeName} is a record; match it with a name, '_', or a record pattern ({{ field = binder }})"
                | None -> err p.PSpan $"unknown type '{typeName}'"
            // an unresolved scrutinee types from the constructor when it
            // names a SINGLE owner union [D:match-scrutinee-infer] — the
            // one-way bind the scalar defaults do, for PCase; this reaches
            // nested payloads too (`Some (Ok n)` resolves Option's param to
            // the owner of Ok). A mixed/ambiguous owner stays unresolved and
            // falls to the teaching: params are not typed from patterns.
            | Ok admitted, TVar v ->
                let teach () =
                    err
                        p.PSpan
                        ("constructor patterns need a scrutinee whose type is already known — params are not typed from patterns. "
                         + "Two repairs: inline the lambda at its use site (a typed pipe position types the binder there), or match on already-typed data")

                match admitted |> Option.defaultWith (fun () -> caseOwners env ctor) with
                | [ owner ] ->
                    match typeDefFor env owner with
                    | Some(Union def) ->
                        let targs = def.Params |> List.map (fun _ -> TVar(freshName ctx "a"))

                        bind ctx env p.PSpan (TNamed(owner, targs)) (TVar v)
                        |> Result.bind (fun () -> checkPattern ctx env (resolve ctx (TVar v)) p)
                    | _ -> teach ()
                | _ :: _ :: _ as owners when admitted.IsNone && (ctorOwners env ctor).IsEmpty ->
                    err p.PSpan (ambiguousCaseMsg env ctor owners)
                | _ -> teach ()
            | Ok _, ty -> err p.PSpan $"constructor patterns need a union value; this one has type {formatTy ty}"

// A binder pattern's shape: fresh vars at the leaves, TUnit at (),
// tuples composed — bound against the RHS type, so components resolve
// by unification. Refutable kinds (literals, constructors) are a
// located check error.
// The casing law [D:lowercase-binds] applies at every binder
// position; fields and match patterns are deliberately untouched.

// `hint` is the value's type where the binder site already knows it (a
// pushed lambda domain, a `for` element, a let's value when a case needs
// it): a case resolves against it first, as a match arm resolves against
// its scrutinee [D:qualified-case-patterns]
let rec private binderShapeAt
    (ctx: Ctx)
    (env: TypeEnv)
    (hint: Ty option)
    (p: Pattern)
    : Result<Ty * (string * Ty) list, TypeError> =
    let hint = hint |> Option.map (resolve ctx)

    match p.PKind with
    | PVar n ->
        checkBinderName p.PSpan n
        |> Result.map (fun () ->
            let t = TVar(freshName ctx "a")
            t, [ n, t ])
    | PWildcard -> Ok(TVar(freshName ctx "a"), [])
    | PUnit -> Ok(TUnit, [])
    | PTuple ps ->
        let hints =
            match hint with
            | Some(TTuple ts) when List.length ts = List.length ps -> List.map Some ts
            | _ -> List.map (fun _ -> None) ps

        List.zip ps hints
        |> List.fold
            (fun acc (sub, h) ->
                acc
                |> Result.bind (fun (ts, bs) ->
                    binderShapeAt ctx env h sub |> Result.map (fun (t, b) -> t :: ts, bs @ b)))
            (Ok([], []))
        |> Result.map (fun (ts, bs) -> TTuple(List.rev ts), bs)
    | PRecord fields ->
        // the row seat [D:record-patterns]: the pattern's fields become
        // row constraints keyed by the field-name span — the exact map a
        // field access writes (EField's TVar arm), so unification,
        // generalization, and [D:row-provenance] are all inherited; a
        // param destructuring { Names = n } generalizes to any record
        // carrying the field, like `fun c -> c.Names` does
        result {
            do! recordPatDups p.PSpan fields

            let! shaped =
                fields
                |> List.fold
                    (fun acc ((f, fspan), sub) ->
                        acc
                        |> Result.bind (fun rows ->
                            binderShape ctx env sub |> Result.map (fun (t, b) -> ((f, fspan, t), b) :: rows)))
                    (Ok [])
                |> Result.map List.rev

            let r = freshName ctx "r"

            ctx.Rows[r] <- (shaped |> List.map (fun ((f, fspan, t), _) -> f, (t, fspan)) |> Map.ofList)

            return TRowVar(r, []), shaped |> List.collect snd
        }
    | PCase(ctor, argPat, qual) ->
        // resolved exactly as a match arm resolves it: the qualifier's
        // import, else the value's type, else the owners in scope and then
        // the imported unions [D:qualified-case-patterns]
        let hinted =
            match hint with
            | Some(TNamed(k, targs)) ->
                match typeDefFor env k with
                | Some(Union u) when u.Cases |> List.exists (fun (c, _) -> c = ctor) -> Some(k, targs)
                | _ -> None
            | _ -> None

        let owners =
            match qual with
            | Some(m, mspan) ->
                match qualifiedCaseOwners env m ctor, hint with
                | Error msg, _ -> err mspan msg
                | Ok os, Some(TNamed(k, _) as ty) when not (List.contains k os) && (typeDefFor env k).IsSome ->
                    err p.PSpan (wrongQualifierMsg env m ctor os ty)
                | Ok os, _ -> Ok os
            | None ->
                match hinted with
                | Some(k, _) -> Ok [ k ]
                | None -> Ok(caseOwners env ctor)

        match owners with
        | Error e -> Error e
        // an unknown uppercase name in a binder is the casing law's
        // case (a function/name spelled uppercase), not refutability
        | Ok [] when qual.IsNone && not (Map.containsKey ctor env.Values) -> casingError p.PSpan ctor
        // a single-case union's constructor cannot fail — `let un (W x) = x`
        // destructures as F#'s does
        | Ok [ owner ] ->
            match typeDefFor env owner with
            | Some(Union u) when u.Cases.Length = 1 ->
                let targs =
                    match hinted with
                    | Some(k, ta) when k = owner -> ta
                    | _ -> u.Params |> List.map (fun _ -> TVar(freshName ctx "a"))

                let ty = TNamed(owner, targs)

                match snd u.Cases.Head, argPat with
                | None, None -> Ok(ty, [])
                | None, Some ap -> err ap.PSpan $"'{ctor}' has no payload"
                | Some payload, Some ap ->
                    let payloadTy = substParams u.Params targs payload

                    result {
                        let! t, binds = binderShapeAt ctx env (Some payloadTy) ap
                        do! bind ctx env ap.PSpan payloadTy t
                        return ty, binds
                    }
                | Some payload, None ->
                    err
                        p.PSpan
                        $"'{ctor}' carries {formatTy (substParams u.Params targs payload)}; add a pattern for it"
            | _ -> err p.PSpan "this pattern can fail; use match"
        | Ok(_ :: _ :: _ as owners) when qual.IsNone && (ctorOwners env ctor).IsEmpty ->
            err p.PSpan (ambiguousCaseMsg env ctor owners)
        | Ok _ -> err p.PSpan "this pattern can fail; use match"
    | PBool _
    | PInt _
    | PStr _
    | PRegex _
    | PSeqNil
    | PCons _
    | PSeqList _ -> err p.PSpan "this pattern can fail; use match"

and private binderShape (ctx: Ctx) (env: TypeEnv) (p: Pattern) : Result<Ty * (string * Ty) list, TypeError> =
    binderShapeAt ctx env None p

// a binder whose case only the value's type can settle — a qualified case
// (its import is checked against the value) or a bare one two imports
// declare: the let checks its value first [D:qualified-case-patterns]
let rec private binderNeedsValueType (env: TypeEnv) (p: Pattern) : bool =
    match p.PKind with
    | PCase(_, _, Some _) -> true
    | PCase(ctor, arg, None) ->
        ((ctorOwners env ctor).IsEmpty && (importedCtorOwners env ctor).Length > 1)
        || (arg |> Option.exists (binderNeedsValueType env))
    | PTuple ps -> ps |> List.exists (binderNeedsValueType env)
    | PRecord fields -> fields |> List.exists (snd >> binderNeedsValueType env)
    | _ -> false

// row provenance [D:row-provenance]: field-access positions for the
// given row vars, physical — logical spans cannot cross the statement
// boundary. A field that arrived via an instantiated scheme keeps its
// original origin over the local span. Empty when no translator is
// ambient (Complete, tests).
let private rowOriginsFor (ctx: Ctx) (vars: Set<string>) : Map<string, (string * int * int * int) list> =
    match toPhys.Value with
    | None -> Map.empty
    | Some tr ->
        vars
        |> Set.toList
        |> List.choose (fun v ->
            tryRows ctx v
            |> Option.map (fun fields ->
                let prior = Map.tryFind v ctx.RowOrigins |> Option.defaultValue []

                v,
                [
                    for KeyValue(field, (_, fspan)) in fields ->
                        match prior |> List.tryFind (fun (f, _, _, _) -> f = field) with
                        | Some o -> o
                        | None ->
                            let pl, pc = tr fspan.Start.Col
                            field, pl, pc, fspan.End.Col - fspan.Start.Col
                ]))
        |> Map.ofList

// The scheme-scooping rule, one implementation: free vars beyond the
// env generalize; their pending constraints scoop into the scheme
// (removed from ctx). Shared by both ELet arms and the binding case
// below, so a scooping fix cannot land in one copy and miss the
// others (that would be a silent generalization bug).
let private generalizeLet (ctx: Ctx) (env: TypeEnv) (valueTy: Ty) : Scheme =
    let fa = tyVars valueTy - envFreeVars ctx env
    let origins = rowOriginsFor ctx fa

    let cs =
        fa
        |> Set.toList
        |> List.choose (fun v ->
            Map.tryFind v ctx.Cons
            |> Option.map (fun ps -> v, ps |> List.map _.Cls |> Set.ofList))
        |> Map.ofList

    // scooped constraints move into the scheme
    ctx.Cons <- cs |> Map.fold (fun m v _ -> mapDrop v m) ctx.Cons

    {
        Forall = fa
        Cs = cs
        Ty = valueTy
        RowOrigins = origins
        HoleDefaults = []
    }

// per-name generalization for destructuring binders: each bound name's
// type generalizes independently against the env (constraints scooped
// per name from the shared ctx) — generalizeLet's per-name sibling
let private generalizeBinding (ctx: Ctx) (env: TypeEnv) (name: string, ty: Ty) : string * Scheme =
    name, generalizeLet ctx env (finalTy ctx ty)

// A column's constructor signature for the pattern-matrix walk: a closed
// set (bool, a union, a tuple's one constructor, a seq's []/cons) or
// Open — ints, strings, records and anything unresolved, where only an
// irrefutable pattern covers.
type private CtorSig =
    | Closed of (string * Ty list) list
    | Open

let private ctorSig (env: TypeEnv) (ty: Ty) : CtorSig =
    match ty with
    | TBool -> Closed [ "true", []; "false", [] ]
    | TTuple tys -> Closed [ "(,)", tys ]
    // the flat seq rule [D:seq-patterns]: [] and an irrefutable cons
    | TSeq _ -> Closed [ "[]", []; "_ :: _", [] ]
    | TNamed(name, targs) ->
        match typeDefFor env name with
        | Some(Union def) ->
            def.Cases
            |> List.map (fun (case, payload) ->
                case,
                (match payload with
                 | Some pt -> [ substParams def.Params targs pt ]
                 | None -> []))
            |> Closed
        | _ -> Open
    | _ -> Open

// a pattern's sub-patterns under constructor `c`, or None when the
// pattern cannot match it; an irrefutable pattern matches every
// constructor with wildcard sub-patterns
let private specializePat (c: string) (arity: int) (p: Pattern) : Pattern list option =
    let wild = { p with PKind = PWildcard }

    match p.PKind with
    | PTuple ps when c = "(,)" -> Some ps
    | _ when isIrrefutablePat p -> Some(List.replicate arity wild)
    | PBool b when c = (if b then "true" else "false") -> Some []
    | PCase(case, arg, _) when case = c ->
        match arg, arity with
        | Some a, 1 -> Some [ a ]
        | None, 0 -> Some []
        | _ -> None
    | PSeqNil when c = "[]" -> Some []
    | PCons(h, t) when c = "_ :: _" && isIrrefutablePat h && isIrrefutablePat t -> Some []
    | _ -> None

let private witnessText (c: string) (args: string list) =
    match c, args with
    | "(,)", parts -> "(" + String.concat ", " parts + ")"
    | _, [] -> c
    | _, [ a ] when a = "_" || not (a.Contains " ") || a.StartsWith "(" -> $"{c} {a}"
    | _, [ a ] -> $"{c} ({a})"
    | _, parts -> c + " (" + String.concat ", " parts + ")"

// The pattern-matrix usefulness walk (Maranget's): is a row of
// wildcards over `tys` useful against `rows`? Some witness (one rendered
// pattern per column) names a value no row covers; None = covered.
// Coverage composes per component — a tuple column expands into its
// components, a union column into its payloads.
let rec private uncoveredWitness (env: TypeEnv) (tys: Ty list) (rows: Pattern list list) : string list option =
    match tys with
    | [] -> if List.isEmpty rows then Some [] else None
    | ty :: restTys ->
        let defaultRows () =
            rows
            |> List.choose (fun r ->
                match r with
                | p :: rest when isIrrefutablePat p -> Some rest
                | _ -> None)

        match ctorSig env ty with
        | Open -> uncoveredWitness env restTys (defaultRows ()) |> Option.map (fun w -> "_" :: w)
        | Closed ctors ->
            let used c arity =
                rows
                |> List.exists (fun r ->
                    match r with
                    | p :: _ when not (isIrrefutablePat p) -> (specializePat c arity p).IsSome
                    | _ -> false)

            let missing = ctors |> List.filter (fun (c, args) -> not (used c args.Length))

            match missing with
            | (c, args) :: _ ->
                // an unused constructor: only the wildcard rows can cover it
                uncoveredWitness env restTys (defaultRows ())
                |> Option.map (fun w -> witnessText c (List.replicate args.Length "_") :: w)
            | _ ->
                ctors
                |> List.tryPick (fun (c, args) ->
                    let arity = args.Length

                    let specialized =
                        rows
                        |> List.choose (fun r ->
                            match r with
                            | p :: rest -> specializePat c arity p |> Option.map (fun sub -> sub @ rest)
                            | [] -> None)

                    uncoveredWitness env (args @ restTys) specialized
                    |> Option.map (fun w ->
                        let sub, rest = List.splitAt arity w
                        witnessText c sub :: rest))

// Exhaustiveness [D:exhaustiveness-hard-error]. Only unguarded arms
// count — a guarded arm can fail at runtime. Coverage is recursive
// through union payloads (Some (Some x) / Some None / None is
// exhaustive): a hard error must not reject genuinely-total matches.
let rec private missingCases (env: TypeEnv) (ty: Ty) (pats: Pattern list) : string list =
    if pats |> List.exists isIrrefutablePat then
        []
    else
        match ty with
        | TNamed(name, targs) ->
            match typeDefFor env name with
            | Some(Union def) ->
                def.Cases
                |> List.filter (fun (case, payload) ->
                    let uncovered =
                        match payload with
                        | None ->
                            pats
                            |> List.exists (fun p ->
                                match p.PKind with
                                | PCase(c, None, _) -> c = case
                                | _ -> false)
                            |> not
                        | Some payloadTy ->
                            let payloadPats =
                                pats
                                |> List.choose (fun p ->
                                    match p.PKind with
                                    | PCase(c, Some arg, _) when c = case -> Some arg
                                    | _ -> None)

                            payloadPats.IsEmpty
                            || not (
                                missingCases env (substParams def.Params targs payloadTy) payloadPats
                                |> List.isEmpty
                            )

                    uncovered)
                // named as the arm to write [D:structured-fixes]: a
                // payload case's pattern carries its payload
                |> List.map (fun (case, payload) -> if payload.IsSome then case + " _" else case)
            // a record scrutinee is a product like a tuple
            // [D:refutable-record-patterns]: only an irrefutable arm
            // completes it (the guard above already returned for those),
            // so anything reaching here still owes the catch-all
            | Some(Record _) -> [ "_" ]
            | _ -> []
        | TBool -> [
            if not (pats |> List.exists (fun p -> p.PKind = PBool true)) then
                "true"
            if not (pats |> List.exists (fun p -> p.PKind = PBool false)) then
                "false"
          ]
        | TInt
        | TStr ->
            // literal patterns never complete a match alone (F#'s
            // rule): a var or wildcard arm must close it
            [ "_" ]
        | TTuple _ ->
            // per-component product coverage [D:exhaustiveness-hard-error]:
            // the pattern-matrix walk names one uncovered tuple
            match uncoveredWitness env [ ty ] (pats |> List.map (fun p -> [ p ])) with
            | Some [ w ] -> [ w ]
            | Some _ -> [ "_" ]
            | None -> []
        | TSeq _ ->
            // [D:seq-patterns]: [] + irrefutable-cons complete (F#'s
            // list rule, flat v1 — chained-cons completeness wants a
            // wildcard); fixed-arity literals never complete alone
            let nilCovered = pats |> List.exists (fun p -> p.PKind = PSeqNil)

            let consCovered =
                pats
                |> List.exists (fun p ->
                    match p.PKind with
                    | PCons(h, t) -> isIrrefutablePat h && isIrrefutablePat t
                    | _ -> false)

            // a chained cons (`a :: b :: rest`) covers every length from
            // its depth up: when the shortest such chain is deeper than
            // one, the gap is the short seqs below it — name those, not
            // `_ :: _` [D:seq-patterns]
            let rec chainDepth (p: Pattern) =
                match p.PKind with
                | PCons(h, t) when isIrrefutablePat h -> chainDepth t |> Option.map ((+) 1)
                | _ when isIrrefutablePat p -> Some 0
                | _ -> None

            let fixedLens =
                pats
                |> List.choose (fun p ->
                    match p.PKind with
                    | PSeqNil -> Some 0
                    | PSeqList ps when ps |> List.forall isIrrefutablePat -> Some ps.Length
                    | _ -> None)
                |> Set.ofList

            let shortest =
                pats
                |> List.choose (fun p ->
                    match p.PKind with
                    | PCons _ -> chainDepth p
                    | _ -> None)
                |> List.sort
                |> List.tryHead

            match shortest with
            | Some k when not consCovered && k > 1 -> [
                for n in 0 .. k - 1 do
                    if not (fixedLens.Contains n) then
                        if n = 0 then
                            "[]"
                        else
                            "[" + String.concat "; " (List.replicate n "_") + "]"
              ]
            | _ -> [
                if not nilCovered then
                    "[]"
                if not consCovered then
                    "_ :: _"
              ]
        // every arm here is refutable (the guard above returned
        // otherwise); a payload the checker could not resolve (a type
        // variable) or any other unanalysed type owes the catch-all
        | _ -> [ "_" ]

let private exhaustive
    (env: TypeEnv)
    (span: Span)
    (scrutTy: Ty)
    (armsWithBody: (Pattern * 'g option * Span) list)
    : Result<unit, TypeError> =
    let arms = armsWithBody |> List.map (fun (p, g, _) -> p, g)

    let unguarded =
        arms |> List.choose (fun (p, g) -> if g.IsNone then Some p else None)

    // [D:unreachable-arm-hard-error]: an unguarded irrefutable arm
    // ends the match, so arms after it are dead; a typo'd constructor
    // becomes a variable binder under the casing law, hence the hint
    // against the scrutinee's cases
    let deadArms =
        arms
        |> List.tryFindIndex (fun (p, g) -> g.IsNone && isIrrefutablePat p)
        |> Option.filter (fun i -> i < List.length arms - 1)

    match deadArms with
    | Some i ->
        let p, _ = arms[i]
        let _, _, body = armsWithBody[i]

        // an or-pattern desugars to one arm per alternative sharing the
        // body [D:or-patterns]: alternatives after this one sit on its own
        // arm, and the arms below count once each, however many
        // alternatives they carry
        let rest = armsWithBody |> List.skip (i + 1)
        let alts = rest |> List.takeWhile (fun (_, _, b) -> b = body) |> List.length

        let later =
            rest
            |> List.skip alts
            |> List.map (fun (_, _, b) -> b)
            |> List.distinct
            |> List.length

        let altPart =
            if alts = 1 then
                "the or-pattern's alternative after it"
            else
                $"the or-pattern's {alts} alternatives after it"

        let armPart =
            if later = 1 then
                "the arm below"
            else
                $"the {later} arms below"

        let tail =
            match alts, later with
            | 0, 1 -> "the arm below is unreachable"
            | 0, _ -> $"{armPart} are unreachable"
            | 1, 0 -> $"{altPart} is unreachable"
            | _, 0 -> $"{altPart} are unreachable"
            | _ -> $"{altPart} and {armPart} are unreachable"

        match p.PKind with
        | PVar name ->
            let hint =
                match scrutTy with
                | TNamed(tyName, _) ->
                    match typeDefFor env tyName with
                    | Some(Union def) -> didYouMean name (List.map fst def.Cases)
                    | _ -> ""
                | _ -> ""

            err p.PSpan $"'{name}' binds as a variable, so this arm matches every value — {tail}{hint}"
        | _ -> err p.PSpan $"this pattern matches every value — {tail}"
    | None ->

        if unguarded |> List.exists isIrrefutablePat then
            Ok()
        else
            match scrutTy with
            | TNamed _
            | TBool
            // per-component product coverage, the matrix walk
            | TTuple _
            // [D:seq-patterns]: [] + irrefutable-cons is a complete
            // seq analysis (F#'s list rule)
            | TSeq _ ->
                match missingCases env scrutTy unguarded with
                | [] -> Ok()
                | missing ->
                    let missingList = String.concat ", " missing
                    err span $"match is not exhaustive; missing: {missingList}"
            | ty -> err span $"match on {formatTy ty} needs a catch-all pattern"

// [D:lambda-core]: the five lambda arms (infer: unit/name/pattern;
// check-mode: name/pattern) share one assembly core — env extension,
// body typing, TFun construction. Each adapter keeps only its
// judgment delta (domain source + body strategy).
let private lambdaCore
    (env: TypeEnv)
    (span: Span)
    (mkKind: TypedExpr -> TypedKind)
    (dom: Ty)
    (binds: (string * Ty) list)
    (typeBody: TypeEnv -> Result<TypedExpr, TypeError>)
    : Result<TypedExpr, TypeError> =
    result {
        let! tbody = typeBody (bindParams env binds)

        return {
            Kind = mkKind tbody
            Ty = TFun(dom, tbody.Ty)
            Span = span
        }
    }

/// the union types declaring a case of this name [D:ambiguous-ctor] —
/// derived from the type table and shared with #help, so a hover cannot
/// answer confidently where the checker refuses, and a later
/// declaration cannot miss the collision. env.Values holds one entry
/// per name, so by the time a bare use is resolved the earlier
// a comma between record-literal fields parses as a tuple value whose
// later item is `name = …` — taught as the separator it is, as the list
// literal's one-tuple teaching does, before the field set mis-resolves
let private commaBetweenFields (fields: (string * Span * Expr) list) : Result<unit, TypeError> =
    fields
    |> List.tryPick (fun (_, _, v) ->
        match v.Kind with
        | ETuple(_ :: rest) ->
            rest
            |> List.tryPick (fun item ->
                match item.Kind with
                | EBinOp("=", { Kind = EVar n }, _) -> Some(n, item.Span)
                | _ -> None)
        | _ -> None)
    |> function
        | Some(n, span) ->
            err
                span
                $"record fields are separated by ';' or a newline, not ',' — the comma made a tuple and '{n} = …' an equality; write {{ … ; {n} = … }}"
        | None -> Ok()

// ---- validated types [D:validated-types] ---------------------------------

/// the validated union a written type name resolves to — bare, or
/// qualified through its import (`Mail.Email`)
let private validatedType (env: TypeEnv) (written: string) : (string * UnionDef) option =
    match resolveTypeName env written with
    | TypeKey key ->
        match typeDefFor env key with
        | Some(Union u) when u.Validated.IsSome -> Some(key, u)
        | _ -> None
    | _ -> None

/// the case of a validated union an import declares, by its module
/// alias and case name: Some the type's declared name
let private validatedModuleCase (env: TypeEnv) (m: string) (case: string) : string option =
    Map.tryFind m env.ModuleTypes
    |> Option.bind (fun owned ->
        owned
        |> Map.toList
        |> List.tryPick (fun (declared, key) ->
            match typeDefFor env key with
            | Some(Union u) when u.Validated.IsSome && u.Cases |> List.exists (fun (c, _) -> c = case) ->
                Some declared
            | _ -> None))

/// a flat boundary's view of a record [D:validated-types]: Args.load and
/// Env.load parse a validated field as its payload (the innermost one,
/// for a validated type over another), so each field law runs on what
/// the token holds; the field keeps its type name and the `create` ids,
/// outermost first, as attributes the loader validates through
let rec private validatedChain (env: TypeEnv) (ty: Ty) (ids: string list) (last: string) =
    match ty with
    | TNamed(n, []) ->
        match typeDefFor env n with
        | Some(Union({ Cases = [ (_, Some p) ] } as u)) when u.Validated.IsSome ->
            validatedChain env p (u.Validated.Value :: ids) n
        | _ -> ty, List.rev ids, last
    | _ -> ty, List.rev ids, last

let private flatFieldInner (fty: Ty) =
    match fty with
    | TNamed("Option", [ t ]) -> t, (fun b -> TNamed("Option", [ b ]))
    | t -> t, id

/// a flat boundary refuses a validated field over an optional payload
/// [D:validated-types]: a flag or variable has one absence, so it cannot
/// say whether the field or the payload is missing
let validatedOptionalPayload (env: TypeEnv) (def: RecordDef) : string option =
    def.Fields
    |> List.tryPick (fun (f, fty) ->
        match flatFieldInner fty with
        | TNamed(n, []) as inner, _ ->
            match validatedChain env inner [] "" with
            | TNamed("Option", [ bottom ]), _ :: _, innermost ->
                Some
                    $"field '{f}': '{n}' is validated over an optional payload — a flag/variable cannot tell \"absent\" from \"absent payload\"; declare the field `Option<{n}>` over a validated `{innermost} of {formatTy bottom}` instead"
            | _ -> None
        | _ -> None)

let validatedFieldsFlat (env: TypeEnv) (def: RecordDef) : RecordDef =
    def.Fields
    |> List.fold
        (fun (d: RecordDef) (f, fty) ->
            let inner, wrap = flatFieldInner fty

            match inner, validatedChain env inner [] "" with
            // an optional payload stays unflattened — the boundary
            // refuses it (validatedOptionalPayload)
            | _, (TNamed("Option", [ _ ]), _, _) -> d
            | TNamed(n, []), (payloadBase, (_ :: _ as ids), _) ->
                let marks = [
                    validatedAttr, Some(AStr(String.concat " " ids))
                    validatedNameAttr, Some(AStr n)
                ]

                {
                    d with
                        Fields = d.Fields |> List.map (fun (g, t) -> if g = f then g, wrap payloadBase else g, t)
                        Attrs = Map.add f ((Map.tryFind f d.Attrs |> Option.defaultValue []) @ marks) d.Attrs
                }
            | _ -> d)
        def

/// the constructor refusal: the case reads, it never builds
let validatedCtorMsg (shown: string) (typeSpelling: string) : string =
    $"'{shown}' is validated — build it with {typeSpelling}.create (it raises when validate refuses; `|> try result` for a Result)"

/// the derived `T.create : payload -> T`, raising validate's message — the runtime
/// registers it under the declaration's id when the `type` runs
let private createRef (env: TypeEnv) (span: Span) (written: string) : Result<TypedExpr, TypeError> =
    match validatedType env written with
    | Some(key, ({ Cases = [ (_, Some payload) ] } as u)) ->
        Ok {
            Kind = TEVar(createVarPrefix + u.Validated.Value)
            Ty = TFun(payload, TNamed(key, []))
            Span = span
        }
    | _ -> err span $"unreachable: '{written}' is no validated type"

// block-level recovery [D:block-recovery]: in recovery mode a failed
// block element is recorded and replaced by a hole of the type the
// position demands (unit in statement position, a fresh hole var as a
// let value), so the enclosing block types on; outside recovery mode
// the failure propagates unchanged
let private holeNode (span: Span) (ty: Ty) : TypedExpr = {
    Kind = TEHole span
    Ty = ty
    Span = span
}

let private holeVar (ctx: Ctx) : Ty = TVar(freshName ctx "__hole")

// `ty` is a thunk: a fresh hole var minted outside recovery mode would
// shift every later var's name, and some messages render them
let private recoverAt
    (ctx: Ctx)
    (span: Span)
    (ty: unit -> Ty)
    (r: Result<TypedExpr, TypeError>)
    : Result<TypedExpr, TypeError> =
    match r, ctx.Recovered with
    | Error e, Some seen ->
        ctx.Recovered <- Some((e, span) :: seen)
        Ok(holeNode span (ty ()))
    | _ -> r

// a hole-bound binder's scheme: ∀h.h on a hole var, so later uses stay
// silent under the existing hole convention [PLAN-diagnostics-arc B6]
let private holeScheme (ctx: Ctx) : Scheme =
    match holeVar ctx with
    | TVar v as ty -> {
        Forall = Set.singleton v
        Cs = Map.empty
        Ty = ty
        RowOrigins = Map.empty
        HoleDefaults = []
      }
    | _ -> failwith "unreachable: holeVar is a var"

let rec private patBinderNames (p: Pattern) : string list =
    match p.PKind with
    | PVar n -> [ n ]
    | _ -> patChildren p |> List.collect patBinderNames

// the ELetPat flavour: every binder leaf takes the hole scheme
let private recoverLetPat
    (ctx: Ctx)
    (pat: Pattern)
    (span: Span)
    (r: Result<(string * Scheme) list * TypedExpr, TypeError>)
    : Result<(string * Scheme) list * TypedExpr, TypeError> =
    match r, ctx.Recovered with
    | Error e, Some seen ->
        ctx.Recovered <- Some((e, span) :: seen)
        Ok(patBinderNames pat |> List.map (fun n -> n, holeScheme ctx), holeNode span (holeVar ctx))
    | _ -> r

let rec private infer (ctx: Ctx) (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
    match expr.Kind with
    // a failure as a value [D:try-form]: Option, or Result carrying the message
    | ETry(body, asResult) ->
        result {
            let! tb = infer ctx env body

            let ty =
                if asResult then
                    TNamed("Result", [ tb.Ty; TStr ])
                else
                    TNamed("Option", [ tb.Ty ])

            return {
                Kind = TETry(tb, asResult)
                Ty = ty
                Span = expr.Span
            }
        }
    // a command chain's `|> try` stage [D:try-form]: the form catches an
    // expression's raise, a command's failure is data through a reifier
    | EPipe(_, ({ Kind = ETryForm _ } as tf)) ->
        err
            tf.Span
            "a command's failure is data through a reifier, not `|> try` — `<command> | complete` (exitCode, stdout, stderr), `| succeeds` or `| exitCode`; or make it an expression first: `$(<command>) |> try`"
    | ETryForm _ ->
        err
            expr.Span
            "'try' goes after a pipeline: `<expr> |> try` gives an Option (None if it failed), `<expr> |> try result` a Result with the error message"
    // the inverse of try [D:orfail-form]: Ok v / Some v unwrap to v; Error
    // and None raise at the form
    | EOrFail(body, msg, site) ->
        result {
            let! tb = infer ctx env body

            let! tm =
                match msg with
                | None -> Ok None
                | Some m ->
                    infer ctx env m
                    |> Result.bind (fun tm ->
                        match resolve ctx tm.Ty with
                        | TStr -> Ok(Some tm)
                        | TVar _ as v -> bind ctx env m.Span TStr v |> Result.map (fun () -> Some tm)
                        | t -> err m.Span $"orFail's message is a string; this is {formatTy (finalTy ctx t)}")

            let shapes = "orFail takes a Result<'T, string> or an Option<'T>"

            let! ty =
                match resolve ctx tb.Ty with
                | TNamed("Option", [ t ]) -> Ok t
                | TNamed("Result", [ t; e ]) ->
                    (match resolve ctx e with
                     | TStr -> Ok t
                     | TVar _ as v -> bind ctx env body.Span TStr v |> Result.map (fun () -> t)
                     | _ -> err site $"{shapes}; this is {formatTy (finalTy ctx tb.Ty)} — its Error must carry a string")
                | TVar _ ->
                    err
                        site
                        $"{shapes}, and the type of its left side is not known yet — use it where the value's type is fixed (a call's result, a matched value)"
                | t -> err site $"{shapes}; this is {formatTy (finalTy ctx t)}"

            let tsite = {
                Kind = TEUnit
                Ty = TUnit
                Span = site
            }

            return {
                Kind = TEOrFail(tb, tm, tsite)
                Ty = ty
                Span = expr.Span
            }
        }
    // a command chain's `|> orFail` stage [D:orfail-form]: a failing
    // command already raises; the reifier adds the message
    | EPipe(_, ({ Kind = EOrFailForm _ } as form)) ->
        err
            form.Span
            "a failing command already stops the script; to stop with your message, use the reifier: `<command> | orFail \"msg\"`. `|> orFail` unwraps a Result or an Option"
    | EOrFailForm _ ->
        err
            expr.Span
            "'orFail' goes after a pipeline: `<expr> |> orFail` unwraps a Result or an Option and raises on Error/None (`|> orFail \"msg\"` adds your message); after a command, `<command> | orFail \"msg\"`"
    | EInt n ->
        Ok {
            Kind = TEInt n
            Ty = TInt
            Span = expr.Span
        }
    | EDur n ->
        Ok {
            Kind = TEDur n
            Ty = TDur
            Span = expr.Span
        }
    | EFloat f ->
        Ok {
            Kind = TEFloat f
            Ty = TFloat
            Span = expr.Span
        }
    | ESize b ->
        Ok {
            Kind = TESize b
            Ty = TSize
            Span = expr.Span
        }
    | ERetry(kind, opts, watch, body, until) ->
        result {
            let famTy =
                TNamed(
                    (match kind with
                     | LoopRetry -> "Retry"
                     | LoopPoll -> "Poll"
                     | LoopRepeat -> "Repeat"),
                    []
                )

            let head = loopHead kind
            let! topts = check ctx env opts famTy

            // watch= is poll's key [D:scoped-procs]: poll waits for
            // ready (a watched child dying is the answer); retry
            // retries failures — a dead process is not a transient one
            do!
                match watch with
                | Some w when kind = LoopRetry ->
                    err w.Span "watch= is poll's key (wait-for-ready); retry retries failures — run the wait under poll"
                | Some w when kind = LoopRepeat ->
                    err
                        w.Span
                        "watch= is poll's key (wait-for-ready); a repeat runs until it is stopped — check the process in the block (Proc.running)"
                | _ -> Ok()

            // interval= is required [D:repeat]: the key=value head's base
            // has none worth running
            do!
                match kind, opts.Kind with
                | LoopRepeat, EVar "|repeatDefaults" ->
                    err opts.Span "a repeat needs interval= — the wait between iterations (repeat interval=1s)"
                | LoopRepeat, EUpdate({ Kind = EVar "|repeatDefaults" }, ups) when
                    not (ups |> List.exists (fun (path, _) -> List.map fst path = [ "interval" ]))
                    ->
                    err opts.Span "a repeat needs interval= — the wait between iterations (repeat interval=1s)"
                | _ -> Ok()

            // a literal interval that is not positive is a hot loop by
            // accident [D:repeat]; a computed one refuses at run time
            do!
                match kind, opts.Kind with
                | LoopRepeat, EUpdate(_, ups) ->
                    let literalMs (e: Expr) =
                        match e.Kind with
                        | EDur ms -> Some ms
                        | EBinOp("-", { Kind = EInt 0L }, { Kind = EDur ms }) -> Some(-ms)
                        | _ -> None

                    match
                        ups
                        |> List.tryPick (fun (path, v) ->
                            match path with
                            | [ ("interval", _) ] ->
                                literalMs v
                                |> Option.filter (fun ms -> ms <= 0L)
                                |> Option.map (fun ms -> v, ms)
                            | _ -> None)
                    with
                    | Some(v, ms) ->
                        err
                            v.Span
                            $"a repeat's interval must be positive, got {formatDuration ms} — it is the wait between iterations, and no wait is a busy loop"
                    | None -> Ok()
                | _ -> Ok()

            let! twatch =
                match watch with
                | Some w -> check ctx env w (TNamed("Proc", [])) |> Result.map Some
                | None -> Ok None

            // a repeat block with no until is statement position: its
            // command tail streams, as an if body's [D:interior-arming]
            let! tbody =
                infer
                    ctx
                    env
                    (if kind = LoopRepeat && until.IsNone then
                         armTail body
                     else
                         body)

            match until with
            | Some((b, bs), pred) ->
                // a bool body is its own predicate [D:retry-poll]: an
                // until segment on top of one is a contradiction
                match resolve ctx tbody.Ty with
                | TBool ->
                    return!
                        err
                            body.Span
                            $"this {head} body yields bool, so it IS the predicate — drop the until segment (a different condition belongs in the body itself)"
                | bodyTy ->
                    let env' = bindValue b (generalize bodyTy) env

                    do! checkBinderName bs b

                    let! tpred = check ctx env' pred TBool

                    return {
                        Kind = TERetry(kind, topts, twatch, tbody, Some((b, bs), tpred))
                        Ty = bodyTy
                        Span = expr.Span
                    }
            | None ->
                match kind, resolve ctx tbody.Ty with
                // a repeat's block is work: unit, or a bool that ends it
                // like poll's predicate [D:repeat]
                | LoopRepeat, TBool ->
                    return {
                        Kind = TERetry(kind, topts, twatch, tbody, None)
                        Ty = TUnit
                        Span = expr.Span
                    }
                | LoopRepeat, (TUnit | TVar _) ->
                    do! bind ctx env body.Span TUnit tbody.Ty

                    return {
                        Kind = TERetry(kind, topts, twatch, tbody, None)
                        Ty = TUnit
                        Span = expr.Span
                    }
                | LoopRepeat, bodyTy ->
                    return!
                        err
                            body.Span
                            $"a repeat block is unit (the work of one iteration) or a bool that ends the loop when true; this one yields {formatTy bodyTy} — add `until r` to bind the value, or end the block with a unit statement"
                | _, TBool
                | _, TVar _ ->
                    do! bind ctx env body.Span TBool tbody.Ty

                    return {
                        Kind = TERetry(kind, topts, twatch, tbody, None)
                        Ty = TUnit
                        Span = expr.Span
                    }
                | _, bodyTy ->
                    return!
                        err
                            body.Span
                            $"{head} without an until segment needs a bool body (the body IS the predicate); this one yields {formatTy bodyTy} — add `until r` to bind the value, or end the body with a condition (cmd | succeeds)"
        }
    | EStr s ->
        Ok {
            Kind = TEStr s
            Ty = TStr
            Span = expr.Span
        }
    | EBool b ->
        Ok {
            Kind = TEBool b
            Ty = TBool
            Span = expr.Span
        }
    | EUnit ->
        Ok {
            Kind = TEUnit
            Ty = TUnit
            Span = expr.Span
        }
    | ECapture inner ->
        // erased [D:district-retirement]: the marker exists for the
        // statement gate; typing sees straight through
        infer ctx env inner
    | EAlways(body, cleanup) ->
        // the bare scope [D:within-always]: the scope's type is the
        // body's; the always block is teardown and must be unit. exit
        // is refused inside always (lexically — a shadowing binding is
        // still refused): an exit during teardown of an unwinding
        // scope is almost never meant, and it would swap the original
        // error for a code. retry/poll stay legal — a cleanup that
        // retries is reasonable, the deadline is the author's.
        result {
            let rec exitSite (e: Expr) : Ast.Span option =
                match e.Kind with
                | EVar "exit" -> Some e.Span
                | _ -> Ast.exprChildren e |> List.tryPick exitSite

            do!
                match exitSite cleanup with
                | Some sp ->
                    Error {
                        Message =
                            "exit inside always is refused — teardown of an unwinding scope must finish (the original error would become a code); fail \"msg\" carries a message instead"
                        Span = sp
                        Origin = None
                    }
                | None -> Ok()

            do! execInAlways body cleanup
            let! tbody = infer ctx env body
            let! tcleanup = check ctx env cleanup TUnit

            return {
                Kind = TEAlways(tbody, tcleanup)
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    | EHold body ->
        // the held scope's type is the body's [D:held-scope]
        result {
            let! tbody = infer ctx env body

            return {
                Kind = TEHold tbody
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    | EWithin(kind, binder, arg, opts, body) ->
        // the arg types are the kinds' contracts [D:within-scopes] and live
        // in withinContracts, shared with the check direction; the scope's
        // type is the body's type
        result {
            do! execInScope kind body
            let! targ, topts, benv = withinContracts ctx env kind binder arg opts

            let! tbody =
                match kind with
                | WithinPlan -> inferPlanBody ctx benv body
                | _ -> infer ctx benv body

            // the scope's type is the body's [D:within-scopes] — except a
            // plan block, which reifies the body's mutations and yields a
            // Plan [D:plan-apply] (the body's own value is discarded — it
            // runs for its capture, not its result)
            let scopeTy =
                match kind with
                | WithinPlan -> TNamed("Plan", [])
                | _ -> tbody.Ty

            return {
                Kind = TEWithin(kind, binder, targ, topts, tbody)
                Ty = scopeTy
                Span = expr.Span
            }
        }
    | ESeq(first, rest) ->
        // a sequenced element is a statement — its block's command tail
        // runs, as at top level [D:seq-arming]
        let first = armTail first

        result {
            let! tfirst =
                result {
                    let! tfirst =
                        // a diverging head's 'a meets the unit demand
                        // [D:fail-bottom] — the taught `fail "x" ; 0` stays legal
                        if divergesTo first then
                            check ctx env first TUnit
                        else
                            infer ctx env first

                    match resolve ctx tfirst.Ty with
                    | TUnit -> return tfirst
                    | ty -> return! err first.Span (seqUnitError first ty)
                }
                |> recoverAt ctx first.Span (fun () -> TUnit)

            let! trest = infer ctx env rest

            return {
                Kind = TESeq(tfirst, trest)
                Ty = trest.Ty
                Span = expr.Span
            }
        }
    | EVar(("print" | "printerr" | "|print") as pname) when isPrintFamily env pname ->
        // Bare-value position (e.g. Seq.iter print): defer like a splice
        // — the use site decides the type, the boundary validates it,
        // and only a genuinely undetermined use defaults to string
        let ty = instantiate ctx expr.Span printScheme

        (match ty with
         | TFun(TVar v, _) -> ctx.PendingPrints <- (v, expr.Span) :: ctx.PendingPrints
         | _ -> ())

        Ok {
            Kind = TEVar pname
            Ty = ty
            Span = expr.Span
        }
    // a backslash argv word [D:argv-backslash]: weir has no line
    // continuation by `\` (indentation continues a command line) and no
    // escapes in argv
    | EVar w when w.StartsWith "|backslash " ->
        let word = w.Substring 11

        if word = "\\" then
            err
                expr.Span
                "weir continues a command line by indentation — drop the `\\` and indent the next line (a literal backslash argument is \"\\\\\")"
        else
            err
                expr.Span
                $"weir has no backslash escapes in argv — `{word}` would pass the backslash too; quote the word instead: \"{word.Substring 1}\""
    | EVar name ->
        match Map.tryFind name env.Values with
        | Some sch ->
            // a bare constructor with two declarations is ambiguous, never
            // last-wins [D:ambiguous-ctor] — the record-literal path's answer
            // to the same question. Pattern positions need no twin: PCase
            // resolves against the scrutinee's type, and an unresolved
            // scrutinee is already refused.
            match ctorOwners env name with
            | _ :: _ :: _ as owners ->
                let ownerList = String.concat ", " owners

                err
                    expr.Span
                    $"ambiguous constructor '{name}'; it is declared by: {ownerList} — rename one of the cases"
            | [ owner ] when
                (match typeDefFor env owner with
                 | Some(Union u) -> u.Validated.IsSome
                 | _ -> false)
                ->
                err expr.Span (validatedCtorMsg name owner)
            | _ ->
                Ok {
                    Kind = TEVar name
                    Ty = instantiate ctx expr.Span sch
                    Span = expr.Span
                }
        | None ->
            if Map.containsKey name env.Modules then
                let members = env.Modules[name] |> Map.keys |> Seq.truncate 5 |> String.concat ", "

                err expr.Span $"'{name}' is a module; use a member: {name}.{{{members}, ...}}"
            else
                let homes =
                    env.Modules
                    |> Map.toList
                    |> List.choose (fun (m, members) ->
                        if Map.containsKey name members then
                            Some $"{m}.{name}"
                        else
                            None)

                match fieldHint env name with
                // a declared record's field written bare (`mode` for
                // `args.mode`): the field leads, a module member follows
                // as the alternative — dropped when a bound value fits
                | Some(hint, confident) ->
                    let alt =
                        match homes with
                        | _ when confident -> ""
                        | [] -> ""
                        | hs ->
                            "; or did you mean "
                            + (hs |> List.map (fun h -> $"'{h}'") |> String.concat " or ")
                            + "?"

                    err expr.Span $"unbound '{name}' — {hint}{alt}"
                | None ->
                    match homes with
                    // no history claim [D:bare-rule]: most of these were
                    // never bare — the wording matches the multi-home arm's
                    | [ one ] ->
                        // `dir` is DOS/cmd muscle memory for a listing, and
                        // Path.dir is the parent-of-a-path function — the
                        // qualified redirect alone sends the reader somewhere
                        // semantically unrelated [D:dir-teach]
                        let hint =
                            if name = "dir" then
                                " (the parent directory of a path) — for a directory listing, use ls"
                            else
                                ""

                        err expr.Span $"'{name}' is module-qualified; use '{one}'{hint}"
                    | _ :: _ ->
                        let all = String.concat " or " homes
                        err expr.Span $"'{name}' is module-qualified here; use {all}"
                    | [] ->
                        match retiredBare name with
                        | Some teach -> err expr.Span $"'{name}' is retired: {teach}"
                        | None when name = "scriptPath" ->
                            // script-only, the args/stdin family [D:script-path]
                            err
                                expr.Span
                                "scriptPath is script-only (the running script's absolute path; absent in the REPL and -e)"
                        | None when name = "List" || name = "Array" ->
                            // the commonest .NET prior (PLAN-dx-review D5): a
                            // confident wrong suggestion ('Post') is worse
                            // than none — answer from the registry instead
                            err
                                expr.Span
                                $"'{name}' is not a weir module — weir's sequences are 'Seq' (Seq.length, Seq.map, ...; one sequence type)"
                        // F#'s String module, the same prior [D:fs-reflexes]
                        | None when name = "String" ->
                            err
                                expr.Span
                                "'String' is not a weir module — weir's string functions are 'Str' (Str.trim, Str.split, Str.contains, ...)"
                        | None ->
                            // same-kind candidates only (PLAN-dx-review D5): a
                            // lowercase name never suggests a constructor and
                            // an uppercase one never suggests a binding — but a
                            // candidate differing only by case stays (Exit ->
                            // exit is the rename teaching [D:exit-rename])
                            let sameCase (c: string) =
                                (c.Length > 0 && System.Char.IsUpper c[0] = System.Char.IsUpper name[0])
                                || System.String.Equals(c, name, System.StringComparison.OrdinalIgnoreCase)

                            let hint =
                                didYouMean
                                    name
                                    (Map.keys env.Values |> Seq.filter Types.isUserName |> Seq.filter sameCase)

                            err expr.Span $"unbound variable '{name}'{hint}"
    | ELet(name, nameSpan, value, body) ->
        result {
            let! tvalue =
                result {
                    do! checkBinderName expr.Span name
                    return! infer ctx env value
                }
                |> recoverAt ctx value.Span (fun () -> holeVar ctx)

            let valueTy = finalTy ctx tvalue.Ty

            let scheme = generalizeLet ctx env valueTy

            let! tbody = infer ctx (bindValue name scheme env) body

            return {
                Kind = TELet(name, nameSpan, tvalue, tbody)
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    | ELambdaPat(pat, body) ->
        result {
            let! shape, binds = binderShape ctx env pat
            return! lambdaCore env expr.Span (fun tb -> TELambdaPat(pat, tb)) shape binds (fun e -> infer ctx e body)
        }
    | ELetPat(pat, value, body) ->
        result {
            let! schemes, tvalue =
                result {
                    // binder judged first: casing/refutability errors beat any
                    // error inside the value (the binder is what the user wrote) —
                    // unless a case in it needs the value's type to resolve
                    let! shape, binds, tvalue =
                        if binderNeedsValueType env pat then
                            result {
                                let! tvalue = infer ctx env value
                                let! shape, binds = binderShapeAt ctx env (Some tvalue.Ty) pat
                                return shape, binds, tvalue
                            }
                        else
                            result {
                                let! shape, binds = binderShape ctx env pat
                                let! tvalue = infer ctx env value
                                return shape, binds, tvalue
                            }

                    do! bind ctx env pat.PSpan shape tvalue.Ty

                    return binds |> List.map (generalizeBinding ctx env), tvalue
                }
                |> recoverLetPat ctx pat value.Span

            let! tbody =
                infer ctx (schemes |> List.fold (fun e (n, s) -> bindValue n s e) env) body

            return {
                Kind = TELetPat(pat, tvalue, tbody)
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    | ELambda("()", pspan, body) ->
        // the unit param pins its type — desugaring to an unconstrained
        // fresh var would generalize (`cleanup 5` would typecheck); the
        // "()" name is unforgeable, and no binding is added
        lambdaCore env expr.Span (fun tb -> TELambda("()", pspan, tb)) TUnit [] (fun e -> infer ctx e body)
    | ELambda(param, pspan, body) ->
        result {
            do! checkBinderName expr.Span param
            let paramTy = TVar(freshName ctx "a")

            return!
                lambdaCore env expr.Span (fun tb -> TELambda(param, pspan, tb)) paramTy [ param, paramTy ] (fun e ->
                    infer ctx e body)
        }
    // a literal exit code outside 0..255 is a check error [D:exit-range]
    // (`exit` is reserved, so the head is always the builtin); `-n`
    // arrives as the prefix-minus desugar `0 - n`
    | EApp({ Kind = EVar "exit" }, arg) when
        (match exitLiteral arg with
         | Some n -> n < 0L || n > 255L
         | None -> false)
        ->
        let code = (exitLiteral arg).Value

        err
            arg.Span
            $"exit {code}: an exit code is 0..255 — the OS keeps only the low byte, so it would exit {code &&& 255L}"
    | EApp({ Kind = EVar "Json"; Span = headSpan }, arg) when ctorOwners env "Json" = [ "HttpBody" ] ->
        // Http's Json body takes the value [D:http-dx]: a jsonable
        // argument routes through `to json`'s own law and wire maps at
        // the call site, so `body = Json record` renders at the
        // boundary. A seq<string> argument stays the pre-rendered
        // document (`Json (x |> to json)` unchanged) — the payload
        // representation never changes, only what the constructor
        // admits.
        result {
            let! targ = infer ctx env arg
            let httpBody = TNamed("HttpBody", [])

            let ctorHead = {
                Kind = TEVar "Json"
                Ty = TFun(TSeq TStr, httpBody)
                Span = headSpan
            }

            let app (payload: TypedExpr) = {
                Kind = TEApp(ctorHead, payload)
                Ty = httpBody
                Span = expr.Span
            }

            match resolve ctx targ.Ty with
            | TSeq inner when resolve ctx inner = TStr -> return app targ
            | TSeq inner when
                (match resolve ctx inner with
                 | TVar _ -> true
                 | _ -> false)
                ->
                // an unresolved element (Json []) keeps the pre-rendered law
                do! bind ctx env arg.Span TStr (resolve ctx inner)
                return app targ
            | TVar _ ->
                do! bind ctx env arg.Span (TSeq TStr) targ.Ty
                return app targ
            | _ ->
                // generic records cross monomorphized [D:wire-generics]
                let wty, wenv = wireMono ctx env targ.Ty
                do! jsonableElem false arg.Span wenv wty

                let! unions = unionWriteTable arg.Span wenv wty

                let tto = {
                    Kind = TETo("json", wireRenamesOf wenv wty, wireCodecsOf wenv wty, unions, false, None)
                    Ty = TFun(targ.Ty, TSeq TStr)
                    Span = arg.Span
                }

                let rendered = {
                    Kind = TEPipe(targ, tto)
                    Ty = TSeq TStr
                    Span = arg.Span
                }

                return app rendered
        }
    | EApp _ ->
        let head, args = spine expr

        // unapplied only [D:operator-values]: a partially applied infix
        // operator reads backwards — (>) 10 would mean fun x -> 10 > x,
        // which is exactly the direction authors get wrong. The
        // teaching shows both spellings so the direction is chosen,
        // not guessed — and offers them as interchangeable only for a
        // commutative op.
        match head.Kind with
        | EOpValue op ->
            // the commutative set: ops where fun x -> x op v and
            // fun x -> v op x are the same function at every type the
            // checker could see here (== and <> are symmetric; * is
            // scalar multiplication at all its overloads, both operand
            // orders admitted). + is excluded: it concatenates strings
            // by context and the operand type is unknown at this
            // refusal site. When in doubt, out — a wrong "differ" is
            // merely verbose, a wrong "or" teaches the wrong function.
            let commutative = [ "=="; "<>"; "*" ]

            let directions =
                if List.contains op commutative then
                    $"write the lambda with its direction explicit: fun x -> x {op} v (or fun x -> v {op} x)"
                else
                    $"write the direction you mean: fun x -> x {op} v and fun x -> v {op} x differ"

            err
                head.Span
                ($"an operator cannot be partially applied — ({op}) v would mean fun x -> v {op} x, "
                 + $"which reads backwards; {directions}")
        | _ ->

            // the wrap-it hint [D:district-retirement]: a bare command in an
            // expression fragment reads as an application of its unbound head
            // word — the repair is the wrapper, so the rejection names it
            // (did-you-mean coexists; the specific teachings — module homes,
            // retirements, scriptPath — keep their own arms via fall-through).
            // `m[k]` is refused, and the refusal teaches [D:accessor-teaching]:
            // the raw unification error pointed at the key (`|seqItem`'s int
            // index meets the key first) and never mentioned Map — so the
            // target is inferred first, before the index can mis-report.
            let mapIndexHint =
                match head.Kind, args with
                | EVar("|seqItem" | "|seqItemFromEnd"), [ _; target ] ->
                    match infer ctx env target with
                    | Ok t ->
                        match resolve ctx t.Ty with
                        | TNamed("Map", _) ->
                            err
                                expr.Span
                                "no m[k] indexing on a Map — Map.get k m is the spelling (Map.tryGet for the asking form)"
                        // Bytes slices [D:range-slicing] but has no single
                        // b[i] — the leaky "expected seq" would mention the
                        // index desugar's own type, so teach the byte forms
                        | TBytes ->
                            err
                                expr.Span
                                "no b[i] index on Bytes — slice it (b[a..b], inclusive) or take a window with Bytes.sub start len"
                        // a string has no s[i] — the raw "expected seq"
                        // would name the desugar's own var; teach the
                        // character forms instead
                        | TStr ->
                            err
                                expr.Span
                                "no s[i] index on a string — slice it (s[i..i], inclusive) or take characters with Str.sub start len"
                        | _ -> Ok()
                    // a broken target reports through the ordinary path
                    | Error _ -> Ok()
                | _ -> Ok()

            let wrapItHint =
                match head.Kind with
                | EVar n when
                    not (Map.containsKey n env.Values)
                    && n.Length > 0
                    && System.Char.IsLower n[0]
                    && not (env.Modules |> Map.exists (fun _ members -> Map.containsKey n members))
                    && (retiredBare n).IsNone
                    && n <> "scriptPath"
                    ->
                    let hint = didYouMean n (Map.keys env.Values |> Seq.filter Types.isUserName)

                    err
                        head.Span
                        $"unbound variable '{n}'{hint} — if you meant to run a command here, wrap it: $({n} …)"
                | _ -> Ok()

            // the Map refusal is the more specific of the two, so it leads
            match mapIndexHint |> Result.bind (fun () -> wrapItHint) with
            | Error e -> Error e
            | Ok() ->

                // the named / qualified record literal [D:modules-v1]: `Ctx { .. }`
                // parses as EApp(EVar Ctx, ERecord) and `Git.Ctx { .. }` as
                // EApp(EField(Git, Ctx), ERecord). A record type name is never a
                // value, so this only fires where a bare application would error —
                // no change to `Some { .. }` (Some is a ctor, not a type).
                let namedRecord (typeName: string) (fields: (string * Span * Expr) list) =
                    result {
                        do! commaBetweenFields fields
                        // a receipt is minted, never written [D:receipts-read-only]
                        do!
                            (if isReceipt typeName then
                                 err expr.Span (receiptLiteralMsg typeName)
                             else
                                 Ok())

                        let def =
                            match typeDefFor env typeName with
                            | Some(Record d) -> d
                            | _ -> failwith "named record target is not a record"

                        match firstDup (fields |> List.map (fun (n, _, _) -> n)) with
                        | Some dup ->
                            let _, dupSpan, _ = fields |> List.findBack (fun (n, _, _) -> n = dup)
                            return! err dupSpan $"duplicate field '{dup}'"
                        | None ->
                            let litNames = fields |> List.map (fun (n, _, _) -> n) |> Set.ofList
                            let defNames = def.Fields |> List.map fst |> Set.ofList

                            if litNames <> defNames then
                                let show s = String.concat ", " (Set.toList s)

                                let detail =
                                    [
                                        if not (Set.isEmpty (Set.difference defNames litNames)) then
                                            $"missing {show (Set.difference defNames litNames)}"
                                        if not (Set.isEmpty (Set.difference litNames defNames)) then
                                            $"unknown {show (Set.difference litNames defNames)}"
                                    ]
                                    |> String.concat "; "

                                return! err expr.Span $"record '{typeName}' has fields {show defNames} ({detail})"
                            else
                                let targs = def.Params |> List.map (fun _ -> TVar(freshName ctx "a"))

                                let checkField (name: string, _: Span, value: Expr) =
                                    let declaredTy =
                                        def.Fields
                                        |> List.find (fun (f, _) -> f = name)
                                        |> snd
                                        |> substParams def.Params targs

                                    check ctx env value declaredTy |> Result.map (fun tv -> name, tv)

                                let! tfields =
                                    fields
                                    |> List.fold
                                        (fun acc f ->
                                            acc
                                            |> Result.bind (fun ts -> checkField f |> Result.map (fun t -> t :: ts)))
                                        (Ok [])

                                // the value carries the declared name, the
                                // type its env key [D:qualified-type-names]
                                return {
                                    Kind = TERecord(def.Name, List.rev tfields)
                                    Ty = TNamed(typeName, targs)
                                    Span = expr.Span
                                }
                    }

                // `Env.load A.Cfg` / `Args.load A.Cli`: a type argument
                // module A declares reads as its env key [D:qualified-type-names]
                let typeArgKind (arg: Expr) =
                    match arg.Kind with
                    | EField({ Kind = EVar m }, t, _) when not (Map.containsKey m env.Values) ->
                        match Map.tryFind m env.ModuleTypes |> Option.bind (Map.tryFind t) with
                        | Some key -> { arg with Kind = EVar key }
                        | None -> arg
                    | _ -> arg

                // a qualified literal's env key, when module m declares a
                // record of that name [D:qualified-type-names]
                let qualifiedRecordKey (m: string) (tyName: string) =
                    match Map.tryFind m env.ModuleTypes |> Option.bind (Map.tryFind tyName) with
                    | Some key ->
                        match typeDefFor env key with
                        | Some(Record _) -> Some key
                        | _ -> None
                    | None -> None

                match head.Kind, args with
                | EVar tyName, [ { Kind = ERecord fields } ] when
                    (match typeDefFor env tyName with
                     | Some(Record _) -> true
                     | _ -> false)
                    && not (Map.containsKey tyName env.Values)
                    ->
                    namedRecord tyName fields
                | EVar tyName, [ { Kind = ERecord _ } ] when
                    (match resolveTypeName env tyName with
                     | TypeAmbiguous _ -> true
                     | _ -> false)
                    && not (Map.containsKey tyName env.Values)
                    ->
                    err head.Span (unknownTypeMsg env tyName)
                | EField({ Kind = EVar m }, tyName, _), [ { Kind = ERecord fields } ] when
                    (qualifiedRecordKey m tyName).IsSome
                    ->
                    namedRecord (qualifiedRecordKey m tyName).Value fields
                | EField({ Kind = EVar "Env" }, "load", _), [ arg ] when
                    not (Map.containsKey "Env" env.Values) && Map.containsKey "Env" env.Modules
                    ->
                    // Env.load T — a typed-boundary instance (from json, env).
                    // Imitates from-json's type-name-in-special-position
                    // resolution, relocated to expression position.
                    (match (typeArgKind arg).Kind with
                     | EVar tyName when (loadShapeDup env tyName).IsSome ->
                         err arg.Span $"duplicate field '{(loadShapeDup env tyName).Value}' in the anonymous record"
                     | EVar tyName ->
                         match typeDefFor env tyName with
                         | Some(Record def) when def.Params.IsEmpty ->
                             // a validated field loads as its payload, then
                             // validates [D:validated-types]
                             let def = validatedFieldsFlat env def
                             // an enum field [D:env-enums]: a monomorphic union,
                             // every case 0-arity — the declared set becomes a
                             // boundary conversion exactly like int/bool
                             let unionOf ft =
                                 match ft with
                                 | TNamed(n, [])
                                 | TNamed("Option", [ TNamed(n, []) ]) ->
                                     match typeDefFor env n with
                                     | Some(Union u) when u.Params.IsEmpty -> Some(n, u)
                                     | _ -> None
                                 | _ -> None

                             let isEnum ft =
                                 match unionOf ft with
                                 | Some(_, u) -> u.Cases |> List.forall (fun (_, p) -> p.IsNone)
                                 | None -> false

                             let loadable ty =
                                 match ty with
                                 | TStr
                                 | TInt
                                 | TFloat
                                 | TBool
                                 | TDur
                                 | TSize
                                 // env is the secret channel in CI [D:secret]:
                                 // secrets.GITHUB_TOKEN becomes an env var, so a
                                 // Secret field is the main producer, not a
                                 // compromise (the non-claim is in SECURITY.md)
                                 | TSecret
                                 // --since 2026-08-01 [D:instant] — the boundary
                                 // parsing these types exist for
                                 | TInstant
                                 | TNamed("Option", [ TStr | TInt | TFloat | TBool | TDur | TSize | TInstant | TSecret ]) ->
                                     true
                                 | ty -> isEnum ty

                             // a payload-carrying case is a schema error, named at
                             // check time — env values are single tokens
                             let payloadCase =
                                 def.Fields
                                 |> List.tryPick (fun (f, ft) ->
                                     unionOf ft
                                     |> Option.bind (fun (n, u) ->
                                         u.Cases
                                         |> List.tryFind (fun (_, p) -> p.IsSome)
                                         |> Option.map (fun (c, _) -> f, n, c)))

                             // case-insensitive matching makes same-cased pairs
                             // ambiguous — the Args subcommand collision's env
                             // sibling (two rules, two conventions [D:env-enums])
                             let caseCollision =
                                 def.Fields
                                 |> List.tryPick (fun (_, ft) ->
                                     unionOf ft
                                     |> Option.bind (fun (_, u) ->
                                         u.Cases
                                         |> List.map (fun (c, _) -> c, c.ToLowerInvariant())
                                         |> List.groupBy snd
                                         |> List.tryPick (fun (w, g) ->
                                             match g with
                                             | (a, _) :: (b, _) :: _ -> Some(a, b, w)
                                             | _ -> None)))

                             // Default on an enum field: attribute literals are
                             // string/int/bool [D:default-attr] — the resting
                             // point spells Option + defaultValue
                             let enumDefault =
                                 def.Fields
                                 |> List.tryPick (fun (f, ft) ->
                                     match ft, Argv.defaultOf def f with
                                     | TNamed(n, []), Some _ when isEnum ft -> Some(f, n)
                                     | _ -> None)

                             match validatedOptionalPayload env def with
                             | Some msg -> err arg.Span msg
                             | None ->

                                 match payloadCase with
                                 | Some(f, n, c) ->
                                     err
                                         arg.Span
                                         $"'{f}': env values are single tokens, so enum fields need 0-arity cases; case '{c}' of {n} carries a payload"
                                 | None ->

                                     match def.Fields |> List.tryFind (fun (_, ft) -> not (loadable ft)) with
                                     | Some(bad, badTy) ->
                                         err
                                             arg.Span
                                             $"Env.load fields must be string, int, float, bool, Duration, Size, Instant, Secret, an enum union (0-arity cases), or Option of these; '{bad}' is {formatTy badTy}"
                                     | None ->

                                         match caseCollision with
                                         | Some(a, b, word) ->
                                             err arg.Span $"cases '{a}' and '{b}' collide as env value '{word}'"
                                         | None ->

                                             match enumDefault with
                                             | Some(f, n) ->
                                                 err
                                                     arg.Span
                                                     $"'{f}': an enum field takes no Default (attribute literals are string/int/bool) — spell the resting point Option<{n}> with Option.defaultValue"
                                             | None ->
                                                 // the resting-point cells under env's field law
                                                 // [D:default-attr]: text bools carry no presence
                                                 // semantics, so both Default literals are legal
                                                 // here — the Args-side false-is-redundant cell
                                                 // flips (validation is the consumer's arm).
                                                 // Bool-false is legal here [D:default-attr];
                                                 // both Default rules sit adjacent in Argv
                                                 match Argv.badEnvDefault def with
                                                 | Some msg -> err arg.Span msg
                                                 | None ->
                                                     let enums =
                                                         def.Fields
                                                         |> List.choose (fun (_, ft) ->
                                                             unionOf ft
                                                             |> Option.map (fun (n, u) -> n, u.Cases |> List.map fst))
                                                         |> Map.ofList

                                                     Ok {
                                                         Kind = TEEnvLoad(def, enums)
                                                         Ty = TNamed(tyName, [])
                                                         Span = expr.Span
                                                     }
                         | Some(Record def) ->
                             err arg.Span $"Env.load needs a monomorphic record; '{tyName}' is generic"
                         | Some(Union _) -> err arg.Span $"'{tyName}' is a union; Env.load needs a record"
                         | None -> err arg.Span (unknownTypeMsg env tyName)
                     // a value literal where the inline TYPE goes [D:env-inline-type]
                     | EAnonRecord _ ->
                         err
                             arg.Span
                             "Env.load takes a type, not a value — the inline type spells fields with ':': Env.load {| PORT: int |}"
                     | _ -> err arg.Span "Env.load takes a record type name, e.g. Env.load Config")
                | EField({ Kind = EVar "Args" }, "load", _), [ arg ] when
                    not (Map.containsKey "Args" env.Values) && Map.containsKey "Args" env.Modules
                    ->
                    // Args.load T — a typed-boundary instance [D:typed-argv]:
                    // Env.load's sibling; the union acceptance is the delta.
                    // The script-mode signal is Self.args [D:self-module]
                    // (injected per-run by baseEnvs; the static Self module
                    // carries prompt alone everywhere [D:prompt], so the
                    // member, not the module, marks a script). Args.load
                    // reads Session.ScriptArgs at eval, so this is purely
                    // the availability gate.
                    (if
                         not (
                             match Map.tryFind "Self" env.Modules with
                             | Some ms -> Map.containsKey "args" ms
                             | None -> false
                         )
                     then
                         err expr.Span "Args.load is script-only (Self.args is not available here)"
                     else
                         let validateFields span (label: string) (def: RecordDef) =
                             // Default cells / field shapes / flag collisions —
                             // the policy lives in Argv [D:typed-argv]; the arm
                             // keeps the span plumbing
                             match
                                 validatedOptionalPayload env def |> Option.map ((+) label),
                                 Argv.fieldProblems label def
                             with
                             | Some msg, _
                             | None, Some msg -> err span msg
                             | None, None -> Ok()

                         // case collisions + payload validation, shared by the bare-union
                         // and shared-flags shapes [D:shared-flags]
                         let unionPayloads span (udef: UnionDef) =
                             let lowered = udef.Cases |> List.map (fun (c, _) -> c, c.ToLowerInvariant())

                             match lowered |> List.groupBy snd |> List.tryFind (fun (_, g) -> g.Length > 1) with
                             | Some(word, (a, _) :: (b, _) :: _) ->
                                 err span $"cases '{a}' and '{b}' collide as subcommand '{word}'"
                             | _ ->
                                 let payloadErr c =
                                     err
                                         span
                                         $"case '{c}' must carry a single record payload; spell it as a record type"

                                 let rec buildPayloads acc cases =
                                     match cases with
                                     | [] -> Ok acc
                                     | (_, None) :: rest -> buildPayloads acc rest
                                     | (c, Some(TNamed(rn, []))) :: rest ->
                                         match typeDefFor env rn with
                                         | Some(Record rdef) when rdef.Params.IsEmpty ->
                                             let rdef = validatedFieldsFlat env rdef

                                             validateFields span $"case '{c}': " rdef
                                             |> Result.bind (fun () -> buildPayloads (Map.add c rdef acc) rest)
                                         | _ -> payloadErr c
                                     | (c, Some _) :: _ -> payloadErr c

                                 buildPayloads Map.empty udef.Cases

                         match (typeArgKind arg).Kind with
                         | EVar tyName when (loadShapeDup env tyName).IsSome ->
                             err arg.Span $"duplicate field '{(loadShapeDup env tyName).Value}' in the anonymous record"
                         | EVar tyName ->
                             match typeDefFor env tyName with
                             | Some(Record def) when def.Params.IsEmpty ->
                                 // a validated field parses as its payload
                                 // [D:validated-types]
                                 let def = validatedFieldsFlat env def
                                 // the field law [D:shared-flags]: at most one
                                 // union-typed field — the subcommand slot; its
                                 // scalar siblings are shared flags
                                 let unionFields =
                                     def.Fields
                                     |> List.choose (fun (f, ft) ->
                                         match ft with
                                         | TNamed(n, []) ->
                                             match typeDefFor env n with
                                             | Some(Union u) when u.Params.IsEmpty -> Some(f, u)
                                             | _ -> None
                                         | _ -> None)

                                 match validatedOptionalPayload env def with
                                 | Some msg -> err arg.Span msg
                                 | None ->

                                     match unionFields with
                                     | [] ->
                                         result {
                                             do! validateFields arg.Span "" def

                                             return {
                                                 Kind = TEArgsLoad(ArgsRecord def)
                                                 Ty = TNamed(tyName, [])
                                                 Span = expr.Span
                                             }
                                         }
                                     | [ (uf, udef) ] ->
                                         result {
                                             let sharedDef = Argv.sharedOf def uf

                                             // the subcommand slot derives no flag, so
                                             // Default has nothing to rest on [D:default-attr]
                                             do!
                                                 (match
                                                     def.Attrs
                                                     |> Map.tryFind uf
                                                     |> Option.bind (List.tryFind (fun (n, _) -> n = "Default"))
                                                  with
                                                  | Some _ ->
                                                      err
                                                          arg.Span
                                                          $"'{uf}' is the subcommand slot: no flag derives there, so Default has no meaning"
                                                  | None -> Ok())

                                             do! validateFields arg.Span "" sharedDef
                                             let! payloads = unionPayloads arg.Span udef

                                             // a name declared in both tiers is a schema
                                             // error — reject-don't-guess; the runtime
                                             // scanner never faces the question.
                                             // Minted --no-X twins ride in both tiers'
                                             // namespaces [D:default-attr]
                                             let sharedFlags =
                                                 (sharedDef.Fields |> List.map (fun (f, _) -> Argv.kebabFlag f))
                                                 @ (Argv.mintedFlags sharedDef |> List.map snd)
                                                 |> Set.ofList

                                             let sharedShorts =
                                                 Argv.explicitShorts sharedDef |> List.map snd |> Set.ofList

                                             let collision =
                                                 payloads
                                                 |> Map.toList
                                                 |> List.tryPick (fun (_, rdef) ->
                                                     (rdef.Fields |> List.map (fun (f, _) -> Argv.kebabFlag f))
                                                     @ (Argv.mintedFlags rdef |> List.map snd)
                                                     |> List.tryPick (fun k ->
                                                         if Set.contains k sharedFlags then
                                                             Some(
                                                                 $"flag '--{k}' is declared in {def.Name} and {rdef.Name}; "
                                                                 + "shared flags are declared once"
                                                             )
                                                         else
                                                             None)
                                                     |> Option.orElse (
                                                         Argv.explicitShorts rdef
                                                         |> List.tryPick (fun (_, sh) ->
                                                             if Set.contains sh sharedShorts then
                                                                 Some(
                                                                     $"'-{sh}' is claimed by [<Short>] in both {def.Name} and {rdef.Name}; "
                                                                     + "a short is declared once"
                                                                 )
                                                             else
                                                                 None)
                                                     ))

                                             match collision with
                                             | Some msg -> return! err arg.Span msg
                                             | None ->
                                                 return {
                                                     Kind = TEArgsLoad(ArgsShared(def, uf, udef, payloads))
                                                     Ty = TNamed(tyName, [])
                                                     Span = expr.Span
                                                 }
                                         }
                                     | (a, _) :: (b, _) :: _ ->
                                         err
                                             arg.Span
                                             $"'{a}' and '{b}' are both union-typed: one subcommand slot per record"
                             | Some(Union udef) when udef.Params.IsEmpty ->
                                 result {
                                     let! payloads = unionPayloads arg.Span udef

                                     return {
                                         Kind = TEArgsLoad(ArgsUnion(udef, payloads))
                                         Ty = TNamed(tyName, [])
                                         Span = expr.Span
                                     }
                                 }
                             | Some(Record _)
                             | Some(Union _) ->
                                 err arg.Span $"Args.load needs a monomorphic type; '{tyName}' is generic"
                             | None -> err arg.Span (unknownTypeMsg env tyName)
                         | EAnonRecord _ ->
                             err
                                 arg.Span
                                 "Args.load takes a type, not a value — the inline type spells fields with ':': Args.load {| verbose: bool |}"
                         | _ -> err arg.Span "Args.load takes a type name, e.g. Args.load Cli")
                // a reified command's argv words [D:secret]: each element
                // obeys the command-argument law a bare command's does, so
                // `curl -H $auth … | complete` takes the Secret; the marker
                // erases to the list (seq<string> to the reifier — Builtins
                // renders each word as Eval.scalarString does)
                | EVar "|argv", [ { Kind = EList items } as lst ] ->
                    result {
                        let! titems =
                            items
                            |> List.fold
                                (fun acc a ->
                                    acc
                                    |> Result.bind (fun ts ->
                                        checkScalarSplice ctx env CmdArg a |> Result.map (fun t -> t :: ts)))
                                (Ok [])

                        return {
                            Kind = TEList(List.rev titems)
                            Ty = TSeq TStr
                            Span = lst.Span
                        }
                    }
                // `NAME=$s cmd` / `within env NAME=$s` [D:secret]: the
                // overlay value is a string or a Secret (the CI token's
                // channel into a child) — Env.pair itself stays string-only
                | EVar "|envPair", [ k; v ] ->
                    result {
                        let! tk = check ctx env k TStr
                        let! tv = infer ctx env v

                        let! vTy =
                            match resolve ctx tv.Ty with
                            | TSecret -> Ok TSecret
                            | _ -> bind ctx env v.Span TStr tv.Ty |> Result.map (fun () -> TStr)

                        let envVarTy = TNamed("EnvVar", [])

                        let tf = {
                            Kind = TEVar "|envPair"
                            Ty = TFun(TStr, TFun(vTy, envVarTy))
                            Span = head.Span
                        }

                        return {
                            Kind =
                                TEApp(
                                    {
                                        Kind = TEApp(tf, tk)
                                        Ty = TFun(vTy, envVarTy)
                                        Span = expr.Span
                                    },
                                    tv
                                )
                            Ty = envVarTy
                            Span = expr.Span
                        }
                    }
                | EVar(("print" | "printerr" | "|print") as pname), [ arg ] when isPrintFamily env pname ->
                    result {
                        let! targ = infer ctx env arg
                        let! argTy = printArgTy ctx env arg.Span targ.Ty

                        let tprint = {
                            Kind = TEVar pname
                            Ty = TFun(argTy, TUnit)
                            Span = head.Span
                        }

                        return {
                            Kind = TEApp(tprint, targ)
                            Ty = TUnit
                            Span = expr.Span
                        }
                    }
                | _ -> checkSpine ctx env head args None
    | EPipe(arg, ({ Kind = ETo(fmt, streamOf) } as toExpr)) ->
        result {
            let! targ = infer ctx env arg

            // the write-side stream word is YAML's [D:yaml-seq-doc] —
            // json's per-element form is the thing named jsonl
            do!
                if streamOf && fmt <> "yaml" then
                    err
                        toExpr.Span
                        $"'to {fmt} stream' does not exist — one document per element is 'to jsonl'; one array document is 'to json'"
                else
                    Ok()

            // a hole (an earlier, already-reported error) takes the
            // per-element shape the stream forms demand, so the cascade
            // stays silent [PLAN-diagnostics-arc B6]
            do!
                match resolve ctx targ.Ty with
                | TVar v when v.StartsWith "__hole" && (fmt = "jsonl" || (fmt = "yaml" && streamOf)) ->
                    bind ctx env arg.Span (TSeq(holeVar ctx)) targ.Ty
                | _ -> Ok()

            match fmt, resolve ctx targ.Ty with
            | "json", _ ->
                // One document [D:to-jsonl]: the top level admits exactly
                // what an element admits — record → object, seq → array,
                // None → null; the per-element (NDJSON) form is 'to jsonl'.
                // Generic records cross monomorphized [D:wire-generics]
                let wty, wenv = wireMono ctx env targ.Ty
                do! jsonableElem false toExpr.Span wenv wty

                let! unions = unionWriteTable toExpr.Span wenv wty

                let tto = {
                    Kind = TETo(fmt, wireRenamesOf wenv wty, wireCodecsOf wenv wty, unions, false, None)
                    Ty = TFun(targ.Ty, TSeq TStr)
                    Span = toExpr.Span
                }

                return {
                    Kind = TEPipe(targ, tto)
                    Ty = TSeq TStr
                    Span = expr.Span
                }
            | "jsonl", TSeq elem ->
                let wty, wenv = wireMono ctx env targ.Ty
                let welem, _ = wireMono ctx env elem
                do! jsonableElem false toExpr.Span wenv welem
                let! unions = unionWriteTable toExpr.Span wenv wty

                let tto = {
                    Kind = TETo(fmt, wireRenamesOf wenv wty, wireCodecsOf wenv wty, unions, false, None)
                    Ty = TFun(targ.Ty, TSeq TStr)
                    Span = toExpr.Span
                }

                return {
                    Kind = TEPipe(targ, tto)
                    Ty = TSeq TStr
                    Span = expr.Span
                }
            | "jsonl", ty ->
                return!
                    err
                        arg.Span
                        $"'to jsonl' needs a seq (a document per element), got {formatTy ty} — one document is 'to json'"
            | "yaml", ty when streamOf ->
                // `to yaml stream` [D:yaml-seq-doc]: one document per
                // element — the bundle write, mirror of `from yaml
                // stream T`. Demands a seq, like jsonl.
                let wty, wenv = wireMono ctx env targ.Ty

                do!
                    match wty with
                    | TSeq elem -> yamlableOut toExpr.Span wenv Set.empty "" elem
                    | _ ->
                        err
                            arg.Span
                            $"'to yaml stream' needs a seq (a document per element), got {formatTy ty} — one document is 'to yaml'"

                let! unions = unionWriteTable toExpr.Span wenv wty

                let guide =
                    match wty with
                    | TSeq elem -> yamlShape toExpr.Span wenv Set.empty "" elem |> Result.toOption
                    | _ -> None

                let tto = {
                    Kind = TETo("yaml", wireRenamesOf wenv wty, wireCodecsOf wenv wty, unions, true, guide)
                    Ty = TFun(targ.Ty, TSeq TStr)
                    Span = toExpr.Span
                }

                return {
                    Kind = TEPipe(targ, tto)
                    Ty = TSeq TStr
                    Span = expr.Span
                }
            | "yaml", TNamed("YamlPatch", _) ->
                // patches do not render [D:yaml-nodes] — the type carries
                // the law, so no tombstone can ever leak into a file
                return!
                    err
                        toExpr.Span
                        "a patch is instructions for a merge, not a document — Yaml.merge applies it; there is nothing to render"
            | "yaml", _ ->
                // to yaml [D:yaml-seq-doc]: one document — a record is a
                // mapping, a seq a sequence document (the yaml analogue
                // of json's array), pairing with `from yaml T`/`from
                // yaml seq<T>`. A top-level seq<string * _> stays one
                // mapping document. The `---` stream is `to yaml stream`.
                // Generic records cross monomorphized [D:wire-generics]
                let wty, wenv = wireMono ctx env targ.Ty

                do!
                    match wty with
                    | TSeq(TTuple [ TStr; _ ]) -> yamlableOut toExpr.Span wenv Set.empty "" wty
                    | TSeq elem -> yamlableOut toExpr.Span wenv Set.empty "" elem
                    | ty -> yamlableOut toExpr.Span wenv Set.empty "" ty

                let! unions = unionWriteTable toExpr.Span wenv wty

                let guide = yamlShape toExpr.Span wenv Set.empty "" wty |> Result.toOption

                let tto = {
                    Kind = TETo("yaml", wireRenamesOf wenv wty, wireCodecsOf wenv wty, unions, false, guide)
                    Ty = TFun(targ.Ty, TSeq TStr)
                    Span = toExpr.Span
                }

                return {
                    Kind = TEPipe(targ, tto)
                    Ty = TSeq TStr
                    Span = expr.Span
                }
            | "xml", _ ->
                // XML is a read boundary only [D:from-xml] — there is no
                // 'to xml' (writing wants element order, namespaces, and a
                // schema weir does not carry)
                return!
                    err
                        toExpr.Span
                        "'to xml' does not exist — XML is read-only ('from xml T'); write JSON or YAML instead"
            | "table", _ ->
                // the table boundary reads only [D:from-table] — the REPL
                // already renders records as a table for display; a
                // machine-readable write is JSON or YAML
                return!
                    err
                        toExpr.Span
                        "'to table' does not exist — the table boundary is read-only ('from table T'); write JSON or YAML instead"
            | fmt, _ -> return! err toExpr.Span $"unknown output format '{fmt}'; available: json, jsonl, yaml"
        }
    | EPipe(arg, ({ Kind = ECmd _ } as cmdExpr)) ->
        result {
            let! targ = infer ctx env arg
            // a value-headed pipeline [D:value-headed-pipe] feeds the LHS as
            // stdin — seq<string> exactly, with the twin teachings pointing
            // each mistake at its fix (a command-headed chain never reaches
            // the error path: its LHS is already seq<string>)
            do!
                match resolve ctx targ.Ty with
                | TSeq TStr -> Ok()
                | TSeq(TVar _ as tv) -> bind ctx env arg.Span TStr tv
                | TVar _ -> bind ctx env arg.Span (TSeq TStr) targ.Ty
                | (TStr | TInt | TBool) as t ->
                    err
                        arg.Span
                        $"a value-headed pipeline feeds seq<string> as stdin; this is {formatTy t} — one line wraps as `[x]`; a value needs show/interpolate first"
                | TSeq elem ->
                    err
                        arg.Span
                        $"a value-headed pipeline feeds seq<string> as stdin; this is seq<{formatTy elem}> — map show or interpolate per element"
                | t -> err arg.Span $"a value-headed pipeline feeds seq<string> as stdin; this is {formatTy t}"

            let! tcmd = infer ctx env cmdExpr

            return {
                Kind = TEPipe(targ, tcmd)
                Ty = TSeq TStr
                Span = expr.Span
            }
        }
    | EPipe(arg,
            ({
                 Kind = EVar(("print" | "printerr" | "|print") as pname)
             } as printExpr)) when isPrintFamily env pname ->
        result {
            let! targ = infer ctx env arg
            let! argTy = printArgTy ctx env arg.Span targ.Ty

            let tprint = {
                Kind = TEVar pname
                Ty = TFun(argTy, TUnit)
                Span = printExpr.Span
            }

            return {
                Kind = TEPipe(targ, tprint)
                Ty = TUnit
                Span = expr.Span
            }
        }
    | EPipe(_,
            {
                Kind = EBinOp(op, _, _)
                Span = opSpan
            }) when op <> ">>" && op <> "<<" && op <> "<|" && op <> "!=" ->
        // Scalar operators yield values, never functions, so piping into
        // one is always wrong — and usually a precedence surprise.
        // Composition is the exception [D:composition-operators]:
        // `xs |> f >> g` pipes into the composed function, the F#
        // idiom, and takes the general arm.
        err
            opSpan
            $"'{op}' binds tighter than '|>', so this parses as xs |> (a {op} b); parenthesize the pipeline: (xs |> f) {op} value"
    // `from xml` also reads the document's bytes, which decode per its
    // BOM or declaration [D:xml-encoding]; text takes the general arm
    | EPipe(arg, ({ Kind = EFrom("xml", _, _, _) } as fromExpr)) ->
        result {
            let! targ = infer ctx env arg

            match resolve ctx targ.Ty with
            | TBytes ->
                let! tfrom = infer ctx env fromExpr

                match tfrom.Ty with
                | TFun(_, resultTy) ->
                    return {
                        Kind =
                            TEPipe(
                                targ,
                                {
                                    tfrom with
                                        Ty = TFun(TBytes, resultTy)
                                }
                            )
                        Ty = resultTy
                        Span = expr.Span
                    }
                | _ -> return! err fromExpr.Span "the right side of a pipe must be a function"
            | _ ->
                let head, args = spine fromExpr
                let! tfn = checkSpine ctx env head args (Some(targ.Ty, arg.Span))

                match resolve ctx tfn.Ty with
                | TFun(_, resultTy) ->
                    return {
                        Kind = TEPipe(targ, tfn)
                        Ty = resultTy
                        Span = expr.Span
                    }
                | _ -> return! err fromExpr.Span "the right side of a pipe must be a function"
        }
    // a lambda right of `|>` is checked against the piped type, so its
    // parameter is known inside the body — `cfg |> fun c -> c |> to
    // yaml` resolves as `to yaml cfg` does, not as a generic value
    | EPipe(arg, ({ Kind = ELambda _ | ELambdaPat _ } as fnExpr)) when
        (match fnExpr.Kind with
         | ELambda("()", _, _) -> false
         | _ -> true)
        ->
        result {
            let! targ = infer ctx env arg
            let res = TVar(freshName ctx "a")
            let! tfn = check ctx env fnExpr (TFun(targ.Ty, res))

            return {
                Kind = TEPipe(targ, tfn)
                Ty = res
                Span = expr.Span
            }
        }
    | EPipe(arg, fnExpr) ->
        result {
            let! targ = infer ctx env arg
            let head, args = spine fnExpr
            let! tfn = checkSpine ctx env head args (Some(targ.Ty, arg.Span))

            match resolve ctx tfn.Ty with
            | TFun(_, resultTy) ->
                return {
                    Kind = TEPipe(targ, tfn)
                    Ty = resultTy
                    Span = expr.Span
                }
            | _ -> return! err fnExpr.Span "the right side of a pipe must be a function"
        }
    // `T.create` [D:validated-types]: the type's own namespace — a module
    // member of the same name wins (the module was named first)
    | EField({ Kind = EVar n }, "create", _) when
        not (Map.containsKey n env.Modules && env.Modules[n].ContainsKey "create")
        && (validatedType env n).IsSome
        ->
        createRef env expr.Span n
    | EField({
                 Kind = EField({ Kind = EVar m }, n, _)
             },
             "create",
             _) when Map.containsKey m env.ModuleTypes && (validatedType env $"{m}.{n}").IsSome ->
        createRef env expr.Span $"{m}.{n}"
    | EField({ Kind = EVar n }, "create", _) when
        not (Map.containsKey n env.Modules)
        && (match resolveTypeName env n with
            | TypeAmbiguous qs -> qs |> List.forall (fun q -> (validatedType env q).IsSome)
            | _ -> false)
        ->
        match resolveTypeName env n with
        | TypeAmbiguous qs ->
            let spellings = quotedOrList (qs |> List.map (fun q -> q + ".create"))
            err expr.Span $"'{n}.create' is ambiguous — more than one import declares '{n}'; write {spellings}"
        | _ -> err expr.Span "unreachable: ambiguity checked in the guard"
    | EField({ Kind = EVar m }, field, _) when
        Map.containsKey m env.Modules
        && not (Map.containsKey m env.Values)
        && (validatedModuleCase env m field).IsSome
        ->
        err expr.Span (validatedCtorMsg $"{m}.{field}" $"{m}.{(validatedModuleCase env m field).Value}")
    | EField({ Kind = EVar m }, field, fieldSpan) when
        Map.containsKey m env.Modules
        && (not (Map.containsKey m env.Values)
            // a builtin module name that also names a builtin value
            // [D:repl-infer]: `Json` is both a module (Json.inferShape) and
            // HttpBody's `Json` constructor. That constructor has no record
            // fields, so `Json.field` could only ever be a module member —
            // resolve it there when the member exists. Scoped to the known
            // builtin overlaps (moduleValueOverlap) so a user union case
            // that shadows a module name still produces an ordinary
            // field-access error [D:desugar-capture].
            || (Set.contains m moduleValueOverlap && env.Modules[m] |> Map.containsKey field))
        ->
        result {
            let members = env.Modules[m]

            match Map.tryFind field members with
            | Some sch ->
                return {
                    Kind = TEVar $"{m}.{field}"
                    Ty = instantiate ctx expr.Span sch
                    Span = expr.Span
                }
            | None ->
                match retiredMember m field with
                | Some teach -> return! err fieldSpan $"'{m}.{field}' is retired: {teach}"
                | None when isSpecialMember m field ->
                    // load is a bespoke arm, not a member — reaching here
                    // means the shape missed it: a space inside the type
                    // name (two arguments), extra arguments, or none
                    // [PLAN-diagnostics-arc A1]
                    let union = if m = "Args" then " or union" else ""

                    return! err fieldSpan $"{m}.load takes ONE record{union} type name, e.g. {m}.load Config"
                | None ->
                    // an unsigned member exists but does not export
                    // [D:module-signatures] — teach the migration (the
                    // signature to add, its inferred type included);
                    // did-you-mean stays scoped to the signed members
                    match env.ModulePrivate |> Map.tryFind m |> Option.bind (Map.tryFind field) with
                    | Some priv ->
                        let hint = didYouMean field (Map.keys members)

                        let rec hasRow t =
                            match t with
                            | TRowVar _ -> true
                            | TFun(a, b) -> hasRow a || hasRow b
                            | TSeq e -> hasRow e
                            | TTuple ts -> List.exists hasRow ts
                            | TNamed(_, args) -> List.exists hasRow args
                            | _ -> false

                        // a row type has no signature spelling: suggesting
                        // one would hand over a line that does not parse
                        if hasRow priv.Ty then
                            return!
                                err
                                    fieldSpan
                                    ($"'{m}.{field}' is module-private (no signature) — to export it, declare the record it reads as a type and add `let {field} : …` naming it in the module "
                                     + $"(its inferred type reads fields of any record, which a signature cannot spell){hint}")
                        else
                            return!
                                err
                                    fieldSpan
                                    ($"'{m}.{field}' is module-private (no signature) — add `let {field} : {formatEchoTy priv.Ty}` "
                                     + $"in the module to export it{hint}")
                    | None ->
                        let hint = didYouMean field (Map.keys members)
                        return! err fieldSpan $"module {m} has no member '{field}'{hint}"
        }
    // `M.f` where M is neither a value nor a module: a qualifier names a
    // module, so suggest module names — not the prelude's constructors
    // (`M` -> `Ok`) — and say how to bring one in [D:module-qualifier]
    | EField({ Kind = EVar m } as target, _, _) when
        m.Length > 0
        && System.Char.IsUpper m[0]
        && not (Map.containsKey m env.Values)
        && not (Map.containsKey m env.Modules)
        ->
        // the existing teachings come first (List/String -> Seq/Str, the
        // Exit -> exit rename); this answers only where the generic message
        // has no suggestion, or would suggest a mere constructor
        match infer ctx env target with
        | Error e when not (e.Message.StartsWith $"unbound variable '{m}'") -> Error e
        | Error e when
            (let marker = "Did you mean '"
             let i = e.Message.IndexOf marker

             i >= 0
             && (let rest = e.Message.Substring(i + marker.Length)
                 let suggested = rest.Substring(0, max 0 (rest.IndexOf '\''))
                 List.isEmpty (ctorOwners env suggested)))
            ->
            Error e
        | _ -> err target.Span (noModuleMsg env m)
    | EField(target, field, fieldSpan) ->
        result {
            let! ttarget = infer ctx env target

            match resolve ctx ttarget.Ty with
            | TNamed(typeName, targs) ->
                match typeDefFor env typeName with
                | Some(Record def) ->
                    match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
                    | Some(_, fieldTy) ->
                        return {
                            Kind = TEField(ttarget, field)
                            Ty = substParams def.Params targs fieldTy
                            Span = expr.Span
                        }
                    | None ->
                        match retiredField typeName field with
                        | Some teach -> return! err fieldSpan $"'{field}' is retired: {teach}"
                        | None ->
                            let hint = didYouMean field (List.map fst def.Fields)
                            return! err fieldSpan $"{typeName} has no field '{field}'{hint}"
                | Some(Union _) ->
                    return! err fieldSpan $"{typeName} is a union; match on it instead of accessing fields"
                | None when List.contains typeName deflessBuiltinNominals ->
                    return! err fieldSpan (opaqueNoFields typeName)
                | None -> return! err target.Span $"unknown type '{typeName}'"
            | TVar v ->
                let r = freshName ctx "r"
                ctx.Subst[v] <- (TRowVar(r, []))
                let fieldTy = TVar(freshName ctx "a")
                ctx.Rows[r] <- (Map [ field, (fieldTy, fieldSpan) ])

                return {
                    Kind = TEField(ttarget, field)
                    Ty = fieldTy
                    Span = expr.Span
                }
            | TRowVar(r, _) ->
                let existing = rowsOf ctx r

                match Map.tryFind field existing with
                | Some(fieldTy, _) ->
                    return {
                        Kind = TEField(ttarget, field)
                        Ty = fieldTy
                        Span = expr.Span
                    }
                | None ->
                    let fieldTy = TVar(freshName ctx "a")
                    ctx.Rows[r] <- (Map.add field (fieldTy, fieldSpan) existing)

                    return {
                        Kind = TEField(ttarget, field)
                        Ty = fieldTy
                        Span = expr.Span
                    }
            | ty -> return! err target.Span $"only records have fields; this expression has type {formatTy ty}"
        }
    | ESlice(target, lo, hi, loEnd, hiEnd) ->
        // a type-directed node [D:range-slicing]: the target's
        // resolved type picks substring vs subsequence. Bounds are
        // ints; an unconstrained target defaults to a sequence (as `x[i]`
        // does — `|seqItem` unifies to seq), so a string slice needs a
        // known-string target and `fun xs -> xs[a..b]` is seq-typed.
        // A `^`-bound is from-the-end [D:range-slicing]; the flag rides to
        // eval, which resolves it against the length (forcing a seq).
        result {
            let! ttarget = infer ctx env target

            let inferBound (o: Expr option) =
                match o with
                | None -> Ok None
                | Some e -> check ctx env e TInt |> Result.map Some

            let! tlo = inferBound lo
            let! thi = inferBound hi

            match resolve ctx ttarget.Ty with
            | TStr ->
                return {
                    Kind = TESlice(ttarget, tlo, thi, SliceStr, loEnd, hiEnd)
                    Ty = TStr
                    Span = expr.Span
                }
            | TBytes ->
                return {
                    Kind = TESlice(ttarget, tlo, thi, SliceBytes, loEnd, hiEnd)
                    Ty = TBytes
                    Span = expr.Span
                }
            | TSeq elem ->
                return {
                    Kind = TESlice(ttarget, tlo, thi, SliceSeq, loEnd, hiEnd)
                    Ty = TSeq elem
                    Span = expr.Span
                }
            | TVar _ ->
                let elem = TVar(freshName ctx "a")
                do! bind ctx env target.Span ttarget.Ty (TSeq elem)

                return {
                    Kind = TESlice(ttarget, tlo, thi, SliceSeq, loEnd, hiEnd)
                    Ty = TSeq elem
                    Span = expr.Span
                }
            | other ->
                return!
                    err
                        target.Span
                        $"a slice `x[a..b]` works on a string, a sequence, or Bytes; this expression is {formatTy other}"
        }
    | EOpValue op ->
        // desugar to exactly `fun a b -> a op b` [D:operator-values]:
        // every typing question — overload-by-context, int defaulting,
        // the Eq/Ord class solving — inherits the infix answers
        // verbatim, and eval sees an ordinary lambda.
        infer ctx env (opValueLambda op expr.Span)
    // operators weir refuses, taught before either side is inferred so an
    // unbound name inside cannot speak first [D:left-to-right-ops]
    | EBinOp("<<", _, _) -> err expr.Span "weir composes left to right — `f << g` is `g >> f`"
    | EBinOp("<|", _, _) -> err expr.Span "weir has no `<|` — write `f (x)`, or pipe `x |> f`"
    | EBinOp("!=", _, _) -> err expr.Span "weir's inequality is `<>` (equality is `==`)"
    | EBinOp(op, left, right) ->
        result {
            let! tleft = infer ctx env left

            // composition rejects a non-function LHS before the RHS is
            // inferred: bash-append lines (`cmd >> file`) usually carry
            // an unbound RHS, and the redirect hint must beat the
            // "unbound variable" error. A pipe on the left is the
            // shared-precedence gotcha (`xs |> f >> g` is
            // `(xs |> f) >> g`, F#'s parse) and gets the parenthesize
            // hint instead [D:composition-operators]
            do!
                match op, resolve ctx tleft.Ty with
                | ">>", (TFun _ | TVar _) -> Ok()
                | ">>", ty ->
                    match left.Kind with
                    | EPipe _ ->
                        err
                            left.Span
                            ($"'{op}' and '|>' share precedence, so this parses as (xs |> f) {op} g; "
                             + $"parenthesize the composition: xs |> (f {op} g)")
                    | _ ->
                        err
                            left.Span
                            ($"'>>' composes functions, and this expression has type {formatTy ty}; "
                             + "to append command output to a file, pipe it: cmd |> File.append \"out.txt\"")
                | _ -> Ok()

            // `cd /work` in an expression parses as `cd / work`: the path
            // '/work' is a division whose RHS 'work' is an unbound variable.
            // Append a quote-the-path hint to that unbound error, but only
            // when a space precedes the '/' (path shape, not glued `a/b`
            // arithmetic), so a genuine `total/coutn` typo keeps its plain
            // did-you-mean [D:unquoted-path-teach]
            let! tright =
                infer ctx env right
                |> Result.mapError (fun e ->
                    match right.Kind with
                    | EVar nm when
                        op = "/"
                        && right.Span.Start.Col - left.Span.End.Col >= 2
                        && e.Message.StartsWith "unbound variable"
                          ->
                          {
                              e with
                                  Message =
                                      e.Message
                                      + $" — if '/{nm}' is a filesystem path, quote it (cd \"/{nm}\"); an unquoted path is an argv word only in a command, not an expression"
                          }
                    | _ -> e)

            let! ty = typeBinOp ctx env expr.Span op tleft tright

            return {
                Kind = TEBinOp(op, tleft, tright)
                Ty = ty
                Span = expr.Span
            }
        }
    | EUpdate(source, updates) ->
        // copy-and-update [D:record-update]. Result type is the
        // source's type — nominal stays nominal (update never adds
        // fields), a row source keeps its own row variable (identity,
        // not a fresh row), which is what lets a row-typed updater
        // generalize. Paths walk nested records (the F# 8 I.X sugar);
        // rows demand a field per hop, mirroring EField.
        result {
            do! commaBetweenFields (updates |> List.map (fun (path, v) -> fst (List.head path), expr.Span, v))
            let! tsource = infer ctx env source

            match firstDup (updates |> List.map (fun (path, _) -> fst (List.head path))) with
            | Some dup -> return! err expr.Span $"duplicate update of field '{dup}'"
            | None ->

                // subjectSpan = the expression whose type must be a record:
                // the source at the first hop, the previous field at deeper
                // ones — the non-record error blames the non-record, never
                // the field being assigned [D:update-span]
                let rec updOne
                    (subjectSpan: Span)
                    (ty: Ty)
                    (path: (string * Span) list)
                    (value: Expr)
                    : Result<TypedExpr, TypeError> =
                    match path with
                    | [] -> infer ctx env value
                    | (field, fieldSpan) :: rest ->
                        match resolve ctx ty with
                        // a receipt is read-only [D:receipts-read-only]; a
                        // row-typed source meets one only at its call, so
                        // eval's TEUpdate arm repeats this refusal
                        | TNamed(typeName, _) when isReceipt typeName -> err subjectSpan (receiptUpdateMsg typeName)
                        | TNamed(typeName, targs) ->
                            match typeDefFor env typeName with
                            | Some(Record def) ->
                                match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
                                | Some(_, declared) ->
                                    let fieldTy = substParams def.Params targs declared

                                    if List.isEmpty rest then
                                        check ctx env value fieldTy
                                    else
                                        updOne fieldSpan fieldTy rest value
                                | None ->
                                    let hint = didYouMean field (List.map fst def.Fields)

                                    err
                                        fieldSpan
                                        $"record update cannot add fields: {typeName} has no field '{field}'{hint}"
                            | Some(Union _) -> err subjectSpan $"{typeName} is a union; only records update"
                            | None when List.contains typeName deflessBuiltinNominals ->
                                err subjectSpan (opaqueNoFields typeName)
                            | None -> err subjectSpan $"unknown type '{typeName}'"
                        | TVar v ->
                            let r = freshName ctx "r"
                            ctx.Subst[v] <- (TRowVar(r, []))
                            updOne subjectSpan (TRowVar(r, [])) path value
                        | TRowVar(r, _) ->
                            let existing = rowsOf ctx r

                            let fieldTy =
                                match Map.tryFind field existing with
                                | Some(t, _) -> t
                                | None ->
                                    let t = TVar(freshName ctx "a")
                                    ctx.Rows[r] <- (Map.add field (t, fieldSpan) existing)
                                    t

                            if List.isEmpty rest then
                                check ctx env value fieldTy
                            else
                                updOne fieldSpan fieldTy rest value
                        | TFun _ as fty ->
                            // a partially-applied constructor in update
                            // position is unambiguous — name the repair
                            err
                                subjectSpan
                                $"only records have updatable fields; this expression is a function ({formatTy fty}) — apply it first"
                        | ty ->
                            err
                                subjectSpan
                                $"only records have updatable fields; this expression has type {formatTy ty}"

                let! tupdates =
                    updates
                    |> List.fold
                        (fun acc (path, value) ->
                            acc
                            |> Result.bind (fun ts ->
                                updOne source.Span tsource.Ty path value
                                |> Result.map (fun tv -> (path |> List.map fst, tv) :: ts)))
                        (Ok [])

                return {
                    Kind = TEUpdate(tsource, List.rev tupdates)
                    Ty = tsource.Ty
                    Span = expr.Span
                }
        }
    | ERecord fields ->
        result {
            do! commaBetweenFields fields

            match firstDup (fields |> List.map (fun (n, _, _) -> n)) with
            | Some dup ->
                let _, dupSpan, _ = fields |> List.findBack (fun (n, _, _) -> n = dup)
                return! err dupSpan $"duplicate field '{dup}'"
            | None ->
                let names = fields |> List.map (fun (n, _, _) -> n) |> Set.ofList

                // registered anonymous defs (adapter shapes, drained
                // literals) never join the candidate set — the anon
                // form has its own spelling [D:anon-literals], and a
                // same-shaped hidden def must not make a declared
                // record look ambiguous
                // a receipt is never a candidate [D:receipts-read-only]: a
                // user record of the same shape wins outright, and a literal
                // of the receipt's shape (its derived fields optional)
                // gets the provenance teaching below
                let candidates = (typesIndex env).RecordsWithFields names

                let receiptShaped =
                    receipts
                    |> Map.toList
                    |> List.tryFind (fun (tn, r) ->
                        match Map.tryFind tn env.Types with
                        | Some(Record def) ->
                            let all = def.Fields |> List.map fst |> Set.ofList

                            Set.isSubset names all
                            && Set.isSubset (Set.difference all (Set.ofList r.Derived)) names
                        | _ -> false)

                match candidates with
                | [] when receiptShaped.IsSome -> return! err expr.Span (receiptLiteralMsg (fst receiptShaped.Value))
                | [ key, def ] ->
                    let targs = def.Params |> List.map (fun _ -> TVar(freshName ctx "a"))

                    let checkField (name: string, _: Span, value: Expr) =
                        let declaredTy =
                            def.Fields
                            |> List.find (fun (f, _) -> f = name)
                            |> snd
                            |> substParams def.Params targs

                        check ctx env value declaredTy |> Result.map (fun tv -> name, tv)

                    let! tfields =
                        fields
                        |> List.fold
                            (fun acc f -> acc |> Result.bind (fun ts -> checkField f |> Result.map (fun t -> t :: ts)))
                            (Ok [])

                    return {
                        Kind = TERecord(def.Name, List.rev tfields)
                        Ty = TNamed(key, targs)
                        Span = expr.Span
                    }
                | [] ->
                    let fieldList = String.concat ", " (Set.toList names)
                    return! err expr.Span $"no declared record has exactly the fields: {fieldList}"
                | many ->
                    let nameList = many |> List.map fst |> String.concat ", "
                    return! err expr.Span $"ambiguous record literal; it matches: {nameList}"
        }
    // the anonymous literal [D:anon-literals]: fields infer, the
    // canonical name is minted here (the parse-time push cannot — it
    // sees values, not types) and registered both mid-statement
    // (AnonLitDefs) and for the next statement's drain. The typed
    // node is TERecord — eval/writers/LSP gain no arms.
    | EAnonRecord fields ->
        result {
            do! commaBetweenFields fields

            match firstDup (fields |> List.map (fun (n, _, _) -> n)) with
            | Some dup ->
                let _, dupSpan, _ = fields |> List.findBack (fun (n, _, _) -> n = dup)
                return! err dupSpan $"duplicate field '{dup}'"
            | None ->
                let! tfields =
                    fields
                    |> List.fold
                        (fun acc (n, s, v) ->
                            acc
                            |> Result.bind (fun ts -> infer ctx env v |> Result.map (fun tv -> (n, s, tv) :: ts)))
                        (Ok [])
                    |> Result.map List.rev

                // ground types only: the canonical name is the type,
                // so it cannot carry an inference variable — the
                // named divergence anon-literal-mono (F# admits the
                // generic form; declared records serve it here)
                let fieldTys = tfields |> List.map (fun (n, s, tv) -> n, s, finalTy ctx tv.Ty)

                match fieldTys |> List.tryFind (fun (_, _, ty) -> not (Set.isEmpty (tyVars ty))) with
                | Some(n, s, _) ->
                    return!
                        err
                            s
                            $"the type of field '{n}' is not fully known at the literal — an anonymous record's name IS its type, so every field needs a concrete type here; annotate the value, or declare a record (declared records may be generic)"
                | None ->
                    let flds = fieldTys |> List.map (fun (n, _, ty) -> n, ty)
                    let name = anonRecordName flds

                    env.AnonLitDefs[name] <-
                        Record {
                            Name = name
                            Params = []
                            Fields = List.sortBy fst flds
                            Attrs = Map.empty
                            OrdKey = []
                            Docs = Map.empty
                        }

                    Types.pushAnonDef name flds

                    return {
                        Kind = TERecord(name, tfields |> List.map (fun (n, _, tv) -> n, tv))
                        Ty = TNamed(name, [])
                        Span = expr.Span
                    }
        }
    | EFrom(fmt, shape, seqOf, streamOf) ->
        result {
            let! shape =
                match shape with
                | Some sh -> canonShape env expr.Span sh |> Result.map Some
                | None -> Ok None

            // resolve the slot's payload to a name [D:anon-records]: a
            // declared name passes through; an anonymous shape resolves
            // to its canonical name, whose def withAnonDefs registered
            // at typecheck entry — one lookup path serves both, so the
            // anonymous form validates exactly as a declared record does.
            // Map<string, T> in the slot [D:map-string]: peel the wrap
            // first — the inner shape then resolves through the same
            // name path a bare slot does, so anon composition is free
            let! inner, mapOf =
                match shape with
                | Some(FromMap(TStr, sh)) ->
                    // a value type naming a declared type (or an unknown
                    // uppercase name) resolves through the record path
                    // below; the built-in type names stay value types
                    // [D:map-values]
                    match sh with
                    | FromTy(TNamed(n, args)) when
                        not (List.contains n ("Option" :: "Map" :: "Yaml" :: deflessBuiltinNominals))
                        ->
                        Ok(Some(if args.IsEmpty then FromName n else FromApp(n, args)), true)
                    | sh -> Ok(Some sh, true)
                | Some(FromMap(k, _)) -> err expr.Span (mapKeyTeach k)
                | sh -> Ok(sh, false)

            let! tyName =
                match inner with
                | None -> Ok None
                // the label the wrap-refusal messages below print; the
                // value-type read is its own arm [D:map-values]
                | Some(FromTy ty) -> Ok(Some(formatTy ty))
                | Some(FromName n) -> Ok(Some n)
                | Some(FromApp(n, args)) ->
                    // a generic record instantiated in the slot
                    // [D:wire-generics]: json/jsonl/yaml read it through
                    // its monomorphic instance (wireMono's key)
                    match typeDefFor env n with
                    | _ when not (List.contains fmt [ "json"; "jsonl"; "yaml" ]) ->
                        err expr.Span $"'from {fmt}' needs a monomorphic record; '{n}' is generic"
                    | Some(Record def) when def.Params.Length = args.Length ->
                        match args |> List.tryFind (fun a -> not (Set.isEmpty (tyVars a))) with
                        | Some a ->
                            err
                                expr.Span
                                $"the adapter slot needs a concrete type — '{formatTy a}' in {formatTy (TNamed(n, args))} is a type variable"
                        | None -> Ok(Some(formatTy (TNamed(n, args))))
                    | Some(Record def) when def.Params.IsEmpty -> err expr.Span $"'{n}' takes no type arguments"
                    | Some(Record def) ->
                        err expr.Span $"'{n}' takes {def.Params.Length} type argument(s), got {args.Length}"
                    | Some(Union _) -> err expr.Span $"'{n}' is a union; a generic union does not cross the wire"
                    | None -> err expr.Span (unknownTypeMsg env n)
                // the parser reads a nested Map as a value type
                // (FromTy) [D:map-values]
                | Some(FromMap _) -> failwith "unreachable: a nested Map< > in the slot parses as FromTy"
                | Some(FromAnon fields) ->
                    match firstDup (fields |> List.map fst) with
                    | Some d -> err expr.Span $"duplicate field '{d}' in the anonymous record"
                    | None -> Ok(Some(anonRecordName fields))

            // the type the slot names, as the program sees it; `name`
            // below is the wire-local key [D:wire-generics]
            let slotTy, env =
                match inner with
                | Some(FromTy ty) -> ty, env
                | Some(FromApp(n, args)) ->
                    let _, wenv = wireMono ctx env (TNamed(n, args))
                    TNamed(n, args), wenv
                // a monomorphic record may hold an instance in a field
                | _ when List.contains fmt [ "json"; "jsonl"; "yaml" ] && tyName.IsSome ->
                    let ty = TNamed(tyName.Value, [])
                    ty, snd (wireMono ctx env ty)
                | _ -> TNamed(defaultArg tyName "", []), env

            // per-adapter admitted set for seq<Name> [D:from-json-seq]
            // [D:yaml-seq]: json and yaml admit the wrap (one document,
            // top level as declared); jsonl refuses — one object per line
            // is already plural in the way the wrap means
            do!
                // the stream cardinality word is YAML's [D:wire-unions] —
                // json's stream form is the thing literally named jsonl
                if fmt = "toml" && (seqOf || mapOf || streamOf) then
                    // a TOML document is one table [D:from-toml]
                    let n = defaultArg tyName "T"
                    err expr.Span $"'from toml' reads one document, and its top level is a table — write from toml {n}"
                elif streamOf && fmt = "table" then
                    let n = defaultArg tyName "Row"
                    err expr.Span $"'from table stream' does not exist — a table is already rows; write from table {n}"
                elif streamOf && fmt <> "yaml" then
                    let n = defaultArg tyName "T"

                    err
                        expr.Span
                        $"'from {fmt} stream' does not exist — NDJSON is 'from jsonl {n}'; one array document is 'from json seq<{n}>'"
                elif streamOf && mapOf then
                    err expr.Span "'from yaml stream' reads documents — Map< > has no place in the stream slot"
                elif seqOf && fmt = "jsonl" then
                    let n = defaultArg tyName "T"
                    err expr.Span $"'from jsonl T' already yields seq<T> — write from jsonl {n}"
                elif mapOf && fmt = "jsonl" then
                    // one object per line vs one keyed object — the two
                    // shapes cannot both be the top level [D:map-string]
                    let n = defaultArg tyName "T"

                    err
                        expr.Span
                        $"'from jsonl' reads one object per line; a Map is ONE object — write from json Map<string, {n}>"
                elif mapOf && fmt = "yaml" then
                    let n = defaultArg tyName "T"
                    err expr.Span $"'from yaml' does not take Map<string, {n}> yet — 'from json' does"
                elif fmt = "table" && seqOf then
                    // matching jsonl: rows are already plural
                    let n = defaultArg tyName "Row"
                    err expr.Span $"'from table {n}' already yields seq<{n}> — write from table {n}"
                elif fmt = "table" && mapOf then
                    let n = defaultArg tyName "Row"
                    err expr.Span $"a table reads rows, not a keyed object — write from table {n} (it yields seq<{n}>)"
                elif fmt = "xml" && (seqOf || mapOf || streamOf) then
                    // XML has a single root element [D:from-xml] — no seq /
                    // stream / Map wrap; a repeated child is a seq< > field
                    let n = defaultArg tyName "T"

                    err
                        expr.Span
                        $"'from xml' reads one document's root element — write from xml {n} (XML has a single root)"
                else
                    Ok()

            match fmt, tyName with
            // from json Map<string, V> with a non-record V
            // [D:map-values]: V is admitted iff a Map<string, V> field
            // would be — the field law, run on the value type; the
            // wrap refusals above already turned every other format
            | "json", _ when
                (match inner with
                 | Some(FromTy _) -> true
                 | _ -> false)
                ->
                let wireTy, wenv = wireMono ctx env slotTy
                do! jsonAdmitted true expr.Span wenv [] "" wireTy

                return {
                    Kind =
                        TEFrom(
                            fmt,
                            TopValue wireTy,
                            jsonDefsClosure wenv Map.empty wireTy,
                            jsonUnionsClosure wenv Map.empty wireTy,
                            false,
                            true
                        )
                    Ty = TFun(TSeq TStr, TNamed("Map", [ TStr; slotTy ]))
                    Span = expr.Span
                }
            // from json T reads one document -> T (pretty-printed bodies
            // pipe straight in); from json seq<T> reads one array
            // document -> seq<T> — the declared type decides what the top
            // level must be, never the input [D:from-json-seq]; from
            // jsonl T reads one document per element -> seq<T>
            // [D:from-jsonl]. The plain name carries the common case;
            // nothing sniffs.
            | ("json" | "jsonl"), Some name ->
                let resultTy =
                    if mapOf then TNamed("Map", [ TStr; slotTy ])
                    elif fmt = "json" && not seqOf then slotTy
                    else TSeq slotTy

                match typeDefFor env name with
                | Some(Record def) when def.Params.IsEmpty ->
                    do! jsonAdmitted true expr.Span env [] "" (TNamed(name, []))

                    let defs =
                        def.Fields
                        |> List.fold (fun a (_, fty) -> jsonDefsClosure env a fty) (Map.ofList [ def.Name, def ])

                    let udefs =
                        def.Fields
                        |> List.fold (fun a (_, fty) -> jsonUnionsClosure env a fty) Map.empty

                    return {
                        Kind = TEFrom(fmt, TopRec def, defs, udefs, seqOf, mapOf)
                        Ty = TFun(TSeq TStr, resultTy)
                        Span = expr.Span
                    }
                // a tagged union in the slot [D:wire-unions]: each document
                // dispatches on the tag — `from jsonl KDoc` reads mixed
                // NDJSON, `from json seq<KDoc>` a mixed array
                | Some(Union udef) when udef.Tag.IsSome ->
                    do! jsonableElem true expr.Span env (TNamed(name, []))

                    let defs = jsonDefsClosure env Map.empty (TNamed(name, []))
                    let udefs = jsonUnionsClosure env Map.empty (TNamed(name, []))

                    return {
                        Kind = TEFrom(fmt, TopUnion udef, defs, udefs, seqOf, mapOf)
                        Ty = TFun(TSeq TStr, resultTy)
                        Span = expr.Span
                    }
                // a validated type in the slot reads its payload's
                // document, then runs `create` [D:validated-types]
                | Some(Union udef) when udef.Validated.IsSome ->
                    let ty = TNamed(name, [])
                    do! jsonAdmitted true expr.Span env [] "" ty

                    return {
                        Kind =
                            TEFrom(
                                fmt,
                                TopValue ty,
                                jsonDefsClosure env Map.empty ty,
                                jsonUnionsClosure env Map.empty ty,
                                seqOf,
                                mapOf
                            )
                        Ty = TFun(TSeq TStr, resultTy)
                        Span = expr.Span
                    }
                | Some(Record def) ->
                    let args = def.Params |> List.map (fun _ -> "int") |> String.concat ", "

                    return!
                        err
                            expr.Span
                            $"'{name}' is generic — name the instance the document holds, e.g. from {fmt} {name}<{args}>"
                | Some(Union _) ->
                    return!
                        err
                            expr.Span
                            $"'{name}' is an untagged union — a union crosses the wire with [<Tag \"field\">] on its declaration"
                | None -> return! err expr.Span (unknownTypeMsg env name)
            | ("json" | "jsonl"), None ->
                let seqHint =
                    if fmt = "json" then
                        " — or seq<FileRow> for a top-level array, Map<string, FileRow> for an ID-keyed object, or an inline shape {| ip: string |}"
                    else
                        ""

                return! err expr.Span $"'from {fmt}' needs a record name, e.g. from {fmt} FileRow{seqHint}"
            | "yaml", Some name ->
                // from yaml T reads one document -> T (a mapping at the
                // top); from yaml seq<T> reads one sequence document ->
                // seq<T> [D:yaml-seq]; from yaml stream T reads N `---`
                // documents, each as T -> seq<T> [D:wire-unions] — the
                // heterogeneous bundle is stream over a tagged union,
                // two orthogonal spellings composed. The declared type
                // decides the top level, never the input.
                match typeDefFor env name with
                | Some(Record def) when def.Params.IsEmpty ->
                    let wirePerDoc = if seqOf then TSeq(TNamed(name, [])) else TNamed(name, [])
                    let perDoc = if seqOf then TSeq slotTy else slotTy
                    let declared = if streamOf then TSeq perDoc else perDoc

                    let! shape = yamlShape expr.Span env Set.empty "" wirePerDoc

                    return {
                        Kind = TEFromYaml(name, shape, streamOf)
                        Ty = TFun(TSeq TStr, declared)
                        Span = expr.Span
                    }
                // a tagged union in the slot [D:wire-unions] — yamlShape
                // builds the dispatch node; untagged keeps the teaching. A
                // validated type reads its payload [D:validated-types]
                | Some(Union udef) when udef.Tag.IsSome || udef.Validated.IsSome ->
                    let perDoc = if seqOf then TSeq(TNamed(name, [])) else TNamed(name, [])
                    let declared = if streamOf then TSeq perDoc else perDoc

                    let! shape = yamlShape expr.Span env Set.empty "" perDoc

                    return {
                        Kind = TEFromYaml(name, shape, streamOf)
                        Ty = TFun(TSeq TStr, declared)
                        Span = expr.Span
                    }
                | Some(Record def) ->
                    let args = def.Params |> List.map (fun _ -> "int") |> String.concat ", "

                    return!
                        err
                            expr.Span
                            $"'{name}' is generic — name the instance the document holds, e.g. from yaml {name}<{args}>"
                | Some(Union _) ->
                    return!
                        err
                            expr.Span
                            $"'{name}' is an untagged union — a union crosses the wire with [<Tag \"field\">] on its declaration"
                | None -> return! err expr.Span (unknownTypeMsg env name)
            | "yaml", None ->
                return!
                    err
                        expr.Span
                        "'from yaml' needs a record name, e.g. from yaml Deployment — or seq<Deployment> for a top-level sequence, or stream Deployment for '---' documents"
            // from toml T reads one document -> T [D:from-toml]: the yaml
            // shape and binder, over the toml parser's node tree
            | "toml", Some name ->
                // the yaml shape law, its messages naming the toml boundary
                let tomlShape ty =
                    yamlShape expr.Span env Set.empty "" ty
                    |> Result.mapError (fun e -> {
                        e with
                            Message = e.Message.Replace("yaml", "toml")
                    })

                match typeDefFor env name with
                | Some(Record def) when def.Params.IsEmpty ->
                    let! shape = tomlShape (TNamed(name, []))

                    return {
                        Kind = TEFromToml(name, shape)
                        Ty = TFun(TSeq TStr, TNamed(name, []))
                        Span = expr.Span
                    }
                | Some(Union udef) when udef.Tag.IsSome || udef.Validated.IsSome ->
                    let! shape = tomlShape (TNamed(name, []))

                    return {
                        Kind = TEFromToml(name, shape)
                        Ty = TFun(TSeq TStr, TNamed(name, []))
                        Span = expr.Span
                    }
                | Some(Record _) -> return! err expr.Span $"'from toml' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) ->
                    return!
                        err
                            expr.Span
                            $"'{name}' is an untagged union — a union crosses the wire with [<Tag \"field\">] on its declaration"
                | None -> return! err expr.Span (unknownTypeMsg env name)
            | "toml", None ->
                return!
                    err
                        expr.Span
                        "'from toml' needs a record name, e.g. from toml Cargo — or an inline shape {| package: {| version: string |} |}"
            // from xml T reads one document's root element -> T
            // [D:from-xml]: field names match child elements, [<Attr>]
            // reads an attribute, [<Elem "X">] names a repeated child, a
            // record is a nested element. Read-only, monomorphic records
            // only; no union, no seq/stream/Map top level (one root).
            | "xml", Some name ->
                match typeDefFor env name with
                | Some(Record def) when def.Params.IsEmpty ->
                    do! xmlableRecord expr.Span env def

                    let defs =
                        def.Fields
                        |> List.fold (fun a (_, fty) -> jsonDefsClosure env a fty) (Map.ofList [ def.Name, def ])

                    return {
                        Kind = TEFrom("xml", TopRec def, defs, Map.empty, false, false)
                        Ty = TFun(TSeq TStr, TNamed(name, []))
                        Span = expr.Span
                    }
                | Some(Record _) -> return! err expr.Span $"'from xml' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) ->
                    return!
                        err
                            expr.Span
                            $"'from xml' needs a record; '{name}' is a union — XML has no tagged-union reading"
                | None -> return! err expr.Span (unknownTypeMsg env name)
            | "xml", None -> return! err expr.Span "'from xml' needs a record name, e.g. from xml Project"
            // from table T reads aligned column output (kubectl/docker
            // style) -> seq<T> [D:from-table]: the first non-blank line
            // is the header, columns slice by header offsets, each field
            // reads its column's cell (Wire matches the raw header
            // verbatim; otherwise the normalized names match). Flat rows
            // only; rows are already plural, so no seq/stream/Map wrap.
            | "table", Some name ->
                match typeDefFor env name with
                | Some(Record def) when def.Params.IsEmpty ->
                    do! tableableRecord expr.Span def

                    return {
                        Kind = TEFrom("table", TopRec def, Map.ofList [ def.Name, def ], Map.empty, false, false)
                        Ty = TFun(TSeq TStr, TSeq(TNamed(name, [])))
                        Span = expr.Span
                    }
                | Some(Record _) ->
                    return! err expr.Span $"'from table' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) ->
                    return!
                        err
                            expr.Span
                            $"'from table' needs a row record; '{name}' is a union — a table row has no tag convention"
                | None -> return! err expr.Span (unknownTypeMsg env name)
            | "table", None ->
                return! err expr.Span "'from table' needs a row record name, e.g. from table Pod — it yields seq<Pod>"
            | fmt, _ -> return! err expr.Span $"unknown format '{fmt}'; available: json, jsonl, yaml, toml, xml, table"
        }
    | ETo _ -> err expr.Span "'to json' / 'to yaml' can only be used as a pipe stage, e.g. xs |> to json"
    | EYaml(tpl, schema, patchBy) ->
        result {
            // patch districts [D:yaml-nodes]: typed YamlPatch, tombstones
            // admitted; schema= refuses (a patch is partial — required
            // fields legitimately absent, whole-document validation
            // cannot apply). A plain district refuses tombstones at
            // their span, naming the kind that admits them.
            do!
                match patchBy, schema with
                | Some _, Some _ ->
                    err expr.Span "a patch is partial — schema= validates whole documents; drop one of patch/schema="
                | None, _ ->
                    match firstTombstone tpl with
                    | Some sp -> err sp "a tombstone ($-) means something only inside a `yaml patch` district"
                    | None -> Ok()
                | _ -> Ok()

            let! ttpl = checkYamlTpl ctx env tpl

            return {
                Kind = TEYaml(ttpl, schema, patchBy)
                Ty = TNamed((if patchBy.IsSome then "YamlPatch" else "Yaml"), [])
                Span = expr.Span
            }
        }
    | EDynProg(display, he) ->
        // the reified spelling of a dynamic head [D:dynamic-head]: type
        // the value string-exactly, then erase — the reifier builtin
        // receives an ordinary string argument
        checkDynHead ctx env display he
    | ECmd(head, args, envO) ->
        result {
            // $@ demands seq<string> exactly [D:argv-splat]; the twin
            // teachings point each mistake at its honest spelling
            let checkArg (a: Expr) =
                match a.Kind with
                | ESplat inner ->
                    result {
                        let! tinner = infer ctx env inner

                        match resolve ctx tinner.Ty with
                        | TVar _ ->
                            do! bind ctx env a.Span (TSeq TStr) tinner.Ty

                            return {
                                Kind = TESplat tinner
                                Ty = TSeq TStr
                                Span = a.Span
                            }
                        | TSeq TStr ->
                            return {
                                Kind = TESplat tinner
                                Ty = TSeq TStr
                                Span = a.Span
                            }
                        | TSeq(TVar _ as tv) ->
                            // an unanchored element ([] and friends)
                            // resolves to string at the splice
                            do! bind ctx env a.Span TStr tv

                            return {
                                Kind = TESplat tinner
                                Ty = TSeq TStr
                                Span = a.Span
                            }
                        | TSeq t ->
                            return!
                                err
                                    a.Span
                                    $"$@ splices a seq<string>; this is seq<{formatTy t}> — map show or interpolate per element"
                        | (TStr | TInt | TBool) as t ->
                            return! err a.Span $"$@ splices a seq<string>; this is {formatTy t} — one value? use $x"
                        | t -> return! err a.Span $"$@ splices a seq<string>; this is {formatTy t}"
                    }
                // bash's `$file.bak` reads a field here [D:field-splices]:
                // on a non-record, name the glued spelling
                | EField({ Kind = EVar n } as target, field, _) ->
                    result {
                        let! ttarget = infer ctx env target

                        match resolve ctx ttarget.Ty with
                        | TNamed _
                        | TVar _
                        | TRowVar _ -> return! checkScalarSplice ctx env CmdArg a
                        | ty ->
                            return!
                                err
                                    a.Span
                                    $"`{n}.{field}` reads field '{field}', but {n} is {formatTy ty}, which has no fields — to glue text onto the value, interpolate one word: $\"{{{n}}}.{field}\""
                    }
                | _ -> checkScalarSplice ctx env CmdArg a

            let! targs =
                args
                |> List.fold
                    (fun acc a -> acc |> Result.bind (fun ts -> checkArg a |> Result.map (fun t -> t :: ts)))
                    (Ok [])

            let! tenvO =
                match envO with
                | None -> Ok None
                | Some e ->
                    result {
                        let! te = infer ctx env e
                        do! bind ctx env e.Span (TSeq(TNamed("EnvVar", []))) te.Ty
                        return Some te
                    }

            // a dynamic head is one program [D:dynamic-head]: string
            // exactly, never re-lexed; a seq capture refuses with the
            // bind-and-pick teaching (which line would run?)
            let! thead =
                match head with
                | HeadLit p -> Ok(THeadLit p)
                | HeadDyn(display, he) ->
                    checkDynHead ctx env display he
                    |> Result.map (fun the -> THeadDyn(display, the))

            return {
                Kind = TECmd(thead, List.rev targs, tenvO)
                Ty = TSeq TStr
                Span = expr.Span
            }
        }
    | EInterp parts ->
        result {
            let checkHole = checkScalarSplice ctx env Hole

            let! tparts =
                parts
                |> List.fold
                    (fun acc p ->
                        acc
                        |> Result.bind (fun ts ->
                            match p with
                            | IStr s -> Ok(IStr s :: ts)
                            | IExpr e -> checkHole e |> Result.map (fun t -> IExpr t :: ts)))
                    (Ok [])

            return {
                Kind = TEInterp(List.rev tparts)
                Ty = TStr
                Span = expr.Span
            }
        }
    | ETuple items ->
        result {
            let! titems =
                items
                |> List.fold
                    (fun acc it -> acc |> Result.bind (fun ts -> infer ctx env it |> Result.map (fun t -> t :: ts)))
                    (Ok [])

            let titems = List.rev titems

            return {
                Kind = TETuple titems
                Ty = TTuple(titems |> List.map _.Ty)
                Span = expr.Span
            }
        }
    | EList items ->
        result {
            match items with
            | [] ->
                return {
                    Kind = TEList []
                    Ty = TSeq(TVar(freshName ctx "a"))
                    Span = expr.Span
                }
            | head :: rest ->
                let! thead = infer ctx env head

                let! trest =
                    rest
                    |> List.fold
                        (fun acc item ->
                            acc
                            |> Result.bind (fun ts -> check ctx env item thead.Ty |> Result.map (fun t -> t :: ts)))
                        (Ok [])

                return {
                    Kind = TEList(thead :: List.rev trest)
                    Ty = TSeq thead.Ty
                    Span = expr.Span
                }
        }
    | EMatch(scrutinee, arms) ->
        result {
            let! tscrutinee = infer ctx env scrutinee

            // Bool patterns default an unresolved scrutinee to bool — the same
            // defaulting as in the operator and splice rules.
            do!
                match resolve ctx tscrutinee.Ty with
                | TVar _ when arms |> List.exists (fun (p, _, _) -> p.PKind.IsPBool) ->
                    bind ctx env scrutinee.Span TBool tscrutinee.Ty
                | TVar _ when arms |> List.exists (fun (p, _, _) -> p.PKind.IsPInt) ->
                    bind ctx env scrutinee.Span TInt tscrutinee.Ty
                | TVar _ when arms |> List.exists (fun (p, _, _) -> p.PKind.IsPStr) ->
                    bind ctx env scrutinee.Span TStr tscrutinee.Ty
                | TVar _ when arms |> List.exists (fun (p, _, _) -> p.PKind.IsPUnit) ->
                    bind ctx env scrutinee.Span TUnit tscrutinee.Ty
                // the arms' constructors together pick the union: the one
                // owning ALL of them (local first), so `Ok`/`Error` arms
                // read as Result even when the script declares its own `Ok`
                // — the first arm alone would pick the shadowing union
                | TVar _ as tv ->
                    let ctors =
                        arms
                        |> List.choose (fun (p, _, _) ->
                            match p.PKind with
                            | PCase(c, _, None) -> Some(c, p.PSpan)
                            | _ -> None)

                    let names = ctors |> List.map fst |> List.distinct

                    // a qualified arm names its union itself [D:qualified-case-patterns]
                    let qualified =
                        arms
                        |> List.exists (fun (p, _, _) ->
                            match p.PKind with
                            | PCase(_, _, Some _) -> true
                            | _ -> false)

                    if
                        qualified
                        || names.Length < 2
                        || names |> List.exists (fun c -> (ctorOwnersAll env c).IsEmpty)
                    then
                        // one constructor (or an unknown one): checkPattern's
                        // per-pattern bind and its messages decide
                        Ok()
                    else
                        let common =
                            names
                            |> List.map (ctorOwnersAll env >> Set.ofList)
                            |> Set.intersectMany
                            |> Set.toList
                            |> preferLocal

                        match common with
                        | [ owner ] ->
                            match typeDefFor env owner with
                            | Some(Union def) ->
                                let targs = def.Params |> List.map (fun _ -> TVar(freshName ctx "a"))
                                bind ctx env scrutinee.Span (TNamed(owner, targs)) tv
                            | _ -> Ok()
                        | [] ->
                            let owned =
                                names
                                |> List.map (fun c ->
                                    let os = ctorOwners env c |> String.concat "/"
                                    $"'{c}' ({os})")
                                |> String.concat ", "

                            err
                                (snd ctors.Head)
                                $"these arms name cases of different unions — {owned}; no one union declares them all, so the match has no single type"
                        | _ -> Ok()
                | _ -> Ok()

            let scrutTy = resolve ctx tscrutinee.Ty

            let checkGuard bindings (guard: Expr option) =
                match guard with
                | None -> Ok None
                | Some g -> check ctx (bindParams env bindings) g TBool |> Result.map Some

            match arms with
            | [] -> return! err expr.Span "a match needs at least one arm"
            | (pat0, guard0, body0) :: rest ->
                let! bindings0 = checkPattern ctx env scrutTy pat0
                let! tguard0 = checkGuard bindings0 guard0
                let! tbody0 = infer ctx (bindParams env bindings0) body0

                let checkArm (pat: Pattern, guard: Expr option, body: Expr) =
                    result {
                        let! bindings = checkPattern ctx env scrutTy pat
                        let! tguard = checkGuard bindings guard
                        let! tbody = check ctx (bindParams env bindings) body tbody0.Ty
                        return pat, tguard, tbody
                    }

                let! trest =
                    rest
                    |> List.fold
                        (fun acc arm -> acc |> Result.bind (fun ts -> checkArm arm |> Result.map (fun t -> t :: ts)))
                        (Ok [])

                let tarms = (pat0, tguard0, tbody0) :: List.rev trest
                // re-resolve: a constructor pattern may have typed the
                // scrutinee var during arm checking [D:match-scrutinee-infer]
                do!
                    exhaustive
                        env
                        expr.Span
                        (finalTy ctx tscrutinee.Ty)
                        (tarms |> List.map (fun (p, g, (b: TypedExpr)) -> p, g, b.Span))

                return {
                    Kind = TEMatch(tscrutinee, tarms)
                    Ty = tbody0.Ty
                    Span = expr.Span
                }
        }
    | EIf(cond, thn, els) ->
        result {
            let! tcond = check ctx env cond TBool

            match els with
            | Some e ->
                let! tthn = infer ctx env thn
                let! tels = check ctx env e tthn.Ty

                return {
                    Kind = TEIf(tcond, tthn, Some tels)
                    Ty = tthn.Ty
                    Span = expr.Span
                }
            | None ->
                let rec tailOf (e: Expr) =
                    match e.Kind with
                    | ESeq(_, b) -> tailOf b
                    | _ -> e

                do!
                    if isExitCodeSpine (tailOf thn) then
                        err (tailOf thn).Span Ast.exitCodeDiscardMsg
                    else
                        Ok()

                // a commandish then-tail arms before typing
                // [D:interior-arming] — `if force then git clean -fd`
                // is the effect form; the teaching text below is
                // untouched for everything else
                let armed = armTail thn

                // a diverging tail meets the unit demand [D:fail-bottom]
                // — `if bad then fail "usage"` stays the guard idiom
                let! tthn =
                    if divergesTo armed then
                        check ctx env armed TUnit
                    else
                        infer ctx env armed

                match resolve ctx tthn.Ty with
                | TUnit ->
                    return {
                        Kind = TEIf(tcond, tthn, None)
                        Ty = TUnit
                        Span = expr.Span
                    }
                | ty ->
                    return!
                        err
                            thn.Span
                            $"an if without an else is unit-valued; this then-branch is {formatTy ty} — add an else"
        }

    // $@ [D:argv-splat] is confined by the grammar to command-argument
    // position, where the ECmd arm's checkArg handles it; it never
    // reaches general inference. This arm closes the match totally so
    // an invariant break surfaces as a clear internal error, not a raw
    // MatchFailureException.
    | ESplat _ -> failwith "unreachable: $@ splat outside command arguments (parser confines it to argv)"

// a plan body's value is discarded (the block yields the Plan
// [D:plan-apply]), so its tail obeys the sequenced-unit rule; a command
// tail is armed so the plan's own command refusal names it
and private inferPlanBody (ctx: Ctx) (env: TypeEnv) (body: Expr) : Result<TypedExpr, TypeError> =
    result {
        let! tbody = infer ctx env (armTail body)

        let rec tail (e: Expr) =
            match e.Kind with
            | ESeq(_, rest) -> tail rest
            | _ -> e

        match resolve ctx tbody.Ty with
        | TUnit -> return tbody
        | TVar _ as v ->
            do! bind ctx env body.Span TUnit v
            return tbody
        | ty ->
            let last = tail body

            return!
                err
                    last.Span
                    $"a plan block's value is discarded (the block yields the Plan); this one is {formatTy ty} — bind it or print it"
    }

and private checkSpine
    (ctx: Ctx)
    (env: TypeEnv)
    (head: Expr)
    (args: Expr list)
    (piped: (Ty * Span) option)
    : Result<TypedExpr, TypeError> =
    result {
        let! thead = infer ctx env head
        return! checkSpineOn ctx env head thead args piped
    }

// the spine with its head already typed — `head` supplies spans only
and private checkSpineOn
    (ctx: Ctx)
    (env: TypeEnv)
    (head: Expr)
    (thead: TypedExpr)
    (args: Expr list)
    (piped: (Ty * Span) option)
    : Result<TypedExpr, TypeError> =
    result {
        let arity = args.Length + (if piped.IsSome then 1 else 0)

        // applying a callee whose type is still an unconstrained variable
        // is itself the constraint that it is a function
        // [D:higher-order-params]: unify it with a fresh arrow so the
        // arguments constrain the params and the result flows — textbook
        // HM, matching what the call site already does (a param not
        // applied in its body infers its arrow from the caller). Two var
        // flavours, one move: a __hole var (a cascade-suppression binding
        // whose let already errored) shapes into __hole fresh vars so the
        // application stays silent [PLAN-diagnostics-arc B6]; an ordinary
        // parameter uses ordinary fresh vars and infers.
        do!
            match finalTy ctx thead.Ty with
            | TVar v ->
                let stem = if v.StartsWith "__hole" then "__hole" else "a"

                let arrow =
                    List.replicate arity ()
                    |> List.foldBack (fun () acc -> TFun(TVar(freshName ctx stem), acc))
                    <| TVar(freshName ctx stem)

                bind ctx env head.Span (TVar v) arrow
            | _ -> Ok()

        let rec arrows n ty =
            match resolve ctx ty with
            | TFun(_, cod) -> arrows (n + 1) cod
            | res -> n, res

        // over-application through a returned function: when the declared
        // arrows run out at a type variable (`id f x`, `snd t 5`), apply
        // what the arrows take, then apply the rest to the result — the
        // result's variable unifies with a fresh arrow like any callee
        let headParams = funParams ctx arity thead.Ty

        let split =
            match headParams with
            | Some _ -> None
            | None ->
                match arrows 0 thead.Ty with
                | available, TVar _ when available > 0 && available <= args.Length ->
                    let taken, rest = List.splitAt available args

                    let partial =
                        taken
                        |> List.fold
                            (fun (acc: Expr) a -> {
                                Kind = EApp(acc, a)
                                Span = Span.union acc.Span a.Span
                            })
                            head

                    Some(partial, taken, rest)
                | _ -> None

        // a result that is no function after all keeps the arity teaching
        let! applied =
            match split with
            | Some(partial, taken, rest) ->
                checkSpineOn ctx env head thead taken None
                |> Result.map (fun tpartial ->
                    match resolve ctx tpartial.Ty with
                    | TFun _
                    | TVar _ -> Some(partial, tpartial, rest)
                    | _ -> None)
            | None -> Ok None

        match applied with
        | Some(partial, tpartial, rest) -> return! checkSpineOn ctx env partial tpartial rest piped
        | None ->

            match headParams with
            | None ->
                match piped with
                | Some _ ->
                    return!
                        err
                            head.Span
                            $"the right side of a pipe must be a function taking the piped value; it has type {formatTy (finalTy ctx thead.Ty)}"
                | None ->
                    let rec countParams ty =
                        match resolve ctx ty with
                        | TFun(_, cod) -> 1 + countParams cod
                        | _ -> 0

                    let available = countParams thead.Ty

                    match thead.Kind with
                    | TEVar name when available > 0 ->
                        // point at the first extra argument, not the head — and
                        // when it sits on a different physical line, the extra
                        // args are an indented continuation the writer likely
                        // meant as a separate statement [D:over-apply-continuation]
                        let extra = List.tryItem available args

                        let onContinuation =
                            match toPhys.Value, extra with
                            | Some tr, Some ex -> fst (tr head.Span.Start.Col) <> fst (tr ex.Span.Start.Col)
                            | _ -> false

                        let hint =
                            if onContinuation then
                                "; these are read as an indented continuation of the line above. A separate statement must line up with it, not sit deeper — and a block body written inline (after '->' or 'then') takes no second statement below it: start the body on its own line so its statements share a column"
                            else
                                ""

                        let span = extra |> Option.map (fun e -> e.Span) |> Option.defaultValue head.Span

                        return! err span $"'{name}' takes at most {available} argument(s), but got {args.Length}{hint}"
                    | TEVar name when (name = "ls" || name = "pwd") && not (List.isEmpty args) ->
                        // a command-name builtin VALUE applied to args is almost
                        // always a shell-command reflex (`ls -la`) [D:ls-command-teach]
                        // — it is weir's typed listing, not the external command,
                        // so point at the force-external escape
                        // a user's own `let ls = …` is no builtin: it is named
                        // as the binding it is, with no alias to suggest
                        let builtinShape =
                            match name, finalTy ctx thead.Ty with
                            | "ls", TSeq(TNamed("FileRow", [])) -> true
                            | "pwd", TStr -> true
                            | _ -> false

                        if builtinShape then
                            return!
                                err
                                    head.Span
                                    $"'{name}' is weir's typed {formatTy (finalTy ctx thead.Ty)} value, not the shell command — to run the external '{name}' with arguments, force it external: ^{name} … (or alias it: #alias x = {name} …)"
                        else
                            return!
                                err
                                    head.Span
                                    $"'{name}' is bound here to a {formatTy (finalTy ctx thead.Ty)} value, not the shell command — to run the external '{name}' with arguments, force it external: ^{name} …"
                    | _ ->
                        return!
                            err
                                head.Span
                                $"this expression is not a function taking {args.Length} argument(s); it has type {formatTy (finalTy ctx thead.Ty)}"
            | Some(paramTys, resultTy) ->
                // when a defaulted-to-string parameter causes the mismatch,
                // say so and name the anchor (PLAN-dx-review D6): the string
                // came from a defaulting decision in the callee's body, not
                // from anything visible at the call. A hole on a generalized
                // var no longer defaults [D:hole-generic]; this serves the
                // defaults a scheme still records
                let nameHoleDefault (paramTy: Ty) (e: TypeError) : TypeError =
                    let defaults =
                        match thead.Kind with
                        | TEVar name ->
                            match Map.tryFind name env.Values with
                            | Some sch -> sch.HoleDefaults
                            | None -> []
                        | _ -> []

                    match defaults, resolve ctx paramTy with
                    | (l, c) :: _, TStr when e.Message.StartsWith "expected string" ->
                        let name =
                            match thead.Kind with
                            | TEVar n -> $"'{n}'"
                            | _ -> "the function"

                        {
                            e with
                                Message =
                                    e.Message
                                    + $" — {name} takes string because a bare interpolation hole defaulted its parameter (the hole at {l}:{c}); a typed use in the hole fixes it (e.g. {{n + 0}}), or pass a string"
                        }
                    | _ -> e

                do!
                    match piped with
                    | Some(pipedTy, _) ->
                        // the stage that cannot take the piped value is the
                        // fault, not the pipeline's source — in a chain the
                        // source may sit lines above the failing stage
                        let stageSpan = {
                            head.Span with
                                End =
                                    (match List.tryLast args with
                                     | Some a -> a.Span.End
                                     | None -> head.Span.End)
                        }

                        bind ctx env stageSpan (List.last paramTys) pipedTy
                        |> Result.mapError (nameHoleDefault (List.last paramTys))
                    | None -> Ok()

                let! typedArgs =
                    let rec go (args: Expr list) (ps: Ty list) (acc: TypedExpr list) =
                        match args, ps with
                        | arg :: args, paramTy :: ps ->
                            match check ctx env arg paramTy with
                            | Ok t -> go args ps (t :: acc)
                            | Error e -> Error(nameHoleDefault paramTy e)
                        | _ -> Ok(List.rev acc)

                    go args paramTys []

                let fullTy =
                    List.foldBack (fun p acc -> TFun(finalTy ctx p, acc)) paramTys (finalTy ctx resultTy)

                let applied =
                    typedArgs
                    |> List.fold
                        (fun (acc: TypedExpr) targ ->
                            let cod =
                                match acc.Ty with
                                | TFun(_, c) -> c
                                | _ -> failwith "unreachable: funParams guaranteed a function"

                            {
                                Kind = TEApp(acc, targ)
                                Ty = cod
                                Span = Span.union acc.Span targ.Span
                            })
                        { thead with Ty = fullTy }

                return applied
    }

// a command chain by AST shape — the parser's isCommandish, check-side
// [D:interior-arming]
and private isCmdChain (e: Expr) =
    match e.Kind with
    | ECmd _
    | EPipe(_, { Kind = ECmd _ }) -> true
    | _ -> false

// arm the tail of a statement body [D:interior-arming]: a commandish
// final expression under a unit demand is the effect form — rewrite it
// to the same `|> print` the statement positions get, recursing through
// sequences. Pure AST pre-pass so teaching errors keep their text.
and armTail (e: Expr) : Expr =
    if isCmdChain e then
        {
            Kind = EPipe(e, { Kind = EVar "|print"; Span = e.Span })
            Span = e.Span
        }
    else
        match e.Kind with
        | ESeq(a, b) -> {
            Kind = ESeq(a, armTail b)
            Span = e.Span
          }
        // the tail rides through a scope and a let-in to their bodies
        // [D:within-scopes] — statement position arms a scope's final
        // command exactly as an if body's
        | EWithin(k, n, a, o, b) -> {
            Kind = EWithin(k, n, a, o, armTail b)
            Span = e.Span
          }
        | EAlways(b, c) -> {
            Kind = EAlways(armTail b, c)
            Span = e.Span
          }
        | EHold b -> {
            Kind = EHold(armTail b)
            Span = e.Span
          }
        | ELet(n, ns, v, b) -> {
            Kind = ELet(n, ns, v, armTail b)
            Span = e.Span
          }
        // a statement-position match arms each arm's tail
        // [D:match-arm-commands] — a command arm streams, exactly as an
        // if body's; arms then unify at unit instead of at seq<string>
        // (the discard the raw match would report). Value position never
        // reaches armTail (gateExprs), so capture-typed matches stay.
        | EMatch(scrut, arms) -> {
            Kind = EMatch(scrut, arms |> List.map (fun (p, g, b) -> p, g, armTail b))
            Span = e.Span
          }
        // a statement-position if/else arms both branches [D:if-body-stop]
        // (an elif chain is the else branch's nested if); the else-less
        // if arms its then-tail at its own check arm
        | EIf(c, t, Some els) -> {
            Kind = EIf(c, armTail t, Some(armTail els))
            Span = e.Span
          }
        | _ -> e

// armTail would rewrite something — its own recursion, as a test
and private tailArms (e: Expr) : bool =
    isCmdChain e
    || (match e.Kind with
        | ESeq(_, b)
        | EWithin(_, _, _, _, b)
        | EAlways(b, _)
        | EHold b
        | ELet(_, _, _, b) -> tailArms b
        | EMatch(_, arms) -> arms |> List.exists (fun (_, _, b) -> tailArms b)
        | EIf(_, t, Some els) -> tailArms t || tailArms els
        | _ -> false)

// an exit-code spine discarded where unit is demanded keeps its
// tailored teaching [D:district-retirement]
and private isExitCodeSpine (e: Expr) =
    let rec head (x: Expr) =
        match x.Kind with
        | EApp(f, _) -> head f
        | _ -> x

    match (head e).Kind with
    | EVar n -> n.StartsWith "|exitCoded"
    | _ -> false

/// the scope's arg and binder contracts [D:within-scopes] — one resolver for
/// both type directions, dispatched kind-first on the union
/// [D:within-kind-union] so a new kind must claim its arg contract or the
/// build fails. A kind resolved in one direction and not the other drops
/// its node silently, and the eval side cannot tell a dropped node from a
/// kind that never carries one.
and private withinContracts
    (ctx: Ctx)
    (env: TypeEnv)
    (kind: WithinKindId)
    (binder: (string * Span) option)
    (arg: Expr option)
    (opts: Expr option)
    : Result<TypedExpr option * TypedExpr option * TypeEnv, TypeError> =
    result {
        let! targ =
            match kind with
            | WithinCd ->
                match arg with
                | Some a -> check ctx env a TStr |> Result.map Some
                | None -> Ok None
            | WithinEnv ->
                match arg with
                | Some a -> check ctx env a (TSeq(TNamed("EnvVar", []))) |> Result.map Some
                | None -> Ok None
            | WithinLock ->
                match arg with
                | Some a -> check ctx env a TStr |> Result.map Some
                | None -> Ok None
            // proc's arg is the command node [D:scoped-procs] — typed as any
            // command (the parser guarantees ECmd), evaluated by the scope as
            // a spawn, never as a statement
            | WithinProc ->
                match arg with
                | Some a -> infer ctx env a |> Result.map Some
                | None -> Ok None
            // serve's arg is the config record [D:http-serve] — a
            // ServerConfig `{ port; maxConcurrent }`; the handler rides
            // opts (below), typed against the request->response function.
            // bodyTimeout is optional in the literal [D:serve-body-timeout]:
            // a bare `{ port; maxConcurrent }` literal is accepted and the
            // timeout rests at its default, so the field addition does not
            // break existing serve scripts. The generic record resolver
            // matches by exact field set, so the two-field literal is
            // checked here against the allowed shape; any non-literal
            // config (a variable, `{ ServerConfig.defaults with … }`)
            // falls through to the ordinary ServerConfig check.
            | WithinServe ->
                match arg with
                | Some({ Kind = ERecord fields } as a) ->
                    let names = fields |> List.map (fun (n, _, _) -> n) |> Set.ofList
                    let core = Set.ofList [ "port"; "maxConcurrent" ]
                    // maxBody is optional the same way [D:serve-max-body]
                    let optional = Set.ofList [ "bodyTimeout"; "maxBody" ]

                    if Set.isSubset core names && Set.isSubset (Set.difference names core) optional then
                        let fieldTy name =
                            match name with
                            | "port" -> TNamed("Option", [ TInt ])
                            | "bodyTimeout" -> TDur
                            | "maxBody" -> TSize
                            | _ -> TInt

                        fields
                        |> List.fold
                            (fun acc (n, _, v) ->
                                acc
                                |> Result.bind (fun ts ->
                                    match check ctx env v (fieldTy n), n with
                                    // the pre-Option spelling names its repair
                                    // [D:serve-port-option]
                                    | Error _, "port" when (infer ctx env v |> Result.map (fun t -> t.Ty)) = Ok TInt ->
                                        err
                                            v.Span
                                            "expected Option<int>, got int — serve's port is optional: `port = Some 8080` for that port, `port = None` for a free one"
                                    | r, _ -> r |> Result.map (fun tv -> (n, tv) :: ts)))
                            (Ok [])
                        |> Result.map (fun tfields ->
                            Some {
                                Kind = TERecord("ServerConfig", List.rev tfields)
                                Ty = TNamed("ServerConfig", [])
                                Span = a.Span
                            })
                    else
                        // an unexpected field set — the ordinary check
                        // produces the located "no declared record" or
                        // field-type diagnostic
                        check ctx env a (TNamed("ServerConfig", [])) |> Result.map Some
                | Some a -> check ctx env a (TNamed("ServerConfig", [])) |> Result.map Some
                | None -> Ok None
            | WithinTmp -> Ok None
            // pure carries no resource [D:pure-stage1] — no arg, no
            // binder; its law is enforced by the checked-statement
            // pipeline's post-check layer (the classifier lives after
            // Builtins, out of this file's compile reach). readonly
            // is the same shape, its law the ambient/mutation ceiling
            // [D:pure-stage2]
            // plan carries no resource either [D:plan-apply] — its law
            // (proc/apply refusals) rides the same post-check layer
            | WithinPure
            | WithinReadonly
            | WithinPlan -> Ok None

        // opts is a Duration for lock (timeout=), the handler function
        // for serve [D:http-serve], and absent elsewhere — the kind
        // decides its type, so a dropped node cannot masquerade
        let! topts =
            match kind, opts with
            | WithinServe, Some o ->
                check ctx env o (TFun(TNamed("HttpServerRequest", []), TNamed("HttpServerResponse", [])))
                |> Result.map Some
            | _, Some o -> check ctx env o TDur |> Result.map Some
            | _, None -> Ok None

        do!
            match binder with
            | Some(n, bs) -> checkBinderName bs n
            | None -> Ok()

        let binderTy =
            match kind with
            | WithinProc -> TNamed("Proc", [])
            | WithinServe -> TNamed("Server", [])
            | WithinTmp
            | WithinCd
            | WithinEnv
            | WithinLock
            | WithinPure
            | WithinReadonly
            | WithinPlan -> TStr

        let benv =
            match binder with
            | Some(n, _) -> bindParams env [ n, binderTy ]
            | None -> env

        return targ, topts, benv
    }

and private check (ctx: Ctx) (env: TypeEnv) (expr: Expr) (expected: Ty) : Result<TypedExpr, TypeError> =
    match expr.Kind, resolve ctx expected with
    // interior arming's check half [D:interior-arming]: a command chain
    // where unit is demanded (a checked lambda body, a sequence tail
    // under unit) is the effect form — `files |> Seq.iter (fun f ->
    // git add $f)` works without a district
    | (ECmd _ | EPipe(_, { Kind = ECmd _ })), TUnit when isCmdChain expr ->
        check
            ctx
            env
            {
                Kind =
                    EPipe(
                        expr,
                        {
                            Kind = EVar "|print"
                            Span = expr.Span
                        }
                    )
                Span = expr.Span
            }
            TUnit
    // an if/else or match under a unit demand (a loop body's or a
    // checked lambda's tail, an always cleanup) arms its branches'
    // command tails as a statement-position one does
    // [D:interior-arming] [D:match-arm-commands]
    | (EIf(_, _, Some _) | EMatch _), TUnit when tailArms expr -> check ctx env (armTail expr) TUnit
    // the check direction rides into a scope's body [D:within-scopes] —
    // a statement-position `within` demands unit of the block, arming a
    // final command exactly as any block does
    | ECapture inner, _ -> check ctx env inner expected
    | EAlways(body, cleanup), _ ->
        result {
            let rec exitSite (e: Expr) : Ast.Span option =
                match e.Kind with
                | EVar "exit" -> Some e.Span
                | _ -> Ast.exprChildren e |> List.tryPick exitSite

            do!
                match exitSite cleanup with
                | Some sp ->
                    Error {
                        Message =
                            "exit inside always is refused — teardown of an unwinding scope must finish (the original error would become a code); fail \"msg\" carries a message instead"
                        Span = sp
                        Origin = None
                    }
                | None -> Ok()

            do! execInAlways body cleanup
            let! tbody = check ctx env body expected
            let! tcleanup = check ctx env cleanup TUnit

            return {
                Kind = TEAlways(tbody, tcleanup)
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    | EHold body, _ ->
        result {
            let! tbody = check ctx env body expected

            return {
                Kind = TEHold tbody
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    // a plan block yields a Plan regardless of the body's type
    // [D:plan-apply]: the body runs for its capture, its own value is
    // discarded — so infer the body (not check it against `expected`)
    // and unify the region's Plan with the expectation
    | EWithin(WithinPlan, binder, arg, opts, body), _ ->
        result {
            let! targ, topts, benv = withinContracts ctx env WithinPlan binder arg opts
            let! tbody = inferPlanBody ctx benv body
            let planTy = TNamed("Plan", [])
            do! bind ctx env expr.Span expected planTy

            return {
                Kind = TEWithin(WithinPlan, binder, targ, topts, tbody)
                Ty = planTy
                Span = expr.Span
            }
        }
    | EWithin(kind, binder, arg, opts, body), _ ->
        result {
            do! execInScope kind body
            let! targ, topts, benv = withinContracts ctx env kind binder arg opts
            let! tbody = check ctx benv body expected

            return {
                Kind = TEWithin(kind, binder, targ, topts, tbody)
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    // the check direction rides through a sequence to its final
    // expression [D:interior-arming] — F#'s rule, and what lets a
    // final command in a unit-demanded block arm
    | ESeq(first, rest), _ ->
        // the infer twin's statement arming [D:seq-arming]
        let first = armTail first

        result {
            let! tfirst =
                result {
                    let! tfirst =
                        // the infer twin's diverging-head carve [D:fail-bottom]
                        if divergesTo first then
                            check ctx env first TUnit
                        else
                            infer ctx env first

                    match resolve ctx tfirst.Ty with
                    | TUnit -> return tfirst
                    | ty -> return! err first.Span (seqUnitError first ty)
                }
                |> recoverAt ctx first.Span (fun () -> TUnit)

            let! trest = check ctx env rest expected

            return {
                Kind = TESeq(tfirst, trest)
                Ty = trest.Ty
                Span = expr.Span
            }
        }
    | EOpValue op, expected ->
        // the check-mode twin [D:operator-values]: the pushed domain
        // reaches the desugared lambda's params — `Seq.reduce (+)` pins
        // the operands from the seq's element type, exactly as the
        // spelled-out lambda would
        check ctx env (opValueLambda op expr.Span) expected
    | ELambdaPat(pat, body), TFun(dom, cod) ->
        // check-mode twin: the binder shape binds against the pushed
        // domain before the body runs, so piped element types reach
        // the components ahead of any hole defaulting
        result {
            let! shape, binds = binderShapeAt ctx env (Some dom) pat
            do! bind ctx env pat.PSpan shape dom
            return! lambdaCore env expr.Span (fun tb -> TELambdaPat(pat, tb)) dom binds (fun e -> check ctx e body cod)
        }
    | ELambda(param, pspan, body), TFun(dom, cod) ->
        result {
            do! checkBinderName expr.Span param

            let typeBody e =
                match body.Kind, resolve ctx cod with
                // a nested lambda against a function cod pushes through
                // [D:seq-fold]: the inner domain may already be resolved
                // (a piped element type), and the infer fallback would
                // drop it
                | (ELambda _ | ELambdaPat _), TFun _ -> check ctx e body cod
                | _ ->
                    if hasVars ctx cod then
                        result {
                            let! tbody = infer ctx e body
                            do! bind ctx env tbody.Span cod tbody.Ty
                            return tbody
                        }
                    else
                        check ctx e body cod

            return! lambdaCore env expr.Span (fun tb -> TELambda(param, pspan, tb)) dom [ param, dom ] typeBody
        }
    | ELambda _, (TInt | TStr | TBool | TSeq _ | TNamed _ as t) ->
        err expr.Span $"expected {formatTy t}, got a function"
    | ELet(name, nameSpan, value, body), _ ->
        result {
            let! tvalue =
                result {
                    do! checkBinderName expr.Span name
                    return! infer ctx env value
                }
                |> recoverAt ctx value.Span (fun () -> holeVar ctx)

            let valueTy = finalTy ctx tvalue.Ty

            let scheme = generalizeLet ctx env valueTy

            let! tbody = check ctx (bindValue name scheme env) body expected

            return {
                Kind = TELet(name, nameSpan, tvalue, tbody)
                Ty = tbody.Ty
                Span = expr.Span
            }
        }
    | _ ->
        result {
            let! te = infer ctx env expr
            do! bind ctx env expr.Span expected te.Ty
            return te
        }

// the dynamic head's type law [D:dynamic-head]: string exactly — the
// value is one program, never re-lexed. A seq capture cannot answer
// "which line is the program", so it refuses with the bind-and-pick
// teaching instead of deciding implicitly.
and private checkDynHead (ctx: Ctx) (env: TypeEnv) (display: string) (he: Expr) : Result<TypedExpr, TypeError> =
    result {
        let! the = infer ctx env he

        match resolve ctx the.Ty with
        | TStr -> return the
        | TVar _ ->
            do! bind ctx env he.Span TStr the.Ty
            return the
        | TSeq _ as t ->
            return!
                err
                    he.Span
                    $"a dynamic head runs one program, and ^{display} is {formatTy t} — which line is it? bind and pick (let tool = cmd | line), then run ^$tool"
        | t -> return! err he.Span $"a dynamic head is a program name (string); ^{display} is {formatTy t}"
    }

and private checkScalarSplice (ctx: Ctx) (env: TypeEnv) (site: SpliceSite) (arg: Expr) : Result<TypedExpr, TypeError> =
    result {
        let! targ = infer ctx env arg

        match resolve ctx targ.Ty with
        | TVar v ->
            // defer [D:splice-default-last]: the enclosing statement's
            // inference may still resolve v (pipe-into-lambda, say);
            // default-or-reject happens at the boundary. A hole demands
            // Render now, so a let that generalizes v carries the class
            // [D:hole-generic]
            do!
                (if site = Hole then
                     spliceAdmit ctx env site arg.Span targ.Ty
                 else
                     Ok())

            ctx.PendingSplices <- (v, arg.Span, site) :: ctx.PendingSplices
            return targ
        | ty ->
            do! spliceAdmit ctx env site arg.Span ty
            return targ
    }

// the yaml district's template typing [D:yaml-district]: splices carry
// the liftable law (string/int/float/bool, Yaml, Option of one, seq of
// those), key splices are strings, `for` binders bind like lambda
// params over the source's element type, literal duplicate keys are
// check errors.
and private yamlSpliceable (ctx: Ctx) (ty: Ty) : bool = yamlSpliceableWith (resolve ctx) ty

and private checkYamlTpl (ctx: Ctx) (env: TypeEnv) (tpl: YamlTpl) : Result<TypedYamlTpl, TypeError> =
    match tpl with
    | YtScalar(raw, q, sp) -> Ok(TYtScalar(raw, q, sp))
    | YtBlock(text, sp) -> Ok(TYtBlock(text, sp))
    | YtDrop sp -> Ok(TYtDrop sp)
    | YtSplice e ->
        result {
            let! te = infer ctx env e
            let rty = resolve ctx te.Ty

            if yamlSpliceable ctx rty then
                return TYtSplice te
            else
                let hint =
                    match rty with
                    | TDur -> " — convert explicitly (Duration.toMillis for the number, or show for the text)"
                    | TSize -> " — convert explicitly (Size.toBytes for the number, or show for the text)"
                    | _ -> ""

                return!
                    err
                        e.Span
                        $"a yaml splice takes string/int/float/bool, a Yaml node, Option of one, or a seq of those; got {formatTy rty}{hint}"
        }
    | YtSeq(items, sp) ->
        items
        |> List.fold
            (fun acc it ->
                acc
                |> Result.bind (fun ts -> checkYamlItem ctx env it |> Result.map (fun t -> t :: ts)))
            (Ok [])
        |> Result.map (fun ts -> TYtSeq(List.rev ts, sp))
    | YtMap(entries, sp) ->
        let litKeys =
            entries
            |> List.choose (function
                | YtPair(YtKeyLit(k, _), _) -> Some k
                | _ -> None)

        match firstDup litKeys with
        | Some dup -> err sp $"duplicate key '{dup}' in this yaml mapping"
        | None ->
            entries
            |> List.fold
                (fun acc en ->
                    acc
                    |> Result.bind (fun es -> checkYamlEntry ctx env en |> Result.map (fun e -> e :: es)))
                (Ok [])
            |> Result.map (fun es -> TYtMap(List.rev es, sp))

and private checkYamlEntry (ctx: Ctx) (env: TypeEnv) (entry: YamlTplEntry) : Result<TypedYamlTplEntry, TypeError> =
    match entry with
    | YtPair(YtKeyLit(k, ksp), v) -> checkYamlTpl ctx env v |> Result.map (fun tv -> TYtPair(TYtKeyLit(k, ksp), tv))
    | YtPair(YtKeySplice e, v) ->
        result {
            let! tk = check ctx env e TStr
            let! tv = checkYamlTpl ctx env v
            return TYtPair(TYtKeySplice tk, tv)
        }
    | YtForEntries(binder, source, body) ->
        result {
            let! tsrc = infer ctx env source
            let elem = TVar(freshName ctx "a")
            do! bind ctx env source.Span (TSeq elem) tsrc.Ty
            let! shape, binds = binderShapeAt ctx env (Some elem) binder
            do! bind ctx env binder.PSpan shape elem
            let env' = bindParams env binds

            let! tbody =
                body
                |> List.fold
                    (fun acc en ->
                        acc
                        |> Result.bind (fun es -> checkYamlEntry ctx env' en |> Result.map (fun e -> e :: es)))
                    (Ok [])

            return TYtForEntries(binder, tsrc, List.rev tbody)
        }

and private checkYamlItem (ctx: Ctx) (env: TypeEnv) (item: YamlTplItem) : Result<TypedYamlTplItem, TypeError> =
    match item with
    | YtItem t -> checkYamlTpl ctx env t |> Result.map TYtItem
    | YtDropItem(t, sp) -> checkYamlTpl ctx env t |> Result.map (fun tt -> TYtDropItem(tt, sp))
    | YtForItems(binder, source, body) ->
        result {
            let! tsrc = infer ctx env source
            let elem = TVar(freshName ctx "a")
            do! bind ctx env source.Span (TSeq elem) tsrc.Ty
            let! shape, binds = binderShapeAt ctx env (Some elem) binder
            do! bind ctx env binder.PSpan shape elem
            let env' = bindParams env binds

            let! tbody =
                body
                |> List.fold
                    (fun acc it ->
                        acc
                        |> Result.bind (fun ts -> checkYamlItem ctx env' it |> Result.map (fun t -> t :: ts)))
                    (Ok [])

            return TYtForItems(binder, tsrc, List.rev tbody)
        }

// the deferred splice resolution — runs at every statement boundary
// (typecheckWith / typecheckBinder), before finalization walks:
// still-unresolved holes default to string, resolved-to-scalar holes
// pass, anything else gets the original rejection at the hole's span
// [D:splice-default-last]
// `generic`: the vars the statement's binding will generalize — a hole
// on one keeps its Render constraint for the scheme instead of the
// string default [D:hole-generic]
let private resolvePendingSplicesCore (ctx: Ctx) (env: TypeEnv) (generic: Set<string>) : Result<unit, TypeError> =
    ctx.PendingSplices
    |> List.rev
    |> List.fold
        (fun acc (v, span, site) ->
            acc
            |> Result.bind (fun () ->
                match resolve ctx (TVar v) with
                | TVar u when site = Hole && generic.Contains u -> Ok()
                | TVar _ ->
                    // the string default fires — record the hole's
                    // physical anchor for the scheme (D6); only interp
                    // holes teach (a cmd splice defaulting is the argv
                    // law, not a surprise)
                    (match site, toPhys.Value with
                     | Hole, Some tr ->
                         let l, c = tr span.Start.Col
                         ctx.HoleDefaulted <- (l, c) :: ctx.HoleDefaulted
                     | _ -> ())

                    bind ctx env span TStr (TVar v)
                | ty -> spliceAdmit ctx env site span ty))
        (Ok())
    |> Result.bind (fun () ->
        // point-free print resolves the same way (PLAN-dx-review D7):
        // printArgTy is the one law — use-site types validate, a free
        // var takes the string default
        ctx.PendingPrints
        |> List.rev
        |> List.fold
            (fun acc (v, span) ->
                acc
                |> Result.bind (fun () -> printArgTy ctx env span (TVar v) |> Result.map ignore))
            (Ok()))

let private resolvePendingSplices (ctx: Ctx) (env: TypeEnv) (generic: Set<string>) : Result<unit, TypeError> =
    // a `+` nothing decided [D:plus-defer] — the first one written
    match ctx.AddsOpen |> Map.toList |> List.tryHead with
    | Some(_, (span, _)) -> err span "cannot infer the operand types of '+'; pipe data in or use concrete values"
    | None -> resolvePendingSplicesCore ctx env generic

// List.map that hands back `xs` itself when `f` changed no element
let private shareMap (f: 'a -> 'a) (xs: 'a list) : 'a list =
    match xs with
    | [] -> xs
    | [ x ] ->
        let x' = f x
        if same x' x then xs else [ x' ]
    | _ ->
        let out = Array.zeroCreate (List.length xs)
        let mutable i = 0
        let mutable changed = false

        for x in xs do
            let x' = f x
            out[i] <- x'

            if not (same x' x) then
                changed <- true

            i <- i + 1

        if changed then List.ofArray out else xs

let private shareOpt (f: 'a -> 'a) (o: 'a option) : 'a option =
    match o with
    | Some x ->
        let x' = f x
        if same x' x then o else Some x'
    | None -> o

let rec private finalizeExpr (ctx: Ctx) (te: TypedExpr) : TypedExpr =
    let fin = finalizeExpr ctx
    let k0 = te.Kind

    let kind =
        match k0 with
        | TELet(n, ns, v, b) ->
            let v' = fin v
            let b' = fin b
            if same v' v && same b' b then k0 else TELet(n, ns, v', b')
        | TELambda(p, ps, b) ->
            let b' = fin b
            if same b' b then k0 else TELambda(p, ps, b')
        | TEApp(f, a) ->
            let f' = fin f
            let a' = fin a
            if same f' f && same a' a then k0 else TEApp(f', a')
        | TEPipe(a, f) ->
            let a' = fin a
            let f' = fin f
            if same a' a && same f' f then k0 else TEPipe(a', f')
        | TEField(t, f) ->
            let t' = fin t
            if same t' t then k0 else TEField(t', f)
        | TESlice(t, lo, hi, s, loEnd, hiEnd) ->
            let t' = fin t
            let lo' = shareOpt fin lo
            let hi' = shareOpt fin hi

            if same t' t && same lo' lo && same hi' hi then
                k0
            else
                TESlice(t', lo', hi', s, loEnd, hiEnd)
        | TEBinOp(op, l, r) ->
            let l' = fin l
            let r' = fin r
            if same l' l && same r' r then k0 else TEBinOp(op, l', r')
        | TEUpdate(src, ups) ->
            let src' = fin src

            let ups' =
                ups
                |> shareMap (fun ((p, v) as u) ->
                    let v' = fin v
                    if same v' v then u else (p, v'))

            if same src' src && same ups' ups then
                k0
            else
                TEUpdate(src', ups')
        | TERecord(n, fields) ->
            let fields' =
                fields
                |> shareMap (fun ((f, v) as fv) ->
                    let v' = fin v
                    if same v' v then fv else (f, v'))

            if same fields' fields then k0 else TERecord(n, fields')
        | TEList items ->
            let items' = shareMap fin items
            if same items' items then k0 else TEList items'
        | TETuple items ->
            let items' = shareMap fin items
            if same items' items then k0 else TETuple items'
        | TEYaml(tpl, schema, patchBy) -> TEYaml(finalizeYamlTpl ctx tpl, schema, patchBy)
        | TELetPat(p, v, b) ->
            let v' = fin v
            let b' = fin b
            if same v' v && same b' b then k0 else TELetPat(p, v', b')
        | TELambdaPat(p, b) ->
            let b' = fin b
            if same b' b then k0 else TELambdaPat(p, b')
        | TECmd(head, args, envO) ->
            let fhead =
                match head with
                | THeadLit _ -> head
                | THeadDyn(d, he) ->
                    let he' = fin he
                    if same he' he then head else THeadDyn(d, he')

            let args' = shareMap fin args
            let envO' = shareOpt fin envO

            if same fhead head && same args' args && same envO' envO then
                k0
            else
                TECmd(fhead, args', envO')
        | TESplat e ->
            let e' = fin e
            if same e' e then k0 else TESplat e'
        | TEInterp parts ->
            let parts' =
                parts
                |> shareMap (fun part ->
                    match part with
                    | IStr _ -> part
                    | IExpr e ->
                        let e' = fin e
                        if same e' e then part else IExpr e')

            if same parts' parts then k0 else TEInterp parts'
        | TEMatch(s, arms) ->
            let s' = fin s

            let arms' =
                arms
                |> shareMap (fun ((p, g, b) as arm) ->
                    let g' = shareOpt fin g
                    let b' = fin b
                    if same g' g && same b' b then arm else (p, g', b'))

            if same s' s && same arms' arms then
                k0
            else
                TEMatch(s', arms')
        | TEIf(c, t, e) ->
            let c' = fin c
            let t' = fin t
            let e' = shareOpt fin e

            if same c' c && same t' t && same e' e then
                k0
            else
                TEIf(c', t', e')
        | TEDur _ -> k0
        | TEFloat _ -> k0
        | TESize _ -> k0
        | TERetry(ip, o, w, b, u) ->
            let o' = fin o
            let w' = shareOpt fin w
            let b' = fin b

            let u' =
                u
                |> shareOpt (fun ((n, p) as nu) ->
                    let p' = fin p
                    if same p' p then nu else (n, p'))

            if same o' o && same w' w && same b' b && same u' u then
                k0
            else
                TERetry(ip, o', w', b', u')
        | TESeq(a, b) ->
            let a' = fin a
            let b' = fin b
            if same a' a && same b' b then k0 else TESeq(a', b')
        | TEWithin(k, n, a, o, b) ->
            let a' = shareOpt fin a
            let o' = shareOpt fin o
            let b' = fin b

            if same a' a && same o' o && same b' b then
                k0
            else
                TEWithin(k, n, a', o', b')
        | TEAlways(b, c) ->
            let b' = fin b
            let c' = fin c
            if same b' b && same c' c then k0 else TEAlways(b', c')
        | TEHold b ->
            let b' = fin b
            if same b' b then k0 else TEHold b'
        | TEEnvLoad _
        | TEArgsLoad _ -> k0
        | leaf -> leaf

    let ty = finalTy ctx te.Ty

    if same kind k0 && same ty te.Ty then
        te
    else
        { te with Kind = kind; Ty = ty }

// typecheckWith: the statement-boundary rule for class constraints.
// Residue = pendings still riding unresolved vars that appear in the
// statement's final type — the caller's generalization scoops them
// (generalizeWith). A pending on a var outside the final type is
// ambiguous (no defaulting, no ambiguity resolution): error asking
// for context — reject-don't-guess, applied at the boundary rather
// than at the operator.
// The statement-level destructuring binder: check the RHS, bind the
// binder shape against it, generalize per name. Residual/ambiguous
// constraints follow typecheckWith's boundary rule.

and private finalizeYamlTpl (ctx: Ctx) (tpl: TypedYamlTpl) : TypedYamlTpl =
    match tpl with
    | TYtScalar _ -> tpl
    | TYtBlock _ -> tpl
    | TYtSplice e -> TYtSplice(finalizeExpr ctx e)
    | TYtDrop _ -> tpl
    | TYtSeq(items, sp) -> TYtSeq(items |> List.map (finalizeYamlItem ctx), sp)
    | TYtMap(entries, sp) -> TYtMap(entries |> List.map (finalizeYamlEntry ctx), sp)

and private finalizeYamlEntry (ctx: Ctx) (entry: TypedYamlTplEntry) : TypedYamlTplEntry =
    match entry with
    | TYtPair(TYtKeyLit(k, ksp), v) -> TYtPair(TYtKeyLit(k, ksp), finalizeYamlTpl ctx v)
    | TYtPair(TYtKeySplice e, v) -> TYtPair(TYtKeySplice(finalizeExpr ctx e), finalizeYamlTpl ctx v)
    | TYtForEntries(b, src, body) -> TYtForEntries(b, finalizeExpr ctx src, body |> List.map (finalizeYamlEntry ctx))

and private finalizeYamlItem (ctx: Ctx) (item: TypedYamlTplItem) : TypedYamlTplItem =
    match item with
    | TYtItem t -> TYtItem(finalizeYamlTpl ctx t)
    | TYtDropItem(t, sp) -> TYtDropItem(finalizeYamlTpl ctx t, sp)
    | TYtForItems(b, src, body) -> TYtForItems(b, finalizeExpr ctx src, body |> List.map (finalizeYamlItem ctx))

/// anonymous shapes in the adapter slot register their canonical defs
/// [D:anon-records] — collected up front so field access on the result
/// type resolves anywhere in the statement; Script.checkStatement uses
/// the same extension to persist them into the outgoing env
let anonDefs (expr: Expr) : (string * TypeDef) list =
    let acc = System.Collections.Generic.Dictionary<string, TypeDef>()

    let rec walk (e: Expr) =
        (match e.Kind with
         | EFrom(_, Some(FromAnon fields), _, _)
         | EFrom(_, Some(FromMap(_, FromAnon fields)), _, _) ->
             let name = anonRecordName fields

             if not (acc.ContainsKey name) then
                 acc[name] <-
                     Record {
                         Name = name
                         Params = []
                         Fields = List.sortBy fst fields
                         Attrs = Map.empty
                         OrdKey = []
                         Docs = Map.empty
                     }
         | _ -> ())

        exprChildren e |> List.iter walk

    walk expr
    acc |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq

/// nested shapes drained from the parser's pending table
/// [D:anon-nesting] — same synthetic defs, same registration
let private pendingAnonRecords () : (string * TypeDef) list =
    Types.drainAnonDefs ()
    |> List.map (fun (name, fields) ->
        name,
        Record {
            Name = name
            Params = []
            Fields = List.sortBy fst fields
            Attrs = Map.empty
            OrdKey = []
            Docs = Map.empty
        })

let private withDefList (env: TypeEnv) (defs: (string * TypeDef) list) : TypeEnv =
    let held (n: string, d: TypeDef) =
        match env.Types.TryGetValue n with
        | true, d' -> obj.ReferenceEquals(d, d')
        | _ -> false

    match defs with
    | [] -> env
    | defs when List.forall held defs -> env
    | defs -> {
        env with
            Types = defs |> List.fold (fun ts (n, d) -> Map.add n d ts) env.Types
      }

let private lastAnonDefs =
    new System.Threading.ThreadLocal<System.WeakReference<Expr> * (string * TypeDef) list>(fun () ->
        System.WeakReference<Expr>(Unchecked.defaultof<Expr>), [])

let private anonDefsOf (expr: Expr) : (string * TypeDef) list =
    let memo, defs = lastAnonDefs.Value

    match memo.TryGetTarget() with
    | true, e when obj.ReferenceEquals(e, expr) -> defs
    | _ ->
        let defs = anonDefs expr
        lastAnonDefs.Value <- (System.WeakReference<Expr>(expr), defs)
        defs

let withAnonDefs (env: TypeEnv) (expr: Expr) : TypeEnv =
    withDefList env (anonDefsOf expr @ pendingAnonRecords ())

/// the anonymous shapes the last statement minted, registered now
/// rather than at the next statement's drain [D:anon-literals] — the
/// REPL's completion reads the session env between statements
let withPendingAnonDefs (env: TypeEnv) : TypeEnv = withDefList env (pendingAnonRecords ())

// budget exhaustion surfaces as an ordinary located TypeError; the
// text names what the author can act on, never the internal counter.
// The two messages are named so the whole-file collectors can recognize
// a budget diagnostic beside its single source and stop the multi-error
// fold on it (one burn bounds the file) [D:budget-stop-first].
let budgetMsgSized =
    "this expression's type grew too large to infer — split the expression into smaller bindings"

let budgetMsgUnsized =
    "type inference ran out of budget on this expression — split it into smaller bindings"

/// is this the message of a budget-exhaustion diagnostic? The predicate
/// lives beside the two strings it matches, so it cannot drift from them.
let isBudgetMessage (msg: string) : bool =
    msg = budgetMsgSized || msg = budgetMsgUnsized

let private budgetError (span: Span) (sized: bool) : TypeError = {
    Span = span
    Message = (if sized then budgetMsgSized else budgetMsgUnsized)
    Origin = None
}

let private catchBudget (f: unit -> Result<'a, TypeError>) : Result<'a, TypeError> =
    try
        f ()
    with BudgetExceeded(span, sized) ->
        Error(budgetError span sized)

let typecheckBinderCore
    (env: TypeEnv)
    (pat: Pattern)
    (expr: Expr)
    : Result<TypedExpr * (string * Scheme) list, TypeError> =
    let env = {
        withAnonDefs env expr with
            AnonLitDefs = System.Collections.Generic.Dictionary()
    }

    let ctx = newCtx ()

    // binder first, unless a case in it needs the value's type
    // [D:qualified-case-patterns] — ELetPat's order
    let shaped =
        if binderNeedsValueType env pat then
            infer ctx env expr
            |> Result.bind (fun te -> binderShapeAt ctx env (Some te.Ty) pat |> Result.map (fun sb -> sb, Some te))
        else
            binderShape ctx env pat |> Result.map (fun sb -> sb, None)

    match shaped with
    | Error e -> Error e
    | Ok((shape, binds), inferred) ->
        match
            (match inferred with
             | Some te -> Ok te
             | None -> infer ctx env expr)
        with
        | Error e -> Error e
        | Ok te ->
            match bind ctx env pat.PSpan shape te.Ty with
            | Error e -> Error e
            | Ok() ->
                let generic =
                    (binds |> List.map (fun (_, ty) -> tyVars (finalTy ctx ty)) |> Set.unionMany)
                    - envFreeVars ctx env

                match resolvePendingSplices ctx env generic with
                | Error e -> Error e
                | Ok() ->
                    let schemes = binds |> List.map (generalizeBinding ctx env)
                    let te = finalizeExpr ctx te

                    let stranded =
                        ctx.Cons
                        |> Map.toList
                        |> List.tryPick (fun (v, ps) ->
                            match resolve ctx (TVar v) with
                            // hole-descended vars stay silent — the
                            // real error was already reported
                            // [PLAN-diagnostics-arc B6]
                            | TVar u
                            | TRowVar(u, _) when not (u.StartsWith "__hole") -> ps |> List.tryHead
                            | _ -> None)

                    match stranded with
                    | Some p -> err p.Span (strandedMessage p.Cls)
                    | None -> Ok(te, schemes)

let typecheckBinder (env: TypeEnv) (pat: Pattern) (expr: Expr) : Result<TypedExpr * (string * Scheme) list, TypeError> =
    catchBudget (fun () -> typecheckBinderCore env pat expr)

let typecheckWithCore
    (env: TypeEnv)
    (expr: Expr)
    : Result<
        TypedExpr * Map<string, Set<Cls>> * Map<string, (string * int * int * int) list> * (int * int) list,
        TypeError
       >
    =
    let env = {
        withAnonDefs env expr with
            AnonLitDefs = System.Collections.Generic.Dictionary()
    }

    let ctx = newCtx ()

    match infer ctx env expr with
    | Error e -> Error e
    | Ok te ->
        // the result's free vars generalize (the SLet scheme takes the
        // residue below) — a hole on one stays generic [D:hole-generic]
        let generic = tyVars (finalTy ctx te.Ty) - envFreeVars ctx env

        match resolvePendingSplices ctx env generic with
        | Error e -> Error e
        | Ok() ->

            let te = finalizeExpr ctx te
            let resultVars = tyVars te.Ty

            let openCons =
                ctx.Cons
                |> Map.toList
                |> List.collect (fun (v, ps) ->
                    match resolve ctx (TVar v) with
                    | TVar u
                    | TRowVar(u, _) when not (u.StartsWith "__hole") -> ps |> List.map (fun p -> u, p)
                    | _ -> [])

            match openCons |> List.tryFind (fun (u, _) -> not (resultVars.Contains u)) with
            | Some(_, p) -> err p.Span (strandedMessage p.Cls)
            | None ->
                let residue =
                    openCons
                    |> List.groupBy fst
                    |> List.map (fun (u, ps) -> u, ps |> List.map (fun (_, p) -> p.Cls) |> Set.ofList)
                    |> Map.ofList

                // origins ride out with the residue [D:row-provenance]:
                // the SLet scheme is built outside this ctx
                Ok(te, residue, rowOriginsFor ctx resultVars, List.rev ctx.HoleDefaulted)

// every TypedExpr embedded in a typed yaml template — splices, key
// splices, and for sources
let rec yamlTplTypedExprs (tpl: TypedYamlTpl) : TypedExpr list =
    match tpl with
    | TYtScalar _ -> []
    | TYtBlock _ -> []
    | TYtDrop _ -> []
    | TYtSplice e -> [ e ]
    | TYtSeq(items, _) ->
        items
        |> List.collect (function
            | TYtItem t -> yamlTplTypedExprs t
            | TYtDropItem(t, _) -> yamlTplTypedExprs t
            | TYtForItems(_, src, body) ->
                src
                :: (body
                    |> List.collect (fun i -> yamlTplTypedExprs (TYtSeq([ i ], Unchecked.defaultof<Ast.Span>)))))
    | TYtMap(entries, _) ->
        entries
        |> List.collect (function
            | TYtPair(TYtKeyLit _, v) -> yamlTplTypedExprs v
            | TYtPair(TYtKeySplice k, v) -> k :: yamlTplTypedExprs v
            | TYtForEntries(_, src, body) ->
                src
                :: (body
                    |> List.collect (fun e -> yamlTplTypedExprs (TYtMap([ e ], Unchecked.defaultof<Ast.Span>)))))

// the typed tree's child list — tooling walks (LSP hover, command-head
// collection) share this instead of re-deriving the case list
let childExprs (te: TypedExpr) : TypedExpr list =
    match te.Kind with
    | TEInt _
    | TEStr _
    | TEBool _
    | TEUnit
    | TEHole _
    | TEVar _
    | TEEnvLoad _
    | TEArgsLoad _
    | TEFrom _
    | TEFromYaml _
    | TEFromToml _
    | TETo _ -> []
    | TETry(b, _) -> [ b ]
    | TEOrFail(b, m, site) -> b :: (Option.toList m @ [ site ])
    | TELet(_, _, v, b) -> [ v; b ]
    | TELetPat(_, v, b) -> [ v; b ]
    | TELambda(_, _, b) -> [ b ]
    | TELambdaPat(_, b) -> [ b ]
    | TEApp(f, a) -> [ f; a ]
    | TEPipe(a, f) -> [ a; f ]
    | TEField(t, _) -> [ t ]
    | TESlice(t, lo, hi, _, _, _) -> t :: (Option.toList lo @ Option.toList hi)
    | TEBinOp(_, l, r) -> [ l; r ]
    | TERecord(_, fields) -> fields |> List.map snd
    | TEMatch(s, arms) -> s :: (arms |> List.collect (fun (_, g, b) -> (g |> Option.toList) @ [ b ]))
    | TEIf(cnd, t, e) -> cnd :: t :: Option.toList e
    | TEDur _ -> []
    | TEFloat _ -> []
    | TESize _ -> []
    | TERetry(_, o, w, b, u) ->
        [ o ]
        @ Option.toList w
        @ [ b ]
        @ (u |> Option.map (snd >> List.singleton) |> Option.defaultValue [])
    | TESeq(a, b) -> [ a; b ]
    | TEWithin(_, _, a, o, b) -> Option.toList a @ Option.toList o @ [ b ]
    | TEAlways(b, c) -> [ b; c ]
    | TEHold b -> [ b ]
    | TEList items -> items
    | TETuple items -> items
    | TECmd(h, args, envO) ->
        (match h with
         | THeadDyn(_, e) -> [ e ]
         | THeadLit _ -> [])
        @ args
        @ Option.toList envO
    | TESplat e -> [ e ]
    | TEUpdate(src, ups) -> src :: (ups |> List.map snd)
    | TEInterp parts ->
        parts
        |> List.choose (function
            | IExpr e -> Some e
            | IStr _ -> None)
    | TEYaml(tpl, _, _) -> yamlTplTypedExprs tpl

/// the first child `f` answers on, visiting in childExprs order
/// without building the list — the walks on the check path use this
/// and its derivatives; childExprs stays for the editor services
let tryPickChild (f: TypedExpr -> 'a option) (te: TypedExpr) : 'a option =
    let rec ofList (xs: TypedExpr list) =
        match xs with
        | [] -> None
        | x :: rest ->
            match f x with
            | Some _ as r -> r
            | None -> ofList rest

    let ofOpt (o: TypedExpr option) =
        match o with
        | Some e -> f e
        | None -> None

    let two (a: TypedExpr) (b: TypedExpr) =
        match f a with
        | Some _ as r -> r
        | None -> f b

    match te.Kind with
    | TEInt _
    | TEStr _
    | TEBool _
    | TEUnit
    | TEHole _
    | TEVar _
    | TEEnvLoad _
    | TEArgsLoad _
    | TEFrom _
    | TEFromYaml _
    | TEFromToml _
    | TETo _
    | TEDur _
    | TEFloat _
    | TESize _ -> None
    | TETry(b, _) -> f b
    | TEOrFail(b, m, site) ->
        match f b with
        | Some _ as r -> r
        | None ->
            match ofOpt m with
            | Some _ as r -> r
            | None -> f site
    | TELet(_, _, v, b) -> two v b
    | TELetPat(_, v, b) -> two v b
    | TELambda(_, _, b) -> f b
    | TELambdaPat(_, b) -> f b
    | TEApp(a, b) -> two a b
    | TEPipe(a, b) -> two a b
    | TEField(t, _) -> f t
    | TESlice(t, lo, hi, _, _, _) ->
        match f t with
        | Some _ as r -> r
        | None ->
            match ofOpt lo with
            | Some _ as r -> r
            | None -> ofOpt hi
    | TEBinOp(_, l, r) -> two l r
    | TERecord(_, fields) ->
        let rec go (xs: (string * TypedExpr) list) =
            match xs with
            | [] -> None
            | (_, e) :: rest ->
                match f e with
                | Some _ as r -> r
                | None -> go rest

        go fields
    | TEMatch(s, arms) ->
        match f s with
        | Some _ as r -> r
        | None ->
            let rec go (xs: (Pattern * TypedExpr option * TypedExpr) list) =
                match xs with
                | [] -> None
                | (_, g, b) :: rest ->
                    match ofOpt g with
                    | Some _ as r -> r
                    | None ->
                        match f b with
                        | Some _ as r -> r
                        | None -> go rest

            go arms
    | TEIf(cnd, t, e) ->
        match two cnd t with
        | Some _ as r -> r
        | None -> ofOpt e
    | TERetry(_, o, w, b, u) ->
        match f o with
        | Some _ as r -> r
        | None ->
            match ofOpt w with
            | Some _ as r -> r
            | None ->
                match f b with
                | Some _ as r -> r
                | None ->
                    match u with
                    | Some(_, p) -> f p
                    | None -> None
    | TESeq(a, b) -> two a b
    | TEWithin(_, _, a, o, b) ->
        match ofOpt a with
        | Some _ as r -> r
        | None ->
            match ofOpt o with
            | Some _ as r -> r
            | None -> f b
    | TEAlways(b, c) -> two b c
    | TEHold b -> f b
    | TEList items -> ofList items
    | TETuple items -> ofList items
    | TECmd(h, args, envO) ->
        match
            (match h with
             | THeadDyn(_, e) -> f e
             | THeadLit _ -> None)
        with
        | Some _ as r -> r
        | None ->
            match ofList args with
            | Some _ as r -> r
            | None -> ofOpt envO
    | TESplat e -> f e
    | TEUpdate(src, ups) ->
        match f src with
        | Some _ as r -> r
        | None ->
            let rec go (xs: (string list * TypedExpr) list) =
                match xs with
                | [] -> None
                | (_, e) :: rest ->
                    match f e with
                    | Some _ as r -> r
                    | None -> go rest

            go ups
    | TEInterp parts ->
        let rec go (xs: InterpPart<TypedExpr> list) =
            match xs with
            | [] -> None
            | IExpr e :: rest ->
                match f e with
                | Some _ as r -> r
                | None -> go rest
            | IStr _ :: rest -> go rest

        go parts
    | TEYaml(tpl, _, _) -> ofList (yamlTplTypedExprs tpl)

let existsChild (f: TypedExpr -> bool) (te: TypedExpr) : bool =
    (tryPickChild (fun e -> if f e then Some() else None) te).IsSome

let forallChild (f: TypedExpr -> bool) (te: TypedExpr) : bool =
    (tryPickChild (fun e -> if f e then None else Some()) te).IsNone

let iterChildren (f: TypedExpr -> unit) (te: TypedExpr) : unit =
    let ofOpt (o: TypedExpr option) =
        match o with
        | Some e -> f e
        | None -> ()

    match te.Kind with
    | TEInt _
    | TEStr _
    | TEBool _
    | TEUnit
    | TEHole _
    | TEVar _
    | TEEnvLoad _
    | TEArgsLoad _
    | TEFrom _
    | TEFromYaml _
    | TEFromToml _
    | TETo _
    | TEDur _
    | TEFloat _
    | TESize _ -> ()
    | TETry(b, _) -> f b
    | TEOrFail(b, m, site) ->
        f b
        ofOpt m
        f site
    | TELet(_, _, v, b) ->
        f v
        f b
    | TELetPat(_, v, b) ->
        f v
        f b
    | TELambda(_, _, b) -> f b
    | TELambdaPat(_, b) -> f b
    | TEApp(a, b) ->
        f a
        f b
    | TEPipe(a, b) ->
        f a
        f b
    | TEField(t, _) -> f t
    | TESlice(t, lo, hi, _, _, _) ->
        f t
        ofOpt lo
        ofOpt hi
    | TEBinOp(_, l, r) ->
        f l
        f r
    | TERecord(_, fields) ->
        for _, e in fields do
            f e
    | TEMatch(s, arms) ->
        f s

        for _, g, b in arms do
            ofOpt g
            f b
    | TEIf(cnd, t, e) ->
        f cnd
        f t
        ofOpt e
    | TERetry(_, o, w, b, u) ->
        f o
        ofOpt w
        f b

        match u with
        | Some(_, p) -> f p
        | None -> ()
    | TESeq(a, b) ->
        f a
        f b
    | TEWithin(_, _, a, o, b) ->
        ofOpt a
        ofOpt o
        f b
    | TEAlways(b, c) ->
        f b
        f c
    | TEHold b -> f b
    | TEList items -> List.iter f items
    | TETuple items -> List.iter f items
    | TECmd(h, args, envO) ->
        (match h with
         | THeadDyn(_, e) -> f e
         | THeadLit _ -> ())

        List.iter f args
        ofOpt envO
    | TESplat e -> f e
    | TEUpdate(src, ups) ->
        f src

        for _, e in ups do
            f e
    | TEInterp parts ->
        for part in parts do
            match part with
            | IExpr e -> f e
            | IStr _ -> ()
    | TEYaml(tpl, _, _) -> List.iter f (yamlTplTypedExprs tpl)

// ---- unused bindings [D:unused-bindings] ----------------------------------
// The usage core: a scoped walk of the typed tree. Free names propagate
// upward minus each form's bound set; let-class binders (TELet/TELetPat)
// are tracked — an unread one collects as a located finding — while
// params, arm patterns, retry `until`, `within` and yaml `for` binders
// shadow only (the exempt classes). `let x = x + 1` reads the outer x:
// the RHS walks before the binder registers (no `let rec` exists).

/// every named binder in a pattern, with its span
let patNameSpans (p: Pattern) : (string * Span) list =
    let rec go (p: Pattern) =
        match p.PKind with
        | PVar n -> [ n, p.PSpan ]
        | _ -> patChildren p |> List.collect go

    go p

/// the escape [D:unused-bindings]: a '_'-prefixed name is
/// deliberately-unused and never errors
let unusedExempt (n: string) = n.StartsWith "_"

type UnusedLocal = {
    UName: string
    USpan: Span
    // a bare `_` as the whole let binder — the anonymous discard
    UBareWildcard: bool
}

/// free names used in a typed statement tree, plus its unused
/// block-local let binders (and bare-`_` block binders)
// a binder in scope during the usage walk: read or not
type private UsageFrame = { mutable Used: bool }

/// free names used in a typed statement tree, plus its unused
/// block-local let binders (and bare-`_` block binders) — one pass
/// with a scope of frames: a read marks the innermost binder of its
/// name, or is free; a binder left unread when its scope closes is a
/// finding (params, arm patterns, retry `until`, `within` and yaml
/// `for` binders shadow only). `let x = x + 1` reads the outer x: the
/// RHS walks before the binder enters (no `let rec` exists)
let bindingUsage (te: TypedExpr) : System.Collections.Generic.HashSet<string> * UnusedLocal list =
    let found = ResizeArray<UnusedLocal>()
    let free = System.Collections.Generic.HashSet<string>()

    let scope =
        System.Collections.Generic.Dictionary<string, System.Collections.Generic.Stack<UsageFrame>>()

    let push (n: string) : UsageFrame =
        let fr = { Used = false }

        match scope.TryGetValue n with
        | true, st -> st.Push fr
        | _ ->
            let st = System.Collections.Generic.Stack<UsageFrame>()
            st.Push fr
            scope[n] <- st

        fr

    let pop (n: string) = scope[n].Pop() |> ignore

    let shadowed (names: string list) (body: unit -> unit) =
        for n in names do
            push n |> ignore

        body ()

        for n in names do
            pop n

    let patNames (p: Pattern) = patNameSpans p |> List.map fst

    let rec walk (te: TypedExpr) : unit =
        match te.Kind with
        | TEVar n ->
            match scope.TryGetValue n with
            | true, st when st.Count > 0 -> st.Peek().Used <- true
            | _ -> free.Add n |> ignore
        | TELet(n, ns, v, b) ->
            walk v
            let fr = push n
            walk b
            pop n

            if not fr.Used && not (unusedExempt n) then
                found.Add {
                    UName = n
                    USpan = ns
                    UBareWildcard = false
                }
        | TELetPat(pat, v, b) ->
            walk v

            match pat.PKind with
            | PWildcard ->
                walk b

                found.Add {
                    UName = "_"
                    USpan = pat.PSpan
                    UBareWildcard = true
                }
            | _ ->
                let names = patNameSpans pat
                let frames = names |> List.map (fun (n, _) -> push n)
                walk b

                for n, _ in names do
                    pop n

                for (n, sp), fr in List.zip names frames do
                    if not fr.Used && not (unusedExempt n) then
                        found.Add {
                            UName = n
                            USpan = sp
                            UBareWildcard = false
                        }
        | TELambda(p, _, b) -> shadowed [ p ] (fun () -> walk b)
        | TELambdaPat(pat, b) -> shadowed (patNames pat) (fun () -> walk b)
        | TEMatch(s, arms) ->
            walk s

            for pat, g, b in arms do
                shadowed (patNames pat) (fun () ->
                    g |> Option.iter walk
                    walk b)
        | TERetry(_, o, w, b, u) ->
            walk o
            w |> Option.iter walk
            walk b

            match u with
            | Some((n, _), pred) -> shadowed [ n ] (fun () -> walk pred)
            | None -> ()
        | TEWithin(_, binder, a, o, b) ->
            a |> Option.iter walk
            o |> Option.iter walk

            match binder with
            | Some(n, _) -> shadowed [ n ] (fun () -> walk b)
            | None -> walk b
        | TEYaml(tpl, _, _) -> walkTpl tpl
        | _ -> iterChildren walk te

    and walkTpl (tpl: TypedYamlTpl) : unit =
        match tpl with
        | TYtScalar _
        | TYtBlock _
        | TYtDrop _ -> ()
        | TYtSplice e -> walk e
        | TYtSeq(items, _) -> List.iter walkItem items
        | TYtMap(entries, _) -> List.iter walkEntry entries

    and walkItem (item: TypedYamlTplItem) : unit =
        match item with
        | TYtItem t -> walkTpl t
        | TYtDropItem(t, _) -> walkTpl t
        | TYtForItems(binder, src, body) ->
            walk src
            shadowed (patNames binder) (fun () -> List.iter walkItem body)

    and walkEntry (entry: TypedYamlTplEntry) : unit =
        match entry with
        | TYtPair(TYtKeyLit _, v) -> walkTpl v
        | TYtPair(TYtKeySplice k, v) ->
            walk k
            walkTpl v
        | TYtForEntries(binder, src, body) ->
            walk src
            shadowed (patNames binder) (fun () -> List.iter walkEntry body)

    walk te
    free, List.ofSeq found

// ---- occurrences [D:lsp-rename] -------------------------------------
// The one scope walk the editor's references, highlight and rename read
// (bindingUsage's sibling: the same forms bind, the same patNameSpans
// leaves). Every user-named use carries the binder that owns it — the
// innermost enclosing binding form, as the checker scoped it — and
// every local binder site is listed once; a free name is TopLevel (a
// statement above binds it, or nothing in the file does).

type Binder =
    | Local of Span
    | TopLevel of string

type Occurrence = {
    Name: string
    Span: Span
    Binder: Binder
    IsBinderSite: bool
}

/// a name the editor may show: a user spelling no desugar introduced
/// (`_chain`, `|print`, the `_.field` wildcard, `()`); a bare uppercase
/// word is a union case in expression position, not a value name
let occurrenceName (n: string) : bool =
    isUserName n
    && not (n.StartsWith "_")
    && not (n.StartsWith "|")
    && not (System.Char.IsUpper n[0] && not (n.Contains '.'))

let occurrences (te: TypedExpr) : Occurrence list =
    let found = ResizeArray<Occurrence>()

    let site (n: string, sp: Span) =
        if occurrenceName n then
            found.Add {
                Name = n
                Span = sp
                Binder = Local sp
                IsBinderSite = true
            }

    let bindPat (pat: Pattern) (scope: Map<string, Span>) =
        patNameSpans pat
        |> List.fold
            (fun s (n, sp) ->
                site (n, sp)
                Map.add n sp s)
            scope

    let rec walk (scope: Map<string, Span>) (te: TypedExpr) : unit =
        match te.Kind with
        | TEVar n when occurrenceName n ->
            found.Add {
                Name = n
                Span = te.Span
                Binder =
                    (match Map.tryFind n scope with
                     | Some sp -> Local sp
                     | None -> TopLevel n)
                IsBinderSite = false
            }
        | TELet(n, ns, v, b) ->
            walk scope v
            site (n, ns)
            walk (Map.add n ns scope) b
        | TELetPat(pat, v, b) ->
            walk scope v
            walk (bindPat pat scope) b
        | TELambda(p, ps, b) ->
            site (p, ps)
            walk (Map.add p ps scope) b
        | TELambdaPat(pat, b) -> walk (bindPat pat scope) b
        | TEMatch(s, arms) ->
            walk scope s

            for pat, g, b in arms do
                let s2 = bindPat pat scope
                g |> Option.iter (walk s2)
                walk s2 b
        | TERetry(_, o, w, b, u) ->
            walk scope o
            w |> Option.iter (walk scope)
            walk scope b

            u
            |> Option.iter (fun ((n, ns), pred) ->
                site (n, ns)
                walk (Map.add n ns scope) pred)
        | TEWithin(_, binder, a, o, b) ->
            a |> Option.iter (walk scope)
            o |> Option.iter (walk scope)

            walk
                (match binder with
                 | Some(n, ns) ->
                     site (n, ns)
                     Map.add n ns scope
                 | None -> scope)
                b
        | TEYaml(tpl, _, _) -> walkTpl scope tpl
        | _ -> iterChildren (walk scope) te

    and walkTpl (scope: Map<string, Span>) (tpl: TypedYamlTpl) : unit =
        match tpl with
        | TYtScalar _
        | TYtBlock _
        | TYtDrop _ -> ()
        | TYtSplice e -> walk scope e
        | TYtSeq(items, _) ->
            for item in items do
                match item with
                | TYtItem t -> walkTpl scope t
                | TYtDropItem(t, _) -> walkTpl scope t
                | TYtForItems(binder, src, body) ->
                    walk scope src
                    let s2 = bindPat binder scope

                    for i in body do
                        walkTpl s2 (TYtSeq([ i ], Unchecked.defaultof<Ast.Span>))
        | TYtMap(entries, _) ->
            for entry in entries do
                match entry with
                | TYtPair(TYtKeyLit _, v) -> walkTpl scope v
                | TYtPair(TYtKeySplice k, v) ->
                    walk scope k
                    walkTpl scope v
                | TYtForEntries(binder, src, body) ->
                    walk scope src
                    let s2 = bindPat binder scope

                    for e in body do
                        walkTpl s2 (TYtMap([ e ], Unchecked.defaultof<Ast.Span>))

    walk Map.empty te
    List.ofSeq found |> List.distinct

let typecheckWith
    (env: TypeEnv)
    (expr: Expr)
    : Result<
        TypedExpr * Map<string, Set<Cls>> * Map<string, (string * int * int * int) list> * (int * int) list,
        TypeError
       >
    =
    catchBudget (fun () -> typecheckWithCore env expr)

let typecheck (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
    typecheckWith env expr |> Result.map (fun (te, _, _, _) -> te)

/// what a recovery pass produced [D:block-recovery]: a tree, nothing to
/// recover, or a spent budget. The pass is armed with a fresh budget, so
/// a caller folding over many statements MUST stop that fold on
/// RecoveryBudgetExhausted — otherwise one exhaustion per statement
/// multiplies across the file [D:budget-stop-first].
type RecoveryResult =
    | RecoveredExpr of TypedExpr * (TypeError * Span) list
    | NoRecovery
    | RecoveryBudgetExhausted

/// the recovery pass [D:block-recovery]: a tree for a statement that
/// already failed to check, with each failed block element a hole, and
/// the failures caught (first first). NoRecovery when nothing recovers —
/// the failure is the statement's own expression. The boundary checks
/// run only for the tree's sake: their failures are not this pass's to
/// report.
let typecheckRecovering (env: TypeEnv) (expr: Expr) : RecoveryResult =
    let env = {
        withAnonDefs env expr with
            AnonLitDefs = System.Collections.Generic.Dictionary()
    }

    let ctx = { newCtx () with Recovered = Some [] }

    try
        match infer ctx env expr with
        | Error _ -> NoRecovery
        | Ok te ->
            let generic = tyVars (finalTy ctx te.Ty) - envFreeVars ctx env
            resolvePendingSplices ctx env generic |> ignore
            let te = finalizeExpr ctx te
            RecoveredExpr(te, ctx.Recovered |> Option.defaultValue [] |> List.rev)
    with BudgetExceeded _ ->
        RecoveryBudgetExhausted

// what a binder pattern is, in words — the shape a payload failed to fit
let private binderWord (p: Pattern) : string =
    match p.PKind with
    | PTuple ps -> $"a {ps.Length}-tuple pattern"
    | PRecord _ -> "a record pattern"
    | PCase(c, _, q) -> $"a '{qualCase q c}' pattern"
    | PUnit -> "()"
    | _ -> "a pattern of another type"

/// a validated type's `validate` block, typed as the function
/// `payload -> unit`, refusing by raising [D:validated-types]: the binder
/// takes the payload's type (a mismatch names both), the body must be
/// unit (a mismatch names its type). `env` already holds the declaration.
let typecheckValidator (env: TypeEnv) (typeName: string) (v: Validator) : Result<TypedExpr, TypeError> =
    catchBudget (fun () ->
        let payload =
            match typeDefFor env typeName with
            | Some(Union { Cases = [ (_, Some p) ] }) -> p
            | _ -> failwith "unreachable: checkDecl admits validate on a single-case union with a payload"

        let want = TUnit

        let lam = {
            Kind = ELambdaPat(v.Binder, v.Body)
            Span = v.Span
        }

        let env = {
            withAnonDefs env lam with
                AnonLitDefs = System.Collections.Generic.Dictionary()
        }

        let ctx = newCtx ()

        result {
            let! shape, binds = binderShapeAt ctx env (Some payload) v.Binder

            do!
                bind ctx env v.Binder.PSpan shape payload
                |> Result.mapError (fun e -> {
                    e with
                        Message =
                            $"'validate' binds {typeName}'s payload, which is {formatTy payload}, but this binder is {binderWord v.Binder}"
                })

            let! tbody = infer ctx (bindParams env binds) v.Body

            do! bind ctx env tbody.Span want tbody.Ty

            // a validator cannot build its own type: that is recursion,
            // which weir does not have (no `let rec`)
            let own =
                match typeDefFor env typeName with
                | Some(Union { Validated = Some id }) -> createVarPrefix + id
                | _ -> ""

            let rec selfUse (te: TypedExpr) : Span option =
                match te.Kind with
                | TEVar n when n = own -> Some te.Span
                | _ -> tryPickChild selfUse te

            match selfUse tbody with
            | Some span ->
                return!
                    err
                        span
                        $"'validate' cannot call {typeName}.create — that is recursion, which weir does not have; validate the payload directly"
            | None ->

                match resolvePendingSplices ctx env Set.empty with
                | Error e -> return! Error e
                | Ok() ->
                    return
                        finalizeExpr ctx {
                            Kind = TELambdaPat(v.Binder, tbody)
                            Ty = TFun(payload, want)
                            Span = v.Span
                        }
        })

// ---- check-against-sig [D:module-signatures] ------------------------------
// A signed module member is checked with its signature's types flowing
// in (the lambda spine is checked bidirectionally: each param takes the
// signature's domain, so constructor patterns and operators inside the
// body resolve), never infer-then-compare. The caller (the module
// loader) owns the prose for the non-body failures — it knows both
// sites — so those travel as data.
type SigFailure =
    // an ordinary located error inside the implementation (sig types
    // already flowed in — the span is the honest place)
    | SigBody of TypeError
    // the implementation's shape does not unify with the signature
    | SigShape of implTy: Ty * span: Span
    // a signature type variable the implementation pins to a concrete
    // type — the implementation is less general than its signature
    | SigPinned of sigVar: string * pinned: Ty * span: Span
    // two signature type variables the implementation forces together
    | SigMerged of sigVar1: string * sigVar2: string * span: Span

let private typecheckAgainstSigCore (env: TypeEnv) (sigTy: Ty) (expr: Expr) : Result<TypedExpr * Scheme, SigFailure> =
    let env = {
        withAnonDefs env expr with
            AnonLitDefs = System.Collections.Generic.Dictionary()
    }

    let ctx = newCtx ()

    // instantiate the signature's vars as fresh unification vars and
    // remember the mapping: generality is judged at the end — a sig var
    // still resolving to an unbound var (distinct per sig var) is the
    // still-as-general verdict; anything else names what pinned it
    let sigVars = tyVars sigTy |> Set.toList
    let mapping = sigVars |> List.map (fun v -> v, freshName ctx "s")
    let instMap = mapping |> List.map (fun (v, f) -> v, TVar f) |> Map.ofList

    let rec inst (t: Ty) : Ty =
        match t with
        | TVar v -> Map.tryFind v instMap |> Option.defaultValue t
        | TFun(a, b) -> TFun(inst a, inst b)
        | TSeq t -> TSeq(inst t)
        | TTuple ts -> TTuple(List.map inst ts)
        | TNamed(n, args) -> TNamed(n, List.map inst args)
        | t -> t

    let expected0 = inst sigTy

    // the bidirectional spine: a lambda meets a function-shaped
    // expectation by taking the domain as its param type and checking
    // the body against the codomain; everything else infers and unifies
    // (`wrap` rebuilds the full implementation type for the mismatch
    // report — the domains are the signature's by construction)
    let rec go (env: TypeEnv) (expected: Ty) (e: Expr) (wrap: Ty -> Ty) : Result<TypedExpr, SigFailure> =
        let fallback () =
            match infer ctx env e with
            | Error terr -> Error(SigBody terr)
            | Ok te ->
                match bind ctx env e.Span expected te.Ty with
                | Ok() -> Ok te
                | Error _ -> Error(SigShape(finalTy ctx (wrap te.Ty), e.Span))

        match e.Kind, resolve ctx expected with
        | ELambda("()", pspan, body), TFun(dom, cod) ->
            (match bind ctx env pspan dom TUnit with
             | Ok() -> Ok()
             | Error _ -> Error(SigShape(finalTy ctx (wrap (TFun(TUnit, cod))), pspan)))
            |> Result.bind (fun () ->
                go env cod body (wrap << fun t -> TFun(TUnit, t))
                |> Result.map (fun tb -> {
                    Kind = TELambda("()", pspan, tb)
                    Ty = TFun(dom, tb.Ty)
                    Span = e.Span
                }))
        | ELambda(param, pspan, body), TFun(dom, cod) ->
            match checkBinderName e.Span param with
            | Error terr -> Error(SigBody terr)
            | Ok() ->
                go (bindParams env [ param, dom ]) cod body (wrap << fun t -> TFun(dom, t))
                |> Result.map (fun tb -> {
                    Kind = TELambda(param, pspan, tb)
                    Ty = TFun(dom, tb.Ty)
                    Span = e.Span
                })
        | ELambdaPat(pat, body), TFun(dom, cod) ->
            match binderShapeAt ctx env (Some dom) pat with
            | Error terr -> Error(SigBody terr)
            | Ok(shape, binds) ->
                match bind ctx env pat.PSpan dom shape with
                | Error _ -> Error(SigShape(finalTy ctx (wrap (TFun(shape, cod))), pat.PSpan))
                | Ok() ->
                    go (bindParams env binds) cod body (wrap << fun t -> TFun(dom, t))
                    |> Result.map (fun tb -> {
                        Kind = TELambdaPat(pat, tb)
                        Ty = TFun(dom, tb.Ty)
                        Span = e.Span
                    })
        | _ -> fallback ()

    match go env expected0 expr id with
    | Error f -> Error f
    | Ok te ->
        match resolvePendingSplices ctx env Set.empty with
        | Error terr -> Error(SigBody terr)
        | Ok() ->
            let te = finalizeExpr ctx te

            // generality: each sig var must still resolve to an unbound
            // var, and no two to the same one
            let resolved = mapping |> List.map (fun (v, f) -> v, finalTy ctx (TVar f))

            let pinned =
                resolved
                |> List.tryPick (fun (v, t) ->
                    match t with
                    | TVar _ -> None
                    | t -> Some(v, t))

            match pinned with
            | Some(v, t) -> Error(SigPinned(v, t, expr.Span))
            | None ->
                let varOf =
                    resolved
                    |> List.map (fun (v, t) ->
                        match t with
                        | TVar u -> u, v
                        | _ -> "", v)

                let merged =
                    varOf
                    |> List.groupBy fst
                    |> List.tryPick (fun (_, vs) ->
                        match vs with
                        | (_, v1) :: (_, v2) :: _ -> Some(v1, v2)
                        | _ -> None)

                match merged with
                | Some(v1, v2) -> Error(SigMerged(v1, v2, expr.Span))
                | None ->
                    // constraint residue rides the exported scheme keyed by
                    // the signature's own var names (the sig grammar spells
                    // no constraints; the implementation's demands are the
                    // truth) — a residue on any other var is stranded,
                    // exactly as the plain-let boundary rules
                    let backMap = varOf |> Map.ofList

                    let openCons =
                        ctx.Cons
                        |> Map.toList
                        |> List.collect (fun (v, ps) ->
                            match resolve ctx (TVar v) with
                            | TVar u
                            | TRowVar(u, _) when not (u.StartsWith "__hole") -> ps |> List.map (fun p -> u, p)
                            | _ -> [])

                    match openCons |> List.tryFind (fun (u, _) -> not (Map.containsKey u backMap)) with
                    | Some(_, p) ->
                        Error(
                            SigBody {
                                Span = p.Span
                                Message = (strandedMessage p.Cls)
                                Origin = None
                            }
                        )
                    | None ->
                        let cs =
                            openCons
                            |> List.groupBy (fun (u, _) -> backMap[u])
                            |> List.map (fun (v, ps) -> v, ps |> List.map (fun (_, p) -> p.Cls) |> Set.ofList)
                            |> Map.ofList

                        let scheme = {
                            Forall = Set.ofList sigVars
                            Cs = cs
                            Ty = sigTy
                            RowOrigins = Map.empty
                            HoleDefaults = []
                        }

                        Ok(te, scheme)

let typecheckAgainstSig (env: TypeEnv) (sigTy: Ty) (expr: Expr) : Result<TypedExpr * Scheme, SigFailure> =
    try
        typecheckAgainstSigCore env sigTy expr
    with BudgetExceeded(span, sized) ->
        Error(SigBody(budgetError span sized))


let rec private validateTy
    (env: TypeEnv)
    (selfName: string)
    (selfArity: int)
    (allowed: Set<string>)
    (span: Span)
    (ty: Ty)
    : Result<unit, TypeError> =
    match ty with
    | TInt
    | TFloat
    | TStr
    | TBool
    | TUnit
    | TDur
    | TInstant
    | TUuid
    | TSize
    | TBytes
    | TSecret -> Ok()
    | TSeq t -> validateTy env selfName selfArity allowed span t
    | TTuple ts -> allOk ts (validateTy env selfName selfArity allowed span)
    // Map is structural, keyed by any equatable type [D:map-keys]; the
    // wire's string-only law is the boundary's own check [D:map-string]
    | TNamed("Map", [ k; v ]) ->
        validateTy env selfName selfArity allowed span k
        |> Result.bind (fun () -> validateTy env selfName selfArity allowed span v)
        |> Result.bind (fun () -> mapKeyEq env span k)
    | TNamed("Map", args) -> err span $"Map takes two type arguments (Map<K, V>); got {List.length args}"
    | TFun(a, b) ->
        Result.bind
            (fun () -> validateTy env selfName selfArity allowed span b)
            (validateTy env selfName selfArity allowed span a)
    | TVar v ->
        if allowed.Contains v then
            Ok()
        else
            err span $"unknown type parameter '{v}; declare it: type X<'{v}> = ..."
    | TRowVar _ -> err span "row types are not allowed in declarations"
    | TNamed(n, targs) ->
        let arity =
            if n = selfName then
                Some selfArity
            // nameable like any nominal, as Map is (arity 0)
            elif List.contains n deflessBuiltinNominals then
                Some 0
            else
                match typeDefFor env n with
                | Some(Record d) -> Some d.Params.Length
                | Some(Union d) -> Some d.Params.Length
                | None -> None

        match arity with
        // F#/.NET collection names [D:fs-reflexes]: one sequence type
        | None when List.contains n [ "list"; "List"; "array"; "Array"; "ResizeArray" ] ->
            err span $"unknown type '{n}' — weir's one sequence type is seq<T> (seq<string>, seq<int>, ...)"
        | None -> err span (unknownTypeMsg env n)
        | Some a when a <> targs.Length -> err span $"'{n}' expects {a} type argument(s), got {targs.Length}"
        | Some _ -> allOk targs (validateTy env selfName selfArity allowed span)

// a signature's type validates like a declaration's [D:module-signatures]:
// unknown names and generic arities refuse at the sig; its type vars are
// implicitly quantified (every 'a is its own universal). Returns the env
// with any anonymous shapes the sig's parse minted, so they persist.
// Returns the type with its names resolved [D:qualified-type-names].
let validateSigTy (env: TypeEnv) (span: Span) (ty: Ty) : Result<TypeEnv * Ty, TypeError> =
    let env = withDefList env (pendingAnonRecords ())

    canonTy env span ty
    |> Result.bind (fun ty ->
        let env = withDefList env (pendingAnonRecords ())
        validateTy env "" 0 (tyVars ty) span ty |> Result.map (fun () -> env, ty))

// registered attribute names [D:attributes]: unknown names are check
// errors; registered-but-unconsumed is legal-and-inert. Validation
// happens at attachment; consumers bind at consumption.
// where an attribute may attach [D:attr-positions] — the registry is
// position-scoped: a registered name in the wrong position teaches its
// home; an unknown name keeps the did-you-mean
type private AttrPos =
    | FieldPos
    | RecordDeclPos
    | UnionDeclPos
    | CasePos

let private attrPosName =
    function
    | FieldPos -> "a record field"
    | RecordDeclPos -> "a record declaration"
    | UnionDeclPos -> "a union declaration"
    | CasePos -> "a union case"

let private attrRegistry: Map<string, (AttrArg option -> string option) * AttrPos list> =
    Map.ofList (
        [
            "Short",
            ((function
             | Some(AStr "h") -> Some "argument 'h' is reserved for --help"
             // a short flag is `-` plus a letter or digit: `-`, a space or
             // other punctuation is no flag the argv scanner can match. A
             // digit stays legal — a signature describes foreign CLIs
             // (`ls -1`), and the sig generator writes them
             // [D:short-alnum]
             | Some(AStr s) when s.Length = 1 && System.Char.IsLetterOrDigit s[0] -> None
             | Some(AStr s) when s.Length = 1 ->
                 Some $"expects a letter or digit, e.g. [<Short \"c\">] — '-{s}' can never be matched as a flag"
             | _ -> Some "expects a one-character string, e.g. [<Short \"c\">]"),
             [ FieldPos ])
            "NoShort",
            ((function
             | None -> None
             | Some _ -> Some "takes no argument"),
             [ FieldPos ])
            // [<Positional>] exists for signatures [D:command-signatures]:
            // foreign CLIs have operands and a signature describes theirs
            // (the earlier removal concerned weir's own CLIs); inert
            // everywhere else, per the attribute law
            "Positional",
            ((function
             | None -> None
             | Some _ -> Some "takes no argument"),
             [ FieldPos ])
            // [<Doc>] is retired [D:doc-help] — a `///` above the field is the one
            // source; a stale `[<Doc "x">]` is now the ordinary unknown-attribute
            // error (the did-you-mean over the remaining names).
            "Default",
            ((function
             | Some(AStr _ | AInt _ | ABool _ | ADur _ | AFloat _ | ASize _) -> None
             | None -> Some "expects a literal (string, int, float, bool, duration, or size), e.g. [<Default 10>]"),
             [ FieldPos ])
            // the wire key [D:wire-keys]: reserved words and illegal
            // identifiers are ordinary JSON/YAML keys — the field keeps a
            // weir name, the attribute names the wire; a union case's tag
            // value rides the same name [D:wire-unions]
            "Wire",
            ((function
             | Some(AStr s) when s <> "" -> None
             | Some(AStr _) ->
                 Some
                     "refuses an empty wire key — name the key the field reads and writes, e.g. [<Wire \"type\">] kind: string"
             | _ -> Some "expects the wire key as a string, e.g. [<Wire \"type\">] kind: string"),
             [ FieldPos; CasePos ])
            // the XML boundary markers [D:from-xml]: [<Attr>] reads a field
            // from an attribute (name defaults to the field), [<Elem "X">]
            // names the repeated element a seq< > field reads. Inert on the
            // json/yaml adapters — an XML-only field annotation.
            "Attr",
            ((function
             | None -> None
             | Some(AStr s) when s <> "" -> None
             | _ -> Some "expects nothing or the attribute name, e.g. [<Attr>] or [<Attr \"Include\">]"),
             [ FieldPos ])
            "Elem",
            ((function
             | Some(AStr s) when s <> "" -> None
             | _ -> Some "expects the element name as a string, e.g. [<Elem \"ProjectReference\">] refs: seq<Ref>"),
             [ FieldPos ])
            // [D:wire-unions] pre-registration [D:attr-positions]: Tag and
            // Other validate here and bind at the boundary — the
            // attribute law's shape (validation at attachment, binding at
            // consumption)
            "Tag",
            ((function
             | Some(AStr s) when s <> "" -> None
             | _ -> Some "expects the discriminator field as a string, e.g. [<Tag \"kind\">]"),
             [ UnionDeclPos ])
            "Other",
            ((function
             | None -> None
             | Some _ -> Some "takes no argument"),
             [ CasePos ])
            // the derived ordering [D:derived-ord]: a position per field
            // or case — the key is written on its members, so reordering
            // a declaration cannot change it
            "Ord",
            ((function
             | Some(AInt n) when n >= 1L -> None
             | _ -> Some "expects a positive int, e.g. [<Ord 1>]"),
             [ FieldPos; CasePos ])
        ]
        // the codec attributes [D:wire-codecs], one per Codec case — the
        // registry is derived, so it cannot drift from the union
        @ (allCodecs
           |> List.map (fun c ->
               codecName c,
               ((function
                | None -> None
                | Some _ -> Some "takes no argument"),
                [ FieldPos ])))
    )

/// the registered attribute names — what attrDocs must explain [D:attributes]
let attributeNames: Set<string> = attrRegistry |> Map.keys |> Set.ofSeq

// two fields resolving to one wire key is nonsense on every adapter —
// refused at the declaration, not discovered at the boundary
// [D:wire-keys]
let private validateWireCollisions (recName: string) (fields: (string * Ty * AttrSpec list) list) =
    // a collision always involves at least one [<Wire>] (field names
    // are unique), so one side carries a span — report there
    let wireOf (name: string, _: Ty, specs: AttrSpec list) =
        specs
        |> List.tryPick (fun a ->
            match a.AName, a.AArg with
            | "Wire", Some(AStr s) -> Some(s, Some a.ASpan)
            | _ -> None)
        |> Option.defaultValue (name, None)

    let rec go (seen: Map<string, string * Span option>) rest =
        match rest with
        | [] -> Ok()
        | f :: tail ->
            let name, _, _ = f
            let wire, spanO = wireOf f

            match Map.tryFind wire seen with
            | Some(prior, priorSpanO) ->
                match spanO |> Option.orElse priorSpanO with
                | Some span ->
                    err
                        span
                        $"two fields of {recName} share the wire key '{wire}' ('{prior}' and '{name}') — wire identity must be unambiguous"
                | None -> Ok() // two plain fields cannot share a name
            | None -> go (Map.add wire (name, spanO) seen) tail

    go Map.empty fields

let private validateAttrsAt (pos: AttrPos) (owner: string) (specs: AttrSpec list) =
    let conflicts a b (seen: Set<string>) (spec: AttrSpec) = spec.AName = a && Set.contains b seen

    let rec go seen specs =
        match specs with
        | [] -> Ok()
        | (a: AttrSpec) :: rest ->
            if Set.contains a.AName seen then
                err a.ASpan $"duplicate attribute '{a.AName}' on {owner}"
            elif conflicts "Short" "NoShort" seen a || conflicts "NoShort" "Short" seen a then
                err a.ASpan $"{owner} has both Short and NoShort"
            else
                match Map.tryFind a.AName attrRegistry with
                | None ->
                    let hint = didYouMean a.AName (Map.keys attrRegistry)
                    err a.ASpan $"unknown attribute '{a.AName}'{hint}"
                | Some(validate, positions) ->
                    if not (List.contains pos positions) then
                        // the wrong position names the home, not did-you-mean
                        // [D:attr-positions]
                        let homes = positions |> List.map attrPosName |> String.concat " or "
                        err a.ASpan $"'{a.AName}' attaches to {homes}, not {attrPosName pos}"
                    else
                        match validate a.AArg with
                        | Some msg -> err a.ASpan $"'{a.AName}' {msg}"
                        | None -> go (Set.add a.AName seen) rest

    go Set.empty specs

// the type a codec encodes [D:wire-codecs] — exhaustive, so a new codec
// states its type; the wire table lists the same codecs under it (a unit
// test holds the two together)
let codecTy (c: Codec) : Ty =
    match c with
    | Iso8601
    | EpochMs
    | EpochSec -> TInstant
    | Millis
    | Seconds -> TDur
    | ByteCount -> TSize

/// the wire table's codecs for a type, for that unit test
let codecsOfTy (ty: Ty) : Codec list = codecsOf ty

// a codec fits its field at the declaration [D:wire-codecs]: the
// declaration alone decides, so a misfit is caught even on a record that
// never crosses a boundary (XML's [<Attr>] checks only at consumption)
let private validateFieldCodec (field: string) (fty: Ty) (specs: AttrSpec list) =
    match specs |> List.filter (fun a -> (codecOfName a.AName).IsSome) with
    | [] -> Ok()
    | a :: b :: _ ->
        err b.ASpan $"field '{field}' declares two encodings ([<{a.AName}>] and [<{b.AName}>]) — one codec per field"
    | [ a ] ->
        let c = (codecOfName a.AName).Value
        let fits = codecsOf fty

        if List.contains c fits then
            Ok()
        else
            match wireStance (codecTarget fty) with
            // the type itself refuses — no encoding changes that
            | Refused teach ->
                let why = teach "JSON or YAML"
                err a.ASpan $"[<{a.AName}>] cannot apply to field '{field}': {why}"
            | stance ->
                let hint =
                    match stance with
                    | Encoded(cs, _, _) -> $" — its encodings: {codecAttrs cs}"
                    | Canonical _ -> $" — {formatTy (codecTarget fty)} crosses as itself, with no codec"
                    | _ -> ""

                err
                    a.ASpan
                    $"[<{a.AName}>] encodes {formatTy (codecTy c)} (or Option/seq of it), but field '{field}' is {formatTy fty}{hint}"

// the `[<Ord n>]` key of a declaration's members [D:derived-ord]: the
// numbered names by ascending n, each position held once — two members
// on one position refuse at the second, naming both
let private ordKey
    (owner: string)
    (kind: string)
    (members: (string * AttrSpec list) list)
    : Result<(string * int64 * Span) list, TypeError> =
    let numbered =
        members
        |> List.choose (fun (m, specs) ->
            specs
            |> List.tryPick (fun a ->
                match a.AName, a.AArg with
                | "Ord", Some(AInt n) -> Some(m, n, a.ASpan)
                | _ -> None))

    let rec go (seen: Map<int64, string>) items =
        match items with
        | [] -> Ok(numbered |> List.sortBy (fun (_, n, _) -> n))
        | (m, n, span) :: rest ->
            match Map.tryFind n seen with
            | Some first ->
                err
                    span
                    $"[<Ord {n}>] on '{owner}': {kind}s '{first}' and '{m}' share position {n} — number each {kind} once"
            | None -> go (Map.add n m seen) rest

    go Map.empty numbered

let private validateFieldAttrs (_: string) (field: string, fty: Ty, specs: AttrSpec list) =
    validateAttrsAt FieldPos $"field '{field}'" specs
    |> Result.bind (fun () -> validateFieldCodec field fty specs)

let private validateShortCollisions (fields: (string * Ty * AttrSpec list) list) =
    let explicitShorts =
        fields
        |> List.collect (fun (f, _, specs) ->
            specs
            |> List.choose (fun a ->
                match a.AName, a.AArg with
                | "Short", Some(AStr s) -> Some(s, f, a.ASpan)
                | _ -> None))

    let rec go seen shorts =
        match shorts with
        | [] -> Ok()
        | (s: string, f: string, sp) :: rest ->
            match Map.tryFind s seen with
            | Some prev -> err sp $"duplicate short '-{s}' (fields '{prev}' and '{f}')"
            | None -> go (Map.add s f seen) rest

    go Map.empty explicitShorts

// the prelude replays its own declarations on every extend (tests build
// envs repeatedly) — exempt it; the ThreadLocal keeps parallel test
// runs isolated (the toPhys pattern)
let preludeLoading: System.Threading.ThreadLocal<bool> =
    new System.Threading.ThreadLocal<bool>(fun () -> false)

// a declaration's field and payload types with their names resolved
// [D:qualified-type-names]; the type's own name (self-reference) stays
let private canonDecl (env: TypeEnv) (decl: Decl) : Result<Decl, TypeError> =
    let one (ty: Ty) =
        canonTyKeeping decl.Name env decl.Span ty

    match decl.Body with
    | DRecord fields ->
        fields
        |> List.fold
            (fun acc (n, t, a) -> acc |> Result.bind (fun xs -> one t |> Result.map (fun t' -> (n, t', a) :: xs)))
            (Ok [])
        |> Result.map (fun fs -> {
            decl with
                Body = DRecord(List.rev fs)
        })
    | DUnion cases ->
        cases
        |> List.fold
            (fun acc (c, p, a) ->
                acc
                |> Result.bind (fun xs ->
                    match p with
                    | Some t -> one t |> Result.map (fun t' -> (c, Some t', a) :: xs)
                    | None -> Ok((c, None, a) :: xs)))
            (Ok [])
        |> Result.map (fun cs -> { decl with Body = DUnion(List.rev cs) })

// the id a validated type's derived `create` is registered under at run
// time [D:validated-types] — fresh per checked declaration
let mutable private validatorSeq = 0

// where `validate` cannot go [D:validated-types]: one case, with a
// payload, monomorphic, untagged — None when the union may declare it
let private validateRefusal (decl: Decl) (cases: (string * Ty option * AttrSpec list) list) : string option =
    match cases with
    | [ (c, None, _) ] ->
        Some
            $"'validate' checks a payload, and case '{c}' carries none — a validated type wraps a value: type {decl.Name} = {c} of string"
    | [ _ ] when not decl.Params.IsEmpty ->
        Some $"a validated type is monomorphic — '{decl.Name}' is generic; validate a concrete payload"
    | [ _ ] when decl.Attrs |> List.exists (fun a -> a.AName = "Tag") ->
        Some "[<Tag>] and 'validate' do not combine — a validated type crosses the wire as its payload, with no tag"
    | [ _ ] -> None
    | _ ->
        Some
            $"'validate' guards a single-case union — '{decl.Name}' has {cases.Length} cases; a union of several cases composes validated payloads (Mail of Email | Phone of Phone) and needs no validator of its own"

let checkDecl (env: TypeEnv) (decl: Decl) : Result<TypeEnv, TypeError> =
    // a declared record's anon field shapes drain here [D:anon-nesting]
    // — registered before validation so the names resolve, and they
    // ride the returned env like any registration
    let env = withDefList env (pendingAnonRecords ())

    if not preludeLoading.Value && builtinTypeNames.ContainsKey decl.Name then
        err decl.Span $"'{decl.Name}' is a built-in type — pick another name"
    else
        match canonDecl env decl with
        | Error e -> Error e
        | Ok decl ->

            let env = withDefList env (pendingAnonRecords ())
            let allowed = Set.ofList decl.Params
            let selfArity = decl.Params.Length
            let selfTy = TNamed(decl.Name, decl.Params |> List.map TVar)

            match firstDup decl.Params with
            | Some dup -> err decl.Span $"duplicate type parameter '{dup}"
            | None ->
                match decl.Body with
                | DRecord _ when decl.Validate.IsSome ->
                    err
                        decl.Validate.Value.Span
                        $"'validate' guards a single-case union (type Email = Email of string), not a record — '{decl.Name}' composes validated values instead: give a field a validated type and the record holds only valid ones"
                | DUnion cases when decl.Validate.IsSome && validateRefusal decl cases |> Option.isSome ->
                    err decl.Validate.Value.Span (validateRefusal decl cases).Value
                | DRecord fields ->
                    result {
                        match firstDup (fields |> List.map (fun (n, _, _) -> n)) with
                        | Some dup -> return! err decl.Span $"duplicate field '{dup}'"
                        | None ->
                            // no registered attribute attaches to a record
                            // declaration [D:attr-positions] — each teaches its home
                            do! validateAttrsAt RecordDeclPos $"record '{decl.Name}'" decl.Attrs

                            let plain = fields |> List.map (fun (n, t, _) -> n, t)

                            do! allOk plain (snd >> validateTy env decl.Name selfArity allowed decl.Span)
                            do! allOk fields (validateFieldAttrs decl.Name)
                            do! validateShortCollisions fields
                            do! validateWireCollisions decl.Name fields

                            let attrs =
                                fields
                                |> List.choose (fun (n, _, specs) ->
                                    if List.isEmpty specs then
                                        None
                                    else
                                        Some(n, specs |> List.map (fun a -> a.AName, a.AArg)))
                                |> Map.ofList

                            let! ordered =
                                ordKey decl.Name "field" (fields |> List.map (fun (f, _, specs) -> f, specs))

                            let def =
                                Record {
                                    Name = decl.Name
                                    Params = decl.Params
                                    Fields = plain
                                    Attrs = attrs
                                    OrdKey = ordered |> List.map (fun (f, _, _) -> f)
                                    // the runner enriches this from the `///` docs
                                    // after checkDecl [D:doc-help]
                                    Docs = Map.empty
                                }

                            let env' = {
                                env with
                                    Types = Map.add decl.Name def env.Types
                            }

                            // a numbered field must itself order, or the
                            // DECLARATION refuses naming it — never a sort site
                            // [D:derived-ord]; an unnumbered field takes no part
                            // (the partial-key law) and is unconstrained; the def
                            // is registered first so a self-typed field admits
                            do!
                                allOk ordered (fun (f, n, span) ->
                                    let ft = plain |> List.find (fst >> (=) f) |> snd

                                    match ordOffender env' Set.empty ft with
                                    | None -> Ok()
                                    | Some off ->
                                        err
                                            span
                                            $"[<Ord {n}>] on '{decl.Name}': field '{f}' is {formatTy ft}, which cannot be ordered — {ordTail env' false off}")

                            // a runtime record value carries only its record
                            // name, and the key comes from a per-process
                            // registry keyed by that name [D:derived-ord] — so
                            // an ordered record one checker can see under the
                            // same name (an import moved to its qualified key,
                            // [D:qualified-type-names]) must agree on the key
                            do!
                                if ordered.IsEmpty then
                                    Ok()
                                else
                                    let key = ordered |> List.map (fun (f, _, _) -> f)

                                    env.Types
                                    |> Map.tryPick (fun k d ->
                                        match d with
                                        | Record d when
                                            k <> decl.Name
                                            && d.Name = decl.Name
                                            && not d.OrdKey.IsEmpty
                                            && d.OrdKey <> key
                                            ->
                                            Some(k, d.OrdKey)
                                        | _ -> None)
                                    |> function
                                        | Some(other, otherKey) ->
                                            err
                                                decl.Span
                                                $"""[<Ord n>] on '{decl.Name}': the imported record '{other}' is ordered by {String.concat ", " otherKey} — a value carries only its record name, so two ordered records named '{decl.Name}' must agree on the key; rename it"""
                                        | None -> Ok()

                            return env'
                    }
                | DUnion cases ->
                    result {
                        match firstDup (cases |> List.map (fun (n, _, _) -> n)) with
                        | Some dup -> return! err decl.Span $"duplicate case '{dup}'"
                        | None ->
                            do! validateAttrsAt UnionDeclPos $"union '{decl.Name}'" decl.Attrs

                            do! allOk cases (fun (c, _, specs) -> validateAttrsAt CasePos $"case '{c}'" specs)

                            // at most one fallback case [D:wire-unions] — two
                            // wildcards cannot both catch
                            do!
                                match
                                    cases
                                    |> List.collect (fun (_, _, specs) ->
                                        specs |> List.filter (fun a -> a.AName = "Other"))
                                with
                                | _ :: second :: _ ->
                                    err
                                        second.ASpan
                                        $"union '{decl.Name}' declares [<Other>] twice — one fallback case catches everything unmatched"
                                | _ -> Ok()

                            do!
                                allOk cases (fun (_, payload, _) ->
                                    match payload with
                                    | Some ty -> validateTy env decl.Name selfArity allowed decl.Span ty
                                    | None -> Ok())

                            // the tag binds here [D:wire-unions] — the union
                            // becomes a wire type, and the declaration laws
                            // fire only when [<Tag>] is present
                            let tag =
                                decl.Attrs
                                |> List.tryPick (fun a ->
                                    match a.AName, a.AArg with
                                    | "Tag", Some(AStr s) -> Some s
                                    | _ -> None)

                            let! ordered =
                                ordKey decl.Name "case" (cases |> List.map (fun (c, _, specs) -> c, specs))

                            // every case numbered or none [D:derived-ord] — an
                            // unplaced case would have no position
                            do!
                                if ordered.IsEmpty || ordered.Length = cases.Length then
                                    Ok()
                                else
                                    let unplaced =
                                        cases
                                        |> List.find (fun (c, _, _) ->
                                            not (ordered |> List.exists (fun (c', _, _) -> c' = c)))
                                        |> fun (c, _, _) -> c

                                    err
                                        decl.Span
                                        $"[<Ord n>] on '{decl.Name}': case '{unplaced}' has no position — number every case or none"

                            let caseAttr name (specs: AttrSpec list) =
                                specs |> List.tryFind (fun a -> a.AName = name)

                            do!
                                match tag with
                                | None ->
                                    // a case attr with no [<Tag>] binds nothing —
                                    // refused, naming the missing half
                                    cases
                                    |> List.tryPick (fun (_, _, specs) ->
                                        caseAttr "Wire" specs |> Option.orElse (caseAttr "Other" specs))
                                    |> function
                                        | Some a ->
                                            err
                                                a.ASpan
                                                $"'{a.AName}' on a case needs [<Tag \"…\">] on the union declaration — the tag is what gives a case a wire identity"
                                        | None -> Ok()
                                | Some tagField ->
                                    result {
                                        do!
                                            if decl.Params.IsEmpty then
                                                Ok()
                                            else
                                                err
                                                    decl.Span
                                                    $"a tagged union is monomorphic — '{decl.Name}' is generic, and the wire boundary needs concrete cases"

                                        // the writers key VUnion values by case name
                                        // [D:wire-unions] — Option's and Yaml's own
                                        // encodings must stay unmistakable
                                        do!
                                            match
                                                cases
                                                |> List.tryFind (fun (c, _, _) ->
                                                    [
                                                        "Some"
                                                        "None"
                                                        "YStr"
                                                        "YInt"
                                                        "YFloat"
                                                        "YBool"
                                                        "YNull"
                                                        "YSeq"
                                                        "YMap"
                                                    ]
                                                    |> List.contains c)
                                            with
                                            | Some(c, _, _) ->
                                                err
                                                    decl.Span
                                                    $"'{c}' is the wire spelling of a builtin (Option / Yaml nodes) — a tagged union's case needs another name"
                                            | None -> Ok()
                                        // every case: a record payload (or nullary);
                                        // the [<Other>] case: string or nullary
                                        do!
                                            allOk cases (fun (c, payload, specs) ->
                                                if (caseAttr "Other" specs).IsSome then
                                                    match payload with
                                                    | None
                                                    | Some TStr -> Ok()
                                                    | Some ty ->
                                                        err
                                                            decl.Span
                                                            $"the [<Other>] case carries the unmatched tag as a string (or nothing) — '{c}' carries {formatTy ty}"
                                                else
                                                    match payload with
                                                    | None -> Ok()
                                                    | Some(TNamed(pn, [])) ->
                                                        match typeDefFor env pn with
                                                        | Some(Record rdef) when rdef.Params.IsEmpty ->
                                                            // the tag rides the union — a payload
                                                            // spelling it (by name or wire key)
                                                            // would write it twice
                                                            match
                                                                rdef.Fields
                                                                |> List.tryFind (fun (f, _) ->
                                                                    Types.wireName rdef f = tagField)
                                                            with
                                                            | Some(f, _) ->
                                                                err
                                                                    decl.Span
                                                                    $"the tag rides the union, not the payload — record '{pn}' declares '{f}' (wire key '{tagField}'), which is '{decl.Name}''s tag field"
                                                            | None -> Ok()
                                                        | Some(Record _) ->
                                                            err
                                                                decl.Span
                                                                $"a tagged union's payload is monomorphic — '{pn}' is generic"
                                                        | _ ->
                                                            err
                                                                decl.Span
                                                                $"a tagged union's case carries a declared record (or nothing) — '{c}' carries {formatTy (TNamed(pn, []))}"
                                                    | Some ty ->
                                                        err
                                                            decl.Span
                                                            $"a tagged union's case carries a declared record (or nothing) — '{c}' carries {formatTy ty}")

                                        // two cases resolving to one tag value —
                                        // the wire-key collision rule again
                                        let values =
                                            cases
                                            |> List.map (fun (c, _, specs) ->
                                                match caseAttr "Wire" specs with
                                                | Some { AArg = Some(AStr w) } -> c, w
                                                | _ -> c, c)

                                        match
                                            values |> List.groupBy snd |> List.tryFind (fun (_, xs) -> xs.Length > 1)
                                        with
                                        | Some(w, ((c1, _) :: (c2, _) :: _)) ->
                                            return!
                                                err
                                                    decl.Span
                                                    $"two cases of {decl.Name} share the tag value '{w}' ('{c1}' and '{c2}') — wire identity must be unambiguous"
                                        | _ -> return ()
                                    }

                            let def =
                                Union {
                                    Name = decl.Name
                                    Params = decl.Params
                                    Cases = cases |> List.map (fun (n, t, _) -> n, t)
                                    Tag = tag
                                    CaseWires =
                                        cases
                                        |> List.choose (fun (c, _, specs) ->
                                            match caseAttr "Wire" specs with
                                            | Some { AArg = Some(AStr w) } -> Some(c, w)
                                            | _ -> None)
                                        |> Map.ofList
                                    OtherCase =
                                        cases
                                        |> List.tryPick (fun (c, _, specs) ->
                                            if (caseAttr "Other" specs).IsSome then Some c else None)
                                    Validated =
                                        decl.Validate
                                        |> Option.map (fun _ ->
                                            $"{decl.Name}#{System.Threading.Interlocked.Increment &validatorSeq}")
                                    OrdKey = ordered |> List.map (fun (c, _, _) -> c)
                                    Docs = Map.empty
                                }

                            let env' = {
                                env with
                                    Types = Map.add decl.Name def env.Types
                            }

                            // every payload must itself order, or the DECLARATION
                            // refuses naming the case [D:derived-ord]; a tuple
                            // payload is a tuple, refused like any
                            do!
                                allOk ordered (fun (c, n, span) ->
                                    let payload =
                                        cases |> List.pick (fun (c', p, _) -> if c' = c then Some p else None)

                                    match payload |> Option.bind (ordOffender env' Set.empty) with
                                    | None -> Ok()
                                    | Some off ->
                                        err
                                            span
                                            $"[<Ord {n}>] on '{decl.Name}': case '{c}' carries {formatTy payload.Value}, which cannot be ordered — {ordTail env' false off}")

                            // the collision law [D:derived-ord]: a runtime union
                            // value carries only its case name, and the case
                            // index comes from a per-process registry keyed by
                            // that name — so two ordered unions one checker can
                            // see may not share a case (the prelude's Option is
                            // ordered by the solver's arm, so Some/None count);
                            // the same name is a REPL redeclaration, which
                            // replaces [D:dup-type-decl]
                            do!
                                if ordered.IsEmpty then
                                    Ok()
                                else
                                    let orderedUnion (d: UnionDef) =
                                        not d.OrdKey.IsEmpty || d.Name = "Option"

                                    cases
                                    |> List.tryPick (fun (c, _, _) ->
                                        env.Types
                                        |> Map.tryPick (fun key d ->
                                            match d with
                                            | Union d when
                                                key <> decl.Name
                                                && orderedUnion d
                                                && d.Cases |> List.exists (fun (c', _) -> c' = c)
                                                ->
                                                Some(c, d.Name)
                                            | _ -> None))
                                    |> function
                                        | Some(c, other) ->
                                            err
                                                decl.Span
                                                $"[<Ord n>] on '{decl.Name}': case '{c}' is also a case of the ordered union '{other}' — a value carries only its case name, so two ordered unions cannot share one; rename it"
                                        | None -> Ok()

                            let ctorTy payload =
                                match payload with
                                | None -> selfTy
                                | Some ty -> TFun(ty, selfTy)

                            let ctorScheme payload = {
                                Forall = allowed + tyVars (ctorTy payload)
                                Cs = Map.empty
                                Ty = ctorTy payload
                                RowOrigins = Map.empty
                                HoleDefaults = []
                            }

                            return
                                cases
                                |> List.fold (fun e (c, payload, _) -> bindValue c (ctorScheme payload) e) env'
                    }

// Advisory findings only — coverage and reachability are checker
// errors [D:exhaustiveness-hard-error].
let warnings (te: TypedExpr) : Warning list =
    // one collection site (command argv nudges); traversal is
    // childExprs' job
    let acc = ResizeArray<Warning>()

    let rec walk (te: TypedExpr) =
        (match te.Kind with
         | TECmd(_, args, _) ->
             // the bash prior-bleed family: ; does not chain, > / >>
             // do not redirect — warn, never block (a quoted literal
             // argument is legitimate)
             for a in args do
                 match a.Kind with
                 | TEStr ";" ->
                     acc.Add {
                         Span = a.Span
                         Message =
                             "';' does not chain commands in weir — put commands on separate lines "
                             + "(sequence unit expressions with ';' in expression position; "
                             + "if you meant a literal ';' argument, ignore this)"
                     }
                 | TEStr ">" ->
                     acc.Add {
                         Span = a.Span
                         Message =
                             "'>' does not redirect in weir — pipe to File.write: "
                             + "cmd |> File.write \"out.txt\" "
                             + "(if you meant a literal '>' argument, ignore this)"
                     }
                 | TEStr ">>" ->
                     acc.Add {
                         Span = a.Span
                         Message =
                             "'>>' does not redirect in weir — pipe to File.append: "
                             + "cmd |> File.append \"out.txt\" "
                             + "(if you meant a literal '>>' argument, ignore this)"
                     }
                 // the two commonest bash chaining glyphs
                 // (PLAN-dx-review D2): the pass-through is deliberate;
                 // staying silent about it would be the defect
                 | TEStr "&&" ->
                     acc.Add {
                         Span = a.Span
                         Message =
                             "'&&' does not chain commands in weir — write `cmd | and next`, or put the commands on "
                             + "separate lines (a failure stops the script; "
                             + "if you meant a literal '&&' argument, ignore this)"
                     }
                 | TEStr "||" ->
                     acc.Add {
                         Span = a.Span
                         Message =
                             "'||' does not chain commands in weir — write `cmd | or fallback`, or branch on the exit: "
                             + "if cmd | succeeds then ... else ... "
                             + "(if you meant a literal '||' argument, ignore this)"
                     }
                 | _ -> ()
         // `[1, 2]` is a one-item list holding a tuple — the comma habit
         // from Python/JS. Only a lone, unparenthesized tuple: a list of
         // several pairs (`["a", 1; "b", 2]`) is the F# idiom, and
         // `[(1, 2)]` says it on purpose (parens widen the span), and so
         // does a pair on its own line below the `[` — the multi-line,
         // one-entry-per-line layout of a pairs list
         | TEList [ { Kind = TETuple(first :: _) } as t ] when
             t.Span.Start = first.Span.Start
             && (match toPhys.Value with
                 | Some tr -> fst (tr te.Span.Start.Col) = fst (tr t.Span.Start.Col)
                 | None -> true)
             ->
             acc.Add {
                 Span = t.Span
                 Message =
                     "this list has one item, a tuple — list items are separated by ';' ([1; 2]); "
                     + "if you meant a list holding one tuple, write it in parentheses: [(1, 2)]"
             }
         | _ -> ())

        iterChildren walk te

    walk te
    List.ofSeq acc

// ---- possible re-enumeration [D:reenum-warning] ---------------------------
// A command-backed seq re-runs its command on every pull; a binding
// enumerated at two or more sites without a visible force is the lazy
// re-run hazard. The RHS judgement is the module weak-purity walk
// (runsCommandT — the [D:modules-v1] rule, moved here so the checker
// and the loader share one spelling): eager positions only, stops at
// lambdas — a command in a lambda body is deferred, so a param-ful
// `let f r = git …` is a function, not a command-backed value.

let rec runsCommandT (te: TypedExpr) : bool =
    match te.Kind with
    | TECmd _ -> true
    // a reifier desugars to an application of its |-variable, not a TECmd:
    // `cmd | line` runs cmd just the same [D:module-let-reifier]
    | TEVar v when (reifierOfVar v).IsSome -> true
    | TELambda _
    | TELambdaPat _ -> false
    | _ -> existsChild runsCommandT te

// the walk sees through the module's own helpers [D:modules-v1]: an
// earlier function whose body reaches a command (directly, or through
// another such helper) maps to its arity; a SATURATED call of one on the
// eager spine runs that command at import, exactly as the command itself
// would. A partial application builds a closure and runs nothing.
let rec private lambdaArity (te: TypedExpr) : int =
    match te.Kind with
    | TELambda(_, _, b)
    | TELambdaPat(_, b) -> 1 + lambdaArity b
    | _ -> 0

let rec private reachesCommand (helpers: Map<string, int>) (te: TypedExpr) : bool =
    match te.Kind with
    | TECmd _ -> true
    | TEVar v when (reifierOfVar v).IsSome || helpers.ContainsKey v -> true
    | TELet(n, _, v, b) -> reachesCommand helpers v || reachesCommand (mapDrop n helpers) b
    | _ -> existsChild (reachesCommand helpers) te

/// Some arity when a module binding is a function whose body reaches a
/// command — the summary the eager-call walk reads
let commandHelperArity (helpers: Map<string, int>) (te: TypedExpr) : int option =
    let a = lambdaArity te
    if a > 0 && reachesCommand helpers te then Some a else None

/// the helper a module let calls eagerly (saturated, outside any lambda)
let rec eagerCommandHelper (helpers: Map<string, int>) (te: TypedExpr) : string option =
    let rec spine (e: TypedExpr) (n: int) =
        match e.Kind with
        | TEApp(f, _) -> spine f (n + 1)
        | _ -> e, n

    let saturated (f: TypedExpr) (extra: int) =
        match spine f extra with
        | { Kind = TEVar h }, n when
            (match Map.tryFind h helpers with
             | Some a -> n >= a
             | None -> false)
            ->
            Some h
        | _ -> None

    match te.Kind with
    | TELambda _
    | TELambdaPat _ -> None
    | TEApp _ ->
        saturated te 0
        |> Option.orElseWith (fun () -> tryPickChild (eagerCommandHelper helpers) te)
    | TEPipe(_, f) ->
        saturated f 1
        |> Option.orElseWith (fun () -> tryPickChild (eagerCommandHelper helpers) te)
    | TELet(n, _, v, b) ->
        eagerCommandHelper helpers v
        |> Option.orElseWith (fun () -> eagerCommandHelper (mapDrop n helpers) b)
    | _ -> tryPickChild (eagerCommandHelper helpers) te

// lazy command-backed lets [D:lazy-command-lets]: in the helpers map an
// arity-0 entry is a binding holding a lazy command seq — reading it
// spawns nothing, forcing it runs its command
let private lazyNames (helpers: Map<string, int>) =
    helpers |> Map.filter (fun _ a -> a = 0) |> Map.keys |> Set.ofSeq

/// the first of `names` read on the eager spine (outside lambdas)
let rec eagerNameRef (names: Set<string>) (te: TypedExpr) : string option =
    match te.Kind with
    | TEVar v when names.Contains v -> Some v
    | TELambda _
    | TELambdaPat _ -> None
    | _ -> tryPickChild (eagerNameRef names) te

/// evaluating this builds a command seq and spawns nothing: a command, a
/// pipeline of commands, a value-headed pipe into a command, or another
/// lazy binding — with a head, argv and value head that run nothing
/// (argv is built when the value is, so a splice forces) [D:lazy-command-lets]
let rec private lazyCommandSeq (names: Set<string>) (te: TypedExpr) : bool =
    let quiet e =
        not (runsCommandT e) && (eagerNameRef names e).IsNone

    match te.Kind with
    | TECmd _ -> forallChild quiet te
    | TEPipe(arg, ({ Kind = TECmd _ } as c)) -> lazyCommandSeq names c && (lazyCommandSeq names arg || quiet arg)
    | TEVar v -> names.Contains v
    | _ -> false

/// the module/init binding summary after `let name = te`: a
/// command-reaching function keeps its arity, a lazy command seq is 0,
/// anything else drops the name (a shadow)
let recordCommandBinding (helpers: Map<string, int>) (name: string) (te: TypedExpr) : Map<string, int> =
    match commandHelperArity helpers te with
    | Some a -> Map.add name a helpers
    | None when lazyCommandSeq (lazyNames helpers) te -> Map.add name 0 helpers
    | None -> mapDrop name helpers

/// why evaluating a module/init binding RHS runs a command now — None when
/// it runs none or only builds a lazy command seq [D:lazy-command-lets]
let commandRunsNow (helpers: Map<string, int>) (te: TypedExpr) : string option =
    let names = lazyNames helpers

    let lazyFix =
        "only a command, a pipeline of commands or a value piped into one stays lazy; or wrap it in a function (let f () = …)"

    let rec eagerVar (pick: string -> bool) (e: TypedExpr) =
        match e.Kind with
        | TEVar v when pick v -> Some v
        | TELambda _
        | TELambdaPat _ -> None
        | _ -> tryPickChild (eagerVar pick) e

    // the stage that forces a seq: `xs |> Seq.freeze` / `Seq.length xs`
    let rec forcer (e: TypedExpr) =
        match e.Kind with
        | TEVar v when not (v.StartsWith "|") -> Some v
        | TEApp(f, _) -> forcer f
        | _ -> None

    let forcing () =
        match te.Kind with
        | TEPipe(_, f) -> forcer f
        | TEApp(f, _) -> forcer f
        | _ -> None
        |> Option.map (fun f -> $"passed to `{f}` here")

    if lazyCommandSeq names te then
        None
    else
        let runs = runsCommandT te

        match
            (if runs then eagerVar (fun v -> v = "|print") te else None),
            (if runs then
                 eagerVar (fun v -> (reifierOfVar v).IsSome) te
             else
                 None),
            eagerNameRef names te
        with
        // `| and` / `| or` stream their commands (the |print desugar)
        | Some _, _, _ -> Some $"`| and`/`| or` runs the commands now — {lazyFix}"
        | None, Some r, _ ->
            Some
                $"`| {(reifierOfVar r).Value}` runs the command now — drop the reifier to keep it lazy, or wrap it in a function (let f () = …)"
        | None, None, Some n ->
            let what = forcing () |> Option.defaultValue "read here, which runs it"
            Some $"'{n}' is a lazy command {what} — {lazyFix}"
        | None, None, None when runs ->
            let what = forcing () |> Option.defaultValue "part of a larger expression here"
            Some $"the command is {what} — {lazyFix}"
        | None, None, None -> None

/// the first command on the RHS's eager spine, rendered for a message:
/// literal argv words as written, splices as $name / $@name, computed
/// words elided
let rec private firstCommand (te: TypedExpr) : TypedExpr option =
    match te.Kind with
    | TECmd _ -> Some te
    | TELambda _
    | TELambdaPat _ -> None
    | _ -> tryPickChild firstCommand te

let private renderCommand (te: TypedExpr) : string =
    match te.Kind with
    | TECmd(prog, args, _) ->
        let word (a: TypedExpr) =
            match a.Kind with
            | TEStr s -> s
            | TEInt n -> string n
            | TEBool b -> (if b then "true" else "false")
            | TEVar n -> "$" + n
            | TESplat { Kind = TEVar n } -> "$@" + n
            | _ -> "…"

        String.concat " " (theadDisplay prog :: (args |> List.map word))
    | _ -> "…"

// the recognized visibly-materialized tails [D:reenum-warning] — the
// closed set, judged at the RHS tail (through let-in bodies): a
// `|> Seq.freeze` tail, an applied `Seq.freeze …` head (the comprehension
// desugars to exactly this), and the eager list literal. Anything else
// — branch arms included — reads as unforced, which is the stated
// over-approximation the word "possible" carries.
let rec private forcedTail (te: TypedExpr) : bool =
    let isForce (f: TypedExpr) =
        match f.Kind with
        // a builtin module member types as TEVar "Module.member"
        | TEVar "Seq.freeze" -> true
        // the comprehension's own desugar ([for x in xs -> e])
        | TEVar "|seqFreeze" -> true
        | _ -> false

    match te.Kind with
    | TEList _ -> true
    | TEPipe(_, f) -> isForce f
    | TEApp(f, _) -> isForce f
    | TELet(_, _, _, b)
    | TELetPat(_, _, b) -> forcedTail b
    | _ -> false

/// Some (rendered command) when a binding RHS is a command-backed seq
/// with no visibly-materialized tail — the re-enumeration hazard's
/// subject [D:reenum-warning]
let commandBackedUnforced (te: TypedExpr) : string option =
    match te.Ty with
    | TSeq _ when runsCommandT te && not (forcedTail te) -> firstCommand te |> Option.map renderCommand
    | _ -> None

type ReenumEvent =
    // a block-local binder qualified — joins the tracked set for its body
    | ReenumBind of id: int * name: string * cmd: string
    // an enumerating use of a tracked binding, at its site; `note` names
    // the indirection ("" for a direct read)
    | ReenumUse of id: int * name: string * note: string * span: Span

/// what reading a name pulls [D:reenum-indirect]: (binding id, binding
/// name) per pull — a command-backed binding pulls itself once; a carrier
/// (a function, or a lazy seq derived from tracked bindings) pulls what
/// its body/RHS reads, each time it is used. `Forced` marks a function
/// whose body ends materialized: a call's result carries nothing
/// [D:reenum-forced-call]
type ReenumCarry = {
    Pulls: (int * string) list
    Forced: bool
}

type ReenumTracked = Map<string, ReenumCarry>

// a function handed to a `Seq.*` function runs per element — a `for` body
// is one (`xs |> Seq.iter (fun x -> …)`): a pull there repeats
let rec private seqHof (f: TypedExpr) =
    match f.Kind with
    | TEVar v -> v.StartsWith "Seq." || v.StartsWith "|seq"
    | TEApp(g, _) -> seqHof g
    | _ -> false

let private isFunctionValue (te: TypedExpr) =
    match te.Kind, te.Ty with
    | (TELambda _ | TELambdaPat _), _ -> true
    | _, TFun _ -> true
    | _ -> false

// a function whose body (under its lambdas) ends materialized
let rec private forcedBody (v: TypedExpr) =
    match v.Kind with
    | TELambda(_, _, b)
    | TELambdaPat(_, b) -> forcedBody b
    | _ -> forcedTail v

// a call of a tracked function whose body ends materialized
// [D:reenum-forced-call] — `f ()`, `x |> f`, through let-in tails
let rec private forcedCall (tracked: ReenumTracked) (v: TypedExpr) =
    let rec head (e: TypedExpr) =
        match e.Kind with
        | TEApp(f, _) -> head f
        | TEVar g -> Some g
        | _ -> None

    let forcedFn (e: TypedExpr) =
        match head e |> Option.bind (fun g -> Map.tryFind g tracked) with
        | Some c -> c.Forced
        | None -> false

    match v.Kind with
    | TEApp(f, _) -> forcedFn f
    | TEPipe(_, f) -> forcedFn f
    | TELet(_, _, _, b)
    | TELetPat(_, _, b) -> forcedCall tracked b
    | _ -> false

// a binding that defers its reads: a function, or a lazy unforced seq
let private carries (tracked: ReenumTracked) (v: TypedExpr) =
    isFunctionValue v
    || (match v.Ty with
        | TSeq _ -> not (forcedTail v) && not (forcedCall tracked v)
        | _ -> false)

/// enumerating uses of tracked (command-backed, unforced) bindings in
/// one statement tree, source order, shadow-aware. Conservative and
/// stated: every read of the name counts as a possible pull — piped,
/// an adapter/for source, an argv splice, printed/echoed, or passed as
/// an argument (a callee may pull). Indirect pulls [D:reenum-indirect]:
/// a carrier binding's RHS does not pull when bound — each use of the
/// carrier replays what it reads, noted "through 'f'"; a read inside a
/// function handed to a `Seq.*` function (a `for` body) counts twice.
/// Block-local lets that qualify join the tracked set for their own
/// body (fresh ids from the caller's well).
let rec private reenumWalk
    (nextId: unit -> int)
    (emit: ReenumEvent -> unit)
    (sink: int -> string -> string -> Span -> unit)
    (tracked: ReenumTracked)
    (te: TypedExpr)
    : unit =
    let walk = reenumWalk nextId emit sink

    let removeAll (names: string list) (m: ReenumTracked) =
        names |> List.fold (fun m n -> mapDrop n m) m

    let outerIds (m: ReenumTracked) =
        m |> Seq.collect (fun kv -> kv.Value.Pulls |> List.map fst) |> Set.ofSeq

    match te.Kind with
    | TEVar n ->
        match Map.tryFind n tracked with
        | Some carry ->
            for id, name in carry.Pulls do
                sink id name (if name = n then "" else $" (through '{n}')") te.Span
        | None -> ()
    | TELet(n, _, v, b) ->
        let carry = reenumBind nextId emit sink tracked n v

        walk
            (if carry.Pulls.IsEmpty && not carry.Forced then
                 mapDrop n tracked
             else
                 Map.add n carry tracked)
            b
    | TELetPat(pat, v, b) ->
        walk tracked v
        walk (removeAll (patNameSpans pat |> List.map fst) tracked) b
    | TELambda(p, _, b) -> walk (mapDrop p tracked) b
    | TELambdaPat(pat, b) -> walk (removeAll (patNameSpans pat |> List.map fst) tracked) b
    | TEApp(f, a) when seqHof f && isFunctionValue a ->
        walk tracked f
        // a pull of a binding from outside the per-element function repeats
        let outer = outerIds tracked

        let loopSink id name note span =
            sink id name note span

            if outer.Contains id then
                sink id name " (read in a loop)" span

        reenumWalk nextId emit loopSink tracked a
    | TEMatch(s, arms) ->
        walk tracked s

        for pat, g, b in arms do
            let t' = removeAll (patNameSpans pat |> List.map fst) tracked
            g |> Option.iter (walk t')
            walk t' b
    | TERetry(_, o, w, b, u) ->
        walk tracked o
        w |> Option.iter (walk tracked)
        walk tracked b
        u |> Option.iter (fun ((n, _), pred) -> walk (mapDrop n tracked) pred)
    | TEWithin(_, binder, a, o, b) ->
        a |> Option.iter (walk tracked)
        o |> Option.iter (walk tracked)

        walk
            (match binder with
             | Some(n, _) -> mapDrop n tracked
             | None -> tracked)
            b
    | _ -> iterChildren (walk tracked) te

/// bind `n = v` [D:reenum-indirect]: a carrier's RHS reads of outer
/// tracked bindings are collected as its pulls, not counted now; any
/// other RHS is walked as uses. A command-backed unforced RHS also pulls
/// itself (a fresh id, announced with ReenumBind). Returns n's carry
/// (no pulls and not Forced = untracked).
and private reenumBind
    (nextId: unit -> int)
    (emit: ReenumEvent -> unit)
    (sink: int -> string -> string -> Span -> unit)
    (tracked: ReenumTracked)
    (n: string)
    (v: TypedExpr)
    : ReenumCarry =
    if carries tracked v then
        let outer =
            tracked |> Seq.collect (fun kv -> kv.Value.Pulls |> List.map fst) |> Set.ofSeq

        let pulls = ResizeArray<int * string>()

        let collect id name note span =
            if outer.Contains id then
                pulls.Add(id, name)
            else
                sink id name note span

        reenumWalk nextId emit collect tracked v

        let own =
            match commandBackedUnforced v with
            | Some cmd ->
                let id = nextId ()
                emit (ReenumBind(id, n, cmd))
                [ id, n ]
            | None -> []

        {
            Pulls = own @ List.ofSeq pulls
            Forced = isFunctionValue v && forcedBody v
        }
    else
        reenumWalk nextId emit sink tracked v
        { Pulls = []; Forced = false }

let private reenumCollect (run: (ReenumEvent -> unit) -> (int -> string -> string -> Span -> unit) -> 'r) =
    let acc = ResizeArray<ReenumEvent>()

    let r =
        run acc.Add (fun id name note span -> acc.Add(ReenumUse(id, name, note, span)))

    r, List.ofSeq acc

/// the events of one statement tree
let reenumEvents (nextId: unit -> int) (tracked0: ReenumTracked) (root: TypedExpr) : ReenumEvent list =
    reenumCollect (fun emit sink -> reenumWalk nextId emit sink tracked0 root)
    |> snd

/// a top-level `let n = v`: the RHS's events and n's carry
/// [D:reenum-indirect]
let reenumLet
    (nextId: unit -> int)
    (tracked0: ReenumTracked)
    (n: string)
    (v: TypedExpr)
    : ReenumCarry * ReenumEvent list =
    reenumCollect (fun emit sink -> reenumBind nextId emit sink tracked0 n v)

// ---- the newTempDir footgun [D:newtempdir-lint] --------------------------
// `Path.newTempDir` bound then `Dir.delete`/`Dir.deleteAll`'d in the same
// scope is the manual spelling of a `within tmp d` block — and a worse one:
// `within` removes the directory on scope exit and on Ctrl+C/kill (the exit
// hook sweeps it), which a straight-line delete misses when the body raises
// or the process is signalled. newTempDir earns its place for the escaping
// case (a directory that outlives the block — a cross-process handoff), so
// an unmatched bind (no in-scope delete) is exactly that legitimate use and
// stays silent [D:gap-a-remainder]. The warning fires only when the pairing
// is visible, mirroring the re-enumeration walk's scope discipline.

/// a `Path.newTempDir ()` call (the arg is unit — ignored)
let isNewTempDirCall (te: TypedExpr) : bool =
    match te.Kind with
    | TEApp({ Kind = TEVar "Path.newTempDir" }, _) -> true
    | _ -> false

/// a `Dir.delete`/`Dir.deleteAll` applied to a bare name -> Some(name, isAll)
let private dirDeleteOf (te: TypedExpr) : (string * bool) option =
    match te.Kind with
    | TEApp({ Kind = TEVar "Dir.deleteAll" }, { Kind = TEVar n }) -> Some(n, true)
    | TEApp({ Kind = TEVar "Dir.delete" }, { Kind = TEVar n }) -> Some(n, false)
    | _ -> None

type TempDirEvent =
    // a block-local newTempDir binder — joins the tracked set for its body
    | TempBind of id: int * name: string
    // a delete of a tracked binder, at its site
    | TempDelete of id: int * name: string * span: Span * isAll: bool

/// newTempDir binds and their in-scope deletes in one statement tree,
/// source order, shadow-aware — the same threading as [D:reenum-warning]:
/// block-local binders join the tracked set for their body (fresh ids from
/// the caller's well), and a name leaving scope (lambda/match/within/let
/// pattern) drops from tracking. Top-level binders arrive via tracked0.
let tempDirEvents (nextId: unit -> int) (tracked0: Map<string, int>) (root: TypedExpr) : TempDirEvent list =
    let acc = ResizeArray<TempDirEvent>()

    let removeAll (names: string list) (m: Map<string, int>) =
        names |> List.fold (fun m n -> mapDrop n m) m

    let rec walk (tracked: Map<string, int>) (te: TypedExpr) : unit =
        (match dirDeleteOf te with
         | Some(n, isAll) ->
             match Map.tryFind n tracked with
             | Some id -> acc.Add(TempDelete(id, n, te.Span, isAll))
             | None -> ()
         | None -> ())

        match te.Kind with
        | TELet(n, _, v, b) ->
            walk tracked v

            let trackedB =
                if isNewTempDirCall v then
                    let id = nextId ()
                    acc.Add(TempBind(id, n))
                    Map.add n id tracked
                else
                    mapDrop n tracked

            walk trackedB b
        | TELetPat(pat, v, b) ->
            walk tracked v
            walk (removeAll (patNameSpans pat |> List.map fst) tracked) b
        | TELambda(p, _, b) -> walk (mapDrop p tracked) b
        | TELambdaPat(pat, b) -> walk (removeAll (patNameSpans pat |> List.map fst) tracked) b
        | TEMatch(s, arms) ->
            walk tracked s

            for pat, g, b in arms do
                let t' = removeAll (patNameSpans pat |> List.map fst) tracked
                g |> Option.iter (walk t')
                walk t' b
        | TEWithin(_, binder, a, o, b) ->
            a |> Option.iter (walk tracked)
            o |> Option.iter (walk tracked)

            walk
                (match binder with
                 | Some(n, _) -> mapDrop n tracked
                 | None -> tracked)
                b
        | _ -> iterChildren (walk tracked) te

    walk tracked0 root
    List.ofSeq acc
