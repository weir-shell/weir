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
