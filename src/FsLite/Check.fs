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

let private bindAll (env: TypeEnv) (bindings: (string * Ty) list) : TypeEnv =
    { env with
        Values = bindings |> List.fold (fun vs (n, t) -> Map.add n t vs) env.Values }

let rec private isEquatable (env: TypeEnv) (seen: Set<string>) (ty: Ty) : bool =
    match ty with
    | TInt _
    | TStr
    | TBool -> true
    | TFun _
    | TSeq _
    | TVar _ -> false
    | TNamed n ->
        seen.Contains n
        || (match Map.tryFind n env.Types with
            | Some(Record def) -> def.Fields |> List.forall (snd >> isEquatable env (Set.add n seen))
            | Some(Union def) ->
                def.Cases
                |> List.forall (fun (_, payload) -> payload |> Option.forall (isEquatable env (Set.add n seen)))
            | None -> false)

let private typeBinOp (env: TypeEnv) (opSpan: Span) (op: string) (l: TypedExpr) (r: TypedExpr) : Result<Ty, TypeError> =
    match op, l.Ty, r.Ty with
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

let rec private freeVars (ty: Ty) : Set<string> =
    match ty with
    | TVar v -> Set.singleton v
    | TFun(a, b) -> Set.union (freeVars a) (freeVars b)
    | TSeq t -> freeVars t
    | TInt _
    | TStr
    | TBool
    | TNamed _ -> Set.empty

let rec private substTy (s: Map<string, Ty>) (ty: Ty) : Ty =
    match ty with
    | TVar v -> Map.tryFind v s |> Option.defaultValue ty
    | TFun(a, b) -> TFun(substTy s a, substTy s b)
    | TSeq t -> TSeq(substTy s t)
    | t -> t

let rec private bindVars
    (span: Span)
    (declared: Ty)
    (actual: Ty)
    (s: Map<string, Ty>)
    : Result<Map<string, Ty>, TypeError> =
    match declared, actual with
    | TVar v, a ->
        match Map.tryFind v s with
        | None -> Ok(Map.add v a s)
        | Some bound when bound = a -> Ok s
        | Some bound -> mismatch span bound a
    | TFun(d1, d2), TFun(a1, a2) -> bindVars span d1 a1 s |> Result.bind (bindVars span d2 a2)
    | TSeq d, TSeq a -> bindVars span d a s
    | d, a when d = a -> Ok s
    | d, a -> mismatch span (substTy s d) a

let rec private funParams (arity: int) (ty: Ty) : (Ty list * Ty) option =
    if arity = 0 then
        Some([], ty)
    else
        match ty with
        | TFun(dom, cod) -> funParams (arity - 1) cod |> Option.map (fun (ps, r) -> dom :: ps, r)
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

let rec infer (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> =
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
        | Some ty ->
            Ok
                { Kind = TEVar name
                  Ty = ty
                  Span = expr.Span }
        | None ->
            let hint = didYouMean name (Map.keys env.Values)
            err expr.Span $"unbound variable '{name}'{hint}"
    | ELet(name, value, body) ->
        result {
            let! tvalue = infer env value
            let! tbody = infer (bindAll env [ name, tvalue.Ty ]) body

            return
                { Kind = TELet(name, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | ELambda(param, _) ->
        err expr.Span $"cannot infer the type of parameter '{param}'; use the lambda where a function type is expected"
    | EApp(({ Kind = ELambda(param, body) } as fnExpr), arg) ->
        result {
            let! targ = infer env arg
            let! tbody = infer (bindAll env [ param, targ.Ty ]) body

            let tfn =
                { Kind = TELambda(param, tbody)
                  Ty = TFun(targ.Ty, tbody.Ty)
                  Span = fnExpr.Span }

            return
                { Kind = TEApp(tfn, targ)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | EApp _ ->
        let head, args = spine expr
        checkSpine env head args None
    | EPipe(arg, ({ Kind = ELambda(param, body) } as fnExpr)) ->
        result {
            let! targ = infer env arg
            let! tbody = infer (bindAll env [ param, targ.Ty ]) body

            let tfn =
                { Kind = TELambda(param, tbody)
                  Ty = TFun(targ.Ty, tbody.Ty)
                  Span = fnExpr.Span }

            return
                { Kind = TEPipe(targ, tfn)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | EPipe(arg, ({ Kind = ETo fmt } as toExpr)) ->
        result {
            let! targ = infer env arg

            match fmt, targ.Ty with
            | "json", TSeq elem ->
                do! jsonableElem toExpr.Span env elem

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
            let! targ = infer env arg
            let head, args = spine fnExpr
            let! tfn = checkSpine env head args (Some(targ.Ty, arg.Span))

            match tfn.Ty with
            | TFun(_, resultTy) ->
                return
                    { Kind = TEPipe(targ, tfn)
                      Ty = resultTy
                      Span = expr.Span }
            | _ -> return! err fnExpr.Span "the right side of a pipe must be a function"
        }
    | EField(target, field, fieldSpan) ->
        result {
            let! ttarget = infer env target

            match ttarget.Ty with
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
            | ty -> return! err target.Span $"only records have fields; this expression has type {formatTy ty}"
        }
    | EBinOp(op, left, right) ->
        result {
            let! tleft = infer env left
            let! tright = infer env right
            let! ty = typeBinOp env expr.Span op tleft tright

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
                        check env value declaredTy |> Result.map (fun tv -> name, tv)

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
    | ETo _ -> err expr.Span "'to json' can only be used as a pipe stage, e.g. xs | to json"
    | EMatch(scrutinee, arms) ->
        result {
            let! tscrutinee = infer env scrutinee

            match arms with
            | [] -> return! err expr.Span "a match needs at least one arm"
            | (pat0, body0) :: rest ->
                let! bindings0 = checkPattern env tscrutinee.Ty pat0
                let! tbody0 = infer (bindAll env bindings0) body0

                let checkArm (pat: Pattern, body: Expr) =
                    result {
                        let! bindings = checkPattern env tscrutinee.Ty pat
                        let! tbody = check (bindAll env bindings) body tbody0.Ty
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
    (env: TypeEnv)
    (head: Expr)
    (args: Expr list)
    (piped: (Ty * Span) option)
    : Result<TypedExpr, TypeError> =
    result {
        let! thead = infer env head
        let arity = args.Length + (if piped.IsSome then 1 else 0)

        match funParams arity thead.Ty with
        | None ->
            match piped with
            | Some _ ->
                return!
                    err
                        head.Span
                        $"the right side of a pipe must be a function taking the piped value; it has type {formatTy thead.Ty}"
            | None ->
                return!
                    err
                        head.Span
                        $"this expression is not a function taking {args.Length} argument(s); it has type {formatTy thead.Ty}"
        | Some(paramTys, resultTy) ->
            let argParams = List.truncate args.Length paramTys

            let! s0 =
                match piped with
                | Some(pipedTy, pipedSpan) -> bindVars pipedSpan (List.last paramTys) pipedTy Map.empty
                | None -> Ok Map.empty

            let isLambda (e: Expr) =
                match e.Kind with
                | ELambda _ -> true
                | _ -> false

            let indexed = List.zip args argParams |> List.mapi (fun i (a, p) -> i, a, p)

            let inferPass (s, typed: Map<int, TypedExpr>) (i, arg: Expr, paramTy) =
                result {
                    let expected = substTy s paramTy

                    if Set.isEmpty (freeVars expected) then
                        let! targ = check env arg expected
                        return s, Map.add i targ typed
                    else
                        let! targ = infer env arg
                        let! s' = bindVars arg.Span expected targ.Ty s
                        return s', Map.add i targ typed
                }

            let lambdaPass (s, typed: Map<int, TypedExpr>) (i, arg: Expr, paramTy) =
                result {
                    let expected = substTy s paramTy

                    match arg.Kind, expected with
                    | ELambda(param, body), TFun(dom, cod) when Set.isEmpty (freeVars dom) ->
                        let! tbody = infer (bindAll env [ param, dom ]) body
                        let! s' = bindVars body.Span cod tbody.Ty s

                        let targ =
                            { Kind = TELambda(param, tbody)
                              Ty = TFun(dom, tbody.Ty)
                              Span = arg.Span }

                        return s', Map.add i targ typed
                    | ELambda _, TFun _ ->
                        return! err arg.Span "cannot infer the lambda's parameter type here; pipe the data in first"
                    | ELambda _, _ -> return! err arg.Span $"expected {formatTy expected}, got a function"
                    | _, _ -> return! inferPass (s, typed) (i, arg, paramTy)
                }

            let foldArgs pass state items =
                items
                |> List.fold (fun acc item -> Result.bind (fun st -> pass st item) acc) (Ok state)

            let notLambdas = indexed |> List.filter (fun (_, a, _) -> not (isLambda a))
            let lambdas = indexed |> List.filter (fun (_, a, _) -> isLambda a)

            let! s1, typed1 = foldArgs inferPass (s0, Map.empty) notLambdas
            let! s2, typed2 = foldArgs lambdaPass (s1, typed1) lambdas

            let fullTy =
                List.foldBack (fun p acc -> TFun(substTy s2 p, acc)) paramTys (substTy s2 resultTy)

            match piped with
            | Some _ when not (Set.isEmpty (freeVars (substTy s2 resultTy))) ->
                return! err head.Span $"cannot infer the type parameters of {formatTy thead.Ty}"
            | _ ->
                let applied =
                    args
                    |> List.mapi (fun i arg -> typed2[i], arg)
                    |> List.fold
                        (fun (acc: TypedExpr) (targ, argExpr) ->
                            let cod =
                                match acc.Ty with
                                | TFun(_, c) -> c
                                | _ -> failwith "unreachable: funParams guaranteed a function"

                            { Kind = TEApp(acc, targ)
                              Ty = cod
                              Span = Span.union acc.Span argExpr.Span })
                        { thead with Ty = fullTy }

                return applied
    }

and check (env: TypeEnv) (expr: Expr) (expected: Ty) : Result<TypedExpr, TypeError> =
    match expr.Kind, expected with
    | ELambda(param, body), TFun(dom, cod) ->
        result {
            let! tbody = check (bindAll env [ param, dom ]) body cod

            return
                { Kind = TELambda(param, tbody)
                  Ty = expected
                  Span = expr.Span }
        }
    | ELambda _, _ -> err expr.Span $"expected {formatTy expected}, got a function"
    | ELet(name, value, body), _ ->
        result {
            let! tvalue = infer env value
            let! tbody = check (bindAll env [ name, tvalue.Ty ]) body expected

            return
                { Kind = TELet(name, tvalue, tbody)
                  Ty = tbody.Ty
                  Span = expr.Span }
        }
    | _ ->
        result {
            let! te = infer env expr

            if te.Ty = expected then
                return te
            else
                return! mismatch expr.Span expected te.Ty
        }

let typecheck (env: TypeEnv) (expr: Expr) : Result<TypedExpr, TypeError> = infer env expr

let rec private validateTy (env: TypeEnv) (selfName: string) (span: Span) (ty: Ty) : Result<unit, TypeError> =
    match ty with
    | TInt _
    | TStr
    | TBool -> Ok()
    | TSeq t -> validateTy env selfName span t
    | TFun(a, b) -> Result.bind (fun () -> validateTy env selfName span b) (validateTy env selfName span a)
    | TVar v -> err span $"type variables ('{v}) are not allowed in declarations"
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
                    |> List.fold (fun vs (c, payload) -> Map.add c (ctorTy payload) vs) env.Values

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
