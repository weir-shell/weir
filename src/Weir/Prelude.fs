module Weir.Prelude

open Weir.Ast
open Weir.Types

let source =
    [ "type Option<'a> = Some of 'a | None"
      "type Result<'a, 'e> = Ok of 'a | Error of 'e" ]

let extend (typeEnv: TypeEnv) (valueEnv: Eval.Env) : TypeEnv * Eval.Env =
    source
    |> List.fold
        (fun (te, ve) line ->
            match Parser.parseStmt line with
            | Result.Ok(SType decl) ->
                match Check.checkDecl te decl with
                | Result.Ok te' ->
                    let ve' =
                        match decl.Body with
                        | DUnion cases -> Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) ve
                        | DRecord _ -> ve

                    te', ve'
                | Result.Error terr -> failwith $"prelude: {Check.formatError terr}"
            | _ -> failwith $"prelude: expected a declaration: {line}")
        (typeEnv, valueEnv)
