module FsLite.Check

open FsLite.Ast
open FsLite.Types

type TypeError = { Span: Span; Message: string }

let formatError (e: TypeError) : string =
    $"[{e.Span.Start.Line}:{e.Span.Start.Col}-{e.Span.End.Col}] type error: {e.Message}"

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

            let env' =
                { env with
                    Values = Map.add name tvalue.Ty env.Values }

            let! tbody = infer env' body

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

            let env' =
                { env with
                    Values = Map.add param targ.Ty env.Values }

            let! tbody = infer env' body

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

            let env' =
                { env with
                    Values = Map.add param targ.Ty env.Values }

            let! tbody = infer env' body

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
            | TRecord recordName ->
                match Map.tryFind recordName env.Types with
                | None -> return! err target.Span $"unknown record type '{recordName}'"
                | Some def ->
                    match def.Fields |> List.tryFind (fun (f, _) -> f = field) with
                    | Some(_, fieldTy) ->
                        return
                            { Kind = TEField(ttarget, field)
                              Ty = fieldTy
                              Span = expr.Span }
                    | None ->
                        let hint = didYouMean field (List.map fst def.Fields)
                        return! err fieldSpan $"{recordName} has no field '{field}'{hint}"
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

and check (env: TypeEnv) (expr: Expr) (expected: Ty) : Result<TypedExpr, TypeError> =
    match expr.Kind, expected with
    | ELambda(param, body), TFun(dom, cod) ->
        result {
            let env' =
                { env with
                    Values = Map.add param dom env.Values }

            let! tbody = check env' body cod

            return
                { Kind = TELambda(param, tbody)
                  Ty = expected
                  Span = expr.Span }
        }
    | ELambda _, _ -> err expr.Span $"expected {formatTy expected}, got a function"
    | ELet(name, value, body), _ ->
        result {
            let! tvalue = infer env value

            let env' =
                { env with
                    Values = Map.add name tvalue.Ty env.Values }

            let! tbody = check env' body expected

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
