module Weir.Effects

// The ambient/mutation PARTITION [D:pure-stage2] — Stage 2 of PLAN-pure.
//
// Stage 0 tagged every builtin INTERNALLY with its effect label
// ({ fs.read, fs.write, net, proc, env, clock }); those labels live
// IMPLICITLY in Purity's classification sets and effectPhrase vocabulary
// (there is no materialized label table — the classifier IS the table).
// Stage 2 REFINES that table into a two-way split by ONE principled
// line: does an effect READ the world (ambient input — reproducible) or
// CHANGE it (external mutation)?
//
//   - Ambient (reads, changes nothing): fs.read, env, clock, the QUERY
//     subset of net (GET/HEAD/OPTIONS/QUERY — idempotent by HTTP's own
//     semantics), Net.portOpen (a probe), and Self.stdin (ambient input).
//   - Mutation (changes the world): fs.write, fs.delete, proc (a command
//     or a Proc member — opaque read+write), console writes, log writes,
//     `exit`, and the MUTATING subset of net (POST/PUT/DELETE/PATCH).
//
// This line is LOAD-BEARING TWICE, which is why it earns its own tier:
// it is the `readonly` ceiling (a computation that only reads
// ambient input is reproducible) AND it is exactly [PLAN-plan-apply]'s
// reads-RUN / mutations-CAPTURE rule. One classification, two consumers.
//
// It lives HERE — before Builtins (Eval) and before Purity (Check) — so
// BOTH ends consult the SAME source of truth: the checker refuses a
// mutation in a `readonly` block, and the interpreter (plan/apply)
// classifies a builtin call at EVAL time. `net` cannot be split on the
// NAME alone: `Http.send` carries its method in the request VALUE, so
// its class is per-CALL — resolved at the value (Builtins reads the
// runtime request's method case; the checker reads a literal request's
// method where derivable, else conservatively refuses via the unknown
// path).

type EffectClass =
    | Ambient
    | Mutation

// ---- the effectful-NAME classification (moved down from Purity so the
// partition can gate on it) [D:pure-stage2] --------------------------------
// whole effectful MODULES (new members default impure — the safe drift
// direction), the effectful members of otherwise-pure modules, and the
// bare effectful names. `fail` stays pure (control flow); `exit` does
// not. This is the SET the boolean purity judgement reads AND the gate
// effectClass reads (a name outside it is pure → no class).
let effectfulModules =
    Set [ "File"; "Dir"; "Env"; "Args"; "Proc"; "Net"; "Log" ]

let effectfulQualified =
    Set
        [ "Http.send"
          "Http.fetch"
          "Http.query"
          "Path.glob"
          "Path.tempRoot"
          "Path.newTempDir"
          "Instant.now"
          "Duration.sleep"
          // Self.stdin is a per-run VALUE injected by Script (not a
          // Builtins member), so the effect walk sees a plain variable
          // — classified here or nowhere. Reading it DRAINS the live
          // one-shot fd: ambient input, an effect. Its siblings
          // (Self.args/pid/scriptPath/entryPath) are per-run CONSTANTS
          // and stay pure-admissible [D:pure-stdin-ctors]
          "Self.stdin" ]

let effectfulBare = Set [ "ls"; "glob"; "print"; "printerr"; "exit"; "prompt" ]

/// is a name classified-effectful? (the |-prefixed reifier desugar
/// targets a spawn [D:exit-reifiers])
let effectfulName (n: string) =
    if n.Contains "." then
        effectfulQualified.Contains n
        || (match n.Split '.' with
            | [| m; _ |] -> effectfulModules.Contains m
            | _ -> false)
    else
        effectfulBare.Contains n || n.StartsWith "|"

// the filesystem WRITE members (File/Dir), the fs.write ∪ fs.delete
// label's membership — the ONE source both effectClass (the partition)
// and Purity.effectPhrase (the teaching vocabulary) read, so the two
// cannot drift.
let fsWriteMembers =
    Set [ "write"; "append"; "copy"; "create"; "delete"; "deleteAll"; "move" ]

// the mutating HTTP methods — the per-method net split's SOURCE OF TRUTH,
// shared by the check-time literal path and eval-time resolution. Method
// names are the union case names UPPERCASED (Builtins.httpMethodName's
// shape): Get/Head/Options/Query → Ambient; Post/Put/Delete/Patch →
// Mutation.
let httpMutatingMethods = Set [ "POST"; "PUT"; "DELETE"; "PATCH" ]

/// class of an `Http.send` call from its METHOD case name (Get/Post/…,
/// any casing) — the per-method net split. Idempotent methods are
/// ambient; the mutating verbs are mutation.
let httpMethodClass (methodCase: string) : EffectClass =
    if httpMutatingMethods.Contains(methodCase.ToUpperInvariant()) then
        Mutation
    else
        Ambient

/// class of an effectful NAME. `Http.send` is method-dependent and
/// returns None here (the caller resolves it from the request value —
/// eval-time in Builtins, or the literal-request path in the checker);
/// every other classified-effectful name has a FIXED class. The dispatch
/// MIRRORS Purity.effectPhrase's exactly — same label table, one
/// refinement, so a name effectPhrase names is a name effectClass places.
let effectClass (n: string) : EffectClass option =
    if not (effectfulName n) then
        None // a pure name has no class — the partition is over effects only
    elif n.StartsWith "|" then
        Some Mutation // the reifier desugar targets a spawn (proc)
    else
        match n with
        | "print"
        | "printerr" -> Some Mutation // a console write changes the world
        | "prompt" -> Some Mutation // reads stdin AND writes the prompt to stderr
        | "exit" -> Some Mutation // takes the process down
        | "ls"
        | "glob"
        | "Path.glob" -> Some Ambient // reads the filesystem
        // Path.tempRoot is a pure query (the tmp root path); Path.newTempDir
        // CREATES a directory — a filesystem write, external mutation
        | "Path.tempRoot" -> Some Ambient
        | "Path.newTempDir" -> Some Mutation
        | "Instant.now" -> Some Ambient // reads the clock (ambient input)
        | "Duration.sleep" -> Some Ambient // waits on the clock — no world change
        | "Self.stdin" -> Some Ambient // reads the process's input stream
        // Http.send: per-method, resolved at the request value (None here)
        | "Http.send" -> None
        | "Http.fetch"
        | "Http.query" -> Some Ambient // GET shorthand / the query method — idempotent
        | _ ->
            match n.Split '.' with
            | [| ("File" | "Dir"); m |] when fsWriteMembers.Contains m -> Some Mutation
            | [| ("File" | "Dir"); _ |] -> Some Ambient // reads the filesystem
            | [| ("Env" | "Args"); _ |] -> Some Ambient // reads the environment
            | [| "Proc"; _ |] -> Some Mutation // touches a process
            | [| "Net"; _ |] -> Some Ambient // Net.portOpen probes, changes nothing
            | [| "Http"; _ |] -> Some Ambient // constructors are pure; send handled above
            | [| "Log"; _ |] -> Some Mutation // writes logs to a stream
            | _ -> Some Mutation // an unclassified effect: the safe (refusing) direction
