module Weir.Eval

open Weir.Types
open Weir.Ast
open Weir.Check

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
    | VStr s ->
        let escaped =
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t")

        $"\"{escaped}\""
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
    | ">=", VInt a, VInt b -> VBool(a >= b)
    | "<=", VInt a, VInt b -> VBool(a <= b)
    | "==", a, b -> VBool(a = b)
    | "<>", a, b -> VBool(a <> b)
    | _ -> unreachable $"the checker rejects '{op}' on {formatValue l} and {formatValue r}"

let private jsonLine (v: Value) : string =
    let buffer = new System.Buffers.ArrayBufferWriter<byte>()
    use writer = new System.Text.Json.Utf8JsonWriter(buffer)

    let rec write (v: Value) =
        match v with
        | VInt n -> writer.WriteNumberValue n
        | VStr s -> writer.WriteStringValue s
        | VBool b -> writer.WriteBooleanValue b
        | VRecord(_, fields) ->
            writer.WriteStartObject()

            for kv in fields do
                writer.WritePropertyName kv.Key
                write kv.Value

            writer.WriteEndObject()
        | v -> unreachable $"the checker rejects 'to json' on {formatValue v}"

    write v
    writer.Flush()
    System.Text.Encoding.UTF8.GetString buffer.WrittenSpan

let private jsonRow (def: RecordDef) (line: string) : Value =
    use doc =
        try
            System.Text.Json.JsonDocument.Parse line
        with ex ->
            failwith $"from json: invalid json line: {line}"

    let root = doc.RootElement

    let readField (name: string, ty: Ty) =
        let mutable prop = Unchecked.defaultof<System.Text.Json.JsonElement>

        if not (root.TryGetProperty(name, &prop)) then
            failwith $"from json: missing field '{name}' in: {line}"

        let value =
            match ty, prop.ValueKind with
            | TInt _, System.Text.Json.JsonValueKind.Number -> VInt(prop.GetInt32())
            | TStr, System.Text.Json.JsonValueKind.String -> VStr(prop.GetString())
            | TBool, System.Text.Json.JsonValueKind.True -> VBool true
            | TBool, System.Text.Json.JsonValueKind.False -> VBool false
            | ty, kind -> failwith $"from json: field '{name}' expected {formatTy ty}, got {kind} in: {line}"

        name, value

    VRecord(def.Name, def.Fields |> List.map readField |> Map.ofList)

let private unquoteGitPath (path: string) : string =
    if path.Length >= 2 && path.StartsWith "\"" && path.EndsWith "\"" then
        let inner = path.Substring(1, path.Length - 2)
        let bytes = ResizeArray<byte>()
        let mutable i = 0

        while i < inner.Length do
            if inner[i] = '\\' && i + 1 < inner.Length then
                let c = inner[i + 1]

                if c >= '0' && c <= '7' && i + 3 < inner.Length then
                    bytes.Add(byte (System.Convert.ToInt32(inner.Substring(i + 1, 3), 8)))
                    i <- i + 4
                else
                    let b =
                        match c with
                        | 'n' -> byte '\n'
                        | 't' -> byte '\t'
                        | 'r' -> byte '\r'
                        | c -> byte c

                    bytes.Add b
                    i <- i + 2
            else
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(string inner[i]))
                i <- i + 1

        System.Text.Encoding.UTF8.GetString(bytes.ToArray())
    else
        path

let private renameTarget (path: string) : string =
    if path.StartsWith "\"" then
        let mutable i = 1

        while i < path.Length && not (path[i] = '"' && path[i - 1] <> '\\') do
            i <- i + 1

        let rest =
            if i + 1 <= path.Length then
                path.Substring(min (i + 1) path.Length)
            else
                ""

        if rest.StartsWith " -> " then rest.Substring 4 else path
    else
        match path.IndexOf " -> " with
        | -1 -> path
        | i -> path.Substring(i + 4)

let private porcelainRow (def: RecordDef) (line: string) : Value =
    if line.Length < 4 then
        failwith $"from porcelain: unexpected line: '{line}'"

    let x, y = line[0], line[1]
    let path = line.Substring 3 |> renameTarget |> unquoteGitPath

    VRecord(
        def.Name,
        Map
            [ "Status", VStr(string x + string y)
              "Staged", VBool(x <> ' ' && x <> '?')
              "Unstaged", VBool(y <> ' ')
              "Path", VStr path ]
    )

let private fromAdapter (fmt: string) (def: RecordDef) : Value =
    let rowOf =
        match fmt with
        | "json" -> jsonRow def
        | "porcelain" -> porcelainRow def
        | f -> unreachable $"the checker rejects unknown format '{f}'"

    VBuiltin(fun v ->
        match v with
        | VSeq lines ->
            VSeq(
                lines
                |> Seq.map (fun l ->
                    match l with
                    | VStr s -> rowOf s
                    | v -> unreachable $"the checker rejects 'from' on non-string elements: {formatValue v}")
            )
        | v -> unreachable $"the checker rejects 'from' on {formatValue v}")

let rec private tryBind (p: Pattern) (v: Value) : (string * Value) list option =
    match p.PKind, v with
    | PWildcard, _ -> Some []
    | PVar name, _ -> Some [ name, v ]
    | PCase(ctor, None), VUnion(case, None) -> if ctor = case then Some [] else None
    | PCase(ctor, Some argPat), VUnion(case, Some payload) -> if ctor = case then tryBind argPat payload else None
    | PCase _, VUnion _ -> None
    | PCase _, v -> unreachable $"the checker rejects constructor patterns on {formatValue v}"

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
    | TEBinOp("&&", l, r) ->
        (match eval env l with
         | VBool false -> VBool false
         | VBool true -> eval env r
         | v -> unreachable $"the checker rejects '&&' on {formatValue v}")
    | TEBinOp("||", l, r) ->
        (match eval env l with
         | VBool true -> VBool true
         | VBool false -> eval env r
         | v -> unreachable $"the checker rejects '||' on {formatValue v}")
    | TEBinOp(op, l, r) -> binOp op (eval env l) (eval env r)
    | TERecord(name, fields) -> VRecord(name, fields |> List.map (fun (n, fv) -> n, eval env fv) |> Map.ofList)
    | TEList items -> VSeq(items |> List.map (eval env))
    | TEFrom(fmt, def) -> fromAdapter fmt def
    | TETo _ ->
        VBuiltin(fun v ->
            match v with
            | VSeq items -> VSeq(items |> Seq.map (jsonLine >> VStr))
            | v -> unreachable $"the checker rejects 'to json' on {formatValue v}")
    | TEMatch(scrutinee, arms) ->
        let v = eval env scrutinee

        let rec tryArms arms =
            match arms with
            | [] -> failwith $"match failure: no arm matched {formatValue v}"
            | (pat, body) :: rest ->
                match tryBind pat v with
                | Some bindings -> eval (bindings |> List.fold (fun e (n, bv) -> Map.add n bv e) env) body
                | None -> tryArms rest

        tryArms arms

and apply (fn: Value) (arg: Value) : Value =
    match fn with
    | VClosure(param, body, closureEnv) -> eval (Map.add param arg closureEnv) body
    | VBuiltin f -> f arg
    | v -> unreachable $"the checker rejects application of {formatValue v}"

let constructorValues (cases: (string * Ty option) list) : (string * Value) list =
    cases
    |> List.map (fun (case, payload) ->
        match payload with
        | None -> case, VUnion(case, None)
        | Some _ -> case, VBuiltin(fun v -> VUnion(case, Some v)))
