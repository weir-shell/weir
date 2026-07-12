module FsLite.Check

open FsLite.Ast
open FsLite.Types

type TypeError = { Span: Span; Message: string }

let formatError (e: TypeError) : string =
    $"[{e.Span.Start.Line}:{e.Span.Start.Col}-{e.Span.End.Col}] type error: {e.Message}"

type Warning = { Span: Span; Message: string }

let formatWarning (w: Warning) : string =
    $"[{w.Span.Start.Line}:{w.Span.Start.Col}-{w.Span.End.Col}] warning: {w.Message}"

type TypedExpr = { Kind: TypedKind; Ty: Ty; Span: Span }

and TypedKind =
    | TEInt of value: int * measure: string option
    | TEStr of string
    | TEBool of bool
    | TEVar of string
    | TELet of name: string * value: TypedExpr * body: TypedExpr
    | TELambda of param: string * body: TypedExpr
    | TEApp of fn: TypedExpr * arg: TypedExpr
    | TEPipe of arg: TypedExpr * fn: TypedExpr
    | TEField of target: TypedExpr * field: string
    | TEBinOp of op: string * left: TypedExpr * right: TypedExpr
    | TERecord of record: string * fields: (string * TypedExpr) list
    | TEMatch of scrutinee: TypedExpr * arms: (Pattern * TypedExpr) list
    | TEFrom of format: string * rowDef: RecordDef
    | TETo of format: string

type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return x = Ok x
    member _.ReturnFrom(r: Result<_, _>) = r

let private result = ResultBuilder()

let private err (span: Span) (msg: string) : Result<'a, TypeError> = Error { Span = span; Message = msg }

let private mismatch (span: Span) (expected: Ty) (actual: Ty) =
    err span $"expected {formatTy expected}, got {formatTy actual}"

let private editDistance (a: string) (b: string) : int =
    let d = Array2D.create (a.Length + 1) (b.Length + 1) 0

    for i in 0 .. a.Length do
        d[i, 0] <- i

    for j in 0 .. b.Length do
        d[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)

    d[a.Length, b.Length]

let private didYouMean (name: string) (candidates: seq<string>) : string =
    candidates
    |> Seq.map (fun c -> c, editDistance name c)
    |> Seq.filter (fun (_, d) -> d <= 2)
    |> Seq.sortBy snd
    |> Seq.tryHead
    |> Option.map (fun (c, _) -> $". Did you mean '{c}'?")
    |> Option.defaultValue ""

let private allOk (items: 'a list) (f: 'a -> Result<unit, TypeError>) : Result<unit, TypeError> =
    items |> List.fold (fun acc x -> Result.bind (fun () -> f x) acc) (Ok())

let private firstDup (xs: string list) : string option =
    xs
    |> List.countBy id
    |> List.tryPick (fun (x, n) -> if n > 1 then Some x else None)

let private bindParams (env: TypeEnv) (bindings: (string * Ty) list) : TypeEnv =
    { env with
        Values = bindings |> List.fold (fun vs (n, t) -> Map.add n (mono t) vs) env.Values }

type private Ctx =
    { mutable Fresh: int
      mutable Subst: Map<string, Ty>
      mutable Rows: Map<string, Map<string, Ty * Span>> }

let private newCtx () =
    { Fresh = 0
      Subst = Map.empty
      Rows = Map.empty }

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
            | _ -> Set.empty

        let rows = rowNames sch.Ty

        let mapping =
            sch.Forall
            |> Set.toList
            |> List.map (fun v -> v, freshName ctx (if rows.Contains v then "r" else "a"))
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
                    TRowVar(r', fields')
                | None -> TRowVar(r, fields')
            | TFun(a, b) -> TFun(rename a, rename b)
            | TSeq t -> TSeq(rename t)
            | t -> t

        rename sch.Ty

let private envFreeVars (ctx: Ctx) (env: TypeEnv) : Set<string> =
    env.Values
    |> Map.fold (fun acc _ sch -> acc + (tyVars (finalTy ctx sch.Ty) - sch.Forall)) Set.empty

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
            Ok()
    | TRowVar(r, _), TNamed n
    | TNamed n, TRowVar(r, _) -> dischargeRow ctx env span r n
    | TRowVar(r1, _), TRowVar(r2, _) -> mergeRows ctx env r1 r2
    | (TRowVar _ as rv), t
    | t, (TRowVar _ as rv) -> err span $"expected {formatTy (finalTy ctx rv)}, got {formatTy (finalTy ctx t)}"
    | TFun(e1, e2), TFun(a1, a2) -> bind ctx env span e1 a1 |> Result.bind (fun () -> bind ctx env span e2 a2)
    | TSeq e, TSeq a -> bind ctx env span e a
    | e, a -> mismatch span (finalTy ctx e) (finalTy ctx a)

and private dischargeRow (ctx: Ctx) (env: TypeEnv) (span: Span) (r: string) (name: string) : Result<unit, TypeError> =
    match Map.tryFind name env.Types with
    | Some(Record def) ->
        let constraints =
            Map.tryFind r ctx.Rows |> Option.defaultValue Map.empty |> Map.toList

        ctx.Subst <- Map.add r (TNamed name) ctx.Subst

        allOk constraints (fun (field, (ft, fspan)) ->
            match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
            | Some(_, declTy) -> bind ctx env fspan declTy ft
            | None ->
                let hint = didYouMean field (List.map fst def.Fields)
                err fspan $"{name} has no field '{field}'{hint}")
    | Some(Union _) -> err span $"{name} is a union; only records have fields"
    | None -> err span $"unknown type '{name}'"

and private mergeRows (ctx: Ctx) (env: TypeEnv) (r1: string) (r2: string) : Result<unit, TypeError> =
    if r1 = r2 then
        Ok()
    else
        let fields1 = Map.tryFind r1 ctx.Rows |> Option.defaultValue Map.empty
        ctx.Subst <- Map.add r1 (TRowVar(r2, [])) ctx.Subst

        allOk (Map.toList fields1) (fun (field, (ft, fspan)) ->
            let fields2 = Map.tryFind r2 ctx.Rows |> Option.defaultValue Map.empty

            match Map.tryFind field fields2 with
            | Some(ft2, _) -> bind ctx env fspan ft2 ft
            | None ->
                ctx.Rows <- Map.add r2 (Map.add field (ft, fspan) fields2) ctx.Rows
                Ok())

let rec private isEquatable (env: TypeEnv) (seen: Set<string>) (ty: Ty) : bool =
    match ty with
    | TInt _
    | TStr
    | TBool -> true
    | TFun _
    | TSeq _
    | TVar _
    | TRowVar _ -> false
    | TNamed n ->
        seen.Contains n
        || (match Map.tryFind n env.Types with
            | Some(Record def) -> def.Fields |> List.forall (snd >> isEquatable env (Set.add n seen))
            | Some(Union def) ->
                def.Cases
                |> List.forall (fun (_, payload) -> payload |> Option.forall (isEquatable env (Set.add n seen)))
            | None -> false)

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
    | ("*" | "/"), TVar _, TVar _ ->
        retryAfter (
            bind ctx env l.Span (TInt None) l.Ty
            |> Result.bind (fun () -> bind ctx env r.Span (TInt None) r.Ty)
        )
    | _, TVar _, ((TInt _ | TStr | TBool) as t) -> retryAfter (bind ctx env l.Span t l.Ty)
    | _, ((TInt _ | TStr | TBool) as t), TVar _ -> retryAfter (bind ctx env r.Span t r.Ty)
    | _, TVar _, TVar _ -> err opSpan $"cannot infer the operand types of '{op}'; pipe data in or use concrete values"
    | _, TRowVar _, _
    | _, _, TRowVar _ -> err opSpan $"operator '{op}' is not defined for records"
    | ("+" | "-"), TInt m, TInt n when m = n -> Ok(TInt m)
    | "+", TStr, TStr -> Ok TStr
    | ("*" | "/"), TInt None, TInt None -> Ok(TInt None)
    | (">" | "<"), TInt m, TInt n when m = n -> Ok TBool
    | "==", a, b when a = b && isEquatable env Set.empty a -> Ok TBool
    | "==", a, b when a = b ->
        err opSpan $"'==' is not defined for {formatTy a}; sequences and functions cannot be compared"
    | _, (TInt _ as a), (TInt _ as b) when a <> b -> mismatch r.Span a b
    | _, a, b when a <> b -> mismatch r.Span a b
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

let private jsonableRecord (span: Span) (def: RecordDef) : Result<unit, TypeError> =
    allOk def.Fields (fun (name, ty) ->
        match ty with
        | TInt _
        | TStr
        | TBool -> Ok()
        | ty -> err span $"field '{name}' has type {formatTy ty}; json rows support int, string and bool fields")

let private jsonableElem (span: Span) (env: TypeEnv) (elem: Ty) : Result<unit, TypeError> =
    match elem with
    | TInt _
    | TStr
    | TBool -> Ok()
    | TNamed n ->
        match Map.tryFind n env.Types with
        | Some(Record def) -> jsonableRecord span def
        | _ -> err span $"'to json' needs primitive or record elements, got {formatTy elem}"
    | _ -> err span $"'to json' needs primitive or record elements, got {formatTy elem}"

let rec private checkPattern (env: TypeEnv) (ty: Ty) (p: Pattern) : Result<(string * Ty) list, TypeError> =
    match p.PKind with
    | PWildcard -> Ok []
    | PVar name -> Ok [ name, ty ]
    | PCase(ctor, argPat) ->
        match ty with
        | TNamed typeName ->
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
                    match argPat with
                    | Some ap -> checkPattern env payloadTy ap
                    | None -> err p.PSpan $"'{ctor}' carries {formatTy payloadTy}; add a pattern for it"
            | Some(Record _) -> err p.PSpan $"{typeName} is a record; only a name or '_' can match it"
            | None -> err p.PSpan $"unknown type '{typeName}'"
        | ty -> err p.PSpan $"constructor patterns need a union value; this one has type {formatTy ty}"

let rec private infer (ctx: Ctx) (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
    match expr.Kind with
    | EInt(n, m) ->
        Ok
            { Kind = TEInt(n, m)
              Ty = TInt m
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
    | EVar name ->
        match Map.tryFind name env.Values with
        | Some sch ->
            Ok
                { Kind = TEVar name
                  Ty = instantiate ctx expr.Span sch
                  Span = expr.Span }
        | None ->
            let hint = didYouMean name (Map.keys env.Values)
            err expr.Span $"unbound variable '{name}'{hint}"
    | ELet(name, value, body) ->
        result {
            let! tvalue = infer ctx env value
            let valueTy = finalTy ctx tvalue.Ty

            let scheme =
                { Forall = tyVars valueTy - envFreeVars ctx env
                  Ty = valueTy }

            let! tbody =
                infer
                    ctx
                    { env with
                        Values = Map.add name scheme env.Values }
                    body

            return
                { Kind = TELet(name, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | ELambda(param, body) ->
        result {
            let paramTy = TVar(freshName ctx "a")
            let! tbody = infer ctx (bindParams env [ param, paramTy ]) body

            return
                { Kind = TELambda(param, tbody)
                  Ty = TFun(paramTy, tbody.Ty)
                  Span = expr.Span }
        }
    | EApp _ ->
        let head, args = spine expr
        checkSpine ctx env head args None
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
            | fmt, _ -> return! err toExpr.Span $"unknown output format '{fmt}'; available: json"
        }
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
    | EField(target, field, fieldSpan) ->
        result {
            let! ttarget = infer ctx env target

            match resolve ctx ttarget.Ty with
            | TNamed typeName ->
                match Map.tryFind typeName env.Types with
                | Some(Record def) ->
                    match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
                    | Some(_, fieldTy) ->
                        return
                            { Kind = TEField(ttarget, field)
                              Ty = fieldTy
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
            let! tright = infer ctx env right
            let! ty = typeBinOp ctx env expr.Span op tleft tright

            return
                { Kind = TEBinOp(op, tleft, tright)
                  Ty = ty
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
                    let checkField (name: string, _: Span, value: Expr) =
                        let declaredTy = def.Fields |> List.find (fun (f, _) -> f = name) |> snd
                        check ctx env value declaredTy |> Result.map (fun tv -> name, tv)

                    let! tfields =
                        fields
                        |> List.fold
                            (fun acc f -> acc |> Result.bind (fun ts -> checkField f |> Result.map (fun t -> t :: ts)))
                            (Ok [])

                    return
                        { Kind = TERecord(def.Name, List.rev tfields)
                          Ty = TNamed def.Name
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
                          Ty = TFun(TSeq TStr, TSeq(TNamed "Change"))
                          Span = expr.Span }
                | _ -> return! err expr.Span "the porcelain adapter needs the builtin Change record"
            | "porcelain", Some _ -> return! err expr.Span "'from porcelain' has a fixed row type (Change)"
            | "json", Some name ->
                match Map.tryFind name env.Types with
                | Some(Record def) ->
                    do! jsonableRecord expr.Span def

                    return
                        { Kind = TEFrom("json", def)
                          Ty = TFun(TSeq TStr, TSeq(TNamed name))
                          Span = expr.Span }
                | Some(Union _) -> return! err expr.Span $"'{name}' is a union; 'from json' needs a record"
                | None -> return! err expr.Span $"unknown type '{name}'{didYouMean name (Map.keys env.Types)}"
            | "json", None -> return! err expr.Span "'from json' needs a record name, e.g. from json FileRow"
            | fmt, _ -> return! err expr.Span $"unknown format '{fmt}'; available: json, porcelain"
        }
    | ETo _ -> err expr.Span "'to json' can only be used as a pipe stage, e.g. xs |> to json"
    | EMatch(scrutinee, arms) ->
        result {
            let! tscrutinee = infer ctx env scrutinee
            let scrutTy = resolve ctx tscrutinee.Ty

            match arms with
            | [] -> return! err expr.Span "a match needs at least one arm"
            | (pat0, body0) :: rest ->
                let! bindings0 = checkPattern env scrutTy pat0
                let! tbody0 = infer ctx (bindParams env bindings0) body0

                let checkArm (pat: Pattern, body: Expr) =
                    result {
                        let! bindings = checkPattern env scrutTy pat
                        let! tbody = check ctx (bindParams env bindings) body tbody0.Ty
                        return pat, tbody
                    }

                let! trest =
                    rest
                    |> List.fold
                        (fun acc arm -> acc |> Result.bind (fun ts -> checkArm arm |> Result.map (fun t -> t :: ts)))
                        (Ok [])

                return
                    { Kind = TEMatch(tscrutinee, (pat0, tbody0) :: List.rev trest)
                      Ty = tbody0.Ty
                      Span = expr.Span }
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
        let arity = args.Length + (if piped.IsSome then 1 else 0)

        match funParams ctx arity thead.Ty with
        | None ->
            match piped with
            | Some _ ->
                return!
                    err
                        head.Span
                        $"the right side of a pipe must be a function taking the piped value; it has type {formatTy (finalTy ctx thead.Ty)}"
            | None ->
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

and private check (ctx: Ctx) (env: TypeEnv) (expr: Expr) (expected: Ty) : Result<TypedExpr, TypeError> =
    match expr.Kind, resolve ctx expected with
    | ELambda(param, body), TFun(dom, cod) ->
        result {
            let env' = bindParams env [ param, dom ]

            let! tbody =
                if hasVars ctx cod then
                    result {
                        let! tbody = infer ctx env' body
                        do! bind ctx env tbody.Span cod tbody.Ty
                        return tbody
                    }
                else
                    check ctx env' body cod

            return
                { Kind = TELambda(param, tbody)
                  Ty = TFun(dom, tbody.Ty)
                  Span = expr.Span }
        }
    | ELambda _, (TInt _ | TStr | TBool | TSeq _ | TNamed _ as t) ->
        err expr.Span $"expected {formatTy t}, got a function"
    | ELet(name, value, body), _ ->
        result {
            let! tvalue = infer ctx env value
            let valueTy = finalTy ctx tvalue.Ty

            let scheme =
                { Forall = tyVars valueTy - envFreeVars ctx env
                  Ty = valueTy }

            let! tbody =
                check
                    ctx
                    { env with
                        Values = Map.add name scheme env.Values }
                    body
                    expected

            return
                { Kind = TELet(name, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | _ ->
        result {
            let! te = infer ctx env expr
            do! bind ctx env expr.Span expected te.Ty
            return te
        }

let rec private finalizeExpr (ctx: Ctx) (te: TypedExpr) : TypedExpr =
    let kind =
        match te.Kind with
        | TELet(n, v, b) -> TELet(n, finalizeExpr ctx v, finalizeExpr ctx b)
        | TELambda(p, b) -> TELambda(p, finalizeExpr ctx b)
        | TEApp(f, a) -> TEApp(finalizeExpr ctx f, finalizeExpr ctx a)
        | TEPipe(a, f) -> TEPipe(finalizeExpr ctx a, finalizeExpr ctx f)
        | TEField(t, f) -> TEField(finalizeExpr ctx t, f)
        | TEBinOp(op, l, r) -> TEBinOp(op, finalizeExpr ctx l, finalizeExpr ctx r)
        | TERecord(n, fields) -> TERecord(n, fields |> List.map (fun (f, v) -> f, finalizeExpr ctx v))
        | TEMatch(s, arms) -> TEMatch(finalizeExpr ctx s, arms |> List.map (fun (p, b) -> p, finalizeExpr ctx b))
        | leaf -> leaf

    { te with
        Kind = kind
        Ty = finalTy ctx te.Ty }

let typecheck (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
    let ctx = newCtx ()
    infer ctx env expr |> Result.map (finalizeExpr ctx)

let rec private validateTy (env: TypeEnv) (selfName: string) (span: Span) (ty: Ty) : Result<unit, TypeError> =
    match ty with
    | TInt _
    | TStr
    | TBool -> Ok()
    | TSeq t -> validateTy env selfName span t
    | TFun(a, b) -> Result.bind (fun () -> validateTy env selfName span b) (validateTy env selfName span a)
    | TVar v -> err span $"type variables ('{v}) are not allowed in declarations"
    | TRowVar _ -> err span "row types are not allowed in declarations"
    | TNamed n ->
        if n = selfName || Map.containsKey n env.Types then
            Ok()
        else
            err span $"unknown type '{n}'{didYouMean n (Map.keys env.Types)}"

let checkDecl (env: TypeEnv) (decl: Decl) : Result<TypeEnv, TypeError> =
    match decl.Body with
    | DRecord fields ->
        result {
            match firstDup (List.map fst fields) with
            | Some dup -> return! err decl.Span $"duplicate field '{dup}'"
            | None ->
                do! allOk fields (snd >> validateTy env decl.Name decl.Span)

                let def = Record { Name = decl.Name; Fields = fields }

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
                        | Some ty -> validateTy env decl.Name decl.Span ty
                        | None -> Ok())

                let def = Union { Name = decl.Name; Cases = cases }

                let ctorTy payload =
                    match payload with
                    | None -> TNamed decl.Name
                    | Some ty -> TFun(ty, TNamed decl.Name)

                let values =
                    cases
                    |> List.fold (fun vs (c, payload) -> Map.add c (generalize (ctorTy payload)) vs) env.Values

                return
                    { env with
                        Types = Map.add decl.Name def env.Types
                        Values = values }
        }

let warnings (env: TypeEnv) (te: TypedExpr) : Warning list =
    let acc = ResizeArray<Warning>()

    let isIrrefutable (p: Pattern) =
        match p.PKind with
        | PWildcard
        | PVar _ -> true
        | PCase _ -> false

    let rec walk (te: TypedExpr) =
        match te.Kind with
        | TEInt _
        | TEStr _
        | TEBool _
        | TEVar _ -> ()
        | TELet(_, value, body) ->
            walk value
            walk body
        | TELambda(_, body) -> walk body
        | TEApp(a, b)
        | TEPipe(a, b) ->
            walk a
            walk b
        | TEField(target, _) -> walk target
        | TEBinOp(_, l, r) ->
            walk l
            walk r
        | TERecord(_, fields) -> fields |> List.iter (snd >> walk)
        | TEFrom _
        | TETo _ -> ()
        | TEMatch(scrutinee, arms) ->
            walk scrutinee
            arms |> List.iter (snd >> walk)

            match arms |> List.tryFindIndex (fst >> isIrrefutable) with
            | Some i ->
                arms
                |> List.skip (i + 1)
                |> List.iter (fun (p, _) ->
                    acc.Add
                        { Span = p.PSpan
                          Message = "this match arm is unreachable" })
            | None ->
                match scrutinee.Ty with
                | TNamed typeName ->
                    match Map.tryFind typeName env.Types with
                    | Some(Union def) ->
                        let covers (case: string) =
                            arms
                            |> List.exists (fun (p, _) ->
                                match p.PKind with
                                | PCase(c, None) -> c = case
                                | PCase(c, Some arg) -> c = case && isIrrefutable arg
                                | _ -> false)

                        let missing = def.Cases |> List.map fst |> List.filter (covers >> not)

                        if not missing.IsEmpty then
                            let missingList = String.concat ", " missing

                            acc.Add
                                { Span = te.Span
                                  Message = $"match is not exhaustive; missing: {missingList}" }
                    | _ -> ()
                | ty ->
                    acc.Add
                        { Span = te.Span
                          Message = $"match on {formatTy ty} needs a catch-all pattern" }

    walk te
    List.ofSeq acc
