module Weir.Prelude

open Weir.Ast
open Weir.Types

// Option is the only prelude type: weir's error model is exceptions
// (`fail`/partial builtins), exit codes, and `Completed` from command
// interaction — never a Result value. `Option` earns its place (the
// `try*` family returns one); a Result type nothing would produce or
// consume has no place [D:no-result].
let source =
    [ "type Option<'a> = Some of 'a | None"
      // the YAML node union [D:yaml-v1] — declared in weir's own source
      // (as Option is), so constructors, Show, and the class laws
      // all fall out of existing machinery. Value-domain behavior:
      // Show renders the recursion; Eq rejects it by the existing no-seq
      // rule with its own teaching text (no new rule). YMap preserves key
      // order (the user-controlled escape from record-field alphabetical
      // rendering).
      "type Yaml = YStr of string | YInt of int | YFloat of float | YBool of bool | YNull | YSeq of seq<Yaml> | YMap of seq<string * Yaml>"
      // the bounded-loop option records [D:retry-poll] — the types are
      // the reference: keys, shapes, and (via Retry.defaults /
      // Poll.defaults) the resting values
      "type Retry = { attempts: int; delay: Duration; timeout: Option<Duration> }"
      "type Poll = { timeout: Duration; interval: Duration }"
      // the typed request boundary [D:http] — field names are public API
      // Other carries a well-formed but unlisted verb [D:serve-method]:
      // a proxy may forward TRACE/QUERY-shaped tokens the union does not
      // name, so the server reads it as `Other v` (route on it, answer 405
      // by handler choice) rather than misreporting it as Get. The client
      // never constructs Other — it is an inbound-only carrier.
      "type HttpMethod = Get | Post | Put | Delete | Patch | Head | Options | Query | Other of string"
      "type Auth = NoAuth | Bearer of Secret | Basic of string * Secret"
      // the shared body union [D:http] [D:http-serve]: NoBody/Json/Text
      // are the client-and-server cases; Stream is the server response's
      // lazy line source — pulled and written chunked as produced
      // (SSE-shaped), the pattern print set for streaming, server-side. On
      // the client send path a Stream body materializes (request-body
      // streaming is out of scope v1).
      "type HttpBody = NoBody | Json of seq<string> | Text of string | Stream of seq<string>"
      "type HttpRequest = { method: HttpMethod; url: string; auth: Auth; headers: seq<string * string>; secretHeaders: seq<string * Secret>; body: HttpBody; timeout: Duration; insecure: bool }"
      "type HttpResponse = { status: int; headers: seq<string * string>; body: seq<string> }"
      // the server boundary [D:http-serve] — the ring protocol study's
      // minimal surface. One family: HttpMethod + header pairs + the body
      // union are shared with the client; the server records differ where
      // the wire differs (a path+query, not a url; no auth/timeout — those
      // are client concerns). maxConcurrent is the handler ceiling, the
      // pmapWith concurrency-law on the scope.
      // bodyTimeout bounds the request-body read [D:serve-body-timeout]:
      // a slow client dribbling its body cannot park a handler slot
      // unboundedly. Optional in the config literal — omit it and it rests
      // at 30s (the serve config accepts `{ port; maxConcurrent }` and
      // `{ port; maxConcurrent; bodyTimeout }` both); exhaustion refuses
      // the request (408), bounded.
      "type ServerConfig = { port: int; maxConcurrent: int; bodyTimeout: Duration }"
      "type HttpServerRequest = { method: HttpMethod; path: string; query: string; headers: seq<string * string>; body: string }"
      "type HttpServerResponse = { status: int; headers: seq<string * string>; body: HttpBody }"
      // the plan/apply Op union [D:plan-apply] — the user-visible,
      // equatable+showable reification of an external mutation captured
      // inside a `plan` block. Each arm mirrors a mutation builtin's
      // effect: File.write -> WriteFile, File/Dir.delete* -> DeleteFile/
      // DeleteDir, File/Dir.copy -> Copy, File/Dir.move -> Move,
      // Dir.create -> MakeDir, Http.send{POST..} -> HttpSend. WriteFile
      // and HttpSend carry data that includes seqs / a Secret (auth);
      // Op/Plan are Eq-admitted by an explicit carve-out in Check (the
      // testing story), and Show masks the Secret via the recursive
      // renderer [D:secret].
      "type Op = WriteFile of string * seq<string> | DeleteFile of string | Copy of string * string | Move of string * string | MakeDir of string | DeleteDir of string | HttpSend of HttpRequest" ]

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
        // register every type name present at prelude-close as built-in
        // [D:desugar-capture]: a later user declaration of one is a
        // located error, not a silent retype of the builtins behind it
        Check.preludeLoading.Value <- false

        for name in te.Types |> Map.keys do
            Check.builtinTypeNames.TryAdd(name, 0uy) |> ignore

        // def-less builtins: type constructors with no Record/Union
        // entry — Map [D:map-string], the Proc handle [D:scoped-procs],
        // and the patch district's type [D:yaml-nodes]. Without this a
        // user `type Proc = …` would silently retype every scoped-process
        // binder behind it. The arity-0 pair rides Check's one list, so
        // registration and signature nameability cannot drift.
        for name in "Map" :: Check.deflessBuiltinNominals do
            Check.builtinTypeNames.TryAdd(name, 0uy) |> ignore

        te, ve
