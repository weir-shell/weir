module FsLite.Types

type Ty =
    | TInt of measure: string option
    | TStr
    | TBool
    | TFun of domain: Ty * codomain: Ty
    | TSeq of element: Ty
    | TRecord of name: string

let rec formatTy (ty: Ty) : string =
    match ty with
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
    | TRecord name -> name

type RecordDef =
    { Name: string
      Fields: (string * Ty) list }

type TypeEnv =
    { Values: Map<string, Ty>
      Types: Map<string, RecordDef> }
