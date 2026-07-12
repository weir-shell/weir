module Weir.Types

type Ty =
    | TInt of measure: string option
    | TStr
    | TBool
    | TFun of domain: Ty * codomain: Ty
    | TSeq of element: Ty
    | TNamed of name: string
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
    | TInt None -> "int"
    | TInt(Some m) -> $"int<{m}>"
    | TStr -> "string"
    | TBool -> "bool"
    | TFun(domain, codomain) ->
        let dom =
            match domain with
            | TFun _ -> $"({formatTy domain})"
            | _ -> formatTy domain

        $"{dom} -> {formatTy codomain}"
    | TSeq element -> $"seq<{formatTy element}>"
    | TNamed name -> name

let rec tyVars (ty: Ty) : Set<string> =
    match ty with
    | TVar v -> Set.singleton v
    | TRowVar(r, fields) -> fields |> List.fold (fun acc (_, t) -> acc + tyVars t) (Set.singleton r)
    | TFun(a, b) -> tyVars a + tyVars b
    | TSeq t -> tyVars t
    | TInt _
    | TStr
    | TBool
    | TNamed _ -> Set.empty

type Scheme = { Forall: Set<string>; Ty: Ty }

let generalize (ty: Ty) : Scheme = { Forall = tyVars ty; Ty = ty }

let mono (ty: Ty) : Scheme = { Forall = Set.empty; Ty = ty }

type RecordDef =
    { Name: string
      Fields: (string * Ty) list }

type UnionDef =
    { Name: string
      Cases: (string * Ty option) list }

type TypeDef =
    | Record of RecordDef
    | Union of UnionDef

type TypeEnv =
    { Values: Map<string, Scheme>
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
