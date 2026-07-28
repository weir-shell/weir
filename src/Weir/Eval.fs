module Weir.Eval

open Weir.Types
open Weir.Ast
open Weir.Argv
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

// One renderer, limits threaded [D:repl-echo]: show keeps its shipped
// constants byte-identical (20 items, "; ...", unclipped strings); the
// REPL echo runs the same core tighter (10, "; …", 120-char clip,
// depth bound). Forcing is bounded at MaxItems+1 per level either way.
type private RenderLimits =
    { MaxItems: int
      MaxStr: int option
      MaxDepth: int
      Ellipsis: string }

let private showLimits =
    { MaxItems = 20
      MaxStr = None
      MaxDepth = System.Int32.MaxValue
      Ellipsis = "; ..." }

let private echoLimits =
    { MaxItems = 10
      MaxStr = Some 120
      MaxDepth = 8
      Ellipsis = "; …" }

let rec private formatWith (lim: RenderLimits) (depth: int) (v: Value) : string =
    if depth > lim.MaxDepth then
        "…"
    else
        let sub = formatWith lim (depth + 1)

        match v with
        | VInt n -> string n
        | VStr s ->
            let raw, clipped =
                match lim.MaxStr with
                | Some m when s.Length > m -> s.Substring(0, m), true
                | _ -> s, false

            let escaped =
                raw.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t")

            let tail = if clipped then "…" else ""
            $"\"{escaped}{tail}\""
        | VBool true -> "true"
        | VBool false -> "false"
        | VRecord(_, fields) ->
            let body =
                fields |> Seq.map (fun kv -> $"{kv.Key} = {sub kv.Value}") |> String.concat "; "

            "{ " + body + " }"
        | VUnion(case, None) -> case
        | VUnion(case, Some payload) ->
            let inner = sub payload

            match payload with
            | VInt _
            | VStr _
            | VBool _ -> $"{case} {inner}"
            | _ -> $"{case} ({inner})"
        | VSeq items ->
            let shown = items |> Seq.truncate (lim.MaxItems + 1) |> List.ofSeq

            let body = shown |> List.truncate lim.MaxItems |> List.map sub |> String.concat "; "

            let ellipsis = if shown.Length > lim.MaxItems then lim.Ellipsis else ""
            $"[{body}{ellipsis}]"
        | VClosure _ -> "<fun>"
        | VClosurePat _ -> "<fun>"
        | VBuiltin _ -> "<builtin>"
        | VUnit -> "()"
        | VTuple items -> "(" + (items |> List.map sub |> String.concat ", ") + ")"

let formatValue (v: Value) : string = formatWith showLimits 0 v

// The REPL/-e echo [D:repl-echo]: bounded render + the way-out hint.
// The count shows only when already known (a materialized list) —
// counting a lazy seq would force it.
let echoValue (v: Value) : string * string option =
    match v with
    | VSeq items ->
        // ONE forcing pass — the echo must not enumerate its source
        // twice: materialize limit+1, render and hint from that list
        let shown = items |> Seq.truncate (echoLimits.MaxItems + 1) |> List.ofSeq
        let rendered = formatWith echoLimits 0 (VSeq(shown :> seq<Value>))

        // the counts phrase only — the SPELLING is composed at the echo
        // sites, which know the element type (pipe-to-print is a lie for
        // record seqs; the hint must name a spelling that types)
        let hint =
            if shown.Length > echoLimits.MaxItems then
                let count =
                    match items with
                    | :? (Value list) as l -> string (List.length l)
                    | :? System.Collections.Generic.ICollection<Value> as c -> string c.Count
                    | _ -> "?"

                Some $"{echoLimits.MaxItems} of {count} shown"
            else
                None

        rendered, hint
    | _ -> formatWith echoLimits 0 v, None

// the way-out spelling per element type [D:repl-echo]: the hint names
// a spelling that TYPES — print takes seq<string> only
let echoSpelling (elemIsString: bool) : string =
    if elemIsString then
        "pipe to print for all"
    else
        "pipe to Seq.map show |> print for all"

// the clipped-echo tail (" (N of M — spelling)") — one spelling for the
// three echo consumers (REPL let/expr arms, -e)
let echoTail (elemIsString: bool) (hint: string option) : string =
    match hint with
    | Some counts -> $" ({counts} — {echoSpelling elemIsString})"
    | None -> ""

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
            [ "status", VStr(string x + string y)
              "staged", VBool(x <> ' ' && x <> '?')
              "unstaged", VBool(y <> ' ')
              "path", VStr path ]
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
    | PRegex(pat, _, _, binder), VStr s ->
        // the cached instance from check time [D:regex-pattern]; group
        // i binds leaf i (an unmatched optional group binds "")
        (match compileRegex pat with
         | Error msg -> unreachable $"the checker rejects invalid regex literals: {msg}"
         | Ok rx ->
             let m = rx.Match s

             if not m.Success then
                 None
             else
                 let group (i: int) = VStr m.Groups[i].Value

                 match binder.PKind with
                 | PUnit
                 | PWildcard -> Some []
                 | PVar n -> Some [ n, group 1 ]
                 | PTuple ps ->
                     ps
                     |> List.mapi (fun i sp -> sp, group (i + 1))
                     |> List.choose (fun (sp, v) ->
                         match sp.PKind with
                         | PVar n -> Some(n, v)
                         | _ -> None)
                     |> Some
                 | _ -> unreachable "the checker constrains Regex binders to unit/name/tuple")
    | PRegex _, v -> unreachable $"the checker rejects Regex patterns on {formatValue v}"
    // seq patterns [D:seq-patterns]: probes pull from the match-site
    // cache (see TEMatch) — bounded force, memoize-once
    | PSeqNil, VSeq items -> if Seq.isEmpty items then Some [] else None
    | PCons(h, t), VSeq items ->
        (match items |> Seq.truncate 1 |> List.ofSeq with
         | [ first ] ->
             tryBind h first
             |> Option.bind (fun hb -> tryBind t (VSeq(items |> Seq.skip 1)) |> Option.map (fun tb -> hb @ tb))
         | _ -> None)
    | PSeqList ps, VSeq items ->
        let probe = items |> Seq.truncate (List.length ps + 1) |> List.ofSeq

        if List.length probe <> List.length ps then
            None
        else
            List.zip ps probe
            |> List.fold
                (fun acc (p, v) -> acc |> Option.bind (fun bs -> tryBind p v |> Option.map (fun b -> bs @ b)))
                (Some [])
    | (PSeqNil | PCons _ | PSeqList _), v -> unreachable $"the checker rejects seq patterns on {formatValue v}"


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

// ---- Args.load [D:typed-argv] ------------------------------------
// collect-then-raise over Session.ScriptArgs; --help short-circuits
// BEFORE validation (help must work on invalid invocations)

let private argvValueSlot (ty: Ty) : string =
    match ty with
    | TInt
    | TNamed("Option", [ TInt ]) -> " <int>"
    | TStr
    | TNamed("Option", [ TStr ]) -> " <string>"
    | _ -> ""

let private argvUsageLinesWith (flagShorts: Map<string, string>) (def: RecordDef) : string list =
    def.Fields
    |> List.map (fun (f, ty) ->
        let flag = "--" + Argv.kebabFlag f

        let short =
            match Map.tryFind flag flagShorts with
            | Some sh -> $"-{sh}, "
            | None -> "    "

        let left = $"  {short}{flag}{argvValueSlot ty}"

        let need =
            match ty, Argv.defaultOf def f with
            | TBool, Some(ABool true) -> $"default: on — --no-{Argv.kebabFlag f} disables"
            | TBool, _ -> ""
            | TNamed("Option", _), _ -> "optional"
            | _, Some(AStr s) -> $"default: {s}"
            | _, Some(AInt n) -> $"default: {n}"
            | _, _ -> "required"

        let right =
            [ need
              match Argv.docOf def f with
              | Some d -> d
              | None -> "" ]
            |> List.filter (fun s -> s <> "")
            |> String.concat " — "

        if right = "" then left else sprintf "%-30s%s" left right)

let private argvUsageLines (def: RecordDef) : string list =
    argvUsageLinesWith (fst (Argv.shortTables def)) def

// the per-case flag scope [D:shared-flags]: shared and payload fields
// together — short derivation runs over the UNION, so a cross-tier
// contest (-q for --quiet and --query) derives for NEITHER in that scope
let private scopeDef (sharedDef: RecordDef) (payloadDef: RecordDef option) : RecordDef =
    match payloadDef with
    | Some pd ->
        { sharedDef with
            Fields = sharedDef.Fields @ pd.Fields
            Attrs = pd.Attrs |> Map.fold (fun m k v -> Map.add k v m) sharedDef.Attrs
            // the two-tier help draws --help text from BOTH tiers [D:doc-help]
            Docs = pd.Docs |> Map.fold (fun m k v -> Map.add k v m) sharedDef.Docs }
    | None -> sharedDef

// pass 1 of the shared-flags scan: shared flags float, the FIRST
// non-flag token anchors as the case selector (an unknown flag consumes
// no value — the standing precedent)
let private argvFindCase (sharedDef: RecordDef) (argv: string list) : (int * string) option =
    let sharedLong =
        sharedDef.Fields
        |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, ty)
        |> Map.ofList

    let _, sharedShorts = Argv.shortTables sharedDef

    let flagTy (t: string) =
        if t.StartsWith "--" then
            Map.tryFind t sharedLong
        elif t.StartsWith "-" && t.Length = 2 then
            match Map.tryFind (t.Substring 1) sharedShorts with
            | Some(ShortOf flag) -> Map.tryFind flag sharedLong
            | _ -> None
        else
            None

    let rec go i (ts: string list) =
        match ts with
        | [] -> None
        | t :: rest ->
            match flagTy t with
            | Some ty when ty <> TBool ->
                (match rest with
                 | _ :: r -> go (i + 2) r
                 | [] -> None)
            | Some _ -> go (i + 1) rest
            | None when t.StartsWith "-" -> go (i + 1) rest
            | None -> Some(i, t)

    go 0 argv

let private argvUsage (target: ArgsTarget) (argv: string list) : string =
    match target with
    | ArgsRecord def -> String.concat "\n" ("usage: [flags]" :: argvUsageLines def)
    | ArgsUnion(udef, payloads) ->
        let caseLines = udef.Cases |> List.map (fun (c, _) -> "  " + c.ToLowerInvariant())

        let blocks =
            udef.Cases
            |> List.collect (fun (c, _) ->
                match Map.tryFind c payloads with
                | Some rdef when not rdef.Fields.IsEmpty -> $"{c.ToLowerInvariant()} flags:" :: argvUsageLines rdef
                | _ -> [])

        String.concat "\n" ([ "usage: <command> [flags]"; "commands:" ] @ caseLines @ blocks)
    | ArgsShared(outer, uf, udef, payloads) ->
        let sharedDef = Argv.sharedOf outer uf

        let payloadOf c (p: Ty option) =
            if p.IsSome then Map.tryFind c payloads else None

        let scopeShortsFor c p =
            fst (Argv.shortTables (scopeDef sharedDef (payloadOf c p)))

        let caseBlock c p =
            match payloadOf c p with
            | Some rdef when not rdef.Fields.IsEmpty ->
                $"{c.ToLowerInvariant()} flags:" :: argvUsageLinesWith (scopeShortsFor c p) rdef
            | _ -> []

        // case-scoped help when a case token rides along [D:shared-flags]
        let scoped =
            argvFindCase sharedDef (argv |> List.filter (fun t -> t <> "--help" && t <> "-h"))
            |> Option.bind (fun (_, tok) -> udef.Cases |> List.tryFind (fun (c, _) -> c.ToLowerInvariant() = tok))

        match scoped with
        | Some(c, p) ->
            String.concat
                "\n"
                ([ $"usage: {c.ToLowerInvariant()} [flags]"; "global options:" ]
                 @ argvUsageLinesWith (scopeShortsFor c p) sharedDef
                 @ caseBlock c p)
        | None ->
            // the global section shows a derived short only when it holds
            // in EVERY case scope (explicit shorts always hold)
            let sharedOwn, _ = Argv.shortTables sharedDef

            let stable =
                sharedOwn
                |> Map.filter (fun flag letter ->
                    udef.Cases
                    |> List.forall (fun (c, p) ->
                        let _, scopeIdx = Argv.shortTables (scopeDef sharedDef (payloadOf c p))

                        match Map.tryFind letter scopeIdx with
                        | Some(ShortOf f) -> f = flag
                        | _ -> false))

            let caseLines = udef.Cases |> List.map (fun (c, _) -> "  " + c.ToLowerInvariant())

            let blocks = udef.Cases |> List.collect (fun (c, p) -> caseBlock c p)

            String.concat
                "\n"
                ([ "usage: [global flags] <command> [flags]"; "global options:" ]
                 @ argvUsageLinesWith stable sharedDef
                 @ [ "commands:" ]
                 @ caseLines
                 @ blocks)

// the three argv-boundary rules — ONE implementation each, shared by
// the record and shared-flags twins [D:argv-rules]. The accumulators
// arrive as PARAMETERS, never closure captures: problem ORDER stays
// each caller's own (scan order, then declaration-order fills —
// pinned exact in e2e).

// the resting-point fill [D:default-attr]: run-time Value construction
// lives HERE (Eval); the Default POLICY it consumes (Argv.defaultOf)
// is check-time schema, already shared in Argv.fs beside the Args/Env
// flip — the check/run line is unchanged by the unification
let private argvFill
    (problems: ResizeArray<string>)
    (def: RecordDef)
    (values: System.Collections.Generic.Dictionary<string, Value>)
    : (string * Value) list =
    def.Fields
    |> List.map (fun (f, ty) ->
        match values.TryGetValue f with
        | true, v -> f, v
        | false, _ ->
            match Argv.defaultOf def f with
            | Some(AStr s) -> f, VStr s
            | Some(AInt n) -> f, VInt n
            | Some(ABool b) -> f, VBool b
            | None ->
                match ty with
                | TBool -> f, VBool false
                | TNamed("Option", _) -> f, VUnion("None", None)
                | _ ->
                    problems.Add $"missing required flag '--{Argv.kebabFlag f}'"
                    f, VUnit)

// repeats of one spelling stay the given-twice error; opposite
// polarities name both spellings
let private argvDup
    (problems: ResizeArray<string>)
    (seen: System.Collections.Generic.HashSet<string>)
    (polarity: System.Collections.Generic.Dictionary<string, bool>)
    (f: string)
    (neg: bool)
    (flagTok: string)
    =
    if not (seen.Add f) then
        let prior =
            (match polarity.TryGetValue f with
             | true, p -> p
             | _ -> false)

        if prior <> neg then
            problems.Add $"'--{Argv.kebabFlag f}' and '--no-{Argv.kebabFlag f}' are both given"
        else
            problems.Add $"'{flagTok}' is given twice"

    polarity[f] <- neg

let private argvParseValue
    (problems: ResizeArray<string>)
    (values: System.Collections.Generic.Dictionary<string, Value>)
    (f: string)
    (ty: Ty)
    (flagTok: string)
    (raw: string)
    =
    match ty with
    | TInt
    | TNamed("Option", [ TInt ]) ->
        match System.Int64.TryParse raw with
        | true, n -> values[f] <- wrapOpt ty (VInt n)
        | _ -> problems.Add $"{flagTok} is not an int ('{raw}')"
    | _ -> values[f] <- wrapOpt ty (VStr raw)

let private argvParseRecord (label: string) (def: RecordDef) (tokens: string list) : Value =
    // minted --no-X twins ride the index as negative entries
    // [D:default-attr]; they join did-you-mean via the same list
    let flagged =
        (def.Fields |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, (f, ty, false)))
        @ (Argv.mintedFlags def |> List.map (fun (f, m) -> "--" + m, (f, TBool, true)))

    let longIndex = Map.ofList flagged
    let _, shortIndex = Argv.shortTables def
    let problems = ResizeArray<string>()
    let values = System.Collections.Generic.Dictionary<string, Value>()
    let seen = System.Collections.Generic.HashSet<string>()
    let polarity = System.Collections.Generic.Dictionary<string, bool>()

    // repeats of one spelling stay the given-twice error; opposite
    // polarities name both spellings
    let dup = argvDup problems seen polarity

    let parseValue = argvParseValue problems values

    let rec go tokens =
        match tokens with
        | [] -> ()
        | (t: string) :: rest when t.StartsWith "--" ->
            match Map.tryFind t longIndex with
            | Some(f, ty, neg) -> consume f ty neg t rest
            | None ->
                problems.Add $"unknown flag '{t}'{didYouMean t (flagged |> List.map fst)}"
                go rest
        | t :: rest when t.StartsWith "-" && t.Length = 2 ->
            match Map.tryFind (t.Substring 1) shortIndex with
            | Some(ShortOf flag) ->
                let f, ty, neg = Map.find flag longIndex
                consume f ty neg t rest
            | Some(AmbiguousShort candidates) ->
                problems.Add $"""'{t}' is ambiguous: {String.concat ", " candidates}"""
                go rest
            | None ->
                problems.Add $"unknown flag '{t}'"
                go rest
        | t :: rest ->
            problems.Add $"unexpected argument '{t}'"
            go rest

    and consume f ty neg flagTok rest =
        dup f neg flagTok

        match ty with
        | TBool ->
            values[f] <- VBool(not neg)
            go rest
        | _ ->
            match rest with
            | raw :: rest' ->
                parseValue f ty flagTok raw
                go rest'
            | [] -> problems.Add $"flag '{flagTok}' needs a value"

    go tokens

    let fields = argvFill problems def values

    if problems.Count > 0 then
        failwith ($"{label}: " + String.concat "; " problems)

    VRecord(def.Name, Map.ofList fields)

// the shared-flags load [D:shared-flags]: shared flags float anywhere on
// the line; the first non-flag token anchors the case; payload flags
// bind only AFTER it. Both tiers collect into ONE boundary error.
let private argvLoadShared
    (outer: RecordDef)
    (unionField: string)
    (udef: UnionDef)
    (payloads: Map<string, RecordDef>)
    (argv: string list)
    : Value =
    let label = $"Args.load {outer.Name}"
    let sharedDef = Argv.sharedOf outer unionField

    let sharedLong =
        (sharedDef.Fields
         |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, (f, ty, false)))
        @ (Argv.mintedFlags sharedDef
           |> List.map (fun (f, m) -> "--" + m, (f, TBool, true)))
        |> Map.ofList

    let caseTable = udef.Cases |> List.map (fun (c, p) -> c.ToLowerInvariant(), (c, p))
    let caseAt = argvFindCase sharedDef argv

    let selected =
        caseAt
        |> Option.bind (fun (_, tok) -> caseTable |> List.tryFind (fun (w, _) -> w = tok))
        |> Option.map snd

    let payloadDef =
        selected
        |> Option.bind (fun (c, p) -> if p.IsSome then Map.tryFind c payloads else None)

    let payloadLong =
        payloadDef
        |> Option.map (fun pd ->
            (pd.Fields |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, (f, ty, false)))
            @ (Argv.mintedFlags pd |> List.map (fun (f, m) -> "--" + m, (f, TBool, true)))
            |> Map.ofList)
        |> Option.defaultValue Map.empty

    let _, scopeShorts = Argv.shortTables (scopeDef sharedDef payloadDef)

    let caseIdx = caseAt |> Option.map fst |> Option.defaultValue System.Int32.MaxValue

    // tier-aware did-you-mean: before the case token, shared flags and
    // case names; after it, shared plus the selected payload's flags
    let beforeCandidates =
        (sharedLong |> Map.toList |> List.map fst) @ (caseTable |> List.map fst)

    let afterCandidates =
        (sharedLong |> Map.toList |> List.map fst)
        @ (payloadLong |> Map.toList |> List.map fst)

    let problems = ResizeArray<string>()
    let sharedValues = System.Collections.Generic.Dictionary<string, Value>()
    let payloadValues = System.Collections.Generic.Dictionary<string, Value>()
    let seen = System.Collections.Generic.HashSet<string>()
    let polarity = System.Collections.Generic.Dictionary<string, bool>()

    let dup = argvDup problems seen polarity

    let parseValue = argvParseValue problems

    let rec go i (ts: string list) =
        match ts with
        | [] -> ()
        | _ :: rest when i = caseIdx -> go (i + 1) rest
        | t :: rest ->
            let resolved =
                if t.StartsWith "--" then
                    match Map.tryFind t sharedLong with
                    | Some(f, ty, neg) -> Choice1Of3(sharedValues, f, ty, neg)
                    | None ->
                        match Map.tryFind t payloadLong with
                        | Some(f, ty, neg) when i > caseIdx -> Choice1Of3(payloadValues, f, ty, neg)
                        | _ -> Choice2Of3()
                elif t.StartsWith "-" && t.Length = 2 then
                    match Map.tryFind (t.Substring 1) scopeShorts with
                    | Some(ShortOf flag) ->
                        (match Map.tryFind flag sharedLong with
                         | Some(f, ty, neg) -> Choice1Of3(sharedValues, f, ty, neg)
                         | None ->
                             match Map.tryFind flag payloadLong with
                             | Some(f, ty, neg) when i > caseIdx -> Choice1Of3(payloadValues, f, ty, neg)
                             | _ -> Choice2Of3())
                    | Some(AmbiguousShort candidates) ->
                        problems.Add $"""'{t}' is ambiguous: {String.concat ", " candidates}"""
                        Choice3Of3()
                    | None -> Choice2Of3()
                else
                    problems.Add $"unexpected argument '{t}'"
                    Choice3Of3()

            match resolved with
            | Choice1Of3(values, f, ty, neg) ->
                dup f neg t

                (match ty with
                 | TBool ->
                     values[f] <- VBool(not neg)
                     go (i + 1) rest
                 | _ ->
                     match rest with
                     | raw :: rest' ->
                         parseValue values f ty t raw
                         go (i + 2) rest'
                     | [] -> problems.Add $"flag '{t}' needs a value")
            | Choice2Of3() ->
                let cands = if i < caseIdx then beforeCandidates else afterCandidates
                problems.Add $"unknown flag '{t}'{didYouMean t cands}"
                go (i + 1) rest
            | Choice3Of3() -> go (i + 1) rest

    go 0 argv

    (match caseAt, selected with
     | None, _ -> problems.Add("missing subcommand; one of: " + String.concat ", " (caseTable |> List.map fst))
     | Some(_, tok), None -> problems.Add $"unknown subcommand '{tok}'{didYouMean tok (caseTable |> List.map fst)}"
     | Some _, Some _ -> ())

    let collectFields def values = argvFill problems def values

    let sharedFields = collectFields sharedDef sharedValues

    let payloadValue =
        match selected with
        | Some(c, Some _) ->
            let pd = Map.find c payloads
            Some(c, Some(VRecord(pd.Name, Map.ofList (collectFields pd payloadValues))))
        | Some(c, None) -> Some(c, None)
        | None -> None

    if problems.Count > 0 then
        failwith ($"{label}: " + String.concat "; " problems)

    let case, payload =
        match payloadValue with
        | Some(c, p) -> c, p
        | None -> failwith $"{label}: internal — no case after validation"

    VRecord(outer.Name, Map.ofList ((unionField, VUnion(case, payload)) :: sharedFields))

let private argvLoad (target: ArgsTarget) : Value =
    let argv = Session.ScriptArgs

    if List.contains "--help" argv || List.contains "-h" argv then
        printfn "%s" (argvUsage target argv)
        raise (ExitRequest 0)

    match target with
    | ArgsRecord def -> argvParseRecord $"Args.load {def.Name}" def argv
    | ArgsShared(outer, uf, udef, payloads) -> argvLoadShared outer uf udef payloads argv
    | ArgsUnion(udef, payloads) ->
        let table = udef.Cases |> List.map (fun (c, p) -> c.ToLowerInvariant(), (c, p))

        match argv with
        | [] ->
            failwith (
                $"Args.load {udef.Name}: missing subcommand; one of: "
                + String.concat ", " (table |> List.map fst)
            )
        | tok :: rest ->
            match table |> List.tryFind (fun (w, _) -> w = tok) with
            | Some(_, (c, None)) ->
                match rest with
                | [] -> VUnion(c, None)
                | extra ->
                    failwith (
                        $"Args.load {udef.Name} {tok}: "
                        + String.concat "; " (extra |> List.map (fun t -> $"unexpected argument '{t}'"))
                    )
            | Some(_, (c, Some _)) ->
                let payload =
                    argvParseRecord $"Args.load {udef.Name} {tok}" (Map.find c payloads) rest

                VUnion(c, Some payload)
            | None ->
                failwith $"Args.load {udef.Name}: unknown subcommand '{tok}'{didYouMean tok (table |> List.map fst)}"

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
                    (match Map.tryFind "name" fields, Map.tryFind "value" fields with
                     | Some(VStr n), Some(VStr value) -> n, value
                     | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
                | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
            |> List.ofSeq
        | v -> unreachable $"the checker rejects a sigil env of {formatValue v}"

// spawn-argv assembly [D:argv-splat]: a splat enumerates ONCE at
// spawn (argv is finite — the splat forces by necessity), order
// preserved, each element ONE word
and argvOf (env: Env) (args: Check.TypedExpr list) : string list =
    args
    |> List.collect (fun a ->
        match a.Kind with
        | Check.TESplat inner ->
            match eval env inner with
            | VSeq items -> items |> Seq.map (scalarString "splat element") |> List.ofSeq
            | v -> unreachable $"the checker rejects '$@' on {formatValue v}"
        | _ -> [ scalarString "command argument" (eval env a) ])

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
    | TELet(name, _, value, body) -> eval (Map.add name (eval env value) env) body
    | TELetPat(pat, value, body) ->
        let bindings = bindPattern pat (eval env value)
        eval (bindings |> List.fold (fun m (n, v) -> Map.add n v m) env) body
    | TELambdaPat(pat, body) -> VClosurePat(pat, body, env)
    | TELambda(param, _, body) -> VClosure(param, body, env)
    | TEApp(fn, arg) -> apply (eval env fn) (eval env arg)
    | TEPipe(arg, { Kind = TECmd(prog, cargs, cenvO) }) ->
        let argv = argvOf env cargs

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
    // composition sits here, not in binOp: it needs `apply` (the
    // eval/apply knot) [D:composition-operators]
    | TEBinOp(">>", l, r) ->
        let f = eval env l
        let g = eval env r
        VBuiltin(fun x -> apply g (apply f x))
    | TEBinOp("<<", l, r) ->
        let g = eval env l
        let f = eval env r
        VBuiltin(fun x -> apply g (apply f x))
    | TEBinOp(op, l, r) -> binOp op (eval env l) (eval env r)
    | TERecord(name, fields) -> VRecord(name, fields |> List.map (fun (n, fv) -> n, eval env fv) |> Map.ofList)
    | TEUpdate(src, updates) ->
        // source evaluated ONCE [D:record-update]; nested paths overlay
        let source = eval env src

        updates
        |> List.fold
            (fun acc (path, tval) ->
                let rec go (v: Value) (path: string list) : Value =
                    match v, path with
                    | VRecord(n, fs), [ f ] -> VRecord(n, Map.add f (eval env tval) fs)
                    | VRecord(n, fs), f :: rest -> VRecord(n, Map.add f (go fs[f] rest) fs)
                    | v, _ -> unreachable $"the checker rejects update on {formatValue v}"

                go acc path)
            source
    | TEList items -> VSeq(items |> List.map (eval env))
    | TETuple items -> VTuple(items |> List.map (eval env))
    | TECmd(prog, args, cenvO) ->
        let argv = argvOf env args

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
        let v0 = eval env scrutinee

        // memoize-once law [D:seq-patterns]: a match containing ANY seq
        // pattern views its scrutinee through ONE cache — arms probe the
        // same buffer (never re-pull), rest binds the buffer suffix plus
        // the untouched tail, effects run once TOTAL
        let rec hasSeqPat (p: Weir.Ast.Pattern) =
            match p.PKind with
            | Weir.Ast.PSeqNil
            | Weir.Ast.PCons _
            | Weir.Ast.PSeqList _ -> true
            | Weir.Ast.PTuple ps -> ps |> List.exists hasSeqPat
            | Weir.Ast.PCase(_, Some a) -> hasSeqPat a
            | _ -> false

        let v =
            match v0 with
            | VSeq items when arms |> List.exists (fun (p, _, _) -> hasSeqPat p) -> VSeq(Seq.cache items)
            | _ -> v0

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
    | TEArgsLoad target -> argvLoad target
    | TEEnvLoad(def, enums) ->
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
                        // the resting point sits BELOW the whole overlay
                        // stack [D:default-attr]: any set var wins
                        match Argv.defaultOf def name with
                        | Some(AStr s) -> VStr s
                        | Some(AInt n) -> VInt n
                        | Some(ABool b) -> VBool b
                        | None ->
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
                    | (TNamed(un, []) | TNamed("Option", [ TNamed(un, []) ])), v ->
                        // the enum conversion [D:env-enums]: matching is
                        // CASE-INSENSITIVE against the declared names (env
                        // convention is uppercase — LOG_LEVEL=DEBUG, =debug
                        // and =Debug all select Debug); an empty value is a
                        // miss with candidates, the int rule's precedent
                        let cases = enums |> Map.tryFind un |> Option.defaultValue []

                        match
                            cases
                            |> List.tryFind (fun c ->
                                System.String.Equals(c, v, System.StringComparison.OrdinalIgnoreCase))
                        with
                        | Some c -> wrapOpt ty (VUnion(c, None))
                        | None ->
                            // the hint compares like the matcher does —
                            // case-insensitively — but names the DECLARED
                            // spelling
                            let hint =
                                cases
                                |> List.tryPick (fun c ->
                                    if didYouMean (v.ToLowerInvariant()) [ c.ToLowerInvariant() ] <> "" then
                                        Some $". Did you mean '{c}'?"
                                    else
                                        None)
                                |> Option.defaultValue ""

                            let listed = String.concat ", " cases
                            problems.Add $"{name} is not a {un} ('{v}'; expected one of: {listed}){hint}"
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
    // TESplat [D:argv-splat] lives only in TECmd argv, expanded by
    // argvOf; it never reaches value evaluation. Closes the match so a
    // stray splat is a clear internal error, not a MatchFailureException.
    | TESplat _ -> unreachable "$@ splat outside command arguments (checker confines it to argv)"

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
