module FsLite.Eval

open FsLite.Check

let unreachable (why: string) : 'a = failwith $"unreachable: {why}"

[<CustomEquality; NoComparison>]
type Value =
    | VInt of int
    | VStr of string
    | VBool of bool
    | VRecord of record: string * fields: Map<string, Value>
    | VUnion of case: string * payload: Value option
    | VSeq of items: seq<Value>
    | VClosure of param: string * body: TypedExpr * env: Env
    | VBuiltin of (Value -> Value)

    override this.Equals(other) =
        match other with
        | :? Value as v ->
            match this, v with
            | VInt a, VInt b -> a = b
            | VStr a, VStr b -> a = b
            | VBool a, VBool b -> a = b
            | VRecord(n1, f1), VRecord(n2, f2) -> n1 = n2 && f1 = f2
            | VUnion(c1, p1), VUnion(c2, p2) -> c1 = c2 && p1 = p2
            | VSeq a, VSeq b -> obj.ReferenceEquals(a, b) || List.ofSeq a = List.ofSeq b
            | VClosure(p1, b1, e1), VClosure(p2, b2, e2) -> p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
            | VBuiltin f, VBuiltin g -> obj.ReferenceEquals(f, g)
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | VInt n -> hash n
        | VStr s -> hash s
        | VBool b -> hash b
        | VRecord(n, _) -> hash n
        | VUnion(c, _) -> hash c
        | VSeq _ -> 0
        | VClosure(p, _, _) -> hash p
        | VBuiltin f -> LanguagePrimitives.PhysicalHash f

and Env = Map<string, Value>

let rec formatValue (v: Value) : string =
    match v with
    | VInt n -> string n
    | VStr s -> $"\"{s}\""
    | VBool true -> "true"
    | VBool false -> "false"
    | VRecord(_, fields) ->
        let body =
            fields
            |> Seq.map (fun kv -> $"{kv.Key} = {formatValue kv.Value}")
            |> String.concat "; "

        "{ " + body + " }"
    | VUnion(case, None) -> case
    | VUnion(case, Some payload) -> $"{case} {formatValue payload}"
    | VSeq items ->
        let shown = items |> Seq.truncate 21 |> List.ofSeq

        let body = shown |> List.truncate 20 |> List.map formatValue |> String.concat "; "

        let ellipsis = if shown.Length > 20 then "; ..." else ""
        $"[{body}{ellipsis}]"
    | VClosure _ -> "<fun>"
    | VBuiltin _ -> "<builtin>"

let private binOp (op: string) (l: Value) (r: Value) : Value =
    match op, l, r with
    | "+", VInt a, VInt b -> VInt(a + b)
    | "+", VStr a, VStr b -> VStr(a + b)
    | "-", VInt a, VInt b -> VInt(a - b)
    | "*", VInt a, VInt b -> VInt(a * b)
    | "/", VInt a, VInt b -> VInt(a / b)
    | ">", VInt a, VInt b -> VBool(a > b)
    | "<", VInt a, VInt b -> VBool(a < b)
    | "==", a, b -> VBool(a = b)
    | _ -> unreachable $"the checker rejects '{op}' on {formatValue l} and {formatValue r}"

let rec eval (env: Env) (te: TypedExpr) : Value =
    match te.Kind with
    | TEInt(n, _) -> VInt n
    | TEStr s -> VStr s
    | TEBool b -> VBool b
    | TEVar name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> unreachable $"the checker rejects unbound variable '{name}'"
    | TELet(name, value, body) -> eval (Map.add name (eval env value) env) body
    | TELambda(param, body) -> VClosure(param, body, env)
    | TEApp(fn, arg) -> apply (eval env fn) (eval env arg)
    | TEPipe(arg, fn) -> apply (eval env fn) (eval env arg)
    | TEField(target, field) ->
        match eval env target with
        | VRecord(name, fields) ->
            match Map.tryFind field fields with
            | Some v -> v
            | None -> unreachable $"the checker rejects unknown field '{field}' on {name}"
        | v -> unreachable $"the checker rejects field access on {formatValue v}"
    | TEBinOp(op, l, r) -> binOp op (eval env l) (eval env r)

and apply (fn: Value) (arg: Value) : Value =
    match fn with
    | VClosure(param, body, closureEnv) -> eval (Map.add param arg closureEnv) body
    | VBuiltin f -> f arg
    | v -> unreachable $"the checker rejects application of {formatValue v}"
