module FsLite.Eval

open FsLite.Ast

[<CustomEquality; NoComparison>]
type Value =
    | VInt of int
    | VStr of string
    | VBool of bool
    | VClosure of param: string * body: Expr * env: Env
    | VBuiltin of (Value -> Value)

    override this.Equals(other) =
        match other with
        | :? Value as v ->
            match this, v with
            | VInt a, VInt b -> a = b
            | VStr a, VStr b -> a = b
            | VBool a, VBool b -> a = b
            | VClosure(p1, b1, e1), VClosure(p2, b2, e2) -> p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
            | VBuiltin f, VBuiltin g -> obj.ReferenceEquals(f, g)
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | VInt n -> hash n
        | VStr s -> hash s
        | VBool b -> hash b
        | VClosure(p, _, _) -> hash p
        | VBuiltin f -> LanguagePrimitives.PhysicalHash f

and Env = Map<string, Value>

let formatValue (v: Value) : string =
    match v with
    | VInt n -> string n
    | VStr s -> $"\"{s}\""
    | VBool true -> "true"
    | VBool false -> "false"
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
    | _ -> failwith $"operator '{op}' cannot be applied to {formatValue l} and {formatValue r}"

let rec eval (env: Env) (e: Expr) : Value =
    match e.Kind with
    | EInt(n, _) -> VInt n
    | EStr s -> VStr s
    | EBool b -> VBool b
    | EVar x ->
        match Map.tryFind x env with
        | Some v -> v
        | None -> failwith $"unbound variable '{x}'"
    | ELet(name, value, body) -> eval (Map.add name (eval env value) env) body
    | ELambda(param, body) -> VClosure(param, body, env)
    | EApp(fn, arg) -> apply (eval env fn) (eval env arg)
    | EPipe(arg, fn) -> apply (eval env fn) (eval env arg)
    | EField _ -> failwith "record values arrive in Spike 2"
    | EBinOp(op, l, r) -> binOp op (eval env l) (eval env r)

and apply (fn: Value) (arg: Value) : Value =
    match fn with
    | VClosure(param, body, closureEnv) -> eval (Map.add param arg closureEnv) body
    | VBuiltin f -> f arg
    | v -> failwith $"cannot apply a non-function value: {formatValue v}"

let builtins: Env =
    Map
        [ "double",
          VBuiltin(fun v ->
              match v with
              | VInt n -> VInt(n * 2)
              | _ -> failwith $"'double' expects an int, got {formatValue v}") ]
