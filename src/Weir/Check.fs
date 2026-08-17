module Weir.Check

open Weir.Ast
open Weir.Types

// Origin [D:row-provenance]: a PHYSICAL (line, col, len) override for
// where the error points — set when the true cause lives in another
// statement, whose logical spans this statement cannot translate.
type TypeError =
    { Span: Span
      Message: string
      Origin: (int * int * int) option }

let formatError (e: TypeError) : string =
    $"[{e.Span.Start.Line}:{e.Span.Start.Col}-{e.Span.End.Col}] type error: {e.Message}"

type Warning = { Span: Span; Message: string }

let formatWarning (w: Warning) : string =
    $"[{w.Span.Start.Line}:{w.Span.Start.Col}-{w.Span.End.Col}] warning: {w.Message}"

// The logical-col -> (physLine, physCol) translator for the statement
// being checked [D:row-provenance]: Script sets it around each
// checkStatement; None for non-statement consumers (Complete, tests),
// where provenance simply does not record.
let toPhys: System.Threading.ThreadLocal<(int -> int * int) option> =
    new System.Threading.ThreadLocal<_>(fun () -> None)

// Args.load targets [D:typed-argv] — the record is the flags shape,
// the union is the subcommand front door (case -> payload def)
type ArgsTarget =
    | ArgsRecord of RecordDef
    | ArgsUnion of def: UnionDef * payloads: Map<string, RecordDef>
    // shared flags by containment [D:shared-flags]: the outer record's
    // scalar fields are shared flags; its ONE union-typed field is the
    // subcommand slot
    | ArgsShared of outer: RecordDef * unionField: string * udef: UnionDef * payloads: Map<string, RecordDef>

// retired names teach their replacement [D:seq-force] — one
// table, both lookup sites
let private retiredMember (m: string) (field: string) : string option =
    match m, field with
    | "Seq", "toList" -> Some "weir has no list type; 'Seq.force' is the materializer"
    | "Option", "defaultTo" -> Some "renamed 'Option.defaultValue' (F# parity); a lazy default is 'Option.defaultWith'"
    // one operation, one name [D:seq-gaps]: the F# spelling wins where
    // one exists (collect), and the weir spelling keeps its seat (where)
    | "Seq", "flatMap" -> Some "F# parity names it 'Seq.collect'"
    | "Seq", "filter" -> Some "weir's filter is 'Seq.where' — one name per operation"
    | _ -> None

// retired FIELDS teach their replacement at the access site
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
    | "toList" -> Some "weir has no list type; 'force' is the materializer"
    | "defaultTo" -> Some "renamed: use 'Option.defaultValue' (or 'Option.defaultWith' for a thunk)"
    | _ -> None

/// Members that live as bespoke checker ARMS, not as entries in a
/// module's member map: `Args.load`/`Env.load` resolve a record/union
/// TYPE name, so their scheme is not a plain member. Completion must
/// still offer them, and the "no member" error must read them as
/// known-but-misused, not a typo — one source so the two stay in step.
let specialModuleMembers: Map<string, string list> =
    Map [ "Args", [ "load" ]; "Env", [ "load" ] ]

let private isSpecialMember (m: string) (field: string) : bool =
    specialModuleMembers
    |> Map.tryFind m
    |> Option.defaultValue []
    |> List.contains field

type TypedExpr = { Kind: TypedKind; Ty: Ty; Span: Span }

and TypedKind =
    | TEInt of value: int64
    | TEStr of string
    | TEBool of bool
    | TEUnit
    | TEVar of string
    | TELet of name: string * nameSpan: Span * value: TypedExpr * body: TypedExpr
    | TELambda of param: string * paramSpan: Span * body: TypedExpr
    | TEApp of fn: TypedExpr * arg: TypedExpr
    | TEPipe of arg: TypedExpr * fn: TypedExpr
    | TEField of target: TypedExpr * field: string
    | TEBinOp of op: string * left: TypedExpr * right: TypedExpr
    | TERecord of record: string * fields: (string * TypedExpr) list
    | TEMatch of scrutinee: TypedExpr * arms: (Pattern * TypedExpr option * TypedExpr) list
    | TEIf of cond: TypedExpr * thn: TypedExpr * els: TypedExpr option
    | TEDur of ms: int64
    | TEFloat of value: float
    | TERetry of
        poll: bool *
        opts: TypedExpr *
        watch: TypedExpr option *
        body: TypedExpr *
        until: (string * TypedExpr) option
    | TESize of bytes: int64
    | TESeq of first: TypedExpr * rest: TypedExpr
    | TEWithin of
        kind: string *
        binder: string option *
        arg: TypedExpr option *
        opts: TypedExpr option *
        body: TypedExpr
    | TEAlways of body: TypedExpr * cleanup: TypedExpr
    | TEEnvLoad of def: RecordDef * enums: Map<string, string list>
    | TEArgsLoad of target: ArgsTarget
    | TEFrom of format: string * rowDef: RecordDef * defs: Map<string, RecordDef> * seqOf: bool * mapOf: bool
    // from yaml T [D:yaml-v1]: eval has no env.Types, so the checker packs
    // the RESOLVED target tree (the [D:env-enums] precedent)
    | TEFromYaml of tyName: string * shape: Yaml.Shape
    | TETo of format: string * renames: Map<string, Map<string, string>>
    | TEList of items: TypedExpr list
    | TECmd of prog: string * args: TypedExpr list * env: TypedExpr option
    | TESplat of TypedExpr
    | TEUpdate of source: TypedExpr * updates: (string list * TypedExpr) list
    | TETuple of TypedExpr list
    | TELetPat of binder: Pattern * value: TypedExpr * body: TypedExpr
    | TELambdaPat of binder: Pattern * body: TypedExpr
    | TEInterp of parts: InterpPart<TypedExpr> list
    // the yaml district's TYPED template [D:yaml-district]
    | TEYaml of TypedYamlTpl * schema: string option

and TypedYamlTpl =
    | TYtScalar of raw: string * quoted: bool * span: Ast.Span
    // literal block scalar content [D:block-scalars] — bytes, no checking
    | TYtBlock of text: string * span: Ast.Span
    | TYtSplice of TypedExpr
    | TYtSeq of TypedYamlTplItem list * span: Ast.Span
    | TYtMap of TypedYamlTplEntry list * span: Ast.Span

and TypedYamlTplEntry =
    | TYtPair of TypedYamlKey * TypedYamlTpl
    | TYtForEntries of binder: Pattern * source: TypedExpr * body: TypedYamlTplEntry list

and TypedYamlTplItem =
    | TYtItem of TypedYamlTpl
    | TYtForItems of binder: Pattern * source: TypedExpr * body: TypedYamlTplItem list

and TypedYamlKey =
    | TYtKeyLit of string * span: Ast.Span
    | TYtKeySplice of TypedExpr

type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return x = Ok x
    member _.ReturnFrom(r: Result<_, _>) = r

let private result = ResultBuilder()

let private err (span: Span) (msg: string) : Result<'a, TypeError> =
    Error
        { Span = span
          Message = msg
          Origin = None }

let private mismatch (span: Span) (expected: Ty) (actual: Ty) =
    // a built request where a URL string is expected is the fetch/send
    // misreading exactly (`Http.get u |> Http.fetch`) — name the pair's
    // split instead of leaving a bare type mismatch [D:fetch-naming]
    let hint =
        match expected, actual with
        | TStr, TNamed("HttpRequest", _) -> " — a built request runs through Http.send; Http.fetch takes a bare URL"
        | _ -> ""

    err span $"expected {formatTy expected}, got {formatTy actual}{hint}"

let private allOk (items: 'a list) (f: 'a -> Result<unit, TypeError>) : Result<unit, TypeError> =
    items |> List.fold (fun acc x -> Result.bind (fun () -> f x) acc) (Ok())

let private firstDup (xs: string list) : string option =
    xs
    |> List.countBy id
    |> List.tryPick (fun (x, n) -> if n > 1 then Some x else None)

let private bindParams (env: TypeEnv) (bindings: (string * Ty) list) : TypeEnv =
    { env with
        Values = bindings |> List.fold (fun vs (n, t) -> Map.add n (mono t) vs) env.Values }

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

// The two splice sites [D:interp-show]: a hole RENDERS for a human
// (Show is the law), a command argument becomes an argv WORD for a
// program (scalar-exact — no bet on the child's parser)
type private SpliceSite =
    | Hole
    | CmdArg

// A pending class constraint on an unresolved type/row var: the
// demanding site's span and message formatter travel with it, so a
// later discharge failure reads at the place that demanded it.
type private Pending =
    { Cls: Cls
      Span: Span
      Describe: Ty -> string }

// == on floats is representation, not a promise weir makes [D:floats]:
// the Eq exclusion's teaching names the idiom
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

type private Ctx =
    { mutable Fresh: int
      mutable Subst: Map<string, Ty>
      mutable Rows: Map<string, Map<string, Ty * Span>>
      // physical field-access origins for INSTANTIATED row vars
      // [D:row-provenance], rehydrated from the scheme's RowOrigins
      mutable RowOrigins: Map<string, (string * int * int * int) list>
      mutable Cons: Map<string, Pending list>
      // splice/hole vars whose scalar defaulting is DEFERRED to the
      // statement boundary [D:splice-default-last]: defaulting fired
      // early once and rejected `1 |> (fun k -> $"{k}")` — order, not
      // rule; the shape check defers with it
      mutable PendingSplices: (string * Span * SpliceSite) list
      // the inference budget [D:depth-guard]'s post-parse sibling:
      // HM inference is DEXPTIME (a 7-line file can double its type
      // per line), and "no answer, forever" is as wrong as a crash —
      // resolution work is counted and exhaustion becomes a located
      // diagnostic. The ceiling is a COST bound, orders of magnitude
      // above any real program, re-askable on a receipt
      mutable Work: int
      // the span the exhaustion diagnostic anchors on — the innermost
      // unification site's, kept current by bind (resolve has no span)
      mutable WorkSpan: Span
      // the innermost bind's operands, for the evidence probe: at
      // exhaustion these are the types whose SIZE is or is not the story
      mutable WorkTys: Ty * Ty
      // point-free print uses (Seq.iter print) DEFER their sentinel to
      // the statement boundary like splices do [D:splice-default-last]:
      // a use-site-determined type passes printArgTy there, an
      // undetermined one keeps the string default (PLAN-dx-review D7)
      mutable PendingPrints: (string * Span) list
      // physical positions of bare holes whose var took the string
      // DEFAULT this statement (PLAN-dx-review D6) — ride the let's
      // scheme so a later call-site mismatch can name the anchor
      mutable HoleDefaulted: (int * int) list }

// exhaustion carries whether type SIZE is actually in evidence — the
// message must not claim "your type grew too large" merely because the
// budget ran out (a future second non-termination shape gets an honest
// generic answer, not a confidently wrong specific one)
exception private BudgetExceeded of Span * sizeEvidenced: bool

let private workBudget = 5_000_000
let private sizeEvidenceFloor = 50_000

let private newCtx () =
    { Fresh = 0
      Subst = Map.empty
      Rows = Map.empty
      RowOrigins = Map.empty
      Cons = Map.empty
      PendingSplices = []
      Work = 0
      WorkSpan =
        { Start = { Line = 1; Col = 1 }
          End = { Line = 1; Col = 1 } }
      WorkTys = TUnit, TUnit
      PendingPrints = []
      HoleDefaulted = [] }

let private freshName (ctx: Ctx) (prefix: string) : string =
    ctx.Fresh <- ctx.Fresh + 1
    $"{prefix}{ctx.Fresh}"

// resolve is the step every type walk pays (occurs, finalTy, bind,
// row merges), so it is where the budget TICKS; the raise site
// disarms the counter first so the evidence probe below can walk
// without re-triggering
let rec private resolveNoTick (ctx: Ctx) (ty: Ty) : Ty =
    match ty with
    | TVar v ->
        match Map.tryFind v ctx.Subst with
        | Some t -> resolveNoTick ctx t
        | None -> ty
    | TRowVar(r, _) ->
        match Map.tryFind r ctx.Subst with
        | Some t -> resolveNoTick ctx t
        | None -> ty
    | t -> t

// a BOUNDED size probe for the budget's evidence check: walks at most
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

let private finalTy (ctx: Ctx) (ty: Ty) : Ty =
    let rec go (seen: Set<string>) ty =
        match resolve ctx ty with
        | TFun(a, b) -> TFun(go seen a, go seen b)
        | TSeq t -> TSeq(go seen t)
        | TTuple ts -> TTuple(ts |> List.map (go seen))
        | TNamed(n, args) -> TNamed(n, args |> List.map (go seen))
        | TRowVar(r, _) when seen.Contains r -> TRowVar(r, [])
        | TRowVar(r, _) ->
            let fields =
                Map.tryFind r ctx.Rows
                |> Option.defaultValue Map.empty
                |> Map.toList
                |> List.map (fun (f, (t, _)) -> f, go (Set.add r seen) t)

            TRowVar(r, fields)
        | t -> t

    go Set.empty ty

let private hasVars (ctx: Ctx) (ty: Ty) : bool =
    not (Set.isEmpty (tyVars (finalTy ctx ty)))

let private occurs (ctx: Ctx) (v: string) (ty: Ty) : bool =
    let seenRows = System.Collections.Generic.HashSet<string>()

    let rec go ty =
        match resolve ctx ty with
        | TVar u -> u = v
        | TRowVar(r, _) ->
            r = v
            || (seenRows.Add r
                && Map.tryFind r ctx.Rows
                   |> Option.defaultValue Map.empty
                   |> Map.exists (fun _ (t, _) -> go t))
        | TFun(a, b) -> go a || go b
        | TSeq t -> go t
        | TTuple ts -> ts |> List.exists go
        | TNamed(_, args) -> args |> List.exists go
        | _ -> false

    go ty

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

        let rows = rowNames sch.Ty

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
                    ctx.Rows <- Map.add r' (fields' |> List.map (fun (f, t) -> f, (t, span)) |> Map.ofList) ctx.Rows

                    // provenance rides the instantiation [D:row-provenance]
                    match Map.tryFind r sch.RowOrigins with
                    | Some os -> ctx.RowOrigins <- Map.add r' os ctx.RowOrigins
                    | None -> ()

                    TRowVar(r', fields')
                | None -> TRowVar(r, fields')
            | TFun(a, b) -> TFun(rename a, rename b)
            | TSeq t -> TSeq(rename t)
            | TTuple ts -> TTuple(ts |> List.map rename)
            | TNamed(n, args) -> TNamed(n, args |> List.map rename)
            | t -> t

        // constraints freshen WITH the vars (deep-copy discipline): the
        // instantiation site's span becomes the demanding site
        for KeyValue(v, clss) in sch.Cs do
            match Map.tryFind v mapping with
            | Some v' ->
                let ps =
                    clss
                    |> Set.toList
                    |> List.map (fun cls ->
                        { Cls = cls
                          Span = span
                          Describe =
                            match cls with
                            | Cls.Eq ->
                                fun t ->
                                    if tyHasFloat t then
                                        floatEqTeaching
                                    else
                                        $"this use requires equatable values, got {formatTy t} — sequences and functions cannot be compared with '=='"
                            | Cls.Show -> fun t -> $"show cannot render functions; this is {formatTy t}"
                            | Cls.Ord ->
                                fun t ->
                                    $"cannot sort by this key: {formatTy t} cannot be ordered — keys are int, float, string, bool, Duration, Size, or Instant" })

                ctx.Cons <- Map.add v' (ps @ (Map.tryFind v' ctx.Cons |> Option.defaultValue [])) ctx.Cons
            | None -> ()

        rename sch.Ty

let private envFreeVars (ctx: Ctx) (env: TypeEnv) : Set<string> =
    env.Values
    |> Map.fold (fun acc _ sch -> acc + (tyVars (finalTy ctx sch.Ty) - sch.Forall)) Set.empty

// The class solver (Session A: Eq only). Concrete types run the shape
// predicate; applied constructors decompose structurally; bare vars
// (and row vars) carry the constraint forward — bind discharges them
// the moment they resolve. Failure formats the ORIGINAL demanded type
// (matching the pre-class message families). Fully erased: no runtime
// presence — the stop-and-report budget's hard line.
let private demand (ctx: Ctx) (env: TypeEnv) (p: Pending) (ty0: Ty) : Result<unit, TypeError> =
    let pend (name: string) =
        ctx.Cons <- Map.add name (p :: (Map.tryFind name ctx.Cons |> Option.defaultValue [])) ctx.Cons

    let rec ok (seen: Set<string>) (ty: Ty) : bool =
        match resolve ctx ty with
        | TVar v ->
            pend v
            true
        | TRowVar(r, _) ->
            // rides the row; discharges when the row does (all fields
            // then satisfy the class, recursively)
            pend r
            true
        | t ->
            let decompose (n: string) (targs: Ty list) =
                let key = formatTy t

                seen.Contains key
                || (match Map.tryFind n env.Types with
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
            // Secret admits Eq [D:secret] (did the token change?) but NOT
            // Ord — sorting secrets is meaningless; constant-time is not
            // claimed (weir is not a crypto library)
            | Cls.Eq, (TInt | TStr | TBool | TUnit | TDur | TSize | TInstant | TBytes | TSecret) -> true
            // floats are EXCLUDED from Eq [D:floats] — finite-only kept
            // equality reflexive; representation (0.1 + 0.2) is the trap
            // that remains, and weir does not vouch for it
            | Cls.Eq, TFloat -> false
            | Cls.Eq, (TFun _ | TSeq _) -> false
            | Cls.Eq, TTuple ts -> ts |> List.forall (ok seen)
            | Cls.Eq, TNamed(n, targs) -> decompose n targs
            // Show: no function anywhere; seqs render fine
            | Cls.Show, (TInt | TFloat | TStr | TBool | TUnit | TDur | TSize | TInstant | TBytes | TSecret) -> true
            | Cls.Show, TFun _ -> false
            | Cls.Show, TSeq elem -> ok seen elem
            | Cls.Show, TTuple ts -> ts |> List.forall (ok seen)
            // Map renders (sorted) iff its VALUES do [D:map-string];
            // Eq stays excluded like seq (no conditional instances —
            // the closed class system is untouched)
            | Cls.Show, TNamed("Map", [ TStr; inner ]) -> ok seen inner
            // a handle shows (pid + state) [D:scoped-procs]; Eq/Ord
            // stay excluded by construction (the decompose fall-through)
            | Cls.Show, TNamed("Proc", []) -> true
            | Cls.Show, TNamed(n, targs) -> decompose n targs
            // Ord: int | string | bool EXACTLY — no decomposition, no
            // record/union ordering (no receipts; the message names it)
            // Duration joins Ord [D:duration] — the first widening since
            // the class set closed; elapsed > timeout is the point
            // floats join Ord [D:floats]: total BECAUSE finite-only
            // Instant joins Ord [D:instant] — before/after IS comparison
            // (Duration's own admission argument, one type later)
            | Cls.Ord, (TInt | TFloat | TStr | TBool | TDur | TSize | TInstant) -> true
            | Cls.Ord, _ -> false
            // vars and row vars are consumed by the outer match arms;
            // the compiler cannot see that through this nesting
            | _, (TVar _ | TRowVar _) -> true

    if ok Set.empty ty0 then
        Ok()
    else
        err p.Span (p.Describe(finalTy ctx ty0))

// the splice law, shared by the eager check and the deferred
// discharge [D:interp-show]: holes consult the Show CLASS (one arm
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
            match ty with
            // a Secret does NOT interpolate [D:secret]: `$"token: {s}"` is
            // how a secret reaches a log line. show renders ***; using the
            // value is the deliberate Secret.reveal
            | TSecret ->
                err span "a Secret does not interpolate — Secret.reveal s to use its value, or show s to render ***"
            | _ ->
                demand
                    ctx
                    env
                    { Cls = Cls.Show
                      Span = span
                      Describe =
                        fun t ->
                            $"interpolation holes render what show renders; {formatTy t} cannot be shown (functions never render)" }
                    ty
        | CmdArg ->
            match ty with
            // a Secret splices to argv [D:secret]: `curl -H $auth` must
            // work — that is what the type exists for. The value reaches
            // argv in the clear (visible in `ps` — the platform's property,
            // a stated non-claim), which is why this is deliberate, not silent
            | TSecret -> Ok()
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
        ctx.Cons <- Map.remove v ctx.Cons
        allOk ps (fun p -> demand ctx env p t)

let rec private bind (ctx: Ctx) (env: TypeEnv) (span: Span) (expected: Ty) (actual: Ty) : Result<unit, TypeError> =
    // the budget ticks in resolve (the step every walk pays); bind
    // keeps the ANCHOR and the evidence operands current so exhaustion
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
            ctx.Subst <- Map.add v t ctx.Subst
            dischargeCons ctx env v t
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
    match Map.tryFind name env.Types with
    | Some(Record def) ->
        let constraints =
            Map.tryFind r ctx.Rows |> Option.defaultValue Map.empty |> Map.toList

        ctx.Subst <- Map.add r (TNamed(name, targs)) ctx.Subst

        dischargeCons ctx env r (TNamed(name, targs))
        |> Result.bind (fun () ->

            // cross-statement provenance [D:row-provenance]: point at the
            // recorded ACCESS, meet as the message note — shared by the
            // no-field arm AND the field-TYPE-mismatch arm (the sibling)
            let atAccess field fspan (baseMsg: string) : Result<unit, TypeError> =
                let origin =
                    Map.tryFind r ctx.RowOrigins
                    |> Option.bind (List.tryFind (fun (f, _, _, _) -> f = field))

                match origin, toPhys.Value with
                | Some(_, ol, oc, len), Some tr ->
                    let ml, mc = tr fspan.Start.Col

                    Error
                        { Span = fspan
                          Message = $"{baseMsg} (the value becomes a {name} at {ml}:{mc})"
                          Origin = Some(ol, oc, len) }
                | _ ->
                    Error
                        { Span = fspan
                          Message = baseMsg
                          Origin = None }

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
    | None -> err span $"unknown type '{name}'"

and private mergeRows (ctx: Ctx) (env: TypeEnv) (r1: string) (r2: string) : Result<unit, TypeError> =
    if r1 = r2 then
        Ok()
    else
        let fields1 = Map.tryFind r1 ctx.Rows |> Option.defaultValue Map.empty
        ctx.Subst <- Map.add r1 (TRowVar(r2, [])) ctx.Subst

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
            let fields2 = Map.tryFind r2 ctx.Rows |> Option.defaultValue Map.empty

            match Map.tryFind field fields2 with
            | Some(ft2, _) -> bind ctx env fspan ft2 ft
            | None ->
                ctx.Rows <- Map.add r2 (Map.add field (ft, fspan) fields2) ctx.Rows
                Ok())

// The sentinel scheme registered for the print builtin. The quantified name
// is unforgeable through declarations (ctx-fresh names are aN/rN), so a
// structural comparison against this scheme is exactly "print, unshadowed".
// PRECEDENT WITH A CEILING: this is the ONE place a builtin's ergonomics
// buys checker complexity (the sentinel + printArgTy + the three
// special-cased arms) — justified for the most-used builtin, NOT to be
// extended to a second builtin without a bless.
let printScheme: Scheme =
    { Forall = Set.singleton "__print"
      Cs = Map.empty
      Ty = TFun(TVar "__print", TUnit)
      RowOrigins = Map.empty
      HoleDefaults = [] }

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
let showScheme: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Show ] ]
      Ty = TFun(TVar "a", TStr)
      RowOrigins = Map.empty
      HoleDefaults = [] }

// Seq.contains — an ordinary constrained scheme
// `Eq a => a -> seq<a> -> bool` [D:inferred-type-classes], served by
// the normal instantiate/apply path.
let containsScheme: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Eq ] ]
      Ty = TFun(TVar "a", TFun(TSeq(TVar "a"), TBool))
      RowOrigins = Map.empty
      HoleDefaults = [] }

// Seq.distinct : Eq a => seq<a> -> seq<a> [D:seq-distinct]
let distinctScheme: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Eq ] ]
      Ty = TFun(TSeq(TVar "a"), TSeq(TVar "a"))
      RowOrigins = Map.empty
      HoleDefaults = [] }


// the sequenced-unit message, ONE home for its two arms (the infer and
// check-mode ESeq twins were verbatim duplicates — a drift hazard the
// dedupe re-run closed [D:maintenance-2])
let private seqUnitError (first: Expr) (ty: Ty) : string =
    let drop =
        match first.Kind with
        | ECapture { Kind = ECmd _ }
        | ECapture { Kind = EPipe(_, { Kind = ECmd _ }) } -> ", or drop the $( ) to run it as a command"
        | _ -> ""

    $"a sequenced expression must be unit; this one is {formatTy ty} — bind it or print it{drop}"

let private printArgTy (ctx: Ctx) (env: TypeEnv) (span: Span) (ty: Ty) : Result<Ty, TypeError> =
    match resolve ctx ty with
    | TVar _ as v -> bind ctx env span TStr v |> Result.map (fun () -> TStr)
    | (TStr | TInt | TFloat | TBool) as t -> Ok t
    // unit is printable as NOTHING [D:exit-reifiers]: the !() sigil
    // desugar wraps interiors in print, and `| orFail` interiors are
    // unit — one rule instead of a shadow drain builtin
    | TUnit -> Ok TUnit
    | TSeq inner ->
        (match resolve ctx inner with
         | TVar _ as v -> bind ctx env span TStr v |> Result.map (fun () -> TSeq TStr)
         | TStr -> Ok(TSeq TStr)
         | TUnit -> err span "print cannot take seq<unit> — a lazy effect sequence never runs; use Seq.iter"
         | t -> err span $"print takes a string, int, float, bool, or seq<string>; this is {formatTy (TSeq t)}")
    | TSecret ->
        // a Secret refuses print [D:secret] — show s prints ***; the value
        // is the deliberate Secret.reveal
        err span "print will not render a Secret — show s prints ***, or Secret.reveal s to print its value"
    | TBytes ->
        // Bytes refuses print [D:bytes] — raw bytes wreck a terminal
        // (the gzip receipt); the exits are deliberate
        err span "print will not render Bytes — Bytes.toBase64 b for text, or File.writeBytes path b for a file"
    | t -> err span $"print takes a string, int, float, bool, or seq<string>; this is {formatTy t}"

// the (op) desugar target [D:operator-values]: `fun a b -> a op b`
// verbatim — params carry the un-typeable '|' prefix so nothing can
// capture them; both the infer and check arms delegate here
let private opValueLambda (op: string) (sp: Span) : Expr =
    let v n = { Kind = EVar n; Span = sp }

    { Kind =
        ELambda(
            "|opL",
            sp,
            { Kind =
                ELambda(
                    "|opR",
                    sp,
                    { Kind = EBinOp(op, v "|opL", v "|opR")
                      Span = sp }
                )
              Span = sp }
        )
      Span = sp }

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
    // docs' most-emphasised difference deserves better than a dump
    | "=", _, _ -> err opSpan "use '==' for equality; '=' binds in let and record fields"
    // composition [D:composition-operators] — fully parametric, typed
    // like a builtin scheme: (a -> b) >> (b -> c) : a -> c, `<<`
    // mirrored. FIRST in the match: the scalar-defaulting arms below
    // must never touch function operands. A non-function LHS on `>>`
    // gets the redirect-aware message (bash muscle memory).
    | (">>" | "<<"), lt, _ ->
        let a = TVar(freshName ctx "a")
        let b = TVar(freshName ctx "b")
        let c = TVar(freshName ctx "c")

        match op, lt with
        | ">>", (TFun _ | TVar _) ->
            bind ctx env l.Span (TFun(a, b)) l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span (TFun(b, c)) r.Ty)
            |> Result.map (fun () -> TFun(a, c))
        | "<<", (TFun _ | TVar _) ->
            bind ctx env l.Span (TFun(b, c)) l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span (TFun(a, b)) r.Ty)
            |> Result.map (fun () -> TFun(a, c))
        | ">>", ty ->
            err
                l.Span
                ($"'>>' composes functions, and this expression has type {formatTy ty}; "
                 + "to append command output to a file, pipe it: cmd |> File.append \"out.txt\"")
        | _, ty -> err l.Span $"'<<' composes functions, and this expression has type {formatTy ty}"
    | ("*" | "/" | "-" | ">" | "<" | ">=" | "<="), TVar _, TVar _ ->
        retryAfter (
            bind ctx env l.Span (TInt) l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span (TInt) r.Ty)
        )
    | ("&&" | "||"), TVar _, TVar _ ->
        retryAfter (
            bind ctx env l.Span TBool l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span TBool r.Ty)
        )
    | _, TVar _, ((TInt | TFloat | TStr | TBool | TDur | TSize | TInstant) as t) ->
        retryAfter (bind ctx env l.Span t l.Ty)
    | _, ((TInt | TFloat | TStr | TBool | TDur | TSize | TInstant) as t), TVar _ ->
        retryAfter (bind ctx env r.Span t r.Ty)
    | ("==" | "<>"), a, b ->
        // Eq via the class solver (Session A): concrete failures keep the
        // pre-class message verbatim; unresolved operands now DEFER (the
        // constraint rides the var) instead of rejecting at the operator
        bind ctx env opSpan a b
        |> Result.bind (fun () ->
            let p =
                { Cls = Cls.Eq
                  Span = opSpan
                  Describe =
                    fun t ->
                        if tyHasFloat t then
                            floatEqTeaching
                        else
                            $"'{op}' is not defined for {formatTy t}; sequences and functions cannot be compared" }

            demand ctx env p a |> Result.map (fun () -> TBool))
    | _, TVar _, TVar _ -> err opSpan $"cannot infer the operand types of '{op}'; pipe data in or use concrete values"
    | _, TRowVar _, _
    | _, _, TRowVar _ -> err opSpan $"operator '{op}' is not defined for records"
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
    // Instant arithmetic [D:instant]: two points SUBTRACT to the
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
                ""

        err r.Span $"expected {formatTy a}, got {formatTy b}{shorthandHint}"
    | _, a, _ -> err opSpan $"operator '{op}' is not defined for {formatTy a}"

let rec private funParams (ctx: Ctx) (arity: int) (ty: Ty) : (Ty list * Ty) option =
    if arity = 0 then
        Some([], ty)
    else
        match resolve ctx ty with
        | TFun(dom, cod) -> funParams ctx (arity - 1) cod |> Option.map (fun (ps, r) -> dom :: ps, r)
        | _ -> None

let rec private spine (e: Expr) : Expr * Expr list =
    match e.Kind with
    | EApp(fn, arg) ->
        let head, args = spine fn
        head, args @ [ arg ]
    | _ -> e, []

// the JSON boundary field law [D:json-option], the ONE list both
// directions share: a scalar (int/string/bool) or an Option of one.
// A missing key or an explicit null reads as None (a key is missing-or-
// null in the format, unlike env's absence-only or argv's flag). NOT
// Option<Option<_>>, NOT Option of a record or seq — the boundary needs
// a flat row.
let private jsonScalar (ty: Ty) : bool =
    match ty with
    | TInt
    | TFloat
    | TStr
    | TBool -> true
    | _ -> false

// the RECURSIVE json field law [D:recursive-fields]: a field is
// admitted if it is a scalar (int, float, string, bool), an Option of
// an admitted type, a record whose fields are all admitted, or a seq
// of an admitted type. `seen` is the ORDERED declaration path (the
// yaml guard's job, but a cycle names its path); `path` locates the
// offending field from the top — a recursive law's failures are deep
// and a category list alone will not find them.
let private jsonAdmittedSet =
    "json fields are int, float, string, bool, Option of an admitted type, a record of admitted fields, seq of an admitted type, or Map<string, T> of one"

let rec private jsonAdmitted
    (span: Span)
    (env: TypeEnv)
    (seen: string list)
    (path: string)
    (ty: Ty)
    : Result<unit, TypeError> =
    let at = if path = "" then "" else $"field '{path}': "

    match ty with
    | TInt
    | TFloat
    | TStr
    | TBool -> Ok()
    | TSize ->
        // parked [D:size]: JSON has no size convention (bytes-int and a
        // string both defensible; the choice wants a receipt)
        err
            span
            $"{at}Size is not representable in JSON — convert explicitly (Size.toBytes into an int field, or show for a string)"
    | TBytes -> err span $"{at}Bytes is not representable in JSON — Bytes.toBase64 into a string field"
    | TDur ->
        // parked [D:duration]: no duration convention (ms-int vs
        // ISO-8601 both defensible) — an honest rejection beats a guess
        err
            span
            $"{at}Duration is not representable in JSON — convert explicitly (Duration.toMillis into an int field, or show for a string)"
    | TInstant ->
        // parked with its siblings [D:instant]: timestamps have no single
        // wire convention (epoch int and ISO string both defensible)
        err
            span
            $"{at}an Instant is not representable in JSON — convert explicitly (Instant.epochMs into an int field, or show for an ISO 8601 string)"
    | TSecret ->
        // a Secret crossing to a wire format is almost certainly a
        // mistake [D:secret]; Secret.reveal is the deliberate spelling
        err
            span
            $"{at}a Secret must not cross to JSON — Secret.reveal it into a string field if you truly mean to write it"
    | TNamed("Option", [ TNamed("Option", _) ]) ->
        err span $"{at}Option<Option<…>> has no JSON reading; flatten the type"
    | TNamed("Option", [ inner ]) -> jsonAdmitted span env seen path inner
    | TSeq elem -> jsonAdmitted span env seen path elem
    // the ID-keyed object [D:map-string]: Map<string, T> is a JSON
    // object whose keys are data — admitted iff T is
    | TNamed("Map", [ TStr; inner ]) -> jsonAdmitted span env seen path inner
    // a non-string key reaches here through an ANONYMOUS shape (declared
    // types validate at declaration) — same teaching as validateTy
    | TNamed("Map", [ k; _ ]) ->
        err
            span
            $"{at}Map keys are strings only (every receipt has them — JSON object keys ARE strings); got Map<{formatTy k}, …>"
    | TNamed("Map", args) -> err span $"{at}Map takes two type arguments (Map<string, T>); got {args.Length}"
    | TNamed(n, []) when seen |> List.contains n ->
        let cycle =
            ((seen |> List.rev |> List.skipWhile ((<>) n)) @ [ n ]) |> String.concat " → "

        err span $"{at}the type cycle {cycle} cannot cross the JSON boundary — it needs finite trees"
    | TNamed(n, []) ->
        match Map.tryFind n env.Types with
        | Some(Record def) when def.Params.IsEmpty ->
            allOk def.Fields (fun (fn, fty) ->
                jsonAdmitted span env (n :: seen) (if path = "" then fn else $"{path}.{fn}") fty)
        | Some(Record _) -> err span $"{at}'{n}' is generic; the JSON boundary needs monomorphic records"
        | Some(Union _) -> err span $"{at}'{n}' is a union, which is not admitted; {jsonAdmittedSet}"
        | None -> err span $"{at}unknown type '{n}'{didYouMean n (Map.keys env.Types)}"
    | ty -> err span $"{at}type {formatTy ty} is not admitted; {jsonAdmittedSet}"

let private jsonableRecord (span: Span) (env: TypeEnv) (def: RecordDef) : Result<unit, TypeError> =
    allOk def.Fields (fun (name, ty) -> jsonAdmitted span env [ def.Name ] name ty)

let private jsonableElem (span: Span) (env: TypeEnv) (elem: Ty) : Result<unit, TypeError> =
    match elem with
    | TSecret ->
        err span "a Secret must not cross to JSON — Secret.reveal it into a string first if you truly mean to write it"
    | elem -> jsonAdmitted span env [] "" elem

// the defs a shape reaches, for the reader [D:recursive-fields]: eval
// converts nested objects without an env, so the closure rides the
// typed node (the yamlShape pattern, by table instead of by tree)
// the write-side rename table [D:wire-keys]: attrs are erased from
// VALUES, so `to json`/`to yaml` carry their record->field->wire map
// on the NODE, computed here at check (the adapter consumed the
// attribute at consumption — the attribute law's shape)
let rec wireRenamesOf (env: TypeEnv) (ty: Ty) : Map<string, Map<string, string>> =
    let defs = jsonDefsClosure env Map.empty ty

    defs
    |> Map.toList
    |> List.choose (fun (n, def) ->
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

and private jsonDefsClosure (env: TypeEnv) (acc: Map<string, RecordDef>) (ty: Ty) : Map<string, RecordDef> =
    match ty with
    | TNamed("Option", [ inner ]) -> jsonDefsClosure env acc inner
    | TSeq elem -> jsonDefsClosure env acc elem
    | TNamed("Map", [ TStr; inner ]) -> jsonDefsClosure env acc inner
    | TNamed(n, []) when not (acc.ContainsKey n) ->
        match Map.tryFind n env.Types with
        | Some(Record def) ->
            def.Fields
            |> List.fold (fun a (_, fty) -> jsonDefsClosure env a fty) (Map.add n def acc)
        | _ -> acc
    | _ -> acc

// ---- the yaml TREE law [D:yaml-v1] — richer than json's flat-row law
// because YAML is a DOCUMENT format, not a row stream: scalars, nested
// monomorphic records, seq<elem>, seq<string * elem> (an open mapping),
// Option anywhere (None omits / null reads None). `seen` guards
// declaration cycles.

// the from-side: build the shape eval will convert through
let rec private yamlShape (span: Span) (env: TypeEnv) (seen: Set<string>) (ty: Ty) : Result<Yaml.Shape, TypeError> =
    match ty with
    | TInt -> Ok Yaml.SInt
    | TSize ->
        err
            span
            "Size is not representable in yaml — convert explicitly (Size.toBytes into an int field, or show for a string)"
    | TBytes -> err span "Bytes is not representable in yaml — Bytes.toBase64 into a string field"
    | TDur ->
        err
            span
            "Duration is not representable in yaml — convert explicitly (Duration.toMillis into an int field, or show for a string)"
    | TSecret ->
        err span "a Secret must not cross to yaml — Secret.reveal it into a string first if you truly mean to write it"
    | TFloat -> Ok Yaml.SFloat
    | TStr -> Ok Yaml.SStr
    | TBool -> Ok Yaml.SBool
    | TNamed("Option", [ TNamed("Option", _) ]) -> err span "Option<Option<…>> has no yaml reading; flatten the type"
    | TNamed("Option", [ inner ]) -> yamlShape span env seen inner |> Result.map Yaml.SOpt
    | TSeq(TTuple [ TStr; v ]) -> yamlShape span env seen v |> Result.map Yaml.SPairs
    | TSeq elem -> yamlShape span env seen elem |> Result.map Yaml.SSeq
    | TNamed(n, []) ->
        if seen.Contains n then
            err span $"'{n}' is recursive; the yaml boundary needs finite trees"
        else
            match Map.tryFind n env.Types with
            | Some(Record def) when def.Params.IsEmpty ->
                def.Fields
                |> List.fold
                    (fun acc (fname, fty) ->
                        acc
                        |> Result.bind (fun fs ->
                            yamlShape span env (seen.Add n) fty
                            |> Result.map (fun s -> (fname, wireName def fname, s) :: fs)))
                    (Ok [])
                |> Result.map (fun fs -> Yaml.SRec(n, List.rev fs))
            | Some(Record _) -> err span $"'{n}' is generic; the yaml boundary needs monomorphic records"
            | Some(Union _) -> err span $"'{n}' is a union; the yaml tree law takes records, seqs, scalars, and Option"
            | None -> err span $"unknown type '{n}'{didYouMean n (Map.keys env.Types)}"
    | TVar v when v.StartsWith "__hole" ->
        // cascade suppression [PLAN-diagnostics-arc B6]: the hole means
        // an earlier error was already reported — never print its name
        Ok Yaml.SStr
    | ty ->
        err span $"type {formatTy ty} cannot cross the yaml boundary (scalars, records, seqs, seq<string * _>, Option)"

// the to-side: the same law, plus `Yaml` NODES render directly
let rec private yamlableOut (span: Span) (env: TypeEnv) (seen: Set<string>) (ty: Ty) : Result<unit, TypeError> =
    match ty with
    | TSize ->
        err
            span
            "Size is not representable in yaml — convert explicitly (Size.toBytes into an int field, or show for a string)"
    | TBytes -> err span "Bytes is not representable in yaml — Bytes.toBase64 into a string field"
    | TDur ->
        err
            span
            "Duration is not representable in yaml — convert explicitly (Duration.toMillis into an int field, or show for a string)"
    | TSecret ->
        err span "a Secret must not cross to yaml — Secret.reveal it into a string first if you truly mean to write it"
    | TInt
    | TFloat
    | TStr
    | TBool
    | TNamed("Yaml", []) -> Ok()
    | TNamed("Option", [ TNamed("Option", _) ]) -> err span "Option<Option<…>> has no yaml rendering; flatten the type"
    | TNamed("Option", [ inner ]) -> yamlableOut span env seen inner
    | TSeq(TTuple [ TStr; v ]) -> yamlableOut span env seen v
    | TSeq elem -> yamlableOut span env seen elem
    | TNamed(n, []) ->
        if seen.Contains n then
            err span $"'{n}' is recursive; the yaml boundary needs finite trees"
        else
            match Map.tryFind n env.Types with
            | Some(Record def) when def.Params.IsEmpty ->
                def.Fields
                |> List.fold
                    (fun acc (_, fty) -> acc |> Result.bind (fun () -> yamlableOut span env (seen.Add n) fty))
                    (Ok())
            | Some(Record _) -> err span $"'{n}' is generic; the yaml boundary needs monomorphic records"
            | Some(Union _) ->
                err span $"'{n}' is a union; the yaml tree law takes records, seqs, scalars, Option, and Yaml nodes"
            | None -> err span $"unknown type '{n}'"
    | TVar v when v.StartsWith "__hole" ->
        // cascade suppression [PLAN-diagnostics-arc B6]
        Ok()
    | ty ->
        err
            span
            $"type {formatTy ty} cannot cross the yaml boundary (scalars, records, seqs, seq<string * _>, Option, Yaml)"

// One Regex instance per distinct literal, shared by check and eval
// (the snippet-hash-cache precedent). INTERPRETED mode only —
// RegexOptions.Compiled is Reflection.Emit, banned by the AOT rule
// [D:regex-pattern].
let private regexCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex>()

let compileRegex (pat: string) : Result<System.Text.RegularExpressions.Regex, string> =
    match regexCache.TryGetValue pat with
    | true, rx -> Ok rx
    | _ ->
        try
            let rx = System.Text.RegularExpressions.Regex pat

            // named groups REJECT [D:no-named-groups]: .NET numbers
            // positional groups FIRST and named ones after — the outlier
            // among engines, so binders would follow neither the pattern's
            // reading order nor the user's Perl/Python experience. Weir
            // names captures at the BINDER instead. Detection by the
            // ENGINE'S OWN group table — lookbehind (?<= (?<! and
            // non-capturing (?: create no groups, so they can never
            // false-positive here (the one place a hand-rolled scan could
            // go wrong).
            let named =
                rx.GetGroupNames()
                |> Array.tryFind (fun n -> not (n |> Seq.forall System.Char.IsDigit))

            match named with
            | Some n ->
                Error
                    $"named group '(?<{n}>' — weir names captures at the binder, not in the pattern: use a plain (…) group and name it in the binder list"
            | None ->
                regexCache[pat] <- rx
                Ok rx
        with ex ->
            Error ex.Message

let rec private checkPattern (env: TypeEnv) (ty: Ty) (p: Pattern) : Result<(string * Ty) list, TypeError> =
    match p.PKind with
    | PWildcard -> Ok []
    | PVar name -> Ok [ name, ty ]
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
                 let! hb = checkPattern env elem h
                 let! tb = checkPattern env ty t
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
                     |> Result.bind (fun bs -> checkPattern env elem sub |> Result.map (fun b -> bs @ b)))
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
    | PTuple ps ->
        match ty with
        | TTuple ts when List.length ps = List.length ts ->
            List.zip ps ts
            |> List.fold
                (fun acc (subP, subTy) ->
                    acc
                    |> Result.bind (fun bs -> checkPattern env subTy subP |> Result.map (fun b -> bs @ b)))
                (Ok [])
        | TTuple ts -> err p.PSpan $"this tuple pattern has {List.length ps} elements; the value has {List.length ts}"
        | ty ->
            // the bare-comma precedence footgun [D:user-language-messages]:
            // `code, _ :: rest` parses as a TUPLE whose last element is a
            // cons (`,` groups looser than `::`), so a seq value lands here,
            // not a tuple. The repair IS the grouping, so NAME it — but only
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
    | PCase(ctor, argPat) ->
        match ty with
        | TNamed(typeName, targs) ->
            match Map.tryFind typeName env.Types with
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
                    | Some ap -> checkPattern env payloadTy ap
                    | None -> err p.PSpan $"'{ctor}' carries {formatTy payloadTy}; add a pattern for it"
            | Some(Record _) -> err p.PSpan $"{typeName} is a record; only a name or '_' can match it"
            | None -> err p.PSpan $"unknown type '{typeName}'"
        | ty -> err p.PSpan $"constructor patterns need a union value; this one has type {formatTy ty}"

// A binder pattern's SHAPE: fresh vars at the leaves, TUnit at (),
// tuples composed — bound against the RHS type, so components resolve
// by unification. Refutable kinds (literals, constructors) are the
// located check error the plan's contract names.
// The casing law [D:lowercase-binds] applies at every binder
// position; fields and match patterns are deliberately untouched.
let casingError (span: Span) (name: string) : Result<'a, TypeError> =
    err
        span
        ($"binding names start lowercase; uppercase names are types, modules, and constructors"
         + $" — bind '{name.ToLowerInvariant()}' (a record field keeps its name: let region = cfg.AWS_REGION)")

let checkBinderName (span: Span) (name: string) : Result<unit, TypeError> =
    if name.Length > 0 && System.Char.IsUpper name[0] then
        casingError span name
    else
        Ok()

let rec private binderShape (ctx: Ctx) (env: TypeEnv) (p: Pattern) : Result<Ty * (string * Ty) list, TypeError> =
    match p.PKind with
    | PVar n ->
        checkBinderName p.PSpan n
        |> Result.map (fun () ->
            let t = TVar(freshName ctx "a")
            t, [ n, t ])
    | PWildcard -> Ok(TVar(freshName ctx "a"), [])
    | PUnit -> Ok(TUnit, [])
    | PTuple ps ->
        ps
        |> List.fold
            (fun acc sub ->
                acc
                |> Result.bind (fun (ts, bs) -> binderShape ctx env sub |> Result.map (fun (t, b) -> t :: ts, bs @ b)))
            (Ok([], []))
        |> Result.map (fun (ts, bs) -> TTuple(List.rev ts), bs)
    | PCase(ctor, _) when not (Map.containsKey ctor env.Values) ->
        // an unknown uppercase name in a binder is the casing law's
        // case (a function/name spelled uppercase), not refutability
        casingError p.PSpan ctor
    | PBool _
    | PInt _
    | PStr _
    | PRegex _
    | PSeqNil
    | PCons _
    | PSeqList _
    | PCase _ -> err p.PSpan "this pattern can fail; use match"

// row provenance [D:row-provenance]: field-access positions for the
// given row vars, PHYSICAL — logical spans cannot cross the statement
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
            Map.tryFind v ctx.Rows
            |> Option.map (fun fields ->
                let prior = Map.tryFind v ctx.RowOrigins |> Option.defaultValue []

                v,
                [ for KeyValue(field, (_, fspan)) in fields ->
                      match prior |> List.tryFind (fun (f, _, _, _) -> f = field) with
                      | Some o -> o
                      | None ->
                          let pl, pc = tr fspan.Start.Col
                          field, pl, pc, fspan.End.Col - fspan.Start.Col ]))
        |> Map.ofList

// THE scheme-scooping rule, one implementation: free vars beyond the
// env generalize; their pending constraints scoop INTO the scheme
// (removed from ctx). Previously verbatim ×3 (both ELet arms + the
// binding case below) — a scooping fix in one copy and not the others
// would have been a silent generalization bug.
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

    // scooped constraints move INTO the scheme
    ctx.Cons <- cs |> Map.fold (fun m v _ -> Map.remove v m) ctx.Cons

    { Forall = fa
      Cs = cs
      Ty = valueTy
      RowOrigins = origins
      HoleDefaults = [] }

// per-name generalization for destructuring binders: each bound name's
// type generalizes INDEPENDENTLY against the env (constraints scooped
// per name from the shared ctx) — generalizeLet's per-name sibling
let private generalizeBinding (ctx: Ctx) (env: TypeEnv) (name: string, ty: Ty) : string * Scheme =
    name, generalizeLet ctx env (finalTy ctx ty)

let rec private isIrrefutablePat (p: Pattern) =
    match p.PKind with
    | PWildcard
    | PVar _
    | PUnit -> true
    | PTuple ps -> ps |> List.forall isIrrefutablePat
    | PBool _
    | PInt _
    | PStr _
    | PRegex _
    | PSeqNil
    | PCons _
    | PSeqList _
    | PCase _ -> false

// Exhaustiveness [D:exhaustiveness-hard-error]. Only unguarded arms
// count — a guarded arm can fail at runtime. Coverage is RECURSIVE
// through union payloads (Some (Some x) / Some None / None is
// exhaustive): a hard error must not reject genuinely-total matches.
let rec private missingCases (env: TypeEnv) (ty: Ty) (pats: Pattern list) : string list =
    if pats |> List.exists isIrrefutablePat then
        []
    else
        match ty with
        | TNamed(name, targs) ->
            match Map.tryFind name env.Types with
            | Some(Union def) ->
                def.Cases
                |> List.filter (fun (case, payload) ->
                    let uncovered =
                        match payload with
                        | None ->
                            pats
                            |> List.exists (fun p ->
                                match p.PKind with
                                | PCase(c, None) -> c = case
                                | _ -> false)
                            |> not
                        | Some payloadTy ->
                            let payloadPats =
                                pats
                                |> List.choose (fun p ->
                                    match p.PKind with
                                    | PCase(c, Some arg) when c = case -> Some arg
                                    | _ -> None)

                            payloadPats.IsEmpty
                            || not (
                                missingCases env (substParams def.Params targs payloadTy) payloadPats
                                |> List.isEmpty
                            )

                    uncovered)
                |> List.map fst
            | _ -> []
        | TBool ->
            [ if not (pats |> List.exists (fun p -> p.PKind = PBool true)) then
                  "true"
              if not (pats |> List.exists (fun p -> p.PKind = PBool false)) then
                  "false" ]
        | TInt
        | TStr ->
            // literal patterns never complete a match alone (F#'s rule,
            // oracle-pinned): a var or wildcard arm must close it
            [ "_" ]
        | TTuple _ ->
            // bounded rule: only an all-irrefutable tuple arm (or _/var)
            // completes; per-component product analysis is out of scope
            // (tuple-exhaustiveness-bounded divergence row)
            [ "_" ]
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

            [ if not nilCovered then
                  "[]"
              if not consCovered then
                  "_ :: _" ]
        | _ -> []

let private exhaustive
    (env: TypeEnv)
    (span: Span)
    (scrutTy: Ty)
    (arms: (Pattern * 'g option) list)
    : Result<unit, TypeError> =
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
        let later = List.length arms - i - 1

        let tail =
            if later = 1 then
                "the arm below is unreachable"
            else
                $"the {later} arms below are unreachable"

        match p.PKind with
        | PVar name ->
            let hint =
                match scrutTy with
                | TNamed(tyName, _) ->
                    match Map.tryFind tyName env.Types with
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
            // [D:seq-patterns]: [] + irrefutable-cons is a complete
            // seq analysis (F#'s list rule)
            | TSeq _ ->
                match missingCases env scrutTy unguarded with
                | [] -> Ok()
                | missing ->
                    let missingList = String.concat ", " missing
                    err span $"match is not exhaustive; missing: {missingList}"
            | ty -> err span $"match on {formatTy ty} needs a catch-all pattern"

// [D:lambda-core] Flag 7 discharged: the five lambda arms (infer:
// unit/name/pattern; check-mode: name/pattern) share ONE assembly
// core — env extension, body typing, TFun construction. Each adapter
// keeps only its judgment delta (domain source + body strategy).
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

        return
            { Kind = mkKind tbody
              Ty = TFun(dom, tbody.Ty)
              Span = span }
    }

let rec private infer (ctx: Ctx) (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
    match expr.Kind with
    | EInt n ->
        Ok
            { Kind = TEInt n
              Ty = TInt
              Span = expr.Span }
    | EDur n ->
        Ok
            { Kind = TEDur n
              Ty = TDur
              Span = expr.Span }
    | EFloat f ->
        Ok
            { Kind = TEFloat f
              Ty = TFloat
              Span = expr.Span }
    | ESize b ->
        Ok
            { Kind = TESize b
              Ty = TSize
              Span = expr.Span }
    | ERetry(isPoll, opts, watch, body, until) ->
        result {
            let famTy = TNamed((if isPoll then "Poll" else "Retry"), [])
            let head = if isPoll then "poll" else "retry"
            let! topts = check ctx env opts famTy

            // watch= is poll's key [D:scoped-procs]: poll waits for
            // ready (a watched child dying IS the answer); retry
            // retries FAILURES — a dead process is not a transient one
            do!
                match watch with
                | Some w when not isPoll ->
                    err w.Span "watch= is poll's key (wait-for-ready); retry retries failures — run the wait under poll"
                | _ -> Ok()

            let! twatch =
                match watch with
                | Some w -> check ctx env w (TNamed("Proc", [])) |> Result.map Some
                | None -> Ok None

            let! tbody = infer ctx env body

            match until with
            | Some((b, _), pred) ->
                // a bool body IS its own predicate [D:retry-poll]: an
                // until segment on top of one is a contradiction
                match resolve ctx tbody.Ty with
                | TBool ->
                    return!
                        err
                            body.Span
                            $"this {head} body yields bool, so it IS the predicate — drop the until segment (a different condition belongs in the body itself)"
                | bodyTy ->
                    let env' =
                        { env with
                            Values = Map.add b (generalize bodyTy) env.Values }

                    let! tpred = check ctx env' pred TBool

                    return
                        { Kind = TERetry(isPoll, topts, twatch, tbody, Some(b, tpred))
                          Ty = bodyTy
                          Span = expr.Span }
            | None ->
                match resolve ctx tbody.Ty with
                | TBool
                | TVar _ ->
                    do! bind ctx env body.Span TBool tbody.Ty

                    return
                        { Kind = TERetry(isPoll, topts, twatch, tbody, None)
                          Ty = TUnit
                          Span = expr.Span }
                | bodyTy ->
                    return!
                        err
                            body.Span
                            $"{head} without an until segment needs a bool body (the body IS the predicate); this one yields {formatTy bodyTy} — add `until r` to bind the value, or end the body with a condition (cmd | succeeds)"
        }
    | EStr s ->
        Ok
            { Kind = TEStr s
              Ty = TStr
              Span = expr.Span }
    | EBool b ->
        Ok
            { Kind = TEBool b
              Ty = TBool
              Span = expr.Span }
    | EUnit ->
        Ok
            { Kind = TEUnit
              Ty = TUnit
              Span = expr.Span }
    | ECapture inner ->
        // erased [D:district-retirement]: the marker exists for the
        // statement gate; typing sees straight through
        infer ctx env inner
    | EAlways(body, cleanup) ->
        // the bare scope [D:within-always]: the scope's type IS the
        // body's; the always block is teardown and must be unit. exit
        // is REFUSED inside always (lexically — a shadowing binding is
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
                    Error
                        { Message =
                            "exit inside always is refused — teardown of an unwinding scope must finish (the original error would become a code); fail \"msg\" carries a message instead"
                          Span = sp
                          Origin = None }
                | None -> Ok()

            let! tbody = infer ctx env body
            let! tcleanup = check ctx env cleanup TUnit

            return
                { Kind = TEAlways(tbody, tcleanup)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | EWithin(kind, binder, arg, opts, body) ->
        // tmp binds its path (a plain string, platform-native); cd
        // consumes a string path; env consumes seq<EnvVar>; lock
        // consumes a string path (+ timeout: Duration) — the arg
        // types are the kinds' contracts [D:within-scopes]; the
        // scope's type IS the body's type
        result {
            let! targ =
                match kind, arg with
                | "cd", Some a -> check ctx env a TStr |> Result.map Some
                | "env", Some a -> check ctx env a (TSeq(TNamed("EnvVar", []))) |> Result.map Some
                | "lock", Some a -> check ctx env a TStr |> Result.map Some
                // proc's arg is the COMMAND node [D:scoped-procs] — typed
                // as any command (the parser guarantees ECmd), evaluated
                // by the scope as a spawn, never as a statement
                | "proc", Some a -> infer ctx env a |> Result.map Some
                | _ -> Ok None

            let! topts =
                match opts with
                | Some o -> check ctx env o TDur |> Result.map Some
                | None -> Ok None

            let benv =
                match binder with
                | Some(n, _) -> bindParams env [ n, (if kind = "proc" then TNamed("Proc", []) else TStr) ]
                | None -> env

            let! tbody = infer ctx benv body

            return
                { Kind = TEWithin(kind, binder |> Option.map fst, targ, topts, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | ESeq(first, rest) ->
        result {
            let! tfirst = infer ctx env first

            match resolve ctx tfirst.Ty with
            | TUnit ->
                let! trest = infer ctx env rest

                return
                    { Kind = TESeq(tfirst, trest)
                      Ty = trest.Ty
                      Span = expr.Span }
            | ty -> return! err first.Span (seqUnitError first ty)
        }
    | EVar(("print" | "printerr" | "|print") as pname) when isPrintFamily env pname ->
        // Bare-value position (e.g. Seq.iter print): defer like a splice
        // — the use site decides the type, the boundary validates it,
        // and only a genuinely undetermined use defaults to string
        let ty = instantiate ctx expr.Span printScheme

        (match ty with
         | TFun(TVar v, _) -> ctx.PendingPrints <- (v, expr.Span) :: ctx.PendingPrints
         | _ -> ())

        Ok
            { Kind = TEVar pname
              Ty = ty
              Span = expr.Span }
    | EVar name ->
        match Map.tryFind name env.Values with
        | Some sch ->
            Ok
                { Kind = TEVar name
                  Ty = instantiate ctx expr.Span sch
                  Span = expr.Span }
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

                match homes with
                // no history claim [D:bare-rule]: most of these were
                // never bare — the wording matches the multi-home arm's
                | [ one ] -> err expr.Span $"'{name}' is module-qualified; use '{one}'"
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
                        // wrong suggestion ('Post') sent readers somewhere
                        // confidently — the registry answer is the repair
                        err
                            expr.Span
                            $"'{name}' is not a weir module — weir's sequences are 'Seq' (Seq.length, Seq.map, ...; one sequence type)"
                    | None ->
                        // same-kind candidates only (PLAN-dx-review D5): a
                        // lowercase name never suggests a constructor and
                        // an uppercase one never suggests a binding — but a
                        // candidate differing ONLY by case stays (Exit ->
                        // exit is the rename teaching [D:exit-rename])
                        let sameCase (c: string) =
                            (c.Length > 0 && System.Char.IsUpper c[0] = System.Char.IsUpper name[0])
                            || System.String.Equals(c, name, System.StringComparison.OrdinalIgnoreCase)

                        let hint =
                            didYouMean name (Map.keys env.Values |> Seq.filter Types.isUserName |> Seq.filter sameCase)

                        err expr.Span $"unbound variable '{name}'{hint}"
    | ELet(name, nameSpan, value, body) ->
        result {
            do! checkBinderName expr.Span name
            let! tvalue = infer ctx env value
            let valueTy = finalTy ctx tvalue.Ty

            let scheme = generalizeLet ctx env valueTy

            let! tbody =
                infer
                    ctx
                    { env with
                        Values = Map.add name scheme env.Values }
                    body

            return
                { Kind = TELet(name, nameSpan, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | ELambdaPat(pat, body) ->
        result {
            let! shape, binds = binderShape ctx env pat
            return! lambdaCore env expr.Span (fun tb -> TELambdaPat(pat, tb)) shape binds (fun e -> infer ctx e body)
        }
    | ELetPat(pat, value, body) ->
        result {
            // binder judged FIRST: casing/refutability errors beat any
            // error inside the value (the binder is what the user wrote)
            let! shape, binds = binderShape ctx env pat
            let! tvalue = infer ctx env value
            do! bind ctx env pat.PSpan shape tvalue.Ty

            let schemes = binds |> List.map (generalizeBinding ctx env)

            let! tbody =
                infer
                    ctx
                    { env with
                        Values = schemes |> List.fold (fun vs (n, s) -> Map.add n s vs) env.Values }
                    body

            return
                { Kind = TELetPat(pat, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | ELambda("()", pspan, body) ->
        // the unit param PINS its type — desugaring to an unconstrained
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
    | EApp _ ->
        let head, args = spine expr

        // unapplied ONLY [D:operator-values]: a partially applied infix
        // operator reads backwards — (>) 10 would mean fun x -> 10 > x,
        // which is exactly the direction authors get wrong (the
        // field-predicates exploration's evidence). The teaching shows
        // both spellings so the direction is chosen, not guessed.
        match head.Kind with
        | EOpValue op ->
            err
                head.Span
                ($"an operator cannot be partially applied — ({op}) v would mean fun x -> v {op} x, "
                 + $"which reads backwards; write the lambda with its direction explicit: fun x -> x {op} v (or fun x -> v {op} x)")
        | _ ->

            // the WRAP-IT hint [D:district-retirement]: a bare command in an
            // expression FRAGMENT reads as an application of its unbound head
            // word — the repair is the wrapper, so the rejection names it
            // (did-you-mean coexists; the specific teachings — module homes,
            // retirements, scriptPath — keep their own arms via fall-through)
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

            match wrapItHint with
            | Error e -> Error e
            | Ok() ->

                // the named / qualified record literal [D:modules-v1]: `Ctx { .. }`
                // parses as EApp(EVar Ctx, ERecord) and `Git.Ctx { .. }` as
                // EApp(EField(Git, Ctx), ERecord). A record TYPE name is never a
                // value, so this only fires where a bare application would ERROR —
                // zero movement on `Some { .. }` (Some is a ctor, not a type).
                let namedRecord (typeName: string) (fields: (string * Span * Expr) list) =
                    result {
                        let def =
                            match Map.tryFind typeName env.Types with
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
                                    [ if not (Set.isEmpty (Set.difference defNames litNames)) then
                                          $"missing {show (Set.difference defNames litNames)}"
                                      if not (Set.isEmpty (Set.difference litNames defNames)) then
                                          $"unknown {show (Set.difference litNames defNames)}" ]
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

                                return
                                    { Kind = TERecord(def.Name, List.rev tfields)
                                      Ty = TNamed(def.Name, targs)
                                      Span = expr.Span }
                    }

                match head.Kind, args with
                | EVar tyName, [ { Kind = ERecord fields } ] when
                    (match Map.tryFind tyName env.Types with
                     | Some(Record _) -> true
                     | _ -> false)
                    && not (Map.containsKey tyName env.Values)
                    ->
                    namedRecord tyName fields
                | EField({ Kind = EVar m }, tyName, _), [ { Kind = ERecord fields } ] when
                    (match Map.tryFind m env.ModuleTypes with
                     | Some ts -> Set.contains tyName ts
                     | None -> false)
                    && (match Map.tryFind tyName env.Types with
                        | Some(Record _) -> true
                        | _ -> false)
                    ->
                    namedRecord tyName fields
                | EField({ Kind = EVar "Env" }, "load", _), [ arg ] when
                    not (Map.containsKey "Env" env.Values) && Map.containsKey "Env" env.Modules
                    ->
                    // Env.load T — a typed-boundary instance (from json, env).
                    // Imitates from-json's type-name-in-special-position
                    // resolution, relocated to expression position.
                    (match arg.Kind with
                     | EVar tyName ->
                         match Map.tryFind tyName env.Types with
                         | Some(Record def) when def.Params.IsEmpty ->
                             // an ENUM field [D:env-enums]: a monomorphic union,
                             // every case 0-arity — the declared set becomes a
                             // boundary conversion exactly like int/bool
                             let unionOf ft =
                                 match ft with
                                 | TNamed(n, [])
                                 | TNamed("Option", [ TNamed(n, []) ]) ->
                                     match Map.tryFind n env.Types with
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
                                 // env is THE secret channel in CI [D:secret]:
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

                             // a payload-carrying case is a SCHEMA error, named at
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
                                             // the resting-point cells under ENV's field law
                                             // [D:default-attr]: text bools carry no presence
                                             // semantics, so BOTH Default literals are legal
                                             // here (the Args-side false-is-redundant cell
                                             // flips — validation is the consumer's arm)
                                             // [D:default-attr]: bool-false is LEGAL here — the
                                             // flip cell; both Default rules sit adjacent in Argv
                                             match Argv.badEnvDefault def with
                                             | Some msg -> err arg.Span msg
                                             | None ->
                                                 let enums =
                                                     def.Fields
                                                     |> List.choose (fun (_, ft) ->
                                                         unionOf ft
                                                         |> Option.map (fun (n, u) -> n, u.Cases |> List.map fst))
                                                     |> Map.ofList

                                                 Ok
                                                     { Kind = TEEnvLoad(def, enums)
                                                       Ty = TNamed(tyName, [])
                                                       Span = expr.Span }
                         | Some(Record def) ->
                             err arg.Span $"Env.load needs a monomorphic record; '{tyName}' is generic"
                         | Some(Union _) -> err arg.Span $"'{tyName}' is a union; Env.load needs a record"
                         | None -> err arg.Span $"unknown type '{tyName}'{didYouMean tyName (Map.keys env.Types)}"
                     | _ -> err arg.Span "Env.load takes a record type name, e.g. Env.load Config")
                | EField({ Kind = EVar "Args" }, "load", _), [ arg ] when
                    not (Map.containsKey "Args" env.Values) && Map.containsKey "Args" env.Modules
                    ->
                    // Args.load T — the sixth typed-boundary instance [D:typed-argv]:
                    // Env.load's sibling; the union acceptance is the delta
                    // script-mode signal = the Self module [D:self-module] (injected
                    // per-run by baseEnvs; absent in the REPL and -e). Args.load reads
                    // Session.ScriptArgs at eval, so this is purely the availability gate.
                    (if not (Map.containsKey "Self" env.Modules) then
                         err expr.Span "Args.load is script-only (Self.args is not available here)"
                     else
                         let validateFields span (label: string) (def: RecordDef) =
                             // Default cells / field shapes / flag collisions —
                             // the policy lives in Argv [D:typed-argv]; the arm
                             // keeps the span plumbing
                             match Argv.fieldProblems label def with
                             | Some msg -> err span msg
                             | None -> Ok()

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
                                         match Map.tryFind rn env.Types with
                                         | Some(Record rdef) when rdef.Params.IsEmpty ->
                                             validateFields span $"case '{c}': " rdef
                                             |> Result.bind (fun () -> buildPayloads (Map.add c rdef acc) rest)
                                         | _ -> payloadErr c
                                     | (c, Some _) :: _ -> payloadErr c

                                 buildPayloads Map.empty udef.Cases

                         match arg.Kind with
                         | EVar tyName ->
                             match Map.tryFind tyName env.Types with
                             | Some(Record def) when def.Params.IsEmpty ->
                                 // the field law [D:shared-flags]: at most ONE
                                 // union-typed field — the subcommand slot; its
                                 // scalar siblings are shared flags
                                 let unionFields =
                                     def.Fields
                                     |> List.choose (fun (f, ft) ->
                                         match ft with
                                         | TNamed(n, []) ->
                                             match Map.tryFind n env.Types with
                                             | Some(Union u) when u.Params.IsEmpty -> Some(f, u)
                                             | _ -> None
                                         | _ -> None)

                                 match unionFields with
                                 | [] ->
                                     result {
                                         do! validateFields arg.Span "" def

                                         return
                                             { Kind = TEArgsLoad(ArgsRecord def)
                                               Ty = TNamed(tyName, [])
                                               Span = expr.Span }
                                     }
                                 | [ (uf, udef) ] ->
                                     result {
                                         let sharedDef = Argv.sharedOf def uf

                                         // the subcommand slot derives no flag —
                                         // Default has nothing to rest [D:default-attr]
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

                                         // a name declared in BOTH tiers is a schema
                                         // error — reject-don't-guess; the runtime
                                         // scanner never faces the question
                                         // minted --no-X twins ride in both tiers'
                                         // namespaces [D:default-attr]
                                         let sharedFlags =
                                             (sharedDef.Fields |> List.map (fun (f, _) -> Argv.kebabFlag f))
                                             @ (Argv.mintedFlags sharedDef |> List.map snd)
                                             |> Set.ofList

                                         let sharedShorts = Argv.explicitShorts sharedDef |> List.map snd |> Set.ofList

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
                                             return
                                                 { Kind = TEArgsLoad(ArgsShared(def, uf, udef, payloads))
                                                   Ty = TNamed(tyName, [])
                                                   Span = expr.Span }
                                     }
                                 | (a, _) :: (b, _) :: _ ->
                                     err
                                         arg.Span
                                         $"'{a}' and '{b}' are both union-typed: one subcommand slot per record"
                             | Some(Union udef) when udef.Params.IsEmpty ->
                                 result {
                                     let! payloads = unionPayloads arg.Span udef

                                     return
                                         { Kind = TEArgsLoad(ArgsUnion(udef, payloads))
                                           Ty = TNamed(tyName, [])
                                           Span = expr.Span }
                                 }
                             | Some(Record _)
                             | Some(Union _) ->
                                 err arg.Span $"Args.load needs a monomorphic type; '{tyName}' is generic"
                             | None -> err arg.Span $"unknown type '{tyName}'{didYouMean tyName (Map.keys env.Types)}"
                         | _ -> err arg.Span "Args.load takes a type name, e.g. Args.load Cli")
                | EVar(("print" | "printerr" | "|print") as pname), [ arg ] when isPrintFamily env pname ->
                    result {
                        let! targ = infer ctx env arg
                        let! argTy = printArgTy ctx env arg.Span targ.Ty

                        let tprint =
                            { Kind = TEVar pname
                              Ty = TFun(argTy, TUnit)
                              Span = head.Span }

                        return
                            { Kind = TEApp(tprint, targ)
                              Ty = TUnit
                              Span = expr.Span }
                    }
                | _ -> checkSpine ctx env head args None
    | EPipe(arg, ({ Kind = ETo fmt } as toExpr)) ->
        result {
            let! targ = infer ctx env arg

            match fmt, resolve ctx targ.Ty with
            | "json", TSeq elem ->
                do! jsonableElem toExpr.Span env (resolve ctx elem)

                let tto =
                    { Kind = TETo(fmt, wireRenamesOf env targ.Ty)
                      Ty = TFun(targ.Ty, TSeq TStr)
                      Span = toExpr.Span }

                return
                    { Kind = TEPipe(targ, tto)
                      Ty = TSeq TStr
                      Span = expr.Span }
            | "json", ty -> return! err arg.Span $"'to json' needs a seq, got {formatTy ty}"
            | "yaml", ty ->
                // to yaml [D:yaml-v1]: a SEQ renders `---`-separated
                // documents; a single yamlable value renders ONE document
                // (yaml is a document format — the seq-only rule is json's,
                // a row format's). A top-level seq<string * _> is ONE
                // mapping document, not documents-of-pairs.
                do!
                    match ty with
                    | TSeq(TTuple [ TStr; _ ]) -> yamlableOut toExpr.Span env Set.empty ty
                    | TSeq elem -> yamlableOut toExpr.Span env Set.empty (resolve ctx elem)
                    | ty -> yamlableOut toExpr.Span env Set.empty ty

                let tto =
                    { Kind = TETo("yaml", wireRenamesOf env targ.Ty)
                      Ty = TFun(targ.Ty, TSeq TStr)
                      Span = toExpr.Span }

                return
                    { Kind = TEPipe(targ, tto)
                      Ty = TSeq TStr
                      Span = expr.Span }
            | fmt, _ -> return! err toExpr.Span $"unknown output format '{fmt}'; available: json, yaml"
        }
    | EPipe(arg, ({ Kind = ECmd _ } as cmdExpr)) ->
        result {
            let! targ = infer ctx env arg
            // a value-headed pipeline [D:value-headed-pipe] feeds the LHS as
            // stdin — seq<string> EXACTLY, with the twin teachings pointing
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

            return
                { Kind = TEPipe(targ, tcmd)
                  Ty = TSeq TStr
                  Span = expr.Span }
        }
    | EPipe(arg, ({ Kind = EVar(("print" | "printerr" | "|print") as pname) } as printExpr)) when
        isPrintFamily env pname
        ->
        result {
            let! targ = infer ctx env arg
            let! argTy = printArgTy ctx env arg.Span targ.Ty

            let tprint =
                { Kind = TEVar pname
                  Ty = TFun(argTy, TUnit)
                  Span = printExpr.Span }

            return
                { Kind = TEPipe(targ, tprint)
                  Ty = TUnit
                  Span = expr.Span }
        }
    | EPipe(_,
            { Kind = EBinOp(op, _, _)
              Span = opSpan }) when op <> ">>" && op <> "<<" ->
        // Scalar operators yield values, never functions, so piping into
        // one is always wrong — and usually a precedence surprise
        // (agent-dogfooding finding). Composition is the exception
        // [D:composition-operators]: `xs |> f >> g` pipes into the
        // composed FUNCTION, the F# idiom, and takes the general arm.
        err
            opSpan
            $"'{op}' binds tighter than '|>', so this parses as xs |> (a {op} b); parenthesize the pipeline: (xs |> f) {op} value"
    | EPipe(arg, fnExpr) ->
        result {
            let! targ = infer ctx env arg
            let head, args = spine fnExpr
            let! tfn = checkSpine ctx env head args (Some(targ.Ty, arg.Span))

            match resolve ctx tfn.Ty with
            | TFun(_, resultTy) ->
                return
                    { Kind = TEPipe(targ, tfn)
                      Ty = resultTy
                      Span = expr.Span }
            | _ -> return! err fnExpr.Span "the right side of a pipe must be a function"
        }
    | EField({ Kind = EVar m }, field, fieldSpan) when
        not (Map.containsKey m env.Values) && Map.containsKey m env.Modules
        ->
        result {
            let members = env.Modules[m]

            match Map.tryFind field members with
            | Some sch ->
                return
                    { Kind = TEVar $"{m}.{field}"
                      Ty = instantiate ctx expr.Span sch
                      Span = expr.Span }
            | None ->
                match retiredMember m field with
                | Some teach -> return! err fieldSpan $"'{m}.{field}' is retired: {teach}"
                | None when isSpecialMember m field ->
                    // load is a bespoke ARM, not a member — reaching here
                    // means the shape missed it: a space inside the type
                    // name (two arguments), extra arguments, or none
                    // [PLAN-diagnostics-arc A1]
                    let union = if m = "Args" then " or union" else ""

                    return! err fieldSpan $"{m}.load takes ONE record{union} type name, e.g. {m}.load Config"
                | None ->
                    let hint = didYouMean field (Map.keys members)
                    return! err fieldSpan $"module {m} has no member '{field}'{hint}"
        }
    | EField(target, field, fieldSpan) ->
        result {
            let! ttarget = infer ctx env target

            match resolve ctx ttarget.Ty with
            | TNamed(typeName, targs) ->
                match Map.tryFind typeName env.Types with
                | Some(Record def) ->
                    match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
                    | Some(_, fieldTy) ->
                        return
                            { Kind = TEField(ttarget, field)
                              Ty = substParams def.Params targs fieldTy
                              Span = expr.Span }
                    | None ->
                        match retiredField typeName field with
                        | Some teach -> return! err fieldSpan $"'{field}' is retired: {teach}"
                        | None ->
                            let hint = didYouMean field (List.map fst def.Fields)
                            return! err fieldSpan $"{typeName} has no field '{field}'{hint}"
                | Some(Union _) ->
                    return! err fieldSpan $"{typeName} is a union; match on it instead of accessing fields"
                | None -> return! err target.Span $"unknown type '{typeName}'"
            | TVar v ->
                let r = freshName ctx "r"
                ctx.Subst <- Map.add v (TRowVar(r, [])) ctx.Subst
                let fieldTy = TVar(freshName ctx "a")
                ctx.Rows <- Map.add r (Map [ field, (fieldTy, fieldSpan) ]) ctx.Rows

                return
                    { Kind = TEField(ttarget, field)
                      Ty = fieldTy
                      Span = expr.Span }
            | TRowVar(r, _) ->
                let existing = Map.tryFind r ctx.Rows |> Option.defaultValue Map.empty

                match Map.tryFind field existing with
                | Some(fieldTy, _) ->
                    return
                        { Kind = TEField(ttarget, field)
                          Ty = fieldTy
                          Span = expr.Span }
                | None ->
                    let fieldTy = TVar(freshName ctx "a")
                    ctx.Rows <- Map.add r (Map.add field (fieldTy, fieldSpan) existing) ctx.Rows

                    return
                        { Kind = TEField(ttarget, field)
                          Ty = fieldTy
                          Span = expr.Span }
            | ty -> return! err target.Span $"only records have fields; this expression has type {formatTy ty}"
        }
    | EOpValue op ->
        // desugar to EXACTLY `fun a b -> a op b` [D:operator-values]:
        // every typing question — overload-by-context, int defaulting,
        // the Eq/Ord class solving — inherits the infix answers
        // verbatim, and eval sees an ordinary lambda.
        infer ctx env (opValueLambda op expr.Span)
    | EBinOp(op, left, right) ->
        result {
            let! tleft = infer ctx env left

            // composition rejects a non-function LHS BEFORE the RHS is
            // inferred: bash-append lines (`cmd >> file`) usually carry
            // an unbound RHS, and the redirect hint must beat the
            // "unbound variable" error. A PIPE on the left is the
            // shared-precedence gotcha (`xs |> f >> g` is
            // `(xs |> f) >> g`, F#'s parse) and gets the parenthesize
            // hint instead [D:composition-operators]
            do!
                match op, resolve ctx tleft.Ty with
                | (">>" | "<<"), (TFun _ | TVar _) -> Ok()
                | (">>" | "<<"), ty ->
                    match left.Kind with
                    | EPipe _ ->
                        err
                            left.Span
                            ($"'{op}' and '|>' share precedence, so this parses as (xs |> f) {op} g; "
                             + $"parenthesize the composition: xs |> (f {op} g)")
                    | _ when op = ">>" ->
                        err
                            left.Span
                            ($"'>>' composes functions, and this expression has type {formatTy ty}; "
                             + "to append command output to a file, pipe it: cmd |> File.append \"out.txt\"")
                    | _ -> err left.Span $"'<<' composes functions, and this expression has type {formatTy ty}"
                | _ -> Ok()

            let! tright = infer ctx env right
            let! ty = typeBinOp ctx env expr.Span op tleft tright

            return
                { Kind = TEBinOp(op, tleft, tright)
                  Ty = ty
                  Span = expr.Span }
        }
    | EUpdate(source, updates) ->
        // copy-and-update [D:record-update]. Result type IS the
        // source's type — nominal stays nominal (update never adds
        // fields), a row source keeps ITS OWN row variable (identity,
        // not a fresh row), which is what lets a row-typed updater
        // generalize. Paths walk nested records (the F# 8 I.X sugar);
        // rows demand a field per hop, mirroring EField.
        result {
            let! tsource = infer ctx env source

            match firstDup (updates |> List.map (fun (path, _) -> fst (List.head path))) with
            | Some dup -> return! err expr.Span $"duplicate update of field '{dup}'"
            | None ->

                // subjectSpan = the expression whose type must be a record:
                // the SOURCE at the first hop, the previous field at deeper
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
                        | TNamed(typeName, targs) ->
                            match Map.tryFind typeName env.Types with
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
                            | None -> err subjectSpan $"unknown type '{typeName}'"
                        | TVar v ->
                            let r = freshName ctx "r"
                            ctx.Subst <- Map.add v (TRowVar(r, [])) ctx.Subst
                            updOne subjectSpan (TRowVar(r, [])) path value
                        | TRowVar(r, _) ->
                            let existing = Map.tryFind r ctx.Rows |> Option.defaultValue Map.empty

                            let fieldTy =
                                match Map.tryFind field existing with
                                | Some(t, _) -> t
                                | None ->
                                    let t = TVar(freshName ctx "a")
                                    ctx.Rows <- Map.add r (Map.add field (t, fieldSpan) existing) ctx.Rows
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

                return
                    { Kind = TEUpdate(tsource, List.rev tupdates)
                      Ty = tsource.Ty
                      Span = expr.Span }
        }
    | ERecord fields ->
        result {
            match firstDup (fields |> List.map (fun (n, _, _) -> n)) with
            | Some dup ->
                let _, dupSpan, _ = fields |> List.findBack (fun (n, _, _) -> n = dup)
                return! err dupSpan $"duplicate field '{dup}'"
            | None ->
                let names = fields |> List.map (fun (n, _, _) -> n) |> Set.ofList

                let candidates =
                    env.Types
                    |> Map.toList
                    |> List.choose (fun (_, def) ->
                        match def with
                        | Record r when Set.ofList (List.map fst r.Fields) = names -> Some r
                        | _ -> None)

                match candidates with
                | [ def ] ->
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

                    return
                        { Kind = TERecord(def.Name, List.rev tfields)
                          Ty = TNamed(def.Name, targs)
                          Span = expr.Span }
                | [] ->
                    let fieldList = String.concat ", " (Set.toList names)
                    return! err expr.Span $"no declared record has exactly the fields: {fieldList}"
                | many ->
                    let nameList = many |> List.map (fun r -> r.Name) |> String.concat ", "
                    return! err expr.Span $"ambiguous record literal; it matches: {nameList}"
        }
    | EFrom(fmt, shape, seqOf) ->
        result {
            // resolve the slot's payload to a NAME [D:anon-records]: a
            // declared name passes through; an anonymous shape resolves
            // to its canonical name, whose def withAnonDefs registered
            // at typecheck entry — one lookup path serves both, so the
            // anonymous form validates EXACTLY as a declared record does
            // Map<string, T> in the slot [D:map-string]: peel the wrap
            // first — the inner shape then resolves through the SAME
            // name path a bare slot does, so anon composition is free
            let inner, mapOf =
                match shape with
                | Some(FromMap sh) -> Some sh, true
                | sh -> sh, false

            let! tyName =
                match inner with
                | None -> Ok None
                | Some(FromName n) -> Ok(Some n)
                | Some(FromMap _) -> err expr.Span "Map< > does not nest in the adapter slot"
                | Some(FromAnon fields) ->
                    match firstDup (fields |> List.map fst) with
                    | Some d -> err expr.Span $"duplicate field '{d}' in the anonymous record"
                    | None -> Ok(Some(anonRecordName fields))

            // per-adapter admitted set for seq<Name> [D:from-json-seq]
            // [D:yaml-seq]: json and yaml admit the wrap (one document,
            // top level as declared); jsonl refuses — one object per line
            // is already plural in the way the wrap means
            do!
                if seqOf && fmt = "jsonl" then
                    let n = defaultArg tyName "T"
                    err expr.Span $"'from jsonl T' already yields seq<T> — write from jsonl {n}"
                elif mapOf && fmt = "jsonl" then
                    // one object per LINE vs ONE keyed object — the two
                    // shapes cannot both be the top level [D:map-string]
                    let n = defaultArg tyName "T"

                    err
                        expr.Span
                        $"'from jsonl' reads one object per line; a Map is ONE object — write from json Map<string, {n}>"
                elif mapOf && fmt = "yaml" then
                    let n = defaultArg tyName "T"
                    err expr.Span $"'from yaml' does not take Map<string, {n}> yet — 'from json' does"
                else
                    Ok()

            match fmt, tyName with
            // from json T reads ONE DOCUMENT -> T (pretty-printed bodies
            // pipe straight in); from json seq<T> reads one ARRAY
            // document -> seq<T> — the declared type decides what the top
            // level must be, never the input [D:from-json-seq]; from
            // jsonl T reads one document per element -> seq<T>
            // [D:from-jsonl]. The plain name carries the common case;
            // nothing sniffs.
            | ("json" | "jsonl"), Some name ->
                match Map.tryFind name env.Types with
                | Some(Record def) when def.Params.IsEmpty ->
                    do! jsonableRecord expr.Span env def

                    let defs =
                        def.Fields
                        |> List.fold (fun a (_, fty) -> jsonDefsClosure env a fty) (Map.ofList [ def.Name, def ])

                    let resultTy =
                        if mapOf then TNamed("Map", [ TStr; TNamed(name, []) ])
                        elif fmt = "json" && not seqOf then TNamed(name, [])
                        else TSeq(TNamed(name, []))

                    return
                        { Kind = TEFrom(fmt, def, defs, seqOf, mapOf)
                          Ty = TFun(TSeq TStr, resultTy)
                          Span = expr.Span }
                | Some(Record _) ->
                    return! err expr.Span $"'from {fmt}' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) -> return! err expr.Span $"'{name}' is a union; 'from {fmt}' needs a record"
                | None -> return! err expr.Span $"unknown type '{name}'{didYouMean name (Map.keys env.Types)}"
            | ("json" | "jsonl"), None ->
                let seqHint =
                    if fmt = "json" then
                        " — or seq<FileRow> for a top-level array, Map<string, FileRow> for an ID-keyed object, or an inline shape {| ip: string |}"
                    else
                        ""

                return! err expr.Span $"'from {fmt}' needs a record name, e.g. from {fmt} FileRow{seqHint}"
            | "yaml", Some name ->
                // from yaml T reads ONE DOCUMENT -> T (a mapping at the
                // top); from yaml seq<T> reads one SEQUENCE document ->
                // seq<T> [D:yaml-seq] — the declared type decides the top
                // level, never the input. Multi-document streams are not
                // supported: weir cannot type a heterogeneous stream, and
                // homogeneous ones are rare (the retirement's reason).
                match Map.tryFind name env.Types with
                | Some(Record def) when def.Params.IsEmpty ->
                    let declared = if seqOf then TSeq(TNamed(name, [])) else TNamed(name, [])

                    let! shape = yamlShape expr.Span env Set.empty declared

                    return
                        { Kind = TEFromYaml(name, shape)
                          Ty = TFun(TSeq TStr, declared)
                          Span = expr.Span }
                | Some(Record _) -> return! err expr.Span $"'from yaml' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) -> return! err expr.Span $"'{name}' is a union; 'from yaml' needs a record"
                | None -> return! err expr.Span $"unknown type '{name}'{didYouMean name (Map.keys env.Types)}"
            | "yaml", None ->
                return!
                    err
                        expr.Span
                        "'from yaml' needs a record name, e.g. from yaml Deployment — or seq<Deployment> for a top-level sequence"
            | fmt, _ -> return! err expr.Span $"unknown format '{fmt}'; available: json, jsonl, yaml"
        }
    | ETo _ -> err expr.Span "'to json' / 'to yaml' can only be used as a pipe stage, e.g. xs |> to json"
    | EYaml(tpl, schema) ->
        result {
            let! ttpl = checkYamlTpl ctx env tpl

            return
                { Kind = TEYaml(ttpl, schema)
                  Ty = TNamed("Yaml", [])
                  Span = expr.Span }
        }
    | ECmd(prog, args, envO) ->
        result {
            // $@ demands seq<string> EXACTLY [D:argv-splat]; the twin
            // teachings point each mistake at its honest spelling
            let checkArg (a: Expr) =
                match a.Kind with
                | ESplat inner ->
                    result {
                        let! tinner = infer ctx env inner

                        match resolve ctx tinner.Ty with
                        | TVar _ ->
                            do! bind ctx env a.Span (TSeq TStr) tinner.Ty

                            return
                                { Kind = TESplat tinner
                                  Ty = TSeq TStr
                                  Span = a.Span }
                        | TSeq TStr ->
                            return
                                { Kind = TESplat tinner
                                  Ty = TSeq TStr
                                  Span = a.Span }
                        | TSeq(TVar _ as tv) ->
                            // an unanchored element ([] and friends)
                            // resolves to string at the splice
                            do! bind ctx env a.Span TStr tv

                            return
                                { Kind = TESplat tinner
                                  Ty = TSeq TStr
                                  Span = a.Span }
                        | TSeq t ->
                            return!
                                err
                                    a.Span
                                    $"$@ splices a seq<string>; this is seq<{formatTy t}> — map show or interpolate per element"
                        | (TStr | TInt | TBool) as t ->
                            return! err a.Span $"$@ splices a seq<string>; this is {formatTy t} — one value? use $x"
                        | t -> return! err a.Span $"$@ splices a seq<string>; this is {formatTy t}"
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

            return
                { Kind = TECmd(prog, List.rev targs, tenvO)
                  Ty = TSeq TStr
                  Span = expr.Span }
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

            return
                { Kind = TEInterp(List.rev tparts)
                  Ty = TStr
                  Span = expr.Span }
        }
    | ETuple items ->
        result {
            let! titems =
                items
                |> List.fold
                    (fun acc it -> acc |> Result.bind (fun ts -> infer ctx env it |> Result.map (fun t -> t :: ts)))
                    (Ok [])

            let titems = List.rev titems

            return
                { Kind = TETuple titems
                  Ty = TTuple(titems |> List.map _.Ty)
                  Span = expr.Span }
        }
    | EList items ->
        result {
            match items with
            | [] ->
                return
                    { Kind = TEList []
                      Ty = TSeq(TVar(freshName ctx "a"))
                      Span = expr.Span }
            | head :: rest ->
                let! thead = infer ctx env head

                let! trest =
                    rest
                    |> List.fold
                        (fun acc item ->
                            acc
                            |> Result.bind (fun ts -> check ctx env item thead.Ty |> Result.map (fun t -> t :: ts)))
                        (Ok [])

                return
                    { Kind = TEList(thead :: List.rev trest)
                      Ty = TSeq thead.Ty
                      Span = expr.Span }
        }
    | EMatch(scrutinee, arms) ->
        result {
            let! tscrutinee = infer ctx env scrutinee

            // Bool patterns default an unresolved scrutinee to bool — the same
            // defaulting precedent as the operator and splice rules.
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
                | _ -> Ok()

            let scrutTy = resolve ctx tscrutinee.Ty

            let checkGuard bindings (guard: Expr option) =
                match guard with
                | None -> Ok None
                | Some g -> check ctx (bindParams env bindings) g TBool |> Result.map Some

            match arms with
            | [] -> return! err expr.Span "a match needs at least one arm"
            | (pat0, guard0, body0) :: rest ->
                let! bindings0 = checkPattern env scrutTy pat0
                let! tguard0 = checkGuard bindings0 guard0
                let! tbody0 = infer ctx (bindParams env bindings0) body0

                let checkArm (pat: Pattern, guard: Expr option, body: Expr) =
                    result {
                        let! bindings = checkPattern env scrutTy pat
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
                do! exhaustive env expr.Span scrutTy (tarms |> List.map (fun (p, g, _) -> p, g))

                return
                    { Kind = TEMatch(tscrutinee, tarms)
                      Ty = tbody0.Ty
                      Span = expr.Span }
        }
    | EIf(cond, thn, els) ->
        result {
            let! tcond = check ctx env cond TBool

            match els with
            | Some e ->
                let! tthn = infer ctx env thn
                let! tels = check ctx env e tthn.Ty

                return
                    { Kind = TEIf(tcond, tthn, Some tels)
                      Ty = tthn.Ty
                      Span = expr.Span }
            | None ->
                let rec tailOf (e: Expr) =
                    match e.Kind with
                    | ESeq(_, b) -> tailOf b
                    | _ -> e

                do!
                    if isExitCodeSpine (tailOf thn) then
                        err
                            (tailOf thn).Span
                            "this discards the exit code — bind it (let rc = <command> | exitCode), match on it, or drop '| exitCode'"
                    else
                        Ok()

                // a commandish then-TAIL arms before typing
                // [D:interior-arming] — `if force then git clean -fd`
                // is the effect form; the teaching text below is
                // untouched for everything else
                let! tthn = infer ctx env (armTail thn)

                match resolve ctx tthn.Ty with
                | TUnit ->
                    return
                        { Kind = TEIf(tcond, tthn, None)
                          Ty = TUnit
                          Span = expr.Span }
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

and private checkSpine
    (ctx: Ctx)
    (env: TypeEnv)
    (head: Expr)
    (args: Expr list)
    (piped: (Ty * Span) option)
    : Result<TypedExpr, TypeError> =
    result {
        let! thead = infer ctx env head
        let arity = args.Length + (if piped.IsSome then 1 else 0)

        // a HOLE-bound head (its let statement errored; the binding is
        // the cascade-suppression hole) applied: shape the hole into an
        // arrow of fresh vars so the application stays SILENT — the one
        // real error was already reported [PLAN-diagnostics-arc B6]
        do!
            match finalTy ctx thead.Ty with
            | TVar v when v.StartsWith "__hole" ->
                let arrow =
                    List.replicate arity ()
                    |> List.foldBack (fun () acc -> TFun(TVar(freshName ctx "__hole"), acc))
                    <| TVar(freshName ctx "__hole")

                bind ctx env head.Span (TVar v) arrow
            | _ -> Ok()

        match funParams ctx arity thead.Ty with
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
                    // point AT the first extra argument, not the head — and
                    // when it sits on a different PHYSICAL line, the extra
                    // args are an indented continuation the writer likely
                    // meant as a separate statement [D:over-apply-continuation]
                    let extra = List.tryItem available args

                    let onContinuation =
                        match toPhys.Value, extra with
                        | Some tr, Some ex -> fst (tr head.Span.Start.Col) <> fst (tr ex.Span.Start.Col)
                        | _ -> false

                    let hint =
                        if onContinuation then
                            "; these are an indented continuation of the line above — a separate statement must start at that line's indent, not deeper"
                        else
                            ""

                    let span = extra |> Option.map (fun e -> e.Span) |> Option.defaultValue head.Span

                    return! err span $"'{name}' takes at most {available} argument(s), but got {args.Length}{hint}"
                | _ ->
                    return!
                        err
                            head.Span
                            $"this expression is not a function taking {args.Length} argument(s); it has type {formatTy (finalTy ctx thead.Ty)}"
        | Some(paramTys, resultTy) ->
            // when a defaulted-to-string parameter causes the mismatch,
            // say so and name the anchor (PLAN-dx-review D6): the string
            // came from a defaulting decision in the callee's body, not
            // from anything visible at the call
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

                    { e with
                        Message =
                            e.Message
                            + $" — {name} takes string because a bare interpolation hole defaulted its parameter (the hole at {l}:{c}); a typed use in the hole fixes it (e.g. {{n + 0}}), or pass a string" }
                | _ -> e

            do!
                match piped with
                | Some(pipedTy, pipedSpan) ->
                    bind ctx env pipedSpan (List.last paramTys) pipedTy
                    |> Result.mapError (nameHoleDefault (List.last paramTys))
                | None -> Ok()

            let! typedArgs =
                List.zip args (List.truncate args.Length paramTys)
                |> List.fold
                    (fun acc (arg, paramTy) ->
                        acc
                        |> Result.bind (fun ts ->
                            check ctx env arg paramTy
                            |> Result.mapError (nameHoleDefault paramTy)
                            |> Result.map (fun t -> t :: ts)))
                    (Ok [])
                |> Result.map List.rev

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

                        { Kind = TEApp(acc, targ)
                          Ty = cod
                          Span = Span.union acc.Span targ.Span })
                    { thead with Ty = fullTy }

            return applied
    }

// a command CHAIN by AST shape — the parser's isCommandish, check-side
// [D:interior-arming]
and private isCmdChain (e: Expr) =
    match e.Kind with
    | ECmd _
    | EPipe(_, { Kind = ECmd _ }) -> true
    | _ -> false

// arm the TAIL of a statement body [D:interior-arming]: a commandish
// final expression under a unit demand is the EFFECT form — rewrite it
// to the same `|> print` the statement positions get, recursing through
// sequences. Pure AST pre-pass so teaching errors keep their text.
and armTail (e: Expr) : Expr =
    if isCmdChain e then
        { Kind = EPipe(e, { Kind = EVar "|print"; Span = e.Span })
          Span = e.Span }
    else
        match e.Kind with
        | ESeq(a, b) ->
            { Kind = ESeq(a, armTail b)
              Span = e.Span }
        // the tail rides through a scope and a let-in to their bodies
        // [D:within-scopes] — statement position arms a scope's final
        // command exactly as an if body's
        | EWithin(k, n, a, o, b) ->
            { Kind = EWithin(k, n, a, o, armTail b)
              Span = e.Span }
        | EAlways(b, c) ->
            { Kind = EAlways(armTail b, c)
              Span = e.Span }
        | ELet(n, ns, v, b) ->
            { Kind = ELet(n, ns, v, armTail b)
              Span = e.Span }
        | _ -> e

// an exit-code spine discarded where unit is demanded keeps its
// tailored teaching (was the district cell's text) [D:district-retirement]
and private isExitCodeSpine (e: Expr) =
    let rec head (x: Expr) =
        match x.Kind with
        | EApp(f, _) -> head f
        | _ -> x

    match (head e).Kind with
    | EVar n -> n.StartsWith "|exitCoded"
    | _ -> false

and private check (ctx: Ctx) (env: TypeEnv) (expr: Expr) (expected: Ty) : Result<TypedExpr, TypeError> =
    match expr.Kind, resolve ctx expected with
    // interior arming's check half [D:interior-arming]: a command chain
    // where UNIT is demanded (a checked lambda body, a sequence tail
    // under unit) is the effect form — `files |> Seq.iter (fun f ->
    // git add $f)` works without a district
    | (ECmd _ | EPipe(_, { Kind = ECmd _ })), TUnit when isCmdChain expr ->
        check
            ctx
            env
            { Kind =
                EPipe(
                    expr,
                    { Kind = EVar "|print"
                      Span = expr.Span }
                )
              Span = expr.Span }
            TUnit
    // the check direction rides INTO a scope's body [D:within-scopes] —
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
                    Error
                        { Message =
                            "exit inside always is refused — teardown of an unwinding scope must finish (the original error would become a code); fail \"msg\" carries a message instead"
                          Span = sp
                          Origin = None }
                | None -> Ok()

            let! tbody = check ctx env body expected
            let! tcleanup = check ctx env cleanup TUnit

            return
                { Kind = TEAlways(tbody, tcleanup)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | EWithin(kind, binder, arg, opts, body), _ ->
        result {
            let! targ =
                match kind, arg with
                | "cd", Some a -> check ctx env a TStr |> Result.map Some
                | "env", Some a -> check ctx env a (TSeq(TNamed("EnvVar", []))) |> Result.map Some
                | "lock", Some a -> check ctx env a TStr |> Result.map Some
                | _ -> Ok None

            let! topts =
                match opts with
                | Some o -> check ctx env o TDur |> Result.map Some
                | None -> Ok None

            let benv =
                match binder with
                | Some(n, _) -> bindParams env [ n, TStr ]
                | None -> env

            let! tbody = check ctx benv body expected

            return
                { Kind = TEWithin(kind, binder |> Option.map fst, targ, topts, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    // the check direction rides THROUGH a sequence to its final
    // expression [D:interior-arming] — F#'s rule, and what lets a
    // final command in a unit-demanded block arm
    | ESeq(first, rest), _ ->
        result {
            let! tfirst = infer ctx env first

            match resolve ctx tfirst.Ty with
            | TUnit ->
                let! trest = check ctx env rest expected

                return
                    { Kind = TESeq(tfirst, trest)
                      Ty = trest.Ty
                      Span = expr.Span }
            | ty -> return! err first.Span (seqUnitError first ty)
        }
    | EOpValue op, expected ->
        // the check-mode twin [D:operator-values]: the pushed domain
        // reaches the desugared lambda's params — `Seq.reduce (+)` pins
        // the operands from the seq's element type, exactly as the
        // spelled-out lambda would
        check ctx env (opValueLambda op expr.Span) expected
    | ELambdaPat(pat, body), TFun(dom, cod) ->
        // check-mode twin: the binder shape binds against the PUSHED
        // domain before the body runs, so piped element types reach
        // the components ahead of any hole defaulting
        result {
            let! shape, binds = binderShape ctx env pat
            do! bind ctx env pat.PSpan shape dom
            return! lambdaCore env expr.Span (fun tb -> TELambdaPat(pat, tb)) dom binds (fun e -> check ctx e body cod)
        }
    | ELambda(param, pspan, body), TFun(dom, cod) ->
        result {
            do! checkBinderName expr.Span param

            let typeBody e =
                match body.Kind, resolve ctx cod with
                // a NESTED lambda against a function cod pushes through
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
            do! checkBinderName expr.Span name
            let! tvalue = infer ctx env value
            let valueTy = finalTy ctx tvalue.Ty

            let scheme = generalizeLet ctx env valueTy

            let! tbody =
                check
                    ctx
                    { env with
                        Values = Map.add name scheme env.Values }
                    body
                    expected

            return
                { Kind = TELet(name, nameSpan, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | _ ->
        result {
            let! te = infer ctx env expr
            do! bind ctx env expr.Span expected te.Ty
            return te
        }

and private checkScalarSplice (ctx: Ctx) (env: TypeEnv) (site: SpliceSite) (arg: Expr) : Result<TypedExpr, TypeError> =
    result {
        let! targ = infer ctx env arg

        match resolve ctx targ.Ty with
        | TVar v ->
            // DEFER [D:splice-default-last]: the enclosing statement's
            // inference may still resolve v (the pipe-into-lambda
            // repro); default-or-reject happens at the boundary
            ctx.PendingSplices <- (v, arg.Span, site) :: ctx.PendingSplices
            return targ
        | ty ->
            do! spliceAdmit ctx env site arg.Span ty
            return targ
    }

// the yaml district's template typing [D:yaml-district]: splices carry
// the LIFTABLE law (string/int/float/bool, Yaml, Option of one, seq of
// those),
// key splices are strings, `for` binders bind like lambda params over
// the source's element type, literal duplicate keys are check errors.
and private yamlSpliceable (ctx: Ctx) (ty: Ty) : bool =
    match resolve ctx ty with
    | TInt
    | TFloat
    | TStr
    | TBool
    | TNamed("Yaml", []) -> true
    // an unresolved var (a template parameter) defers to the VALUE-driven
    // lift at eval — the sortBy posture: no type-class constraint exists,
    // so concrete violations check-error and polymorphic ones become a
    // located runtime failure naming the law
    | TVar _ -> true
    | TNamed("Option", [ TNamed("Option", _) ]) -> false
    | TNamed("Option", [ inner ]) -> yamlSpliceable ctx inner
    | TSeq elem -> yamlSpliceable ctx elem
    | _ -> false

and private checkYamlTpl (ctx: Ctx) (env: TypeEnv) (tpl: YamlTpl) : Result<TypedYamlTpl, TypeError> =
    match tpl with
    | YtScalar(raw, q, sp) -> Ok(TYtScalar(raw, q, sp))
    | YtBlock(text, sp) -> Ok(TYtBlock(text, sp))
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
            let! shape, binds = binderShape ctx env binder
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
    | YtForItems(binder, source, body) ->
        result {
            let! tsrc = infer ctx env source
            let elem = TVar(freshName ctx "a")
            do! bind ctx env source.Span (TSeq elem) tsrc.Ty
            let! shape, binds = binderShape ctx env binder
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
// (typecheckWith / typecheckBinder), BEFORE finalization walks:
// still-unresolved holes default to string (the original rule, moved),
// resolved-to-scalar holes pass, anything else gets the ORIGINAL
// rejection at the hole's span [D:splice-default-last]
let private resolvePendingSplices (ctx: Ctx) (env: TypeEnv) : Result<unit, TypeError> =
    ctx.PendingSplices
    |> List.rev
    |> List.fold
        (fun acc (v, span, site) ->
            acc
            |> Result.bind (fun () ->
                match resolve ctx (TVar v) with
                | TVar _ ->
                    // the string DEFAULT fires — record the hole's
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

let rec private finalizeExpr (ctx: Ctx) (te: TypedExpr) : TypedExpr =
    let kind =
        match te.Kind with
        | TELet(n, ns, v, b) -> TELet(n, ns, finalizeExpr ctx v, finalizeExpr ctx b)
        | TELambda(p, ps, b) -> TELambda(p, ps, finalizeExpr ctx b)
        | TEApp(f, a) -> TEApp(finalizeExpr ctx f, finalizeExpr ctx a)
        | TEPipe(a, f) -> TEPipe(finalizeExpr ctx a, finalizeExpr ctx f)
        | TEField(t, f) -> TEField(finalizeExpr ctx t, f)
        | TEBinOp(op, l, r) -> TEBinOp(op, finalizeExpr ctx l, finalizeExpr ctx r)
        | TEUpdate(src, ups) -> TEUpdate(finalizeExpr ctx src, ups |> List.map (fun (p, v) -> p, finalizeExpr ctx v))
        | TERecord(n, fields) -> TERecord(n, fields |> List.map (fun (f, v) -> f, finalizeExpr ctx v))
        | TEList items -> TEList(items |> List.map (finalizeExpr ctx))
        | TETuple items -> TETuple(items |> List.map (finalizeExpr ctx))
        | TEYaml(tpl, schema) -> TEYaml(finalizeYamlTpl ctx tpl, schema)
        | TELetPat(p, v, b) -> TELetPat(p, finalizeExpr ctx v, finalizeExpr ctx b)
        | TELambdaPat(p, b) -> TELambdaPat(p, finalizeExpr ctx b)
        | TECmd(prog, args, envO) ->
            TECmd(prog, args |> List.map (finalizeExpr ctx), envO |> Option.map (finalizeExpr ctx))
        | TESplat e -> TESplat(finalizeExpr ctx e)
        | TEInterp parts ->
            TEInterp(
                parts
                |> List.map (function
                    | IStr s -> IStr s
                    | IExpr e -> IExpr(finalizeExpr ctx e))
            )
        | TEMatch(s, arms) ->
            TEMatch(
                s |> finalizeExpr ctx,
                arms
                |> List.map (fun (p, g, b) -> p, g |> Option.map (finalizeExpr ctx), finalizeExpr ctx b)
            )
        | TEIf(c, t, e) -> TEIf(finalizeExpr ctx c, finalizeExpr ctx t, e |> Option.map (finalizeExpr ctx))
        | TEDur _ -> te.Kind
        | TEFloat _ -> te.Kind
        | TESize _ -> te.Kind
        | TERetry(ip, o, w, b, u) ->
            TERetry(
                ip,
                finalizeExpr ctx o,
                w |> Option.map (finalizeExpr ctx),
                finalizeExpr ctx b,
                u |> Option.map (fun (n, p) -> n, finalizeExpr ctx p)
            )
        | TESeq(a, b) -> TESeq(finalizeExpr ctx a, finalizeExpr ctx b)
        | TEWithin(k, n, a, o, b) ->
            TEWithin(k, n, a |> Option.map (finalizeExpr ctx), o |> Option.map (finalizeExpr ctx), finalizeExpr ctx b)
        | TEAlways(b, c) -> TEAlways(finalizeExpr ctx b, finalizeExpr ctx c)
        | TEEnvLoad _
        | TEArgsLoad _ -> te.Kind
        | leaf -> leaf

    { te with
        Kind = kind
        Ty = finalTy ctx te.Ty }

// typecheckWith: the statement-boundary rule for class constraints.
// Residue = pendings still riding UNRESOLVED vars that appear in the
// statement's final type — the caller's generalization scoops them
// (generalizeWith). A pending on a var OUTSIDE the final type is
// AMBIGUOUS (no defaulting, no ambiguity resolution): error asking
// for context, the reject-don't-guess posture one step later than the
// old at-the-operator rule.
// The statement-level destructuring binder: check the RHS, bind the
// binder shape against it, generalize per name. Residual/ambiguous
// constraints follow typecheckWith's boundary rule.

and private finalizeYamlTpl (ctx: Ctx) (tpl: TypedYamlTpl) : TypedYamlTpl =
    match tpl with
    | TYtScalar _ -> tpl
    | TYtBlock _ -> tpl
    | TYtSplice e -> TYtSplice(finalizeExpr ctx e)
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
    | TYtForItems(b, src, body) -> TYtForItems(b, finalizeExpr ctx src, body |> List.map (finalizeYamlItem ctx))

/// anonymous shapes in the adapter slot register their canonical defs
/// [D:anon-records] — collected up front so field access on the result
/// type resolves anywhere in the statement; Script.checkStatement uses
/// the same extension to persist them into the outgoing env
let anonDefs (expr: Expr) : (string * TypeDef) list =
    let acc = System.Collections.Generic.Dictionary<string, TypeDef>()

    let rec walk (e: Expr) =
        (match e.Kind with
         | EFrom(_, Some(FromAnon fields), _)
         | EFrom(_, Some(FromMap(FromAnon fields)), _) ->
             let name = anonRecordName fields

             if not (acc.ContainsKey name) then
                 acc[name] <-
                     Record
                         { Name = name
                           Params = []
                           Fields = List.sortBy fst fields
                           Attrs = Map.empty
                           Docs = Map.empty }
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
        Record
            { Name = name
              Params = []
              Fields = List.sortBy fst fields
              Attrs = Map.empty
              Docs = Map.empty })

let private withDefList (env: TypeEnv) (defs: (string * TypeDef) list) : TypeEnv =
    match defs with
    | [] -> env
    | defs ->
        { env with
            Types = defs |> List.fold (fun ts (n, d) -> Map.add n d ts) env.Types }

let withAnonDefs (env: TypeEnv) (expr: Expr) : TypeEnv =
    withDefList env (anonDefs expr @ pendingAnonRecords ())

// budget exhaustion surfaces as an ordinary located TypeError; the
// text names what the author can act on, never the internal counter
let private budgetError (span: Span) (sized: bool) : TypeError =
    { Span = span
      Message =
        (if sized then
             "this expression's type grew too large to infer — split the expression into smaller bindings, or annotate the intended type"
         else
             "type inference ran out of budget on this expression — split it into smaller bindings")
      Origin = None }

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
    let env = withAnonDefs env expr
    let ctx = newCtx ()

    match binderShape ctx env pat with
    | Error e -> Error e
    | Ok(shape, binds) ->
        match infer ctx env expr with
        | Error e -> Error e
        | Ok te ->
            match bind ctx env pat.PSpan shape te.Ty with
            | Error e -> Error e
            | Ok() ->

                match resolvePendingSplices ctx env with
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
                    | Some p ->
                        err
                            p.Span
                            "this leaves an equality requirement on a type nothing determines — pipe in data or use a concrete value"
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
    let env = withAnonDefs env expr
    let ctx = newCtx ()

    match infer ctx env expr with
    | Error e -> Error e
    | Ok te ->
        match resolvePendingSplices ctx env with
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
            | Some(_, p) ->
                err
                    p.Span
                    "this leaves an equality requirement on a type nothing determines — pipe in data or use a concrete value"
            | None ->
                let residue =
                    openCons
                    |> List.groupBy fst
                    |> List.map (fun (u, ps) -> u, ps |> List.map (fun (_, p) -> p.Cls) |> Set.ofList)
                    |> Map.ofList

                // origins ride out with the residue [D:row-provenance]:
                // the SLet scheme is built OUTSIDE this ctx
                Ok(te, residue, rowOriginsFor ctx resultVars, List.rev ctx.HoleDefaulted)

// every TypedExpr embedded in a typed yaml template — splices, key
// splices, and for sources
let rec yamlTplTypedExprs (tpl: TypedYamlTpl) : TypedExpr list =
    match tpl with
    | TYtScalar _ -> []
    | TYtBlock _ -> []
    | TYtSplice e -> [ e ]
    | TYtSeq(items, _) ->
        items
        |> List.collect (function
            | TYtItem t -> yamlTplTypedExprs t
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
    | TEVar _
    | TEEnvLoad _
    | TEArgsLoad _
    | TEFrom _
    | TEFromYaml _
    | TETo _ -> []
    | TELet(_, _, v, b) -> [ v; b ]
    | TELetPat(_, v, b) -> [ v; b ]
    | TELambda(_, _, b) -> [ b ]
    | TELambdaPat(_, b) -> [ b ]
    | TEApp(f, a) -> [ f; a ]
    | TEPipe(a, f) -> [ a; f ]
    | TEField(t, _) -> [ t ]
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
    | TEList items -> items
    | TETuple items -> items
    | TECmd(_, args, envO) -> args @ Option.toList envO
    | TESplat e -> [ e ]
    | TEUpdate(src, ups) -> src :: (ups |> List.map snd)
    | TEInterp parts ->
        parts
        |> List.choose (function
            | IExpr e -> Some e
            | IStr _ -> None)
    | TEYaml(tpl, _) -> yamlTplTypedExprs tpl

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
    | TSize
    | TBytes
    | TSecret -> Ok()
    | TSeq t -> validateTy env selfName selfArity allowed span t
    | TTuple ts -> allOk ts (validateTy env selfName selfArity allowed span)
    // Map is STRUCTURAL, string-keyed only [D:map-string] — the key
    // slot refuses anything else with the narrowing's reason
    | TNamed("Map", [ TStr; v ]) -> validateTy env selfName selfArity allowed span v
    | TNamed("Map", [ k; _ ]) ->
        err
            span
            $"Map keys are strings only (every receipt has them — JSON object keys ARE strings); got Map<{formatTy k}, …>"
    | TNamed("Map", args) -> err span $"Map takes two type arguments (Map<string, T>); got {List.length args}"
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
            else
                match Map.tryFind n env.Types with
                | Some(Record d) -> Some d.Params.Length
                | Some(Union d) -> Some d.Params.Length
                | None -> None

        match arity with
        | None -> err span $"unknown type '{n}'{didYouMean n (Map.keys env.Types)}"
        | Some a when a <> targs.Length -> err span $"'{n}' expects {a} type argument(s), got {targs.Length}"
        | Some _ -> allOk targs (validateTy env selfName selfArity allowed span)

// registered attribute names [D:attributes]: unknown names are check
// errors; registered-but-unconsumed is legal-and-inert. Validation
// happens at attachment; consumers bind at consumption.
let private attrRegistry: Map<string, AttrArg option -> string option> =
    Map.ofList
        [ "Short",
          (function
          | Some(AStr "h") -> Some "argument 'h' is reserved for --help"
          | Some(AStr s) when s.Length = 1 -> None
          | _ -> Some "expects a one-character string, e.g. [<Short \"c\">]")
          "NoShort",
          (function
          | None -> None
          | Some _ -> Some "takes no argument")
          // [<Positional>] returns for SIGNATURES [D:command-signatures]:
          // foreign CLIs have operands and a signature describes theirs
          // (its drop was about weir's own CLIs); inert everywhere else,
          // the attribute law
          "Positional",
          (function
          | None -> None
          | Some _ -> Some "takes no argument")
          // [<Doc>] RETIRED [D:doc-help] — a `///` above the field is the one
          // source; a stale `[<Doc "x">]` is now the ordinary unknown-attribute
          // error (the did-you-mean over the remaining names).
          "Default",
          (function
          | Some(AStr _ | AInt _ | ABool _ | ADur _ | AFloat _ | ASize _) -> None
          | None -> Some "expects a literal (string, int, float, bool, duration, or size), e.g. [<Default 10>]")
          // the wire key [D:wire-keys]: reserved words and illegal
          // identifiers are ordinary JSON/YAML keys — the field keeps a
          // weir name, the attribute names the wire
          "Wire",
          (function
          | Some(AStr s) when s <> "" -> None
          | _ -> Some "expects the wire key as a string, e.g. [<Wire \"type\">] kind: string") ]

// two fields resolving to ONE wire key is nonsense on every adapter —
// refused at the declaration, not discovered at the boundary
// [D:wire-keys]
let private validateWireCollisions (recName: string) (fields: (string * Ty * AttrSpec list) list) =
    // a collision always involves at least one [<Wire>] (field names
    // are unique), so ONE side carries a span — report there
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

let private validateFieldAttrs (recName: string) (field: string, _: Ty, specs: AttrSpec list) =
    let conflicts a b (seen: Set<string>) (spec: AttrSpec) = spec.AName = a && Set.contains b seen

    let rec go seen specs =
        match specs with
        | [] -> Ok()
        | (a: AttrSpec) :: rest ->
            if Set.contains a.AName seen then
                err a.ASpan $"duplicate attribute '{a.AName}' on field '{field}'"
            elif conflicts "Short" "NoShort" seen a || conflicts "NoShort" "Short" seen a then
                err a.ASpan $"field '{field}' has both Short and NoShort"
            else
                match Map.tryFind a.AName attrRegistry with
                | None ->
                    let hint = didYouMean a.AName (Map.keys attrRegistry)
                    err a.ASpan $"unknown attribute '{a.AName}'{hint}"
                | Some validate ->
                    match validate a.AArg with
                    | Some msg -> err a.ASpan $"'{a.AName}' {msg}"
                    | None -> go (Set.add a.AName seen) rest

    go Set.empty specs

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

// the built-in type names, registered once by Prelude.extend
// [D:desugar-capture]: a user redeclaring one silently RETYPED every
// builtin referencing it (type Retry = { x: int } re-broke the retry
// sugar through the TYPE after the value was made un-shadowable)
// concurrent: tests build envs in PARALLEL (the oracle found the
// plain HashSet corrupting under simultaneous extends)
let builtinTypeNames: System.Collections.Concurrent.ConcurrentDictionary<string, byte> =
    System.Collections.Concurrent.ConcurrentDictionary<string, byte>()

// the prelude REPLAYS its own declarations on every extend (tests build
// envs repeatedly) — exempt it; the ThreadLocal keeps parallel test
// runs isolated (the toPhys pattern)
let preludeLoading: System.Threading.ThreadLocal<bool> =
    new System.Threading.ThreadLocal<bool>(fun () -> false)

let checkDecl (env: TypeEnv) (decl: Decl) : Result<TypeEnv, TypeError> =
    // a declared record's anon FIELD shapes drain here [D:anon-nesting]
    // — registered before validation so the names resolve, and they
    // ride the returned env like any registration
    let env = withDefList env (pendingAnonRecords ())

    if not preludeLoading.Value && builtinTypeNames.ContainsKey decl.Name then
        err decl.Span $"'{decl.Name}' is a built-in type — pick another name"
    else
        let allowed = Set.ofList decl.Params
        let selfArity = decl.Params.Length
        let selfTy = TNamed(decl.Name, decl.Params |> List.map TVar)

        match firstDup decl.Params with
        | Some dup -> err decl.Span $"duplicate type parameter '{dup}"
        | None ->
            match decl.Body with
            | DRecord fields ->
                result {
                    match firstDup (fields |> List.map (fun (n, _, _) -> n)) with
                    | Some dup -> return! err decl.Span $"duplicate field '{dup}'"
                    | None ->
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

                        let def =
                            Record
                                { Name = decl.Name
                                  Params = decl.Params
                                  Fields = plain
                                  Attrs = attrs
                                  // the runner enriches this from the `///` docs
                                  // after checkDecl [D:doc-help]
                                  Docs = Map.empty }

                        return
                            { env with
                                Types = Map.add decl.Name def env.Types }
                }
            | DUnion cases ->
                result {
                    match firstDup (List.map fst cases) with
                    | Some dup -> return! err decl.Span $"duplicate case '{dup}'"
                    | None ->
                        do!
                            allOk cases (fun (_, payload) ->
                                match payload with
                                | Some ty -> validateTy env decl.Name selfArity allowed decl.Span ty
                                | None -> Ok())

                        let def =
                            Union
                                { Name = decl.Name
                                  Params = decl.Params
                                  Cases = cases }

                        let ctorTy payload =
                            match payload with
                            | None -> selfTy
                            | Some ty -> TFun(ty, selfTy)

                        let ctorScheme payload =
                            { Forall = allowed + tyVars (ctorTy payload)
                              Cs = Map.empty
                              Ty = ctorTy payload
                              RowOrigins = Map.empty
                              HoleDefaults = [] }

                        let values =
                            cases
                            |> List.fold (fun vs (c, payload) -> Map.add c (ctorScheme payload) vs) env.Values

                        return
                            { env with
                                Types = Map.add decl.Name def env.Types
                                Values = values }
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
                     acc.Add
                         { Span = a.Span
                           Message =
                             "';' does not chain commands in weir — put commands on separate lines "
                             + "(sequence unit expressions with ';' in expression position; "
                             + "if you meant a literal ';' argument, ignore this)" }
                 | TEStr ">" ->
                     acc.Add
                         { Span = a.Span
                           Message =
                             "'>' does not redirect in weir — pipe to File.write: "
                             + "cmd |> File.write \"out.txt\" "
                             + "(if you meant a literal '>' argument, ignore this)" }
                 | TEStr ">>" ->
                     acc.Add
                         { Span = a.Span
                           Message =
                             "'>>' does not redirect in weir — pipe to File.append: "
                             + "cmd |> File.append \"out.txt\" "
                             + "(if you meant a literal '>>' argument, ignore this)" }
                 // the two commonest bash chaining glyphs were the
                 // family's missing members (PLAN-dx-review D2): the
                 // pass-through is deliberate, the SILENCE was the defect
                 | TEStr "&&" ->
                     acc.Add
                         { Span = a.Span
                           Message =
                             "'&&' does not chain commands in weir — put commands on separate lines "
                             + "(a later line runs only if the earlier one succeeds: '| orFail' raises on failure; "
                             + "if you meant a literal '&&' argument, ignore this)" }
                 | TEStr "||" ->
                     acc.Add
                         { Span = a.Span
                           Message =
                             "'||' does not chain commands in weir — branch on the exit instead: "
                             + "if cmd | succeeds then ... else ... "
                             + "(if you meant a literal '||' argument, ignore this)" }
                 | _ -> ()
         | _ -> ())

        childExprs te |> List.iter walk

    walk te
    List.ofSeq acc
