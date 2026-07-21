module Weir.Eval

open Weir.Types
open Weir.Ast
open Weir.Check

let unreachable (why: string) : 'a = failwith $"unreachable: {why}"

// Exit.code's carrier: an intentional exit, not an error — the runner
// returns the code silently instead of printing a located message.
exception ExitRequest of code: int

[<CustomEquality; NoComparison>]
type Value =
    | VInt of int64
    | VStr of string
    | VBool of bool
    | VUnit
    | VRecord of record: string * fields: Map<string, Value>
    | VUnion of case: string * payload: Value option
    | VSeq of items: seq<Value>
    | VTuple of items: Value list
    | VClosure of param: string * body: TypedExpr * env: Env
    | VClosurePat of binder: Pattern * body: TypedExpr * env: Env
    | VBuiltin of (Value -> Value)

    override this.Equals(other) =
        match other with
        | :? Value as v ->
            match this, v with
            | VInt a, VInt b -> a = b
            | VStr a, VStr b -> a = b
            | VBool a, VBool b -> a = b
            | VUnit, VUnit -> true
            | VRecord(n1, f1), VRecord(n2, f2) -> n1 = n2 && f1 = f2
            | VUnion(c1, p1), VUnion(c2, p2) -> c1 = c2 && p1 = p2
            | VSeq a, VSeq b -> obj.ReferenceEquals(a, b) || List.ofSeq a = List.ofSeq b
            | VTuple a, VTuple b -> a = b
            | VClosure(p1, b1, e1), VClosure(p2, b2, e2) -> p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
            | VClosurePat(p1, b1, e1), VClosurePat(p2, b2, e2) -> p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
            | VBuiltin f, VBuiltin g -> obj.ReferenceEquals(f, g)
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | VInt n -> hash n
        | VStr s -> hash s
        | VBool b -> hash b
        | VUnit -> 17
        | VRecord(n, _) -> hash n
        | VUnion(c, _) -> hash c
        | VSeq _ -> 0
        | VTuple items -> hash (List.length items)
        | VClosure(p, _, _) -> hash p
        | VClosurePat(p, _, _) -> hash p
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
    | VUnion(case, Some payload) ->
        let inner = formatValue payload

        match payload with
        | VInt _
        | VStr _
        | VBool _ -> $"{case} {inner}"
        | _ -> $"{case} ({inner})"
    | VSeq items ->
        let shown = items |> Seq.truncate 21 |> List.ofSeq

        let body = shown |> List.truncate 20 |> List.map formatValue |> String.concat "; "

        let ellipsis = if shown.Length > 20 then "; ..." else ""
        $"[{body}{ellipsis}]"
    | VClosure _ -> "<fun>"
    | VClosurePat _ -> "<fun>"
    | VBuiltin _ -> "<builtin>"
    | VUnit -> "()"
    | VTuple items -> "(" + (items |> List.map formatValue |> String.concat ", ") + ")"

// The line-per-element renderer. Both consumers — the print builtin and the
// runner's command-statement streaming — must call this one function; the
// byte-identity of their output is a plan-level claim, not a coincidence.
let writeLinesTo (w: System.IO.TextWriter) (items: seq<Value>) : unit =
    for item in items do
        match item with
        | VStr s -> w.WriteLine s
        | other -> w.WriteLine(formatValue other)

let writeLines (items: seq<Value>) : unit = writeLinesTo System.Console.Out items

// Overflow policy (Part 3): int arithmetic is CHECKED — wrapping silently
// is the bash-calculator bug class; a raise joins the named runtime
// failure classes instead.
let private checkedInt (f: unit -> int64) : Value =
    try
        VInt(f ())
    with :? System.OverflowException ->
        failwith "integer overflow"

let private binOp (op: string) (l: Value) (r: Value) : Value =
    match op, l, r with
    | "+", VInt a, VInt b -> checkedInt (fun () -> Checked.(+) a b)
    | "+", VStr a, VStr b -> VStr(a + b)
    | "-", VInt a, VInt b -> checkedInt (fun () -> Checked.(-) a b)
    | "*", VInt a, VInt b -> checkedInt (fun () -> Checked.(*) a b)
    | "/", VInt a, VInt b -> checkedInt (fun () -> a / b)
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
            | TInt, System.Text.Json.JsonValueKind.Number -> VInt(prop.GetInt64())
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

let scalarString (what: string) (v: Value) : string =
    match v with
    | VStr s -> s
    | VInt n -> string n
    | VBool true -> "true"
    | VBool false -> "false"
    | v -> unreachable $"the checker rejects {what} {formatValue v}"

let rec private tryBind (p: Pattern) (v: Value) : (string * Value) list option =
    match p.PKind, v with
    | PWildcard, _ -> Some []
    | PVar name, _ -> Some [ name, v ]
    | PBool b, VBool v -> if b = v then Some [] else None
    | PBool _, v -> unreachable $"the checker rejects bool patterns on {formatValue v}"
    | PInt n, VInt v -> if n = v then Some [] else None
    | PInt _, v -> unreachable $"the checker rejects int patterns on {formatValue v}"
    | PStr s, VStr v -> if s = v then Some [] else None
    | PStr _, v -> unreachable $"the checker rejects string patterns on {formatValue v}"
    | PUnit, _ -> Some []
    | PTuple ps, VTuple vs when List.length ps = List.length vs ->
        List.zip ps vs
        |> List.fold
            (fun acc (subP, subV) -> acc |> Option.bind (fun bs -> tryBind subP subV |> Option.map (fun b -> bs @ b)))
            (Some [])
    | PTuple _, v -> unreachable $"the checker rejects tuple patterns on {formatValue v}"
    | PCase(ctor, None), VUnion(case, None) -> if ctor = case then Some [] else None
    | PCase(ctor, Some argPat), VUnion(case, Some payload) -> if ctor = case then tryBind argPat payload else None
    | PCase _, VUnion _ -> None
    | PCase _, v -> unreachable $"the checker rejects constructor patterns on {formatValue v}"


// binder patterns are irrefutable by checking, so the bind always
// succeeds — the None arm is the standard checker-guarantee marker
let bindPattern (p: Pattern) (v: Value) : (string * Value) list =
    match tryBind p v with
    | Some bs -> bs
    | None -> unreachable $"the checker guarantees binder patterns match; got {formatValue v}"

let private wrapOpt (ty: Ty) (v: Value) : Value =
    match ty with
    | TNamed("Option", _) -> VUnion("Some", Some v)
    | _ -> v

// the sigil env slot evaluated to overlay pairs — inside the stream's
// delay, so Env.fromFile boundary errors keep raise-at-force semantics
let rec private overlayOf (env: Env) (cenvO: TypedExpr option) : (string * string) list =
    match cenvO with
    | None -> []
    | Some ce ->
        match eval env ce with
        | VSeq items ->
            items
            |> Seq.map (fun item ->
                match item with
                | VRecord(_, fields) ->
                    (match Map.tryFind "Name" fields, Map.tryFind "Value" fields with
                     | Some(VStr n), Some(VStr value) -> n, value
                     | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
                | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
            |> List.ofSeq
        | v -> unreachable $"the checker rejects a sigil env of {formatValue v}"

and eval (env: Env) (te: TypedExpr) : Value =
    match te.Kind with
    | TEInt n -> VInt(int64 n)
    | TEStr s -> VStr s
    | TEBool b -> VBool b
    | TEUnit -> VUnit
    | TEVar name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> unreachable $"the checker rejects unbound variable '{name}'"
    | TELet(name, value, body) -> eval (Map.add name (eval env value) env) body
    | TELetPat(pat, value, body) ->
        let bindings = bindPattern pat (eval env value)
        eval (bindings |> List.fold (fun m (n, v) -> Map.add n v m) env) body
    | TELambdaPat(pat, body) -> VClosurePat(pat, body, env)
    | TELambda(param, body) -> VClosure(param, body, env)
    | TEApp(fn, arg) -> apply (eval env fn) (eval env arg)
    | TEPipe(arg, { Kind = TECmd(prog, cargs, cenvO) }) ->
        let argv = cargs |> List.map (fun a -> scalarString "command argument" (eval env a))

        let stdin =
            match eval env arg with
            | VSeq items ->
                items
                |> Seq.map (fun v ->
                    match v with
                    | VStr s -> s
                    | v -> unreachable $"the checker rejects non-string stdin: {formatValue v}")
            | v -> unreachable $"the checker rejects piping {formatValue v} into a command"

        VSeq(
            Seq.delay (fun () -> Proc.linesWith (overlayOf env cenvO) (Proc.resolveProg prog) argv (Some stdin))
            |> Seq.map VStr
        )
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
    | TETuple items -> VTuple(items |> List.map (eval env))
    | TECmd(prog, args, cenvO) ->
        let argv = args |> List.map (fun a -> scalarString "command argument" (eval env a))

        VSeq(
            Seq.delay (fun () -> Proc.linesWith (overlayOf env cenvO) (Proc.resolveProg prog) argv None)
            |> Seq.map VStr
        )
    | TEInterp parts ->
        let sb = System.Text.StringBuilder()

        for p in parts do
            match p with
            | IStr s -> sb.Append s |> ignore
            | IExpr e -> sb.Append(scalarString "interpolation hole" (eval env e)) |> ignore

        VStr(sb.ToString())
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
            | [] -> unreachable $"the checker guarantees totality; no arm matched {formatValue v}"
            | (pat, guard, body) :: rest ->
                match tryBind pat v with
                | Some bindings ->
                    let armEnv = bindings |> List.fold (fun e (n, bv) -> Map.add n bv e) env

                    let guardPasses =
                        match guard with
                        | None -> true
                        | Some g ->
                            match eval armEnv g with
                            | VBool b -> b
                            | gv -> unreachable $"the checker rejects a non-bool guard: {formatValue gv}"

                    if guardPasses then eval armEnv body else tryArms rest
                | None -> tryArms rest

        tryArms arms
    | TEEnvLoad def ->
        // snapshot at force time; collect every problem, raise ONCE
        let problems = ResizeArray<string>()

        let fields =
            def.Fields
            |> List.map (fun (name, ty) ->
                let raw = System.Environment.GetEnvironmentVariable name

                let value =
                    match ty, raw with
                    | TNamed("Option", _), null -> VUnion("None", None)
                    | _, null ->
                        problems.Add $"{name} is missing"
                        VUnit
                    | (TStr | TNamed("Option", [ TStr ])), v -> wrapOpt ty (VStr v)
                    | (TInt | TNamed("Option", [ TInt ])), v ->
                        match System.Int64.TryParse v with
                        | true, n -> wrapOpt ty (VInt n)
                        | _ ->
                            problems.Add $"{name} is not an int ('{v}')"
                            VUnit
                    | (TBool | TNamed("Option", [ TBool ])), v ->
                        match v with
                        | "true" -> wrapOpt ty (VBool true)
                        | "false" -> wrapOpt ty (VBool false)
                        | _ ->
                            problems.Add $"{name} is not a bool ('{v}'; exactly true or false)"
                            VUnit
                    | _ -> unreachable "the checker rejects non-scalar Env.load fields"

                name, value)

        if problems.Count > 0 then
            failwith ($"Env.load {def.Name}: " + String.concat "; " problems)

        VRecord(def.Name, Map.ofList fields)
    | TESeq(a, b) ->
        eval env a |> ignore
        eval env b
    | TEIf(cond, thn, els) ->
        match eval env cond, els with
        | VBool true, _ -> eval env thn
        | VBool false, Some e -> eval env e
        | VBool false, None -> VUnit
        | v, _ -> unreachable $"the checker rejects a non-bool condition: {formatValue v}"

and apply (fn: Value) (arg: Value) : Value =
    match fn with
    | VClosure(param, body, closureEnv) -> eval (Map.add param arg closureEnv) body
    | VClosurePat(pat, body, closureEnv) ->
        let bindings = bindPattern pat arg
        eval (bindings |> List.fold (fun m (n, v) -> Map.add n v m) closureEnv) body
    | VBuiltin f -> f arg
    | v -> unreachable $"the checker rejects application of {formatValue v}"

let constructorValues (cases: (string * Ty option) list) : (string * Value) list =
    cases
    |> List.map (fun (case, payload) ->
        match payload with
        | None -> case, VUnion(case, None)
        | Some _ -> case, VBuiltin(fun v -> VUnion(case, Some v)))
