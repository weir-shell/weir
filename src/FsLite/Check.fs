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

let private typeBinOp (opSpan: Span) (op: string) (l: TypedExpr) (r: TypedExpr) : Result<Ty, TypeError> =
    match op, l.Ty, r.Ty with
    | ("+" | "-"), TInt m, TInt n when m = n -> Ok(TInt m)
    | "+", TStr, TStr -> Ok TStr
    | ("*" | "/"), TInt None, TInt None -> Ok(TInt None)
    | (">" | "<"), TInt m, TInt n when m = n -> Ok TBool
    | "==", a, b when
        a = b
        && (match a with
            | TFun _ -> false
            | _ -> true)
        ->
        Ok TBool
    | _, (TInt _ as a), (TInt _ as b) when a <> b -> mismatch r.Span a b
    | _, a, b when a <> b -> mismatch r.Span a b
    | _, a, _ -> err opSpan $"operator '{op}' is not defined for {formatTy a}"

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
    | EApp(fn, arg) ->
        result {
            let! tfn = infer env fn

            match tfn.Ty with
            | TFun(dom, cod) ->
                let! targ = check env arg dom

                return
                    { Kind = TEApp(tfn, targ)
                      Ty = cod
                      Span = expr.Span }
            | ty -> return! err fn.Span $"this expression is not a function; it has type {formatTy ty}"
        }
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
    | EPipe(arg, fn) ->
        result {
            let! tfn = infer env fn

            match tfn.Ty with
            | TFun(dom, cod) ->
                let! targ = check env arg dom

                return
                    { Kind = TEPipe(targ, tfn)
                      Ty = cod
                      Span = expr.Span }
            | ty -> return! err fn.Span $"the right side of a pipe must be a function; it has type {formatTy ty}"
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
            let! ty = typeBinOp expr.Span op tleft tright

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
