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
    | "Seq", "collect" ->
        Some "F#'s Seq.collect is flatMap — the name stays reserved for it; the materializer is Seq.force"
    | "Option", "defaultTo" -> Some "renamed 'Option.defaultValue' (F# parity); a lazy default is 'Option.defaultWith'"
    | _ -> None

let private retiredBare (name: string) : string option =
    match name with
    | "toList" -> Some "weir has no list type; 'force' is the materializer"
    | "collect" -> Some "'force' materializes (F#'s collect is flatMap, reserved)"
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
    | TESeq of first: TypedExpr * rest: TypedExpr
    | TEWithin of kind: string * binder: string option * arg: TypedExpr option * body: TypedExpr
    | TEEnvLoad of def: RecordDef * enums: Map<string, string list>
    | TEArgsLoad of target: ArgsTarget
    | TEFrom of format: string * rowDef: RecordDef
    // from yaml T [D:yaml-v1]: eval has no env.Types, so the checker packs
    // the RESOLVED target tree (the [D:env-enums] precedent)
    | TEFromYaml of tyName: string * shape: Yaml.Shape
    | TETo of format: string
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
    err span $"expected {formatTy expected}, got {formatTy actual}"

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

// A pending class constraint on an unresolved type/row var: the
// demanding site's span and message formatter travel with it, so a
// later discharge failure reads at the place that demanded it.
type private Pending =
    { Cls: Cls
      Span: Span
      Describe: Ty -> string }

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
      mutable PendingSplices: (string * Span * string) list }

let private newCtx () =
    { Fresh = 0
      Subst = Map.empty
      Rows = Map.empty
      RowOrigins = Map.empty
      Cons = Map.empty
      PendingSplices = [] }

let private freshName (ctx: Ctx) (prefix: string) : string =
    ctx.Fresh <- ctx.Fresh + 1
    $"{prefix}{ctx.Fresh}"

let rec private resolve (ctx: Ctx) (ty: Ty) : Ty =
    match ty with
    | TVar v ->
        match Map.tryFind v ctx.Subst with
        | Some t -> resolve ctx t
        | None -> ty
    | TRowVar(r, _) ->
        match Map.tryFind r ctx.Subst with
        | Some t -> resolve ctx t
        | None -> ty
    | t -> t

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
                                    $"this use requires equatable values, got {formatTy t} — sequences and functions cannot be compared with '=='"
                            | Cls.Show -> fun t -> $"show cannot render functions; this is {formatTy t}"
                            | Cls.Ord ->
                                fun t ->
                                    $"cannot sort by this key: {formatTy t} cannot be ordered — keys are int, string, or bool" })

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
            | Cls.Eq, (TInt | TStr | TBool | TUnit) -> true
            | Cls.Eq, (TFun _ | TSeq _) -> false
            | Cls.Eq, TTuple ts -> ts |> List.forall (ok seen)
            | Cls.Eq, TNamed(n, targs) -> decompose n targs
            // Show: no function anywhere; seqs render fine
            | Cls.Show, (TInt | TStr | TBool | TUnit) -> true
            | Cls.Show, TFun _ -> false
            | Cls.Show, TSeq elem -> ok seen elem
            | Cls.Show, TTuple ts -> ts |> List.forall (ok seen)
            | Cls.Show, TNamed(n, targs) -> decompose n targs
            // Ord: int | string | bool EXACTLY — no decomposition, no
            // record/union ordering (no receipts; the message names it)
            | Cls.Ord, (TInt | TStr | TBool) -> true
            | Cls.Ord, _ -> false
            // vars and row vars are consumed by the outer match arms;
            // the compiler cannot see that through this nesting
            | _, (TVar _ | TRowVar _) -> true

    if ok Set.empty ty0 then
        Ok()
    else
        err p.Span (p.Describe(finalTy ctx ty0))

// vars discharge their pendings the moment they resolve — no trial
// resolution exists anywhere in the checker, so a discharge is final
let private dischargeCons (ctx: Ctx) (env: TypeEnv) (v: string) (t: Ty) : Result<unit, TypeError> =
    match Map.tryFind v ctx.Cons with
    | None -> Ok()
    | Some ps ->
        ctx.Cons <- Map.remove v ctx.Cons
        allOk ps (fun p -> demand ctx env p t)

let rec private bind (ctx: Ctx) (env: TypeEnv) (span: Span) (expected: Ty) (actual: Ty) : Result<unit, TypeError> =
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
      RowOrigins = Map.empty }

let private isPrintFamily (env: TypeEnv) (name: string) =
    (name = "print" || name = "printerr")
    && Map.tryFind name env.Values = Some printScheme

// show : Show a => a -> string — the debugging renderer (REPL-shaped,
// lossy) [D:inferred-type-classes]: an ordinary constrained scheme
// on the normal instantiate/apply path; showable = no function
// anywhere, recursively (seqs render fine — Show is wider than Eq).
let showScheme: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Show ] ]
      Ty = TFun(TVar "a", TStr)
      RowOrigins = Map.empty }

// Seq.contains — an ordinary constrained scheme
// `Eq a => a -> seq<a> -> bool` [D:inferred-type-classes], served by
// the normal instantiate/apply path.
let containsScheme: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Eq ] ]
      Ty = TFun(TVar "a", TFun(TSeq(TVar "a"), TBool))
      RowOrigins = Map.empty }

// Seq.distinct : Eq a => seq<a> -> seq<a> [D:seq-distinct]
let distinctScheme: Scheme =
    { Forall = Set.singleton "a"
      Cs = Map [ "a", Set [ Cls.Eq ] ]
      Ty = TFun(TSeq(TVar "a"), TSeq(TVar "a"))
      RowOrigins = Map.empty }


let private printArgTy (ctx: Ctx) (env: TypeEnv) (span: Span) (ty: Ty) : Result<Ty, TypeError> =
    match resolve ctx ty with
    | TVar _ as v -> bind ctx env span TStr v |> Result.map (fun () -> TStr)
    | (TStr | TInt | TBool) as t -> Ok t
    // unit is printable as NOTHING [D:exit-reifiers]: the !()/district
    // desugar wraps interiors in print, and `| orFail` interiors are
    // unit — one rule instead of a shadow drain builtin
    | TUnit -> Ok TUnit
    | TSeq inner ->
        (match resolve ctx inner with
         | TVar _ as v -> bind ctx env span TStr v |> Result.map (fun () -> TSeq TStr)
         | TStr -> Ok(TSeq TStr)
         | TUnit -> err span "print cannot take seq<unit> — a lazy effect sequence never runs; use Seq.iter"
         | t -> err span $"print takes a string, int, bool, or seq<string>; this is {formatTy (TSeq t)}")
    | t -> err span $"print takes a string, int, bool, or seq<string>; this is {formatTy t}"

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
    | _, TVar _, ((TInt | TStr | TBool) as t) -> retryAfter (bind ctx env l.Span t l.Ty)
    | _, ((TInt | TStr | TBool) as t), TVar _ -> retryAfter (bind ctx env r.Span t r.Ty)
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
                    fun t -> $"'{op}' is not defined for {formatTy t}; sequences and functions cannot be compared" }

            demand ctx env p a |> Result.map (fun () -> TBool))
    | _, TVar _, TVar _ -> err opSpan $"cannot infer the operand types of '{op}'; pipe data in or use concrete values"
    | _, TRowVar _, _
    | _, _, TRowVar _ -> err opSpan $"operator '{op}' is not defined for records"
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
    | TStr
    | TBool -> true
    | _ -> false

let private jsonFieldOk (ty: Ty) : bool =
    jsonScalar ty
    || (match ty with
        | TNamed("Option", [ inner ]) -> jsonScalar inner
        | _ -> false)

let private jsonableRecord (span: Span) (def: RecordDef) : Result<unit, TypeError> =
    allOk def.Fields (fun (name, ty) ->
        if jsonFieldOk ty then
            Ok()
        else
            err span $"field '{name}' has type {formatTy ty}; json rows support int, string, bool, and Option of those")

let private jsonableElem (span: Span) (env: TypeEnv) (elem: Ty) : Result<unit, TypeError> =
    if jsonFieldOk elem then
        Ok()
    else
        match elem with
        | TNamed(n, []) ->
            match Map.tryFind n env.Types with
            | Some(Record def) -> jsonableRecord span def
            | _ -> err span $"'to json' needs primitive or record elements, got {formatTy elem}"
        | _ -> err span $"'to json' needs primitive or record elements, got {formatTy elem}"

// ---- the yaml TREE law [D:yaml-v1] — richer than json's flat-row law
// because YAML is a DOCUMENT format, not a row stream: scalars, nested
// monomorphic records, seq<elem>, seq<string * elem> (an open mapping),
// Option anywhere (None omits / null reads None). `seen` guards
// declaration cycles.

// the from-side: build the shape eval will convert through
let rec private yamlShape (span: Span) (env: TypeEnv) (seen: Set<string>) (ty: Ty) : Result<Yaml.Shape, TypeError> =
    match ty with
    | TInt -> Ok Yaml.SInt
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
                            yamlShape span env (seen.Add n) fty |> Result.map (fun s -> (fname, s) :: fs)))
                    (Ok [])
                |> Result.map (fun fs -> Yaml.SRec(n, List.rev fs))
            | Some(Record _) -> err span $"'{n}' is generic; the yaml boundary needs monomorphic records"
            | Some(Union _) -> err span $"'{n}' is a union; the yaml tree law takes records, seqs, scalars, and Option"
            | None -> err span $"unknown type '{n}'{didYouMean n (Map.keys env.Types)}"
    | ty ->
        err span $"type {formatTy ty} cannot cross the yaml boundary (scalars, records, seqs, seq<string * _>, Option)"

// the to-side: the same law, plus `Yaml` NODES render directly
let rec private yamlableOut (span: Span) (env: TypeEnv) (seen: Set<string>) (ty: Ty) : Result<unit, TypeError> =
    match ty with
    | TInt
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
        | ty -> err p.PSpan $"bool patterns need a bool scrutinee; this one has type {formatTy ty}"
    | PInt _ ->
        match ty with
        | TInt -> Ok []
        | ty -> err p.PSpan $"int literal patterns need an int scrutinee; this one has type {formatTy ty}"
    | PStr _ ->
        match ty with
        | TStr -> Ok []
        | ty -> err p.PSpan $"string literal patterns need a string scrutinee; this one has type {formatTy ty}"
    | PSeqNil ->
        (match ty with
         | TSeq _ -> Ok []
         | ty -> err p.PSpan $"seq patterns need a seq scrutinee; this one has type {formatTy ty}")
    | PCons(h, t) ->
        (match ty with
         | TSeq elem ->
             result {
                 let! hb = checkPattern env elem h
                 let! tb = checkPattern env ty t
                 return hb @ tb
             }
         | ty -> err p.PSpan $"seq patterns need a seq scrutinee; this one has type {formatTy ty}")
    | PSeqList ps ->
        (match ty with
         | TSeq elem ->
             ps
             |> List.fold
                 (fun acc sub ->
                     acc
                     |> Result.bind (fun bs -> checkPattern env elem sub |> Result.map (fun b -> bs @ b)))
                 (Ok [])
         | ty -> err p.PSpan $"seq patterns need a seq scrutinee; this one has type {formatTy ty}")
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
        | ty -> err p.PSpan $"Regex patterns need a string scrutinee; this one has type {formatTy ty}"
    | PUnit ->
        match ty with
        | TUnit -> Ok []
        | ty -> err p.PSpan $"'()' patterns need a unit scrutinee; this one has type {formatTy ty}"
    | PTuple ps ->
        match ty with
        | TTuple ts when List.length ps = List.length ts ->
            List.zip ps ts
            |> List.fold
                (fun acc (subP, subTy) ->
                    acc
                    |> Result.bind (fun bs -> checkPattern env subTy subP |> Result.map (fun b -> bs @ b)))
                (Ok [])
        | TTuple ts ->
            err p.PSpan $"this tuple pattern has {List.length ps} elements; the scrutinee has {List.length ts}"
        | ty -> err p.PSpan $"tuple patterns need a tuple scrutinee; this one has type {formatTy ty}"
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
      RowOrigins = origins }

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
    | EWithin(kind, binder, arg, body) ->
        // tmp binds its path (a plain string, platform-native); cd
        // consumes a string path; env consumes seq<EnvVar> — the arg
        // types are the kinds' contracts [D:within-scopes]; the
        // scope's type IS the body's type
        result {
            let! targ =
                match kind, arg with
                | "cd", Some a -> check ctx env a TStr |> Result.map Some
                | "env", Some a -> check ctx env a (TSeq(TNamed("EnvVar", []))) |> Result.map Some
                | _ -> Ok None

            let benv =
                match binder with
                | Some(n, _) -> bindParams env [ n, TStr ]
                | None -> env

            let! tbody = infer ctx benv body

            return
                { Kind = TEWithin(kind, binder |> Option.map fst, targ, tbody)
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
            | ty ->
                return!
                    err
                        first.Span
                        $"a sequenced expression must be unit; this one is {formatTy ty} — bind it or print it"
        }
    | EVar(("print" | "printerr") as pname) when isPrintFamily env pname ->
        // Bare-value position (e.g. Seq.iter print): the defaulted form.
        Ok
            { Kind = TEVar pname
              Ty = TFun(TStr, TUnit)
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
                | [ one ] -> err expr.Span $"'{name}' moved into a module; use '{one}'"
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
                    | None ->
                        let hint = didYouMean name (Map.keys env.Values |> Seq.filter Types.isUserName)

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
                                    acc |> Result.bind (fun ts -> checkField f |> Result.map (fun t -> t :: ts)))
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
            // Env.load T — the third typed-boundary instance (porcelain,
            // from json, env). Imitates from-json's type-name-in-special-
            // position resolution, relocated to expression position.
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
                         | TBool
                         | TNamed("Option", [ TStr | TInt | TBool ]) -> true
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
                                 $"Env.load fields must be string, int, bool, an enum union (0-arity cases), or Option of these; '{bad}' is {formatTy badTy}"
                         | None ->

                             match caseCollision with
                             | Some(a, b, word) -> err arg.Span $"cases '{a}' and '{b}' collide as env value '{word}'"
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
                                                 unionOf ft |> Option.map (fun (n, u) -> n, u.Cases |> List.map fst))
                                             |> Map.ofList

                                         Ok
                                             { Kind = TEEnvLoad(def, enums)
                                               Ty = TNamed(tyName, [])
                                               Span = expr.Span }
                 | Some(Record def) -> err arg.Span $"Env.load needs a monomorphic record; '{tyName}' is generic"
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
                             err span $"case '{c}' must carry a single record payload; spell it as a record type"

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
                             err arg.Span $"'{a}' and '{b}' are both union-typed: one subcommand slot per record"
                     | Some(Union udef) when udef.Params.IsEmpty ->
                         result {
                             let! payloads = unionPayloads arg.Span udef

                             return
                                 { Kind = TEArgsLoad(ArgsUnion(udef, payloads))
                                   Ty = TNamed(tyName, [])
                                   Span = expr.Span }
                         }
                     | Some(Record _)
                     | Some(Union _) -> err arg.Span $"Args.load needs a monomorphic type; '{tyName}' is generic"
                     | None -> err arg.Span $"unknown type '{tyName}'{didYouMean tyName (Map.keys env.Types)}"
                 | _ -> err arg.Span "Args.load takes a type name, e.g. Args.load Cli")
        | EVar(("print" | "printerr") as pname), [ arg ] when isPrintFamily env pname ->
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
                    { Kind = TETo fmt
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
                    { Kind = TETo "yaml"
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
    | EPipe(arg, ({ Kind = EVar(("print" | "printerr") as pname) } as printExpr)) when isPrintFamily env pname ->
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

                let rec updOne (ty: Ty) (path: (string * Span) list) (value: Expr) : Result<TypedExpr, TypeError> =
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
                                        updOne fieldTy rest value
                                | None ->
                                    let hint = didYouMean field (List.map fst def.Fields)

                                    err
                                        fieldSpan
                                        $"record update cannot add fields: {typeName} has no field '{field}'{hint}"
                            | Some(Union _) -> err fieldSpan $"{typeName} is a union; only records update"
                            | None -> err fieldSpan $"unknown type '{typeName}'"
                        | TVar v ->
                            let r = freshName ctx "r"
                            ctx.Subst <- Map.add v (TRowVar(r, [])) ctx.Subst
                            updOne (TRowVar(r, [])) path value
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
                                updOne fieldTy rest value
                        | ty ->
                            err fieldSpan $"only records have updatable fields; this expression has type {formatTy ty}"

                let! tupdates =
                    updates
                    |> List.fold
                        (fun acc (path, value) ->
                            acc
                            |> Result.bind (fun ts ->
                                updOne tsource.Ty path value
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
    | EFrom(fmt, tyName) ->
        result {
            match fmt, tyName with
            | "porcelain", None ->
                match Map.tryFind "Change" env.Types with
                | Some(Record def) ->
                    return
                        { Kind = TEFrom("porcelain", def)
                          Ty = TFun(TSeq TStr, TSeq(TNamed("Change", [])))
                          Span = expr.Span }
                | _ -> return! err expr.Span "the porcelain adapter needs the builtin Change record"
            | "porcelain", Some _ -> return! err expr.Span "'from porcelain' has a fixed row type (Change)"
            | "json", Some name ->
                match Map.tryFind name env.Types with
                | Some(Record def) when def.Params.IsEmpty ->
                    do! jsonableRecord expr.Span def

                    return
                        { Kind = TEFrom("json", def)
                          Ty = TFun(TSeq TStr, TSeq(TNamed(name, [])))
                          Span = expr.Span }
                | Some(Record _) -> return! err expr.Span $"'from json' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) -> return! err expr.Span $"'{name}' is a union; 'from json' needs a record"
                | None -> return! err expr.Span $"unknown type '{name}'{didYouMean name (Map.keys env.Types)}"
            | "json", None -> return! err expr.Span "'from json' needs a record name, e.g. from json FileRow"
            | "yaml", Some name ->
                // from yaml T [D:yaml-v1]: seq<string> lines in, seq<T>
                // DOCUMENTS out (`---` separated; one doc = one element)
                match Map.tryFind name env.Types with
                | Some(Record def) when def.Params.IsEmpty ->
                    let! shape = yamlShape expr.Span env Set.empty (TNamed(name, []))

                    return
                        { Kind = TEFromYaml(name, shape)
                          Ty = TFun(TSeq TStr, TSeq(TNamed(name, [])))
                          Span = expr.Span }
                | Some(Record _) -> return! err expr.Span $"'from yaml' needs a monomorphic record; '{name}' is generic"
                | Some(Union _) -> return! err expr.Span $"'{name}' is a union; 'from yaml' needs a record"
                | None -> return! err expr.Span $"unknown type '{name}'{didYouMean name (Map.keys env.Types)}"
            | "yaml", None -> return! err expr.Span "'from yaml' needs a record name, e.g. from yaml Deployment"
            | fmt, _ -> return! err expr.Span $"unknown format '{fmt}'; available: json, porcelain, yaml"
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
                | _ -> checkScalarSplice ctx env "command arguments" a

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
            let checkHole = checkScalarSplice ctx env "interpolation holes"

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
            do!
                match piped with
                | Some(pipedTy, pipedSpan) -> bind ctx env pipedSpan (List.last paramTys) pipedTy
                | None -> Ok()

            let! typedArgs =
                List.zip args (List.truncate args.Length paramTys)
                |> List.fold
                    (fun acc (arg, paramTy) ->
                        acc
                        |> Result.bind (fun ts -> check ctx env arg paramTy |> Result.map (fun t -> t :: ts)))
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
        { Kind = EPipe(e, { Kind = EVar "print"; Span = e.Span })
          Span = e.Span }
    else
        match e.Kind with
        | ESeq(a, b) ->
            { Kind = ESeq(a, armTail b)
              Span = e.Span }
        // the tail rides through a scope and a let-in to their bodies
        // [D:within-scopes] — statement position arms a scope's final
        // command exactly as an if body's
        | EWithin(k, n, a, b) ->
            { Kind = EWithin(k, n, a, armTail b)
              Span = e.Span }
        | ELet(n, ns, v, b) ->
            { Kind = ELet(n, ns, v, armTail b)
              Span = e.Span }
        | _ -> e

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
                    { Kind = EVar "print"
                      Span = expr.Span }
                )
              Span = expr.Span }
            TUnit
    // the check direction rides INTO a scope's body [D:within-scopes] —
    // a statement-position `within` demands unit of the block, arming a
    // final command exactly as any block does
    | EWithin(kind, binder, arg, body), _ ->
        result {
            let! targ =
                match kind, arg with
                | "cd", Some a -> check ctx env a TStr |> Result.map Some
                | "env", Some a -> check ctx env a (TSeq(TNamed("EnvVar", []))) |> Result.map Some
                | _ -> Ok None

            let benv =
                match binder with
                | Some(n, _) -> bindParams env [ n, TStr ]
                | None -> env

            let! tbody = check ctx benv body expected

            return
                { Kind = TEWithin(kind, binder |> Option.map fst, targ, tbody)
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
            | ty ->
                return!
                    err
                        first.Span
                        $"a sequenced expression must be unit; this one is {formatTy ty} — bind it or print it"
        }
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

and private checkScalarSplice (ctx: Ctx) (env: TypeEnv) (what: string) (arg: Expr) : Result<TypedExpr, TypeError> =
    result {
        let! targ = infer ctx env arg

        match resolve ctx targ.Ty with
        | TVar v ->
            // DEFER [D:splice-default-last]: the enclosing statement's
            // inference may still resolve v (the pipe-into-lambda
            // repro); default-or-reject happens at the boundary
            ctx.PendingSplices <- (v, arg.Span, what) :: ctx.PendingSplices
            return targ
        | TStr
        | TInt
        | TBool -> return targ
        | ty -> return! err arg.Span $"{what} must be strings, ints or bools; this one is {formatTy ty}"
    }

// the yaml district's template typing [D:yaml-district]: splices carry
// the LIFTABLE law (string/int/bool, Yaml, Option of one, seq of those),
// key splices are strings, `for` binders bind like lambda params over
// the source's element type, literal duplicate keys are check errors.
and private yamlSpliceable (ctx: Ctx) (ty: Ty) : bool =
    match resolve ctx ty with
    | TInt
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
                return!
                    err
                        e.Span
                        $"a yaml splice takes string/int/bool, a Yaml node, Option of one, or a seq of those; got {formatTy rty}"
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
        (fun acc (v, span, what) ->
            acc
            |> Result.bind (fun () ->
                match resolve ctx (TVar v) with
                | TVar _ -> bind ctx env span TStr (TVar v)
                | TStr
                | TInt
                | TBool -> Ok()
                | ty -> err span $"{what} must be strings, ints or bools; this one is {formatTy ty}"))
        (Ok())

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
        | TESeq(a, b) -> TESeq(finalizeExpr ctx a, finalizeExpr ctx b)
        | TEWithin(k, n, a, b) -> TEWithin(k, n, a |> Option.map (finalizeExpr ctx), finalizeExpr ctx b)
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

let typecheckBinder (env: TypeEnv) (pat: Pattern) (expr: Expr) : Result<TypedExpr * (string * Scheme) list, TypeError> =
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

let typecheckWith
    (env: TypeEnv)
    (expr: Expr)
    : Result<TypedExpr * Map<string, Set<Cls>> * Map<string, (string * int * int * int) list>, TypeError> =
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
                Ok(te, residue, rowOriginsFor ctx resultVars)

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
    | TESeq(a, b) -> [ a; b ]
    | TEWithin(_, _, a, b) -> Option.toList a @ [ b ]
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

let typecheck (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
    typecheckWith env expr |> Result.map (fun (te, _, _) -> te)

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
    | TStr
    | TBool
    | TUnit -> Ok()
    | TSeq t -> validateTy env selfName selfArity allowed span t
    | TTuple ts -> allOk ts (validateTy env selfName selfArity allowed span)
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
          // [<Doc>] RETIRED [D:doc-help] — a `///` above the field is the one
          // source; a stale `[<Doc "x">]` is now the ordinary unknown-attribute
          // error (the did-you-mean over the remaining names).
          "Default",
          (function
          | Some(AStr _ | AInt _ | ABool _) -> None
          | None -> Some "expects a literal (string, int, or bool), e.g. [<Default 10>]") ]

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

let checkDecl (env: TypeEnv) (decl: Decl) : Result<TypeEnv, TypeError> =
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
                          RowOrigins = Map.empty }

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
                 | _ -> ()
         | _ -> ())

        childExprs te |> List.iter walk

    walk te
    List.ofSeq acc
