module Weir.Purity

// The purity classifier — [D:pure]'s display-stage judgement, moved
// out of Can.fs so the ENFORCEMENT stage can reach it [D:pure-stage1]:
// Check.fs compiles before Builtins and cannot see the closed builtin
// surface, so the `pure` region's law lives in the checked-statement
// pipeline's post-check layer (Script.fs) and the classifier sits
// HERE, between Builtins and Script. Can.pureTopBindings (which needs
// Script's types) stays in Can.fs and delegates.
//
// A binding is pure when its body can reach NO effect — exposed as a
// boolean only (labels stay internal; `only`/`deterministic` tiers
// expose them later). CONSERVATIVE by construction: an unknown
// callable (a function-typed param, an applied field, an import's
// member) forfeits — the judgement may miss, it can never lie.

open Weir.Types
open Weir.Ast
open Weir.Check

// whole effectful MODULES (new members default impure — the safe drift
// direction), plus the effectful members of otherwise-pure modules and
// the bare effectful names. `fail` stays pure (control flow, not an
// external touch); `exit` does not (it takes the process down). The
// effectful-name classification (the sets and the predicate) MOVED to
// Effects.fs [D:pure-stage2] so the ambient/mutation partition can gate
// on the SAME judgement — a name outside it is pure and has no class.
let private effectfulName = Weir.Effects.effectfulName

// the CLOSED builtin surface: a dotted or bare name found here is pure
// unless classified above — exhaustive by construction, so a pure
// member (Str.trim) never falls into the unknown-callable bucket
let private builtinNames =
    lazy (Weir.Builtins.valueEnv |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

// a bare UPPERCASE name in expression position is a union CONSTRUCTOR:
// binders start lowercase (Check.casingError's law) and a module never
// survives check as a value — so a function type here is a payload
// arrow, not a callable that could reach an effect. Data by
// construction, pure [D:pure-stdin-ctors]
let private isCtorName (n: string) =
    not (n.Contains ".") && n.Length > 0 && System.Char.IsUpper n[0]

let rec tyHasFun (t: Ty) =
    match t with
    | TFun _ -> true
    | TSeq i -> tyHasFun i
    | TTuple ts -> ts |> List.exists tyHasFun
    | TNamed(_, args) -> args |> List.exists tyHasFun
    | _ -> false

// pattern-bound names SHADOW: a binder from a pattern is an unknown to
// this judgement (data passes, an unknown callable forfeits) — leaving
// the OUTER name visible through it could make the judgement lie, the
// one direction [D:pure] forbids
let rec private patBound (p: Pattern) : string list =
    match p.PKind with
    | PVar n -> [ n ]
    | _ -> patChildren p |> List.collect patBound

let private dropPat (p: Pattern) (env: Map<string, bool>) =
    patBound p |> List.fold (fun e n -> Map.remove n e) env

/// conservative purity of a typed expression; `env` carries the purity
/// of KNOWN user bindings (earlier top-level lets, local lets)
let rec isPureExpr (env: Map<string, bool>) (te: TypedExpr) : bool =
    let headOf (e: TypedExpr) =
        let rec go (e: TypedExpr) =
            match e.Kind with
            | TEApp(g, _) -> go g
            | _ -> e

        go e

    match te.Kind with
    | TECmd _ -> false
    | TEWithin(kind, _, _, _, body) ->
        // a pure REGION is itself pure exactly when its body is (the
        // region asserts, it does not touch); every resource kind is
        // an effect [D:pure-stage1]
        (match kind with
         | WithinPure -> isPureExpr env body
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinLock -> false)
    | TEEnvLoad _ -> false
    | TEArgsLoad _ -> false
    // retry/poll sleep between attempts — the clock is ambient input
    | TERetry _ -> false
    | TEVar n ->
        if effectfulName n then false
        elif builtinNames.Force().Contains n then true
        elif isCtorName n then true
        else
            match Map.tryFind n env with
            | Some p -> p
            | None ->
                // a data-typed unknown is inert; an unknown CALLABLE
                // could do anything — no badge
                not (tyHasFun te.Ty)
    | TELet(n, _, v, b) ->
        let pv = isPureExpr env v
        pv && isPureExpr (Map.add n pv env) b
    | TELetPat(p, v, b) -> isPureExpr env v && isPureExpr (dropPat p env) b
    | TELambda(p, _, b) -> isPureExpr (Map.remove p env) b
    | TELambdaPat(p, b) -> isPureExpr (dropPat p env) b
    | TEApp(_, _) ->
        // applying a COMPUTED function (a field's value, a match result)
        // is an unknown call; var heads resolve through the rules above,
        // inline lambdas through their bodies
        (match (headOf te).Kind with
         | TEVar _
         | TELambda _
         | TELambdaPat _ -> true
         | _ -> false)
        && (Check.childExprs te |> List.forall (isPureExpr env))
    | _ -> Check.childExprs te |> List.forall (isPureExpr env)

// the teaching's OBJECT: what a classified-effectful name does, in the
// internal labels' vocabulary [D:pure-stage1] — one phrase per family,
// offender + span only in v1 (the plan's full call-trace rendering is
// a recorded simplification)
// the fs.write ∪ fs.delete membership lives in Effects [D:pure-stage2] —
// the ONE set both the partition and this teaching vocabulary read
let private fsWriteMembers = Weir.Effects.fsWriteMembers

let private effectPhrase (n: string) : string =
    if n.StartsWith "|" then
        $"'{n}' runs a command"
    else
        match n with
        | "print"
        | "printerr" -> $"'{n}' writes to the console"
        | "exit" -> "'exit' takes the process down"
        | "ls"
        | "glob"
        | "Path.glob" -> $"'{n}' reads the filesystem"
        | "Path.tempRoot"
        | "Path.newTempDir" -> $"'{n}' touches the filesystem"
        | "Instant.now" -> "'Instant.now' reads the clock"
        | "Duration.sleep" -> "'Duration.sleep' waits on the clock"
        | "Self.stdin" -> "'Self.stdin' reads the process's input stream"
        | _ ->
            match n.Split '.' with
            | [| ("File" | "Dir"); m |] when fsWriteMembers.Contains m -> $"'{n}' writes the filesystem"
            | [| ("File" | "Dir"); _ |] -> $"'{n}' reads the filesystem"
            | [| ("Env" | "Args"); _ |] -> $"'{n}' reads the environment"
            | [| "Proc"; _ |] -> $"'{n}' touches a process"
            | [| ("Net" | "Http"); _ |] -> $"'{n}' talks to the network"
            | [| "Log"; _ |] -> $"'{n}' writes logs"
            | _ -> $"'{n}' is effectful"

/// the FIRST effectful node under a pure region, with its span — the
/// located teaching's payload [D:pure-stage1]. Mirrors isPureExpr's
/// judgement exactly (same env discipline, same conservatism); where
/// isPureExpr answers false, this names why and where.
let rec firstEffect (env: Map<string, bool>) (te: TypedExpr) : (Span * string) option =
    let headOf (e: TypedExpr) =
        let rec go (e: TypedExpr) =
            match e.Kind with
            | TEApp(g, _) -> go g
            | _ -> e

        go e

    match te.Kind with
    | TECmd(prog, _, _) -> Some(te.Span, $"'{prog}' runs a command")
    | TEWithin(kind, _, _, _, body) ->
        (match kind with
         | WithinPure -> firstEffect env body
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinLock -> Some(te.Span, $"'within {withinKindName kind}' scopes a resource"))
    | TEEnvLoad _ -> Some(te.Span, "'Env.load' reads the environment")
    | TEArgsLoad _ -> Some(te.Span, "'Args.load' reads the arguments")
    | TERetry _ -> Some(te.Span, "'retry'/'poll' waits on the clock")
    | TEVar n ->
        if effectfulName n then
            Some(te.Span, effectPhrase n)
        elif builtinNames.Force().Contains n || isCtorName n then
            None
        else
            match Map.tryFind n env with
            | Some true -> None
            | Some false -> Some(te.Span, $"'{n}' reaches an effect")
            | None ->
                if tyHasFun te.Ty then
                    Some(te.Span, $"'{n}' is an unknown callable, and an unknown callable forfeits purity")
                else
                    None
    | TELet(n, _, v, b) ->
        firstEffect env v
        |> Option.orElseWith (fun () -> firstEffect (Map.add n true env) b)
    | TELetPat(p, v, b) ->
        firstEffect env v
        |> Option.orElseWith (fun () -> firstEffect (dropPat p env) b)
    | TELambda(p, _, b) -> firstEffect (Map.remove p env) b
    | TELambdaPat(p, b) -> firstEffect (dropPat p env) b
    | TEApp(_, _) ->
        (match (headOf te).Kind with
         | TEVar _
         | TELambda _
         | TELambdaPat _ -> Check.childExprs te |> List.tryPick (firstEffect env)
         | _ ->
             Some((headOf te).Span, "a computed function is an unknown callable, and an unknown callable forfeits purity"))
    | _ -> Check.childExprs te |> List.tryPick (firstEffect env)

/// scan a checked statement's tree for `pure` regions and report the
/// first violation [D:pure-stage1] — the env tracks bindings on the way
/// down exactly as isPureExpr does, so a region deep in a statement
/// still sees the statement's own earlier lets
let rec pureViolation (env: Map<string, bool>) (te: TypedExpr) : (Span * string) option =
    match te.Kind with
    | TEWithin(kind, _, arg, opts, body) ->
        (match kind with
         | WithinPure -> firstEffect env body
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinLock ->
             [ arg; opts ]
             |> List.choose id
             |> List.tryPick (pureViolation env)
             |> Option.orElseWith (fun () -> pureViolation env body))
    | TELet(n, _, v, b) ->
        pureViolation env v
        |> Option.orElseWith (fun () -> pureViolation (Map.add n (isPureExpr env v) env) b)
    | TELetPat(p, v, b) ->
        pureViolation env v
        |> Option.orElseWith (fun () -> pureViolation (dropPat p env) b)
    | TELambda(p, _, b) -> pureViolation (Map.remove p env) b
    | TELambdaPat(p, b) -> pureViolation (dropPat p env) b
    | _ -> Check.childExprs te |> List.tryPick (pureViolation env)
