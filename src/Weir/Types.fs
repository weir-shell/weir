module Weir.Types

type Ty =
    | TInt
    | TStr
    | TBool
    | TUnit
    | TFun of domain: Ty * codomain: Ty
    | TSeq of element: Ty
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
    | TNamed(name, []) -> name
    | TNamed(name, args) ->
        let argStr = args |> List.map formatTy |> String.concat ", "
        $"{name}<{argStr}>"

let rec tyVars (ty: Ty) : Set<string> =
    match ty with
    | TVar v -> Set.singleton v
    | TRowVar(r, fields) -> fields |> List.fold (fun acc (_, t) -> acc + tyVars t) (Set.singleton r)
    | TFun(a, b) -> tyVars a + tyVars b
    | TSeq t -> tyVars t
    | TNamed(_, args) -> args |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TInt
    | TStr
    | TBool
    | TUnit -> Set.empty

// The closed class family (2026-07-20, PLAN-type-classes Session A).
// Compiler-owned, structural, no user instances; fully erased after
// checking — a constraint never reaches the value domain.
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
      Ty: Ty }

let generalize (ty: Ty) : Scheme =
    { Forall = tyVars ty
      Cs = Map.empty
      Ty = ty }

// generalization with the checker's constraint residue: only
// constraints on vars actually quantified ride into the scheme
let generalizeWith (cs: Map<string, Set<Cls>>) (ty: Ty) : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty }

let mono (ty: Ty) : Scheme =
    { Forall = Set.empty
      Cs = Map.empty
      Ty = ty }

type RecordDef =
    { Name: string
      Params: string list
      Fields: (string * Ty) list }

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
