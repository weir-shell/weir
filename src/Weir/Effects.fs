module Weir.Effects

// The ambient/mutation partition [D:pure-stage2] — Stage 2 of PLAN-pure.
//
// Every builtin carries an effect label ({ fs.read, fs.write, net,
// proc, env, clock }); the labels live implicitly in Purity's
// classification sets and effectPhrase vocabulary (there is no
// materialized label table — the classifier is the table). This module
// refines that table into a two-way split by one principled line: does
// an effect read the world (ambient input — reproducible) or change it
// (external mutation)?
//
//   - Ambient (reads, changes nothing): fs.read, env, clock, the query
//     subset of net (GET/HEAD/OPTIONS/QUERY — idempotent by HTTP's own
//     semantics), Net.portOpen (a probe), and Self.stdin (ambient input).
//   - Mutation (changes the world): fs.write, fs.delete, proc (a command
//     or a Proc member — opaque read+write), console writes, log writes,
//     `exit`, and the mutating subset of net (POST/PUT/DELETE/PATCH).
//
// This line is load-bearing twice, which is why it earns its own tier:
// it is the `readonly` ceiling (a computation that only reads
// ambient input is reproducible) and it is exactly [PLAN-plan-apply]'s
// reads-run / mutations-capture rule. One classification, two consumers.
//
// It lives here — before Builtins (Eval) and before Purity (Check) — so
// both ends consult the same source of truth: the checker refuses a
// mutation in a `readonly` block, and the interpreter (plan/apply)
// classifies a builtin call at eval time. `net` cannot be split on the
// name alone: `Http.send` carries its method in the request value, so
// its class is per-call — resolved at the value (Builtins reads the
// runtime request's method case; the checker reads a literal request's
// method where derivable, else conservatively refuses via the unknown
// path).

type EffectClass =
    | Ambient
    | Mutation

// ---- the effectful-name classification (here, not in Purity, so the
// partition can gate on it) [D:pure-stage2] --------------------------------
// whole effectful modules (new members default impure — the safe drift
// direction), the effectful members of otherwise-pure modules, and the
// bare effectful names. `fail` stays pure (control flow); `exit` does
// not. This is the set the boolean purity judgement reads and the gate
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
          // Self.stdin is a per-run value injected by Script (not a
          // Builtins member), so the effect walk sees a plain variable
          // — classified here or nowhere. Reading it drains the live
          // one-shot fd: ambient input, an effect. Its siblings
          // (Self.args/pid/scriptPath/entryPath) are per-run constants
          // and stay pure-admissible [D:pure-stdin-ctors]
          "Self.stdin" ]

let effectfulBare = Set [ "ls"; "glob"; "print"; "printerr"; "exit"; "prompt" ]

// the library desugars [D:desugar-namespace]: `|`-prefixed keys that a
// rewrite (for, ranges, retry/poll) targets at a plain library member,
// not a spawn — the other `|`-family (the command reifiers |completed
// /|orFailed/|print/…) does target a command. Both wear the un-typeable
// `|` prefix, so `StartsWith "|"` alone conflates them; a classifier
// must consult this map and read the library key as its target member.
// The one copy lives here (before both Builtins' alias registration and
// the Purity/Can classifiers) so the sugar's meaning and its
// classification cannot drift — Builtins.internalAliases resolves each
// triple to its Value from the same list.
let libraryDesugars: (string * string * string) list =
    [ "|seqIter", "Seq", "iter"
      "|seqMap", "Seq", "map"
      "|seqFreeze", "Seq", "freeze"
      "|seqAppend", "Seq", "append"
      "|seqRange", "Seq", "range"
      "|seqItem", "Seq", "item"
      "|retryDefaults", "Retry", "defaults"
      "|pollDefaults", "Poll", "defaults" ]

// a `|`-desugar key → its qualified target member (Some "Seq.iter"),
// or None when the name is not a library desugar (a command reifier, or
// not a `|`-name at all). A classifier reads a library desugar as this
// target; a command reifier stays a spawn.
let private libraryDesugarTargets: Map<string, string> =
    libraryDesugars |> List.map (fun (k, m, f) -> k, $"{m}.{f}") |> Map.ofList

let libraryDesugarTarget (n: string) : string option = libraryDesugarTargets.TryFind n

/// the `|`-name family split [D:desugar-namespace]: a command reifier
/// targets a spawn (effectful); a library desugar targets a plain member
/// (classified as that member, never as a command).
let isCommandReifier (n: string) =
    n.StartsWith "|" && not (libraryDesugarTargets.ContainsKey n)

/// is a name classified-effectful? A command reifier targets a spawn
/// [D:exit-reifiers]; a library desugar reads as its target member, so a
/// pure library target (Seq.iter) is not effectful [D:desugar-namespace].
let rec effectfulName (n: string) =
    if n.Contains "." then
        effectfulQualified.Contains n
        || (match n.Split '.' with
            | [| m; _ |] -> effectfulModules.Contains m
            | _ -> false)
    else
        match libraryDesugarTarget n with
        | Some target ->
            // read the desugar as its target member (Seq.iter is pure);
            // the recursion terminates — a target is never a `|`-name
            effectfulName target
        | None -> effectfulBare.Contains n || isCommandReifier n

// the filesystem write members (File/Dir), the fs.write ∪ fs.delete
// label's membership — the one source both effectClass (the partition)
// and Purity.effectPhrase (the teaching vocabulary) read, so the two
// cannot drift.
let fsWriteMembers =
    Set [ "write"; "append"; "copy"; "create"; "delete"; "deleteAll"; "move" ]

// the mutating HTTP methods — the per-method net split's source of truth,
// shared by the check-time literal path and eval-time resolution. Method
// names are the union case names uppercased (Builtins.httpMethodName's
// shape): Get/Head/Options/Query → Ambient; Post/Put/Delete/Patch →
// Mutation.
let httpMutatingMethods = Set [ "POST"; "PUT"; "DELETE"; "PATCH" ]

/// class of an `Http.send` call from its method case name (Get/Post/…,
/// any casing) — the per-method net split. Idempotent methods are
/// ambient; the mutating verbs are mutation.
let httpMethodClass (methodCase: string) : EffectClass =
    if httpMutatingMethods.Contains(methodCase.ToUpperInvariant()) then
        Mutation
    else
        Ambient

/// class of an effectful name. `Http.send` is method-dependent and
/// returns None here (the caller resolves it from the request value —
/// eval-time in Builtins, or the literal-request path in the checker);
/// every other classified-effectful name has a fixed class. The dispatch
/// mirrors Purity.effectPhrase's exactly — same label table, one
/// refinement, so a name effectPhrase names is a name effectClass places.
let effectClass (n: string) : EffectClass option =
    if not (effectfulName n) then
        None // a pure name has no class — the partition is over effects only
    elif isCommandReifier n then
        Some Mutation // a COMMAND reifier targets a spawn (proc); a library
    // desugar never reaches here — effectfulName already read it as its
    // pure target [D:desugar-namespace]
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
        // creates a directory — a filesystem write, external mutation
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
