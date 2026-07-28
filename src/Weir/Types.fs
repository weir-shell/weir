module Weir.Types

// reifier desugar targets carry an un-typeable '|' prefix
// [D:drop-reify-builtins] — identifiers are [A-Za-z_].. so `| complete`
// resolves them while user code cannot name them. Suggestion/completion
// pools filter to user-typeable names.
let isUserName (n: string) =
    n.Length > 0 && (System.Char.IsLetter n[0] || n[0] = '_')

type Ty =
    | TInt
    | TStr
    | TBool
    | TUnit
    | TFun of domain: Ty * codomain: Ty
    | TSeq of element: Ty
    | TTuple of elements: Ty list // arity 2+ [D:tuples-reversal]
    | TNamed of name: string * args: Ty list
    | TVar of name: string
    | TRowVar of name: string * fields: (string * Ty) list

let rec formatTy (ty: Ty) : string =
    match ty with
    | TVar v -> $"'{v}"
    | TRowVar(_, []) -> "{ .. }"
    | TRowVar(_, fields) ->
        let fs =
            fields |> List.map (fun (f, t) -> $"{f}: {formatTy t}") |> String.concat "; "

        $"{{ {fs}; .. }}"
    | TInt -> "int"
    | TStr -> "string"
    | TBool -> "bool"
    | TUnit -> "unit"
    | TFun(domain, codomain) ->
        let dom =
            match domain with
            | TFun _ -> $"({formatTy domain})"
            | _ -> formatTy domain

        $"{dom} -> {formatTy codomain}"
    | TSeq element -> $"seq<{formatTy element}>"
    | TTuple elements ->
        let part (t: Ty) =
            match t with
            | TFun _
            | TTuple _ -> $"({formatTy t})"
            | _ -> formatTy t

        elements |> List.map part |> String.concat " * "
    | TNamed(name, []) -> name
    | TNamed(name, args) ->
        let argStr = args |> List.map formatTy |> String.concat ", "
        $"{name}<{argStr}>"

/// the annotated DECLARATION form for hover [D:annotated-signature]:
/// `name (p1: t1) (p2: t2) : result`, decomposing `ty` by the given
/// parameter names (the arrow tail beyond the named params is the
/// result). Valid F# declaration syntax — claims nothing false. Zero
/// names -> `name : ty`, no empty parens. The plain arrow `formatTy`
/// stays the fallback (unnamed values) and the truth for type errors.
let formatSignature (name: string) (paramNames: string list) (ty: Ty) : string =
    let rec split names t =
        match names, t with
        | n :: rest, TFun(dom, cod) ->
            let ps, result = split rest cod
            (n, dom) :: ps, result
        | _ -> [], t

    match split paramNames ty with
    | [], _ -> $"{name} : {formatTy ty}"
    | ps, result ->
        let rendered =
            ps |> List.map (fun (n, t) -> $"({n}: {formatTy t})") |> String.concat " "

        $"{name} {rendered} : {formatTy result}"

let rec tyVars (ty: Ty) : Set<string> =
    match ty with
    | TVar v -> Set.singleton v
    | TRowVar(r, fields) -> fields |> List.fold (fun acc (_, t) -> acc + tyVars t) (Set.singleton r)
    | TFun(a, b) -> tyVars a + tyVars b
    | TSeq t -> tyVars t
    | TTuple ts -> ts |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TNamed(_, args) -> args |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TInt
    | TStr
    | TBool
    | TUnit -> Set.empty

// The closed class family [D:inferred-type-classes] — fully erased
// after checking: a constraint never reaches the value domain.
[<RequireQualifiedAccess>]
type Cls =
    | Eq
    | Show
    | Ord

// Cs: constraints on quantified vars — `Eq a => a -> a -> bool` is
// { Forall = {a}; Cs = [a, {Eq}]; Ty = a -> a -> bool }.
type Scheme =
    { Forall: Set<string>
      Cs: Map<string, Set<Cls>>
      Ty: Ty
      // row-field PROVENANCE [D:row-provenance]: quantified row var ->
      // (field, physLine, physCol, len) of the access that demanded it —
      // translated to PHYSICAL at generalization (spans die at statement
      // boundaries), rehydrated at instantiation, reported by the
      // row-vs-record discharge
      RowOrigins: Map<string, (string * int * int * int) list> }

let generalize (ty: Ty) : Scheme =
    { Forall = tyVars ty
      Cs = Map.empty
      Ty = ty
      RowOrigins = Map.empty }

// generalization with the checker's constraint residue: only
// constraints on vars actually quantified ride into the scheme
let generalizeWith (cs: Map<string, Set<Cls>>) (ty: Ty) : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty
      RowOrigins = Map.empty }

// generalizeWith + row origins [D:row-provenance], filtered like Cs:
// only origins for quantified row vars ride into the scheme
let generalizeWithOrigins
    (cs: Map<string, Set<Cls>>)
    (origins: Map<string, (string * int * int * int) list>)
    (ty: Ty)
    : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty
      RowOrigins = origins |> Map.filter (fun v _ -> fa.Contains v) }

let mono (ty: Ty) : Scheme =
    { Forall = Set.empty
      Cs = Map.empty
      Ty = ty
      RowOrigins = Map.empty }

// attribute arguments [D:attributes]: literal-only, the splice family
type AttrArg =
    | AStr of string
    | AInt of int64
    | ABool of bool

type RecordDef =
    { Name: string
      Params: string list
      Fields: (string * Ty) list
      // check-time data, FULLY ERASED [D:attributes] — never reaches
      // eval, Value, show, json, or equatability
      Attrs: Map<string, (string * AttrArg option) list> }

type UnionDef =
    { Name: string
      Params: string list
      Cases: (string * Ty option) list }

type TypeDef =
    | Record of RecordDef
    | Union of UnionDef

type TypeEnv =
    { Values: Map<string, Scheme>
      Modules: Map<string, Map<string, Scheme>>
      Types: Map<string, TypeDef> }

let editDistance (a: string) (b: string) : int =
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

let didYouMean (name: string) (candidates: seq<string>) : string =
    candidates
    |> Seq.map (fun c -> c, editDistance name c)
    |> Seq.filter (fun (_, d) -> d <= 2)
    |> Seq.sortBy snd
    |> Seq.tryHead
    |> Option.map (fun (c, _) -> $". Did you mean '{c}'?")
    |> Option.defaultValue ""
