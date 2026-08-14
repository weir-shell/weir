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
      "type Yaml = YStr of string | YInt of int | YFloat of float | YBool of bool | YNull | YSeq of seq<Yaml> | YMap of seq<string * Yaml>"
      // the bounded-loop option records [D:retry-poll] — the types ARE
      // the reference: keys, shapes, and (via Retry.defaults /
      // Poll.defaults) the resting values
      "type Retry = { attempts: int; delay: Duration; timeout: Option<Duration> }"
      "type Poll = { timeout: Duration; interval: Duration }"
      // the typed request boundary [D:http] — field names are PUBLIC API
      "type HttpMethod = Get | Post | Put | Delete | Patch | Head | Options | Query"
      "type Auth = NoAuth | Bearer of Secret | Basic of string * Secret"
      "type HttpBody = NoBody | Json of seq<string> | Text of string"
      "type HttpRequest = { method: HttpMethod; url: string; auth: Auth; headers: seq<string * string>; secretHeaders: seq<string * Secret>; body: HttpBody; timeout: Duration; insecure: bool }"
      "type HttpResponse = { status: int; headers: seq<string * string>; body: seq<string> }" ]

let extend (typeEnv: TypeEnv) (valueEnv: Eval.Env) : TypeEnv * Eval.Env =
    Check.preludeLoading.Value <- true

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
    |> fun (te, ve) ->
        // register every type name present at prelude-close as BUILT-IN
        // [D:desugar-capture]: a later user declaration of one is a
        // located error, not a silent retype of the builtins behind it
        Check.preludeLoading.Value <- false

        for name in te.Types |> Map.keys do
            Check.builtinTypeNames.TryAdd(name, 0uy) |> ignore

        // def-LESS builtins: type constructors with no Record/Union
        // entry — Map [D:map-string] and the Proc handle
        // [D:scoped-procs]. Without this a user `type Proc = …` would
        // silently retype every scoped-process binder behind it.
        for name in [ "Map"; "Proc" ] do
            Check.builtinTypeNames.TryAdd(name, 0uy) |> ignore

        te, ve
