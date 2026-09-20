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
// boolean only (labels stay internal; `only`/`readonly` tiers
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
         // pure/readonly ASSERT, they do not touch — the region is
         // pure iff its body is (an ambient read inside a readonly
         // block still makes it impure) [D:pure-stage1] [D:pure-stage2].
         // A plan region [D:plan-apply] is transparent to the OUTER law:
         // its ambient reads still RUN when the plan is built, so the
         // region is pure iff its body is.
         | WithinPure
         | WithinReadonly
         | WithinPlan -> isPureExpr env body
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinServe
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
    | TECmd(h, _, _) -> Some(te.Span, $"'{theadDisplay h}' runs a command")
    | TEWithin(kind, _, _, _, body) ->
        (match kind with
         // a nested pure/readonly region asserts, it does not touch —
         // the pure ceiling still walks its body (an ambient read inside a
         // readonly block is an effect pure forbids) [D:pure-stage2].
         // A plan region [D:plan-apply] is transparent to the outer pure
         // ceiling — its reads run when the plan builds.
         | WithinPure
         | WithinReadonly
         | WithinPlan -> firstEffect env body
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinServe
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

// the mutation-class phrase: effectPhrase names the effect; this
// appends its CLASS so the readonly teaching says both what the
// offender does AND that it is external mutation [D:pure-stage2]
let private mutationPhrase (n: string) : string = effectPhrase n

// resolve an `Http.send` request argument's METHOD case at CHECK time
// [D:pure-stage2] — the per-method net split's check-time half. A
// literal constructor (`Http.post u`) or an explicit `method = Post`
// update is determinable; anything else is None (the caller
// conservatively treats an unresolved send as mutation — over-refusing
// is licensed, admitting a mutation is not).
let rec private httpSendMethod (arg: TypedExpr) : string option =
    match arg.Kind with
    // pipes/apps: strip to the constructor head
    | TEApp(f, _) -> httpSendMethod f
    | TEPipe(a, f) ->
        // `u |> Http.get` shape: the fn carries the method
        match httpSendMethod f with
        | Some m -> Some m
        | None -> httpSendMethod a
    | TEVar n ->
        match n.Split '.' with
        | [| "Http"; ("get" | "head" | "options" | "query" | "post" | "put" | "delete" | "patch") as m |] ->
            Some(m.ToUpperInvariant())
        | _ -> None
    // { Http.post u with method = Get; … } — an explicit method update
    // WINS (last write); otherwise the source's constructor decides
    | TEUpdate(src, ups) ->
        match ups |> List.tryPick (fun (path, v) -> if path = [ "method" ] then Some v else None) with
        | Some { Kind = TEVar case } -> Some(case.ToUpperInvariant())
        | Some _ -> None // a computed method — not statically known
        | None -> httpSendMethod src
    | TERecord(_, fields) ->
        match fields |> List.tryPick (fun (f, v) -> if f = "method" then Some v else None) with
        | Some { Kind = TEVar case } -> Some(case.ToUpperInvariant())
        | _ -> None
    | _ -> None

// the EXTERNAL-MUTATION class of an Http.send call given its request
// argument [D:pure-stage2]: a determinable idempotent method is Ambient
// (allowed); anything else — a mutating verb OR an unresolved method —
// is Mutation (conservative)
let private httpSendIsMutation (arg: TypedExpr option) : bool =
    match arg |> Option.bind httpSendMethod with
    | Some m -> Weir.Effects.httpMethodClass m = Weir.Effects.Mutation
    | None -> true // unknown method — refuse (the safe direction)

/// the FIRST external-MUTATION node under a `readonly` region, with
/// its span [D:pure-stage2] — the ambient/mutation partition's ceiling
/// walk. Ambient effects (fs.read, env, clock, query-net, Self.stdin)
/// PASS; only external mutation (fs.write, fs.delete, proc, mutating-net,
/// console, an unknown callable) is reported. Same env discipline and
/// conservatism as firstEffect; reuses Effects.effectClass as the line.
let rec firstMutation (env: Map<string, bool>) (te: TypedExpr) : (Span * string) option =
    let headOf (e: TypedExpr) =
        let rec go (e: TypedExpr) =
            match e.Kind with
            | TEApp(g, _) -> go g
            | _ -> e

        go e

    // the reusable teaching: "'File.write' writes the filesystem"
    let mut (span: Span) (n: string) = Some(span, mutationPhrase n)

    match te.Kind with
    | TECmd(h, _, _) -> Some(te.Span, $"'{theadDisplay h}' runs a command") // proc: mutation
    | TEWithin(kind, _, arg, opts, body) ->
        (match kind with
         // a nested pure/readonly region asserts, does not touch —
         // walk its body for mutations (a pure body has none, trivially
         // read-only; the pin `pure ⊂ readonly`)
         | WithinPure
         | WithinReadonly ->
             [ arg; opts ] |> List.choose id |> List.tryPick (firstMutation env)
             |> Option.orElseWith (fun () -> firstMutation env body)
         // a plan region [D:plan-apply] performs NO external mutation: its
         // captured builtins do not run (they append Ops), only its
         // ambient reads execute — so a `plan` block is read-only. The
         // captured mutations are the Plan's DATA, not effects here;
         // Plan.apply (a later call) is where they'd run.
         | WithinPlan -> None
         // tmp/cd/env/proc/lock all scope a RESOURCE: tmp/proc mutate,
         // cd/env/lock change the process/child world — every within
         // resource is external mutation for the read-only ceiling
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinServe
         | WithinLock -> Some(te.Span, $"'within {withinKindName kind}' scopes a resource (external mutation)"))
    // Env.load/Args.load READ the environment/arguments — ambient input,
    // allowed; keep walking children (an argument could mutate)
    | TEEnvLoad _
    | TEArgsLoad _ -> Check.childExprs te |> List.tryPick (firstMutation env)
    // retry/poll waits on the clock — ambient; walk the body for mutation
    | TERetry _ -> Check.childExprs te |> List.tryPick (firstMutation env)
    | TEVar n ->
        if effectfulName n then
            match Weir.Effects.effectClass n with
            | Some Weir.Effects.Mutation -> mut te.Span n
            | Some Weir.Effects.Ambient -> None // ambient read — allowed
            | None ->
                // Http.send: method-dependent, resolved at the enclosing
                // application (handled in the TEApp arm); a bare send
                // reference cannot be placed — refuse conservatively
                Some(te.Span, $"'{n}' talks to the network (external mutation unless a query method)")
        elif builtinNames.Force().Contains n || isCtorName n then
            None
        else
            // a user binding: reading DATA is never a mutation — even a
            // value computed from an impure command holds data once bound
            // (the command ran OUTSIDE the block). Only a CALLABLE can
            // mutate when applied: a function-typed binding proven
            // mutation-free passes; one that is impure or unknown forfeits
            // (an application would run it — the conservatism line)
            if not (tyHasFun te.Ty) then
                None
            else
                // conservatism [D:pure-stage1]: PureBindings tracks PURITY,
                // not mutation-freeness, so a read-only function reads as
                // impure here — refusing it over-refuses (licensed; a
                // refusal never lies) rather than track a second env
                match Map.tryFind n env with
                | Some true -> None // proven pure ⊂ mutation-free
                | Some false -> Some(te.Span, $"'{n}' may reach an effect — a function not proven pure forfeits read-only")
                | None -> Some(te.Span, $"'{n}' is an unknown callable, and an unknown callable forfeits read-only")
    | TELet(n, _, v, b) ->
        firstMutation env v
        |> Option.orElseWith (fun () -> firstMutation (Map.add n true env) b)
    | TELetPat(p, v, b) ->
        firstMutation env v
        |> Option.orElseWith (fun () -> firstMutation (dropPat p env) b)
    | TELambda(p, _, b) -> firstMutation (Map.remove p env) b
    | TELambdaPat(p, b) -> firstMutation (dropPat p env) b
    // `req |> Http.send` — the piped send: the request is the pipe's ARG
    | TEPipe(reqArg, { Kind = TEVar "Http.send" }) ->
        if httpSendIsMutation (Some reqArg) then
            Some(te.Span, "'Http.send' talks to the network (external mutation — a mutating HTTP method)")
        else
            firstMutation env reqArg
    | TEApp(_, _) ->
        // the per-method net split's check-time site: an Http.send
        // application resolves its method from the request argument
        (match (headOf te).Kind with
         | TEVar "Http.send" ->
             let reqArg =
                 match te.Kind with
                 | TEApp(_, a) -> Some a
                 | _ -> None

             if httpSendIsMutation reqArg then
                 Some((headOf te).Span, "'Http.send' talks to the network (external mutation — a mutating HTTP method)")
             else
                 // ambient (query method): walk ONLY the argument for a
                 // nested mutation — NOT the `Http.send` head var (whose
                 // TEVar arm would re-report the network as mutation)
                 reqArg |> Option.bind (firstMutation env)
         | TEVar _
         | TELambda _
         | TELambdaPat _ -> Check.childExprs te |> List.tryPick (firstMutation env)
         | _ ->
             Some(
                 (headOf te).Span,
                 "a computed function is an unknown callable, and an unknown callable forfeits read-only"
             ))
    | _ -> Check.childExprs te |> List.tryPick (firstMutation env)

/// the FIRST plan-scope refusal under a `plan` region [D:plan-apply]:
/// unlike readonly, fs/http MUTATIONS are fine (they are captured
/// as Ops) — but `proc` HARD-REFUSES (a spawned binary reads AND writes
/// opaquely, uncapturable) and `apply` INSIDE a plan refuses (a mutation
/// cannot be coherently captured). A nested `plan` region COMPOSES: this
/// walk stops at it (its own pureViolation arm re-enters), so a nested
/// plan's proc is caught by the inner region, not double-reported here.
let rec firstPlanRefusal (te: TypedExpr) : (Span * string) option =
    match te.Kind with
    // a command IS proc — the uncapturable spawn
    | TECmd(h, _, _) ->
        Some(
            te.Span,
            $"'{theadDisplay h}' runs a command, and 'proc' is refused inside 'plan' — a spawned binary reads and writes opaquely, so its effects cannot be captured; plan covers weir-native mutation only (File/Dir/Http)"
        )
    // a nested plan composes: it handles its own refusals
    | TEWithin(WithinPlan, _, _, _, _) -> None
    | TEVar n ->
        match n.Split '.' with
        | [| "Proc"; m |] ->
            Some(
                te.Span,
                $"'Proc.{m}' touches a process, and 'proc' is refused inside 'plan' — a spawned binary's effects cannot be captured; plan covers weir-native mutation only (File/Dir/Http)"
            )
        | [| "Plan"; "apply" |] ->
            Some(
                te.Span,
                "'Plan.apply' is refused inside 'plan' — a mutation cannot be coherently captured; build the plan here, apply it OUTSIDE the block"
            )
        // the '|'-prefixed reifier desugar targets a spawn (proc)
        | _ when n.StartsWith "|" ->
            Some(
                te.Span,
                "a command reifier runs a command, and 'proc' is refused inside 'plan' — its effects cannot be captured; plan covers weir-native mutation only (File/Dir/Http)"
            )
        | _ -> None
    | _ -> Check.childExprs te |> List.tryPick firstPlanRefusal

/// scan a checked statement's tree for `pure` regions and report the
/// first violation [D:pure-stage1] — the env tracks bindings on the way
/// down exactly as isPureExpr does, so a region deep in a statement
/// still sees the statement's own earlier lets
let rec pureViolation (env: Map<string, bool>) (te: TypedExpr) : (Span * string) option =
    match te.Kind with
    | TEWithin(kind, _, arg, opts, body) ->
        (match kind with
         // pure: the located teaching names the offender's effect family
         // [D:pure-stage1] — the FULL message lives here now (was wrapped
         // in Script) so the readonly ceiling can carry its own
         | WithinPure ->
             firstEffect env body
             |> Option.map (fun (span, phrase) -> span, $"this 'pure' block forbids effects, but {phrase}")
         // the read-only ceiling [D:pure-stage2]: ambient reads pass,
         // external mutation refuses — the located teaching names the
         // offender AND its class
         | WithinReadonly ->
             (firstMutation env body
              |> Option.map (fun (span, phrase) ->
                  span, $"this 'readonly' block forbids external mutation, but {phrase} — reads are allowed"))
         // the plan scope [D:plan-apply]: fs/http mutations are CAPTURED
         // (fine), proc and nested apply REFUSE — then keep walking the
         // body for nested pure/readonly/plan regions
         | WithinPlan ->
             firstPlanRefusal body
             |> Option.orElseWith (fun () -> pureViolation env body)
         | WithinTmp
         | WithinCd
         | WithinEnv
         | WithinProc
         | WithinServe
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
