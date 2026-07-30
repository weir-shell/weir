module Weir.Prelude

open Weir.Ast
open Weir.Types

// Option is the only prelude type: weir's error model is exceptions
// (`fail`/partial builtins), exit codes, and `Completed` from command
// interaction — never a Result value. `Option` earns its place (the
// `try*` family returns one); a Result nothing produced or consumed was
// removed [D:no-result].
let source =
    [ "type Option<'a> = Some of 'a | None"
      // the YAML node union [D:yaml-v1] — declared in weir's OWN source
      // (the Option precedent), so constructors, Show, and the class laws
      // all fall out of existing machinery. Value-domain answers, probed:
      // Show renders the recursion; Eq REJECTS it by the existing no-seq
      // rule with its own teaching text (no new rule). YMap preserves KEY
      // ORDER (the user-controlled escape from record-field alphabetical
      // rendering); no float case — weir has no float scalar.
      "type Yaml = YStr of string | YInt of int | YBool of bool | YNull | YSeq of seq<Yaml> | YMap of seq<string * Yaml>" ]

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
